using Streety.Geo;

namespace Streety;

/// <summary>
/// A warp destination and, once the host has arrived, the ground its origin is centred on.
/// </summary>
/// <remarks>
/// Fusion syncs raw Unity positions, so every peer in a lobby has to derive the same <see cref="GeoOrigin"/>. The
/// host probes the ground and shares its ECEF position.
///
/// The ground only seeds the origin on arrival. Once the host rebases, the shared origin moves away from it, so the
/// current one is tracked separately in <see cref="Origin"/>.
/// </remarks>
public sealed class WarpTarget
{
    public double Latitude { get; }

    public double Longitude { get; }

    public string Name { get; }

    /// <summary>
    /// ECEF position of the ground the origin is centred on after arrival, or null until it's known.
    /// </summary>
    public Double3? Ground { get; set; }

    /// <summary>
    /// The origin every peer shares at this location, or null until it's known. Starts centred on
    /// <see cref="Ground"/> and follows the host's rebases after that.
    /// </summary>
    public GeoOrigin Origin { get; set; }

    public WarpTarget(double latitude, double longitude, string name)
    {
        Latitude = latitude;
        Longitude = longitude;
        Name = name;
    }

    /// <summary>
    /// Whether a ground message tagged with these coordinates belongs to this warp. Coordinates come over the
    /// network unchanged, so exact comparison is intended.
    /// </summary>
    public bool Matches(double latitude, double longitude) => Latitude == latitude && Longitude == longitude;
}
