namespace Editor.Sprites;

// One pixel, 8 bits a channel, straight (not premultiplied) alpha.
public readonly record struct Rgba(byte R, byte G, byte B, byte A)
{
    public static readonly Rgba Clear = new(0, 0, 0, 0);

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}{A:X2}";
}

// The picture being drawn in the sprite editor and everything that changes it: the brush, lines, rectangles, ellipses, the fill
// bucket and undo. It knows nothing about the screen, so it can be tested on its own. Drawing replaces pixels (a half-see-through color
// makes half-see-through pixels, and the eraser makes them clear), as pixel-art editors do.
public sealed class SpriteCanvas
{
    public const int MaxSize = 1024;
    private const int MaxUndoSteps = 100;
    private const long MaxUndoBytes = 160L * 1024 * 1024;

    private readonly List<Snapshot> _undo = new();   // oldest first
    private readonly List<Snapshot> _redo = new();
    private Snapshot? _pending;                      // the picture as it was when the current change began
    private int _savedVersion;

    private sealed record Snapshot(int Width, int Height, byte[] Pixels);

    public int Width { get; private set; }
    public int Height { get; private set; }

    // Width * Height * 4 bytes, row after row from the top. The array is replaced when the size changes.
    public byte[] Pixels { get; private set; }

    // Goes up with every change, so a view can tell when it must redraw.
    public int Version { get; private set; }

    public bool Dirty => Version != _savedVersion;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool Changing => _pending is not null;

    public SpriteCanvas(int width, int height)
    {
        Width = Math.Clamp(width, 1, MaxSize);
        Height = Math.Clamp(height, 1, MaxSize);
        Pixels = new byte[Width * Height * 4];
    }

    // A canvas holding a copy of this picture, which is clean (nothing to save).
    public static SpriteCanvas From(int width, int height, byte[] rgba)
    {
        if (width < 1 || height < 1 || width > MaxSize || height > MaxSize)
            throw new ArgumentException($"A sprite can be at most {MaxSize} x {MaxSize} pixels.");
        if (rgba.Length < width * height * 4)
            throw new ArgumentException("The picture's pixels do not match its size.");
        var canvas = new SpriteCanvas(width, height);
        Array.Copy(rgba, canvas.Pixels, width * height * 4);
        return canvas;
    }

    public void MarkSaved() => _savedVersion = Version;

