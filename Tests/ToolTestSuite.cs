using System.Diagnostics;
using System.Text.Json;
using System.Xml;

namespace RoslynMcp.Tests;

internal static class ToolTestSuite
{
    public static async Task<TestSuiteReport> RunAllAsync(string? methodFilter = null)
    {
        var allCases = BuildAllTestCases();
        if (!string.IsNullOrWhiteSpace(methodFilter))
        {
            allCases = allCases
                .Where(c => c.MethodName.Contains(methodFilter.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var results = new List<TestResult>(allCases.Count);
        var overallSw = Stopwatch.StartNew();

        foreach (var tc in allCases)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await tc.ExecuteAsync();
                sw.Stop();
                results.Add(new TestResult(tc.MethodName, tc.Category, tc.Description, true, sw.ElapsedMilliseconds));
            }
            catch (Exception ex)
            {
                sw.Stop();
                results.Add(new TestResult(tc.MethodName, tc.Category, tc.Description, false, sw.ElapsedMilliseconds, ex.Message));
            }
        }

        overallSw.Stop();

        var coverage = results
            .GroupBy(r => r.MethodName)
            .OrderBy(g => g.Key)
            .Select(g => new MethodCoverageReport(
                MethodName: g.Key,
                TotalTests: g.Count(),
                PassedTests: g.Count(r => r.Passed),
                HasBasic: g.Any(r => r.Category == TestCategory.Basic),
                HasAdvanced: g.Any(r => r.Category == TestCategory.Advanced),
                HasEdgeCase: g.Any(r => r.Category == TestCategory.EdgeCase),
                HasFailureCase: g.Any(r => r.Category == TestCategory.FailureCase)))
            .ToArray();

        return new TestSuiteReport(
            TotalMethods: coverage.Length,
            TotalTests: results.Count,
            PassedTests: results.Count(r => r.Passed),
            FailedTests: results.Count(r => !r.Passed),
            TotalDurationMs: overallSw.ElapsedMilliseconds,
            MethodCoverage: coverage,
            Results: results.ToArray());
    }

    public static List<TestCase> BuildAllTestCases()
    {
        var cases = new List<TestCase>();

        void Add(string method, TestCategory cat, string desc, Action syncTest) =>
            cases.Add(new TestCase(method, cat, desc, () => { syncTest(); return Task.CompletedTask; }));

        void AddAsync(string method, TestCategory cat, string desc, Func<Task> asyncTest) =>
            cases.Add(new TestCase(method, cat, desc, asyncTest));

        // =========================================================================
        // 1. Tools.Parse
        // =========================================================================
        Add(nameof(Tools.Parse), TestCategory.Basic, "Parse valid class with zero errors", () =>
        {
            var json = AssertEx.ParseJson(Tools.Parse("namespace Demo; public class Calculator { public int Add(int a, int b) => a + b; }"));
            AssertEx.True(json.GetProperty("ok").GetBoolean());
            AssertEx.Equal(0, json.GetProperty("diagnostics").GetArrayLength());
        });
        Add(nameof(Tools.Parse), TestCategory.Advanced, "Parse C# 12 primary constructor and collection expression", () =>
        {
            var src = "namespace Modern;\npublic class Box(int seed) { public int[] Items => [seed, seed + 1]; }";
            var json = AssertEx.ParseJson(Tools.Parse(src));
            AssertEx.True(json.GetProperty("ok").GetBoolean());
            AssertEx.True(json.GetProperty("tokens").GetInt32() > 5);
        });
        Add(nameof(Tools.Parse), TestCategory.EdgeCase, "Parse empty and comment-only source", () =>
        {
            var json = AssertEx.ParseJson(Tools.Parse("/* comment only */"));
            AssertEx.True(json.GetProperty("ok").GetBoolean());
            var invalidSyntax = AssertEx.ParseJson(Tools.Parse("public class Broken {"));
            AssertEx.False(invalidSyntax.GetProperty("ok").GetBoolean());
        });
        Add(nameof(Tools.Parse), TestCategory.FailureCase, "Null source throws ArgumentNullException", () =>
        {
            AssertEx.Throws<ArgumentNullException>(() => Tools.Parse(null!));
        });

        // =========================================================================
        // 2. Tools.FindNodes
        // =========================================================================
        Add(nameof(Tools.FindNodes), TestCategory.Basic, "Find MethodDeclaration nodes in class", () =>
        {
            var src = "class C { void A() {} void B() {} }";
            var json = AssertEx.ParseJson(Tools.FindNodes(src, "MethodDeclaration"));
            AssertEx.Equal(2, json.GetProperty("count").GetInt32());
        });
        Add(nameof(Tools.FindNodes), TestCategory.Advanced, "Find InvocationExpression nodes case-insensitively with line numbers", () =>
        {
            var src = "using System;\nclass C {\n  void M() {\n    Console.WriteLine(Math.Max(1, 2));\n  }\n}";
            var json = AssertEx.ParseJson(Tools.FindNodes(src, "invocationexpression"));
            AssertEx.Equal(2, json.GetProperty("count").GetInt32());
            AssertEx.Equal(4, json.GetProperty("matches")[0].GetProperty("line").GetInt32());
        });
        Add(nameof(Tools.FindNodes), TestCategory.EdgeCase, "Find absent SyntaxKind returns 0 matches and truncates >200 char nodes", () =>
        {
            var longBody = "class C { void M() { var s = \"" + new string('x', 240) + "\"; } }";
            var empty = AssertEx.ParseJson(Tools.FindNodes(longBody, "GotoStatement"));
            AssertEx.Equal(0, empty.GetProperty("count").GetInt32());
            var truncated = AssertEx.ParseJson(Tools.FindNodes(longBody, "ClassDeclaration"));
            AssertEx.Contains("…", truncated.GetProperty("matches")[0].GetProperty("text").GetString()!);
        });
        Add(nameof(Tools.FindNodes), TestCategory.FailureCase, "Unrecognized SyntaxKind throws ArgumentException", () =>
        {
            AssertEx.Throws<ArgumentException>(() => Tools.FindNodes("class C {}", "NotASyntaxKind"), "unrecognized SyntaxKind");
        });

        // =========================================================================
        // 3. Tools.ReplaceNode
        // =========================================================================
        Add(nameof(Tools.ReplaceNode), TestCategory.Basic, "Replace literal inside source and compute diff", () =>
        {
            var src = "class C { int X = 1; }";
            int idx = src.IndexOf('1');
            var json = AssertEx.ParseJson(Tools.ReplaceNode(src, idx, 1, "42"));
            AssertEx.Contains("int X = 42;", json.GetProperty("text").GetString()!);
            AssertEx.False(json.GetProperty("newErrors").GetBoolean());
        });
        Add(nameof(Tools.ReplaceNode), TestCategory.Advanced, "Replace method implementation and track error delta", () =>
        {
            var src = "public class Calc {\n    public int Run() => 0;\n}";
            int start = src.IndexOf("=> 0;");
            var json = AssertEx.ParseJson(Tools.ReplaceNode(src, start, 5, "{ return 10 + 20; }"));
            AssertEx.Equal(0, json.GetProperty("errAfter").GetInt32());
            AssertEx.True(json.GetProperty("diff").GetArrayLength() > 0);
        });
        Add(nameof(Tools.ReplaceNode), TestCategory.EdgeCase, "Zero-length insertion at start of file", () =>
        {
            var src = "class C {}";
            var json = AssertEx.ParseJson(Tools.ReplaceNode(src, 0, 0, "using System;\n"));
            AssertEx.Contains("using System;", json.GetProperty("text").GetString()!);
        });
        Add(nameof(Tools.ReplaceNode), TestCategory.FailureCase, "Out-of-bounds span throws ArgumentException", () =>
        {
            AssertEx.Throws<ArgumentException>(() => Tools.ReplaceNode("class C {}", 0, 50, "x"), "out of bounds");
            AssertEx.Throws<ArgumentException>(() => Tools.ReplaceNode("class C {}", -1, 2, "x"), "out of bounds");
        });

        // =========================================================================
        // 4. Tools.Format
        // =========================================================================
        Add(nameof(Tools.Format), TestCategory.Basic, "Format compact single-line class into indented lines", () =>
        {
            var formatted = Tools.Format("namespace N{public class C{public int X{get;set;}}}");
            AssertEx.Contains("\n", formatted);
            AssertEx.Contains("    public int X", formatted);
        });
        Add(nameof(Tools.Format), TestCategory.Advanced, "Format complex nested switch and control flow", () =>
        {
            var raw = "class C{int M(int x){if(x>0){return x switch{1=>10,_=>20};}return 0;}}";
            var formatted = Tools.Format(raw);
            AssertEx.Contains("return x switch", formatted);
        });
        Add(nameof(Tools.Format), TestCategory.EdgeCase, "Format empty string returns empty string", () =>
        {
            AssertEx.Equal("", Tools.Format(""));
        });
        Add(nameof(Tools.Format), TestCategory.FailureCase, "Null source throws ArgumentNullException", () =>
        {
            AssertEx.Throws<ArgumentNullException>(() => Tools.Format(null!));
        });

        // =========================================================================
        // 5. Tools.Execute
        // =========================================================================
        AddAsync(nameof(Tools.Execute), TestCategory.Basic, "Execute arithmetic expression and return result", async () =>
        {
            var json = AssertEx.ParseJson(await Tools.Execute("6 * 7"));
            AssertEx.True(json.GetProperty("ok").GetBoolean());
            AssertEx.Equal("42", json.GetProperty("result").GetString());
        });
        AddAsync(nameof(Tools.Execute), TestCategory.Advanced, "Execute LINQ + Console.WriteLine capturing stdout and return value", async () =>
        {
            var code = "Console.Write(\"step1\"); return Enumerable.Range(1, 4).Sum();";
            var json = AssertEx.ParseJson(await Tools.Execute(code));
            AssertEx.True(json.GetProperty("ok").GetBoolean());
            AssertEx.Equal("10", json.GetProperty("result").GetString());
            AssertEx.Equal("step1", json.GetProperty("stdout").GetString());
        });
        AddAsync(nameof(Tools.Execute), TestCategory.EdgeCase, "Execute statement with no return value produces null result", async () =>
        {
            var json = AssertEx.ParseJson(await Tools.Execute("int a = 10;"));
            AssertEx.True(json.GetProperty("ok").GetBoolean());
            AssertEx.Equal(JsonValueKind.Null, json.GetProperty("result").ValueKind);
        });
        AddAsync(nameof(Tools.Execute), TestCategory.FailureCase, "Empty code throws ArgumentException and invalid script returns ok=false", async () =>
        {
            await AssertEx.ThrowsAsync<ArgumentException>(() => Tools.Execute("   "));
            var compileErr = AssertEx.ParseJson(await Tools.Execute("int x = \"notAnInt\";"));
            AssertEx.False(compileErr.GetProperty("ok").GetBoolean());
        });

        // =========================================================================
        // 6. Tools.Analyze
        // =========================================================================
        AddAsync(nameof(Tools.Analyze), TestCategory.Basic, "Analyze valid C# using System.Linq and Collections with zero errors", async () =>
        {
            var src = "using System.Linq; using System.Collections.Generic; public class C { public List<int> Get() => new List<int>{1,2}.Where(x => x > 0).ToList(); }";
            var json = AssertEx.ParseJson(await Tools.Analyze(src));
            AssertEx.Equal(0, json.GetProperty("count").GetInt32());
        });
        AddAsync(nameof(Tools.Analyze), TestCategory.Advanced, "Analyze code with type mismatch error and verify diagnostic ID and line", async () =>
        {
            var src = "public class C {\n  public int Bad() {\n    return \"wrong\";\n  }\n}";
            var json = AssertEx.ParseJson(await Tools.Analyze(src));
            AssertEx.True(json.GetProperty("count").GetInt32() > 0);
            AssertEx.Equal("CS0029", json.GetProperty("diagnostics")[0].GetProperty("id").GetString());
        });
        AddAsync(nameof(Tools.Analyze), TestCategory.EdgeCase, "Analyze code with unused variable warning CS0219", async () =>
        {
            var src = "public class C { public void M() { int unused = 5; } }";
            var json = AssertEx.ParseJson(await Tools.Analyze(src));
            AssertEx.Equal("Warning", json.GetProperty("diagnostics")[0].GetProperty("sev").GetString());
        });
        AddAsync(nameof(Tools.Analyze), TestCategory.FailureCase, "Null source throws ArgumentNullException", async () =>
        {
            await AssertEx.ThrowsAsync<ArgumentNullException>(() => Tools.Analyze(null!));
        });

        // =========================================================================
        // 7. Tools.CountTokens
        // =========================================================================
        Add(nameof(Tools.CountTokens), TestCategory.Basic, "Count cl100k_base tokens on C# snippet", () =>
        {
            var json = AssertEx.ParseJson(Tools.CountTokens("public class Foo { }"));
            AssertEx.True(json.GetProperty("tokens").GetInt32() > 0);
            AssertEx.Equal(20, json.GetProperty("chars").GetInt32());
        });
        Add(nameof(Tools.CountTokens), TestCategory.Advanced, "Count tokens on multi-line file", () =>
        {
            var src = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"public int Prop{i} {{ get; set; }}"));
            var json = AssertEx.ParseJson(Tools.CountTokens(src));
            AssertEx.Equal(src.Length, json.GetProperty("chars").GetInt32());
            AssertEx.True(json.GetProperty("tokens").GetInt32() > 40);
        });
        Add(nameof(Tools.CountTokens), TestCategory.EdgeCase, "Count tokens on empty string returns 0", () =>
        {
            var json = AssertEx.ParseJson(Tools.CountTokens(""));
            AssertEx.Equal(0, json.GetProperty("tokens").GetInt32());
            AssertEx.Equal(0, json.GetProperty("chars").GetInt32());
        });
        Add(nameof(Tools.CountTokens), TestCategory.FailureCase, "Null text throws ArgumentNullException", () =>
        {
            AssertEx.Throws<ArgumentNullException>(() => Tools.CountTokens(null!));
        });

