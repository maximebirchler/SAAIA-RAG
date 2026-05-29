# TODO SAAIA - ingestion, OCR, LLM serveur, retrieval et validation LLM

> Date de creation : 2026-05-05
> Source : discussions de reprise + travaux du matin sur ingestion/OCR/LLM serveur/retrieval/tests cuisine
> Scope : backend ingestion/RAG, LLM serveur d'enrichissement, client ToolAgent, gouvernance runtime, tests qualite
> Principe directeur : rendre l'ingestion et la recuperation fiables avant d'exiger du LLM client des reponses parfaites.

## Legende

- `[x]` fait et verifie au moins une fois
- `[~]` partiellement fait, a durcir ou a revalider
- `[ ]` a faire
- `[!]` point de risque ou decision produit a fermer

---

## Update 2026-05-06 - reprise nuit

- [x] Backend langue documentaire : ajout d'un resolveur partage `DocumentLanguageResolver` pour normaliser les tags BCP-47 et detecter des langues hors 6 langues UI :
  - latin etendu : `nl`, `sv/no/da/fi`, `pl/cs/ro/tr/id/vi`
  - scripts non latins : arabe, hebreu, cyrillique, grec, devanagari, thai, hangul, kana, han/CJK
  - fallback `und` conserve quand la langue reste vraiment incertaine
- [x] Backend profils : `DocumentProfileProjector` utilise maintenant ce resolveur partage et ne retombe plus sur le francais pour les documents non-UI.
- [x] Backend summaries : `CapabilityBBackofficeSummaryService` et `DocumentProfileEnrichmentService` utilisent la langue document detectee ou profilee, y compris `nl/ar/zh/ru/...`; les fallbacks restent extractifs/neutres si aucune template localisee n'existe.
- [x] Backend OCR : l'auto-selection Tesseract utilise maintenant le detecteur universel et mappe davantage de langues (`nld`, `ara`, `chi_sim`, `tur`, etc.) au lieu de se limiter a `fra/eng/deu/spa/por/ita`.
- [x] Backend RAG : les content cues des profils utilisent un fallback neutre `content_cues` pour les langues document hors UI, plus de fallback francais implicite pour `nl/ar/...`.
- [x] Backend Capability A : les questions hypothetique backoffice et prompts LLM recoivent la langue document detectee; pour les langues hors UI le fallback est neutre (`doc: sujet ?`) plutot qu'anglais.
- [x] Backend contextual embeddings : remplacement des labels anglais injectes (`Document:`, `Section:`, `Excerpt:`...) par des cles neutres (`document_name:`, `section_title:`, `excerpt:`...).
- [x] Backend robustesse RAG : les casts JSON de `processing_runs.payload` utilises par `/rag/search` pour la qualite extraction sont maintenant proteges contre les anciens payloads vides/non numeriques.
- [x] Backend LLM serveur : les questions hypothetique et limites produites par `DocumentProfileEnrichmentService` sont filtrees contre le corpus d'ancrage avant indexation, pour eviter qu'une question inventee cree de faux hits RAG.
- [x] Backend summaries : les tags regionaux (`fr-ch`, `pt-br`, etc.) utilisent bien les templates de leur langue primaire au lieu de retomber en anglais.
- [x] Backend Capability A : les candidats d'enrichissement recuperent la langue stockee du profil documentaire et la transmettent aux questions hypothetique/preview au lieu de deviner seulement depuis quelques titres/extraits.
- [x] Backend guidance RAG : les notes de clarification/qualification sont localisees selon la langue de la requete quand elle est detectee (`fr/en/es/pt/de/it`), avec fallback FR pour compatibilite.
- [x] Backend retrieval : fermeture des trois regressions de test restantes apres despecialisation :
  - bruit lexical `ingredient(s)` exclu des termes/phrases de fallback generiques
  - detection des unites courtes structurees preservee meme si la normalisation retire la ponctuation des etapes
  - logique `answer` vs `answer_with_caveat` resserree pour distinguer question factuelle directe et conseil operationnel client/projet
- [x] Tests ajoutes/renforces :
  - `DocumentLanguageResolverTests`
  - langues non-UI dans profils, summaries, enrichissements et OCR
  - Capability A non-UI sans fallback `What does`
  - RAG `content_cues` neutre pour langues hors UI
  - contextual text sans labels anglais historiques
  - filtrage des questions/limites LLM serveur non ancrees
  - templates regionaux `fr-ch` / `pt-br`
- [~] Dette volontairement non destructive :
  - certaines migrations anciennes contiennent encore des listes FR/EN/cuisine/procedure deja appliquees historiquement; ne pas les reecrire sans strategie migration/backfill.
  - il manque encore deux fixtures PDF OCR reelles dans les tests automatises : un PDF scanne et un PDF texte+image avec OCR bout en bout opt-in.
- [x] Backend OCR : diagnostics image-page enrichis sans stocker le texte OCR brut :
  - statut par page candidate/tentee/sautee
  - raison d'echec ou de skip (`budget`, `render_failed`, `ocr_failed`, `ocr_output_missing`, `ocr_empty`, `no_novel_text`, `novel_text_applied`)
  - compteurs mots/caracteres OCR, exit code et timeout
  - exposition dans le payload `ocrDiagnostics` et dans `GET /admin/documents/{docId}/extraction-pages`
- [x] Backend OCR : le filtre anti-bruit OCR est maintenant script-aware pour ne pas penaliser arabe, cyrillique ou CJK avec des heuristiques de voyelles latines.
- [x] Client RAG : les sources finales du writer principal conservent maintenant `sourceHash`, langues document/profil, categorie, qualite extraction, content cards et `selectionHints`.
- [x] Client RAG : le fallback local `sources.resolve` et `rag.summarize_live` propagent aussi les `selectionHints` jusqu'aux cartes sources.
- [x] Tests ajoutes/renforces :
  - diagnostics OCR image-page serialises et exposes
  - OCR non-latin non classe en bruit
  - source cards parsees avec `selectionHints`
  - live summary et source resolve conservent les metadata enrichies
- [~] Audit langue universelle backend :
  - `DocumentProfileProjector`, `CapabilityBBackofficeSummaryService`, `DocumentProfileEnrichmentService`, `CapabilityBBackofficeWorker`, `Capability A`, RAG lexical/intents et OCR auto-langue restent trop dependants des six langues UI ou de fallbacks anglais/francais.
  - Suite prioritaire : introduire une resolution langue documentaire BCP-47 plus universelle et la faire consommer par profils, summaries, questions hypothetique et OCR.
- [x] Client : le stockage de resume demande maintenant la generation serveur/backoffice au lieu de faire generer puis soumettre un gros resume par le LLM client.
- [x] Client : la langue des resumes stockes est rattachee a la langue document (`docLanguage`) et plus a la langue UI.
- [x] Client : `admin.summary.submit` n'est plus expose comme outil runtime/manifest LLM ; les intentions `summary.store` et `refresh_summary` routent vers `admin.summary.generate`.
- [x] Client : `admin.summary.submit` ignore volontairement `language` si `docLanguage` est absent et envoie `und`, pour eviter de confondre langue UI et langue document.
- [x] Client : la normalisation d'arguments `admin.summary.submit` ne promeut plus `language` en `docLanguage`.
- [x] Client : les `matchedContentCards` renvoyees par le backend sont conservees dans les resultats RAG et dans le prompt writer compact.
- [x] Client/backend : premiere passe de de-specialisation des heuristiques quantite/duree :
  - remplacement de `servings/personnes/portions/bols/assiettes` par un detecteur generique `nombre + libelle plausible`
  - remplacement de `N ingredients` / `serves N` par le meme detecteur generique cote base source
  - renommage affichage `Temps / portions visibles` en `Durees / quantites visibles`
  - retrait des unites cuisine (`cuilleres/cups/tbsp/tsp`) comme signaux globaux
- [x] Backend ingestion : `PdfExtractor` retire maintenant les lignes courtes repetees sur de nombreuses pages avant de construire tokens/pages.
- [x] Tests ajoutes :
  - preservation des `matchedContentCards` cote client
  - nettoyage de boilerplate repete sans vocabulaire metier
- [x] Ops : exposition dans le compose prod et les `.env.example` des knobs ingestion/concurrence :
  - chunk sizes
  - batch embeddings
  - worker concurrency
  - scanner cadence
  - timeouts TEI/Qdrant
  - bulkheads TEI/Qdrant
- [~] Suite recommandee : produire un vrai `IngestionCapacityPlan` calcule depuis hardware + licence/seats, puis l'utiliser pour proposer/forcer ces valeurs au lieu de simples variables statiques.
- [x] RAG : les scores retournes par `/rag/search` appliquent maintenant une penalite douce selon `extractionQuality` :
  - revue manuelle document/page
  - confiance extraction faible
  - statuts `manual_review`, `low_confidence`, `ocr_failed`
  - pas de hard filter : la source reste visible, mais elle descend derriere une source saine equivalente

## Update 2026-05-07 soir / 2026-05-08 nuit

- [x] Client OCR/source cards : les nouveaux diagnostics backend `ocr_required_but_disabled`, `ocr_disabled`, `ocr_output_missing` et `no_indexable_text` sont localises sur les 6 langues UI et ne fuient plus en snake_case dans les cartes sources.
- [x] Backend admin extraction-quality : les PDFs non indexables apres echec OCR/no-text ne disparaissent plus des diagnostics admin :
  - inclusion controlee des documents `status=error` + `auto_ingest_paused` + run `failed`
  - exposition `documentStatus`, `processingRunStatus`, `documentIndexable`, `failureReason`, `ocrFailureReason`
  - scoring explicite `ocr_required_but_disabled`, `scanned_pdf_not_indexable`, `no_indexable_text`, `document_not_indexable`
  - `/admin/documents/{docId}/extraction-pages` retourne aussi un resume doc-level quand aucune revision/page n'a ete indexee
- [x] Client admin extraction-quality/pages : les nouveaux champs backend sont conserves dans `inventory.rendered`, rendus en langage humain, et localises sur les 6 langues UI sans fuite snake_case.
- [x] Client admin jobs : le statut document `error` est maintenant traduit/localise.
- [x] Client OCR/source cards : test safety net ajoute pour verifier qualite, mode OCR, raisons OCR et metadata source sans code brut.
- [x] Backend ingestion batch embeddings : la taille de batch est confirmee configurable (`Ingestion:EmbeddingsBatchSize` / alias `TeiBatchSize`) et n'affecte pas la qualite, seulement debit/memoire/retry.
- [x] Backend ingestion batch embeddings : clamp centralise `1..256`, `/ready` expose configure/effectif/min/max et `dynamic=false`.
- [x] Client summaries : `admin.summary.submit` reste retire du manifest/runtime LLM client; la voie utilisateur passe par `admin.summary.generate` backoffice.
- [x] Client `summary.status.list` / `admin.summary.missing` :
  - accepte aussi `admin.summary.missing` comme inventaire deterministe
  - preserve `profileMissing`, `scopePath`, `endOfList`, `nextLink`
  - preserve `categoryRef/categoryPath` depuis `/catalog/summaries`
  - conserve les champs gouvernance Capability B et jobs actifs dans les payloads de replay
