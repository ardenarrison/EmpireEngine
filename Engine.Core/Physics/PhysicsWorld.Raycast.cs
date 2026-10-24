using System.Numerics;
using Engine.Core.Scene;
using Engine.Core.Scripting;

namespace Engine.Core.Physics;

// Ray queries against the scene's colliders. Boxes and spheres are tested exactly; a mesh collider is tested against its
// actual triangles (even on a moving rigidbody, which collides as a convex hull), from both sides.
// As in Unity, a ray that starts inside a box or sphere does not hit it.
public sealed partial class PhysicsWorld
{
    // Every collider the ray hits within maxDistance, nearest first. direction need not be normalized.
    // The object `ignore` and everything under it are skipped, so a player can shoot past its own colliders.
    public List<RaycastHit> RaycastAll(Scene.Scene scene, Vector3 origin, Vector3 direction, float maxDistance = float.PositiveInfinity,
        SceneObject? ignore = null)
    {
        var hits = new List<RaycastHit>();
        float length = direction.Length();
        if (length < 1e-9f || !float.IsFinite(length) || !IsFinite(origin) || !(maxDistance > 0f)) return hits;
        var dir = direction / length;

        foreach (var obj in scene.All)
        {
            if (ignore is not null && IsSelfOrDescendant(obj, ignore)) continue;
            foreach (var collider in obj.Components.OfType<Collider>())
                if (RayCollider(collider, obj, origin, dir, maxDistance, out float t, out var normal))
                    hits.Add(new RaycastHit(obj, collider, origin + dir * t, normal, t));
        }
        hits.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return hits;
    }

    // The nearest collider the ray hits within maxDistance.
    public bool Raycast(Scene.Scene scene, Vector3 origin, Vector3 direction, out RaycastHit hit,
        float maxDistance = float.PositiveInfinity, SceneObject? ignore = null)
    {
        var all = RaycastAll(scene, origin, direction, maxDistance, ignore);
        hit = all.Count > 0 ? all[0] : default;
        return all.Count > 0;
    }

    private static bool IsSelfOrDescendant(SceneObject obj, SceneObject ancestor)
    {
        for (var o = obj; o is not null; o = o.Parent)
            if (o == ancestor) return true;
        return false;
    }

