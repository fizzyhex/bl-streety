using System.Collections.Concurrent;
using System.Diagnostics;

using Streety.Geo;
using Streety.Gltf;
using Streety.Render;

using UnityEngine;

namespace Streety.Tiles;

/// <summary>
/// a point the world is streamed around: a rig's head, or a warp probe.
/// </summary>
public struct Viewer
{
    public Double3 Ecef;

    /// <summary>
    /// tiles within this distance get colliders.
    /// </summary>
    public double ColliderRadius;

    /// <summary>
    /// when set, any selected tile crossed by the vertical line through this viewer gets colliders (warp probes).
    /// </summary>
    public bool ProbeColumn;
}

/// <summary>
/// selects, loads, uploads and evicts tiles. everything here runs on the main thread except the load tasks.
/// </summary>
public sealed class TileStreamer : IDisposable
{
    // tiles at or below this error keep cpu-side mesh data so they can become colliders
    private const double ReadableMaxError = 64;

    // tiles at or below this error are eligible for colliders near viewers
    private const double ColliderMaxError = 16;

    private const int UnusedJsonFrames = 3600;

    // root.json retries back off from this, doubling up to the max
    private const float RootRetrySeconds = 2f;
    private const float RootRetryMaxSeconds = 60f;

    private readonly TileClient _client;
    private readonly TileRenderer _renderer;
    private readonly Il2CppWorker _worker;

    private readonly ConcurrentQueue<(Tile tile, object result, Exception error)> _completed = new();
    private readonly ConcurrentQueue<(LoadedTile loaded, uint[][] indices)> _filtered = new();
    private readonly List<Tile> _requests = new();
    private readonly HashSet<Tile> _inFlight = new();
    private readonly List<Tile> _loaded = new();
    private readonly List<Tile> _selected = new();
    private readonly List<Tile> _uploadQueue = new();
    private readonly Stopwatch _uploadWatch = new();

    private readonly CancellationTokenSource _lifetime = new();

    private Tile _root;
    private bool _rootLoading;
    private int _rootFailures;
    private float _rootRetryAt;
    private int _frame;
    private float _sseFactor = 1000f;

    public GeoOrigin Origin { get; private set; }

    public long LoadedBytes { get; private set; }
    public int SelectedCount => _selected.Count;
    public int InFlightCount => _inFlight.Count;
    public int UploadQueueCount => _uploadQueue.Count;
    public string LastError { get; private set; }

    public bool HasRoot => _root != null;

    /// <summary>
    /// google turned the key down when asked for root.json; retrying won't help until the key changes.
    /// </summary>
    public bool KeyRejected { get; private set; }

    public IReadOnlyList<Tile> Selected => _selected;

    public IReadOnlyList<Tile> Loaded => _loaded;

    /// <summary>
    /// changes whenever a tile collider is added, swapped or removed.
    /// </summary>
    public int ColliderRevision { get; private set; }

    /// <summary>
    /// optional; trims tile colliders (water surfaces). collider-eligible tiles keep their cpu geometry for it.
    /// </summary>
    public ITileColliderFilter ColliderFilter { get; set; }

    public TileStreamer(TileClient client, TileRenderer renderer, Il2CppWorker worker, GeoOrigin origin)
    {
        _client = client;
        _renderer = renderer;
        _worker = worker;
        Origin = origin;
    }

    /// <summary>
    /// moves the origin and re-poses every loaded tile; loaded content is kept.
    /// </summary>
    public void SetOrigin(GeoOrigin origin)
    {
        Origin = origin;

        foreach (var tile in _loaded)
        {
            if (tile.Loaded != null)
                TileRenderer.Reposition(tile.Loaded, origin);
        }
    }

    public void Update(IReadOnlyList<Viewer> viewers, Camera camera)
    {
        _frame++;

        DrainCompleted();
        DrainFiltered();

        if (_root == null)
        {
            EnsureRoot();
            return;
        }

        if (_client.SessionExpired && !_rootLoading)
            RefreshSession();

        if (camera != null)
        {
            var fov = camera.fieldOfView * Mathf.Deg2Rad;
            _sseFactor = camera.pixelHeight / (2f * Mathf.Tan(fov * 0.5f));
        }

        _selected.Clear();
        _requests.Clear();

        if (viewers.Count > 0)
            Select(_root, viewers);

        UpdateVisibility(viewers);
        CancelStale();
        StartRequests();
        Upload();
        Evict();
    }

