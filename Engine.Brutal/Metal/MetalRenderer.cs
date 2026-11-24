using System.Numerics;
using Engine.Brutal.Gfx;
using Engine.Brutal.Rendering;
using Engine.Core.Animation;
using Engine.Core.Assets;
using Engine.Core.Rendering;
using Engine.Core.Scene;

namespace Engine.Brutal.Metal;

// The scene renderer with Metal. It draws what BrutalRenderer draws, the same way and with the same shaders (translated from SPIR-V,
// see SpirvToMsl): the sky, then solid models, then see-through ones farthest first, then particles, all lit by the sun (with its
// shadow map, drawn first each frame), the scene's other lights, ambient light and the sky. Scripts that draw with Vulkan
// (IVulkanRender) don't run with Metal.
public sealed class MetalRenderer : ISceneRenderer
{
    private const int FramesInFlight = MetalContext.FramesInFlight;
    private static readonly int FrameBlockIndex = SpirvToMsl.BufferIndex(0, 1);
    private static readonly int ShadowMapIndex = SpirvToMsl.TextureIndex(0, 0);
    private static readonly int PanoramaIndex = SpirvToMsl.TextureIndex(0, 2);
    private static readonly int FirstMaterialIndex = SpirvToMsl.TextureIndex(1, 0);

    private static readonly MetalVertexAttribute[] ModelAttributes =
    [
        new(0, MetalGpu.VertexFloat3, 0),
        new(1, MetalGpu.VertexFloat3, 12),
        new(2, MetalGpu.VertexFloat2, 24),
        new(3, MetalGpu.VertexFloat4, 32),
    ];

    private readonly MetalContext _ctx;
    private readonly ShaderLibrary _shaders;
    private readonly MetalMeshCache _meshes;
    private readonly MetalParticles _particles;
    private readonly MetalPostProcess _postProcess;
    private readonly Dictionary<(int Samples, bool Transparent), nint> _pipelines = new();
    private readonly Dictionary<int, nint> _skyPipelines = new();
    private readonly nint _shadowPipeline;
    private readonly float[] _push = new float[SceneShading.PushFloats];
    private readonly float[] _frameData = new float[SceneShading.FrameFloats];
    private Matrix4x4 _viewProjection;
    private Vector3 _eye;

    // Shadow maps: the sun's depth, one per frame in flight, so a frame never overwrites one the GPU is still reading.
    private readonly nint[] _shadowMaps = new nint[FramesInFlight];
    private readonly nint _shadowSampler;
    private int _shadowSize = 2048;
    private Matrix4x4 _lightViewProjection;
    private bool _shadowsOn = true;
    private float _shadowStrength = 1f;
    private float _shadowDistance = 40f;
    private float _shadowTexelDepth;

    private EnvironmentData _environment = EnvironmentData.Default;
    private IReadOnlyList<LightData> _lights = Array.Empty<LightData>();
    private readonly nint _panoramaSampler;
    private readonly MetalTexture _blackPanorama;
    private MetalTexture? _panorama;
    private string _panoramaKey = "";

    private sealed class QueuedDraw
    {
        public required string Key { get; init; }
        public required Matrix4x4 World { get; init; }
        public required Vector4 Tint { get; init; }
        public ModelPose? Pose { get; init; }
        public nint[]? Vertices { get; set; }   // per mesh: the bent copy of the vertices for this frame, or 0 for the original
    }

    private readonly List<QueuedDraw> _draws = new();

    // Vertex buffers for posed meshes, which have new vertices every frame; each frame slot has its own.
    private readonly List<nint>[] _dynamicBuffers = Enumerable.Range(0, FramesInFlight).Select(_ => new List<nint>()).ToArray();
    private readonly Dictionary<int, float[]> _vertexScratch = new();
    private bool _framePending;

    public MetalRenderer(MetalContext ctx, ShaderLibrary shaders)
    {
        _ctx = ctx;
        _shaders = shaders;
        _meshes = new MetalMeshCache(ctx.Device);
        _shadowSampler = MetalGpu.Sampler(ctx.Device, linear: false, mipmaps: false, repeatU: false, repeatV: false);
        // The panorama wraps around horizontally and clamps at the poles, as an equirectangular image needs.
        _panoramaSampler = MetalGpu.Sampler(ctx.Device, linear: true, mipmaps: true, repeatU: true, repeatV: false);
        _blackPanorama = MetalTexture.Solid(ctx.Device, 0, 0, 0, 255);
        CreateShadowMaps();
        _shadowPipeline = MetalGpu.Pipeline(ctx.Device, "Shadow", ctx.Function(shaders, BrutalRenderer.ModelVertexShader), 0, 0,
            depth: true, samples: 1, MetalBlend.None, ModelAttributes, SceneShading.VertexStride);
        PipelineFor(1, transparent: false);
        _particles = new MetalParticles(ctx, shaders);
        _postProcess = new MetalPostProcess(ctx, shaders);
        _ctx.BeforeEndFrame = FlushFrame;
        _ctx.AfterScenePass = cmd => _postProcess.Record(cmd, _ctx.PostAntiAliasing, _ctx.PostEffects);
    }

