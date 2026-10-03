using System.Text.Json;
using System.Text.RegularExpressions;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Humanizer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using ModelContextProtocol.Server;

namespace RoslynMcp;

[McpServerToolType]
internal static class Tools
{
    static SyntaxTree T(string s) => CSharpSyntaxTree.ParseText(s ?? throw new ArgumentNullException(nameof(s)));

    [McpServerTool, System.ComponentModel.Description("Parse C# source, return diagnostics.")]
    public static string Parse(string source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        var d = T(source).GetDiagnostics().ToArray();
        return RoslynHelpers.Ser(new
        {
            ok = !d.Any(x => x.Severity == DiagnosticSeverity.Error),
            tokens = RoslynHelpers.Tok(source),
            diagnostics = d.Select(x => new
            {
                sev = x.Severity.ToString(),
                msg = x.GetMessage(),
                start = x.Location.SourceSpan.Start,
                len = x.Location.SourceSpan.Length,
                line = x.Location.GetLineSpan().StartLinePosition.Line + 1
            })
        });
    }

    [McpServerTool, System.ComponentModel.Description("Find nodes by SyntaxKind name.")]
    public static string FindNodes(string source, string kind)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (!Enum.TryParse<SyntaxKind>(kind, ignoreCase: true, out var k))
            throw new ArgumentException($"unrecognized SyntaxKind: {kind}");
        var tree = T(source);
        var m = tree.GetRoot().DescendantNodes().Where(n => n.IsKind(k))
            .Select(n => new
            {
                kind = n.Kind().ToString(),
                start = n.Span.Start,
                len = n.Span.Length,
                line = n.LineRange().StartLine,
                text = n.PreviewText(200)
            })
            .ToArray();
        return RoslynHelpers.Ser(new { count = m.Length, matches = m });
    }

    [McpServerTool, System.ComponentModel.Description("Splice [start,start+len) with replacement, diffed.")]
    public static string ReplaceNode(string source, int start, int len, string replacement)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (start < 0 || len < 0 || start + len > source.Length)
            throw new ArgumentException("span out of bounds");
        var nt = source[..start] + (replacement ?? "") + source[(start + len)..];
        var eb = T(source).GetDiagnostics().Count(x => x.Severity == DiagnosticSeverity.Error);
        var ea = T(nt).GetDiagnostics().Count(x => x.Severity == DiagnosticSeverity.Error);
        var diff = InlineDiffBuilder.Diff(source, nt);
        return RoslynHelpers.Ser(new
        {
            text = nt,
            newErrors = ea > eb,
            errBefore = eb,
            errAfter = ea,
            diff = diff.Lines.Where(l => l.Type != ChangeType.Unchanged)
                .Select(l => new { type = l.Type.ToString(), text = l.Text })
        });
    }

    [McpServerTool, System.ComponentModel.Description("Format via Roslyn defaults.")]
    public static string Format(string source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        return T(source).GetRoot().FormatWithRoslyn();
    }

    [McpServerTool, System.ComponentModel.Description("Compile+run C# snippet with standard .NET imports and Console stdout capture. Unsandboxed.")]
    public static async Task<string> Execute(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("code cannot be empty.", nameof(code));

        var prevOut = Console.Out;
        using var sw = new StringWriter();
        try
        {
            Console.SetOut(sw);
            var options = ScriptOptions.Default
                .AddReferences(
                    typeof(object).Assembly,
                    typeof(Console).Assembly,
                    typeof(Enumerable).Assembly,
                    typeof(List<>).Assembly,
                    typeof(JsonSerializer).Assembly,
                    typeof(Regex).Assembly,
                    typeof(SyntaxNode).Assembly,
                    typeof(CSharpSyntaxTree).Assembly)
                .AddImports(
                    "System",
                    "System.IO",
                    "System.Linq",
                    "System.Text",
                    "System.Text.Json",
                    "System.Text.RegularExpressions",
                    "System.Collections.Generic",
                    "System.Threading.Tasks",
                    "Microsoft.CodeAnalysis",
                    "Microsoft.CodeAnalysis.CSharp",
                    "Microsoft.CodeAnalysis.CSharp.Syntax");

            var r = await CSharpScript.RunAsync(code, options);
            var stdout = sw.ToString();
            return RoslynHelpers.Ser(new
            {
                ok = true,
                result = r.ReturnValue?.ToString(),
                stdout = string.IsNullOrEmpty(stdout) ? null : stdout
            });
        }
        catch (CompilationErrorException e)
        {
            return RoslynHelpers.Ser(new { ok = false, errors = e.Diagnostics.Select(d => d.ToString()) });
        }
        catch (Exception e)
        {
            return RoslynHelpers.Ser(new { ok = false, error = e.Message });
        }
        finally
        {
            Console.SetOut(prevOut);
        }
    }

    [McpServerTool, System.ComponentModel.Description("Compile C# source against full .NET 8 runtime references and return semantic/compiler diagnostics.")]
    public static Task<string> Analyze(string source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        var tree = T(source);
        var comp = CSharpCompilation.Create(
            "_",
            new[] { tree },
            WorkspaceEngine.DefaultReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));

        var diags = comp.GetDiagnostics()
            .Where(d => d.Location.SourceTree == tree && d.Severity >= DiagnosticSeverity.Warning)
            .ToArray();

        return Task.FromResult(RoslynHelpers.Ser(new
        {
            count = diags.Length,
            summary = "diagnostic".ToQuantity(diags.Length),
            diagnostics = diags.Select(d => new
            {
                id = d.Id,
                sev = d.Severity.ToString(),
                line = d.Location.GetLineSpan().StartLinePosition.Line + 1,
                msg = d.GetMessage()
            })
        }));
    }

    [McpServerTool, System.ComponentModel.Description("Token count, cl100k_base approximation.")]
    public static string CountTokens(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        return RoslynHelpers.Ser(new { tokens = RoslynHelpers.Tok(text), chars = text.Length });
    }

    [McpServerTool, System.ComponentModel.Description(
        "Define a reusable workflow: ordered tool-call steps with {{param}} substitution. " +
        "stepsJson: JSON array of {tool, args}. args keys MUST match the target " +
        "tool method's C# parameter names (optional parameters with defaults may be omitted). " +
        "paramsJson: JSON array of param names usable as {{name}} in any step's args (plus {{$prev}} for previous step output). " +
        "If any step targets Execute, returns a pending confirmationToken instead of activating — " +
        "call ConfirmWorkflow(token) to activate.")]
    public static string DefineWorkflow(string name, string stepsJson, string paramsJson)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Workflow name cannot be empty.", nameof(name));
        var steps = RoslynHelpers.Des<WorkflowStep[]>(stepsJson, nameof(stepsJson));
        if (steps.Length == 0)
            throw new ArgumentException("stepsJson must contain at least one step.", nameof(stepsJson));
        var pars = string.IsNullOrWhiteSpace(paramsJson)
            ? Array.Empty<string>()
            : RoslynHelpers.Des<string[]>(paramsJson, nameof(paramsJson));

        var outcome = WorkflowStore.Stage(name, pars, steps);
        return outcome == "active"
            ? RoslynHelpers.Ser(new { status = "active", name })
            : RoslynHelpers.Ser(new
            {
                status = "pending_confirmation",
                name,
                confirmationToken = outcome,
                note = "workflow contains Execute; call ConfirmWorkflow to activate"
            });
    }

    [McpServerTool, System.ComponentModel.Description("Activate a pending workflow (one containing Execute) by its confirmation token.")]
    public static string ConfirmWorkflow(string token)
    {
        var w = WorkflowStore.Confirm(token);
        return RoslynHelpers.Ser(new { status = "active", name = w.Name });
    }

    [McpServerTool, System.ComponentModel.Description("List all active (invokable) workflows with their required params and step tools.")]
    public static string ListWorkflows() => RoslynHelpers.Ser(new
    {
        workflows = WorkflowStore.ListActive().Select(w => new
        {
            w.Name,
            w.Params,
            steps = w.Steps.Select(s => s.Tool),
            containsExecute = w.HasExecute
        })
    });

    [McpServerTool, System.ComponentModel.Description(
        "Run an active workflow. paramValuesJson: JSON object mapping param names to values. " +
        "Executes steps in order, halts and reports on first step failure.")]
    public static string RunWorkflow(string name, string paramValuesJson)
    {
        var w = WorkflowStore.Get(name);
        var vals = RoslynHelpers.Des<Dictionary<string, string>>(paramValuesJson, nameof(paramValuesJson));
        return WorkflowEngine.Run(w, vals);
    }
}
