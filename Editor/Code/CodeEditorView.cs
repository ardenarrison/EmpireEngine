using System.Text;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Engine.Core.Scripting;
using Engine.Scripting.Build;
using Microsoft.CodeAnalysis;

namespace Editor.Code;

// The code view: edits the C# scripts in the project's Scripts folder, drawn with Canvas2D in a monospaced font.
// It replaces the 3D view while the Code button is on. C# is colored as you type, and the scripts are compiled in
// the background a moment after each edit: errors and warnings are underlined, listed under the editor, and shown
// when the mouse rests on them. Typed characters, key repeat and the clipboard come from ImGui's input queue.
public sealed class CodeEditorView
{
    private const float MenuBarH = 26f;   // the ImGui menu bar stays on top of the view
    private const float HeaderTop = MenuBarH;
    private const float HeaderH = 36f;
    private const float BodyTop = HeaderTop + HeaderH;
    private const float FileListW = 220f;
    private const float ProblemsH = 170f;
    private const float LineH = 21f;
    private const float RowH = 22f;
    private const float TextPad = 10f;
    private const double CheckDelay = 0.4;   // seconds after the last edit before the scripts are compiled again
    private const int MaxNameLength = 40;

    private static readonly float4 Background = Hex(0x1E1E1E);
    private static readonly float4 PanelColor = Hex(0x252526);
    private static readonly float4 HeaderColor = Hex(0x2D2D30);
    private static readonly float4 GutterText = Hex(0x858585);
    private static readonly float4 GutterActive = Hex(0xC6C6C6);
    private static readonly float4 CurrentLine = Hex(0x2A2D2E);
    private static readonly float4 SelectionColor = Hex(0x264F78);
    private static readonly float4 CaretColor = Hex(0xAEAFAD);
    private static readonly float4 ErrorColor = Hex(0xF14C4C);
    private static readonly float4 WarningColor = Hex(0xCCA700);
    private static readonly float4 TextColor = Hex(0xD4D4D4);
    private static readonly float4 DimColor = Hex(0x8B8B8B);
    private static readonly float4 ButtonColor = Hex(0x3A3D41);
    private static readonly float4 ButtonHover = Hex(0x4A4E54);
    private static readonly float4 FieldColor = Hex(0x3C3C3C);
    private static readonly float4 RowHover = Hex(0x2A2D2E);
    private static readonly float4 RowActive = Hex(0x37373D);
    private static readonly float4 TooltipColor = Hex(0x252526);
    private static readonly float4 BorderColor = Hex(0x454545);
    private static readonly float4 BannerColor = Hex(0x3B3222);

    private static readonly float4[] TokenColors =
    {
        Hex(0xD4D4D4),   // Plain
        Hex(0x569CD6),   // Keyword
        Hex(0xC586C0),   // Control
        Hex(0x4EC9B0),   // Type
        Hex(0xDCDCAA),   // Method
        Hex(0x9CDCFE),   // Variable
        Hex(0xCE9178),   // String
        Hex(0xB5CEA8),   // Number
        Hex(0x6A9955),   // Comment
        Hex(0x9B9B9B),   // Preprocessor
    };

    // An error or warning from the last check, in one script.
    private sealed record Problem(string Path, int Line, int Col, int EndLine, int EndCol, bool IsError, string Id, string Message);
    private sealed record CheckResult(List<Problem> Problems, HashSet<string> TypeNames, string? Failure);

    // Loaded once on a background thread: reading every framework assembly takes a second or two.
    private static readonly Lazy<List<MetadataReference>> References = new(
        () => ScriptBuilder.LoadReferences(ScriptBuilder.DefaultReferences(typeof(Behaviour).Assembly.Location)),
        LazyThreadSafetyMode.ExecutionAndPublication);

    // DLLs scripts may also use, such as the project's enabled packages. Read before every check.
    public static Func<IEnumerable<string>>? ExtraReferences { get; set; }

    private readonly string _folder;
    private readonly VcInputState _mouse = new();
    private readonly Dictionary<string, CodeDocument> _docs = new(StringComparer.OrdinalIgnoreCase);
    private CodeDocument? _doc;

    private List<Problem> _problems = new();
    private HashSet<string> _typeNames = new(StringComparer.Ordinal);
    private Task<CheckResult>? _check;
    private bool _checkQueued = true;
    private double _checkDue;
    private string _checkFailure = "";
    private string _folderSignature = "";
    private double _nextFolderScan;

    private TokenColor[][] _colors = [];
    private CodeDocument? _colorsFor;
    private int _colorsVersion = -1;
    private int _colorsNamesVersion = -1;
    private int _namesVersion;

