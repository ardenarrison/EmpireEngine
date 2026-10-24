using System.Numerics;
using Engine.Core.Assets;
using Engine.Core.Scene;

namespace Engine.Brutal.Rendering;

// Draws every Terrain in a scene. A terrain's mesh is uploaded to the renderer once, and again only when its resolution
// or colors change; when the ground changes only the vertices are sent, with the frame's draw.
public sealed class TerrainDrawer
{
    private readonly Dictionary<Guid, GlbModel> _uploaded = new();

    // Makes sure each terrain's mesh is on the GPU. Call before BeginFrame, because replacing a mesh waits for the GPU.
    public void Sync(BrutalRenderer renderer, Scene scene)
    {
        foreach (var obj in scene.All)
            foreach (var terrain in obj.Components.OfType<Terrain>())
            {
                var (model, _) = terrain.Prepare();
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
                var (model, pose) = terrain.Prepare();
                if (_uploaded.TryGetValue(obj.Id, out var known) && ReferenceEquals(known, model))
                    renderer.DrawModel(Key(obj), obj.WorldMatrix, Vector4.One, pose);
            }
    }

    private static string Key(SceneObject obj) => "terrain:" + obj.Id;
}
