using System;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Controls;

public sealed partial class SetupWizardDialog : ContentDialog
{
    private readonly string _backendUrl;
    private readonly string _userId;
    private readonly LlamaCppProcessManager _llmProc;
    private readonly string _uiLanguage;

    public bool Applied { get; private set; }

    // NOTE: ctor is internal because it takes internal service types (AppSettings, LlamaCppProcessManager).
    // This dialog is only instantiated from within the WinUI client assembly.
    internal SetupWizardDialog(
        string backendUrl,
        string userId,
        string apiKeyInitial,
        AppSettings settingsInitial,
        LlamaCppProcessManager llmProc)
    {
        InitializeComponent();

        _backendUrl = (backendUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(_backendUrl)) _backendUrl = "http://localhost:5122";

        _userId = (userId ?? "").Trim();
        _llmProc = llmProc;
        _uiLanguage = ClientUiText.NormalizeLanguage((settingsInitial ?? AppSettings.Load()).UiLanguage);

        BackendUrlBox.Text = _backendUrl;
        ApiKeyBox.Password = apiKeyInitial ?? "";

        SeedUiFromSettings(settingsInitial ?? AppSettings.Load());
        ApplyUiTexts();
        ResetStatusTexts();
    }

    private void SeedUiFromSettings(AppSettings settings)
    {
        var s = settings ?? AppSettings.Load();

        // End-user mode: hide advanced LLM section (paths/process management).
        // Integrator mode: show it.
        LlmSection.Visibility = s.ShowAdvancedUi ? Visibility.Visible : Visibility.Collapsed;

        UseLocalLlmCheck.IsChecked = s.UseLocalLlm;
        AutoStartCheck.IsChecked = s.AutoStartOnConnect;
        LlamaExeBox.Text = s.LlamaExePath ?? "";
        ModelPathBox.Text = s.ModelPath ?? "";
        HostBox.Text = string.IsNullOrWhiteSpace(s.Host) ? "127.0.0.1" : s.Host;
        PortBox.Text = s.Port <= 0 ? "1234" : s.Port.ToString();
        ModelIdBox.Text = s.ModelId ?? "";
        ExtraArgsBox.Text = s.ExtraArgs ?? "";
    }

    private void ResetStatusTexts()
    {
        ReadyStatusText.Text = "";
        ApiKeyStatusText.Text = "";
        LlmStatusText.Text = "";
    }

