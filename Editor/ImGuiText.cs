using System.Text;

namespace Editor;

// Dynamic text for ImGui. Passing UTF-8 bytes uses the same path as "text"u8 literals,
// which avoids the char-span conversion that was crashing the editor.
public static class ImGuiText
{
    public static ReadOnlySpan<byte> ToU8(this string text) => Encoding.UTF8.GetBytes(text);
}
