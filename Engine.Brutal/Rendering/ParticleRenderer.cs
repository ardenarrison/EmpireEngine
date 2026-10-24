using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Engine.Brutal.Gfx;
using Engine.Core.Rendering;
using Engine.Core.Scene;

namespace Engine.Brutal.Rendering;

// Draws particle sprites inside the scene pass, after the models: one instance per particle, expanded into a
// camera-facing quad by the vertex shader. Depth-tested against the scene but not written, so sprites overlap freely.
// Two pipelines: alpha blending, and additive blending for glowing effects.
internal sealed class ParticleRenderer : IDisposable
{
    private const int FloatsPerInstance = 9;          // position(3) size(1) color(4) rotation(1)
    private const int InstanceStride = FloatsPerInstance * sizeof(float);
    private const int PushFloats = 16 + 4 + 4 + 4;    // viewProj, camera right, camera up, sprite style
    private const int StageVertexFragment = 0x11;
    private const int BufferVertex = 0x80;

    private const string VertexGlsl = """
        #version 450
        layout(push_constant) uniform PC { mat4 viewProj; vec4 right; vec4 up; vec4 style; } pc;
        layout(location = 0) in vec3 inPos;
        layout(location = 1) in float inSize;
        layout(location = 2) in vec4 inColor;
        layout(location = 3) in float inRotation;
        layout(location = 0) out vec2 outUv;
        layout(location = 1) out vec4 outColor;
        const vec2 corners[6] = vec2[](vec2(-1,-1), vec2(1,-1), vec2(1,1), vec2(-1,-1), vec2(1,1), vec2(-1,1));
        void main() {
            vec2 c = corners[gl_VertexIndex];
            float s = sin(inRotation), k = cos(inRotation);
            vec2 r = vec2(c.x * k - c.y * s, c.x * s + c.y * k) * (inSize * 0.5);
            vec3 world = inPos + pc.right.xyz * r.x + pc.up.xyz * r.y;
            gl_Position = pc.viewProj * vec4(world, 1.0);
            outUv = c;
            outColor = inColor;
        }
        """;

    // style.x: 0 soft circle, 1 hard circle, 2 square.
    private const string FragmentGlsl = """
        #version 450
        layout(push_constant) uniform PC { mat4 viewProj; vec4 right; vec4 up; vec4 style; } pc;
        layout(location = 0) in vec2 inUv;
        layout(location = 1) in vec4 inColor;
        layout(location = 0) out vec4 outColor;
        void main() {
            float d = length(inUv);
            float a = 1.0;
            if (pc.style.x < 0.5) {
                a = clamp(1.0 - d, 0.0, 1.0);
                a = a * a * (3.0 - 2.0 * a);
            } else if (pc.style.x < 1.5) {
                a = clamp((1.0 - d) / max(fwidth(d), 1e-4), 0.0, 1.0);
            }
            a *= inColor.a;
            if (a <= 0.002) discard;
            outColor = vec4(inColor.rgb, a);
        }
        """;

    private readonly record struct Batch(int First, int Count, ParticleBlend Blend, ParticleSprite Sprite, float Distance);

    private readonly VulkanContext _ctx;
    private readonly Device _device;
    private readonly VkPipelineLayout _layout;
    private readonly VkShaderModule _vert;
    private readonly VkShaderModule _frag;
    private readonly Dictionary<(int Samples, bool Additive), VkPipeline> _pipelines = new();   // per MSAA sample count
    private readonly VkBuffer[] _buffers = new VkBuffer[VulkanContext.FramesInFlight];
    private readonly VkDeviceMemory[] _memories = new VkDeviceMemory[VulkanContext.FramesInFlight];
    private readonly int[] _capacity = new int[VulkanContext.FramesInFlight];   // instances each buffer holds
    private readonly List<float> _data = new();
    private readonly List<Batch> _batches = new();
    private readonly float[] _push = new float[PushFloats];

    public ParticleRenderer(VulkanContext ctx, ShaderLibrary shaders)
    {
        _ctx = ctx;
        _device = ctx.Device;
        var range = new VkPushConstantRange
        {
            StageFlags = (VkShaderStageFlags)StageVertexFragment,
            Offset = (ByteSize32)0,
            Size = (ByteSize32)(PushFloats * sizeof(float)),
        };
        _layout = _device.CreatePipelineLayout(Array.Empty<VkDescriptorSetLayout>(), new[] { range }, null);
        _vert = shaders.Get("particle.vert", VertexGlsl, ShaderKind.VertexShader);
        _frag = shaders.Get("particle.frag", FragmentGlsl, ShaderKind.FragmentShader);
        PipelineFor(1, additive: false);
        PipelineFor(1, additive: true);
    }

