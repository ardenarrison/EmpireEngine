namespace EmpireHub;

// Makes a new project folder: Scenes/Main.empirescene, empty Scripts and Models (3D) or Sprites (2D) folders, and project.empire.
// A 3D scene has a light, a sky, a camera and a flat terrain for ground; a 2D scene has a plain backdrop and an orthographic
// camera looking straight at the X-Y plane. No art is bundled: the 3D ground is a terrain, which the engine makes.
public static class ProjectTemplate
{
    // Returns null when the name and location are usable, otherwise what is wrong with them.
    public static string? Validate(string name, string location)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a project name.";
        if (name.Trim() != name) return "The name can't start or end with a space.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            return "The name can't contain / \\ : * ? \" < > |";
        if (string.IsNullOrWhiteSpace(location)) return "Choose a location.";

        string folder = Path.Combine(location, name);
        if (File.Exists(folder)) return "A file with that name is already there.";
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            return "A folder with that name already exists and isn't empty.";
        return null;
    }

    public static string Create(string name, string location, string? editorVersion, bool is2D = false)
    {
        string folder = Path.GetFullPath(Path.Combine(location, name));
        Directory.CreateDirectory(Path.Combine(folder, "Scenes"));
        Directory.CreateDirectory(Path.Combine(folder, "Scripts"));
        Directory.CreateDirectory(Path.Combine(folder, is2D ? "Sprites" : "Models"));

        File.WriteAllText(Path.Combine(folder, "Scenes", "Main.empirescene"), is2D ? StarterScene2D() : StarterScene());
        File.WriteAllText(Path.Combine(folder, ".gitignore"), "Generated/\nimgui.ini\n.DS_Store\n");
        new EmpireProject { Name = name, EditorVersion = editorVersion, Type = is2D ? "2D" : "3D" }.Write(folder);
        return folder;
    }

    // A flat backdrop and a camera 10 units in front of the X-Y plane, looking straight at it, showing 10 units from top to bottom.
    private static string StarterScene2D() => $$"""
        {
          "formatVersion": 1,
          "name": "Main",
          "objects": [
            {
              "id": "{{Guid.NewGuid()}}",
              "name": "Environment",
              "position": [0, 0, 0],
              "rotation": [0, 0, 0, 1],
              "scale": [1, 1, 1],
              "components": [
                { "type": "Environment", "skyMode": "Color", "skyColor": [0.22, 0.28, 0.36] }
              ]
            },
            {
              "id": "{{Guid.NewGuid()}}",
              "name": "Main Camera",
              "position": [0, 0, 10],
              "rotation": [0, 0, 0, 1],
              "scale": [1, 1, 1],
              "components": [
                { "type": "Camera", "near": 0.1, "far": 100, "orthographic": true, "orthographicSize": 5 }
              ]
            }
          ]
        }
        """;

    private static string StarterScene() => $$"""
        {
          "formatVersion": 1,
          "name": "Main",
          "objects": [
            {
              "id": "{{Guid.NewGuid()}}",
              "name": "Sun",
              "position": [0, 10, 0],
              "rotation": [0, 0, 0, 1],
              "scale": [1, 1, 1],
              "components": [
                { "type": "DirectionalLight", "intensity": 4, "lightColor": [1, 1, 1], "yaw": 33.69, "pitch": 47.96 }
              ]
            },
            {
              "id": "{{Guid.NewGuid()}}",
              "name": "Environment",
              "position": [0, 0, 0],
              "rotation": [0, 0, 0, 1],
              "scale": [1, 1, 1],
              "components": [
                { "type": "Environment" }
              ]
            },
            {
              "id": "{{Guid.NewGuid()}}",
              "name": "Main Camera",
              "position": [0, 6, 12],
              "rotation": [-0.2164396, 0, 0, 0.976296],
              "scale": [1, 1, 1],
              "components": [
                { "type": "Camera", "focalLength": 35, "near": 0.1, "far": 5000 }
              ]
            },
            {
              "id": "{{Guid.NewGuid()}}",
              "name": "Ground",
              "position": [0, 0, 0],
              "rotation": [0, 0, 0, 1],
              "scale": [1, 1, 1],
              "components": [
                { "type": "Terrain", "terrainData": { "width": 60, "length": 60, "height": 12, "baseColor": [0.30, 0.50, 0.20] } }
              ]
            }
          ]
        }
        """;
}