    private double _now;
    private double _caretMoved;
    private bool _revealCaret;
    private string _status = "";
    private bool _exitRequested;

    // Layout from the last Build, used for hit-testing on the next Update.
    private float2 _size;
    private float _charW = 9f;
    private float _textLeft, _editorTop, _editorBottom, _editorRight;
    private float _problemsTop;
    private float _problemsScroll;
    private float _filesScroll;
    private readonly List<(Box Box, Action Click)> _clickables = new();
    private bool _selecting;

    // The "new script" name field in the header.
    private string _newName = "";
    private bool _nameFocused;

    public CodeEditorView(string scriptsFolder) => _folder = scriptsFolder;

    public bool HasUnsavedChanges => _docs.Values.Any(d => d.Dirty);

    // Returns true once after the "3D view" button was pressed.
    public bool TakeExit()
    {
        bool exit = _exitRequested;
        _exitRequested = false;
        return exit;
    }

    // Opens Scripts/<name>.cs, for example from the Inspector's Edit button.
    public void OpenScript(string name) => Open(Path.Combine(_folder, name + ".cs"));

    public void SaveAll()
    {
        int count = 0;
        foreach (var doc in _docs.Values.Where(d => d.Dirty && !d.ReadOnly))
        {
            doc.Save();
            count++;
        }
        if (count > 0) Status($"Saved {count} script{(count == 1 ? "" : "s")}.");
    }

    // Cmd/Ctrl+S while the code view is showing.
    public void SaveCurrent()
    {
        if (_doc is null) return;
        if (_doc.ReadOnly)
        {
            Status($"{_doc.Name}.cs is generated from {Path.GetFileName(_doc.GeneratedFrom)}. Edit that in VC instead.");
            return;
        }
        try
        {
            _doc.Save();
            Status($"Saved {_doc.Name}.cs.");
        }
        catch (Exception ex)
        {
            Status($"Could not save {_doc.Name}.cs: {ex.Message}");
        }
    }

    // ---- input ---------------------------------------------------------------------------

    public void Update(GlfwWindow window, bool uiWantsMouse, double now)
    {
        _now = now;
        _mouse.Update(window);
        ScanFolder(now);
        PollCheck(now);

        if (_doc is null && ListScripts().FirstOrDefault() is { } first)
            Open(first);

        HandleMouse(uiWantsMouse);
        HandleKeyboard();
    }

    private void HandleMouse(bool uiWantsMouse)
    {
        var io = ImGui.GetIO();
        var p = _mouse.Mouse;

        // Wheel scrolling goes to whichever panel is under the pointer.
        float wheel = io.MouseWheel, wheelH = io.MouseWheelH;
        if (!uiWantsMouse && (wheel != 0 || wheelH != 0))
        {
            if (p.Y >= _problemsTop) _problemsScroll = Math.Max(0, _problemsScroll - wheel * RowH * 3);
            else if (p.X < FileListW) _filesScroll = Math.Max(0, _filesScroll - wheel * RowH * 3);
            else if (_doc is not null)
            {
                _doc.ScrollY = Math.Max(0, _doc.ScrollY - wheel * LineH * 3);
                _doc.ScrollX = Math.Max(0, _doc.ScrollX - wheelH * _charW * 6);
            }
        }

        if (_mouse.Pressed && !uiWantsMouse)
        {
            _nameFocused = false;
            foreach (var (box, click) in _clickables)
                if (box.Contains(p))
                {
                    click();
                    return;
                }

            if (_doc is not null && InEditor(p))
            {
                var pos = PositionAt(p);
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    _doc.SelectWordAt(pos);
                else
                {
                    _doc.MoveTo(pos, io.KeyShift);
                    _selecting = true;
                }
                CaretMoved(reveal: false);
            }
        }

        if (_selecting && _mouse.Down && _doc is not null)
        {
            // Dragging past the top or bottom edge scrolls.
            if (p.Y < _editorTop) _doc.ScrollY = Math.Max(0, _doc.ScrollY - LineH);
            if (p.Y > _editorBottom) _doc.ScrollY += LineH;
            _doc.MoveTo(PositionAt(p), extend: true);
            CaretMoved(reveal: false);
        }
        if (_mouse.Released) _selecting = false;
    }