- [x] Client `rag.summarize_live` :
  - la langue document issue du backend/source gagne sur le `docLanguage` fourni par le routeur
  - la langue document est recalculee apres fallback RAG si `sources.resolve` etait pauvre
  - les cues `short/medium/long` ne sont plus anglais hors documents anglais
  - les langues inconnues/hors template utilisent uniquement nom document + metadata/cartes, sans fallback anglais/multilingue fixe
- [x] Tests client executes :
  - OCR/source cards cible : `95/95`
  - summary status/governance cible : `8/8`
  - live summary multilingue cible : `21/21`
  - suite complete `SAAIA.Client.ToolAgent.Tests` : `776/776`
  - build WinUI : `0 warning, 0 error`
- [x] Tests ajoutes/executés apres patch OCR admin :
  - backend extraction-quality OCR non indexable : `4/4`
  - backend batch embeddings/readiness : `6/6`
  - client extraction-quality/pages/multilingue : `17/17`
- [x] Backend OCR E2E : ajout d'un test reel opt-in `SAAIA_TEST_OCR_E2E=1` qui genere un PDF scanne image-only, verifie OCR full-document + image-page OCR + projection sections/units/chunks, sans imposer OCR a la CI.
- [x] Tests backend OCR opt-in non active : `3/3` verifies, le test compile et retourne proprement tant que `SAAIA_TEST_OCR_E2E` n'est pas defini.
- [x] Client `sources.resolve` : `docName` est maintenant preserve comme champ canonique dans `SourceRef`, les payloads finaux, le fallback local et les enrichissements backend+memoire.
- [x] Client admin extraction-quality : les documents listes par diagnostics OCR alimentent aussi `LastListedDocuments`/`PdfMap`, ce qui permet les suivis du type `pages du document 1` meme pour un PDF scanne non indexable.
- [x] Backend extraction-quality : les anciens runs `failed` avec `failureReason` mais sans `documentIndexable=false` sont maintenant traites comme non indexables dans la liste et dans `/extraction-pages`.
- [x] Client OCR/i18n : les statuts backend `image_ocr_applied_ok`, `ocr_applied_ok_with_page_warnings`, `ocr_applied_low_confidence`, `manual_review_empty_text`, `manual_review_low_text`, `extraction_ok_with_page_warnings`, `text_extraction_ok_with_images`, `extraction_ok` sont localises sur les 6 langues UI.
- [x] Backend LLM serveur : le probe live Capability B utilise maintenant `Chat:LlmModel` au lieu du modele hard-code `local`; endpoint runtime, diagnostics et decision de generation transmettent la config Chat.
- [x] Backend `/admin/summaries/submit` :
  - les jobs Capability B `queued` ne peuvent plus etre termines manuellement sans claim worker
  - les jobs Capability B `running` exigent une lease valide et non expiree
  - les jobs non-summary et les jobs rattaches a un autre document sont rejetes
  - l'UPDATE terminal refait les garde-fous atomiques `job_type/doc_id/status/lease`
- [x] Backend LLM serveur diagnostics :
  - `LocalLlmChatClient` distingue maintenant `llm_timeout`, `llm_transport_error`, `llm_queue_full`, `http_###`, `empty_body`, `choices_missing`, `content_missing`
  - les fallbacks Capability B conservent `llmError`, `llmFailureKind`, `llmFailureCategory`, timings, status code et bytes lus dans `summary_meta`
  - les champs LLM utiles sont propages dans `admin_jobs.result`
  - les diagnostics runtime Capability B exposent des comptes par categorie d'echec LLM (`queue`, `timeout`, `http`, `transport`, `empty`, `configuration`, `exception`, `quality`)
- [x] Tests executes apres corrections `sources.resolve`/OCR/LLM probe :
  - client sources/OCR/i18n cible : `11/11`
  - client sources/OCR/inventaire elargi : `205/205`
  - backend OCR/document foundation elargi : `141/141`
  - backend runtime/admin/LLM probe : `60/60`
- [x] Tests executes apres fermeture `/admin/summaries/submit` et diagnostics LLM serveur :
  - backend LLM/runtime/submit/admin cible : `41/41`
  - backend admin/runtime elargi : `158/158`
  - suite complete `SAAIA.Backend.Tests` : `716/716`
  - backend build : `0 warning, 0 error`
  - client i18n/contrats runtime cible : `219/219`
  - suite complete `SAAIA.Client.ToolAgent.Tests` : `778/778`
  - build WinUI : `0 warning, 0 error`
  - `git diff --check` : pas d'erreur bloquante, warnings CRLF/LF seulement
- [x] Client admin jobs OCR :
  - extraction de `failure_reason=...`, `ocr_failure=...`, `text_status=...`, `quality_status=...` depuis `lastError`
  - traduction des raisons OCR/non-indexables sur les 6 langues UI
  - plus de fuite snake_case pour les erreurs de PDF scanne/non indexable dans le centre des jobs
- [x] Audit heuristiques corpus produit :
  - aucun hard-code produit trouve sur nom de PDF/document, `PDF34`, `cold/frozen`, `cuisine`, `recette` ou `recipe`
  - les references `PDF01/PDF02/source 34` restantes sont des references generiques de source/outillage
  - le reliquat identifie concerne un vocabulaire procedural generique (`items/materials/procedure/steps/quantities`) duplique entre ingestion, RAG et client
- [x] Client planning retrieval :
  - suppression du fallback multilingue invente quand la question planning ne contient aucun signal documentaire utile
  - les requetes pauvres restent centrees sur la question normalisee de l'utilisateur au lieu d'injecter `procedure/source-backed/vorbereitung`
- [x] Backend RAG structured cues :
  - ajout d'un `StructuredContentLexicon` interne partage pour centraliser les marqueurs structurels existants sans changement de comportement
  - `RagEndpoints` utilise ce lexique pour les chunks structues et listes de quantites
  - tests de parite ajoutes pour marqueurs multilingues et listes de quantites
- [x] Backend ingestion/retrieval structured cues :
  - `StructuredContentLexicon` expose maintenant `NormalizeForStructuredLookup`, `ContainsItemizedCue`, `ContainsProcedureCue`, `ContainsGovernanceCue` et `LooksLikeStructuredLeadMarker`
  - `DocumentUnitExtractor` consomme le helper de lead marker partage au lieu d'une regex locale dediee
  - `RetrievalChunkProjector` consomme les helpers itemise/procedure/gouvernance partages au lieu de listes dupliquees
  - le lexique reste limite aux signaux structurels generiques et multilingues, sans nom de corpus/PDF/categorie/document
- [x] Client RAG/card evidence :
  - les fiches exactes non procedurales peuvent maintenant afficher `matchedContentCards.evidence.facts[].sourceText` sous une rubrique generique `Informations visibles`
  - les facts des content cards sont conserves meme quand l'extrait RAG texte est deja long
  - les expansions de planning n'ajoutent plus `sources documents`, `document`, `pdf`, `corpus`, `category`, `procedure`, `recette` ou `recipe` si ces termes ne viennent pas de la question utilisateur
- [x] Tests executes apres branchement lexique/client evidence :
  - backend lexique + DocumentUnitExtractor + RetrievalChunkProjector cible : `45/45`
  - backend lexique + DocumentUnitExtractor + RetrievalChunkProjector + RetrievalRuntimeSwitch cible : `152/152`
  - client planning/admin jobs/content-card evidence cible : `17/17`
  - client RagContextBudget + admin jobs + localisation deterministe cible : `234/234`
  - backend build : `0 warning, 0 error`
  - build WinUI relance seul apres verrou XAML parallele : `0 warning, 0 error`
- [x] Tests executes apres nettoyage heuristiques/OCR jobs :
  - client admin jobs/planning cible : `15/15`
  - client admin jobs/localisation cible : `67/67`
  - backend structured lexicon + retrieval runtime cible : `124/124`
- [x] Backend `DocumentProfileProjector` / LLM serveur profile :
  - `StructuredContentLexicon` porte aussi les signaux generiques de contexte structure, base de scaling, unites non scalables et contexte securite/parametres
  - `DocumentProfileProjector` consomme ces helpers sans les brancher dans les filtres de titres, pour eviter de supprimer de vrais titres metier
  - les evidences `scaleBasis`, `quantityFacts`, `nonScalableReasons` produites par le LLM serveur generent maintenant les bons `signals` (`scale_basis`, `quantity_list`, `scalable_quantities`, `non_scalable_quantities`)
  - les contextes techniques/securite (`bar`, temperature, rpm, normes, pages, temps) restent non scalables et ne creent pas de fausses quantites ajustables
- [x] Backend `DocumentProfileEnrichmentService` :
  - le schema prompt LLM serveur accepte explicitement `scaleBasis`, `quantityFacts`, `nonScalableReasons` et `facts`
  - les evidences structurees LLM sont groundees avant stockage
  - les pages de facts hors bornes document sont nullifiees au lieu d'etre conservees
  - les avertissements qualite extraction/OCR ne disparaissent plus pour les langues hors UI (`nl` explicite, fallback neutre pour autres tags)
- [x] Backend Capability A :
  - les questions hypothetique LLM sont filtrees par longueur, langue et ancrage documentaire
  - si toutes les questions LLM sont hors langue/non ancrees/trop longues, retour au fallback deterministe neutre
- [x] Tests executes apres durcissement profile/Capability A :
  - backend profile/summary/lexique cible : `106/106`
  - backend Capability A cible : `9/9`
- [x] Serveur verifie en lecture seule :
  - `/ready` OK
  - backend, TEI, Qdrant, Postgres, LLM backoffice et OCR OK
  - OCR ready avec `ocrmypdf`, renderer image et Tesseract disponibles
  - pas de jobs `queued/running` observes dans `ingestion_jobs` ni `admin_jobs` au moment du check
- [~] Reste a faire avant de considerer cette tranche totalement fermee :
  - finir de faire reculer cote client les rubriques/fallbacks `Durees/Elements/Etapes` derriere `matchedContentCards.evidence` et `selectionHints` dans les chemins non exact-card
  - relancer un filet backend/client plus large apres les derniers ajouts diagnostics
  - deployer seulement apres decision, avec check queue serveur juste avant

## Update 2026-05-08 - fermeture sources enrichies, i18n et profils LLM

- [x] Client `sources.resolve` / RAG :
  - conservation de l'evidence brute des `matchedContentCards` dans `SourceRef` sans perte de type JSON
  - fusion backend + memoire RAG des content cards dedupliquee lors de `sources.resolve`
  - les sources resolues explicitement ne redeviennent plus des sources pauvres quand le backend renvoie une source document-wide
- [x] Client `rag.summarize_live` :
  - requetes de recuperation multilingues basees sur langue document/profil et metadata source, avec fallback neutre pour langues hors UI
  - conservation des metadata de chunks et evidences de content cards dans les sources de resume live
  - les payloads d'ancres/sources exposent `docName`, `sourceHash`, langues, categorie, qualite extraction, content cards et selection hints
