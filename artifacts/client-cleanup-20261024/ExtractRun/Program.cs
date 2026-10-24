using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var root = Path.GetFullPath(args[0]);
var folder = Path.Combine(root, "client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag");
var originalPath = Path.Combine(folder, "SourceBackedAgentV2Runner.cs");
var beforePath = Path.Combine(root, "artifacts/client-cleanup-20261024/ExtractRun/runner-before.cs.txt");
var originalText = File.ReadAllText(File.Exists(beforePath) ? beforePath : originalPath);
var parseOptions = new CSharpParseOptions(preprocessorSymbols: ["TRACE", "SAAIA_TEST_HOOKS"]);
var sourceTrees = Directory.EnumerateFiles(folder, "*.cs")
    .Where(path => File.ReadAllText(path).Contains("partial class SourceBackedAgentV2Runner"))
    .Where(path => path == originalPath || !Path.GetFileName(path).StartsWith("SourceBackedAgentV2Runner.") || Path.GetFileName(path) == "SourceBackedAgentV2Runner.Construction.cs")
    .Select(path => CSharpSyntaxTree.ParseText(path == originalPath ? originalText : File.ReadAllText(path), parseOptions, path)).ToList();
sourceTrees.Add(CSharpSyntaxTree.ParseText("global using System; global using System.Linq; global using System.Collections.Generic; global using System.Threading; global using System.Threading.Tasks; global using System.Text; global using System.Text.Json; global using SAAIA.Client.WinUI.Services; global using SAAIA.Client.WinUI.Services.ToolAgent;", parseOptions));
var references = Directory.EnumerateFiles(@"C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\8.0.27\ref\net8.0", "*.dll")
    .Concat(Directory.EnumerateFiles(Path.Combine(root, "client/SAAIA.Client.WinUI/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64"), "*.dll")
        .Where(path => !Path.GetFileName(path).StartsWith("System.") && !Path.GetFileName(path).StartsWith("Microsoft.Extensions.")))
    .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
var compilation = CSharpCompilation.Create("ExtractRun", sourceTrees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
var tree = sourceTrees.Single(tree => tree.FilePath == originalPath);
var syntax = tree.GetRoot();
var semantic = compilation.GetSemanticModel(tree);
var run = syntax.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == "RunCoreAsync");
var loop = run.Body!.Statements.OfType<ForStatementSyntax>().Single();
var runLocals = run.Body.Statements.TakeWhile(statement => statement != loop)
    .OfType<LocalDeclarationStatementSyntax>().Where(statement => statement.UsingKeyword.IsKind(SyntaxKind.None))
    .SelectMany(statement => statement.Declaration.Variables).ToArray();
var iterationLocals = ((BlockSyntax)loop.Statement).Statements.OfType<LocalDeclarationStatementSyntax>()
    .SelectMany(statement => statement.Declaration.Variables).ToArray();
var locals = runLocals.Concat(iterationLocals).Select(variable =>
{
    var symbol = (ILocalSymbol)semantic.GetDeclaredSymbol(variable)!;
    return new { variable.Identifier.ValueText, Type = symbol.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier | SymbolDisplayMiscellaneousOptions.UseSpecialTypes)), symbol, Variable = variable };
}).ToArray();
if (args.Length == 1 || args[1] == "--inspect")
{
    foreach (var variable in locals) Console.WriteLine(variable.ValueText + ": " + variable.Type);
    foreach (var diagnostic in compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).Take(15)) Console.WriteLine(diagnostic);
    return;
}

var statements = ((BlockSyntax)loop.Statement).Statements.ToArray();
var boundaries = new[] { 0,
    Array.FindIndex(statements, statement => statement is LocalDeclarationStatementSyntax local && local.Declaration.Variables.Any(variable => variable.Identifier.ValueText == "completion")),
    Array.FindIndex(statements, statement => statement is LocalDeclarationStatementSyntax local && local.Declaration.Variables.Any(variable => variable.Identifier.ValueText == "semanticYieldTerminalSubmission")),
    Array.FindIndex(statements, statement => statement is ExpressionStatementSyntax expression && expression.Expression is AssignmentExpressionSyntax assignment && assignment.Left.ToString() == "latestDraft"),
    statements.Length };
