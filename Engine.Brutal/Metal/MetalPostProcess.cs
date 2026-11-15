using Engine.Brutal.Gfx;
using Engine.Brutal.Rendering;

namespace Engine.Brutal.Metal;

// FXAA or SMAA and the color effects with Metal, as PostProcessPass does them with Vulkan, from the same shaders. MetalContext draws
// the scene into its own image when any of them is on, then calls Record, which reads that image and draws the finished picture
// into the window's image (clearing the rest of it). With both anti-aliasing and effects, the anti-aliased picture goes into an image
// of its own first and the effects pass reads that. Everything is made the first time it is needed.
internal sealed class MetalPostProcess : IDisposable
{
    private const int PushFloats = 12;
    private static readonly MTLClearColor Transparent = new(0, 0, 0, 0);

    private readonly MetalContext _ctx;
    private readonly ShaderLibrary _shaders;
    private readonly float[] _push = new float[PushFloats];
    private nint _linear, _point;
    private nint _fxaa, _effects, _smaaEdges, _smaaWeights, _smaaBlend;
    private nint _area, _search, _edges, _weights, _graded;
    private int _targetWidth, _targetHeight;

    public MetalPostProcess(MetalContext ctx, ShaderLibrary shaders)
    {
        _ctx = ctx;
        _shaders = shaders;
    }

    public void Record(nint cmd, PostAntiAliasing mode, PostEffects effects)
    {
        bool fx = effects.Any;
        if (mode == PostAntiAliasing.None && !fx) return;
        EnsureTargets(mode, fx);

        int width = _ctx.Width, height = _ctx.Height;
        var rect = _ctx.SceneRectPixels;
        _push[0] = 1f / width;
        _push[1] = 1f / height;
        _push[2] = width;
        _push[3] = height;
        _push[4] = (float)rect.X / width;
        _push[5] = (float)rect.Y / height;
        _push[6] = (float)rect.Width / width;
        _push[7] = (float)rect.Height / height;
        _push[8] = MathF.Pow(2f, Math.Clamp(effects.Exposure, -10f, 10f));
        _push[9] = Math.Clamp(effects.Contrast, 0f, 4f);
        _push[10] = Math.Clamp(effects.Vignette, 0f, 1f);
        _push[11] = Math.Clamp(effects.Sharpen, 0f, 1f);

        nint scene = _ctx.SceneColorTexture;
        // The anti-aliased picture goes on screen, or into _graded when the effects pass comes after.
        nint output = fx ? _graded : _ctx.TargetTexture;
        if (mode == PostAntiAliasing.Fxaa)
        {
            Draw(cmd, output, MetalContext.Backdrop, rect, Pipeline(ref _fxaa, PostProcessShaders.Fxaa, MetalContext.ColorFormat),
                (scene, _linear), (scene, _linear), (scene, _linear));
        }
        else if (mode == PostAntiAliasing.Smaa)
        {
            // SMAA: where the edges are, then how much of each neighbor to blend across them, then the blend itself.
            Draw(cmd, _edges, Transparent, rect, Pipeline(ref _smaaEdges, PostProcessShaders.SmaaEdges, MetalGpu.FormatRg8),
                (scene, _linear), (scene, _linear), (scene, _linear));
            Draw(cmd, _weights, Transparent, rect, Pipeline(ref _smaaWeights, PostProcessShaders.SmaaWeights, MetalGpu.FormatRgba8),
                (_edges, _linear), (_area, _point), (_search, _point));
            Draw(cmd, output, MetalContext.Backdrop, rect, Pipeline(ref _smaaBlend, PostProcessShaders.SmaaBlend, MetalContext.ColorFormat),
                (scene, _linear), (_weights, _linear), (scene, _linear));
        }

        if (fx)
        {
            nint source = mode == PostAntiAliasing.None ? scene : _graded;
            Draw(cmd, _ctx.TargetTexture, MetalContext.Backdrop, rect, Pipeline(ref _effects, PostProcessShaders.Effects, MetalContext.ColorFormat),
                (source, _linear), (source, _linear), (source, _linear));
        }
    }

