using System.Text.Json;
using System.Text.Json.Nodes;
using VisualIdentifier = VisualCode.Transpiler.Code;

namespace Editor;

// Creating, renaming, copying and moving the files in a project. Scenes, prefabs and project.empire hold paths from the project
// folder, and objects hold their scripts' class names, so what points at a file follows it. Scripts that name a file in their
// own code aren't changed.
public static class ProjectFiles
{
    public const string ScriptsFolder = "Scripts";

    // A file or folder that moved, as paths from the project folder. A script that changed name also has its old and new class.
    public sealed record Moved(string OldPath, string NewPath, string? OldClass = null, string? NewClass = null);

    private static readonly string[] FixedFolders = [ScriptsFolder, "Scenes", "Packages", "Generated"];
    private static readonly string[] FixedFiles = ["project.empire", "project-settings.json"];
    private static readonly JsonSerializerOptions Written = new() { WriteIndented = true };

    public static bool IsScript(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".visualcode";

    // What is wrong with a name for a file or folder, or null when it is fine.
    public static string? NameProblem(string name)
    {
        if (name.Trim().Length == 0) return "Type a name.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            return "The name can't contain / \\ : * ? \" < > |";
        if (name.StartsWith('.')) return "The name can't start with a dot.";
        return null;
    }

    public static string? FolderProblem(string folder, string name) =>
        NameProblem(name) ?? (Exists(Path.Combine(folder, name.Trim())) ? $"{name.Trim()} already exists here." : null);

    public static string CreateFolder(string folder, string name)
    {
        string path = Path.Combine(folder, name.Trim());
        Directory.CreateDirectory(path);
        return path;
    }

    // A text file's name: what was typed, with .txt if it has no extension of its own.
    public static string TextFileName(string typed)
    {
        string name = typed.Trim();
        return Path.HasExtension(name) ? name : name + ".txt";
    }

    public static string? TextFileProblem(string folder, string typed) => FolderProblem(folder, TextFileName(typed));

    public static string CreateTextFile(string folder, string typed)
    {
        string path = Path.Combine(folder, TextFileName(typed));
        File.WriteAllText(path, "");
        return path;
    }

    public static string? ScriptProblem(string root, string typed)
    {
        string name = ScriptFiles.ToClassName(typed);
        if (name.Length == 0) return "Type a name, with at least one letter.";
        return ScriptExists(root, name) ? $"A script called {name} already exists." : null;
    }

    // Why this can't be renamed or moved, or null when it can. A project needs these where they are, under these names.
    public static string? WhyFixed(string root, string path)
    {
        string relative = Relative(root, path);
        if (FixedFolders.Contains(relative, StringComparer.OrdinalIgnoreCase) || FixedFiles.Contains(relative, StringComparer.OrdinalIgnoreCase))
            return $"The project needs {relative} where it is.";
        if (Under(relative, "Packages") || Under(relative, "Generated"))
            return $"{Path.GetFileName(path)} is managed by the editor.";
        return IsGenerated(path) ? $"{Path.GetFileName(path)} is generated from a VC script. Change that instead." : null;
    }

    // Why this can't be copied, or null when it can.
    public static string? WhyNotCopied(string root, string path)
    {
        string relative = Relative(root, path);
        if (FixedFolders.Contains(relative, StringComparer.OrdinalIgnoreCase)) return $"The project only has one {relative} folder.";
        return IsGenerated(path) ? $"{Path.GetFileName(path)} is generated from a VC script. Copy that instead." : null;
    }

    // A file's new full path for a typed name. Only the name changes: a file keeps its extension, and a script's name is a class name.
    public static string RenameTarget(string path, string typed)
    {
        string folder = Path.GetDirectoryName(path)!;
        string name = typed.Trim();
        if (Directory.Exists(path)) return Path.Combine(folder, name);
        string extension = Path.GetExtension(path);
        if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) name = name[..^extension.Length];
        if (IsScript(path)) name = ScriptFiles.ToClassName(name);
        return Path.Combine(folder, name + extension);
    }

    public static string? RenameProblem(string root, string path, string typed)
    {
        if (WhyFixed(root, path) is { } fixedReason) return fixedReason;
        string target = RenameTarget(path, typed);
        string name = Directory.Exists(path) ? Path.GetFileName(target) : Path.GetFileNameWithoutExtension(target);
        if (NameProblem(name) is { } problem) return problem;
        bool sameFile = string.Equals(target, path, StringComparison.OrdinalIgnoreCase);   // only the capitals are changing
        if (!sameFile && Exists(target)) return $"{Path.GetFileName(target)} already exists here.";
        return IsScript(path) && !sameFile && ScriptExists(root, Path.GetFileNameWithoutExtension(target))
            ? $"A script called {Path.GetFileNameWithoutExtension(target)} already exists."
            : null;
    }

