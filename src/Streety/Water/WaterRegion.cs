using System.Text.Json;

namespace Streety.Water;

/// <summary>
/// one grid cell of openstreetmap water, rasterised into connected bodies. built off the main thread, then immutable
/// apart from each body's sampled water level.
/// </summary>
/// <remarks>
/// the raster is an equirectangular patch in metres, which is plenty accurate over a couple of kilometres. it covers
/// the cell plus a margin so bodies and coastlines near the edge still connect up; only the core belongs to this cell.
/// </remarks>
public sealed class WaterRegion
{
    public const double CellDegrees = 0.02;
    public const double MarginDegrees = 0.002;
    public const double PixelMetres = 2.5;

    // anything smaller is a fountain or a mapping sliver, not something to swim in
    private const int MinBodyPixels = 8;

    public int CellY { get; }
    public int CellX { get; }
    public long Key => CellKey(CellY, CellX);

    public IReadOnlyList<WaterBody> Bodies => _bodies;

    private readonly double _south, _west;
    private readonly double _metresPerDegLat, _metresPerDegLon;
    private readonly int _width, _height;
    private readonly int _coreX0, _coreY0, _coreX1, _coreY1;
    private readonly ushort[] _labels;
    private readonly List<WaterBody> _bodies = new();

    private WaterRegion(int cellY, int cellX)
    {
        CellY = cellY;
        CellX = cellX;

        var (south, west, north, east) = CellBounds(cellY, cellX);
        _south = south - MarginDegrees;
        _west = west - MarginDegrees;

        var lat = (south + north) * 0.5 * Math.PI / 180;
        _metresPerDegLat = 111132.92 - 559.82 * Math.Cos(2 * lat) + 1.175 * Math.Cos(4 * lat);
        _metresPerDegLon = 111412.84 * Math.Cos(lat) - 93.5 * Math.Cos(3 * lat);

        _width = (int)Math.Ceiling((east - west + 2 * MarginDegrees) * _metresPerDegLon / PixelMetres);
        _height = (int)Math.Ceiling((north - south + 2 * MarginDegrees) * _metresPerDegLat / PixelMetres);

        _coreX0 = (int)(MarginDegrees * _metresPerDegLon / PixelMetres);
        _coreY0 = (int)(MarginDegrees * _metresPerDegLat / PixelMetres);
        _coreX1 = Math.Min(_width, (int)Math.Ceiling((east - _west) * _metresPerDegLon / PixelMetres));
        _coreY1 = Math.Min(_height, (int)Math.Ceiling((north - _south) * _metresPerDegLat / PixelMetres));

        _labels = new ushort[_width * _height];
    }

    public static long CellKey(int cellY, int cellX) => ((long)cellY << 32) | (uint)cellX;

    public static (int cellY, int cellX) CellOf(double latitude, double longitude) =>
        ((int)Math.Floor(latitude / CellDegrees), (int)Math.Floor(longitude / CellDegrees));

    public static (double south, double west, double north, double east) CellBounds(int cellY, int cellX) =>
        (cellY * CellDegrees, cellX * CellDegrees, (cellY + 1) * CellDegrees, (cellX + 1) * CellDegrees);

    /// <summary>
    /// the body at a point inside this cell, or null for dry land.
    /// </summary>
    public WaterBody Find(double latitude, double longitude)
    {
        var x = (int)((longitude - _west) * _metresPerDegLon / PixelMetres);
        var y = (int)((latitude - _south) * _metresPerDegLat / PixelMetres);

        if (x < 0 || y < 0 || x >= _width || y >= _height)
            return null;

        var label = _labels[y * _width + x];
        return label == 0 ? null : _bodies[label - 1];
    }

