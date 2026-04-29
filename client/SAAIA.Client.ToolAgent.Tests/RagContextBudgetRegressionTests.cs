using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class RagContextBudgetRegressionTests
{
    [Fact]
    public void NormalizeRagHits_compacts_already_normalized_hits_before_writer_prompt()
    {
        var longExcerpt = new string('x', 1000);
        var payload = JsonSerializer.Serialize(new
        {
            hits = Enumerable.Range(1, 12).Select(i => new
            {
                docPath = $"Manual{i}.pdf",
                docName = $"Manual {i}",
                pageStart = i,
                pageEnd = i,
                excerpt = longExcerpt,
                score = 0.9
            })
        });

        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var hits = normalized.GetProperty("hits").EnumerateArray().ToList();
        var firstHit = normalized.GetProperty("hits").EnumerateArray().First();
        var excerpt = firstHit.GetProperty("excerpt").GetString();

        Assert.Equal(8, hits.Count);
        Assert.Equal("Manual1.pdf", firstHit.GetProperty("docPath").GetString());
        Assert.Equal(1, firstHit.GetProperty("pageStart").GetInt32());
        Assert.NotNull(excerpt);
        Assert.True(excerpt!.Length <= 243);
        Assert.EndsWith("...", excerpt);
    }

    [Fact]
    public void SerializeTail_compacts_long_messages_before_writer_prompt()
    {
        var history = Enumerable.Range(1, 4)
            .Select(i => (i % 2 == 0 ? "assistant" : "user", new string((char)('a' + i), 1200)))
            .ToList();

        var json = ToolAgentOrchestrator.SerializeTailForTests(history, maxTurns: 3);
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.EnumerateArray().ToList();

        Assert.Equal(3, items.Count);
        Assert.All(items, item =>
        {
            var content = item.GetProperty("content").GetString();
            Assert.NotNull(content);
            Assert.True(content!.Length <= 423);
            Assert.EndsWith("...", content);
        });
    }
}
