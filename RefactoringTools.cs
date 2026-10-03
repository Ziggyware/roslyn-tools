using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Rename;
using ModelContextProtocol.Server;

namespace RoslynMcp;

[McpServerToolType]
internal static class RefactoringTools
{
    [McpServerTool, Description(
        "Semantically rename any symbol (class, interface, method, property, field, parameter, or local variable) " +
        "across a file, project, or solution using Roslyn's Renamer. Updates only genuine symbol references, " +
        "overrides, and implementations — never unrelated identifiers or substrings.")]
    public static async Task<string> RenameSymbol(
        string sourceOrPath,
        string symbolOrLocation,
        string newName,
        bool writeToDisk = false)
    {
        if (!SyntaxFacts.IsValidIdentifier(newName))
            throw new ArgumentException($"'{newName}' is not a valid C# identifier.");

        using var ctx = WorkspaceEngine.Load(sourceOrPath);
        var target = WorkspaceEngine.ResolveTarget(ctx, symbolOrLocation);
        var symbol = target.Symbol
            ?? throw new ArgumentException($"Could not resolve a symbol at '{symbolOrLocation}'.");

        var oldName = symbol.Name;
        var renamedSolution = await Renamer.RenameSymbolAsync(ctx.Solution, symbol, new SymbolRenameOptions(), newName);
        var renamedProject = renamedSolution.GetProject(ctx.PrimaryProject.Id)!;
        var newCompilation = (CSharpCompilation)(await renamedProject.GetCompilationAsync())!;

        var modifiedFiles = new List<object>();
        string? primaryNewSource = null;

        foreach (var origDoc in ctx.UserDocuments)
        {
            var updatedDoc = renamedProject.GetDocument(origDoc.Id);
            if (updatedDoc is null) continue;
            var newText = (await updatedDoc.GetTextAsync()).ToString();

            if (origDoc.Id == ctx.PrimaryDocument.Id)
                primaryNewSource = newText;

            if (!string.Equals(origDoc.Source, newText, StringComparison.Ordinal))
            {
                bool saved = false;
                if (writeToDisk && !string.IsNullOrWhiteSpace(origDoc.FilePath) && File.Exists(origDoc.FilePath))
                {
                    File.WriteAllText(origDoc.FilePath, newText);
                    saved = true;
                }

                modifiedFiles.Add(new
                {
                    file = origDoc.Name,
                    filePath = origDoc.FilePath,
                    savedToDisk = saved,
                    diff = WorkspaceEngine.BuildDiffSummary(origDoc.Source, newText),
                    updatedSource = newText
                });
            }
        }

        int errorsBefore = ctx.Compilation.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);
        int errorsAfter = newCompilation.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);

        return RoslynHelpers.Ser(new
        {
            symbolKind = symbol.Kind.ToString(),
            oldName,
            newName,
            modifiedFileCount = modifiedFiles.Count,
            errorsBefore,
            errorsAfter,
            introducedErrors = errorsAfter > errorsBefore,
            primaryUpdatedSource = primaryNewSource ?? ctx.PrimaryDocument.Source,
            modifiedFiles
        });
    }

    [McpServerTool, Description(
        "Add a new member or replace an existing member (method, property, field, constructor, event, nested type) " +
        "inside a target type by AST structure — no character offsets required. " +
        "For methods/constructors, matches existing overloads by parameter types so other overloads are preserved.")]
    public static string AddOrReplaceMember(
        string sourceOrPath,
        string typeName,
        string memberCode,
        bool replaceExisting = true,
        bool writeToDisk = false)
    {
        var parsedMember = SyntaxFactory.ParseMemberDeclaration(memberCode.Trim())
            ?? throw new ArgumentException("Could not parse memberCode as a valid C# member declaration.");

        var parseDiagnostics = parsedMember.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (parseDiagnostics.Length > 0)
            throw new ArgumentException($"memberCode has syntax errors: {string.Join("; ", parseDiagnostics.Select(d => d.GetMessage()))}");

        using var ctx = WorkspaceEngine.Load(sourceOrPath);
        var (doc, typeNode) = FindTypeDeclaration(ctx, typeName);

        var existingMember = FindMatchingMember(typeNode, parsedMember);
        TypeDeclarationSyntax updatedType;
        string action;

        if (existingMember is not null && replaceExisting)
        {
            var replacement = !HasDocumentationComment(parsedMember) && HasDocumentationComment(existingMember)
                ? parsedMember.WithLeadingTrivia(existingMember.GetLeadingTrivia())
                : parsedMember;
            updatedType = typeNode.ReplaceNode(existingMember, replacement);
            action = "replaced";
        }
        else
        {
            updatedType = InsertMemberInCanonicalOrder(typeNode, parsedMember);
            action = "added";
        }

        var outcome = WorkspaceEngine.FinalizeMutation(ctx, doc, doc.Tree.GetRoot().ReplaceNode(typeNode, updatedType), writeToDisk);
        return RoslynHelpers.Ser(new
        {
            action,
            targetType = typeNode.Identifier.Text,
            memberSignature = GetMemberKey(parsedMember),
            file = outcome.File,
            savedToDisk = outcome.SavedToDisk,
            errorsBefore = outcome.ErrorsBefore,
            errorsAfter = outcome.ErrorsAfter,
            warningsAfter = outcome.WarningsAfter,
            diagnostics = outcome.Diagnostics,
            diff = outcome.Diff,
            text = outcome.FormattedSource
        });
    }

    [McpServerTool, Description(
        "Remove a member (method, property, field, constructor, or event) from a type by name or signature " +
        "(e.g. 'CalculateTotal' or 'CalculateTotal(Order, decimal)' for a specific overload). " +
        "Reports any broken references introduced by the removal.")]
    public static string RemoveMember(
        string sourceOrPath,
        string typeName,
        string memberNameOrSignature,
        bool writeToDisk = false)
    {
        using var ctx = WorkspaceEngine.Load(sourceOrPath);
        var (doc, typeNode) = FindTypeDeclaration(ctx, typeName);

        var toRemove = FindMembersByNameOrSignature(typeNode, memberNameOrSignature);
        if (toRemove.Count == 0)
            throw new ArgumentException($"No member matching '{memberNameOrSignature}' found in type '{typeNode.Identifier.Text}'.");

        var updatedType = typeNode.RemoveNodes(toRemove, SyntaxRemoveOptions.KeepNoTrivia)!;
        var outcome = WorkspaceEngine.FinalizeMutation(ctx, doc, doc.Tree.GetRoot().ReplaceNode(typeNode, updatedType), writeToDisk);

        return RoslynHelpers.Ser(new
        {
            targetType = typeNode.Identifier.Text,
            removedCount = toRemove.Count,
            removedMembers = toRemove.Select(GetMemberKey).ToArray(),
            file = outcome.File,
            savedToDisk = outcome.SavedToDisk,
            errorsBefore = outcome.ErrorsBefore,
            errorsAfter = outcome.ErrorsAfter,
            warningsAfter = outcome.WarningsAfter,
            danglingReferenceDiagnostics = outcome.Diagnostics,
            diff = outcome.Diff,
            text = outcome.FormattedSource
        });
    }

    [McpServerTool, Description(
        "Extract statements between [startLine..endLine] (1-based) into a new method using Roslyn DataFlowAnalysis " +
        "and ControlFlowAnalysis. Automatically computes input parameters, single or tuple return values for " +
        "out-flowing variables, async/Task wrapping if 'await' is used, and static modifier if no instance state is accessed.")]
    public static string ExtractMethod(
        string sourceOrPath,
        string methodOrLocation,
        int startLine,
        int endLine,
        string newMethodName,
        bool writeToDisk = false)
    {
        if (!SyntaxFacts.IsValidIdentifier(newMethodName))
            throw new ArgumentException($"'{newMethodName}' is not a valid C# method name.");
        if (startLine <= 0 || endLine < startLine)
            throw new ArgumentException("startLine and endLine must be valid 1-based line numbers with endLine >= startLine.");

        using var ctx = WorkspaceEngine.Load(sourceOrPath);
        var target = WorkspaceEngine.ResolveTarget(ctx, methodOrLocation);
        var doc = target.Document;
        var model = doc.Model;

        var enclosingMethod = target.Node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault()
            ?? doc.Tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m =>
                {
                    var lr = m.LineRange();
                    return lr.StartLine <= startLine && lr.EndLine >= endLine;
                })
            ?? throw new ArgumentException($"Could not locate an enclosing method spanning lines {startLine}..{endLine}.");

        if (enclosingMethod.Body is null)
            throw new InvalidOperationException("Enclosing method does not have a statement block body.");

        var statements = SemanticTools.FindContiguousStatementsInLineRange(doc.Tree, enclosingMethod.Body, startLine, endLine);
        if (statements.Count == 0)
            throw new ArgumentException($"No complete statements found within lines {startLine}..{endLine}.");

        var firstStmt = statements[0];
        var lastStmt = statements[^1];
        var parentBlock = (BlockSyntax)firstStmt.Parent!;

        var controlFlow = model.AnalyzeControlFlow(firstStmt, lastStmt);
        var dataFlow = model.AnalyzeDataFlow(firstStmt, lastStmt);

        if (!dataFlow.Succeeded || !controlFlow.Succeeded)
            throw new InvalidOperationException("Roslyn ControlFlow/DataFlow analysis could not analyze the selected statement range.");

        if (controlFlow.ExitPoints.Any())
            throw new InvalidOperationException(
                "Selected statements contain non-linear control-flow exits (early return/break/continue) that jump outside the block.");

        var inSymbols = dataFlow.DataFlowsIn
            .Where(s => s is ILocalSymbol or IParameterSymbol && !s.IsImplicitlyDeclared && s.Name != "this")
            .ToArray();

        var outSymbols = dataFlow.DataFlowsOut
            .Where(s => s is ILocalSymbol or IParameterSymbol && !s.IsImplicitlyDeclared && s.Name != "this")
            .ToArray();

        var declaredInsideSet = new HashSet<ISymbol>(dataFlow.VariablesDeclared, SymbolEqualityComparer.Default);

        bool containsAwait = statements
            .SelectMany(s => s.DescendantNodesAndSelf())
            .Any(n => n is AwaitExpressionSyntax && n.Ancestors().OfType<AnonymousFunctionExpressionSyntax>().FirstOrDefault() is null);

        bool enclosingIsStatic = enclosingMethod.Modifiers.Any(SyntaxKind.StaticKeyword);
        bool makeStatic = enclosingIsStatic || !StatementsAccessInstanceState(model, statements);

        var paramDecls = inSymbols.Select(s => $"{s.GetValueOrReturnTypeString()} {s.Name}").ToArray();
        var callArgs = inSymbols.Select(s => s.Name).ToArray();

        string rawReturnType;
        StatementSyntax? returnStmt = null;

        if (outSymbols.Length == 0)
        {
            rawReturnType = "void";
        }
        else if (outSymbols.Length == 1)
        {
            rawReturnType = outSymbols[0].GetValueOrReturnTypeString();
            returnStmt = SyntaxFactory.ParseStatement($"return {outSymbols[0].Name};");
        }
        else
        {
            var tupleElements = outSymbols.Select(s => $"{s.GetValueOrReturnTypeString()} {s.Name}");
            rawReturnType = $"({string.Join(", ", tupleElements)})";
            returnStmt = SyntaxFactory.ParseStatement($"return ({string.Join(", ", outSymbols.Select(s => s.Name))});");
        }

        string methodReturnType = containsAwait
            ? (rawReturnType == "void" ? "Task" : $"Task<{rawReturnType}>")
            : rawReturnType;

        string invocationExpr = $"{newMethodName}({string.Join(", ", callArgs)})";
        if (containsAwait)
            invocationExpr = $"await {invocationExpr}";

        StatementSyntax callStmt;
        if (outSymbols.Length == 0)
        {
            callStmt = SyntaxFactory.ParseStatement($"{invocationExpr};");
        }
        else if (outSymbols.Length == 1)
        {
            var outSym = outSymbols[0];
            string lhs = declaredInsideSet.Contains(outSym) ? $"{outSym.GetValueOrReturnTypeString()} {outSym.Name}" : outSym.Name;
            callStmt = SyntaxFactory.ParseStatement($"{lhs} = {invocationExpr};");
        }
        else
        {
            var deconstructParts = outSymbols.Select(s =>
                declaredInsideSet.Contains(s) ? $"{s.GetValueOrReturnTypeString()} {s.Name}" : s.Name);
            callStmt = SyntaxFactory.ParseStatement($"({string.Join(", ", deconstructParts)}) = {invocationExpr};");
        }

        var newBlockStatements = new List<StatementSyntax>();
        bool insertedCall = false;
        foreach (var stmt in parentBlock.Statements)
        {
            if (statements.Contains(stmt))
            {
                if (!insertedCall) { newBlockStatements.Add(callStmt); insertedCall = true; }
            }
            else
            {
                newBlockStatements.Add(stmt);
            }
        }

        var updatedEnclosingMethod = enclosingMethod.ReplaceNode(
            parentBlock,
            parentBlock.WithStatements(SyntaxFactory.List(newBlockStatements)));

        var modifiers = new List<string> { "private" };
        if (makeStatic) modifiers.Add("static");
        if (containsAwait) modifiers.Add("async");

        var newMethodText = new StringBuilder();
        newMethodText.AppendLine($"{string.Join(" ", modifiers)} {methodReturnType} {newMethodName}({string.Join(", ", paramDecls)})");
        newMethodText.AppendLine("{");
        foreach (var s in statements)
            newMethodText.AppendLine(s.ToFullString().Trim());
        if (returnStmt is not null)
            newMethodText.AppendLine(returnStmt.ToFullString().Trim());
        newMethodText.AppendLine("}");

        var parsedNewMethod = (MethodDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration(newMethodText.ToString())!;
        var enclosingType = enclosingMethod.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()
            ?? throw new InvalidOperationException("Enclosing method must be inside a type declaration.");

        int methodIdx = enclosingType.Members.IndexOf(enclosingMethod);
        var updatedMembers = methodIdx >= 0
            ? enclosingType.Members.RemoveAt(methodIdx).Insert(methodIdx, updatedEnclosingMethod).Insert(methodIdx + 1, parsedNewMethod)
            : enclosingType.Members.Add(parsedNewMethod);
        var updatedType = enclosingType.WithMembers(updatedMembers);

        var outcome = WorkspaceEngine.FinalizeMutation(ctx, doc, doc.Tree.GetRoot().ReplaceNode(enclosingType, updatedType), writeToDisk);
        return RoslynHelpers.Ser(new
        {
            extractedMethodName = newMethodName,
            signature = $"{string.Join(" ", modifiers)} {methodReturnType} {newMethodName}({string.Join(", ", paramDecls)})",
            callSite = callStmt.ToString(),
            parameters = paramDecls,
            returnType = methodReturnType,
            isAsync = containsAwait,
            isStatic = makeStatic,
            file = outcome.File,
            savedToDisk = outcome.SavedToDisk,
            errorsAfter = outcome.ErrorsAfter,
            warningsAfter = outcome.WarningsAfter,
            diagnostics = outcome.Diagnostics,
            diff = outcome.Diff,
            text = outcome.FormattedSource
        });
    }

    [McpServerTool, Description(
        "Implement an interface on a class, struct, or record using Roslyn symbol inspection. " +
        "Resolves both workspace interfaces and .NET BCL interfaces (e.g. IDisposable, IAsyncDisposable, IEquatable<T>, IComparable<T>), " +
        "adds the interface to the base list if missing, and generates idiomatic stubs for all unimplemented members.")]
    public static string ImplementInterface(
        string sourceOrPath,
        string className,
        string interfaceName,
        bool explicitImplementation = false,
        bool writeToDisk = false)
    {
        using var ctx = WorkspaceEngine.Load(sourceOrPath);
        var (doc, typeNode) = FindTypeDeclaration(ctx, className);
        var classSymbol = doc.Model.GetDeclaredSymbol(typeNode) as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Could not resolve type symbol for '{className}'.");

        var ifaceSymbol = ResolveInterfaceSymbol(ctx.Compilation, classSymbol, interfaceName)
            ?? throw new ArgumentException($"Could not resolve interface '{interfaceName}' in workspace or .NET runtime assemblies.");

        var allInterfacesToImplement = ifaceSymbol.AllInterfaces.Add(ifaceSymbol);
        var existingMemberNames = new HashSet<string>(typeNode.Members.Select(GetMemberKey), StringComparer.Ordinal);
        var generatedMembers = new List<MemberDeclarationSyntax>();
        var generatedDescriptions = new List<string>();

        foreach (var currentIface in allInterfacesToImplement)
        {
            foreach (var member in currentIface.GetMembers())
            {
                if (member.IsImplicitlyDeclared || member.IsStatic) continue;
                if (classSymbol.FindImplementationForInterfaceMember(member) is not null) continue;

                var stub = GenerateInterfaceMemberStub(currentIface, member, classSymbol, explicitImplementation);
                if (stub is null || !existingMemberNames.Add(GetMemberKey(stub))) continue;

                generatedMembers.Add(stub);
                generatedDescriptions.Add(member.MinDisplay());
            }
        }

        var updatedType = typeNode;
        var ifaceDisplayName = ifaceSymbol.MinDisplay();
        bool alreadyInBaseList = updatedType.BaseList?.Types.Any(bt =>
            string.Equals(bt.Type.ToString().Trim(), ifaceDisplayName, StringComparison.Ordinal) ||
            string.Equals(bt.Type.ToString().Trim(), interfaceName.Trim(), StringComparison.Ordinal)) ?? false;

        if (!alreadyInBaseList)
            updatedType = updatedType.AddBaseListTypes(SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(ifaceDisplayName)));

        foreach (var member in generatedMembers)
            updatedType = InsertMemberInCanonicalOrder(updatedType, member);

        var newRoot = (CompilationUnitSyntax)doc.Tree.GetRoot().ReplaceNode(typeNode, updatedType);
        var ifaceNs = ifaceSymbol.ContainingNamespace?.ToDisplayString();
        if (!string.IsNullOrWhiteSpace(ifaceNs) &&
            !ifaceSymbol.ContainingNamespace!.IsGlobalNamespace &&
            ifaceNs != "System" &&
            !newRoot.Usings.Any(u => u.Name?.ToString() == ifaceNs))
        {
            newRoot = newRoot.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(ifaceNs)));
        }

        var outcome = WorkspaceEngine.FinalizeMutation(ctx, doc, newRoot, writeToDisk);
        return RoslynHelpers.Ser(new
        {
            targetType = typeNode.Identifier.Text,
            implementedInterface = ifaceDisplayName,
            explicitImplementation,
            generatedMemberCount = generatedDescriptions.Count,
            generatedMembers = generatedDescriptions,
            file = outcome.File,
            savedToDisk = outcome.SavedToDisk,
            errorsAfter = outcome.ErrorsAfter,
            warningsAfter = outcome.WarningsAfter,
            diagnostics = outcome.Diagnostics,
            diff = outcome.Diff,
            text = outcome.FormattedSource
        });
    }

    [McpServerTool, Description(
        "Generate boilerplate members on a type using Roslyn semantic inspection: " +
        "generatorKind: 'constructor' (DI constructor for readonly fields & properties), " +
        "'equals_hashcode' (IEquatable<T>, Equals, GetHashCode), " +
        "'tostring' (interpolated ToString override), " +
        "'overrides' (override abstract/virtual members from base class), " +
        "'extract_interface' (create I{TypeName} interface from public instance members).")]
    public static string GenerateTypeMembers(
        string sourceOrPath,
        string typeName,
        string generatorKind = "constructor",
        bool includeNullChecks = true,
        bool writeToDisk = false)
    {
        using var ctx = WorkspaceEngine.Load(sourceOrPath);
        var (doc, typeNode) = FindTypeDeclaration(ctx, typeName);
        var typeSymbol = doc.Model.GetDeclaredSymbol(typeNode) as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Could not resolve symbol for type '{typeName}'.");

        var updatedType = typeNode;
        CompilationUnitSyntax? updatedRoot = null;
        var addedSignatures = new List<string>();

        switch (generatorKind.Trim().ToLowerInvariant())
        {
            case "constructor":
            case "di_constructor":
            {
                var injectable = new List<(string Name, string ParamName, ITypeSymbol Type, bool IsReference)>();
                foreach (var member in typeSymbol.GetMembers())
                {
                    if (member.IsStatic || member.IsImplicitlyDeclared) continue;
                    if (member is IFieldSymbol field && !field.IsConst)
                    {
                        var decl = field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as VariableDeclaratorSyntax;
                        if (decl?.Initializer is not null) continue;
                        injectable.Add((field.Name, RoslynHelpers.ToCamelCaseIdentifier(field.Name), field.Type,
                            field.Type.IsReferenceType && field.NullableAnnotation != NullableAnnotation.Annotated));
                    }
                    else if (member is IPropertySymbol prop && !prop.IsIndexer && prop.SetMethod is null)
                    {
                        var decl = prop.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as PropertyDeclarationSyntax;
                        if (decl?.ExpressionBody is not null || decl?.Initializer is not null) continue;
                        injectable.Add((prop.Name, RoslynHelpers.ToCamelCaseIdentifier(prop.Name), prop.Type,
                            prop.Type.IsReferenceType && prop.NullableAnnotation != NullableAnnotation.Annotated));
                    }
                }

                var paramList = string.Join(", ", injectable.Select(m => $"{m.Type.MinDisplay()} {m.ParamName}"));
                var sb = new StringBuilder();
                sb.AppendLine($"public {typeSymbol.Name}({paramList})");
                sb.AppendLine("{");
                foreach (var m in injectable)
                {
                    if (includeNullChecks && m.IsReference)
                        sb.AppendLine($"    ArgumentNullException.ThrowIfNull({m.ParamName});");
                    sb.AppendLine($"    {(m.Name == m.ParamName ? $"this.{m.Name}" : m.Name)} = {m.ParamName};");
                }
                sb.AppendLine("}");

                updatedType = InsertMemberInCanonicalOrder(updatedType, SyntaxFactory.ParseMemberDeclaration(sb.ToString())!);
                addedSignatures.Add($"{typeSymbol.Name}({paramList})");
                break;
            }

            case "equals_hashcode":
            {
                var valueMembers = GetStateMemberNames(typeSymbol);
                var eqComparisons = valueMembers.Count == 0
                    ? "true"
                    : string.Join(" && ", valueMembers.Select(m => $"EqualityComparer<{m.Type}>.Default.Equals({m.Name}, other.{m.Name})"));
                var hashArgs = valueMembers.Count == 0
                    ? "0"
                    : $"HashCode.Combine({string.Join(", ", valueMembers.Take(8).Select(m => m.Name))})";

                bool isVal = typeSymbol.IsValueType;
                string nullableType = isVal ? typeSymbol.Name : $"{typeSymbol.Name}?";

                updatedType = InsertMemberInCanonicalOrder(updatedType, SyntaxFactory.ParseMemberDeclaration($$"""
                    public bool Equals({{nullableType}} other)
                    {
                        {{(isVal ? "" : "if (other is null) return false;\n    if (ReferenceEquals(this, other)) return true;")}}
                        return {{eqComparisons}};
                    }
                    """)!);
                updatedType = InsertMemberInCanonicalOrder(updatedType, SyntaxFactory.ParseMemberDeclaration(
                    $"public override bool Equals(object? obj) => obj is {typeSymbol.Name} other && Equals(other);")!);
                updatedType = InsertMemberInCanonicalOrder(updatedType, SyntaxFactory.ParseMemberDeclaration(
                    $"public override int GetHashCode() => {hashArgs};")!);

                var eqIface = $"IEquatable<{typeSymbol.Name}>";
                if (!(updatedType.BaseList?.Types.Any(t => t.ToString().Contains(eqIface)) ?? false))
                    updatedType = updatedType.AddBaseListTypes(SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(eqIface)));

                addedSignatures.AddRange([$"Equals({nullableType})", "Equals(object?)", "GetHashCode()"]);
                break;
            }

            case "tostring":
            {
                var parts = GetStateMemberNames(typeSymbol).Select(m => $"{m.Name} = {{{m.Name}}}");
                var toStringNode = SyntaxFactory.ParseMemberDeclaration(
                    $"public override string ToString() => $\"{typeSymbol.Name} {{ {string.Join(", ", parts)} }}\";")!;
                var existing = FindMatchingMember(updatedType, toStringNode);
                updatedType = existing is not null
                    ? updatedType.ReplaceNode(existing, toStringNode)
                    : InsertMemberInCanonicalOrder(updatedType, toStringNode);
                addedSignatures.Add("ToString()");
                break;
            }

            case "overrides":
            {
                for (var bt = typeSymbol.BaseType; bt is not null && bt.SpecialType != SpecialType.System_Object; bt = bt.BaseType)
                {
                    foreach (var member in bt.GetMembers())
                    {
                        if ((!member.IsAbstract && !member.IsVirtual) || typeSymbol.GetMembers(member.Name).Any(m => m.IsOverride))
                            continue;

                        if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary } ms)
                        {
                            var access = ms.DeclaredAccessibility == Accessibility.Protected ? "protected" : "public";
                            var ret = ms.ReturnType.MinDisplay();
                            var pars = string.Join(", ", ms.Parameters.Select(p => $"{p.Type.MinDisplay()} {p.Name}"));
                            var args = string.Join(", ", ms.Parameters.Select(p => p.Name));
                            var body = ms.IsAbstract
                                ? "throw new NotImplementedException();"
                                : (ms.ReturnsVoid ? $"base.{ms.Name}({args});" : $"return base.{ms.Name}({args});");

                            updatedType = InsertMemberInCanonicalOrder(
                                updatedType,
                                SyntaxFactory.ParseMemberDeclaration($"{access} override {ret} {ms.Name}({pars}) {{ {body} }}")!);
                            addedSignatures.Add($"override {ret} {ms.Name}({pars})");
                        }
                    }
                }
                break;
            }

            case "extract_interface":
            case "interface_extraction":
            {
                var ifaceName = $"I{typeSymbol.Name}";
                var sb = new StringBuilder();
                sb.AppendLine($"public interface {ifaceName}\n{{");
                foreach (var m in typeSymbol.GetMembers())
                {
                    if (m.DeclaredAccessibility != Accessibility.Public || m.IsStatic || m.IsImplicitlyDeclared) continue;
                    if (m is IMethodSymbol { MethodKind: MethodKind.Ordinary } ms)
                    {
                        var ret = ms.ReturnType.MinDisplay();
                        var pars = string.Join(", ", ms.Parameters.Select(p => $"{p.Type.MinDisplay()} {p.Name}"));
                        sb.AppendLine($"    {ret} {ms.Name}({pars});");
                        addedSignatures.Add($"{ret} {ms.Name}({pars})");
                    }
                    else if (m is IPropertySymbol { IsIndexer: false } ps)
                    {
                        var pType = ps.Type.MinDisplay();
                        var getStr = ps.GetMethod is { DeclaredAccessibility: Accessibility.Public } ? "get; " : "";
                        var setStr = ps.SetMethod is { DeclaredAccessibility: Accessibility.Public } ? "set; " : "";
                        sb.AppendLine($"    {pType} {ps.Name} {{ {getStr}{setStr}}}");
                        addedSignatures.Add($"{pType} {ps.Name}");
                    }
                }
                sb.AppendLine("}");

                var ifaceDecl = SyntaxFactory.ParseMemberDeclaration(sb.ToString())!;
                if (!(updatedType.BaseList?.Types.Any(t => t.ToString().Trim() == ifaceName) ?? false))
                    updatedType = updatedType.AddBaseListTypes(SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(ifaceName)));

                var rootWithUpdatedType = doc.Tree.GetRoot().ReplaceNode(typeNode, updatedType);
                var updatedTypeInRoot = rootWithUpdatedType.DescendantNodes().OfType<TypeDeclarationSyntax>()
                    .First(t => t.Identifier.ValueText == updatedType.Identifier.ValueText);
                updatedRoot = (CompilationUnitSyntax)rootWithUpdatedType.InsertNodesBefore(updatedTypeInRoot, new SyntaxNode[] { ifaceDecl });
                break;
            }

            default:
                throw new ArgumentException(
                    $"Unrecognized generatorKind '{generatorKind}'. Supported: constructor, equals_hashcode, tostring, overrides, extract_interface.");
        }

        var finalRoot = updatedRoot ?? doc.Tree.GetRoot().ReplaceNode(typeNode, updatedType);
        var outcome = WorkspaceEngine.FinalizeMutation(ctx, doc, finalRoot, writeToDisk);

        return RoslynHelpers.Ser(new
        {
            targetType = typeSymbol.Name,
            generatorKind,
            generatedMembers = addedSignatures,
            file = outcome.File,
            savedToDisk = outcome.SavedToDisk,
            errorsAfter = outcome.ErrorsAfter,
            warningsAfter = outcome.WarningsAfter,
            diagnostics = outcome.Diagnostics,
            diff = outcome.Diff,
            text = outcome.FormattedSource
        });
    }

    [McpServerTool, Description(
        "Organize and fix C# 'using' directives using Roslyn semantic analysis: " +
        "automatically resolves and adds missing 'using' namespaces for unresolved types (from workspace and .NET BCL), " +
        "removes unused 'using' directives, and sorts System.* namespaces first.")]
    public static string OrganizeUsings(
        string sourceOrPath,
        string? targetFile = null,
        bool addMissingUsings = true,
        bool removeUnusedUsings = true,
        bool sortUsings = true,
        bool writeToDisk = false)
    {
        using var ctx = WorkspaceEngine.Load(sourceOrPath);
        var doc = string.IsNullOrWhiteSpace(targetFile)
            ? ctx.PrimaryDocument
            : ctx.FindDocument(targetFile) ?? throw new ArgumentException($"Document '{targetFile}' not found.");

        var root = (CompilationUnitSyntax)doc.Tree.GetRoot();
        var model = doc.Model;

        var existingUsings = root.Usings
            .Select(u => u.Name?.ToString() ?? "")
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.Ordinal);

        var addedNamespaces = new List<string>();
        var removedNamespaces = new List<string>();

        if (addMissingUsings)
        {
            var unresolvedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var diag in model.GetDiagnostics())
            {
                if (diag.Id is "CS0246" or "CS0103" && diag.Location.IsInSource)
                {
                    var simple = root.FindNode(diag.Location.SourceSpan)
                        .DescendantNodesAndSelf().OfType<SimpleNameSyntax>().FirstOrDefault();
                    if (simple is not null)
                        unresolvedNames.Add(simple.Identifier.ValueText);
                }
            }

            if (unresolvedNames.Count > 0)
            {
                var typeNamespaceIndex = BuildTypeToNamespaceLookup(ctx.Compilation.GlobalNamespace);
                foreach (var typeName in unresolvedNames)
                {
                    if (typeNamespaceIndex.TryGetValue(typeName, out var candidateNamespaces))
                    {
                        var bestNs = candidateNamespaces
                            .OrderBy(ns => ns.StartsWith("System", StringComparison.Ordinal) ? 0 : 1)
                            .ThenBy(ns => ns.Length)
                            .First();
                        if (existingUsings.Add(bestNs))
                            addedNamespaces.Add(bestNs);
                    }
                }
            }
        }

        if (removeUnusedUsings)
        {
            var usedNamespaces = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in root.DescendantNodes())
            {
                if (node is UsingDirectiveSyntax) continue;
                if (model.ResolveSymbol(node)?.ContainingNamespace is { IsGlobalNamespace: false } ns)
                    usedNamespaces.Add(ns.ToDisplayString());
                if (model.GetTypeInfo(node).Type?.ContainingNamespace is { IsGlobalNamespace: false } tNs)
                    usedNamespaces.Add(tNs.ToDisplayString());
            }

            foreach (var u in root.Usings)
            {
                if (u.Alias is not null || !u.StaticKeyword.IsKind(SyntaxKind.None) || !u.GlobalKeyword.IsKind(SyntaxKind.None))
                    continue;
                var nsName = u.Name?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(nsName) && !usedNamespaces.Contains(nsName) && !addedNamespaces.Contains(nsName))
                {
                    existingUsings.Remove(nsName);
                    removedNamespaces.Add(nsName);
                }
            }
        }

        var specialUsings = root.Usings
            .Where(u => u.Alias is not null || !u.StaticKeyword.IsKind(SyntaxKind.None) || !u.GlobalKeyword.IsKind(SyntaxKind.None))
            .ToList();

        IEnumerable<string> finalNamespaces = sortUsings
            ? existingUsings.OrderBy(ns => ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) ? 0 : 1).ThenBy(ns => ns, StringComparer.Ordinal)
            : existingUsings;

        var newUsingSyntaxList = finalNamespaces
            .Select(ns => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(ns)))
            .Concat(specialUsings)
            .ToArray();

        var outcome = WorkspaceEngine.FinalizeMutation(ctx, doc, root.WithUsings(SyntaxFactory.List(newUsingSyntaxList)), writeToDisk);
        return RoslynHelpers.Ser(new
        {
            file = outcome.File,
            addedNamespaces,
            removedNamespaces,
            finalUsings = finalNamespaces.ToArray(),
            savedToDisk = outcome.SavedToDisk,
            errorsAfter = outcome.ErrorsAfter,
            warningsAfter = outcome.WarningsAfter,
            diagnostics = outcome.Diagnostics,
            diff = outcome.Diff,
            text = outcome.FormattedSource
        });
    }

    [McpServerTool, Description(
        "Apply semantic/syntactic C# AST transformations via CSharpSyntaxRewriter: " +
        "transform: 'file_scoped_namespace' | 'expression_bodied' | 'var_to_explicit' | 'explicit_to_var' | " +
        "'add_readonly_fields' | 'add_null_checks'.")]
    public static string ApplySyntaxTransform(
        string sourceOrPath,
        string transform,
        string? targetFile = null,
        bool writeToDisk = false)
    {
        using var ctx = WorkspaceEngine.Load(sourceOrPath);
        var doc = string.IsNullOrWhiteSpace(targetFile)
            ? ctx.PrimaryDocument
            : ctx.FindDocument(targetFile) ?? throw new ArgumentException($"Document '{targetFile}' not found.");

        var root = doc.Tree.GetRoot();
        var model = doc.Model;
        int changeCount = 0;

        SyntaxNode transformedRoot = transform.Trim().ToLowerInvariant() switch
        {
            "file_scoped_namespace" => ConvertToFileScopedNamespace(root, ref changeCount),
            "expression_bodied" => ConvertToExpressionBodiedMembers(root, ref changeCount),
            "var_to_explicit" => ConvertVarToExplicitTypes(root, model, ref changeCount),
            "explicit_to_var" => ConvertExplicitTypesToVar(root, model, ref changeCount),
            "add_readonly_fields" => AddReadonlyToImmutableFields(root, model, ref changeCount),
            "add_null_checks" => AddParameterNullGuards(root, model, ref changeCount),
            _ => throw new ArgumentException(
                $"Unrecognized transform '{transform}'. Supported: file_scoped_namespace, expression_bodied, var_to_explicit, explicit_to_var, add_readonly_fields, add_null_checks.")
        };

        var outcome = WorkspaceEngine.FinalizeMutation(ctx, doc, transformedRoot, writeToDisk);
        return RoslynHelpers.Ser(new
        {
            transform,
            transformationsApplied = changeCount,
            file = outcome.File,
            savedToDisk = outcome.SavedToDisk,
            errorsAfter = outcome.ErrorsAfter,
            warningsAfter = outcome.WarningsAfter,
            diagnostics = outcome.Diagnostics,
            diff = outcome.Diff,
            text = outcome.FormattedSource
        });
    }

    private static (DocumentEntry Doc, TypeDeclarationSyntax TypeNode) FindTypeDeclaration(WorkspaceContext ctx, string typeName)
    {
        var cleanName = typeName.Split('.').Last().Trim();
        foreach (var doc in ctx.UserDocuments)
        {
            foreach (var td in doc.Tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var sym = doc.Model.GetDeclaredSymbol(td);
                if (string.Equals(td.Identifier.ValueText, cleanName, StringComparison.Ordinal) ||
                    string.Equals(sym?.MinDisplay(), typeName, StringComparison.Ordinal) ||
                    string.Equals(td.Identifier.ValueText, cleanName, StringComparison.OrdinalIgnoreCase))
                {
                    return (doc, td);
                }
            }
        }
        throw new ArgumentException($"Type declaration '{typeName}' was not found in the workspace.");
    }

    private static MemberDeclarationSyntax? FindMatchingMember(TypeDeclarationSyntax typeNode, MemberDeclarationSyntax candidate)
    {
        var candidateKey = GetMemberKey(candidate);
        return typeNode.Members.FirstOrDefault(m => string.Equals(GetMemberKey(m), candidateKey, StringComparison.Ordinal));
    }

    private static List<MemberDeclarationSyntax> FindMembersByNameOrSignature(TypeDeclarationSyntax typeNode, string query)
    {
        var clean = query.Trim();
        bool hasParens = clean.Contains('(');
        var matches = new List<MemberDeclarationSyntax>();

        foreach (var m in typeNode.Members)
        {
            var key = GetMemberKey(m);
            var simpleName = key.Contains('(') ? key[..key.IndexOf('(')] : key;
            if (hasParens)
            {
                if (string.Equals(key.Replace(" ", ""), clean.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                    matches.Add(m);
            }
            else if (string.Equals(simpleName, clean, StringComparison.Ordinal) ||
                     (m is FieldDeclarationSyntax fd && fd.Declaration.Variables.Any(v => v.Identifier.ValueText == clean)))
            {
                matches.Add(m);
            }
        }
        return matches;
    }

    private static string GetMemberKey(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => $"{m.Identifier.ValueText}({string.Join(", ", m.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? ""))})",
        ConstructorDeclarationSyntax c => $"{c.Identifier.ValueText}({string.Join(", ", c.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? ""))})",
        PropertyDeclarationSyntax p => p.Identifier.ValueText,
        IndexerDeclarationSyntax idx => $"this[{string.Join(", ", idx.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? ""))}]",
        FieldDeclarationSyntax f => string.Join(",", f.Declaration.Variables.Select(v => v.Identifier.ValueText)),
        EventDeclarationSyntax e => e.Identifier.ValueText,
        EventFieldDeclarationSyntax ef => string.Join(",", ef.Declaration.Variables.Select(v => v.Identifier.ValueText)),
        BaseTypeDeclarationSyntax bt => bt.Identifier.ValueText,
        _ => member.ToString()
    };

    private static int GetMemberOrderRank(MemberDeclarationSyntax member) => member switch
    {
        FieldDeclarationSyntax => 1,
        ConstructorDeclarationSyntax => 2,
        PropertyDeclarationSyntax or IndexerDeclarationSyntax => 3,
        EventDeclarationSyntax or EventFieldDeclarationSyntax => 4,
        MethodDeclarationSyntax => 5,
        BaseTypeDeclarationSyntax => 6,
        _ => 7
    };

    private static TypeDeclarationSyntax InsertMemberInCanonicalOrder(TypeDeclarationSyntax typeNode, MemberDeclarationSyntax newMember)
    {
        int targetRank = GetMemberOrderRank(newMember);
        var list = typeNode.Members.ToList();
        int insertIdx = list.Count;

        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (GetMemberOrderRank(list[i]) <= targetRank)
            {
                insertIdx = i + 1;
                break;
            }
            if (i == 0) insertIdx = 0;
        }

        list.Insert(insertIdx, newMember);
        return typeNode.WithMembers(SyntaxFactory.List(list));
    }

    private static bool HasDocumentationComment(SyntaxNode node) =>
        node.GetLeadingTrivia().Any(t =>
            t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
            t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));

    private static bool StatementsAccessInstanceState(SemanticModel model, List<StatementSyntax> statements)
    {
        foreach (var node in statements.SelectMany(s => s.DescendantNodesAndSelf()))
        {
            if (node is ThisExpressionSyntax or BaseExpressionSyntax)
                return true;

            var sym = model.GetSymbolInfo(node).Symbol;
            if (sym is IFieldSymbol or IPropertySymbol or IMethodSymbol or IEventSymbol &&
                !sym.IsStatic && sym.ContainingType is not null &&
                node is SimpleNameSyntax && node.Parent is not MemberAccessExpressionSyntax)
            {
                return true;
            }
        }
        return false;
    }

    private static INamedTypeSymbol? ResolveInterfaceSymbol(
        CSharpCompilation compilation,
        INamedTypeSymbol targetClass,
        string interfaceName)
    {
        var trimmed = interfaceName.Trim();
        string baseName = trimmed;
        string[]? typeArgNames = null;
        var ltIdx = trimmed.IndexOf('<');
        if (ltIdx > 0 && trimmed.EndsWith('>'))
        {
            baseName = trimmed[..ltIdx].Trim();
            typeArgNames = trimmed[(ltIdx + 1)..^1]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        int arity = typeArgNames?.Length ?? 0;
        string metadataSuffix = arity > 0 ? $"`{arity}" : "";

        string[] candidateNamespaces =
        [
            "",
            targetClass.ContainingNamespace?.ToDisplayString() ?? "",
            "System",
            "System.Collections",
            "System.Collections.Generic",
            "System.ComponentModel",
            "System.IO",
            "System.Threading.Tasks"
        ];

        INamedTypeSymbol? rawInterface = null;
        foreach (var ns in candidateNamespaces.Distinct())
        {
            var fullMeta = string.IsNullOrEmpty(ns) ? $"{baseName}{metadataSuffix}" : $"{ns}.{baseName}{metadataSuffix}";
            rawInterface = compilation.GetTypeByMetadataName(fullMeta);
            if (rawInterface is { TypeKind: TypeKind.Interface })
                break;
        }

        rawInterface ??= FindInterfaceInNamespace(compilation.GlobalNamespace, baseName, arity);
        if (rawInterface is null) return null;

        if (arity > 0 && rawInterface.IsGenericType && typeArgNames is not null)
        {
            var resolvedTypeArgs = new ITypeSymbol[arity];
            for (int i = 0; i < arity; i++)
            {
                var argName = typeArgNames[i];
                resolvedTypeArgs[i] = string.Equals(argName, targetClass.Name, StringComparison.Ordinal)
                    ? targetClass
                    : ResolveSimpleTypeSymbol(compilation, argName) ?? compilation.GetSpecialType(SpecialType.System_Object);
            }
            return rawInterface.Construct(resolvedTypeArgs);
        }

        return rawInterface;
    }

    private static INamedTypeSymbol? FindInterfaceInNamespace(INamespaceSymbol ns, string name, int arity)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            if (type.TypeKind == TypeKind.Interface &&
                string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase) &&
                type.Arity == arity)
            {
                return type;
            }
        }
        foreach (var subNs in ns.GetNamespaceMembers())
        {
            var found = FindInterfaceInNamespace(subNs, name, arity);
            if (found is not null) return found;
        }
        return null;
    }

    private static ITypeSymbol? ResolveSimpleTypeSymbol(CSharpCompilation comp, string name) => name switch
    {
        "int" or "Int32" => comp.GetSpecialType(SpecialType.System_Int32),
        "string" or "String" => comp.GetSpecialType(SpecialType.System_String),
        "bool" or "Boolean" => comp.GetSpecialType(SpecialType.System_Boolean),
        "double" or "Double" => comp.GetSpecialType(SpecialType.System_Double),
        "decimal" or "Decimal" => comp.GetSpecialType(SpecialType.System_Decimal),
        "long" or "Int64" => comp.GetSpecialType(SpecialType.System_Int64),
        "object" or "Object" => comp.GetSpecialType(SpecialType.System_Object),
        _ => comp.GetTypeByMetadataName(name) ?? comp.GetTypeByMetadataName($"System.{name}")
    };

    private static MemberDeclarationSyntax? GenerateInterfaceMemberStub(
        INamedTypeSymbol iface,
        ISymbol member,
        INamedTypeSymbol implementingType,
        bool explicitImpl)
    {
        var ifacePrefix = explicitImpl ? $"{iface.MinDisplay()}." : "";
        var accessPrefix = explicitImpl ? "" : "public ";

        if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary } method)
        {
            var retType = method.ReturnType.MinDisplay();
            var pars = string.Join(", ", method.Parameters.Select(p => $"{p.Type.MinDisplay()} {p.Name}"));

            string body = (iface.Name, method.Name, method.Parameters.Length) switch
            {
                ("IDisposable", "Dispose", 0) => "GC.SuppressFinalize(this);",
                ("IAsyncDisposable", "DisposeAsync", 0) => "GC.SuppressFinalize(this);\nreturn ValueTask.CompletedTask;",
                ("IEquatable", "Equals", 1) => implementingType.IsValueType
                    ? $"return EqualityComparer<object>.Default.Equals(this, {method.Parameters[0].Name});"
                    : $"return {method.Parameters[0].Name} is not null && ReferenceEquals(this, {method.Parameters[0].Name});",
                ("IComparable", "CompareTo", 1) => $"return {method.Parameters[0].Name} is null ? 1 : 0;",
                _ => "throw new NotImplementedException();"
            };

            return SyntaxFactory.ParseMemberDeclaration($"{accessPrefix}{retType} {ifacePrefix}{method.Name}({pars})\n{{\n    {body}\n}}");
        }

        if (member is IPropertySymbol { IsIndexer: false } prop)
        {
            var propType = prop.Type.MinDisplay();
            if (explicitImpl)
            {
                var getAccessor = prop.GetMethod is not null ? "get => throw new NotImplementedException(); " : "";
                var setAccessor = prop.SetMethod is not null ? "set => throw new NotImplementedException(); " : "";
                return SyntaxFactory.ParseMemberDeclaration($"{propType} {ifacePrefix}{prop.Name} {{ {getAccessor}{setAccessor}}}");
            }
            var getStr = prop.GetMethod is not null ? "get; " : "";
            var setStr = prop.SetMethod is not null ? "set; " : "";
            return SyntaxFactory.ParseMemberDeclaration($"public {propType} {prop.Name} {{ {getStr}{setStr}}}");
        }

        if (member is IEventSymbol evt)
        {
            var evtType = evt.Type.MinDisplay();
            return explicitImpl
                ? SyntaxFactory.ParseMemberDeclaration($"{evtType} {ifacePrefix}{evt.Name} {{ add {{ }} remove {{ }} }}")
                : SyntaxFactory.ParseMemberDeclaration($"public event {evtType}? {evt.Name};");
        }

        return null;
    }

    private static List<(string Name, string Type)> GetStateMemberNames(INamedTypeSymbol typeSymbol)
    {
        var list = new List<(string Name, string Type)>();
        foreach (var m in typeSymbol.GetMembers())
        {
            if (m.IsStatic || m.IsImplicitlyDeclared) continue;
            if (m is IPropertySymbol { IsIndexer: false, GetMethod: not null } prop)
                list.Add((prop.Name, prop.Type.MinDisplay()));
            else if (m is IFieldSymbol { IsConst: false } field && m.DeclaredAccessibility == Accessibility.Public)
                list.Add((field.Name, field.Type.MinDisplay()));
        }
        return list;
    }

    private static Dictionary<string, List<string>> BuildTypeToNamespaceLookup(INamespaceSymbol rootNs)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var stack = new Stack<INamespaceSymbol>([rootNs]);

        while (stack.Count > 0)
        {
            var ns = stack.Pop();
            var nsDisplay = ns.IsGlobalNamespace ? "" : ns.ToDisplayString();

            if (!string.IsNullOrEmpty(nsDisplay))
            {
                foreach (var type in ns.GetTypeMembers())
                {
                    if (type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
                        continue;

                    if (!map.TryGetValue(type.Name, out var list))
                    {
                        list = new List<string>();
                        map[type.Name] = list;
                    }
                    if (!list.Contains(nsDisplay))
                        list.Add(nsDisplay);
                }
            }

            foreach (var child in ns.GetNamespaceMembers())
                stack.Push(child);
        }

        return map;
    }

    private static SyntaxNode ConvertToFileScopedNamespace(SyntaxNode root, ref int count)
    {
        if (root is not CompilationUnitSyntax unit) return root;
        var blockNamespaces = unit.Members.OfType<NamespaceDeclarationSyntax>().ToList();
        if (blockNamespaces.Count != 1) return root;

        var oldNs = blockNamespaces[0];
        var fileScopedNs = SyntaxFactory.FileScopedNamespaceDeclaration(
            oldNs.AttributeLists, oldNs.Modifiers, oldNs.NamespaceKeyword, oldNs.Name,
            SyntaxFactory.Token(SyntaxKind.SemicolonToken), oldNs.Externs, oldNs.Usings, oldNs.Members);

        count++;
        return unit.ReplaceNode(oldNs, fileScopedNs);
    }

    private static SyntaxNode ConvertToExpressionBodiedMembers(SyntaxNode root, ref int count)
    {
        var methodsToConvert = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.Body is { Statements.Count: 1 } && m.Body.Statements[0] is ReturnStatementSyntax { Expression: not null })
            .ToList();

        var current = root;
        foreach (var m in methodsToConvert)
        {
            var currentMethod = current.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(x => x.Identifier.ValueText == m.Identifier.ValueText && x.SpanStart == m.SpanStart);
            if (currentMethod?.Body?.Statements[0] is not ReturnStatementSyntax { Expression: { } expr })
                continue;

            var updated = currentMethod
                .WithBody(null)
                .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(expr))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
            current = current.ReplaceNode(currentMethod, updated);
            count++;
        }
        return current;
    }

    private static SyntaxNode ConvertVarToExplicitTypes(SyntaxNode root, SemanticModel model, ref int count)
    {
        var replacements = new Dictionary<TypeSyntax, TypeSyntax>();
        foreach (var decl in root.DescendantNodes().OfType<VariableDeclarationSyntax>())
        {
            if (!decl.Type.IsVar || decl.Variables.Count != 1) continue;
            var typeInfo = model.GetTypeInfo(decl.Type).Type;
            if (typeInfo is null or IErrorTypeSymbol || typeInfo.IsAnonymousType) continue;
            replacements[decl.Type] = SyntaxFactory.ParseTypeName(typeInfo.MinDisplay()).WithTriviaFrom(decl.Type);
            count++;
        }

        return replacements.Count == 0 ? root : root.ReplaceNodes(replacements.Keys, (orig, _) => replacements[orig]);
    }

    private static SyntaxNode ConvertExplicitTypesToVar(SyntaxNode root, SemanticModel model, ref int count)
    {
        var replacements = new Dictionary<TypeSyntax, TypeSyntax>();
        foreach (var localStmt in root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            if (localStmt.IsConst) continue;
            var decl = localStmt.Declaration;
            if (decl.Type.IsVar || decl.Variables.Count != 1) continue;
            var init = decl.Variables[0].Initializer?.Value;
            if (init is null or LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression } or ImplicitObjectCreationExpressionSyntax)
                continue;

            var lhsType = model.GetTypeInfo(decl.Type).Type;
            var rhsType = model.GetTypeInfo(init).Type;
            if (lhsType is not null && SymbolEqualityComparer.Default.Equals(lhsType, rhsType))
            {
                replacements[decl.Type] = SyntaxFactory.IdentifierName("var").WithTriviaFrom(decl.Type);
                count++;
            }
        }

        return replacements.Count == 0 ? root : root.ReplaceNodes(replacements.Keys, (orig, _) => replacements[orig]);
    }

    private static SyntaxNode AddReadonlyToImmutableFields(SyntaxNode root, SemanticModel model, ref int count)
    {
        var writtenOutsideCtor = new HashSet<IFieldSymbol>(SymbolEqualityComparer.Default);
        foreach (var node in root.DescendantNodes())
        {
            SyntaxNode? targetExpr = node switch
            {
                AssignmentExpressionSyntax assign => assign.Left,
                PrefixUnaryExpressionSyntax pre => pre.Operand,
                PostfixUnaryExpressionSyntax post => post.Operand,
                ArgumentSyntax arg when !arg.RefKindKeyword.IsKind(SyntaxKind.None) => arg.Expression,
                _ => null
            };

            if (targetExpr is not null &&
                model.GetSymbolInfo(targetExpr).Symbol is IFieldSymbol fs &&
                node.Ancestors().OfType<ConstructorDeclarationSyntax>().FirstOrDefault() is null)
            {
                writtenOutsideCtor.Add(fs);
            }
        }

        var replacements = new Dictionary<FieldDeclarationSyntax, FieldDeclarationSyntax>();
        foreach (var fieldDecl in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            if (fieldDecl.Modifiers.Any(SyntaxKind.ReadOnlyKeyword) ||
                fieldDecl.Modifiers.Any(SyntaxKind.ConstKeyword) ||
                fieldDecl.Modifiers.Any(SyntaxKind.VolatileKeyword))
            {
                continue;
            }

            var symbols = fieldDecl.Declaration.Variables
                .Select(v => model.GetDeclaredSymbol(v) as IFieldSymbol)
                .Where(s => s is not null)
                .Cast<IFieldSymbol>()
                .ToList();

            if (symbols.Count > 0 && symbols.All(s => s.DeclaredAccessibility == Accessibility.Private && !writtenOutsideCtor.Contains(s)))
            {
                replacements[fieldDecl] = fieldDecl.AddModifiers(SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));
                count++;
            }
        }

        return replacements.Count == 0 ? root : root.ReplaceNodes(replacements.Keys, (orig, _) => replacements[orig]);
    }

    private static SyntaxNode AddParameterNullGuards(SyntaxNode root, SemanticModel model, ref int count)
    {
        var replacements = new Dictionary<BaseMethodDeclarationSyntax, BaseMethodDeclarationSyntax>();
        foreach (var method in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
        {
            if (method.Body is null || !method.Modifiers.Any(SyntaxKind.PublicKeyword))
                continue;

            var existingText = method.Body.ToString();
            var guards = new List<StatementSyntax>();

            foreach (var p in method.ParameterList.Parameters)
            {
                if (model.GetDeclaredSymbol(p) is not IParameterSymbol pSym) continue;
                if (!pSym.Type.IsReferenceType || pSym.NullableAnnotation == NullableAnnotation.Annotated || pSym.HasExplicitDefaultValue)
                    continue;
                if (!existingText.Contains($"ThrowIfNull({pSym.Name})", StringComparison.Ordinal))
                    guards.Add(SyntaxFactory.ParseStatement($"ArgumentNullException.ThrowIfNull({pSym.Name});"));
            }

            if (guards.Count > 0)
            {
                replacements[method] = method.WithBody(method.Body.WithStatements(SyntaxFactory.List(guards.Concat(method.Body.Statements))));
                count += guards.Count;
            }
        }

        return replacements.Count == 0 ? root : root.ReplaceNodes(replacements.Keys, (orig, _) => replacements[orig]);
    }
}
