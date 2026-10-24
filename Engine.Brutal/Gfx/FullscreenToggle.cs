using Brutal.GlfwApi;
using Brutal.Numerics;

namespace Engine.Brutal.Gfx;

// F11 switches between a window and fullscreen. Fullscreen uses the monitor's largest 16:9 video mode, so the
// 16:9 scene fills the screen. The display letterboxes anything else.
public sealed class FullscreenToggle
{
    private bool _wasDown;
    private int2 _windowedPosition = new(100, 100);
    private int2 _windowedSize = new(1280, 720);

    public void Update(GlfwWindow window)
    {
        bool down = window.GetKey(GlfwKey.F11) == GlfwKeyAction.Press;
        if (down && !_wasDown)
        {
            if (window.IsFullscreen)
            {
                window.SetWindowed(_windowedPosition, _windowedSize);
            }
            else
            {
                _windowedPosition = window.Position;
                _windowedSize = window.Size;

                var monitor = Glfw.PrimaryMonitor;
                var mode = Largest16x9Mode(monitor) ?? monitor.VideoMode;
                window.SetFullscreen(monitor, new int2(mode.Width, mode.Height), -1);
            }
        }
        _wasDown = down;
    }

    private static GlfwVidMode? Largest16x9Mode(GlfwMonitor monitor)
    {
        GlfwVidMode? best = null;
        foreach (var mode in monitor.VideoModes)
            if (mode.Width * 9 == mode.Height * 16 && (best is null || mode.Width > best.Value.Width))
                best = mode;
        return best;
    }
}
