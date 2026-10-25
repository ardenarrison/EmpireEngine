using Editor.Ui;
using System.Text.RegularExpressions;
using Brutal.GlfwApi;
using Brutal.Numerics;
using VisualCode.Blocks;
using VisualCode.Compile;
using VisualCode.Model;
using VisualCode.Transpiler;

namespace Editor;

// The visual scripting view, drawn with Canvas2D. It replaces the 3D view while the VC button is on.
// Drag blocks from the palette, snap them below blocks or into C-block mouths, drop reporters into value boxes,
// edit values in the blocks or the inspector, and drag blocks onto the palette to delete them.
// Cmd/Ctrl+D (or the inspector's Duplicate button) copies the selected block and the blocks below it.
public sealed class VisualScriptView
{
    private const float MenuBarH = 26f;   // the ImGui menu bar stays on top of the view
    private const float HeaderTop = MenuBarH;
    private const float HeaderH = 36f;
    private const float PanelTop = HeaderTop + HeaderH + 2f;
    private const float PaletteW = 330f;
    private const float InspectorW = 270f;
    private const float BlockH = 32f;
    private const float ChipH = 24f;
    private const float MouthH = 26f;
    private const float ItemH = 24f;
    private const float SnapDistance = 28f;
    private const float DragThreshold = 4f;
    private const int MaxFieldLength = 32;   // the most characters any text field in the view accepts

    private static readonly string[] CategoryOrder = { "Events", "Motion", "Control", "Sensing", "Variables", "Operators" };
    private static readonly Dictionary<string, float4> Categories = new()
    {
        ["Events"] = new float4(1f, 0.75f, 0f, 1f),
        ["Motion"] = new float4(0.3f, 0.6f, 1f, 1f),
        ["Control"] = new float4(1f, 0.67f, 0.1f, 1f),
        ["Sensing"] = new float4(0.36f, 0.72f, 0.84f, 1f),
        ["Variables"] = new float4(1f, 0.55f, 0.1f, 1f),
        ["Operators"] = new float4(0.35f, 0.75f, 0.35f, 1f),
    };

    private static readonly float4 Ink = new(0.08f, 0.08f, 0.10f, 1f);
    private static float4 Dim => Theme.Light ? new(0.38f, 0.40f, 0.45f, 1f) : new(0.55f, 0.57f, 0.62f, 1f);
    private static float4 White => Theme.Light ? new(0.10f, 0.10f, 0.12f, 1f) : new(1f, 1f, 1f, 1f);
    private static float4 BackgroundColor => Theme.Light ? new(0.96f, 0.96f, 0.98f, 1f) : new(0.10f, 0.11f, 0.14f, 1f);
    private static float4 HeaderColor => Theme.Light ? new(0.89f, 0.90f, 0.94f, 1f) : new(0.16f, 0.17f, 0.21f, 1f);
    private static float4 PanelColor => Theme.Light ? new(0.93f, 0.94f, 0.96f, 1f) : new(0.14f, 0.15f, 0.19f, 1f);
    private static float4 ButtonColor => Theme.Light ? new(0.85f, 0.87f, 0.91f, 1f) : new(0.24f, 0.26f, 0.33f, 1f);
    private static float4 ButtonHoverColor => Theme.Light ? new(0.78f, 0.81f, 0.88f, 1f) : new(0.32f, 0.35f, 0.44f, 1f);
    private static readonly float4 FieldColor = new(0.95f, 0.95f, 0.97f, 1f);
    private static readonly float4 FieldFocusColor = new(1f, 1f, 1f, 1f);
    private static float4 MouthColor => Theme.Light ? new(0.86f, 0.88f, 0.93f, 1f) : new(0.20f, 0.22f, 0.27f, 1f);
    private static readonly float4 Gray = new(0.5f, 0.5f, 0.5f, 1f);

    private sealed record Slot(Box Box, string NodeId, string Input, InputKind Kind);
    private sealed record Hit(Box Box, string NodeId);
    private sealed record Attach(float X, float Y, string NodeId, string Relation);
    private sealed record PaletteHit(Box Box, BlockSpec Spec);
    private sealed record Seg(string? Text, string? Input);

    private enum PendingKind { None, Block, Chip, Palette, Scroll }
    private enum DragKind { None, Stack, Reporter }

    private readonly string _scriptsFolder;
    private readonly BlockRegistry _registry = BlockRegistry.Default;
    private readonly VcInputState _input = new();

