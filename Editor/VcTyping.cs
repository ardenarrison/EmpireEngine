using System.Text;

namespace Editor;

// What a visual code field takes from typed characters: letters, digits, spaces and - _ .
public static class VcTyping
{
    public static string Filter(IReadOnlyList<uint> characters)
    {
        var typed = new StringBuilder();
        foreach (uint character in characters)
            if (character < 128 && (char.IsAsciiLetterOrDigit((char)character) || character is ' ' or '-' or '_' or '.'))
                typed.Append((char)character);
        return typed.ToString();
    }
}
