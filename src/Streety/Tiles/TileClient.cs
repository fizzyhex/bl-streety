using System.Net;
using System.Net.Http;
using System.Web;

namespace Streety.Tiles;

/// <summary>
/// http access to the map tiles api. owns the api key and the tiles session; never logs either.
/// </summary>
public sealed class TileClient : IDisposable
{
    public static readonly Uri RootUri = new("https://tile.googleapis.com/v1/3dtiles/root.json");

    /// <summary>
    /// google asks for a fresh root request at least every three hours.
    /// </summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(2.5);

    private readonly HttpClient _http;
    private readonly string _key;

    public string Session { get; private set; }
    public DateTime SessionStartedUtc { get; private set; }

    public bool SessionExpired => DateTime.UtcNow - SessionStartedUtc > SessionLifetime;

    public TileClient(string key)
    {
        _key = key;

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            MaxConnectionsPerServer = 16,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"streety/{StreetyMod.Version}");
    }

    /// <summary>
    /// fetches root.json and adopts the session it hands out.
    /// </summary>
    public async Task<byte[]> FetchRootAsync(CancellationToken token)
    {
        var bytes = await GetAsync(RootUri, token).ConfigureAwait(false);

        Session = FindSession(bytes);
        SessionStartedUtc = DateTime.UtcNow;

        return bytes;
    }

    public async Task<byte[]> GetAsync(Uri uri, CancellationToken token)
    {
        using var response = await _http.GetAsync(Authorize(uri), HttpCompletionOption.ResponseContentRead, token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase} for {uri.AbsolutePath}", null, response.StatusCode);

        return await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// adds the key and the current session, replacing any stale session baked into a uri.
    /// </summary>
    private Uri Authorize(Uri uri)
    {
        var builder = new UriBuilder(uri);
        var query = HttpUtility.ParseQueryString(builder.Query);

        query["key"] = _key;

        if (!string.IsNullOrEmpty(Session))
            query["session"] = Session;

        builder.Query = query.ToString();
        return builder.Uri;
    }

    private static string FindSession(byte[] json)
    {
        const string marker = "session=";

        var text = System.Text.Encoding.UTF8.GetString(json);
        var index = text.IndexOf(marker, StringComparison.Ordinal);

        if (index < 0)
            return null;

        var start = index + marker.Length;
        var end = start;

        while (end < text.Length && text[end] != '"' && text[end] != '&')
            end++;

        return text[start..end];
    }

    public void Dispose() => _http.Dispose();
}
