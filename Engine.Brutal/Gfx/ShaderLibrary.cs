using System.Runtime.CompilerServices;
using Brutal.VulkanApi;

namespace Engine.Brutal.Gfx;

public enum ShaderStage { Vertex, Fragment }

// A shader as GLSL. Every one the engine uses is listed in ShaderPack.All, which is how it gets into a built game.
public sealed record ShaderSource(string Name, string Glsl, ShaderStage Stage);

// Makes shader modules and caches them by name. The editor compiles them from GLSL. A game has no compiler, which is a large library:
// it reads them ready-made from the file Build Project wrote, given here as compiledShaders. With Metal there is no Vulkan device: the
// Metal renderer takes the SPIR-V from Spirv and translates it (see MetalContext.Function).
public sealed class ShaderLibrary : IDisposable
{
    private readonly Device? _device;
    private readonly Dictionary<string, byte[]>? _compiled;
    private readonly Dictionary<string, VkShaderModule> _modules = new();
    private ShaderCompiler? _compiler;

    public ShaderLibrary(Device? device, string? compiledShaders = null)
    {
        _device = device;
        if (compiledShaders is not null) _compiled = ShaderPack.Read(compiledShaders);
    }

    public VkShaderModule Get(ShaderSource source)
    {
        if (_modules.TryGetValue(source.Name, out var module))
            return module;

        var device = _device ?? throw new InvalidOperationException("This shader library has no Vulkan device.");
        module = VkUtil.CreateShaderModule(device, Spirv(source));
        _modules[source.Name] = module;
        return module;
    }

    // The shader as SPIR-V: compiled in the editor, read from the game's shaders file in a game.
    public byte[] Spirv(ShaderSource source) =>
        _compiled is null ? Compile(source)
            : _compiled.GetValueOrDefault(source.Name) ?? throw new InvalidOperationException($"This game has no compiled {source.Name} shader. Build it again.");

    // On its own because a game doesn't have what ShaderCompiler uses: it must only be loaded when this runs, not with Get.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private byte[] Compile(ShaderSource source)
    {
        if (!ShaderPack.All.Contains(source))
            throw new InvalidOperationException($"The {source.Name} shader isn't in ShaderPack.All, so built games wouldn't have it.");
        return (_compiler ??= new ShaderCompiler()).Compile(source);
    }

    public void Dispose()
    {
        foreach (var module in _modules.Values)
            _device!.DestroyShaderModule(module, null);
        _modules.Clear();
    }
}
