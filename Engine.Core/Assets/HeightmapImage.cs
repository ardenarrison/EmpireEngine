using System.Buffers.Binary;
using System.IO.Compression;

namespace Engine.Core.Assets;

// Reads a heightmap picture as heights from 0 (black) to 1 (white), row by row from the top. 16-bit PNGs keep all their
// precision, which smooth slopes need: an 8-bit picture has only 256 steps, and gentle hills made from one show them as
// terraces. Any other picture (8-bit PNG, JPEG, BMP, GIF) is read through ImageData, using the average of red, green and blue.
public static class HeightmapImage
{
    public static bool IsHeightmapImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif";

    public static (float[] Values, int Width, int Height) Load(string path) => Decode(File.ReadAllBytes(path), Path.GetFileName(path));

    public static (float[] Values, int Width, int Height) Decode(byte[] bytes, string name)
    {
        if (TryDecodePng16(bytes) is { } deep) return deep;

        var image = ImageData.Decode(bytes, name);
        var values = new float[image.Width * image.Height];
        if (image.Pixels is { } p)
            for (int i = 0; i < values.Length; i++)
                values[i] = (p[i * 4] + p[i * 4 + 1] + p[i * 4 + 2]) / (3f * 255f);
        else if (image.FloatPixels is { } f)
            for (int i = 0; i < values.Length; i++)
                values[i] = Math.Clamp((f[i * 4] + f[i * 4 + 1] + f[i * 4 + 2]) / 3f, 0f, 1f);
        return (values, image.Width, image.Height);
    }

    // A 16-bit, non-interlaced PNG (gray, gray with alpha, RGB or RGBA): the first channel of each pixel. Null for anything
    // else, which is left to the general decoder.
    private static (float[], int, int)? TryDecodePng16(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 33 || !bytes.AsSpan(0, 8).SequenceEqual(signature)) return null;

        int width = 0, height = 0, channels = 0;
        using var idat = new MemoryStream();
        int at = 8;
        while (at + 8 <= bytes.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(at));
            string type = System.Text.Encoding.ASCII.GetString(bytes, at + 4, 4);
            if (length < 0 || at + 12 + length > bytes.Length) return null;
            var data = bytes.AsSpan(at + 8, length);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                byte depth = data[8], colorType = data[9], interlace = data[12];
                channels = colorType switch { 0 => 1, 4 => 2, 2 => 3, 6 => 4, _ => 0 };
                if (depth != 16 || channels == 0 || interlace != 0 || width < 1 || height < 1 || (long)width * height > 64L << 20)
                    return null;
            }
            else if (type == "IDAT") idat.Write(data);
            else if (type == "IEND") break;
            at += 12 + length;
        }
        if (channels == 0) return null;

        int bpp = channels * 2, stride = width * bpp;
        var raw = new byte[(stride + 1) * height];
        try
        {
            idat.Position = 0;
            using var inflate = new ZLibStream(idat, CompressionMode.Decompress);
            int read = 0;
            while (read < raw.Length)
            {
                int n = inflate.Read(raw, read, raw.Length - read);
                if (n == 0) return null;
                read += n;
            }
        }
        catch (InvalidDataException) { return null; }

        // Undo each row's filter in place (the PNG specification's five filters), then take the first channel.
        var values = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            int row = y * (stride + 1) + 1, previous = row - stride - 1;
            byte filter = raw[row - 1];
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? raw[row + i - bpp] : 0;
                int b = y > 0 ? raw[previous + i] : 0;
                int c = y > 0 && i >= bpp ? raw[previous + i - bpp] : 0;
                int add = filter switch
                {
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => 0,
                };
                raw[row + i] = (byte)(raw[row + i] + add);
            }
            for (int x = 0; x < width; x++)
                values[y * width + x] = ((raw[row + x * bpp] << 8) | raw[row + x * bpp + 1]) / 65535f;
        }
        return (values, width, height);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
