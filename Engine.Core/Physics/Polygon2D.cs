using System.Numerics;

namespace Engine.Core.Physics;

// The geometry of a polygon collider's outline. The physics only handles convex shapes, so an outline with dents is cut into
// convex pieces that together cover it.
public static class Polygon2D
{
    // What is wrong with an outline, or null when it can collide.
    public static string? Problem(IReadOnlyList<Vector2> points)
    {
        if (points.Count < 3) return "It needs at least 3 points.";
        var clean = Clean(points);
        if (clean.Count >= 4 && Crosses(clean)) return "Its edges cross, so it won't collide with anything.";   // first, as halves of a crossed outline can cancel out to no area
        return clean.Count < 3 || MathF.Abs(SignedArea(clean)) <= Tiny(clean) ? "Its points are all in a line, so it has no area." : null;
    }

    // Positive when the points go counter-clockwise.
    public static float SignedArea(IReadOnlyList<Vector2> points)
    {
        float area = 0f;
        for (int i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            area += a.X * b.Y - b.X * a.Y;
        }
        return area / 2f;
    }

    // Convex pieces, each counter-clockwise, that together cover the outline. None when the outline has a Problem.
    public static List<Vector2[]> ConvexPieces(IReadOnlyList<Vector2> points)
    {
        if (Problem(points) is not null) return new();
        var clean = Clean(points);
        if (SignedArea(clean) < 0f) clean.Reverse();
        return Merge(clean, Triangles(clean)).Select(piece => piece.Select(i => clean[i]).ToArray()).ToList();
    }

    // Without repeated points, or points on a straight line between their neighbors, which add nothing to the shape.
    private static List<Vector2> Clean(IReadOnlyList<Vector2> points)
    {
        var clean = new List<Vector2>();
        foreach (var point in points)
            if (clean.Count == 0 || Vector2.DistanceSquared(clean[^1], point) > 1e-12f)
                clean.Add(point);
        while (clean.Count > 1 && Vector2.DistanceSquared(clean[0], clean[^1]) <= 1e-12f)
            clean.RemoveAt(clean.Count - 1);

        for (int i = 0; i < clean.Count && clean.Count > 2;)
        {
            var before = clean[(i + clean.Count - 1) % clean.Count];
            var after = clean[(i + 1) % clean.Count];
            if (MathF.Abs(Cross(clean[i] - before, after - clean[i])) <= Tiny(clean)) clean.RemoveAt(i);
            else i++;
        }
        return clean;
    }

    // An area too small to mean anything at this outline's size.
    private static float Tiny(IReadOnlyList<Vector2> points)
    {
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var point in points)
        {
            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
        }
        var size = max - min;
        return MathF.Max(size.X * size.X, size.Y * size.Y) * 1e-7f;
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    private static bool Crosses(List<Vector2> points)
    {
        int n = points.Count;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                if (j == i + 1 || (i == 0 && j == n - 1)) continue;   // neighbors share a point
                if (SegmentsMeet(points[i], points[(i + 1) % n], points[j], points[(j + 1) % n])) return true;
            }
        return false;
    }

    private static bool SegmentsMeet(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        float d1 = Cross(b - a, c - a), d2 = Cross(b - a, d - a), d3 = Cross(d - c, a - c), d4 = Cross(d - c, b - c);
        if (((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f)) && ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f))) return true;
        return (d1 == 0f && OnSegment(a, b, c)) || (d2 == 0f && OnSegment(a, b, d)) || (d3 == 0f && OnSegment(c, d, a)) || (d4 == 0f && OnSegment(c, d, b));
    }

    private static bool OnSegment(Vector2 a, Vector2 b, Vector2 p) =>
        p.X >= MathF.Min(a.X, b.X) && p.X <= MathF.Max(a.X, b.X) && p.Y >= MathF.Min(a.Y, b.Y) && p.Y <= MathF.Max(a.Y, b.Y);

    // Cuts off ears: corners that point outward with nothing else inside them. Every simple outline has one.
    private static List<int[]> Triangles(List<Vector2> points)
    {
        var remaining = Enumerable.Range(0, points.Count).ToList();
        var triangles = new List<int[]>();
        while (remaining.Count > 3)
        {
            int cut = -1;
            for (int i = 0; i < remaining.Count && cut < 0; i++)
                if (IsEar(points, remaining, i)) cut = i;
            if (cut < 0) cut = 0;   // only rounding can leave no ear
            int count = remaining.Count;
            triangles.Add([remaining[(cut + count - 1) % count], remaining[cut], remaining[(cut + 1) % count]]);
            remaining.RemoveAt(cut);
        }
        triangles.Add(remaining.ToArray());
        return triangles;
    }

    private static bool IsEar(List<Vector2> points, List<int> remaining, int at)
    {
        int count = remaining.Count;
        int before = remaining[(at + count - 1) % count], corner = remaining[at], after = remaining[(at + 1) % count];
        Vector2 a = points[before], b = points[corner], c = points[after];
        if (Cross(b - a, c - b) <= 0f) return false;
        foreach (int other in remaining)
        {
            if (other == before || other == corner || other == after) continue;
            var p = points[other];
            if (Cross(b - a, p - a) >= 0f && Cross(c - b, p - b) >= 0f && Cross(a - c, p - c) >= 0f) return false;
        }
        return true;
    }

    // Joins pieces across a shared edge whenever the result is still convex, so a shape has few pieces and few inner seams.
    private static List<int[]> Merge(List<Vector2> points, List<int[]> pieces)
    {
        for (bool joined = true; joined;)
        {
            joined = false;
            for (int a = 0; a < pieces.Count && !joined; a++)
                for (int b = a + 1; b < pieces.Count && !joined; b++)
                    if (Join(pieces[a], pieces[b]) is { } merged && IsConvex(points, merged))
                    {
                        pieces[a] = merged;
                        pieces.RemoveAt(b);
                        joined = true;
                    }
        }
        return pieces;
    }

    // Two counter-clockwise pieces as one loop, when they share an edge.
    private static int[]? Join(int[] a, int[] b)
    {
        for (int i = 0; i < a.Length; i++)
            for (int j = 0; j < b.Length; j++)
            {
                if (a[i] != b[(j + 1) % b.Length] || a[(i + 1) % a.Length] != b[j]) continue;
                var loop = new List<int>();
                for (int k = 1; k <= a.Length; k++) loop.Add(a[(i + k) % a.Length]);
                for (int k = 2; k < b.Length; k++) loop.Add(b[(j + k) % b.Length]);
                return loop.ToArray();
            }
        return null;
    }

    private static bool IsConvex(List<Vector2> points, int[] loop)
    {
        for (int i = 0; i < loop.Length; i++)
        {
            var a = points[loop[i]];
            var b = points[loop[(i + 1) % loop.Length]];
            var c = points[loop[(i + 2) % loop.Length]];
            if (Cross(b - a, c - b) < 0f) return false;
        }
        return true;
    }
}
