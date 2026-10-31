using Brutal;
using Brutal.Numerics;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Engine.Brutal.Gfx;

namespace Engine.Brutal.Rendering;

// Runs FXAA or SMAA and the color effects (exposure, contrast, vignette, sharpen) over the finished scene. VulkanContext draws the
// scene into its own image when any of them is on, then calls Record, which reads that image and draws the finished picture into
// the swapchain image (and clears the rest of it, since the scene pass no longer does). With both anti-aliasing and effects, the
// anti-aliased picture goes into an image of its own first and the effects pass reads that. Everything here is made the first time
// it is needed: a game that never uses these pays nothing.
internal sealed unsafe class PostProcessPass : IDisposable
{
    private const int FormatR8 = 9;           // VK_FORMAT_R8_UNORM
    private const int FormatRg8 = 16;         // VK_FORMAT_R8G8_UNORM
    private const int FormatRgba8 = 37;       // VK_FORMAT_R8G8B8A8_UNORM
    private const int Bindings = 3;           // the most textures any pass reads
    private const int PushFloats = 12;        // metrics, the scene rectangle, the effect settings (see PostProcessShaders)
    private static readonly float4 Backdrop = new(0.05f, 0.08f, 0.15f, 1f);   // the scene pass's clear color, outside the scene

    private readonly VulkanContext _ctx;
    private readonly Device _device;
    private readonly ShaderLibrary _shaders;
    private readonly float[] _push = new float[PushFloats];

    private VkSampler _linear;
    private VkSampler _point;
    private VkDescriptorSetLayout _setLayout;
    private VkPipelineLayout _layout;
    private VkDescriptorPool _pool;
    private bool _basicsReady;

    private VkPipeline? _fxaa, _effects;
    private VkPipeline? _smaaEdges, _smaaWeights, _smaaBlend;

    // SMAA's lookup tables; its two in-between pictures; and the anti-aliased picture the effects read. The last three are as
    // big as the window.
    private Texture? _area, _search, _edges, _weights, _graded;
    private int _targetWidth, _targetHeight;
    private int _sceneGeneration = -1;
    private VkDescriptorSet _fxaaSet, _edgesSet, _weightsSet, _blendSet, _effectsFromScene, _effectsFromGraded;
    private bool _setsAllocated;

    private readonly struct Texture(VkImage image, VkDeviceMemory memory, VkImageView view)
    {
        public readonly VkImage Image = image;
        public readonly VkDeviceMemory Memory = memory;
        public readonly VkImageView View = view;
    }

    public PostProcessPass(VulkanContext ctx, ShaderLibrary shaders)
    {
        _ctx = ctx;
        _device = ctx.Device;
        _shaders = shaders;
    }

    // Draws the finished scene into the swapchain image. The scene image is already in shader-read layout.
    public void Record(CommandBuffer cmd, PostAntiAliasing mode, PostEffects effects)
    {
        bool fx = effects.Any;
        if (mode == PostAntiAliasing.None && !fx) return;
        EnsureBasics();
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

        // The anti-aliased picture goes on screen, or into _graded when the effects pass comes after.
        var swapchain = _ctx.CurrentSwapchainView;
        if (mode == PostAntiAliasing.Fxaa)
            Output(cmd, fx, rect, PipelineFxaa(), _fxaaSet);
        else if (mode == PostAntiAliasing.Smaa)
        {
            // SMAA: where the edges are, then how much of each neighbor to blend across them, then the blend itself.
            var clear = new float4(0f, 0f, 0f, 0f);
            DrawIntermediate(cmd, _edges!.Value, clear, rect, Pipeline(ref _smaaEdges, "smaa.edges", PostProcessShaders.SmaaEdgeVertex, PostProcessShaders.SmaaEdgeFragment, FormatRg8), _edgesSet);
            DrawIntermediate(cmd, _weights!.Value, clear, rect, Pipeline(ref _smaaWeights, "smaa.weights", PostProcessShaders.SmaaWeightsVertex, PostProcessShaders.SmaaWeightsFragment, FormatRgba8), _weightsSet);
            Output(cmd, fx, rect, Pipeline(ref _smaaBlend, "smaa.blend", PostProcessShaders.SmaaBlendVertex, PostProcessShaders.SmaaBlendFragment, (int)_ctx.ColorFormat), _blendSet);
        }

        if (fx)
            Draw(cmd, swapchain, Backdrop, rect, Pipeline(ref _effects, "effects", PostProcessShaders.FullscreenVertex, PostProcessShaders.EffectsFragment, (int)_ctx.ColorFormat),
                mode == PostAntiAliasing.None ? _effectsFromScene : _effectsFromGraded);
    }

