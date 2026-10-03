using System.Collections.Immutable;
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;

namespace RoslynMcp;

[McpServerToolType]
internal static class SemanticTools
{
    [McpServerTool, Description(
        "Get comprehensive Roslyn semantic information for a symbol, declaration, or expression. " +
        "sourceOrPath: C# source text, JSON {file:source} map, or disk path (.cs, .csproj, .sln, .slnx, directory). " +
        "symbolOrLocation: symbol name (e.g. 'MyClass.MyMethod', 'MyType'), 'line:col' (1-based), 'File.cs:line:col', or '@offset'.")]
    public static string GetSymbolInfo(string sourceOrPath, string symbolOrLocation)
    {
        using var ctx = WorkspaceEngine.Load(sourceOrPath);
        var target = WorkspaceEngine.ResolveTarget(ctx, symbolOrLocation);
        var model = target.Document.Model;
        var node = target.Node;
        var symbol = target.Symbol;

        var typeInfo = model.GetTypeInfo(node);
        var operation = model.GetOperation(node);

        object? typeDetails = null;
        object? methodDetails = null;
        object? memberDetails = null;

        if (symbol is INamedTypeSymbol namedType)
        {
            var baseChain = new List<string>();
            for (var bt = namedType.BaseType; bt is not null; bt = bt.BaseType)
                baseChain.Add(bt.MinDisplay());

            var members = namedType.GetMembers()
                .Where(m => !m.IsImplicitlyDeclared)
                .Select(m => new
                {
                    name = m.Name,
                    kind = m.Kind.ToString(),
                    accessibility = m.DeclaredAccessibility.ToString(),
                    signature = m.MinDisplay(),
                    modifiers = m.GetModifiers()
                })
                .ToArray();

            typeDetails = new
            {
                typeKind = namedType.TypeKind.ToString(),
                isRecord = namedType.IsRecord,
                isGeneric = namedType.IsGenericType,
                typeParameters = namedType.TypeParameters.Select(tp => new
                {
                    name = tp.Name,
                    variance = tp.Variance.ToString(),
                    constraints = GetTypeParameterConstraints(tp)
                }).ToArray(),
                baseTypes = baseChain,
                directInterfaces = namedType.Interfaces.Select(i => i.MinDisplay()).ToArray(),
                allInterfaces = namedType.AllInterfaces.Select(i => i.MinDisplay()).ToArray(),
                memberCount = members.Length,
                members
            };
        }
        else if (symbol is IMethodSymbol methodSym)
        {
            var implementedInterfaces = new List<string>();
            if (methodSym.ContainingType is not null)
            {
                foreach (var iface in methodSym.ContainingType.AllInterfaces)
                {
                    foreach (var ifaceMember in iface.GetMembers().OfType<IMethodSymbol>())
                    {
                        var impl = methodSym.ContainingType.FindImplementationForInterfaceMember(ifaceMember);
                        if (SymbolEqualityComparer.Default.Equals(impl, methodSym))
                            implementedInterfaces.Add($"{iface.Name}.{ifaceMember.Name}");
                    }
                }
            }

            methodDetails = new
            {
                methodKind = methodSym.MethodKind.ToString(),
                returnType = methodSym.ReturnType.MinDisplay(),
                isAsync = methodSym.IsAsync,
                isExtensionMethod = methodSym.IsExtensionMethod,
                isGeneric = methodSym.IsGenericMethod,
                typeParameters = methodSym.TypeParameters.Select(tp => tp.Name).ToArray(),
                parameters = methodSym.Parameters.Select(p => new
                {
                    name = p.Name,
                    type = p.Type.MinDisplay(),
                    refKind = p.RefKind.ToString(),
                    isOptional = p.IsOptional,
                    isParams = p.IsParams,
                    defaultValue = p.HasExplicitDefaultValue ? p.ExplicitDefaultValue?.ToString() ?? "null" : null
                }).ToArray(),
                overriddenMethod = methodSym.OverriddenMethod?.MinDisplay(),
                implementedInterfaceMembers = implementedInterfaces
            };
        }
        else if (symbol is IPropertySymbol propSym)
        {
            memberDetails = new
            {
                memberType = propSym.Type.MinDisplay(),
                isIndexer = propSym.IsIndexer,
                isReadOnly = propSym.IsReadOnly,
                isRequired = propSym.IsRequired,
                hasGetter = propSym.GetMethod is not null,
                hasSetter = propSym.SetMethod is not null,
                isInitOnly = propSym.SetMethod?.IsInitOnly ?? false
            };
        }
        else if (symbol is IFieldSymbol fieldSym)
        {
            memberDetails = new
            {
                memberType = fieldSym.Type.MinDisplay(),
                isReadOnly = fieldSym.IsReadOnly,
                isConst = fieldSym.IsConst,
                isRequired = fieldSym.IsRequired,
                constantValue = fieldSym.HasConstantValue ? fieldSym.ConstantValue?.ToString() ?? "null" : null
            };
        }
        else if (symbol is ILocalSymbol localSym)
        {
            memberDetails = new
            {
                variableType = localSym.Type.MinDisplay(),
                isConst = localSym.IsConst,
                isRef = localSym.IsRef,
                constantValue = localSym.HasConstantValue ? localSym.ConstantValue?.ToString() ?? "null" : null
            };
        }
        else if (symbol is IParameterSymbol paramSym)
        {
            memberDetails = new
            {
                parameterType = paramSym.Type.MinDisplay(),
                refKind = paramSym.RefKind.ToString(),
                isOptional = paramSym.IsOptional,
                isParams = paramSym.IsParams,
                defaultValue = paramSym.HasExplicitDefaultValue ? paramSym.ExplicitDefaultValue?.ToString() ?? "null" : null
            };
        }

        var declarations = symbol?.DeclaringSyntaxReferences.Select(r =>
        {
            var syn = r.GetSyntax();
            return new
            {
                location = WorkspaceEngine.FormatLocation(syn.GetLocation()),
                syntaxKind = syn.Kind().ToString(),
                snippet = syn.PreviewText(400)
            };
        }).ToArray() ?? Array.Empty<object>();

        var attributes = symbol?.GetAttributes().Select(a => new
        {
            name = a.AttributeClass?.Name ?? a.ToString(),
            fullString = a.ToString()
        }).ToArray() ?? Array.Empty<object>();

        return RoslynHelpers.Ser(new
        {
            resolvedAt = new
            {
                file = target.Document.Name,
                line = target.Line,
                column = target.Column,
                position = target.Position,
                syntaxKind = node.Kind().ToString(),
                nodeText = node.PreviewText(180)
            },
            symbol = symbol is null ? null : new
            {
                name = symbol.Name,
                fullName = symbol.FullDisplay(),
                signature = symbol.MinDisplay(),
                kind = symbol.Kind.ToString(),
                accessibility = symbol.DeclaredAccessibility.ToString(),
                modifiers = symbol.GetModifiers(),
                containingNamespace = symbol.ContainingNamespace?.IsGlobalNamespace == false ? symbol.ContainingNamespace.ToDisplayString() : "(global)",
                containingType = symbol.ContainingType?.MinDisplay(),
                assembly = symbol.ContainingAssembly?.Name,
                summary = symbol.GetXmlSummary(),
                attributes,
                declarations
            },
            typeDetails,
            methodDetails,
            memberDetails,
            expressionInfo = new
            {
                inferredType = typeInfo.Type?.MinDisplay(),
                convertedType = typeInfo.ConvertedType?.MinDisplay(),
                nullability = typeInfo.Nullability.Annotation.ToString(),
                operationKind = operation?.Kind.ToString()
            }
        });
    }

