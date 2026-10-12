using Streety.Geo;

using UnityEngine;
using UnityEngine.AI;

namespace Streety.Tiles;

/// <summary>
/// bakes a runtime navmesh around every rig from the tile colliders, so npcs can walk the streamed world.
/// one region per rig (merged when rigs are close), rebuilt asynchronously one at a time. main thread only.
/// </summary>
/// <remarks>
/// every client bakes its own: whoever owns an npc under fusion simulates it locally, so each needs the ground.
/// </remarks>
public sealed class TileNavMesh : IDisposable
{
    // tiles are photogrammetry, not level geometry; a coarser voxel keeps bakes quick and smooths the noise
    private const float VoxelSize = 0.2f;

    // a region needs rebaking once its rig has walked this fraction of the radius away from its centre
    private const float RecentreFraction = 0.35f;

    // collider changes are batched; streaming changes them constantly
    private const float MinRebakeSeconds = 2f;

    // a region nobody's near is dropped after this long
    private const float RegionTimeout = 30f;

    private const float Height = 200f;

    private sealed class Region
    {
        public Vector3 Centre;
        public readonly List<(int agentType, NavMeshData data, NavMeshDataInstance instance)> Layers = new();
        public int BakedRevision = -1;
        public GeoOrigin BakedOrigin;
        public bool Dirty = true;
        public float LastBaked = float.NegativeInfinity;
        public float LastUsed;
    }

    private readonly TileStreamer _streamer;
    private readonly List<Region> _regions = new();
    private readonly Il2CppSystem.Collections.Generic.List<NavMeshBuildSource> _sources = new();

    // dropped while a bake was still writing to them; destroyed once it finishes
    private readonly List<Region> _retired = new();

    private readonly List<AsyncOperation> _bakes = new();
    private Region _baking;
    private int _bakeIndex;

    public TileNavMesh(TileStreamer streamer)
    {
        _streamer = streamer;
    }

    private static float Radius => Prefs.NavMeshRadius.Value;

    /// <summary>
    /// <paramref name="rigs"/> are rig positions in unity space.
    /// </summary>
    public void Update(IReadOnlyList<Vector3> rigs)
    {
        if (_baking != null)
        {
            foreach (var op in _bakes)
            {
                if (!op.isDone)
                    return;
            }

            _bakes.Clear();
            _baking = null;

            foreach (var region in _retired)
                Clear(region);

            _retired.Clear();
        }

        AssignRegions(rigs);
        DropIdleRegions();

        var next = PickRegion();

        if (next != null)
            StartBake(next);
    }

    private void AssignRegions(IReadOnlyList<Vector3> rigs)
    {
        var recentre = Radius * RecentreFraction;

        foreach (var rig in rigs)
        {
            Region nearest = null;
            var best = float.MaxValue;

            foreach (var region in _regions)
            {
                var d = FlatDistance(region.Centre, rig);

                if (d < best)
                {
                    best = d;
                    nearest = region;
                }
            }

            if (nearest == null || best > Radius)
            {
                _regions.Add(new Region { Centre = rig, LastUsed = Time.time });
                continue;
            }

            nearest.LastUsed = Time.time;

            // never move a region mid-bake; it picks the new centre up on its next turn
            if (best > recentre && nearest != _baking)
            {
                nearest.Centre = rig;
                nearest.Dirty = true;
            }
        }
    }

    private void DropIdleRegions()
    {
        for (var i = _regions.Count - 1; i >= 0; i--)
        {
            var region = _regions[i];

            if (region == _baking || Time.time - region.LastUsed < RegionTimeout)
                continue;

            Clear(region);
            _regions.RemoveAt(i);
        }
    }

    private Region PickRegion()
    {
        if (_regions.Count == 0)
            return null;

        // round robin, so one busy region can't starve the others
        for (var i = 0; i < _regions.Count; i++)
        {
            var region = _regions[(_bakeIndex + i) % _regions.Count];

            // an origin shift moves every tile; what's baked no longer lines up
            var moved = !ReferenceEquals(region.BakedOrigin, _streamer.Origin);
            var changed = region.BakedRevision != _streamer.ColliderRevision
                          && Time.time - region.LastBaked >= MinRebakeSeconds;

            if (region.Dirty || moved || changed)
            {
                _bakeIndex = (_bakeIndex + i + 1) % _regions.Count;
                return region;
            }
        }

        return null;
    }

    private void StartBake(Region region)
    {
        var bounds = new Bounds(region.Centre, new Vector3(Radius * 2, Height, Radius * 2));

        CollectSources(bounds);

        region.Dirty = false;
        region.BakedRevision = _streamer.ColliderRevision;
        region.BakedOrigin = _streamer.Origin;
        region.LastBaked = Time.time;

        EnsureLayers(region);

        foreach (var (agentType, data, _) in region.Layers)
        {
            var settings = NavMesh.GetSettingsByID(agentType);
            settings.overrideVoxelSize = true;
            settings.voxelSize = VoxelSize;

            _bakes.Add(NavMeshBuilder.UpdateNavMeshDataAsync(data, settings, _sources, bounds));
        }

        _baking = region;
    }

    private void CollectSources(Bounds bounds)
    {
        _sources.Clear();

        foreach (var tile in _streamer.Loaded)
        {
            var loaded = tile.Loaded;

            if (loaded is not { HasCollider: true, Visible: true })
                continue;

            for (var i = 0; i < loaded.Parts.Count; i++)
            {
                var part = loaded.Parts[i];
                var collider = part != null ? part.GetComponent<MeshCollider>() : null;

                if (collider == null || collider.sharedMesh == null || !collider.bounds.Intersects(bounds))
                    continue;

                _sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.Mesh,
                    sourceObject = collider.sharedMesh,
                    transform = part.transform.localToWorldMatrix,
                    area = 0,
                });
            }
        }
    }

    private static void EnsureLayers(Region region)
    {
        if (region.Layers.Count > 0)
            return;

        // bake for every agent type the game defines; npcs pick theirs
        var count = NavMesh.GetSettingsCount();

        for (var i = 0; i < count; i++)
        {
            var id = NavMesh.GetSettingsByIndex(i).agentTypeID;
            var data = new NavMeshData(id) { name = $"streety navmesh ({id})" };
            region.Layers.Add((id, data, NavMesh.AddNavMeshData(data)));
        }
    }

    private static void Clear(Region region)
    {
        foreach (var (_, data, instance) in region.Layers)
        {
            if (instance.valid)
                instance.Remove();

            if (data != null)
                UnityEngine.Object.Destroy(data);
        }

        region.Layers.Clear();
    }

    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        var dx = a.x - b.x;
        var dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// drops every region (e.g. before a warp); the next update starts fresh.
    /// </summary>
    public void Reset()
    {
        foreach (var region in _regions)
        {
            if (region == _baking)
                _retired.Add(region);
            else
                Clear(region);
        }

        _regions.Clear();
    }

    public void Dispose()
    {
        Reset();

        // the game is going away with the session; nothing is left to finish the bake for
        foreach (var region in _retired)
            Clear(region);

        _retired.Clear();
        _bakes.Clear();
        _baking = null;
    }
}
