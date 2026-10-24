using System.Diagnostics;

namespace EmpireHub;

// One installed copy of the Empire Engine editor: a folder holding empire.dll and its dependencies.
public sealed class EditorInstall
{
    public required string Version { get; init; }
    public required string Folder { get; init; }
    public required bool Bundled { get; init; }   // in the hub's Empire Engine folder, as opposed to located by the user
    public bool Downloaded { get; init; }         // put there by the hub from a GitHub release rather than shipped with it

    public string Kind => Downloaded ? "Downloaded from GitHub" : Bundled ? "Built with the Hub" : "Located";
    public bool CanRemove => true;
    public string RemoveText => Bundled ? "Uninstall" : "Remove";   // an editor in the hub's folder is deleted; a located one is only forgotten

    private const string EntryDll = "empire.dll";

    public static EditorInstall? FromFolder(string folder, bool bundled)
    {
        string dll = Path.Combine(folder, EntryDll);
        if (!File.Exists(dll)) return null;

        // The editor's product version comes from EmpireVersion in Directory.Build.props.
        string? version = FileVersionInfo.GetVersionInfo(dll).ProductVersion?.Split('+')[0];
        if (string.IsNullOrWhiteSpace(version)) version = Path.GetFileName(folder.TrimEnd('/', '\\'));
        bool downloaded = bundled && File.Exists(Path.Combine(folder, ReleaseInstaller.MarkerFile));
        return new EditorInstall { Version = version, Folder = Path.GetFullPath(folder), Bundled = bundled, Downloaded = downloaded };
    }

    // The bundled editors (in <hub>/Empire/<version>/) followed by the ones the user located, newest first.
    public static List<EditorInstall> Discover(HubSettings settings)
    {
        var list = new List<EditorInstall>();
        string bundledRoot = Path.Combine(AppContext.BaseDirectory, "Empire");
        if (Directory.Exists(bundledRoot))
            foreach (var dir in Directory.GetDirectories(bundledRoot))
                if (FromFolder(dir, bundled: true) is { } install) list.Add(install);

        foreach (var folder in settings.LocatedInstalls)
            if (FromFolder(folder, bundled: false) is { } install && !list.Any(i => HubSettings.SamePath(i.Folder, install.Folder)))
                list.Add(install);

        return list.OrderByDescending(i => ParseVersion(i.Version)).ThenBy(i => i.Bundled ? 0 : 1).ToList();
    }

    // The install a project should open with: its own version if installed, otherwise the newest.
    public static EditorInstall? ForProject(IReadOnlyList<EditorInstall> installs, string? projectVersion) =>
        installs.FirstOrDefault(i => i.Version == projectVersion) ?? installs.FirstOrDefault();

    public static Version ParseVersion(string text) =>
        System.Version.TryParse(text.Split('-')[0], out var v) ? v : new Version(0, 0);

    // Starts the editor on a scene. The project folder is the content root and the working directory.
    public Process Launch(string scenePath, string projectFolder)
    {
        string appHost = Path.Combine(Folder, OperatingSystem.IsWindows() ? "empire.exe" : "empire");
        var info = File.Exists(appHost)
            ? new ProcessStartInfo(appHost)
            : new ProcessStartInfo("dotnet") { ArgumentList = { Path.Combine(Folder, EntryDll) } };
        info.ArgumentList.Add(scenePath);
        info.ArgumentList.Add(projectFolder);
        info.WorkingDirectory = projectFolder;
        info.UseShellExecute = false;

        // Editors built on macOS bundle their own Vulkan loader and MoltenVK. This is a fallback for ones that don't
        // (built where neither was installed, or located by hand): their loader comes from Homebrew or the Vulkan SDK,
        // whose folders aren't on dyld's default search path. The fallback path replaces the default, so /usr/lib stays.
        if (OperatingSystem.IsMacOS())
        {
            string? existing = info.Environment.TryGetValue("DYLD_FALLBACK_LIBRARY_PATH", out var value) ? value : null;
            string?[] paths = [existing, "/opt/homebrew/lib", "/usr/local/lib", "/usr/lib"];
            info.Environment["DYLD_FALLBACK_LIBRARY_PATH"] = string.Join(':', paths.Where(p => !string.IsNullOrEmpty(p)));
        }

        return Process.Start(info) ?? throw new InvalidOperationException("The editor did not start.");
    }
}
