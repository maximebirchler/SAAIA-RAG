# Contre-revue du GC vectoriel — code et migration 072

Cette revue est **en lecture seule et ponctuelle**, pendant que l’agent propriétaire corrige et fait tester son lot. Elle ne constitue ni un résultat de tests ni une approbation de purge. Aucun service, API, SSH, build/test .NET, Git, produit ou test n’a été modifié/exécuté par le relecteur. Les empreintes observées figurent dans `gc-code-independent-review-v1.json` ; les mutations ultérieures du propriétaire nécessitent leurs propres preuves.

## Garanties cohérentes dans le code lu

- Le maximum de génération vit hors des cascades tenant/document. La migration rapproche documents, révisions, runs et payloads bornés syntaxiquement ; une saturation au-delà de `int.MaxValue` est un refus explicite. Une réservation perdue peut créer un trou, sans réutiliser une génération.
- Le manifeste est enregistré dans une transaction, sous bail de job puis verrou document, avant les upserts Qdrant. Les IDs physiques sont calculés à partir du chunk canonique et du plan ; la citation conserve l’ID canonique.
- Le retrait ne vise que `generation < documents.ingestion_version`. La génération actuelle et la publication SQL active plus ancienne restent protégées ; un plan actif legacy nul protège toute sa génération publiée. L’absence physique d’un document ne suffit pas à créer un retrait.
- Publication et retraite sont dans la même transaction ; rollback ne laisse pas un ticket exécutable indépendant de la publication. Le registre refuse la réactivation d’un fingerprint retiré. Le worker vérifie à nouveau le plan avant chaque upsert.
- Le GC a son propre bail opaque et son checkpoint exige propriétaire et échéance encore valides. Aucun verrou de document/job n’est tenu pendant le HTTP. Les tombstones et IDs restent présents après un balayage ; le curseur repart pour un recheck borné, ce qui couvre les écritures tardives sans annoncer une absence éternelle.
- Le transport lit uniquement les IDs enregistrés et vérifie tenant/document/génération/plan/chunk. Il supprime seulement les IDs relus et vérifiés, puis relit l’absence. Aucun delete par filtre documentaire/catégorie/version ni `EnsureCollection` n’a été identifié dans ce chemin.
- Les cibles historiques doivent correspondre à la cible courante exacte ou à une cible approuvée. Les credentials de la cible courante ne sont pas réutilisés pour une autre cible. Le client HTTP dédié désactive redirections, cookies et proxy, borne les réponses et propage l’annulation.
- L’activation est explicite et désactivée par défaut. Les sondes exposent IDs/compteurs/codes, sans texte de document ni credentials. Le statut d’inventaire legacy reste explicitement incomplet.

Références principales : `IngestionGenerationRegistry.cs`, `IngestionVectorRegistry.cs`, `JobRepo.Completion.cs`, `VectorGcQueue.cs`, `VectorGcQdrantTransport.cs`, `VectorGarbageCollector.cs`, `VectorGcRuntimeState.cs` et migration `072_vector_lifecycle_registry.sql`.

## Constats transmis et état exact

### Identité renvoyée par le CAS d’admission

Le schéma initial autorise un `doc_id` libre et `ResolveDocumentIdAsync` préserve l’ID d’une ligne existante. Aucun parcours UI changeant cet ID n’a été établi ; les inserts applicatifs neufs sont déterministes. Le bord reste toutefois compatible avec des imports/fixtures ou une recréation physique.

Sur le premier snapshot lu, les admissions réservaient le watermark pour l’identité A résolue une fois, puis `ON CONFLICT (tenant_id,doc_path)` pouvait renvoyer une identité B sans imposer leur égalité. La version de B pouvait alors recevoir une réservation faite pour A. Le constat a été transmis avec cette nuance au coordinateur et au propriétaire. La dernière source relue ajoute `documents.doc_id=EXCLUDED.doc_id` et réactualise l’ID à chaque retry. **Correction source observée ; aucun résultat PG de cette correction n’a été exécuté ou revendiqué ici.** Le témoin pertinent intercale le remplacement A→B entre résolution et INSERT, avec un maximum historique distinct pour B.

### Indisponibilité HTTP confondue avec incohérence d’identité

Le premier transport renvoyait null pour tous les HTTP non2xx, ensuite classés `point_identity_or_response_mismatch`. Une lecture503/401 était donc comptée comme quarantaine de payload au lieu d’indisponibilité/credentials. Aucun delete n’était envoyé : il s’agissait d’un défaut de diagnostic. Le propriétaire a depuis introduit `read_http_N`, `delete_http_N` et des raisons distinctes pour réponse/identité/JSON. Cette fermeture est visible dans la source relue ; sa validation centralisée reste distincte.

### Réponse partielle et preuve d’absence

L’autre relecteur a établi qu’une réponse206 ne peut prouver une lecture complète ni l’absence. Le dernier snapshot relu utilisait encore `IsSuccessStatusCode`. Le propriétaire préparait le RED causal et la restriction aux réponses complètes attendues. Il s’agit d’un point à fermer avant qualification, attribué à cette contre-revue parallèle, et non d’un test supplémentaire exécuté ici.

### Ajout tardif au manifeste

La migration initiale protège UPDATE/DELETE des lignes de points, mais pas un INSERT ultérieur à de nouveaux ordinaux. Le propriétaire prévoit un scellement unique du manifeste et un témoin PG. Ce constat concerne l’intégrité de l’ensemble annoncé immuable ; **aucune suppression de point actif n’a été démontrée à partir de cette ouverture**. Les contrôles d’ID dérivé, payload et génération strictement ancienne restent une barrière. Il ne faut donc pas transformer ce durcissement en affirmation non prouvée de destruction de données courantes.

## Limites et acceptation encore nécessaires

L’identité de runtime/URL/collection n’est pas une identité universelle de namespace après restauration d’une base ancienne, clone ou remplacement de stockage. La conception impose déjà un protocole distinct, GC suspendu et rapprochement du namespace. Le code désactivé par défaut et la première qualification dans une pile possédée ne remplacent pas ce protocole. Les points legacy sans manifeste exact, les cibles inconnues et les alternatives de la génération courante restent hors collecte prouvée.

Les matrices de conception v1 sont des spécifications historiques, pas des résultats. Les preuves actuelles doivent fermer séparément : CAS d’ordre et d’identité, scellement/immutabilité, rollback publication/retrait, protection de la publication ancienne pendant échec, perte/reprise de bail GC, réponses HTTP partielles/timeout/ACK non terminal, ancienne cible et credentials, réapparition tardive et nouveau plan témoin conservé. PostgreSQL réel et Qdrant réel isolé restent nécessaires avant activation du GC géré.

La première ingestion Linux annoncée par le coordinateur utilise un checkpoint antérieur du code, en Development non signé. Elle peut diagnostiquer le runtime de déploiement et l’ingestion, mais ne qualifie ni la nouvelle migration/GC ni la Production. Cette distinction doit rester dans le rapport final.
