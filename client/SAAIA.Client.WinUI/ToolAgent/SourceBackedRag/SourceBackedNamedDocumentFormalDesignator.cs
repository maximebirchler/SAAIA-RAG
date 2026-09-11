using System.Globalization;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedNamedDocumentResolver
{
    internal static bool IsUniqueFormalDesignatorCandidate(
        string requestedReference,
        SourceBackedDocumentResolutionCandidate candidate)
    {
        var requested = NormalizePath(CleanReference(requestedReference));
        if (string.IsNullOrWhiteSpace(requested)
            || requested.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        var requestedName = ReadFileName(requested);
        if (HasExtension(requestedName)
            || !requestedName.Any(char.IsLetter))
        {
            return false;
        }

        var designators = ExtractFormalDesignators(requestedName);
        if (designators.Count == 0)
            return false;

        return new[]
            {
                ReadFileName(NormalizePath(candidate.DocPath)),
                ReadFileName(NormalizePath(candidate.DocName))
            }
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(RemoveExtension)
            .Any(candidateStem => designators.All(designator =>
                ContainsExactFormalDesignator(candidateStem, designator)));
    }

    internal static bool IsFormalDesignatorCatalogQueryMatch(
        string query,
        SourceBackedDocumentResolutionCandidate candidate)
    {
        var normalizedQuery = query.Trim();
        var designators = ExtractFormalDesignators(normalizedQuery);
        if (designators.Count != 1
            || !string.Equals(
                normalizedQuery,
                designators[0],
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return new[] { candidate.DocPath, candidate.DocName }
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(ReadFileName)
            .Select(RemoveExtension)
            .Any(candidateStem => ContainsExactFormalDesignator(
                candidateStem,
                designators[0]));
    }

    private static IReadOnlyList<string> ExtractFormalDesignators(string value)
        => Regex.Matches(
                value,
                @"(?<!\d)\d{4,6}(?:[-/]\d{1,4})?(?!\d)",
                RegexOptions.CultureInvariant)
            .Select(static match => match.Value)
            .Where(static value => !IsPlausibleStandaloneYear(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool IsPlausibleStandaloneYear(string value)
        => value.Length == 4
           && int.TryParse(
               value,
               NumberStyles.None,
               CultureInfo.InvariantCulture,
               out var numeric)
           && numeric is >= 1900 and <= 2099;

    private static bool ContainsExactFormalDesignator(
        string candidateStem,
        string designator)
        => Regex.IsMatch(
            candidateStem,
            $@"(?<!\d){Regex.Escape(designator)}(?![\d/-])",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
}
