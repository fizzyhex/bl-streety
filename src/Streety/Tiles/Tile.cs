using Streety.Geo;
using Streety.Gltf;
using Streety.Render;

namespace Streety.Tiles;

public enum ContentKind
{
    None,
    Model,
    ExternalTileset,
}

public enum TileState
{
    Unloaded,
    Loading,
    Decoded,
    Ready,
    Failed,
}

/// <summary>
/// one node of the 3d tiles hierarchy. mutated only on the main thread; workers hand results back through
/// <see cref="TileStreamer"/>.
/// </summary>
public sealed class Tile
{
    public Tile Parent { get; init; }
    public List<Tile> Children { get; } = new();
    public int Depth { get; init; }

    public BoundingVolume Volume { get; init; }
    public double GeometricError { get; init; }
    public bool RefineAdd { get; init; }

    /// <summary>
    /// accumulated tile transform (ecef); gltf content is placed with this * y-up -> z-up.
    /// </summary>
    public Double4x4 Transform { get; init; }

    public ContentKind ContentKind { get; init; }
    public Uri ContentUri { get; init; }

    public TileState State { get; set; }
    public int FailCount { get; set; }
    public float RetryAt { get; set; }

    public CancellationTokenSource Cancellation { get; set; }

    /// <summary>
    /// decoded content waiting for main-thread upload.
    /// </summary>
    public TileContent Pending { get; set; }

    public LoadedTile Loaded { get; set; }

    public int LastVisitedFrame { get; set; } = -1;
    public int LastSelectedFrame { get; set; } = -1;

    /// <summary>
    /// lower loads first; distance to the nearest viewer.
    /// </summary>
    public double Priority { get; set; }

    public bool HasRenderableContent => ContentKind == ContentKind.Model;

    public bool IsLeaf => Children.Count == 0 && ContentKind != ContentKind.ExternalTileset;

    public bool IsReady => !HasRenderableContent || State == TileState.Ready;

    public override string ToString() => $"tile d{Depth} ge{GeometricError:F1} {ContentKind} {State}";
}
