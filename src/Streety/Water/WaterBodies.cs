using Streety.Geo;

using UnityEngine;

namespace Streety.Water;

/// <summary>
/// a trigger box per body of water, topped at its surface, with a fluidlab liquid volume when fluidlab is installed.
/// </summary>
/// <remarks>
/// one box per body rather than one per patch: fluidlab drops a body that crosses from one liquid collider into
/// another, so seams inside a lake would leave swimmers stranded. land inside a body's bounds sits above the box.
/// tiles have no riverbed, so a solid floor sits <see cref="Depth"/> below the surface.
/// </remarks>
public sealed class WaterBodies : IDisposable
{
    public const float Depth = 8f;

    private const float FloorThickness = 1f;
    private const float Density = 1000f;

    // surfaces move as more of a body is sampled; don't shuffle volumes for noise
    private const double MoveThreshold = 0.1;

    private sealed class Volume
    {
        public WaterRegion Region;
        public GameObject Object;
        public double Level;
        public double Latitude, Longitude;
    }

    private readonly GameObject _root;
    private readonly int _floorLayer;
    private readonly Dictionary<WaterBody, Volume> _volumes = new();
    private GeoOrigin _placedFor;

    public WaterBodies(int floorLayer)
    {
        _root = new GameObject("streety water");
        _floorLayer = floorLayer;
    }

    /// <summary>
    /// the trigger at a water surface, as opposed to anything else in the level.
    /// </summary>
    public bool IsSurface(Collider collider) =>
        collider != null && collider.isTrigger && collider.transform.parent == _root.transform;

    public void Update(WaterMap map, GeoOrigin origin)
    {
        var moved = _placedFor != origin;
        _placedFor = origin;

        var regions = map.Regions;

        // bodies whose cell was forgotten
        List<WaterBody> gone = null;

        foreach (var (body, volume) in _volumes)
        {
            if (!regions.Contains(volume.Region))
                (gone ??= new List<WaterBody>()).Add(body);
        }

        if (gone != null)
        {
            foreach (var body in gone)
            {
                UnityEngine.Object.Destroy(_volumes[body].Object);
                _volumes.Remove(body);
            }
        }

        foreach (var region in regions)
        foreach (var body in region.Bodies)
        {
            var level = body.Level;

            if (!level.HasValue)
                continue;

            if (!_volumes.TryGetValue(body, out var volume))
            {
                _volumes[body] = Create(region, body, level.Value, origin);
                continue;
            }

            if (moved || Math.Abs(volume.Level - level.Value) > MoveThreshold)
            {
                volume.Level = level.Value;
                Place(volume, origin);
            }
        }
    }

    private Volume Create(WaterRegion region, WaterBody body, double level, GeoOrigin origin)
    {
        var (south, west, north, east) = region.Bounds(body);
        var size = new Vector3(
            (body.MaxX - body.MinX + 1) * (float)WaterRegion.PixelMetres,
            Depth,
            (body.MaxY - body.MinY + 1) * (float)WaterRegion.PixelMetres);

        var gameObject = new GameObject("water");
        gameObject.transform.SetParent(_root.transform, false);

        var trigger = gameObject.AddComponent<BoxCollider>();
        trigger.isTrigger = true;
        trigger.size = size;

        FluidLabBridge.TryAttach(gameObject, size, Density);

        var floor = new GameObject("water floor") { layer = _floorLayer };
        floor.transform.SetParent(gameObject.transform, false);
        floor.transform.localPosition = new Vector3(0, -(Depth + FloorThickness) * 0.5f, 0);
        floor.AddComponent<BoxCollider>().size = new Vector3(size.x, FloorThickness, size.z);

        var volume = new Volume
        {
            Region = region,
            Object = gameObject,
            Level = level,
            Latitude = (south + north) * 0.5,
            Longitude = (west + east) * 0.5,
        };

        Place(volume, origin);
        return volume;
    }

    private static void Place(Volume volume, GeoOrigin origin)
    {
        var centre = Wgs84.GeodeticToEcef(volume.Latitude, volume.Longitude, volume.Level - Depth * 0.5);
        var (_, north, up) = Wgs84.EnuBasis(volume.Latitude, volume.Longitude);
        var n = origin.EcefDirectionToLocal(north);
        var u = origin.EcefDirectionToLocal(up);

        volume.Object.transform.SetPositionAndRotation(
            origin.EcefToUnity(centre),
            Quaternion.LookRotation(new Vector3((float)n.X, (float)n.Y, (float)n.Z), new Vector3((float)u.X, (float)u.Y, (float)u.Z)));
    }

    public void Dispose()
    {
        if (_root != null)
            UnityEngine.Object.Destroy(_root);

        _volumes.Clear();
    }
}
