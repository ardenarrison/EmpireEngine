using Brutal.ImGuiApi;
using Brutal.Numerics;
using Editor.Ui;

namespace Editor.Terminal;

// Window > Terminal (Ctrl+`): a terminal along the bottom of the editor, running the user's shell in the project's folder. It is a
// real terminal (a pseudo-terminal with a screen emulator), so commands, colors, editors, pagers and full-screen tools such as
// Claude Code all work; run `claude` in it and sign in there as you would anywhere.
//
// Nothing runs until the user opens it, and it only ever runs what they type. Click inside it to type: while it has the keyboard the
// editor's own shortcuts stand down, and Escape goes to the program, not the editor. Click outside it to give the keyboard back.
public sealed class TerminalWindow(string workingDirectory)
{
    private const int DefaultCols = 100, DefaultRows = 24;

    private readonly TerminalEmulator _screen = new(DefaultCols, DefaultRows);
    private PtySession? _session;
    private bool _open;
    private bool _focused;
    private string _error = "";
    private int _cols = DefaultCols, _rows = DefaultRows;
    private double _resizeAt;
    private float _wheelRemainder;                   // part of a line the wheel or trackpad has scrolled but not yet applied
    private int _scrollBack;                          // lines the view is scrolled back from the live screen
    private (int Row, int Col)? _selFrom, _selTo;     // selection, in lines relative to the live screen (negative is scrollback)
    private bool _dragging;
    private double _now;
    private bool _wired;
    private Box _windowRect;

    public bool IsOpen => _open;
    public bool IsFocused => _open && _focused;

    public void Toggle() { if (_open) Close(); else Open(); }

    public void Open()
    {
        _open = true;
        _focused = true;
        EnsureSession();
    }

    public void Close()
    {
        _open = false;
        _focused = false;
    }

    // Stops the shell and anything running in it (when the editor closes).
    public void Shutdown()
    {
        _session?.Dispose();
        _session = null;
    }

    private void EnsureSession()
    {
        if (_session is { HasExited: false }) return;
        _session?.Dispose();
        _session = null;
        _error = "";
        try
        {
            _session = PtySession.Start(workingDirectory, _cols, _rows);
            _screen.Resize(_cols, _rows);
            _screen.Write("\u001bc"u8);   // a fresh screen
            if (!_wired)
            {
                _screen.Respond += text => _session?.Write(text);
                _wired = true;
            }
        }
        catch (PtyException ex)
        {
            _error = ex.Message;
        }
        _scrollBack = 0;
        _selFrom = _selTo = null;
    }

    // Once a frame (even while the window is hidden): moves what the shell printed onto the screen.
    public void Update()
    {
        if (_session is null) return;
        int before = _screen.ScrollbackCount;
        var bytes = _session.TakeOutput();
        if (bytes.Length == 0) return;
        _screen.Write(bytes);
        if (_scrollBack > 0)   // keep the view where it is while new lines arrive below
            _scrollBack = Math.Min(_screen.ScrollbackCount, _scrollBack + (_screen.ScrollbackCount - before));
    }

    // ---- drawing ------------------------------------------------------------------------------------------------------------

    public void Draw(Gui g, double now)
    {
        _now = now;
        if (!_open) { g.ExternalTyping = false; return; }

        float h = Math.Clamp(g.Size.Y * 0.42f, 220f, 560f);
        var rect = new Box(0f, g.Size.Y - h, g.Size.X, h);
        _windowRect = rect;
        if (!g.BeginWindow("Terminal", rect, "Terminal", WindowFlags.Closable | WindowFlags.NoScroll))
            Close();

        if (g.Button("Interrupt", enabled: _session is { HasExited: false })) _session?.Write("\u0003"u8);   // the same as Ctrl+C
        g.SameLine();
        if (g.Button("Restart")) { _session?.Dispose(); _session = null; EnsureSession(); }
        g.SameLine();
        if (g.Button("Clear")) { _screen.Write("\u001b[3J"u8); _scrollBack = 0; _session?.Write("\u000c"u8); }
        g.SameLine();
        if (g.Button("Copy", enabled: HasSelection)) CopySelection();
        g.SameLine();
        if (g.Button("Paste", enabled: _session is { HasExited: false })) Paste();
        g.SameLine();
        g.TextDisabled(_focused ? "Typing here. Click outside to give the keyboard back to the editor." : "Click inside to type.");

        var area = g.Reserve(g.AvailWidth, rect.H - 70f);
        Resize(g, area);
        DrawScreen(g, area);
        HandleMouse(g, area);

        if (_error.Length > 0)
            g.C.Text(_error, area.X + 6f, area.Y + 6f, Gui.ErrorColor);

        g.EndWindow();
        if (_open) HandleKeys(g);
        g.ExternalTyping = IsFocused;
    }

