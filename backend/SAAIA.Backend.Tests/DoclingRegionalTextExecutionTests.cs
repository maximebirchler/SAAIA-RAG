using System.Text.Json;
using Xunit;

public sealed class DoclingRegionalTextExecutionTests
{
    [Fact]
    public void Requested_regional_text_repair_requires_observed_settings_not_a_declared_label()
    {
        var options=new DocumentIntelligenceOptions { RegionalTextRepairEnabled=true };
        Assert.Throws<InvalidDataException>(() => DoclingExecutionMetadataPolicy.Validate(options,new(),false));
    }

    [Fact]
    public void Regional_text_repair_settings_mismatch_is_rejected()
    {
        var response=new DoclingConvertResponse { Execution=new() { TextRepairSettings=Settings(true) } };
        Assert.Throws<InvalidDataException>(() => DoclingExecutionMetadataPolicy.Validate(new(),response,false));
    }

    [Fact]
    public void Regional_text_repair_is_disabled_by_default_and_legacy_service_stays_unattested()
    {
        var options=new DocumentIntelligenceOptions();
        Assert.False(options.RegionalTextRepairEnabled);
        DoclingExecutionMetadataPolicy.Validate(options,new(),false);
    }

    [Fact]
    public void Regional_text_repair_refuses_invalid_signed_budget_without_clamping_it()
    {
        var options = new DocumentIntelligenceOptions { RegionalTextRepairMinimumRapidScore = 0.5 };
        Assert.Throws<InvalidDataException>(() => DoclingExecutionMetadataPolicy.Validate(options, new(), false));
        options = new() { RegionalTextRepairMaxRegions = 5 };
        Assert.Throws<InvalidDataException>(() => DoclingExecutionMetadataPolicy.Validate(options, new(), false));
    }

    [Fact]
    public void Regional_text_repair_enabled_with_no_candidate_records_zero_actual_attempts()
    {
        var options = new DocumentIntelligenceOptions { RegionalTextRepairEnabled = true };
        var response = new DoclingConvertResponse
        {
            Document = new() { JsonContent = new() },
            Execution = new() { TextRepairSettings = Settings(true) },
            TextRepairSummary = JsonSerializer.SerializeToElement(new
            {
                schema_version="saaia_text_region_repair_v1",candidate_count=0,attempted_count=0,applied_count=0,
                duration_ms=7,limit_max_regions=4,limit_total_timeout_seconds=60d
            })
        };
        DoclingExecutionMetadataPolicy.Validate(options, response, false);
        var observed = DoclingRegionalTextRepairPolicy.Observe(response);
        Assert.Equal("enabled", observed.Status);
        Assert.Equal(0, observed.Attempted);
        Assert.Equal(7, observed.DurationMs);
    }

    internal static JsonElement Settings(bool enabled) => JsonSerializer.SerializeToElement(new
    {
        schema_version="saaia_text_repair_settings_v1",enabled,max_regions=4,total_timeout_seconds=60d,
        region_timeout_seconds=15d,render_scale=4d,padding_points=2d,minimum_rapid_score=.98,
        minimum_secondary_score=.95,max_rendered_pixels=20000000,secondary_engine="tesseract",secondary_language="eng",
        secondary_page_segmentation_mode=6,agreement_normalization="unicode_nfc_and_whitespace_only",
        linguistic_prefix_negation_guard="fr_en_de_finite_v1",universal_multilingual_validation=false,
        process_isolation="per_region_linux_process_group"
    });
}
