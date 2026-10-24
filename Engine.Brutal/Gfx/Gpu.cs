using System.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Engine.Brutal.Gfx;
using Engine.Core.Scene;
using Engine.Core.Scripting;

namespace Engine.Brutal;

// Direct Vulkan access for scripts. Add "using Engine.Brutal;" (and "using Brutal.VulkanApi;" for the raw types)
// to a script to use it. The engine owns everything here: create your own objects from Device, but never destroy
// the device, queue, swapchain or command buffer.
public static class Gpu
{
    private static VulkanContext? _ctx;

    // False until the engine has created its Vulkan device (for example in a script's constructor).
    public static bool IsAvailable => _ctx is not null;

    public static VulkanContext Context =>
        _ctx ?? throw new InvalidOperationException("Vulkan is not running yet. Use Gpu from Start, Update or RenderVulkan.");

    public static Device Device => Context.Device;
    public static DeviceEx DeviceEx => Context.DeviceEx;
    public static PhysicalDevice PhysicalDevice => Context.Physical;
    public static Queue Queue => Context.Queue;

    // Formats of the scene pass, for VkPipelineRenderingCreateInfo when building pipelines for RenderVulkan.
    public static VkFormat ColorFormat => Context.ColorFormat;
    public static VkFormat DepthFormat => (VkFormat)VulkanContext.DepthFormat;

    // Samples per pixel of the scene pass: 1 normally, or 2, 4 or 8 when the camera uses MSAA. A pipeline for
    // RenderVulkan must set MultisampleState.RasterizationSamples to this, and be rebuilt when it changes.
    public static int SampleCount => Context.SampleCount;

    // The command buffer being recorded this frame. Only record into it from RenderVulkan.
    public static CommandBuffer CommandBuffer => Context.CurrentCommandBuffer;

    // Per-frame resources (uniform buffers and so on) should be kept FramesInFlight times and indexed by FrameIndex.
    public const int FramesInFlight = VulkanContext.FramesInFlight;
    public static int FrameIndex => Context.FrameIndex;

    // Swapchain size in pixels.
    public static int Width => Context.Width;
    public static int Height => Context.Height;

    // The camera used for this frame's scene pass. Row-major, as System.Numerics; use v * ViewProjection.
    public static Matrix4x4 ViewProjection { get; internal set; } = Matrix4x4.Identity;
    public static Vector3 CameraPosition { get; internal set; }

    // Waits until the GPU has finished all submitted work, e.g. before destroying a buffer it may still read.
    public static void WaitIdle() => Context.WaitIdle();

    internal static void Attach(VulkanContext ctx) => _ctx = ctx;

    internal static void Detach(VulkanContext ctx)
    {
        if (_ctx == ctx) _ctx = null;
    }
}

// Implement on a Behaviour to record your own Vulkan commands every frame.
public interface IVulkanRender
{
    // Runs inside the scene's dynamic rendering pass, after the engine's models, with the 16:9 scene viewport and
    // scissor set. Bind your own pipeline; it must be created for Gpu.ColorFormat and Gpu.DepthFormat.
    void RenderVulkan(CommandBuffer cmd);

    // Runs once when play mode stops or the game closes, after the GPU is idle. Destroy what you created here.
    void ReleaseVulkan() { }
}

// Calls IVulkanRender on the scene's scripts. Used by the editor and the player.
public static class VulkanScripts
{
    private static readonly HashSet<Behaviour> Failed = new();

    public static void Render(Scene scene, CommandBuffer cmd)
    {
        foreach (var script in Scripts(scene))
        {
            if (Failed.Contains((Behaviour)script)) continue;
            try
            {
                script.RenderVulkan(cmd);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Vulkan error in {script.GetType().Name}: {ex.Message}. Its RenderVulkan is stopped.");
                Failed.Add((Behaviour)script);
            }
        }
    }

    public static void Release(Scene scene)
    {
        var scripts = Scripts(scene).ToList();
        if (scripts.Count > 0 && Gpu.IsAvailable)
            Gpu.WaitIdle();
        foreach (var script in scripts)
        {
            try
            {
                script.ReleaseVulkan();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Vulkan error in {script.GetType().Name}.ReleaseVulkan: {ex.Message}");
            }
        }
        Failed.Clear();
    }

    private static IEnumerable<IVulkanRender> Scripts(Scene scene) =>
        scene.All
            .SelectMany(o => o.Components.OfType<ScriptComponent>())
            .Select(c => c.Instance)
            .OfType<IVulkanRender>();
}
