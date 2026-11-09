#:package Ahwoo.BRUTAL.Glfw@2026.10.4
#:property PublishAot=false
#:property AllowUnsafeBlocks=true
// Teaches the engine the layout of a controller GLFW doesn't know. Connect the controller, run this from the repository folder,
// and press each button it asks for:
//     dotnet run Tools/MapController.cs              (--again to redo a controller that already works, --wait 60 to set how
//                                                     many seconds it waits for one to be connected)
// It writes the controller's line into Engine.Brutal/Gfx/GamepadMappings.txt, which games load when they start, and then lets
// you try the buttons. Prompts use the letters on a Nintendo controller; the layout itself is by position, as the engine's
// GamepadButton is.
using Brutal.GlfwApi;

bool again = args.Contains("--again");
int wait = args.SkipWhile(a => a != "--wait").Skip(1).Select(a => int.TryParse(a, out int s) ? s : 60).FirstOrDefault(60);
string? file = MappingsFile();
if (file is null)
{
    Console.WriteLine("Run this from the Empire Engine repository folder: Engine.Brutal/Gfx/GamepadMappings.txt wasn't found.");
    return 1;
}
if (!Glfw.Init())
{
    Console.WriteLine("GLFW could not start.");
    return 1;
}

try
{
    Console.WriteLine($"Looking for a controller for {wait} seconds. Connect one now if it isn't already.");
    int slot = -1;
    for (var until = DateTime.UtcNow.AddSeconds(wait); slot < 0 && DateTime.UtcNow < until; Thread.Sleep(50))
    {
        Glfw.PollEvents();
        slot = Enumerable.Range(0, 16).FirstOrDefault(s => Glfw.JoystickPresent(s), -1);
    }
    if (slot < 0)
    {
        Console.WriteLine("No controller was found. Check it's connected (for a Switch 2 Pro Controller, through switch2mac) and run this again.");
        return 1;
    }

    string name = (Glfw.GetJoystickName(slot) ?? "Controller").Replace(",", " ");
    string guid = Glfw.GetJoystickGuid(slot) ?? "";
    Console.WriteLine($"Found {name} ({guid}).");
    if (Glfw.JoystickIsGamepad(slot) && !again)
    {
        Console.WriteLine($"It already works, as {Glfw.GetGamepadName(slot)}. Nothing to do. Run with --again to map it anyway.");
        return 0;
    }

    Console.WriteLine("Let go of everything for a moment.");
    var rest = Sample(slot, TimeSpan.FromSeconds(1));
    Console.WriteLine("Now press each one when asked, then let go. Press Enter to skip one the controller doesn't have.\n");

    var parts = new List<string>();
    var used = new HashSet<string>();
    foreach (var (target, prompt, kind) in Targets())
    {
        Console.Write($"  {prompt,-38}");
        string? input = null;
        while (input is null)
        {
            if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Enter) break;
            Glfw.PollEvents();
            Thread.Sleep(10);
            var now = Read(slot);
            if (now is null)
            {
                Console.WriteLine("\nThe controller went away. Connect it and run this again.");
                return 1;
            }
            if (Detect(rest, now, kind) is not { } found) continue;
            if (!used.Add(Key(found)))
            {
                Console.Write($"(that one is already used) ");
                WaitForRest(slot, rest);
                continue;
            }
            input = found;
        }
        Console.WriteLine(input is null ? "skipped" : input);
        if (input is not null) parts.Add($"{target}:{input}");
        WaitForRest(slot, rest);
    }

    string platform = OperatingSystem.IsMacOS() ? "Mac OS X" : OperatingSystem.IsWindows() ? "Windows" : "Linux";
    string line = $"{guid},{name},{string.Join(",", parts)},platform:{platform},";
    Save(file, guid, platform, line);
    Console.WriteLine($"\nAdded to {Path.GetRelativePath(Directory.GetCurrentDirectory(), file)}:\n{line}\n");

    if (!Glfw.UpdateGamepadMappings(line) || !Glfw.JoystickIsGamepad(slot))
    {
        Console.WriteLine("GLFW didn't accept the layout, so it isn't usable. Run this again and try each button once more.");
        return 1;
    }
    Console.WriteLine($"It works now, as {Glfw.GetGamepadName(slot)}. Press buttons to check them, and Enter to finish:");
    var held = new HashSet<GlfwGamepadButton>();
    while (!(Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Enter))
    {
        Glfw.PollEvents();
        Thread.Sleep(10);
        if (!Glfw.GetGamepadState(slot, out var state)) continue;
        foreach (var button in Enum.GetValues<GlfwGamepadButton>().Where(b => b != GlfwGamepadButton.Last).Distinct())
        {
            bool down = Pressed(state, (int)button);
            if (down && held.Add(button)) Console.WriteLine($"  {Position(button)}");
            if (!down) held.Remove(button);
        }
    }
    Console.WriteLine("Done. Build or play your game again to use it.");
    return 0;
}
finally
{
    Glfw.Terminate();
}

