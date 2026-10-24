using System.Numerics;

namespace Engine.Core.Scripting;

// Keys a script can check. The names match the keyboard keys.
public enum Keys
{
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    Space, Enter, Escape, Tab, Backspace,
    LeftShift, RightShift, LeftControl, RightControl, LeftAlt, RightAlt,
    Left, Right, Up, Down,
}

// The mouse buttons, for Input.GetMouseButton. 0, 1 and 2 also work, as in Unity.
public enum MouseButton { Left, Right, Middle }

// The mouse as the engine sees it at one moment. Position is in the game view: (0, 0) is its top-left corner, x grows to
// the right and y downward, in screen points. Scroll is how far the wheel moved since the last sample (up is positive).
public readonly record struct MouseState(Vector2 Position, bool Left, bool Right, bool Middle, float Scroll);

// A line in the world from a point, for raycasts: Physics.Raycast(Input.GetMouseRay(), out var hit).
public readonly record struct Ray(Vector3 Origin, Vector3 Direction)
{
    public Vector3 GetPoint(float distance) => Origin + Direction * distance;
}

// Keyboard and mouse state for scripts. GetKey is true while a key is held; GetKeyDown only on the frame it went down,
// and GetKeyUp only on the frame it came up. The mouse works the same way with GetMouseButton, GetMouseButtonDown and
// GetMouseButtonUp, plus mousePosition, mouseDelta and mouseScrollDelta. Set cursorLocked for mouse-look: the cursor is
// hidden and held in place, and mouseDelta keeps reporting movement.
public static class Input
{
    // Set by the engine. Reports whether a key is physically down right now.
    public static Func<Keys, bool> Provider { get; set; } = _ => false;

    private static HashSet<Keys> _current = new();
    private static HashSet<Keys> _previous = new();

    public static bool GetKey(Keys key) => _current.Contains(key);

    public static bool GetKeyDown(Keys key) => _current.Contains(key) && !_previous.Contains(key);

    public static bool GetKeyUp(Keys key) => !_current.Contains(key) && _previous.Contains(key);

    // String overloads for scripts and visual scripts. Names are the editor's key names: "W", "1", "Space", "Shift".
    public static bool GetKey(string name) => GetKey(ParseKey(name));

    public static bool GetKeyDown(string name) => GetKeyDown(ParseKey(name));

    public static bool GetKeyUp(string name) => GetKeyUp(ParseKey(name));

    // ---- typed text --------------------------------------------------------------------------------------

    // Set by the engine. Returns the characters typed since it was last called (what the keyboard layout produced,
    // so Shift+a is "A"), which is what text fields need.
    public static Func<string> TextProvider { get; set; } = () => "";

    // The characters typed this frame, as in Unity. Game UI text fields read it too.
    public static string inputString { get; private set; } = "";

    // ---- mouse ----------------------------------------------------------------------------------------

    // Set by the engine. Reports the mouse right now; the scroll is what built up since the last call.
    public static Func<MouseState> MouseProvider { get; set; } = () => default;

    private static MouseState _mouse, _previousMouse;
    private static Vector2 _mouseDelta;
    private static bool _settleMouse = true;   // the next sample starts a new run, so it has no movement
    private static bool _cursorLocked;
    private static Vector2 _viewSize = new(1f, 1f);
    private static Matrix4x4 _viewProjection = Matrix4x4.Identity;

    // Where the mouse is in the game view, in screen points from its top-left corner (y grows downward).
    public static Vector2 mousePosition => _mouse.Position;

    // How far the mouse moved since the last frame, in screen points (y grows downward). Keeps working while the
    // cursor is locked, which is what mouse-look uses.
    public static Vector2 mouseDelta => _mouseDelta;

    // How far the scroll wheel (or a trackpad's two-finger scroll) moved this frame. Up is positive.
    public static float mouseScrollDelta => _mouse.Scroll;

    // The size of the game view in screen points, the same units as mousePosition.
    public static Vector2 screenSize => _viewSize;

    // True hides the cursor and holds it in the window, for mouse-look; mouseDelta still reports movement. In the
    // editor, Escape or stopping the game unlocks it.
    public static bool cursorLocked
    {
        get => _cursorLocked;
        set
        {
            if (_cursorLocked == value) return;
            _cursorLocked = value;
            _settleMouse = true;   // the cursor jumps when it is locked or freed; that is not movement
        }
    }

    // False hides the cursor over the game but leaves it free to move. Locked cursors are always hidden.
    public static bool cursorVisible { get; set; } = true;

    // True while the button is held. 0 is the left button, 1 the right and 2 the middle.
    public static bool GetMouseButton(MouseButton button) => IsDown(_mouse, button);
    public static bool GetMouseButton(int button) => GetMouseButton(ToButton(button));
    public static bool GetMouseButton(string button) => GetMouseButton(ParseButton(button));

    // True only on the frame the button went down.
    public static bool GetMouseButtonDown(MouseButton button) => IsDown(_mouse, button) && !IsDown(_previousMouse, button);
    public static bool GetMouseButtonDown(int button) => GetMouseButtonDown(ToButton(button));
    public static bool GetMouseButtonDown(string button) => GetMouseButtonDown(ParseButton(button));

