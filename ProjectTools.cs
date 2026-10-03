using System.ComponentModel;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;

namespace RoslynMcp;

[McpServerToolType]
internal static class ProjectTools
{
    [McpServerTool, Description(
        "Parse diagnostics across N sources in one call. sourcesJson: JSON object mapping " +
        "a caller-chosen label (e.g. filename) to source text. No cross-file linking — each " +
        "parsed independently, same as calling Parse per file.")]
    public static string ParseMany(string sourcesJson)
    {
        var sources = RoslynHelpers.Des<Dictionary<string, string>>(sourcesJson, nameof(sourcesJson));
        if (sources.Count == 0)
            throw new ArgumentException("sourcesJson must contain at least one source entry.", nameof(sourcesJson));

        var results = sources.ToDictionary(kv => kv.Key, kv =>
        {
            var d = CSharpSyntaxTree.ParseText(kv.Value ?? "").GetDiagnostics().ToArray();
            return (object)new
            {
                ok = !d.Any(x => x.Severity == DiagnosticSeverity.Error),
                errorCount = d.Count(x => x.Severity == DiagnosticSeverity.Error)
            };
        });
        return RoslynHelpers.Ser(new { fileCount = sources.Count, results });
    }

    [McpServerTool, Description(
        "Structural read of a .csproj (accepts raw XML or a file path on disk): TargetFramework(s), " +
        "explicit <Compile> items, <PackageReference> and <ProjectReference> entries, Nullable, ImplicitUsings.")]
    public static string ReadCsproj(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            throw new ArgumentException("xml cannot be empty.", nameof(xml));

        var rawXml = File.Exists(xml) ? File.ReadAllText(xml) : xml;
        var doc = XDocument.Parse(rawXml);
        if (doc.Root?.Name.LocalName != "Project")
            throw new ArgumentException($"Expected root <Project> element in .csproj XML, found '<{doc.Root?.Name.LocalName}>'.");

        var ns = doc.Root.Name.Namespace;
        string[] G(string el, string attr) =>
            doc.Descendants(ns + el).Select(e => e.Attribute(attr)?.Value ?? "").Where(v => v != "").ToArray();

        return RoslynHelpers.Ser(new
        {
            sdk = doc.Root.Attribute("Sdk")?.Value,
            targetFrameworks = doc.Descendants(ns + "TargetFramework").Select(e => e.Value)
                .Concat(doc.Descendants(ns + "TargetFrameworks").SelectMany(e => e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries)))
                .ToArray(),
            outputType = doc.Descendants(ns + "OutputType").Select(e => e.Value).FirstOrDefault() ?? "Library",
            nullable = doc.Descendants(ns + "Nullable").Select(e => e.Value).FirstOrDefault(),
            implicitUsings = doc.Descendants(ns + "ImplicitUsings").Select(e => e.Value).FirstOrDefault(),
            explicitCompileItems = G("Compile", "Include"),
            packageReferences = doc.Descendants(ns + "PackageReference")
                .Select(e => new
                {
                    id = e.Attribute("Include")?.Value,
                    version = e.Attribute("Version")?.Value ?? e.Element(ns + "Version")?.Value
                })
                .ToArray(),
            projectReferences = G("ProjectReference", "Include")
        });
    }

    [McpServerTool, Description(
        "Structural read of a .slnx (XML solution format; accepts raw XML or a file path on disk): declared projects and their paths.")]
    public static string ReadSlnx(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            throw new ArgumentException("xml cannot be empty.", nameof(xml));

        var rawXml = File.Exists(xml) ? File.ReadAllText(xml) : xml;
        var doc = XDocument.Parse(rawXml);
        if (doc.Root?.Name.LocalName != "Solution")
            throw new ArgumentException($"Expected root <Solution> element in .slnx XML, found '<{doc.Root?.Name.LocalName}>'.");

        var projects = doc.Descendants("Project")
            .Select(e => new { path = e.Attribute("Path")?.Value })
            .Where(p => !string.IsNullOrWhiteSpace(p.path))
            .ToArray();
        return RoslynHelpers.Ser(new { projectCount = projects.Length, projects });
    }

    [McpServerTool, Description(
        "Load an entire C# workspace (.csproj, .sln, .slnx, directory path, or multi-file JSON) into a linked " +
        "Roslyn Compilation and return a complete architectural overview: files, namespaces, types, member counts, " +
        "entry points, and cross-file compilation health.")]
    public static string InspectWorkspace(string pathOrSourcesJson)
    {
        using var ctx = WorkspaceEngine.Load(pathOrSourcesJson);

        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        var filesOverview = new List<object>();
        var entryPoints = new List<string>();

        int classCount = 0, interfaceCount = 0, recordCount = 0, structCount = 0, enumCount = 0;
        int totalMethods = 0, totalProperties = 0, totalLines = 0;

        foreach (var doc in ctx.UserDocuments)
        {
            var root = doc.Tree.GetRoot();
            int lines = doc.Tree.GetText().Lines.Count;
            totalLines += lines;

            if (root.ChildNodes().OfType<GlobalStatementSyntax>().Any())
                entryPoints.Add($"{doc.Name} (top-level statements)");

            var docTypes = new List<object>();
            foreach (var td in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                var sym = doc.Model.GetDeclaredSymbol(td);
                if (sym is null) continue;

                namespaces.Add(sym.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "(global)");

                if (sym.IsRecord) recordCount++;
                else if (sym.TypeKind == TypeKind.Class) classCount++;
                else if (sym.TypeKind == TypeKind.Interface) interfaceCount++;
                else if (sym.TypeKind == TypeKind.Struct) structCount++;
                else if (sym.TypeKind == TypeKind.Enum) enumCount++;

                var methods = sym.GetMembers().OfType<IMethodSymbol>()
                    .Where(m => !m.IsImplicitlyDeclared && m.MethodKind is MethodKind.Ordinary or MethodKind.Constructor)
                    .ToArray();
                var props = sym.GetMembers().OfType<IPropertySymbol>()
                    .Where(p => !p.IsImplicitlyDeclared)
                    .ToArray();

                totalMethods += methods.Length;
                totalProperties += props.Length;

                foreach (var m in methods.Where(m => m.Name == "Main" && m.IsStatic))
                    entryPoints.Add($"{sym.MinDisplay()}.Main ({doc.Name})");

                docTypes.Add(new
                {
                    name = sym.Name,
                    fullName = sym.MinDisplay(),
                    kind = sym.IsRecord ? "Record" : sym.TypeKind.ToString(),
                    accessibility = sym.DeclaredAccessibility.ToString(),
                    methodCount = methods.Length,
                    propertyCount = props.Length
                });
            }

            filesOverview.Add(new { file = doc.Name, lines, types = docTypes });
        }

        var userTrees = ctx.UserDocuments.Select(d => d.Tree).ToHashSet();
        var diags = ctx.Compilation.GetDiagnostics()
            .Where(d => d.Location.SourceTree is not null && userTrees.Contains(d.Location.SourceTree))
            .ToArray();

        int errors = diags.Count(d => d.Severity == DiagnosticSeverity.Error);
        int warnings = diags.Count(d => d.Severity == DiagnosticSeverity.Warning);

        return RoslynHelpers.Ser(new
        {
            projectName = ctx.PrimaryProject.Name,
            rootPath = ctx.RootPath,
            isFromDisk = ctx.IsFromDisk,
            fileCount = filesOverview.Count,
            totalLines,
            namespaces = namespaces.OrderBy(x => x).ToArray(),
            typeBreakdown = new
            {
                total = classCount + interfaceCount + recordCount + structCount + enumCount,
                classes = classCount,
                interfaces = interfaceCount,
                records = recordCount,
                structs = structCount,
                enums = enumCount
            },
            totalMethods,
            totalProperties,
            entryPoints,
            compilationHealth = new
            {
                ok = errors == 0,
                errorCount = errors,
                warningCount = warnings
            },
            files = filesOverview
        });
    }
}
