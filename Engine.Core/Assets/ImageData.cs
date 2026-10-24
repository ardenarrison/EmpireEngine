using StbImageSharp;

namespace Engine.Core.Assets;

// One level of an image's mip chain, from full size down to 1x1. Exactly one of the pixel arrays is set.
public sealed record ImageLevel(int Width, int Height, byte[]? Pixels, float[]? FloatPixels);

// A decoded picture, RGBA. 8-bit images (PNG, JPEG, BMP, TGA) keep their bytes as stored; Radiance .hdr images keep
// linear float values. Decoding is separate from what the picture is used for, so the same image can serve as a base
// color in one material and a data map in another.
public sealed class ImageData
{
    public required string Name { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public byte[]? Pixels { get; init; }          // width * height * 4 bytes
    public float[]? FloatPixels { get; init; }    // width * height * 4 floats, for HDR images

    public bool IsHdr => FloatPixels is not null;

    // True when any pixel is not fully opaque. Used to decide whether an alpha-blended glTF material is a cutout.
    public bool HasTransparency
    {
        get
        {
            if (_hasTransparency is { } known) return known;
            bool found = false;
            if (Pixels is { } p)
                for (int i = 3; i < p.Length && !found; i += 4)
                    found = p[i] < 255;
            _hasTransparency = found;
            return found;
        }
    }
    private bool? _hasTransparency;

    public static ImageData Load(string path) => Decode(File.ReadAllBytes(path), Path.GetFileName(path));

    public static ImageData Decode(byte[] bytes, string name)
    {
        try
        {
            // Radiance HDR files start with "#?RADIANCE" or "#?RGBE".
            if (bytes.Length > 2 && bytes[0] == '#' && bytes[1] == '?')
            {
                var hdr = ImageResultFloat.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
                return new ImageData { Name = name, Width = hdr.Width, Height = hdr.Height, FloatPixels = hdr.Data };
            }
            var image = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
            return new ImageData { Name = name, Width = image.Width, Height = image.Height, Pixels = image.Data };
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException($"Could not read the image {name}: {ex.Message}", ex);
        }
    }

    // The full mip chain, level 0 first. Each level averages 2x2 blocks of the one before. Levels wider than
    // maxWidth are left out, which shrinks very large images (panoramas) without changing how they look.
    public IReadOnlyList<ImageLevel> BuildMips(int maxWidth = int.MaxValue)
    {
        var levels = new List<ImageLevel> { new(Width, Height, Pixels, FloatPixels) };
        while (levels[^1].Width > 1 || levels[^1].Height > 1)
            levels.Add(Downsample(levels[^1]));

        int skip = 0;
        while (skip < levels.Count - 1 && levels[skip].Width > maxWidth) skip++;
        return skip == 0 ? levels : levels.Skip(skip).ToList();
    }

    private static ImageLevel Downsample(ImageLevel from)
    {
        int w = Math.Max(1, from.Width / 2), h = Math.Max(1, from.Height / 2);
        if (from.Pixels is { } src)
        {
            var dst = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int x0 = Math.Min(x * 2, from.Width - 1), x1 = Math.Min(x * 2 + 1, from.Width - 1);
                    int y0 = Math.Min(y * 2, from.Height - 1), y1 = Math.Min(y * 2 + 1, from.Height - 1);
                    for (int c = 0; c < 4; c++)
                    {
                        int sum = src[(y0 * from.Width + x0) * 4 + c] + src[(y0 * from.Width + x1) * 4 + c]
                                + src[(y1 * from.Width + x0) * 4 + c] + src[(y1 * from.Width + x1) * 4 + c];
                        dst[(y * w + x) * 4 + c] = (byte)((sum + 2) / 4);
                    }
                }
            return new ImageLevel(w, h, dst, null);
        }

        var fsrc = from.FloatPixels!;
        var fdst = new float[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Min(x * 2, from.Width - 1), x1 = Math.Min(x * 2 + 1, from.Width - 1);
                int y0 = Math.Min(y * 2, from.Height - 1), y1 = Math.Min(y * 2 + 1, from.Height - 1);
                for (int c = 0; c < 4; c++)
                    fdst[(y * w + x) * 4 + c] = (fsrc[(y0 * from.Width + x0) * 4 + c] + fsrc[(y0 * from.Width + x1) * 4 + c]
                                               + fsrc[(y1 * from.Width + x0) * 4 + c] + fsrc[(y1 * from.Width + x1) * 4 + c]) * 0.25f;
            }
        return new ImageLevel(w, h, null, fdst);
    }
}
