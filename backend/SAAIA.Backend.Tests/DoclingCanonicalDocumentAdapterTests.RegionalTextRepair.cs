using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

public sealed partial class DoclingCanonicalDocumentAdapterTests
{
    [Fact]
    public void Regional_text_repair_keeps_raw_and_both_source_readings_with_applied_flag()
    {
        var source = BuildDocument(); var item = PrepareRepair(source);
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var block = document.Pages[0].Blocks.Single(block => block.Text.Canonical == item.Text);
        Assert.Equal(item.OriginalText, block.Text.Raw);
        Assert.Contains("regional_text_ocr_repair_applied", block.QualityFlags);
        Assert.Contains("rapidocr", block.Provenance.Attributes["regionalTextRepair"]);
        Assert.Contains("tesseract", block.Provenance.Attributes["regionalTextRepair"]);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("source")]
    [InlineData("engine")]
    [InlineData("confidence")]
    public void Regional_text_repair_rejects_unbound_or_unproven_canonical_replacements(string tamper)
    {
        var source = BuildDocument(); var item = PrepareRepair(source);
        var proof = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.RegionalTextRepair!.Value.GetRawText())!;
        if (tamper == "hash") proof["replacement_text_sha256"] = JsonSerializer.SerializeToElement(new string('f', 64));
        if (tamper == "source") proof["source_ref"] = JsonSerializer.SerializeToElement("#/texts/other");
        if (tamper is "engine" or "confidence")
        {
            var observations = proof["observations"].EnumerateArray().Select(value => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(value.GetRawText())!).ToArray();
            if (tamper == "engine") observations[1]["engine"] = JsonSerializer.SerializeToElement("rapidocr");
            else observations[1]["minimum_confidence"] = JsonSerializer.SerializeToElement(0.1);
            proof["observations"] = JsonSerializer.SerializeToElement(observations);
        }
        item.RegionalTextRepair = JsonSerializer.SerializeToElement(proof);
        Assert.Throws<InvalidDataException>(() => DoclingCanonicalDocumentAdapter.Project(BuildContext(), source));
    }

    [Fact]
    public void Regional_text_repair_inconclusive_keeps_text_and_observations_without_an_applied_claim()
    {
        var source = BuildDocument(); var item = source.Texts[1];
        item.RegionalTextRepairAttempt = JsonSerializer.SerializeToElement(new
        {
            schema_version="saaia_text_region_repair_v1",source_ref=item.SelfRef,status="not_applied",
            reason="independent_readings_disagree",attempted=true,original_text=item.Text,source_provenance=item.Provenance,
            observations=new[] { new { engine="rapidocr", text="first reading" }, new { engine="tesseract", text="other reading" } }
        });
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var block = document.Pages[0].Blocks.Single(block => block.Text.Canonical == item.Text);
        Assert.Equal(item.OriginalText, block.Text.Raw);
        Assert.Contains("regional_text_ocr_repair_inconclusive", block.QualityFlags);
        Assert.DoesNotContain("regional_text_ocr_repair_applied", block.QualityFlags);
        Assert.Contains("independent_readings_disagree", block.Provenance.Attributes["regionalTextRepairAttempt"]);
    }

    [Fact]
    public void Regional_text_repair_rejects_inserted_negation_inside_reliable_source_suffix()
    {
        var source = BuildDocument(); var item = PrepareRepair(source);
        RewriteReplacement(item, "All installations maintain not positive shutdown state");
        Assert.Throws<InvalidDataException>(() => DoclingCanonicalDocumentAdapter.Project(BuildContext(), source));
    }

    [Theory]
    [InlineData("if")]
    [InlineData("or")]
    [InlineData("de")]
    [InlineData("un")]
    public void Regional_text_repair_cannot_drop_a_two_letter_word_inside_the_damaged_prefix(string word)
    {
        var source = BuildDocument(); var item = PrepareRepair(source);
        var original = word + " e a t   maintain positive shutdown state";
        var proof = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.RegionalTextRepair!.Value.GetRawText())!;
        proof["original_text"] = JsonSerializer.SerializeToElement(original);
        proof["original_text_sha256"] = JsonSerializer.SerializeToElement(Hash(original));
        proof["damage"] = JsonSerializer.SerializeToElement(new { candidate=true, fragmented_prefix_end=8, reliable_suffix_start=8 });
        item.OriginalText = original; item.RegionalTextRepair = JsonSerializer.SerializeToElement(proof);
        Assert.Throws<InvalidDataException>(() => DoclingCanonicalDocumentAdapter.Project(BuildContext(), source));
    }

    [Fact]
    public void Regional_text_repair_inconclusive_multi_region_item_remains_projectable_without_claiming_a_repair()
    {
        var source = BuildDocument(); var item = source.Texts[1];
        item.OriginalText = item.Text = "First source. Second source.";
        var box = item.Provenance[0].BoundingBox;
        item.Provenance =
        [
            new() { PageNumber=1, BoundingBox=box, CharacterSpan=[0,13] },
            new() { PageNumber=1, BoundingBox=box, CharacterSpan=[14,28] }
        ];
        item.RegionalTextRepairAttempt = JsonSerializer.SerializeToElement(new
        {
            schema_version="saaia_text_region_repair_v1",source_ref=item.SelfRef,status="not_applied",
            reason="source_geometry_invalid",attempted=false,original_text=item.Text,source_provenance=item.Provenance
        });
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var fragments = document.Pages[0].Blocks.Where(block => block.Text.Raw is "First source." or "Second source.").ToArray();
        Assert.Equal(2, fragments.Length);
        Assert.All(fragments, block =>
        {
            Assert.Contains("regional_text_ocr_repair_inconclusive", block.QualityFlags);
            Assert.DoesNotContain("regional_text_ocr_repair_applied", block.QualityFlags);
            Assert.Equal(block.Text.Raw, block.Text.Canonical);
        });
    }

    [Fact]
    public void Regional_text_repair_keeps_pre_repair_text_when_docling_orig_is_empty()
    {
        var source = BuildDocument(); var item = PrepareRepair(source);
        var expected = item.OriginalText;
        item.OriginalText = "";
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var block = document.Pages[0].Blocks.Single(block => block.Text.Canonical == item.Text);
        Assert.Equal(expected, block.Text.Raw);
        Assert.Equal(Hash(expected), block.Text.RawSha256);
    }

    [Fact]
    public void Regional_text_repair_manifest_counts_actual_work_without_relabeling_primary_ocr()
    {
        var response = BuildResponse();
        var item = PrepareRepair(response.Document.JsonContent!);
        response.Execution = new() { TextRepairSettings = DoclingRegionalTextExecutionTests.Settings(true) };
        response.TextRepairs = [JsonSerializer.SerializeToElement(new
        {
            schema_version="saaia_text_region_repair_v1",source_ref=item.SelfRef,status="applied",
            reason="independent_high_confidence_source_retaining_agreement",attempted=true
        })];
        response.TextRepairSummary = JsonSerializer.SerializeToElement(new
        {
            schema_version="saaia_text_region_repair_v1",candidate_count=1,attempted_count=1,applied_count=1,
            duration_ms=31,limit_max_regions=4,limit_total_timeout_seconds=60d
        });
        var options = new DocumentIntelligenceOptions { RegionalTextRepairEnabled = true };
        DoclingExecutionMetadataPolicy.Validate(options, response, false);
        var stages = DoclingIngestionStageManifestFactory.Create(options, new(), BuildRagOptions(), response,
            new(1, 2, 3, 4), "head-source-hash");
        var regional = Assert.Single(stages, stage => stage.StageId == "regional_text_ocr_repair");
        Assert.Equal("RapidOCR+Tesseract", regional.Engine);
        Assert.Contains("rapidocr=3.9.1", regional.EngineVersion);
        Assert.Contains("tesseract=5.3.0", regional.EngineVersion);
        Assert.Equal(31, regional.DurationMs);
        Assert.Equal("1", regional.Attributes["appliedCount"]);
        Assert.Equal("false", regional.Attributes["universalMultilingualValidation"]);
        Assert.Contains("\"enabled\":true", regional.Attributes["signedSettings"]);
        Assert.Contains("\"minimum_rapid_score\":0.98", regional.Attributes["observedSettings"]);
        Assert.Contains(item.SelfRef, regional.Attributes["observedRegionSummaries"]);
        Assert.Null(regional.ModelId); // Labels alone do not claim a globally immutable pair of models.
        Assert.Equal("RapidOCR", Assert.Single(stages, stage => stage.StageId == "docling_ocr").Engine);
        response.TextRepairSummary = JsonSerializer.SerializeToElement(new
        {
            schema_version="saaia_text_region_repair_v1",candidate_count=0,attempted_count=0,applied_count=0,
            duration_ms=31,limit_max_regions=4,limit_total_timeout_seconds=60d
        });
        Assert.Throws<InvalidDataException>(() => DoclingExecutionMetadataPolicy.Validate(options, response, false));
    }

    private static void RewriteReplacement(DoclingTextItem item, string replacement)
    {
        var proof = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.RegionalTextRepair!.Value.GetRawText())!;
        proof["replacement_text"] = JsonSerializer.SerializeToElement(replacement);
        proof["replacement_text_sha256"] = JsonSerializer.SerializeToElement(Hash(replacement));
        var observations = proof["observations"].EnumerateArray().Select(value =>
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(value.GetRawText())!).ToArray();
        foreach (var observation in observations)
        {
            var line = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(observation["lines"][0].GetRawText())!;
            line["text"] = JsonSerializer.SerializeToElement(replacement);
            observation["lines"] = JsonSerializer.SerializeToElement(new[] { line });
        }
        proof["observations"] = JsonSerializer.SerializeToElement(observations);
        item.Text = replacement; item.RegionalTextRepair = JsonSerializer.SerializeToElement(proof);
    }

    private static DoclingTextItem PrepareRepair(DoclingDocument source)
    {
        var item = source.Texts[1];
        const string original = "e a t n   maintain positive shutdown state";
        const string replacement = "All installations maintain positive shutdown state";
        item.OriginalText=original; item.Text=replacement;
        var polygon=new[] { new[] {20d,52},new[] {80d,52},new[] {80d,68},new[] {20d,68} };
        item.RegionalTextRepair=JsonSerializer.SerializeToElement(new
        {
            schema_version="saaia_text_region_repair_v1",status="applied",reason="independent_high_confidence_source_retaining_agreement",
            source_ref=item.SelfRef,original_text=original,original_text_sha256=Hash(original),
            replacement_text=replacement,replacement_text_sha256=Hash(replacement),source_provenance=item.Provenance,
            damage=new { candidate=true,fragmented_prefix_end=7,reliable_suffix_start=7 },
            agreement_normalization="unicode_nfc_and_whitespace_only",
            source_preservation=new { reliable_suffix_tokens_in_order=true,critical_prefix_tokens_in_order=true,
                lowercase_two_letter_prefix_words_preserved=true,
                linguistic_prefix_negation_guard="fr_en_de_finite_v1",universal_multilingual_validation=false },
            observations=new[]
            {
                new { engine="rapidocr",engine_version="3.9.1",model_id="rapid-model",duration_ms=12,coordinate_origin="TOPLEFT",minimum_confidence=.99,
                    lines=new[] { new { text=replacement,confidence=.99,polygon } } },
                new { engine="tesseract",engine_version="5.3.0",model_id="eng:psm6",duration_ms=12,coordinate_origin="TOPLEFT",minimum_confidence=.99,
                    lines=new[] { new { text=replacement,confidence=.99,polygon } } }
            }
        });
        return item;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
