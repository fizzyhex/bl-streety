using System.Net.Http;
using System.Text.Json;

namespace Streety.Services;

public readonly record struct GeocodeResult(double Latitude, double Longitude, string Name);

/// <summary>
/// google geocoding api: place name -> lat/lng. needs the geocoding api enabled on the same key.
/// </summary>
public sealed class Geocoder : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly string _key;

    public Geocoder(string key)
    {
        _key = key;
    }

    public async Task<GeocodeResult> LookupAsync(string query, CancellationToken token)
    {
        var url = "https://maps.googleapis.com/maps/api/geocode/json"
                  + $"?address={Uri.EscapeDataString(query)}&key={Uri.EscapeDataString(_key)}";

        using var response = await _http.GetAsync(url, token).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);

        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var status = root.GetProperty("status").GetString();

        switch (status)
        {
            case "OK":
                break;
            case "ZERO_RESULTS":
                throw new GeocodeException("couldn't find that place");
            case "REQUEST_DENIED":
                throw new GeocodeException("geocoding denied; is the geocoding api enabled for this key?");
            default:
                throw new GeocodeException($"geocoding failed ({status?.ToLowerInvariant()})");
        }

        var result = root.GetProperty("results")[0];
        var location = result.GetProperty("geometry").GetProperty("location");
        var name = result.TryGetProperty("formatted_address", out var address) ? address.GetString() : query;

        return new GeocodeResult(location.GetProperty("lat").GetDouble(), location.GetProperty("lng").GetDouble(), name);
    }

    public void Dispose() => _http.Dispose();
}

public sealed class GeocodeException : Exception
{
    public GeocodeException(string message) : base(message)
    {
    }
}
