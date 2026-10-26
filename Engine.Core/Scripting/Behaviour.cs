using System;
using System.Collections.Generic;
using System.Numerics;
using Engine.Core.Scene;

namespace Engine.Core.Scripting;

// Base class for user scripts. A script is a .cs file in the project's Scripts folder whose class has the same name.
// Attach it to an object in the Inspector. The methods below act on that object.
public abstract class Behaviour
{
    private sealed class Routine
    {
        public required IEnumerator<Wait> Enumerator { get; init; }
        public Wait? Current;
    }

    private readonly List<Routine> _routines = new();
    private float _timer;

    public SceneObject Object { get; internal set; } = null!;

    // The object this script is attached to, as in sceneObject.transform.position. The same object as Object.
    public SceneObject sceneObject => Object;

    // Destroys an object and everything under it, at the end of this frame or after delay seconds. Any script can destroy any
    // object it has a reference to, including its own: Destroy(other), Destroy(sceneObject), Destroy(enemy, 2f).
    public void Destroy(SceneObject? target, float delay = 0f) => Scene.Scene.Destroy(target, delay);

    // Called once, before the first Update.
    public virtual void Start() { }

    // Called every frame while the scene plays.
    public virtual void Update() { }

    // Called after SaveGame.Load has put the saved scene back, on scripts whose Start had already run when the game
    // was saved. Their public fields are restored; use this to rebuild anything else, like a cached reference.
    public virtual void OnGameLoaded() { }

    // Collisions as the visual script blocks read them. RecordCollision is called by the engine; a collision that started or
    // ended is visible to CollisionEnter / CollisionExit during the script's next Update, and Colliding is true for as long
    // as the two are touching. Objects are matched by name.
    private readonly HashSet<SceneObject> _touching = new();
    private List<SceneObject> _enterPending = new(), _exitPending = new(), _enterNow = new(), _exitNow = new();

    internal void RecordCollision(SceneObject other, bool enter)
    {
        if (enter)
        {
            _touching.Add(other);
            _enterPending.Add(other);
        }
        else
        {
            _touching.Remove(other);
            _exitPending.Add(other);
        }
    }

    protected bool CollisionEnter(string objectName) => _enterNow.Any(o => o.Name == objectName);
    protected bool CollisionExit(string objectName) => _exitNow.Any(o => o.Name == objectName);
    protected bool Colliding(string objectName) => _touching.Any(o => o.Name == objectName && StillInScene(o));

    // An object that was destroyed while touching this one never sends an end of collision, so it is checked for here.
    private static bool StillInScene(SceneObject obj) => SceneManager.World?.Contains(obj) ?? true;

    // Called once when this object is destroyed with Destroy, just before it leaves the scene (children go with their parent,
    // and each gets it too). For scripts that had started. Not called when a scene is unloaded or the game stops.
    public virtual void OnDestroy() { }

    // Destroys every object in the scene with exactly this name, as the visual code block "destroy [object]" does. Does
    // nothing when there is none.
    public void DestroyObjectsNamed(string objectName)
    {
        foreach (var target in Scene.Scene.FindObjectsWithName(objectName))
            Destroy(target);
    }

    // Called when this object's colliders start touching another object's, and when they stop.
    public virtual void OnCollisionEnter(SceneObject other) { }
    public virtual void OnCollisionExit(SceneObject other) { }

    // The first component of type T on this object, e.g. GetComponent<Rigidbody>(). Null when there is none.
    public T? GetComponent<T>() where T : Component => Object.Components.OfType<T>().FirstOrDefault();

    // This object's Rigidbody, as in this.rigidbody.AddForce(0, 10, 0). Stops the script with a clear message when
    // the object has none; use GetComponent<Rigidbody>() to check first.
    public Rigidbody rigidbody => GetComponent<Rigidbody>() ??
        throw new InvalidOperationException($"'{Object.Name}' has no Rigidbody; add one in the Inspector");

