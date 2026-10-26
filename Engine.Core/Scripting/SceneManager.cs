using Engine.Core.Scene;
using Engine.Core.Audio;
using Engine.Core.Serialization;

namespace Engine.Core.Scripting;

public enum LoadSceneMode
{
    Single,     // the new scene replaces everything except objects marked DontDestroyOnLoad
    Additive,   // the new scene's objects are added to what is already loaded
}

// A scene loading in the background. Check IsDone, or Progress for a loading bar.
public sealed class SceneLoadOperation
{
    public string Name { get; internal init; } = "";
    public LoadSceneMode Mode { get; internal init; }
    public bool IsDone { get; internal set; }
    public string? Error { get; internal set; }

    // 0 while the file is being read, 0.9 once it is ready to be added, 1 when it is in the scene.
    public float Progress => IsDone ? 1f : Ready ? 0.9f : 0f;
    internal bool Ready;
}

// Loads, adds and removes scenes while the game plays, as in Unity's SceneManager. A scene is a .empirescene file in the
// project's Scenes folder, named without the extension: SceneManager.LoadScene("Level2").
// All loaded scenes share one world, so they see each other and collide with each other; each object remembers which scene it
// came from (SceneObject.SceneName), which is how a scene is unloaded again. Loading takes effect at the start of the next
// frame, so it is safe to call from anywhere in a script. LoadSceneAsync reads the file on another thread, which is what
// lets a big level stream in without a stall.
//
// A script that only needs the next scene:        SceneManager.LoadScene("Level2");
// A level streamed in as the player nears it:    SceneManager.LoadSceneAsync("Hall", LoadSceneMode.Additive);
public static class SceneManager
{
    private sealed class Work
    {
        public required Func<bool> Ready { get; init; }
        public required Action<Scene.Scene> Apply { get; init; }
    }

    private static readonly object Gate = new();
    private static readonly List<Work> Queue = new();
    private static readonly List<string> Loaded = new();

    // The scene that is playing. Set by the engine every frame.
    public static Scene.Scene? World { get; internal set; }

    // The project folder scene names are looked up in. The editor and the player set it.
    public static string ContentRoot { get; set; } = "";

    // Called with a scene that was just read in the background, so the host can load its models and sounds before it is
    // added. It runs on the loading thread.
    public static Action<Scene.Scene>? AssetWarmer { get; set; }

    // The name of the scene that was loaded last with LoadSceneMode.Single (or the one the game started with).
    public static string ActiveScene { get; private set; } = "";

    // Every scene in the world, the active one first.
    public static IReadOnlyList<string> LoadedScenes
    {
        get { lock (Gate) return Loaded.ToArray(); }
    }

    public static int SceneCount
    {
        get { lock (Gate) return Loaded.Count; }
    }

    public static bool IsLoaded(string name)
    {
        lock (Gate) return Loaded.Contains(Stem(name), StringComparer.OrdinalIgnoreCase);
    }

    // The name of the scene an object came from.
    public static string SceneOf(SceneObject obj) => obj.SceneName.Length > 0 ? obj.SceneName : ActiveScene;

    // The first loaded scene file name for a scene path or name: "Scenes/Level2.empirescene" and "Level2" are both "Level2".
    private static string Stem(string name) => Path.GetFileNameWithoutExtension(name.Replace('\\', '/'));

    // Starts the game from a scene that is already loaded. The hosts call it once when play starts.
    public static void Begin(Scene.Scene scene, string name, string contentRoot)
    {
        lock (Gate)
        {
            Queue.Clear();
            Loaded.Clear();
            Loaded.Add(Stem(name));
            ActiveScene = Stem(name);
        }
        World = scene;
        ContentRoot = contentRoot;
        Destruction.Clear();
        foreach (var obj in scene.All)
            if (obj.SceneName.Length == 0)
                obj.SceneName = ActiveScene;
        SaveGame.ClearData();
    }

    // ---- loading -------------------------------------------------------------------------

    // Loads a scene at the start of the next frame. Single replaces the current scene; Additive adds to it.
    public static void LoadScene(string name, LoadSceneMode mode = LoadSceneMode.Single)
    {
        string stem = Stem(name);
        string path = FindFile(name);
        Scene.Scene? parsed = null;
        Enqueue(() => true, world => Apply(world, stem, mode, parsed ??= SceneSerializer.Load(path)));
    }

    // Reads a scene on another thread, then adds it at the start of a frame after it is ready.
    public static SceneLoadOperation LoadSceneAsync(string name, LoadSceneMode mode = LoadSceneMode.Single)
    {
        string stem = Stem(name);
        string path = FindFile(name);
        var operation = new SceneLoadOperation { Name = stem, Mode = mode };
        var task = Task.Run(() =>
        {
            var scene = SceneSerializer.Load(path);
            try { AssetWarmer?.Invoke(scene); }
            catch (Exception ex) { Console.WriteLine($"Could not preload assets for scene '{stem}': {ex.Message}"); }
            operation.Ready = true;
            return scene;
        });
        Enqueue(() => task.IsCompleted, world =>
        {
            if (task.IsFaulted)
            {
                operation.Error = task.Exception?.GetBaseException().Message ?? "unknown error";
                operation.IsDone = true;
                Console.WriteLine($"Could not load scene '{stem}': {operation.Error}");
                return;
            }
            Apply(world, stem, mode, task.Result);
            operation.IsDone = true;
        });
        return operation;
    }

