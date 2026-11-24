using System.Globalization;
using System.Numerics;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Editor.Ui;
using Engine.Brutal.Gfx;
using Engine.Brutal.Ui;
using Engine.Core.Assets;
using Engine.Core.Scene;

namespace Editor.Sprites;

public enum SpriteTool { Brush, Eraser, Line, Rectangle, Ellipse, Fill, Picker }

// The sprite editor: paint a picture pixel by pixel with a brush, lines, rectangles, ellipses and a fill bucket, and save it as a
// PNG, JPEG or BMP file in the project, ready to use as a sprite. It replaces the 3D view while open. The picture and what changes it
// are in SpriteCanvas; this draws the tools and the picture, and turns the mouse into strokes.
public sealed class SpriteEditorView(string contentRoot)
{
    private const float PanelWidth = 262f;
    private const float MinZoom = 0.25f, MaxZoom = 64f;
    private const int MaxRecentColors = 10;
    private static readonly string[] FormatNames = ["PNG", "JPEG", "BMP"];
    private static readonly ImageFormat[] Formats = [ImageFormat.Png, ImageFormat.Jpeg, ImageFormat.Bmp];

    private static readonly int[] Palette =
    [
        0x000000, 0xFFFFFF, 0x7F7F7F, 0xC3C3C3, 0x880015, 0xED1C24, 0xFF7F27, 0xFFF200, 0x22B14C, 0xB5E61D,
        0x00A2E8, 0x99D9EA, 0x3F48CC, 0x7092BE, 0xA349A4, 0xC8BFE7, 0xFFAEC9, 0xB97A57, 0xFFC90E, 0xEFE4B0,
    ];

    private SpriteCanvas _canvas = new(32, 32);
    private string? _openPath;                  // the project file being edited (relative, with its extension), or null for a new sprite
    private SpriteTool _tool = SpriteTool.Brush;
    private int _size = 1;
    private bool _round = true;
    private bool _filled;
    private int _tolerance;
    private bool _showGrid = true;
    private Vector4 _color = new(0.93f, 0.11f, 0.14f, 1f);
    private readonly List<Rgba> _recent = new();
    private string _hexText = "", _hexShown = "";

    // The view of the picture.
    private float _zoom = 8f, _originX, _originY;   // _origin is where the picture's top left is, from the view's top left
    private float _drawLeft, _drawTop;                // where the picture's top left was drawn on the screen
    private bool _fit = true;                         // fit the picture to the view on the next frame
    private bool _panning;
    private float _panX, _panY;
    private bool _prevMiddle;

    // The stroke being drawn.
    private bool _stroking;
    private SpriteTool _strokeTool;
    private (int X, int Y) _anchor, _last;

    // What the GPU has of the picture.
    private int _image = -1;
    private int _shownVersion = -1;
    private byte[]? _shownPixels;

    // Saving and the other things the panel keeps.
    private string _name = "Sprites/NewSprite";
    private int _formatIndex;
    private int _quality = 90;
    private string _message = "";
    private bool _messageIsError;
    private string? _confirmOverwrite;
    private int _newWidth = 32, _newHeight = 32;

    private enum Dialog { None, Open, Discard }
    private Dialog _dialog;
    private Action? _afterDiscard;
    private List<string> _files = new();
    private bool _exit;

    // Raised with the project-relative path (forward slashes) after a file is written, so the editor can refresh what shows it.
    public event Action<string>? Saved;

    public bool Dirty => _canvas.Dirty;

    // What Save writes: the file name (a path inside the project, without an extension) and its format.
    public string SaveName
    {
        get => _name;
        set { _name = value; _confirmOverwrite = null; }
    }

    public ImageFormat Format
    {
        get => Formats[_formatIndex];
        set { _formatIndex = Math.Max(0, Array.IndexOf(Formats, value)); _confirmOverwrite = null; }
    }

    // The picture being edited, for checking what it holds.
    public SpriteCanvas Canvas => _canvas;

    public SpriteTool Tool { get => _tool; set => _tool = value; }
    public int BrushSize { get => _size; set => _size = Math.Clamp(value, 1, 64); }
    public bool FillShapes { get => _filled; set => _filled = value; }
    public float Zoom => _zoom;

    // The color the tools paint with.
    public Rgba PaintColor
    {
        get => Paint;
        set => _color = new Vector4(value.R / 255f, value.G / 255f, value.B / 255f, value.A / 255f);
    }

    // Where the middle of a pixel of the picture is on the screen, as last drawn.
    public (float X, float Y) ScreenOfPixel(int x, int y) => (_drawLeft + (x + 0.5f) * _zoom, _drawTop + (y + 0.5f) * _zoom);

