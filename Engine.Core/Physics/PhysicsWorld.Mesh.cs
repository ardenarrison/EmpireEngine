using System.Numerics;
using System.Runtime.CompilerServices;
using Engine.Core.Assets;
using Engine.Core.Scene;

namespace Engine.Core.Physics;

// Mesh colliders: a model's triangles, kept in a bounding volume tree so a body only tests the triangles near it.
// Spheres test against each triangle's closest point; boxes use the separating axis test with face clipping.
public sealed partial class PhysicsWorld
{
    private const int LeafTriangles = 4;

    // Loads a model for mesh colliders, by path relative to the content folder. The editor and the player set it.
    public static Func<string, GlbModel>? ModelLoader { get; set; }

    // One tree per loaded model. A reloaded model is a new instance, so it gets a new tree.
    private static readonly ConditionalWeakTable<GlbModel, TriangleTree> Trees = new();
    private static readonly HashSet<string> Warned = new();

    // Prints a warning once, so a problem that repeats every step does not flood the console.
    private static void Warn(string message)
    {
        lock (Warned)
            if (Warned.Add(message))
                Console.WriteLine(message);
    }

    private sealed partial class Shape
    {
        public bool IsMesh;
        public TriangleTree? Tree;
        public Matrix4x4 MeshWorld;      // mesh space to world, including the collider's Center offset
        public Matrix4x4 MeshToLocal;    // world to mesh space
        public float MeshRadius;

        public static Shape? FromMesh(MeshCollider collider, SceneObject obj, Matrix4x4 world)
        {
            if (ModelFor(collider, obj) is not { } model) return null;
            return FromModel(model, collider, world);
        }

        // A terrain's ground, as a static mesh shape.
        public static Shape? FromTerrain(Terrain terrain, Matrix4x4 world) => FromModel(terrain.CollisionModel, terrain, world);

        private static Shape? FromModel(GlbModel model, Collider collider, Matrix4x4 world)
        {
            var tree = Trees.GetValue(model, TriangleTree.Build);
            if (tree.Count == 0) return null;

            var meshWorld = Matrix4x4.CreateTranslation(collider.Center) * world;
            if (!Matrix4x4.Invert(meshWorld, out var toLocal)) return null;

            var center = Vector3.Transform((tree.Min + tree.Max) / 2f, meshWorld);
            float radius = 0f;
            foreach (var corner in Corners(tree.Min, tree.Max))
                radius = MathF.Max(radius, Vector3.Distance(Vector3.Transform(corner, meshWorld), center));

            return new Shape
            {
                IsMesh = true,
                Tree = tree,
                MeshWorld = meshWorld,
                MeshToLocal = toLocal,
                MeshRadius = radius,
                Center = center,
                Rotation = Quaternion.Identity,
                Friction = Math.Clamp(collider.Friction, 0f, 2f),
                Bounciness = Math.Clamp(collider.Bounciness, 0f, 1f),
            };
        }
    }

    // ---- narrow phase against triangles ----------------------------------------------------
    // Normals point from the mesh toward the other shape.

