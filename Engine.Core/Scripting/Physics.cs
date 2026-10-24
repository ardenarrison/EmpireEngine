using System.Numerics;
using Engine.Core.Scene;

namespace Engine.Core.Scripting;

// What a raycast hit: the object and collider, where the ray touched it, the surface's direction there, and how far it was.
public readonly record struct RaycastHit(SceneObject Object, Collider Collider, Vector3 Point, Vector3 Normal, float Distance);

// Queries against the colliders of the scene that is playing, as in Unity's Physics class.
// Example: if (Physics.Raycast(Object.Position, forward, out var hit, 20f, ignore: Object)) Console.WriteLine(hit.Object.Name);
public static class Physics
{
    // Pull of gravity on rigidbodies with UseGravity, in units per second squared.
    public static Vector3 Gravity
    {
        get => SceneManager.World?.Physics.Gravity ?? new Vector3(0f, -9.81f, 0f);
        set { if (SceneManager.World is { } world) world.Physics.Gravity = value; }
    }

    // Shoots a ray from origin along direction and reports the nearest collider it hits within maxDistance.
    // The object `ignore` and its children are skipped, which keeps a shooter from hitting itself.
    // A ray that starts inside a box or sphere collider does not hit that collider.
    public static bool Raycast(Vector3 origin, Vector3 direction, out RaycastHit hit, float maxDistance = float.PositiveInfinity,
        SceneObject? ignore = null)
    {
        hit = default;
        return SceneManager.World is { } world && world.Physics.Raycast(world, origin, direction, out hit, maxDistance, ignore);
    }

    // The same when only a yes or no is needed.
    public static bool Raycast(Vector3 origin, Vector3 direction, float maxDistance = float.PositiveInfinity,
        SceneObject? ignore = null) =>
        Raycast(origin, direction, out _, maxDistance, ignore);

    // The same along a Ray, such as Input.GetMouseRay() to find what the mouse is over.
    public static bool Raycast(Ray ray, out RaycastHit hit, float maxDistance = float.PositiveInfinity, SceneObject? ignore = null) =>
        Raycast(ray.Origin, ray.Direction, out hit, maxDistance, ignore);

    public static bool Raycast(Ray ray, float maxDistance = float.PositiveInfinity, SceneObject? ignore = null) =>
        Raycast(ray.Origin, ray.Direction, out _, maxDistance, ignore);

    // Every collider the ray passes through within maxDistance, nearest first.
    public static RaycastHit[] RaycastAll(Vector3 origin, Vector3 direction, float maxDistance = float.PositiveInfinity,
        SceneObject? ignore = null) =>
        SceneManager.World is { } world
            ? world.Physics.RaycastAll(world, origin, direction, maxDistance, ignore).ToArray()
            : [];
}
