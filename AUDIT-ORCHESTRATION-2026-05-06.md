# Audit orchestration SAAIA - 2026-05-06

> Workspace : `C:\Users\MBirchler\Desktop\ecom\SAAIA\GIT\RAG`
> Role : document central vivant pour les audits ingestion, OCR, profils, content cards, resumes, RAG, client multilingue et runtime serveur.
> Regle de collaboration : ne pas reverter le worktree, ne pas toucher aux fichiers modifies par autrui. Cette passe ne modifie que ce fichier.

## Resume executif

Le repo est en chantier actif : beaucoup de fichiers backend, client, infra et tests sont modifies ou non suivis. Cette orchestration traite donc l'etat courant comme un WIP utilisateur et ne declare pas de baseline stable sans relance des tests.

Les decisions produit a maintenir sont claires :

- pas de hardcoding de categorie, nom de PDF, recette, corpus client ou exemple documentaire dans le code produit ;
- les categories sont des donnees issues de l'arborescence/document catalog, pas des branches de code ;
- le backend doit traiter la langue du document, y compris quand elle differe de la langue UI ;
- le client UI/chat est limite aux langues `fr`, `en`, `es`, `pt`, `de`, `it` ;
- le LLM serveur sert l'ingestion/backoffice, notamment les resumes et enrichissements documentaires, mais ne sert pas le chat client final ;
- le chat client reste genere par le LLM local embarque cote Windows.

Constat important : `README.md` contient encore une formulation historique "serveur sans LLM / aucun LLM cote serveur". Elle doit etre alignee avec la decision actuelle : "pas de chat client cote serveur", mais LLM serveur autorise pour ingestion/backoffice. Tant que cette doc reste ambigue, elle peut induire de mauvaises corrections.

Etat technique observe :

- ingestion : pipeline structurel PDF -> pages -> sections -> units -> retrieval chunks -> exact/contextual entries -> embeddings/Qdrant, avec reprise checkpoint, bulkheads TEI/Qdrant et timings ;
- OCR : extraction native, OCR complet si texte faible, OCR page-image, detection de langue OCR, diagnostics page/document, score de confiance et penalite douce RAG ;
- profils/content cards : profil deterministe puis enrichissement LLM backoffice avec grounding strict ; les cartes sont limitees, dedupliquees et renvoyees au client via `matchedContentCards` ;
- resumes : generation backoffice Capability B, stockage lie a la langue document, fallback deterministe, traduction d'affichage possible cote client ;
- RAG : retrieval hybride dense/sparse/exact/contextual, qualite extraction exposee au client et reranking par penalite de confiance ;
- client multilingue : UI et router limites a six langues, avec detection/alias, prompts writer et traductions de resumes stockes ;
- runtime serveur : worker Capability B cadence par inactivite ingestion, auto-enqueue, lease stale, capacity plan LLM separe.

Ajout Raman a prioriser : le risque le plus critique est une course Capability B entre generation de resume et complete. Si le document est reindexe entre les deux, un resume genere sur la revision N peut etre stocke avec le hash courant N+1 et paraitre frais. Les autres points Raman montent en P1 : content cards a couverture zero, catches silencieux du profile search, mojibake/multilingue, et index exact/composite.

Ajout Fermat a prioriser cote server/runtime/deploy : les principaux risques P1 sont la config prod Linux potentiellement signee avec placeholder `__LICENSE_SEATS__`, le capacity planner LLM qui annonce plus de capacite que le backend n'utilise reellement, la readiness LLM backoffice non bloquante, et un `sudo chown -R` remote sans garde forte.

Ajout Lagrange cote client/RAG : aucun P0 client/RAG confirme. Les risques P1 sont la perte de metadata source (`sourceHash`, qualite extraction, content cards, langues) entre les hits RAG et le payload final UI, et le fait que `sourceHash` de resume puisse encore etre accepte depuis client/admin au lieu d'etre strictement server-owned.

Relecture active des rapports `out/agents` du 2026-05-06 matin : Raman, Lagrange et Fermat ne confirment plus de P0 technique ouvert dans le worktree courant. Plusieurs sujets critiques semblent corriges ou partiellement corriges par d'autres agents, mais restent en statut "corrige non verifie" tant que les tests cibles et validations ops n'ont pas ete lances.

## Orchestration active

Perimetre d'ownership courant :

- fichiers autorises : `AUDIT-ORCHESTRATION-2026-05-06.md` et, si necessaire, futurs rapports sous `out/agents/*` ;
- fichiers interdits dans cette mission : code produit backend/client/infra, migrations, tests, manifests et docs hors orchestration ;
- aucun revert de changements d'autres agents ou de Codex ;
- aucune execution build/test tant qu'elle n'est pas demandee explicitement dans une passe de validation.

Invariants non negociables :

- pas de hardcoding contenu/PDF/categorie/recette/corpus dans les chemins produit ou prompts deterministes ;
- backend document-driven : la langue de reference est la langue du document, y compris hors six langues UI ;
- client UI/chat limite a `fr`, `en`, `es`, `pt`, `de`, `it` ;
- LLM serveur reserve au backoffice ingestion/enrichissement/resumes, jamais au chat client final ;
- enrichissements longs uniquement quand l'ingestion est idle ou explicitement arbitree comme non concurrente.

Snapshot prioritaire cross-domaine :

| Priorite | Domaine | Point actif | Prochain signal de cloture |
| --- | --- | --- | --- |
| P0 | LLM serveur / resumes | Capability B revision/hash semble corrige, non verifie | Test course N/N+1 + atomicite resume/job |
| P0 | Produit/runtime | LLM serveur backoffice seulement, pas chat client | Docs/runbooks alignes et aucun endpoint chat serveur client |
| P0 | Ingestion/client | `docLanguage` backend ne doit jamais venir de la langue UI | Test stored summary/admin submit avec langue document distincte |
| P0 | Generalisation | Aucun hardcoding PDF/categorie/recette/corpus produit | Grep/lint prompts, manifests et chemins deterministes |
| P1 | Retrieval/content cards | Filtrer cartes a couverture zero et pages trompeuses | Test `matchedContentCards` hors overlap |
| P1 | Retrieval/DB | Profile search, exact index et NUL summaries/API a verrouiller | Plan SQL + tests NUL PostgreSQL |
| P1 | Multilingue/OCR | OCR image-page budgete/observable et langues hors six UI | Diagnostics pages OCR sautees + fixtures langues declarees |
| P1 | Runtime/deploy | Backoffice flag, capacity LLM, routing instances, readiness modele | Env example + effective slots + probe modele configure |
| P1 | Client/RAG | `sources.resolve`, live summary et heuristiques corpus-specific | Payload enrichi + lint hardcoding + templates multilingues |
| P2 | Exploitabilite | Artefacts, OCR readiness, reindex admin, Dockerignore, idle tenant-aware | Contrats et garde-fous ops documentes/testes |

## Integration rapports agents

Les rapports agents doivent etre integres ici avant toute passe code, puis references vers les tableaux P0/P1/P2, TODO et tests. Les chemins cites par les agents sont des indices d'audit : ils doivent etre reverifies au moment de corriger.

| Agent | Zone principale | Statut integration | Priorite issue | Slots a maintenir |
| --- | --- | --- | --- | --- |
| Raman | Backend ingestion/OCR/Capability B/retrieval | Relu; P0 indique corrige non verifie | P1 NUL summaries/API, OCR image-page, atomicite profile/cards | Registre actif R-001 a R-010, tests Raman |
| Lagrange | Client/RAG/resumes metadata | Relu; RAG principal et hash resume semblent durcis | P1 `sources.resolve`, `rag.summarize_live`, hardcoding heuristique | Registre actif R-011 a R-017, tests Lagrange |
| Fermat | Server/runtime/deploy | Relu; P0 non confirme, P1 ops/deploy ouverts | P1 activation backoffice, slots/instances, readiness modele, `.dockerignore` | Registre actif R-018 a R-027, tests Fermat |

