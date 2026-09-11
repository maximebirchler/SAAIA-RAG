using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SourceBackedFocusedDocumentMemoryTests
{
    [Fact]
    public void Unique_verified_document_becomes_focused_even_when_multiple_pages_are_cited()
    {
        var sources = new[]
        {
            Source("doc-1", "Technical/Data sheets/FIT.pdf", "FIT.pdf", 1),
            Source("doc-1", "Technical/Data sheets/FIT.pdf", "FIT.pdf", 2)
        };

        var focused = ToolAgentOrchestrator.TryBuildUniqueSourceBackedFocusedDocument(sources);

        Assert.NotNull(focused);
        Assert.Equal("doc-1", focused.DocId);
        Assert.Equal("Technical/Data sheets/FIT.pdf", focused.DocPath);
        Assert.Equal("FIT.pdf", focused.DocName);
        Assert.Equal("Technical/Data sheets", focused.CategoryPath);
    }

    [Fact]
    public void Multiple_verified_documents_do_not_create_an_arbitrary_focus()
    {
        var sources = new[]
        {
            Source("doc-1", "Technical/one.pdf", "one.pdf", 1),
            Source("doc-2", "Technical/two.pdf", "two.pdf", 1)
        };

        var focused = ToolAgentOrchestrator.TryBuildUniqueSourceBackedFocusedDocument(sources);

        Assert.Null(focused);
    }

    private static ToolMemory.SourceRef Source(
        string docId,
        string docPath,
        string docName,
        int page)
        => new()
        {
            DocId = docId,
            DocPath = docPath,
            DocName = docName,
            PageStart = page,
            PageEnd = page,
            CategoryPath = "Technical/Data sheets",
            Label = docName
        };
}
