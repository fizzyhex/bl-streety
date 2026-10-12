namespace Streety.Geo;

/// <summary>
/// column-major affine matrix in doubles (same element order as gltf / 3d tiles).
/// </summary>
public sealed class Double4x4
{
    // m[column * 4 + row]
    public readonly double[] M = new double[16];

    public static Double4x4 Identity
    {
        get
        {
            var m = new Double4x4();
            m.M[0] = m.M[5] = m.M[10] = m.M[15] = 1;
            return m;
        }
    }

    public double this[int row, int column]
    {
        get => M[column * 4 + row];
        set => M[column * 4 + row] = value;
    }

    public static Double4x4 FromColumnMajor(IReadOnlyList<double> values)
    {
        var m = new Double4x4();

        for (var i = 0; i < 16; i++)
            m.M[i] = values[i];

        return m;
    }

    public static Double4x4 FromTranslation(Double3 t)
    {
        var m = Identity;
        m[0, 3] = t.X;
        m[1, 3] = t.Y;
        m[2, 3] = t.Z;
        return m;
    }

    /// <summary>
    /// builds a matrix from three basis columns and a translation.
    /// </summary>
    public static Double4x4 FromBasis(Double3 x, Double3 y, Double3 z, Double3 t)
    {
        var m = Identity;
        m[0, 0] = x.X; m[1, 0] = x.Y; m[2, 0] = x.Z;
        m[0, 1] = y.X; m[1, 1] = y.Y; m[2, 1] = y.Z;
        m[0, 2] = z.X; m[1, 2] = z.Y; m[2, 2] = z.Z;
        m[0, 3] = t.X; m[1, 3] = t.Y; m[2, 3] = t.Z;
        return m;
    }

    /// <summary>
    /// gltf quaternion (x, y, z, w) + translation + scale.
    /// </summary>
    public static Double4x4 FromTrs(Double3 t, double qx, double qy, double qz, double qw, Double3 s)
    {
        var m = Identity;
        double xx = qx * qx, yy = qy * qy, zz = qz * qz;
        double xy = qx * qy, xz = qx * qz, yz = qy * qz;
        double wx = qw * qx, wy = qw * qy, wz = qw * qz;

        m[0, 0] = (1 - 2 * (yy + zz)) * s.X;
        m[1, 0] = 2 * (xy + wz) * s.X;
        m[2, 0] = 2 * (xz - wy) * s.X;

        m[0, 1] = 2 * (xy - wz) * s.Y;
        m[1, 1] = (1 - 2 * (xx + zz)) * s.Y;
        m[2, 1] = 2 * (yz + wx) * s.Y;

        m[0, 2] = 2 * (xz + wy) * s.Z;
        m[1, 2] = 2 * (yz - wx) * s.Z;
        m[2, 2] = (1 - 2 * (xx + yy)) * s.Z;

        m[0, 3] = t.X;
        m[1, 3] = t.Y;
        m[2, 3] = t.Z;
        return m;
    }

    public static Double4x4 operator *(Double4x4 a, Double4x4 b)
    {
        var r = new Double4x4();

        for (var c = 0; c < 4; c++)
        for (var row = 0; row < 4; row++)
        {
            double sum = 0;

            for (var k = 0; k < 4; k++)
                sum += a[row, k] * b[k, c];

            r[row, c] = sum;
        }

        return r;
    }

    public Double3 MultiplyPoint(Double3 p) => new(
        this[0, 0] * p.X + this[0, 1] * p.Y + this[0, 2] * p.Z + this[0, 3],
        this[1, 0] * p.X + this[1, 1] * p.Y + this[1, 2] * p.Z + this[1, 3],
        this[2, 0] * p.X + this[2, 1] * p.Y + this[2, 2] * p.Z + this[2, 3]);

    public Double3 MultiplyVector(Double3 v) => new(
        this[0, 0] * v.X + this[0, 1] * v.Y + this[0, 2] * v.Z,
        this[1, 0] * v.X + this[1, 1] * v.Y + this[1, 2] * v.Z,
        this[2, 0] * v.X + this[2, 1] * v.Y + this[2, 2] * v.Z);

    public Double3 Column(int c) => new(this[0, c], this[1, c], this[2, c]);
    public Double3 Translation => Column(3);
}
