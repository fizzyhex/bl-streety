using System.Buffers.Binary;
using System.Text.Json;

using Streety.Geo;

namespace Streety.Gltf;

/// <summary>
/// minimal glb reader for google's photorealistic 3d tiles: triangle primitives with POSITION, TEXCOORD_0 and a
/// base colour texture, optionally draco compressed. thread-safe and allocation-light; runs on workers.
/// </summary>
/// <remarks>
/// output vertices are mirrored on local z and triangle winding is reversed to match <see cref="GeoOrigin"/>.
/// uvs are flipped vertically for unity's bottom-left texture origin.
/// </remarks>
public static class GlbReader
{
    private const uint Magic = 0x46546C67;
    private const uint ChunkJson = 0x4E4F534A;
    private const uint ChunkBin = 0x004E4942;

    private const int ComponentByte = 5120;
    private const int ComponentUnsignedByte = 5121;
    private const int ComponentShort = 5122;
    private const int ComponentUnsignedShort = 5123;
    private const int ComponentUnsignedInt = 5125;
    private const int ComponentFloat = 5126;

    private const int ModeTriangles = 4;

    private sealed class Primitive
    {
        public float[] Positions;
        public float[] Uvs;
        public uint[] Indices;
        public int ImageIndex = -1;
    }

    public static TileContent Read(ReadOnlyMemory<byte> glb)
    {
        var span = glb.Span;

        if (span.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(span) != Magic)
            throw new InvalidDataException("not a glb");

        var offset = 12;
        ReadOnlyMemory<byte> json = default;
        ReadOnlyMemory<byte> bin = default;

        while (offset + 8 <= span.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]);
            var type = BinaryPrimitives.ReadUInt32LittleEndian(span[(offset + 4)..]);
            var data = glb.Slice(offset + 8, length);

            if (type == ChunkJson)
                json = data;
            else if (type == ChunkBin)
                bin = data;