    // One full-screen triangle into a target, inside the scene's rectangle; the rest of the target is cleared.
    private void Draw(nint cmd, nint target, MTLClearColor clear, (int X, int Y, int Width, int Height) rect, nint pipeline,
        params (nint Texture, nint Sampler)[] inputs)
    {
        nint encoder = MetalGpu.Encoder(cmd, MetalGpu.PassDescriptor(target, MetalGpu.LoadClear, clear), "Post-process");
        // The viewport is the whole target, so the triangle's texture coordinates line up with the scene image pixel for pixel; only
        // the scissor limits the drawing to the scene's rectangle.
        MetalGpu.Viewport(encoder, 0, 0, _ctx.Width, _ctx.Height);
        MetalGpu.Scissor(encoder, rect.X, rect.Y, rect.Width, rect.Height);
        MetalGpu.SetPipeline(encoder, pipeline);
        for (int i = 0; i < inputs.Length; i++)
            MetalGpu.FragmentTexture(encoder, inputs[i].Texture, inputs[i].Sampler, SpirvToMsl.TextureIndex(0, i));
        MetalGpu.Bytes<float>(encoder, _push, SpirvToMsl.PushConstantIndex);
        MetalGpu.Draw(encoder, 3);
        MetalGpu.End(encoder);
    }

    private nint Pipeline(ref nint cache, PostProcessShaders.Pass pass, nuint format)
    {
        if (cache == 0)
            cache = MetalGpu.Pipeline(_ctx.Device, pass.Vertex.Name, _ctx.Function(_shaders, pass.Vertex), _ctx.Function(_shaders, pass.Fragment),
                format, depth: false, samples: 1, MetalBlend.None);
        return cache;
    }

    // Makes what the chosen settings need, at the window's size.
    private void EnsureTargets(PostAntiAliasing mode, bool effects)
    {
        if (_linear == 0)
        {
            _linear = MetalGpu.Sampler(_ctx.Device, linear: true, mipmaps: false, repeatU: false, repeatV: false);
            _point = MetalGpu.Sampler(_ctx.Device, linear: false, mipmaps: false, repeatU: false, repeatV: false);
        }
        if (_targetWidth != _ctx.Width || _targetHeight != _ctx.Height)
        {
            ReleaseIntermediates();
            _targetWidth = _ctx.Width;
            _targetHeight = _ctx.Height;
        }
        bool smaa = mode == PostAntiAliasing.Smaa;
        if (smaa && _edges == 0)
        {
            _edges = Target(MetalGpu.FormatRg8);
            _weights = Target(MetalGpu.FormatRgba8);
        }
        if (effects && mode != PostAntiAliasing.None && _graded == 0)
            _graded = Target(MetalContext.ColorFormat);
        if (smaa && _area == 0)
        {
            _area = MetalGpu.Texture(_ctx.Device, MetalGpu.FormatRg8, 160, 560);
            MetalGpu.Upload(_area, 0, 160, 560, PostProcessShaders.AreaTexture, 2);
            _search = MetalGpu.Texture(_ctx.Device, MetalGpu.FormatR8, 64, 16);
            MetalGpu.Upload(_search, 0, 64, 16, PostProcessShaders.SearchTexture, 1);
        }
    }

    private nint Target(nuint format) =>
        MetalGpu.Texture(_ctx.Device, format, _ctx.Width, _ctx.Height, usage: MetalGpu.UsageRenderTarget | MetalGpu.UsageRead,
            storage: MetalGpu.StoragePrivate);

    private void ReleaseIntermediates()
    {
        ObjC.Release(_edges);
        ObjC.Release(_weights);
        ObjC.Release(_graded);
        _edges = _weights = _graded = 0;
    }

    public void Dispose()
    {
        ReleaseIntermediates();
        foreach (var o in new[] { _fxaa, _effects, _smaaEdges, _smaaWeights, _smaaBlend, _area, _search, _linear, _point })
            ObjC.Release(o);
    }
}
