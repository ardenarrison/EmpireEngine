using System.Runtime.InteropServices;
using Brutal;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.Pointers;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace Engine.Brutal.Gfx;

// Owns instance, device, swapchain, depth buffer, a UI render pass, command buffers and per-frame sync.
public sealed unsafe class VulkanContext : IDisposable
{
    public const int FramesInFlight = 2;
    public const int DepthFormat = 126;                 // VK_FORMAT_D32_SFLOAT
    public const int LayoutDepthAttachment = 1000241000; // VK_IMAGE_LAYOUT_DEPTH_ATTACHMENT_OPTIMAL
    public const int LayoutPresentSrc = 1000001002;

    private const int ColorAspect = 0x1;
    private const int DepthAspect = 0x2;

    private readonly Instance _instance;
    private readonly VkSurfaceKHR _surface;
    private readonly PhysicalDevice _physical;
    private readonly DeviceEx _device;
    private readonly Queue _queue;
    private readonly int _queueFamily;
    private readonly GlfwWindow _window;
    private readonly delegate* unmanaged<VkQueue, VkPresentInfoKHR*, int> _presentFn;

    private VkSwapchainKHR _swapchain;
    private VkImage[] _images = [];
    private VkImageView[] _views = [];
    private VkExtent2D _extent;
    private VkFormat _colorFormat;

    private VkImage _depthImage;
    private VkDeviceMemory _depthMemory;
    private VkImageView _depthView;

    // With MSAA the scene is drawn into these multisampled images and resolved into the swapchain image.
    private VkImage _msaaImage;
    private VkDeviceMemory _msaaMemory;
    private VkImageView _msaaView;
    private int _samples = 1;

    private VkRenderPass _uiPass;
    private VkFramebuffer[] _framebuffers = [];

    private VkCommandPool _pool;
    private CommandBuffer[] _cmds = [];
    private VkSemaphore[] _imageAvailable = [];
    private VkSemaphore[] _renderFinished = [];
    private VkFence[] _inFlight = [];

    private int _frame;
    private int _imageIndex;

    public VulkanContext(GlfwWindow window, int fallbackWidth, int fallbackHeight)
    {
        _window = window;
        var extensions = new HashSet<string>();
        byte** glfwExts = Glfw.GetRequiredInstanceExtensions(out uint extCount);
        for (int i = 0; i < extCount; i++)
            extensions.Add(Marshal.PtrToStringUTF8((nint)glfwExts[i])!);
        extensions.Add("VK_KHR_portability_enumeration");
        extensions.Add("VK_KHR_get_physical_device_properties2");

        _instance = Vulkan.CreateInstance(new Instance.CreateInfo
        {
            AppInfo = new VkApplicationInfo { ApiVersion = (1 << 22) | (3 << 12) },
            Flags = VkInstanceCreateFlags.EnumeratePortabilityBitKHR,
            EnabledExtensions = extensions,
            // Set EMPIRE_VULKAN_VALIDATION=1 to turn on the Khronos validation layer (it needs the Vulkan SDK or a
            // package that provides it). It reports misuse of the Vulkan API on the console, which is the way to
            // track down a VK_ERROR_DEVICE_LOST.
            EnabledLayers = Environment.GetEnvironmentVariable("EMPIRE_VULKAN_VALIDATION") == "1"
                ? new HashSet<string> { "VK_LAYER_KHRONOS_validation" }
                : new HashSet<string>(),
        });

        Glfw.CreateWindowSurface(_instance.Handle.VkHandle, window, out ulong surfaceRaw);
        _surface = new VkSurfaceKHR((nint)surfaceRaw);

        _physical = _instance.PhysicalDevices[0];
        _queueFamily = _physical.GetFirstQueueFamilyWithFlag((VkQueueFlags)1);

        float priority = 1f;
        var queueInfo = new VkDeviceQueueCreateInfo
        {
            QueueFamilyIndex = _queueFamily,
            QueueCount = 1,
            QueuePriorities = &priority,
        };
        var deviceExts = new HashSet<string> { "VK_KHR_swapchain" };
        if (_physical.SupportsExtension("VK_KHR_portability_subset"u8))
            deviceExts.Add("VK_KHR_portability_subset");

        // dynamic rendering + synchronization2 are core in 1.3 but must be enabled explicitly.
        var features13 = new VkPhysicalDeviceVulkan13Features
        {
            DynamicRendering = true,
            Synchronization2 = true,
        };
        _device = _physical.CreateDevice(new DeviceEx.CreateInfo
        {
            Extensions = deviceExts,
            QueueCreateInfos = new[] { queueInfo },
            Next = (Ptr)(&features13),
        });
        _queue = _device.GetQueue(_queueFamily, 0);
        _presentFn = (delegate* unmanaged<VkQueue, VkPresentInfoKHR*, int>)
            _device.GetProcAddr("vkQueuePresentKHR"u8).Invoke;

        MaxSampleCount = QueryMaxSampleCount();
        CreateSwapchain(fallbackWidth, fallbackHeight);
        CreateDepthBuffer();
        CreateUiPass();
        CreateFramebuffers();
        CreateFrameResources();
        Gpu.Attach(this);
    }

