# Client.Core

Noyau portable .NET 8 utilisé par le client WinUI et testé sans WinUI, modèle, corpus ou serveur.

## Frontières

- `Diagnostics/ClientDiagnosticJournal` possède l'historique immuable des opérations terminées (64 opérations, 240 événements par opération par défaut). `lastN` porte sur les tours de questions terminés ; une opération source ou une reprise en arrière-plan ne remplace pas la dernière question.
- Le propriétaire UI ouvre le tour avant la persistance de la question. Les services empruntent son contexte. Le temps total s'arrête après l'affectation du résultat UI et sa persistance ; il ne prétend pas mesurer la présentation des pixels par le compositeur Windows. Les appels directs de l'agent se terminent au retour de l'agent.
- `AdvancedAnalysisJobPoller` possède la boucle transport, limites, délais, retries et validation d'identité/révision. Un poll réseau est borné à 30 secondes par défaut et réessaie sans déclarer terminé un job durable. Il reçoit des adaptateurs injectés, ne connaît ni LLM ni fenêtre, et ne transforme jamais une attente ou un échec en réponse réussie. L'annulation de son suivi local ne demande pas à elle seule l'annulation du job serveur.
- `Execution/AgentRunLifecycle` protège les transitions de cycle de vie et leurs issues terminales exclusives. Les règles de sélection sémantique restent dans les composants existants ; ce type ne prend pas les décisions du modèle.
- `Execution/AgentIterationPipeline` exécute une fois les étapes ordonnées et s'arrête dès qu'une étape demande `Continue`, `Stop` ou fournit un résultat `Complete`. `Advance` seul autorise l'étape suivante. Le runner utilise ce pipeline avec ses quatre vraies phases : préparation, acquisition, soumissions/outils et vérification/révision. Chaque requête possède son état mutable, chaque tour son état distinct ; la mémoire de conversation reste un contrat d'entrée/sortie séparé. La santé de l'observateur est comptée même quand celui-ci échoue en observant une exception métier, sans remplacer cette exception.
- `Execution/AgentIterationPlanner` décide des gates mécaniques de collecte, réserve de rendu, sélection et finalisation ; le runner utilise réellement ce résultat pour le tour courant. `AgentToolMenuPlanner` construit le menu d'outils effectivement exposé selon ce plan. Ces politiques sont indépendantes des prompts et des fournisseurs.
- `Execution/EvidenceAdequacyCheckpoint` possède l'état du checkpoint d'une seule exécution (signature, sélection précédente, candidats audités, manque encore actif). Une réponse de protocole invalide ne peut pas remplacer sa dernière décision valide. Le modèle reste l'auteur de la décision d'adéquation.
- `Conversation/ConversationActivityGate` réserve une surface de discussion à son chargement ou à son tour courant. La lease possède son token dès l'admission, avant tout await ; annuler pendant la préparation conserve l'écriture durable et empêche le démarrage du modèle. Demander l'annulation ne libère pas sa mémoire avant la fin du propriétaire et ne touche pas le token d'un ancien tour. Le client capture l'identité de discussion pour ses callbacks et sa persistance ; changer ou charger une session ne peut pas réhydrater la mémoire au milieu d'un envoi. Un rafraîchissement visuel ne rebind pas cette identité ; les réglages ne la restaurent que préconnexion.
- `Sources/SourceLocator` transporte la même identité pour une carte et un lien. Le registre borné refuse de choisir entre deux révisions. Un lien historique sans identité reste explicitement non vérifié ; une acceptation de lancement Windows ne garantit pas le rendu de la page PDF.

## Diagnostic de production

Le client utilise ces composants dans un Release standard. Aucun symbole de test n'est nécessaire. Il écrit des événements `CLIENT_PROBE` structurés dans les logs tournants existants, propage `traceparent`, `X-SAAIA-Turn-Id` et `X-SAAIA-Session-Id` vers l'API, et conserve les identifiants avec les sources persistées. La corrélation d'une reprise s'appuie sur le job et son `originTraceId` ; elle ne fabrique pas de durée en soustrayant les horloges de deux machines.

Les événements ordinaires contiennent identifiants, compteurs, durées monotones, codes et configuration effective sans clés ni texte de question/document. Les valeurs inconnues restent nulles. Le TTFT n'est renseigné que lorsqu'il est mesuré par le fournisseur sur un flux réel. Une panne de journalisation est comptée et ne change pas le résultat métier.

Les événements modèle sont liés au couple trace/tour de leur propriétaire, même si leur callback arrive dans un autre contexte ambiant. Deux abonnés à un fournisseur partagé ne comptent pas deux fois le même appel. Une métrique sans identité n'est pas attribuée arbitrairement. Le bilan du collecteur distingue un sink configuré d'une absence d'échec observé ; il ne prétend pas prouver une livraison externe lorsque le sink est absent.

Les deux exports support incluent le même historique borné et un manifeste des pièces présentes, absentes et en échec. Les logs historiques bruts et le contenu sont exclus par défaut ; `SAAIA_DIAGNOSTIC_CONTENT=true` active explicitement le contenu. Les secrets restent masqués dans ce mode. Les empreintes de copies modifiées par le masquage sont recalculées, en conservant l'empreinte source dans un sidecar `.sha256.source`.

## Validation hors ligne

`dotnet test client/SAAIA.Client.Core.Tests/SAAIA.Client.Core.Tests.csproj`

Les tests couvrent l'historique (y compris échecs et annulations), ses bornes et son immutabilité, la santé du collecteur, les transitions, le polling/retry/identité/annulation, les sources ambiguës et le masquage. Ils n'effectuent aucun appel réseau. Les tests d'adaptateurs WinUI sont dans `ClientProductionDiagnosticsTests` et utilisent un `HttpMessageHandler` et un collecteur de disponibilité injectés.

Le test `SourceBackedAgentV2Tests.RunStateIsolation` lance deux requêtes réellement chevauchées sur le même runner et vérifie que preuves, budgets de tour et traces restent séparés. L'extraction du dispatcher conserve les 83 instructions principales de l'ancienne boucle dans leur ordre ; la preuve Roslyn et l'original se trouvent dans `artifacts/client-cleanup-20261024/ExtractRun/`. L'agrégat de helpers de protocole reste volumineux : cette séparation rend l'état et les étapes auditables, sans prétendre avoir réécrit ou validé sémantiquement toutes ces politiques héritées.
