using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Engine.Core.Rendering;

namespace Engine.Brutal.Gfx;

// The window's drawing surface and the frame being drawn, whichever API draws it. VulkanContext and Metal's MetalContext implement it.
public interface IGraphicsContext : IDisposable
{
    GraphicsApi Api { get; }
    int Width { get; }                           // the drawing surface in pixels
    int Height { get; }
    bool IsMinimized { get; }
    int MaxSampleCount { get; }                  // the most MSAA samples per pixel the GPU can do (1 without MSAA)

    // Share of the window height kept free at the bottom for editor panels. The scene is fitted above it.
    float BottomReservedFraction { get; set; }

    // The part of the window the 16:9 scene fills, as fractions: x, y, width, height.
    float4 SceneViewport { get; }

    // Sets MSAA, post-process anti-aliasing and the color effects from a camera's settings (no camera means none). Call between frames.
    void ApplyCamera(Engine.Core.Scene.CameraComponent? camera);

    // A frame with nothing but 2D drawing (the splash screen): the scene area is cleared, then drawUi draws over the window.
    void BeginFrame();
    void EndFrame(Action<UiPass>? drawUi);

    void WaitIdle();
}

// Where 2D drawing goes at the end of a frame: a Vulkan command buffer inside the UI render pass, or a Metal render encoder.
public sealed class UiPass
{
    public CommandBuffer Vulkan { get; }
    public nint MetalEncoder { get; }

    public UiPass(CommandBuffer cmd) => Vulkan = cmd;
    public UiPass(nint metalEncoder) => MetalEncoder = metalEncoder;
}

public static class SceneLayout
{
    // The scene is always drawn at 16:9. Any leftover space is left black, so the picture is never stretched.
    public const float Aspect = 16f / 9f;

    // The part of a width x height surface the scene fills, as fractions (x, y, width, height), with bottomReserved of the height
    // kept free below it.
    public static float4 Viewport(int width, int height, float bottomReserved)
    {
        float areaW = width;
        float areaH = height * (1f - Math.Clamp(bottomReserved, 0f, 0.9f));
        float w, h;
        if (areaW / areaH > Aspect)
        {
            h = areaH;
            w = h * Aspect;
        }
        else
        {
            w = areaW;
            h = w / Aspect;
        }
        return new float4((areaW - w) / 2f / width, (areaH - h) / 2f / height, w / width, h / height);
    }

    // The same rectangle in pixels.
    public static (int X, int Y, int Width, int Height) Pixels(float4 viewport, int width, int height) =>
        ((int)(viewport.X * width), (int)(viewport.Y * height), (int)(viewport.Z * width), (int)(viewport.W * height));
}
