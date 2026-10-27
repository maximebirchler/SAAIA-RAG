using System.Security.Cryptography;
using System.Text.Json;
using SAAIA.Backend.Tests;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

public sealed partial class DoclingCanonicalDocumentAdapterTests
{
    [Fact]
    public void Visual_asset_document_policy_shares_the_count_limit_and_preserves_individual_png_limits()
    {
        Assert.Equal(1024, CanonicalVisualAssetPolicy.MaximumDocumentAssets);
        Assert.Equal(384L * 1024 * 1024, CanonicalVisualAssetPolicy.MaximumDocumentBytes);
        Assert.Equal(CanonicalVisualAssetPolicy.MaximumDocumentAssets, CanonicalVisualAssetRepo.MaximumDocumentAssets);
        Assert.Equal(16 * 1024 * 1024, CanonicalPngAsset.MaximumBytes);
        Assert.Equal(16_000_000L, CanonicalPngAsset.MaximumPixels);
    }

    [Fact]
    public void Visual_asset_preflight_accepts_exact_small_count_and_byte_boundaries()
    {
        var source = BuildDocument();
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        source.Pages["1"].Image = VisualAssetTestPng.Image();
        source.Pictures[0].Image = VisualAssetTestPng.Image();

        DoclingCanonicalVisualAssetProjector.Apply(document, source, new(2, 2L * VisualAssetTestPng.Bytes.Length));

        Assert.Equal(2, document.Assets.Count);
        Assert.Equal(CanonicalVisualAssetPolicy.MeasureSource(source),
            CanonicalVisualAssetPolicy.MeasurePublication(document.Assets));
        Assert.All(document.Assets, asset => Assert.Equal(VisualAssetTestPng.Bytes, asset.PublicationBytes));
    }

    [Theory]
    [InlineData(true, "count")]
    [InlineData(false, "bytes")]
    public void Visual_asset_preflight_refuses_before_decoding_or_adding_any_pixels(bool count, string dimension)
    {
        var source = BuildDocument();
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        source.Pages["1"].Image = VisualAssetTestPng.Image();
        source.Pictures[0].Image = VisualAssetTestPng.Image();
        // Valid base64 transport with deliberately invalid PNG: a budget refusal
        // must happen before PNG decoding/validation or partial asset admission.
        source.Pages["1"].Image!.Uri = "data:image/png;base64,AAAA";
        source.Pictures[0].Image!.Uri = "data:image/png;base64,AAAA";
        var budget = count ? new CanonicalVisualAssetBudget(1, 6) : new(2, 5);

        var error = Assert.Throws<CanonicalVisualAssetBudgetException>(() =>
            DoclingCanonicalVisualAssetProjector.Apply(document, source, budget));

        Assert.Equal(dimension, error.Dimension);
        Assert.Equal("preflight", error.Phase);
        Assert.Equal(2, error.Measurements.AssetCount);
        Assert.Equal(6, error.Measurements.LogicalBytes);
        Assert.Equal(budget, error.Budget);
        Assert.Empty(document.Assets);
        Assert.Throws<InvalidDataException>(() => DoclingCanonicalVisualAssetProjector.Apply(document, source));
    }

    [Fact]
    public void Visual_asset_append_is_charged_against_the_whole_document_before_mutation()
    {
        var source = BuildDocument();
        source.Pages["1"].Image = VisualAssetTestPng.Image();
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var original = Assert.Single(document.Assets);

        var error = Assert.Throws<CanonicalVisualAssetBudgetException>(() =>
            DoclingCanonicalVisualAssetProjector.Apply(document, source, new(1, 2L * original.SizeBytes)));

        Assert.Equal("count", error.Dimension);
        Assert.Equal(2, error.Measurements.AssetCount);
        Assert.Same(original, Assert.Single(document.Assets));
    }

