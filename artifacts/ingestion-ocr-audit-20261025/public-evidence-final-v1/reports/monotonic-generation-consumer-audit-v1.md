# Génération monotone après annulation : audit des consommateurs

Revue des sources uniquement, puis préparation de cinq tests IntegrationPg.
Aucun service, requête SQL/HTTP, test .NET, build ou opération Git exécuté par
l'agent de cette revue. Les conclusions de comportement ci-dessous reposent
sur les callers lus ; les nouveaux tests attendent la capture centralisée.

## Impact réel identifié

`ingestion_version` reste désormais monotone : après l'abandon de la génération
5, la publication valide peut rester à 3. Cet écart ne prouve pas qu'un travail
est encore en cours.

Trois consommateurs utilisent encore cette ancienne déduction :

- `RuntimeCapabilityAEnrichmentStore.BuildReasons` ajoute
  `indexed_version_outdated` au seul test `IngestionVersion > IndexedVersion`.
  `LoadCandidatesAsync` transforme alors le document en candidat avec
  `RecommendedAction=enqueue_reindex`. Le service de commande réutilise cette
  liste et le coordinateur peut remettre en file le document lors d'une
  campagne explicitement demandée ; le contrôle d'absence de job actif ne
  supprime pas le faux candidat. Aucun ré-enqueue autonome n'est établi ici.
- `RuntimeCapabilityDiagnosticsBuilder.BuildCapabilityAReasons` fait la même
  déduction. Son résumé opérationnel compte le document dans `CandidateCount`
  et `ReadyToEnqueueCount` si aucun job n'est actif et si l'auto-ingestion n'est
  pas suspendue. Les pages de diagnostic et artifacts de capacité A exposent
  donc un faux backlog.
- `tools/run-ingestion-quality-checks.ps1` sélectionne
  `version_gap_no_active` pour tout document indexé ayant cet écart, puis le
  transforme inconditionnellement en erreur qualité. Une annulation réglée
  suffit à déclencher un faux négatif.

Callers principaux :
`RuntimeCapabilityAdminReadService.GetCapabilityAEnrichmentCandidatesAsync`,
`RuntimeCapabilityAEnrichmentCommandService.EnqueueCapabilityAEnrichmentAsync`,
`RuntimeCapabilityAEnrichmentCoordinator.EnqueueAsync`,
`RuntimeCapabilityDiagnosticsBuilder.LoadCapabilityAOperationalSummaryAsync`.
Les routes sont les surfaces admin de candidats/enqueue/diagnostics de la
capacité `capability_a.corpus_enrichment`.

## Risque WinUI « encore en cours » non confirmé

Le parsing d'`AdminJobsPanel.Content.cs` conserve le statut fourni. Dans
`AdminJobsPanel.Models.cs`, `IsTerminal`, `IsRunning`, `IsQueued` et `IsPaused`
se fondent sur le statut du job. Les versions ne servent qu'à la ligne
d'information `BuildAdminJobDocumentVersionsLine` et à la signature de rendu.
Les contrôles de pause concernent la première ingestion (`indexedVersion<=0`),
pas un écart 5/3. La projection backend `AdminJobsQuerySupport` utilise aussi
le statut et les indicateurs explicites de contrôle.

Le catalogue utilisateur filtre les documents indexés ; les recherches et
lectures canoniques suivent la révision publiée par `indexed_version`. Le
filtre dense compare le payload à la version d'ingestion de cette révision
publiée et au plan actif, pas à la dernière génération demandée du document.
Le scanner ne lit même pas `documents.ingestion_version` : il décide à partir
du statut, des métadonnées de fichier, du fingerprint de pipeline et des vrais
jobs actifs. Aucun faux busy/requeue au seul écart n'a été trouvé dans ces
chemins lors de cette revue statique.

## Limite de fraîcheur à conserver

`EnqueueUpsertAsync` met à jour taille/date de fichier mais conserve le hash
de contenu publié du document. L'égalité de `documents.content_hash` avec le
hash de révision ne prouve donc pas l'égalité du fichier courant. Le checkpoint
`payload.resume.sourceHash` et `fileSize` constitue une observation de la
tentative ; ce n'est pas une nouvelle lecture du disque au moment du diagnostic.

Une génération annulée peut être décrite comme abandonnée avec source courante
non revérifiée. Une différence de hash observée par la tentative, ou de
métadonnées source par rapport à la publication, doit garder son avertissement
de fraîcheur. Un échec doit garder un diagnostic explicite distinct du simple
travail pending. Aucun nouvel I/O documentaire n'est requis pour afficher
honnêtement cette incertitude.

## Tests RED préparés

`MonotonicIngestionConsumersIntegrationTests` utilise une publication complète
à 3 et une génération 5, dans la fixture PostgreSQL dédiée existante. Les
tests appellent les vrais deux lecteurs de capacité A et exécutent le vrai
SELECT de gap extrait du script qualité, sans réécrire son SQL dans la fixture.

Les cinq cas sont : annulation sans checkpoint de fraîcheur (aucun backlog ni
reindex automatique), queued, running, échec avec diagnostic distinct, puis
annulation avec hash source observé différent et taille modifiée (avertissement
de données publiées anciennes conservé). Les tests de métadonnées ne prétendent
pas exécuter l'ingestion d'un PDF ou vérifier son contenu physique.

Filtre centralisé :
`FullyQualifiedName~MonotonicIngestionConsumersIntegrationTests`, profil
`IntegrationPg`. Aucun correctif des trois consommateurs n'est appliqué avant
la capture RED et la coordination de l'orchestrateur.
