using System.Diagnostics;
using System.Numerics;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Metal;
using CoreAnimation;
using Editor.Ui;
using Engine.Brutal.Gfx;
using Foundation;
using UIKit;

namespace EmpireiPad;

// The editor's window and input on iPad. The view posts what happens on the main thread; PollEvents takes it in on the editor's
// thread, once a frame. A press never lands on the frame the pointer moved, or a tap would miss what it was aimed at, because
// the editor's UI works out what is under the pointer before it looks at the buttons.
public sealed unsafe class TouchWindow(EditorView view) : IAppWindow, IUiInput
{
    private enum Kind { Move, Button, Key, Character, Wheel, Swipe, Pinch }
    private readonly record struct Input(Kind Kind, double X = 0, double Y = 0, int Code = 0, bool Down = false);

    private const double RepeatDelay = 0.4, RepeatInterval = 0.05, DoubleTapSeconds = 0.35, DoubleTapPoints = 16;
    private const float PointsPerWheelNotch = 30f;

    private readonly nint _layer = view.Layer.Handle;
    private readonly Queue<Input> _queue = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // On the main thread's side: sizes, copied here because the renderer runs on its own thread.
    private volatile int _width, _height, _pixelWidth, _pixelHeight;
    private volatile bool _active = true;
    private float _scale = 1f;

    // On the editor thread's side.
    private double2 _cursor;
    private readonly bool[] _buttons = new bool[3];
    private readonly bool[] _clicked = new bool[3], _doubleClicked = new bool[3];
    private double _lastPress = double.NegativeInfinity;
    private double2 _lastPressAt;
    private readonly HashSet<GlfwKey> _keys = new();
    private readonly HashSet<int> _keysDown = new();   // the keys that went down in the frame being taken in
    private readonly Dictionary<ImGuiKey, double> _held = new();   // keys down, and when each next repeats
    private readonly HashSet<ImGuiKey> _pressed = new(), _repeated = new();
    private readonly List<uint> _typed = new();
    private float _wheel, _wheelH;
    private Vector2 _swipe;
    private bool _wantsText;

    public event Action<double>? PinchZoom;

    // ---- posted from the main thread ---------------------------------------------------------

    private void Post(Input input)
    {
        lock (_queue) _queue.Enqueue(input);
    }

    public void PostMove(double x, double y) => Post(new Input(Kind.Move, x, y));
    public void PostButton(GlfwMouseButton button, bool down) => Post(new Input(Kind.Button, Code: (int)button, Down: down));
    public void PostKey(int hidUsage, bool down) => Post(new Input(Kind.Key, Code: hidUsage, Down: down));
    public void PostCharacter(uint codepoint) => Post(new Input(Kind.Character, Code: (int)codepoint));
    public void PostWheel(double x, double y) => Post(new Input(Kind.Wheel, x, y));
    public void PostSwipe(double x, double y) => Post(new Input(Kind.Swipe, x, y));
    public void PostPinch(double scale) => Post(new Input(Kind.Pinch, scale));

    public void UpdateSize()
    {
        _scale = (float)view.ContentScaleFactor;
        _width = (int)view.Bounds.Width;
        _height = (int)view.Bounds.Height;
        _pixelWidth = (int)(view.Bounds.Width * view.ContentScaleFactor);
        _pixelHeight = (int)(view.Bounds.Height * view.ContentScaleFactor);
    }

    public void SetActive(bool active) => _active = active;

    // ---- IAppWindow ----------------------------------------------------------------------------

    public IEnumerable<string> InstanceExtensions => ["VK_KHR_surface", "VK_EXT_metal_surface"];
    public bool HasVulkanLoader => false;

    public ulong CreateSurface(Instance instance)
    {
        var info = new VkMetalSurfaceCreateInfoEXT { Layer = (void*)_layer };
        instance.CreateMetalSurfaceEXT(in info, null, out var surface);
        return (ulong)surface.VkHandle;
    }

    public int2 Size => new(_width, _height);
    public int2 FramebufferSize => _active ? new int2(_pixelWidth, _pixelHeight) : new int2(0, 0);   // zero pauses the renderer
    public float ContentScale => _scale;
    public bool ShouldClose { get; set; }
    public string Title { get; set; } = "";
    public double2 CursorPos => _cursor;
    public GlfwKeyAction GetKey(GlfwKey key) => _keys.Contains(key) ? GlfwKeyAction.Press : GlfwKeyAction.Release;
    public GlfwButtonAction GetMouseButton(GlfwMouseButton button) => _buttons[(int)button] ? GlfwButtonAction.Press : GlfwButtonAction.Release;
    public void SetInputMode(GlfwInputMode mode, int value) { }

