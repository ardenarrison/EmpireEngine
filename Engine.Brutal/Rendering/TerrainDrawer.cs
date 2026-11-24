using System.Numerics;
using Engine.Core.Assets;
using Engine.Core.Scene;

namespace Engine.Brutal.Rendering;

// Draws every Terrain in a scene. A terrain's mesh is uploaded to the renderer once, and again only when its resolution
// or textures change. While the ground or its colors are changing (sculpting, painting) the vertices are sent with each frame's
// draw; once a terrain has stood still for a moment it is uploaded again as an ordinary mesh and drawn without that, because
// rebuilding the vertices of a big terrain every frame costs far more than drawing it. Textures are read with loadImage (by
// project path); one that can't be read is reported once and the terrain is drawn without it.
public sealed class TerrainDrawer(Func<string, ImageData?>? loadImage = null)
{
    private const int FramesToSettle = 20;   // frames without a change before the vertices stop being sent every frame

    private sealed class State
    {
        public GlbModel Model = null!;
        public (int Ground, int Colors) Seen;     // the versions at the last look
        public (int Ground, int Colors) OnGpu;    // the versions the uploaded mesh holds
        public int StillFrames;
        public bool Settled => StillFrames >= FramesToSettle && OnGpu == Seen;
    }

    private readonly Dictionary<Guid, State> _states = new();
    private readonly HashSet<string> _failed = new();

    // Makes sure each terrain's mesh is on the GPU. Call before BeginFrame, because replacing a mesh waits for the GPU.
    public void Sync(ISceneRenderer renderer, Scene scene)
    {
        foreach (var obj in scene.All)
            foreach (var terrain in obj.Components.OfType<Terrain>())
            {
                var (model, _) = terrain.Prepare(Image);
                var versions = (terrain.Version, terrain.ColorVersion);
                if (!_states.TryGetValue(obj.Id, out var state))
                {
                    renderer.UploadModel(Key(obj), model);
                    _states[obj.Id] = new State { Model = model, Seen = versions, OnGpu = versions, StillFrames = FramesToSettle };   // what was just uploaded is current
                    continue;
                }
                if (!ReferenceEquals(state.Model, model))
                {
                    renderer.ReloadModel(Key(obj), model);   // resolution or textures changed
                    state.Model = model;
                    state.OnGpu = versions;
                    state.StillFrames = FramesToSettle;
                }
                if (versions != state.Seen)
                {
                    state.Seen = versions;
                    state.StillFrames = 0;
                }
                else if (state.StillFrames < FramesToSettle)
                    state.StillFrames++;
                else if (state.OnGpu != versions)
                {
                    renderer.ReloadModel(Key(obj), model);   // the vertices have stopped changing: keep them on the GPU
                    state.OnGpu = versions;
                }
            }
    }

    // Queues the terrains for this frame. Call between BeginFrame and EndFrame, after Sync.
    public void Draw(ISceneRenderer renderer, Scene scene)
    {
        foreach (var obj in scene.All)
            foreach (var terrain in obj.Components.OfType<Terrain>())
            {
                var (model, pose) = terrain.Prepare(Image);
                if (_states.TryGetValue(obj.Id, out var state) && ReferenceEquals(state.Model, model))
                    renderer.DrawModel(Key(obj), obj.WorldMatrix, Vector4.One, state.Settled ? null : pose);
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
