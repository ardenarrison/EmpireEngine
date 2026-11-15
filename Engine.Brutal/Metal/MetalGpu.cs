using System.Runtime.InteropServices;

namespace Engine.Brutal.Metal;

// How a pipeline blends what it draws over what is there.
internal enum MetalBlend { None, Alpha, Additive }

// One vertex attribute: its shader location, Metal vertex format and byte offset in the vertex.
internal readonly record struct MetalVertexAttribute(int Location, nuint Format, int Offset);

// Small helpers for making Metal objects. Everything returned is owned by the caller (release it with ObjC.Release).
internal static unsafe class MetalGpu
{
    // MTLPixelFormat
    public const nuint FormatR8 = 10, FormatRg8 = 30, FormatRgba8 = 70, FormatBgra8 = 80, FormatRgba16F = 115, FormatDepth32 = 252;
    // MTLVertexFormat
    public const nuint VertexFloat = 28, VertexFloat2 = 29, VertexFloat3 = 30, VertexFloat4 = 31;
    // MTLTextureUsage
    public const nuint UsageRead = 1, UsageRenderTarget = 4;
    // MTLStorageMode
    public const nuint StorageShared = 0, StoragePrivate = 2;
    // MTLLoadAction, MTLStoreAction
    public const nuint LoadDontCare = 0, LoadLoad = 1, LoadClear = 2;
    public const nuint StoreDontCare = 0, StoreStore = 1, StoreResolve = 2;
    public const nuint PrimitiveTriangle = 3;
    public const nuint IndexUInt32 = 1;

    // A buffer the CPU writes and the GPU reads, in memory both share.
    public static nint Buffer(nint device, long bytes) =>
        ObjC.SendU(device, "newBufferWithLength:options:", (nuint)Math.Max(bytes, 16), StorageShared);

