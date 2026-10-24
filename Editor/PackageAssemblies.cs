using System.Reflection;
using System.Security.Cryptography;

namespace Editor;

// Loads the enabled packages' DLLs for play mode. Each DLL is read into memory first, so the file stays free to be
// rebuilt (Windows would lock a DLL loaded from its path). A DLL whose bytes haven't changed is reused from the last
// play. Scripts that use a package find it by name through the AssemblyResolve hook.
public static class PackageAssemblies
{
    private static readonly Dictionary<string, (string Hash, Assembly Assembly)> Loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Assembly> ByName = new(StringComparer.Ordinal);
    private static bool _hooked;

    public static List<Assembly> Load(IEnumerable<string> dlls)
    {
        if (!_hooked)
        {
            _hooked = true;
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
                ByName.GetValueOrDefault(new AssemblyName(args.Name).Name ?? "");
        }

        var result = new List<Assembly>();
        foreach (var path in dlls)
        {
            byte[] bytes = File.ReadAllBytes(path);
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!Loaded.TryGetValue(path, out var entry) || entry.Hash != hash)
            {
                entry = (hash, Assembly.Load(bytes));
                Loaded[path] = entry;
                ByName[entry.Assembly.GetName().Name ?? ""] = entry.Assembly;
            }
            result.Add(entry.Assembly);
        }
        return result;
    }
}
