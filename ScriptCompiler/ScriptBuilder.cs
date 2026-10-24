using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Engine.Scripting.Build;

// Compiles a project's Scripts folder into one DLL with Roslyn. Used by play mode in the Editor
// and by Build Project. The game itself only loads the DLL.
public static class ScriptBuilder
{
    private const string GlobalUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.Linq;
        global using System.Numerics;
        global using Engine.Core.Scene;
        global using Engine.Core.Scripting;
        """;

    // Every framework library the running program uses, plus the engine assemblies next to it
    // (Engine.Brutal and the Brutal Vulkan bindings, so scripts can use Vulkan directly).
    public static List<string> DefaultReferences(string engineAssemblyPath)
    {
        var refs = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        refs.Add(engineAssemblyPath);
        if (Path.GetDirectoryName(Path.GetFullPath(engineAssemblyPath)) is { } engineDir)
            refs.AddRange(Directory.GetFiles(engineDir, "*.dll"));
        return refs;
    }

    // Compiles every .cs file in scriptsFolder into outputDll. Returns false and the errors when compilation fails.
    public static bool Compile(string scriptsFolder, string outputDll, IEnumerable<string> referencePaths, out string errors)
    {
        var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(GlobalUsings, path: "GlobalUsings.cs") };
        if (Directory.Exists(scriptsFolder))
            foreach (var file in Directory.GetFiles(scriptsFolder, "*.cs"))
                trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file));

        var compilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(outputDll),
            trees,
            References(referencePaths),
            // Unsafe is allowed so scripts can call raw Vulkan with pointers. Release optimization: without it the
            // runtime runs scripts unoptimized, several times slower.
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                optimizationLevel: OptimizationLevel.Release));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputDll))!);
        var result = compilation.Emit(outputDll);
        if (!result.Success)
        {
            errors = string.Join("\n", result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString()));
            return false;
        }

        errors = "";
        return true;
    }

    // Compiles source text without writing anything and returns every error and warning. Used by the editor's code
    // view to check scripts as they are typed. Pass the references from LoadReferences, which are slow to load.
    // files maps each script's path to its current text (unsaved text included).
    public static IReadOnlyList<Diagnostic> Check(IReadOnlyDictionary<string, string> files,
        IReadOnlyList<MetadataReference> references, CancellationToken cancel = default)
    {
        var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(GlobalUsings, path: "GlobalUsings.cs", cancellationToken: cancel) };
        foreach (var (path, text) in files)
            trees.Add(CSharpSyntaxTree.ParseText(text, path: path, cancellationToken: cancel));

        var compilation = CSharpCompilation.Create("ScriptCheck", trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetDiagnostics(cancel)
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .ToList();
    }

    // The type names scripts can use (from the references and the scripts themselves), for highlighting.
    public static HashSet<string> TypeNames(IReadOnlyDictionary<string, string> files, IReadOnlyList<MetadataReference> references)
    {
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(f.Value, path: f.Key)).ToList();
        var compilation = CSharpCompilation.Create("ScriptTypes", trees, references);
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Walk(INamespaceSymbol ns)
        {
            foreach (var member in ns.GetMembers())
            {
                if (member is INamespaceSymbol child) Walk(child);
                else if (member is INamedTypeSymbol { DeclaredAccessibility: Accessibility.Public or Accessibility.NotApplicable or Accessibility.Internal } type)
                    names.Add(type.Name);
            }
        }
        // Only the namespaces scripts import, so common words elsewhere in the framework don't light up.
        foreach (var ns in new[] { "System", "System.Collections.Generic", "System.Linq", "System.Numerics", "Engine.Core.Scene", "Engine.Core.Scripting" })
            if (FindNamespace(compilation.GlobalNamespace, ns) is { } found)
                foreach (var type in found.GetTypeMembers()) names.Add(type.Name);
        Walk(compilation.Assembly.GlobalNamespace);
        return names;
    }

    private static INamespaceSymbol? FindNamespace(INamespaceSymbol root, string dotted)
    {
        var current = root;
        foreach (var part in dotted.Split('.'))
        {
            current = current.GetNamespaceMembers().FirstOrDefault(n => n.Name == part);
            if (current is null) return null;
        }
        return current;
    }

    public static List<MetadataReference> LoadReferences(IEnumerable<string> paths) => References(paths);

    private static List<MetadataReference> References(IEnumerable<string> paths)
    {
        var refs = new List<MetadataReference>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
                continue;
            try
            {
                refs.Add(MetadataReference.CreateFromFile(path));
            }
            catch (BadImageFormatException)
            {
                // Native libraries are not references. Skip them.
            }
        }
        return refs;
    }
}
