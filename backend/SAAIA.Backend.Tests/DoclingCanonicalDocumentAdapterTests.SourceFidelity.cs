using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

public sealed partial class DoclingCanonicalDocumentAdapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Docling_fidelity_repeated_margin_warning_remains_retrievable_even_when_parser_marks_furniture(bool parserFurniture)
    {
        var source = FidelityDocument();
        const string warning = "Disconnect power before opening the equipment enclosure.";
        for (var page = 1; page <= 3; page++)
        {
            source.Pages[page.ToString()] = new() { PageNumber = page, Size = new() { Width = 100, Height = 200 } };
            AddFidelityText(source, warning, parserFurniture ? "page_header" : "text", page, top: 195, bottom: 185,
                furniture: parserFurniture);
            AddFidelityText(source, $"Physical page {page} records the inspected operating equipment and its verified isolation procedure.", page: page);
        }
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var chunks = DoclingCanonicalRetrievalProjector.Project(canonical, source, 100, 1);

        for (var page = 1; page <= 3; page++)
            Assert.Contains(chunks, chunk => chunk.PageStart <= page && chunk.PageEnd >= page
                && chunk.Text.Contains(warning, StringComparison.Ordinal) && IngestionWorker.ShouldPublishRetrievalChunk(chunk));
        Assert.All(canonical.Pages.SelectMany(page => page.Blocks).Where(block => block.Text.Canonical == warning),
            block => Assert.Equal(warning, block.Text.Retrieval));
    }

    [Fact]
    public void Docling_fidelity_recovered_native_text_has_no_inferred_last_subject_heading()
    {
        var source = FidelityDocument();
        AddFidelityText(source, "Assembly Alpha", "section_header");
        AddFidelityText(source, "Inspect the original assembly components and record the mechanical condition before service.");
        AddFidelityText(source, "Assembly Beta", "section_header");
        AddFidelityText(source, "The second assembly has an independent operating procedure and a separate maintenance record.");
        const string recovered = "Residual pressure must be verified before removing the upper cover.";
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        CanonicalNativeTextCoverageReconciler.Apply(canonical, FidelityNative((1,
            string.Join('\n', source.Texts.Take(2).Select(item => item.Text).Append(recovered)
                .Concat(source.Texts.Skip(2).Select(item => item.Text))))));

        var chunk = Assert.Single(DoclingCanonicalRetrievalProjector.Project(canonical, source, 100, 1),
            item => item.Text.Contains(recovered, StringComparison.Ordinal));
        Assert.Null(chunk.SectionTitle);
        Assert.Null(chunk.HeadingPath);
        Assert.Null(chunk.SectionOrdinal);
        Assert.DoesNotContain("Assembly Beta", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("not_asserted", chunk.ChunkComposition ?? "", StringComparison.Ordinal);
        Assert.NotEmpty(chunk.CanonicalBlockIds!);
        Assert.Equal(1, chunk.PageStart);
    }

    [Fact]
    public void Docling_fidelity_navigation_elsewhere_on_page_does_not_hide_recovered_content()
    {
        var response = BuildResponse();
        var source = response.Document.JsonContent!;
        source.Tables[0].Label = "document_index";
        const string recovered = "Residual pressure must be verified before removing the upper cover.";
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        CanonicalNativeTextCoverageReconciler.Apply(canonical,
            FidelityNative((1, "Canonical body\nA B\nC D\n" + recovered)));

        var chunk = Assert.Single(DoclingCanonicalRetrievalProjector.Project(canonical, source, 100, 1),
            item => item.Text.Contains(recovered, StringComparison.Ordinal));
        Assert.True(IngestionWorker.ShouldPublishRetrievalChunk(chunk));
        Assert.NotEqual(RetrievalContentClassifier.NavigationRole, chunk.ContentRole);
    }

    [Theory]
    [InlineData("The equipment maximum operating pressure is 20 bar when the control valve is closed and all protective interlocks remain enabled during normal operation.",
        "The equipment maximum operating pressure is 30 bar when the control valve is closed and all protective interlocks remain enabled during normal operation.")]
    [InlineData("Approved", "Inspection records contain the operating equipment identification and the verified procedure.")]
    public void Docling_fidelity_high_coverage_and_one_word_do_not_erase_native_disagreement(string native, string converted)
    {
        var source = FidelityDocument();
        AddFidelityText(source, converted);
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);

        var summary = CanonicalNativeTextCoverageReconciler.Apply(canonical, FidelityNative((1, native)));

        Assert.True(summary.RecoveredLineCount > 0);
        Assert.Contains(canonical.Pages[0].Blocks, block => block.Text.Retrieval.Contains(native, StringComparison.Ordinal));
        Assert.Contains(canonical.Pages[0].Blocks, block => block.Text.Canonical == converted);
    }

    [Fact]
    public void Docling_fidelity_missing_physical_page_refuses_a_complete_projection()
    {
        var source = FidelityDocument();
        AddFidelityText(source, "Source page one contains a verified operating procedure.");
        var native = FidelityNative((1, "Source page one contains a verified operating procedure."),
            (2, "The source page two restriction cannot silently disappear."));
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);

        var error = Assert.Throws<InvalidDataException>(() => CanonicalNativeTextCoverageReconciler.Apply(canonical, native));

        Assert.Contains("page_inventory", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Docling_fidelity_legacy_page_view_respects_exact_unicode_region_boundaries()
    {
        var source = FidelityDocument();
        source.Pages["2"] = new() { PageNumber = 2, Size = new() { Width = 100, Height = 200 } };
        var text = AddFidelityText(source, "Alpha 😀 Beta Gamma");
        text.Provenance = [new() { PageNumber = 1, BoundingBox = Box(10, 150, 90, 130, "BOTTOMLEFT"), CharacterSpan = [0, 12] },
            new() { PageNumber = 2, BoundingBox = Box(10, 150, 90, 130, "BOTTOMLEFT"), CharacterSpan = [13, 18] }];
        var response = new DoclingConvertResponse { Status = "success", Document = new() { JsonContent = source } };

        var extraction = DoclingLegacyExtractionAdapter.Project(response);

        Assert.Equal("Alpha 😀 Beta", extraction.Pages[0].Text);
        Assert.Equal("Gamma", extraction.Pages[1].Text);
        Assert.DoesNotContain("Gamma", extraction.Pages[0].RawText);
    }

    [Fact]
    public void Docling_fidelity_failed_table_repair_has_a_durable_degraded_stage()
    {
        var response = BuildResponse();
        response.TableRepairs.Add(new() { TableRef = "#/tables/0", Status = "error", Reason = "RuntimeError" });

        var stages = DoclingIngestionStageManifestFactory.Create(new(), new(), BuildRagOptions(), response,
            new(1, 2, 3, 4), "source-fidelity-test");

        var stage = Assert.Single(stages, stage => stage.StageId == "regional_table_ocr_repair");
        Assert.Equal("1", stage.Attributes["failedTableCount"]);
        Assert.Equal("degraded", stage.Attributes["outcome"]);
        Assert.Equal("0", stage.Attributes["appliedTableCount"]);
    }

    [Fact]
    public void Docling_fidelity_document_timing_does_not_claim_observed_page_counts()
    {
        var response = BuildResponse();
        response.Timings["ocr"] = new() { Scope = "document", Count = 1, Times = [0.2] };
        var diagnostics = DocumentExtractionPolicy.BuildDoclingOcrDiagnostics(new(), response,
            DoclingLegacyExtractionAdapter.Project(response));
        var json = JsonSerializer.SerializeToElement(diagnostics);

        Assert.True(json.TryGetProperty("PageCountsAvailable", out var available));
        Assert.False(available.GetBoolean());
        Assert.Equal("document", json.GetProperty("EngineTimingScope").GetString());
        Assert.Equal(1, json.GetProperty("EngineTimingCount").GetInt32());
        Assert.Equal("engine_managed_page_counts_unknown", diagnostics.CoverageStatus);
    }

    [Fact]
    public void Docling_fidelity_true_page_counter_stays_out_of_searchable_content()
    {
        var response = BuildResponse();
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), response.Document.JsonContent!);
        var chunks = DoclingCanonicalRetrievalProjector.Project(canonical, response.Document.JsonContent!, 100, 1);
        Assert.DoesNotContain(chunks.Where(IngestionWorker.ShouldPublishRetrievalChunk), item => item.Text.Trim() == "1");
    }

    [Fact]
    public void Docling_fidelity_reconciled_page_and_unit_views_include_the_same_recovered_observation()
    {
        var response = BuildResponse();
        const string fact = "Residual pressure must be verified before removing the upper cover.";
        var native = FidelityNative((1, "Canonical body\n" + fact));
        var legacy = DoclingLegacyExtractionAdapter.Project(response);
        var structure = IngestionProjectionStage.ProjectStructure(new(Guid.NewGuid(), Guid.NewGuid(), 1,
            new string('a', 64), 100, "fidelity.pdf", new(), response, native, legacy), NullLogger.Instance);

        Assert.NotNull(structure.CanonicalExtraction);
        Assert.Contains(fact, structure.CanonicalExtraction.Pages[0].Text, StringComparison.Ordinal);
        var unit = Assert.Single(structure.Units, unit => unit.Text.Contains(fact, StringComparison.Ordinal));
        Assert.Null(unit.SectionOrdinal);
        Assert.Equal(1, unit.PageStart);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(structure.CanonicalExtraction.Pages[0].RawText!)),
            structure.CanonicalExtraction.Pages[0].Checksum);
    }

    [Fact]
    public void Docling_fidelity_manifest_hashes_effective_force_instead_of_the_declared_default()
    {
        var actual = BuildResponse(); actual.RequestedForceOcr = true;
        var declared = BuildResponse();
        var actualStages = DoclingIngestionStageManifestFactory.Create(new() { ForceOcr = false }, new(), BuildRagOptions(),
            actual, new(1, 2, 3, 4), "fidelity-test");
        var declaredStages = DoclingIngestionStageManifestFactory.Create(new() { ForceOcr = true }, new(), BuildRagOptions(),
            declared, new(1, 2, 3, 4), "fidelity-test");
        Assert.Equal(actualStages.Single(stage => stage.StageId == "docling_ocr").OptionsSha256,
            declaredStages.Single(stage => stage.StageId == "docling_ocr").OptionsSha256);
    }

    [Fact]
    public void Docling_fidelity_unavailable_native_audit_is_not_a_zero_page_success()
    {
        var source = FidelityDocument(); AddFidelityText(source, "Verified operating instructions remain available.");
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var summary = CanonicalNativeTextCoverageReconciler.Apply(canonical, null, enabled: true);
        Assert.Equal("unavailable", summary.Availability);
        Assert.Equal(0, summary.AuditedPageCount);
    }

    [Fact]
    public void Docling_fidelity_unknown_page_counts_are_null_in_durable_diagnostics()
    {
        var diagnostics = new PdfOcrDiagnostics("docling_auto", 0, 0, 0, 3, [], [], [], [], [])
            { PageCountsAvailable = false, EngineTimingCount = 1, EngineTimingScope = "document" };
        var method = typeof(DocumentFoundationRepo).GetMethod("BuildOcrDiagnosticsPayload",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var json = JsonSerializer.SerializeToElement(method.Invoke(null, [diagnostics]));
        Assert.False(json.GetProperty("pageCountsAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("attemptedPageCount").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("candidatePages").ValueKind);
        Assert.Equal(1, json.GetProperty("engineTimingCount").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Docling_capability_unprojected_structured_objects_are_not_silently_declared_complete(bool form)
    {
        var source = FidelityDocument();
        AddFidelityText(source, "Supported text remains present beside an unsupported structured object.");
        var item = new DoclingContentItem { SelfRef = form ? "#/form_items/0" : "#/key_value_items/0" };
        if (form) source.FormItems.Add(item); else source.KeyValueItems.Add(item);
        var error = Assert.Throws<InvalidDataException>(() => DoclingCanonicalDocumentAdapter.Project(BuildContext(), source));
        Assert.Contains("unprojected_structured", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Docling_capability_figure_inventory_does_not_claim_semantic_reading()
    {
        var source = BuildDocument();
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        Assert.NotEmpty(canonical.Pages[0].Figures);
        Assert.Contains("figure_semantics_not_extracted", canonical.Pages[0].QualityFlags);
    }

    [Fact]
    public void Docling_fidelity_repeated_margin_unit_does_not_reintroduce_section_assertion()
    {
        var source = FidelityDocument();
        const string warning = "Disconnect power before opening the equipment enclosure.";
        for (var page = 1; page <= 3; page++)
        {
            source.Pages[page.ToString()] = new() { PageNumber = page, Size = new() { Width = 100, Height = 200 } };
            AddFidelityText(source, "Assembly " + page, "section_header", page);
            AddFidelityText(source, "The assembly procedure records the verified condition and the independent maintenance result.", page: page);
            AddFidelityText(source, warning, page: page, top: 15, bottom: 5);
        }
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var units = CanonicalExtractionViewProjector.ProjectUnits(canonical, source);
        var warningUnits = units.Where(unit => unit.Text == warning).ToArray();
        Assert.Equal(3, warningUnits.Length);
        Assert.All(warningUnits, unit => Assert.Null(unit.SectionOrdinal));
        var warningIds = canonical.Pages.SelectMany(page => page.Blocks).Where(block => block.Text.Canonical == warning)
            .Select(block => block.BlockId).ToHashSet(StringComparer.Ordinal);
        Assert.All(canonical.SectionTree, section => Assert.DoesNotContain(section.ContentBlockIds, warningIds.Contains));
    }

    [Fact]
    public void Docling_fidelity_repeated_explicit_section_headings_keep_their_declared_structure()
    {
        var source = FidelityDocument();
        for (var page = 1; page <= 3; page++)
        {
            source.Pages[page.ToString()] = new() { PageNumber = page, Size = new() { Width = 100, Height = 200 } };
            AddFidelityText(source, "Operating instructions", "section_header", page, top: 195, bottom: 185);
            AddFidelityText(source, "The assembly procedure records its verified operating condition and maintenance result.", page: page);
        }
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        Assert.Equal(3, canonical.SectionTree.Count);
        var units = CanonicalExtractionViewProjector.ProjectUnits(canonical, source);
        Assert.All(units.Where(unit => unit.Text == "Operating instructions"), unit => Assert.NotNull(unit.SectionOrdinal));
    }

    [Theory]
    [InlineData("20", "-20")]
    [InlineData("20", "20.5")]
    [InlineData("20", "20,5")]
    [InlineData("Approved", "Not Approved")]
    [InlineData("1 5", "15")]
    [InlineData("not able", "notable")]
    public void Docling_literal_coverage_does_not_hide_value_sign_or_negation(string native, string converted)
    {
        var source = FidelityDocument(); AddFidelityText(source, converted);
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var summary = CanonicalNativeTextCoverageReconciler.Apply(canonical, FidelityNative((1, native)));
        Assert.Equal(1, summary.RecoveredLineCount);
        Assert.Contains(canonical.Pages[0].Blocks, block => block.Text.Canonical == native);
        Assert.Contains(canonical.Pages[0].Blocks, block => block.Text.Canonical == converted);
    }

    [Fact]
    public void Docling_literal_coverage_exact_block_across_native_line_wrapping_is_not_duplicated()
    {
        var source = FidelityDocument();
        AddFidelityText(source, "Inspect the installed control equipment before returning the assembly to normal service.");
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var summary = CanonicalNativeTextCoverageReconciler.Apply(canonical,
            FidelityNative((1, "Inspect the installed control equipment\nbefore returning the assembly to normal service.")));
        Assert.Equal(0, summary.RecoveredLineCount);
    }

    [Fact]
    public void Docling_literal_coverage_preserves_a_source_occurrence_missing_from_canonical_and_is_idempotent()
    {
        var source = FidelityDocument(); AddFidelityText(source, "Approved");
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var native = FidelityNative((1, "Approved\nApproved"));
        Assert.Equal(1, CanonicalNativeTextCoverageReconciler.Apply(canonical, native).RecoveredLineCount);
        Assert.Equal(0, CanonicalNativeTextCoverageReconciler.Apply(canonical, native).RecoveredLineCount);
    }

    private static DoclingDocument FidelityDocument() => new()
    {
        SchemaName = "DoclingDocument", Version = "1.10.0",
        Pages = { ["1"] = new() { PageNumber = 1, Size = new() { Width = 100, Height = 200 } } }
    };

    private static DoclingTextItem AddFidelityText(DoclingDocument source, string text, string label = "text",
        int page = 1, double top = 150, double bottom = 130, bool furniture = false)
    {
        var item = Text($"#/texts/{source.Texts.Count}", label, text, text, furniture ? "furniture" : "body",
            Box(10, top, 90, bottom, "BOTTOMLEFT"), level: label == "section_header" ? 1 : null, pageNumber: page);
        source.Texts.Add(item);
        (furniture ? source.Furniture : source.Body).Children.Add(new() { Ref = item.SelfRef });
        return item;
    }

    private static PdfExtractionResult FidelityNative(params (int Page, string Text)[] input)
    {
        var pages = input.Select(item => new ExtractedPdfPage(item.Page, item.Text,
            item.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, item.Text.Length,
            SHA256.HashData(Encoding.UTF8.GetBytes(item.Text)),
            PdfPageExtractionQuality.FromText(item.Text, item.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, item.Text.Length))).ToArray();
        return new([], pages.ToList(), PdfExtractionQualitySummary.FromPages(pages));
    }
}