    // The terminal's own colors, which follow the editor's theme: a dark screen on dark, a white one on light.
    private static int DefaultBackground => Theme.PickRgb(0x1E1E1E, 0xFFFFFF);
    private static int DefaultForeground => Theme.PickRgb(0xCCCCCC, 0x333333);
    private static int CursorColor => Theme.PickRgb(0xCCCCCC, 0x333333);
    private static int Notice => Theme.PickRgb(0xCCA700, 0x9A6700);

    private float CharW(Gui g) => Math.Max(1f, g.C.MeasureText("M"));
    private float LineH(Gui g) => g.C.FontSize + 4f;

    // The program is told the size of the area the window gives it, a moment after the window stops changing size.
    private void Resize(Gui g, Box area)
    {
        int cols = Math.Clamp((int)((area.W - 12f) / CharW(g)), 20, 500);
        int rows = Math.Clamp((int)((area.H - 8f) / LineH(g)), 5, 200);
        if (cols == _cols && rows == _rows) return;
        _cols = cols;
        _rows = rows;
        _resizeAt = _now + 0.15;
        _screen.Resize(cols, rows);
        _scrollBack = Math.Min(_scrollBack, _screen.ScrollbackCount);
    }

    private double _lastSent;
    private int _sentCols, _sentRows;

    private void DrawScreen(Gui g, Box area)
    {
        var c = g.C;
        if (_session is not null && _now >= _resizeAt && (_sentCols != _cols || _sentRows != _rows) && _now - _lastSent > 0.1)
        {
            _session.Resize(_cols, _rows);
            _sentCols = _cols; _sentRows = _rows; _lastSent = _now;
        }

        c.Rect(area.X, area.Y, area.W, area.H, Color(DefaultBackground));
        float cw = CharW(g), lh = LineH(g);
        float ox = area.X + 6f, oy = area.Y + 4f;
        int rows = Math.Min(_rows, _screen.Rows), cols = Math.Min(_cols, _screen.Cols);

        var sel = Selection();
        var text = new System.Text.StringBuilder();
        for (int r = 0; r < rows; r++)
        {
            float y = oy + r * lh;
            int absoluteRow = r - _scrollBack;

            // backgrounds (and the selection) first
            for (int col = 0; col < cols;)
            {
                var cell = _screen.GetCell(r, col, _scrollBack);
                var (fg, bg) = Colors(cell);
                bool selected = sel is { } s && InSelection(s, absoluteRow, col);
                int end = col + 1;
                while (end < cols)
                {
                    var next = _screen.GetCell(r, end, _scrollBack);
                    bool nextSelected = sel is { } s2 && InSelection(s2, absoluteRow, end);
                    if (Colors(next).Bg != bg || nextSelected != selected) break;
                    end++;
                }
                if (selected) c.Rect(ox + col * cw, y, (end - col) * cw, lh, Color(Theme.PickRgb(0x264F78, 0xADD6FF)));
                else if (bg != Colors(Cell.Blank()).Bg) c.Rect(ox + col * cw, y, (end - col) * cw, lh, Color(bg));
                col = end;
            }

            // then the characters, in runs of one color
            for (int col = 0; col < cols;)
            {
                var cell = _screen.GetCell(r, col, _scrollBack);
                if (TerminalGlyphs.BoxEdges(cell.Ch) != TerminalGlyphs.Edge.None || TerminalGlyphs.BlockRect(cell.Ch, out _, out _, out _, out _, out _))
                {
                    DrawShape(c, cell, ox + col * cw, y, cw, lh);
                    col++;
                    continue;
                }
                var (fg, _) = Colors(cell);
                text.Clear();
                int end = col;
                while (end < cols)
                {
                    var next = _screen.GetCell(r, end, _scrollBack);
                    if (TerminalGlyphs.BoxEdges(next.Ch) != TerminalGlyphs.Edge.None || TerminalGlyphs.BlockRect(next.Ch, out _, out _, out _, out _, out _)) break;
                    if (Colors(next).Fg != fg || next.Attr != cell.Attr) break;
                    text.Append((next.Attr & Cell.Hidden) != 0 ? ' ' : TerminalGlyphs.ToAscii(next.Ch));
                    end++;
                }
                string run = text.ToString();
                if (run.Trim().Length > 0) c.Text(run, ox + col * cw, y + 1f, Color(fg, (cell.Attr & Cell.Dim) != 0 ? 0.6f : 1f));
                if ((cell.Attr & Cell.Underline) != 0) c.Rect(ox + col * cw, y + lh - 3f, (end - col) * cw, 1f, Color(fg));
                if ((cell.Attr & Cell.Strike) != 0) c.Rect(ox + col * cw, y + lh * 0.5f, (end - col) * cw, 1f, Color(fg));
                col = end;
            }
        }

        // the cursor
        if (_scrollBack == 0 && _screen.CursorVisible && _session is { HasExited: false })
        {
            float cx = ox + _screen.CursorX * cw, cy = oy + _screen.CursorY * lh;
            if (_focused)
            {
                if ((_now % 1.0) < 0.6)
                {
                    c.Rect(cx, cy, cw, lh, Color(CursorColor, 0.85f));
                    var under = _screen.GetCell(_screen.CursorY, _screen.CursorX);
                    if (under.Ch > ' ') c.Text(TerminalGlyphs.ToAscii(under.Ch).ToString(), cx, cy + 1f, Color(DefaultBackground));
                }
            }
            else
            {
                c.Outline(cx, cy, cw, lh, 1f, Color(CursorColor, 0.7f));
            }
        }

        if (_scrollBack > 0) c.Text($"scrolled back {_scrollBack} lines - type or scroll down to return", area.X + area.W - 360f, area.Y + 2f, Color(Notice));
        if (_session is { HasExited: true })
        {
            string message = $"[The shell exited{(_session.ExitCode is { } code ? $" (code {code})" : "")}. Press Enter to start a new one.]";
            c.Text(message, ox, oy + (rows - 1) * lh, Color(Notice));
        }
    }

