using System;
using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ToolFailureDeterminismTests
{
    [Fact]
    public void Admin_required_error_returns_a_deterministic_answer()
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod("TryBuildToolFailureAnswer", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var plan = new RouterPlan { Intent = "admin.catalog.health", Language = "fr" };
        var results = new ToolResults();
        using var doc = JsonDocument.Parse("{" + "\"error\":\"admin_required\"}" );
        results.Items.Add(new ToolResults.Item { ToolName = "admin.catalog.health", Result = doc.RootElement.Clone() });

        var answer = method!.Invoke(null, new object[] { plan, results, "fr" }) as string;

        Assert.False(string.IsNullOrWhiteSpace(answer));
        Assert.Contains("admin", answer!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generic_tool_failure_returns_a_deterministic_answer()
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod("TryBuildToolFailureAnswer", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var plan = new RouterPlan { Intent = "inventory.list", Language = "fr" };
        var results = new ToolResults();
        using var doc = JsonDocument.Parse("{" + "\"error\":\"tool_failed\"}" );
        results.Items.Add(new ToolResults.Item { ToolName = "documents.search", Result = doc.RootElement.Clone() });

        var answer = method!.Invoke(null, new object[] { plan, results, "fr" }) as string;

        Assert.False(string.IsNullOrWhiteSpace(answer));
    }
}