    [Fact]
    public void Visual_asset_multi_provenance_shares_one_decode_but_preserves_every_asset_identity_and_pixel()
    {
        var source = BuildDocument();
        var picture = source.Pictures[0];
        picture.Image = VisualAssetTestPng.Image();
        picture.Captions.Clear();
        source.Pages["2"] = new() { PageNumber = 2, Size = new() { Width = 100, Height = 200 } };
        var secondProvenance = Provenance(Box(25, 40, 75, 30, "BOTTOMLEFT"));
        secondProvenance.PageNumber = 2;
        picture.Provenance.Add(secondProvenance);
        var uri = picture.Image.Uri;

        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var measurements = CanonicalVisualAssetPolicy.MeasureSource(source);

        Assert.Equal(2, document.Assets.Count);
        Assert.Equal(2, document.Assets.Select(asset => asset.AssetId).Distinct().Count());
        Assert.Equal(2, document.Assets.Select(asset => asset.FigureId).Distinct().Count());
        Assert.Same(document.Assets[0].PublicationBytes, document.Assets[1].PublicationBytes);
        Assert.Equal(2L * VisualAssetTestPng.Bytes.Length, measurements.LogicalBytes);
        Assert.Equal(1, measurements.UniqueImageReferences);
        Assert.Equal(VisualAssetTestPng.Bytes.Length, measurements.UniqueImageBytes);
        Assert.Equal(measurements, CanonicalVisualAssetPolicy.MeasurePublication(document.Assets));
        var hash = Convert.ToHexString(SHA256.HashData(VisualAssetTestPng.Bytes)).ToLowerInvariant();
        foreach (var asset in document.Assets)
        {
            Assert.Equal(VisualAssetTestPng.Bytes, asset.PublicationBytes);
            Assert.Equal(hash, asset.Sha256);
            var page = Assert.Single(document.Pages, page => page.PageNumber == asset.PageNumber);
            var figure = Assert.Single(page.Figures, figure => figure.FigureId == asset.FigureId);
            Assert.Equal(asset.AssetId, Assert.Single(figure.AssetIds));
            Assert.Equal("available", figure.Provenance.Attributes["visualAssetStatus"]);
        }
        var repeat = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        Assert.Equal(CanonicalContractJson.SerializeCanonical(document), CanonicalContractJson.SerializeCanonical(repeat));
        Assert.Equal(uri, picture.Image.Uri);
        Assert.Equal(2, picture.Provenance.Count);
    }

    [Fact]
    public void Visual_asset_source_reference_cache_also_shares_page_and_figure_payloads()
    {
        var source = BuildDocument();
        source.Pages["1"].Image = source.Pictures[0].Image = VisualAssetTestPng.Image();
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);

