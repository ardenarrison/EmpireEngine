using Engine.Core.Audio;

namespace Engine.Core.Assets;

// Loads each model once. The editor asks for changed files (re-exported from Blender, say) and reloads them.
// Safe to call from several threads, so a scene that loads in the background can warm the cache.
public sealed class AssetCache(string contentRoot)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (GlbModel Model, DateTime WriteTime)> _models = new(StringComparer.OrdinalIgnoreCase);

    public string ContentRoot { get; } = contentRoot;
    private readonly Dictionary<string, ImageData> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AudioClip> _clips = new(StringComparer.OrdinalIgnoreCase);

    // A picture in the project (a sky panorama, say), decoded once.
    public ImageData GetImage(string relativePath)
    {
        lock (_gate)
        {
            if (!_images.TryGetValue(relativePath, out var image))
                _images[relativePath] = image = ImageData.Load(Path.Combine(ContentRoot, relativePath));
            return image;
        }
    }

    // A sound in the project (.mp3 or .wav), decoded once.
    public AudioClip GetAudio(string relativePath)
    {
        lock (_gate)
        {
            if (!_clips.TryGetValue(relativePath, out var clip))
                _clips[relativePath] = clip = AudioClip.Load(Path.Combine(ContentRoot, relativePath));
            return clip;
        }
    }

    public GlbModel GetModel(string relativePath)
    {
        lock (_gate)
        {
            if (!_models.TryGetValue(relativePath, out var entry))
            {
                entry = Load(relativePath);
                _models[relativePath] = entry;
            }
            return entry.Model;
        }
    }

    // Reloads every cached model whose file changed since it was loaded, and returns their paths.
    // A file that fails to load (for example while it is still being written) keeps its old version and is retried.
    public List<string> ReloadChanged()
    {
        lock (_gate)
        {
            var changed = new List<string>();
            foreach (var (path, entry) in _models.ToList())
            {
                var full = Path.Combine(ContentRoot, path);
                if (!File.Exists(full) || File.GetLastWriteTimeUtc(full) == entry.WriteTime)
                    continue;
                try
                {
                    _models[path] = Load(path);
                    changed.Add(path);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
                {
                    Console.WriteLine($"Could not reload {path} yet: {ex.Message}");
                }
            }
            return changed;
        }
    }

    private (GlbModel, DateTime) Load(string relativePath)
    {
        var full = Path.Combine(ContentRoot, relativePath);
        var writeTime = File.GetLastWriteTimeUtc(full);
        return (ModelLoader.Load(full), writeTime);
    }

    // Loads everything a scene's objects use that is expensive to read: models, sounds and sky images. Failures are
    // skipped here; they surface (and are reported) when the object is first used.
    public void Warm(Scene.Scene scene)
    {
        foreach (var obj in scene.All)
            foreach (var component in obj.Components)
            {
                try
                {
                    switch (component)
                    {
                        case Scene.MeshRenderer { Model.Length: > 0 } mesh: GetModel(mesh.Model); break;
                        case Scene.Animator animator:
                            foreach (var entry in animator.Animations.Where(a => a.Model.Length > 0))
                                GetModel(entry.Model);
                            break;
                        case Scene.AudioSource { Clip.Length: > 0 } source: AudioEngine.LoadClip(source.Clip); break;
                        case Scene.SceneEnvironment { Panorama.Length: > 0 } env: GetImage(env.Panorama); break;
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or System.Text.Json.JsonException)
                {
                    // reported when the object is used
                }
            }
    }
}
