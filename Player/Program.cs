using System.Reflection;
using System.Text.Json;
using Engine.Brutal;
using Engine.Brutal.Audio;
using Engine.Core.Assets;
using Engine.Core.Audio;
using Engine.Core.Scripting;
using Engine.Core.Serialization;

// Standalone game entry point. Reads GameData/game.json next to the executable and runs its start scene.
var gameData = Path.Combine(AppContext.BaseDirectory, "GameData");
var manifestPath = Path.Combine(gameData, "game.json");

if (!File.Exists(manifestPath))
{
    Console.WriteLine($"Missing {manifestPath}. The GameData folder must sit next to the executable.");
    return 1;
}

var manifest = JsonSerializer.Deserialize<GameManifest>(
                   File.ReadAllText(manifestPath),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidDataException("game.json is empty.");

var contentRoot = Path.Combine(gameData, "Core");
var assets = new AssetCache(contentRoot);
var scene = SceneSerializer.Load(Path.Combine(contentRoot, manifest.StartScene));

// Sounds, scene loading and save games. Saves go in the user's application data folder, under the game's name.
AudioEngine.ClipLoader = assets.GetAudio;
SceneManager.AssetWarmer = assets.Warm;
SaveGame.Directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    string.Concat(manifest.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)), "Saves");
SceneManager.Begin(scene, Path.GetFileNameWithoutExtension(manifest.StartScene), contentRoot);

// The game's scripts, then the packages the project turned on (built next to Scripts.dll as EmpirePackage.*.dll).
var assemblies = new List<Assembly>();
var scriptsDll = Path.Combine(gameData, "Scripts.dll");
if (File.Exists(scriptsDll)) assemblies.Add(Assembly.LoadFrom(scriptsDll));
foreach (var package in Directory.GetFiles(gameData, "EmpirePackage.*.dll").OrderBy(f => f, StringComparer.Ordinal))
    assemblies.Add(Assembly.LoadFrom(package));
var scriptErrors = ScriptRuntime.Load(scene, assemblies);
if (scriptErrors.Length > 0)
{
    Console.WriteLine("Script errors:\n" + scriptErrors);
    return 1;
}

using var audio = new AudioOutput();
new BrutalPlayer(assets).Run(scene, manifest.Name);
return 0;
