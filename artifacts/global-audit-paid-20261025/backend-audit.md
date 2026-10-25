# Audit backend, contrats, retrieval et worker avancé

Audit du 25 octobre 2026, sur le checkout annoncé par le coordinateur comme propre à `1e6b7e5`. Lecture seule : aucun Git, build, test, serveur, secret, appel API/modèle ou consommation payante. Seul ce rapport neuf est écrit. Les tests cités sont lus, **pas réexécutés**. Un défaut « établi par le code » signifie que le chemin fautif est identifiable sans supposer une réponse particulière du modèle ; la reproduction d'exécution reste à réaliser dans une fixture isolée.

Le moteur possède bien des outils pour rechercher, reformuler, lire les pages d'une révision observée, trouver un titre littéral et lire une autre fenêtre du texte canonique. Je n'ai pas établi de panne générale des identités document/chunk/révision. J'ai en revanche identifié trois défauts prioritaires de cycle de vie/admission/atomicité, un défaut de comptabilisation du temps des outils, et confirmé le côté serveur d'une perte de contexte signalée par l'audit client. Les garanties financières sont transmises au volet budget mené séparément.

## B1 — P1 — Une panne du renouvellement de bail laisse le provider en cours sans annulation ni attente

**Statut : défaut établi par le contrôle de flux ; panne non injectée pendant cet audit.**

[AdvancedAnalysisWorker.cs:397](</C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/AdvancedAnalysis/AdvancedAnalysisWorker.cs:397>) démarre `_provider.ExecuteAsync` avant la boucle de renouvellement. Si `RenewAndReadStateAsync` lève alors que le jeton d'arrêt du service n'est pas annulé, le catch à [la ligne 422](</C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/AdvancedAnalysis/AdvancedAnalysisWorker.cs:422>) relance directement. La seule annulation explicite du provider, puis son attente, se trouvent après ce catch, sur le chemin où le renouvellement a **retourné** Lost/Canceled. La sortie dispose le `CancellationTokenSource` lié ; elle n'annule pas un travail encore actif par cette seule disposition.

Conséquence possible : `ProcessClaimedAsync` traite une erreur de provider et tente de marquer la tentative échouée, tandis que la tâche fournisseur continue des requêtes ou de la recherche. Si la base est indisponible, le job peut ensuite être récupéré par expiration alors que l'ancien travail n'a pas été terminé. Le filtre de bail des écritures empêche nombre de publications tardives ; il ne récupère pas l'argent ni les ressources d'appels déjà partis.

Correction à préparer : encadrer **tout** le travail possédant le bail par une durée de vie qui annule et observe la tâche enfant sur toutes les sorties, y compris exception du heartbeat. Ne pas masquer l'erreur d'origine par un échec de drainage. Prévoir une attente bornée pour une implémentation provider défectueuse qui ignorerait son jeton. La résolution initiale des preuves, effectuée avant cette boucle à la ligne 236, doit aussi être couverte par le bail ou recontrôlée avant le premier appel modèle ; une préparation lente ne doit pas démarrer un appel sur un bail déjà expiré.

Test pertinent : reprendre la fixture dédiée de `AdvancedAnalysisWorkerTests`, provider contrôlé bloqué sur un signal, déclencher un échec du prochain renouvellement PostgreSQL **après** le démarrage, puis exiger que le jeton provider soit annulé et que sa tâche soit observée avant la fin de `ProcessOnceAsync`. Vérifier l'absence de deuxième appel et de publication tardive. Le test existant `Cancellation_during_execution_stops_provider_and_prevents_late_result` à `AdvancedAnalysisWorkerTests.cs:447` couvre une annulation explicite retournée par la base ; il ne couvre pas une exception du renouvellement.

## B2 — P1 — Création/annulation de job et audit ne sont pas atomiques

**Statut : défaut établi par le code et la signature d'AuditWriter ; reproduction causale à ajouter.**

[AdvancedAnalysisEndpoints.cs:158](</C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Endpoints/AdvancedAnalysisEndpoints.cs:158>) insère le job avec une transaction implicite. `AuditWriter.WriteAsync` est ensuite appelé sans transaction à [la ligne 190](</C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Endpoints/AdvancedAnalysisEndpoints.cs:190>). Le même découpage existe pour l'annulation : UPDATE à la ligne 313, audit à [la ligne 326](</C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Endpoints/AdvancedAnalysisEndpoints.cs:326>). `AuditWriter` accepte déjà une transaction optionnelle et propage les erreurs de son INSERT.