    /// <summary>
    /// geodetic bounds of a body's pixels inside this cell's core.
    /// </summary>
    public (double south, double west, double north, double east) Bounds(WaterBody body) => (
        _south + body.MinY * PixelMetres / _metresPerDegLat,
        _west + body.MinX * PixelMetres / _metresPerDegLon,
        _south + (body.MaxY + 1) * PixelMetres / _metresPerDegLat,
        _west + (body.MaxX + 1) * PixelMetres / _metresPerDegLon);

    /// <summary>
    /// parses an overpass <c>out geom</c> response. a cell with no water still yields an (empty) region.
    /// </summary>
    public static WaterRegion Build(int cellY, int cellX, ReadOnlySpan<byte> json)
    {
        var region = new WaterRegion(cellY, cellX);
        var water = new byte[region._width * region._height];
        var coast = new List<(double x, double y)[]>();

        using (var document = JsonDocument.Parse(json.ToArray()))
        {
            foreach (var element in document.RootElement.GetProperty("elements").EnumerateArray())
            {
                var isCoast = element.TryGetProperty("tags", out var tags)
                              && tags.TryGetProperty("natural", out var natural)
                              && natural.GetString() == "coastline";

                switch (element.GetProperty("type").GetString())
                {
                    case "way" when isCoast:
                        coast.Add(region.ReadLine(element));
                        break;

                    case "way":
                        var ring = region.ReadLine(element);

                        // an unclosed water way is broken data; filling it would flood the cell
                        if (ring.Length > 3 && ring[0] == ring[^1])
                            region.FillEvenOdd(water, new List<(double, double)[]> { ring });

                        break;

                    case "relation" when element.TryGetProperty("members", out var members):
                        var rings = new List<(double x, double y)[]>();

                        foreach (var member in members.EnumerateArray())
                        {
                            if (member.TryGetProperty("geometry", out _))
                                rings.Add(region.ReadLine(member));
                        }

                        // members needn't be joined into rings: even-odd parity only cares about the edges
                        region.FillEvenOdd(water, rings);
                        break;
                }
            }
        }

        if (coast.Count > 0)
            region.FillSea(water, coast);

        region.Label(water);
        return region;
    }

    private (double x, double y)[] ReadLine(JsonElement element)
    {
        var geometry = element.GetProperty("geometry");
        var points = new (double x, double y)[geometry.GetArrayLength()];
        var i = 0;

        foreach (var node in geometry.EnumerateArray())
        {
            // relation members can carry null placeholders for nodes outside the response
            if (node.ValueKind != JsonValueKind.Object)
                continue;

            points[i++] = (
                (node.GetProperty("lon").GetDouble() - _west) * _metresPerDegLon / PixelMetres,
                (node.GetProperty("lat").GetDouble() - _south) * _metresPerDegLat / PixelMetres);
        }

        return i == points.Length ? points : points[..i];
    }

    private void FillEvenOdd(byte[] water, List<(double x, double y)[]> rings)
    {
        var rows = new List<double>[_height];

        foreach (var ring in rings)
        {
            for (var i = 0; i + 1 < ring.Length; i++)
            {
                var (x0, y0) = ring[i];
                var (x1, y1) = ring[i + 1];

                if (y0 == y1)
                    continue;

                // rows whose centre line this edge crosses
                var first = Math.Max(0, (int)Math.Ceiling(Math.Min(y0, y1) - 0.5));
                var last = Math.Min(_height - 1, (int)Math.Ceiling(Math.Max(y0, y1) - 0.5) - 1);

                for (var row = first; row <= last; row++)
                {
                    var y = row + 0.5;
                    (rows[row] ??= new List<double>()).Add(x0 + (y - y0) * (x1 - x0) / (y1 - y0));
                }
            }
        }

        for (var row = 0; row < _height; row++)
        {
            var crossings = rows[row];

            if (crossings == null)
                continue;

            crossings.Sort();

            for (var i = 0; i + 1 < crossings.Count; i += 2)
            {
                var from = Math.Max(0, (int)Math.Ceiling(crossings[i] - 0.5));
                var to = Math.Min(_width - 1, (int)Math.Floor(crossings[i + 1] - 0.5));

                for (var x = from; x <= to; x++)
                    water[row * _width + x] = 1;
            }
        }
    }

