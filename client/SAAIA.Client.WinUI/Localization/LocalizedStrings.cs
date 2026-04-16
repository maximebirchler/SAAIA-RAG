using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Localization;

internal static class LocalizedStrings
{
    private static readonly Dictionary<string, Dictionary<string, string>> Catalog =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["greeting"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "Bonjour, comment puis-je vous aider aujourd'hui ?",
                ["en"] = "Hello, how can I assist you today?",
                ["es"] = "Hola, ¿cómo puedo ayudarte hoy?",
                ["pt"] = "Olá, como posso ajudar você hoje?",
                ["de"] = "Hallo, wie kann ich Ihnen heute helfen?",
                ["it"] = "Ciao, come posso aiutarti oggi?"
            },
            ["language_changed"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "D'accord, je continue en français.",
                ["en"] = "Got it, I'll continue in English.",
                ["es"] = "De acuerdo, continuaré en español.",
                ["pt"] = "Certo, vou continuar em português.",
                ["de"] = "Alles klar, ich mache auf Deutsch weiter.",
                ["it"] = "Va bene, continuerò in italiano."
            },
            ["style_changed"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "D'accord, j'adopte maintenant un style {0}.",
                ["en"] = "Got it, I'll use a {0} style from now on.",
                ["es"] = "De acuerdo, usaré un estilo {0} a partir de ahora.",
                ["pt"] = "Certo, vou usar um estilo {0} a partir de agora.",
                ["de"] = "Alles klar, ich verwende ab jetzt einen {0} Stil.",
                ["it"] = "Va bene, d'ora in poi userò uno stile {0}."
            },
            ["mode_changed"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "D'accord, je passe maintenant en mode {0}.",
                ["en"] = "Got it, I'll use {0} mode from now on.",
                ["es"] = "De acuerdo, ahora usaré el modo {0}.",
                ["pt"] = "Certo, vou usar o modo {0} a partir de agora.",
                ["de"] = "Alles klar, ich verwende ab jetzt den Modus {0}.",
                ["it"] = "Va bene, d'ora in poi userò la modalità {0}."
            },
            ["no_documents_found"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "Aucun document trouvé.",
                ["en"] = "No documents found.",
                ["es"] = "No se encontró ningún documento.",
                ["pt"] = "Nenhum documento encontrado.",
                ["de"] = "Keine Dokumente gefunden.",
                ["it"] = "Nessun documento trovato."
            },
            ["document_list_error"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "Impossible de lister les documents (erreur inattendue).",
                ["en"] = "Unable to list documents (unexpected error).",
                ["es"] = "No fue posible listar los documentos (error inesperado).",
                ["pt"] = "Não foi possível listar os documentos (erro inesperado).",
                ["de"] = "Die Dokumentliste konnte nicht erstellt werden (unerwarteter Fehler).",
                ["it"] = "Impossibile elencare i documenti (errore imprevisto)."
            },
            ["document_tree_error"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "Impossible de construire l’arborescence (erreur inattendue).",
                ["en"] = "Unable to build the tree (unexpected error).",
                ["es"] = "No fue posible construir el árbol (error inesperado).",
                ["pt"] = "Não foi possível construir a árvore (erro inesperado).",
                ["de"] = "Die Baumansicht konnte nicht erstellt werden (unerwarteter Fehler).",
                ["it"] = "Impossibile costruire l'albero (errore imprevisto)."
            },
            ["short_overview_unavailable"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "Je n'ai pas pu produire un aperçu court pour ce document.",
                ["en"] = "I could not build a short overview for this document.",
                ["es"] = "No pude generar una vista general breve de este documento.",
                ["pt"] = "Não consegui gerar uma visão geral curta deste documento.",
                ["de"] = "Ich konnte keine kurze Übersicht für dieses Dokument erstellen.",
                ["it"] = "Non sono riuscito a generare una breve panoramica di questo documento."
            },
            ["summary_unavailable"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "Je n'ai pas pu produire un résumé pour ce document.",
                ["en"] = "I could not build a summary for this document.",
                ["es"] = "No pude generar un resumen de este documento.",
                ["pt"] = "Não consegui gerar um resumo deste documento.",
                ["de"] = "Ich konnte keine Zusammenfassung für dieses Dokument erstellen.",
                ["it"] = "Non sono riuscito a generare un riassunto di questo documento."
            },

