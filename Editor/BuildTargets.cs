using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using EmpireHub;

namespace Editor;

public enum BuildPlatform { Windows, Mac, Linux }

// The processor a game is built for.
public enum BuildArchitecture { X64, Arm64, X86, Arm32 }

// How well games built for a platform and processor run.
public enum ArchitectureSupport
{
    Works,              // the engine's native libraries exist for it and games have been run there
    Experimental,       // the native libraries exist, but games have hardly been tried there
    NoNativeLibraries,  // .NET runs there, but BRUTAL has no window, Vulkan or shader libraries for it, so games stop at start
}

// One processor type a platform can be built for. Released means the GitHub releases carry an editor for it, which is where a released
// editor gets the player from; without one, only an editor run from source can build it.
public sealed record ArchitectureOption(BuildArchitecture Architecture, string Rid, ArchitectureSupport Support, bool Released, string Label);

// The platforms and processor types Build Project can make a game for. Building for this computer's own platform and processor
// uses its own runtime. Each platform otherwise starts on the processor most of its computers have: x64 for Windows and Linux,
// Apple Silicon for the Mac.
public static class BuildTargets
{
    public static BuildPlatform ThisPlatform =>
        OperatingSystem.IsWindows() ? BuildPlatform.Windows : OperatingSystem.IsLinux() ? BuildPlatform.Linux : BuildPlatform.Mac;

    public static BuildArchitecture ThisArchitecture => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => BuildArchitecture.Arm64,
        Architecture.X86 => BuildArchitecture.X86,
        Architecture.Arm or Architecture.Armv6 => BuildArchitecture.Arm32,
        _ => BuildArchitecture.X64,
    };

    public static bool IsThisComputer(BuildPlatform platform, BuildArchitecture architecture) =>
        platform == ThisPlatform && architecture == ThisArchitecture;

    // Every processor type .NET can run games on for a platform, in the order the Build window lists them. Windows on 32-bit ARM
    // and 32-bit Macs aren't there: .NET doesn't run on them.
    public static IReadOnlyList<ArchitectureOption> Architectures(BuildPlatform platform) => platform switch
    {
        BuildPlatform.Windows =>
        [
            Option(platform, BuildArchitecture.X64, ArchitectureSupport.Works, released: true, "x64 (Intel and AMD)"),
            Option(platform, BuildArchitecture.Arm64, ArchitectureSupport.NoNativeLibraries, released: true, "arm64 (Windows on ARM)"),
            Option(platform, BuildArchitecture.X86, ArchitectureSupport.NoNativeLibraries, released: false, "x86 (32-bit)"),
        ],
        BuildPlatform.Mac =>
        [
            Option(platform, BuildArchitecture.Arm64, ArchitectureSupport.Works, released: true, "arm64 (Apple Silicon)"),
            Option(platform, BuildArchitecture.X64, ArchitectureSupport.Experimental, released: true, "x64 (Intel Mac)"),
        ],
        _ =>
        [
            Option(platform, BuildArchitecture.X64, ArchitectureSupport.Works, released: true, "x64 (Intel and AMD)"),
            Option(platform, BuildArchitecture.Arm64, ArchitectureSupport.Experimental, released: true, "arm64"),
            Option(platform, BuildArchitecture.Arm32, ArchitectureSupport.NoNativeLibraries, released: false, "arm32 (32-bit, such as older Raspberry Pi)"),
        ],
    };

    private static ArchitectureOption Option(BuildPlatform platform, BuildArchitecture architecture, ArchitectureSupport support,
        bool released, string label) =>
        new(architecture, Rid(platform, architecture), support, released, label);

    public static ArchitectureOption? Find(BuildPlatform platform, BuildArchitecture architecture) =>
        Architectures(platform).FirstOrDefault(o => o.Architecture == architecture);

    // The processor a platform's builds start on: this computer's for its own platform, otherwise the usual one.
    public static BuildArchitecture DefaultArchitecture(BuildPlatform platform) =>
        platform == ThisPlatform && Find(platform, ThisArchitecture) is not null ? ThisArchitecture
        : platform == BuildPlatform.Mac ? BuildArchitecture.Arm64 : BuildArchitecture.X64;

    // The .NET runtime identifier, such as win-x64. This computer's own is used as it is (it may say more, like linux-musl-x64).
    public static string Rid(BuildPlatform platform, BuildArchitecture architecture)
    {
        if (IsThisComputer(platform, architecture)) return RuntimeInformation.RuntimeIdentifier;
        string os = platform switch { BuildPlatform.Windows => "win", BuildPlatform.Mac => "osx", _ => "linux" };
        string cpu = architecture switch
        {
            BuildArchitecture.Arm64 => "arm64",
            BuildArchitecture.X86 => "x86",
            BuildArchitecture.Arm32 => "arm",
            _ => "x64",
        };
        return $"{os}-{cpu}";
    }

    public static string ArchitectureName(BuildArchitecture architecture) => architecture switch
    {
        BuildArchitecture.Arm64 => "arm64",
        BuildArchitecture.X86 => "x86",
        BuildArchitecture.Arm32 => "arm32",
        _ => "x64",
    };

    // The game's folder. Each platform and processor gets a folder of its own: building one over another would mix two sets of
    // runtime files that don't work together. This computer's own build is just the name, another platform's usual processor adds
    // the platform (MyGame-Windows), and any other processor adds that too (MyGame-Windows-arm64, MyGame-arm64 on this platform).
    public static string FolderName(string name, BuildPlatform platform, BuildArchitecture architecture)
    {
        if (IsThisComputer(platform, architecture)) return name;
        if (platform == ThisPlatform) return $"{name}-{ArchitectureName(architecture)}";
        bool usual = architecture == (platform == BuildPlatform.Mac ? BuildArchitecture.Arm64 : BuildArchitecture.X64);
        return usual ? $"{name}-{platform}" : $"{name}-{platform}-{ArchitectureName(architecture)}";
    }

    // Why this editor can't build a platform and processor, or null when it can. fromSource: the editor runs from a checkout of
    // the repository, so it publishes the player itself instead of taking it from a release.
    public static string? CannotBuild(BuildPlatform platform, BuildArchitecture architecture, bool fromSource)
    {
        if (Find(platform, architecture) is not { } option)
            return $".NET doesn't run on {platform} {ArchitectureName(architecture)}.";
        if (platform == BuildPlatform.Mac && fromSource && !OperatingSystem.IsMacOS())
            return "An editor run from source can only build a Mac game on a Mac, because the game has to be signed there. " +
                   "A released editor can build one on any computer.";
        if (!fromSource && !option.Released && !IsThisComputer(platform, architecture))
            return $"There is no {option.Rid} release to take the player from, so only an editor run from a checkout of the " +
                   "repository can build it (it runs dotnet publish).";
        return null;
    }

    // A warning to show before and after building, or null. Games for a processor without the native libraries are still built,
    // so they are ready if those libraries come, but they won't start yet.
    public static string? Warning(BuildPlatform platform, BuildArchitecture architecture) => Find(platform, architecture)?.Support switch
    {
        ArchitectureSupport.NoNativeLibraries =>
            $"Games for {platform} {ArchitectureName(architecture)} won't start yet: BRUTAL, the library the engine draws with, has " +
            "no window, Vulkan or shader libraries for it. The game is built, but it stops as soon as it opens.",
        ArchitectureSupport.Experimental =>
            $"{platform} {ArchitectureName(architecture)} is experimental: games have hardly been tried there. Try yours on that kind of computer before you ship it.",
        _ => null,
    };
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
