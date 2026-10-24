using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Engine.Core.Assets;

namespace Engine.Brutal.Gfx;

// What every material needs to be drawn with textures: the layout of a material's descriptor set (five samplers, set 1
// of the model pipeline), one shared sampler, and plain 1x1 textures standing in for maps a material doesn't have.
//
// Bindings: 0 base color, 1 normal, 2 metallic-roughness (green = roughness, blue = metallic), 3 emissive, 4 occlusion
// (red). A missing map is white, which leaves the material's factors unchanged, except the normal map, which is
// "flat": the shader skips normal mapping unless the material really has one.
public sealed unsafe class GpuMaterials : IDisposable
{
    public const int TextureSlots = 5;

    private readonly VulkanContext _ctx;
    private readonly Device _device;
    private readonly GpuTexture _white;
    private readonly GpuTexture _flatNormal;

    public VkDescriptorSetLayout SetLayout { get; }
    public VkSampler Sampler { get; }

    public GpuMaterials(VulkanContext ctx)
    {
        _ctx = ctx;
        _device = ctx.Device;
        _white = GpuTexture.Solid(ctx, 255, 255, 255, 255);
        _flatNormal = GpuTexture.Solid(ctx, 128, 128, 255, 255);

        // Trilinear filtering with repeat wrapping: how glTF samples by default.
        var samplerInfo = new VkSamplerCreateInfo
        {
            MagFilter = (VkFilter)1,
            MinFilter = (VkFilter)1,
            MipmapMode = (VkSamplerMipmapMode)1,
            AddressModeU = (VkSamplerAddressMode)0,
            AddressModeV = (VkSamplerAddressMode)0,
            AddressModeW = (VkSamplerAddressMode)0,
            MinLod = 0f,
            MaxLod = 16f,
        };
        Sampler = _device.CreateSampler(in samplerInfo, null);

        var bindings = new VkDescriptorSetLayoutBinding[TextureSlots];
        for (int i = 0; i < TextureSlots; i++)
            bindings[i] = new VkDescriptorSetLayoutBinding
            {
                Binding = i,
                DescriptorType = (VkDescriptorType)1,       // combined image sampler
                DescriptorCount = 1,
                StageFlags = (VkShaderStageFlags)0x10,      // fragment
            };
        fixed (VkDescriptorSetLayoutBinding* b = bindings)
        {
            var info = new VkDescriptorSetLayoutCreateInfo { BindingCount = TextureSlots, Bindings = b };
            SetLayout = _device.CreateDescriptorSetLayout(in info, null);
        }
    }

    // A pool for one model's material sets. Destroying the pool frees every set made from it.
    public VkDescriptorPool CreatePool(int maxSets)
    {
        var size = new VkDescriptorPoolSize { Type = (VkDescriptorType)1, DescriptorCount = maxSets * TextureSlots };
        var info = new VkDescriptorPoolCreateInfo { MaxSets = maxSets, PoolSizeCount = 1, PoolSizes = &size };
        return _device.CreateDescriptorPool(in info, null);
    }

    // A descriptor set for a material. resolve turns each of its images into a GPU texture (or null for none).
    public VkDescriptorSet CreateSet(VkDescriptorPool pool, MaterialData material, Func<ImageData?, GpuTexture?> resolve)
    {
        var layout = SetLayout;
        var allocInfo = new VkDescriptorSetAllocateInfo { DescriptorPool = pool, DescriptorSetCount = 1, SetLayouts = &layout };
        VkDescriptorSet set;
        var allocate = (delegate* unmanaged<VkDevice, VkDescriptorSetAllocateInfo*, VkDescriptorSet*, int>)
            _ctx.DeviceEx.GetProcAddr("vkAllocateDescriptorSets"u8).Invoke;
        int result = allocate(_device.Handle, &allocInfo, &set);
        if (result != 0)
            throw new InvalidOperationException($"vkAllocateDescriptorSets failed with {result}");

        var views = new[]
        {
            (resolve(material.BaseColorImage) ?? _white).View,
            (resolve(material.NormalImage) ?? _flatNormal).View,
            (resolve(material.MetallicRoughnessImage) ?? _white).View,
            (resolve(material.EmissiveImage) ?? _white).View,
            (resolve(material.OcclusionImage) ?? _white).View,
        };
        var images = stackalloc VkDescriptorImageInfo[TextureSlots];
        var writes = new VkWriteDescriptorSet[TextureSlots];
        for (int i = 0; i < TextureSlots; i++)
        {
            images[i] = new VkDescriptorImageInfo { ImageView = views[i], Sampler = Sampler, ImageLayout = (VkImageLayout)5 };
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
        return set;
    }

    public void Dispose()
    {
        _device.DestroySampler(Sampler, null);
        _device.DestroyDescriptorSetLayout(SetLayout, null);
        _white.Dispose();
        _flatNormal.Dispose();
    }
}