    /// <summary>
    /// osm coastlines keep land on the left and sea on the right. the coastline splits the raster into areas, and each
    /// area is sea or land by a vote of the points just either side of it, so one odd segment can't flood the land.
    /// </summary>
    private void FillSea(byte[] water, List<(double x, double y)[]> coast)
    {
        var barrier = new bool[_width * _height];

        foreach (var line in coast)
        {
            for (var i = 0; i + 1 < line.Length; i++)
                DrawLine(barrier, line[i], line[i + 1]);
        }

        var areas = new int[_width * _height];
        var count = 0;
        var stack = new Stack<int>();

        for (var start = 0; start < areas.Length; start++)
        {
            if (barrier[start] || areas[start] != 0)
                continue;

            count++;
            Flood(start, count, stack, i => !barrier[i] && areas[i] == 0, i => areas[i] = count);
        }

        var votes = new int[count + 1];

        foreach (var line in coast)
        {
            for (var i = 0; i + 1 < line.Length; i++)
            {
                var (x0, y0) = line[i];
                var (x1, y1) = line[i + 1];
                var dx = x1 - x0;
                var dy = y1 - y0;
                var length = Math.Sqrt(dx * dx + dy * dy);

                if (length < 2)
                    continue;

                // right-hand normal, with y pointing north
                var nx = dy / length * 2;
                var ny = -dx / length * 2;
                var mx = (x0 + x1) * 0.5;
                var my = (y0 + y1) * 0.5;

                Vote(areas, votes, mx + nx, my + ny, 1);
                Vote(areas, votes, mx - nx, my - ny, -1);
            }
        }

        for (var i = 0; i < areas.Length; i++)
        {
            if (votes[areas[i]] > 0)
                water[i] = 1;
        }
    }

    private void Vote(int[] areas, int[] votes, double x, double y, int vote)
    {
        var px = (int)x;
        var py = (int)y;

        if (px >= 0 && py >= 0 && px < _width && py < _height)
            votes[areas[py * _width + px]] += vote;
    }

    /// <summary>
    /// supercover line, so a 4-connected flood can't slip through diagonally.
    /// </summary>
    private void DrawLine(bool[] cells, (double x, double y) a, (double x, double y) b)
    {
        var steps = (int)Math.Ceiling(Math.Max(Math.Abs(b.x - a.x), Math.Abs(b.y - a.y)) * 2) + 1;
        int lastX = int.MinValue, lastY = int.MinValue;

        for (var s = 0; s <= steps; s++)
        {
            var t = (double)s / steps;
            var x = (int)Math.Floor(a.x + (b.x - a.x) * t);
            var y = (int)Math.Floor(a.y + (b.y - a.y) * t);

            if (lastX != int.MinValue && x != lastX && y != lastY)
                Mark(cells, x, lastY);

            Mark(cells, x, y);
            lastX = x;
            lastY = y;
        }
    }

    private void Mark(bool[] cells, int x, int y)
    {
        if (x >= 0 && y >= 0 && x < _width && y < _height)
            cells[y * _width + x] = true;
    }

