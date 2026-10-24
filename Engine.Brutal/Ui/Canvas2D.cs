using Brutal;
using Brutal.Numerics;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Engine.Brutal.Gfx;
using StbTrueTypeSharp;

namespace Engine.Brutal.Ui;

// A 2D renderer drawn straight into the swapchain with Vulkan, in the UI pass after the scene. The editor's panels and
// the game's UI (in the editor's play mode and in built games) both draw with it.
// Solid rectangles and text share one pipeline. Text comes from a glyph atlas that stb_truetype
// rasterizes from a TTF file. Coordinates are pixels, with (0, 0) at the top-left of the window.
// The atlas is rasterized at the display's pixel density (2x on Retina) and sampled with bilinear filtering, so
// text stays sharp; it is rebuilt when the window moves to a display with a different density.
public sealed unsafe class Canvas2D : IDisposable
{
    private const int FirstChar = 32;
    private const int LastChar = 126;
    private const int GlyphCount = LastChar - FirstChar + 1;
    private const int MaxAtlasSize = 4096;
    private readonly float _fontSize;
    private const float BaselineFactor = 0.8f;  // distance from the top of a line to its baseline, as a share of font size
    private const int FloatsPerVertex = 8;      // position (2), atlas uv (2), color (4)
    private const int MaxVertices = 400_000;   // across all layers; collider wireframes alone can use 70k
    public const int LayerCount = 4;
    private const int StageVertexFragment = 0x11;
    private const int StageFragment = 0x10;
    private const int BufferVertex = 0x80;
    private const int BufferStorage = 0x20;
    private const int DescriptorStorageBuffer = 7;

    private const string VertexGlsl = """
        #version 450
        layout(push_constant) uniform PC { vec2 screen; float atlasWidth; float imageWidth; } pc;
        layout(location = 0) in vec2 inPos;
        layout(location = 1) in vec2 inUv;
        layout(location = 2) in vec4 inColor;
        layout(location = 0) out vec2 outUv;
        layout(location = 1) out vec4 outColor;
        void main() {
            gl_Position = vec4(inPos / pc.screen * 2.0 - 1.0, 0.0, 1.0);
            outUv = inUv;
            outColor = inColor;
        }
        """;

    // A uv of exactly (-1, -1) means a solid rectangle. A uv.x of -2 or less is an image: its x is -(2 + x) and its y is y,
    // where (x, y) is a position in the image buffer's pixels. Otherwise the uv is a position in atlas pixels and the glyph
    // coverage is read from the buffer.
    private const string FragmentGlsl = """
        #version 450
        layout(push_constant) uniform PC { vec2 screen; float atlasWidth; float imageWidth; } pc;
        layout(std430, set = 0, binding = 0) readonly buffer Atlas { float cov[]; } atlas;
        layout(std430, set = 0, binding = 1) readonly buffer Images { uint px[]; } img;
        layout(location = 0) in vec2 inUv;
        layout(location = 1) in vec4 inColor;
        layout(location = 0) out vec4 outColor;
        float texel(ivec2 p) {
            int size = int(pc.atlasWidth);
            p = clamp(p, ivec2(0), ivec2(size - 1));
            return atlas.cov[p.y * size + p.x];
        }
        // One pixel of the image buffer, red first, as straight (not premultiplied) 0 to 1 values.
        vec4 pixel(ivec2 p) {
            int width = int(pc.imageWidth);
            int rows = img.px.length() / width;
            p = clamp(p, ivec2(0), ivec2(width - 1, rows - 1));
            uint v = img.px[p.y * width + p.x];
            return vec4(float(v & 255u), float((v >> 8) & 255u), float((v >> 16) & 255u), float(v >> 24)) / 255.0;
        }
        void main() {
            if (inUv.x > -1.5 && inUv.x < 0.0) {
                outColor = inColor;
                return;
            }
            if (inUv.x <= -1.5) {
                // Bilinear, blending premultiplied colors so a transparent pixel's color never leaks into an edge.
                vec2 t = vec2(-inUv.x - 2.0, inUv.y) - 0.5;
                ivec2 i = ivec2(floor(t));
                vec2 f = t - vec2(i);
                vec4 a = pixel(i), b = pixel(i + ivec2(1, 0)), c = pixel(i + ivec2(0, 1)), d = pixel(i + ivec2(1, 1));
                a.rgb *= a.a; b.rgb *= b.a; c.rgb *= c.a; d.rgb *= d.a;
                vec4 mixed = mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
                if (mixed.a > 0.0) mixed.rgb /= mixed.a;
                outColor = vec4(mixed.rgb * inColor.rgb, mixed.a * inColor.a);
                return;
            }
            // Bilinear: blend the four texels around the sample point, so glyphs placed between pixels stay smooth.
            vec2 t = inUv - 0.5;
            ivec2 i = ivec2(floor(t));
            vec2 f = t - vec2(i);
            float top = mix(texel(i), texel(i + ivec2(1, 0)), f.x);
            float bottom = mix(texel(i + ivec2(0, 1)), texel(i + ivec2(1, 1)), f.x);
            outColor = vec4(inColor.rgb, inColor.a * mix(top, bottom, f.y));
        }
        """;

