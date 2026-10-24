namespace EmpireHub;

// Makes a new project folder: Scenes/Main.empirescene with a light, a sky, a camera and a flat terrain for ground,
// empty Models and Scripts folders and project.empire. No art is bundled: the ground is a terrain, which the engine makes.
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

    public static string Create(string name, string location, string? editorVersion)
    {
        string folder = Path.GetFullPath(Path.Combine(location, name));
        Directory.CreateDirectory(Path.Combine(folder, "Scenes"));
        Directory.CreateDirectory(Path.Combine(folder, "Scripts"));
        Directory.CreateDirectory(Path.Combine(folder, "Models"));

        File.WriteAllText(Path.Combine(folder, "Scenes", "Main.empirescene"), StarterScene());
        File.WriteAllText(Path.Combine(folder, ".gitignore"), "Generated/\nimgui.ini\n.DS_Store\n");
        new EmpireProject { Name = name, EditorVersion = editorVersion }.Write(folder);
        return folder;
    }

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
