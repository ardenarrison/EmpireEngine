using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Engine.Core.Assets;

// The picture file formats the sprite editor saves.
public enum ImageFormat { Png, Jpeg, Bmp }

// Writes 8-bit RGBA pictures (straight alpha, row after row from the top) as PNG, JPEG or BMP files, with no library: the
// project's other pictures are read with StbImageSharp, which only decodes.
public static class ImageEncoder
{
    public static string Extension(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Bmp => ".bmp",
        _ => ".png",
    };

    // The format a file name's extension names, or null for any other.
    public static ImageFormat? FromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => ImageFormat.Png,
        ".jpg" or ".jpeg" => ImageFormat.Jpeg,
        ".bmp" => ImageFormat.Bmp,
        _ => null,
    };

    // quality is only used by JPEG, from 1 (smallest file) to 100 (best picture).
    public static byte[] Encode(ImageFormat format, int width, int height, byte[] rgba, int quality = 90)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            throw new ArgumentException("The picture's pixels do not match its size.");
        return format switch
        {
            ImageFormat.Jpeg => Jpeg(width, height, rgba, quality),
            ImageFormat.Bmp => Bmp(width, height, rgba),
            _ => Png(width, height, rgba),
        };
    }

    // ---- PNG ---------------------------------------------------------------------------------------

    public static byte[] Png(int width, int height, byte[] rgba)
    {
        int row = width * 4;
        var raw = new byte[(row + 1) * height];   // each row starts with its filter type, 0 for none
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(rgba, y * row, raw, y * (row + 1) + 1, row);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;   // bits per channel
        header[9] = 6;   // red, green, blue, alpha

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        PngChunk(png, "IHDR", header);
        PngChunk(png, "IDAT", compressed.ToArray());
        PngChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void PngChunk(Stream png, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        var body = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, body);
        data.CopyTo(body, 4);
        png.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc(body));
        png.Write(number);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int i = 0; i < 8; i++)
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    // ---- BMP ---------------------------------------------------------------------------------------

    // An opaque picture is a plain 24-bit BMP, which every program reads. One with see-through pixels is a 32-bit BMP with an alpha
    // channel (a version 4 header with channel masks), which programs that know about alpha show correctly.
    public static byte[] Bmp(int width, int height, byte[] rgba)
    {
        bool alpha = false;
        for (int i = 3; i < width * height * 4 && !alpha; i += 4)
            alpha = rgba[i] < 255;

        int bytesPerPixel = alpha ? 4 : 3;
        int rowBytes = (width * bytesPerPixel + 3) & ~3;   // rows are padded to a multiple of 4 bytes
        int headerBytes = alpha ? 14 + 108 : 14 + 40;
        var file = new byte[headerBytes + rowBytes * height];
        var span = file.AsSpan();

        span[0] = (byte)'B';
        span[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(span[2..], file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[10..], headerBytes);

        var dib = span[14..];
        BinaryPrimitives.WriteInt32LittleEndian(dib, alpha ? 108 : 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(dib[8..], height);   // positive: rows run from the bottom up
        BinaryPrimitives.WriteInt16LittleEndian(dib[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(dib[14..], (short)(bytesPerPixel * 8));
        BinaryPrimitives.WriteInt32LittleEndian(dib[16..], alpha ? 3 : 0);   // BI_BITFIELDS, or BI_RGB
        BinaryPrimitives.WriteInt32LittleEndian(dib[20..], rowBytes * height);
        BinaryPrimitives.WriteInt32LittleEndian(dib[24..], 2835);   // 72 pixels per inch
        BinaryPrimitives.WriteInt32LittleEndian(dib[28..], 2835);
        if (alpha)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dib[40..], 0x00FF0000);   // red mask
            BinaryPrimitives.WriteUInt32LittleEndian(dib[44..], 0x0000FF00);   // green mask
            BinaryPrimitives.WriteUInt32LittleEndian(dib[48..], 0x000000FF);   // blue mask
            BinaryPrimitives.WriteUInt32LittleEndian(dib[52..], 0xFF000000);   // alpha mask
            BinaryPrimitives.WriteUInt32LittleEndian(dib[56..], 0x73524742);   // "sRGB"
        }

        for (int y = 0; y < height; y++)
        {
            int source = (height - 1 - y) * width * 4;
            int target = headerBytes + y * rowBytes;
            for (int x = 0; x < width; x++, source += 4, target += bytesPerPixel)
            {
                file[target] = rgba[source + 2];       // blue, green, red (then alpha)
                file[target + 1] = rgba[source + 1];
                file[target + 2] = rgba[source];
                if (alpha) file[target + 3] = rgba[source + 3];
            }
        }
        return file;
    }

    // ---- JPEG --------------------------------------------------------------------------------------

    // A baseline JPEG with full-resolution color (no chroma subsampling, so sharp pixel art edges stay sharp). JPEG has no alpha:
    // see-through pixels are blended with white.
    public static byte[] Jpeg(int width, int height, byte[] rgba, int quality)
    {
        quality = Math.Clamp(quality, 1, 100);
        var lumaTable = ScaleQuantization(LumaQuantization, quality);
        var chromaTable = ScaleQuantization(ChromaQuantization, quality);

        using var output = new MemoryStream();
        output.Write([0xFF, 0xD8]);                                                                  // start of image
        output.Write([0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]);   // JFIF
        WriteQuantization(output, 0, lumaTable);
        WriteQuantization(output, 1, chromaTable);
        output.Write([0xFF, 0xC0, 0, 17, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3,
            1, 0x11, 0, 2, 0x11, 1, 3, 0x11, 1]);                                                   // baseline, 3 components, no subsampling
        WriteHuffmanTable(output, 0x00, DcLumaBits, DcValues);
        WriteHuffmanTable(output, 0x10, AcLumaBits, AcLumaValues);
        WriteHuffmanTable(output, 0x01, DcChromaBits, DcValues);
        WriteHuffmanTable(output, 0x11, AcChromaBits, AcChromaValues);
        output.Write([0xFF, 0xDA, 0, 12, 3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0]);                   // start of scan

        var dcLuma = HuffmanCodes(DcLumaBits, DcValues);
        var acLuma = HuffmanCodes(AcLumaBits, AcLumaValues);
        var dcChroma = HuffmanCodes(DcChromaBits, DcValues);
        var acChroma = HuffmanCodes(AcChromaBits, AcChromaValues);

        var bits = new BitWriter(output);
        var block = new float[64];
        var coefficients = new int[64];
        var previousDc = new int[3];   // each component's last DC value, which the next one is written relative to
        for (int blockY = 0; blockY < height; blockY += 8)
            for (int blockX = 0; blockX < width; blockX += 8)
            {
                for (int component = 0; component < 3; component++)
                {
                    FillBlock(rgba, width, height, blockX, blockY, component, block);
                    Quantize(block, component == 0 ? lumaTable : chromaTable, coefficients);
                    WriteBlock(bits, coefficients, ref previousDc[component], component == 0 ? dcLuma : dcChroma, component == 0 ? acLuma : acChroma);
                }
            }
        bits.Flush();
        output.Write([0xFF, 0xD9]);   // end of image
        return output.ToArray();
    }

    // One 8x8 block of one component (Y, Cb or Cr), shifted to be centered on 0. Pixels past the edge repeat the last row or column.
    private static void FillBlock(byte[] rgba, int width, int height, int blockX, int blockY, int component, float[] block)
    {
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int i = (Math.Min(blockY + y, height - 1) * width + Math.Min(blockX + x, width - 1)) * 4;
                float a = rgba[i + 3] / 255f;
                float r = rgba[i] * a + 255f * (1f - a), g = rgba[i + 1] * a + 255f * (1f - a), b = rgba[i + 2] * a + 255f * (1f - a);
                float value = component switch
                {
                    0 => 0.299f * r + 0.587f * g + 0.114f * b,
                    1 => -0.168736f * r - 0.331264f * g + 0.5f * b + 128f,
                    _ => 0.5f * r - 0.418688f * g - 0.081312f * b + 128f,
                };
                block[y * 8 + x] = value - 128f;
            }
    }

    private static readonly float[,] Cosines = BuildCosines();

    private static float[,] BuildCosines()
    {
        var table = new float[8, 8];   // [frequency, position]
        for (int u = 0; u < 8; u++)
            for (int x = 0; x < 8; x++)
                table[u, x] = (float)(Math.Cos((2 * x + 1) * u * Math.PI / 16.0) * (u == 0 ? Math.Sqrt(0.125) : 0.5));
        return table;
    }

    // The 2D discrete cosine transform of a block, divided by the quantization table and rounded, in zigzag order.
    private static void Quantize(float[] block, int[] table, int[] zigzag)
    {
        Span<float> rows = stackalloc float[64];
        for (int y = 0; y < 8; y++)
            for (int u = 0; u < 8; u++)
            {
                float sum = 0f;
                for (int x = 0; x < 8; x++) sum += block[y * 8 + x] * Cosines[u, x];
                rows[y * 8 + u] = sum;
            }
        for (int u = 0; u < 8; u++)
            for (int v = 0; v < 8; v++)
            {
                float sum = 0f;
                for (int y = 0; y < 8; y++) sum += rows[y * 8 + u] * Cosines[v, y];
                int natural = v * 8 + u;
                zigzag[ZigzagPosition[natural]] = (int)MathF.Round(sum / table[natural]);
            }
    }

    private static void WriteBlock(BitWriter bits, int[] zigzag, ref int previousDc, (int Code, int Length)[] dc, (int Code, int Length)[] ac)
    {
        int difference = zigzag[0] - previousDc;
        previousDc = zigzag[0];
        int size = Magnitude(difference);
        bits.Write(dc[size]);
        if (size > 0) bits.Write(difference < 0 ? difference - 1 : difference, size);

        int run = 0;
        for (int i = 1; i < 64; i++)
        {
            int value = zigzag[i];
            if (value == 0) { run++; continue; }
            while (run > 15) { bits.Write(ac[0xF0]); run -= 16; }   // sixteen zeros in a row
            int bitsNeeded = Magnitude(value);
            bits.Write(ac[(run << 4) | bitsNeeded]);
            bits.Write(value < 0 ? value - 1 : value, bitsNeeded);
            run = 0;
        }
        if (run > 0) bits.Write(ac[0x00]);   // end of block
    }

    // How many bits a value's size takes: 0 for 0, 1 for 1, 2 for 2 to 3, 3 for 4 to 7...
    private static int Magnitude(int value)
    {
        value = Math.Abs(value);
        int size = 0;
        while (value > 0) { size++; value >>= 1; }
        return size;
    }

    private sealed class BitWriter(Stream output)
    {
        private int _buffer, _count;

        public void Write((int Code, int Length) code) => Write(code.Code, code.Length);

        public void Write(int value, int length)
        {
            if (length == 0) return;
            _buffer = (_buffer << length) | (value & ((1 << length) - 1));
            _count += length;
            while (_count >= 8)
            {
                int b = (_buffer >> (_count - 8)) & 0xFF;
                output.WriteByte((byte)b);
                if (b == 0xFF) output.WriteByte(0);   // a 0xFF byte in the data is followed by 0 so it isn't read as a marker
                _count -= 8;
            }
            _buffer &= (1 << _count) - 1;
        }

        public void Flush()
        {
            if (_count > 0) Write(0x7F, 8 - _count);   // pad the last byte with 1 bits
        }
    }

    private static int[] ScaleQuantization(int[] table, int quality)
    {
        int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
        return table.Select(t => Math.Clamp((t * scale + 50) / 100, 1, 255)).ToArray();
    }

    private static void WriteQuantization(Stream output, int id, int[] table)
    {
        output.Write([0xFF, 0xDB, 0, 67, (byte)id]);
        foreach (int natural in Zigzag) output.WriteByte((byte)table[natural]);
    }

    private static void WriteHuffmanTable(Stream output, int id, byte[] counts, byte[] values)
    {
        int length = 2 + 1 + 16 + values.Length;
        output.Write([0xFF, 0xC4, (byte)(length >> 8), (byte)length, (byte)id]);
        output.Write(counts);
        output.Write(values);
    }

    // The code for each symbol of a Huffman table given as the JPEG spec does: how many codes there are of each length 1 to 16, then the
    // symbols in code order.
    private static (int Code, int Length)[] HuffmanCodes(byte[] counts, byte[] values)
    {
        if (counts.Sum(c => c) != values.Length)
            throw new InvalidOperationException("A JPEG Huffman table's code counts don't match its symbols.");
        var codes = new (int, int)[256];
        int code = 0, index = 0;
        for (int length = 1; length <= 16; length++)
        {
            for (int i = 0; i < counts[length - 1]; i++)
                codes[values[index++]] = (code++, length);
            code <<= 1;
        }
        return codes;
    }

    private static readonly int[] Zigzag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    // Where each coefficient (by its position in the 8x8 block) lands in zigzag order.
    private static readonly int[] ZigzagPosition = Enumerable.Range(0, 64).Select(n => Array.IndexOf(Zigzag, n)).ToArray();

    private static readonly int[] LumaQuantization =
    [
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
    ];

    private static readonly int[] ChromaQuantization =
    [
        17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
    ];

    private static readonly byte[] DcLumaBits = [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] DcChromaBits = [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
    private static readonly byte[] DcValues = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    private static readonly byte[] AcLumaBits = [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D];
    private static readonly byte[] AcLumaValues =
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14, 0x32, 0x81, 0x91,
        0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0, 0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A,
        0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x53,
        0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79,
        0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5,
        0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9,
        0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2,
        0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0xFA,
    ];

    private static readonly byte[] AcChromaBits = [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];
    private static readonly byte[] AcChromaValues =
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71, 0x13, 0x22, 0x32, 0x81, 0x08, 0x14,
        0x42, 0x91, 0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0, 0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34, 0xE1, 0x25, 0xF1, 0x17,
        0x18, 0x19, 0x1A, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A,
        0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78,
        0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3,
        0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7,
        0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF2,
        0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0xFA,
    ];
}