    [McpServerTool, Description(
        "Search and filter declared symbols across a file, project, directory, or solution using Roslyn's SemanticModel. " +
        "query: glob pattern ('*Service*', 'Parse*'), substring, or regex. " +
        "kind: all|class|interface|record|struct|enum|method|property|field|constructor|event. " +
        "accessibility: all|public|internal|protected|private. " +
        "attribute: filter by attribute name (e.g. 'McpServerTool'). " +
        "modifier: filter by modifier (e.g. 'async', 'static', 'abstract', 'virtual', 'override', 'extension', 'readonly'). " +
        "returnType: filter methods/properties/fields by type name.")]
    public static string FindSymbols(
        string sourceOrPath,
        string query = "*",
        string kind = "all",
        string accessibility = "all",
        string attribute = "",
        string modifier = "",
        string returnType = "")
    {
        using var ctx = WorkspaceEngine.Load(sourceOrPath);
        var regex = RoslynHelpers.CompileQueryRegex(query);
        var matches = new List<object>();

        foreach (var doc in ctx.UserDocuments)
        {
            foreach (var node in doc.Tree.GetRoot().DescendantNodes())
            {
                if (node is not (BaseTypeDeclarationSyntax or MethodDeclarationSyntax or PropertyDeclarationSyntax
                    or EventDeclarationSyntax or ConstructorDeclarationSyntax or VariableDeclaratorSyntax
                    or DelegateDeclarationSyntax))
                {
                    continue;
                }

                if (node is VariableDeclaratorSyntax &&
                    node.Ancestors().Any(a => a is BaseMethodDeclarationSyntax) &&
                    !string.Equals(kind, "local", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var sym = doc.Model.GetDeclaredSymbol(node);
                if (sym is null) continue;

                var friendlyKind = sym.GetFriendlyKind();
                if (!string.IsNullOrWhiteSpace(kind) && !string.Equals(kind, "all", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(friendlyKind, kind.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(sym.Kind.ToString(), kind.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(accessibility) && !string.Equals(accessibility, "all", StringComparison.OrdinalIgnoreCase) &&
                    !sym.DeclaredAccessibility.ToString().Contains(accessibility.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!regex.IsMatch(sym.Name) && !regex.IsMatch(sym.MinDisplay()))
                    continue;

                var mods = sym.GetModifiers();
                if (!string.IsNullOrWhiteSpace(modifier) &&
                    !mods.Any(m => string.Equals(m, modifier.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var attrs = sym.GetAttributes().Select(a => a.AttributeClass?.Name ?? a.ToString()).ToArray();
                if (!string.IsNullOrWhiteSpace(attribute))
                {
                    var attrQuery = attribute.Trim().Trim('[', ']');
                    if (!attrs.Any(a => a.Contains(attrQuery, StringComparison.OrdinalIgnoreCase)))
                        continue;
                }

                var symType = sym.GetValueOrReturnTypeString();
                if (!string.IsNullOrWhiteSpace(returnType) &&
                    !symType.Contains(returnType.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var lr = node.LineRange();
                matches.Add(new
                {
                    name = sym.Name,
                    qualifiedName = sym.ContainingType is not null ? $"{sym.ContainingType.Name}.{sym.Name}" : sym.Name,
                    kind = friendlyKind,
                    signature = sym.MinDisplay(),
                    accessibility = sym.DeclaredAccessibility.ToString(),
                    modifiers = mods,
                    type = symType,
                    attributes = attrs,
                    file = doc.Name,
                    startLine = lr.StartLine,
                    endLine = lr.EndLine,
                    summary = sym.GetXmlSummary()
                });
            }
        }

        return RoslynHelpers.Ser(new { count = matches.Count, symbols = matches });
    }

    [McpServerTool, Description(
        "Find all semantic references to a symbol across a file, project, or solution using Roslyn SymbolFinder. " +
        "Classifies each reference as Read, Write, ReadWrite, Invocation, ObjectCreation, or TypeReference, " +
        "and reports the file, 1-based line/column, enclosing member, and source line.")]
    public static async Task<string> FindReferences(string sourceOrPath, string symbolOrLocation)
    {
        using var ctx = WorkspaceEngine.Load(sourceOrPath);
        var target = WorkspaceEngine.ResolveTarget(ctx, symbolOrLocation);
        var symbol = target.Symbol
            ?? throw new ArgumentException($"No semantic symbol found at '{symbolOrLocation}'.");

        var referencedSymbols = await SymbolFinder.FindReferencesAsync(symbol, ctx.Solution);
        var seenSpans = new HashSet<(string File, int Start)>();
        var items = new List<object>();

        foreach (var refSym in referencedSymbols)
        {
            foreach (var refLoc in refSym.Locations)
            {
                var loc = refLoc.Location;
                if (!loc.IsInSource || loc.SourceTree is null) continue;
                var docEntry = ctx.UserDocuments.FirstOrDefault(d => d.Tree == loc.SourceTree);
                if (docEntry is not null && seenSpans.Add((docEntry.Name, loc.SourceSpan.Start)))
                    items.Add(BuildReferenceRecord(docEntry, loc.SourceSpan));
            }
        }

        foreach (var doc in ctx.UserDocuments)
        {
            foreach (var idNode in doc.Tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                if (!string.Equals(idNode.Identifier.ValueText, symbol.Name, StringComparison.Ordinal))
                    continue;
                var candidate = doc.Model.ResolveSymbol(idNode);
                if (candidate is null) continue;

                if (SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, symbol.OriginalDefinition) ||
                    SymbolEqualityComparer.Default.Equals(candidate, symbol) ||
                    (candidate is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor &&
                     SymbolEqualityComparer.Default.Equals(ctor.ContainingType, symbol)))
                {
                    if (seenSpans.Add((doc.Name, idNode.SpanStart)))
                        items.Add(BuildReferenceRecord(doc, idNode.Span));
                }
            }
        }

        return RoslynHelpers.Ser(new
        {
            symbol = symbol.MinDisplay(),
            kind = symbol.Kind.ToString(),
            declarationLocations = symbol.Locations.Where(l => l.IsInSource).Select(WorkspaceEngine.FormatLocation).ToArray(),
            referenceCount = items.Count,
            references = items
        });
    }

    [McpServerTool, Description(
        "Build a bidirectional call graph (incoming callers and outgoing callees) for a method, constructor, or property. " +
        "Uses SymbolFinder.FindCallersAsync and SemanticModel operation walking across the workspace.")]
    public static async Task<string> GetCallGraph(string sourceOrPath, string methodSymbolOrLocation, int depth = 2)
    {
        using var ctx = WorkspaceEngine.Load(sourceOrPath);
        var target = WorkspaceEngine.ResolveTarget(ctx, methodSymbolOrLocation);
        var symbol = target.Symbol;

        if (symbol is not (IMethodSymbol or IPropertySymbol))
        {
            var enclosingMethod = target.Node.AncestorsAndSelf().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
            if (enclosingMethod is not null)
                symbol = target.Document.Model.GetDeclaredSymbol(enclosingMethod);
        }

        if (symbol is null)
            throw new ArgumentException($"Could not resolve a callable symbol at '{methodSymbolOrLocation}'.");

        int maxDepth = Math.Clamp(depth, 1, 5);
        var callers = await TraceCallersAsync(ctx, symbol, maxDepth, new HashSet<ISymbol>(SymbolEqualityComparer.Default));
        var (workspaceCallees, externalCallees, isRecursive) = TraceCallees(ctx, symbol, maxDepth, new HashSet<ISymbol>(SymbolEqualityComparer.Default));

        return RoslynHelpers.Ser(new
        {
            target = new
            {
                name = symbol.Name,
                signature = symbol.MinDisplay(),
                containingType = symbol.ContainingType?.MinDisplay(),
                isRecursive
            },
            incomingCallerCount = callers.Count,
            incomingCallers = callers,
            outgoingWorkspaceCallCount = workspaceCallees.Count,
            outgoingWorkspaceCalls = workspaceCallees,
            outgoingExternalCallCount = externalCallees.Count,
            outgoingExternalCalls = externalCallees
        });
    }

    [McpServerTool, Description(
        "Inspect the complete inheritance and implementation hierarchy of a type (class, interface, struct, record): " +
        "base classes, implemented interfaces, derived subclasses, implementing types, and member contract mapping.")]
    public static async Task<string> GetTypeHierarchy(string sourceOrPath, string typeSymbolOrLocation)
    {
        using var ctx = WorkspaceEngine.Load(sourceOrPath);
        var target = WorkspaceEngine.ResolveTarget(ctx, typeSymbolOrLocation);
        var typeSymbol = target.Symbol as INamedTypeSymbol
            ?? target.Symbol?.ContainingType
            ?? throw new ArgumentException($"Could not resolve a named type at '{typeSymbolOrLocation}'.");

        var baseTypes = new List<object>();
        for (var bt = typeSymbol.BaseType; bt is not null; bt = bt.BaseType)
        {
            baseTypes.Add(new
            {
                name = bt.Name,
                fullName = bt.MinDisplay(),
                isAbstract = bt.IsAbstract,
                assembly = bt.ContainingAssembly?.Name
            });
        }

        var interfaces = typeSymbol.AllInterfaces.Select(i => new
        {
            name = i.Name,
            fullName = i.MinDisplay(),
            isDirect = typeSymbol.Interfaces.Contains(i, SymbolEqualityComparer.Default)
        }).ToArray();

        var derivedSet = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        if (typeSymbol.TypeKind == TypeKind.Class)
        {
            foreach (var d in await SymbolFinder.FindDerivedClassesAsync(typeSymbol, ctx.Solution))
                derivedSet.Add(d);
        }
        else if (typeSymbol.TypeKind == TypeKind.Interface)
        {
            foreach (var d in await SymbolFinder.FindDerivedInterfacesAsync(typeSymbol, ctx.Solution))
                derivedSet.Add(d);
            foreach (var impl in (await SymbolFinder.FindImplementationsAsync(typeSymbol, ctx.Solution)).OfType<INamedTypeSymbol>())
                derivedSet.Add(impl);
        }

        foreach (var doc in ctx.UserDocuments)
        {
            foreach (var typeDecl in doc.Tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (doc.Model.GetDeclaredSymbol(typeDecl) is INamedTypeSymbol candidate &&
                    !SymbolEqualityComparer.Default.Equals(candidate, typeSymbol) &&
                    InheritsFromOrImplements(candidate, typeSymbol))
                {
                    derivedSet.Add(candidate);
                }
            }
        }

        var derivedTypes = derivedSet.Select(d => new
        {
            name = d.Name,
            fullName = d.MinDisplay(),
            typeKind = d.TypeKind.ToString(),
            isAbstract = d.IsAbstract,
            isSealed = d.IsSealed,
            locations = d.Locations.Where(l => l.IsInSource).Select(WorkspaceEngine.FormatLocation).ToArray()
        }).ToArray();

        var contractImplementations = new List<object>();
        if (typeSymbol.TypeKind == TypeKind.Interface || typeSymbol.IsAbstract)
        {
            foreach (var member in typeSymbol.GetMembers().Where(m => !m.IsImplicitlyDeclared && (typeSymbol.TypeKind == TypeKind.Interface || m.IsAbstract || m.IsVirtual)))
            {
                var implsForMember = new List<object>();
                foreach (var subType in derivedSet.Where(d => d.TypeKind is TypeKind.Class or TypeKind.Struct))
                {
                    var impl = subType.FindImplementationForInterfaceMember(member)
                        ?? subType.GetMembers(member.Name).FirstOrDefault(m => m.IsOverride);
                    if (impl is not null)
                    {
                        implsForMember.Add(new
                        {
                            implementingType = subType.Name,
                            signature = impl.MinDisplay(),
                            location = impl.Locations.FirstOrDefault(l => l.IsInSource) is { } loc ? WorkspaceEngine.FormatLocation(loc) : null
                        });
                    }
                }
                contractImplementations.Add(new
                {
                    contractMember = member.MinDisplay(),
                    kind = member.Kind.ToString(),
                    implementations = implsForMember
                });
            }
        }

        return RoslynHelpers.Ser(new
        {
            type = new
            {
                name = typeSymbol.Name,
                fullName = typeSymbol.MinDisplay(),
                typeKind = typeSymbol.TypeKind.ToString(),
                isAbstract = typeSymbol.IsAbstract,
                isSealed = typeSymbol.IsSealed,
                isRecord = typeSymbol.IsRecord
            },
            baseTypes,
            implementedInterfaces = interfaces,
            derivedAndImplementingTypes = derivedTypes,
            contractImplementations
        });
    }

    [McpServerTool, Description(
        "Perform Roslyn ControlFlowAnalysis and DataFlowAnalysis on a method or a statement range [startLine..endLine]. " +
        "Reports reachability, exit points, return statements, variables declared, dataFlowsIn (parameters needed to extract), " +
        "dataFlowsOut (return values needed to extract), captured variables, and a proposed ExtractMethod signature.")]
    public static string AnalyzeDataAndControlFlow(
        string sourceOrPath,
        string methodOrLocation,
        int startLine = 0,
        int endLine = 0)
    {
        using var ctx = WorkspaceEngine.Load(sourceOrPath);
        var target = WorkspaceEngine.ResolveTarget(ctx, methodOrLocation);
        var doc = target.Document;
        var model = doc.Model;

        var methodNode = target.Node.AncestorsAndSelf().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault()
            ?? target.Node.DescendantNodesAndSelf().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();

        BlockSyntax? bodyBlock = methodNode?.Body;
        SyntaxNode? analysisTarget = bodyBlock ?? (SyntaxNode?)methodNode?.ExpressionBody?.Expression ?? target.Node;

        ControlFlowAnalysis? controlFlow = null;
        DataFlowAnalysis? dataFlow = null;
        StatementSyntax? firstStmt = null;
        StatementSyntax? lastStmt = null;

        if (startLine > 0 && endLine >= startLine && bodyBlock is not null)
        {
            var matchingStmts = FindContiguousStatementsInLineRange(doc.Tree, bodyBlock, startLine, endLine);
            if (matchingStmts.Count > 0)
            {
                firstStmt = matchingStmts[0];
                lastStmt = matchingStmts[^1];
                controlFlow = model.AnalyzeControlFlow(firstStmt, lastStmt);
                dataFlow = model.AnalyzeDataFlow(firstStmt, lastStmt);
            }
        }

        if (dataFlow is null)
        {
            if (bodyBlock is { Statements.Count: > 0 })
            {
                firstStmt = bodyBlock.Statements.First();
                lastStmt = bodyBlock.Statements.Last();
                controlFlow = model.AnalyzeControlFlow(firstStmt, lastStmt);
                dataFlow = model.AnalyzeDataFlow(firstStmt, lastStmt);
            }
            else if (analysisTarget is not null)
            {
                dataFlow = model.AnalyzeDataFlow(analysisTarget);
            }
        }

        static object FormatSymbols(ImmutableArray<ISymbol> syms) => syms.Select(s => new
        {
            name = s.Name,
            kind = s.Kind.ToString(),
            type = s.GetValueOrReturnType()?.MinDisplay() ?? "unknown"
        }).ToArray();

        object? extractMethodPreview = null;
        if (dataFlow is { Succeeded: true })
        {
            var inParams = dataFlow.DataFlowsIn
                .Where(s => s is ILocalSymbol or IParameterSymbol && !s.IsImplicitlyDeclared && s.Name != "this")
                .Select(s => $"{s.GetValueOrReturnTypeString()} {s.Name}")
                .ToArray();

            var outVars = dataFlow.DataFlowsOut
                .Where(s => s is ILocalSymbol or IParameterSymbol && !s.IsImplicitlyDeclared && s.Name != "this")
                .Select(s => (Type: s.GetValueOrReturnTypeString(), s.Name))
                .ToArray();

            bool containsAwait = firstStmt is not null && lastStmt is not null
                && firstStmt.Parent is BlockSyntax blk
                && blk.Statements
                    .SkipWhile(s => s != firstStmt)
                    .TakeWhile(s => s != lastStmt)
                    .Append(lastStmt)
                    .SelectMany(s => s.DescendantNodesAndSelf())
                    .Any(n => n is AwaitExpressionSyntax);

            string rawReturn = outVars.Length switch
            {
                0 => "void",
                1 => outVars[0].Type,
                _ => $"({string.Join(", ", outVars.Select(o => $"{o.Type} {o.Name}"))})"
            };

            string finalReturn = containsAwait
                ? (rawReturn == "void" ? "Task" : $"Task<{rawReturn}>")
                : rawReturn;

            bool hasUnresolvedJump = controlFlow is { Succeeded: true } && controlFlow.ExitPoints.Any();
            extractMethodPreview = new
            {
                canSafelyExtract = !hasUnresolvedJump,
                reason = hasUnresolvedJump
                    ? "Region contains non-linear exit points (early return/break/continue) that leave the selected block."
                    : "Region has clean linear entry/exit and can be extracted automatically.",
                isAsync = containsAwait,
                proposedReturnType = finalReturn,
                proposedParameters = inParams,
                outFlowingVariables = outVars.Select(o => new { o.Name, o.Type }).ToArray(),
                proposedSignature = $"private {(containsAwait ? "async " : "")}{finalReturn} ExtractedMethod({string.Join(", ", inParams)})"
            };
        }

        return RoslynHelpers.Ser(new
        {
            enclosingMethod = methodNode is not null ? model.GetDeclaredSymbol(methodNode)?.MinDisplay() : null,
            analyzedRange = firstStmt is not null && lastStmt is not null ? new
            {
                startLine = firstStmt.LineRange().StartLine,
                endLine = lastStmt.LineRange().EndLine
            } : null,
            controlFlow = controlFlow is null ? null : new
            {
                succeeded = controlFlow.Succeeded,
                startPointIsReachable = controlFlow.StartPointIsReachable,
                endPointIsReachable = controlFlow.EndPointIsReachable,
                returnStatementCount = controlFlow.ReturnStatements.Length,
                returnStatements = controlFlow.ReturnStatements.Select(s => s.ToString()).ToArray(),
                exitPointCount = controlFlow.ExitPoints.Length,
                exitPoints = controlFlow.ExitPoints.Select(s => new
                {
                    kind = s.Kind().ToString(),
                    line = s.LineRange().StartLine,
                    text = s.ToString()
                }).ToArray()
            },
            dataFlow = dataFlow is null ? null : new
            {
                succeeded = dataFlow.Succeeded,
                variablesDeclared = FormatSymbols(dataFlow.VariablesDeclared),
                dataFlowsIn = FormatSymbols(dataFlow.DataFlowsIn),
                dataFlowsOut = FormatSymbols(dataFlow.DataFlowsOut),
                alwaysAssigned = FormatSymbols(dataFlow.AlwaysAssigned),
                readInside = FormatSymbols(dataFlow.ReadInside),
                writtenInside = FormatSymbols(dataFlow.WrittenInside),
                readOutside = FormatSymbols(dataFlow.ReadOutside),
                writtenOutside = FormatSymbols(dataFlow.WrittenOutside),
                capturedVariables = FormatSymbols(dataFlow.Captured)
            },
            extractMethodPreview
        });
    }

    [McpServerTool, Description(
        "List all symbols (locals, parameters, fields, properties, methods, extension methods, and types) " +
        "accessible in scope at a specific code position using SemanticModel.LookupSymbols.")]
    public static string GetScopeSymbols(
        string sourceOrPath,
        string location,
        string filter = "",
        bool includeExtensionMethods = true)
    {
        using var ctx = WorkspaceEngine.Load(sourceOrPath);
        var target = WorkspaceEngine.ResolveTarget(ctx, location);
        var symbols = target.Document.Model.LookupSymbols(target.Position, includeReducedExtensionMethods: includeExtensionMethods);

        if (!string.IsNullOrWhiteSpace(filter))
        {
            var q = filter.Trim();
            symbols = symbols.Where(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToImmutableArray();
        }

        var localsAndParams = symbols
            .Where(s => s.Kind is SymbolKind.Local or SymbolKind.Parameter)
            .Select(s => new
            {
                name = s.Name,
                kind = s.Kind.ToString(),
                type = s.GetValueOrReturnTypeString()
            })
            .ToArray();

        var members = symbols
            .Where(s => s.Kind is SymbolKind.Field or SymbolKind.Property or SymbolKind.Method or SymbolKind.Event
                && s is not IMethodSymbol { IsExtensionMethod: true })
            .GroupBy(s => s.Name)
            .Select(g =>
            {
                var first = g.First();
                return new
                {
                    name = g.Key,
                    kind = first.Kind.ToString(),
                    overloadCount = g.Count(),
                    signature = first.MinDisplay(),
                    containingType = first.ContainingType?.Name
                };
            })
            .Take(80)
            .ToArray();

        var extensionMethods = symbols
            .OfType<IMethodSymbol>()
            .Where(m => m.IsExtensionMethod)
            .GroupBy(m => m.Name)
            .Select(g => new
            {
                name = g.Key,
                signature = g.First().MinDisplay(),
                containingType = g.First().ContainingType?.Name
            })
            .Take(50)
            .ToArray();

        return RoslynHelpers.Ser(new
        {
            position = new { file = target.Document.Name, line = target.Line, column = target.Column },
            totalAccessibleSymbols = symbols.Length,
            localsAndParameters = localsAndParams,
            membersOnEnclosingTypes = members,
            extensionMethods
        });
    }

    internal static List<StatementSyntax> FindContiguousStatementsInLineRange(
        SyntaxTree tree,
        BlockSyntax rootBlock,
        int startLine1Based,
        int endLine1Based)
    {
        foreach (var block in rootBlock.DescendantNodesAndSelf().OfType<BlockSyntax>())
        {
            var stmts = block.Statements.Where(s =>
            {
                var lr = tree.LineRange(s.Span);
                return lr.StartLine >= startLine1Based && lr.EndLine <= endLine1Based;
            }).ToList();

            if (stmts.Count > 0)
                return stmts;
        }
        return new List<StatementSyntax>();
    }

    private static object BuildReferenceRecord(DocumentEntry doc, Microsoft.CodeAnalysis.Text.TextSpan span)
    {
        var node = doc.Tree.GetRoot().FindNode(span);
        var enclosingMemberNode = node.Ancestors().FirstOrDefault(a =>
            a is BaseMethodDeclarationSyntax or PropertyDeclarationSyntax or FieldDeclarationSyntax or BaseTypeDeclarationSyntax);
        var enclosingSymbol = enclosingMemberNode is not null ? doc.Model.GetDeclaredSymbol(enclosingMemberNode) : null;

        return new
        {
            access = ClassifyReferenceAccess(doc.Model, node),
            enclosingMember = enclosingSymbol?.MinDisplay(),
            location = WorkspaceEngine.FormatLocation(doc.Tree.GetLocation(span))
        };
    }

    private static string ClassifyReferenceAccess(SemanticModel model, SyntaxNode node)
    {
        for (var cur = node; cur is not null && cur is not StatementSyntax and not MemberDeclarationSyntax; cur = cur.Parent)
        {
            if (cur.Parent is AssignmentExpressionSyntax assign && assign.Left.Span.Contains(node.Span))
                return assign.IsKind(SyntaxKind.SimpleAssignmentExpression) ? "Write" : "ReadWrite";
            if (cur.Parent is PrefixUnaryExpressionSyntax prefix &&
                (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression)))
                return "ReadWrite";
            if (cur.Parent is PostfixUnaryExpressionSyntax postfix &&
                (postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression)))
                return "ReadWrite";
            if (cur.Parent is ArgumentSyntax arg && !arg.RefKindKeyword.IsKind(SyntaxKind.None))
                return arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) ? "Write" : "ReadWrite";
            if (cur.Parent is InvocationExpressionSyntax inv && inv.Expression.Span.Contains(node.Span))
                return "Invocation";
            if (cur.Parent is ObjectCreationExpressionSyntax obj && obj.Type.Span.Contains(node.Span))
                return "ObjectCreation";
        }

        return model.GetSymbolInfo(node).Symbol is ITypeSymbol ? "TypeReference" : "Read";
    }

    private static async Task<List<object>> TraceCallersAsync(
        WorkspaceContext ctx,
        ISymbol symbol,
        int remainingDepth,
        HashSet<ISymbol> visited)
    {
        var results = new List<object>();
        if (remainingDepth <= 0 || !visited.Add(symbol))
            return results;

        foreach (var caller in await SymbolFinder.FindCallersAsync(symbol, ctx.Solution))
        {
            var nestedCallers = remainingDepth > 1
                ? await TraceCallersAsync(ctx, caller.CallingSymbol, remainingDepth - 1, visited)
                : new List<object>();

            results.Add(new
            {
                caller = caller.CallingSymbol.MinDisplay(),
                containingType = caller.CallingSymbol.ContainingType?.Name,
                isDirect = caller.IsDirect,
                callSites = caller.Locations.Where(l => l.IsInSource).Select(WorkspaceEngine.FormatLocation).ToArray(),
                calledBy = nestedCallers
            });
        }
        return results;
    }

    private static (List<object> WorkspaceCalls, List<object> ExternalCalls, bool IsRecursive) TraceCallees(
        WorkspaceContext ctx,
        ISymbol symbol,
        int remainingDepth,
        HashSet<ISymbol> visited)
    {
        var workspaceCalls = new List<object>();
        var externalCalls = new List<object>();
        bool isRecursive = false;

        if (remainingDepth <= 0 || !visited.Add(symbol))
            return (workspaceCalls, externalCalls, false);

        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            var syntax = syntaxRef.GetSyntax();
            var doc = ctx.UserDocuments.FirstOrDefault(d => d.Tree == syntax.SyntaxTree);
            if (doc is null) continue;

            var seenCallees = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in syntax.DescendantNodes())
            {
                if (node is not (InvocationExpressionSyntax or ObjectCreationExpressionSyntax))
                    continue;

                var calleeSym = doc.Model.ResolveSymbol(node);
                if (calleeSym is null) continue;

                if (SymbolEqualityComparer.Default.Equals(calleeSym.OriginalDefinition, symbol.OriginalDefinition))
                    isRecursive = true;

                var sig = calleeSym.MinDisplay();
                var line = node.LineRange().StartLine;
                if (calleeSym.Locations.Any(l => l.IsInSource))
                {
                    if (!seenCallees.Add($"ws:{sig}:{line}")) continue;
                    var (nestedWs, _, _) = remainingDepth > 1
                        ? TraceCallees(ctx, calleeSym, remainingDepth - 1, new HashSet<ISymbol>(visited, SymbolEqualityComparer.Default))
                        : (new List<object>(), new List<object>(), false);

                    workspaceCalls.Add(new
                    {
                        callee = sig,
                        containingType = calleeSym.ContainingType?.Name,
                        callLine = line,
                        calls = nestedWs
                    });
                }
                else if (seenCallees.Add($"ext:{sig}"))
                {
                    externalCalls.Add(new
                    {
                        callee = sig,
                        containingType = calleeSym.ContainingType?.MinDisplay(),
                        assembly = calleeSym.ContainingAssembly?.Name,
                        firstCallLine = line
                    });
                }
            }
        }

        return (workspaceCalls, externalCalls, isRecursive);
    }

    private static bool InheritsFromOrImplements(INamedTypeSymbol candidate, INamedTypeSymbol target)
    {
        for (var bt = candidate.BaseType; bt is not null; bt = bt.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(bt.OriginalDefinition, target.OriginalDefinition))
                return true;
        }
        return candidate.AllInterfaces.Any(i =>
            SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, target.OriginalDefinition));
    }

    private static string[] GetTypeParameterConstraints(ITypeParameterSymbol tp)
    {
        var list = new List<string>();
        if (tp.HasReferenceTypeConstraint) list.Add("class");
        if (tp.HasValueTypeConstraint) list.Add("struct");
        if (tp.HasUnmanagedTypeConstraint) list.Add("unmanaged");
        if (tp.HasNotNullConstraint) list.Add("notnull");
        foreach (var ct in tp.ConstraintTypes)
            list.Add(ct.MinDisplay());
        if (tp.HasConstructorConstraint) list.Add("new()");
        return list.ToArray();
    }
}