    // This object's AudioSource, as in this.audioSource.Play(). Stops the script with a clear message when the object has none.
    public AudioSource audioSource
    {
        get
        {
            var source = GetComponent<AudioSource>() ??
                throw new InvalidOperationException($"'{Object.Name}' has no Audio Source; add one in the Inspector");
            source.Host ??= Object;
            return source;
        }
    }

    // The same as audioSource.
    public AudioSource AudioSource => audioSource;

    // This object's Animator, as in this.animator.Play("Walk"). Stops the script with a clear message when the object has none.
    public Animator animator
    {
        get
        {
            var found = GetComponent<Animator>() ??
                throw new InvalidOperationException($"'{Object.Name}' has no Animator; add one in the Inspector");
            found.Host ??= Object;
            return found;
        }
    }

    // The same as animator.
    public Animator Animator => animator;

    // The same as rigidbody, for scripts that write this.Rigidbody.
    public Rigidbody Rigidbody => rigidbody;

    // Moves the object by x, y, z in world units.
    public void Translate(float x, float y, float z) => Object.Position += new Vector3(x, y, z);

    // Turns the object by x, y, z degrees around the X, Y and Z axes.
    public void Rotate(float x, float y, float z)
    {
        var turn = Quaternion.CreateFromYawPitchRoll(Degrees(y), Degrees(x), Degrees(z));
        Object.Rotation = Quaternion.Normalize(turn * Object.Rotation);
    }

    // Multiplies the object's size by x, y, z. Scale(2, 2, 2) doubles it; Scale(1, 1, 1) changes nothing.
    public void Scale(float x, float y, float z) =>
        Object.Scale = new Vector3(Object.Scale.X * x, Object.Scale.Y * y, Object.Scale.Z * z);

    private static float Degrees(float degrees) => degrees * MathF.PI / 180f;

    // ---- Helpers used by visual scripts ----

    protected Vector3 Position
    {
        get => Object.Position;
        set => Object.Position = value;
    }

    // Seconds since this script's timer was last reset.
    protected float Timer => _timer;

    protected void ResetTimer() => _timer = 0f;

    protected void Spawn(IEnumerable<Wait> routine) =>
        _routines.Add(new Routine { Enumerator = routine.GetEnumerator() });

    // Runs every running coroutine. Call once per frame from Update.
    protected void TickRoutines()
    {
        // What happened since the last tick is what the collision blocks see during this one.
        (_enterNow, _enterPending) = (_enterPending, _enterNow);
        (_exitNow, _exitPending) = (_exitPending, _exitNow);
        _enterPending.Clear();
        _exitPending.Clear();
        _touching.RemoveWhere(o => !StillInScene(o));

        float dt = Time.deltaTime;
        _timer += dt;
        for (int i = 0; i < _routines.Count;)
        {
            var r = _routines[i];
            if (r.Current is { } wait)
            {
                if (!wait.Update(dt))
                {
                    i++;
                    continue;
                }
                r.Current = null;
            }

            if (!r.Enumerator.MoveNext())
            {
                _routines.RemoveAt(i);
                continue;
            }

            r.Current = r.Enumerator.Current;
            i++;
        }
    }

    protected Wait WaitSeconds(float seconds) => new TimerWait(seconds);
    protected Wait WaitUntil(Func<bool> condition) => new PredicateWait(condition);
    protected Wait GlideTo(Vector3 target, float seconds) =>
        new GlideWait(() => Position, p => Position = p, target, seconds);

    protected void MoveForward(float distance) =>
        Position += Vector3.Transform(-Vector3.UnitZ, Object.Rotation) * distance;

    protected void Turn(Vector3 axis, float degrees) =>
        Object.Rotation = Quaternion.Normalize(
            Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), Degrees(degrees)) * Object.Rotation);

    // True while the mouse button ("Left", "Right" or "Middle") is held. Used by the visual script mouse blocks.
    protected static bool MouseDown(string button) => Input.GetMouseButton(button);

    public static float RandomRange(float min, float max) => min + (max - min) * (float)Random.Shared.NextDouble();
    public static float SafeDivide(float a, float b) => b == 0f ? 0f : a / b;
    public static bool Approximately(float a, float b) => MathF.Abs(a - b) < 1e-5f;
}
