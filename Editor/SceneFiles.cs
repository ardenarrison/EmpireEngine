using System.Numerics;
using Engine.Core.Scene;
using Engine.Core.Serialization;

namespace Editor;

// Creating new scene files in the project.
public static class SceneFiles
{
    public const string Extension = ".empirescene";

    // The name a user typed, without spaces around it or a .empirescene ending they added themselves.
    public static string CleanName(string typed)
    {
        string name = typed.Trim();
        return name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? name[..^Extension.Length].TrimEnd() : name;
    }

    // What is wrong with a scene name in a folder, or null when it can be created there.
    public static string? Validate(string folder, string typed)
    {
        string name = CleanName(typed);
        if (name.Length == 0) return "Type a name for the scene.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            return "The name can't contain / \\ : * ? \" < > |";
        if (name.StartsWith('.')) return "The name can't start with a dot.";
        if (File.Exists(Path.Combine(folder, name + Extension)))
            return $"A scene called {name} already exists here.";
        return null;
    }

    // Writes a new scene with a sun, a sky and a camera looking at the origin, so it is lit and playable straight away.
    // Returns the file's full path.
    public static string Create(string folder, string typed)
    {
        string name = CleanName(typed);
        var scene = new Scene { Name = name };

        var sun = new SceneObject { Name = "Sun", Position = new Vector3(0, 10, 0) };
        sun.Components.Add(new DirectionalLight { Intensity = 4f });
        scene.Add(sun);

        var environment = new SceneObject { Name = "Environment" };
        environment.Components.Add(new SceneEnvironment());
        scene.Add(environment);

        var camera = new SceneObject
        {
            Name = "Main Camera",
            Position = new Vector3(0, 6, 12),
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -25f * MathF.PI / 180f),
        };
        camera.Components.Add(new CameraComponent { Far = 5000f });
        scene.Add(camera);

        string path = Path.Combine(folder, name + Extension);
        SceneSerializer.Save(scene, path);
        return path;
    }
}