- [x] Client source cards :
  - affichage localise des valeurs factuelles et raisons non-scalables issues de `matchedContentCards.evidence`
  - pas d'affichage de `sourceText` brut ni de reason codes backend (`technical_parameter_context`, `time_limit`, etc.)
  - pas d'affichage de schema technique `content_card_evidence_v*` quand l'evidence ne contient qu'une contrainte documentee
  - labels ajoutes pour `content_card_values` et `content_card_reasons` sur les 6 langues UI
- [x] Client i18n RAG :
  - `Extrait:` est localise (`Excerpt`, `Auszug`, etc.)
  - les references pages visibles utilisent `S.` en allemand et `p.` ailleurs
  - les labels bruts de source ne portent plus la page quand `pageStart/pageEnd` sont disponibles separement
  - filet statique ajoute pour eviter le retour de `Append(" p.")` / `(p.` dans les chemins visibles ToolAgent
- [x] Backend runtime diagnostics Capability B :
  - les candidats et agregats diagnostics sont filtres par tenant
  - les campagnes et categories d'erreurs LLM ne melangent plus les tenants
  - les categories d'echec LLM sont normalisees avant agregation
- [x] Backend `DocumentProfileEnrichmentService` :
  - si la langue document/profil cible est connue, elle gagne sur la langue declaree par le LLM
  - les limites qualite OCR/extraction passent avant les limites LLM pour ne plus etre tronquees par le plafond
  - les evidences de content cards sont filtrees avant validation finale et les signaux structures derives d'evidence sont retires si l'evidence n'est pas ancree
- [x] Backend `CapabilityBBackofficeSummaryService` :
  - le guard de mismatch langue ne force plus un fallback pour une langue inconnue sans signaux de detection fiables
  - les langues hors table gardent le resume LLM au lieu de tomber en fallback sur simple presence de mots anglais
- [x] Tests executes apres fermeture :
  - client ciblé sources/live summary/i18n : `178/178`
  - client localisation safety net : `134/134`
  - suite complete `SAAIA.Client.ToolAgent.Tests` : `808/808`
  - backend `DocumentProfileEnrichmentServiceTests` + `CapabilityBBackofficeSummaryServiceTests` : `41/41`
  - suite complete `SAAIA.Backend.Tests` : `774/774`
  - backend build : `0 warning, 0 error`
  - `git diff --check` : pas d'erreur bloquante, warnings CRLF/LF seulement
- [~] Reste a faire :
  - deployer sur serveur uniquement apres check SSH + queues ingestion/admin jobs
  - tester sur corpus reel cuisine apres redeploiement et ingestion stabilisee
  - evaluer s'il faut remplacer les fallbacks deterministes encore proceduraux par des formulations encore plus neutres basees sur content cards/selection hints

## 0. Regles produit non negociables

- [x] Le LLM serveur ne sert pas au chat utilisateur direct.
- [x] Chaque client garde son LLM local pour la discussion.
- [x] Le backend utilise le LLM serveur pour l'ingestion, les enrichissements, les resumes, les mots-cles, les cartes de contenu et autres traitements documentaires.
- [x] La langue de traitement ingestion est la langue du document, pas la langue parametree dans le client.
- [x] La langue du client sert uniquement a l'interface utilisateur et aux messages destines a l'utilisateur.
- [x] Aucun nom de PDF, recette, categorie, phrase issue d'un document ou contenu indexe ne doit etre hard-code dans le code produit.
- [x] Nettoyage backend 2026-05-05 :
  - suppression guidance ATEX specifique
  - suppression alias categories builtin `atex/general/programmation`
  - comportement clarification conformite trop large conserve via logique generique
- [x] Remplacement des marqueurs navigation/index specifiques Cuisine par detection generique :
  - `index/liste/catalogue/inventaire de X`
  - conserve la detection des index de recettes sans hard-code de phrase issue du corpus
- [~] Audit client a finir :
  - plusieurs heuristiques `recette/recipe` existent encore cote client comme intentions documentaires generiques
  - elles ne pointent pas vers un PDF precis mais restent orientees domaine
  - decision a prendre : conserver comme intention documentaire generique ou remplacer par signaux issus des profils/cartes de documents
- [x] Nettoyage client produit des exemples et signaux trop specifiques :
  - suppression de `atex` des signaux de langue FR
  - commentaires/examples `ATEX/...` remplaces par exemples generiques
  - grep produit client vide sur `ATEX/Mettler/inertage/index des recettes` hors tests
- [x] Les tests peuvent etre cibles sur une categorie exemple comme Cuisine, mais le code produit doit rester generique.
- [x] Le systeme doit s'adapter a l'arborescence reelle des documents.
- [x] Les categories sont des donnees, pas des branches de code.
- [x] L'ingestion ne doit pas etre bloquee par des resumes longs ou enrichissements LLM non critiques.
- [x] Priorite runtime backend : ingestion active > enrichissements courts > resumes longs/backoffice.
- [x] Si aucune ingestion n'est active depuis un delai defini, le LLM serveur peut travailler sur les documents sans enrichissements.
- [x] Si une ingestion arrive pendant un enrichissement, l'enrichissement finit proprement son item courant puis rend la priorite a l'ingestion.
- [x] Tout doit etre prevu pour le multilingue sur toutes les langues supportees.
- [x] On accepte de stocker plus de metadata/document pour ameliorer qualite, vitesse et fiabilite.
- [x] On prefere une base de donnees plus riche a un prompt fragile qui essaie de deviner sans contexte.

---

## 1. Etat verifie au 2026-05-05

### 1.1 Backend et serveur

- [x] Serveur `saaia-server` joignable.
- [x] Backend deploye sur Docker.
- [x] `/ready` OK apres deploiement.
- [x] Postgres OK.
- [x] Qdrant OK avec auth API key.
- [x] TEI OK.
- [x] LLM serveur configure et visible dans `/ready`.
- [x] OCR active dans `/ready`.
- [x] `ocrmypdf` installe dans l'image backend.
- [x] `tesseract` installe dans l'image backend.
- [x] Langues Tesseract installees : `eng`, `fra`, `deu`, `spa`, `por`, `ita`.
- [x] `ocr_command_available=true`.
- [x] `ocr_auto_detect_languages=true`.
- [x] `ocr_image_page_enabled=true`.
- [x] `ocr_image_page_max_pages=50`.
- [x] `ocr_image_page_render_dpi=220`.
- [x] `ocr_image_page_segmentation_mode=3`.

### 1.2 OCR scanne

- [x] OCR complet sur PDF scanne reel teste.
- [x] Document teste : `Documents scannes - OCR imparfait/ANSI Z535.4.pdf`.
- [x] Page 1 comparee visuellement au rendu image.
- [x] Apres reglage `eng + psm 3`, texte page 1 stocke exactement :
  - `ANSI Z535.4-2007`
  - `American National Standard`
  - `for Product Safety Signs and Labels`
- [x] Regression observee puis corrigee : l'ancien run avait perdu les pluriels `Signs` et `Labels`.
- [~] A faire plus tard : echantillonnage sur plusieurs pages et plusieurs documents scannes, pas uniquement une page.

### 1.3 OCR hybride PDF texte + images

- [x] PDF textuel avec image contenant du texte teste.
- [x] Extraction source obtenue : `pdf_text_plus_image_ocr`.
- [x] L'OCR image s'applique uniquement aux pages contenant des images.
- [x] Les textes des images sont ajoutes aux chunks.
- [x] Les textes des images ne sont pas dupliques.
- [x] Filtre anti-doublon renforce pour ignorer le texte natif relu par OCR avec espaces differents.
- [x] Les PDFs synthetiques de test ont ete supprimes du corpus serveur apres validation.
- [x] Les jobs delete des PDFs synthetiques sont `done`.

### 1.4 Course scanner/watcher pendant ingestion

- [x] Bug observe : `superseded_version` pouvait auto-pause un document comme une annulation utilisateur.
- [x] Correction : `superseded_version` ne stabilise plus le document en `auto_ingest_paused=true`.
- [x] Le scanner peut reprendre automatiquement la version courante.
- [x] Test ajoute : `ShouldStabilizeDocumentAfterCancel_keeps_superseded_versions_requeueable`.

### 1.5 Tests automatises recents

- [x] Backend : `506/506` tests verts lors de la derniere campagne backend complete.
- [x] Client ToolAgent : `543/543` tests verts apres corrections RAG client.
- [~] Warnings NuGet OpenTelemetry connus, non lies au chantier OCR.

---

## 2. Chantier ingestion parfaite

### 2.1 Detection qualite extraction

- [x] Calcul qualite extraction native par page.
- [x] Signaux `nativeExtractionQuality` stockes dans les runs.
- [x] Detection de besoin OCR complet quand le PDF contient trop peu de texte exploitable.
- [x] Detection de pages avec images via `imageCount`.
- [x] Telemetrie OCR stockee :
  - `extractionSource`
  - `ocrAttempted`
  - `ocrApplied`
  - `ocrLanguages`
  - `ocrDurationMs`
  - `nativeTextStatus`
  - `nativeOcrRecommended`
  - `imagePageCount`
- [x] Ajouter un score global `extraction_confidence` par document.
- [x] Ajouter un score `page_extraction_confidence` par page.
- [x] Classer automatiquement les documents :
  - `extraction_ok`
  - `ocr_applied_ok`
  - `ocr_applied_low_confidence`
  - `native_text_suspicious`
  - `manual_review_recommended`
- [x] Exposer ces statuts dans une surface admin lisible :
  - `GET /admin/documents/extraction-quality`
  - `GET /admin/documents/{docId}/extraction-pages`
- [x] Exposer aussi les avertissements page dans le rapport document :
  - `pageReviewRecommendedDocuments`
  - `pageReviewRecommendedPages`
  - `pageReviewRecommendedCount`
  - `pageWarningDocuments`
  - `pageWarningPages`
  - `pageWarningCount`
  - statuts `*_with_page_warnings`
- [x] Separer les avertissements informatifs des vraies revues manuelles :
  - pages vides sans image => informatif
  - pages courtes sans image => informatif
  - pages image sans texte => revue manuelle
  - texte substantiel sans unite/chunk => revue manuelle
- [x] Ajouter des apercus courts par page signalee dans `GET /admin/documents/{docId}/extraction-pages` :
  - `unitPreviews`
  - `chunkPreviews`
  - limites a 3 extraits courts pour rendre le diagnostic actionnable sans exporter tout le PDF
- [x] Distinguer les pages vides mais deja couvertes par un chunk :
  - statut `page_ok_indexed_by_context`
  - compteur `indexedByContextPages`
  - reduction des faux warnings Cuisine de `119` a `70`
- [x] Ajouter une vue "documents a revoir" pour OCR faible, pages quasi vides, OCR tres bruyant, langues incertaines.
- [x] Raccorder le client aux diagnostics admin :
  - API client `DocumentsExtractionQualityAsync`
  - API client `DocumentExtractionPagesAsync`
  - outils admin `documents.extraction_quality`
  - outils admin `documents.extraction_pages`
- [x] Ajouter des compteurs par categorie :
  - documents avec OCR complet
  - documents avec OCR image-page
  - documents sans OCR
  - documents en faible confiance
  - pages avec avertissements informatifs
  - pages en revue manuelle
