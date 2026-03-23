using System;
using System.Collections.Generic;

namespace SAAIA.Client.WinUI.Services;

internal static class ClientUiText
{
    private static readonly Dictionary<string, Dictionary<string, string>> Catalog = new(StringComparer.OrdinalIgnoreCase)
    {
        ["status.ready"] = Multi("Prêt.", "Ready.", "Listo.", "Pronto.", "Bereit.", "Pronto."),
        ["status.settings_applied"] = Multi("Paramètres appliqués.", "Settings applied.", "Configuración aplicada.", "Definições aplicadas.", "Einstellungen angewendet.", "Impostazioni applicate."),
        ["status.settings_failed"] = Multi("Échec des paramètres : ", "Settings failed: ", "Error de configuración: ", "Falha nas definições: ", "Einstellungsfehler: ", "Errore impostazioni: "),
        ["status.help_busy"] = Multi("Une réponse est déjà en cours.", "A reply is already in progress.", "Ya hay una respuesta en curso.", "Já existe uma resposta em curso.", "Eine Antwort läuft bereits.", "È già in corso una risposta."),
        ["status.help_connect_first"] = Multi("Connecte d'abord l'application.", "Connect the app first.", "Conecta primero la aplicación.", "Liga primeiro a aplicação.", "Verbinde zuerst die Anwendung.", "Collega prima l'applicazione."),
        ["status.help_send_failed"] = Multi("Impossible d'envoyer la commande d'aide : ", "Could not send the help command: ", "No se pudo enviar el comando de ayuda: ", "Não foi possível enviar o comando de ajuda: ", "Der Hilfebefehl konnte nicht gesendet werden: ", "Impossibile inviare il comando di aiuto: "),

        ["header.chats"] = Multi("Discussions", "Chats", "Chats", "Chats", "Chats", "Chat"),
        ["header.help"] = Multi("Aide", "Help", "Ayuda", "Ajuda", "Hilfe", "Aiuto"),
        ["header.settings"] = Multi("Paramètres", "Settings", "Configuración", "Definições", "Einstellungen", "Impostazioni"),
        ["panel.chats"] = Multi("Discussions", "Chats", "Chats", "Chats", "Chats", "Chat"),
        ["button.new"] = Multi("Nouveau", "New", "Nuevo", "Novo", "Neu", "Nuovo"),
        ["button.connect"] = Multi("Connecter", "Connect", "Conectar", "Ligar", "Verbinden", "Connetti"),
        ["button.setup"] = Multi("Setup", "Setup", "Setup", "Setup", "Setup", "Setup"),
        ["button.send"] = Multi("Envoyer", "Send", "Enviar", "Enviar", "Senden", "Invia"),
        ["button.cancel"] = Multi("Annuler", "Cancel", "Cancelar", "Cancelar", "Abbrechen", "Annulla"),
        ["button.jump_bottom"] = Multi("Aller en bas", "Jump to bottom", "Ir abajo", "Ir para baixo", "Nach unten", "Vai in basso"),
        ["button.search"] = Multi("Rechercher", "Search", "Buscar", "Pesquisar", "Suchen", "Cerca"),
        ["typing"] = Multi("Écriture…", "Typing…", "Escribiendo…", "A escrever…", "Schreibt…", "Sta scrivendo…"),
        ["input.placeholder"] = Multi("Tape ta question...", "Type your question...", "Escribe tu pregunta...", "Escreve a tua pergunta...", "Schreibe deine Frage...", "Scrivi la tua domanda..."),
        ["chat.default_title"] = Multi("Nouvelle discussion", "New chat", "Nuevo chat", "Novo chat", "Neuer Chat", "Nuova chat"),
        ["session.menu.rename"] = Multi("Renommer", "Rename", "Renombrar", "Renomear", "Umbenennen", "Rinomina"),
        ["session.menu.delete"] = Multi("Supprimer", "Delete", "Eliminar", "Eliminar", "Löschen", "Elimina"),
        ["session.rename.title"] = Multi("Renommer la discussion", "Rename chat", "Renombrar chat", "Renomear chat", "Chat umbenennen", "Rinomina chat"),
        ["session.rename.placeholder"] = Multi("Titre…", "Title…", "Título…", "Título…", "Titel…", "Titolo…"),
        ["session.rename.save"] = Multi("Enregistrer", "Save", "Guardar", "Guardar", "Speichern", "Salva"),
        ["session.rename.done"] = Multi("Discussion renommée.", "Chat renamed.", "Chat renombrado.", "Chat renomeado.", "Chat umbenannt.", "Chat rinominata."),
        ["session.rename.failed"] = Multi("Échec du renommage : ", "Rename failed: ", "Error al renombrar: ", "Falha ao renomear: ", "Umbenennen fehlgeschlagen: ", "Rinomina non riuscita: "),
        ["session.delete.title"] = Multi("Supprimer la discussion ?", "Delete chat?", "¿Eliminar chat?", "Eliminar chat?", "Chat löschen?", "Eliminare la chat?"),
        ["session.delete.confirm"] = Multi("Supprimer définitivement : \"{0}\" ?", "Delete permanently: \"{0}\"?", "¿Eliminar permanentemente: \"{0}\"?", "Eliminar permanentemente: \"{0}\"?", "Endgültig löschen: \"{0}\"?", "Eliminare definitivamente: \"{0}\"?"),
        ["session.delete.confirm_button"] = Multi("Supprimer", "Delete", "Eliminar", "Eliminar", "Löschen", "Elimina"),
        ["session.delete.done"] = Multi("Discussion supprimée.", "Chat deleted.", "Chat eliminado.", "Chat eliminado.", "Chat gelöscht.", "Chat eliminata."),
        ["session.delete.failed"] = Multi("Échec de la suppression : ", "Delete failed: ", "Error al eliminar: ", "Falha ao eliminar: ", "Löschen fehlgeschlagen: ", "Eliminazione non riuscita: "),
        ["status.connect_first"] = Multi("Clique d'abord sur Connecter.", "Click Connect first.", "Haz clic primero en Conectar.", "Clica primeiro em Ligar.", "Klicke zuerst auf Verbinden.", "Fai prima clic su Connetti."),
        ["status.creating_chat"] = Multi("Création d'une nouvelle discussion…", "Creating new chat…", "Creando un nuevo chat…", "A criar uma nova conversa…", "Neuer Chat wird erstellt…", "Creazione di una nuova chat…"),
        ["status.new_session"] = Multi("Nouvelle session : {0}", "New session: {0}", "Nueva sesión: {0}", "Nova sessão: {0}", "Neue Sitzung: {0}", "Nuova sessione: {0}"),
        ["status.new_chat_failed"] = Multi("Échec de la nouvelle discussion : ", "New chat failed: ", "Error al crear el chat: ", "Falha ao criar a conversa: ", "Neuer Chat fehlgeschlagen: ", "Creazione nuova chat non riuscita: "),
        ["status.loading_chat"] = Multi("Chargement de la discussion…", "Loading chat…", "Cargando chat…", "A carregar a conversa…", "Chat wird geladen…", "Caricamento chat…"),
        ["status.loaded_session"] = Multi("Chargée. Session : {0}", "Loaded. Session: {0}", "Cargada. Sesión: {0}", "Carregada. Sessão: {0}", "Geladen. Sitzung: {0}", "Caricata. Sessione: {0}"),
        ["startup.title"] = Multi("SAAIA", "SAAIA", "SAAIA", "SAAIA", "SAAIA", "SAAIA"),
        ["startup.subtitle"] = Multi("Initialisation de l'application…", "Initializing the application…", "Inicializando la aplicación…", "Inicializando a aplicação…", "Anwendung wird initialisiert…", "Inizializzazione dell'applicazione…"),
        ["startup.status.initializing"] = Multi("Initialisation…", "Initializing…", "Inicializando…", "Inicializando…", "Initialisierung…", "Inizializzazione…"),
        ["startup.status.checking_setup"] = Multi("Vérification de la configuration…", "Checking configuration…", "Comprobando la configuración…", "Verificando a configuração…", "Konfiguration wird geprüft…", "Verifica della configurazione…"),
        ["startup.status.starting_assistant"] = Multi("Démarrage de l'assistant…", "Starting assistant…", "Iniciando el asistente…", "Iniciando o assistente…", "Assistent wird gestartet…", "Avvio dell'assistente…"),
        ["startup.status.connecting"] = Multi("Connexion au serveur…", "Connecting to server…", "Conectando al servidor…", "A ligar ao servidor…", "Verbindung zum Server…", "Connessione al server…"),
        ["status.init_failed"] = Multi("Échec de l'initialisation : ", "Init failed: ", "Error de inicialización: ", "Falha na inicialização: ", "Initialisierung fehlgeschlagen: ", "Errore di inizializzazione: "),

        ["dialog.close"] = Multi("Fermer", "Close", "Cerrar", "Fechar", "Schließen", "Chiudi"),

        ["help.title"] = Multi("Aide rapide", "Quick help", "Ayuda rápida", "Ajuda rápida", "Schnellhilfe", "Aiuto rapido"),
        ["help.subtitle.ready"] = Multi("Choisis une action stable, puis cherche une catégorie ou un document seulement si nécessaire.", "Choose a stable action, then search for a category or document only when needed.", "Elige una acción estable y busca una categoría o un documento solo si hace falta.", "Escolhe uma ação estável e pesquisa uma categoria ou documento apenas se necessário.", "Wähle eine stabile Aktion und suche nur bei Bedarf nach einer Kategorie oder einem Dokument.", "Scegli un'azione stabile e cerca una categoria o un documento solo se necessario."),
        ["help.subtitle.busy"] = Multi("Les commandes sont désactivées tant qu'une réponse est en cours.", "Commands are disabled while a reply is in progress.", "Los comandos están desactivados mientras haya una respuesta en curso.", "Os comandos ficam desativados enquanto houver uma resposta em curso.", "Befehle sind deaktiviert, solange eine Antwort läuft.", "I comandi sono disattivati mentre è in corso una risposta."),
        ["help.subtitle.disconnected"] = Multi("Connecte l'application pour utiliser l'envoi direct depuis ce panneau.", "Connect the app to use direct sending from this panel.", "Conecta la aplicación para usar el envío directo desde este panel.", "Liga a aplicação para usar o envio direto neste painel.", "Verbinde die Anwendung, um das direkte Senden aus diesem Bereich zu nutzen.", "Collega l'applicazione per usare l'invio diretto da questo pannello."),
        ["help.section.quick"] = Multi("Actions rapides", "Quick actions", "Acciones rápidas", "Ações rápidas", "Schnellaktionen", "Azioni rapide"),
        ["help.section.guided"] = Multi("Actions guidées", "Guided actions", "Acciones guiadas", "Ações guiadas", "Geführte Aktionen", "Azioni guidate"),
        ["help.section.admin"] = Multi("Administration", "Administration", "Administración", "Administração", "Administration", "Amministrazione"),
        ["help.section.results"] = Multi("Résultats", "Results", "Resultados", "Resultados", "Ergebnisse", "Risultati"),
        ["help.mode.none"] = Multi("Sélectionne une action guidée ci-dessous.", "Select a guided action below.", "Selecciona una acción guiada abajo.", "Seleciona uma ação guiada abaixo.", "Wähle unten eine geführte Aktion aus.", "Seleziona qui sotto un'azione guidata."),
        ["help.mode.category.documents"] = Multi("Choisis une catégorie pour lister ses documents.", "Choose a category to list its documents.", "Elige una categoría para listar sus documentos.", "Escolhe uma categoria para listar os seus documentos.", "Wähle eine Kategorie, um ihre Dokumente aufzulisten.", "Scegli una categoria per elencare i suoi documenti."),
        ["help.mode.category.stats"] = Multi("Choisis une catégorie pour voir ses statistiques.", "Choose a category to show its statistics.", "Elige una categoría para ver sus estadísticas.", "Escolhe uma categoria para ver as suas estatísticas.", "Wähle eine Kategorie, um ihre Statistiken zu sehen.", "Scegli una categoria per vedere le sue statistiche."),
        ["help.mode.document.search"] = Multi("Recherche un document par nom ou mot-clé.", "Search for a document by name or keyword.", "Busca un documento por nombre o palabra clave.", "Pesquisa um documento por nome ou palavra-chave.", "Suche ein Dokument nach Name oder Stichwort.", "Cerca un documento per nome o parola chiave."),
        ["help.mode.document.reindex"] = Multi("Recherche un document pour relancer son ingestion.", "Search for a document to re-trigger its ingestion.", "Busca un documento para relanzar su ingestión.", "Pesquisa um documento para relançar a sua ingestão.", "Suche ein Dokument, um seine Verarbeitung erneut zu starten.", "Cerca un documento per rilanciare la sua ingestione."),
        ["help.search.placeholder.category"] = Multi("Recherche une catégorie…", "Search a category…", "Busca una categoría…", "Pesquisa uma categoria…", "Kategorie suchen…", "Cerca una categoria…"),
        ["help.search.placeholder.document"] = Multi("Recherche un document…", "Search a document…", "Busca un documento…", "Pesquisa um documento…", "Dokument suchen…", "Cerca un documento…"),
        ["help.search.hint.category"] = Multi("Aucune liste massive : les résultats sont filtrés localement et limités.", "No massive list: results are filtered locally and limited.", "Sin listas masivas: los resultados se filtran localmente y se limitan.", "Sem listas massivas: os resultados são filtrados localmente e limitados.", "Keine riesige Liste: Ergebnisse werden lokal gefiltert und begrenzt.", "Nessun elenco enorme: i risultati sono filtrati localmente e limitati."),
        ["help.search.hint.document"] = Multi("La recherche de documents interroge le serveur et renvoie seulement quelques résultats.", "Document search queries the server and returns only a few results.", "La búsqueda de documentos consulta el servidor y devuelve solo unos pocos resultados.", "A pesquisa de documentos consulta o servidor e devolve apenas alguns resultados.", "Die Dokumentensuche fragt den Server ab und liefert nur wenige Ergebnisse.", "La ricerca dei documenti interroga il server e restituisce solo pochi risultati."),
        ["help.search.next_step.category"] = Multi("Étape suivante : la zone de recherche s'ouvre juste ci-dessous. Tape quelques lettres ou clique sur un résultat proposé.", "Next step: the search box opens just below. Type a few letters or click one of the proposed results.", "Siguiente paso: la zona de búsqueda se abre justo debajo. Escribe unas letras o haz clic en uno de los resultados propuestos.", "Passo seguinte: a caixa de pesquisa abre mesmo abaixo. Escreve algumas letras ou clica num dos resultados sugeridos.", "Nächster Schritt: Das Suchfeld öffnet sich direkt darunter. Gib ein paar Buchstaben ein oder klicke auf eines der vorgeschlagenen Ergebnisse.", "Passo successivo: la casella di ricerca si apre appena sotto. Digita alcune lettere oppure fai clic su uno dei risultati proposti."),
        ["help.search.next_step.document"] = Multi("Étape suivante : la zone de recherche s'ouvre juste ci-dessous. Tape au moins 2 caractères puis appuie sur Entrée ou sur Rechercher.", "Next step: the search box opens just below. Type at least 2 characters, then press Enter or Search.", "Siguiente paso: la zona de búsqueda se abre justo debajo. Escribe al menos 2 caracteres y luego pulsa Intro o Buscar.", "Passo seguinte: a caixa de pesquisa abre mesmo abaixo. Escreve pelo menos 2 caracteres e depois carrega em Enter ou em Pesquisar.", "Nächster Schritt: Das Suchfeld öffnet sich direkt darunter. Gib mindestens 2 Zeichen ein und drücke dann Enter oder Suche.", "Passo successivo: la casella di ricerca si apre appena sotto. Digita almeno 2 caratteri, poi premi Invio oppure Cerca."),
        ["help.search.top_categories"] = Multi("Suggestions dynamiques", "Dynamic suggestions", "Sugerencias dinámicas", "Sugestões dinâmicas", "Dynamische Vorschläge", "Suggerimenti dinamici"),
        ["help.search.loading"] = Multi("Recherche en cours…", "Searching…", "Buscando…", "A pesquisar…", "Suche läuft…", "Ricerca in corso…"),
        ["help.loading"] = Multi("Chargement des commandes et du catalogue…", "Loading commands and catalog…", "Cargando comandos y catálogo…", "A carregar comandos e catálogo…", "Befehle und Katalog werden geladen…", "Caricamento dei comandi e del catalogo…"),
        ["help.admin.badge"] = Multi("Admin", "Admin", "Admin", "Admin", "Admin", "Admin"),
        ["help.search.type_more"] = Multi("Tape au moins 2 caractères.", "Type at least 2 characters.", "Escribe al menos 2 caracteres.", "Escreve pelo menos 2 caracteres.", "Gib mindestens 2 Zeichen ein.", "Digita almeno 2 caratteri."),
        ["help.search.no_results"] = Multi("Aucun résultat.", "No results.", "Sin resultados.", "Sem resultados.", "Keine Ergebnisse.", "Nessun risultato."),
        ["help.none.categories"] = Multi("Aucune catégorie chargée pour le moment.", "No categories loaded yet.", "Aún no hay categorías cargadas.", "Ainda não há categorias carregadas.", "Noch keine Kategorien geladen.", "Nessuna categoria caricata al momento."),
        ["help.none.documents"] = Multi("Aucun document trouvé pour le moment.", "No documents found yet.", "Aún no se encontraron documentos.", "Ainda não foram encontrados documentos.", "Noch keine Dokumente gefunden.", "Nessun documento trovato al momento."),
        ["help.meta.documents"] = Multi("{0} document(s)", "{0} document(s)", "{0} documento(s)", "{0} documento(s)", "{0} Dokument(e)", "{0} documento/i"),

        ["cmd.catalog.categories"] = Multi("Voir les catégories du catalogue", "Show catalog categories", "Ver categorías del catálogo", "Ver categorias do catálogo", "Katalogkategorien anzeigen", "Mostra le categorie del catalogo"),
        ["cmd.catalog.stats"] = Multi("Voir les statistiques du catalogue", "Show catalog statistics", "Ver estadísticas del catálogo", "Ver estatísticas do catálogo", "Katalogstatistiken anzeigen", "Mostrare le statistiche del catalogo"),
        ["cmd.catalog.tree"] = Multi("Voir l'arborescence du catalogue", "Show the catalog tree", "Ver el árbol del catálogo", "Ver a árvore do catálogo", "Katalogbaum anzeigen", "Mostrare l'albero del catalogo"),
        ["cmd.guided.documents_by_category"] = Multi("Voir les documents d'une catégorie", "Show documents in a category", "Ver los documentos de una categoría", "Ver os documentos de uma categoria", "Dokumente einer Kategorie anzeigen", "Mostrare i documenti di una categoria"),
        ["cmd.guided.category_stats"] = Multi("Voir les statistiques d'une catégorie", "Show category statistics", "Ver las estadísticas de una categoría", "Ver as estatísticas de uma categoria", "Kategorienstatistiken anzeigen", "Mostrare le statistiche di una categoria"),
        ["cmd.guided.document_search"] = Multi("Rechercher un document", "Search for a document", "Buscar un documento", "Pesquisar um documento", "Nach einem Dokument suchen", "Cercare un documento"),
        ["cmd.guided.reindex"] = Multi("Réindexer un document", "Reindex a document", "Reindexar un documento", "Reindexar um documento", "Ein Dokument neu indexieren", "Reindicizzare un documento"),
        ["cmd.summary.missing.count"] = Multi("Compter les résumés manquants", "Count missing summaries", "Contar resúmenes faltantes", "Contar resumos em falta", "Fehlende Zusammenfassungen zählen", "Contare i riassunti mancanti"),
        ["cmd.summary.missing.list"] = Multi("Lister les résumés manquants", "List missing summaries", "Listar resúmenes faltantes", "Listar resumos em falta", "Fehlende Zusammenfassungen auflisten", "Elencare i riassunti mancanti"),
        ["cmd.summary.present.count"] = Multi("Compter les résumés stockés", "Count stored summaries", "Contar resúmenes almacenados", "Contar resumos armazenados", "Gespeicherte Zusammenfassungen zählen", "Contare i riassunti salvati"),
        ["cmd.summary.present.list"] = Multi("Lister les résumés stockés", "List stored summaries", "Listar resúmenes almacenados", "Listar resumos armazenados", "Gespeicherte Zusammenfassungen auflisten", "Elencare i riassunti salvati"),
        ["cmd.admin.rescan"] = Multi("Lancer un rescan du catalogue", "Run a catalog rescan", "Lanzar un reescaneo del catálogo", "Lançar um novo scan do catálogo", "Katalog erneut scannen", "Avviare una nuova scansione del catalogo"),

        ["settings.title"] = Multi("Paramètres", "Settings", "Configuración", "Definições", "Einstellungen", "Impostazioni"),
        ["settings.subtitle"] = Multi("Réglages de base pour l'interface et l'assistant local.", "Core settings for the interface and local assistant.", "Ajustes principales para la interfaz y el asistente local.", "Definições principais da interface e do assistente local.", "Grundeinstellungen für Oberfläche und lokalen Assistenten.", "Impostazioni di base per l'interfaccia e l'assistente locale."),
        ["settings.tab.general"] = Multi("Général", "General", "General", "Geral", "Allgemein", "Generale"),
        ["settings.tab.advanced"] = Multi("Options avancées", "Advanced options", "Opciones avanzadas", "Opções avançadas", "Erweiterte Optionen", "Opzioni avanzate"),
        ["settings.advanced.subtitle"] = Multi("Support, réparation et diagnostic.", "Support, repair and diagnostics.", "Soporte, reparación y diagnóstico.", "Suporte, reparação e diagnóstico.", "Support, Reparatur und Diagnose.", "Supporto, riparazione e diagnostica."),
        ["settings.section.interface"] = Multi("Interface", "Interface", "Interfaz", "Interface", "Oberfläche", "Interfaccia"),
        ["settings.section.behavior"] = Multi("Comportement", "Behavior", "Comportamiento", "Comportamento", "Verhalten", "Comportamento"),
        ["settings.interface.note"] = Multi("Ces réglages sont appliqués immédiatement après validation.", "These settings are applied immediately after confirmation.", "Estos ajustes se aplican inmediatamente tras la validación.", "Estas definições são aplicadas imediatamente após a validação.", "Diese Einstellungen werden nach dem Bestätigen sofort angewendet.", "Queste impostazioni vengono applicate subito dopo la conferma."),
        ["settings.apply"] = Multi("Appliquer", "Apply", "Aplicar", "Aplicar", "Anwenden", "Applica"),
        ["settings.language"] = Multi("Langue de l'interface", "Interface language", "Idioma de la interfaz", "Idioma da interface", "Sprache der Oberfläche", "Lingua dell'interfaccia"),
        ["settings.section.assistant"] = Multi("Assistant", "Assistant", "Asistente", "Assistente", "Assistent", "Assistente"),
        ["settings.section.repair"] = Multi("Dépannage assistant", "Assistant repair", "Reparación del asistente", "Reparação do assistente", "Assistent reparieren", "Riparazione assistente"),
        ["settings.section.support"] = Multi("Support", "Support", "Soporte", "Suporte", "Support", "Supporto"),
        ["settings.toggle.assistant"] = Multi("Assistant IA", "AI assistant", "Asistente IA", "Assistente IA", "KI-Assistent", "Assistente IA"),
        ["settings.toggle.assistant.on"] = Multi("Activé", "Enabled", "Activado", "Ativado", "Aktiviert", "Attivato"),
        ["settings.toggle.assistant.off"] = Multi("Désactivé", "Disabled", "Desactivado", "Desativado", "Deaktiviert", "Disattivato"),
        ["settings.toggle.strict"] = Multi("Mode strict", "Strict mode", "Modo estricto", "Modo estrito", "Strenger Modus", "Modalità rigorosa"),
        ["settings.toggle.strict.on"] = Multi("Sources uniquement", "Sources only", "Solo fuentes", "Só fontes", "Nur Quellen", "Solo fonti"),
        ["settings.toggle.strict.off"] = Multi("Standard", "Standard", "Estándar", "Padrão", "Standard", "Standard"),
        ["settings.rag_quality"] = Multi("Qualité de recherche", "Retrieval quality", "Calidad de búsqueda", "Qualidade de pesquisa", "Suchqualität", "Qualità della ricerca"),
        ["settings.style"] = Multi("Style de réponse", "Response style", "Estilo de respuesta", "Estilo de resposta", "Antwortstil", "Stile della risposta"),
        ["settings.length"] = Multi("Longueur de réponse", "Response length", "Longitud de respuesta", "Tamanho da resposta", "Antwortlänge", "Lunghezza della risposta"),
        ["settings.choice.quick"] = Multi("Rapide", "Quick", "Rápido", "Rápido", "Schnell", "Rapido"),
        ["settings.choice.balanced"] = Multi("Équilibré", "Balanced", "Equilibrado", "Equilibrado", "Ausgewogen", "Bilanciato"),
        ["settings.choice.deep"] = Multi("Approfondi", "Deep", "Profundo", "Profundo", "Tief", "Approfondito"),
        ["settings.choice.precise"] = Multi("Précis", "Precise", "Preciso", "Preciso", "Präzise", "Preciso"),
        ["settings.choice.creative"] = Multi("Créatif", "Creative", "Creativo", "Criativo", "Kreativ", "Creativo"),
        ["settings.choice.short"] = Multi("Court", "Short", "Corto", "Curto", "Kurz", "Breve"),
        ["settings.choice.standard"] = Multi("Standard", "Standard", "Estándar", "Padrão", "Standard", "Standard"),
        ["settings.choice.long"] = Multi("Long", "Long", "Largo", "Longo", "Lang", "Lungo"),
        ["settings.repair.button"] = Multi("Installer / réparer l'assistant…", "Install / repair assistant…", "Instalar / reparar el asistente…", "Instalar / reparar o assistente…", "Assistent installieren / reparieren…", "Installare / riparare l'assistente…"),
        ["settings.support.button"] = Multi("Exporter diagnostic…", "Export diagnostic…", "Exportar diagnóstico…", "Exportar diagnóstico…", "Diagnose exportieren…", "Esporta diagnostica…"),
        ["settings.support.note"] = Multi("Le diagnostic ne contient pas la clé API (elle est masquée).", "The diagnostic does not contain the API key (it is masked).", "El diagnóstico no contiene la clave API (está oculta).", "O diagnóstico não contém a chave API (está mascarada).", "Die Diagnose enthält keinen API-Schlüssel (er ist maskiert).", "La diagnostica non contiene la chiave API (è mascherata).")
    };

