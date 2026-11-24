using System.Numerics;
using Engine.Core.Rendering;

namespace Engine.Brutal.Rendering;

public static class VulkanMath
{
    // A perspective projection for Vulkan and Metal: Y down the screen, reversed depth and no far plane (see ClipDepth.Perspective).
    public static Matrix4x4 Projection(float fovY, float aspect, float near) => ClipDepth.Perspective(fovY, aspect, near);

    // The same for an orthographic camera: halfHeight world units above and below the middle of the view, whatever the distance.
    // It keeps a far plane, since its depth is spread evenly between near and far.
    public static Matrix4x4 Orthographic(float halfHeight, float aspect, float near, float far) =>
        ClipDepth.Orthographic(halfHeight, aspect, near, far);

    // A scene camera's projection: orthographic (with the camera's Far) or perspective (which sees forever), as it is set.
    public static Matrix4x4 ProjectionFor(Engine.Core.Scene.CameraComponent camera, float aspect) => camera.Orthographic
        ? Orthographic(camera.OrthographicSize, aspect, camera.Near, camera.Far)
        : Projection(camera.VerticalFov(aspect), aspect, camera.Near);
}