Template pour nouveau rapport agent :

| Champ | Contenu attendu |
| --- | --- |
| Agent | Nom et domaine |
| P0 confirme | Oui/non, avec chemin et scenario reproductible |
| P1/P2 | Liste courte, risque utilisateur/ops, chemins concernes |
| Recommandation | Action concrete, pas seulement diagnostic |
| Tests manquants | Test minimal qui ferme le risque |
| Impact invariants | Hardcoding, langue document, six langues UI, LLM serveur, ingestion idle |

## Etat worktree relu

Date de relecture : 2026-05-06, rapports agents les plus recents sous `out/agents/`.

Rapports lus :

- `out/agents/fermat-server-deploy-runtime.md` ;
- `out/agents/raman-backend-ingestion-ocr-db.md` ;
- `out/agents/lagrange-client-rag-multilingual.md`.

Constats worktree :

- worktree tres charge : backend, client, contrats, infra et tests ont des modifications en cours ; elles appartiennent aux autres agents/Codex et ne sont pas modifiees ici ;
- `.dockerignore` racine est present mais non suivi, ce qui reste un risque deploy tant qu'il n'est pas versionne ;
- `out/` est non suivi et contient les rapports agents ; ce document central les considere comme sources d'audit ;
- plusieurs sujets anciennement P0/P1 paraissent traites dans le code courant (`CapabilityB` hash/revision, metadata RAG principal, submit summary expected hash, deploy chown, garde `/admin/reindex`), mais aucun build/test n'a ete lance par l'orchestrateur.

## Registre actif des gaps

Statuts :

- `Ouvert` : risque encore confirme par rapport agent ou worktree ;
- `Corrige non verifie` : le rapport agent indique une correction presente dans le worktree, mais les tests/validations n'ont pas ete lances ici ;
- `A surveiller` : pas bloquant immediat, mais invariant a garder dans les lint/tests.

### P0 actifs

| ID | Statut | Gap | Owner | Risque | Next action verifiable |
| --- | --- | --- | --- | --- | --- |
| R-001 | Corrige non verifie | Capability B sourceHash/revision race | Backend runtime | Resume N stocke comme frais N+1 si regression | Lancer test course N/N+1 + atomicite resume/job ; verifier complete stale refuse/marque stale |
| R-002 | A surveiller | LLM serveur backoffice uniquement | Runtime + produit | Glissement vers chat client serveur | Grep endpoints/docs/runbooks ; confirmer que chat client final reste local |
| R-003 | A surveiller | Langue document distincte langue UI | Backend + client | Traduction involontaire du corpus, metadata fausses | Test doc langue hors UI active ; `docLanguage` vient du document/profil, pas du router UI |
| R-004 | A surveiller | Aucun hardcoding contenu/PDF/categorie/recette | Client + backend + QA | Produit specialise corpus historique | Lint prompts/manifests/heuristiques/migrations ; qualifier tout `PDF\d+`, recette, categorie concrete |
| R-005 | A surveiller | Enrichissements lourds hors ingestion | Backend runtime | LLM/OCR/TEI vole ressources ingestion | Test ingestion active bloque/differe Capability B et profils LLM |

### P1 ouverts

| ID | Statut | Gap | Owner | Risque | Next action verifiable |
| --- | --- | --- | --- | --- | --- |
| R-006 | Ouvert | NUL UTF-8/PostgreSQL dans summaries backoffice/API | Backend summary | Erreur Npgsql ou job bloque sur `summary_text`/`summary_meta` | Sanitizer DB texte/json commun + tests `CapabilityBCompleteAsync` et `SubmitSummaryAsync` avec `\0` |
| R-007 | Ouvert | OCR image-page limite aux premieres pages image sans signal pages sautees | Backend OCR/ingestion | Texte de schemas/captures apres budget absent sans diagnostic clair | Exposer candidates/tentees/appliquees/sautees + test > `OcrImagePageMaxPages` |
| R-008 | Ouvert | Profil LLM + content cards persistables hors transaction apres resume done | Backend foundation/runtime | Profil/cartes LLM partiels alors que job resume est termine | Transaction profil+cards ou statut partiel explicite + test echec entre purge et insert |
| R-009 | Ouvert | `sources.resolve` pauvre en metadata | Backend RAG + contrats + client | Source explicite perd hash/langue/qualite/cards | Etendre `ResolvedSourceDto`, endpoint et mapping client ; test `BuildSourceResolveAnswer` enrichi |
| R-010 | Ouvert | `rag.summarize_live` ignore metadata et requetes multilingues | Client ToolAgent | Live summary moins tracable, retrieval lexical faible hors anglais | Porter `SourceRef` enrichi dans chunks/anchors + templates multilingues + tests DE/ES/PT/IT |
| R-011 | Ouvert | Heuristiques deterministes corpus-specific | Client ToolAgent + QA | Ranking/reponse biaises par `PDF34`, PDF wording, ingredients/preparation/froid | Remplacer par metadata generiques/configurees + lint hardcoding runtime |
| R-012 | Ouvert | Backoffice LLM peut rester desactive malgre URL/modele configures | Infra/runtime | Operateur croit utiliser serveur LLM mais fallback client/admin | Ajouter `BACKOFFICE_LLM_ENABLED` dans env examples + recommandation `/ready`/admin |
| R-013 | Ouvert | Slots LLM annonces non consommes par Capability B | Runtime LLM | Capacity plan affiche debit non reel, worker mono-slot | `CapabilityBWorkerConcurrency` ou plan cape ; exposer `effectiveBackofficeSlots` |
| R-014 | Ouvert | Instances LLM multiples non routees par backend | Infra/runtime | Containers inutilises, faux debit, ressources gaspillees | Borner instances=1 sans gateway ou generer gateway `saaia-llama` |
| R-015 | Ouvert | Readiness/probe/execution LLM n'utilisent pas le meme modele | Runtime readiness | Ready vert mais modele configure invalide, probe faux negatif | Probe avec `ChatOptions.LlmModel`; parser `/v1/models` ou mini completion modele configure |
| R-016 | Ouvert | Root `.dockerignore` non tracke | Infra/deploy | Build prod `context: ..` peut envoyer docs/modeles/secrets en CI/serveur | Versionner/valider `.dockerignore`; check deploy si absent/non suivi |

### P2 ouverts

