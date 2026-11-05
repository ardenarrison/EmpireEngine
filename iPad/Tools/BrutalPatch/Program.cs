using Mono.Cecil;
using Mono.Cecil.Cil;

// BRUTAL chooses its native libraries with OperatingSystem.IsMacOS() and stops on any system it doesn't know, which includes
// iPadOS. This writes copies of its assemblies for the iPad app, which the desktop builds don't use:
//   - those checks ask IsIOS() instead, so the app takes the macOS path
//   - the macOS library names become the frameworks inside the app, loaded by path
//
// Usage: BrutalPatch <output folder> <assembly>...
var frameworks = new Dictionary<string, string>
{
    ["VulkanEx"] = "@executable_path/Frameworks/MoltenVK.framework/MoltenVK",
    ["shaderc_shared.1"] = "@executable_path/Frameworks/shaderc.framework/shaderc",
};

string output = args[0];
Directory.CreateDirectory(output);

foreach (string path in args.Skip(1))
{
    var resolver = new DefaultAssemblyResolver();
    foreach (string folder in SearchFolders(path)) resolver.AddSearchDirectory(folder);
    using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = false });
    int patched = 0;
    foreach (var module in assembly.Modules)
        foreach (var type in AllTypes(module.Types))
            foreach (var method in type.Methods.Where(m => m.HasBody))
                foreach (var instruction in method.Body.Instructions.ToList())
                {
                    if (instruction.OpCode == OpCodes.Ldstr && frameworks.TryGetValue((string)instruction.Operand, out var framework))
                    {
                        instruction.Operand = framework;
                        patched++;
                    }
                    if (instruction.OpCode != OpCodes.Call || instruction.Operand is not MethodReference called) continue;
                    if (called is { Name: "IsMacOS", DeclaringType.FullName: "System.OperatingSystem" })
                    {
                        instruction.Operand = new MethodReference("IsIOS", called.ReturnType, called.DeclaringType);
                        patched++;
                    }
                    else if (called is { Name: "Load", DeclaringType.FullName: "System.Runtime.InteropServices.NativeLibrary" } && called.Parameters.Count == 3)
                    {
                        // Load(name, assembly, searchPath) searches by name. Drop the last two and call Load(path).
                        var load = new MethodReference("Load", called.ReturnType, called.DeclaringType);
                        load.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
                        var il = method.Body.GetILProcessor();
                        il.InsertBefore(instruction, il.Create(OpCodes.Pop));
                        il.InsertBefore(instruction, il.Create(OpCodes.Pop));
                        instruction.Operand = load;
                        patched++;
                    }
                }
    assembly.Write(Path.Combine(output, Path.GetFileName(path)));
    Console.WriteLine($"{Path.GetFileName(path)}: {patched} changes");
}

// The folder of every BRUTAL package (each assembly's references are in the others) and of .NET itself.
// An assembly is at <packages>/<package>/<version>/lib/<framework>/.
static IEnumerable<string> SearchFolders(string assembly)
{
    string packages = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(assembly)!, "..", "..", "..", ".."));
    return Directory.GetDirectories(packages, "ahwoo.brutal.*")
        .SelectMany(package => Directory.GetDirectories(package))
        .SelectMany(version => Directory.Exists(Path.Combine(version, "lib")) ? Directory.GetDirectories(Path.Combine(version, "lib")) : [])
        .Append(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
}

static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types) =>
    types.SelectMany(type => AllTypes(type.NestedTypes).Prepend(type));