    // Rebuilt every frame by Build, and used for hit-testing on the next frame.
    private readonly List<Slot> _slots = new();
    private readonly List<Hit> _chips = new();
    private readonly List<Hit> _blocks = new();
    private readonly List<Attach> _attach = new();
    private readonly List<PaletteHit> _palette = new();

    private VcGraph _graph = new();
    private string? _path;
    private string? _selectedId;
    private string? _focus;
    private string _name = "Script";
    private string _newVar = "";
    private string _status = "";
    private float _paletteScroll;
    private float _fontTop = 9f;   // half the font's height plus a pixel, so text sits centered in a row
    private float _viewWidth;
    private bool _uiWants;
    private bool _keysConsumed;

    // Pointer interaction.
    private PendingKind _pending = PendingKind.None;
    private string? _pendingId;
    private BlockSpec? _pendingSpec;
    private float2 _pressPoint;
    private float2 _grab;
    private float _scrollStart;
    private float _scrollOrigin;
    private DragKind _drag = DragKind.None;
    private string? _dragHead;
    private HashSet<string> _dragSet = new();

    public VisualScriptView(string scriptsFolder)
    {
        _scriptsFolder = scriptsFolder;
    }

    // True while a text field has focus, so global shortcuts like P can leave the keys alone.
    public bool IsTyping => _focus is not null;

    private bool _exitRequested;

    // Returns true once after the "3D view" button was pressed.
    public bool TakeExit()
    {
        bool exit = _exitRequested;
        _exitRequested = false;
        return exit;
    }

    public void Update(GlfwWindow window, bool uiWantsMouse)
    {
        _input.Update(window);
        _uiWants = uiWantsMouse;
        _keysConsumed = false;

        if (_input.Pressed && !uiWantsMouse) OnPress(_input.Mouse);
        if (_input.Down && _pending != PendingKind.None) OnDrag(_input.Mouse);
        if (_input.Released) OnRelease(_input.Mouse);

        if (_input.Duplicate && _focus is null && _drag == DragKind.None && _selectedId is { } id && _graph.Find(id) is { } node)
            DuplicateNode(node);
    }

    // ---- building a frame ------------------------------------------------------------

    public void Build(Canvas2D c, float2 size)
    {
        _viewWidth = size.X;
        _fontTop = c.FontSize / 2f + 1f;
        _slots.Clear();
        _chips.Clear();
        _blocks.Clear();
        _attach.Clear();
        _palette.Clear();

        c.Begin();
        c.Rect(0, 0, size.X, size.Y, BackgroundColor);
        DrawHeader(c, size);
        DrawPalette(c, size);
        DrawWorkspace(c, size);
        DrawInspector(c, size);
        c.Text(_status, PaletteW + 12f, size.Y - 24f, Dim);
    }

    private void DrawHeader(Canvas2D c, float2 size)
    {
        c.Rect(0, HeaderTop, size.X, HeaderH, HeaderColor);
        float by = HeaderTop + 6f;
        float bh = HeaderH - 12f;

        if (Button(c, "New", new Box(10, by, 60, bh))) NewScript();
        if (Button(c, "Save", new Box(78, by, 60, bh))) Save();
        if (Button(c, "Check", new Box(146, by, 70, bh))) Check();

        var nameBox = new Box(230, by, 200, bh);
        _slots.Add(new Slot(nameBox, "", "name", InputKind.Any));
        TextField(c, FieldKey("", "name"), nameBox, ref _name);

        if (Button(c, "3D view", new Box(size.X - 118f, by, 106f, bh)))
            _exitRequested = true;

        float fx = 448f;
        foreach (var file in ListScripts())
        {
            string label = Path.GetFileNameWithoutExtension(file);
            float w = Math.Max(90f, c.MeasureText(label) + 18f);
            if (fx + w > size.X - 130f) break;
            if (Button(c, label, new Box(fx, by, w, bh))) Open(file);
            fx += w + 6f;
        }
    }

