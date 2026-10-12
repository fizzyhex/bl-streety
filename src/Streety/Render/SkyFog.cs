using Il2Cpp;

using UnityEngine;
using UnityEngine.Rendering;

namespace Streety.Render;

/// <summary>
/// distance fog that takes its colour from the sky, so far-off city fades into the horizon behind it.
/// </summary>
/// <remarks>
/// <para>
/// slz's shaders blend fog towards <c>_SkyTexture</c>, a mipped cubemap of the sky ("mip sky fog"). their
/// SkyManager renders that cubemap once per game session and keeps it, so it'd show whatever sky existed first and
/// never follow the sun. we render our own and hand it over instead, re-rendering whenever the sun moves or changes
/// colour — whatever moved it.
/// </para>
/// <para>
/// density comes from a visibility distance (koschmieder: 2% contrast left at that distance), so it can later be
/// driven per region or from weather.
/// </para>
/// <para>
/// litmas only ships its exp2 fog variant (<c>skip_variants FOG_LINEAR FOG_EXP</c>), so that's the mode: it also
/// keeps the street around you clear while the distance hazes over.
/// </para>
/// </remarks>
public sealed class SkyFog : IDisposable
{
    private const int CubeResolution = 128;

    // re-render once the sun has moved this far (~a minute of real time) or its colour shifts
    private const float RefreshDegrees = 0.25f;
    private const float RefreshColour = 0.01f;

    private readonly bool _originalFog;
    private readonly FogMode _originalFogMode;
    private readonly float _originalFogDensity;
    private readonly Texture _originalSky;

    private RenderTexture _cube;
    private Camera _camera;
    private Vector3 _lastSunForward;
    private Color _lastSunColour;
    private bool _rendered;

    public SkyFog()
    {
        _originalFog = RenderSettings.fog;
        _originalFogMode = RenderSettings.fogMode;
        _originalFogDensity = RenderSettings.fogDensity;
        _originalSky = SkyManager.skytexture;
    }

    /// <summary>
    /// how far you can see, in metres; sets the fog density.
    /// </summary>
    public float Visibility { get; set; } = Prefs.FogVisibilityKm.Value * 1000f;

    public void Update()
    {
        RenderSettings.fog = Visibility > 0f;
        RenderSettings.fogMode = FogMode.ExponentialSquared;

        // exp(-(density * d)^2) = 0.02 at d = visibility
        RenderSettings.fogDensity = Visibility > 0f ? 1.978f / Visibility : 0f;

        var sun = RenderSettings.sun;
        var forward = sun != null ? sun.transform.forward : Vector3.down;
        var colour = sun != null ? sun.color * sun.intensity : Color.black;

        if (_rendered
            && Vector3.Angle(forward, _lastSunForward) < RefreshDegrees
            && MaxDifference(colour, _lastSunColour) < RefreshColour)
            return;

        _lastSunForward = forward;
        _lastSunColour = colour;
        _rendered = true;

        Render();
    }

    private void Render()
    {
        if (_cube == null)
            Create();

        _camera.RenderToCubemap(_cube, 63);

        // CheckSky runs every frame from the volumetrics override and re-applies this, so swap it rather than
        // just setting the global once
        SkyManager.skytexture = _cube;
        SkyManager.SetSkyTexture(_cube);

        // ambient lighting is sampled from the skybox too; without this nights stay lit like the level's bake
        DynamicGI.UpdateEnvironment();
    }

    private void Create()
    {
        _cube = new RenderTexture(CubeResolution, CubeResolution, 16, RenderTextureFormat.DefaultHDR)
        {
            name = "streety sky",
            dimension = TextureDimension.Cube,
            useMipMap = true,
            autoGenerateMips = true,
        };

        var cameraObject = new GameObject("streety sky camera");
        cameraObject.SetActive(false);

        _camera = cameraObject.AddComponent<Camera>();
        _camera.enabled = false;
        _camera.cullingMask = 0;
        _camera.clearFlags = CameraClearFlags.Skybox;
        _camera.backgroundColor = Color.black;
    }

    private static float MaxDifference(Color a, Color b) =>
        Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Max(Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b)));

    public void Dispose()
    {
        RenderSettings.fog = _originalFog;
        RenderSettings.fogMode = _originalFogMode;
        RenderSettings.fogDensity = _originalFogDensity;

        // hand back slz's own sky, or let it regenerate one
        SkyManager.skytexture = _originalSky;

        if (_originalSky != null)
            SkyManager.SetSkyTexture(_originalSky);

        if (_camera != null)
            UnityEngine.Object.Destroy(_camera.gameObject);

        if (_cube != null)
            UnityEngine.Object.Destroy(_cube);

        _camera = null;
        _cube = null;
    }
}
