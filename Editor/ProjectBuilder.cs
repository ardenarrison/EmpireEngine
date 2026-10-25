using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Engine.Core.Serialization;
using Engine.Core.Scripting;
using Engine.Scripting.Build;

namespace Editor;

// Log holds the full output of every command the build ran, so errors can be copied in full.
public sealed record BuildResult(bool Success, string Message, string? OutputFolder, string Log = "");

// Builds a standalone game into <parent>/<buildName>/:
//   <buildName>(.exe) and its runtime libraries: published from Player/ for this machine when the source is here, or
//                     copied from the built Player/ folder that a released editor carries next to it
//   GameData/Core/...   a copy of the project's content; Models/... keeps its folder structure
//   GameData/game.json  which scene the game starts with
public static class ProjectBuilder
{
    public static readonly string LogPath = Path.Combine(Path.GetTempPath(), "empire-build.log");

    // MSBuild variables that a parent `dotnet run` can leave in the environment. Passing them on makes the
    // nested dotnet load a different MSBuild than its own SDK, which breaks restore and publish.
    private static readonly string[] InheritedBuildVariables =
    [
        "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath", "DOTNET_HOST_PATH",
    ];

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static BuildResult Build(string scenePath, string contentRoot, string parentFolder, string buildName)
    {
        try
        {
            return BuildCore(scenePath, contentRoot, parentFolder, buildName);
        }
        catch (Exception ex)
        {
            return Fail(ex.ToString());
        }
    }

