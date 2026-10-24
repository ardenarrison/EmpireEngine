using System.Numerics;
using Engine.Core.Scripting;

namespace Engine.Core.Scene;

// How AddForce and AddTorque use their value, as in Unity.
public enum ForceMode
{
    Force,           // a push over time, in newtons; heavier objects speed up less. Call every frame for a steady push
    Acceleration,    // a push over time that ignores mass
    Impulse,         // an instant kick, like a jump or a hit; heavier objects speed up less
    VelocityChange,  // an instant change of speed that ignores mass
}

// Makes an object move under physics while the scene plays: gravity, forces, and collisions with other colliders.
// The object needs a collider (on itself or on child objects) to collide. Without a Rigidbody, a collider is static.
public sealed class Rigidbody : Component
{
    public float Mass { get; set; } = 1f;              // kilograms
    public bool UseGravity { get; set; } = true;
    public bool IsKinematic { get; set; }             // moved only by scripts; pushes others but is never pushed
    public bool FreezeRotation { get; set; }          // collisions never turn the object
    public float Drag { get; set; } = 0.05f;          // slows movement, per second
    public float AngularDrag { get; set; } = 0.05f;   // slows spinning, per second

    // World-space velocity in units per second and spin in radians per second. Not saved with the scene.
    public Vector3 Velocity { get; set; }
    public Vector3 AngularVelocity { get; set; }

    // Collected from AddForce and AddTorque calls, and applied at the start of the next physics step.
    // Forces are multiplied by the frame's time when they are added, so a push called every frame from Update
    // is equally strong at any frame rate, however many physics steps a frame happens to take.
    internal Vector3 PendingImpulse;
    internal Vector3 PendingVelocity;
    internal Vector3 PendingAngularImpulse;
    internal Vector3 PendingAngularVelocity;

    // Pushes the object in a world direction. With the default ForceMode.Force, call it every frame for a steady push.
    public void AddForce(Vector3 force, ForceMode mode = ForceMode.Force)
    {
        switch (mode)
        {
            case ForceMode.Force: PendingImpulse += force * Time.deltaTime; break;
            case ForceMode.Acceleration: PendingVelocity += force * Time.deltaTime; break;
            case ForceMode.Impulse: PendingImpulse += force; break;
            case ForceMode.VelocityChange: PendingVelocity += force; break;
        }
    }

    public void AddForce(float x, float y, float z, ForceMode mode = ForceMode.Force) => AddForce(new Vector3(x, y, z), mode);

    // An instant kick, the same as AddForce(impulse, ForceMode.Impulse).
    public void AddImpulse(Vector3 impulse) => AddForce(impulse, ForceMode.Impulse);

    // Spins the object around a world axis; the axis length is the strength. Modes work as for AddForce.
    public void AddTorque(Vector3 torque, ForceMode mode = ForceMode.Force)
    {
        switch (mode)
        {
            case ForceMode.Force: PendingAngularImpulse += torque * Time.deltaTime; break;
            case ForceMode.Acceleration: PendingAngularVelocity += torque * Time.deltaTime; break;
            case ForceMode.Impulse: PendingAngularImpulse += torque; break;
            case ForceMode.VelocityChange: PendingAngularVelocity += torque; break;
        }
    }

    public void AddTorque(float x, float y, float z, ForceMode mode = ForceMode.Force) => AddTorque(new Vector3(x, y, z), mode);

    internal void ClearPending()
    {
        PendingImpulse = Vector3.Zero;
        PendingVelocity = Vector3.Zero;
        PendingAngularImpulse = Vector3.Zero;
        PendingAngularVelocity = Vector3.Zero;
    }
}

// Shared settings for every collider shape. Center is in the object's local space, so it moves and scales with it.
public abstract class Collider : Component
{
    public Vector3 Center { get; set; }
    public float Friction { get; set; } = 0.5f;     // 0 is ice; 1 is rubber
    public float Bounciness { get; set; }           // 0 stops dead; 1 bounces back at full speed
}

// A box of Size local units, centered on Center. Scales with the object.
public sealed class BoxCollider : Collider
{
    public Vector3 Size { get; set; } = Vector3.One;
}

// A ball of Radius local units around Center. Scales with the object's largest scale axis.
public sealed class SphereCollider : Collider
{
    public float Radius { get; set; } = 0.5f;
}

// Collides using a model's shape. On a static object or a kinematic rigidbody it uses the exact triangles, for ground,
// ramps and level geometry. On a moving rigidbody it uses the mesh's convex hull (the shape a shrink-wrap would make),
// so hollows and dents are filled in.
// Model is a path like "Models/Ramp.glb"; leave it empty to use the object's Mesh component.
public sealed class MeshCollider : Collider
{
    public string Model { get; set; } = "";
}
