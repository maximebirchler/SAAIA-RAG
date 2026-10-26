# Audit de la validation réelle de l’ingestion et des moyens locaux

Périmètre : lecture des sources, manifests et ressources locales le 25 octobre 2026. Aucun backend, OCR, Docling, conteneur, base ou test .NET démarré. Aucun appel fournisseur, accès à un secret ou changement de source. Les seuls calculs exécutés sont des inventaires locaux, la lecture du nombre de pages des PDF et les normaliseurs Python purs avec cache bytecode désactivé. Le PostgreSQL de la campagne en cours n’a pas été interrogé ni modifié.

Ce document propose un protocole d’acceptation. Il ne constitue pas une acceptation produit et ne prétend pas qu’une ingestion « parfaite » de tout PDF possible puisse être démontrée par un corpus fini.

## Constats prioritaires

1. **Un PASS Unit peut ne pas avoir exécuté d’OCR.** `backend/SAAIA.Backend.Tests/PdfOcrTextExtractorTests.cs:26–30` déclare `Real_scanned_pdf_ocr_extracts_text_when_opted_in` comme un `[Fact]` ordinaire et retourne immédiatement si `SAAIA_TEST_OCR_E2E` n’est pas `1`. Ce n’est ni un skip explicite ni un profil distinct. Compter ce PASS parmi les preuves d’OCR réel serait faux. Lorsqu’il est activé, ce test utilise réellement les commandes OCR et un PDF raster synthétique, mais ne passe pas par le worker ou la publication SQL.

2. **Le Gold évalue une conversion et une projection, pas l’ingestion complète.** `tools/ingestion_gold/runner.py` appelle directement `/v1/convert/file` puis le projecteur C#. `canonical_projection/Program.cs:45–80` appelle l’adaptateur, les réconciliations et le projecteur canonique ; `:114` écrit un snapshot JSON. Il n’exécute pas la queue, le worker, la publication SQL ou les embeddings. La formulation « sortie canonique réellement publiée par le backend » de `tools/ingestion_gold/README.md:41–43` dépasse cette preuve. La projection est réelle, la publication en base n’a pas lieu dans ce programme.

3. **Les paramètres Gold ordinaires diffèrent du produit.** `Program.cs:16–17` choisit 220 mots maximum et un minimum de 1 ; `IngestionOptions.cs:22–24` choisit 200/25. Le runner ne transmet pas d’options pour les aligner. Le helper spécifique `--native-paid-corpus` utilise les options de production, mais reste une projection de 1 à 8 pages sélectionnées et déclare explicitement `ingestionWorkerExecuted=false` (`NativePaidCorpusProjection.cs:8,20,103`). Aucun des deux chemins ne doit être présenté comme validation du worker.

4. **Les métriques de texte Gold peuvent masquer une erreur critique.** `evaluator.py:16–18` supprime la ponctuation ; son normaliseur confond effectivement `Maximum 1.5 bar` et `Maximum 15 bar`, ainsi que `Limit +5 C` et `Limit -5 C`. `canonical_evaluator.py:21–25` confond aussi les signes +/−. Ces collisions ont été évaluées localement sur les fonctions pures, sans extraction ni réseau. Les marqueurs au seuil 0,95 et `SequenceMatcher` restent utiles pour un diagnostic OCR global, mais ne prouvent pas la fidélité d’une valeur, d’une unité, d’un signe ou d’une négation.

5. **Un corpus vide peut réussir.** `fixtures.py:465–493` accepte un manifeste valide dont `cases` est vide ; `runner.py:323` calcule ensuite `all([])`, qui vaut `true`. Une qualification doit exiger un nombre positif et exact de cas, leur identité complète et les étapes réellement exécutées. `--skip-canonical` est un mode de diagnostic brut qui ne doit pas satisfaire une gate canonique/E2E.

6. **La reproductibilité raster dépend d’une police non épinglée.** `fixtures.py:319–329` choisit Arial sous Windows, Segoe UI sinon, DejaVu sous Linux, puis une police par défaut. Les PDF raster sont annoncés déterministes et leurs hashes sont comparés ; changer de machine peut donc provoquer un drift légitime. Il faut figer la police autorisée et son hash, ainsi que les versions ReportLab/Pillow, ou publier des variantes de corpus explicitement distinctes. Il ne faut pas retirer la vérification SHA pour masquer cette différence.

