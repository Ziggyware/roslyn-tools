using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Humanizer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;

namespace RoslynMcp;

[McpServerToolType]
internal static class AnalysisTools
{
    [McpServerTool, Description(
        "Compile a C# source string, multi-file JSON map, .cs file, .csproj, .sln, .slnx, or directory with full .NET 8 BCL references. " +
        "Returns compiler diagnostics with 1-based line/column, offending source lines, and semantic fix suggestions " +
        "(such as candidate 'using' namespaces for missing types or available method overloads).")]
    public static string CompileAndDiagnose(string sourceOrPath, string minSeverity = "Warning")
    {
        using var ctx = WorkspaceEngine.Load(sourceOrPath);
        if (!Enum.TryParse<DiagnosticSeverity>(minSeverity, ignoreCase: true, out var minSev))
            throw new ArgumentException($"Unrecognized DiagnosticSeverity '{minSeverity}'. Expected: Hidden, Info, Warning, or Error.");

        var userTrees = ctx.UserDocuments.Select(d => d.Tree).ToHashSet();
        var allDiags = ctx.Compilation.GetDiagnostics()
            .Where(d => d.Location.SourceTree is not null && userTrees.Contains(d.Location.SourceTree))
            .ToArray();

        int errorCount = allDiags.Count(d => d.Severity == DiagnosticSeverity.Error);
        int warningCount = allDiags.Count(d => d.Severity == DiagnosticSeverity.Warning);
        int infoCount = allDiags.Count(d => d.Severity == DiagnosticSeverity.Info);

        var filtered = allDiags
            .Where(d => d.Severity >= minSev)
            .OrderByDescending(d => d.Severity)
            .ThenBy(d => d.Location.SourceTree?.FilePath)
            .ThenBy(d => d.Location.SourceSpan.Start)
            .Take(100)
            .Select(d =>
            {
                var doc = ctx.UserDocuments.First(u => u.Tree == d.Location.SourceTree);
                return new
                {
                    id = d.Id,
                    severity = d.Severity.ToString(),
                    message = d.GetMessage(),
                    location = WorkspaceEngine.FormatLocation(d.Location),
                    suggestions = BuildFixSuggestions(ctx, doc, d)
                };
            })
            .ToArray();

        return RoslynHelpers.Ser(new
        {
            ok = errorCount == 0,
            fileCount = ctx.UserDocuments.Count(),
            errorCount,
            warningCount,
            infoCount,
            summary = $"{"error".ToQuantity(errorCount)}, {"warning".ToQuantity(warningCount)}",
            diagnostics = filtered
        });
    }