- [x] Ajouter dans les compteurs par categorie les documents a enrichir par LLM serveur.
- [x] Validation serveur rapport global :
  - 67 documents indexes
  - 67 documents `ok`
  - 11 OCR appliques
  - 0 document en revue manuelle
  - categories exposees avec compteurs OCR/qualite/page warnings

### 2.2 OCR complet pour PDFs scannes

- [x] Installer `ocrmypdf` dans Docker backend.
- [x] Installer Tesseract et langues supportees.
- [x] Utiliser `--tesseract-pagesegmode 3`.
- [x] Selectionner la langue OCR selon le document.
- [x] Fallback technique anglais pour corpus ANSI/NFPA/UL/standards industriels.
- [x] Stocker les textes OCR dans les structures d'ingestion normales.
- [ ] Ajouter un echantillonnage automatique post-OCR :
  - verifier que les premieres pages ne sont pas vides
  - verifier densite de mots
  - verifier ratio caracteres alphanumeriques/bruit
  - verifier presence de sequences absurdes repetees
- [x] Ajouter un premier filtre generique des unites courtes tres probablement bruit OCR :
  - tokens longs incoherents
  - ponctuation OCR excessive
  - melange quotes/slash/pipe
  - garde-fou sur presence suffisante de mots naturels multilingues
  - tests avec bruit OCR reel + avertissement utile conserve
- [x] Centraliser le filtre bruit OCR pour le reutiliser :
  - extraction d'un helper `OcrNoiseFilter`
  - utilise par les unites documentaires
  - utilise aussi avant fusion des lignes OCR d'images dans les PDFs textuels
  - detection ajoutee pour lignes OCR verticales/symboliques et lignes inversees/mixed-case issues de scans
- [x] Reindex reel de controle `UL 508A` :
  - version 5 terminee apres filtre renforce
  - page de garde faible valeur classee informative, pas revue manuelle
  - validation endpoint page : `manualReviewRecommendedPages=0`, `probableOcrNoisePages=0`
- [x] Validation rapport global dossier scanne :
  - 14 documents indexes
  - 9 OCR appliques
  - 0 document en revue manuelle globale
  - 10 documents avec avertissements page
  - 24 pages avec avertissements informatifs
  - 0 page en revue manuelle
- [x] Reindex cible post-filtre renforce :
  - `ANSI Z535.1.pdf` v3 termine : bruit inverse supprime, units 310 -> 309
  - `UL 121201 ... Scan.pdf` v3 termine : units 762 -> 761, chunks 135 -> 134
  - `NFPA 79 2024 ...pdf` v3 termine : gros bruit inverse retire, reste des lignes de schemas utiles/imparfaites
  - `UL 508A ... Scan.pdf` v5 termine : units 2337 -> 2334, chunks 503/504 -> 501, bruit page 45 supprime
  - `DVS 2205 ...pdf` v4 termine : units 578 -> 577, ligne symbole page 7 supprimee
  - `ISO 7200 ...pdf` v2 termine : reindex OK
  - scan DB global refait apres completion
- [!] Limite constatee apres scan :
  - les derniers retours "weird/qsp" ne sont pas tous du bruit
  - beaucoup sont des tableaux, URLs, citations, avertissements, formules, schemas electriques ou lignes d'index imprime
  - ne pas durcir aveuglement le filtre sans score page/document et revue visuelle, sinon on supprimera du contenu utile
  - prochaine etape logique : rapport qualite OCR par document + file "manual_review_recommended"
- [x] Ajouter un rapport OCR par document dans les metadata et l'exposer en admin.
- [ ] Ajouter un mode reindex OCR force via endpoint admin.
- [ ] Ajouter un mode reindex OCR pour une seule page ou plage de pages si techniquement pertinent.
- [ ] Ajouter une option de nettoyage d'anciens runs OCR imparfaits.

### 2.3 OCR images dans PDFs textuels

- [x] Detecter les pages avec images.
- [x] Rendre uniquement les pages candidates avec Ghostscript.
- [x] OCRiser chaque page candidate avec Tesseract.
- [x] Fusionner uniquement les lignes nouvelles.
- [x] Eviter les doublons natifs exacts.
- [x] Eviter les doublons natifs avec espaces differents.
- [ ] Ameliorer la selection des pages candidates :
  - ignorer logos tres petits si possible
  - ignorer images decoratives sans texte si possible
  - conserver images grandes ou proches de tableaux/legendes
- [ ] Etudier OCR par zones/crops au lieu d'OCR page complete.
- [ ] Ajouter heuristique "page image mais OCR vide" pour ne pas polluer.
- [ ] Ajouter telemetry :
  - nombre pages rendues
  - nombre pages OCR reussies
  - nombre lignes image ajoutees
  - nombre lignes ignorees comme doublons
  - nombre lignes ignorees comme bruit
- [ ] Exposer ces compteurs dans `/admin/documents/extraction-quality`.
- [ ] Ajouter tests avec :
  - image texte seule
  - image decorative sans texte
  - tableau image
  - schema avec annotations
  - logo + texte natif
  - PDF long avec seulement 1 image textuelle

### 2.4 Langue document

- [x] Ne pas utiliser la langue du client pour l'ingestion.
- [x] Detection langue basee sur nom fichier + texte natif.
- [x] OCR en langue principale + anglais si langue non anglaise et anglais disponible.
- [ ] Stocker `document_language` de facon canonique sur le profil document.
- [ ] Stocker `language_confidence`.
- [ ] Gerer documents mixtes :
  - document majoritairement FR avec citations EN
  - document technique EN avec notes FR
  - document multilingue reel
- [ ] Ajouter tests langue :
  - francais
  - anglais
  - allemand
  - espagnol
  - portugais
  - italien
  - inconnu/fallback
- [ ] Verifier que les enrichissements LLM serveur restent dans la langue du document.

### 2.5 Robustesse ingestion

- [x] Fix `superseded_version` qui auto-pausait a tort.
- [ ] Ajouter test d'integration complet scanner/watcher :
  - fichier cree
  - watcher enqueue v1
  - scanner bump v2
  - v1 annulee `superseded_version`
  - v2 indexee automatiquement
  - document non auto-pause
- [ ] Distinguer clairement les annulations :
  - admin pause
  - admin cancel
  - user/admin resume
  - superseded version
  - source removed
  - timeout
  - crash worker
- [ ] S'assurer que les documents `pending` sans job actif sont repris par scanner.
- [ ] S'assurer que les documents `missing` ne bloquent pas les retours apres copie.
- [ ] Ajouter diagnostics admin "pourquoi ce document ne s'indexe pas".
- [ ] Ajouter un bouton/action admin "reparer et requeue" pour cas non destructifs.
- [x] Corriger collision content cards `revision_id/profile_version/normalized_title` :
  - purge des cartes par `revision_id + profile_version`
  - upsert sur le titre normalise, qui correspond a la contrainte metier
  - tests backend complets verts apres correction
- [x] Redeployer ce correctif puis relancer les deux jobs echoues recents.
  - `SMS 1145 ... Nominalpressure 6 atm.pdf` relance puis `done`
  - `ANSI Z535.4.pdf` relance puis `done`
  - les anciens jobs failed restent dans l'historique, mais le dernier etat par document est sain

### 2.6 Performance ingestion et taille des lots

- [x] Localiser precisement le batch actuel `16 chunks par 16 chunks` :
  - generation embeddings TEI
  - insertion Qdrant
  - transaction Postgres
  - eventuel parallellisme OCR/extraction
- [x] Verifier si la valeur est hard-codee, configuree ou derivable d'une policy runtime.
  - source confirmee : `Ingestion:EmbeddingsBatchSize`
  - valeur effective serveur constatee : `32`
  - clamp code actuel : `1..256`
  - ancienne cle compatible : `TeiBatchSize`
  - les `.Take(16)` trouves ailleurs concernent des plafonds de termes/requetes retrieval, pas l'embedding batch ingestion
  - logs serveur confirmes : progression `32/504`, `64/504`, etc.
  - observation serveur : TEI environ 14-17 s par lot de 32, Qdrant environ 20-35 ms par lot
- [~] Rendre cette valeur configurable sans redeploiement si possible.
  - actuellement configurable par fichier de config/deploiement
  - a etudier : modification admin runtime sans rebuild/redeploy
- [ ] Mesurer l'impact batch size sur :
  - vitesse totale d'ingestion
  - latence TEI
  - pression RAM backend
  - pression Qdrant
  - contention Postgres
  - stabilite sous ingestion concurrente
- [x] Confirmer que la taille de batch n'a pas d'effet direct sur la qualite semantique des embeddings.
  - a modele TEI identique et chunk identique, l'embedding produit ne depend pas du nombre de chunks envoyes dans le meme appel
  - le batch size est donc un levier vitesse/stabilite/cout runtime, pas un levier de qualite semantique directe
- [ ] Surveiller les effets indirects possibles :
  - timeouts
  - retries
  - chunks manquants
  - saturation serveur
  - latence RAG utilisateur pendant ingestion
- [ ] Ajouter une policy dynamique selon profil serveur :
  - CPU/RAM disponible
  - TEI CPU/GPU
  - nombre de workers ingestion
  - nombre de seats licence
  - charge courante
- [ ] Decider si le batch doit etre dynamique en fonction du serveur.
  - recommandation actuelle : oui, mais seulement apres benchmark et avec bornes strictes
  - demarrage conservateur : conserver la config statique `EmbeddingsBatchSize=32`
  - future policy : descendre automatiquement si timeout/pression memoire, monter seulement si latence stable
  - ne pas lier brutalement aux seats : les seats influencent la concurrence et la charge attendue, mais le batch embedding depend surtout de TEI/GPU/RAM et de la taille moyenne des chunks
- [ ] Prevoir un auto-tuning prudent :
  - commencer bas
  - augmenter si latence stable
  - reduire si erreurs/timeouts/pression memoire
  - garder des bornes min/max explicites
- [ ] Exposer les valeurs effectives dans diagnostics admin.
- [x] Exposer les valeurs effectives de base dans `/ready` :
  - `ingestion_embeddings_batch_size`
  - `ingestion_worker_concurrency`
  - `ingestion_tei_max_concurrency`
  - `ingestion_qdrant_max_concurrency`
  - timeouts TEI/Qdrant
- [ ] Ajouter benchmark ingestion reproductible pour comparer batch 8/16/32/64 selon serveur.

---

## 3. LLM serveur pour enrichissement documentaire

### 3.1 Role exact du LLM serveur

- [x] Clarifier le scope : le LLM serveur n'est pas le cerveau du chat utilisateur.
- [x] Le LLM serveur travaille pour le backend.
- [x] Le client n'interagit pas directement avec le LLM serveur pour discuter.
- [ ] Formaliser ce principe dans une doc architecture courte.
- [ ] Ajouter un commentaire de code ou doc admin pour eviter confusion future.

### 3.2 Priorisation runtime serveur

- [x] Decision produit : oublier la plage horaire fixe.
- [x] Decision produit : si pas d'ingestion active, le LLM serveur peut enrichir/resumer.
- [x] Definir `ingestion_idle_delay_seconds` : `900` secondes par defaut.
- [x] Mettre en place un coordinateur :
  - detecte ingestion active/recente
  - suspend lancement nouveaux enrichissements quand ingestion active
  - laisse finir l'item LLM courant
  - reprend enrichissements apres idle
