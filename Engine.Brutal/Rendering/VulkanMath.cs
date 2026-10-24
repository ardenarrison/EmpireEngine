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
}
