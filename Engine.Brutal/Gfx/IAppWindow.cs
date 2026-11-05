using System.Runtime.InteropServices;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.VulkanApi;

namespace Engine.Brutal.Gfx;

// Which keyboard the iPad editor types with.
public enum KeyboardPreference
{
    Automatic,   // a connected keyboard when there is one, otherwise the one on the screen
    OnScreen,    // the keyboard on the screen
    Connected,   // a Smart Folio or Bluetooth keyboard: the one on the screen never opens
}

// What the renderer and the editor need from the window they run in: a GLFW window on the desktop, a Metal view on iPad.
// Keys and mouse buttons are named as GLFW names them on both.
public interface IAppWindow
{
    IEnumerable<string> InstanceExtensions { get; }   // the Vulkan extensions its surface needs
    bool HasVulkanLoader => true;                     // false when Vulkan is MoltenVK itself, as on iPad
    ulong CreateSurface(Instance instance);           // makes the VkSurfaceKHR to draw on
    int2 Size { get; }                                // in screen points
    int2 FramebufferSize { get; }                     // in pixels
    float ContentScale { get; }                       // pixels per point
    bool ShouldClose { get; set; }
    string Title { get; set; }
    double2 CursorPos { get; }                        // in screen points
    void PollEvents();
    GlfwKeyAction GetKey(GlfwKey key);
    GlfwButtonAction GetMouseButton(GlfwMouseButton button);
    void SetInputMode(GlfwInputMode mode, int value);
    event Action<double>? PinchZoom;                  // a pinch: the new size as a multiple of the old, so 1.05 is fingers apart

    // The desktop window, for what only it can do, like going fullscreen.
    GlfwWindow? Glfw => null;

    // True while the editor expects typing, so a window with an on-screen keyboard can open it.
    bool WantsTextInput { get => false; set { } }

    // Null on a window that has only one keyboard.
    KeyboardPreference? KeyboardPreference { get => null; set { } }
}

// The desktop window.
public sealed unsafe class GlfwAppWindow : IAppWindow
{
    private readonly GlfwWindow _window;

    public GlfwAppWindow(GlfwWindow window)
    {
        _window = window;
        window.OnTrackpadZoom += (_, scale) => PinchZoom?.Invoke(scale);
    }

    public IEnumerable<string> InstanceExtensions
    {
        get
        {
            byte** names = global::Brutal.GlfwApi.Glfw.GetRequiredInstanceExtensions(out uint count);
            var extensions = new List<string>((int)count);
            for (int i = 0; i < count; i++)
                extensions.Add(Marshal.PtrToStringUTF8((nint)names[i])!);
            return extensions;
        }
    }

    public ulong CreateSurface(Instance instance)
    {
        global::Brutal.GlfwApi.Glfw.CreateWindowSurface(instance.Handle.VkHandle, _window, out ulong surface);
        return surface;
    }

    public GlfwWindow Glfw => _window;
    public int2 Size => _window.Size;
    public int2 FramebufferSize => _window.FramebufferSize;
    public float ContentScale => _window.ContentScale.X;
    public bool ShouldClose { get => _window.ShouldClose; set => _window.ShouldClose = value; }
    public string Title { get => _window.Title; set => _window.Title = value; }
    public double2 CursorPos => _window.CursorPos;
    public void PollEvents() => global::Brutal.GlfwApi.Glfw.PollEvents();
    public GlfwKeyAction GetKey(GlfwKey key) => _window.GetKey(key);
    public GlfwButtonAction GetMouseButton(GlfwMouseButton button) => _window.GetMouseButton(button);
    public void SetInputMode(GlfwInputMode mode, int value) => _window.SetInputMode(mode, value);
    public event Action<double>? PinchZoom;
}