    private void EnsureRoot()
    {
        if (_rootLoading || KeyRejected || Time.time < _rootRetryAt)
            return;

        _rootLoading = true;
        var token = _lifetime.Token;

        Task.Run(async () =>
        {
            try
            {
                var bytes = await _client.FetchRootAsync(token).ConfigureAwait(false);
                var root = TilesetParser.Parse(bytes, TileClient.RootUri, null);
                _completed.Enqueue((null, root, null));
            }
            catch (Exception e)
            {
                _completed.Enqueue((null, null, e));
            }
        }, token);
    }

    /// <summary>
    /// 400 is an invalid key, 401/403 a key without the map tiles api (or billing) behind it.
    /// </summary>
    private static bool IsKeyRejection(Exception error)
    {
        return error is System.Net.Http.HttpRequestException { StatusCode: { } status }
            && (int)status is 400 or 401 or 403;
    }

    private void RefreshSession()
    {
        _rootLoading = true;
        var token = _lifetime.Token;

        Task.Run(async () =>
        {
            try
            {
                await _client.FetchRootAsync(token).ConfigureAwait(false);
                StreetyMod.Log.Msg("tiles session refreshed");
            }
            catch (Exception e)
            {
                StreetyMod.Log.Warning($"session refresh failed: {e.Message}");
            }
            finally
            {
                _rootLoading = false;
            }
        }, token);
    }

    /// <summary>
    /// screen-space error traversal with replacement refinement. returns whether the subtree can draw without holes.
    /// </summary>
    private bool Select(Tile tile, IReadOnlyList<Viewer> viewers)
    {
        tile.LastVisitedFrame = _frame;

        var distance = double.MaxValue;

        foreach (var viewer in viewers)
            distance = Math.Min(distance, tile.Volume.DistanceTo(viewer.Ecef));

        tile.Priority = distance;

        if (tile.ContentKind == ContentKind.ExternalTileset)
        {
            if (tile.State != TileState.Ready)
            {
                Request(tile);
                return false;
            }
        }

        var sse = tile.GeometricError * _sseFactor / Math.Max(distance, 1e-3);
        // empty tiles have nothing to draw at their own level, so they always refine
        var refine = tile.Children.Count > 0
                     && (sse > Prefs.MaxScreenSpaceError.Value || !tile.HasRenderableContent);

        if (!refine)
        {
            if (!tile.HasRenderableContent)
                return true;

            if (tile.State == TileState.Ready)
            {
                _selected.Add(tile);
                return true;
            }

            Request(tile);
            return false;
        }

        var start = _selected.Count;
        var allReady = true;

        if (tile.RefineAdd && tile.HasRenderableContent)
        {
            if (tile.State == TileState.Ready)
                _selected.Add(tile);
            else
                Request(tile);
        }

        foreach (var child in tile.Children)
            allReady &= Select(child, viewers);

        if (allReady)
            return true;

        // children are incomplete: draw this tile in their place if we can, and keep loading them
        if (tile.HasRenderableContent && !tile.RefineAdd)
        {
            if (tile.State == TileState.Ready)
            {
                _selected.RemoveRange(start, _selected.Count - start);
                _selected.Add(tile);
                return true;
            }

            Request(tile);
        }

        return false;
    }

    private void Request(Tile tile)
    {
        if (tile.State is TileState.Unloaded || (tile.State is TileState.Failed && Time.time >= tile.RetryAt))
            _requests.Add(tile);
    }

