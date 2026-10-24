using System.Numerics;
using System.Text.Json.Serialization;
using Engine.Core.Rendering;

namespace Engine.Core.Scene;

public enum ParticleSimulationSpace { Local, World }   // Local: particles move with the object. World: they stay where they were born
public enum ParticleShapeType { Cone, Sphere, Hemisphere, Box, Circle, Point }
public enum ParticleBlend { Alpha, Additive }          // Additive brightens what is behind: fire, sparks, magic
public enum ParticleSprite { SoftCircle, Circle, Square }

// A value picked at random between Min and Max for each particle. Set both the same for a constant.
public sealed class MinMax
{
    public float Min { get; set; }
    public float Max { get; set; }

    public MinMax() { }
    public MinMax(float value) : this(value, value) { }
    public MinMax(float min, float max) { Min = min; Max = max; }

    public static implicit operator MinMax(float value) => new(value);

    internal float Pick(Random random) => Min + (Max - Min) * random.NextSingle();
}

public sealed class ParticleMainModule
{
    public float Duration { get; set; } = 5f;           // seconds in one cycle of emission
    public bool Looping { get; set; } = true;
    public bool Prewarm { get; set; }                   // a looping system starts as if it had already run one cycle
    public float StartDelay { get; set; }
    public MinMax StartLifetime { get; set; } = 5f;     // seconds each particle lives
    public MinMax StartSpeed { get; set; } = 5f;        // units per second, along the shape's direction
    public MinMax StartSize { get; set; } = 1f;         // world units across
    public MinMax StartRotation { get; set; } = 0f;     // degrees
    public Vector4 StartColor { get; set; } = Vector4.One;
    public Vector4 StartColor2 { get; set; } = Vector4.One;
    public bool RandomBetweenTwoColors { get; set; }    // each particle gets a color between StartColor and StartColor2
    public float GravityModifier { get; set; }          // 1 falls like a rigidbody; 0 floats
    public ParticleSimulationSpace SimulationSpace { get; set; } = ParticleSimulationSpace.Local;
    public float SimulationSpeed { get; set; } = 1f;
    public bool PlayOnAwake { get; set; } = true;
    public int MaxParticles { get; set; } = 1000;
}

public sealed class ParticleEmissionModule
{
    public bool Enabled { get; set; } = true;
    public float RateOverTime { get; set; } = 10f;      // particles per second
    public List<ParticleBurst> Bursts { get; set; } = new();
}

// Count particles at once, Time seconds into each cycle, repeated Cycles times Interval seconds apart.
public sealed class ParticleBurst
{
    public float Time { get; set; }
    public int Count { get; set; } = 30;
    public int Cycles { get; set; } = 1;
    public float Interval { get; set; } = 0.01f;
}

// Where particles start and which way they go. Shapes point along the object's +Y axis.
public sealed class ParticleShapeModule
{
    public bool Enabled { get; set; } = true;
    public ParticleShapeType Shape { get; set; } = ParticleShapeType.Cone;
    public float Angle { get; set; } = 25f;             // cone spread in degrees
    public float Radius { get; set; } = 1f;             // cone base, sphere, hemisphere and circle
    public Vector3 BoxSize { get; set; } = Vector3.One;
    public bool EmitFromShell { get; set; }             // sphere and hemisphere: start on the surface, not inside
    public Vector3 Position { get; set; }               // offset from the object, in its local space
    public float RandomizeDirection { get; set; }       // 0 follows the shape, 1 is fully random
}

public sealed class ParticleVelocityModule
{
    public bool Enabled { get; set; }
    public Vector3 Linear { get; set; }                 // extra velocity in the simulation space, e.g. wind
}

public sealed class ParticleLimitVelocityModule
{
    public bool Enabled { get; set; }
    public float Drag { get; set; } = 1f;               // slows particles down, per second
    public float SpeedLimit { get; set; }               // 0 for no limit
}

// Each particle's color is multiplied by a blend from StartColor to EndColor over its life.
public sealed class ParticleColorModule
{
    public bool Enabled { get; set; }
    public Vector4 StartColor { get; set; } = Vector4.One;
    public Vector4 EndColor { get; set; } = new(1f, 1f, 1f, 0f);   // fades out by default
}