    public DeviceEx DeviceEx => _device;
    public Device Device => _device;
    public PhysicalDevice Physical => _physical;
    public Queue Queue => _queue;
    public VkFormat ColorFormat => _colorFormat;
    public VkRenderPass UiRenderPass => _uiPass;
    public int ImageCount => _images.Length;

    // Samples per pixel the scene is drawn with: 1 (no MSAA), 2, 4 or 8. Pipelines for the scene pass must be
    // created with this count (see Gpu.SampleCount).
    public int SampleCount => _samples;

    // The most samples per pixel this GPU can do for both color and depth (1 when it can't do MSAA).
    public int MaxSampleCount { get; }

    // The nearest sample count the GPU supports that isn't more than requested: 1, 2, 4 or 8.
    public int SupportedSampleCount(int requested)
    {
        for (int count = 8; count >= 2; count /= 2)
            if (count <= requested && count <= MaxSampleCount) return count;
        return 1;
    }

    // Changes how many samples per pixel the scene uses (clamped to what the GPU supports). Waits for the GPU and
    // rebuilds the depth and color images, so call it between frames, and only when the value changes.
    public void SetSampleCount(int requested)
    {
        int samples = SupportedSampleCount(requested);
        if (samples == _samples) return;

        _device.WaitIdle();
        DestroySceneTargets();
        _samples = samples;
        CreateDepthBuffer();
        SampleCountChanged?.Invoke(samples);
    }

    // Raised after the sample count changes, so things that depend on it (pipelines) can follow.
    public event Action<int>? SampleCountChanged;

    // Both color and depth must support a count for a pass that uses both, so take what they share.
    private int QueryMaxSampleCount()
    {
        var limits = _physical.GetProperties().Limits;
        var shared = (int)limits.FramebufferColorSampleCounts & (int)limits.FramebufferDepthSampleCounts;
        for (int count = 8; count >= 2; count /= 2)
            if ((shared & count) != 0) return count;
        return 1;
    }
    public int Width => _extent.Width;
    public int Height => _extent.Height;

    // The scene is always drawn at 16:9. Any leftover space is left black, so the picture is never stretched.
    public const float SceneAspect = 16f / 9f;

    // Share of the window height kept free at the bottom for editor panels. The scene is fitted above it.
    public float BottomReservedFraction { get; set; }

    // The part of the window the scene fills, as fractions: x, y, width, height.
    public float4 SceneViewport
    {
        get
        {
            float areaW = _extent.Width;
            float areaH = _extent.Height * (1f - Math.Clamp(BottomReservedFraction, 0f, 0.9f));
            float w, h;
            if (areaW / areaH > SceneAspect)
            {
                h = areaH;
                w = h * SceneAspect;
            }
            else
            {
                w = areaW;
                h = w / SceneAspect;
            }
            return new float4(
                (areaW - w) / 2f / _extent.Width,
                (areaH - h) / 2f / _extent.Height,
                w / _extent.Width,
                h / _extent.Height);
        }
    }

