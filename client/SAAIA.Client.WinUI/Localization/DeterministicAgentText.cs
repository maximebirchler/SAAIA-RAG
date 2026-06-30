using System;
using System.Collections.Generic;
using System.Text;
using SAAIA.Client.WinUI.Services.ToolAgent;

namespace SAAIA.Client.WinUI.Localization;

internal static class DeterministicAgentText
{
    private static string Lang(string? language) => LocalizedStrings.NormalizeLanguage(language);

    private static string Pick(string? language, string fr, string en, string es, string pt, string de, string it)
        => Lang(language) switch
        {
            "en" => en,
            "es" => es,
            "pt" => pt,
            "de" => de,
            "it" => it,
            _ => fr
        };

    private static string WithOptionalDocRef(string docRef, string? language, Func<string, string> builder)
    {
        var suffix = string.IsNullOrWhiteSpace(docRef)
            ? string.Empty
            : Lang(language) switch
            {
                "en" => $" for {docRef}",
                "es" => $" para {docRef}",
                "pt" => $" para {docRef}",
                "de" => $" für {docRef}",
                "it" => $" per {docRef}",
                _ => $" pour {docRef}"
            };
        return builder(suffix);
    }

    public static string PhaseRouter(string? language)
        => Pick(language, "J'analyse la demande…", "I am reading the request…", "Estoy leyendo la solicitud…", "Estou a ler o pedido…", "Ich lese die Anfrage…", "Sto leggendo la richiesta…");

    public static string PhaseClarification(string? language)
        => Pick(language, "Je vérifie ce qu'il faut préciser…", "I am checking what needs to be clarified…", "Estoy comprobando qué hay que precisar…", "Estou a verificar o que precisa de ser esclarecido…", "Ich prüfe, was geklärt werden muss…", "Sto verificando cosa va chiarito…");

    public static string PhaseTools(string? language)
        => Pick(language, "Je consulte les informations disponibles…", "I am checking the available information…", "Estoy consultando la información disponible…", "Estou a consultar as informações disponíveis…", "Ich prüfe die verfügbaren Informationen…", "Sto consultando le informazioni disponibili…");

    public static string PhaseWriting(string? language)
        => Pick(language, "Je prépare une réponse claire…", "I am preparing a clear answer…", "Estoy preparando una respuesta clara…", "Estou a preparar uma resposta clara…", "Ich bereite eine klare Antwort vor…", "Sto preparando una risposta chiara…");

    public static string PhaseSummary(string? language)
        => Pick(language, "Je synthétise les informations…", "I am summarizing the information…", "Estoy sintetizando la información…", "Estou a sintetizar as informações…", "Ich fasse die Informationen zusammen…", "Sto sintetizzando le informazioni…");

    public static string PhaseRag(string? language)
        => Pick(language, "Je cherche dans les documents…", "I am searching the documents…", "Estoy buscando en los documentos…", "Estou a pesquisar nos documentos…", "Ich suche in den Dokumenten…", "Sto cercando nei documenti…");

    public static string ProgressCollectInformation(string? language)
        => Pick(language, "Je collecte les informations utiles…", "I am collecting the useful information…", "Estoy recopilando la información útil…", "Estou coletando as informações úteis…", "Ich sammle die nützlichen Informationen…", "Sto raccogliendo le informazioni utili…");

    public static string ProgressPlanRetrievalStrategy(string? language)
        => Pick(language, "Je choisis la prochaine recherche documentaire...", "I am choosing the next document search...", "Estoy eligiendo la siguiente busqueda documental...", "Estou a escolher a proxima pesquisa documental...", "Ich waehle die naechste Dokumentensuche...", "Sto scegliendo la prossima ricerca nei documenti...");

    public static string ProgressSearchSourceBackedCandidates(string? language)
        => Pick(language, "Je cherche des candidats exploitables dans les documents...", "I am searching the documents for usable candidates...", "Estoy buscando candidatos utiles en los documentos...", "Estou a procurar candidatos utilizaveis nos documentos...", "Ich suche in den Dokumenten nach brauchbaren Kandidaten...", "Sto cercando candidati utilizzabili nei documenti...");

    public static string ProgressExploreFollowupSources(string? language)
        => Pick(language, "J'explore les pistes trouvees dans les sources...", "I am exploring leads found in the sources...", "Estoy explorando pistas encontradas en las fuentes...", "Estou a explorar pistas encontradas nas fontes...", "Ich pruefe Hinweise aus den Quellen weiter...", "Sto esplorando indizi trovati nelle fonti...");

    public static string ProgressInspectDocumentStructure(string? language)
        => Pick(language, "Je lis la structure des documents pour mieux cibler la suite...", "I am reading document structure to target the next step...", "Estoy leyendo la estructura de los documentos para orientar la siguiente busqueda...", "Estou a ler a estrutura dos documentos para orientar a etapa seguinte...", "Ich lese die Dokumentstruktur, um gezielter weiterzusuchen...", "Sto leggendo la struttura dei documenti per mirare meglio il seguito...");

    public static string ProgressVerifyCandidateCoverage(string? language)
        => Pick(language, "Je verifie que les candidats couvrent vraiment la demande...", "I am checking that the candidates really cover the request...", "Estoy comprobando que los candidatos cubran realmente la solicitud...", "Estou a verificar se os candidatos cobrem mesmo o pedido...", "Ich pruefe, ob die Kandidaten die Anfrage wirklich abdecken...", "Sto verificando che i candidati coprano davvero la richiesta...");

    public static string ProgressCorrectPreviousInterpretation(string? language)
        => Pick(language, "Je corrige mon interprétation précédente…", "I am correcting my previous interpretation…", "Estoy corrigiendo mi interpretación anterior…", "Estou corrigindo minha interpretação anterior…", "Ich korrigiere meine vorherige Interpretation…", "Sto correggendo la mia interpretazione precedente…");

    public static string ProgressDraftFinalAnswer(string? language)
        => Pick(language, "Je rédige la réponse finale…", "I am drafting the final answer…", "Estoy redactando la respuesta final…", "Estou redigindo a resposta final…", "Ich formuliere die endgültige Antwort…", "Sto redigendo la risposta finale…");

    public static string ProgressCheckAlignmentWithSources(string? language)
        => Pick(language, "Je vérifie l’alignement avec les sources disponibles…", "I am checking alignment with the available sources…", "Estoy comprobando la coherencia con las fuentes disponibles…", "Estou verificando o alinhamento com as fontes disponíveis…", "Ich prüfe die Übereinstimmung mit den verfügbaren Quellen…", "Sto verificando l’allineamento con le fonti disponibili…");


    public static string ProgressRetrieveRepresentativePassages(string? language)
        => Pick(language, "Je récupère des passages représentatifs du document…", "I am retrieving representative passages from the document…", "Estoy recuperando pasajes representativos del documento…", "Estou recuperando trechos representativos do documento…", "Ich rufe repräsentative Passagen aus dem Dokument ab…", "Sto recuperando passaggi rappresentativi dal documento…");

    public static string ProgressComposeShortOverview(string? language)
        => Pick(language, "Je formule un aperçu court du document…", "I am drafting a short overview of the document…", "Estoy redactando una vista breve del documento…", "Estou redigindo uma visão breve do documento…", "Ich formuliere eine kurze Übersicht des Dokuments…", "Sto redigendo una breve panoramica del documento…");

    public static string ProgressCheckStoredSummaryAvailable(string? language)
        => Pick(language, "Je vérifie si un résumé stocké est déjà disponible…", "I am checking whether a stored summary is already available…", "Estoy comprobando si ya hay un resumen almacenado disponible…", "Estou verificando se já existe um resumo armazenado disponível…", "Ich prüfe, ob bereits eine gespeicherte Zusammenfassung verfügbar ist…", "Sto verificando se è già disponibile un riassunto salvato…");

    public static string ProgressReturnStoredSummary(string? language)
        => Pick(language, "Je renvoie le résumé stocké…", "I am returning the stored summary…", "Estoy devolviendo el resumen almacenado…", "Estou retornando o resumo armazenado…", "Ich gebe die gespeicherte Zusammenfassung zurück…", "Sto restituendo il riassunto salvato…");

    public static string ProgressCheckReusableSummaryCache(string? language)
        => Pick(language, "Je vérifie le cache de résumés réutilisables…", "I am checking the reusable summary cache…", "Estoy comprobando la caché de resúmenes reutilizables…", "Estou verificando o cache de resumos reutilizáveis…", "Ich prüfe den Cache wiederverwendbarer Zusammenfassungen…", "Sto verificando la cache dei riassunti riutilizzabili…");

    public static string ProgressReusableSummaryAlreadyAvailable(string? language)
        => Pick(language, "Un résumé réutilisable est déjà disponible. Je le renvoie…", "A reusable summary is already available. I am returning it…", "Ya hay un resumen reutilizable disponible. Lo devuelvo…", "Já existe um resumo reutilizável disponível. Vou retorná-lo…", "Eine wiederverwendbare Zusammenfassung ist bereits verfügbar. Ich gebe sie zurück…", "È già disponibile un riassunto riutilizzabile. Lo restituisco…");

    public static string ProgressGenerateAndStoreReusableSummary(string? language)
        => Pick(language, "Je génère et je stocke le résumé réutilisable…", "I am generating and storing the reusable summary…", "Estoy generando y almacenando el resumen reutilizable…", "Estou gerando e armazenando o resumo reutilizável…", "Ich erstelle und speichere die wiederverwendbare Zusammenfassung…", "Sto generando e salvando il riassunto riutilizzabile…");

    public static string ProgressCheckExistingStoredSummary(string? language)
        => Pick(language, "Je vérifie s'il existe déjà un résumé stocké…", "I am checking whether a stored summary already exists…", "Estoy comprobando si ya existe un resumen almacenado…", "Estou verificando se já existe um resumo armazenado…", "Ich prüfe, ob bereits eine gespeicherte Zusammenfassung existiert…", "Sto verificando se esiste già un riassunto salvato…");

    public static string ProgressRephraseStoredSummaryForDisplay(string? language)
        => Pick(language, "Je reformule le résumé stocké pour l'affichage…", "I am rephrasing the stored summary for display…", "Estoy reformulando el resumen almacenado para mostrarlo…", "Estou reformulando o resumo armazenado para exibição…", "Ich formuliere die gespeicherte Zusammenfassung für die Anzeige um…", "Sto riformulando il riassunto salvato per la visualizzazione…");

    public static string ProgressBuildLiveSummaryFromDocument(string? language)
        => Pick(language, "Je construis un résumé live à partir du document…", "I am building a live summary from the document…", "Estoy construyendo un resumen en vivo a partir del documento…", "Estou criando um resumo ao vivo a partir do documento…", "Ich erstelle eine Live-Zusammenfassung aus dem Dokument…", "Sto costruendo un riassunto live a partire dal documento…");

    public static string ProgressWriteFinalSummary(string? language)
        => Pick(language, "Je rédige le résumé final…", "I am drafting the final summary…", "Estoy redactando el resumen final…", "Estou redigindo o resumo final…", "Ich formuliere die endgültige Zusammenfassung…", "Sto redigendo il riassunto finale…");


    public static string ProgressCheckStoredSummaryForDocument(string docRef, string? language)
        => WithOptionalDocRef(docRef, language,
            (suffix) => Pick(language,
                $"Je vérifie s'il existe déjà un résumé stocké{suffix}…",
                $"I am checking whether a stored summary already exists{suffix}…",
                $"Estoy comprobando si ya existe un resumen almacenado{suffix}…",
                $"Estou verificando se já existe um resumo armazenado{suffix}…",
                $"Ich prüfe, ob bereits eine gespeicherte Zusammenfassung existiert{suffix}…",
                $"Sto verificando se esiste già un riassunto salvato{suffix}…"));

    public static string ProgressLoadStoredSummaryForDocument(string docRef, string? language)
        => WithOptionalDocRef(docRef, language,
            (suffix) => Pick(language,
                $"Je charge le résumé stocké{suffix}…",
                $"I am loading the stored summary{suffix}…",
                $"Estoy cargando el resumen almacenado{suffix}…",
                $"Estou carregando o resumo armazenado{suffix}…",
                $"Ich lade die gespeicherte Zusammenfassung{suffix}…",
                $"Sto caricando il riassunto salvato{suffix}…"));