    private void UpdateVisibility(IReadOnlyList<Viewer> viewers)
    {
        foreach (var tile in _selected)
            tile.LastSelectedFrame = _frame;

        foreach (var tile in _loaded)
        {
            var loaded = tile.Loaded;
            var visible = tile.LastSelectedFrame == _frame;

            if (loaded.Visible != visible)
            {
                loaded.Visible = visible;
                loaded.Root.SetActive(visible);
            }

            if (!visible)
                continue;

            if (!loaded.ColliderRequested)
            {
                if (loaded.Readable && WantsCollider(tile, viewers))
                    RequestCollider(loaded);
            }
            else if (ColliderFilter != null && loaded.Geometry != null && !loaded.ColliderBusy
                     && loaded.ColliderRevision != ColliderFilter.Revision)
            {
                // new water arrived since this collider was built
                RequestCollider(loaded);
            }
        }
    }

    private bool WantsCollider(Tile tile, IReadOnlyList<Viewer> viewers)
    {
        foreach (var viewer in viewers)
        {
            if (viewer.ProbeColumn)
            {
                var up = viewer.Ecef.Normalized;

                if (tile.Volume.IntersectsLine(viewer.Ecef, up))
                    return true;
            }

            if ((tile.GeometricError <= ColliderMaxError || tile.IsLeaf)
                && tile.Volume.DistanceTo(viewer.Ecef) <= viewer.ColliderRadius)
                return true;
        }

        return false;
    }

    private void RequestCollider(LoadedTile loaded)
    {
        loaded.ColliderRequested = true;

        var filter = ColliderFilter;
        var geometry = loaded.Geometry;

        if (filter == null || geometry == null)
        {
            BakeColliders(loaded, null);
            return;
        }

        loaded.ColliderBusy = true;
        loaded.ColliderRevision = filter.Revision;

        var models = loaded.PartMatrices.ToArray();

        Task.Run(() =>
        {
            uint[][] indices = null;

            try
            {
                indices = filter.Filter(geometry, models);
            }
            catch (Exception e)
            {
                StreetyMod.Log.Warning($"collider filter failed: {e.Message}");
            }

            _filtered.Enqueue((loaded, indices));
        });
    }

    private void DrainFiltered()
    {
        while (_filtered.TryDequeue(out var item))
        {
            var (loaded, indices) = item;

            if (loaded.Root == null || loaded.Geometry == null)
                continue;

            // already built and still unfiltered: nothing to change
            if (indices == null && loaded.HasCollider && loaded.ColliderMeshes.Count == 0)
            {
                loaded.ColliderBusy = false;
                continue;
            }

            Mesh[] meshes = null;

            if (indices != null)
            {
                meshes = new Mesh[loaded.Parts.Count];

                for (var i = 0; i < meshes.Length; i++)
                {
                    meshes[i] = indices[i] != null
                        ? _renderer.CreateColliderMesh(loaded.Geometry.Nodes[i], indices[i])
                        : loaded.Meshes[i];
                }
            }

            BakeColliders(loaded, meshes);
        }
    }

    private void BakeColliders(LoadedTile loaded, Mesh[] meshes)
    {
        var ids = (meshes ?? loaded.Meshes.ToArray()).Select(m => m.GetInstanceID()).ToArray();

        _worker.Enqueue(
            () =>
            {
                foreach (var id in ids)
                    Physics.BakeMesh(id, false);
            },
            () =>
            {
                TileRenderer.SetColliders(loaded, meshes);
                loaded.ColliderBusy = false;
                ColliderRevision++;
            });
    }

    private void CancelStale()
    {
        if (_inFlight.Count == 0)
            return;

        List<Tile> stale = null;

        foreach (var tile in _inFlight)
        {
            if (_frame - tile.LastVisitedFrame > 30)
                (stale ??= new List<Tile>()).Add(tile);
        }

        if (stale == null)
            return;

        foreach (var tile in stale)
        {
            tile.Cancellation?.Cancel();
            tile.Cancellation = null;
            tile.State = TileState.Unloaded;
            _inFlight.Remove(tile);
        }
    }

    private void StartRequests()
    {
        if (_requests.Count == 0)
            return;

        // structure first (it unlocks everything below it), then nearest content
        _requests.Sort((a, b) =>
        {
            var aJson = a.ContentKind == ContentKind.ExternalTileset;
            var bJson = b.ContentKind == ContentKind.ExternalTileset;

            if (aJson != bJson)
                return aJson ? -1 : 1;

            return a.Priority.CompareTo(b.Priority);
        });

        var max = Prefs.MaxConcurrentRequests.Value;

        foreach (var tile in _requests)
        {
            if (_inFlight.Count >= max)
                break;

            if (tile.State is not (TileState.Unloaded or TileState.Failed))
                continue;

            StartLoad(tile);
        }
    }