| ID | Statut | Gap | Owner | Risque | Next action verifiable |
| --- | --- | --- | --- | --- | --- |
| R-017 | Ouvert | Duplications legacy content cards par canonicalisation divergente | Backend DB/foundation | Cartes equivalentes accent/non-accent remontent jusqu'a reindex | Canonicalisation unique runtime/backfill + migration dedup generique + test DB |
| R-018 | Ouvert | Matrice langue document inegale OCR/detection/profil/resume | Backend OCR/profile/summary | Langues OCR supportees finissent `und` ou metadata instable | Aligner langues declarees + signal incertitude + tests langues supportees |
| R-019 | Ouvert | Batch embeddings/config dynamique non garantie | Backend ingestion/runtime | Ops pense changer batch sans redemarrage ; bulkheads figes | Source unique effective settings + test 32 -> 16 si objectif dynamique |
| R-020 | Ouvert | Politique jobs stale divergente worker/scanner | Backend ingestion | Meme job stale devient queued ou failed selon timing | Fonction partagee stale + tests running/cancel/pause/snapshot |
| R-021 | Ouvert | Source cards UI portent metadata mais ne l'affichent pas | Client UI | Verification utilisateur pauvre malgre payload enrichi | Badges langue/qualite/hash/cards + snapshots 6 langues |
| R-022 | Ouvert | Payload source reduit trop la qualite extraction | Client ToolAgent/UI | L'assistant voit des signaux que l'UI ne permet pas d'auditer | Porter `documentQualityStatus`, `pageQualityStatus`, `textStatus`, `signals`, `extractionSource` |
| R-023 | Ouvert | Sources issues inventaire/documents perdent metadata | Client + backend catalogue | Ouverture/focus document sans revision/langue/qualite | Enrichir endpoints catalogue/documents ou appel detail/RAG leger |
| R-024 | Ouvert | OCR readiness ne verifie pas langues Tesseract installees | Backend readiness/Ops | Container ready puis OCR degrade/echec au premier document | Exposer langues installees/manquantes ; fail si langue explicite absente |
| R-025 | Ouvert | Routes reindex encore ambigues | Backend admin/Ops | Operateur lance scan massif par confusion | Renommer/documenter full catalog + `confirm=REINDEX_ALL`/dry-run/count |
| R-026 | Ouvert | `install.sh` rend secrets via `sed` non echappe | Infra/deploy | Secret avec `/`, `&`, guillemets casse rendu/signature | Renderer robuste ou outil .NET + smoke secrets speciaux |
| R-027 | Ouvert | Modeles serveur faibles insuffisamment gates | Runtime LLM/Ops | Qualite summaries/profils degradee sous 3B sans signal fort | Exposer profil low-capacity + smoke JSON profile/summary + plancher par capability |
| R-028 | Ouvert | Artefacts LLM/deploy legacy a clarifier | Infra/Ops | Compose statique ou configs locales sensibles induisent erreur | Marquer compose legacy/generateur officiel + procedure nettoyage artefacts |

Note de lecture : les sections P0/P1/P2 ci-dessous gardent l'historique d'audit et les invariants. Pour l'etat le plus recent apres relecture des rapports agents et du worktree, utiliser en priorite le `Registre actif des gaps`.

## P0 - A fermer avant stabilisation produit

| ID | Sujet | Risque | Constat | Action attendue |
| --- | --- | --- | --- | --- |
| P0-01 | Capability B sourceHash/revision race | Resume version N stocke comme frais pour version N+1 si reindex entre generation et completion | Raman : `backend/SAAIA.Backend/CapabilityBBackofficeWorker.cs:150` genere depuis la version indexee courante puis complete avec `SourceHash:null`; `backend/SAAIA.Backend/RuntimeGovernance/RuntimeCapabilityBExecutionCoordinator.cs:209` recalcule le hash courant au complete | Lier job au claim/generation par `doc_id` + `indexed_version` + `revision_id` + `source_hash`; transmettre ce hash au complete; refuser ou marquer stale si la version courante a change; rendre atomique upsert resume + etat job |
| P0-02 | Separation LLM serveur / chat client | Le serveur pourrait etre mal utilise comme chat final si la doc ou un endpoint glisse | Le code introduit `CapabilityBBackofficeWorker`, `CapabilityBBackofficeSummaryService`, `DocumentProfileEnrichmentService` et `Chat__LlmBaseUrl` pour backoffice ; le README dit encore "aucun LLM cote serveur" | Aligner la doc produit/runbooks : "serveur LLM backoffice seulement, jamais chat client final" |
| P0-03 | Langue document vs langue UI | Resumes/profils dans la mauvaise langue, traduction involontaire du corpus | La generation backoffice resolve `doc.ProfileLanguage`/profil/extraits ; le client UI normalise seulement six langues | Garder `docLanguage` comme source de verite backend ; interdire la promotion de `language` UI en `docLanguage` |
| P0-04 | Hardcoding corpus dans produit | Specialisation Cuisine/PDF/recette qui casse l'objectif generaliste | Grep produit hors `bin/obj` : pas de `recipe/recette` cote client source ; une migration backend `038_prune_remaining_action_profile_content_cards.sql` contient `pour des recettes %` | Decider si cette migration historique est acceptable comme nettoyage ponctuel ; sinon remplacer avant livraison par une regle generique ou migration neutralisee |
| P0-05 | Ingestion prioritaire | Resumes/enrichissements longs peuvent voler les ressources OCR/TEI/Qdrant | Capability B a un idle gate (`CapabilityBRequireIngestionIdleForExecution`, idle delay) et auto-enqueue | Tester concurrence reelle : ingestion active doit bloquer ou differer le worker backoffice |
| P0-06 | Tests verts non verifies dans cette passe | Faux sentiment de stabilite | TODO indique dernieres campagnes `506/506` backend et `543/543` client, mais non relancees ici | Relancer les suites ciblees puis complete avant de fermer P0 |

## P1 - Haute priorite apres P0

