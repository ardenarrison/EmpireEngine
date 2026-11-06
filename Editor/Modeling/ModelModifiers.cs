using System.Numerics;

namespace Editor.Modeling;

// A mesh on its way through the modifiers: points and faces, in model space.
public sealed class MeshWork(List<Vector3> points, List<Poly> polys)
{
    public List<Vector3> Points { get; } = points;
    public List<Poly> Polys { get; } = polys;

    public (Vector3 Min, Vector3 Max) Bounds()
    {
        if (Points.Count == 0) return (Vector3.Zero, Vector3.Zero);
        return (Points.Aggregate(new Vector3(float.MaxValue), Vector3.Min), Points.Aggregate(new Vector3(float.MinValue), Vector3.Max));
    }

    // A face's triangles, as corners. Faces with more than three corners go through ModelEdit's triangulation.
    public IEnumerable<Corner[]> Triangles()
    {
        foreach (var poly in Polys)
            foreach (var (a, b, c) in ModelEdit.Triangulate(poly, Points))
                yield return [poly.Corners[a], poly.Corners[b], poly.Corners[c]];
    }

    // Gives every corner its normal: corners at a point with the same key share one.
    public void ComputeNormals()
    {
        var sums = new Dictionary<(int, int), Vector3>();
        foreach (var poly in Polys)
        {
            var face = ModelEdit.Normal(poly, Points);
            foreach (var corner in poly.Corners)
                sums[(corner.Point, corner.Key)] = sums.GetValueOrDefault((corner.Point, corner.Key)) + face;
        }
        foreach (var poly in Polys)
            for (int i = 0; i < poly.Corners.Length; i++)
                if (sums[(poly.Corners[i].Point, poly.Corners[i].Key)] is { } sum && sum.LengthSquared() > 1e-20f)
                    poly.Corners[i].Normal = Vector3.Normalize(sum);
    }

    public static Corner Plain(int point, int key, Vector2 uv = default) =>
        new() { Point = point, Normal = Vector3.UnitY, Uv = uv, Color = Vector4.One, Key = key };

    public static (int, int) Edge(int a, int b) => a < b ? (a, b) : (b, a);

    // Each edge, with how many faces use it.
    public Dictionary<(int, int), int> EdgeUses()
    {
        var uses = new Dictionary<(int, int), int>();
        foreach (var poly in Polys)
            for (int i = 0; i < poly.Corners.Length; i++)
            {
                var edge = Edge(poly.Corners[i].Point, poly.Corners[(i + 1) % poly.Corners.Length].Point);
                uses[edge] = uses.GetValueOrDefault(edge) + 1;
            }
        return uses;
    }
}

// One step in a model's modifier stack. A modifier makes a new mesh from the one given to it and leaves that one alone, so the mesh
// being edited stays as it is until the modifier is applied. Problem says why it did nothing, the last time it ran.
public abstract class ModelModifier
{
    public const int MaxFaces = 500_000;   // past this the editor would stop answering

    public bool Enabled { get; set; } = true;
    public string? Problem { get; protected set; }
    public abstract string Name { get; }

    public MeshWork Run(MeshWork input)
    {
        Problem = null;
        return Apply(input);
    }

    protected abstract MeshWork Apply(MeshWork input);

    protected MeshWork TooBig(MeshWork input)
    {
        Problem = $"This would make more than {MaxFaces:N0} faces, so it was left out.";
        return input;
    }
}

// Rounds a mesh by splitting every face and smoothing the result (Catmull-Clark). Levels is how many times.
public sealed class SubdivisionModifier : ModelModifier
{
    public override string Name => "Subdivision Surface";
    public int Levels { get; set; } = 1;

    protected override MeshWork Apply(MeshWork input)
    {
        var work = input;
        for (int level = 0; level < Levels; level++)
        {
            if (work.Polys.Sum(p => p.Corners.Length) > MaxFaces) return TooBig(input);
            work = Once(work);
        }
        return work;
    }

