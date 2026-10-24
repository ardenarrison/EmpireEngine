using Brutal.GlfwApi;
using Engine.Core.Assets;

namespace Engine.Brutal.Gfx;

// Empire Engine's icon (EmpireEngineIcon.png in the repository root, a crown) on the editor's and games' windows: the title bar and
// taskbar on Windows and Linux, with its transparent background, and the Dock on macOS, where it sits on a white rounded
// tile like other Mac apps (EmpireEngineIcon-mac.png). Windows .exe files also get it from EmpireEngineIcon.ico at build time.
// Tools/make_icons.py makes the .ico and the Mac image from EmpireEngineIcon.png.
public static class AppIcon
{
    private const string ResourceName = "Engine.Brutal.EmpireEngineIcon.png";
    private const string MacResourceName = "Engine.Brutal.EmpireEngineIcon-mac.png";
    private const int WindowIconSize = 256;   // what Windows and Linux are given; they scale it for the title bar

    // The icon as a PNG file's bytes (1024 x 1024), with a transparent background.
    public static byte[] Png() => Resource(ResourceName);

    // The macOS Dock's copy: the crown on a white rounded square, on Apple's icon grid.
    public static byte[] MacPng() => Resource(MacResourceName);

    private static byte[] Resource(string name)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} is missing from Engine.Brutal.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    // Puts the icon on a window. Call it right after the window is made.
    public static unsafe void Apply(GlfwWindow window)
    {
        if (OperatingSystem.IsMacOS())
        {
            // GLFW can't set window icons on macOS (windows there have none); the Dock icon is the app's icon.
            MacDockIcon.Set(MacPng());
            return;
        }

        var image = ImageData.Decode(Png(), "EmpireEngineIcon.png");
        if (image.Pixels is not { } pixels) return;
        var (small, size) = Shrink(pixels, image.Width, WindowIconSize);
        fixed (byte* p = small)
            window.SetIcon(1, new GlfwImage { Width = size, Height = size, Pixels = p });
    }

    // Scales a square RGBA image down by a whole factor to about target pixels across, averaging each block of pixels
    // weighted by their alpha, so the transparent background doesn't darken the edges.
    private static (byte[] Pixels, int Size) Shrink(byte[] rgba, int size, int target)
    {
        int factor = Math.Max(1, size / target);
        int outSize = size / factor;
        var result = new byte[outSize * outSize * 4];
        for (int y = 0; y < outSize; y++)
            for (int x = 0; x < outSize; x++)
            {
                long r = 0, g = 0, b = 0, a = 0;
                for (int dy = 0; dy < factor; dy++)
                    for (int dx = 0; dx < factor; dx++)
                    {
                        int i = ((y * factor + dy) * size + x * factor + dx) * 4;
                        int alpha = rgba[i + 3];
                        r += rgba[i] * alpha;
                        g += rgba[i + 1] * alpha;
                        b += rgba[i + 2] * alpha;
                        a += alpha;
                    }
                int o = (y * outSize + x) * 4;
                if (a > 0)
                {
                    result[o] = (byte)(r / a);
                    result[o + 1] = (byte)(g / a);
                    result[o + 2] = (byte)(b / a);
                }
                result[o + 3] = (byte)(a / (factor * factor));
            }
        return (result, outSize);
    }
}
