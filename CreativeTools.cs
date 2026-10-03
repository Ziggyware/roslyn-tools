using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp;

[McpServerToolType]
internal static class CreativeTools
{
    [McpServerTool, Description(
        "Scaffold a syntactically valid starting file for a class/interface/record/struct/enum via SyntaxFactory. " +
        "kind: class|interface|record|struct|enum. Guarantees parseable output by construction.")]
    public static string ScaffoldFile(string kind, string typeName, string ns, string[] usings)
    {
        if (!SyntaxFacts.IsValidIdentifier(typeName))
            throw new ArgumentException($"'{typeName}' is not a valid C# type identifier.", nameof(typeName));
        if (string.IsNullOrWhiteSpace(ns))
            throw new ArgumentException("Namespace 'ns' cannot be empty.", nameof(ns));

        MemberDeclarationSyntax type = kind.Trim().ToLowerInvariant() switch
        {
            "class" => SyntaxFactory.ClassDeclaration(typeName)
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword)),
            "interface" => SyntaxFactory.InterfaceDeclaration(typeName)
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword)),
            "struct" => SyntaxFactory.StructDeclaration(typeName)
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword)),
            "record" => SyntaxFactory.RecordDeclaration(SyntaxKind.RecordDeclaration,
                SyntaxFactory.Token(SyntaxKind.RecordKeyword), typeName)
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                .WithOpenBraceToken(SyntaxFactory.Token(SyntaxKind.OpenBraceToken))
                .WithCloseBraceToken(SyntaxFactory.Token(SyntaxKind.CloseBraceToken)),
            "enum" => SyntaxFactory.EnumDeclaration(typeName)
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword)),
            _ => throw new ArgumentException($"unrecognized kind: {kind}")
        };

        var nsDecl = SyntaxFactory.FileScopedNamespaceDeclaration(SyntaxFactory.ParseName(ns))
            .AddMembers(type);
        var unit = SyntaxFactory.CompilationUnit()
            .AddUsings((usings ?? Array.Empty<string>()).Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(u => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(u))).ToArray())
            .AddMembers(nsDecl)
            .NormalizeWhitespace();

        return unit.ToFullString();
    }

    [McpServerTool, Description(
        "Scaffold a complete, production-ready C# type (class, record, interface, struct, or enum) with fields, " +
        "properties, constructor, methods, attributes, base types, and XML doc comments from a JSON specification. " +
        "specJson schema: { kind, name, namespace, usings:[], modifiers:[], baseTypes:[], attributes:[], summary, " +
        "fields:[{name,type,isReadOnly}], properties:[{name,type,isReadOnly,isInitOnly,isRequired,defaultValue}], " +
        "generateConstructor:bool, methods:[{name,returnType,parameters:[{name,type}],isAsync,isStatic,body,attributes:[]}], enumMembers:[] }.")]
    public static string ScaffoldType(string specJson, string? outputPath = null)
    {
        var spec = RoslynHelpers.Des<TypeScaffoldSpec>(specJson, nameof(specJson));
        if (string.IsNullOrWhiteSpace(spec.Name) || !SyntaxFacts.IsValidIdentifier(spec.Name))
            throw new ArgumentException($"specJson must specify a valid C# identifier 'name' (got '{spec.Name}').");

        var kind = (spec.Kind ?? "class").Trim().ToLowerInvariant();
        if (kind is not ("class" or "record" or "interface" or "struct" or "enum"))
            throw new ArgumentException($"Unrecognized type kind '{kind}'. Expected: class, record, interface, struct, or enum.");

        var sb = new StringBuilder();
        var usings = (spec.Usings ?? ["System", "System.Collections.Generic", "System.Linq", "System.Threading.Tasks"])
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct()
            .OrderBy(u => u.StartsWith("System", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(u => u);

        foreach (var u in usings)
            sb.AppendLine($"using {u.Trim().TrimEnd(';')};");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(spec.Namespace))
        {
            sb.AppendLine($"namespace {spec.Namespace.Trim()};");
            sb.AppendLine();
        }

        AppendXmlSummary(sb, spec.Summary, "");
        AppendAttributes(sb, spec.Attributes, "");

        var mods = spec.Modifiers is { Length: > 0 } ? string.Join(" ", spec.Modifiers) : "public";
        var baseList = spec.BaseTypes is { Length: > 0 } ? " : " + string.Join(", ", spec.BaseTypes) : "";

        sb.AppendLine($"{mods} {kind} {spec.Name}{baseList}");
        sb.AppendLine("{");

        if (kind == "enum")
        {
            if (spec.EnumMembers is { Length: > 0 })
            {
                for (int i = 0; i < spec.EnumMembers.Length; i++)
                    sb.AppendLine($"    {spec.EnumMembers[i]}{(i < spec.EnumMembers.Length - 1 ? "," : "")}");
            }
        }
        else
        {
            if (spec.Fields is { Length: > 0 })
            {
                foreach (var f in spec.Fields)
                {
                    var fAccess = string.IsNullOrWhiteSpace(f.Access) ? "private" : f.Access;
                    var ro = f.IsReadOnly ? " readonly" : "";
                    var init = !string.IsNullOrWhiteSpace(f.DefaultValue) ? $" = {f.DefaultValue};" : ";";
                    sb.AppendLine($"    {fAccess}{ro} {f.Type ?? "object"} {f.Name}{init}");
                }
                sb.AppendLine();
            }

            if (spec.GenerateConstructor && kind is "class" or "struct" or "record")
            {
                var ctorTargets = new List<(string MemberName, string ParamName, string Type)>();
                if (spec.Fields is not null)
                {
                    foreach (var f in spec.Fields.Where(f => string.IsNullOrWhiteSpace(f.DefaultValue)))
                        ctorTargets.Add((f.Name, RoslynHelpers.ToCamelCaseIdentifier(f.Name), f.Type ?? "object"));
                }
                if (spec.Properties is not null)
                {
                    foreach (var p in spec.Properties.Where(p => p.IsReadOnly && string.IsNullOrWhiteSpace(p.DefaultValue)))
                        ctorTargets.Add((p.Name, RoslynHelpers.ToCamelCaseIdentifier(p.Name), p.Type ?? "object"));
                }

                var ctorParams = string.Join(", ", ctorTargets.Select(t => $"{t.Type} {t.ParamName}"));
                sb.AppendLine($"    public {spec.Name}({ctorParams})\n    {{");
                foreach (var t in ctorTargets)
                    sb.AppendLine($"        {(t.MemberName == t.ParamName ? $"this.{t.MemberName}" : t.MemberName)} = {t.ParamName};");
                sb.AppendLine("    }\n");
            }

            if (spec.Properties is { Length: > 0 })
            {
                foreach (var p in spec.Properties)
                {
                    AppendXmlSummary(sb, p.Summary, "    ");
                    AppendAttributes(sb, p.Attributes, "    ");
                    var pAccess = kind == "interface" ? "" : (string.IsNullOrWhiteSpace(p.Access) ? "public " : p.Access + " ");
                    var req = p.IsRequired && kind != "interface" ? "required " : "";
                    var setter = p.IsReadOnly ? "" : (p.IsInitOnly ? "init; " : "set; ");
                    var init = !string.IsNullOrWhiteSpace(p.DefaultValue) && kind != "interface" ? $" = {p.DefaultValue};" : "";
                    sb.AppendLine($"    {pAccess}{req}{p.Type ?? "string"} {p.Name} {{ get; {setter}}}{init}");
                }
                sb.AppendLine();
            }

            if (spec.Methods is { Length: > 0 })
            {
                foreach (var m in spec.Methods)
                {
                    AppendXmlSummary(sb, m.Summary, "    ");
                    AppendAttributes(sb, m.Attributes, "    ");
                    var pars = m.Parameters is { Length: > 0 }
                        ? string.Join(", ", m.Parameters.Select(p => $"{p.Type ?? "object"} {p.Name}"))
                        : "";
                    var ret = string.IsNullOrWhiteSpace(m.ReturnType) ? "void" : m.ReturnType;

                    if (kind == "interface")
                    {
                        sb.AppendLine($"    {ret} {m.Name}({pars});\n");
                    }
                    else
                    {
                        var mAccess = string.IsNullOrWhiteSpace(m.Access) ? "public" : m.Access;
                        var stat = m.IsStatic ? " static" : "";
                        var asy = m.IsAsync ? " async" : "";
                        sb.AppendLine($"    {mAccess}{stat}{asy} {ret} {m.Name}({pars})\n    {{");
                        var body = string.IsNullOrWhiteSpace(m.Body)
                            ? (ret == "void" ? "" : (m.IsAsync && ret == "Task" ? "await Task.CompletedTask;" : "throw new NotImplementedException();"))
                            : m.Body.Trim();
                        if (!string.IsNullOrEmpty(body))
                            sb.AppendLine($"        {body}");
                        sb.AppendLine("    }\n");
                    }
                }
            }
        }

        sb.AppendLine("}");

        var formatted = CSharpSyntaxTree.ParseText(sb.ToString()).GetRoot().FormatWithRoslyn();
        bool written = false;
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var fullOut = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullOut)!);
            File.WriteAllText(fullOut, formatted);
            written = true;
        }

        var (errors, warnings, diags) = WorkspaceEngine.GetCompilationDiagnostics(formatted);
        return RoslynHelpers.Ser(new
        {
            typeName = spec.Name,
            kind,
            savedToDisk = written,
            outputPath,
            errors,
            warnings,
            diagnostics = diags,
            code = formatted
        });
    }

    [McpServerTool, Description(
        "Apply N edits [{start,len,replacement}] to source in one call, back-to-front by offset so " +
        "edits don't invalidate each other. Overlapping ranges are rejected, not silently applied.")]
    public static string MultiReplace(string source, string editsJson)
    {
        var edits = RoslynHelpers.Des<SpanEditItem[]>(editsJson, nameof(editsJson));
        var ordered = edits.OrderByDescending(e => e.Start).ToArray();
        for (int i = 0; i < ordered.Length - 1; i++)
        {
            var cur = ordered[i];
            var prev = ordered[i + 1];
            if (prev.Start + prev.Len > cur.Start)
                throw new ArgumentException($"overlapping edits at {prev.Start} and {cur.Start}");
        }
        var text = source;
        foreach (var e in ordered)
        {
            if (e.Start < 0 || e.Len < 0 || e.Start + e.Len > text.Length)
                throw new ArgumentException($"edit span [{e.Start},{e.Start + e.Len}) out of bounds");
            text = text[..e.Start] + (e.Replacement ?? "") + text[(e.Start + e.Len)..];
        }
        return RoslynHelpers.Ser(new { text, editsApplied = ordered.Length });
    }

    [McpServerTool, Description(
        "Depth-bounded structural outline of a file or source: namespaces, types, constructors, properties, fields, " +
        "events, enum members, and methods with signatures and line spans (no bodies). maxDepth default 4.")]
    public static string TreeSummary(string source, int maxDepth = 4)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("source cannot be empty", nameof(source));
        if (maxDepth < 0)
            throw new ArgumentException("maxDepth must be >= 0", nameof(maxDepth));

        string text = File.Exists(source) ? File.ReadAllText(source) : source;
        var tree = CSharpSyntaxTree.ParseText(text);
        var root = tree.GetRoot();

        object Walk(SyntaxNode n, int depth)
        {
            var lr = tree.LineRange(n.Span);
            var (name, signature) = n switch
            {
                BaseNamespaceDeclarationSyntax ns => (ns.Name.ToString(), $"namespace {ns.Name}"),
                TypeDeclarationSyntax t => (t.Identifier.Text, $"{t.Modifiers} {t.Keyword.Text} {t.Identifier.Text}{t.TypeParameterList}{t.BaseList}".Trim()),
                EnumDeclarationSyntax e => (e.Identifier.Text, $"{e.Modifiers} enum {e.Identifier.Text}".Trim()),
                EnumMemberDeclarationSyntax em => (em.Identifier.Text, em.ToString().Trim()),
                DelegateDeclarationSyntax d => (d.Identifier.Text, d.ToString().Trim()),
                ConstructorDeclarationSyntax c => (c.Identifier.Text, $"{c.Modifiers} {c.Identifier.Text}{c.ParameterList}".Trim()),
                MethodDeclarationSyntax m => (m.Identifier.Text, $"{m.Modifiers} {m.ReturnType} {m.Identifier.Text}{m.TypeParameterList}{m.ParameterList}".Trim()),
                PropertyDeclarationSyntax p => (p.Identifier.Text, $"{p.Modifiers} {p.Type} {p.Identifier.Text}".Trim()),
                FieldDeclarationSyntax f => (
                    string.Join(",", f.Declaration.Variables.Select(v => v.Identifier.Text)),
                    $"{f.Modifiers} {f.Declaration.Type} {string.Join(", ", f.Declaration.Variables.Select(v => v.Identifier.Text))}".Trim()),
                EventFieldDeclarationSyntax ef => (
                    string.Join(",", ef.Declaration.Variables.Select(v => v.Identifier.Text)),
                    ef.ToString().Trim()),
                EventDeclarationSyntax ev => (ev.Identifier.Text, $"{ev.Modifiers} event {ev.Type} {ev.Identifier.Text}".Trim()),
                _ => (n.Kind().ToString(), n.Kind().ToString())
            };

            var children = depth >= maxDepth ? Array.Empty<object>()
                : n.ChildNodes()
                    .Where(c => c is BaseNamespaceDeclarationSyntax or BaseTypeDeclarationSyntax
                        or MethodDeclarationSyntax or ConstructorDeclarationSyntax
                        or PropertyDeclarationSyntax or FieldDeclarationSyntax
                        or EventDeclarationSyntax or EventFieldDeclarationSyntax
                        or EnumMemberDeclarationSyntax or DelegateDeclarationSyntax)
                    .Select(c => Walk(c, depth + 1)).ToArray();

            return new { kind = n.Kind().ToString(), name, signature, line = lr.StartLine, endLine = lr.EndLine, children };
        }
        return RoslynHelpers.Ser(Walk(root, 0));
    }

    [McpServerTool, Description(
        "Enclosing syntactic and semantic ancestry chain for an offset: " +
        "e.g. parameter x of method Foo of class Bar of namespace Baz, plus resolved Roslyn symbol info.")]
    public static string Explain(string source, int position)
    {
        using var ctx = WorkspaceEngine.Load(source);
        var doc = ctx.PrimaryDocument;
        var root = doc.Tree.GetRoot();
        var text = doc.Source;

        if (position < 0 || position > text.Length)
            throw new ArgumentException("position out of bounds");

        var node = root.FindNode(new TextSpan(position, 0));
        var chain = new List<string>();
        for (var n = node; n is not null; n = n.Parent)
        {
            var desc = n switch
            {
                BaseNamespaceDeclarationSyntax ns => $"namespace {ns.Name}",
                TypeDeclarationSyntax t => $"{t.Keyword.Text} {t.Identifier.Text}",
                EnumDeclarationSyntax e => $"{e.EnumKeyword.Text} {e.Identifier.Text}",
                BaseTypeDeclarationSyntax bt => $"{bt.Kind().ToString().Replace("Declaration", "").ToLowerInvariant()} {bt.Identifier.Text}",
                ConstructorDeclarationSyntax c => $"constructor {c.Identifier.Text}{c.ParameterList}",
                MethodDeclarationSyntax m => $"method {m.Identifier.Text}",
                LocalFunctionStatementSyntax lf => $"local function {lf.Identifier.Text}",
                ParameterSyntax p => $"parameter {p.Identifier.Text}",
                PropertyDeclarationSyntax p => $"property {p.Identifier.Text}",
                FieldDeclarationSyntax f => $"field {string.Join(", ", f.Declaration.Variables.Select(v => v.Identifier.Text))}",
                CatchClauseSyntax cc => $"catch ({cc.Declaration?.Type})",
                ForEachStatementSyntax fe => $"foreach ({fe.Identifier.Text} in {fe.Expression})",
                _ => null
            };
            if (desc is not null) chain.Add(desc);
        }
        chain.Reverse();

        var sym = doc.Model.ResolveSymbol(node);
        var typeInfo = doc.Model.GetTypeInfo(node).Type;
        var lr = doc.Tree.LineRange(new TextSpan(position, 0));

        return RoslynHelpers.Ser(new
        {
            position,
            line = lr.StartLine,
            column = lr.StartColumn,
            syntaxKind = node.Kind().ToString(),
            chain,
            symbol = sym?.MinDisplay(),
            symbolKind = sym?.Kind.ToString(),
            inferredType = typeInfo?.MinDisplay()
        });
    }

    [McpServerTool, Description(
        "Query syntax nodes across a file, project, or codebase with rich structural and semantic filters: " +
        "syntaxKinds: comma-separated SyntaxKind names (e.g. 'InvocationExpression,ObjectCreationExpression,AwaitExpression'). " +
        "textPattern: regex or substring to match against node text. " +
        "ancestorKind: require an enclosing ancestor of this SyntaxKind (e.g. 'CatchClause', 'ForEachStatement', 'LockStatement').")]
    public static string QuerySyntaxTree(
        string sourceOrPath,
        string? syntaxKinds = null,
        string? textPattern = null,
        string? ancestorKind = null)
    {
        using var ctx = WorkspaceEngine.Load(sourceOrPath);

        var targetKinds = new HashSet<SyntaxKind>();
        if (!string.IsNullOrWhiteSpace(syntaxKinds))
        {
            foreach (var part in syntaxKinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Enum.TryParse<SyntaxKind>(part, ignoreCase: true, out var k))
                    targetKinds.Add(k);
                else if (Enum.TryParse<SyntaxKind>(part + "Syntax", ignoreCase: true, out var ks))
                    targetKinds.Add(ks);
                else
                    throw new ArgumentException($"Unrecognized SyntaxKind '{part}' in syntaxKinds.");
            }
        }

        SyntaxKind? requiredAncestor = null;
        if (!string.IsNullOrWhiteSpace(ancestorKind))
        {
            if (!Enum.TryParse<SyntaxKind>(ancestorKind.Trim(), ignoreCase: true, out var ak))
                throw new ArgumentException($"Unrecognized ancestor SyntaxKind '{ancestorKind}'.");
            requiredAncestor = ak;
        }

        var textRegex = string.IsNullOrWhiteSpace(textPattern) ? null : RoslynHelpers.CompileQueryRegex(textPattern, allowGlob: false);
        var results = new List<object>();

        foreach (var doc in ctx.UserDocuments)
        {
            foreach (var node in doc.Tree.GetRoot().DescendantNodes())
            {
                if (targetKinds.Count > 0 && !targetKinds.Contains(node.Kind()))
                    continue;
                if (requiredAncestor is not null && !node.Ancestors().Any(a => a.IsKind(requiredAncestor.Value)))
                    continue;

                var nodeStr = node.ToString();
                if (textRegex is not null && !textRegex.IsMatch(nodeStr))
                    continue;

                var sym = doc.Model.ResolveSymbol(node);
                var typeSym = doc.Model.GetTypeInfo(node).Type;
                var enclosingMethod = node.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
                var enclosingType = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
                var lr = node.LineRange();

                results.Add(new
                {
                    file = doc.Name,
                    kind = node.Kind().ToString(),
                    startLine = lr.StartLine,
                    endLine = lr.EndLine,
                    spanStart = node.SpanStart,
                    spanLength = node.Span.Length,
                    enclosingType = enclosingType?.Identifier.ValueText,
                    enclosingMethod = (enclosingMethod as MethodDeclarationSyntax)?.Identifier.ValueText,
                    symbol = sym?.MinDisplay(),
                    expressionType = typeSym?.MinDisplay(),
                    text = node.PreviewText(220)
                });

                if (results.Count >= 100) break;
            }
            if (results.Count >= 100) break;
        }

        return RoslynHelpers.Ser(new { count = results.Count, matches = results });
    }

    private static void AppendXmlSummary(StringBuilder sb, string? summary, string indent)
    {
        if (string.IsNullOrWhiteSpace(summary)) return;
        sb.AppendLine($"{indent}/// <summary>\n{indent}/// {summary.Trim()}\n{indent}/// </summary>");
    }

    private static void AppendAttributes(StringBuilder sb, string[]? attributes, string indent)
    {
        if (attributes is null) return;
        foreach (var attr in attributes.Where(a => !string.IsNullOrWhiteSpace(a)))
            sb.AppendLine($"{indent}[{attr.Trim().Trim('[', ']')}]");
    }

    [DebuggerDisplay("{ToString(),nq}")]
    private sealed record SpanEditItem(int Start, int Len, string? Replacement)
    {
        public override string ToString() => $"SpanEdit([{Start}..{Start + Len}] -> '{Replacement}')";
    }

    [DebuggerDisplay("{ToString(),nq}")]
    private sealed class TypeScaffoldSpec
    {
        public string? Kind { get; set; }
        public string Name { get; set; } = "";
        public string? Namespace { get; set; }
        public string[]? Usings { get; set; }
        public string[]? Modifiers { get; set; }
        public string[]? BaseTypes { get; set; }
        public string[]? Attributes { get; set; }
        public string? Summary { get; set; }
        public FieldSpec[]? Fields { get; set; }
        public PropertySpec[]? Properties { get; set; }
        public bool GenerateConstructor { get; set; }
        public MethodSpec[]? Methods { get; set; }
        public string[]? EnumMembers { get; set; }

        public override string ToString() =>
            $"TypeScaffoldSpec({Kind ?? "class"} {Namespace}.{Name}, fields={Fields?.Length ?? 0}, props={Properties?.Length ?? 0}, methods={Methods?.Length ?? 0})";
    }

    [DebuggerDisplay("{ToString(),nq}")]
    private sealed class FieldSpec
    {
        public string Name { get; set; } = "";
        public string? Type { get; set; }
        public string? Access { get; set; }
        public bool IsReadOnly { get; set; } = true;
        public string? DefaultValue { get; set; }

        public override string ToString() => $"FieldSpec({Access ?? "private"} {(IsReadOnly ? "readonly " : "")}{Type ?? "object"} {Name})";
    }

    [DebuggerDisplay("{ToString(),nq}")]
    private sealed class PropertySpec
    {
        public string Name { get; set; } = "";
        public string? Type { get; set; }
        public string? Access { get; set; }
        public bool IsReadOnly { get; set; }
        public bool IsInitOnly { get; set; }
        public bool IsRequired { get; set; }
        public string? DefaultValue { get; set; }
        public string? Summary { get; set; }
        public string[]? Attributes { get; set; }

        public override string ToString() => $"PropertySpec({Access ?? "public"} {Type ?? "string"} {Name})";
    }

    [DebuggerDisplay("{ToString(),nq}")]
    private sealed class MethodSpec
    {
        public string Name { get; set; } = "";
        public string? ReturnType { get; set; }
        public string? Access { get; set; }
        public bool IsStatic { get; set; }
        public bool IsAsync { get; set; }
        public string? Body { get; set; }
        public string? Summary { get; set; }
        public string[]? Attributes { get; set; }
        public ParameterSpec[]? Parameters { get; set; }

        public override string ToString() =>
            $"MethodSpec({ReturnType ?? "void"} {Name}({string.Join(", ", Parameters?.Select(p => p.ToString()) ?? Array.Empty<string>())}))";
    }

    [DebuggerDisplay("{ToString(),nq}")]
    private sealed class ParameterSpec
    {
        public string Name { get; set; } = "";
        public string? Type { get; set; }

        public override string ToString() => $"{Type ?? "object"} {Name}";
    }
}