    public static string NormalizeLanguage(string? language)
    {
        var s = (language ?? string.Empty).Trim().ToLowerInvariant();
        return s switch
        {
            "fr" or "fr-ch" or "fr-fr" => "fr",
            "en" or "en-us" or "en-gb" => "en",
            "es" or "es-es" => "es",
            "pt" or "pt-pt" or "pt-br" => "pt",
            "de" or "de-ch" or "de-de" => "de",
            "it" or "it-ch" or "it-it" => "it",
            _ => "fr"
        };
    }

    public static string Get(string key, string? language)
    {
        var lang = NormalizeLanguage(language);
        if (Catalog.TryGetValue(key, out var values))
        {
            if (values.TryGetValue(lang, out var value))
                return value;
            if (values.TryGetValue("en", out var en))
                return en;
        }

        return key;
    }

    public static string Format(string key, string? language, params object[] args)
        => string.Format(Get(key, language), args);

    public static IReadOnlyList<(string Code, string Label)> GetLanguageOptions()
        => new[]
        {
            ("fr", "Français"),
            ("en", "English"),
            ("es", "Español"),
            ("pt", "Português"),
            ("de", "Deutsch"),
            ("it", "Italiano")
        };

    public static IReadOnlyList<string> SupportedLanguageCodes()
        => new[] { "fr", "en", "es", "pt", "de", "it" };

