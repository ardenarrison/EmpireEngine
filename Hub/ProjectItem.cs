namespace EmpireHub;

// One row of the project list.
public sealed class ProjectItem
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required DateTime LastOpened { get; init; }
    public string? EditorVersion { get; init; }
    public bool Exists { get; init; }

    public string VersionText => !Exists ? "Missing" : EditorVersion is null ? "Any version" : "Empire Engine " + EditorVersion;
    public double RowOpacity => Exists ? 1.0 : 0.5;

    public string LastOpenedText
    {
        get
        {
            var ago = DateTime.Now - LastOpened;
            if (ago.TotalMinutes < 1) return "Just now";
            if (ago.TotalHours < 1) return $"{(int)ago.TotalMinutes} min ago";
            if (ago.TotalDays < 1) return $"{(int)ago.TotalHours} h ago";
            if (ago.TotalDays < 7) return $"{(int)ago.TotalDays} d ago";
            return LastOpened.ToString("d MMM yyyy");
        }
    }

    public static ProjectItem From(ProjectEntry entry)
    {
        bool exists = Directory.Exists(entry.Path);
        var project = exists ? EmpireProject.TryRead(entry.Path) : null;
        return new ProjectItem
        {
            Name = project?.Name is { Length: > 0 } name ? name : System.IO.Path.GetFileName(entry.Path.TrimEnd('/', '\\')),
            Path = entry.Path,
            LastOpened = entry.LastOpened,
            EditorVersion = project?.EditorVersion,
            Exists = exists,
        };
    }
}
