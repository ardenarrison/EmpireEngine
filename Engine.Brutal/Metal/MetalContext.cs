using System.Runtime.InteropServices;
using Brutal.Numerics;
using Engine.Brutal.Gfx;
using Engine.Core.Rendering;

namespace Engine.Brutal.Metal;

// The Metal counterpart of VulkanContext: the GPU, a CAMetalLayer on the window, the depth buffer, the MSAA and post-process images,
// and the frame being drawn. A frame is one command buffer: the renderer's shadow pass, then the scene pass (cleared to the
// backdrop, drawn inside the 16:9 rectangle), then the post-process passes when they are on, then the UI pass over the window.
// Up to FramesInFlight frames are on the GPU at once; BeginFrame waits for the oldest, so per-frame buffers indexed by FrameIndex are
// free to rewrite once it returns.
public sealed unsafe class MetalContext : IGraphicsContext
{
    public const int FramesInFlight = 2;
    internal const nuint ColorFormat = MetalGpu.FormatBgra8;
    internal static readonly MTLClearColor Backdrop = new(0.05, 0.08, 0.15, 1);

    [DllImport("/System/Library/Frameworks/Metal.framework/Metal")] private static extern nint MTLCreateSystemDefaultDevice();
    [DllImport("glfw", EntryPoint = "glfwGetCocoaWindow")] private static extern nint GetCocoaWindow(nint window);

    private readonly IAppWindow? _host;   // null when drawing off screen (the tests)
    private readonly nint _nsWindow;
    private readonly nint _layer;
    private readonly Dictionary<string, (nint Library, nint Function)> _functions = new();
    private readonly Dictionary<(bool Test, bool Write, bool Reversed), nint> _depthStates = new();

    private int _width, _height;
    private nint _depth, _msaa, _sceneColor, _spare;
    private int _samples = 1;
    private PostAntiAliasing _post = PostAntiAliasing.None;
    private PostEffects _effects = PostEffects.Off;
    private bool _postTarget;

    private readonly nint[] _submitted = new nint[FramesInFlight];   // each slot's last command buffer, kept until it is waited for
    private int _frame;
    private nint _pool, _cmd, _drawable, _target, _sceneEncoder;
    private bool _scenePassOpen;

    public MetalContext(IAppWindow host)
    {
        _host = host;
        var glfw = host.Glfw ?? throw new NotSupportedException("Metal needs a desktop window.");
        NativeLibrary.Load("/System/Library/Frameworks/QuartzCore.framework/QuartzCore");   // for CAMetalLayer
        using var pool = AutoreleasePool.Open();

        Device = MTLCreateSystemDefaultDevice();
        if (Device == 0) throw new InvalidOperationException("This Mac has no GPU that supports Metal.");
        Queue = ObjC.Send(Device, "newCommandQueue");
        DeviceName = ObjC.ToText(ObjC.Send(Device, "name")) ?? "Metal GPU";

        // The window draws through a Metal layer on its content view.
        _nsWindow = GetCocoaWindow(glfw.NativeHandle);
        if (_nsWindow == 0) throw new InvalidOperationException("The window has no Cocoa window for Metal to draw in.");
        nint view = ObjC.Send(_nsWindow, "contentView");
        _layer = ObjC.Retain(ObjC.Send(ObjC.Class("CAMetalLayer"), "layer"));
        ObjC.Call(_layer, "setDevice:", Device);
        ObjC.CallU(_layer, "setPixelFormat:", ColorFormat);
        ObjC.CallBool(_layer, "setFramebufferOnly:", CapturePath is null);
        ObjC.CallBool(_layer, "setDisplaySyncEnabled:", true);   // vsync, like Vulkan's FIFO present mode
        ObjC.Call(view, "setLayer:", _layer);
        ObjC.CallBool(view, "setWantsLayer:", true);

        for (int count = 8; count >= 2; count /= 2)
            if (ObjC.SendBool(Device, "supportsTextureSampleCount:", (nuint)count)) { MaxSampleCount = count; break; }
        if (MaxSampleCount == 0) MaxSampleCount = 1;

        Resize(Math.Max(host.FramebufferSize.X, 1), Math.Max(host.FramebufferSize.Y, 1));
    }

    // Draws into an image of its own instead of a window, which ReadPixels reads back. For the tests: it needs a Mac whose GPU
    // shares memory with the CPU (Apple silicon).
    internal MetalContext(int width, int height)
    {
        using var pool = AutoreleasePool.Open();
        Device = MTLCreateSystemDefaultDevice();
        if (Device == 0) throw new InvalidOperationException("This Mac has no GPU that supports Metal.");
        Queue = ObjC.Send(Device, "newCommandQueue");
        DeviceName = ObjC.ToText(ObjC.Send(Device, "name")) ?? "Metal GPU";
        for (int count = 8; count >= 2; count /= 2)
            if (ObjC.SendBool(Device, "supportsTextureSampleCount:", (nuint)count)) { MaxSampleCount = count; break; }
        if (MaxSampleCount == 0) MaxSampleCount = 1;
        Resize(width, height);
        _spare = MetalGpu.Texture(Device, ColorFormat, width, height, usage: MetalGpu.UsageRenderTarget | MetalGpu.UsageRead,
            storage: MetalGpu.StorageShared);
    }

