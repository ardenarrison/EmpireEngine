using System.Text.Json;

namespace EmpireHub;

// project.empire, the small file at the root of a project folder that the hub reads. A folder without one still
// opens as long as it has a scene in Scenes/ (projects made before the hub existed).
public sealed class EmpireProject
{
    public const string FileName = "project.empire";

    public string Name { get; set; } = "";
    public string? EditorVersion { get; set; }
    public string StartScene { get; set; } = "Scenes/Main.empirescene";

    public static EmpireProject? TryRead(string folder)
    {
        try
        {
            string file = Path.Combine(folder, FileName);
            if (File.Exists(file))
                return JsonSerializer.Deserialize(File.ReadAllText(file), HubJson.Default.EmpireProject);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not read {FileName} in {folder}: {ex.Message}");
        }
        return null;
    }

    public void Write(string folder) =>
        File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(this, HubJson.Default.EmpireProject));

    public static bool LooksLikeProject(string folder) =>
        File.Exists(Path.Combine(folder, FileName)) || FindScenes(folder).Any();

    // The scene the editor opens: the project's start scene if it exists, else Main, else the first scene found.
    public static string? FindStartScene(string folder)
    {
        if (TryRead(folder) is { } project)
        {
            string start = Path.Combine(folder, project.StartScene);
            if (File.Exists(start)) return start;
        }
        var scenes = FindScenes(folder).ToList();
        return scenes.FirstOrDefault(s => Path.GetFileNameWithoutExtension(s) == "Main") ?? scenes.FirstOrDefault();
    }

    private static IEnumerable<string> FindScenes(string folder)
    {
        string scenes = Path.Combine(folder, "Scenes");
        return Directory.Exists(scenes)
            ? Directory.GetFiles(scenes, "*.empirescene").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            : Enumerable.Empty<string>();
    }
}
