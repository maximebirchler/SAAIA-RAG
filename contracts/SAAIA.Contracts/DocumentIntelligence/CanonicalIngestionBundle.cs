using System.Text.Json.Serialization;

namespace SAAIA.Contracts.DocumentIntelligence;

public sealed class CanonicalIngestionBundle
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CanonicalSchema.BundleVersion;

    [JsonPropertyName("document")]
    public CanonicalDocument Document { get; set; } = new();

    [JsonPropertyName("manifestEnvelope")]
    public IngestionManifestEnvelope ManifestEnvelope { get; set; } = new();

    [JsonPropertyName("sourceAnchors")]
    public List<SourceAnchor> SourceAnchors { get; set; } = [];
}

public static class CanonicalIngestionBundleValidator
{
    public static IReadOnlyList<CanonicalValidationIssue> Validate(CanonicalIngestionBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var issues = new List<CanonicalValidationIssue>();
        var document = bundle.Document;
        var manifest = bundle.ManifestEnvelope.Manifest;

        if (bundle.SchemaVersion != CanonicalSchema.BundleVersion)
            issues.Add(new("$.schemaVersion", "schema_version", "Unsupported canonical ingestion bundle schema."));

        issues.AddRange(CanonicalContractValidator.Validate(document));
        issues.AddRange(IngestionManifestValidator.Validate(bundle.ManifestEnvelope));

        AddIdentityIssueIf(
            manifest.DocumentId != document.DocumentId,
            "$.manifestEnvelope.manifest.documentId",
            "Manifest document ID does not match the canonical document.",
            issues);
        AddIdentityIssueIf(
            manifest.RevisionId != document.RevisionId,
            "$.manifestEnvelope.manifest.revisionId",
            "Manifest revision ID does not match the canonical document.",
            issues);
        AddIdentityIssueIf(
            !string.Equals(manifest.SourceSha256, document.Source.Sha256, StringComparison.OrdinalIgnoreCase),
            "$.manifestEnvelope.manifest.sourceSha256",
            "Manifest source hash does not match the canonical document.",
            issues);
        AddIdentityIssueIf(
            !string.Equals(document.ManifestSha256, bundle.ManifestEnvelope.ManifestSha256, StringComparison.OrdinalIgnoreCase),
            "$.document.manifestSha256",
            "Canonical document does not reference this manifest.",
            issues);

        var sourceArtifact = manifest.Artifacts.FirstOrDefault(static artifact =>
            string.Equals(
                artifact.ArtifactType,
                CanonicalArtifactTypes.SourceDocument,
                StringComparison.Ordinal));
        AddIdentityIssueIf(
            sourceArtifact is null,
            "$.manifestEnvelope.manifest.artifacts",
            "Manifest does not declare its primary source document.",
            issues);
        if (sourceArtifact is not null)
        {
            AddIdentityIssueIf(
                !string.Equals(sourceArtifact.Sha256, document.Source.Sha256, StringComparison.OrdinalIgnoreCase),
                "$.manifestEnvelope.manifest.artifacts",
                "Source artifact hash does not match the canonical document source.",
                issues);
            AddIdentityIssueIf(
                sourceArtifact.SizeBytes != document.Source.SizeBytes,
                "$.manifestEnvelope.manifest.artifacts",
                "Source artifact size does not match the canonical document source.",
                issues);
            AddIdentityIssueIf(
                !string.Equals(sourceArtifact.SchemaVersion, document.Source.MediaType, StringComparison.Ordinal),
                "$.manifestEnvelope.manifest.artifacts",
                "Source artifact media type does not match the canonical document source.",
                issues);
        }

        var anchorIds = new HashSet<string>(StringComparer.Ordinal);
        var projectionKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < bundle.SourceAnchors.Count; index++)
        {
            var anchor = bundle.SourceAnchors[index];
            issues.AddRange(CanonicalContractValidator.Validate(anchor, document));
            if (!anchorIds.Add(anchor.AnchorId))
                issues.Add(new($"$.sourceAnchors[{index}].anchorId", "duplicate_id", $"Anchor ID '{anchor.AnchorId}' is duplicated."));

            var projectionKey = $"{anchor.ProjectionType}\n{anchor.ProjectionId}";
            if (!projectionKeys.Add(projectionKey))
                issues.Add(new($"$.sourceAnchors[{index}]", "duplicate_projection", "Only one source anchor may own a projection."));
        }

        return issues;
    }

    public static void ValidateOrThrow(CanonicalIngestionBundle bundle)
    {
        var issues = Validate(bundle);
        if (issues.Count > 0)
            throw new CanonicalContractException(issues);
    }

    private static void AddIdentityIssueIf(
        bool condition,
        string path,
        string message,
        List<CanonicalValidationIssue> issues)
    {
        if (condition)
            issues.Add(new(path, "identity_mismatch", message));
    }
}
