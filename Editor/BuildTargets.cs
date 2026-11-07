using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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

// A released editor keeps one copy of what it has in common with the player. Player/ holds only the player's own files and a list
// of the rest, which are in the same places in the editor's folder, the one above it.
public static class PlayerFiles
{
    public const string SharedList = "shared-with-editor.txt";   // SHARED_LIST in publish.py

    // The files a player shares with its editor, as paths with /. Empty for a player that has everything itself.
    public static IReadOnlyList<string> Shared(string player)
    {
        string list = Path.Combine(player, SharedList);
        if (!File.Exists(list)) return [];
        return File.ReadAllLines(list).Where(l => l.Length > 0 && !Path.IsPathRooted(l) && !l.Split('/').Contains("..")).ToList();
    }

    // Native libraries the editor has and games don't: the editor's own, and what Player/LeftOutOfGames.targets leaves out.
    public static bool IsLeftOut(string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName);
        if (name.StartsWith("lib", StringComparison.Ordinal)) name = name[3..];
        return name is "imgui" or "glad" or "mscordbi" or "clrgc" or "clrgcexp" || fileName is "libglfw.3.dylib" or "libglfw.3.5.dylib" ||
               new[] { "mscordaccore", "shaderc_shared", "SPIRV-Tools-shared" }.Any(start => name.StartsWith(start, StringComparison.Ordinal));
    }

    // The libraries a compiled script or package refers to that aren't in these folders, so it can't run there.
    public static List<string> MissingLibraries(string dll, params string[] folders)
    {
        using var file = new PEReader(File.OpenRead(dll));
        var metadata = file.GetMetadataReader();
        return metadata.AssemblyReferences.Select(r => metadata.GetString(metadata.GetAssemblyReference(r).Name))
            .Where(name => !folders.Any(folder => File.Exists(Path.Combine(folder, name + ".dll"))))
            .ToList();
    }

    // Takes the runtime files of an earlier build out of a game's folder, so what is no longer in a game doesn't stay there and get
    // shipped with it. Only in a folder that holds a built game, and only runtime files, so anything else put there is kept.
    public static void ClearEarlierBuild(string game)
    {
        if (!File.Exists(Path.Combine(game, "GameData", "game.json"))) return;
        foreach (string file in Directory.GetFiles(game))
            if (IsRuntimeFile(Path.GetFileName(file)))
                File.Delete(file);
        string vulkan = Path.Combine(game, "vulkan");
        if (Directory.Exists(vulkan)) Directory.Delete(vulkan, recursive: true);
    }

    private static bool IsRuntimeFile(string name) =>
        name is "createdump" or "createdump.exe" || name.Contains(".so.", StringComparison.Ordinal) ||
        new[] { ".dll", ".dylib", ".so", ".pdb", ".deps.json", ".runtimeconfig.json" }.Any(end => name.EndsWith(end, StringComparison.OrdinalIgnoreCase));

    // Copies the shared files into a game. A downloaded player already has them, so its own copies have gone in with the rest of it.
    public static void CopyShared(string player, string game)
    {
        string editor = Path.GetDirectoryName(Path.GetFullPath(player))!;
        foreach (string file in Shared(player))
        {
            if (File.Exists(Path.Combine(player, file))) continue;
            string source = Path.Combine(editor, file);
            if (!File.Exists(source))
                throw new FileNotFoundException($"This editor is missing {file}, which games need. Install this version again.");
            string target = Path.Combine(game, file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(target, File.GetUnixFileMode(source));
        }
    }
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

    // Unpacks just the player from an editor zip: Empire/<version>/Player/, or inside the editor app in a Mac zip, and the files it
    // shares with the editor, which are in the editor's folder in the zip.
    public static void Unpack(string zipPath, string folder)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        string prefix = zip.Entries
            .Select(e => Regex.Match(e.FullName.Replace('\\', '/'), @"^(Empire/[^/]+/(?:[^/]+\.app/Contents/MacOS/)?Player/)Player\.dll$"))
            .FirstOrDefault(m => m.Success)?.Groups[1].Value
            ?? throw new InstallException("This download doesn't contain a built player.");
        ReleaseInstaller.ExtractEntries(zip, prefix, folder, skip: null, CancellationToken.None);

        var shared = PlayerFiles.Shared(folder).ToHashSet();
        string editor = prefix[..^"Player/".Length];
        if (shared.Count > 0)
            ReleaseInstaller.ExtractEntries(zip, editor, folder, name => !shared.Contains(name[editor.Length..]), CancellationToken.None);
    }
}