Si l'audit échoue après l'insertion, le client reçoit une erreur alors que le job est déjà visible et exécutable par le worker. Un replay du même handoff retourne le job existant dès la ligne 103, sans réparer l'audit absent. En annulation, l'utilisateur peut recevoir une erreur alors que l'annulation est déjà effective. C'est le même type de frontière que celle récemment corrigée pour la suppression de discussion ; la correction de suppression ne couvre pas ces endpoints.

Correction à préparer : transaction explicite pour l'écriture métier et son audit, ACK uniquement après commit. Préserver l'idempotence sur `(tenant_id,user_id,handoff_id)` et les filtres tenant/user de l'annulation. Un événement d'audit par action réellement choisie doit avoir une sémantique définie pour les replays.

Test pertinent : trigger de refus d'INSERT dans `audit_events`, uniquement dans une **nouvelle** base synthétique. La création doit laisser zéro nouveau job et zéro audit ; l'annulation doit conserver le statut/révision/lease initiaux si elle est rejetée. Tester ensuite le chemin nominal et les scopes tenant/user. Le test existant `Durable_job_is_idempotent_user_scoped_and_cancelable` (`AdvancedAnalysisEndpointsTests.cs:160`) confirme le nominal, pas le rollback sur panne d'audit.

## B3 — P2 — Le quota de jobs simultanés peut être dépassé par deux créations concurrentes

**Statut : défaut établi par l'entrelacement possible des commandes ; concurrence non exécutée ici.**

Le comptage à [AdvancedAnalysisEndpoints.cs:114](</C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Endpoints/AdvancedAnalysisEndpoints.cs:114>) et l'insertion à la ligne 158 sont séparés, sans transaction, verrou par utilisateur ni contrainte SQL portant sur le quota. L'unicité existante ne concerne que le même handoff.

Entrelacement suffisant : quota configuré à 1, zéro job actif ; deux handoffs distincts du même utilisateur lisent tous deux zéro avant d'insérer. Les deux INSERT sont valides et la file contient deux jobs. Le défaut concerne l'admission, pas l'isolation entre tenants. Il peut augmenter la file et les travaux payants acceptés au-delà de la limite annoncée.

Correction à préparer en même temps que B2 : sérialiser l'admission au niveau `(tenant,user)` dans la transaction qui recompte et insère. Un verrou de session seule ne protège pas deux discussions du même utilisateur. Conserver la restitution du job existant pour un handoff idempotent.

Test pertinent : deux appels concurrents avec handoffs distincts et quota 1, synchronisés avant le comptage dans une base dédiée. Exiger une seule création acceptée, l'autre 429, et exactement un job actif. Ajouter le contrôle que deux utilisateurs indépendants ne partagent pas ce verrou logique.

## B4 — P2 — Le temps consommé des outils ne représente pas tous les appels admis

**Statut : omission établie dans la comptabilité ; impact de latence à mesurer, aucun ralentissement injecté ici.**

[AdvancedAnalysisToolGateway.cs:413](</C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/AdvancedAnalysis/AdvancedAnalysisToolGateway.cs:413>) démarre le chronomètre après l'attente du bulkhead et la résolution éventuelle du nom de document. Il l'arrête puis augmente `_elapsedMilliseconds` aux lignes 476–478, avant la revalidation SQL des preuves (482) et la persistance de l'événement (563). Si l'appel lève avant l'incrément, le catch arrête le chronomètre et persiste un échec, mais n'ajoute pas son temps au compteur mémoire de la tentative.

Ainsi, un outil échoué peut avoir un temps durable enregistré mais ne pas consommer le budget temporel courant. Le rechargement d'historique réintroduit ensuite ce temps via `history.Sum`, ce qui rend le compteur différent avant/après reprise. Le plafond de nombre d'appels reste actif : il n'y a pas de boucle infinie démontrée. Le compteur ne constitue cependant pas un plafond de durée réelle de l'ensemble de l'opération, et la résolution/persistance lentes sont invisibles dans la durée renvoyée à l'agent.

Correction à préparer : définir explicitement si le budget porte sur le coût actif du retrieval ou sur le temps complet de l'outil. Charger exactement une fois le temps de chaque appel admis, y compris échec, avec un finally et des bornes cohérentes entre mémoire, observation et trace durable. Mesurer séparément attente, retrieval, revalidation et persistance si l'attente est exclue du budget par choix.

