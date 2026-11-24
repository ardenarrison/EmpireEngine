using Engine.Core.Assets;
using Engine.Core.Scene;

namespace Engine.Brutal.Rendering;

// Draws every SpriteRenderer in a scene. Each picture (at each size and flip) is made into a flat mesh and uploaded once; a
// picture that can't be read is reported once and skipped.
public sealed class SpriteDrawer(Func<string, ImageData> loadImage)
{
    private readonly HashSet<string> _uploaded = new();
    private readonly HashSet<string> _failed = new();
    private readonly Dictionary<string, GlbModel> _models = new();
    private readonly HashSet<string> _reload = new();   // uploaded meshes whose picture changed: rebuilt the next time a sprite uses them

    // Call when a picture's file was written again, so sprites showing it show the new picture. The image cache must forget it too.
    public void Invalidate(string spritePath)
    {
        string prefix = "sprite:" + spritePath.Replace('\\', '/') + "|";
        bool Stale(string key) => key.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        foreach (var key in _uploaded.Where(Stale).ToList())
            _reload.Add(key);
        foreach (var key in _models.Keys.Where(Stale).ToList())
            _models.Remove(key);
        _failed.RemoveWhere(Stale);
    }

    // Makes sure every sprite's mesh is on the GPU. Call before BeginFrame, because uploading waits for the GPU.
    public void Sync(ISceneRenderer renderer, Scene scene)
    {
        foreach (var obj in scene.All)
            foreach (var sprite in obj.Components.OfType<SpriteRenderer>())
            {
                string key = Key(sprite);
                if (_reload.Remove(key))
                {
                    if (Model(sprite) is { } fresh) renderer.ReloadModel(key, fresh);
                    else _uploaded.Remove(key);   // the new picture can't be read: draw nothing rather than the old one
                }
                else if (Model(sprite) is { } model && _uploaded.Add(key))
                    renderer.UploadModel(key, model);
            }
    }

    // Queues the sprites for this frame. Call between BeginFrame and EndFrame, after Sync.
    public void Draw(ISceneRenderer renderer, Scene scene)
    {
        foreach (var obj in scene.All)
            foreach (var sprite in obj.Components.OfType<SpriteRenderer>())
                if (_uploaded.Contains(Key(sprite)))
                    renderer.DrawModel(Key(sprite), obj.WorldMatrix, sprite.Tint, null);
    }

    // The sprite's mesh, or null when it has no picture or the picture can't be read.
    public GlbModel? Model(SpriteRenderer sprite)
    {
        if (sprite.Sprite.Length == 0) return null;
        string key = Key(sprite);
        if (_models.TryGetValue(key, out var model)) return model;
        if (_failed.Contains(key)) return null;
        try
        {
            model = SpriteMesh.Build(loadImage(sprite.Sprite), SpriteRenderer.ClampPixelsPerUnit(sprite.PixelsPerUnit), sprite.FlipX, sprite.FlipY);
            _models[key] = model;
            return model;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _failed.Add(key);
            Console.WriteLine($"Could not show the sprite {sprite.Sprite}: {ex.Message}");
            return null;
        }
    }

    private static string Key(SpriteRenderer sprite) =>
        FormattableString.Invariant($"sprite:{sprite.Sprite}|{SpriteRenderer.ClampPixelsPerUnit(sprite.PixelsPerUnit)}|{sprite.FlipX}|{sprite.FlipY}");
}