| ID | Sujet | Risque | Constat | Action attendue |
| --- | --- | --- | --- | --- |
| P1-01 | Content cards couverture zero | Cartes non pertinentes retournees avec pages possiblement fausses | Raman cible `RagEndpoints` : `SelectDocumentProfileContentCards`, `ResolveDocumentProfileMatchPageRange`, `BuildMatchedContentCards` | Filtrer les cartes a couverture 0; ne retourner que les cartes dont la plage page/chunk couvre effectivement le match |
| P1-02 | Mojibake et multilingue profils/retrieval | Embeddings pollues par labels anglais ou texte encode de travers; mauvaise qualite hors six langues | Raman cible `DocumentProfileProjector`, `RetrievalChunkProjector`, `ContextualTextProjector`; labels anglais presents dans embeddings | Normaliser UTF-8, eliminer mojibake, localiser ou neutraliser les labels d'embedding, tester documents accentues et non-latins |
| P1-03 | Perf et panne silencieuse profile search | Search profile lent, non indexable, ou retourne `[]` sur erreur SQL sans signal operateur | Raman cible `SearchDocumentProfileMatchesAsync` : texte effectif non materialise, `LIKE`/regex, catch `PostgresException` retourne `[]` | Materialiser/indexer le texte de recherche effectif; remplacer panne silencieuse par telemetry + degraded signal; ajouter test perf SQL |
| P1-04 | Exact match sans index `normalized_text` | Recherche exacte degradee ou lente sur corpus large | Raman signale absence d'index adapte sur `normalized_text` | Ajouter index/strategie adaptee aux exact matches normalises et test de plan/perf |
| P1-05 | Multilingue backend "toute langue" | Le backend accepte `und`/BCP-47, mais les heuristiques/templates fortes couvrent surtout `fr/en/es/pt/de/it` | OCR mapping connait davantage de langues Tesseract configurables, mais detection dominante et templates sont six-langues + fallback neutre | Ajouter validations documents hors six langues : pas de crash, pas de traduction UI, fallback neutre correct |
| P1-06 | OCR bruit court | Suppression trop agressive de lignes courtes utiles, ou conservation de bruit OCR | `PdfOcrTextExtractor` protege certains labels courts industriels et codes ; `OcrNoiseFilter` existe en WIP | Tester pages scannees industrielles, labels courts, codes references, tableaux et documents mixtes |
| P1-07 | Content cards qualite | Cartes trop nombreuses, bruit de phrases d'action, pertes de titres utiles | `DocumentProfileProjector` limite a 240, deduplique, filtre low-signal ; migrations 031-042 nettoient l'historique | Mesurer precision/recall des cartes sur corpus non-Cuisine et documents scannes |
| P1-08 | RAG qualite extraction | Penalite trop faible ou trop forte sur documents OCR low confidence | `ApplyExtractionQualityScorePenalty` degrade les scores sans hard filter | Calibrer sur corpus : source faible doit rester visible mais passer derriere source saine equivalente |
| P1-09 | Runtime capacity plan | Concurrence LLM serveur statique ou plan absent | `RuntimeLlmCapacityPlanService` lit `llm.capacity-plan.json`, compare licence/seats et recommande replan | Automatiser generation/validation capacity plan en install/update et readiness admin |
| P1-10 | Resume source hash revision-aware | Resumes obsoletes possibles apres reindex/changement fichier | Migration `043_revision_aware_summary_source_hash.sql` est non suivie dans le worktree, mais Raman montre que hash recalcule au complete ne suffit pas | Verifier migration, tests SummaryBackofficeGovernance, invalidation, regeneration et refusal stale |
| P1-11 | Documentation d'exploitation OCR/LLM | Ops peut deployer avec OCR/LLM mal configure | Compose prod expose OCR, LLM, bulkheads, idle gate ; `appsettings.json` dev garde OCR off | Mettre a jour runbooks : mode dev/prod, readiness, langues OCR, diagnostics |
| P1-12 | Prod Linux config seats placeholder | Config JSON signee invalide ou licence/seats faux en prod | Fermat : `infra/scripts/prod/install.sh` ne remplace probablement pas `__LICENSE_SEATS__` dans `deployment.config.prod.template.json` | Corriger substitution avant signature/deploiement et ajouter validation qui fail si placeholder reste |
| P1-13 | Planner LLM surestime la capacite effective | Backoffice croit disposer de plusieurs instances/slots alors que le backend appelle un seul endpoint et traite sequentiellement | Fermat : backend n'a qu'un `Chat:LlmBaseUrl`; `CapabilityBBackofficeWorker` est sequentiel | Caper plan a 1 instance/slot effectif ou ajouter routeur/concurrence reelle cote backend |
| P1-14 | Readiness LLM backoffice non bloquante | Prod peut etre "ready" alors que Capability B LLM est active mais indisponible | Fermat : `/ready` expose seulement `details.llm` et ne bloque pas | Ajouter readiness stricte backoffice LLM ou endpoint dedie, lie a `BACKOFFICE_LLM_ENABLED` |
| P1-15 | `deploy-remote-backend.ps1` chown recursif | Mauvaise variable `SAAIA_INSTALL_ROOT` peut changer ownership trop large sur serveur | Fermat : `sudo chown -R` sans garde forte | Ajouter garde chemin absolu, deny root/home/system, confirmation cible et dry-run/log |
| P1-16 | Metadata RAG perdue dans sources UI | Le prompt peut voir `sourceHash`/qualite/cards, mais les cartes sources finales perdent ces preuves | Lagrange : backend OK dans `RagEndpoints`; client garde pour prompt dans `ToolAgentOrchestrator.State.cs`; mais `ToolMemory.SourceRef`, `BuildSourcesPayload` et `SourceCard` gardent surtout chemin/pages/label | Etendre `SourceRef`, payload sources et cartes avec champs optionnels `docId`, `sourceHash`, `docLanguage`, `profileLanguage`, `categoryPath/ref`, `extractionQuality`, `matchedContentCards`, `chunkId` |
| P1-17 | `sourceHash` resume pas strictement server-owned | Client/admin peut fournir un hash qui devient source de fraicheur si le serveur ne le recalcule pas strictement | Lagrange : `SummaryEndpoints.SubmitSummaryAsync` et `RuntimeCapabilityBExecutionCoordinator.CompleteAsync` calculent seulement si absent | Toujours recalculer cote serveur; si hash fourni, le traiter comme `expectedSourceHash` et rejeter en cas de mismatch |

## P2 - Durcissement et confort

| ID | Sujet | Benefice | Action attendue |
| --- | --- | --- | --- |
| P2-01 | Dashboard audit | Suivi plus simple de la qualite ingestion/RAG | Exporter un snapshot admin lisible : documents ok, OCR, low confidence, summaries missing, backlog Capability B |
| P2-02 | Corpus de validation par domaines | Eviter la regression vers Cuisine uniquement | Ajouter packs industriels/generalistes et packs multilingues hors six langues UI |
| P2-03 | UX client incertitude OCR | Reponse plus honnete quand source faible | Harmoniser wording client quand `extractionQuality` signale revue manuelle ou OCR faible |
| P2-04 | Glossaire runtime | Reduire les confusions "Capability A/B", backoffice, local LLM | Ajouter un glossaire docs ou admin panel |
| P2-05 | `document_revision_artifacts` peu consomme | Clarifier les artefacts utiles au diagnostic et a la regeneration | Documenter les consommateurs attendus et ajouter contrat artefacts |
| P2-06 | Source de verite links/contextual | Eviter divergence entre `retrieval_chunk_links` et `contextual_text_entries` | Clarifier quelle table pilote le contexte et les liens, puis tester la coherence |
| P2-07 | `BuildChunkLinkMap` quadratique | Meilleure perf sur documents longs | Remplacer la recherche intra-section par index par section/ordinal |
| P2-08 | OCR auto-detection incomplete | Meilleure couverture langues et corpus mixte | Completer detection ou expliciter fallback, avec tests OCR multi-langues |
| P2-09 | `content_card_id` instable | Eviter references rompues si reordonnancement | Stabiliser l'identifiant sur contenu canonique + page/revision plutot que ordre |
| P2-10 | Admin reindex endpoints ambigus | Eviter un scan massif lance par erreur operateur | Clarifier `/admin/ingestion/reindex` vs `/admin/reindex`, rendre le scan massif explicite et protege |
| P2-11 | `.dockerignore` racine non suivi | Build prod compose peut envoyer documents, modeles ou secrets | Suivre/valider `.dockerignore` racine puisque prod compose build context vaut `..` |
| P2-12 | Knobs ingestion doubles | Drift entre config signee et env runtime | Clarifier precedence config signee/env, documenter et tester |
| P2-13 | OCR readiness superficielle | Binaires presents mais langues/smoke OCR absents | Verifier langues disponibles et mini-smoke OCR |
| P2-14 | Idle Capability B global non tenant-aware | Tenant actif peut bloquer backoffice pour tous | Evaluer idle par tenant ou par file priorisee |
| P2-15 | `rag.summarize_live` metadata incomplete | Live summary moins tracable que stored summary | Propager vrai `docLanguage`/`sourceHash` via metadata backend ou fallback `RagItem.SourceHash` |
| P2-16 | `sources.resolve` trop pauvre | UI/source cards ne peuvent pas afficher fraicheur, langue, qualite | Ajouter `sourceHash`, langues et qualite extraction |
| P2-17 | Hardcoding exemples prompts/manifests | Risque de specialisation PDF/categorie/corpus dans chemins deterministes | Verifier/neutraliser `PDF34`, wording "Ce qui vient des PDF", vocabulaire recette/corpus et tout doc concret |
| P2-18 | Trace de decisions | Faciliter reprises multi-agents | Ajouter section "Decision log" actualisee ici a chaque arbitrage |

## Zones auditees

### Ingestion

Fichiers lus :

- `backend/SAAIA.Backend/Ingestion/IngestionWorker.cs`
- `backend/SAAIA.Backend/Ingestion/JobRepo.Completion.cs`
- `backend/SAAIA.Backend/Options/IngestionOptions.cs`
- `backend/SAAIA.Backend/Db/Migrations/016_document_foundation.sql` a `043_revision_aware_summary_source_hash.sql` par inventaire

Constats :