            offset += 8 + length;
        }

        if (json.IsEmpty)
            throw new InvalidDataException("glb has no json chunk");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var content = new TileContent();

        if (root.TryGetProperty("asset", out var asset) && asset.TryGetProperty("copyright", out var copyright))
            content.Copyright = copyright.GetString();

        ReadImages(root, bin, content);

        var scene = root.TryGetProperty("scene", out var sceneIndex) ? sceneIndex.GetInt32() : 0;

        if (!root.TryGetProperty("scenes", out var scenes) || !root.TryGetProperty("nodes", out var nodes))
            return content;

        foreach (var node in scenes[scene].GetProperty("nodes").EnumerateArray())
            VisitNode(root, bin, nodes, node.GetInt32(), Double4x4.Identity, content);

        return content;
    }

    private static void VisitNode(JsonElement root, ReadOnlyMemory<byte> bin, JsonElement nodes, int index,
        Double4x4 parent, TileContent content)
    {
        var node = nodes[index];
        var world = parent * LocalMatrix(node);

        if (node.TryGetProperty("mesh", out var meshIndex))
        {
            var mesh = root.GetProperty("meshes")[meshIndex.GetInt32()];
            var primitives = new List<Primitive>();

            foreach (var primitive in mesh.GetProperty("primitives").EnumerateArray())
            {
                var read = ReadPrimitive(root, bin, primitive);

                if (read != null)
                    primitives.Add(read);
            }

            if (primitives.Count > 0)
                content.Nodes.Add(Merge(primitives, world));
        }

        if (node.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray())
                VisitNode(root, bin, nodes, child.GetInt32(), world, content);
        }
    }

    private static Double4x4 LocalMatrix(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var matrix))
            return Double4x4.FromColumnMajor(matrix.EnumerateArray().Select(e => e.GetDouble()).ToArray());

        var t = Double3.Zero;
        var s = new Double3(1, 1, 1);
        double qx = 0, qy = 0, qz = 0, qw = 1;

        if (node.TryGetProperty("translation", out var tr))
            t = new Double3(tr[0].GetDouble(), tr[1].GetDouble(), tr[2].GetDouble());

        if (node.TryGetProperty("scale", out var sc))
            s = new Double3(sc[0].GetDouble(), sc[1].GetDouble(), sc[2].GetDouble());

        if (node.TryGetProperty("rotation", out var r))
        {
            qx = r[0].GetDouble();
            qy = r[1].GetDouble();
            qz = r[2].GetDouble();
            qw = r[3].GetDouble();
        }

        return Double4x4.FromTrs(t, qx, qy, qz, qw, s);
    }

    private static Primitive ReadPrimitive(JsonElement root, ReadOnlyMemory<byte> bin, JsonElement primitive)
    {
        var mode = primitive.TryGetProperty("mode", out var m) ? m.GetInt32() : ModeTriangles;

        if (mode != ModeTriangles)
            return null;

        var result = new Primitive
        {
            ImageIndex = ResolveBaseColorImage(root, primitive),
        };

        if (primitive.TryGetProperty("extensions", out var extensions)
            && extensions.TryGetProperty("KHR_draco_mesh_compression", out var draco))
        {
            DecodeDraco(root, bin, draco, result);
        }
        else
        {
            var attributes = primitive.GetProperty("attributes");

            result.Positions = ReadFloats(root, bin, attributes.GetProperty("POSITION").GetInt32(), 3);

            if (attributes.TryGetProperty("TEXCOORD_0", out var uv))
                result.Uvs = ReadFloats(root, bin, uv.GetInt32(), 2);

            result.Indices = primitive.TryGetProperty("indices", out var indices)
                ? ReadIndices(root, bin, indices.GetInt32())
                : SequentialIndices(result.Positions.Length / 3);
        }

        return result.Positions is { Length: > 0 } ? result : null;
    }

    private static int ResolveBaseColorImage(JsonElement root, JsonElement primitive)
    {
        if (!primitive.TryGetProperty("material", out var materialIndex)
            || !root.TryGetProperty("materials", out var materials))
            return -1;

        var material = materials[materialIndex.GetInt32()];

        if (!material.TryGetProperty("pbrMetallicRoughness", out var pbr)
            || !pbr.TryGetProperty("baseColorTexture", out var baseColor))
            return -1;

        var texture = root.GetProperty("textures")[baseColor.GetProperty("index").GetInt32()];
        return texture.TryGetProperty("source", out var source) ? source.GetInt32() : -1;
    }

    private static void ReadImages(JsonElement root, ReadOnlyMemory<byte> bin, TileContent content)
    {
        if (!root.TryGetProperty("images", out var images))
            return;

        foreach (var image in images.EnumerateArray())
        {
            if (image.TryGetProperty("bufferView", out var view))
                content.Images.Add(BufferView(root, bin, view.GetInt32(), out _).ToArray());
            else
                content.Images.Add(null);
        }
    }

    private static ReadOnlyMemory<byte> BufferView(JsonElement root, ReadOnlyMemory<byte> bin, int index, out int stride)
    {
        var view = root.GetProperty("bufferViews")[index];

        if (view.TryGetProperty("buffer", out var buffer) && buffer.GetInt32() != 0)
            throw new InvalidDataException("external buffers aren't supported");

        var offset = view.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0;
        var length = view.GetProperty("byteLength").GetInt32();
        stride = view.TryGetProperty("byteStride", out var s) ? s.GetInt32() : 0;

        return bin.Slice(offset, length);
    }

    private static int ComponentSize(int componentType) => componentType switch
    {
        ComponentByte or ComponentUnsignedByte => 1,
        ComponentShort or ComponentUnsignedShort => 2,
        ComponentUnsignedInt or ComponentFloat => 4,
        _ => throw new InvalidDataException($"unknown component type {componentType}"),
    };

    private static float[] ReadFloats(JsonElement root, ReadOnlyMemory<byte> bin, int accessorIndex, int components)
    {
        var accessor = root.GetProperty("accessors")[accessorIndex];
        var count = accessor.GetProperty("count").GetInt32();
        var componentType = accessor.GetProperty("componentType").GetInt32();
        var normalized = accessor.TryGetProperty("normalized", out var n) && n.GetBoolean();
        var accessorOffset = accessor.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0;

        var view = BufferView(root, bin, accessor.GetProperty("bufferView").GetInt32(), out var stride).Span;
        var size = ComponentSize(componentType);

        if (stride == 0)
            stride = size * components;

        var result = new float[count * components];

        for (var i = 0; i < count; i++)
        {
            var element = view[(accessorOffset + i * stride)..];

            for (var c = 0; c < components; c++)
            {
                var at = element[(c * size)..];

                result[i * components + c] = componentType switch
                {
                    ComponentFloat => BinaryPrimitives.ReadSingleLittleEndian(at),
                    ComponentUnsignedShort => BinaryPrimitives.ReadUInt16LittleEndian(at) / (normalized ? 65535f : 1f),
                    ComponentShort => Math.Max(BinaryPrimitives.ReadInt16LittleEndian(at) / (normalized ? 32767f : 1f), -1f),
                    ComponentUnsignedByte => at[0] / (normalized ? 255f : 1f),
                    ComponentByte => Math.Max((sbyte)at[0] / (normalized ? 127f : 1f), -1f),
                    _ => throw new InvalidDataException($"unsupported float accessor type {componentType}"),
                };
            }
        }

        return result;
    }

    private static uint[] ReadIndices(JsonElement root, ReadOnlyMemory<byte> bin, int accessorIndex)
    {
        var accessor = root.GetProperty("accessors")[accessorIndex];
        var count = accessor.GetProperty("count").GetInt32();
        var componentType = accessor.GetProperty("componentType").GetInt32();
        var accessorOffset = accessor.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0;

        var view = BufferView(root, bin, accessor.GetProperty("bufferView").GetInt32(), out var stride).Span;
        var size = ComponentSize(componentType);

        if (stride == 0)
            stride = size;

        var result = new uint[count];

        for (var i = 0; i < count; i++)
        {
            var at = view[(accessorOffset + i * stride)..];

            result[i] = componentType switch
            {
                ComponentUnsignedByte => at[0],
                ComponentUnsignedShort => BinaryPrimitives.ReadUInt16LittleEndian(at),
                ComponentUnsignedInt => BinaryPrimitives.ReadUInt32LittleEndian(at),
                _ => throw new InvalidDataException($"unsupported index type {componentType}"),
            };
        }

        return result;
    }

    private static uint[] SequentialIndices(int count)
    {
        var result = new uint[count];

        for (var i = 0; i < count; i++)
            result[i] = (uint)i;

        return result;
    }

    private static unsafe void DecodeDraco(JsonElement root, ReadOnlyMemory<byte> bin, JsonElement draco, Primitive result)
    {
        var data = BufferView(root, bin, draco.GetProperty("bufferView").GetInt32(), out _);
        var attributes = draco.GetProperty("attributes");

        DracoNative.DracoMesh* mesh = null;

        fixed (byte* ptr = data.Span)
        {
            if (DracoNative.DecodeDracoMesh(ptr, (uint)data.Length, &mesh) < 0 || mesh == null)
                throw new InvalidDataException("draco decode failed");
        }

        try
        {
            var vertexCount = mesh->NumVertices;

            result.Positions = ReadDracoAttribute(mesh, attributes.GetProperty("POSITION").GetInt32(), 3, vertexCount);

            if (attributes.TryGetProperty("TEXCOORD_0", out var uv))
                result.Uvs = ReadDracoAttribute(mesh, uv.GetInt32(), 2, vertexCount);

            DracoNative.DracoData* indices = null;

            if (!DracoNative.GetMeshIndices(mesh, &indices))
                throw new InvalidDataException("draco mesh has no indices");

            try
            {
                var count = mesh->NumFaces * 3;
                var source = (int*)indices->Data;
                result.Indices = new uint[count];

                for (var i = 0; i < count; i++)
                    result.Indices[i] = (uint)source[i];
            }
            finally
            {
                DracoNative.ReleaseDracoData(&indices);
            }
        }
        finally
        {
            DracoNative.ReleaseDracoMesh(&mesh);
        }
    }

    private static unsafe float[] ReadDracoAttribute(DracoNative.DracoMesh* mesh, int uniqueId, int components, int vertexCount)
    {
        DracoNative.DracoAttribute* attribute = null;

        if (!DracoNative.GetAttributeByUniqueId(mesh, uniqueId, &attribute))
            throw new InvalidDataException($"draco attribute {uniqueId} missing");

        DracoNative.DracoData* data = null;

        try
        {
            if (attribute->DataType != DracoNative.DataType.Float32 || attribute->NumComponents < components)
                throw new InvalidDataException($"unexpected draco attribute format {attribute->DataType}x{attribute->NumComponents}");

            if (!DracoNative.GetAttributeData(mesh, attribute, &data))
                throw new InvalidDataException("draco attribute data unavailable");

            var source = (float*)data->Data;
            var stride = attribute->NumComponents;
            var result = new float[vertexCount * components];

            for (var i = 0; i < vertexCount; i++)
            for (var c = 0; c < components; c++)
                result[i * components + c] = source[i * stride + c];

            return result;
        }
        finally
        {
            if (data != null)
                DracoNative.ReleaseDracoData(&data);

            DracoNative.ReleaseDracoAttribute(&attribute);
        }
    }

    /// <summary>
    /// interleaves, mirrors and concatenates primitives into one buffer with a submesh each.
    /// </summary>
    private static NodeMesh Merge(List<Primitive> primitives, Double4x4 matrix)
    {
        var vertexCount = 0;
        var indexCount = 0;
        var use32 = false;

        foreach (var p in primitives)
        {
            var count = p.Positions.Length / 3;
            vertexCount += count;
            indexCount += p.Indices.Length - p.Indices.Length % 3;

            // submeshes use baseVertex, so 16-bit only needs each primitive to fit
            if (count > ushort.MaxValue)
                use32 = true;
        }

        var mesh = new NodeMesh
        {
            Matrix = matrix,
            VertexCount = vertexCount,
            Vertices = new float[vertexCount * NodeMesh.FloatsPerVertex],
            Indices = new byte[indexCount * (use32 ? 4 : 2)],
            Use32BitIndices = use32,
            MinX = float.MaxValue, MinY = float.MaxValue, MinZ = float.MaxValue,
            MaxX = float.MinValue, MaxY = float.MinValue, MaxZ = float.MinValue,
        };

        var vertexCursor = 0;
        var indexCursor = 0;
        var indexSpan = mesh.Indices.AsSpan();

        foreach (var p in primitives)
        {
            var count = p.Positions.Length / 3;

            for (var i = 0; i < count; i++)
            {
                var o = (vertexCursor + i) * NodeMesh.FloatsPerVertex;
                var x = p.Positions[i * 3];
                var y = p.Positions[i * 3 + 1];
                var z = -p.Positions[i * 3 + 2];

                mesh.Vertices[o] = x;
                mesh.Vertices[o + 1] = y;
                mesh.Vertices[o + 2] = z;

                if (p.Uvs != null)
                {
                    mesh.Vertices[o + 3] = p.Uvs[i * 2];
                    mesh.Vertices[o + 4] = 1f - p.Uvs[i * 2 + 1];
                }

                if (x < mesh.MinX) mesh.MinX = x;
                if (y < mesh.MinY) mesh.MinY = y;
                if (z < mesh.MinZ) mesh.MinZ = z;
                if (x > mesh.MaxX) mesh.MaxX = x;
                if (y > mesh.MaxY) mesh.MaxY = y;
                if (z > mesh.MaxZ) mesh.MaxZ = z;
            }

            var triangles = p.Indices.Length / 3;
            var start = indexCursor;

            for (var t = 0; t < triangles; t++)
            {
                // reversed winding to undo the mirror
                WriteIndex(indexSpan, indexCursor++, p.Indices[t * 3], use32);
                WriteIndex(indexSpan, indexCursor++, p.Indices[t * 3 + 2], use32);
                WriteIndex(indexSpan, indexCursor++, p.Indices[t * 3 + 1], use32);
            }

            mesh.SubMeshes.Add(new SubMesh
            {
                IndexStart = start,
                IndexCount = indexCursor - start,
                BaseVertex = vertexCursor,
                ImageIndex = p.ImageIndex,
            });

            vertexCursor += count;
        }

        return mesh;
    }

    private static void WriteIndex(Span<byte> span, int index, uint value, bool use32)
    {
        if (use32)
            BinaryPrimitives.WriteUInt32LittleEndian(span[(index * 4)..], value);
        else
            BinaryPrimitives.WriteUInt16LittleEndian(span[(index * 2)..], (ushort)value);
    }
}
