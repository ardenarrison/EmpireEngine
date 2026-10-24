using System.Numerics;
using Engine.Core.Serialization;

namespace Engine.Core.Scene;

// A ready-made effect: every module of a particle system set up to look like fire, smoke and so on. Applying one
// replaces the system's settings; everything can still be changed afterwards. Sizes and speeds are for an object at
// scale 1 (in meters), so scale the object to make the effect bigger.
public sealed record ParticlePreset(string Name, string Description, Func<ParticleSystem> Create)
{
    // Replaces every module of the system with the preset's, then plays it from the start.
    public void Apply(ParticleSystem target)
    {
        var fresh = Create();
        target.Main = fresh.Main;
        target.Emission = fresh.Emission;
        target.Shape = fresh.Shape;
        target.VelocityOverLifetime = fresh.VelocityOverLifetime;
        target.LimitVelocityOverLifetime = fresh.LimitVelocityOverLifetime;
        target.ColorOverLifetime = fresh.ColorOverLifetime;
        target.SizeOverLifetime = fresh.SizeOverLifetime;
        target.RotationOverLifetime = fresh.RotationOverLifetime;
        target.Collision = fresh.Collision;
        target.Renderer = fresh.Renderer;
        target.Restart();
    }

    // True when the system's settings are exactly the preset's.
    public bool Matches(ParticleSystem system) =>
        SceneSerializer.ParticleJson(system) == SceneSerializer.ParticleJson(Create());

    // A new particle system with the preset applied.
    public ParticleSystem CreateSystem() => Create();
}

