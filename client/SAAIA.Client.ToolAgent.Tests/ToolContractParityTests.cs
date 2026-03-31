using System;
using System.Linq;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ToolContractParityTests
{
    [Fact]
    public void Manifest_and_runtime_catalogs_stay_in_sync()
    {
        var report = ToolManifest.ValidateRuntimeCatalog(ToolAgentOrchestrator.GetExecutableToolNamesForTests());
        Assert.True(report.IsValid, report.FormatErrorMessage());
    }

    [Fact]
    public void Runtime_consistency_guard_does_not_throw()
    {
        var ex = Record.Exception(ToolAgentOrchestrator.EnsureToolContractConsistency);
        Assert.Null(ex);
    }

    [Fact]
    public void Rag_categories_is_not_available_anymore()
    {
        Assert.False(ToolManifest.KnownToolNames.Contains("rag.categories"));
        Assert.DoesNotContain("rag.categories", ToolAgentOrchestrator.GetExecutableToolNamesForTests());
    }

    [Fact]
    public void Admin_ingestion_reindex_is_not_available_as_a_runtime_llm_tool_anymore()
    {
        Assert.False(ToolManifest.KnownToolNames.Contains("admin.ingestion.reindex"));
        Assert.DoesNotContain("admin.ingestion.reindex", ToolAgentOrchestrator.GetExecutableToolNamesForTests());
    }

    [Theory]
    [InlineData("documents.categories")]
    [InlineData("support.bundle")]
    [InlineData("diagnostic.performance")]
    [InlineData("summary.get")]
    [InlineData("summary.exists")]
    [InlineData("summary.search")]
    [InlineData("summary.status.count")]
    [InlineData("summary.status.list")]
    [InlineData("summary.present.count")]
    [InlineData("summary.present.list")]
    [InlineData("admin.summary.missing")]
    [InlineData("admin.summary.request")]
    [InlineData("admin.summary.submit")]
    [InlineData("admin.summary.status")]
    [InlineData("admin.summary.delete")]
    [InlineData("admin.summary.generate")]
    [InlineData("documents.empty_count")]
    [InlineData("documents.empty_list")]
    public void Sensitive_tools_are_declared_and_executable(string toolName)
    {
        Assert.True(ToolManifest.KnownToolNames.Contains(toolName));
        Assert.Contains(toolName, ToolAgentOrchestrator.GetExecutableToolNamesForTests());
    }

    [Fact]
    public void Admin_tools_are_also_known_tools()
    {
        var unknownAdmins = ToolManifest.AdminToolNames
            .Where(x => !ToolManifest.KnownToolNames.Contains(x))
            .ToArray();

        Assert.True(unknownAdmins.Length == 0, "Unknown admin tools: " + string.Join(", ", unknownAdmins));
    }
}
