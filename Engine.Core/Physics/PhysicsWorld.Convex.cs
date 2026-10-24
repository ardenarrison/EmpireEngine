using System.Numerics;
using System.Runtime.CompilerServices;
using Engine.Core.Assets;
using Engine.Core.Scene;

namespace Engine.Core.Physics;

// Mesh colliders on moving rigidbodies collide as the mesh's convex hull: the shape a sheet would make if shrink-wrapped
// around it. Big meshes are simplified to at most MaxHullPoints points first, so collision stays fast.
// Convex shapes (hulls, boxes, triangles) are tested against each other with the separating axis test over their
// face normals and edge pairs; contacts come from clipping the incident face against the reference face.
public sealed partial class PhysicsWorld
{
    private const int MaxHullPoints = 48;

    private static readonly ConditionalWeakTable<GlbModel, ConvexHull> Hulls = new();

    // The edges of the hull a moving mesh collider uses, in the model's own space. The editor draws them.
    public static IReadOnlyList<(Vector3 A, Vector3 B)> HullOutline(GlbModel model)
    {
        var hull = Hulls.GetValue(model, ConvexHull.Build);
        return hull.Edges.Select(e => (hull.Points[e.A], hull.Points[e.B])).ToList();
    }

    private sealed partial class Shape
    {
        public bool IsHull;
        public ConvexHull? Hull;
        public Vector3[] HullWorld = [];   // the hull's points in world space for this step

        public static Shape? FromHull(MeshCollider collider, SceneObject obj, Matrix4x4 world)
        {
            if (ModelFor(collider, obj) is not { } model) return null;
            ConvexHull hull;
            try
            {
                hull = Hulls.GetValue(model, ConvexHull.Build);
            }
            catch (Exception ex)
            {
                Warn($"'{obj.Name}': could not build a convex hull for the mesh collider: {ex.Message}");
                return null;
            }
            if (hull.Points.Length < 4 || hull.Faces.Length < 4) return null;

            var meshWorld = Matrix4x4.CreateTranslation(collider.Center) * world;
            var points = hull.Points.Select(p => Vector3.Transform(p, meshWorld)).ToArray();
            var center = points.Aggregate(Vector3.Zero, (s, p) => s + p) / points.Length;
            Decompose(world, out var scale, out var rotation, out _);

            var shape = new Shape
            {
                IsHull = true,
                Hull = hull,
                HullWorld = points,
                Center = center,
                Rotation = rotation,
                // Inertia uses the hull's box in the object's own frame, the same way a box collider does.
                HalfExtents = (hull.Max - hull.Min) * 0.5f * Vector3.Abs(scale),
                Radius = points.Max(p => Vector3.Distance(p, center)),
                Friction = Math.Clamp(collider.Friction, 0f, 2f),
                Bounciness = Math.Clamp(collider.Bounciness, 0f, 1f),
                AxisX = Vector3.Transform(Vector3.UnitX, rotation),
                AxisY = Vector3.Transform(Vector3.UnitY, rotation),
                AxisZ = Vector3.Transform(Vector3.UnitZ, rotation),
            };
            return IsFinite(center) ? shape : null;
        }

        // The mesh collider's model: its own, or the object's Mesh component's. Warns once when there is none.
        public static GlbModel? ModelFor(MeshCollider collider, SceneObject obj)
        {
            string path = collider.Model.Length > 0
                ? collider.Model
                : obj.Components.OfType<MeshRenderer>().FirstOrDefault()?.Model ?? "";
            if (path.Length == 0)
            {
                Warn($"'{obj.Name}': the mesh collider has no model. Add a Mesh component or set the collider's model.");
                return null;
            }
            if (ModelLoader is null)
            {
                Warn("Mesh colliders need PhysicsWorld.ModelLoader to be set.");
                return null;
            }
            try
            {
                return ModelLoader(path);
            }
            catch (Exception ex)
            {
                Warn($"'{obj.Name}': the mesh collider could not load {path}: {ex.Message}");
                return null;
            }
        }
    }

    // ---- convex polyhedra in world space -------------------------------------------------------

    private sealed class Poly
    {
        public required Vector3[] V;
        public required int[][] Faces;        // vertex loops, counter-clockwise seen from outside
        public required Vector3[] Normals;    // outward
        public required float[] Offsets;      // plane: dot(normal, p) = offset
        public required (int A, int B)[] Edges;
        public Vector3 Center;