if (!boundaries.SequenceEqual(boundaries.Order()) || boundaries.Any(index => index < 0)) throw new Exception("phase_boundary_missing");
int PhaseOf(SyntaxNode node)
{
    for (var phase = 0; phase < 4; phase++)
        if (node.SpanStart >= statements[boundaries[phase]].SpanStart && node.Span.End <= statements[boundaries[phase + 1] - 1].Span.End) return phase + 1;
    return node.SpanStart < loop.SpanStart ? 0 : 5;
}
var owners = new Dictionary<ISymbol, (string Owner, string Name)>(SymbolEqualityComparer.Default);
var ownedRun = new List<(string Name, string Type)>();
var ownedIteration = new List<(string Name, string Type)>();
foreach (var local in locals)
{
    var phases = run.DescendantNodes().OfType<IdentifierNameSyntax>()
        .Where(identifier => SymbolEqualityComparer.Default.Equals(semantic.GetSymbolInfo(identifier).Symbol, local.symbol))
        .Select(PhaseOf).Append(PhaseOf(local.Variable)).Distinct().ToArray();
    if (phases.Length <= 1 && local.ValueText != "traceId") continue;
    var owner = runLocals.Contains(local.Variable) ? "runState" : "iteration";
    var type = local.Type.Replace("global::SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag.SourceBackedAgentV2Runner.", "")
        .Replace("global::SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag.", "")
        .Replace("global::System.Collections.Generic.", "")
        .Replace("global::SAAIA.Client.Core.Execution.", "");
    if (string.IsNullOrWhiteSpace(type) || type == "?") type = local.ValueText switch
    {
        "selectionEligibleObservedEvidenceIds" => "string[]",
        "semanticSelectionHandling" => "SemanticSelectionHandling",
        _ => throw new Exception("unresolved_owned_type:" + local.ValueText)
    };
    if (((VariableDeclarationSyntax)local.Variable.Parent!).Type.IsVar)
    {
        type = type.TrimEnd('?');
        if (local.Variable.Initializer?.Value is MemberAccessExpressionSyntax access && access.Expression.ToString() == "initialization")
        {
            var initializationRecord = sourceTrees.SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<RecordDeclarationSyntax>()).Single(record => record.Identifier.ValueText == "RunInitialization");
            if (initializationRecord.ParameterList!.Parameters.Single(parameter => parameter.Identifier.ValueText == access.Name.Identifier.ValueText).Type is NullableTypeSyntax) type += "?";
        }
    }
    owners.Add(local.symbol, (owner, local.ValueText));
    (owner == "runState" ? ownedRun : ownedIteration).Add((local.ValueText, type));
}
var turnSymbol = semantic.GetDeclaredSymbol(loop.Declaration!.Variables.Single())!;
owners.Add(turnSymbol, ("runState", "turn"));
foreach (var parameter in run.ParameterList.Parameters.Where(parameter => parameter.Identifier.ValueText is "intake" or "lifecycle"))
    owners.Add(semantic.GetDeclaredSymbol(parameter)!, ("runState", parameter.Identifier.ValueText));
