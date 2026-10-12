using BoneLib;

using Il2CppSLZ.Marrow;

using Streety.Audio;
using Streety.Geo;
using Streety.Render;
using Streety.Services;
using Streety.Tiles;

using UnityEngine;

namespace Streety;

/// <summary>
/// everything streety does while the streety level is loaded: streaming, warping, origin shifts, attribution.
/// </summary>
public sealed class StreetySession : IDisposable
{
    private enum WarpPhase
    {
        None,
        Geocoding,
        Streaming,
    }

    // players wait above the target on a platform while the ground streams in
    private const float WaitHeight = 3000f;
    private const float ProbeTop = 9000f;
    private const float SettleSeconds = 1f;
    private const float WarpTimeout = 45f;

    private readonly GameObject _root;
    private readonly TileClient _client;
    private readonly Geocoder _geocoder;
    private readonly Il2CppWorker _worker;
    private readonly TileStreamer _streamer;
    private readonly AttributionLabel _attribution;
    private readonly RigReflectionProbe _reflections;
    private readonly SunDriver _sun;
    private readonly SkyFog _sky;
    private readonly StreetyMusic _music;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Viewer> _viewers = new();

    private WarpPhase _warp;
    private string _warpName;
    private double _warpLat, _warpLon;
    private Task<GeocodeResult> _geocodeTask;
    private float _warpStarted;
    private GameObject _platform;
    private Vector3? _probeHit;
    private float _probeStableSince;
    private bool _pendingStart;
    private bool _arrived;
    private Camera _camera;

    /// <summary>
    /// lower-case status line for the menu; location names keep their case.
    /// </summary>
    public string Status { get; private set; } = "starting…";

    public string LocationName { get; private set; }

    public bool IsWarping => _warp != WarpPhase.None;

    public StreetySession()
    {
        _root = new GameObject("streety");

        var key = Prefs.ResolveMapsKey();

        if (string.IsNullOrEmpty(key))
        {
            Status = "no maps key; set maps_key in UserData/MelonPreferences.cfg";
            StreetyMod.Log.Error(Status);
            return;
        }

        Gltf.DracoNative.Initialize();

        var (template, layer) = TileMaterials.Resolve();
        _client = new TileClient(key);
        _geocoder = new Geocoder(key);
        _worker = new Il2CppWorker("streety collider baker");

        var renderer = new TileRenderer(_root.transform, template, layer);
        var origin = new GeoOrigin(Prefs.StartLatitude.Value, Prefs.StartLongitude.Value, 0);
        _streamer = new TileStreamer(_client, renderer, _worker, origin);

        _attribution = new AttributionLabel();
        _reflections = new RigReflectionProbe();
        _sun = new SunDriver();
        _sky = new SkyFog();
        _music = new StreetyMusic();

        // the rig may not exist yet during level load; the first warp starts once it does
        _pendingStart = true;
    }

    /// <summary>
    /// geocodes a place name and warps every local player there.
    /// </summary>
    public void WarpTo(string query)
    {
        if (_streamer == null)
            return;

        if (string.IsNullOrWhiteSpace(query))
        {
            Status = "type a location first";
            return;
        }

        _warp = WarpPhase.Geocoding;
        _geocodeTask = _geocoder.LookupAsync(query.Trim(), _lifetime.Token);
        Status = "looking up location…";
    }

    private void BeginWarp(double latitude, double longitude, string name)
    {
        _warpLat = latitude;
        _warpLon = longitude;
        _warpName = name;
        _warp = WarpPhase.Streaming;
        _warpStarted = Time.time;
        _probeHit = null;
        _arrived = false;

        // a new place: nothing loaded is useful any more
        _streamer.UnloadAll();
        _streamer.SetOrigin(new GeoOrigin(latitude, longitude, 0));

        CreatePlatform(new Vector3(0, WaitHeight, 0));
        Player.RigManager?.Teleport(new Vector3(0, WaitHeight + 0.1f, 0), true);

        Status = $"loading tiles for {name}…";
        StreetyMod.Log.Msg($"warping to {name} {_streamer.Origin}");
    }