    private void DrawPalette(Canvas2D c, float2 size)
    {
        float bottom = size.Y;
        c.Rect(0, PanelTop, PaletteW, bottom - PanelTop, PanelColor);

        float y = PanelTop + 8f - _paletteScroll;
        foreach (var category in CategoryOrder)
        {
            if (Visible(y, 18f, bottom))
                c.Text(category.ToUpperInvariant(), 10f, y, CategoryColor(category));
            y += 22f;

            if (category == "Variables")
            {
                if (Visible(y, 24f, bottom))
                {
                    var field = new Box(10, y, PaletteW - 80f, 24f);
                    _slots.Add(new Slot(field, "", "newvar", InputKind.Any));
                    TextField(c, FieldKey("", "newvar"), field, ref _newVar);
                    if (Button(c, "Add", new Box(PaletteW - 62f, y, 52f, 24f))) AddVariable(_newVar);
                }
                y += 30f;
            }

            foreach (var spec in _registry.Specs.Where(s => s.Category == category))
            {
                if (Visible(y, ItemH, bottom))
                {
                    var box = new Box(10, y, PaletteW - 20f, ItemH);
                    c.Rect(box.X, box.Y, box.W, box.H, CategoryColor(category));
                    c.Text(Fit(c, Plain(spec.Label), box.W - 12f), box.X + 6f, TopOf(box.Y, box.H), Ink);
                    _palette.Add(new PaletteHit(box, spec));
                }
                y += ItemH + 4f;
            }
            y += 10f;
        }

        float contentH = y + _paletteScroll - PanelTop;
        _paletteScroll = Math.Clamp(_paletteScroll, 0f, Math.Max(0f, contentH - (bottom - PanelTop)));
    }

    private void DrawWorkspace(Canvas2D c, float2 size)
    {
        var refs = _graph.ReferencedIds();
        foreach (var node in _graph.Nodes.Where(n => !refs.Contains(n.Id)).ToList())
        {
            float x = Math.Max((float)node.X, PaletteW + 12f);
            float y = Math.Max((float)node.Y, PanelTop + 8f);
            node.X = x;
            node.Y = y;

            var spec = _registry.Get(node.Type);
            if (spec is null) continue;

            if (spec.Shape == BlockShape.Reporter)
            {
                float w = RowWidth(c, node) + 8f;
                DrawRow(c, node, x, y, w, ChipH + 8f, chip: true);
                _blocks.Add(new Hit(new Box(x, y, w, ChipH + 8f), node.Id));
            }
            else
            {
                LayoutChain(c, node.Id, x, y);
            }
        }
    }

    private void DrawInspector(Canvas2D c, float2 size)
    {
        float ix = size.X - InspectorW;
        c.Rect(ix, PanelTop, InspectorW, size.Y - PanelTop, PanelColor);
        c.Text("Selected block", ix + 12f, PanelTop + 10f, White);

        var node = _selectedId is null ? null : _graph.Find(_selectedId);
        var spec = node is null ? null : _registry.Get(node.Type);
        if (node is null || spec is null)
        {
            c.Text("Click a block to edit it.", ix + 12f, PanelTop + 40f, Dim);
            return;
        }

        float fw = InspectorW - 24f;
        float y = PanelTop + 40f;
        c.Text(Fit(c, Plain(spec.Label), fw), ix + 12f, y, White);
        y += 30f;

        foreach (var input in spec.Inputs)
        {
            c.Text(input.Name, ix + 12f, y, Dim);
            y += 18f;

            if (node.Inputs.TryGetValue(input.Name, out var v) && v.Node is { } childId && _graph.Find(childId) is { } child)
            {
                c.Text(Fit(c, "plugged: " + Label(child), fw - 70f), ix + 12f, y + 4f, White);
                if (Button(c, "Select", new Box(ix + 12f + fw - 62f, y, 62f, 24f))) _selectedId = childId;
                y += 34f;
            }
            else
            {
                string value = LiteralOf(node, input.Name, input);
                var box = new Box(ix + 12f, y, fw, 26f);
                _slots.Add(new Slot(box, node.Id, input.Name, input.Kind));
                if (TextField(c, FieldKey(node.Id, input.Name), box, ref value))
                    node.Inputs[input.Name] = new VcInput { Literal = value };
                y += 36f;
            }
        }

        if (Button(c, "Duplicate block", new Box(ix + 12f, y + 8f, fw, 30f)))
            DuplicateNode(node);
        if (Button(c, "Delete block", new Box(ix + 12f, y + 46f, fw, 30f)))
            DeleteNode(node);
    }

    // ---- layout and drawing of blocks ------------------------------------------------

    // Lays out one stack from startId downward. Returns the y of its bottom edge.
    private float LayoutChain(Canvas2D c, string? startId, float x, float y)
    {
        var seen = new HashSet<string>();
        float cy = y;
        var id = startId;
        while (id is not null && seen.Add(id) && _graph.Find(id) is { } node)
        {
            cy = LayoutBlock(c, node, x, cy);
            id = node.Next;
        }
        return cy;
    }