    private void HandleKeyboard()
    {
        var io = ImGui.GetIO();
        bool cmd = io.KeyCtrl || io.KeySuper;
        bool shift = io.KeyShift;
        bool mac = OperatingSystem.IsMacOS();
        bool word = mac ? io.KeyAlt : io.KeyCtrl;

        // Characters typed this frame. Shortcuts with Cmd/Ctrl never type.
        var typed = new StringBuilder();
        var queue = io.InputQueueCharacters;
        for (int i = 0; i < queue.Count; i++)
        {
            uint ch = queue[i];
            if (ch >= 32 && ch != 127 && ch <= 0xFFFF) typed.Append((char)ch);
        }
        if (cmd) typed.Clear();

        if (_nameFocused)
        {
            HandleNameField(typed.ToString());
            return;
        }
        if (_doc is null) return;
        var doc = _doc;

        // Cmd/Ctrl+S is handled by the editor's global shortcut, which calls SaveCurrent while this view shows.
        if (cmd && Pressed(ImGuiKey.A)) { doc.SelectAll(); CaretMoved(reveal: false); return; }
        if (cmd && Pressed(ImGuiKey.C)) { Copy(); return; }

        if (doc.ReadOnly)
        {
            // Generated scripts can be read, selected and copied, but not changed.
            HandleNavigation(doc, cmd, shift, word, mac);
            if (typed.Length > 0 || Pressed(ImGuiKey.Backspace) || Pressed(ImGuiKey.Enter))
                Status($"{doc.Name}.cs is generated from {Path.GetFileName(doc.GeneratedFrom)}. Edit that in VC instead.");
            return;
        }

        int version = doc.Version;
        if (cmd && Pressed(ImGuiKey.X)) { Copy(); if (doc.HasSelection) doc.Insert(""); }
        else if (cmd && Pressed(ImGuiKey.V)) doc.Insert(CodeDocument.Normalize(ClipboardText()));
        else if (cmd && Pressed(ImGuiKey.Z)) { if (shift) doc.Redo(); else doc.Undo(); }
        else if (cmd && Pressed(ImGuiKey.Y)) doc.Redo();
        else if (cmd && Pressed(ImGuiKey.Slash)) doc.ToggleComment();
        else if (Pressed(ImGuiKey.Enter) || Pressed(ImGuiKey.KeypadEnter)) doc.NewLine();
        else if (Pressed(ImGuiKey.Backspace)) doc.Backspace();
        else if (Pressed(ImGuiKey.Delete)) doc.Delete();
        else if (Pressed(ImGuiKey.Tab)) doc.Indent(outdent: shift);
        else if (typed.Length > 0)
            foreach (char ch in typed.ToString()) doc.Type(ch);
        else
            HandleNavigation(doc, cmd, shift, word, mac);

        if (doc.Version != version)
        {
            CaretMoved(reveal: true);
            _checkQueued = true;
            _checkDue = _now + CheckDelay;
        }
    }

    private void HandleNavigation(CodeDocument doc, bool cmd, bool shift, bool word, bool mac)
    {
        var before = (doc.Caret, doc.Anchor);
        bool lineJump = mac && cmd;
        int page = Math.Max(1, (int)((_editorBottom - _editorTop) / LineH) - 1);

        if (Pressed(ImGuiKey.LeftArrow)) { if (lineJump) doc.MoveHome(shift); else doc.MoveLeft(word, shift); }
        else if (Pressed(ImGuiKey.RightArrow)) { if (lineJump) doc.MoveEnd(shift); else doc.MoveRight(word, shift); }
        else if (Pressed(ImGuiKey.UpArrow)) { if (lineJump) doc.MoveTo(new TextPos(0, 0), shift); else doc.MoveVertical(-1, shift); }
        else if (Pressed(ImGuiKey.DownArrow)) { if (lineJump) doc.MoveTo(new TextPos(int.MaxValue, int.MaxValue), shift); else doc.MoveVertical(1, shift); }
        else if (Pressed(ImGuiKey.Home)) { if (cmd && !mac) doc.MoveTo(new TextPos(0, 0), shift); else doc.MoveHome(shift); }
        else if (Pressed(ImGuiKey.End)) { if (cmd && !mac) doc.MoveTo(new TextPos(int.MaxValue, int.MaxValue), shift); else doc.MoveEnd(shift); }
        else if (Pressed(ImGuiKey.PageUp)) doc.MoveVertical(-page, shift);
        else if (Pressed(ImGuiKey.PageDown)) doc.MoveVertical(page, shift);

        if ((doc.Caret, doc.Anchor) != before) CaretMoved(reveal: true);
    }

    private static bool Pressed(ImGuiKey key) => ImGui.IsKeyPressed(key, true);

    private void CaretMoved(bool reveal)
    {
        _caretMoved = _now;
        if (reveal) _revealCaret = true;
    }

    private void Copy()
    {
        if (_doc is { HasSelection: true } doc)
            ImGui.SetClipboardText(doc.SelectedText().ToU8());
    }