    // The last anti-aliasing pass: into the swapchain image, or into _graded when effects follow.
    private void Output(CommandBuffer cmd, bool effectsFollow, (int X, int Y, int Width, int Height) rect, VkPipeline pipeline, VkDescriptorSet set)
    {
        if (effectsFollow)
            DrawIntermediate(cmd, _graded!.Value, Backdrop, rect, pipeline, set);
        else
            Draw(cmd, _ctx.CurrentSwapchainView, Backdrop, rect, pipeline, set);
    }

    // One full-screen triangle into a target, inside the scene's rectangle; the rest of the target is cleared.
    private void Draw(CommandBuffer cmd, VkImageView target, float4 clearColor, (int X, int Y, int Width, int Height) rect, VkPipeline pipeline, VkDescriptorSet set)
    {
        var color = new VkRenderingAttachmentInfo
        {
            ImageView = target,
            ImageLayout = (VkImageLayout)2,
            LoadOp = (VkAttachmentLoadOp)1,             // CLEAR
            StoreOp = (VkAttachmentStoreOp)0,           // STORE
            ClearValue = new VkClearValue { Color = new VkClearColorValue { Float32 = clearColor } },
        };
        var renderInfo = new VkRenderingInfo
        {
            RenderArea = new VkRect2D { Extent = new VkExtent2D { Width = _ctx.Width, Height = _ctx.Height } },
            LayerCount = 1,
            ColorAttachmentCount = 1,
            ColorAttachments = &color,
        };
        cmd.BeginRendering(in renderInfo);
        // The viewport is the whole target, so the triangle's texture coordinates line up with the scene image pixel for pixel;
        // only the scissor limits the drawing to the scene's rectangle.
        cmd.SetViewport(0, new[] { new VkViewport { Width = _ctx.Width, Height = _ctx.Height, MinDepth = 0, MaxDepth = 1 } });
        cmd.SetScissor(0, new[] { new VkRect2D { Offset = new VkOffset2D { X = rect.X, Y = rect.Y }, Extent = new VkExtent2D { Width = rect.Width, Height = rect.Height } } });
        cmd.BindPipeline((VkPipelineBindPoint)0, pipeline);
        cmd.BindDescriptorSets((VkPipelineBindPoint)0, _layout, 0, new[] { set }, default);
        cmd.PushConstants(_layout, (VkShaderStageFlags)0x11, default, _push);
        cmd.Draw(3, 1, 0, 0);
        cmd.EndRendering();
    }

    // The same into one of the in-between pictures, which has to be made writable first and readable after.
    private void DrawIntermediate(CommandBuffer cmd, Texture target, float4 clearColor, (int X, int Y, int Width, int Height) rect, VkPipeline pipeline, VkDescriptorSet set)
    {
        // 0x480: the previous frame's passes read it in a fragment shader (0x80); 0x400 is color output.
        VkUtil.Transition(cmd, target.Image, 1, 0, 2, 0x480, 0x400, 0x120, 0x100);
        Draw(cmd, target.View, clearColor, rect, pipeline, set);
        VkUtil.Transition(cmd, target.Image, 1, 2, 5, 0x400, 0x80, 0x100, 0x20);
    }

    // ---- setting up ------------------------------------------------------------------------------------------------------

