namespace Engine.Core.Assets;

// Picks the loader for a model file by its extension.
public static class ModelLoader
{
    public static readonly IReadOnlyList<string> Extensions = [".glb", ".gltf", ".fbx", ".obj"];

    public static bool IsModel(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    // The names of the animations in a model file, without loading the model. Only glTF files (.glb, .gltf) carry
    // animations here; other formats give none.
    public static IReadOnlyList<string> AnimationNames(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".glb" or ".gltf" ? GlbLoader.ReadAnimationNames(path) : Array.Empty<string>();

    public static GlbModel Load(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".fbx" => FbxLoader.Load(path),
        ".obj" => ObjLoader.Load(path),
        _ => GlbLoader.Load(path),
    };
}