    public static string BuildPromptCategories(string? language)
        => NormalizeLanguage(language) switch
        {
            "en" => "Give me the catalog categories.",
            "es" => "Dame las categorías del catálogo.",
            "pt" => "Dá-me as categorias do catálogo.",
            "de" => "Zeige mir die Kategorien des Katalogs.",
            "it" => "Mostrami le categorie del catalogo.",
            _ => "Donne-moi les catégories du catalogue."
        };

    public static string BuildPromptCatalogStats(string? language)
        => NormalizeLanguage(language) switch
        {
            "en" => "Give me the catalog statistics.",
            "es" => "Dame las estadísticas del catálogo.",
            "pt" => "Dá-me as estatísticas do catálogo.",
            "de" => "Gib mir die Katalogstatistiken.",
            "it" => "Dammi le statistiche del catalogo.",
            _ => "Donne-moi les statistiques du catalogue."
        };

    public static string BuildPromptCatalogTree(string? language)
        => NormalizeLanguage(language) switch
        {
            "en" => "Show me the catalog tree.",
            "es" => "Muéstrame el árbol del catálogo.",
            "pt" => "Mostra-me a árvore do catálogo.",
            "de" => "Zeige mir den Katalogbaum.",
            "it" => "Mostrami l'albero del catalogo.",
            _ => "Montre-moi l'arborescence du catalogue."
        };