    // The samplers, the descriptor layout and pool, and the pipeline layout every pass shares.
    private void EnsureBasics()
    {
        if (_basicsReady) return;
        _linear = CreateSampler(linear: true);
        _point = CreateSampler(linear: false);

        var bindings = new VkDescriptorSetLayoutBinding[Bindings];
        for (int i = 0; i < Bindings; i++)
            bindings[i] = new VkDescriptorSetLayoutBinding
            {
                Binding = i,
                DescriptorType = (VkDescriptorType)1,       // combined image sampler
                DescriptorCount = 1,
                StageFlags = (VkShaderStageFlags)0x10,      // fragment
            };
        fixed (VkDescriptorSetLayoutBinding* b = bindings)
        {
            var info = new VkDescriptorSetLayoutCreateInfo { BindingCount = Bindings, Bindings = b };
            _setLayout = _device.CreateDescriptorSetLayout(in info, null);
        }

        var range = new VkPushConstantRange { StageFlags = (VkShaderStageFlags)0x11, Offset = (ByteSize32)0, Size = (ByteSize32)(PushFloats * 4) };
        _layout = _device.CreatePipelineLayout(new[] { _setLayout }, new[] { range }, null);

        var size = new VkDescriptorPoolSize { Type = (VkDescriptorType)1, DescriptorCount = 6 * Bindings };
        var poolInfo = new VkDescriptorPoolCreateInfo { MaxSets = 6, PoolSizeCount = 1, PoolSizes = &size };
        _pool = _device.CreateDescriptorPool(in poolInfo, null);
        _basicsReady = true;
    }

    private VkSampler CreateSampler(bool linear)
    {
        var filter = (VkFilter)(linear ? 1 : 0);
        var info = new VkSamplerCreateInfo
        {
            MagFilter = filter,
            MinFilter = filter,
            MipmapMode = (VkSamplerMipmapMode)0,
            AddressModeU = (VkSamplerAddressMode)2,         // clamp to edge
            AddressModeV = (VkSamplerAddressMode)2,
            AddressModeW = (VkSamplerAddressMode)2,
            MinLod = 0f,
            MaxLod = 0f,
        };
        return _device.CreateSampler(in info, null);
    }

    // Makes what the chosen settings need, and points the descriptor sets at the current pictures.
    private void EnsureTargets(PostAntiAliasing mode, bool effects)
    {
        bool smaa = mode == PostAntiAliasing.Smaa;
        bool graded = effects && mode != PostAntiAliasing.None;

        if (_targetWidth != _ctx.Width || _targetHeight != _ctx.Height)
        {
            if (_edges is not null || _graded is not null)
            {
                _ctx.WaitIdle();
                DestroyIntermediates();
                _sceneGeneration = -1;              // the sets point at the old pictures
            }
            _targetWidth = _ctx.Width;
            _targetHeight = _ctx.Height;
        }
        if (smaa && _edges is null)
        {
            _edges = CreateTarget(FormatRg8);
            _weights = CreateTarget(FormatRgba8);
            _sceneGeneration = -1;
        }
        if (graded && _graded is null)
        {
            _graded = CreateTarget((int)_ctx.ColorFormat);
            _sceneGeneration = -1;
        }
        if (smaa && _area is null)
        {
            _area = UploadTexture(FormatRg8, 160, 560, PostProcessShaders.AreaTexture);
            _search = UploadTexture(FormatR8, 64, 16, PostProcessShaders.SearchTexture);
            _sceneGeneration = -1;
        }

        if (!_setsAllocated)
        {
            _fxaaSet = AllocateSet();
            _edgesSet = AllocateSet();
            _weightsSet = AllocateSet();
            _blendSet = AllocateSet();
            _effectsFromScene = AllocateSet();
            _effectsFromGraded = AllocateSet();
            _setsAllocated = true;
            _sceneGeneration = -1;
        }

        if (_sceneGeneration != _ctx.SceneColorGeneration)
        {
            var scene = _ctx.SceneColorView;
            // Every binding gets something valid, even ones a pass doesn't read.
            WriteSet(_fxaaSet, (scene, _linear), (scene, _linear), (scene, _linear));
            WriteSet(_effectsFromScene, (scene, _linear), (scene, _linear), (scene, _linear));
            if (_edges is not null && _area is not null)
            {
                WriteSet(_edgesSet, (scene, _linear), (scene, _linear), (scene, _linear));
                WriteSet(_weightsSet, (_edges.Value.View, _linear), (_area.Value.View, _point), (_search!.Value.View, _point));
                WriteSet(_blendSet, (scene, _linear), (_weights!.Value.View, _linear), (scene, _linear));
            }
            if (_graded is { } g)
                WriteSet(_effectsFromGraded, (g.View, _linear), (g.View, _linear), (g.View, _linear));
            _sceneGeneration = _ctx.SceneColorGeneration;
        }
    }

