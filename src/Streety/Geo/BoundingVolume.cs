using System.Text.Json;

namespace Streety.Geo;

/// <summary>
/// a 3d tiles bounding volume in ecef; boxes are oriented (centre + three half-axes).
/// </summary>
public sealed class BoundingVolume
{
    public Double3 Center { get; private init; }

    // half-axes; zero for spheres
    public Double3 AxisX { get; private init; }
    public Double3 AxisY { get; private init; }
    public Double3 AxisZ { get; private init; }

    public double Radius { get; private init; }
    public bool IsSphere { get; private init; }

    public static BoundingVolume Parse(JsonElement json, Double4x4 transform)
    {
        if (json.TryGetProperty("box", out var box))
        {
            var b = ReadDoubles(box, 12);

            return new BoundingVolume
            {
                Center = transform.MultiplyPoint(new Double3(b[0], b[1], b[2])),
                AxisX = transform.MultiplyVector(new Double3(b[3], b[4], b[5])),
                AxisY = transform.MultiplyVector(new Double3(b[6], b[7], b[8])),
                AxisZ = transform.MultiplyVector(new Double3(b[9], b[10], b[11])),
            }.WithRadius();
        }

        if (json.TryGetProperty("sphere", out var sphere))
        {
            var s = ReadDoubles(sphere, 4);

            return new BoundingVolume
            {
                Center = transform.MultiplyPoint(new Double3(s[0], s[1], s[2])),
                Radius = s[3],
                IsSphere = true,
            };
        }

        if (json.TryGetProperty("region", out var region))
            return FromRegion(ReadDoubles(region, 6));

        throw new InvalidDataException("unsupported bounding volume");
    }

    private BoundingVolume WithRadius() => new()
    {
        Center = Center,
        AxisX = AxisX,
        AxisY = AxisY,
        AxisZ = AxisZ,
        Radius = (AxisX + AxisY + AxisZ).Length,
    };

    /// <summary>
    /// region = [west, south, east, north, minHeight, maxHeight] in radians; approximated as a sphere.
    /// </summary>
    private static BoundingVolume FromRegion(double[] r)
    {
        const double rad = 180.0 / Math.PI;

        var a = Wgs84.GeodeticToEcef(r[1] * rad, r[0] * rad, r[4]);
        var b = Wgs84.GeodeticToEcef(r[3] * rad, r[2] * rad, r[5]);
        var center = Wgs84.GeodeticToEcef((r[1] + r[3]) * 0.5 * rad, (r[0] + r[2]) * 0.5 * rad, (r[4] + r[5]) * 0.5);

        return new BoundingVolume
        {
            Center = center,
            Radius = Math.Max((a - center).Length, (b - center).Length),
            IsSphere = true,
        };
    }

    /// <summary>
    /// distance from a point to the volume surface; zero when inside.
    /// </summary>
    public double DistanceTo(Double3 point)
    {
        var d = point - Center;

        if (IsSphere)
            return Math.Max(0, d.Length - Radius);

        var sq = 0.0;
        sq += AxisExcessSquared(d, AxisX);
        sq += AxisExcessSquared(d, AxisY);
        sq += AxisExcessSquared(d, AxisZ);

        return Math.Sqrt(sq);
    }

    private static double AxisExcessSquared(Double3 d, Double3 halfAxis)
    {
        var length = halfAxis.Length;

        if (length < 1e-9)
            return 0;

        var projected = Math.Abs(Double3.Dot(d, halfAxis)) / length;
        var excess = projected - length;

        return excess > 0 ? excess * excess : 0;
    }

    /// <summary>
    /// whether an infinite line (point + direction) passes through the volume.
    /// </summary>
    public bool IntersectsLine(Double3 point, Double3 direction)
    {
        var d = point - Center;

        if (IsSphere)
        {
            var dir = direction.Normalized;
            var closest = d - dir * Double3.Dot(d, dir);
            return closest.LengthSquared <= Radius * Radius;
        }

        // slab test in box space
        var tMin = double.NegativeInfinity;
        var tMax = double.PositiveInfinity;

        foreach (var axis in new[] { AxisX, AxisY, AxisZ })
        {
            var length = axis.Length;

            if (length < 1e-9)
                continue;

            var n = axis / length;
            var origin = Double3.Dot(d, n);
            var slope = Double3.Dot(direction, n);

            if (Math.Abs(slope) < 1e-12)
            {
                if (Math.Abs(origin) > length)
                    return false;

                continue;
            }

            var t0 = (-length - origin) / slope;
            var t1 = (length - origin) / slope;

            if (t0 > t1)
                (t0, t1) = (t1, t0);

            tMin = Math.Max(tMin, t0);
            tMax = Math.Min(tMax, t1);

            if (tMin > tMax)
                return false;
        }

        return true;
    }

    private static double[] ReadDoubles(JsonElement array, int count)
    {
        var values = new double[count];
        var i = 0;

        foreach (var element in array.EnumerateArray())
        {
            if (i >= count)
                break;

            values[i++] = element.GetDouble();
        }

        if (i != count)
            throw new InvalidDataException($"expected {count} values, got {i}");

        return values;
    }
}
