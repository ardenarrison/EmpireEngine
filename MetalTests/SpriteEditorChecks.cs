using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Editor.Sprites;
using Editor.Ui;
using Engine.Brutal.Gfx;
using Engine.Brutal.Metal;
using Engine.Brutal.Ui;
using Engine.Core.Assets;

// Draws the sprite editor off screen with a fake window and mouse, paints with it, and checks both the picture it holds and the pixels
// that reach the screen. Nothing here touches the real mouse or any real window.
static class SpriteEditorChecks
{
    private sealed class FakeWindow : IAppWindow
    {
        public double2 Cursor;
        public bool Left, Right;
        public IEnumerable<string> InstanceExtensions => [];
        public ulong CreateSurface(Instance instance) => 0;
        public int2 Size => new(1280, 800);
        public int2 FramebufferSize => new(1280, 800);
        public float ContentScale => 1f;
        public bool ShouldClose { get; set; }
        public string Title { get; set; } = "";
        public double2 CursorPos => Cursor;
        public void PollEvents() { }
        public GlfwKeyAction GetKey(GlfwKey key) => GlfwKeyAction.Release;
        public GlfwButtonAction GetMouseButton(GlfwMouseButton button) =>
            (button == GlfwMouseButton.Left && Left) || (button == GlfwMouseButton.Right && Right) ? GlfwButtonAction.Press : GlfwButtonAction.Release;
        public void SetInputMode(GlfwInputMode mode, int value) { }
        public event Action<double>? PinchZoom { add { } remove { } }
    }

    private sealed class FakeInput : IUiInput
    {
        public float Wheel;
        public bool Ctrl, Alt, Shift;
        public readonly HashSet<ImGuiKey> Pressed = new();
        public float2 DisplaySize => new(1280, 800);
        public float2 DisplayFramebufferScale => new(1, 1);
        public float MouseWheel => Wheel;
        public float MouseWheelH => 0f;
        public bool KeyCtrl => Ctrl;
        public bool KeyShift => Shift;
        public bool KeyAlt => Alt;
        public bool KeySuper => false;
        public IReadOnlyList<uint> InputQueueCharacters => [];
        public bool WantsMouse => false;
        public void BeginFrame() { }
        public void Render(UiPass pass) { }
        public bool IsKeyPressed(ImGuiKey key, bool repeat = true) => Pressed.Contains(key);
        public bool IsMouseClicked(ImGuiMouseButton button) => false;
        public bool IsMouseDoubleClicked(ImGuiMouseButton button) => false;
        public string GetClipboardText() => "";
        public void SetClipboardText(string text) { }
        public void Dispose() { }
    }