- [x] Garantir un seul travail lourd LLM serveur a la fois par defaut.
- [ ] Prevoir slots/instances selon capacite serveur, mais avec limite stricte.
- [x] Ajouter telemetry file d'attente :
  - jobs enrichissement en attente
  - job courant
  - duree moyenne
  - age du plus vieux job
  - interruptions/priorite ingestion
- [x] Exposer l'etat scheduler dans `/admin/runtime/operational-summary` :
  - `enabled`
  - `requiresIngestionIdle`
  - `autoEnqueueEnabled`
  - `state`
  - `reason`
  - `activeIngestionJobs`
  - `idleForSeconds`
  - `autoEnqueueBatchSize`
- [x] Corriger le faux diagnostic `runtime_live_unavailable` :
  - le service de diagnostics n'avait pas `IHttpClientFactory` via DI
  - `/ready` voyait bien `llm=server-ok`
  - Capability B n'a plus de blocker live runtime apres redeploiement
- [x] Ajouter endpoint admin pour pause/resume enrichissements.
- [ ] Ajouter endpoint admin pour forcer enrichissement d'un document.

### 3.3 Types d'enrichissements LLM serveur

- [~] Resume court document :
  - 5 a 10 lignes
  - langue du document
  - sans invention
  - avec niveau de confiance
  - implementation Capability B active et deployee
  - generation forcee et testee sur les 10 documents Cuisine
  - qualite globalement exploitable mais encore dependante du petit modele serveur
- [x] Durcir le resume court Capability B :
  - prompt documentaire neutre
  - pas de metadata interne dans le prompt utilisateur
  - exemples limites
  - interdiction de fusionner des extraits independants
  - temperature basse
  - fallback si mauvaise langue detectee
  - deduplication de phrases et termes repetes en post-traitement
- [x] Ameliorer la selection des extraits representatifs :
  - echantillonnage par buckets sur le document complet
  - penalisation generique des tables des matieres a points
  - penalisation generique des references/URLs/bibliographies
  - penalisation generique du front-matter credits/remerciements/copyright/ISBN
  - test d'integration ajoute pour preferer contenu utile aux blocs longs faible valeur
- [ ] Resume long document :
  - environ 1 page pour PDF long
  - sur demande ou en backoffice idle
  - langue du document
- [ ] Mots-cles principaux :
  - termes metier
  - synonymes importants
  - acronymes
  - produits/normes/personnes si presents
- [ ] Themes/sous-themes :
  - generiques et dynamiques
  - derives du document
  - aucun hardcoding par categorie
- [ ] Intentions/questions probables :
  - "ce document peut aider a repondre a..."
  - utile pour vague queries
  - stocker sous forme indexable
- [ ] Cartes de contenu :
  - titre normalise
  - description courte
  - pages couvertes
  - mots-cles
  - references vers sections/chunks
- [ ] Extraction d'entites :
  - normes
  - materiels
  - recettes/ingredients uniquement si documents cuisine, mais schema generique
  - valeurs/chiffres importants
  - contraintes/securite/allergenes si presents
- [ ] Resume par section ou groupe de pages.
- [ ] Mini glossaire par document si vocabulaire specifique.
- [ ] Table des sujets couverts/non couverts si detectable.

### 3.4 Controle qualite des enrichissements LLM

- [ ] Chaque enrichissement doit citer les pages/chunks sources.
- [ ] Chaque enrichissement doit avoir `model`, `prompt_version`, `created_at`, `language`, `source_revision_id`.
- [ ] Si le document change, enrichissement marque stale.
- [ ] Si le LLM serveur echoue, document reste indexe et searchable.
- [ ] Les enrichissements ne doivent jamais supprimer/remplacer les chunks bruts.
- [ ] Ajouter un score de confiance.
- [ ] Ajouter un statut :
  - `not_requested`
  - `pending`
  - `running`
  - `done`
  - `failed_retryable`
  - `failed_terminal`
  - `stale`
- [ ] Ajouter retry avec backoff.
- [ ] Ajouter detection des hallucinations grossieres :
  - resume mentionne un terme absent des sources
  - pages citees inexistantes
  - langue de sortie differente de la langue document
- [x] Ajouter detection minimale mauvaise langue resume court :
  - FR attendu mais sortie clairement EN => fallback deterministe FR
  - test unitaire ajoute
- [x] Ajouter tests unitaires prompts/parsing pour resume court Capability B.
- [ ] Ajouter tests integration avec LLM fake deterministic.

---

## 4. Materialisation DB et index supplementaires

### 4.1 Tables/profils documentaires

- [x] `document_profiles` existe deja.
- [x] `document_profile_content_cards` existe deja.
- [x] Correction dedup des content cards deja faite.
- [x] Migration de purge bruit OCR ajoutee.
- [ ] Materialiser les content cards dans une table/index dedie vraiment exploitable par retrieval rapide.
- [ ] Ajouter colonnes/index utiles :
  - `tenant_id`
  - `doc_id`
  - `revision_id`
  - `language`
  - `card_type`
  - `normalized_title`
  - `keywords`
  - `page_start`
  - `page_end`
  - `source_unit_ids`
  - `confidence`
  - `embedding_status`
- [ ] Ajouter index Postgres :
  - trigram/GIN sur titres
  - full text par langue si possible
  - index tenant/doc/revision
  - index status/stale
- [ ] Ajouter table pour `document_enrichment_jobs`.
- [ ] Ajouter table pour `document_enrichment_artifacts`.
- [ ] Ajouter revisioning propre :
  - artefact lie a `revision_id`
  - artefact stale si nouvelle revision
  - suppression/purge des revisions obsoletes selon policy

### 4.2 Collections Qdrant

- [x] Collection chunks existante.
- [ ] Evaluer collection supplementaire `document_profiles`.
- [ ] Evaluer collection supplementaire `document_content_cards`.
- [ ] Evaluer collection supplementaire `document_summaries`.
- [ ] Definir payload commun :
  - tenant
  - doc_id
  - revision_id
  - doc_path
  - category path
  - language
  - artifact_type
  - page range
  - source ids
- [ ] Definir quand embedder :
  - a l'ingestion pour chunks essentiels
  - en idle pour resumes/cartes
  - retry si TEI indisponible
- [ ] Ajouter backfill admin pour embeddings d'artefacts.
- [ ] Ajouter nettoyage Qdrant quand document supprime ou revision obsolete.

### 4.3 Donnees utiles sans LLM

- [ ] Generer automatiquement mots-cles statistiques :
  - top termes TF-IDF
  - acronymes
  - references normatives
  - titres detectes
  - noms de fichiers/dossiers
- [ ] Stocker mini resume extractif sans LLM :
  - premiers titres
  - table des matieres si detectee
  - sections avec forte densite de termes
- [ ] Stocker signaux d'arborescence :
  - categorie
  - sous-categorie
  - nom fichier
  - chemin complet normalise
- [ ] Utiliser ces signaux comme fallback quand LLM serveur indisponible.

---

## 5. Retrieval/RAG plus intelligent

### 5.1 Pipeline recherche cible

- [x] Retrieval actuel deja riche : exact/sparse/dense/fusion/context.
- [ ] Ajouter couche `document profile retrieval`.
- [ ] Ajouter couche `content cards retrieval`.
- [ ] Ajouter couche `summary retrieval`.
- [ ] Ajouter couche `keyword/entity retrieval`.
- [ ] Ajouter orchestration multi-couches :
  1. normaliser requete
  2. detecter langue requete
  3. chercher exact matches
  4. chercher chunks sparse/dense
  5. chercher profils/cartes/resumes
  6. fusionner scores
  7. etendre avec chunks voisins/pages liees
  8. produire evidence pack compact
- [ ] Ne jamais filtrer par categorie uniquement sur heuristique fragile.
- [ ] Utiliser categorie/arborescence comme boost, pas comme mur, sauf demande explicite utilisateur.
- [ ] Gerer requetes vagues :
  - "je ne sais pas quoi faire pour les repas"
  - "aide-moi a planifier"
  - "trouve les documents qui parlent de..."
  - "est-ce que j'ai quelque chose sur..."
- [ ] Gerer requetes multilingues :
  - requete FR, doc EN
  - requete EN, doc FR
  - termes techniques non traduits
- [ ] Gerer synonymes via artefacts documentaires, pas hardcoding par domaine.

### 5.2 Evidence pack pour LLM client

- [x] Audit agent Mendel 2026-05-05 : identifier les signaux backend produits mais pas assez consommes par client/writer.
- [x] Propager dans le contrat partage et le writer client les premiers signaux RAG riches :
  - `guidance`
  - `metrics`
  - retriever
  - exact match
  - source hash
  - embedding basis
  - chunk type
  - section/heading
  - table/warning
  - match questions hypothetique
- [x] Ajouter consignes writer pour respecter `guidance.behavior=ask_clarification` et `guidance.behavior=answer_with_caveat`.
- [x] Ajouter test ToolAgent de non-regression `Writer_rag_results_preserve_backend_guidance_metrics_and_hit_signals`.
- [x] Generaliser plusieurs heuristiques client qui etaient trop Cuisine :
  - filtre low-signal base maintenant sur structure/procedure, plus sur verbes de cuisine
  - fallback d'extraction "ingredients/elements" remplace par signaux structurels generiques
  - detection materiel reduite aux headings/termes generiques `material/equipment/tools`
- [x] Corriger `rag.multi_search` documentaire :
  - les requetes planifiees par le router ne sont plus ecrasees par les requetes robustes ajoutees cote client
  - fusion dedupee et bornee a 12 requetes
  - test `Documentary_defaults_merge_router_multi_search_with_robust_planning_queries`
- [x] Corriger ranking documentaire generique :
  - les pages intro/guides generiques ne doivent pas battre une source structuree quand elle existe
  - le filtre cible utilisateur ne doit pas supprimer une source structuree plus exploitable
  - detection temperature etendue a `140 C` sans symbole degre
  - tests Cuisine pilotes verts sans hard-code de document ou recette dans le code produit
- [x] Corriger deux points UX client releves par audit :
  - banniere d'information des dialogs en grille avec wrapping stable
  - overlay runtime admin passe en shell scrollable plus large
- [~] Nettoyage physique restant : certaines anciennes lignes mojibake avec mots alimentaires/ustensiles restent dans le fichier mais ne sont plus sur le chemin actif; les supprimer lors d'une passe d'encodage propre.
- [ ] Construire un contexte plus explicite pour le client :
  - question utilisateur
  - intention detectee
  - documents candidats
  - raisons de selection
  - chunks sources
  - cartes/resumes pertinents
  - consignes de non-hallucination
- [ ] Rendre clair quand le backend ne trouve rien.
- [ ] Rendre clair quand le backend trouve des documents faibles/incertains.
- [ ] Demander au LLM client de citer les sources disponibles.
- [ ] Demander au LLM client de dire "je n'ai pas trouve" uniquement si evidence pack vide ou non pertinent.
- [ ] Ajouter budget contextuel :
  - ne pas saturer le petit modele client
  - preferer chunks tres pertinents
  - inclure resume/carte quand utile
