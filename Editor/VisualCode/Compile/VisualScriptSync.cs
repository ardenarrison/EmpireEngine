using System.Text;
using VisualCode.Model;
using VisualCode.Transpiler;

namespace VisualCode.Compile;

// Before play mode compiles the Scripts folder, turns each .visualcode file into a .cs file next to it.
public static class VisualScriptSync
{
    // Returns the errors, one per line, or an empty string. A script with errors has no .cs file until they are fixed.
    public static string Sync(string scriptsFolder)
    {
        if (!Directory.Exists(scriptsFolder)) return "";

        var errors = new StringBuilder();
        var transpiler = new VcTranspiler();
        foreach (var file in Directory.GetFiles(scriptsFolder, "*.visualcode"))
        {
            var className = Code.Identifier(Path.GetFileNameWithoutExtension(file));
            var outPath = Path.Combine(scriptsFolder, className + ".cs");
            try
            {
                var result = transpiler.Transpile(VcGraph.Load(File.ReadAllText(file)), className);
                foreach (var d in result.Diagnostics) Console.WriteLine($"{Path.GetFileName(file)}: {d}");
                if (result.HasErrors)
                {
                    Console.WriteLine($"{Path.GetFileName(file)}: transpile failed.");
                    foreach (var d in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                        errors.AppendLine($"{Path.GetFileName(file)}: {d.Message}");
                    if (File.Exists(outPath)) File.Delete(outPath);
                    continue;
                }
                File.WriteAllText(outPath, result.Code);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{Path.GetFileName(file)}: {ex.Message}");
                errors.AppendLine($"{Path.GetFileName(file)}: {ex.Message}");
                if (File.Exists(outPath)) File.Delete(outPath);
            }
        }
        return errors.ToString();
    }
}