Tests pertinents : appel admis ralenti qui échoue avant le retour du retrieval, puis lecture du budget sans recréer le gateway ; conservation identique après rechargement de l'historique. Contrôle d'une revalidation lente si la durée complète est retenue. Les tests actuels de `AdvancedAnalysisWorkerTests.ToolBudget.cs` couvrent capacité, limite de nombre, atomicité d'une fenêtre trop large et historique ; je n'y ai pas trouvé ce contrôle temporel d'échec.

## B5 — P1 partagé avec l'audit client — Le serveur ne restitue pas le contexte conversationnel perdu au handoff

**Statut : absence serveur confirmée ; défaut de suivi confirmé conjointement avec le chemin client, périmètre limité aux demandes dépendantes d'un tour précédent.**

[AdvancedAnalysisProvider.cs:29](</C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/AdvancedAnalysis/AdvancedAnalysisProvider.cs:29>) ne reçoit que job/tenant/user, Handoff, Evidence et PreviousToolEvents. Le worker désérialise son handoff puis construit cette requête à [AdvancedAnalysisWorker.cs:260](</C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/AdvancedAnalysis/AdvancedAnalysisWorker.cs:260>) ; aucune lecture de `chat_messages` ne recharge l'historique conversationnel dans le module AdvancedAnalysis. Le Planner repart de `request.Handoff.RequestText` à [PromptsAndEvidence.cs:292](</C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/AdvancedAnalysis/OpenAiCompatibleAdvancedAnalysisProvider.PromptsAndEvidence.cs:292>), comme les autres prompts. ResearchState représente des recherches antérieures et des références, pas les contraintes utilisateur de la conversation.

Le constat client communiqué par l'autre auditeur est que le handoff de grille transmet le dernier `effectiveUserMessage` seul hors branches spéciales de référence utilisateur. Un suivi tel que « refais pareil sans X » peut alors perdre la demande initiale et ses contraintes. `SessionId` assure l'appartenance et les diagnostics ; sa présence ne signifie pas qu'un historique sera chargé.

Correction à concevoir avec le client : transmettre une demande autonome et ses contraintes conversationnelles explicitement distinguées des preuves documentaires, ou résoudre un contexte borné du côté serveur avec le bon scope. Ne pas transformer les anciennes réponses de l'assistant en preuves. Test inter-parcours simulé : première demande avec contraintes, suivi elliptique, transfert vers le provider contrôlé ; exiger que les contraintes pertinentes arrivent au vrai prompt, et que la sélection d'une autre discussion ne puisse les fournir.

## Points financiers transmis au volet budget, sans correction dans ce périmètre

Deux garanties ne sont pas démontrées par le garde actuel. Elles sont établies au niveau de son fonctionnement, sans observation de dépense réelle dans cet audit :

- [AdvancedAnalysisExternalBudgetGuard.cs:81](</C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/AdvancedAnalysis/AdvancedAnalysisExternalBudgetGuard.cs:81>) réserve une entrée estimée à caractères/4. Cette estimation n'est pas une borne haute de tokenisation. `Complete/Close` accepte ensuite un usage fourni plus élevé. Un test simulé peut réserver avec 1 000 caractères, un token de sortie, puis fermer avec 1 000 tokens d'entrée valides : un plafond par job situé entre ces deux coûts aura été franchi après admission. Le nom `reserved_upper_bound` ne suffit donc pas à garantir un plafond financier strict. Il faut un compte fiable ou une réserve conservatrice, et identifier explicitement les estimations.
- Les réservations sont en mémoire ; `AppendLedger` n'écrit qu'à la clôture ([ligne 323](</C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/AdvancedAnalysis/AdvancedAnalysisExternalBudgetGuard.cs:323>)). Une interruption du processus après envoi mais avant clôture, ou une erreur d'append, peut laisser une dépense absente du ledger après redémarrage. Les tests existants reconstruisent un garde après une **clôture réussie**, pas après un appel laissé incertain. Un test de deux instances du garde sur le même fichier démontre aussi la limite des verrous mémoire ; ce dernier cas reste une hypothèse de déploiement multiprocessus, pas la topologie actuelle affirmée. Une réserve persistée avant envoi et une reprise bloquée sur les écritures incertaines méritent examen avant tout plafond qualifié de dur.

Le coordinateur traite ces points avec le harnais économique. Le provider générique accepte volontairement un nom de modèle configuré ; l'interdiction de modèle cher et la limite de famille 5.6 de la mission doivent être contrôlées dans le profil/harnais avant le moindre appel, sans transformer un prix historique par défaut en tarif courant validé.

