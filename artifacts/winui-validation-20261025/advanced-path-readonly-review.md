# Parcours avancé — revue et preuves sans dépense

Cette revue n'a lancé ni UI ni modèle. Elle a lu le code, les logs du client déjà piloté par le parent, les événements du fournisseur déterministe local et les GET du chat-store isolé. Aucun corps de prompt, réponse privée ou clé n'est conservé ici.

## Déclenchement réel

Il n'existe pas de bouton UI « créer un job avancé » : l'envoi normal passe par `ToolAgentOrchestrator.RunPipeline`, puis `BuildAndRememberAdvancedAnalysisHandoff`. Une frontière locale, un budget local épuisé ou une tâche structurée hors enveloppe peut produire ce handoff. `RagChatAgent.RunCoreAsync` observe `LastAdvancedAnalysisHandoff` et appelle `AdvancedAnalysisClientService` avec la session capturée par l'admission UI. Le service crée le job par `POST /advanced-analysis/jobs`, puis poll le même identifiant.

Le client reste pointé sur le backend 5127. Le vrai worker 5137 partage la seule base isolée de cette campagne ; son fournisseur est le répondant déterministe 18089. Il n'est pas nécessaire que le client pointe directement sur5137 pour que le worker consomme son job.

## Cas observé dans les traces existantes

Tour d'origine : `62f1125da0e4f04a24ef603ed8b5db2f`, tour client `68cfc2ed600c449d9573ea961a464dad`, session `f23536a9-2296-4a1c-96b4-0b4c02a79e4b`.

- À12:18:16UTC, `router.instance_context.completed` annonce `accepted=true`, `actual_context_supplied=true`.
- La frontière choisit `user_instance_context_outside_local_envelope` avec une seule unité de réponse. Le volume et le budget ne sont donc pas la cause de ce transfert.
- Création du job `c061b0f4-c9aa-4026-8cac-4f0f5d49849f`, handoff `15032bd1-ec11-4901-9408-8e6f1d14b53f`.
- Polls révisions1→2→4→5, puis `advanced.result.validation` et `advanced.poll.complete`.
- Deux requêtes du fournisseur avancé à12:18:20 et12:18:22UTC portent le même traceparent racine `62f1125...`. Le fournisseur ne reçoit pas les headers turn/session ; la filiation W3C est présente.
- Le message assistant durable `dadb052a-7a67-4dc4-8918-a76517e68f96` référence le même job, handoff, session et trace d'origine. Le GET du job confirme `succeeded`, révision5.

Ce cas prouve le chemin technique UI→job→worker→poll→message durable. Le résultat contrôlé est une insuffisance documentaire synthétique ; il ne prouve aucune compréhension, sélection de preuves ou réponse sémantique du modèle.

La décision « contexte utilisateur » sur cette question simple est une classification locale discutable : le code applique la valeur de cette classification en sélectionnant le contrat de décision appliquée, puis la frontière avancée. La requête effective contient également la reprise de clarification, avec961 caractères pour105 caractères de nouvelle saisie. Cette revue n'attribue pas le faux positif au seul modèle ni ne change la frontière : il faut comparer l'entrée effective du classifieur et sa décision dans un examen sémantique séparé.

## Premier cas : chemin documentaire perdu

Tour précédent : `c5ef05db0552593503e6939d98f1737f`. Le routeur initial avait choisi `rag.answer`, `documents.context`, aucune clarification et une seule unité de preuve. La clarification est apparue ensuite dans le pipeline source-backed, avant toute exécution documentaire finale.

Cause mécanique trouvée : la construction de mission et sa continuité de réparation transformaient une référence explicite complète par `Path.GetFileName`, supprimant le dossier. Le résolveur actuel distingue correctement les chemins complets et les basenames ; ce rétrécissement fabriquait donc une ambiguïté entre homonymes.

Le correctif conserve la référence intégrale. Les tests couvrent nom seul, chemin slash, chemin Windows, réparation et deux fichiers homonymes : le nom seul reste ambigu ; chaque chemin complet résout l'identité exacte. Il ne s'agit pas d'une sélection approximative d'un document.

## Sondes corrigées

Le mode métadonnées éliminait `source_event` et `source_step`, transformant les événements internes en simples « step » sans nom. Le correctif conserve uniquement ces identifiants contrôlés, des statuts/motifs codés et une projection explicite des compteurs. La longueur de référence et la présence d'un séparateur de dossier permettent maintenant de constater le rétrécissement sans journaliser le nom du document. `source_fields` libre n'est pas ajouté à la liste autorisée en métadonnées.

Validation :114 tests réussis, zéro échec ou ignoré, sources inchangées pendant le run. Manifeste : `document-identity-probes-unit-accepted/manifest.json`. Le contrôle d'architecture passe. La reproduction UI sur le prochain binaire reste requise.

## Reprise au redémarrage à prouver visuellement

1. Préparer un transfert réellement initié par l'envoi UI. La simulation du fournisseur avancé peut être ralentie via `runtime/advanced-worker/fixture-control.json` (`mode=advanced`, délai explicite), tout en gardant le vrai worker et PostgreSQL.
2. Attendre le log `Advanced analysis snapshot persisted` et vérifier le message assistant durable avec `advancedAnalysis.schemaVersion`, jobId, handoffId, sessionId, statut non terminal et révision≥1.
3. Fermer le client pendant le job. La fermeture arrête le polling local ; elle ne doit pas annuler le job serveur.
4. Relancer le même profil isolé et charger la même session. `AdvancedAnalysisPersistedStateParser` vérifie schéma, identités et session ; `StartAdvancedAnalysisTrackersForCurrentSession` recrée seulement le tracker des états non terminaux.
5. Constater une nouvelle trace `advanced_resume`, son `originTraceId`, le même job et le même message assistant ; le résultat doit mettre à jour ce message. Compter les jobs avant/après pour exclure une création supplémentaire.

Une simple fermeture après le statut final ne prouve que le rechargement du message, pas la reprise d'un job en cours.

## Fixture du routeur 18087

Le mode `advanced` actuel du fixture est conçu pour les prompts du worker : il répond au planificateur de recherche ou au writer de résultat. Il n'est pas un « forcer handoff client » valide à lui seul. Le routeur client comporte une classification structurée, un contrôle de contexte utilisateur et un second contrat d'arguments. Un unique `toolArguments` global ne simule pas nécessairement correctement ces étapes.

Pour un parcours purement contrôlé, il faudrait fournir une réponse conforme pour chaque schéma de classification et des arguments source-backed comportant un nombre d'unités hors enveloppe locale. Il faut ensuite constater effectivement l'événement de frontière et le POST de création ; une configuration préparée ne vaut pas une exécution. Le cas déjà observé avec le vrai petit modèle local et le fournisseur avancé déterministe est une preuve technique plus directe que ce raccourci non exécuté.