    private void StartLoad(Tile tile)
    {
        tile.State = TileState.Loading;
        tile.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _inFlight.Add(tile);

        var token = tile.Cancellation.Token;

        Task.Run(async () =>
        {
            try
            {
                var bytes = await _client.GetAsync(tile.ContentUri, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                object result = tile.ContentKind == ContentKind.ExternalTileset
                    ? TilesetParser.Parse(bytes, tile.ContentUri, tile)
                    : GlbReader.Read(bytes);

                _completed.Enqueue((tile, result, null));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // superseded; the main thread already reset this tile
            }
            catch (Exception e)
            {
                _completed.Enqueue((tile, null, e));
            }
        }, token);
    }

    private void DrainCompleted()
    {
        while (_completed.TryDequeue(out var item))
        {
            var (tile, result, error) = item;

            if (tile == null)
            {
                _rootLoading = false;

                if (error != null)
                {
                    if (IsKeyRejection(error))
                    {
                        KeyRejected = true;
                        LastError = "maps key rejected";
                        StreetyMod.Log.Error($"maps key rejected: {error.Message}");
                        continue;
                    }

                    _rootFailures++;
                    var backoff = Math.Min(RootRetryMaxSeconds, RootRetrySeconds * (1 << Math.Min(_rootFailures - 1, 8)));
                    _rootRetryAt = Time.time + backoff;
                    LastError = $"couldn't reach google tiles: {error.Message}";
                    StreetyMod.Log.Error($"{LastError}; retrying in {backoff:F0}s");
                    continue;
                }

                _rootFailures = 0;
                LastError = null;
                _root = (Tile)result;
                StreetyMod.Log.Msg($"tiles root loaded ({_root.Children.Count} children)");
                continue;
            }

            // a cancelled request can still complete; ignore results nobody is waiting for
            if (!_inFlight.Remove(tile))
                continue;

            tile.Cancellation?.Dispose();
            tile.Cancellation = null;

            if (error != null)
            {
                tile.FailCount++;
                tile.State = TileState.Failed;
                tile.RetryAt = Time.time + Math.Min(60f, 2f * tile.FailCount * tile.FailCount);
                LastError = error.Message;
                StreetyMod.Log.Warning($"tile load failed ({tile}): {error.Message}");
                continue;
            }

            if (result is Tile externalRoot)
            {
                tile.Children.Clear();
                tile.Children.Add(externalRoot);
                tile.State = TileState.Ready;
                continue;
            }

            tile.Pending = (TileContent)result;
            tile.State = TileState.Decoded;
            _uploadQueue.Add(tile);
        }
    }

    private void Upload()
    {
        if (_uploadQueue.Count == 0)
            return;

        _uploadQueue.Sort((a, b) => a.Priority.CompareTo(b.Priority));

        var budget = Prefs.UploadBudgetMs.Value;
        var uploaded = 0;
        _uploadWatch.Restart();

        while (_uploadQueue.Count > uploaded && (uploaded == 0 || _uploadWatch.Elapsed.TotalMilliseconds < budget))
        {
            var tile = _uploadQueue[uploaded++];

            // dropped out of view while waiting; don't spend main-thread time on it
            if (_frame - tile.LastVisitedFrame > 30)
            {
                tile.Pending = null;
                tile.State = TileState.Unloaded;
                continue;
            }

            try
            {
                // leaves are the finest data there is, so coarse-coverage areas still get ground
                var keepReadable = tile.GeometricError <= ReadableMaxError || tile.IsLeaf;
                tile.Loaded = _renderer.Create(tile, tile.Pending, Origin, keepReadable);
                tile.State = TileState.Ready;

                if (ColliderFilter != null && keepReadable && (tile.GeometricError <= ColliderMaxError || tile.IsLeaf))
                {
                    // the filter only needs the meshes; the images are already on the gpu
                    tile.Pending.Images.Clear();
                    tile.Loaded.Geometry = tile.Pending;
                    tile.Loaded.Bytes += tile.Pending.EstimatedBytes;
                }

                LoadedBytes += tile.Loaded.Bytes;
                _loaded.Add(tile);
            }
            catch (Exception e)
            {
                tile.State = TileState.Failed;
                tile.FailCount++;
                tile.RetryAt = Time.time + 30f;
                StreetyMod.Log.Error($"tile upload failed: {e}");
            }
            finally
            {
                tile.Pending = null;
            }
        }

        _uploadQueue.RemoveRange(0, uploaded);
    }

    private void Evict()
    {
        var budget = Prefs.MemoryBudgetMb.Value * 1024L * 1024L;

        if (LoadedBytes > budget)
        {
            // tiles outside this frame's traversal first, then least recently selected; never evict what's on screen.
            // ancestors of on-screen tiles are visited every frame but never selected, and they're the fallback when a
            // refinement isn't ready yet, so evicting them first drops whole regions to a much coarser lod
            _loaded.Sort((a, b) =>
            {
                var visited = a.LastVisitedFrame.CompareTo(b.LastVisitedFrame);

                return visited != 0 ? visited : a.LastSelectedFrame.CompareTo(b.LastSelectedFrame);
            });

            var removed = 0;

            while (removed < _loaded.Count && LoadedBytes > budget * 0.9)
            {
                var tile = _loaded[removed];

                if (tile.LastSelectedFrame == _frame)
                    break;

                Unload(tile);
                removed++;
            }

            _loaded.RemoveRange(0, removed);
        }

        if (_frame % 600 == 0 && _root != null)
            PruneTilesets(_root);
    }

    private void Unload(Tile tile)
    {
        if (tile.Loaded != null)
        {
            LoadedBytes -= tile.Loaded.Bytes;

            if (tile.Loaded.HasCollider)
                ColliderRevision++;

            TileRenderer.Destroy(tile.Loaded);
            tile.Loaded = null;
        }

        tile.State = TileState.Unloaded;
    }

    /// <summary>
    /// forgets external tilesets nobody has visited for a while, so long flights don't grow memory forever.
    /// </summary>
    private void PruneTilesets(Tile tile)
    {
        foreach (var child in tile.Children)
        {
            if (child.ContentKind == ContentKind.ExternalTileset
                && child.State == TileState.Ready
                && _frame - child.LastVisitedFrame > UnusedJsonFrames
                && !HasLiveContent(child))
            {
                child.Children.Clear();
                child.State = TileState.Unloaded;
                continue;
            }

            PruneTilesets(child);
        }
    }

    private bool HasLiveContent(Tile tile)
    {
        if (tile.Loaded != null || tile.State is TileState.Loading or TileState.Decoded)
            return true;

        foreach (var child in tile.Children)
        {
            if (HasLiveContent(child))
                return true;
        }

        return false;
    }

    /// <summary>
    /// copyright holders of everything on screen, most frequent first (google's attribution rules).
    /// </summary>
    public string BuildAttribution()
    {
        var counts = new Dictionary<string, int>();

        foreach (var tile in _selected)
        {
            var copyright = tile.Loaded?.Copyright;

            if (string.IsNullOrEmpty(copyright))
                continue;

            foreach (var part in copyright.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                counts[part] = counts.TryGetValue(part, out var c) ? c + 1 : 1;
        }

        return string.Join("; ", counts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key));
    }

    /// <summary>
    /// drops all loaded content (e.g. before a long-distance warp).
    /// </summary>
    public void UnloadAll()
    {
        foreach (var tile in _loaded)
            Unload(tile);

        _loaded.Clear();

        foreach (var tile in _inFlight)
        {
            tile.Cancellation?.Cancel();
            tile.State = TileState.Unloaded;
        }

        _inFlight.Clear();

        foreach (var tile in _uploadQueue)
        {
            tile.Pending = null;
            tile.State = TileState.Unloaded;
        }

        _uploadQueue.Clear();
        LoadedBytes = 0;
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        UnloadAll();
        _lifetime.Dispose();
    }
}
