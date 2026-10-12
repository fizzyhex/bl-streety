using Il2CppInterop.Runtime.InteropTypes.Arrays;

using Streety.Geo;
using Streety.Gltf;

using UnityEngine;
using UnityEngine.Rendering;

namespace Streety.Render;

/// <summary>
/// unity objects owned by one loaded tile.
/// </summary>
public sealed class LoadedTile
{
    public GameObject Root;
    public readonly List<GameObject> Parts = new();
    public readonly List<Mesh> Meshes = new();
    public readonly List<Texture2D> Textures = new();
    public readonly List<Material> Materials = new();

    /// <summary>
    /// ecef model matrix per part, for re-posing after an origin shift.
    /// </summary>
    public readonly List<Double4x4> PartMatrices = new();

    public string Copyright;
    public long Bytes;
    public bool Readable;

    public bool ColliderRequested;
    public bool HasCollider;

    public bool Visible;
}

/// <summary>
/// turns <see cref="TileContent"/> into unity objects. main thread only.
/// </summary>
public sealed class TileRenderer : IDisposable
{
    // gltf is y-up, 3d tiles are z-up; content is placed with tile.transform * this
    private static readonly Double4x4 YUpToZUp = Double4x4.FromColumnMajor(new double[]
    {
        1, 0, 0, 0,
        0, 0, 1, 0,
        0, -1, 0, 0,
        0, 0, 0, 1,
    });

    private static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
    private static readonly int MainTex = Shader.PropertyToID("_MainTex");

    private readonly Transform _parent;
    private readonly Material _template;
    private readonly int _layer;
    private readonly Il2CppStructArray<VertexAttributeDescriptor> _layout;

