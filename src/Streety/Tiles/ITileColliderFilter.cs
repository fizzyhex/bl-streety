using Streety.Geo;
using Streety.Gltf;

namespace Streety.Tiles;

/// <summary>
/// removes triangles from tile colliders (e.g. water surfaces). runs on thread-pool threads.
/// </summary>
public interface ITileColliderFilter
{
    /// <summary>
    /// changes whenever filtering might give a different answer; tiles with colliders are re-filtered.
    /// read on the main thread only.
    /// </summary>
    int Revision { get; }

    /// <summary>
    /// per node, the triangle indices to keep (absolute, no base vertex), or null to keep the node whole.
    /// returns null when nothing is removed.
    /// </summary>
    uint[][] Filter(TileContent content, IReadOnlyList<Double4x4> models);
}
