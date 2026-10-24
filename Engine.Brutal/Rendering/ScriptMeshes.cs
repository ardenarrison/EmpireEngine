using System.Runtime.CompilerServices;
using Engine.Core.Assets;

namespace Engine.Brutal.Rendering;

// Meshes made by scripts at runtime (MeshRenderer.Mesh), such as a water surface. Each is uploaded the first time it is
// drawn, under a key of its own, and freed again once the script has let go of it and it has been garbage collected.
public sealed class ScriptMeshes
{
    private readonly ConditionalWeakTable<GlbModel, string> _keys = new();
    private readonly Dictionary<string, WeakReference<GlbModel>> _uploaded = new();
    private int _next;
    private int _framesUntilSweep;

    // The renderer key for a mesh, uploading it if this is the first time it is drawn.
    public string KeyFor(BrutalRenderer renderer, GlbModel mesh)
    {
        if (_keys.TryGetValue(mesh, out var key)) return key;
        key = $"script-mesh:{++_next}";
        _keys.Add(mesh, key);
        _uploaded[key] = new WeakReference<GlbModel>(mesh);
        renderer.UploadModel(key, mesh);
        return key;
    }

    // Frees meshes no script holds any more. Call before BeginFrame: freeing waits for the GPU. Checks about once a second.
    public void Sweep(BrutalRenderer renderer)
    {
        if (--_framesUntilSweep > 0) return;
        _framesUntilSweep = 60;
        var gone = _uploaded.Where(e => !e.Value.TryGetTarget(out _)).Select(e => e.Key).ToList();
        if (gone.Count == 0) return;
        renderer.UnloadModels(gone);
        foreach (var key in gone) _uploaded.Remove(key);
    }
}
