using System.Globalization;
using System.Numerics;
using System.Text;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace Editor.Ui;

[Flags]
public enum WindowFlags
{
    None = 0,
    NoTitle = 1,
    NoScroll = 2,
    NoPadding = 4,
    NoBackground = 8,
    Popup = 16,      // drawn above panels; closes when the mouse is pressed outside it
    Modal = 32,      // blocks every other window while open
    Closable = 64,   // an X in the title bar; BeginWindow returns false on the frame it is clicked
}

// The editor's immediate-mode UI, drawn with Canvas2D like the VC and code views. Calls are shaped like ImGui's
// (Button, Selectable, DragFloat3, Checkbox, InputText, Combo, CollapsingHeader, menus...), so panels read the same.
//
// Each frame: Begin, then windows and widgets, then End. Labels follow ImGui's "Text##id" rule: the part after ##
// is only for telling widgets apart. Which window is under the mouse comes from the previous frame's layout, so a
// popup drawn late in the frame still takes clicks before the panel under it.
public sealed class Gui
{
    public const float RowHeight = 22f;
    public const float MenuBarHeight = 26f;
    private const float TitleHeight = 26f;
    private const float Padding = 8f;
    private const float SpacingX = 6f;
    private const float SpacingY = 4f;
    private const float DragThreshold = 3f;

    public static float4 WindowColor => Theme.Pick(0x252526, 0xF3F3F3);
    public static float4 TitleColor => Theme.Pick(0x2D2D30, 0xE7E7E7);
    public static float4 BorderColor => Theme.Pick(0x3F3F46, 0xC8C8C8);
    public static float4 TextColor => Theme.Pick(0xDCDCDC, 0x1F1F1F);
    public static float4 DimColor => Theme.Pick(0x8B8B8B, 0x6E6E6E);
    public static float4 ButtonColor => Theme.Pick(0x3A3D41, 0xDDDDDD);
    public static float4 ButtonHover => Theme.Pick(0x4A4E54, 0xCFCFCF);
    public static float4 ButtonDown => Theme.Pick(0x55595F, 0xBFBFBF);
    public static float4 AccentColor => Theme.Pick(0x0E639C, 0x1A73C8);
    public static float4 AccentHover => Theme.Pick(0x1177BB, 0x0B5CAD);
    public static float4 FieldColor => Theme.Pick(0x1E1E1E, 0xFFFFFF);
    public static float4 FieldHover => Theme.Pick(0x2A2A2A, 0xF0F0F0);
    public static float4 RowHover => Theme.Pick(0x2A2D2E, 0xE8E8E8);
    public static float4 RowSelected => Theme.Pick(0x094771, 0xCCE4F7);
    public static float4 HeaderColor => Theme.Pick(0x333337, 0xE3E3E3);
    public static float4 PopupColor => Theme.Pick(0x2B2B2E, 0xF8F8F8);
    public static float4 ErrorColor => Theme.Pick(0xF14C4C, 0xC42B1C);

    private sealed record WindowRec(string Id, Box Rect, int Z, bool Modal);

    // Layout state of the window (or child region) being drawn.
    private sealed class Frame
    {
        public string Id = "";
        public Box Rect;
        public Box Content;     // the visible area widgets go in
        public Box? Clip;
        public int Layer;
        public float CursorY;   // where the next line starts
        public float LineX;     // left edge of lines, after indentation
        public float Indent;
        public float PrevRight, PrevTop, PrevHeight;
        public bool SameLine;
        public float MaxY;      // lowest point any item reached, for the content height
        public float Scroll;
        public bool Scrolls;
        public float MenuX;     // next menu title's x, in the menu bar
    }

    public Canvas2D C { get; private set; } = null!;
    public float2 Size { get; private set; }

    // Input, read once per frame in Begin.
    public float2 Mouse { get; private set; }
    public bool MouseDown { get; private set; }
    public bool MousePressed { get; private set; }
    public bool MouseReleased { get; private set; }
    public bool RightPressed { get; private set; }
    public bool Shift { get; private set; }
    public bool Command { get; private set; }   // Cmd on macOS, Ctrl elsewhere (either is accepted)
    public bool EnterPressed { get; private set; }   // Return or Enter went down this frame
    public float Wheel => _wheel;                    // this frame's vertical scroll
    public string TypedAll { get; private set; } = "";   // every printable character typed this frame, including non-ASCII (empty while Cmd/Ctrl is held)
    public string Typed => _typed;                   // printable characters typed this frame (none while Cmd/Ctrl is held)
    private bool _doubleClicked;
    private float _wheel;
    private string _typed = "";
    private bool _prevDown, _prevRight;
    private double _now;

    private List<WindowRec> _windows = new();
    private List<WindowRec> _prevWindows = new();
    private string? _hovered;          // the window under the mouse, from last frame's layout
    private int _z;
    private readonly Stack<Frame> _stack = new();
    private Frame _w = new();
    private readonly List<string> _ids = new();

    private string? _active;           // the widget the mouse went down on
    private bool _activeSeen;
    private float2 _activeStart;
    private float _dragStartValue;
    private bool _dragMoved;

    private string? _focus;            // the text or number field being typed in
    private bool _focusSeen;
    private string _editText = "";
    private bool _focusNumeric;
    private bool _focusJustSet;
    private bool _focusNextInput;
    private double _caretReset;

