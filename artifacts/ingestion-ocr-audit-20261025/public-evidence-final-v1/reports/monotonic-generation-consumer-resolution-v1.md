# Générations abandonnées : correctif préparé des consommateurs

Le RED centralisé v2 a atteint la logique produit : deux échecs causaux
(annulation classée en backlog, échec sans diagnostic distinct), trois
contre-contrôles réussis. La première capture v1 avait seulement détecté une
fixture invalide ; elle ne prouve pas un défaut du produit.

Les trois consommateurs de l'audit sont corrigés et gelés pour le GREEN
centralisé. Aucun build, service, requête ou test .NET n'a été lancé par l'agent
chargé du correctif.

## Classification

Les deux lecteurs backend partagent maintenant les observations SQL et la
classification dans `IngestionGenerationDiagnostics`, défini avec le store
de capacité A. L'observation regarde le dernier job portant la génération
allouée courante, les jobs réellement actifs, la publication précédente et
les métadonnées ou hash de checkpoint effectivement disponibles.

- Une génération avec job actif reste signalée et bloque l'enqueue concurrent.
- Une génération annulée et réglée ne suffit plus à créer un candidat de
  réindexation. Les raisons `ingestion_generation_abandoned` et
  `source_freshness_unverified` restent dans les `ReasonCounts` de la surface
  de diagnostic existante, même si `CandidateCount` vaut zéro.
- Un échec conserve `ingestion_attempt_failed`. L'absence de publication garde
  aussi `never_indexed` et les blocages de politique existants.
- Une génération sans job correspondant reste explicitement
  `ingestion_generation_unresolved` ; elle n'est pas traitée comme une
  annulation connue.
- Une différence de taille/date observée, ou de hash de checkpoint par rapport
  à la publication, conserve `indexed_version_outdated`, y compris après une
  annulation. Cet avertissement ne réactive pas à lui seul un candidat annulé.

Le script qualité conserve son check `version_gap_no_active` mais distingue
quatre métriques. `abandoned_source_unverified` et `failed_attempt` sont des
avertissements explicites ; `observed_source_changed` et
`unresolved_generation` restent des erreurs. La politique indépendante
`StrictFailedJobs` et le contrôle des vrais jobs actifs restent présents.

## Fraîcheur et limites

Aucun fichier documentaire n'est relu par les diagnostics. L'égalité de
`documents.content_hash` avec le hash de révision n'est jamais utilisée pour
attester le fichier actuel. Un checkpoint décrit une observation de la
tentative, pas une nouvelle vérification du disque. L'absence de différence
observée reste donc une source courante non revérifiée.

Le client WinUI n'a reçu aucun changement : sa classification active/terminale
repose déjà sur le statut du job. L'audit statique ne remplace pas une nouvelle
validation native du client.

## Validation à rattacher

Le filtre `FullyQualifiedName~MonotonicIngestionConsumersIntegrationTests`
contient désormais sept cas PG : annulation, queued, running, échec, source
observée modifiée après annulation, génération inexpliquée sans job et échec
sans publication valide. Le test qualité exécute le SELECT du vrai script ;
les deux métriques d'avertissement sont distinguées des erreurs, sans effacer
leurs lignes de diagnostic. Les fixtures vérifient leurs quatre tables de
texte, checksums UTF8, pages et offsets avant les assertions métier.

À ce stade, le GREEN centralisé de ce lot est encore attendu. Les preuves RED
et les anciens rapports ne sont pas réécrits. Le statut produit demeure
`TESTE_NON_APPROUVE`.
