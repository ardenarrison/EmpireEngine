using System.Numerics;
using System.Runtime.InteropServices;
using Engine.Core.Assets;
using Engine.Core.Scene;

namespace Engine.Core.Rendering;

public interface IRenderer : IDisposable
{
    void UploadModel(string key, GlbModel model);
    // The sun's shadows for the frame about to be drawn: strength 0 (or enabled false) turns them off. distance is how
    // far from the camera they reach and resolution the shadow map's size in texels (the scene's setting). Call it
    // before BeginFrame.
    void SetShadows(bool enabled, float strength, float distance, int resolution = 2048);

    void BeginFrame(Matrix4x4 viewProjection, Vector3 cameraPosition, Vector3 sunDirection, Vector3 sunRadiance);
    void DrawModel(string key, Matrix4x4 world, Vector4 tint);

    // The same with an animation pose (null for the resting pose). The pose is read now, so it may be changed afterwards.
    void DrawModel(string key, Matrix4x4 world, Vector4 tint, Animation.ModelPose? pose);

    // The sky, ambient light and extra lights for the frame about to be drawn (the sun is passed to BeginFrame). Call
    // it before BeginFrame. loadImage finds the sky panorama when the environment uses one (path relative to the project).
    void SetLighting(EnvironmentData environment, IReadOnlyList<LightData> lights, Func<string, ImageData>? loadImage = null);

    // Camera-facing sprites, drawn after the models with blending. Particles are copied, so the span can be reused.
    void DrawParticles(ReadOnlySpan<ParticleInstance> particles, ParticleBlend blend, ParticleSprite sprite);
    void EndFrame();
}

// One particle sprite: where it is, how wide (world units), its color, and its spin in radians.
// Laid out as 9 floats, matching the particle shader's per-instance inputs.
[StructLayout(LayoutKind.Sequential)]
public struct ParticleInstance
{
    public Vector3 Position;
    public float Size;
    public Vector4 Color;
    public float Rotation;
}