    // The particle pipeline for the scene pass's sample count, made the first time that count is used.
    private VkPipeline PipelineFor(int samples, bool additive)
    {
        if (!_pipelines.TryGetValue((samples, additive), out var pipeline))
            _pipelines[(samples, additive)] = pipeline = CreatePipeline(_vert, _frag, additive, samples);
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

    // Records this frame's particles into the open scene pass. Systems draw back to front, so overlapping
    // effects blend in the right order.
    public void Record(CommandBuffer cmd, Matrix4x4 viewProjection)
    {
        if (_batches.Count == 0) return;
        int slot = _ctx.FrameIndex;   // this slot's last frame has finished, so its buffer is free to rewrite
        int instances = _data.Count / FloatsPerInstance;
        EnsureCapacity(slot, instances);
        _device.WriteMemory<float>(CollectionsMarshal.AsSpan(_data).ToArray(), _memories[slot], default, default);

        WriteMatrix(_push, 0, viewProjection);
        CameraAxes(viewProjection, out var right, out var up);
        _push[16] = right.X; _push[17] = right.Y; _push[18] = right.Z; _push[19] = 0f;
        _push[20] = up.X; _push[21] = up.Y; _push[22] = up.Z; _push[23] = 0f;

        cmd.BindVertexBuffer(0, _buffers[slot], default);
        foreach (var batch in _batches.OrderByDescending(b => b.Distance))
        {
            cmd.BindPipeline((VkPipelineBindPoint)0, PipelineFor(_ctx.SampleCount, batch.Blend == ParticleBlend.Additive));
            _push[24] = (float)batch.Sprite;
            cmd.PushConstants(_layout, (VkShaderStageFlags)StageVertexFragment, default, _push);
            cmd.Draw(6, batch.Count, 0, batch.First);
        }
    }

    // The camera's right and up directions in the world, read back from the view-projection: two points one step
    // apart in screen x (or y) at the same depth differ exactly along that axis.
    private static void CameraAxes(Matrix4x4 viewProjection, out Vector3 right, out Vector3 up)
    {
        right = Vector3.UnitX;
        up = Vector3.UnitY;
        if (!Matrix4x4.Invert(viewProjection, out var inverse)) return;
        Vector3 At(float x, float y)
        {
            var h = Vector4.Transform(new Vector4(x, y, 0.5f, 1f), inverse);
            return new Vector3(h.X, h.Y, h.Z) / h.W;
        }
        var o = At(0f, 0f);
        var r = At(1f, 0f) - o;
        var u = At(0f, -1f) - o;   // Vulkan's screen y points down
        if (r.LengthSquared() > 1e-12f) right = Vector3.Normalize(r);
        if (u.LengthSquared() > 1e-12f) up = Vector3.Normalize(u);
    }

    private void EnsureCapacity(int slot, int instances)
    {
        if (_capacity[slot] >= instances) return;
        int capacity = Math.Max(1024, _capacity[slot]);
        while (capacity < instances) capacity *= 2;
        if (_capacity[slot] > 0)
        {
            _device.DestroyBuffer(_buffers[slot], null);
            _device.FreeMemory(_memories[slot], null);
        }
        (_buffers[slot], _memories[slot]) = VkUtil.CreateHostBuffer(_device, _ctx.Physical, (VkBufferUsageFlags)BufferVertex,
            (ulong)(capacity * InstanceStride));
        _capacity[slot] = capacity;
    }

    private static void WriteMatrix(float[] dst, int offset, Matrix4x4 m)
    {
        dst[offset + 0] = m.M11; dst[offset + 1] = m.M12; dst[offset + 2] = m.M13; dst[offset + 3] = m.M14;
        dst[offset + 4] = m.M21; dst[offset + 5] = m.M22; dst[offset + 6] = m.M23; dst[offset + 7] = m.M24;
        dst[offset + 8] = m.M31; dst[offset + 9] = m.M32; dst[offset + 10] = m.M33; dst[offset + 11] = m.M34;
        dst[offset + 12] = m.M41; dst[offset + 13] = m.M42; dst[offset + 14] = m.M43; dst[offset + 15] = m.M44;
    }

    private unsafe VkPipeline CreatePipeline(VkShaderModule vert, VkShaderModule frag, bool additive, int samples)
    {
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
                // One record per particle, stepped per instance.
                new VkVertexInputBindingDescription { Binding = 0, Stride = (ByteSize32)InstanceStride, InputRate = (VkVertexInputRate)1 },
            };
            var attributes = new[]
            {
                new VkVertexInputAttributeDescription { Location = 0, Binding = 0, Format = (VkFormat)106, Offset = (ByteSize32)0 },   // vec3
                new VkVertexInputAttributeDescription { Location = 1, Binding = 0, Format = (VkFormat)100, Offset = (ByteSize32)12 },  // float
                new VkVertexInputAttributeDescription { Location = 2, Binding = 0, Format = (VkFormat)109, Offset = (ByteSize32)16 },  // vec4
                new VkVertexInputAttributeDescription { Location = 3, Binding = 0, Format = (VkFormat)100, Offset = (ByteSize32)32 },  // float
            };
            var colorFormats = new[] { _ctx.ColorFormat };
            var viewport = new VkViewport { Width = _ctx.Width, Height = _ctx.Height, MinDepth = 0, MaxDepth = 1 };
            var scissor = new VkRect2D { Extent = new VkExtent2D { Width = _ctx.Width, Height = _ctx.Height } };
            var blend = new VkPipelineColorBlendAttachmentState
            {
                BlendEnable = true,
                SrcColorBlendFactor = (VkBlendFactor)6,                       // SRC_ALPHA
                DstColorBlendFactor = (VkBlendFactor)(additive ? 1 : 7),      // ONE, or ONE_MINUS_SRC_ALPHA
                ColorBlendOp = (VkBlendOp)0,
                SrcAlphaBlendFactor = (VkBlendFactor)(additive ? 0 : 1),      // additive leaves alpha alone
                DstAlphaBlendFactor = (VkBlendFactor)(additive ? 1 : 7),
                AlphaBlendOp = (VkBlendOp)0,
                ColorWriteMask = (VkColorComponentFlags)0xF,
            };
            var dynamicStates = new[] { (VkDynamicState)0, (VkDynamicState)1 };

            fixed (VkPipelineShaderStageCreateInfo* stagesPtr = stages)
            fixed (VkVertexInputBindingDescription* bindingsPtr = bindings)
            fixed (VkVertexInputAttributeDescription* attributesPtr = attributes)
            fixed (VkFormat* colorFormatsPtr = colorFormats)
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
                var multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = (VkSampleCountFlags)samples };
                var depthState = new VkPipelineDepthStencilStateCreateInfo
                {
                    DepthTestEnable = true,
                    DepthWriteEnable = false,      // sprites never hide each other through the depth buffer
                    DepthCompareOp = (VkCompareOp)1,
                };
                var colorBlend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &blend };
                var dynamicState = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 2, DynamicStates = dynamicStatesPtr };
                var rendering = new VkPipelineRenderingCreateInfo
                {
                    ColorAttachmentCount = 1,
                    ColorAttachmentFormats = colorFormatsPtr,
                    DepthAttachmentFormat = (VkFormat)VulkanContext.DepthFormat,
                };
                var info = new VkGraphicsPipelineCreateInfo
                {
                    Next = &rendering,
                    StageCount = stages.Length,
                    Stages = stagesPtr,
                    VertexInputState = &vertexInput,
                    InputAssemblyState = &inputAssembly,
                    ViewportState = &viewportState,
                    DynamicState = &dynamicState,
                    RasterizationState = &raster,
                    MultisampleState = &multisample,
                    DepthStencilState = &depthState,
                    ColorBlendState = &colorBlend,
                    Layout = _layout,
                };
                return _device.CreateGraphicsPipeline(default, info, null);
            }
        }
    }

    public void Dispose()
    {
        foreach (var pipeline in _pipelines.Values)
            _device.DestroyPipeline(pipeline, null);
        _device.DestroyPipelineLayout(_layout, null);
        for (int i = 0; i < _buffers.Length; i++)
            if (_capacity[i] > 0)
            {
                _device.DestroyBuffer(_buffers[i], null);
                _device.FreeMemory(_memories[i], null);
            }
    }
}
