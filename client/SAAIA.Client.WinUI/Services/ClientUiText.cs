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
        ["api.error.user_id_not_configured"] = Multi("userId non configure", "userId not configured", "userId no configurado", "userId nao configurado", "userId nicht konfiguriert", "userId non configurato"),
        ["api.error.retry_loop_unexpected_end"] = Multi("Fin inattendue de la boucle de renvoi.", "Unexpected send retry loop termination.", "Fin inesperado del bucle de reintentos.", "Fim inesperado do ciclo de reenvio.", "Unerwartetes Ende der Sendewiederholung.", "Terminazione imprevista del ciclo di reinvio."),
        ["api.error.invalid_create_session_response"] = Multi("Reponse de creation de session invalide", "Invalid create session response", "Respuesta invalida al crear la sesion", "Resposta invalida ao criar a sessao", "Ungueltige Antwort beim Erstellen der Sitzung", "Risposta non valida alla creazione della sessione"),
        ["api.error.message_id_required"] = Multi("messageId requis", "messageId is required", "messageId es obligatorio", "messageId e obrigatorio", "messageId ist erforderlich", "messageId e obbligatorio"),
        ["api.error.job_id_required"] = Multi("jobId requis.", "jobId is required.", "jobId es obligatorio.", "jobId e obrigatorio.", "jobId ist erforderlich.", "jobId e obbligatorio."),
        ["api.error.invalid_documents_catalog_response"] = Multi("Reponse invalide du catalogue documentaire", "Invalid documents catalog response", "Respuesta invalida del catalogo documental", "Resposta invalida do catalogo documental", "Ungueltige Antwort des Dokumentkatalogs", "Risposta non valida del catalogo documentale"),
        ["api.error.doc_id_required"] = Multi("docId requis", "docId is required", "docId es obligatorio", "docId e obrigatorio", "docId ist erforderlich", "docId e obbligatorio"),
        ["api.error.invalid_rag_search_response"] = Multi("Reponse invalide de recherche RAG", "Invalid rag search response", "Respuesta invalida de busqueda RAG", "Resposta invalida da pesquisa RAG", "Ungueltige RAG-Suchantwort", "Risposta non valida della ricerca RAG"),

        ["status.model_quarantined"] = Multi("Modele non disponible - contactez l'administrateur.", "Model unavailable - contact the administrator.", "Modelo no disponible - contacta con el administrador.", "Modelo indisponivel - contacte o administrador.", "Modell nicht verfuegbar - Administrator kontaktieren.", "Modello non disponibile - contatta l'amministratore."),
        ["status.provisioning.already_applied"] = Multi("Provisioning deja applique ({0}).", "Provisioning already applied ({0}).", "Provisioning ya aplicado ({0}).", "Provisioning ja aplicado ({0}).", "Provisioning bereits angewendet ({0}).", "Provisioning gia applicato ({0})."),
        ["status.provisioning.invalid_empty"] = Multi("Fichier de provisioning invalide (vide).", "Provisioning file invalid (empty).", "Archivo de provisioning no valido (vacio).", "Ficheiro de provisioning invalido (vazio).", "Provisioning-Datei ungueltig (leer).", "File di provisioning non valido (vuoto)."),
        ["status.provisioning.applied"] = Multi("Provisioning applique depuis {0}.", "Provisioning applied from {0}.", "Provisioning aplicado desde {0}.", "Provisioning aplicado a partir de {0}.", "Provisioning angewendet aus {0}.", "Provisioning applicato da {0}."),
        ["status.provisioning.apply_failed"] = Multi("Echec du provisioning : ", "Provisioning apply failed: ", "Error al aplicar provisioning: ", "Falha ao aplicar provisioning: ", "Provisioning-Anwendung fehlgeschlagen: ", "Applicazione provisioning non riuscita: "),

        ["header.chats"] = Multi("Discussions", "Chats", "Chats", "Chats", "Chats", "Chat"),
        ["header.help"] = Multi("Aide", "Help", "Ayuda", "Ajuda", "Hilfe", "Aiuto"),
        ["header.settings"] = Multi("Paramètres", "Settings", "Configuración", "Definições", "Einstellungen", "Impostazioni"),
        ["header.jobs"] = Multi("Jobs admin", "Admin jobs", "Trabajos admin", "Jobs admin", "Admin-Jobs", "Job admin"),
        ["header.runtime"] = Multi("Pilotage runtime", "Runtime ops", "Operaciones runtime", "Operações runtime", "Runtime-Betrieb", "Operazioni runtime"),
        ["panel.chats"] = Multi("Discussions", "Chats", "Chats", "Chats", "Chats", "Chat"),
        ["button.new"] = Multi("Nouveau", "New", "Nuevo", "Novo", "Neu", "Nuovo"),
        ["button.connect"] = Multi("Connecter", "Connect", "Conectar", "Ligar", "Verbinden", "Connetti"),
        ["button.setup"] = Multi("Configuration", "Setup", "Configuración", "Configuração", "Einrichtung", "Configurazione"),
        ["button.send"] = Multi("Envoyer", "Send", "Enviar", "Enviar", "Senden", "Invia"),
        ["button.cancel"] = Multi("Annuler", "Cancel", "Cancelar", "Cancelar", "Abbrechen", "Annulla"),
        ["button.jump_bottom"] = Multi("Aller en bas", "Jump to bottom", "Ir abajo", "Ir para baixo", "Nach unten", "Vai in basso"),
        ["button.search"] = Multi("Rechercher", "Search", "Buscar", "Pesquisar", "Suchen", "Cerca"),
        ["button.refresh"] = Multi("Actualiser", "Refresh", "Actualizar", "Atualizar", "Aktualisieren", "Aggiorna"),
        ["button.copy"] = Multi("Copier", "Copy", "Copiar", "Copiar", "Kopieren", "Copia"),
        ["button.local_llm"] = Multi("LLM local", "Local LLM", "LLM local", "LLM local", "Lokales LLM", "LLM locale"),
        ["button.runtime_diagnostics"] = Multi("Diagnostic runtime", "Runtime diagnostics", "Diagnostico runtime", "Diagnostico runtime", "Runtime-Diagnose", "Diagnostica runtime"),
        ["status.streaming"] = Multi("Generation en cours...", "Generating...", "Generando...", "A gerar...", "Generierung laeuft...", "Generazione in corso..."),
        ["panel.sources"] = Multi("Sources", "Sources", "Fuentes", "Fontes", "Quellen", "Fonti"),
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
        ["session.chat_name"] = Multi("Nom de la discussion", "Chat name", "Nombre del chat", "Nome do chat", "Chat-Name", "Nome chat"),
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
        ["startup.error.not_connected"] = Multi("Pas connecté au serveur", "Not connected to the server", "Sin conexión al servidor", "Não ligado ao servidor", "Nicht mit dem Server verbunden", "Non connesso al server"),
        ["startup.retry"] = Multi("Réessayer", "Retry", "Reintentar", "Tentar novamente", "Erneut versuchen", "Riprova"),
        ["startup.configure"] = Multi("Configurer", "Configure", "Configurar", "Configurar", "Konfigurieren", "Configura"),
        ["status.init_failed"] = Multi("Échec de l'initialisation : ", "Init failed: ", "Error de inicialización: ", "Falha na inicialização: ", "Initialisierung fehlgeschlagen: ", "Errore di inizializzazione: "),

        ["dialog.close"] = Multi("Fermer", "Close", "Cerrar", "Fechar", "Schließen", "Chiudi"),
        ["admin.session.title"] = Multi("Session administrateur", "Administrator session", "Sesión de administrador", "Sessão de administrador", "Administrator-Sitzung", "Sessione amministratore"),
        ["admin.session.subtitle"] = Multi("La clé admin reste uniquement dans cette session de l'application.", "The admin key stays only in this app session.", "La clave admin solo permanece en esta sesión de la aplicación.", "A chave admin fica apenas nesta sessão da aplicação.", "Der Admin-Schlüssel bleibt nur in dieser App-Sitzung erhalten.", "La chiave admin resta solo in questa sessione dell'app."),
        ["admin.session.placeholder.enter"] = Multi("Entrer la clé admin", "Enter the admin key", "Introducir la clave admin", "Introduzir a chave admin", "Admin-Schlüssel eingeben", "Inserisci la chiave admin"),
        ["admin.session.placeholder.update"] = Multi("Remplacer la clé admin de session", "Replace the session admin key", "Reemplazar la clave admin de sesión", "Substituir a chave admin da sessão", "Admin-Sitzungsschlüssel ersetzen", "Sostituisci la chiave admin di sessione"),
        ["admin.session.active"] = Multi("Une session admin est active pour cette ouverture de l'application.", "An admin session is active for this app launch.", "Hay una sesión admin activa para esta apertura de la aplicación.", "Existe uma sessão admin ativa nesta abertura da aplicação.", "Für diesen App-Start ist eine Admin-Sitzung aktiv.", "Per questa apertura dell'app è attiva una sessione admin."),
        ["admin.session.inactive"] = Multi("Aucune session admin active.", "No admin session is active.", "No hay ninguna sesión admin activa.", "Nenhuma sessão admin ativa.", "Keine Admin-Sitzung aktiv.", "Nessuna sessione admin attiva."),
        ["admin.session.connect"] = Multi("Activer", "Enable", "Activar", "Ativar", "Aktivieren", "Attiva"),
        ["admin.session.update"] = Multi("Mettre à jour", "Update", "Actualizar", "Atualizar", "Aktualisieren", "Aggiorna"),
        ["admin.session.disconnect"] = Multi("Désactiver", "Disable", "Desactivar", "Desativar", "Deaktivieren", "Disattiva"),
        ["admin.session.empty"] = Multi("Clé admin vide.", "Admin key is empty.", "La clave admin está vacía.", "A chave admin está vazia.", "Admin-Schlüssel ist leer.", "La chiave admin è vuota."),
        ["admin.session.validating"] = Multi("Vérification de la clé admin…", "Checking the admin key…", "Comprobando la clave admin…", "A verificar a chave admin…", "Admin-Schlüssel wird geprüft…", "Verifica della chiave admin…"),
        ["admin.session.invalid"] = Multi("Clé admin invalide ou refusée par le serveur.", "Admin key is invalid or was rejected by the server.", "La clave admin es inválida o fue rechazada por el servidor.", "A chave admin é inválida ou foi rejeitada pelo servidor.", "Der Admin-Schlüssel ist ungültig oder wurde vom Server abgelehnt.", "La chiave admin non è valida oppure è stata rifiutata dal server."),
        ["admin.session.validation_unavailable"] = Multi("Impossible de vérifier la clé admin pour le moment.", "Could not verify the admin key right now.", "No se pudo verificar la clave admin en este momento.", "Não foi possível verificar a chave admin neste momento.", "Der Admin-Schlüssel konnte momentan nicht überprüft werden.", "Impossibile verificare la chiave admin in questo momento."),

        ["admin.jobs.title"] = Multi("Centre des jobs", "Jobs center", "Centro de trabajos", "Centro de jobs", "Job-Center", "Centro job"),
        ["admin.runtime.title"] = Multi("Pilotage runtime", "Runtime operations", "Operaciones runtime", "Operações runtime", "Runtime-Betrieb", "Operazioni runtime"),
        ["admin.runtime.subtitle"] = Multi("Vue dédiée pour les backlogs A/B, les jobs actifs et le backfill d'offsets legacy.", "Dedicated view for A/B backlogs, active jobs, and legacy offset backfill.", "Vista dedicada para los backlog A/B, los trabajos activos y el backfill de offsets legacy.", "Vista dedicada para os backlog A/B, os jobs ativos e o backfill de offsets legacy.", "Dedizierte Ansicht für A/B-Backlogs, aktive Jobs und Legacy-Offset-Backfill.", "Vista dedicata per backlog A/B, job attivi e backfill degli offset legacy."),
        ["admin.runtime.refresh"] = Multi("Actualiser", "Refresh", "Actualizar", "Atualizar", "Aktualisieren", "Aggiorna"),
        ["admin.runtime.requalify"] = Multi("Requalifier", "Requalify", "Recalificar", "Requalificar", "Neu qualifizieren", "Ririqualifica"),
        ["admin.runtime.loading"] = Multi("Chargement du pilotage runtime…", "Loading runtime operations…", "Cargando las operaciones runtime…", "A carregar as operações runtime…", "Runtime-Betrieb wird geladen…", "Caricamento operazioni runtime…"),
        ["admin.runtime.load_failed"] = Multi("Impossible de charger le pilotage runtime.", "Could not load runtime operations.", "No se pudo cargar las operaciones runtime.", "Não foi possível carregar as operações runtime.", "Runtime-Betrieb konnte nicht geladen werden.", "Impossibile caricare le operazioni runtime."),
        ["admin.runtime.generated"] = Multi("Généré le {0}", "Generated {0}", "Generado el {0}", "Gerado em {0}", "Erzeugt am {0}", "Generato il {0}"),
        ["admin.runtime.metric.a_backlog"] = Multi("Backlog A", "A backlog", "Backlog A", "Backlog A", "A-Backlog", "Backlog A"),
        ["admin.runtime.metric.a_ready"] = Multi("A prêt", "A ready", "A listo", "A pronto", "A bereit", "A pronto"),
        ["admin.runtime.metric.a_offsets"] = Multi("Offsets legacy", "Legacy offsets", "Offsets legacy", "Offsets legacy", "Legacy-Offsets", "Offset legacy"),
        ["admin.runtime.metric.b_backlog"] = Multi("Backlog B", "B backlog", "Backlog B", "Backlog B", "B-Backlog", "Backlog B"),
        ["admin.runtime.metric.b_ready"] = Multi("B prêt", "B ready", "B listo", "B pronto", "B bereit", "B pronto"),
        ["admin.runtime.metric.b_active"] = Multi("Jobs B actifs", "Active B jobs", "Jobs B activos", "Jobs B ativos", "Aktive B-Jobs", "Job B attivi"),
        ["admin.runtime.section.capability_a"] = Multi("Capacité A", "Capability A", "Capacidad A", "Capacidade A", "Fähigkeit A", "Capacità A"),
        ["admin.runtime.section.capability_b"] = Multi("Capacité B", "Capability B", "Capacidad B", "Capacidade B", "Fähigkeit B", "Capacità B"),
        ["admin.runtime.field.status"] = Multi("Statut", "Status", "Estado", "Estado", "Status", "Stato"),
        ["admin.runtime.field.selected"] = Multi("Sélectionnée", "Selected", "Seleccionada", "Selecionada", "Ausgewählt", "Selezionata"),
        ["admin.runtime.field.recommendations"] = Multi("Recommandations", "Recommendations", "Recomendaciones", "Recomendações", "Empfehlungen", "Raccomandazioni"),
        ["admin.runtime.fact.backlog"] = Multi("Backlog", "Backlog", "Backlog", "Backlog", "Backlog", "Backlog"),
        ["admin.runtime.fact.ready"] = Multi("Prêts à lancer", "Ready to enqueue", "Listos para encolar", "Prontos para enfileirar", "Bereit zum Einreihen", "Pronti per l'accodamento"),
        ["admin.runtime.fact.blocked_active"] = Multi("Bloqués par job actif", "Blocked by active job", "Bloqueados por job activo", "Bloqueados por job ativo", "Durch aktiven Job blockiert", "Bloccati da job attivo"),
        ["admin.runtime.fact.blocked_policy"] = Multi("Bloqués par policy/cooldown", "Blocked by policy/cooldown", "Bloqueados por policy/cooldown", "Bloqueados por policy/cooldown", "Durch Policy/Cooldown blockiert", "Bloccati da policy/cooldown"),
        ["admin.runtime.fact.legacy_offsets"] = Multi("Backfill offsets legacy", "Legacy offset backfill", "Backfill de offsets legacy", "Backfill de offsets legacy", "Legacy-Offset-Backfill", "Backfill offset legacy"),
        ["admin.runtime.fact.active_jobs_campaigns"] = Multi("Jobs actifs: {0} | Campagnes: {1}", "Active jobs: {0} | Campaigns: {1}", "Jobs activos: {0} | Campañas: {1}", "Jobs ativos: {0} | Campanhas: {1}", "Aktive Jobs: {0} | Kampagnen: {1}", "Job attivi: {0} | Campagne: {1}"),
        ["admin.runtime.fact.latest_campaign"] = Multi("Dernière campagne: {0}%", "Latest campaign: {0}%", "Última campaña: {0}%", "Última campanha: {0}%", "Letzte Kampagne: {0}%", "Ultima campagna: {0}%"),
        ["admin.runtime.action.preview_offsets"] = Multi("Prévisualiser le backfill offsets", "Preview offset backfill", "Previsualizar backfill de offsets", "Previsualizar backfill de offsets", "Offset-Backfill vorschauen", "Anteprima backfill offset"),
        ["admin.runtime.action.open_jobs"] = Multi("Ouvrir les jobs admin", "Open admin jobs", "Abrir jobs admin", "Abrir jobs admin", "Admin-Jobs öffnen", "Apri job admin"),
        ["admin.runtime.action.preview_offsets.loading"] = Multi("Préparation du dry-run offsets...", "Preparing offset dry-run...", "Preparando el dry-run de offsets...", "A preparar o dry-run de offsets...", "Offset-Dry-Run wird vorbereitet...", "Preparazione dry-run offset..."),
        ["admin.runtime.action.preview_offsets.result"] = Multi("Dry-run offsets prêt : {0} document(s) planifié(s) sur {1} candidat(s), {2} ignoré(s). Exemples : {3}", "Offset dry-run ready: {0} planned across {1} candidate(s), {2} skipped. Examples: {3}", "Dry-run de offsets listo: {0} planificado(s) sobre {1} candidato(s), {2} omitido(s). Ejemplos: {3}", "Dry-run de offsets pronto: {0} planeado(s) em {1} candidato(s), {2} ignorado(s). Exemplos: {3}", "Offset-Dry-Run bereit: {0} geplant bei {1} Kandidat(en), {2} übersprungen. Beispiele: {3}", "Dry-run offset pronto: {0} pianificato/i su {1} candidato/i, {2} ignorato/i. Esempi: {3}"),
        ["admin.runtime.action.preview_offsets.result_short"] = Multi("Dry-run offsets prêt : {0} document(s) planifié(s) sur {1} candidat(s), {2} ignoré(s).", "Offset dry-run ready: {0} planned across {1} candidate(s), {2} skipped.", "Dry-run de offsets listo: {0} planificado(s) sobre {1} candidato(s), {2} omitido(s).", "Dry-run de offsets pronto: {0} planeado(s) em {1} candidato(s), {2} ignorado(s).", "Offset-Dry-Run bereit: {0} geplant bei {1} Kandidat(en), {2} übersprungen.", "Dry-run offset pronto: {0} pianificato/i su {1} candidato/i, {2} ignorato/i."),
        ["admin.runtime.action.preview_offsets.empty"] = Multi("Aucun backfill offsets à préparer. {0} candidat(s) inspecté(s), {1} ignoré(s).", "No offset backfill to prepare. {0} candidate(s) inspected, {1} skipped.", "No hay backfill de offsets para preparar. {0} candidato(s) inspeccionado(s), {1} omitido(s).", "Nenhum backfill de offsets para preparar. {0} candidato(s) inspecionado(s), {1} ignorado(s).", "Kein Offset-Backfill vorzubereiten. {0} Kandidat(en) geprüft, {1} übersprungen.", "Nessun backfill offset da preparare. {0} candidato/i controllato/i, {1} ignorato/i."),
        ["admin.runtime.action.preview_offsets.failed"] = Multi("Impossible de préparer le dry-run offsets.", "Could not prepare the offset dry-run.", "No se pudo preparar el dry-run de offsets.", "Não foi possível preparar o dry-run de offsets.", "Offset-Dry-Run konnte nicht vorbereitet werden.", "Impossibile preparare il dry-run offset."),
        ["admin.runtime.empty"] = Multi("Aucune donnée opérationnelle n'est disponible pour l'instant.", "No operational runtime data is available yet.", "Aún no hay datos operativos runtime disponibles.", "Ainda não há dados operacionais runtime disponíveis.", "Noch keine Runtime-Betriebsdaten verfügbar.", "Nessun dato operativo runtime disponibile al momento."),
        ["admin.runtime.action.requalify.loading"] = Multi("Requalification runtime en cours...", "Runtime requalification in progress...", "RecalificaciÃ³n runtime en curso...", "RequalificaÃ§Ã£o runtime em curso...", "Runtime-Neuqualifizierung lÃ¤uft...", "Ririqualificazione runtime in corso..."),
        ["admin.runtime.action.requalify.done"] = Multi("Requalification terminÃ©e : {0} capacitÃ©(s), {1} qualifiÃ©e(s), {2} sÃ©lectionnÃ©e(s), {3} warmup result(s).", "Requalification completed: {0} capability(ies), {1} qualified, {2} selected, {3} warmup result(s).", "RecalificaciÃ³n completada: {0} capacidad(es), {1} cualificada(s), {2} seleccionada(s), {3} resultado(s) de warmup.", "RequalificaÃ§Ã£o concluÃ­da: {0} capacidade(s), {1} qualificada(s), {2} selecionada(s), {3} resultado(s) de warmup.", "Neuqualifizierung abgeschlossen: {0} FÃ¤higkeit(en), {1} qualifiziert, {2} ausgewÃ¤hlt, {3} Warmup-Ergebnis(se).", "Ririqualificazione completata: {0} capacitÃ , {1} qualificata/e, {2} selezionata/e, {3} risultato/i warmup."),
        ["admin.runtime.action.requalify.failed"] = Multi("Impossible de lancer la requalification runtime.", "Could not start runtime requalification.", "No se pudo iniciar la recalificaciÃ³n runtime.", "NÃ£o foi possÃ­vel iniciar a requalificaÃ§Ã£o runtime.", "Runtime-Neuqualifizierung konnte nicht gestartet werden.", "Impossibile avviare la ririqualificazione runtime."),
        ["admin.runtime.action.open_a_kpis"] = Multi("Ouvrir KPI A", "Open A KPIs", "Abrir KPI A", "Abrir KPI A", "A-KPIs oeffnen", "Apri KPI A"),
        ["admin.runtime.kpi_a.title"] = Multi("KPI Capacite A", "Capability A KPIs", "KPI Capacidad A", "KPI Capacidade A", "KPI Fahigkeit A", "KPI Capacita A"),
        ["admin.runtime.kpi_a.subtitle"] = Multi("Vue dediee aux seuils, alertes et panneaux ops de la capacite A sans surcharger le pilotage runtime principal.", "Dedicated view for capability A thresholds, alerts, and ops panels without overloading the main runtime view.", "Vista dedicada a los umbrales, alertas y paneles ops de la capacidad A sin sobrecargar la vista runtime principal.", "Vista dedicada aos limiares, alertas e paineis ops da capacidade A sem sobrecarregar a vista runtime principal.", "Dedizierte Ansicht fur Schwellwerte, Warnungen und Ops-Panels von Fahigkeit A ohne die Hauptansicht zu uberladen.", "Vista dedicata a soglie, allerte e pannelli ops della capacita A senza appesantire la vista runtime principale."),
        ["admin.runtime.kpi_a.refresh"] = Multi("Actualiser les KPI A", "Refresh A KPIs", "Actualizar KPI A", "Atualizar KPI A", "A-KPIs aktualisieren", "Aggiorna KPI A"),
        ["admin.runtime.kpi_a.loading"] = Multi("Chargement des KPI A...", "Loading A KPIs...", "Cargando KPI A...", "A carregar KPI A...", "A-KPIs werden geladen...", "Caricamento KPI A..."),
        ["admin.runtime.kpi_a.load_failed"] = Multi("Impossible de charger les KPI A.", "Could not load A KPIs.", "No se pudo cargar los KPI A.", "Nao foi possivel carregar os KPI A.", "A-KPIs konnten nicht geladen werden.", "Impossibile caricare i KPI A."),
        ["admin.runtime.kpi_a.live_unavailable"] = Multi("Les KPI A sont disponibles, mais la lecture live A est temporairement indisponible.", "A KPIs are available, but the A live snapshot is temporarily unavailable.", "Los KPI A estan disponibles, pero la lectura live A no esta disponible temporalmente.", "Os KPI A estao disponiveis, mas a leitura live A esta temporariamente indisponivel.", "A-KPIs sind verfugbar, aber die A-Live-Ansicht ist vorubergehend nicht verfugbar.", "I KPI A sono disponibili, ma la lettura live A e temporaneamente non disponibile."),
        ["admin.runtime.kpi_a.state_ready"] = Multi("Environnement : {0} | fenetre d'observation : {1} min", "Environment: {0} | observation window: {1} min", "Entorno: {0} | ventana de observacion: {1} min", "Ambiente: {0} | janela de observacao: {1} min", "Umgebung: {0} | Beobachtungsfenster: {1} Min", "Ambiente: {0} | finestra di osservazione: {1} min"),
        ["admin.runtime.kpi_a.metric.operation_p95"] = Multi("Latence P95 A", "A P95 latency", "Latencia P95 A", "Latencia P95 A", "A-P95-Latenz", "Latenza P95 A"),
        ["admin.runtime.kpi_a.metric.skip_rate"] = Multi("Taux skip A", "A skip rate", "Tasa skip A", "Taxa skip A", "A-Skip-Rate", "Tasso skip A"),
        ["admin.runtime.kpi_a.metric.ready_rate"] = Multi("Taux pret A", "A ready rate", "Tasa lista A", "Taxa pronta A", "A-Bereit-Rate", "Tasso pronto A"),
        ["admin.runtime.kpi_a.metric.offset_backfill"] = Multi("Part backfill offsets", "Offset backfill share", "Parte backfill offsets", "Parte backfill offsets", "Offset-Backfill-Anteil", "Quota backfill offset"),
        ["admin.runtime.kpi_a.section.notes"] = Multi("Cadre ops", "Ops framing", "Marco ops", "Quadro ops", "Ops-Rahmen", "Quadro ops"),
        ["admin.runtime.kpi_a.section.current"] = Multi("Etat live A", "A live snapshot", "Estado live A", "Estado live A", "A-Live-Status", "Stato live A"),
        ["admin.runtime.kpi_a.metric.current_backlog"] = Multi("Backlog live", "Live backlog", "Backlog live", "Backlog live", "Live-Backlog", "Backlog live"),
        ["admin.runtime.kpi_a.metric.current_ready"] = Multi("Prets live", "Live ready", "Listos live", "Prontos live", "Live-bereit", "Pronti live"),
        ["admin.runtime.kpi_a.metric.current_ready_rate"] = Multi("Taux pret live", "Live ready rate", "Tasa lista live", "Taxa pronta live", "Live-Bereit-Rate", "Tasso pronto live"),
        ["admin.runtime.kpi_a.metric.current_offset_share"] = Multi("Part offsets live", "Live offset share", "Parte offsets live", "Parte offsets live", "Live-Offset-Anteil", "Quota offset live"),
        ["admin.runtime.kpi_a.section.metrics"] = Multi("Metriques suivies", "Tracked metrics", "Metricas seguidas", "Metricas acompanhadas", "Verfolgte Metriken", "Metriche seguite"),
        ["admin.runtime.kpi_a.section.compare"] = Multi("Lecture live vs cible", "Live vs target", "Lectura live vs objetivo", "Leitura live vs alvo", "Live vs Ziel", "Lettura live vs target"),
        ["admin.runtime.kpi_a.compare.ready_rate"] = Multi("Taux pret live : {0}% | cible : {1}% | {2}", "Live ready rate: {0}% | target: {1}% | {2}", "Tasa lista live: {0}% | objetivo: {1}% | {2}", "Taxa pronta live: {0}% | alvo: {1}% | {2}", "Live-Bereit-Rate: {0}% | Ziel: {1}% | {2}", "Tasso pronto live: {0}% | target: {1}% | {2}"),
        ["admin.runtime.kpi_a.compare.offset_share"] = Multi("Part offsets live : {0}% | cible max : {1}% | {2}", "Live offset share: {0}% | max target: {1}% | {2}", "Parte offsets live: {0}% | objetivo max: {1}% | {2}", "Parte offsets live: {0}% | alvo max: {1}% | {2}", "Live-Offset-Anteil: {0}% | Max-Ziel: {1}% | {2}", "Quota offset live: {0}% | target max: {1}% | {2}"),
        ["admin.runtime.kpi_a.compare.on_target"] = Multi("dans la cible", "on target", "en objetivo", "no alvo", "im Ziel", "in target"),
        ["admin.runtime.kpi_a.compare.watch"] = Multi("a surveiller", "watch", "a vigilar", "a vigiar", "beobachten", "da monitorare"),
        ["admin.runtime.kpi_a.section.alerts"] = Multi("Alertes et seuils", "Alerts and thresholds", "Alertas y umbrales", "Alertas e limiares", "Warnungen und Schwellen", "Allerte e soglie"),
        ["admin.runtime.kpi_a.section.panels"] = Multi("Panneaux recommandes", "Recommended panels", "Paneles recomendados", "Paineis recomendados", "Empfohlene Panels", "Pannelli raccomandati"),
        ["admin.runtime.kpi_a.empty_metrics"] = Multi("Aucune metrique definie.", "No metrics defined.", "No hay metricas definidas.", "Nenhuma metrica definida.", "Keine Metriken definiert.", "Nessuna metrica definita."),
        ["admin.runtime.kpi_a.empty_alerts"] = Multi("Aucune alerte definie.", "No alerts defined.", "No hay alertas definidas.", "Nenhum alerta definido.", "Keine Warnungen definiert.", "Nessun avviso definito."),
        ["admin.runtime.kpi_a.fact.instrument"] = Multi("Instrument : {0}", "Instrument: {0}", "Instrumento: {0}", "Instrumento: {0}", "Instrument: {0}", "Strumento: {0}"),
        ["admin.runtime.kpi_a.fact.aggregation"] = Multi("Agregation : {0}", "Aggregation: {0}", "Agregacion: {0}", "Agregacao: {0}", "Aggregation: {0}", "Aggregazione: {0}"),
        ["admin.runtime.kpi_a.fact.unit"] = Multi("Unite : {0}", "Unit: {0}", "Unidad: {0}", "Unidade: {0}", "Einheit: {0}", "Unita: {0}"),
        ["admin.runtime.kpi_a.fact.tags"] = Multi("Tags : {0}", "Tags: {0}", "Etiquetas: {0}", "Tags: {0}", "Tags: {0}", "Tag: {0}"),
        ["admin.jobs.subtitle"] = Multi("Suivi propre des réindexations et des autres traitements admin, séparé des discussions.", "Clean tracking for reindexing and other admin jobs, separate from chat.", "Seguimiento limpio de las reindexaciones y otros trabajos admin, separado del chat.", "Acompanhamento limpo das reindexações e de outros jobs admin, separado do chat.", "Saubere Verfolgung von Neuindexierungen und anderen Admin-Jobs, getrennt vom Chat.", "Monitoraggio pulito delle reindicizzazioni e degli altri job admin, separato dalla chat."),
        ["admin.jobs.page.subtitle"] = Multi("Historique admin clair, stable et séparé des discussions.", "Clear, stable admin history separate from chat.", "Historial admin claro y estable, separado del chat.", "Histórico admin claro e estável, separado do chat.", "Klarer, stabiler Admin-Verlauf, getrennt vom Chat.", "Cronologia admin chiara e stabile, separata dalla chat."),
        ["admin.jobs.page.summary"] = Multi("{0} visible(s) • {1} chargé(s) • {2} actif(s)", "{0} visible • {1} loaded • {2} active", "{0} visibles • {1} cargados • {2} activos", "{0} visíveis • {1} carregados • {2} ativos", "{0} sichtbar • {1} geladen • {2} aktiv", "{0} visibili • {1} caricati • {2} attivi"),
        ["admin.jobs.back"] = Multi("Retour", "Back", "Volver", "Voltar", "Zurück", "Indietro"),
        ["admin.runtime.action.open_b_quality"] = Multi("Ouvrir la revue qualitÃ© B", "Open B quality review", "Abrir revisiÃ³n de calidad B", "Abrir revisÃ£o de qualidade B", "B-QualitÃ¤tsprÃ¼fung Ã¶ffnen", "Apri revisione qualitÃ  B"),
        ["admin.runtime.quality.title"] = Multi("Revue qualitÃ© B", "B quality review", "RevisiÃ³n de calidad B", "RevisÃ£o de qualidade B", "B-QualitÃ¤tsprÃ¼fung", "Revisione qualitÃ  B"),
        ["admin.runtime.quality.subtitle"] = Multi("Vue dÃ©diÃ©e aux rÃ©sumÃ©s B sous le seuil de qualitÃ©, avec agrÃ©gats et recommandations de revue.", "Dedicated view for B summaries below the quality threshold, with aggregates and review recommendations.", "Vista dedicada a los resÃºmenes B por debajo del umbral de calidad, con agregados y recomendaciones.", "Vista dedicada aos resumos B abaixo do limiar de qualidade, com agregados e recomendaÃ§Ãµes.", "Dedizierte Ansicht fÃ¼r B-Zusammenfassungen unterhalb des QualitÃ¤tsschwellenwerts mit Aggregaten und Empfehlungen.", "Vista dedicata ai riepiloghi B sotto la soglia di qualitÃ , con aggregati e raccomandazioni."),
        ["admin.runtime.quality.refresh"] = Multi("Actualiser la revue", "Refresh review", "Actualizar revisiÃ³n", "Atualizar revisÃ£o", "PrÃ¼fung aktualisieren", "Aggiorna revisione"),
        ["admin.runtime.quality.loading"] = Multi("Chargement de la revue qualitÃ© Bâ€¦", "Loading B quality reviewâ€¦", "Cargando revisiÃ³n de calidad Bâ€¦", "A carregar a revisÃ£o de qualidade Bâ€¦", "B-QualitÃ¤tsprÃ¼fung wird geladenâ€¦", "Caricamento revisione qualitÃ  Bâ€¦"),
        ["admin.runtime.quality.load_failed"] = Multi("Impossible de charger la revue qualitÃ© B.", "Could not load the B quality review.", "No se pudo cargar la revisiÃ³n de calidad B.", "NÃ£o foi possÃ­vel carregar a revisÃ£o de qualidade B.", "B-QualitÃ¤tsprÃ¼fung konnte nicht geladen werden.", "Impossibile caricare la revisione qualitÃ  B."),
        ["admin.runtime.quality.empty"] = Multi("Aucun rÃ©sumÃ© B stockÃ© n'est actuellement sous le seuil de qualitÃ©.", "No stored B summary is currently below the quality threshold.", "NingÃºn resumen B almacenado estÃ¡ actualmente por debajo del umbral de calidad.", "Nenhum resumo B armazenado estÃ¡ atualmente abaixo do limiar de qualidade.", "Derzeit liegt keine gespeicherte B-Zusammenfassung unter dem QualitÃ¤tsschwellenwert.", "Nessun riepilogo B archiviato Ã¨ attualmente sotto la soglia di qualitÃ ."),
        ["admin.runtime.quality.state_ready"] = Multi("Environnement : {0} | seuil qualitÃ© : {1}", "Environment: {0} | quality threshold: {1}", "Entorno: {0} | umbral de calidad: {1}", "Ambiente: {0} | limiar de qualidade: {1}", "Umgebung: {0} | QualitÃ¤tsschwelle: {1}", "Ambiente: {0} | soglia qualitÃ : {1}"),
        ["admin.runtime.quality.metric.low_count"] = Multi("Sous seuil", "Below threshold", "Bajo el umbral", "Abaixo do limiar", "Unter Schwelle", "Sotto soglia"),
        ["admin.runtime.quality.metric.fallbacks"] = Multi("Fallbacks", "Fallbacks", "Fallbacks", "Fallbacks", "Fallbacks", "Fallback"),
        ["admin.runtime.quality.metric.runtime_unavailable"] = Multi("Runtime indisponible", "Runtime unavailable", "Runtime no disponible", "Runtime indisponÃ­vel", "Runtime nicht verfÃ¼gbar", "Runtime non disponibile"),
        ["admin.runtime.quality.metric.lowest_score"] = Multi("Pire score", "Lowest score", "Peor puntuaciÃ³n", "Pior pontuaÃ§Ã£o", "Niedrigster Wert", "Punteggio peggiore"),
        ["admin.runtime.quality.section.strategies"] = Multi("RÃ©partition par stratÃ©gie", "Breakdown by strategy", "Desglose por estrategia", "DistribuiÃ§Ã£o por estratÃ©gia", "Aufteilung nach Strategie", "Ripartizione per strategia"),
        ["admin.runtime.quality.section.statuses"] = Multi("RÃ©partition par statut runtime", "Breakdown by runtime status", "Desglose por estado runtime", "DistribuiÃ§Ã£o por estado runtime", "Aufteilung nach Runtime-Status", "Ripartizione per stato runtime"),
        ["admin.runtime.quality.empty_distribution"] = Multi("Aucune donnÃ©e", "No data", "Sin datos", "Sem dados", "Keine Daten", "Nessun dato"),
        ["admin.runtime.quality.fact.score"] = Multi("Score qualitÃ© : {0}", "Quality score: {0}", "PuntuaciÃ³n de calidad: {0}", "PontuaÃ§Ã£o de qualidade: {0}", "QualitÃ¤tswert: {0}", "Punteggio qualitÃ : {0}"),
        ["admin.runtime.quality.fact.severity"] = Multi("Severite : {0}", "Severity: {0}", "Severidad: {0}", "Severidade: {0}", "Schweregrad: {0}", "Gravita: {0}"),
        ["admin.runtime.quality.fact.recommended_action"] = Multi("Action recommandee : {0}", "Recommended action: {0}", "Accion recomendada: {0}", "Acao recomendada: {0}", "Empfohlene Aktion: {0}", "Azione consigliata: {0}"),
        ["admin.runtime.quality.fact.strategy"] = Multi("StratÃ©gie : {0}", "Strategy: {0}", "Estrategia: {0}", "EstratÃ©gia: {0}", "Strategie: {0}", "Strategia: {0}"),
        ["admin.runtime.quality.fact.runtime_status"] = Multi("Statut runtime : {0}", "Runtime status: {0}", "Estado runtime: {0}", "Estado runtime: {0}", "Runtime-Status: {0}", "Stato runtime: {0}"),
        ["admin.runtime.quality.fact.summary_length"] = Multi("Longueur du rÃ©sumÃ© : {0}", "Summary length: {0}", "Longitud del resumen: {0}", "Comprimento do resumo: {0}", "LÃ¤nge der Zusammenfassung: {0}", "Lunghezza del riepilogo: {0}"),
        ["admin.runtime.quality.fact.updated"] = Multi("Mis Ã  jour : {0}", "Updated: {0}", "Actualizado: {0}", "Atualizado: {0}", "Aktualisiert: {0}", "Aggiornato: {0}"),
        ["admin.runtime.quality.fact.fallback"] = Multi("Raison du fallback : {0}", "Fallback reason: {0}", "Motivo del fallback: {0}", "Motivo do fallback: {0}", "Fallback-Grund: {0}", "Motivo del fallback: {0}"),
        ["admin.runtime.quality.fact.coverage"] = Multi("Couverture sections / mots-clÃ©s : {0} / {1}", "Section / keyword coverage: {0} / {1}", "Cobertura secciones / palabras clave: {0} / {1}", "Cobertura secÃ§Ãµes / palavras-chave: {0} / {1}", "Abschnitts- / SchlÃ¼sselwortabdeckung: {0} / {1}", "Copertura sezioni / parole chiave: {0} / {1}"),
        ["admin.runtime.quality.action.regenerate"] = Multi("Relancer B", "Regenerate B", "Regenerar B", "Regenerar B", "B neu generieren", "Rigenera B"),
        ["admin.runtime.quality.regenerating"] = Multi("Relance de la generation B...", "Regenerating B summary...", "Regenerando resumen B...", "A regenerar resumo B...", "B-Zusammenfassung wird neu generiert...", "Rigenerazione riepilogo B..."),
        ["admin.runtime.quality.regenerate_done"] = Multi("Relance B planifiee : {0} job(s).", "B regeneration queued: {0} job(s).", "Regeneracion B planificada: {0} job(s).", "Regeneracao B planeada: {0} job(s).", "B-Neugenerierung geplant: {0} Job(s).", "Rigenerazione B pianificata: {0} job."),
        ["admin.runtime.quality.regenerate_done_detailed"] = Multi("Relance B planifiee : {0} job(s), {1} candidat(s), {2} ignore(s). Job : {3}.", "B regeneration queued: {0} job(s), {1} candidate(s), {2} skipped. Job: {3}.", "Regeneracion B planificada: {0} job(s), {1} candidato(s), {2} omitido(s). Job: {3}.", "Regeneracao B planeada: {0} job(s), {1} candidato(s), {2} ignorado(s). Job: {3}.", "B-Neugenerierung geplant: {0} Job(s), {1} Kandidat(en), {2} uebersprungen. Job: {3}.", "Rigenerazione B pianificata: {0} job, {1} candidato/i, {2} ignorato/i. Job: {3}."),
        ["admin.runtime.quality.regenerate_none"] = Multi("Aucun job B planifie : {0} candidat(s), {1} ignore(s).", "No B job queued: {0} candidate(s), {1} skipped.", "Ningun job B planificado: {0} candidato(s), {1} omitido(s).", "Nenhum job B planeado: {0} candidato(s), {1} ignorado(s).", "Kein B-Job geplant: {0} Kandidat(en), {1} uebersprungen.", "Nessun job B pianificato: {0} candidato/i, {1} ignorato/i."),
        ["admin.runtime.quality.regenerate_failed"] = Multi("Relance B impossible : ", "Could not regenerate B: ", "No se pudo regenerar B: ", "Nao foi possivel regenerar B: ", "B konnte nicht neu generiert werden: ", "Impossibile rigenerare B: "),
        ["admin.runtime.quality.regenerate_unavailable"] = Multi("Document indisponible pour relance B.", "Document unavailable for B regeneration.", "Documento no disponible para regeneracion B.", "Documento indisponivel para regeneracao B.", "Dokument fuer B-Neugenerierung nicht verfuegbar.", "Documento non disponibile per rigenerazione B."),
    ["admin.jobs.list.title"] = Multi("Jobs admin", "Admin jobs", "Jobs admin", "Jobs admin", "Admin-Jobs", "Job admin"),
        ["admin.runtime.action.open_a_jobs"] = Multi("Ouvrir les jobs A", "Open A jobs", "Abrir jobs A", "Abrir jobs A", "A-Jobs öffnen", "Apri job A"),
        ["admin.runtime.action.open_b_jobs"] = Multi("Ouvrir les jobs B", "Open B jobs", "Abrir jobs B", "Abrir jobs B", "B-Jobs öffnen", "Apri job B"),
        ["admin.jobs.detail.title"] = Multi("Détail du job", "Job details", "Detalle del trabajo", "Detalhes do job", "Jobdetails", "Dettaglio job"),
        ["admin.jobs.show_more"] = Multi("Afficher 50 de plus", "Show 50 more", "Mostrar 50 más", "Mostrar mais 50", "50 weitere anzeigen", "Mostra altri 50"),
        ["admin.jobs.detail.empty"] = Multi("Sélectionne un job dans la liste pour voir son détail ici.", "Select a job in the list to see its details here.", "Selecciona un trabajo en la lista para ver sus detalles aquí.", "Seleciona um job na lista para ver os detalhes aqui.", "Wähle einen Job in der Liste, um hier Details zu sehen.", "Seleziona un job nell'elenco per vedere qui i dettagli."),
        ["admin.jobs.detail.error"] = Multi("Erreur", "Error", "Error", "Erro", "Fehler", "Errore"),
        ["admin.jobs.refresh"] = Multi("Actualiser", "Refresh", "Actualizar", "Atualizar", "Aktualisieren", "Aggiorna"),
        ["admin.jobs.auto_refresh"] = Multi("Actualisation auto", "Auto refresh", "Actualización automática", "Atualização automática", "Automatische Aktualisierung", "Aggiornamento automatico"),
        ["admin.jobs.auto_on"] = Multi("Activée", "On", "Activada", "Ativada", "Ein", "Attiva"),
        ["admin.jobs.auto_off"] = Multi("Coupée", "Off", "Desactivada", "Desativada", "Aus", "Disattiva"),
        ["admin.jobs.search.placeholder"] = Multi("Rechercher par document, job ID ou erreur…", "Search by document, job ID, or error…", "Buscar por documento, ID o error…", "Pesquisar por documento, ID do job ou erro…", "Nach Dokument, Job-ID oder Fehler suchen…", "Cerca per documento, ID job o errore…"),
        ["admin.jobs.only_active"] = Multi("Actifs uniquement", "Active only", "Solo activos", "Só ativos", "Nur aktiv", "Solo attivi"),
        ["admin.jobs.filter.all"] = Multi("Tous", "All", "Todos", "Todos", "Alle", "Tutti"),
        ["admin.jobs.filter.ingestion"] = Multi("Indexations", "Indexing", "Indexaciones", "Indexações", "Indexierungen", "Indicizzazioni"),
        ["admin.jobs.filter.summary"] = Multi("Résumés", "Summaries", "Resúmenes", "Resumos", "Zusammenfassungen", "Riepiloghi"),
        ["admin.jobs.filter.date.finished"] = Multi("Terminé", "Finished", "Terminados", "Concluídos", "Beendet", "Terminato"),
        ["admin.jobs.filter.date.started"] = Multi("Démarré", "Started", "Iniciados", "Iniciado", "Gestartet", "Avviato"),
        ["admin.jobs.filter.date.created"] = Multi("Créé", "Created", "Creados", "Criados", "Erstellt", "Creato"),
        ["admin.jobs.filter.range.all"] = Multi("Toutes dates", "All dates", "Todas las fechas", "Todas as datas", "Alle Daten", "Tutte le date"),
        ["admin.jobs.filter.range.today"] = Multi("Aujourd'hui", "Today", "Hoy", "Hoje", "Heute", "Oggi"),
        ["admin.jobs.filter.range.custom"] = Multi("Période", "Range", "Periodo", "Período", "Zeitraum", "Periodo"),
        ["admin.jobs.filter.from"] = Multi("Du", "From", "Desde", "De", "Von", "Da"),
        ["admin.jobs.filter.to"] = Multi("Au", "To", "Hasta", "Até", "Bis", "A"),
        ["admin.jobs.filter.sort.newest"] = Multi("Récent d'abord", "Newest first", "Más recientes", "Mais recentes", "Neueste zuerst", "Più recenti"),
        ["admin.jobs.filter.sort.oldest"] = Multi("Ancien d'abord", "Oldest first", "Más antiguos", "Mais antigos", "Älteste zuerst", "Più vecchi"),
        ["admin.jobs.status_filter.all"] = Multi("Tous les statuts", "All statuses", "Todos los estados", "Todos os estados", "Alle Status", "Tutti gli stati"),
        ["admin.jobs.status_filter.active"] = Multi("Actifs", "Active", "Activos", "Ativos", "Aktiv", "Attivi"),
        ["admin.jobs.status_filter.paused"] = Multi("En pause", "Paused", "En pausa", "Em pausa", "Pausiert", "In pausa"),
        ["admin.jobs.loading"] = Multi("Chargement des jobs admin…", "Loading admin jobs…", "Cargando trabajos admin…", "A carregar jobs admin…", "Admin-Jobs werden geladen…", "Caricamento job admin…"),
        ["admin.jobs.empty"] = Multi("Aucun job à afficher avec les filtres actuels.", "No jobs to display with the current filters.", "No hay trabajos para mostrar con los filtros actuales.", "Nenhum job para mostrar com os filtros atuais.", "Mit den aktuellen Filtern sind keine Jobs anzuzeigen.", "Nessun job da mostrare con i filtri attuali."),
        ["admin.jobs.copy_id"] = Multi("Copier l'ID", "Copy ID", "Copiar ID", "Copiar ID", "ID kopieren", "Copia ID"),
        ["admin.jobs.id_copied"] = Multi("ID du job copié.", "Job ID copied.", "ID del trabajo copiado.", "ID do job copiado.", "Job-ID kopiert.", "ID job copiato."),
        ["admin.jobs.cancel"] = Multi("Annuler le job", "Cancel job", "Cancelar trabajo", "Cancelar job", "Job abbrechen", "Annulla job"),
        ["admin.jobs.pause"] = Multi("Mettre en pause", "Pause", "Pausar", "Pausar", "Pausieren", "Metti in pausa"),
        ["admin.jobs.resume"] = Multi("Reprendre l'ingestion", "Resume ingestion", "Reanudar ingestión", "Retomar ingestão", "Ingestion fortsetzen", "Riprendi ingestione"),
        ["admin.jobs.cancel_done"] = Multi("Annulation confirmée.", "Cancellation confirmed.", "Cancelación confirmada.", "Cancelamento confirmado.", "Abbruch bestätigt.", "Annullamento confermato."),
        ["admin.jobs.pause_done"] = Multi("Pause confirmée.", "Pause confirmed.", "Pausa confirmada.", "Pausa confirmada.", "Pause bestätigt.", "Pausa confermata."),
        ["admin.jobs.cancel_requested"] = Multi("Annulation demandée. Le job doit encore converger vers l'état annulé.", "Cancellation requested. The job still needs to converge to the canceled state.", "Cancelación solicitada. El trabajo aún debe converger al estado cancelado.", "Cancelamento solicitado. O job ainda precisa convergir para o estado cancelado.", "Abbruch angefordert. Der Job muss noch in den Status abgebrochen übergehen.", "Annullamento richiesto. Il job deve ancora convergere nello stato annullato."),
        ["admin.jobs.resume_done"] = Multi("Ingestion relancée.", "Ingestion resumed.", "Ingestión reanudada.", "Ingestão retomada.", "Ingestion fortgesetzt.", "Ingestione ripresa."),
        ["admin.jobs.resume_not_paused"] = Multi("Ce document n'est pas en pause.", "This document is not paused.", "Este documento no está en pausa.", "Este documento não está em pausa.", "Dieses Dokument ist nicht pausiert.", "Questo documento non è in pausa."),
        ["admin.jobs.resume_missing_file"] = Multi("Impossible de reprendre : fichier absent du répertoire.", "Cannot resume: file is missing from the folder.", "No se puede reanudar: falta el archivo en la carpeta.", "Não é possível retomar: ficheiro ausente na pasta.", "Fortsetzen nicht möglich: Datei fehlt im Ordner.", "Impossibile riprendere: file mancante nella cartella."),
        ["admin.jobs.resume_failed"] = Multi("Échec de la reprise : ", "Could not resume ingestion: ", "No se pudo reanudar la ingestión: ", "Não foi possível retomar a ingestão: ", "Fortsetzen fehlgeschlagen: ", "Ripresa ingestione non riuscita: "),
        ["admin.jobs.cancel_already_finished"] = Multi("Le job était déjà terminé au moment de la demande d'annulation.", "The job had already finished when the cancellation was requested.", "El trabajo ya había finalizado cuando se solicitó la cancelación.", "O job já tinha terminado quando o cancelamento foi solicitado.", "Der Job war bereits beendet, als der Abbruch angefordert wurde.", "Il job era già terminato quando è stato richiesto l'annullamento."),
        ["admin.jobs.cancel_nothing"] = Multi("Aucun job actif n'a pu être annulé pour cette demande.", "No active job could be canceled for this request.", "No se pudo cancelar ningún trabajo activo para esta solicitud.", "Nenhum job ativo pôde ser cancelado para este pedido.", "Für diese Anfrage konnte kein aktiver Job abgebrochen werden.", "Nessun job attivo è stato annullato per questa richiesta."),
        ["admin.jobs.refresh_failed"] = Multi("Échec du chargement des jobs admin : ", "Could not load admin jobs: ", "No se pudieron cargar los trabajos admin: ", "Não foi possível carregar os jobs admin: ", "Admin-Jobs konnten nicht geladen werden: ", "Impossibile caricare i job admin: "),
        ["admin.jobs.no_admin"] = Multi("Active d'abord la session admin pour ouvrir les jobs.", "Enable the admin session first to open jobs.", "Activa primero la sesión admin para abrir los trabajos.", "Ativa primeiro a sessão admin para abrir os jobs.", "Aktiviere zuerst die Admin-Sitzung, um die Jobs zu öffnen.", "Attiva prima la sessione admin per aprire i job."),
        ["admin.jobs.detached.status"] = Multi("Suivi déplacé dans le centre des jobs admin.", "Tracking moved to the admin jobs center.", "Seguimiento movido al centro de trabajos admin.", "Acompanhamento movido para o centro de jobs admin.", "Verfolgung in das Admin-Job-Center verschoben.", "Monitoraggio spostato nel centro job admin."),
        ["admin.jobs.detached.launch"] = Multi("Réindexation lancée pour {0}. Job ID : {1}. Ouvre le centre des jobs admin pour suivre l'avancement.", "Reindexing started for {0}. Job ID: {1}. Open the admin jobs center to follow the progress.", "Reindexación iniciada para {0}. ID del trabajo: {1}. Abre el centro de trabajos admin para seguir el avance.", "Reindexação iniciada para {0}. ID do job: {1}. Abre o centro de jobs admin para acompanhar o progresso.", "Neuindexierung für {0} gestartet. Job-ID: {1}. Öffne das Admin-Job-Center, um den Fortschritt zu verfolgen.", "Reindicizzazione avviata per {0}. ID job: {1}. Apri il centro job admin per seguire l'avanzamento."),
        ["admin.jobs.summary"] = Multi("{0} job(s) visibles • {1} actif(s) • {2} chargé(s)", "{0} visible job(s) • {1} active • {2} loaded", "{0} trabajo(s) visibles • {1} activos • {2} cargados", "{0} job(s) visíveis • {1} ativos • {2} carregados", "{0} sichtbare Job(s) • {1} aktiv • {2} geladen", "{0} job visibili • {1} attivi • {2} caricati"),
        ["admin.jobs.visible_summary"] = Multi("{0} job(s) correspondent aux filtres sur {1} chargés.", "{0} job(s) match the filters out of {1} loaded.", "{0} trabajo(s) coinciden con los filtros de {1} cargados.", "{0} job(s) correspondem aos filtros entre {1} carregados.", "{0} Job(s) entsprechen den Filtern von {1} geladenen.", "{0} job corrispondono ai filtri su {1} caricati."),
        ["admin.jobs.metric.active"] = Multi("Actifs", "Active", "Activos", "Ativos", "Aktiv", "Attivi"),
        ["admin.jobs.metric.queued"] = Multi("En attente", "Queued", "En espera", "Em espera", "Wartend", "In coda"),
        ["admin.jobs.metric.running"] = Multi("En cours", "Running", "En curso", "Em curso", "Laufend", "In corso"),
        ["admin.jobs.metric.paused"] = Multi("En pause", "Paused", "En pausa", "Em pausa", "Pausiert", "In pausa"),
        ["admin.jobs.metric.failed"] = Multi("Échecs", "Failed", "Fallidos", "Falhados", "Fehler", "Falliti"),
        ["admin.jobs.metric.canceled"] = Multi("Annulés", "Canceled", "Cancelados", "Cancelados", "Abgebrochen", "Annullati"),
        ["admin.jobs.metric.done"] = Multi("Terminés", "Done", "Terminados", "Concluídos", "Abgeschlossen", "Completati"),
        ["admin.jobs.group.active"] = Multi("En cours", "Running", "En curso", "Em curso", "Laufend", "In corso"),
        ["admin.jobs.group.paused"] = Multi("En pause", "Paused", "En pausa", "Em pausa", "Pausiert", "In pausa"),
        ["admin.jobs.group.failed"] = Multi("Échecs", "Failed", "Fallidos", "Falhados", "Fehler", "Falliti"),
        ["admin.jobs.group.canceled"] = Multi("Annulés", "Canceled", "Cancelados", "Cancelados", "Abgebrochen", "Annullati"),
        ["admin.jobs.group.history"] = Multi("Terminés", "Done", "Terminados", "Concluídos", "Abgeschlossen", "Completati"),
        ["admin.jobs.group.title"] = Multi("{0} ({1})", "{0} ({1})", "{0} ({1})", "{0} ({1})", "{0} ({1})", "{0} ({1})"),
        ["admin.jobs.status.queued"] = Multi("en attente", "queued", "en espera", "em espera", "wartend", "in coda"),
        ["admin.jobs.status.running"] = Multi("en cours", "running", "en curso", "em curso", "laufend", "in corso"),
        ["admin.jobs.status.cancel_requested"] = Multi("annulation demandée", "cancel requested", "cancelación solicitada", "cancelamento solicitado", "Abbruch angefordert", "annullamento richiesto"),
        ["admin.jobs.status.paused"] = Multi("en pause", "paused", "en pausa", "em pausa", "pausiert", "in pausa"),
        ["admin.jobs.status.done"] = Multi("terminé", "done", "terminado", "concluído", "abgeschlossen", "completato"),
        ["admin.jobs.status.failed"] = Multi("échec", "failed", "fallido", "falhado", "fehlgeschlagen", "fallito"),
        ["admin.jobs.status.canceled"] = Multi("annulé", "canceled", "cancelado", "cancelado", "abgebrochen", "annullato"),
        ["admin.jobs.date.created"] = Multi("créé {0}", "created {0}", "creado {0}", "criado {0}", "erstellt {0}", "creato {0}"),
        ["admin.jobs.date.started"] = Multi("démarré {0}", "started {0}", "iniciado {0}", "iniciado {0}", "gestartet {0}", "avviato {0}"),
        ["admin.jobs.date.finished"] = Multi("terminé {0}", "finished {0}", "terminado {0}", "concluído {0}", "beendet {0}", "terminato {0}"),
        ["admin.jobs.date.none"] = Multi("Horodatage indisponible", "Timestamp unavailable", "Marca temporal no disponible", "Carimbo temporal indisponível", "Zeitstempel nicht verfügbar", "Timestamp non disponibile"),
        ["admin.jobs.job_id_short"] = Multi("Job {0}", "Job {0}", "Job {0}", "Job {0}", "Job {0}", "Job {0}"),
        ["admin.jobs.hide_details"] = Multi("Masquer", "Hide", "Ocultar", "Ocultar", "Ausblenden", "Nascondi"),
        ["admin.jobs.select"] = Multi("Sélectionner pour suppression", "Select for deletion", "Seleccionar para eliminar", "Selecionar para apagar", "Zum Löschen auswählen", "Seleziona per eliminare"),
        ["admin.jobs.selection.none"] = Multi("Aucun historique sélectionné.", "No history selected.", "Ningún historial seleccionado.", "Nenhum histórico selecionado.", "Kein Verlauf ausgewählt.", "Nessuna cronologia selezionata."),
        ["admin.jobs.selection.count"] = Multi("{0} job(s) sélectionné(s)", "{0} job(s) selected", "{0} trabajo(s) seleccionados", "{0} job(s) selecionados", "{0} Job(s) ausgewählt", "{0} job selezionati"),
        ["admin.jobs.delete_selection"] = Multi("Supprimer la sélection", "Delete selection", "Eliminar selección", "Apagar seleção", "Auswahl löschen", "Elimina selezione"),
        ["admin.jobs.purge"] = Multi("Purger l'historique", "Purge history", "Purgar historial", "Purgar histórico", "Verlauf bereinigen", "Pulisci cronologia"),
        ["admin.jobs.purge.visible_done"] = Multi("Supprimer les terminés visibles", "Delete visible completed jobs", "Eliminar los terminados visibles", "Apagar os concluídos visíveis", "Sichtbar abgeschlossene löschen", "Elimina i completati visibili"),
        ["admin.jobs.purge.visible_failed"] = Multi("Supprimer les échecs visibles", "Delete visible failed jobs", "Eliminar los fallidos visibles", "Apagar os falhados visíveis", "Sichtbar fehlgeschlagene löschen", "Elimina i falliti visibili"),
        ["admin.jobs.purge.visible_canceled"] = Multi("Supprimer les annulés visibles", "Delete visible canceled jobs", "Eliminar los cancelados visibles", "Apagar os cancelados visíveis", "Sichtbar abgebrochene löschen", "Elimina gli annullati visibili"),
        ["admin.jobs.purge.visible_all"] = Multi("Supprimer tout l'historique visible", "Delete all visible history", "Eliminar todo el historial visible", "Apagar todo o histórico visível", "Gesamten sichtbaren Verlauf löschen", "Elimina tutta la cronologia visibile"),
        ["admin.jobs.purge.all_history"] = Multi("Purger tout l'historique", "Purge all history", "Purgar todo el historial", "Purgar todo o histórico", "Gesamten Verlauf bereinigen", "Pulisci tutta la cronologia"),
        ["admin.jobs.error.timeout"] = Multi("Délai d'attente dépassé pendant l'ingestion", "Timeout while processing ingestion", "Tiempo de espera agotado durante la ingestión", "Tempo limite excedido durante a ingestão", "Zeitüberschreitung während der Ingestion", "Timeout durante l'ingestione"),
        ["admin.jobs.error.canceled_by_admin"] = Multi("Annulé par l'administrateur", "Canceled by administrator", "Cancelado por el administrador", "Cancelado pelo administrador", "Vom Administrator abgebrochen", "Annullato dall'amministratore"),
        ["admin.jobs.error.canceled_after_commit"] = Multi("Annulation prise en compte en fin de traitement", "Cancellation applied at commit time", "Cancelación aplicada al finalizar el tratamiento", "Cancelamento aplicado no fim do processamento", "Abbruch beim Commit berücksichtigt", "Annullamento applicato in fase di commit"),
        ["admin.jobs.error.superseded"] = Multi("Job remplacé par une version plus récente", "Job superseded by a newer version", "Trabajo sustituido por una versión más reciente", "Job substituído por uma versão mais recente", "Job durch eine neuere Version ersetzt", "Job sostituito da una versione più recente"),
        ["admin.jobs.error.coalesced_by_missing"] = Multi("Annulé car la source a été marquée comme manquante", "Canceled because the source was marked missing", "Cancelado porque la fuente se marcó como ausente", "Cancelado porque a fonte foi marcada como ausente", "Abgebrochen, weil die Quelle als fehlend markiert wurde", "Annullato perché la sorgente è stata segnata come mancante"),
        ["admin.jobs.error.coalesced_by_upsert"] = Multi("Annulé car un nouveau job d'indexation a pris le relais", "Canceled because a newer indexing job superseded it", "Cancelado porque un trabajo de indexación más reciente lo sustituyó", "Cancelado porque um job de indexação mais recente o substituiu", "Abgebrochen, weil ein neuerer Indexierungsjob ihn ersetzt hat", "Annullato perché un job di indicizzazione più recente lo ha sostituito"),
        ["admin.jobs.error.file_missing"] = Multi("Fichier introuvable au moment du traitement", "File missing at processing time", "Archivo no encontrado en el momento del tratamiento", "Ficheiro ausente no momento do processamento", "Datei zum Verarbeitungszeitpunkt nicht gefunden", "File mancante al momento dell'elaborazione"),
        ["admin.jobs.error.stale_running"] = Multi("Le job a été repris après une exécution bloquée", "Job was recovered after a stale running state", "El trabajo se recuperó tras un estado de ejecución bloqueado", "O job foi recuperado após um estado de execução bloqueado", "Job wurde nach einem hängenden Lauf wiederhergestellt", "Il job è stato recuperato dopo uno stato in esecuzione bloccato"),
        ["admin.jobs.error.source_removed_during_ingestion"] = Multi("Source supprimée pendant l'ingestion", "Source removed during ingestion", "Fuente eliminada durante la ingestión", "Fonte removida durante a ingestão", "Quelle während der Ingestion entfernt", "Sorgente rimossa durante l'ingestione"),
        ["admin.jobs.delete_done"] = Multi("{0} job(s) supprimé(s) de l'historique.", "{0} job(s) removed from history.", "{0} trabajo(s) eliminados del historial.", "{0} job(s) removidos do histórico.", "{0} Job(s) aus dem Verlauf gelöscht.", "{0} job rimossi dalla cronologia."),
        ["admin.jobs.delete_failed"] = Multi("Échec de la suppression de l'historique : ", "Could not delete history: ", "No se pudo eliminar el historial: ", "Não foi possível apagar o histórico: ", "Verlauf konnte nicht gelöscht werden: ", "Impossibile eliminare la cronologia: "),
        ["admin.jobs.delete_nothing"] = Multi("Aucun job terminal à supprimer avec les filtres actuels.", "No terminal job to delete with the current filters.", "No hay trabajos terminales que eliminar con los filtros actuales.", "Nenhum job terminal para apagar com os filtros atuais.", "Mit den aktuellen Filtern gibt es keine terminalen Jobs zum Löschen.", "Nessun job terminale da eliminare con i filtri attuali."),
        ["admin.jobs.delete_selection_none"] = Multi("La sélection ne contient plus de job terminal supprimable.", "The selection no longer contains any deletable terminal job.", "La selección ya no contiene ningún trabajo terminal eliminable.", "A seleção já não contém nenhum job terminal eliminável.", "Die Auswahl enthält keinen löschbaren terminalen Job mehr.", "La selezione non contiene più alcun job terminale eliminabile."),
        ["admin.jobs.refresh_failed_soft"] = Multi("dernière actualisation impossible", "last refresh failed", "última actualización fallida", "última atualização falhou", "letzte Aktualisierung fehlgeschlagen", "ultimo aggiornamento non riuscito"),
        ["admin.jobs.id_copy_unavailable"] = Multi("Copie indisponible sur ce poste.", "Copy unavailable on this device.", "Copia no disponible en este dispositivo.", "Cópia indisponível neste posto.", "Kopieren auf diesem Gerät nicht verfügbar.", "Copia non disponibile su questo dispositivo."),
        ["admin.jobs.card_error"] = Multi("Impossible d'afficher le job {0}.", "Could not render job {0}.", "No se pudo mostrar el trabajo {0}.", "Não foi possível mostrar o job {0}.", "Job {0} konnte nicht dargestellt werden.", "Impossibile mostrare il job {0}."),
        ["admin.jobs.details.job_id"] = Multi("Job ID", "Job ID", "ID del trabajo", "ID do job", "Job-ID", "ID job"),
        ["admin.jobs.details.runtime_capability"] = Multi("Capacite runtime", "Runtime capability", "Capacidad runtime", "Capacidade runtime", "Runtime-Faehigkeit", "Capacita runtime"),
        ["admin.jobs.details.execution_mode"] = Multi("Mode execution", "Execution mode", "Modo de ejecucion", "Modo de execucao", "Ausfuehrungsmodus", "Modalita esecuzione"),
        ["admin.jobs.details.type"] = Multi("Domaine", "Family", "Familia", "Família", "Bereich", "Ambito"),
        ["admin.jobs.details.job_type"] = Multi("Action", "Action", "Acción", "Ação", "Aktion", "Azione"),
        ["admin.jobs.details.status"] = Multi("Statut", "Status", "Estado", "Estado", "Status", "Stato"),
        ["admin.jobs.details.doc_id"] = Multi("Document ID", "Document ID", "ID del documento", "ID do documento", "Dokument-ID", "ID documento"),
        ["admin.jobs.details.doc_path"] = Multi("Chemin", "Path", "Ruta", "Caminho", "Pfad", "Percorso"),
        ["admin.jobs.details.phase"] = Multi("Étape", "Step", "Etapa", "Etapa", "Schritt", "Fase"),
        ["admin.jobs.details.progress"] = Multi("Progression", "Progress", "Progreso", "Progresso", "Fortschritt", "Avanzamento"),
        ["admin.jobs.details.cancel_requested_flag"] = Multi("Annulation demandée", "Cancel requested", "Cancelación solicitada", "Cancelamento solicitado", "Abbruch angefordert", "Annullamento richiesto"),
        ["admin.jobs.details.enqueue_source"] = Multi("Origine du lancement", "Launch source", "Origen del lanzamiento", "Origem do arranque", "Startquelle", "Origine del lancio"),
        ["admin.jobs.details.doc_status"] = Multi("État du document", "Document state", "Estado del documento", "Estado do documento", "Dokumentstatus", "Stato del documento"),
        ["admin.jobs.details.doc_versions"] = Multi("Versions du document", "Document versions", "Versiones del documento", "Versões do documento", "Dokumentversionen", "Versioni del documento"),
        ["admin.jobs.details.auto_pause"] = Multi("Réindexation automatique", "Automatic reindexing", "Reindexación automática", "Reindexação automática", "Automatische Neuindizierung", "Reindicizzazione automatica"),
        ["admin.jobs.auto_pause.on"] = Multi("en pause", "paused", "en pausa", "em pausa", "pausiert", "in pausa"),
        ["admin.jobs.auto_pause.off"] = Multi("active", "active", "activa", "ativa", "aktiv", "attiva"),
        ["admin.jobs.auto_pause.on_reason"] = Multi("en pause ({0})", "paused ({0})", "en pausa ({0})", "em pausa ({0})", "pausiert ({0})", "in pausa ({0})"),
        ["admin.jobs.details.created"] = Multi("Création", "Created", "Creación", "Criação", "Erstellt", "Creato"),
        ["admin.jobs.details.started"] = Multi("Démarrage", "Started", "Inicio", "Início", "Gestartet", "Avviato"),
        ["admin.jobs.details.finished"] = Multi("Fin", "Finished", "Fin", "Fim", "Ende", "Fine"),
        ["admin.jobs.details.doc_versions.value"] = Multi("ingestion {0} • index {1}", "ingestion {0} • index {1}", "ingesta {0} • índice {1}", "ingestão {0} • índice {1}", "Ingestion {0} • Index {1}", "ingestione {0} • indice {1}"),
        ["admin.jobs.card.document_state"] = Multi("Document", "Document", "Documento", "Documento", "Dokument", "Documento"),
        ["admin.jobs.card.document_versions"] = Multi("ingestion {0} / indexée {1}", "ingestion {0} / indexed {1}", "ingesta {0} / indexada {1}", "ingestão {0} / indexada {1}", "Ingestion {0} / indexiert {1}", "ingestione {0} / indicizzata {1}"),
        ["admin.jobs.value.yes"] = Multi("Oui", "Yes", "Sí", "Sim", "Ja", "Sì"),
        ["admin.jobs.value.no"] = Multi("Non", "No", "No", "Não", "Nein", "No"),
        ["admin.jobs.type.ingestion"] = Multi("Ingestion", "Ingestion", "Ingesta", "Ingestão", "Ingestion", "Ingestione"),
        ["admin.jobs.type.summary"] = Multi("Résumé admin", "Admin summary", "Resumen admin", "Resumo admin", "Admin-Zusammenfassung", "Riepilogo admin"),
        ["admin.jobs.job_type.upsert"] = Multi("Mise à jour de l'index", "Index update", "Actualización del índice", "Atualização do índice", "Index-Aktualisierung", "Aggiornamento dell'indice"),
        ["admin.jobs.job_type.delete"] = Multi("Suppression de l'index", "Index deletion", "Eliminación del índice", "Remoção do índice", "Index-Löschung", "Eliminazione dell'indice"),
        ["admin.jobs.job_type.summary"] = Multi("Génération du résumé", "Summary generation", "Generación del resumen", "Geração do resumo", "Zusammenfassung erzeugen", "Generazione del riepilogo"),
        ["admin.jobs.phase.preparing"] = Multi("Préparation", "Preparing", "Preparación", "Preparação", "Vorbereitung", "Preparazione"),
        ["admin.jobs.phase.extracting"] = Multi("Extraction", "Extracting", "Extracción", "Extração", "Extraktion", "Estrazione"),
        ["admin.jobs.phase.chunking"] = Multi("Découpage", "Chunking", "Segmentación", "Segmentação", "Aufteilung", "Suddivisione"),
        ["admin.jobs.phase.embedding"] = Multi("Embeddings", "Embeddings", "Embeddings", "Embeddings", "Embeddings", "Embeddings"),
        ["admin.jobs.phase.upserting"] = Multi("Écriture index", "Index write", "Escritura del índice", "Escrita do índice", "Index schreiben", "Scrittura indice"),
        ["admin.jobs.phase.deleting"] = Multi("Suppression", "Deleting", "Eliminación", "Remoção", "Löschen", "Eliminazione"),
        ["admin.jobs.phase.resuming"] = Multi("Reprise", "Resuming", "Reanudación", "Retoma", "Fortsetzen", "Ripresa"),
        ["admin.jobs.phase.finalizing"] = Multi("Finalisation", "Finalizing", "Finalización", "Finalização", "Finalisierung", "Finalizzazione"),
        ["admin.jobs.phase.completed"] = Multi("Terminé", "Completed", "Terminado", "Concluído", "Abgeschlossen", "Completato"),
        ["admin.jobs.enqueue_source.admin"] = Multi("Lancé manuellement par un admin", "Started manually by an admin", "Lanzado manualmente por un admin", "Iniciado manualmente por um admin", "Manuell von einem Admin gestartet", "Avviato manualmente da un admin"),
        ["admin.jobs.enqueue_source.api"] = Multi("Demandé par l'API", "Requested by the API", "Solicitado por la API", "Pedido pela API", "Von der API angefordert", "Richiesto dall'API"),
        ["admin.jobs.enqueue_source.scanner"] = Multi("Relancé automatiquement par le scanner", "Restarted automatically by the scanner", "Relanzado automáticamente por el escáner", "Relançado automaticamente pelo scanner", "Automatisch vom Scanner neu gestartet", "Riavviato automaticamente dallo scanner"),
        ["admin.jobs.enqueue_source.watcher"] = Multi("Déclenché par la surveillance des fichiers", "Triggered by file watching", "Activado por la vigilancia de archivos", "Acionado pela monitorização de ficheiros", "Durch Dateiüberwachung ausgelöst", "Attivato dal monitoraggio file"),
        ["admin.jobs.document_status.indexed"] = Multi("indexé", "indexed", "indexado", "indexado", "indiziert", "indicizzato"),
        ["admin.jobs.document_status.pending"] = Multi("en attente d'indexation", "pending indexing", "pendiente de indexación", "pendente de indexação", "Indexierung ausstehend", "in attesa di indicizzazione"),
        ["admin.jobs.document_status.outdated"] = Multi("à réindexer", "needs reindexing", "requiere reindexación", "precisa de reindexação", "muss neu indiziert werden", "da reindicizzare"),
        ["admin.jobs.document_status.missing"] = Multi("absent", "missing", "ausente", "ausente", "fehlt", "mancante"),
        ["admin.jobs.document_status.deleted"] = Multi("supprimé", "deleted", "eliminado", "removido", "gelöscht", "eliminato"),
        ["admin.jobs.document_status.failed"] = Multi("en erreur", "failed", "en error", "com falha", "fehlerhaft", "in errore"),
        ["admin.jobs.document_status.active"] = Multi("actif", "active", "activo", "ativo", "aktiv", "attivo"),
        ["admin.jobs.document_status.inactive"] = Multi("inactif", "inactive", "inactivo", "inativo", "inaktiv", "inattivo"),
        ["admin.jobs.auto_pause.reason.admin_cancel"] = Multi("demandée par l'administrateur", "requested by the administrator", "solicitada por el administrador", "solicitada pelo administrador", "vom Administrator angefordert", "richiesta dall'amministratore"),
        ["admin.jobs.auto_pause.reason.admin_pause"] = Multi("demandée par l'administrateur", "requested by the administrator", "solicitada por el administrador", "solicitada pelo administrador", "vom Administrator angefordert", "richiesta dall'amministratore"),
        ["admin.jobs.auto_pause.reason.repeated_failures"] = Multi("après erreurs répétées", "after repeated failures", "tras errores repetidos", "após falhas repetidas", "nach wiederholten Fehlern", "dopo errori ripetuti"),
        ["admin.session.enabled"] = Multi("Session admin activée.", "Admin session enabled.", "Sesión admin activada.", "Sessão admin ativada.", "Admin-Sitzung aktiviert.", "Sessione admin attivata."),
        ["admin.session.disabled"] = Multi("Session admin désactivée.", "Admin session disabled.", "Sesión admin desactivada.", "Sessão admin desativada.", "Admin-Sitzung deaktiviert.", "Sessione admin disattivata."),
        ["admin.tracking.reconnect_required"] = Multi("Reconnecte la session admin pour reprendre le suivi réel de cette réindexation.", "Reconnect the admin session to resume the live status of this reindexing job.", "Vuelve a conectar la sesión admin para reanudar el estado real de esta reindexación.", "Restabeleça a sessão admin para retomar o estado real desta reindexação.", "Verbinde die Admin-Sitzung erneut, um den echten Status dieser Neuindexierung wieder aufzunehmen.", "Ricollega la sessione admin per riprendere lo stato reale di questa reindicizzazione."),

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
        ["settings.appearance"] = Multi("Apparence", "Appearance", "Apariencia", "Aspeto", "Darstellung", "Aspetto"),
        ["settings.theme.system"] = Multi("Suivre Windows", "Follow Windows", "Seguir Windows", "Seguir o Windows", "Windows folgen", "Segui Windows"),
        ["settings.theme.dark"] = Multi("Sombre", "Dark", "Oscuro", "Escuro", "Dunkel", "Scuro"),
        ["settings.theme.light"] = Multi("Clair", "Light", "Claro", "Claro", "Hell", "Chiaro"),
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
        ["settings.support.note"] = Multi("Le diagnostic ne contient pas la clé API (elle est masquée).", "The diagnostic does not contain the API key (it is masked).", "El diagnóstico no contiene la clave API (está oculta).", "O diagnóstico não contém a chave API (está mascarada).", "Die Diagnose enthält keinen API-Schlüssel (er ist maskiert).", "La diagnostica non contiene la chiave API (è mascherata)."),
        ["settings.status.busy"] = Multi("Une action est déjà en cours.", "An action is already running.", "Ya hay una acción en curso.", "Já existe uma ação em curso.", "Es läuft bereits eine Aktion.", "È già in corso un'azione."),
        ["settings.status.exporting"] = Multi("Création du diagnostic…", "Creating diagnostic bundle…", "Creando el diagnóstico…", "A criar o diagnóstico…", "Diagnose wird erstellt…", "Creazione diagnostica…"),
        ["settings.status.exported"] = Multi("Diagnostic exporté :", "Diagnostic exported:", "Diagnóstico exportado:", "Diagnóstico exportado:", "Diagnose exportiert:", "Diagnostica esportata:"),
        ["settings.status.export_failed"] = Multi("Échec export diagnostic.", "Diagnostic export failed.", "Error al exportar el diagnóstico.", "Falha ao exportar o diagnóstico.", "Diagnoseexport fehlgeschlagen.", "Esportazione diagnostica non riuscita."),
        ["settings.status.repair.running"] = Multi("Installation / réparation en cours…", "Install / repair in progress…", "Instalación / reparación en curso…", "Instalação / reparação em curso…", "Installation / Reparatur läuft…", "Installazione / riparazione in corso…"),
        ["settings.status.repair.ready"] = Multi("Assistant prêt (LLM disponible).", "Assistant ready (LLM available).", "Asistente listo (LLM disponible).", "Assistente pronto (LLM disponível).", "Assistent bereit (LLM verfügbar).", "Assistente pronto (LLM disponibile)."),
        ["settings.status.repair.not_ready"] = Multi("Assistant démarré, mais le modèle n'est pas encore prêt. Réessaie dans 1–2 minutes.", "Assistant started, but the model is not ready yet. Try again in 1–2 minutes.", "El asistente se inició, pero el modelo aún no está listo. Vuelve a intentarlo en 1–2 minutos.", "O assistente arrancou, mas o modelo ainda não está pronto. Tenta novamente em 1–2 minutos.", "Der Assistent wurde gestartet, aber das Modell ist noch nicht bereit. Versuche es in 1–2 Minuten erneut.", "L'assistente è stato avviato, ma il modello non è ancora pronto. Riprova tra 1–2 minuti."),
        ["settings.status.repair.checking"] = Multi("Vérification de l'assistant…", "Checking the assistant…", "Comprobando el asistente…", "A verificar o assistente…", "Assistent wird geprüft…", "Verifica dell'assistente…"),
        ["settings.status.repair.loading"] = Multi("Chargement du modèle en cours…", "Model is loading…", "Cargando el modelo…", "A carregar o modelo…", "Modell wird geladen…", "Caricamento del modello…"),
        ["settings.status.repair.starting"] = Multi("Démarrage de l'assistant…", "Starting the assistant…", "Iniciando el asistente…", "A iniciar o assistente…", "Assistent wird gestartet…", "Avvio dell'assistente…"),
        ["settings.status.repair.downloading"] = Multi("Téléchargement des composants…", "Downloading components…", "Descargando componentes…", "A transferir componentes…", "Komponenten werden heruntergeladen…", "Download dei componenti…"),
        ["settings.status.repair.download_done"] = Multi("Téléchargement terminé. Redémarre l'assistant si nécessaire.", "Download completed. Restart the assistant if needed.", "Descarga terminada. Reinicia el asistente si es necesario.", "Transferência concluída. Reinicia o assistente se necessário.", "Download abgeschlossen. Starte den Assistenten bei Bedarf neu.", "Download completato. Riavvia l'assistente se necessario."),
        ["settings.status.repair.no_auto"] = Multi("Aucun moyen automatique trouvé. Vérifie l'installation locale ou contacte l'intégrateur.", "No automatic method found. Check the local installation or contact the integrator.", "No se encontró ningún método automático. Comprueba la instalación local o contacta con el integrador.", "Não foi encontrado nenhum método automático. Verifica a instalação local ou contacta o integrador.", "Es wurde keine automatische Methode gefunden. Prüfe die lokale Installation oder kontaktiere den Integrator.", "Nessun metodo automatico trovato. Controlla l'installazione locale o contatta l'integratore."),
        ["settings.status.repair.cancelled"] = Multi("Installation annulée.", "Installation cancelled.", "Instalación cancelada.", "Instalação cancelada.", "Installation abgebrochen.", "Installazione annullata."),
        ["settings.status.repair.launch_failed"] = Multi("Impossible de lancer l'installation.", "Could not start the installation.", "No se pudo iniciar la instalación.", "Não foi possível iniciar a instalação.", "Die Installation konnte nicht gestartet werden.", "Impossibile avviare l'installazione."),
        ["settings.status.repair.waiting"] = Multi("Attente du démarrage du LLM…", "Waiting for the LLM to start…", "Esperando al arranque del LLM…", "À espera do arranque do LLM…", "Warten auf den Start des LLM…", "In attesa dell'avvio del LLM…"),
        ["settings.status.repair.timeout"] = Multi("Le LLM ne répond pas encore. Vérifie le service local puis réessaie.", "The LLM is not responding yet. Check the local service and try again.", "El LLM aún no responde. Comprueba el servicio local y vuelve a intentarlo.", "O LLM ainda não responde. Verifica o serviço local e tenta novamente.", "Das LLM antwortet noch nicht. Prüfe den lokalen Dienst und versuche es erneut.", "Il LLM non risponde ancora. Controlla il servizio locale e riprova."),
        ["settings.status.failed_prefix"] = Multi("Échec : ", "Failed: ", "Error: ", "Falha: ", "Fehler: ", "Errore: ")
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
        return NormalizeLanguage(language) switch
        {
            "en" => $"Help action — reindex document: {compact}",
            "es" => $"Acción de ayuda — reindexar documento: {compact}",
            "pt" => $"Ação da ajuda — reindexar documento: {compact}",
            "de" => $"Hilfeaktion — Dokument neu indexieren: {compact}",
            "it" => $"Azione guida — reindicizza documento: {compact}",
            _ => $"Action aide — réindexer le document : {compact}"
        };
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