    // The model pipeline for the scene pass's sample count, solid or see-through (blending by alpha, made the first time it is used).
    private nint PipelineFor(int samples, bool transparent)
    {
        if (!_pipelines.TryGetValue((samples, transparent), out nint pipeline))
            _pipelines[(samples, transparent)] = pipeline = MetalGpu.Pipeline(_ctx.Device, transparent ? "Models (see-through)" : "Models",
                _ctx.Function(_shaders, BrutalRenderer.ModelVertexShader), _ctx.Function(_shaders, BrutalRenderer.ModelFragmentShader),
                MetalContext.ColorFormat, depth: true, samples, transparent ? MetalBlend.Alpha : MetalBlend.None,
                ModelAttributes, SceneShading.VertexStride);
        return pipeline;
    }

    private nint SkyPipelineFor(int samples)
    {
        if (!_skyPipelines.TryGetValue(samples, out nint pipeline))
            _skyPipelines[samples] = pipeline = MetalGpu.Pipeline(_ctx.Device, "Sky",
                _ctx.Function(_shaders, BrutalRenderer.SkyVertexShader), _ctx.Function(_shaders, BrutalRenderer.SkyFragmentShader),
                MetalContext.ColorFormat, depth: true, samples, MetalBlend.None);
        return pipeline;
    }

    private void CreateShadowMaps()
    {
        for (int i = 0; i < FramesInFlight; i++)
        {
            ObjC.Release(_shadowMaps[i]);
            _shadowMaps[i] = MetalGpu.Texture(_ctx.Device, MetalGpu.FormatDepth32, _shadowSize, _shadowSize,
                usage: MetalGpu.UsageRenderTarget | MetalGpu.UsageRead, storage: MetalGpu.StoragePrivate);
        }
    }

    public void UploadModel(string key, GlbModel model) => _meshes.Upload(key, model);

    public void UnloadModels(IReadOnlyCollection<string> keys)
    {
        foreach (var key in keys)
            _meshes.Remove(key);
    }

    public void ReloadModel(string key, GlbModel model) => _meshes.Replace(key, model);

    public void SetShadows(bool enabled, float strength, float distance, int resolution = 2048)
    {
        resolution = Math.Clamp(resolution, 256, 8192);   // every Mac that runs Metal handles 8192 x 8192 textures, and more
        if (resolution != _shadowSize)
        {
            _shadowSize = resolution;
            CreateShadowMaps();
        }
        _shadowStrength = Math.Clamp(strength, 0f, 1f);
        _shadowsOn = enabled && _shadowStrength > 0f;
        _shadowDistance = float.IsFinite(distance) ? Math.Clamp(distance, SceneShading.MinShadowDistance, SceneShading.MaxShadowDistance) : 40f;
    }

    // A new panorama image is loaded and uploaded here, so changing the sky costs a moment; frames with the same sky cost nothing extra.
    public void SetLighting(EnvironmentData environment, IReadOnlyList<LightData> lights, Func<string, ImageData>? loadImage = null)
    {
        _environment = environment;
        _lights = lights;

        string wanted = environment.Sky == SkyMode.Panorama ? environment.Panorama : "";
        if (wanted == _panoramaKey) return;
        _panorama?.Dispose();   // frames in flight keep it alive
        _panorama = null;
        _panoramaKey = wanted;
        if (wanted.Length > 0 && loadImage is not null)
        {
            try
            {
                _panorama = MetalTexture.Create(_ctx.Device, loadImage(wanted).BuildMips(SceneShading.MaxPanoramaWidth));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not load the sky panorama {wanted}: {ex.Message}");
            }
        }
    }

    public void BeginFrame(Matrix4x4 viewProjection, Vector3 cameraPosition, Vector3 sunDirection, Vector3 sunRadiance)
    {
        _viewProjection = viewProjection;
        _eye = cameraPosition;
        _particles.BeginFrame();
        SceneShading.WriteFrameConstants(_push, cameraPosition, sunDirection, sunRadiance);
        _lightViewProjection = SceneShading.LightViewProjection(sunDirection, viewProjection, cameraPosition, _shadowDistance, _shadowSize,
            out _shadowTexelDepth);
        _draws.Clear();
        _framePending = true;
        _ctx.BeginFrame(deferScenePass: true);
    }

