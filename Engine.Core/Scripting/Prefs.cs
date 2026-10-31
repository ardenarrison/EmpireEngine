using System.Globalization;
using System.Text.Json;

namespace Engine.Core.Scripting;

// Small values that stay between plays of the game, such as settings and high scores. Unlike SaveGame they are not tied to
// a save slot and need no Save call: each Set writes the file straight away.
//
//   Prefs.SetInt("highScore", 120);
//   int best = Prefs.GetInt("highScore");          // 0 when it was never set
//   Prefs.SetFloat("volume", 0.8f);
//   string name = Prefs.GetString("playerName", "Player");
//
// They are kept in prefs.json, in the game's folder in the user's application data (next to its Saves folder). In the
// editor that folder belongs to the project, so values set while playing in the editor stay apart from the built game's.
public static class Prefs
{
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _values;
    private static string? _loadedFrom;
    private static string? _filePath;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    // The file the values are kept in. It follows SaveGame.Directory unless set.
    public static string FilePath
    {
        get => _filePath ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(SaveGame.Directory)) ?? SaveGame.Directory, "prefs.json");
        set => _filePath = value;
    }

    public static void SetInt(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));
    public static void SetFloat(string key, float value) => Set(key, value.ToString("R", CultureInfo.InvariantCulture));
    public static void SetBool(string key, bool value) => Set(key, value ? "true" : "false");
    public static void SetString(string key, string value) => Set(key, value ?? "");

    public static int GetInt(string key, int defaultValue = 0) =>
        Get(key) is { } text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : defaultValue;

    public static float GetFloat(string key, float defaultValue = 0f) =>
        Get(key) is { } text && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : defaultValue;

    public static bool GetBool(string key, bool defaultValue = false) =>
        Get(key) is { } text && bool.TryParse(text, out bool v) ? v : defaultValue;

    public static string GetString(string key, string defaultValue = "") => Get(key) ?? defaultValue;

    public static bool HasKey(string key)
    {
        lock (Gate) return Values().ContainsKey(key);
    }

    // Forgets one value, or every value.
    public static void DeleteKey(string key)
    {
        lock (Gate)
            if (Values().Remove(key)) Write();
    }

    public static void DeleteAll()
    {
        lock (Gate)
        {
            Values().Clear();
            Write();
        }
    }

    private static string? Get(string key)
    {
        lock (Gate) return Values().TryGetValue(key, out var v) ? v : null;
    }

    private static void Set(string key, string value)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("A pref needs a name.", nameof(key));
        lock (Gate)
        {
            var values = Values();
            if (values.TryGetValue(key, out var old) && old == value) return;
            values[key] = value;
            Write();
        }
    }

    // The values in the file, read the first time they are needed (and again if the file's place changes).
    private static Dictionary<string, string> Values()
    {
        string path = FilePath;
        if (_values is not null && _loadedFrom == path) return _values;
        _values = new Dictionary<string, string>();
        _loadedFrom = path;
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) is { } read)
                _values = read;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.WriteLine($"Couldn't read {path}: {ex.Message}. Starting with no prefs.");
        }
        return _values;
    }

    // Replaces the file in one step, so a crash never leaves it half written. A failure is reported, not thrown: losing a
    // setting shouldn't stop the game.
    private static void Write()
    {
        string path = _loadedFrom ?? FilePath;
        string temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(_values, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Couldn't save prefs to {path}: {ex.Message}");
        }
    }
}