public static class ParticlePresets
{
    public static IReadOnlyList<ParticlePreset> All { get; } = new[]
    {
        new ParticlePreset("Fire", "A steady flame: bright, additive and always burning.", () => new ParticleSystem
        {
            Main =
            {
                Duration = 1f, Looping = true, Prewarm = true,
                StartLifetime = new MinMax(0.5f, 0.9f),
                StartSpeed = new MinMax(1.2f, 2.0f),
                StartSize = new MinMax(0.5f, 0.9f),
                StartRotation = new MinMax(0f, 360f),
                StartColor = new Vector4(1f, 0.45f, 0.08f, 1f),
                StartColor2 = new Vector4(1f, 0.75f, 0.2f, 1f),
                RandomBetweenTwoColors = true,
                GravityModifier = -0.1f,                                   // flames drift upward
                SimulationSpace = ParticleSimulationSpace.Local,           // stays with the object
                MaxParticles = 300,
            },
            Emission = { RateOverTime = 55f },
            Shape = { Shape = ParticleShapeType.Cone, Angle = 8f, Radius = 0.25f, RandomizeDirection = 0.05f },
            ColorOverLifetime = { Enabled = true, StartColor = Vector4.One, EndColor = new Vector4(1f, 0.25f, 0.05f, 0f) },
            SizeOverLifetime = { Enabled = true, StartSize = 1f, EndSize = 0.2f },
            RotationOverLifetime = { Enabled = true, AngularVelocity = new MinMax(-60f, 60f) },
            Renderer = { Blend = ParticleBlend.Additive, Sprite = ParticleSprite.SoftCircle },
        }),

        new ParticlePreset("Explosion", "A one-time fireball that bursts outward, slows quickly and fades. Press Restart to set it off again.", () => new ParticleSystem
        {
            Main =
            {
                Duration = 1.2f, Looping = false, Prewarm = false,
                StartLifetime = new MinMax(0.5f, 1.1f),
                StartSpeed = new MinMax(4f, 9f),
                StartSize = new MinMax(0.8f, 1.6f),
                StartRotation = new MinMax(0f, 360f),
                StartColor = new Vector4(1f, 0.55f, 0.1f, 1f),
                StartColor2 = new Vector4(1f, 0.85f, 0.35f, 1f),
                RandomBetweenTwoColors = true,
                GravityModifier = -0.15f,
                SimulationSpace = ParticleSimulationSpace.World,           // the blast stays where it went off
                MaxParticles = 400,
            },
            Emission = { RateOverTime = 0f, Bursts = { new ParticleBurst { Time = 0f, Count = 90, Cycles = 1, Interval = 0.01f } } },
            Shape = { Shape = ParticleShapeType.Sphere, Radius = 0.3f },
            LimitVelocityOverLifetime = { Enabled = true, Drag = 2.2f, SpeedLimit = 0f },
            ColorOverLifetime = { Enabled = true, StartColor = Vector4.One, EndColor = new Vector4(0.6f, 0.15f, 0.05f, 0f) },
            SizeOverLifetime = { Enabled = true, StartSize = 0.6f, EndSize = 1.6f },
            RotationOverLifetime = { Enabled = true, AngularVelocity = new MinMax(-120f, 120f) },
            Renderer = { Blend = ParticleBlend.Additive, Sprite = ParticleSprite.SoftCircle },
        }),

        new ParticlePreset("Embers", "Tiny glowing sparks that drift up and away, cooling as they go.", () => new ParticleSystem
        {
            Main =
            {
                Duration = 1f, Looping = true, Prewarm = true,
                StartLifetime = new MinMax(1.5f, 3f),
                StartSpeed = new MinMax(0.6f, 1.6f),
                StartSize = new MinMax(0.03f, 0.07f),
                StartRotation = 0f,
                StartColor = new Vector4(1f, 0.55f, 0.12f, 1f),
                StartColor2 = new Vector4(1f, 0.8f, 0.3f, 1f),
                RandomBetweenTwoColors = true,
                GravityModifier = -0.12f,
                SimulationSpace = ParticleSimulationSpace.World,           // sparks leave a moving fire behind
                MaxParticles = 150,
            },
            Emission = { RateOverTime = 14f },
            Shape = { Shape = ParticleShapeType.Cone, Angle = 25f, Radius = 0.3f, RandomizeDirection = 0.3f },
            VelocityOverLifetime = { Enabled = true, Linear = new Vector3(0.2f, 0f, 0.1f) },   // a light breeze
            ColorOverLifetime = { Enabled = true, StartColor = Vector4.One, EndColor = new Vector4(1f, 0.35f, 0.08f, 0f) },
            SizeOverLifetime = { Enabled = true, StartSize = 1f, EndSize = 0.3f },
            Renderer = { Blend = ParticleBlend.Additive, Sprite = ParticleSprite.Circle },
        }),

        new ParticlePreset("Smoke", "Soft gray puffs that rise slowly, swell and fade.", () => new ParticleSystem
        {
            Main =
            {
                Duration = 1f, Looping = true, Prewarm = true,
                StartLifetime = new MinMax(3.5f, 6f),
                StartSpeed = new MinMax(0.5f, 1.0f),
                StartSize = new MinMax(0.8f, 1.4f),
                StartRotation = new MinMax(0f, 360f),
                StartColor = new Vector4(0.35f, 0.35f, 0.37f, 0.55f),
                StartColor2 = new Vector4(0.55f, 0.55f, 0.58f, 0.5f),
                RandomBetweenTwoColors = true,
                GravityModifier = -0.06f,
                SimulationSpace = ParticleSimulationSpace.World,
                MaxParticles = 200,
            },
            Emission = { RateOverTime = 6f },
            Shape = { Shape = ParticleShapeType.Cone, Angle = 12f, Radius = 0.25f, RandomizeDirection = 0.1f },
            VelocityOverLifetime = { Enabled = true, Linear = new Vector3(0.25f, 0f, 0.1f) },
            LimitVelocityOverLifetime = { Enabled = true, Drag = 0.4f, SpeedLimit = 0f },
            ColorOverLifetime = { Enabled = true, StartColor = Vector4.One, EndColor = new Vector4(0.8f, 0.8f, 0.8f, 0f) },
            SizeOverLifetime = { Enabled = true, StartSize = 0.4f, EndSize = 2.4f },
            RotationOverLifetime = { Enabled = true, AngularVelocity = new MinMax(-25f, 25f) },
            Renderer = { Blend = ParticleBlend.Alpha, Sprite = ParticleSprite.SoftCircle, SortByDistance = true },
        }),
    };

    // The preset a system's settings still match, or null when it has been changed.
    public static ParticlePreset? Find(ParticleSystem system) => All.FirstOrDefault(p => p.Matches(system));
}