    private VkDescriptorSet AllocateSet()
    {
        var layout = _setLayout;
        var allocInfo = new VkDescriptorSetAllocateInfo { DescriptorPool = _pool, DescriptorSetCount = 1, SetLayouts = &layout };
        VkDescriptorSet set;
        var allocate = (delegate* unmanaged<VkDevice, VkDescriptorSetAllocateInfo*, VkDescriptorSet*, int>)
            _ctx.DeviceEx.GetProcAddr("vkAllocateDescriptorSets"u8).Invoke;
        int result = allocate(_device.Handle, &allocInfo, &set);
        if (result != 0)
            throw new InvalidOperationException($"vkAllocateDescriptorSets failed with {result}");
        return set;
    }

    // Waits for the GPU first: a set that a frame still being drawn uses can't be rewritten.
    private void WriteSet(VkDescriptorSet set, params (VkImageView View, VkSampler Sampler)[] bindings)
    {
        _ctx.WaitIdle();
        var images = stackalloc VkDescriptorImageInfo[Bindings];
        var writes = new VkWriteDescriptorSet[Bindings];
        for (int i = 0; i < Bindings; i++)
        {
            images[i] = new VkDescriptorImageInfo { ImageView = bindings[i].View, Sampler = bindings[i].Sampler, ImageLayout = (VkImageLayout)5 };
            writes[i] = new VkWriteDescriptorSet
            {
                DstSet = set,
                DstBinding = i,
                DescriptorCount = 1,
                DescriptorType = (VkDescriptorType)1,
                ImageInfo = &images[i],
            };
        }
        _device.UpdateDescriptorSets(writes, default);
    }

    // ---- images ----------------------------------------------------------------------------------------------------------

    // A window-sized picture a pass draws into and a later pass reads.
    private Texture CreateTarget(int format)
    {
        var info = new VkImageCreateInfo
        {
            ImageType = (VkImageType)1,
            Format = (VkFormat)format,
            Extent = new VkExtent3D { Width = _ctx.Width, Height = _ctx.Height, Depth = 1 },
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = (VkSampleCountFlags)1,
            Tiling = (VkImageTiling)0,
            Usage = (VkImageUsageFlags)0x14,            // COLOR_ATTACHMENT + SAMPLED
            SharingMode = (VkSharingMode)0,
            InitialLayout = (VkImageLayout)0,
        };
        return Finish(info, format);
    }

    // A small lookup table, filled from bytes.
    private Texture UploadTexture(int format, int width, int height, byte[] data)
    {
        var info = new VkImageCreateInfo
        {
            ImageType = (VkImageType)1,
            Format = (VkFormat)format,
            Extent = new VkExtent3D { Width = width, Height = height, Depth = 1 },
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = (VkSampleCountFlags)1,
            Tiling = (VkImageTiling)0,
            Usage = (VkImageUsageFlags)0x6,             // TRANSFER_DST + SAMPLED
            SharingMode = (VkSharingMode)0,
            InitialLayout = (VkImageLayout)0,
        };
        var texture = Finish(info, format);

        var (staging, stagingMemory) = VkUtil.CreateHostBuffer(_device, _ctx.Physical, (VkBufferUsageFlags)0x1, (ulong)data.Length);
        _device.WriteMemory<byte>(data, stagingMemory, default, default);
        var region = new VkBufferImageCopy
        {
            ImageSubresource = new VkImageSubresourceLayers { AspectMask = (VkImageAspectFlags)1, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1 },
            ImageExtent = new VkExtent3D { Width = width, Height = height, Depth = 1 },
        };
        var image = texture.Image;
        _ctx.ExecuteImmediate(cmd =>
        {
            VkUtil.Transition(cmd, image, 1, 0, 7, 0x1, 0x1000, 0, 0x1000);
            cmd.CopyBufferToImage(staging, image, (VkImageLayout)7, new[] { region });
            VkUtil.Transition(cmd, image, 1, 7, 5, 0x1000, 0x80, 0x1000, 0x20);
        });
        _device.DestroyBuffer(staging, null);
        _device.FreeMemory(stagingMemory, null);
        return texture;
    }

    private Texture Finish(VkImageCreateInfo info, int format)
    {
        var image = _device.CreateImage(in info, null);
        var memory = _device.AllocateMemory(_ctx.Physical, image, (VkMemoryPropertyFlags)1, null);
        _device.BindImageMemory(image, memory, (ByteSize64)0);
        var viewInfo = new VkImageViewCreateInfo
        {
            Image = image,
            ViewType = (VkImageViewType)1,
            Format = (VkFormat)format,
            SubresourceRange = new VkImageSubresourceRange { AspectMask = (VkImageAspectFlags)1, LevelCount = 1, LayerCount = 1 },
        };
        return new Texture(image, memory, _device.CreateImageView(in viewInfo, null));
    }

