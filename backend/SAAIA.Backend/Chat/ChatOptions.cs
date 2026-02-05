namespace SAAIA.Backend.Chat;

public sealed class ChatOptions
{
    // LLM (llama.cpp OpenAI-compatible)
    public string LlmBaseUrl { get; set; } = "http://127.0.0.1:1234";

    // IMPORTANT: chez toi, llama.cpp expose un alias (LLM_MODEL_ID) = "local" qui pointe vers qwen2.5
    public string LlmModel { get; set; } = "local";
    public string? LlmApiKey { get; set; }

    // Ollama fallback (optionnel)
    public bool EnableOllamaFallback { get; set; } = false;
    public string? OllamaBaseUrl { get; set; } = "http://localhost:11434";
    public string? OllamaModel { get; set; } = "qwen2.5:7b-instruct";

    // Timeouts (gérés via CancellationToken côté endpoint)
    public int LlmTimeoutSeconds { get; set; } = 120;
    public int LlmFirstTokenTimeoutSeconds { get; set; } = 25;

    // Génération (clés canoniques)
    public double LlmTemperature { get; set; } = 0.2;
    public int LlmMaxTokens { get; set; } = 256;

    // ==========================
    // Backward compatible aliases
    // ==========================
    // Anciennes clés JSON : Temperature / MaxTokens
    public double Temperature { get => LlmTemperature; set => LlmTemperature = value; }
    public int MaxTokens { get => LlmMaxTokens; set => LlmMaxTokens = value; }

    // Prompt : optionnel, injecté en tête (sans retirer les règles RAG strictes)
    public string SystemPrompt { get; set; } = "";

    // Streaming UI
    public int StreamFlushChars { get; set; } = 128;
    public int PingIntervalSeconds { get; set; } = 5;

    // Prompt sizing
    public int MaxContextChars { get; set; } = 6000;
    public int PromptMaxSources { get; set; } = 4;
    public int PromptMaxCharsPerSource { get; set; } = 450;

    // ==================
    // Retrieval (sélection des sources)
    // ==================

    public int RetrievalCandidateMultiplier { get; set; } = 4;
    public int RetrievalShingleWords { get; set; } = 5;
    public double RetrievalNearDuplicateJaccard { get; set; } = 0.85;
    public double RetrievalLowConfidenceScore { get; set; } = 0.72;
    public double RetrievalOkScore { get; set; } = 0.78;

    // Réponse (contraintes)
    public int ResponseMaxWords { get; set; } = 160;
    public int ResponseMaxSentences { get; set; } = 10;
    public int ResponseMaxWordsPerSentence { get; set; } = 22;

    // Citations
    public bool AppendCitationsAtEnd { get; set; } = true;

    // Fallback retrieval-only (quand LLM timeout)
    public int FallbackMaxSources { get; set; } = 3;
    public int FallbackMaxCharsPerSource { get; set; } = 900;

    // Limiteur
    public int MaxConcurrentGenerations { get; set; } = 2;
    public int MaxQueueLength { get; set; } = 10;
}
