// Run by publish.py: checks that the engine, and the libraries it's built on, have every library they refer to in a published
// player. One that isn't there is in Player/LeftOutOfGames.targets, which games leave out to stay small.
#:property PublishAot=false
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// References nothing in a game reaches: the editor's shader compiler, and attributes for XML that nothing reads.
var unreached = new HashSet<(string, string)> { ("Engine.Brutal.dll", "Brutal.ShaderC"), ("Brutal.Numerics.dll", "System.Xml.ReaderWriter") };

string folder = args[0];
int problems = 0;
foreach (var dll in Directory.GetFiles(folder, "*.dll").Order())
{
    string name = Path.GetFileName(dll);
    if (name.StartsWith("System.") || name.StartsWith("Microsoft.") || name is "mscorlib.dll" or "netstandard.dll" or "WindowsBase.dll") continue;
    using var file = new PEReader(File.OpenRead(dll));
    if (!file.HasMetadata) continue;   // a native library
    var metadata = file.GetMetadataReader();
    foreach (var reference in metadata.AssemblyReferences)
    {
        string library = metadata.GetString(metadata.GetAssemblyReference(reference).Name);
        if (File.Exists(Path.Combine(folder, library + ".dll")) || unreached.Contains((name, library))) continue;
        Console.WriteLine($"{name} uses {library}.dll, which games leave out. Delete it from Player/LeftOutOfGames.targets.");
        problems++;
    }
}
return problems == 0 ? 0 : 1;