    public bool IsMinimized => _window.FramebufferSize.X == 0 || _window.FramebufferSize.Y == 0;
    public CommandBuffer CurrentCommandBuffer => _cmds[_frame];
    public int FrameIndex => _frame;   // 0 .. FramesInFlight - 1

    private void CreateSwapchain(int fallbackWidth, int fallbackHeight)
    {
        var caps = _physical.GetSurfaceCapabilitiesKHR(_surface);
        int formatCount = _physical.GetSurfaceFormatsKHR_Count(_surface);
        var formats = new VkSurfaceFormatKHR[formatCount];
        _physical.GetSurfaceFormatsKHR_Write(_surface, formats);

        int width = caps.CurrentExtent.Width, height = caps.CurrentExtent.Height;
        if (width <= 0 || width == -1) { width = fallbackWidth; height = fallbackHeight; }
        _extent = new VkExtent2D { Width = width, Height = height };
        _colorFormat = formats[0].Format;

        int minImages = caps.MinImageCount + 1;
        if (caps.MaxImageCount > 0 && minImages > caps.MaxImageCount) minImages = caps.MaxImageCount;

        var swapInfo = new VkSwapchainCreateInfoKHR
        {
            Surface = _surface,
            MinImageCount = minImages,
            ImageFormat = _colorFormat,
            ImageColorSpace = formats[0].ColorSpace,
            ImageExtent = _extent,
            ImageArrayLayers = 1,
            ImageUsage = (VkImageUsageFlags)0x10,        // COLOR_ATTACHMENT
            ImageSharingMode = (VkSharingMode)0,
            PreTransform = caps.CurrentTransform,
            CompositeAlpha = (VkCompositeAlphaFlagsKHR)1,
            PresentMode = (VkPresentModeKHR)2,           // FIFO (vsync)
        };
        _swapchain = _device.CreateSwapchainKHR(in swapInfo, null);

        int imageCount = _device.GetSwapchainImagesKHR_Count(_swapchain);
        _images = new VkImage[imageCount];
        _device.GetSwapchainImagesKHR_Write(_swapchain, _images);

        _views = new VkImageView[imageCount];
        for (int i = 0; i < imageCount; i++)
        {
            var viewInfo = new VkImageViewCreateInfo
            {
                Image = _images[i],
                ViewType = (VkImageViewType)1,
                Format = _colorFormat,
                SubresourceRange = new VkImageSubresourceRange
                {
                    AspectMask = (VkImageAspectFlags)ColorAspect,
                    LevelCount = 1,
                    LayerCount = 1,
                },
            };
            _views[i] = _device.CreateImageView(in viewInfo, null);
        }
    }

    private void CreateDepthBuffer()
    {
        var imageInfo = new VkImageCreateInfo
        {
            ImageType = (VkImageType)1,
            Format = (VkFormat)DepthFormat,
            Extent = new VkExtent3D { Width = _extent.Width, Height = _extent.Height, Depth = 1 },
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = (VkSampleCountFlags)_samples,
            Tiling = (VkImageTiling)0,
            Usage = (VkImageUsageFlags)0x20,           // DEPTH_STENCIL_ATTACHMENT
            SharingMode = (VkSharingMode)0,
            InitialLayout = (VkImageLayout)0,
        };
        _depthImage = _device.CreateImage(in imageInfo, null);
        _depthMemory = _device.AllocateMemory(_physical, _depthImage, (VkMemoryPropertyFlags)1, null);
        _device.BindImageMemory(_depthImage, _depthMemory, (ByteSize64)0);

        var viewInfo = new VkImageViewCreateInfo
        {
            Image = _depthImage,
            ViewType = (VkImageViewType)1,
            Format = (VkFormat)DepthFormat,
            SubresourceRange = new VkImageSubresourceRange
            {
                AspectMask = (VkImageAspectFlags)DepthAspect,
                LevelCount = 1,
                LayerCount = 1,
            },
        };
        _depthView = _device.CreateImageView(in viewInfo, null);

        if (_samples > 1)
            CreateMsaaColor();
    }