    private void Label(byte[] water)
    {
        var stack = new Stack<int>();
        var pixels = new List<int>();

        for (var start = 0; start < water.Length; start++)
        {
            if (water[start] == 0 || _labels[start] != 0)
                continue;

            if (_bodies.Count == ushort.MaxValue - 1)
                break;

            var label = (ushort)(_bodies.Count + 1);
            pixels.Clear();
            Flood(start, label, stack, i => water[i] != 0 && _labels[i] == 0, i =>
            {
                _labels[i] = label;
                pixels.Add(i);
            });

            var body = new WaterBody();

            foreach (var i in pixels)
            {
                var x = i % _width;
                var y = i / _width;

                if (x < _coreX0 || y < _coreY0 || x >= _coreX1 || y >= _coreY1)
                    continue;

                body.Include(x, y);
            }

            // too small, or only in the margin (that's the neighbouring cell's body)
            if (pixels.Count < MinBodyPixels || body.Pixels == 0)
            {
                foreach (var i in pixels)
                    _labels[i] = ushort.MaxValue;

                continue;
            }

            _bodies.Add(body);
        }

        // ushort.MaxValue marked discarded pixels so the flood didn't revisit them
        for (var i = 0; i < _labels.Length; i++)
        {
            if (_labels[i] == ushort.MaxValue)
                _labels[i] = 0;
        }

        // margin pixels belong to the neighbouring cell; lookups there are answered by that cell
        for (var y = 0; y < _height; y++)
        for (var x = 0; x < _width; x++)
        {
            if (x < _coreX0 || y < _coreY0 || x >= _coreX1 || y >= _coreY1)
                _labels[y * _width + x] = 0;
        }
    }

    private void Flood(int start, int label, Stack<int> stack, Func<int, bool> open, Action<int> take)
    {
        stack.Push(start);
        take(start);

        while (stack.Count > 0)
        {
            var i = stack.Pop();
            var x = i % _width;

            if (x > 0 && open(i - 1)) { take(i - 1); stack.Push(i - 1); }
            if (x < _width - 1 && open(i + 1)) { take(i + 1); stack.Push(i + 1); }
            if (i >= _width && open(i - _width)) { take(i - _width); stack.Push(i - _width); }
            if (i + _width < _labels.Length && open(i + _width)) { take(i + _width); stack.Push(i + _width); }
        }
    }
}

/// <summary>
/// one connected body of water within a cell. its surface height is learned from the tiles that cover it.
/// </summary>
public sealed class WaterBody
{
    private const double BinMetres = 0.25;

    // below this much sampled surface the level is a guess
    private const double MinSampledArea = 20;

    // the surface is the low end of what's sampled: bridges, boats and bank overlap only ever sit above it
    private const double LevelPercentile = 0.25;

    public int MinX { get; private set; } = int.MaxValue;
    public int MinY { get; private set; } = int.MaxValue;
    public int MaxX { get; private set; } = int.MinValue;
    public int MaxY { get; private set; } = int.MinValue;
    public int Pixels { get; private set; }

    private readonly object _lock = new();
    private readonly Dictionary<int, double> _bins = new();
    private double _area;
    private double? _level;
    private bool _dirty;

    internal void Include(int x, int y)
    {
        MinX = Math.Min(MinX, x);
        MinY = Math.Min(MinY, y);
        MaxX = Math.Max(MaxX, x);
        MaxY = Math.Max(MaxY, y);
        Pixels++;
    }

    public static int Bin(double height) => (int)Math.Floor(height / BinMetres);

    /// <summary>
    /// ellipsoidal height of the surface, or null until enough of it has been seen. thread-safe.
    /// </summary>
    public double? Level
    {
        get
        {
            lock (_lock)
            {
                if (_dirty)
                {
                    _level = ComputeLevel();
                    _dirty = false;
                }

                return _level;
            }
        }
    }

    /// <summary>
    /// adds area-weighted height samples (height bin -> m²). thread-safe.
    /// </summary>
    public void AddSamples(Dictionary<int, double> bins)
    {
        lock (_lock)
        {
            foreach (var (bin, area) in bins)
            {
                _bins[bin] = _bins.TryGetValue(bin, out var existing) ? existing + area : area;
                _area += area;
            }

            _dirty = true;
        }
    }

    private double? ComputeLevel()
    {
        if (_area < MinSampledArea)
            return null;

        var target = _area * LevelPercentile;
        var sum = 0.0;

        foreach (var (bin, area) in _bins.OrderBy(kv => kv.Key))
        {
            sum += area;

            if (sum >= target)
                return (bin + 0.5) * BinMetres;
        }

        return null;
    }
}