        // =========================================================================
        // 8. Tools.DefineWorkflow
        // =========================================================================
        Add(nameof(Tools.DefineWorkflow), TestCategory.Basic, "Define active workflow without Execute step", () =>
        {
            WorkflowStore.ClearInMemoryForTesting();
            var steps = "[{\"Tool\":\"Parse\",\"Args\":{\"source\":\"{{code}}\"}}]";
            var json = AssertEx.ParseJson(Tools.DefineWorkflow("wf_basic", steps, "[\"code\"]"));
            AssertEx.Equal("active", json.GetProperty("status").GetString());
        });
        Add(nameof(Tools.DefineWorkflow), TestCategory.Advanced, "Define multi-step workflow spanning CreativeTools and AnalysisTools", () =>
        {
            WorkflowStore.ClearInMemoryForTesting();
            var steps = "[{\"Tool\":\"Format\",\"Args\":{\"source\":\"{{src}}\"}},{\"Tool\":\"CompileAndDiagnose\",\"Args\":{\"sourceOrPath\":\"{{$prev}}\"}}]";
            var json = AssertEx.ParseJson(Tools.DefineWorkflow("wf_multi", steps, "[\"src\"]"));
            AssertEx.Equal("active", json.GetProperty("status").GetString());
        });
        Add(nameof(Tools.DefineWorkflow), TestCategory.EdgeCase, "Workflow containing Execute step requires confirmation token", () =>
        {
            WorkflowStore.ClearInMemoryForTesting();
            var steps = "[{\"Tool\":\"Execute\",\"Args\":{\"code\":\"1+1\"}}]";
            var json = AssertEx.ParseJson(Tools.DefineWorkflow("wf_exec", steps, "[]"));
            AssertEx.Equal("pending_confirmation", json.GetProperty("status").GetString());
            AssertEx.Equal(12, json.GetProperty("confirmationToken").GetString()!.Length);
        });
        Add(nameof(Tools.DefineWorkflow), TestCategory.FailureCase, "Duplicate workflow name or empty steps throws exception", () =>
        {
            WorkflowStore.ClearInMemoryForTesting();
            var steps = "[{\"Tool\":\"Parse\",\"Args\":{\"source\":\"class C{}\"}}]";
            Tools.DefineWorkflow("wf_dup", steps, "[]");
            AssertEx.Throws<InvalidOperationException>(() => Tools.DefineWorkflow("wf_dup", steps, "[]"), "already exists");
            AssertEx.Throws<ArgumentException>(() => Tools.DefineWorkflow("wf_empty", "[]", "[]"));
        });

        // =========================================================================
        // 9. Tools.ConfirmWorkflow
        // =========================================================================
        Add(nameof(Tools.ConfirmWorkflow), TestCategory.Basic, "Confirm staged workflow containing Execute", () =>
        {
            WorkflowStore.ClearInMemoryForTesting();
            var staged = AssertEx.ParseJson(Tools.DefineWorkflow("wf_confirm_1", "[{\"Tool\":\"Execute\",\"Args\":{\"code\":\"2+2\"}}]", "[]"));
            var token = staged.GetProperty("confirmationToken").GetString()!;
            var confirmed = AssertEx.ParseJson(Tools.ConfirmWorkflow(token));
            AssertEx.Equal("active", confirmed.GetProperty("status").GetString());
        });
        Add(nameof(Tools.ConfirmWorkflow), TestCategory.Advanced, "Confirm workflow and execute it via RunWorkflow", () =>
        {
            WorkflowStore.ClearInMemoryForTesting();
            var staged = AssertEx.ParseJson(Tools.DefineWorkflow("wf_confirm_run", "[{\"Tool\":\"Execute\",\"Args\":{\"code\":\"{{expr}}\"}}]", "[\"expr\"]"));
            Tools.ConfirmWorkflow(staged.GetProperty("confirmationToken").GetString()!);
            var run = AssertEx.ParseJson(Tools.RunWorkflow("wf_confirm_run", "{\"expr\":\"10 + 5\"}"));
            AssertEx.True(run.GetProperty("log")[0].GetProperty("ok").GetBoolean());
        });
        Add(nameof(Tools.ConfirmWorkflow), TestCategory.EdgeCase, "Confirming one token leaves other pending workflows untouched", () =>
        {
            WorkflowStore.ClearInMemoryForTesting();
            var s1 = AssertEx.ParseJson(Tools.DefineWorkflow("wf_p1", "[{\"Tool\":\"Execute\",\"Args\":{\"code\":\"1\"}}]", "[]"));
            var s2 = AssertEx.ParseJson(Tools.DefineWorkflow("wf_p2", "[{\"Tool\":\"Execute\",\"Args\":{\"code\":\"2\"}}]", "[]"));
            Tools.ConfirmWorkflow(s1.GetProperty("confirmationToken").GetString()!);
            AssertEx.Throws<KeyNotFoundException>(() => WorkflowStore.Get("wf_p2"));
            Tools.ConfirmWorkflow(s2.GetProperty("confirmationToken").GetString()!);
        });
        Add(nameof(Tools.ConfirmWorkflow), TestCategory.FailureCase, "Invalid or already used confirmation token throws KeyNotFoundException", () =>
        {
            AssertEx.Throws<KeyNotFoundException>(() => Tools.ConfirmWorkflow("bad_token_00"));
        });

        // =========================================================================
        // 10. Tools.ListWorkflows
        // =========================================================================
        Add(nameof(Tools.ListWorkflows), TestCategory.Basic, "List active workflows", () =>
        {
            WorkflowStore.ClearInMemoryForTesting();
            Tools.DefineWorkflow("wf_list_1", "[{\"Tool\":\"Parse\",\"Args\":{\"source\":\"class C{}\"}}]", "[]");
            var json = AssertEx.ParseJson(Tools.ListWorkflows());
            AssertEx.Equal(1, json.GetProperty("workflows").GetArrayLength());
        });
        Add(nameof(Tools.ListWorkflows), TestCategory.Advanced, "List multiple workflows and verify containsExecute metadata", () =>
        {
            WorkflowStore.ClearInMemoryForTesting();
            Tools.DefineWorkflow("wf_safe", "[{\"Tool\":\"Format\",\"Args\":{\"source\":\"class C{}\"}}]", "[]");
            var staged = AssertEx.ParseJson(Tools.DefineWorkflow("wf_unsafe", "[{\"Tool\":\"Execute\",\"Args\":{\"code\":\"1\"}}]", "[]"));
            Tools.ConfirmWorkflow(staged.GetProperty("confirmationToken").GetString()!);
            var json = AssertEx.ParseJson(Tools.ListWorkflows());
            AssertEx.Equal(2, json.GetProperty("workflows").GetArrayLength());
        });
        Add(nameof(Tools.ListWorkflows), TestCategory.EdgeCase, "Empty store returns empty workflows array", () =>
        {
            WorkflowStore.ClearInMemoryForTesting();
            var json = AssertEx.ParseJson(Tools.ListWorkflows());
            AssertEx.Equal(0, json.GetProperty("workflows").GetArrayLength());
        });
        Add(nameof(Tools.ListWorkflows), TestCategory.FailureCase, "Pending unconfirmed workflow is excluded and cannot be retrieved", () =>
        {
            WorkflowStore.ClearInMemoryForTesting();
            Tools.DefineWorkflow("wf_hidden", "[{\"Tool\":\"Execute\",\"Args\":{\"code\":\"1\"}}]", "[]");
            var json = AssertEx.ParseJson(Tools.ListWorkflows());
            AssertEx.Equal(0, json.GetProperty("workflows").GetArrayLength());
            AssertEx.Throws<KeyNotFoundException>(() => WorkflowStore.Get("wf_hidden"));
        });