## Ce qui est justifié dans les chemins lus

- Les appels serveur de recherche passent bien par `RetrievalEngine` avec `SourceBackedCanonical=true` (`AdvancedAnalysisToolGateway.cs:470`) ; `RetrievalEngine.Entry.cs:26` les dirige vers `CanonicalRetrieval`. Ce chemin est distinct du retrieval historique enrichi par des heuristiques. Je n'ai pas établi qu'une ancienne réécriture de requête serait encore imposée ici.
- `read_source`, `find_source_text` et `read_evidence` sont réellement déclarés selon les permissions et les sources visibles (`NativeResearchTools.cs:65–103`). Leurs résultats alimentent observations, preuves et historique d'outils ; ce ne sont pas des fonctions seulement promises dans un prompt. Les bornes de quatre pages, topK et quantité de preuves servent à borner le travail ; leur existence seule n'est pas un défaut. Des diagnostics de fenêtre trop large, page absente ou pagination sont présents.
- `AdvancedAnalysisEvidenceResolver` joint tenant, document, révision, chunk/carte et source hash ; les identités attendues sont vérifiées, les citations reviennent depuis les lignes canoniques. Les fenêtres de pages conservent les bornes réelles des chunks au lieu d'attribuer un chunk multi-page à une seule page interne. Les tests `AdvancedAnalysisWorkerTests.EvidenceAudit`, `CanonicalFind` et `ToolBudget` contiennent des contrôles de changement d'identité, de mauvais tenant et de lecture bornée. Leur présence est vérifiée, leur réussite n'est pas réaffirmée par cet audit sans exécution.
- Publication du résultat et écritures d'outils vérifient encore le bail actif. L'ajout d'un événement d'outil est transactionnel avec son compteur (`AdvancedAnalysisJobStore.cs:344–411`). Cela justifie la structure de reprise durable ; B1 traite la tâche enfant et B2 les endpoints, pas un retrait de ces protections.
- Les sondes de payload ajoutent tailles, nombre de preuves, outils proposés/choisis et identifiants opaques, sans exporter le texte du prompt (`LlmPayloadDiagnostics.cs`). Les captures exactes de développement sont un mécanisme distinct, opt-in. Je n'ai pas identifié de fuite de contenu dans cette projection particulière.

## Scope candidat : différence importante entre client et serveur

Le serveur ne réimpose pas le seul `CandidateScopePaths` unique à chaque recherche. `ParsePlan` conserve la catégorie/sourceKey/documentHint du modèle ; `CandidateScopePaths` enrichit les catégories autorisées (`Planning.cs:415–443`). Une recherche sans sourceKey et sans documentHint peut rester générale et changer de catégorie.

Il existe une restriction plus précise : si `GetRequestedDocumentIdentifiers` trouve une référence documentaire dans `RequestedDocumentName` ou RequestText, `ParseResearchReview` réapplique un documentHint à une recherche sans DocId (`Planning.cs:297–302`). Avec un seul document, `ResolvePlannedDocumentHint` le sélectionne systématiquement (`Planning.cs:693–695`). C'est cohérent pour une extraction explicitement demandée. **Hypothèse à vérifier côté producteur du handoff :** si RequestedDocumentName peut n'être qu'une piste inférée, il faut distinguer piste et contrainte ; je ne déclare pas pour autant un défaut serveur établi pour un simple dossier candidat.

## Structure et suppressions

Les frontières Store/Resolver/Gateway/Provider/Validator existent et les références ont été suivies jusqu'au worker, aux endpoints et aux tests. Les fichiers partiels du provider restent couplés à une orchestration complexe ; une nouvelle extraction ne résoudrait pas à elle seule les défauts ci-dessus. Aucune suppression de code n'est proposée : je n'ai pas démontré de code mort dans les chemins examinés, et les anciens helpers de retrieval peuvent avoir des appelants historiques distincts du parcours canonique. L'absence d'appel dans une méthode avancée ne suffit pas à les supprimer.

Ordre proposé au coordinateur : corriger B1, réunir B2/B3 dans une admission transactionnelle testée, résoudre le contexte de suivi avec le client, fiabiliser B4, puis réunir les preuves budgétaires du volet séparé. Exécuter d'abord les reproductions contrôlées et leurs régressions, avant le premier test payant économique. Aucun de ces constats n'établit que le modèle serait incapable de raisonner sur un planning ; ils identifient ce que le logiciel doit garantir pour que l'évaluation du modèle soit interprétable.
