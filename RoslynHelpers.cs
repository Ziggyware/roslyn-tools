using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using SharpToken;

namespace RoslynMcp;

[DebuggerDisplay("{ToString(),nq}")]
internal readonly record struct LineRange(int StartLine, int StartColumn, int EndLine, int EndColumn)
{
    public int LineCount => Math.Max(1, EndLine - StartLine + 1);
    public override string ToString() => $"L{StartLine}:{StartColumn}-L{EndLine}:{EndColumn}";
}

[DebuggerDisplay("{ToString(),nq}")]
internal sealed record DiffChangeItem(int Line, string Type, string Text)
{
    public override string ToString() => $"[{Type} L{Line}] {Text}";
}

[DebuggerDisplay("{ToString(),nq}")]
internal sealed record DiffSummary(
    bool HasChanges,
    int LinesInserted,
    int LinesDeleted,
    DiffChangeItem[] Changes,
    bool Truncated)
{
    public override string ToString() =>
        $"+{LinesInserted}/-{LinesDeleted} ({Changes.Length} change(s), hasChanges={HasChanges})";
}

[DebuggerDisplay("{ToString(),nq}")]
internal sealed record DiagnosticItem(string Id, string Severity, string Message, int Line, int Column)
{
    public override string ToString() => $"[{Severity}] {Id} at L{Line}:{Column}: {Message}";
}

[DebuggerDisplay("{ToString(),nq}")]
internal sealed record MutationOutcome(
    string File,
    string FormattedSource,
    bool SavedToDisk,
    int ErrorsBefore,
    int ErrorsAfter,
    int WarningsAfter,
    DiagnosticItem[] Diagnostics,
    DiffSummary Diff)
{
    public override string ToString() =>
        $"Mutation({File}: saved={SavedToDisk}, errors {ErrorsBefore}->{ErrorsAfter}, warnings={WarningsAfter}, diff={Diff})";
}

internal static class RoslynHelpers
{
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private static readonly Lazy<GptEncoding> LazyEncoder = new(() => GptEncoding.GetEncoding("cl100k_base"));

    public static string Ser(object value) => JsonSerializer.Serialize(value, JsonOpts);