            ["summary_not_stored"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "Aucun résumé stocké n'est disponible pour ce document.",
                ["en"] = "No stored summary is available for this document.",
                ["es"] = "No hay ningún resumen almacenado para este documento.",
                ["pt"] = "Nenhum resumo armazenado está disponível para este documento.",
                ["de"] = "Für dieses Dokument ist keine gespeicherte Zusammenfassung verfügbar.",
                ["it"] = "Nessun riassunto salvato è disponibile per questo documento."
            },
            ["summary_store_requires_admin"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "Le stockage d'un résumé réutilisable nécessite une session admin active.",
                ["en"] = "Storing a reusable summary requires an active admin session.",
                ["es"] = "Guardar un resumen reutilizable requiere una sesión de administrador activa.",
                ["pt"] = "Armazenar um resumo reutilizável requer uma sessão de administrador ativa.",
                ["de"] = "Das Speichern einer wiederverwendbaren Zusammenfassung erfordert eine aktive Admin-Sitzung.",
                ["it"] = "Il salvataggio di un riassunto riutilizzabile richiede una sessione admin attiva."
            },
            ["summary_already_stored"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "Un résumé stocké existe déjà pour ce document. Je te redonne la version disponible.",
                ["en"] = "A stored summary already exists for this document. I am returning the available version.",
                ["es"] = "Ya existe un resumen almacenado para este documento. Devuelvo la versión disponible.",
                ["pt"] = "Já existe um resumo armazenado para este documento. Vou retornar a versão disponível.",
                ["de"] = "Für dieses Dokument existiert bereits eine gespeicherte Zusammenfassung. Ich gebe die verfügbare Version zurück.",
                ["it"] = "Esiste già un riassunto salvato per questo documento. Restituisco la versione disponibile."
            },
            ["summary_store_done"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "Le résumé réutilisable a bien été stocké. Voici la version disponible.",
                ["en"] = "The reusable summary has been stored successfully. Here is the available version.",
                ["es"] = "El resumen reutilizable se ha almacenado correctamente. Aquí está la versión disponible.",
                ["pt"] = "O resumo reutilizável foi armazenado com sucesso. Aqui está a versão disponível.",
                ["de"] = "Die wiederverwendbare Zusammenfassung wurde erfolgreich gespeichert. Hier ist die verfügbare Version.",
                ["it"] = "Il riassunto riutilizzabile è stato salvato correttamente. Ecco la versione disponibile."
            },
            ["summary_store_failed"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "Je n'ai pas réussi à stocker un résumé réutilisable pour ce document.",
                ["en"] = "I could not store a reusable summary for this document.",
                ["es"] = "No pude almacenar un resumen reutilizable para este documento.",
                ["pt"] = "Não consegui armazenar um resumo reutilizável para este documento.",
                ["de"] = "Ich konnte keine wiederverwendbare Zusammenfassung für dieses Dokument speichern.",
                ["it"] = "Non sono riuscito a salvare un riassunto riutilizzabile per questo documento."
            },
            ["no_previous_answer_to_translate"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "Je n'ai pas encore de réponse précédente à traduire.",
                ["en"] = "I do not have a previous answer to translate yet.",
                ["es"] = "Todavía no tengo una respuesta anterior para traducir.",
                ["pt"] = "Ainda não tenho uma resposta anterior para traduzir.",
                ["de"] = "Ich habe noch keine vorherige Antwort zum Übersetzen.",
                ["it"] = "Non ho ancora una risposta precedente da tradurre."
            },
            ["phase.interpreting"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "J'interprète la demande…",
                ["en"] = "I am interpreting the request…",
                ["es"] = "Estoy interpretando la solicitud…",
                ["pt"] = "Estou interpretando a solicitação…",
                ["de"] = "Ich interpretiere die Anfrage…",
                ["it"] = "Sto interpretando la richiesta…"
            },
            ["phase.answer_short"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "Je prépare une réponse courte…",
                ["en"] = "I am preparing a short answer…",
                ["es"] = "Estoy preparando una respuesta breve…",
                ["pt"] = "Estou preparando uma resposta curta…",
                ["de"] = "Ich bereite eine kurze Antwort vor…",
                ["it"] = "Sto preparando una risposta breve…"
            }
,
            ["guided_command_help_required"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "Cette demande ressemble à une commande guidée catalogue/admin, mais elle ne correspond pas exactement à une commande supportée. Je n'ai lancé aucun outil pour éviter une action partielle ou incorrecte. Ouvre le bouton ? puis clique sur la commande voulue pour insérer la formulation exacte attendue. Garde le langage naturel pour les recherches, les questions sur le contenu d'un document et les résumés.",
                ["en"] = "This request looks like a guided catalog/admin command, but it does not exactly match a supported command. I did not run any tool to avoid a partial or incorrect action. Open the ? button and click the command you want to insert the exact supported wording. Keep natural language for searches, document-content questions, and summaries.",
                ["es"] = "Esta solicitud parece un comando guiado de catálogo/administración, pero no coincide exactamente con un comando soportado. No ejecuté ninguna herramienta para evitar una acción parcial o incorrecta. Abre el botón ? y haz clic en el comando deseado para insertar la formulación exacta admitida. Mantén el lenguaje natural para las búsquedas, las preguntas sobre el contenido de un documento y los resúmenes.",
                ["pt"] = "Este pedido parece um comando guiado de catálogo/administração, mas não corresponde exatamente a um comando suportado. Não executei nenhuma ferramenta para evitar uma ação parcial ou incorreta. Abre o botão ? e clica no comando pretendido para inserir a formulação exata suportada. Mantém a linguagem natural para pesquisas, perguntas sobre o conteúdo de um documento e resumos.",
                ["de"] = "Diese Anfrage sieht wie ein geführter Katalog-/Admin-Befehl aus, entspricht aber nicht exakt einem unterstützten Befehl. Ich habe kein Tool ausgeführt, um eine teilweise oder falsche Aktion zu vermeiden. Öffne die Schaltfläche ? und klicke auf den gewünschten Befehl, um die exakt unterstützte Formulierung einzufügen. Verwende natürliche Sprache für Recherchen, Fragen zum Dokumentinhalt und Zusammenfassungen.",
                ["it"] = "Questa richiesta sembra un comando guidato di catalogo/amministrazione, ma non corrisponde esattamente a un comando supportato. Non ho eseguito alcuno strumento per evitare un'azione parziale o errata. Apri il pulsante ? e fai clic sul comando desiderato per inserire la formulazione esatta supportata. Mantieni il linguaggio naturale per le ricerche, le domande sul contenuto di un documento e i riassunti."
            }
,
            ["help_only_command_use_help"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "Cette action guidée n'est disponible que depuis le bouton ?. Je n'ai lancé aucune commande depuis le chat. Utilise le Help pour les actions admin sensibles, et garde le chat libre pour les recherches, questions documentaires et résumés.",
                ["en"] = "This guided action is only available from the ? button. I did not run any command from the chat. Use Help for sensitive admin actions, and keep free chat for searches, document questions, and summaries.",
                ["es"] = "Esta acción guiada solo está disponible desde el botón ?. No ejecuté ningún comando desde el chat. Usa la Ayuda para las acciones admin sensibles y deja el chat libre para búsquedas, preguntas documentales y resúmenes.",
                ["pt"] = "Esta ação guiada só está disponível a partir do botão ?. Não executei nenhum comando a partir do chat. Usa a Ajuda para ações admin sensíveis e mantém o chat livre para pesquisas, perguntas documentais e resumos.",
                ["de"] = "Diese geführte Aktion ist nur über die Schaltfläche ? verfügbar. Ich habe keinen Befehl aus dem Chat ausgeführt. Verwende die Hilfe für sensible Admin-Aktionen und den freien Chat für Recherchen, Dokumentfragen und Zusammenfassungen.",
                ["it"] = "Questa azione guidata è disponibile solo dal pulsante ?. Non ho eseguito alcun comando dalla chat. Usa l'Aiuto per le azioni admin sensibili e lascia la chat libera per ricerche, domande sui documenti e riassunti."
            }
        };

    private static readonly Dictionary<string, string[]> LanguageAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["fr"] = new[] { "fr", "french", "français", "francais", "francese" },
            ["en"] = new[] { "en", "english", "anglais", "inglés", "ingles", "inglês", "inglese" },
            ["es"] = new[] { "es", "spanish", "español", "espanol", "espagnol", "spagnolo" },
            ["pt"] = new[] { "pt", "portuguese", "português", "portugues", "portugais", "portoghese" },
            ["de"] = new[] { "de", "german", "deutsch", "deutch", "allemand", "tedesco" },
            ["it"] = new[] { "it", "italian", "italiano", "italien" }
        };

    private static readonly Dictionary<string, string[]> LanguageSignals =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["fr"] = new[] { "bonjour", "salut", "coucou", "donne", "liste", "serveur", "arborescence", "résumé", "resume", "français", "francais", "merci", "stp", "comment", "documents", "document", "quels", "quelles", "présents", "present", "présent", "combien", "qui", "tu", "quoi", "categorie", "catégorie", "statistiques", "sans", "vient", "viens", "lister", "atex" },
            ["en"] = new[] { "hello", "hi", "hey", "give", "list", "server", "tree", "summary", "please", "what", "how", "english", "document", "file", "documents", "category", "categories", "which", "many", "present", "thank", "thanks", "who", "are", "you" },
            ["es"] = new[] { "hola", "dame", "lista", "servidor", "árbol", "arbol", "resumen", "español", "espanol", "archivo", "qué", "significa", "cuántos", "cuantos", "documentos", "hay", "estadísticas", "estadisticas", "categoria", "categoría", "quien", "eres" },
            ["pt"] = new[] { "olá", "ola", "lista", "servidor", "árvore", "arvore", "resumo", "português", "portugues", "arquivo", "quantos", "documentos", "estatísticas", "estatisticas", "categoria", "quem", "és", "voce" },
            ["de"] = new[] { "hallo", "bitte", "baum", "server", "zusammenfassung", "deutsch", "dokument", "dokumente", "datei", "was", "bedeutet", "liste", "wieviele", "wie viele", "vorhanden", "wer", "bist", "du" },
            ["it"] = new[] { "ciao", "elenco", "server", "albero", "riassunto", "italiano", "documento", "documenti", "file", "significa", "quali", "quanti", "presenti", "sono", "sul", "chi", "sei", "categoria", "statistiche" }
        };

    public static string Normalize(string? language) => NormalizeLanguage(language);

    public static string NormalizeLanguage(string? language)
    {
        var s = StripDiacritics(language ?? "fr").Trim().ToLowerInvariant();
        if (s.StartsWith("en")) return "en";
        if (s.StartsWith("es")) return "es";
        if (s.StartsWith("pt")) return "pt";
        if (s.StartsWith("de")) return "de";
        if (s.StartsWith("it")) return "it";
        return "fr";
    }

    public static string Get(string key, string? language)
    {
        var lang = NormalizeLanguage(language);
        if (Catalog.TryGetValue(key, out var values))
        {
            if (values.TryGetValue(lang, out var value))
                return value;
            if (values.TryGetValue("fr", out var fr))
                return fr;
        }

        return key;
    }

    public static string Format(string key, string? language, params object[] args)
        => string.Format(Get(key, language), args);

    public static string Greeting(string? language) => Get("greeting", language);
    public static string LanguageChanged(string? language) => Get("language_changed", language);
    public static string StyleChanged(string? style, string? language)
        => Format("style_changed", language, LocalizedStyleName(style, language));
    public static string ModeChanged(string? mode, string? language)
        => Format("mode_changed", language, LocalizedModeName(mode, language));
    public static string NoDocumentsFound(string? language) => Get("no_documents_found", language);
    public static string DocumentListError(string? language) => Get("document_list_error", language);
    public static string DocumentTreeError(string? language) => Get("document_tree_error", language);
    public static string ShortOverviewUnavailable(string? language) => Get("short_overview_unavailable", language);
    public static string SummaryUnavailable(string? language) => Get("summary_unavailable", language);
    public static string SummaryNotStored(string? language) => Get("summary_not_stored", language);
    public static string SummaryStoreRequiresAdmin(string? language) => Get("summary_store_requires_admin", language);
    public static string SummaryAlreadyStored(string? language) => Get("summary_already_stored", language);
    public static string SummaryStoreDone(string? language) => Get("summary_store_done", language);
    public static string SummaryStoreFailed(string? language) => Get("summary_store_failed", language);
    public static string GuidedCommandHelpRequired(string? language) => Get("guided_command_help_required", language);
    public static string HelpOnlyCommandUseHelp(string? language) => Get("help_only_command_use_help", language);

    public static string LocalizedLanguageName(string language, string? uiLanguage)
    {
        var target = NormalizeLanguage(language);
        var ui = NormalizeLanguage(uiLanguage);

        return ui switch
        {
            "en" => target switch
            {
                "fr" => "French",
                "en" => "English",
                "es" => "Spanish",
                "pt" => "Portuguese",
                "de" => "German",
                "it" => "Italian",
                _ => "French"
            },
            "es" => target switch
            {
                "fr" => "francés",
                "en" => "inglés",
                "es" => "español",
                "pt" => "portugués",
                "de" => "alemán",
                "it" => "italiano",
                _ => "francés"
            },
            "pt" => target switch
            {
                "fr" => "francês",
                "en" => "inglês",
                "es" => "espanhol",
                "pt" => "português",
                "de" => "alemão",
                "it" => "italiano",
                _ => "francês"
            },
            "de" => target switch
            {
                "fr" => "Französisch",
                "en" => "Englisch",
                "es" => "Spanisch",
                "pt" => "Portugiesisch",
                "de" => "Deutsch",
                "it" => "Italienisch",
                _ => "Französisch"
            },
            "it" => target switch
            {
                "fr" => "francese",
                "en" => "inglese",
                "es" => "spagnolo",
                "pt" => "portoghese",
                "de" => "tedesco",
                "it" => "italiano",
                _ => "francese"
            },
            _ => target switch
            {
                "fr" => "français",
                "en" => "anglais",
                "es" => "espagnol",
                "pt" => "portugais",
                "de" => "allemand",
                "it" => "italien",
                _ => "français"
            }
        };
    }

    public static string LocalizedStyleName(string? style, string? uiLanguage)
    {
        var target = NormalizeStyle(style);
        var ui = NormalizeLanguage(uiLanguage);

        return ui switch
        {
            "en" => target switch
            {
                "plain" => "plain",
                "technical" => "technical",
                "executive" => "executive",
                _ => "automatic"
            },
            "es" => target switch
            {
                "plain" => "claro",
                "technical" => "técnico",
                "executive" => "ejecutivo",
                _ => "automático"
            },
            "pt" => target switch
            {
                "plain" => "claro",
                "technical" => "técnico",
                "executive" => "executivo",
                _ => "automático"
            },
            "de" => target switch
            {
                "plain" => "klaren",
                "technical" => "technischen",
                "executive" => "managementorientierten",
                _ => "automatischen"
            },
            "it" => target switch
            {
                "plain" => "chiaro",
                "technical" => "tecnico",
                "executive" => "executive",
                _ => "automatico"
            },
            _ => target switch
            {
                "plain" => "clair",
                "technical" => "technique",
                "executive" => "exécutif",
                _ => "automatique"
            }
        };
    }

    public static string LocalizedModeName(string? mode, string? uiLanguage)
    {
        var target = NormalizeMode(mode);
        var ui = NormalizeLanguage(uiLanguage);

        return ui switch
        {
            "en" => target switch
            {
                "standard" => "standard",
                "strict" => "strict",
                _ => "automatic"
            },
            "es" => target switch
            {
                "standard" => "estándar",
                "strict" => "estricto",
                _ => "automático"
            },
            "pt" => target switch
            {
                "standard" => "padrão",
                "strict" => "estrito",
                _ => "automático"
            },
            "de" => target switch
            {
                "standard" => "Standard",
                "strict" => "streng",
                _ => "automatisch"
            },
            "it" => target switch
            {
                "standard" => "standard",
                "strict" => "rigorosa",
                _ => "automatica"
            },
            _ => target switch
            {
                "standard" => "standard",
                "strict" => "strict",
                _ => "automatique"
            }
        };
    }

    public static string NoPreviousAnswerToTranslate(string? language)
        => Get("no_previous_answer_to_translate", language);

    public static string NormalizeStyle(string? style)
    {
        var s = StripDiacritics(style ?? "auto").Trim().ToLowerInvariant();
        return s switch
        {
            "plain" or "simple" or "clear" or "clair" or "sobre" => "plain",
            "technical" or "technique" or "tech" or "detailed" or "detaille" => "technical",
            "executive" or "executif" or "exec" or "management" => "executive",
            _ => "auto"
        };
    }

    public static string NormalizeMode(string? mode)
    {
        var s = StripDiacritics(mode ?? "auto").Trim().ToLowerInvariant();
        return s switch
        {
            "standard" or "normal" or "balanced" or "equilibre" or "equilibrio" or "padrao" or "padrão" => "standard",
            "strict" or "stricte" or "rigoureux" or "rigoureuse" or "estricto" or "estrito" or "streng" => "strict",
            _ => "auto"
        };
    }

    public static string DetectLanguage(string? text, string? fallbackLanguage = "fr")
    {
        var detected = DetectLanguageInternal(text);
        return string.IsNullOrWhiteSpace(detected) ? NormalizeLanguage(fallbackLanguage) : detected;
    }

    public static bool TryDetectLanguagePreferenceChange(string? text, out string language)
    {
        var s = StripDiacritics(text ?? string.Empty).Trim();
        if (s.Length == 0)
        {
            language = string.Empty;
            return false;
        }

        var patterns = new[]
        {
            @"^(?:please\s+)?(?:respond|answer|reply|talk|speak|continue|write|translate|reponds|réponds|parle|continue|continuer|ecris|écris|traduis|traduit|responde|contesta|habla|sigue|escribe|traduce|fale|continua|escreva|traduza|antworte|beantworte|sprich|schreibe|übersetze|rispondi|parla|scrivi|traduci)\s+(?:in|en|em|auf)?\s*(?<lang>[\p{L}]+)(?:\s+(?:please|por favor|svp|stp|bitte|per favore))?\s*[!.?]*$",
            @"^(?:traduis|traduit|translate|traduce|traduza|traduci)\s+(?:en|in|em|auf)?\s*(?<lang>[\p{L}]+)(?:\s+(?:please|por favor|svp|stp|bitte|per favore))?\s*[!.?]*$",
            @"^(?:en|in|em|auf)\s+(?<lang>[\p{L}]+)(?:\s+(?:please|por favor|svp|stp|bitte|per favore))?\s*[!.?]*$",
            @"^(?:non\s+|not\s+)?(?:en|in|em|auf)\s+(?<lang>[\p{L}]+)(?:\s+(?:please|por favor|svp|stp|bitte|per favore))?\s*[!.?]*$",
            @"^(?<lang>[\p{L}]+)(?:\s+(?:please|por favor|svp|stp|bitte|per favore))?\s*[!.?]*$",
            @"^(?:ca|ça)\s+donne\s+quoi\s+(?:en|in|em|auf)\s+(?<lang>[\p{L}]+)\s*[!.?]*$",
            @"^(?:what\s+about|how\s+about|and|et|alors|donc|du\s+coup|maintenant|now)\s+(?:en|in|em|auf)\s+(?<lang>[\p{L}]+)\s*[!.?]*$",
            @"^(?:comment|how)\s+(?:le|la|that|this|ca|ça|cela|ce\s+message)\s+(?:se\s+dit|says|looks|sounds)?\s*(?:en|in|em|auf)\s+(?<lang>[\p{L}]+)\s*[!.?]*$"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(s, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success && TryMapLanguageAlias(match.Groups["lang"].Value, out language))
                return true;
        }

        language = string.Empty;
        return false;
    }

    public static bool TryDetectStylePreferenceChange(string? text, out string style)
    {
        var s = StripDiacritics(text ?? string.Empty).Trim();
        if (s.Length == 0)
        {
            style = string.Empty;
            return false;
        }

        var patterns = new[]
        {
            @"^(?:reponds|repond|ecris|continue|parle|answer|reply|write|continue|respond|responde|escribe|contesta|fale|responda|antworte|schreibe|rispondi|scrivi)\s+(?:avec\s+un\s+style|dans\s+un\s+style|de\s+maniere|de\s+facon|in\s+a|with\s+a|en\s+modo|em\s+estilo|im\s+stil|con\s+uno\s+stile)\s+(?<style>[\p{L}\-_ ]+)\s*[!.?]*$",
            @"^(?:style|tone|ton|mode|stil|estilo)\s+(?<style>[\p{L}\-_ ]+)\s*[!.?]*$",
            @"^(?<style>plain|simple|clear|clair|sobre|technical|technique|tech|executive|executif|exec|management)\s+(?:style|tone|mode|stil|estilo)?\s*[!.?]*$",
            @"^(?:pour\s+la\s+suite|dorenavant|desormais|from\s+now\s+on|a\s+partir\s+de\s+maintenant)\s+(?:style|tone|mode)?\s*(?<style>[\p{L}\-_ ]+)\s*[!.?]*$"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(s, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
                continue;

            var candidate = NormalizeStyle(match.Groups["style"].Value);
            if (candidate != "auto")
            {
                style = candidate;
                return true;
            }
        }

        style = string.Empty;
        return false;
    }

    public static bool TryDetectModePreferenceChange(string? text, out string mode)
    {
        var s = StripDiacritics(text ?? string.Empty).Trim();
        if (s.Length == 0)
        {
            mode = string.Empty;
            return false;
        }

        var patterns = new[]
        {
            @"^(?:passe|mets|met|laisse|garde|continue|travaille|set|switch|use|keep|work|stay|pon|usa|mantieni|wechsel|nutze)\s+(?:en|to|in|em|im)?\s*(?:le|la|the|el|o|il|den)?\s*(?:mode\s+)?(?<mode>[\p{L}\-_ ]+)\s*[!.?]*$",
            @"^(?:mode|working\s+mode|modo|modus)\s+(?<mode>[\p{L}\-_ ]+)\s*[!.?]*$",
            @"^(?<mode>auto|automatic|automatique|automatica|automático|automatisch|standard|normal|balanced|equilibre|equilibrio|strict|stricte|rigoureux|rigoureuse|estricto|estrito|streng)\s+(?:mode|modo|modus)?\s*[!.?]*$",
            @"^(?:pour\s+la\s+suite|dorenavant|desormais|from\s+now\s+on|a\s+partir\s+de\s+maintenant)\s+(?:mode)?\s*(?<mode>[\p{L}\-_ ]+)\s*[!.?]*$"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(s, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
                continue;

            var rawMode = match.Groups["mode"].Value;
            var candidate = NormalizeMode(rawMode);
            if (candidate != "auto" || Regex.IsMatch(rawMode, @"(?i)\b(auto|automatic|automatique|automatica|automático|automatisch)\b"))
            {
                mode = candidate;
                return true;
            }
        }

        mode = string.Empty;
        return false;
    }

    private static string DetectLanguageInternal(string? message)
    {
        var s = StripDiacritics(message ?? string.Empty).Trim();
        if (s.Length == 0) return string.Empty;

        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in LanguageSignals)
        {
            var score = 0;
            foreach (var token in pair.Value)
            {
                var normalizedToken = StripDiacritics(token);
                if (Regex.IsMatch(s, $@"\b{Regex.Escape(normalizedToken)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    score += normalizedToken.Length <= 3 ? 1 : 2;
            }
            scores[pair.Key] = score;
        }

        var best = scores.OrderByDescending(x => x.Value).First();
        if (best.Value < 2) return string.Empty;

        var second = scores.OrderByDescending(x => x.Value).Skip(1).First();
        if (best.Value == second.Value) return string.Empty;

        return best.Key;
    }

    private static bool TryMapLanguageAlias(string token, out string language)
    {
        var normalized = StripDiacritics(token).Trim().ToLowerInvariant();

        foreach (var pair in LanguageAliases)
        {
            if (pair.Value.Any(x => StripDiacritics(x) == normalized))
            {
                language = pair.Key;
                return true;
            }
        }

        language = string.Empty;
        return false;
    }

    private static string StripDiacritics(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);

        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
