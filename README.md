# RoslynMcp — Compiler-Grade C# Intelligence & Refactoring for AI Agents

`RoslynMcp` is a .NET 8 Model Context Protocol (MCP) server that gives AI coding agents direct access to the **Microsoft Roslyn Compiler Platform** (`SemanticModel`, `SymbolFinder`, `Renamer`, `ControlFlowAnalysis`, `DataFlowAnalysis`, `SyntaxFactory`, and `CSharpSyntaxRewriter`).

---

## Why You Need `RoslynMcp` (And How It Upgrades an AI Agent)

Without a compiler-backed MCP server, AI agents treat C# codebases as **flat text files**:
- **High Token Burn**: Agents read thousands of lines of implementation bodies just to understand class contracts.
- **Blind Regex & Grep**: Agents cannot distinguish overloaded methods (`Process(Order)` vs `Process(string)`), interface implementations, or implicit `var` types.
- **Fragile Line/Offset Edits**: Manual string splicing frequently corrupts braces, misses callers across files, or breaks variable scoping when extracting methods.
- **No Semantic Verification**: Agents guess whether their edits compile or whether a change broke a public API contract.

`RoslynMcp` upgrades any MCP-capable AI agent into a **compiler-aware C# engineer**:
1. **Understand Codebases in 80% Fewer Tokens**: `GetPublicApiSurface`, `InspectWorkspace`, `GetCallGraph`, `GetTypeHierarchy`, and `GetDependencyGraph` expose exact architecture, coupling, and circular dependencies without dumping raw method bodies.
2. **Navigate by Meaning, Not Regex**: `GetSymbolInfo`, `FindSymbols`, `FindReferences` (classified as `Read`, `Write`, `ReadWrite`, `Invocation`), and `GetScopeSymbols` resolve symbols across files and .NET 8 BCL metadata.
3. **Mutate ASTs Safely Without Counting Offsets**: `RenameSymbol`, `AddOrReplaceMember`, `RemoveMember`, `ExtractMethod`, `ImplementInterface`, `GenerateTypeMembers`, `OrganizeUsings`, and `ApplySyntaxTransform` target symbols by name or `line:col` and validate compilation diagnostics automatically.
4. **Prevent Subtle Refactoring Bugs**: `AnalyzeDataAndControlFlow` and `ExtractMethod` use Roslyn's data-flow engine to compute exact input parameters (`dataFlowsIn`), single or tuple return values (`dataFlowsOut`), and `async`/`Task` wrappers.

---

## AI Agent Interaction: Without `RoslynMcp` vs. With `RoslynMcp`

**User Prompt:**
> *"In our Order processing project, extract the pricing/tax calculation in `OrderService.CheckoutAsync` (lines 45–58) into a helper method, rename `IOrderRepository.Save` to `SaveOrder` across the codebase, and verify there are no compiler errors."*

### Without `RoslynMcp` (Text & Regex Agent)

1. **Context Bloat (`grep` + `read_file` x 8 files)**:
   - Agent greps for `"Save"` and gets 47 hits (including `File.Save`, comments, and ` Draft.Save`).
   - Agent reads 8 full `.cs` files into context (~14,000 tokens).
2. **Broken Manual Method Extraction (`edit_file`)**:
   - Agent cuts lines 45–58 out of `CheckoutAsync` and pastes them into a new method, but misses that `discountApplied` was mutated inside those lines and read on line 64 — introducing a **`CS0103` undeclared variable error** and forgetting `async Task<(decimal, bool)>`.
3. **Destructive Text Rename (`sed` / `edit_file`)**:
   - Agent replaces `.Save(` with `.SaveOrder(`, accidentally renaming an unrelated `AuditLogger.Save(...)` call while missing an explicit interface implementation `void IOrderRepository.Save(Order o)`.
4. **Result**: 6 tool calls, ~18,000 tokens consumed, 3 compiler errors left in the codebase.

---

### With `RoslynMcp` (Semantic Roslyn Agent)

1. **Instant Architectural & Data-Flow Inspection**:
   ```json
   // 1. Agent previews the exact data flow of lines 45..58 before touching code:
   { "tool": "AnalyzeDataAndControlFlow", "args": { "sourceOrPath": "OrderService.cs", "methodOrLocation": "OrderService.CheckoutAsync", "startLine": 45, "endLine": 58 } }
   // -> Returns:
   // {
   //   "extractMethodPreview": {
   //     "canSafelyExtract": true,
   //     "isAsync": true,
   //     "proposedSignature": "private static async Task<(decimal tax, bool discountApplied)> ExtractedMethod(Order order, decimal taxRate)"
   //   }
   // }
   ```
