using Brutal.GlfwApi;
using Engine.Core.Scripting;

namespace Engine.Brutal.Gfx;

// Connects script input to the keyboard. Each key name in Keys has a GLFW key with the same name, except the number
// keys: Keys.D0 to D9 are GLFW's Number0 to Number9.
public static class KeyboardInput
{
    public static void Bind(GlfwWindow window)
    {
        var keys = new Dictionary<Keys, GlfwKey>();
        foreach (var key in Enum.GetValues<Keys>())
        {
            string name = key.ToString();
            if (name is ['D', >= '0' and <= '9']) name = "Number" + name[1];
            if (Enum.TryParse<GlfwKey>(name, out var glfw))
                keys[key] = glfw;
        }

        Input.Provider = key =>
            keys.TryGetValue(key, out var glfw) && window.GetKey(glfw) == GlfwKeyAction.Press;
    }
}