7. **Les outils existants visent parfois l’infrastructure habituelle.** Le README Gold propose un exemple SSH ; `run-ingestion-quality-checks.ps1:1–8` cible par défaut le serveur et ses conteneurs. Ce sont des ressources de diagnostic à adapter à une nouvelle instance détenue par la campagne, pas des commandes à lancer telles quelles sur cette machine. Aucune de ces commandes n’a été exécutée dans cet audit.

## Ce que les moyens actuels prouvent

| Moyen | Partie réellement exercée | Partie non démontrée |
| --- | --- | --- |
| Tests PDF de rétention/disposition | PDF synthétiques réels, PdfPig, conservation et ordre sur leurs cas | Tous les PDF, OCR, queue, publication et vecteurs |
| `DoclingClientTests` | Contrat HTTP, bornes et erreurs avec `StubHttpClientFactory` | Exécution réelle de Docling ou des modèles OCR |
| `DoclingCanonicalDocumentAdapterTests` | Projection d’un objet Docling construit par le test | Conversion d’un vrai PDF par le moteur |
| Gold avec Docling réel | Conversion du PDF synthétique et réconciliation/projection canoniques | Worker, transactions de publication, embeddings, reprise |
| IntegrationPg de publication | PostgreSQL réel isolé et fonctions de publication/recherche | OCR et worker si les objets sont construits ou les jobs seedés |
| Probe vertical local | PDF réel, extraction native, `CompleteUpsertAsync`, HTTP réel et client agent | Worker et services hébergés retirés du host, OCR, vecteurs |
| Campagne locale Luna/native corpus | PDF réel, projection native, SQL et outils canoniques réels | Worker d’ingestion, Docling/OCR, qualité vectorielle |

Le probe vertical est explicite : `DocumentFoundationIntegrationTests.LocalVertical.cs:63–65` seede un job puis appelle directement `CompleteUpsertAsync`; `:131` retire les services hébergés du backend. Ce test ne doit donc pas devenir une preuve du worker sous prétexte que son API/client sont réels.

L’environnement WinUI isolé désactive l’ingestion, le watcher et le scanner (`start-environment.ps1:109`). Le préflight payé déclare correctement `ingestionValidated=false` et des substituts TEI/Qdrant (`Test-OwnedRealCorpus.ps1:27,51`). Ce marquage doit rester vrai après une réussite des quatre pages projetées. Une vérification SQL des checksums prouve les octets stockés, pas l’achèvement du pipeline d’ingestion.

La recherche des instanciations du worker dans les tests n’a pas trouvé de `new IngestionWorker`. Les tests nommés `IngestionWorkerQdrantTests` observés vérifient des politiques/helpers. Les tests PostgreSQL de publication restent précieux ; leur portée ne doit pas être étendue à une boucle de worker qu’ils n’exécutent pas.

## Corpus déjà disponible

Le manifeste `config/ingestion-layout-gold.v1.json` contient huit PDF synthétiques d’une page, avec générateurs et hashes :

| Cas | Couverture |
| --- | --- |
| `native-two-column-v1` | Deux colonnes, ordre et en-tête/pied |
| `native-three-column-v1` | Trois colonnes |
| `native-unequal-columns-v1` | Colonnes de longueurs différentes et note traversante |
| `native-late-page-title-v1` | Ordre des objets PDF différent de l’ordre visuel |
| `native-columns-and-table-v1` | Colonnes, tableau et légende |
| `structured-table-v1` | Cellules fusionnées, vides, multiligne et spans |
| `mixed-image-text-v1` | Texte natif, panneau raster, légende et liste ; OCR forcé |
| `full-page-scan-v1` | Scan raster multilingue, faible contraste et légère rotation ; OCR forcé |

Le contenu est principalement anglophone ; le scan contient aussi des lignes FR/DE/IT sans forte couverture des accents et symboles critiques. Tous les cas sont monopages : ils ne suffisent pas pour les transitions de page, répétitions, préambules, continuations de tableau ou changements de sujet.

Trois PDF locaux supplémentaires sont présents, lus seulement pour leurs comptes :

| Fichier local sous `out/remote-deploy` | Pages | Octets |
| --- | ---: | ---: |
| `30-recettes-preferees-des-francais.pdf` | 36 | 1 436 547 |
| `nist-complex-source.pdf` | 53 | 338 329 |
| `sms-1145-scanned-source.pdf` | 5 | 1 781 528 |