// Each particle's size is multiplied by a blend from StartSize to EndSize over its life.
public sealed class ParticleSizeModule
{
    public bool Enabled { get; set; }
    public float StartSize { get; set; } = 1f;
    public float EndSize { get; set; }
}

public sealed class ParticleRotationModule
{
    public bool Enabled { get; set; }
    public MinMax AngularVelocity { get; set; } = 45f;  // degrees per second
}

// Particles bounce off a flat ground at a world height.
public sealed class ParticleCollisionModule
{
    public bool Enabled { get; set; }
    public float GroundHeight { get; set; }
    public float Bounce { get; set; } = 0.5f;           // share of the speed into the ground kept as bounce
    public float Dampen { get; set; }                   // share of the sliding speed lost on each hit
    public float LifetimeLoss { get; set; }             // share of the lifetime used up on each hit
}

public sealed class ParticleRendererModule
{
    public ParticleBlend Blend { get; set; } = ParticleBlend.Alpha;
    public ParticleSprite Sprite { get; set; } = ParticleSprite.SoftCircle;
    public bool SortByDistance { get; set; } = true;    // alpha particles draw back to front so they overlap correctly
}

// Emits and animates particles, drawn as camera-facing sprites. Works on an empty object; settings follow
// Unity's particle system modules. Scripts can call Play, Stop, Pause, Clear and Emit.
public sealed class ParticleSystem : Component
{
    public ParticleMainModule Main { get; set; } = new();
    public ParticleEmissionModule Emission { get; set; } = new();
    public ParticleShapeModule Shape { get; set; } = new();
    public ParticleVelocityModule VelocityOverLifetime { get; set; } = new();
    public ParticleLimitVelocityModule LimitVelocityOverLifetime { get; set; } = new();
    public ParticleColorModule ColorOverLifetime { get; set; } = new();
    public ParticleSizeModule SizeOverLifetime { get; set; } = new();
    public ParticleRotationModule RotationOverLifetime { get; set; } = new();
    public ParticleCollisionModule Collision { get; set; } = new();
    public ParticleRendererModule Renderer { get; set; } = new();

    private struct Particle
    {
        public Vector3 Position, Velocity;     // in the simulation space
        public float Age, Lifetime, Size, Rotation, AngularVelocity;
        public Vector4 Color;
    }

    private Particle[] _particles = new Particle[64];
    private int _count;
    private bool _started, _emitting, _paused;
    private float _time;           // seconds since Play, including the start delay
    private float _emitDebt;       // fractional particles owed by the emission rate
    private int _cycle;
    private int[] _burstsFired = [];
    private int _pendingEmit;
    private Random _random = new();

    // True while emitting or while any particle is still alive.
    [JsonIgnore] public bool IsPlaying => !_paused && (_emitting || _count > 0);
    [JsonIgnore] public bool IsPaused => _paused;
    [JsonIgnore] public bool IsEmitting => _emitting && !_paused;
    [JsonIgnore] public int ParticleCount => _count;
    [JsonIgnore] public float Time => _time;

    public void Play()
    {
        _started = true;
        if (_paused)
        {
            _paused = false;
            return;
        }
        if (_emitting) return;
        _emitting = true;
        _time = 0f;
        _emitDebt = 0f;
        _cycle = 0;
        _burstsFired = new int[Emission.Bursts.Count];
        _prewarmPending = Main.Prewarm && Main.Looping;
    }

    // Stops emitting. Live particles finish their lives unless clear is true.
    public void Stop(bool clear = false)
    {
        _started = true;
        _emitting = false;
        _paused = false;
        if (clear) _count = 0;
    }

    public void Pause()
    {
        if (_emitting || _count > 0) _paused = true;
    }

    public void Clear() => _count = 0;

    public void Restart()
    {
        Stop(clear: true);
        Play();
    }

    // Emits count particles at once, on the next update, whether or not the system is playing.
    public void Emit(int count) => _pendingEmit += Math.Max(0, count);

