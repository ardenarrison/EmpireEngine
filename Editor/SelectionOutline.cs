using System.Numerics;
using System.Runtime.CompilerServices;
using Brutal.Numerics;
using Engine.Core.Assets;
using Engine.Core.Scene;

namespace Editor;

// A glowing gold outline around the selected objects in the 3D view. An object with a model is outlined along its
// silhouette: the model's edges between a face turned toward the camera and one turned away (plus open edges), found
// once per model and re-tested against the camera every frame. Objects without a model get a box (a terrain) or a ring
// (cameras, lights, empty objects). The outline is drawn over the scene, so it stays visible behind other objects.
public sealed class SelectionOutline
{
    private const int MaxTriangles = 300_000;   // bigger models get a box instead
    private const int MaxEdgesDrawn = 8000;

    private static readonly float3 Gold = new(1f, 0.78f, 0.15f);

    private sealed class Silhouette
    {
        public required Vector3[] A, B;        // each edge's ends, in the model's space
        public required Vector3[] NormalA, NormalB;   // the two faces' normals, in the model's space (zero for an open edge)
        public required bool[] Open;           // an edge with a face on one side only
    }

    private readonly ConditionalWeakTable<GlbModel, Silhouette?> _cache = new();

    // Draws the outline of obj. primary is the object the Inspector shows; the rest of the selection is a little dimmer.
    public void Draw(Canvas2D draw, SceneObject obj, GlbModel? model, Terrain? terrain, bool primary, Matrix4x4 viewProj,
        float4 size, Vector3 eye, float seconds)
    {
        float strength = (primary ? 1f : 0.65f) * (0.88f + 0.12f * MathF.Sin(seconds * 3.2f));
        var world = obj.WorldMatrix;
        var segments = new List<(float2 A, float2 B)>();

        if (terrain is not null)
            BoxSegments(world, viewProj, size, new Vector3(-terrain.Width / 2f, 0f, -terrain.Length / 2f),
                        new Vector3(terrain.Width / 2f, terrain.Height, terrain.Length / 2f), segments);
        else if (model is not null && model.BoundsMax.X >= model.BoundsMin.X)
        {
            var silhouette = _cache.GetValue(model, Build);
            if (silhouette is null)
                BoxSegments(world, viewProj, size, model.BoundsMin, model.BoundsMax, segments);
            else if (Matrix4x4.Invert(world, out var toLocal))
                EdgeSegments(silhouette, world, Vector3.Transform(eye, toLocal), viewProj, size, segments);
        }
        else
        {
            // A marker: a ring around where the object is
            if (!TryProject(world.Translation, viewProj, size, out var center)) return;
            const int steps = 28;
            for (int i = 0; i < steps; i++)
            {
                float a0 = i * MathF.Tau / steps, a1 = (i + 1) * MathF.Tau / steps;
                segments.Add((center + new float2(MathF.Cos(a0), MathF.Sin(a0)) * 17f,
                              center + new float2(MathF.Cos(a1), MathF.Sin(a1)) * 17f));
            }
        }

        // From the widest and faintest to a thin bright core, so it reads as a glow.
        foreach (var (width, alpha, mix) in new[] { (11f, 0.06f, 0f), (7.5f, 0.11f, 0f), (4.5f, 0.24f, 0f), (2.6f, 0.8f, 0f), (1.3f, 1f, 0.55f) })
        {
            var color = new float4(Math.Min(1f, Gold.X + (1f - Gold.X) * mix), Math.Min(1f, Gold.Y + (1f - Gold.Y) * mix),
                                   Math.Min(1f, Gold.Z + (1f - Gold.Z) * mix), alpha * strength);
            foreach (var (a, b) in segments)
                draw.Line(a, b, width, color);
        }
    }

    private static void EdgeSegments(Silhouette s, Matrix4x4 world, Vector3 eyeLocal, Matrix4x4 viewProj, float4 size,
        List<(float2, float2)> into)
    {
        for (int i = 0; i < s.A.Length && into.Count < MaxEdgesDrawn; i++)
        {
            if (!s.Open[i])
            {
                bool facingA = Vector3.Dot(s.NormalA[i], eyeLocal - s.A[i]) > 0f;
                bool facingB = Vector3.Dot(s.NormalB[i], eyeLocal - s.A[i]) > 0f;
                if (facingA == facingB) continue;
            }
            if (TryProject(Vector3.Transform(s.A[i], world), viewProj, size, out var a) &&
                TryProject(Vector3.Transform(s.B[i], world), viewProj, size, out var b))
                into.Add((a, b));
        }
    }

