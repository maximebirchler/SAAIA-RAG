namespace SAAIA.Client.WinUI.Services;

/// <summary>
/// Valeurs par défaut client (non sensibles).
/// NOTE: ces valeurs sont utilisées quand l'utilisateur n'a pas encore configuré l'app.
/// </summary>
internal static class ClientDefaults
{
    // Backend (API RAG + chat-store)
    public const string BackendBaseUrl = "http://localhost:5122";

    // LLM OpenAI-compatible local (llama.cpp server)
    public const string LlmBaseUrl = "http://127.0.0.1:1234/v1";

    // IMPORTANT: doit matcher l'ID renvoyé par /v1/models (dans llama.cpp c'est souvent le nom du fichier GGUF)
    // On met un modèle "safe" et léger par défaut (compatible CPU/iGPU).
    // Qwen3 Q5 est le gagnant du benchmark fonctionnel RAG du 24-25.07.2026.
    public const string LlmModel = "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf";

    // Empty means "search the whole corpus". A concrete category here would silently
    // filter normal user questions and hide relevant documents from other folders.
    public const string DefaultCategory = "";
}
