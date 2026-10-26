# GC produit — contre-revue finale ciblée des sources gelées

État : **constats intermédiaires fermés statiquement ; résultats GREEN centralisés encore distincts**. Cette revue n’exécute aucun .NET, service, API ou Git et ne modifie ni produit ni tests. Elle ne prétend pas couvrir tout le produit ou approuver l’activation du GC en production.

## Ancrage exact

Le registre `gc-product-final-review-evidence-v1.json` vérifie **18/18 fichiers conformes** au gel `gc-source-freeze-v1.json`, agrégat annoncé **`c5c5cf08f30a4f4d2febbae7e8dd390be3c51e1002152aafdf3c112181981732`**. La portée principale est transport/queue/collecteur/registre/état/options/DI ; migration, publication et passages du worker ont été rapprochés pour vérifier leurs interactions. Les preuves des versions intermédiaires demeurent dans `gc-product-review-initial-findings.md`.

| Surface principale | SHA256 final |
| --- | --- |
| `VectorGcQdrantTransport.cs` | `8c33a2275f80b703e4a4d2554bb97e4002f53364cc988f9eccc572e0274619aa` |
| `VectorGcQueue.cs` | `876a082906679497f8f3971b259bef6b51ef5180e51df9982f5da109dc065574` |
| `VectorGarbageCollector.cs` | `6b6d245a827b7ef1d1897fe197ba8761295f9116362878470461d969a9ec8e17` |
| `IngestionVectorRegistry.cs` | `0db3498791377dac52389b305e515f6d3771431ee71c832a953d9f654622e237` |
| `VectorGcRuntimeState.cs` | `833641121dbf15a73a9ffc7bb2227a14499d60f87667b2a2cc1a5f3783f4d59c` |
| `VectorGcOptions.cs` | `f069c0fa726e92ddb371f47307df4c7dc8132e45e39c9896d5c3035b6e3436fb` |

## Résolution des constats

**R01 — Lecture partielle assimilée à une absence.** Le coordinateur a capturé le RED causal : `tests/gc-partial-http-red-v1/manifest.json`, run `7336228aff31433587465a015710f8c6`, **4 échecs, sources stables**. Il couvre HTTP206/202 sur lecture initiale et readback. Le transport final exige **HTTP200** sur ces lectures et sur DELETE, puis conserve les contrôles JSON/identité/ACK `completed`. Une autre réussite HTTP ne suffit plus à établir une absence. Le test supplémentaire du DELETE202 refuse aussi une réponse présentant un corps plausible.

**R02 — Manifeste invalide hors traitement par plan.** `ReadBatchAsync` est maintenant à l’intérieur du `try` par plan. Son refus devient `manifest_incomplete`, checkpoint possédé sans progression, backoff et absence de DELETE. Le témoin PG ajouté retire un point uniquement par une corruption explicitement injectée dans sa fixture isolée : il ne prétend pas qu’un parcours normal contourne les barrières SQL. La migration scelle également le manifeste une seule fois, avec cardinalité vérifiée ; après scellement les INSERT supplémentaires sont interdits, tout comme UPDATE/DELETE des points.

**R03 — Faux zéros de classification.** L’inventaire distingue désormais **transport, protocole et quarantaine d’identité/manifeste**. Les codes de réponse invalide, JSON invalide, ACK incomplet ou absence non vérifiée sont classés. Le compteur `ProtocolFailures` figure aussi dans le log d’inventaire et dans l’état publié. Un manifeste non scellé est explicitement compté comme quarantaine et n’est pas réclamé par le GC.

Ces trois fermetures sont vérifiées dans les sources. Le GREEN des tests ajoutés et les captures PostgreSQL/Qdrant réelles ne sont pas inférés de leur seule présence.

## Propriétés de sécurité cohérentes dans le périmètre relu

La retraite ne vise que les générations strictement anciennes et protège la publication active plus ancienne ; si son plan actif legacy est inconnu, toute sa génération publiée est protégée. La génération courante et ses alternatives ne sont pas purgées. L’inscription du manifeste précède l’upsert, sous bail de job et verrou document ; publication et retraite sont dans une seule transaction. Les marqueurs retirés ne sont pas réactivés par le registre ou la publication.

Les IDs physiques distinguent les plans et les générations ; les citations gardent les IDs canoniques. Le GC ne supprime que les IDs de son manifeste après contrôle du payload tenant/document/génération/fingerprint/chunk. Il n’emploie ni delete large par filtre ni création d’une collection manquante. ACK et lecture d’absence restent deux faits séparés.

Le bail GC utilise un owner opaque renouvelé lors de la claim ; le checkpoint exige owner et échéance encore valides. Une réponse tardive d’un ancien collecteur ne peut acquitter le travail de son successeur. Le HTTP se déroule sans verrou SQL de job/document. Les bords timeout/arrêt ne progressent pas le curseur sans vérification. Les tombstones et listes exactes sont conservés après balayage, avec recheck périodique : une écriture tardive peut donc être retrouvée, sans annoncer une absence éternelle.

Une ancienne cible doit correspondre à un descripteur approuvé exact ; elle ne récupère ni la destination ni la clé courante. Le client dédié supprime auth résiduelle, interdit redirect/proxy/cookies et borne les corps JSON à 1MiB. Les logs du lot exposent IDs, compteurs et codes, sans réponse HTTP, texte source ou credential. Les options bornent nombre de plans, taille de lot, durée de lot/ronde et finalisation ; l’activation reste désactivée par défaut.

## Limites explicites

- **Une absence est observée via l’API Qdrant**, pas une preuve d’effacement physique des octets après compaction ni une attestation universelle de toutes les répliques. Les rechecks restent nécessaires face aux écritures tardives.
- Les points legacy sans manifeste exact restent hors inventaire exhaustif ; `/ready` conserve explicitement `vector_gc_legacy_inventory_complete=false`. Les cibles non approuvées, plans non scellés et alternatives de la génération courante ne sont pas faussement annoncés comme purgés.
- Une ligne de document physiquement absente ne suffit pas à créer une nouvelle retraite : le lot reste conservateur sur ce bord de maintenance. Les tests de suppression/recréation protègent les identités ; ils ne prouvent pas une collecte exhaustive de tous les anciens stockages.
- URL/collection/révision ne représentent pas une identité universelle de namespace après clone, restauration ancienne ou remplacement de stockage. Ces opérations demandent le protocole séparé déjà décrit, GC suspendu et namespace rapproché. La simple égalité de chaîne ne suffit pas.
- `SecretRefResolver` lit `FILE:` synchroniquement. La borne complète suppose des références locales et petites ; un I/O UNC/NFS bloqué n’est pas rendu annulable par le seul token HTTP. Aucune référence/clé réelle n’a été lue pour ce constat.
- Le CAS de génération/identité possède ses témoins distincts et sa contre-revue parallèle. L’ancrage de ses fichiers n’équivaut pas à une exécution de tests par ce relecteur.

**Aucun nouveau défaut matériel n’a été identifié sur le snapshot gelé dans cette portée.** La décision d’acceptation doit rapprocher ses GREEN centralisés, sa DLL réellement exécutée et les captures de purge Qdrant possédée. Une ingestion Linux réussie sur un ancien binaire ou un diagnostic Development non signé ne valide pas automatiquement ce nouveau GC ni la Production, et aucun résultat de planning/LLM n’est déduit ici.