    public static void Write<T>(nint buffer, ReadOnlySpan<T> data, long byteOffset = 0) where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(data);
        bytes.CopyTo(new Span<byte>((byte*)ObjC.Send(buffer, "contents") + byteOffset, bytes.Length));
    }

    public static long Length(nint buffer) => (long)ObjC.SendNUInt(buffer, "length");

    public static nint Texture(nint device, nuint format, int width, int height, int mips = 1, nuint usage = UsageRead,
        nuint? storage = null, int samples = 1)
    {
        using var pool = AutoreleasePool.Open();
        nint desc = ObjC.SendTexture(ObjC.Class("MTLTextureDescriptor"), "texture2DDescriptorWithPixelFormat:width:height:mipmapped:",
            format, (nuint)Math.Max(width, 1), (nuint)Math.Max(height, 1), 0);
        if (samples > 1)
        {
            ObjC.CallU(desc, "setTextureType:", 4);   // MTLTextureType2DMultisample
            ObjC.CallU(desc, "setSampleCount:", (nuint)samples);
        }
        ObjC.CallU(desc, "setMipmapLevelCount:", (nuint)Math.Max(mips, 1));
        ObjC.CallU(desc, "setUsage:", usage);
        if (storage is { } mode) ObjC.CallU(desc, "setStorageMode:", mode);
        nint texture = ObjC.Send(device, "newTextureWithDescriptor:", desc);
        return texture != 0 ? texture : throw new InvalidOperationException($"Metal could not make a {width} x {height} texture.");
    }

    // Fills one mip level of a texture from tightly packed pixels.
    public static void Upload(nint texture, int level, int width, int height, ReadOnlySpan<byte> pixels, int bytesPerPixel)
    {
        fixed (byte* p = pixels)
            ObjC.Call(texture, "replaceRegion:mipmapLevel:withBytes:bytesPerRow:", new MTLRegion(width, height), (nuint)level, (nint)p,
                (nuint)(width * bytesPerPixel));
    }

    // linear: bilinear filtering (and trilinear between mips when mipmaps is true). repeatU / repeatV wrap; otherwise clamp to the edge.
    public static nint Sampler(nint device, bool linear, bool mipmaps, bool repeatU, bool repeatV)
    {
        nint desc = ObjC.New("MTLSamplerDescriptor");
        try
        {
            nuint filter = linear ? 1u : 0u;
            ObjC.CallU(desc, "setMinFilter:", filter);
            ObjC.CallU(desc, "setMagFilter:", filter);
            ObjC.CallU(desc, "setMipFilter:", mipmaps ? 2u : 0u);   // linear between mips, or none
            ObjC.CallU(desc, "setSAddressMode:", repeatU ? 2u : 0u);
            ObjC.CallU(desc, "setTAddressMode:", repeatV ? 2u : 0u);
            ObjC.CallU(desc, "setRAddressMode:", 0);
            ObjC.CallFloat(desc, "setLodMaxClamp:", mipmaps ? 16f : 0f);
            return ObjC.Send(device, "newSamplerStateWithDescriptor:", desc);
        }
        finally
        {
            ObjC.Release(desc);
        }
    }

    public static nint DepthState(nint device, bool test, bool write)
    {
        nint desc = ObjC.New("MTLDepthStencilDescriptor");
        try
        {
            ObjC.CallU(desc, "setDepthCompareFunction:", test ? 1u : 7u);   // less, or always
            ObjC.CallBool(desc, "setDepthWriteEnabled:", write);
            return ObjC.Send(device, "newDepthStencilStateWithDescriptor:", desc);
        }
        finally
        {
            ObjC.Release(desc);
        }
    }

    // A render pipeline. colorFormat 0 means no color output (the shadow pass); attributes null means the vertex shader makes its own
    // positions (full-screen triangles). Vertex data comes from buffer SpirvToMsl.VertexBufferIndex.
    public static nint Pipeline(nint device, string label, nint vertex, nint fragment, nuint colorFormat, bool depth, int samples,
        MetalBlend blend, MetalVertexAttribute[]? attributes = null, int stride = 0, bool perInstance = false)
    {
        using var pool = AutoreleasePool.Open();
        nint desc = ObjC.New("MTLRenderPipelineDescriptor");
        try
        {
            nint name = ObjC.String(label);
            ObjC.Call(desc, "setLabel:", name);
            ObjC.Release(name);
            ObjC.Call(desc, "setVertexFunction:", vertex);
            if (fragment != 0) ObjC.Call(desc, "setFragmentFunction:", fragment);
            ObjC.CallU(desc, "setRasterSampleCount:", (nuint)Math.Max(samples, 1));
            if (depth) ObjC.CallU(desc, "setDepthAttachmentPixelFormat:", FormatDepth32);

            if (colorFormat != 0)
            {
                nint color = ObjC.SendU(ObjC.Send(desc, "colorAttachments"), "objectAtIndexedSubscript:", 0);
                ObjC.CallU(color, "setPixelFormat:", colorFormat);
                if (blend != MetalBlend.None)
                {
                    // Factors: 0 zero, 1 one, 4 source alpha, 5 one minus source alpha.
                    bool additive = blend == MetalBlend.Additive;
                    ObjC.CallBool(color, "setBlendingEnabled:", true);
                    ObjC.CallU(color, "setSourceRGBBlendFactor:", 4);
                    ObjC.CallU(color, "setDestinationRGBBlendFactor:", additive ? 1u : 5u);
                    ObjC.CallU(color, "setSourceAlphaBlendFactor:", additive ? 0u : 1u);
                    ObjC.CallU(color, "setDestinationAlphaBlendFactor:", additive ? 1u : 5u);
                }
            }

            if (attributes is not null)
            {
                nint vertexDesc = ObjC.Send(ObjC.Class("MTLVertexDescriptor"), "vertexDescriptor");
                nint attrs = ObjC.Send(vertexDesc, "attributes");
                foreach (var a in attributes)
                {
                    nint attr = ObjC.SendU(attrs, "objectAtIndexedSubscript:", (nuint)a.Location);
                    ObjC.CallU(attr, "setFormat:", a.Format);
                    ObjC.CallU(attr, "setOffset:", (nuint)a.Offset);
                    ObjC.CallU(attr, "setBufferIndex:", SpirvToMsl.VertexBufferIndex);
                }
                nint layout = ObjC.SendU(ObjC.Send(vertexDesc, "layouts"), "objectAtIndexedSubscript:", SpirvToMsl.VertexBufferIndex);
                ObjC.CallU(layout, "setStride:", (nuint)stride);
                ObjC.CallU(layout, "setStepFunction:", perInstance ? 2u : 1u);
                ObjC.CallU(layout, "setStepRate:", 1);
                ObjC.Call(desc, "setVertexDescriptor:", vertexDesc);
            }

            nint error = 0;
            nint pipeline = ObjC.Send(device, "newRenderPipelineStateWithDescriptor:error:", desc, (nint)(&error));
            if (pipeline == 0)
                throw new InvalidOperationException($"Metal could not make the {label} pipeline: {ObjC.ErrorText(error)}");
            return pipeline;
        }
        finally
        {
            ObjC.Release(desc);
        }
    }

    // ---- drawing -----------------------------------------------------------------------------------------------------------

    public static void SetPipeline(nint encoder, nint pipeline) => ObjC.Call(encoder, "setRenderPipelineState:", pipeline);
    public static void SetDepth(nint encoder, nint state) => ObjC.Call(encoder, "setDepthStencilState:", state);

    public static void Viewport(nint encoder, double x, double y, double width, double height) =>
        ObjC.Call(encoder, "setViewport:", new MTLViewport(x, y, width, height));

    public static void Scissor(nint encoder, int x, int y, int width, int height) =>
        ObjC.Call(encoder, "setScissorRect:", new MTLScissorRect(x, y, width, height));

    // Small data (at most 4 KB) copied into the command stream, for both stages at once.
    public static void Bytes<T>(nint encoder, ReadOnlySpan<T> data, int index, bool vertex = true, bool fragment = true) where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(data);
        fixed (byte* p = bytes)
        {
            if (vertex) ObjC.Call(encoder, "setVertexBytes:length:atIndex:", (nint)p, (nuint)bytes.Length, (nuint)index);
            if (fragment) ObjC.Call(encoder, "setFragmentBytes:length:atIndex:", (nint)p, (nuint)bytes.Length, (nuint)index);
        }
    }

    public static void VertexBuffer(nint encoder, nint buffer, long offset = 0) =>
        ObjC.Call(encoder, "setVertexBuffer:offset:atIndex:", buffer, (nuint)offset, SpirvToMsl.VertexBufferIndex);

    public static void FragmentBuffer(nint encoder, nint buffer, int index) =>
        ObjC.Call(encoder, "setFragmentBuffer:offset:atIndex:", buffer, 0, (nuint)index);

    public static void FragmentTexture(nint encoder, nint texture, nint sampler, int index)
    {
        ObjC.Call(encoder, "setFragmentTexture:atIndex:", texture, (nuint)index);
        ObjC.Call(encoder, "setFragmentSamplerState:atIndex:", sampler, (nuint)index);
    }

    public static void Draw(nint encoder, int vertexCount, int instanceCount = 1, int baseInstance = 0) =>
        ObjC.CallU(encoder, "drawPrimitives:vertexStart:vertexCount:instanceCount:baseInstance:", PrimitiveTriangle, 0,
            (nuint)vertexCount, (nuint)instanceCount, (nuint)baseInstance);

    public static void DrawIndexed(nint encoder, int indexCount, nint indexBuffer) =>
        ObjC.CallIndexed(encoder, "drawIndexedPrimitives:indexCount:indexType:indexBuffer:indexBufferOffset:", PrimitiveTriangle,
            (nuint)indexCount, IndexUInt32, indexBuffer, 0);

    // A render pass descriptor (autoreleased) drawing into one color texture and, optionally, a depth texture.
    public static nint PassDescriptor(nint color, nuint load, MTLClearColor clear, nint resolve = 0, nint depth = 0, bool keepDepth = false)
    {
        nint pass = ObjC.Send(ObjC.Class("MTLRenderPassDescriptor"), "renderPassDescriptor");
        if (color != 0)
        {
            nint attachment = ObjC.SendU(ObjC.Send(pass, "colorAttachments"), "objectAtIndexedSubscript:", 0);
            ObjC.Call(attachment, "setTexture:", color);
            ObjC.CallU(attachment, "setLoadAction:", load);
            ObjC.Call(attachment, "setClearColor:", clear);
            if (resolve != 0)
            {
                ObjC.Call(attachment, "setResolveTexture:", resolve);
                ObjC.CallU(attachment, "setStoreAction:", StoreResolve);
            }
            else
            {
                ObjC.CallU(attachment, "setStoreAction:", StoreStore);
            }
        }
        if (depth != 0)
        {
            nint attachment = ObjC.Send(pass, "depthAttachment");
            ObjC.Call(attachment, "setTexture:", depth);
            ObjC.CallU(attachment, "setLoadAction:", LoadClear);
            ObjC.CallDouble(attachment, "setClearDepth:", 1.0);
            ObjC.CallU(attachment, "setStoreAction:", keepDepth ? StoreStore : StoreDontCare);
        }
        return pass;
    }

    public static nint Encoder(nint commandBuffer, nint passDescriptor, string label)
    {
        nint encoder = ObjC.Send(commandBuffer, "renderCommandEncoderWithDescriptor:", passDescriptor);
        nint name = ObjC.String(label);
        ObjC.Call(encoder, "setLabel:", name);
        ObjC.Release(name);
        return encoder;
    }

    public static void End(nint encoder) => ObjC.Call(encoder, "endEncoding");
}