    // The multisampled color image the scene is drawn into when MSAA is on. It is resolved into the swapchain
    // image and never read afterwards, so its contents don't need to be kept.
    private void CreateMsaaColor()
    {
        var imageInfo = new VkImageCreateInfo
        {
            ImageType = (VkImageType)1,
            Format = _colorFormat,
            Extent = new VkExtent3D { Width = _extent.Width, Height = _extent.Height, Depth = 1 },
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = (VkSampleCountFlags)_samples,
            Tiling = (VkImageTiling)0,
            Usage = (VkImageUsageFlags)0x10,           // COLOR_ATTACHMENT
            SharingMode = (VkSharingMode)0,
            InitialLayout = (VkImageLayout)0,
        };
        _msaaImage = _device.CreateImage(in imageInfo, null);
        _msaaMemory = _device.AllocateMemory(_physical, _msaaImage, (VkMemoryPropertyFlags)1, null);
        _device.BindImageMemory(_msaaImage, _msaaMemory, (ByteSize64)0);

        var viewInfo = new VkImageViewCreateInfo
        {
            Image = _msaaImage,
            ViewType = (VkImageViewType)1,
            Format = _colorFormat,
            SubresourceRange = new VkImageSubresourceRange
            {
                AspectMask = (VkImageAspectFlags)ColorAspect,
                LevelCount = 1,
                LayerCount = 1,
            },
        };
        _msaaView = _device.CreateImageView(in viewInfo, null);
    }

    // Frees the depth image and, when MSAA is on, the multisampled color image.
    private void DestroySceneTargets()
    {
        _device.DestroyImageView(_depthView, null);
        _device.DestroyImage(_depthImage, null);
        _device.FreeMemory(_depthMemory, null);
        if (_samples > 1)
        {
            _device.DestroyImageView(_msaaView, null);
            _device.DestroyImage(_msaaImage, null);
            _device.FreeMemory(_msaaMemory, null);
        }
    }

    // A classic render pass for the UI. It loads the scene that is already in the color attachment,
    // and its final layout moves the image to PRESENT_SRC, so no separate present barrier is needed.
    private void CreateUiPass()
    {
        var attachments = new[]
        {
            new VkAttachmentDescription
            {
                Format = _colorFormat,
                Samples = (VkSampleCountFlags)1,
                LoadOp = (VkAttachmentLoadOp)0,          // LOAD
                StoreOp = (VkAttachmentStoreOp)0,        // STORE
                StencilLoadOp = (VkAttachmentLoadOp)2,   // DONT_CARE
                StencilStoreOp = (VkAttachmentStoreOp)1, // DONT_CARE
                InitialLayout = (VkImageLayout)2,        // COLOR_ATTACHMENT_OPTIMAL (left by the scene pass)
                FinalLayout = (VkImageLayout)LayoutPresentSrc,
            },
        };
        var refs = new[]
        {
            new VkAttachmentReference { Attachment = 0, Layout = (VkImageLayout)2 },
        };
        var deps = new[]
        {
            new VkSubpassDependency
            {
                SrcSubpass = -1,                          // VK_SUBPASS_EXTERNAL
                DstSubpass = 0,
                SrcStageMask = (VkPipelineStageFlags)0x400,
                DstStageMask = (VkPipelineStageFlags)0x400,
                SrcAccessMask = (VkAccessFlags)0x100,
                DstAccessMask = (VkAccessFlags)0x100,
            },
        };

        fixed (VkAttachmentDescription* a = attachments)
        fixed (VkAttachmentReference* r = refs)
        fixed (VkSubpassDependency* d = deps)
        {
            var subpass = new[]
            {
                new VkSubpassDescription
                {
                    PipelineBindPoint = (VkPipelineBindPoint)0,
                    ColorAttachmentCount = 1,
                    ColorAttachments = r,
                },
            };
            fixed (VkSubpassDescription* s = subpass)
            {
                var info = new VkRenderPassCreateInfo
                {
                    AttachmentCount = 1,
                    Attachments = a,
                    SubpassCount = 1,
                    Subpasses = s,
                    DependencyCount = 1,
                    Dependencies = d,
                };
                _uiPass = _device.CreateRenderPass(in info, null);
            }
        }
    }

