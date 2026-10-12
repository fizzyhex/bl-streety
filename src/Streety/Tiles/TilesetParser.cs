using System.Text.Json;

using Streety.Geo;

namespace Streety.Tiles;

/// <summary>
/// parses 3d tiles 1.0 tileset json into <see cref="Tile"/> nodes. thread-safe; runs on workers.
/// </summary>
public static class TilesetParser
{
    /// <summary>
    /// parses a tileset and returns its root, parented to <paramref name="parent"/> (for external tilesets).
    /// </summary>
    public static Tile Parse(ReadOnlyMemory<byte> json, Uri baseUri, Tile parent)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement.GetProperty("root");

        var parentTransform = parent?.Transform ?? Double4x4.Identity;
        var parentRefineAdd = parent?.RefineAdd ?? false;
        var depth = parent?.Depth + 1 ?? 0;

        return ParseTile(root, baseUri, parent, parentTransform, parentRefineAdd, depth);
    }

    private static Tile ParseTile(JsonElement json, Uri baseUri, Tile parent, Double4x4 parentTransform,
        bool parentRefineAdd, int depth)
    {
        var transform = json.TryGetProperty("transform", out var t)
            ? parentTransform * Double4x4.FromColumnMajor(t.EnumerateArray().Select(e => e.GetDouble()).ToArray())
            : parentTransform;

        var refineAdd = json.TryGetProperty("refine", out var refine)
            ? string.Equals(refine.GetString(), "ADD", StringComparison.OrdinalIgnoreCase)
            : parentRefineAdd;

        var kind = ContentKind.None;
        Uri contentUri = null;

        if (json.TryGetProperty("content", out var content))
        {
            var uri = content.TryGetProperty("uri", out var u) ? u.GetString() : content.GetProperty("url").GetString();
            contentUri = new Uri(baseUri, uri);

            kind = contentUri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? ContentKind.ExternalTileset
                : ContentKind.Model;
        }

        var tile = new Tile
        {
            Parent = parent,
            Depth = depth,
            Volume = BoundingVolume.Parse(json.GetProperty("boundingVolume"), transform),
            GeometricError = json.GetProperty("geometricError").GetDouble(),
            RefineAdd = refineAdd,
            Transform = transform,
            ContentKind = kind,
            ContentUri = contentUri,
        };

        if (json.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray())
                tile.Children.Add(ParseTile(child, baseUri, tile, transform, refineAdd, depth + 1));
        }

        return tile;
    }
}