    private void Destroy(Texture? texture)
    {
        if (texture is not { } t) return;
        _device.DestroyImageView(t.View, null);
        _device.DestroyImage(t.Image, null);
        _device.FreeMemory(t.Memory, null);
    }

    private void DestroyIntermediates()
    {
        Destroy(_edges);
        Destroy(_weights);
        Destroy(_graded);
        _edges = _weights = _graded = null;
    }

    // ---- pipelines -------------------------------------------------------------------------------------------------------

    private VkPipeline PipelineFxaa() =>
        _fxaa ??= CreatePipeline("fxaa", PostProcessShaders.FullscreenVertex, PostProcessShaders.FxaaFragment, (int)_ctx.ColorFormat);

    private VkPipeline Pipeline(ref VkPipeline? cache, string name, string vertex, string fragment, int format) =>
        cache ??= CreatePipeline(name, vertex, fragment, format);

    // A pipeline for a full-screen triangle into one color target: no vertex data, no depth, no blending.
    private VkPipeline CreatePipeline(string name, string vertexGlsl, string fragmentGlsl, int colorFormat)
    {
        var vert = _shaders.Get(name + ".vert", vertexGlsl, ShaderKind.VertexShader);
        var frag = _shaders.Get(name + ".frag", fragmentGlsl, ShaderKind.FragmentShader);
        byte[] mainName = "main\0"u8.ToArray();
        var colorFormats = new[] { (VkFormat)colorFormat };
        var viewport = new VkViewport { Width = _ctx.Width, Height = _ctx.Height, MinDepth = 0, MaxDepth = 1 };
        var scissor = new VkRect2D { Extent = new VkExtent2D { Width = _ctx.Width, Height = _ctx.Height } };
        var blend = new VkPipelineColorBlendAttachmentState { ColorWriteMask = (VkColorComponentFlags)0xF };
        var dynamicStates = new[] { (VkDynamicState)0, (VkDynamicState)1 };

        fixed (byte* entry = mainName)
        fixed (VkFormat* colorFormatsPtr = colorFormats)
        fixed (VkDynamicState* dynamicStatesPtr = dynamicStates)
        {
            var stages = new[]
            {
                new VkPipelineShaderStageCreateInfo { Stage = (VkShaderStageFlags)0x1, Module = vert, Name = entry },
                new VkPipelineShaderStageCreateInfo { Stage = (VkShaderStageFlags)0x10, Module = frag, Name = entry },
            };
            fixed (VkPipelineShaderStageCreateInfo* stagesPtr = stages)
            {
                var vertexInput = new VkPipelineVertexInputStateCreateInfo();
                var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = (VkPrimitiveTopology)3 };
                var viewportState = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, Viewports = &viewport, ScissorCount = 1, Scissors = &scissor };
                var raster = new VkPipelineRasterizationStateCreateInfo
                {
                    PolygonMode = (VkPolygonMode)0, CullMode = (VkCullModeFlags)0, FrontFace = (VkFrontFace)0, LineWidth = 1f,
                };
                var multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = (VkSampleCountFlags)1 };
                var depthState = new VkPipelineDepthStencilStateCreateInfo { DepthTestEnable = false, DepthWriteEnable = false };
                var colorBlend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &blend };
                var dynamicState = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 2, DynamicStates = dynamicStatesPtr };
                var rendering = new VkPipelineRenderingCreateInfo { ColorAttachmentCount = 1, ColorAttachmentFormats = colorFormatsPtr };
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
        if (!_basicsReady) return;
        _ctx.WaitIdle();
        foreach (var pipeline in new[] { _fxaa, _effects, _smaaEdges, _smaaWeights, _smaaBlend })
            if (pipeline is { } p) _device.DestroyPipeline(p, null);
        DestroyIntermediates();
        Destroy(_area);
        Destroy(_search);
        _device.DestroyDescriptorPool(_pool, null);
        _device.DestroyPipelineLayout(_layout, null);
        _device.DestroyDescriptorSetLayout(_setLayout, null);
        _device.DestroySampler(_linear, null);
        _device.DestroySampler(_point, null);
    }
}
