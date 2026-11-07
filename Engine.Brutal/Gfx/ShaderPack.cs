using Engine.Brutal.Rendering;
using Engine.Brutal.Ui;

namespace Engine.Brutal.Gfx;

// All the engine's shaders compiled to SPIR-V, in one file in GameData. Build Project writes it, so a game doesn't need the shader
// compiler. SPIR-V is the same on every platform, so a game for another platform gets the same file.
public static class ShaderPack
{
    public const string FileName = "shaders.bin";

    // A new shader has to be added here. The editor stops with a message the first time it compiles one that isn't.
    public static IReadOnlyList<ShaderSource> All =>
    [
        BrutalRenderer.ModelVertexShader, BrutalRenderer.ModelFragmentShader, BrutalRenderer.SkyVertexShader, BrutalRenderer.SkyFragmentShader,
        Canvas2D.VertexShader, Canvas2D.FragmentShader,
        ParticleRenderer.VertexShader, ParticleRenderer.FragmentShader,
        .. PostProcessShaders.All,
    ];

    public static void Write(string file)
    {
        var compiler = new ShaderCompiler();
        using var writer = new BinaryWriter(File.Create(file));
        var shaders = All;
        writer.Write(shaders.Count);
        foreach (var shader in shaders)
        {
            byte[] spirv = compiler.Compile(shader);
            writer.Write(shader.Name);
            writer.Write(spirv.Length);
            writer.Write(spirv);
        }
    }

    public static Dictionary<string, byte[]> Read(string file)
    {
        if (!File.Exists(file))
            throw new InvalidDataException($"{file} is missing, so this game has no shaders. Build it again.");
        using var reader = new BinaryReader(File.OpenRead(file));
        var shaders = new Dictionary<string, byte[]>();
        for (int count = reader.ReadInt32(); count > 0; count--)
        {
            string name = reader.ReadString();
            shaders[name] = reader.ReadBytes(reader.ReadInt32());
        }
        return shaders;
    }
}