- workers concurrents bornes par `WorkerConcurrency` ;
- jobs stale requeues avec cadence, lock heartbeat et cancellation admin ;
- `superseded_version` ne doit pas stabiliser un document comme une annulation utilisateur ;
- pipeline upsert extrait pages, sections, units, chunks, exact matches, contextual text, embeddings, Qdrant, puis commit DB ;
- checkpoint/resume sur hash, taille et nombre de chunks ;
- category runtime issue du chemin document, avec fallback `DefaultCategory`, pas de categories metier codees dans le registry d'alias.

Risques :

- reprise checkpoint a valider apres changement chunking/ocr ;
- gros corpus + OCR + TEI peut saturer si capacity plan non applique ;
- migrations de nettoyage de cartes doivent etre qualifiees entre historique acceptable et logique produit.

### OCR et qualite extraction

Fichiers lus :

- `backend/SAAIA.Backend/Pdf/PdfExtractor.cs`
- `backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs`
- `backend/SAAIA.Backend/Pdf/ExtractionQualityDiagnostics.cs`
- `backend/SAAIA.Backend/Pdf/OcrNoiseFilter.cs` par inventaire
- `backend/SAAIA.Backend/Dockerfile`
- `infra/docker-compose.prod.yml`
- `infra/.env.example`

Constats :

- extraction native PdfPig avec suppression de boilerplate repete ;
- score page/document : empty/low/ok, ratios, OCR recommended ;
- OCR complet via `ocrmypdf` si extraction native insuffisante ;
- OCR image-page via rendu Ghostscript + Tesseract si document textuel contient des images ;
- fusion OCR image-page avec dedup de lignes natives ;
- `tesseract-ocr-all` installe en Docker, donc politique backend non limitee aux langues UI ;
- prod active OCR par env compose, dev garde OCR desactive par defaut ;
- diagnostics page distinguent warning informatif et revue manuelle.

Risques :

- detection dominante six-langues peut etre faible pour langues moins frequentes ;
- lignes courtes utiles et codes industriels doivent rester preserves ;
- page vide couverte par chunk contextuel ne doit pas gonfler les alertes.
- auto-detection OCR encore incomplete selon Raman : expliciter fallback et verifier documents mixtes/multi-langues.

### Profils documents

Fichiers lus :

- `backend/SAAIA.Backend/Pdf/DocumentProfileProjector.cs`
- `backend/SAAIA.Backend/Llm/DocumentProfileEnrichmentService.cs`
- `backend/SAAIA.Backend/Ingestion/DocumentFoundationRepo.cs` par inventaire et usage

Constats :

- profil deterministe `deterministic_v1` : langue, resume extractif, keywords, entities, topics, questions, limits, search text, checksum ;
- templates localises pour `fr/en/es/pt/de/it`, fallback neutre si langue inconnue ;
- profil enrichi `llm_backoffice_v1` seulement si LLM serveur configure ;
- enrichissement LLM strictement grounded : termes et cartes filtres contre corpus de grounding ;
- pas d'invention de facts/pages/quantites/certifications dans le prompt d'enrichissement.

Risques :

- fallback `und` doit rester documentaire, pas traduit vers la langue UI ;
- champs `questions` peuvent influencer retrieval et doivent rester grounded ;
- merge LLM + baseline doit conserver assez de signal sans reinjecter bruit.
- mojibake et labels anglais dans les textes d'embedding peuvent degrader le retrieval multilingue (`DocumentProfileProjector`, `RetrievalChunkProjector`, `ContextualTextProjector`).

### Content cards

Fichiers lus :

- `backend/SAAIA.Backend/Pdf/DocumentProfileProjector.cs`
- `backend/SAAIA.Backend/Llm/DocumentProfileEnrichmentService.cs`
- `backend/SAAIA.Backend/Endpoints/RagEndpoints.cs`
- migrations `029` a `042` par inventaire

Constats :

- cartes construites depuis sections, unit leads, exact leads ;
- scoring favorise contexte structurel, titres compacts, couverture equilibree par pages ;
- filtres low-signal sur imperatifs, phrases d'action, punctuation bruitee, leads minuscules ;
- limite `MaxContentCards = 240` ;
- RAG renvoie `matchedContentCards` dans le DTO et le contrat partage.

Risques :

- Raman : `SelectDocumentProfileContentCards` / `ResolveDocumentProfileMatchPageRange` / `BuildMatchedContentCards` peuvent renvoyer des cartes a couverture 0, donc non pertinentes ou avec pages fausses ;
- migration `038` contient un pattern recette ponctuel ;
- les cartes peuvent etre trop dependantes de heuristiques de titres OCR ;
- tests de preservation client existent selon TODO, a relancer.
- `content_card_id` peut devenir instable si l'ordre de projection change ; a stabiliser si des consommateurs externes l'utilisent.

### Resumes backoffice

Fichiers lus :

- `backend/SAAIA.Backend/Llm/CapabilityBBackofficeSummaryService.cs`
- `backend/SAAIA.Backend/CapabilityBBackofficeWorker.cs`
- `backend/SAAIA.Backend/Endpoints/SummaryEndpoints.cs` par inventaire
- `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.Summaries.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.LiveSummary.cs`

Constats :

- generation serveur/backoffice Capability B, pas chat client ;
- resolution langue : preferred/doc profile/dominant excerpts, puis `und` si besoin ;
- fallback deterministe si LLM absent, vide ou mismatch de langue ;
- qualite resume mesuree : longueur, structure, couverture sections/keywords ;
- client stocke/relit les resumes avec `docLanguage`, traduit pour affichage si l'utilisateur demande une autre langue ;
- `summary_store_queued` indique explicitement que la generation reusable est confiee au serveur ;
- Lagrange signale que `SummaryEndpoints.SubmitSummaryAsync` et `RuntimeCapabilityBExecutionCoordinator.CompleteAsync` calculent le `sourceHash` seulement s'il est absent, alors que ce hash doit rester server-owned.

Risques :

- P0 Raman : course possible entre generation et completion Capability B. `CapabilityBBackofficeWorker.cs:150` complete avec `SourceHash:null`; `RuntimeCapabilityBExecutionCoordinator.cs:209` recalcule le hash courant. Si reindex N+1 arrive pendant la generation N, le resume N peut etre stocke frais pour N+1 ;
- live summary client existe encore pour fallback/affichage ; ne pas le confondre avec resume reutilisable backoffice ;
- source hash revision-aware a valider avec binding claim-time, pas seulement recalcul au complete ;
- atomicite upsert resume + etat job a garantir pour eviter job done sans resume ou resume sans etat coherent ;
- `sourceHash` fourni par client/admin doit etre traite comme `expectedSourceHash` et rejete en cas de mismatch, jamais comme verite de fraicheur ;
- mismatch langue LLM doit etre teste sur documents courts et multilingues.

### RAG

Fichiers lus :

- `backend/SAAIA.Backend/Endpoints/RagEndpoints.cs`
- `backend/SAAIA.Backend/Rag/QdrantClient.cs`
- `backend/SAAIA.Backend/Pdf/RetrievalChunkProjector.cs` par inventaire
- `contracts/SAAIA.Contracts/ApiContracts.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/PromptCatalog.cs`

Constats :

- categories exposees depuis DB `documents`, pas depuis liste metier codee ;
- retrieval : exact match, quoted title, dense Qdrant, sparse BM25, contextual/hyp questions selon le code ;
- DTO RAG expose metrics, provenance, context, extraction quality et matched content cards ;
- score retourne applique une penalite qualite extraction sans filtrer brutalement ;
- writer client sait mentionner naturellement l'incertitude OCR/low confidence ;
- Lagrange confirme que `RagEndpoints` porte bien des metadata utiles jusqu'aux hits RAG, mais pas jusqu'au payload final des sources UI.

Risques :