2. **Data-Flow Verified Method Extraction**:
   ```json
   // 2. Agent extracts the method via Roslyn AST + DataFlowAnalysis:
   { "tool": "ExtractMethod", "args": { "sourceOrPath": "OrderService.cs", "methodOrLocation": "CheckoutAsync", "startLine": 45, "endLine": 58, "newMethodName": "CalculatePricingAsync", "writeToDisk": true } }
   // -> Generates `(decimal tax, bool discountApplied) = await CalculatePricingAsync(order, taxRate);`
   //    and `private static async Task<(decimal tax, bool discountApplied)> CalculatePricingAsync(...)`
   //    with `errorsAfter: 0`.
   ```
3. **Cross-File Semantic Symbol Rename**:
   ```json
   // 3. Agent renames IOrderRepository.Save across the entire solution:
   { "tool": "RenameSymbol", "args": { "sourceOrPath": "Commerce.slnx", "symbolOrLocation": "IOrderRepository.Save", "newName": "SaveOrder", "writeToDisk": true } }
   // -> Updates interface, all implementing classes, and all genuine callers across 4 files;
   //    leaves unrelated `AuditLogger.Save` untouched. `errorsAfter: 0`.
   ```
4. **Result**: 3 deterministic tool calls, ~1,200 tokens consumed, **0 compiler errors**, verified by Roslyn.

---

## Universal Input & Target Resolution

Every semantic, refactoring, and analysis tool accepts a unified `sourceOrPath` input:
- **Disk Paths**: `.sln`, `.slnx`, `.csproj`, directory path, or `.cs` file path (automatically links sibling project files).
- **Multi-File JSON**: `{"IOrderRepo.cs": "...", "OrderService.cs": "..."}`
- **Raw C# String**: Single-file C# source text (linked against all .NET 8 BCL assemblies and implicit global usings).

Target parameters (`symbolOrLocation` / `location`) accept any of:
- **Symbol Name / Overload**: `"OrderService.CheckoutAsync"`, `"Calc.Add(int, int)"`, `"IOrderRepository"`
- **1-Based Line & Column**: `"45:12"`, `"L45"`, or `"OrderService.cs:45:12"`
- **0-Based Offset**: `"@1024"`

---

## Concise Tool Reference (41 Tools)

### 1. Semantic Understanding & Navigation (`SemanticTools`)
- **`GetSymbolInfo(sourceOrPath, symbolOrLocation)`** — Full symbol metadata, signature, modifiers, generic constraints, base types, interfaces, attributes, XML summary, `TypeInfo`, and `IOperation` kind.
- **`FindSymbols(sourceOrPath, query, kind, accessibility, attribute, modifier, returnType)`** — Semantic search across all declared symbols with wildcard/regex and rich metadata filters.
- **`FindReferences(sourceOrPath, symbolOrLocation)`** — Cross-workspace reference finder classifying each site as `Read`, `Write`, `ReadWrite`, `Invocation`, `ObjectCreation`, or `TypeReference`.
- **`GetCallGraph(sourceOrPath, methodSymbolOrLocation, depth)`** — Bidirectional call graph (incoming callers + outgoing workspace & BCL callees + recursion detection).
- **`GetTypeHierarchy(sourceOrPath, typeSymbolOrLocation)`** — Base classes, direct/transitive interfaces, derived subclasses, implementing types, and contract-to-implementation member map.
- **`AnalyzeDataAndControlFlow(sourceOrPath, methodOrLocation, startLine, endLine)`** — Reachability, exit points, `dataFlowsIn`, `dataFlowsOut`, closure captures, and `ExtractMethod` signature preview.
- **`GetScopeSymbols(sourceOrPath, location, filter, includeExtensionMethods)`** — Lists all in-scope locals, parameters, members, and extension methods at a position.

### 2. Semantic Refactoring & Code Generation (`RefactoringTools`)
- **`RenameSymbol(sourceOrPath, symbolOrLocation, newName, writeToDisk)`** — Solution-wide semantic rename via `Renamer.RenameSymbolAsync`.
- **`AddOrReplaceMember(sourceOrPath, typeName, memberCode, replaceExisting, writeToDisk)`** — Overload-aware AST member insertion or replacement in canonical order.
- **`RemoveMember(sourceOrPath, typeName, memberNameOrSignature, writeToDisk)`** — AST member removal with dangling-reference diagnostic reporting.
- **`ExtractMethod(sourceOrPath, methodOrLocation, startLine, endLine, newMethodName, writeToDisk)`** — Data-flow driven method extraction supporting `async` and tuple returns.
- **`ImplementInterface(sourceOrPath, className, interfaceName, explicitImplementation, writeToDisk)`** — Implements workspace or .NET BCL interfaces (`IDisposable`, `IEquatable<T>`, `IComparable<T>`, etc.).
- **`GenerateTypeMembers(sourceOrPath, typeName, generatorKind, includeNullChecks, writeToDisk)`** — Generates `'constructor'`, `'equals_hashcode'`, `'tostring'`, `'overrides'`, or `'extract_interface'`.
- **`OrganizeUsings(sourceOrPath, targetFile, addMissingUsings, removeUnusedUsings, sortUsings, writeToDisk)`** — Discovers and adds missing `using` namespaces from workspace/.NET BCL, removes unused `using`s, and sorts.
- **`ApplySyntaxTransform(sourceOrPath, transform, targetFile, writeToDisk)`** — AST rewrites: `'file_scoped_namespace'`, `'expression_bodied'`, `'var_to_explicit'`, `'explicit_to_var'`, `'add_readonly_fields'`, `'add_null_checks'`.