    public static string ProgressBuildLiveSummaryForDocument(string docRef, string? language)
        => WithOptionalDocRef(docRef, language,
            (suffix) => Pick(language,
                $"Je construis un résumé live à partir de passages représentatifs{suffix}…",
                $"I am building a live summary from representative passages{suffix}…",
                $"Estoy construyendo un resumen en vivo a partir de pasajes representativos{suffix}…",
                $"Estou criando um resumo ao vivo a partir de trechos representativos{suffix}…",
                $"Ich erstelle eine Live-Zusammenfassung aus repräsentativen Passagen{suffix}…",
                $"Sto costruendo un riassunto live a partire da passaggi rappresentativi{suffix}…"));

    public static string DocumentNotFound(string? language)
        => Pick(language, "Le document n'a pas pu être trouvé.", "The document could not be found.", "No se pudo encontrar el documento.", "Não foi possível encontrar o documento.", "Das Dokument konnte nicht gefunden werden.", "Non è stato possibile trovare il documento.");

    public static string SourceHeading(string? language)
        => Pick(language, "Source", "Source", "Fuente", "Fonte", "Quelle", "Fonte");

    public static string TreeSkippedLocalUnresolved(string? language, int dropped)
        => Pick(language,
            $"({dropped} élément(s) ignoré(s) car le fichier n'a pas pu être résolu localement.)",
            $"({dropped} item(s) were skipped because the file could not be resolved locally.)",
            $"({dropped} elemento(s) omitido(s) porque el archivo no pudo resolverse localmente.)",
            $"({dropped} item(ns) foram ignorado(s) porque o arquivo não pôde ser resolvido localmente.)",
            $"({dropped} Element(e) wurden übersprungen, weil die Datei lokal nicht aufgelöst werden konnte.)",
            $"({dropped} elemento/i ignorato/i perché il file non ha potuto essere risolto localmente.)");

    public static string DegradedNoLlm(string? language)
        => Pick(language,
            "Je ne peux pas rédiger une réponse complète pour le moment. Les sources trouvées restent disponibles, mais je préfère ne pas te donner une synthèse approximative.",
            "I cannot draft a complete answer right now. The found sources remain available, but I would rather avoid giving you an approximate synthesis.",
            "Ahora no puedo redactar una respuesta completa. Las fuentes encontradas siguen disponibles, pero prefiero no darte una síntesis aproximada.",
            "Neste momento não consigo redigir uma resposta completa. As fontes encontradas continuam disponíveis, mas prefiro evitar uma síntese aproximada.",
            "Ich kann im Moment keine vollständige Antwort formulieren. Die gefundenen Quellen bleiben verfügbar, aber ich möchte dir keine ungefähre Zusammenfassung geben.",
            "In questo momento non posso scrivere una risposta completa. Le fonti trovate restano disponibili, ma preferisco evitare una sintesi approssimativa.");

    public static string AnswerNotEnoughUsableInfo(string? language)
        => Pick(language,
            "Je n'ai pas assez d'informations exploitables pour répondre clairement.",
            "I do not have enough usable information to answer clearly.",
            "No tengo suficiente información utilizable para responder con claridad.",
            "Não tenho informações utilizáveis suficientes para responder com clareza.",
            "Ich habe nicht genügend verwertbare Informationen, um klar zu antworten.",
            "Non ho informazioni utilizzabili sufficienti per rispondere chiaramente.");

    public static string RagSearchBusy(string? language)
        => Pick(language,
            "La recherche dans les documents est momentanément très sollicitée. Réessaie dans quelques instants : la réponse sera meilleure avec des sources complètes.",
            "Document search is temporarily under heavy load. Please retry in a moment: the answer will be better with complete sources.",
            "La búsqueda en los documentos está muy solicitada temporalmente. Vuelve a intentarlo en unos instantes: la respuesta será mejor con fuentes completas.",
            "A pesquisa nos documentos está temporariamente muito solicitada. Tenta novamente dentro de instantes: a resposta será melhor com fontes completas.",
            "Die Suche in den Dokumenten ist vorübergehend stark ausgelastet. Bitte versuche es gleich erneut: Mit vollständigen Quellen wird die Antwort besser.",
            "La ricerca nei documenti è temporaneamente molto sollecitata. Riprova tra poco: la risposta sarà migliore con fonti complete.");

    public static string SourceBackedClarificationRequest(string? language)
        => Pick(language,
            "Peux-tu préciser ce que tu veux vérifier, ou le périmètre dans lequel je dois chercher ?",
            "Could you specify what you want me to check, or the scope I should search in?",
            "¿Puedes precisar qué quieres que compruebe, o el ámbito en el que debo buscar?",
            "Podes especificar o que queres verificar, ou o âmbito em que devo pesquisar?",
            "Kannst du genauer sagen, was ich prüfen soll, oder in welchem Bereich ich suchen soll?",
            "Puoi precisare che cosa vuoi verificare, o l'ambito in cui devo cercare?");

    public static string SourceBackedExpandedSearchOffer(string? language)
        => Pick(language,
            "Je peux élargir la recherche pour essayer de trouver des sources plus adaptées.",
            "I can broaden the search to try to find more suitable sources.",
            "Puedo ampliar la búsqueda para intentar encontrar fuentes más adecuadas.",
            "Posso alargar a pesquisa para tentar encontrar fontes mais adequadas.",
            "Ich kann die Suche erweitern, um passendere Quellen zu finden.",
            "Posso ampliare la ricerca per provare a trovare fonti più adatte.");

    public static string DocumentsCount(int total, string? language)
        => Pick(language,
            $"Il y a actuellement {total} document(s) indexé(s) sur le serveur.",
            $"There are currently {total} indexed document(s) on the server.",
            $"Actualmente hay {total} documento(s) indexado(s) en el servidor.",
            $"Atualmente há {total} documento(s) indexado(s) no servidor.",
            $"Derzeit sind {total} Dokument(e) auf dem Server indexiert.",
            $"Attualmente ci sono {total} documento/i indicizzato/i sul server.");

    public static string DocumentsListHeader(string? language)
        => Pick(language,
            "Documents présents sur le serveur :",
            "Documents present on the server:",
            "Documentos presentes en el servidor:",
            "Documentos presentes no servidor:",
            "Dokumente auf dem Server:",
            "Documenti presenti sul server:");

    public static string DocumentsListHeader(string? language, string? scopePath)
    {
        var scope = (scopePath ?? string.Empty).Replace('\\', '/').Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(scope))
            return DocumentsListHeader(language);

