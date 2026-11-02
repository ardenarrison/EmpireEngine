using System.Numerics;

namespace Engine.Brutal.Rendering;

public static class VulkanMath
{
    // System.Numerics gives an OpenGL-style projection (Y up, depth -1..1).
    // Vulkan wants Y down and depth 0..1. Applied as a right-hand multiply in row-vector form.
    public static Matrix4x4 Projection(float fovY, float aspect, float near, float far)
    {
        var p = Matrix4x4.CreatePerspectiveFieldOfView(fovY, aspect, near, far);
        var fix = new Matrix4x4(
            1, 0, 0, 0,
            0, -1, 0, 0,
            0, 0, 0.5f, 0,
            0, 0, 0.5f, 1);
        return p * fix;
    }

    // The same for an orthographic camera: halfHeight world units above and below the middle of the view, whatever the distance.
    public static Matrix4x4 Orthographic(float halfHeight, float aspect, float near, float far)
    {
        float h = MathF.Max(halfHeight, 0.0001f);
        var p = Matrix4x4.CreateOrthographic(h * 2f * aspect, h * 2f, near, far);
        var fix = new Matrix4x4(
            1, 0, 0, 0,
            0, -1, 0, 0,
            0, 0, 0.5f, 0,
            0, 0, 0.5f, 1);
        return p * fix;
    }

    // A scene camera's projection: orthographic or perspective, as it is set.
    public static Matrix4x4 ProjectionFor(Engine.Core.Scene.CameraComponent camera, float aspect) => camera.Orthographic
        ? Orthographic(camera.OrthographicSize, aspect, camera.Near, camera.Far)
        : Projection(camera.VerticalFov(aspect), aspect, camera.Near, camera.Far);
}
