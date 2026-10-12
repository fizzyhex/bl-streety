using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;

using MelonLoader.Utils;

using Streety.Geo;
using Streety.Gltf;
using Streety.Tiles;

using UnityEngine;

namespace Streety.Water;

/// <summary>
/// openstreetmap water around every rig, fetched from overpass one grid cell at a time and cached on disk.
/// </summary>
/// <remarks>
/// one request per ~2 km cell, one at a time, cached for a month, so a session costs a handful of calls at most.
/// overpass servers are shared and often busy: a busy server backs off on its own clock and the next one in
/// <see cref="Prefs.OverpassUrls"/> takes over. every client derives the same water from the same osm data, so
/// nothing needs syncing over fusion.
/// </remarks>
public sealed class WaterMap : ITileColliderFilter, IDisposable
{
    // how close a rig gets to a cell edge before the neighbouring cell is wanted too
    private const double NeighbourMetres = 500;

    // cells nobody has needed for this long are dropped; cells this many cells from every rig go straight away (warps)
    private const float ForgetSeconds = 120f;
    private const int ForgetCells = 3;

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);

    // a busy server is left alone for this long, doubling per failure up to the max
    private const float ServerBackoffSeconds = 30f;
    private const float ServerBackoffMaxSeconds = 300f;

    // a cell whose data won't parse isn't a server problem; it waits this long before trying again
    private const float BadCellRetrySeconds = 600f;

    // overpass admits queries by their declared time and memory; ours are tiny, so saying so gets them in under load
    private const string QueryHeader = "[out:json][timeout:25][maxsize:33554432]";

    // tile triangles this far above a body's surface stay solid (bridges, piers, boats)
    private const double SurfaceClearance = 1.0;

    private enum CellState
    {
        Queued,
        Loading,
        Ready,
        Failed,
    }

    private sealed class Cell
    {
        public int Y, X;
        public CellState State;
        public bool Cached;
        public float RetryAt;
        public float LastNeeded;
        public float LastOccupied;
    }

    private sealed class Server
    {
        public string Url;
        public string Host;
        public int Failures;
        public float RetryAt;
    }

    /// <summary>
    /// the server couldn't answer (busy, rate-limited, timed out, unreachable); another server may.
    /// </summary>
    private sealed class ServerException : Exception
    {
        public ServerException(string message, Exception inner = null) : base(message, inner)
        {
        }
    }

    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly Server[] _servers;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<long, Cell> _cells = new();
    private readonly List<Cell> _occupied = new();
    private readonly ConcurrentDictionary<long, WaterRegion> _regions = new();
    private readonly ConcurrentQueue<(Cell cell, Server server, WaterRegion region, Exception error)> _completed = new();

    private Cell _loading;

    /// <summary>
    /// bumps whenever a cell's water arrives or leaves; tiles re-filter their colliders when it changes.
    /// </summary>
    public int Revision { get; private set; }

    /// <summary>
    /// true once any osm data is in use (for attribution).
    /// </summary>
    public bool HasData => !_regions.IsEmpty;

    public ICollection<WaterRegion> Regions => _regions.Values;

    public WaterMap()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            // a dead mirror should hand over quickly, not hold the queue for the whole request timeout
            ConnectTimeout = TimeSpan.FromSeconds(10),
        })
        {
            // the query's own 25s budget plus time to send the answer
            Timeout = TimeSpan.FromSeconds(40),
        };

        // overpass asks clients to identify themselves
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"streety/{StreetyMod.Version} (bonelab mod)");

        _cacheDir = Path.Combine(MelonEnvironment.UserDataDirectory, "streety", "osm");
        _servers = ParseServers(Prefs.OverpassUrls.Value);

        if (_servers.Length == 0)
            _servers = ParseServers(Prefs.OverpassUrls.DefaultValue);
    }

    private static Server[] ParseServers(string urls) => (urls ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(url => Uri.TryCreate(url, UriKind.Absolute, out _))
        .Select(url => new Server { Url = url, Host = new Uri(url).Host })
        .ToArray();

    public void Update(IReadOnlyList<Viewer> viewers)
    {
        while (_completed.TryDequeue(out var item))
            Complete(item.cell, item.server, item.region, item.error);

        var now = Time.time;
        _occupied.Clear();

        foreach (var viewer in viewers)
        {
            var (lat, lon, _) = Wgs84.EcefToGeodetic(viewer.Ecef);
            var occupied = Want(lat, lon, now);
            occupied.LastOccupied = now;
            _occupied.Add(occupied);

            // neighbours the rig is close to, so their water is there before it walks in
            var dLat = NeighbourMetres / 111_000.0;
            var dLon = dLat / Math.Max(0.05, Math.Cos(lat * Math.PI / 180));

            for (var y = -1; y <= 1; y++)
            for (var x = -1; x <= 1; x++)
            {
                if (x != 0 || y != 0)
                    Want(lat + y * dLat, lon + x * dLon, now);
            }
        }

        Forget(now);
        StartNext(now);
    }

    private Cell Want(double latitude, double longitude, float now)
    {
        var (cy, cx) = WaterRegion.CellOf(latitude, longitude);
        var key = WaterRegion.CellKey(cy, cx);

        if (!_cells.TryGetValue(key, out var cell))
        {
            cell = new Cell { Y = cy, X = cx, State = CellState.Queued, Cached = IsCached(cy, cx) };
            _cells.Add(key, cell);
        }

        cell.LastNeeded = now;
        return cell;
    }

    private void Forget(float now)
    {
        List<long> stale = null;

        foreach (var (key, cell) in _cells)
        {
            if (cell == _loading || cell.LastNeeded == now)
                continue;

            if (now - cell.LastNeeded > ForgetSeconds || IsFar(cell))
                (stale ??= new List<long>()).Add(key);
        }

        if (stale == null)
            return;

        foreach (var key in stale)
        {
            _cells.Remove(key);

            if (_regions.TryRemove(key, out _))
                Revision++;
        }
    }

    private bool IsFar(Cell cell)
    {
        if (_occupied.Count == 0)
            return false;

        foreach (var occupied in _occupied)
        {
            if (Math.Abs(cell.Y - occupied.Y) <= ForgetCells && Math.Abs(cell.X - occupied.X) <= ForgetCells)
                return false;
        }

        return true;
    }

    private void StartNext(float now)
    {
        if (_loading != null)
            return;

        var server = NextServer(now);
        Cell next = null;

        foreach (var cell in _cells.Values)
        {
            // only what a rig wants right now (not where it just warped from), and the cells rigs stand in first.
            // cached cells don't need a server, so they still load while every server is backing off
            var due = cell.LastNeeded == now
                      && (cell.State == CellState.Queued || (cell.State == CellState.Failed && now >= cell.RetryAt))
                      && (cell.Cached || server != null);

            if (due && (next == null || (cell.LastOccupied == now && next.LastOccupied != now)))
                next = cell;
        }

        if (next == null)
            return;

        if (next.Cached)
            server = null;

        _loading = next;
        next.State = CellState.Loading;

        var token = _lifetime.Token;
        Task.Run(async () =>
        {
            try
            {
                var json = server == null
                    ? await ReadCacheAsync(next.Y, next.X, token).ConfigureAwait(false)
                    : await FetchAsync(server, next.Y, next.X, token).ConfigureAwait(false);

                _completed.Enqueue((next, server, WaterRegion.Build(next.Y, next.X, json), null));
            }
            catch (Exception e) when (!token.IsCancellationRequested)
            {
                _completed.Enqueue((next, server, null, e));
            }
        }, token);
    }

    /// <summary>
    /// the first server in preference order that isn't backing off, or null.
    /// </summary>
    private Server NextServer(float now)
    {
        foreach (var server in _servers)
        {
            if (now >= server.RetryAt)
                return server;
        }

        return null;
    }

    private void Complete(Cell cell, Server server, WaterRegion region, Exception error)
    {
        _loading = null;

        if (error is ServerException && server != null)
        {
            // the cell's fine; the next free server picks it up straight away
            cell.State = CellState.Queued;
            server.Failures++;

            var backoff = Math.Min(ServerBackoffMaxSeconds, ServerBackoffSeconds * (1 << Math.Min(server.Failures - 1, 8)));
            server.RetryAt = Time.time + backoff;

            var fallback = NextServer(Time.time);
            StreetyMod.Log.Warning(fallback != null
                ? $"water: {server.Host} {error.Message}; trying {fallback.Host}"
                : $"water: {server.Host} {error.Message}; every overpass server is busy, waiting {backoff:F0}s");
            return;
        }

        if (error != null)
        {
            cell.State = CellState.Failed;
            cell.Cached = false;
            cell.RetryAt = Time.time + BadCellRetrySeconds;
            StreetyMod.Log.Warning($"water lookup failed for cell {cell.Y},{cell.X}: {error.Message}");
            return;
        }

        if (server != null)
            server.Failures = 0;

        cell.State = CellState.Ready;
        cell.Cached = true;

        // forgotten while loading
        if (!_cells.ContainsKey(region.Key))
            return;

        _regions[region.Key] = region;
        Revision++;
        StreetyMod.Log.Msg($"water: cell {cell.Y},{cell.X} has {region.Bodies.Count} bodies"
                           + (server != null ? $" (from {server.Host})" : ""));
    }

    private string CachePath(int cellY, int cellX) => Path.Combine(_cacheDir, $"water_{cellY}_{cellX}.json");

    private bool IsCached(int cellY, int cellX)
    {
        var path = CachePath(cellY, cellX);
        return File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < CacheLifetime;
    }

    private Task<byte[]> ReadCacheAsync(int cellY, int cellX, CancellationToken token) =>
        File.ReadAllBytesAsync(CachePath(cellY, cellX), token);

    private async Task<byte[]> FetchAsync(Server server, int cellY, int cellX, CancellationToken token)
    {
        var (south, west, north, east) = WaterRegion.CellBounds(cellY, cellX);
        var m = WaterRegion.MarginDegrees;
        var bbox = string.Format(CultureInfo.InvariantCulture, "({0:F5},{1:F5},{2:F5},{3:F5})",
            south - m, west - m, north + m, east + m);

        // standing water, river areas and reservoirs, plus coastlines for the sea; dry and covered water is left out
        const string filter = "[\"intermittent\"!=\"yes\"][\"covered\"!=\"yes\"][!\"tunnel\"]";
        var query = QueryHeader + ";("
                    + $"way[\"natural\"=\"water\"]{filter}{bbox};"
                    + $"relation[\"natural\"=\"water\"]{filter}{bbox};"
                    + $"way[\"waterway\"=\"riverbank\"]{filter}{bbox};"
                    + $"relation[\"waterway\"=\"riverbank\"]{filter}{bbox};"
                    + $"way[\"landuse\"=\"reservoir\"]{filter}{bbox};"
                    + $"relation[\"landuse\"=\"reservoir\"]{filter}{bbox};"
                    + $"way[\"natural\"=\"coastline\"]{bbox};"
                    + ");out geom;";

        byte[] bytes;

        try
        {
            using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", query) });
            using var response = await _http.PostAsync(server.Url, content, token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new ServerException(response.StatusCode switch
                {
                    HttpStatusCode.TooManyRequests => "is rate-limiting us (429)",
                    HttpStatusCode.GatewayTimeout => "is busy (504)",
                    _ => $"returned {(int)response.StatusCode}",
                });
            }

            bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        }
        catch (Exception e) when ((e is HttpRequestException || e is TaskCanceledException) && !token.IsCancellationRequested)
        {
            throw new ServerException(e is TaskCanceledException ? "timed out" : $"is unreachable ({e.Message})", e);
        }

        // a query that runs out of time or memory still answers 200, with whatever it had and a remark; don't cache
        // half a cell
        using (var document = System.Text.Json.JsonDocument.Parse(bytes))
        {
            if (document.RootElement.TryGetProperty("remark", out var remark))
                throw new ServerException($"gave up ({remark.GetString()})");
        }

        try
        {
            Directory.CreateDirectory(_cacheDir);
            await File.WriteAllBytesAsync(CachePath(cellY, cellX), bytes, token).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            StreetyMod.Log.Warning($"couldn't cache water for cell {cellY},{cellX}: {e.Message}");
        }

        return bytes;
    }

    /// <summary>
    /// the body of water at a geodetic point, or null. thread-safe.
    /// </summary>
    public WaterBody Find(double latitude, double longitude)
    {
        var (cy, cx) = WaterRegion.CellOf(latitude, longitude);

        return _regions.TryGetValue(WaterRegion.CellKey(cy, cx), out var region)
            ? region.Find(latitude, longitude)
            : null;
    }

    /// <summary>
    /// drops tile triangles that are really the surface of mapped water, so rigs and props fall into it instead of
    /// standing on it. heights on the way through teach each body where its surface is.
    /// </summary>
    public uint[][] Filter(TileContent content, IReadOnlyList<Double4x4> models)
    {
        if (_regions.IsEmpty)
            return null;

        var nodes = content.Nodes;
        var wet = new (WaterBody[] bodies, double[] heights)[nodes.Count];
        var samples = new Dictionary<WaterBody, Dictionary<int, double>>();
        var any = false;

        for (var n = 0; n < nodes.Count; n++)
        {
            var node = nodes[n];
            var model = models[n];
            var triangles = CountTriangles(node);
            WaterBody[] bodies = null;
            double[] heights = null;
            var t = 0;

            foreach (var sub in node.SubMeshes)
            {
                for (var i = 0; i + 2 < sub.IndexCount; i += 3, t++)
                {
                    var a = ReadIndex(node, sub.IndexStart + i) + sub.BaseVertex;
                    var b = ReadIndex(node, sub.IndexStart + i + 1) + sub.BaseVertex;
                    var c = ReadIndex(node, sub.IndexStart + i + 2) + sub.BaseVertex;

                    var pa = Vertex(node, a);
                    var pb = Vertex(node, b);
                    var pc = Vertex(node, c);

                    // the model matrix is affine, so the centroid can be transformed once
                    var centroid = model.MultiplyPoint((pa + pb + pc) / 3);
                    var (lat, lon, height) = Wgs84.EcefToGeodetic(centroid);
                    var body = Find(lat, lon);

                    if (body == null)
                        continue;

                    var area = Double3.Cross(model.MultiplyVector(pb - pa), model.MultiplyVector(pc - pa)).Length * 0.5;

                    if (!samples.TryGetValue(body, out var bins))
                        samples[body] = bins = new Dictionary<int, double>();

                    var bin = WaterBody.Bin(height);
                    bins[bin] = bins.TryGetValue(bin, out var existing) ? existing + area : area;

                    bodies ??= new WaterBody[triangles];
                    heights ??= new double[triangles];
                    bodies[t] = body;
                    heights[t] = height;
                    any = true;
                }
            }

            wet[n] = (bodies, heights);
        }

        if (!any)
            return null;

        foreach (var (body, bins) in samples)
            body.AddSamples(bins);

        var levels = new Dictionary<WaterBody, double?>();
        var result = new uint[nodes.Count][];
        var dropped = false;

        for (var n = 0; n < nodes.Count; n++)
        {
            var (bodies, heights) = wet[n];

            if (bodies == null)
                continue;

            var node = nodes[n];
            var kept = new List<uint>(CountTriangles(node) * 3);
            var nodeDropped = false;
            var t = 0;

            foreach (var sub in node.SubMeshes)
            {
                for (var i = 0; i + 2 < sub.IndexCount; i += 3, t++)
                {
                    var body = bodies[t];

                    if (body != null)
                    {
                        if (!levels.TryGetValue(body, out var level))
                            levels[body] = level = body.Level;

                        if (level.HasValue && heights[t] < level.Value + SurfaceClearance)
                        {
                            nodeDropped = true;
                            continue;
                        }
                    }

                    kept.Add((uint)(ReadIndex(node, sub.IndexStart + i) + sub.BaseVertex));
                    kept.Add((uint)(ReadIndex(node, sub.IndexStart + i + 1) + sub.BaseVertex));
                    kept.Add((uint)(ReadIndex(node, sub.IndexStart + i + 2) + sub.BaseVertex));
                }
            }

            if (nodeDropped)
            {
                result[n] = kept.ToArray();
                dropped = true;
            }
        }

        return dropped ? result : null;
    }

    private static int CountTriangles(NodeMesh node)
    {
        var count = 0;

        foreach (var sub in node.SubMeshes)
            count += sub.IndexCount / 3;

        return count;
    }

    private static int ReadIndex(NodeMesh node, int i) => node.Use32BitIndices
        ? (int)BitConverter.ToUInt32(node.Indices, i * 4)
        : BitConverter.ToUInt16(node.Indices, i * 2);

    /// <summary>
    /// a vertex in gltf node space: undoes the z mirror applied for unity (see <see cref="GlbReader"/>).
    /// </summary>
    private static Double3 Vertex(NodeMesh node, int v)
    {
        var o = v * NodeMesh.FloatsPerVertex;
        return new Double3(node.Vertices[o], node.Vertices[o + 1], -node.Vertices[o + 2]);
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _http.Dispose();
        _lifetime.Dispose();
    }
}
