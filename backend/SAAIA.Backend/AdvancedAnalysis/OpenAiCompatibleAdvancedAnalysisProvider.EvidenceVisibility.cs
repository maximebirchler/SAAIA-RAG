namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private static bool HasUnprojectedCanonicalContent(PromptEvidenceItem evidence)
    {
        if (evidence.OriginalContentLength <= 0) return false;
        var ranges = evidence.SupportingContentWindows.Select(window =>
                (Start: window.ContentOffset, Length: window.Content.Length))
            .Append((Start: evidence.ContentOffset, Length: evidence.Content.Length))
            .Where(range => range.Length > 0).OrderBy(range => range.Start);
        long covered = 0;
        foreach (var range in ranges)
        {
            if (range.Start > covered) return true;
            covered = Math.Max(covered, (long)range.Start + range.Length);
            if (covered >= evidence.OriginalContentLength) return false;
        }
        return covered < evidence.OriginalContentLength;
    }
}