    // Atlas rectangle for one character (in atlas pixels), plus its size and placement relative to the pen and
    // baseline (in canvas pixels, which differ from atlas pixels by the bake scale).
    private readonly record struct Glyph(float X0, float Y0, float X1, float Y1, float Width, float Height,
        float XOff, float YOff, float Advance);

    private readonly VulkanContext _ctx;
    private readonly Device _device;
    private readonly PhysicalDevice _physical;
    private readonly Glyph[] _glyphs = new Glyph[GlyphCount];
    private readonly byte[] _fontData;
    private VkBuffer _atlasBuffer;
    private VkDeviceMemory _atlasMemory;
    private ulong _atlasBytes;
    private int _atlasSize;
    // Pictures drawn with Image, stacked in one buffer of 8-bit RGBA pixels that all share a row length.
    private readonly List<(byte[] Rgba, int Width, int Height)> _imageSources = new();
    private readonly List<(int X, int Y, int Width, int Height)> _imageRects = new();
    private VkBuffer _imageBuffer;
    private VkDeviceMemory _imageMemory;
    private ulong _imageBytes;
    private int _imageStride = 1;
    private bool _hasImageBuffer;
    private float _bakedScale;
    private readonly VkDescriptorSetLayout _setLayout;
    private readonly VkDescriptorPool _pool;
    private readonly VkDescriptorSet _set;
    private readonly VkPipelineLayout _layout;
    private readonly VkPipeline _pipeline;
    private readonly VkBuffer[] _vertexBuffers = new VkBuffer[VulkanContext.FramesInFlight];
    private readonly VkDeviceMemory[] _vertexMemories = new VkDeviceMemory[VulkanContext.FramesInFlight];
    // One vertex list per layer, drawn in layer order, so later layers sit on top whatever order calls come in.
    private readonly float[][] _layers = Enumerable.Range(0, LayerCount).Select(_ => new float[4096 * FloatsPerVertex]).ToArray();
    private readonly int[] _layerCounts = new int[LayerCount];
    private readonly float[] _upload = new float[MaxVertices * FloatsPerVertex];
    private readonly float[] _push = new float[4];
    private int _vertexCount;   // across all layers
    private int _frameSlot;
    private int _layer = 1;
    private Box? _clip;