    // The off-screen image's pixels, blue, green, red, alpha, row after row from the top. Waits for the GPU first.
    internal byte[] ReadPixels()
    {
        WaitIdle();
        return ReadTexture(_spare);
    }

    private int2 FramebufferSize => _host?.FramebufferSize ?? new int2(_width, _height);

    public GraphicsApi Api => GraphicsApi.Metal;
    public nint Device { get; }
    public nint Queue { get; }
    public string DeviceName { get; }
    public int Width => _width;
    public int Height => _height;
    public int MaxSampleCount { get; }
    public int SampleCount => _samples;
    public int FrameIndex => _frame;
    public bool IsMinimized => FramebufferSize.X == 0 || FramebufferSize.Y == 0;
    public float BottomReservedFraction { get; set; }
    public float4 SceneViewport => SceneLayout.Viewport(_width, _height, BottomReservedFraction);
    public (int X, int Y, int Width, int Height) SceneRectPixels => SceneLayout.Pixels(SceneViewport, _width, _height);

    public PostAntiAliasing PostAntiAliasing => _post;
    public PostEffects PostEffects => _effects;
    public bool PostProcessing => _postTarget;
    public nint SceneColorTexture => _sceneColor;

    // The command buffer of the frame being recorded, and the open scene pass's encoder.
    public nint CommandBuffer => _cmd;
    public nint SceneEncoder => _sceneEncoder;

    // The window's image this frame, which the post-process passes draw the finished scene into.
    public nint TargetTexture => _target;

    // Runs at the start of EndFrame, before the scene pass is opened if it hasn't been: the renderer records its shadow pass and
    // the scene draws here, once every draw of the frame is known.
    public Action? BeforeEndFrame { get; set; }

    // Called after the scene pass with the scene in SceneColorTexture, when PostProcessing is on. It must draw the scene into
    // TargetTexture inside the 16:9 rectangle, since nothing else does.
    public Action<nint>? AfterScenePass { get; set; }

    // Raised after the sample count changes, so pipelines made for it can follow.
    public event Action<int>? SampleCountChanged;

    public int SupportedSampleCount(int requested)
    {
        for (int count = 8; count >= 2; count /= 2)
            if (count <= requested && count <= MaxSampleCount) return count;
        return 1;
    }

    public void SetSampleCount(int requested)
    {
        int samples = SupportedSampleCount(requested);
        if (samples == _samples) return;
        WaitIdle();
        _samples = samples;
        CreateSceneTargets();
        SampleCountChanged?.Invoke(samples);
    }

    public void SetPostProcessing(PostAntiAliasing mode, PostEffects effects)
    {
        if (AfterScenePass is null) { mode = PostAntiAliasing.None; effects = PostEffects.Off; }
        _post = mode;
        _effects = effects;
        bool needTarget = mode != PostAntiAliasing.None || effects.Any;
        if (needTarget == _postTarget) return;
        WaitIdle();
        _postTarget = needTarget;
        CreateSceneTargets();
    }

    public void ApplyCamera(Engine.Core.Scene.CameraComponent? camera)
    {
        SetSampleCount(camera?.RequestedSamples ?? 1);
        SetPostProcessing(camera?.AntiAliasing switch
        {
            Engine.Core.Scene.AntiAliasingMode.Fxaa => PostAntiAliasing.Fxaa,
            Engine.Core.Scene.AntiAliasingMode.Smaa => PostAntiAliasing.Smaa,
            _ => PostAntiAliasing.None,
        }, camera is null ? PostEffects.Off : new PostEffects(camera.Exposure, camera.Contrast, camera.Vignette, camera.Sharpen));
    }

    // ---- shaders and states ----------------------------------------------------------------------------------------------

    // The Metal function for a shader, translated from its SPIR-V and compiled the first time it is asked for.
    internal nint Function(ShaderLibrary shaders, ShaderSource source)
    {
        if (_functions.TryGetValue(source.Name, out var cached)) return cached.Function;
        var (msl, entry) = SpirvToMsl.Translate(shaders.Spirv(source), source.Stage, source.Name);
        using var pool = AutoreleasePool.Open();
        nint text = ObjC.String(msl);
        nint error = 0;
        nint library = ObjC.Send(Device, "newLibraryWithSource:options:error:", text, 0, (nint)(&error));
        ObjC.Release(text);
        if (library == 0)
            throw new InvalidOperationException($"Metal could not compile the {source.Name} shader: {ObjC.ErrorText(error)}");
        nint name = ObjC.String(entry);
        nint function = ObjC.Send(library, "newFunctionWithName:", name);
        ObjC.Release(name);
        if (function == 0)
            throw new InvalidOperationException($"The {source.Name} shader has no {entry} function in Metal.");
        _functions[source.Name] = (library, function);
        return function;
    }