    // Removes every object that came from the scene, at the start of the next frame. The active scene can be unloaded too.
    public static void UnloadScene(string name)
    {
        string stem = Stem(name);
        if (!IsLoaded(stem))
            throw new InvalidOperationException($"Scene '{stem}' is not loaded, so it cannot be unloaded.");
        Enqueue(() => true, world =>
        {
            world.RemoveWhere(o => !o.Persistent && string.Equals(SceneOf(o), stem, StringComparison.OrdinalIgnoreCase));
            lock (Gate) Loaded.RemoveAll(n => string.Equals(n, stem, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(ActiveScene, stem, StringComparison.OrdinalIgnoreCase))
                lock (Gate) ActiveScene = Loaded.FirstOrDefault() ?? "";
        });
    }

    // Keeps an object (and what is under it) when a new scene replaces the current one, for things like music or a player.
    // An object under another is first moved to the top level, keeping its place in the world.
    public static void DontDestroyOnLoad(SceneObject obj)
    {
        if (obj.Parent is not null && World is { } world)
            world.SetParent(obj, null);
        foreach (var o in Scene.Scene.Subtree(obj))
            o.Persistent = true;
    }

    private static string FindFile(string name)
    {
        if (ContentRoot.Length == 0)
            throw new InvalidOperationException("No project folder is set, so scenes cannot be loaded.");
        // "Level2" and "Wing/Hall" are looked for in the Scenes folder; a path from the project folder, like
        // "Levels/Level2.empirescene", works too.
        string relative = name.Replace('\\', '/');
        if (!relative.EndsWith(".empirescene", StringComparison.OrdinalIgnoreCase))
            relative += ".empirescene";
        string inScenes = Path.Combine(ContentRoot, "Scenes", relative);
        if (File.Exists(inScenes)) return inScenes;
        string fromRoot = Path.Combine(ContentRoot, relative);
        if (File.Exists(fromRoot)) return fromRoot;
        throw new FileNotFoundException($"Scene '{name}' was not found. Looked for Scenes/{relative} in the project folder.", inScenes);
    }

    internal static void Enqueue(Func<bool> ready, Action<Scene.Scene> apply)
    {
        lock (Gate) Queue.Add(new Work { Ready = ready, Apply = apply });
    }

    // Runs at the start of each frame, before scripts: sets the world and applies loads that are ready, in the order asked.
    internal static void Process(Scene.Scene world)
    {
        World = world;
        while (true)
        {
            Work work;
            lock (Gate)
            {
                if (Queue.Count == 0 || !Queue[0].Ready()) return;
                work = Queue[0];
                Queue.RemoveAt(0);
            }
            try
            {
                work.Apply(world);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or System.Text.Json.JsonException)
            {
                Console.WriteLine($"Scene load failed: {ex.Message}");
            }
        }
    }

    private static void Apply(Scene.Scene world, string stem, LoadSceneMode mode, Scene.Scene loaded)
    {
        if (mode == LoadSceneMode.Additive && IsLoaded(stem))
        {
            Console.WriteLine($"Scene '{stem}' is already loaded, so it was not added again.");
            return;
        }

        if (mode == LoadSceneMode.Single)
        {
            world.RemoveWhere(o => !o.Persistent);
            lock (Gate)
            {
                Loaded.Clear();
                ActiveScene = stem;
            }
            world.Name = stem;
            world.Physics.Reset();
            // The new scene's own settings and gravity take over.
            world.Settings.CopyFrom(loaded.Settings);
            world.Physics.Gravity = loaded.Physics.Gravity;
        }
        lock (Gate) Loaded.Add(stem);

        var added = new List<SceneObject>();
        foreach (var root in loaded.Roots.ToList())
        {
            foreach (var obj in Scene.Scene.Subtree(root))
                obj.SceneName = stem;
            world.Add(root);
            added.AddRange(Scene.Scene.Subtree(root));
        }
        string problems = ScriptRuntime.Attach(added);
        if (problems.Length > 0)
            Console.WriteLine($"Script errors in scene '{stem}':\n{problems}");
    }

    // Puts a loaded save game's scene in place of the world. SaveGame.Load calls it.
    internal static void ApplySave(Scene.Scene world, Scene.Scene saved, string active, IEnumerable<string> loaded)
    {
        world.Clear();
        world.Physics.Reset();
        world.Settings.CopyFrom(saved.Settings);
        world.Physics.Gravity = saved.Physics.Gravity;
        lock (Gate)
        {
            Loaded.Clear();
            Loaded.AddRange(loaded);
            ActiveScene = active;
        }
        world.Name = active;

        var restored = new List<SceneObject>();
        foreach (var root in saved.Roots.ToList())
        {
            world.Add(root);
            restored.AddRange(Scene.Scene.Subtree(root));
        }
        AudioEngine.StopAll();
        string problems = ScriptRuntime.Attach(restored);
        if (problems.Length > 0)
            Console.WriteLine($"Script errors while loading the saved game:\n{problems}");
        ScriptRuntime.NotifyLoaded(restored);
    }
}