        // =========================================================================
        // 11. Tools.RunWorkflow
        // =========================================================================
        Add(nameof(Tools.RunWorkflow), TestCategory.Basic, "Run single-step Parse workflow", () =>
        {
            WorkflowStore.ClearInMemoryForTesting();
            Tools.DefineWorkflow("wf_run_1", "[{\"Tool\":\"Parse\",\"Args\":{\"source\":\"{{code}}\"}}]", "[\"code\"]");
            var json = AssertEx.ParseJson(Tools.RunWorkflow("wf_run_1", "{\"code\":\"public class A {}\"}"));
            AssertEx.True(json.GetProperty("log")[0].GetProperty("ok").GetBoolean());
        });
        Add(nameof(Tools.RunWorkflow), TestCategory.Advanced, "Run multi-step workflow chaining {{$prev_text}} across RefactoringTools and AnalysisTools", () =>
        {
            WorkflowStore.ClearInMemoryForTesting();
            var steps = """
                [
                    {"Tool":"AddOrReplaceMember","Args":{"sourceOrPath":"{{src}}","typeName":"Calc","memberCode":"public int Double(int x) => x * 2;"}},
                    {"Tool":"CompileAndDiagnose","Args":{"sourceOrPath":"{{$prev_text}}"}}
                ]
                """;
            Tools.DefineWorkflow("wf_chain", steps, "[\"src\"]");
            var res = AssertEx.ParseJson(Tools.RunWorkflow("wf_chain", "{\"src\":\"public class Calc { }\"}"));
            AssertEx.Equal(2, res.GetProperty("log").GetArrayLength());
            AssertEx.True(res.GetProperty("log")[1].GetProperty("ok").GetBoolean());
        });
        Add(nameof(Tools.RunWorkflow), TestCategory.EdgeCase, "Workflow halts on first failing step and logs error", () =>
        {
            WorkflowStore.ClearInMemoryForTesting();
            var steps = """
                [
                    {"Tool":"FindNodes","Args":{"source":"class C {}","kind":"InvalidKindXYZ"}},
                    {"Tool":"Parse","Args":{"source":"class C {}"}}
                ]
                """;
            Tools.DefineWorkflow("wf_halt", steps, "[]");
            var res = AssertEx.ParseJson(Tools.RunWorkflow("wf_halt", "{}"));
            AssertEx.Equal(1, res.GetProperty("log").GetArrayLength());
            AssertEx.False(res.GetProperty("log")[0].GetProperty("ok").GetBoolean());
        });
        Add(nameof(Tools.RunWorkflow), TestCategory.FailureCase, "Missing workflow or missing required parameter throws exception", () =>
        {
            WorkflowStore.ClearInMemoryForTesting();
            Tools.DefineWorkflow("wf_req", "[{\"Tool\":\"Parse\",\"Args\":{\"source\":\"{{reqParam}}\"}}]", "[\"reqParam\"]");
            AssertEx.Throws<ArgumentException>(() => Tools.RunWorkflow("wf_req", "{}"), "missing param");
            AssertEx.Throws<KeyNotFoundException>(() => Tools.RunWorkflow("non_existent_wf", "{}"));
        });

        // =========================================================================
        // 12. ProjectTools.ParseMany
        // =========================================================================
        Add(nameof(ProjectTools.ParseMany), TestCategory.Basic, "Parse multiple valid files in one call", () =>
        {
            var json = AssertEx.ParseJson(ProjectTools.ParseMany("{\"A.cs\":\"class A {}\",\"B.cs\":\"class B {}\"}"));
            AssertEx.Equal(2, json.GetProperty("fileCount").GetInt32());
            AssertEx.True(json.GetProperty("results").GetProperty("A.cs").GetProperty("ok").GetBoolean());
        });
        Add(nameof(ProjectTools.ParseMany), TestCategory.Advanced, "Parse mixed valid and broken files and count per-file errors", () =>
        {
            var json = AssertEx.ParseJson(ProjectTools.ParseMany("{\"Good.cs\":\"class G {}\",\"Bad.cs\":\"class B { int x = ; }\"}"));
            AssertEx.True(json.GetProperty("results").GetProperty("Good.cs").GetProperty("ok").GetBoolean());
            AssertEx.False(json.GetProperty("results").GetProperty("Bad.cs").GetProperty("ok").GetBoolean());
        });
        Add(nameof(ProjectTools.ParseMany), TestCategory.EdgeCase, "Parse empty file content inside dictionary", () =>
        {
            var json = AssertEx.ParseJson(ProjectTools.ParseMany("{\"Empty.cs\":\"\"}"));
            AssertEx.Equal(0, json.GetProperty("results").GetProperty("Empty.cs").GetProperty("errorCount").GetInt32());
        });
        Add(nameof(ProjectTools.ParseMany), TestCategory.FailureCase, "Empty JSON object or invalid JSON throws ArgumentException", () =>
        {
            AssertEx.Throws<ArgumentException>(() => ProjectTools.ParseMany("{}"));
            AssertEx.Throws<ArgumentException>(() => ProjectTools.ParseMany("not-json"));
        });

