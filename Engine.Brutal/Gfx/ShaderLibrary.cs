using System.Text;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;

namespace Engine.Brutal.Gfx;

// Compiles GLSL to SPIR-V with ShaderC and caches the resulting shader modules by name.
public sealed class ShaderLibrary : IDisposable
{
    private readonly Device _device;
    private readonly Compiler _compiler;
    private readonly CompileOptions _options;
    private readonly Dictionary<string, VkShaderModule> _modules = new();

    public ShaderLibrary(Device device)
    {
        _device = device;
        _compiler = ShaderC.CreateCompiler();
        _options = ShaderC.CreateCompilerOptions();
    }

    public VkShaderModule Get(string name, string glsl, ShaderKind kind)
    {
        if (_modules.TryGetValue(name, out var module))
            return module;

        byte[] spirv = CompileSpirv(glsl, kind);
        module = VkUtil.CreateShaderModule(_device, spirv);
        _modules[name] = module;
        return module;
    }

    private unsafe byte[] CompileSpirv(string glsl, ShaderKind kind)
    {
        // Compile throws if shaderc reports an error, so reaching the return means success.
        using var result = _compiler.Compile(Encoding.UTF8.GetBytes(glsl), kind, "shader"u8, "main"u8, _options);
        int length = (int)result.GetLength().Value;
        return new ReadOnlySpan<byte>(result.GetBytes(), length).ToArray();
    }

    public void Dispose()
    {
        foreach (var module in _modules.Values)
            _device.DestroyShaderModule(module, null);
        _modules.Clear();
    }
}
