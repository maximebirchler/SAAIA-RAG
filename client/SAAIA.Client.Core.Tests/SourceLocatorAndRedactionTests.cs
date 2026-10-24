using SAAIA.Client.Core.Diagnostics;
using SAAIA.Client.Core.Sources;
using Xunit;

namespace SAAIA.Client.Core.Tests;

public sealed class SourceLocatorAndRedactionTests
{
    [Fact]
    public void Link_uses_same_canonical_identity_as_card_when_unambiguous()
    {
        var registry = new SourceLocatorRegistry();
        var card = new SourceLocator("folder/book.pdf", 4, "hash", "revision", "chunk", "anchor",
            "card", true, "trace");
        registry.Register(card);
        Assert.Equal(card, registry.Resolve("folder\\book.pdf", 4));
    }

    [Fact]
    public void Link_does_not_guess_between_revisions_or_claim_exact_identity_when_incomplete()
    {
        var registry = new SourceLocatorRegistry();
        registry.Register(new("book.pdf", 4, "old", "r1", RequireExactSourceHash: true));
        registry.Register(new("book.pdf", 4, "new", "r2", RequireExactSourceHash: true));
        var locator = registry.Resolve("book.pdf", 4);
        Assert.Null(locator.SourceHash);
        Assert.Equal("blocked_ambiguous_identity", locator.IdentityAssurance);
        Assert.True(locator.RequireExactSourceHash);
        Assert.Equal("ambiguous_indexed_revision", locator.ResolutionIssue);
        Assert.Equal("identity_unverified", registry.Resolve("unknown.pdf", 1).IdentityAssurance);
    }

    [Fact]
    public void Registry_is_bounded()
    {
        var registry = new SourceLocatorRegistry(capacity: 1);
        registry.Register(new("old.pdf", 1, "old"));
        registry.Register(new("new.pdf", 1, "new"));
        Assert.Null(registry.Resolve("old.pdf", 1).SourceHash);
        Assert.Equal("new", registry.Resolve("new.pdf", 1).SourceHash);
    }

    [Fact]
    public void Link_to_an_interior_page_keeps_the_card_revision_and_requests_the_clicked_page()
    {
        var registry = new SourceLocatorRegistry();
        registry.Register(new("book.pdf", 3, "hash", "revision", RequireExactSourceHash: true, PageEnd: 6));
        var locator = registry.Resolve("book.pdf", 5);
        Assert.Equal(5, locator.Page);
        Assert.Equal("revision", locator.RevisionId);
        Assert.True(locator.RequireExactSourceHash);
    }

    [Fact]
    public void Metadata_export_redacts_legacy_content_and_secrets_but_keeps_numeric_diagnosis()
    {
        const string json = """{"apiKey":"credential","answer":"private answer","queries":["private question"],"source":{"docPath":"secret.pdf"},"routerMs":12,"evidenceCount":3}""";
        var result = SupportDiagnosticRedactor.RedactJson(json);
        Assert.DoesNotContain("credential", result);
        Assert.DoesNotContain("private", result);
        Assert.DoesNotContain("secret.pdf", result);
        Assert.Contains("12", result);
        Assert.Contains("evidenceCount", result);
    }

    [Fact]
    public void Explicit_content_mode_still_redacts_credentials()
    {
        var result = SupportDiagnosticRedactor.RedactJson("""{"answer":"allowed text","password":"never export"}""", true);
        Assert.Contains("allowed text", result);
        Assert.DoesNotContain("never export", result);
    }

    [Fact]
    public void Text_export_redacts_credentials_even_when_not_in_the_process_environment()
    {
        var result = SupportDiagnosticRedactor.RedactText("API_KEY=credential\n  - POSTGRES_PASSWORD: private-password\nAuthorization: Bearer abcdef-token\nmodel=available");
        Assert.DoesNotContain("credential", result);
        Assert.DoesNotContain("private-password", result);
        Assert.DoesNotContain("abcdef-token", result);
        Assert.Contains("model=available", result);
    }

    [Fact]
    public void Metadata_export_keeps_measured_counts_and_removes_url_credentials_and_query()
    {
        var result = SupportDiagnosticRedactor.RedactJson("""{"answerChars":12,"hasApiKey":true,"url":"https://user:password@example.test/api?api_key=private"}""");
        Assert.Contains("12", result);
        Assert.Contains("true", result);
        Assert.DoesNotContain("user", result);
        Assert.DoesNotContain("password", result);
        Assert.DoesNotContain("private", result);
    }

    [Fact]
    public void Alternate_document_keys_numeric_credentials_and_generic_string_arrays_are_redacted()
    {
        var result = SupportDiagnosticRedactor.RedactJson("""{"documentPath":"private.pdf","documentName":"private name","password":1234,"notes":["private text"],"items":["private value"],"inputTokens":1234,"hasApiKey":true}""");
        Assert.DoesNotContain("private", result);
        using var document = System.Text.Json.JsonDocument.Parse(result);
        Assert.Equal("[REDACTED]", document.RootElement.GetProperty("password").GetString());
        Assert.Equal(1234, document.RootElement.GetProperty("inputTokens").GetInt32());
        Assert.True(document.RootElement.GetProperty("hasApiKey").GetBoolean());
    }
}