    [McpServerTool, Description(
        "Compute comprehensive Roslyn code quality and complexity metrics for all types and methods in a codebase: " +
        "Cyclomatic Complexity, Cognitive Complexity, Max Nesting Depth, Lines of Code (LOC), Parameter Count, " +
        "Efferent Coupling (Ce), Maintainability Index (0-100), and refactoring hotspots.")]
    public static string GetCodeMetrics(string sourceOrPath, int complexityThreshold = 10)
    {
        if (complexityThreshold <= 0)
            throw new ArgumentException("complexityThreshold must be >= 1.", nameof(complexityThreshold));

        using var ctx = WorkspaceEngine.Load(sourceOrPath);
        var typeMetricsList = new List<object>();
        var hotspots = new List<object>();

        int totalMethods = 0, totalLoc = 0, maxCyclomatic = 0, maxCognitive = 0;

        foreach (var doc in ctx.UserDocuments)
        {
            var model = doc.Model;
            foreach (var typeDecl in doc.Tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var typeSym = model.GetDeclaredSymbol(typeDecl);
                var methodRecords = new List<object>();
                var typeCoupledTypes = new HashSet<string>(StringComparer.Ordinal);

                foreach (var methodDecl in typeDecl.Members.OfType<BaseMethodDeclarationSyntax>())
                {
                    totalMethods++;
                    var mSym = model.GetDeclaredSymbol(methodDecl);
                    var mName = methodDecl switch
                    {
                        MethodDeclarationSyntax md => md.Identifier.ValueText,
                        ConstructorDeclarationSyntax cd => cd.Identifier.ValueText + ".ctor",
                        _ => mSym?.Name ?? methodDecl.Kind().ToString()
                    };

                    var lr = methodDecl.LineRange();
                    int loc = lr.LineCount;
                    totalLoc += loc;

                    int cyclomatic = CalculateCyclomaticComplexity(methodDecl);
                    var (cognitive, maxNesting) = CalculateCognitiveComplexityAndNesting(methodDecl);
                    int paramCount = methodDecl.ParameterList.Parameters.Count;
                    int localCount = methodDecl.DescendantNodes().OfType<VariableDeclaratorSyntax>().Count();
                    int stmtCount = methodDecl.DescendantNodes().OfType<StatementSyntax>().Count(s => s is not BlockSyntax);

                    var referencedTypes = GetReferencedExternalTypes(model, methodDecl, typeSym);
                    foreach (var rt in referencedTypes) typeCoupledTypes.Add(rt);

                    double maintainabilityIndex = ComputeMaintainabilityIndex(loc, cyclomatic, methodDecl);
                    maxCyclomatic = Math.Max(maxCyclomatic, cyclomatic);
                    maxCognitive = Math.Max(maxCognitive, cognitive);

                    var sig = mSym?.MinDisplay() ?? mName;
                    methodRecords.Add(new
                    {
                        name = mName,
                        signature = sig,
                        file = doc.Name,
                        startLine = lr.StartLine,
                        endLine = lr.EndLine,
                        loc,
                        statementCount = stmtCount,
                        cyclomaticComplexity = cyclomatic,
                        cognitiveComplexity = cognitive,
                        maxNestingDepth = maxNesting,
                        parameterCount = paramCount,
                        localVariableCount = localCount,
                        efferentCoupling = referencedTypes.Count,
                        maintainabilityIndex = Math.Round(maintainabilityIndex, 1)
                    });

                    if (cyclomatic >= complexityThreshold || cognitive >= complexityThreshold || maxNesting >= 4 || paramCount >= 6)
                    {
                        var reasons = new List<string>();
                        if (cyclomatic >= complexityThreshold)
                            reasons.Add($"High cyclomatic complexity ({cyclomatic} >= {complexityThreshold})");
                        if (cognitive >= complexityThreshold)
                            reasons.Add($"High cognitive complexity ({cognitive} >= {complexityThreshold})");
                        if (maxNesting >= 4)
                            reasons.Add($"Deep control-flow nesting (depth {maxNesting}) — consider guard clauses or ExtractMethod");
                        if (paramCount >= 6)
                            reasons.Add($"Large parameter list ({paramCount} params) — consider introducing a parameter object/record");

                        hotspots.Add(new
                        {
                            method = sig,
                            file = doc.Name,
                            startLine = lr.StartLine,
                            endLine = lr.EndLine,
                            cyclomaticComplexity = cyclomatic,
                            cognitiveComplexity = cognitive,
                            maxNestingDepth = maxNesting,
                            maintainabilityIndex = Math.Round(maintainabilityIndex, 1),
                            recommendations = reasons
                        });
                    }
                }

                var tLr = typeDecl.LineRange();
                typeMetricsList.Add(new
                {
                    type = typeSym?.MinDisplay() ?? typeDecl.Identifier.ValueText,
                    kind = typeDecl.Keyword.ValueText,
                    file = doc.Name,
                    startLine = tLr.StartLine,
                    endLine = tLr.EndLine,
                    fieldCount = typeDecl.Members.OfType<FieldDeclarationSyntax>().Sum(f => f.Declaration.Variables.Count),
                    propertyCount = typeDecl.Members.OfType<PropertyDeclarationSyntax>().Count(),
                    methodCount = methodRecords.Count,
                    efferentCoupling = typeCoupledTypes.Count,
                    coupledTypes = typeCoupledTypes.OrderBy(x => x).Take(25).ToArray(),
                    methods = methodRecords
                });
            }
        }

        return RoslynHelpers.Ser(new
        {
            summary = new
            {
                fileCount = ctx.UserDocuments.Count(),
                typeCount = typeMetricsList.Count,
                methodCount = totalMethods,
                totalMethodLoc = totalLoc,
                maxCyclomaticComplexity = maxCyclomatic,
                maxCognitiveComplexity = maxCognitive,
                hotspotCount = hotspots.Count
            },
            hotspots,
            types = typeMetricsList
        });
    }