    internal nint DepthState(bool test, bool write, bool reversed = true)
    {
        if (!_depthStates.TryGetValue((test, write, reversed), out nint state))
            _depthStates[(test, write, reversed)] = state = MetalGpu.DepthState(Device, test, write, reversed);
        return state;
    }

    // ---- frames ----------------------------------------------------------------------------------------------------------

    void IGraphicsContext.BeginFrame() => BeginFrame();

    public void BeginFrame(bool deferScenePass = false)
    {
        var size = FramebufferSize;
        if (size.X > 0 && size.Y > 0 && (size.X != _width || size.Y != _height))
            Resize(size.X, size.Y);

        _pool = ObjC.PoolPush();
        Wait(_frame);
        _cmd = ObjC.Send(Queue, "commandBuffer");

        // nextDrawable can come back empty (the window is hidden, or the system is short of images). The frame is then drawn into
        // a spare image and not shown.
        _drawable = _layer != 0 ? ObjC.Send(_layer, "nextDrawable") : 0;
        if (_drawable != 0)
        {
            _target = ObjC.Send(_drawable, "texture");
        }
        else
        {
            if (_spare == 0) _spare = MetalGpu.Texture(Device, ColorFormat, _width, _height, usage: MetalGpu.UsageRenderTarget, storage: MetalGpu.StoragePrivate);
            _target = _spare;
        }

        _scenePassOpen = false;
        if (!deferScenePass)
            BeginScenePass();
    }

    // Opens the scene pass: clears color and depth, then limits drawing to the 16:9 scene rectangle.
    public void BeginScenePass()
    {
        nint color = _postTarget ? _sceneColor : _target;
        nint pass = _samples > 1
            ? MetalGpu.PassDescriptor(_msaa, MetalGpu.LoadClear, Backdrop, resolve: color, depth: _depth)
            : MetalGpu.PassDescriptor(color, MetalGpu.LoadClear, Backdrop, depth: _depth);
        _sceneEncoder = MetalGpu.Encoder(_cmd, pass, "Scene");
        var (x, y, w, h) = SceneRectPixels;
        MetalGpu.Viewport(_sceneEncoder, x, y, w, h);
        MetalGpu.Scissor(_sceneEncoder, x, y, w, h);
        _scenePassOpen = true;
    }

    void IGraphicsContext.EndFrame(Action<UiPass>? drawUi) => EndFrame(drawUi);

    // Ends the scene pass, runs the post-process passes and the UI pass, then shows the frame.
    public void EndFrame(Action<UiPass>? drawUi)
    {
        BeforeEndFrame?.Invoke();
        if (!_scenePassOpen)
            BeginScenePass();
        MetalGpu.End(_sceneEncoder);
        _sceneEncoder = 0;
        _scenePassOpen = false;

        if (_postTarget)
            AfterScenePass?.Invoke(_cmd);

        nint ui = MetalGpu.Encoder(_cmd, MetalGpu.PassDescriptor(_target, MetalGpu.LoadLoad, Backdrop), "UI");
        MetalGpu.Viewport(ui, 0, 0, _width, _height);
        drawUi?.Invoke(new UiPass(ui));
        MetalGpu.End(ui);

        if (CapturePath is not null && ++_capturedFrames == CaptureFrame)
            Capture();
        if (_drawable != 0)
            ObjC.Call(_cmd, "presentDrawable:", _drawable);
        _submitted[_frame] = ObjC.Retain(_cmd);
        ObjC.Call(_cmd, "commit");
        _cmd = _drawable = _target = 0;
        ObjC.PoolPop(_pool);
        _pool = 0;
        _frame = (_frame + 1) % FramesInFlight;
    }

    // Set EMPIRE_METAL_CAPTURE to a file path to save the window's picture as it is a few seconds in, as a .ppm image. For checking what
    // Metal draws on a real window without a screenshot of the screen.
    private static readonly string? CapturePath = Environment.GetEnvironmentVariable("EMPIRE_METAL_CAPTURE") is { Length: > 0 } path ? path : null;
    private const int CaptureFrame = 300;
    private int _capturedFrames;