    private readonly Dictionary<string, float> _scroll = new();
    private readonly Dictionary<string, float> _contentHeight = new();
    private readonly HashSet<string> _scrollToEnd = new();   // child regions to show the bottom of this frame
    private readonly Dictionary<string, bool> _open = new();
    private readonly Dictionary<string, float> _popupHeights = new();
    private string? _openPopup;
    private float2 _popupPos;
    private float _popupWidth;
    private string? _openMenu;

    public Box LastItem { get; private set; }
    public bool LastItemHovered { get; private set; }

    // True on the frame the last item was double-clicked with the left mouse button.
    public bool LastItemDoubleClicked => LastItemHovered && _doubleClicked;

    // True when the mouse is over a window, or a widget is being dragged: the 3D view should leave the mouse alone.
    public bool WantsMouse => _hovered is not null || _active is not null;
    public bool IsTyping => _focus is not null || ExternalTyping;
    public bool ExternalTyping { get; set; }         // set while something that isn't a text field (the terminal) has the keyboard, so editor shortcuts stand down
    public bool AnyActive => _active is not null || _focus is not null;

    // ---- frame --------------------------------------------------------------------------------

    public void Begin(GlfwWindow window, Canvas2D canvas, float2 size, double now)
    {
        C = canvas;
        Size = size;
        _now = now;
        canvas.Begin();

        var cursor = window.CursorPos;
        Mouse = new float2((float)cursor.X, (float)cursor.Y);
        bool down = window.GetMouseButton(GlfwMouseButton.Left) == GlfwButtonAction.Press;
        bool right = window.GetMouseButton(GlfwMouseButton.Right) == GlfwButtonAction.Press;
        MousePressed = down && !_prevDown;
        MouseReleased = !down && _prevDown;
        MouseDown = down;
        RightPressed = right && !_prevRight;
        _prevDown = down;
        _prevRight = right;

        var io = ImGui.GetIO();
        Shift = io.KeyShift;
        EnterPressed = ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false);
        Command = io.KeyCtrl || io.KeySuper;
        _wheel = io.MouseWheel;
        _doubleClicked = ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
        var typed = new StringBuilder();
        var queue = io.InputQueueCharacters;
        for (int i = 0; i < queue.Count; i++)
            if (queue[i] is >= 32 and < 127) typed.Append((char)queue[i]);
        _typed = Command ? "" : typed.ToString();
        var typedAll = new StringBuilder();
        for (int i = 0; i < queue.Count; i++)
            if (queue[i] >= 32 && queue[i] != 127 && !(queue[i] >= 0x80 && queue[i] < 0xA0) && !(queue[i] >= 0xF700 && queue[i] <= 0xF8FF)) typedAll.Append((char)queue[i]);
        TypedAll = io.KeyCtrl || io.KeySuper ? "" : typedAll.ToString();

        // The window under the mouse: the topmost one from last frame, and only a modal (or something above it)
        // while a modal is open.
        _prevWindows = _windows;
        _windows = new List<WindowRec>();
        int floor = _prevWindows.Where(w => w.Modal).Select(w => w.Z).DefaultIfEmpty(int.MinValue).Max();
        _hovered = _prevWindows.Where(w => w.Z >= floor && w.Rect.Contains(Mouse)).OrderByDescending(w => w.Z)
            .FirstOrDefault()?.Id;
        if (floor != int.MinValue && _hovered is null) _hovered = "##modal-backdrop";   // eat clicks around a modal

        // A press anywhere outside an open popup or menu closes it.
        if ((MousePressed || RightPressed) && _openPopup is not null && !IsPopupWindow(_hovered))
            _openPopup = null;
        if (MousePressed && _openMenu is not null && !IsPopupWindow(_hovered) && _hovered != "##menubar")
            _openMenu = null;

