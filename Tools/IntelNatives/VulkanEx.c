// libVulkanEx for Intel Macs: the four batch-destroy helpers BRUTAL's Vulkan bindings expect next to the Vulkan loader.
// Each takes the device, an array of handles, how many there are and the allocator; it destroys every non-null handle
// and sets it to null.
#include <stdint.h>

typedef struct VkDevice_T* VkDevice;
typedef uint64_t VkHandle;
typedef void VkAllocationCallbacks;

#ifdef __linux__
// Linux builds have no Vulkan library to link against (the cross compiler has none), so the loader's functions are looked up the
// first time one is needed.
#include <dlfcn.h>
#define VKFN(fn) static void (*fn)(VkDevice, VkHandle, const VkAllocationCallbacks*); \
    static void load_##fn(void) { if (!fn) { void* lib = dlopen("libvulkan.so.1", RTLD_NOW | RTLD_GLOBAL); if (lib) *(void**)&fn = dlsym(lib, #fn); } }
VKFN(vkDestroyImage) VKFN(vkDestroyImageView) VKFN(vkDestroyBuffer) VKFN(vkDestroyBufferView)
#define LOAD(fn) load_##fn(); if (!fn) return
#else
extern void vkDestroyImage(VkDevice, VkHandle, const VkAllocationCallbacks*);
extern void vkDestroyImageView(VkDevice, VkHandle, const VkAllocationCallbacks*);
extern void vkDestroyBuffer(VkDevice, VkHandle, const VkAllocationCallbacks*);
extern void vkDestroyBufferView(VkDevice, VkHandle, const VkAllocationCallbacks*);
#define LOAD(fn)
#endif

#define EXPORT __attribute__((visibility("default")))
#define BATCH(name, fn) \
    EXPORT void name(VkDevice device, VkHandle* handles, int32_t count, const VkAllocationCallbacks* allocator) \
    { \
        if (!handles) return; \
        LOAD(fn); \
        for (int32_t i = 0; i < count; i++) \
            if (handles[i]) { fn(device, handles[i], allocator); handles[i] = 0; } \
    }

BATCH(vxDestroyImages, vkDestroyImage)
BATCH(vxDestroyImageViews, vkDestroyImageView)
BATCH(vxDestroyBuffers, vkDestroyBuffer)
BATCH(vxDestroyBufferViews, vkDestroyBufferView)