- [ ] Ajouter mode "exploration" pour questions larges :
  - plan de repas
  - recherche documentaire ouverte
  - comparaison de plusieurs documents
- [ ] Suite audit Mendel a traiter :
  - exploiter `document_summaries` pour intentions resume/vue globale
  - exposer qualite extraction/OCR dans les hits RAG ou evidence pack
  - resoudre les matches profil/cartes vers pages/chunks sourcables
  - conserver les metriques retrieval uniquement pour diagnostic/performance utilisateur
- [x] Utiliser `hypothetical_questions` dans le retrieval initial des `document_profiles`, pas seulement en annotation apres retrieval.
- [ ] Suite audit Heisenberg a traiter : scope RAG fin par categorie.
  - ajouter `CategoryPath` et `CategoryRef` a `RagSearchRequestDto`
  - resoudre via `DocumentsCategoryScopeResolver`
  - appliquer `d.doc_path LIKE categoryPath/%` dans exact/sparse/profile/metadata/linked/fallback SQL
  - post-filtrer dense/Qdrant via Postgres pour ne pas casser les points deja indexes
  - ajouter tests path/ref et compat legacy `Category`
- [ ] Suite audit Franklin a traiter : propagation qualite extraction/OCR.
  - enrichissement post-selection dans `/rag/search`
  - DTO nullable `extractionQuality` cote backend, contrats et client
  - agreger pessimiste sur hits multi-pages
  - conserver compact dans le ToolAgent sans en faire un filtre dur
  - ajouter tests backend/contrats/client
- [ ] Suite audit Wegener a traiter : utiliser `document_summaries` comme retriever.
  - retriever lexical summary pour questions larges/synthese/vue globale
  - seulement summaries frais par `source_hash`
  - fusionner scores avec chunks/profiles, sans appel LLM direct pendant la requete utilisateur

### 5.3 Qualite retrieval

- [ ] Ajouter dataset d'evaluation retrieval par theme.
- [ ] Pour chaque question test, stocker :
  - documents attendus
  - pages attendues si connues
  - type question
  - langue question
  - langue document cible
  - reponse attendue ou criteres de reponse
- [ ] Mesurer :
  - recall@1
  - recall@3
  - recall@5
  - MRR
  - presence source correcte
  - hallucination
  - temps backend
  - temps LLM client
- [ ] Ajouter export TSV/JSON des resultats.
- [ ] Ajouter comparaison entre versions d'ingestion/retrieval.

---

## 6. Client ToolAgent et experience utilisateur

### 6.1 Role du client

- [x] Client utilise son LLM local pour la discussion.
- [x] Backend fournit recherche RAG et evidence pack.
- [ ] Client doit exploiter les nouveaux artefacts backend :
  - summaries
  - content cards
  - document profile
  - extraction quality
  - confidence
  - language
- [ ] Client ne doit jamais contenir de logique specifique Cuisine.
- [ ] Client ne doit jamais contenir de nom de document indexe.
- [ ] Client peut utiliser des heuristiques generiques :
  - demande de recherche documentaire
  - demande de synthese
  - demande de planification
  - demande de comparaison
  - demande de liste
  - demande de recettes seulement si le corpus renvoie des documents cuisine pertinents, pas par code dur.

### 6.2 Reponses utilisateur

- [ ] Ameliorer reponse quand aucun document pertinent :
  - dire ce qui a ete cherche
  - proposer reformulation
  - ne pas inventer une source
- [ ] Ameliorer reponse quand documents faibles :
  - "j'ai trouve des elements proches..."
  - afficher incertitude
- [ ] Ameliorer reponse quand plusieurs documents :
  - synthese croisee
  - citations groupees
  - differences/contradictions
- [ ] Gerer les questions larges :
  - proposer plan
  - poser question de clarification seulement si necessaire
  - utiliser documents disponibles
- [ ] Gerer multi-tour :
  - l'utilisateur precise "l'inertage precisement"
  - conserver le sujet
  - relancer une recherche enrichie
- [ ] Gerer les demandes "trouve-moi les documents qui parlent de X".

### 6.3 Multilingue client

- [ ] Verifier toutes les nouvelles chaines UI.
- [ ] Ajouter ressources pour toutes les langues definies.
- [ ] Aucun texte backend brut non traduit dans l'UI utilisateur.
- [ ] Les statuts techniques admin peuvent rester plus techniques, mais doivent etre comprehensibles.
- [ ] Le client doit pouvoir poser une question FR sur un doc EN.
- [ ] Le LLM client doit pouvoir repondre dans la langue utilisateur tout en citant des sources dans la langue document.

### 6.4 Runtime LLM client

- [~] Claude avait identifie deux bugs plausibles :
  - runtime local arrete apres idle sans auto-restart
  - contexte 3072 trop petit pour certains prompts RAG
- [ ] Verifier si ces points sont deja corriges dans le code actuel.
- [ ] Si non, ajouter `EnsureRunningAsync` avant appel LLM local.
- [ ] Si non, ajuster ctx-size/client context budget selon modele et machine.
- [ ] Ajouter tests/regressions :
  - runtime stop idle puis question utilisateur
  - prompt RAG proche limite contexte
  - erreur llama-server HTTP 400 context overflow
  - streaming annule/retry
- [ ] Ne pas masquer les erreurs en "reponse non generee" sans diagnostic utile.

---

## 7. Tests Cuisine comme categorie pilote

### 7.1 Objectif

- [x] Cuisine sert de categorie exemple.
- [x] Le code produit ne doit pas etre cuisine-specific.
- [ ] Rendre Cuisine parfaitement fonctionnelle avant de generaliser aux autres themes.
- [ ] Utiliser Cuisine pour valider :
  - ingestion
  - OCR
  - enrichissements LLM serveur
  - retrieval multi-couches
  - evidence pack
  - reponse LLM client
  - citations
  - latence

### 7.2 Donnees et questions

- [x] Dossier documents cuisine existe cote local.
- [~] Documents cuisine en cours ou deja indexes selon etat serveur.
- [x] Dossier questions global prevu : `Questions LLM`.
- [x] Format actuel question/reponse cuisine deja discute.
- [ ] Inventorier les fichiers de questions Cuisine.
- [ ] Normaliser format recommande :
  - id
  - theme
  - question
  - langue question
  - documents attendus
  - pages attendues si connues
  - reponse attendue
  - criteres obligatoires
  - criteres interdits
  - type de question
  - difficulte
- [ ] Ne pas rendre le format trop lourd pour toi.
- [ ] Prevoir import automatique TSV/XLSX/JSON.

### 7.3 Scenarios cuisine a couvrir

- [ ] Recherche directe :
  - "quelle sauce avec une entrecote ?"
  - "donne-moi une recette de sauce au poivre"
- [ ] Question vague :
  - "je ne sais pas quoi faire pour les repas cette semaine"
  - "aide-moi a planifier des repas"
- [ ] Contraintes :
  - temps limite
  - ingredients disponibles
  - allergenes
  - budget
  - repas enfants/adultes
- [ ] Substitution :
  - remplacer ingredient
  - alternative sans alcool
  - alternative vegetarienne si documents le permettent
- [ ] Comparaison :
  - comparer deux sauces
  - plat rapide vs plat long
- [ ] Multi-doc :
  - combiner entree/plat/dessert
  - menu semaine a partir de plusieurs PDFs
- [ ] Multi-tour :
  - question vague puis precision
  - "plutot boeuf"
  - "et une sauce ?"
- [ ] Source/citation :
  - demander quel document parle de X
  - demander preuve/source
- [ ] Negative tests :
  - sujet absent
  - demander une recette non presente
  - demander une info dangereuse/non couverte
- [ ] Multilingue :
  - question FR sur doc EN
  - question EN sur doc FR
  - termes culinaires ambigus

### 7.4 Criteres d'acceptation Cuisine

- [ ] Pour questions directes : document correct dans top 3.
- [ ] Pour questions directes : source correcte citee.
- [ ] Pour questions larges : reponse utile, structuree, sans inventer de documents.
- [ ] Pour plan repas : proposition coherente basee sur corpus, avec alternatives si corpus limite.
- [ ] Pour sauce/plat : recommandation argumentee et sourcee.
- [ ] Pour absence de donnees : le modele le dit clairement.
- [ ] Taux hallucination cible : 0 sur questions avec reponse connue.
- [ ] Temps backend retrieval cible a definir.
- [ ] Temps generation client cible a definir selon modele.
- [ ] Resultats exportes et comparables entre runs.

---

## 8. Generalisation aux 31 themes futurs

- [x] Les themes futurs auront chacun leurs propres PDFs et questions.
- [x] Le systeme doit rester adaptatif.
- [ ] Documenter le format ideal pour chaque theme.
- [ ] Prevoir un script d'inventaire :
  - nombre PDFs
  - nombre pages
  - langues detectees
  - OCR needed/applied
  - enrichissements manquants
  - questions disponibles
- [ ] Prevoir une campagne par theme :
  1. ingestion brute
  2. qualite extraction/OCR
  3. enrichissements LLM serveur
  4. tests retrieval
  5. tests reponse LLM client
  6. rapport
- [ ] Ne jamais ajouter d'heuristique propre a un theme dans le code produit.
- [ ] Ajouter seulement des heuristiques generiques qui s'appuient sur les artefacts dynamiques.

---

## 9. Capacite serveur, modeles, slots et licences

### 9.1 Positionnement runtime

- [x] Client : LLM local par utilisateur pour discussion.
- [x] Serveur : LLM pour ingestion/enrichissement/backoffice.
- [ ] Formaliser modeles supportes :
  - client faible
  - client recommande
  - serveur faible capacite
  - serveur recommande
  - serveur uniquement gros modele
- [ ] Garder petits modeles disponibles pour serveurs peu puissants.
- [ ] Nettoyer les modeles non utilises sur le serveur pour liberer disque.
- [ ] Conserver seulement :
  - modele serveur recommande
  - petits modeles supportes
  - modeles de bench utiles si justifies
- [ ] Documenter pourquoi les gros modeles A3B/27B ne sont pas choix confort sur ce laptop serveur si confirme par bench.

### 9.2 Calcul installation selon serveur + seats

- [ ] A l'installation, calculer :
  - CPU
  - RAM
  - GPU/VRAM
  - disque libre
  - runtime disponible
  - nombre de seats licence
- [ ] En deduire :
  - modele serveur recommande
  - nombre d'instances LLM serveur
  - nombre de slots
  - taille file d'attente
  - concurrency ingestion/enrichissement
- [ ] Refuser configuration absurde :
  - trop de seats pour machine faible sans degrade mode
  - trop d'instances LLM
  - modele trop lourd
- [ ] Proposer degrade mode :
  - enrichissements plus lents
  - resume long desactive
  - petit modele
  - moins de slots

### 9.3 Changement de licence

- [ ] Au changement de licence, verifier si le nombre de seats a change.
- [ ] Si seats change :
  - recalculer plan capacite
  - comparer ancien/nouveau plan
  - appliquer si compatible
  - alerter admin si materiel insuffisant
- [ ] Ajouter tests :
  - seats augmente
  - seats baisse
  - serveur faible
  - serveur GPU
  - changement sans impact