    public static string BuildPromptCategoryDocuments(string? language, string categoryName)
        => NormalizeLanguage(language) switch
        {
            "en" => $"List the documents in category {categoryName}.",
            "es" => $"Lista los documentos de la categoría {categoryName}.",
            "pt" => $"Lista os documentos da categoria {categoryName}.",
            "de" => $"Liste die Dokumente der Kategorie {categoryName}.",
            "it" => $"Elenca i documenti della categoria {categoryName}.",
            _ => $"Liste les documents de la catégorie {categoryName}."
        };

    public static string BuildPromptCategoryStats(string? language, string categoryName)
        => NormalizeLanguage(language) switch
        {
            "en" => $"Show me the statistics for category {categoryName}.",
            "es" => $"Muéstrame las estadísticas de la categoría {categoryName}.",
            "pt" => $"Mostra-me as estatísticas da categoria {categoryName}.",
            "de" => $"Zeige mir die Statistiken der Kategorie {categoryName}.",
            "it" => $"Mostrami le statistiche della categoria {categoryName}.",
            _ => $"Donne-moi les statistiques de la catégorie {categoryName}."
        };

    public static string BuildPromptSearchDocuments(string? language, string query)
        => NormalizeLanguage(language) switch
        {
            "en" => $"Search for documents matching {query}.",
            "es" => $"Busca los documentos que coinciden con {query}.",
            "pt" => $"Pesquisa os documentos que correspondem a {query}.",
            "de" => $"Suche nach Dokumenten, die zu {query} passen.",
            "it" => $"Cerca i documenti che corrispondono a {query}.",
            _ => $"Cherche les documents qui correspondent à {query}."
        };