- guidance clarification est en francais cote backend ; verifier si le client traduit ou si c'est acceptable pour UI multilingue ;
- reranking qualite peut changer le top result de facon subtile ;
- SearchCore est tres large : besoin de tests ciblant categoryPath, docPath, exact refs, sparse fallback, content cards.
- Raman : `SearchDocumentProfileMatchesAsync` peut etre lent ou tomber en panne silencieusement (`PostgresException` -> `[]`) ; materialiser/indexer le texte de recherche effectif et telemetriser les erreurs ;
- Raman : exact match sans index adapte sur `normalized_text` ; verifier plan SQL et ajouter index/strategie ;
- source de verite a clarifier entre `retrieval_chunk_links` et `contextual_text_entries` ;
- Lagrange : `sourceHash`, langues, qualite extraction et content cards se perdent entre les hits RAG et les sources finales ; `sources.resolve` doit aussi retourner ces metadata.

### Client multilingue

Fichiers lus :

- `client/SAAIA.Client.WinUI/Services/ClientUiText.cs`
- `client/SAAIA.Client.WinUI/Localization/LocalizedStrings.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.Language.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/PromptCatalog.cs`

Constats :

- `ClientUiText.SupportedLanguageCodes()` limite l'UI a `fr/en/es/pt/de/it` ;
- router output schema limite `language` aux memes six codes ;
- detection/alias multilingue pour changement de langue, style, mode ;
- writer : repond dans la langue demandee, mais uniquement depuis les tool results en mode documentaire ;
- stored summary : source language/doc language conservee, traduction d'affichage cachee par cle source version + target language ;
- Lagrange : `ToolAgentOrchestrator.State.cs` garde les metadata pour le prompt, mais `ToolMemory.SourceRef`, `BuildSourcesPayload` et `SourceCard` reduisent la source finale a chemin/pages/label.

Risques :

- UI six-langues ne doit pas devenir une limitation backend ingestion ;
- messages backend francais dans guidance peuvent fuir dans une conversation non-fr ;
- detection de langue courte peut retourner `fr` par defaut, ce qui est acceptable UI mais pas pour docLanguage ;
- live summary (`rag.summarize_live`) ne doit pas fabriquer une langue/hash incomplets si la metadata backend ou `RagItem.SourceHash` est disponible.

### Runtime serveur

Fichiers lus :

- `backend/SAAIA.Backend/Options/RuntimeGovernanceOptions.cs`
- `backend/SAAIA.Backend/CapabilityBBackofficeWorker.cs`
- `backend/SAAIA.Backend/RuntimeGovernance/RuntimeCapabilityBIngestionIdleCoordinator.cs`
- `backend/SAAIA.Backend/RuntimeGovernance/RuntimeLlmCapacityPlanService.cs`
- `infra/scripts/llm/install-llm.ps1` par inventaire

Fichiers signales par Fermat pour audit runtime/deploy :

- `infra/scripts/prod/install.sh`
- `infra/config/deployment.config.prod.template.json`
- `infra/scripts/prod/deploy-remote-backend.ps1`
- `.dockerignore`
- endpoints `/ready`, `/admin/ingestion/reindex`, `/admin/reindex`

Constats :

- Capability B worker activable/desactivable ;
- idle gate par defaut : `CapabilityBRequireIngestionIdleForExecution = true`, delay 900s ;
- auto-enqueue quand ingestion idle, batch et tenant limit ;
- requeue des jobs Capability B stale avec lease timeout ;
- capacity plan LLM lu depuis chemins candidats, compare a la licence/seats, recommandations si absent/invalide.

Risques :

- absence de capacity plan peut laisser une file LLM sous-optimale ;
- idle global ingestion peut etre trop conservateur pour multi-tenant ;
- interruption pendant enrichissement doit finir proprement l'item courant puis rendre la priorite a l'ingestion.
- complete Capability B doit refuser un job stale si la revision courante differe de celle claim/generation.
- Fermat : config prod Linux peut conserver `__LICENSE_SEATS__` si `install.sh` ne substitue pas le template avant signature ;
- Fermat : planner LLM peut annoncer plusieurs instances/slots sans routeur ni concurrence backend reelle ;
- Fermat : `/ready` ne bloque pas le deploiement si le LLM serveur backoffice est indisponible ;
- Fermat : `deploy-remote-backend.ps1` doit blinder les gardes avant `sudo chown -R` ;
- Fermat : endpoints reindex admin ambigus et `.dockerignore` racine non suivi sont des risques operateur/deploy.

## Decisions

| ID | Decision | Statut | Notes |
| --- | --- | --- | --- |
| D-001 | Aucun hardcoding de categorie/PDF/recette/corpus dans le code produit | Validee comme principe, audit a fermer | Tests/fixtures peuvent rester specifiques ; produit runtime non |
| D-002 | Backend document-driven pour langues ; UI client limitee a six langues | Validee | `und` et BCP-47 doivent rester acceptes cote backend |
| D-003 | LLM serveur autorise uniquement ingestion/backoffice | Validee | Ne sert pas le chat client final |
| D-004 | Resumes reutilisables generes/stores par serveur en langue document | Validee | Client peut traduire a l'affichage |
| D-005 | Ingestion prioritaire sur enrichissements longs | Validee | Worker Capability B attend idle ingestion |
| D-006 | RAG ne hard-filter pas les sources OCR faibles | Validee | Penalite douce + signal d'incertitude |
| D-007 | Categories = donnees du catalogue/arborescence | Validee | Alias generes depuis path/name/top-level |
| D-008 | README historique "aucun LLM serveur" est obsolete/ambigu | A corriger | Ne pas corriger dans cette passe par contrainte utilisateur |
| D-009 | Resume Capability B doit etre lie a une revision immuable | A implementer | Claim/generation/complete doivent porter `indexed_version`, `revision_id`, `source_hash`; complete stale doit etre refuse ou marque stale |
| D-010 | `sourceHash` des resumes est server-owned | A implementer | Hash fourni par client/admin = `expectedSourceHash`; le serveur recalcule toujours et rejette mismatch |
| D-011 | Les sources UI doivent conserver les metadata de preuve RAG | A implementer | Payload/cartes doivent porter hash, langues, qualite, content cards et ids utiles sans hardcoding PDF/categorie |

## Decisions immediates Codex

Ordre de correction demande pour la prochaine passe code, sans build/test dans cette mise a jour documentaire :

1. Corriger d'abord le P0 Capability B sourceHash/revision race.
2. Ensuite traiter le risque install/config prod Linux : substitution `__LICENSE_SEATS__`, validation anti-placeholder et config signee.
3. Puis traiter le risque admin reindex : clarifier endpoints, rendre le scan massif explicite et proteger l'operateur.
4. Puis traiter le risque `.dockerignore` racine / build context prod pour eviter d'envoyer documents, modeles ou secrets.
5. Appliquer la meme logique hash stale/server-owned a `SubmitSummaryAsync` juste apres Capability B.
6. Garder les sujets metadata sources UI, LLM capacity/readiness, chown remote, OCR readiness et idle tenant-aware dans la file P1/P2 selon la priorite ci-dessus.

## TODO actionnables