### 9.4 Concurrence et 100 utilisateurs

- [x] 100 utilisateurs ne signifient pas 100 modeles serveur.
- [x] Les discussions utilisateur tournent sur les LLM clients.
- [ ] Backend doit gerer 100 requetes RAG concurrentes avec bulkheads.
- [ ] TEI/Qdrant/Postgres doivent avoir limites et queues.
- [ ] Le LLM serveur doit rester file d'attente backoffice, pas bloquer le chat.
- [ ] UX utilisateur :
  - ne pas donner l'impression d'etre ignore
  - afficher progression/retry si backend charge
  - differencier recherche RAG lente vs generation client lente
- [ ] Ajouter load tests :
  - 10 utilisateurs
  - 25 utilisateurs
  - 50 utilisateurs
  - 100 utilisateurs
- [ ] Mesurer :
  - latence `/rag/search`
  - erreurs TEI/Qdrant
  - saturation Postgres
  - CPU/RAM
  - queue length

---

## 10. Operations serveur et hygiene

### 10.1 Serveur

- [x] Backend deploye apres OCR.
- [x] `/ready` OK.
- [ ] Verifier scripts d'arret automatique a 23h59 et documenter/desactiver proprement si necessaire.
- [ ] Ajouter runbook "garder serveur allume toute la nuit".
- [ ] Ajouter runbook "verifier OCR".
- [ ] Ajouter runbook "reindex document".
- [ ] Ajouter runbook "nettoyer modeles".
- [ ] Ajouter runbook "diagnostiquer document bloque".

### 10.2 Nettoyage modeles serveur

- [ ] Inventorier `/opt/saaia/models` ou chemin reel.
- [ ] Identifier modeles utilises par compose/config.
- [ ] Identifier modeles supportes a conserver.
- [ ] Identifier modeles bench/anciens a supprimer.
- [ ] Avant suppression :
  - verifier aucun service ne pointe dessus
  - verifier espace gagne
  - conserver checksums/catalogue si besoin
- [ ] Supprimer seulement apres liste de validation.

### 10.3 Nettoyage depot

- [!] Le depot est sale et contient beaucoup de modifications non commitees.
- [ ] Ne jamais revert des changements utilisateur.
- [ ] Inventorier les fichiers modifies.
- [ ] Classer :
  - changements OCR/ingestion a garder
  - changements runtime/enrichissements a garder
  - artefacts temporaires a supprimer
  - vieux fichiers hors mois de mai a evaluer
- [ ] Nettoyer `out/` et artefacts generes si non necessaires.
- [ ] Mettre a jour `.gitignore` si des artefacts reviennent.
- [ ] Ne pas supprimer aveuglement tout ce qui est vieux : certains fichiers anciens sont source produit.
- [ ] Supprimer uniquement les artefacts obsoletes clairement identifies.
- [ ] Faire un `git diff --check`.
- [ ] Faire builds/tests avant tout commit.

---

## 11. Securite, gouvernance et audit

- [x] Qdrant API key configuree.
- [x] Config signee presente et verifiee.
- [ ] Verifier que les nouveaux endpoints admin sont bien sous `X-Admin-Key`.
- [ ] Verifier que les enrichissements ne leakent pas secrets/logs.
- [ ] Ajouter audit logs pour :
  - reindex OCR force
  - pause/resume enrichissements
  - suppression modeles
  - recalcul capacite licence
  - cleanup documents/modeles
- [ ] Verifier support bundle inclut diagnostics OCR/enrichissements sans contenu sensible excessif.
- [ ] Ajouter redaction si resume/enrichissement peut contenir donnees sensibles.

---

## 12. Qualite LLM : ne pas brider, guider

- [x] Clarification importante : on ne met pas des batons dans les roues du LLM.
- [x] Objectif : fournir au LLM client le bon contexte, propre, source, compact.
- [x] Le LLM reste le cerveau conversationnel, mais il ne doit pas deviner ce que le retrieval n'a pas trouve.
- [ ] Prompt client doit encourager :
  - raisonnement sur evidence pack
  - synthese
  - decisions utiles
  - clarifications si necessaire
  - citations
- [ ] Prompt client doit interdire :
  - inventer des sources
  - affirmer absence si recherche incertaine
  - ignorer documents recuperes
- [ ] Ajouter tests de jugement qualite :
  - coherence
  - source
  - completude
  - non hallucination
  - utilite utilisateur
- [ ] Ajouter evaluation automatique ou semi-automatique des 294 questions Cuisine.
- [ ] Ajouter rapport humain lisible.

---

## 13. Ordre d'execution recommande

### Phase A - Stabiliser ce qui vient d'etre fait

- [ ] Refaire un `git diff` complet.
- [ ] Verifier que les migrations OCR/content cards sont ordonnees et non conflictuelles.
- [x] Verifier que les endpoints admin exposent correctement les nouveaux champs OCR.
- [x] Reexecuter backend tests et client ToolAgent tests.
- [ ] Faire un petit rapport "OCR actuel".

### Phase B - LLM serveur idle enrichissement v1

- [ ] Implementer coordinateur idle ingestion.
- [ ] Implementer queue enrichissement document.
- [ ] Implementer resume court + mots-cles + content cards LLM.
- [ ] Stocker langue document, model, prompt version, revision id.
- [ ] Ajouter endpoints admin lecture/statut.
- [ ] Ajouter tests fake LLM.

### Phase C - Retrieval enrichi

- [ ] Materialiser content cards/profiles pour recherche rapide.
- [ ] Ajouter embeddings optionnels sur cartes/resumes.
- [ ] Modifier `/rag/search` pour fusionner chunks + cards + profiles + summaries.
- [ ] Ajouter scoring/telemetry.
- [ ] Ajouter tests retrieval.

### Phase D - Client consomme le nouveau contexte

- [ ] Adapter evidence pack cote client.
- [ ] Adapter prompt ToolAgent.
- [ ] Ajouter traductions UI.
- [ ] Ajouter tests ToolAgent.

### Phase E - Campagne Cuisine

- [ ] Reindexer Cuisine avec pipeline final.
- [ ] Generer/enrichir documents Cuisine.
- [ ] Lancer toutes les questions Cuisine.
- [ ] Produire rapport par question.
- [ ] Corriger jusqu'a qualite cible.

### Phase F - Generalisation

- [ ] Appliquer meme campagne aux autres themes.
- [ ] Corriger uniquement generique.
- [ ] Mettre a jour docs/runbooks.

---

## 14. Definition de "parfait" pour l'ingestion

Un document est considere bien ingere si :

- [ ] le fichier est detecte automatiquement
- [ ] la langue document est detectee ou fallback clair
- [ ] le texte natif est extrait quand il existe
- [ ] les PDFs scannes passent par OCR
- [ ] les images textuelles dans PDFs natifs sont OCRisees
- [ ] les textes OCR utiles sont ajoutes
- [ ] les doublons natif/OCR sont filtres
- [ ] les chunks sont lisibles
- [ ] les pages sources sont conservees
- [ ] les metadata qualite sont stockees
- [ ] les enrichissements LLM sont faits ou planifies
- [ ] le document apparait dans les recherches attendues
- [ ] l'utilisateur peut obtenir une reponse sourcee
- [ ] les echecs sont visibles et actionnables

---

## 15. Definition de "parfait" pour les reponses LLM

Une reponse est consideree bonne si :

- [ ] elle repond vraiment a la question
- [ ] elle utilise les documents quand ils existent
- [ ] elle cite les bonnes sources
- [ ] elle ne cite pas de documents non pertinents
- [ ] elle ne pretend pas qu'il n'y a rien si le corpus contient la reponse
- [ ] elle ne sort pas un dump technique
- [ ] elle reste dans la langue de l'utilisateur
- [ ] elle signale l'incertitude si retrieval faible
- [ ] elle pose une question de clarification seulement quand c'est utile
- [ ] elle reste utile meme pour une question vague
- [ ] elle respecte les limites du petit modele client

---

## 16. Points de doute a surveiller

- [!] OCR page complete sur pages avec images peut relire du texte natif et ajouter du bruit si filtre insuffisant.
- [!] OCR par zones/crops sera peut-etre necessaire pour documents tres visuels.
- [!] Les gros resumes LLM peuvent etre couteux : a garder en idle/backoffice.
- [!] Les petits modeles serveur peuvent produire des enrichissements moyens : besoin de tests qualite.
- [!] Les questions vagues demandent une couche profil/resume, pas seulement chunks.
- [!] Le multilingue retrieval FR<->EN doit etre teste serieusement.
- [!] Nettoyage Git et nettoyage serveur doivent etre faits prudemment pour ne pas supprimer des sources utiles.
- [!] Les 31 themes futurs vont exposer des cas que Cuisine ne couvre pas.

---

## 17. Update 2026-05-28 - analyse projets RAG externes, writer libre et observabilite

### 17.1 Verdict architecture

- [x] Conserver l'architecture SAAIA existante : client Windows avec LLM local pour le chat utilisateur, backend serveur pour ingestion/RAG/backoffice, LLM serveur strictement backoffice.
- [x] Ne pas repartir de zero : SAAIA possede deja un contrat RAG riche, une ingestion avancee, des profils documentaires, des content cards, des signaux OCR/qualite et un ToolAgent prudent.
- [ ] Rendre ces signaux observables, testables et pilotables au lieu de creer un nouveau `EvidencePack` concurrent.
- [ ] Appliquer le principe directeur : sources strictes, redaction libre.

### 17.2 Inspirations externes a reprendre

- [ ] RAGFlow :
  - ingestion avancee ;
  - chunking parent-child ;
  - parsing PDF/OCR/layout ;
  - tests retrieval ;
  - contexte table/image/section ;
  - extraction de table des matieres en signal documentaire.
- [ ] Dify :
  - retrieval lab administrable ;
  - test de requete avec chunks, scores, seuils, rerank et citations ;
  - parent-child retrieval ;
  - metadonnees et filtrage query-time.
- [ ] Open WebUI :
  - configuration RAG pragmatique ;
  - hybrid search BM25 + vecteur ;
  - rerank configurable ;
  - seuils, topK, citations, reindex.
- [ ] paperless-gpt :
  - OCR/enrichissement documentaire securise ;
  - modes `append`, `update`, `replace` ;
  - prompts configurables mais gouvernes ;
  - validation manuelle ;
  - garde-fous avant actions destructives.
- [ ] Haystack :
  - pipeline explicite, testable et tracable ;
  - composants converter/cleaner/splitter/retriever/ranker/evaluator.
- [ ] AnythingLLM :
  - provenance/resync documents ;
  - separation source brute, chunks et metadonnees.
- [~] paperless-ai :
  - conserver les idees de workflow auto/manual, regles de traitement et chat documentaire ;
  - ne pas le prendre comme base produit car projet indique comme non maintenu.

Sources consultees :

- `https://github.com/icereed/paperless-gpt`
- `https://github.com/clusterzx/paperless-ai`
- `https://docs.dify.ai/en/use-dify/knowledge`
- `https://github.com/infiniflow/ragflow`
- `https://docs.openwebui.com/features/chat-conversations/rag/`
- `https://haystack.deepset.ai/`
- `https://github.com/Mintplex-Labs/anything-llm`

