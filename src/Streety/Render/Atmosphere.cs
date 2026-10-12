using Streety.Geo;

using UnityEngine;

namespace Streety.Render;

/// <summary>
/// feeds the <c>fizzyhex/streety/*</c> shaders: height haze, sky colour, clouds, stars and night, all from the sun
/// and the <see cref="Climate"/> of wherever the player is.
/// </summary>
/// <remarks>
/// everything goes out as shader globals (see StreetyAtmosphere.hlsl in the sdk project), so the tile and sky
/// materials never need touching. colours are worked out in srgb and sent linear.
/// </remarks>
public sealed class Atmosphere : IDisposable
{
    private const float WeatherInterval = 5f;
    private const float RefreshInterval = 15f;

    // haze is integrated this far at most; past it everything is haze anyway
    private const float MaxRayMetres = 150000f;

    // cloud noise cells and how many before it repeats (must match the sky shader's noise period)
    private const float CloudCellMetres = 3000f;
    private const float CloudPeriodCells = 256f;
    private const float CloudSoftness = 0.22f;
    private const float CloudOpacity = 0.92f;

    private const double EarthRadius = 6371000.0;

    // skylight lighting the haze; sunlight scattered by it on top
    private static readonly Color DayAmbient = new(0.7f, 0.77f, 0.87f);
    private static readonly Color TwilightAmbient = new(0.42f, 0.4f, 0.5f);
    private static readonly Color NightAmbient = new(0.1f, 0.11f, 0.15f);

    // warm sodium glow from the city under the sky, reflected off haze and cloud
    private static readonly Color LightPollution = new(0.22f, 0.15f, 0.1f);
    private static readonly Color NightSky = new(0.04f, 0.05f, 0.08f);
    private static readonly Color AfterglowColour = new(0.75f, 0.42f, 0.22f);
    private static readonly Color TileNightGlow = new(0.24f, 0.22f, 0.2f);

    private const float HazeSunScale = 0.35f;
    private const float HazeForwardScatter = 0.6f;
    private const float HazeGlow = 0.5f;
    private const float SkyExposure = 1.3f;

    private static readonly int HazeId = Shader.PropertyToID("_StreetyHaze");
    private static readonly int HazeAmbientId = Shader.PropertyToID("_StreetyHazeAmbient");
    private static readonly int SunDirectionId = Shader.PropertyToID("_StreetySunDirection");
    private static readonly int SunColourId = Shader.PropertyToID("_StreetySunColour");
    private static readonly int SkyId = Shader.PropertyToID("_StreetySky");
    private static readonly int SkyTintId = Shader.PropertyToID("_StreetySkyTint");
    private static readonly int NightSkyId = Shader.PropertyToID("_StreetyNightSky");
    private static readonly int SunLightId = Shader.PropertyToID("_StreetySunLight");
    private static readonly int CloudsId = Shader.PropertyToID("_StreetyClouds");
    private static readonly int CloudShapeId = Shader.PropertyToID("_StreetyCloudShape");
    private static readonly int StarRotationId = Shader.PropertyToID("_StreetyStarRotation");
    private static readonly int TileGlowId = Shader.PropertyToID("_StreetyTileGlow");
    private static readonly int GroundLightId = Shader.PropertyToID("_StreetyGroundLight");

    // share of direct sunlight the ground bounces back up into shadowed faces
    private const float GroundBounce = 0.12f;

    private readonly Material _originalSkybox;
    private Material _sky;

    private Weather _weather;
    private bool _hasWeather;
    private float _nextWeather;
    private float _lastRevision;
    private Weather _revisionWeather;

    private bool _loggedRegion;

    private Vector2 _windOffset;
    private bool _windSeeded;

    public Atmosphere()
    {
        _originalSkybox = RenderSettings.skybox;
    }

    public Weather Weather => _weather;

    /// <summary>
    /// bumps whenever the sky changes enough to be worth re-rendering for reflections and fog.
    /// </summary>
    public int Revision { get; private set; }

    /// <summary>
    /// visibility at the camera's height, metres; what unity's own fog should match for props and rigs.
    /// </summary>
    public float LocalVisibility { get; private set; }

    /// <summary>
    /// draws the level's sky with streety's sky material from now on.
    /// </summary>
    public void UseSky(Material sky)
    {
        if (sky == null || _sky == sky)
            return;

        _sky = sky;
        RenderSettings.skybox = sky;
        Revision++;
    }

