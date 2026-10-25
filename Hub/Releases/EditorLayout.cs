namespace EmpireHub;

// How an installed editor is laid out. Everywhere but the Mac an editor is a folder holding empire.dll and its files. On the Mac the
// editor is an app: the version folder holds Empire Engine Editor.app, and the files are inside it, in Contents/MacOS. Older editors on
// a Mac are plain folders, and the Hub still opens them.
public static class EditorLayout
{
    // The folder that holds empire.dll for an editor installed in (or pointed at) `folder`: the folder itself, the app's inside when
    // the folder is an app or holds one, or null when there is no editor there.
    public static string? EntryFolder(string folder)
    {
        if (File.Exists(Path.Combine(folder, "empire.dll"))) return folder;
        if (HubUpdater.IsBundleRoot(folder)) return InsideApp(folder);
        if (!Directory.Exists(folder)) return null;
        try
        {
            foreach (var app in Directory.GetDirectories(folder, "*.app").OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
                if (InsideApp(app) is { } inside) return inside;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static string? InsideApp(string app)
    {
        string inside = Path.Combine(app, "Contents", "MacOS");
        return File.Exists(Path.Combine(inside, "empire.dll")) ? inside : null;
    }

    // The .app of an editor whose files are in entryFolder, or null for a plain folder.
    public static string? AppOf(string entryFolder) => HubPaths.BundlePath(entryFolder);
}
