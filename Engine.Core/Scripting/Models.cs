using Engine.Core.Assets;
using Engine.Core.Scene;
using Engine.Core.Serialization;

namespace Engine.Core.Scripting;

// Puts a model or prefab from the project into the scene that is playing:
//     var rock = Models.Load("Models/Rock.glb");
//     var enemy = Models.Load("Prefabs/Enemy.pre");
//     enemy.Position = new Vector3(0, 2, 0);
// The path is from the project folder, in the editor and in a built game alike. The object is in the scene as soon as this
// returns. Its scripts' Start runs on the next frame, and it stays until it is destroyed or the scene is replaced.
public static class Models
{
    // Makes one new object for a model (.glb, .gltf, .fbx, .obj), or a new copy of a prefab (.pre) as it was saved, and returns
    // it. A problem with the path or the file is an error, which stops the script that asked and says what was wrong.
    public static SceneObject Load(string path)
    {
        var world = SceneManager.World ?? throw new InvalidOperationException("Models.Load only works while a scene is playing.");
        string relative = (path ?? "").Replace('\\', '/');
        string file = FileInProject(relative);
        bool prefab = SceneSerializer.IsPrefab(file);
        if (!prefab && !ModelLoader.IsModel(file))
            throw new ArgumentException($"'{path}' isn't a model or a prefab. Models.Load takes .glb, .gltf, .fbx, .obj and .pre files.");
        if (!File.Exists(file))
            throw new FileNotFoundException($"There is no '{relative}' in the project.");

        if (prefab)
        {
            var root = SceneSerializer.AddPrefab(world, file);
            string problems = ScriptRuntime.Attach(Scene.Scene.Subtree(root).ToList());
            if (problems.Length > 0)
                Console.WriteLine($"Script errors in prefab '{relative}':\n{problems}");
            return root;
        }

        // Read now, so a file that can't be read stops this script instead of the game when it comes to be drawn.
        Animator.ModelLoader?.Invoke(relative);
        var obj = new SceneObject { Name = Path.GetFileNameWithoutExtension(relative) };
        obj.Components.Add(new MeshRenderer { Model = relative });
        world.Add(obj);
        return obj;
    }

    // The full path of a file given from the project folder. A built game only has what is in the project.
    private static string FileInProject(string relative)
    {
        string root = Path.GetFullPath(SceneManager.ContentRoot);
        string file = Path.GetFullPath(Path.Combine(root, relative));
        if (!file.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{relative}' isn't in the project, so a built game wouldn't have it. Give a path from the project folder, like Models/Rock.glb.");
        return file;
    }
}