    // Back to the state before the first update, so PlayOnAwake applies again (used when play mode starts).
    internal void ResetRuntime()
    {
        _count = 0;
        _started = _emitting = _paused = false;
        _pendingEmit = 0;
        _random = new Random();
    }

    private bool _prewarmPending;

    // ---- simulation ------------------------------------------------------------------------

    internal void Simulate(SceneObject obj, float dt, Vector3 gravity)
    {
        if (!_started)
        {
            _started = true;
            if (Main.PlayOnAwake) Play();
        }
        if (_prewarmPending)
        {
            _prewarmPending = false;
            const float step = 1f / 30f;
            for (float t = 0f; t < Main.Duration; t += step)
                Step(obj, step, gravity);
        }
        if (_paused) return;
        Step(obj, dt * MathF.Max(0f, Main.SimulationSpeed), gravity);
    }

    private void Step(SceneObject obj, float dt, Vector3 gravity)
    {
        var world = obj.WorldMatrix;
        bool local = Main.SimulationSpace == ParticleSimulationSpace.Local;
        Matrix4x4.Invert(world, out var toLocal);

        UpdateParticles(dt, world, toLocal, local, gravity);

        if (_pendingEmit > 0)
        {
            int n = _pendingEmit;
            _pendingEmit = 0;
            for (int i = 0; i < n; i++) Spawn(world, local, 0f);
        }

        if (!_emitting || dt <= 0f) return;
        _time += dt;
        float t = _time - Main.StartDelay;
        if (t < 0f) return;

        float duration = MathF.Max(Main.Duration, 0.01f);
        if (!Main.Looping && t > duration)
        {
            t = duration;
            _emitting = false;
        }
        int cycle = Main.Looping ? (int)(t / duration) : 0;
        if (cycle != _cycle)
        {
            _cycle = cycle;
            _burstsFired = new int[Emission.Bursts.Count];
        }
        float cycleTime = Main.Looping ? t - cycle * duration : t;

        if (!Emission.Enabled) return;

        // Rate over time. Particles born during this step are spread across it, so a fast emitter looks even.
        float frameDt = MathF.Min(dt, t);
        _emitDebt += MathF.Max(0f, Emission.RateOverTime) * frameDt;
        int due = (int)_emitDebt;
        _emitDebt -= due;
        for (int i = 0; i < due; i++)
            Spawn(world, local, frameDt * (i + 0.5f) / due);

        if (_burstsFired.Length != Emission.Bursts.Count)
            _burstsFired = new int[Emission.Bursts.Count];
        for (int b = 0; b < Emission.Bursts.Count; b++)
        {
            var burst = Emission.Bursts[b];
            while (_burstsFired[b] < Math.Max(1, burst.Cycles) &&
                   cycleTime >= burst.Time + _burstsFired[b] * MathF.Max(0f, burst.Interval))
            {
                _burstsFired[b]++;
                for (int i = 0; i < burst.Count; i++) Spawn(world, local, 0f);
            }
        }
    }

    private void UpdateParticles(float dt, Matrix4x4 world, Matrix4x4 toLocal, bool local, Vector3 gravity)
    {
        var g = gravity * Main.GravityModifier;
        if (local) g = Vector3.TransformNormal(g, toLocal);
        var linear = VelocityOverLifetime.Enabled ? VelocityOverLifetime.Linear : Vector3.Zero;

        for (int i = 0; i < _count;)
        {
            ref var p = ref _particles[i];
            p.Age += dt;
            if (p.Age >= p.Lifetime)
            {
                _particles[i] = _particles[--_count];   // order does not matter; drawing sorts if it needs to
                continue;
            }

            p.Velocity += g * dt;
            if (LimitVelocityOverLifetime.Enabled)
            {
                p.Velocity /= 1f + dt * MathF.Max(0f, LimitVelocityOverLifetime.Drag);
                float limit = LimitVelocityOverLifetime.SpeedLimit;
                if (limit > 0f && p.Velocity.LengthSquared() > limit * limit)
                    p.Velocity = Vector3.Normalize(p.Velocity) * limit;
            }
            p.Position += (p.Velocity + linear) * dt;
            p.Rotation += p.AngularVelocity * dt;

            if (Collision.Enabled) Collide(ref p, world, toLocal, local);
            i++;
        }
    }

