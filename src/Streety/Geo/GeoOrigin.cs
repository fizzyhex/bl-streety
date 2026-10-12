using UnityEngine;

namespace Streety.Geo;

/// <summary>
/// a local tangent frame on the globe. ecef is mapped into unity as x = east, y = up, z = north.
/// </summary>
/// <remarks>
/// east/up/north is a mirror of east/north/up, so meshes are uploaded with local z flipped and their
/// winding reversed (see <see cref="Gltf.GlbReader"/>); that keeps every tile transform a pure rotation.
/// </remarks>
public sealed class GeoOrigin
{
    public double Latitude { get; }
    public double Longitude { get; }
    public double Height { get; }
    public Double3 Ecef { get; }

    private readonly Double3 _east, _north, _up;

    public GeoOrigin(double latitude, double longitude, double height)
    {
        Latitude = latitude;
        Longitude = longitude;
        Height = height;
        Ecef = Wgs84.GeodeticToEcef(latitude, longitude, height);

        (_east, _north, _up) = Wgs84.EnuBasis(latitude, longitude);
    }

    public static GeoOrigin FromEcef(Double3 ecef)
    {
        var (lat, lon, h) = Wgs84.EcefToGeodetic(ecef);
        return new GeoOrigin(lat, lon, h);
    }

    /// <summary>
    /// ecef direction -> unity direction, still in doubles.
    /// </summary>
    public Double3 EcefDirectionToLocal(Double3 v) =>
        new(Double3.Dot(v, _east), Double3.Dot(v, _up), Double3.Dot(v, _north));

    public Double3 EcefToLocal(Double3 p) => EcefDirectionToLocal(p - Ecef);

    public Double3 LocalToEcef(Double3 local) =>
        Ecef + _east * local.X + _up * local.Y + _north * local.Z;

    public Vector3 EcefToUnity(Double3 p)
    {
        var l = EcefToLocal(p);
        return new Vector3((float)l.X, (float)l.Y, (float)l.Z);
    }

    public Double3 UnityToEcef(Vector3 p) => LocalToEcef(new Double3(p.x, p.y, p.z));

    /// <summary>
    /// converts an ecef-space model matrix into a unity pose. pair with <see cref="MirrorZ"/> on the mesh data.
    /// </summary>
    public (Vector3 position, Quaternion rotation, Vector3 scale) ModelMatrixToUnity(Double4x4 model)
    {
        // linear part in unity axes, then undo the mirror by flipping the local z column
        var x = EcefDirectionToLocal(model.Column(0));
        var y = EcefDirectionToLocal(model.Column(1));
        var z = -EcefDirectionToLocal(model.Column(2));

        var scale = new Vector3((float)x.Length, (float)y.Length, (float)z.Length);

        var forward = z.Normalized;
        var up = y.Normalized;

        var rotation = Quaternion.LookRotation(
            new Vector3((float)forward.X, (float)forward.Y, (float)forward.Z),
            new Vector3((float)up.X, (float)up.Y, (float)up.Z));

        return (EcefToUnity(model.Translation), rotation, scale);
    }

    public override string ToString() => $"({Latitude:F6}, {Longitude:F6}, {Height:F1}m)";
}
