using UnityEngine;

namespace Streety.Render;

/// <summary>
/// the air over a place at a moment: how far you can see, what tints the haze, and the cloud layer.
/// </summary>
public readonly struct Weather
{
    /// <summary>
    /// meteorological visibility at sea level, in metres.
    /// </summary>
    public float Visibility { get; init; }

    /// <summary>
    /// how quickly the haze thins with height (e-folding height, metres).
    /// </summary>
    public float ScaleHeight { get; init; }

    /// <summary>
    /// colour the haze scatters: bluish-white for wet air, ochre for dust.
    /// </summary>
    public Color HazeTint { get; init; }

    /// <summary>
    /// shifts the sky's scattering colour; 0.5 grey is neutral.
    /// </summary>
    public Color SkyTint { get; init; }

    public float AtmosphereThickness { get; init; }

    public float CloudCover { get; init; }
    public float CloudHeight { get; init; }

    /// <summary>
    /// wind carrying the clouds, metres per second towards east (x) and north (y).
    /// </summary>
    public Vector2 Wind { get; init; }

    /// <summary>
    /// 0 for dry air, 1 for saturated; morning mist and summer haze scale with it.
    /// </summary>
    public float Humidity { get; init; }

    public float Dust { get; init; }
}

/// <summary>
/// a made-up but plausible climate for anywhere on earth, with no weather service behind it.
/// </summary>
/// <remarks>
/// <para>
/// latitude picks a climate band (wet tropics, dry subtropics, temperate, polar) and the date its season. the ground
/// streety has streamed in refines that: green imagery means wet air, sand means dust, snow means crisp air.
/// </para>
/// <para>
/// day-to-day weather is smooth noise over place and time. every client works out the same sky for the same place
/// and moment, so fusion players see the same weather without syncing anything.
/// </para>
/// </remarks>
public static class Climate
{
    // weather systems this wide (degrees) and lasting about this long (hours)
    private const double WeatherDegrees = 4.0;
    private const double WeatherHours = 30.0;

    public static Weather At(double latitude, double longitude, DateTime utc, GroundTone ground)
    {
        var absLat = (float)Math.Abs(latitude);

        // +1 at midsummer, -1 at midwinter, whichever hemisphere
        var season = (float)Math.Cos(2 * Math.PI * (utc.DayOfYear - 172) / 365.25) * Mathf.Sign((float)latitude);

        var tropical = 1f - Smooth(10f, 22f, absLat);
        var subtropical = Smooth(14f, 22f, absLat) * (1f - Smooth(32f, 40f, absLat));
        var temperate = Smooth(32f, 42f, absLat) * (1f - Smooth(58f, 66f, absLat));
        var polar = Smooth(58f, 68f, absLat);
        var bands = tropical + subtropical + temperate + polar;

        var humidity = (0.85f * tropical + 0.3f * subtropical + 0.65f * temperate + 0.55f * polar) / bands;
        var dust = 0.25f * subtropical / bands;
        var snow = polar * Mathf.Max(0f, -season) * 0.5f;

        // the streamed imagery nudges the latitude guess: green means wet, sand means dust. grey cities and sea say
        // little either way, so they leave it alone
        if (ground.Confidence > 0f)
        {
            humidity = Mathf.Clamp01(humidity + ground.Confidence * (0.8f * ground.Greenness - 0.4f * ground.Aridity));
            dust = Mathf.Clamp01(dust + ground.Confidence * ground.Aridity * (0.2f + 0.4f * subtropical / bands));
            snow = Mathf.Max(snow, ground.Confidence * ground.Snow);
        }

        // summer muggy in the wet places
        humidity = Mathf.Clamp01(humidity + 0.12f * Mathf.Max(0f, season) * humidity);

        var hours = (utc - DateTime.UnixEpoch).TotalHours / WeatherHours;
        var cellLat = latitude / WeatherDegrees;
        var cellLon = longitude / WeatherDegrees;

        var cloudNoise = Noise(cellLat, cellLon, hours, 1);
        var hazeNoise = Noise(cellLat, cellLon, hours * 1.7, 2);
        var windNoise = Noise(cellLat, cellLon, hours * 0.6, 3);

        // the subtropical high keeps deserts clear
        var cloudiness = 0.2f + 0.45f * humidity - 0.35f * dust - 0.25f * subtropical / bands
                         + 0.25f * temperate * Mathf.Max(0f, -season) / bands
                         + 0.15f * polar / bands;
        // dry places stay dry; wet ones swing from clear to overcast
        var cover = Mathf.Clamp01(cloudiness + (cloudNoise - 0.5f) * Mathf.Lerp(0.35f, 1.2f, humidity) * (1f - dust));

        var visibility = Mathf.Lerp(60000f, 18000f, humidity);
        visibility *= Mathf.Lerp(1f, 0.3f, dust);
        visibility *= 1f + 0.5f * snow;
        visibility *= Mathf.Lerp(0.55f, 1.45f, hazeNoise);
        visibility *= 1f - 0.5f * MorningMist(longitude, utc) * humidity;

        var hazeTint = Color.Lerp(new Color(0.93f, 0.97f, 1f), new Color(1f, 0.8f, 0.58f), dust);
        hazeTint = Color.Lerp(hazeTint, new Color(0.88f, 0.94f, 1f), snow);

        // dust bleaches and warms the sky; dry polar air deepens it
        var skyTint = Color.Lerp(new Color(0.5f, 0.5f, 0.5f), new Color(0.6f, 0.53f, 0.44f), dust);
        skyTint = Color.Lerp(skyTint, new Color(0.45f, 0.5f, 0.57f), polar * (1f - humidity) / bands);

        // westerlies at mid latitudes, trade winds from the east nearer the equator
        var prevailing = (temperate + polar - tropical - 0.5f * subtropical) / bands;
        var heading = (windNoise - 0.5f) * Mathf.PI * 1.2f;
        var speed = Mathf.Lerp(4f, 14f, Noise(cellLat, cellLon, hours, 4));
        var wind = new Vector2(Mathf.Cos(heading) * Mathf.Sign(prevailing + 0.01f), Mathf.Sin(heading)) * speed;

        return new Weather
        {
            Visibility = Mathf.Clamp(visibility, 2000f, 120000f),
            ScaleHeight = Mathf.Lerp(Mathf.Lerp(1200f, 1800f, humidity), 2600f, dust) * (1f - 0.3f * Mathf.Max(0f, -season)),
            HazeTint = hazeTint,
            SkyTint = skyTint,
            AtmosphereThickness = 1f + 0.35f * dust + 0.15f * humidity,
            CloudCover = cover,
            CloudHeight = Mathf.Lerp(2400f, 1100f, humidity) + 1200f * dust,
            Wind = wind,
            Humidity = humidity,
            Dust = dust,
        };
    }