    private void Capture()
    {
        // The window's image lives in memory the GPU manages; synchronizing it makes what was drawn readable by the CPU.
        nint blit = ObjC.Send(_cmd, "blitCommandEncoder");
        ObjC.Call(blit, "synchronizeResource:", _target);
        MetalGpu.End(blit);
        nint cmd = _cmd;
        ObjC.Call(cmd, "commit");
        ObjC.Call(cmd, "waitUntilCompleted");
        var pixels = ReadTexture(_target);
        using var file = File.Create(CapturePath!);
        file.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{_width} {_height}\n255\n"));
        var rgb = new byte[_width * _height * 3];
        for (int i = 0, j = 0; i < pixels.Length; i += 4, j += 3)
        {
            rgb[j] = pixels[i + 2]; rgb[j + 1] = pixels[i + 1]; rgb[j + 2] = pixels[i];
        }
        file.Write(rgb);
        Console.WriteLine($"Saved the frame to {CapturePath}");
        _cmd = ObjC.Send(Queue, "commandBuffer");   // the frame's own buffer went with the capture; present with a fresh one
    }

    // A BGRA texture's pixels, row after row from the top.
    private byte[] ReadTexture(nint texture)
    {
        var pixels = new byte[_width * _height * 4];
        fixed (byte* p = pixels)
            ObjC.Call(texture, "getBytes:bytesPerRow:fromRegion:mipmapLevel:", (nint)p, (nuint)(_width * 4), new MTLRegion(_width, _height), 0);
        return pixels;
    }

    // Waits until a slot's last frame has finished on the GPU.
    private void Wait(int slot)
    {
        if (_submitted[slot] == 0) return;
        ObjC.Call(_submitted[slot], "waitUntilCompleted");
        // A frame the GPU couldn't finish (status 5, error) is said once; the next frames are tried as usual.
        if (ObjC.SendNUInt(_submitted[slot], "status") == 5 && !_reportedGpuError)
        {
            _reportedGpuError = true;
            Console.WriteLine($"Metal could not draw a frame: {ObjC.ErrorText(ObjC.Send(_submitted[slot], "error"))}");
        }
        ObjC.Release(_submitted[slot]);
        _submitted[slot] = 0;
    }

    private bool _reportedGpuError;

    // True once a frame has failed on the GPU (the tests check it).
    internal bool HadGpuError => _reportedGpuError;

    // Waits for every frame sent to the GPU. The frame being recorded, if any, isn't sent yet, so it doesn't count.
    public void WaitIdle()
    {
        for (int slot = 0; slot < FramesInFlight; slot++)
            Wait(slot);
    }

    // ---- targets ---------------------------------------------------------------------------------------------------------

    private void Resize(int width, int height)
    {
        WaitIdle();
        _width = width;
        _height = height;
        if (_layer != 0)
        {
            ObjC.Call(_layer, "setDrawableSize:", new CGSize(width, height));
            ObjC.CallDouble(_layer, "setContentsScale:", ObjC.SendDouble(_nsWindow, "backingScaleFactor"));
            ObjC.Release(_spare);
            _spare = 0;
        }
        CreateSceneTargets();
    }

    // The depth buffer, the MSAA color image when MSAA is on, and the scene image when post-processing is on, at the window's size.
    // A command buffer keeps what it uses alive, so the old ones can be let go at once.
    private void CreateSceneTargets()
    {
        ObjC.Release(_depth);
        ObjC.Release(_msaa);
        ObjC.Release(_sceneColor);
        _msaa = _sceneColor = 0;
        _depth = MetalGpu.Texture(Device, MetalGpu.FormatDepth32, _width, _height, usage: MetalGpu.UsageRenderTarget,
            storage: MetalGpu.StoragePrivate, samples: _samples);
        if (_samples > 1)
            _msaa = MetalGpu.Texture(Device, ColorFormat, _width, _height, usage: MetalGpu.UsageRenderTarget,
                storage: MetalGpu.StoragePrivate, samples: _samples);
        if (_postTarget)
        {
            _sceneColor = MetalGpu.Texture(Device, ColorFormat, _width, _height, usage: MetalGpu.UsageRenderTarget | MetalGpu.UsageRead,
                storage: MetalGpu.StoragePrivate);
            SceneColorGeneration++;
        }
    }

    // Counts how many times the scene image has been made, so what reads it knows to follow.
    public int SceneColorGeneration { get; private set; }

    public void Dispose()
    {
        WaitIdle();
        foreach (var (library, function) in _functions.Values)
        {
            ObjC.Release(function);
            ObjC.Release(library);
        }
        foreach (var state in _depthStates.Values)
            ObjC.Release(state);
        ObjC.Release(_depth);
        ObjC.Release(_msaa);
        ObjC.Release(_sceneColor);
        ObjC.Release(_spare);
        ObjC.Release(_layer);
        ObjC.Release(Queue);
        ObjC.Release(Device);
    }
}
