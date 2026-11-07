using System.Text;
using Brutal.ShaderCApi;

namespace Engine.Brutal.Gfx;

// Compiles GLSL to SPIR-V with ShaderC. Only the editor has ShaderC, so nothing a game runs may touch this.
internal sealed class ShaderCompiler
{
    private readonly Compiler _compiler = ShaderC.CreateCompiler();
    private readonly CompileOptions _options = ShaderC.CreateCompilerOptions();

    public unsafe byte[] Compile(ShaderSource source)
    {
        var kind = source.Stage == ShaderStage.Vertex ? ShaderKind.VertexShader : ShaderKind.FragmentShader;
        // Compile throws if shaderc reports an error, so reaching the return means success.
        using var result = _compiler.Compile(Encoding.UTF8.GetBytes(source.Glsl), kind, "shader"u8, "main"u8, _options);
        int length = (int)result.GetLength().Value;
        return new ReadOnlySpan<byte>(result.GetBytes(), length).ToArray();
    }
}
