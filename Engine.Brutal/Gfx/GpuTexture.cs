using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Engine.Core.Assets;

namespace Engine.Brutal.Gfx;

// An image on the GPU with its full mip chain, ready to sample in a fragment shader. 8-bit images are stored as
// R8G8B8A8_UNORM and HDR images as R16G16B16A16_SFLOAT. Colors are not converted to or from sRGB: the renderer lights
// in the same "display" space the screen shows, so a texture's colors are used exactly as stored.
public sealed unsafe class GpuTexture : IDisposable
{
    private const int FormatRgba8 = 37;       // VK_FORMAT_R8G8B8A8_UNORM
    private const int FormatRgba16F = 97;     // VK_FORMAT_R16G16B16A16_SFLOAT

    private readonly Device _device;

    public VkImage Image { get; }
    public VkDeviceMemory Memory { get; }
    public VkImageView View { get; }
    public int MipLevels { get; }
    public bool IsHdr { get; }

    private GpuTexture(Device device, VkImage image, VkDeviceMemory memory, VkImageView view, int mipLevels, bool hdr)
    {
        _device = device;
        Image = image;
        Memory = memory;
        View = view;
        MipLevels = mipLevels;
        IsHdr = hdr;
    }

    // A 1x1 texture of one color, used where a material has no map.
    public static GpuTexture Solid(VulkanContext ctx, byte r, byte g, byte b, byte a) =>
        Create(ctx, [new ImageLevel(1, 1, [r, g, b, a], null)]);

    public static GpuTexture Create(VulkanContext ctx, IReadOnlyList<ImageLevel> levels)
    {
        bool hdr = levels[0].FloatPixels is not null;
        int bytesPerPixel = hdr ? 8 : 4;

        // All levels go into one staging buffer, one after another.
        var offsets = new long[levels.Count];
        long total = 0;
        for (int i = 0; i < levels.Count; i++)
        {
            offsets[i] = total;
            total += (long)levels[i].Width * levels[i].Height * bytesPerPixel;
        }
        var data = new byte[total];
        for (int i = 0; i < levels.Count; i++)
        {
            if (hdr)
            {
                var floats = levels[i].FloatPixels!;
                var halves = new Half[floats.Length];
                for (int p = 0; p < floats.Length; p++)
                    halves[p] = (Half)Math.Clamp(floats[p], 0f, 65000f);
                MemoryMarshal.AsBytes<Half>(halves).CopyTo(data.AsSpan((int)offsets[i]));
            }
            else
            {
                levels[i].Pixels!.CopyTo(data, (int)offsets[i]);
            }
        }

        var device = ctx.Device;
        var physical = ctx.Physical;
        int format = hdr ? FormatRgba16F : FormatRgba8;

        var imageInfo = new VkImageCreateInfo
        {
            ImageType = (VkImageType)1,
            Format = (VkFormat)format,
            Extent = new VkExtent3D { Width = levels[0].Width, Height = levels[0].Height, Depth = 1 },
            MipLevels = levels.Count,
            ArrayLayers = 1,
            Samples = (VkSampleCountFlags)1,
            Tiling = (VkImageTiling)0,
            Usage = (VkImageUsageFlags)(0x2 | 0x4),     // transfer destination + sampled
            SharingMode = (VkSharingMode)0,
            InitialLayout = (VkImageLayout)0,
        };
        var image = device.CreateImage(in imageInfo, null);
        var memory = device.AllocateMemory(physical, image, (VkMemoryPropertyFlags)1, null);
        device.BindImageMemory(image, memory, (ByteSize64)0);

        var viewInfo = new VkImageViewCreateInfo
        {
            Image = image,
            ViewType = (VkImageViewType)1,
            Format = (VkFormat)format,
            SubresourceRange = new VkImageSubresourceRange { AspectMask = (VkImageAspectFlags)1, LevelCount = levels.Count, LayerCount = 1 },
        };
        var view = device.CreateImageView(in viewInfo, null);

        var (staging, stagingMemory) = VkUtil.CreateHostBuffer(device, physical, (VkBufferUsageFlags)0x1, (ulong)total);
        device.WriteMemory<byte>(data, stagingMemory, default, default);

        var regions = new VkBufferImageCopy[levels.Count];
        for (int i = 0; i < levels.Count; i++)
            regions[i] = new VkBufferImageCopy
            {
                BufferOffset = (ByteSize64)(ulong)offsets[i],
                ImageSubresource = new VkImageSubresourceLayers { AspectMask = (VkImageAspectFlags)1, MipLevel = i, BaseArrayLayer = 0, LayerCount = 1 },
                ImageExtent = new VkExtent3D { Width = levels[i].Width, Height = levels[i].Height, Depth = 1 },
            };

        int count = levels.Count;
        ctx.ExecuteImmediate(cmd =>
        {
            // Undefined -> transfer destination, copy every level, then -> shader read only.
            VkUtil.Transition(cmd, image, 1, 0, 7, 0x1, 0x1000, 0, 0x1000, count);
            cmd.CopyBufferToImage(staging, image, (VkImageLayout)7, regions);
            VkUtil.Transition(cmd, image, 1, 7, 5, 0x1000, 0x80, 0x1000, 0x20, count);
        });

        device.DestroyBuffer(staging, null);
        device.FreeMemory(stagingMemory, null);
        return new GpuTexture(device, image, memory, view, levels.Count, hdr);
    }

    public void Dispose()
    {
        _device.DestroyImageView(View, null);
        _device.DestroyImage(Image, null);
        _device.FreeMemory(Memory, null);
    }
}