    public void Update()
    {
        if (_streamer == null)
            return;

        _worker.Pump();

        if (_pendingStart)
        {
            if (Player.RigManager == null)
                return;

            _pendingStart = false;
            BeginWarp(Prefs.StartLatitude.Value, Prefs.StartLongitude.Value, Prefs.StartName.Value);
        }

        var camera = FindCamera();

        if (camera != null && camera.farClipPlane < Prefs.FarClip.Value)
            camera.farClipPlane = Prefs.FarClip.Value;

        UpdateGeocode();
        CollectViewers();

        _streamer.Update(_viewers, camera);

        if (_warp == WarpPhase.Streaming)
            UpdateWarp();
        else if (_warp == WarpPhase.None)
            UpdateRebase();

        if (Time.frameCount % 30 == 0)
            _attribution.SetText(_streamer.BuildAttribution());

        _attribution.Update();
        _reflections.Update();

        // sun first: the sky re-renders from wherever it ended up
        _sun.Update(_streamer.Origin);
        _sky.Update();

        // music plays once an area's loaded, not while waiting on the platform
        _music.Update(_arrived);

        if (_warp == WarpPhase.None && _streamer.LastError != null && _streamer.SelectedCount == 0)
            Status = _streamer.LastError.ToLowerInvariant();
    }

    private Camera FindCamera()
    {
        if (_camera != null && _camera.isActiveAndEnabled)
            return _camera;

        _camera = Camera.main;

        if (_camera == null)
            _camera = Player.Head != null ? Player.Head.GetComponentInChildren<Camera>() : null;

        if (_camera == null)
            _camera = UnityEngine.Object.FindObjectOfType<Camera>();

        return _camera;
    }

    private void UpdateGeocode()
    {
        if (_warp != WarpPhase.Geocoding || _geocodeTask is not { IsCompleted: true })
            return;

        var task = _geocodeTask;
        _geocodeTask = null;

        if (task.IsFaulted)
        {
            _warp = WarpPhase.None;
            Status = task.Exception?.InnerException is GeocodeException g
                ? g.Message
                : "couldn't look that up";

            StreetyMod.Log.Warning($"geocode failed: {task.Exception?.InnerException?.Message}");
            return;
        }

        if (task.IsCanceled)
        {
            _warp = WarpPhase.None;
            return;
        }

        var result = task.Result;
        BeginWarp(result.Latitude, result.Longitude, result.Name);
    }

    private void CollectViewers()
    {
        _viewers.Clear();

        var radius = Prefs.ColliderRadius.Value;
        var origin = _streamer.Origin;

        // every live rig, local or remote (fusion), streams the world around it
        foreach (var rigs in RigManager.Cache._cache.Values)
        foreach (var rig in rigs)
        {
            var head = rig?.physicsRig?.m_head;

            if (head == null)
                continue;

            _viewers.Add(new Viewer
            {
                Ecef = origin.UnityToEcef(head.position),
                ColliderRadius = radius,
            });
        }

        if (_warp == WarpPhase.Streaming)
        {
            // stream the ground under the target in full detail, wherever the player is waiting
            var probeHeight = _probeHit?.y + 1.7f ?? 0f;

            _viewers.Add(new Viewer
            {
                Ecef = origin.UnityToEcef(new Vector3(0, probeHeight, 0)),
                ColliderRadius = radius,
                ProbeColumn = true,
            });
        }
    }