    // Removes characters that are not allowed in file names.
    public static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return cleaned.Trim('.', ' ');
    }

    // Text for the clipboard: the message, every line that mentions an error, and the full log.
    public static string ErrorReport(BuildResult result)
    {
        var errors = result.Log.Split('\n').Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase));
        return result.Message +
               "\n\nErrors:\n" + string.Join('\n', errors) +
               "\n\nFull log:\n" + result.Log;
    }

    private static BuildResult BuildCore(string scenePath, string contentRoot, string parentFolder, string buildName)
    {
        string name = SanitizeName(buildName);
        if (name.Length == 0)
            return Fail("The build name is empty.");

        // A build from source has Player/Player.csproj above the editor. A released editor has no source, but carries the
        // player already built (Player/ next to empire.dll), which is used as it is.
        string? playerProject = FindPlayerProject();
        string? prebuiltPlayer = playerProject is null ? FindPrebuiltPlayer() : null;
        if (playerProject is null && prebuiltPlayer is null)
            return Fail("Could not find Player/Player.csproj in a folder above the Editor, or a built Player folder next to it.");

        string content = Path.GetFullPath(contentRoot);
        string scene = Path.GetFullPath(scenePath);
        string outDir = Path.GetFullPath(Path.Combine(parentFolder, name));

        if (!IsInside(scene, content))
            return Fail("The scene must be inside the project's content folder.");
        if (IsInside(outDir, content))
            return Fail("Choose a build folder outside the project's content folder.");

        string rid = RuntimeInformation.RuntimeIdentifier;

        // Build files for the Player go in a temp folder outside the projects, so they never mix with the Editor's obj/bin.
        string artifacts = Path.Combine(Path.GetTempPath(), "empire-player-build");

        string log;
        if (prebuiltPlayer is not null)
        {
            // 1-2. Copy the built player, with its program renamed to the game's name. The program finds its Player.dll by a
            //      path stored inside it, not by its own file name, so renaming it is safe.
            CopyPrebuiltPlayer(prebuiltPlayer, outDir, name);
            log = $"Copied the built player from {prebuiltPlayer} (no .NET SDK needed).";
        }
        else
        {
            string playerDir = Path.GetDirectoryName(playerProject)!;

            // 1. Restore packages for this platform.
            var (restoreExit, restoreLog) = Run("dotnet",
                $"restore \"{playerProject}\" -r {rid} --artifacts-path \"{artifacts}\"", playerDir);
            if (restoreExit != 0)
                return Failed("dotnet restore failed", restoreExit, restoreLog);

            // 2. Publish the Player for this platform, including the runtime.
            var (publishExit, publishLog) = Run("dotnet",
                $"publish \"{playerProject}\" -c Release -r {rid} --self-contained true --no-restore " +
                $"--artifacts-path \"{artifacts}\" -p:GameName=\"{name}\" -o \"{outDir}\"", playerDir);
            log = restoreLog + "\n" + publishLog;
            if (publishExit != 0)
                return Failed("dotnet publish failed", publishExit, log);
        }

        // 3. Publish does not always include the Vulkan and GLFW native libraries. Copy them in from the Editor's
        //    output folder, the NuGet package cache, and (on macOS) the Vulkan loader.
        var copiedNatives = CopyNativeLibraries(AppContext.BaseDirectory, outDir)
            .Concat(CopyNativeLibrariesFromPackages(outDir))
            .ToList();
        log += "\nCopied native libraries: " + (copiedNatives.Count == 0 ? "none" : string.Join(", ", copiedNatives));

        // 3b. macOS games carry their own Vulkan loader and MoltenVK, so players need nothing installed.
        var (vulkanCopied, vulkanWarning) = BundleVulkan(outDir);
        log += "\nBundled Vulkan: " + (vulkanCopied.Count == 0 ? "none" : string.Join(", ", vulkanCopied));
        if (vulkanWarning is not null) log += "\nWARNING: " + vulkanWarning;

        // 4. Copy the project's content into GameData/Core, so Models/x.glb lands in GameData/Core/Models/x.glb.
        string gameData = Path.Combine(outDir, "GameData");
        string core = Path.Combine(gameData, "Core");
        if (Directory.Exists(core))
            Directory.Delete(core, recursive: true);
        CopyDirectory(content, core);

        // 4b. Compile the scripts into GameData/Scripts.dll. The game loads that DLL and never compiles scripts.
        string coreScripts = Path.Combine(core, "Scripts");
        if (Directory.Exists(coreScripts))
            Directory.Delete(coreScripts, recursive: true);
        // The project's enabled packages go next to Scripts.dll, which is compiled against them. Their copies in the
        // content folder aren't needed.
        string corePackages = Path.Combine(core, Packages.ProjectFolder);
        if (Directory.Exists(corePackages))
            Directory.Delete(corePackages, recursive: true);
        foreach (var old in Directory.GetFiles(gameData, "EmpirePackage.*.dll"))
            File.Delete(old);
        var (packageDlls, packageErrors) = Packages.Build(content, typeof(Behaviour).Assembly.Location);
        if (packageErrors.Length > 0)
        {
            File.WriteAllText(LogPath, packageErrors);
            return new BuildResult(false, "A package did not compile. Copy the errors.", null, packageErrors);
        }
        var shippedPackages = new List<string>();
        foreach (var packageDll in packageDlls)
        {
            string target = Path.Combine(gameData, Path.GetFileName(packageDll));
            File.Copy(packageDll, target, overwrite: true);
            shippedPackages.Add(target);
        }
        log += "\nPackages: " + (shippedPackages.Count == 0 ? "none" : string.Join(", ", shippedPackages.Select(Path.GetFileName)));

        string scriptsDll = Path.Combine(gameData, "Scripts.dll");
        if (!ScriptBuilder.Compile(Path.Combine(content, "Scripts"), scriptsDll,
                Directory.GetFiles(outDir, "*.dll").Where(f => !IsNativeLibrary(f)).Concat(shippedPackages), out string scriptErrors))
        {
            File.WriteAllText(LogPath, scriptErrors);
            return new BuildResult(false, "The scripts did not compile. Copy the errors and fix them.", null, scriptErrors);
        }

        // 5. Record which scene starts the game.
        string startScene = Path.GetRelativePath(content, scene).Replace('\\', '/');
        File.WriteAllText(Path.Combine(gameData, "game.json"),
            JsonSerializer.Serialize(new GameManifest(name, startScene), Options));

        File.WriteAllText(LogPath, log);
        string message = $"Built \"{name}\" to:\n{outDir}";
        if (vulkanWarning is not null) message += "\n\nWarning: " + vulkanWarning;
        return new BuildResult(true, message, outDir, log);
    }

    private static string? FindPlayerProject()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Player", "Player.csproj");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // The built player a released editor carries: Player/ next to empire.dll, holding the Player program and Player.dll.
    private static string? FindPrebuiltPlayer()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "Player");
        bool hasProgram = File.Exists(Path.Combine(folder, "Player")) || File.Exists(Path.Combine(folder, "Player.exe"));
        return hasProgram && File.Exists(Path.Combine(folder, "Player.dll")) ? folder : null;
    }

    // Copies the built player into the game's folder, with Player/Player.exe named after the game.
    private static void CopyPrebuiltPlayer(string from, string to, string gameName)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            string fileName = Path.GetFileName(file);
            if (fileName.StartsWith('.')) continue;
            bool isProgram = fileName is "Player" or "Player.exe";
            string target = Path.Combine(to, isProgram ? gameName + Path.GetExtension(fileName) : fileName);
            File.Copy(file, target, overwrite: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(target, File.GetUnixFileMode(file));
        }
        foreach (var dir in Directory.GetDirectories(from))
        {
            string dirName = Path.GetFileName(dir);
            if (!dirName.StartsWith('.'))
                CopyDirectory(dir, Path.Combine(to, dirName));
        }
    }

    private static (int ExitCode, string Output) Run(string fileName, string arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var variable in InheritedBuildVariables)
            psi.Environment.Remove(variable);
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        using var process = Process.Start(psi);
        if (process is null)
            return (-1, "Could not start dotnet. Check that the .NET SDK is installed and on your PATH.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            string fileName = Path.GetFileName(file);
            if (fileName.StartsWith('.')) continue;
            File.Copy(file, Path.Combine(to, fileName), overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(from))
        {
            string dirName = Path.GetFileName(dir);
            if (dirName.StartsWith('.')) continue;
            CopyDirectory(dir, Path.Combine(to, dirName));
        }
    }

    // Copies native libraries that are not already in the build. Managed assemblies are skipped, since publish supplies them.
    private static List<string> CopyNativeLibraries(string fromFolder, string toFolder)
    {
        var copied = new List<string>();
        foreach (var file in Directory.GetFiles(fromFolder))
        {
            string name = Path.GetFileName(file);
            string target = Path.Combine(toFolder, name);
            if (File.Exists(target) || !IsNativeLibrary(file)) continue;
            File.Copy(file, target);
            copied.Add(name);
        }
        return copied;
    }

    // The Vulkan and GLFW natives come from the NuGet package cache, where the Editor loads them from.
    // Copy the ones for this platform.
    private static List<string> CopyNativeLibrariesFromPackages(string toFolder)
    {
        var copied = new List<string>();
        string packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        if (!Directory.Exists(packages)) return copied;

        string rid = RuntimeInformation.RuntimeIdentifier;
        foreach (var package in Directory.EnumerateDirectories(packages))
            foreach (var version in Directory.EnumerateDirectories(package))
            {
                string nativeDir = Path.Combine(version, "runtimes", rid, "native");
                if (Directory.Exists(nativeDir))
                    copied.AddRange(CopyNativeLibraries(nativeDir, toFolder));
            }
        return copied;
    }

    // macOS needs the Vulkan loader (libvulkan.1.dylib). Copy it from Homebrew or /usr/local when it is missing.
    private static (List<string> Copied, string? Warning) BundleVulkan(string toFolder)
    {
        var copied = new List<string>();
        if (!OperatingSystem.IsMacOS()) return (copied, null);

        // The editor's own copies come first: they are what the editor itself runs on, and they exist even where
        // Homebrew and the Vulkan SDK do not. The system's copies are only a fallback.
        string editorDir = AppContext.BaseDirectory;
        string rid = RuntimeInformation.RuntimeIdentifier;
        string loaderTarget = Path.Combine(toFolder, "libvulkan.1.dylib");
        if (!File.Exists(loaderTarget))
        {
            var loaderSources = new[]
            {
                Path.Combine(editorDir, "libvulkan.1.dylib"),
                Path.Combine(editorDir, "runtimes", rid, "native", "libvulkan.1.dylib"),
                "/opt/homebrew/lib/libvulkan.1.dylib",
                "/usr/local/lib/libvulkan.1.dylib",
            };
            if (loaderSources.FirstOrDefault(File.Exists) is { } loader)
            {
                File.Copy(loader, loaderTarget);   // follows symlinks, so the real library is copied
                copied.Add("libvulkan.1.dylib");
            }
        }

        string driverTarget = Path.Combine(toFolder, "vulkan", "icd.d");
        string bundledDrivers = Path.Combine(editorDir, "vulkan");
        // The driver's folder is icd.d, or icd in the Mac editor app (a folder named *.d inside an app can't be code signed). A game
        // keeps whichever name it was given; the game finds either.
        if (HasDriver(bundledDrivers))
        {
            CopyDirectory(bundledDrivers, Path.Combine(toFolder, "vulkan"));
            copied.Add("vulkan (MoltenVK)");
        }
        else if (new[] { "/opt/homebrew/lib/libMoltenVK.dylib", "/usr/local/lib/libMoltenVK.dylib" }.FirstOrDefault(File.Exists) is { } molten)
        {
            Directory.CreateDirectory(driverTarget);
            File.Copy(molten, Path.Combine(driverTarget, "libMoltenVK.dylib"), overwrite: true);
            File.WriteAllText(Path.Combine(driverTarget, "MoltenVK_icd.json"),
                "{\n  \"file_format_version\": \"1.0.0\",\n  \"ICD\": {\n    \"library_path\": \"./libMoltenVK.dylib\",\n" +
                "    \"api_version\": \"1.4.0\",\n    \"is_portability_driver\": true\n  }\n}\n");
            copied.Add("vulkan/icd.d (MoltenVK, from this Mac)");
        }

        bool haveLoader = File.Exists(loaderTarget);
        bool haveDriver = HasDriver(Path.Combine(toFolder, "vulkan"));
        if (haveLoader && haveDriver) return (copied, null);

        string missing = !haveLoader && !haveDriver ? "the Vulkan loader and MoltenVK" : !haveLoader ? "the Vulkan loader" : "MoltenVK";
        return (copied, $"{missing} could not be included, so this build will not start on a Mac without them installed. " +
                        "Build the editor with the Vulkan loader and MoltenVK available (Homebrew: vulkan-loader and molten-vk, " +
                        "or the Vulkan SDK) so it can bundle them.");
    }

    // Whether a vulkan folder holds MoltenVK's driver manifest, in icd.d or icd.
    private static bool HasDriver(string vulkanFolder) =>
        new[] { "icd.d", "icd" }.Any(folder => File.Exists(Path.Combine(vulkanFolder, folder, "MoltenVK_icd.json")));

    private static bool IsNativeLibrary(string file)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext is ".dylib" or ".so") return true;
        if (ext != ".dll") return false;
        try
        {
            AssemblyName.GetAssemblyName(file);   // managed assemblies parse; native DLLs do not
            return false;
        }
        catch (BadImageFormatException)
        {
            return true;
        }
    }

    private static bool IsInside(string path, string folder)
    {
        string p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        string f = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    private static BuildResult Failed(string step, int exitCode, string log)
    {
        File.WriteAllText(LogPath, log);
        return new BuildResult(false, $"{step} (exit code {exitCode}).", null, log);
    }

    private static BuildResult Fail(string message) => new(false, message, null);
}
