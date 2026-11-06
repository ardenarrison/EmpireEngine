using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using EmpireHub;

namespace Editor;

public enum BuildPlatform { Windows, Mac, Linux }

// The platforms Build Project can make a game for. Building for this computer's platform uses its own processor type. Building
// for another uses the one most of that platform's computers have: x64 for Windows and Linux, Apple Silicon for the Mac.
public static class BuildTargets
{
    public static BuildPlatform ThisPlatform =>
        OperatingSystem.IsWindows() ? BuildPlatform.Windows : OperatingSystem.IsLinux() ? BuildPlatform.Linux : BuildPlatform.Mac;

    public static string Rid(BuildPlatform platform) =>
        platform == ThisPlatform ? RuntimeInformation.RuntimeIdentifier : platform switch
        {
            BuildPlatform.Windows => "win-x64",
            BuildPlatform.Mac => "osx-arm64",
            _ => "linux-x64",
        };

    // The game's folder. A build for another platform gets a folder of its own: building it over this platform's game would mix
    // two sets of runtime files that don't work together.
    public static string FolderName(string name, BuildPlatform platform) =>
        platform == ThisPlatform ? name : $"{name}-{platform}";
}

// The built players for other platforms. A released editor only carries the player for its own platform, so the one for another
// comes out of that platform's editor zip in the same release, the zip the Hub installs from. Each is downloaded once and kept.
public static class PlayerDownloads
{
    private static string Cache =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EmpireEditor", "Players");

    // The folder holding this version's player for a platform (a name like win-x64). Throws InstallException with a plain
    // explanation when it can't be had.
    public static async Task<string> GetAsync(string version, string platform, Action<string> status)
    {
        string folder = Path.Combine(Cache, version, platform);
        if (File.Exists(Path.Combine(folder, "Player.dll"))) return folder;

        status($"Finding the {platform} player...");
        using var http = ReleaseCatalog.CreateClient();
        var releases = await ReleaseCatalog.FetchAsync(http, CancellationToken.None, platform);
        var asset = releases.FirstOrDefault(r => r.Version == version)?.Asset
            ?? throw new InstallException($"Empire {version} has no {platform} download on GitHub, so there is no player to build with.");
        if (!ReleaseInstaller.IsAllowedUrl(asset.Url))
            throw new InstallException("This release's download isn't on GitHub, so the editor won't fetch it.");

        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        string temporary = $"{folder}.{Guid.NewGuid():N}";
        string zip = temporary + ".zip";
        try
        {
            var progress = new Progress<InstallProgress>(p =>
                status(p.Fraction >= 0 ? $"Downloading the {platform} player: {p.Fraction:P0}" : $"Downloading the {platform} player..."));
            string sha = await ReleaseInstaller.DownloadAsync(http, asset, zip, progress, CancellationToken.None);
            if (asset.Sha256 is { } expected && !expected.Equals(sha, StringComparison.OrdinalIgnoreCase))
                throw new InstallException("The download doesn't match the checksum GitHub lists for it, so it was thrown away. Try again.");

            status($"Unpacking the {platform} player...");
            Unpack(zip, temporary);
            Directory.Move(temporary, folder);
            return folder;
        }
        finally
        {
            if (File.Exists(zip)) File.Delete(zip);
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    // Unpacks just the player from an editor zip: Empire/<version>/Player/, or inside the editor app in a Mac zip.
    public static void Unpack(string zipPath, string folder)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        string prefix = zip.Entries
            .Select(e => Regex.Match(e.FullName.Replace('\\', '/'), @"^(Empire/[^/]+/(?:[^/]+\.app/Contents/MacOS/)?Player/)Player\.dll$"))
            .FirstOrDefault(m => m.Success)?.Groups[1].Value
            ?? throw new InstallException("This download doesn't contain a built player.");
        ReleaseInstaller.ExtractEntries(zip, prefix, folder, skip: null, CancellationToken.None);
    }
}