        public static Poly Make(Vector3[] v, int[][] faces, (int, int)[] edges, Vector3? center = null)
        {
            var c = center ?? v.Aggregate(Vector3.Zero, (s, p) => s + p) / v.Length;
            var normals = new Vector3[faces.Length];
            var offsets = new float[faces.Length];
            for (int f = 0; f < faces.Length; f++)
            {
                // Newell's method: robust for any planar loop.
                var n = Vector3.Zero;
                var loop = faces[f];
                var mid = Vector3.Zero;
                for (int i = 0; i < loop.Length; i++)
                {
                    var p = v[loop[i]];
                    var q = v[loop[(i + 1) % loop.Length]];
                    n += new Vector3((p.Y - q.Y) * (p.Z + q.Z), (p.Z - q.Z) * (p.X + q.X), (p.X - q.X) * (p.Y + q.Y));
                    mid += p;
                }
                mid /= loop.Length;
                n = n.LengthSquared() > 1e-20f ? Vector3.Normalize(n) : Vector3.UnitY;
                if (Vector3.Dot(n, mid - c) < 0f) n = -n;
                normals[f] = n;
                offsets[f] = Vector3.Dot(n, mid);
            }
            return new Poly { V = v, Faces = faces, Normals = normals, Offsets = offsets, Edges = edges, Center = c };
        }

        public static Poly FromHull(Shape s) => Make(s.HullWorld, s.Hull!.Faces, s.Hull.Edges, s.Center);

        private static readonly int[][] BoxFaces =
        {
            [0, 2, 6, 4], [1, 5, 7, 3],   // -x, +x
            [0, 4, 5, 1], [2, 3, 7, 6],   // -y, +y
            [0, 1, 3, 2], [4, 6, 7, 5],   // -z, +z
        };
        private static readonly (int, int)[] BoxEdges =
        {
            (0, 1), (2, 3), (4, 5), (6, 7), (0, 2), (1, 3), (4, 6), (5, 7), (0, 4), (1, 5), (2, 6), (3, 7),
        };

        public static Poly FromBox(Shape s)
        {
            var v = new Vector3[8];
            for (int i = 0; i < 8; i++)
                v[i] = s.Center +
                       s.AxisX * ((i & 1) == 0 ? -s.HalfExtents.X : s.HalfExtents.X) +
                       s.AxisY * ((i & 2) == 0 ? -s.HalfExtents.Y : s.HalfExtents.Y) +
                       s.AxisZ * ((i & 4) == 0 ? -s.HalfExtents.Z : s.HalfExtents.Z);
            return Make(v, BoxFaces, BoxEdges, s.Center);
        }

        // A triangle as a flat solid with a face on each side.
        public static Poly FromTriangle(Vector3 a, Vector3 b, Vector3 c)
        {
            var v = new[] { a, b, c };
            var center = (a + b + c) / 3f;
            var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            var normals = new[] { n, -n };
            float d = Vector3.Dot(n, a);
            return new Poly
            {
                V = v,
                Faces = [[0, 1, 2], [0, 2, 1]],
                Normals = normals,
                Offsets = [d, -d],
                Edges = [(0, 1), (1, 2), (2, 0)],
                Center = center,
            };
        }

        public (float Min, float Max) Project(Vector3 axis)
        {
            float min = float.MaxValue, max = float.MinValue;
            foreach (var p in V)
            {
                float d = Vector3.Dot(p, axis);
                if (d < min) min = d;
                if (d > max) max = d;
            }
            return (min, max);
        }
    }

    // Separating axis test between two convex polyhedra. Contact normals point from a toward b.
    private static void PolyPoly(Poly a, Poly b, AddContact add)
    {
        // Face axes: how far b's deepest point sits in front of each face of a (positive means separated), and back.
        float bestA = float.MinValue, bestB = float.MinValue;
        int faceA = -1, faceB = -1;
        for (int f = 0; f < a.Faces.Length; f++)
        {
            float sep = b.Project(a.Normals[f]).Min - a.Offsets[f];
            if (sep > 0f) return;
            if (sep > bestA) { bestA = sep; faceA = f; }
        }
        for (int f = 0; f < b.Faces.Length; f++)
        {
            float sep = a.Project(b.Normals[f]).Min - b.Offsets[f];
            if (sep > 0f) return;
            if (sep > bestB) { bestB = sep; faceB = f; }
        }

        float bestEdge = float.MinValue;
        int edgeA = -1, edgeB = -1;
        Vector3 edgeAxis = default;
        var towardB = b.Center - a.Center;
        for (int i = 0; i < a.Edges.Length; i++)
        {
            var da = a.V[a.Edges[i].B] - a.V[a.Edges[i].A];
            for (int j = 0; j < b.Edges.Length; j++)
            {
                var axis = Vector3.Cross(da, b.V[b.Edges[j].B] - b.V[b.Edges[j].A]);
                float length = axis.Length();
                if (length < 1e-6f) continue;
                axis /= length;
                if (Vector3.Dot(axis, towardB) < 0f) axis = -axis;
                float sep = b.Project(axis).Min - a.Project(axis).Max;
                if (sep > 0f) return;
                if (sep > bestEdge) { bestEdge = sep; edgeA = i; edgeB = j; edgeAxis = axis; }
            }
        }

        // The shallowest axis wins; faces are favored so resting contacts stay stable.
        float bestFace = MathF.Max(bestA, bestB);
        if (edgeA >= 0 && -bestEdge < -bestFace * 0.95f - 0.01f)
        {
            PolyEdgeContact(a, b, edgeA, edgeB, edgeAxis, -bestEdge, add);
            return;
        }
        if (bestB > bestA * 0.95f + 0.01f)
            PolyFaceContacts(b, faceB, a, add, flip: true);
        else
            PolyFaceContacts(a, faceA, b, add, flip: false);
    }

