using System.Numerics;

namespace Engine.Core.Scene;

// A ready-made mood: the sky, the ambient light and how the sky lights the scene, plus the sun that goes with it
// (its color, brightness and angle). Applying one sets all of these at once; everything can still be changed afterwards.
public sealed record EnvironmentPreset(
    string Name, string Description,
    SkyMode Sky, Vector3 Top, Vector3 Horizon, Vector3 Ground, float SkyBrightness,
    Vector3 AmbientColor, float AmbientBrightness, float SkyLighting,
    Vector3 SunColor, float SunBrightness, float SunYaw, float SunPitch)
{
    // Sets the environment's sky and ambient light. A panorama image the environment already has is kept, in case the
    // sky is switched back to it. The sun, when there is one, takes the preset's light.
    public void Apply(SceneEnvironment environment, DirectionalLight? sun)
    {
        environment.Sky = Sky;
        environment.SkyColor = Top;
        environment.HorizonColor = Horizon;
        environment.GroundColor = Ground;
        environment.SkyIntensity = SkyBrightness;
        environment.AmbientColor = AmbientColor;
        environment.AmbientIntensity = AmbientBrightness;
        environment.SkyLighting = SkyLighting;
        if (sun is null) return;
        sun.Color = SunColor;
        sun.Intensity = SunBrightness;
        sun.Yaw = SunYaw;
        sun.Pitch = SunPitch;
    }

    // True when the environment still has this preset's sky and ambient settings (the sun isn't compared, so
    // moving the sun doesn't make the preset look changed).
    public bool Matches(SceneEnvironment e) =>
        e.Sky == Sky && Near(e.SkyColor, Top) && Near(e.HorizonColor, Horizon) && Near(e.GroundColor, Ground) &&
        Near(e.SkyIntensity, SkyBrightness) && Near(e.AmbientColor, AmbientColor) &&
        Near(e.AmbientIntensity, AmbientBrightness) && Near(e.SkyLighting, SkyLighting);

    private static bool Near(float a, float b) => MathF.Abs(a - b) < 0.0025f;
    private static bool Near(Vector3 a, Vector3 b) => Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z);
}

public static class EnvironmentPresets
{
    // Brightness values are in the renderer's units: a sun of 4 lights a surface facing it to about its own color.
    // Yaw and pitch point from the scene toward the sun; a low pitch is a low sun.
    public static IReadOnlyList<EnvironmentPreset> All { get; } = new[]
    {
        new EnvironmentPreset("Day", "A clear, ordinary day. The default look.",
            SkyMode.Gradient, new(0.25f, 0.45f, 0.80f), new(0.75f, 0.82f, 0.90f), new(0.25f, 0.25f, 0.28f), 1f,
            Vector3.One, 0.08f, 0.5f,
            new(1f, 0.96f, 0.88f), 4f, 33.69f, 47.96f),

        new EnvironmentPreset("Light", "Bright and airy, with soft shadows and a lot of fill light.",
            SkyMode.Gradient, new(0.45f, 0.65f, 0.95f), new(0.93f, 0.96f, 1.00f), new(0.60f, 0.62f, 0.66f), 1.05f,
            new(1f, 1f, 1f), 0.12f, 0.6f,
            new(1f, 1f, 0.96f), 2.3f, 20f, 62f),

        new EnvironmentPreset("Dark", "Night: a near-black sky and cool, dim moonlight.",
            SkyMode.Gradient, new(0.012f, 0.018f, 0.05f), new(0.04f, 0.05f, 0.11f), new(0.005f, 0.005f, 0.012f), 1f,
            new(0.45f, 0.5f, 0.8f), 0.08f, 0.25f,
            new(0.45f, 0.55f, 1f), 1.0f, -35f, 38f),

        new EnvironmentPreset("Moody", "Heavy overcast: flat gray-blue light and muted color.",
            SkyMode.Gradient, new(0.26f, 0.30f, 0.36f), new(0.50f, 0.53f, 0.57f), new(0.13f, 0.14f, 0.16f), 0.95f,
            new(0.72f, 0.78f, 0.88f), 0.07f, 0.45f,
            new(0.82f, 0.88f, 1f), 1.5f, 200f, 28f),

        new EnvironmentPreset("Fun", "Bright candy colors under a warm, high sun.",
            SkyMode.Gradient, new(0.30f, 0.52f, 1.00f), new(1.00f, 0.72f, 0.90f), new(0.45f, 0.90f, 0.62f), 1.1f,
            new(1f, 0.9f, 1f), 0.14f, 0.6f,
            new(1f, 0.95f, 0.72f), 3.2f, 60f, 55f),

        new EnvironmentPreset("Dungeon", "Underground: no sky, almost no ambient light. Add torches (point lights).",
            SkyMode.Color, new(0.012f, 0.010f, 0.014f), new(0.012f, 0.010f, 0.014f), new(0.012f, 0.010f, 0.014f), 1f,
            new(0.65f, 0.5f, 0.38f), 0.065f, 0f,
            new(1f, 0.8f, 0.6f), 0.22f, 0f, 70f),

        new EnvironmentPreset("Castle", "A hazy medieval afternoon: pale sky and a warm sun.",
            SkyMode.Gradient, new(0.40f, 0.50f, 0.66f), new(0.80f, 0.79f, 0.74f), new(0.26f, 0.23f, 0.20f), 1f,
            new(1f, 0.95f, 0.85f), 0.10f, 0.5f,
            new(1f, 0.90f, 0.75f), 3.4f, 120f, 40f),

        new EnvironmentPreset("House", "Bright, warm and even, like a lit interior.",
            SkyMode.Gradient, new(0.70f, 0.75f, 0.82f), new(0.86f, 0.85f, 0.80f), new(0.42f, 0.36f, 0.30f), 1f,
            new(1f, 0.93f, 0.82f), 0.24f, 0.4f,
            new(1f, 0.95f, 0.85f), 2.2f, 40f, 55f),

        new EnvironmentPreset("Sunrise", "Early morning: an orange horizon and a low, warm sun.",
            SkyMode.Gradient, new(0.24f, 0.36f, 0.70f), new(1.00f, 0.62f, 0.34f), new(0.20f, 0.15f, 0.18f), 1.05f,
            new(1f, 0.75f, 0.6f), 0.10f, 0.6f,
            new(1f, 0.66f, 0.36f), 3.2f, 70f, 12f),
    };

    // The preset whose sky and ambient settings the environment has, or null when it has been changed.
    public static EnvironmentPreset? Find(SceneEnvironment environment) => All.FirstOrDefault(p => p.Matches(environment));
}
