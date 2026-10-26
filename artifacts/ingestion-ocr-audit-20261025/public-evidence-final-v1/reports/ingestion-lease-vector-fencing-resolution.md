# Clôture des baux et générations vectorielles

État de ce compte rendu : correction implémentée et gelée ; compilation et tests orchestrés exclusivement par root. Les cinq cas RED initiaux ont échoué comme attendu avant la correction. Les nouveaux cas supplémentaires ne sont pas présentés comme des RED indépendamment capturés. Aucun service, modèle, appel réseau, test .NET ou opération Git lancé par cet agent.

## Défauts établis

1. Le heartbeat ignorait une mise à jour affectant zéro ligne et absorbait ses exceptions. Après reprise d’un travail stale par B, A pouvait continuer. Les transactions de publication contrôlaient `running`, mais pas le propriétaire du bail. Les publications dégradées pouvaient aussi modifier le document et le travail de B.
2. Les points Qdrant utilisaient l’identité du chunk canonique. Un appel A déjà émis pouvait donc remplacer les points de B après son commit, même avec une vérification du bail juste avant HTTP. Un marqueur de plan stocké seulement dans le payload n’empêchait pas cet écrasement.
3. Les suppressions vectorielles larges de prépurge, nettoyage et suppression documentaire pouvaient atteindre une génération publiée entre-temps. Remettre les numéros de version à zéro après une suppression, ou les faire reculer après une annulation, autorisait aussi la réutilisation des identités de source.

## Invariants du correctif

- `TryDequeueAsync` génère un nouveau `locked_by` opaque, composé du consumer et d’un UUID. Il ne dépend pas du compteur `attempts`, réinitialisable lors d’une reprise administrative. `IngestionJob.LeaseOwner` transporte cette valeur exacte.
- Le renouvellement affectant zéro ligne arrête l’opération et l’attend. Une exception de renouvellement est traitée de même. La décision finale est prise après observation du dernier renouvellement, et une annulation extérieure reste respectée même si l’opération l’ignore. Une publication propre peut avoir effacé son verrou avant le retour du wrapper : elle n’est reconnue que par son `executionLeaseOwner` persistant et son état terminal `done`/`failed`. Un nouveau dequeue remplace ce token.
- Les publications upsert, delete, OCR échoué et récupération non publiable exigent un bail explicite correspondant, verrouillé dans leur transaction. L’absence de bail ne constitue pas une voie de compatibilité permissive. Le tenant et le chemin sont également rapprochés.
- Les transitions worker vers failed/defer/cancel/superseded sont protégées sous le même verrou et la même transaction. La stabilisation du document après annulation est incluse dans cette transaction ; elle n’est plus exécutée séparément par un ancien worker. Progression et checkpoint utilisent un `WHERE locked_by` atomique. La recherche de références n’a trouvé aucun appel administratif justifiant un bypass sans bail de ces méthodes : les valeurs nulles, vides ou blanches sont donc refusées. Le dernier helper public de stabilisation directe, uniquement appelé par un ancien test, a été supprimé ; sa fonction privée ne peut être appelée qu’au sein de la transaction d’annulation protégée.
- La version ne recule plus après delete ou annulation, y compris dans la commande administrative. La suppression conserve la dernière version d’ingestion et remet seulement la version indexée à zéro ; une réapparition reçoit une version supérieure.
- Le fingerprint du plan couvre les entrées projetées et effectives, la cible, le modèle et ses révisions, le code, ainsi que tenant/document/chemin/catégorie/version. L’identité canonique SQL des chunks, des citations et des liens reste inchangée.
- Le point Qdrant est identifié par les 16 premiers octets du SHA256 UTF-8 de `qdrant|{canonicalChunkId:N}|{planFingerprint}`, interprétés comme un `Guid` .NET. Son `payload.chunk_id` reste l’identité canonique SQL. Des plans distincts ne peuvent plus se remplacer ; la reprise du même plan reste idempotente.
- La migration 071 ajoute `documents.ingestion_vector_plan_fingerprint`. Ce plan devient actif dans la transaction de publication. L’unique filtre dense rapproche ensuite le plan du payload de celui du document actif. `NULL` SQL accepte seulement un payload legacy `NULL`, jamais un nouveau plan quelconque.
- La vérification finale Qdrant contrôle séparément l’UUID physique du point, son chunk canonique et son fingerprint. Elle ne confond plus ces deux identités.

## Tests préparés et entretien

Le RED initial couvre quatre publications A après reprise par B — normale, non publiable, OCR échoué, delete — et l’annulation/observation de l’opération après heartbeat à zéro ligne. Les régressions ajoutées couvrent les six autres mutations worker, les versions delete/cancel et leur réapparition, le renouvellement du token même après remise à zéro d’`attempts`, une erreur de heartbeat pendant le retour d’un succès, la confidentialité de l’erreur, la cancellation extérieure, les points de plans concurrents et le filtrage legacy strict.

Les fixtures de publication existantes fournissent désormais leur bail réel `test-worker`, correspondant à leur seed. L’ancien test qui exigeait la remise de version à zéro est aligné sur la conservation de la génération 3. Les méthodes sans appelant produit `MarkDoneAsync`/`TouchAsync`, les trois méthodes de suppression Qdrant larges et le prédicat de prépurge ont été retirés après recherche de références ; leurs sept tests qui reflétaient exclusivement ce comportement supprimé ne sont pas comptés comme des protections encore actives.

Une contre-revue a ensuite isolé l’attente du heartbeat sur le verrou détenu par sa propre publication. Root a capturé ce RED : délai PostgreSQL d’une seconde et verrou conservé jusqu’au retour du renouvellement, **un échec sur le propriétaire intact et un témoin PASS rejetant le successeur**. Le renouvellement utilise désormais `FOR UPDATE SKIP LOCKED`, puis une lecture MVCC du propriétaire exact. Lorsque la transaction protège déjà la ligne, ce témoin évite l’auto-blocage sans prétendre avoir prolongé le TTL. La sonde `ingestion.lease_renewal` distingue `Renewed`, `OwnerObservedRenewalDeferred` et `OwnTerminal`, avec `ttl_renewed` explicite. Le commit final reste clôturé sous verrou.

L’ancien test de stabilisation a également été renforcé : une vraie révision 3 est d’abord publiée, l’ingestion 5 est annulée par le chemin worker protégé, puis le test exige que la révision et la version indexée 3 restent identiques, tout en conservant le compteur d’ingestion 5. Les gardes null/vide/blanc comparent le snapshot complet du travail et du document avant/après leurs appels refusés.

## Limites explicites

Aucune suppression vectorielle large n’est conservée dans ce worker. Les anciennes générations et les écritures tardives deviennent des points inactifs filtrés ; leur collecte physique sûre reste un travail distinct. Leur accumulation peut affecter le rappel avant postfiltrage : l’overfetch borné et sa dégradation observable sont traités séparément par l’agent retrieval. Cette correction ne promet donc ni un stockage sans orphelins ni un rappel illimité.

La vérification de présence des points atteste leur identité et leur plan, pas la pertinence sémantique des embeddings. Les résultats des moteurs OCR réels et de la campagne d’ingestion doivent rester rattachés à leurs propres preuves ; les quatorze faits du PDF précédent ne valident pas à eux seuls toute l’ingestion.

Résultats GREEN et manifeste final : à rattacher après retour des exécutions root ; aucune réussite non reçue n’est anticipée ici.