    private void Collide(ref Particle p, Matrix4x4 world, Matrix4x4 toLocal, bool local)
    {
        var position = local ? Vector3.Transform(p.Position, world) : p.Position;
        if (position.Y >= Collision.GroundHeight) return;
        var velocity = local ? Vector3.TransformNormal(p.Velocity, world) : p.Velocity;
        if (velocity.Y >= 0f) return;

        position.Y = Collision.GroundHeight;
        float keep = 1f - Math.Clamp(Collision.Dampen, 0f, 1f);
        velocity = new Vector3(velocity.X * keep, -velocity.Y * Math.Clamp(Collision.Bounce, 0f, 1f), velocity.Z * keep);
        p.Age += p.Lifetime * Math.Clamp(Collision.LifetimeLoss, 0f, 1f);

        p.Position = local ? Vector3.Transform(position, toLocal) : position;
        p.Velocity = local ? Vector3.TransformNormal(velocity, toLocal) : velocity;
    }

    private void Spawn(Matrix4x4 world, bool local, float preAge)
    {
        if (_count >= Math.Max(0, Main.MaxParticles)) return;
        if (_count == _particles.Length)
            Array.Resize(ref _particles, Math.Min(_particles.Length * 2, Math.Max(64, Main.MaxParticles)));

        SampleShape(out var position, out var direction);
        float speed = Main.StartSpeed.Pick(_random);
        if (!local)
        {
            position = Vector3.Transform(position, world);
            var d = Vector3.TransformNormal(direction, world);
            direction = d.LengthSquared() > 1e-12f ? Vector3.Normalize(d) : direction;
        }

        var color = Main.RandomBetweenTwoColors
            ? Vector4.Lerp(Main.StartColor, Main.StartColor2, _random.NextSingle())
            : Main.StartColor;
        var particle = new Particle
        {
            Position = position,
            Velocity = direction * speed,
            Lifetime = MathF.Max(0.01f, Main.StartLifetime.Pick(_random)),
            Size = MathF.Max(0f, Main.StartSize.Pick(_random)),
            Rotation = Main.StartRotation.Pick(_random) * MathF.PI / 180f,
            AngularVelocity = RotationOverLifetime.Enabled ? RotationOverLifetime.AngularVelocity.Pick(_random) * MathF.PI / 180f : 0f,
            Color = color,
        };
        // Born partway through this step: move it along as if it had been alive for preAge seconds.
        particle.Age = preAge;
        particle.Position += particle.Velocity * preAge;
        _particles[_count++] = particle;
    }

    // A start point and direction in the object's local space. Every shape points along +Y.
    private void SampleShape(out Vector3 position, out Vector3 direction)
    {
        var s = Shape;
        position = Vector3.Zero;
        direction = Vector3.UnitY;
        if (s.Enabled)
        {
            float radius = MathF.Max(0f, s.Radius);
            switch (s.Shape)
            {
                case ParticleShapeType.Sphere:
                case ParticleShapeType.Hemisphere:
                {
                    direction = RandomUnit();
                    if (s.Shape == ParticleShapeType.Hemisphere) direction.Y = MathF.Abs(direction.Y);
                    float r = s.EmitFromShell ? radius : radius * MathF.Cbrt(_random.NextSingle());
                    position = direction * r;
                    break;
                }
                case ParticleShapeType.Cone:
                {
                    // A point on the base disc; particles further out tilt further out, up to the cone angle.
                    float spread = Math.Clamp(s.Angle, 0f, 90f) * MathF.PI / 180f;
                    float around = _random.NextSingle() * MathF.Tau;
                    float out01 = MathF.Sqrt(_random.NextSingle());
                    var radial = new Vector3(MathF.Cos(around), 0f, MathF.Sin(around));
                    position = radial * (radius * out01);
                    float tilt = radius > 0f ? spread * out01 : MathF.Acos(1f - _random.NextSingle() * (1f - MathF.Cos(spread)));
                    direction = Vector3.UnitY * MathF.Cos(tilt) + radial * MathF.Sin(tilt);
                    break;
                }
                case ParticleShapeType.Box:
                    position = (new Vector3(_random.NextSingle(), _random.NextSingle(), _random.NextSingle()) - new Vector3(0.5f)) * s.BoxSize;
                    break;
                case ParticleShapeType.Circle:
                {
                    float around = _random.NextSingle() * MathF.Tau;
                    var radial = new Vector3(MathF.Cos(around), 0f, MathF.Sin(around));
                    position = radial * (radius * MathF.Sqrt(_random.NextSingle()));
                    direction = radial;
                    break;
                }
            }
            if (s.RandomizeDirection > 0f)
            {
                var mixed = Vector3.Lerp(direction, RandomUnit(), Math.Clamp(s.RandomizeDirection, 0f, 1f));
                if (mixed.LengthSquared() > 1e-8f) direction = Vector3.Normalize(mixed);
            }
            position += s.Position;
        }
    }

