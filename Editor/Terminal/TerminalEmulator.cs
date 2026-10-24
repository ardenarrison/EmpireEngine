using System.Text;

namespace Editor.Terminal;

// One character cell. Fg and Bg: -1 is the terminal's default color, 0 to 255 a palette color, and 0x1000000 | 0xRRGGBB a true color.
public struct Cell
{
    public char Ch;
    public int Fg;
    public int Bg;
    public byte Attr;

    public const byte Bold = 1, Dim = 2, Italic = 4, Underline = 8, Inverse = 16, Hidden = 32, Strike = 64;
    public const int Rgb = 0x1000000;
    public static Cell Blank(int bg = -1) => new() { Ch = ' ', Fg = -1, Bg = bg };
}

// A terminal screen: it reads the bytes a program writes (text plus the escape sequences terminals understand) and keeps what the
// screen would show, with scrollback. It knows nothing about windows or processes, so it can be tested on its own.
//
// It understands what everyday command-line programs and full-screen ones (editors, pagers, and tools such as Claude Code)
// send: colors (16, 256 and true color), cursor movement, erasing, scrolling regions, inserting and deleting, the alternate
// screen, and the questions programs ask a terminal (where is the cursor, what are you), which it answers through Respond.
// Things it doesn't need (mouse reporting, sixel pictures, window titles beyond storing them) are read and ignored.
public sealed class TerminalEmulator
{
    private enum State { Ground, Escape, EscapeIntermediate, Csi, Osc, StringIgnored, OscEscape, StringEscape }

    private readonly int _maxScrollback;
    private readonly List<Cell[]> _scrollback = new();
    private Cell[][] _screen;
    private Cell[][]? _saved;                // the main screen while the alternate one is shown
    private int _top, _bottom;               // the scrolling region, inclusive rows
    private int _savedX, _savedY;            // cursor saved by ESC 7 and CSI s
    private int _altSavedX, _altSavedY;      // cursor saved when the alternate screen was entered
    private bool _wrapPending;
    private bool _autoWrap = true;
    private bool[] _tabs;

    // current drawing style
    private int _fg = -1, _bg = -1;
    private byte _attr;

    // parser
    private State _state = State.Ground;
    private readonly StringBuilder _params = new();
    private readonly StringBuilder _intermediates = new();
    private readonly StringBuilder _osc = new();
    private int _utf8Need, _utf8Code;

    public TerminalEmulator(int cols, int rows, int scrollback = 2000)
    {
        Cols = Math.Max(2, cols);
        Rows = Math.Max(1, rows);
        _maxScrollback = scrollback;
        _screen = NewScreen();
        _top = 0;
        _bottom = Rows - 1;
        _tabs = NewTabs();
    }

    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public bool AlternateScreen => _saved is not null;
    public bool ApplicationCursorKeys { get; private set; }     // arrow keys send ESC O A instead of ESC [ A
    public bool BracketedPaste { get; private set; }
    public string Title { get; private set; } = "";
    public int ScrollbackCount => _scrollback.Count;

    // Text the program is waiting for as an answer (to "where is the cursor?" and similar). Send it to the program.
    public event Action<string>? Respond;

    // ---- reading cells --------------------------------------------------------------------------------------------

    // The cell at screen row `row` (0 is the top) when the view is scrolled back by `scrollOffset` lines (0 is live).
    public Cell GetCell(int row, int col, int scrollOffset = 0)
    {
        var line = LineAt(row - scrollOffset);
        return col >= 0 && col < line.Length ? line[col] : Cell.Blank();
    }

    // Row index r < 0 is in the scrollback (-1 is the newest scrollback line).
    private Cell[] LineAt(int r)
    {
        if (r >= 0 && r < Rows) return _screen[r];
        int back = -r - 1;
        if (r < 0 && back < _scrollback.Count) return _scrollback[_scrollback.Count - 1 - back];
        return new Cell[Cols];
    }