    public void Update(GeoOrigin origin, SunDriver sun, GroundTone ground, Vector3 camera)
    {
        var utc = sun.Utc;

        if (!_hasWeather || Time.unscaledTime >= _nextWeather)
            UpdateWeather(origin, utc, ground);

        sun.Overcast = _weather.CloudCover;

        var light = sun.Light;
        var toSun = light != null ? -light.transform.forward : Vector3.up;
        var elevation = sun.Elevation;
        var cover = _weather.CloudCover;

        var day = Smooth(-1f, 15f, elevation);
        var dusk = Smooth(-12f, -3f, elevation);
        var night = 1f - Smooth(-14f, -4f, elevation);
        var afterglow = Smooth(-9f, -1f, elevation) * (1f - Smooth(1f, 10f, elevation));

        var sunLight = light != null ? Linear(light.color) * light.intensity : Vector4.zero;
        sunLight += Linear(AfterglowColour) * (0.5f * afterglow * (1f - cover));

        var pollution = Linear(LightPollution) * (0.4f + cover);

        var ambient = Vector4.Lerp(Linear(NightAmbient) * 0.15f + pollution * 0.3f, Linear(TwilightAmbient) * 0.5f, dusk);
        ambient = Vector4.Lerp(ambient, Linear(DayAmbient), day);

        // overcast greys the haze out
        var grey = (ambient.x * 0.2126f + ambient.y * 0.7152f + ambient.z * 0.0722f) * 0.9f;
        ambient = Vector4.Lerp(ambient, new Vector4(grey, grey, grey, 0f), 0.6f * cover);
        ambient = Vector4.Scale(ambient, Linear(_weather.HazeTint));

        var hazeSun = Vector4.Scale(sunLight * HazeSunScale, Linear(_weather.HazeTint));

        // the level has no baked lighting, so shaded faces get their light from here: sky above, bounce below
        var groundLight = ambient * 0.25f + sunLight * (GroundBounce * Mathf.Max(0f, toSun.y));

        var visibility = Prefs.FogVisibilityKm.Value > 0f ? Prefs.FogVisibilityKm.Value * 1000f : _weather.Visibility;
        var seaLevel = (float)-origin.Height;
        var extinction = 3.912f / visibility;

        LocalVisibility = visibility * Mathf.Exp(Mathf.Max(0f, camera.y - seaLevel) / _weather.ScaleHeight);

        Shader.SetGlobalVector(HazeId, new Vector4(extinction, 1f / _weather.ScaleHeight, seaLevel, MaxRayMetres));
        Shader.SetGlobalVector(HazeAmbientId, new Vector4(ambient.x, ambient.y, ambient.z, HazeForwardScatter));
        Shader.SetGlobalVector(SunDirectionId, toSun);
        Shader.SetGlobalVector(SunColourId, new Vector4(hazeSun.x, hazeSun.y, hazeSun.z, HazeGlow));
        Shader.SetGlobalVector(SunLightId, sunLight);
        Shader.SetGlobalVector(GroundLightId, groundLight);

        var stars = (1f - Smooth(-14f, -6f, elevation)) * (1f - cover);
        var exposure = SkyExposure * Smooth(-12f, 0f, elevation) * (1f - 0.35f * cover);
        Shader.SetGlobalVector(SkyId, new Vector4(exposure, _weather.AtmosphereThickness, 0f, stars));
        Shader.SetGlobalVector(SkyTintId, Linear(_weather.SkyTint));
        Shader.SetGlobalVector(NightSkyId, (Linear(NightSky) * 0.6f + pollution * 0.1f) * night);
        Shader.SetGlobalVector(TileGlowId, Linear(TileNightGlow) * (0.5f * night));

        UpdateClouds(origin);
        Shader.SetGlobalMatrix(StarRotationId, StarRotation(origin, utc));
    }

