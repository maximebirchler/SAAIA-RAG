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
        ["header.jobs"] = Multi("Jobs admin", "Admin jobs", "Trabajos admin", "Jobs admin", "Admin-Jobs", "Job admin"),
        ["panel.chats"] = Multi("Discussions", "Chats", "Chats", "Chats", "Chats", "Chat"),
        ["button.new"] = Multi("Nouveau", "New", "Nuevo", "Novo", "Neu", "Nuovo"),
        ["button.connect"] = Multi("Connecter", "Connect", "Conectar", "Ligar", "Verbinden", "Connetti"),
        ["button.setup"] = Multi("Configuration", "Setup", "Configuración", "Configuração", "Einrichtung", "Configurazione"),
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
        ["admin.jobs.subtitle"] = Multi("Suivi propre des réindexations et des autres traitements admin, séparé des discussions.", "Clean tracking for reindexing and other admin jobs, separate from chat.", "Seguimiento limpio de las reindexaciones y otros trabajos admin, separado del chat.", "Acompanhamento limpo das reindexações e de outros jobs admin, separado do chat.", "Saubere Verfolgung von Neuindexierungen und anderen Admin-Jobs, getrennt vom Chat.", "Monitoraggio pulito delle reindicizzazioni e degli altri job admin, separato dalla chat."),
        ["admin.jobs.page.subtitle"] = Multi("Historique admin clair, stable et séparé des discussions.", "Clear, stable admin history separate from chat.", "Historial admin claro y estable, separado del chat.", "Histórico admin claro e estável, separado do chat.", "Klarer, stabiler Admin-Verlauf, getrennt vom Chat.", "Cronologia admin chiara e stabile, separata dalla chat."),
        ["admin.jobs.page.summary"] = Multi("{0} visible(s) • {1} chargé(s) • {2} actif(s)", "{0} visible • {1} loaded • {2} active", "{0} visibles • {1} cargados • {2} activos", "{0} visíveis • {1} carregados • {2} ativos", "{0} sichtbar • {1} geladen • {2} aktiv", "{0} visibili • {1} caricati • {2} attivi"),
        ["admin.jobs.back"] = Multi("Retour", "Back", "Volver", "Voltar", "Zurück", "Indietro"),
        ["admin.jobs.list.title"] = Multi("Historique et jobs actifs", "History and active jobs", "Historial y trabajos activos", "Histórico e jobs ativos", "Verlauf und aktive Jobs", "Cronologia e job attivi"),
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
        ["admin.jobs.filter.ingestion"] = Multi("Réindexation", "Reindexing", "Reindexación", "Reindexação", "Neuindexierung", "Reindicizzazione"),
        ["admin.jobs.filter.summary"] = Multi("Résumés", "Summaries", "Resúmenes", "Resumos", "Zusammenfassungen", "Riepiloghi"),
        ["admin.jobs.status_filter.all"] = Multi("Tous les statuts", "All statuses", "Todos los estados", "Todos os estados", "Alle Status", "Tutti gli stati"),
        ["admin.jobs.status_filter.active"] = Multi("Actifs", "Active", "Activos", "Ativos", "Aktiv", "Attivi"),
        ["admin.jobs.status_filter.paused"] = Multi("En pause", "Paused", "En pausa", "Em pausa", "Pausiert", "In pausa"),
        ["admin.jobs.loading"] = Multi("Chargement des jobs admin…", "Loading admin jobs…", "Cargando trabajos admin…", "A carregar jobs admin…", "Admin-Jobs werden geladen…", "Caricamento job admin…"),
        ["admin.jobs.empty"] = Multi("Aucun job à afficher avec les filtres actuels.", "No jobs to display with the current filters.", "No hay trabajos para mostrar con los filtros actuales.", "Nenhum job para mostrar com os filtros atuais.", "Mit den aktuellen Filtern sind keine Jobs anzuzeigen.", "Nessun job da mostrare con i filtri attuali."),
        ["admin.jobs.copy_id"] = Multi("Copier l'ID", "Copy ID", "Copiar ID", "Copiar ID", "ID kopieren", "Copia ID"),
        ["admin.jobs.id_copied"] = Multi("ID du job copié.", "Job ID copied.", "ID del trabajo copiado.", "ID do job copiado.", "Job-ID kopiert.", "ID job copiato."),
        ["admin.jobs.cancel"] = Multi("Annuler le job", "Cancel job", "Cancelar trabajo", "Cancelar job", "Job abbrechen", "Annulla job"),
        ["admin.jobs.resume"] = Multi("Reprendre l'ingestion", "Resume ingestion", "Reanudar ingestión", "Retomar ingestão", "Ingestion fortsetzen", "Riprendi ingestione"),
        ["admin.jobs.cancel_done"] = Multi("Annulation confirmée.", "Cancellation confirmed.", "Cancelación confirmada.", "Cancelamento confirmado.", "Abbruch bestätigt.", "Annullamento confermato."),
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
        ["admin.jobs.group.active"] = Multi("Jobs actifs", "Active jobs", "Trabajos activos", "Jobs ativos", "Aktive Jobs", "Job attivi"),
        ["admin.jobs.group.paused"] = Multi("En pause", "Paused", "En pausa", "Em pausa", "Pausiert", "In pausa"),
        ["admin.jobs.group.failed"] = Multi("Échecs", "Failed", "Fallidos", "Falhados", "Fehler", "Falliti"),
        ["admin.jobs.group.canceled"] = Multi("Annulés", "Canceled", "Cancelados", "Cancelados", "Abgebrochen", "Annullati"),
        ["admin.jobs.group.history"] = Multi("Historique terminé", "Completed history", "Historial completado", "Histórico concluído", "Abgeschlossene Historie", "Storico completato"),
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
        ["admin.jobs.error.source_removed_during_ingestion"] = Multi("Source supprimée pendant l'ingestion", "Source removed during ingestion", "Fuente eliminada durante la ingestión", "Fonte removida durante a ingestão", "Quelle während der Ingestion entfernt", "Sorgente rimossa durante l'ingestione"),
        ["admin.jobs.delete_done"] = Multi("{0} job(s) supprimé(s) de l'historique.", "{0} job(s) removed from history.", "{0} trabajo(s) eliminados del historial.", "{0} job(s) removidos do histórico.", "{0} Job(s) aus dem Verlauf gelöscht.", "{0} job rimossi dalla cronologia."),
        ["admin.jobs.delete_failed"] = Multi("Échec de la suppression de l'historique : ", "Could not delete history: ", "No se pudo eliminar el historial: ", "Não foi possível apagar o histórico: ", "Verlauf konnte nicht gelöscht werden: ", "Impossibile eliminare la cronologia: "),
        ["admin.jobs.delete_nothing"] = Multi("Aucun job terminal à supprimer avec les filtres actuels.", "No terminal job to delete with the current filters.", "No hay trabajos terminales que eliminar con los filtros actuales.", "Nenhum job terminal para apagar com os filtros atuais.", "Mit den aktuellen Filtern gibt es keine terminalen Jobs zum Löschen.", "Nessun job terminale da eliminare con i filtri attuali."),
        ["admin.jobs.delete_selection_none"] = Multi("La sélection ne contient plus de job terminal supprimable.", "The selection no longer contains any deletable terminal job.", "La selección ya no contiene ningún trabajo terminal eliminable.", "A seleção já não contém nenhum job terminal eliminável.", "Die Auswahl enthält keinen löschbaren terminalen Job mehr.", "La selezione non contiene più alcun job terminale eliminabile."),
        ["admin.jobs.refresh_failed_soft"] = Multi("dernière actualisation impossible", "last refresh failed", "última actualización fallida", "última atualização falhou", "letzte Aktualisierung fehlgeschlagen", "ultimo aggiornamento non riuscito"),
        ["admin.jobs.id_copy_unavailable"] = Multi("Copie indisponible sur ce poste.", "Copy unavailable on this device.", "Copia no disponible en este dispositivo.", "Cópia indisponível neste posto.", "Kopieren auf diesem Gerät nicht verfügbar.", "Copia non disponibile su questo dispositivo."),
        ["admin.jobs.card_error"] = Multi("Impossible d'afficher le job {0}.", "Could not render job {0}.", "No se pudo mostrar el trabajo {0}.", "Não foi possível mostrar o job {0}.", "Job {0} konnte nicht dargestellt werden.", "Impossibile mostrare il job {0}."),
        ["admin.jobs.details.job_id"] = Multi("Job ID", "Job ID", "ID del trabajo", "ID do job", "Job-ID", "ID job"),
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
        ["admin.jobs.card.document_versions"] = Multi("versions {0}/{1}", "versions {0}/{1}", "versiones {0}/{1}", "versões {0}/{1}", "Versionen {0}/{1}", "versioni {0}/{1}"),
        ["admin.jobs.value.yes"] = Multi("Oui", "Yes", "Sí", "Sim", "Ja", "Sì"),
        ["admin.jobs.value.no"] = Multi("Non", "No", "No", "Não", "Nein", "No"),
        ["admin.jobs.type.ingestion"] = Multi("Ingestion", "Ingestion", "Ingesta", "Ingestão", "Ingestion", "Ingestione"),
        ["admin.jobs.type.summary"] = Multi("Résumé admin", "Admin summary", "Resumen admin", "Resumo admin", "Admin-Zusammenfassung", "Riepilogo admin"),
        ["admin.jobs.job_type.upsert"] = Multi("Mise à jour de l'index", "Index update", "Actualización del índice", "Atualização do índice", "Index-Aktualisierung", "Aggiornamento dell'indice"),
        ["admin.jobs.job_type.delete"] = Multi("Suppression de l'index", "Index deletion", "Eliminación del índice", "Remoção do índice", "Index-Löschung", "Eliminazione dell'indice"),
        ["admin.jobs.job_type.summary"] = Multi("Génération du résumé", "Summary generation", "Generación del resumen", "Geração do resumo", "Zusammenfassung erzeugen", "Generazione del riepilogo"),
        ["admin.jobs.phase.extracting"] = Multi("Extraction", "Extracting", "Extracción", "Extração", "Extraktion", "Estrazione"),
        ["admin.jobs.phase.chunking"] = Multi("Découpage", "Chunking", "Segmentación", "Segmentação", "Aufteilung", "Suddivisione"),
        ["admin.jobs.phase.embedding"] = Multi("Embeddings", "Embeddings", "Embeddings", "Embeddings", "Embeddings", "Embeddings"),
        ["admin.jobs.phase.upserting"] = Multi("Écriture index", "Index write", "Escritura del índice", "Escrita do índice", "Index schreiben", "Scrittura indice"),
        ["admin.jobs.phase.deleting"] = Multi("Suppression", "Deleting", "Eliminación", "Remoção", "Löschen", "Eliminazione"),
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
        ["admin.jobs.document_status.failed"] = Multi("en erreur", "failed", "en error", "com falha", "fehlerhaft", "in errore"),
        ["admin.jobs.document_status.active"] = Multi("actif", "active", "activo", "ativo", "aktiv", "attivo"),
        ["admin.jobs.document_status.inactive"] = Multi("inactif", "inactive", "inactivo", "inativo", "inaktiv", "inattivo"),
        ["admin.jobs.auto_pause.reason.admin_cancel"] = Multi("après annulation par l'administrateur", "after administrator cancellation", "tras cancelación del administrador", "após cancelamento pelo administrador", "nach Abbruch durch den Administrator", "dopo annullamento da parte dell'amministratore"),
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