    private float LayoutBlock(Canvas2D c, VcNode node, float x, float y)
    {
        var spec = _registry.Get(node.Type);
        float w = Math.Max(200f, RowWidth(c, node) + 12f);
        DrawRow(c, node, x, y, w, BlockH, chip: false);
        _blocks.Add(new Hit(new Box(x, y, w, BlockH), node.Id));

        float bottom = y + BlockH;
        if (spec is { Shape: BlockShape.CBlock or BlockShape.IfElse })
        {
            bottom = LayoutMouth(c, node, node.Body, "body", x + 18f, bottom, w);
            if (spec.Shape == BlockShape.IfElse)
            {
                c.Rect(x, bottom, w, MouthH, CategoryColor(spec.Category));
                c.Text("else", x + 8f, TopOf(bottom, MouthH), Ink);
                _blocks.Add(new Hit(new Box(x, bottom, w, MouthH), node.Id));
                bottom += MouthH;
                bottom = LayoutMouth(c, node, node.ElseBody, "else", x + 18f, bottom, w);
            }
        }

        _attach.Add(new Attach(x, bottom, node.Id, "next"));
        return bottom;
    }

    private float LayoutMouth(Canvas2D c, VcNode owner, string? firstId, string relation, float x, float y, float w)
    {
        _attach.Add(new Attach(x, y, owner.Id, relation));
        if (firstId is null || _graph.Find(firstId) is null)
        {
            c.Rect(x, y, Math.Max(120f, w - 18f), MouthH, MouthColor);
            c.Text("drop blocks here", x + 8f, TopOf(y, MouthH), Dim);
            return y + MouthH;
        }

        float bottom = LayoutChain(c, firstId, x, y);
        c.Rect(x - 10f, y, 6f, Math.Max(MouthH, bottom - y), MouthColor);
        return Math.Max(bottom, y + MouthH);
    }

    // Draws one row of a block: its label with values in place, and reporters plugged in as chips.
    private void DrawRow(Canvas2D c, VcNode node, float x, float y, float w, float h, bool chip)
    {
        var spec = _registry.Get(node.Type);
        if (spec is null) return;

        c.Rect(x, y, w, h, chip ? ChipColor(spec.Category) : CategoryColor(spec.Category));
        float cx = x + 8f;
        foreach (var seg in Segments(spec.Label))
        {
            if (seg.Input is null)
            {
                c.Text(seg.Text!, cx, TopOf(y, h), Ink);
                cx += c.MeasureText(seg.Text!);
                continue;
            }

            var inputSpec = spec.Inputs.FirstOrDefault(i => i.Name == seg.Input);
            if (node.Inputs.TryGetValue(seg.Input, out var v) && v.Node is { } childId && _graph.Find(childId) is { } child)
            {
                float cw = RowWidth(c, child) + 8f;
                _chips.Add(new Hit(new Box(cx, y + 3f, cw, h - 6f), child.Id));
                DrawRow(c, child, cx, y + 3f, cw, h - 6f, chip: true);
                cx += cw;
            }
            else
            {
                string value = LiteralOf(node, seg.Input, inputSpec);
                float fw = FieldWidth(c, value);
                var box = new Box(cx, y + 4f, fw, h - 8f);
                _slots.Add(new Slot(box, node.Id, seg.Input, inputSpec?.Kind ?? InputKind.Number));
                if (TextField(c, FieldKey(node.Id, seg.Input), box, ref value))
                    node.Inputs[seg.Input] = new VcInput { Literal = value };
                cx += fw;
            }
        }
    }

    private float RowWidth(Canvas2D c, VcNode node)
    {
        var spec = _registry.Get(node.Type);
        if (spec is null) return 16f;

        float w = 16f;
        foreach (var seg in Segments(spec.Label))
        {
            if (seg.Input is null)
            {
                w += c.MeasureText(seg.Text!);
                continue;
            }

            if (node.Inputs.TryGetValue(seg.Input, out var v) && v.Node is { } childId && _graph.Find(childId) is { } child)
                w += RowWidth(c, child) + 8f;
            else
                w += FieldWidth(c, LiteralOf(node, seg.Input, spec.Inputs.FirstOrDefault(i => i.Name == seg.Input)));
        }
        return w;
    }

    private static float FieldWidth(Canvas2D c, string value) => Math.Max(44f, c.MeasureText(value) + 14f);

