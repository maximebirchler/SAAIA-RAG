
using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class CatalogTransitionRegressionTests
{
    [Fact]
    public void ParsePresentedCategories_supports_catalog_value_shape_and_preserves_category_ref()
    {
        using var doc = JsonDocument.Parse("""
        {
          "value": [
            {
              "categoryRef": "cat_003",
              "categoryPath": "Programmation",
              "canonicalName": "Programmation",
              "displayOrder": 3,
              "documentCount": 1,
              "aliases": ["Programming"]
            }
          ]
        }
        """);

        var method = typeof(ToolAgentOrchestrator).GetMethod("ParsePresentedCategories", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var categories = (System.Collections.Generic.List<ToolMemory.CategorySnapshot>)method!.Invoke(null, new object[] { doc.RootElement })!;
        var category = Assert.Single(categories);
        Assert.Equal("cat_003", category.CategoryRef);
        Assert.Equal("Programmation", category.CategoryPath);
        Assert.Contains("Programming", category.Aliases);
    }

    [Fact]
    public void Summary_status_snapshot_accepts_catalog_value_shape()
    {
        using var doc = JsonDocument.Parse("""
        {
          "value": [
            {
              "docId": "abc",
              "docPath": "ATEX/test.pdf",
              "canonicalName": "test.pdf",
              "categoryCanonicalName": "ATEX",
              "summaryState": "missing"
            }
          ],
          "totals": {
            "total": 1,
            "missingStored": 1,
            "staleStored": 0,
            "profileMissing": 1
          }
        }
        """);

        var mem = new ToolMemory();
        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: mem);
        var method = typeof(ToolAgentOrchestrator).GetMethod("UpdateSummaryStatusSnapshotFromJson", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        method!.Invoke(sut, new object[] { doc.RootElement });

        Assert.NotNull(mem.LastSummaryStatusSnapshot);
        Assert.Equal(1, mem.LastSummaryStatusSnapshot!.Total);
        Assert.Equal(1, mem.LastSummaryStatusSnapshot.ProfileMissing);
        var item = Assert.Single(mem.LastSummaryStatusSnapshot.Items);
        Assert.Equal("ATEX", item.Category);
    }

    [Fact]
    public void Summary_status_snapshot_preserves_profile_missing_metadata()
    {
        using var doc = JsonDocument.Parse("""
        {
          "value": [
            {
              "docId": "abc",
              "docPath": "Generic/profile.pdf",
              "canonicalName": "profile.pdf",
              "categoryCanonicalName": "Generic",
              "summaryState": "fresh",
              "capabilityBProfileState": "missing",
              "capabilityBHasBackofficeProfile": false,
              "capabilityBReasons": ["profile_missing"]
            }
          ],
          "totals": {
            "total": 1,
            "missingStored": 0,
            "staleStored": 0,
            "profileMissing": 1
          }
        }
        """);

        var mem = new ToolMemory();
        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: mem);
        var method = typeof(ToolAgentOrchestrator).GetMethod("UpdateSummaryStatusSnapshotFromJson", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        method!.Invoke(sut, new object[] { doc.RootElement });

        Assert.NotNull(mem.LastSummaryStatusSnapshot);
        Assert.Equal(1, mem.LastSummaryStatusSnapshot!.ProfileMissing);
        var item = Assert.Single(mem.LastSummaryStatusSnapshot.Items);
        Assert.Equal("fresh", item.SummaryState);
        Assert.Equal("missing", item.CapabilityBProfileState);
        Assert.False(item.CapabilityBHasBackofficeProfile);
        Assert.Contains("profile_missing", item.CapabilityBReasons);

        var rendered = ToolAgentOrchestrator.RenderDeterministicInventoryFromData("summary_status_list", doc.RootElement, "en");
        Assert.Contains("[missing server profile]", rendered);
    }

    [Fact]
    public void Summary_status_snapshot_preserves_capability_b_governance_metadata()
    {
        using var doc = JsonDocument.Parse("""
        {
          "value": [
            {
              "docId": "abc",
              "docPath": "Generic/governance.pdf",
              "canonicalName": "governance.pdf",
              "categoryCanonicalName": "Generic",
              "summaryState": "stale",
              "hasActiveSummaryJob": true,
              "activeSummaryJobId": "job-1",
              "activeSummaryJobStatus": "running",
              "activeSummaryJobExecutionMode": "server_backoffice",
              "activeSummaryJobRuntimeCapabilityKey": "capability_b",
              "activeSummaryJobRuntimeCapabilityStatus": "qualified",
              "activeSummaryJobEnqueueSource": "capability_b",
              "activeSummaryJobCampaignId": "campaign-1",
              "capabilityBReadyToEnqueue": true,
              "capabilityBRecommendedAction": "enqueue_profile_refresh",
              "capabilityBPolicyBlocked": false,
              "capabilityBPriorityScore": 42.5,
              "capabilityBLastJobStatus": "failed",
              "capabilityBLastJobError": "timeout",
              "capabilityBProfileState": "stale",
              "capabilityBHasBackofficeProfile": true,
              "capabilityBReasons": ["summary_stale"]
            }
          ],
          "totals": { "total": 1, "staleStored": 1 }
        }
        """);

        var mem = new ToolMemory();
        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: mem);
        var updateMethod = typeof(ToolAgentOrchestrator).GetMethod("UpdateSummaryStatusSnapshotFromJson", BindingFlags.NonPublic | BindingFlags.Instance);
        var createMethod = typeof(ToolAgentOrchestrator).GetMethod("CreateSummaryStatusJson", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(updateMethod);
        Assert.NotNull(createMethod);

        updateMethod!.Invoke(sut, new object[] { doc.RootElement });

        var item = Assert.Single(mem.LastSummaryStatusSnapshot!.Items);
        Assert.True(item.HasActiveSummaryJob);
        Assert.Equal("job-1", item.ActiveSummaryJobId);
        Assert.Equal("running", item.ActiveSummaryJobStatus);
        Assert.Equal("server_backoffice", item.ActiveSummaryJobExecutionMode);
        Assert.Equal("capability_b", item.ActiveSummaryJobRuntimeCapabilityKey);
        Assert.True(item.CapabilityBReadyToEnqueue);
        Assert.Equal("enqueue_profile_refresh", item.CapabilityBRecommendedAction);
        Assert.False(item.CapabilityBPolicyBlocked);
        Assert.Equal(42.5, item.CapabilityBPriorityScore);
        Assert.Equal("failed", item.CapabilityBLastJobStatus);
        Assert.Equal("timeout", item.CapabilityBLastJobError);

        var replayJson = (JsonElement)createMethod!.Invoke(null, new object[] { mem.LastSummaryStatusSnapshot })!;
        var replayItem = Assert.Single(replayJson.GetProperty("items").EnumerateArray());
        Assert.True(replayItem.GetProperty("hasActiveSummaryJob").GetBoolean());
        Assert.Equal("enqueue_profile_refresh", replayItem.GetProperty("capabilityBRecommendedAction").GetString());
        Assert.Equal(42.5, replayItem.GetProperty("capabilityBPriorityScore").GetDouble());

        var rendered = ToolAgentOrchestrator.RenderDeterministicInventoryFromData("summary_status_list", replayJson, "en");
        Assert.Contains("[summary to update]", rendered);
        Assert.Contains("[processing in progress running]", rendered);
        Assert.Contains("[server summary ready to prepare]", rendered);
        Assert.Contains("[last processing issue (failed); details in logs]", rendered);
        Assert.Contains("[priority 42.5]", rendered);
        Assert.DoesNotContain("[stale]", rendered);
        Assert.DoesNotContain("Capability B", rendered);
        Assert.DoesNotContain("enqueue_profile_refresh", rendered);
        Assert.DoesNotContain("timeout", rendered);
    }
}