    private void CreateFramebuffers()
    {
        _framebuffers = new VkFramebuffer[_views.Length];
        for (int i = 0; i < _views.Length; i++)
        {
            var views = new[] { _views[i] };
            fixed (VkImageView* v = views)
            {
                var info = new VkFramebufferCreateInfo
                {
                    RenderPass = _uiPass,
                    AttachmentCount = 1,
                    Attachments = v,
                    Width = _extent.Width,
                    Height = _extent.Height,
                    Layers = 1,
                };
                _framebuffers[i] = _device.CreateFramebuffer(in info, null);
            }
        }
    }

    private void CreateFrameResources()
    {
        _pool = _device.CreateCommandPool(new VkCommandPoolCreateInfo
        {
            QueueFamilyIndex = _queueFamily,
            Flags = (VkCommandPoolCreateFlags)0x2,
        }, null);

        _cmds = new CommandBuffer[FramesInFlight];
        _imageAvailable = new VkSemaphore[FramesInFlight];
        _inFlight = new VkFence[FramesInFlight];
        for (int i = 0; i < FramesInFlight; i++)
        {
            _cmds[i] = _device.AllocateCommandBuffer(new VkCommandBufferAllocateInfo
            {
                CommandPool = _pool,
                Level = (VkCommandBufferLevel)0,
            });
            _imageAvailable[i] = _device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);

            var fenceInfo = new VkFenceCreateInfo { Flags = (VkFenceCreateFlags)1 };
            _inFlight[i] = _device.CreateFence(in fenceInfo, null);
        }

