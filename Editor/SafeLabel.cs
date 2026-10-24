using System.Text;

namespace Editor;

// Makes a name safe to hand to ImGui as a label: printable ASCII only, no '#' (which ImGui reads as an ID
// separator), and a bounded length. Anything else becomes '?'. Use "##" plus a unique ID for the ID part.
public static class SafeLabel
{
    public static string Of(string? name, int maxChars = 48)
    {
        var sb = new StringBuilder();
        foreach (char c in name ?? "")
        {
            if (sb.Length >= maxChars)
            {
                sb.Append("...");
                break;
            }
            sb.Append(c >= 32 && c < 127 ? (c == '#' ? '_' : c) : '?');
        }
        return sb.Length == 0 ? "(unnamed)" : sb.ToString();
    }
}
