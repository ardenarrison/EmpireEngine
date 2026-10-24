using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Backends;
using Brutal.VulkanApi;
using Engine.Brutal.Gfx;

namespace Editor;

// Wraps BRUTAL's ImGui: one context, the GLFW input backend, and the Vulkan renderer backend.
public sealed class EditorUi : IDisposable
{
    private readonly GlfwBackend _platform;
    private readonly VulkanBackend _renderer;

    public EditorUi(GlfwWindow window, VulkanContext ctx)
    {
        ImGui.CreateContext();
        ImGui.GetIO().IniFilename = default;     // ImGui only draws game UI now, so there is no window layout to save
        _platform = new GlfwBackend(window, GlfwBackend.ClientApi.Vulkan);

        var info = new VulkanBackend.CreateInfo
        {
            Device = ctx.DeviceEx,
            GraphicsQueue = ctx.Queue,
            RenderPass = ctx.UiRenderPass,
            SubPass = 0,
            MinImageCount = 2,
            ImageCount = ctx.ImageCount,
            SampleCount = (VkSampleCountFlags)1,
            DescriptorPoolSize = 64,
        };
        _renderer = new VulkanBackend(in info);
    }

    public bool WantsMouse => ImGui.GetIO().WantCaptureMouse;

    public void BeginFrame()
    {
        _platform.NewFrame();
        ImGui.NewFrame();
    }

    public void Render(CommandBuffer cmd)
    {
        ImGui.Render();
        _renderer.RenderDrawData(ImGui.GetDrawData(), cmd);
    }

    public void Dispose()
    {
        _renderer.Dispose();
        _platform.Dispose();
        ImGui.DestroyContext();
    }
}