    private static void BoxSegments(Matrix4x4 world, Matrix4x4 viewProj, float4 size, Vector3 min, Vector3 max,
        List<(float2, float2)> into)
    {
        var corners = new Vector3[8];
        for (int i = 0; i < 8; i++)
            corners[i] = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
        for (int i = 0; i < 8; i++)
            foreach (int bit in new[] { 1, 2, 4 })
                if ((i & bit) == 0 &&
                    TryProject(Vector3.Transform(corners[i], world), viewProj, size, out var a) &&
                    TryProject(Vector3.Transform(corners[i | bit], world), viewProj, size, out var b))
                    into.Add((a, b));
    }

    // Finds every edge of the model, with the normals of the (up to two) faces that meet there.
    private static Silhouette? Build(GlbModel model)
    {
        long triangles = 0;
        foreach (var m in model.Meshes) triangles += m.Indices.Length / 3;
        if (triangles == 0 || triangles > MaxTriangles) return null;

        var ids = new Dictionary<(float, float, float), int>();
        var edges = new Dictionary<(int, int), int>();   // edge -> index into the lists below
        var a = new List<Vector3>(); var b = new List<Vector3>();
        var normalA = new List<Vector3>(); var normalB = new List<Vector3>();
        var faces = new List<int>();

        foreach (var m in model.Meshes)
        {
            var p = m.Positions;
            int count = p.Length / 3;
            var points = new Vector3[count];
            var vertexIds = new int[count];
            for (int i = 0; i < count; i++)
            {
                points[i] = Vector3.Transform(new Vector3(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]), m.RestTransform);
                var key = (points[i].X, points[i].Y, points[i].Z);
                if (!ids.TryGetValue(key, out int id)) ids[key] = id = ids.Count;
                vertexIds[i] = id;
            }

            for (int t = 0; t + 2 < m.Indices.Length; t += 3)
            {
                uint i0 = m.Indices[t], i1 = m.Indices[t + 1], i2 = m.Indices[t + 2];
                if (i0 >= count || i1 >= count || i2 >= count) continue;
                var normal = Vector3.Cross(points[i1] - points[i0], points[i2] - points[i0]);
                if (normal.LengthSquared() < 1e-18f) continue;
                normal = Vector3.Normalize(normal);

                Add((int)i0, (int)i1);
                Add((int)i1, (int)i2);
                Add((int)i2, (int)i0);

                void Add(int from, int to)
                {
                    int u = vertexIds[from], v = vertexIds[to];
                    if (u == v) return;
                    var key = u < v ? (u, v) : (v, u);
                    if (edges.TryGetValue(key, out int e))
                    {
                        if (faces[e] == 1) normalB[e] = normal;
                        faces[e]++;
                    }
                    else
                    {
                        edges[key] = a.Count;
                        a.Add(points[from]); b.Add(points[to]);
                        normalA.Add(normal); normalB.Add(Vector3.Zero);
                        faces.Add(1);
                    }
                }
            }
        }

        if (a.Count == 0) return null;
        var open = new bool[a.Count];
        for (int i = 0; i < open.Length; i++) open[i] = faces[i] != 2;   // one face, or more than two: always outlined
        return new Silhouette { A = a.ToArray(), B = b.ToArray(), NormalA = normalA.ToArray(), NormalB = normalB.ToArray(), Open = open };
    }

    private static bool TryProject(Vector3 point, Matrix4x4 viewProj, float4 size, out float2 screen)
    {
        var clip = Vector4.Transform(new Vector4(point, 1f), viewProj);
        if (clip.W <= 0.01f)
        {
            screen = default;
            return false;
        }
        screen = new float2(size.X + (clip.X / clip.W * 0.5f + 0.5f) * size.Z, size.Y + (clip.Y / clip.W * 0.5f + 0.5f) * size.W);
        return true;
    }
}
