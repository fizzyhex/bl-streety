using Streety.Geo;

namespace Streety.Gltf;

/// <summary>
/// cpu-side, upload-ready tile geometry. built on worker threads; contains no il2cpp objects.
/// </summary>
public sealed class TileContent
{
    public List<NodeMesh> Nodes { get; } = new();

    /// <summary>
    /// encoded images (jpeg/png) indexed by gltf image index.
    /// </summary>
    public List<byte[]> Images { get; } = new();

    public string Copyright { get; set; }

    public long EstimatedBytes
    {
        get
        {
            long total = 0;

            foreach (var node in Nodes)
                total += node.Vertices.Length * sizeof(float) + node.Indices.Length;

            // assume decoded rgba with mips, roughly 8x the jpeg
            foreach (var image in Images)
                total += image.Length * 8L;

            return total;
        }
    }
}

/// <summary>
/// one gltf node's geometry merged into a single vertex/index buffer with a submesh per primitive.
/// </summary>
public sealed class NodeMesh
{
    /// <summary>
    /// gltf node world matrix (gltf y-up space, before the 3d tiles y-up -> z-up conversion).
    /// </summary>
    public Double4x4 Matrix;

    /// <summary>
    /// interleaved position (3 floats) + uv (2 floats); z already mirrored for unity.
    /// </summary>
    public float[] Vertices;

    public int VertexCount;

    /// <summary>
    /// raw index bytes; uint16 when <see cref="Use32BitIndices"/> is false, else uint32. winding already reversed.
    /// </summary>
    public byte[] Indices;

    public bool Use32BitIndices;

    public List<SubMesh> SubMeshes = new();

    // local-space bounds of the mirrored vertices
    public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

    public const int FloatsPerVertex = 5;
}

public struct SubMesh
{
    public int IndexStart;
    public int IndexCount;
    public int BaseVertex;

    /// <summary>
    /// image index for the base colour, or -1.
    /// </summary>
    public int ImageIndex;
}
