namespace Streety.Geo;

/// <summary>
/// wgs84 ellipsoid conversions between geodetic (lat/lon/height) and earth-centred earth-fixed coordinates.
/// </summary>
public static class Wgs84
{
    public const double SemiMajorAxis = 6378137.0;
    public const double Flattening = 1.0 / 298.257223563;
    public const double SemiMinorAxis = SemiMajorAxis * (1 - Flattening);
    public const double EccentricitySquared = Flattening * (2 - Flattening);

    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;

    public static Double3 GeodeticToEcef(double latitudeDeg, double longitudeDeg, double height)
    {
        var lat = latitudeDeg * DegToRad;
        var lon = longitudeDeg * DegToRad;
        var sinLat = Math.Sin(lat);
        var cosLat = Math.Cos(lat);

        var n = SemiMajorAxis / Math.Sqrt(1 - EccentricitySquared * sinLat * sinLat);

        return new Double3(
            (n + height) * cosLat * Math.Cos(lon),
            (n + height) * cosLat * Math.Sin(lon),
            (n * (1 - EccentricitySquared) + height) * sinLat);
    }

    /// <summary>
    /// bowring's method; sub-millimetre accurate for anything near the surface.
    /// </summary>
    public static (double latitudeDeg, double longitudeDeg, double height) EcefToGeodetic(Double3 ecef)
    {
        const double a = SemiMajorAxis;
        const double b = SemiMinorAxis;
        const double e2 = EccentricitySquared;
        var ep2 = (a * a - b * b) / (b * b);

        var p = Math.Sqrt(ecef.X * ecef.X + ecef.Y * ecef.Y);
        var theta = Math.Atan2(ecef.Z * a, p * b);
        var sinT = Math.Sin(theta);
        var cosT = Math.Cos(theta);

        var lat = Math.Atan2(ecef.Z + ep2 * b * sinT * sinT * sinT, p - e2 * a * cosT * cosT * cosT);
        var lon = Math.Atan2(ecef.Y, ecef.X);

        var sinLat = Math.Sin(lat);
        var n = a / Math.Sqrt(1 - e2 * sinLat * sinLat);
        var height = p / Math.Cos(lat) - n;

        return (lat * RadToDeg, lon * RadToDeg, height);
    }

    /// <summary>
    /// east/north/up unit vectors (in ecef) at a geodetic location.
    /// </summary>
    public static (Double3 east, Double3 north, Double3 up) EnuBasis(double latitudeDeg, double longitudeDeg)
    {
        var lat = latitudeDeg * DegToRad;
        var lon = longitudeDeg * DegToRad;
        double sinLat = Math.Sin(lat), cosLat = Math.Cos(lat);
        double sinLon = Math.Sin(lon), cosLon = Math.Cos(lon);

        var east = new Double3(-sinLon, cosLon, 0);
        var north = new Double3(-sinLat * cosLon, -sinLat * sinLon, cosLat);
        var up = new Double3(cosLat * cosLon, cosLat * sinLon, sinLat);

        return (east, north, up);
    }
}