        // One render-finished semaphore per swapchain image, so a pending present never shares one.
        _renderFinished = new VkSemaphore[_images.Length];
        for (int i = 0; i < _images.Length; i++)
            _renderFinished[i] = _device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
    }

    // Rebuilds the parts of the swapchain that depend on the window's pixel size. Runs when the window changes size.
    private void Recreate()
    {
        _device.WaitIdle();

        foreach (var semaphore in _renderFinished)
            _device.DestroySemaphore(semaphore, null);
        foreach (var framebuffer in _framebuffers)
            _device.DestroyFramebuffer(framebuffer, null);
        DestroySceneTargets();
        foreach (var view in _views)
            _device.DestroyImageView(view, null);
        _device.DestroySwapchainKHR(_swapchain, null);

        CreateSwapchain(_window.FramebufferSize.X, _window.FramebufferSize.Y);
        CreateDepthBuffer();
        CreateFramebuffers();

        _renderFinished = new VkSemaphore[_images.Length];
        for (int i = 0; i < _images.Length; i++)
            _renderFinished[i] = _device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
    }

    // Waits for this frame slot, acquires an image, and starts the scene's dynamic rendering pass.
    // Runs at the start of EndFrame, while the frame is still recording. The renderer uses it to record the
    // shadow pass and the scene draws once every draw for the frame is known.
    public Action? BeforeEndFrame { get; set; }
    private bool _scenePassOpen;

    public void BeginFrame(bool deferScenePass = false)
    {
        // The window changed size (resize or fullscreen), so the swapchain has to match it again.
        var size = _window.FramebufferSize;
        if (size.X != _extent.Width || size.Y != _extent.Height)
            Recreate();

        var fence = _inFlight[_frame];
        _device.WaitForFences(new[] { fence }, false, -1);
        _device.ResetFences(new[] { fence });
        _device.AcquireNextImageKHR(_swapchain, -1, _imageAvailable[_frame], default(VkFence), out int idx);
        _imageIndex = idx;

        var cmd = _cmds[_frame];
        cmd.Reset();
        cmd.Begin();

        // Pipeline stages: 0x100 early and 0x200 late fragment tests, 0x400 color attachment output.
        // The swapchain image comes back from the presentation engine at the color attachment output stage (that is the
        // stage the submit waits for the acquire semaphore at), so the barrier has to start there, not at the top of
        // the pipe, or it can run while the image is still being presented.
        VkUtil.Transition(cmd, _images[idx], ColorAspect, 0, 2, 0x400, 0x400, 0, 0x100);

        // The depth buffer (and the MSAA color image) is one image shared by every frame in flight, so this frame's
        // setup must wait for the previous frame's writes to it to finish before it clears and reuses it.
        VkUtil.Transition(cmd, _depthImage, DepthAspect, 0, LayoutDepthAttachment, 0x300, 0x100, 0x400, 0x400);
        if (_samples > 1)
            VkUtil.Transition(cmd, _msaaImage, ColorAspect, 0, 2, 0x400, 0x400, 0x180, 0x100);

        _scenePassOpen = false;
        if (!deferScenePass)
            BeginScenePass();
    }

    // Opens the scene pass: clears color and depth, then limits drawing to the 16:9 scene rectangle.
    public void BeginScenePass()
    {
        var cmd = _cmds[_frame];
        var color = new VkRenderingAttachmentInfo
        {
            ImageView = _views[_imageIndex],
            ImageLayout = (VkImageLayout)2,
            LoadOp = (VkAttachmentLoadOp)1,
            StoreOp = (VkAttachmentStoreOp)0,
            ClearValue = new VkClearValue
            {
                Color = new VkClearColorValue { Float32 = new float4(0.05f, 0.08f, 0.15f, 1f) },
            },
        };
        if (_samples > 1)
        {
            // Draw into the multisampled image; the GPU averages its samples into the swapchain image at the end.
            color.ImageView = _msaaView;
            color.ResolveMode = VkResolveModeFlags.AverageBit;
            color.ResolveImageView = _views[_imageIndex];
            color.ResolveImageLayout = (VkImageLayout)2;
            color.StoreOp = (VkAttachmentStoreOp)1;   // DONT_CARE: only the resolved result is needed
        }
        var depth = new VkRenderingAttachmentInfo
        {
            ImageView = _depthView,
            ImageLayout = (VkImageLayout)LayoutDepthAttachment,
            LoadOp = (VkAttachmentLoadOp)1,
            StoreOp = (VkAttachmentStoreOp)1,
            ClearValue = new VkClearValue
            {
                DepthStencil = new VkClearDepthStencilValue { Depth = 1f, Stencil = 0 },
            },
        };
        var renderInfo = new VkRenderingInfo
        {
            RenderArea = new VkRect2D { Extent = _extent },
            LayerCount = 1,
            ColorAttachmentCount = 1,
            ColorAttachments = &color,
            DepthAttachment = &depth,
        };
        cmd.BeginRendering(in renderInfo);

        // The scene is drawn inside its 16:9 rectangle, so the viewport and scissor cover only that part.
        var rect = SceneViewport;
        int x = (int)(rect.X * _extent.Width), y = (int)(rect.Y * _extent.Height);
        int w = (int)(rect.Z * _extent.Width), h = (int)(rect.W * _extent.Height);
        cmd.SetViewport(0, new[] { new VkViewport { X = x, Y = y, Width = w, Height = h, MinDepth = 0, MaxDepth = 1 } });
        cmd.SetScissor(0, new[] { new VkRect2D { Offset = new VkOffset2D { X = x, Y = y }, Extent = new VkExtent2D { Width = w, Height = h } } });
        _scenePassOpen = true;
    }

    // Ends the scene pass, records the optional UI pass, submits with fences, and presents.
    public void EndFrame(Action<CommandBuffer>? drawUi)
    {
        BeforeEndFrame?.Invoke();
        if (!_scenePassOpen)
            BeginScenePass();
        var cmd = _cmds[_frame];
        cmd.EndRendering();

        var begin = new VkRenderPassBeginInfo
        {
            RenderPass = _uiPass,
            Framebuffer = _framebuffers[_imageIndex],
            RenderArea = new VkRect2D { Extent = _extent },
        };
        cmd.BeginRenderPass(in begin, (VkSubpassContents)0);
        drawUi?.Invoke(cmd);
        cmd.EndRenderPass();
        cmd.End();

        var waitSems = new[] { _imageAvailable[_frame] };
        var waitStages = new[] { (VkPipelineStageFlags)0x400 };
        var cmds = new[] { cmd };
        var signalSems = new[] { _renderFinished[_imageIndex] };
        QueueExtensions.Submit(_queue, waitSems, waitStages, cmds, signalSems, _inFlight[_frame]);

        var swaps = new[] { _swapchain };
        var indices = new[] { _imageIndex };
        fixed (VkSemaphore* ws = signalSems)
        fixed (VkSwapchainKHR* sw = swaps)
        fixed (int* ix = indices)
        {
            var present = new VkPresentInfoKHR
            {
                WaitSemaphoreCount = 1,
                WaitSemaphores = ws,
                SwapchainCount = 1,
                Swapchains = sw,
                ImageIndices = ix,
            };
            _presentFn(_queue.Handle, &present);
        }

        _frame = (_frame + 1) % FramesInFlight;
    }

    public void WaitIdle() => _device.WaitIdle();

    private CommandBuffer _uploadCommands;
    private VkFence _uploadFence;
    private bool _uploadReady;

    // Records commands into a separate command buffer, submits it, and waits for the GPU to finish. For one-off
    // work like uploading a texture. Safe to call at any point in a frame, since it never touches the frame's own
    // command buffer, but it stalls the GPU until the work is done, so use it sparingly.
    public void ExecuteImmediate(Action<CommandBuffer> record)
    {
        if (!_uploadReady)
        {
            _uploadCommands = _device.AllocateCommandBuffer(new VkCommandBufferAllocateInfo
            {
                CommandPool = _pool,
                Level = (VkCommandBufferLevel)0,
            });
            var fenceInfo = new VkFenceCreateInfo();
            _uploadFence = _device.CreateFence(in fenceInfo, null);
            _uploadReady = true;
        }

        var cmd = _uploadCommands;
        cmd.Reset();
        cmd.Begin();
        record(cmd);
        cmd.End();

        QueueExtensions.Submit(_queue, Array.Empty<VkSemaphore>(), Array.Empty<VkPipelineStageFlags>(), new[] { cmd },
            Array.Empty<VkSemaphore>(), _uploadFence);
        _device.WaitForFences(new[] { _uploadFence }, false, -1);
        _device.ResetFences(new[] { _uploadFence });
    }

    public void Dispose()
    {
        Gpu.Detach(this);
        _device.WaitIdle();

        if (_uploadReady)
            _device.DestroyFence(_uploadFence, null);
        for (int i = 0; i < FramesInFlight; i++)
        {
            _device.DestroyFence(_inFlight[i], null);
            _device.DestroySemaphore(_imageAvailable[i], null);
        }
        foreach (var semaphore in _renderFinished)
            _device.DestroySemaphore(semaphore, null);
        _device.DestroyCommandPool(_pool, null);

        foreach (var framebuffer in _framebuffers)
            _device.DestroyFramebuffer(framebuffer, null);
        _device.DestroyRenderPass(_uiPass, null);

        DestroySceneTargets();

        foreach (var view in _views)
            _device.DestroyImageView(view, null);
        _device.DestroySwapchainKHR(_swapchain, null);
        _instance.DestroySurfaceKHR(_surface, null);

        _device.Dispose();
        _instance.Dispose();
    }
}
