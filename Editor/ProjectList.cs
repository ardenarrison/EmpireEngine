using EmpireHub;

namespace Editor;

public sealed record ProjectChoice(string Folder, string Name, string Type);

// The projects File > Change Project offers. On the desktop those are the Hub's, and a project made in the editor joins them. An
// iPad has no Hub: its projects are the folders in the app's Documents folder.
public static class ProjectList
{
    private static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public static List<ProjectChoice> All() => OperatingSystem.IsIOS()
        ? InFolder(Documents)
        : HubSettings.Load().Projects.Select(p => p.Path).Where(Directory.Exists).Where(EmpireProject.LooksLikeProject).Select(Describe).ToList();

    // The projects directly inside a folder, by name.
    public static List<ProjectChoice> InFolder(string folder) => !Directory.Exists(folder)
        ? new List<ProjectChoice>()
        : Directory.GetDirectories(folder).Where(EmpireProject.LooksLikeProject).Select(Describe)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public static ProjectChoice Describe(string folder)
    {
        var project = EmpireProject.TryRead(folder);
        string name = project?.Name is { Length: > 0 } saved ? saved : Path.GetFileName(folder.TrimEnd('/', '\\'));
        return new ProjectChoice(folder, name, project?.Is2D == true ? "2D" : "3D");
    }

    // Where a new project goes unless the user says otherwise.
    public static string DefaultLocation(string currentProject)
    {
        if (OperatingSystem.IsIOS()) return Documents;
        return HubSettings.Load().LastLocation is { Length: > 0 } last ? last : Path.GetDirectoryName(Path.GetFullPath(currentProject).TrimEnd('/', '\\')) ?? "";
    }

    // Puts a project at the top of the Hub's list, so the Hub shows it too.
    public static void Remember(string folder)
    {
        if (OperatingSystem.IsIOS()) return;
        var settings = HubSettings.Load();
        settings.Touch(folder);
        settings.LastLocation = Path.GetDirectoryName(Path.GetFullPath(folder).TrimEnd('/', '\\'));
        settings.Save();
    }
}