### 3. Diagnostics, Metrics, Architecture & API Diffing (`AnalysisTools`)
- **`CompileAndDiagnose(sourceOrPath, minSeverity)`** — Full .NET 8 compilation with actionable fix suggestions for unresolved types and overloads.
- **`GetCodeMetrics(sourceOrPath, complexityThreshold)`** — Cyclomatic Complexity, Cognitive Complexity, Nesting Depth, LOC, Efferent Coupling (`Ce`), Maintainability Index (0–100), and refactoring hotspots.
- **`GetDependencyGraph(sourceOrPath, granularity)`** — Type/namespace/file dependency graph with Afferent/Efferent Coupling (`Ca`, `Ce`), Instability (`I`), Tarjan SCC circular dependency detection, and Mermaid diagram.
- **`SemanticDiff(oldSourceOrPath, newSourceOrPath)`** — Symbol-level API diff detecting breaking changes, visibility reductions, added/removed symbols, and modified method bodies.
- **`GetPublicApiSurface(sourceOrPath, includeInternal, includePrivate, includeDocComments)`** — Token-efficient C# contract skeleton with `SharpToken` reduction stats.

### 4. Scaffolding, Project Inspection & Workflows (`CreativeTools`, `ProjectTools`, `Tools`)
- **`ScaffoldType(specJson, outputPath)`** & **`ScaffoldFile(kind, typeName, ns, usings)`** — Guaranteed-valid `SyntaxFactory` type generation.
- **`QuerySyntaxTree(sourceOrPath, syntaxKinds, textPattern, ancestorKind)`**, **`TreeSummary(source, maxDepth)`**, **`Explain(source, position)`** — Structural and semantic AST inspection.
- **`InspectWorkspace(pathOrSourcesJson)`**, **`ReadCsproj(xml)`**, **`ReadSlnx(xml)`**, **`ParseMany(sourcesJson)`** — Project and solution structure analysis.
- **`Parse`**, **`FindNodes`**, **`ReplaceNode`**, **`Format`**, **`Analyze`**, **`CountTokens`**, **`Execute`** — Core syntax, formatting, token counting, and C# scripting tools.
- **`DefineWorkflow`**, **`ConfirmWorkflow`**, **`ListWorkflows`**, **`RunWorkflow`** — Multi-step tool pipelines across all tool classes with `{{param}}`, `{{$prev}}`, and `{{$prev_text}}` substitution.

---

## Architecture, Helpers & Debuggability

- **`RoslynHelpers.cs` & `WorkspaceEngine.cs`**: Centralizes JSON serialization (`Ser`, `Des<T>`), token counting (`Tok`), symbol formatting (`MinDisplay`, `FullDisplay`, `GetValueOrReturnType`, `GetModifiers`, `GetXmlSummary`), 1-based line spans (`LineRange`), and mutation post-processing (`FinalizeMutation`).
- **Debuggable Domain Models**: Every custom record, struct, and class across the codebase (`WorkspaceContext`, `DocumentEntry`, `ResolvedTarget`, `SourceLocationInfo`, `LineRange`, `DiffSummary`, `DiffChangeItem`, `DiagnosticItem`, `MutationOutcome`, `Workflow`, `WorkflowStep`, scaffold specs, and test models) implements a custom `ToString()` and `[DebuggerDisplay]` conversion for immediate inspection in debuggers and logs.

---

## Running the Server & Verification Test Suite

### Run as an MCP Stdio Server
```bash
dotnet run --project RoslynMcp.csproj
```

### Run the Built-In Test Suite (164 Tests Across All 41 Methods)
Every MCP tool method is tested across **Basic**, **Advanced**, **EdgeCase**, and **FailureCase** scenarios:
```bash
# Run all 164 tests across all 41 methods:
dotnet run --project RoslynMcp.csproj -- --test

# Run tests for a specific tool (e.g. ExtractMethod or RenameSymbol):
dotnet run --project RoslynMcp.csproj -- --test ExtractMethod
```