    // True once, when the user chose to leave the sprite editor (and any unsaved picture was saved or given up).
    public bool TakeExit()
    {
        bool exit = _exit;
        _exit = false;
        return exit;
    }

    // Starts a new, empty sprite.
    public void NewSprite(int width, int height)
    {
        _canvas = new SpriteCanvas(width, height);
        _openPath = null;
        _newWidth = _canvas.Width;
        _newHeight = _canvas.Height;
        _fit = true;
        _stroking = false;
        _message = "";
    }

    // Opens a picture from the project, asking what to do about unsaved changes to the sprite being edited first.
    public void Edit(string relativePath) => AfterDiscarding(() => Open(relativePath));

    // Opens a picture from the project, given by its path inside it, replacing the sprite being edited.
    private void Open(string relativePath)
    {
        try
        {
            string full = Path.GetFullPath(Path.Combine(contentRoot, relativePath));
            if (!IsInside(full)) throw new InvalidDataException("That file is outside the project.");
            var image = ImageData.Load(full);
            if (image.Pixels is null) throw new InvalidDataException("That picture can't be edited.");
            if (image.Width > SpriteCanvas.MaxSize || image.Height > SpriteCanvas.MaxSize)
                throw new InvalidDataException($"That picture is {image.Width} x {image.Height}; the sprite editor handles up to {SpriteCanvas.MaxSize} x {SpriteCanvas.MaxSize}.");

            _canvas = SpriteCanvas.From(image.Width, image.Height, image.Pixels);
            _openPath = Normalize(Path.GetRelativePath(contentRoot, full));
            _name = Path.ChangeExtension(_openPath, null);
            _formatIndex = Math.Max(0, Array.IndexOf(Formats, ImageEncoder.FromPath(full) ?? ImageFormat.Png));
            _newWidth = _canvas.Width;
            _newHeight = _canvas.Height;
            _fit = true;
            _stroking = false;
            _confirmOverwrite = null;
            Say($"Opened {_openPath}", error: false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Say($"Could not open {relativePath}: {ex.Message}", error: true);
        }
    }

    // Writes the picture to the file named in the panel. Returns false (and says why in the panel) when it could not.
    public bool Save()
    {
        var (relative, full, problem) = ResolveSavePath();
        if (problem is not null)
        {
            Say(problem, error: true);
            return false;
        }

        bool samePicture = _openPath is not null && string.Equals(_openPath, relative, StringComparison.OrdinalIgnoreCase);
        if (File.Exists(full) && !samePicture && _confirmOverwrite != full)
        {
            _confirmOverwrite = full;
            Say($"{relative} already exists. Press Save again to replace it.", error: false);
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var bytes = ImageEncoder.Encode(Formats[_formatIndex], _canvas.Width, _canvas.Height, _canvas.Pixels, _quality);
            string temp = full + ".saving";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, full, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say($"Could not save {relative}: {ex.Message}", error: true);
            return false;
        }

        _canvas.MarkSaved();
        _openPath = relative;
        _confirmOverwrite = null;
        Say($"Saved {relative}", error: false);
        Saved?.Invoke(relative);
        return true;
    }

    // Escape: closes the dialog, or drops the stroke being drawn, or leaves. Returns nothing to do but the call.
    public void Escape()
    {
        if (_dialog != Dialog.None) _dialog = Dialog.None;
        else if (_stroking) CancelStroke();
        else RequestExit();
    }

    // Trackpad pinch: zooms the picture by this fraction.
    public void Pinch(float amount)
    {
        if (amount != 0f) _zoom = Math.Clamp(_zoom * (1f + amount), MinZoom, MaxZoom);
    }

    private void RequestExit()
    {
        if (_canvas.Dirty)
        {
            _afterDiscard = () => _exit = true;
            _dialog = Dialog.Discard;
        }
        else
            _exit = true;
    }

    // ---- drawing ---------------------------------------------------------------------------------

    // Draws the whole editor below `top` (the menu bar). Call every frame while it is open.
    public void Draw(Gui g, IAppWindow window, float top)
    {
        // Fills the gaps around the panels, so the 3D scene underneath doesn't show through them.
        g.C.Rect(0f, top, g.Size.X, g.Size.Y - top, Gui.WindowColor);
        var tools = new Box(6f, top + 6f, PanelWidth, g.Size.Y - top - 12f);
        var view = new Box(PanelWidth + 12f, top + 6f, g.Size.X - PanelWidth - 18f, g.Size.Y - top - 12f);

        SyncPicture(g.C);
        DrawView(g, window, view);
        DrawPanel(g, tools);
        DrawDialog(g);
        HandleKeys(g);
    }

    // Gives the GPU the picture as it is now.
    private void SyncPicture(Canvas2D canvas)
    {
        if (_image < 0)
            _image = canvas.AddImage(_canvas.Pixels, _canvas.Width, _canvas.Height);
        else if (_shownVersion != _canvas.Version || !ReferenceEquals(_shownPixels, _canvas.Pixels))
            canvas.ReplaceImage(_image, _canvas.Pixels, _canvas.Width, _canvas.Height);
        _shownVersion = _canvas.Version;
        _shownPixels = _canvas.Pixels;
    }

    private void DrawView(Gui g, IAppWindow window, Box view)
    {
        var c = g.C;
        g.BeginWindow("SpriteView", view, flags: WindowFlags.NoTitle | WindowFlags.NoScroll | WindowFlags.NoPadding);
        var box = g.Reserve(view.W, view.H);
        bool hovered = g.LastItemHovered;
        c.Rect(box.X, box.Y, box.W, box.H, Theme.Pick(0x1B1B1C, 0xDADADA));

        if (_fit) FitToView(box);

        // Zoom with the wheel, keeping the pixel under the mouse where it is.
        float localX = g.Mouse.X - box.X, localY = g.Mouse.Y - box.Y;
        if (hovered && g.Wheel != 0f && _dialog == Dialog.None)
        {
            float next = Math.Clamp(_zoom * MathF.Pow(1.2f, g.Wheel), MinZoom, MaxZoom);
            float pointX = (localX - _originX) / _zoom, pointY = (localY - _originY) / _zoom;
            _zoom = next;
            _originX = localX - pointX * _zoom;
            _originY = localY - pointY * _zoom;
        }

        // Pan by dragging with the right or middle button.
        bool middle = window.GetMouseButton(GlfwMouseButton.Middle) == GlfwButtonAction.Press;
        bool right = window.GetMouseButton(GlfwMouseButton.Right) == GlfwButtonAction.Press;
        if (!_panning && hovered && !_stroking && _dialog == Dialog.None && (g.RightPressed || (middle && !_prevMiddle)))
        {
            _panning = true;
            _panX = g.Mouse.X;
            _panY = g.Mouse.Y;
        }
        _prevMiddle = middle;
        if (_panning)
        {
            if (right || middle)
            {
                _originX += g.Mouse.X - _panX;
                _originY += g.Mouse.Y - _panY;
                _panX = g.Mouse.X;
                _panY = g.Mouse.Y;
            }
            else
                _panning = false;
        }

        float left = box.X + MathF.Round(_originX), topEdge = box.Y + MathF.Round(_originY);
        float width = _canvas.Width * _zoom, height = _canvas.Height * _zoom;
        (_drawLeft, _drawTop) = (left, topEdge);
        int pixelX = (int)MathF.Floor((g.Mouse.X - left) / _zoom), pixelY = (int)MathF.Floor((g.Mouse.Y - topEdge) / _zoom);

        // The mouse paints.
        bool alt = UiInput.Current.KeyAlt;
        if (hovered && g.MousePressed && !_panning && !_stroking && _dialog == Dialog.None)
            StartStroke(alt ? SpriteTool.Picker : _tool, pixelX, pixelY);
        if (_stroking)
        {
            if (g.MouseDown) ContinueStroke(pixelX, pixelY, g.Shift);
            else EndStroke();
        }

        // The picture over a checkerboard that shows through its clear pixels.
        var visible = Intersect(new Box(left, topEdge, width, height), box);
        DrawChecker(c, visible);
        c.Image(_image, left, topEdge, width, height, new float4(1f, 1f, 1f, 1f), smooth: _zoom < 1f);
        c.Outline(left - 1f, topEdge - 1f, width + 2f, height + 2f, 1f, Gui.BorderColor);
        if (_showGrid && _zoom >= 6f) DrawGrid(c, left, topEdge, visible);

        if (hovered && !_panning && _dialog == Dialog.None && _canvas.Contains(pixelX, pixelY) &&
            (_tool is SpriteTool.Brush or SpriteTool.Eraser) && !alt)
            DrawBrushOutline(c, left, topEdge, pixelX, pixelY, visible);

        // Where the mouse is and how big the picture is.
        string info = $"{_canvas.Width} x {_canvas.Height}   {_zoom * 100f:0}%";
        if (_canvas.Contains(pixelX, pixelY)) info += $"   x {pixelX}, y {pixelY}   {_canvas.Get(pixelX, pixelY)}";
        c.Rect(box.X, box.Y + box.H - 22f, box.W, 22f, new float4(0f, 0f, 0f, 0.45f));
        c.Text(info, box.X + 8f, g.TextTop(box.Y + box.H - 22f, 22f), new float4(1f, 1f, 1f, 0.9f));
        g.EndWindow();
    }

    private void FitToView(Box box)
    {
        float fit = Math.Min((box.W - 48f) / _canvas.Width, (box.H - 72f) / _canvas.Height);
        _zoom = Math.Clamp(fit >= 1f ? MathF.Floor(fit) : fit, MinZoom, MaxZoom);
        _originX = (box.W - _canvas.Width * _zoom) / 2f;
        _originY = (box.H - 22f - _canvas.Height * _zoom) / 2f;
        _fit = false;
    }

    private static Box Intersect(Box a, Box b)
    {
        float x0 = Math.Max(a.X, b.X), y0 = Math.Max(a.Y, b.Y);
        float x1 = Math.Min(a.X + a.W, b.X + b.W), y1 = Math.Min(a.Y + a.H, b.Y + b.H);
        return new Box(x0, y0, Math.Max(0f, x1 - x0), Math.Max(0f, y1 - y0));
    }

    // Light and dark squares, in the part of the picture that shows.
    private static void DrawChecker(Canvas2D c, Box visible)
    {
        if (visible.W <= 0f || visible.H <= 0f) return;
        c.Rect(visible.X, visible.Y, visible.W, visible.H, new float4(0.80f, 0.80f, 0.80f, 1f));
        const float cell = 12f;
        var dark = new float4(0.62f, 0.62f, 0.62f, 1f);
        int firstColumn = (int)MathF.Floor(visible.X / cell), firstRow = (int)MathF.Floor(visible.Y / cell);
        int columns = (int)MathF.Ceiling((visible.X + visible.W) / cell) - firstColumn;
        int rows = (int)MathF.Ceiling((visible.Y + visible.H) / cell) - firstRow;
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
            {
                if (((firstColumn + column + firstRow + row) & 1) == 0) continue;
                var cellBox = Intersect(new Box((firstColumn + column) * cell, (firstRow + row) * cell, cell, cell), visible);
                if (cellBox.W > 0f && cellBox.H > 0f) c.Rect(cellBox.X, cellBox.Y, cellBox.W, cellBox.H, dark);
            }
    }

    // A line between every pixel, once they are big enough to tell apart.
    private void DrawGrid(Canvas2D c, float left, float top, Box visible)
    {
        if (visible.W <= 0f || visible.H <= 0f) return;
        var line = new float4(0f, 0f, 0f, 0.28f);
        int firstX = Math.Max(0, (int)MathF.Floor((visible.X - left) / _zoom)), lastX = Math.Min(_canvas.Width, (int)MathF.Ceiling((visible.X + visible.W - left) / _zoom));
        int firstY = Math.Max(0, (int)MathF.Floor((visible.Y - top) / _zoom)), lastY = Math.Min(_canvas.Height, (int)MathF.Ceiling((visible.Y + visible.H - top) / _zoom));
        for (int x = firstX; x <= lastX; x++)
            c.Rect(left + x * _zoom, visible.Y, 1f, visible.H, line);
        for (int y = firstY; y <= lastY; y++)
            c.Rect(visible.X, top + y * _zoom, visible.W, 1f, line);
    }

    // Shows which pixels the brush will paint where the mouse is.
    private void DrawBrushOutline(Canvas2D c, float left, float top, int x, int y, Box visible)
    {
        var tint = _tool == SpriteTool.Eraser ? new float4(1f, 1f, 1f, 0.35f) : new float4(_color.X, _color.Y, _color.Z, 0.5f);
        var footprint = new SpriteCanvas(Math.Max(_size + 2, 3), Math.Max(_size + 2, 3));
        int middle = footprint.Width / 2;
        footprint.Stamp(middle, middle, _size, _round, new Rgba(255, 255, 255, 255));
        for (int j = 0; j < footprint.Height; j++)
            for (int i = 0; i < footprint.Width; i++)
            {
                if (footprint.Get(i, j).A == 0) continue;
                float px = left + (x + i - middle) * _zoom, py = top + (y + j - middle) * _zoom;
                var cell = Intersect(new Box(px, py, _zoom, _zoom), visible);
                if (cell.W > 0f && cell.H > 0f) c.Rect(cell.X, cell.Y, cell.W, cell.H, tint);
            }
    }

    // ---- strokes -----------------------------------------------------------------------------------

    private Rgba Paint => ToRgba(_color);

    private static Rgba ToRgba(Vector4 c) => new(Channel(c.X), Channel(c.Y), Channel(c.Z), Channel(c.W));

    private static byte Channel(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);

    private void StartStroke(SpriteTool tool, int x, int y)
    {
        _strokeTool = tool;
        _anchor = _last = (x, y);
        switch (tool)
        {
            case SpriteTool.Picker:
                _stroking = true;
                PickAt(x, y);
                break;
            case SpriteTool.Fill:
                if (_canvas.Contains(x, y))
                {
                    _canvas.BeginChange();
                    _canvas.Flood(x, y, Paint, _tolerance);
                    _canvas.EndChange();
                    Remember(Paint);
                }
                break;
            case SpriteTool.Brush or SpriteTool.Eraser:
                _canvas.BeginChange();
                _canvas.Stamp(x, y, _size, _round, tool == SpriteTool.Eraser ? Rgba.Clear : Paint);
                _stroking = true;
                break;
            default:   // line, rectangle, ellipse: dragged out from here
                _canvas.BeginChange();
                DrawShape(tool, x, y, x, y);
                _stroking = true;
                break;
        }
    }

    private void ContinueStroke(int x, int y, bool constrain)
    {
        switch (_strokeTool)
        {
            case SpriteTool.Picker:
                PickAt(x, y);
                break;
            case SpriteTool.Brush or SpriteTool.Eraser:
                if ((x, y) != _last)
                {
                    _canvas.Line(_last.X, _last.Y, x, y, _size, _round, _strokeTool == SpriteTool.Eraser ? Rgba.Clear : Paint);
                    _last = (x, y);
                }
                break;
            default:
                if ((x, y) == _last && !constrain) break;
                _last = (x, y);
                _canvas.RestoreBase();
                var (endX, endY) = constrain ? Constrain(_strokeTool, _anchor, x, y) : (x, y);
                DrawShape(_strokeTool, _anchor.X, _anchor.Y, endX, endY);
                break;
        }
    }

    private void EndStroke()
    {
        _stroking = false;
        if (_strokeTool == SpriteTool.Picker) return;
        _canvas.EndChange();
        if (_strokeTool != SpriteTool.Eraser) Remember(Paint);
    }

    private void CancelStroke()
    {
        _canvas.CancelChange();
        _stroking = false;
    }

    private void DrawShape(SpriteTool tool, int x0, int y0, int x1, int y1)
    {
        switch (tool)
        {
            case SpriteTool.Line: _canvas.Line(x0, y0, x1, y1, _size, _round, Paint); break;
            case SpriteTool.Rectangle: _canvas.Rectangle(x0, y0, x1, y1, _size, _filled, Paint); break;
            case SpriteTool.Ellipse: _canvas.Ellipse(x0, y0, x1, y1, _size, _filled, Paint); break;
        }
    }

    // Shift: a line goes sideways, up and down or at 45 degrees; a rectangle becomes a square and an ellipse a circle.
    private static (int X, int Y) Constrain(SpriteTool tool, (int X, int Y) anchor, int x, int y)
    {
        int dx = x - anchor.X, dy = y - anchor.Y;
        int stepX = dx < 0 ? -1 : 1, stepY = dy < 0 ? -1 : 1;
        int ax = Math.Abs(dx), ay = Math.Abs(dy);
        if (tool == SpriteTool.Line)
        {
            if (ax > 2 * ay) return (x, anchor.Y);
            if (ay > 2 * ax) return (anchor.X, y);
        }
        int m = Math.Max(ax, ay);
        return (anchor.X + stepX * m, anchor.Y + stepY * m);
    }

    private void PickAt(int x, int y)
    {
        if (!_canvas.Contains(x, y)) return;
        var p = _canvas.Get(x, y);
        _color = new Vector4(p.R / 255f, p.G / 255f, p.B / 255f, p.A / 255f);
    }

    private void Remember(Rgba color)
    {
        _recent.Remove(color);
        _recent.Insert(0, color);
        if (_recent.Count > MaxRecentColors) _recent.RemoveAt(_recent.Count - 1);
    }

    // ---- the panel ---------------------------------------------------------------------------------

    private void DrawPanel(Gui g, Box rect)
    {
        g.BeginWindow("SpriteTools", rect, "Sprite Editor");

        if (g.Button("< Back to the scene", width: -1f))
            RequestExit();
        g.Spacing();

        // Tools, two to a row.
        (SpriteTool Tool, string Label)[] tools =
        [
            (SpriteTool.Brush, "Brush (B)"), (SpriteTool.Eraser, "Eraser (E)"),
            (SpriteTool.Line, "Line (L)"), (SpriteTool.Rectangle, "Rectangle (R)"),
            (SpriteTool.Ellipse, "Ellipse (O)"), (SpriteTool.Fill, "Fill (G)"),
            (SpriteTool.Picker, "Picker (I)"),
        ];
        float half = (g.AvailWidth - 6f) / 2f;
        for (int i = 0; i < tools.Length; i++)
        {
            if (i % 2 == 1) g.SameLine();
            if (g.Button(tools[i].Label, half, 0f, highlighted: _tool == tools[i].Tool))
                _tool = tools[i].Tool;
        }
        g.Spacing();

        g.DragInt("Size", ref _size, 0.2f, 1, 64);
        g.Checkbox("Round", ref _round);
        g.Checkbox("Fill shapes", ref _filled);
        if (_tool == SpriteTool.Fill)
            g.DragInt("Tolerance", ref _tolerance, 0.5f, 0, 255);
        g.TextWrapped(ToolHint(), Gui.DimColor);
        g.Spacing();

        // Undo and the view of the picture.
        float quarter = (g.AvailWidth - 18f) / 4f;
        if (g.Button("Undo", quarter, enabled: _canvas.CanUndo))
            Undo();
        g.SameLine();
        if (g.Button("Redo", quarter, enabled: _canvas.CanRedo))
            Redo();
        g.SameLine();
        if (g.Button("Fit", quarter))
            _fit = true;
        g.SameLine();
        if (g.Button("1:1", quarter))
        {
            _zoom = 1f;
            _fit = false;
        }
        g.TextDisabled("Scroll to zoom, right-drag to pan.");
        g.Separator();

        if (g.CollapsingHeader("Color", defaultOpen: true))
            DrawColors(g);
        if (g.CollapsingHeader("File", defaultOpen: true))
            DrawFileSection(g);
        if (g.CollapsingHeader("Canvas"))
            DrawCanvasSection(g);
        g.EndWindow();
    }

    private void DrawCanvasSection(Gui g)
    {
        g.DragInt("Width", ref _newWidth, 0.5f, 1, SpriteCanvas.MaxSize);
        g.DragInt("Height", ref _newHeight, 0.5f, 1, SpriteCanvas.MaxSize);
        int[] presets = [16, 32, 64, 128, 256];
        float presetWidth = (g.AvailWidth - 6f * (presets.Length - 1)) / presets.Length;
        for (int i = 0; i < presets.Length; i++)
        {
            if (i > 0) g.SameLine();
            if (g.Button(presets[i].ToString(CultureInfo.InvariantCulture), presetWidth))
                (_newWidth, _newHeight) = (presets[i], presets[i]);
        }
        float third = (g.AvailWidth - 12f) / 3f;
        if (g.Button("Resize", third))
        {
            _canvas.Resize(_newWidth, _newHeight);
            _fit = true;
        }
        g.SameLine();
        if (g.Button("New", third))
            AfterDiscarding(() => NewSprite(_newWidth, _newHeight));
        g.SameLine();
        if (g.Button("Clear", third))
            _canvas.Fill(Rgba.Clear);
        g.TextWrapped("Resize keeps the picture from the top left. New starts empty.", Gui.DimColor);
        g.Checkbox("Show grid", ref _showGrid);
    }

    private string ToolHint() => _tool switch
    {
        SpriteTool.Brush => "Drag to paint. Hold Alt to pick a color from the picture.",
        SpriteTool.Eraser => "Drag to make pixels clear.",
        SpriteTool.Line => "Drag a line. Hold Shift for a straight or 45 degree one. Size is its thickness.",
        SpriteTool.Rectangle => "Drag a rectangle. Hold Shift for a square. Size is the outline's thickness.",
        SpriteTool.Ellipse => "Drag an ellipse. Hold Shift for a circle. Size is the outline's thickness.",
        SpriteTool.Fill => "Click to fill the area of alike pixels. Tolerance lets slightly different ones count as alike.",
        _ => "Click to take a color from the picture.",
    };

    private void DrawColors(Gui g)
    {
        g.ColorEdit("##color", ref _color);

        string hex = _color.W >= 0.999f ? Paint.ToString()[..7] : Paint.ToString();
        if (hex != _hexShown)
        {
            _hexText = hex;
            _hexShown = hex;
        }
        if (g.InputText("Hex", ref _hexText, 9) && TryParseHex(_hexText, out var parsed))
        {
            _color = new Vector4(parsed.R / 255f, parsed.G / 255f, parsed.B / 255f, parsed.A / 255f);
            _hexShown = _hexText;
        }

        var swatches = Palette.Select(rgb => new Rgba((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255)).ToList();
        DrawSwatches(g, "palette", swatches);
        if (_recent.Count > 0)
        {
            g.TextDisabled("Recent");
            DrawSwatches(g, "recent", _recent);
        }
    }

    private void DrawSwatches(Gui g, string id, IReadOnlyList<Rgba> colors)
    {
        const float size = 22f, gap = 4f;
        int perRow = Math.Max(1, (int)((g.AvailWidth + gap) / (size + gap)));
        g.PushId(id);
        for (int i = 0; i < colors.Count; i++)
        {
            if (i % perRow != 0) g.SameLine();
            var box = g.Reserve(size, size);
            bool clicked = g.Behavior($"swatch{i}", box, out bool hover, out _);
            var color = colors[i];
            g.C.Rect(box.X, box.Y, box.W, box.H, new float4(color.R / 255f, color.G / 255f, color.B / 255f, 1f));
            g.C.Outline(box.X, box.Y, box.W, box.H, hover ? 2f : 1f, hover ? Gui.AccentHover : Gui.BorderColor);
            if (clicked) _color = new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
        }
        g.PopId();
    }

    private static bool TryParseHex(string text, out Rgba color)
    {
        color = default;
        text = text.Trim().TrimStart('#');
        if (text.Length is not (6 or 8) || !uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
            return false;
        color = text.Length == 6
            ? new Rgba((byte)(value >> 16), (byte)(value >> 8), (byte)value, 255)
            : new Rgba((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    private void DrawFileSection(Gui g)
    {
        g.TextDisabled(_openPath is null ? "A new sprite, not saved yet" : "Editing " + SafeLabel.Of(_openPath, 60));
        if (g.InputText("Save as", ref _name, 200))
            _confirmOverwrite = null;
        if (g.Combo("Format", _formatIndex, FormatNames, out int picked))
        {
            _formatIndex = picked;
            _confirmOverwrite = null;
        }
        if (Formats[_formatIndex] == ImageFormat.Jpeg)
        {
            g.DragInt("Quality", ref _quality, 0.5f, 1, 100);
            g.TextWrapped("JPEG can't be see-through: clear pixels become white.", Gui.DimColor);
        }
        g.TextWrapped($"Saved in the project as {SafeLabel.Of(SavePathPreview(), 80)}", Gui.DimColor);

        float half = (g.AvailWidth - 6f) / 2f;
        if (g.Button(_canvas.Dirty ? "Save *" : "Save", half, highlighted: true))
            Save();
        g.SameLine();
        if (g.Button("Open...", half))
        {
            _files = ListPictures();
            _dialog = Dialog.Open;
        }
        if (_message.Length > 0)
            g.TextWrapped(_message, _messageIsError ? Gui.ErrorColor : Gui.DimColor);
    }

    // ---- dialogs -----------------------------------------------------------------------------------

    private void DrawDialog(Gui g)
    {
        if (_dialog == Dialog.Open)
        {
            var rect = new Box(g.Size.X / 2f - 230f, g.Size.Y / 2f - 220f, 460f, 440f);
            bool open = g.BeginWindow("OpenSprite", rect, "Open a picture from the project", WindowFlags.Modal | WindowFlags.Closable | WindowFlags.NoScroll);
            if (_files.Count == 0)
                g.TextWrapped("There are no PNG, JPEG, BMP or GIF pictures in this project yet.");
            else
            {
                g.BeginChild("files", 0f, 0f);
                for (int i = 0; i < _files.Count; i++)
                    if (g.Selectable($"{SafeLabel.Of(_files[i], 90)}##{i}", string.Equals(_files[i], _openPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        string chosen = _files[i];
                        _dialog = Dialog.None;
                        AfterDiscarding(() => Open(chosen));
                    }
                g.EndChild();
            }
            g.EndWindow();
            if (!open && _dialog == Dialog.Open) _dialog = Dialog.None;
        }
        else if (_dialog == Dialog.Discard)
        {
            var rect = new Box(g.Size.X / 2f - 200f, g.Size.Y / 2f - 70f, 400f, 124f);
            g.BeginWindow("DiscardSprite", rect, "Unsaved Sprite", WindowFlags.Modal | WindowFlags.NoScroll);
            g.TextWrapped("The sprite has changes that are not saved.");
            g.Spacing();
            if (g.Button("Save", highlighted: true))
            {
                var next = _afterDiscard;
                _dialog = Dialog.None;
                _afterDiscard = null;
                if (Save()) next?.Invoke();
            }
            g.SameLine();
            if (g.Button("Discard Changes"))
            {
                var next = _afterDiscard;
                _dialog = Dialog.None;
                _afterDiscard = null;
                next?.Invoke();
            }
            g.SameLine();
            if (g.Button("Cancel"))
            {
                _dialog = Dialog.None;
                _afterDiscard = null;
            }
            g.EndWindow();
        }
    }

    // Does this now when nothing is unsaved, and otherwise after the user chooses what to do about the changes.
    private void AfterDiscarding(Action action)
    {
        if (_canvas.Dirty)
        {
            _afterDiscard = action;
            _dialog = Dialog.Discard;
        }
        else
            action();
    }

    // ---- keys --------------------------------------------------------------------------------------

    private void HandleKeys(Gui g)
    {
        if (g.IsTyping || _dialog != Dialog.None) return;
        var input = UiInput.Current;
        bool Pressed(ImGuiKey key) => input.IsKeyPressed(key, false);

        if (g.Command)
        {
            if (Pressed(ImGuiKey.Z)) { if (g.Shift) Redo(); else Undo(); }
            else if (Pressed(ImGuiKey.Y)) Redo();
            return;
        }
        if (_stroking) return;

        if (Pressed(ImGuiKey.B)) _tool = SpriteTool.Brush;
        else if (Pressed(ImGuiKey.E)) _tool = SpriteTool.Eraser;
        else if (Pressed(ImGuiKey.L)) _tool = SpriteTool.Line;
        else if (Pressed(ImGuiKey.R)) _tool = SpriteTool.Rectangle;
        else if (Pressed(ImGuiKey.O)) _tool = SpriteTool.Ellipse;
        else if (Pressed(ImGuiKey.G)) _tool = SpriteTool.Fill;
        else if (Pressed(ImGuiKey.I)) _tool = SpriteTool.Picker;
        else if (Pressed(ImGuiKey.LeftBracket)) _size = Math.Max(1, _size - 1);
        else if (Pressed(ImGuiKey.RightBracket)) _size = Math.Min(64, _size + 1);
    }

    private void Undo()
    {
        if (_stroking) CancelStroke();
        _canvas.Undo();
        _newWidth = _canvas.Width;
        _newHeight = _canvas.Height;
    }

    private void Redo()
    {
        if (_stroking) CancelStroke();
        _canvas.Redo();
        _newWidth = _canvas.Width;
        _newHeight = _canvas.Height;
    }

    // ---- files -------------------------------------------------------------------------------------

    private void Say(string message, bool error)
    {
        _message = message;
        _messageIsError = error;
    }

    private string Extension => ImageEncoder.Extension(Formats[_formatIndex]);

    // The name typed in the panel, without a picture extension, as a path inside the project with forward slashes.
    private string TypedName()
    {
        string name = Normalize(_name.Trim());
        if (ImageEncoder.FromPath(name) is not null || name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            name = Path.ChangeExtension(name, null);
        return name.Trim('/');
    }

    private string SavePathPreview() => TypedName().Length == 0 ? "(type a name)" : TypedName() + Extension;

    // Where Save writes: the project-relative path, the full path, and why it can't be used (null when it can).
    private (string Relative, string Full, string? Problem) ResolveSavePath()
    {
        string name = TypedName();
        string relative = name + Extension;
        if (name.Length == 0)
            return (relative, "", "Type a name to save the sprite as, such as Sprites/Hero.");
        string typed = Normalize(_name.Trim());
        if (typed.StartsWith('/') || Path.IsPathRooted(typed) || name.Split('/').Any(part => part is "." or ".." || part.Length == 0))
            return (relative, "", "The name must be a path inside the project, such as Sprites/Hero.");
        if (name.Split('/').Any(part => part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            return (relative, "", "The name has a character a file name can't have.");

        string full = Path.GetFullPath(Path.Combine(contentRoot, relative));
        if (!IsInside(full))
            return (relative, full, "The name must be a path inside the project.");
        return (relative, full, null);
    }

    private bool IsInside(string full)
    {
        string root = Path.GetFullPath(contentRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    // Every picture in the project the editor can open, by project-relative path.
    private List<string> ListPictures()
    {
        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(contentRoot));
        while (pending.Count > 0 && found.Count < 1000)
        {
            string folder = pending.Pop();
            try
            {
                foreach (string file in Directory.EnumerateFiles(folder))
                    if (SpriteRenderer.IsSpriteImage(file))
                        found.Add(Normalize(Path.GetRelativePath(contentRoot, file)));
                foreach (string sub in Directory.EnumerateDirectories(folder))
                {
                    string name = Path.GetFileName(sub);
                    if (!name.StartsWith('.') && name is not ("bin" or "obj" or "node_modules"))
                        pending.Push(sub);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a folder that can't be read has no pictures to offer
            }
        }
        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }
}