    private Vector3 RandomUnit()
    {
        float y = _random.NextSingle() * 2f - 1f;
        float around = _random.NextSingle() * MathF.Tau;
        float r = MathF.Sqrt(MathF.Max(0f, 1f - y * y));
        return new Vector3(MathF.Cos(around) * r, y, MathF.Sin(around) * r);
    }

    // ---- drawing -----------------------------------------------------------------------------

    // This system's live particles as world-space sprites, back to front for alpha blending when enabled.
    internal void Collect(SceneObject obj, Vector3 eye, List<ParticleInstance> output)
    {
        int start = output.Count;
        var world = obj.WorldMatrix;
        bool local = Main.SimulationSpace == ParticleSimulationSpace.Local;
        for (int i = 0; i < _count; i++)
        {
            ref var p = ref _particles[i];
            float t = p.Age / p.Lifetime;
            var color = p.Color;
            if (ColorOverLifetime.Enabled)
                color *= Vector4.Lerp(ColorOverLifetime.StartColor, ColorOverLifetime.EndColor, t);
            float size = p.Size;
            if (SizeOverLifetime.Enabled)
                size *= SizeOverLifetime.StartSize + (SizeOverLifetime.EndSize - SizeOverLifetime.StartSize) * t;
            if (size <= 0f || color.W <= 0f) continue;
            output.Add(new ParticleInstance
            {
                Position = local ? Vector3.Transform(p.Position, world) : p.Position,
                Size = size,
                Color = color,
                Rotation = p.Rotation,
            });
        }

        if (Renderer.Blend == ParticleBlend.Alpha && Renderer.SortByDistance && output.Count - start > 1)
            output.Sort(start, output.Count - start,
                Comparer<ParticleInstance>.Create((a, b) =>
                    Vector3.DistanceSquared(b.Position, eye).CompareTo(Vector3.DistanceSquared(a.Position, eye))));
    }
}

// Runs and draws every particle system in a scene. The editor previews them while editing; play mode and built
// games run them through SceneUpdate.
public static class Particles
{
    public static void Update(Scene scene, float dt)
    {
        foreach (var obj in scene.All)
            foreach (var system in obj.Components.OfType<ParticleSystem>())
                system.Simulate(obj, dt, scene.Physics.Gravity);
    }

    // Clears every system so play mode starts fresh and PlayOnAwake applies again.
    public static void Reset(Scene scene)
    {
        foreach (var system in scene.All.SelectMany(o => o.Components.OfType<ParticleSystem>()))
            system.ResetRuntime();
    }

    public static void Draw(Scene scene, IRenderer renderer, Vector3 eye)
    {
        var instances = new List<ParticleInstance>();
        foreach (var obj in scene.All)
            foreach (var system in obj.Components.OfType<ParticleSystem>())
            {
                instances.Clear();
                system.Collect(obj, eye, instances);
                if (instances.Count > 0)
                    renderer.DrawParticles(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(instances),
                        system.Renderer.Blend, system.Renderer.Sprite);
            }
    }
}
