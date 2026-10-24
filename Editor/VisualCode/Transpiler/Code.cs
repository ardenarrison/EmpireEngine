using System.Text.RegularExpressions;

namespace VisualCode.Transpiler;

// Small helpers for building indented C# lines.
public static class Code
{
    public static List<string> Indent(IEnumerable<string> lines) =>
        lines.Select(l => l.Length == 0 ? l : "    " + l).ToList();

    public static List<string> Block(string header, IEnumerable<string> inner)
    {
        var result = new List<string> { header, "{" };
        result.AddRange(Indent(inner));
        result.Add("}");
        return result;
    }

    // A loop that yields once per pass so it never freezes the frame.
    public static List<string> Loop(IEnumerable<string> inner) =>
        Block("while (true)", inner.Append("yield return null;"));

    public static List<string> One(string line) => new() { line };

    public static string Escape(string text) => text.Replace("\r", " ").Replace("\n", " ");

    public static string Identifier(string name)
    {
        var s = Regex.Replace(name, "[^A-Za-z0-9_]", "_");
        if (s.Length == 0 || char.IsDigit(s[0])) s = "_" + s;
        return s;
    }
}