Ce sont des témoins réels à conserver localement avec leur hash et des annotations attendues séparées. Le document NIST ne doit pas être rebaptisé CSF 2.0 : la campagne précédente a identifié NIST 800-60 volume I révision 1. Les documents tiers ne doivent pas être recopiés automatiquement dans Git.

## Petit complément de corpus à construire localement

Les attentes doivent être figées avant les corrections, rester hors du runtime et annoter page physique, texte, géométrie et relations attendues. Huit compléments courts produiraient une base de seize documents synthétiques :

1. Document natif FR/EN de deux pages : accents, ligatures, µ, m², fractions, virgule/point décimal, signes +/−, comparateurs, négations, qualificateurs courts et valeurs répétées.
2. Document de trois pages : titres hiérarchiques et signets, préambule avant le premier titre, note continuant la page précédente, puis changement réel de sujet. Les annotations distinguent relation de section certaine et simple voisinage physique.
3. Tableau de deux pages : en-têtes répétés, cellules fusionnées, cellules réellement vides, unités dans l’en-tête et continuation. Chaque valeur critique possède une coordonnée ligne/colonne attendue.
4. Page native avec plusieurs petits panneaux raster : labels courts, états ON/OFF et avertissement. Un label OCR ne doit pas être inventé, supprimé ou dupliqué à cause du calque natif voisin.
5. Scan FR sans calque texte caché : quantités et avertissements exacts, à résolution normale puis dégradée contrôlée. La version difficile peut être refusée explicitement ; elle ne doit pas produire de faux succès silencieux.
6. Scan EN de tableau et formulaire : page tournée, cases vides, valeur manuscrite non prise en charge et signature graphique. Les champs non reconnus restent manquants et tracés.
7. PDF mixte de trois pages, native/raster/native, avec texte OCR déjà présent et valeurs contradictoires selon les pages. Il faut préserver la page et la version de chaque fait et éviter les doublons.
8. Figure/diagramme avec légende, labels raster et image contenant une sous-figure : l’inventaire doit conserver les placements, polygones et provenance, sans prétendre une interprétation sémantique de l’image non réalisée.

Les corpus raster doivent figer police, résolution, angle, contraste, version du générateur et hash. Les originaux générateurs et les annotations sont la vérité de test ; un passage préalable par le parseur testé ne peut pas fabriquer son propre attendu.

## Moyens locaux observés et conditions manquantes

Disponibles dans l’environnement de confiance inspecté : Python avec Pillow, ReportLab et pypdf ; .NET ; Poppler `pdftoppm` et `pdfinfo`. Le binaire WSL est accessible, mais aucune distribution, VM, image ou service WSL n’a été lancé ou validé.

Non trouvés sur PATH : Tesseract, ocrmypdf, Ghostscript, Docling et Docker. Dans le Python de confiance, les modules Docling, RapidOCR, ONNX Runtime, Torch et ocrmypdf ne sont pas présents. Cette observation ne prouve pas une absence absolue dans d’autres environnements ; aucune exploration de secrets ou activation d’un autre runtime n’a été tentée.

Le dépôt fournit le Dockerfile Docling CPU épinglé et son wrapper (`infra/docling-sidecar`), ses tests de hiérarchie/réparation de tableaux et ses contrats de provenance. Il manque encore une exécution locale vérifiée de ce service avec modèles OCR disponibles hors réseau, versions/hashes observés et readiness réelle. Une image ou un module installé ne suffit pas : la première conversion doit prouver que le modèle est chargé sans téléchargement implicite ni appel externe.

Pour le parcours OCR hérité, il faut en plus les commandes et langues réelles configurées ; ses défauts désactivent l’OCR (`IngestionOptions.cs:75–76`). Le parcours Docling et le parcours OCR hérité sont deux configurations à qualifier séparément. Les options du test doivent reproduire le profil produit retenu plutôt que changer de parseur silencieusement en cas d’échec.

La disponibilité de RAM, espace disque, modèles embarqués et runtimes de conteneur reste à confirmer par l’orchestrateur avant installation/exécution. Aucun besoin d’API LLM payante n’est nécessaire pour cette qualification d’ingestion.

## Protocole E2E reproductible proposé

Un runner dédié reste à mettre en place ; le runner Gold actuel ne remplit pas ces étapes. Chaque exécution doit créer une nouvelle racine de preuve et de documents, une base/cluster isolés, un tenant et des ports loopback propres. Elle doit laisser la base et les profils habituels intacts.