    private static MeshWork Once(MeshWork work)
    {
        var points = work.Points;
        var facePoints = work.Polys.Select(p => p.Corners.Aggregate(Vector3.Zero, (sum, c) => sum + points[c.Point]) / p.Corners.Length).ToList();

        var edgeFaces = new Dictionary<(int, int), List<int>>();
        for (int f = 0; f < work.Polys.Count; f++)
        {
            var corners = work.Polys[f].Corners;
            for (int i = 0; i < corners.Length; i++)
            {
                var edge = MeshWork.Edge(corners[i].Point, corners[(i + 1) % corners.Length].Point);
                if (!edgeFaces.TryGetValue(edge, out var faces))
                    edgeFaces[edge] = faces = new();
                faces.Add(f);
            }
        }

        // New points go after the old ones: first one for each face, then one for each edge.
        var result = new List<Vector3>(points);
        int firstFacePoint = result.Count;
        result.AddRange(facePoints);
        var edgePoints = new Dictionary<(int, int), int>();
        foreach (var (edge, faces) in edgeFaces)
        {
            var middle = (points[edge.Item1] + points[edge.Item2]) * 0.5f;
            edgePoints[edge] = result.Count;
            result.Add(faces.Count == 2 ? (middle + (facePoints[faces[0]] + facePoints[faces[1]]) * 0.5f) * 0.5f : middle);
        }

        // Each old point moves toward the average of what surrounds it. An open edge only follows the open edge.
        var touchingFaces = new Vector3[points.Count];
        var faceCounts = new int[points.Count];
        var touchingEdges = new Vector3[points.Count];
        var edgeCounts = new int[points.Count];
        var openEdges = new Vector3[points.Count];
        var openCounts = new int[points.Count];
        for (int f = 0; f < work.Polys.Count; f++)
            foreach (var corner in work.Polys[f].Corners)
            {
                touchingFaces[corner.Point] += facePoints[f];
                faceCounts[corner.Point]++;
            }
        foreach (var (edge, faces) in edgeFaces)
        {
            var middle = (points[edge.Item1] + points[edge.Item2]) * 0.5f;
            foreach (int end in new[] { edge.Item1, edge.Item2 })
            {
                touchingEdges[end] += middle;
                edgeCounts[end]++;
                if (faces.Count == 1)
                {
                    openEdges[end] += middle;
                    openCounts[end]++;
                }
            }
        }
        for (int i = 0; i < points.Count; i++)
        {
            if (openCounts[i] >= 2)
                result[i] = (points[i] + openEdges[i] / openCounts[i]) * 0.5f;
            else if (faceCounts[i] >= 3 && edgeCounts[i] > 0)
            {
                float n = faceCounts[i];
                result[i] = (touchingFaces[i] / n + touchingEdges[i] / edgeCounts[i] * 2f + points[i] * (n - 3f)) / n;
            }
        }

        var polys = new List<Poly>(work.Polys.Sum(p => p.Corners.Length));
        for (int f = 0; f < work.Polys.Count; f++)
        {
            var poly = work.Polys[f];
            var corners = poly.Corners;
            int count = corners.Length;
            var center = ModelEdit.Mixed(corners, firstFacePoint + f, 0);
            var middles = new Corner[count];
            for (int i = 0; i < count; i++)
            {
                var next = corners[(i + 1) % count];
                middles[i] = ModelEdit.Between(corners[i], next, edgePoints[MeshWork.Edge(corners[i].Point, next.Point)]);
                middles[i].Key = 0;
            }
            for (int i = 0; i < count; i++)
            {
                var own = corners[i];
                own.Key = 0;
                polys.Add(new Poly([own, middles[i], center, middles[(i + count - 1) % count]], poly.Part));
            }
        }
        return new MeshWork(result, polys);
    }
}

// Repeats the mesh. Offset is the step between copies, as a share of the mesh's size on each axis.
public sealed class ArrayModifier : ModelModifier
{
    public override string Name => "Array";
    public int Count { get; set; } = 2;
    public Vector3 Offset { get; set; } = Vector3.UnitX;

    protected override MeshWork Apply(MeshWork input)
    {
        if ((long)input.Polys.Count * Count > MaxFaces) return TooBig(input);
        var (min, max) = input.Bounds();
        var step = (max - min) * Offset;
        var points = new List<Vector3>(input.Points.Count * Count);
        var polys = new List<Poly>(input.Polys.Count * Count);
        for (int copy = 0; copy < Count; copy++)
        {
            int first = points.Count;
            points.AddRange(input.Points.Select(p => p + step * copy));
            foreach (var poly in input.Polys)
            {
                var corners = (Corner[])poly.Corners.Clone();
                for (int i = 0; i < corners.Length; i++) corners[i].Point += first;
                polys.Add(new Poly(corners, poly.Part));
            }
        }
        return new MeshWork(points, polys);
    }
}

// Adds a reflection of the mesh across the model's own center. Points on the mirror join their reflection, so halves stay joined.
public sealed class MirrorModifier : ModelModifier
{
    private const float Join = 1e-4f;

    public override string Name => "Mirror";
    public bool X { get; set; } = true;
    public bool Y { get; set; }
    public bool Z { get; set; }