    public static string BuildPromptSummaryMissingCount(string? language)
        => NormalizeLanguage(language) switch
        {
            "en" => "How many documents do not have a stored summary?",
            "es" => "¿Cuántos documentos no tienen un resumen almacenado?",
            "pt" => "Quantos documentos não têm um resumo armazenado?",
            "de" => "Wie viele Dokumente haben keine gespeicherte Zusammenfassung?",
            "it" => "Quanti documenti non hanno un riassunto memorizzato?",
            _ => "Combien de documents n'ont pas de résumé stocké ?"
        };

    public static string BuildPromptSummaryMissingList(string? language)
        => NormalizeLanguage(language) switch
        {
            "en" => "List the documents without a stored summary.",
            "es" => "Lista los documentos sin resumen almacenado.",
            "pt" => "Lista os documentos sem resumo armazenado.",
            "de" => "Liste die Dokumente ohne gespeicherte Zusammenfassung auf.",
            "it" => "Elenca i documenti senza riassunto memorizzato.",
            _ => "Liste les documents sans résumé stocké."
        };

    public static string BuildPromptSummaryPresentCount(string? language)
        => NormalizeLanguage(language) switch
        {
            "en" => "How many documents have a stored summary?",
            "es" => "¿Cuántos documentos tienen un resumen almacenado?",
            "pt" => "Quantos documentos têm um resumo armazenado?",
            "de" => "Wie viele Dokumente haben eine gespeicherte Zusammenfassung?",
            "it" => "Quanti documenti hanno un riassunto memorizzato?",
            _ => "Combien de documents ont un résumé stocké ?"
        };

