using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

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
    private static string SettingsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EmpireHub");

    public static string ExecutableName => OperatingSystem.IsWindows() ? "EmpireHub.exe" : "EmpireHub";

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
        try
        {
            string probe = Path.Combine(hubFolder, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = $"The Hub can't write to its own folder ({hubFolder}), so it can't update itself there. Download the new version instead.";
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
            await Task.Run(() => Unpack(zip, hub, ct), ct);
            File.Delete(zip);
            done = true;
            progress?.Report(new InstallProgress("Done", 1));
            return hub;
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

    // Unpacks the Hub's zip. It holds the Hub's files at the top level; anything under Empire/ is left out, so an update can
    // never put files into (or over) the installed editors.
    public static void Unpack(string zipPath, string folder, CancellationToken ct = default)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        ReleaseInstaller.ExtractEntries(zip, "", folder, name => name.StartsWith("Empire/", StringComparison.Ordinal), ct);

        string exe = Path.Combine(folder, ExecutableName);
        if (!File.Exists(exe) || !File.Exists(Path.Combine(folder, "EmpireHub.dll")))
            throw new InstallException("This download doesn't contain the Hub (no EmpireHub program in it).");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    // Starts the new Hub in update mode. The caller then closes this Hub, which is what the new one is waiting for.
    public static void LaunchApply(string newHubFolder, string hubFolder)
    {
        var info = new ProcessStartInfo(Path.Combine(newHubFolder, ExecutableName)) { UseShellExecute = false, WorkingDirectory = newHubFolder };
        info.ArgumentList.Add(ApplyArgument);
        info.ArgumentList.Add(hubFolder);
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
            CopyOver(AppContext.BaseDirectory, hubFolder);
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

        string hub = Path.Combine(hubFolder, ExecutableName);
        if (File.Exists(hub))
            Process.Start(new ProcessStartInfo(hub) { UseShellExecute = false, WorkingDirectory = hubFolder });
        return 0;
    }

    // Copies the new Hub's files over the Hub's folder. The Empire folder (the installed editors) is left alone, and files
    // that only exist in the old Hub are kept. Each file is written next to its target and then moved over it, so a file
    // is never half written, and a replaced program is a new file (macOS kills a signed program whose file is changed in place).
    public static void CopyOver(string from, string to)
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
            CopyOver(dir, Path.Combine(to, name));
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