    public bool WantsTextInput
    {
        get => _wantsText;
        set
        {
            if (value == _wantsText) return;
            _wantsText = value;
            view.BeginInvokeOnMainThread(() => view.SetTyping(value));
        }
    }

    public KeyboardPreference? KeyboardPreference
    {
        get => view.Keyboard;
        set
        {
            if (value is { } preference) view.BeginInvokeOnMainThread(() => view.Keyboard = preference);
        }
    }

    public void PollEvents()
    {
        Array.Clear(_clicked);
        Array.Clear(_doubleClicked);
        _pressed.Clear();
        _repeated.Clear();
        _typed.Clear();
        _wheel = _wheelH = 0f;
        _swipe = Vector2.Zero;
        double now = _clock.Elapsed.TotalSeconds;

        lock (_queue)
        {
            bool moved = false, pressed = false;
            _keysDown.Clear();
            while (_queue.TryPeek(out var input))
            {
                if (input.Kind == Kind.Button && (moved || pressed)) break;   // the rest waits for the next frame
                if (input.Kind == Kind.Key && !input.Down && _keysDown.Contains(input.Code)) break;   // or a quick tap would never be seen as down
                _queue.Dequeue();
                switch (input.Kind)
                {
                    case Kind.Move:
                        moved = true;
                        _cursor = new double2(input.X, input.Y);
                        break;
                    case Kind.Button:
                        pressed = true;
                        Button(input.Code, input.Down, now);
                        break;
                    case Kind.Key:
                        if (input.Down) _keysDown.Add(input.Code);
                        Key(input.Code, input.Down, now);
                        break;
                    case Kind.Character:
                        _typed.Add((uint)input.Code);
                        break;
                    case Kind.Wheel:
                        _wheel += (float)input.Y / PointsPerWheelNotch;
                        _wheelH += (float)input.X / PointsPerWheelNotch;
                        break;
                    case Kind.Swipe:
                        if (PointerOverScene)
                            _swipe += new Vector2((float)input.X, (float)input.Y);
                        else
                        {
                            _wheel += (float)input.Y / PointsPerWheelNotch;
                            _wheelH += (float)input.X / PointsPerWheelNotch;
                        }
                        break;
                    case Kind.Pinch:
                        PinchZoom?.Invoke(input.X);
                        break;
                }
            }
        }

        foreach (var key in _held.Keys.ToList())
            if (now >= _held[key])
            {
                _repeated.Add(key);
                _held[key] = now + RepeatInterval;
            }
    }

    private void Button(int button, bool down, double now)
    {
        _buttons[button] = down;
        if (!down) return;
        _clicked[button] = true;
        if (button != (int)GlfwMouseButton.Left) return;
        double dx = _cursor.X - _lastPressAt.X, dy = _cursor.Y - _lastPressAt.Y;
        _doubleClicked[button] = now - _lastPress < DoubleTapSeconds && dx * dx + dy * dy < DoubleTapPoints * DoubleTapPoints;
        _lastPress = _doubleClicked[button] ? double.NegativeInfinity : now;
        _lastPressAt = _cursor;
    }

    private void Key(int hidUsage, bool down, double now)
    {
        if (!KeyMap.Find(hidUsage, out var glfw, out var imgui)) return;
        if (down)
        {
            _keys.Add(glfw);
            _pressed.Add(imgui);
            _held[imgui] = now + RepeatDelay;
        }
        else
        {
            _keys.Remove(glfw);
            _held.Remove(imgui);
        }
    }

    // ---- IUiInput ------------------------------------------------------------------------------

    public float2 DisplaySize => new(_width, _height);
    public float2 DisplayFramebufferScale => new(_scale, _scale);
    public float MouseWheel => _wheel;
    public float MouseWheelH => _wheelH;
    public bool KeyCtrl => _keys.Contains(GlfwKey.LeftControl) || _keys.Contains(GlfwKey.RightControl);
    public bool KeyShift => _keys.Contains(GlfwKey.LeftShift) || _keys.Contains(GlfwKey.RightShift);
    public bool KeyAlt => _keys.Contains(GlfwKey.LeftAlt) || _keys.Contains(GlfwKey.RightAlt);
    public bool KeySuper => _keys.Contains(GlfwKey.LeftSuper) || _keys.Contains(GlfwKey.RightSuper);
    public IReadOnlyList<uint> InputQueueCharacters => _typed;
    public bool WantsMouse => false;
    public Vector2 SceneSwipe => _swipe;
    public bool PointerOverScene { get; set; }