    public void DrawModel(string key, Matrix4x4 world, Vector4 tint) => DrawModel(key, world, tint, null);

    public void DrawModel(string key, Matrix4x4 world, Vector4 tint, ModelPose? pose) =>
        _draws.Add(new QueuedDraw { Key = key, World = world, Tint = tint, Pose = pose });

    public void DrawParticles(ReadOnlySpan<ParticleInstance> particles, ParticleBlend blend, ParticleSprite sprite) =>
        _particles.Add(particles, blend, sprite, _eye);

    public void EndFrame() => _ctx.EndFrame(null);

    public void EndFrame(Action<UiPass>? drawUi) => _ctx.EndFrame(drawUi);

    private static Matrix4x4 MeshWorld(QueuedDraw draw, MetalMesh mesh, int index) =>
        SceneShading.MeshWorld(draw.World, draw.Pose, mesh.RestTransform, index);

    private static nint VertexBufferFor(QueuedDraw draw, MetalMesh mesh, int index) =>
        draw.Vertices is { } own && index < own.Length && own[index] != 0 ? own[index] : mesh.VertexBuffer;

    // Writes the vertices of every posed mesh queued this frame into this slot's buffers.
    private void PrepareAnimatedMeshes(int slot)
    {
        var buffers = _dynamicBuffers[slot];
        int used = 0;
        foreach (var draw in _draws)
        {
            if (draw.Pose is not { } pose) continue;
            var meshes = _meshes.Get(draw.Key);
            for (int i = 0; i < meshes.Length && i < pose.Positions.Length; i++)
            {
                var mesh = meshes[i];
                if (SceneShading.PosedVertices(pose, i, mesh.VertexCount, mesh.TexCoords, mesh.Colors, _vertexScratch) is not { } data)
                    continue;
                long bytes = (long)data.Length * sizeof(float);
                if (used == buffers.Count)
                    buffers.Add(MetalGpu.Buffer(_ctx.Device, bytes));
                else if (MetalGpu.Length(buffers[used]) < bytes)
                {
                    ObjC.Release(buffers[used]);   // this slot's last frame has finished with it
                    buffers[used] = MetalGpu.Buffer(_ctx.Device, bytes);
                }
                MetalGpu.Write<float>(buffers[used], data);
                draw.Vertices ??= new nint[meshes.Length];
                draw.Vertices[i] = buffers[used];
                used++;
            }
        }
    }

    // Runs just before the frame ends, when every DrawModel call is known: the shadow pass from this frame's models, then the scene.
    private void FlushFrame()
    {
        if (!_framePending) return;
        _framePending = false;
        int slot = _ctx.FrameIndex;

        PrepareAnimatedMeshes(slot);
        RenderShadowMap(slot);
        SceneShading.WriteFrameData(_frameData, _environment, _lights, _panorama is not null, _panorama?.MipLevels ?? 1, _panorama?.IsHdr ?? false,
            _lightViewProjection, new SceneShading.ShadowSettings(_shadowsOn, _shadowStrength, _shadowTexelDepth, _shadowDistance, _shadowSize));

        _ctx.BeginScenePass();
        nint encoder = _ctx.SceneEncoder;
        int samples = _ctx.SampleCount;

        // What every model and the sky read: the frame block, the shadow map and the sky panorama.
        MetalGpu.Bytes<float>(encoder, _frameData, FrameBlockIndex, vertex: false);
        MetalGpu.FragmentTexture(encoder, _shadowMaps[slot], _shadowSampler, ShadowMapIndex);
        MetalGpu.FragmentTexture(encoder, (_panorama ?? _blackPanorama).Handle, _panoramaSampler, PanoramaIndex);

        // The sky goes first, behind everything: a full-screen triangle with no depth test.
        MetalGpu.SetPipeline(encoder, SkyPipelineFor(samples));
        MetalGpu.SetDepth(encoder, _ctx.DepthState(test: false, write: false));
        Matrix4x4.Invert(_viewProjection, out var inverseViewProjection);
        SceneShading.WriteMatrix(_push, 0, inverseViewProjection);
        MetalGpu.Bytes<float>(encoder, _push, SpirvToMsl.PushConstantIndex);
        MetalGpu.Draw(encoder, 3);

        MetalGpu.SetPipeline(encoder, PipelineFor(samples, transparent: false));
        MetalGpu.SetDepth(encoder, _ctx.DepthState(test: true, write: true));
        var transparent = new List<(QueuedDraw Draw, int Mesh, float Distance)>();
        foreach (var draw in _draws)
        {
            var meshes = _meshes.Get(draw.Key);
            for (int i = 0; i < meshes.Length; i++)
            {
                if (SceneShading.IsTransparent(draw.Tint, meshes[i].Material))
                {
                    var at = MeshWorld(draw, meshes[i], i).Translation;
                    transparent.Add((draw, i, Vector3.DistanceSquared(at, _eye)));
                    continue;
                }
                DrawMesh(encoder, draw, meshes[i], i);
            }
        }

        // See-through meshes after every solid one, farthest first, so nearer ones blend over farther ones.
        if (transparent.Count > 0)
        {
            transparent.Sort((a, b) => b.Distance.CompareTo(a.Distance));
            MetalGpu.SetPipeline(encoder, PipelineFor(samples, transparent: true));
            MetalGpu.SetDepth(encoder, _ctx.DepthState(test: true, write: false));
            foreach (var (draw, meshIndex, _) in transparent)
                DrawMesh(encoder, draw, _meshes.Get(draw.Key)[meshIndex], meshIndex);
        }
        _draws.Clear();
        _particles.Record(encoder, _viewProjection);   // see-through sprites go after every solid model
    }

