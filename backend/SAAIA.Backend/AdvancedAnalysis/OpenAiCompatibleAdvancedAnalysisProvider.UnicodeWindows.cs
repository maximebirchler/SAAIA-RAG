namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    // Existing evidence offsets remain UTF-16 code-unit offsets. Never change
    // the source or silently move an explicitly requested start; only shorten
    // a bounded end so that JSON cannot replace half a character with U+FFFD.
    private static bool IsEvidenceTextBoundary(string text, int offset)
        => offset >= 0 && offset <= text.Length
           && !(offset > 0 && offset < text.Length
                && char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]));

    private static int BoundEvidenceTextLength(string text, int offset, int maximumLength)
    {
        var length = Math.Clamp(maximumLength, 0, text.Length - offset);
        return IsEvidenceTextBoundary(text, offset + length) ? length : length - 1;
    }
}
