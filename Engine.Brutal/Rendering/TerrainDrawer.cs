using System.Numerics;
using Engine.Core.Assets;
using Engine.Core.Scene;

namespace Engine.Brutal.Rendering;

// Draws every Terrain in a scene. A terrain's mesh is uploaded to the renderer once, and again only when its resolution
// or textures change; when the ground or its colors change only the vertices are sent, with the frame's draw. Textures are
// read with loadImage (by project path); one that can't be read is reported once and the terrain is drawn without it.
public sealed class TerrainDrawer(Func<string, ImageData>? loadImage = null)
{
    private readonly Dictionary<Guid, GlbModel> _uploaded = new();
    private readonly HashSet<string> _failed = new();

    // Makes sure each terrain's mesh is on the GPU. Call before BeginFrame, because replacing a mesh waits for the GPU.
    public void Sync(BrutalRenderer renderer, Scene scene)
    {
        foreach (var obj in scene.All)
            foreach (var terrain in obj.Components.OfType<Terrain>())
            {
                var (model, _) = terrain.Prepare(Image);
                if (_uploaded.TryGetValue(obj.Id, out var known) && ReferenceEquals(known, model)) continue;
                if (known is null) renderer.UploadModel(Key(obj), model);
                else renderer.ReloadModel(Key(obj), model);
                _uploaded[obj.Id] = model;
            }
    }

    // Queues the terrains for this frame. Call between BeginFrame and EndFrame, after Sync.
    public void Draw(BrutalRenderer renderer, Scene scene)
    {
        foreach (var obj in scene.All)
            foreach (var terrain in obj.Components.OfType<Terrain>())
            {
                var (model, pose) = terrain.Prepare(Image);
                if (_uploaded.TryGetValue(obj.Id, out var known) && ReferenceEquals(known, model))
                    renderer.DrawModel(Key(obj), obj.WorldMatrix, Vector4.One, pose);
            }
    }

    // True when this picture could not be read, so the Inspector can say so.
    public bool Failed(string path) => _failed.Contains(path);

    private ImageData? Image(string path)
    {
        if (loadImage is null || _failed.Contains(path)) return null;
        try
        {
            return loadImage(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _failed.Add(path);
            Console.WriteLine($"Could not use the terrain texture {path}: {ex.Message}");
            return null;
        }
    }

    private static string Key(SceneObject obj) => "terrain:" + obj.Id;
}