    [McpServerTool, Description(
        "Build an architectural dependency graph across a codebase using Roslyn SemanticModel. " +
        "granularity: 'type' | 'namespace' | 'file'. " +
        "Computes Afferent Coupling (Ca), Efferent Coupling (Ce), Instability (I), detects circular dependencies " +
        "via Tarjan's SCC algorithm, and outputs a Mermaid diagram alongside structured JSON.")]
    public static string GetDependencyGraph(string sourceOrPath, string granularity = "type")
    {
        string mode = granularity.Trim().ToLowerInvariant();
        if (mode is not ("type" or "namespace" or "file"))
            throw new ArgumentException($"Unrecognized granularity '{granularity}'. Expected: 'type', 'namespace', or 'file'.");

        using var ctx = WorkspaceEngine.Load(sourceOrPath);
        var workspaceTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var typeToDoc = new Dictionary<INamedTypeSymbol, DocumentEntry>(SymbolEqualityComparer.Default);

        foreach (var doc in ctx.UserDocuments)
        {
            foreach (var td in doc.Tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (doc.Model.GetDeclaredSymbol(td) is INamedTypeSymbol ts)
                {
                    workspaceTypes.Add(ts.OriginalDefinition);
                    typeToDoc[ts.OriginalDefinition] = doc;
                }
            }
        }

        string GetNodeId(INamedTypeSymbol sym) => mode switch
        {
            "namespace" => sym.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "(global)",
            "file" => typeToDoc.TryGetValue(sym.OriginalDefinition, out var d) ? d.Name : sym.Name,
            _ => sym.MinDisplay()
        };

        var nodes = new HashSet<string>(StringComparer.Ordinal);
        var edgeReasons = new Dictionary<(string From, string To), HashSet<string>>();

        void AddEdge(INamedTypeSymbol fromType, INamedTypeSymbol? toType, string kind)
        {
            if (toType is null) return;
            var origTo = toType.OriginalDefinition;
            if (!workspaceTypes.Contains(origTo)) return;

            var u = GetNodeId(fromType);
            var v = GetNodeId(origTo);
            nodes.Add(u);
            nodes.Add(v);
            if (u == v) return;

            var key = (u, v);
            if (!edgeReasons.TryGetValue(key, out var reasons))
            {
                reasons = new HashSet<string>(StringComparer.Ordinal);
                edgeReasons[key] = reasons;
            }
            reasons.Add(kind);
        }

        foreach (var doc in ctx.UserDocuments)
        {
            var model = doc.Model;
            foreach (var td in doc.Tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(td) is not INamedTypeSymbol fromSym) continue;
                nodes.Add(GetNodeId(fromSym));

                if (fromSym.BaseType is not null) AddEdge(fromSym, fromSym.BaseType, "Inherits");
                foreach (var iface in fromSym.Interfaces) AddEdge(fromSym, iface, "Implements");

                foreach (var desc in td.DescendantNodes())
                {
                    var sym = model.GetSymbolInfo(desc).Symbol;
                    switch (sym)
                    {
                        case INamedTypeSymbol referencedType:
                            AddEdge(fromSym, referencedType, "ReferencesType");
                            break;
                        case IMethodSymbol methodSym:
                            AddEdge(fromSym, methodSym.ContainingType, "CallsMethod");
                            if (methodSym.ReturnType is INamedTypeSymbol retNamed)
                                AddEdge(fromSym, retNamed, "UsesReturnType");
                            break;
                        case IPropertySymbol propSym:
                            AddEdge(fromSym, propSym.ContainingType, "AccessesProperty");
                            break;
                        case IFieldSymbol fieldSym:
                            AddEdge(fromSym, fieldSym.ContainingType, "AccessesField");
                            break;
                    }
                }
            }
        }

