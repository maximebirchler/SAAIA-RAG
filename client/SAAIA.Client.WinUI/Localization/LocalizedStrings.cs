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
            ["rag_search_busy"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "Le serveur documentaire est très sollicité pour le moment. Réessaie dans quelques secondes.",
                ["en"] = "The document server is busy right now. Please retry in a few seconds.",
                ["es"] = "El servidor documental está muy ocupado en este momento. Vuelve a intentarlo en unos segundos.",
                ["pt"] = "O servidor documental está muito ocupado neste momento. Tenta novamente dentro de alguns segundos.",
                ["de"] = "Der Dokumentserver ist gerade stark ausgelastet. Bitte versuche es in ein paar Sekunden erneut.",
                ["it"] = "Il server documentale è molto occupato in questo momento. Riprova tra qualche secondo."
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
            ["summary_store_queued"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "La génération du résumé réutilisable a été confiée au serveur. Il sera disponible dès que le traitement backend sera terminé.",
                ["en"] = "Reusable summary generation has been queued on the server. It will be available once backend processing completes.",
                ["es"] = "La generación del resumen reutilizable se ha enviado al servidor. Estará disponible cuando termine el procesamiento backend.",
                ["pt"] = "A geração do resumo reutilizável foi enviada ao servidor. Ele ficará disponível quando o processamento backend terminar.",
                ["de"] = "Die Erstellung der wiederverwendbaren Zusammenfassung wurde an den Server übergeben. Sie ist verfügbar, sobald die Backend-Verarbeitung abgeschlossen ist.",
                ["it"] = "La generazione del riassunto riutilizzabile è stata affidata al server. Sarà disponibile quando l'elaborazione backend sarà terminata."
            },
            ["summary_store_backoffice_unavailable"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "Le serveur ne peut pas générer ce résumé réutilisable pour le moment : le LLM backoffice n'est pas disponible.",
                ["en"] = "The server cannot generate this reusable summary right now because the backoffice LLM is unavailable.",
                ["es"] = "El servidor no puede generar este resumen reutilizable ahora porque el LLM backoffice no está disponible.",
                ["pt"] = "O servidor não pode gerar este resumo reutilizável agora porque o LLM backoffice não está disponível.",
                ["de"] = "Der Server kann diese wiederverwendbare Zusammenfassung gerade nicht erstellen, weil das Backoffice-LLM nicht verfügbar ist.",
                ["it"] = "Il server non può generare ora questo riassunto riutilizzabile perché il LLM backoffice non è disponibile."
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
            ["source_card.language"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "langue",
                ["en"] = "language",
                ["es"] = "idioma",
                ["pt"] = "idioma",
                ["de"] = "Sprache",
                ["it"] = "lingua"
            },
            ["source_card.document_language"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "langue document",
                ["en"] = "document language",
                ["es"] = "idioma del documento",
                ["pt"] = "idioma do documento",
                ["de"] = "Dokumentsprache",
                ["it"] = "lingua documento"
            },
            ["source_card.profile_language"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "langue profil",
                ["en"] = "profile language",
                ["es"] = "idioma del perfil",
                ["pt"] = "idioma do perfil",
                ["de"] = "Profilsprache",
                ["it"] = "lingua profilo"
            },
            ["source_card.quality"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "qualité",
                ["en"] = "quality",
                ["es"] = "calidad",
                ["pt"] = "qualidade",
                ["de"] = "Qualität",
                ["it"] = "qualità"
            },
            ["source_card.page_quality"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "qualité page",
                ["en"] = "page quality",
                ["es"] = "calidad página",
                ["pt"] = "qualidade página",
                ["de"] = "Seitenqualität",
                ["it"] = "qualità pagina"
            },
            ["source_card.document_quality"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "qualité document",
                ["en"] = "document quality",
                ["es"] = "calidad documento",
                ["pt"] = "qualidade documento",
                ["de"] = "Dokumentqualität",
                ["it"] = "qualità documento"
            },
            ["source_card.ocr_applied"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "OCR appliqué",
                ["en"] = "OCR applied",
                ["es"] = "OCR aplicado",
                ["pt"] = "OCR aplicado",
                ["de"] = "OCR angewendet",
                ["it"] = "OCR applicato"
            },
            ["source_card.ocr_recommended"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "OCR recommandé",
                ["en"] = "OCR recommended",
                ["es"] = "OCR recomendado",
                ["pt"] = "OCR recomendado",
                ["de"] = "OCR empfohlen",
                ["it"] = "OCR consigliato"
            },
            ["source_card.ocr_attempted"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "OCR tenté",
                ["en"] = "OCR attempted",
                ["es"] = "OCR intentado",
                ["pt"] = "OCR tentado",
                ["de"] = "OCR versucht",
                ["it"] = "OCR tentato"
            },
            ["source_card.ocr_failure"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "incident OCR",
                ["en"] = "OCR issue",
                ["es"] = "incidencia OCR",
                ["pt"] = "incidente OCR",
                ["de"] = "OCR-Hinweis",
                ["it"] = "problema OCR"
            },
            ["source_card.ocr_reason"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "raison OCR",
                ["en"] = "OCR reason",
                ["es"] = "motivo OCR",
                ["pt"] = "motivo OCR",
                ["de"] = "OCR-Grund",
                ["it"] = "motivo OCR"
            },
            ["source_card.ocr_pages"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "pages OCR",
                ["en"] = "OCR pages",
                ["es"] = "páginas OCR",
                ["pt"] = "páginas OCR",
                ["de"] = "OCR-Seiten",
                ["it"] = "pagine OCR"
            },
            ["source_card.ocr_novel_pages"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "pages OCR utiles",
                ["en"] = "useful OCR pages",
                ["es"] = "páginas OCR útiles",
                ["pt"] = "páginas OCR úteis",
                ["de"] = "nützliche OCR-Seiten",
                ["it"] = "pagine OCR utili"
            },
            ["source_card.native_text"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "texte natif",
                ["en"] = "native text",
                ["es"] = "texto nativo",
                ["pt"] = "texto nativo",
                ["de"] = "nativer Text",
                ["it"] = "testo nativo"
            },
            ["source_card.page_review_count"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "pages à revoir",
                ["en"] = "pages to review",
                ["es"] = "páginas a revisar",
                ["pt"] = "páginas a rever",
                ["de"] = "Seiten zur Prüfung",
                ["it"] = "pagine da rivedere"
            },
            ["source_card.page_warning_count"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "pages avec alerte",
                ["en"] = "warning pages",
                ["es"] = "páginas con aviso",
                ["pt"] = "páginas com aviso",
                ["de"] = "Warnseiten",
                ["it"] = "pagine con avviso"
            },
            ["source_card.image_pages"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "pages image",
                ["en"] = "image pages",
                ["es"] = "páginas con imagen",
                ["pt"] = "páginas com imagem",
                ["de"] = "Bildseiten",
                ["it"] = "pagine immagine"
            },
            ["source_card.native_ocr_recommended"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "OCR natif recommandé",
                ["en"] = "native OCR recommended",
                ["es"] = "OCR nativo recomendado",
                ["pt"] = "OCR nativo recomendado",
                ["de"] = "native OCR empfohlen",
                ["it"] = "OCR nativo consigliato"
            },
            ["source_card.ocr_mode"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "mode OCR",
                ["en"] = "OCR mode",
                ["es"] = "modo OCR",
                ["pt"] = "modo OCR",
                ["de"] = "OCR-Modus",
                ["it"] = "modalità OCR"
            },
            ["source_card.ocr_languages"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "langues OCR",
                ["en"] = "OCR languages",
                ["es"] = "idiomas OCR",
                ["pt"] = "idiomas OCR",
                ["de"] = "OCR-Sprachen",
                ["it"] = "lingue OCR"
            },
            ["source_card.ocr_duration"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "durée OCR",
                ["en"] = "OCR duration",
                ["es"] = "duración OCR",
                ["pt"] = "duração OCR",
                ["de"] = "OCR-Dauer",
                ["it"] = "durata OCR"
            },
            ["source_card.document_pages"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "pages",
                ["en"] = "pages",
                ["es"] = "páginas",
                ["pt"] = "páginas",
                ["de"] = "Seiten",
                ["it"] = "pagine"
            },
            ["source_card.text_pages"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "pages texte",
                ["en"] = "text pages",
                ["es"] = "páginas con texto",
                ["pt"] = "páginas com texto",
                ["de"] = "Textseiten",
                ["it"] = "pagine testo"
            },
            ["source_card.empty_pages"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "pages vides",
                ["en"] = "empty pages",
                ["es"] = "páginas vacías",
                ["pt"] = "páginas vazias",
                ["de"] = "leere Seiten",
                ["it"] = "pagine vuote"
            },
            ["source_card.sparse_pages"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "pages pauvres",
                ["en"] = "sparse pages",
                ["es"] = "páginas escasas",
                ["pt"] = "páginas escassas",
                ["de"] = "seiten mit wenig Text",
                ["it"] = "pagine scarne"
            },
            ["source_card.ocr_mode.image_page"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "OCR image page",
                ["en"] = "image-page OCR",
                ["es"] = "OCR de página imagen",
                ["pt"] = "OCR de página imagem",
                ["de"] = "Bildseiten-OCR",
                ["it"] = "OCR pagina immagine"
            },
            ["source_card.ocr_mode.full_document"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "OCR document complet",
                ["en"] = "full-document OCR",
                ["es"] = "OCR de documento completo",
                ["pt"] = "OCR de documento completo",
                ["de"] = "Vollständige Dokument-OCR",
                ["it"] = "OCR documento completo"
            },
            ["source_card.ocr_mode.full_document_force"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "OCR document complet forcé",
                ["en"] = "forced full-document OCR",
                ["es"] = "OCR completo forzado",
                ["pt"] = "OCR completo forçado",
                ["de"] = "erzwungene vollständige Dokument-OCR",
                ["it"] = "OCR completo forzato"
            },
            ["source_card.ocr_mode.full_document_plus_image_page"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "OCR complet + images",
                ["en"] = "full OCR + images",
                ["es"] = "OCR completo + imágenes",
                ["pt"] = "OCR completo + imagens",
                ["de"] = "vollständige OCR + Bilder",
                ["it"] = "OCR completo + immagini"
            },
            ["source_card.ocr_mode.full_document_force_plus_image_page"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "OCR complet forcé + images",
                ["en"] = "forced full OCR + images",
                ["es"] = "OCR completo forzado + imágenes",
                ["pt"] = "OCR completo forçado + imagens",
                ["de"] = "erzwungene vollständige OCR + Bilder",
                ["it"] = "OCR completo forzato + immagini"
            },
            ["source_card.ocr_mode.ocr_disabled"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "OCR désactivé",
                ["en"] = "OCR disabled",
                ["es"] = "OCR desactivado",
                ["pt"] = "OCR desativado",
                ["de"] = "OCR deaktiviert",
                ["it"] = "OCR disattivato"
            },
            ["source_card.ocr_reason.timeout"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "délai dépassé",
                ["en"] = "timeout",
                ["es"] = "tiempo agotado",
                ["pt"] = "tempo esgotado",
                ["de"] = "Zeitüberschreitung",
                ["it"] = "tempo scaduto"
            },
            ["source_card.ocr_reason.command_missing"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "commande absente",
                ["en"] = "command missing",
                ["es"] = "comando ausente",
                ["pt"] = "comando ausente",
                ["de"] = "Befehl fehlt",
                ["it"] = "comando mancante"
            },
            ["source_card.ocr_reason.process_start_failed"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "démarrage impossible",
                ["en"] = "start failed",
                ["es"] = "inicio fallido",
                ["pt"] = "arranque falhou",
                ["de"] = "Start fehlgeschlagen",
                ["it"] = "avvio non riuscito"
            },
            ["source_card.ocr_reason.exit_code_non_zero"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "sortie en erreur",
                ["en"] = "error exit",
                ["es"] = "salida con error",
                ["pt"] = "saída com erro",
                ["de"] = "Fehlercode",
                ["it"] = "uscita con errore"
            },
            ["source_card.ocr_reason.sidecar_missing"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "fichier OCR absent",
                ["en"] = "OCR file missing",
                ["es"] = "archivo OCR ausente",
                ["pt"] = "ficheiro OCR ausente",
                ["de"] = "OCR-Datei fehlt",
                ["it"] = "file OCR mancante"
            },
            ["source_card.ocr_reason.below_min_words"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "trop peu de mots",
                ["en"] = "too few words",
                ["es"] = "muy pocas palabras",
                ["pt"] = "poucas palavras",
                ["de"] = "zu wenige Wörter",
                ["it"] = "troppo poche parole"
            },
            ["source_card.ocr_reason.ocr_extraction_failed"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "extraction OCR échouée",
                ["en"] = "OCR extraction failed",
                ["es"] = "extracción OCR fallida",
                ["pt"] = "extração OCR falhou",
                ["de"] = "OCR-Extraktion fehlgeschlagen",
                ["it"] = "estrazione OCR non riuscita"
            },
            ["source_card.ocr_reason.ocr_failed"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "OCR échoué",
                ["en"] = "OCR failed",
                ["es"] = "OCR fallido",
                ["pt"] = "OCR falhou",
                ["de"] = "OCR fehlgeschlagen",
                ["it"] = "OCR non riuscito"
            },
            ["source_card.ocr_reason.ocr_disabled"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "OCR désactivé",
                ["en"] = "OCR disabled",
                ["es"] = "OCR desactivado",
                ["pt"] = "OCR desativado",
                ["de"] = "OCR deaktiviert",
                ["it"] = "OCR disattivato"
            },
            ["source_card.ocr_reason.ocr_required_but_disabled"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "OCR requis mais désactivé",
                ["en"] = "OCR required but disabled",
                ["es"] = "OCR requerido pero desactivado",
                ["pt"] = "OCR necessário mas desativado",
                ["de"] = "OCR erforderlich, aber deaktiviert",
                ["it"] = "OCR richiesto ma disattivato"
            },
            ["source_card.ocr_reason.ocr_output_missing"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "sortie OCR absente",
                ["en"] = "OCR output missing",
                ["es"] = "salida OCR ausente",
                ["pt"] = "saída OCR ausente",
                ["de"] = "OCR-Ausgabe fehlt",
                ["it"] = "output OCR mancante"
            },
            ["source_card.ocr_reason.scanned_pdf_not_indexable"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "PDF scanné non indexable",
                ["en"] = "scanned PDF not indexable",
                ["es"] = "PDF escaneado no indexable",
                ["pt"] = "PDF digitalizado não indexável",
                ["de"] = "gescanntes PDF nicht indexierbar",
                ["it"] = "PDF scansionato non indicizzabile"
            },
            ["source_card.ocr_reason.no_indexable_text"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "aucun texte indexable",
                ["en"] = "no indexable text",
                ["es"] = "sin texto indexable",
                ["pt"] = "sem texto indexável",
                ["de"] = "kein indexierbarer Text",
                ["it"] = "nessun testo indicizzabile"
            },
            ["source_card.ocr_reason.document_not_indexable"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "document non indexable",
                ["en"] = "document not indexable",
                ["es"] = "documento no indexable",
                ["pt"] = "documento não indexável",
                ["de"] = "Dokument nicht indexierbar",
                ["it"] = "documento non indicizzabile"
            },
            ["source_card.ocr_reason.render_failed"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "rendu page échoué",
                ["en"] = "page render failed",
                ["es"] = "renderizado fallido",
                ["pt"] = "renderização falhou",
                ["de"] = "Seitenrendering fehlgeschlagen",
                ["it"] = "rendering pagina non riuscito"
            },
            ["source_card.ocr_reason.exception"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "exception OCR",
                ["en"] = "OCR exception",
                ["es"] = "excepción OCR",
                ["pt"] = "exceção OCR",
                ["de"] = "OCR-Ausnahme",
                ["it"] = "eccezione OCR"
            },
            ["source_card.ocr_reason.image_ocr_no_novel_text"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "aucun texte image utile",
                ["en"] = "no useful image text",
                ["es"] = "sin texto útil en imagen",
                ["pt"] = "sem texto útil na imagem",
                ["de"] = "kein nützlicher Bildtext",
                ["it"] = "nessun testo immagine utile"
            },
            ["source_card.ocr_reason.ocr_not_better_than_native_text"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "texte natif meilleur",
                ["en"] = "native text better",
                ["es"] = "texto nativo mejor",
                ["pt"] = "texto nativo melhor",
                ["de"] = "nativer Text besser",
                ["it"] = "testo nativo migliore"
            },
            ["source_card.ocr_reason.image_ocr_not_better_than_native_text"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "texte natif meilleur",
                ["en"] = "native text better",
                ["es"] = "texto nativo mejor",
                ["pt"] = "texto nativo melhor",
                ["de"] = "nativer Text besser",
                ["it"] = "testo nativo migliore"
            },
            ["source_card.ocr_reason.image_ocr_merged_native_text"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "texte image ajouté",
                ["en"] = "image text merged",
                ["es"] = "texto de imagen añadido",
                ["pt"] = "texto de imagem adicionado",
                ["de"] = "Bildtext ergänzt",
                ["it"] = "testo immagine aggiunto"
            },
            ["source_card.ocr_reason.force_ocr_replaced_native_text"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "OCR forcé retenu",
                ["en"] = "forced OCR used",
                ["es"] = "OCR forzado usado",
                ["pt"] = "OCR forçado usado",
                ["de"] = "erzwungene OCR genutzt",
                ["it"] = "OCR forzato usato"
            },
            ["source_card.ocr_reason.ocr_replaced_native_text"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "OCR retenu",
                ["en"] = "OCR used",
                ["es"] = "OCR usado",
                ["pt"] = "OCR usado",
                ["de"] = "OCR genutzt",
                ["it"] = "OCR usato"
            },
            ["source_card.ocr_reason.duplicate_or_below_threshold"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "doublon ou seuil trop bas",
                ["en"] = "duplicate or too short",
                ["es"] = "duplicado o demasiado corto",
                ["pt"] = "duplicado ou curto demais",
                ["de"] = "Duplikat oder zu kurz",
                ["it"] = "duplicato o troppo breve"
            },
            ["source_card.ocr_reason.novel_text_applied"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "texte nouveau ajouté",
                ["en"] = "new text added",
                ["es"] = "texto nuevo añadido",
                ["pt"] = "texto novo adicionado",
                ["de"] = "neuer Text ergänzt",
                ["it"] = "nuovo testo aggiunto"
            },
            ["source_card.ocr_reason.no_novel_text"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "pas de texte nouveau",
                ["en"] = "no new text",
                ["es"] = "sin texto nuevo",
                ["pt"] = "sem texto novo",
                ["de"] = "kein neuer Text",
                ["it"] = "nessun testo nuovo"
            },
            ["source_card.review_recommended"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "revue recommandée",
                ["en"] = "review recommended",
                ["es"] = "revisión recomendada",
                ["pt"] = "revisão recomendada",
                ["de"] = "Prüfung empfohlen",
                ["it"] = "revisione consigliata"
            },
            ["source_card.hash"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "rév.",
                ["en"] = "rev",
                ["es"] = "rev.",
                ["pt"] = "rev.",
                ["de"] = "Rev.",
                ["it"] = "rev."
            },
            ["source_card.content_cards"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "cartes",
                ["en"] = "cards",
                ["es"] = "tarjetas",
                ["pt"] = "cartões",
                ["de"] = "Karten",
                ["it"] = "schede"
            },
            ["source_card.content_card_ids"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "cartes ID",
                ["en"] = "card IDs",
                ["es"] = "ID tarjetas",
                ["pt"] = "IDs cartões",
                ["de"] = "Karten-IDs",
                ["it"] = "ID schede"
            },
            ["source_card.content_card_evidence"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "preuve cartes",
                ["en"] = "card evidence",
                ["es"] = "evidencia tarjetas",
                ["pt"] = "evidência cartões",
                ["de"] = "Kartenbeleg",
                ["it"] = "evidenza schede"
            },
            ["source_card.content_card_facts"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "faits",
                ["en"] = "facts",
                ["es"] = "hechos",
                ["pt"] = "factos",
                ["de"] = "Fakten",
                ["it"] = "fatti"
            },
            ["source_card.content_card_scale_basis"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "base",
                ["en"] = "basis",
                ["es"] = "base",
                ["pt"] = "base",
                ["de"] = "Basis",
                ["it"] = "base"
            },
            ["source_card.content_card_non_scalable"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "non adaptable",
                ["en"] = "non-scalable",
                ["es"] = "no adaptable",
                ["pt"] = "não adaptável",
                ["de"] = "nicht skalierbar",
                ["it"] = "non scalabile"
            },
            ["source_card.content_card_values"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "valeurs",
                ["en"] = "values",
                ["es"] = "valores",
                ["pt"] = "valores",
                ["de"] = "Werte",
                ["it"] = "valori"
            },
            ["source_card.content_card_reasons"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "raisons",
                ["en"] = "reasons",
                ["es"] = "razones",
                ["pt"] = "razões",
                ["de"] = "Gründe",
                ["it"] = "ragioni"
            },
            ["source_card.profile_signals"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "profil",
                ["en"] = "profile",
                ["es"] = "perfil",
                ["pt"] = "perfil",
                ["de"] = "Profil",
                ["it"] = "profilo"
            },
            ["source_card.profile_terms"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "termes",
                ["en"] = "terms",
                ["es"] = "términos",
                ["pt"] = "termos",
                ["de"] = "Begriffe",
                ["it"] = "termini"
            },
            ["source_card.profile_keywords"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "mots-clés",
                ["en"] = "keywords",
                ["es"] = "palabras clave",
                ["pt"] = "palavras-chave",
                ["de"] = "Schlüsselwörter",
                ["it"] = "parole chiave"
            },
            ["source_card.profile_topics"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "thèmes",
                ["en"] = "topics",
                ["es"] = "temas",
                ["pt"] = "temas",
                ["de"] = "Themen",
                ["it"] = "temi"
            },
            ["source_card.profile_version"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "version",
                ["en"] = "version",
                ["es"] = "versión",
                ["pt"] = "versão",
                ["de"] = "Version",
                ["it"] = "versione"
            },
            ["source_card.profile_matches"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "correspondances",
                ["en"] = "matches",
                ["es"] = "coincidencias",
                ["pt"] = "correspondências",
                ["de"] = "Treffer",
                ["it"] = "corrispondenze"
            },
            ["source_card.category"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "catégorie",
                ["en"] = "category",
                ["es"] = "categoría",
                ["pt"] = "categoria",
                ["de"] = "Kategorie",
                ["it"] = "categoria"
            },
            ["source_card.selection_score"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "sélection",
                ["en"] = "selection",
                ["es"] = "selección",
                ["pt"] = "seleção",
                ["de"] = "Auswahl",
                ["it"] = "selezione"
            },
            ["source_card.content_role"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "type contenu",
                ["en"] = "content type",
                ["es"] = "tipo contenido",
                ["pt"] = "tipo conteúdo",
                ["de"] = "Inhaltstyp",
                ["it"] = "tipo contenuto"
            },
            ["source_card.content_density"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "densité",
                ["en"] = "density",
                ["es"] = "densidad",
                ["pt"] = "densidade",
                ["de"] = "Dichte",
                ["it"] = "densità"
            },
            ["source_card.retrieval_navigation"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "navigation",
                ["en"] = "navigation",
                ["es"] = "navegación",
                ["pt"] = "navegação",
                ["de"] = "Navigation",
                ["it"] = "navigazione"
            },
            ["source_card.navigation_reason"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "raison",
                ["en"] = "reason",
                ["es"] = "razón",
                ["pt"] = "razão",
                ["de"] = "Grund",
                ["it"] = "motivo"
            },
            ["source_card.content_role.content"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "contenu",
                ["en"] = "content",
                ["es"] = "contenido",
                ["pt"] = "conteúdo",
                ["de"] = "Inhalt",
                ["it"] = "contenuto"
            },
            ["source_card.content_role.navigation"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "navigation",
                ["en"] = "navigation",
                ["es"] = "navegación",
                ["pt"] = "navegação",
                ["de"] = "Navigation",
                ["it"] = "navigazione"
            },
            ["source_card.content_role.mixed_navigation_content"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "mixte",
                ["en"] = "mixed",
                ["es"] = "mixto",
                ["pt"] = "misto",
                ["de"] = "gemischt",
                ["it"] = "misto"
            },
            ["source_card.evidence_role"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "preuve",
                ["en"] = "evidence",
                ["es"] = "evidencia",
                ["pt"] = "evidencia",
                ["de"] = "Beleg",
                ["it"] = "evidenza"
            },
            ["source_card.evidence_role.actionable_item"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "utilisable",
                ["en"] = "usable",
                ["es"] = "utilizable",
                ["pt"] = "utilizavel",
                ["de"] = "nutzbar",
                ["it"] = "utilizzabile"
            },
            ["source_card.evidence_role.supporting_context"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "contexte",
                ["en"] = "context",
                ["es"] = "contexto",
                ["pt"] = "contexto",
                ["de"] = "Kontext",
                ["it"] = "contesto"
            },
            ["source_card.evidence_role.navigation"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "navigation",
                ["en"] = "navigation",
                ["es"] = "navegacion",
                ["pt"] = "navegacao",
                ["de"] = "Navigation",
                ["it"] = "navigazione"
            },
            ["source_card.evidence_role.fragment"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "fragment",
                ["en"] = "fragment",
                ["es"] = "fragmento",
                ["pt"] = "fragmento",
                ["de"] = "Fragment",
                ["it"] = "frammento"
            },
            ["source_card.evidence_role.low_confidence"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "confiance faible",
                ["en"] = "low confidence",
                ["es"] = "confianza baja",
                ["pt"] = "confianca baixa",
                ["de"] = "geringe Sicherheit",
                ["it"] = "bassa fiducia"
            },
            ["source_card.evidence_role.advisory"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "conseil",
                ["en"] = "advisory",
                ["es"] = "asesoría",
                ["pt"] = "aconselhamento",
                ["de"] = "Hinweis",
                ["it"] = "consiglio"
            },
            ["source_card.quality.ok"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "texte OK",
                ["en"] = "text OK",
                ["es"] = "texto correcto",
                ["pt"] = "texto OK",
                ["de"] = "Text OK",
                ["it"] = "testo OK"
            },
            ["source_card.quality.document_ok"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "document OK",
                ["en"] = "document OK",
                ["es"] = "documento correcto",
                ["pt"] = "documento OK",
                ["de"] = "Dokument OK",
                ["it"] = "documento OK"
            },
            ["source_card.quality.page_ok"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "page OK",
                ["en"] = "page OK",
                ["es"] = "página correcta",
                ["pt"] = "página OK",
                ["de"] = "Seite OK",
                ["it"] = "pagina OK"
            },
            ["source_card.quality.page_ok_with_images"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "page OK avec images",
                ["en"] = "page OK with images",
                ["es"] = "página correcta con imágenes",
                ["pt"] = "página OK com imagens",
                ["de"] = "Seite OK mit Bildern",
                ["it"] = "pagina OK con immagini"
            },
            ["source_card.quality.page_ok_indexed_by_context"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "page OK via contexte",
                ["en"] = "page OK via context",
                ["es"] = "página correcta por contexto",
                ["pt"] = "página OK por contexto",
                ["de"] = "Seite OK über Kontext",
                ["it"] = "pagina OK tramite contesto"
            },
            ["source_card.quality.page_ok_empty_text"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "page vide OK",
                ["en"] = "empty page OK",
                ["es"] = "página vacía correcta",
                ["pt"] = "página vazia OK",
                ["de"] = "leere Seite OK",
                ["it"] = "pagina vuota OK"
            },
            ["source_card.quality.page_ok_low_value_text"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "texte faible valeur OK",
                ["en"] = "low-value text OK",
                ["es"] = "texto de bajo valor correcto",
                ["pt"] = "texto de baixo valor OK",
                ["de"] = "Text mit geringem Wert OK",
                ["it"] = "testo a basso valore OK"
            },
            ["source_card.quality.low_text"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "texte faible",
                ["en"] = "low text",
                ["es"] = "texto insuficiente",
                ["pt"] = "texto insuficiente",
                ["de"] = "wenig Text",
                ["it"] = "testo scarso"
            },
            ["source_card.quality.empty_text"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "texte vide",
                ["en"] = "empty text",
                ["es"] = "texto vacío",
                ["pt"] = "texto vazio",
                ["de"] = "leerer Text",
                ["it"] = "testo vuoto"
            },
            ["source_card.quality.manual_review_low_text"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "texte faible, revue requise",
                ["en"] = "low text, review required",
                ["es"] = "texto insuficiente, revisión requerida",
                ["pt"] = "texto insuficiente, revisão necessária",
                ["de"] = "wenig Text, Prüfung nötig",
                ["it"] = "testo scarso, revisione richiesta"
            },
            ["source_card.quality.ocr_applied_ok"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "OCR appliqué, texte OK",
                ["en"] = "OCR applied, text OK",
                ["es"] = "OCR aplicado, texto correcto",
                ["pt"] = "OCR aplicado, texto OK",
                ["de"] = "OCR angewendet, Text OK",
                ["it"] = "OCR applicato, testo OK"
            },
            ["source_card.quality.ocr_applied_ok_with_page_warnings"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "OCR appliqué, alertes page",
                ["en"] = "OCR applied, page warnings",
                ["es"] = "OCR aplicado, avisos de página",
                ["pt"] = "OCR aplicado, avisos de página",
                ["de"] = "OCR angewendet, Seitenhinweise",
                ["it"] = "OCR applicato, avvisi pagina"
            },
            ["source_card.quality.extraction_ok"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "extraction OK",
                ["en"] = "extraction OK",
                ["es"] = "extracción correcta",
                ["pt"] = "extração OK",
                ["de"] = "Extraktion OK",
                ["it"] = "estrazione OK"
            },
            ["source_card.quality.extraction_ok_with_page_review"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "extraction OK, revue page",
                ["en"] = "extraction OK, page review",
                ["es"] = "extracción correcta, revisar página",
                ["pt"] = "extração OK, rever página",
                ["de"] = "Extraktion OK, Seitenprüfung",
                ["it"] = "estrazione OK, revisione pagina"
            },
            ["source_card.quality.extraction_ok_with_page_warnings"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "extraction OK, alertes page",
                ["en"] = "extraction OK, page warnings",
                ["es"] = "extracción correcta, avisos de página",
                ["pt"] = "extração OK, avisos de página",
                ["de"] = "Extraktion OK, Seitenhinweise",
                ["it"] = "estrazione OK, avvisi pagina"
            },
            ["source_card.quality.text_extraction_ok_with_images"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "texte OK avec images",
                ["en"] = "text OK with images",
                ["es"] = "texto correcto con imágenes",
                ["pt"] = "texto OK com imagens",
                ["de"] = "Text OK mit Bildern",
                ["it"] = "testo OK con immagini"
            },
            ["source_card.quality.image_ocr_applied_ok"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "OCR image appliqué",
                ["en"] = "image OCR applied",
                ["es"] = "OCR de imagen aplicado",
                ["pt"] = "OCR de imagem aplicado",
                ["de"] = "Bild-OCR angewendet",
                ["it"] = "OCR immagine applicato"
            },
            ["source_card.quality.ocr_failed_or_insufficient"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "OCR insuffisant",
                ["en"] = "OCR insufficient",
                ["es"] = "OCR insuficiente",
                ["pt"] = "OCR insuficiente",
                ["de"] = "OCR unzureichend",
                ["it"] = "OCR insufficiente"
            },
            ["source_card.quality.ocr_applied_low_confidence"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "OCR appliqué, confiance faible",
                ["en"] = "OCR applied, low confidence",
                ["es"] = "OCR aplicado, confianza baja",
                ["pt"] = "OCR aplicado, baixa confiança",
                ["de"] = "OCR angewendet, geringe Sicherheit",
                ["it"] = "OCR applicato, fiducia bassa"
            },
            ["source_card.quality.manual_review_empty_text"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "texte vide, revue requise",
                ["en"] = "empty text, review required",
                ["es"] = "texto vacío, revisión requerida",
                ["pt"] = "texto vazio, revisão necessária",
                ["de"] = "leerer Text, Prüfung nötig",
                ["it"] = "testo vuoto, revisione richiesta"
            },
            ["source_card.quality.manual_review_probable_ocr_noise"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "bruit OCR probable",
                ["en"] = "probable OCR noise",
                ["es"] = "probable ruido OCR",
                ["pt"] = "provável ruído OCR",
                ["de"] = "wahrscheinliches OCR-Rauschen",
                ["it"] = "probabile rumore OCR"
            },
            ["source_card.quality.manual_review_text_not_indexed"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "texte non indexé, revue requise",
                ["en"] = "text not indexed, review required",
                ["es"] = "texto no indexado, revisión requerida",
                ["pt"] = "texto não indexado, revisão necessária",
                ["de"] = "Text nicht indexiert, Prüfung nötig",
                ["it"] = "testo non indicizzato, revisione richiesta"
            },
            ["source_card.quality.ocr_required_but_disabled"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "OCR requis mais désactivé",
                ["en"] = "OCR required but disabled",
                ["es"] = "OCR requerido pero desactivado",
                ["pt"] = "OCR necessário mas desativado",
                ["de"] = "OCR erforderlich, aber deaktiviert",
                ["it"] = "OCR richiesto ma disattivato"
            },
            ["source_card.quality.no_indexable_text"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "aucun texte indexable",
                ["en"] = "no indexable text",
                ["es"] = "sin texto indexable",
                ["pt"] = "sem texto indexável",
                ["de"] = "kein indexierbarer Text",
                ["it"] = "nessun testo indicizzabile"
            },
            ["source_card.quality.scanned_pdf_not_indexable"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "PDF scanné non indexable",
                ["en"] = "scanned PDF not indexable",
                ["es"] = "PDF escaneado no indexable",
                ["pt"] = "PDF digitalizado não indexável",
                ["de"] = "gescanntes PDF nicht indexierbar",
                ["it"] = "PDF scansionato non indicizzabile"
            },
            ["source_card.quality.document_not_indexable"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "document non indexable",
                ["en"] = "document not indexable",
                ["es"] = "documento no indexable",
                ["pt"] = "documento não indexável",
                ["de"] = "Dokument nicht indexierbar",
                ["it"] = "documento non indicizzabile"
            },
            ["source_card.quality.unknown"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "qualité inconnue",
                ["en"] = "unknown quality",
                ["es"] = "calidad desconocida",
                ["pt"] = "qualidade desconhecida",
                ["de"] = "unbekannte Qualität",
                ["it"] = "qualità sconosciuta"
            }
,
            ["rag_degraded_sources_header"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fr"] = "Meilleures sources trouvées :",
                ["en"] = "Best sources found:",
                ["es"] = "Mejores fuentes encontradas:",
                ["pt"] = "Melhores fontes encontradas:",
                ["de"] = "Beste gefundene Quellen:",
                ["it"] = "Migliori fonti trovate:"
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
            ["fr"] = new[] { "bonjour", "salut", "coucou", "donne", "liste", "serveur", "arborescence", "résumé", "resume", "français", "francais", "merci", "stp", "comment", "documents", "document", "quels", "quelles", "présents", "present", "présent", "combien", "qui", "tu", "quoi", "categorie", "catégorie", "statistiques", "sans", "vient", "viens", "lister" },
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
    public static string RagSearchBusy(string? language) => Get("rag_search_busy", language);
    public static string DocumentListError(string? language) => Get("document_list_error", language);
    public static string DocumentTreeError(string? language) => Get("document_tree_error", language);
    public static string ShortOverviewUnavailable(string? language) => Get("short_overview_unavailable", language);
    public static string SummaryUnavailable(string? language) => Get("summary_unavailable", language);
    public static string SummaryNotStored(string? language) => Get("summary_not_stored", language);
    public static string SummaryStoreRequiresAdmin(string? language) => Get("summary_store_requires_admin", language);
    public static string SummaryAlreadyStored(string? language) => Get("summary_already_stored", language);
    public static string SummaryStoreDone(string? language) => Get("summary_store_done", language);
    public static string SummaryStoreQueued(string? language) => Get("summary_store_queued", language);
    public static string SummaryStoreBackofficeUnavailable(string? language) => Get("summary_store_backoffice_unavailable", language);
    public static string SummaryStoreFailed(string? language) => Get("summary_store_failed", language);
    public static string GuidedCommandHelpRequired(string? language) => Get("guided_command_help_required", language);
    public static string HelpOnlyCommandUseHelp(string? language) => Get("help_only_command_use_help", language);
    public static string RagDegradedSourcesHeader(string? language) => Get("rag_degraded_sources_header", language);

    public static string SourceCardLabel(string key, string? language)
        => Get($"source_card.{key}", language);

    public static string LocalizedSourceSelectionHintRole(string? role, string? uiLanguage)
    {
        var normalized = NormalizeIdentifier(role);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        var key = $"source_card.evidence_role.{normalized}";
        var value = Get(key, uiLanguage);
        return !string.Equals(value, key, StringComparison.OrdinalIgnoreCase)
            ? value
            : HumanizeIdentifier(role!);
    }

    public static string LocalizedSourceQualityStatus(string? status, string? uiLanguage)
    {
        var normalized = NormalizeIdentifier(status);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        var key = $"source_card.quality.{normalized}";
        var value = Get(key, uiLanguage);
        return !string.Equals(value, key, StringComparison.OrdinalIgnoreCase)
            ? value
            : HumanizeIdentifier(status!);
    }

    public static string LocalizedSourceOcrReason(string? reason, string? uiLanguage)
    {
        var normalized = NormalizeIdentifier(reason);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        var key = $"source_card.ocr_reason.{normalized}";
        var value = Get(key, uiLanguage);
        return !string.Equals(value, key, StringComparison.OrdinalIgnoreCase)
            ? value
            : HumanizeIdentifier(reason!);
    }

    public static string LocalizedSourceOcrMode(string? mode, string? uiLanguage)
    {
        var normalized = NormalizeIdentifier(mode);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        var key = $"source_card.ocr_mode.{normalized}";
        var value = Get(key, uiLanguage);
        return !string.Equals(value, key, StringComparison.OrdinalIgnoreCase)
            ? value
            : HumanizeIdentifier(mode!);
    }

    public static string LocalizedSourceLanguageName(string? language, string? uiLanguage)
    {
        if (string.IsNullOrWhiteSpace(language))
            return string.Empty;

        var trimmed = language.Trim();
        if (TryMapLanguageAlias(trimmed, out var mapped))
            return LocalizedLanguageName(mapped, uiLanguage);

        var candidate = trimmed.Replace('_', '-');
        var dash = candidate.IndexOf('-', StringComparison.Ordinal);
        if (dash > 0 && TryMapLanguageAlias(candidate[..dash], out mapped))
            return LocalizedLanguageName(mapped, uiLanguage);

        try
        {
            var culture = CultureInfo.GetCultureInfo(candidate);
            return culture.NativeName;
        }
        catch (CultureNotFoundException)
        {
            return LooksLikeLanguageCode(trimmed)
                ? trimmed.ToUpperInvariant()
                : HumanizeIdentifier(trimmed);
        }
    }

    public static string NormalizeSourceLanguageIdentifier(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return string.Empty;

        var trimmed = language.Trim();
        if (TryMapLanguageAlias(trimmed, out var mapped))
            return mapped;

        var token = NormalizeIdentifier(trimmed.Replace('_', '-'));
        var dash = token.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 ? token[..dash] : token;
    }

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

    private static string NormalizeIdentifier(string? value)
        => StripDiacritics(value ?? string.Empty).Trim().Replace(' ', '_').ToLowerInvariant();

    private static string HumanizeIdentifier(string value)
    {
        var words = Regex.Split(value.Trim(), @"[\s_\-]+")
            .Where(static word => !string.IsNullOrWhiteSpace(word))
            .ToArray();
        if (words.Length == 0)
            return string.Empty;

        var text = string.Join(" ", words.Select(static word => word.Equals("ocr", StringComparison.OrdinalIgnoreCase)
            ? "OCR"
            : word.ToLowerInvariant()));
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static bool LooksLikeLanguageCode(string value)
        => Regex.IsMatch(value.Trim(), @"^[A-Za-z]{2,3}(?:[-_][A-Za-z0-9]{2,8})*$", RegexOptions.CultureInvariant);

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