    private static List<Seg> Segments(string label)
    {
        var list = new List<Seg>();
        foreach (var part in Regex.Split(label, @"(\{\w+\})"))
        {
            if (part.Length == 0) continue;
            var match = Regex.Match(part, @"^\{(\w+)\}$");
            list.Add(match.Success ? new Seg(null, match.Groups[1].Value) : new Seg(part, null));
        }
        return list;
    }

    private static string LiteralOf(VcNode node, string input, InputSpec? spec) =>
        node.Inputs.TryGetValue(input, out var v) && v.Literal is { } literal ? literal : spec?.Default ?? "";

    private string Label(VcNode node)
    {
        var spec = _registry.Get(node.Type);
        if (spec is null) return node.Type;
        return Regex.Replace(spec.Label, @"\{(\w+)\}", m =>
        {
            string input = m.Groups[1].Value;
            if (node.Inputs.TryGetValue(input, out var v) && v.Node is { } childId)
                return _graph.Find(childId) is { } child ? "(" + Label(child) + ")" : "(missing)";
            string text = LiteralOf(node, input, spec.Inputs.FirstOrDefault(i => i.Name == input));
            return text.Length == 0 ? "_" : text;
        });
    }

    private static string Plain(string label) => Regex.Replace(label, @"\{\w+\}", "..");

    private static string Fit(Canvas2D c, string text, float maxWidth)
    {
        if (c.MeasureText(text) <= maxWidth) return text;
        while (text.Length > 1 && c.MeasureText(text + "..") > maxWidth)
            text = text[..^1];
        return text + "..";
    }

    // ---- widgets ---------------------------------------------------------------------

    private bool Button(Canvas2D c, string label, Box b)
    {
        bool hover = b.Contains(_input.Mouse) && !_uiWants;
        c.Rect(b.X, b.Y, b.W, b.H, hover ? ButtonHoverColor : ButtonColor);
        c.Text(label, b.X + 8f, TopOf(b.Y, b.H), White);
        return hover && _input.Pressed;
    }

    // Draws a text field and, when it has focus, takes typed characters. Returns true when the value changed.
    private bool TextField(Canvas2D c, string id, Box box, ref string value)
    {
        bool focused = _focus == id;
        c.Rect(box.X, box.Y, box.W, box.H, focused ? FieldFocusColor : FieldColor);
        c.Text(value, box.X + 6f, TopOf(box.Y, box.H), Ink);
        if (focused)
            c.Text("|", box.X + 6f + c.MeasureText(value), TopOf(box.Y, box.H), Ink);

        // Only one field gets the keys each frame, even when two fields show the same value.
        if (!focused || _keysConsumed) return false;

        bool changed = false;
        if (_input.Typed.Length > 0 && value.Length < MaxFieldLength)
        {
            value += _input.Typed;
            if (value.Length > MaxFieldLength) value = value[..MaxFieldLength];
            changed = true;
        }
        if (_input.Backspace && value.Length > 0)
        {
            value = value[..^1];
            changed = true;
        }
        if (_input.Enter || _input.Escape)
            _focus = null;
        if (changed || _input.Enter || _input.Escape)
            _keysConsumed = true;
        return changed;
    }

    private static string FieldKey(string nodeId, string input) => $"{nodeId}:{input}";

    private static T? HitAt<T>(List<T> items, Func<T, Box> box, float2 p) where T : class
    {
        for (int i = items.Count - 1; i >= 0; i--)
            if (box(items[i]).Contains(p)) return items[i];
        return null;
    }

    private static bool Visible(float y, float h, float bottom) => y >= PanelTop && y + h <= bottom;

    private float TopOf(float y, float h) => y + h / 2f - _fontTop;

    private static float4 CategoryColor(string category) =>
        Categories.TryGetValue(category, out var color) ? color : Gray;

    private static float4 ChipColor(string category)
    {
        var color = CategoryColor(category);
        return new float4(color.X * 0.8f, color.Y * 0.8f, color.Z * 0.8f, 1f);
    }

    // ---- pointer -----------------------------------------------------------------------