    protected override MeshWork Apply(MeshWork input)
    {
        var work = input;
        foreach (var (axis, on) in new[] { (0, X), (1, Y), (2, Z) })
            if (on) work = Reflect(work, axis);
        return work;
    }

    private static MeshWork Reflect(MeshWork work, int axis)
    {
        var points = new List<Vector3>(work.Points);
        var map = new int[points.Count];
        for (int i = 0; i < map.Length; i++)
        {
            var at = points[i];
            if (MathF.Abs(at[axis]) <= Join)
            {
                at[axis] = 0f;
                points[i] = at;
                map[i] = i;
            }
            else
            {
                at[axis] = -at[axis];
                map[i] = points.Count;
                points.Add(at);
            }
        }

        // A face lying on the mirror ends up inside what the two halves make, so it goes, unless that's all there is.
        bool OnMirror(Poly poly) => poly.Corners.All(c => map[c.Point] == c.Point);
        var kept = work.Polys.Any(p => !OnMirror(p)) ? work.Polys.Where(p => !OnMirror(p)).ToList() : work.Polys;
        var polys = kept.Select(p => p.Copy()).ToList();
        foreach (var poly in kept)
        {
            if (OnMirror(poly)) continue;
            var corners = poly.Corners.Reverse().ToArray();
            for (int i = 0; i < corners.Length; i++) corners[i].Point = map[corners[i].Point];
            polys.Add(new Poly(corners, poly.Part));
        }
        return new MeshWork(points, polys);
    }
}

// Gives a surface thickness. Offset says which way it grows: -1 inward, 1 outward, 0 both ways.
public sealed class SolidifyModifier : ModelModifier
{
    public override string Name => "Solidify";
    public float Thickness { get; set; } = 0.05f;
    public float Offset { get; set; } = -1f;

    protected override MeshWork Apply(MeshWork input)
    {
        var directions = new Vector3[input.Points.Count];
        foreach (var poly in input.Polys)
        {
            var face = ModelEdit.Normal(poly, input.Points);
            if (face.LengthSquared() < 1e-20f) continue;
            face = Vector3.Normalize(face);
            foreach (var corner in poly.Corners)
                directions[corner.Point] += face;
        }
        for (int i = 0; i < directions.Length; i++)
            if (directions[i].LengthSquared() > 1e-12f) directions[i] = Vector3.Normalize(directions[i]);

        float outer = Thickness * (Offset + 1f) * 0.5f;
        float inner = Thickness * (Offset - 1f) * 0.5f;
        int count = input.Points.Count;
        var points = new List<Vector3>(count * 2);
        points.AddRange(input.Points.Select((p, i) => p + directions[i] * outer));
        points.AddRange(input.Points.Select((p, i) => p + directions[i] * inner));

        var polys = input.Polys.Select(p => p.Copy()).ToList();
        var uses = input.EdgeUses();
        int key = 1 << 28;   // the rim's keys, apart from any the mesh has
        foreach (var poly in input.Polys)
        {
            var back = poly.Corners.Reverse().ToArray();
            for (int i = 0; i < back.Length; i++) back[i].Point += count;
            polys.Add(new Poly(back, poly.Part));

            var corners = poly.Corners;
            for (int i = 0; i < corners.Length; i++)
            {
                var a = corners[i];
                var b = corners[(i + 1) % corners.Length];
                if (uses[MeshWork.Edge(a.Point, b.Point)] != 1) continue;
                key++;
                polys.Add(new Poly([MeshWork.Plain(b.Point, key, b.Uv), MeshWork.Plain(a.Point, key, a.Uv),
                    MeshWork.Plain(a.Point + count, key, a.Uv), MeshWork.Plain(b.Point + count, key, b.Uv)], poly.Part));
            }
        }
        return new MeshWork(points, polys);
    }
}

// Reduces the number of faces, removing the edges that change the shape least (quadric error collapse). Ratio is the share of
// triangles to keep. The result is all triangles.
public sealed class DecimateModifier : ModelModifier
{
    public override string Name => "Decimate";
    public float Ratio { get; set; } = 0.5f;

    // A plane's error for a point, as the ten numbers of a symmetric 4x4 matrix.
    private sealed class Quadric
    {
        public readonly double[] M = new double[10];

        public void AddPlane(Vector3 n, float d, double weight)
        {
            double a = n.X, b = n.Y, c = n.Z, e = d;
            double[] values = [a * a, a * b, a * c, a * e, b * b, b * c, b * e, c * c, c * e, e * e];
            for (int i = 0; i < 10; i++) M[i] += values[i] * weight;
        }

