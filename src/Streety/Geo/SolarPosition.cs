namespace Streety.Geo;

/// <summary>
/// where the sun is in the sky for a place and moment; noaa's solar calculator equations, good to a fraction of a
/// degree for any date this side of 1900-2100. refraction is ignored.
/// </summary>
public static class SolarPosition
{
    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;

    /// <summary>
    /// azimuth (degrees clockwise from north) and elevation (degrees above the horizon).
    /// </summary>
    public static (double azimuth, double elevation) Compute(double latitudeDeg, double longitudeDeg, DateTime utc)
    {
        utc = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc;

        var julianDay = utc.ToOADate() + 2415018.5;
        var t = (julianDay - 2451545.0) / 36525.0;

        var meanLongitude = Mod(280.46646 + t * (36000.76983 + t * 0.0003032), 360);
        var meanAnomaly = 357.52911 + t * (35999.05029 - 0.0001537 * t);
        var eccentricity = 0.016708634 - t * (0.000042037 + 0.0000001267 * t);

        var m = meanAnomaly * DegToRad;
        var centre = Math.Sin(m) * (1.914602 - t * (0.004817 + 0.000014 * t))
                     + Math.Sin(2 * m) * (0.019993 - 0.000101 * t)
                     + Math.Sin(3 * m) * 0.000289;

        var omega = (125.04 - 1934.136 * t) * DegToRad;
        var apparentLongitude = (meanLongitude + centre - 0.00569 - 0.00478 * Math.Sin(omega)) * DegToRad;

        var meanObliquity = 23 + (26 + (21.448 - t * (46.815 + t * (0.00059 - t * 0.001813))) / 60) / 60;
        var obliquity = (meanObliquity + 0.00256 * Math.Cos(omega)) * DegToRad;

        var declination = Math.Asin(Math.Sin(obliquity) * Math.Sin(apparentLongitude));

        // equation of time, in minutes
        var y = Math.Tan(obliquity / 2);
        y *= y;
        var l0 = meanLongitude * DegToRad;
        var equationOfTime = 4 * RadToDeg * (y * Math.Sin(2 * l0)
                                             - 2 * eccentricity * Math.Sin(m)
                                             + 4 * eccentricity * y * Math.Sin(m) * Math.Cos(2 * l0)
                                             - 0.5 * y * y * Math.Sin(4 * l0)
                                             - 1.25 * eccentricity * eccentricity * Math.Sin(2 * m));

        var trueSolarTime = Mod(utc.TimeOfDay.TotalMinutes + equationOfTime + 4 * longitudeDeg, 1440);
        var hourAngle = (trueSolarTime / 4 < 0 ? trueSolarTime / 4 + 180 : trueSolarTime / 4 - 180) * DegToRad;

        var lat = latitudeDeg * DegToRad;
        var cosZenith = Math.Sin(lat) * Math.Sin(declination) + Math.Cos(lat) * Math.Cos(declination) * Math.Cos(hourAngle);
        var zenith = Math.Acos(Math.Clamp(cosZenith, -1, 1));

        var denominator = Math.Cos(lat) * Math.Sin(zenith);
        double azimuth;

        if (Math.Abs(denominator) < 1e-9)
        {
            // at a pole or with the sun straight overhead the azimuth is arbitrary
            azimuth = 180;
        }
        else
        {
            var cosAzimuth = (Math.Sin(lat) * Math.Cos(zenith) - Math.Sin(declination)) / denominator;
            var a = Math.Acos(Math.Clamp(cosAzimuth, -1, 1)) * RadToDeg;
            azimuth = hourAngle > 0 ? Mod(a + 180, 360) : Mod(540 - a, 360);
        }

        return (azimuth, 90 - zenith * RadToDeg);
    }

    /// <summary>
    /// unit vector towards the sun in <see cref="GeoOrigin"/>'s unity axes: x = east, y = up, z = north.
    /// </summary>
    public static Double3 Direction(double latitudeDeg, double longitudeDeg, DateTime utc)
    {
        var (azimuth, elevation) = Compute(latitudeDeg, longitudeDeg, utc);

        var az = azimuth * DegToRad;
        var el = elevation * DegToRad;

        return new Double3(Math.Sin(az) * Math.Cos(el), Math.Sin(el), Math.Cos(az) * Math.Cos(el));
    }

    /// <summary>
    /// local mean sidereal time in degrees: how far the stars have turned overhead.
    /// </summary>
    public static double LocalSiderealDegrees(double longitudeDeg, DateTime utc)
    {
        utc = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc;

        var daysSinceJ2000 = utc.ToOADate() + 2415018.5 - 2451545.0;
        return Mod(280.46061837 + 360.98564736629 * daysSinceJ2000 + longitudeDeg, 360);
    }

    private static double Mod(double value, double modulus) => ((value % modulus) + modulus) % modulus;
}