        Assert.Same(document.Assets[0].PublicationBytes, document.Assets[1].PublicationBytes);
        Assert.NotEqual(document.Assets[0].AssetId, document.Assets[1].AssetId);
        Assert.Null(document.Assets[0].FigureId);
        Assert.NotNull(document.Assets[1].FigureId);
        Assert.Equal(1, CanonicalVisualAssetPolicy.MeasurePublication(document.Assets).UniqueImageReferences);
    }

    [Fact]
    public void Visual_asset_default_count_boundary_keeps_all_1024_provenances_without_duplicate_pixel_allocations()
    {
        var source = BuildDocument();
        var picture = source.Pictures[0];
        picture.Image = VisualAssetTestPng.Image();
        while (picture.Provenance.Count < CanonicalVisualAssetPolicy.MaximumDocumentAssets)
            picture.Provenance.Add(Provenance(Box(25, 40, 75, 30, "BOTTOMLEFT")));

        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);

        Assert.Equal(1024, document.Assets.Count);
        Assert.Equal(1024, document.Pages[0].Figures.Count);
        Assert.Equal(1024, document.Assets.Select(asset => asset.AssetId).Distinct().Count());
        Assert.All(document.Assets, asset => Assert.Same(document.Assets[0].PublicationBytes, asset.PublicationBytes));
        var measurements = CanonicalVisualAssetPolicy.MeasurePublication(document.Assets);
        Assert.Equal(1024L * VisualAssetTestPng.Bytes.Length, measurements.LogicalBytes);
        Assert.Equal(VisualAssetTestPng.Bytes.Length, measurements.UniqueImageBytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Visual_asset_base64_measurement_counts_padding_and_allowed_whitespace_without_decoding(int length)
    {
        var image = VisualAssetTestPng.Image();
        var encoded = Convert.ToBase64String(Enumerable.Repeat((byte)42, length).ToArray());
        image.Uri = "data:image/png;base64, \t" + encoded.Insert(2, "\r\n") + "\n";
        Assert.Equal(length, CanonicalPngAsset.MeasureEmbeddedByteLength(image));
        Assert.Throws<InvalidDataException>(() => CanonicalPngAsset.ReadEmbedded(image));
    }

    [Theory]
    [InlineData("AA=A")]
    [InlineData("AAAA=")]
    [InlineData("AA!A")]
    public void Visual_asset_preflight_rejects_invalid_base64_transport(string encoded)
    {
        var image = VisualAssetTestPng.Image();
        image.Uri = "data:image/png;base64," + encoded;
        Assert.Throws<InvalidDataException>(() => CanonicalPngAsset.MeasureEmbeddedByteLength(image));
    }

    [Fact]
    public void Visual_asset_preflight_never_replaces_png_crc_validation()
    {
        var bytes = VisualAssetTestPng.Bytes.ToArray();
        bytes[^1] ^= 1;
        var source = BuildDocument();
        source.Pictures[0].Image = VisualAssetTestPng.Image();
        source.Pictures[0].Image!.Uri = "data:image/png;base64," + Convert.ToBase64String(bytes);

        Assert.Equal(bytes.Length, CanonicalVisualAssetPolicy.MeasureSource(source).LogicalBytes);
        var error = Assert.Throws<InvalidDataException>(() => DoclingCanonicalDocumentAdapter.Project(BuildContext(), source));
        Assert.Equal("canonical_visual_asset_png_crc_invalid", error.Message);
    }

    [Theory]
    [InlineData(true, "count")]
    [InlineData(false, "bytes")]
    public async Task Visual_asset_sql_publication_uses_the_same_budget_before_any_database_call(bool count, string dimension)
    {
        var source = BuildDocument();
        source.Pages["1"].Image = VisualAssetTestPng.Image();
        source.Pictures[0].Image = VisualAssetTestPng.Image();
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var budget = count ? new CanonicalVisualAssetBudget(1, 2L * VisualAssetTestPng.Bytes.Length)
            : new(2, 2L * VisualAssetTestPng.Bytes.Length - 1);

        var error = await Assert.ThrowsAsync<CanonicalVisualAssetBudgetException>(() =>
            CanonicalVisualAssetRepo.StoreAsync(null!, null!, Guid.NewGuid(), document, CancellationToken.None, budget));

        Assert.Equal(dimension, error.Dimension);
        Assert.Equal("publication", error.Phase);
        Assert.Equal(CanonicalVisualAssetPolicy.MeasureSource(source), error.Measurements);
    }

    [Fact]
    public void Visual_asset_publication_measurement_refuses_forged_payload_sizes()
    {
        var source = BuildDocument();
        source.Pictures[0].Image = VisualAssetTestPng.Image();
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        document.Assets[0].SizeBytes--;
        Assert.Throws<InvalidDataException>(() => CanonicalVisualAssetPolicy.MeasurePublication(document.Assets));
    }

    [Fact]
    public void Visual_asset_count_refusal_emits_exact_numeric_probe_without_private_pixels_or_text()
    {
        var response = BuildResponse();
        var picture = response.Document.JsonContent!.Pictures[0];
        picture.Image = VisualAssetTestPng.Image();
        while (picture.Provenance.Count <= CanonicalVisualAssetPolicy.MaximumDocumentAssets)
            picture.Provenance.Add(Provenance(Box(25, 40, 75, 30, "BOTTOMLEFT")));
        var logger = new DiagnosticRecordingLogger<DoclingCanonicalDocumentAdapterTests>();
        var request = new IngestionStructureRequest(Guid.NewGuid(), Guid.NewGuid(), 1,
            new string('a', 64), 1234, "PRIVATE-DO-NOT-LOG.pdf", new(), response, null,
            DoclingLegacyExtractionAdapter.Project(response));

        Assert.Throws<CanonicalVisualAssetBudgetException>(() => IngestionProjectionStage.ProjectStructure(request, logger));

        var entry = Assert.Single(logger.Events, entry => Equals(entry.Values.GetValueOrDefault("Operation"), "ingestion.project_structure"));
        Assert.Equal("failed", entry.Values["Outcome"]);
        Assert.Equal("canonical_visual_asset_document_count_budget_exceeded", entry.Values["ReasonCode"]);
        using var json = JsonDocument.Parse((string)entry.Values["Measurements"]!);
        var fields = json.RootElement;
        Assert.Equal("preflight", fields.GetProperty("visual_asset_budget_phase").GetString());
        Assert.Equal("count", fields.GetProperty("visual_asset_budget_dimension").GetString());
        Assert.Equal(1024, fields.GetProperty("visual_asset_maximum_count").GetInt32());
        Assert.Equal(384L * 1024 * 1024, fields.GetProperty("visual_asset_maximum_bytes").GetInt64());
        Assert.Equal(1025, fields.GetProperty("visual_asset_logical_count").GetInt32());
        Assert.Equal(1025L * VisualAssetTestPng.Bytes.Length, fields.GetProperty("visual_asset_logical_bytes").GetInt64());
        Assert.Equal(0, fields.GetProperty("visual_asset_page_count").GetInt32());
        Assert.Equal(1025, fields.GetProperty("visual_asset_figure_count").GetInt32());
        Assert.Equal(1, fields.GetProperty("visual_asset_unique_image_references").GetInt32());
        Assert.Equal(VisualAssetTestPng.Bytes.Length, fields.GetProperty("visual_asset_unique_image_bytes").GetInt32());
        Assert.Equal(0, fields.GetProperty("diagnostic_dropped_measurements").GetInt32());
        Assert.Equal(0, fields.GetProperty("diagnostic_truncated_values").GetInt32());
        Assert.DoesNotContain("PRIVATE-DO-NOT-LOG", entry.Text);
        Assert.DoesNotContain(picture.Image.Uri, entry.Text);
    }
}