    public void BeginFrame() { }
    public void Render(CommandBuffer cmd) { }
    public bool IsKeyPressed(ImGuiKey key, bool repeat = true) => _pressed.Contains(key) || (repeat && _repeated.Contains(key));
    public bool IsMouseClicked(ImGuiMouseButton button) => _clicked[(int)button];
    public bool IsMouseDoubleClicked(ImGuiMouseButton button) => _doubleClicked[(int)button];

    public string GetClipboardText()
    {
        string text = "";
        view.InvokeOnMainThread(() => text = UIPasteboard.General.String ?? "");
        return text;
    }

    public void SetClipboardText(string text) => view.BeginInvokeOnMainThread(() => UIPasteboard.General.String = text);

    public void Dispose() { }
}

// USB HID key codes, which UIKit reports, as GLFW and ImGui name them.
public static class KeyMap
{
    private static readonly Dictionary<int, (GlfwKey, ImGuiKey)> Keys = Build();

    public static bool Find(int hidUsage, out GlfwKey glfw, out ImGuiKey imgui)
    {
        bool found = Keys.TryGetValue(hidUsage, out var keys);
        (glfw, imgui) = keys;
        return found;
    }

    private static Dictionary<int, (GlfwKey, ImGuiKey)> Build()
    {
        var keys = new Dictionary<int, (GlfwKey, ImGuiKey)>
        {
            [39] = (GlfwKey.Number0, ImGuiKey._0), [40] = (GlfwKey.Enter, ImGuiKey.Enter), [41] = (GlfwKey.Escape, ImGuiKey.Escape),
            [42] = (GlfwKey.Backspace, ImGuiKey.Backspace), [43] = (GlfwKey.Tab, ImGuiKey.Tab), [44] = (GlfwKey.Space, ImGuiKey.Space),
            [45] = (GlfwKey.Minus, ImGuiKey.Minus), [46] = (GlfwKey.Equal, ImGuiKey.Equal), [47] = (GlfwKey.LeftBracket, ImGuiKey.LeftBracket),
            [48] = (GlfwKey.RightBracket, ImGuiKey.RightBracket), [49] = (GlfwKey.Backslash, ImGuiKey.Backslash),
            [51] = (GlfwKey.Semicolon, ImGuiKey.Semicolon), [52] = (GlfwKey.Apostrophe, ImGuiKey.Apostrophe),
            [53] = (GlfwKey.GraveAccent, ImGuiKey.GraveAccent), [54] = (GlfwKey.Comma, ImGuiKey.Comma), [55] = (GlfwKey.Period, ImGuiKey.Period),
            [56] = (GlfwKey.Slash, ImGuiKey.Slash), [73] = (GlfwKey.Insert, ImGuiKey.Insert), [74] = (GlfwKey.Home, ImGuiKey.Home),
            [75] = (GlfwKey.PageUp, ImGuiKey.PageUp), [76] = (GlfwKey.Delete, ImGuiKey.Delete), [77] = (GlfwKey.End, ImGuiKey.End),
            [78] = (GlfwKey.PageDown, ImGuiKey.PageDown), [79] = (GlfwKey.Right, ImGuiKey.RightArrow), [80] = (GlfwKey.Left, ImGuiKey.LeftArrow),
            [81] = (GlfwKey.Down, ImGuiKey.DownArrow), [82] = (GlfwKey.Up, ImGuiKey.UpArrow), [88] = (GlfwKey.KpEnter, ImGuiKey.KeypadEnter),
            [224] = (GlfwKey.LeftControl, ImGuiKey.LeftCtrl), [225] = (GlfwKey.LeftShift, ImGuiKey.LeftShift), [226] = (GlfwKey.LeftAlt, ImGuiKey.LeftAlt),
            [227] = (GlfwKey.LeftSuper, ImGuiKey.LeftSuper), [228] = (GlfwKey.RightControl, ImGuiKey.RightCtrl), [229] = (GlfwKey.RightShift, ImGuiKey.RightShift),
            [230] = (GlfwKey.RightAlt, ImGuiKey.RightAlt), [231] = (GlfwKey.RightSuper, ImGuiKey.RightSuper),
        };
        for (int i = 0; i < 26; i++) keys[4 + i] = (GlfwKey.A + i, ImGuiKey.A + i);          // A to Z
        for (int i = 0; i < 9; i++) keys[30 + i] = (GlfwKey.Number1 + i, ImGuiKey._1 + i);   // 1 to 9
        for (int i = 0; i < 12; i++) keys[58 + i] = (GlfwKey.F1 + i, ImGuiKey.F1 + i);       // F1 to F12
        return keys;
    }
}