        var nodeMetrics = nodes.OrderBy(n => n).Select(n =>
        {
            var outgoing = edgeReasons.Keys.Where(e => e.From == n).Select(e => e.To).ToArray();
            var incoming = edgeReasons.Keys.Where(e => e.To == n).Select(e => e.From).ToArray();
            int ce = outgoing.Length, ca = incoming.Length;
            return new
            {
                id = n,
                afferentCouplingCa = ca,
                efferentCouplingCe = ce,
                instability = (ca + ce) == 0 ? 0.0 : Math.Round((double)ce / (ca + ce), 2),
                dependsOn = outgoing,
                dependedOnBy = incoming
            };
        }).ToArray();

        var edges = edgeReasons
            .OrderBy(kv => kv.Key.From).ThenBy(kv => kv.Key.To)
            .Select(kv => new
            {
                from = kv.Key.From,
                to = kv.Key.To,
                relationship = string.Join(", ", kv.Value.OrderBy(x => x))
            })
            .ToArray();

        var adjacency = nodes.ToDictionary(
            n => n,
            n => edgeReasons.Keys.Where(e => e.From == n).Select(e => e.To).ToList(),
            StringComparer.Ordinal);
        var cycles = FindStronglyConnectedCycles(adjacency);

        var mermaid = new StringBuilder("graph LR\n");
        foreach (var edge in edges)
            mermaid.AppendLine($"    {SanitizeMermaidId(edge.from)}[\"{edge.from}\"] -->|{edge.relationship}| {SanitizeMermaidId(edge.to)}[\"{edge.to}\"]");