    // The text between two points (row, col), for copying. Rows may be negative (scrollback). Trailing spaces are trimmed.
    public string GetText(int fromRow, int fromCol, int toRow, int toCol)
    {
        if (toRow < fromRow || (toRow == fromRow && toCol < fromCol)) (fromRow, fromCol, toRow, toCol) = (toRow, toCol, fromRow, fromCol);
        var sb = new StringBuilder();
        for (int r = fromRow; r <= toRow; r++)
        {
            var line = LineAt(r);
            int start = r == fromRow ? Math.Max(0, fromCol) : 0;
            int end = r == toRow ? Math.Min(Cols - 1, toCol) : Cols - 1;
            var row = new StringBuilder();
            for (int c = start; c <= end && c < line.Length; c++) row.Append(line[c].Ch == '\0' ? ' ' : line[c].Ch);
            sb.Append(row.ToString().TrimEnd());
            if (r < toRow) sb.Append('\n');
        }
        return sb.ToString();
    }

    public string RowText(int row, int scrollOffset = 0)
    {
        var line = LineAt(row - scrollOffset);
        return new string(line.Select(c => c.Ch == '\0' ? ' ' : c.Ch).ToArray()).TrimEnd();
    }

    // ---- resizing -------------------------------------------------------------------------------------------------

    public void Resize(int cols, int rows)
    {
        cols = Math.Max(2, cols);
        rows = Math.Max(1, rows);
        if (cols == Cols && rows == Rows) return;

        // Keep the lines around the cursor on screen: extra lines go to the scrollback, new ones come back from it.
        if (!AlternateScreen)
        {
            while (rows < Rows && CursorY > rows - 1)
            {
                PushScrollback(_screen[0]);
                _screen = _screen.Skip(1).ToArray();
                Rows--;
                CursorY--;
            }
        }
        var resized = new Cell[rows][];
        int copy = Math.Min(rows, Rows);
        for (int r = 0; r < rows; r++)
        {
            resized[r] = BlankLine(cols);
            if (r < copy) Array.Copy(_screen[r], resized[r], Math.Min(cols, Cols));
        }
        _screen = resized;
        if (_saved is not null)
        {
            var savedResized = new Cell[rows][];
            for (int r = 0; r < rows; r++)
            {
                savedResized[r] = BlankLine(cols);
                if (r < _saved.Length) Array.Copy(_saved[r], savedResized[r], Math.Min(cols, _saved[r].Length));
            }
            _saved = savedResized;
        }
        Cols = cols;
        Rows = rows;
        _top = 0;
        _bottom = rows - 1;
        CursorX = Math.Min(CursorX, cols - 1);
        CursorY = Math.Min(CursorY, rows - 1);
        _wrapPending = false;
        _tabs = NewTabs();
    }

    // ---- input ------------------------------------------------------------------------------------------------------

