using System.Text;
using Brutal.GlfwApi;
using Brutal.Numerics;

namespace Editor;

// Pointer and keyboard state for the visual scripting view, read from GLFW once per frame.
public sealed class VcInputState
{
    private static readonly (GlfwKey Key, char Plain, char Shifted)[] Typable = BuildTypable();
    private readonly bool[] _prev = new bool[Typable.Length];
    private bool _wasDown;
    private bool _prevBackspace;
    private bool _prevEnter;
    private bool _prevEscape;
    private bool _prevDuplicate;

    public float2 Mouse { get; private set; }
    public bool Down { get; private set; }
    public bool Pressed { get; private set; }
    public bool Released { get; private set; }
    public string Typed { get; private set; } = "";
    public bool Backspace { get; private set; }
    public bool Enter { get; private set; }
    public bool Escape { get; private set; }
    public bool Duplicate { get; private set; }   // Cmd+D on macOS, Ctrl+D elsewhere

    public void Update(GlfwWindow window)
    {
        var cursor = window.CursorPos;
        Mouse = new float2((float)cursor.X, (float)cursor.Y);

        bool down = window.GetMouseButton(GlfwMouseButton.Left) == GlfwButtonAction.Press;
        Pressed = down && !_wasDown;
        Released = !down && _wasDown;
        Down = down;
        _wasDown = down;

        bool shift = window.GetKey(GlfwKey.LeftShift) == GlfwKeyAction.Press;
        var typed = new StringBuilder();
        for (int i = 0; i < Typable.Length; i++)
        {
            bool now = window.GetKey(Typable[i].Key) == GlfwKeyAction.Press;
            if (now && !_prev[i])
                typed.Append(shift ? Typable[i].Shifted : Typable[i].Plain);
            _prev[i] = now;
        }
        Typed = typed.ToString();

        Backspace = Edge(window, GlfwKey.Backspace, ref _prevBackspace);
        Enter = Edge(window, GlfwKey.Enter, ref _prevEnter);
        Escape = Edge(window, GlfwKey.Escape, ref _prevEscape);

        bool duplicate = ShortcutHeld(window) && window.GetKey(GlfwKey.D) == GlfwKeyAction.Press;
        Duplicate = duplicate && !_prevDuplicate;
        _prevDuplicate = duplicate;
    }

    private static bool ShortcutHeld(GlfwWindow window)
    {
        if (OperatingSystem.IsMacOS())
            return window.GetKey(GlfwKey.LeftSuper) == GlfwKeyAction.Press ||
                   window.GetKey(GlfwKey.RightSuper) == GlfwKeyAction.Press;

        return window.GetKey(GlfwKey.LeftControl) == GlfwKeyAction.Press ||
               window.GetKey(GlfwKey.RightControl) == GlfwKeyAction.Press;
    }

    private static bool Edge(GlfwWindow window, GlfwKey key, ref bool previous)
    {
        bool now = window.GetKey(key) == GlfwKeyAction.Press;
        bool pressed = now && !previous;
        previous = now;
        return pressed;
    }

    private static (GlfwKey Key, char Plain, char Shifted)[] BuildTypable()
    {
        var list = new List<(GlfwKey Key, char Plain, char Shifted)>();
        for (char c = 'A'; c <= 'Z'; c++)
            if (TryKey(c.ToString(), out var letter))
                list.Add((letter, char.ToLowerInvariant(c), c));
        for (int d = 0; d <= 9; d++)
        {
            if (TryKey($"Number{d}", out var digit) || TryKey($"D{d}", out digit) || TryKey($"Digit{d}", out digit))
                list.Add((digit, (char)('0' + d), (char)('0' + d)));
            if (TryKey($"Kp{d}", out var pad))
                list.Add((pad, (char)('0' + d), (char)('0' + d)));
        }
        if (TryKey("Space", out var space)) list.Add((space, ' ', ' '));
        if (TryKey("Minus", out var minus)) list.Add((minus, '-', '_'));
        if (TryKey("Period", out var period)) list.Add((period, '.', '.'));
        if (TryKey("KpDecimal", out var padPeriod)) list.Add((padPeriod, '.', '.'));
        if (TryKey("KpSubtract", out var padMinus)) list.Add((padMinus, '-', '-'));
        return list.ToArray();
    }

    private static bool TryKey(string name, out GlfwKey key) =>
        Enum.TryParse(name, ignoreCase: true, out key);
}
