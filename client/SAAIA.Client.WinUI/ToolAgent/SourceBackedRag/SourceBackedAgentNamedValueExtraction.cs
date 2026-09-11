using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static bool IsStrictExtractiveNamedValueClaim(
        string claimText,
        IReadOnlyList<string> evidenceIds,
        SelectedEvidenceWriterContext writerContext)
    {
        var normalizedClaim = NormalizeStrictExtractiveText(claimText);
        if (normalizedClaim.Length == 0)
            return false;

        var paddedClaim = " " + normalizedClaim + " ";
        return writerContext.Groups
            .SelectMany(static group => group.CitableEvidence)
            .Where(item => evidenceIds.Contains(
                item.EvidenceId,
                StringComparer.OrdinalIgnoreCase))
            .Select(item => " " + NormalizeStrictExtractiveText(
                FirstNonBlank(item.Excerpt, item.NormalizedExcerpt)) + " ")
            .Any(excerpt => excerpt.Contains(
                paddedClaim,
                StringComparison.Ordinal));
    }

    private static string NormalizeStrictExtractiveText(string? value)
    {
        var decomposed = (value ?? string.Empty).Normalize(
            NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character)
                == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            builder.Append(char.IsLetterOrDigit(character)
                ? char.ToLowerInvariant(character)
                : ' ');
        }
        return Regex.Replace(
                builder.ToString(),
                @"\s+",
                " ",
                RegexOptions.CultureInvariant)
            .Trim();
    }
}