    // The reference face's polygon clips the incident polyhedron's most opposed face; points behind it are contacts.
    private static void PolyFaceContacts(Poly reference, int face, Poly incident, AddContact add, bool flip)
    {
        var n = reference.Normals[face];
        int incFace = 0;
        float most = float.MaxValue;
        for (int f = 0; f < incident.Faces.Length; f++)
        {
            float d = Vector3.Dot(incident.Normals[f], n);
            if (d < most) { most = d; incFace = f; }
        }
        var polygon = incident.Faces[incFace].Select(i => incident.V[i]).ToList();

        var loop = reference.Faces[face];
        var faceCenter = loop.Aggregate(Vector3.Zero, (s, i) => s + reference.V[i]) / loop.Length;
        for (int e = 0; e < loop.Length && polygon.Count > 0; e++)
        {
            var p0 = reference.V[loop[e]];
            var p1 = reference.V[loop[(e + 1) % loop.Length]];
            var side = Vector3.Cross(p1 - p0, n);
            if (side.LengthSquared() < 1e-20f) continue;
            side = Vector3.Normalize(side);
            if (Vector3.Dot(side, faceCenter - p0) > 0f) side = -side;   // point outward
            polygon = Clip(polygon, side, Vector3.Dot(side, p0));
        }

        var points = new List<(Vector3 Point, float Depth)>();
        foreach (var p in polygon)
        {
            float separation = Vector3.Dot(n, p) - reference.Offsets[face];
            if (separation <= 0f)
                points.Add((p - n * (separation * 0.5f), -separation));
        }
        foreach (var (point, depth) in Reduce(points))
            add(point, flip ? -n : n, depth);
    }

    private static void PolyEdgeContact(Poly a, Poly b, int edgeA, int edgeB, Vector3 n, float depth, AddContact add)
    {
        // The tested edges set the directions; the contact uses the parallel edges that actually face each other.
        var ea = SupportEdge(a, a.V[a.Edges[edgeA].B] - a.V[a.Edges[edgeA].A], n);
        var eb = SupportEdge(b, b.V[b.Edges[edgeB].B] - b.V[b.Edges[edgeB].A], -n);
        ClosestOnSegments(ea.P, ea.Q, eb.P, eb.Q, out var onA, out var onB);
        add((onA + onB) * 0.5f, n, depth);
    }

    private static (Vector3 P, Vector3 Q) SupportEdge(Poly poly, Vector3 direction, Vector3 toward)
    {
        direction = Vector3.Normalize(direction);
        (Vector3, Vector3) best = default;
        float bestDot = float.MinValue;
        foreach (var (i, j) in poly.Edges)
        {
            var p = poly.V[i];
            var q = poly.V[j];
            var d = q - p;
            float length = d.Length();
            if (length < 1e-9f || MathF.Abs(Vector3.Dot(d / length, direction)) < 0.999f) continue;
            float along = Vector3.Dot((p + q) * 0.5f, toward);
            if (along > bestDot) { bestDot = along; best = (p, q); }
        }
        return best;
    }

