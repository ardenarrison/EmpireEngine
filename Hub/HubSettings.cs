using System.Text.Json;

namespace EmpireHub;

// What the hub remembers between runs: the project list and editor installs the user located by hand.
// Stored as settings.json in the per-user app data folder (AppData on Windows, Application Support on macOS,
// ~/.config on Linux).
public sealed class HubSettings
{
    public List<ProjectEntry> Projects { get; set; } = new();
    public List<string> LocatedInstalls { get; set; } = new();
    public string? LastLocation { get; set; }

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EmpireHub");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static HubSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), HubJson.Default.HubSettings) ?? new HubSettings();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not read {FilePath}: {ex.Message}");
        }
        return new HubSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, HubJson.Default.HubSettings));
    }

    // Adds the project, or moves it to the top if it is already listed.
    public void Touch(string projectPath)
    {
        string full = Path.GetFullPath(projectPath);
        Projects.RemoveAll(p => SamePath(p.Path, full));
        Projects.Insert(0, new ProjectEntry { Path = full, LastOpened = DateTime.Now });
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
}

public sealed class ProjectEntry
{
    public string Path { get; set; } = "";
    public DateTime LastOpened { get; set; }
}