        public void Add(Quadric other)
        {
            for (int i = 0; i < 10; i++) M[i] += other.M[i];
        }

        public double Error(Vector3 v, Quadric other)
        {
            double x = v.X, y = v.Y, z = v.Z;
            double M0 = M[0] + other.M[0], M1 = M[1] + other.M[1], M2 = M[2] + other.M[2], M3 = M[3] + other.M[3], M4 = M[4] + other.M[4];
            double M5 = M[5] + other.M[5], M6 = M[6] + other.M[6], M7 = M[7] + other.M[7], M8 = M[8] + other.M[8], M9 = M[9] + other.M[9];
            return M0 * x * x + 2 * M1 * x * y + 2 * M2 * x * z + 2 * M3 * x + M4 * y * y + 2 * M5 * y * z + 2 * M6 * y + M7 * z * z + 2 * M8 * z + M9;
        }
    }

    protected override MeshWork Apply(MeshWork input)
    {
        var tris = input.Triangles().Select(t => (Corner[])t.Clone()).ToList();
        var parts = new List<int>(tris.Count);
        foreach (var poly in input.Polys)
            for (int i = 0; i < poly.Corners.Length - 2; i++)
                parts.Add(poly.Part);

        int target = Math.Max(4, (int)(tris.Count * Math.Clamp(Ratio, 0f, 1f)));
        if (target >= tris.Count) return input;

        var points = new List<Vector3>(input.Points);
        var quadrics = points.Select(_ => new Quadric()).ToArray();
        var trisOf = points.Select(_ => new List<int>()).ToArray();
        var edgeUses = new Dictionary<(int, int), int>();
        for (int t = 0; t < tris.Count; t++)
        {
            var c = tris[t];
            var n = Vector3.Cross(points[c[1].Point] - points[c[0].Point], points[c[2].Point] - points[c[0].Point]);
            float area = n.Length();
            if (area > 1e-12f)
            {
                n /= area;
                float d = -Vector3.Dot(n, points[c[0].Point]);
                foreach (var corner in c) quadrics[corner.Point].AddPlane(n, d, area);
            }
            for (int i = 0; i < 3; i++)
            {
                trisOf[c[i].Point].Add(t);
                var edge = MeshWork.Edge(c[i].Point, c[(i + 1) % 3].Point);
                edgeUses[edge] = edgeUses.GetValueOrDefault(edge) + 1;
            }
        }

        // An open edge gets a wall of its own, which keeps the outline of an open surface in place.
        foreach (var ((a, b), uses) in edgeUses)
        {
            if (uses != 1) continue;
            var along = points[b] - points[a];
            float length = along.Length();
            if (length < 1e-9f) continue;
            var touching = trisOf[a].First(t => tris[t].Any(c => c.Point == b));
            var c0 = tris[touching];
            var faceNormal = Vector3.Cross(points[c0[1].Point] - points[c0[0].Point], points[c0[2].Point] - points[c0[0].Point]);
            var wall = Vector3.Cross(along, faceNormal);
            if (wall.LengthSquared() < 1e-20f) continue;
            wall = Vector3.Normalize(wall);
            float d = -Vector3.Dot(wall, points[a]);
            quadrics[a].AddPlane(wall, d, length * 100.0);
            quadrics[b].AddPlane(wall, d, length * 100.0);
        }

        var alive = Enumerable.Repeat(true, tris.Count).ToArray();
        var pointAlive = Enumerable.Repeat(true, points.Count).ToArray();
        var stamps = new int[points.Count];
        var queue = new PriorityQueue<(int A, int B, int StampA, int StampB), double>();

        (Vector3 At, double Cost) Best(int a, int b)
        {
            var middle = (points[a] + points[b]) * 0.5f;
            double atA = quadrics[a].Error(points[a], quadrics[b]);
            double atB = quadrics[a].Error(points[b], quadrics[b]);
            double atMiddle = quadrics[a].Error(middle, quadrics[b]);
            return atMiddle <= atA && atMiddle <= atB ? (middle, atMiddle) : atA <= atB ? (points[a], atA) : (points[b], atB);
        }
        void Enqueue(int a, int b) => queue.Enqueue((a, b, stamps[a], stamps[b]), Best(a, b).Cost);

        foreach (var (a, b) in edgeUses.Keys) Enqueue(a, b);

        // True if putting this point at `at` would turn one of its triangles over.
        bool Folds(int point, int other, Vector3 at)
        {
            foreach (int t in trisOf[point])
            {
                if (!alive[t]) continue;
                var c = tris[t];
                if (c.Any(k => k.Point == other)) continue;
                var p0 = points[c[0].Point];
                var p1 = points[c[1].Point];
                var p2 = points[c[2].Point];
                var before = Vector3.Cross(p1 - p0, p2 - p0);
                if (c[0].Point == point) p0 = at;
                else if (c[1].Point == point) p1 = at;
                else p2 = at;
                var after = Vector3.Cross(p1 - p0, p2 - p0);
                if (Vector3.Dot(before, after) <= 0f) return true;
            }
            return false;
        }

        int remaining = tris.Count;
        while (remaining > target && queue.TryDequeue(out var entry, out _))
        {
            var (a, b, stampA, stampB) = entry;
            if (!pointAlive[a] || !pointAlive[b] || stamps[a] != stampA || stamps[b] != stampB) continue;
            var (at, _) = Best(a, b);
            if (Folds(a, b, at) || Folds(b, a, at)) continue;

            pointAlive[b] = false;
            points[a] = at;
            quadrics[a].Add(quadrics[b]);
            stamps[a]++;
            foreach (int t in trisOf[b])
            {
                if (!alive[t]) continue;
                var c = tris[t];
                if (c.Any(k => k.Point == a))
                {
                    alive[t] = false;
                    remaining--;
                    continue;
                }
                for (int i = 0; i < 3; i++)
                    if (c[i].Point == b) c[i].Point = a;
                trisOf[a].Add(t);
            }
            var around = new HashSet<int>();
            foreach (int t in trisOf[a])
                if (alive[t])
                    foreach (var corner in tris[t])
                        if (corner.Point != a) around.Add(corner.Point);
            foreach (int neighbor in around)
            {
                stamps[neighbor]++;
                Enqueue(a, neighbor);
            }
        }

        var moved = new int[points.Count];
        var kept = new List<Vector3>();
        Array.Fill(moved, -1);
        var polys = new List<Poly>(remaining);
        for (int t = 0; t < tris.Count; t++)
        {
            if (!alive[t]) continue;
            var c = tris[t];
            for (int i = 0; i < 3; i++)
            {
                if (moved[c[i].Point] < 0)
                {
                    moved[c[i].Point] = kept.Count;
                    kept.Add(points[c[i].Point]);
                }
                c[i].Point = moved[c[i].Point];
            }
            polys.Add(new Poly(c, parts[t]));
        }
        return new MeshWork(kept, polys);
    }
}

