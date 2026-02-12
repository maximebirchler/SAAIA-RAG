namespace SAAIA.Client.WinUI.Services;

internal static class ClientDefaults
{
    // Backend (API RAG + chat-store)
    public const string BackendBaseUrl = "http://localhost:5122";

    // LLM OpenAI-compat (ton port docker: 127.0.0.1:1234 -> container:8080)
    public const string LlmBaseUrl = "http://127.0.0.1:1234/v1";

    // IMPORTANT: doit matcher l'ID renvoyé par /v1/models (souvent = nom du fichier GGUF)
    public const string LlmModel = "Mistral-7B-Instruct-v0.3-IQ3_M.gguf";

    public const string DefaultCategory = "general";
}
