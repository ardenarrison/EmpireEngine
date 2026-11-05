using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Backends;
using Brutal.VulkanApi;
using Brutal.Numerics;
using Editor.Ui;
using Engine.Brutal.Gfx;

namespace Editor;

// Wraps BRUTAL's ImGui: one context, the GLFW input backend, and the Vulkan renderer backend. It only supplies input now
// (see IUiInput): the editor draws itself with Gui.
public sealed class EditorUi : IUiInput
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
    public float2 DisplaySize => ImGui.GetIO().DisplaySize;
    public float2 DisplayFramebufferScale => ImGui.GetIO().DisplayFramebufferScale;
    public float MouseWheel => ImGui.GetIO().MouseWheel;
    public float MouseWheelH => ImGui.GetIO().MouseWheelH;
    public bool KeyCtrl => ImGui.GetIO().KeyCtrl;
    public bool KeyShift => ImGui.GetIO().KeyShift;
    public bool KeyAlt => ImGui.GetIO().KeyAlt;
    public bool KeySuper => ImGui.GetIO().KeySuper;

    public IReadOnlyList<uint> InputQueueCharacters
    {
        get
        {
            var queue = ImGui.GetIO().InputQueueCharacters;
            var characters = new uint[queue.Count];
            for (int i = 0; i < characters.Length; i++) characters[i] = queue[i];
            return characters;
        }
    }

    public bool IsKeyPressed(ImGuiKey key, bool repeat = true) => ImGui.IsKeyPressed(key, repeat);
    public bool IsMouseClicked(ImGuiMouseButton button) => ImGui.IsMouseClicked(button);
    public bool IsMouseDoubleClicked(ImGuiMouseButton button) => ImGui.IsMouseDoubleClicked(button);
    public void SetClipboardText(string text) => ImGui.SetClipboardText(text.ToU8());

    public string GetClipboardText()
    {
        var text = ImGui.GetClipboardText();
        return text.IsNull() ? "" : System.Text.Encoding.UTF8.GetString(text.AsSpan());
    }

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
