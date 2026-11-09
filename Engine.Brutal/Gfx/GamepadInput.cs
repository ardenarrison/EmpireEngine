using Brutal.GlfwApi;
using Engine.Core.Scripting;

namespace Engine.Brutal.Gfx;

// Connects script input to game controllers through GLFW, which knows the layout of most controllers, the Nintendo Switch Pro
// Controller among them, and of those in GamepadMappings.txt. Controllers are numbered in the order GLFW lists them. One with no
// layout is left out, and the console says so once: Tools/MapController.cs can add its layout.
public static class GamepadInput
{
    private const int Slots = 16;   // GLFW's joystick slots
    private const int ButtonCount = 15, AxisCount = 6;
    private static bool _mappingsAdded;

    public static void Bind()
    {
        if (OperatingSystem.IsIOS()) return;   // the iPad doesn't use GLFW
        if (!_mappingsAdded)
        {
            _mappingsAdded = true;
            AddMappings();
        }
        var reported = new HashSet<string>();
        Input.GamepadProvider = () =>
        {
            var pads = new List<GamepadState>();
            for (int slot = 0; slot < Slots; slot++)
            {
                if (!Glfw.JoystickPresent(slot)) continue;
                if (!Glfw.JoystickIsGamepad(slot))
                {
                    string name = Glfw.GetJoystickName(slot) ?? "A controller";
                    if (reported.Add(name))
                        Console.WriteLine($"{name} is connected, but its button layout isn't known (id {Glfw.GetJoystickGuid(slot)}), so games can't use it. " +
                                          "Run dotnet run Tools/MapController.cs in the engine's folder to add it.");
                    continue;
                }
                if (Glfw.GetGamepadState(slot, out var state))
                    pads.Add(Read(Glfw.GetGamepadName(slot) ?? "Controller", state));
            }
            return pads;
        };
    }

    // The layouts in GamepadMappings.txt, added to GLFW's own.
    private static void AddMappings()
    {
        using var stream = typeof(GamepadInput).Assembly.GetManifestResourceStream("Engine.Brutal.Gfx.GamepadMappings.txt");
        if (stream is null) return;
        var lines = new StreamReader(stream).ReadToEnd().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        if (lines.Count > 0 && !Glfw.UpdateGamepadMappings(string.Join("\n", lines)))
            Console.WriteLine("Some controller layouts in GamepadMappings.txt couldn't be read.");
    }

    // GLFW's buttons are in GamepadButton's order. Its sticks point down for positive Y and its triggers go from -1 to 1;
    // games get up as positive and triggers from 0 to 1.
    private static unsafe GamepadState Read(string name, GlfwGamepadState state)
    {
        var buttons = new bool[ButtonCount];
        var axes = new float[AxisCount];
        for (int i = 0; i < ButtonCount; i++) buttons[i] = state.Buttons[i] != 0;
        for (int i = 0; i < AxisCount; i++) axes[i] = state.Axes[i];
        axes[(int)GamepadAxis.LeftY] = -axes[(int)GamepadAxis.LeftY];
        axes[(int)GamepadAxis.RightY] = -axes[(int)GamepadAxis.RightY];
        axes[(int)GamepadAxis.LeftTrigger] = (axes[(int)GamepadAxis.LeftTrigger] + 1f) / 2f;
        axes[(int)GamepadAxis.RightTrigger] = (axes[(int)GamepadAxis.RightTrigger] + 1f) / 2f;
        return new GamepadState(name, buttons, axes);
    }
}
