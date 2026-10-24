using System.Numerics;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Engine.Core.Scripting;

namespace Engine.Brutal.Gfx;

// Connects script mouse input to a window. viewArea gives the game view inside the window (x, y, width, height in
// screen points), so positions are measured from its corner; scroll gives how far the wheel moved since it was last
// asked. The cursor follows Input.cursorLocked and Input.cursorVisible.
public static class MouseInput
{
    private static int _appliedMode = -1;

    public static void Bind(GlfwWindow window, Func<float4> viewArea, Func<float> scroll)
    {
        _appliedMode = -1;
        Input.MouseProvider = () =>
        {
            ApplyCursor(window);
            var area = viewArea();
            var cursor = window.CursorPos;
            return new MouseState(
                new Vector2((float)cursor.X - area.X, (float)cursor.Y - area.Y),
                window.GetMouseButton(GlfwMouseButton.Left) == GlfwButtonAction.Press,
                window.GetMouseButton(GlfwMouseButton.Right) == GlfwButtonAction.Press,
                window.GetMouseButton(GlfwMouseButton.Middle) == GlfwButtonAction.Press,
                scroll());
        };
    }

    // Shows the cursor and lets it go, for when a game stops (the editor keeps running).
    public static void Release(GlfwWindow window)
    {
        Input.cursorLocked = false;
        Input.cursorVisible = true;
        ApplyCursor(window);
    }

    // Puts the window's cursor in the mode the scripts asked for, when it changed.
    private static void ApplyCursor(GlfwWindow window)
    {
        int mode = Input.cursorLocked ? Glfw.Constants.GLFW_CURSOR_DISABLED
            : Input.cursorVisible ? Glfw.Constants.GLFW_CURSOR_NORMAL
            : Glfw.Constants.GLFW_CURSOR_HIDDEN;
        if (mode == _appliedMode) return;
        window.SetInputMode(GlfwInputMode.Cursor, mode);
        _appliedMode = mode;
    }
}