    public bool Contains(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    public Rgba Get(int x, int y)
    {
        if (!Contains(x, y)) return Rgba.Clear;
        int i = (y * Width + x) * 4;
        return new Rgba(Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
    }

    public void Set(int x, int y, Rgba color)
    {
        if (!Contains(x, y)) return;
        int i = (y * Width + x) * 4;
        Pixels[i] = color.R;
        Pixels[i + 1] = color.G;
        Pixels[i + 2] = color.B;
        Pixels[i + 3] = color.A;
    }

    // ---- undo --------------------------------------------------------------------------------------

    // Call before a stroke or a one-off change, and EndChange after it. Everything done in between is one undo step (or none, when
    // nothing changed). Between them RestoreBase puts the picture back as it was, for a shape that is still being dragged out.
    public void BeginChange()
    {
        _pending ??= new Snapshot(Width, Height, (byte[])Pixels.Clone());
    }

    public void EndChange()
    {
        if (_pending is not { } before) return;
        _pending = null;
        if (before.Width == Width && before.Height == Height && before.Pixels.AsSpan().SequenceEqual(Pixels))
            return;
        _undo.Add(before);
        _redo.Clear();
        Trim();
        Version++;
    }

    // Puts the picture back to how it was at BeginChange, keeping the change open.
    public void RestoreBase()
    {
        if (_pending is not { } before) return;
        if (before.Width != Width || before.Height != Height)
        {
            Width = before.Width;
            Height = before.Height;
            Pixels = new byte[Width * Height * 4];
        }
        Array.Copy(before.Pixels, Pixels, Pixels.Length);
        Version++;
    }

    // Drops the change in progress as if it never happened.
    public void CancelChange()
    {
        if (_pending is null) return;
        RestoreBase();
        _pending = null;
    }

    public bool Undo()
    {
        EndChange();
        if (_undo.Count == 0) return false;
        _redo.Add(Take());
        Apply(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        return true;
    }

    public bool Redo()
    {
        EndChange();
        if (_redo.Count == 0) return false;
        _undo.Add(Take());
        Apply(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        return true;
    }

    private Snapshot Take() => new(Width, Height, (byte[])Pixels.Clone());

    private void Apply(Snapshot snapshot)
    {
        Width = snapshot.Width;
        Height = snapshot.Height;
        Pixels = (byte[])snapshot.Pixels.Clone();
        Version++;
    }

    // Forgets the oldest steps when there are too many, or they take too much memory.
    private void Trim()
    {
        long bytes = _undo.Sum(s => (long)s.Pixels.Length);
        while (_undo.Count > MaxUndoSteps || (_undo.Count > 1 && bytes > MaxUndoBytes))
        {
            bytes -= _undo[0].Pixels.Length;
            _undo.RemoveAt(0);
        }
    }

    // ---- whole picture -----------------------------------------------------------------------------

    // Clears the whole picture to one color (clear, usually).
    public void Fill(Rgba color)
    {
        BeginChange();
        for (int i = 0; i < Pixels.Length; i += 4)
        {
            Pixels[i] = color.R;
            Pixels[i + 1] = color.G;
            Pixels[i + 2] = color.B;
            Pixels[i + 3] = color.A;
        }
        Version++;
        EndChange();
    }

    // Changes the size. What fits of the old picture stays where it was (from the top left), and new room is clear.
    public void Resize(int width, int height)
    {
        width = Math.Clamp(width, 1, MaxSize);
        height = Math.Clamp(height, 1, MaxSize);
        if (width == Width && height == Height) return;
        BeginChange();
        var pixels = new byte[width * height * 4];
        int copyWidth = Math.Min(width, Width), copyHeight = Math.Min(height, Height);
        for (int y = 0; y < copyHeight; y++)
            Buffer.BlockCopy(Pixels, y * Width * 4, pixels, y * width * 4, copyWidth * 4);
        Width = width;
        Height = height;
        Pixels = pixels;
        Version++;
        EndChange();
    }

    // ---- brush -------------------------------------------------------------------------------------

    // Stamps the brush with its middle on (x, y): a size x size square, or a disk of that size. A size of 1 or 2 is always square.
    public void Stamp(int x, int y, int size, bool round, Rgba color)
    {
        size = Math.Clamp(size, 1, 256);
        int left = x - size / 2, top = y - size / 2;
        double middle = (size - 1) / 2.0, radius = size / 2.0, limit = radius * radius - 0.5;
        for (int j = 0; j < size; j++)
            for (int i = 0; i < size; i++)
            {
                if (round && size > 2)
                {
                    double dx = i - middle, dy = j - middle;
                    if (dx * dx + dy * dy > limit) continue;
                }
                Set(left + i, top + j, color);
            }
        Version++;
    }

    // Stamps the brush along the line from one pixel to another, so a quick stroke leaves no gaps.
    public void Line(int x0, int y0, int x1, int y1, int size, bool round, Rgba color)
    {
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
        int stepX = x0 < x1 ? 1 : -1, stepY = y0 < y1 ? 1 : -1;
        int error = dx + dy;
        while (true)
        {
            Stamp(x0, y0, size, round, color);
            if (x0 == x1 && y0 == y1) break;
            int doubled = 2 * error;
            if (doubled >= dy) { error += dy; x0 += stepX; }
            if (doubled <= dx) { error += dx; y0 += stepY; }
        }
    }

    // ---- shapes ------------------------------------------------------------------------------------

    // A rectangle between two corners (both included). Outlined, it is `thickness` pixels thick, inside the corners.
    public void Rectangle(int x0, int y0, int x1, int y1, int thickness, bool filled, Rgba color)
    {
        (x0, x1) = (Math.Min(x0, x1), Math.Max(x0, x1));
        (y0, y1) = (Math.Min(y0, y1), Math.Max(y0, y1));
        thickness = Math.Max(1, thickness);
        for (int y = Math.Max(y0, 0); y <= Math.Min(y1, Height - 1); y++)
            for (int x = Math.Max(x0, 0); x <= Math.Min(x1, Width - 1); x++)
            {
                bool edge = x < x0 + thickness || x > x1 - thickness || y < y0 + thickness || y > y1 - thickness;
                if (filled || edge) Set(x, y, color);
            }
        Version++;
    }

    // An ellipse that fills the rectangle between two corners (both included). Outlined, it is about `thickness` pixels thick.
    public void Ellipse(int x0, int y0, int x1, int y1, int thickness, bool filled, Rgba color)
    {
        (x0, x1) = (Math.Min(x0, x1), Math.Max(x0, x1));
        (y0, y1) = (Math.Min(y0, y1), Math.Max(y0, y1));
        thickness = Math.Max(1, thickness);
        double a = (x1 - x0 + 1) / 2.0, b = (y1 - y0 + 1) / 2.0;
        double centerX = x0 + a, centerY = y0 + b;
        double innerA = a - thickness, innerB = b - thickness;
        bool hollow = !filled && innerA > 0 && innerB > 0;
        for (int y = Math.Max(y0, 0); y <= Math.Min(y1, Height - 1); y++)
            for (int x = Math.Max(x0, 0); x <= Math.Min(x1, Width - 1); x++)
            {
                double dx = x + 0.5 - centerX, dy = y + 0.5 - centerY;
                if (dx * dx / (a * a) + dy * dy / (b * b) > 1.0) continue;
                if (hollow && dx * dx / (innerA * innerA) + dy * dy / (innerB * innerB) <= 1.0) continue;
                Set(x, y, color);
            }
        Version++;
    }

    // ---- fill bucket -------------------------------------------------------------------------------

    // Paints the area around (x, y) whose pixels are alike: every channel within `tolerance` (0 to 255) of the pixel clicked.
    // Returns how many pixels were painted.
    public int Flood(int x, int y, Rgba color, int tolerance = 0)
    {
        if (!Contains(x, y)) return 0;
        var target = Get(x, y);
        tolerance = Math.Clamp(tolerance, 0, 255);
        if (target == color) return 0;

        bool Matches(int px, int py)
        {
            int i = (py * Width + px) * 4;
            return Math.Abs(Pixels[i] - target.R) <= tolerance && Math.Abs(Pixels[i + 1] - target.G) <= tolerance &&
                   Math.Abs(Pixels[i + 2] - target.B) <= tolerance && Math.Abs(Pixels[i + 3] - target.A) <= tolerance;
        }

        var seen = new bool[Width * Height];
        var stack = new Stack<(int X, int Y)>();
        stack.Push((x, y));
        int painted = 0;
        while (stack.Count > 0)
        {
            var (sx, sy) = stack.Pop();
            if (seen[sy * Width + sx] || !Matches(sx, sy)) continue;

            // Paint the whole run of matching pixels along this row, then look above and below it.
            int left = sx, right = sx;
            while (left > 0 && !seen[sy * Width + left - 1] && Matches(left - 1, sy)) left--;
            while (right < Width - 1 && !seen[sy * Width + right + 1] && Matches(right + 1, sy)) right++;
            for (int px = left; px <= right; px++)
            {
                seen[sy * Width + px] = true;
                Set(px, sy, color);
                painted++;
                if (sy > 0 && !seen[(sy - 1) * Width + px]) stack.Push((px, sy - 1));
                if (sy < Height - 1 && !seen[(sy + 1) * Width + px]) stack.Push((px, sy + 1));
            }
        }
        Version++;
        return painted;
    }
}
