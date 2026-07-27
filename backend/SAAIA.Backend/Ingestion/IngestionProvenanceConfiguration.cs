internal static class IngestionProvenanceConfiguration
{
    public static string Validate(RagOptions rag, string? codeRevision)
    {
        ArgumentNullException.ThrowIfNull(rag);
        ArgumentException.ThrowIfNullOrWhiteSpace(rag.EmbeddingsModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(rag.EmbeddingsModelRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(rag.EmbeddingsRuntimeRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(rag.QdrantRuntimeRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeRevision);

        var normalizedCodeRevision = codeRevision.Trim();
        if (string.Equals(
                normalizedCodeRevision,
                "unknown",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The code revision must identify the deployed source.",
                nameof(codeRevision));
        }

        return normalizedCodeRevision;
    }

    public static bool TryValidate(
        RagOptions rag,
        string? codeRevision,
        out string? error)
    {
        try
        {
            Validate(rag, codeRevision);
            error = null;
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException
            or ArgumentNullException)
        {
            error = ex.Message;
            return false;
        }
    }
}