    private void UpdateWeather(GeoOrigin origin, DateTime utc, GroundTone ground)
    {
        _weather = Climate.At(origin.Latitude, origin.Longitude, utc, ground);
        _nextWeather = Time.unscaledTime + WeatherInterval;

        var first = !_hasWeather;
        _hasWeather = true;

        // once on arrival from latitude alone, and again once the streamed ground has had its say
        if (ground.Confidence <= 0f)
            _loggedRegion = false;

        if (first || (!_loggedRegion && ground.Confidence >= 1f))
        {
            _loggedRegion = ground.Confidence >= 1f;
            StreetyMod.Log.Msg($"weather: visibility {_weather.Visibility / 1000f:F0}km, cloud {_weather.CloudCover:P0}, "
                               + $"humidity {_weather.Humidity:F2}, dust {_weather.Dust:F2}"
                               + (_loggedRegion ? $" (ground {Hex(ground.Mean)})" : ""));
        }

        var changed = Mathf.Abs(_weather.CloudCover - _revisionWeather.CloudCover) > 0.03f
                      || Mathf.Abs(_weather.Visibility / Mathf.Max(_revisionWeather.Visibility, 1f) - 1f) > 0.05f;

        if (first || changed || Time.unscaledTime - _lastRevision > RefreshInterval)
        {
            _revisionWeather = _weather;
            _lastRevision = Time.unscaledTime;
            Revision++;
        }
    }

    /// <summary>
    /// cloud noise is anchored to the earth, not the unity origin, so rebasing and warping don't move the sky.
    /// </summary>
    private void UpdateClouds(GeoOrigin origin)
    {
        var lat = origin.Latitude * Math.PI / 180.0;
        var lon = origin.Longitude * Math.PI / 180.0;
        var period = (double)CloudCellMetres * CloudPeriodCells;

        if (!_windSeeded)
        {
            // start where a day's wind would have carried them, so a fresh session doesn't always open on the same sky
            _windOffset = _weather.Wind * (float)(DateTime.UtcNow.TimeOfDay.TotalSeconds % period);
            _windSeeded = true;
        }

        // clouds drift against the wind in noise space so they move with it in the world
        _windOffset -= _weather.Wind * Time.deltaTime;
        _windOffset.x %= (float)period;
        _windOffset.y %= (float)period;

        var east = Wrap(EarthRadius * lon * Math.Cos(lat) + _windOffset.x, period);
        var north = Wrap(EarthRadius * lat + _windOffset.y, period);

        Shader.SetGlobalVector(CloudsId, new Vector4(_weather.CloudCover, CloudOpacity, (float)east, (float)north));
        Shader.SetGlobalVector(CloudShapeId, new Vector4(_weather.CloudHeight, CloudCellMetres, CloudSoftness, CloudPeriodCells));
    }

    /// <summary>
    /// world -> celestial sphere (x towards the vernal equinox, z the north celestial pole), for the star field.
    /// </summary>
    private static Matrix4x4 StarRotation(GeoOrigin origin, DateTime utc)
    {
        var lst = (float)(SolarPosition.LocalSiderealDegrees(origin.Longitude, utc) * Mathf.Deg2Rad);
        var lat = (float)(origin.Latitude * Mathf.Deg2Rad);

        float sinL = Mathf.Sin(lst), cosL = Mathf.Cos(lst), sinP = Mathf.Sin(lat), cosP = Mathf.Cos(lat);

        var m = Matrix4x4.identity;
        m.SetRow(0, new Vector4(-sinL, cosP * cosL, -sinP * cosL, 0f));
        m.SetRow(1, new Vector4(cosL, cosP * sinL, -sinP * sinL, 0f));
        m.SetRow(2, new Vector4(0f, sinP, cosP, 0f));
        return m;
    }

    // ColorUtility is stripped from the game
    private static string Hex(Color c) =>
        $"#{(int)(Mathf.Clamp01(c.r) * 255):X2}{(int)(Mathf.Clamp01(c.g) * 255):X2}{(int)(Mathf.Clamp01(c.b) * 255):X2}";

    private static Vector4 Linear(Color c)
    {
        var l = c.linear;
        return new Vector4(l.r, l.g, l.b, 0f);
    }

    private static double Wrap(double value, double period) => ((value % period) + period) % period;

    private static float Smooth(float from, float to, float value)
    {
        var t = Mathf.Clamp01((value - from) / (to - from));
        return t * t * (3f - 2f * t);
    }

    public void Dispose()
    {
        if (_sky != null)
            RenderSettings.skybox = _originalSkybox;

        // zeroed globals switch the haze off for anything else still using the shaders
        Shader.SetGlobalVector(HazeId, Vector4.zero);
        Shader.SetGlobalVector(CloudsId, Vector4.zero);
        Shader.SetGlobalVector(TileGlowId, Vector4.zero);

        _sky = null;
    }
}
