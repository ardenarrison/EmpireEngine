using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Editor.Code;

public enum TokenColor : byte { Plain, Keyword, Control, Type, Method, Variable, String, Number, Comment, Preprocessor }

// Colors C# from Roslyn's syntax tree. Names count as types from where they appear (a declaration's type, after
// new, in a base list...) or because the last error check found a type by that name, such as Vector3 or Time.
public static class CSharpHighlighter
{
    private static readonly HashSet<SyntaxKind> ControlKeywords = new()
    {
        SyntaxKind.IfKeyword, SyntaxKind.ElseKeyword, SyntaxKind.ForKeyword, SyntaxKind.ForEachKeyword,
        SyntaxKind.WhileKeyword, SyntaxKind.DoKeyword, SyntaxKind.ReturnKeyword, SyntaxKind.BreakKeyword,
        SyntaxKind.ContinueKeyword, SyntaxKind.SwitchKeyword, SyntaxKind.CaseKeyword, SyntaxKind.DefaultKeyword,
        SyntaxKind.TryKeyword, SyntaxKind.CatchKeyword, SyntaxKind.FinallyKeyword, SyntaxKind.ThrowKeyword,
        SyntaxKind.YieldKeyword, SyntaxKind.GotoKeyword, SyntaxKind.AwaitKeyword, SyntaxKind.InKeyword,
    };

    // One color per character, per line.
    public static TokenColor[][] Classify(IReadOnlyList<string> lines, IReadOnlySet<string> typeNames)
    {
        string text = string.Join("\n", lines);
        var colors = new TokenColor[text.Length];
        var root = CSharpSyntaxTree.ParseText(text).GetRoot();

        foreach (var token in root.DescendantTokens(descendIntoTrivia: true))
            Mark(colors, token.Span, ColorOf(token, typeNames));

        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            var kind = trivia.Kind();
            if (kind is SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia or SyntaxKind.DisabledTextTrivia
                or SyntaxKind.SingleLineDocumentationCommentTrivia or SyntaxKind.MultiLineDocumentationCommentTrivia)
                Mark(colors, trivia.Span, TokenColor.Comment);
            else if (trivia.IsDirective)
                Mark(colors, trivia.Span, TokenColor.Preprocessor);
        }

        var result = new TokenColor[lines.Count][];
        int start = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            result[i] = colors.AsSpan(start, lines[i].Length).ToArray();
            start += lines[i].Length + 1;
        }
        return result;
    }

    private static void Mark(TokenColor[] colors, Microsoft.CodeAnalysis.Text.TextSpan span, TokenColor color)
    {
        if (color == TokenColor.Plain) return;
        int end = Math.Min(span.End, colors.Length);
        for (int i = Math.Max(0, span.Start); i < end; i++) colors[i] = color;
    }

    private static TokenColor ColorOf(SyntaxToken token, IReadOnlySet<string> typeNames)
    {
        var kind = token.Kind();
        if (ControlKeywords.Contains(kind)) return TokenColor.Control;
        if (SyntaxFacts.IsKeywordKind(kind)) return TokenColor.Keyword;
        if (kind == SyntaxKind.NumericLiteralToken) return TokenColor.Number;

        string name = kind.ToString();
        if (name.Contains("StringLiteral") || name.Contains("CharacterLiteral") || name.Contains("InterpolatedString") ||
            name.Contains("RawString") || kind == SyntaxKind.InterpolatedStringTextToken)
            return TokenColor.String;

        return kind == SyntaxKind.IdentifierToken ? ColorOfIdentifier(token, typeNames) : TokenColor.Plain;
    }

    private static TokenColor ColorOfIdentifier(SyntaxToken token, IReadOnlySet<string> typeNames)
    {
        switch (token.Parent)
        {
            case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or ConstructorDeclarationSyntax
                or DestructorDeclarationSyntax or TypeParameterSyntax:
                return TokenColor.Type;
            case MethodDeclarationSyntax or LocalFunctionStatementSyntax:
                return TokenColor.Method;
            case SimpleNameSyntax nameNode when IsNamespaceName(nameNode):
                return TokenColor.Plain;
            case SimpleNameSyntax nameNode:
                if (token.Text == "var" && SyntaxFacts.IsInTypeOnlyContext(nameNode)) return TokenColor.Keyword;
                if (IsInvoked(nameNode)) return TokenColor.Method;
                if (SyntaxFacts.IsInTypeOnlyContext(nameNode) || nameNode.Parent is ObjectCreationExpressionSyntax)
                    return TokenColor.Type;
                if (typeNames.Contains(token.Text) && token.Text.Length > 0 && char.IsUpper(token.Text[0]))
                    return TokenColor.Type;
                return TokenColor.Variable;
            default:
                return TokenColor.Variable;
        }
    }

    // Names in `using System.Linq;` and `namespace Game.Things`, which read as plain text.
    private static bool IsNamespaceName(SimpleNameSyntax name)
    {
        SyntaxNode node = name;
        while (node.Parent is QualifiedNameSyntax) node = node.Parent;
        return node.Parent is UsingDirectiveSyntax { StaticKeyword.RawKind: 0, Alias: null } ||
               node.Parent is BaseNamespaceDeclarationSyntax ns && ns.Name == node;
    }

    // True for Foo() and x.Foo(), where the name is what gets called.
    private static bool IsInvoked(SimpleNameSyntax name) => name.Parent switch
    {
        InvocationExpressionSyntax call => call.Expression == name,
        MemberAccessExpressionSyntax access => access.Name == name &&
                                               access.Parent is InvocationExpressionSyntax call && call.Expression == access,
        _ => false,
    };
}