    public static T Des<T>(string json, string paramName = "json")
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException($"{paramName} cannot be empty.", paramName);
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOpts)
                ?? throw new ArgumentException($"{paramName} deserialized to null.", paramName);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"{paramName} did not parse as valid JSON ({ex.Message}).", paramName, ex);
        }
    }

    public static int Tok(string text) => string.IsNullOrEmpty(text) ? 0 : LazyEncoder.Value.Encode(text).Count;

    public static string MinDisplay(this ISymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    public static string FullDisplay(this ISymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

    public static string ToDebugString(this ISymbol symbol) =>
        $"{symbol.GetFriendlyKind()} {symbol.MinDisplay()} [{symbol.DeclaredAccessibility}]";

    public static string ToDebugString(this SyntaxNode node)
    {
        var r = node.LineRange();
        return $"{node.Kind()} ({r}): {node.PreviewText(80)}";
    }

    public static ITypeSymbol? GetValueOrReturnType(this ISymbol? symbol) => symbol switch
    {
        IMethodSymbol ms => ms.ReturnType,
        IPropertySymbol ps => ps.Type,
        IFieldSymbol fs => fs.Type,
        IEventSymbol es => es.Type,
        ILocalSymbol ls => ls.Type,
        IParameterSymbol prs => prs.Type,
        INamedTypeSymbol ts => ts.BaseType,
        _ => null
    };

    public static string GetValueOrReturnTypeString(this ISymbol? symbol) =>
        symbol.GetValueOrReturnType()?.MinDisplay() ?? "";

    public static string GetFriendlyKind(this ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol { IsRecord: true } => "record",
        INamedTypeSymbol nt => nt.TypeKind.ToString().ToLowerInvariant(),
        IMethodSymbol { MethodKind: MethodKind.Constructor } => "constructor",
        _ => symbol.Kind.ToString().ToLowerInvariant()
    };

    public static string[] GetModifiers(this ISymbol symbol)
    {
        var mods = new List<string>(4);
        if (symbol.IsStatic) mods.Add("static");
        if (symbol.IsAbstract) mods.Add("abstract");
        if (symbol.IsVirtual) mods.Add("virtual");
        if (symbol.IsOverride) mods.Add("override");
        if (symbol.IsSealed) mods.Add("sealed");
        if (symbol.IsExtern) mods.Add("extern");
        if (symbol is IMethodSymbol { IsAsync: true }) mods.Add("async");
        if (symbol is IMethodSymbol { IsExtensionMethod: true }) mods.Add("extension");
        if (symbol is IMethodSymbol { IsReadOnly: true } or IPropertySymbol { IsReadOnly: true } or IFieldSymbol { IsReadOnly: true } or ITypeSymbol { IsReadOnly: true })
            mods.Add("readonly");
        if (symbol is IFieldSymbol { IsConst: true }) mods.Add("const");
        if (symbol is IFieldSymbol { IsRequired: true } or IPropertySymbol { IsRequired: true }) mods.Add("required");
        if (symbol is INamedTypeSymbol { IsRecord: true }) mods.Add("record");
        return mods.ToArray();
    }

    public static string GetXmlSummary(this ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
            return "";
        try
        {
            var doc = XDocument.Parse(xml);
            var summary = doc.Descendants("summary").FirstOrDefault()?.Value;
            return string.IsNullOrWhiteSpace(summary) ? "" : Regex.Replace(summary.Trim(), @"\s+", " ");
        }
        catch
        {
            return xml.Trim();
        }
    }

    public static ISymbol? ResolveSymbol(this SemanticModel model, SyntaxNode node) =>
        model.GetDeclaredSymbol(node)
        ?? model.GetSymbolInfo(node).Symbol
        ?? model.GetSymbolInfo(node).CandidateSymbols.FirstOrDefault();

    public static LineRange LineRange(this SyntaxNode node) =>
        node.SyntaxTree.LineRange(node.Span);

    public static LineRange LineRange(this SyntaxTree tree, TextSpan span)
    {
        var ls = tree.GetLineSpan(span);
        return new LineRange(
            ls.StartLinePosition.Line + 1,
            ls.StartLinePosition.Character + 1,
            ls.EndLinePosition.Line + 1,
            ls.EndLinePosition.Character + 1);
    }

    public static string PreviewText(this SyntaxNode node, int maxLength = 200)
    {
        var s = node.ToString();
        return s.Length > maxLength ? s[..maxLength] + "…" : s;
    }

    public static string FormatWithRoslyn(this SyntaxNode node)
    {
        using var ws = new AdhocWorkspace();
        try
        {
            return Formatter.Format(node, ws).ToFullString();
        }
        catch
        {
            return node.NormalizeWhitespace().ToFullString();
        }
    }

    public static DiffSummary BuildDiff(string oldText, string newText, int maxChanges = 150)
    {
        var diff = InlineDiffBuilder.Diff(oldText, newText);
        var changes = diff.Lines
            .Select((line, idx) => (line, lineNum: idx + 1))
            .Where(x => x.line.Type != ChangeType.Unchanged)
            .Select(x => new DiffChangeItem(x.line.Position ?? x.lineNum, x.line.Type.ToString(), x.line.Text))
            .ToArray();

        return new DiffSummary(
            HasChanges: !string.Equals(oldText, newText, StringComparison.Ordinal),
            LinesInserted: diff.Lines.Count(l => l.Type == ChangeType.Inserted),
            LinesDeleted: diff.Lines.Count(l => l.Type == ChangeType.Deleted),
            Changes: changes.Take(maxChanges).ToArray(),
            Truncated: changes.Length > maxChanges);
    }

    public static Regex CompileQueryRegex(string? query, bool allowGlob = true)
    {
        if (string.IsNullOrWhiteSpace(query) || query == "*")
            return new Regex(".*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        if (allowGlob && (query.Contains('*') || query.Contains('?')))
        {
            var pattern = "^" + Regex.Escape(query).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }

        try
        {
            return new Regex(query, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
        catch
        {
            return new Regex(Regex.Escape(query), RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
    }

    public static string ToCamelCaseIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name)) return "value";
        var trimmed = name.TrimStart('_');
        if (string.IsNullOrEmpty(trimmed)) return "value";
        var camel = char.ToLowerInvariant(trimmed[0]) + trimmed[1..];
        return SyntaxFacts.GetKeywordKind(camel) != SyntaxKind.None ? "@" + camel : camel;
    }
}