    private static string ClipboardText()
    {
        var text = ImGui.GetClipboardText();
        return text.IsNull() ? "" : Encoding.UTF8.GetString(text.AsSpan());
    }

    private void HandleNameField(string typed)
    {
        foreach (char ch in typed)
            if ((char.IsLetterOrDigit(ch) || ch == '_') && _newName.Length < MaxNameLength) _newName += ch;
        if (Pressed(ImGuiKey.Backspace) && _newName.Length > 0) _newName = _newName[..^1];
        if (Pressed(ImGuiKey.Enter) || Pressed(ImGuiKey.KeypadEnter)) CreateScript();
        if (Pressed(ImGuiKey.Escape)) _nameFocused = false;
    }

    // ---- files -----------------------------------------------------------------------------

    private List<string> ListScripts() =>
        Directory.Exists(_folder)
            ? Directory.GetFiles(_folder, "*.cs").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();

    private void Open(string path)
    {
        if (!File.Exists(path))
        {
            Status($"{Path.GetFileName(path)} doesn't exist.");
            return;
        }
        if (!_docs.TryGetValue(path, out var doc))
        {
            try
            {
                doc = CodeDocument.Load(path);
            }
            catch (Exception ex)
            {
                Status($"Could not open {Path.GetFileName(path)}: {ex.Message}");
                return;
            }
            _docs[path] = doc;
        }
        _doc = doc;
        _selecting = false;
        CaretMoved(reveal: false);
    }

    private void CreateScript()
    {
        string name = ScriptFiles.ToClassName(_newName);
        if (name.Length == 0)
        {
            Status("Type a name for the new script first.");
            _nameFocused = true;
            return;
        }
        string path = Path.Combine(_folder, name + ".cs");
        if (File.Exists(path))
        {
            Status($"{name}.cs already exists.");
            Open(path);
            return;
        }
        ScriptFiles.Create(_folder, name);
        _newName = "";
        _nameFocused = false;
        Open(path);
        _checkQueued = true;
        Status($"Created Scripts/{name}.cs.");
    }

    // Once a second: notices scripts added, removed or saved outside the editor (a VC save regenerates its .cs file),
    // reloads open scripts that have no unsaved edits here, and queues a fresh check.
    private void ScanFolder(double now)
    {
        if (now < _nextFolderScan) return;
        _nextFolderScan = now + 1.0;

        var files = ListScripts();
        string signature = string.Join("|", files.Select(f => $"{f}:{File.GetLastWriteTimeUtc(f).Ticks}"));
        if (signature == _folderSignature) return;
        _folderSignature = signature;
        _checkQueued = true;

        foreach (var (path, doc) in _docs.ToList())
        {
            if (!File.Exists(path))
            {
                if (!doc.Dirty) _docs.Remove(path);
                if (_doc == doc && !doc.Dirty) _doc = null;
                continue;
            }
            if (!doc.Dirty && CodeDocument.Normalize(File.ReadAllText(path)) != doc.Text)
                doc.Reload();
        }
    }

    // ---- error checking ----------------------------------------------------------------------

    private void PollCheck(double now)
    {
        if (_check is { IsCompleted: true } done)
        {
            var result = done.Result;
            _check = null;
            _checkFailure = result.Failure ?? "";
            if (result.Failure is null)
            {
                _problems = result.Problems;
                if (!result.TypeNames.SetEquals(_typeNames))
                {
                    _typeNames = result.TypeNames;
                    _namesVersion++;
                }
            }
        }

        if (_check is not null || !_checkQueued || now < _checkDue) return;
        _checkQueued = false;

        // Every script, with unsaved edits taking the place of what's on disk.
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in ListScripts())
        {
            try { files[file] = CodeDocument.Normalize(File.ReadAllText(file)); }
            catch (IOException) { }
        }
        foreach (var (path, doc) in _docs)
            if (File.Exists(path) || doc.Dirty) files[path] = doc.Text;

