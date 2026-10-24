using System.Reflection;
using System.Runtime.Loader;
using Engine.Core.Scene;
using Engine.Core.Scripting;
using Engine.Scripting.Build;

namespace Editor;

// A class the Inspector can attach: a Behaviour script, or a component (a class that extends Component) from the
// project's scripts or from an enabled package. Package is the package's id, or null for the project's own scripts.
public sealed record ScriptClass(string Name, bool IsComponent, string? Package, IReadOnlyList<ScriptFieldInfo> Fields);

// The public fields of every script in the Scripts folder and every enabled package, so the Inspector can show them
// while editing, and the components they define, for Add Component. When a .cs file or the project's packages change,
// the enabled packages are built and the scripts compiled again in the background; the new assemblies are read in a
// collectible load context, which is unloaded right after so nothing stays locked or loaded.
public sealed class ScriptFieldCache
{
    private readonly string _projectRoot;
    private readonly string _scriptsFolder;
    private Dictionary<string, ScriptClass> _classes = new();
    private string _signature = "";
    private Task<(Dictionary<string, ScriptClass>? Classes, string Errors)>? _compile;
    private string _pendingSignature = "";
    private double _nextCheck;

    public ScriptFieldCache(string projectRoot)
    {
        _projectRoot = projectRoot;
        _scriptsFolder = Path.Combine(projectRoot, "Scripts");
    }

    public bool IsCompiling => _compile is not null;
    public string Errors { get; private set; } = "";

    // Every class from the last good compile.
    public IEnumerable<ScriptClass> Classes => _classes.Values;

    // The class with this name, or null when there is none (or before the first compile has finished).
    public ScriptClass? Find(string name) => _classes.GetValueOrDefault(name);

    // The script's editable fields, or null before the first compile has finished.
    public IReadOnlyList<ScriptFieldInfo>? For(string scriptName)
    {
        if (_classes.TryGetValue(scriptName, out var found)) return found.Fields;
        return _signature.Length == 0 ? null : Array.Empty<ScriptFieldInfo>();
    }

    // Compiles again on the next check, for example right after a package was turned on or off.
    public void Invalidate()
    {
        _signature = "changed";
        _nextCheck = 0;
    }

    // Call once per frame. Checks the folder at most twice a second and picks up finished compiles.
    public void Update(double now)
    {
        if (_compile is { IsCompleted: true } done)
        {
            var (classes, errors) = done.Result;
            if (classes is not null) _classes = classes;   // on errors, keep the last good fields
            Errors = errors;
            _signature = _pendingSignature;
            _compile = null;
        }

        if (_compile is not null || now < _nextCheck) return;
        _nextCheck = now + 0.5;

        string signature = Signature();
        if (signature == _signature) return;
        _pendingSignature = signature;
        _compile = Task.Run(Compile);
    }

    // Changes whenever a script is added, removed, renamed or saved, or the project's packages change.
    private string Signature()
    {
        string scripts = Directory.Exists(_scriptsFolder)
            ? string.Join("|", Directory.GetFiles(_scriptsFolder, "*.cs").OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => $"{Path.GetFileName(f)}:{File.GetLastWriteTimeUtc(f).Ticks}"))
            : "none";
        return scripts + "|packages:" + string.Join(",", Packages.Enabled(_projectRoot));
    }

    private (Dictionary<string, ScriptClass>?, string) Compile()
    {
        string dll = Path.Combine(Path.GetTempPath(), "empire-inspector", Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            string engine = typeof(Behaviour).Assembly.Location;
            var (packageDlls, packageErrors) = Packages.Build(_projectRoot, engine);
            var references = ScriptBuilder.DefaultReferences(engine);
            references.AddRange(packageDlls);
            if (!ScriptBuilder.Compile(_scriptsFolder, dll, references, out string errors))
                return (null, packageErrors + errors);
            return (Read(File.ReadAllBytes(dll), packageDlls), packageErrors);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
        finally
        {
            try { File.Delete(dll); } catch (IOException) { }
        }
    }

    private Dictionary<string, ScriptClass> Read(byte[] scripts, IReadOnlyList<string> packageDlls)
    {
        var context = new AssemblyLoadContext("inspector-scripts", isCollectible: true);
        try
        {
            // Engine assemblies resolve from the editor's own context, so Behaviour and Component are the same types here.
            var packages = new List<(string Id, Assembly Assembly)>();
            foreach (var path in packageDlls)
            {
                var assembly = context.LoadFromStream(new MemoryStream(File.ReadAllBytes(path)));
                packages.Add((Path.GetFileNameWithoutExtension(path)["EmpirePackage.".Length..], assembly));
            }
            var result = new Dictionary<string, ScriptClass>();
            // Packages first, so a project script with the same name replaces the package's class, as in play mode.
            foreach (var (id, assembly) in packages)
                AddClasses(result, assembly, id);
            AddClasses(result, context.LoadFromStream(new MemoryStream(scripts)), null);
            return result;
        }
        finally
        {
            context.Unload();
        }
    }

    private static void AddClasses(Dictionary<string, ScriptClass> result, Assembly assembly, string? package)
    {
        foreach (var type in SafeTypes(assembly))
        {
            if (type.IsAbstract || type.IsNested) continue;
            bool behaviour = typeof(Behaviour).IsAssignableFrom(type);
            bool component = typeof(Component).IsAssignableFrom(type);
            if (behaviour || component)
                result[type.Name] = new ScriptClass(type.Name, component, package, ScriptFields.Describe(type));
        }
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>(); }
    }
}
