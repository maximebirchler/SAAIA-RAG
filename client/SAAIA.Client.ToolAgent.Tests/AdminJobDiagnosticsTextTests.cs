using SAAIA.Client.WinUI;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class AdminJobDiagnosticsTextTests
{
    private static readonly string[] Languages = ["fr", "en", "es", "pt", "de", "it"];

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void Admin_job_error_text_localizes_embedded_ocr_failure_reason(string language)
    {
        var raw = "No text extracted from PDF; text_status=empty_text; ocr_recommended=True; failure_reason=scanned_pdf_not_indexable; ocr_failure=exit_code_non_zero";

        var text = MainWindow.BuildAdminJobErrorTextForDiagnostics(raw, isCanceled: false, language);

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain("failure_reason", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ocr_failure", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scanned_pdf_not_indexable", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exit_code_non_zero", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("_", text);
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void Admin_job_auto_pause_reason_localizes_ocr_and_indexability_codes(string language)
    {
        foreach (var code in new[]
                 {
                     "ocr_required_but_disabled",
                     "scanned_pdf_not_indexable",
                     "no_indexable_text",
                     "document_not_indexable",
                     "ocr_disabled",
                     "ocr_extraction_failed",
                     "ocr_failed",
                     "exit_code_non_zero",
                     "ocr_output_missing",
                     "no_novel_text"
                 })
        {
            var text = MainWindow.TranslateAdminJobAutoPauseReasonForDiagnostics(code, language);

            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain("_", text);
            Assert.NotEqual(code, text);
        }
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void Admin_job_status_unknown_falls_back_to_localized_text(string language)
    {
        var text = MainWindow.TranslateAdminJobStatusForDiagnostics("future_backend_status", language);

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain("future_backend_status", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("admin.jobs.status.", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("_", text);
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void Admin_job_resume_reason_unknown_never_leaks_backend_token(string language)
    {
        var text = MainWindow.TranslateAdminJobResumeReasonForDiagnostics("future_backend_reason", language);

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain("future_backend_reason", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("future backend reason", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("_", text);
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void Admin_job_resume_reason_maps_known_server_reasons(string language)
    {
        foreach (var reason in new[] { "not_paused", "missing_file", "invalid_state", "not_owner" })
        {
            var text = MainWindow.TranslateAdminJobResumeReasonForDiagnostics(reason, language);

            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain(reason, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("_", text);
        }
    }

    [Fact]
    public void Admin_job_diagnostic_reason_prefers_failure_reason_over_ocr_failure()
    {
        var raw = "failure_reason=scanned_pdf_not_indexable; ocr_failure=exit_code_non_zero";

        Assert.Equal("scanned_pdf_not_indexable", MainWindow.ResolveAdminJobDiagnosticReasonCode(raw));
    }

    public static TheoryData<string> AllLanguages()
    {
        var data = new TheoryData<string>();
        foreach (var language in Languages)
            data.Add(language);
        return data;
    }
}
