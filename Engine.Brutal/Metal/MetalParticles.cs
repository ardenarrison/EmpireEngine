using System.Numerics;
using System.Runtime.InteropServices;
using Engine.Brutal.Gfx;
using Engine.Brutal.Rendering;
using Engine.Core.Rendering;
using Engine.Core.Scene;

namespace Engine.Brutal.Metal;

// Particle sprites with Metal, as ParticleRenderer draws them with Vulkan: one instance per particle, expanded into a camera-facing
// quad by the vertex shader, depth-tested but not written, blended by alpha or added for glowing effects. Systems draw back to front.
internal sealed class MetalParticles : IDisposable
{
    private const int FloatsPerInstance = 9;          // position(3) size(1) color(4) rotation(1)
    private const int InstanceStride = FloatsPerInstance * sizeof(float);
    private const int PushFloats = 16 + 4 + 4 + 4;    // viewProj, camera right, camera up, sprite style

    private readonly record struct Batch(int First, int Count, ParticleBlend Blend, ParticleSprite Sprite, float Distance);

    private static readonly MetalVertexAttribute[] Attributes =
    [
        new(0, MetalGpu.VertexFloat3, 0),
        new(1, MetalGpu.VertexFloat, 12),
        new(2, MetalGpu.VertexFloat4, 16),
        new(3, MetalGpu.VertexFloat, 32),
    ];

    private readonly MetalContext _ctx;
    private readonly ShaderLibrary _shaders;
    private readonly Dictionary<(int Samples, bool Additive), nint> _pipelines = new();
    private readonly nint[] _buffers = new nint[MetalContext.FramesInFlight];
    private readonly List<float> _data = new();
    private readonly List<Batch> _batches = new();
    private readonly float[] _push = new float[PushFloats];

    public MetalParticles(MetalContext ctx, ShaderLibrary shaders)
    {
        _ctx = ctx;
        _shaders = shaders;
        PipelineFor(1, additive: false);
        PipelineFor(1, additive: true);
    }

    private nint PipelineFor(int samples, bool additive)
    {
        if (!_pipelines.TryGetValue((samples, additive), out nint pipeline))
            _pipelines[(samples, additive)] = pipeline = MetalGpu.Pipeline(_ctx.Device, additive ? "Particles (added)" : "Particles",
                _ctx.Function(_shaders, ParticleRenderer.VertexShader), _ctx.Function(_shaders, ParticleRenderer.FragmentShader),
                MetalContext.ColorFormat, depth: true, samples, additive ? MetalBlend.Additive : MetalBlend.Alpha,
                Attributes, InstanceStride, perInstance: true);
        return pipeline;
    }

    public void BeginFrame()
    {
        _data.Clear();
        _batches.Clear();
    }

    public void Add(ReadOnlySpan<ParticleInstance> particles, ParticleBlend blend, ParticleSprite sprite, Vector3 eye)
    {
        if (particles.IsEmpty) return;
        int first = _data.Count / FloatsPerInstance;
        var center = Vector3.Zero;
        foreach (var p in particles)
        {
            _data.Add(p.Position.X); _data.Add(p.Position.Y); _data.Add(p.Position.Z);
            _data.Add(p.Size);
            _data.Add(p.Color.X); _data.Add(p.Color.Y); _data.Add(p.Color.Z); _data.Add(p.Color.W);
            _data.Add(p.Rotation);
            center += p.Position;
        }
        center /= particles.Length;
        _batches.Add(new Batch(first, particles.Length, blend, sprite, Vector3.DistanceSquared(center, eye)));
    }

    // Draws this frame's particles into the open scene pass.
    public void Record(nint encoder, Matrix4x4 viewProjection)
    {
        if (_batches.Count == 0) return;
        int slot = _ctx.FrameIndex;   // this slot's last frame has finished, so its buffer is free to rewrite
        long bytes = (long)_data.Count * sizeof(float);
        if (_buffers[slot] == 0 || MetalGpu.Length(_buffers[slot]) < bytes)
        {
            ObjC.Release(_buffers[slot]);
            long capacity = 1024L * InstanceStride;
            while (capacity < bytes) capacity *= 2;
            _buffers[slot] = MetalGpu.Buffer(_ctx.Device, capacity);
        }
        MetalGpu.Write<float>(_buffers[slot], CollectionsMarshal.AsSpan(_data));

        SceneShading.WriteMatrix(_push, 0, viewProjection);
        ParticleRenderer.CameraAxes(viewProjection, out var right, out var up);
        _push[16] = right.X; _push[17] = right.Y; _push[18] = right.Z; _push[19] = 0f;
        _push[20] = up.X; _push[21] = up.Y; _push[22] = up.Z; _push[23] = 0f;

        MetalGpu.SetDepth(encoder, _ctx.DepthState(test: true, write: false));   // sprites never hide each other through the depth buffer
        MetalGpu.VertexBuffer(encoder, _buffers[slot]);
        foreach (var batch in _batches.OrderByDescending(b => b.Distance))
        {
            MetalGpu.SetPipeline(encoder, PipelineFor(_ctx.SampleCount, batch.Blend == ParticleBlend.Additive));
            _push[24] = (float)batch.Sprite;
            MetalGpu.Bytes<float>(encoder, _push, SpirvToMsl.PushConstantIndex);
            MetalGpu.Draw(encoder, 6, batch.Count, batch.First);
        }
    }

    public void Dispose()
    {
        foreach (var pipeline in _pipelines.Values)
            ObjC.Release(pipeline);
        foreach (var buffer in _buffers)
            ObjC.Release(buffer);
    }
}