// Puts a tube around every edge, with a ball at every point: the mesh becomes a frame of its own edges.
public sealed class SkinModifier : ModelModifier
{
    public override string Name => "Skin";
    public float Radius { get; set; } = 0.05f;
    public int Sides { get; set; } = 6;

    protected override MeshWork Apply(MeshWork input)
    {
        var edges = input.EdgeUses().Keys.ToList();
        int sides = Math.Clamp(Sides, 3, 32);
        if ((long)edges.Count * sides + (long)input.Points.Count * sides * sides / 2 > MaxFaces) return TooBig(input);

        var points = new List<Vector3>();
        var polys = new List<Poly>();
        int key = 1;
        foreach (var (a, b) in edges)
        {
            var from = input.Points[a];
            var to = input.Points[b];
            var along = to - from;
            if (along.LengthSquared() < 1e-12f) continue;
            along = Vector3.Normalize(along);
            var across = Vector3.Normalize(Vector3.Cross(along, MathF.Abs(along.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
            var up = Vector3.Cross(along, across);
            int first = points.Count;
            for (int i = 0; i < sides; i++)
            {
                float angle = i * MathF.Tau / sides;
                var round = (across * MathF.Cos(angle) + up * MathF.Sin(angle)) * Radius;
                points.Add(from + round);
                points.Add(to + round);
            }
            key++;
            for (int i = 0; i < sides; i++)
            {
                int next = (i + 1) % sides;
                polys.Add(new Poly([MeshWork.Plain(first + i * 2, key), MeshWork.Plain(first + next * 2, key),
                    MeshWork.Plain(first + next * 2 + 1, key), MeshWork.Plain(first + i * 2 + 1, key)], 0));
            }
        }

        foreach (var center in input.Points)
            AddBall(points, polys, center, sides, ++key);
        return new MeshWork(points, polys);
    }

    private void AddBall(List<Vector3> points, List<Poly> polys, Vector3 center, int sides, int key)
    {
        int rings = Math.Max(2, sides / 2);
        int top = points.Count;
        points.Add(center + Vector3.UnitY * Radius);
        int firstRing = points.Count;
        for (int ring = 1; ring < rings; ring++)
        {
            float tilt = ring * MathF.PI / rings;
            for (int i = 0; i < sides; i++)
            {
                float angle = i * MathF.Tau / sides;
                points.Add(center + new Vector3(MathF.Sin(tilt) * MathF.Cos(angle), MathF.Cos(tilt), MathF.Sin(tilt) * MathF.Sin(angle)) * Radius);
            }
        }
        int bottom = points.Count;
        points.Add(center - Vector3.UnitY * Radius);

        int At(int ring, int i) => firstRing + (ring - 1) * sides + i % sides;
        for (int i = 0; i < sides; i++)
        {
            polys.Add(new Poly([MeshWork.Plain(top, key), MeshWork.Plain(At(1, i + 1), key), MeshWork.Plain(At(1, i), key)], 0));
            for (int ring = 1; ring + 1 < rings; ring++)
                polys.Add(new Poly([MeshWork.Plain(At(ring, i), key), MeshWork.Plain(At(ring, i + 1), key),
                    MeshWork.Plain(At(ring + 1, i + 1), key), MeshWork.Plain(At(ring + 1, i), key)], 0));
            polys.Add(new Poly([MeshWork.Plain(bottom, key), MeshWork.Plain(At(rings - 1, i), key), MeshWork.Plain(At(rings - 1, i + 1), key)], 0));
        }
    }
}

// Sweeps the mesh's open edges around an axis through the model's center, or all its edges if it has none open. Height moves it
// along the axis as it turns, for a spiral. The faces of the original aren't kept.
public sealed class ScrewModifier : ModelModifier
{
    public override string Name => "Screw";
    public int Axis { get; set; } = 1;
    public float Angle { get; set; } = 360f;
    public int Steps { get; set; } = 16;
    public float Height { get; set; }

    protected override MeshWork Apply(MeshWork input)
    {
        // Each edge keeps the direction it runs in its face, so that side by side the strips turn the same way.
        var uses = input.EdgeUses();
        bool open = uses.Values.Any(count => count == 1);
        var edges = new List<(int A, int B)>();
        var taken = new HashSet<(int, int)>();
        foreach (var poly in input.Polys)
            for (int i = 0; i < poly.Corners.Length; i++)
            {
                int from = poly.Corners[i].Point, to = poly.Corners[(i + 1) % poly.Corners.Length].Point;
                var edge = MeshWork.Edge(from, to);
                if ((!open || uses[edge] == 1) && taken.Add(edge))
                    edges.Add((from, to));
            }
        if (edges.Count == 0)
        {
            Problem = "There are no edges to sweep.";
            return input;
        }
        int steps = Math.Max(1, Steps);
        if ((long)edges.Count * steps > MaxFaces) return TooBig(input);

        var axis = Axis == 0 ? Vector3.UnitX : Axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
        bool closed = MathF.Abs(Angle) >= 360f - 1e-3f && Height == 0f;
        int rows = closed ? steps : steps + 1;
        int count = input.Points.Count;
        var points = new List<Vector3>(count * rows);
        for (int row = 0; row < rows; row++)
        {
            float t = row / (float)steps;
            var turn = Quaternion.CreateFromAxisAngle(axis, Angle * MathF.PI / 180f * t);
            points.AddRange(input.Points.Select(p => Vector3.Transform(p, turn) + axis * (Height * t)));
        }

        var polys = new List<Poly>(edges.Count * steps);
        foreach (var (a, b) in edges)
            for (int row = 0; row < steps; row++)
            {
                int next = closed ? (row + 1) % rows : row + 1;
                float u = row / (float)steps, u2 = (row + 1) / (float)steps;
                polys.Add(new Poly([MeshWork.Plain(row * count + a, 0, new Vector2(u, 0f)), MeshWork.Plain(row * count + b, 0, new Vector2(u, 1f)),
                    MeshWork.Plain(next * count + b, 0, new Vector2(u2, 1f)), MeshWork.Plain(next * count + a, 0, new Vector2(u2, 0f))], 0));
            }

        // The edges have no direction of their own, so the sweep is turned to face away from the axis.
        float facing = 0f;
        foreach (var poly in polys)
        {
            var center = poly.Corners.Aggregate(Vector3.Zero, (sum, c) => sum + points[c.Point]) / 4f;
            var outward = center - axis * Vector3.Dot(center, axis);
            facing += Vector3.Dot(ModelEdit.Normal(poly, points), outward);
        }
        if (facing < 0f)
            foreach (var poly in polys)
                Array.Reverse(poly.Corners);
        return new MeshWork(points, polys);
    }
}

public enum BooleanOperation { Difference, Union, Intersect }

// Cuts with, joins to, or keeps what overlaps another mesh (a binary space partition, the usual way). Both should be closed solids.
// Other is that mesh in this one's model space; the view fills it in from the chosen object.
public sealed class BooleanModifier : ModelModifier
{
    private const int MaxTriangles = 30_000;   // the method slows sharply with size
    private const float Epsilon = 1e-5f;

    public override string Name => "Boolean";
    public BooleanOperation Operation { get; set; }
    public Guid? Target { get; set; }
    public MeshWork? Other { get; set; }

    private sealed record Vertex(Vector3 At, Corner Corner);
    private sealed record Plane(Vector3 Normal, float W);

    private sealed class Polygon(List<Vertex> vertices, int part, Plane plane)
    {
        public List<Vertex> Vertices { get; } = vertices;
        public int Part { get; } = part;
        public Plane Plane { get; } = plane;

        public Polygon Flipped()
        {
            var reversed = new List<Vertex>(Vertices);
            reversed.Reverse();
            return new Polygon(reversed, Part, new Plane(-Plane.Normal, -Plane.W));
        }
    }

    protected override MeshWork Apply(MeshWork input)
    {
        if (Other is not { } other)
        {
            Problem = "Choose an object to use.";
            return input;
        }
        var a = ToPolygons(input);
        var b = ToPolygons(other);
        if (a.Count + b.Count > MaxTriangles)
        {
            Problem = $"This is too many faces for a boolean ({a.Count + b.Count:N0} triangles, up to {MaxTriangles:N0}). Try Decimate first.";
            return input;
        }

        // The tree can go as deep as there are faces, more than a normal thread's stack allows.
        List<Polygon> result = [];
        var thread = new Thread(() => result = Combine(a, b), 256 * 1024 * 1024);
        thread.Start();
        thread.Join();
        return ToMesh(result);
    }

    private List<Polygon> Combine(List<Polygon> polygonsA, List<Polygon> polygonsB)
    {
        var a = new Node(polygonsA);
        var b = new Node(polygonsB);
        switch (Operation)
        {
            case BooleanOperation.Union:
                a.ClipTo(b);
                b.ClipTo(a);
                b.Invert();
                b.ClipTo(a);
                b.Invert();
                a.Build(b.All());
                break;
            case BooleanOperation.Intersect:
                a.Invert();
                b.ClipTo(a);
                b.Invert();
                a.ClipTo(b);
                b.ClipTo(a);
                a.Build(b.All());
                a.Invert();
                break;
            default:
                a.Invert();
                a.ClipTo(b);
                b.ClipTo(a);
                b.Invert();
                b.ClipTo(a);
                b.Invert();
                a.Build(b.All());
                a.Invert();
                break;
        }
        return a.All();
    }

    private static List<Polygon> ToPolygons(MeshWork mesh)
    {
        var result = new List<Polygon>();
        foreach (var poly in mesh.Polys)
            foreach (var (i, j, k) in ModelEdit.Triangulate(poly, mesh.Points))
            {
                var corners = new[] { poly.Corners[i], poly.Corners[j], poly.Corners[k] };
                var at = corners.Select(c => mesh.Points[c.Point]).ToArray();
                var normal = Vector3.Cross(at[1] - at[0], at[2] - at[0]);
                if (normal.LengthSquared() < 1e-20f) continue;
                normal = Vector3.Normalize(normal);
                var vertices = corners.Select((c, n) => new Vertex(at[n], c)).ToList();
                result.Add(new Polygon(vertices, poly.Part, new Plane(normal, Vector3.Dot(normal, at[0]))));
            }
        return result;
    }

    // Joins the pieces back into one mesh. Corners in the same place become one point.
    private static MeshWork ToMesh(List<Polygon> polygons)
    {
        var points = new List<Vector3>();
        var cells = new Dictionary<(int, int, int), int>();
        var polys = new List<Poly>(polygons.Count);
        int Point(Vector3 at)
        {
            var cell = ((int)MathF.Round(at.X * 1e4f), (int)MathF.Round(at.Y * 1e4f), (int)MathF.Round(at.Z * 1e4f));
            if (!cells.TryGetValue(cell, out int point))
            {
                cells[cell] = point = points.Count;
                points.Add(at);
            }
            return point;
        }

        int key = 1;
        foreach (var polygon in polygons)
        {
            key++;   // cut faces are flat: their old shading doesn't carry across a cut
            var corners = new List<Corner>(polygon.Vertices.Count);
            foreach (var vertex in polygon.Vertices)
            {
                var corner = vertex.Corner;
                corner.Point = Point(vertex.At);
                corner.Key = key;
                if (corners.Count == 0 || corners[^1].Point != corner.Point)
                    corners.Add(corner);
            }
            if (corners.Count > 1 && corners[0].Point == corners[^1].Point) corners.RemoveAt(corners.Count - 1);
            if (corners.Select(c => c.Point).Distinct().Count() >= 3)
                polys.Add(new Poly(corners.ToArray(), polygon.Part));
        }
        return new MeshWork(points, polys);
    }

    private static void Split(Plane plane, Polygon polygon, List<Polygon> coplanarFront, List<Polygon> coplanarBack, List<Polygon> front, List<Polygon> back)
    {
        const int Coplanar = 0, Front = 1, Back = 2, Spanning = 3;
        int kind = 0;
        var kinds = new int[polygon.Vertices.Count];
        for (int i = 0; i < kinds.Length; i++)
        {
            float t = Vector3.Dot(plane.Normal, polygon.Vertices[i].At) - plane.W;
            kinds[i] = t < -Epsilon ? Back : t > Epsilon ? Front : Coplanar;
            kind |= kinds[i];
        }

        switch (kind)
        {
            case Coplanar:
                (Vector3.Dot(plane.Normal, polygon.Plane.Normal) > 0f ? coplanarFront : coplanarBack).Add(polygon);
                break;
            case Front:
                front.Add(polygon);
                break;
            case Back:
                back.Add(polygon);
                break;
            case Spanning:
                var f = new List<Vertex>();
                var b = new List<Vertex>();
                for (int i = 0; i < kinds.Length; i++)
                {
                    int j = (i + 1) % kinds.Length;
                    var vi = polygon.Vertices[i];
                    var vj = polygon.Vertices[j];
                    if (kinds[i] != Back) f.Add(vi);
                    if (kinds[i] != Front) b.Add(vi);
                    if ((kinds[i] | kinds[j]) == Spanning)
                    {
                        float t = (plane.W - Vector3.Dot(plane.Normal, vi.At)) / Vector3.Dot(plane.Normal, vj.At - vi.At);
                        var corner = vi.Corner;
                        corner.Uv = Vector2.Lerp(vi.Corner.Uv, vj.Corner.Uv, t);
                        corner.Color = Vector4.Lerp(vi.Corner.Color, vj.Corner.Color, t);
                        var cut = new Vertex(Vector3.Lerp(vi.At, vj.At, t), corner);
                        f.Add(cut);
                        b.Add(cut);
                    }
                }
                if (f.Count >= 3) front.Add(new Polygon(f, polygon.Part, polygon.Plane));
                if (b.Count >= 3) back.Add(new Polygon(b, polygon.Part, polygon.Plane));
                break;
        }
    }

    private sealed class Node
    {
        private Plane? _plane;
        private Node? _front, _back;
        private List<Polygon> _polygons = new();

        public Node(List<Polygon> polygons) => Build(polygons);

        private Node() { }

        public void Invert()
        {
            _polygons = _polygons.Select(p => p.Flipped()).ToList();
            if (_plane is { } plane) _plane = new Plane(-plane.Normal, -plane.W);
            _front?.Invert();
            _back?.Invert();
            (_front, _back) = (_back, _front);
        }

        private List<Polygon> Clip(List<Polygon> polygons)
        {
            if (_plane is not { } plane) return new List<Polygon>(polygons);
            var front = new List<Polygon>();
            var back = new List<Polygon>();
            foreach (var polygon in polygons)
                Split(plane, polygon, front, back, front, back);
            if (_front is not null) front = _front.Clip(front);
            back = _back is not null ? _back.Clip(back) : new List<Polygon>();
            front.AddRange(back);
            return front;
        }

        public void ClipTo(Node other)
        {
            _polygons = other.Clip(_polygons);
            _front?.ClipTo(other);
            _back?.ClipTo(other);
        }

        public List<Polygon> All()
        {
            var all = new List<Polygon>(_polygons);
            if (_front is not null) all.AddRange(_front.All());
            if (_back is not null) all.AddRange(_back.All());
            return all;
        }

        public void Build(List<Polygon> polygons)
        {
            if (polygons.Count == 0) return;
            _plane ??= polygons[0].Plane;
            var front = new List<Polygon>();
            var back = new List<Polygon>();
            foreach (var polygon in polygons)
                Split(_plane, polygon, _polygons, _polygons, front, back);
            if (front.Count > 0) (_front ??= new Node()).Build(front);
            if (back.Count > 0) (_back ??= new Node()).Build(back);
        }
    }
}
