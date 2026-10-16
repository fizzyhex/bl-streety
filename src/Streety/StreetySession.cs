using BoneLib;

using Il2CppSLZ.Marrow;

using Streety.Audio;
using Streety.Geo;
using Streety.Movement;
using Streety.Multiplayer;
using Streety.Render;
using Streety.Services;
using Streety.Tiles;
using Streety.Water;

using UnityEngine;

namespace Streety;

/// <summary>
/// high-level state container for the loaded streety level.
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

    // how long a joining client waits to be told where the lobby is before it falls back to the default start
    private const float HostWaitSeconds = 20f;

    // how long into a warp a client waits for the host's ground before centring on its own probe. longer than
    // WarpTimeout, so a host that arrives via the timeout still gets there first.
    private const float HostGroundSeconds = WarpTimeout + 15f;

    private const float PerchDistance = 80f;
    private const float PerchHeight = 12f;
    private const float PerchFootprint = 12f;
    private const int PerchBearings = 8;

    // how long the local player has to be off the platform before it vanishes
    private const float LeaveSeconds = 0.25f;
    private const float ArriveGraceSeconds = 1f;

    private const string KeyRejectedStatus = "maps key rejected! check it has the map tiles api enabled.";
    private static readonly List<Viewer> NoViewers = new();

    private readonly GameObject _root;
    private readonly TileClient _client;
    private readonly Geocoder _geocoder;
    private readonly Il2CppWorker _worker;
    private readonly TileStreamer _streamer;
    private readonly TileRenderer _renderer;
    private readonly AttributionLabel _attribution;
    private readonly RigReflectionProbe _reflections;
    private readonly SunDriver _sun;
    private readonly RenderAssets _renderAssets;
    private readonly GroundTone _ground;
    private readonly Atmosphere _atmosphere;
    private readonly SkyFog _sky;
    private readonly StreetyMusic _music;
    private readonly NimbusBoost _nimbus;
    private readonly LocationPin _pin;
    private readonly WaterMap _water;
    private readonly WaterBodies _waterBodies;
    private readonly TileNavMesh _navMesh;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Viewer> _viewers = new();
    private readonly List<Vector3> _rigs = new();

    private WarpPhase _warp;
    private WarpTarget _target;
    private Task<GeocodeResult> _geocodeTask;
    private float _warpStarted;
    private GameObject _platform;
    private Vector3? _probeHit;
    private float _probeStableSince;
    private bool _pendingStart;
    private WarpTarget _startOverride;
    private float? _hostWaitSince;
    private bool _arrived;
    private float _arrivedAt;
    private Double3 _platformEcef;
    private bool _hasPlatform;
    private bool _perched;
    private bool _onPlatform;
    private float _offPlatformSince;
    private Camera _camera;

    public string Status { get; private set; } = "starting...";

    public string LocationName { get; private set; }

    public bool IsWarping => _warp != WarpPhase.None;

    /// <summary>
    /// Whether an area has been streamed in and the player has arrived there. False before the first arrival and
    /// again once a warp discards the previous area.
    /// </summary>
    public bool HasGround => _arrived;

    /// <summary>
    /// Whether the safety platform should be present: before ground is available and after arrival until the local
    /// player steps off the platform.
    /// </summary>
    public bool PlatformNeeded => !_arrived || _perched;

    /// <summary>
    /// The top surface of the safety platform in Unity space, or null until the first warp places it.
    /// </summary>
    public Vector3? PlatformSurface => _hasPlatform ? _streamer.Origin.EcefToUnity(_platformEcef) : null;

    /// <summary>
    /// The current warp's destination and, once arrived, its ground; null before the first warp. Sent to late joiners.
    /// </summary>
    public WarpTarget Target => _target;

    public NimbusBoost Nimbus => _nimbus;

    public SunDriver Sun => _sun;

    public StreetySession()
    {
        _root = new GameObject("streety");

        var key = Prefs.ResolveMapsKey();

        if (string.IsNullOrEmpty(key))
        {
            Status = "no maps key; paste mapskey:<key> into the game, or set maps_key in UserData/MelonPreferences.cfg";
            StreetyMod.Log.Error(Status);
            return;
        }

        Gltf.DracoNative.Initialize();

        var (template, layer) = TileMaterials.Resolve();
        _client = new TileClient(key);
        _geocoder = new Geocoder(key);
        _worker = new Il2CppWorker("streety collider baker");

        _ground = new GroundTone();
        _renderer = new TileRenderer(_root.transform, template, layer) { Ground = _ground };
        var origin = new GeoOrigin(Prefs.StartLatitude.Value, Prefs.StartLongitude.Value, 0);
        _streamer = new TileStreamer(_client, _renderer, _worker, origin);

        // Water requires FluidLab; without it, water remains solid and the OSM terrain is left unchanged.
        if (Prefs.Water.Value && FluidLabBridge.Available)
        {
            _water = new WaterMap();
            _waterBodies = new WaterBodies(layer);
            _streamer.ColliderFilter = _water;
        }
        else if (Prefs.Water.Value)
        {
            StreetyMod.Log.Msg("fluidlab isn't installed; water will not be swimmable");
        }

        if (Prefs.NavMesh.Value)
            _navMesh = new TileNavMesh(_streamer);

        _attribution = new AttributionLabel();
        _reflections = new RigReflectionProbe();
        _sun = new SunDriver();
        _renderAssets = new RenderAssets();
        _atmosphere = new Atmosphere();
        _sky = new SkyFog();
        _music = new StreetyMusic();
        _nimbus = new NimbusBoost();
        _pin = new LocationPin(_root.transform);

        // Defer warping until after rig/level load
        _pendingStart = true;

        // If sent before the session begun, pop the pending level request from the host
        _startOverride = FusionBridge.TakePending();
    }

    /// <summary>
    /// Geocodes a place name and warps warps to it, or sends a travel request to host.
    /// </summary>
    public void WarpTo(string query)
    {
        if (_streamer == null)
            return;

        if (FusionBridge.IsClient)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                Status = "type a location first";
                return;
            }

            Status = FusionBridge.RequestTravel(query.Trim())
                ? "asked the host to travel"
                : "couldn't reach the host";
            return;
        }

        WarpToLocal(query);
    }

    /// <summary>
    /// Geocodes place name and warps to it.
    /// </summary>
    public void WarpToLocal(string query)
    {
        if (_streamer == null)
            return;

        if (_streamer.KeyRejected)
        {
            Status = KeyRejectedStatus;
            return;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            Status = "type a location first";
            return;
        }

        _warp = WarpPhase.Geocoding;
        _geocodeTask = _geocoder.LookupAsync(query.Trim(), _lifetime.Token);
        Status = "looking up location…";
    }

    /// <summary>
    /// Follows the host to an already-resolved location without geocoding or announcing it back.
    /// </summary>
    public void WarpToResolved(double latitude, double longitude, string name)
    {
        if (_streamer == null)
            return;

        var target = new WarpTarget(latitude, longitude, name);

        // still waiting on the first warp: this is the start
        if (_pendingStart)
        {
            _startOverride = target;
            return;
        }

        BeginWarp(target, false);
    }

    /// <summary>
    /// Records the host's ground for a warp. Ignored when it belongs to a different warp than the one in progress or
    /// queued to start.
    /// </summary>
    public void ReceiveGround(double latitude, double longitude, Double3 ground)
    {
        var target = _pendingStart ? _startOverride : _target;

        if (target == null || !target.Matches(latitude, longitude))
            return;

        // Already landed on our own probe after giving up on the host. The origin the host sends alongside this
        // rebases us onto the shared one, so the ground itself isn't needed any more.
        if (!_pendingStart && _warp != WarpPhase.Streaming)
        {
            StreetyMod.Log.Warning("the host's ground arrived after we'd landed; following its origin instead");
            return;
        }

        target.Ground = ground;
    }

    /// <summary>
    /// Follows the host's origin for a warp. Before arrival it's kept for <see cref="FinishWarp"/>; after arrival the
    /// world is rebased onto it straight away. Ignored when it belongs to a different warp.
    /// </summary>
    public void ReceiveOrigin(double latitude, double longitude, GeoOrigin origin)
    {
        var target = _pendingStart ? _startOverride : _target;

        if (target == null || !target.Matches(latitude, longitude))
            return;

        target.Origin = origin;

        if (!_pendingStart && _arrived && _warp == WarpPhase.None)
            ApplyOrigin(origin);
    }

    private void BeginWarp(WarpTarget target, bool announce = true)
    {
        _target = target;
        _warp = WarpPhase.Streaming;
        _warpStarted = Time.time;
        _probeHit = null;
        _arrived = false;
        _perched = false;
        _pin.Hide(true);

        // reset state
        _streamer.UnloadAll();
        _navMesh?.Reset();
        _ground.Reset();
        _streamer.SetOrigin(new GeoOrigin(target.Latitude, target.Longitude, 0));

        CreatePlatform(new Vector3(0, WaitHeight, 0));
        PlacePlatform(new Vector3(0, WaitHeight, 0));
        if (Player.RigManager != null)
            RigTeleport.To(Player.RigManager, new Vector3(0, WaitHeight + 0.1f, 0));

        Status = $"loading tiles for {target.Name}…";
        StreetyMod.Log.Msg($"warping to {target.Name} {_streamer.Origin}");

        // existing players follow immediately while their surrounding tiles stream in.
        if (announce)
            FusionBridge.Announce(target.Latitude, target.Longitude, target.Name);
    }

    public void Update()
    {
        if (_streamer == null)
            return;

        _worker.Pump();

        if (_pendingStart)
        {
            // Do not move players to the wait platform until the tile set is available.
            if (!_streamer.HasRoot)
            {
                _streamer.Update(NoViewers, null);

                if (_streamer.KeyRejected)
                    Status = KeyRejectedStatus;
                else if (_streamer.LastError != null)
                    Status = _streamer.LastError.ToLowerInvariant();

                return;
            }

            // Wait for Streety's materials so tiles are not rendered with the fallback assets.
            if (Player.RigManager == null || !_renderAssets.Settled)
                return;

            // When joining another player's lobby, wait briefly for the host to provide the current location.
            if (_startOverride == null && FusionBridge.IsClient)
            {
                _hostWaitSince ??= Time.time;

                if (Time.time - _hostWaitSince < HostWaitSeconds)
                {
                    Status = "waiting for the host…";
                    return;
                }

                StreetyMod.Log.Warning("the host didn't say where to start; using the default");
            }

            _pendingStart = false;
            UseRenderAssets();

            if (_startOverride != null)
                BeginWarp(_startOverride, false);
            else
                BeginWarp(new WarpTarget(Prefs.StartLatitude.Value, Prefs.StartLongitude.Value, Prefs.StartName.Value));

            _startOverride = null;
        }

        var camera = FindCamera();

        if (camera != null && camera.farClipPlane < Prefs.FarClip.Value)
            camera.farClipPlane = Prefs.FarClip.Value;

        UpdateGeocode();
        CollectViewers();

        _water?.Update(_viewers);
        _streamer.Update(_viewers, camera);
        _waterBodies?.Update(_water, _streamer.Origin);

        // The nav mesh is not useful while the player is waiting on the platform.
        if (_warp == WarpPhase.None)
            _navMesh?.Update(_rigs);

        if (_warp == WarpPhase.Streaming)
            UpdateWarp();
        else if (_warp == WarpPhase.None)
            UpdateRebase();

        UpdatePerch();

        if (Time.frameCount % 30 == 0)
            _attribution.SetText(BuildAttribution());

        _attribution.Update();
        _reflections.Update();

        // Update the sun before the atmosphere so the sky cubemap reflects the latest lighting state.
        var eye = camera != null ? camera.transform.position : Vector3.zero;
        _sun.Update(_streamer.Origin);
        _atmosphere.Update(_streamer.Origin, _sun, _ground, eye);
        _sky.Visibility = _atmosphere.LocalVisibility;
        _sky.Update(_atmosphere.Revision, eye);
        _pin.Update(_streamer.Origin, eye);

        // Start music after the area loads, not while the player is waiting on the platform.
        _music.Update(_arrived);

        _nimbus.Update(_streamer.Origin);

        if (_warp == WarpPhase.None && _streamer.LastError != null && _streamer.SelectedCount == 0)
            Status = _streamer.LastError.ToLowerInvariant();
    }

    private string BuildAttribution()
    {
        var attribution = _streamer.BuildAttribution();

        if (_water is not { HasData: true })
            return attribution;

        const string osm = "© OpenStreetMap contributors";
        return string.IsNullOrEmpty(attribution) ? osm : $"{attribution}; {osm}";
    }

    private void UseRenderAssets()
    {
        if (_renderAssets.Tile != null)
            _renderer.Template = _renderAssets.Tile;

        _atmosphere.UseSky(_renderAssets.Sky);
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
        BeginWarp(new WarpTarget(result.Latitude, result.Longitude, result.Name));
    }

    private void CollectViewers()
    {
        _viewers.Clear();
        _rigs.Clear();

        var radius = Prefs.ColliderRadius.Value;
        var origin = _streamer.Origin;

        // Stream the world around every live local or remote Fusion rig.
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

            _rigs.Add(head.position);
        }

        if (_warp == WarpPhase.Streaming)
        {
            // Stream the ground below the target in full detail while the player waits.
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
            if (_target.Ground is { } hostGround)
            {
                FinishWarp(hostGround);
                return;
            }

            // Clients centre on the host's ground so Unity positions mean the same place on every peer.
            if (!FusionBridge.IsClient || elapsed > HostGroundSeconds)
            {
                if (FusionBridge.IsClient)
                    StreetyMod.Log.Warning("the host didn't share its ground; using our own");

                FinishWarp(_streamer.Origin.UnityToEcef(_probeHit.Value));
                return;
            }

            Status = $"waiting for the host to reach {_target.Name}...";
            return;
        }

        if (elapsed > WarpTimeout * 2)
        {
            _warp = WarpPhase.None;
            Status = $"couldn't find the ground at {_target.Name}";
            StreetyMod.Log.Warning(Status);
            return;
        }

        Status = _probeHit.HasValue
            ? $"downloading {_target.Name}... ({_streamer.InFlightCount} loading)"
            : $"loading tiles for {_target.Name}... ({_streamer.InFlightCount} loading)";
    }

    /// <summary>
    /// Centres the origin on <paramref name="groundEcef"/> and moves the local player to a perch beside it. In a
    /// lobby, the host's ground is shared so every peer ends up with the same origin.
    /// </summary>
    private void FinishWarp(Double3 groundEcef)
    {
        // Select the perch while the colliders are still in the positions used by the probe.
        var perchEcef = _streamer.Origin.UnityToEcef(FindPerch(_streamer.Origin.EcefToUnity(groundEcef)));

        // Recenter vertically so the ground remains near y = 0, where floating-point precision is highest. A client
        // that heard the host rebase before it arrived uses the host's origin instead.
        var (lat, lon, height) = Wgs84.EcefToGeodetic(groundEcef);
        var origin = FusionBridge.IsClient && _target.Origin != null
            ? _target.Origin
            : new GeoOrigin(lat, lon, height);

        _streamer.SetOrigin(origin);

        _target.Ground = groundEcef;
        _target.Origin = origin;
        FusionBridge.AnnounceGround(_target);
        FusionBridge.AnnounceOrigin(_target);

        var perch = _streamer.Origin.EcefToUnity(perchEcef);
        PlacePlatform(perch);

        if (Player.RigManager != null)
            RigTeleport.To(Player.RigManager, perch + new Vector3(0, 0.05f, 0));
        DestroyPlatform();

        _pin.Show(_target.Name, groundEcef);

        _warp = WarpPhase.None;
        _arrived = true;
        _arrivedAt = Time.time;
        _perched = true;
        LocationName = _target.Name;
        Status = $"currently at {_target.Name}";
        StreetyMod.Log.Msg($"arrived at {_target.Name} ({lat:F6}, {lon:F6}, {height:F1}m)");
    }

    /// <summary>
    /// Finds a position beside <paramref name="target"/> with the lowest surrounding obstruction, raised clear of
    /// the ground and roofs and above the level geo.
    /// </summary>
    private Vector3 FindPerch(Vector3 target)
    {
        // The target may be the top of a tower, so find the lowest ground directly below it.
        var floor = RaycastTiles(new Vector3(target.x, ProbeTop, target.z), ProbeTop * 2, true)?.y ?? target.y;

        var best = new Vector3(target.x, floor + PerchHeight, target.z);
        var bestTop = float.MaxValue;

        for (var i = 0; i < PerchBearings; i++)
        {
            var angle = i * Mathf.PI * 2f / PerchBearings;
            var centre = target + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * PerchDistance;
            // Use the candidate's own ground rather than the target's; the target may be on a tower.
            var top = float.MinValue;
            var found = false;

            // Check the footprint center and corners to avoid placing the player partly inside a building.
            for (var j = 0; j < 5; j++)
            {
                var offset = j == 0
                    ? Vector3.zero
                    : new Vector3(j % 2 == 0 ? 1 : -1, 0, j < 3 ? 1 : -1) * (PerchFootprint * 0.5f);

                var hit = RaycastTiles(new Vector3(centre.x + offset.x, ProbeTop, centre.z + offset.z), ProbeTop * 2);

                if (!hit.HasValue)
                    continue;

                found = true;
                top = Mathf.Max(top, hit.Value.y);
            }

            if (!found || top >= bestTop)
                continue;

            bestTop = top;
            best = new Vector3(centre.x, Mathf.Max(top, floor) + PerchHeight, centre.z);
        }

        return best;
    }

    /// <summary>
    /// Releases the safety platform and location pin after the local player has stepped away from the platform.
    /// </summary>
    private void UpdatePerch()
    {
        if (!_perched || _onPlatform)
            return;

        var since = Mathf.Max(_offPlatformSince, _arrivedAt + ArriveGraceSeconds - LeaveSeconds);

        if (Time.time - since < LeaveSeconds)
            return;

        _perched = false;
        _pin.Hide();
    }

    /// <summary>
    /// Records whether the local player is inside the safety platform's trigger zone.
    /// </summary>
    public void ReportOnPlatform(bool inside)
    {
        if (_onPlatform && !inside)
            _offPlatformSince = Time.time;

        _onPlatform = inside;
    }

    private void PlacePlatform(Vector3 surface)
    {
        _platformEcef = _streamer.Origin.UnityToEcef(surface);
        _hasPlatform = true;
    }

    /// <summary>
    /// Recenters the Unity origin when players move too far away to preserve floating-point precision. In a lobby
    /// only the host decides, using <see cref="Prefs.LobbyRebasing"/>, and clients follow via
    /// <see cref="ReceiveOrigin"/>.
    /// </summary>
    private void UpdateRebase()
    {
        if (!Prefs.Rebasing.Value || FusionBridge.IsClient)
            return;

        if (PickRebaseCentre() is not { } centre)
            return;

        var old = _streamer.Origin;
        var (lat, lon, _) = Wgs84.EcefToGeodetic(old.UnityToEcef(centre));
        var origin = new GeoOrigin(lat, lon, old.Height);

        ApplyOrigin(origin);

        if (_target != null)
        {
            _target.Origin = origin;
            FusionBridge.AnnounceOrigin(_target);
        }
    }

    /// <summary>
    /// Picks the Unity-space point to recentre on, or null when nothing has strayed past the rebase radius. Outside a
    /// lobby this is always the local rig.
    /// </summary>
    private Vector3? PickRebaseCentre()
    {
        var limit = Prefs.RebaseDistance.Value;
        var mode = FusionBridge.IsHost ? Prefs.ResolveRebaseMode() : RebaseMode.HostBased;

        switch (mode)
        {
            case RebaseMode.Averaged:
                if (_rigs.Count == 0)
                    return null;

                var sum = Vector3.zero;

                foreach (var rig in _rigs)
                    sum += rig;

                var mean = sum / _rigs.Count;
                return Beyond(mean, limit) ? mean : null;

            default:
                var feet = Player.RigManager?.physicsRig?.feet;

                if (feet == null)
                    return null;

                var position = feet.transform.position;
                return Beyond(position, limit) ? position : null;
        }
    }

    private static bool Beyond(Vector3 position, float limit) =>
        position.x * position.x + position.z * position.z >= limit * limit;

    /// <summary>
    /// Switches to <paramref name="origin"/> and moves the local rig so it stays at the same place on the globe.
    /// Remote rigs catch up with their owners' next position update.
    /// </summary>
    private void ApplyOrigin(GeoOrigin origin)
    {
        var rig = Player.RigManager;
        var feet = rig?.physicsRig?.feet;
        Double3? feetEcef = feet != null ? _streamer.Origin.UnityToEcef(feet.transform.position) : null;

        _streamer.SetOrigin(origin);

        if (feetEcef.HasValue)
            RigTeleport.To(rig, origin.EcefToUnity(feetEcef.Value), false);

        StreetyMod.Log.Msg($"rebased origin to {origin}");
    }

    /// <summary>
    /// Finds the highest ground directly below <paramref name="from"/>, or the lowest ground when
    /// <paramref name="lowest"/> is true.
    /// </summary>
    private Vector3? RaycastTiles(Vector3 from, float distance, bool lowest = false)
    {
        // Water surfaces are triggers and count as ground, allowing players to arrive on the water.
        var hits = Physics.RaycastAll(from, Vector3.down, distance, ~0, QueryTriggerInteraction.Collide);
        Vector3? best = null;

        foreach (var hit in hits)
        {
            var collider = hit.collider;

            if (collider == null)
                continue;

            var ground = collider.transform.IsChildOf(_root.transform) && !collider.isTrigger;

            if (!ground && _waterBodies?.IsSurface(collider) != true)
                continue;

            if (_platform != null && collider.gameObject == _platform)
                continue;

            if (!best.HasValue || (lowest ? hit.point.y < best.Value.y : hit.point.y > best.Value.y))
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

        _navMesh?.Dispose();
        _streamer?.Dispose();
        _worker?.Dispose();
        _client?.Dispose();
        _geocoder?.Dispose();
        _attribution?.Dispose();
        _reflections?.Dispose();
        _sky?.Dispose();
        _atmosphere?.Dispose();
        _sun?.Dispose();
        _music?.Dispose();
        _nimbus?.Dispose();
        _pin?.Dispose();
        _waterBodies?.Dispose();
        _water?.Dispose();

        if (_root != null)
            UnityEngine.Object.Destroy(_root);

        _lifetime.Dispose();
    }
}