        return RoslynHelpers.Ser(new
        {
            granularity = mode,
            nodeCount = nodes.Count,
            edgeCount = edges.Length,
            hasCircularDependencies = cycles.Count > 0,
            circularDependencies = cycles,
            nodes = nodeMetrics,
            edges,
            mermaidDiagram = mermaid.ToString().TrimEnd()
        });
    }

    [McpServerTool, Description(
        "Compare two versions of a C# file or codebase at the Roslyn Symbol & AST level (not just line diffs). " +
        "Detects breaking public API changes, added/removed types and members, signature modifications, " +
        "and methods whose implementation body changed.")]
    public static string SemanticDiff(string oldSourceOrPath, string newSourceOrPath)
    {
        using var oldCtx = WorkspaceEngine.Load(oldSourceOrPath);
        using var newCtx = WorkspaceEngine.Load(newSourceOrPath);

        var oldSymbols = CollectSymbolInventory(oldCtx);
        var newSymbols = CollectSymbolInventory(newCtx);

        var breakingChanges = new List<object>();
        var addedApis = new List<object>();
        var removedNonPublic = new List<object>();
        var bodyModifiedMethods = new List<object>();

        foreach (var (key, oldItem) in oldSymbols)
        {
            if (!newSymbols.TryGetValue(key, out var newItem))
            {
                if (oldItem.Accessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal)
                {
                    breakingChanges.Add(new
                    {
                        kind = "RemovedPublicSymbol",
                        symbol = oldItem.Signature,
                        symbolKind = oldItem.Kind,
                        previousAccessibility = oldItem.Accessibility.ToString()
                    });
                }
                else
                {
                    removedNonPublic.Add(new { symbol = oldItem.Signature, symbolKind = oldItem.Kind });
                }
                continue;
            }

            if (IsAccessibilityReduced(oldItem.Accessibility, newItem.Accessibility))
            {
                breakingChanges.Add(new
                {
                    kind = "ReducedVisibility",
                    symbol = newItem.Signature,
                    oldVisibility = oldItem.Accessibility.ToString(),
                    newVisibility = newItem.Accessibility.ToString()
                });
            }

            if (!string.Equals(oldItem.ReturnOrValueType, newItem.ReturnOrValueType, StringComparison.Ordinal))
            {
                breakingChanges.Add(new
                {
                    kind = "ChangedTypeOrReturnType",
                    symbol = newItem.Signature,
                    oldType = oldItem.ReturnOrValueType,
                    newType = newItem.ReturnOrValueType
                });
            }

            if (!string.IsNullOrEmpty(oldItem.NormalizedBody) &&
                !string.Equals(oldItem.NormalizedBody, newItem.NormalizedBody, StringComparison.Ordinal))
            {
                bodyModifiedMethods.Add(new
                {
                    symbol = newItem.Signature,
                    file = newItem.File,
                    line = newItem.Line,
                    bodyDiff = WorkspaceEngine.BuildDiffSummary(oldItem.RawBody, newItem.RawBody)
                });
            }
        }

        foreach (var (key, newItem) in newSymbols)
        {
            if (!oldSymbols.ContainsKey(key))
            {
                addedApis.Add(new
                {
                    symbol = newItem.Signature,
                    symbolKind = newItem.Kind,
                    accessibility = newItem.Accessibility.ToString(),
                    file = newItem.File,
                    line = newItem.Line
                });
            }
        }

        return RoslynHelpers.Ser(new
        {
            hasBreakingChanges = breakingChanges.Count > 0,
            breakingChangeCount = breakingChanges.Count,
            addedCount = addedApis.Count,
            removedNonPublicCount = removedNonPublic.Count,
            bodyModifiedCount = bodyModifiedMethods.Count,
            breakingChanges,
            addedSymbols = addedApis,
            removedNonPublicSymbols = removedNonPublic,
            modifiedMethodBodies = bodyModifiedMethods
        });
    }

    [McpServerTool, Description(
        "Extract a compact, token-efficient C# API contract skeleton of a file or entire codebase (namespaces, types, " +
        "base types, attributes, and member signatures with method bodies stripped). " +
        "Ideal for allowing an AI agent to understand a large codebase in minimal context tokens.")]
    public static string GetPublicApiSurface(
        string sourceOrPath,
        bool includeInternal = true,
        bool includePrivate = false,
        bool includeDocComments = true)
    {
        using var ctx = WorkspaceEngine.Load(sourceOrPath);
        var rewriter = new ApiSkeletonRewriter(includeInternal, includePrivate);

        int originalTokens = 0;
        var skeletons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var combined = new StringBuilder();

        foreach (var doc in ctx.UserDocuments)
        {
            originalTokens += RoslynHelpers.Tok(doc.Source);
            var rewritten = rewriter.Visit(doc.Tree.GetRoot());
            if (rewritten is null) continue;

            var formatted = rewritten.FormatWithRoslyn().Trim();
            if (!includeDocComments)
                formatted = StripComments(formatted);

            skeletons[doc.Name] = formatted;
            combined.AppendLine($"// === {doc.Name} ===");
            combined.AppendLine(formatted);
            combined.AppendLine();
        }

        var combinedText = combined.ToString().TrimEnd();
        int skeletonTokens = RoslynHelpers.Tok(combinedText);
        double reductionPct = originalTokens == 0 ? 0 : Math.Round((1.0 - (double)skeletonTokens / originalTokens) * 100.0, 1);

        return RoslynHelpers.Ser(new
        {
            fileCount = skeletons.Count,
            originalTokens,
            skeletonTokens,
            tokenReductionPercent = reductionPct,
            combinedSkeleton = combinedText,
            files = skeletons
        });
    }

    private static string[] BuildFixSuggestions(WorkspaceContext ctx, DocumentEntry doc, Diagnostic diag)
    {
        var suggestions = new List<string>();
        var root = doc.Tree.GetRoot();

        if (diag.Id is "CS0246" or "CS0103" && diag.Location.IsInSource)
        {
            var id = root.FindNode(diag.Location.SourceSpan)
                .DescendantNodesAndSelf().OfType<SimpleNameSyntax>().FirstOrDefault()?.Identifier.ValueText;
            if (!string.IsNullOrWhiteSpace(id))
            {
                foreach (var ns in FindNamespacesContainingType(ctx.Compilation.GlobalNamespace, id).Take(5))
                    suggestions.Add($"Add 'using {ns};' (or call OrganizeUsings)");
            }
        }
        else if (diag.Id is "CS1501" or "CS7036" or "CS1503" && diag.Location.IsInSource)
        {
            var inv = root.FindNode(diag.Location.SourceSpan)
                .AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
            if (inv is not null)
            {
                foreach (var cand in doc.Model.GetSymbolInfo(inv).CandidateSymbols.Take(5))
                    suggestions.Add($"Candidate overload: {cand.MinDisplay()}");
            }
        }

        return suggestions.ToArray();
    }

    private static List<string> FindNamespacesContainingType(INamespaceSymbol rootNs, string typeName)
    {
        var list = new List<string>();
        var stack = new Stack<INamespaceSymbol>([rootNs]);

        while (stack.Count > 0)
        {
            var ns = stack.Pop();
            if (!ns.IsGlobalNamespace)
            {
                foreach (var t in ns.GetTypeMembers(typeName))
                {
                    if (t.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal)
                        list.Add(ns.ToDisplayString());
                }
            }
            foreach (var sub in ns.GetNamespaceMembers())
                stack.Push(sub);
        }
        return list.Distinct().OrderBy(x => x.StartsWith("System") ? 0 : 1).ThenBy(x => x.Length).ToList();
    }

    private static int CalculateCyclomaticComplexity(SyntaxNode methodNode)
    {
        int complexity = 1;
        foreach (var node in methodNode.DescendantNodes())
        {
            switch (node)
            {
                case IfStatementSyntax:
                case ForStatementSyntax:
                case ForEachStatementSyntax:
                case ForEachVariableStatementSyntax:
                case WhileStatementSyntax:
                case DoStatementSyntax:
                case CatchClauseSyntax:
                case ConditionalExpressionSyntax:
                case ConditionalAccessExpressionSyntax:
                case SwitchExpressionArmSyntax:
                    complexity++;
                    break;
                case SwitchSectionSyntax section:
                    complexity += section.Labels.Count(l => l is not DefaultSwitchLabelSyntax);
                    break;
                case BinaryExpressionSyntax bin when
                    bin.IsKind(SyntaxKind.LogicalAndExpression) ||
                    bin.IsKind(SyntaxKind.LogicalOrExpression) ||
                    bin.IsKind(SyntaxKind.CoalesceExpression):
                    complexity++;
                    break;
                case BinaryPatternSyntax pat when
                    pat.IsKind(SyntaxKind.AndPattern) ||
                    pat.IsKind(SyntaxKind.OrPattern):
                    complexity++;
                    break;
            }
        }
        return complexity;
    }

    private static (int CognitiveComplexity, int MaxNesting) CalculateCognitiveComplexityAndNesting(SyntaxNode methodNode)
    {
        int cognitive = 0, maxNesting = 0;

        void Walk(SyntaxNode node, int nesting)
        {
            maxNesting = Math.Max(maxNesting, nesting);
            foreach (var child in node.ChildNodes())
            {
                bool increasesNesting = false;
                switch (child)
                {
                    case IfStatementSyntax:
                        if (child.Parent is ElseClauseSyntax)
                        {
                            cognitive += 1;
                            Walk(child, nesting);
                            continue;
                        }
                        cognitive += 1 + nesting;
                        increasesNesting = true;
                        break;
                    case ElseClauseSyntax elseClause when elseClause.Statement is not IfStatementSyntax:
                        cognitive += 1;
                        increasesNesting = true;
                        break;
                    case SwitchStatementSyntax:
                    case SwitchExpressionSyntax:
                    case ForStatementSyntax:
                    case ForEachStatementSyntax:
                    case ForEachVariableStatementSyntax:
                    case WhileStatementSyntax:
                    case DoStatementSyntax:
                    case CatchClauseSyntax:
                    case ConditionalExpressionSyntax:
                        cognitive += 1 + nesting;
                        increasesNesting = true;
                        break;
                    case LambdaExpressionSyntax:
                    case LocalFunctionStatementSyntax:
                        increasesNesting = true;
                        break;
                    case BinaryExpressionSyntax bin when
                        (bin.IsKind(SyntaxKind.LogicalAndExpression) || bin.IsKind(SyntaxKind.LogicalOrExpression)) &&
                        (bin.Parent is not BinaryExpressionSyntax parentBin || parentBin.Kind() != bin.Kind()):
                        cognitive += 1;
                        break;
                }
                Walk(child, increasesNesting ? nesting + 1 : nesting);
            }
        }

        Walk(methodNode, 0);
        return (cognitive, maxNesting);
    }

    private static HashSet<string> GetReferencedExternalTypes(SemanticModel model, SyntaxNode node, ISymbol? enclosingType)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in node.DescendantNodes())
        {
            var typeSym = model.GetTypeInfo(child).Type ?? (model.GetSymbolInfo(child).Symbol as ITypeSymbol);
            if (typeSym is not INamedTypeSymbol { SpecialType: SpecialType.None } named) continue;
            if (enclosingType is not null && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, enclosingType.OriginalDefinition))
                continue;
            set.Add(named.MinDisplay());
        }
        return set;
    }

    private static double ComputeMaintainabilityIndex(int loc, int cyclomatic, SyntaxNode methodNode)
    {
        int tokenCount = Math.Max(1, methodNode.DescendantTokens().Count());
        int distinctTokens = Math.Max(1, methodNode.DescendantTokens().Select(t => t.Text).Distinct().Count());
        double halsteadVolume = tokenCount * Math.Log2(Math.Max(2, distinctTokens));
        double rawMi = 171.0 - 5.2 * Math.Log(Math.Max(1.0, halsteadVolume)) - 0.23 * cyclomatic - 16.2 * Math.Log(Math.Max(1, loc));
        return Math.Clamp(rawMi * 100.0 / 171.0, 0.0, 100.0);
    }

    private static List<string[]> FindStronglyConnectedCycles(Dictionary<string, List<string>> adjacency)
    {
        int index = 0;
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowlink = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var cycles = new List<string[]>();

        void StrongConnect(string v)
        {
            indices[v] = lowlink[v] = index++;
            stack.Push(v);
            onStack.Add(v);

            if (adjacency.TryGetValue(v, out var neighbors))
            {
                foreach (var w in neighbors)
                {
                    if (!indices.ContainsKey(w))
                    {
                        StrongConnect(w);
                        lowlink[v] = Math.Min(lowlink[v], lowlink[w]);
                    }
                    else if (onStack.Contains(w))
                    {
                        lowlink[v] = Math.Min(lowlink[v], indices[w]);
                    }
                }
            }

            if (lowlink[v] == indices[v])
            {
                var component = new List<string>();
                string w;
                do
                {
                    w = stack.Pop();
                    onStack.Remove(w);
                    component.Add(w);
                } while (w != v);

                if (component.Count > 1)
                {
                    component.Reverse();
                    component.Add(component[0]);
                    cycles.Add(component.ToArray());
                }
            }
        }

        foreach (var node in adjacency.Keys)
        {
            if (!indices.ContainsKey(node))
                StrongConnect(node);
        }

        return cycles;
    }

    private static string SanitizeMermaidId(string id)
    {
        var sb = new StringBuilder(id.Length);
        foreach (var c in id)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    [DebuggerDisplay("{ToString(),nq}")]
    private sealed record SymbolInventoryItem(
        string Key,
        string Signature,
        string Kind,
        Accessibility Accessibility,
        string ReturnOrValueType,
        string NormalizedBody,
        string RawBody,
        string File,
        int Line)
    {
        public override string ToString() => $"{Kind} {Signature} [{Accessibility}] ({File}:L{Line})";
    }

    private static Dictionary<string, SymbolInventoryItem> CollectSymbolInventory(WorkspaceContext ctx)
    {
        var map = new Dictionary<string, SymbolInventoryItem>(StringComparer.Ordinal);
        foreach (var doc in ctx.UserDocuments)
        {
            foreach (var node in doc.Tree.GetRoot().DescendantNodes())
            {
                if (node is not (BaseTypeDeclarationSyntax or MethodDeclarationSyntax
                    or ConstructorDeclarationSyntax or PropertyDeclarationSyntax or FieldDeclarationSyntax))
                {
                    continue;
                }

                if (node is FieldDeclarationSyntax fd)
                {
                    foreach (var v in fd.Declaration.Variables)
                    {
                        if (doc.Model.GetDeclaredSymbol(v) is not IFieldSymbol fs) continue;
                        var key = fs.FullDisplay();
                        map[key] = new SymbolInventoryItem(
                            key, fs.MinDisplay(), "Field", fs.DeclaredAccessibility,
                            fs.Type.MinDisplay(),
                            v.Initializer?.Value.NormalizeWhitespace().ToFullString() ?? "",
                            v.Initializer?.Value.ToString() ?? "",
                            doc.Name, v.LineRange().StartLine);
                    }
                    continue;
                }

                var sym = doc.Model.GetDeclaredSymbol(node);
                if (sym is null) continue;

                var idKey = sym.FullDisplay();
                SyntaxNode? bodyNode = node switch
                {
                    MethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
                    ConstructorDeclarationSyntax c => (SyntaxNode?)c.Body ?? c.ExpressionBody,
                    PropertyDeclarationSyntax p => p.ExpressionBody,
                    _ => null
                };

                map[idKey] = new SymbolInventoryItem(
                    idKey, sym.MinDisplay(), sym.Kind.ToString(), sym.DeclaredAccessibility,
                    sym.GetValueOrReturnTypeString(),
                    bodyNode?.NormalizeWhitespace().ToFullString() ?? "",
                    bodyNode?.ToFullString().Trim() ?? "",
                    doc.Name, node.LineRange().StartLine);
            }
        }
        return map;
    }

    private static bool IsAccessibilityReduced(Accessibility oldAcc, Accessibility newAcc)
    {
        static int Rank(Accessibility a) => a switch
        {
            Accessibility.Public => 5,
            Accessibility.ProtectedOrInternal => 4,
            Accessibility.Internal or Accessibility.Protected => 3,
            Accessibility.ProtectedAndInternal => 2,
            Accessibility.Private => 1,
            _ => 0
        };
        return Rank(newAcc) < Rank(oldAcc);
    }

    private static string StripComments(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var commentTrivia = root.DescendantTrivia()
            .Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                        t.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
                        t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                        t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            .ToList();
        return root.ReplaceTrivia(commentTrivia, (_, _) => default).NormalizeWhitespace().ToFullString();
    }

    private sealed class ApiSkeletonRewriter(bool includeInternal, bool includePrivate) : CSharpSyntaxRewriter
    {
        private bool ShouldKeep(SyntaxTokenList modifiers, bool isInterfaceMember = false)
        {
            if (isInterfaceMember || includePrivate) return true;
            if (modifiers.Any(SyntaxKind.PublicKeyword) || modifiers.Any(SyntaxKind.ProtectedKeyword))
                return true;
            return includeInternal && (modifiers.Any(SyntaxKind.InternalKeyword) || !modifiers.Any(SyntaxKind.PrivateKeyword));
        }

        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
            ShouldKeep(node.Modifiers, node.Parent is InterfaceDeclarationSyntax)
                ? node.WithBody(null).WithExpressionBody(null).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                : null;

        public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node) =>
            ShouldKeep(node.Modifiers)
                ? node.WithBody(null).WithExpressionBody(null).WithInitializer(null).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                : null;

        public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            if (!ShouldKeep(node.Modifiers, node.Parent is InterfaceDeclarationSyntax))
                return null;

            if (node.ExpressionBody is not null)
            {
                return node
                    .WithExpressionBody(null)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None))
                    .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))));
            }

            if (node.AccessorList is not null)
            {
                var accessors = node.AccessorList.Accessors.Select(a =>
                    a.WithBody(null).WithExpressionBody(null).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
                return node
                    .WithInitializer(null)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None))
                    .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)));
            }

            return node;
        }

        public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node) =>
            ShouldKeep(node.Modifiers) ? node : null;

        public override string ToString() =>
            $"ApiSkeletonRewriter(includeInternal={includeInternal}, includePrivate={includePrivate})";
    }
}
