using Streety.Geo;

using UnityEngine;

namespace Streety.Render;

/// <summary>
/// points the level's sun where the real sun is over the current location, and warms/dims it near the horizon.
/// </summary>
/// <remarks>
/// set <see cref="Manual"/> when something else (e.g. a grabbable sun) owns the direction; the colour still follows
/// whatever elevation the light ends up at, and <see cref="SkyFog"/> picks up the change either way.
/// </remarks>
public sealed class SunDriver : IDisposable
{
    private static readonly Color HorizonColour = new(1f, 0.52f, 0.28f);

    // fully white-ish above this elevation, fully horizon-coloured at 0
    private const float WarmBelowDegrees = 25f;

    // fade out as the disc drops under the horizon
    private const float FadeStartDegrees = 4f;
    private const float FadeEndDegrees = -4f;

    private Light _light;
    private Quaternion _originalRotation;
    private Color _originalColour;
    private float _originalIntensity;
    private LightShadows _originalShadows;

    /// <summary>
    /// the moment the sun is drawn for; null follows the real clock (plus <see cref="Prefs.SunHourOffset"/>).
    /// </summary>
    public DateTime? TimeOverride { get; set; }

    /// <summary>
    /// stop placing the sun from the clock; something else is moving the light.
    /// </summary>
    public bool Manual { get; set; }

    public DateTime Utc => TimeOverride ?? DateTime.UtcNow.AddHours(Prefs.SunHourOffset.Value);

    /// <summary>
    /// elevation of the light as it's currently pointed, in degrees.
    /// </summary>
    public float Elevation { get; private set; }

    /// <summary>
    /// 0-1 cloud cover between the sun and the ground; thick cloud dims it.
    /// </summary>
    public float Overcast { get; set; }

    /// <summary>
    /// the level's sun once found.
    /// </summary>
    public Light Light => _light;

    public void Update(GeoOrigin origin)
    {
        if (!Resolve())
            return;

        if (!Manual)
        {
            var toSun = SolarPosition.Direction(origin.Latitude, origin.Longitude, Utc);
            var forward = -new Vector3((float)toSun.X, (float)toSun.Y, (float)toSun.Z);
            _light.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
        }

        Elevation = Mathf.Asin(Mathf.Clamp(-_light.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg;

        var warmth = 1f - Mathf.Clamp01(Elevation / WarmBelowDegrees);
        var fade = Mathf.InverseLerp(FadeEndDegrees, FadeStartDegrees, Elevation);

        _light.color = Color.Lerp(_originalColour, HorizonColour, warmth * warmth);
        _light.intensity = _originalIntensity * fade * fade * (1f - 0.65f * Overcast * Overcast);

        // no point rendering shadows from a sun under the ground
        _light.shadows = fade > 0f ? _originalShadows : LightShadows.None;
    }

    private bool Resolve()
    {
        if (_light != null)
            return true;

        _light = RenderSettings.sun;

        if (_light == null)
        {
            foreach (var light in UnityEngine.Object.FindObjectsOfType<Light>())
            {
                if (light.type == LightType.Directional)
                {
                    _light = light;
                    break;
                }
            }
        }

        if (_light == null)
            return false;

        _originalRotation = _light.transform.rotation;
        _originalColour = _light.color;
        _originalIntensity = _light.intensity;
        _originalShadows = _light.shadows;

        StreetyMod.Log.Msg($"sun: {_light.name}");
        return true;
    }

    /// <summary>
    /// puts the level's light back the way it was, for levels that outlive the session (dev_any_level).
    /// </summary>
    public void Dispose()
    {
        if (_light == null)
            return;

        _light.transform.rotation = _originalRotation;
        _light.color = _originalColour;
        _light.intensity = _originalIntensity;
        _light.shadows = _originalShadows;
        _light = null;
    }
}