        _check = Task.Run(() => RunCheck(files));
    }

    private static CheckResult RunCheck(Dictionary<string, string> files)
    {
        try
        {
            var references = References.Value;
            if (ExtraReferences?.Invoke().ToList() is { Count: > 0 } extra)
                references = references.Concat(ScriptBuilder.LoadReferences(extra)).ToList();
            var problems = new List<Problem>();
            foreach (var d in ScriptBuilder.Check(files, references))
            {
                if (!d.Location.IsInSource || d.Location.SourceTree?.FilePath is not { } path || !files.ContainsKey(path)) continue;
                var span = d.Location.GetLineSpan();
                problems.Add(new Problem(path, span.StartLinePosition.Line, span.StartLinePosition.Character,
                    span.EndLinePosition.Line, span.EndLinePosition.Character,
                    d.Severity == DiagnosticSeverity.Error, d.Id, d.GetMessage()));
            }
            // Errors first, then by file, line and column.
            problems = problems
                .OrderBy(p => p.IsError ? 0 : 1)
                .ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Line)
                .ThenBy(p => p.Col)
                .ToList();
            return new CheckResult(problems, ScriptBuilder.TypeNames(files, references), null);
        }
        catch (Exception ex)
        {
            return new CheckResult(new List<Problem>(), new HashSet<string>(), "Error checking failed: " + ex.Message);
        }
    }

    // ---- drawing -------------------------------------------------------------------------------

    public void Build(Canvas2D c, float2 size)
    {
        _size = size;
        _charW = c.MeasureText("M");
        _clickables.Clear();

        c.Begin();
        c.Rect(0, 0, size.X, size.Y, Background);
        DrawFileList(c, size);
        DrawEditor(c, size);
        DrawProblems(c, size);   // after the editor, so the last code line can't show through it
        DrawHeader(c, size);     // and the header over anything scrolled up behind it
        DrawTooltip(c);
    }

    private void DrawHeader(Canvas2D c, float2 size)
    {
        c.Rect(0, 0, size.X, BodyTop, HeaderColor);
        float by = HeaderTop + 6f, bh = HeaderH - 12f;
        float x = 10f;

        if (Button(c, "Save", new Box(x, by, 64, bh))) SaveCurrent();
        x += 72f;

        var field = new Box(x, by, 190f, bh);
        c.Rect(field.X, field.Y, field.W, field.H, _nameFocused ? Hex(0x4A4A4A) : FieldColor);
        string shown = _newName.Length == 0 && !_nameFocused ? "New script name" : _newName + (_nameFocused && Blink() ? "|" : "");
        c.Text(Fit(c, shown, field.W - 12f), field.X + 6f, Mid(field.Y, field.H, c), _newName.Length == 0 && !_nameFocused ? DimColor : TextColor);
        _clickables.Add((field, () => _nameFocused = true));
        x += 198f;
        if (Button(c, "New", new Box(x, by, 52, bh))) CreateScript();
        x += 64f;

        if (_doc is not null)
        {
            string title = $"{_doc.Name}.cs" + (_doc.Dirty ? "  (unsaved)" : "") + (_doc.ReadOnly ? "  (read-only)" : "");
            c.Text(Fit(c, title, size.X - x - 140f), x, Mid(by, bh, c), _doc.Dirty ? WarningColor : TextColor);
        }

        if (Button(c, "3D view", new Box(size.X - 108f, by, 98f, bh))) _exitRequested = true;
    }

    private void DrawFileList(Canvas2D c, float2 size)
    {
        c.Rect(0, BodyTop, FileListW, size.Y - BodyTop, PanelColor);
        c.Text("SCRIPTS", 12f, BodyTop + 8f, DimColor);

        float top = BodyTop + 32f;
        float y = top - _filesScroll;
        var files = ListScripts();
        foreach (var file in files)
        {
            if (y + RowH > top && y < size.Y)
            {
                var box = new Box(0, y, FileListW, RowH);
                bool current = _doc is not null && string.Equals(_doc.Path, file, StringComparison.OrdinalIgnoreCase);
                bool hover = box.Contains(_mouse.Mouse);
                if (current || hover) c.Rect(box.X, box.Y, box.W, box.H, current ? RowActive : RowHover);

                int errors = _problems.Count(p => p.IsError && string.Equals(p.Path, file, StringComparison.OrdinalIgnoreCase));
                bool dirty = _docs.TryGetValue(file, out var doc) && doc.Dirty;
                string name = Path.GetFileName(file) + (dirty ? " *" : "");
                float right = FileListW - 10f;
                if (errors > 0)
                {
                    string count = errors.ToString();
                    right -= c.MeasureText(count) + 4f;
                    c.Text(count, right, y + 2f, ErrorColor);
                }
                c.Text(Fit(c, name, right - 22f), 14f, y + 2f, errors > 0 ? ErrorColor : TextColor);
                string path = file;
                _clickables.Add((box, () => Open(path)));
            }
            y += RowH;
        }
        if (files.Count == 0)
            c.Text("No scripts yet.", 14f, top, DimColor);

        float content = files.Count * RowH;
        _filesScroll = Math.Clamp(_filesScroll, 0, Math.Max(0, content - (size.Y - top)));
    }

    private void DrawEditor(Canvas2D c, float2 size)
    {
        float left = FileListW;
        float top = BodyTop;
        _problemsTop = size.Y - ProblemsH;
        _editorRight = size.X;

        if (_doc is null)
        {
            c.Text("Pick a script on the left, or type a name and press New.", left + 24f, top + 24f, DimColor);
            _textLeft = left;
            _editorTop = top;
            _editorBottom = _problemsTop;
            return;
        }
        var doc = _doc;

        if (doc.ReadOnly)
        {
            c.Rect(left, top, size.X - left, RowH + 6f, BannerColor);
            c.Text(Fit(c, $"Generated from {Path.GetFileName(doc.GeneratedFrom)}. Read-only here: edit it in VC, and this file is rewritten when it saves.",
                size.X - left - 24f), left + 12f, top + 3f, WarningColor);
            top += RowH + 6f;
        }

        int digits = Math.Max(3, doc.Lines.Count.ToString().Length);
        float gutterW = digits * _charW + 28f;
        _textLeft = left + gutterW + TextPad;
        _editorTop = top + 4f;
        _editorBottom = _problemsTop;
        float viewH = _editorBottom - _editorTop;
        float viewW = _editorRight - _textLeft - 12f;

        if (_revealCaret) RevealCaret(doc, viewH, viewW);
        int maxLen = doc.Lines.Max(l => l.Length);
        doc.ScrollY = Math.Clamp(doc.ScrollY, 0, Math.Max(0, doc.Lines.Count * LineH - LineH * 3));
        doc.ScrollX = Math.Clamp(doc.ScrollX, 0, Math.Max(0, maxLen * _charW - viewW + _charW * 4));

        var colors = ColorsFor(doc);
        var problems = _problems.Where(p => string.Equals(p.Path, doc.Path, StringComparison.OrdinalIgnoreCase)).ToList();
        int first = (int)(doc.ScrollY / LineH);
        int last = Math.Min(doc.Lines.Count - 1, first + (int)(viewH / LineH) + 1);
        int firstCol = (int)(doc.ScrollX / _charW);
        int visibleCols = (int)(viewW / _charW) + 2;
        float xShift = firstCol * _charW - doc.ScrollX;
        var (selStart, selEnd) = doc.Selection;

        for (int i = first; i <= last; i++)
        {
            float y = _editorTop + i * LineH - doc.ScrollY;
            if (y + LineH <= _editorTop || y >= _editorBottom) continue;
            string line = doc.Lines[i];

            if (i == doc.Caret.Line && !doc.HasSelection)
                c.Rect(left + gutterW, y, size.X - left - gutterW, LineH, CurrentLine);

            // Selection.
            if (doc.HasSelection && i >= selStart.Line && i <= selEnd.Line)
            {
                int s = i == selStart.Line ? selStart.Col : 0;
                int e = i == selEnd.Line ? selEnd.Col : line.Length + 1;
                float sx = Math.Max(_textLeft, ColX(s, doc)), ex = Math.Min(_editorRight, ColX(e, doc));
                if (ex > sx) c.Rect(sx, y, ex - sx, LineH, SelectionColor);
            }

            // Text, one run of same-colored characters at a time, clipped to the visible columns.
            int from = Math.Min(firstCol, line.Length), to = Math.Min(line.Length, firstCol + visibleCols);
            var lineColors = i < colors.Length ? colors[i] : [];
            int run = from;
            while (run < to)
            {
                var color = run < lineColors.Length ? lineColors[run] : TokenColor.Plain;
                int end = run + 1;
                while (end < to && (end < lineColors.Length ? lineColors[end] : TokenColor.Plain) == color) end++;
                string piece = line[run..end];
                if (piece.Trim().Length > 0)
                    c.Text(piece, _textLeft + (run - firstCol) * _charW + xShift, y + 1f, TokenColors[(int)color]);
                run = end;
            }

            // Squiggles: a thick line under each problem's span on this line (at least one character wide).
            foreach (var p in problems.Where(p => i >= p.Line && i <= p.EndLine))
            {
                int s = i == p.Line ? p.Col : 0;
                int e = i == p.EndLine ? p.EndCol : line.Length;
                if (e <= s) { s = Math.Max(0, Math.Min(s, line.Length - 1)); e = s + 1; }
                float sx = Math.Max(_textLeft, ColX(s, doc)), ex = Math.Min(_editorRight, ColX(e, doc));
                if (ex > sx) c.Rect(sx, y + LineH - 3f, ex - sx, 2f, p.IsError ? ErrorColor : WarningColor);
            }
        }

        // Caret, blinking, and steady for a moment after it moves.
        if (Blink() && !_nameFocused)
        {
            float cy = _editorTop + doc.Caret.Line * LineH - doc.ScrollY;
            float cx = ColX(doc.Caret.Col, doc);
            if (cy >= _editorTop - 1 && cy + LineH <= _editorBottom + 1 && cx >= _textLeft - 1)
                c.Rect(cx, cy + 1f, 2f, LineH - 2f, CaretColor);
        }

        // Gutter last, so text scrolled to the left stays under it.
        c.Rect(left, top, gutterW, _editorBottom - top, Background);
        for (int i = first; i <= last; i++)
        {
            float y = _editorTop + i * LineH - doc.ScrollY;
            if (y + LineH <= _editorTop || y >= _editorBottom) continue;
            string number = (i + 1).ToString();
            c.Text(number, left + gutterW - 10f - c.MeasureText(number), y + 1f, i == doc.Caret.Line ? GutterActive : GutterText);
            if (problems.FirstOrDefault(p => p.Line == i) is { } marker)
                c.Rect(left + 6f, y + LineH / 2f - 3f, 6f, 6f, marker.IsError ? ErrorColor : WarningColor);
        }
        c.Rect(left, top, size.X - left, _editorTop - top, Background);
    }

    private void DrawProblems(Canvas2D c, float2 size)
    {
        float left = FileListW;
        float top = size.Y - ProblemsH;
        c.Rect(left, top, size.X - left, ProblemsH, PanelColor);
        c.Rect(left, top, size.X - left, 1f, BorderColor);

        int errors = _problems.Count(p => p.IsError), warnings = _problems.Count - errors;
        string summary = _checkFailure.Length > 0 ? _checkFailure
            : $"PROBLEMS   {errors} error{(errors == 1 ? "" : "s")}, {warnings} warning{(warnings == 1 ? "" : "s")}";
        c.Text(Fit(c, summary, size.X - left - 260f), left + 12f, top + 6f, errors > 0 || _checkFailure.Length > 0 ? ErrorColor : DimColor);
        string state = _check is not null ? "Checking..." : _checkQueued ? "Edited" : "Up to date";
        c.Text(state, size.X - c.MeasureText(state) - 14f, top + 6f, DimColor);

        float listTop = top + 30f;
        float statusH = _status.Length > 0 ? RowH : 0f;
        float listBottom = size.Y - statusH;
        float y = listTop - _problemsScroll;
        foreach (var p in _problems)
        {
            if (y + RowH > listTop && y < listBottom)
            {
                var box = new Box(left, y, size.X - left, RowH);
                if (box.Contains(_mouse.Mouse)) c.Rect(box.X, box.Y, box.W, box.H, RowHover);
                c.Rect(left + 12f, y + RowH / 2f - 3f, 6f, 6f, p.IsError ? ErrorColor : WarningColor);
                string where = $"{Path.GetFileName(p.Path)}:{p.Line + 1}:{p.Col + 1}";
                c.Text(where, left + 26f, y + 2f, DimColor);
                float mx = left + 26f + Math.Max(c.MeasureText(where) + 16f, _charW * 22);
                c.Text(Fit(c, $"{p.Id}  {OneLine(p.Message)}", size.X - mx - 12f), mx, y + 2f, TextColor);
                var problem = p;
                _clickables.Add((box, () => JumpTo(problem)));
            }
            y += RowH;
        }
        if (_problems.Count == 0 && _checkFailure.Length == 0)
            c.Text(_check is not null && _typeNames.Count == 0 ? "Checking the scripts..." : "No problems.", left + 26f, listTop, DimColor);

        // The hidden rows: clamp the scroll, and blank everything that scrolled above the list.
        _problemsScroll = Math.Clamp(_problemsScroll, 0, Math.Max(0, _problems.Count * RowH - (listBottom - listTop)));

        if (_status.Length > 0)
        {
            c.Rect(left, size.Y - statusH, size.X - left, statusH, HeaderColor);
            c.Text(Fit(c, _status, size.X - left - 24f), left + 12f, size.Y - statusH + 2f, TextColor);
        }
    }

    // The message of the problem under the mouse in the editor, in a box next to the pointer.
    private void DrawTooltip(Canvas2D c)
    {
        if (_doc is null || _selecting || !InEditor(_mouse.Mouse)) return;
        var pos = PositionAt(_mouse.Mouse, clampCol: false);
        var hit = _problems.FirstOrDefault(p =>
            string.Equals(p.Path, _doc.Path, StringComparison.OrdinalIgnoreCase) &&
            new TextPos(p.Line, p.Col).CompareTo(pos) <= 0 &&
            new TextPos(p.EndLine, Math.Max(p.EndCol, p.Col + 1)).CompareTo(pos) > 0);
        if (hit is null) return;

        var lines = Wrap($"{(hit.IsError ? "error" : "warning")} {hit.Id}: {OneLine(hit.Message)}", 70);
        float w = lines.Max(l => c.MeasureText(l)) + 20f;
        float h = lines.Count * LineH + 12f;
        float x = Math.Min(_mouse.Mouse.X + 12f, _size.X - w - 8f);
        float y = _mouse.Mouse.Y + 18f;
        if (y + h > _size.Y - 8f) y = _mouse.Mouse.Y - h - 8f;
        c.Rect(x - 1, y - 1, w + 2, h + 2, hit.IsError ? ErrorColor : WarningColor);
        c.Rect(x, y, w, h, TooltipColor);
        for (int i = 0; i < lines.Count; i++)
            c.Text(lines[i], x + 10f, y + 6f + i * LineH, TextColor);
    }

    private TokenColor[][] ColorsFor(CodeDocument doc)
    {
        if (_colorsFor != doc || _colorsVersion != doc.Version || _colorsNamesVersion != _namesVersion)
        {
            try
            {
                _colors = CSharpHighlighter.Classify(doc.Lines, _typeNames);
            }
            catch (Exception)
            {
                _colors = [];
            }
            _colorsFor = doc;
            _colorsVersion = doc.Version;
            _colorsNamesVersion = _namesVersion;
        }
        return _colors;
    }

    private void RevealCaret(CodeDocument doc, float viewH, float viewW)
    {
        _revealCaret = false;
        float caretY = doc.Caret.Line * LineH;
        if (caretY < doc.ScrollY) doc.ScrollY = caretY;
        if (caretY + LineH > doc.ScrollY + viewH) doc.ScrollY = caretY + LineH - viewH;
        float caretX = doc.Caret.Col * _charW;
        if (caretX < doc.ScrollX) doc.ScrollX = Math.Max(0, caretX - _charW * 4);
        if (caretX > doc.ScrollX + viewW - _charW) doc.ScrollX = caretX - viewW + _charW * 4;
    }

    private void JumpTo(Problem p)
    {
        Open(p.Path);
        if (_doc is null) return;
        _doc.MoveTo(new TextPos(p.Line, p.Col), extend: false);
        _revealCaret = true;
        CaretMoved(reveal: true);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private bool InEditor(float2 p) =>
        p.X >= FileListW && p.X < _editorRight && p.Y >= _editorTop && p.Y < _editorBottom;

    private float ColX(int col, CodeDocument doc) => _textLeft + col * _charW - doc.ScrollX;

    private TextPos PositionAt(float2 p, bool clampCol = true)
    {
        var doc = _doc!;
        int line = (int)MathF.Floor((p.Y - _editorTop + doc.ScrollY) / LineH);
        int col = (int)MathF.Round((p.X - _textLeft + doc.ScrollX) / _charW);
        if (!clampCol)
            return new TextPos(line, (int)MathF.Floor((p.X - _textLeft + doc.ScrollX) / _charW));
        if (line >= doc.Lines.Count) return new TextPos(doc.Lines.Count - 1, doc.Lines[^1].Length);
        return doc.Clamp(new TextPos(line, col));
    }

    private bool Blink() => (_now - _caretMoved) % 1.1 < 0.65;

    // Header buttons act during Build, on the frame the mouse went down over them (like the VC view's buttons).
    private bool Button(Canvas2D c, string label, Box b)
    {
        bool hover = b.Contains(_mouse.Mouse);
        c.Rect(b.X, b.Y, b.W, b.H, hover ? ButtonHover : ButtonColor);
        c.Text(label, b.X + (b.W - c.MeasureText(label)) / 2f, Mid(b.Y, b.H, c), TextColor);
        return hover && _mouse.Pressed;
    }

    private static float Mid(float y, float h, Canvas2D c) => y + (h - c.FontSize) / 2f - 1f;

    private static string Fit(Canvas2D c, string text, float maxWidth)
    {
        if (maxWidth <= 0) return "";
        if (c.MeasureText(text) <= maxWidth) return text;
        while (text.Length > 1 && c.MeasureText(text + "...") > maxWidth) text = text[..^1];
        return text + "...";
    }

    private static string OneLine(string text) => text.Replace("\r", " ").Replace("\n", " ");

    private static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var word in text.Split(' '))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > width)
            {
                lines.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0) current.Append(' ');
            current.Append(word);
        }
        if (current.Length > 0) lines.Add(current.ToString());
        return lines;
    }

    private void Status(string text) => _status = text;

    private static float4 Hex(int rgb) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
}