    public static Moved Rename(string root, string path, string typed)
    {
        string target = RenameTarget(path, typed);
        var moved = new Moved(Relative(root, path), Relative(root, target));
        if (target == path) return moved;
        if (IsScript(path))
            moved = moved with { OldClass = ClassOf(path), NewClass = ClassOf(target) };

        if (Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            // The class name has to match the file name, so the declaration is renamed with it.
            string text = ScriptFiles.RenameClass(File.ReadAllText(path), moved.OldClass!, moved.NewClass!);
            File.Delete(path);
            File.WriteAllText(target, text);
        }
        else
        {
            MoveEntry(path, target);
            if (Path.GetExtension(path).Equals(".visualcode", StringComparison.OrdinalIgnoreCase))
                File.Delete(Path.Combine(Path.GetDirectoryName(path)!, moved.OldClass + ".cs"));   // made again under the new name
        }
        UpdateProject(root, moved);
        return moved;
    }

    // Why this can't be deleted, or null when it can. The scene that is open has to stay until another one is.
    public static string? DeleteProblem(string root, string path, string openScene)
    {
        if (WhyFixed(root, path) is { } reason) return reason;
        bool holdsScene = string.Equals(path, openScene, StringComparison.OrdinalIgnoreCase) || Under(Relative(root, openScene), Relative(root, path));
        return holdsScene ? $"{Relative(root, openScene)} is open, so it can't be deleted." : null;
    }

    // Whether a scene, prefab or project file's text points at this file or something in this folder, or at its script.
    public static bool Uses(string json, string root, string path)
    {
        string relative = Relative(root, path);
        string? scriptClass = IsScript(path) ? ClassOf(path) : null;
        bool found = false;
        Rewrite(json, (key, value) =>
        {
            found |= key == "scriptName" ? value == scriptClass : MovedPath(value, relative, relative) is not null;
            return null;
        });
        return found;
    }

    // The scenes, prefabs and project file that point at this, as paths from the project folder.
    public static List<string> UsedBy(string root, string path) =>
        ProjectJsonFiles(root)
            .Where(file => !string.Equals(file, path, StringComparison.OrdinalIgnoreCase) && !Under(Relative(root, file), Relative(root, path)))
            .Where(file => Uses(File.ReadAllText(file), root, path))
            .Select(file => Relative(root, file))
            .ToList();