        // =========================================================================
        // 13. ProjectTools.ReadCsproj
        // =========================================================================
        Add(nameof(ProjectTools.ReadCsproj), TestCategory.Basic, "Read standard SDK .csproj XML", () =>
        {
            var xml = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>";
            var json = AssertEx.ParseJson(ProjectTools.ReadCsproj(xml));
            AssertEx.Equal("net8.0", json.GetProperty("targetFrameworks")[0].GetString());
            AssertEx.Equal("Library", json.GetProperty("outputType").GetString());
        });
        Add(nameof(ProjectTools.ReadCsproj), TestCategory.Advanced, "Read multi-target .csproj with PackageReference and ProjectReference", () =>
        {
            var xml = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFrameworks>net6.0;net8.0</TargetFrameworks>
                    <OutputType>Exe</OutputType>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="DiffPlex" Version="1.7.2" />
                    <ProjectReference Include="..\Core\Core.csproj" />
                  </ItemGroup>
                </Project>
                """;
            var json = AssertEx.ParseJson(ProjectTools.ReadCsproj(xml));
            AssertEx.Equal(2, json.GetProperty("targetFrameworks").GetArrayLength());
            AssertEx.Equal("Exe", json.GetProperty("outputType").GetString());
            AssertEx.Equal(1, json.GetProperty("packageReferences").GetArrayLength());
            AssertEx.Equal(1, json.GetProperty("projectReferences").GetArrayLength());
        });
        Add(nameof(ProjectTools.ReadCsproj), TestCategory.EdgeCase, "Read empty <Project/> element", () =>
        {
            var json = AssertEx.ParseJson(ProjectTools.ReadCsproj("<Project />"));
            AssertEx.Equal(0, json.GetProperty("packageReferences").GetArrayLength());
        });
        Add(nameof(ProjectTools.ReadCsproj), TestCategory.FailureCase, "Non-Project root element or malformed XML throws exception", () =>
        {
            AssertEx.Throws<ArgumentException>(() => ProjectTools.ReadCsproj("<WrongRoot />"));
            AssertEx.Throws<XmlException>(() => ProjectTools.ReadCsproj("<Project unclosed"));
        });

        // =========================================================================
        // 14. ProjectTools.ReadSlnx
        // =========================================================================
        Add(nameof(ProjectTools.ReadSlnx), TestCategory.Basic, "Read single-project .slnx XML", () =>
        {
            var json = AssertEx.ParseJson(ProjectTools.ReadSlnx("<Solution><Project Path=\"RoslynMcp.csproj\" /></Solution>"));
            AssertEx.Equal(1, json.GetProperty("projectCount").GetInt32());
        });
        Add(nameof(ProjectTools.ReadSlnx), TestCategory.Advanced, "Read nested folder .slnx structure", () =>
        {
            var xml = "<Solution><Folder Name=\"/src/\"><Project Path=\"A/A.csproj\" /><Project Path=\"B/B.csproj\" /></Folder></Solution>";
            var json = AssertEx.ParseJson(ProjectTools.ReadSlnx(xml));
            AssertEx.Equal(2, json.GetProperty("projectCount").GetInt32());
        });
        Add(nameof(ProjectTools.ReadSlnx), TestCategory.EdgeCase, "Empty <Solution/> with no projects returns projectCount=0", () =>
        {
            var json = AssertEx.ParseJson(ProjectTools.ReadSlnx("<Solution />"));
            AssertEx.Equal(0, json.GetProperty("projectCount").GetInt32());
        });
        Add(nameof(ProjectTools.ReadSlnx), TestCategory.FailureCase, "Invalid root tag or empty string throws ArgumentException", () =>
        {
            AssertEx.Throws<ArgumentException>(() => ProjectTools.ReadSlnx("<NotSolution />"));
            AssertEx.Throws<ArgumentException>(() => ProjectTools.ReadSlnx(""));
        });

        // =========================================================================
        // 15. ProjectTools.InspectWorkspace
        // =========================================================================
        Add(nameof(ProjectTools.InspectWorkspace), TestCategory.Basic, "Inspect 2-file workspace and verify compilation health", () =>
        {
            var ws = "{\"IUser.cs\":\"namespace App; public interface IUser { string Name { get; } }\",\"User.cs\":\"namespace App; public class User : IUser { public string Name => \\\"Alice\\\"; }\"}";
            var json = AssertEx.ParseJson(ProjectTools.InspectWorkspace(ws));
            AssertEx.Equal(2, json.GetProperty("fileCount").GetInt32());
            AssertEx.True(json.GetProperty("compilationHealth").GetProperty("ok").GetBoolean());
        });
        Add(nameof(ProjectTools.InspectWorkspace), TestCategory.Advanced, "Inspect workspace with classes, records, structs, enums, interfaces, and Main entry point", () =>
        {
            var src = """
                namespace Sys;
                public interface IRun {}
                public record Rec(int Id);
                public struct Pt { public int X; }
                public enum Kind { A, B }
                public class App : IRun { public static void Main() {} }
                """;
            var json = AssertEx.ParseJson(ProjectTools.InspectWorkspace(src));
            var tb = json.GetProperty("typeBreakdown");
            AssertEx.Equal(5, tb.GetProperty("total").GetInt32());
            AssertEx.Equal(1, json.GetProperty("entryPoints").GetArrayLength());
        });
        Add(nameof(ProjectTools.InspectWorkspace), TestCategory.EdgeCase, "Inspect top-level statement file with zero named types", () =>
        {
            var json = AssertEx.ParseJson(ProjectTools.InspectWorkspace("System.Console.WriteLine(123);"));
            AssertEx.Equal(0, json.GetProperty("typeBreakdown").GetProperty("total").GetInt32());
            AssertEx.Equal(1, json.GetProperty("entryPoints").GetArrayLength());
        });
        Add(nameof(ProjectTools.InspectWorkspace), TestCategory.FailureCase, "Empty input throws ArgumentException", () =>
        {
            AssertEx.Throws<ArgumentException>(() => ProjectTools.InspectWorkspace("   "));
        });

        // =========================================================================
        // 16. CreativeTools.ScaffoldFile
        // =========================================================================
        Add(nameof(CreativeTools.ScaffoldFile), TestCategory.Basic, "Scaffold a public class in a file-scoped namespace", () =>
        {
            var code = CreativeTools.ScaffoldFile("class", "OrderService", "Commerce.Orders", ["System"]);
            AssertEx.Contains("namespace Commerce.Orders;", code);
            AssertEx.Contains("public class OrderService", code);
        });
        Add(nameof(CreativeTools.ScaffoldFile), TestCategory.Advanced, "Scaffold interface, record, struct, and enum types", () =>
        {
            foreach (var k in new[] { "interface", "record", "struct", "enum" })
            {
                var code = CreativeTools.ScaffoldFile(k, "SampleType", "MyNs", ["System", "System.Linq"]);
                var parsed = AssertEx.ParseJson(Tools.Parse(code));
                AssertEx.True(parsed.GetProperty("ok").GetBoolean());
            }
        });
        Add(nameof(CreativeTools.ScaffoldFile), TestCategory.EdgeCase, "Scaffold with empty usings array", () =>
        {
            var code = CreativeTools.ScaffoldFile("class", "Minimal", "Root", []);
            AssertEx.DoesNotContain("using ", code);
        });
        Add(nameof(CreativeTools.ScaffoldFile), TestCategory.FailureCase, "Invalid kind or invalid C# identifier throws ArgumentException", () =>
        {
            AssertEx.Throws<ArgumentException>(() => CreativeTools.ScaffoldFile("delegate", "MyDel", "Ns", []));
            AssertEx.Throws<ArgumentException>(() => CreativeTools.ScaffoldFile("class", "123Invalid", "Ns", []));
        });

        // =========================================================================
        // 17. CreativeTools.ScaffoldType
        // =========================================================================
        Add(nameof(CreativeTools.ScaffoldType), TestCategory.Basic, "Scaffold class from JSON spec with property and method", () =>
        {
            var spec = """{"kind":"class","name":"Invoice","namespace":"Billing","properties":[{"name":"Total","type":"decimal"}]}""";
            var json = AssertEx.ParseJson(CreativeTools.ScaffoldType(spec));
            AssertEx.Equal(0, json.GetProperty("errors").GetInt32());
            AssertEx.Contains("public decimal Total", json.GetProperty("code").GetString()!);
        });
        Add(nameof(CreativeTools.ScaffoldType), TestCategory.Advanced, "Scaffold service with readonly fields, DI constructor, async method, attributes, and XML docs", () =>
        {
            var spec = """
                {
                  "kind": "class",
                  "name": "PaymentProcessor",
                  "namespace": "Billing.Services",
                  "summary": "Processes customer payments.",
                  "fields": [{"name": "_gateway", "type": "string", "isReadOnly": true}],
                  "properties": [{"name": "Currency", "type": "string", "isInitOnly": true, "defaultValue": "\"USD\""}],
                  "generateConstructor": true,
                  "methods": [{"name": "ChargeAsync", "returnType": "Task<bool>", "isAsync": true, "parameters": [{"name": "amount", "type": "decimal"}], "body": "await Task.CompletedTask; return amount > 0;"}]
                }
                """;
            var json = AssertEx.ParseJson(CreativeTools.ScaffoldType(spec));
            AssertEx.Equal(0, json.GetProperty("errors").GetInt32());
            AssertEx.Contains("public PaymentProcessor(string gateway)", json.GetProperty("code").GetString()!);
        });
        Add(nameof(CreativeTools.ScaffoldType), TestCategory.EdgeCase, "Scaffold enum with enumMembers", () =>
        {
            var spec = """{"kind":"enum","name":"OrderStatus","namespace":"Billing","enumMembers":["Pending","Paid","Shipped"]}""";
            var json = AssertEx.ParseJson(CreativeTools.ScaffoldType(spec));
            AssertEx.Equal(0, json.GetProperty("errors").GetInt32());
            AssertEx.Contains("Shipped", json.GetProperty("code").GetString()!);
        });
        Add(nameof(CreativeTools.ScaffoldType), TestCategory.FailureCase, "Malformed JSON or invalid type name throws ArgumentException", () =>
        {
            AssertEx.Throws<ArgumentException>(() => CreativeTools.ScaffoldType("{bad json"));
            AssertEx.Throws<ArgumentException>(() => CreativeTools.ScaffoldType("{\"name\":\"99Invalid\"}"));
        });

        // =========================================================================
        // 18. CreativeTools.MultiReplace
        // =========================================================================
        Add(nameof(CreativeTools.MultiReplace), TestCategory.Basic, "Apply two non-overlapping edits", () =>
        {
            var src = "int a = 1; int b = 2;";
            var edits = $"[{{\"start\":{src.IndexOf('1')},\"len\":1,\"replacement\":\"10\"}},{{\"start\":{src.IndexOf('2')},\"len\":1,\"replacement\":\"20\"}}]";
            var json = AssertEx.ParseJson(CreativeTools.MultiReplace(src, edits));
            AssertEx.Equal("int a = 10; int b = 20;", json.GetProperty("text").GetString());
        });
        Add(nameof(CreativeTools.MultiReplace), TestCategory.Advanced, "Apply unsorted edits and verify back-to-front ordering", () =>
        {
            var src = "AAA BBB CCC";
            var edits = "[{\"start\":0,\"len\":3,\"replacement\":\"First\"},{\"start\":8,\"len\":3,\"replacement\":\"Third\"},{\"start\":4,\"len\":3,\"replacement\":\"Second\"}]";
            var json = AssertEx.ParseJson(CreativeTools.MultiReplace(src, edits));
            AssertEx.Equal("First Second Third", json.GetProperty("text").GetString());
        });
        Add(nameof(CreativeTools.MultiReplace), TestCategory.EdgeCase, "Adjacent touching edits and empty edit array succeed", () =>
        {
            var json = AssertEx.ParseJson(CreativeTools.MultiReplace("ABCDEF", "[{\"start\":0,\"len\":3,\"replacement\":\"X\"},{\"start\":3,\"len\":3,\"replacement\":\"Y\"}]"));
            AssertEx.Equal("XY", json.GetProperty("text").GetString());
        });
        Add(nameof(CreativeTools.MultiReplace), TestCategory.FailureCase, "Overlapping edits throw ArgumentException", () =>
        {
            AssertEx.Throws<ArgumentException>(() =>
                CreativeTools.MultiReplace("ABCDEFGH", "[{\"start\":1,\"len\":4,\"replacement\":\"X\"},{\"start\":3,\"len\":3,\"replacement\":\"Y\"}]"),
                "overlapping");
        });

        // =========================================================================
        // 19. CreativeTools.TreeSummary
        // =========================================================================
        Add(nameof(CreativeTools.TreeSummary), TestCategory.Basic, "Outline class with method and property", () =>
        {
            var src = "namespace N; public class C { public int P { get; set; } public void M() {} }";
            var json = AssertEx.ParseJson(CreativeTools.TreeSummary(src));
            AssertEx.Equal("CompilationUnit", json.GetProperty("kind").GetString());
        });
        Add(nameof(CreativeTools.TreeSummary), TestCategory.Advanced, "Outline constructors, fields, enums, and nested types with signatures", () =>
        {
            var src = "namespace N { public enum E { A, B } public class C { private int _f; public C(int f) { _f = f; } } }";
            var json = AssertEx.ParseJson(CreativeTools.TreeSummary(src, 4));
            AssertEx.True(json.GetProperty("children")[0].GetProperty("children").GetArrayLength() == 2);
        });
        Add(nameof(CreativeTools.TreeSummary), TestCategory.EdgeCase, "maxDepth=1 stops at namespace without descending into types", () =>
        {
            var src = "namespace N { public class C { public void M() {} } }";
            var json = AssertEx.ParseJson(CreativeTools.TreeSummary(src, 1));
            AssertEx.Equal(0, json.GetProperty("children")[0].GetProperty("children").GetArrayLength());
        });
        Add(nameof(CreativeTools.TreeSummary), TestCategory.FailureCase, "Empty source or negative maxDepth throws ArgumentException", () =>
        {
            AssertEx.Throws<ArgumentException>(() => CreativeTools.TreeSummary(""));
            AssertEx.Throws<ArgumentException>(() => CreativeTools.TreeSummary("class C {}", -1));
        });

        // =========================================================================
        // 20. CreativeTools.Explain
        // =========================================================================
        Add(nameof(CreativeTools.Explain), TestCategory.Basic, "Explain syntactic chain for parameter inside method", () =>
        {
            var src = "namespace N; public class C { public void Foo(int bar) {} }";
            int pos = src.IndexOf("bar");
            var json = AssertEx.ParseJson(CreativeTools.Explain(src, pos));
            AssertEx.Equal(4, json.GetProperty("chain").GetArrayLength());
        });
        Add(nameof(CreativeTools.Explain), TestCategory.Advanced, "Explain foreach inside catch block inside local function with semantic symbol", () =>
        {
            var src = "namespace N; class C { void M(int[] items) { void Local() { try {} catch (System.Exception) { foreach (var item in items) {} } } } }";
            int pos = src.IndexOf("in items") + 3;
            var json = AssertEx.ParseJson(CreativeTools.Explain(src, pos));
            AssertEx.Equal("Parameter", json.GetProperty("symbolKind").GetString());
        });
        Add(nameof(CreativeTools.Explain), TestCategory.EdgeCase, "Explain at offset 0 and offset source.Length", () =>
        {
            var src = "class C {}";
            AssertEx.ParseJson(CreativeTools.Explain(src, 0));
            AssertEx.ParseJson(CreativeTools.Explain(src, src.Length));
        });
        Add(nameof(CreativeTools.Explain), TestCategory.FailureCase, "Out-of-bounds offset throws ArgumentException", () =>
        {
            AssertEx.Throws<ArgumentException>(() => CreativeTools.Explain("class C {}", 100));
            AssertEx.Throws<ArgumentException>(() => CreativeTools.Explain("class C {}", -1));
        });

        // =========================================================================
        // 21. CreativeTools.QuerySyntaxTree
        // =========================================================================
        Add(nameof(CreativeTools.QuerySyntaxTree), TestCategory.Basic, "Query InvocationExpression nodes", () =>
        {
            var src = "using System; class C { void M() { Console.WriteLine(1); } }";
            var json = AssertEx.ParseJson(CreativeTools.QuerySyntaxTree(src, "InvocationExpression"));
            AssertEx.Equal(1, json.GetProperty("count").GetInt32());
        });
        Add(nameof(CreativeTools.QuerySyntaxTree), TestCategory.Advanced, "Query nodes filtered by multiple kinds, textPattern, and ancestorKind", () =>
        {
            var src = """
                using System.Threading.Tasks;
                class Svc {
                    async Task Run() {
                        await Task.Delay(10);
                        try { await SaveAsync(); } catch { }
                    }
                    Task SaveAsync() => Task.CompletedTask;
                }
                """;
            var json = AssertEx.ParseJson(CreativeTools.QuerySyntaxTree(src, "AwaitExpression", "SaveAsync", "TryStatement"));
            AssertEx.Equal(1, json.GetProperty("count").GetInt32());
        });
        Add(nameof(CreativeTools.QuerySyntaxTree), TestCategory.EdgeCase, "Query with no matching nodes returns count=0", () =>
        {
            var json = AssertEx.ParseJson(CreativeTools.QuerySyntaxTree("class C {}", "LockStatement"));
            AssertEx.Equal(0, json.GetProperty("count").GetInt32());
        });
        Add(nameof(CreativeTools.QuerySyntaxTree), TestCategory.FailureCase, "Unrecognized syntaxKinds or ancestorKind throws ArgumentException", () =>
        {
            AssertEx.Throws<ArgumentException>(() => CreativeTools.QuerySyntaxTree("class C {}", "FakeKind"));
            AssertEx.Throws<ArgumentException>(() => CreativeTools.QuerySyntaxTree("class C {}", null, null, "FakeAncestor"));
        });

        // =========================================================================
        // 22. SemanticTools.GetSymbolInfo
        // =========================================================================
        Add(nameof(SemanticTools.GetSymbolInfo), TestCategory.Basic, "Resolve method symbol by qualified name", () =>
        {
            var src = "namespace Demo; public class Calc { public int Add(int a, int b = 5) => a + b; }";
            var json = AssertEx.ParseJson(SemanticTools.GetSymbolInfo(src, "Calc.Add"));
            AssertEx.Equal("Method", json.GetProperty("symbol").GetProperty("kind").GetString());
            AssertEx.Equal("int", json.GetProperty("methodDetails").GetProperty("returnType").GetString());
        });
        Add(nameof(SemanticTools.GetSymbolInfo), TestCategory.Advanced, "Resolve generic class with constraints, interfaces, XML doc, and attributes", () =>
        {
            var src = """
                using System;
                namespace Demo;
                /// <summary>Generic repo.</summary>
                [Serializable]
                public class Repo<T> : IDisposable where T : class, new()
                {
                    public void Dispose() {}
                }
                """;
            var json = AssertEx.ParseJson(SemanticTools.GetSymbolInfo(src, "Repo"));
            AssertEx.Equal("Generic repo.", json.GetProperty("symbol").GetProperty("summary").GetString());
            AssertEx.True(json.GetProperty("typeDetails").GetProperty("isGeneric").GetBoolean());
        });
        Add(nameof(SemanticTools.GetSymbolInfo), TestCategory.EdgeCase, "Disambiguate overloaded methods by parameter signature and line:col", () =>
        {
            var src = "public class C {\n  public int F(int x) => x;\n  public string F(string s) => s;\n}";
            var jInt = AssertEx.ParseJson(SemanticTools.GetSymbolInfo(src, "C.F(int)"));
            var jStr = AssertEx.ParseJson(SemanticTools.GetSymbolInfo(src, "3:17"));
            AssertEx.Equal("int", jInt.GetProperty("methodDetails").GetProperty("returnType").GetString());
            AssertEx.Equal("string", jStr.GetProperty("methodDetails").GetProperty("returnType").GetString());
        });
        Add(nameof(SemanticTools.GetSymbolInfo), TestCategory.FailureCase, "Unresolvable symbol throws ArgumentException", () =>
        {
            AssertEx.Throws<ArgumentException>(() => SemanticTools.GetSymbolInfo("class C {}", "NoSuchSymbol"));
        });

        // =========================================================================
        // 23. SemanticTools.FindSymbols
        // =========================================================================
        Add(nameof(SemanticTools.FindSymbols), TestCategory.Basic, "Find all symbols in source", () =>
        {
            var src = "public class A { public int X { get; set; } public void M() {} }";
            var json = AssertEx.ParseJson(SemanticTools.FindSymbols(src));
            AssertEx.Equal(3, json.GetProperty("count").GetInt32());
        });
        Add(nameof(SemanticTools.FindSymbols), TestCategory.Advanced, "Filter symbols by glob, kind, accessibility, modifier, attribute, and returnType", () =>
        {
            var src = """
                using System;
                using System.Threading.Tasks;
                public class Svc {
                    [Obsolete]
                    public static async Task<int> FetchCountAsync() => await Task.FromResult(1);
                    private void Helper() {}
                }
                """;
            var json = AssertEx.ParseJson(SemanticTools.FindSymbols(src, "*Count*", "method", "public", "Obsolete", "async", "Task<int>"));
            AssertEx.Equal(1, json.GetProperty("count").GetInt32());
            AssertEx.Equal("FetchCountAsync", json.GetProperty("symbols")[0].GetProperty("name").GetString());
        });
        Add(nameof(SemanticTools.FindSymbols), TestCategory.EdgeCase, "Include local variables when kind='local'", () =>
        {
            var src = "public class C { public void M() { int secretLocal = 42; } }";
            var json = AssertEx.ParseJson(SemanticTools.FindSymbols(src, "secretLocal", "local"));
            AssertEx.Equal(1, json.GetProperty("count").GetInt32());
        });
        Add(nameof(SemanticTools.FindSymbols), TestCategory.FailureCase, "Empty sourceOrPath throws ArgumentException", () =>
        {
            AssertEx.Throws<ArgumentException>(() => SemanticTools.FindSymbols(""));
        });

        // =========================================================================
        // 24. SemanticTools.FindReferences
        // =========================================================================
        AddAsync(nameof(SemanticTools.FindReferences), TestCategory.Basic, "Find references to field across methods", async () =>
        {
            var src = "public class Counter { private int _val; public void Inc() { _val = _val + 1; } }";
            var json = AssertEx.ParseJson(await SemanticTools.FindReferences(src, "_val"));
            AssertEx.True(json.GetProperty("referenceCount").GetInt32() >= 2);
        });
        AddAsync(nameof(SemanticTools.FindReferences), TestCategory.Advanced, "Classify Read, Write, ReadWrite, and Invocation across multi-file workspace", async () =>
        {
            var ws = """
                {
                  "Store.cs": "namespace N; public class Store { public int Count; public void Bump() { Count = 1; Count++; } }",
                  "App.cs": "namespace N; public class App { public int ReadIt(Store s) { s.Bump(); return s.Count; } }"
                }
                """;
            var json = AssertEx.ParseJson(await SemanticTools.FindReferences(ws, "Store.Count"));
            var refs = json.GetProperty("references").EnumerateArray().Select(r => r.GetProperty("access").GetString()).ToList();
            AssertEx.True(refs.Contains("Write") && refs.Contains("ReadWrite") && refs.Contains("Read"));
        });
        AddAsync(nameof(SemanticTools.FindReferences), TestCategory.EdgeCase, "Unreferenced symbol returns referenceCount=0", async () =>
        {
            var src = "public class Unused { public void NeverCalled() {} }";
            var json = AssertEx.ParseJson(await SemanticTools.FindReferences(src, "NeverCalled"));
            AssertEx.Equal(0, json.GetProperty("referenceCount").GetInt32());
        });
        AddAsync(nameof(SemanticTools.FindReferences), TestCategory.FailureCase, "Non-existent symbol throws ArgumentException", async () =>
        {
            await AssertEx.ThrowsAsync<ArgumentException>(() => SemanticTools.FindReferences("class C {}", "MissingSym"));
        });

        // =========================================================================
        // 25. SemanticTools.GetCallGraph
        // =========================================================================
        AddAsync(nameof(SemanticTools.GetCallGraph), TestCategory.Basic, "Build incoming callers and outgoing callees for method", async () =>
        {
            var src = "public class C { public void Entry() => Step(); public void Step() => Leaf(); public void Leaf() {} }";
            var json = AssertEx.ParseJson(await SemanticTools.GetCallGraph(src, "C.Step"));
            AssertEx.Equal(1, json.GetProperty("incomingCallerCount").GetInt32());
            AssertEx.Equal(1, json.GetProperty("outgoingWorkspaceCallCount").GetInt32());
        });
        AddAsync(nameof(SemanticTools.GetCallGraph), TestCategory.Advanced, "Detect recursion and external BCL calls across depth=3", async () =>
        {
            var src = "using System; public class Fact { public int Compute(int n) { Console.WriteLine(n); return n <= 1 ? 1 : n * Compute(n - 1); } }";
            var json = AssertEx.ParseJson(await SemanticTools.GetCallGraph(src, "Fact.Compute", 3));
            AssertEx.True(json.GetProperty("target").GetProperty("isRecursive").GetBoolean());
            AssertEx.True(json.GetProperty("outgoingExternalCallCount").GetInt32() >= 1);
        });
        AddAsync(nameof(SemanticTools.GetCallGraph), TestCategory.EdgeCase, "Resolve enclosing method when pointing at line inside method body", async () =>
        {
            var src = "public class C {\n  public void Outer() {\n    Inner();\n  }\n  public void Inner() {}\n}";
            var json = AssertEx.ParseJson(await SemanticTools.GetCallGraph(src, "3:5"));
            AssertEx.Equal("Outer", json.GetProperty("target").GetProperty("name").GetString());
        });
        AddAsync(nameof(SemanticTools.GetCallGraph), TestCategory.FailureCase, "Pointing at type with no callable symbol throws ArgumentException", async () =>
        {
            await AssertEx.ThrowsAsync<ArgumentException>(() => SemanticTools.GetCallGraph("public class Empty {}", "Empty"));
        });

        // =========================================================================
        // 26. SemanticTools.GetTypeHierarchy
        // =========================================================================
        AddAsync(nameof(SemanticTools.GetTypeHierarchy), TestCategory.Basic, "Inspect base types and implemented interfaces", async () =>
        {
            var src = "public interface IRun {} public class Base {} public class Derived : Base, IRun {}";
            var json = AssertEx.ParseJson(await SemanticTools.GetTypeHierarchy(src, "Derived"));
            AssertEx.Equal(2, json.GetProperty("baseTypes").GetArrayLength()); // Base and object
            AssertEx.Equal(1, json.GetProperty("implementedInterfaces").GetArrayLength());
        });
        AddAsync(nameof(SemanticTools.GetTypeHierarchy), TestCategory.Advanced, "Inspect interface implementations and contract member mapping across types", async () =>
        {
            var src = """
                public interface IHandler { string Handle(int x); }
                public class FastHandler : IHandler { public string Handle(int x) => "fast"; }
                public class SlowHandler : IHandler { public string Handle(int x) => "slow"; }
                """;
            var json = AssertEx.ParseJson(await SemanticTools.GetTypeHierarchy(src, "IHandler"));
            AssertEx.Equal(2, json.GetProperty("derivedAndImplementingTypes").GetArrayLength());
            AssertEx.Equal(2, json.GetProperty("contractImplementations")[0].GetProperty("implementations").GetArrayLength());
        });
        AddAsync(nameof(SemanticTools.GetTypeHierarchy), TestCategory.EdgeCase, "Passing a member name automatically resolves its ContainingType", async () =>
        {
            var src = "public class Parent {} public class Child : Parent { public void Act() {} }";
            var json = AssertEx.ParseJson(await SemanticTools.GetTypeHierarchy(src, "Child.Act"));
            AssertEx.Equal("Child", json.GetProperty("type").GetProperty("name").GetString());
        });
        AddAsync(nameof(SemanticTools.GetTypeHierarchy), TestCategory.FailureCase, "Unknown type throws ArgumentException", async () =>
        {
            await AssertEx.ThrowsAsync<ArgumentException>(() => SemanticTools.GetTypeHierarchy("class C {}", "UnknownType"));
        });

        // =========================================================================
        // 27. SemanticTools.AnalyzeDataAndControlFlow
        // =========================================================================
        Add(nameof(SemanticTools.AnalyzeDataAndControlFlow), TestCategory.Basic, "Analyze control and data flow of a method", () =>
        {
            var src = "public class C { public int Calc(int a) { int b = a * 2; return b + 1; } }";
            var json = AssertEx.ParseJson(SemanticTools.AnalyzeDataAndControlFlow(src, "Calc"));
            AssertEx.True(json.GetProperty("dataFlow").GetProperty("succeeded").GetBoolean());
            AssertEx.Equal(1, json.GetProperty("dataFlow").GetProperty("variablesDeclared").GetArrayLength());
        });
        Add(nameof(SemanticTools.AnalyzeDataAndControlFlow), TestCategory.Advanced, "Analyze statement sub-range with dataFlowsIn, dataFlowsOut, and async extract preview", () =>
        {
            var src = """
                using System.Threading.Tasks;
                public class OrderProcessor {
                    public async Task<decimal> ComputeAsync(decimal price, decimal rate) {
                        await Task.Delay(1);
                        decimal tax = price * rate;
                        decimal total = price + tax;
                        return total;
                    }
                }
                """;
            var json = AssertEx.ParseJson(SemanticTools.AnalyzeDataAndControlFlow(src, "ComputeAsync", 4, 6));
            var preview = json.GetProperty("extractMethodPreview");
            AssertEx.True(preview.GetProperty("canSafelyExtract").GetBoolean());
            AssertEx.True(preview.GetProperty("isAsync").GetBoolean());
        });
        Add(nameof(SemanticTools.AnalyzeDataAndControlFlow), TestCategory.EdgeCase, "Detect non-linear early return exit point preventing safe extraction", () =>
        {
            var src = """
                public class C {
                    public int M(int x) {
                        if (x < 0) return -1;
                        int y = x * 2;
                        return y;
                    }
                }
                """;
            var json = AssertEx.ParseJson(SemanticTools.AnalyzeDataAndControlFlow(src, "M", 3, 4));
            AssertEx.False(json.GetProperty("extractMethodPreview").GetProperty("canSafelyExtract").GetBoolean());
        });
        Add(nameof(SemanticTools.AnalyzeDataAndControlFlow), TestCategory.FailureCase, "Invalid location throws ArgumentException", () =>
        {
            AssertEx.Throws<ArgumentException>(() => SemanticTools.AnalyzeDataAndControlFlow("class C {}", "MissingMethod"));
        });

        // =========================================================================
        // 28. SemanticTools.GetScopeSymbols
        // =========================================================================
        Add(nameof(SemanticTools.GetScopeSymbols), TestCategory.Basic, "List locals and parameters in scope at line inside method", () =>
        {
            var src = "public class C {\n  private int _field;\n  public void M(string arg) {\n    int local = 1;\n    var x = local;\n  }\n}";
            var json = AssertEx.ParseJson(SemanticTools.GetScopeSymbols(src, "5:5"));
            AssertEx.Equal(2, json.GetProperty("localsAndParameters").GetArrayLength());
        });
        Add(nameof(SemanticTools.GetScopeSymbols), TestCategory.Advanced, "Filter accessible symbols including LINQ extension methods", () =>
        {
            var src = "using System.Linq;\npublic class C {\n  public void M(int[] nums) {\n    var q = nums;\n  }\n}";
            var json = AssertEx.ParseJson(SemanticTools.GetScopeSymbols(src, "4:5", "Where", true));
            AssertEx.True(json.GetProperty("extensionMethods").GetArrayLength() >= 1);
        });
        Add(nameof(SemanticTools.GetScopeSymbols), TestCategory.EdgeCase, "Exclude extension methods when includeExtensionMethods=false", () =>
        {
            var src = "using System.Linq;\npublic class C {\n  public void M(int[] nums) {\n    var q = nums;\n  }\n}";
            var json = AssertEx.ParseJson(SemanticTools.GetScopeSymbols(src, "4:5", "", false));
            AssertEx.Equal(0, json.GetProperty("extensionMethods").GetArrayLength());
        });
        Add(nameof(SemanticTools.GetScopeSymbols), TestCategory.FailureCase, "Non-existent document in location throws ArgumentException", () =>
        {
            AssertEx.Throws<ArgumentException>(() => SemanticTools.GetScopeSymbols("class C {}", "Nope.cs:1:1"));
        });

        // =========================================================================
        // 29. RefactoringTools.RenameSymbol
        // =========================================================================
        AddAsync(nameof(RefactoringTools.RenameSymbol), TestCategory.Basic, "Rename method and update call sites", async () =>
        {
            var src = "public class Calc { public int Add(int a, int b) => a + b; public int Use() => Add(1, 2); }";
            var json = AssertEx.ParseJson(await RefactoringTools.RenameSymbol(src, "Calc.Add", "Sum"));
            var updated = json.GetProperty("primaryUpdatedSource").GetString()!;
            AssertEx.Contains("public int Sum(", updated);
            AssertEx.Contains("=> Sum(1, 2);", updated);
        });
        AddAsync(nameof(RefactoringTools.RenameSymbol), TestCategory.Advanced, "Rename interface method across multiple files updating implementation and callers", async () =>
        {
            var ws = """
                {
                  "ISvc.cs": "namespace N; public interface ISvc { int Execute(int x); }",
                  "Svc.cs": "namespace N; public class Svc : ISvc { public int Execute(int x) => x + 1; }",
                  "Caller.cs": "namespace N; public class Caller { public int Call(ISvc s) => s.Execute(5); }"
                }
                """;
            var json = AssertEx.ParseJson(await RefactoringTools.RenameSymbol(ws, "ISvc.Execute", "RunOperation"));
            AssertEx.Equal(3, json.GetProperty("modifiedFileCount").GetInt32());
            AssertEx.Equal(0, json.GetProperty("errorsAfter").GetInt32());
        });
        AddAsync(nameof(RefactoringTools.RenameSymbol), TestCategory.EdgeCase, "Rename local variable without altering class member of same name", async () =>
        {
            var src = "public class C {\n  public int Value { get; set; }\n  public int M() {\n    int count = 1;\n    return count + Value;\n  }\n}";
            var json = AssertEx.ParseJson(await RefactoringTools.RenameSymbol(src, "4:9", "localCount"));
            var updated = json.GetProperty("primaryUpdatedSource").GetString()!;
            AssertEx.Contains("int localCount = 1;", updated);
            AssertEx.Contains("public int Value", updated);
        });
        AddAsync(nameof(RefactoringTools.RenameSymbol), TestCategory.FailureCase, "Invalid C# identifier throws ArgumentException", async () =>
        {
            await AssertEx.ThrowsAsync<ArgumentException>(() => RefactoringTools.RenameSymbol("class C {}", "C", "123Invalid"));
        });

        // =========================================================================
        // 30. RefactoringTools.AddOrReplaceMember
        // =========================================================================
        Add(nameof(RefactoringTools.AddOrReplaceMember), TestCategory.Basic, "Add new method to existing class", () =>
        {
            var src = "public class Calc { public int Add(int a, int b) => a + b; }";
            var json = AssertEx.ParseJson(RefactoringTools.AddOrReplaceMember(src, "Calc", "public int Sub(int a, int b) => a - b;"));
            AssertEx.Equal("added", json.GetProperty("action").GetString());
            AssertEx.Equal(0, json.GetProperty("errorsAfter").GetInt32());
        });
        Add(nameof(RefactoringTools.AddOrReplaceMember), TestCategory.Advanced, "Replace specific method overload while preserving other overloads and XML docs", () =>
        {
            var src = """
                public class Formatter {
                    /// <summary>Formats an int.</summary>
                    public string Format(int x) => x.ToString();
                    public string Format(string s) => s;
                }
                """;
            var json = AssertEx.ParseJson(RefactoringTools.AddOrReplaceMember(src, "Formatter", "public string Format(int x) => $\"#{x}\";"));
            AssertEx.Equal("replaced", json.GetProperty("action").GetString());
            var text = json.GetProperty("text").GetString()!;
            AssertEx.Contains("/// <summary>Formats an int.</summary>", text);
            AssertEx.Contains("Format(string s)", text);
        });
        Add(nameof(RefactoringTools.AddOrReplaceMember), TestCategory.EdgeCase, "Insert field before existing method in canonical order", () =>
        {
            var src = "public class C { public void M() {} }";
            var json = AssertEx.ParseJson(RefactoringTools.AddOrReplaceMember(src, "C", "private readonly int _id;"));
            var text = json.GetProperty("text").GetString()!;
            AssertEx.True(text.IndexOf("_id") < text.IndexOf("void M()"));
        });
        Add(nameof(RefactoringTools.AddOrReplaceMember), TestCategory.FailureCase, "Invalid member syntax or unknown type throws ArgumentException", () =>
        {
            AssertEx.Throws<ArgumentException>(() => RefactoringTools.AddOrReplaceMember("class C {}", "C", "public void Broken("));
            AssertEx.Throws<ArgumentException>(() => RefactoringTools.AddOrReplaceMember("class C {}", "MissingType", "public int X;"));
        });

        // =========================================================================
        // 31. RefactoringTools.RemoveMember
        // =========================================================================
        Add(nameof(RefactoringTools.RemoveMember), TestCategory.Basic, "Remove method by name", () =>
        {
            var src = "public class C { public void Keep() {} public void Drop() {} }";
            var json = AssertEx.ParseJson(RefactoringTools.RemoveMember(src, "C", "Drop"));
            AssertEx.Equal(1, json.GetProperty("removedCount").GetInt32());
            AssertEx.DoesNotContain("Drop", json.GetProperty("text").GetString()!);
        });
        Add(nameof(RefactoringTools.RemoveMember), TestCategory.Advanced, "Remove specific overload by signature and detect dangling caller reference", () =>
        {
            var src = "public class C { public int F(int x) => x; public int F(int x, int y) => x + y; public int Caller() => F(1, 2); }";
            var json = AssertEx.ParseJson(RefactoringTools.RemoveMember(src, "C", "F(int, int)"));
            AssertEx.Contains("F(int x)", json.GetProperty("text").GetString()!);
            AssertEx.True(json.GetProperty("errorsAfter").GetInt32() > 0);
        });
        Add(nameof(RefactoringTools.RemoveMember), TestCategory.EdgeCase, "Remove field declaration by field variable name", () =>
        {
            var src = "public class C { private int _secret = 42; }";
            var json = AssertEx.ParseJson(RefactoringTools.RemoveMember(src, "C", "_secret"));
            AssertEx.Equal(1, json.GetProperty("removedCount").GetInt32());
        });
        Add(nameof(RefactoringTools.RemoveMember), TestCategory.FailureCase, "Removing non-existent member throws ArgumentException", () =>
        {
            AssertEx.Throws<ArgumentException>(() => RefactoringTools.RemoveMember("public class C {}", "C", "NoSuchMember"));
        });

        // =========================================================================
        // 32. RefactoringTools.ExtractMethod
        // =========================================================================
        Add(nameof(RefactoringTools.ExtractMethod), TestCategory.Basic, "Extract calculation lines into helper method with inferred parameter and return type", () =>
        {
            var src = """
                public class Pricing {
                    public decimal GetFinal(decimal basePrice) {
                        decimal discount = basePrice * 0.1m;
                        decimal net = basePrice - discount;
                        return net;
                    }
                }
                """;
            var json = AssertEx.ParseJson(RefactoringTools.ExtractMethod(src, "GetFinal", 3, 4, "ComputeNet"));
            AssertEx.Equal(0, json.GetProperty("errorsAfter").GetInt32());
            AssertEx.Contains("ComputeNet(basePrice)", json.GetProperty("text").GetString()!);
        });
        Add(nameof(RefactoringTools.ExtractMethod), TestCategory.Advanced, "Extract async block with multiple out-flowing variables returning Task<Tuple>", () =>
        {
            var src = """
                using System.Threading.Tasks;
                public class InvoiceBuilder {
                    public async Task<decimal> BuildAsync(decimal amount, decimal rate) {
                        await Task.Delay(1);
                        decimal tax = amount * rate;
                        decimal total = amount + tax;
                        return total + tax;
                    }
                }
                """;
            var json = AssertEx.ParseJson(RefactoringTools.ExtractMethod(src, "BuildAsync", 4, 6, "CalculateTaxAndTotalAsync"));
            AssertEx.Equal(0, json.GetProperty("errorsAfter").GetInt32());
            AssertEx.True(json.GetProperty("isAsync").GetBoolean());
            AssertEx.Contains("Task<(decimal tax, decimal total)>", json.GetProperty("returnType").GetString()!);
        });
        Add(nameof(RefactoringTools.ExtractMethod), TestCategory.EdgeCase, "Extracted method accessing instance state is marked instance (non-static)", () =>
        {
            var src = """
                public class Counter {
                    private int _step = 2;
                    public int Next(int current) {
                        int updated = current + _step;
                        return updated;
                    }
                }
                """;
            var json = AssertEx.ParseJson(RefactoringTools.ExtractMethod(src, "Next", 4, 4, "AddStep"));
            AssertEx.False(json.GetProperty("isStatic").GetBoolean());
            AssertEx.Equal(0, json.GetProperty("errorsAfter").GetInt32());
        });
        Add(nameof(RefactoringTools.ExtractMethod), TestCategory.FailureCase, "Extracting block with early return or invalid line range throws exception", () =>
        {
            var src = """
                public class C {
                    public int M(int x) {
                        if (x < 0) return 0;
                        int y = x + 1;
                        return y;
                    }
                }
                """;
            AssertEx.Throws<InvalidOperationException>(() => RefactoringTools.ExtractMethod(src, "M", 3, 4, "Helper"));
            AssertEx.Throws<ArgumentException>(() => RefactoringTools.ExtractMethod(src, "M", 5, 2, "Helper"));
        });

        // =========================================================================
        // 33. RefactoringTools.ImplementInterface
        // =========================================================================
        Add(nameof(RefactoringTools.ImplementInterface), TestCategory.Basic, "Implement workspace interface on class", () =>
        {
            var src = "public interface IGreeter { string Greet(string name); }\npublic class Greeter {}";
            var json = AssertEx.ParseJson(RefactoringTools.ImplementInterface(src, "Greeter", "IGreeter"));
            AssertEx.Equal(1, json.GetProperty("generatedMemberCount").GetInt32());
            AssertEx.Equal(0, json.GetProperty("errorsAfter").GetInt32());
        });
        Add(nameof(RefactoringTools.ImplementInterface), TestCategory.Advanced, "Implement .NET BCL generic interfaces IEquatable<T>, IDisposable, and IAsyncDisposable", () =>
        {
            var src = "using System;\npublic class Resource {}";
            var j1 = AssertEx.ParseJson(RefactoringTools.ImplementInterface(src, "Resource", "IDisposable"));
            var j2 = AssertEx.ParseJson(RefactoringTools.ImplementInterface(j1.GetProperty("text").GetString()!, "Resource", "IEquatable<Resource>", explicitImplementation: true));
            AssertEx.Equal(0, j2.GetProperty("errorsAfter").GetInt32());
            AssertEx.Contains("IEquatable<Resource>.Equals", j2.GetProperty("text").GetString()!);
        });
        Add(nameof(RefactoringTools.ImplementInterface), TestCategory.EdgeCase, "Skip already implemented interface members without duplication", () =>
        {
            var src = "public interface I Pair { int A { get; } int B { get; } }\npublic interface IPair { int A { get; } int B { get; } }\npublic class MyPair : IPair { public int A => 1; }";
            var json = AssertEx.ParseJson(RefactoringTools.ImplementInterface(src, "MyPair", "IPair"));
            AssertEx.Equal(1, json.GetProperty("generatedMemberCount").GetInt32());
        });
        Add(nameof(RefactoringTools.ImplementInterface), TestCategory.FailureCase, "Unresolvable interface throws ArgumentException", () =>
        {
            AssertEx.Throws<ArgumentException>(() => RefactoringTools.ImplementInterface("public class C {}", "C", "INonExistent999"));
        });

        // =========================================================================
        // 34. RefactoringTools.GenerateTypeMembers
        // =========================================================================
        Add(nameof(RefactoringTools.GenerateTypeMembers), TestCategory.Basic, "Generate DI constructor with null checks", () =>
        {
            var src = "public class OrderSvc { private readonly string _conn; private readonly int _retries; }";
            var json = AssertEx.ParseJson(RefactoringTools.GenerateTypeMembers(src, "OrderSvc", "constructor"));
            AssertEx.Equal(0, json.GetProperty("errorsAfter").GetInt32());
            AssertEx.Contains("ArgumentNullException.ThrowIfNull(conn);", json.GetProperty("text").GetString()!);
        });
        Add(nameof(RefactoringTools.GenerateTypeMembers), TestCategory.Advanced, "Generate equals_hashcode, tostring, overrides, and extract_interface", () =>
        {
            var src = "public abstract class Base { public abstract int Compute(int x); }\npublic class Item : Base { public int Id { get; set; } public string Name { get; set; } = \"\"; }";
            var s1 = AssertEx.ParseJson(RefactoringTools.GenerateTypeMembers(src, "Item", "overrides")).GetProperty("text").GetString()!;
            var s2 = AssertEx.ParseJson(RefactoringTools.GenerateTypeMembers(s1, "Item", "equals_hashcode")).GetProperty("text").GetString()!;
            var s3 = AssertEx.ParseJson(RefactoringTools.GenerateTypeMembers(s2, "Item", "tostring")).GetProperty("text").GetString()!;
            var j4 = AssertEx.ParseJson(RefactoringTools.GenerateTypeMembers(s3, "Item", "extract_interface"));
            AssertEx.Equal(0, j4.GetProperty("errorsAfter").GetInt32());
            AssertEx.Contains("public interface IItem", j4.GetProperty("text").GetString()!);
        });
        Add(nameof(RefactoringTools.GenerateTypeMembers), TestCategory.EdgeCase, "Skip fields that already have initializers when generating constructor", () =>
        {
            var src = "public class C { private readonly int _preset = 10; private readonly string _dynamic; }";
            var json = AssertEx.ParseJson(RefactoringTools.GenerateTypeMembers(src, "C", "constructor"));
            AssertEx.Contains("public C(string dynamic)", json.GetProperty("text").GetString()!);
        });
        Add(nameof(RefactoringTools.GenerateTypeMembers), TestCategory.FailureCase, "Unrecognized generatorKind throws ArgumentException", () =>
        {
            AssertEx.Throws<ArgumentException>(() => RefactoringTools.GenerateTypeMembers("public class C {}", "C", "unknown_kind"));
        });

        // =========================================================================
        // 35. RefactoringTools.OrganizeUsings
        // =========================================================================
        Add(nameof(RefactoringTools.OrganizeUsings), TestCategory.Basic, "Remove unused usings and sort System first", () =>
        {
            var src = "using System.Diagnostics;\nusing System.Text;\nusing System;\npublic class C { public StringBuilder Sb = new(); }";
            var json = AssertEx.ParseJson(RefactoringTools.OrganizeUsings(src, addMissingUsings: false, removeUnusedUsings: true, sortUsings: true));
            AssertEx.DoesNotContain("System.Diagnostics", json.GetProperty("text").GetString()!);
        });
        Add(nameof(RefactoringTools.OrganizeUsings), TestCategory.Advanced, "Automatically discover and add missing BCL namespaces for unresolved types", () =>
        {
            var src = "public class Builder { public StringBuilder Create() => new StringBuilder(); }";
            var json = AssertEx.ParseJson(RefactoringTools.OrganizeUsings(src));
            AssertEx.Contains("using System.Text;", json.GetProperty("text").GetString()!);
            AssertEx.Equal(0, json.GetProperty("errorsAfter").GetInt32());
        });
        Add(nameof(RefactoringTools.OrganizeUsings), TestCategory.EdgeCase, "Preserve static and alias using directives", () =>
        {
            var src = "using static System.Math;\nusing Env = System.Environment;\npublic class C { public double X => PI; }";
            var json = AssertEx.ParseJson(RefactoringTools.OrganizeUsings(src));
            var text = json.GetProperty("text").GetString()!;
            AssertEx.Contains("using static System.Math;", text);
            AssertEx.Contains("using Env = System.Environment;", text);
        });
        Add(nameof(RefactoringTools.OrganizeUsings), TestCategory.FailureCase, "Non-existent targetFile throws ArgumentException", () =>
        {
            AssertEx.Throws<ArgumentException>(() => RefactoringTools.OrganizeUsings("public class C {}", "NoSuchFile.cs"));
        });

        // =========================================================================
        // 36. RefactoringTools.ApplySyntaxTransform
        // =========================================================================
        Add(nameof(RefactoringTools.ApplySyntaxTransform), TestCategory.Basic, "Convert block-scoped namespace to file-scoped namespace", () =>
        {
            var src = "namespace MyCorp.App\n{\n    public class Demo {}\n}";
            var json = AssertEx.ParseJson(RefactoringTools.ApplySyntaxTransform(src, "file_scoped_namespace"));
            AssertEx.Contains("namespace MyCorp.App;", json.GetProperty("text").GetString()!);
        });
        Add(nameof(RefactoringTools.ApplySyntaxTransform), TestCategory.Advanced, "Apply expression_bodied, var_to_explicit, explicit_to_var, and add_null_checks", () =>
        {
            var src = "public class C { public string Echo(string input) { var len = input.Length; return input; } }";
            var s1 = AssertEx.ParseJson(RefactoringTools.ApplySyntaxTransform(src, "var_to_explicit")).GetProperty("text").GetString()!;
            AssertEx.Contains("int len = input.Length;", s1);
            var s2 = AssertEx.ParseJson(RefactoringTools.ApplySyntaxTransform(s1, "explicit_to_var")).GetProperty("text").GetString()!;
            AssertEx.Contains("var len = input.Length;", s2);
            var s3 = AssertEx.ParseJson(RefactoringTools.ApplySyntaxTransform(s2, "add_null_checks")).GetProperty("text").GetString()!;
            AssertEx.Contains("ArgumentNullException.ThrowIfNull(input);", s3);
            var j4 = AssertEx.ParseJson(RefactoringTools.ApplySyntaxTransform("public class C { public int Get() { return 5; } }", "expression_bodied"));
            AssertEx.Contains("=> 5;", j4.GetProperty("text").GetString()!);
        });
        Add(nameof(RefactoringTools.ApplySyntaxTransform), TestCategory.EdgeCase, "add_readonly_fields marks immutable private field readonly while leaving mutated field mutable", () =>
        {
            var src = "public class C { private int _fixed; private int _mutated; public C(int f) { _fixed = f; } public void Inc() { _mutated++; } }";
            var json = AssertEx.ParseJson(RefactoringTools.ApplySyntaxTransform(src, "add_readonly_fields"));
            var text = json.GetProperty("text").GetString()!;
            AssertEx.Contains("private readonly int _fixed;", text);
            AssertEx.Contains("private int _mutated;", text);
        });
        Add(nameof(RefactoringTools.ApplySyntaxTransform), TestCategory.FailureCase, "Unrecognized transform throws ArgumentException", () =>
        {
            AssertEx.Throws<ArgumentException>(() => RefactoringTools.ApplySyntaxTransform("public class C {}", "not_a_transform"));
        });

        // =========================================================================
        // 37. AnalysisTools.CompileAndDiagnose
        // =========================================================================
        Add(nameof(AnalysisTools.CompileAndDiagnose), TestCategory.Basic, "Compile valid multi-type code with zero errors", () =>
        {
            var json = AssertEx.ParseJson(AnalysisTools.CompileAndDiagnose("public record Item(int Id); public class Store { public Item Get() => new(1); }"));
            AssertEx.True(json.GetProperty("ok").GetBoolean());
            AssertEx.Equal(0, json.GetProperty("errorCount").GetInt32());
        });
        Add(nameof(AnalysisTools.CompileAndDiagnose), TestCategory.Advanced, "Provide semantic fix suggestions for unresolved types and overload mismatches", () =>
        {
            var src = "public class C { public void M() { StringBuilder sb = new(); } }";
            var json = AssertEx.ParseJson(AnalysisTools.CompileAndDiagnose(src));
            AssertEx.False(json.GetProperty("ok").GetBoolean());
            var suggestions = json.GetProperty("diagnostics")[0].GetProperty("suggestions");
            AssertEx.True(suggestions.GetArrayLength() > 0);
            AssertEx.Contains("System.Text", suggestions[0].GetString()!);
        });
        Add(nameof(AnalysisTools.CompileAndDiagnose), TestCategory.EdgeCase, "Filter diagnostics by minSeverity='Error' hides warnings", () =>
        {
            var src = "public class C { public void M() { int unused = 1; } }";
            var json = AssertEx.ParseJson(AnalysisTools.CompileAndDiagnose(src, "Error"));
            AssertEx.True(json.GetProperty("warningCount").GetInt32() > 0);
            AssertEx.Equal(0, json.GetProperty("diagnostics").GetArrayLength());
        });
        Add(nameof(AnalysisTools.CompileAndDiagnose), TestCategory.FailureCase, "Invalid minSeverity throws ArgumentException", () =>
        {
            AssertEx.Throws<ArgumentException>(() => AnalysisTools.CompileAndDiagnose("class C {}", "FatalError"));
        });

        // =========================================================================
        // 38. AnalysisTools.GetCodeMetrics
        // =========================================================================
        Add(nameof(AnalysisTools.GetCodeMetrics), TestCategory.Basic, "Compute metrics for simple class", () =>
        {
            var src = "public class Simple { public int Id { get; set; } public int GetId() => Id; }";
            var json = AssertEx.ParseJson(AnalysisTools.GetCodeMetrics(src));
            AssertEx.Equal(1, json.GetProperty("summary").GetProperty("maxCyclomaticComplexity").GetInt32());
        });
        Add(nameof(AnalysisTools.GetCodeMetrics), TestCategory.Advanced, "Identify complexity hotspot with high branching and deep nesting", () =>
        {
            var src = """
                public class ComplexProcessor {
                    public int Process(int a, int b, int c, int d, int e, int f) {
                        if (a > 0 && b > 0) {
                            for (int i = 0; i < c; i++) {
                                if (d > 0) {
                                    while (e > 0) {
                                        if (f > 0) return 1;
                                        e--;
                                    }
                                }
                            }
                        }
                        return 0;
                    }
                }
                """;
            var json = AssertEx.ParseJson(AnalysisTools.GetCodeMetrics(src, complexityThreshold: 4));
            AssertEx.Equal(1, json.GetProperty("summary").GetProperty("hotspotCount").GetInt32());
            AssertEx.True(json.GetProperty("hotspots")[0].GetProperty("maxNestingDepth").GetInt32() >= 4);
        });
        Add(nameof(AnalysisTools.GetCodeMetrics), TestCategory.EdgeCase, "Interface with no method bodies produces 0 hotspots", () =>
        {
            var json = AssertEx.ParseJson(AnalysisTools.GetCodeMetrics("public interface IEmpty { void Run(); }"));
            AssertEx.Equal(0, json.GetProperty("summary").GetProperty("hotspotCount").GetInt32());
        });
        Add(nameof(AnalysisTools.GetCodeMetrics), TestCategory.FailureCase, "Non-positive complexityThreshold throws ArgumentException", () =>
        {
            AssertEx.Throws<ArgumentException>(() => AnalysisTools.GetCodeMetrics("class C {}", 0));
        });

        // =========================================================================
        // 39. AnalysisTools.GetDependencyGraph
        // =========================================================================
        Add(nameof(AnalysisTools.GetDependencyGraph), TestCategory.Basic, "Build type dependency graph and Mermaid diagram", () =>
        {
            var src = "public class Repo {} public class Svc { private readonly Repo _r = new(); }";
            var json = AssertEx.ParseJson(AnalysisTools.GetDependencyGraph(src, "type"));
            AssertEx.Equal(2, json.GetProperty("nodeCount").GetInt32());
            AssertEx.Equal(1, json.GetProperty("edgeCount").GetInt32());
            AssertEx.Contains("graph LR", json.GetProperty("mermaidDiagram").GetString()!);
        });
        Add(nameof(AnalysisTools.GetDependencyGraph), TestCategory.Advanced, "Detect circular dependency cycle via Tarjan SCC", () =>
        {
            var src = """
                public class NodeA { public NodeB? B; }
                public class NodeB { public NodeC? C; }
                public class NodeC { public NodeA? A; }
                """;
            var json = AssertEx.ParseJson(AnalysisTools.GetDependencyGraph(src, "type"));
            AssertEx.True(json.GetProperty("hasCircularDependencies").GetBoolean());
            AssertEx.Equal(4, json.GetProperty("circularDependencies")[0].GetArrayLength()); // A -> B -> C -> A
        });
        Add(nameof(AnalysisTools.GetDependencyGraph), TestCategory.EdgeCase, "Support namespace and file granularities", () =>
        {
            var ws = "{\"A.cs\":\"namespace Ns1 { public class A { public Ns2.B? B; } }\",\"B.cs\":\"namespace Ns2 { public class B {} }\"}";
            var nsGraph = AssertEx.ParseJson(AnalysisTools.GetDependencyGraph(ws, "namespace"));
            var fileGraph = AssertEx.ParseJson(AnalysisTools.GetDependencyGraph(ws, "file"));
            AssertEx.Equal(1, nsGraph.GetProperty("edgeCount").GetInt32());
            AssertEx.Equal(1, fileGraph.GetProperty("edgeCount").GetInt32());
        });
        Add(nameof(AnalysisTools.GetDependencyGraph), TestCategory.FailureCase, "Invalid granularity throws ArgumentException", () =>
        {
            AssertEx.Throws<ArgumentException>(() => AnalysisTools.GetDependencyGraph("class C {}", "invalid_granularity"));
        });

        // =========================================================================
        // 40. AnalysisTools.SemanticDiff
        // =========================================================================
        Add(nameof(AnalysisTools.SemanticDiff), TestCategory.Basic, "Identical sources produce zero breaking or behavioral changes", () =>
        {
            var src = "public class Api { public int Run(int x) => x + 1; }";
            var json = AssertEx.ParseJson(AnalysisTools.SemanticDiff(src, src));
            AssertEx.False(json.GetProperty("hasBreakingChanges").GetBoolean());
            AssertEx.Equal(0, json.GetProperty("bodyModifiedCount").GetInt32());
        });
        Add(nameof(AnalysisTools.SemanticDiff), TestCategory.Advanced, "Detect removed public API, reduced visibility, type change, added symbol, and modified body", () =>
        {
            var v1 = """
                public class Api {
                    public void Removed() {}
                    public void Hidden() {}
                    public int Value { get; set; }
                    public int Compute(int x) => x + 1;
                }
                """;
            var v2 = """
                public class Api {
                    internal void Hidden() {}
                    public long Value { get; set; }
                    public int Compute(int x) => x * 10;
                    public void Added() {}
                }
                """;
            var json = AssertEx.ParseJson(AnalysisTools.SemanticDiff(v1, v2));
            AssertEx.True(json.GetProperty("hasBreakingChanges").GetBoolean());
            AssertEx.Equal(3, json.GetProperty("breakingChangeCount").GetInt32());
            AssertEx.Equal(1, json.GetProperty("addedCount").GetInt32());
            AssertEx.Equal(1, json.GetProperty("bodyModifiedCount").GetInt32());
        });
        Add(nameof(AnalysisTools.SemanticDiff), TestCategory.EdgeCase, "Whitespace-only body changes and private removals do not trigger breaking changes", () =>
        {
            var v1 = "public class Api { public int M() { return 1 + 2; } private void Priv() {} }";
            var v2 = "public class Api {\n    public int M()\n    {\n        return 1   +   2;\n    }\n}";
            var json = AssertEx.ParseJson(AnalysisTools.SemanticDiff(v1, v2));
            AssertEx.False(json.GetProperty("hasBreakingChanges").GetBoolean());
            AssertEx.Equal(0, json.GetProperty("bodyModifiedCount").GetInt32());
            AssertEx.Equal(1, json.GetProperty("removedNonPublicCount").GetInt32());
        });
        Add(nameof(AnalysisTools.SemanticDiff), TestCategory.FailureCase, "Empty source throws ArgumentException", () =>
        {
            AssertEx.Throws<ArgumentException>(() => AnalysisTools.SemanticDiff("", "class C {}"));
        });

        // =========================================================================
        // 41. AnalysisTools.GetPublicApiSurface
        // =========================================================================
        Add(nameof(AnalysisTools.GetPublicApiSurface), TestCategory.Basic, "Strip method bodies and reduce token count", () =>
        {
            var src = """
                public class OrderService {
                    public int Process(int a, int b) {
                        int sum = 0;
                        for (int i = a; i < b; i++) sum += i;
                        return sum;
                    }
                }
                """;
            var json = AssertEx.ParseJson(AnalysisTools.GetPublicApiSurface(src));
            var skeleton = json.GetProperty("combinedSkeleton").GetString()!;
            AssertEx.Contains("public int Process(int a, int b);", skeleton);
            AssertEx.DoesNotContain("for (int i", skeleton);
            AssertEx.True(json.GetProperty("tokenReductionPercent").GetDouble() > 0);
        });
        Add(nameof(AnalysisTools.GetPublicApiSurface), TestCategory.Advanced, "Filter out internal and private members and strip comments", () =>
        {
            var src = """
                /// <summary>Doc comment</summary>
                public class Svc {
                    public void Pub() {}
                    internal void Int() {}
                    private void Priv() {}
                }
                """;
            var json = AssertEx.ParseJson(AnalysisTools.GetPublicApiSurface(src, includeInternal: false, includePrivate: false, includeDocComments: false));
            var skeleton = json.GetProperty("combinedSkeleton").GetString()!;
            AssertEx.Contains("void Pub();", skeleton);
            AssertEx.DoesNotContain("Int()", skeleton);
            AssertEx.DoesNotContain("Priv()", skeleton);
            AssertEx.DoesNotContain("Doc comment", skeleton);
        });
        Add(nameof(AnalysisTools.GetPublicApiSurface), TestCategory.EdgeCase, "Convert expression-bodied property to get-only contract and keep private when requested", () =>
        {
            var src = "public class C { private int Secret => 42; }";
            var json = AssertEx.ParseJson(AnalysisTools.GetPublicApiSurface(src, includeInternal: true, includePrivate: true));
            AssertEx.Contains("private int Secret { get; }", json.GetProperty("combinedSkeleton").GetString()!);

            // Verify custom ToString() debug conversions on domain objects
            using var ctx = WorkspaceEngine.Load(src);
            var target = WorkspaceEngine.ResolveTarget(ctx, "C.Secret");
            AssertEx.Contains("WorkspaceContext(", ctx.ToString());
            AssertEx.Contains("DocumentEntry(", ctx.PrimaryDocument.ToString());
            AssertEx.Contains("ResolvedTarget(", target.ToString());
            AssertEx.Contains("L1:", new LineRange(1, 1, 1, 10).ToString());
            AssertEx.Contains("WorkflowStep(", new WorkflowStep("Parse", new() { ["source"] = "x" }).ToString());
        });
        Add(nameof(AnalysisTools.GetPublicApiSurface), TestCategory.FailureCase, "Empty sourceOrPath throws ArgumentException", () =>
        {
            AssertEx.Throws<ArgumentException>(() => AnalysisTools.GetPublicApiSurface("   "));
        });

        return cases;
    }
}
