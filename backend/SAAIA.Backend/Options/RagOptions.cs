sealed class RagOptions
{
    public string QdrantBaseUrl { get; set; } = "http://localhost:6333/";
    public string QdrantCollection { get; set; } = "knowledge_base";

    public string EmbeddingsBaseUrl { get; set; } = "http://localhost:8081/";
    public string EmbeddingsModel { get; set; } = "intfloat/multilingual-e5-base";

    public int DefaultTopK { get; set; } = 5;
    public int MaxTopK { get; set; } = 20;

    // ==========================
    // Backward compatible aliases
    // ==========================
    // Anciennes clés JSON : TeiBaseUrl / CollectionName
    public string TeiBaseUrl { get => EmbeddingsBaseUrl; set => EmbeddingsBaseUrl = value; }
    public string CollectionName { get => QdrantCollection; set => QdrantCollection = value; }
}