    // The font that ships inside Engine.Brutal (JetBrains Mono), so the editor and built games need no font file.
    public static byte[] DefaultFont()
    {
        using var stream = typeof(Canvas2D).Assembly.GetManifestResourceStream("Engine.Brutal.Ui.Fonts.JetBrainsMono-Regular.ttf")
            ?? throw new InvalidOperationException("The built-in UI font is missing from Engine.Brutal.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    // pixelScale is the display's framebuffer pixels per canvas pixel (2 on Retina); Render adjusts to it later.
    public Canvas2D(VulkanContext ctx, ShaderLibrary shaders, string fontPath, float fontSize = 20f, float pixelScale = 1f)
        : this(ctx, shaders, File.ReadAllBytes(fontPath), fontSize, pixelScale)
    {
    }

    // The same with the font's bytes, such as DefaultFont().
    public Canvas2D(VulkanContext ctx, ShaderLibrary shaders, byte[] fontData, float fontSize = 20f, float pixelScale = 1f)
    {
        _ctx = ctx;
        _fontSize = fontSize;
        _device = ctx.Device;
        _physical = ctx.Physical;
        _fontData = fontData;

        for (int slot = 0; slot < VulkanContext.FramesInFlight; slot++)
            (_vertexBuffers[slot], _vertexMemories[slot]) = CreateHostBuffer(_device, _physical,
                (VkBufferUsageFlags)BufferVertex, (ulong)(MaxVertices * FloatsPerVertex * sizeof(float)));

        _setLayout = CreateSetLayout();
        _pool = CreateDescriptorPool();
        _set = AllocateDescriptorSet();
        UploadImages();     // an empty buffer for now, so the descriptor set is complete
        Bake(pixelScale);
        _layout = CreateLayout();
        _pipeline = CreatePipeline(shaders);
    }

    // Rasterizes printable ASCII at fontSize * scale into one single-channel atlas, uploads it to a storage buffer
    // that the fragment shader reads, and points the descriptor set at it. Called again when the scale changes.
    private void Bake(float scale)
    {
        scale = Math.Clamp(scale, 1f, 4f);
        float pixelSize = _fontSize * scale;

        // Start from a size that usually fits and double until every glyph does.
        int size = 256;
        byte[] pixels;
        var baked = new StbTrueType.stbtt_bakedchar[GlyphCount];
        while (true)
        {
            pixels = new byte[size * size];
            StbTrueType.stbtt_BakeFontBitmap(_fontData, 0, pixelSize, pixels, size, size, FirstChar, GlyphCount, baked);
            // Its return value says true even when glyphs ran out of room, so check that every visible one got space.
            bool fits = baked.Skip(1).All(b => b.x1 > b.x0);
            if (fits || size >= MaxAtlasSize) break;
            size *= 2;
        }

        for (int i = 0; i < GlyphCount; i++)
        {
            var b = baked[i];
            // Round letters (b, e, o, s) dip a little below the baseline, which unhinted rasterizing turns into a whole
            // extra pixel row, so they look lower than flat letters. Cut that row; real descenders (g, p, y, comma) are
            // deeper than the limit and are left alone.
            int y1 = b.y1;
            float below = b.yoff + (b.y1 - b.y0);
            if (b.y1 > b.y0 && below > 0f && below <= 1.25f * scale)
                y1 -= (int)MathF.Ceiling(below);
            _glyphs[i] = new Glyph(b.x0, b.y0, b.x1, y1, (b.x1 - b.x0) / scale, (y1 - b.y0) / scale,
                b.xoff / scale, b.yoff / scale, b.xadvance / scale);
        }

        var coverage = new float[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
            coverage[i] = pixels[i] / 255f;

        if (_atlasSize != 0)
        {
            // Frames in flight may still read the old atlas.
            _ctx.WaitIdle();
            _device.DestroyBuffer(_atlasBuffer, null);
            _device.FreeMemory(_atlasMemory, null);
        }
        (_atlasBuffer, _atlasMemory) = CreateHostBuffer(_device, _physical, (VkBufferUsageFlags)BufferStorage,
            (ulong)(coverage.Length * sizeof(float)));
        _device.WriteMemory<float>(coverage, _atlasMemory, default, default);
        _atlasBytes = (ulong)(coverage.Length * sizeof(float));
        WriteDescriptors();

        _atlasSize = size;
        _bakedScale = scale;
    }

    // ---- building a frame --------------------------------------------------------------

    // Layers draw in order: 0 under everything (scene gizmos), 1 for panels, 2 for popups and menus, 3 on top.
    public const int GizmoLayer = 0, PanelLayer = 1, PopupLayer = 2, TopLayer = 3;

    public void Begin()
    {
        _vertexCount = 0;
        Array.Clear(_layerCounts);
        _layer = PanelLayer;
        _clip = null;
    }

    public float FontSize => _fontSize;

    // The layer that following calls draw into.
    public int Layer
    {
        get => _layer;
        set => _layer = Math.Clamp(value, 0, LayerCount - 1);
    }

    // Rectangles and text are cut to this box (null for no clipping). Lines, triangles and circles are not.
    public Box? Clip
    {
        get => _clip;
        set => _clip = value;
    }

    public void Rect(float x, float y, float width, float height, float4 color) =>
        Quad(x, y, x + width, y + height, -1f, -1f, -1f, -1f, color);

    // A rectangle outline drawn inside the given box.
    public void Outline(float x, float y, float width, float height, float thickness, float4 color)
    {
        Rect(x, y, width, thickness, color);
        Rect(x, y + height - thickness, width, thickness, color);
        Rect(x, y + thickness, thickness, height - thickness * 2, color);
        Rect(x + width - thickness, y + thickness, thickness, height - thickness * 2, color);
    }

    // Adds a picture (8-bit RGBA, straight alpha, row after row from the top) for Image to draw. Returns its handle.
    // Costs a pause while the GPU finishes its current frame, so add pictures when starting up, not every frame.
    public int AddImage(byte[] rgba, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            throw new ArgumentException("The picture's pixels do not match its size.");
        _imageSources.Add((rgba, width, height));
        UploadImages();
        WriteDescriptors();
        return _imageSources.Count - 1;
    }

    // Draws a picture from AddImage stretched to the box. tint multiplies its colors and alpha, so (1, 1, 1, a) fades it.
    public void Image(int image, float x, float y, float width, float height, float4 tint)
    {
        var r = _imageRects[image];
        ImageQuad(x, y, x + width, y + height, r.X, r.Y, r.X + r.Width, r.Y + r.Height, tint);
    }

    public float MeasureText(string text)
    {
        float width = 0f;
        foreach (char c in text)
            width += GlyphFor(c).Advance;
        return width;
    }

    // Draws one line of text with its top edge at y.
    public void Text(string text, float x, float y, float4 color)
    {
        float baseline = y + _fontSize * BaselineFactor;
        float pen = x;
        foreach (char c in text)
        {
            var g = GlyphFor(c);
            float left = pen + g.XOff;
            float top = baseline + g.YOff;
            if (g.Width > 0 && g.Height > 0)
                Quad(left, top, left + g.Width, top + g.Height, g.X0, g.Y0, g.X1, g.Y1, color);
            pen += g.Advance;
        }
    }

    // A straight line of the given thickness in pixels, at any angle.
    public void Line(float2 a, float2 b, float thickness, float4 color)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 1e-4f) return;
        float nx = -dy / length * thickness * 0.5f, ny = dx / length * thickness * 0.5f;
        var p0 = new float2(a.X + nx, a.Y + ny);
        var p1 = new float2(b.X + nx, b.Y + ny);
        var p2 = new float2(b.X - nx, b.Y - ny);
        var p3 = new float2(a.X - nx, a.Y - ny);
        Triangle(p0, p1, p2, color);
        Triangle(p0, p2, p3, color);
    }

    public void Triangle(float2 a, float2 b, float2 c, float4 color)
    {
        if (!Room(3)) return;
        Vertex(a.X, a.Y, -1f, -1f, color);
        Vertex(b.X, b.Y, -1f, -1f, color);
        Vertex(c.X, c.Y, -1f, -1f, color);
    }

    public void Circle(float2 center, float radius, float4 color, int segments = 16)
    {
        for (int i = 0; i < segments; i++)
        {
            float a0 = i * MathF.Tau / segments, a1 = (i + 1) * MathF.Tau / segments;
            Triangle(center,
                new float2(center.X + MathF.Cos(a0) * radius, center.Y + MathF.Sin(a0) * radius),
                new float2(center.X + MathF.Cos(a1) * radius, center.Y + MathF.Sin(a1) * radius), color);
        }
    }

    private Glyph GlyphFor(char c) =>
        c < FirstChar || c > LastChar ? _glyphs[' ' - FirstChar] : _glyphs[c - FirstChar];

    // An axis-aligned quad, cut to the clip box. Texture coordinates are cut in proportion, so clipped text stays right.
    private void Quad(float x0, float y0, float x1, float y1,
        float u0, float v0, float u1, float v1, float4 color)
    {
        if (_clip is { } c)
        {
            float cx0 = Math.Max(x0, c.X), cy0 = Math.Max(y0, c.Y);
            float cx1 = Math.Min(x1, c.X + c.W), cy1 = Math.Min(y1, c.Y + c.H);
            if (cx1 <= cx0 || cy1 <= cy0) return;
            if (u0 >= 0f)
            {
                float du = (u1 - u0) / (x1 - x0), dv = (v1 - v0) / (y1 - y0);
                (u0, u1) = (u0 + (cx0 - x0) * du, u0 + (cx1 - x0) * du);
                (v0, v1) = (v0 + (cy0 - y0) * dv, v0 + (cy1 - y0) * dv);
            }
            (x0, y0, x1, y1) = (cx0, cy0, cx1, cy1);
        }

        if (!Room(6)) return;
        Vertex(x0, y0, u0, v0, color);
        Vertex(x1, y0, u1, v0, color);
        Vertex(x1, y1, u1, v1, color);
        Vertex(x0, y0, u0, v0, color);
        Vertex(x1, y1, u1, v1, color);
        Vertex(x0, y1, u0, v1, color);
    }

    // The same for a picture. Its texture coordinates are positions in the image buffer, stored as -(2 + position) in x so
    // the shader can tell them from glyphs (x of 0 or more) and solid fills (x of -1).
    private void ImageQuad(float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, float4 color)
    {
        if (_clip is { } c)
        {
            float cx0 = Math.Max(x0, c.X), cy0 = Math.Max(y0, c.Y);
            float cx1 = Math.Min(x1, c.X + c.W), cy1 = Math.Min(y1, c.Y + c.H);
            if (cx1 <= cx0 || cy1 <= cy0) return;
            float du = (u1 - u0) / (x1 - x0), dv = (v1 - v0) / (y1 - y0);
            (u0, u1) = (u0 + (cx0 - x0) * du, u0 + (cx1 - x0) * du);
            (v0, v1) = (v0 + (cy0 - y0) * dv, v0 + (cy1 - y0) * dv);
            (x0, y0, x1, y1) = (cx0, cy0, cx1, cy1);
        }

        if (!Room(6)) return;
        Vertex(x0, y0, -2f - u0, v0, color);
        Vertex(x1, y0, -2f - u1, v0, color);
        Vertex(x1, y1, -2f - u1, v1, color);
        Vertex(x0, y0, -2f - u0, v0, color);
        Vertex(x1, y1, -2f - u1, v1, color);
        Vertex(x0, y1, -2f - u0, v1, color);
    }

    // Makes room for n more vertices in the current layer. False when the frame is full.
    private bool Room(int n)
    {
        if (_vertexCount + n > MaxVertices) return false;
        ref var layer = ref _layers[_layer];
        int needed = (_layerCounts[_layer] + n) * FloatsPerVertex;
        if (needed > layer.Length)
            Array.Resize(ref layer, Math.Max(needed, layer.Length * 2));
        return true;
    }

    private void Vertex(float x, float y, float u, float v, float4 color)
    {
        var layer = _layers[_layer];
        int i = _layerCounts[_layer]++ * FloatsPerVertex;
        _vertexCount++;
        layer[i + 0] = x;
        layer[i + 1] = y;
        layer[i + 2] = u;
        layer[i + 3] = v;
        layer[i + 4] = color.X;
        layer[i + 5] = color.Y;
        layer[i + 6] = color.Z;
        layer[i + 7] = color.W;
    }

    // ---- drawing ---------------------------------------------------------------------

    // Records this frame's shapes into the UI render pass. width and height use the same pixels as Rect and Text;
    // pixelScale converts them to framebuffer pixels (2 on a Retina display), so the canvas covers the whole window.
    public void Render(CommandBuffer cmd, float width, float height, float2 pixelScale)
    {
        if (_vertexCount == 0) return;

        // The window moved to a display with a different pixel density: rasterize the glyphs again to match.
        float scale = Math.Clamp(Math.Max(pixelScale.X, pixelScale.Y), 1f, 4f);
        if (Math.Abs(scale - _bakedScale) > 0.01f)
            Bake(scale);

        int slot = _frameSlot;
        _frameSlot = (_frameSlot + 1) % VulkanContext.FramesInFlight;

        int offset = 0;
        for (int layer = 0; layer < LayerCount; layer++)
        {
            int floats = _layerCounts[layer] * FloatsPerVertex;
            Array.Copy(_layers[layer], 0, _upload, offset, floats);
            offset += floats;
        }
        _device.WriteMemory<float>(new ReadOnlySpan<float>(_upload, 0, offset), _vertexMemories[slot], default, default);

        _push[0] = width;
        _push[1] = height;
        _push[2] = _atlasSize;
        _push[3] = _imageStride;

        cmd.BindPipeline((VkPipelineBindPoint)0, _pipeline);
        float pixelW = width * pixelScale.X, pixelH = height * pixelScale.Y;
        cmd.SetViewport(0, new[] { new VkViewport { Width = pixelW, Height = pixelH, MinDepth = 0, MaxDepth = 1 } });
        cmd.SetScissor(0, new[] { new VkRect2D { Extent = new VkExtent2D { Width = (int)MathF.Ceiling(pixelW), Height = (int)MathF.Ceiling(pixelH) } } });
        cmd.BindDescriptorSets((VkPipelineBindPoint)0, _layout, 0, new[] { _set }, default);
        cmd.PushConstants(_layout, (VkShaderStageFlags)StageVertexFragment, default, _push);
        cmd.BindVertexBuffer(0, _vertexBuffers[slot], default);
        cmd.Draw(_vertexCount, 1, 0, 0);
    }

    // ---- Vulkan setup ----------------------------------------------------------------

    private VkDescriptorSetLayout CreateSetLayout()
    {
        // Binding 0 is the glyph atlas and binding 1 the pictures, both read by the fragment shader.
        var bindings = stackalloc VkDescriptorSetLayoutBinding[2];
        for (int i = 0; i < 2; i++)
            bindings[i] = new VkDescriptorSetLayoutBinding
            {
                Binding = i,
                DescriptorType = (VkDescriptorType)DescriptorStorageBuffer,
                DescriptorCount = 1,
                StageFlags = (VkShaderStageFlags)StageFragment,
            };
        var info = new VkDescriptorSetLayoutCreateInfo { BindingCount = 2, Bindings = bindings };
        return _device.CreateDescriptorSetLayout(in info, null);
    }

    private VkDescriptorPool CreateDescriptorPool()
    {
        var poolSize = new VkDescriptorPoolSize { Type = (VkDescriptorType)DescriptorStorageBuffer, DescriptorCount = 2 };
        var poolInfo = new VkDescriptorPoolCreateInfo { MaxSets = 1, PoolSizeCount = 1, PoolSizes = &poolSize };
        return _device.CreateDescriptorPool(in poolInfo, null);
    }

    private VkDescriptorSet AllocateDescriptorSet()
    {
        var layout = _setLayout;
        var allocInfo = new VkDescriptorSetAllocateInfo
        {
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            SetLayouts = &layout,
        };
        VkDescriptorSet set;
        var allocate = (delegate* unmanaged<VkDevice, VkDescriptorSetAllocateInfo*, VkDescriptorSet*, int>)
            _ctx.DeviceEx.GetProcAddr("vkAllocateDescriptorSets"u8).Invoke;
        int result = allocate(_device.Handle, &allocInfo, &set);
        if (result != 0)
            throw new InvalidOperationException($"vkAllocateDescriptorSets failed with {result}");
        return set;
    }

    // Points the descriptor set at the current glyph atlas and picture buffers.
    private void WriteDescriptors()
    {
        var atlasInfo = new VkDescriptorBufferInfo { Buffer = _atlasBuffer, Offset = (ByteSize64)0, Range = (ByteSize64)_atlasBytes };
        var imageInfo = new VkDescriptorBufferInfo { Buffer = _imageBuffer, Offset = (ByteSize64)0, Range = (ByteSize64)_imageBytes };
        var writes = new[]
        {
            new VkWriteDescriptorSet
            {
                DstSet = _set, DstBinding = 0, DescriptorCount = 1,
                DescriptorType = (VkDescriptorType)DescriptorStorageBuffer, BufferInfo = &atlasInfo,
            },
            new VkWriteDescriptorSet
            {
                DstSet = _set, DstBinding = 1, DescriptorCount = 1,
                DescriptorType = (VkDescriptorType)DescriptorStorageBuffer, BufferInfo = &imageInfo,
            },
        };
        _device.UpdateDescriptorSets(writes, default);
    }

    // Packs every picture into one buffer: stacked top to bottom, each row as long as the widest picture, with two empty
    // rows between pictures so smoothing at an edge never reads the next one. Replaces the old buffer.
    private void UploadImages()
    {
        int stride = Math.Max(1, _imageSources.Select(i => i.Width).DefaultIfEmpty(1).Max());
        _imageRects.Clear();
        int rows = 0;
        foreach (var (_, w, h) in _imageSources)
        {
            _imageRects.Add((0, rows, w, h));
            rows += h + 2;
        }
        rows = Math.Max(rows, 1);

        var pixels = new uint[stride * rows];
        for (int n = 0; n < _imageSources.Count; n++)
        {
            var (rgba, w, h) = _imageSources[n];
            var rect = _imageRects[n];
            for (int y = 0; y < h; y++)
                System.Buffer.BlockCopy(rgba, y * w * 4, pixels, ((rect.Y + y) * stride) * 4, w * 4);
        }

        if (_hasImageBuffer)
        {
            // Frames in flight may still read the old buffer.
            _ctx.WaitIdle();
            _device.DestroyBuffer(_imageBuffer, null);
            _device.FreeMemory(_imageMemory, null);
        }
        (_imageBuffer, _imageMemory) = CreateHostBuffer(_device, _physical, (VkBufferUsageFlags)BufferStorage,
            (ulong)(pixels.Length * sizeof(uint)));
        _device.WriteMemory<uint>(pixels, _imageMemory, default, default);
        _imageBytes = (ulong)(pixels.Length * sizeof(uint));
        _imageStride = stride;
        _hasImageBuffer = true;
    }

    private VkPipelineLayout CreateLayout()
    {
        var range = new VkPushConstantRange
        {
            StageFlags = (VkShaderStageFlags)StageVertexFragment,
            Offset = (ByteSize32)0,
            Size = (ByteSize32)(4 * sizeof(float)),
        };
        return _device.CreatePipelineLayout(new[] { _setLayout }, new[] { range }, null);
    }

    private VkPipeline CreatePipeline(ShaderLibrary shaders)
    {
        var vert = shaders.Get("canvas2d.vert", VertexGlsl, ShaderKind.VertexShader);
        var frag = shaders.Get("canvas2d.frag", FragmentGlsl, ShaderKind.FragmentShader);
        byte[] mainName = "main\0"u8.ToArray();

        fixed (byte* entry = mainName)
        {
            var stages = new[]
            {
                new VkPipelineShaderStageCreateInfo { Stage = (VkShaderStageFlags)0x1, Module = vert, Name = entry },
                new VkPipelineShaderStageCreateInfo { Stage = (VkShaderStageFlags)0x10, Module = frag, Name = entry },
            };
            var bindings = new[]
            {
                new VkVertexInputBindingDescription { Binding = 0, Stride = (ByteSize32)(FloatsPerVertex * sizeof(float)), InputRate = (VkVertexInputRate)0 },
            };
            var attributes = new[]
            {
                new VkVertexInputAttributeDescription { Location = 0, Binding = 0, Format = (VkFormat)103, Offset = (ByteSize32)0 },
                new VkVertexInputAttributeDescription { Location = 1, Binding = 0, Format = (VkFormat)103, Offset = (ByteSize32)8 },
                new VkVertexInputAttributeDescription { Location = 2, Binding = 0, Format = (VkFormat)109, Offset = (ByteSize32)16 },
            };
            var viewport = new VkViewport { Width = 1, Height = 1, MinDepth = 0, MaxDepth = 1 };
            var scissor = new VkRect2D { Extent = new VkExtent2D { Width = 1, Height = 1 } };
            var dynamicStates = new[] { (VkDynamicState)0, (VkDynamicState)1 };
            var blend = new VkPipelineColorBlendAttachmentState
            {
                BlendEnable = true,
                SrcColorBlendFactor = (VkBlendFactor)6,      // SRC_ALPHA
                DstColorBlendFactor = (VkBlendFactor)7,      // ONE_MINUS_SRC_ALPHA
                ColorBlendOp = (VkBlendOp)0,
                SrcAlphaBlendFactor = (VkBlendFactor)1,      // ONE
                DstAlphaBlendFactor = (VkBlendFactor)7,
                AlphaBlendOp = (VkBlendOp)0,
                ColorWriteMask = (VkColorComponentFlags)0xF,
            };

            fixed (VkPipelineShaderStageCreateInfo* stagesPtr = stages)
            fixed (VkVertexInputBindingDescription* bindingsPtr = bindings)
            fixed (VkVertexInputAttributeDescription* attributesPtr = attributes)
            fixed (VkDynamicState* dynamicStatesPtr = dynamicStates)
            {
                var vertexInput = new VkPipelineVertexInputStateCreateInfo
                {
                    VertexBindingDescriptionCount = 1,
                    VertexBindingDescriptions = bindingsPtr,
                    VertexAttributeDescriptionCount = attributes.Length,
                    VertexAttributeDescriptions = attributesPtr,
                };
                var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = (VkPrimitiveTopology)3 };
                var viewportState = new VkPipelineViewportStateCreateInfo
                {
                    ViewportCount = 1, Viewports = &viewport,
                    ScissorCount = 1, Scissors = &scissor,
                };
                var raster = new VkPipelineRasterizationStateCreateInfo
                {
                    PolygonMode = (VkPolygonMode)0,
                    CullMode = (VkCullModeFlags)0,
                    FrontFace = (VkFrontFace)0,
                    LineWidth = 1f,
                };
                var multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = (VkSampleCountFlags)1 };
                var colorBlend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &blend };
                var dynamicState = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 2, DynamicStates = dynamicStatesPtr };

