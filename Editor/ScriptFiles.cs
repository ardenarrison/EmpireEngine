namespace Editor;

// Helpers for the scripts a project keeps in its Scripts folder.
public static class ScriptFiles
{
    // Turns any text into a valid C# class name: letters, digits and underscores, starting with a letter.
    public static string ToClassName(string text)
    {
        string name = new string(text.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (name.Length == 0)
            return "";
        if (char.IsDigit(name[0]))
            name = "_" + name;
        return name;
    }

    // Creates Scripts/<name>.cs with a starter template, unless the file already exists.
    public static void Create(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name + ".cs");
        if (File.Exists(path))
            return;

        File.WriteAllText(path, $$"""
            // Runs every frame while the scene plays. The class name must match this file name.
            public class {{name}} : Behaviour
            {
                public override void Update()
                {
                }
            }
            """);
    }
}