    private static bool RayCollider(Collider collider, SceneObject obj, Vector3 origin, Vector3 dir, float maxDistance,
        out float t, out Vector3 normal)
    {
        t = 0f;
        normal = default;
        var world = obj.WorldMatrix;
        switch (collider)
        {
            case SphereCollider sphere:
            {
                Decompose(world, out var scale, out _, out _);
                scale = Vector3.Abs(scale);
                float radius = MathF.Abs(sphere.Radius) * MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));
                var center = Vector3.Transform(sphere.Center, world);
                if (radius <= 0f || !RaySphere(origin, dir, center, radius, out t) || t > maxDistance) return false;
                normal = Vector3.Normalize(origin + dir * t - center);
                return true;
            }
            case BoxCollider box:
            {
                if (!Matrix4x4.Invert(world, out var inverse)) return false;
                var half = Vector3.Abs(box.Size) * 0.5f;
                var localOrigin = Vector3.Transform(origin, inverse);
                var localDir = Vector3.TransformNormal(dir, inverse);   // not normalized, so t stays in world units
                if (!RayBox(localOrigin, localDir, box.Center - half, box.Center + half, out t, out var localNormal) || t > maxDistance)
                    return false;
                normal = WorldNormal(localNormal, inverse);
                return true;
            }
            case Terrain terrain:
            {
                if (!terrain.Collide) return false;
                var terrainTree = Trees.GetValue(terrain.CollisionModel, TriangleTree.Build);
                if (terrainTree.Count == 0 || !Matrix4x4.Invert(world, out var terrainInverse)) return false;
                var terrainOrigin = Vector3.Transform(origin, terrainInverse);
                var terrainDir = Vector3.TransformNormal(dir, terrainInverse);
                if (!terrainTree.Raycast(terrainOrigin, terrainDir, maxDistance, out t, out int hitTriangle)) return false;
                var terrainNormal = Vector3.Cross(terrainTree.Vertices[hitTriangle * 3 + 1] - terrainTree.Vertices[hitTriangle * 3],
                    terrainTree.Vertices[hitTriangle * 3 + 2] - terrainTree.Vertices[hitTriangle * 3]);
                normal = WorldNormal(terrainNormal, terrainInverse);
                if (Vector3.Dot(normal, dir) > 0f) normal = -normal;
                return IsFinite(normal);
            }
            case MeshCollider mesh:
            {
                if (Shape.ModelFor(mesh, obj) is not { } model) return false;
                var tree = Trees.GetValue(model, TriangleTree.Build);
                var meshWorld = Matrix4x4.CreateTranslation(mesh.Center) * world;
                if (tree.Count == 0 || !Matrix4x4.Invert(meshWorld, out var inverse)) return false;
                var localOrigin = Vector3.Transform(origin, inverse);
                var localDir = Vector3.TransformNormal(dir, inverse);
                if (!tree.Raycast(localOrigin, localDir, maxDistance, out t, out int tri)) return false;
                var localNormal = Vector3.Cross(tree.Vertices[tri * 3 + 1] - tree.Vertices[tri * 3],
                    tree.Vertices[tri * 3 + 2] - tree.Vertices[tri * 3]);
                normal = WorldNormal(localNormal, inverse);
                if (Vector3.Dot(normal, dir) > 0f) normal = -normal;   // face the ray, whichever side it hit
                return IsFinite(normal);
            }
        }
        return false;
    }

    // A local-space normal in world space: normals move by the inverse transpose of the world matrix.
    private static Vector3 WorldNormal(Vector3 local, Matrix4x4 worldToLocal)
    {
        var n = Vector3.TransformNormal(local, Matrix4x4.Transpose(worldToLocal));
        return n.LengthSquared() > 1e-18f ? Vector3.Normalize(n) : Vector3.UnitY;
    }

    private static bool RaySphere(Vector3 origin, Vector3 dir, Vector3 center, float radius, out float t)
    {
        t = 0f;
        var m = origin - center;
        float c = Vector3.Dot(m, m) - radius * radius;
        if (c <= 0f) return false;                       // starts inside
        float b = Vector3.Dot(m, dir);
        if (b > 0f) return false;                        // pointing away
        float disc = b * b - c;
        if (disc < 0f) return false;
        t = -b - MathF.Sqrt(disc);
        return t >= 0f;
    }

    // Slab test against a box, giving the nearest entry and the face it enters through. A start inside does not hit.
    private static bool RayBox(Vector3 origin, Vector3 dir, Vector3 min, Vector3 max, out float t, out Vector3 normal)
    {
        t = 0f;
        normal = default;
        float tEnter = float.NegativeInfinity, tExit = float.PositiveInfinity;
        int axis = -1;
        float sign = 0f;
        for (int i = 0; i < 3; i++)
        {
            float o = Get(origin, i), d = Get(dir, i), lo = Get(min, i), hi = Get(max, i);
            if (MathF.Abs(d) < 1e-12f)
            {
                if (o < lo || o > hi) return false;     // parallel and outside this slab
                continue;
            }
            float t0 = (lo - o) / d, t1 = (hi - o) / d;
            float near = MathF.Min(t0, t1), far = MathF.Max(t0, t1);
            if (near > tEnter)
            {
                tEnter = near;
                axis = i;
                sign = d > 0f ? -1f : 1f;               // entering through the face the ray travels toward
            }
            tExit = MathF.Min(tExit, far);
            if (tEnter > tExit) return false;
        }
        if (axis < 0 || tEnter < 0f) return false;      // starts inside, or no usable slab
        t = tEnter;
        normal = axis switch { 0 => new Vector3(sign, 0, 0), 1 => new Vector3(0, sign, 0), _ => new Vector3(0, 0, sign) };
        return true;
    }

    private static float Get(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    // Does the ray reach the box within maxT? (For walking the triangle tree; a start inside counts.)
    private static bool RayHitsBox(Vector3 origin, Vector3 dir, Vector3 min, Vector3 max, float maxT)
    {
        float tEnter = 0f, tExit = maxT;
        for (int i = 0; i < 3; i++)
        {
            float o = Get(origin, i), d = Get(dir, i), lo = Get(min, i), hi = Get(max, i);
            if (MathF.Abs(d) < 1e-12f)
            {
                if (o < lo || o > hi) return false;
                continue;
            }
            float t0 = (lo - o) / d, t1 = (hi - o) / d;
            tEnter = MathF.Max(tEnter, MathF.Min(t0, t1));
            tExit = MathF.Min(tExit, MathF.Max(t0, t1));
            if (tEnter > tExit) return false;
        }
        return true;
    }

    // Moller-Trumbore, counting both faces.
    private static bool RayTriangle(Vector3 origin, Vector3 dir, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0f;
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(dir, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f) return false;
        float inv = 1f / det;
        var s = origin - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f) return false;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(dir, q) * inv;
        if (v < 0f || u + v > 1f) return false;
        t = Vector3.Dot(e2, q) * inv;
        return t >= 0f;
    }
}
