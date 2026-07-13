using System.Text.Json;
using System.Xml.Linq;
using ModelContextProtocol.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynMcp;

[McpServerToolType]
internal static class ProjectTools
{
    static readonly JsonSerializerOptions J = new() { WriteIndented = false };
    static string Ser(object o) => JsonSerializer.Serialize(o, J);

    [McpServerTool, System.ComponentModel.Description(
        "Parse diagnostics across N sources in one call. sourcesJson: JSON object mapping " +
        "a caller-chosen label (e.g. filename) to source text. No cross-file linking — each " +
        "parsed independently, same as calling Parse per file.")]
    public static string ParseMany(string sourcesJson)
    {
        var sources = JsonSerializer.Deserialize<Dictionary<string, string>>(sourcesJson)
            ?? throw new ArgumentException("sourcesJson did not parse to an object");
        var results = sources.ToDictionary(kv => kv.Key, kv =>
        {
            var d = CSharpSyntaxTree.ParseText(kv.Value).GetDiagnostics().ToArray();
            return (object)new
            {
                ok = !d.Any(x => x.Severity == DiagnosticSeverity.Error),
                errorCount = d.Count(x => x.Severity == DiagnosticSeverity.Error)
            };
        });
        return Ser(new { fileCount = sources.Count, results });
    }

    [McpServerTool, System.ComponentModel.Description(
        "Structural read of a .csproj: TargetFramework(s), explicit <Compile> items, " +
        "<PackageReference> and <ProjectReference> entries. Does NOT resolve references, " +
        "does NOT check they exist, does NOT build a Compilation. Metadata extraction only.")]
    public static string ReadCsproj(string xml)
    {
        var doc = XDocument.Parse(xml);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        string[] G(string el, string attr) =>
            doc.Descendants(ns + el).Select(e => e.Attribute(attr)?.Value ?? "").Where(v => v != "").ToArray();
        return Ser(new
        {
            targetFrameworks = doc.Descendants(ns + "TargetFramework").Select(e => e.Value)
                .Concat(doc.Descendants(ns + "TargetFrameworks").SelectMany(e => e.Value.Split(';')))
                .ToArray(),
            explicitCompileItems = G("Compile", "Include"),
            packageReferences = doc.Descendants(ns + "PackageReference")
                .Select(e => new { id = e.Attribute("Include")?.Value, version = e.Attribute("Version")?.Value })
                .ToArray(),
            projectReferences = G("ProjectReference", "Include"),
            outputType = doc.Descendants(ns + "OutputType").Select(e => e.Value).FirstOrDefault() ?? "Library"
        });
    }

    [McpServerTool, System.ComponentModel.Description(
        "Structural read of a .slnx (XML solution format): declared projects and their paths. " +
        "V: .slnx schema recollection is unverified against current tooling — if this throws " +
        "on a real file, the schema differs from what this expects; report the raw parse error.")]
    public static string ReadSlnx(string xml)
    {
        var doc = XDocument.Parse(xml);
        var projects = doc.Descendants("Project")
            .Select(e => new { path = e.Attribute("Path")?.Value })
            .Where(p => p.path is not null)
            .ToArray();
        return Ser(new { projectCount = projects.Length, projects });
    }
}
