using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions", 0, true)]
    [InlineData("responses", 0, true)]
    [InlineData("chat-completions", 1, true)]
    [InlineData("responses", 1, true)]
    [InlineData("chat-completions", 0, false)]
    [InlineData("responses", 0, false)]
    [InlineData("chat-completions", 1, false)]
    [InlineData("responses", 1, false)]
    public async Task NativeProjectionCursor_every_visible_line_uses_the_admitted_prefix_cursor_without_changing_reader_data(
        string protocol, int offset, bool partial)
    {
        // The full documentary budget can now admit both old short lines.
        // Long indivisible literals keep this case about actual pagination,
        // while the nonpartial case retains its original small complete view.
        var padding = partial ? new string('λ', 4_000) : string.Empty;
        var raw = $"Literal zero 😀.{padding}\r\nLiteral one é.{padding}\r\nLiteral two exact.{padding}\r\n";
        var rawSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        var document = new CanonicalDocument
        {
            DocumentId = Guid.Parse("11111111-1111-4111-8111-111111111111"),
            RevisionId = Guid.Parse("22222222-2222-4222-8222-222222222222"),
            ManifestSha256 = new string('d', 64), Source = new() { Sha256 = new string('a', 64) },
            Pages = [new() { PageNumber = 1, Blocks = [new()
            {
                BlockId = "native-audit", BlockType = "native_layout_recovery", Text = new() { Raw = raw, RawSha256 = rawSha }
            }] }]
        };
        var window = AdvancedAnalysisNativeObservationWindows.Raw(document, 1, "native-audit", rawSha, offset, 2, 100_000);
        var codes = partial ? Enumerable.Range(0, 12).Select(index => "warning_" + index + "_" + new string('x', 92)).ToArray()
            : new[] { "canonical_native_audit_literal_not_primary" };
        var evidence = window.Lines.Select(line => new AdvancedAnalysisResolvedEvidence(new()
        {
            EvidenceId = "N" + line.Source.LineOrdinal, DocId = document.DocumentId.ToString("D"),
            RevisionId = document.RevisionId.ToString("D"), SourceHash = document.Source.Sha256,
            FileName = "menus.pdf", DocPath = "Documents/menus.pdf", PageStart = 1, PageEnd = 1, NativeObservation = line.Source
        }, line.Text)
        {
            SourceOverview = new(1, 1, 3), SourceQuality = new(null, null, codes),
            NativeObservation = new(line.Source, 3, offset, 2, window.NextOffset, codes)
        }).ToArray();
        var body = await CaptureNativeBudgetAsync(protocol, evidence, rawSha, nativeOffset: offset, nativeLimit: 2,
            reserveCurrentNativeTurn: true);
        var user = WorkspaceUser(body);
        var items = user.GetProperty("evidence").EnumerateArray().ToArray();
        Assert.Equal(partial ? 1 : 2, items.Length);
        var view = Assert.Single(user.GetProperty("alternativeNativeReadings").GetProperty("views").EnumerateArray());
        Assert.Equal(partial ? "partial_model_budget" : "observed", view.GetProperty("status").GetString());
        var next = partial ? offset + 1 : offset + 2 < 3 ? offset + 2 : (int?)null;
        Assert.Equal(next, view.GetProperty("nextOffset").ValueKind == JsonValueKind.Null ? null : view.GetProperty("nextOffset").GetInt32());
        foreach (var item in items)
        {
            var native = item.GetProperty("nativeObservation");
            Assert.Equal(next, native.GetProperty("nextLineOffset").ValueKind == JsonValueKind.Null
                ? null : native.GetProperty("nextLineOffset").GetInt32());
            var ordinal = native.GetProperty("reference").GetProperty("lineOrdinal").GetInt32();
            var original = evidence.Single(value => value.NativeObservation!.Reference.LineOrdinal == ordinal);
            Assert.Equal(original.Content, item.GetProperty("content").GetString());
            Assert.False(item.GetProperty("contentTruncated").GetBoolean());
            Assert.Equal(original.Reference.EvidenceId, item.GetProperty("evidenceId").GetString());
            Assert.Equal(original.NativeObservation!.Reference.LineTextSha256, native.GetProperty("reference").GetProperty("lineTextSha256").GetString());
            Assert.Equal(codes, native.GetProperty("qualitySignals").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(codes, item.GetProperty("sourceQuality").GetProperty("signals").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(window.NextOffset, original.NativeObservation.NextLineOffset);
        }
        using var tool = NativeBudgetToolOutput(body, protocol);
        Assert.True(tool.RootElement.GetProperty("alternativeNativeReadings").GetProperty("views")[0].TryGetProperty("metadataReference", out _));
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(body), 1, 14_000);
    }
}
