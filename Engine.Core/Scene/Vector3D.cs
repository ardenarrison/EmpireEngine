using System.Globalization;
using System.Numerics;
using System.Text.Json.Serialization;

namespace Engine.Core.Scene;

// How an object's position is stored, chosen per project in File > Project Settings. Float is the usual 32-bit precision, about
// seven digits: fine for most games, but a position far from the origin (say 100,000 units) can only move in steps of a hair
// under a hundredth. Double keeps about sixteen digits, so very large worlds and tiny movements added up over time stay exact.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PositionType
{
    Float,
    Double,
}

// A position (or any offset) with double precision. Scripts read and write it through sceneObject.transform.positionD.
// It converts to and from Vector3 so the two mix freely: a Vector3 becomes a Vector3D on its own, and (Vector3)v goes back.
public readonly record struct Vector3D(double X, double Y, double Z)
{
    public static readonly Vector3D Zero = new(0, 0, 0);
    public static readonly Vector3D One = new(1, 1, 1);

    public Vector3D(Vector3 v) : this(v.X, v.Y, v.Z) { }

    public double LengthSquared => X * X + Y * Y + Z * Z;
    public double Length => Math.Sqrt(LengthSquared);

    public static double Dot(Vector3D a, Vector3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static double Distance(Vector3D a, Vector3D b) => (a - b).Length;
    public static Vector3D Lerp(Vector3D a, Vector3D b, double t) => a + (b - a) * t;

    public static Vector3D Cross(Vector3D a, Vector3D b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    // The same direction with a length of 1, or zero for a zero vector.
    public static Vector3D Normalize(Vector3D v)
    {
        double length = v.Length;
        return length == 0 ? Zero : v / length;
    }

    // Each part rounded to the nearest float, which is what a Float project keeps.
    public Vector3D ToFloatPrecision() => new((float)X, (float)Y, (float)Z);

    // v turned by q, in double precision.
    public static Vector3D Rotate(Vector3D v, Quaternion q)
    {
        var u = new Vector3D(q.X, q.Y, q.Z);
        var t = Cross(u, v) * 2.0;
        return v + t * q.W + Cross(u, t);
    }

    public static implicit operator Vector3D(Vector3 v) => new(v);
    public static explicit operator Vector3(Vector3D v) => new((float)v.X, (float)v.Y, (float)v.Z);

    public static Vector3D operator +(Vector3D a, Vector3D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3D operator -(Vector3D a, Vector3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3D operator -(Vector3D a) => new(-a.X, -a.Y, -a.Z);
    public static Vector3D operator *(Vector3D a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vector3D operator *(double s, Vector3D a) => a * s;
    public static Vector3D operator *(Vector3D a, Vector3D b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
    public static Vector3D operator /(Vector3D a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"<{X}, {Y}, {Z}>");
}