    // Sphere against a convex polyhedron. The normal points from the polyhedron toward the sphere.
    private static void SpherePoly(Shape sphere, Poly poly, AddContact add)
    {
        var c = sphere.Center;
        float maxDist = float.MinValue;
        int maxFace = 0;
        for (int f = 0; f < poly.Faces.Length; f++)
        {
            float d = Vector3.Dot(poly.Normals[f], c) - poly.Offsets[f];
            if (d > sphere.Radius) return;   // fully in front of one face: no touch
            if (d > maxDist) { maxDist = d; maxFace = f; }
        }

        if (maxDist <= 0f)
        {
            // The center is inside: push out through the nearest face.
            var n = poly.Normals[maxFace];
            add(c - n * ((maxDist + sphere.Radius) * 0.5f), n, sphere.Radius - maxDist);
            return;
        }

        // Outside: the closest point on the surface lies on one of the faces the center is in front of.
        Vector3 closest = default;
        float best = float.MaxValue;
        for (int f = 0; f < poly.Faces.Length; f++)
        {
            float d = Vector3.Dot(poly.Normals[f], c) - poly.Offsets[f];
            if (d <= 0f) continue;
            var q = ClosestOnFace(poly, f, c - poly.Normals[f] * d);
            float dist = Vector3.DistanceSquared(q, c);
            if (dist < best) { best = dist; closest = q; }
        }
        float distance = MathF.Sqrt(best);
        if (distance >= sphere.Radius || distance < 1e-7f) return;
        var normal = (c - closest) / distance;
        add((closest + c - normal * sphere.Radius) * 0.5f, normal, sphere.Radius - distance);
    }

    // The closest point of a face polygon to a point already projected onto the face's plane.
    private static Vector3 ClosestOnFace(Poly poly, int face, Vector3 onPlane)
    {
        var loop = poly.Faces[face];
        var n = poly.Normals[face];
        var faceCenter = loop.Aggregate(Vector3.Zero, (s, i) => s + poly.V[i]) / loop.Length;
        bool inside = true;
        Vector3 best = onPlane;
        float bestDist = float.MaxValue;
        for (int e = 0; e < loop.Length; e++)
        {
            var p0 = poly.V[loop[e]];
            var p1 = poly.V[loop[(e + 1) % loop.Length]];
            var side = Vector3.Cross(p1 - p0, n);
            if (Vector3.Dot(side, faceCenter - p0) > 0f) side = -side;   // point out of the face, either winding
            if (Vector3.Dot(side, onPlane - p0) > 0f)
                inside = false;
            ClosestOnSegments(onPlane, onPlane, p0, p1, out _, out var q);
            float dist = Vector3.DistanceSquared(q, onPlane);
            if (dist < bestDist) { bestDist = dist; best = q; }
        }
        return inside ? onPlane : best;
    }

    // ---- hull building ------------------------------------------------------------------------

    // A convex hull in the model's own space: its corner points, faces as point loops, and edges.
    private sealed class ConvexHull
    {
        public Vector3[] Points = [];
        public int[][] Faces = [];
        public (int A, int B)[] Edges = [];
        public Vector3 Min, Max;

        public static ConvexHull Build(GlbModel model)
        {
            var all = new List<Vector3>();
            foreach (var mesh in model.Meshes)
                for (int i = 0; i + 2 < mesh.Positions.Length; i += 3)
                    all.Add(new Vector3(mesh.Positions[i], mesh.Positions[i + 1], mesh.Positions[i + 2]));
            if (all.Count == 0) return new ConvexHull();

            var min = all.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            var max = all.Aggregate(new Vector3(float.MinValue), Vector3.Max);
            float size = MathF.Max(Vector3.Distance(min, max), 1e-6f);
            float eps = size * 1e-4f;

            var points = Distinct(all, eps * 10f);
            if (points.Count > MaxHullPoints)
                points = Distinct(SupportPoints(points, MaxHullPoints), eps * 10f);

            // A flat mesh (a plane, a card) gets a thin slab, so it still has volume to collide with.
            if (IsFlat(points, eps, out var flatNormal))
            {
                float half = MathF.Max(size * 0.01f, 0.005f);
                points = points.Select(p => p + flatNormal * half).Concat(points.Select(p => p - flatNormal * half)).ToList();
            }

            return FromPoints(points, eps);
        }

