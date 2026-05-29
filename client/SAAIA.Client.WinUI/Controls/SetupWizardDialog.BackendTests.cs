using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;

using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Controls;

public sealed partial class SetupWizardDialog
{
    private async void TestReady_Click(object sender, RoutedEventArgs e)
    {
        ReadyStatusText.Text = SZ("Test…", "Testing…", "Probando…", "A testar…", "Test…", "Test…");
        ReadyRawBox.Visibility = Visibility.Collapsed;
        ReadyRawBox.Text = "";

        // Test the primary URL the user typed AND every alternate URL — the wizard's job
        // is to tell them "which of these is reachable from this machine right now?".
        // Picks the first 2xx-responding URL and runs the deep /ready parse on it; lists
        // the others below so the user knows whether the fallback list is correct.
        var primary = (BackendUrlBox.Text ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(primary))
        {
            ReadyStatusText.Text = SZ("URL backend manquante.", "Missing backend URL.", "URL backend ausente.", "URL do backend em falta.", "Backend-URL fehlt.", "URL backend mancante.");
            return;
        }

        var candidates = new System.Collections.Generic.List<string> { primary };
        var altsRaw = (BackendUrlAlternatesBox.Text ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(altsRaw))
        {
            foreach (var line in altsRaw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var u = line.Trim().TrimEnd('/');
                if (u.Length > 0 && !candidates.Contains(u, StringComparer.OrdinalIgnoreCase))
                    candidates.Add(u);
            }
        }

        try
        {
            // Probe in parallel; first 2xx wins. 4s timeout per URL keeps the wizard
            // feeling snappy even when one of the URLs is unreachable on this network.
            var pick = await BackendUrlPicker.PickAsync(candidates, TimeSpan.FromSeconds(4), CancellationToken.None);

            if (pick.Url is null)
            {
                ReadyStatusText.Text = SZ(
                    $"Aucune URL ne répond. ({pick.FailureSummary})",
                    $"No URL responded. ({pick.FailureSummary})",
                    $"Ninguna URL respondió. ({pick.FailureSummary})",
                    $"Nenhum URL respondeu. ({pick.FailureSummary})",
                    $"Keine URL hat geantwortet. ({pick.FailureSummary})",
                    $"Nessun URL ha risposto. ({pick.FailureSummary})");
                return;
            }

            // Got a reachable URL. Parse /ready on it for the deep DB/TEI/Qdrant summary.
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            using var resp = await http.GetAsync(pick.Url + "/ready");
            var raw = await resp.Content.ReadAsStringAsync();
            var httpOk = resp.IsSuccessStatusCode;
            var summary = ParseReadySummary(raw);
            var ok = httpOk && summary.BodyOk;

            // Tag the active URL in the status line so the user knows which one matched.
            var label = string.Equals(pick.Url, primary, StringComparison.OrdinalIgnoreCase)
                ? SZ("URL principale", "primary URL", "URL principal", "URL principal", "Haupt-URL", "URL principale")
                : $"{SZ("via alternative", "via alternate", "vía alternativa", "via alternativa", "via Alternative", "via alternativa")}: {pick.Url}";

            if (ok)
            {
                ReadyStatusText.Text = $"OK ({label}) — " + FormatComponentList(summary);
                if (!string.IsNullOrWhiteSpace(summary.FirstWarning))
                {
                    ReadyRawBox.Text = ExplainComponentWarning(summary.FirstWarning!);
                    ReadyRawBox.Visibility = Visibility.Visible;
                }
            }
            else
            {
                var code = (int)resp.StatusCode;
                ReadyStatusText.Text = $"HTTP {code} ({label}) — {FormatComponentList(summary)}";

                if (!string.IsNullOrWhiteSpace(summary.FirstError))
                {
                    ReadyRawBox.Text = ExplainComponentError(summary.FirstError!);
                    ReadyRawBox.Visibility = Visibility.Visible;
                }
            }
        }
        catch (TaskCanceledException)
        {
            ReadyStatusText.Text = SZ("Timeout.", "Timeout.", "Timeout.", "Timeout.", "Timeout.", "Timeout.");
        }
        catch (Exception ex)
        {
            ReadyStatusText.Text = SZ("Échec : ", "Failed: ", "Fallo: ", "Falha: ", "Fehler: ", "Errore: ") + ShortErr(ex);
        }
    }

    internal readonly record struct ReadySummary(
        bool BodyOk,
        bool? Db,
        bool? Tei,
        bool? Qdrant,
        string? Llm,
        bool? OcrEnabled,
        bool? OcrReady,
        string? FirstError,
        string? FirstWarning);

