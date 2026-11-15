using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Engine.Brutal.Gfx;

namespace Engine.Brutal.Ui;

// Canvas2D's GPU part with Vulkan: a host-visible vertex buffer per frame in flight, the atlas and pictures as storage buffers in one
// descriptor set, and a pipeline for the context's UI render pass.
internal sealed unsafe class VulkanCanvasGpu : ICanvasGpu
{
    private const int FloatsPerVertex = Canvas2D.FloatsPerVertex;
    private const int MaxVertices = Canvas2D.MaxVertices;
    private const int StageVertexFragment = 0x11;
    private const int StageFragment = 0x10;
    private const int BufferVertex = 0x80;
    private const int BufferStorage = 0x20;
    private const int DescriptorStorageBuffer = 7;

    private readonly VulkanContext _ctx;
    private readonly Device _device;
    private readonly PhysicalDevice _physical;
    private VkBuffer _atlasBuffer;
    private VkDeviceMemory _atlasMemory;
    private ulong _atlasBytes;
    private bool _hasAtlas;
    private VkBuffer _imageBuffer;
    private VkDeviceMemory _imageMemory;
    private ulong _imageBytes;
    private bool _hasImageBuffer;
    private readonly VkDescriptorSetLayout _setLayout;
    private readonly VkDescriptorPool _pool;
    private readonly VkDescriptorSet _set;
    private readonly VkPipelineLayout _layout;
    private readonly VkPipeline _pipeline;
    private readonly VkBuffer[] _vertexBuffers = new VkBuffer[VulkanContext.FramesInFlight];
    private readonly VkDeviceMemory[] _vertexMemories = new VkDeviceMemory[VulkanContext.FramesInFlight];
    private int _frameSlot;

    public VulkanCanvasGpu(VulkanContext ctx, ShaderLibrary shaders)
    {
        _ctx = ctx;
        _device = ctx.Device;
        _physical = ctx.Physical;

        for (int slot = 0; slot < VulkanContext.FramesInFlight; slot++)
            (_vertexBuffers[slot], _vertexMemories[slot]) = CreateHostBuffer(_device, _physical,
                (VkBufferUsageFlags)BufferVertex, (ulong)(MaxVertices * FloatsPerVertex * sizeof(float)));

        _setLayout = CreateSetLayout();
        _pool = CreateDescriptorPool();
        _set = AllocateDescriptorSet();
        _layout = CreateLayout();
        _pipeline = CreatePipeline(shaders);
    }

    public void SetAtlas(float[] coverage)
    {
        if (_hasAtlas)
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
        _hasAtlas = true;
        WriteDescriptors();
    }

    public void SetImages(uint[] pixels)
    {
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
        _hasImageBuffer = true;
        WriteDescriptors();
    }

    // Records the shapes into the UI render pass.
    public void Draw(UiPass pass, ReadOnlySpan<float> vertices, int vertexCount, float[] push, float pixelW, float pixelH)
    {
        var cmd = pass.Vulkan;
        int slot = _frameSlot;
        _frameSlot = (_frameSlot + 1) % VulkanContext.FramesInFlight;
        _device.WriteMemory<float>(vertices, _vertexMemories[slot], default, default);

        cmd.BindPipeline((VkPipelineBindPoint)0, _pipeline);
        cmd.SetViewport(0, new[] { new VkViewport { Width = pixelW, Height = pixelH, MinDepth = 0, MaxDepth = 1 } });
        cmd.SetScissor(0, new[] { new VkRect2D { Extent = new VkExtent2D { Width = (int)MathF.Ceiling(pixelW), Height = (int)MathF.Ceiling(pixelH) } } });
        cmd.BindDescriptorSets((VkPipelineBindPoint)0, _layout, 0, new[] { _set }, default);
        cmd.PushConstants(_layout, (VkShaderStageFlags)StageVertexFragment, default, push);
        cmd.BindVertexBuffer(0, _vertexBuffers[slot], default);
        cmd.Draw(vertexCount, 1, 0, 0);
    }

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

    // Points the descriptor set at the current glyph atlas and picture buffers, once both exist.
    private void WriteDescriptors()
    {
        if (!_hasAtlas || !_hasImageBuffer) return;
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
        var vert = shaders.Get(Canvas2D.VertexShader);
        var frag = shaders.Get(Canvas2D.FragmentShader);
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
