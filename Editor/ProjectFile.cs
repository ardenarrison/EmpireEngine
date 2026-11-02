using System.Text.Json.Nodes;

namespace Editor;

// What the editor reads from project.empire, the file the Hub writes at the project's root: for now, whether the project is 2D.
public static class ProjectFile
{
    public const string FileName = "project.empire";

    // True when project.empire says "type": "2D". A missing or unreadable file, or any other type, means 3D.
    public static bool Is2D(string projectFolder)
    {
        try
        {
            string file = Path.Combine(projectFolder, FileName);
            return File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject root &&
                   root["type"] is JsonValue type && type.TryGetValue(out string? text) &&
                   string.Equals(text?.Trim(), "2D", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return false;
        }
    }
}
