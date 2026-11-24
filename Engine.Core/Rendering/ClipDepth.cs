using System.Numerics;

namespace Engine.Core.Rendering;

// Cameras use reversed depth: the near plane is at depth 1, and depth falls toward 0 with distance. A 32-bit float depth buffer then
// keeps almost the same precision however far away things are, so big, far-off surfaces close to each other don't flicker through
// one another. Nearer things have greater depth, so depth tests pass on "greater" and the depth buffer clears to Far.
//
// A perspective camera has no far plane: it draws things however far away they are, and depth 0 is infinitely far. An
// orthographic camera's depth is spread evenly from its near plane to its far plane instead, so it keeps a far plane.
public static class ClipDepth
{
    public const float Near = 1f;
    public const float Far = 0f;

    // A depth between the near plane and the farthest depth that is a real point for every camera (depth 0 is at infinity for a
    // perspective camera). Unproject a screen point at Near and at Ahead to get a ray through it.
    public const float Ahead = 0.5f;

    // A right-handed perspective projection (the camera looks down -Z) for Vulkan and Metal: Y points down the screen, and depth
    // is reversed, near / distance, so it reaches 0 only infinitely far away. Row-vector form, like System.Numerics.
    public static Matrix4x4 Perspective(float fovY, float aspect, float near)
    {
        float yScale = 1f / MathF.Tan(fovY * 0.5f);
        return new Matrix4x4(
            yScale / aspect, 0, 0, 0,
            0, -yScale, 0, 0,
            0, 0, 0, -1,
            0, 0, near, 0);
    }

    // An orthographic projection: halfHeight world units above and below the middle of the view, whatever the distance, with
    // depth reversed from 1 at the near plane to 0 at the far plane.
    public static Matrix4x4 Orthographic(float halfHeight, float aspect, float near, float far)
    {
        float h = MathF.Max(halfHeight, 0.0001f);
        float range = far - near;
        return new Matrix4x4(
            1f / (h * aspect), 0, 0, 0,
            0, -1f / h, 0, 0,
            0, 0, 1f / range, 0,
            0, 0, far / range, 1);
    }
}