        return Pick(language,
            $"Documents de la catégorie {scope} :",
            $"Documents in category {scope}:",
            $"Documentos de la categoría {scope}:",
            $"Documentos da categoria {scope}:",
            $"Dokumente der Kategorie {scope}:",
            $"Documenti della categoria {scope}:");
    }

    public static string DocumentsSearchHeader(string? language, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return DocumentsListHeader(language);

        return Pick(language,
            $"Documents correspondant à la recherche « {query} » :",
            $"Documents matching search \"{query}\":",
            $"Documentos que coinciden con la búsqueda « {query} »:",
            $"Documentos correspondentes à pesquisa \"{query}\":",
            $"Dokumente passend zur Suche \"{query}\":",
            $"Documenti corrispondenti alla ricerca \"{query}\":");
    }

    public static string EmptyFoldersCount(int total, string? language)
        => Pick(language,
            $"Il y a actuellement {total} dossier(s) vide(s) sur le serveur.",
            $"There are currently {total} empty folder(s) on the server.",
            $"Actualmente hay {total} carpeta(s) vacía(s) en el servidor.",
            $"Atualmente há {total} pasta(s) vazia(s) no servidor.",
            $"Derzeit gibt es {total} leere Ordner auf dem Server.",
            $"Attualmente ci sono {total} cartella/e vuota/e sul server.");

    public static string NoEmptyFoldersFound(string? language)
        => Pick(language,
            "Aucun dossier vide n'a été trouvé sur le serveur.",
            "No empty folders were found on the server.",
            "No se encontraron carpetas vacías en el servidor.",
            "Nenhuma pasta vazia foi encontrada no servidor.",
            "Auf dem Server wurden keine leeren Ordner gefunden.",
            "Non sono state trovate cartelle vuote sul server.");

    public static string EmptyFoldersHeader(string? language)
        => Pick(language,
            "Dossiers vides sur le serveur :",
            "Empty folders on the server:",
            "Carpetas vacías en el servidor:",
            "Pastas vazias no servidor:",
            "Leere Ordner auf dem Server:",
            "Cartelle vuote sul server:");


    public static string CategoriesListHeader(string? language)
        => Pick(language,
            "Catégories présentes sur le serveur :",
            "Categories present on the server:",
            "Categorías presentes en el servidor:",
            "Categorias presentes no servidor:",
            "Kategorien auf dem Server:",
            "Categorie presenti sul server:");

    public static string CategoriesCount(int total, string? language)
        => Pick(language,
            $"Il y a actuellement {total} catégorie(s) de premier niveau sur le serveur.",
            $"There are currently {total} first-level category(ies) on the server.",
            $"Actualmente hay {total} categoría(s) de primer nivel en el servidor.",
            $"Atualmente há {total} categoria(s) de primeiro nível no servidor.",
            $"Derzeit gibt es {total} Kategorien der ersten Ebene auf dem Server.",
            $"Attualmente ci sono {total} categorie di primo livello sul server.");

    private static string BoolWord(bool value, string? language)
        => value
            ? Pick(language, "oui", "yes", "si", "sim", "ja", "si")
            : Pick(language, "non", "no", "no", "nao", "nein", "no");

    public static string DiagnosticPerformanceHeader(string? language)
        => Pick(language,
            "Diagnostic memoire et performance :",
            "Memory and performance diagnostics:",
            "Diagnostico de memoria y rendimiento:",
            "Diagnostico de memoria e desempenho:",
            "Speicher- und Leistungsdiagnose:",
            "Diagnostica memoria e prestazioni:");

    public static string DiagnosticPerformanceProfile(string profile, int schemaVersion, string? cdcAlignment, string? language)
        => Pick(language,
            $"- Profil : {profile} (schema v{schemaVersion}, CDC {cdcAlignment})",
            $"- Profile: {profile} (schema v{schemaVersion}, CDC {cdcAlignment})",
            $"- Perfil: {profile} (schema v{schemaVersion}, CDC {cdcAlignment})",
            $"- Perfil: {profile} (schema v{schemaVersion}, CDC {cdcAlignment})",
            $"- Profil: {profile} (Schema v{schemaVersion}, CDC {cdcAlignment})",
            $"- Profilo: {profile} (schema v{schemaVersion}, CDC {cdcAlignment})");

    public static string DiagnosticPerformanceTimings(long routerMs, long toolsMs, long writerMs, long totalMs, string? language)
        => Pick(language,
            $"- Timings : routeur {routerMs} ms, outils {toolsMs} ms, redaction {writerMs} ms, total {totalMs} ms",
            $"- Timings: router {routerMs} ms, tools {toolsMs} ms, writer {writerMs} ms, total {totalMs} ms",
            $"- Tiempos: router {routerMs} ms, herramientas {toolsMs} ms, redaccion {writerMs} ms, total {totalMs} ms",
            $"- Tempos: roteador {routerMs} ms, ferramentas {toolsMs} ms, redacao {writerMs} ms, total {totalMs} ms",
            $"- Zeiten: Router {routerMs} ms, Werkzeuge {toolsMs} ms, Schreiben {writerMs} ms, gesamt {totalMs} ms",
            $"- Tempi: router {routerMs} ms, strumenti {toolsMs} ms, scrittura {writerMs} ms, totale {totalMs} ms");

    public static string DiagnosticPerformanceWorkspace(int categoryCount, int documentCount, bool hasCapabilities, string? language)
        => Pick(language,
            $"- Workspace : {categoryCount} categorie(s) canoniques, {documentCount} document(s) connu(s), snapshot capabilities {BoolWord(hasCapabilities, language)}",
            $"- Workspace: {categoryCount} canonical category(ies), {documentCount} known document(s), capabilities snapshot {BoolWord(hasCapabilities, language)}",
            $"- Workspace: {categoryCount} categoria(s) canonica(s), {documentCount} documento(s) conocido(s), snapshot de capacidades {BoolWord(hasCapabilities, language)}",
            $"- Workspace: {categoryCount} categoria(s) canonica(s), {documentCount} documento(s) conhecido(s), snapshot de capacidades {BoolWord(hasCapabilities, language)}",
            $"- Workspace: {categoryCount} kanonische Kategorie(n), {documentCount} bekannte Dokument(e), Capabilities-Snapshot {BoolWord(hasCapabilities, language)}",
            $"- Workspace: {categoryCount} categoria/e canonica/che, {documentCount} documento/i noto/i, snapshot capabilities {BoolWord(hasCapabilities, language)}");

    public static string DiagnosticPerformanceSession(bool hasFocusedDocument, int listedDocumentsCount, bool hasResolvedCategory, bool hasPendingClarification, string? language)
        => Pick(language,
            $"- Session : document focal {BoolWord(hasFocusedDocument, language)}, listee {listedDocumentsCount} doc(s), categorie resolue {BoolWord(hasResolvedCategory, language)}, clarification en attente {BoolWord(hasPendingClarification, language)}",
            $"- Session: focused document {BoolWord(hasFocusedDocument, language)}, listed {listedDocumentsCount} doc(s), resolved category {BoolWord(hasResolvedCategory, language)}, pending clarification {BoolWord(hasPendingClarification, language)}",
            $"- Sesion: documento focal {BoolWord(hasFocusedDocument, language)}, {listedDocumentsCount} doc(s) listados, categoria resuelta {BoolWord(hasResolvedCategory, language)}, aclaracion pendiente {BoolWord(hasPendingClarification, language)}",
            $"- Sessao: documento focal {BoolWord(hasFocusedDocument, language)}, {listedDocumentsCount} doc(s) listados, categoria resolvida {BoolWord(hasResolvedCategory, language)}, esclarecimento pendente {BoolWord(hasPendingClarification, language)}",
            $"- Sitzung: fokussiertes Dokument {BoolWord(hasFocusedDocument, language)}, {listedDocumentsCount} gelistete Dok., aufgeloeste Kategorie {BoolWord(hasResolvedCategory, language)}, offene Klaerung {BoolWord(hasPendingClarification, language)}",
            $"- Sessione: documento focale {BoolWord(hasFocusedDocument, language)}, {listedDocumentsCount} doc elencati, categoria risolta {BoolWord(hasResolvedCategory, language)}, chiarimento in attesa {BoolWord(hasPendingClarification, language)}");

    public static string DiagnosticPerformanceExecution(string? mode, bool hasRouterIntent, int toolNamesCount, bool hasAdminOperation, string? language)
        => Pick(language,
            $"- Execution : mode {(string.IsNullOrWhiteSpace(mode) ? "auto" : mode)}, intent routeur {BoolWord(hasRouterIntent, language)}, {toolNamesCount} outil(s), operation admin {BoolWord(hasAdminOperation, language)}",
            $"- Execution: mode {(string.IsNullOrWhiteSpace(mode) ? "auto" : mode)}, router intent {BoolWord(hasRouterIntent, language)}, {toolNamesCount} tool(s), admin operation {BoolWord(hasAdminOperation, language)}",
            $"- Ejecucion: modo {(string.IsNullOrWhiteSpace(mode) ? "auto" : mode)}, intent del router {BoolWord(hasRouterIntent, language)}, {toolNamesCount} herramienta(s), operacion admin {BoolWord(hasAdminOperation, language)}",
            $"- Execucao: modo {(string.IsNullOrWhiteSpace(mode) ? "auto" : mode)}, intent do roteador {BoolWord(hasRouterIntent, language)}, {toolNamesCount} ferramenta(s), operacao admin {BoolWord(hasAdminOperation, language)}",
            $"- Ausfuehrung: Modus {(string.IsNullOrWhiteSpace(mode) ? "auto" : mode)}, Router-Intent {BoolWord(hasRouterIntent, language)}, {toolNamesCount} Werkzeug(e), Admin-Operation {BoolWord(hasAdminOperation, language)}",
            $"- Esecuzione: modalita {(string.IsNullOrWhiteSpace(mode) ? "auto" : mode)}, intento router {BoolWord(hasRouterIntent, language)}, {toolNamesCount} strumento/i, operazione admin {BoolWord(hasAdminOperation, language)}");

    public static string DiagnosticPerformancePersistence(bool languagePersisted, bool stylePersisted, bool modePersisted, bool focusedDocumentPersisted, bool resolvedCategoryPersisted, string? language)
        => Pick(language,
            $"- Persistance : langue {BoolWord(languagePersisted, language)}, style {BoolWord(stylePersisted, language)}, mode {BoolWord(modePersisted, language)}, document focal {BoolWord(focusedDocumentPersisted, language)}, categorie resolue {BoolWord(resolvedCategoryPersisted, language)}",
            $"- Persistence: language {BoolWord(languagePersisted, language)}, style {BoolWord(stylePersisted, language)}, mode {BoolWord(modePersisted, language)}, focused document {BoolWord(focusedDocumentPersisted, language)}, resolved category {BoolWord(resolvedCategoryPersisted, language)}",
            $"- Persistencia: idioma {BoolWord(languagePersisted, language)}, estilo {BoolWord(stylePersisted, language)}, modo {BoolWord(modePersisted, language)}, documento focal {BoolWord(focusedDocumentPersisted, language)}, categoria resuelta {BoolWord(resolvedCategoryPersisted, language)}",
            $"- Persistencia: idioma {BoolWord(languagePersisted, language)}, estilo {BoolWord(stylePersisted, language)}, modo {BoolWord(modePersisted, language)}, documento focal {BoolWord(focusedDocumentPersisted, language)}, categoria resolvida {BoolWord(resolvedCategoryPersisted, language)}",
            $"- Persistenz: Sprache {BoolWord(languagePersisted, language)}, Stil {BoolWord(stylePersisted, language)}, Modus {BoolWord(modePersisted, language)}, fokussiertes Dokument {BoolWord(focusedDocumentPersisted, language)}, aufgeloeste Kategorie {BoolWord(resolvedCategoryPersisted, language)}",
            $"- Persistenza: lingua {BoolWord(languagePersisted, language)}, stile {BoolWord(stylePersisted, language)}, modalita {BoolWord(modePersisted, language)}, documento focale {BoolWord(focusedDocumentPersisted, language)}, categoria risolta {BoolWord(resolvedCategoryPersisted, language)}");

    public static string DiagnosticPerformanceReset(bool preservesM1Lite, bool preservesPreferences, bool clearsM3, bool clearsM6, bool resetsModeToAuto, string? language)
        => Pick(language,
            $"- Reset : preserve M1-lite {BoolWord(preservesM1Lite, language)}, preferences {BoolWord(preservesPreferences, language)}, vide M3 {BoolWord(clearsM3, language)}, vide M6 {BoolWord(clearsM6, language)}, remet le mode sur auto {BoolWord(resetsModeToAuto, language)}",
            $"- Reset: preserves M1-lite {BoolWord(preservesM1Lite, language)}, preferences {BoolWord(preservesPreferences, language)}, clears M3 {BoolWord(clearsM3, language)}, clears M6 {BoolWord(clearsM6, language)}, resets mode to auto {BoolWord(resetsModeToAuto, language)}",
            $"- Reset: preserva M1-lite {BoolWord(preservesM1Lite, language)}, preferencias {BoolWord(preservesPreferences, language)}, limpia M3 {BoolWord(clearsM3, language)}, limpia M6 {BoolWord(clearsM6, language)}, reinicia el modo a auto {BoolWord(resetsModeToAuto, language)}",
            $"- Reset: preserva M1-lite {BoolWord(preservesM1Lite, language)}, preferencias {BoolWord(preservesPreferences, language)}, limpa M3 {BoolWord(clearsM3, language)}, limpa M6 {BoolWord(clearsM6, language)}, volta o modo para auto {BoolWord(resetsModeToAuto, language)}",
            $"- Reset: behaelt M1-lite {BoolWord(preservesM1Lite, language)}, Praeferenzen {BoolWord(preservesPreferences, language)}, leert M3 {BoolWord(clearsM3, language)}, leert M6 {BoolWord(clearsM6, language)}, setzt Modus auf auto {BoolWord(resetsModeToAuto, language)}",
            $"- Reset: preserva M1-lite {BoolWord(preservesM1Lite, language)}, preferenze {BoolWord(preservesPreferences, language)}, pulisce M3 {BoolWord(clearsM3, language)}, pulisce M6 {BoolWord(clearsM6, language)}, riporta la modalita ad auto {BoolWord(resetsModeToAuto, language)}");

    public static string ExtractionQualityHeader(string? language)
        => Pick(language,
            "Diagnostic qualite d'extraction :",
            "Extraction quality diagnostics:",
            "Diagnostico de calidad de extraccion:",
            "Diagnostico de qualidade de extracao:",
            "Diagnose der Extraktionsqualitaet:",
            "Diagnostica qualita di estrazione:");

    public static string ExtractionPagesHeader(string docLabel, string? language)
    {
        docLabel = string.IsNullOrWhiteSpace(docLabel) ? Pick(language, "document", "document", "documento", "documento", "Dokument", "documento") : docLabel.Trim();
        return Pick(language,
            $"Diagnostic des pages pour {docLabel} :",
            $"Page diagnostics for {docLabel}:",
            $"Diagnostico de paginas para {docLabel}:",
            $"Diagnostico de paginas para {docLabel}:",
            $"Seitendiagnose fuer {docLabel}:",
            $"Diagnostica pagine per {docLabel}:");
    }

    public static string ExtractionScope(string scopePath, string? language)
        => Pick(language,
            $"- Perimetre : {scopePath}",
            $"- Scope: {scopePath}",
            $"- Ambito: {scopePath}",
            $"- Escopo: {scopePath}",
            $"- Bereich: {scopePath}",
            $"- Ambito: {scopePath}");

    public static string ExtractionQualitySummary(
        int total,
        int ok,
        int lowText,
        int emptyText,
        int unknown,
        int ocrRecommended,
        int ocrApplied,
        int manualReview,
        int warningPages,
        string? language)
        => Pick(language,
            $"- Synthese : {total} document(s), {ok} OK, {lowText} texte faible, {emptyText} vide(s), {unknown} inconnu(s), OCR conseille {ocrRecommended}, OCR applique {ocrApplied}, revue manuelle {manualReview}, pages avec avertissements {warningPages}.",
            $"- Summary: {total} document(s), {ok} OK, {lowText} low text, {emptyText} empty, {unknown} unknown, OCR recommended {ocrRecommended}, OCR applied {ocrApplied}, manual review {manualReview}, warning pages {warningPages}.",
            $"- Sintesis: {total} documento(s), {ok} OK, {lowText} texto bajo, {emptyText} vacio(s), {unknown} desconocido(s), OCR recomendado {ocrRecommended}, OCR aplicado {ocrApplied}, revision manual {manualReview}, paginas con avisos {warningPages}.",
            $"- Sintese: {total} documento(s), {ok} OK, {lowText} texto fraco, {emptyText} vazio(s), {unknown} desconhecido(s), OCR recomendado {ocrRecommended}, OCR aplicado {ocrApplied}, revisao manual {manualReview}, paginas com avisos {warningPages}.",
            $"- Zusammenfassung: {total} Dokument(e), {ok} OK, {lowText} wenig Text, {emptyText} leer, {unknown} unbekannt, OCR empfohlen {ocrRecommended}, OCR angewendet {ocrApplied}, manuelle Pruefung {manualReview}, Warnseiten {warningPages}.",
            $"- Sintesi: {total} documento/i, {ok} OK, {lowText} testo scarso, {emptyText} vuoto/i, {unknown} sconosciuto/i, OCR consigliato {ocrRecommended}, OCR applicato {ocrApplied}, revisione manuale {manualReview}, pagine con avvisi {warningPages}.");

    public static string ExtractionPagesSummary(int pages, int reviewPages, int noisePages, int emptyPages, int lowTextPages, int imagePages, string? language)
        => Pick(language,
            $"- Synthese : {pages} page(s), revue manuelle {reviewPages}, bruit OCR probable {noisePages}, vides {emptyPages}, texte faible {lowTextPages}, images {imagePages}.",
            $"- Summary: {pages} page(s), manual review {reviewPages}, probable OCR noise {noisePages}, empty {emptyPages}, low text {lowTextPages}, images {imagePages}.",
            $"- Sintesis: {pages} pagina(s), revision manual {reviewPages}, ruido OCR probable {noisePages}, vacias {emptyPages}, texto bajo {lowTextPages}, imagenes {imagePages}.",
            $"- Sintese: {pages} pagina(s), revisao manual {reviewPages}, ruido OCR provavel {noisePages}, vazias {emptyPages}, texto fraco {lowTextPages}, imagens {imagePages}.",
            $"- Zusammenfassung: {pages} Seite(n), manuelle Pruefung {reviewPages}, wahrscheinliches OCR-Rauschen {noisePages}, leer {emptyPages}, wenig Text {lowTextPages}, Bilder {imagePages}.",
            $"- Sintesi: {pages} pagina/e, revisione manuale {reviewPages}, probabile rumore OCR {noisePages}, vuote {emptyPages}, testo scarso {lowTextPages}, immagini {imagePages}.");

    public static string ExtractionNoDocuments(string? language)
        => Pick(language, "- Aucun document diagnosticable.", "- No diagnosable document.", "- Ningun documento diagnosticable.", "- Nenhum documento diagnosticavel.", "- Kein diagnostizierbares Dokument.", "- Nessun documento diagnosticabile.");

    public static string ExtractionNoPages(string? language)
        => Pick(language, "- Aucune page diagnosticable.", "- No diagnosable page.", "- Ninguna pagina diagnosticable.", "- Nenhuma pagina diagnosticavel.", "- Keine diagnostizierbare Seite.", "- Nessuna pagina diagnosticabile.");

    public static string ExtractionConfidenceLabel(string? language)
        => Pick(language, "confiance", "confidence", "confianza", "confianca", "Konfidenz", "confidenza");

    public static string ExtractionSourceLabel(string? language)
        => Pick(language, "source", "source", "fuente", "fonte", "Quelle", "fonte");

    public static string ExtractionDocumentStatusLabel(string? language)
        => Pick(language, "document", "document", "documento", "documento", "Dokument", "documento");

    public static string ExtractionProcessingRunStatusLabel(string? language)
        => Pick(language, "traitement", "processing", "procesamiento", "processamento", "Verarbeitung", "elaborazione");

    public static string ExtractionDocumentStatus(string? status, string? language)
    {
        var label = ExtractionOperationalStatusLabel(status, language);
        return string.IsNullOrWhiteSpace(label)
            ? string.Empty
            : $"{ExtractionDocumentStatusLabel(language)} {label}";
    }

    public static string ExtractionProcessingRunStatus(string? status, string? language)
    {
        var label = ExtractionOperationalStatusLabel(status, language);
        return string.IsNullOrWhiteSpace(label)
            ? string.Empty
            : $"{ExtractionProcessingRunStatusLabel(language)} {label}";
    }

    public static string ExtractionTextStatusLabel(string? language)
        => Pick(language, "texte", "text", "texto", "texto", "Text", "testo");

    public static string ExtractionImageOcrStatusLabel(string? language)
        => Pick(language, "OCR image", "image OCR", "OCR imagen", "OCR imagem", "Bild-OCR", "OCR immagine");

    public static string ExtractionTextPages(int textPages, int pageCount, string? language)
        => Pick(language,
            $"pages texte {textPages}/{pageCount}",
            $"text pages {textPages}/{pageCount}",
            $"paginas con texto {textPages}/{pageCount}",
            $"paginas com texto {textPages}/{pageCount}",
            $"Textseiten {textPages}/{pageCount}",
            $"pagine testo {textPages}/{pageCount}");

    public static string ExtractionOcrApplied(string? language)
        => Pick(language, "OCR applique", "OCR applied", "OCR aplicado", "OCR aplicado", "OCR angewendet", "OCR applicato");

    public static string ExtractionOcrRecommended(string? language)
        => Pick(language, "OCR conseille", "OCR recommended", "OCR recomendado", "OCR recomendado", "OCR empfohlen", "OCR consigliato");

    public static string ExtractionOcrCandidate(string? language)
        => Pick(language, "candidat OCR", "OCR candidate", "candidato OCR", "candidato OCR", "OCR-Kandidat", "candidato OCR");

    public static string ExtractionOcrTimedOut(string? language)
        => Pick(language, "OCR timeout", "OCR timeout", "timeout OCR", "timeout OCR", "OCR-Timeout", "timeout OCR");

    public static string ExtractionReviewRecommended(string? language)
        => Pick(language, "revue manuelle conseillee", "manual review recommended", "revision manual recomendada", "revisao manual recomendada", "manuelle Pruefung empfohlen", "revisione manuale consigliata");

    public static string ExtractionRetrievalChunkSummary(int rejectedDocuments, int noSearchableDocuments, int reviewDocuments, string? language)
        => Pick(language,
            $"- Index RAG : {rejectedDocuments} document(s) avec passages rejetes, {noSearchableDocuments} sans passage exploitable, {reviewDocuments} a revoir pour l'index.",
            $"- RAG index: {rejectedDocuments} document(s) with rejected passages, {noSearchableDocuments} with no usable passage, {reviewDocuments} to review for indexing.",
            $"- Indice RAG: {rejectedDocuments} documento(s) con pasajes rechazados, {noSearchableDocuments} sin pasaje utilizable, {reviewDocuments} a revisar para el indice.",
            $"- Indice RAG: {rejectedDocuments} documento(s) com trechos rejeitados, {noSearchableDocuments} sem trecho utilizavel, {reviewDocuments} a rever para o indice.",
            $"- RAG-Index: {rejectedDocuments} Dokument(e) mit verworfenen Passagen, {noSearchableDocuments} ohne nutzbare Passage, {reviewDocuments} fuer den Index zu pruefen.",
            $"- Indice RAG: {rejectedDocuments} documento/i con passaggi scartati, {noSearchableDocuments} senza passaggio utilizzabile, {reviewDocuments} da rivedere per l'indice.");

    public static string ExtractionRetrievalChunkQuality(
        int? total,
        int? searchable,
        int? rejected,
        bool reviewRecommended,
        string? reasons,
        string? language)
    {
        var totalText = total.HasValue ? total.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";
        var searchableText = searchable.HasValue ? searchable.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";
        var rejectedText = rejected.HasValue ? rejected.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";
        var baseText = Pick(language,
            $"passages RAG {searchableText}/{totalText} exploitables, rejetes {rejectedText}",
            $"RAG passages {searchableText}/{totalText} usable, rejected {rejectedText}",
            $"pasajes RAG {searchableText}/{totalText} utilizables, rechazados {rejectedText}",
            $"trechos RAG {searchableText}/{totalText} utilizaveis, rejeitados {rejectedText}",
            $"RAG-Passagen {searchableText}/{totalText} nutzbar, verworfen {rejectedText}",
            $"passaggi RAG {searchableText}/{totalText} utilizzabili, scartati {rejectedText}");
        if (reviewRecommended)
        {
            baseText += Pick(language,
                ", revue index conseillee",
                ", index review recommended",
                ", revision del indice recomendada",
                ", revisao do indice recomendada",
                ", Indexpruefung empfohlen",
                ", revisione indice consigliata");
        }

        return string.IsNullOrWhiteSpace(reasons)
            ? baseText
            : baseText + Pick(language, $", raisons {reasons}", $", reasons {reasons}", $", razones {reasons}", $", razoes {reasons}", $", Gruende {reasons}", $", motivi {reasons}");
    }

    public static string ExtractionCategoriesHeader(string? language)
        => Pick(language, "- Categories a surveiller :", "- Categories to watch:", "- Categorias a vigilar:", "- Categorias a observar:", "- Zu beobachtende Kategorien:", "- Categorie da monitorare:");

    public static string ExtractionCategorySummary(
        string categoryPath,
        int total,
        int lowText,
        int emptyText,
        int ocrRecommended,
        int manualReview,
        int warningPages,
        string? language)
        => Pick(language,
            $"{categoryPath}: {total} doc(s), texte faible {lowText}, vide {emptyText}, OCR conseille {ocrRecommended}, revue {manualReview}, avertissements pages {warningPages}",
            $"{categoryPath}: {total} doc(s), low text {lowText}, empty {emptyText}, OCR recommended {ocrRecommended}, review {manualReview}, warning pages {warningPages}",
            $"{categoryPath}: {total} doc(s), texto bajo {lowText}, vacio {emptyText}, OCR recomendado {ocrRecommended}, revision {manualReview}, paginas con avisos {warningPages}",
            $"{categoryPath}: {total} doc(s), texto fraco {lowText}, vazio {emptyText}, OCR recomendado {ocrRecommended}, revisao {manualReview}, paginas com avisos {warningPages}",
            $"{categoryPath}: {total} Dok., wenig Text {lowText}, leer {emptyText}, OCR empfohlen {ocrRecommended}, Pruefung {manualReview}, Warnseiten {warningPages}",
            $"{categoryPath}: {total} doc, testo scarso {lowText}, vuoto {emptyText}, OCR consigliato {ocrRecommended}, revisione {manualReview}, pagine con avvisi {warningPages}");

    public static string ExtractionSignalsLabel(string? language)
        => Pick(language, "signaux", "signals", "senales", "sinais", "Signale", "segnali");

    public static string ExtractionNativeTextLabel(string? language)
        => Pick(language, "texte natif", "native text", "texto nativo", "texto nativo", "nativer Text", "testo nativo");

    public static string ExtractionOcrLanguagesLabel(string? language)
        => Pick(language, "langues OCR", "OCR languages", "idiomas OCR", "idiomas OCR", "OCR-Sprachen", "lingue OCR");

    public static string ExtractionOcrDurationLabel(string? language)
        => Pick(language, "duree OCR", "OCR duration", "duracion OCR", "duracao OCR", "OCR-Dauer", "durata OCR");

    public static string ExtractionOcrReasonLabel(string? language)
        => Pick(language, "raison OCR", "OCR reason", "razon OCR", "razao OCR", "OCR-Grund", "ragione OCR");

    public static string ExtractionOcrModeLabel(string? language)
        => Pick(language, "mode OCR", "OCR mode", "modo OCR", "modo OCR", "OCR-Modus", "modalita OCR");

    public static string ExtractionOcrMode(string? mode, string? language)
    {
        var label = ExtractionStatusLabel(mode, language);
        if (string.IsNullOrWhiteSpace(label))
            return string.Empty;

        return $"{ExtractionOcrModeLabel(language)} {label}";
    }

    public static string ExtractionOcrExitCodeLabel(string? language)
        => Pick(language, "code OCR", "OCR code", "codigo OCR", "codigo OCR", "OCR-Code", "codice OCR");

    public static string ExtractionPreviewLabel(string? language)
        => Pick(language, "apercu", "preview", "vista previa", "pre-visualizacao", "Vorschau", "anteprima");

    public static string ExtractionWordStats(int totalWords, double? averageWordsPerPage, double? textPageRatio, string? language)
    {
        var parts = new List<string> { Pick(language, $"{totalWords} mots", $"{totalWords} words", $"{totalWords} palabras", $"{totalWords} palavras", $"{totalWords} Woerter", $"{totalWords} parole") };
        if (averageWordsPerPage.HasValue)
            parts.Add(Pick(language,
                $"{averageWordsPerPage.Value:0.#}/page",
                $"{averageWordsPerPage.Value:0.#}/page",
                $"{averageWordsPerPage.Value:0.#}/pagina",
                $"{averageWordsPerPage.Value:0.#}/pagina",
                $"{averageWordsPerPage.Value:0.#}/Seite",
                $"{averageWordsPerPage.Value:0.#}/pagina"));
        if (textPageRatio.HasValue)
            parts.Add(Pick(language,
                $"pages texte {textPageRatio.Value:0%}",
                $"text pages {textPageRatio.Value:0%}",
                $"paginas texto {textPageRatio.Value:0%}",
                $"paginas texto {textPageRatio.Value:0%}",
                $"Textseiten {textPageRatio.Value:0%}",
                $"pagine testo {textPageRatio.Value:0%}"));

        return string.Join(", ", parts);
    }

    public static string ExtractionPageIssues(int warningPages, int reviewPages, string? language)
        => Pick(language,
            $"pages a verifier {reviewPages}, avertissements {warningPages}",
            $"review pages {reviewPages}, warnings {warningPages}",
            $"paginas a revisar {reviewPages}, avisos {warningPages}",
            $"paginas a rever {reviewPages}, avisos {warningPages}",
            $"Pruefseiten {reviewPages}, Warnungen {warningPages}",
            $"pagine da rivedere {reviewPages}, avvisi {warningPages}");

    public static string ExtractionPageCounters(int words, int chars, int images, int chunks, string? language)
        => Pick(language,
            $"{words} mots, {chars} caracteres, {images} image(s), {chunks} chunk(s)",
            $"{words} words, {chars} chars, {images} image(s), {chunks} chunk(s)",
            $"{words} palabras, {chars} caracteres, {images} imagen(es), {chunks} chunk(s)",
            $"{words} palavras, {chars} caracteres, {images} imagem(ns), {chunks} chunk(s)",
            $"{words} Woerter, {chars} Zeichen, {images} Bild(er), {chunks} Chunk(s)",
            $"{words} parole, {chars} caratteri, {images} immagine/i, {chunks} chunk(s)");

    public static string ExtractionStatusLabel(string? code, string? language)
    {
        var normalized = (code ?? string.Empty).Trim();
        if (normalized.Length == 0)
            return string.Empty;

        return normalized.ToLowerInvariant() switch
        {
            "ok" or "page_ok" or "text_ok" => "OK",
            "low_text" or "page_low_text" => Pick(language, "texte faible", "low text", "texto bajo", "texto fraco", "wenig Text", "testo scarso"),
            "empty_text" or "page_empty_text" => Pick(language, "texte vide", "empty text", "texto vacio", "texto vazio", "leerer Text", "testo vuoto"),
            "unknown" => Pick(language, "inconnu", "unknown", "desconocido", "desconhecido", "unbekannt", "sconosciuto"),
            "ocr_recommended" => ExtractionOcrRecommended(language),
            "ocr_applied_ok" => ExtractionOcrApplied(language),
            "image_ocr_applied_ok" => Pick(language, "OCR image applique", "image OCR applied", "OCR de imagen aplicado", "OCR de imagem aplicado", "Bild-OCR angewendet", "OCR immagine applicato"),
            "ocr_applied_ok_with_page_warnings" => Pick(language, "OCR applique avec avertissements", "OCR applied with warnings", "OCR aplicado con avisos", "OCR aplicado com avisos", "OCR mit Warnungen angewendet", "OCR applicato con avvisi"),
            "ocr_applied_low_confidence" => Pick(language, "OCR applique avec confiance faible", "OCR applied with low confidence", "OCR aplicado con baja confianza", "OCR aplicado com baixa confianca", "OCR mit niedriger Konfidenz angewendet", "OCR applicato con bassa confidenza"),
            "manual_review_empty_text" => Pick(language, "revision manuelle texte vide", "manual review empty text", "revision manual texto vacio", "revisao manual texto vazio", "manuelle Pruefung leerer Text", "revisione manuale testo vuoto"),
            "manual_review_low_text" => Pick(language, "revision manuelle texte faible", "manual review low text", "revision manual texto bajo", "revisao manual texto fraco", "manuelle Pruefung wenig Text", "revisione manuale testo scarso"),
            "extraction_ok_with_page_warnings" => Pick(language, "extraction OK avec avertissements", "extraction OK with warnings", "extraccion OK con avisos", "extracao OK com avisos", "Extraktion OK mit Warnungen", "estrazione OK con avvisi"),
            "text_extraction_ok_with_images" => Pick(language, "extraction texte OK avec images", "text extraction OK with images", "extraccion de texto OK con imagenes", "extracao de texto OK com imagens", "Textextraktion OK mit Bildern", "estrazione testo OK con immagini"),
            "extraction_ok" => Pick(language, "extraction OK", "extraction OK", "extraccion OK", "extracao OK", "Extraktion OK", "estrazione OK"),
            "page_ok_with_images" => Pick(language, "OK avec images", "OK with images", "OK con imagenes", "OK com imagens", "OK mit Bildern", "OK con immagini"),
            "page_ok_indexed_by_context" => Pick(language, "OK par contexte", "OK by context", "OK por contexto", "OK por contexto", "OK durch Kontext", "OK da contesto"),
            "ocr_failed_or_insufficient" => Pick(language, "OCR echec ou insuffisant", "OCR failed or insufficient", "OCR fallido o insuficiente", "OCR falhou ou insuficiente", "OCR fehlgeschlagen oder unzureichend", "OCR fallito o insufficiente"),
            "ocr_required_but_disabled" => Pick(language, "OCR requis mais desactive", "OCR required but disabled", "OCR requerido pero desactivado", "OCR necessario mas desativado", "OCR erforderlich aber deaktiviert", "OCR richiesto ma disattivato"),
            "ocr_disabled" => Pick(language, "OCR desactive", "OCR disabled", "OCR desactivado", "OCR desativado", "OCR deaktiviert", "OCR disattivato"),
            "ocr_extraction_failed" => Pick(language, "extraction OCR echouee", "OCR extraction failed", "extraccion OCR fallida", "extracao OCR falhou", "OCR-Extraktion fehlgeschlagen", "estrazione OCR fallita"),
            "ocr_failed" => Pick(language, "OCR echoue", "OCR failed", "OCR fallido", "OCR falhou", "OCR fehlgeschlagen", "OCR fallito"),
            "exit_code_non_zero" => Pick(language, "code de sortie non nul", "non-zero exit code", "codigo de salida no cero", "codigo de saida nao zero", "Exit-Code ungleich null", "codice di uscita non zero"),
            "ocr_output_missing" => Pick(language, "sortie OCR manquante", "OCR output missing", "salida OCR ausente", "saida OCR ausente", "OCR-Ausgabe fehlt", "output OCR mancante"),
            "no_novel_text" => Pick(language, "aucun texte OCR nouveau", "no new OCR text", "sin texto OCR nuevo", "sem texto OCR novo", "kein neuer OCR-Text", "nessun nuovo testo OCR"),
            "scanned_pdf_not_indexable" => Pick(language, "PDF scanne non indexable", "scanned PDF not indexable", "PDF escaneado no indexable", "PDF digitalizado nao indexavel", "gescanntes PDF nicht indexierbar", "PDF scansionato non indicizzabile"),
            "no_indexable_text" => Pick(language, "aucun texte indexable", "no indexable text", "sin texto indexable", "sem texto indexavel", "kein indexierbarer Text", "nessun testo indicizzabile"),
            "document_not_indexable" => Pick(language, "document non indexable", "document not indexable", "documento no indexable", "documento nao indexavel", "Dokument nicht indexierbar", "documento non indicizzabile"),
            _ => normalized
        };
    }

    private static string ExtractionOperationalStatusLabel(string? status, string? language)
    {
        var normalized = (status ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return string.Empty;

        return normalized switch
        {
            "indexed" => Pick(language, "indexe", "indexed", "indexado", "indexado", "indexiert", "indicizzato"),
            "error" => Pick(language, "en erreur", "error", "error", "erro", "Fehler", "errore"),
            "done" => Pick(language, "termine", "done", "terminado", "concluido", "fertig", "completato"),
            "failed" => Pick(language, "echoue", "failed", "fallido", "falhou", "fehlgeschlagen", "fallito"),
            "running" => Pick(language, "en cours", "running", "en curso", "em curso", "laufend", "in corso"),
            "queued" => Pick(language, "en attente", "queued", "en cola", "na fila", "wartend", "in coda"),
            "paused" => Pick(language, "en pause", "paused", "pausado", "pausado", "pausiert", "in pausa"),
            "canceled" or "cancelled" => Pick(language, "annule", "canceled", "cancelado", "cancelado", "abgebrochen", "annullato"),
            _ => normalized
        };
    }

    public static string ExtractionDocumentNotIndexable(string? language)
        => Pick(language, "document non indexable", "document not indexable", "documento no indexable", "documento nao indexavel", "Dokument nicht indexierbar", "documento non indicizzabile");

    public static string ExtractionFailureReason(string? reason, string? language)
    {
        var label = ExtractionStatusLabel(reason, language);
        if (string.IsNullOrWhiteSpace(label))
            return string.Empty;

        return Pick(language, $"raison {label}", $"reason {label}", $"razon {label}", $"razao {label}", $"Grund {label}", $"ragione {label}");
    }

    public static string ExtractionOcrFailureReason(string? reason, string? language)
    {
        var label = ExtractionStatusLabel(reason, language);
        if (string.IsNullOrWhiteSpace(label))
            return string.Empty;

        return Pick(language, $"echec OCR {label}", $"OCR failure {label}", $"fallo OCR {label}", $"falha OCR {label}", $"OCR-Fehler {label}", $"errore OCR {label}");
    }

    public static string ExtractionOcrAppliedReason(string? reason, string? language)
    {
        var label = ExtractionStatusLabel(reason, language);
        if (string.IsNullOrWhiteSpace(label))
            return string.Empty;

        return Pick(language, $"decision OCR {label}", $"OCR decision {label}", $"decision OCR {label}", $"decisao OCR {label}", $"OCR-Entscheid {label}", $"decisione OCR {label}");
    }

    public static string MissingSummariesHeader(string? language)
        => Pick(language,
            "Voici la liste des documents sans résumé stocké :",
            "Here is the list of documents without a stored summary:",
            "Aquí está la lista de documentos sin resumen almacenado:",
            "Aqui está a lista de documentos sem resumo armazenado:",
            "Hier ist die Liste der Dokumente ohne gespeicherte Zusammenfassung:",
            "Ecco l'elenco dei documenti senza riassunto salvato:");

    public static string MissingSummariesCount(int total, string? language)
        => Pick(language,
            $"Il y a actuellement {total} document(s) indexé(s) sans résumé stocké.",
            $"There are currently {total} indexed document(s) without a stored summary.",
            $"Actualmente hay {total} documento(s) indexado(s) sin resumen almacenado.",
            $"Atualmente há {total} documento(s) indexado(s) sem resumo armazenado.",
            $"Derzeit gibt es {total} indexierte Dokument(e) ohne gespeicherte Zusammenfassung.",
            $"Attualmente ci sono {total} documento/i indicizzato/i senza riassunto salvato.");

    public static string BackofficeProfilesMissingCount(int total, string? language)
        => Pick(language,
            $"Dont {total} document(s) avec un profil serveur manquant.",
            $"Including {total} document(s) with a missing server profile.",
            $"Incluye {total} documento(s) con un perfil de servidor faltante.",
            $"Inclui {total} documento(s) com perfil de servidor em falta.",
            $"Davon {total} Dokument(e) mit fehlendem Serverprofil.",
            $"Inclusi {total} documento/i con profilo server mancante.");

    public static string BackofficeProfileMissingSuffix(string? language)
        => Pick(language,
            "profil serveur manquant",
            "missing server profile",
            "perfil de servidor faltante",
            "perfil de servidor em falta",
            "Serverprofil fehlt",
            "profilo server mancante");

    public static string SummaryStatusActiveJobSuffix(string? status, string? language)
    {
        status = SummaryProcessingStatusLabel(status, language);
        return Pick(language,
            $"traitement en cours {status}",
            $"processing in progress {status}",
            $"proceso en curso {status}",
            $"processamento em curso {status}",
            $"Verarbeitung laeuft {status}",
            $"elaborazione in corso {status}");
    }

    public static string SummaryStatusCapabilityActionSuffix(string? action, string? language)
    {
        action = SummaryRecommendedActionLabel(action, language);
        return Pick(language,
            $"résumé serveur {action}",
            $"server summary {action}",
            $"resumen servidor {action}",
            $"resumo servidor {action}",
            $"Serverzusammenfassung {action}",
            $"riepilogo server {action}");
    }

    public static string SummaryStatusPolicyBlockedSuffix(string? reason, string? language)
    {
        reason = SummaryPolicyReasonLabel(reason, language);
        return Pick(language,
            $"bloqué par règle serveur{reason}",
            $"blocked by server rule{reason}",
            $"bloqueado por regla del servidor{reason}",
            $"bloqueado por regra do servidor{reason}",
            $"durch Serverregel blockiert{reason}",
            $"bloccato da regola server{reason}");
    }

    public static string SummaryStatusLastJobIssueSuffix(string? status, string? error, string? language)
    {
        var details = !string.IsNullOrWhiteSpace(error)
            ? Pick(language, " ; détail dans les logs", "; details in logs", "; detalle en logs", "; detalhe nos logs", "; Details in den Logs", "; dettagli nei log")
            : string.Empty;
        var statusLabel = SummaryProcessingStatusLabel(status, language);
        return Pick(language,
            $"dernier traitement en anomalie ({statusLabel}){details}",
            $"last processing issue ({statusLabel}){details}",
            $"anomalia del ultimo proceso ({statusLabel}){details}",
            $"anomalia no ultimo processamento ({statusLabel}){details}",
            $"letzte Verarbeitung auffaellig ({statusLabel}){details}",
            $"anomalia nell'ultima elaborazione ({statusLabel}){details}");
    }

    public static string SummaryStatusStaleSuffix(string? language)
        => Pick(language,
            "résumé à mettre à jour",
            "summary to update",
            "resumen por actualizar",
            "resumo a atualizar",
            "Zusammenfassung zu aktualisieren",
            "riepilogo da aggiornare");

    public static string SummaryStatusPrioritySuffix(double score, string? language)
    {
        var rounded = Math.Round(score, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Pick(language,
            $"priorite {rounded}",
            $"priority {rounded}",
            $"prioridad {rounded}",
            $"prioridade {rounded}",
            $"Prioritaet {rounded}",
            $"priorita {rounded}");
    }

    private static string SummaryProcessingStatusLabel(string? status, string? language)
    {
        var normalized = (status ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "queued" or "pending" => Pick(language, "en attente", "waiting", "en espera", "em espera", "wartet", "in attesa"),
            "running" or "in_progress" or "processing" => Pick(language, "en cours", "running", "en curso", "em curso", "laeuft", "in corso"),
            "completed" or "succeeded" or "done" => Pick(language, "terminé", "completed", "terminado", "terminado", "abgeschlossen", "terminato"),
            "failed" or "error" => Pick(language, "en erreur", "failed", "con error", "com erro", "fehlgeschlagen", "errore"),
            "canceled" or "cancelled" => Pick(language, "annulé", "canceled", "cancelado", "cancelado", "abgebrochen", "annullato"),
            _ => Pick(language, "à vérifier", "to review", "por revisar", "a rever", "zu pruefen", "da verificare")
        };
    }

    private static string SummaryRecommendedActionLabel(string? action, string? language)
    {
        var normalized = (action ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "" or "ready" or "enqueue" or "enqueue_profile_refresh" or "enqueue_summary" => Pick(language, "prêt à préparer", "ready to prepare", "listo para preparar", "pronto a preparar", "bereit zur Vorbereitung", "pronto da preparare"),
            "skip" or "wait" => Pick(language, "en attente", "waiting", "en espera", "em espera", "wartet", "in attesa"),
            "review" or "manual_review" => Pick(language, "à revoir", "to review", "por revisar", "a rever", "zu pruefen", "da verificare"),
            _ => Pick(language, "à préparer", "to prepare", "por preparar", "a preparar", "vorzubereiten", "da preparare")
        };
    }

    private static string SummaryPolicyReasonLabel(string? reason, string? language)
    {
        var normalized = (reason ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        var label = normalized switch
        {
            "active_job" or "job_active" => Pick(language, "traitement déjà en cours", "processing already running", "proceso ya en curso", "processamento ja em curso", "Verarbeitung laeuft bereits", "elaborazione gia in corso"),
            "cooldown" or "policy_cooldown" => Pick(language, "temporisation active", "temporary delay active", "espera temporal activa", "espera temporaria ativa", "Wartezeit aktiv", "attesa temporanea attiva"),
            "runtime_unqualified" or "runtime_degraded" => Pick(language, "serveur à revérifier", "server to recheck", "servidor por revisar", "servidor a rever", "Server erneut pruefen", "server da ricontrollare"),
            _ => Pick(language, "raison serveur à vérifier", "server reason to review", "razón del servidor por revisar", "razao do servidor a rever", "Servergrund zu pruefen", "ragione server da verificare")
        };

        return $": {label}";
    }

    public static string NoMissingSummaries(string? language)
        => Pick(language,
            "Tous les documents indexés disposent déjà d'un résumé stocké.",
            "All indexed documents already have a stored summary.",
            "Todos los documentos indexados ya tienen un resumen almacenado.",
            "Todos os documentos indexados já têm um resumo armazenado.",
            "Alle indexierten Dokumente verfügen bereits über eine gespeicherte Zusammenfassung.",
            "Tutti i documenti indicizzati dispongono già di un riassunto salvato.");


    public static string StoredSummariesHeader(string? language)
        => Pick(language,
            "Voici la liste des documents avec un résumé stocké :",
            "Here is the list of documents with a stored summary:",
            "Aquí está la lista de documentos con un resumen almacenado:",
            "Aqui está a lista de documentos com um resumo armazenado:",
            "Hier ist die Liste der Dokumente mit gespeicherter Zusammenfassung:",
            "Ecco l'elenco dei documenti con un riassunto salvato:");

    public static string StoredSummariesCount(int total, string? language)
        => Pick(language,
            $"Il y a actuellement {total} document(s) indexé(s) qui ont un résumé stocké.",
            $"There are currently {total} indexed document(s) with a stored summary.",
            $"Actualmente hay {total} documento(s) indexado(s) con un resumen almacenado.",
            $"Atualmente há {total} documento(s) indexado(s) com um resumo armazenado.",
            $"Derzeit gibt es {total} indexierte Dokument(e) mit gespeicherter Zusammenfassung.",
            $"Attualmente ci sono {total} documento/i indicizzato/i con un riassunto salvato.");

    public static string NoStoredSummaries(string? language)
        => Pick(language,
            "Aucun document indexé n'a actuellement de résumé stocké.",
            "No indexed document currently has a stored summary.",
            "Actualmente ningún documento indexado tiene un resumen almacenado.",
            "Atualmente nenhum documento indexado tem um resumo armazenado.",
            "Derzeit hat kein indexiertes Dokument eine gespeicherte Zusammenfassung.",
            "Attualmente nessun documento indicizzato ha un riassunto salvato.");

    public static string ToolFailureAdminRequired(string? language)
        => Pick(language,
            "Cette action nécessite une session admin active dans le client WinUI.",
            "This action requires an active admin session in the WinUI client.",
            "Esta acción requiere una sesión de administrador activa en el cliente WinUI.",
            "Esta ação requer uma sessão de administrador ativa no cliente WinUI.",
            "Diese Aktion erfordert eine aktive Admin-Sitzung im WinUI-Client.",
            "Questa azione richiede una sessione admin attiva nel client WinUI.");

    public static string ToolFailureAdminInvalidOrForbidden(string? language)
        => Pick(language,
            "La session admin WinUI est invalide ou n'est plus autorisée pour cette action.",
            "The WinUI admin session is invalid or is no longer allowed for this action.",
            "La sesión de administrador de WinUI es inválida o ya no está autorizada para esta acción.",
            "A sessão de administrador do WinUI é inválida ou não está mais autorizada para esta ação.",
            "Die WinUI-Admin-Sitzung ist ungültig oder für diese Aktion nicht mehr berechtigt.",
            "La sessione admin WinUI non è valida o non è più autorizzata per questa azione.");

    public static string ToolFailureUnknownPlan(string? language)
        => Pick(language,
            "Le plan d'outils interne était invalide pour cette demande. Relance la demande ou reformule-la.",
            "The internal tool plan was invalid for this request. Please retry or reformulate the request.",
            "El plan interno de herramientas no era válido para esta solicitud. Vuelve a intentarlo o reformula la solicitud.",
            "O plano interno de ferramentas era inválido para esta solicitação. Tente novamente ou reformule o pedido.",
            "Der interne Tool-Plan war für diese Anfrage ungültig. Bitte versuche es erneut oder formuliere die Anfrage um.",
            "Il piano interno degli strumenti non era valido per questa richiesta. Riprova o riformula la richiesta.");

    public static string ToolFailureToolFailed(string? language)
        => Pick(language,
            "Un ou plusieurs outils ont échoué avant de pouvoir produire une réponse fondée.",
            "One or more tools failed before a grounded answer could be produced.",
            "Una o varias herramientas fallaron antes de poder producir una respuesta fundamentada.",
            "Uma ou mais ferramentas falharam antes que fosse possível produzir uma resposta fundamentada.",
            "Ein oder mehrere Werkzeuge sind fehlgeschlagen, bevor eine fundierte Antwort erzeugt werden konnte.",
            "Uno o più strumenti hanno avuto un errore prima di poter produrre una risposta fondata.");

    public static string ToolFailureDocumentNotFound(string? language)
        => Pick(language,
            "Je n'ai pas trouvé le document demandé dans le catalogue indexé.",
            "I could not find the requested document in the indexed catalog.",
            "No he encontrado el documento solicitado en el catálogo indexado.",
            "Não encontrei o documento solicitado no catálogo indexado.",
            "Ich konnte das angeforderte Dokument im indizierten Katalog nicht finden.",
            "Non ho trovato il documento richiesto nel catalogo indicizzato.");

    public static string ToolFailureSourceNotFound(string? language)
        => Pick(language,
            "Je n'ai pas trouvé la source demandée parmi les documents déjà résolus dans cette conversation.",
            "I could not find the requested source among the documents already resolved in this conversation.",
            "No he encontrado la fuente solicitada entre los documentos ya resueltos en esta conversación.",
            "Não encontrei a fonte solicitada entre os documentos já resolvidos nesta conversa.",
            "Ich konnte die angeforderte Quelle nicht unter den in dieser Unterhaltung bereits aufgelösten Dokumenten finden.",
            "Non ho trovato la fonte richiesta tra i documenti già risolti in questa conversazione.");

    public static string ToolAction(string toolName, string? language)
        => toolName switch
        {
            "documents.tree" => Pick(language, "Je vais chercher l'arborescence des documents…", "I am retrieving the document tree…", "Estoy recuperando el árbol de documentos…", "Estou recuperando a árvore de documentos…", "Ich rufe den Dokumentbaum ab…", "Sto recuperando l'albero dei documenti…"),
            "documents.count" => Pick(language, "Je compte les documents indexés…", "I am counting indexed documents…", "Estoy contando los documentos indexados…", "Estou contando os documentos indexados…", "Ich zähle die indexierten Dokumente…", "Sto contando i documenti indicizzati…"),
            "documents.categories" => Pick(language, "Je récupère les catégories du catalogue…", "I am retrieving the catalog categories…", "Estoy recuperando las categorías del catálogo…", "Estou recuperando as categorias do catálogo…", "Ich rufe die Katalogkategorien ab…", "Sto recuperando le categorie del catalogo…"),
            "documents.stats" => Pick(language, "Je rassemble les statistiques du catalogue…", "I am compiling catalog statistics…", "Estoy recopilando las estadísticas del catálogo…", "Estou reunindo as estatísticas do catálogo…", "Ich stelle die Katalogstatistiken zusammen…", "Sto raccogliendo le statistiche del catalogo…"),
            "documents.empty_count" => Pick(language, "Je compte les dossiers vides…", "I am counting empty folders…", "Estoy contando las carpetas vacías…", "Estou contando as pastas vazias…", "Ich zähle die leeren Ordner…", "Sto contando le cartelle vuote…"),
            "documents.empty_list" => Pick(language, "Je liste les dossiers vides…", "I am listing empty folders…", "Estoy enumerando las carpetas vacías…", "Estou listando as pastas vazias…", "Ich liste die leeren Ordner auf…", "Sto elencando le cartelle vuote…"),
            "documents.list" or "documents.search" => Pick(language, "Je cherche dans les documents indexés…", "I am searching the indexed documents…", "Estoy buscando en los documentos indexados…", "Estou pesquisando nos documentos indexados…", "Ich suche in den indexierten Dokumenten…", "Sto cercando nei documenti indicizzati…"),
            "rag.search" or "rag.multi_search" => Pick(language, "Je récupère les passages les plus pertinents…", "I am retrieving the most relevant passages…", "Estoy recuperando los pasajes más relevantes…", "Estou recuperando os trechos mais relevantes…", "Ich rufe die relevantesten Passagen ab…", "Sto recuperando i passaggi più pertinenti…"),
            "sources.resolve" => Pick(language, "Je résous la source demandée…", "I am resolving the requested source…", "Estoy resolviendo la fuente solicitada…", "Estou resolvendo a fonte solicitada…", "Ich löse die angeforderte Quelle auf…", "Sto risolvendo la fonte richiesta…"),
            "admin.summary.generate" => Pick(language, "Je prépare le cache de résumé réutilisable pour ce document…", "I am preparing the reusable summary cache for this document…", "Estoy preparando la caché de resumen reutilizable para este documento…", "Estou preparando o cache de resumo reutilizável para este documento…", "Ich bereite den Cache für wiederverwendbare Zusammenfassungen dieses Dokuments vor…", "Sto preparando la cache del riassunto riutilizzabile per questo documento…"),
            "admin.summary.submit" => Pick(language, "Je stocke le résumé réutilisable…", "I am storing the reusable summary…", "Estoy almacenando el resumen reutilizable…", "Estou armazenando o resumo reutilizável…", "Ich speichere die wiederverwendbare Zusammenfassung…", "Sto salvando il riassunto riutilizzabile…"),
            _ => ProgressCollectInformation(language)
        };

    public static string ToolProgress(string toolName, string? language)
        => ToolAction(toolName, language);

    public static string ToolPhase(string toolName, string? language)
        => toolName switch
        {
            "documents.list" or "documents.search" or "documents.get" or "documents.count" or "documents.categories" or "documents.tree" or "documents.stats" or "documents.empty_count" or "documents.empty_list"
                => Pick(language, "Recherche documents…", "Document search…", "Búsqueda de documentos…", "Pesquisa de documentos…", "Dokumentsuche…", "Ricerca documenti…"),
            "rag.search" or "rag.multi_search"
                => Pick(language, "Recherche documentaire…", "Document retrieval…", "Búsqueda documental…", "Pesquisa documental…", "Dokumentrecherche…", "Ricerca documentale…"),
            "sources.resolve"
                => Pick(language, "Ouverture de la source…", "Opening source…", "Abriendo la fuente…", "A abrir a fonte…", "Quelle wird geöffnet…", "Apertura fonte…"),
            "summary.get" or "summary.exists" or "summary.search"
                => Pick(language, "Chargement résumé…", "Loading summary…", "Cargando resumen…", "Carregando resumo…", "Zusammenfassung wird geladen…", "Caricamento riassunto…"),
            "admin.summary.missing" or "admin.summary.request" or "admin.summary.submit" or "admin.summary.status" or "admin.summary.delete" or "admin.summary.generate"
                => Pick(language, "Outils admin résumés…", "Admin summary tools…", "Herramientas admin de resúmenes…", "Ferramentas admin de resumos…", "Admin-Zusammenfassungstools…", "Strumenti admin riassunti…"),
            "admin.catalog.health" or "admin.catalog.rescan_now"
                => Pick(language, "Outils admin catalogue…", "Admin catalog tools…", "Herramientas admin del catálogo…", "Ferramentas admin do catálogo…", "Admin-Katalogwerkzeuge…", "Strumenti admin catalogo…"),
            "admin.ingestion.reindex"
                => Pick(language, "Relance ingestion…", "Restarting ingestion…", "Reinicio de ingestión…", "Reiniciando ingestão…", "Ingestion wird neu gestartet…", "Riavvio ingestione…"),
            "admin.jobs.list" or "admin.jobs.cancel"
                => Pick(language, "Traitements serveur…", "Server processing…", "Procesos del servidor…", "Processamentos servidor…", "Serververarbeitung…", "Elaborazioni server…"),
            "diagnostic.performance"
                => Pick(language, "Diagnostic…", "Diagnostics…", "Diagnóstico…", "Diagnóstico…", "Diagnose…", "Diagnostica…"),
            "export.create"
                => Pick(language, "Export…", "Export…", "Exportación…", "Exportação…", "Export…", "Esportazione…"),
            "support.bundle"
                => Pick(language, "Support…", "Support…", "Soporte…", "Suporte…", "Support…", "Supporto…"),
            "rag.debug.scroll"
                => Pick(language, "Diagnostic…", "Diagnostics…", "Diagnóstico…", "Diagnóstico…", "Diagnose…", "Diagnostica…"),
            _ => PhaseTools(language)
        };

    public static string JsonEnvelopeError(string? language)
        => Pick(language,
            "⚠️ La réponse interne est arrivée dans un format inattendu. Peux-tu relancer ta question (ou reformuler) ?",
            "⚠️ The internal response arrived in an unexpected format. Please retry (or rephrase).",
            "⚠️ La respuesta interna llegó con un formato inesperado. Vuelve a intentarlo o reformúlala.",
            "⚠️ A resposta interna chegou em um formato inesperado. Tente novamente ou reformule.",
            "⚠️ Die interne Antwort kam in einem unerwarteten Format an. Bitte versuche es erneut oder formuliere die Frage um.",
            "⚠️ La risposta interna è arrivata in un formato inatteso. Riprova o riformula la richiesta.");

    public static string StatsTitle(string? language)
        => Pick(language, "Statistiques du catalogue :", "Catalog statistics:", "Estadísticas del catálogo:", "Estatísticas do catálogo:", "Katalogstatistiken:", "Statistiche del catalogo:");

    public static string StatsTitle(string? language, string? scopePath)
    {
        var scope = (scopePath ?? string.Empty).Replace('\\', '/').Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(scope))
            return StatsTitle(language);

        return Pick(language,
            $"Statistiques de la catégorie {scope} :",
            $"Category statistics for {scope}:",
            $"Estadísticas de la categoría {scope}:",
            $"Estatísticas da categoria {scope}:",
            $"Kategoristatistiken für {scope}:",
            $"Statistiche della categoria {scope}:");
    }

    public static string StatsIndexedDocuments(int total, string? language)
        => Pick(language, $"- Documents indexés : {total}", $"- Indexed documents: {total}", $"- Documentos indexados: {total}", $"- Documentos indexados: {total}", $"- Indexierte Dokumente: {total}", $"- Documenti indicizzati: {total}");

    public static string StatsTotalFolders(int total, string? language)
        => Pick(language, $"- Dossiers totaux : {total}", $"- Total folders: {total}", $"- Carpetas totales: {total}", $"- Pastas totais: {total}", $"- Ordner insgesamt: {total}", $"- Cartelle totali: {total}");

    public static string StatsEmptyFolders(int total, string? language)
        => Pick(language, $"- Dossiers vides : {total}", $"- Empty folders: {total}", $"- Carpetas vacías: {total}", $"- Pastas vazias: {total}", $"- Leere Ordner: {total}", $"- Cartelle vuote: {total}");

    public static string StatsMaximumDepth(int depth, string? language)
        => Pick(language, $"- Profondeur maximale : {depth}", $"- Maximum depth: {depth}", $"- Profundidad máxima: {depth}", $"- Profundidade máxima: {depth}", $"- Maximale Tiefe: {depth}", $"- Profondità massima: {depth}");

    public static string StatsMainStructure(string? language)
        => Pick(language, "- Structure principale :", "- Main structure:", "- Estructura principal:", "- Estrutura principal:", "- Hauptstruktur:", "- Struttura principale:");

    public static string FirstLevelFolders(int count, string? language)
        => Pick(language, $"- Dossiers de premier niveau : {count}", $"- First-level folders: {count}", $"- Carpetas de primer nivel: {count}", $"- Pastas de primeiro nível: {count}", $"- Ordner der ersten Ebene: {count}", $"- Cartelle di primo livello: {count}");

    public static string FolderDepthLabel(int depth, string? language)
        => Lang(language) switch
        {
            "en" => depth switch { 1 => "First-level folders", 2 => "Second-level folders", 3 => "Third-level folders", _ => $"Level {depth} folders" },
            "es" => depth switch { 1 => "Carpetas de primer nivel", 2 => "Carpetas de segundo nivel", 3 => "Carpetas de tercer nivel", _ => $"Carpetas de nivel {depth}" },
            "pt" => depth switch { 1 => "Pastas de primeiro nível", 2 => "Pastas de segundo nível", 3 => "Pastas de terceiro nível", _ => $"Pastas de nível {depth}" },
            "de" => depth switch { 1 => "Ordner der ersten Ebene", 2 => "Ordner der zweiten Ebene", 3 => "Ordner der dritten Ebene", _ => $"Ordner der Ebene {depth}" },
            "it" => depth switch { 1 => "Cartelle di primo livello", 2 => "Cartelle di secondo livello", 3 => "Cartelle di terzo livello", _ => $"Cartelle di livello {depth}" },
            _ => depth switch { 1 => "Dossiers de premier niveau", 2 => "Dossiers de deuxième niveau", 3 => "Dossiers de troisième niveau", _ => $"Dossiers de niveau {depth}" },
        };

    public static string RootFolderLine(string name, int total, int direct, int subfolders, string? language)
        => Pick(language,
            $"  • {name}: {total} document(s), {subfolders} sous-dossier(s)",
            $"  • {name}: {total} document(s), {subfolders} subfolder(s)",
            $"  • {name}: {total} documento(s), {subfolders} subcarpeta(s)",
            $"  • {name}: {total} documento(s), {subfolders} subpasta(s)",
            $"  • {name}: {total} Dokument(e), {subfolders} Unterordner",
            $"  • {name}: {total} documento/i, {subfolders} sottocartella/e");

    public static string NoSubfoldersInScope(string? language)
        => Pick(language,
            "  • Aucun dossier ni sous-dossier.",
            "  • No folder or subfolder.",
            "  • No hay carpetas ni subcarpetas.",
            "  • Nenhuma pasta nem subpasta.",
            "  • Keine Ordner oder Unterordner.",
            "  • Nessuna cartella né sottocartella.");

    public static string AdminRescanQueued(string? language, string? jobId)
    {
        _ = jobId;
        return Pick(language,
            "Le rescan du catalogue est en cours…",
            "The catalog rescan is running…",
            "El reescaneo del catálogo está en curso…",
            "O reescaneamento do catálogo está em andamento…",
            "Der Katalog-Rescan läuft…",
            "La scansione del catalogo è in corso…");
    }

    public static string AdminReindexStarted(string? language, string documentLabel)
        => Pick(language,
            $"La réindexation du document {documentLabel} a été lancée.",
            $"The reindexing of document {documentLabel} has been started.",
            $"La reindexación del documento {documentLabel} se ha iniciado.",
            $"A reindexação do documento {documentLabel} foi iniciada.",
            $"Die Neuindexierung des Dokuments {documentLabel} wurde gestartet.",
            $"La reindicizzazione del documento {documentLabel} è stata avviata.");

    public static string AdminReindexProgressPhase(string? language, string? phase, int? percent = null, int? current = null, int? total = null, int? elapsedSeconds = null)
    {
        var normalized = (phase ?? string.Empty).Trim().ToLowerInvariant();
        var elapsed = elapsedSeconds.GetValueOrDefault();
        var elapsedPart = elapsed > 0 ? $" • {elapsed}s" : string.Empty;
        var percentPart = percent.HasValue ? $"{Math.Clamp(percent.Value, 0, 100)} % • " : string.Empty;
        var countPart = current.HasValue && total.HasValue && total.Value > 0 ? $" ({Math.Min(current.Value, total.Value)}/{total.Value})" : string.Empty;

        return normalized switch
        {
            "queued" => Pick(language, $"En file d'attente…{elapsedPart}", $"Queued…{elapsedPart}", $"En cola…{elapsedPart}", $"Em fila…{elapsedPart}", $"In der Warteschlange…{elapsedPart}", $"In coda…{elapsedPart}"),
            "preparing" => Pick(language, $"{percentPart}Préparation du document…{elapsedPart}", $"{percentPart}Preparing document…{elapsedPart}", $"{percentPart}Preparando el documento…{elapsedPart}", $"{percentPart}Preparando o documento…{elapsedPart}", $"{percentPart}Dokument wird vorbereitet…{elapsedPart}", $"{percentPart}Preparazione del documento…{elapsedPart}"),
            "extracting" => Pick(language, $"{percentPart}Extraction du contenu…{elapsedPart}", $"{percentPart}Extracting content…{elapsedPart}", $"{percentPart}Extrayendo el contenido…{elapsedPart}", $"{percentPart}Extraindo o conteúdo…{elapsedPart}", $"{percentPart}Inhalt wird extrahiert…{elapsedPart}", $"{percentPart}Estrazione del contenuto…{elapsedPart}"),
            "embedding" or "indexing" => Pick(language, $"{percentPart}Indexation{countPart}…{elapsedPart}", $"{percentPart}Indexing{countPart}…{elapsedPart}", $"{percentPart}Indexación{countPart}…{elapsedPart}", $"{percentPart}Indexação{countPart}…{elapsedPart}", $"{percentPart}Indexierung{countPart}…{elapsedPart}", $"{percentPart}Indicizzazione{countPart}…{elapsedPart}"),
            "deleting" => Pick(language, $"{percentPart}Nettoyage des anciens points…{elapsedPart}", $"{percentPart}Cleaning previous points…{elapsedPart}", $"{percentPart}Limpiando puntos anteriores…{elapsedPart}", $"{percentPart}Limpando pontos anteriores…{elapsedPart}", $"{percentPart}Vorherige Punkte werden bereinigt…{elapsedPart}", $"{percentPart}Pulizia dei punti precedenti…{elapsedPart}"),
            "finalizing" => Pick(language, $"{percentPart}Finalisation…{elapsedPart}", $"{percentPart}Finalizing…{elapsedPart}", $"{percentPart}Finalizando…{elapsedPart}", $"{percentPart}Finalizando…{elapsedPart}", $"{percentPart}Finalisierung…{elapsedPart}", $"{percentPart}Finalizzazione…{elapsedPart}"),
            _ => Pick(language, $"{percentPart}En cours{countPart}…{elapsedPart}", $"{percentPart}Running{countPart}…{elapsedPart}", $"{percentPart}En curso{countPart}…{elapsedPart}", $"{percentPart}Em andamento{countPart}…{elapsedPart}", $"{percentPart}Läuft{countPart}…{elapsedPart}", $"{percentPart}In corso{countPart}…{elapsedPart}")
        };
    }

    public static string AdminReindexQueued(string? language, string documentLabel, string? jobId)
    {
        _ = jobId;
        return Pick(language,
            $"La réindexation du document {documentLabel} est en file d'attente.",
            $"The reindexing of document {documentLabel} is queued.",
            $"La reindexación del documento {documentLabel} está en cola.",
            $"A reindexação do documento {documentLabel} está na fila.",
            $"Die Neuindexierung des Dokuments {documentLabel} ist in der Warteschlange.",
            $"La reindicizzazione del documento {documentLabel} è in coda.");
    }

    public static string AdminReindexRunning(string? language, string documentLabel, int? elapsedSeconds = null)
    {
        _ = elapsedSeconds;
        return Pick(language,
            $"La réindexation du document {documentLabel} est en cours.",
            $"The reindexing of document {documentLabel} is running.",
            $"La reindexación del documento {documentLabel} está en curso.",
            $"A reindexação do documento {documentLabel} está em andamento.",
            $"Die Neuindexierung des Dokuments {documentLabel} läuft.",
            $"La reindicizzazione del documento {documentLabel} è in corso.");
    }

    public static string AdminReindexRunningWithPercent(string? language, string documentLabel, int percent)
    {
        var p = Math.Clamp(percent, 0, 100);
        return Pick(language,
            $"La réindexation du document {documentLabel} est en cours ({p} %).",
            $"The reindexing of document {documentLabel} is running ({p}%).",
            $"La reindexación del documento {documentLabel} está en curso ({p} %).",
            $"A reindexação do documento {documentLabel} está em andamento ({p} %).",
            $"Die Neuindexierung des Dokuments {documentLabel} läuft ({p} %).",
            $"La reindicizzazione del documento {documentLabel} è in corso ({p} %).");
    }

    public static string AdminReindexCompleted(string? language, string documentLabel)
        => Pick(language,
            $"La réindexation du document {documentLabel} est terminée.",
            $"The reindexing of document {documentLabel} is complete.",
            $"La reindexación del documento {documentLabel} ha terminado.",
            $"A reindexação do documento {documentLabel} está concluída.",
            $"Die Neuindexierung des Dokuments {documentLabel} ist abgeschlossen.",
            $"La reindicizzazione del documento {documentLabel} è completata.");

    public static string AdminReindexAlreadyActive(string? language, string documentLabel)
        => Pick(language,
            $"Une ingestion ou réindexation est déjà active pour le document {documentLabel}. Ouvre le centre des jobs admin pour suivre l'avancement.",
            $"An ingestion or reindexing job is already active for document {documentLabel}. Open the admin jobs center to follow the progress.",
            $"Ya hay una ingestión o reindexación activa para el documento {documentLabel}. Abre el centro de trabajos admin para seguir el avance.",
            $"Já existe uma ingestão ou reindexação ativa para o documento {documentLabel}. Abre o centro de jobs admin para acompanhar o progresso.",
            $"Für das Dokument {documentLabel} läuft bereits eine Ingestion oder Neuindexierung. Öffne das Admin-Job-Center, um den Fortschritt zu verfolgen.",
            $"Per il documento {documentLabel} è già attiva un'ingestione o reindicizzazione. Apri il centro job admin per seguire l'avanzamento.");

    public static string DocumentTargetNotFound(string? language, string? documentRef)
    {
        var label = string.IsNullOrWhiteSpace(documentRef) ? Pick(language, "ce document", "this document", "este documento", "este documento", "dieses Dokument", "questo documento") : documentRef.Trim();
        return Pick(language,
            $"Le document {label} n'existe pas ou n'a pas été trouvé.",
            $"The document {label} does not exist or was not found.",
            $"El documento {label} no existe o no se ha encontrado.",
            $"O documento {label} não existe ou não foi encontrado.",
            $"Das Dokument {label} existiert nicht oder wurde nicht gefunden.",
            $"Il documento {label} non esiste o non è stato trovato.");
    }


    public static string DocumentTargetIsCategory(string? language, string? documentRef)
    {
        var label = string.IsNullOrWhiteSpace(documentRef) ? Pick(language, "cette référence", "this reference", "esta referencia", "esta referência", "diese Referenz", "questo riferimento") : documentRef.Trim();
        return Pick(language,
            $"La référence {label} correspond à un dossier ou une catégorie, pas à un document réindexable. Précise le nom exact du document.",
            $"The reference {label} points to a folder or category, not to a reindexable document. Please provide the exact document name.",
            $"La referencia {label} corresponde a una carpeta o categoría, no a un documento reindexable. Indica el nombre exacto del documento.",
            $"A referência {label} corresponde a uma pasta ou categoria, não a um documento reindexável. Indique o nome exato do documento.",
            $"Die Referenz {label} verweist auf einen Ordner oder eine Kategorie, nicht auf ein neu zu indexierendes Dokument. Bitte gib den genauen Dokumentnamen an.",
            $"Il riferimento {label} corrisponde a una cartella o categoria, non a un documento reindicizzabile. Indica il nome esatto del documento.");
    }

    public static string DocumentTargetAmbiguous(string? language, string? documentRef)
    {
        var label = string.IsNullOrWhiteSpace(documentRef) ? Pick(language, "ce document", "this document", "este documento", "este documento", "dieses Dokument", "questo documento") : documentRef.Trim();
        return Pick(language,
            $"La référence {label} est ambiguë. Précise le nom exact du document.",
            $"The reference {label} is ambiguous. Please provide the exact document name.",
            $"La referencia {label} es ambigua. Indica el nombre exacto del documento.",
            $"A referência {label} é ambígua. Indique o nome exato do documento.",
            $"Die Referenz {label} ist mehrdeutig. Bitte gib den genauen Dokumentnamen an.",
            $"Il riferimento {label} è ambiguo. Indica il nome esatto del documento.");
    }



    // Backward/forward-compatible aliases used by newer reindex hardening tests and patches.
    public static string AdminReindexDocumentNotFound(string? language, string? documentRef)
        => DocumentTargetNotFound(language, documentRef);

    public static string AdminReindexDocumentTargetIsCategory(string? language, string? documentRef)
        => DocumentTargetIsCategory(language, documentRef);

    public static string AdminReindexDocumentAmbiguous(string? language, string? documentRef)
        => DocumentTargetAmbiguous(language, documentRef);

    public static string AdminReindexFailed(string? language, string documentLabel, string? error)
    {
        var detail = string.IsNullOrWhiteSpace(error) ? string.Empty : " " + error.Trim();
        return Pick(language,
            $"La réindexation du document {documentLabel} a échoué.{detail}",
            $"The reindexing of document {documentLabel} failed.{detail}",
            $"La reindexación del documento {documentLabel} ha fallado.{detail}",
            $"A reindexação do documento {documentLabel} falhou.{detail}",
            $"Die Neuindexierung des Dokuments {documentLabel} ist fehlgeschlagen.{detail}",
            $"La reindicizzazione del documento {documentLabel} non è riuscita.{detail}");
    }

    public static string AdminJobQueued(string? language, string? actionLabel, int? elapsedSeconds = null)
    {
        var elapsed = elapsedSeconds.GetValueOrDefault();
        var suffix = elapsed > 0 ? $" ({elapsed}s)" : string.Empty;
        return Pick(language,
            $"{actionLabel ?? "Action admin"} en file d'attente…{suffix}",
            $"{actionLabel ?? "Admin action"} queued…{suffix}",
            $"{actionLabel ?? "Acción admin"} en cola…{suffix}",
            $"{actionLabel ?? "Ação admin"} em fila…{suffix}",
            $"{actionLabel ?? "Admin-Aktion"} in der Warteschlange…{suffix}",
            $"{actionLabel ?? "Azione admin"} in coda…{suffix}");
    }

    public static string AdminJobRunning(string? language, string? actionLabel, int? elapsedSeconds = null)
    {
        var elapsed = elapsedSeconds.GetValueOrDefault();
        var suffix = elapsed > 0 ? $" ({elapsed}s)" : string.Empty;
        return Pick(language,
            $"{actionLabel ?? "Action admin"} en cours…{suffix}",
            $"{actionLabel ?? "Admin action"} running…{suffix}",
            $"{actionLabel ?? "Acción admin"} en curso…{suffix}",
            $"{actionLabel ?? "Ação admin"} em curso…{suffix}",
            $"{actionLabel ?? "Admin-Aktion"} läuft…{suffix}",
            $"{actionLabel ?? "Azione admin"} in corso…{suffix}");
    }

    public static string AdminRescanCompleted(string? language, int? totalDocuments, int? totalCategories, int? maxDepth)
    {
        var details = new StringBuilder();
        var documentCount = totalDocuments.GetValueOrDefault();
        var categoryCount = totalCategories.GetValueOrDefault();
        var depth = maxDepth.GetValueOrDefault();
        if (documentCount > 0)
            details.Append(Pick(language, $" {documentCount} document(s)", $" {documentCount} document(s)", $" {documentCount} documento(s)", $" {documentCount} documento(s)", $" {documentCount} Dokument(e)", $" {documentCount} documento/i"));
        if (categoryCount > 0)
            details.Append(Pick(language, $", {categoryCount} dossier(s)", $", {categoryCount} folder(s)", $", {categoryCount} carpeta(s)", $", {categoryCount} pasta(s)", $", {categoryCount} Ordner", $", {categoryCount} cartella/e"));
        if (depth > 0)
            details.Append(Pick(language, $", profondeur {depth}", $", depth {depth}", $", profundidad {depth}", $", profundidade {depth}", $", Tiefe {depth}", $", profondità {depth}"));

        return Pick(language,
            $"Le rescan du catalogue est terminé.{details}",
            $"The catalog rescan is complete.{details}",
            $"El reescaneo del catálogo ha terminado.{details}",
            $"O reescaneamento do catálogo foi concluído.{details}",
            $"Der Katalog-Rescan ist abgeschlossen.{details}",
            $"La scansione del catalogo è completata.{details}");
    }
}
