using System.Runtime.CompilerServices;

namespace Streety.Geo;

/// <summary>
/// double precision vector; earth-scale positions don't fit in floats.
/// </summary>
public readonly struct Double3
{
    public readonly double X, Y, Z;

    public Double3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public static readonly Double3 Zero = new(0, 0, 0);

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public double LengthSquared => X * X + Y * Y + Z * Z;
    public Double3 Normalized => this / Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Dot(Double3 a, Double3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Double3 Cross(Double3 a, Double3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public static Double3 operator +(Double3 a, Double3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Double3 operator -(Double3 a, Double3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Double3 operator -(Double3 a) => new(-a.X, -a.Y, -a.Z);
    public static Double3 operator *(Double3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Double3 operator /(Double3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    public override string ToString() => $"({X:F3}, {Y:F3}, {Z:F3})";
}