    // Box-drawing and block characters, drawn as rectangles so they join up from cell to cell.
    private static void DrawShape(Canvas2D c, Cell cell, float x, float y, float w, float h)
    {
        var (fg, _) = Colors(cell);
        var color = Color(fg, (cell.Attr & Cell.Dim) != 0 ? 0.6f : 1f);
        if (TerminalGlyphs.BlockRect(cell.Ch, out int l, out int t, out int r, out int b, out float alpha))
        {
            c.Rect(x + w * l / 8f, y + h * t / 8f, w * (r - l) / 8f, h * (b - t) / 8f, new float4(color.X, color.Y, color.Z, color.W * alpha));
            return;
        }
        var edges = TerminalGlyphs.BoxEdges(cell.Ch);
        float midX = MathF.Floor(x + w / 2f), midY = MathF.Floor(y + h / 2f);
        if (edges.HasFlag(TerminalGlyphs.Edge.Left)) c.Rect(x, midY, midX - x + 1f, 1f, color);
        if (edges.HasFlag(TerminalGlyphs.Edge.Right)) c.Rect(midX, midY, x + w - midX, 1f, color);
        if (edges.HasFlag(TerminalGlyphs.Edge.Up)) c.Rect(midX, y, 1f, midY - y + 1f, color);
        if (edges.HasFlag(TerminalGlyphs.Edge.Down)) c.Rect(midX, midY, 1f, y + h - midY, color);
    }

    // Foreground and background as 0xRRGGBB, with inverse applied. -1 for the default colors resolves to the terminal's own.
    private static (int Fg, int Bg) Colors(Cell cell)
    {
        int fg = Resolve(cell.Fg, DefaultForeground), bg = Resolve(cell.Bg, DefaultBackground);
        if ((cell.Attr & Cell.Bold) != 0 && cell.Fg is >= 0 and < 8) fg = Resolve(cell.Fg + 8, DefaultForeground);
        if ((cell.Attr & Cell.Inverse) != 0) (fg, bg) = (bg, fg);
        return (fg, bg);
    }

    private static int Resolve(int color, int fallback) =>
        color == -1 ? fallback : (color & Cell.Rgb) != 0 ? color & 0xFFFFFF : TerminalGlyphs.Palette(color, Theme.Light);

