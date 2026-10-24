using System.Text.Json;

namespace Editor.Assistant;

// What the editor remembers about the assistant: whether the user agreed to the notice, which model, and what they chose to
// attach. Saved per user, never in a project and never in a scene, so sharing a project never shares any of it.
public sealed class AssistantSettings
{
    // Raise this when the notice changes in a way people should read again; everyone is then asked once more.
    public const int CurrentConsentVersion = 1;

    public int ConsentVersion { get; set; }
    public string Model { get; set; } = AssistantClient.DefaultModel;
    public bool IncludeScript { get; set; }       // off until the user turns it on: nothing is attached by default
    public bool IncludeSelection { get; set; }

    public bool HasConsented => ConsentVersion >= CurrentConsentVersion;

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EmpireEngineEditor");

    private static string FilePath(string folder) => Path.Combine(folder, "assistant.json");

    public static AssistantSettings Load(string? folder = null)
    {
        try
        {
            string file = FilePath(folder ?? DefaultFolder);
            if (File.Exists(file))
                return JsonSerializer.Deserialize<AssistantSettings>(File.ReadAllText(file)) ?? new AssistantSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new AssistantSettings();
    }

    public void Save(string? folder = null)
    {
        folder ??= DefaultFolder;
        Directory.CreateDirectory(folder);
        File.WriteAllText(FilePath(folder), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

// Where the user's API key lives. The ANTHROPIC_API_KEY environment variable wins if it is set (and keeps the key out of
// every file); otherwise the key is in one file in the user's own settings folder that only they can read. It is never
// put in a project, a scene, a log or the editor's error reports.
public static class AssistantKeyStore
{
    public const string EnvironmentVariable = "ANTHROPIC_API_KEY";
    private const string FileName = "anthropic-api-key";

    // A key is printable ASCII with no spaces, so it can never break out of a header. Null means it is fine.
    public static string? ValidateKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "Enter your Anthropic API key first.";
        if (key.Length is < 20 or > 400 || key.Any(c => c < 33 || c > 126))
            return "That doesn't look like an Anthropic API key (it should be one long string with no spaces, starting with sk-ant-).";
        return null;
    }

    // The key to use and where it came from ("environment" or "file"), or null when there is none.
    public static string? Resolve(out string source, string? folder = null)
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim();
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            source = "environment";
            return fromEnvironment;
        }
        source = "file";
        return Load(folder);
    }

    public static string? Load(string? folder = null)
    {
        try
        {
            string file = Path.Combine(folder ?? AssistantSettings.DefaultFolder, FileName);
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static void Save(string key, string? folder = null)
    {
        key = key.Trim();
        if (ValidateKey(key) is { } problem) throw new AssistantException(problem);
        folder ??= AssistantSettings.DefaultFolder;
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, FileName);

        // Create the file closed to everyone but the user before the key goes in, so it is never readable by others.
        using (var stream = new FileStream(file, FileMode.Create, FileAccess.Write))
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            stream.Write(System.Text.Encoding.ASCII.GetBytes(key));
        }
    }

    public static void Delete(string? folder = null)
    {
        try { File.Delete(Path.Combine(folder ?? AssistantSettings.DefaultFolder, FileName)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