var rewriter = new StateRewriter(semantic, owners, loop);
string Rewrite(SyntaxNode node) => rewriter.Visit(node)?.ToFullString() ?? string.Empty;
string Format(string source) => CSharpSyntaxTree.ParseText(source, parseOptions).GetRoot().NormalizeWhitespace().ToFullString() + Environment.NewLine;
string SyntaxCanonical(string source) => CSharpSyntaxTree.ParseText(source, parseOptions).GetRoot().NormalizeWhitespace().ToFullString();
string DedentIteration(string body) => string.Join("\n", body.Replace("\r", "").Split('\n').Select(line => line.StartsWith("    ") ? line[4..] : line));
string Indent(string body, int spaces) => string.Join("\n", body.Replace("\r", "").Split('\n').Select(line => line.Length == 0 ? line : new string(' ', spaces) + line));
const string header = "using SAAIA.Client.Core.Execution;\n\nnamespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;\n\npublic sealed partial class SourceBackedAgentV2Runner\n{\n";
const string outcome = "AgentIterationOutcome<SourceBackedPipelineResult>";
var phaseNames = new[] { "PrepareIterationAsync", "AcquireIterationCompletionAsync", "ProcessIterationSubmissionsAndToolsAsync", "VerifyAndReviewIterationDraftAsync" };
var phaseProof = new List<object>();
var expectedOutput = new Dictionary<string, string>();
for (var phase = 0; phase < 4; phase++)
{
    var original = statements[boundaries[phase]..boundaries[phase + 1]];
    var body = string.Concat(original.Select(Rewrite));
    var text = header + $"    private async Task<{outcome}> {phaseNames[phase]}(\n        RunExecutionState runState, IterationExecutionState iteration, CancellationToken ct)\n    {{\n" + DedentIteration(body)
        + $"\n        return {outcome}.Advance;\n    }}\n}}\n";
    var file = Path.Combine(folder, "SourceBackedAgentV2Runner." + phaseNames[phase].Replace("Async", "") + ".cs");
    expectedOutput.Add(file, text);
    phaseProof.Add(new { phase = phaseNames[phase], originalTopLevelStatements = original.Length,
        originalStatementsIncludingNested = original.Sum(statement => statement.DescendantNodesAndSelf().OfType<StatementSyntax>().Count()),
        outerContinues = original.Sum(statement => statement.DescendantNodesAndSelf().OfType<ContinueStatementSyntax>().Count(node => StateRewriter.ControlTarget(node) == loop)),
        outerBreaks = original.Sum(statement => statement.DescendantNodesAndSelf().OfType<BreakStatementSyntax>().Count(node => StateRewriter.ControlTarget(node) == loop)),
        runReturns = original.Sum(statement => statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Count(StateRewriter.IsRunReturn)),
        canonicalSha256 = Hash(SyntaxCanonical(body)) });
}
var initStatements = run.Body.Statements.TakeWhile(statement => statement != loop).Where(statement => statement is not LocalDeclarationStatementSyntax local
    || (!local.UsingKeyword.IsKind(SyntaxKind.UsingKeyword) && !local.Declaration.Variables.Any(variable => variable.Identifier.ValueText == "traceId")))
    .Where(statement => statement != run.Body.Statements.First()).ToArray();
expectedOutput.Add(Path.Combine(folder, "SourceBackedAgentV2Runner.InitializeExecution.cs"), header + "    private async Task InitializeExecutionAsync(RunExecutionState runState, CancellationToken ct)\n    {\n" + string.Concat(initStatements.Select(Rewrite)) + "\n    }\n}\n");
string Fields(IEnumerable<(string Name, string Type)> values) => string.Join("\n", values.Select(value =>
    $"public {value.Type} {value.Name}" + (value.Type is "bool" or "int" || value.Type.EndsWith('?') ? ";" : " = null!;")));