    /// <summary>
    /// 1 around an hour after local sunrise, fading by mid-morning; scaled by humidity it thickens the haze.
    /// </summary>
    private static float MorningMist(double longitude, DateTime utc)
    {
        var solarHour = (float)((utc.TimeOfDay.TotalHours + longitude / 15.0) % 24.0 + 24.0) % 24f;
        return Smooth(4f, 6.5f, solarHour) * (1f - Smooth(7.5f, 10.5f, solarHour));
    }

    private static float Smooth(float from, float to, float value)
    {
        var t = Mathf.Clamp01((value - from) / (to - from));
        return t * t * (3f - 2f * t);
    }

    // smooth 3d value noise in [0, 1]; integer hashing keeps it identical on every machine
    private static float Noise(double x, double y, double z, int seed)
    {
        var ix = (long)Math.Floor(x);
        var iy = (long)Math.Floor(y);
        var iz = (long)Math.Floor(z);

        var fx = Fade(x - ix);
        var fy = Fade(y - iy);
        var fz = Fade(z - iz);

        double Corner(long dx, long dy, long dz) => Hash(ix + dx, iy + dy, iz + dz, seed);
        double Lerp(double a, double b, double t) => a + (b - a) * t;

        var x00 = Lerp(Corner(0, 0, 0), Corner(1, 0, 0), fx);
        var x10 = Lerp(Corner(0, 1, 0), Corner(1, 1, 0), fx);
        var x01 = Lerp(Corner(0, 0, 1), Corner(1, 0, 1), fx);
        var x11 = Lerp(Corner(0, 1, 1), Corner(1, 1, 1), fx);

        return (float)Lerp(Lerp(x00, x10, fy), Lerp(x01, x11, fy), fz);
    }

    private static double Fade(double t) => t * t * (3 - 2 * t);

    private static double Hash(long x, long y, long z, int seed)
    {
        unchecked
        {
            var h = (ulong)x * 0x9E3779B97F4A7C15UL ^ (ulong)y * 0xC2B2AE3D27D4EB4FUL
                    ^ (ulong)z * 0x165667B19E3779F9UL ^ (ulong)seed * 0x27D4EB2F165667C5UL;
            h ^= h >> 33;
            h *= 0xFF51AFD7ED558CCDUL;
            h ^= h >> 33;
            h *= 0xC4CEB9FE1A85EC53UL;
            h ^= h >> 33;
            return (h >> 11) * (1.0 / (1UL << 53));
        }
    }
}