    private void OnPress(float2 p)
    {
        _focus = null;

        if (HitAt(_slots, s => s.Box, p) is { } slot)
        {
            _focus = FieldKey(slot.NodeId, slot.Input);
            return;
        }
        if (HitAt(_chips, h => h.Box, p) is { } chip)
        {
            StartPending(PendingKind.Chip, chip.NodeId, chip.Box, p);
            return;
        }
        if (HitAt(_blocks, h => h.Box, p) is { } block)
        {
            _selectedId = block.NodeId;
            StartPending(PendingKind.Block, block.NodeId, block.Box, p);
            return;
        }
        if (HitAt(_palette, h => h.Box, p) is { } item)
        {
            _pendingSpec = item.Spec;
            StartPending(PendingKind.Palette, null, item.Box, p);
            return;
        }
        if (p.X < PaletteW)
        {
            _pending = PendingKind.Scroll;
            _scrollStart = p.Y;
            _scrollOrigin = _paletteScroll;
            return;
        }
        if (p.X < _viewWidth - InspectorW)
            _selectedId = null;
    }

    private void StartPending(PendingKind kind, string? id, Box box, float2 p)
    {
        _pending = kind;
        _pendingId = id;
        _pressPoint = p;
        _grab = new float2(p.X - box.X, p.Y - box.Y);
    }

    private void OnDrag(float2 p)
    {
        if (_pending == PendingKind.Scroll)
        {
            _paletteScroll = Math.Max(0f, _scrollOrigin - (p.Y - _scrollStart));
            return;
        }

        if (_drag == DragKind.None)
        {
            if (Distance(p, _pressPoint) < DragThreshold) return;
            BeginDrag(p);
        }

        if (_dragHead is { } head && _graph.Find(head) is { } node)
        {
            node.X = p.X - _grab.X;
            node.Y = p.Y - _grab.Y;
        }
    }

    private void BeginDrag(float2 p)
    {
        switch (_pending)
        {
            case PendingKind.Block when _pendingId is { } blockId && _graph.Find(blockId) is { } block:
                Unlink(block, keepRest: false);
                _dragSet = Chain(block.Id);
                _dragHead = block.Id;
                _drag = IsReporter(block) ? DragKind.Reporter : DragKind.Stack;
                break;

            case PendingKind.Chip when _pendingId is { } chipId && _graph.Find(chipId) is { } chip:
                Unlink(chip, keepRest: false);
                _dragSet = Subtree(chip.Id);
                _dragHead = chip.Id;
                _drag = DragKind.Reporter;
                break;

            case PendingKind.Palette when _pendingSpec is { } spec:
                var created = NewNode(spec);
                created.X = p.X - _grab.X;
                created.Y = p.Y - _grab.Y;
                _graph.Nodes.Add(created);
                _dragSet = new HashSet<string> { created.Id };
                _dragHead = created.Id;
                _drag = spec.Shape == BlockShape.Reporter ? DragKind.Reporter : DragKind.Stack;
                break;
        }
    }

    private void OnRelease(float2 p)
    {
        if (_drag == DragKind.None)
        {
            // A press without a drag is a click.
            if (_pending == PendingKind.Palette && _pendingSpec is { } spec)
                AddFromPalette(spec);
            else if (_pending == PendingKind.Chip)
                _selectedId = _pendingId;
        }
        else if (_dragHead is { } head)
        {
            if (p.X < PaletteW)
            {
                RemoveIds(_dragSet);
                _selectedId = null;
            }
            else if (_drag == DragKind.Reporter)
            {
                PlugReporter(head, p);
                _selectedId = head;
            }
            else
            {
                AttachStack(head);
                _selectedId = head;
            }
        }

        _pending = PendingKind.None;
        _pendingId = null;
        _pendingSpec = null;
        _drag = DragKind.None;
        _dragHead = null;
        _dragSet = new HashSet<string>();
    }

    // Drops a reporter into the first value box under the pointer that accepts its type.
    private void PlugReporter(string headId, float2 p)
    {
        var head = _graph.Find(headId);
        if (head is null) return;
        var returns = _registry.Get(head.Type)?.Returns ?? "any";

        for (int i = _slots.Count - 1; i >= 0; i--)
        {
            var slot = _slots[i];
            if (!slot.Box.Contains(p) || _dragSet.Contains(slot.NodeId)) continue;
            if (!Accepts(slot.Kind, returns) || _graph.Find(slot.NodeId) is not { } owner) continue;
            owner.Inputs[slot.Input] = new VcInput { Node = headId };
            return;
        }
    }

