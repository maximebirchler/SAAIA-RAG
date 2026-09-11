using System.Reflection;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class Bug137CumulativeLlmBudgetContractTests
{
    private const string BudgetTypeName =
        "SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag.SourceBackedLlmCumulativeBudget";
    private const string ContextTypeName =
        "SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag.SourceBackedLlmCumulativeBudgetContext";

    [Fact]
    public void Bug137_OptionsExposeDistinctCumulativeAndPerCallBudgets()
    {
        var source = ReadProductSource(
            "ToolAgent", "SourceBackedRag", "SourceBackedAgentV2Options.cs");

        Assert.Contains("MaximumContextTokens", source, StringComparison.Ordinal);
        Assert.Contains("MaximumCumulativeLlmTokens", source, StringComparison.Ordinal);
        Assert.Contains("MaximumCumulativeLlmElapsedMilliseconds", source, StringComparison.Ordinal);
        Assert.Contains("CumulativeLlmTerminalReserveTokens", source, StringComparison.Ordinal);
        Assert.Contains("CumulativeLlmTerminalReserveMilliseconds", source, StringComparison.Ordinal);
        Assert.Contains("12_000", source, StringComparison.Ordinal);
        Assert.Contains("240_000", source, StringComparison.Ordinal);
        Assert.Contains("4_800", source, StringComparison.Ordinal);
        Assert.Contains("60_000", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Bug137_BudgetAndAsyncContextTypesExist()
    {
        Assert.NotNull(FindType(BudgetTypeName));
        Assert.NotNull(FindType(ContextTypeName));
    }

    [Fact]
    public void Bug137_ExactNormalFitIsAdmittedAndOneTokenBeyondIsBlockedBeforeIo()
    {
        var budget = CreateBudget(
            maximumTokens: 100,
            maximumElapsedMilliseconds: 1_000,
            terminalReserveTokens: 20,
            terminalReserveMilliseconds: 200,
            elapsedMilliseconds: () => 0);

        var exact = Reserve(budget, inputTokens: 60, maximumOutputTokens: 20, terminal: false);
        var overflow = Reserve(budget, inputTokens: 1, maximumOutputTokens: 0, terminal: false);

        Assert.True(Read<bool>(exact, "Admitted"));
        Assert.Equal(80, Read<int>(exact, "ReservedTokens"));
        Assert.False(Read<bool>(overflow, "Admitted"));
        Assert.Equal("terminal_budget_only", Read<string>(overflow, "Reason"));
        Assert.Equal(1, Read<int>(GetSnapshot(budget), "AdmittedCalls"));
    }

    [Fact]
    public void Bug137_ServerUsageReconcilesAndReleasesUnusedReservation()
    {
        var budget = CreateBudget(100, 1_000, 20, 200, () => 0);
        var admission = Reserve(budget, 50, 20, terminal: false);
        Complete(
            budget,
            Read<long>(admission, "ReservationId"),
            promptTokens: 40,
            completionTokens: 10);

        var snapshot = GetSnapshot(budget);
        Assert.Equal(50, Read<int>(snapshot, "ChargedTokens"));
        Assert.Equal(0, Read<int>(snapshot, "ReservedTokens"));
        Assert.Equal(50, Read<int>(snapshot, "RemainingTokens"));
        Assert.Equal("server_usage", Read<string>(snapshot, "LastUsageSource"));
    }

    [Fact]
    public void Bug137_MissingUsageChargesFullReservationWithoutCreatingCredit()
    {
        var budget = CreateBudget(100, 1_000, 20, 200, () => 0);
        var admission = Reserve(budget, 40, 20, terminal: false);
        Complete(
            budget,
            Read<long>(admission, "ReservationId"),
            promptTokens: null,
            completionTokens: null);

        var snapshot = GetSnapshot(budget);
        Assert.Equal(60, Read<int>(snapshot, "ChargedTokens"));
        Assert.Equal(40, Read<int>(snapshot, "RemainingTokens"));
        Assert.Equal(
            "exact_input_plus_reserved_output",
            Read<string>(snapshot, "LastUsageSource"));
    }

    [Fact]
    public void Bug137_FailedAdmittedCallIsPessimisticallyChargedAndRetrySharesLedger()
    {
        var budget = CreateBudget(100, 1_000, 20, 200, () => 0);
        var first = Reserve(budget, 40, 20, terminal: false);
        Fail(budget, Read<long>(first, "ReservationId"));
        var retry = Reserve(budget, 21, 0, terminal: false);

        var snapshot = GetSnapshot(budget);
        Assert.Equal(60, Read<int>(snapshot, "ChargedTokens"));
        Assert.False(Read<bool>(retry, "Admitted"));
        Assert.Equal("terminal_budget_only", Read<string>(retry, "Reason"));
        Assert.Equal(1, Read<int>(snapshot, "FailedCalls"));
    }

    [Fact]
    public async Task Bug137_ConcurrentReservationsCannotDoubleSpendRemainingTokens()
    {
        var budget = CreateBudget(100, 1_000, 0, 0, () => 0);
        using var gate = new ManualResetEventSlim(false);
        var first = Task.Run(() =>
        {
            gate.Wait();
            return Reserve(budget, 60, 0, terminal: false);
        });
        var second = Task.Run(() =>
        {
            gate.Wait();
            return Reserve(budget, 60, 0, terminal: false);
        });

        gate.Set();
        var admissions = await Task.WhenAll(first, second);

        Assert.Equal(1, admissions.Count(admission =>
            Read<bool>(admission, "Admitted")));
        Assert.Equal(60, Read<int>(GetSnapshot(budget), "ReservedTokens"));
    }

    [Fact]
    public void Bug137_MonotonicDeadlineProtectsTerminalTimeAndBlocksAtDeadline()
    {
        long elapsed = 799;
        var budget = CreateBudget(100, 1_000, 20, 200, () => elapsed);

        var normalBeforeReserve = Reserve(budget, 1, 0, terminal: false);
        elapsed = 800;
        var normalInsideReserve = Reserve(budget, 1, 0, terminal: false);
        var terminalInsideReserve = Reserve(budget, 1, 0, terminal: true);
        elapsed = 1_000;
        var terminalAtDeadline = Reserve(budget, 1, 0, terminal: true);

        Assert.True(Read<bool>(normalBeforeReserve, "Admitted"));
        Assert.False(Read<bool>(normalInsideReserve, "Admitted"));
        Assert.Equal("terminal_budget_only", Read<string>(normalInsideReserve, "Reason"));
        Assert.True(Read<bool>(terminalInsideReserve, "Admitted"));
        Assert.False(Read<bool>(terminalAtDeadline, "Admitted"));
        Assert.Equal("time_budget_exhausted", Read<string>(terminalAtDeadline, "Reason"));
    }

    [Fact]
    public void Bug137_TerminalReservationCanUseProtectedTokensButExplorationCannot()
    {
        var budget = CreateBudget(100, 1_000, 20, 200, () => 0);
        var main = Reserve(budget, 80, 0, terminal: false);
        var exploration = Reserve(budget, 1, 0, terminal: false);
        var terminal = Reserve(budget, 20, 0, terminal: true);

        Assert.True(Read<bool>(main, "Admitted"));
        Assert.False(Read<bool>(exploration, "Admitted"));
        Assert.True(Read<bool>(terminal, "Admitted"));
        Assert.Equal(100, Read<int>(GetSnapshot(budget), "ReservedTokens"));
    }

    [Fact]
    public void Bug137_AsyncScopesNestRestoreAndDoNotLeakAcrossTurns()
    {
        var contextType = RequireType(ContextTypeName);
        var current = RequireProperty(contextType, "Current", BindingFlags.Static);
        var push = RequireMethod(contextType, "Push", BindingFlags.Static, parameterCount: 1);
        var parent = CreateBudget(100, 1_000, 20, 200, () => 0);
        var child = CreateBudget(200, 2_000, 40, 400, () => 0);

        Assert.Null(current.GetValue(null));
        using ((IDisposable)push.Invoke(null, new[] { parent })!)
        {
            Assert.Same(parent, current.GetValue(null));
            using ((IDisposable)push.Invoke(null, new[] { child })!)
                Assert.Same(child, current.GetValue(null));
            Assert.Same(parent, current.GetValue(null));
        }
        Assert.Null(current.GetValue(null));
    }

    [Fact]
    public void Bug137_ProviderCountsNativeAndStructuredCompletionsThroughSameContext()
    {
        var source = ReadProductSource(
            "ToolAgent", "OpenAiCompatibleLlmProvider.cs");

        Assert.Contains(
            "SourceBackedLlmCumulativeBudgetContext.ExecuteAsync",
            source,
            StringComparison.Ordinal);
        Assert.True(
            CountOccurrences(
                source,
                "SourceBackedLlmCumulativeBudgetContext.ExecuteAsync") >= 2,
            "Native and structured completion paths must both pass through the ledger.");
    }

    [Fact]
    public void Bug137_RunPipelineOpensOneBudgetScopeForTheWholeNativeTurn()
    {
        var source = ReadProductSource(
            "ToolAgent", "ToolAgentOrchestrator.RunPipeline.cs");

        Assert.Contains(
            "SourceBackedLlmCumulativeBudgetContext.Push",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "MaximumCumulativeLlmTokens",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new SourceBackedLlmCumulativeBudget",
            ReadProductSource("ToolAgent", "ToolAgentOrchestrator.RouterCore.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Bug137_RouterRethrowsTypedBudgetStopInsteadOfFallingBack()
    {
        var source = ReadProductSource(
            "ToolAgent", "ToolAgentOrchestrator.RouterCore.cs");

        Assert.Contains(
            "catch (SourceBackedLlmBudgetExceededException)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "router.native.cumulative_budget_stopped",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Bug137_RunnerHasMechanicalCumulativeFinalizationTransition()
    {
        var source = ReadProductSource(
            "ToolAgent", "SourceBackedRag", "SourceBackedAgentV2Runner.cs");
        var partial = ReadProductSource(
            "ToolAgent", "SourceBackedRag", "SourceBackedAgentCumulativeBudgetTransition.cs");

        Assert.Contains(
            "TryActivateCumulativeBudgetFinalization",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "terminal_budget_only",
            partial,
            StringComparison.Ordinal);
        Assert.Contains(
            "BuildSemanticYieldTerminalDecisionTools",
            partial,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "selectedEvidenceIds = bundle.Items",
            partial,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bug137_WriterReviewAndRepairCannotCreateIndependentLedgers()
    {
        var root = Path.Combine(
            FindRepoRoot(),
            "client",
            "SAAIA.Client.WinUI",
            "ToolAgent",
            "SourceBackedRag");
        var sources = Directory.EnumerateFiles(root, "*.cs")
            .Where(path => Path.GetFileName(path).Contains(
                "Writer",
                StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).Contains(
                    "Review",
                    StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).Contains(
                    "Repair",
                    StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText)
            .ToArray();

        Assert.NotEmpty(sources);
        Assert.DoesNotContain(
            sources,
            source => source.Contains(
                "new SourceBackedLlmCumulativeBudget",
                StringComparison.Ordinal));
        Assert.Contains(
            "same_scope_writer_review_repair",
            ReadProductSource(
                "ToolAgent", "SourceBackedRag", "SourceBackedLlmCumulativeBudget.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Bug137_TracesExposeBudgetWithoutPromptOrEvidenceContent()
    {
        var source = ReadProductSource(
            "ToolAgent", "SourceBackedRag", "SourceBackedLlmCumulativeBudget.cs");

        foreach (var field in new[]
                 {
                     "maximum_tokens",
                     "charged_tokens",
                     "reserved_tokens",
                     "remaining_tokens",
                     "remaining_ms",
                     "admission_reason",
                     "call_class"
                 })
        {
            Assert.Contains(field, source, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("evidence_text", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt_text", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bug137_ExistingTerminalContractKeepsLlmAsDecisionOwner()
    {
        var source = ReadProductSource(
            "ToolAgent", "SourceBackedRag", "SourceBackedAgentSemanticYieldTerminalDecision.cs");

        Assert.Contains(
            "decision_source\", \"llm_orchestrator",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "clarification",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "insufficiency",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Bug137_ProductContractIsDomainNeutral()
    {
        var files = new[]
        {
            ReadProductSource("Services", "RagChatAgent.cs"),
            ReadProductSource("ToolAgent", "ToolAgentOrchestrator.RunPipeline.cs"),
            ReadProductSource("ToolAgent", "ToolAgentOrchestrator.RouterCore.cs"),
            ReadProductSource("ToolAgent", "SourceBackedRag", "SourceBackedAgentV2Options.cs"),
            ReadOptionalProductSource("ToolAgent", "SourceBackedRag", "SourceBackedLlmCumulativeBudget.cs"),
            ReadOptionalProductSource("ToolAgent", "SourceBackedRag", "SourceBackedAgentCumulativeBudgetTransition.cs")
        };
        var product = string.Join('\n', files);

        foreach (var forbidden in new[]
                 {
                     "ABB 266",
                     "HART",
                     "Easy Setup",
                     "Integrated LCD display available"
                 })
        {
            Assert.DoesNotContain(
                forbidden,
                product,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private static object CreateBudget(
        int maximumTokens,
        long maximumElapsedMilliseconds,
        int terminalReserveTokens,
        long terminalReserveMilliseconds,
        Func<long> elapsedMilliseconds)
    {
        var type = RequireType(BudgetTypeName);
        var constructor = type.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(candidate => candidate.GetParameters().Length == 5);
        Assert.NotNull(constructor);
        return constructor!.Invoke(new object[]
        {
            maximumTokens,
            maximumElapsedMilliseconds,
            terminalReserveTokens,
            terminalReserveMilliseconds,
            elapsedMilliseconds
        });
    }

    private static object Reserve(
        object budget,
        int inputTokens,
        int maximumOutputTokens,
        bool terminal)
        => RequireMethod(
                budget.GetType(),
                "TryReserve",
                BindingFlags.Instance,
                parameterCount: 3)
            .Invoke(budget, new object[]
            {
                inputTokens,
                maximumOutputTokens,
                terminal
            })!;

    private static void Complete(
        object budget,
        long reservationId,
        int? promptTokens,
        int? completionTokens)
        => RequireMethod(
                budget.GetType(),
                "Complete",
                BindingFlags.Instance,
                parameterCount: 3)
            .Invoke(budget, new object?[]
            {
                reservationId,
                promptTokens,
                completionTokens
            });

    private static void Fail(object budget, long reservationId)
        => RequireMethod(
                budget.GetType(),
                "Fail",
                BindingFlags.Instance,
                parameterCount: 1)
            .Invoke(budget, new object[] { reservationId });

    private static object GetSnapshot(object budget)
        => RequireMethod(
                budget.GetType(),
                "GetSnapshot",
                BindingFlags.Instance,
                parameterCount: 0)
            .Invoke(budget, null)!;

    private static T Read<T>(object value, string propertyName)
    {
        var property = RequireProperty(
            value.GetType(),
            propertyName,
            BindingFlags.Instance);
        return (T)property.GetValue(value)!;
    }

    private static Type RequireType(string name)
    {
        var type = FindType(name);
        Assert.NotNull(type);
        return type!;
    }

    private static Type? FindType(string name)
        => typeof(SourceBackedAgentV2Runner).Assembly.GetType(
            name,
            throwOnError: false,
            ignoreCase: false);

    private static MethodInfo RequireMethod(
        Type type,
        string name,
        BindingFlags scope,
        int parameterCount)
    {
        var method = type.GetMethods(
                scope | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.Ordinal)
                && candidate.GetParameters().Length == parameterCount);
        Assert.NotNull(method);
        return method!;
    }

    private static PropertyInfo RequireProperty(
        Type type,
        string name,
        BindingFlags scope)
    {
        var property = type.GetProperty(
            name,
            scope | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        return property!;
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static string ReadProductSource(params string[] relativeParts)
    {
        var path = Path.Combine(
            new[]
            {
                FindRepoRoot(),
                "client",
                "SAAIA.Client.WinUI"
            }.Concat(relativeParts).ToArray());
        Assert.True(File.Exists(path), $"Expected product source missing: {path}");
        return File.ReadAllText(path);
    }

    private static string ReadOptionalProductSource(params string[] relativeParts)
    {
        var path = Path.Combine(
            new[]
            {
                FindRepoRoot(),
                "client",
                "SAAIA.Client.WinUI"
            }.Concat(relativeParts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "client",
                    "SAAIA.Client.WinUI",
                    "SAAIA.Client.WinUI.csproj")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
