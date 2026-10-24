using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EmpireHub;

public readonly record struct InstallProgress(string Stage, double Fraction);   // Fraction 0 to 1, or -1 when it can't be measured

// Downloads a release's zip from GitHub and unpacks the editor in it next to the other installs, as
// <installs folder>/<version>/ (the hub's own installs are in <hub>/Empire/<version>/).
//
// It only trusts what it can check: the download must come from GitHub over https, must match the checksum GitHub lists
// for it, must be a zip with an Empire editor in it (Empire/<version>/empire.dll), and is unpacked into a temporary folder
// that only becomes <version>/ once everything is in place, so a failed or cancelled install leaves nothing behind.
public static class ReleaseInstaller
{
    public const string MarkerFile = ".installed-from-github.json";

    private const long MaxDownloadBytes = 2L * 1024 * 1024 * 1024;      // 2 GB: far bigger than any Empire release
    private const long MaxUnpackedBytes = 6L * 1024 * 1024 * 1024;      // a zip that unpacks to more than this is refused

    // Where the hub's installs live: <hub>/Empire.
    public static string DefaultRoot => Path.Combine(AppContext.BaseDirectory, "Empire");

    // The versions already in an installs folder, by folder name.
    public static HashSet<string> InstalledVersions(string root) =>
        Directory.Exists(root)
            ? Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, "empire.dll")))
                .Select(d => Path.GetFileName(d)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static async Task<string> InstallAsync(HttpClient http, EmpireRelease release, string root,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        if (release.Asset is not { } asset)
            throw new InstallException($"Empire {release.Version} has no download for this computer ({ReleaseCatalog.CurrentPlatform}).");
        if (!IsAllowedUrl(asset.Url))
            throw new InstallException("This release's download isn't on GitHub, so the hub won't fetch it.");
        if (InstalledVersions(root).Contains(release.Version))
            throw new InstallException($"Empire {release.Version} is already installed.");

        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InstallException($"Can't write to {root}: {ex.Message} Move Empire Hub to a folder you can write to.");
        }

        string zip = Path.Combine(root, $".download-{Guid.NewGuid():N}.zip");
        string staging = Path.Combine(root, $".installing-{Guid.NewGuid():N}");
        try
        {
            string sha = await DownloadAsync(http, asset, zip, progress, ct);
            if (asset.Sha256 is { } expected && !expected.Equals(sha, StringComparison.OrdinalIgnoreCase))
                throw new InstallException("The download doesn't match the checksum GitHub lists for it, so it was thrown away. Try again.");

            progress?.Report(new InstallProgress("Unpacking", -1));
            string folder = await Task.Run(() => Unpack(zip, release.Version, staging, ct), ct);

            string final = Path.Combine(root, folder);
            if (Directory.Exists(final))
                throw new InstallException($"Empire {folder} is already installed.");

            File.WriteAllText(Path.Combine(staging, MarkerFile), JsonSerializer.Serialize(new InstallMarker
            {
                Version = folder, Tag = release.Tag, Source = asset.Url, Sha256 = sha, InstalledAt = DateTimeOffset.Now,
            }, ReleaseJson.Default.InstallMarker));
            Directory.Move(staging, final);
            progress?.Report(new InstallProgress("Done", 1));
            return final;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InstallException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or HttpRequestException)
        {
            throw new InstallException($"The install failed: {ex.Message}");
        }
        finally
        {
            TryDelete(zip);
            TryDeleteFolder(staging);
        }
    }

    // Only GitHub, over https. (Redirects to GitHub's download servers are fine: they are github.com or *.githubusercontent.com.)
    // When the hub has been pointed at another releases list for testing, this computer's own address is allowed too.
    public static bool IsAllowedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (ReleaseCatalog.UsesOverride && uri.IsLoopback && uri.Scheme is "http" or "https") return true;
        if (uri.Scheme != "https") return false;
        string host = uri.Host.ToLowerInvariant();
        return host == "github.com" || host.EndsWith(".github.com") || host.EndsWith(".githubusercontent.com");
    }

    private static async Task<string> DownloadAsync(HttpClient http, ReleaseAsset asset, string path,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new InstallProgress("Downloading", 0));
        using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InstallException($"GitHub answered with an error ({(int)response.StatusCode}) for the download.");
        if (response.RequestMessage?.RequestUri is { } finalUri && !IsAllowedUrl(finalUri.ToString()))
            throw new InstallException("GitHub sent the download to somewhere that isn't GitHub, so the hub stopped.");

        long total = response.Content.Headers.ContentLength ?? asset.Size;
        if (total > MaxDownloadBytes)
            throw new InstallException("This download is much bigger than any Empire release should be, so the hub won't fetch it.");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var file = File.Create(path))
        {
            var buffer = new byte[128 * 1024];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                received += read;
                if (received > MaxDownloadBytes)
                    throw new InstallException("The download is bigger than expected, so the hub stopped.");
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                progress?.Report(new InstallProgress("Downloading", total > 0 ? Math.Min(1.0, (double)received / total) : -1));
            }
            if (total > 0 && received != total)
                throw new InstallException("The download ended early. Check your connection and try again.");
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    // Unpacks Empire/<version>/ from the zip into stagingFolder. Returns the version folder's name (the zip says what
    // version it holds, which is what the hub and projects go by).
    public static string Unpack(string zipPath, string wantedVersion, string stagingFolder, CancellationToken ct = default)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        // Which versions the zip holds: every Empire/<name>/empire.dll.
        var editors = zip.Entries.Select(e => Regex.Match(Normalize(e.FullName), @"^Empire/([^/]+)/empire\.dll$"))
            .Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct().ToList();
        if (editors.Count == 0)
            throw new InstallException("This download doesn't contain an Empire editor (no Empire/<version>/empire.dll in it).");
        string folder = editors.FirstOrDefault(f => f.Equals(wantedVersion, StringComparison.OrdinalIgnoreCase)) ?? editors[0];
        if (!Regex.IsMatch(folder, @"^[0-9A-Za-z][0-9A-Za-z._+\-]*$") || folder.Contains(".."))
            throw new InstallException($"The version folder in this download ('{folder}') isn't a usable name.");

        string prefix = $"Empire/{folder}/";
        Directory.CreateDirectory(stagingFolder);
        string stagingFull = Path.GetFullPath(stagingFolder) + Path.DirectorySeparatorChar;
        long written = 0;

        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            string name = Normalize(entry.FullName);
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length == prefix.Length) continue;

            string relative = name[prefix.Length..];
            if (relative.Split('/').Any(part => part is ".." || part.Contains(':')) || relative.StartsWith('/'))
                throw new InstallException($"This download has an unsafe path in it ('{entry.FullName}'), so the hub stopped.");
            string dest = Path.GetFullPath(Path.Combine(stagingFolder, relative));
            if (!dest.StartsWith(stagingFull, StringComparison.Ordinal))
                throw new InstallException($"This download has an unsafe path in it ('{entry.FullName}'), so the hub stopped.");

            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(dest);
                continue;
            }

            // A zip made on macOS or Linux remembers each file's permissions in its high bits.
            uint unixMode = (uint)entry.ExternalAttributes >> 16;
            if ((unixMode & 0xF000) == 0xA000)
                throw new InstallException($"This download contains a symbolic link ('{entry.FullName}'), which the hub doesn't unpack.");

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using (var source = entry.Open())
            using (var file = File.Create(dest))
            {
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += read;
                    if (written > MaxUnpackedBytes)
                        throw new InstallException("This download unpacks to far more than an Empire release should, so the hub stopped.");
                    file.Write(buffer, 0, read);
                }
            }
            if (!OperatingSystem.IsWindows() && (unixMode & 0x1FF) != 0)
                File.SetUnixFileMode(dest, (UnixFileMode)(unixMode & 0x1FF) | UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        if (!File.Exists(Path.Combine(stagingFolder, "empire.dll")))
            throw new InstallException("The editor in this download is incomplete (empire.dll is missing).");

        // The editor starts through its own launcher; make sure it can run even if the zip didn't keep the permission.
        if (!OperatingSystem.IsWindows())
            foreach (var launcher in new[] { "empire", Path.Combine("Player", "Player") })
            {
                string path = Path.Combine(stagingFolder, launcher);
                if (File.Exists(path))
                    File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
        return folder;
    }

    private static string Normalize(string name) => name.Replace('\\', '/');

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteFolder(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