### 17.3 Ce que SAAIA a deja et doit capitaliser

- [x] Contrat RAG riche dans `contracts/SAAIA.Contracts/ApiContracts.cs` :
  - timings TEI/Qdrant/rerank ;
  - qualite extraction/OCR ;
  - content cards ;
  - selection hints ;
  - source hash ;
  - pages ;
  - chunks ;
  - signaux de provenance.
- [x] Ingestion deja fortement instrumentee dans `backend/SAAIA.Backend/Ingestion/DocumentFoundationRepo.cs` :
  - qualite OCR/page/chunk ;
  - artefacts de revision ;
  - profils documentaires ;
  - content cards ;
  - diagnostics extraction.
- [x] ToolAgent deja capable de :
  - `rag.multi_search` ;
  - prudence source-backed ;
  - fallback sans hallucination ;
  - propagation de nombreuses metadonnees sources.
- [~] Console admin en cours :
  - utile mais encore trop eclatee ;
  - a transformer en vrai RAG Workbench coherent.

### 17.4 Vrai manque identifie

- [!] Le probleme principal n'est pas "le LLM est nul".
- [!] Le probleme principal est que le writer recoit parfois un paquet de preuves trop pauvre, trop redondant, trop peu diversifie ou trop mal controle.
- [ ] Avant le writer, verifier automatiquement :
  - nombre de sources utiles ;
  - diversite documents/pages/chunks ;
  - presence des elements demandes ;
  - qualite extraction/OCR ;
  - contenu reel vs navigation/sommaire/index ;
  - risque de repetition excessive ;
  - coherence avec la forme attendue de la question.
- [ ] Si une demande large exige de nombreux elements distincts, ne pas laisser le writer remplir par repetition ou extrapolation.
- [ ] Si les preuves sont insuffisantes :
  - relancer une recherche elargie ;
  - utiliser profils, content cards, categories et contexte parent ;
  - si toujours insuffisant, expliquer clairement la limite et proposer une recherche etendue.

### 17.5 RAG Diagnostics / Retrieval Lab

- [ ] Ajouter une page admin "Test Retrieval" dans la console admin.
- [ ] Pour une requete donnee, afficher :
  - requete originale ;
  - expansions ;
  - langue detectee ;
  - categorie/corpus utilise ;
  - exact match ;
  - sparse/BM25 ;
  - dense/Qdrant ;
  - RRF/fusion ;
  - rerank ;
  - autocut ;
  - candidats gardes ;
  - candidats rejetes ;
  - raison de selection/rejet ;
  - score dense ;
  - score lexical ;
  - score rerank ;
  - source ;
  - page ;
  - chunk ;
  - section/parent ;
  - timings TEI/Qdrant/rerank ;
  - paquet de preuves final envoye au client/writer.
- [ ] Ajouter une vue "Chunks" :
  - `chunk_text` ;
  - `contextual_text` ;
  - page ;
  - section ;
  - type logique ;
  - qualite ;
  - OCR ;
  - source hash ;
  - content cards liees.
- [ ] Ajouter une vue "documents faibles" :
  - sans profil ;
  - sans content cards ;
  - OCR faible ;
  - trop de chunks rejetes ;
  - doublons probables ;
  - table des matieres/index dominants.

### 17.6 Evidence Gate avant Writer

- [ ] Ajouter un `SourceBackedEvidenceGate` generique avant l'appel writer.
- [ ] Mesurer :
  - sources utiles ;
  - documents distincts ;
  - pages distinctes ;
  - chunks distincts ;
  - actionability/support score ;
  - roles `actionable_item`, `supporting_context`, `advisory`, `navigation`, `low_confidence` ;
  - qualite OCR/extraction ;
  - presence de contraintes utilisateur.
- [ ] Pour les syntheses larges :
  - exiger une diversite minimale ;
  - limiter les doublons document/page ;
  - declencher une expansion retrieval si couverture insuffisante.
- [ ] Pour les questions exactes :
  - preferer la meilleure source primaire ;
  - ne pas melanger des valeurs/etapes de plusieurs documents sauf comparaison explicite.
- [ ] Pour les demandes sans preuves suffisantes :
  - ne pas inventer ;
  - ne pas faire une reponse maigre remplie de repetitions ;
  - demander si l'utilisateur veut une recherche elargie ou expliquer la limite.

### 17.7 Writer plus libre, faits stricts

- [ ] Separer les roles LLM :
  - router/planner : strict, temperature basse ;
  - evidence gate : deterministe ;
  - writer : redaction libre et utile ;
  - critic : controle factuel sans casser le style.
- [ ] Autoriser le Markdown simple dans les reponses utilisateur :
  - titres courts ;
  - `**gras**` ;
  - listes propres ;
  - tableaux simples quand utile.
- [ ] Remplacer les consignes trop bloquantes type `Return plain text only` pour les reponses finales RAG par un contrat de rendu plus humain.
- [ ] Demander au writer :
  - de repondre directement ;
  - de ne pas repeter la question ;
  - de structurer selon la demande ;
  - de corriger les degats OCR evidents sans changer les faits ;
  - de corriger accents, espaces casses et mots colles quand c'est sans risque ;
  - de ne pas faire un dump brut des extraits ;
  - de mettre les limites apres la proposition utile, pas en ouverture defensive.
- [ ] Garder interdiction stricte d'inventer :
  - sources ;
  - pages ;
  - quantites ;
  - etapes ;
  - valeurs ;
  - compatibilites ;
  - obligations ;
  - conclusions certifiees.
- [ ] Ajuster les temperatures par role :
  - planner/router : `0.0-0.2` ;
  - writer : `0.35-0.5` ;
  - critic : `0.0-0.2` ;
  - resume/enrichissement backoffice : `0.2-0.35`.

### 17.8 Critic intelligent

- [ ] Le critic doit corriger seulement si :
  - fait invente ;
  - source absente ;
  - page fausse ;
  - quantite non sourcee ;
  - etape non sourcee ;
  - conclusion trop forte ;
  - contrainte utilisateur ignoree ;
  - citation/document invente.
- [ ] Le critic ne doit pas transformer une bonne reponse en reponse froide.
- [ ] Ajouter des tests ou le writer produit une reponse belle et correcte que le critic doit laisser intacte.

### 17.9 Manifest d'ingestion unifie

- [ ] Creer un manifest lisible par document/job/revision avec :
  - hash source ;
  - taille/mtime ;
  - version parser ;
  - version OCR ;
  - version chunking ;
  - modele embedding ;
  - pages extraites ;
  - pages OCRisees ;
  - chunks crees ;
  - chunks rejetes ;
  - raisons de rejet ;
  - sections detectees ;
  - profils produits ;
  - content cards produites ;
  - erreurs ;
  - timings ;
  - etat Capability B / resume serveur.
- [ ] Exposer le manifest dans l'admin.
- [ ] Ajouter telemetry ingestion :
  - compteurs ;
  - histogrammes par phase ;
  - erreurs normalisees ;
  - durees OCR/parser/chunk/embed/qdrant.

### 17.10 Parent-child retrieval

- [ ] Formaliser :
  - child = chunk precis optimise retrieval ;
  - parent = section/unite/paragraphe voisin pour contexte writer.
- [ ] Rechercher sur child, fournir parent au writer si utile.
- [ ] Garder citation exacte au niveau document/page/chunk.
- [ ] Ne pas gonfler le contexte avec des parents non pertinents.
- [ ] Ajouter tests :
  - question exacte ;
  - question large ;
  - procedure ;
  - comparaison ;
  - source avec table des matieres bruitee.

### 17.11 LLM serveur backoffice

- [ ] Utiliser le LLM serveur uniquement pour ameliorer l'index, pas pour repondre directement aux utilisateurs.
- [ ] En idle/backoffice, generer :
  - resumes documentaires ;
  - mots-cles ;
  - titres normalises ;
  - questions hypothetique par section ;
  - detection table des matieres vs contenu reel ;
  - validation/nettoyage de chunks ;
  - captions images/tableaux si OCR/layout disponible ;
  - mini-profils documentaires.
- [ ] Mettre en place modes de mise a jour inspires paperless-gpt :
  - `append` par defaut ;
  - `update` sous score de confiance suffisant ;
  - `replace` admin only avec confirmation, diff, audit et rollback.

### 17.12 Recherche elargie generique

- [ ] Detecter la forme de demande :
  - planning ;
  - recommandation ;
  - comparaison ;
  - checklist ;
  - procedure ;
  - liste de documents ;
  - resume ;
  - question exacte.
- [ ] Adapter le retrieval sans hardcoding metier :
  - exact ;
  - large ;
  - categorie ;
  - profils ;
  - content cards ;
  - parent/context ;
  - multilingue.
- [ ] Pour les demandes larges :
  - chercher plusieurs candidats distincts ;
  - ne pas remplir une grille par repetitions faibles ;
  - proposer une banque de candidats quand le corpus ne couvre pas assez ;
  - demander recherche elargie si necessaire.

### 17.13 Tests et validation

- [ ] Tests cuisine :
  - planning semaine ;
  - idees rapides ;
  - recette ingredients/etapes/source ;
  - sauce/accompagnement ;
  - exclusion `sans X` ;
  - document inexistant ;
  - question ambigue ;
  - source faible/OCR faible.
- [ ] Tests generiques hors cuisine :
  - planning maintenance ;
  - planning formation ;
  - comparaison fournisseurs/documents ;
  - procedure securite ;
  - checklist ;
  - resume ;
  - recherche de documents.
- [ ] Tests multilingues client :
  - FR ;
  - EN ;
  - ES ;
  - PT ;
  - DE ;
  - IT.
- [ ] Tests UI :
  - Markdown simple rendu correctement ;
  - gras lisible ;
  - tableaux lisibles ;
  - sources cliquables ;
  - PDF ouvert a la bonne page ;
  - theme clair/sombre ;
  - textes localises.

### 17.14 A ne pas faire

- [!] Ne pas importer Dify/RAGFlow/Open WebUI comme dependances lourdes.
- [!] Ne pas coder des regles cuisine/planning/recette/PDF en dur.
- [!] Ne pas laisser le LLM inventer quand les sources sont faibles.
- [!] Ne pas augmenter `topK` partout sans controle.
- [!] Ne pas remplacer PDF ou metadonnees sans mode audit/rollback.
- [!] Ne pas rendre les prompts admin libres sans versioning et garde-fous.
- [!] Ne pas deplacer le chat utilisateur vers le LLM serveur.
- [!] Ne pas confondre "redaction libre" et "faits libres".

### 17.15 Ordre prioritaire recommande

1. [ ] Writer plus libre + Markdown simple.
2. [ ] Critic factuel qui preserve le style.
3. [ ] Evidence Gate avant writer.
4. [ ] Expansion retrieval generique si preuves insuffisantes.
5. [ ] Deduplication sources/pages/chunks avant writer.
6. [ ] Tests cuisine representatifs.
7. [ ] RAG Diagnostics / Retrieval Lab.
8. [ ] Manifest d'ingestion unifie.
9. [ ] Parent-child retrieval.
10. [ ] Enrichissements LLM serveur backoffice.