                var info = new VkGraphicsPipelineCreateInfo
                {
                    StageCount = stages.Length,
                    Stages = stagesPtr,
                    VertexInputState = &vertexInput,
                    InputAssemblyState = &inputAssembly,
                    ViewportState = &viewportState,
                    DynamicState = &dynamicState,
                    RasterizationState = &raster,
                    MultisampleState = &multisample,
                    ColorBlendState = &colorBlend,
                    Layout = _layout,
                    RenderPass = _ctx.UiRenderPass,
                    Subpass = 0,
                };
                return _device.CreateGraphicsPipeline(default, info, null);
            }
        }
    }

    // Host-visible and coherent, so the CPU can write it directly. Same approach as the engine's own buffers.
    private static (VkBuffer Buffer, VkDeviceMemory Memory) CreateHostBuffer(
        Device device, PhysicalDevice physical, VkBufferUsageFlags usage, ulong size)
    {
        var info = new VkBufferCreateInfo
        {
            Size = (ByteSize64)size,
            Usage = usage,
            SharingMode = (VkSharingMode)0,
        };
        var buffer = device.CreateBuffer(in info, null);
        var memory = device.AllocateMemory(physical, buffer, (VkMemoryPropertyFlags)6, null);
        device.BindBufferMemory(buffer, memory, (ByteSize64)0);
        return (buffer, memory);
    }

    public void Dispose()
    {
        _ctx.WaitIdle();
        _device.DestroyPipeline(_pipeline, null);
        _device.DestroyPipelineLayout(_layout, null);
        _device.DestroyDescriptorPool(_pool, null);
        _device.DestroyDescriptorSetLayout(_setLayout, null);
        _device.DestroyBuffer(_atlasBuffer, null);
        _device.FreeMemory(_atlasMemory, null);
        _device.DestroyBuffer(_imageBuffer, null);
        _device.FreeMemory(_imageMemory, null);
        for (int slot = 0; slot < VulkanContext.FramesInFlight; slot++)
        {
            _device.DestroyBuffer(_vertexBuffers[slot], null);
            _device.FreeMemory(_vertexMemories[slot], null);
        }
    }
}