    public static void Run(ShaderLibrary shaders, Action<bool, string> check)
    {
        const int W = 1280, H = 800;
        using var ctx = new MetalContext(W, H);   // a window-sized picture of its own: the editor needs room
        string project = Path.Combine(Path.GetTempPath(), $"empire-sprite-editor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(project);
        try
        {
            using var canvas = new Canvas2D(ctx, shaders, Canvas2D.DefaultFont(), 14f);
            var window = new FakeWindow();
            var input = new FakeInput();
            UiInput.Current = input;
            var gui = new Gui();
            var view = new SpriteEditorView(project);
            var saved = new List<string>();
            view.Saved += saved.Add;
            double now = 0;

            byte[] Frame(float x, float y, bool down, bool right = false, float wheel = 0f)
            {
                window.Cursor = new double2(x, y);
                window.Left = down;
                window.Right = right;
                input.Wheel = wheel;
                gui.Begin(window, canvas, new float2(W, H), now += 0.016);
                view.Draw(gui, window, Gui.MenuBarHeight);
                gui.End();
                ctx.BeginFrame();
                ctx.EndFrame(pass => canvas.Render(pass, W, H, new float2(1f, 1f)));
                input.Pressed.Clear();
                return ctx.ReadPixels();
            }
            (int R, int G, int B) Pixel(byte[] p, float x, float y)
            {
                int i = ((int)y * W + (int)x) * 4;
                return (p[i + 2], p[i + 1], p[i]);   // blue, green, red, alpha
            }
            (float X, float Y) At(int px, int py) => view.ScreenOfPixel(px, py);
            // A whole stroke: press on one pixel, drag through the others, let go.
            byte[] Drag(params (int X, int Y)[] points)
            {
                var (x0, y0) = At(points[0].X, points[0].Y);
                Frame(x0, y0, false);
                byte[] shot = Frame(x0, y0, true);
                foreach (var point in points.Skip(1))
                {
                    var (x, y) = At(point.X, point.Y);
                    shot = Frame(x, y, true);
                }
                var (xe, ye) = At(points[^1].X, points[^1].Y);
                return Frame(xe, ye, false);
            }

            view.NewSprite(16, 16);
            Frame(5f, 5f, false);
            Frame(5f, 5f, false);
            float zoom = view.Zoom;
            check(zoom >= 8f && zoom == MathF.Floor(zoom), $"a new 16 x 16 sprite is fitted to the view at a whole zoom ({zoom}x)");

            // The brush.
            view.PaintColor = new Rgba(237, 28, 36, 255);
            var shot = Drag((3, 3), (8, 3));
            check(Enumerable.Range(3, 6).All(x => view.Canvas.Get(x, 3) == new Rgba(237, 28, 36, 255)) && view.Canvas.Get(3, 4).A == 0 && view.Canvas.Get(9, 3).A == 0,
                "dragging the brush paints a line of pixels, and only those");
            check(view.Dirty && view.Canvas.CanUndo, "a stroke makes the sprite unsaved, with something to undo");
            var (rx, ry) = At(5, 3);
            var red = Pixel(shot, rx, ry);
            check(red.R > 200 && red.G < 60 && red.B < 70, $"the stroke shows on the screen {red}");
            var (cx, cy) = At(5, 6);
            var empty = Pixel(shot, cx, cy);
            check(Math.Abs(empty.R - empty.G) < 8 && Math.Abs(empty.G - empty.B) < 8 && empty.R > 130, $"a clear pixel shows the checkerboard {empty}");
            // Sharp blocks, not smoothed: just inside either edge of a pixel is its own color, and just outside is the neighbor's.
            var (sx, _) = At(3, 3);
            float half = view.Zoom / 2f;
            var inside = Pixel(shot, sx - half + 3f, ry);
            var outside = Pixel(shot, sx - half - 3f, ry);
            check(inside.R > 200 && inside.G < 60 && !(outside.R > 200 && outside.G < 60), $"pixels are sharp blocks, not blurred {inside} next to {outside}");

            // The eraser.
            view.Tool = SpriteTool.Eraser;
            Drag((4, 3), (5, 3));
            check(view.Canvas.Get(3, 3).A > 0 && view.Canvas.Get(4, 3).A == 0 && view.Canvas.Get(5, 3).A == 0 && view.Canvas.Get(6, 3).A > 0, "the eraser makes pixels clear");

            // A rectangle follows the mouse until it is let go.
            view.Tool = SpriteTool.Rectangle;
            view.PaintColor = new Rgba(0, 162, 232, 255);
            var (ax, ay) = At(10, 10);
            Frame(ax, ay, false);
            Frame(ax, ay, true);
            var (bx, by) = At(14, 13);
            Frame(bx, by, true);
            check(view.Canvas.Get(10, 10).A > 0 && view.Canvas.Get(14, 13).A > 0 && view.Canvas.Get(12, 11).A == 0, "a rectangle is drawn while the mouse is still down, outlined");
            var (mx, my) = At(12, 12);
            Frame(mx, my, true);
            check(view.Canvas.Get(14, 13).A == 0 && view.Canvas.Get(12, 12).A > 0, "and follows the mouse back, leaving nothing behind");
            Frame(mx, my, false);
            check(view.Canvas.CanUndo && view.Canvas.Get(10, 12).A > 0 && view.Canvas.Get(11, 11).A == 0, "letting go finishes it");

            // An ellipse, filled, then the fill bucket and undo.
            view.Tool = SpriteTool.Ellipse;
            view.FillShapes = true;
            view.PaintColor = new Rgba(34, 177, 76, 255);
            Drag((0, 8), (5, 14));
            check(view.Canvas.Get(2, 11).A > 0 && view.Canvas.Get(0, 8).A == 0, "a filled ellipse is round");
            view.Tool = SpriteTool.Fill;
            view.PaintColor = new Rgba(255, 201, 14, 255);
            int before = view.Canvas.Pixels.Where((_, i) => i % 4 == 3 && view.Canvas.Pixels[i] > 0).Count();
            Drag((15, 0));
            int after = view.Canvas.Pixels.Where((_, i) => i % 4 == 3 && view.Canvas.Pixels[i] > 0).Count();
            check(after > before + 100 && view.Canvas.Get(15, 0) == new Rgba(255, 201, 14, 255), $"the fill bucket paints the open area ({before} to {after} pixels)");
            input.Ctrl = true;
            input.Pressed.Add(ImGuiKey.Z);
            Frame(5f, 5f, false);
            input.Ctrl = false;
            check(view.Canvas.Get(15, 0).A == 0, "Ctrl+Z undoes the fill");

            // Picking a color with Alt, whatever the tool.
            view.Tool = SpriteTool.Brush;
            input.Alt = true;
            Drag((3, 3));
            input.Alt = false;
            check(view.PaintColor == new Rgba(237, 28, 36, 255) && view.Canvas.Get(3, 3) == new Rgba(237, 28, 36, 255), "Alt-clicking picks the color under the mouse");

            // The wheel zooms around the mouse.
            var (px, py) = At(8, 8);
            float zoomBefore = view.Zoom;
            Frame(px, py, false, wheel: 1f);
            var (nx, ny) = At(8, 8);
            check(view.Zoom > zoomBefore && Math.Abs(nx - px) <= view.Zoom && Math.Abs(ny - py) <= view.Zoom, $"scrolling zooms in, keeping the pixel under the mouse there ({zoomBefore}x to {view.Zoom}x)");
            Frame(px, py, false, right: true);
            var (ox, oy) = At(8, 8);
            Frame(px + 60f, py + 40f, false, right: true);
            Frame(px + 60f, py + 40f, false);
            var (qx, qy) = At(8, 8);
            check(Math.Abs((qx - ox) - 60f) < 1.5f && Math.Abs((qy - oy) - 40f) < 1.5f, "dragging with the right button pans the picture");

            // Saving.
            view.SaveName = "Sprites/Hero";
            view.Format = ImageFormat.Png;
            check(view.Save() && File.Exists(Path.Combine(project, "Sprites", "Hero.png")) && saved.SequenceEqual(["Sprites/Hero.png"]) && !view.Dirty, "Save writes the PNG into a new folder, says so, and makes the sprite clean");
            var png = ImageData.Load(Path.Combine(project, "Sprites", "Hero.png"));
            check(png.Width == 16 && png.Height == 16 && png.Pixels!.AsSpan().SequenceEqual(view.Canvas.Pixels), "the file holds exactly the picture");
            view.Format = ImageFormat.Jpeg;
            view.SaveName = "Sprites/Hero.png";
            check(view.Save() && File.Exists(Path.Combine(project, "Sprites", "Hero.jpg")) && ImageData.Load(Path.Combine(project, "Sprites", "Hero.jpg")).Width == 16, "JPEG: the typed extension is replaced by the format's, and the file reads back");
            view.Format = ImageFormat.Bmp;
            check(view.Save() && ImageData.Load(Path.Combine(project, "Sprites", "Hero.bmp")).Height == 16, "BMP saves too");

            File.WriteAllText(Path.Combine(project, "Other.png"), "not a picture");
            view.Format = ImageFormat.Png;
            view.SaveName = "Other";
            bool firstTry = view.Save();
            check(!firstTry && File.ReadAllText(Path.Combine(project, "Other.png")) == "not a picture", "an existing file is not replaced the first time");
            check(view.Save() && ImageData.Load(Path.Combine(project, "Other.png")).Width == 16, "but is when Save is pressed again");

            int filesBefore = Directory.GetFiles(project, "*", SearchOption.AllDirectories).Length;
            var escaped = Path.Combine(Path.GetDirectoryName(project)!, "escape.png");
            bool anySaved = false;
            foreach (string bad in new[] { "", "   ", "../escape", "/etc/escape", "a//b", "..", "Sprites/../../escape", "bad\0name" })
            {
                view.SaveName = bad;
                anySaved |= view.Save();
            }
            check(!anySaved && !File.Exists(escaped) && Directory.GetFiles(project, "*", SearchOption.AllDirectories).Length == filesBefore, "empty names and names that leave the project are refused, and nothing is written");

            // Opening.
            view.NewSprite(4, 4);
            check(view.Canvas.Width == 4 && !view.Dirty, "a new sprite starts clean and empty");
            view.Edit("Sprites/Hero.png");
            check(view.Canvas.Width == 16 && view.Canvas.Get(3, 3) == new Rgba(237, 28, 36, 255) && !view.Dirty, "opening a saved file brings back the picture");
            File.WriteAllBytes(Path.Combine(project, "huge.png"), ImageEncoder.Png(SpriteCanvas.MaxSize + 1, 2, new byte[(SpriteCanvas.MaxSize + 1) * 8]));
            view.Edit("huge.png");
            check(view.Canvas.Width == 16, "a picture too big to edit is refused and the sprite stays");
            view.Edit("../outside.png");
            check(view.Canvas.Width == 16, "so is a path outside the project");

            check(!ctx.HadGpuError, "the sprite editor draws without GPU errors");

            // For looking at by eye: set SPRITE_EDITOR_DUMP to a .ppm path.
            if (Environment.GetEnvironmentVariable("SPRITE_EDITOR_DUMP") is { Length: > 0 } dump)
            {
                view.Tool = SpriteTool.Brush;
                var finalShot = Frame(5f, 5f, false);
                using var file = File.Create(dump);
                file.Write(System.Text.Encoding.ASCII.GetBytes($"P6 {W} {H} 255\n"));
                for (int i = 0; i < finalShot.Length; i += 4) { file.WriteByte(finalShot[i + 2]); file.WriteByte(finalShot[i + 1]); file.WriteByte(finalShot[i]); }
            }
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }
}