| ID | Priorite | Action | Responsable propose | Validation |
| --- | --- | --- | --- | --- |
| T-001 | P0 | Corriger race Capability B sourceHash/revision | Backend runtime | Job claim contient `doc_id`, `indexed_version`, `revision_id`, `source_hash`; complete avec hash transmis; stale refuse/marque stale |
| T-002 | P0 | Rendre atomique completion resume + etat job | Backend runtime | Test transactionnel : pas de job done sans resume frais coherent, pas de resume sans etat job coherent |
| T-003 | P0 | Ajouter test course version N/N+1 Capability B | Backend tests | Generation sur N, reindex N+1 avant complete, complete ne stocke pas N comme frais pour N+1 |
| T-004 | P0 | Mettre a jour README/runbooks pour "LLM serveur backoffice seulement" | Produit + backend | Relecture docs + grep "aucun LLM cote serveur" |
| T-005 | P0 | Ajouter/relancer test qui prouve `language` UI ne devient pas `docLanguage` | Client ToolAgent | `admin.summary.submit`, stored summary, translation cache |
| T-006 | P0 | Tester idle gate Capability B pendant ingestion active | Backend runtime | Job ingestion queued/running bloque Capability B |
| T-007 | P0 | Classer la migration `038` contenant `recettes` | Backend data | Decision explicite : historique accepte ou remplacement |
| T-008 | P0 | Relancer backend + client tests apres stabilisation WIP | QA/Orchestrateur | Suites completes vertes |
| T-009 | P1 | Filtrer content cards a couverture 0 | Backend RAG | `matchedContentCards` ne contient que cartes couvrant le match/page range |
| T-010 | P1 | Ajouter test content cards zero couverture | Backend tests | Carte hors plage/chunk non retournee, page range fiable |
| T-011 | P1 | Corriger mojibake et labels anglais dans embeddings multilingues | Backend document foundation | Tests UTF-8 accents/non-latin, embeddings sans labels parasites ou labels neutres |
| T-012 | P1 | Durcir `SearchDocumentProfileMatchesAsync` | Backend RAG | Texte effectif materialise/indexe, telemetry erreur SQL, plus de panne silencieuse `[]` sans signal |
| T-013 | P1 | Ajouter index/strategie exact match sur `normalized_text` | Backend DB/RAG | Plan SQL utilise index adapte; regression perf sur corpus large |
| T-014 | P1 | Valider documents hors `fr/en/es/pt/de/it` | Backend ingestion | Profil `und`/BCP-47, OCR configurable, resume non traduit UI |
| T-015 | P1 | Calibrer penalite extraction quality RAG | Backend RAG | Corpus avec source saine vs OCR faible |
| T-016 | P1 | Verifier `matchedContentCards` de bout en bout | Backend + client | DTO backend, contrats, prompt writer compact |
| T-017 | P1 | Valider source hash revision-aware des resumes | Backend summary | Reindex change resume stale -> regeneration |
| T-018 | P1 | Formaliser capacity plan LLM dans install/update | Infra/runtime | Plan present, matches licence, readiness admin ok |
| T-019 | P1 | Ajouter compteurs documents a enrichir par LLM serveur dans diagnostics categorie | Backend admin | Endpoint admin expose backlog enrichissement |
| T-020 | P1 | Corriger substitution `__LICENSE_SEATS__` dans install prod Linux | Infra/deploy | `deployment.config.prod.template.json` ne contient plus de placeholder apres install; signature refuse placeholder |
| T-021 | P1 | Aligner capacity planner LLM avec capacite backend reelle | Runtime + infra | Plan cape a 1 si pas de routeur/concurrence; ou routeur/concurrence implementes |
| T-022 | P1 | Ajouter readiness stricte LLM backoffice | Backend runtime | Si backoffice LLM active et LLM indisponible, endpoint strict fail ou expose etat bloquant dedie |
| T-023 | P1 | Blinder `deploy-remote-backend.ps1` avant `sudo chown -R` | Infra/deploy | Garde chemin absolu, denylist systeme, confirmation/dry-run, logs |
| T-024 | P2 | Clarifier endpoints admin reindex | Backend admin | `/admin/reindex` scan massif explicite, documente, protege contre lancement accidentel |
| T-025 | P2 | Suivre et valider `.dockerignore` racine | Infra/deploy | Build context prod exclut documents, models, secrets, artifacts lourds |
| T-026 | P2 | Clarifier precedence knobs ingestion config signee/env | Infra + backend | Runbook + test de resolution config |
| T-027 | P2 | Ameliorer OCR readiness | Backend readiness | Verification langues disponibles + mini-smoke OCR |
| T-028 | P2 | Evaluer idle Capability B tenant-aware | Backend runtime | Tenant avec ingestion active ne bloque pas inutilement les autres, ou decision contraire documentee |
| T-029 | P2 | Clarifier consommation de `document_revision_artifacts` | Backend foundation | Contrat artefacts + tests consommateurs |
| T-030 | P2 | Clarifier source de verite links/contextual | Backend RAG | Decision doc + test coherence `retrieval_chunk_links` / `contextual_text_entries` |
| T-031 | P2 | Optimiser `BuildChunkLinkMap` | Backend ingestion | Complexite lineaire ou quasi-lineaire sur documents longs |
| T-032 | P2 | Stabiliser `content_card_id` | Backend foundation | ID stable apres reordonnancement non semantique |
| T-033 | P2 | Construire packs non-Cuisine et multilingues | QA produit | Regression RAG + LLM validation multi-domaines |
| T-034 | P2 | Harmoniser guidance backend multilingue ou laisser client traduire | Backend + client | Conversation ES/PT/DE/IT avec clarification RAG |
| T-035 | P1 | Etendre metadata sources RAG jusqu'a l'UI | Client ToolAgent + contrats | `SourceRef`, `BuildSourcesPayload` et `SourceCard` portent `docId`, `sourceHash`, langues, categorie, qualite, cards et `chunkId` optionnels |
| T-036 | P1 | Rendre `SubmitSummaryAsync` strictement server-owned pour `sourceHash` | Backend summary | Serveur recalcule toujours; hash fourni = expected; mismatch rejete |
| T-037 | P2 | Propager metadata reelle dans `rag.summarize_live` | Client ToolAgent + backend | `docLanguage`/`sourceHash` resolus depuis metadata backend ou fallback `RagItem.SourceHash` |
| T-038 | P2 | Enrichir `sources.resolve` | Backend RAG + contrats | Retourne `sourceHash`, langues et qualite extraction |
| T-039 | P2 | Linter prompts/manifests contre hardcoding documentaire | QA produit + client/backend | Pas de `PDF34`, "Ce qui vient des PDF", recette/corpus ou doc concret dans chemins deterministes |
| T-040 | P2 | Ajouter fixtures non-recette multilingues | QA produit | E2E RAG/resume/UI avec corpus generaliste et langues hors exemples historiques |

## Tests a lancer

Note de cette passe : aucun build/test ne doit etre lance maintenant. La liste ci-dessous est le backlog de validation a executer apres corrections.

Tests complets :

```powershell
dotnet test RAG.sln
dotnet test backend/SAAIA.Backend.Tests/SAAIA.Backend.Tests.csproj
dotnet test client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj
```

Tests cibles backend ingestion/OCR/RAG :

```powershell
dotnet test backend/SAAIA.Backend.Tests/SAAIA.Backend.Tests.csproj --filter "FullyQualifiedName~PdfExtractorBoilerplateTests|FullyQualifiedName~PdfOcrTextExtractorTests|FullyQualifiedName~ExtractionQualityDiagnosticsTests|FullyQualifiedName~RagExtractionQualityScoringTests|FullyQualifiedName~RagMatchedContentCardsTests|FullyQualifiedName~ReadyEndpointOcrReadinessTests"
dotnet test backend/SAAIA.Backend.Tests/SAAIA.Backend.Tests.csproj --filter "FullyQualifiedName~DocumentFoundationIntegrationTests|FullyQualifiedName~DocumentProfileProjectorTests|FullyQualifiedName~DocumentProfileEnrichmentServiceTests|FullyQualifiedName~DocumentUnitExtractorTests|FullyQualifiedName~RetrievalRuntimeSwitchTests"
```