1. **Figer le profil.** Hash des sources et DLL réellement chargées, configuration explicite et signée selon le mode utilisé, parseur, moteur OCR, modèles, langues, versions, limites et matériels. Aucun fournisseur LLM externe. Manifeste de corpus non vide, identités uniques et couverture exacte des cas attendus.
2. **Contrôler les dépendances.** Locale PostgreSQL par sentinelles comportementales ; Docling/OCR réel prêt ; modèles localisés ; répertoires détenus ; absence de port habituel ciblé. Ne pas qualifier une dépendance réelle avec une réponse de protocole simulée.
3. **Activer le vrai worker.** Watcher et scanner initialement désactivés pour un déclenchement déterministe, `Ingestion.WorkerEnabled=true`, répertoire de PDF neuf. Soumettre seulement par `/ingest/enqueue` ou `/ingestion/scan`, puis observer la queue via `/ingestion/jobs`. Aucun SQL seedé de sections/unités/chunks pour cette preuve E2E.
4. **Suivre les étapes réelles.** Enqueue, lease, hash source, extraction native/Docling/OCR, projection, embeddings et publication. Chaque étape comporte temps, issue, compteurs, provenance et identifiant de tentative. Vérifier les bornes de cancellation et les terminaux ; un `succeeded` seul ne prouve pas le contenu.
5. **Comparer les artefacts.** Source PDF immuable, pages physiques, objets canoniques, unités, chunks, contextes, tables/cellules et figures/ancres doivent correspondre aux annotations. Recalculer les checksums sur le texte réellement stocké en UTF8. Contrôler les références, le tenant, la révision, les plages physiques et les coordonnées. Interdire les succès obtenus avec les faits de la page voisine.
6. **Interroger le produit.** Catalogue, recherche canonique/exacte et lecture de contexte par API réelle, sur des questions déterministes et sans LLM. Vérifier les identités retournées et la présence des valeurs critiques attendues. La similitude OCR ne remplace pas le contrôle exact des nombres, signes, unités, négations et relations de cellules.
7. **Tester le cycle de vie.** Réingestion identique sans doublon ; nouvelle version avec valeur changée ; annulation pendant OCR et publication ; changement de fichier pendant un job ; reprise après interruption ; PDF invalide/vide/chiffré selon périmètre supporté. Aucun artefact partiel présenté comme version courante, aucune donnée d’un autre tenant, aucun job actif restant à la clôture.
8. **Qualifier les vecteurs séparément.** Une première preuve du worker peut employer des substituts TEI/Qdrant déclarés pour isoler extraction et publication. Elle doit rester marquée `vectorValidated=false`. L’acceptation complète exige ensuite le vrai modèle d’embeddings local épinglé et un vrai Qdrant isolé : dimensions, points et payloads exacts, versionnement, nettoyage et recherches réellement exécutées.
9. **Mesurer et clore.** Temps froid/chaud, p50/p95 observés, mémoire maximale et taille des artefacts ; pas de SLA inventé après le résultat. Stop des seuls processus détenus, ports libérés, zéro job actif, données et preuves privées préservées ; sélection publique expurgée avec hashes et limites explicites.

## Gates d’acceptation

- Tous les cas prévus sont exécutés ; cas manquant, skip, early-return, table/cellule requise absente ou observation mal formée entraîne un échec ou un état incomplet explicite.
- Les faits critiques du corpus sont conservés exactement avec leur page, unité, signe, négation et relation ; une erreur critique ne peut être compensée par une moyenne de qualité élevée.
- Les résultats incertains ou impossibles à extraire sont signalés et gardent la preuve source ; aucun remplacement inventé ou statut `indexed` trompeur n’est accepté.
- Les invariants SQL, d’identité et de publication passent après les scénarios de réingestion, supersession et annulation.
- La couverture de chaque moteur est déclarée séparément : natif, OCR réel, Docling réel, publication worker, vecteurs. Une réussite d’une projection de quatre pages ne remplit pas ces six cases.
- Les seuils globaux non critiques et les limites de ressources sont proposés puis figés avant l’exécution. Leur réussite ne permet pas d’écrire « tous PDF validés » ; seul le périmètre testé et supporté peut être accepté.

Priorité recommandée : fermer les faux succès des harness, figer les métriques critiques, installer/qualifier le moteur local, puis exécuter le petit corpus par le vrai worker avant de reprendre des essais LLM payants.
