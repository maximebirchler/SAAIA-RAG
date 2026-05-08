using SAAIA.Client.WinUI.Controls;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SetupWizardReadySummaryTests
{
    [Fact]
    public void ParseReadySummary_reads_ocr_readiness_details()
    {
        const string raw = """
{
  "ok": false,
  "details": {
    "db": true,
    "tei": true,
    "qdrant": true,
    "llm": "client-only",
    "ocr_enabled": true,
    "ocr_ready": false,
    "ocr_error": "ocr_image_text_unavailable"
  }
}
""";

        var summary = SetupWizardDialog.ParseReadySummary(raw);

        Assert.False(summary.BodyOk);
        Assert.True(summary.Db);
        Assert.True(summary.Tei);
        Assert.True(summary.Qdrant);
        Assert.Equal("client-only", summary.Llm);
        Assert.True(summary.OcrEnabled);
        Assert.False(summary.OcrReady);
        Assert.Equal("ocr_error: ocr_image_text_unavailable", summary.FirstError);
        Assert.Null(summary.FirstWarning);
    }

    [Fact]
    public void ParseReadySummary_reads_component_warnings_without_failing_readiness()
    {
        const string raw = """
{
  "ok": true,
  "details": {
    "db": true,
    "tei": true,
    "qdrant": true,
    "llm": "server-ok",
    "ocr_enabled": true,
    "ocr_ready": true,
    "ocr_warning": "ocr_languages_not_verified"
  }
}
""";

        var summary = SetupWizardDialog.ParseReadySummary(raw);

        Assert.True(summary.BodyOk);
        Assert.True(summary.OcrReady);
        Assert.Null(summary.FirstError);
        Assert.Equal("ocr_warning: ocr_languages_not_verified", summary.FirstWarning);
    }
}