    private void DrawMesh(nint encoder, QueuedDraw draw, MetalMesh mesh, int meshIndex)
    {
        SceneShading.WriteMeshConstants(_push, MeshWorld(draw, mesh, meshIndex), _viewProjection, draw.Tint, mesh.Material);
        MetalGpu.Bytes<float>(encoder, _push, SpirvToMsl.PushConstantIndex);
        for (int t = 0; t < mesh.Textures.Length; t++)
            MetalGpu.FragmentTexture(encoder, mesh.Textures[t], _meshes.Sampler, FirstMaterialIndex + t);
        MetalGpu.VertexBuffer(encoder, VertexBufferFor(draw, mesh, meshIndex));
        MetalGpu.DrawIndexed(encoder, mesh.IndexCount, mesh.IndexBuffer);
    }

    // Renders this frame's queued solid models into its shadow map, from the sun. With shadows off the map is only cleared.
    private void RenderShadowMap(int slot)
    {
        // The shadow map keeps ordinary depth (0 nearest the sun), unlike the reversed scene depth.
        nint pass = MetalGpu.PassDescriptor(0, MetalGpu.LoadClear, default, depth: _shadowMaps[slot], keepDepth: true, clearDepth: 1.0);
        nint encoder = MetalGpu.Encoder(_ctx.CommandBuffer, pass, "Shadow map");
        MetalGpu.SetPipeline(encoder, _shadowPipeline);
        MetalGpu.SetDepth(encoder, _ctx.DepthState(test: true, write: true, reversed: false));
        MetalGpu.Viewport(encoder, 0, 0, _shadowSize, _shadowSize);
        foreach (var draw in _shadowsOn ? _draws : [])
        {
            var meshes = _meshes.Get(draw.Key);
            for (int i = 0; i < meshes.Length; i++)
            {
                var mesh = meshes[i];
                if (SceneShading.IsTransparent(draw.Tint, mesh.Material)) continue;   // see-through surfaces don't cast shadows
                SceneShading.WriteMatrix(_push, 0, MeshWorld(draw, mesh, i) * _lightViewProjection);
                MetalGpu.Bytes<float>(encoder, _push, SpirvToMsl.PushConstantIndex, fragment: false);
                MetalGpu.VertexBuffer(encoder, VertexBufferFor(draw, mesh, i));
                MetalGpu.DrawIndexed(encoder, mesh.IndexCount, mesh.IndexBuffer);
            }
        }
        MetalGpu.End(encoder);
    }

    public void Dispose()
    {
        _ctx.WaitIdle();
        _ctx.BeforeEndFrame = null;
        _ctx.AfterScenePass = null;
        _postProcess.Dispose();
        _particles.Dispose();
        _meshes.Dispose();
        foreach (var list in _dynamicBuffers)
            foreach (var buffer in list)
                ObjC.Release(buffer);
        foreach (var pipeline in _pipelines.Values.Concat(_skyPipelines.Values))
            ObjC.Release(pipeline);
        ObjC.Release(_shadowPipeline);
        foreach (var map in _shadowMaps)
            ObjC.Release(map);
        ObjC.Release(_shadowSampler);
        ObjC.Release(_panoramaSampler);
        _panorama?.Dispose();
        _blackPanorama.Dispose();
    }
}