    // Snaps a dragged stack to the nearest attach point, if one is close enough.
    private void AttachStack(string headId)
    {
        var head = _graph.Find(headId);
        if (head is null || _registry.Get(head.Type)?.Shape == BlockShape.Hat) return;

        float hx = (float)head.X, hy = (float)head.Y;
        Attach? best = null;
        float bestDistance = SnapDistance;
        foreach (var a in _attach)
        {
            if (_dragSet.Contains(a.NodeId)) continue;
            float d = Distance(new float2(a.X, a.Y), new float2(hx, hy));
            if (d < bestDistance)
            {
                bestDistance = d;
                best = a;
            }
        }
        if (best is null) return;

        var target = _graph.Find(best.NodeId);
        if (target is null) return;
        var tail = LastInChain(head);
        switch (best.Relation)
        {
            case "body":
                tail.Next = target.Body;
                target.Body = head.Id;
                break;
            case "else":
                tail.Next = target.ElseBody;
                target.ElseBody = head.Id;
                break;
            default:
                tail.Next = target.Next;
                target.Next = head.Id;
                break;
        }
    }

    private void AddFromPalette(BlockSpec spec)
    {
        var node = NewNode(spec);
        int count = _graph.Nodes.Count;
        node.X = PaletteW + 40 + (count % 6) * 30;
        node.Y = PanelTop + 30 + (count % 10) * 36;
        _graph.Nodes.Add(node);
        _selectedId = node.Id;
    }

