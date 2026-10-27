using System.Text.RegularExpressions;

/// <summary>Observed package/configuration data is never replaced by declared deployment labels.</summary>
internal static class DoclingExecutionMetadataPolicy
{
    internal static void Validate(DocumentIntelligenceOptions options, DoclingConvertResponse response, bool requestedForceOcr)
    {
        DoclingRegionalTextRepairPolicy.Validate(options, response);
        DoclingTableRegionObservationPolicy.Validate(options, response);
        var observed = response.Execution;
        if (observed is null) return; // Legacy service: explicitly unattested in its manifest.
        if (observed.ForceOcr.HasValue && observed.ForceOcr != requestedForceOcr
            || observed.DoOcr.HasValue && observed.DoOcr != options.DoOcr)
            throw new InvalidDataException("docling_execution_ocr_configuration_mismatch");
        if (observed.TableRepairEnabled.HasValue && observed.TableRepairEnabled != options.RegionalTableRepairEnabled
            || observed.TableRepairScale.HasValue && observed.TableRepairScale != options.RegionalTableRepairScale
            || observed.TableRepairPaddingPoints.HasValue && observed.TableRepairPaddingPoints != options.RegionalTableRepairPaddingPoints
            || observed.TableRepairMinimumScore.HasValue && observed.TableRepairMinimumScore != options.RegionalTableRepairMinimumScore)
            throw new InvalidDataException("docling_execution_table_configuration_mismatch");
        Check("docling-serve", options.EngineVersion);
        Check("docling-slim", options.EngineVersion);
        Check("docling-parse", options.ParseEngineVersion);
        Check("docling-ibm-models", options.ModelPackageVersion);
        Check("rapidocr", options.OcrEngineVersion);
        Check("onnxruntime", options.OcrEngineVersion);
        void Check(string package, string declared)
        {
            if (!observed.PackageVersions.TryGetValue(package, out var actual) || string.IsNullOrWhiteSpace(actual)) return;
            var expected = Regex.Match(declared, @"(?:^|_)" + Regex.Escape(package) + @"-(?<version>[^_]+)", RegexOptions.CultureInvariant);
            if (expected.Success && !string.Equals(expected.Groups["version"].Value, actual, StringComparison.Ordinal))
                throw new InvalidDataException("docling_execution_package_version_mismatch:" + package);
        }
    }

    internal static string? Package(DoclingConvertResponse response, string package)
    {
        if (response.Execution is null || !response.Execution.PackageVersions.TryGetValue(package, out var value)
            || string.IsNullOrWhiteSpace(value) || value.Length > 128)
            return null;
        return value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+') ? value : null;
    }

    internal static string DescribeEngine(DocumentIntelligenceOptions options, DoclingConvertResponse response)
    {
        var serve = Package(response, "docling-serve");
        var slim = Package(response, "docling-slim");
        return serve is null && slim is null ? options.EngineVersion
            : string.Join("_", new[] { serve is null ? null : "docling-serve-" + serve,
                slim is null ? null : "docling-slim-" + slim }.Where(value => value is not null));
    }
}