        _z = 0;
        _activeSeen = false;
        _focusSeen = false;
        _focusJustSet = false;
        _stack.Clear();
        _ids.Clear();
        _w = new Frame
        {
            Id = "##screen", Rect = new Box(0, 0, size.X, size.Y), Content = new Box(0, 0, size.X, size.Y),
            Layer = Canvas2D.PanelLayer,
        };
    }

    public void End()
    {
        _scrollToEnd.Clear();
        if (!MouseDown || !_activeSeen) _active = null;
        if (!_focusSeen) _focus = null;
        C.Layer = Canvas2D.PanelLayer;
        C.Clip = null;
    }

    private static bool IsPopupWindow(string? id) => id is not null && id.StartsWith("##popup:", StringComparison.Ordinal);

    // ---- windows ------------------------------------------------------------------------------

    // Starts a window at a fixed rectangle. Returns false on the frame its close button is clicked; call EndWindow
    // either way.
    public bool BeginWindow(string id, Box rect, string? title = null, WindowFlags flags = WindowFlags.None)
    {
        bool popup = flags.HasFlag(WindowFlags.Popup);
        bool modal = flags.HasFlag(WindowFlags.Modal);
        int z = ++_z + (popup ? 100_000 : 0) + (modal ? 50_000 : 0);
        _windows.Add(new WindowRec(id, rect, z, modal));

        _stack.Push(_w);
        var frame = new Frame { Id = id, Rect = rect, Layer = popup || modal ? Canvas2D.PopupLayer : Canvas2D.PanelLayer };
        C.Layer = frame.Layer;
        C.Clip = null;

        if (modal)
            C.Rect(0, 0, Size.X, Size.Y, new float4(0f, 0f, 0f, 0.35f));
        if (!flags.HasFlag(WindowFlags.NoBackground))
        {
            C.Rect(rect.X, rect.Y, rect.W, rect.H, popup ? PopupColor : WindowColor);
            C.Outline(rect.X, rect.Y, rect.W, rect.H, 1f, BorderColor);
        }

        bool stayOpen = true;
        float top = rect.Y;
        if (title is not null && !flags.HasFlag(WindowFlags.NoTitle))
        {
            C.Rect(rect.X + 1, rect.Y + 1, rect.W - 2, TitleHeight - 1, TitleColor);
            C.Text(Fit(title, rect.W - 40f), rect.X + Padding, TextTop(rect.Y, TitleHeight), TextColor);
            if (flags.HasFlag(WindowFlags.Closable))
            {
                var close = new Box(rect.X + rect.W - TitleHeight, rect.Y, TitleHeight, TitleHeight);
                bool hover = _hovered == id && close.Contains(Mouse);
                if (hover) C.Rect(close.X + 3, close.Y + 3, close.W - 6, close.H - 6, ButtonHover);
                C.Text("x", close.X + (close.W - C.MeasureText("x")) / 2f, TextTop(close.Y, close.H) - 1f, TextColor);
                if (hover && MousePressed) stayOpen = false;
            }
            top += TitleHeight;
        }

        float pad = flags.HasFlag(WindowFlags.NoPadding) ? 0f : Padding;
        frame.Content = new Box(rect.X + pad, top + pad, rect.W - pad * 2, rect.Y + rect.H - top - pad * 2);
        frame.Clip = new Box(rect.X + 1, top, rect.W - 2, rect.Y + rect.H - top - 1);
        frame.Scrolls = !flags.HasFlag(WindowFlags.NoScroll);
        BeginLayout(frame);
        return stayOpen;
    }

    public void EndWindow()
    {
        FinishLayout(_w);
        _w = _stack.Pop();
        C.Layer = _w.Layer;
        C.Clip = _w.Clip;
    }

    // A scrolling region inside the current window, w x h (0 or less fills the rest of that side).
    public void BeginChild(string id, float w, float h, bool border = true)
    {
        string full = _w.Id + "/" + id;
        float availW = AvailWidth;
        float top = _w.SameLine ? _w.PrevTop : _w.CursorY;
        float width = w <= 0 ? availW + w : w;
        float height = h <= 0 ? _w.Content.Y + _w.Content.H - top + h : h;
        var rect = Reserve(width, Math.Max(20f, height));
        _windows.Add(new WindowRec(full, ClipBox(rect), ++_z + (_w.Layer == Canvas2D.PopupLayer ? 100_000 : 0), false));

        _stack.Push(_w);
        var frame = new Frame { Id = full, Rect = rect, Layer = _w.Layer };
        if (border)
        {
            C.Rect(rect.X, rect.Y, rect.W, rect.H, FieldColor);
            C.Outline(rect.X, rect.Y, rect.W, rect.H, 1f, BorderColor);
        }
        frame.Content = new Box(rect.X + 6, rect.Y + 6, rect.W - 12, rect.H - 12);
        var clip = new Box(rect.X + 1, rect.Y + 1, rect.W - 2, rect.H - 2);
        frame.Clip = _w.Clip is { } parent ? Intersect(clip, parent) : clip;
        frame.Scrolls = true;
        BeginLayout(frame);
    }

    public void EndChild() => EndWindow();

    // Shows the bottom of a child region (by the id given to BeginChild, in the current window) this frame, for a chat that
    // follows its newest message. The content height it uses is last frame's, so call it for a few frames after a change.
    public void ScrollChildToEnd(string childId) => _scrollToEnd.Add(_w.Id + "/" + childId);

    private void BeginLayout(Frame frame)
    {
        if (frame.Scrolls)
        {
            float scroll = _scroll.GetValueOrDefault(frame.Id);
            if (_scrollToEnd.Contains(frame.Id)) scroll = float.MaxValue;   // clamped just below to the end of last frame's content
            if (_hovered == frame.Id && _wheel != 0) scroll -= _wheel * RowHeight * 3;
            float max = Math.Max(0f, _contentHeight.GetValueOrDefault(frame.Id) - frame.Content.H);
            frame.Scroll = Math.Clamp(scroll, 0f, max);
            _scroll[frame.Id] = frame.Scroll;
        }
        frame.CursorY = frame.Content.Y - frame.Scroll;
        frame.LineX = frame.Content.X;
        frame.MaxY = frame.CursorY;
        _w = frame;
        C.Clip = frame.Clip;
    }

    private void FinishLayout(Frame frame)
    {
        float contentH = frame.MaxY - (frame.Content.Y - frame.Scroll);
        _contentHeight[frame.Id] = contentH;
        if (frame.Scrolls && contentH > frame.Content.H + 1f)
        {
            // A thin bar on the right showing where the view is.
            var track = new Box(frame.Rect.X + frame.Rect.W - 5f, frame.Content.Y, 3f, frame.Content.H);
            float thumbH = Math.Max(20f, track.H * frame.Content.H / contentH);
            float thumbY = track.Y + (track.H - thumbH) * (frame.Scroll / Math.Max(1f, contentH - frame.Content.H));
            C.Rect(track.X, thumbY, track.W, thumbH, ButtonHover);
        }
    }

    // ---- popups and menus -----------------------------------------------------------------------

    // Opens a context menu where the mouse is when the current window (or the last item) is right-clicked.
    public bool BeginContextMenu(string id, bool forLastItem = false, float width = 200f)
    {
        bool trigger = forLastItem ? LastItemHovered : _hovered == _w.Id && ClipBox(_w.Rect).Contains(Mouse);
        if (RightPressed && trigger)
            OpenPopup(id, Mouse, width);
        return BeginPopup(id);
    }

    public void OpenPopup(string id, float2 at, float width)
    {
        _openPopup = id;
        _popupPos = at;
        _popupWidth = width;
    }

    public bool IsPopupOpen(string id) => _openPopup == id;

    public bool BeginPopup(string id)
    {
        if (_openPopup != id) return false;
        float height = _popupHeights.GetValueOrDefault(id, 200f);
        float x = Math.Min(_popupPos.X, Size.X - _popupWidth - 4f);
        float y = _popupPos.Y + height > Size.Y - 4f ? Math.Max(4f, _popupPos.Y - height) : _popupPos.Y;
        BeginWindow("##popup:" + id, new Box(x, y, _popupWidth, height), flags: WindowFlags.Popup | WindowFlags.NoScroll);
        _w.Content = new Box(x + 4, y + 4, _popupWidth - 8, height - 8);
        _w.CursorY = _w.Content.Y;
        _w.LineX = _w.Content.X;
        _w.MaxY = _w.CursorY;
        return true;
    }

    public void EndPopup()
    {
        // Sized to its contents from the next frame on.
        _popupHeights[_w.Id["##popup:".Length..]] = _w.MaxY - _w.Rect.Y + 4f;
        EndWindow();
    }

    public void CloseCurrentPopup()
    {
        _openPopup = null;
        _openMenu = null;
    }

    public void BeginMenuBar()
    {
        BeginWindow("##menubar", new Box(0, 0, Size.X, MenuBarHeight), flags: WindowFlags.NoPadding | WindowFlags.NoScroll);
        C.Rect(0, 0, Size.X, MenuBarHeight, TitleColor);
        C.Rect(0, MenuBarHeight - 1, Size.X, 1, BorderColor);
        _w.MenuX = 6f;
    }

    public void EndMenuBar() => EndWindow();

    // A menu title in the menu bar. Returns true while its menu is open; then call MenuItem and EndMenu.
    public bool BeginMenu(string label)
    {
        float w = C.MeasureText(label) + 20f;
        var box = new Box(_w.MenuX, 0, w, MenuBarHeight - 1);
        _w.MenuX += w;
        bool hover = _hovered == "##menubar" && box.Contains(Mouse);
        if (hover && MousePressed) _openMenu = _openMenu == label ? null : label;
        else if (hover && _openMenu is not null && _openMenu != label) _openMenu = label;   // slide between menus

        bool open = _openMenu == label;
        if (hover || open) C.Rect(box.X, box.Y, box.W, box.H, open ? ButtonDown : ButtonHover);
        C.Text(label, box.X + 10f, TextTop(box.Y, box.H), TextColor);
        if (!open) return false;

        string popup = "menu:" + label;
        _openPopup = popup;
        _popupPos = new float2(box.X, MenuBarHeight);
        _popupWidth = 230f;
        return BeginPopup(popup);
    }

    public void EndMenu() => EndPopup();

    public bool MenuItem(string label, string? shortcut = null, bool enabled = true)
    {
        var box = Reserve(AvailWidth, RowHeight);
        string id = Id(label);
        bool clicked = Behavior(id, box, out bool hover, out _) && enabled;
        if (hover && enabled) C.Rect(box.X, box.Y, box.W, box.H, AccentColor);
        C.Text(Display(label), box.X + 8f, TextTop(box.Y, box.H), enabled ? TextColor : DimColor);
        if (shortcut is not null)
            C.Text(shortcut, box.X + box.W - C.MeasureText(shortcut) - 8f, TextTop(box.Y, box.H), DimColor);
        if (clicked) CloseCurrentPopup();
        return clicked;
    }

    // ---- layout -------------------------------------------------------------------------------

    public float AvailWidth => _w.Content.X + _w.Content.W - (_w.SameLine ? _w.PrevRight + SpacingX : _w.LineX);

    public void SameLine() => _w.SameLine = true;

    public void Spacing(float h = 6f)
    {
        _w.CursorY += h;
        _w.MaxY = Math.Max(_w.MaxY, _w.CursorY);
    }

    public void Indent(float w = 14f)
    {
        _w.Indent += w;
        _w.LineX = _w.Content.X + _w.Indent;
    }

    public void Unindent(float w = 14f) => Indent(-w);

    public void Separator()
    {
        var box = Reserve(_w.Content.X + _w.Content.W - _w.LineX, 7f);
        C.Rect(box.X, box.Y + 3f, box.W, 1f, BorderColor);
    }

    // Places an item of the given size on the current line (after SameLine) or the next one, and returns its box.
    public Box Reserve(float w, float h)
    {
        float x, y;
        // An item that doesn't fit on the current line goes on the next one.
        if (_w.SameLine && _w.PrevRight + SpacingX + w > _w.Content.X + _w.Content.W + 0.5f && _w.PrevRight > _w.LineX)
            _w.SameLine = false;
        if (_w.SameLine)
        {
            x = _w.PrevRight + SpacingX;
            y = _w.PrevTop;
        }
        else
        {
            x = _w.LineX;
            y = _w.CursorY;
        }
        _w.SameLine = false;
        var box = new Box(x, y, Math.Max(0f, w), h);
        _w.PrevRight = x + box.W;
        _w.PrevTop = y;
        _w.PrevHeight = h;
        _w.CursorY = Math.Max(_w.CursorY, y + h + SpacingY);
        _w.MaxY = Math.Max(_w.MaxY, y + h);
        LastItem = box;
        LastItemHovered = Hovered(box);
        return box;
    }

    public void PushId(string id) => _ids.Add(id);
    public void PushId(int id) => _ids.Add(id.ToString(CultureInfo.InvariantCulture));
    public void PopId() => _ids.RemoveAt(_ids.Count - 1);

    private string Id(string label) => _w.Id + "|" + string.Join("/", _ids) + "|" + label;

    private static string Display(string label)
    {
        int at = label.IndexOf("##", StringComparison.Ordinal);
        return at < 0 ? label : label[..at];
    }

    // ---- interaction ----------------------------------------------------------------------------

    // True when the mouse is over the visible part of the box, in the window that has the mouse.
    public bool Hovered(Box box) => _hovered == _w.Id && box.Contains(Mouse) && ClipBox(box).Contains(Mouse);

    // Press-and-release behavior shared by buttons, rows and fields. Returns true on release over the box.
    public bool Behavior(string id, Box box, out bool hovered, out bool held)
    {
        hovered = Hovered(box);
        if (hovered && MousePressed)
        {
            _active = id;
            _activeStart = Mouse;
            _dragMoved = false;
        }
        if (_active == id) _activeSeen = true;
        held = _active == id && MouseDown;
        bool clicked = _active == id && MouseReleased && hovered;
        if (_active == id && MouseReleased) _active = null;
        return clicked;
    }

    private Box ClipBox(Box box) => _w.Clip is { } clip ? Intersect(box, clip) : box;

    private static Box Intersect(Box a, Box b)
    {
        float x0 = Math.Max(a.X, b.X), y0 = Math.Max(a.Y, b.Y);
        float x1 = Math.Min(a.X + a.W, b.X + b.W), y1 = Math.Min(a.Y + a.H, b.Y + b.H);
        return new Box(x0, y0, Math.Max(0f, x1 - x0), Math.Max(0f, y1 - y0));
    }

    // ---- text ---------------------------------------------------------------------------------------

    public void Text(string text, float4? color = null)
    {
        var box = Reserve(C.MeasureText(text), RowHeight);
        if (Visible(box)) C.Text(text, box.X, TextTop(box.Y, box.H), color ?? TextColor);
    }

    // False for items scrolled entirely out of their window, which can skip drawing.
    private bool Visible(Box box) => _w.Clip is not { } clip || (box.Y + box.H > clip.Y && box.Y < clip.Y + clip.H);

    public void TextDisabled(string text) => Text(text, DimColor);

    public void TextWrapped(string text, float4? color = null)
    {
        float width = Math.Max(40f, AvailWidth);
        foreach (var line in Wrap(text, width))
        {
            var box = Reserve(width, RowHeight - 4f);
            if (Visible(box)) C.Text(line, box.X, TextTop(box.Y, box.H), color ?? TextColor);
        }
    }

    private List<string> Wrap(string text, float width)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var word in paragraph.Split(' '))
            {
                string attempt = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && C.MeasureText(attempt) > width)
                {
                    lines.Add(line.ToString());
                    line.Clear().Append(word);
                }
                else
                {
                    line.Clear().Append(attempt);
                }
            }
            lines.Add(line.ToString());
        }
        return lines;
    }

    public float TextTop(float y, float h) => y + (h - C.FontSize) / 2f - 1f;

    public string Fit(string text, float maxWidth)
    {
        if (maxWidth <= 0) return "";
        if (C.MeasureText(text) <= maxWidth) return text;
        while (text.Length > 1 && C.MeasureText(text + "...") > maxWidth) text = text[..^1];
        return text + "...";
    }

    // ---- buttons and rows ---------------------------------------------------------------------------

    // width 0 fits the label; a negative width fills the line minus that much.
    public bool Button(string label, float width = 0f, float height = 0f, bool enabled = true, bool highlighted = false)
    {
        string text = Display(label);
        float w = width > 0 ? width : width < 0 ? AvailWidth + width : C.MeasureText(text) + 18f;
        var box = Reserve(w, height > 0 ? height : RowHeight);
        bool clicked = Behavior(Id(label), box, out bool hover, out bool held);
        var color = !enabled ? ButtonColor : highlighted ? (hover ? AccentHover : AccentColor)
            : held ? ButtonDown : hover ? ButtonHover : ButtonColor;
        C.Rect(box.X, box.Y, box.W, box.H, color);
        C.Text(Fit(text, box.W - 8f), box.X + Math.Max(4f, (box.W - C.MeasureText(text)) / 2f), TextTop(box.Y, box.H),
            !enabled ? DimColor : highlighted ? Theme.OnAccent : TextColor);
        return clicked && enabled;
    }

    // A full-width row that highlights when selected or hovered. Returns true when clicked.
    public bool Selectable(string label, bool selected, float width = 0f, bool enabled = true, float4? textColor = null)
    {
        float w = width > 0 ? width : AvailWidth;
        var box = Reserve(w, RowHeight);
        bool clicked = Behavior(Id(label), box, out bool hover, out _);
        if (selected) C.Rect(box.X, box.Y, box.W, box.H, RowSelected);
        else if (hover && enabled) C.Rect(box.X, box.Y, box.W, box.H, RowHover);
        C.Text(Fit(Display(label), box.W - 8f), box.X + 4f, TextTop(box.Y, box.H), enabled ? textColor ?? TextColor : DimColor);
        return clicked && enabled;
    }

    // A section header that opens and closes. Returns true while open.
    public bool CollapsingHeader(string label, bool defaultOpen = false)
    {
        string id = Id(label);
        bool open = _open.GetValueOrDefault(id, defaultOpen);
        var box = Reserve(_w.Content.X + _w.Content.W - _w.LineX, RowHeight);
        if (Behavior(id, box, out bool hover, out _)) open = !open;
        _open[id] = open;
        C.Rect(box.X, box.Y, box.W, box.H, hover ? ButtonHover : HeaderColor);
        C.Text(open ? "v" : ">", box.X + 6f, TextTop(box.Y, box.H), DimColor);
        C.Text(Fit(Display(label), box.W - 26f), box.X + 20f, TextTop(box.Y, box.H), TextColor);
        return open;
    }

    // ---- labeled fields -------------------------------------------------------------------------------

    // A full-width row with the label on the left. Returns the box the control goes in.
    private Box Row(string label, float height = RowHeight)
    {
        var row = Reserve(_w.Content.X + _w.Content.W - _w.LineX, height);
        string text = Display(label);
        if (text.Length == 0) return row;
        float labelW = Math.Min(row.W * 0.42f, 130f);
        C.Text(Fit(text, labelW - 6f), row.X, TextTop(row.Y, RowHeight), TextColor);
        return new Box(row.X + labelW, row.Y, row.W - labelW, height);
    }

    public bool Checkbox(string label, ref bool value)
    {
        var control = Row(label);
        var box = new Box(control.X, control.Y + 2, RowHeight - 4, RowHeight - 4);
        bool clicked = Behavior(Id(label), box, out bool hover, out _);
        if (clicked) value = !value;
        C.Rect(box.X, box.Y, box.W, box.H, hover ? FieldHover : FieldColor);
        C.Outline(box.X, box.Y, box.W, box.H, 1f, BorderColor);
        if (value) C.Rect(box.X + 4, box.Y + 4, box.W - 8, box.H - 8, AccentHover);
        return clicked;
    }

    public bool DragFloat(string label, ref float value, float speed = 0.05f, float min = float.MinValue,
        float max = float.MaxValue)
    {
        var control = Row(label);
        return DragField(Id(label), control, ref value, speed, min, max, integer: false);
    }

    public bool DragInt(string label, ref int value, float speed = 0.2f, int min = int.MinValue, int max = int.MaxValue)
    {
        var control = Row(label);
        float f = value;
        if (!DragField(Id(label), control, ref f, speed, min, max, integer: true)) return false;
        value = (int)Math.Clamp(Math.Round(f), min, max);
        return true;
    }

    public bool DragFloat2(string label, ref float2 value, float speed = 0.05f, float min = float.MinValue,
        float max = float.MaxValue)
    {
        Span<float> v = stackalloc float[] { value.X, value.Y };
        if (!DragFloats(label, v, speed, min, max)) return false;
        value = new float2(v[0], v[1]);
        return true;
    }

    public bool DragFloat3(string label, ref float3 value, float speed = 0.05f, float min = float.MinValue,
        float max = float.MaxValue)
    {
        Span<float> v = stackalloc float[] { value.X, value.Y, value.Z };
        if (!DragFloats(label, v, speed, min, max)) return false;
        value = new float3(v[0], v[1], v[2]);
        return true;
    }

    private bool DragFloats(string label, Span<float> values, float speed, float min, float max)
    {
        var control = Row(label);
        string id = Id(label);
        float gap = 3f, w = (control.W - gap * (values.Length - 1)) / values.Length;
        bool changed = false;
        for (int i = 0; i < values.Length; i++)
        {
            var box = new Box(control.X + i * (w + gap), control.Y, w, control.H);
            changed |= DragField($"{id}#{i}", box, ref values[i], speed, min, max, integer: false);
        }
        return changed;
    }

    // Colors as R, G, B, A from 0 to 1, with a swatch of the color in front.
    public bool ColorEdit(string label, ref Vector4 color)
    {
        var control = Row(label);
        string id = Id(label);
        var swatch = new Box(control.X, control.Y + 2, RowHeight - 4, RowHeight - 4);
        C.Rect(swatch.X, swatch.Y, swatch.W, swatch.H, new float4(color.X, color.Y, color.Z, 1f));
        C.Outline(swatch.X, swatch.Y, swatch.W, swatch.H, 1f, BorderColor);
        Span<float> v = stackalloc float[] { color.X, color.Y, color.Z, color.W };
        float left = swatch.X + swatch.W + 4f, gap = 3f;
        float w = (control.X + control.W - left - gap * 3) / 4f;
        bool changed = false;
        for (int i = 0; i < 4; i++)
            changed |= DragField($"{id}#{i}", new Box(left + i * (w + gap), control.Y, w, control.H), ref v[i], 0.005f, 0f, 1f, integer: false);
        if (changed) color = new Vector4(v[0], v[1], v[2], v[3]);
        return changed;
    }

    // A color as R, G, B from 0 to 1, with a swatch of it in front.
    public bool ColorEdit3(string label, ref Vector3 color)
    {
        var control = Row(label);
        string id = Id(label);
        var swatch = new Box(control.X, control.Y + 2, RowHeight - 4, RowHeight - 4);
        C.Rect(swatch.X, swatch.Y, swatch.W, swatch.H, new float4(Math.Clamp(color.X, 0f, 1f), Math.Clamp(color.Y, 0f, 1f), Math.Clamp(color.Z, 0f, 1f), 1f));
        C.Outline(swatch.X, swatch.Y, swatch.W, swatch.H, 1f, BorderColor);
        Span<float> v = stackalloc float[] { color.X, color.Y, color.Z };
        float left = swatch.X + swatch.W + 4f, gap = 3f;
        float w = (control.X + control.W - left - gap * 2) / 3f;
        bool changed = false;
        for (int i = 0; i < 3; i++)
            changed |= DragField($"{id}#{i}", new Box(left + i * (w + gap), control.Y, w, control.H), ref v[i], 0.005f, 0f, 1f, integer: false);
        if (changed) color = new Vector3(v[0], v[1], v[2]);
        return changed;
    }

    // A number box: drag left or right to change it, or click (without dragging) to type a value.
    private bool DragField(string id, Box box, ref float value, float speed, float min, float max, bool integer)
    {
        if (_focus == id)
            return EditNumber(id, box, ref value, min, max, integer);

        bool clicked = Behavior(id, box, out bool hover, out bool held);
        bool changed = false;
        if (_active == id && MousePressed && hover) _dragStartValue = value;
        if (held)
        {
            float dx = Mouse.X - _activeStart.X;
            if (MathF.Abs(dx) > DragThreshold) _dragMoved = true;
            if (_dragMoved)
            {
                float next = Math.Clamp(_dragStartValue + dx * speed * (Shift ? 10f : 1f), min, max);
                if (integer) next = MathF.Round(next);
                if (next != value)
                {
                    value = next;
                    changed = true;
                }
            }
        }
        if (clicked && !_dragMoved)
            Focus(id, Format(value, integer), numeric: true);

        C.Rect(box.X, box.Y, box.W, box.H, held ? ButtonDown : hover ? FieldHover : FieldColor);
        string text = Format(value, integer);
        C.Text(Fit(text, box.W - 6f), box.X + Math.Max(3f, (box.W - C.MeasureText(text)) / 2f), TextTop(box.Y, box.H), TextColor);
        return changed;
    }

    private bool EditNumber(string id, Box box, ref float value, float min, float max, bool integer)
    {
        _focusSeen = true;
        foreach (char ch in _typed)
            if (char.IsDigit(ch) || ch is '-' or '.' or 'e' or 'E' or '+') _editText += ch;
        HandleEditKeys();

        bool commit = Pressed(ImGuiKey.Enter) || Pressed(ImGuiKey.KeypadEnter) || Pressed(ImGuiKey.Tab) ||
                      (MousePressed && !Hovered(box) && !_focusJustSet);
        bool cancel = Pressed(ImGuiKey.Escape);
        DrawEditBox(box);

        if (cancel)
        {
            _focus = null;
            return false;
        }
        if (!commit) return false;
        _focus = null;
        if (!float.TryParse(_editText, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)) return false;
        parsed = Math.Clamp(integer ? MathF.Round(parsed) : parsed, min, max);
        if (parsed == value) return false;
        value = parsed;
        return true;
    }

    // A one-line text box. Typing changes the value as you go; Enter, Escape or a click elsewhere finishes.
    // Makes the next InputText drawn start with the keyboard focus, for a dialog that opens ready to type in.
    public void FocusNextInput() => _focusNextInput = true;

    // mask shows stars instead of the text, for something secret such as an API key. (Pasting still works.)
    public bool InputText(string label, ref string value, int maxLength = 256, bool mask = false)
    {
        var control = Row(label);
        string id = Id(label);
        if (_focusNextInput && _focus != id)
        {
            _focusNextInput = false;
            Focus(id, value, numeric: false);
        }
        if (_focus != id)
        {
            if (Behavior(id, control, out bool hover, out _))
                Focus(id, value, numeric: false);
            C.Rect(control.X, control.Y, control.W, control.H, hover ? FieldHover : FieldColor);
            C.Outline(control.X, control.Y, control.W, control.H, 1f, BorderColor);
            C.Text(Fit(mask ? new string('*', value.Length) : value, control.W - 10f), control.X + 5f, TextTop(control.Y, control.H), TextColor);
            return false;
        }

        _focusSeen = true;
        string before = _editText;
        foreach (char ch in _typed)
            if (_editText.Length < maxLength) _editText += ch;
        HandleEditKeys();
        if (_editText.Length > maxLength) _editText = _editText[..maxLength];

        if (Pressed(ImGuiKey.Enter) || Pressed(ImGuiKey.KeypadEnter) || Pressed(ImGuiKey.Escape) ||
            (MousePressed && !Hovered(control) && !_focusJustSet))
            _focus = null;
        DrawEditBox(control, mask);

        if (_editText == before && _editText == value) return false;
        bool changed = _editText != value;
        value = _editText;
        return changed;
    }

    private void Focus(string id, string text, bool numeric)
    {
        _focus = id;
        _focusSeen = true;
        _focusJustSet = true;
        _editText = text;
        _focusNumeric = numeric;
        _caretReset = _now;
    }

    private void HandleEditKeys()
    {
        if (_typed.Length > 0) _caretReset = _now;
        if (Pressed(ImGuiKey.Backspace) && _editText.Length > 0)
        {
            _editText = Command ? "" : _editText[..^1];
            _caretReset = _now;
        }
        if (Command && Pressed(ImGuiKey.V))
        {
            var clip = ImGui.GetClipboardText();
            string pasted = clip.IsNull() ? "" : Encoding.UTF8.GetString(clip.AsSpan()).Replace("\r", "").Replace("\n", " ");
            _editText += _focusNumeric ? new string(pasted.Where(c => char.IsDigit(c) || c is '-' or '.' or 'e' or 'E').ToArray()) : pasted;
        }
        if (Command && Pressed(ImGuiKey.C))
            ImGui.SetClipboardText(_editText.ToU8());
        if (Command && Pressed(ImGuiKey.A)) _editText = "";   // select-all-and-replace, the common case
    }

    private void DrawEditBox(Box box, bool mask = false)
    {
        C.Rect(box.X, box.Y, box.W, box.H, FieldColor);
        C.Outline(box.X, box.Y, box.W, box.H, 1f, AccentHover);
        // Show the end of the text when it doesn't fit, so the caret stays visible.
        string shown = mask ? new string('*', _editText.Length) : _editText;
        while (shown.Length > 0 && C.MeasureText(shown) > box.W - 14f) shown = shown[1..];
        C.Text(shown, box.X + 5f, TextTop(box.Y, box.H), TextColor);
        if ((_now - _caretReset) % 1.0 < 0.6)
            C.Rect(box.X + 6f + C.MeasureText(shown), box.Y + 4f, 1.5f, box.H - 8f, TextColor);
    }

    private static bool Pressed(ImGuiKey key) => ImGui.IsKeyPressed(key, true);

    // A slider that snaps to a few named stops (such as 2x, 4x, 8x). Drag or click to choose one. Stops from dimFrom
    // on are drawn dim, for choices that are allowed but not available here. Returns true when the stop changes.
    public bool SliderStops(string label, ref int index, IReadOnlyList<string> stops, int dimFrom = int.MaxValue)
    {
        var control = Row(label);
        int n = stops.Count;
        if (n < 2) return false;
        index = Math.Clamp(index, 0, n - 1);

        const float valueWidth = 34f;
        float left = control.X + 8f, right = control.X + control.W - valueWidth - 8f;
        float cy = control.Y + control.H / 2f;

        Behavior(Id(label), control, out bool hover, out bool held);
        bool changed = false;
        if (held)
        {
            int target = (int)MathF.Round(Math.Clamp((Mouse.X - left) / (right - left), 0f, 1f) * (n - 1));
            if (target != index)
            {
                index = target;
                changed = true;
            }
        }

        float StopX(int i) => left + (right - left) * i / (n - 1);
        C.Rect(left, cy - 1.5f, right - left, 3f, ButtonHover);
        C.Rect(left, cy - 1.5f, StopX(index) - left, 3f, AccentHover);
        for (int i = 0; i < n; i++)
            C.Rect(StopX(i) - 1.5f, cy - 5f, 3f, 10f, i >= dimFrom ? DimColor : i <= index ? AccentHover : ButtonDown);
        C.Rect(StopX(index) - 5f, cy - 8f, 10f, 16f, held || hover ? AccentHover : TextColor);

        string value = stops[index];
        C.Text(value, control.X + control.W - C.MeasureText(value) - 4f, TextTop(control.Y, control.H), TextColor);
        return changed;
    }

    // A dropdown. Returns true when an option is picked, with its index in picked.
    public bool Combo(string label, int current, IReadOnlyList<string> options, out int picked)
    {
        picked = current;
        var control = Row(label);
        string id = Id(label);
        string popup = "combo:" + id;
        if (Behavior(id, control, out bool hover, out _))
        {
            if (IsPopupOpen(popup)) _openPopup = null;
            else
            {
                // The list is as wide as its longest choice (at least as wide as the field), kept on screen.
                float width = MathF.Max(control.W, options.Count == 0 ? 0f : options.Max(o => C.MeasureText(o)) + 28f);
                width = MathF.Min(width, Size.X - 8f);
                float x = MathF.Max(4f, MathF.Min(control.X, Size.X - width - 4f));
                OpenPopup(popup, new float2(x, control.Y + control.H), width);
            }
        }
        C.Rect(control.X, control.Y, control.W, control.H, hover ? ButtonHover : ButtonColor);
        string shown = current >= 0 && current < options.Count ? options[current] : "";
        C.Text(Fit(shown, control.W - 26f), control.X + 6f, TextTop(control.Y, control.H), TextColor);
        C.Text("v", control.X + control.W - 14f, TextTop(control.Y, control.H), DimColor);

        bool chose = false;
        if (BeginPopup(popup))
        {
            for (int i = 0; i < options.Count; i++)
                if (Selectable($"{options[i]}##{i}", i == current))
                {
                    picked = i;
                    chose = true;
                    CloseCurrentPopup();
                }
            EndPopup();
        }
        return chose && picked != current;
    }

    public bool Combo<T>(string label, ref T value) where T : struct, Enum
    {
        var values = Enum.GetValues<T>();
        int current = Array.IndexOf(values, value);
        if (!Combo(label, current, values.Select(v => v.ToString()).ToList(), out int picked)) return false;
        value = values[picked];
        return true;
    }

    // ---- drawing helpers ----------------------------------------------------------------------------

    // Text that follows the mouse above everything, for drag and drop.
    public void DragLabel(string text)
    {
        int layer = C.Layer;
        var clip = C.Clip;
        C.Layer = Canvas2D.TopLayer;
        C.Clip = null;
        float w = C.MeasureText(text) + 12f;
        C.Rect(Mouse.X + 12f, Mouse.Y + 8f, w, RowHeight, PopupColor);
        C.Text(text, Mouse.X + 18f, TextTop(Mouse.Y + 8f, RowHeight), TextColor);
        C.Layer = layer;
        C.Clip = clip;
    }

    public static float4 Hex(int rgb, float alpha = 1f) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);

    private static string Format(float value, bool integer)
    {
        if (integer) return ((int)MathF.Round(value)).ToString(CultureInfo.InvariantCulture);
        string text = value.ToString("0.###", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;   // tiny negatives (like rotations of -0.0001) read as zero
    }
}
