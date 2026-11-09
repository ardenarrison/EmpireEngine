using System.Numerics;
using System.Runtime.CompilerServices;
using Engine.Core.Scene;

namespace Engine.Core.Physics;

// A polygon collider collides as the convex pieces of its outline, each a hull reaching Depth units in Z, all on the same body.
// Cutting an outline up is slow next to a physics step, so the pieces are kept until its points change.
public sealed partial class PhysicsWorld
{
    private sealed class PolygonPieces
    {
        public Vector2[] Source = [];
        public List<Vector2[]> Pieces = new();
        public List<ConvexHull> Prisms = new();
        public Vector2 Half;   // half the whole outline's size
    }

    private static readonly ConditionalWeakTable<PolygonCollider2D, PolygonPieces> Polygons = new();

    private static PolygonPieces PiecesOf(PolygonCollider2D polygon)
    {
        var known = Polygons.GetOrCreateValue(polygon);
        if (known.Source.AsSpan().SequenceEqual(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(polygon.Points)) && known.Source.Length > 0)
            return known;

        known.Source = polygon.Points.ToArray();
        known.Pieces = Polygon2D.ConvexPieces(polygon.Points);
        known.Prisms = known.Pieces.Select(piece => Prism(piece.Length)).ToList();
        if (known.Source.Length > 0)
            known.Half = (known.Source.Aggregate(Vector2.Max) - known.Source.Aggregate(Vector2.Min)) / 2f;
        return known;
    }

    // The faces and edges of a shape with n corners at the back (points 0 to n - 1) and the same n at the front.
    private static ConvexHull Prism(int n)
    {
        var faces = new List<int[]> { Enumerable.Range(n, n).ToArray(), Enumerable.Range(0, n).Reverse().ToArray() };
        var edges = new List<(int, int)>();
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            faces.Add([i, next, n + next, n + i]);
            edges.Add((i, next));
            edges.Add((n + i, n + next));
            edges.Add((i, n + i));
        }
        return new ConvexHull { Faces = faces.ToArray(), Edges = edges.ToArray() };
    }

    private sealed partial class Shape
    {
        public static List<Shape> FromPolygon(PolygonCollider2D polygon, SceneObject obj, Matrix4x4 world)
        {
            var shapes = new List<Shape>();
            var known = PiecesOf(polygon);
            if (known.Pieces.Count == 0)
            {
                if (Polygon2D.Problem(polygon.Points) is { } problem) Warn($"'{obj.Name}': the polygon collider doesn't collide. {problem}");
                return shapes;
            }

            Decompose(world, out var scale, out var rotation, out _);
            scale = Vector3.Abs(scale);
            var axisZ = Vector3.Transform(Vector3.UnitZ, rotation);
            var half = axisZ * (MathF.Max(MathF.Abs(polygon.Depth), 0.01f) * 0.5f);
            var local = Matrix4x4.CreateTranslation(polygon.Center) * world;

            for (int p = 0; p < known.Pieces.Count; p++)
            {
                var piece = known.Pieces[p];
                int n = piece.Length;
                var corners = piece.Select(c => Vector3.Transform(new Vector3(c, 0f), local)).ToArray();
                // A mirrored object turns the outline around, and faces go counter-clockwise seen from outside.
                if (Vector3.Dot(Vector3.Cross(corners[1] - corners[0], corners[2] - corners[1]), axisZ) < 0f) Array.Reverse(corners);

                var points = new Vector3[n * 2];
                for (int i = 0; i < n; i++)
                {
                    points[i] = corners[i] - half;
                    points[n + i] = corners[i] + half;
                }
                var center = corners.Aggregate(Vector3.Zero, (s, c) => s + c) / n;
                var shape = new Shape
                {
                    IsHull = true,
                    Hull = known.Prisms[p],
                    HullWorld = points,
                    Center = center,
                    Rotation = rotation,
                    // A body's inertia comes from its first shape, so each piece gives the whole outline's size.
                    HalfExtents = new Vector3(known.Half.X * scale.X, known.Half.Y * scale.Y, half.Length()),
                    Radius = points.Max(point => Vector3.Distance(point, center)),
                    Friction = Math.Clamp(polygon.Friction, 0f, 2f),
                    Bounciness = Math.Clamp(polygon.Bounciness, 0f, 1f),
                    AxisX = Vector3.Transform(Vector3.UnitX, rotation),
                    AxisY = Vector3.Transform(Vector3.UnitY, rotation),
                    AxisZ = axisZ,
                    IsTrigger = polygon.IsTrigger,
                };
                if (IsFinite(center)) shapes.Add(shape);
            }
            return shapes;
        }
    }

    // Where a ray enters a convex shape. A ray that starts inside it doesn't hit, as with the other shapes that have faces.
    private static bool RayPoly(Poly poly, Vector3 origin, Vector3 dir, out float t, out Vector3 normal)
    {
        t = 0f;
        normal = default;
        float exit = float.MaxValue;
        for (int f = 0; f < poly.Faces.Length; f++)
        {
            float towards = Vector3.Dot(poly.Normals[f], dir);
            float distance = poly.Offsets[f] - Vector3.Dot(poly.Normals[f], origin);
            if (MathF.Abs(towards) < 1e-9f)
            {
                if (distance < 0f) return false;   // running alongside this face, outside it
                continue;
            }
            float at = distance / towards;
            if (towards < 0f)
            {
                if (at > t)
                {
                    t = at;
                    normal = poly.Normals[f];
                }
            }
            else exit = MathF.Min(exit, at);
            if (t > exit) return false;
        }
        return normal != default;
    }
}