expectedOutput.Add(Path.Combine(folder, "SourceBackedAgentV2Runner.ExecutionState.cs"), Format(header + """
    // These owners are allocated per RunAsync call. No execution state is stored on the reusable runner.
    // Nonnullable run references are assigned by InitializeExecutionAsync, which RunCore awaits before
    // invoking any phase. If initialization fails, no phase is entered. Nullable fields express absence.
    // Iteration references are assigned by preparation or acquisition before later phases may read them.
    private sealed class RunExecutionState(SourceBackedIntake initialIntake, AgentRunLifecycle runLifecycle)
    {
        public SourceBackedIntake intake = initialIntake;
        public readonly AgentRunLifecycle lifecycle = runLifecycle;
        public readonly AgentIterationObserverHealth phaseObserverHealth = new();
        public readonly SourceBackedTelemetryContext.CollectorHealth traceObserverHealth = new();
        public int turn;
""" + Fields(ownedRun) + "\n}\nprivate sealed class IterationExecutionState\n{\n" + Fields(ownedIteration) + "\n}\n}"));
var finalReturn = run.Body.Statements.Last();
var core = $$"""
    private async Task<SourceBackedPipelineResult> RunCoreAsync(
        SourceBackedIntake intake, AgentRunLifecycle lifecycle, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intake);
        var runState = new RunExecutionState(intake, lifecycle)
        {
            traceId = "source-backed-agent-v2-" + Guid.NewGuid().ToString("N")[..10]
        };
        using var telemetryScope = SourceBackedTelemetryContext.Push(runState.traceId, runState.traceObserverHealth);
        try
        {
            await InitializeExecutionAsync(runState, ct).ConfigureAwait(false);
            for (runState.turn = 1; runState.turn <= runState.maximumRunTurns; runState.turn++)
            {
                var iteration = new IterationExecutionState();
                var outcome = await ExecuteIterationAsync(runState, iteration, ct).ConfigureAwait(false);
                switch (outcome.Disposition)
                {
                    case AgentIterationDisposition.Complete:
                        return outcome.Result!;
                    case AgentIterationDisposition.Continue:
                    case AgentIterationDisposition.Advance:
                        continue;
                    case AgentIterationDisposition.Stop:
                        break;
                    default:
                        throw new InvalidOperationException("unknown_agent_iteration_outcome");
                }
                break;
            }
            {{Indent(Rewrite(finalReturn), 4)}}
        }
        finally
        {
            var traceFailures = runState.traceObserverHealth.TraceSinkFailures;
            var phaseFailures = runState.phaseObserverHealth.ObserverFailures;
            ClientDiagnostics.Journal.Emit("agent.collector_health", traceFailures + phaseFailures > 0 ? "degraded" : "success",
                traceFailures + phaseFailures > 0 ? "observer_failed" : "no_observer_failures",
                new Dictionary<string, long?> { ["trace_sink_failures"] = traceFailures, ["phase_observer_failures"] = phaseFailures,
                    ["trace_sink_configured"] = _traceSink is null ? 0 : 1 });
        }
    }

    private async Task<{{outcome}}> ExecuteIterationAsync(
        RunExecutionState runState, IterationExecutionState iteration, CancellationToken ct)
    {
        try
        {
            return await AgentIterationPipeline.RunAsync(runState, iteration,
            new AgentIterationStep<RunExecutionState, IterationExecutionState, SourceBackedPipelineResult>[]
            {
                new("prepare", PrepareIterationAsync),
                new("acquire_completion", AcquireIterationCompletionAsync),
                new("submissions_and_tools", ProcessIterationSubmissionsAndToolsAsync),
                new("verify_and_review", VerifyAndReviewIterationDraftAsync)
            }, ct, runState.phaseObserverHealth, observation => ClientDiagnostics.Journal.Emit("agent.iteration." + observation.Phase,
                observation.Outcome, observation.Reason, new Dictionary<string, long?>
                {
                    ["duration_ms"] = observation.ElapsedMilliseconds,
                    ["iteration"] = runState.lifecycle.Iteration
                })).ConfigureAwait(false);
        }
        finally
        {
            if (runState.phaseObserverHealth.ObserverFailures > 0)
                ClientDiagnostics.Journal.Emit("agent.iteration.collector_health", "unavailable", "observer_failed",
                    new Dictionary<string, long?> { ["observer_failures"] = runState.phaseObserverHealth.ObserverFailures });
        }
    }
""";
var outerMethods = syntax.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(method => method != run).ToArray();
expectedOutput.Add(originalPath, header + string.Join("\n", outerMethods.Select(method => method.ToFullString())) + core + "\n}\n");
foreach (var file in expectedOutput.Keys.ToArray())
    expectedOutput[file] = string.Join("\n", expectedOutput[file].Replace("\r", "").Split('\n').Select(line => line.TrimEnd()));
