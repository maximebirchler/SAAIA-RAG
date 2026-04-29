using System;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Controls;

public sealed partial class SetupWizardDialog : ContentDialog
{
    private string _backendUrl;
    private readonly string _userId;
    private readonly LlamaCppProcessManager _llmProc;
    private readonly string _uiLanguage;

    // Set by the host (MainWindow) before showing — invoked when the user clicks Apply
    // or Cancel inside the overlay shell. Replaces the old ContentDialog ShowAsync flow.
    private Action? _overlayCloseAction;

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
        BackendUrlAlternatesBox.Text = (settingsInitial ?? AppSettings.Load()).BackendUrlAlternates ?? "";
        ApiKeyBox.Password = apiKeyInitial ?? "";

        SeedUiFromSettings(settingsInitial ?? AppSettings.Load());
        ApplyUiTexts();
        ResetStatusTexts();
    }

    // Detach the WizardShell so the host can present it inside MainWindow's overlay system
    // (smoke + presenter) — same pattern as UserSettingsDialog. The dialog itself is never
    // shown via ShowAsync(): that's why Image 2 had a faint ContentDialog rectangle leaking.
    internal UIElement DetachContentForOverlay(Action closeAction)
    {
        _overlayCloseAction = closeAction ?? throw new ArgumentNullException(nameof(closeAction));

        if (Content is UIElement existingContent)
        {
            Content = null;
            return existingContent;
        }

        return WizardShell;
    }

    private void WizardApplyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var apiKey = (ApiKeyBox.Password ?? "").Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                ApiKeyStatusText.Text = SZ("Clé API requise.", "API key required.", "Se requiere clave API.", "Chave API obrigatória.", "API-Schluessel erforderlich.", "Chiave API richiesta.");
                return;
            }

            SecureLocalStore.SetServerApiKey(apiKey);

            var s = ReadSettingsFromUi();
            s.Save();

            Applied = true;
            _overlayCloseAction?.Invoke();
        }
        catch (Exception ex)
        {
            ApiKeyStatusText.Text = SZ("Échec de l'application : ", "Apply failed: ", "Error al aplicar: ", "Falha ao aplicar: ", "Anwenden fehlgeschlagen: ", "Applicazione non riuscita: ") + ex.Message;
        }
    }

    private void WizardCancelButton_Click(object sender, RoutedEventArgs e)
    {
        Applied = false;
        _overlayCloseAction?.Invoke();
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

        // Show "lancer un processus local" UI only when an llama-server.exe is actually
        // configured. Otherwise the LLM is reached via Docker / remote and the Démarrer
        // button would just produce the contradictory "no executable configured" hint.
        UpdateLocalProcessSectionVisibility();
        LlamaExeBox.TextChanged += (_, __) => UpdateLocalProcessSectionVisibility();
        UseLocalLlmCheck.Checked += (_, __) => UpdateLocalProcessSectionVisibility();
        UseLocalLlmCheck.Unchecked += (_, __) => UpdateLocalProcessSectionVisibility();
    }

    private void UpdateLocalProcessSectionVisibility()
    {
        var assistantOn = UseLocalLlmCheck.IsChecked == true;
        var hasExe = !string.IsNullOrWhiteSpace(LlamaExeBox.Text);

        // The "manage local process" sub-section (auto-start + paths) only appears
        // when both: assistant is on AND an exe path is set. Same logic for Start/Stop.
        var manageProcess = assistantOn && hasExe;
        LocalLlmProcessSection.Visibility = manageProcess ? Visibility.Visible : Visibility.Collapsed;
        StartLocalLlmButton.Visibility = manageProcess ? Visibility.Visible : Visibility.Collapsed;
        StopLocalLlmButton.Visibility = manageProcess ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ResetStatusTexts()
    {
        ReadyStatusText.Text = "";
        ApiKeyStatusText.Text = "";
        LlmStatusText.Text = "";
    }

    private void ApplyUiTexts()
    {
        // Footer buttons live inside the WizardShell (Image 2 fix) — set their labels here.
        WizardApplyButton.Content = SZ("Appliquer", "Apply", "Aplicar", "Aplicar", "Anwenden", "Applica");
        WizardCancelButton.Content = SZ("Annuler", "Cancel", "Cancelar", "Cancelar", "Abbrechen", "Annulla");

        HeroTitleText.Text = SZ("Configuration initiale", "Initial setup", "Configuracion inicial", "Configuracao inicial", "Ersteinrichtung", "Configurazione iniziale");
        HeroSubtitleText.Text = SZ(
            "Connectez le client au serveur SAAIA et validez votre clé API.",
            "Connect the client to the SAAIA backend and validate your API key.",
            "Conecta el cliente al servidor SAAIA y valida tu clave API.",
            "Liga o cliente ao servidor SAAIA e valida a tua chave API.",
            "Verbinden Sie den Client mit dem SAAIA-Backend und validieren Sie Ihren API-Schluessel.",
            "Collega il client al server SAAIA e convalida la tua chiave API.");

        BackendSectionTitleText.Text = SZ("Serveur SAAIA", "SAAIA backend", "Servidor SAAIA", "Servidor SAAIA", "SAAIA-Backend", "Server SAAIA");
        BackendSectionNoteText.Text = SZ(
            "URL du serveur RAG. Le test vérifie qu'il répond.",
            "RAG backend URL. The test checks it responds.",
            "URL del servidor RAG. La prueba verifica que responde.",
            "URL do servidor RAG. O teste verifica se responde.",
            "URL des RAG-Backends. Der Test prueft, ob es antwortet.",
            "URL del server RAG. Il test verifica che risponda.");
        TestReadyButton.Content = SZ("Tester /ready", "Test /ready", "Probar /ready", "Testar /ready", "Test /ready", "Test /ready");

        BackendAltLabelText.Text = SZ(
            "URLs alternatives (une par ligne, optionnel)",
            "Alternative URLs (one per line, optional)",
            "URLs alternativas (una por línea, opcional)",
            "URLs alternativos (um por linha, opcional)",
            "Alternative URLs (eine pro Zeile, optional)",
            "URL alternativi (uno per riga, opzionale)");
        BackendAltNoteText.Text = SZ(
            "Le client essaiera ces URLs si la principale ne répond pas. Pratique pour roamer entre Wifi (IP locale) et VPN (Tailscale).",
            "The client will try these if the main URL fails. Useful to roam between Wifi (LAN IP) and VPN (Tailscale).",
            "El cliente probará estas URLs si la principal falla. Útil para alternar entre Wifi (IP local) y VPN (Tailscale).",
            "O cliente tentará estes URLs se o principal falhar. Útil para alternar entre Wifi (IP local) e VPN (Tailscale).",
            "Der Client probiert diese URLs, wenn die Haupt-URL nicht antwortet. Praktisch beim Wechsel zwischen Wifi (LAN-IP) und VPN (Tailscale).",
            "Il client proverà questi URL se quello principale non risponde. Utile per spostarsi tra Wifi (IP locale) e VPN (Tailscale).");

        ApiKeySectionTitleText.Text = SZ("Clé API", "API key", "Clave API", "Chave API", "API-Schluessel", "Chiave API");
        ApiKeySectionNoteText.Text = SZ(
            "Clé fournie par l'intégrateur (commence par « saaia_ »). Stockée chiffrée localement.",
            "Key provided by the integrator (starts with 'saaia_'). Stored encrypted locally.",
            "Clave proporcionada por el integrador (empieza por 'saaia_'). Se guarda cifrada localmente.",
            "Chave fornecida pelo integrador (começa por 'saaia_'). Guardada cifrada localmente.",
            "Vom Integrator bereitgestellter Schluessel (beginnt mit 'saaia_'). Lokal verschluesselt gespeichert.",
            "Chiave fornita dall'integratore (inizia con 'saaia_'). Memorizzata cifrata localmente.");
        ApiKeyBox.PlaceholderText = "saaia_…";
        TestApiKeyButton.Content = SZ("Tester la clé API", "Test API key", "Probar la clave API", "Testar a chave API", "API-Schluessel testen", "Testa la chiave API");

        LlmSectionTitleText.Text = SZ("LLM local (intégrateur)", "Local LLM (integrator)", "LLM local (integrador)", "LLM local (integrador)", "Lokales LLM (Integrator)", "LLM locale (integratore)");
        LlmSectionNoteText.Text = SZ(
            "Réservé à l'intégrateur. Laissez tel quel si le LLM tourne déjà (Docker, serveur distant).",
            "Integrator-only. Leave as-is if the LLM already runs (Docker, remote server).",
            "Solo integrador. Dejar como está si el LLM ya está en marcha (Docker, servidor remoto).",
            "Apenas integrador. Deixe como está se o LLM já estiver a correr (Docker, servidor remoto).",
            "Nur Integrator. Unveraendert lassen, wenn das LLM bereits laeuft (Docker, Remote-Server).",
            "Solo integratore. Lasciare invariato se il LLM è già in esecuzione (Docker, server remoto).");
        UseLocalLlmCheck.Content = SZ("Activer l'assistant (LLM)", "Enable assistant (LLM)", "Activar asistente (LLM)", "Ativar assistente (LLM)", "Assistenten aktivieren (LLM)", "Attiva assistente (LLM)");
        AutoStartCheck.Content = SZ("Démarrer automatiquement le processus LLM local à la connexion", "Auto-start local LLM process on Connect", "Iniciar automaticamente el proceso LLM local al conectar", "Iniciar automaticamente o processo LLM local ao ligar", "Lokalen LLM-Prozess beim Verbinden automatisch starten", "Avvia automaticamente il processo LLM locale alla connessione");
        LlamaExeLabelText.Text = SZ("Exécutable serveur (llama-server.exe)", "Server executable (llama-server.exe)", "Ejecutable del servidor (llama-server.exe)", "Executavel do servidor (llama-server.exe)", "Server-Executable (llama-server.exe)", "Eseguibile server (llama-server.exe)");
        LlamaExeBox.PlaceholderText = SZ(@"C:\SAAIA\llm\llama-server.exe", @"C:\SAAIA\llm\llama-server.exe", @"C:\SAAIA\llm\llama-server.exe", @"C:\SAAIA\llm\llama-server.exe", @"C:\SAAIA\llm\llama-server.exe", @"C:\SAAIA\llm\llama-server.exe");
        ModelPathLabelText.Text = SZ("Modèle (.gguf)", "Model (.gguf)", "Modelo (.gguf)", "Modelo (.gguf)", "Modell (.gguf)", "Modello (.gguf)");
        ModelPathBox.PlaceholderText = SZ(@"C:\SAAIA\models\model.gguf", @"C:\SAAIA\models\model.gguf", @"C:\SAAIA\models\model.gguf", @"C:\SAAIA\models\model.gguf", @"C:\SAAIA\models\model.gguf", @"C:\SAAIA\models\model.gguf");
        HostLabelText.Text = SZ("Hôte", "Host", "Host", "Host", "Host", "Host");
        HostBox.PlaceholderText = SZ("127.0.0.1", "127.0.0.1", "127.0.0.1", "127.0.0.1", "127.0.0.1", "127.0.0.1");
        PortLabelText.Text = SZ("Port", "Port", "Puerto", "Porta", "Port", "Porta");
        PortBox.PlaceholderText = SZ("1234", "1234", "1234", "1234", "1234", "1234");
        ModelIdLabelText.Text = SZ("ID modèle (OpenAI 'model')", "Model ID (OpenAI 'model')", "ID del modelo (OpenAI 'model')", "ID do modelo (OpenAI 'model')", "Modell-ID (OpenAI 'model')", "ID modello (OpenAI 'model')");
        ModelIdBox.PlaceholderText = SZ("model-id.gguf", "model-id.gguf", "model-id.gguf", "model-id.gguf", "model-id.gguf", "model-id.gguf");
        ExtraArgsLabelText.Text = SZ("Arguments supplémentaires (optionnel)", "Extra args (optional)", "Argumentos extra (opcional)", "Argumentos extra (opcional)", "Zusaetzliche Argumente (optional)", "Argomenti extra (opzionale)");
        TestModelsButton.Content = SZ("Tester /v1/models", "Test /v1/models", "Probar /v1/models", "Testar /v1/models", "Test /v1/models", "Test /v1/models");
        StartLocalLlmButton.Content = SZ("Démarrer le LLM local", "Start local LLM", "Iniciar LLM local", "Iniciar LLM local", "Lokales LLM starten", "Avvia LLM locale");
        StopLocalLlmButton.Content = SZ("Arrêter", "Stop", "Detener", "Parar", "Stoppen", "Ferma");
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