    public void Write(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) Feed(b);
    }

    public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));

    private void Feed(byte b)
    {
        // UTF-8 continuation bytes in the middle of text
        if (_state == State.Ground && _utf8Need > 0)
        {
            if ((b & 0xC0) == 0x80)
            {
                _utf8Code = (_utf8Code << 6) | (b & 0x3F);
                if (--_utf8Need == 0) Print(_utf8Code);
                return;
            }
            _utf8Need = 0;   // a broken sequence: show the replacement character and handle this byte normally
            Print(0xFFFD);
        }

        // Control characters work in every state except inside a string.
        if (b < 0x20 && _state is not (State.Osc or State.StringIgnored or State.OscEscape or State.StringEscape))
        {
            Control(b);
            return;
        }
        if (b == 0x18 || b == 0x1A) { _state = State.Ground; return; }   // CAN and SUB end any sequence

        switch (_state)
        {
            case State.Ground:
                if (b < 0x80) Print(b);
                else if (b >= 0xC2 && b <= 0xDF) { _utf8Need = 1; _utf8Code = b & 0x1F; }
                else if (b >= 0xE0 && b <= 0xEF) { _utf8Need = 2; _utf8Code = b & 0x0F; }
                else if (b >= 0xF0 && b <= 0xF4) { _utf8Need = 3; _utf8Code = b & 0x07; }
                else Print(0xFFFD);
                break;

            case State.Escape:
                if (b == '[') { _state = State.Csi; _params.Clear(); _intermediates.Clear(); }
                else if (b == ']') { _state = State.Osc; _osc.Clear(); }
                else if (b is (byte)'P' or (byte)'X' or (byte)'^' or (byte)'_') _state = State.StringIgnored;   // DCS, SOS, PM, APC
                else if (b >= 0x20 && b <= 0x2F) { _state = State.EscapeIntermediate; _intermediates.Clear(); _intermediates.Append((char)b); }
                else { EscapeFinal((char)b); _state = State.Ground; }
                break;

            case State.EscapeIntermediate:   // ESC ( B and the like: character set choices, which this terminal ignores
                if (b >= 0x20 && b <= 0x2F) _intermediates.Append((char)b);
                else
                {
                    if (_intermediates.ToString() == "#" && b == '8') FillWithE();   // DECALN
                    _state = State.Ground;
                }
                break;

            case State.Csi:
                if (b >= 0x30 && b <= 0x3F) _params.Append((char)b);
                else if (b >= 0x20 && b <= 0x2F) _intermediates.Append((char)b);
                else if (b >= 0x40 && b <= 0x7E) { CsiFinal((char)b); _state = State.Ground; }
                else _state = State.Ground;
                break;

            case State.Osc:
                if (b == 0x07) { EndOsc(); _state = State.Ground; }
                else if (b == 0x1B) _state = State.OscEscape;
                else if (_osc.Length < 2048) _osc.Append((char)b);
                break;

            case State.OscEscape:
                if (b == '\\') EndOsc();
                _state = State.Ground;
                if (b != '\\') Feed(b);
                break;

            case State.StringIgnored:
                if (b == 0x07) _state = State.Ground;
                else if (b == 0x1B) _state = State.StringEscape;
                break;

            case State.StringEscape:
                _state = b == '\\' ? State.Ground : State.StringIgnored;
                break;
        }
    }

    private void Control(byte b)
    {
        if (_state is State.Escape or State.Csi or State.EscapeIntermediate)
        {
            if (b == 0x1B) { _state = State.Escape; return; }
        }
        switch (b)
        {
            case 0x1B: _state = State.Escape; break;
            case 0x08: if (CursorX > 0) CursorX--; _wrapPending = false; break;           // backspace
            case 0x09: Tab(1); break;
            case 0x0A or 0x0B or 0x0C: LineFeed(); break;
            case 0x0D: CursorX = 0; _wrapPending = false; break;
        }
    }

    private void EndOsc()
    {
        string text = _osc.ToString();
        int semi = text.IndexOf(';');
        if (semi > 0 && (text[..semi] is "0" or "2")) Title = SafeTitle(text[(semi + 1)..]);
    }

    private static string SafeTitle(string title) => new string(title.Where(c => c >= 32 && c < 127).Take(120).ToArray());

    // ---- printing ----------------------------------------------------------------------------------------------------

    private void Print(int code)
    {
        char ch = code > 0xFFFF ? '�' : (char)code;
        if (_wrapPending && _autoWrap)
        {
            CursorX = 0;
            LineFeed();
        }
        _wrapPending = false;
        _screen[CursorY][CursorX] = new Cell { Ch = ch, Fg = _fg, Bg = _bg, Attr = _attr };
        if (CursorX < Cols - 1) CursorX++;
        else _wrapPending = true;
    }

    private void LineFeed()
    {
        _wrapPending = false;
        if (CursorY == _bottom) ScrollUp(1);
        else if (CursorY < Rows - 1) CursorY++;
    }

    private void ReverseIndex()
    {
        _wrapPending = false;
        if (CursorY == _top) ScrollDown(1);
        else if (CursorY > 0) CursorY--;
    }

    private void Tab(int count)
    {
        for (int i = 0; i < count; i++)
        {
            int x = CursorX + 1;
            while (x < Cols - 1 && !_tabs[x]) x++;
            CursorX = Math.Min(x, Cols - 1);
        }
        _wrapPending = false;
    }

    // Scrolls the lines of the region up; the top one goes to the scrollback when the region is the whole main screen.
    private void ScrollUp(int n)
    {
        for (int i = 0; i < n; i++)
        {
            if (_top == 0 && !AlternateScreen) PushScrollback(_screen[0]);
            for (int r = _top; r < _bottom; r++) _screen[r] = _screen[r + 1];
            _screen[_bottom] = BlankLine(Cols, _bg);
        }
    }

    private void ScrollDown(int n)
    {
        for (int i = 0; i < n; i++)
        {
            for (int r = _bottom; r > _top; r--) _screen[r] = _screen[r - 1];
            _screen[_top] = BlankLine(Cols, _bg);
        }
    }

    private void PushScrollback(Cell[] line)
    {
        _scrollback.Add(line);
        if (_scrollback.Count > _maxScrollback) _scrollback.RemoveAt(0);
    }

    // ---- escape sequences ----------------------------------------------------------------------------------------------

    private void EscapeFinal(char c)
    {
        switch (c)
        {
            case '7': _savedX = CursorX; _savedY = CursorY; break;
            case '8': CursorX = Math.Min(_savedX, Cols - 1); CursorY = Math.Min(_savedY, Rows - 1); _wrapPending = false; break;
            case 'D': LineFeed(); break;
            case 'E': CursorX = 0; LineFeed(); break;
            case 'M': ReverseIndex(); break;
            case 'H': if (CursorX < _tabs.Length) _tabs[CursorX] = true; break;
            case 'c': FullReset(); break;
            case '=' or '>': break;   // keypad modes
        }
    }

    private int[] Numbers(int count = 0)
    {
        string p = _params.ToString().TrimStart('?', '>', '<', '=');
        var parts = p.Length == 0 ? new[] { "" } : p.Split(';');
        var result = new int[Math.Max(count, parts.Length)];
        for (int i = 0; i < result.Length; i++)
            result[i] = i < parts.Length && int.TryParse(parts[i].Split(':')[0], out int v) ? v : 0;
        return result;
    }

    private int Param(int[] p, int index, int fallback) => index < p.Length && p[index] > 0 ? p[index] : fallback;

    private void CsiFinal(char final)
    {
        string raw = _params.ToString();
        char prefix = raw.Length > 0 && raw[0] is '?' or '>' or '<' or '=' ? raw[0] : '\0';
        string inter = _intermediates.ToString();
        var p = Numbers();
        _wrapPending = final is 'm' or 'h' or 'l' or 'n' or 'c' or 'q' or 'u' or 'r' or 's' ? _wrapPending : false;

        if (inter.Length > 0 && !(final == 'p' || final == 'q')) return;   // sequences with intermediates we don't use

        switch (final)
        {
            case 'A': CursorY = Math.Max(_top <= CursorY ? _top : 0, CursorY - Param(p, 0, 1)); break;
            case 'B': CursorY = Math.Min(CursorY >= _top && CursorY <= _bottom ? _bottom : Rows - 1, CursorY + Param(p, 0, 1)); break;
            case 'C' or 'a': CursorX = Math.Min(Cols - 1, CursorX + Param(p, 0, 1)); break;
            case 'D': CursorX = Math.Max(0, CursorX - Param(p, 0, 1)); break;
            case 'E': CursorX = 0; CursorY = Math.Min(Rows - 1, CursorY + Param(p, 0, 1)); break;
            case 'F': CursorX = 0; CursorY = Math.Max(0, CursorY - Param(p, 0, 1)); break;
            case 'G' or '`': CursorX = Math.Clamp(Param(p, 0, 1) - 1, 0, Cols - 1); break;
            case 'H' or 'f':
                CursorY = Math.Clamp(Param(p, 0, 1) - 1, 0, Rows - 1);
                CursorX = Math.Clamp(Param(p, 1, 1) - 1, 0, Cols - 1);
                break;
            case 'd': CursorY = Math.Clamp(Param(p, 0, 1) - 1, 0, Rows - 1); break;
            case 'I': Tab(Param(p, 0, 1)); break;
            case 'Z':
                for (int i = 0; i < Param(p, 0, 1); i++)
                {
                    int x = CursorX - 1;
                    while (x > 0 && !_tabs[x]) x--;
                    CursorX = Math.Max(0, x);
                }
                break;
            case 'J': EraseDisplay(p[0]); break;
            case 'K': EraseLine(p[0]); break;
            case 'L': InsertLines(Param(p, 0, 1)); break;
            case 'M': DeleteLines(Param(p, 0, 1)); break;
            case '@': InsertChars(Param(p, 0, 1)); break;
            case 'P': DeleteChars(Param(p, 0, 1)); break;
            case 'X':
                for (int i = 0; i < Param(p, 0, 1) && CursorX + i < Cols; i++) _screen[CursorY][CursorX + i] = Cell.Blank(_bg);
                break;
            case 'S': ScrollUp(Param(p, 0, 1)); break;
            case 'T' when prefix == '\0': ScrollDown(Param(p, 0, 1)); break;
            case 'm' when prefix == '\0': Sgr(p); break;
            case 'r' when prefix == '\0':
                int top = Param(p, 0, 1) - 1, bottom = Param(p, 1, Rows) - 1;
                if (top >= 0 && bottom < Rows && top < bottom) { _top = top; _bottom = bottom; }
                else { _top = 0; _bottom = Rows - 1; }
                CursorX = 0; CursorY = 0;
                break;
            case 's' when prefix == '\0': _savedX = CursorX; _savedY = CursorY; break;
            case 'u' when prefix == '\0': CursorX = Math.Min(_savedX, Cols - 1); CursorY = Math.Min(_savedY, Rows - 1); break;
            case 'g': if (p[0] == 3) Array.Clear(_tabs); else if (p[0] == 0 && CursorX < _tabs.Length) _tabs[CursorX] = false; break;
            case 'h' or 'l': SetMode(prefix, p, final == 'h'); break;
            case 'n' when prefix == '\0':
                if (p[0] == 5) Respond?.Invoke("\u001b[0n");
                else if (p[0] == 6) Respond?.Invoke($"\u001b[{CursorY + 1};{CursorX + 1}R");
                break;
            case 'c' when prefix == '\0' && p[0] == 0: Respond?.Invoke("\u001b[?62;c"); break;
            case 'c' when prefix == '>': Respond?.Invoke("\u001b[>0;0;0c"); break;
            case 'q' when prefix == '>': Respond?.Invoke("\u001bP>|empire-terminal\u001b\\"); break;
        }
    }

    private void SetMode(char prefix, int[] p, bool on)
    {
        foreach (int mode in p)
        {
            if (prefix == '?')
            {
                switch (mode)
                {
                    case 1: ApplicationCursorKeys = on; break;
                    case 7: _autoWrap = on; break;
                    case 25: CursorVisible = on; break;
                    case 47 or 1047 or 1049: SwitchScreen(on, save: mode == 1049); break;
                    case 2004: BracketedPaste = on; break;
                }
            }
        }
    }

    private void SwitchScreen(bool alternate, bool save)
    {
        if (alternate && _saved is null)
        {
            if (save) { _altSavedX = CursorX; _altSavedY = CursorY; }
            _saved = _screen;
            _screen = NewScreen();
            _top = 0; _bottom = Rows - 1;
            CursorX = 0; CursorY = 0;
        }
        else if (!alternate && _saved is not null)
        {
            _screen = _saved;
            _saved = null;
            _top = 0; _bottom = Rows - 1;
            if (save) { CursorX = Math.Min(_altSavedX, Cols - 1); CursorY = Math.Min(_altSavedY, Rows - 1); }
        }
        _wrapPending = false;
    }

    private void Sgr(int[] p)
    {
        // "38;5;n" and "38;2;r;g;b" (also written with colons) take several numbers
        string[] raw = _params.ToString().Split(';');
        for (int i = 0; i < p.Length; i++)
        {
            int v = p[i];
            switch (v)
            {
                case 0: _fg = -1; _bg = -1; _attr = 0; break;
                case 1: _attr |= Cell.Bold; break;
                case 2: _attr |= Cell.Dim; break;
                case 3: _attr |= Cell.Italic; break;
                case 4: _attr |= Cell.Underline; break;
                case 7: _attr |= Cell.Inverse; break;
                case 8: _attr |= Cell.Hidden; break;
                case 9: _attr |= Cell.Strike; break;
                case 22: _attr &= unchecked((byte)~(Cell.Bold | Cell.Dim)); break;
                case 23: _attr &= unchecked((byte)~Cell.Italic); break;
                case 24: _attr &= unchecked((byte)~Cell.Underline); break;
                case 27: _attr &= unchecked((byte)~Cell.Inverse); break;
                case 28: _attr &= unchecked((byte)~Cell.Hidden); break;
                case 29: _attr &= unchecked((byte)~Cell.Strike); break;
                case >= 30 and <= 37: _fg = v - 30; break;
                case 39: _fg = -1; break;
                case >= 40 and <= 47: _bg = v - 40; break;
                case 49: _bg = -1; break;
                case >= 90 and <= 97: _fg = v - 90 + 8; break;
                case >= 100 and <= 107: _bg = v - 100 + 8; break;
                case 38 or 48:
                    int color = -1;
                    string[] colon = i < raw.Length ? raw[i].Split(':') : [];
                    if (colon.Length > 1)
                    {
                        // 38:5:n or 38:2::r:g:b (the colon form)
                        int[] nums = colon.Skip(1).Select(s => int.TryParse(s, out int n) ? n : 0).ToArray();
                        if (nums.Length >= 2 && nums[0] == 5) color = Math.Clamp(nums[1], 0, 255);
                        else if (nums.Length >= 4 && nums[0] == 2) color = Cell.Rgb | RgbOf(nums[^3], nums[^2], nums[^1]);
                    }
                    else if (i + 1 < p.Length && p[i + 1] == 5 && i + 2 < p.Length) { color = Math.Clamp(p[i + 2], 0, 255); i += 2; }
                    else if (i + 1 < p.Length && p[i + 1] == 2 && i + 4 < p.Length) { color = Cell.Rgb | RgbOf(p[i + 2], p[i + 3], p[i + 4]); i += 4; }
                    if (color != -1) { if (v == 38) _fg = color; else _bg = color; }
                    break;
            }
        }
    }

    private static int RgbOf(int r, int g, int b) => (Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(b, 0, 255);

    // ---- erasing and editing ----------------------------------------------------------------------------------------------

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (int r = CursorY + 1; r < Rows; r++) _screen[r] = BlankLine(Cols, _bg);
                break;
            case 1:
                for (int r = 0; r < CursorY; r++) _screen[r] = BlankLine(Cols, _bg);
                EraseLine(1);
                break;
            case 2:
                for (int r = 0; r < Rows; r++) _screen[r] = BlankLine(Cols, _bg);
                break;
            case 3: _scrollback.Clear(); break;
        }
    }

    private void EraseLine(int mode)
    {
        var line = _screen[CursorY];
        int from = mode == 0 ? CursorX : 0, to = mode == 1 ? CursorX : Cols - 1;
        if (mode == 2) { from = 0; to = Cols - 1; }
        for (int c = from; c <= to && c < Cols; c++) line[c] = Cell.Blank(_bg);
    }

    private void InsertLines(int n)
    {
        if (CursorY < _top || CursorY > _bottom) return;
        n = Math.Min(n, _bottom - CursorY + 1);
        for (int i = 0; i < n; i++)
        {
            for (int r = _bottom; r > CursorY; r--) _screen[r] = _screen[r - 1];
            _screen[CursorY] = BlankLine(Cols, _bg);
        }
        CursorX = 0;
    }

    private void DeleteLines(int n)
    {
        if (CursorY < _top || CursorY > _bottom) return;
        n = Math.Min(n, _bottom - CursorY + 1);
        for (int i = 0; i < n; i++)
        {
            for (int r = CursorY; r < _bottom; r++) _screen[r] = _screen[r + 1];
            _screen[_bottom] = BlankLine(Cols, _bg);
        }
        CursorX = 0;
    }

    private void InsertChars(int n)
    {
        var line = _screen[CursorY];
        n = Math.Min(n, Cols - CursorX);
        for (int c = Cols - 1; c >= CursorX + n; c--) line[c] = line[c - n];
        for (int c = CursorX; c < CursorX + n; c++) line[c] = Cell.Blank(_bg);
    }

    private void DeleteChars(int n)
    {
        var line = _screen[CursorY];
        n = Math.Min(n, Cols - CursorX);
        for (int c = CursorX; c < Cols - n; c++) line[c] = line[c + n];
        for (int c = Cols - n; c < Cols; c++) line[c] = Cell.Blank(_bg);
    }

    private void FillWithE()
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++) _screen[r][c] = new Cell { Ch = 'E', Fg = -1, Bg = -1 };
    }

    private void FullReset()
    {
        _saved = null;
        _screen = NewScreen();
        _scrollback.Clear();
        _top = 0; _bottom = Rows - 1;
        CursorX = CursorY = 0;
        _fg = _bg = -1; _attr = 0;
        _autoWrap = true; CursorVisible = true; ApplicationCursorKeys = false; BracketedPaste = false;
        _wrapPending = false;
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------

    private Cell[][] NewScreen()
    {
        var screen = new Cell[Rows][];
        for (int r = 0; r < Rows; r++) screen[r] = BlankLine(Cols);
        return screen;
    }

    private static Cell[] BlankLine(int cols, int bg = -1)
    {
        var line = new Cell[cols];
        Array.Fill(line, Cell.Blank(bg));
        return line;
    }

    private bool[] NewTabs()
    {
        var tabs = new bool[Cols];
        for (int c = 8; c < Cols; c += 8) tabs[c] = true;
        return tabs;
    }
}
