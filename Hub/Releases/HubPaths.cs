namespace EmpireHub;

// Where the Hub's own files and the editors it installs live. A Hub run from a build or unzipped on Windows or Linux is a plain folder
// holding its files, and installs editors in an Empire folder beside them. On macOS the Hub is an app (Empire Engine Hub.app): its files
// are inside the app, which must be left exactly as shipped (an app is signed as a whole, and one run from the Downloads folder is
// opened read-only by macOS), so editors are installed in the user's own folder instead.
public static class HubPaths
{
    // Whether the Hub's folder is the inside of an app bundle: .../Something.app/Contents/MacOS.
    public static bool IsInAppBundle(string hubFolder) => BundlePath(hubFolder) is not null;

    // The .app folder the Hub's folder is inside, or null for a plain folder.
    public static string? BundlePath(string hubFolder)
    {
        string trimmed = hubFolder.TrimEnd('/', '\\');
        string normalized = trimmed.Replace('\\', '/');
        const string tail = "/Contents/MacOS";
        if (!normalized.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) return null;
        string app = normalized[..^tail.Length];
        string name = app[(app.LastIndexOf('/') + 1)..];
        return name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && name.Length > ".app".Length ? trimmed[..^tail.Length] : null;
    }

    // Where editors are installed (the folder holding <version>/ folders): beside the Hub's files, or inside the user's app-data folder
    // when the Hub is an app bundle.
    public static string InstallsRootFor(string hubFolder, string appData) =>
        IsInAppBundle(hubFolder) ? Path.Combine(appData, "EmpireHub", "Empire") : Path.Combine(hubFolder, "Empire");

    public static string InstallsRoot =>
        InstallsRootFor(AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
}