    private void UpdateWarp()
    {
        var hit = RaycastTiles(new Vector3(0, ProbeTop, 0), ProbeTop * 2);

        if (hit.HasValue)
        {
            if (!_probeHit.HasValue || (hit.Value - _probeHit.Value).sqrMagnitude > 0.25f)
                _probeStableSince = Time.time;

            _probeHit = hit;
        }

        var elapsed = Time.time - _warpStarted;
        var idle = _streamer.InFlightCount == 0 && _streamer.UploadQueueCount == 0;
        var settled = _probeHit.HasValue && idle && Time.time - _probeStableSince > SettleSeconds;

        if (settled || (_probeHit.HasValue && elapsed > WarpTimeout))
        {
            FinishWarp(_probeHit.Value);
            return;
        }

        if (elapsed > WarpTimeout * 2)
        {
            _warp = WarpPhase.None;
            Status = $"couldn't find the ground at {_warpName}";
            StreetyMod.Log.Warning(Status);
            return;
        }

        Status = _probeHit.HasValue
            ? $"refining {_warpName}… ({_streamer.InFlightCount} loading)"
            : $"loading tiles for {_warpName}… ({_streamer.InFlightCount} loading)";
    }

    private void FinishWarp(Vector3 ground)
    {
        // recentre vertically so the ground sits near y = 0, where floats are most precise
        var groundEcef = _streamer.Origin.UnityToEcef(ground);
        var (lat, lon, height) = Wgs84.EcefToGeodetic(groundEcef);
        _streamer.SetOrigin(new GeoOrigin(lat, lon, height));

        Player.RigManager?.Teleport(new Vector3(0, 0.05f, 0), true);
        DestroyPlatform();

        _warp = WarpPhase.None;
        _arrived = true;
        LocationName = _warpName;
        Status = $"at {_warpName}";
        StreetyMod.Log.Msg($"arrived at {_warpName} ({lat:F6}, {lon:F6}, {height:F1}m)");
    }

    /// <summary>
    /// keeps the local player near the unity origin so float precision holds up while exploring.
    /// </summary>
    private void UpdateRebase()
    {
        var rig = Player.RigManager;
        var feet = rig?.physicsRig?.feet;

        if (feet == null)
            return;

        var position = feet.transform.position;
        var limit = Prefs.RebaseDistance.Value;

        if (position.x * position.x + position.z * position.z < limit * limit)
            return;

        var old = _streamer.Origin;
        var feetEcef = old.UnityToEcef(position);
        var (lat, lon, _) = Wgs84.EcefToGeodetic(feetEcef);
        var origin = new GeoOrigin(lat, lon, old.Height);

        _streamer.SetOrigin(origin);
        rig.Teleport(origin.EcefToUnity(feetEcef), false);

        StreetyMod.Log.Msg($"rebased origin to {origin}");
    }

    private Vector3? RaycastTiles(Vector3 from, float distance)
    {
        var hits = Physics.RaycastAll(from, Vector3.down, distance, ~0, QueryTriggerInteraction.Ignore);
        Vector3? best = null;

        foreach (var hit in hits)
        {
            var collider = hit.collider;

            if (collider == null || !collider.transform.IsChildOf(_root.transform))
                continue;

            if (_platform != null && collider.gameObject == _platform)
                continue;

            if (!best.HasValue || hit.point.y > best.Value.y)
                best = hit.point;
        }

        return best;
    }

    private void CreatePlatform(Vector3 position)
    {
        DestroyPlatform();

        _platform = new GameObject("streety wait platform");
        _platform.transform.SetParent(_root.transform, false);
        _platform.transform.position = position - new Vector3(0, 0.25f, 0);

        var box = _platform.AddComponent<BoxCollider>();
        box.size = new Vector3(8f, 0.5f, 8f);
    }

    private void DestroyPlatform()
    {
        if (_platform != null)
            UnityEngine.Object.Destroy(_platform);

        _platform = null;
    }

    public void Dispose()
    {
        _lifetime.Cancel();

        _streamer?.Dispose();
        _worker?.Dispose();
        _client?.Dispose();
        _geocoder?.Dispose();
        _attribution?.Dispose();
        _reflections?.Dispose();
        _sky?.Dispose();
        _sun?.Dispose();
        _music?.Dispose();

        if (_root != null)
            UnityEngine.Object.Destroy(_root);

        _lifetime.Dispose();
    }
}