    public TileRenderer(Transform parent, Material template, int layer)
    {
        _parent = parent;
        _template = template;
        _layer = layer;

        // must follow VertexAttribute order; matches NodeMesh.Vertices
        _layout = new Il2CppStructArray<VertexAttributeDescriptor>(3);
        _layout[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0);
        _layout[1] = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, 0);
        _layout[2] = new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, 0);
    }

    public LoadedTile Create(Tiles.Tile tile, TileContent content, GeoOrigin origin, bool keepReadable)
    {
        var loaded = new LoadedTile
        {
            Root = new GameObject($"tile d{tile.Depth}"),
            Copyright = content.Copyright,
            Readable = keepReadable,
        };

        loaded.Root.layer = _layer;
        loaded.Root.transform.SetParent(_parent, false);
        loaded.Root.SetActive(false);

        var materials = CreateMaterials(content, loaded);

        foreach (var node in content.Nodes)
        {
            var mesh = CreateMesh(node, keepReadable);
            loaded.Meshes.Add(mesh);
            loaded.Bytes += node.Vertices.Length * sizeof(float) + node.Indices.Length;

            var part = new GameObject("part")
            {
                layer = _layer,
            };

            part.transform.SetParent(loaded.Root.transform, false);
            part.AddComponent<MeshFilter>().sharedMesh = mesh;

            var renderer = part.AddComponent<MeshRenderer>();
            // the imagery already has the sun's shadows baked in; only rigs and props shadow the tiles
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

            var partMaterials = new Il2CppReferenceArray<Material>(node.SubMeshes.Count);

            for (var i = 0; i < node.SubMeshes.Count; i++)
            {
                var image = node.SubMeshes[i].ImageIndex;
                partMaterials[i] = image >= 0 && image < materials.Length && materials[image] != null
                    ? materials[image]
                    : _template;
            }

            renderer.sharedMaterials = partMaterials;

            var model = tile.Transform * YUpToZUp * node.Matrix;
            loaded.Parts.Add(part);
            loaded.PartMatrices.Add(model);
        }

        Reposition(loaded, origin);
        return loaded;
    }

    /// <summary>
    /// re-poses a tile after the origin moved.
    /// </summary>
    public static void Reposition(LoadedTile loaded, GeoOrigin origin)
    {
        for (var i = 0; i < loaded.Parts.Count; i++)
        {
            var (position, rotation, scale) = origin.ModelMatrixToUnity(loaded.PartMatrices[i]);
            var transform = loaded.Parts[i].transform;

            transform.SetPositionAndRotation(position, rotation);
            transform.localScale = scale;
        }
    }

    private Material[] CreateMaterials(TileContent content, LoadedTile loaded)
    {
        var materials = new Material[content.Images.Count];

        for (var i = 0; i < content.Images.Count; i++)
        {
            var bytes = content.Images[i];

            if (bytes == null)
                continue;

            var texture = new Texture2D(2, 2, TextureFormat.RGB24, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
            };

            if (!ImageConversion.LoadImage(texture, bytes, true))
            {
                UnityEngine.Object.Destroy(texture);
                continue;
            }

            loaded.Textures.Add(texture);
            loaded.Bytes += (long)texture.width * texture.height * 4 * 4 / 3;

            var material = new Material(_template);

            if (material.HasProperty(BaseMap))
                material.SetTexture(BaseMap, texture);

            if (material.HasProperty(MainTex))
                material.SetTexture(MainTex, texture);

            loaded.Materials.Add(material);
            materials[i] = material;
        }

        return materials;
    }

    private unsafe Mesh CreateMesh(NodeMesh node, bool keepReadable)
    {
        const MeshUpdateFlags flags = MeshUpdateFlags.DontValidateIndices
                                      | MeshUpdateFlags.DontRecalculateBounds
                                      | MeshUpdateFlags.DontNotifyMeshUsers;

        var mesh = new Mesh();
        var format = node.Use32BitIndices ? IndexFormat.UInt32 : IndexFormat.UInt16;
        var indexSize = node.Use32BitIndices ? 4 : 2;
        var indexCount = node.Indices.Length / indexSize;

        mesh.SetVertexBufferParams(node.VertexCount, _layout);

        fixed (float* vertices = node.Vertices)
        {
            mesh.InternalSetVertexBufferData(0, (IntPtr)vertices, 0, 0, node.VertexCount,
                NodeMesh.FloatsPerVertex * sizeof(float), flags);
        }

        mesh.SetIndexBufferParams(indexCount, format);

        fixed (byte* indices = node.Indices)
        {
            mesh.InternalSetIndexBufferData((IntPtr)indices, 0, 0, indexCount, indexSize, flags);
        }

        mesh.subMeshCount = node.SubMeshes.Count;

        for (var i = 0; i < node.SubMeshes.Count; i++)
        {
            var sub = node.SubMeshes[i];

            mesh.SetSubMesh(i, new SubMeshDescriptor(sub.IndexStart, sub.IndexCount, MeshTopology.Triangles)
            {
                baseVertex = sub.BaseVertex,
            }, flags);
        }

        var min = new Vector3(node.MinX, node.MinY, node.MinZ);
        var max = new Vector3(node.MaxX, node.MaxY, node.MaxZ);
        mesh.bounds = new Bounds((min + max) * 0.5f, max - min);

        mesh.UploadMeshData(!keepReadable);
        return mesh;
    }

    /// <summary>
    /// adds a mesh collider after the meshes were baked off-thread.
    /// </summary>
    public static void AddColliders(LoadedTile loaded)
    {
        if (loaded.HasCollider || loaded.Root == null)
            return;

        for (var i = 0; i < loaded.Parts.Count; i++)
        {
            var collider = loaded.Parts[i].AddComponent<MeshCollider>();
            collider.sharedMesh = loaded.Meshes[i];
        }

        loaded.HasCollider = true;
    }

    public static void Destroy(LoadedTile loaded)
    {
        if (loaded.Root != null)
            UnityEngine.Object.Destroy(loaded.Root);

        foreach (var mesh in loaded.Meshes)
            if (mesh != null) UnityEngine.Object.Destroy(mesh);

        foreach (var texture in loaded.Textures)
            if (texture != null) UnityEngine.Object.Destroy(texture);

        foreach (var material in loaded.Materials)
            if (material != null) UnityEngine.Object.Destroy(material);

        loaded.Parts.Clear();
        loaded.Meshes.Clear();
        loaded.Textures.Clear();
        loaded.Materials.Clear();
    }

    public void Dispose()
    {
    }
}