        // Every face plane is one that some three points span with every other point behind it.
        // Fine for the at most 2 * MaxHullPoints points a hull is built from.
        private static ConvexHull FromPoints(List<Vector3> points, float eps)
        {
            var center = points.Aggregate(Vector3.Zero, (s, p) => s + p) / points.Count;
            var planes = new List<(Vector3 N, float D)>();
            int count = points.Count;
            for (int i = 0; i < count; i++)
                for (int j = i + 1; j < count; j++)
                    for (int k = j + 1; k < count; k++)
                    {
                        var n = Vector3.Cross(points[j] - points[i], points[k] - points[i]);
                        float length = n.Length();
                        if (length < eps * eps) continue;
                        n /= length;
                        if (Vector3.Dot(n, points[i] - center) < 0f) n = -n;
                        float d = Vector3.Dot(n, points[i]);
                        if (points.Any(p => Vector3.Dot(n, p) - d > eps)) continue;
                        if (planes.Any(pl => Vector3.Dot(pl.N, n) > 0.9999f && MathF.Abs(pl.D - d) < eps * 2f)) continue;
                        planes.Add((n, d));
                    }

            // Each face is the loop of points on its plane, ordered around their middle.
            var faces = new List<int[]>();
            var used = new HashSet<int>();
            foreach (var (n, d) in planes)
            {
                var on = Enumerable.Range(0, count).Where(i => MathF.Abs(Vector3.Dot(n, points[i]) - d) <= eps * 2f).ToList();
                if (on.Count < 3) continue;
                var mid = on.Aggregate(Vector3.Zero, (s, i) => s + points[i]) / on.Count;
                var u = Vector3.Normalize(points[on[0]] - mid);
                var v = Vector3.Cross(n, u);
                var loop = on.OrderBy(i => MathF.Atan2(Vector3.Dot(points[i] - mid, v), Vector3.Dot(points[i] - mid, u)))
                             .ToList();
                // Drop points that sit on a straight run of the loop; they add edges but no shape.
                loop = loop.Where((p, idx) =>
                {
                    var prev = points[loop[(idx + loop.Count - 1) % loop.Count]];
                    var next = points[loop[(idx + 1) % loop.Count]];
                    return Vector3.Cross(points[p] - prev, next - points[p]).Length() > eps * eps;
                }).ToList();
                if (loop.Count < 3) continue;
                faces.Add(loop.ToArray());
                foreach (var i in loop) used.Add(i);
            }

            // Keep only corner points, renumbered.
            var remap = new Dictionary<int, int>();
            var corners = new List<Vector3>();
            foreach (var i in used.OrderBy(i => i))
            {
                remap[i] = corners.Count;
                corners.Add(points[i]);
            }
            var faceLoops = faces.Select(f => f.Select(i => remap[i]).ToArray()).ToArray();
            var edges = new HashSet<(int, int)>();
            foreach (var loop in faceLoops)
                for (int e = 0; e < loop.Length; e++)
                {
                    int a = loop[e], b = loop[(e + 1) % loop.Length];
                    edges.Add(a < b ? (a, b) : (b, a));
                }

            return new ConvexHull
            {
                Points = corners.ToArray(),
                Faces = faceLoops,
                Edges = edges.ToArray(),
                Min = corners.Count > 0 ? corners.Aggregate(new Vector3(float.MaxValue), Vector3.Min) : Vector3.Zero,
                Max = corners.Count > 0 ? corners.Aggregate(new Vector3(float.MinValue), Vector3.Max) : Vector3.Zero,
            };
        }

        private static List<Vector3> Distinct(IEnumerable<Vector3> points, float tolerance)
        {
            var result = new List<Vector3>();
            float t2 = tolerance * tolerance;
            foreach (var p in points)
                if (!result.Any(q => Vector3.DistanceSquared(p, q) < t2))
                    result.Add(p);
            return result;
        }

        // The farthest point in each of count directions spread evenly over a sphere: a simplified hull's corners.
        private static IEnumerable<Vector3> SupportPoints(List<Vector3> points, int count)
        {
            var center = points.Aggregate(Vector3.Zero, (s, p) => s + p) / points.Count;
            float golden = MathF.PI * (3f - MathF.Sqrt(5f));
            for (int i = 0; i < count; i++)
            {
                float y = 1f - 2f * (i + 0.5f) / count;
                float r = MathF.Sqrt(1f - y * y);
                var dir = new Vector3(MathF.Cos(golden * i) * r, y, MathF.Sin(golden * i) * r);
                yield return points.MaxBy(p => Vector3.Dot(p - center, dir));
            }
        }

        private static bool IsFlat(List<Vector3> points, float eps, out Vector3 normal)
        {
            normal = Vector3.UnitY;
            if (points.Count < 3) return true;
            var a = points[0];
            var b = points.MaxBy(p => Vector3.DistanceSquared(p, a));
            var c = points.MaxBy(p => Vector3.Cross(b - a, p - a).LengthSquared());
            var n = Vector3.Cross(b - a, c - a);
            if (n.LengthSquared() < 1e-20f) return true;   // all on a line; any normal will do
            normal = Vector3.Normalize(n);
            var nn = normal;
            return points.All(p => MathF.Abs(Vector3.Dot(nn, p - a)) <= eps);
        }
    }
}