    public static void Delete(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
            return;
        }
        File.Delete(path);
        if (Path.GetExtension(path).Equals(".visualcode", StringComparison.OrdinalIgnoreCase))
            File.Delete(Path.Combine(Path.GetDirectoryName(path)!, ClassOf(path) + ".cs"));
    }

    // Why this can't be pasted into the folder, or null when it can.
    public static string? PasteProblem(string root, string source, string folder, bool cut)
    {
        if (!Exists(source)) return $"{Path.GetFileName(source)} isn't there any more.";
        if ((cut ? WhyFixed(root, source) : WhyNotCopied(root, source)) is { } reason) return reason;

        string into = Relative(root, folder);
        if (Under(into, "Packages") || Under(into, "Generated") || into.Equals("Packages", StringComparison.OrdinalIgnoreCase) || into.Equals("Generated", StringComparison.OrdinalIgnoreCase))
            return $"{into} is managed by the editor.";
        bool intoScripts = into.Equals(ScriptsFolder, StringComparison.OrdinalIgnoreCase);
        if (IsScript(source) != intoScripts)
            return IsScript(source) ? "Scripts only run from the Scripts folder." : "A built game leaves out the Scripts folder, so only scripts go there.";
        if (Directory.Exists(source) && (string.Equals(folder, source, StringComparison.OrdinalIgnoreCase) || Under(into, Relative(root, source))))
            return "A folder can't go inside itself.";
        if (cut)
        {
            string target = Path.Combine(folder, Path.GetFileName(source));
            if (string.Equals(target, source, StringComparison.OrdinalIgnoreCase)) return $"{Path.GetFileName(source)} is already here.";
            if (Exists(target)) return $"{Path.GetFileName(source)} already exists here.";
        }
        return null;
    }

    // Moves source into the folder, or makes a copy there under a name that isn't taken. Returns what moved, or null for a copy,
    // which nothing points at yet, and the new file's full path.
    public static (Moved? Moved, string Path) Paste(string root, string source, string folder, bool cut)
    {
        if (cut)
        {
            string target = Path.Combine(folder, Path.GetFileName(source));
            MoveEntry(source, target);
            var moved = new Moved(Relative(root, source), Relative(root, target));
            UpdateProject(root, moved);
            return (moved, target);
        }

        string copy = CopyName(root, source, folder);
        if (Directory.Exists(source))
            CopyFolder(source, copy);
        else if (Path.GetExtension(source).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            File.WriteAllText(copy, ScriptFiles.RenameClass(File.ReadAllText(source), ClassOf(source), ClassOf(copy)));   // two classes with one name wouldn't compile
        else
            File.Copy(source, copy);
        return (null, copy);
    }

    // A scene, prefab or project file's text with what moved followed: its paths, and the class of a script that changed name.
    public static string Rewrite(string json, Moved moved) => Rewrite(json, (key, value) =>
        key == "scriptName" && moved.OldClass is not null
            ? (value == moved.OldClass ? moved.NewClass : null)
            : MovedPath(value, moved.OldPath, moved.NewPath));

    // A path after a file or folder moved, or null when the move doesn't touch it. A script's text field holds its value as JSON.
    public static string? MovedPath(string value, string oldPath, string newPath)
    {
        if (value == oldPath) return newPath;
        if (value.StartsWith(oldPath + "/", StringComparison.Ordinal)) return newPath + value[oldPath.Length..];
        return value == JsonSerializer.Serialize(oldPath) ? JsonSerializer.Serialize(newPath) : null;
    }

    private static string Rewrite(string json, Func<string?, string, string?> change)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return json; }   // not ours to fix, and nothing in it can be followed
        return root is not null && Visit(root, null, change) ? root.ToJsonString(Written) : json;
    }

    private static bool Visit(JsonNode node, string? key, Func<string?, string, string?> change)
    {
        bool changed = false;
        if (node is JsonObject obj)
            foreach (var (name, value) in obj.ToList())
            {
                if (value is null) continue;
                if (value is JsonValue && value.GetValueKind() == JsonValueKind.String && change(name, value.GetValue<string>()) is { } text)
                {
                    obj[name] = text;
                    changed = true;
                }
                else changed |= Visit(value, name, change);
            }
        else if (node is JsonArray array)
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is not { } item) continue;
                if (item is JsonValue && item.GetValueKind() == JsonValueKind.String && change(key, item.GetValue<string>()) is { } text)
                {
                    array[i] = text;
                    changed = true;
                }
                else changed |= Visit(item, key, change);
            }
        return changed;
    }

    // Every scene and prefab in the project, and project.empire, follow what moved.
    private static void UpdateProject(string root, Moved moved)
    {
        foreach (string file in ProjectJsonFiles(root))
        {
            string text = File.ReadAllText(file);
            string changed = Rewrite(text, moved);
            if (changed != text) File.WriteAllText(file, changed);
        }
    }

    private static IEnumerable<string> ProjectJsonFiles(string folder)
    {
        foreach (string file in Directory.GetFiles(folder))
            if (Path.GetExtension(file).ToLowerInvariant() is ".empirescene" or ".pre" || Path.GetFileName(file) == "project.empire")
                yield return file;
        foreach (string child in Directory.GetDirectories(folder))
            if (!Path.GetFileName(child).StartsWith('.') && Path.GetFileName(child) is not ("bin" or "obj"))
                foreach (string file in ProjectJsonFiles(child))
                    yield return file;
    }

    // "Rock copy.glb", then "Rock copy 2.glb". A script is a class name, so it has no spaces: "PlayerCopy", then "PlayerCopy2".
    private static string CopyName(string root, string source, string folder)
    {
        string extension = Directory.Exists(source) ? "" : Path.GetExtension(source);
        string name = Directory.Exists(source) ? Path.GetFileName(source) : Path.GetFileNameWithoutExtension(source);
        bool script = IsScript(source);
        string Named(int n) => Path.Combine(folder, (script ? $"{ScriptFiles.ToClassName(name)}Copy" : $"{name} copy") + (n == 1 ? "" : script ? $"{n}" : $" {n}") + extension);
        for (int n = 1; ; n++)
            if (!Exists(Named(n)) && !(script && ScriptExists(root, Path.GetFileNameWithoutExtension(Named(n)))))
                return Named(n);
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
            if (!Path.GetFileName(file).StartsWith('.'))
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (string folder in Directory.GetDirectories(from))
            if (!Path.GetFileName(folder).StartsWith('.'))
                CopyFolder(folder, Path.Combine(to, Path.GetFileName(folder)));
    }

    private static void MoveEntry(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }

    // The class a script file holds: a visual script's name is made into one when it is compiled.
    private static string ClassOf(string path) =>
        Path.GetExtension(path).Equals(".visualcode", StringComparison.OrdinalIgnoreCase)
            ? VisualIdentifier.Identifier(Path.GetFileNameWithoutExtension(path))
            : Path.GetFileNameWithoutExtension(path);

    private static bool ScriptExists(string root, string name)
    {
        string scripts = Path.Combine(root, ScriptsFolder);
        return File.Exists(Path.Combine(scripts, name + ".cs")) || File.Exists(Path.Combine(scripts, name + ".visualcode"));
    }

    // A .cs file that a visual script next to it is compiled into.
    private static bool IsGenerated(string path)
    {
        if (!Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase)) return false;
        string folder = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        return Directory.Exists(folder) &&
               Directory.GetFiles(folder, "*.visualcode").Any(f => string.Equals(ClassOf(f), name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool Under(string relative, string folder) => relative.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