Tests cibles summaries/runtime :

```powershell
dotnet test backend/SAAIA.Backend.Tests/SAAIA.Backend.Tests.csproj --filter "FullyQualifiedName~CapabilityBBackofficeSummaryServiceTests|FullyQualifiedName~SummaryBackofficeGovernanceTests|FullyQualifiedName~RuntimeGovernanceCoreLogicTests|FullyQualifiedName~IngestionAdminStatePoliciesTests"
```

Tests manquants a creer ou completer (Raman/Fermat) :

- course Capability B version N/N+1 : claim/generation sur revision N, reindex N+1 avant complete, complete refuse ou marque stale ;
- atomicite complete resume/job : upsert resume et transition job doivent etre transactionnels ;
- content cards zero couverture : carte sans overlap lexical/page/chunk ne sort pas dans `matchedContentCards`, fallback propre si aucune carte fiable ;
- multilingue UTF-8 : documents accentues, mojibake, labels embeddings neutres/localises ;
- profile search perf SQL : `SearchDocumentProfileMatchesAsync` ne masque pas `PostgresException`, telemetry presente, plan indexe ;
- exact/composite index : exact match sur `normalized_text` garde un plan stable sur corpus large ;
- contrat artefacts : `document_revision_artifacts` a consommateur attendu ou contrat documente ;
- install prod Linux : `__LICENSE_SEATS__` absent du fichier signe final ;
- LLM capacity/readiness : plan ne surestime pas slots effectifs, readiness stricte fail si backoffice LLM active mais indisponible ;
- deploy remote safety : `deploy-remote-backend.ps1` refuse chemins dangereux avant `sudo chown -R` ;
- admin reindex : endpoint scan massif demande intention explicite ;
- Dockerignore prod : build context exclut documents, modeles, secrets et artifacts lourds ;
- OCR readiness : langues Tesseract disponibles + mini-smoke.

Tests manquants a creer ou completer (Lagrange) :

- E2E source metadata jusqu'a UI : `sourceHash`, qualite extraction, langues, categorie, content cards et `chunkId` visibles dans payload/cartes ;
- backend hash stale pour `SubmitSummaryAsync` : hash client/admin fourni comme expected, mismatch rejete apres reindex ;
- live summary metadata : `rag.summarize_live` propage un vrai `docLanguage` et un vrai `sourceHash`, pas une valeur inventee ;
- `/sources/resolve` apres changement `indexed_version` : hash et metadata correspondent a la revision courante ;
- lint prompts/manifests : aucun `PDF34`, "Ce qui vient des PDF", categorie concrete, recette/corpus ou document client dans les chemins deterministes ;
- fixtures non-recette multilingues : corpus generaliste avec accents, non-latin/fallback et domaines non culinaires.

Tests cibles client multilingue/RAG :

```powershell
dotnet test client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj --filter "FullyQualifiedName~LanguageSwitchRegressionTests|FullyQualifiedName~ToolContractParityTests|FullyQualifiedName~RagContextBudgetRegressionTests|FullyQualifiedName~ApiClientDocumentsTransitionTests|FullyQualifiedName~SetupWizardReadySummaryTests"
```

Tests manuels/ops a prevoir :

- `/ready` avec OCR actif et LLM serveur configure ;
- `/admin/documents/extraction-quality` sur corpus scanne, textuel et hybride ;
- `/admin/documents/{docId}/extraction-pages` sur pages vides, pages image, pages OCR bruit ;
- `/rag/search` avec source saine + source OCR faible equivalente ;
- resume stocke en langue document, puis affichage traduit dans une autre langue UI ;
- source card finale affichant les metadata RAG utiles sans exposer de hardcoding PDF/categorie ;
- ingestion active pendant backlog Capability B ;
- absence de LLM serveur : fallback deterministe backoffice, chat client local inchange.

## Points bloquants / incertitudes

- Tests non lances dans cette passe : audit documentaire uniquement.
- `rg --files` est bloque localement par Windows (`Access is denied`) ; recherches faites avec `git ls-files` et `Select-String`.
- Worktree tres charge : nombreux fichiers modifies/non suivis avant cette intervention. Ne pas assimiler les constats a une baseline committee.
- `README.md` contredit la decision runtime actuelle sur le LLM serveur.
- La migration `038_prune_remaining_action_profile_content_cards.sql` contient une expression `recettes`; a qualifier.
- Pas de verification live DB/Qdrant/TEI/LLM/OCR dans cette passe.
- La promesse "backend traite toute langue document" est architecturalement visee, mais les heuristiques de detection/templates restent surtout optimisees pour six langues plus fallback neutre.
- Raman et Fermat sont des rapports d'audit a integrer dans le backlog ; les chemins exacts doivent etre reverifies au moment de corriger le code.
- P0 Capability B bloque la fermeture "resume frais" tant que claim/generation/complete ne portent pas la meme revision/source_hash.
- Fermat signale un risque prod Linux sur config signee : tout placeholder `__LICENSE_SEATS__` residuel doit etre traite comme bloquant de deploiement.
- Lagrange ne confirme aucun P0 client/RAG, mais deux P1 restent ouverts : metadata sources perdues dans l'UI et `sourceHash` resume pas strictement server-owned.
- La correction P0/P1 hash stale Capability B doit etre appliquee ensuite a `SubmitSummaryAsync` pour eviter deux politiques de fraicheur divergentes.
- Aucun build/test lance volontairement pour respecter la consigne de cette passe.

## Responsabilites

| Zone | Responsable propose | Backup | Livrables |
| --- | --- | --- | --- |
| Orchestration audit | Orchestrateur SAAIA | Tech lead | Ce fichier, priorites, decisions, suivi |
| Ingestion/OCR | Backend ingestion | QA corpus | Tests OCR, diagnostics extraction, migrations |
| Profils/content cards | Backend document foundation | RAG owner | Qualite cartes, grounding LLM, nettoyage historique |
| Resumes backoffice | Backend runtime + client ToolAgent | Produit | `docLanguage`, source hash, UI display/translation |
| RAG | Backend RAG | Client ToolAgent | Retrieval hybrid, quality penalty, content cards |
| Client multilingue | Client WinUI/ToolAgent | Produit | Six langues UI, router/writer, stored summaries |
| Runtime serveur | Infra/runtime | Ops | LLM capacity plan, idle gate, readiness |
| Deploiement prod | Infra/deploy | Ops + backend | install Linux, config signee, dockerignore, deploy remote, reindex admin |
| Validation | QA | Orchestrateur | Suites automatiques, packs manuels, evidence |

## Journal d'audit

| Date | Auteur | Changement |
| --- | --- | --- |
| 2026-05-06 | Codex / orchestrateur d'audit | Creation du fichier, premiere synthese repo, P0/P1/P2, decisions, TODO, tests, blocages, responsabilites |
| 2026-05-06 | Raman + Fermat / integre par Codex | Ajout P0 Capability B sourceHash/revision race, P1 content cards/search profile/multilingue/index exact, P1/P2 runtime-deploy, decisions immediates Codex et tests manquants |
| 2026-05-06 | Lagrange / integre par Codex | Ajout audit client/RAG : metadata sources perdues, `sourceHash` resume server-owned, live summary/sources.resolve, hardcoding prompts et tests manquants |
| 2026-05-06 | Codex / orchestrateur actif | Ajout perimetre ownership markdown, invariants non negociables, snapshot prioritaire cross-domaine et template d'integration rapports agents |
| 2026-05-06 | Codex / orchestrateur actif | Relecture worktree et rapports agents `out/agents`; ajout registre actif P0/P1/P2 avec statuts, owners, risques et next actions verifiables |