    internal static ReadySummary ParseReadySummary(string raw)
    {
        bool bodyOk = false;
        bool? db = null, tei = null, qdrant = null, ocrEnabled = null, ocrReady = null;
        string? llm = null;
        string? firstError = null;
        string? firstWarning = null;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("ok", out var p) && p.ValueKind == JsonValueKind.True)
                bodyOk = true;

            if (doc.RootElement.TryGetProperty("details", out var details) &&
                details.ValueKind == JsonValueKind.Object)
            {
                if (details.TryGetProperty("db", out var dbProp))
                    db = dbProp.ValueKind == JsonValueKind.True;
                if (details.TryGetProperty("tei", out var teiProp))
                    tei = teiProp.ValueKind == JsonValueKind.True;
                if (details.TryGetProperty("qdrant", out var qProp))
                    qdrant = qProp.ValueKind == JsonValueKind.True;
                if (details.TryGetProperty("llm", out var llmProp) && llmProp.ValueKind == JsonValueKind.String)
                    llm = llmProp.GetString();
                if (details.TryGetProperty("ocr_enabled", out var ocrEnabledProp))
                    ocrEnabled = ocrEnabledProp.ValueKind == JsonValueKind.True;
                if (details.TryGetProperty("ocr_ready", out var ocrReadyProp))
                    ocrReady = ocrReadyProp.ValueKind == JsonValueKind.True;

                // Surface the first component-level *_error string as the actionable hint.
                foreach (var prop in details.EnumerateObject())
                {
                    if (!prop.Name.EndsWith("_error", StringComparison.Ordinal)) continue;
                    if (prop.Value.ValueKind != JsonValueKind.String) continue;
                    var v = prop.Value.GetString();
                    if (string.IsNullOrWhiteSpace(v)) continue;
                    firstError = $"{prop.Name}: {v}";
                    break;
                }

                foreach (var prop in details.EnumerateObject())
                {
                    if (!prop.Name.EndsWith("_warning", StringComparison.Ordinal)) continue;
                    if (prop.Value.ValueKind != JsonValueKind.String) continue;
                    var v = prop.Value.GetString();
                    if (string.IsNullOrWhiteSpace(v)) continue;
                    firstWarning = $"{prop.Name}: {v}";
                    break;
                }
            }
        }
        catch
        {
            // Non-JSON or unexpected shape — leave fields null; caller will say "non interpretable".
        }

        return new ReadySummary(bodyOk, db, tei, qdrant, llm, ocrEnabled, ocrReady, firstError, firstWarning);
    }

    private string FormatComponentList(ReadySummary s)
    {
        var parts = new System.Collections.Generic.List<string>(5);
        if (s.Db is bool d) parts.Add("DB " + Mark(d));
        if (s.Tei is bool t) parts.Add("TEI " + Mark(t));
        if (s.Qdrant is bool q) parts.Add("Qdrant " + Mark(q));
        if (s.OcrEnabled == true && s.OcrReady is bool ocr) parts.Add("OCR " + Mark(ocr));
        if (!string.IsNullOrWhiteSpace(s.Llm)) parts.Add(SZ("Moteur résumés ", "Summary engine ", "Motor de resúmenes ", "Motor de resumos ", "Zusammenfassungs-Engine ", "Motore riassunti ") + s.Llm);

        if (parts.Count == 0)
            return SZ("réponse non interprétable", "unparseable response", "respuesta no interpretable", "resposta não interpretável", "Antwort nicht interpretierbar", "risposta non interpretabile");

        return string.Join(" · ", parts);
    }

    private string Mark(bool ok)
        => ok ? "OK" : "KO";

    // Translate raw backend component errors into something an integrator can act on.
    // The Qdrant case is the headline one — users keep confusing the SAAIA API key
    // (this dialog) with the backend↔Qdrant internal credential (set on the server).
    private string ExplainComponentError(string raw)
    {
        var lower = raw.ToLowerInvariant();

        if (lower.StartsWith("qdrant_error", StringComparison.Ordinal)
            && (lower.Contains("401") || lower.Contains("unauthorized") || lower.Contains("invalid api key") || lower.Contains("jwt")))
        {
            return SZ(
                "Qdrant : authentification interne refusée (401). " +
                "⚠ Cette clé n'est PAS la clé API SAAIA ci-dessus — c'est la clé que le backend utilise pour parler à Qdrant. " +
                "Action côté serveur : vérifier QDRANT_API_KEY (ou jwt) dans la config du backend, puis redémarrer.",

                "Qdrant: internal authentication refused (401). " +
                "⚠ This is NOT the SAAIA API key shown above — it is the credential the backend uses to talk to Qdrant. " +
                "Server-side fix: check QDRANT_API_KEY (or jwt) in the backend config, then restart.",

                "Qdrant: autenticación interna rechazada (401). " +
                "⚠ Esta NO es la clave API SAAIA mostrada arriba — es la credencial que el backend usa para hablar con Qdrant. " +
                "Acción en el servidor: verifica QDRANT_API_KEY (o jwt) en la configuración del backend y reinícialo.",

                "Qdrant: autenticação interna recusada (401). " +
                "⚠ Esta NÃO é a chave API SAAIA mostrada acima — é a credencial que o backend usa para falar com o Qdrant. " +
                "Ação no servidor: verifica QDRANT_API_KEY (ou jwt) na configuração do backend e reinicia.",

                "Qdrant: interne Authentifizierung abgelehnt (401). " +
                "⚠ Dies ist NICHT der oben gezeigte SAAIA-API-Schluessel — es ist die Credential, die das Backend nutzt, um mit Qdrant zu sprechen. " +
                "Server-Seite: pruefe QDRANT_API_KEY (oder jwt) in der Backend-Konfiguration und starte neu.",

                "Qdrant: autenticazione interna rifiutata (401). " +
                "⚠ Questa NON è la chiave API SAAIA mostrata sopra — è la credenziale che il backend usa per parlare con Qdrant. " +
                "Lato server: controlla QDRANT_API_KEY (o jwt) nella configurazione del backend e riavvia.");
        }

        if (lower.StartsWith("tei_error", StringComparison.Ordinal))
        {
            var detail = ComponentErrorDetail(raw, "tei_error");
            return SZ(
                $"TEI (embeddings) : {detail}. Vérifier que le service TEI tourne côté backend.",
                $"TEI (embeddings): {detail}. Check the TEI service is running on the backend.",
                $"TEI (embeddings): {detail}. Comprueba que el servicio TEI esté activo en el backend.",
                $"TEI (embeddings): {detail}. Verifica se o serviço TEI está a correr no backend.",
                $"TEI (Embeddings): {detail}. Pruefe, ob der TEI-Dienst im Backend laeuft.",
                $"TEI (embeddings): {detail}. Verifica che il servizio TEI sia attivo nel backend.");
        }

        if (lower.StartsWith("db_error", StringComparison.Ordinal))
        {
            var detail = ComponentErrorDetail(raw, "db_error");
            return SZ(
                $"Base de données : {detail}. Problème côté backend (Postgres ou config).",
                $"Database: {detail}. Backend-side issue (Postgres or config).",
                $"Base de datos: {detail}. Problema del lado del backend (Postgres o config).",
                $"Base de dados: {detail}. Problema no backend (Postgres ou config).",
                $"Datenbank: {detail}. Problem im Backend (Postgres oder Config).",
                $"Database: {detail}. Problema lato backend (Postgres o config).");
        }

        if (lower.StartsWith("ocr_error", StringComparison.Ordinal))
        {
            var detail = ComponentErrorDetail(raw, "ocr_error");
            return SZ(
                $"OCR : {detail}. L'OCR est activé côté serveur, mais un exécutable requis manque ou n'est pas accessible.",
                $"OCR: {detail}. OCR is enabled on the server, but a required executable is missing or unavailable.",
                $"OCR: {detail}. El OCR está activado en el servidor, pero falta un ejecutable requerido o no está disponible.",
                $"OCR: {detail}. O OCR está ativado no servidor, mas falta um executável obrigatório ou este não está disponível.",
                $"OCR: {detail}. OCR ist serverseitig aktiviert, aber ein erforderliches Programm fehlt oder ist nicht erreichbar.",
                $"OCR: {detail}. L'OCR è attivo sul server, ma un eseguibile richiesto manca o non è disponibile.");
        }

        // Fallback: show the raw component error as-is.
        return raw;
    }

    private string ExplainComponentWarning(string raw)
    {
        var lower = raw.ToLowerInvariant();

        if (lower.StartsWith("ocr_warning", StringComparison.Ordinal)
            && lower.Contains("ocr_languages_not_verified", StringComparison.Ordinal))
        {
            return SZ(
                "OCR : le serveur répond, mais les langues OCR configurées n'ont pas pu être vérifiées. L'OCR peut fonctionner, mais il faut confirmer que les paquets de langue Tesseract requis sont bien installés côté serveur.",
                "OCR: the server responds, but the configured OCR languages could not be verified. OCR may work, but confirm that the required Tesseract language packs are installed on the server.",
                "OCR: el servidor responde, pero no se pudieron verificar los idiomas OCR configurados. El OCR puede funcionar, pero confirma que los paquetes de idioma de Tesseract necesarios estén instalados en el servidor.",
                "OCR: o servidor responde, mas os idiomas OCR configurados não puderam ser verificados. O OCR pode funcionar, mas confirma que os pacotes de idioma Tesseract necessários estão instalados no servidor.",
                "OCR: Der Server antwortet, aber die konfigurierten OCR-Sprachen konnten nicht verifiziert werden. OCR kann funktionieren, aber pruefe, ob die benoetigten Tesseract-Sprachpakete auf dem Server installiert sind.",
                "OCR: il server risponde, ma non è stato possibile verificare le lingue OCR configurate. L'OCR può funzionare, ma verifica che i pacchetti lingua Tesseract richiesti siano installati sul server.");
        }

        return raw;
    }

    private static string ComponentErrorDetail(string raw, string prefix)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var text = raw.Trim();
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring(prefix.Length).TrimStart();
            if (text.StartsWith(":", StringComparison.Ordinal))
                text = text.Substring(1).TrimStart();
        }

        return string.IsNullOrWhiteSpace(text) ? raw.Trim() : text;
    }

    private async void TestApiKey_Click(object sender, RoutedEventArgs e)
    {
        ApiKeyStatusText.Text = SZ("Test…", "Testing…", "Probando…", "A testar…", "Test…", "Test…");

        try
        {
            var apiKey = (ApiKeyBox.Password ?? "").Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                ApiKeyStatusText.Text = SZ("Clé vide.", "Empty key.", "Clave vacía.", "Chave vazia.", "Schluessel leer.", "Chiave vuota.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_userId))
            {
                ApiKeyStatusText.Text = SZ("userId client manquant.", "Missing client userId.", "Falta userId cliente.", "Falta userId cliente.", "Client-userId fehlt.", "userId client mancante.");
                return;
            }

            // Direct HTTP test against /chat/sessions so we can show the raw HTTP status code on failure
            // (the high-level ApiClient swallows it inside HttpRequestException's generic message).
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var req = new HttpRequestMessage(HttpMethod.Post, _backendUrl.TrimEnd('/') + "/chat/sessions");
            req.Headers.Add("X-Api-Key", apiKey);
            var bodyJson = JsonSerializer.Serialize(new
            {
                userId = _userId,
                title = "setup-test",
                clientUser = Environment.UserName
            });
            req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

            using var resp = await http.SendAsync(req);
            var raw = await resp.Content.ReadAsStringAsync();

            if (resp.IsSuccessStatusCode)
            {
                // Best-effort cleanup of the test session.
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    if (doc.RootElement.TryGetProperty("sessionId", out var sidProp) &&
                        sidProp.ValueKind == JsonValueKind.String)
                    {
                        var sid = sidProp.GetString();
                        if (!string.IsNullOrWhiteSpace(sid))
                        {
                            using var delReq = new HttpRequestMessage(HttpMethod.Delete,
                                $"{_backendUrl.TrimEnd('/')}/chat/sessions/{sid}?userId={Uri.EscapeDataString(_userId)}");
                            delReq.Headers.Add("X-Api-Key", apiKey);
                            using var _ = await http.SendAsync(delReq);
                        }
                    }
                }
                catch
                {
                    // Cleanup is best-effort; do not fail the test on it.
                }

                ApiKeyStatusText.Text = SZ("Clé OK.", "Key OK.", "Clave OK.", "Chave OK.", "Schluessel OK.", "Chiave OK.");
                return;
            }

            // Failure: show a short, actionable message based on HTTP status.
            var code = (int)resp.StatusCode;
            var hint = code switch
            {
                401 => SZ("clé invalide ou révoquée", "invalid or revoked key", "clave inválida o revocada", "chave inválida ou revogada", "ungueltig oder widerrufen", "non valida o revocata"),
                403 => SZ("clé non autorisée", "key not authorized", "clave no autorizada", "chave não autorizada", "Schluessel nicht autorisiert", "chiave non autorizzata"),
                404 => SZ("endpoint introuvable (URL backend ?)", "endpoint not found (backend URL?)", "endpoint no encontrado (URL backend?)", "endpoint não encontrado (URL backend?)", "Endpoint nicht gefunden (Backend-URL?)", "endpoint non trovato (URL backend?)"),
                _ => SZ("voir logs serveur", "see server logs", "ver logs del servidor", "ver logs do servidor", "Server-Logs ansehen", "vedi log server"),
            };
            ApiKeyStatusText.Text = $"HTTP {code} — {hint}";
        }
        catch (TaskCanceledException)
        {
            ApiKeyStatusText.Text = SZ("Timeout (10s).", "Timeout (10s).", "Timeout (10s).", "Timeout (10s).", "Timeout (10s).", "Timeout (10s).");
        }
        catch (Exception ex)
        {
            // Network-level error (DNS, refused, TLS...). Keep it short.
            ApiKeyStatusText.Text = SZ("Échec : ", "Failed: ", "Fallo: ", "Falha: ", "Fehler: ", "Errore: ") + ShortErr(ex);
        }
    }

    private static string ShortErr(Exception ex)
    {
        var m = ex.Message ?? "";
        if (m.Length > 120) m = m.Substring(0, 117) + "...";
        return m;
    }
}
