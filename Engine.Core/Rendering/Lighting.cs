using System.Numerics;
using Engine.Core.Assets;
using Engine.Core.Scene;

namespace Engine.Core.Rendering;

public enum LightType { Directional = 0, Point = 1, Spot = 2 }

// One light as the renderer needs it, in world space. Directional lights use Direction (toward the light);
// point and spot lights use Position and Range; spot lights also use Direction (the way the light shines) and
// the cosines of their inner and outer cone angles.
public readonly record struct LightData(LightType Type, Vector3 Position, Vector3 Direction, Vector3 Radiance,
    float Range, float CosInner, float CosOuter);

// The sky and ambient light for a frame. Ambient is already color times intensity.
public sealed record EnvironmentData(SkyMode Sky, Vector3 Zenith, Vector3 Horizon, Vector3 Ground, string Panorama,
    float SkyIntensity, Vector3 Ambient, float SkyLighting)
{
    // What a scene without a SceneEnvironment looks like: the dark blue backdrop and 0.08 flat ambient it has always had.
    public static EnvironmentData Default { get; } = new(SkyMode.Color, new(0.05f, 0.08f, 0.15f), new(0.05f, 0.08f, 0.15f),
        new(0.05f, 0.08f, 0.15f), "", 1f, new Vector3(0.08f), 0f);

    public static EnvironmentData From(SceneEnvironment e) => new(e.Sky, e.SkyColor, e.HorizonColor, e.GroundColor,
        e.Panorama, e.SkyIntensity, e.AmbientColor * e.AmbientIntensity, e.SkyLighting);
}

// Gathers a scene's lighting for the renderer. Editor and built games both use it, so they light a scene the same way.
public static class SceneLighting
{
    // The most point, spot and extra directional lights drawn at once. When a scene has more, the nearest to the
    // camera win (directional lights always count).
    public const int MaxLights = 16;

    // The sun: the scene's first directional light. It is drawn separately from the others and is the one that casts
    // shadows, so it is left out of Collect's list.
    public static DirectionalLight? FindSun(Scene.Scene scene) =>
        scene.All.SelectMany(o => o.Components.OfType<DirectionalLight>()).FirstOrDefault();

    public static (EnvironmentData Environment, List<LightData> Lights) Collect(Scene.Scene scene, Vector3 eye)
    {
        var sun = FindSun(scene);
        var environment = scene.All.SelectMany(o => o.Components.OfType<SceneEnvironment>()).FirstOrDefault() is { } e
            ? EnvironmentData.From(e)
            : EnvironmentData.Default;

        var directional = new List<LightData>();
        var local = new List<(LightData Light, float Distance)>();
        foreach (var obj in scene.All)
        {
            var world = obj.WorldMatrix;
            var position = world.Translation;
            foreach (var component in obj.Components)
            {
                switch (component)
                {
                    case DirectionalLight light when light != sun:
                        directional.Add(new LightData(LightType.Directional, Vector3.Zero, light.Direction, light.Radiance, 0f, 0f, 0f));
                        break;
                    case PointLight point:
                        local.Add((new LightData(LightType.Point, position, Vector3.Zero, point.Radiance,
                            MathF.Max(point.Range, 0.01f), 0f, 0f), Vector3.Distance(eye, position) - point.Range));
                        break;
                    case SpotLight spot:
                    {
                        var direction = Vector3.TransformNormal(-Vector3.UnitZ, world);
                        direction = direction.LengthSquared() > 1e-12f ? Vector3.Normalize(direction) : -Vector3.UnitZ;
                        float inner = Math.Clamp(spot.InnerAngle, 0f, 89f);
                        float outer = Math.Clamp(Math.Max(spot.OuterAngle, inner + 0.5f), 0.5f, 89.5f);
                        local.Add((new LightData(LightType.Spot, position, direction, spot.Radiance, MathF.Max(spot.Range, 0.01f),
                            MathF.Cos(inner * MathF.PI / 180f), MathF.Cos(outer * MathF.PI / 180f)), Vector3.Distance(eye, position) - spot.Range));
                        break;
                    }
                }
            }
        }

        var lights = directional.Take(MaxLights).ToList();
        lights.AddRange(local.OrderBy(l => l.Distance).Take(MaxLights - lights.Count).Select(l => l.Light));
        return (environment, lights);
    }
}