    // True only on the frame the button came up.
    public static bool GetMouseButtonUp(MouseButton button) => !IsDown(_mouse, button) && IsDown(_previousMouse, button);
    public static bool GetMouseButtonUp(int button) => GetMouseButtonUp(ToButton(button));
    public static bool GetMouseButtonUp(string button) => GetMouseButtonUp(ParseButton(button));

    // A value for common controls, as in Unity:
    //   "Horizontal"  -1 to 1 from A/D or the left/right arrows      "Vertical"  -1 to 1 from S/W or the down/up arrows
    //   "Mouse X"     mouse movement to the right this frame        "Mouse Y"   mouse movement upward this frame
    //   "Mouse ScrollWheel"  the scroll wheel this frame
    // The mouse axes are a tenth of the movement in screen points, so multiply by a sensitivity that feels right.
    public static float GetAxis(string axis) => axis.Replace(" ", "").ToLowerInvariant() switch
    {
        "horizontal" => (GetKey(Keys.D) || GetKey(Keys.Right) ? 1f : 0f) - (GetKey(Keys.A) || GetKey(Keys.Left) ? 1f : 0f),
        "vertical" => (GetKey(Keys.W) || GetKey(Keys.Up) ? 1f : 0f) - (GetKey(Keys.S) || GetKey(Keys.Down) ? 1f : 0f),
        "mousex" => _mouseDelta.X * 0.1f,
        "mousey" => -_mouseDelta.Y * 0.1f,
        "mousescrollwheel" => _mouse.Scroll * 0.1f,
        _ => throw new ArgumentException($"Unknown axis '{axis}'. Use Horizontal, Vertical, Mouse X, Mouse Y or Mouse ScrollWheel."),
    };

    // The ray from the camera through a point in the game view (in the same units as mousePosition). Use it to find
    // what is under the mouse: Physics.Raycast(Input.ScreenPointToRay(Input.mousePosition), out var hit).
    public static Ray ScreenPointToRay(Vector2 point)
    {
        if (!Matrix4x4.Invert(_viewProjection, out var inverse))
            return new Ray(Vector3.Zero, -Vector3.UnitZ);
        float x = point.X / MathF.Max(_viewSize.X, 1f) * 2f - 1f;
        float y = point.Y / MathF.Max(_viewSize.Y, 1f) * 2f - 1f;
        var near = Vector4.Transform(new Vector4(x, y, 0f, 1f), inverse);
        var far = Vector4.Transform(new Vector4(x, y, 1f, 1f), inverse);
        var start = new Vector3(near.X, near.Y, near.Z) / near.W;
        var end = new Vector3(far.X, far.Y, far.Z) / far.W;
        var direction = end - start;
        return new Ray(start, direction.LengthSquared() > 1e-12f ? Vector3.Normalize(direction) : -Vector3.UnitZ);
    }

    // The ray from the camera through the mouse.
    public static Ray GetMouseRay() => ScreenPointToRay(_mouse.Position);

    // Set by the engine every frame: the game view's size and the camera's view-projection, for ScreenPointToRay.
    public static void SetView(Vector2 size, Matrix4x4 viewProjection)
    {
        _viewSize = new Vector2(MathF.Max(size.X, 1f), MathF.Max(size.Y, 1f));
        _viewProjection = viewProjection;
    }

    private static bool IsDown(MouseState state, MouseButton button) => button switch
    {
        MouseButton.Left => state.Left,
        MouseButton.Right => state.Right,
        _ => state.Middle,
    };

    private static MouseButton ToButton(int button) => button switch
    {
        0 => MouseButton.Left,
        1 => MouseButton.Right,
        2 => MouseButton.Middle,
        _ => throw new ArgumentException($"Unknown mouse button {button}. Use 0 (left), 1 (right) or 2 (middle)."),
    };

    // Names for scripts and visual scripts: "Left", "Right", "Middle".
    public static MouseButton ParseButton(string name) =>
        Enum.TryParse<MouseButton>(name, ignoreCase: true, out var button)
            ? button
            : throw new ArgumentException($"Unknown mouse button '{name}'. Use Left, Right or Middle.");

    public static Keys ParseKey(string name)
    {
        string member = name switch
        {
            "Shift" => "LeftShift",
            "Control" => "LeftControl",
            "Alt" => "LeftAlt",
            _ when name.Length == 1 && char.IsDigit(name[0]) => "D" + name,
            _ => name,
        };
        return Enum.TryParse<Keys>(member, ignoreCase: true, out var key) ? key : throw new ArgumentException($"Unknown key '{name}'.");
    }

    // Samples the keyboard once per frame. The engine calls this before scripts run.
    internal static void Advance()
    {
        _previous = _current;
        _current = new HashSet<Keys>();
        foreach (var key in Enum.GetValues<Keys>())
            if (Provider(key))
                _current.Add(key);

        inputString = TextProvider() ?? "";

        _previousMouse = _mouse;
        _mouse = MouseProvider();
        _mouseDelta = _settleMouse ? Vector2.Zero : _mouse.Position - _previousMouse.Position;
        _settleMouse = false;
    }

    // A fresh start for a game that is about to play: nothing held, the cursor free and shown.
    internal static void Reset()
    {
        _previous = new HashSet<Keys>();
        _current = new HashSet<Keys>();
        TextProvider();   // anything typed before the game started is not the game's
        inputString = "";
        _mouse = _previousMouse = default;
        _mouseDelta = Vector2.Zero;
        _settleMouse = true;
        _cursorLocked = false;
        cursorVisible = true;
    }
}