    private static float4 Color(int rgb, float alpha = 1f) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);

    // ---- the mouse ----------------------------------------------------------------------------------------------------------

    private void HandleMouse(Gui g, Box area)
    {
        bool over = g.Hovered(area);
        if (g.MousePressed)
            _focused = _windowRect.Contains(g.Mouse);   // a click anywhere in the terminal window (its buttons too) keeps the keyboard; a click elsewhere in the editor takes it away

        // A trackpad sends many tiny steps (a fraction of a line each), so the leftover is kept until it adds up to whole lines.
        if (over && g.Wheel != 0)
        {
            _wheelRemainder += g.Wheel * 3f;
            int lines = (int)_wheelRemainder;
            _wheelRemainder -= lines;
            _scrollBack = Math.Clamp(_scrollBack + lines, 0, _screen.ScrollbackCount);
        }
        else if (!over)
            _wheelRemainder = 0f;

        float cw = CharW(g), lh = LineH(g);
        (int Row, int Col) CellAt() => (
            Math.Clamp((int)((g.Mouse.Y - (area.Y + 4f)) / lh), 0, _rows - 1) - _scrollBack,
            Math.Clamp((int)((g.Mouse.X - (area.X + 6f)) / cw), 0, _cols - 1));

        if (g.MousePressed && over)
        {
            _selFrom = _selTo = CellAt();
            _dragging = true;
        }
        else if (_dragging && g.MouseDown)
        {
            _selTo = CellAt();
        }
        else if (_dragging)
        {
            _dragging = false;
            if (_selFrom == _selTo) _selFrom = _selTo = null;   // a plain click selects nothing
        }
    }

    private bool HasSelection => _selFrom is not null && _selTo is not null && _selFrom != _selTo;

    private ((int Row, int Col) From, (int Row, int Col) To)? Selection()
    {
        if (!HasSelection) return null;
        var a = _selFrom!.Value; var b = _selTo!.Value;
        return a.Row < b.Row || (a.Row == b.Row && a.Col <= b.Col) ? (a, b) : (b, a);
    }

    private static bool InSelection(((int Row, int Col) From, (int Row, int Col) To) s, int row, int col) =>
        (row > s.From.Row || (row == s.From.Row && col >= s.From.Col)) && (row < s.To.Row || (row == s.To.Row && col <= s.To.Col));

    private void CopySelection()
    {
        if (Selection() is not { } s) return;
        ImGui.SetClipboardText(_screen.GetText(s.From.Row, s.From.Col, s.To.Row, s.To.Col).ToU8());
    }

    // ---- the keyboard -------------------------------------------------------------------------------------------------------

    private void HandleKeys(Gui g)
    {
        if (!_focused || _session is null) return;
        var io = ImGui.GetIO();

        if (_session.HasExited)
        {
            if (g.EnterPressed) EnsureSession();
            return;
        }

        bool ctrl = io.KeyCtrl, shift = io.KeyShift, super = io.KeySuper, alt = io.KeyAlt;
        bool mac = OperatingSystem.IsMacOS();

        // copy and paste: Cmd+C / Cmd+V on a Mac, Ctrl+Shift+C / Ctrl+Shift+V elsewhere (and Shift+Insert)
        bool copyKey = mac ? super && Pressed(ImGuiKey.C) : ctrl && shift && Pressed(ImGuiKey.C);
        bool pasteKey = (mac ? super && Pressed(ImGuiKey.V) : ctrl && shift && Pressed(ImGuiKey.V)) || (shift && Pressed(ImGuiKey.Insert));
        if (copyKey) { CopySelection(); return; }
        if (pasteKey) { Paste(); return; }
        if (super) return;   // other Cmd shortcuts belong to the editor and the system

        // page up and down scroll the history when Shift is held
        if (shift && Pressed(ImGuiKey.PageUp)) { _scrollBack = Math.Min(_screen.ScrollbackCount, _scrollBack + Math.Max(1, _rows - 1)); return; }
        if (shift && Pressed(ImGuiKey.PageDown)) { _scrollBack = Math.Max(0, _scrollBack - Math.Max(1, _rows - 1)); return; }

        var send = new System.Text.StringBuilder();
        bool app = _screen.ApplicationCursorKeys;
        string prefix = alt ? "\u001b" : "";   // Alt (Option) sends Escape before the key, which is how terminal programs read it

        // letters with Ctrl are control characters: Ctrl+C is 3 (interrupt), Ctrl+D is 4 (end of input), and so on
        if (ctrl && !alt)
        {
            for (ImGuiKey k = ImGuiKey.A; k <= ImGuiKey.Z; k++)
                if (Pressed(k))
                {
                    if (!mac && k == ImGuiKey.C && HasSelection) { CopySelection(); _selFrom = _selTo = null; return; }   // Ctrl+C copies when something is selected
                    send.Append((char)(k - ImGuiKey.A + 1));
                }
            if (Pressed(ImGuiKey.LeftBracket)) send.Append('\u001b');
            if (Pressed(ImGuiKey.Backslash)) send.Append('\u001c');
            if (Pressed(ImGuiKey.RightBracket)) send.Append('\u001d');
            if (Pressed(ImGuiKey.Space)) send.Append('\0');
        }
        else
        {
            foreach (char ch in g.TypedAll) send.Append(prefix).Append(ch);
        }

        if (Pressed(ImGuiKey.Enter) || Pressed(ImGuiKey.KeypadEnter)) send.Append(prefix).Append('\r');
        if (Pressed(ImGuiKey.Backspace)) send.Append(prefix).Append(ctrl ? '\u0008' : '\u007f');
        if (Pressed(ImGuiKey.Tab)) send.Append(shift ? "\u001b[Z" : prefix + "\t");
        if (Pressed(ImGuiKey.Escape)) send.Append('\u001b');
        if (Pressed(ImGuiKey.UpArrow)) send.Append(Arrow('A', app, ctrl, shift, alt));
        if (Pressed(ImGuiKey.DownArrow)) send.Append(Arrow('B', app, ctrl, shift, alt));
        if (Pressed(ImGuiKey.RightArrow)) send.Append(Arrow('C', app, ctrl, shift, alt));
        if (Pressed(ImGuiKey.LeftArrow)) send.Append(Arrow('D', app, ctrl, shift, alt));
        if (Pressed(ImGuiKey.Home)) send.Append(app ? "\u001bOH" : "\u001b[H");
        if (Pressed(ImGuiKey.End)) send.Append(app ? "\u001bOF" : "\u001b[F");
        if (!shift && Pressed(ImGuiKey.PageUp)) send.Append("\u001b[5~");
        if (!shift && Pressed(ImGuiKey.PageDown)) send.Append("\u001b[6~");
        if (!shift && Pressed(ImGuiKey.Insert)) send.Append("\u001b[2~");
        if (Pressed(ImGuiKey.Delete)) send.Append("\u001b[3~");
        string[] functionKeys = ["\u001bOP", "\u001bOQ", "\u001bOR", "\u001bOS", "\u001b[15~", "\u001b[17~", "\u001b[18~", "\u001b[19~", "\u001b[20~", "\u001b[21~", "\u001b[23~", "\u001b[24~"];
        for (int i = 0; i < 12; i++)
            if (Pressed(ImGuiKey.F1 + i)) send.Append(functionKeys[i]);

        if (send.Length == 0) return;
        _scrollBack = 0;
        _selFrom = _selTo = null;
        _session.Write(send.ToString());
    }

    private static bool Pressed(ImGuiKey key) => ImGui.IsKeyPressed(key, true);

    // Arrow keys: ESC [ A normally, ESC O A in application mode, and with modifiers ESC [ 1 ; n A (n is 2 for Shift, 3 Alt, 5 Ctrl).
    private static string Arrow(char letter, bool application, bool ctrl, bool shift, bool alt)
    {
        int mod = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
        if (mod > 1) return $"\u001b[1;{mod}{letter}";
        return application ? $"\u001bO{letter}" : $"\u001b[{letter}";
    }

    // Pastes the clipboard. Escape characters are removed first, so text from a web page can't smuggle commands in as terminal
    // controls, and the program is told it is a paste when it asked to be (bracketed paste).
    private void Paste()
    {
        if (_session is not { HasExited: false }) return;
        var clip = ImGui.GetClipboardText();
        if (clip.IsNull()) return;
        string text = System.Text.Encoding.UTF8.GetString(clip.AsSpan()).Replace("\r\n", "\r").Replace('\n', '\r');
        text = new string(text.Where(ch => ch == '\r' || ch == '\t' || ch >= 32 && ch != 127).ToArray());
        if (text.Length == 0) return;
        _scrollBack = 0;
        _session.Write(_screen.BracketedPaste ? "\u001b[200~" + text + "\u001b[201~" : text);
    }
}
