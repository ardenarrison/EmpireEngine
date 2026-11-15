using Engine.Brutal.Gfx;
using Engine.Brutal.Ui;

namespace Engine.Brutal.Metal;

// Canvas2D's GPU part with Metal: a shared vertex buffer per frame in flight, the atlas and pictures as buffers the fragment shader
// reads, and one alpha-blended pipeline for the UI pass.
internal sealed class MetalCanvasGpu : ICanvasGpu
{
    private static readonly int AtlasIndex = SpirvToMsl.BufferIndex(0, 0);
    private static readonly int ImagesIndex = SpirvToMsl.BufferIndex(0, 1);

    private readonly MetalContext _ctx;
    private readonly nint _pipeline;
    private readonly nint[] _vertexBuffers = new nint[MetalContext.FramesInFlight];
    private readonly uint[] _sizes = new uint[SpirvToMsl.BufferSizesIndex];   // byte sizes of the fragment shader's buffers, by index
    private nint _atlas, _images;

    public MetalCanvasGpu(MetalContext ctx, ShaderLibrary shaders)
    {
        _ctx = ctx;
        for (int slot = 0; slot < _vertexBuffers.Length; slot++)
            _vertexBuffers[slot] = MetalGpu.Buffer(ctx.Device, (long)Canvas2D.MaxVertices * Canvas2D.FloatsPerVertex * sizeof(float));
        _pipeline = MetalGpu.Pipeline(ctx.Device, "Canvas2D", ctx.Function(shaders, Canvas2D.VertexShader), ctx.Function(shaders, Canvas2D.FragmentShader),
            MetalContext.ColorFormat, depth: false, samples: 1, MetalBlend.Alpha,
            [
                new(0, MetalGpu.VertexFloat2, 0),
                new(1, MetalGpu.VertexFloat2, 8),
                new(2, MetalGpu.VertexFloat4, 16),
            ], Canvas2D.FloatsPerVertex * sizeof(float));
    }

    // A frame being drawn keeps the old buffer alive, so it can be replaced at once.
    public void SetAtlas(float[] coverage)
    {
        ObjC.Release(_atlas);
        _atlas = MetalGpu.Buffer(_ctx.Device, coverage.Length * sizeof(float));
        MetalGpu.Write<float>(_atlas, coverage);
        _sizes[AtlasIndex] = (uint)(coverage.Length * sizeof(float));
    }

    public void SetImages(uint[] pixels)
    {
        ObjC.Release(_images);
        _images = MetalGpu.Buffer(_ctx.Device, pixels.Length * sizeof(uint));
        MetalGpu.Write<uint>(_images, pixels);
        _sizes[ImagesIndex] = (uint)(pixels.Length * sizeof(uint));
    }

    public void Draw(UiPass pass, ReadOnlySpan<float> vertices, int vertexCount, float[] push, float pixelWidth, float pixelHeight)
    {
        nint encoder = pass.MetalEncoder;
        nint buffer = _vertexBuffers[_ctx.FrameIndex];   // this slot's last frame has finished, so its buffer is free to rewrite
        MetalGpu.Write(buffer, vertices);

        MetalGpu.SetPipeline(encoder, _pipeline);
        MetalGpu.Viewport(encoder, 0, 0, pixelWidth, pixelHeight);
        MetalGpu.Scissor(encoder, 0, 0, Math.Min((int)MathF.Ceiling(pixelWidth), _ctx.Width), Math.Min((int)MathF.Ceiling(pixelHeight), _ctx.Height));
        MetalGpu.Bytes<float>(encoder, push, SpirvToMsl.PushConstantIndex);
        MetalGpu.VertexBuffer(encoder, buffer);
        MetalGpu.FragmentBuffer(encoder, _atlas, AtlasIndex);
        MetalGpu.FragmentBuffer(encoder, _images, ImagesIndex);
        MetalGpu.Bytes<uint>(encoder, _sizes, SpirvToMsl.BufferSizesIndex, vertex: false);
        MetalGpu.Draw(encoder, vertexCount);
    }

    public void Dispose()
    {
        ObjC.Release(_pipeline);
        ObjC.Release(_atlas);
        ObjC.Release(_images);
        foreach (var buffer in _vertexBuffers)
            ObjC.Release(buffer);
    }
}
