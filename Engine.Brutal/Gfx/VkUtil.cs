using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace Engine.Brutal.Gfx;

internal static unsafe class VkUtil
{
    public static void Transition(CommandBuffer cmd, VkImage image, int aspect,
        int oldLayout, int newLayout, ulong srcStage, ulong dstStage, ulong srcAccess, ulong dstAccess, int levelCount = 1)
    {
        var barrier = new VkImageMemoryBarrier2
        {
            SrcStageMask = (VkPipelineStageFlags2)srcStage,
            SrcAccessMask = (VkAccessFlags2)srcAccess,
            DstStageMask = (VkPipelineStageFlags2)dstStage,
            DstAccessMask = (VkAccessFlags2)dstAccess,
            OldLayout = (VkImageLayout)oldLayout,
            NewLayout = (VkImageLayout)newLayout,
            SrcQueueFamilyIndex = -1,
            DstQueueFamilyIndex = -1,
            Image = image,
            SubresourceRange = new VkImageSubresourceRange
            {
                AspectMask = (VkImageAspectFlags)aspect,
                LevelCount = levelCount,
                LayerCount = 1,
            },
        };
        var dependency = new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &barrier };
        cmd.PipelineBarrier2(in dependency);
    }

    public static VkShaderModule CreateShaderModule(Device device, byte[] spirv)
    {
        fixed (byte* code = spirv)
        {
            var info = new VkShaderModuleCreateInfo
            {
                CodeSize = (ByteSize64)(ulong)spirv.Length,
                Code = (int*)code,
            };
            return device.CreateShaderModule(in info, null);
        }
    }

    // Host-visible + coherent so the CPU can write directly. Simple, not the fastest path.
    public static (VkBuffer Buffer, VkDeviceMemory Memory) CreateHostBuffer(
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
}