var evidencePath = Path.Combine(root, "artifacts/client-cleanup-20261024/runner-phase-extraction-proof.json");
if (args[1] == "--write")
{
    File.WriteAllText(beforePath, originalText);
    foreach (var (file, contents) in expectedOutput) File.WriteAllText(file, contents);
}
else if (args[1] == "--verify")
{
    foreach (var (file, contents) in expectedOutput)
        if (!string.Equals(SyntaxCanonical(File.ReadAllText(file)), SyntaxCanonical(contents), StringComparison.Ordinal)) throw new Exception("phase_syntax_not_preserved:" + file);
}
else throw new Exception("expected_write_or_verify");
var proof = new
{
    originalSourceSha256 = Hash(originalText), transformation = "Semantic-symbol local runState lifting; outer-loop exits become typed outcomes; inner loops/switch/local-function exits remain unchanged.",
    originalLoopTopLevelStatements = statements.Length,
    partitionedExactlyOnceInOriginalOrder = boundaries[0] == 0 && boundaries[^1] == statements.Length,
    runStateFields = ownedRun.Select(field => new { field.Name, field.Type }),
    iterationStateFields = ownedIteration.Select(field => new { field.Name, field.Type }),
    phases = phaseProof,
    output = expectedOutput.Select(pair => new { file = Path.GetRelativePath(root, pair.Key).Replace('\\', '/'), sha256 = Hash(pair.Value) }),
    limitations = "Static preservation proof of one extraction, not semantic acceptance of the inherited policies or a model/UI test."
};
File.WriteAllText(evidencePath, JsonSerializer.Serialize(proof, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Preserved {statements.Length} ordered top-level iteration statements into four typed phases; {ownedRun.Count} run and {ownedIteration.Count} iteration fields.");

static string Hash(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

sealed class StateRewriter(SemanticModel semantic, Dictionary<ISymbol, (string Owner, string Name)> owners, ForStatementSyntax outerLoop) : CSharpSyntaxRewriter
{
    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        var symbol = semantic.GetSymbolInfo(node).Symbol;
        if (symbol is not null && owners.TryGetValue(symbol, out var owner))
            return SyntaxFactory.ParseExpression(owner.Owner + "." + owner.Name).WithTriviaFrom(node);
        return base.VisitIdentifierName(node);
    }

    public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
    {
        if (!node.Declaration.Variables.Any(variable => owners.ContainsKey(semantic.GetDeclaredSymbol(variable)!))) return base.VisitLocalDeclarationStatement(node);
        var assignments = node.Declaration.Variables.Select(variable =>
        {
            var owner = owners[semantic.GetDeclaredSymbol(variable)!];
            return variable.Initializer is null ? null : SyntaxFactory.ParseStatement(owner.Owner + "." + owner.Name + " = " + Visit(variable.Initializer.Value)!.ToFullString().TrimStart() + ";");
        }).Where(statement => statement is not null).Cast<StatementSyntax>().ToArray();
        return assignments.Length == 0 ? null : assignments.Length == 1 ? assignments[0].WithTriviaFrom(node) : SyntaxFactory.Block(assignments).WithTriviaFrom(node);
    }

    public override SyntaxNode? VisitContinueStatement(ContinueStatementSyntax node) => ControlTarget(node) == outerLoop
        ? SyntaxFactory.ParseStatement("return AgentIterationOutcome<SourceBackedPipelineResult>.Continue;").WithTriviaFrom(node) : base.VisitContinueStatement(node);
    public override SyntaxNode? VisitBreakStatement(BreakStatementSyntax node) => ControlTarget(node) == outerLoop
        ? SyntaxFactory.ParseStatement("return AgentIterationOutcome<SourceBackedPipelineResult>.Stop;").WithTriviaFrom(node) : base.VisitBreakStatement(node);
    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node) => IsRunReturn(node) && outerLoop.Span.Contains(node.Span)
        ? SyntaxFactory.ParseStatement("return AgentIterationOutcome<SourceBackedPipelineResult>.Complete(" + Visit(node.Expression)!.ToFullString().Trim() + ");").WithTriviaFrom(node)
        : base.VisitReturnStatement(node);

    public static bool IsRunReturn(ReturnStatementSyntax node) => node.Ancestors().First(ancestor => ancestor is MethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax) is MethodDeclarationSyntax;
    public static SyntaxNode? ControlTarget(StatementSyntax node) => node.Ancestors().FirstOrDefault(ancestor => ancestor is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax || node is BreakStatementSyntax && ancestor is SwitchStatementSyntax);
}