    public static string BuildPromptSummaryPresentList(string? language)
        => NormalizeLanguage(language) switch
        {
            "en" => "List the documents with a stored summary.",
            "es" => "Lista los documentos con resumen almacenado.",
            "pt" => "Lista os documentos com resumo armazenado.",
            "de" => "Liste die Dokumente mit gespeicherter Zusammenfassung auf.",
            "it" => "Elenca i documenti con riassunto memorizzato.",
            _ => "Liste les documents avec un résumé stocké."
        };

    public static string BuildPromptAdminRescan(string? language)
        => NormalizeLanguage(language) switch
        {
            "en" => "Run a catalog rescan.",
            "es" => "Lanza un reescaneo del catálogo.",
            "pt" => "Lança um novo scan do catálogo.",
            "de" => "Starte einen erneuten Katalogscan.",
            "it" => "Avvia una nuova scansione del catalogo.",
            _ => "Lance un rescan du catalogue."
        };

    public static string BuildPromptAdminReindex(string? language, string documentRef)
        => NormalizeLanguage(language) switch
        {
            "en" => $"Reindex the document {documentRef}.",
            "es" => $"Reindexa el documento {documentRef}.",
            "pt" => $"Reindexa o documento {documentRef}.",
            "de" => $"Reindiziere das Dokument {documentRef}.",
            "it" => $"Reindicizza il documento {documentRef}.",
            _ => $"Relance l'ingestion du document {documentRef}."
        };


    public static string BuildPromptAdminReindexDisplay(string? language, string documentRef)
    {
        var compact = CompactDocumentLabel(documentRef, 64);
        return BuildPromptAdminReindex(language, compact);
    }

    private static string CompactDocumentLabel(string value, int maxLength)
    {
        var s = (value ?? string.Empty).Trim();
        if (s.Length <= maxLength || maxLength < 12)
            return s;

        var keepHead = Math.Max(8, (maxLength - 1) / 2);
        var keepTail = Math.Max(4, maxLength - keepHead - 1);
        return s[..keepHead] + "…" + s[^keepTail..];
    }

    private static Dictionary<string, string> Multi(string fr, string en, string es, string pt, string de, string it)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["fr"] = fr,
            ["en"] = en,
            ["es"] = es,
            ["pt"] = pt,
            ["de"] = de,
            ["it"] = it
        };
}