    private static void MeshContacts(Shape mesh, Shape other, AddContact add)
    {
        // The other shape's world box, carried into mesh space to find the triangles it could touch.
        var extent = other.IsHull
            ? other.HullWorld.Aggregate(Vector3.Zero, (e, p) => Vector3.Max(e, Vector3.Abs(p - other.Center)))
            : other.IsBox
            ? new Vector3(
                other.HalfExtents.X * MathF.Abs(other.AxisX.X) + other.HalfExtents.Y * MathF.Abs(other.AxisY.X) + other.HalfExtents.Z * MathF.Abs(other.AxisZ.X),
                other.HalfExtents.X * MathF.Abs(other.AxisX.Y) + other.HalfExtents.Y * MathF.Abs(other.AxisY.Y) + other.HalfExtents.Z * MathF.Abs(other.AxisZ.Y),
                other.HalfExtents.X * MathF.Abs(other.AxisX.Z) + other.HalfExtents.Y * MathF.Abs(other.AxisY.Z) + other.HalfExtents.Z * MathF.Abs(other.AxisZ.Z))
            : new Vector3(other.Radius);
        var localMin = new Vector3(float.MaxValue);
        var localMax = new Vector3(float.MinValue);
        foreach (var corner in Corners(other.Center - extent, other.Center + extent))
        {
            var p = Vector3.Transform(corner, mesh.MeshToLocal);
            localMin = Vector3.Min(localMin, p);
            localMax = Vector3.Max(localMax, p);
        }

        var hits = new List<int>();
        mesh.Tree!.Query(localMin, localMax, hits);
        var hull = other.IsHull ? Poly.FromHull(other) : null;   // built once, shared by every triangle
        foreach (int tri in hits)
        {
            var a = Vector3.Transform(mesh.Tree.Vertices[tri * 3], mesh.MeshWorld);
            var b = Vector3.Transform(mesh.Tree.Vertices[tri * 3 + 1], mesh.MeshWorld);
            var c = Vector3.Transform(mesh.Tree.Vertices[tri * 3 + 2], mesh.MeshWorld);
            if (other.IsHull) PolyPoly(Poly.FromTriangle(a, b, c), hull!, add);
            else if (other.IsBox) BoxTriangle(other, a, b, c, add);
            else SphereTriangle(other, a, b, c, add);
        }
    }

    private static void SphereTriangle(Shape sphere, Vector3 a, Vector3 b, Vector3 c, AddContact add)
    {
        var q = ClosestOnTriangle(sphere.Center, a, b, c);
        var d = sphere.Center - q;
        float dist = d.Length();
        if (dist >= sphere.Radius) return;

        Vector3 n;
        if (dist > 1e-6f)
            n = d / dist;
        else
        {
            // The center lies on the triangle: push out along the face normal.
            n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            if (!IsFinite(n)) return;
        }
        float depth = sphere.Radius - dist;
        add((q + sphere.Center - n * sphere.Radius) * 0.5f, n, depth);
    }

    // Separating axis test between a box and one triangle: the face normal, the box's three axes, and nine edge pairs.
    private static void BoxTriangle(Shape box, Vector3 a, Vector3 b, Vector3 c, AddContact add)
    {
        var tri = new[] { a, b, c };
        var faceNormal = Vector3.Cross(b - a, c - a);
        if (faceNormal.LengthSquared() < 1e-12f) return;
        faceNormal = Vector3.Normalize(faceNormal);

        float bestFace = float.MaxValue, bestEdge = float.MaxValue;
        int faceAxis = -1, edgeBox = -1, edgeTri = -1;
        Vector3 faceN = default, edgeN = default;

        bool Test(Vector3 axis, out float depth, out Vector3 n)
        {
            float tMin = float.MaxValue, tMax = float.MinValue;
            foreach (var v in tri)
            {
                float p = Vector3.Dot(v, axis);
                tMin = MathF.Min(tMin, p);
                tMax = MathF.Max(tMax, p);
            }
            float center = Vector3.Dot(box.Center, axis);
            float radius = box.HalfExtents.X * MathF.Abs(Vector3.Dot(box.AxisX, axis)) +
                           box.HalfExtents.Y * MathF.Abs(Vector3.Dot(box.AxisY, axis)) +
                           box.HalfExtents.Z * MathF.Abs(Vector3.Dot(box.AxisZ, axis));
            // Push the box out on the side its center is on.
            if (center >= (tMin + tMax) * 0.5f)
            {
                n = axis;
                depth = tMax - (center - radius);
            }
            else
            {
                n = -axis;
                depth = center + radius - tMin;
            }
            return depth >= 0f;
        }

        for (int i = 0; i < 4; i++)
        {
            var axis = i == 0 ? faceNormal : box.Axis(i - 1);
            if (!Test(axis, out float depth, out var n)) return;
            if (depth < bestFace)
            {
                bestFace = depth;
                faceAxis = i;
                faceN = n;
            }
        }

        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                var axis = Vector3.Cross(box.Axis(i), tri[(j + 1) % 3] - tri[j]);
                float length = axis.Length();
                if (length < 1e-5f) continue;
                if (!Test(axis / length, out float depth, out var n)) return;
                if (depth < bestEdge)
                {
                    bestEdge = depth;
                    edgeBox = i;
                    edgeTri = j;
                    edgeN = n;
                }
            }

