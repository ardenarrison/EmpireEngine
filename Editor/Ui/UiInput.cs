using System.Numerics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Brutal.VulkanApi;

namespace Editor.Ui;

// The editor's keyboard, wheel and clipboard for one frame: ImGui's input queue on the desktop (EditorUi), touch and UIKit on
// iPad. Keys are named as ImGui names them on both.
public interface IUiInput : IDisposable
{
    float2 DisplaySize { get; }
    float2 DisplayFramebufferScale { get; }
    float MouseWheel { get; }
    float MouseWheelH { get; }
    bool KeyCtrl { get; }
    bool KeyShift { get; }
    bool KeyAlt { get; }
    bool KeySuper { get; }
    IReadOnlyList<uint> InputQueueCharacters { get; }   // what was typed this frame
    bool WantsMouse { get; }

    void BeginFrame();
    void Render(CommandBuffer cmd);
    bool IsKeyPressed(ImGuiKey key, bool repeat = true);
    bool IsMouseClicked(ImGuiMouseButton button);
    bool IsMouseDoubleClicked(ImGuiMouseButton button);
    string GetClipboardText();
    void SetClipboardText(string text);

    // Two fingers moved over the scene this frame, in screen points. Only touch screens have it: elsewhere a drag is a mouse's.
    Vector2 SceneSwipe => Vector2.Zero;

    // Set each frame: whether a two-finger drag should move the scene's camera, rather than scroll a panel.
    bool PointerOverScene { get => false; set { } }
}

public static class UiInput
{
    public static IUiInput Current { get; set; } = null!;
}