    private void AddVariable(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            Status("Type a variable name first.");
            return;
        }
        var name = VisualCode.Transpiler.Code.Identifier(trimmed);
        if (_graph.Variables.Any(v => v.Name == name))
        {
            Status($"'{name}' already exists.");
            return;
        }
        _graph.Variables.Add(new VcVariable { Name = name, Type = "number", Default = "0" });
        _newVar = "";
        Status($"Added variable '{name}'.");
    }

    private void DeleteNode(VcNode node)
    {
        if (FindParent(node.Id).Parent is null)
            RemoveIds(Chain(node.Id));
        else
        {
            Unlink(node, keepRest: true);
            RemoveIds(Subtree(node.Id));
        }
        _selectedId = null;
    }

    // Copies a block, everything plugged into it or inside its mouths, and (for stack blocks) the blocks below it.
    // The copy is a loose stack placed just below and right of the original, and becomes the selection.
    private void DuplicateNode(VcNode node)
    {
        var ids = IsReporter(node) ? Subtree(node.Id) : Chain(node.Id);
        var map = ids.ToDictionary(id => id, _ => "n" + Guid.NewGuid().ToString("N")[..10]);
        string? Remap(string? id) => id is not null && map.TryGetValue(id, out var mapped) ? mapped : null;

        // Nested blocks keep stale X/Y, so place the copy from where the original was drawn last frame.
        var drawn = _blocks.Concat(_chips).FirstOrDefault(h => h.NodeId == node.Id)?.Box
                    ?? new Box((float)node.X, (float)node.Y, 0, 0);

        foreach (var source in _graph.Nodes.Where(n => ids.Contains(n.Id)).ToList())
        {
            var copy = new VcNode
            {
                Id = map[source.Id],
                Type = source.Type,
                X = source.X,
                Y = source.Y,
                Next = Remap(source.Next),
                Body = Remap(source.Body),
                ElseBody = Remap(source.ElseBody),
            };
            foreach (var (name, input) in source.Inputs)
                copy.Inputs[name] = new VcInput { Literal = input.Literal, Node = Remap(input.Node) };
            _graph.Nodes.Add(copy);
        }

        var head = _graph.Find(map[node.Id])!;
        head.X = drawn.X + 24f;
        head.Y = drawn.Y + 24f;
        _selectedId = head.Id;
        Status(ids.Count == 1 ? $"Duplicated {Label(node)}." : $"Duplicated {ids.Count} blocks.");
    }

    // ---- graph helpers ---------------------------------------------------------------

    private bool IsReporter(VcNode node) => _registry.Get(node.Type)?.Shape == BlockShape.Reporter;

    private static bool Accepts(InputKind kind, string returns) => returns switch
    {
        "bool" => kind == InputKind.Bool,
        "number" => kind is InputKind.Number or InputKind.Any,
        _ => kind is InputKind.Number or InputKind.Any or InputKind.Bool,
    };

    private VcNode NewNode(BlockSpec spec)
    {
        var node = new VcNode { Id = "n" + Guid.NewGuid().ToString("N")[..10], Type = spec.Type };
        foreach (var input in spec.Inputs)
        {
            node.Inputs[input.Name] = new VcInput
            {
                Literal = input.Kind == InputKind.Variable ? (_graph.Variables.FirstOrDefault()?.Name ?? "") : input.Default,
            };
        }
        return node;
    }

    private string DefaultFor(string type, string input) =>
        _registry.Get(type)?.Inputs.FirstOrDefault(i => i.Name == input)?.Default ?? "0";

    private (VcNode? Parent, string Relation) FindParent(string id)
    {
        foreach (var n in _graph.Nodes)
        {
            if (n.Next == id) return (n, "next");
            if (n.Body == id) return (n, "body");
            if (n.ElseBody == id) return (n, "else");
            foreach (var (name, v) in n.Inputs)
                if (v.Node == id) return (n, name);
        }
        return (null, "");
    }

    // Removes a node from its parent. keepRest keeps the blocks below it attached to the parent.
    private void Unlink(VcNode node, bool keepRest)
    {
        var (parent, relation) = FindParent(node.Id);
        if (parent is null) return;

        string? rest = keepRest ? node.Next : null;
        switch (relation)
        {
            case "next": parent.Next = rest; break;
            case "body": parent.Body = rest; break;
            case "else": parent.ElseBody = rest; break;
            default: parent.Inputs[relation] = new VcInput { Literal = DefaultFor(parent.Type, relation) }; break;
        }
    }

    private VcNode LastInChain(VcNode head)
    {
        var node = head;
        var seen = new HashSet<string> { head.Id };
        while (node.Next is { } next && seen.Add(next) && _graph.Find(next) is { } n)
            node = n;
        return node;
    }

    // A block, the reporters plugged into it, and everything inside its mouths. Not the blocks below it.
    private HashSet<string> Subtree(string id)
    {
        var set = new HashSet<string>();
        AddSubtree(id, set);
        return set;
    }

    // The block and every block below it in its stack.
    private HashSet<string> Chain(string id)
    {
        var set = new HashSet<string>();
        AddChainSubtree(id, set);
        return set;
    }

    private void AddSubtree(string id, HashSet<string> set)
    {
        if (!set.Add(id) || _graph.Find(id) is not { } node) return;
        foreach (var input in node.Inputs.Values)
            if (input.Node is { } child) AddSubtree(child, set);
        AddChainSubtree(node.Body, set);
        AddChainSubtree(node.ElseBody, set);
    }

    private void AddChainSubtree(string? start, HashSet<string> set)
    {
        for (var id = start; id is not null && !set.Contains(id); id = _graph.Find(id)?.Next)
            AddSubtree(id, set);
    }

    private void RemoveIds(HashSet<string> ids)
    {
        _graph.Nodes.RemoveAll(n => ids.Contains(n.Id));
    }

    private static float Distance(float2 a, float2 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    // ---- files -----------------------------------------------------------------------

    private List<string> ListScripts()
    {
        if (!Directory.Exists(_scriptsFolder)) return new List<string>();
        return Directory.GetFiles(_scriptsFolder, "*.visualcode").OrderBy(f => f).ToList();
    }

    private void Status(string text) => _status = text;

    private void NewScript()
    {
        _graph = new VcGraph();
        _path = null;
        _selectedId = null;
        _name = "Script";
        Status("New script.");
    }

    private void Open(string file)
    {
        try
        {
            _graph = VcGraph.Load(File.ReadAllText(file));
            _path = file;
            _selectedId = null;
            _name = _graph.Name;
            Status($"Opened {Path.GetFileName(file)}.");
        }
        catch (Exception ex)
        {
            Status($"Could not open: {ex.Message}");
        }
    }

    private void Save()
    {
        var name = _name.Trim();
        _graph.Name = name.Length == 0 ? "Script" : name;
        var className = VisualCode.Transpiler.Code.Identifier(_graph.Name);
        var result = new VcTranspiler().Transpile(_graph, className);

        Directory.CreateDirectory(_scriptsFolder);
        _path = Path.Combine(_scriptsFolder, className + ".visualcode");
        File.WriteAllText(_path, _graph.ToJson());
        VisualScriptSync.Sync(_scriptsFolder);
        Status($"Saved {className}.visualcode. {Summary(result)}");
    }

    private void Check()
    {
        var className = VisualCode.Transpiler.Code.Identifier(_graph.Name);
        Status(Summary(new VcTranspiler().Transpile(_graph, className)));
    }

    private static string Summary(TranspileResult result)
    {
        int errors = result.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        int warnings = result.Diagnostics.Count - errors;
        return errors == 0 && warnings == 0 ? "No problems." : $"{errors} error(s), {warnings} warning(s).";
    }
}