        // Faces win unless an edge axis is clearly shallower, so boxes rest and slide smoothly over flat meshes.
        if (edgeBox >= 0 && bestEdge < bestFace * 0.95f - 0.01f)
        {
            BoxTriangleEdge(box, edgeBox, tri[edgeTri], tri[(edgeTri + 1) % 3], edgeN, bestEdge, add);
            return;
        }

        if (faceAxis == 0)
            TriangleFaceContacts(box, tri, faceN, add);
        else
            BoxFaceContacts(box, faceAxis - 1, tri, faceN, add);
    }

    // The triangle is the reference face: the box face most opposed to n is clipped to the triangle's edges.
    private static void TriangleFaceContacts(Shape box, Vector3[] tri, Vector3 n, AddContact add)
    {
        int k = 0;
        float best = -1f;
        for (int i = 0; i < 3; i++)
        {
            float d = MathF.Abs(Vector3.Dot(box.Axis(i), n));
            if (d > best) { best = d; k = i; }
        }
        var faceNormal = Vector3.Dot(box.Axis(k), n) > 0f ? -box.Axis(k) : box.Axis(k);
        var faceCenter = box.Center + faceNormal * box.Half(k);
        var du = box.Axis((k + 1) % 3) * box.Half((k + 1) % 3);
        var dv = box.Axis((k + 2) % 3) * box.Half((k + 2) % 3);
        var polygon = new List<Vector3> { faceCenter + du + dv, faceCenter - du + dv, faceCenter - du - dv, faceCenter + du - dv };

        var centroid = (tri[0] + tri[1] + tri[2]) / 3f;
        for (int e = 0; e < 3 && polygon.Count > 0; e++)
        {
            var p0 = tri[e];
            var side = Vector3.Normalize(Vector3.Cross(tri[(e + 1) % 3] - p0, n));
            if (Vector3.Dot(side, centroid - p0) > 0f) side = -side;   // point outward
            polygon = Clip(polygon, side, Vector3.Dot(side, p0));
        }

        var points = new List<(Vector3 Point, float Depth)>();
        foreach (var p in polygon)
        {
            float separation = Vector3.Dot(p - tri[0], n);
            if (separation <= 0f)
                points.Add((p - n * (separation * 0.5f), -separation));
        }
        foreach (var (point, depth) in Reduce(points))
            add(point, n, depth);
    }

    // A box face is the reference face: the triangle is clipped to that face's sides.
    private static void BoxFaceContacts(Shape box, int axis, Vector3[] tri, Vector3 n, AddContact add)
    {
        var refNormal = -n;   // the box face that faces the triangle
        var faceCenter = box.Center + refNormal * box.Half(axis);
        var polygon = new List<Vector3>(tri);
        for (int k = 1; k <= 2 && polygon.Count > 0; k++)
        {
            int side = (axis + k) % 3;
            var sideAxis = box.Axis(side);
            float offset = Vector3.Dot(sideAxis, box.Center);
            polygon = Clip(polygon, sideAxis, offset + box.Half(side));
            if (polygon.Count > 0)
                polygon = Clip(polygon, -sideAxis, -offset + box.Half(side));
        }

        var points = new List<(Vector3 Point, float Depth)>();
        foreach (var p in polygon)
        {
            float separation = Vector3.Dot(p - faceCenter, refNormal);
            if (separation <= 0f)
                points.Add((p - refNormal * (separation * 0.5f), -separation));
        }
        foreach (var (point, depth) in Reduce(points))
            add(point, n, depth);
    }

    private static void BoxTriangleEdge(Shape box, int i, Vector3 t0, Vector3 t1, Vector3 n, float depth, AddContact add)
    {
        // The box edge parallel to axis i that sits furthest toward the triangle (against n).
        var mid = box.Center;
        for (int k = 0; k < 3; k++)
            if (k != i)
                mid -= box.Axis(k) * (box.Half(k) * MathF.Sign(Vector3.Dot(box.Axis(k), n)));
        var e0 = mid - box.Axis(i) * box.Half(i);
        var e1 = mid + box.Axis(i) * box.Half(i);
        ClosestOnSegments(e0, e1, t0, t1, out var onBox, out var onTri);
        add((onBox + onTri) * 0.5f, n, depth);
    }

    // ---- geometry helpers --------------------------------------------------------------------

    // Closest point on triangle abc to p (Ericson, Real-Time Collision Detection 5.1.5).
    private static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return a;

        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return b;

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));

        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return c;

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));

        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));

        float denom = 1f / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }

    // Closest points between segments p1-q1 and p2-q2 (Ericson 5.1.9).
    private static void ClosestOnSegments(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out Vector3 c1, out Vector3 c2)
    {
        var d1 = q1 - p1;
        var d2 = q2 - p2;
        var r = p1 - p2;
        float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
        float s, t;
        if (a <= 1e-9f && e <= 1e-9f)
        {
            c1 = p1;
            c2 = p2;
            return;
        }
        if (a <= 1e-9f)
        {
            s = 0f;
            t = Math.Clamp(f / e, 0f, 1f);
        }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= 1e-9f)
            {
                t = 0f;
                s = Math.Clamp(-c / a, 0f, 1f);
            }
            else
            {
                float b = Vector3.Dot(d1, d2);
                float denom = a * e - b * b;
                s = denom > 1e-9f ? Math.Clamp((b * f - c * e) / denom, 0f, 1f) : 0f;
                t = (b * s + f) / e;
                if (t < 0f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
                else if (t > 1f) { t = 1f; s = Math.Clamp((b - c) / a, 0f, 1f); }
            }
        }
        c1 = p1 + d1 * s;
        c2 = p2 + d2 * t;
    }

    private static IEnumerable<Vector3> Corners(Vector3 min, Vector3 max)
    {
        for (int i = 0; i < 8; i++)
            yield return new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
    }

    // ---- triangle tree ---------------------------------------------------------------------------

    // A bounding volume hierarchy over a model's triangles, in the model's own space.
    private sealed class TriangleTree
    {
        private struct Node
        {
            public Vector3 Min, Max;
            public int Left, Right;     // child nodes, or -1 for a leaf
            public int Start, Count;    // a leaf's range in _order
        }

        public Vector3[] Vertices = [];   // three per triangle
        public int Count => Vertices.Length / 3;
        public Vector3 Min, Max;

        private readonly List<Node> _nodes = new();
        private int[] _order = [];

        public static TriangleTree Build(GlbModel model)
        {
            var vertices = new List<Vector3>();
            foreach (var mesh in model.Meshes)
            {
                var p = mesh.Positions;
                Vector3 At(uint i) => new(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]);
                int vertexCount = p.Length / 3;
                for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
                {
                    uint i0 = mesh.Indices[i], i1 = mesh.Indices[i + 1], i2 = mesh.Indices[i + 2];
                    if (i0 >= vertexCount || i1 >= vertexCount || i2 >= vertexCount) continue;
                    Vector3 a = At(i0), b = At(i1), c = At(i2);
                    if (Vector3.Cross(b - a, c - a).LengthSquared() < 1e-14f) continue;   // skip slivers
                    vertices.Add(a);
                    vertices.Add(b);
                    vertices.Add(c);
                }
            }

            var tree = new TriangleTree { Vertices = vertices.ToArray() };
            tree._order = Enumerable.Range(0, tree.Count).ToArray();
            if (tree.Count > 0)
            {
                tree.BuildNode(0, tree.Count);
                tree.Min = tree._nodes[0].Min;
                tree.Max = tree._nodes[0].Max;
            }
            return tree;
        }

        private int BuildNode(int start, int count)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            var cMin = new Vector3(float.MaxValue);
            var cMax = new Vector3(float.MinValue);
            for (int i = start; i < start + count; i++)
            {
                int t = _order[i];
                for (int v = 0; v < 3; v++)
                {
                    min = Vector3.Min(min, Vertices[t * 3 + v]);
                    max = Vector3.Max(max, Vertices[t * 3 + v]);
                }
                var centroid = Centroid(t);
                cMin = Vector3.Min(cMin, centroid);
                cMax = Vector3.Max(cMax, centroid);
            }

            int index = _nodes.Count;
            _nodes.Add(new Node { Min = min, Max = max, Left = -1, Right = -1, Start = start, Count = count });
            if (count <= LeafTriangles) return index;

            // Split at the median along the axis where triangle centers spread the most.
            var spread = cMax - cMin;
            int axis = spread.X > spread.Y ? (spread.X > spread.Z ? 0 : 2) : (spread.Y > spread.Z ? 1 : 2);
            Array.Sort(_order, start, count, Comparer<int>.Create((x, y) =>
                Component(Centroid(x), axis).CompareTo(Component(Centroid(y), axis))));
            int half = count / 2;
            int left = BuildNode(start, half);
            int right = BuildNode(start + half, count - half);
            var node = _nodes[index];
            node.Left = left;
            node.Right = right;
            _nodes[index] = node;
            return index;
        }

        private Vector3 Centroid(int t) => (Vertices[t * 3] + Vertices[t * 3 + 1] + Vertices[t * 3 + 2]) / 3f;

        // Adds every triangle whose bounds overlap the box min-max.
        public void Query(Vector3 min, Vector3 max, List<int> result)
        {
            if (_nodes.Count == 0) return;
            var stack = new Stack<int>();
            stack.Push(0);
            while (stack.Count > 0)
            {
                var node = _nodes[stack.Pop()];
                if (node.Max.X < min.X || node.Min.X > max.X ||
                    node.Max.Y < min.Y || node.Min.Y > max.Y ||
                    node.Max.Z < min.Z || node.Min.Z > max.Z)
                    continue;
                if (node.Left < 0)
                {
                    for (int i = node.Start; i < node.Start + node.Count; i++)
                        result.Add(_order[i]);
                    continue;
                }
                stack.Push(node.Left);
                stack.Push(node.Right);
            }
        }

        // The nearest triangle a ray hits within maxT (the ray is origin + t * direction, in the tree's space).
        // Triangles count from both sides.
        public bool Raycast(Vector3 origin, Vector3 direction, float maxT, out float hitT, out int hitTriangle)
        {
            hitT = maxT;
            hitTriangle = -1;
            if (_nodes.Count == 0) return false;

            var stack = new Stack<int>();
            stack.Push(0);
            while (stack.Count > 0)
            {
                var node = _nodes[stack.Pop()];
                if (!RayHitsBox(origin, direction, node.Min, node.Max, hitT)) continue;
                if (node.Left < 0)
                {
                    for (int i = node.Start; i < node.Start + node.Count; i++)
                    {
                        int t = _order[i];
                        if (RayTriangle(origin, direction, Vertices[t * 3], Vertices[t * 3 + 1], Vertices[t * 3 + 2], out float at) && at < hitT)
                        {
                            hitT = at;
                            hitTriangle = t;
                        }
                    }
                    continue;
                }
                stack.Push(node.Left);
                stack.Push(node.Right);
            }
            return hitTriangle >= 0;
        }
    }
}
