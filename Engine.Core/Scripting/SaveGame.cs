using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Engine.Core.Serialization;

namespace Engine.Core.Scripting;

// Saves and loads the whole state of the game in named slots.
//
//   SaveGame.SetInt("coins", 12);        // your own values travel with the save
//   SaveGame.Save("slot1");              // every loaded scene, as it is right now, plus those values
//   SaveGame.Load("slot1");              // puts it all back at the start of the next frame
//
// A save keeps each object's transform and components (a Rigidbody's speed, an Audio Source's place in its clip), every
// script's public fields, which scenes are loaded, and Time.time. Script variables that are private, and anything a
// script made up that is not a public field, are not saved: keep that kind of thing in SetInt/SetString and friends and
// read it back in OnGameLoaded.
public static class SaveGame
{
    public const int FormatVersion = 1;
    private const string Extension = ".empiresave";

    private static readonly Dictionary<string, string> Data = new();
    private static readonly object Gate = new();
    private static string? _directory;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class SaveFile
    {
        public int FormatVersion { get; set; } = SaveGame.FormatVersion;
        public string SavedAt { get; set; } = "";
        public string ActiveScene { get; set; } = "";
        public List<string> LoadedScenes { get; set; } = new();
        public float Time { get; set; }
        public Dictionary<string, string> Data { get; set; } = new();
        public JsonElement Scene { get; set; }
    }

    // Where save files go. The editor and the player set it to a folder for the project; it starts as
    // EmpireEngine/Saves in the user's application data folder.
    public static string Directory
    {
        get => _directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EmpireEngine", "Saves");
        set => _directory = value;
    }

    // ---- your own values ---------------------------------------------------------------

    public static void SetInt(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));
    public static void SetFloat(string key, float value) => Set(key, value.ToString("R", CultureInfo.InvariantCulture));
    public static void SetBool(string key, bool value) => Set(key, value ? "true" : "false");
    public static void SetString(string key, string value) => Set(key, value);

    public static int GetInt(string key, int defaultValue = 0) =>
        TryGet(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : defaultValue;

    public static float GetFloat(string key, float defaultValue = 0f) =>
        TryGet(key, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : defaultValue;

    public static bool GetBool(string key, bool defaultValue = false) =>
        TryGet(key, out var v) && bool.TryParse(v, out var b) ? b : defaultValue;

    public static string GetString(string key, string defaultValue = "") =>
        TryGet(key, out var v) ? v : defaultValue;

    public static bool HasKey(string key) => TryGet(key, out _);

    public static void DeleteKey(string key)
    {
        lock (Gate) Data.Remove(key);
    }

    // Forgets every value set with SetInt and friends. A new game starts with none.
    public static void ClearData()
    {
        lock (Gate) Data.Clear();
    }

    private static void Set(string key, string value)
    {
        lock (Gate) Data[key] = value;
    }

    private static bool TryGet(string key, out string value)
    {
        lock (Gate) return Data.TryGetValue(key, out value!);
    }

    // ---- slots ---------------------------------------------------------------------------

    // Writes the game as it is now to the slot, replacing what was there. Returns false (and says why on the console)
    // when it could not be written.
    public static bool Save(string slot)
    {
        if (SceneManager.World is not { } world)
        {
            Console.WriteLine("Cannot save: no scene is playing.");
            return false;
        }
        try
        {
            string path = PathFor(slot);
            string sceneJson = SceneSerializer.ToJson(world, runtimeState: true);
            SaveFile file;
            lock (Gate)
                file = new SaveFile
                {
                    SavedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    ActiveScene = SceneManager.ActiveScene,
                    LoadedScenes = SceneManager.LoadedScenes.ToList(),
                    Time = Time.time,
                    Data = new Dictionary<string, string>(Data),
                    Scene = JsonDocument.Parse(sceneJson).RootElement.Clone(),
                };

            System.IO.Directory.CreateDirectory(Directory);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, Options));
            File.Move(temp, path, overwrite: true);   // a crash while writing leaves the old save alone
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.WriteLine($"Could not save slot '{slot}': {ex.Message}");
            return false;
        }
    }

    // Puts the slot's game in place of the one that is playing, at the start of the next frame, so it is safe to call
    // from anywhere in a script. Returns false (and says why) when the slot is missing or unreadable; the game carries on.
    public static bool Load(string slot)
    {
        try
        {
            string path = PathFor(slot);
            if (!File.Exists(path))
            {
                Console.WriteLine($"Cannot load: there is no save in slot '{slot}'.");
                return false;
            }

            var file = JsonSerializer.Deserialize<SaveFile>(File.ReadAllText(path), Options)
                       ?? throw new InvalidDataException("the file is empty");
            if (file.FormatVersion > FormatVersion)
                throw new NotSupportedException($"it was saved by a newer version of the engine (format {file.FormatVersion})");
            var scene = SceneSerializer.FromJson(file.Scene.GetRawText(), $"save '{slot}'");

            SceneManager.Enqueue(() => true, world =>
            {
                SceneManager.ApplySave(world, scene, file.ActiveScene, file.LoadedScenes);
                lock (Gate)
                {
                    Data.Clear();
                    foreach (var (key, value) in file.Data)
                        Data[key] = value;
                }
                Time.time = file.Time;
            });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException
                                       or InvalidDataException or NotSupportedException or InvalidOperationException)
        {
            Console.WriteLine($"Could not load slot '{slot}': {ex.Message}");
            return false;
        }
    }

    public static bool Exists(string slot) => File.Exists(PathFor(slot));

    // When the slot was saved, in the user's time, or null when it is empty or unreadable.
    public static DateTime? Timestamp(string slot)
    {
        try
        {
            string path = PathFor(slot);
            if (!File.Exists(path)) return null;
            var file = JsonSerializer.Deserialize<SaveFile>(File.ReadAllText(path), Options);
            return file is not null && DateTime.TryParse(file.SavedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
                ? t.ToLocalTime()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Delete(string slot)
    {
        string path = PathFor(slot);
        if (File.Exists(path)) File.Delete(path);
    }

    // The names of the slots that have a save, sorted.
    public static string[] Slots()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        return System.IO.Directory.GetFiles(Directory, "*" + Extension)
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Slot names are file names, so they may use letters, digits, spaces, dashes and underscores only.
    private static string PathFor(string slot)
    {
        if (string.IsNullOrWhiteSpace(slot) || !Regex.IsMatch(slot, @"^[\w \-]{1,64}$"))
            throw new ArgumentException($"'{slot}' is not a valid slot name. Use letters, digits, spaces, dashes and underscores.");
        return Path.Combine(Directory, slot.Trim() + Extension);
    }
}
