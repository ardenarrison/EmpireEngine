using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Engine.Scripting.Build;

// A package that comes with the editor: a folder of C# scripts plus package.json. Packages are written exactly like a
// project's own scripts (usually as classes that extend Component, so they add new components); nothing in them is
// part of the engine.
public sealed record PackageInfo(string Id, string Name, string Version, string Description, string Folder);

// Packages for a project. The editor ships them in its Packages folder; a project turns them on in
// <project>/Packages/packages.json, and each enabled package is compiled into <project>/Packages/EmpirePackage.<Id>.dll.
// The project's scripts are compiled against those DLLs, play mode and the built game load them, and builds copy them
// next to Scripts.dll. Every package starts turned off.
public static class Packages
{
    public const string ProjectFolder = "Packages";
    public const string ManifestFile = "packages.json";
    private const string DllPrefix = "EmpirePackage.";

    private static readonly object Gate = new();   // the editor compiles from more than one thread

    // Where the editor's packages are: <editor>/Packages.
    public static string DefaultRoot => Path.Combine(AppContext.BaseDirectory, "Packages");

    // The packages in a folder of packages (one subfolder each, with package.json), sorted by name.
    public static IReadOnlyList<PackageInfo> Available(string? root = null)
    {
        root ??= DefaultRoot;
        var list = new List<PackageInfo>();
        if (!Directory.Exists(root)) return list;
        foreach (var folder in Directory.GetDirectories(root))
        {
            string manifest = Path.Combine(folder, "package.json");
            if (!File.Exists(manifest)) continue;
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(manifest));
                string id = node?["id"]?.GetValue<string>() ?? Path.GetFileName(folder);
                if (!IsValidId(id)) continue;
                list.Add(new PackageInfo(id,
                    node?["name"]?.GetValue<string>() ?? id,
                    node?["version"]?.GetValue<string>() ?? "1.0.0",
                    node?["description"]?.GetValue<string>() ?? "",
                    folder));
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
            {
                Console.WriteLine($"Could not read the package in {folder}: {ex.Message}");
            }
        }
        return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Letters, digits and underscores, so an id is always a safe file name.
    private static bool IsValidId(string id) => id.Length > 0 && id.All(c => char.IsLetterOrDigit(c) || c == '_');

    // The ids of the packages a project has turned on.
    public static List<string> Enabled(string projectRoot)
    {
        string file = Path.Combine(projectRoot, ProjectFolder, ManifestFile);
        try
        {
            if (!File.Exists(file)) return new();
            var node = JsonNode.Parse(File.ReadAllText(file));
            return node?["enabled"]?.AsArray().Select(n => n?.GetValue<string>() ?? "").Where(IsValidId)
                       .Distinct(StringComparer.Ordinal).ToList() ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            Console.WriteLine($"Could not read {file}: {ex.Message}");
            return new();
        }
    }

    // Turns a package on or off for a project. Turning one off deletes its DLL; objects that use its components keep
    // their settings and work again when it is turned back on.
    public static void SetEnabled(string projectRoot, string id, bool enabled)
    {
        lock (Gate)
        {
            var list = Enabled(projectRoot);
            list.Remove(id);
            if (enabled) list.Add(id);
            string folder = Path.Combine(projectRoot, ProjectFolder);
            Directory.CreateDirectory(folder);
            var json = new JsonObject { ["enabled"] = new JsonArray(list.Select(i => (JsonNode)i).ToArray()) };
            File.WriteAllText(Path.Combine(folder, ManifestFile), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (!enabled)
            {
                TryDelete(DllPath(projectRoot, id));
                TryDelete(DllPath(projectRoot, id) + ".stamp");
            }
        }
    }

    // Where an enabled package's compiled DLL lives in the project.
    public static string DllPath(string projectRoot, string id) => Path.Combine(projectRoot, ProjectFolder, DllPrefix + id + ".dll");

    // Compiles each enabled package whose DLL is missing or out of date (its sources, its version or the engine
    // changed). A package the editor doesn't have any more keeps the DLL already in the project. Returns the DLLs to
    // load and reference, in the order the packages were turned on, and any compile errors.
    // engineAssembly is the path of Engine.Core.dll, the same one scripts are compiled against.
    public static (List<string> Dlls, string Errors) Build(string projectRoot, string engineAssembly, string? packagesRoot = null)
    {
        lock (Gate)
        {
            var available = Available(packagesRoot).ToDictionary(p => p.Id, StringComparer.Ordinal);
            var references = ScriptBuilder.DefaultReferences(engineAssembly);
            string engine = File.Exists(engineAssembly)
                ? $"{new FileInfo(engineAssembly).Length}:{File.GetLastWriteTimeUtc(engineAssembly).Ticks}"
                : "";
            var dlls = new List<string>();
            var errors = new StringBuilder();
            foreach (var id in Enabled(projectRoot))
            {
                string dll = DllPath(projectRoot, id);
                if (!available.TryGetValue(id, out var package))
                {
                    if (File.Exists(dll)) dlls.Add(dll);
                    else errors.AppendLine($"Package '{id}' is turned on but this editor doesn't have it.");
                    continue;
                }

                string stamp = Stamp(package, engine);
                string stampFile = dll + ".stamp";
                bool upToDate = File.Exists(dll) && File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp;
                if (!upToDate)
                {
                    // Write to a new file and move it in, so a half-written DLL is never left behind.
                    string temp = dll + ".new";
                    if (!ScriptBuilder.Compile(package.Folder, temp, references, out string compileErrors))
                    {
                        errors.AppendLine($"Package '{package.Name}' did not compile:\n{compileErrors}");
                        TryDelete(temp);
                        continue;
                    }
                    File.Move(temp, dll, overwrite: true);
                    File.WriteAllText(stampFile, stamp);
                }
                dlls.Add(dll);
                references.Add(dll);   // a later package may use an earlier one
            }
            return (dlls, errors.ToString());
        }
    }

    // What a package's DLL was built from: its version, its sources and the engine it was compiled against.
    private static string Stamp(PackageInfo package, string engine)
    {
        using var sha = SHA256.Create();
        var text = new StringBuilder(package.Version).Append('|').Append(engine).Append('|');
        foreach (var file in Directory.GetFiles(package.Folder, "*.cs").OrderBy(f => f, StringComparer.Ordinal))
            text.Append(Path.GetFileName(file)).Append(':').Append(Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(file)))).Append('|');
        return text.ToString();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