// What to ask for, in SDL's names. SDL's a, b, x and y are positions (a is the bottom button), so they're asked for by the
// Nintendo letter in that place.
static IEnumerable<(string Target, string Prompt, Kind Kind)> Targets() =>
[
    ("a", "Press B (bottom face button)", Kind.Button), ("b", "Press A (right face button)", Kind.Button),
    ("x", "Press Y (left face button)", Kind.Button), ("y", "Press X (top face button)", Kind.Button),
    ("leftshoulder", "Press L", Kind.Button), ("rightshoulder", "Press R", Kind.Button),
    ("lefttrigger", "Press ZL", Kind.Trigger), ("righttrigger", "Press ZR", Kind.Trigger),
    ("back", "Press -", Kind.Button), ("start", "Press +", Kind.Button), ("guide", "Press Home", Kind.Button),
    ("leftstick", "Press the left stick in", Kind.Button), ("rightstick", "Press the right stick in", Kind.Button),
    ("dpup", "Press up on the pad", Kind.Button), ("dpdown", "Press down on the pad", Kind.Button),
    ("dpleft", "Press left on the pad", Kind.Button), ("dpright", "Press right on the pad", Kind.Button),
    ("leftx", "Push the left stick right", Kind.Stick), ("lefty", "Push the left stick down", Kind.Stick),
    ("rightx", "Push the right stick right", Kind.Stick), ("righty", "Push the right stick down", Kind.Stick),
];

// What changed from rest, in the mapping's own words: b3 for button 3, h0.1 for a hat's direction, a2 for an axis.
static string? Detect(Raw rest, Raw now, Kind kind)
{
    // A hat first: GLFW also reports a hat's directions as extra buttons, and the hat is the one to use.
    for (int h = 0; h < now.Hats.Length && h < rest.Hats.Length; h++)
        if (now.Hats[h] != rest.Hats[h] && now.Hats[h] is 1 or 2 or 4 or 8) return $"h{h}.{now.Hats[h]}";
    for (int b = 0; b < now.Buttons.Length && b < rest.Buttons.Length; b++)
        if (now.Buttons[b] != 0 && rest.Buttons[b] == 0) return $"b{b}";
    for (int a = 0; a < now.Axes.Length && a < rest.Axes.Length; a++)
    {
        float moved = now.Axes[a] - rest.Axes[a];
        if (MathF.Abs(moved) < 0.6f) continue;
        return kind switch
        {
            Kind.Stick => moved > 0f ? $"a{a}" : $"a{a}~",
            Kind.Trigger when rest.Axes[a] < -0.5f => $"a{a}",          // a trigger that rests at -1 uses all of its axis
            _ => moved > 0f ? $"+a{a}" : $"-a{a}",
        };
    }
    return null;
}

// The input itself, so an axis can't be given to both of its directions, or a button to two jobs.
static string Key(string input) => input.TrimStart('+', '-').TrimEnd('~');

static Raw Sample(int slot, TimeSpan time)
{
    Raw? last = null;
    for (var until = DateTime.UtcNow + time; DateTime.UtcNow < until; Thread.Sleep(10))
    {
        Glfw.PollEvents();
        last = Read(slot) ?? last;
    }
    return last ?? new Raw([], [], []);
}

static void WaitForRest(int slot, Raw rest)
{
    for (int quiet = 0; quiet < 15; Thread.Sleep(10))
    {
        Glfw.PollEvents();
        if (Read(slot) is not { } now) return;
        bool still = Detect(rest, now, Kind.Button) is null && Detect(rest, now, Kind.Stick) is null;
        quiet = still ? quiet + 1 : 0;
    }
}

static Raw? Read(int slot)
{
    if (!Glfw.JoystickPresent(slot)) return null;
    return new Raw(Glfw.GetJoystickButtons(slot).ToArray(), Glfw.GetJoystickAxes(slot).ToArray(),
                   Glfw.GetJoystickHats(slot).ToArray().Select(h => (int)h).ToArray());
}

static unsafe bool Pressed(GlfwGamepadState state, int button) => state.Buttons[button] != 0;

static string Position(GlfwGamepadButton button) => (int)button switch
{
    0 => "South (B)", 1 => "East (A)", 2 => "West (Y)", 3 => "North (X)", 4 => "L", 5 => "R", 6 => "-", 7 => "+", 8 => "Home",
    9 => "left stick in", 10 => "right stick in", 11 => "pad up", 12 => "pad right", 13 => "pad down", _ => "pad left",
};

// The mappings file, found from the folder this runs in, so it works from anywhere in the repository.
static string? MappingsFile()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        string candidate = Path.Combine(dir.FullName, "Engine.Brutal", "Gfx", "GamepadMappings.txt");
        if (File.Exists(candidate)) return candidate;
    }
    return null;
}

// Puts the line in the file, in place of any earlier line for the same controller on the same platform.
static void Save(string file, string guid, string platform, string line)
{
    var lines = File.ReadAllLines(file)
        .Where(l => !(l.StartsWith(guid + ",", StringComparison.OrdinalIgnoreCase) && l.Contains($"platform:{platform},")))
        .Append(line);
    File.WriteAllText(file, string.Join("\n", lines) + "\n");
}

enum Kind { Button, Trigger, Stick }

record Raw(byte[] Buttons, float[] Axes, int[] Hats);
