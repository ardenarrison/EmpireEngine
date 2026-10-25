using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace EmpireHub;

// A newer Hub that GitHub has a download for.
public sealed record HubUpdate(string Version, string Tag, ReleaseAsset Asset, string? PageUrl, string Notes);

// Updates the Hub from its GitHub releases: finds a newer version, downloads the Hub's zip, checks it, and swaps the files.
//
// A running program can't safely overwrite itself (Windows locks it, macOS can kill a signed program whose file changes), so
// the new version is unpacked into a temporary folder and started with --apply-update. That copy waits for the old Hub to
// close, copies its own files over the Hub's folder, and starts the Hub again. The editors in <hub>/Empire/ are never touched.
//
// What is checked on the way: the download must come from GitHub over https and match the checksum GitHub lists for it
// (without one the Hub refuses, since this is a program it is about to run), and the zip is unpacked with the same
// protections as an editor install.
public static class HubUpdater
{
    public const string ApplyArgument = "--apply-update";
    private const string TempPrefix = "EmpireHubUpdate-";

    // The version of the running Hub, from the build.
    public static string CurrentVersion =>
        typeof(HubUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    // Where the Hub keeps its settings (the same folder as HubSettings.Folder, which isn't part of the update code).
    private static string SettingsFolder =>
        Environment.GetEnvironmentVariable("EMPIRE_HUB_SETTINGS") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EmpireHub");

    public static string ExecutableName => OperatingSystem.IsWindows() ? "EmpireHub.exe" : "EmpireHub";

    // What an update replaces: for a plain folder the folder itself, for the macOS app the Empire Engine Hub.app folder.
    public static string CurrentRoot => HubPaths.BundlePath(AppContext.BaseDirectory) ?? AppContext.BaseDirectory.TrimEnd('/', '\\');

    // Whether a root is an app bundle (its name ends in .app) rather than a plain folder.
    public static bool IsBundleRoot(string root) => root.TrimEnd('/', '\\').EndsWith(".app", StringComparison.OrdinalIgnoreCase);

    // The Hub's program inside a root: at the top of a plain folder, in Contents/MacOS in an app bundle.
    public static string ExecutableIn(string root) =>
        IsBundleRoot(root) ? Path.Combine(root, "Contents", "MacOS", "EmpireHub") : Path.Combine(root, ExecutableName);

    // The newest final release that has a Hub download for this computer and is newer than the running version.
    public static HubUpdate? FindUpdate(IEnumerable<EmpireRelease> releases, string currentVersion)
    {
        var current = ReleaseCatalog.Numeric(currentVersion);
        return releases
            .Where(r => !r.Prerelease && r.HubAsset is not null && ReleaseCatalog.Numeric(r.Version) > current)
            .OrderByDescending(r => ReleaseCatalog.Numeric(r.Version))
            .Select(r => new HubUpdate(r.Version, r.Tag, r.HubAsset!, r.PageUrl, r.Notes))
            .FirstOrDefault();
    }

    // Asks GitHub for the releases and returns the update to offer, or null when this Hub is the newest. Throws InstallException
    // with a plain explanation when GitHub can't be reached.
    public static async Task<HubUpdate?> CheckAsync(HttpClient http, CancellationToken ct, string? currentVersion = null, string? platform = null) =>
        FindUpdate(await ReleaseCatalog.FetchAsync(http, ct, platform), currentVersion ?? CurrentVersion);

    // Whether this Hub can replace its own files: it has to be the installed program (EmpireHub in its own folder, not run
    // through "dotnet run" or from a build folder) and its folder has to be writable.
    public static bool CanReplace(string hubFolder, string? processPath, out string reason)
    {
        if (processPath is null || !Path.GetFileName(processPath).Equals(ExecutableName, StringComparison.OrdinalIgnoreCase) ||
            !SameFolder(Path.GetDirectoryName(processPath), hubFolder) ||
            File.Exists(Path.Combine(hubFolder, "EmpireHub.pdb")))   // only a build has one; the published Hub is built without
        {
            reason = "This copy of the Hub is running from a build, not from an installed Hub, so it can't replace itself.";
            return false;
        }
        bool inApp = HubPaths.IsInAppBundle(hubFolder);
        try
        {
            string probe = Path.Combine(hubFolder, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = inApp
                ? "The Hub can't change itself where it is. If you opened it straight from the Downloads folder, drag it into Applications, open it from there, and try again. Or download the new version instead."
                : $"The Hub can't write to its own folder ({hubFolder}), so it can't update itself there. Download the new version instead.";
            return false;
        }
        reason = "";
        return true;
    }

    private static bool SameFolder(string? a, string b) =>
        a is not null && string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    // Downloads the update and unpacks it into a new temporary folder. Returns the folder holding the new Hub, ready for
    // LaunchApply. Nothing of the running Hub has been changed yet, and a failure leaves nothing behind.
    public static async Task<string> DownloadAsync(HttpClient http, HubUpdate update, IProgress<InstallProgress>? progress,
        CancellationToken ct, string? tempRoot = null)
    {
        var asset = update.Asset;
        if (!ReleaseInstaller.IsAllowedUrl(asset.Url))
            throw new InstallException("This update's download isn't on GitHub, so the Hub won't fetch it.");
        if (asset.Sha256 is null)
            throw new InstallException("GitHub lists no checksum for this update, so the Hub can't check it and won't run it.");

        string work = Path.Combine(tempRoot ?? Path.GetTempPath(), TempPrefix + Guid.NewGuid().ToString("N"));
        bool done = false;
        try
        {
            Directory.CreateDirectory(work);
            string zip = Path.Combine(work, "download.zip");
            string sha = await ReleaseInstaller.DownloadAsync(http, asset, zip, progress, ct);
            if (!asset.Sha256.Equals(sha, StringComparison.OrdinalIgnoreCase))
                throw new InstallException("The download doesn't match the checksum GitHub lists for it, so it was thrown away. Try again.");

            progress?.Report(new InstallProgress("Unpacking", -1));
            string hub = Path.Combine(work, "hub");
            string root = await Task.Run(() => Unpack(zip, hub, ct), ct);
            File.Delete(zip);
            done = true;
            progress?.Report(new InstallProgress("Done", 1));
            return root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or HttpRequestException)
        {
            throw new InstallException($"The update failed: {ex.Message}");
        }
        finally
        {
            if (!done) TryDeleteFolder(work);
        }
    }

    // Unpacks the Hub's zip into `folder` and returns the new Hub's root. A Windows or Linux zip holds the Hub's files at the top level
    // (anything under Empire/ is left out, so an update can never put files into the installed editors); the macOS zip holds one
    // Empire Engine Hub.app, whose root is that folder.
    public static string Unpack(string zipPath, string folder, CancellationToken ct = default)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        string? app = zip.Entries.Select(e => Regex.Match(e.FullName.Replace('\\', '/'), @"^([^/]+\.app)/Contents/MacOS/EmpireHub$"))
            .FirstOrDefault(m => m.Success)?.Groups[1].Value;

        string root = folder;
        if (app is not null)
        {
            if (!Regex.IsMatch(app, @"^[0-9A-Za-z][0-9A-Za-z ._\-]*\.app$") || app.Contains(".."))
                throw new InstallException($"The app in this download ('{app}') doesn't have a usable name.");
            root = Path.Combine(folder, app);
            ReleaseInstaller.ExtractEntries(zip, app + "/", root, skip: null, ct);
        }
        else
        {
            ReleaseInstaller.ExtractEntries(zip, "", folder, name => name.StartsWith("Empire/", StringComparison.Ordinal), ct);
        }

        string exe = ExecutableIn(root);
        if (!File.Exists(exe) || !File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "EmpireHub.dll")))
            throw new InstallException("This download doesn't contain the Hub (no EmpireHub program in it).");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        return root;
    }

    // Starts the new Hub in update mode. The caller then closes this Hub, which is what the new one is waiting for.
    // newRoot is what DownloadAsync returned and currentRoot is what is being replaced (CurrentRoot).
    public static void LaunchApply(string newRoot, string currentRoot)
    {
        var info = new ProcessStartInfo(ExecutableIn(newRoot)) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(ExecutableIn(newRoot)) };
        info.ArgumentList.Add(ApplyArgument);
        info.ArgumentList.Add(currentRoot);
        info.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(info);
    }

    // What the new Hub does when started with --apply-update <hub folder> <old process id>: wait for the old Hub to close,
    // copy its own files over that folder, and start the Hub again. Returns the process exit code.
    public static int RunApply(string[] args)
    {
        if (args.Length < 3) return 2;
        string hubFolder = args[1];
        string errorFile = Path.Combine(SettingsFolder, "update-error.txt");
        try
        {
            if (int.TryParse(args[2], out int pid))
            {
                try { Process.GetProcessById(pid).WaitForExit(60_000); }
                catch (ArgumentException) { /* it has already closed */ }
            }
            string source = HubPaths.BundlePath(AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
            string? problem = ApplyFiles(source, hubFolder);
            if (problem is not null) throw new InvalidOperationException(problem);
            if (File.Exists(errorFile)) File.Delete(errorFile);
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(SettingsFolder);
                File.WriteAllText(errorFile, $"The Hub's update could not be finished ({DateTime.Now:g}): {ex.Message}\n");
            }
            catch (Exception) { /* nowhere to write it */ }
        }

        string hub = ExecutableIn(hubFolder);
        if (File.Exists(hub))
        {
            // An app is opened the way macOS opens apps; a plain folder's program is started directly.
            if (IsBundleRoot(hubFolder) && File.Exists("/usr/bin/open"))
                Process.Start(new ProcessStartInfo("/usr/bin/open") { ArgumentList = { hubFolder }, UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo(hub) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(hub) });
        }
        return 0;
    }

    // Puts the new Hub (sourceRoot) over the installed one (targetRoot). Both have to be the same kind: two plain folders (the new files
    // go over the old; files only the old one has are kept) or two app bundles (the whole app is made exactly like the new one, so its
    // seal stays valid). Returns null when done, or a plain explanation of why it wasn't.
    public static string? ApplyFiles(string sourceRoot, string targetRoot, bool resign = true)
    {
        bool sourceApp = IsBundleRoot(sourceRoot), targetApp = IsBundleRoot(targetRoot);
        if (sourceApp != targetApp)
            return "The new Hub is packaged differently from this one (an app instead of a folder, or the other way round), so it can't be put in place here. Download the new version instead.";
        if (sourceApp)
        {
            // Only ever rewrite something that already is a Hub app, since extra files in it are removed.
            if (!File.Exists(ExecutableIn(targetRoot)))
                return $"{targetRoot} isn't a Hub app, so it was left alone.";
            CopyOver(sourceRoot, targetRoot, removeExtra: true);
            return resign ? Resign(targetRoot) : null;
        }
        CopyOver(sourceRoot, targetRoot);
        return null;
    }

    // Signs the app again, ad hoc, once its files are in place. The signature that came with the download is kept in file attributes
    // that the unpacking here doesn't restore, and the app is only accepted by macOS as one sealed thing if it is signed. (codesign comes
    // with macOS.) Returns null, or a plain explanation when it couldn't be done.
    public static string? Resign(string appRoot)
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists("/usr/bin/codesign")) return null;
        try
        {
            using var codesign = Process.Start(new ProcessStartInfo("/usr/bin/codesign")
            {
                ArgumentList = { "--force", "--deep", "--sign", "-", "--timestamp=none", appRoot },
                UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true,
            })!;
            var error = codesign.StandardError.ReadToEndAsync();
            if (!codesign.WaitForExit(120_000))
            {
                try { codesign.Kill(); } catch (InvalidOperationException) { }
                return "The updated Hub was put in place, but signing it again took too long.";
            }
            return codesign.ExitCode == 0 ? null : $"The updated Hub was put in place, but signing it again failed: {error.Result.Trim()}";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return $"The updated Hub was put in place, but signing it again failed: {ex.Message}";
        }
    }

    // Copies the new Hub's files over the Hub's folder. The Empire folder (the installed editors) is left alone, and files
    // that only exist in the old Hub are kept. Each file is written next to its target and then moved over it, so a file
    // is never half written, and a replaced program is a new file (macOS kills a signed program whose file is changed in place).
    // With removeExtra, anything in the target that the new Hub doesn't have is removed too, so the result is exactly the new Hub.
    public static void CopyOver(string from, string to, bool removeExtra = false)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            string name = Path.GetFileName(file);
            if (name.StartsWith('.')) continue;
            Retry(() => ReplaceFile(file, Path.Combine(to, name)));
        }
        foreach (var dir in Directory.GetDirectories(from))
        {
            string name = Path.GetFileName(dir);
            if (name.Equals("Empire", StringComparison.OrdinalIgnoreCase) || name.StartsWith('.')) continue;
            CopyOver(dir, Path.Combine(to, name), removeExtra);
        }

        if (!removeExtra) return;
        foreach (var file in Directory.GetFiles(to))
        {
            string name = Path.GetFileName(file);
            if (!name.StartsWith('.') && !File.Exists(Path.Combine(from, name))) Retry(() => File.Delete(file));
        }
        foreach (var dir in Directory.GetDirectories(to))
        {
            string name = Path.GetFileName(dir);
            if (!name.Equals("Empire", StringComparison.OrdinalIgnoreCase) && !name.StartsWith('.') && !Directory.Exists(Path.Combine(from, name)))
                Retry(() => Directory.Delete(dir, recursive: true));
        }
    }

    // The text of the error the last update left behind, or null. The Hub shows it and clears it.
    public static string? TakeLastError()
    {
        string file = Path.Combine(SettingsFolder, "update-error.txt");
        try
        {
            if (!File.Exists(file)) return null;
            string text = File.ReadAllText(file).Trim();
            File.Delete(file);
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void ReplaceFile(string source, string target)
    {
        string temp = target + $".new-{Guid.NewGuid():N}";
        try
        {
            File.Copy(source, temp, overwrite: true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, File.GetUnixFileMode(source));
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // Files can stay locked for a moment after a program closes (Windows), so a copy is tried again a few times.
    private static void Retry(Action action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20) { Thread.Sleep(500); }
        }
    }

    // Deletes update folders an earlier run left behind (the new Hub can't delete the folder it is running from).
    public static void CleanOldUpdates(string? tempRoot = null)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(tempRoot ?? Path.GetTempPath(), TempPrefix + "*"))
                if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) > TimeSpan.FromHours(1))
                    TryDeleteFolder(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteFolder(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
