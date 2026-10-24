using System.Text;
using Brutal.GlfwApi;
using Engine.Core.Scripting;

namespace Engine.Brutal.Gfx;

// Connects Input.inputString (and so the game's text fields) to the characters a window receives. The characters come
// from the keyboard layout, so Shift+a arrives as "A" and dead keys and input methods work as they do elsewhere.
public static class TextInput
{
    private const int MaxBuffered = 256;   // characters kept between frames, so nothing piles up while unread

    public static void Bind(GlfwWindow window)
    {
        var typed = new StringBuilder();
        window.OnChar += (_, codepoint) =>
        {
            if (typed.Length < MaxBuffered)
                typed.Append(char.ConvertFromUtf32((int)codepoint));
        };
        Input.TextProvider = () =>
        {
            string text = typed.ToString();
            typed.Clear();
            return text;
        };
    }
}