    private void ApplyUiTexts()
    {
        Title = SZ("Configuration initiale", "Initial setup", "Configuracion inicial", "Configuracao inicial", "Ersteinrichtung", "Configurazione iniziale");
        PrimaryButtonText = SZ("Appliquer", "Apply", "Aplicar", "Aplicar", "Anwenden", "Applica");
        CloseButtonText = SZ("Annuler", "Cancel", "Cancelar", "Cancelar", "Abbrechen", "Annulla");

        BackendSectionTitleText.Text = SZ("Backend", "Backend", "Backend", "Backend", "Backend", "Backend");
        BackendSectionNoteText.Text = SZ(
            "Le serveur SAAIA (RAG) est on-prem. Cette etape verifie /ready.",
            "The SAAIA backend (RAG) is on-prem. This step checks /ready.",
            "El backend SAAIA (RAG) es on-prem. Este paso verifica /ready.",
            "O backend SAAIA (RAG) e on-prem. Este passo verifica /ready.",
            "Das SAAIA-Backend (RAG) ist on-prem. Dieser Schritt prueft /ready.",
            "Il backend SAAIA (RAG) e on-prem. Questo passaggio verifica /ready.");
        TestReadyButton.Content = SZ("Tester /ready", "Test /ready", "Probar /ready", "Testar /ready", "Test /ready", "Test /ready");

        ApiKeySectionTitleText.Text = SZ("Cle API", "API key", "Clave API", "Chave API", "API-Schluessel", "Chiave API");
        ApiKeySectionNoteText.Text = SZ(
            "Cette cle est stockee localement (DPAPI) et sert a acceder au chat-store (sessions/messages).",
            "This key is stored locally (DPAPI) and is used to access the chat store (sessions/messages).",
            "Esta clave se almacena localmente (DPAPI) y sirve para acceder al chat-store (sesiones/mensajes).",
            "Esta chave e armazenada localmente (DPAPI) e serve para aceder ao chat-store (sessoes/mensagens).",
            "Dieser Schluessel wird lokal gespeichert (DPAPI) und dient fuer den Zugriff auf den Chat-Store (Sitzungen/Nachrichten).",
            "Questa chiave e memorizzata localmente (DPAPI) e serve per accedere al chat-store (sessioni/messaggi).");
        ApiKeyBox.PlaceholderText = SZ("X-Api-Key", "X-Api-Key", "X-Api-Key", "X-Api-Key", "X-Api-Key", "X-Api-Key");
        TestApiKeyButton.Content = SZ("Tester la cle API", "Test API key", "Probar la clave API", "Testar a chave API", "API-Schluessel testen", "Testa la chiave API");

        LlmSectionTitleText.Text = SZ("LLM local (avance)", "Local LLM (advanced)", "LLM local (avanzado)", "LLM local (avancado)", "Lokales LLM (erweitert)", "LLM locale (avanzato)");
        LlmSectionNoteText.Text = SZ(
            "Section avancee (integrateur). En usage standard, l'app utilise un endpoint OpenAI-compatible deja lance (ex: Docker).",
            "Advanced section (integrator). In standard usage, the app uses an OpenAI-compatible endpoint that is already running (for example Docker).",
            "Seccion avanzada (integrador). En uso normal, la app utiliza un endpoint compatible con OpenAI que ya esta en ejecucion (por ejemplo Docker).",
            "Secao avancada (integrador). Em uso normal, a app utiliza um endpoint compativel com OpenAI ja iniciado (por exemplo Docker).",
            "Erweiterter Bereich (Integrator). Im Standardbetrieb verwendet die App einen bereits laufenden OpenAI-kompatiblen Endpoint (zum Beispiel Docker).",
            "Sezione avanzata (integratore). In uso standard, l'app utilizza un endpoint compatibile OpenAI gia avviato (per esempio Docker).");
        UseLocalLlmCheck.Content = SZ("Activer l'assistant (LLM)", "Enable assistant (LLM)", "Activar asistente (LLM)", "Ativar assistente (LLM)", "Assistenten aktivieren (LLM)", "Attiva assistente (LLM)");
        AutoStartCheck.Content = SZ("Demarrer automatiquement le processus LLM local a la connexion", "Auto-start local LLM process on Connect", "Iniciar automaticamente el proceso LLM local al conectar", "Iniciar automaticamente o processo LLM local ao ligar", "Lokalen LLM-Prozess beim Verbinden automatisch starten", "Avvia automaticamente il processo LLM locale alla connessione");
        LlamaExeLabelText.Text = SZ("Executable serveur (llama-server.exe)", "Server executable (llama-server.exe)", "Ejecutable del servidor (llama-server.exe)", "Executavel do servidor (llama-server.exe)", "Server-Executable (llama-server.exe)", "Eseguibile server (llama-server.exe)");
        LlamaExeBox.PlaceholderText = SZ(@"C:\SAAIA\llm\llama-server.exe", @"C:\SAAIA\llm\llama-server.exe", @"C:\SAAIA\llm\llama-server.exe", @"C:\SAAIA\llm\llama-server.exe", @"C:\SAAIA\llm\llama-server.exe", @"C:\SAAIA\llm\llama-server.exe");
        ModelPathLabelText.Text = SZ("Modele (.gguf)", "Model (.gguf)", "Modelo (.gguf)", "Modelo (.gguf)", "Modell (.gguf)", "Modello (.gguf)");
        ModelPathBox.PlaceholderText = SZ(@"C:\SAAIA\models\model.gguf", @"C:\SAAIA\models\model.gguf", @"C:\SAAIA\models\model.gguf", @"C:\SAAIA\models\model.gguf", @"C:\SAAIA\models\model.gguf", @"C:\SAAIA\models\model.gguf");
        HostLabelText.Text = SZ("Hote", "Host", "Host", "Host", "Host", "Host");
        HostBox.PlaceholderText = SZ("127.0.0.1", "127.0.0.1", "127.0.0.1", "127.0.0.1", "127.0.0.1", "127.0.0.1");
        PortLabelText.Text = SZ("Port", "Port", "Puerto", "Porta", "Port", "Porta");
        PortBox.PlaceholderText = SZ("1234", "1234", "1234", "1234", "1234", "1234");
        ModelIdLabelText.Text = SZ("ID modele (OpenAI 'model')", "Model ID (OpenAI 'model')", "ID del modelo (OpenAI 'model')", "ID do modelo (OpenAI 'model')", "Modell-ID (OpenAI 'model')", "ID modello (OpenAI 'model')");
        ModelIdBox.PlaceholderText = SZ("model-id.gguf", "model-id.gguf", "model-id.gguf", "model-id.gguf", "model-id.gguf", "model-id.gguf");
        ExtraArgsLabelText.Text = SZ("Arguments supplementaires (optionnel)", "Extra args (optional)", "Argumentos extra (opcional)", "Argumentos extra (opcional)", "Zusaetzliche Argumente (optional)", "Argomenti extra (opzionale)");
        TestModelsButton.Content = SZ("Tester /v1/models", "Test /v1/models", "Probar /v1/models", "Testar /v1/models", "Test /v1/models", "Test /v1/models");
        StartLocalLlmButton.Content = SZ("Demarrer le LLM local", "Start local LLM", "Iniciar LLM local", "Iniciar LLM local", "Lokales LLM starten", "Avvia LLM locale");
        StopLocalLlmButton.Content = SZ("Arreter", "Stop", "Detener", "Parar", "Stoppen", "Ferma");
    }

    private string SZ(string fr, string en, string es, string pt, string de, string it)
        => _uiLanguage switch
        {
            "en" => en,
            "es" => es,
            "pt" => pt,
            "de" => de,
            "it" => it,
            _ => fr
        };
}
