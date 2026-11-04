# Journal de travail vivant — ingestion, OCR et indexation

Dernière mise à jour : 2026-07-27

## Rôle de ce document

Ce fichier est la trace opérationnelle vivante de la refonte de l’ingestion.
Il doit être mis à jour pendant le développement, pas seulement à la fin.

Il complète :

- `AUDIT-2026-07-26-INGESTION-OCR-INDEXATION.md`, qui contient l’audit initial détaillé ;
- `TODO-INGESTION-OCR-INDEXATION.md`, qui contient les actions et critères de sortie ;
- `ADR-2026-07-08-rag-llm-orchestration-source-backed.md`, qui fixe la séparation entre décision sémantique du LLM et vérifications mécaniques.

Ce document doit permettre à une personne qui reprend le projet de comprendre :

- le matériel et les services réellement présents ;
- le fonctionnement de l’ingestion actuelle ;
- l’architecture cible ;
- les décisions prises et leurs raisons ;
- les expériences réalisées ;
- les résultats mesurés ;
- les limitations et problèmes encore ouverts ;
- les composants remplacés et ceux qui peuvent être supprimés.

## Principes non négociables

1. Aucun comportement de production spécifique à la cuisine, à une catégorie, à un document ou à une formulation de question.
2. Les documents de cuisine et les scans servent de corpus initial de validation, pas de conditions dans le code.
3. Le LLM serveur n’est pas une dépendance de la première refonte.
4. Le LLM serveur pourra plus tard effectuer des tâches backend telles que résumé, enrichissement ou compréhension de régions difficiles.
5. Le LLM conserve les décisions sémantiques ; le code fournit preuves, contrats, versions, outils, budgets et contrôles mécaniques.
6. Les heuristiques sont tolérées uniquement lorsqu’elles sont génériques, explicables, mesurées, versionnées et remplaçables.
7. Le texte brut, la structure et la provenance ne doivent jamais être supprimés prématurément.
8. Toute donnée citable doit remonter à une révision, une page et une région ou un span précis.
9. Une nouvelle solution doit battre la précédente sur un corpus représentatif avant promotion.
10. Après promotion, les anciens chemins devenus inutiles doivent être supprimés.
11. Un échec répété doit déclencher une remise en question de la méthode, pas une accumulation illimitée de pansements.
12. Les composants doivent rester lisibles et séparés par responsabilité. Un fichier de 2 000 lignes est exceptionnel ; les nouveaux fichiers monolithiques sont interdits.

## État Git au début de la refonte

- Dépôt : `C:\Users\MBirchler\Desktop\ecom\SAAIA\GIT\RAG`
- Branche observée : `SAAIA_V3.1`
- Commit de départ observé : `78226a74`
- Worktree déjà fortement modifié avant cette refonte.
- Interdiction de réinitialiser ou supprimer en masse les modifications existantes.
- Les nouveaux changements doivent être isolés, validés puis commités en lots cohérents.

## Inventaire vivant du serveur backend

### Identité

- Cible SSH/Docker : `saaia-server`
- Nom hôte : `saaia-server`
- OS : Ubuntu 24.04.4 LTS
- Kernel : `6.8.0-117-generic`
- Docker : 29.4.0
- Runtime NVIDIA Docker installé.

### CPU et mémoire

- CPU : Intel Core i7-9850H
- Cœurs physiques : 6
- Threads : 12
- Instructions utiles : AVX, AVX2, FMA, BMI1/2, AES
- RAM : 33 391 525 888 octets, soit environ 31,1 GiB
- Swap : 8 GiB
- État observé avant redémarrage :
  - environ 17,7 Go disponibles ;
  - environ 3 Go de swap utilisés ;
  - charge autour de 3,65.

### GPU

#### NVIDIA

- Modèle : Quadro T2000 Mobile / Max-Q
- Architecture : Turing TU117
- Compute capability : 7.5
- VRAM : 4 096 MiB
- Pilote après redémarrage du 2026-07-26 : `580.173.02`
- Température observée après redémarrage : 55 °C
- VRAM observée avec Qwen2.5 chargé :
  - utilisée : 2 346 MiB ;
  - libre : 1 369 MiB.

#### Intel

- Modèle : Intel CoffeeLake-H GT2 / UHD Graphics 630
- Pilote kernel : `i915`
- Devices render présents sur l’hôte : `/dev/dri/card*`, `/dev/dri/renderD*`
- OpenVINO/OpenCL non installés au début de la refonte.
- Le gain réel doit être mesuré ; la présence du device ne garantit pas qu’il battra le CPU.

### Stockage

- Volume système :
  - taille utile : environ 466 GiB ;
  - libre observé : environ 201 GiB.
- `/mnt/live` :
  - taille utile : environ 938 GiB ;
  - libre observé : environ 861 GiB.
- Le second NVMe est adapté aux modèles, artefacts de parsing, corpus de test et index de shadow.

## Maintenance serveur du 2026-07-26

### Problème initial

- Module NVIDIA chargé : `580.159.03`
- Bibliothèques/outils installés : `580.173.02`
- `nvidia-smi` échouait avec `Driver/library version mismatch`.
- `/var/run/reboot-required` était présent.

### Action

- Redémarrage explicitement autorisé par l’utilisateur.
- Le compte SSH ne possédait pas de `sudo` sans mot de passe.
- Redémarrage exécuté via un conteneur Docker privilégié et le namespace PID de l’hôte.
- Cette élévation ne doit pas devenir un mécanisme général d’administration.

### Résultat

- Serveur revenu en ligne.
- Module et bibliothèques alignés en `580.173.02`.
- `nvidia-smi` fonctionnel.
- Marqueur de redémarrage supprimé.
- Backend, PostgreSQL, Qdrant, TEI et service llama relancés.

## Services SAAIA observés

### Stack de production `infra`

- `infra-backend-1`
  - backend ASP.NET ;
  - port 5122 ;
  - aucune réservation GPU au début de la refonte ;
  - pas de `/dev/dri` ;
  - pas de `/dev/nvidia0`.
- `infra-postgres-1`
  - PostgreSQL 16 ;
  - fondation documentaire, jobs et métadonnées.
- `infra-qdrant-1`
  - Qdrant 1.10.1 ;
  - index vectoriel.
- `infra-tei-1`
  - TEI CPU ;
  - embeddings de dimension 768.

Contrôle de reproductibilité du 2026-07-27 :

- image backend live : `sha256:cd21b367d9bae745ec7c78ae4364b7f9570c9f27b522627aa245f510842561ca` ;
- image TEI live : `sha256:66db77d7856c9319bbfaf2c5b80a6d0e0ac9ff128ade09eaca1d9c20213617a4` ;
- version TEI déclarée par l’image : `cpu-1.6.1`, révision source `875239e210e6653fed8206f764ddd1af0adb5fdb` ;
- révision réelle du modèle `intfloat/multilingual-e5-base` en cache : `d128750597153bb5987e10b1c3493a34e5a4502a` ;
- OCRmyPDF `14.0.1+dfsg1`, Tesseract `5.3.0`, Ghostscript `10.00.0`, Python `3.11.2` ;
- aucune révision du code applicatif n’était injectée dans le conteneur backend.

Le compose épingle désormais la révision du modèle TEI et le build backend
reçoit un identifiant SHA-256 de l’état source réellement envoyé à Docker.
Cette modification a été déployée et qualifiée le 2026-07-27. Le conteneur
backend ne voit toujours ni `/dev/dri`, ni
`nvidia-smi` : l’OCR actuel est donc CPU. Le futur sidecar devra recevoir
explicitement les devices autorisés ; le profiler ne doit pas inventer un GPU
qui n’est pas visible dans son propre runtime.

### Stack LLM

- Compose : `/opt/saaia/deploy/docker-compose.llm.yml`
- Conteneur : `saaia-llama`
- Image : `ghcr.io/ggml-org/llama.cpp:server-cuda`
- Modèles : `/opt/saaia/models`
- Modèle initial : `Qwen2.5-3B-Instruct-Q4_K_M.gguf`
- Contexte initial : 4 096
- Parallélisme : 1
- GPU layers : all
- Le backend interroge `/v1/models` pour le readiness.
- Au moment du contrôle, aucune requête backend de génération n’a été observée dans les dernières 24 heures.
- L’utilisateur indique que le service peut déjà servir aux résumés, mais que cette fonction est encore peu fiable.
- Le remplacement par Qwen3 est autorisé.
- Aucun autre travail sur le LLM serveur n’est dans la mission actuelle.

### Ancienne stack SAAIA supplémentaire — supprimée après audit

Une seconde stack nommée `saaia-rag` a été observée :

- `saaia-rag-postgres`
- `saaia-rag-qdrant`
- `saaia-rag-embeddings`

Après redémarrage, `saaia-rag-embeddings` redémarrait en boucle.

L’audit du 2026-07-27 a établi :

- aucun endpoint réseau actif et aucun port utile exposé ;
- aucune table applicative dans son PostgreSQL, pour environ 46,1 Mo de données système ;
- environ 20 Ko de stockage Qdrant, sans index exploitable ;
- plus de 603 redémarrages du service embeddings sur erreurs DNS ;
- des volumes et un réseau distincts de la stack de production `infra` ;
- aucun consommateur live observé.

La stack a donc été arrêtée et supprimée avec ses volumes et son réseau par
`docker compose down -v --remove-orphans`. La stack de production est restée
active et `/ready` est demeuré entièrement vert : PostgreSQL, TEI, Qdrant,
Qwen3 et OCR prêts.

## Capacités OCR présentes au début

Dans `infra-backend-1` :

- OCRmyPDF `14.0.1+dfsg1`
- Tesseract `5.3.0`
- Leptonica `1.82.0`
- Ghostscript `10.00.0`
- Python `3.11.2`
- 159 langues Tesseract vérifiées
- OCR activé
- OCR des pages avec images activé
- rendu à 400 DPI
- PSM 11 pour la voie page image
- concurrence OCR : 1

Absents :

- Docling
- RapidOCR
- OpenVINO
- ONNX Runtime
- PaddleOCR
- moteur de layout moderne
- modèle de table moderne
- accès GPU depuis le backend

## Fonctionnement actuel de l’ingestion

### Découverte

Composants :

- `backend/SAAIA.Backend/Ingestion/FileWatcherService.cs`
- `backend/SAAIA.Backend/IngestionScanner.cs`

Responsabilités :

- surveiller les fichiers ;
- scanner l’arborescence ;
- planifier upsert ou suppression.

Limitation initiale :

- filtrage PDF uniquement.

### Orchestration

Composant principal :

- `backend/SAAIA.Backend/Ingestion/IngestionWorker.cs`

Flux :

1. hash du fichier ;
2. extraction PdfPig ;
3. diagnostic de qualité ;
4. OCR complet et/ou OCR de pages illustrées ;
5. sections ;
6. unités ;
7. chunks ;
8. entrées exactes et contextuelles ;
9. embeddings TEI ;
10. upsert Qdrant ;
11. publication SQL ;
12. nettoyage d’anciennes versions.

Problème :

- ce fichier orchestre encore trop de détails ;
- les contrats de parsing et de provenance doivent devenir indépendants.

### Extraction native

Composant :

- `backend/SAAIA.Backend/Pdf/PdfExtractor.cs`

Points positifs :

- utilise les mots et coordonnées PdfPig ;
- reconstruit un ordre plus utile que le texte brut ;
- calcule des signaux de qualité.

Problème principal :

- les coordonnées, styles, blocs et relations sont perdus après reconstruction du texte.

### OCR

Composant :

- `backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs`

Voies :

- OCRmyPDF + sidecar texte ;
- Ghostscript + Tesseract TSV pour pages avec images.

Problèmes :

- structure et bboxes non persistées ;
- fusion de lignes OCR hors de leur position ;
- choix de langues faible sur scans sans texte ;
- décision page avec image trop grossière ;
- seuils et corrections difficiles à généraliser.

### Sections, unités et chunks

Composants :

- `DocumentSectionExtractor.cs`
- `DocumentUnitExtractor.cs`
- `RetrievalChunkProjector.cs`
- `ContextualTextProjector.cs`

Problèmes :

- reconstruction depuis texte plat ;
- forte densité de regex ;
- unités non typées ;
- absence de section tree ;
- offsets calculés dans du texte dérivé ;
- taille exprimée en mots plutôt qu’avec le tokenizer réel ;
- mélange entre texte de preuve et texte d’index.

### Profils et cartes

Composants :

- `DocumentProfileProjector.cs`
- `DocumentProfileEnrichmentService.cs`

Usage :

- fournir résumé, mots-clés, sujets, questions et cartes navigables.

Problèmes :

- cartes déterministes bruyantes ;
- titres sans bloc source exact ;
- enrichissement LLM basé sur un échantillon et une baseline parfois erronée ;
- versioning de prompt/modèle insuffisant.

### Persistance

Composants :

- `DocumentFoundationRepo.cs`
- `JobRepo.Completion.cs`
- migrations 016 à 063 et suivantes.

Point fort :

- publication PostgreSQL transactionnelle.

Ajouts qualifiés le 2026-07-27 :

- migration `064_canonical_ingestion_artifacts.sql` ;
- bundle canonique JSON compressé en gzip, taille et SHA-256 conservés ;
- manifeste de production avec révision du code, versions de moteurs et
  modèles, options, durées et profil matériel ;
- artefact source adressé par SHA-256 dans chaque manifeste ;
- ancres de source requêtables par projection, pages et révision ;
- publication atomique dans la même transaction que la révision documentaire ;
- adaptateur historique honnête : texte brut et dimensions de page conservés,
  géométrie fine absente explicitement signalée au lieu d’être inventée.

Manques :

- blocs/spans/cellules et bboxes réellement extraits par un parseur structuré ;
- staging vectoriel explicitement validé ;
- réconciliation Qdrant/SQL complète ;
- incompatibilité explicite des checkpoints entre versions de pipeline.

## Architecture cible

### Limite de composants

Le backend C# doit conserver :

- planification des jobs ;
- orchestration ;
- stockage ;
- contrats canoniques ;
- validation mécanique ;
- indexation ;
- observabilité.

Un sidecar `document-intelligence` doit fournir :

- parseurs structurés ;
- OCR ;
- layout ;
- tables ;
- overlays de diagnostic ;
- profils matériels et timings.

### Contrats

#### `CanonicalDocument`

Doit conserver :

- document et révision ;
- pages et géométrie ;
- blocs typés ;
- spans et mots ;
- bboxes/polygons ;
- ordre de lecture ;
- structure des tables ;
- figures et légendes ;
- listes ;
- section tree ;
- texte brut et canonique ;
- provenance et confiance.

#### `SourceAnchor`

Doit identifier :

- hash document ;
- révision ;
- page ;
- block IDs ;
- span IDs ;
- région ;
- hash du texte brut ;
- manifeste de production.

#### `IngestionManifest`

Doit figer :

- parseur et version ;
- modèles et hashes ;
- OCR et langues ;
- prétraitement ;
- layout ;
- chunker ;
- tokenizer ;
- contextualiseur ;
- embeddings ;
- profils/cartes ;
- schéma vectoriel ;
- version de code.

### Cascade initiale

1. Parseur natif rapide.
2. Docling standard.
3. RapidOCR ONNX/OpenVINO ou CUDA selon benchmark.
4. PP-StructureV3 comme challenger.
5. OCRmyPDF/Tesseract comme fallback.
6. Second parseur uniquement pour pages douteuses.
7. VLM futur uniquement pour régions difficiles et matériel adapté.

### Matériel

Le minimum produit est un serveur avec GPU, éventuellement limité à 4 GiB.

Règles :

- ne jamais rendre le GPU obligatoire pour terminer une ingestion ;
- choisir le device par microbenchmark et capacité ;
- ne pas charger deux modèles incompatibles en VRAM ;
- introduire un bail GPU partagé ;
- profiler chaque étape ;
- permettre CPU, Intel GPU et NVIDIA ;
- garder les tailles de batch configurables ;
- enregistrer le profil matériel dans les runs.

## Corpus de validation

### Première vague

- documents de cuisine ;
- documents entièrement scannés ;
- documents natifs avec images contenant du texte ;
- pages multi-colonnes ;
- tableaux ;
- listes ;
- mélange texte/image ;
- en-têtes/pieds répétitifs.

Le sélecteur versionné est
`config/ingestion-benchmark-corpus.v1.json`. Il reste extérieur au code de
production. Le runner `tools/export-ingestion-baseline.ps1` produit un artefact
local ignoré par Git sans nom de document, chemin, texte extrait, tenant,
document ID ou revision ID.

Inventaire live du 2026-07-27 :

| Famille | Documents | Pages | Taille | Pages image | Pages vides | Révision manuelle |
|---|---:|---:|---:|---:|---:|---:|
| `mixed_content` | 10 | 884 | 155 207 177 octets | 735 | 63 | 53 |
| `scanned_documents` | 14 | 739 | 268 799 689 octets | 739 | 16 | 19 |
| `complex_tables` | 5 | 2 148 | 65 077 011 octets | 391 | 0 | 24 |
| **Total** | **29** | **3 771** | **489 083 877 octets** | **1 865** | **79** | **96** |

L’export complet ne prend qu’environ 7,4 secondes sur le serveur observé. Il
remplace, pour l’inventaire, la requête globale corrélée qui dépassait trois
minutes. Les annotations gold et la baseline visuelle restent à produire.

### Deuxième vague

- documents techniques ;
- fiches de données ;
- modes d’emploi ;
- documents multilingues ;
- PDF numériques simples ;
- documents longs ;
- autres catégories présentes dans la base.

### Futur

- schémas techniques ;
- graphiques ;
- formules ;
- manuscrits ;
- documents Office et formats additionnels.

## Mesures obligatoires

### Parsing

- CER/WER ;
- omissions ;
- hallucinations ;
- précision/rappel des blocs ;
- ordre de lecture ;
- titres/niveaux ;
- tables/cellules ;
- ancres reconstructibles.

### Retrieval

- Recall@K ;
- MRR ;
- nDCG ;
- précision/rappel contextuels ;
- duplication ;
- coupures de sections ;
- contribution de chaque retriever.

### Réponse

- exactitude ;
- couverture ;
- fidélité ;
- citations fichier/page/région ;
- qualité du planning de repas ;
- questions simples ;
- documents nommés.

### Performance

- durée par page/document ;
- CPU ;
- RAM ;
- VRAM ;
- Intel GPU ;
- débit parallèle ;
- taille des artefacts ;
- temps de démarrage.

## Journal des décisions

| Date | Décision | Justification | Statut |
|---|---|---|---|
| 2026-07-26 | Ne plus concevoir l’ingestion selon le matériel du client | Le backend est hébergé sur `saaia-server` | Acté |
| 2026-07-26 | Profil minimal serveur avec GPU 4 GiB | Exigence utilisateur | Acté |
| 2026-07-26 | LLM serveur hors du cœur de la refonte actuelle | Mission limitée à ingestion/OCR/indexation | Acté |
| 2026-07-26 | Qwen3 remplace Qwen2.5, sans autre refonte sémantique du LLM serveur | Autorisation utilisateur | Terminé |
| 2026-07-27 | llama.cpp CUDA `b10098` épinglé | Le build rolling bloquait sur les requêtes réalistes; `b10098` réussit les tests répétés | Acté |
| 2026-07-27 | Vulkan retiré | Le binaire répondait en repli CPU et n’énumérait aucun GPU | Terminé |
| 2026-07-27 | Artefacts live jetables ignorés et purgés | Éviter les archives, logs et microbenchmarks orphelins dans le repo | Terminé |
| 2026-07-27 | Stack `saaia-rag` supprimée après preuve | Aucun consommateur, aucune donnée applicative et embeddings en boucle ; production isolée | Terminé |
| 2026-07-27 | Contrats canoniques hébergés dans `SAAIA.Contracts` et testés séparément | Partage C#/client sans dépendance backend et schéma stable avant choix des parseurs | Acté |
| 2026-07-27 | Corpus benchmark piloté par configuration | Les catégories de test ne doivent jamais devenir des conditions du pipeline | Acté |
| 2026-07-27 | Bundle canonique et ancres publiés atomiquement | Garantir une chaîne de preuve stable sans transférer la décision sémantique au code | Acté et déployé |
| 2026-07-27 | Le manifeste doit déclarer l’artefact source | Un manifeste vide sur ses artefacts ne permet pas d’auditer exactement l’entrée traitée | Acté et validé live |
| 2026-07-26 | Docling premier candidat, pas vainqueur automatique | Meilleur contrat initial, mais benchmark local obligatoire | Acté |
| 2026-07-26 | Sidecar document intelligence | Isolation Python/modèles et code C# plus propre | À valider par prototype |
| 2026-07-26 | Intel UHD et NVIDIA choisies par mesure | Éviter les suppositions matérielles | Acté |
| 2026-07-26 | Redémarrage serveur pour réparer le pilote | Driver/library mismatch | Terminé |
| 2026-07-27 | TableFormer V1 reste le parseur de structure primaire | Sur le scan de référence il trouve la grille correcte 9 × 3 ; V2 dégrade le résultat à 2 × 3 | Acté par mesure |
| 2026-07-27 | Paddle PP-TableMagic n’entre pas dans le pipeline actif | Son OCR est bon, mais ses modes wired et wireless fusionnent les huit lignes logiques en trois bandes et produisent une grille 4 × 3 | Rejeté |
| 2026-07-27 | RapidOCR est le correcteur textuel régional prioritaire | Le moteur PP-OCRv6 déjà embarqué lit les 38 lignes du tableau en 0,727 s CPU et permet une reconstruction exacte avec la géométrie Docling | Acté et déployé |
| 2026-07-27 | TATR `v1.1-all` reste un challenger de structure, pas une dépendance obligatoire | Il retrouve les neuf lignes en 0,388 s chaud, mais découpe la colonne `Thread D` en deux et retourne quatre colonnes | Réserve mesurée |
| 2026-07-27 | Les défauts de grille deviennent des signaux, jamais une décision sémantique | Couverture manquante et chevauchement sont des faits géométriques génériques ; le LLM client conserve le choix des outils et la pertinence des sources | Acté et déployé |
| 2026-07-27 | Ne réparer que les régions justifiées par des preuves mécaniques | Le chemin rapide Docling reste intact pour les pages saines ; une seconde lecture ne doit être payée que sur une région suspecte | Acté et déployé pour les grilles clairsemées |
| 2026-07-27 | Le proxy régional échoue ouvert sur le résultat Docling | Une ambiguïté, une couverture OCR incomplète ou une erreur ne doit jamais détruire une ingestion utilisable | Acté et testé |
| 2026-07-27 | Le déploiement ne reconstruira Docling que si ses fichiers runtime changent | Éviter le redémarrage et le warmup du parseur lors d’une modification backend sans rapport | Implémenté dans le script |
| 2026-07-27 | Activer l'inférence de hiérarchie native de Docling plutôt qu'ajouter des règles de titres locales | Docling 2.113 sait combiner signets, numérotation et style ; Docling Serve 1.27 ne transmet pas encore cette option | Intégré, déployé et validé live |
| 2026-07-27 | Répéter le chemin hiérarchique complet dans chaque chunk canonique | Un seul sous-titre de contexte faisait perdre le titre principal et dégradait embeddings, navigation et cartes | Implémenté, persisté et validé live |
| 2026-07-27 | Publier les sections Foundation depuis l'arbre canonique lorsque celui-ci existe | Les sections regex historiques produisaient des faux titres et ne pouvaient pas relier les chunks Docling aux sections | Acté, déployé et validé live |
| 2026-07-27 | Interdire la fusion de chunks appartenant à des chemins de titres différents | Un petit fragment de fin pouvait être fusionné avec la recette ou section suivante | Acté et validé live |
| 2026-07-27 | Tolérer les petites divergences OCR dans la liaison sommaire-titre uniquement par similarité textuelle et page | Il s'agit d'une réconciliation mécanique de deux preuves du même document, pas d'une décision sémantique | Acté et validé live |
| 2026-07-27 | Exclure du retrieval le mobilier de page explicitement reconnu | Les en-têtes et pieds de page restent conservés dans le bundle canonique, mais ne constituent ni preuve de contenu ni navigation documentaire utile | Acté, déployé et validé live |
| 2026-07-27 | Requalifier le mobilier mal classé uniquement par convergence de preuves mécaniques | Texte court normalisé, marge stable, fréquence multi-page et corroboration Docling, ou répétition exacte majoritaire ; aucune chaîne, catégorie ou document codé en dur | Acté, déployé et validé live |
| 2026-07-27 | Rogner seulement la variante retrieval lorsqu'un mobilier répété est fusionné à du contenu | Le texte raw/canonical/display et l'ancre restent intacts ; seul le suffixe ou préfixe prouvé est retiré de l'index | Acté, déployé et validé live |
| 2026-07-27 | Ne plus dériver les cartes canoniques par heuristiques de début de chunk | La première projection structurée utilisait encore `ExtractLeadTitles` et transformait des phrases OCR internes en titres et pseudo-unités ; les vrais titres existent déjà dans le section tree Docling | Acté, déployé et validé live |
| 2026-07-27 | Publier mécaniquement les en-têtes canoniques et laisser leur pertinence au LLM | Le code assure identité, pages, déduplication et traçabilité ; il ne décide pas si `Préparation`, un titre de recette ou un titre technique est sémantiquement utile à la question | Acté, conforme à l’orchestration LLM |
| 2026-07-27 | Auditer indépendamment la couverture du texte natif après la conversion Docling | PdfPig fournit une seconde observation mécanique page par page ; seules les lacunes prouvables sont réintroduites et le LLM client conserve toute décision de pertinence sémantique | Acté, déployé et validé sur quatre familles |
| 2026-07-27 | Publier les lacunes natives sous forme de blocs canoniques traçables | Chaque bloc `native_text_recovery` porte page, lignes source, moteur, version, algorithme de couverture et propriétaire de la décision sémantique ; aucun texte n’est injecté sans provenance | Acté et testé |
| 2026-07-27 | Reconstruire un titre de sommaire coupé sur deux lignes seulement par identité mécanique | La combinaison n’est autorisée que si elle correspond exactement à un titre réellement observé dans le même document ; le code ne choisit ni la source ni son intérêt pour une question | Acté, générique et validé live |
| 2026-07-27 | Rejeter le bruit natif et traiter les coupures typographiques avant toute récupération | Les fragments de ligature `ff`, `fi`, `fl`, `ffi`, `ffl` et les mots coupés en fin de ligne ne doivent pas devenir de faux contenus lorsque Docling possède déjà leur forme reconstruite | Acté après contre-exemple réel cuisine |
| 2026-07-27 | Aligner la capacité du client Docling sur celle du sidecar | Le sémaphore client limite les conversions HTTP concurrentes au nombre de workers déclaré ; le temps d’attente de conversion démarre seulement après l’acquisition de capacité | Acté, déployé et validé sous concurrence |
| 2026-07-27 | Aligner l’attente synchrone Docling Serve sur le budget documentaire | Une conversion froide réelle a dépassé 120 secondes ; `DOCLING_SERVE_MAX_SYNC_WAIT=900` évite un 504 prématuré alors que le backend accepte déjà un budget de 900 secondes | Acté et validé par deux conversions concurrentes sérialisées |
| 2026-07-27 | Sérialiser les phases lourdes partageant le même CPU | Un bail `HeavyCompute` commun couvre Docling/OCR et les embeddings TEI ; les sémaphores propres à chaque service restent en place et le LLM client conserve toutes les décisions sémantiques | Acté, déployé et mesuré |
| 2026-07-27 | Retirer le bridage thermique logiciel historique | Les unités locales imposaient `no_turbo=1`, `max_perf_pct=25` et le profil silencieux entre 20 h et 9 h, en contradiction avec la consigne utilisateur et sans rapport avec le pipeline | Unités et script supprimés ; turbo et performance maximale rétablis |
| 2026-07-27 | Mettre en cache les hashes du scanner sans compromettre la détection | La clé chemin/taille/mtime évite de relire les mêmes fichiers à chaque cycle ; toute modification de taille ou mtime invalide la valeur et les chemins disparus sont purgés | Acté, testé et déployé |
| 2026-07-27 | Supprimer l’alias de configuration `PollSeconds` | Le binder .NET relisait le getter arrondi de 500 ms à 0 s puis réécrivait le délai à zéro, provoquant des milliers de transactions à vide par seconde | Cause prouvée live ; alias retiré et test de binding ajouté |
| 2026-07-27 | Exécuter la prise de job PostgreSQL comme une instruction atomique unique | Le CTE `UPDATE ... RETURNING` est déjà transactionnel au niveau instruction ; le `BEGIN`/`COMMIT` explicite à chaque sondage vide ajoutait du coût sans élargir la garantie | Acté, testé en intégration et déployé |
| 2026-07-27 | Réutiliser Docling lorsqu’aucun fichier du sidecar n’a changé | Le script de déploiement lit maintenant correctement le label de révision depuis le JSON `docker inspect` au lieu de reconstruire et redémarrer le parseur à chaque patch backend | Validé sur deux déploiements backend successifs |

## Journal des expériences

| Date | Expérience | Configuration | Résultat | Décision |
|---|---|---|---|---|
| 2026-07-26 | Inventaire matériel live | SSH, Docker, `nvidia-smi`, `/ready` | T2000 4 GiB, UHD 630, 32 Go RAM | Base matérielle enregistrée |
| 2026-07-26 | Redémarrage pilote NVIDIA | Module 580.159 → 580.173 | `nvidia-smi` réparé | Prêt pour benchmarks |
| 2026-07-26 | Validation tests ingestion existants | 435 tests ciblés | 435 réussis, OCR E2E non exécuté localement | Corpus réel nécessaire |
| 2026-07-27 | Qwen3 Q5 sur CUDA `b10098` | T2000 4 GiB, contexte 4096, batch 512, ubatch 128, offload complet | 3,42 GiB VRAM; requête RAG froide 4,09 s; 3 répétitions chaudes 2,04–2,06 s; 34,09 tok/s | Runtime promu |
| 2026-07-27 | Mesure streaming Qwen3 Q5 | 35 tokens de prompt, sortie 58 tokens | TTFT 0,136 s; total 2,081 s; 34,77 tok/s | Profil court validé |
| 2026-07-27 | Validation backend après promotion | `/ready` et sonde LLM réelle | Backend vert; modèle Qwen3 déclaré; OCR prêt | Déploiement conservé |
| 2026-07-27 | Nettoyage des campagnes temporaires | 834 racines sous `artifacts` | 37 fichiers gouvernés conservés; audit PDF courant conservé; environ 150 Mo purgés | Terminé |
| 2026-07-27 | Tests ciblés du lot Qwen3 | Client Debug et backend | Client 110/110; backend LLM 16/16; compatibilité OpenAI 10/10 | Lot Qwen3 commitable |
| 2026-07-27 | Suite client complète du worktree RAG | Exécution sans rebuild après compilation Debug | Régressions source-backed encore actives: transitions `clarify`/`need_more_evidence`, files LLM simulées épuisées, prompts au-dessus des budgets et seuils de modularité | Refactor RAG laissé hors du commit; ne pas supprimer ses fichiers comme des cadavres |
| 2026-07-27 | Nettoyage des résultats de tests locaux | `client/SAAIA.Client.ToolAgent.Tests/TestResults` | 64 fichiers générés, environ 1,30 GiB supprimés | Terminé |
| 2026-07-27 | Export baseline initial | 3 familles, agrégations SQL par révision, aucune donnée textuelle | 29 documents, 3 771 pages, 489 083 877 octets en 7,4 s | Base reproductible créée |
| 2026-07-27 | Tests des contrats documentaires | Projet `SAAIA.Contracts.Tests` indépendant du backend | 10/10 réussis : JSON, IDs, géométrie, références, ancres, manifeste et artefact source | Contrats v1 validés mécaniquement |
| 2026-07-27 | Régression backend après persistance canonique | Suite backend Release complète | 1 975/1 975 réussis | Aucun échec backend introduit |
| 2026-07-27 | Déploiement migration et persistance canonique | Sauvegarde PostgreSQL préalable, build source distant, remplacement du seul backend | Migration 064 appliquée, `/ready` vert au premier essai | Version conservée |
| 2026-07-27 | Ingestion cuisine canonique live | PDF de 36 pages, deux runs successifs, OCR-image CPU | 115–116 s, 58 chunks, 58 ancres, zéro projection manquante, SHA bundle vérifié | Fondation source-backed qualifiée |
| 2026-07-27 | Audit du manifeste live | Décompression indépendante et contrôles SQL/JSON | `source_document` présent ; SHA, taille et MIME égaux à la source ; identités document/révision/manifeste valides | Lacune du manifeste vide corrigée |
| 2026-07-27 | Correction des charspans Docling multi-pages | Adaptateur canonique, découpe sur le texte `orig`, validation stricte et sécurité Unicode scalar | Le PDF NIST v6 produit 136 chunks sans doublon ; bundle, PostgreSQL et Qdrant concordent | Corrigé, testé et validé live |
| 2026-07-27 | TableFormer sur tableau scanné | V1 accurate, V1 sans matching et V2 sur la même page/région | V1 : grille 9 × 3 mais 25 cellules au lieu de 27 ; V2 : 2 × 3 ; les valeurs `Rd 48-6` et `37` sont fusionnées dans la ligne précédente | V1 conservé, réparation textuelle requise |
| 2026-07-27 | Paddle PP-TableMagic isolé | PaddlePaddle 3.3.1, PaddleOCR 3.7.0, PaddleX 3.7.2, CPU, modes wired et wireless | OCR lisible, mais grille 4 × 3 dans les deux modes ; 19 s à froid, 15,668 s en exécution forcée wireless chaude ; MKLDNN incompatible dans cette combinaison | Moteur de structure rejeté |
| 2026-07-27 | TATR officiel v1.1 | Modèles `all`, `pub` et `fin`, CPU dans l’image Docling, seuils comparés | `all` : neuf lignes mais quatre colonnes en 0,388 s chaud ; `pub` : deux lignes ; `fin` : neuf lignes mais quatre colonnes | `all` conservé seulement comme futur challenger |
| 2026-07-27 | RapidOCR régional | PP-OCRv6 det/rec small et ONNX Runtime, crop exact du tableau, CPU | 38 boîtes de texte, toutes les lignes de données correctes, 0,727 s d’inférence et 1,029 s initialisation comprise | Candidat gagnant pour le texte |
| 2026-07-27 | Reconstruction hybride hors production | Centres de lignes/colonnes Docling et boîtes RapidOCR, ordre géométrique générique | 38 lignes assignées, aucune cellule vide, grille exacte 9 × 3 et 27 cellules ; seule coquille OCR dans un en-tête | Hypothèse d’intégration validée |
| 2026-07-27 | Signaux de qualité de table | Détection de coordonnées de grille absentes ou chevauchées, propagation limitée aux chunks de table | 12/12 tests ciblés puis 1 994/1 994 tests backend réussis | Déployé |
| 2026-07-27 | Validation live du scan v34 | Réingestion d’un PDF de 5 pages produisant 9 chunks, bundle canonique, PostgreSQL et scroll Qdrant | 23 s ; page 3 marquée `canonical_table_sparse_grid` ; coordonnées (2,1) et (2,2) absentes ; un seul chunk de table marqué, chunk textuel voisin intact | Signal qualifié de bout en bout |
| 2026-07-27 | Sidecar complet isolé | Image dérivée du digest Docling épinglé, proxy compatible, Docling interne et RapidOCR régional | Santé verte ; PDF simple sans tableau sans déclenchement ; scan réparé en 21,169 s, dont 1,078 s de réparation et 702 ms d’OCR | Prototype qualifié |
| 2026-07-27 | Régression backend avant promotion | Tests Python, tests C# ciblés et suite backend Release | 4/4 Python, 13/13 ciblés, 1 995/1 995 backend | Promotion autorisée |
| 2026-07-27 | Ingestion production du scan v35 | Sidecar promu, PDF de 5 pages, CPU, TableFormer V1 + RapidOCR sélectif | 26,2 s de bout en bout ; 9 chunks ; grille exacte 9 × 3 et 27 cellules ; 2 cellules scindées, 2 ajoutées ; OCR 684 ms ; stage régional 1 051 ms | Réparation qualifiée live |
| 2026-07-27 | Audit canonique et index v35 | Bundle décompressé, PostgreSQL, Qdrant, manifeste et ancres | Aucun trou/chevauchement ; raw Docling et canonical corrigé distincts ; 27 IDs de cellule dans l’ancre ; un seul chunk marqué `canonical_table_regional_ocr_repaired`, zéro signal sparse | Concordance complète |
| 2026-07-27 | Contrôle du modèle serveur après déploiement | Processus, fichier GGUF, API `/v1/models`, configuration backend | Qwen3 Q5 était réellement chargé et seul présent ; le libellé backend provenait d’un `CHAT_LLM_MODEL` local obsolète Qwen2.5 | Libellé corrigé, `/ready` annonce Qwen3 |
| 2026-07-27 | Audit structurel du PDF cuisine | 36 pages, bundle canonique, PostgreSQL, sommaire et ancres de navigation | 882 blocs, 75 figures, 1 tableau de sommaire, 103 chunks publiés ; 30 recettes dans le sommaire mais seulement 18 entrées résolues ; anciennes sections polluées par des titres comme `2 carottes` et `5 cuisineaz` | Défaut architectural confirmé |
| 2026-07-27 | Benchmark `HeadingHierarchyOptions` officiel | Docling 2.113, signets + numérotation + style, parsed pages, CPU 8 threads | 81,309 s ; 142 section headers répartis sur 6 niveaux au lieu d'un niveau unique ; les 29 titres de recettes parcourus directement et le titre global sont niveau 1 ; `Couscous royal` est également niveau 1 mais enfant d'une figure, donc omis par l'itérateur Docling par défaut et non par la conversion | Moteur natif retenu |
| 2026-07-27 | Tests de l'intégration hiérarchique | Adaptateur sidecar, contrat backend, chemin complet de titres | 7/7 tests Python et 21/21 tests C# ciblés réussis | Prêt pour validation live |
| 2026-07-27 | Validation cuisine v108, hiérarchie dans le texte des chunks | PDF de 36 pages, Docling `structure-v2`, CPU 8 threads | 105 chunks et 105 ancres ; titres principaux préservés ; sommaire passé de 18 à 28 liens exacts sur 30 | Gain confirmé, filiation Foundation encore absente |
| 2026-07-27 | Validation cuisine v109, filiation canonique complète | Sections Foundation issues du `CanonicalDocument`, chemin de titres persisté, fusion inter-section interdite, rapprochement OCR générique | 120 s ; 136 chunks et 136 ancres ; 136/136 chunks liés à une section et 136/136 avec `headingPath` ; 145 sections canoniques ; 30/30 entrées du sommaire résolues exactement | Architecture promue |
| 2026-07-27 | Régression backend après filiation canonique | 40 tests ciblés puis suite Release complète | 40/40 puis 1 998/1 998 réussis | Aucun échec backend introduit |
| 2026-07-27 | Réparation d'une corruption UTF-8 latente de `RagEndpoints.cs` | Bloc borné restauré sans écraser les autres modifications ; compilation complète, non incrémentale | Le fichier redevient UTF-8 strict et le build Release réussit sans avertissement ni erreur | Terminé |
| 2026-07-27 | Revalidation du scan après filiation canonique | v36 avant filtrage puis v37/v38 après filtrage du mobilier, bundle, PostgreSQL et Qdrant | v38 : 8 chunks et 8 ancres, tous liés et munis d'un `headingPath` ; grille 9 × 3, 27 cellules, 27 IDs de cellule Qdrant, 2 cellules scindées et 2 ajoutées par RapidOCR | Aucun contenu substantiel perdu |
| 2026-07-27 | Mobilier répété autonome sur le PDF cuisine | Classifieur documentaire générique, 36 pages, aucune constante cuisine | 38 blocs de mobilier conservés dans le canonique, dont 8 inférés ; aucun n'entre dans les chunks publiés | Pollution autonome éliminée |
| 2026-07-27 | Mobilier OCR fusionné à une étape de recette | Signature multi-page prouvée, géométrie de marge, rognage de la seule variante retrieval | Le bloc `list_item` conserve son texte canonique complet ; sa variante retrieval conserve la vraie instruction et retire seulement le suffixe répété ; signal `canonical_repeated_page_furniture_trimmed` présent | Décontamination source-safe validée |
| 2026-07-27 | Validation cuisine v111 | Bundle décompressé, PostgreSQL, navigation et scroll Qdrant | 114 chunks, 114 ancres, 114/114 sections, 114/114 `headingPath`, 30/30 liens de sommaire `title_exact` à 0,96, zéro occurrence parasite dans PostgreSQL et les 114 points Qdrant | Architecture mobilier promue |
| 2026-07-27 | Régression backend après filtrage du mobilier | 16 tests ciblés puis suite Release complète | 16/16 puis 1 999/1 999 réussis | Aucun échec backend introduit |
| 2026-07-27 | Première projection du profil structuré, scan v39 et cuisine v112 | Profil alimenté depuis les chunks canoniques plutôt que les pages legacy | Version structurée active et cartes du scan réduites de 19 à 13, mais des débuts de chunks internes restent pris pour des titres et des quantités OCR deviennent de fausses unités (`stockholm`, `january`, `ipe`) | Résultat rejeté, architecture revue |
| 2026-07-27 | Profil canonique v3, scan v40 | Cartes projetées uniquement depuis les vrais en-têtes du section tree, sans extraction sémantique heuristique ni pseudo-faits | 6 sections donnent exactement 6 cartes source, toutes `canonical_section_heading`, sans carte `unit_lead`, sans faux fait ; 8 chunks, 8 ancres, 8/8 `headingPath` et 8/8 IDs d’évidence | Qualifié live |
| 2026-07-27 | Profil canonique v3, cuisine v113 | Même code générique sur le livre de 36 pages | 61 cartes de sections dédupliquées ; les 30 titres du sommaire possèdent chacun une carte et un lien `title_exact` à 0,96 ; 114 chunks/114 ancres, zéro mobilier parasite dans PostgreSQL ou Qdrant | Qualifié live |
| 2026-07-27 | Contrôle qualité canonique et régression backend | Contrôles SQL des ancres/IDs, réconciliation PostgreSQL-Qdrant des deux catégories et suite Release complète | Aucun problème d’intégrité ; 2 002/2 002 tests backend réussis ; catégorie scans sans avertissement, avertissements cuisine restants limités aux autres révisions legacy | Lot canonique validé |
| 2026-07-27 | Audit de couverture native, NIST v18 | PdfPig contre canonique Docling, récupération page-ancrée et navigation documentaire | 20 lignes récupérées dans 5 blocs légitimes ; 148 chunks pipeline, 146 recherchables, 35/35 entrées de navigation avec cible, ancre et `title_exact` ; 146/146 points Qdrant | Lacunes natives et titres enveloppés qualifiés |
| 2026-07-27 | Contre-épreuve anti-bruit, cuisine v117 | Même réconciliateur générique sur le livre de 36 pages après correction des ligatures et mots coupés | 970 lignes candidates auditées, 0 récupération parasite ; 114 chunks, 114 ancres, 61 cartes, 30/30 navigations exactes et 114/114 points Qdrant | Absence de sur-récupération confirmée |
| 2026-07-27 | Contre-épreuve technique, fiche FIT v8 | Même pipeline sans règle par catégorie ni document | 0 récupération, 9 chunks, 9 ancres et 13 cartes | Chemin numérique simple préservé |
| 2026-07-27 | Contre-épreuve scannée, SMS v41 | PDF de 5 pages, Docling + RapidOCR régional, audit natif indépendant | 5 pages natives ignorées car non exploitables, 0 récupération ; table 9 × 3 et 27 cellules, 2 cellules ajoutées par réparation, 8 chunks, 8 ancres et 8/8 points Qdrant | Chemin OCR scanné préservé |
| 2026-07-27 | Qualification globale de la catégorie scannée | Harnais de qualité strict sur toutes les révisions indexées de la catégorie | `ok=true`, 0 problème, 0 avertissement ; comptes PostgreSQL-Qdrant exacts, dont SMS v41 à 8/8 | Catégorie qualifiée |
| 2026-07-27 | Test de capacité Docling avant alignement | Deux ingestions longues lancées ensemble, attente synchrone sidecar à 120 s | Première conversion en 504 à 120 s ; après ajout du sémaphore seul, seconde conversion en 504 à 240 s, prouvant la sérialisation mais aussi le budget sidecar insuffisant | Cause isolée |
| 2026-07-27 | Test de capacité Docling final | Un worker Docling, sémaphore client à 1, attente synchrone à 900 s, cuisine et NIST lancés ensemble | Les deux conversions terminent en HTTP 200 : 208,48 s puis 241,23 s ; aucune requête de conversion simultanée | Configuration robuste validée |
| 2026-07-27 | Mesure de contention Docling–TEI | Conversion Docling CPU et embeddings TEI exécutés en chevauchement | Docling et TEI saturent simultanément le CPU ; les durées de conversion et d’embedding augmentent fortement malgré la sérialisation Docling | Gouvernance CPU inter-services à concevoir après benchmark séquentiel |
| 2026-07-27 | Régression backend après audit natif, navigation et capacité | Suite Release complète | 2 008/2 008 tests réussis, 0 échec et 0 test ignoré | Lot fonctionnel validé |
| 2026-07-27 | Gouverneur partagé sous deux ingestions concurrentes avec CPU encore bridé | Cuisine v118 et NIST v19, Docling et TEI observés par `docker stats`, heartbeat actif | Aucun chevauchement lourd durable et aucun timeout ; 564,799 s et 589,788 s, mais le profil nocturne limitait le CPU à 25 % | Architecture validée, mesure de performance à refaire sans bridage |
| 2026-07-27 | Profil serveur sans bridage artificiel | Turbo autorisé, `max_perf_pct=100`, profil thermique Dell Performance ; ventilateurs observés jusqu’à 5 100 tr/min sous charge | Publication backend distante ramenée d’environ 80 s à 21–26 s ; fréquence et ventilation redeviennent pilotées par le matériel | Profil conservé |
| 2026-07-27 | Scanner à vide avec cache de contenu | 412 PDF inventoriés, 200 candidats de doublons de taille, cycles de 10 s | Premier cycle : 0 hit/200 miss ; cycles suivants : 200 hit/0 miss, fin de cycle en quelques millisecondes | Cache promu |
| 2026-07-27 | Diagnostic du polling worker | Zéro job actif, `strace`, compteurs PostgreSQL et journal temporaire des instructions | Avant correction : backend ≈74 % CPU, PostgreSQL ≈172 %, environ 3 385 `BEGIN`/`COMMIT` en 0,6 s et 826 862 `sched_yield` observés ; cause ramenée à l’alias `PollSeconds` | Défaut racine corrigé, pas masqué |
| 2026-07-27 | Validation du polling corrigé | Délai 500 ms visible dans `/ready` et au démarrage, file vide, deux workers | Backend stabilisé à ≈1,6 % CPU, PostgreSQL ≈1,2 % ; 107 `recvfrom` et 26 `sendto` seulement sur 5 s de `strace` | Correction live validée |
| 2026-07-27 | Requalification concurrente finale sans bridage | Cuisine v119 et NIST v20 lancés simultanément ; Docling 8 threads, TEI CPU, `HeavyCompute=1` | Cuisine 222,555 s, NIST 230,290 s, soit environ 60 % plus vite que v118/v19 ; aucun 504. Extraction : 170 588/76 882 ms ; embeddings : 43 560/53 407 ms | Gouvernance et profil matériel promus |
| 2026-07-27 | Contrôle qualité après performance | SQL Foundation, navigation, harnais qualité et Qdrant | Cuisine : 114/114 vecteurs, 114/114 `headingPath`, 30/30 `title_exact` ; NIST : 146/146 vecteurs, 35/35 cibles `title_exact`, 20 lignes/5 blocs récupérés ; catégorie scans : 14 documents, 0 problème, 0 avertissement | Qualité inchangée confirmée |
| 2026-07-27 | Régression backend finale du lot | Suite Release complète après cache scanner, gouverneur, observabilité et polling | 2 018/2 018 tests réussis, 0 échec, 0 ignoré | Lot backend validé |
| 2026-07-27 | Couche texte native C1 corrompue, catalogue ABB | Détection générique des contrôles U+0080–U+009F, OCR Docling forcé par conversion et blocage de la réinjection native | 16/16 pages OCRisées en 75,451 s ; 48 chunks et 48 ancres ; zéro caractère C1 ou de remplacement ; aucune page native corrompue réinjectée | Défaut racine corrigé et validé live |
| 2026-07-27 | Comparatif Docling Heron/Heron101, page de tableau PTFE | Même page, même runtime CPU, modèles officiels chauds | Heron : 21,008 s, tableau correct 23 × 25 / 436 cellules ; Heron101 : 21,088 s, structure erronée à 23 colonnes / 431 cellules malgré une confiance légèrement supérieure | Heron conservé ; challenger supprimé du sidecar |
| 2026-07-27 | Qualification inter-catégories après correctif C1 | ABB, livre cuisine scanné, brochure PTFE, rapport SDG et guide Yosemite | PostgreSQL et Qdrant concordent à 48/147/68/236/117 chunks-points ; autant d’ancres ; 616/616 points possèdent chemin, pages, ID de chunk et IDs canoniques | Correctif générique qualifié sur cinq structures |
| 2026-07-27 | Régression finale du correctif C1 | 96 tests ciblés puis suite backend Release complète | 96/96 puis 2 023/2 023 réussis | Lot commitable |

## Qualification de clôture du 2026-07-27

### Défaut racine fermé

Certains PDF contiennent une couche texte qui paraît présente mais transporte des
caractères de contrôle C1. Cette couche pouvait auparavant éviter l’OCR, puis être
réinjectée par la réconciliation native. Le pipeline :

1. mesure maintenant ce défaut dans `PdfPageExtractionQuality` avec le signal
   `invalid_control_chars_detected` ;
2. force l’OCR Docling uniquement pour la conversion concernée ;
3. marque toutes les pages comme candidates à cette conversion ;
4. interdit au réconciliateur de remettre le texte natif corrompu dans le
   document canonique.

Le mécanisme ne contient aucun nom de document, catégorie ou règle sémantique.
Il reste mécanique ; le LLM client conserve le choix des sources et leur
pertinence pour la réponse.

### Résultats live représentatifs

| Famille | Document | Version | Pages | Chunks PostgreSQL | Ancres | Points Qdrant | Métadonnées mécaniques manquantes |
|---|---|---:|---:|---:|---:|---:|---:|
| Couche native corrompue | `ABB - CAT-Instruments de mesure-FR.pdf` | 4 | 16 | 48 | 48 | 48 | 0 |
| Cuisine scannée/mixte | `livre-recette-sist-2025-web.pdf` | 67 | 32 | 147 | 147 | 147 | 0 |
| Tableau technique | `PTFE Brochure.pdf` | 7 | 11 | 68 | 68 | 68 | 0 |
| Rapport multi-pages | `UN_SDG_Report_2024.pdf` | 3 | 51 | 236 | 236 | 236 | 0 |
| Brochure multi-colonnes | `Yosemite-Guide-Volume-50-4-508V1.pdf` | 4 | 7 | 117 | 117 | 117 | 0 |

Les 616 points portent tous `doc_path`, `page_start`, `page_end`, `chunk_id` et
au moins un identifiant canonique de bloc, span ou cellule. La recherche live
sur ABB retourne bien le fichier et les pages mécaniques pertinentes.

### Choix du modèle de mise en page

Le catalogue officiel Docling présente Heron comme le profil équilibré par
défaut et les modèles plus lourds comme des options pour des cas complexes :
<https://docling-project.github.io/docling/usage/model_catalog/>.
La publication de référence montre un gain brut possible de Heron101, mais un
écart de coût CPU et des gains plus faibles après le post-traitement Docling :
<https://arxiv.org/html/2509.11720>.

La mesure locale sur le tableau PTFE est décisive : Heron101 ne corrige pas le
cas et dégrade le nombre de colonnes. Heron reste donc le modèle de production.
Le modèle temporaire Heron101 et le PDF de benchmark ont été supprimés du
conteneur ; aucun artefact de runtime abandonné n’est conservé.

## Questions ouvertes

1. L’Intel UHD 630 bat-elle le CPU pour RapidOCR/OpenVINO sur ce serveur ?
2. Sur quels signaux supplémentaires faut-il déclencher un challenger de structure sans multiplier les heuristiques fragiles ?
3. Comment arbitrer la T2000 entre Qwen3 (3,42 GiB mesurés) et les moteurs documentaires GPU sans provoquer d’OOM ?
4. Le correcteur régional CPU rend-il inutile la suspension du LLM pour la majorité des documents ?
5. Quels documents peuvent légalement être inclus dans un corpus gold versionné ?
6. Quel format d’artefact canonique offre le meilleur compromis JSON/MessagePack/Parquet ?
7. Quelle stratégie de shadow index minimise le risque de migration ?
8. Quel partage de capacité CPU entre Docling et TEI maximise le débit global sans dégrader la latence d’une ingestion isolée ?

## Prochain jalon

1. Ajouter au corpus gold des tableaux complets, cellules fusionnées légitimes, cellules vides, rotations, plusieurs tableaux par page et scans dégradés.
2. Mesurer l’ordre de lecture et les colonnes sur des documents techniques et multi-colonnes sans règle par catégorie.
3. Vérifier que les structures saines ne régressent pas et que les refus du correcteur restent exploitables par le LLM client.
4. Ajouter les overlays de diagnostic région/blocs/cellules et une vue des raisons d’escalade.
5. Évaluer TATR seulement sur les cas où la structure Docling elle-même est mécaniquement incohérente.
6. Mesurer OpenVINO/Intel et le partage matériel uniquement si le profil CPU régional ne respecte plus les objectifs de débit.
7. Mesurer une baseline séquentielle Docling puis TEI, la comparer au chevauchement actuel, puis concevoir un gouverneur de ressources partagé sur des preuves de débit et de latence.

## Passe ordre de lecture et corpus gold du 2026-07-28

### Cause racine mesurée

Les échecs multi-colonnes ne sont pas principalement un défaut d'OCR. Sur des
PDF natifs légaux et déterministes, Docling Heron et Egret peuvent regrouper
plusieurs colonnes dans un même bloc ou une fausse table. Une fois les lignes
fusionnées horizontalement, un simple tri des blocs Docling ne peut plus
reconstruire les récits indépendants.

Sept fixtures sont maintenant générées par `tools/ingestion_gold` :

1. deux colonnes ;
2. trois colonnes ;
3. deux colonnes de hauteurs différentes avec note de fin ;
4. deux colonnes suivies d'un tableau ;
5. tableau avec en-tête fusionné, cellule vide et cellule multiligne ;
6. texte natif, panneau raster, légende et liste ;
7. scan pleine page multilingue, légèrement tourné et peu contrasté.

Le manifeste versionne les SHA-256 et les invariants. Les attentes ne sont
jamais chargées par le runtime.

### Comparaison des moteurs

- Egret-large corrige le cas simple à deux colonnes et reste légèrement plus
  rapide à chaud, mais perd des tables, figures et index sur les documents
  réels PTFE, cuisine et Yosemite.
- Egret-medium interprète le récit à deux colonnes comme une table.
- L'OCR forcé pleine page ne corrige pas la cause lorsque le regroupement de
  mise en page reste faux.
- PP-StructureV3 fusionne également les lignes alignées entre colonnes et
  coûte environ 28 à 30 secondes par page CPU sur les fixtures, contre environ
  0,5 à 2,8 secondes pour Docling chaud.
- Heron reste donc le parseur structurel primaire. Les challengers rejetés et
  leurs conteneurs temporaires ont été supprimés.

### Architecture retenue

Le backend conserve maintenant une seconde observation mécanique du calque
PDF natif :

- `NearestNeighbourWordExtractor` reconstruit les mots à partir des glyphes ;
- `RecursiveXYCut` segmente les régions ;
- `UnsupervisedReadingOrderDetector` produit l'ordre de lecture par colonnes ;
- chaque bloc conserve texte, page, ordre, rectangle et identifiant
  d'algorithme ;
- Docling reste propriétaire des tables, figures, titres, cellules et ancres
  structurelles.

`CanonicalNativeLayoutReconciler` publie un
`native_layout_recovery` seulement lorsque :

1. la lecture géométrique et la lecture native aplatie couvrent pratiquement
   les mêmes mots ;
2. leur ordre diverge de façon mesurable ;
3. Docling n'a pas déjà conservé correctement cette lecture ;
4. les gardes mécaniques de fragmentation et de région tabulaire autorisent
   le candidat.

Les blocs sont des alternatives, pas une vérité sémantique imposée. Leur
provenance enregistre moteur, version, algorithme, accords d'ordre et de
contenu, politique de table et
`semanticDecisionOwner=llm_client`. Le projecteur de retrieval les transporte
avec leurs IDs canoniques et signaux de qualité. Le LLM client reste seul juge
de leur pertinence pour une question.

### Résultats synthétiques et réels avant déploiement

Sur les réponses Heron capturées, la seconde lecture restaure :

- 12 blocs utiles sur le cas colonnes plus tableau, hors cellules structurées ;
- 17 blocs sur les trois colonnes ;
- 14 blocs sur les colonnes inégales ;
- le cas simple à deux colonnes dans le test PDF généré en C#.

Le tableau structuré déjà correct n'est pas dupliqué. Le scan sans calque natif
ne fabrique aucun bloc. Sur les captures réelles, l'audit local a retenu des
alternatives sur les pages multi-colonnes de cuisine, PTFE et Yosemite, tout
en laissant les tables canoniques Docling disponibles séparément. Cette
mesure qualifie l'algorithme ; elle ne vaut pas encore validation live du
nombre final de chunks ou de la qualité du retrieval.

### Validation locale

- corpus gold Python : 5/5 ;
- sidecar Python : 7/7 ;
- compilation Python : réussie ;
- tests C# ciblés extraction, OCR, canonique et retrieval : 97/97 après
  correction du hash du fixture de test ;
- suite backend Release : 2 027/2 027, zéro échec et zéro test ignoré ;
- `git diff --check` ciblé : aucune erreur de contenu.

### Limites encore ouvertes

1. L'ordre d'un pied de page ou d'une note pleine largeur peut rester placé
   entre deux colonnes par le détecteur PdfPig ; le mobilier multi-page est
   retiré ensuite, mais le cas mono-page doit rester observable.
2. Le panneau raster synthétique est OCRisé, mais Docling ne le publie pas
   encore toujours comme figure.
3. Le harnais évalue actuellement la sortie brute Docling ; il faut ajouter
   une vue canonique post-réconciliation.
4. À ce checkpoint, le lot n'était pas encore déployé. Cette limite est fermée
   par la qualification live consignée ci-dessous.

## Qualification live de la seconde lecture — 2026-07-28

### Correctifs promus

La qualification serveur a révélé puis fermé quatre défauts distincts, sans
ajouter de règle liée à une catégorie ou à un document :

1. les blocs alternatifs PdfPig entraient encore dans la construction des
   sections Docling ; ils sont désormais isolés comme surfaces de recherche
   indépendantes ;
2. une alternative trop peu segmentée pouvait concaténer presque toute une
   page ; elle est maintenant refusée par des contrôles mécaniques de
   segmentation et d'accord de contenu ;
3. le texte de retrieval pouvait conserver un espace artificiel après un
   tiret (`semi- finished`) ; la variante retrieval est réparée en conservant
   le tiret et le texte brut ;
4. un niveau de titre Docling aberrant pouvait contaminer les pages suivantes.
   La hiérarchie est désormais normalisée page par page et une page sans titre
   n'hérite d'un contexte antérieur que si la section canonique prouve la
   continuation.

Les quatre lots correspondants sont commités séparément :

- `515f0ef` — isolation des alternatives natives ;
- `b19495e` — refus des alternatives sous-segmentées ;
- `a50dde6` — normalisation du texte de retrieval après tiret ;
- `a1e6bfe` — contexte de titres borné par page.

La suite backend Release finale réussit **2 031/2 031** tests, sans échec ni
test ignoré.

### Réingestions finales sur le serveur

Le backend déployé expose exactement la révision
`a1e6bfeeb3cd201d018a9948bc979bb654c0544f-source-1d147051ae6756266130dda7250044d0c554235600e201708a67986fd9adfcfa`.
Les quatre jobs ont terminé du premier coup, sans retry ni erreur.

| Famille | Document | Version | Révision | Pages | Chunks | Ancres | Points Qdrant | Alternatives |
|---|---|---:|---|---:|---:|---:|---:|---:|
| Cuisine mixte | `livre-recette-sist-2025-web.pdf` | 72 | `9441ef23-04c6-840a-33b1-a64f53c7222a` | 32 | 176 | 176 | 176 | 24 blocs sur 18 pages |
| Tableau technique | `PTFE Brochure.pdf` | 11 | `12ab9a1c-c37e-6084-051a-101d1a7959a9` | 11 | 90 | 90 | 90 | 23 blocs sur 10 pages |
| Brochure multi-colonnes | `Yosemite-Guide-Volume-50-4-508V1.pdf` | 8 | `1f9dbf4d-138e-c2ff-0506-8383d41305e2` | 7 | 128 | 128 | 128 | 11 blocs sur 2 pages |
| Scan + tableau | `SMS 1145…6 atm.pdf` | 44 | `e257edf0-768e-0a00-d038-dc374416cfcc` | 5 | 10 | 10 | 10 | 0 |

Yosemite enregistre en plus une page alternative refusée pour
sous-segmentation. Le scan conserve son tableau 9 × 3 et ses 27 cellules ;
PTFE conserve 817 références distinctes de cellules dans 18 chunks de table.

### Intégrité des artefacts

Pour chacun des quatre bundles `canonical_ingestion_bundle_v1`, la charge gzip
a été relue depuis PostgreSQL et décompressée en mémoire :

- taille décompressée égale à `byte_size` ;
- SHA-256 recalculé égal à `content_hash` ;
- `document.manifestSha256` égal au hash de l'enveloppe ;
- IDs de révision du document et du manifeste égaux à la révision publiée ;
- nombre d'ancres du JSON égal au nombre d'ancres PostgreSQL et aux chunks ;
- stage PdfPig en version `0.1.13.0` ;
- `semanticDecisionOwner=llm_client`.

Les tailles brutes vérifiées sont 2 123 051 octets pour cuisine, 1 587 293
pour PTFE, 2 480 991 pour Yosemite et 146 225 pour SMS.

### Retrieval orienté par le LLM

Les tests live montrent une frontière architecturale nette :

- `gâteau chocolat-courgette ingrédients préparation` retourne la bonne
  recette page 23 au rang 1 ;
- `PTFE product forms compression molded billets sheets rods` retourne
  l'alternative multi-colonnes correcte page 5 au rang 1 ;
- une sous-requête précise sur l'absence de carburant dans Yosemite Valley
  retourne la page 5 au rang 1 ;
- une sous-requête précise sur les trois parkings principaux retourne la page
  2 au rang 1 ;
- `Rd 48-6` retourne le tableau SMS page 3 au rang 1.

À l'inverse, une question naturelle large peut encore être réduite par le
backend historique à un terme générique ou déclencher trop tôt une route de
navigation. Ce n'est pas un défaut d'ingestion : le contenu exact, les pages,
les tableaux et les alternatives sont présents et récupérables. La prochaine
validation de réponse doit prouver que le LLM client observe ses résultats,
reformule et choisit lui-même les sous-requêtes suivantes, conformément à
l'ADR.

### Limite observable conservée

Sur la page 16 du livre cuisine, Docling place le titre visuel de la recette
après la colonne des ingrédients dans son ordre primaire. Le chunk
`Ingrédients` ne porte donc que ce titre local. La seconde lecture mécanique
conserve cependant le titre de recette puis tous les ingrédients dans le bon
ordre ; une recherche ciblée retourne la préparation rang 1, les ingrédients
rang 2 et l'alternative complète rang 3.

Il serait fragile de transformer ce cas en règle « recette ». Il est ajouté au
backlog du corpus gold comme problème générique de titre supérieur livré après
une première colonne.

### Harnais global

Les quatre catégories restent `ok=true` et les comptes PostgreSQL/Qdrant sont
exacts. La catégorie des 14 documents scannés ne produit aucun avertissement.
Les avertissements cuisine, technique et voyages concernent des révisions
plus anciennes non réingérées : 12 candidats de coupure après tiret au total,
des cartes de profil tronquées ou suspectes et quatre unités substantives non
couvertes. Ils restent visibles dans le backlog ; ils ne sont ni masqués ni
présentés comme corrigés par ce lot.

## Qualification canonique automatisée — 2026-07-28

Le harnais gold ne s'arrête plus à la réponse brute du sidecar. Un projecteur
C# dédié appelle désormais les mêmes composants que la production :

1. projection du document Docling ;
2. extraction native PdfPig ;
3. réconciliation de couverture textuelle et de lecture géométrique ;
4. inventaire des images PDF natives ;
5. validation du contrat canonique ;
6. projection des chunks réellement publiables.

Le corpus compte maintenant huit PDF synthétiques déterministes, dont un cas
générique où l'ordre des objets source diffère de la hiérarchie visuelle d'un
titre et de deux colonnes. La campagne live finale contre
`infra-docling-1` réussit **8/8 cas canoniques**, alors que la sortie brute
Docling seule n'en réussit que **2/8**. Tous les chunks évalués conservent leurs
identités de preuve et leurs pages.

Le dernier écart du document mixte est fermé sans classification métier :
PdfPig conserve le placement du raster embarqué sous forme de figure
`embedded_raster`, avec page, polygone normalisé, dimensions en pixels,
version du moteur et provenance
`semanticDecisionOwner=llm_client`. Aucun texte alternatif, aucune légende et
aucune pertinence ne sont inventés. La même mécanique inventorie correctement
le raster d'un scan pleine page.

Résultats saillants de la campagne :

- colonnes simples : 12 blocs alternatifs, ordre restauré ;
- trois colonnes : 17 blocs alternatifs, ordre restauré et table primaire
  conservée ;
- colonnes inégales : 14 blocs alternatifs ;
- titre/ordre source tardif : 12 blocs alternatifs ;
- colonnes avec tableau : 12 blocs alternatifs et table intacte ;
- tableau structuré : aucune alternative inutile ;
- texte + raster : une figure native et tous les marqueurs OCR ;
- scan pleine page : une figure native, tous les marqueurs OCR, aucune
  alternative inutile.

Le rapport reproductible est écrit localement sous
`tmp/pdfs/ingestion-layout-canonical-final-report.json`; les réponses et
snapshots de diagnostic restent sous `tmp/` et ne sont pas versionnés.

## Promotion production de l'inventaire d'images — 2026-07-28

Le commit `fde5b24d` est déployé sous la révision exacte
`fde5b24db6f78e071fa6e53c4c7c521c69f85bc4-source-0360babcbcd5cfd211c4c41afdb642a95b6c6c8420f1da967f204bd1d7c455d4`.
`/ready` reste `ok=true` et expose
`document_intelligence_native_pdf_image_inventory_enabled=true`.

Deux réingestions de production ont terminé au premier essai :

| Famille | Version | Révision | Chunks | Ancres bundle/SQL | Qdrant | Figures Docling | Placements PdfPig |
|---|---:|---|---:|---:|---:|---:|---:|
| Cuisine mixte | 73 | `520fac1e-0508-784b-00c8-9ec6125435a6` | 176 | 176/176 | 176 | 149 | 290 |
| Scan SMS | 45 | `1a44afb5-2a41-fa75-3dec-c89b571225ab` | 10 | 10/10 | 10 | 5 | 5 |

Les deux bundles gzip ont été décompressés et vérifiés en mémoire : taille
exacte, SHA-256 exact, stage `native_pdf_image_inventory` unique, révision de
code exacte et `semanticDecisionOwner=llm_client`. Le bundle cuisine mesure
2 320 458 octets bruts / 343 598 octets stockés ; le bundle scan mesure
149 518 / 24 029 octets.

Une inspection géométrique du scan a aussi empêché une fausse conclusion de
duplication : les cinq placements PdfPig couvrent chacun une page raster
complète, tandis que les cinq figures Docling sont des sous-régions visuelles
internes (une page 2 et quatre page 4). Ces objets décrivent deux granularités
de preuve différentes. Leur simple imbrication ne justifie donc pas de
supprimer l'une d'elles par une heuristique de chevauchement.

## Qualification isolée des canaux auxiliaires — 2026-10-27

Statut produit : **TESTE_NON_APPROUVE**. Cette qualification ne constitue
ni une promotion du déploiement habituel, ni une validation du planning.
Elle n'a appelé aucun fournisseur LLM payant.

Deux options, désactivées par défaut, permettent désormais de conserver les
observations typographiques natives et de lire les métadonnées des images
source via `read_source`. La typographie conserve les glyphes, positions et
ressources effectivement observés, avec des plafonds et motifs de refus.
Elle ne certifie ni le rendu des polices, ni les relations mathématiques.
Les citations d'images sont liées au tenant, document, révision, SHA source
et page ; leur route API reste authentifiée. Elles ne transmettent aucun
pixel au modèle et ne prouvent pas l'existence d'un viewer client.

Les gardes ajoutées refusent les grilles de tableaux dépassant le budget
de projection avant allocation et évitent les débordements d'indices.
Une formule ou un bloc de code sans transcription canonique signale aussi
son absence dans les chunks voisins de la page. Le texte brut conservé ne
devient pas, par cette seule conservation, une transcription vérifiée.

Vérifications sur les mêmes sources, rehashées avant leur commit :

- 5 848 tests unitaires PASS, deux tests ignorés explicitement ;
- cinq tests PostgreSQL isolés PASS, comprenant publication typographique
  et lectures des citations visuelles ;
- build WinUI Release x64 sans avertissement ni erreur ;
- image Linux construite depuis l'identité source
  `12a6c419c332895f8bc36cd13156f46e11e9e95bf858db18305d536e98a6d582` ;
- dix documents ingérés par le vrai worker en Production signée, chacun
  terminé en une tentative ; zéro job actif après la campagne ;
- lecture SQL réelle des dix catalogues, 930 lignes secondaires et
  191 assets : identités, pagination, SHA et digests avant/après conformes ;
- typographie observée sur 85 pages, avec états de refus ou d'indisponibilité
  conservés ; aucun style rendu ni glyphe refusé n'est déclaré validé ;
- seuls les conteneurs de qualification possédés ont été utilisés et arrêtés.

La campagne principale conserve son verdict négatif. Ses contrôles signalent
encore l'attente d'un ordre alternatif sur le cas en colonnes, une différence
sur un fait de recette, le scan mathématique et certaines surfaces
structurées NIST. Leur diagnostic reste séparé du succès de stockage et de
lecture des canaux auxiliaires ; les oracles existants restent inchangés.

Preuves privées locales : `artifacts/ingestion-fidelity-fixes-20261026/`.
La capture principale Source18 a pour SHA-256
`ab5d88f41b190a71d5ccefc7b78850083d0deb9f5e1cee7680629956cfd48a8a` ;
le supplément de lecture des canaux a pour SHA-256
`e49c863672a78b1abb64ce16d0ff04830e8c03f607c104ea7c66c8afd84bc82e`.
Les PDF tiers, captures privées, clés et poids de modèles ne sont pas inclus
dans le dépôt public.

## Lecture littérale des audits natifs — qualification locale du 2026-10-27

L'option `AdvancedAnalysis.NativeObservationReadEnabled`, désactivée par
défaut, expose désormais l'inventaire et les lignes des observations natives
déjà conservées dans le bundle canonique. Le modèle découvre cette capacité
dès le premier prompt. Les lignes sont revalidées depuis le stockage avec
tenant, document, révision, SHA source, manifeste, bloc et empreinte littérale.
Leur référence ne fabrique ni chunk indexé, ni ancre, ni relation de cellule.

Le lecteur conserve les caractères et terminateurs de lignes exacts. Son
curseur est un ordinal de ligne ; les bornes de citation comptent des scalaires
Unicode. Une ligne indivisible hors budget est refusée sans avancer le curseur.
Les textes figurent une seule fois dans les preuves du prompt ; les inventaires
et sorties d'outils portent des métadonnées et références. Le budget inclut le
JSON effectivement échappé dans les payloads Chat et Responses. L'absence
d'observation capturée reste inconnue, distincte d'un texte capturé vide.

Le gel de ce lot comprend 21 fichiers, dont les tests. Vérifications locales :

- 577 tests ciblés PASS ; les quatre cas PostgreSQL sont exécutés séparément ;
- quatre tests PostgreSQL isolés PASS, sur la DLL finale du lot ;
- 5 884 tests unitaires globaux PASS, deux ignorés et aucun échec ;
- 2 032 sources de campagne identiques avant et après la suite globale ;
- build WinUI Release x64 sans hooks de test, zéro avertissement et zéro erreur ;
- contre-revue indépendante du lot sans défaut matériel ouvert dans son périmètre.

Le build Linux du lecteur est distinct de la publication d'ingestion : identité
source `aef763fb4c928e5ba057e920f573ec35a1e686972a818fd5f6fc59feeeb2bf76`.
Il ne remplace pas l'identité du worker Source18 et ne prouve pas une nouvelle
ingestion. La qualification Linux de ce lecteur sur les données Source18 fait
l'objet d'un supplément séparé ; les résultats locaux ci-dessus ne la présument
pas. Aucun nouveau parcours visuel WinUI, viewer de référence native, pixel
fourni au modèle ou appel LLM payant n'est qualifié par ces tests.

Ces lectures restent secondaires et leur fidélité sémantique non vérifiée.
Elles ne remplacent ni le texte primaire ni les oracles existants. Les essais
OCR locaux supplémentaires et leurs sorties non approuvées sont conservés
séparément dans les artefacts privés ; ils ne modifient pas le moteur de
production. Le statut produit reste **TESTE_NON_APPROUVE**.

## Lecteur Linux Source19 — résultats supplémentaires du 2026-10-27

Le chargement réel des composants du lecteur dans l'image Linux a réussi,
sans démarrer le backend ni appeler un modèle. L'essai exhaustif suivant sur
PostgreSQL s'est arrêté au délai coopératif de 360 secondes, pendant une
lecture native. Aucun bilan partiel durable ne permet d'en déduire que les
premiers documents étaient validés. Le harnais rechargeait le bundle complet
pour de nombreuses petites fenêtres ; son volume théorique de décompression
n'est pas une mesure de la latence normale du produit.

L'essai avec les plafonds documentaires par défaut (14 000 caractères de
prompt, 24 000 par élément et 256 000 au total) reste également négatif.
Deux causes sont distinguées : le harnais s'arrêtait après un refus de lot
de quatre lignes au lieu d'essayer une fenêtre plus petite ; sur NIST, le
volume des métadonnées pouvait refuser même une ligne. Le rejeu hors ligne
sur la DLL Source19 montre que les sept lignes de l'avertissement de recette
sont lisibles une à une dans Chat et Responses, sans changer ces plafonds.
Ce diagnostic Windows ne constitue pas une répétition Linux réussie.

Les preuves négatives, les captures SQL et les oracles antérieurs sont
conservés. Le nouveau lot de navigation, de sondes et de budget documentaire
doit être qualifié séparément ; les résultats Source19 ne le présument pas.
Les essais Granite sur quatre pages originales n'ont pas fourni une
alternative suffisamment fidèle et ne modifient pas le moteur de production.

## Navigation canonique, sondes vectorielles et budget documentaire — lot Source20

Les sommaires canoniques utilisent leurs cellules typées et les chunks
réellement publiés. Les numéros imprimés restent distincts des pages physiques ;
une cible physique exige un titre canonique unique et corroboré. Les limites,
ambiguïtés et refus sont persistés, y compris lorsqu'aucune entrée n'est
produite. Le rejeu hors ligne du sommaire NIST page 5 restitue 26 entrées sur
27 ; le chevauchement minuscule de la dernière ligne reste un refus explicite.
Ce résultat ne remplace pas une observation de l'API sur une nouvelle ingestion.

Les sondes vectorielles conservent les causes de rejet, la couverture de
l'inspection, des compteurs et des échantillons bornés. Les omissions sont
explicites. Une inspection terminée ne prouve ni la visibilité peinte ni
l'éligibilité géométrique d'une région. Les règles d'admission restent
inchangées ; clipping et visibilité effective restent inconnus.

Le budget natif ne recompte plus chaque catalogue partagé comme une nouvelle
preuve par ligne. Les retours d'outils peuvent référencer les métadonnées
strictement identiques présentes dans le message courant ; les catalogues ou
fenêtres divergents restent complets. Les qualités, identités, empreintes et
curseurs demeurent dans le payload. Le garde sur le JSON final échappé et les
plafonds 14 000/24 000/256 000 restent inchangés. Sur la capture NIST réelle,
une ligne entière passe hors ligne dans Chat et Responses ; cela ne garantit
pas qu'un lot de deux lignes entre dans le même plafond. Ce plafond est une
contribution documentaire en caractères, pas la taille totale HTTP ni un
nombre de tokens. L'adaptation quatre/deux/une ligne relève du harnais de test.

Qualification locale de l'ensemble gelé le 2026-10-27 :

- 5 931 tests unitaires PASS, deux ignorés, zéro échec ;
- sept tests PostgreSQL isolés PASS, sans skip, dont les trois nouveaux cas de navigation ;
- assemblages PostgreSQL identiques à ceux de la campagne unitaire ;
- 2 044 fichiers de source identiques avant et après les campagnes ;
- compilation WinUI Release x64/win-x64 sans hooks, zéro avertissement et zéro erreur ;
- contre-revues indépendantes des trois lots sans défaut matériel ouvert dans leurs périmètres.

La base PostgreSQL temporaire est supprimée et son port fermé. La compilation
WinUI ne constitue pas un nouveau parcours visuel. Le gel Linux Source20 et
ses images ont leur propre identité ; leur qualification réelle est séparée
de ces résultats locaux. Aucune preuve d'ingestion complète, de réparation
des prédictions OCR ou de planning réussi n'est déduite de ces tests.
Le produit reste **TESTE_NON_APPROUVE**, sans appel payant dans cette campagne.

## Résultats Linux Source20 et références de qualité Source21

La nouvelle ingestion Source20 a réellement traité les dix PDF de référence
du 2026-10-27T06:29:06Z au 2026-10-27T06:43:04Z. Les dix jobs sont terminés ;
zéro job actif et cinq conteneurs arrêtés sont constatés après le parcours.
La signature Production, l'image et la DLL sont liées aux preuves capturées.
Les 427 fichiers rapatriés sont vérifiés par empreinte. Le vérificateur brut
reste négatif sur les quatre cas historiques ; aucun oracle n'est modifié.

La nouvelle capture SQL/API confirme 26/27 entrées NIST page 5, avec titres,
labels imprimés, cellules sources publiées et cibles physiques concordants.
Sur la page 40, les sondes observent 158 primitives inspectées, 64 supportées
et 64 règles produites, avec 56 rejets de clipping et deux formes remplies
non supportées. La géométrie reste partielle. Les 62 lignes OCR secondaires
sont conservées sans rattachement physique. Ni clip effectif ni coordonnées
des rejets ne sont prouvés par ces diagnostics.

Le contrôle élargi Source20 révélait encore huit lignes NIST refusées dans
au moins un protocole. Source21 remplace uniquement une troisième copie
des alertes de qualité par une référence aux preuves exactes du message
courant. Chaque ligne, identité, empreinte, borne et tableau d'alertes est
comparé avant substitution. Les deux tableaux par preuve restent présents ;
absence, divergence ou fenêtre incomplète conserve le tableau de la vue.
La référence vise la collection effective `evidence` ou `observations`.
Le flag OFF et le garde final de 14 000 caractères restent inchangés.

Qualification locale Source21 : 589 tests ciblés PASS et suite backend
complète à 3 366 PASS, deux ignorés, zéro échec et sources stables. Le rejeu
des captures réelles Source18 admet 69/69 lignes NIST et 87/87 de recette
dans Chat et Responses, soit 312 payloads HTTP en mémoire sans refus,
réseau, SQL ni modèle. Les alertes entières et les références sont contrôlées.
Ce lot ne change aucun contrat client ni stockage SQL ; les validations
client et PostgreSQL Source20 restent identifiées comme telles.

Le lecteur Source21 doit encore être qualifié sur les données réellement
ingérées par Source20. Les résultats Windows et la fin des jobs Linux ne
certifient ni les formules, ni les fusions de cellules, ni un planning.
Le statut produit demeure **TESTE_NON_APPROUVE**.

## Lecture Linux Source21 et capture diagnostique des coordonnées Source22

Le lecteur Source21 a été construit dans une nouvelle image Linux et son
chargement qualifié séparément : cinq composants chargés correspondent aux
fichiers du backend. Le premier opérateur de lecture a ensuite refusé le
manifeste Source20 avant Docker et avant SQL, car sa liste de fichiers omettait
`capture-navigation-v20.py`. L'échec et sa reproduction sont conservés.
L'opérateur v2 admet uniquement ce nom supplémentaire, déjà lié au manifeste
par empreinte ; ses 44 contrôles hors ligne incluent les 69 cibles réelles.

La lecture v2 a réellement réussi avec l'image Source21 sur les données
Source20 : NIST p.5 et p.40, recette p.5, 30 blocs et 156 lignes reconstruits.
NIST p.5 conserve une capture native indisponible avec comptes inconnus ;
les sept blocs/69 lignes de p.40 et les 23 blocs/87 lignes de recette sont
restitués sans perte. L'avertissement de recette conserve son apostrophe U+2019.
Reader, résolveur et passerelle sont exercés en SQL readonly. Les deux formats
fournisseur sont sérialisés en mémoire, aux plafonds 14 000/24 000/256 000,
avec alertes entières et références exactes. Aucun modèle n'est appelé.
Les digests de publication sont identiques avant/après. Le lecteur et les
cinq services possédés sont arrêtés, zéro job reste actif et les ports sont
absents. Ce parcours représentatif ne qualifie pas encore les 104 pages ni
la fidélité de transcription.

Source22 ajoute une capture interne facultative de la géométrie réellement
reçue par le backend. Elle conserve les lexèmes numériques JSON avant leur
conversion en doubles, les bits réellement lus et les points produits,
avec liaison table/page/provenance/cellule. Les chaînes décodées sont
distinguées des nombres bruts. L'empreinte PDF porte sur le file-part
effectivement sérialisé dans la même tentative. Le point observé est l'entité
HTTP finale après décompression ; ce n'est pas le JSON original amont de
Docling. Aucun texte, image ou base64 du document n'est copié dans ce payload.

Le flag est OFF par défaut. ON sans stockage canonique ne collecte pas de
géométrie et produit un motif explicite dans la sonde. Le budget de cinq
secondes de travail propre est cumulatif et clos avant le manifeste ; les
attentes réseau n'y sont pas imputées. Les caps de réponse, inventaire, JSON
et gzip sont explicites. Le carrier préparé est immuable. Le store privé
utilise la table binaire existante dans la même transaction que le bundle,
vérifie le tuple de publication, admet une répétition identique et refuse
une substitution. Rollback et purge de révision ne laissent pas d'orphelin.

Qualification locale Source22 : RED initial à cinq échecs et un témoin
positif ; ciblé final à 27 PASS sans avertissement ; quatre PostgreSQL
isolés PASS sans skip ; suite backend complète à 3 393 PASS, deux ignorés
et zéro échec. Les 2 054 fichiers suivis par le harnais sont identiques
avant/après. Les 19 sources du lot correspondent au gel contre-relu ; les
empreintes produit des PostgreSQL correspondent aussi à ce gel. La base
temporaire est supprimée et son port fermé. La contre-revue ne relève aucun
défaut matériel ouvert dans ce périmètre. Les contrats et le client sont
inchangés ; leurs résultats Source20 ne sont pas renommés Source22.

Un témoin synthétique multi-provenance établit huit liens diagnostiques
distincts avant le refus primaire existant de CellId répétées entre pages.
Il ne prouve pas un bundle multipage valide et ne justifie aucune réparation
par ajout de page aux identifiants sans qualifier la correspondance physique.
La sonde Source22 attend sa construction et sa qualification Linux réelles.
La navigation NIST reste à 26/27, sans nouvelle tolérance. Le produit demeure
**TESTE_NON_APPROUVE**, sans essai payant ni planning dans cette campagne.

## Reconnaissance des rectangles remplis implicitement, Source23

Une capture réelle de NIST p.40, séparée de toute ingestion et sans SQL,
retrouve 130 chemins, 158 sous-chemins et 746 commandes. Les deux formes
remplies non supportées sont des rectangles axiaux Move + trois Line,
sans Close explicite. Leur remplissage ferme implicitement le contour selon
la sémantique PDF ; leurs hauteurs de 11,52 et 11,46 points dépassent le seuil
d'une règle fine. Elles ne doivent donc produire aucune nouvelle règle.

Source23 reconnaît cette forme exacte uniquement pour un remplissage sans
stroke, avec continuité, quatre coins distincts, deux abscisses, deux ordonnées
et quatre côtés axiaux finis. Les commandes sources restent intactes. Aucun
segment de fermeture n'est ajouté aux tracés ouverts S ou B ; les clips,
budgets, seuils et limites de visibilité restent inchangés.

La reproduction avant correction parse douze vrais PDF synthétiques et donne
trois échecs causaux et neuf témoins positifs. Après correction, 33 contrôles
ciblés passent, sans avertissement, avec sources stables et les mêmes douze
empreintes PDF réellement parsées. Ce résultat local ne remplace pas un rejeu
sur le PDF réel. La page 40 doit conserver ses 64 règles, ses 56 refus de
clipping et sa géométrie partielle ; la fidélité et la visibilité effective
ne sont toujours pas approuvées. Aucun appel payant n'est exécuté.

## Couverture exhaustive du lecteur21 sur les données20

Les 34 tranches de lecture réelle ont réussi. L'agrégat couvre dix documents,
104 états de page, 736 blocs et 2 750 lignes du canal des observations natives,
sans tranche manquante, échouée ou inconnue. Les 211 fichiers exportés sont
recontrôlés par empreinte ; le recalcul sur le PC donne les mêmes octets
d'agrégat que sur le serveur. La contre-revue vérifie aussi les 34 commandes,
les digests avant/après et entre tranches, les contrôles de périmètre ainsi
que l'arrêt du lecteur et des cinq services possédés à chaque tranche.

Le budget de diagnostic est 200 000/24 000/256 000 caractères. Ce résultat
ne qualifie pas le plafond produit de 14 000, ni la fidélité OCR, ni une
atomicité globale entre les tranches ; il ne recertifie pas l'ingestion20.
Les lignes entières sont vérifiées par le programme figé, avec empreintes
et comptes conservés ; le texte brut n'est pas recopié dans cet export.
Aucun fournisseur, modèle, OCR ou PDF n'est exécuté par ces lectures SQL.

## Sondes de lecture24 et correction du périphérique diagnostique25

Les nouvelles sondes distinguent connexion/pool, transaction, accès SQL avec
matérialisation, décompression avec copies, empreinte, UTF8, JSON et validation
du bundle, puis les fenêtres et enrichissements. Elles sont activées par
un contexte Activity booléen explicite ; absentes ou OFF, elles n'émettent
aucun détail supplémentaire. Elles réutilisent BackendDiagnostics, avec
filiation des spans, phases fermées et sorties inconnues null. Aucune donnée
documentaire, requête ou exception.Message n'est ajoutée aux mesures ; aucune
dimension métrique ne contient d'identifiant de document. Un collecteur qui
échoue ne remplace pas le résultat métier. Aucun cache ou budget n'est changé.

Le helper commun est placé dans Observability pour éviter une dépendance du
stockage d'artefacts vers la couche AdvancedAnalysis. Le RED causal compte
six échecs et trois témoins ; le ciblé initial étendu passe 620 tests. Après
le déplacement sans changement de logique, 56 contrôles passent, dont les
14 nouveaux, sans avertissement. Les requêtes littérales restent identiques.
Ces tests n'exécutent pas SQL ; l'attribution des coûts du runtime reste à
mesurer. Durées parents et enfants sont inclusives et ne doivent pas être
additionnées. Les octets matérialisés ne sont pas des octets réseau/disque.

La qualification Linux22 a révélé deux échecs distincts, tous deux conservés.
Le premier refuse le bridge avant préparation : une copie CRLF ne correspond
pas à son empreinte LF. Le paquet v3 restaure les octets exacts et vérifie les
huit pins transitifs avant toute copie. Préparation et signature PC réussissent.
La clé privée reste sur le PC ; seule la signature est transférée.

Le runtime22 démarre réellement le 2026-10-27T08:38:10Z et s'arrête à 08:39:30Z.
Le document native-columns-and-table-v1.pdf échoue à sa première tentative :
le nouveau stage diagnostique référence cpu, alors que le vrai profiler
déclare cpu:0. Le runner conserve son verdict négatif et constate zéro job
actif après nettoyage. Les cinq conteneurs sont réobservés arrêtés, sans OOM.
Les captures navigation/géométrie ne sont pas produites et ne sont pas validées.
Les 54 fichiers de preuve exportés sont vérifiés ; cette cohorte n'est pas
réessayée dans son dossier.

Le correctif25 change uniquement DeviceId en cpu:0, déjà déclaré. Le RED
utilise les vraies StageManifestFactory, BundleFactory et Capture du profiler :
deux échecs observed/refused reproduisent broken_reference, deux contrôles
restent verts. Le GREEN élargi passe 31 tests, zéro skip et avertissement.
Le validateur et l'inventaire matériel restent inchangés.

La suite backend intégrée23/24/25 passe 3 423 tests, deux ignorés, zéro échec,
avec 2 058 fichiers sources stables du 2026-10-27T08:56:23Z à 08:58:11Z.
Les gels sont contre-relus. La nouvelle archive Linux contient 574 fichiers
et exactement sept modifications de production depuis22 ; les 34 sources
du sidecar restent identiques. Sa construction et la nouvelle ingestion
restent des étapes distinctes. Le produit demeure TESTE_NON_APPROUVE.

## Qualification réelle Source25 et localisation du refus de navigation

La construction Linux25 a terminé avec une image et un binaire identifiés,
puis une nouvelle configuration a été signée et vérifiée sur le PC. Seule
la signature est transférée ; la clé privée reste sur le PC. Le runtime
Production25 exécute réellement les dix jobs en une tentative chacun du
2026-10-27T09:12:59Z au 09:27:02Z. La signature, le runtime TEI et les
identités des points Qdrant sont vérifiés. La campagne conserve son verdict
global négatif : les critères historiques sur colonnes, recette, scan
mathématique et surfaces NIST ne sont pas tous satisfaits. Les cinq services
possédés sont arrêtés, sans OOM ni job actif ; 486 fichiers de preuve
rapatriés sont recontrôlés par empreinte.

La capture géométrique interne produit dix payloads observés, dont quatre
sans cellule de tableau, zéro refus et zéro canal absent. Les quatre vides
ne signifient pas que leurs documents sont vides. Les captures readonly de
navigation et de géométrie conservent les digests de publication avant/après.

Sur NIST p.5, 26 entrées de navigation sont produites. La ligne 12 refusée
contient déjà le chevauchement dans l'entité reçue du sidecar : le bord droit
du titre est le token `534.0`, le bord gauche du numéro est
`533.9999999999999`. Les doubles lus diffèrent d'un ULP ; la normalisation
par 612 produit un chevauchement de deux ULP. Les 50 liens de cellules sont
vérifiés entre tokens, conversion et bundle. La conversion C# n'est donc
pas la cause initiale. Aucune réparation régionale locale n'a été appliquée
à cette table ; le point amont exact dans Docling reste non prouvé. Aucune
tolérance de géométrie ni modification des cellules n'est introduite.

Le canal typographique natif de cette page est distinct du canal raw natif
indisponible dans le lecteur : il contient 3 384 glyphes observés. Le titre
complet est corroboré par trois runs et une cible heading unique existe
sur la page physique 17. Le numéro imprimé 9 n'est pas corroboré par ces runs.
Le bloc cible est présent comme contexte, sans ancre ou chunk retrieval
direct ; ces références ne doivent pas être fabriquées. Un éventuel lien
additif doit porter ces qualifications plutôt que certifier une grille ou
une paire titre-numéro intégralement vérifiée.

La comparaison indépendante 20→25 vérifie l'égalité du contenu primaire des
dix documents : 104 pages, 2 312 blocs, 25 tables, 789 cellules, 373 chunks
et 373 ancres primaires. Les références locales à chaque cohorte sont
mappées explicitement, sans normaliser le texte ni les nombres. Les seules
différences de provenance canonique résiduelles sont trois durées SMS ;
l'égalité intégrale de toute provenance reste fausse. Les canaux auxiliaires
différents ne sont pas effacés pour fabriquer un résultat identique.

Le rejeu réel confirme aussi le correctif23 des remplissages implicites :
NIST p.40 conserve 158 primitives inspectées et 64 règles. Les deux refus
`unsupported_filled_shape` ont disparu, les 56 refus `clipping_path`
restent. La géométrie demeure partielle et la grille physique non approuvée.
Les observations natives décrivent des primitives inspectées, pas leur
visibilité peinte démontrée.

## Limite de granularité et essai de sondes25 conservé négatif

Le bundle25 NIST vérifié fait 9 377 836 octets avant gzip. Il conserve
96 696 glyphes sur 37 pages ; les 16 pages 38–53 refusent les runs avec
`native_typography_document_glyph_limit`. Les comptes refusés restent
inconnus. Cette limite du diagnostic typographique ne signifie pas que la
transcription primaire de ces pages manque. Une projection de stockage par
page est étudiée ; aucun plafond n'est relevé et aucun cache global ajouté.

Le loader25 a réellement chargé ses cinq composants et préparé quatre
méthodes de lecture/sondes dans un conteneur isolé sans réseau, sans SQL,
sans modèle et sans entrypoint backend. Le premier essai SQL OFF/ON porte
ensuite sur les sept blocs et 69 lignes de NIST p.40, aux mêmes plafonds
produit 14k/24k/256k. Le processus lecteur termine avec exit0 ; l'opérateur
refuse sa collecte avec exit2, phase `probe_collect`, et arrête la séquence
avant la recette. Le nettoyage observe cinq services arrêtés, zéro job
actif et aucun port publié.

Le readback hors ligne reproduit `instrumentation_residual_value_mismatch`.
Sur 66 résidus ON, la soustraction de durées presque égales amplifie l'écart
d'arrondi entre deux ordres de sommation. Les résidus enregistrés égalent
exactement parent moins somme enregistrée ; une contre-revue vérifie les
sommes avec une borne rationnelle de l'erreur float64. L'ancien vérificateur
évaluait sa tolérance sur le petit résidu. L'opérateur négatif et les 11
preuves rapatriées restent immuables. Un addendum numérique distinct doit
être testé et contre-relu avant de qualifier les mesures ; il ne modifie
ni les sondes produit, ni les nombres observés, ni un critère de fidélité.

L'addendum numérique séparé est ensuite qualifié : quinze contrôles incluent
les 66 témoins réels, le résidu zéro et des corruptions qui restent refusées.
Le CLI rehash les quinze fichiers de son gel et les onze pins de la capture,
reproduit le refus original puis valide les mesures hors ligne. Root rejoue
ce CLI dans un nouveau fichier ; la contre-revue du delta est favorable.
L'égalité des réponses OFF/ON et des digests de publication est vérifiée.
La collecte n'a perdu aucun événement et les seize phases ON sont couvertes.
Le verdict de l'opérateur historique reste faux ; ses contrôles de PDF et
configuration placés après la collecte n'ont pas été exécutés et restent
explicitement non qualifiés. Aucun nouveau SQL ou conteneur n'est relancé
pour cette relecture.

Les mesures NIST portent sur le lecteur25 appliqué aux données20, pas sur
un nouveau rejeu d'ingestion25. OFF dure 76,835 s, puis ON 76,306 s ; cet
ordre peut réchauffer les caches et ne mesure pas un surcoût non biaisé.
ON observe 143 décodages et 1 340 853 514 octets produits. Les phases codec
cumulées inclusives valent 4,082 s de décompression/copie, 2,463 s de SHA,
0,897 s d'UTF8, 15,934 s de JSON et 52,300 s de validation de bundle.
Le fetch et la matérialisation observés prennent 0,294 s. Ce ne sont pas
des temps CPU ou une mesure séparée du serveur SQL. Une projection dérivée
raw par page est étudiée pour conserver les validations tout en évitant
de relire tous les glyphes du document à chaque ligne ; elle n'est pas
encore implémentée. La mesure recette reste non exécutée à ce checkpoint.

Aucun appel payant, planning ou envoi de corpus à un fournisseur n'est
effectué par ces travaux. Le produit demeure **TESTE_NON_APPROUVE**.

Un diagnostic raster séparé, hors produit, réutilise dix petits PDF et leur
oracle analytique préexistant. Les quinze échantillonnages d'encre intérieure
préparés avant rendu concordent avec Poppler 26.07.0 à 288 dpi. Le cas B
ouvert confirme aussi une limite : un remplissage soutient des pixels sur
une fermeture virtuelle sans prouver un trait tracé. Le rendu du vrai NIST
p.40 trouve des pixels sombres sur tous les points échantillonnés des
64 anciennes lignes candidates figées, sans chercher ou déplacer leur axe.
Ce constat ne démontre ni l'attribution des pixels à chaque chemin, ni un
inventaire complet de règles, ni les relations de cellules. Aucun seuil
d'adoption, remplacement de l'observer ou changement de grille n'est fait.

## Lien de sommaire corroboré par le titre natif, Source26

Le projecteur conserve le refus de la géométrie de ligne chevauchante. Un
canal additif peut néanmoins relier un titre littéral natif complet à un
heading canonique exact et unique. Le repère commun, la source, la révision,
la page et les hashes du handle sont contrôlés. Les seules normalisations
sont les blancs ; chiffres, signes, accents et casse ne sont pas égalisés.
Les slices UTF16 sont entières et les baselines ne deviennent pas des boîtes
de glyphes. Aucun texte, cellule, point ou style primaire n'est modifié.

La contre-revue et ses témoins causaux ferment plusieurs faux positifs :
repère canonique non prouvé, préfixe de mot/titre, suffixe dans un autre run,
glyphe partiellement traversant et duplication d'une ligne source. Un glyphe
traversant ne peut être écarté du titre que par un propriétaire complet et
unique dans une autre cellule publiée de la même table et ligne, avec
texte natif intégral exactement égal au texte de cette cellule. Les autres
cas restent refusés, sans epsilon. Les preuves et métadonnées sont bornées
et ne sont jamais tronquées pour tenir dans le budget.

Les nouveaux liens sont ajoutés après les entrées ordinaires et legacy,
pour conserver leurs indices et IDs. L'API et le sérialiseur SQL partagent
la preuve facultative ; les champs supplémentaires sont absents lorsqu'elle
n'existe pas. Le numéro imprimé reste littéral et non corroboré par cette
preuve de titre ; il ne détermine pas la page cible par un offset supposé.

Qualification locale finale : 79/79 tests ciblés, dont 50 nouveaux, zéro
avertissement ; cinq PostgreSQL réels, dont deux nouveaux, sans skip et avec
cluster supprimé. Le rejeu détaché du cœur canonique utilise les 118 ancres
réellement capturées25 ; ce n'est ni un appel HTTP/SQL du nouveau produit,
ni un rejeu complet de publication. Les 33 anciennes entrées SQL/API restent
égales, dont 26 en p.5 et sept en p.6. Le lien ajouté à l'index33 part de
p.5/row12 vers le bloc heading de la page physique17, sans chunk/ancre cible
fabriqués. Il conserve trois slices, 1 616 octets de métadonnées et
`source_row_physical_overlap_observed`. La première tentative rejetée par
le segment du numéro9 reste conservée ; le numéro était présent dans un
autre ordre de rendu que les runs du titre.

La suite backend complète passe ensuite 3 473 tests, deux ignorés et aucun
échec, avec 2 060 sources identiques avant/après. Le gel d'auteur compte
huit fichiers possédés, six dépendances relues et 21 preuves rehashées par
root. L'entrée Linux prévue contient 575 fichiers et exactement six
changements de production depuis25 ; les 34 sources du sidecar demeurent
identiques. Construction et qualification Production26 restent à exécuter.

L'essai recette de sondes25 termine réellement avec exit0, opérateur positif,
23 blocs et 87 lignes, postchecks PDF/configuration et cleanup complets.
Le readback et la contre-revue confirment 183 décodages et 1 972 217 169
octets produits, avec 65,021 s inclusives de validation et 14,591 s de JSON.
Ces données20 sont lues par le lecteur25 ; elles ne recertifient pas
l'ingestion. L'ancien opérateur NIST négatif reste distinct de son addendum
de mesures validées. Aucun gain futur de stockage raw par page n'est encore
mesuré ; ce lot reste séparé de la navigation.

L'état demeure **TESTE_NON_APPROUVE**, sans fournisseur payant ni planning.

### 27 octobre 2026 — cible adaptée au serveur actuel

À la demande explicite de Maxime, la cible devient la meilleure ingestion
qualifiable avec le matériel existant. Les améliorations nécessitant un
serveur plus puissant seront qualifiées ultérieurement ; elles ne sont pas
un prérequis pour livrer les catégories de documents déjà maîtrisées. Cette
décision ne transforme pas les erreurs de formules ou de structure en succès.

Relevé en lecture seule à 12:25–12:26 Europe/Berlin : i7-9850H, six cœurs,
douze threads, 33 391 513 600 octets de RAM. Docling26 est volontairement
limité à deux CPU et 5 GiB, sans périphérique GPU attaché. Une Quadro T2000
Mobile est présente ; `nvidia-smi` refuse son initialisation NVML pour
incompatibilité entre pilote et bibliothèque. Aucune correction de pilote,
augmentation de quota, modification de service ou opération de redémarrage
n'est exécutée par ce relevé. La VRAM exploitable n'a pas été mesurée.

La mesure de charge intervient après l'arrêt des services26 : elle ne prouve
pas leur absence de saturation pendant ingestion. Le swap est presque plein,
mais les deux intervalles vmstat actuels ne montrent pas de pagination ;
cela ne démontre ni une pénurie RAM actuelle ni son absence pendant le run.
Les dix jobs26 sont toutefois `done`, une tentative chacun, sans LastError,
et les cinq conteneurs possédés n'ont aucun OOMKilled. Le contrôle de fidélité
global reste négatif ; il ne s'agit pas d'un arrêt matériel observé.

Priorités : terminer la lecture RAW par page et mesurer son gain Linux sur
les mêmes demandes, observer CPU/RAM/throttling pendant la prochaine campagne
possédée avant tout réglage de concurrence, préserver toutes les sources et
alertes, et qualifier la fidélité par catégorie de document. Les expériences
OCR lourdes, le parallélisme accru et l'extension de capture typographique
restent différables. Davantage de puissance ne prouve pas une meilleure
transcription à modèle, paramètres et pixels identiques.

Preuves : `artifacts/ingestion-fidelity-fixes-20261026/current-server-ingestion-target-v1.json`
et les deux observations privées rapatriées dans
`private/server-capacity-observations-v1/`. Aucun appel payant ni achat.

### 27 octobre 2026 — navigation26 réelle et lecture RAW par page

La construction26 puis l'ingestion réelle terminent entre 12:01 et12:15
Europe/Berlin. Dix jobs `done`, une tentative chacun, signature Production
vérifiée, aucun OOMKilled. Les cinq services possédés sont arrêtés et les
ports publiés absents. Les472 fichiers référencés de l'export ont été
rapatriés et rehashés. Le premier export a refusé l'absence du reçu root
`final-state.json` ; il n'a pas été assoupli. Root a réobservé les cinq CID,
leurs identités/ports et le dernier état des dix jobs, puis écrit ce reçu.
La seconde exportation réussit ; les premières sorties restent conservées.

La navigation NISTp5 possède réellement27 entrées. Les33 anciennes entrées
SQL sont identiques, hors leurs dates de création/mise à jour propres à la
nouvelle exécution ; les151 anciens objets API sont intégralement égaux.
L'unique ajout, index33, relie le titre sourcep5 à la page physique17 via
trois slices natifs. Le label imprimé9 reste non corroboré, l'avertissement
de chevauchement reste présent et chunk/ancre cibles restent null. Les
digests de publication avant/après capture concordent. Les quatre ensembles
d'échecs historiques du vérificateur demeurent identiques à25 ; ce succès
de navigation n'approuve ni le tableau NIST entier ni les formules.

Le nouveau stockage dérivé RAW ajoute la migration075 : catalogue courant
et pages d'une génération, liés à la source, au root canonique et au
manifeste. Publication et rotation utilisent la transaction du bundle et
son verrou existant ; la purge cascade. Aucun champ public ni capture
supplémentaire n'est ajouté. Les limites sont4MiB par page,32MiB par
document,1MiB par catalogue et2s de travail coopératif de préparation.
Un refus prévisible publie un reçu INLINE explicite, sans page partielle.

Reader et résolveur partagent un contexte interne de page et les mêmes
fonctions de lignes/Unicode/CRLF, SHA, LineId, provenance, qualité et budgets.
Le cache reste propre à l'appel, limité aux pages demandées avec les bornes
existantes64 documents/128MiB. Seules absence historique et raison INLINE
validée autorisent le chemin complet. Corruption, page attendue absente,
root périmé et rotation observée entre pages sont refusés.

Les tests causaux exposent l'ancienne route complète puis valident la
nouvelle : sur le cas RAW, zéro décodage de bundle et deux décodages page
pour Reader+résolveur. Les25 tests PostgreSQL intégrés passent, y compris
concurrence, rollback, purge, parité et corruptions. Deux défauts de revue
(géométrie autonome incomplète et ancien inventaire non contigu) ont reçu
six témoins RED puis correction. Les pages historiques1,4 ne sont pas
renumérotées ; elles utilisent un repli explicite compatible. Les deux
contre-revues finales n'ont aucun défaut matériel ouvert dans ce lot.

La suite backend root du27octobre à12:29–12:31 Europe/Berlin donne3524PASS,
zéro échec et deux skips préexistants. Les2075 sources demeurent stables,
fingerprint76697B64951D45DA0F1C17CED801C2FEBCA9D75E4A20A5411D4B0E92756EF474.
Les gels possédés comptent23 fichiers (stockageB11, lecteurC12), rehashés
par root. Les sondes opt-in ajoutent page_load/page_decode et
native_page_validate ; les durées sont inclusives et leurs bytes parent/
enfant ne s'additionnent pas. Ce lot local ne prouve pas encore son gain
de performance Linux sur le corpus réel : la construction27, sa publication
et ses lectures instrumentées neuves restent à qualifier.

### 27 octobre 2026 — publication27 réelle et lecture RAW qualifiée

Le lot RAW est poussé dans `f4f5604`. La construction Linux réelle27
réutilise une archive assainie de584 fichiers, source
`8ddd79a4226cfcb9f678414a76302a1dc8ab614a4267b237c1cd3e26d62218c0`.
La configuration Production est signée et vérifiée sur ce PC ; seule la
signature est transférée. Les dix documents terminent en une tentative
chacun, sans erreur de job. Le runner atteint `verification_completed`,
constate zéro job actif et arrête ses cinq conteneurs sans OOMKilled.
Le résultat global reste négatif sur les mêmes quatre catégories que26.
Il ne s'agit pas d'une approbation de fidélité générale.

La comparaison hors ligne26→27 conserve exactement le contenu primaire
des dix documents :104 pages,2312 blocs,25 tables,789 cellules,373 chunks
et373 ancres primaires. IDs canoniques et IDs bruts des chunks sont
identiques. Métadonnées mappées et qualité des chunks sont exactes.
Le canal provenance reste négatif9/10 : deux durées SMS diffèrent
(991→989 ms et1837→1798 ms), sans différence de contenu primaire.
Les canaux auxiliaires et leurs ancres ne sont égaux que4/10 ; ils ne
sont pas assimilés au primaire. Les captures ne permettent pas cette
comparaison pour les tables SQL complètes retrieval_units/contextualTexts.

La sonde compilée est chargée dans l'image réelle27, avec cinq composants
liés aux fichiers du backend. Le contrôle LOAD ne fait ni SQL ni mesure de
performance. Le paquet lié aux captures réelles est ensuite exécuté en
trois étapes séquentielles : métadonnées, NISTp40, recettep5. Une première
préparation locale refusée à cause de logs vides est préservée ; le paquet
BOUNDv2 les exclut, sans assouplir le vérificateur ni changer le portable.

La vérification SQL en lecture seule constate dix catalogues RAW, zéro
repli INLINE,104 états de page et zéro orphelin. Parmi ces états,47 ont
des observations natives et57 déclarent explicitement
`no_captured_native_observation`. Ce dernier nombre ne signifie pas une
perte de texte primaire et ne mesure pas la capture typographique.
Ce contrôle de métadonnées ne décode pas les104 payloads.

| Parcours réel27 | Wall OFF | Wall ON | Décodages de page ON | Octets décompressés ON |
|---|---:|---:|---:|---:|
| NISTp40,7 blocs/69 lignes | 0,797761 s | 0,6044165 s | 143 | 1197196 |
| Recettep5,23 blocs/87 lignes | 0,934018 s | 0,8327334 s | 183 | 3475902 |

Le texte, LineId, offsets, qualité et provenance des156 lignes sont
confrontés à l'inline de la même publication. La publication est inchangée
avant/après ; les résultats fonctionnels OFF/ON sont identiques. Un
contrôle complet par document précède les mesures. Dans les passes ON,
zéro décodage de bundle complet est observé ; les compteurs détaillés OFF
restent inconnus par conception. Les326 décodages de page produisent
4673098 octets, comptés uniquement sur les gzip enfants directs, sans
addition des octets parent/enfant. Quinze phases, zéro événement invalide,
perdu ou échec de collecte sur chaque cas. Les trois opérateurs attestent
l'arrêt du CID de sonde et des cinq services, zéro job et aucun port actif.

Les parités et calculs ont une relecture hors ligne indépendante de
l'exécution, plus une relecture d'auteur numérique explicitement désignée.
Ces temps incluent les comparaisons inline ; les durées sont inclusives,
pas du CPU exclusif. L'ordre OFF puis ON peut chauffer les caches. Aucun
pourcentage causal de gain contre25/data20, dont les bundles diffèrent,
ni surcoût non biaisé d'instrumentation n'est affirmé. Cette qualification
couvre les deux pages représentatives ; elle ne valide pas tout le wire
LLM ni toutes les structures documentaires.

L'observateur de ressources27 recueille287 rounds/1435 lignes sur environ
859 s, sans mesure indisponible. Docling atteint une médiane CPU198,14%
sous quota2CPU et4,195/5GiB de RAM échantillonnée. Les4523/6467 périodes
CFS throttled motivent une comparaison de profils ; le compteur763,765s
n'est pas du temps wall récupérable. Deux snapshots courts de l'hôte
montrent14,6–16,3Go disponibles et aucune pagination active dans leurs
intervalles ; ils ne prouvent pas l'absence de pression sur toute la
campagne. Swap consommé par conteneur et pics continus restent inconnus.

Preuves bornées :
`source27-raw-actual-offline-qualification-v1.json`,
`raw27-log-author-readback-b-v1.json`,
`primary-comparison-source26-source27-v1/receipt-v1.json`,
`source27-resource-actual-review-b-v1/review-result.json`, sous
`artifacts/ingestion-fidelity-fixes-20261026/`.
La capture RAW privée a44 pins ; SHA archive
`d7a2c508d0224d1db1a064babae1c1e7de81af7c1a6ef42b1506461d55d41abf`.
Le statut produit reste `TESTE_NON_APPROUVE`. Aucun appel payant ni
nouvelle inférence lourde ; la comparaison CPU28 est un essai séparé.

### 27 octobre 2026 — contrôle des104 payloads et choix du profil actuel

L'export SQL075 réel démarre seulement PostgreSQL27 possédé, effectue une
transaction repeatable-read/read-only puis le réarrête. Dix catalogues et
104 payloads sont récupérés :389684 octets d'enveloppe privée, sans clé ni
configuration. Les métadonnées avant/après concordent ;36 commandes Docker
sont comptées, zéro job actif et les cinq CID sont indépendamment constatés
arrêtés, sans port actif. Avant exécution, la revue a corrigé un défaut de
cleanup (une erreur du contrôle des jobs pouvait empêcher l'arrêt) et deux
valeurs NOEXEC conservées à tort dans le rapport réel. Les versions
refusées sont préservées ; le nouveau cleanup tente les trois étapes
indépendamment. Les témoins causaux puis23 tests passent.

Le contrôle hors ligne V3 des octets réellement exportés réussit pour
les10 catalogues/104 payloads : SHA, tailles, gzip, UTF8 strict, schéma,
identités/générations, inventaires et descripteurs. La projection est
structurellement exacte par rapport au canonique capturé27, avec736 blocs
natifs et2750 lignes de texte : ordre, raw, provenance, qualité, géométrie
et bits binary64. Les catalogues représentent29057 octets ; les pages,
677318 octets UTF8 et213551 octets gzip. Les47 états observed et57 états
unavailable sont tous vérifiés. Ce contrôle ne réexécute ni le projecteur
produit ni le Reader sur104 pages ; leurs156 lignes réellement lues dans
les deux sondes restent une preuve distincte.

La contre-revue a également exposé le token JSON numérique `-0`, perdu par
un json.loads standard avant la comparaison. L'addendum hors ligne V3 le
préserve et passe29 tests, dont le RED du parseur précédent et six témoins
numériques. L'export SQL V2 et ses octets restent inchangés. Le moteur
primaire historique possédait déjà ce parse_int et n'est pas modifié.

Le profil alternatif28 conserve source, images et modèles27 ; seuls
quota/threads/marge mémoire Docling passent de2CPU/2threads/5GiB à
3CPU/3threads/7GiB. Dix jobs terminent en une tentative, worker Production
signé et cleanup réel conforme. Le wall runner passe de855,573170 à
792,748878s, soit62,824292s de moins dans cette comparaison unique. Ce
n'est pas une mesure causale CPU seule : mémoire, threads, caches et
ordonnancement ne sont pas isolés.

Le texte/pages/ordre/checksums, les373 chunks et leurs métadonnées/qualités
restent exacts. Toutefois494 coordonnées de tables/figures sur six PDF et
48 coordonnées d'ancres NIST diffèrent. L'écart maximal observé est environ
2,619e-7 unité. Cette faible amplitude ne prouve ni une régression visible
ni une équivalence du rendu ; les points ne satisfont pas le critère
strict préenregistré. Le profil3/7 n'est donc pas adopté automatiquement.
Les confiances de provenance diffèrent aussi ; les quatre catégories
d'échec historiques restent identiques, sans reclassification d'oracle.

La référence retenue reste **2CPU/5GiB/threads2/OMP2**, avec NumThreads2
dans la configuration signée, et les optimisations RAW27 déjà qualifiées.
Les259 rounds de ressources28 n'ont ni indisponible, reset, restart ni
OOM observé. Docling atteint4,617GiB dans un prélèvement, contre4,195GiB
en27. Ces fenêtres ne couvrent pas tout le démarrage et ne donnent pas
les pics continus ni le swap consommé. Les snapshots courts ne montrent
pas de pagination active ; aucune absence de pression continue n'est
inférée. Aucun quota des services habituels n'a été modifié.

Deux fichiers opt-in reproduisent les seules ressources de référence :
`infra/docker-compose.ingestion-reference.yml` et
`infra/config/ingestion-reference.fragment.json`, copiés exactement du
profil audité (huit tests statiques). Ils n'activent aucun service ni
modèle et ne changent aucun défaut. Le premier se passe après le Compose
de base ; le second est un fragment à fusionner dans le document complet
**avant** nouvelle signature. Un NumThreads déjà signé différent prime
sur l'environnement. Le moteur doit également rester en mode CPU. Ce
profil ne reproduit pas toute la stack de qualification et ne constitue
pas un déploiement validé de la stack habituelle. La validation Compose
complète sur l'environnement cible et le raccord opt-in au rendu signé
restent à faire lors d'un futur déploiement ; l'installateur n'est pas
modifié dans cette mission.

Le contrôle des104 payloads est
`source27-raw-payload-exhaustive-qualification-v3.json`, SHA
`4ca36f03d023212d9b1084fd52590d4e7f99040a1e2e1d1acf3b096551532a89`.
La comparaison est
`primary-comparison-source27-source28-bound-v1/receipt-v1.json`.
Ces preuves restent sous `artifacts/ingestion-fidelity-fixes-20261026/`.
Le périmètre technique actuel est qualifié sur ce corpus ; les formules,
grilles/en-têtes/fusions non corroborés, ordre alternatif et limites
typographiques restent explicites. Un serveur plus puissant ne prouve
pas à lui seul leur fidélité. Le produit reste `TESTE_NON_APPROUVE` et
aucun planning ni LLM payant n'a été testé dans cette étape.

Le bilan public versionné, sans corpus ni configuration privée, est
`artifacts/validation/current-server-ingestion-20261027/qualification.json`.
Il sépare la qualification technique actuelle, les deux pages réellement
lues, l'intégrité/parité des104 payloads, le profil3/7 non adopté et les
limites documentaires. Il épingle les reçus locaux et les contre-revues.
Le lot final ajoute uniquement ce bilan, ce journal et les deux fichiers
opt-in de ressources ; aucun défaut applicatif ni installateur n'est
changé après les tests du code `f4f5604`.

### 27 octobre 2026 — parcours jusqu’au faux fournisseur qualifié

La nouvelle campagne ferme le contrôle du transport documentaire sans appel
payant. Deux publications réelles source27 (NIST et recettes) sont rejouées
dans un PostgreSQL16 Windows isolé, avec leurs identités, chunks, métadonnées
et artefacts canoniques vérifiés. Le repository produit republie les ancres
et lectures natives. Le runtime exerce réellement API, store, worker,
gateway, recherche SQL/canonique, outils et socket HTTP loopback. Il ne
refait ni l’OCR ni une ingestion Linux Production ; TEI/Qdrant sont des
adaptateurs déterministes, sans qualification de pertinence vectorielle.

Les régressions causales ont corrigé les coupures Unicode backend/client,
les espaces entre fragments Responses, les avertissements perdus dans les
tableaux/résumés et les omissions de projection non comptées. Les lectures
explicitement demandées les plus récentes passent avant les anciennes.
Le budget mesure le dernier échappement JSON ; les inventaires strictement
identiques utilisent des références explicites vers leur exemplaire courant,
avec conservation du contenu complet en cas de divergence. Les lots RAW
non observés sont refusés avant exécution puis peuvent être corrigés,
également dans la voie JSON historique. Les refus Chat conservent leur trace.

Une preuve réelle supplémentaire a révélé un curseur Reader à3 alors que
la projection partielle ne livrait qu’une ligne et annonçait la reprise à2.
Le prompt aligne désormais les deux curseurs sur le préfixe effectivement
admis, sans modifier le Reader, les textes, références ou qualités. La
lecture initiale offset1/limit2 reste inchangée ; le faux fournisseur suit
seulement le curseur observé pour récupérer la dernière des trois lignes
obligatoires. Les budgets14000/7000 ne sont pas augmentés. Le maximum final
observé est13147 caractères documentaires.

Les six campagnes de la DLL Release finale passent : positifs40POST,
RAW erroné puis corrigé44POST et citations inventées44POST, deux documents
sur chacun des protocoles Chat Completions/Responses. Soit12cas/128POST,
avec égalité des octets reçus, traces applicatives et sondes, références,
qualité/provenance et résultats SQL/API. Les mauvaises citations restent
refusées, sans réponse persistée. Les diagnostics n’ont aucun compteur
perdu ou tronqué. Tous les environnements possédés sont arrêtés.

Le vérificateur est éprouvé sur copies :14/14 corruptions rejetées au motif
exact sur les deux protocoles, originaux inchangés. Qualité, référence et
arguments sont aussi altérés après recalcul des liaisons SHA/trace/sonde,
afin d’atteindre les contrôles profonds. Les défauts d’outillage rencontrés
(copie facultative exigée à tort, chemins Windows longs, faux fournisseur
qui ne poursuivait pas la pagination) sont corrigés sans assouplir le
critère des trois lignes ; les anciens refus restent conservés.

Validation finale :3609 tests backend,2485 client,82 cœur client et60
contrats réussis, soit6236 unitaires sans échec et deux exclusions
(fichiers Linux et OCR opt-in). Les959 sources client/contrats du lot
commun déjà vert sont revérifiées identiques. La suite PostgreSQL complète
passe418 tests, zéro échec et deux anciens oracles explicitement exclus.
Le cluster temporaire est supprimé, son port fermé, l’environnement
restauré et le listener habituel5432 inchangé. WinUI production Release
x64 compile sans options de test, sans avertissement ni erreur, sources
stables. Les649 tests ciblés fournisseur/table sont inclus dans les3609 ;
ils ne s’ajoutent pas au total. Harnais37 tests Python et5 cas PowerShell,
helper de mutations7 tests dont un vrai chemin Windows long.

Le backend Release final a le SHA
`4e579fea1447697f38d5a09a17231d4d15add7af4bee4f02a8c0deddc9a40306`.
Les builds ont été exécutés sur un arbre modifié depuis `d5482db` : les
inventaires de sources et SHA des DLL identifient le code testé, pas le
seul commit de base. Gel produit v7 :
`9b19f6b770b4b350d8ec72cad1094d8d5300bc094db571cd867ddf40292850b2` ;
gel harnais v8 :
`95e1b202681c1d5a587f2a6b34e851ba15ce86eb655969b06fba8ca17ceeb8ff`.
Les quatre refus courts antérieurs (source falsifiée et sortie interrompue,
huit cas/huitPOST) restent attribués à leur DLL v3 et ne sont pas renommés
en tests de la DLL finale.

Le bilan lisible et le reçu public épinglant les preuves privées sont dans
`artifacts/validation/model-wire-qualification-20261027/`. Les corps HTTP,
corpus, clés et configurations privées ne sont pas versionnés. Cette
qualification démontre ces parcours contrôlés, sans garantir tous les
PDF, tous les défauts possibles de l’oracle, la pertinence dense, une
nouvelle visite interactive WinUI ou le raisonnement d’un modèle réel.
Elle ne prouve pas la récupération d’une fin de chunk invisible au-delà
de2400 caractères sur ces deux chunks courts. Les limites documentaires
antérieures restent explicites et le produit reste `TESTE_NON_APPROUVE`.
Le planning et la qualité sémantique seront évalués séparément avec un
vrai modèle. Rapports Telegram delta28 puis29 envoyés avec reçus locaux ;
coût fournisseur de cette campagne :0.


## 2026-10-29 — Reprise des corrections après le bilan Telegram 58

Le bilan 58 a été envoyé en six parties ; il ne suspendait pas la mission. La reprise travaille hors ligne sur les défauts observés du second essai Luna : contexte initial peu utile, réserve à97/128 puis128/128 qui retire la recherche, doublon exact carte/chunk, quatre choix répétés cinq jours et condition du taboulé perdue. Les preuves fermées paid2 (95fichiers) et les audits canoniques/descriptifs restent dans artifacts/meal-resume-20261029/. Le planning reste TESTE_NON_APPROUVE.

Le lot initial écrit mutualise une politique de synthèse (réutilisation permise sans répétition imposée, alternatives utiles sans quota distinct artificiel, conditions/prérequis de la source conservés) entre les rôles. La réparation Writer est extraite dans un fichier dédié ; sa sonde llm.writer_repair distingue motif de validation, budget disponible, réservation du Critic, réparation réussie/refusée/en échec, sans corps privé. Le motif exact rejoint le prompt de réparation.

Validation ciblée locale : quality-repair-targeted-v2/manifest.json,42tests passés,0échec,0ignoré ; compilation effectuée, empreinte des sources identique avant/après. Le lot comprend11nouveaux cas et les régressions connexes. La V1 reste conservée : deux assertions de texte existantes signalaient la disparition du terme repeatable dans le prompt agent ; la formulation a été précisée avant le rejeu V2. Ces tests valident les contrats et sondes, pas le raisonnement d'un modèle réel.

En cours, non encore validé : ensemble actif borné à128 avec protection des preuves utilisées, rotation et reconstruction durable au retry ; priorisation des corps utiles et alternatives avant copies textuelles de même provenance. Aucun nouvel appel API ni changement serveur depuis le bilan58. Cumul estimé du journal fermé0,05030316USD,0réservation ouverte, sans vérification de facture/solde. Rapport delta59 envoyé avec reçu local ; prochain envoi horaire après22:31UTC si nouveauté utile.

La rotation et son rejeu durable sont maintenant écrits : les preuves encore utilisées sont protégées, le plafond actif reste128, et les résultats d'outils committent uniquement les références canoniques de l'ensemble actif. Les anciens historiques sans instantané conservent leur traitement. Les tests PostgreSQL de persistance et reprise sont écrits mais ne sont pas encore exécutés sur une base réelle à ce point du journal. Les refus locaux de capacité ne ferment plus à tort toutes les opérations documentaires restantes.

Les campagnes ciblées élargies V1/V2 ont révélé des attentes de pagination devenues inadaptées et un défaut réel distinct : une grille pouvait relever implicitement le budget configuré, tandis que le contrôle final ne couvrait pas les collections purement textuelles. Le plafond est désormais strict ; l'admission mesure le bloc documentaire sérialisé, métadonnées et tour d'outils compris, dans tous les modes. Le refus final précède réservation et HTTP. V3 :814tests réussis,2échecs sur816, sources stables ; les deux échecs attendent à tort une fenêtre complète de6000caractères sous8000 avec ses métadonnées et son résultat d'outil. La fixture distingue maintenant une fenêtre de4000 qui tient entièrement et6000 qui nécessite une reprise avec le curseur réellement transmis. Son rejeu reste à faire ; les anciens échecs sont conservés.

Le rejeu privé des97preuves canoniques reconstruites est réellement exécuté sur la DLL V3. Tous les identifiants sont conservés et le classement change, sans démontrer la pertinence métier. Le corps enregistré du premier Writer contient34520caractères documentaires sur65536, dont6621pour les champs de texte et27899pour le reste. Les objets SourceOverview complets d'origine n'ayant pas été archivés, ce rejeu ne qualifie ni l'admission finale contrefactuelle ni la qualité d'un nouveau modèle. Un nouveau parcours complet gratuit reste nécessaire avant l'essai payant.

Les contrôles d'architecture et17contrôles comportementaux des sondes passent. L'inventaire demandé par l'utilisateur porte sur1236fichiers texte de production,328094lignes physiques et1132929mots approximatifs à22:05UTC, hors tests, outils de développement, dépendances et généré ; un recomptage indépendant des corps et des empreintes concorde. Cet instantané physique ne prouve pas que chaque méthode est utilisée. Aucun appel payant ni changement serveur n'a été effectué pendant ces corrections.

Après cette analyse, V4 passe822tests ciblés sans échec ni exclusion. Quatre nouveaux cas isolent le refus final primaire (evidence/observations, Chat/Responses) avec un vrai garde budgétaire temporaire : aucun HTTP, aucune réservation durable, diagnostic content-free. Les fenêtres4000/6000 vérifient respectivement le corps complet avec préfixe borné et le corps partiel sans ancien préfixe, avec reprise exacte et canonique inchangé. La suite unitaire complète passe6479tests :3835backend,2492client,82cœur client,70contrats ; deux exclusions opt-in explicites (fichiers Linux, OCR réel). Sources avant/après identiques pour V4 et la suite complète. Les822cas sont inclus dans ce total, ils ne s'y ajoutent pas. La campagne PostgreSQL isolée réelle est lancée sur ces mêmes DLL compilées ; son résultat est encore attendu.

PostgreSQL est ensuite validé : 423 tests réussis, aucun échec, deux anciens oracles explicitement ignorés. Les deux nouveaux cas JSONB passent réellement, pour l'historique ancien et la rotation, avec expiration du bail puis reconstruction depuis les événements persistés. Les empreintes des DLL correspondent à la compilation unitaire précédente ; les sources sont identiques avant/après. Le cluster possédé est supprimé, son port fermé, le secret de démarrage retiré et l'environnement restauré ; aucune modification du cluster habituel. Cela qualifie la persistance et la reprise testées, pas un redémarrage du processus Linux ni la qualité sémantique du planning. Rapport Telegram 60 envoyé en une partie à22:29UTC, reçu réussi conservé ; les opérations continuent.

Le commit `0b2ce0ac40fd66e9cbbac2f7501a266a3fa02bfa` est poussé ; GitHub Actions40966967712 est terminé avec succès. Le backend Linux est reconstruit à partir des603fichiers de production inventoriés, puis ses six métadonnées de build, l'image et la DLL sont revérifiées au retour. La discussion fictive du deuxième essai payant est supprimée une seule fois par l'API normale ; GET404, cascades et audit+1 sont vérifiés. Une capture indépendante conserve les32 tables documentaires,77 migrations et4PDF/510pages ; archive fermée de29fichiers revérifiée intégralement. Aucun SQL d'écriture direct ni appel fournisseur pour ce nettoyage.

Le premier démarrage gratuit de la nouvelle DLL échoue avant toute soumission : `UnauthorizedAccessException` sur la configuration signée copiée en mode0600, propriétaire1000, alors que le backend utilise UID0 avec toutes ses capacités retirées. Le code139 n'est pas un OOM ; les deux applications sont arrêtées. Le correctif de préparation touche exactement9fichiers copiés et4dossiers neufs : ACL de lecture/écriture UID0 et héritage privé sur les seuls deux dossiers de traces, pour leur collecte par UID1000. Aucun contenu de fichier, clé payante, ancien environnement ou corpus n'est modifié. Un premier garde trop sensible à l'ordre des objets Mounts refuse avant mutation ; V2 compare les objets complets triés par destination unique, sans changer le contrôle intégral HostConfig.

À23:19UTC, le correctif ACLV2 et son vrai probe Docker sans réseau passent :12empreintes lues sous UID0/cap-dropALL, trois créations exclusives puis retrait, deux relectures des sentinelles de traces par l'opérateur1000 ; dossiers de traces et ledger gratuit à nouveau vides. Les13chemins, propriétaires, inodes, modes et empreintes sont revérifiés sur PC. Les applications restent arrêtées ; redémarrage dédié et qualification des11échanges du faux fournisseur restent à effectuer. Les préparations du faux fournisseur, de l'inspecteurV3 et du readback ne constituent pas une qualification réelle anticipée. Rapport61 envoyé avec succès en une partie à23:23UTC ; aucun nouvel appel payant, dernier cumul fermé estimé0,05030316USD. Le planning reste `TESTE_NON_APPROUVE`.


## 2026-10-30 — Qualification gratuite réelle et nouveau défaut de navigation

Après correction du garde sur le seul verrou vide du ledger gratuit (0644 créé réellement, ancien oracle0660), le runtime Linux possède les bonnes images/DLL, readiness et trois contrôles TLS. Aucun changement du ledger payant. Le faux fournisseur V2 réalise onze échanges et seize opérations documentaires réussies ; le STOP final HTTP400 est volontaire. L’inspecteur V3 vérifie177comparaisons canoniques visibles,209identifiants distincts admis,128maximum actifs et82retraits, avec instantanés durables, références protégées et poursuite read/find/search après saturation. Les95fichiers fermés sont tous vérifiés sur PC avant et après extraction. Ces résultats ne valent ni qualification RAW, ni redémarrage Linux, ni réponse métier.

L’audit indépendant des vrais prompts trouve un blocage amont avant même la première rotation : à123/128, quatre sources ont encore des preuves actives, mais le dernier corps lu domine la projection ; les trois enums natifs et le parser ne reconnaissent alors que cette source visible. Les trois autres sources sont actives mais inaccessibles au modèle. Le premier Writer reçoit20preuves complètes sur97 (10435caractères de contenu), contre10dans l’ancien essai ; il manque néanmoins un catalogue de navigation indépendant des corps affichés. Les noms de documents sont également absents au premier Writer. Le nouveau correctif reste en cours : catalogue léger de sources actives revalidées, mêmes handles dans schéma/parser, noms seuls pour orientation, budget strict incluant ce catalogue, aucune citation autorisée depuis un corps invisible.

Le faux fournisseur V2 avait aussi envoyé six includeVisualSources:null alors que le schéma déclare un booléen ; le parser les avait acceptés comme false. Le test gratuit établit donc l’exécution/fidélité observée, pas la conformité stricte des arguments. Une V3 et un inspecteur V4 sont préparés : false réel, validation du schéma et retour vers une autre source initialement vue dont les corps ne sont plus affichés. Aucun essai payant avant ce contre-test et les contrôles du correctif.

La seule session fictive W1 est supprimée une fois par l’API normale : GET404, cascades et audit+1 réussis, paire backend/faux fournisseur arrêtée, source34 inchangée. Capture SQL32 après nettoyage terminée à00:05UTC,11mesures,23591octets, pic mémoire observé997773312octets sous1GiB, aucun OOM/redémarrage. Comparaison finale et archive du nettoyage encore en cours à ce point. Aucun nouveau crédit dépensé ; dernier cumul fermé estimé0,05030316USD et aucune réservation ouverte dans ce journal fermé. Produit toujours TESTE_NON_APPROUVE.

Le nettoyage W1 est fermé à00:10UTC :32 tables,77 migrations,4PDF/510pages inchangés ; zéro tâche/session/message et aucun OOM ni redémarrage PG. Le reçu réel de suppression, la capture après et leurs preuves sont fermés dans34fichiers, tous revérifiés sur PC avant et après extraction. La lecture indépendante du closer ne trouve pas de défaut matériel ; celui-ci ne lit que des fichiers et ne contacte aucun service. Les applications possédées demeurent arrêtées. Le code du catalogue est en validation locale, sans nouvel appel payant.

Le catalogue actif F1 est validé localement : 3 845 tests backend réussis, aucun échec et deux exclusions explicites (OCR réel et sensibilité Linux sous Windows). Les 832 cas ciblés sont inclus dans ce total. La compilation fraîche V6 et le test complet V7 ont les mêmes empreintes de sources et d’assemblages ; 18 fichiers produit/tests sont gelés et revérifiés par root. Quatre sources actives avec une seule visible conservent leurs handles dans les outils et le parser ; les sources retirées, inconnues ou ambiguës sont refusées. Un carrier affiché au catalogue sans son corps reste interdit comme citation finale. Aucun plafond de production n’est relevé.

Les régressions rouges restent conservées : anciennes attentes interdisant même le basename, nombreuses sources distinctes sous 8 k, coutures natives qui ne réservaient pas le vrai tour d’outils. Les six frontières de qualité synthétiques conservent un refus explicite à 14 k et un succès intégral à 20 k, avec dix codes de qualité, huit descripteurs, lignes et empreintes inchangés ; taille réellement mesurée entre 14 892 et 14 935 caractères sérialisés selon le protocole. Les neuf codes du précédent cas réel Moulinex ne sont pas renommés en dix. Architecture et 17 contrôles comportementaux des sondes passent. Faux fournisseur V3 et inspecteur V5 passent 48 tests purs : l’égalité catalogue/sources SQL actives est exigée, même pour une source jamais visible ; les anciennes versions restent gelées. Tout cela ne qualifie pas encore le nouveau runtime Linux ni le planning métier.

Rapport Telegram 62 envoyé avec succès en une partie à 00:23:35 UTC, reçu local conservé. Le staging V5 est créé avec les onze fichiers signés/TLS/faux accès identiques ; aucune clé privée de signature ni clé OpenAI n’est copiée. Les trois nouveaux dossiers de traces/ledger gratuit attendent encore leur qualification ACL et leur probe sans réseau avant tout démarrage. Aucun nouvel appel payant.

Le commit catalogue `c21e9201764701f00b2cc42ec01cd60a91ec683c` est poussé ; GitHub Actions 40979821187 est terminé avec succès. La nouvelle image Linux V6 est construite depuis les 604 fichiers de production, puis ses métadonnées, image et DLL sont revérifiées au retour sur PC. Les ACL V3 sont qualifiées sans réseau : neuf lectures exactes, trois écritures de probe exclusives ensuite retirées, treize cibles bornées. Le premier démarrage W2 refuse avant mutation parce qu'un sous-dossier de capture vide existe déjà ; seul ce dossier vide possédé est retiré après vérification. Le même contrôleur réussit ensuite à 00:38 UTC : configuration signée, DLL, TLS, readiness et limites de ressources conformes, source34 inchangée. Aucun appel fournisseur réel.

Le second parcours gratuit W2 soumet une seule tâche fictive, sans retry, et s'arrête volontairement au onzième échange HTTP. Un premier readback refuse la représentation textuelle de `createdAtUtc` : `.292220` envoyé devient `.29222` en SQL, soit exactement le même instant. L'oracle V4 compare désormais cet unique champ à 100 ns sans flottant ; tous les autres champs restent stricts. Ses 29 tests et une contrelecture indépendante passent, y compris le refus d'un tick de différence. Le readback réel V4 passe à 00:51 UTC : onze traces, deux flux de logs complets, zéro écriture SQL directe, aucune clé fournisseur ni contenu de ledger lu.

L'archive W2 ferme 95 fichiers, 27 811 525 octets de preuves ; toutes les tailles et empreintes sont vérifiées avant puis après extraction sur PC. L'inspecteur V5 est réellement exécuté sous neuf gardes réseau/processus : PASS pour les onze corps HTTP, 170 fenêtres documentaires canoniques, 210 identifiants distincts admis, 84 retraits de transition et plafond actif 128 respecté. Quatre sources actives restent exactement présentes au catalogue ; le dixième échange relit la page 10 d'un autre PDF dont aucun corps n'est alors affiché, et trois extraits reviennent dans le onzième Writer, identiques aux données canoniques. Une contrelecture indépendante directement depuis le tar confirme ces faits. Cela ne valide ni une décision du modèle réel ni le planning, le RAW ou un redémarrage de processus. Produit toujours TESTE_NON_APPROUVE.

La capture SQL32 avant nettoyage W2 passe à 00:59 UTC : 12 mesures, 23 860 octets, pic mémoire observé 998 682 624 octets sous 1 GiB, aucune nouvelle erreur OOM, aucun redémarrage PostgreSQL ni session SQL résiduelle. Sa comparaison documentaire et le nettoyage de la seule session fictive restent à fermer avant le nouvel essai payant. Aucun crédit supplémentaire dépensé ; le dernier cumul estimé fermé demeure 0,05030316 USD, distinct du solde fournisseur.

La conservation W2 avant nettoyage passe réellement :32 tables,77 migrations,4PDF/510pages inchangés. La seule session fictive est supprimée une fois par l'API normale ; GET404, cascades, audit+1 et arrêt de la paire possédée sont vérifiés. Capture SQL32 après nettoyage à01:05:49UTC,11mesures,23591octets, pic999931904octets sous1GiB, aucun OOM/redémarrage. Le closer fichiers V2 passe et ferme34fichiers, tous revérifiés dans le tar et sur PC. Aucune clé fournisseur lue ni appel payant pour ces opérations. Le manifeste du runtime payé est contrelu sur ses15références réelles ; les corps du ledger sont identiques au dernier snapshot fermé, six anciennes charges et aucune réservation ouverte.

Le nouveau runtime payé démarre réellement à01:11UTC et passe les contrôles de signature, DLL, image, readiness et ressources ; le faux fournisseur reste arrêté. Une seule tâche réelle est lancée à01:15:49UTC avec 5.6 Luna, sans retry, dans le plafond cumulatif0,50USD. Résultat :succeeded/answered,20claims,7références,4appels,57358tokens d'entrée et2650de sortie,39718ms mesurés par le résultat. Coût estimé0,0334478USD. Le journal fermé contient dix charges, dont quatre nouvelles ; ses six anciennes lignes sont inchangées octet pour octet, zéro réservation ouverte. Cumul estimé0,08375096USD, distinct du solde/facture OpenAI. Aucun autre appel payé n'est lancé après cette tâche.

Le readback réel V4 payé passe :quatre traces, deux flux de logs complets, SQL en lecture seule, source34 inchangée. Capture SQL32 à01:24:03UTC,12mesures,28174octets, pic998334464octets sous1GiB, aucun OOM/redémarrage. La comparaison conserve32 tables,77 migrations,4PDF/510pages ; une tâche et une session fictive restent présentes. Un SHA de capture mal recopié dans le closer V1 est refusé avant création de sortie ; V1 est conservé et V2 ne change que ce littéral, dérivé automatiquement du vrai fichier. V2 passe réellement sous gardes réseau/processus. Le stderr SQL vide est conservé et copié à l'identique sous un suffixe .log admis par le collecteur, sans assouplir ses règles.

La fermeture V5 conserve81fichiers et toutes les requêtes de polling, avec quatre traces originales et les corps des dix charges/réservations. Archive et extraction sont revérifiées sur PC. L'inspection V5 réelle passe sans anomalie sous neuf gardes :cohérence des sondes, compteurs, catalogue actif et budget. Ce sont les sérialisations backend, sans interception indépendante des HTTP OpenAI. L'évaluation mécanique distincte compare ensuite le worker qualifié, le corpus SQL complet, les corps effectivement visibles dans l'entrée finale, les citations et les20coordonnées :PASS. Elle constate sept recettes distinctes et treize réutilisations ; les originaux capturés ne sont pas réécrits. Ces contrôles ne constituent pas l'approbation métier.

La revue de contenu indépendante confirme les sept propositions dans trois PDF, sans référence superflue ni chiffre culinaire ajouté. Qualité professionnelle non approuvée :même porridge cinq matins et deux soupes dans sept des dix repas principaux. La demande n'exigeait pas vingt recettes différentes ; aucun quota de diversité nouveau n'est inventé. Rapport Telegram63 envoyé avec succès à01:22:22UTC, une partie, reçu conservé. Le produit demeure TESTE_NON_APPROUVE.

L'audit causal des quatre vraies traces trouve ensuite une clôture prématurée de la recherche :planner,writer,writer-research-followup-1,synthesis-recovery ; aucun Critic, désactivé dans ce profil. Le Writer demande quatre recherches, puis signale une insuffisance après leurs résultats. Il reste alors treize appels modèle, cinquante-deux outils et du temps documentaire, avec researchAllowed=true. ResearchContinuation refuse pourtant la revue d'insuffisance parce qu'une recherche a déjà eu lieu ; le fournisseur bascule vers un completer sans outils sur douze extraits et autorise leur réutilisation pour remplir les cases. La politique existante favorise déjà les alternatives utiles ; le code n'impose pas sept recettes. Un indice de pancake adapté au petit-déjeuner est visible, sans lecture ciblée demandée. Correction ciblée autorisée :permettre l'unique revue bornée d'insuffisance après recherche aussi, dans les budgets et avec un locator déclaré ; préserver les gardes anti-duplicats et l'insuffisance honnête après cette revue. Implémentation et régressions gratuites en cours, aucun verdict anticipé ni nouvelle dépense.

La correction ne change que la transition de revue d'insuffisance : elle devient admissible après une recherche partielle, dans les mêmes limites et une seule fois pour cette synthèse. Trois sondes sans contenu privé précisent la recherche déjà effectuée et les locators disponibles. Le contre-test causal V2 compile réellement l'ancien code puis échoue dans les trois variantes attendues ; les octets corrigés sont restaurés et revérifiés. Les onze nouveaux cas passent ensuite. Le premier montage RED V1 échouait à compiler deux définitions : cette erreur de harnais est conservée et exclue des preuves causales.

La première suite complète révèle deux queues simulées CandidateInventory incomplètes après l'ouverture attendue de cette revue :3854PASS,2FAIL,2ignorés explicites. Une réponse d'insuffisance simulée est ajoutée à chacune, sans réduire budgets ni assertions ; les nouveaux contrôles exigent une revue unique, quatre/cinq appels exacts, le même inventaire et aucun completer fermé. La seconde suite complète recompilée passe :3856PASS,0FAIL,2ignorés explicites sur3858cas. Architecture PASS et17contrôles comportementaux de sondes PASS. Ces preuves gratuites concernent l'orchestration ; elles ne prouvent pas une amélioration autonome du planning.

Après revue indépendante et18tests purs du nettoyeur, root supprime une seule fois la discussion fictive paid4 par l'API normale :GET404,cascades et audit+1 confirmés, seul backend possédé arrêté, fake déjà arrêté préservé et source34 inchangée. Le journal actif des coûts n'est ni lu ni modifié par ce nettoyage. La capture SQL32 après nettoyage passe à02:05:04UTC :11mesures,23591octets,pic1000108032octets sous1GiB,aucun OOM/redémarrage ni session résiduelle. La comparaison documentaire finale de cette capture reste à fermer. Aucun nouvel appel payant ; cumul estimé fermé inchangé0,08375096USD,produit TESTE_NON_APPROUVE.

### 2026-10-30 — revue après recherche qualifiée, nouvel essai réel payé5

Le commit `ce9834e530abbbd3f6d976ea9ffbe34792e56a3c` est poussé et GitHub Actions40989814930 est terminé avec succès. Le build Linux7 est reconstruit et revérifié :604fichiers de production, une seule différence produit par rapport au build6, dans ResearchContinuation. La DLL et l'image exécutées correspondent aux métadonnées du build. La comparaison après nettoyage paid4 est également fermée :32 tables,77 migrations,4PDF/510pages inchangés et aucun job restant.

Le parcours gratuit W3 passe réellement sur cette DLL. Cinq requêtes capturées prouvent la séquence recherche partielle, insuffisance, revue avec outils puis lecture d'un autre document. Les dix opérations documentaires et73fenêtres canoniques visibles sont vérifiées ;73ne désigne pas un nombre de pages PDF distinctes. La cinquième réponse400 est l'arrêt intentionnel du faux fournisseur, vérifié par son corps exact. Les premiers inspecteurs V1/V2 sont conservés comme échecs d'oracle : préfixe Planner historique admissible, puis normalisation LF de la requête de recherche telle qu'effectuée par le code C#. V3 passe indépendamment et à l'identique sur PC, sans modifier les captures ni relâcher les identités, pages, corps ou empreintes. L'archive de61fichiers est revérifiée. Cette preuve ne valide pas la qualité métier ni une nouvelle rotation ou un redémarrage du processus.

La discussion fictive W3 est supprimée une seule fois par l'API normale, avecGET404,cascades et audit+1. Les deux applications possédées sont arrêtées et source34 est inchangée. La capture et la comparaison SQL32 après nettoyage passent :32 tables,77 migrations,4versions/510pages inchangés ; zéro job, session, message, ingestion, OOM ou redémarrage PostgreSQL. L'horodatage réel de capture03:00:02UTC est conservé séparément de celui de la comparaison.

Un nouvel essai payé5 est ensuite lancé à03:10:57UTC avec la même question, le même corpus et5.6Luna, sans retry ni nouveau plafond. Résultat :succeeded/answered,20claims,8objets sélectionnés,2appels,23812tokens d'entrée et2529de sortie. Le coût mesuré est0,0164576USD ; le cumul est0,10020856USD sur0,50USD, avec12charges et12réservations, aucune ouverte. Les dix anciennes lignes sont conservées comme préfixe exact des fichiers de coûts. Ces chiffres ne sont pas une vérification du solde ou de la facture OpenAI.

Le readback réel conserve les deux traces, les deux flux complets de logs, les événements SQL et le corpus canonique. La fermeture conserve49fichiers ; archive et extraction sont revérifiées, puis inspection et évaluation mécanique passent. Le contrôle compare notamment les corps visibles, le worker qualifié, les citations et les20coordonnées. La comparaison SQL32 payé5 passe également : documents inchangés, une seule tâche et une seule session fictive exactes, aucune ingestion/OOM/reprise PostgreSQL.

La qualité professionnelle reste non approuvée : même porridge cinq matins, rubriques génériques et composants proposés comme collations, répétitions importantes. Aucun quota nouveau de vingt recettes distinctes n'est ajouté. Le Writer a répondu directement sans recherche complémentaire ; la correction de revue d'insuffisance n'a donc pas été exercée par ce nouvel essai. Son entrée réelle comportait les quatre outils de recherche,19observations et la politique de qualité existante. La configuration de campagne désactive le Critic, alors que le produit l'active par défaut. L'audit des consignes, de la sélection initiale et de cette différence de configuration se poursuit avant toute nouvelle dépense. Telegram65 envoyé avec succès à03:18:02UTC, une partie, reçu conservé. TESTE_NON_APPROUVE maintenu.

### 2026-10-30 — contre-épreuve Critic et cohérence du contrat de sélection

La discussion fictive payé5 est supprimée normalement :DELETE unique,audit+1,cascades absentes,backend possédé arrêté,faux fournisseur conservé arrêté. La comparaison SQL32 après nettoyage conserve32 tables,77 migrations,4versions/510pages ; aucun job,session,message,ingestion,OOM ou redémarrage. Les anciennes configurations sont sauvegardées sur PC et serveur. Une seule option change dans les deux phases signées :SemanticCriticEnabled passe de false à true. Les autres champs privés et publics,question,modèle,prix,corpus,plafonds,profil5,image et DLL restent identiques. L'installation conserve les ACL et les deux applications restent arrêtées jusqu'au démarrage contrôlé. Cette transition ne revendique aucune qualification gratuite nouvelle de la branche Critic.

L'essai payé6 est lancé à03:39:57UTC. Il se termine succeeded mais insufficient_documentation,avec zéro claim et aucun planning. Les cinq appels réels sont Planner,Writer,writer-research-followup-1,writer-insufficiency-review,Critic ; aucun completer ultérieur. Le Writer demande quatre nouvelles recherches générales,puis exige à tort des repas distincts. La revue d'insuffisance corrigée est effectivement atteinte,mais ne demande pas de lecture complémentaire ; le Critic confirme ensuite l'insuffisance. Les outils,les locators de source,la rotation et les budgets restent ouverts. Cela ne démontre ni l'impossibilité du planning ni l'incapacité du modèle.

Coût payé6 :0,04300614USD pour cinq charges. Le journal ferme17charges et17réservations,zéro réservation ouverte,cumul0,14321470USD sur0,50USD. Les douze anciennes lignes sont conservées comme préfixe exact. Archive49fichiers revérifiée et inspection V7 réelle PASS. Un contrôle séparé des cinq entrées vérifie51fenêtres documentaires avec les fonctions canoniques existantes :corps,identités,pages,qualité déclarée,troncatures et curseurs concordent avec le worker qualifié et le corpus SQL. Aucune évaluation de grille20cases n'est inventée :elle est inapplicable à cette réponse sans proposition. Les documents restent inchangés,et la discussion fictive payé6 est ensuite supprimée une seule fois avec audit+1,cascades vérifiées et arrêt des seules applications possédées. La comparaison après nettoyage confirme à nouveau32/77/4/510 et zéro job restant.

L'audit trouve des consignes contradictoires concrètes :certains prompts de revue,relecture et réparation exigent des candidats distincts indépendamment du contrat qui autorise le réemploi. Ces phrases sont conditionnées à une demande explicite de distinction ; aucune exigence de diversité ni heuristique de repas n'est ajoutée. Le passage dossier-ready reste inchangé,car son appel est déjà réservé au mode distinct. Les tests compilent réellement les anciennes consignes contre les nouvelles régressions :six échecs attendus et un succès,puis sept succès après restauration exacte des sources corrigées. Revue indépendante,architecture2109sources et17contrôles comportementaux des sondes PASS.

Le premier appel direct de la suite complète omet par erreur le filtre de profil :3863PASS,371exclusions et deux échecs IntegrationPg faute de connexion de fixture ; aucun SQL n'est exécuté par ces deux cas. Cette invocation est conservée et ne sert pas de validation Unit. Le runner officiel est ensuite exécuté avec Profile=Unit,Backend,Release et les mêmes DLL déjà compilées :3863PASS,0FAIL,2exclusions explicites sur3865cas,sources stables. Les corrections de prompts ne sont pas encore une preuve de meilleur comportement du modèle réel. Les événements SQL fermés révèlent aussi zéro résultat sparse et exact pour les douze recherches de payé6,avec résultats denses disponibles et sans erreur de retriever ; un diagnostic borné des formulations de recherche est préparé avant un nouvel essai payant. TESTE_NON_APPROUVE maintenu.

### 2026-10-30 — diagnostic réel de la recherche textuelle, avant toute nouvelle dépense

Le commit `30fc036cbbabff366d3fe97c25df0705254a96f1` est poussé ; GitHub Actions41001146341 se termine avec succès à04:17:33UTC. Le backend Linux8 est construit depuis604fichiers publics et relu indépendamment : seuls les deux prompts diffèrent du build7, les602autres fichiers sont identiques. Les identités source,image et DLL concordent. Cette image n'est pas démarrée avant la correction supplémentaire décrite ci-dessous ; un build ne vaut pas qualification du runtime.

Le diagnostic SQL borné s'exécute réellement à04:18UTC dans une transaction REPEATABLE READ READ ONLY, suivie d'un ROLLBACK. Il conserve les quatre révisions et leurs empreintes, inspecte2016entrées/2016chunks et ne trouve aucune anomalie des liens contrôlés. Les douze formulations réellement utilisées par payé6 donnent toutes zéro résultat textuel. Les quatre reformulations tirées de noms déjà observés donnent respectivement5,2,3et3chunks. Le moteur interprète les longues listes non citées comme des termes cumulatifs. Ces mesures isolent le canal sparse ; elles ne rejouent ni le dense ni le classement complet et ne prouvent pas à elles seules toute la causalité de l'échec métier. Le reçu instrumenté passe :zéro écriture,erreur,session résiduelle,OOM ou redémarrage ; le serveur conserve son plafond1GiB PostgreSQL.

L'audit des cinq vraies traces confirme une seconde difficulté :le modèle voit20candidats sélectionnés par recherche,mais les phases de diagnostic sont omises ; il ignore que le canal textuel n'a rien trouvé. Le descriptif natif présente la recherche comme seulement sémantique,et certaines consignes invitent à accumuler des variantes dans une même requête. Une correction générique de ce contrat et d'un petit signal sparse est engagée, sans modifier l'algorithme de recherche,les filtres,les documents,les corps de preuve ou les plafonds. Ses tests et son comportement réel restent à établir à ce point.

Telegram66 est envoyé avec succès à04:18:04UTC,une partie,reçu conservé. Aucun nouvel appel payant depuis payé6 ; le cumul estimé reste0,14321470USD sur0,50USD,avec17charges et aucune réservation ouverte. Les fichiers de préparation du contrôle gratuit W4 et son TLS renouvelé ne constituent pas encore un essai exécuté. Le statut reste TESTE_NON_APPROUVE.

La correction du contrat de recherche est ensuite gelée :une petite partial commune décrit la recherche hybride,les requêtes ciblées,les variantes séparées et la conservation de toutes les obligations utilisateur dans le handoff et la vérification finale. Elle est exposée au Planner,à sa revue et aux outils natifs/legacy. Le diagnostic projeté ajoute uniquement canonicalSparseStatus :zero_hits/has_hits/unknown. Un zéro exige une phase canonique unique et complète,sans dégradation sparse ; une phase manquante,omise,ambiguë,invalidée ou en erreur reste inconnue. Les autres phases,leurs temps et leurs compteurs superposés restent omis. Le canal dense,les requêtes exécutées,les filtres et les limites ne sont pas modifiés.

Les15régressions sont réellement compilées sur le produit antérieur,avec retrait temporaire de la nouvelle partial :15échecs comportementaux,aucun échec de compilation. Les sources corrigées sont restaurées octet pour octet dans un finally ; les15cas passent ensuite. Les trois parcours HTTP simulés prouvent notamment guillemets,OR,catégories,topK et identité documentaire conservés,avec un exemple d'inertage hors cuisine. Douze cas distinguent présence de résultats textuels,zéro réel et information inconnue ; une sentinelle présente dans le diagnostic brut reste absente du contexte modèle. Une assertion initialement vide a été corrigée avant exécution et son ancien gel est conservé.

La suite officielle Unit/Backend/Release passe sur les DLL fraîchement compilées par GREEN :3878PASS,0FAIL,2exclusions explicites sur3880cas ; empreintes sources avant/après identiques. Le plafond existant de3500caractères pour huit diagnostics reste éprouvé sans relèvement. Architecture2111sources/21fichiers à revue de taille et17contrôles comportementaux de sondes passent. Ces résultats qualifient le contrat,la projection et leur transport simulé ; la reformulation autonome et le planning réel restent à vérifier sur le nouveau build Linux. Aucun appel payant ni réingestion pour ce changement de consultation.

### 2026-10-30 — qualification gratuite W4 et reprise payante bornée

Le commit `7046a7411bfd0dad05756b7fd307307e75d5a239` est poussé ; GitHub Actions41003950659 passe. Le build Linux9 est construit depuis605fichiers publics : une nouvelle partial de politique de recherche et trois fichiers existants changent par rapport au build8. Le retour sur PC vérifie les métadonnées, l'image et la DLL. Les producteurs documentaires restent identiques ; aucune réingestion n'est nécessaire pour ces seules corrections de consultation.

Le parcours gratuit W4 s'exécute réellement avec le backend9 et un fournisseur HTTPS fictif. Les six échanges couvrent Planner, Writer, suivi, revue d'insuffisance, second suivi et Critic ; dix opérations documentaires réussissent. Le contrôle complet sur PC passe sous neuf gardes réseau/processus : six corps capturés identiques aux traces et sondes,91fenêtres visibles conformes au worker et au corpus SQL, statut sparse conforme au diagnostic brut, consignes de sélection conditionnelles, catalogue actif et lecture d'une autre source après revue vérifiés. Le Critic utilise effectivement4096tokens maximum. Une première version de l'oracle attendait à tort une complétion vide pour les deux appels natifs structurés ; le contrôle corrigé exige leurs arguments exacts et l'opération normalisée. Les captures restent inchangées. Ce scénario contrôlé se termine volontairement par une insuffisance documentaire : il ne valide aucune qualité de réponse du vrai modèle.

La conservation avant nettoyage passe, puis la seule discussion fictive W4 est supprimée une fois par l'API normale, avec cascades et audit+1 vérifiés. Les deux applications possédées sont arrêtées. Une nouvelle capture à05:09:01UTC confirme32 tables documentaires,77 migrations,4PDF/510pages inchangés, zéro tâche/session/message résiduel et aucun nouvel OOM ou redémarrage PostgreSQL. Le ledger payé est comparé octet pour octet aux snapshots fermés :17charges,0,13642784USD dans ce ledger,aucune réservation ouverte ; avec le précédent montant hors ledger, cumul0,14321470USD sur0,50USD. Ce montant ne représente pas le solde fournisseur.

Telegram67 est envoyé avec succès à05:18:17UTC,une partie,reçu local conservé. Les nouvelles préparations de runtime et d'API payés passent respectivement24et23tests purs, puis une contrelecture indépendante. La configuration signée payée reste identique ; seule sa copie de staging, son ACL et un nouveau dossier de traces sont qualifiés. Une première préparation locale du handoff est refusée pour encodage Windows avant tout appel ; une version UTF-8 sémantiquement identique est admise. Le backend payé7 démarre à05:15:53UTC et une seule tâche réelle est lancée à05:18:29UTC avec5.6Luna, sans retry, dans le plafond cumulatif existant. Son résultat et son coût ne sont pas encore établis à ce point. TESTE_NON_APPROUVE maintenu.

L'essai payé7 échoue avec `accumulated_evidence_limit_exceeded` avant tout Writer. Le seul Planner répond correctement au protocole et propose huit recherches ; les six événements SQL correspondent exactement à leurs six premières formulations. Cinq opérations réussissent et accumulent successivement28,54,62,83,puis110identités documentaires distinctes. La sixième voudrait en ajouter19,soit129pour un plafond128 ; son admission entière est refusée et n'ajoute aucun extrait. La sonde confirme rotation désactivée,quatre carriers de source protégés et aucun champ diagnostique omis. Deux recherches du Planner ne sont jamais exécutées. Le code de bootstrap propage ce refus alors que la phase de continuation ultérieure sait activer une rotation. Il s'agit d'un arrêt du logiciel avant présentation des110preuves au rédacteur, pas d'une conclusion du modèle sur leur insuffisance. Plusieurs recherches ont maintenant des résultats textuels ; cet essai unique ne suffit pas à quantifier l'amélioration métier.

Le readback payé7 est réellement exécuté : une trace Planner,deux logs complets,six événements SQL,transaction en lecture seule et source34 inchangée. Le ledger ferme18charges/18réservations,aucune ouverte ; nouvel appel0,0021713USD,cumul estimé0,14538600USD sur0,50USD. Les17anciennes lignes restent le préfixe exact des nouveaux snapshots. Le scénario échoué est conservé dans33fichiers,dont toutes les empreintes sont revérifiées dans le tar et sur PC. Le premier close refuse un SHA de plan mal retranscrit avant toute création de sortie ; la valeur est reprise du fichier PC mesuré. L'inspection diagnostique conserve explicitement `evidenceConsistent=false`,l'absence de Writer et l'impossibilité de vérifier une projection documentaire jamais envoyée ; aucune gate normale de2à16appels n'est assouplie pour transformer cet échec en succès.

Une nouvelle capture SQL32 à05:26:04UTC et sa comparaison locale confirment32 tables,77 migrations,4PDF/510pages inchangés,une seule tâche/session fictive et aucun message,ingestion active,OOM ou redémarrage PostgreSQL. Un reçu distinct admet uniquement l'intégrité des fichiers fermés et ce statut d'échec ; il ne vaut ni qualification du parcours de planning ni approbation du produit. La correction préparée distingue désormais interruption du lot initial et interdiction durable de recherche : elle devra conserver les preuves acquises,annoncer les requêtes refusées ou omises et permettre une lecture ciblée après activation de la rotation,sans relever128ni affaiblir les contrôles d'identité. Ses nouvelles régressions ne sont pas encore compilées à ce point. Aucun nouvel essai payant n'est lancé.

### 2026-10-30 — correction de la saturation du lot initial et validation locale

La seule discussion fictive payé7 est supprimée une fois par l'API normale, avec audit+1, cascades vérifiées et arrêt du seul backend possédé ; le faux fournisseur était déjà arrêté. La capture SQL32 après nettoyage, réellement effectuée à05:45:20UTC, conserve32 tables,77 migrations,4PDF/510pages et confirme zéro tâche,session,message ou ingestion résiduelle, sans OOM ni redémarrage PostgreSQL. Les33fichiers d'échec restent intacts. Le cumul estimé reste0,14538600USD sur0,50USD,18charges et aucune réservation ouverte.

La correction du bootstrap conserve les110preuves déjà admises, décrit le refus atomique et les opérations jamais exécutées, puis remet la décision au Writer lorsque la rotation protégée existante est activée. Le refus ne devient pas une interdiction permanente de recherche. L'historique conserve séparément succès,échec et statut inconnu, y compris un échec suivi d'une reprise identique réussie ; seules les recherches réussies sont dédupliquées. Les aperçus des requêtes refusées ou omises sont bornés et déclarent toute troncature. Une nouvelle sonde compte les admissions,le refus,les omissions et l'ouverture effective de la recherche, sans texte documentaire. Le plafond128,les contrôles canoniques,les identités,les budgets et les erreurs fatales de sécurité ne sont pas relâchés. Un premier refus sans admission réussie dans le même lot reste fatal, même si le handoff contient déjà des preuves.

La première validation causale conserve13échecs et4succès sur le produit antérieur. Son premier GREEN conserve5échecs : les fixtures finales citaient E4 alors que son corps n'était pas visible. Le contrôle produit refusait correctement ces propositions. Les tests sont corrigés seuls pour demander explicitement la lecture canonique avant cette citation ; aucune garde de citation n'est retirée. La validation finale V3 recompile réellement l'ancien produit contre les nouveaux tests :13échecs comportementaux et4invariants réussis, puis17succès après restauration exacte des sources corrigées. Le produit reste inchangé entre V2 et V3.

La suite officielle Unit/Backend/Release exécutée sur les DLL fraîchement compilées passe3895cas, sans échec, avec2exclusions explicites sur3897cas ; les empreintes de2217sources avant/après sont identiques. L'architecture passe sur2114sources avec21fichiers à revue de taille ; les17contrôles comportementaux des sondes passent également. Aucun fournisseur payant n'est appelé par ces contrôles. Une fixture gratuite reprend la réponse Planner réellement enregistrée, puis simule les étapes suivantes : sa préparation et sa contrelecture passent14tests purs. Le déploiement du nouveau build Linux,la rotation réelle après ce refus et la qualité du planning du vrai modèle restent à établir. TESTE_NON_APPROUVE maintenu.

### 2026-10-30 — bootstrap W5 éprouvé sur Linux et préparation payé8

Le commit `934c4389ba232afb1f2169311257236c0fb0e8c2` est poussé et GitHub Actions41012269148 passe. Le build Linux10 est construit depuis606fichiers publics : une nouvelle partial bootstrap et trois fichiers produits modifiés par rapport au build9 ; aucun producteur documentaire ne change. L'archive source est vérifiée membre par membre et le readback sur PC confirme image,DLL,inventaire et provenance. Le profil8 conserve question,modèle5.6Luna,prix,options signées,plafonds et corpus. La configuration gratuite,TLS et clé factice sont copiés dans un staging7 neuf ; aucune clé OpenAI ni configuration payée n'est copiée pour le contrôle gratuit. Démarrage Production,signature,DLL,privilèges,montages complets et trois contrôles TLS passent.

W5 s'exécute réellement avec le nouveau backend. Seul le Planner est une réponse réelle enregistrée ; toutes les réponses suivantes sont simulées. Les six échanges HTTP200 couvrent Planner,Writer,suivi,revue d'insuffisance,suivi et Critic. SQL conserve huit opérations : cinq recherches réussies,un refus atomique de capacité,sa reprise globale identique réussie,puis une lecture ciblée d'une autre source initialement observée. Le refus intervient après110identités admises,avec19nouvelles demandées pour128places. La reprise n'invente aucune identité de document,admet ces19nouvelles identités et retire exactement une ancienne :128restent actifs. Les deux dernières requêtes du Planner restent honnêtement non exécutées. La recherche reste possible après le refus initial,avec historique des tentatives séparées.

Le readback conserve les six traces,tous les événements SQL,le corpus et les deux logs complets. La fermeture conserve75fichiers,25 567 205octets bruts ; archive et extraction sont revérifiées avant/après. L'inspection complète sur PC passe sous neuf gardes réseau/processus : six corps HTTP identiques aux traces et sondes,98fenêtres visibles conformes au worker et au corpus SQL,catalogue actif,diagnostic sparse,consignes conditionnelles,reprise sous rotation,lecture de l'autre source et4096tokens maximum du Critic. Le scénario se termine volontairement en insuffisance sans claim : aucune qualité métier du vrai modèle n'est déduite.

Deux ajustements d'oracle sont conservés séparément des captures. L'inspecteurV1 exigeait un diagnostic projeté pour la recherche refusée,alors que le catch produit publie uniquement le refus de ressource ; le diagnostic brut demeure dans SQL. V2 accepte ce diagnostic absent uniquement pour cet événement échoué strictement lié,compte explicitement cinq omissions opérationnelles et refuse un faux zéro fabriqué. Ses17tests purs et sa contrelecture passent. Le premier wrapper root emploie ensuite un nom de champ de compteur erroné après l'inspection réussie ; le wrapper corrigé rejoue les mêmes données et produit exactement la même empreinte de reçu. Aucun corps de preuve,contrôle de citation ou fichier produit n'est modifié pour ces ajustements.

La comparaison SQL32 préalable passe :32 tables,77 migrations,4PDF/510pages inchangés,une tâche/session fictive,sans message,ingestion active,OOM ou redémarrage PostgreSQL. Le seul dialogue W5 est supprimé normalement,une fois,avec GET404,cascades,audit+1 et arrêt des deux applications possédées. Une nouvelle capture réellement effectuée à06:48:46UTC et sa comparaison confirment les mêmes32/77/4/510 et zéro tâche,session,message ou ingestion résiduelle. Les75fichiers restent intacts.

Deux sondes de lecture du ledger payé,sans réseau et en lecture seule,confirment ses corps identiques aux snapshots payé7,18charges et18réservations closes,zéro ouverte. Cumul estimé inchangé0,14538600USD sur0,50USD ; aucune facture ni solde OpenAI n'est prétendu vérifié. Telegram68 est envoyé avec succès à06:19:27UTC,une partie,reçu conservé. Les configurations et signatures payées sont copiées octet pour octet dans le staging7 ; la clé OpenAI et le ledger existants ne sont pas copiés. Un répertoire de traces neuf est qualifié sans réseau. Les15références réelles du runtime payé sont admises ; son démarrage est lancé à06:56:56UTC. Aucun résultat payé8 ni coût nouveau n'est établi à ce point. TESTE_NON_APPROUVE maintenu.

### 2026-10-30 — payé8 : passages fidèles, refus du relecteur et clarification des consignes

Un seul essai réel est lancé à07:00:30UTC avec5.6Luna. Il atteint les cinq appels attendus et onze opérations documentaires, toutes réussies ; aucun refus de capacité ne bloque cette fois la recherche. Le premier Writer renvoie une insuffisance avec deux claims, la revue demande de rechercher davantage, puis le suivi Writer produit une proposition complète de20claims. Le Critic la remplace finalement par une insuffisance de cinq claims. Le statut technique succeeded signifie ici que le traitement se termine normalement, pas que le planning demandé est fourni ou accepté.

La lecture des cinq complétions et de leurs entrées exactes identifie deux exigences ajoutées par le relecteur : une saison obligatoire alors que la demande ne date pas la semaine, et une étiquette documentaire de collation pour pouvoir proposer un dessert à ce créneau. Le passage saisonnier décrit un contexte favorable ; il ne formule pas une interdiction hors de ce contexte. Le contrat autorise déjà une proposition SAAIA fondée sur les caractéristiques documentées, sans attribuer ce placement au livre. L'audit source ne trouve aucun validateur serveur imposant ces deux exigences. Il relève en revanche une ambiguïté entre catégorie descriptive, recommandation positive et condition réellement nécessaire dans les consignes communes. C'est une cause probable du refus observé, pas la preuve que le correctif suffira au prochain modèle réel. La proposition intermédiaire est également très répétitive ; ses20cases ne constituent pas une validation professionnelle.

Les61fichiers de l'essai sont fermés et vérifiés membre par membre avant et après extraction sur PC. L'inspection normale des cinq traces, des sondes, du résultat et des événements SQL ne trouve aucune incohérence. Une seconde vérification confronte les70passages visibles dans tous les appels au worker complet et au corpus SQL : contenu exact, identités, pages physiques, qualité, coupures et curseurs concordent. Les consignes communes étaient effectivement présentes dans les quatre appels de synthèse. Cette fidélité au corpus ingéré ne signifie pas que chaque PDF original est parfait visuellement, ni que le modèle interprète correctement tous les passages.

Le ledger ferme23charges et23réservations, aucune ouverte. Payé8 coûte0,04954296USD estimé ; le cumul suivi atteint0,19492896USD sur le plafond0,50USD, montant hors ledger antérieur inclus une seule fois. Les18anciennes lignes restent le préfixe exact. Ce total ne constitue pas le solde ni la facture OpenAI. Telegram69 est envoyé avec succès à07:19:48UTC, une partie, reçu local conservé.

La correction se limite à deux consignes produit : une recommandation ou rubrique descriptive n'est pas exclusive sans preuve de cette exclusivité ; le modèle doit juger la compatibilité ordinaire à partir du document et de la demande réelle. L'absence d'interdiction ne prouve pas l'aptitude ailleurs. Les vrais prérequis, interdictions, variantes, classifications explicitement demandées, faits, identités et citations restent obligatoires. Aucun plafond, producteur documentaire, algorithme de recherche ou règle de licence n'est modifié.

Dix régressions sont compilées réellement : avec les deux anciens fichiers produit, huit échouent et deux invariants sur les restrictions passent ; après restauration exacte des sources corrigées, les dix passent. Les parcours HTTP simulés couvrent Writer et Critic, deux styles de consignes, deux protocoles, puis la composition des étapes de revue et de réparation. Ils vérifient le transport des règles, des corps et des citations ; ils ne certifient pas l'interprétation d'un vrai modèle. La suite officielle Unit/Backend/Release passe3905cas sans échec, avec deux exclusions explicites sur3907cas. Les2218sources restent identiques avant/après ; les DLL sont celles fraîchement compilées par GREEN. L'architecture passe sur2115sources, avec21fichiers à revue de taille. La contrelecture indépendante ne trouve aucun défaut matériel dans le petit correctif et ses tests.

La conservation préalable SQL32 passe, puis seule la discussion fictive payé8 est supprimée une fois par l'API normale : GET404, cascades, audit+1 et arrêt du backend possédé sont vérifiés ; le fournisseur fictif déjà arrêté reste inchangé. Une capture indépendante à07:40:05UTC confirme32 tables documentaires,77 migrations,4PDF/510pages inchangés, zéro tâche, session, message, ingestion active, nouvel OOM ou redémarrage PostgreSQL. Toutes les preuves fermées restent intactes. Le build Linux suivant et le contrôle gratuit des consignes corrigées restent à exécuter avant un nouvel essai réel borné. TESTE_NON_APPROUVE maintenu.

### 2026-10-30 — W6 gratuit fermé, puis payé9 : le problème de découverte reste ouvert

Le commit81af74b est poussé et GitHub Actions41021862185 passe. Le build Linux11 est relu sur PC :606fichiers publics, image et DLL cohérentes, seuls les deux fichiers de consignes changent face au build10. Aucun producteur documentaire ne change ; aucune réingestion n'est requise pour ces seules consignes. Le contrôle gratuit W6 utilise un Planner enregistré, puis des réponses scénarisées : six échanges HTTPS réels, huit événements SQL dont un refus atomique de capacité,98fenêtres conformes au worker complet. Les nouvelles consignes sont présentes dans les cinq messages SYSTEM de synthèse. Ce contrôle valide le transport et la continuation, pas le raisonnement d'un modèle réel.

Une première admission du runtime gratuit refuse un champ owner attendu à tort dans une carte comportant exactement quatre rôles ; la préparation V2 corrige cet oracle, avec ancien échec conservé. Les75fichiers W6 sont fermés puis revérifiés sur PC. La seule discussion fictive est supprimée normalement et les applications possédées sont arrêtées. La capture SQL32 à08:22:33UTC conserve32 tables,77 migrations,4PDF/510pages, sans résidu, OOM ou redémarrage. Deux sondes sans réseau et en lecture seule confirment le ledger inchangé à23charges closes. Telegram70 est envoyé à08:20:15UTC, reçu conservé.

Un seul essai réel payé9 est ensuite lancé à08:31:59UTC avec5.6Luna et la même demande. Il termine à08:32:40UTC : cinq appels modèle, onze opérations documentaires, dont dix réussies et un refus d'admission à la huitième. Le Writer, sa revue et le suivi final concluent à une insuffisance ; aucun des20créneaux n'est rendu. Le Critic sait demander trois recherches complémentaires, mais sa dernière réponse réclame cinq collations malgré le contrat explicite autorisant la réutilisation de choix documentés. Le modèle dispose encore de budget et d'outils. Ce n'est ni un quota fournisseur ni un timeout ; le succès technique de la tâche ne vaut pas planning fourni.

La lecture des entrées exactes relève112preuves admises initialement, dont33cartes titrées, mais18extraits seulement visibles au Writer. Plusieurs recettes concrètes admises sont absentes du contexte visible, tandis que des passages généraux y figurent. Les18extraits visibles sont complets, sans troncature de leur corps ; l'omission d'autres candidats résulte de la sélection et de la limite de projection. Après les recherches du Critic,128preuves sont actives et14visibles. Une amélioration générique de leur découverte est à concevoir et éprouver ; aucun titre ne doit être promu en preuve de contenu ni en aptitude à un repas. Le workspace candidat est désactivé dans cet essai et n'est donc pas sa cause.

Le readback payé9 ferme cinq traces, les deux logs complets et la projection SQL en lecture seule. Le ledger contient28charges et28réservations, aucune ouverte ; coût estimé de payé9 :0,0447559USD, cumul estimé0,23968486USD sur0,50USD. Le préfixe historique de23lignes est identique. Ces montants ne sont ni un solde ni une facture fournisseur. Les53fichiers sont archivés et vérifiés membre par membre. Le premier close refuse un SHA mal retranscrit depuis l'affichage PTY avant création de sortie ; le SHA est ensuite dérivé du fichier téléchargé.

L'inspecteur V10 conserve evidenceConsistent=false : son ancien rapprochement sparse ne sait lier que les recherches réussies, alors que le modèle voit ici un prior refusé avec diagnostic nul. Le SQL conserve le diagnostic brut de ce refus sans admettre de preuve. Une correction V11 de cet oracle est en préparation, avec relecture obligatoire de l'archive V10 et conservation de son ancien résultat ; aucun contournement n'est admis. Le contrôle de fidélité complet refuse correctement de partir de l'inspection encore non qualifiée. La conservation SQL32 réellement capturée à08:46:35UTC passe déjà, sans modification documentaire ni OOM. Le nettoyage de la discussion payé9 attend les preuves d'inspection et de fidélité. Aucun nouvel appel payant n'est lancé ; TESTE_NON_APPROUVE maintenu.

Le correctif V11 de l'oracle passe ensuite58tests purs et une contrelecture indépendante. Il rattache chaque omission opérationnelle au refus SQL exact dans le préfixe réellement consommé, sans transformer le diagnostic nul en zéro lexical ni emprunter celui d'une reprise ultérieure. Une garde supplémentaire refuse un scope documentaire sans carrier vérifiable. La réinspection réelle à09:02:04UTC recompose d'abord l'ancien V10, retrouve son unique erreur connue et conserve ses53fichiers ; le nouveau reçu vérifie cinq traces et cinq sondes, avec quatre omissions opérationnelles liées à la même huitième opération refusée. Les autres contrôles restent obligatoires. Le contrôle complet de fidélité passe alors sur les68fenêtres visibles des cinq appels : corps, identités, pages, qualité et curseurs conformes au worker complet et au corpus SQL. Ces résultats confirment la fidélité de ce qui était présenté ; ils ne valident pas la qualité du refus ni un planning absent.

Après ces preuves, la seule discussion fictive payé9 est supprimée une fois par l'API normale : cascades, GET404 et audit+1 vérifiés, backend possédé arrêté, faux fournisseur déjà arrêté préservé. Une capture SQL32 indépendante à09:07:19UTC confirme32 tables documentaires,77 migrations,4PDF/510pages inchangés et zéro tâche/session/message/ingestion résiduelle, sans nouvel OOM ou redémarrage PostgreSQL. Les preuves fermées restent intactes. Aucun appel modèle supplémentaire ni nouvelle dépense depuis payé9.

La correction produit suivante est préparée : un catalogue de navigation compact expose les titres et pages exacts des cartes déjà admises, même quand leurs corps ne tiennent pas dans la projection. Il ne vaut ni preuve, ni décision d'aptitude, ni obligation de variété ; les outils existants doivent lire les corps avant citation. Le budget de métadonnées reste inclus dans le plafond documentaire, borné à son quart et8000caractères échappés au maximum ; aucun plafond d'appels ni algorithme de classement n'est modifié. Treize cas C# sont écrits mais pas encore exécutés à ce point. Le catalogue facilite la découverte des candidats existants ; il ne prouve pas encore que5.6Luna les lira ni qu'un planning professionnel sera produit. TESTE_NON_APPROUVE maintenu.

La contrelecture statique de ce petit changement ne relève aucun défaut matériel. Les treize tests compilent réellement avec l'ancien produit : neuf échecs comportementaux et quatre invariants réussis. Les trois fichiers antérieurs et la nouvelle partial sont restaurés exactement dans un finally ; les treize cas passent ensuite avec le produit corrigé. Les tests couvrent les deux protocoles et styles, les titres omis lisibles par source/page, le refus de citation d'un corps absent, le budget échappé strict, les identités et retraits, les omissions annoncées et les sondes numériques sans texte documentaire. Le corps après lecture reste simulé à ce stade ; la vraie lecture de source sur le serveur attend le prochain contrôle gratuit.

La suite officielle Unit/Backend/Release passe3918tests, zéro échec et deux exclusions explicites sur3920cas. Les DLL sont celles fraîchement compilées par GREEN ; les2220sources restent identiques avant/après. L'architecture passe sur2117sources avec21fichiers à revue de taille. La nouvelle partial compte88lignes. Ces validations restent hors ligne et ne certifient pas le choix d'outils ni le planning d'un vrai modèle. Le build Linux12 et le scénario gratuit de navigation puis lecture canonique sont les prochaines étapes, avant un nouvel essai payant borné.

2026-10-30, navigation W7 : le produit est poussé au commit d6d3ae8d ; la validation hors ligne GitHub 41032482944 réussit. Le build Linux12 termine à09:21:16UTC et son readback vérifie l'image, les DLL et les607fichiers publics. La comparaison avec le build11 confirme une modification du consommateur de contexte seulement : trois fichiers modifiés et la petite partial de navigation ajoutée, producteur d'ingestion inchangé.

Le premier démarrage gratuit W7 est arrêté avant toute question : le wrapper Python du fournisseur fictif interprète --planner comme une abréviation ambiguë de --planner-sha256/--planner-bytes. Les deux conteneurs sont arrêtés par le rollback ; ce n'est pas un défaut d'image ni un appel payant. Le script V3 désactive uniquement cette abréviation. Le cas réel de commande complète reproduit la sortie2 avec V2 et passe avec V3, parmi17tests purs ; anciennes sources et preuves conservées. Le remplacement nommé est effectué sur la paire arrêtée, avec les six autres membres inchangés. Une reprise dédiée réadmet la paire complète et les preuves initiales, sans suppression/création de conteneur ni dérogation à la fraîcheur SQL. Elle réussit à09:57UTC : Production, signature, identité du build, privilèges, trois contrôles TLS, métadonnées du lock gratuit et Source34 conformes.

Le scénario gratuit lancé à09:59:22UTC termine avec six enveloppes : Planner enregistré, réponses ultérieures scriptées, huit opérations documentaires dont sept réussites et le refus de capacité attendu. Le réessai admet19nouvelles preuves, en retire une et respecte128preuves actives. Les81fichiers fermés sont téléchargés puis vérifiés avant/après extraction ; l'inspecteur V2 passe sous neuf gardes réseau/processus. Les84fenêtres visibles correspondent exactement au worker complet et au corpus SQL, avec pages, identités, qualité et curseurs contrôlés. Les titres du nouveau catalogue correspondent aux cartes ACTIVES :31au premier Writer puis34sur les appels suivants, aucun locator omis dans ce scénario,6605puis7160caractères échappés sur8000autorisés. Les cinq sondes numériques correspondent au contenu transmis. Une page absente de tous les extraits visibles est demandée par read_source, retournée par le véritable outil, puis transmise inchangée au Writer et au Critic. Le locator choisi est un titre documentaire de navigation : cette preuve porte sur l'accès et la fidélité de la page, pas sur l'aptitude culinaire d'une recette.

La réponse terminale insuffisante sans claim est volontaire dans ce contrôle fictif ; aucun planning réel ni décision d'un nouveau modèle n'est validé. L'ancien inspecteur W7 V1 et son contre-exemple d'observation vide sont conservés : V2 exige explicitement des observations nulles pour compter une carte primaire. La capture SQL32 pré-nettoyage du30octobre10:09:26UTC réussit avec zéro session SQL résiduelle et sans écriture, OOM ou redémarrage ; sa comparaison documentaire et le nettoyage normal de l'unique discussion fictive restent à terminer à ce point. Aucun appel payant depuis payé9 : cumul estimé0,23968486USD sur0,50USD,28charges/28réservations et aucune ouverte lors de la dernière lecture du ledger payé. TESTE_NON_APPROUVE maintenu.

Le nettoyage normal W7 a finalement réussi : une seule discussion fictive supprimée, cascades et audit contrôlés, deux conteneurs possédés arrêtés. La capture du30octobre10:16:35UTC confirme32 tables documentaires identiques,77 migrations,4PDF/510pages, aucune discussion/question/message ou ingestion résiduelle, aucun OOM ni redémarrage PostgreSQL. Les soldes de campagne restent rapprochés à28charges/28réservations, aucune ouverte, avant l'essai suivant.

2026-10-30, essai réel payé10 : le même modèle économique gpt-5.6-luna, le même cas cinq jours/quatre repas et le build Linux12 sont utilisés, sans Terra ni changement d'ingestion. Le job lancé à10:33:42UTC réussit à10:34:39UTC : réponse answered avec20claims en3appels réels (Planner, Writer, Critic), huit recherches canoniques SQL toutes réussies. La capture V12 ferme59fichiers et sa relecture locale vérifie tous les membres avant/après extraction. Les35locators de cartes actifs sont tous exposés au Writer et au Critic, sur7293caractères échappés pour8000autorisés ; leur contrôle à ce stade porte sur les métadonnées SQL/profil, pas encore sur la fidélité de tous les corps au Worker. Les16preuves textuelles visibles sont les extraits candidats effectivement présentés, les128preuves admises ne sont pas toutes des corps transmis. Aucune demande native supplémentaire n'est faite par le modèle dans ces trois appels.

Le ledger payé10 est capturé sans écriture et ses trois nouveaux débits sont rapprochés des trois réservations et de la consommation fournisseur : coût estimé0,0324227USD, cumul de campagne0,27210756USD sur0,50USD,31charges/31réservations, aucune ouverte. La baseline hors ledger est ajoutée une seule fois. Ces montants sont calculés à partir de la consommation, sans lecture de facture ni affirmation sur le solde du compte OpenAI.

La vérification visuelle root couvre les six PDF/pages réellement cités, avec empreintes des originaux contrôlées : toutes les identités choisies correspondent à de vraies recettes sur leurs propres pages, y compris les recettes de droite sur les doubles pages Moulinex. Le texte modeste des20claims correspond aux passages visibles. Le résultat utilise six recettes distinctes pour20cases : répétitions permises par le contrat figé, mais variété limitée, insuffisante pour proclamer une qualité professionnelle générale. La vérification de ces six pages ne certifie pas tous les PDF, toutes les étapes de recette, l'équilibre nutritionnel ni le rendu WinUI. Les fenêtres peuvent contenir des restes OCR/en-têtes ou d'une recette voisine ; ils n'ont pas été utilisés pour les affirmations contrôlées.

La première évaluation mécanique complète de payé10 échoue dans notre propre vérificateur : il exige le titre intégral de selectedItem aussi dans claim.text. Le contrat produit valide séparément l'identité sélectionnée et un commentaire factuel court ; les cellules contiennent bien les noms complets et les citations. Ce faux rejet est conservé avec l'ancien évaluateur gelé, puis un correctif minimal et des cas causaux sont préparés. Aucun original capturé n'est réécrit et aucun nouvel appel payant n'est lancé pour corriger ce contrôle. La comparaison pré-nettoyage du10:45:57UTC vérifie déjà le corpus SQL32 conservé et l'unique job/discussion fictive attendu. Fidélité complète, rejeu mécanique corrigé, nettoyage payé10 et contrôle SQL32 final restent en cours à ce point. TESTE_NON_APPROUVE maintenu.

Le rejeu de payé10 sur le même ensemble fermé réussit après le seul correctif de contrat du vérificateur :20cellules,6identités de recettes,6cartes sources uniques, aucune modification des textes enregistrés. L'ancien oracle et l'échec sont conservés ; le nouvel adaptateur V4 passe65tests purs avec contrelecture indépendante. La vérification de tous les rôles réussit ensuite :16présentations d'extraits au Writer et16au Critic, aucune au Planner ; corpus complet Worker/SQL comparé, pages, qualité et curseurs conservés. Les repères de navigation restent des métadonnées qualifiées séparément par V12, sans valeur de preuve citable. La revue visuelle root et ces contrôles mécaniques ne constituent pas une acceptation du produit ni de sa répétabilité.

Le rapport Telegram73 est envoyé à10:59:53UTC, une partie, code0. Avant commit, la revue root découvre qu'un nouveau test destiné au dépôt charge l'ancien oracle depuis artifacts, dossier ignoré par Git : il échouerait sur un checkout neuf. Le test causal reste donc dans les preuves locales gelées ; les régressions suivies doivent être rendues autonomes et exécutées sans artifacts avant tout push. La correction du vérificateur et les reçus réels ne sont pas modifiés par cette séparation du test historique. Nettoyage normal payé10 et vérification SQL32 finale en préparation.

La fermeture réelle payé10 réussit : un seul DELETE de la discussion fictive, audit augmenté d'une unité, session/job et cascades absents, backend possédé arrêté et fournisseur fictif déjà arrêté conservé. Les59fichiers fermés restent identiques. La capture SQL32 du30octobre11:08:43UTC confirme32 tables documentaires conservées,77 migrations,4versions/510pages, zéro job/session/message/ingestion ou expiration résiduel, aucun OOM ni redémarrage. Cette fermeture ne consomme aucun appel fournisseur ; le cumul reste0,27210756USD,31débits rapprochés et aucune réservation ouverte.

Les dix régressions suivies sont rendues autonomes : leur rejeu avec les39tests existants dans un arbre temporaire de cinq seules sources publiques, sans artifacts ni dépôt Git, passe49cas sous gardes. Le cas causal avec l'ancien oracle reste dans les preuves locales, hors discovery du dépôt. La suite officielle Test-PlanningOffline.ps1 réussit ensuite sur le checkout courant : six suites,198tests Python découverts dont197réussites et une exclusion système explicite autorisée, cinq ensembles PowerShell réussis. Aucun binaire applicatif, service, clé, SQL ou modèle n'est utilisé. Le petit correctif porte uniquement sur le vérificateur et ses régressions ; le backend build12 n'a pas changé. La prochaine qualification utile est la répétition du même scénario figé, en observant séparément la variété et avant toute acceptation fonctionnelle/WinUI. TESTE_NON_APPROUVE maintenu.


## 2026-10-30 — PAID11 : citations fidèles, mais perte de continuité des lectures

La correction du vérificateur de texte de justification, publiée au commit `738bcd5`, a passé le workflow GitHub `41046155074`. Elle ne modifie ni le modèle ni les documents.

Le même scénario gelé a ensuite été répété une fois avec le vrai modèle `gpt-5.6-luna`, sans changement de question, de corpus, de profil ou de budget. PAID11 a produit une grille de vingt cases en 73,3 secondes et six appels. Les seize événements documentaires comprennent quinze succès et un refus explicite de capacité lors de la huitième recherche initiale. Le modèle a ensuite demandé huit recherches exactes dans les sources. L’archive complète de soixante-dix fichiers et la relecture API/SQL ont été vérifiées. Le même évaluateur mécanique V4 valide les vingt coordonnées, les citations et les corps visibles. La vérification indépendante SQL/Worker compare les soixante-douze apparitions de preuves dans les six appels, sans réécriture des captures.

La revue visuelle directe des quatre pages PDF originales confirme l’existence des recettes et les affirmations citées. Elle ne valide pas le planning : quatre recettes sont réutilisées seize fois, avec le même plat aux dix déjeuners/soupers. Le contrat autorise les répétitions et n’exige pas vingt recettes distinctes ; cette grille reste toutefois insatisfaisante pour un usage professionnel. Le modèle avait lui-même signalé le manque de variété utile avant une révision qui a rempli la grille par répétition. Le produit reste `TESTE_NON_APPROUVE`.

Une observation précise des captures révèle une cause en amont : dix extraits de contenu visibles après le premier lot de lectures restent tous actifs dans la mémoire de travail de 128 éléments, mais aucun n’est visible dans le contexte du lot suivant. Ce n’est ni une suppression des documents ni une altération du texte ingéré. Le code remplace la liste prioritaire de lectures explicites par le dernier lot. La révision d’une réponse insuffisante perd également son explication initiale détaillée. Le correctif conserve maintenant une union ordonnée des identifiants de lectures explicites encore actifs, en donnant la priorité au dernier lot. Il retire les identifiants périmés avant le calcul de rétention et relit uniquement les objets canoniques actuels. Une lecture de fenêtre ne vide plus les autres lectures. La révision reçoit aussi le motif initial, limité à 2 000 unités UTF-16 sans couper un caractère, explicitement non fiable et non citable. Aucun plafond de contexte, nombre d’appels, règle de citation ou quota d’objets distincts n’est augmenté. La priorité ne garantit pas que tous les corps tiennent simultanément dans le budget ; les omissions et refus de capacité restent applicables.

Une preuve causale locale compile les quinze nouveaux cas contre l’ancienne DLL produit : quinze échecs reproduisent les pertes de lectures/motif. Après recompilation normale du produit corrigé, ces quinze cas passent, avec les deux protocoles OpenAI et des plafonds de 8 000 et 40 000 caractères pour la projection. La suite Backend Unit passe ensuite 3 933 cas, avec deux exclusions explicites, sur 3 935 ; les sources sont identiques avant/après. Les six suites officielles de validation hors ligne passent également. Les tests HTTP sont simulés et ne prouvent pas la qualité du prochain planning autonome. Le backend Linux doit encore être reconstruit et qualifié avant l’essai réel suivant. Preuves : `accumulated-explicit-read-recovery-validation-v1/` sous le même dossier de campagne.

La session fictive PAID11 a été supprimée une seule fois par l’API normale ; ses dépendances ont disparu et les deux conteneurs de test sont arrêtés. Une capture SQL32 séparée après suppression, datée du 30 octobre à 11:56:19 UTC, confirme les 32 tables documentaires, 77 migrations, quatre versions PDF et 510 pages inchangées, sans nouveau redémarrage ou OOM PostgreSQL. Coût calculé sur les usages de PAID11 : 0,06578832 USD ; cumul de campagne 0,33789588 USD, 37 charges et 37 réservations soldées, aucune ouverte. Ce calcul ne prouve ni la facture finale ni le solde fournisseur.

Preuves locales : `artifacts/meal-resume-20261029/planning-paid11-inspection-actual-v1/`, `private/paid11-semantic-review-v1/`, `private/paid11-ledger-v1/` et `private/bootstrap-paid11-cleanup-v1/`. Ces captures privées ne sont pas publiées sur Git.

### 2026-11-02 — Reprise et qualification gratuite W8 des lectures cumulées

Le correctif des lectures explicites et du motif initial est poussé au commit `50e0aaaa` ; GitHub Actions `41053016397` réussit. Le build Linux13 est vérifié sur PC : image, DLL Linux et inventaire de607fichiers cohérents. Seuls trois consommateurs du contexte changent face au build12 ; l'ingestion productrice reste identique. Ces changements seuls ne nécessitent donc pas de réingestion du corpus déjà vérifié.

À la reprise, le runtime gratuit préparé le30octobre n'a soumis aucune question : zéro session, tâche, trace et POST au fournisseur. Ses deux certificats de test avaient expiré pendant l'interruption. Une capture SQL32 indépendante puis un contrôle d'inactivité autorisent l'arrêt des deux seuls conteneurs inutilisés. Les certificats éphémères sont renouvelés avec la même durée de12heures, sans changement de confiance globale. Le nouveau stage11 conserve exactement les17autres fichiers et leurs permissions. Les trois épreuves TLS réelles passent : connexion valide, rejet d'une autorité inconnue et rejet d'un mauvais nom. Le runtime démarre à13:40:10UTC avec la DLL corrigée.

Une seule question gratuite W8 est ensuite soumise. Le Planner est enregistré ; les cinq autres réponses sont scénarisées. Les six échanges HTTPS se terminent en insuffisance contrôlée, conformément au scénario. Les77fichiers fermés sont téléchargés et vérifiés membre par membre. L'inspection réelle relie les huit opérations aux événements SQL : sept réussites et un refus de capacité correctement conservé. Elle vérifie les deux lectures explicites successives, leur maintien dans la mémoire active, la visibilité de leurs témoins canoniques, le motif initial transmis comme contexte non fiable et non citable, et l'historique natif borné. Les82fenêtres visibles correspondent au worker complet et à la projection canonique SQL. Cela qualifie ce scénario réel avec un faux fournisseur ; ce n'est ni un nouvel appel OpenAI ni une validation métier du planning.

La conservation indépendante avant nettoyage confirme32 tables documentaires,77 migrations,4PDF/510pages inchangés, avec une seule discussion fictive et sa tâche, sans ingestion active ni nouvel OOM ou redémarrage PostgreSQL. Deux sondes sans réseau vérifient aussi le ledger payant inchangé :37charges soldées, aucune réservation ouverte, cumul calculé0,33789588USD sur0,50USD. Ce calcul ne prouve pas le solde ni la facture fournisseur. Le nettoyage normal de la discussion W8 et sa capture SQL32 après suppression restent à exécuter à ce point, avant le prochain essai réel borné. TESTE_NON_APPROUVE maintenu.

Preuves : `artifacts/meal-resume-20261029/accumulated-read-free-inspection-actual-w8-v2/`, `private/free-w8-closed-20261102-v2/`, `private/free-w8-before-cleanup-20261102-v2/` et `private/free-w8-tls-recovery-20261102-v1/`.

La discussion gratuite W8 est ensuite supprimée une seule fois par l'API normale, avec GET404, cascades et audit+1 vérifiés. Les deux services possédés sont arrêtés. La capture SQL32 indépendante après nettoyage conserve32 tables,77 migrations,4PDF/510pages et zéro tâche, session, message ou ingestion active, sans OOM ni redémarrage PostgreSQL. Le rapport Telegram76 est envoyé le2novembre à13:56:00UTC, une partie, code de sortie0.

### 2026-11-02 — PAID12 refusé pour clé expirée, renouvellement et préparation PAID13

Le premier essai OpenAI après le correctif commence à14:01:10UTC et se termine à14:01:11UTC : une seule tentative HTTP, refus401 avec le code fournisseur `expired_secret_key`. Aucun outil documentaire n'est exécuté et aucun résultat modèle n'est produit. La page officielle du compte confirme que la clé SAAIA du25octobre a expiré le1novembre. Cet essai ne mesure donc pas la qualité du planning ni une limite de raisonnement du modèle.

Les29fichiers de preuve sont fermés puis revérifiés sur PC. L'inspection plusieurs tours conserve son résultat négatif attendu pour une seule requête refusée ; elle n'est pas transformée en succès métier. Le snapshot financier original conserve également son incertitude :38charges et38réservations closes, aucune ouverte, charge courante enregistrée0USD, mais absence d'usage fournisseur validé. Une qualification séparée recoupe ensuite le statut401 reçu, le code d'expiration, la trace, la sonde HTTP complète, l'identifiant de réservation et la charge `provider_http_rejected`, avec zéro jeton et zéro outil SQL. Elle classe ce seul rejet comme non facturable observé dans le ledger local, sans certifier une facture ou un solde fournisseur et sans réécrire les reçus antérieurs. La borne réservée initiale0,0099587USD reste dans les preuves. Les deux lectures sans réseau du ledger confirment37usages fournisseur et ce rejet, pour un cumul local inchangé de0,33789588USD sur0,50USD.

La seule discussion fictive PAID12 est supprimée normalement, avec audit+1 et cascades vérifiés ; le backend est arrêté et le faux fournisseur déjà arrêté est préservé. Une nouvelle capture SQL32 après suppression vérifie les mêmes32 tables,77 migrations,4PDF/510pages et zéro résidu ou incident mémoire. Les preuves de suppression, du corpus et du rejet sont liées par leurs empreintes réelles.

La clé est renouvelée dans le même projet, pour le même utilisateur, les mêmes cinq permissions restreintes et une durée de sept jours. Elle est importée dans le magasin Windows DPAPI puis transférée uniquement dans le fichier secret de qualification déjà autorisé. Les permissions et l'inode Linux sont conservés. Un GET de la liste des modèles réussit sur PC puis depuis le serveur, avec5.6Luna disponible et aucune génération demandée. Le contrôle automatique refuse la suppression de la copie locale temporaire ; celle-ci reste sous ACL restreinte et le secret n'a été ni affiché ni hashé. Ce refus ne bloque pas le parcours modèle.

Le runtime PAID13 démarre ensuite avec la même DLL, la même configuration signée, le même corpus et les mêmes limites. Ses admissions exigent les preuves fraîches du rejet, du nettoyage, du corpus, de l'authentification et du ledger. Un nouvel essai unique du même planning est lancé à14:41UTC ; son résultat, son coût et sa validation métier restent à examiner. Aucun achat supplémentaire. TESTE_NON_APPROUVE maintenu.

### 2026-11-02 — PAID13 : deux défauts de présentation confirmés dans les appels réels

Le vrai modèle termine en `insufficient_documentation`, sans affirmation produite, après cinq appels. Les quinze opérations SQL réussissent : huit recherches initiales et sept recherches exactes uniques. Les huit appels natifs comportent un doublon. Les 52 fichiers de preuves sont fermés et relus depuis leur archive sur PC ; les cinq enveloppes sont cohérentes et les 53 apparitions de preuves visibles correspondent au corpus Worker et à la projection SQL, pages, qualité et curseurs compris. Cette fidélité textuelle ne vérifie pas à elle seule la bonne association des réponses aux appels, ni l'équilibre des extraits choisis.

La relecture root des enveloppes confirme deux erreurs. Le code associait les appels natifs aux requêtes dédupliquées par leur position : le doublon recevait le résultat vide d'une autre recherche et la dernière recherche exécutée était présentée comme non exécutée à nouveau. En outre, les six recherches exactes non vides retrouvaient dix-huit extraits, mais les trois enveloppes suivantes ne montraient que les douze extraits des quatre premières recherches ; aucun extrait des deux dernières recherches n'était visible. Les identifiants restaient pourtant actifs. Le correctif vise une corrélation par identité normalisée et une répartition des lectures du nouveau lot avant l'application du même budget documentaire. Les corps canoniques restent actuels et ne sont pas mis en cache dans l'historique.

Un autre défaut est trouvé dans l'inventaire de candidats : le mode autorisant les répétitions calculait encore un déficit d'objets distincts. Cet inventaire est absent des cinq enveloppes PAID13 ; ce défaut latent ne peut donc pas expliquer cet essai. Le correctif préservera les vingt coordonnées demandées sans fabriquer de quota de vingt recettes différentes ni d'approbation de couverture. Les trois corrections et leurs régressions sont en préparation ; aucun nouvel appel payant n'est autorisé par ces seules observations.

Le coût issu des usages PAID13 est de 0,04548066 USD, pour un cumul de campagne de 0,38337654 USD sur 0,50 USD. Les 43 charges et réservations sont rapprochées, aucune réservation ouverte ; la facture et le solde fournisseur ne sont pas vérifiés. Le rapport Telegram77 est envoyé à15:00:53UTC, une partie, code0. La comparaison SQL32 avant nettoyage conserve les 32 tables, 77 migrations et quatre PDF/510 pages, sans OOM ni redémarrage. La seule discussion fictive PAID13 reste à supprimer par l'API normale puis à vérifier par une nouvelle capture SQL32. `TESTE_NON_APPROUVE` maintenu.

Preuves locales : `artifacts/meal-resume-20261029/private/paid13-closed-v1/`, `private/paid13-before-cleanup-v1/` et `session-state-v86.json`. Les corps de requêtes, documents, configurations et secrets restent hors Git et Telegram.

La fermeture PAID13 est ensuite exécutée réellement après revue indépendante de l'outil et admission locale des 52 fichiers fermés et huit références exactes. Une seule suppression API est vérifiée, avec audit+1, GET404 et disparition de la session, du job et de leurs cascades. Le backend possédé est arrêté, le faux fournisseur déjà arrêté est préservé, les deux identités et les sources restent inchangées. La comparaison séparée après nettoyage conserve les 32 tables documentaires, 77 migrations, quatre versions et 510 pages, avec zéro job, session, message, ingestion active ou expiration résiduel ; aucun nouvel OOM ou redémarrage PostgreSQL. Ces opérations n'appellent pas le fournisseur. Les reçus sont conservés dans `private/paid13-after-cleanup-v1/`.

La contrelecture des corrections écarte une reconstruction tardive des identités par un second parseur : elle risquait de diverger des normalisations et du ciblage documentaire utilisés à l'exécution. La conception retenue conserve plutôt, dès l'admission et avant toute rotation documentaire, la correspondance entre identifiant d'appel et requête canonique réellement admise, en réutilisant les parseurs existants. Le rendu et le calcul de budget lisent cette même correspondance ; aucun corps documentaire n'est stocké dans cette mémoire d'association. Les tests de régression et la vérification des bornes de source sont encore en préparation à ce point, sans nouveau test payant.

### 2026-11-02 — Corrélation native, équilibre des lectures et validation complète locale

La correspondance par identifiant d'appel réutilise la construction de requête et le parseur canoniques, puis intersecte leurs identités avec les requêtes effectivement admises. Elle est capturée dans le contexte initial, avant la rotation ou l'exécution SQL, dans la synthèse et le navigateur déjà existant ; aucun explorateur n'est activé. Les métadonnées de source capturées conservent aussi le contrôle tardif document/version/empreinte/chemin, indépendant des corps qui tiennent dans le contexte. Le normaliseur de chemin existant devient interne sans changer sa logique. Les anciennes fixtures de projection indiquent explicitement leurs appels admis ; aucun appariement par position ni parseur de secours n'est ajouté au produit.

La preuve causale finale compile les 43 nouveaux cas contre les produits HEAD50e : 31 échecs et 12 contrôles réussis. La version corrigée passe les 43, avec les deux protocoles, les huit appels et leur doublon, le dernier résultat vide, le ciblage documentaire, les sources croisées, les poignées sans corps visible, l'équilibrage des six groupes, les fenêtres Unicode et le contrat réutilisable. Le premier montage du scénario à huit appels était incomplet pour un chargement structuré ; seul ce montage est corrigé avant le rejeu causal, ses assertions de résultat restant identiques. Les sources corrigées sont restaurées exactement après chaque passage historique.

La première suite Backend Unit complète trouve ensuite 23 échecs : 19 régressions de place disponible causées par le nouveau feedback répétitif et quatre assertions textuelles confondant une consigne avec un véritable appel d'outil. L'audit confirme que le même rendu et les mêmes couches d'échappement sont déjà comptés au pré-calcul et à l'envoi final ; aucun nouveau contournement de plafond n'est trouvé. Les informations neutres répétées et la seconde consigne sont retirées. Les véritables omissions primaires gardent leurs identifiants/localisateurs bornés ; les fenêtres natives/secondaires conservent leurs diagnostics existants. Les compteurs complets restent dans la sonde finale, jamais dans les simulations de budget. Les quatre assertions vérifient désormais les appels et résultats structurés jusque dans le HTTP. Aucun plafond, texte canonique, curseur, état opaque ou assertion numérique n'est augmenté ou assoupli.

Après compaction, les 43 cas causaux réussissent de nouveau. Les 33 cas de régression et de contrôle de capacité réussissent également, puis la suite Backend Unit officielle passe 3 976 cas, zéro échec et deux exclusions explicites, sur 3 978. Les 789 fichiers backend de l'inventaire de cette vérification sont identiques avant/après ; ce compte inclut les sources de tests et n'est pas une mesure du logiciel livré. Les six suites officielles de planning hors ligne ont réussi, avec 198 cas Python dont une exclusion système explicite, sans service, binaire applicatif ni fournisseur. Le rapport Telegram78 est envoyé à15:56:22UTC, une partie, code0. Aucun nouvel appel OpenAI ; le cumul calculé reste 0,38337654 USD sur 0,50 USD. Le profil réel PAID13 utilise 65 536 caractères documentaires et 65 536 d'historique ; les 40 000 caractères cités dans certains contrôles sont une borne unitaire, sans modification implicite du profil réel.

Preuves : `artifacts/meal-resume-20261029/native-read-inventory-validation-v3/` pour le causal, les 33 régressions et l'Unit final ; `native-read-inventory-validation-v2/` conserve la première suite et les contrôles hors ligne. Le prochain build Linux, le scénario W9 avec faux fournisseur et le prochain planning autonome restent à exécuter. Ces tests simulés ne constituent pas une acceptation métier ; `TESTE_NON_APPROUVE` maintenu.

### 2026-11-02 — Build14 vérifié et refus gratuit W9 avant toute question

Le commit `e016b5c1` est poussé ; GitHub Actions `41478863854` réussit. Le build14 Linux contient ces sources et sa DLL a été relue et vérifiée sur le PC, avec l'inventaire de 607 fichiers. Aucun traitement d'ingestion ou de Docling n'est relancé par cette reconstruction des consommateurs du contexte.

Le premier démarrage W9 à 17:00 UTC échoue avant toute soumission API. Le backend démarre, puis le contrôleur constate l'arrêt du simulateur et arrête la paire de test. Le reçu expose `new_exact_created_image_required` parce que le simulateur ne fonctionne plus au contrôle de son état ; son journal précise la véritable cause : `recorded_planner_encoder_byte_identity_required`. Le fichier préparé pour rejouer le Planner était indenté, alors que l'encodeur gelé exige un JSON UTF8 compact exact. Ce refus porte sur le montage du test, sans nouveau comportement observé du modèle réel. Aucun owner réussi n'est produit, aucune question n'est soumise, aucune trace ou capture de requête fournisseur n'est créée.

La réparation conserve le fichier original de 585 octets et crée un dérivé de sérialisation de 399 octets avec le véritable encodeur du simulateur ; son objet JSON reste identique. Les deux empreintes sont liées dans le reçu local. Quatre tests purs supplémentaires et leur rejeu indépendant vérifient la vraie chaîne d'installation et le chargement CLI, avec accents et caractères Unicode. Ils refusent les formats incompatibles avant TLS ou démarrage serveur. La logique du simulateur et son contrôle d'identité restent inchangés.

La capture SQL32 séparée après échec confirme les 32 tables documentaires et les 510 pages inchangées, zéro tâche et session, aucun nouvel OOM ni redémarrage PostgreSQL. Les deux nouveaux conteneurs sont arrêtés et leur inspection complète est conservée. Un nouveau stage13 et trois répertoires de test vides sont préparés, sans modifier le stage12 échoué. À ce point la reprise gratuite n'a pas redémarré et le planning métier reste non validé. Cumul calculé inchangé : 0,38337654 USD sur 0,50 USD, sans prétendre vérifier le solde fournisseur.

Preuves privées : `private/backend-query-build-readback-v14/`, `private/native-correlation-free-w9-failed-startup-v1/`, `private/w9-failed-startup-conservation-v1/` et `private/native-correlation-free-w9-root-inputs-v2/`, sous `artifacts/meal-resume-20261029/`. Les preuves du refus sont conservées avant la nouvelle tentative.

### 2026-11-02 — W9 réel gratuit : corrélation et six groupes visibles validés

La reprise V5 démarre réellement à 17:23 UTC avec stage13, nouvelle paire de test et contrôles TLS réussis. Une seule soumission API suit à 17:25. Le scénario rejoue le Planner enregistré puis quatre réponses programmées ; il ne mesure pas la capacité autonome du modèle. Les quinze événements SQL réussissent : huit recherches générales et sept recherches ciblées, dont six avec trois résultats et la dernière avec zéro résultat. Le terminal contient cinq appels simulés, aucune affirmation et l'insuffisance volontaire attendue. API et SQL concordent.

L'inspection locale des captures retrouve deux hypothèses incorrectes dans les preuves de test. Le reçu de valeur par défaut du Critic était encore lié à un ancien inventaire compilé ; un nouveau reçu est dérivé de l'inventaire build14 effectivement relu, des deux fichiers de sources inchangés et de la configuration signée. La valeur 4096 et ses bornes restent identiques. L'inspecteur exigeait aussi un topK explicite dans le Planner, alors que le parseur produit applique 20 lorsque ce champ est absent ou non convertible en Int32. L'inspecteur V2 reproduit exactement cette valeur par défaut et le bornage 1–60 ; six tests purs et leur contrelecture couvrent le contrat. L'ancien inspecteur et les captures restent inchangés. Aucun Planner, résultat SQL ou corps documentaire n'est réécrit et aucune API n'est rejouée.

L'inspection officielle passe ensuite sur l'archive V2 transférée : huit appels natifs, sept opérations uniques et un résultat répété correctement associé ; la recherche vide est exécutée et correctement attribuée. Chacun des six groupes fructueux conserve au moins un véritable corps primaire visible. Les 47 fenêtres documentaires des cinq échanges correspondent au Worker et au corpus SQL. Les omissions bornées, le motif initial d'insuffisance, la révision, le Critic, les compteurs et les sondes passent. Le lien des sondes natives reste documenté comme trace et ordre de journal, sans prétendre posséder un hash direct de requête absent du runtime.

Les 88 fichiers de preuve sont archivés, transférés et vérifiés ; l'archive V1 et les premiers refus restent conservés. La session fictive est supprimée une seule fois par l'API normale, avec audit +1 et dépendances absentes ; seuls les deux conteneurs de test sont arrêtés. Le contrôle SQL32 séparé après nettoyage passe : 32 tables documentaires, 77 migrations, quatre versions et 510 pages inchangées, zéro tâche/session/message/ingestion active, aucun nouvel OOM ni redémarrage PostgreSQL. La clé existante est acceptée par un GET models frais, sans génération. Le rapport Telegram80 est envoyé à 17:48 UTC, une partie, code0.

Preuves : `native-correlation-free-inspection-actual-w9-v1/inspection-receipt-v1.json`, `private/free-w9-closed-20261102-v2/`, `private/free-w9-before-cleanup-20261102-v1/` et `private/free-w9-after-cleanup-20261102-v1/`, sous le même dossier de campagne. Le prochain essai OpenAI reste à lancer. Coût OpenAI calculé inchangé : 0,38337654 USD ; reliquat de cette campagne 0,11662346 USD, distinct du solde fournisseur. La fin W9 étant programmée, aucune acceptation métier du planning n'en découle : `TESTE_NON_APPROUVE` maintenu.


### 2026-11-02 — PAID14 : grille complète, chemin documentaire vérifié et limite de composition

Une seule nouvelle soumission du même planning est lancée à 17:59 UTC avec le vrai modèle OpenAI gpt-5.6-luna et build14. Elle se termine en `answered` après quatre appels : Planner, Writer, recherche complémentaire du Writer et Critic. Les vingt cases et vingt affirmations sont présentes ; six recettes et six références documentaires sont utilisées. Les seize événements SQL comprennent huit recherches générales et huit recherches ciblées : quinze réussissent ; la huitième recherche initiale conserve son refus explicite `accumulated_evidence_limit_exceeded`. Les huit recherches ciblées retournent respectivement 2, 3, 3, 3, 3, 3, 2 et 3 résultats. Ce refus initial ne désactive pas la recherche ultérieure.

Les 63 fichiers du parcours sont archivés, transférés et réinspectés sur PC. Les quatre enveloppes passent le contrôle Worker/SQL et leurs 34 apparitions de corps documentaires visibles sont conformes au corpus canonique. Les huit identifiants d'appel natif sont reliés aux bonnes sources, requêtes, opérations SQL et nombres de résultats dans les deux enveloppes suivantes. Le contrôle local initial avait confondu les identifiants non listés après huit avec les corps absents de la projection ; le contrôle V2 applique la sémantique exacte du produit : intersection avec les preuves actuellement visibles puis liste bornée à huit. Aucun message ni résultat capturé n'est réécrit. L'évaluateur mécanique passe les vingt coordonnées sans imposer vingt recettes distinctes, mais ne produit aucune approbation métier.

La vérification visuelle root des six pages physiques citées confirme l'existence et le titre des six recettes. Le placement dans le planning est bien une synthèse, et non une catégorie explicitement prescrite par le PDF. La faiblesse observée est la répétition : deux préparations sucrées alternent dans les dix petits déjeuners/collations, quatre recettes salées remplissent les dix autres cases. Une variété professionnelle satisfaisante et des repas équilibrés complets ne sont pas établis. Cela ne prouve ni une absence d'autres recettes dans le corpus, ni une incapacité du modèle : les appels réels proposent encore les quatre outils documentaires, 34 localisateurs sans omission et un budget de recherche restant. L'audit public identifie une contradiction probable entre l'objectif de variété utile et la consigne du Critic de préserver chaque choix sourcé sauf défaut documentaire ; une correction ciblée est en préparation, sans nouvelle boucle ou quota d'objets distincts.

Le coût calculé depuis l'usage courant est de 0,0440617 USD. Le cumul de campagne atteint 0,42743824 USD sur 0,50 USD, laissant 0,07256176 USD. Les 47 charges et 47 réservations sont rapprochées, aucune réservation ouverte. Ces preuves ne vérifient pas le solde ou la facture fournisseur. La discussion fictive PAID14 est ensuite supprimée une seule fois par l'API normale ; audit +1, GET404 et cascades absentes sont vérifiés. Le backend possédé est arrêté, le faux fournisseur arrêté est préservé. La capture SQL32 indépendante après suppression conserve les 32 tables, 77 migrations, quatre versions et 510 pages, zéro tâche/session/message/ingestion active, aucun nouvel OOM ni redémarrage PostgreSQL. Aucun nouvel appel payant pendant ces contrôles. `TESTE_NON_APPROUVE` maintenu.

Preuves : `private/paid14-closed-v1/`, `private/paid14-semantic-review-v1/`, `private/paid14-before-cleanup-v1/`, `private/paid14-after-cleanup-v1/` et `session-state-v98.json`, sous `artifacts/meal-resume-20261029/`. Les corps, documents et configurations restent hors Git et Telegram.


### 2026-11-02 — Correction de la politique de sélection du Writer et du Critic

La contradiction est corrigée dans la politique commune et dans les deux styles de consignes du Critic : les faits, conditions et citations valides restent protégés, mais une sélection correctement sourcée peut être remplacée par une alternative documentée lorsqu'elle améliore réellement l'utilité du livrable. Le relecteur peut utiliser les outils proposés pour lire les corps manquants. Les répétitions demandées ou utiles restent permises ; aucune obligation de vingt recettes distinctes, boucle supplémentaire, hausse de capacité ou modification de passerelle n'est ajoutée. Ce changement ne touche pas le producteur d'ingestion et ne nécessite donc pas de réingérer des documents inchangés.

Dix tests synthétiques exécutent le vrai parcours fournisseur avec un faux transport HTTP, dans les deux protocoles et les deux styles de prompt. Ils vérifient la conservation de répétitions explicitement demandées, la recherche ciblée d'une alternative par le Critic puis une substitution sourcée, la préservation du prérequis de cette alternative et des dix-neuf autres affirmations, ainsi que l'absence de recherche forcée ou de quota distinct lorsqu'un budget est épuisé. Les trois ou quatre appels effectivement consommés concordent avec le résultat. Les tests démontrent le transport de la politique et l'admissibilité du parcours ; ils ne simulent pas une amélioration autonome réelle du modèle.

La suite Unit backend complète passe : 3986 tests réussis, zéro échec et deux exclusions explicites, soit 3988 cas. Les dix tests ciblés finaux passent après le renforcement des assertions sur les dix-neuf unités non affectées. La compilation finale n'émet pas les trois avertissements relevés : le type non nullable est explicité après le filtre d'identifiant non vide dans l'inventaire, et deux assertions xUnit utilisent directement le prédicat de Single. Aucun comportement de sélection n'est modifié par ces nettoyages. La contrelecture indépendante valide les trois politiques ; la validation autonome du vrai modèle reste à effectuer. `TESTE_NON_APPROUVE` maintenu.

Preuves de tests : `artifacts/meal-resume-20261029/synthesis-purpose-validation-v1/unit-backend/manifest.json` et `focused-v2/manifest.json`. Aucun appel payant pendant cette correction.


### 2026-11-02 — BUILD15, GitHub vert et échec gratuit W10 conservé

Le commit 4bc94120bcf7986fdb357000fa04c5c276222023 est poussé. Le workflow GitHub Offline validation 41503214287 réussit entièrement. BUILD15 compile réellement les 607 fichiers sélectionnés ; la relecture de ses métadonnées et de sa DLL Linux passe. La comparaison avec BUILD14 retrouve exactement les trois fichiers de prompts et la correction de typage prévue dans l'inventaire. Les producteurs d'ingestion sont inchangés. Le profil13 ne modifie que la date et les identités du backend ; question, corpus, fournisseur, prix configurés, capacités et budget restent identiques.

Le stage14 conserve les 22 fichiers du stage13 avec leurs ACL, hormis le nouveau profil. J'ai toutefois mal préparé les répertoires de travail neufs : leur création générique en 0700 ne reproduisait pas les permissions particulières du journal et des traces. Le premier lancement W10 est refusé avant création de sortie, remplacement de conteneur ou requête modèle, sur la permission du journal. Le journal vide est corrigé vers le mode0770 et son ACL exacte, puis le runtime et ses contrôles TLS passent.

Une seule API gratuite est soumise ensuite. Elle échoue après réception du Planner programmé, avec advanced_development_trace_write_failed, tentative1 et résultat absent/null. Le SQL indépendant confirme un résultat null explicite et zéro outil documentaire. La capture HTTP indépendante contient une seule requête et sa réponse200 ; la réponse correspond exactement au Planner enregistré et ne résulte pas d'un modèle réel. Aucune trace C# n'est produite. Le diagnostic depuis le conteneur confirme uid0, capDrop ALL, montage déclaré RW, mais test d'accès en écriture refusé. Le dossier des traces v14 est en0700 sans ACL, alors que le modèle éprouvé v13 est en0770 avec ACL d'accès et ACL par défaut. Le dossier de capture reste correctement en0700 ; chaque rôle doit donc reprendre ses propres permissions, et non un mode unique.

Les 70 fichiers sont fermés puis transférés et revérifiés, pour 23402874 octets. L'inspection négative confirme le reçu API réel false, l'échec, une réponse programmée, zéro recherche et zéro trace ; aucune insuffisance réussie, revue Critic ou validation métier n'est inventée. Le SQL32 avant nettoyage conserve les32 tables documentaires,77 migrations,4versions et510pages, avec une seule session/tâche échouée et aucun incident mémoire ou redémarrage. Le contrôle préalable du nettoyage refuse les cinq annexes provenant des racines dédiées BUILD15 et source34 : aucune suppression n'a alors été tentée. Une admission limitée à ces cinq chemins, empreintes et tailles, uniquement pendant la vérification de l'archive, est en préparation ; les preuves originales restent intactes.

Preuves : private/free-w10-failed-v1/, native-correlation-free-negative-inspection-actual-w10-v1/inspection-receipt-v1.json et private/free-w10-failed-before-cleanup-v1/, sous artifacts/meal-resume-20261029/. Le coût réel OpenAI calculé reste0,42743824USD sur0,50 ; aucun appel payant depuis PAID14. Le rapport Telegram82 est envoyé à19:42UTC, une partie, code0. TESTE_NON_APPROUVE maintenu.

### 2026-11-02 — Vérification des traces avant réservation et requête fournisseur

L'audit public retrouve un défaut réel derrière l'erreur de préparation : ValidateDevelopmentTraceDirectory créait le répertoire sans vérifier la création d'un fichier par l'identité effective du backend. Le premier enregistrement réel arrivait après l'appel HTTP et le rapprochement du budget. L'état prêt pouvait donc être positif malgré un dossier non inscriptible. En mode de capture stricte, cela pouvait découvrir une impossibilité de diagnostic après une génération facturable.

Le correctif vérifie maintenant le dossier avant chaque réservation et chaque envoi HTTP : création exclusive d'un fichier temporaire unique, écriture d'un octet sans contenu documentaire, Flush puis suppression de ce seul fichier, sous le même verrou que les captures. Les sondes n'exposent ni chemin, ni clé, ni payload ; elles distinguent création, écriture, suppression, annulation, absence de requête et absence de réservation. L'attente annulée ne libère pas un verrou non acquis et ses diagnostics restent complets. L'état prêt réutilise cette vérification et peut redevenir positif après réparation du dossier. La capture désactivée et le mode non strict conservent leur comportement.

Les sept tests ciblés passent : refus préalable avec zéro HTTP et zéro réservation, panne entre Planner et Writer, traces exactes avec conservation des fichiers étrangers, capture désactivée, annulation du verrou et récupération de readiness. La suite Unit backend complète passe avec3993réussites, zéro échec et deux exclusions explicites, soit3995cas. Les sources restent stables durant les deux exécutions ; les empreintes des assemblies issues de la compilation ciblée et exécutées par la suite complète sont identiques. Le contrôle d'architecture passe. Cette sonde préventive ne garantit pas qu'un disque ou des permissions ne changeront pas après le contrôle ; le refus strict lors de l'écriture finale reste en place. La qualification Linux de cette nouvelle version reste à effectuer avant toute génération payante.

Preuves : artifacts/meal-resume-20261029/strict-capture-preflight-validation-v1/. Aucune modification de modèle, corpus, routage ou plafond ; TESTE_NON_APPROUVE maintenu.


### 2026-11-02 — Nettoyage W10 achevé, contre-exemple Linux W11 et cache de disponibilité corrigé

Le nettoyage V3 de W10 est exécuté une seule fois par l'API normale : audit +1, discussion et tâche fictives absentes, deux conteneurs possédés arrêtés, source34 inchangée. Le contrôle SQL32 après suppression passe : 32 tables documentaires, 77 migrations, quatre PDF et 510 pages conservés, zéro travail résiduel et aucun nouvel OOM ou redémarrage PostgreSQL. Les refus V1/V2 et les archives originales restent conservés.

BUILD16 est compilé depuis a38 et relu sur PC : source b09f6d92, nouvelle image et nouvelle DLL Linux réellement mesurées. Le runtime W11 démarre avec 22 fichiers de stage et les ACL copiées par rôle depuis la génération 13 ; les traces disposent aussi de leur ACL par défaut. Le contrôle de démarrage et les contrôles TLS passent. Aucune API de planning W11 n'est soumise et aucune génération n'a lieu.

Deux contre-épreuves retirent temporairement le seul accès ACL de uid0 aux traces. Elles restent explicitement échouées : le contrôle immédiat affiche 200 et un sink déclaré accessible malgré le masque effectif. Chaque finally restaure exactement les ACL d'accès et par défaut, le mode et l'identité du dossier ; READY revient à 200, les sept identités/états de conteneurs sont conservés et aucun POST de génération n'est capturé. La version observée conserve les seuls scalaires READY autorisés avant assertion. La date provider_sampled_at_utc reste identique aux trois requêtes tandis que leur date de réponse change : ReadyEndpoints réemploie pendant dix secondes le résultat ancien du sink sans nouvelle sonde filesystem. Le contrôle SQL32 après contre-épreuves passe avec zéro tâche/session/message/ingestion et conservation documentaire complète. Le runtime W11 inutilisé est ensuite arrêté, sans DELETE ni changement source34 ; sa fermeture de métadonnées est en préparation. Aucune réussite en cinq échanges ou acceptation métier n'est déduite de ces observations.

Le correctif transforme les deux branches de cache en rafraîchissement asynchrone. En capture stricte avec dossier configuré, ProbeProviderAsync s'exécute à nouveau avant ApplyRuntime ; le cache DB/TEI de dix secondes et le comportement non strict sont conservés. Les anciennes clés HTTP/error/scope sont retirées avant nouvelle observation. Quatre cas exercent HandleAsync existant, les deux branches du cache, un payload 503 replacé en cache puis la réparation immédiate, l'immutabilité du cache d'origine, une dépendance déjà défaillante et le mode non strict sans HTTP.

Le différentiel root compile ces nouveaux tests sur les deux fichiers runtime de a38 : deux cas échouent précisément sur attendu 503 / obtenu 200, deux contrôles passent. Les sources courantes sont restaurées byte pour byte en finally. La nouvelle compilation passe ensuite les onze tests de préflight strict ; la suite Unit backend utilisant exactement ces assemblies passe 3 997 cas, zéro échec, deux exclusions explicites, soit 3 999 au total. Les sources restent stables ; le contrôle d'architecture passe sur 2 127 fichiers. La contrelecture indépendante du correctif est GO. La qualification Linux du cache corrigé reste à effectuer avec BUILD17, puis le scénario documentaire gratuit et le prochain planning réel.

Preuves : artifacts/meal-resume-20261029/readiness-cache-validation-v1/, private/native-correlation-free-w11-root-inputs-v1/, private/w11-counterproof-after-v1/ et ready-cache-live-sink-fix-v1/. Le rapport Telegram 83 est envoyé à 20:41 UTC, une partie, code 0. Aucun nouvel appel payant depuis PAID14 : cumul calculé 0,42743824 USD sur 0,50 USD, distinct d'une facture ou d'un solde fournisseur. TESTE_NON_APPROUVE maintenu.

### 2026-11-02 — BUILD17 et W12 : contrôle Linux corrigé puis conservation après nettoyage

Le commit 8897fbfcd1fd1337cec320ab6ed870f72e2058dd est poussé ; le workflow GitHub Offline validation 41524263300 réussit. BUILD17 contient les deux corrections de disponibilité, sans changement de producteur d'ingestion. Les mêmes assemblages de tests passent 3997 cas, zéro échec et deux exclusions explicites. La compilation Linux et la relecture des 607 fichiers d'inventaire et de la DLL passent.

Le scénario gratuit W12 utilise réellement ce binaire. Le masquage du seul accès uid0 aux traces produit immédiatement HTTP 200 puis 503, puis 200 après restauration exacte des ACL et des métadonnées. Les trois échantillons sont distincts, sans redémarrage. Une seule soumission gratuite suit : cinq enveloppes programmées, quinze opérations SQL réussies, huit appels natifs dont un doublon correctement rejoué et une recherche vide correctement exécutée. Les 47 fenêtres visibles correspondent au Worker et au corpus SQL ; les six groupes fructueux conservent une projection primaire. L'insuffisance finale reste programmée et ne constitue pas un avis du vrai modèle. Les 92 fichiers sont archivés et vérifiés. La discussion fictive est supprimée une seule fois ; audit +1, cascades absentes et arrêt des deux conteneurs possédés passent. La comparaison indépendante après nettoyage conserve les 32 tables, 77 migrations, quatre documents et 510 pages, sans nouvel OOM ni redémarrage PostgreSQL.

### 2026-11-02 — PAID15 : huit recherches réussies, rédaction bloquée avant HTTP par la réservation de budget

Après rapprochement inchangé des 47 écritures antérieures et authentification GET models sans génération, le runtime Production PAID15 démarre avec BUILD17 et les consignes corrigées. Une seule API est soumise à 21:54 UTC. Le Planner réel gpt-5.6-luna répond en 5,292 s ; ses huit recherches documentaires réussissent. La requête Writer est préparée, puis refusée avant réservation et avant tout HTTP par `advanced_external_budget_campaign_hard_limit`. Elle compte 118249 caractères, dont 64306 caractères documentaires sous le plafond de 65536 ; sa projection contient 9168 caractères de texte visible. La réservation financière majore l'entrée par son volume UTF-8 plus 256 tokens et prévoit la sortie maximale de 8192 tokens. La limite observée est locale à cette campagne ; aucun épuisement du solde fournisseur n'est démontré.

Le seul appel réel coûte 0,0020344 USD selon l'usage fourni. Les 48 charges et 48 réservations se rapprochent, aucune réservation ouverte ; cumul 0,42947264 USD sur 0,50 USD, reliquat 0,07052736 USD. Ces chiffres ne vérifient pas la facture fournisseur. Les 35 fichiers sont fermés, transférés, vérifiés et réinspectés. L'inspecteur positif conserve ses cinq refus : une seule trace Planner, deux sondes dont le Writer sans HTTP ni réservation, et aucun échange final montrant les huit résultats SQL au Writer. `unobserved_final_sql_tool_prefix` ne permet donc pas de certifier une projection Writer inexistante. Aucun planning n'est produit, aucun résultat d'acceptation métier n'est promu. Qualification du diagnostic et nettoyage normal de cette seule session en cours ; aucune nouvelle génération autorisée par ce paragraphe. TESTE_NON_APPROUVE maintenu.

Le diagnostic séparé est ensuite exécuté sur l'archive complète : il lie les cinq refus à l'unique Planner facturé et à la sonde Writer refusée avant HTTP, sans promouvoir les indicateurs documentaires. La suppression normale de la seule discussion fictive est exécutée une fois ; audit +1, GET404, cascades et arrêt du seul backend sont vérifiés, faux fournisseur déjà arrêté préservé. La capture après nettoyage et sa comparaison indépendante passent : 32 tables, 77 migrations, quatre documents et 510 pages inchangés, aucune tâche/session/message/ingestion active, aucun nouvel OOM ni redémarrage PostgreSQL. Les quatorze fichiers du nettoyage sont archivés, transférés et vérifiés. Le rapport Telegram85 est envoyé à 22:12 UTC, une partie, code0.

Une mesure hors ligne sur une requête Writer PAID14 conservée identifie un coût évitable d'encodage : sérialiser seulement l'enveloppe HTTP avec l'encodeur JSON compact réduit 119409 à 101010 octets UTF-8, soit 18399 octets, tout en conservant une égalité profonde du JSON décodé. Il ne s'agit ni d'une baisse prouvée des tokens facturés, ni d'une validation d'un nouveau planning. La correction produit et ses tests sont en cours : encodage extérieur uniquement, capture de la chaîne exacte envoyée, mesure du documentaire avec un encodage cohérent, sondes numériques de réservation refusée depuis le même instantané financier verrouillé. Aucun plafond ni texte documentaire n'est réduit.

### 2026-11-02 — Encodage HTTP compact, captures exactes et diagnostic des refus budgétaires

La correction conserve la sérialisation interne des prompts et le calcul conservateur utilisé pour sélectionner les extraits. Seule l'enveloppe HTTP utilise une copie mise en cache des options avec un encodeur JSON compact. La chaîne sérialisée exacte sert à l'envoi, au SHA, à la réservation financière et aux captures, y compris lors d'une erreur HTTP. Le garde documentaire final et la sonde de collection appliquent le même encodeur extérieur à leurs deux termes, sans soustraire une baseline d'ancien format. Les plafonds numériques et les règles documentaires restent identiques.

Les refus de coût par tâche, de nombre d'appels et de plafond de campagne portent désormais les bornes tentées et les soldes au moment du refus, depuis le même instantané financier verrouillé. Treize scalaires numériques autorisés et le marqueur de réservation non créée sont copiés vers les sondes ; aucun corps, clé, champ arbitraire d'exception ou identifiant de réservation fictif n'est ajouté.

Neuf nouveaux cas ciblés passent : trois seuils financiers, une liste fermée de champs et cinq contrôles du transport dans les deux protocoles, dont un historique d'outils natifs. Le corps HTTP, le texte décodé, les caractères spéciaux, les pages, les métadonnées, les captures et leurs SHA, les sondes et la réservation UTF-8 concordent. La contre-épreuve sur les quatre anciennes implémentations fait échouer les huit cas causaux, sans erreur ni timeout ; les fichiers corrigés sont restaurés octet par octet dans finally. Le contrôle par réflexion du nouveau helper est distingué de cette preuve causale.

La première suite complète relève dix-sept attentes devenues incorrectes après réduction du volume encodé. Le fitting conservateur est séparé pour préserver la sélection antérieure, puis les fixtures des vrais gardes de volume sont recalibrées sur leurs deux requêtes compactes témoins : elles continuent à prouver un refus avant HTTP au-dessus de la frontière et la préservation intégrale en dessous. Aucune assertion de qualité ni ligne entière n'est retirée. Les 31 cas ciblés finaux passent sans avertissement. La suite complète sur le même assemblage passe 4007 cas, zéro échec et deux exclusions explicites, soit 4009 cas ; sources stables pendant les deux exécutions. Le contrôle de structure passe sur 2131 sources, avec les 21 revues de taille déjà existantes. Aucun nouvel appel payant depuis PAID15. La qualification Linux du nouveau binaire et le prochain planning réel restent à effectuer ; TESTE_NON_APPROUVE maintenu.


### 2026-11-02 — BUILD18/W13 : encodage exact confirmé, métrique NAV incohérente détectée

Le commit 63c44217f42cfe7c099a5757f3bb012d6e754394 est poussé ; Offline validation 41539049652 réussit. BUILD18 compile et sa DLL/inventaire sont relus. Le préflight réel de capture stricte fait 200/503/200 sous retrait/restauration du seul accès concerné, sans redémarrage. Une seule soumission gratuite W13 termine : cinq échanges programmés et quinze opérations SQL réussies. Cette insuffisance finale est imposée par le faux fournisseur et ne valide pas le planning métier.

Le premier export ROOT pointait par erreur vers les répertoires provider/traces v16 alors que le runtime W13 montait v17. L'inspecteur gelé refuse correctement la divergence. L'archive incorrecte reste conservée ; aucun octet n'est réécrit. L'export V2 corrige ces chemins et lie désormais les racines aux montages du compose, les identités terminal/API/session et chaque trace au readback indépendant. La contrelecture et dix contrôles synthétiques passent. Les 93 fichiers du nouvel export sont transférés et leurs tailles/empreintes vérifiées. Les cinq requêtes HTTP brutes correspondent exactement aux cinq traces et aux cinq sondes du job courant : encodage compact effectif, aucun appel payant.

Le contrôle suivant refuse navigation_wire_delta_or_cap_changed : dans chacun des quatre appels documentaires, la métrique NAV déclare 6166 caractères contre 4678 réellement encodés, sous le plafond inchangé de 8000. Le constructeur NAV utilise encore l'ancien calcul d'échappement. Il s'agit d'un défaut de métadonnée/sonde, pas d'une disparition observée de texte. Un diagnostic distinct conserve passed:false et les corrélations mécaniques vérifiées ; aucune qualification positive ni acceptation métier n'est fabriquée. La correction de la métrique et sa contre-épreuve sont en cours.

Rapport Telegram 86 envoyé à 23:10 UTC, une partie, code 0. Cumul mesuré inchangé : 0,42947264 USD / 0,50 USD, 48 écritures réglées et zéro réservation ouverte au dernier relevé. Le solde fournisseur n'est pas vérifié. La borne conservatrice de réservation ne garantit pas qu'un nouvel essai réel puisse terminer ses recherches et son contrôle final avec les 0,07052736 USD restants ; aucun nouveau Planner payant n'est engagé avant correction et décision financière explicite. TESTE_NON_APPROUVE maintenu.


### 2026-11-02 — Correction NAV : contre-épreuve causale et suite backend complète

Le constructeur conserve exactement la sélection historique des locateurs et son plafond, puis recalcule seulement la métrique exposée avec l'encodeur réellement utilisé dans HTTP. Les titres, pages, instructions, limite de 128 locateurs et budget de sélection restent identiques. Les trois usages du helper d'échappement historique sont audités : les deux mesures NAV sont corrigées ; le fitting documentaire par item reste volontairement conservateur. Le helper d'assertion antérieur est aligné sur l'encodage HTTP.

Six nouveaux cas vérifient deux protocoles aux limites de 8000/64000 caractères, les caractères spéciaux, le fragment présent dans le vrai corps HTTP, l'égalité métadonnée/sonde, et l'omission entière d'un titre qui entrerait dans le volume compact mais dépassait la frontière de sélection antérieure. Les dix-neuf tests NAV passent. La contre-épreuve remplace le seul runtime NAV par celui de 63c442 : les quatre cas de mesure échouent précisément sur 1211 attendus / 1403 annoncés. Le runtime corrigé est restauré octet par octet en finally et les dix-neuf cas repassent après compilation.

La suite Unit backend utilisant les mêmes assemblages passe 4013 cas, zéro échec, deux exclusions explicites ; les empreintes source et assemblages concordent avec l'exécution ciblée finale. Le contrôle de structure passe sur 2132 sources avec 21 revues de taille existantes. La contrelecture indépendante des snapshots est GO. Preuves : artifacts/meal-resume-20261029/nav-wire-metric-validation-v1/ et admitted-card-navigation-wire-source-peer-v1/. Cette correction reste à qualifier dans Linux ; aucun nouvel appel payant, aucune approbation produit.


### 2026-11-02 — W13 nettoyé, BUILD19 relu, audit élargi à l'historique natif

Le W13 non qualifié conserve son diagnostic passed:false. Une branche de nettoyage distincte, contre-lue avec 16 contrôles purs, lie ce diagnostic aux cinq corps exacts, à l'archive de 93 fichiers et aux quinze opérations SQL. Elle n'attribue aucune fidélité documentaire positive. Le seul DELETE normal supprime la discussion fictive ; audit +1, API404, cascades absentes et arrêt du couple possédé sont vérifiés. Le post SQL32 passe : 32 tables, 77 migrations, 4 documents et 510 pages inchangés, zéro job/session restant, aucun nouvel OOM/redémarrage. Les 14 fichiers de preuve du nettoyage sont fermés, transférés et vérifiés. Une nouvelle capture distincte de garde W14, horodatée 23:27:55 UTC, passe également la comparaison ; aucune opération payante.

BUILD19 compile le correctif NAV 2e0f347 et la relecture Linux vérifie la DLL, les 609 fichiers, l'identité de l'image et son utilisateur. La première commande de lancement avait placé ses propres journaux dans le dossier qui exige exactement quatre entrées : le garde refuse avant compilation. Les trois fichiers sont conservés hors de ce dossier et le lancement corrigé réussit, sans modifier les quatre entrées. Aucune application BUILD19 n'est démarrée ; ce binaire ne constitue pas une qualification du parcours.

Avant le prochain runtime, l'audit ROOT examine les mesures qui suivent NAV dans les preuves W13. Les trois historiques réellement capturés font chacun 22129 caractères UTF-16 contre 28516 annoncés par advanced.native_tool_results. NativeResearchTools et llm.call emploient encore l'ancien encodage pour leur champ history_characters. La correction conserve les calculs conservateurs de fitting et leur limite ; elle doit distinguer ce fitting de la taille HTTP réelle. L'audit public exhaustif classe 17 groupes de métriques et une contrelecture indépendante confirme le périmètre : ces deux champs sont les seuls compteurs HTTP finaux restants identifiés ; les autres tailles décrivent le JSON interne, le texte décodé ou une limite conservatrice explicite. Preuves : outer-wire-volume-probe-public-audit-v1/, wire-budget-trace-audit-build20-v1/ et private/free-w13-closed-v2/history-metric-audit-v1.json. BUILD20 et W14 restent à qualifier après ce correctif ; aucune acceptation métier ni nouveau planning réel n'est revendiqué.


### 2026-11-02 — Historique natif : sondes HTTP exactes, fitting inchangé et contre-épreuves indépendantes

Les deux champs history_characters utilisent maintenant le même encodeur extérieur que la requête HTTP. Le champ séparé history_fitting_characters expose le calcul conservateur antérieur. Le calcul qui commande la sélection, le refus, son code, les Error.Data et l'outcome restent inchangés ; aucune limite ni quantité documentaire n'est élargie. L'audit des 17 groupes de mesures et sa contrelecture ne trouvent pas d'autre compteur HTTP final utilisant encore l'ancien encodage dans ce périmètre.

Les quatre tests vérifient les deux protocoles, l'historique réellement présent dans le corps capturé, les compteurs des outils et de llm.call, le contenu et les chemins Unicode/guillemets, puis une frontière où le format compact entrerait mais où le fitting historique doit toujours refuser avant un autre HTTP. La première fixture synthétique sérialisait le DTO avec la casse par défaut : les mesures passaient, puis l'assertion de contenu échouait. La fixture est corrigée vers les options Web/UnicodeAll réelles, sans supprimer l'assertion. Ce premier échec et le retrait du GO statique V1 restent archivés ; le gel et la contrelecture V2 sont distincts.

Les quatre cas V2 passent. Deux contre-épreuves remplacent successivement le seul ancien fichier NativeResearchTools, puis le seul ancien fichier principal : quatre échecs d'assertion pour chaque sonde, sans erreur de compilation, tandis que l'autre fichier reste corrigé. Les deux sources sont restaurées octet par octet en finally ; les quatre cas repassent après compilation. La suite complète sur exactement les mêmes assemblages passe 4017 cas, zéro échec et deux exclusions explicites, soit 4019 cas. Empreintes de source stables et contrôle de structure réussi sur 2133 sources/21 revues existantes. Preuves : native-history-wire-validation-v1/ et native-history-wire-source-peer-v2/. Le commit précédent du correctif NAV, 2e0f347, est aussi validé par GitHub Offline validation41545001154/job125618323179. Aucun nouvel appel payant ; qualification Linux du nouvel ensemble encore à effectuer, produit TESTE_NON_APPROUVE.

### 2026-11-03 — BUILD20/W14 : parcours gratuit complet, puis nettoyage et SQL32 validés

Le commit d85ecbb est poussé et Offline validation41548125809 réussit. BUILD20 est compilé sous Linux ; les 609 fichiers, la DLL et l'image sont relus. Le runtime W14 passe la contre-épreuve de capture 200/503/200 avec restauration exacte des accès, puis une seule soumission au faux fournisseur. Ses cinq requêtes HTTP sont identiques aux traces et aux sondes du job courant. Les quinze opérations SQL réussissent ; les 47 fenêtres documentaires contrôlées correspondent aux projections canoniques. La navigation expose les 29 titres/pages attendus et ses 4678 caractères concordent avec le corps ; les historiques exposent 22129 caractères réels et distinguent le fitting conservateur de 28516.

Les omissions bornées restent explicitement signalées : cette preuve ne prétend pas transmettre tout le corpus dans chaque requête. Le résultat final d'insuffisance est programmé par le faux fournisseur ; il ne démontre ni une décision d'un vrai modèle, ni la qualité d'un planning. Les 93 fichiers de preuve sont fermés, transférés et vérifiés. Une seule suppression normale de la session fictive réussit ; ses dépendances disparaissent, l'audit de suppression augmente d'une unité et les deux conteneurs possédés sont arrêtés. La comparaison finale conserve les 32 tables documentaires, 77 migrations, 4 PDF et 510 pages, sans job/session restant, OOM ou redémarrage nouveau. Les 14 fichiers de preuve de nettoyage sont également archivés et vérifiés. Preuves : native-correlation-free-inspection-actual-w14-v1/ et private/free-w14-cleanup-closed-v1/.

La cause du dernier refus financier est revue séparément : le plafond cumulatif de 0,50 USD provenait d'une enveloppe initiale choisie par l'agent. Il ne prouvait pas l'épuisement des crédits du fournisseur. Dans l'autorisation humaine existante d'utiliser les crédits déjà achetés, ROOT choisit une nouvelle sous-limite cumulative de 1,00 USD, sans achat. Le budget signé de campagne devient 0,99321314 USD après le coût historique hors ledger ; le plafond par job reste 0,49321314 USD. Les 48 écritures historiques et leurs anciens plafonds sont conservés. Le nouveau couple de configuration est signé puis vérifié avec succès et reste distinct de la configuration gratuite antérieure. Aucun nouveau planning payant n'est encore exécuté à ce stade ; les admissions du runtime courant restent requises. TESTE_NON_APPROUVE maintenu.

### 2026-11-03 — PAID16 : réponse réelle, fidélité des textes et cause de la perte des alternatives

Après signature vérifiée et admission des 22 références, le nouveau profil démarre BUILD20 avec la même génération économique GPT-5.6 Luna. Un premier démarrage avait refusé avant toute mutation : le répertoire vide des traces payantes n'avait pas été préparé. ROOT copie les permissions du rôle payant précédent, vérifie les deux conteneurs toujours arrêtés et lance une nouvelle tentative distincte ; ce démarrage réussit. Les journaux de refus restent conservés. Une seule soumission réelle termine en 52,6 secondes : cinq appels au modèle, vingt-quatre opérations documentaires réussies, vingt propositions, neuf références documentaires et huit recettes distinctes. La grille fonctionne ; la répétition de journées entières laisse sa qualité professionnelle à améliorer, sans imposer un quota artificiel de vingt recettes différentes.

Les 60 fichiers du test sont fermés, transférés et réinspectés. Une vérification supplémentaire indépendante rapproche les 44 corps visibles des cinq échanges avec le Worker et le snapshot SQL canoniques ; elle passe. Les traces de requêtes sérialisées restent distinguées d'une capture HTTP payante indépendante. Le rapprochement financier conserve les 48 lignes historiques puis les cinq nouvelles, sans réservation ouverte : coût calculé au barème prudent de 0,0567575 USD et cumul de 0,48623014 USD, sans prétendre une facture fournisseur. Le nettoyage normal supprime une seule fois la session fictive, vérifie les cascades et l'audit, puis arrête le backend ; le faux fournisseur reste arrêté. Le post SQL32 conserve les 32 tables, 77 migrations, quatre PDF et 510 pages, avec zéro job/session restant. Les 14 fichiers du nettoyage sont fermés et vérifiés séparément.

Le readback prouve la cause de contexte : les huit extraits alternatifs visibles après le premier lot sont toujours parmi les 128 éléments actifs après le second, mais aucun n'est encore visible pour la rédaction finale. Il reste pourtant onze appels au modèle et quarante opérations documentaires possibles. Le code place tous les résultats du lot récent avant les lectures précédentes ; le projecteur remplit ensuite son budget dans cet ordre. La politique de variété existe déjà et les outils ont effectivement été utilisés. Cette priorité peut donc limiter la sélection au dernier lot ; une amélioration sémantique du prochain résultat réel reste à démontrer.

### 2026-11-03 — Lectures successives : priorité corrigée et contre-épreuve causale

EvidenceReading conserve d'abord le premier résultat distinct de chaque requête courante, puis alterne les anciens identifiants encore actifs et les résultats secondaires courants. La lecture ciblée reste prioritaire. Aucun corps historique n'est mémorisé ; les identifiants sont toujours résolus sur le contenu canonique actuel. Les plafonds, le fitting conservateur, le dernier lot de l'historique natif, les curseurs et les règles de citation restent inchangés. La projection séquentielle conserve sa limite : une entrée trop coûteuse peut encore empêcher l'affichage des suivantes ; la correction ne promet pas que les 128 éléments tiennent ensemble.

Huit tests couvrent l'ordre, l'expiration, le remplacement canonique, les résultats vides et les deux protocoles HTTP avec caractères échappés. L'oracle indépendant mesure le budget documentaire avec sa baseline et prouve que les six premiers résultats courants, une ancienne alternative et le second résultat utile d'une recherche peuvent coexister, alors que tout le nouveau lot ne tient pas. La première fixture échoue avant cet oracle car son propre contexte est trop volumineux ; les quatre échecs et le retrait de la revue statique V1 restent conservés. La fixture V2 construit ses témoins depuis les enregistrements canoniques individuels et conserve toutes les assertions de contenu et de limite. Les huit cas passent réellement.

La contre-épreuve remet uniquement l'ancien fichier produit : les quatre cas HTTP échouent sur l'ancienne alternative absente, sans erreur ni timeout. Les quatre fichiers corrigés sont restaurés octet par octet, puis les huit cas repassent après compilation. La suite complète sur les mêmes assemblages et la même empreinte des sources passe 4025 cas, zéro échec et les deux exclusions explicites existantes, soit 4027 cas. Le contrôle de structure passe sur 2134 sources avec les 21 revues existantes. Preuves : cross-batch-explicit-read-validation-v1/, cross-batch-explicit-read-source-peer-v2/ et private/paid16-all-role-fidelity-v1/. Aucun nouvel appel payant après PAID16 ; la qualification Linux du correctif et son effet sur le vrai planning restent à effectuer. TESTE_NON_APPROUVE maintenu.


### 2026-11-03 — Qualification FREE W15 du contexte inter-recherches (BUILD21)

Le nouveau binaire correspond au commit `fac9dba65cd41a1dc845e97b2272e68f84180e29`, identité source `dc80e434a1bb7dcf52384ee172b50aa2d78ef27dc5f24e419d5c3d93273b55a4`. Le seul fichier de production modifié depuis BUILD20 est `OpenAiCompatibleAdvancedAnalysisProvider.EvidenceReading.cs` ; le producteur d'ingestion et le corpus vérifié sont identiques. Cette correction de sélection du contexte n'appelle donc pas une nouvelle ingestion.

Exécution réelle du banc isolé, fournisseur **simulé** : configuration FREE signée C6, profil20, stage20, runtime FREE20. Le certificat temporaire de l'ancien banc expirait ; un certificat de test neuf a été généré pour le seul fournisseur simulé, sans changer la confiance système. Le profil conserve l'enveloppe de campagne cumulative de 1 USD ; la configuration FREE effectivement exécutée reste plafonnée à 0,49321314 USD. La configuration PAID conserve séparément campagne0,99321314 / job0,49321314.

La contre-épreuve des traces a réellement observé disponibilité200 → indisponibilité503 quand l'accès d'écriture est masqué → disponibilité200 après restauration. ACL, métadonnées et conteneurs sont conservés ; aucun job ni appel de génération pendant ce contrôle.

Le job fictif `aa4b2ec4-ef45-4e16-833e-6ee8420a8779` et sa session `29200769-43ef-4e82-b08f-9fd595133692` terminent correctement. Capture HTTP au faux fournisseur et readback indépendant : cinq requêtes, 47 fenêtres documentaires comparées aux données canoniques Worker et SQL, 15 opérations SQL réussies sur15. Les huit appels natifs correspondent à sept opérations uniques ; le rejeu du doublonC, le résultat videG, la projection et les omissions bornées passent. La réponse d'insuffisance est programmée dans ce scénario : elle ne valide pas le raisonnement ni la qualité métier d'un modèle réel.

Les captures ont été fermées dans une archive de94 fichiers, transférées puis toutes rehashées. L'inspecteur documentaire W9 est conservé ; un petit adaptateur W15 corrige uniquement trois clauses de contexte financier devenues historiques (campagne1/.993 au lieu de.50/.493). Le plafond FREE.493 et les gardes documentaires sont inchangés. Preuves : `artifacts/meal-resume-20261029/native-correlation-free-inspection-actual-w15-v1/inspection-receipt-v1.json`, `private/free-w15-closed-v1/transfer-verification-v1.json` et `private/native-correlation-free-w15-root-inputs-v1/`.

Avant nettoyage, la comparaison SQL32 retrouve les32 tables documentaires inchangées, 77 migrations, quatre documents/510 pages, aucun OOM ni redémarrage PostgreSQL. La suppression normale de l'unique session fictive est lancée après admission des dix références fermées ; son résultat et la conservation après nettoyage restent à vérifier à cette entrée.

Aucun nouvel appel payant depuis PAID16. Cumul local conservateur0,48623014USD /53 écritures et zéro réservation ouverte au dernier relevé ; la continuité sera relue avant PAID17. Le relevé local n'est pas une facture ni le solde fournisseur. Rapport Telegram89 envoyé avec succès le2026-11-03 à01:51:21Z. Prochain objectif : mesurer l'effet de la conservation inter-recherches sur un vrai planning économique. **TESTE_NON_APPROUVE** maintenu.


### 2026-11-03 — PAID17 : refus réel expliqué, fidélité séparée de pertinence

Le nouveau test réel OpenAI `gpt-5.6-luna`, sur BUILD21/commit `fac9dba`, termine en 34,224 secondes, cinq appels. Job `e922ddda-ecb6-4612-8c80-adcc4840fd10`, session `059da16d-4c7f-4509-a7c0-5cb89aa4d2a0`. Le traitement est `succeeded`, mais son résultat est **insufficient_documentation**, zéro claim/preuve finale : le planning reste refusé. Les réponses Writer, revue et Critic contiennent réellement cette insuffisance ; aucun fallback backend n'a transformé un planning valide en ce résultat.

Préparation : stage21 ne change que le profil ; les21autres fichiers/configurations signées/TLS/ACL restent identiques. Une erreur ROOT de dérivation du compose laissait les deux montages de configuration sur stage19. Le moteur a refusé avant création du runtime, accès clé ou mutation ledger. Les trois entrées refusées sont conservées localement et dans `OP/paid17-prelaunch-refused-mount-v1`. Une vérification hors ligne des montages réels a été ajoutée ; le lancement corrigé passe avec manifeste `e9c50f21fccf55fbca63ec9d8a7c8a26a0e28fe29a00b4c294e53d6d63a97d31`. Aucun job supplémentaire n'a été soumis pour cette correction opérateur.

Les captures actuelles et leur readback sont fermés en51 fichiers, tous transférés/rehashés. L'inspection indépendante est cohérente. Les59 fenêtres visibles des cinq rôles correspondent au Worker et au snapshot SQL ; source/qualité/offsets restent exacts. Il s'agit des requêtes sérialisées backend, sans capture réseau indépendante de l'API payante. **Fidélité des textes ne vaut pas pertinence de leur sélection.**

Deux défauts observés : le planner a prévu huit recherches. La septième tentative a échoué avec `accumulated_evidence_limit_exceeded` et la huitième n'a pas été exécutée. La rotation n'est activée qu'au premier batch du Writer ; elle ne résout donc pas cette saturation du bootstrap. Puis le Writer a demandé quatre lectures de pages d'une seule source. La projection suivante expose14 fragments de ces pages ; aucun des17 extraits initialement montrés n'y subsiste. Certains corps ne font que11 ou 16 caractères. Le catalogue conserve les locateurs et tous les textes contrôlés sont exacts, mais la lecture d'une source a pris la priorité sur les corps initiaux.

La revue finale disposait encore de 11 appels modèle et53 opérations documentaires, près de 297 secondes de budget documentaire et `researchAllowed=true`. Le contrat transmis autorise la réutilisation et n'exige pas20 objets distincts. Le modèle invoque néanmoins un manque de 20 éléments au lieu de compléter sa recherche par besoin précis. Ce comportement n'est pas une preuve d'absence documentaire dans le corpus.

Comptabilité réellement réconciliée : cinq nouvelles lignes d'usage, coût 0,04706534 USD ; les53 lignes historiques restent byte-identiques. Ledger 58 charges/58 réservations, zéro réservation ouverte. Ledger0,52650862 +base hors ledger0,00678686 =cumul0,53329548USD ; reliquat de l'enveloppe cumulative1USD :0,46670452USD. Facture/solde fournisseur non réconciliés ; TESTE_NON_APPROUVE maintenu.

Preuves principales : `artifacts/meal-resume-20261029/private/paid17-runtime-preparation-v2/context-observation-v1.json`, `private/paid17-closed-v1/`, `private/paid17-all-role-fidelity-v1/receipt-v1.json`. Avant nettoyage, SQL32retrouve les32 tables documentaires inchangées/77 migrations/4 PDF / 510 pages, sans OOM/redémarrage. Le nettoyage normal de cette seule session fictive est lancé ; conservation après nettoyage à confirmer. Deux corrections produit restent hors ligne/en cours : répartition bornée du topK entre recherches initiales, et priorité de la projection précédente séparée des références épinglées. Aucun nouvel essai payant avant leur diagnostic/validation. Telegram90 envoyé à02:26:21Z, uniquement sur ces nouveautés.


### 2026-11-03 — Nettoyage PAID17 achevé et deux correctifs causalement validés

Le nettoyage normal PAID17 a réussi : une seule suppression API, GET404, audit+1, cascades absentes, backend arrêté et fournisseur simulé déjà arrêté conservé. La comparaison SQL32 après nettoyage passe : zéro travail/session de test, 32 tables documentaires inchangées, 77 migrations, quatre PDF et 510 pages ; aucun OOM ni redémarrage PostgreSQL. Reçus : `private/paid17-after-cleanup-v1/cleanup-receipt.private.json` et `documentary-conservation-v1.json`. L'inspection complète de la paire arrêtée conserve les identités, configurations, montages et dates de démarrage.

Le bootstrap répartit désormais le topK sur les places disponibles et les recherches restantes. Les requêtes et leurs périmètres sont conservés ; chaque exécution reste comptée. La sonde `advanced.bootstrap_search_bound` indique topK demandé/effectif et capacité, sans corps documentaire. Les preuves canoniques restent admises intégralement, dans le même plafond de 128 références.

La projection précédente donne un ordre de préférence souple aux IDs encore canoniques. Aucun corps historique n'est stocké et aucun pin supplémentaire n'est créé. Les lectures explicites et fenêtres ciblées gardent leur priorité. Un élément rejeté par le workspace ou l'inventaire perd ce bonus souple, tout en restant accessible dans le canonique. Cette priorité améliore la coexistence contrôlée ; elle ne garantit pas que tout l'historique tienne ni sa pertinence métier.

Preuve causale ROOT : huit cas de reproduction échouent avec les quatre sources de HEAD précédent. Quatre constatent la carte initiale absente, deux une fin de lot inexécutée et deux le refus atomique attendu lorsque 119 places sont déjà occupées. Le premier qualificateur exigeait trop strictement huit erreurs d'assertion ; son résultat négatif est conservé. Le reçu de qualificationV2 vérifie séparément les deux exceptions de capacité attendues, sans répéter ni modifier les tests. Toutes les sources sont restaurées byte pour byte. Les 14 cas ciblés passent ensuite avec les corrections.

La suite complète, sur les mêmes assemblages et la même empreinte de 2239 fichiers sources que le contrôle ciblé, passe : **4039 réussites, zéro échec, deux exclusions existantes, 4041 cas au total**. Architecture : 2136 sources, 21 revues de taille existantes, PASS. Preuve liée : `artifacts/meal-resume-20261029/bootstrap-prior-projection-validation-v1/root-qualification-receipt-v1.json`. Aucun appel au modèle pendant ces validations. Le prochain binaire doit encore être compilé et qualifié avec le fournisseur simulé avant un nouvel essai réel. **TESTE_NON_APPROUVE** reste inchangé.

### 2026-11-03 — BUILD22 et W16 qualifiés ; expansion canonique encore à prendre en compte

Le commit `d2d12f2` est poussé et sa validation GitHub réussit (run41569015370). BUILD22 est réellement compilé sous Linux puis son identité est relue : inventaire `65f566e1…`, image `9cad6c4e…`, DLL `f5718183…`. Les609 chemins sont conservés ; quatre sources de requête diffèrent du paquet précédent, les605 autres fichiers sont identiques. Le producteur d'ingestion est inchangé : cette modification d'orchestration ne nécessite pas de réingérer le corpus déjà contrôlé.

Le scénario FREE W16 exécute un unique travail `ace57704-1ca0-40c2-8ff8-c152b1c7ab0a`, session `0a32cb71-27a8-44cf-a56b-b34706c9dae5`. Les huit recherches initiales et sept opérations natives réussissent :15 événements SQL, aucune erreur, cinq échanges HTTP capturés. La demande native répétée est rejouée sans seconde recherche et l'opération vide est effectivement exécutée. Les44 fenêtres visibles correspondent au Worker et au snapshot SQL ; les contrôles de source, qualité, offsets, omissions, corrélation native et six groupes restent appliqués. Maximum actif observé122. Ce résultat technique ne valide pas un planning autonome : les réponses du faux fournisseur sont scriptées, dont l'insuffisance finale. Preuve actuelle : `native-correlation-free-inspection-actual-w16-v1/inspection-receipt-v1.json`.

L'inspecteur W16V1 avait une hypothèse incorrecte : il exigeait des quotas document/page nuls alors que les valeurs structurées normales sont8/2. Son refus et le retrait de sa revue d'applicabilité restent conservés. V2 vérifie ces valeurs exactes, le topK calculé et ses sondes liées au même journal/trace/opération SQL ; tous les prédicats documentaires parents sont conservés. Les14 tests publics passent, puis V2 qualifie réellement les mêmes captures sans nouveau travail. La fermeture V2 inclut ce vérificateur corrigé, ses parents distincts, et la contre-épreuve de traces. Les94 fichiers sont transférés et rehashés ; archive `03d26bc5…`, index `29a147e0…`. La fermeture V1 reste disponible et n'est pas présentée comme qualification réussie de son ancien vérificateur.

Le contrôle des droits sur les traces passe réellement200/503/200, avec restauration exacte des ACL/métadonnées et aucun travail soumis. Le nettoyage W16 passe ensuite par une seule suppression normale, GET404, audit+1, cascades absentes et arrêt de la paire isolée. Avant/après nettoyage :32 tables documentaires inchangées,77 migrations,4 PDF/510 pages, aucun OOM ni redémarrage PostgreSQL ; après, zéro travail/session de test. Reçus sous `private/free-w16-after-cleanup-v1/` ; preuve SQL32 `beee6e64…`. Les identités, configurations, montages et dates de démarrage de la paire arrêtée sont vérifiés séparément.

La conservation précédente reste une priorité souple, pas une promesse de tout garder visible : dans ce scénario,17 IDs sont montrés au premier Writer ; deux subsistent après ses nouvelles opérations, puis un en revue/Critic. Les corps contrôlés sont fidèles, mais le contexte borné reste sélectif. L'audit des consignes ne trouve aucun quota actif de20 recettes distinctes en mode réutilisable ; l'adéquation de chaque choix à son rôle reste une décision métier à éprouver avec le vrai modèle.

Un risque supplémentaire a été établi avant le prochain appel payant : `TopK` borne les résultats de recherche, tandis que `BuildEvidenceReferences` peut produire quatre fiches et un chunk par résultat. Une première recherche de topK16 a donc pu ajouter25 références sans violer le contrat du moteur. Le bornage BUILD22 répartissait des places de preuves comme s'il s'agissait de résultats : il améliore le scénario observé mais n'est pas une garantie générale contre la saturation. Les tests précédents utilisaient un faux gateway à une preuve par résultat et ne couvraient pas cette expansion. Correctif conservateur en préparation : constante partagée de cinq références maximum par résultat, allocation dans la bonne unité et tests utilisant le véritable constructeur de références. Sans au moins cinq places par opération restante, conserver toutes les expansions et garantir toutes les recherches est impossible ; cette limite doit rester explicite, sans troncature silencieuse ni rotation anticipée. Aucun essai payant n'est lancé pendant cette correction.

Aucun nouveau coût OpenAI depuis PAID17. Cumul local0,53329548USD,58 charges/réservations rapprochées, zéro réservation ouverte au dernier relevé réel ; facture/solde fournisseur non vérifiés. Telegram91 envoyé avec succès à03:25:04Z (un message). **TESTE_NON_APPROUVE** maintenu.

### 2026-11-03 — Allocation canonique dans la bonne unité, validation ROOT achevée

La borne partagée `AdvancedAnalysisToolGateway.MaximumEvidenceReferencesPerMatch=5` gouverne désormais le maximum de quatre fiches associées et le calcul du bootstrap. Celui-ci alloue des résultats à partir du nombre de places canoniques divisé par cinq. Les références retournées restent intégrales : aucune sélection/troncature après revalidation, aucun nouvel appel/réessai, aucune éviction ou rotation anticipée, aucun relèvement du plafond128. Les sondes indiquent aussi la capacité en résultats et si la marge permet de garantir au moins un résultat complet par opération restante. Une capacité insuffisante est signalée même si topK1 reste inchangé ; le refus atomique et le retour de limite existants demeurent applicables.

Les nouveaux tests utilisent le véritable constructeur de références (quatre fiches plus chunk) et l'admission atomique réelle en mémoire, avec un résolveur synthétique. Depuis une base vide, huit recherches produisant chacune l'expansion maximale tiennent en125 références. Avec119 références préexistantes, neuf places libres ne permettent pas de garantir huit résultats de cinq références : le premier lot de cinq est admis, le suivant est refusé intégralement, les124 corps précédents restent présents et les six opérations omises sont décrites. Les deux protocoles sont couverts. Les anciens tests à une référence par résultat conservent leurs assertions de huit périmètres exécutés, de non-éviction et de plafond ; ils ne requièrent plus de remplir artificiellement toutes les places.

L'ablation ROOT rétablit uniquement le calcul bootstrap de BUILD22, en conservant le constructeur sémantiquement identique. Deux cas depuis une base vide reproduisent l'arrêt prématuré du suffixe ; deux cas déjà serrés échouent seulement sur les nouveaux champs de diagnostic, pas sur une nouvelle correction de leur refus de capacité. Après restauration exacte des quatre fichiers,19/19 tests ciblés passent. La suite complète sans restriction de namespace passe **4044 réussites, zéro échec, deux exclusions existantes,4046 cas** ; même empreinte de2240 sources et mêmes assemblages que le contrôle ciblé. Le premier passage limité à un namespace (3643 réussites) est conservé séparément et n'est pas le résultat complet. Architecture :2137 sources,21 revues de taille existantes, PASS. Reçu : `artifacts/meal-resume-20261029/bootstrap-reference-expansion-validation-v1/root-qualification-receipt-v1.json`.

Cette correction doit encore être compilée et éprouvée dans un nouveau runtime FREE W17. Les preuves BUILD22/W16 restent attribuées à leur ancien code. Les préparations PAID18 sur BUILD22 sont conservées non exécutées ; elles ne sont pas une autorisation d'utiliser ce binaire après la correction. Producteur d'ingestion inchangé ; aucun nouvel appel payant. **TESTE_NON_APPROUVE** maintenu.


### 2026-11-03 — BUILD23, W17 réel qualifié, puis PAID18 : saturation corrigée mais sélection dégradée

Le commit `4b9fd501` est poussé et GitHub41574463818 passe. BUILD23 est compilé et relu effectivement ; seul le chemin de requête avancée a changé, pas le producteur d’ingestion. Le runtime FREE W17 vérifie48 fenêtres canoniques sur5 trames HTTP et15 opérations SQL réussies, zéro échec, maximum73 références actives. Son contrôle de capture200/503/200 passe. Les94 fichiers sont fermés puis la seule session fictive est supprimée normalement ; SQL32 après nettoyage conserve32 tables/77 migrations/4 PDF/510 pages et zéro travail/session, sans OOM ni redémarrage. Paire arrêtée vérifiée avant PAID18.

PAID18 exécute une seule fois la même demande, avec le vrai `gpt-5.6-luna`, profil25 et BUILD23 :39,165s,5 appels modèle,93644 tokens entrée/3026 sortie. Les8 recherches bootstrap et8 `find_source_text` réussissent toutes ; aucune saturation documentaire cette fois. Le résultat demeure `insufficient_documentation`, avec5 claims et6 références, pas un planning validé. Les53 fichiers sont fermés, transférés et rehashés. Les52 corps visibles des cinq rôles correspondent aux enregistrements complets Worker et SQL, avec qualité/positions/cursors vérifiés. C’est la fidélité des requêtes sérialisées backend, sans capture HTTP indépendante du fournisseur payant.

Le diagnostic distingue fidélité et sélection : les3 preuves citées par le Writer initial restent toutes actives parmi77 références, mais les3 disparaissent de la projection du suivi et du Critic. Sur18 corps initiaux, un seul reste visible ; le contexte final en montre8 et le dernier ne contient que2 caractères. Une fiche substantielle de petit-déjeuner page124 est ainsi remplacée dans le contexte visible par un index page4, malgré un catalogue de sources stable. La priorité de tous les premiers hits `find` et la branche de revue d’insuffisance qui ne mémorise pas les citations positives sont en correction hors ligne.

Mesure ROOT sur l’assemblage réellement testé : cap documentaire65536, coût legacy du suivi65532 caractères contre52255 dans le message compact réellement transmis. Environ13281 caractères de marge réelle sont inutilisés. Correction du fitting vers le même encodage que la barrière finale en cours, sans relever le cap. Aucun nouveau test payant avant la reproduction causale et les contrôles de régression.

Finance PAID18 réellement rapprochée :5 nouvelles écritures,0,04975294USD ; historique58 inchangé, total63 charges/63 réservations, zéro ouverte. Cumul local avec la base hors ledger :0,58304842USD ; reliquat de l’enveloppe cumulative1USD :0,41695158USD. Facture/solde fournisseur non vérifiés. Nettoyage normal PAID18 lancé une seule fois, preuve SQL32 après nettoyage encore attendue au moment de cette entrée. Telegram93 envoyé à04:50:02Z (272 mots, exit0). **TESTE_NON_APPROUVE** maintenu.

Preuves : `artifacts/meal-resume-20261029/private/paid18-closed-v1/`, `private/paid18-all-role-fidelity-v1/receipt-v1.json`, `private/paid18-runtime-preparation-v1/context-observation-v1.json`, `documentary-fit-measurement-v1.json` et `ledger-snapshot-receipt-v1.json`.


PAID18 — fermeture achevée : une seule suppression API, GET404, audit+1, cascades absentes et paire backend/fournisseur simulé arrêtée. La capture PostgreSQL32 après nettoyage passe : zéro travail/session/message de test,32 tables documentaires et77 migrations inchangées,4 PDF/510 pages, aucun nouvel OOM ni redémarrage. Inspection complète de la paire arrêtée : identités, Config/HostConfig, montages et dates de démarrage conservés. Reçus dans `private/paid18-after-cleanup-v1/`. Les corrections de projection sont encore en validation hors ligne, aucun nouvel appel payant.


### 2026-11-03 — Projection corrigée et qualifiée hors ligne : citations souples, corps avant index, coût compact exact

La continuité de citations est désormais indépendante de l’option ActiveProposal, effectivement désactivée dans PAID18. La boucle mémorise uniquement les IDs de citations valides et visibles d’un résultat, avant le passage éventuel à la revue d’insuffisance. Cette liste bornée ne contient aucun texte, n’ajoute aucune protection au working set et ne crée aucune preuve. Chaque priorité est rebondée sur les objets canoniques actifs ; IDs retirés ou rejetés exclus.

Pour les lots composés uniquement de `find_source_text`, la préférence de projection choisit un corps non navigant disponible avant un index, puis entrelace les nouveaux résultats avec les corps déjà cités. Le résultat canonique et son ordre physique restent intacts. Sans citation admissible, l’ordre équitable historique est conservé. Les lectures explicites de pages et de fenêtres gardent leur priorité. Les limites et diagnostics d’omission restent actifs ; la pertinence métier d’une citation n’est pas certifiée par cette mémoire.

Le fitting par extrait et par collection utilise maintenant le même encodeur extérieur compact que le transport et la barrière finale. Le JSON documentaire intérieur, les règles de lignes entières, le plafond65536 et le garde séparé de l’historique natif restent inchangés. La sonde de fitting du principal appel modèle mesure aussi cet encodage ; le garde natif conserve sa mesure conservatrice distincte.

Contre-tests ROOT : restauration temporaire de trois fichiers de fitting HEAD → six refus de capacité injustifiés reproduits et quatre différences de sonde ; restauration de trois fichiers de rétention HEAD, avec le compact conservé → six erreurs de priorité/ordre reproduites. Ces six dernières assertions ne démontrent pas six pertes de corps, car la marge compacte pouvait déjà faire coexister les corps de cette fixture. Les neuf fichiers ont été restaurés exactement après chaque ablation. Les trois corps perdus de la vraie exécution PAID18 restent une observation distincte archivée.

Les vérifications élargies ont également révélé des fixtures liées à l’ancien encodage ou à l’ancien premier résultat d’index. Elles ont été recalibrées sur un oracle compact indépendant : une projection entière doit réussir si elle tient, puis être refusée avant HTTP à son coût exact moins un ; les tests de concurrence conservent une réelle saturation, les corps complets, qualité, pages, polygones et cursors. Le test inter-batch exige les six nouveaux primaires substantiels, un ancien corps et un secondaire distinct ; il conserve l’index dans les résultats canoniques. Aucun plafond relevé et aucune assertion documentaire supprimée. Les trois avertissements nullables de la fixture d’expansion bootstrap sont remplacés par des préconditions explicites.

Qualification finale :54/54 ciblés et suite Unit Backend complète **4055 réussites, zéro échec, deux exclusions existantes,4057 cas**, même empreinte de2241 sources et mêmes assemblages. Les sorties initiales à12 échecs restent conservées séparément. Architecture :2138 sources,21 revues de taille existantes, PASS. Reçu `artifacts/meal-resume-20261029/projection-continuity-validation-v1/root-qualification-receipt-v1.json`. Six fichiers runtime de requête changés ; producteur d’ingestion inchangé. Aucun appel payant supplémentaire ; cumul local0,58304842USD, zéro réservation ouverte. Compilation Linux BUILD24, qualification FREE W18 et effet autonome sur le planning restent à exécuter. **TESTE_NON_APPROUVE** maintenu.

### 2026-11-03 — BUILD24 / W18 / PAID19 : grille complète, qualité des repas à corriger

Le commit 854c1d0f est poussé ; GitHub Actions 41583892873 passe. BUILD24 est compilé et relu sur Linux. W18, fournisseur simulé, vérifie 72 fenêtres canoniques sur cinq trames HTTP et 15 opérations SQL réussies. Le comptage compact indépendant correspond aux cinq sondes du runtime ; le plafond ne change pas. Après suppression normale de la session fictive, aucune session ni tâche ne reste. Les documents et le producteur d'ingestion sont inchangés.

PAID19 rejoue une seule fois la question d'origine sur les quatre PDF et le même modèle gpt-5.6-luna. Réponse « answered » en 48,038 s : 20 cases, neuf recettes, trois PDF utilisés. Les 19 recherches réussissent. Le rédacteur utilise deux reprises et déclare finalement une insuffisance, sans citation positive à préserver. Le relecteur pouvait demander une autre recherche ; il répond avec les 16 extraits déjà présents, dont les 12 qu'il cite. Ce résultat positif ne démontre pas l'effet isolé de la rétention de citations. Les six requêtes sérialisées par le backend sont archivées : 86 occurrences de corps vérifiées contre Worker et SQL ; résultat API identique au résultat SQL. Cela ne constitue pas une interception HTTP indépendante d'OpenAI.

Vérification visuelle des neuf pages d'origine : recettes, ingrédients et préparation présents. Les 20 cases ont des preuves, mais la salade de pommes de terre, présentée comme accompagnement, remplit seule deux déjeuners. Deux recettes seulement se partagent les dix petits-déjeuners et collations ; le scénario n'impose pas 20 recettes distinctes. Le contrôle mécanique V4 refusait des références distinctes qui remontent au même extrait canonique. V5 conserve les rejets de vrais doublons et reproduit séparément le regroupement WinUI attendu : 12 références vers neuf cartes. V5 valide les 20 cases mécaniquement ; ce nombre de cartes n'a pas été observé sur l'application installée. L'examen métier n'approuve pas encore le planning.

Finance locale : six appels, 134685 jetons en entrée et 3692 en sortie ; 0,07149494 USD rapprochés ; cumul 0,65454336 USD, 69 écritures de charge et zéro réservation ouverte. Ce chiffre ne vérifie ni facture ni solde OpenAI. Fermeture de 59 fichiers ; suppression API unique, paire arrêtée, et comparaison après nettoyage : zéro session ni tâche, 32 tables, 77 migrations, quatre PDF/510 pages conservés, zéro nouvel OOM ni redémarrage. Rapport Telegram 95 envoyé avec reçu.

La politique de synthèse précise maintenant la différence entre accompagnement et choix autonome, et préfère une option encore inutilisée lorsqu'elle convient aussi bien et dispose de preuves équivalentes. Le choix utile de répéter reste autorisé. Modification du chemin de synthèse uniquement ; **TESTE_NON_APPROUVE** maintenu jusqu'à validation métier du résultat corrigé.

### 2026-11-03 — BUILD25 / W19 / PAID20 : refus de lecture de pages denses

Le commit 6fd23726 est poussé ; GitHub Actions 41587998039 passe. BUILD25 contient une seule modification parmi 609 fichiers du backend : les deux précisions sur les accompagnements et la variété. W19 sur le fournisseur simulé vérifie les cinq requêtes HTTP, les deux nouvelles consignes dans quatre rôles, 72 extraits canoniques, 15 opérations SQL réussies et l’accord du comptage compact. Après suppression unique de la session fictive, les deux conteneurs sont arrêtés et SQL32 constate zéro session ni tâche restante et un corpus inchangé.

PAID20 rejoue la même demande avec gpt-5.6-luna : 20 cases, sept recettes provenant de quatre PDF. Les sept pages originales ont été vérifiées visuellement ; les déjeuners sont plausibles comme plats seuls. Les mêmes petit-déjeuner et collation sont répétés chacun cinq fois ; la variété est inférieure à PAID19 et l’acceptation métier reste ouverte. La revue mécanique V5 passe. Le résultat API et SQL est identique. Les sept requêtes backend archivées contiennent 108 occurrences d’extraits contrôlées contre le corpus Worker et SQL, avec un traitement spécifique de la dernière synthèse sans outils. Aucune interception HTTP indépendante d’OpenAI.

Cause mesurée : les huit recherches initiales et huit recherches littérales réussissent. Le relecteur demande ensuite quatre lectures de pages avec topK5. La page qui comporte cinq fragments est lue ; les trois pages qui en comportent six, six et sept sont refusées sans admission de preuve. Une contenait une recette de dessert exploitable comme autre collation. La réponse d’outil expose un seuil d’au moins six, mais le modèle ne retente aucune page. Après le contrôle de ses citations, la synthèse de récupération produit une grille à partir des passages disponibles, sans recherche additionnelle. L’incidence exacte de ces refus sur la variété du résultat ne peut pas être isolée de la sélection du modèle.

L’inspecteur figé V22 traitait l’état retryable « failed » de ces lectures comme un cas inconnu. Le résultat négatif et les 74 fichiers scellés sont conservés. Le supplément reproduit le refus de l’inspecteur et rattache les trois opérations refusées aux requêtes SQL exactes, à leur retour natif sans preuve et aux historiques ultérieurs ; elles ne sont pas comptées comme zéro résultat documentaire. Le passage de fidélité et le contrôle mécanique utilisent une vue diagnostique séparée.

Le backend décrit maintenant topK comme une borne pour la page entière, conseille 12 pour une seule page de densité inconnue et renvoie une instruction de reprise ciblée : fenêtre plus petite pour une lecture multipage ; limite proposée plus haute pour une page dense ; autre outil si le maximum est déjà atteint. Le retour signale qu’aucun texte n’a été lu et que le nombre détecté est un minimum. Aucun changement des plafonds, aucune relance automatique, ni de l’ingestion.

Finance : sept appels, 158981 jetons d’entrée, 8140 en sortie, 78,137 s, 0,09279812 USD rapprochés ; cumul local 0,74734148 USD, 76 charges, zéro réservation ouverte. Facture et solde fournisseur non vérifiés. Telegram 96 envoyé avec reçu. **TESTE_NON_APPROUVE**.

Qualification locale du retour de reprise : 21 tests ciblés passent, dont le même scénario de page dense dans les deux protocoles. Profil officiel Unit Backend : 4057 réussites, zéro échec, deux exclusions existantes. Le contrôle PostgreSQL dans une base locale isolée passe : refus atomique, reprise de la fenêtre et refus si l'identité de la révision change. Le nettoyage PAID20 est terminé : une suppression normale, conteneurs de test arrêtés, comparaison SQL32 zéro session/tâche et corpus inchangé. Le nouveau retour reste à vérifier sur le binaire Linux et avec le modèle réel.

### 2026-11-03 — BUILD26 / W20b : reprise guidée d’une lecture vérifiée sur Linux

Le commit e611c196 est poussé ; GitHub Actions 41590182469 passe. Compilation et relecture Linux BUILD26 réussies. Le premier essai simulé W20 est négatif : sa page n’était pas visible dans la première requête du rédacteur, le fournisseur simulé s’est arrêté avant tout appel de lecture, huit recherches SQL étaient déjà réussies. Les preuves sont conservées et sa session fictive a été supprimée.

W20b reprend avec une vraie page déjà visible, dont le snapshot canonique contient six fragments. Sur six échanges HTTP simulés, l’opération SQL 9 demande une lecture bornée à cinq fragments : refus récupérable, zéro preuve renvoyée. La requête suivante transmet au fournisseur la suggestion de relire la même page avec une limite de dix. L’opération 10 conserve document, révision et page, puis renvoie les six fragments. Les six figurent dans la requête suivante et ont été comparés au corpus Worker et au snapshot SQL. Les traces indépendantes et les corps HTTP concordent. L’archive de 105 fichiers conserve la première version du vérificateur, qui rejetait les valeurs optionnelles neutres du schéma natif. La vérification corrigée conserve le contrôle des types, des arguments et de la corrélation exacte.

Un DELETE normal supprime la session de test et le travail associé ; les deux conteneurs de l’essai sont arrêtés. Comparaison SQL32 après nettoyage : aucune tâche ni session restante, quatre PDF et 510 pages conservés, aucun nouvel OOM ni redémarrage observé. Aucun appel OpenAI depuis PAID20. Telegram 97 envoyé avec reçu. L’utilisation spontanée du conseil par un vrai modèle et la qualité métier du prochain planning restent à mesurer. **TESTE_NON_APPROUVE**.

### 2026-11-03 — PAID21 : fidélité confirmée, continuité et coût de l’historique à corriger

PAID21 rejoue la question et le corpus inchangés avec GPT-5.6 Luna, BUILD26. Le résultat API et SQL est identique : insuffisance, une proposition sourcée, cinq appels, 29,012 s. Huit recherches initiales, sept recherches littérales (dont une vide) et une recherche globale : 16 réussites, aucune lecture de page et aucun refus de capacité. Il restait 48 opérations documentaires permises selon le message du relecteur. L’absence de nouvelle demande par le modèle est observée ; elle ne prouve pas l’absence de sources.

Le contrôle de 48 fichiers archivés et la réinspection passent. Les cinq traces du backend comportent 74 occurrences d’extraits vérifiées contre le Worker et SQL. Cela vérifie les corps sérialisés par le backend, sans interception indépendante du réseau OpenAI. Les preuves actives passent de 52 à 72, les visibles de 23 à 14. Quatre des extraits initiaux restent visibles au second rédacteur et trois au relecteur. Le calcul exact indique 56 427 caractères pour la collection initiale et 32 311 au tour suivant ; ce tour porte aussi 25 141 caractères d’historique natif. Sept retours répètent chacun un inventaire tabulaire identique à un inventaire présent dans le message courant, sans vue tabulaire demandée. La place utilisée et l’ordre de priorité sont deux pistes distinctes. Aucune régression d’ingestion ou insuffisance matérielle n’est démontrée par cet essai.

Coût local : 0,05259264 USD, cumul 0,79993412 USD, 81 charges, aucune réservation ouverte. Pas de confirmation du solde ou de la facture OpenAI. Nettoyage normal de l’unique session de test, backend arrêté et fournisseur simulé laissé arrêté, comparaison après nettoyage réussie. Rapport de nuit Telegram 98 reçu en trois parties. Ne pas répéter l’essai payant avant une contre-épreuve de conservation des passages initiaux et de maîtrise de la place occupée. **TESTE_NON_APPROUVE**.

### 2026-11-03 — Continuité documentaire et audit de neutralité des domaines

Les correctifs de continuité ont été validés par comparaison sur une copie isolée de l’ancienne version : quatre tests simulés dans les deux protocoles échouent avant correction (aucun des deux passages initiaux n’est visible) et passent après, avec exactement les mêmes limites. Une page explicitement demandée garde sa priorité. La projection alterne un nombre borné de passages substantiels encore actifs avec les nouveaux résultats même si aucune citation n’a encore été produite. Les retours de recherche peuvent référencer un inventaire déjà présent dans la requête uniquement si identité et contenu coïncident exactement et que la référence réduit effectivement le volume. L’historique de PAID21 aurait diminué de 8 563 caractères sur chacune des deux requêtes concernées avec cette seule substitution à projection fixe ; ce calcul n’est ni une réexécution ni une estimation de qualité. Suite Unit Backend du commit 7186c42 : 4 065 réussites, deux exclusions, aucun échec.

Un audit demandé ensuite a retrouvé dans le produit des exemples culinaires et un nom de PDF dans une consigne de qualification WinUI, ainsi que des heuristiques qui débordaient le rôle d’exemples. Les consignes sont reformulées en termes de champs, preuves et choix génériques. Le backend ne traite plus comme titre exact une reconstruction qui saute des mots de quantité ou de portion ; les retours à la ligne contigus et les marqueurs de page explicitement pris en charge restent testés. WinUI évalue la structure des champs renseignés sans exiger les intitulés d’un domaine, contrôle toujours le titre, la page et la source, et distingue les références contenant un numéro des fragments isolés. L’exception OCR ciblant des mots précis est retirée. Les scénarios de cuisine restent légitimes dans la banque de tests, au même titre que la maintenance et l’archivage. Les quatre identités, révisions, empreintes et noms de fichiers du corpus actuel n’ont donné aucune occurrence dans le code produit contrôlé. Aucun changement des données d’ingestion, aucun nouvel appel payant.

Validation de cette version : 4 066 réussites et deux exclusions existantes côté backend ; 2 512 réussites côté WinUI. Architecture : 2 140 sources, 21 revues de taille existantes, PASS. Ces vérifications sont hors ligne ; le nouveau binaire Linux et le nouveau test métier ne sont pas encore qualifiés.

BUILD27 (commit f4408f6) compilé et relu sous Linux, neuf fichiers de requête changés sur 609 ; aucun producteur d’ingestion modifié. GitHub Actions 41595984954 passe. W21 exécute sur le serveur isolé cinq échanges HTTP et seize opérations SQL : huit recherches initiales, sept recherches littérales dont un contrôle vide, une recherche globale. Sur 78 occurrences de passages rapprochées avec le Worker et SQL, la reprise garde huit passages de trois sources initiales et intègre dix nouveaux passages. Les huit références d’inventaire pointent chacune vers l’entrée de la même source dans le message courant. Le contrôle indépendant vérifie ces renvois, sans prétendre reconstruire les inventaires à partir des PDF. Archive de 63 fichiers ; le premier vérificateur et son échec de lecture des messages de rôle sont conservés, le dérivé V2 qualifie la même archive. Un DELETE de la session fictive et conservation SQL32 réussis ; aucune tâche, corpus intact, paire arrêtée. Aucun appel OpenAI pendant W21 ; Telegram 99 envoyé. **TESTE_NON_APPROUVE**.

### 2026-11-03 — PAID22 : une proposition sans preuve, résultat refusé

Essai réel Luna : huit recherches initiales réussies, trois appels modèle (planificateur, rédacteur, critique), aucune lecture ou recherche complémentaire. Le rédacteur puis le critique ont proposé vingt éléments. Dix correspondent à des titres, neuf à des passages explicites ; pour le dernier, aucun libellé équivalent littéral n’a été retrouvé dans les passages ni dans l’ensemble des unités et segments de la révision citée. Le garde-fou a rejeté le résultat final ; API et SQL concordent sur l’absence de planning livré. Les 44 occurrences documentaires visibles dans les trois appels concordent avec le Worker et SQL. Ce contrôle ne constitue pas une interception HTTP indépendante du fournisseur réel.

La configuration de cet essai n’activait pas le retour de correction des candidats : aucune demande de réparation n’a précédé le rejet, alors que les budgets d’appels et d’opérations le permettaient. Suite : demander au modèle de réparer ou de remplacer uniquement sur preuve disponible, ou déclarer l’insuffisance. Vérifier le mécanisme hors ligne avant un nouvel appel réel.

Coût mesuré localement : 0,03656640 USD, cumul 0,83650052 USD ; 84 charges et aucune réservation ouverte. Aucun solde fournisseur déduit. Archive fermée : 61 fichiers ; nettoyage vérifié d’une seule session de test. Comparaison après nettoyage : corpus conservé, aucune tâche restante, backend et simulateur arrêtés. **TESTE_NON_APPROUVE**.

Deuxième passe de neutralité : cas confirmés à traiter séparément. WinUI produit des expansions figées pour les recherches thermiques/électriques et privilégie les fiches de sécurité ; le backend emploie également des bonus de classement et de navigation spécialisés. L’OCR contient encore des exemptions de bruit fondées sur des lexiques métiers, pouvant influer sur la conservation du texte. L’audit des noms du corpus ne prouve donc pas une neutralité de traitement. Les corrections doivent préserver les demandes explicites et la fidélité de l’OCR, puis être éprouvées sur plusieurs familles de documents avant validation. Telegram 100 envoyé.

### 2026-11-03 — Reprise du contrôle de neutralité

WinUI : la requête conserve le type demandé et le classement compare les titres de la même manière pour une fiche de sécurité et un certificat. Les expansions thermiques et électriques imposées ont été retirées. Suite Unit Client : 2 514 réussites, aucun échec ni exclusion. Contrôle d’architecture : PASS, 2 142 fichiers et 21 revues de taille existantes.

Première suite backend : 4 073 réussites, six échecs et deux exclusions existantes. Trois échecs montrent que la réparation d’identité débordait sur les sélections simples. La restriction à une grille complète autorisant explicitement la répétition est préparée. Les trois autres concernent le traitement du type demandé ou un ancien test qui imposait une expansion FDS depuis un terme générique. Pas de validation backend avant relance.

OCR : reproduction hors ligne sur 19 entrées synthétiques et fixtures, deux groupes basculent entre contenu conservé et bruit selon le seul vocabulaire. Le rôle navigation ne change pas. Protocole et reçu dans artifacts/meal-resume-20261029/ocr-vocabulary-audit-v1 ; l’effet sur les documents réels n’est pas encore mesuré. Telegram 101 envoyé, aucun appel ni coût fournisseur supplémentaire.

Deuxième suite Unit Backend : 4 081 réussites, aucun échec, deux exclusions existantes. Les demandes simples conservent le rejet strict ; la réparation est réservée aux grilles complètes autorisant la répétition. Les régressions de recherche couvrent FDS, certificat/rapport et type libre, distinguent une demande de rédaction d’un type de source et vérifient que les alias FDS exigent un signal explicite.

Trois lignes OCR réelles de mathématiques historiques ont aussi été vérifiées isolément après leur annotation visuelle : le prédicat général signale les trois comme bruit, le prédicat d’unité publiée n’en signale aucune et aucune n’est réécrite. Cela ne démontre pas une perte en ingestion, où le contexte et les règles d’admission diffèrent. Restent nécessaires des régions OCR ancrées à l’image dans plusieurs domaines, dont du bruit avéré.

### 2026-11-03 — Reprise de l’acceptation stricte du planning

GitHub Offline validation 41667013366 réussit sur e0c07ed. L’accès SSH possédé est rétabli. BUILD28 Linux compile le même commit, avec 609 fichiers vérifiés et inspection indépendante de l’image et du binaire. La compilation n’a pas démarré le backend de qualification.

Le rejeu hors ligne des vraies trames PAID22 retrouve uniquement la case C9 non soutenue au rédacteur et au critique, sur les 44 occurrences documentaires déjà contrôlées. Six scénarios simulés passent pour la réparation et l’insuffisance au plafond d’appels. Six autres passent pour une grille stricte 5×4 : le Planner ne peut relâcher la distinction, la grille distincte et sourcée est admise, un doublon persistant donne une insuffisance sans claim. Ces contrôles ne démontrent pas une sélection réelle.

Le cas historique accepte les répétitions et reste un diagnostic. Le cas d’acceptation distinct exige vingt choix adaptés avec preuve exacte et trois réussites réelles consécutives sur le même état. Les témoins de faisabilité du corpus ne sont pas transmis au modèle. Le nouveau harnais est séparé des scénarios historiques. Aucun appel payant effectué pendant cette reprise ; **TESTE_NON_APPROUVE**.

### 2026-11-03 — W22 Linux strict, premier essai réel et pertes de découverte

W22 sur BUILD28 : le faux Planner propose le mode répétable mais le backend conserve le contrat strict. Le Writer et le Critic soumettent vingt cases avec dix identités distinctes ; récupération puis insuffisance sans choix publié. Quatre trames HTTPS exactes, huit recherches SQL, 75 passages rapprochés avec Worker/SQL, 28 fichiers fermés. Un DELETE de la session fictive ; 32 tables inchangées, zéro session/tâche restante. L’outil d’acceptation dfb67f8 et GitHub 41682659849 passent.

PAID23, vrai Luna, même demande stricte sans candidats préchargés : cinq appels, huit recherches initiales et quatre recherches littérales réussies. Le premier rédacteur demande les quatre recherches ; le suivant rédige onze choix intermédiaires. La revue et le critique n’engagent pas de nouvelle recherche alors que 52 opérations documentaires restent possibles. Terminal API/SQL : succeeded / insufficient_documentation, zéro choix publié. L’archive de 58 fichiers et la réinspection V25 vérifient 87 passages visibles face à la publication Worker et SQL. Pas de capture HTTP indépendante côté fournisseur réel. Coût réconcilié 0,05767010 USD ; total local 0,89417062 USD, 89 charges/réservations, aucune ouverte, reliquat 0,10582938 USD sous le plafond local ; le solde OpenAI n’est pas déduit. Le DELETE unique, l’arrêt de la paire et les 32 tables/77 migrations/510 pages inchangées sont vérifiés. Telegram 104 envoyé.

Le corpus comporte un témoin privé de vingt identités distinctes, cinq par rôle, chacune liée à sa page originale et à un corps canonique ; leur pertinence par créneau relève de la revue visuelle et de l’interprétation du service. Trois témoins sont visibles dans la requête Writer initiale ; un seul après le lot de finds, deux restant pourtant dans les preuves actives. Le quota de huit corps historiques remplit les places dans l’ordre des groupes : le correctif générique donne un tour aux rôles explicitement ciblés avant les groupes redondants, sans élargir le quota ni changer les lectures explicites. Le contre-factuel rend un témoin et couvre quatre rôles au lieu de trois ; il ne démontre pas une autre réponse du modèle. La revue stricte expose désormais la couverture du brouillon par rôle, calculée à partir des noms exacts, distincts, présents dans un corps visible, sans valeur de preuve ni de pertinence métier. Suite Unit Backend : 4 086 réussites, aucun échec, deux exclusions existantes.

Une lecture SQL bornée de Source34 trouve 2 149 repères de navigation, dont 1 783 ancres de titre. Les vingt témoins ont tous une ancre exacte sur la bonne page. L’intersection avec titres des cartes ramène la liste à 271 locateurs tout en conservant les vingt témoins. Un outil générique de navigation paginé est en qualification isolée ; il ne transforme pas les repères en citations. Aucun nouvel essai réel avant sa qualification. **TESTE_NON_APPROUVE**.

### 2026-11-03 — Navigation par titres, essai W23 puis rejeu W23b

- L’outil générique navigue dans les titres d’une source déjà observée ; il retourne une localisation, sans preuve citable. La lecture du passage reste une opération distincte. La fonctionnalité est désactivée par défaut et activée par la configuration signée de qualification.
- Le démarrage W23 a appliqué uniquement la migration 076. La comparaison avant/après retrouve les 32 tables documentaires identiques et les 77 migrations précédentes inchangées. Le premier listing a réussi côté serveur, puis le simulateur a rejeté trois champs de diagnostic explicitement nuls. L’échec HTTP 400 et le résultat absent sont conservés dans une capsule de 24 fichiers. Le rejeu de la capture reproduit le refus initial et le correctif lève exactement ce refus ; sept contrôles du simulateur passent.
- W23 a été fermé par une seule suppression normale de la discussion fictive. Le contrôle après fermeture retrouve 32 tables inchangées, 78 migrations, 4 documents et 510 pages ; aucun travail résiduel et aucun redémarrage PostgreSQL.
- W23b a exécuté le scénario contrôlé sur le serveur : 7 requêtes HTTPS et 7 traces corrélées, 8 recherches initiales, 2 pages de titres avec le même filtre et la même source, puis 1 lecture physique de la même révision. Les listes ne sont pas citables. L’oracle indépendant compare 142 corps vus dans les échanges au Worker et à PostgreSQL, dont le passage de la lecture canonique. Le résultat volontaire est une insuffisance sans affirmation publiée. Capsule : 40 fichiers. Aucun appel OpenAI ni facturation supplémentaire pour W23/W23b.
- La navigation est qualifiée pour ce parcours contrôlé. La validation métier du planning distinct demeure ouverte (20 choix sourcés, trois succès consécutifs et WinUI). Statut : TESTE_NON_APPROUVE. Rapport Telegram 105 envoyé.
- W23b a été fermé par la même route normale : un DELETE, une entrée d’audit, zéro session et zéro travail résiduels, 32 tables documentaires et 78 migrations inchangées. Le workflow GitHub Offline validation 41688460981 est passé pour le code du serveur testé.
- PAID24 (vrai GPT-5.6 Luna) : 3 appels HTTP, 16 recherches SQL réussies (8 initiales et 8 demandées), aucun appel de navigation par titres ni de lecture directe. La troisième réponse propose 7 choix intermédiaires ; aucune affirmation n’a été publiée. Le diagnostic sur les vingt témoins montre un seul nouveau témoin récupéré par les huit recherches et 4 puis 2 témoins dans les prompts Writer successifs.
- Le quatrième appel (revue d’insuffisance) a été refusé localement : corps non envoyé de 127 172 octets, borne d’entrée 127 428, sortie maximale 8 192 ; réserve nécessaire 0,07845960 $ contre 0,07539448 $ disponible. Aucune réservation ni tentative HTTP pour cet appel. API et PostgreSQL concordent sur l’échec sans résultat. Registre : 3 charges et 3 réservations soldées, 0,03043490 $ pour cet essai, 0,92460552 $ cumulés, zéro réservation ouverte. Le solde OpenAI n’a pas été mesuré. Capsule de 46 fichiers vérifiée sur le PC, rapport Telegram 106 envoyé.
- PAID24 fermé par DELETE normal : une suppression, une entrée d’audit, aucun travail ni discussion fictive résiduels. Les 32 tables documentaires, les 78 migrations et les 510 pages restent identiques.
- Deux corrections génériques font suite à PAID24 : suppression du catalogue hérité quand les vraies fonctions natives sont déjà déclarées et fusion de la consigne répétée des diagnostics ; indication de navigation lorsque des recherches textuelles se succèdent pour une grille à choix nommés et que des sources observées sont disponibles. Le modèle garde le choix de l’outil et des sources. Les règles de preuve, permissions et réservation ne changent pas. Rejeu isolé du compactage sur les captures Writer : −5 605 et −6 533 octets ; le passage du plafond sur un nouvel enchaînement reste à mesurer.

### 2026-11-03 — W24 qualifié, premier parcours réel de navigation PAID25

- W24 sur le serveur (Build30) confirme le compactage et le guidage : 7 requêtes, 11 opérations SQL, deux listes paginées sans preuve puis une lecture. Par comparaison avec W23b : 32 919 octets UTF-8 retirés, 142 passages inchangés (ordre, texte, fenêtres). Le guidage apparaît avant la navigation et disparaît ensuite. Fin volontaire : insuffisance, zéro affirmation. W24 a été fermé par DELETE normal, 32 tables, 78 migrations et 510 pages inchangées. La suite Release du backend : 4 095 réussites, deux exclusions facultatives, zéro échec.
- PAID25 avec vrai GPT-5.6 Luna, même question et corpus : après 8 recherches initiales, le Writer a demandé 3 listes parallèles de 20 titres, filtrées sur les cartes, dans 3 sources. Les 3 opérations SQL ont réussi, mais aucune de ces pages de titres n'a été envoyée au modèle : l’enveloppe suivante dépassait le plafond documentaire local (67 913 caractères sur 65 536). Aucun appel HTTP ni charge pour cette relance, aucune réponse métier enregistrée.
- Cause reproduite : le calcul préalable des extraits ne disposait pas du catalogue actif dans son tableau d’essai, donc il mesurait des sorties de navigation sans pages. Le rendu final comptait correctement les 3 pages. Le calcul et le rendu partagent désormais ce catalogue ; le plafond et la validation finale sont inchangés. Deux tests à trois listes parallèles échouent sur le code initial et passent sur le code corrigé (Responses et Chat Completions).
- Registre PAID25 : 2 appels réels, 0,01334828 $ pour l’essai, 0,93795380 $ cumulés, 94 écritures de charge, aucune réservation ouverte. Reliquat sous le plafond de 1,15 $ : 0,21204620 $ (distinct du solde fournisseur). Capsule de 40 fichiers vérifiée ; rapport Telegram 107 envoyé. Le planning demeure TESTE_NON_APPROUVE.
- Build31 : 4 097 tests backend réussis (deux facultatifs ignorés). W25 rejoue le lot parallèle sur Linux avec le faux fournisseur : six échanges, trois listes de trois sources (28 locateurs, zéro preuve citable), une lecture canonique vérifiée sur la même révision, 12 opérations SQL et 119 passages comparés. La relance qui reçoit les trois pages tient dans le plafond de 65 536 caractères. Le résultat contrôlé reste une insuffisance sans affirmation ; cette validation ne prédit pas les vingt choix du vrai modèle.
- W25 fermé normalement : une suppression, une entrée d’audit, 32 tables documentaires / 78 migrations / 510 pages inchangées.
- PAID26 (vrai GPT-5.6 Luna) termine techniquement en insuffisance, sans affirmation. Les huit recherches initiales et huit listes de titres réussissent, et le modèle demande ensuite quatre lectures. Les quatre fenêtres inclusives demandées font 5, 11, 7 et 8 pages alors que le maximum de quatre était expliqué dans le texte mais non exprimé par le schéma des deux bornes. Le lot est refusé avant SQL ; la relance de correction ne produit aucune nouvelle lecture. L’oracle confirme 144 passages visibles, et la capsule de 61 fichiers est relue sur le PC.
- PAID26 : 7 appels réels, 0,07389754 $ soldés, cumul local 1,01185134 $, 101 écritures et zéro réservation ouverte. Le plafond de campagne de cet essai est 1,15 $. Rapport Telegram 108 envoyé.
- Nouveau contrat natif de lecture : le fournisseur voit une page de départ et un nombre de pages de 1 à 4. Le serveur convertit vers ses bornes physiques habituelles et continue de refuser les plages trop grandes, y compris dans les anciens messages. Les lectures d’observations restent sur une seule page. Les tests ciblés couvrent les quatre demandes réelles, les deux transports et le retour de correction ; le nouveau comportement reste à valider sur le serveur avec le faux fournisseur.
- PAID26 fermé par la route normale, avec une seule suppression et un audit. Aucun job/session résiduel ; 32 tables, 78 migrations, 510 pages inchangées.
- Build32 : 4 110 tests backend réussis et deux facultatifs ignorés. En W26, six échanges simulés et 15 opérations SQL réussies : huit recherches, trois listes de titres et quatre lectures (1, 1, 1 et 2 pages). L’oracle vérifie 111 fenêtres visibles dont 12 corps issus des lectures, sur la révision ciblée. Les titres restent des repères. Cette qualification gratuite valide le contrat et le fil jusqu’au fournisseur ; le planning reste TESTE_NON_APPROUVE.
- W26 fermé normalement : un DELETE, un audit, zéro job/session résiduels et conservation des 32 tables, des 78 migrations et des 510 pages.
- PAID27 (GPT-5.6 Luna sur Build32) termine en insuffisance sans choix publié. Huit recherches initiales et six listes de titres réussissent ; aucune lecture demandée, aucun refus de protocole, et 50 opérations documentaires / 10 appels modèle restaient à la fin du parcours de revue. Les six listes non filtrées affichent 56 repères : 29 sans page physique, sept correspondant à une carte à titre/page/révision identiques, zéro des vingt témoins exacts. La seconde vague ne contient qu’un repère recoupé par une carte. Les 113 occurrences de passages visibles sont vérifiées ; capsule de 54 fichiers. Six appels réels pour 0,06025994 $, cumul 1,07211128 $, 107 écritures et zéro réservation ouverte. Rapport Telegram 109 envoyé. TESTE_NON_APPROUVE.
- Le nouveau guidage distingue un repère de titre d’un corps déjà lu. Pour trouver des corps nommés, il indique le filtre de titres recoupés par une carte, tout en laissant la navigation complète disponible. Quand le dernier lot a exposé des pages mais aucun corps de lecture visible, l’état est inclus dans le prochain tour et dans la revue existante. Le modèle choisit la source et la lecture ; aucune opération automatique supplémentaire.
- Build33 / W27 : 4 114 tests backend passés (deux facultatifs ignorés). Le faux fournisseur a reçu six requêtes, dont les indications de filtrage et de lecture. L’oracle a vérifié 111 passages, 15 opérations SQL, 28 repères de navigation non citables et 12 corps issus de quatre lectures. L’indication de lire apparaît après les listes et disparaît après lecture. Issue contrôlée : insuffisance sans affirmation, aucun coût fournisseur ; TESTE_NON_APPROUVE.
- W27 fermé normalement : une suppression, un audit, zéro job/session résiduels ; 32 tables, 78 migrations et 510 pages conservées. Authentification du fournisseur réel vérifiée sans requête de génération ; le plafond cumulé local de 1,25 $ reste inchangé.
- PAID28 (Build33, GPT-5.6 Luna) : le filtre recommandé est employé dans les quatre listes. 37 repères avec page (28 tuples source/page/titre), aucun des 20 témoins exacts dans ces listes. Le conseil de lire et les outils sont bien présents au tour suivant et à la revue, avec 52 opérations encore disponibles. Aucune lecture, aucune demande refusée. Résultat partiel : six affirmations, neuf références contrôlées dans les requêtes, aucune case du planning strict. Cinq appels, 0,05265008 $ soldés, 1,12476136 $ cumulés dans le registre local, aucune réservation ouverte. Capsule de 59 fichiers relue ; rapport Telegram 110 envoyé. Build33 Offline validation GitHub 41699562772 verte ; TESTE_NON_APPROUVE.
- Reprise du 4 novembre : clôture PAID28 et arrêt du couple de test confirmés ; capture fraîche SQL32 à 06:11 UTC comparée à la clôture, 32 tables / 78 migrations inchangées. Le certificat éphémère du faux fournisseur avait expiré ; nouvelle identité TLS préparée uniquement pour ce test isolé, sans changement du magasin de confiance Windows. Relecture privée PAID28 : le modèle invoque le manque de corps, les consignes autorisent déjà l’adéquation ordinaire des éléments à la demande. Le nombre six est une sortie partielle, pas un inventaire exhaustif.
- Une première commande de suite complète sans sélection de profil a exécuté 4 117 tests avec succès et ignoré 372 tests. Deux tests PostgreSQL ont échoué faute de DSN, sans SQL exécuté. Le résultat brut est conservé ; une campagne Unit correctement isolée sera relancée sur la version finale du correctif.
- Correctif de lecture bornée : au premier réexamen Writer après une insuffisance accompagnée de pages repérées mais non lues, le backend vérifie au plus quatre pages canoniques, réparties entre sources, dans les budgets existants. Les identités et passages sont revalidés et le modèle voit ces lectures dans son contexte documentaire. Les résultats natifs conservent leurs identifiants originaux. Sonde llm.locator_readback ajoutée. Les cas de lecture partielle, scope révisé, doublon réussi/échoué, TopK et délai sont couverts ; état conservé jusqu’au Critic et réinitialisé par un nouveau tour natif. 62 tests ciblés passent, puis campagne Unit isolée : 4 126 réussis, deux facultatifs ignorés, aucun échec.
- Build34 Linux contrôlé sur le commit 4314c51 : contexte assaini de 613 fichiers, image et DLL mesurées sur Linux. W28 simule une insuffisance après trois listes de titres. Cinq trames et 15 opérations SQL réussies, dont quatre lectures backend d’une page ; quatre nouvelles fenêtres visibles et substantielles au réexamen, 12 corps exacts et 95 occurrences vérifiées. Les résultats des outils natifs gardent leur corrélation, aucune lecture du backend attribuée au modèle. Sonde de lecture présente une fois. Issue simulée volontaire : insuffisance sans affirmation. TESTE_NON_APPROUVE, essai réel encore à effectuer.
- GitHub Offline validation 41784530470 (commit 4314c51) : terminé avec succès.
- PAID29 sur Build34 : cinq appels réels, issue insuffisante avec neuf affirmations partielles. Huit recherches initiales puis huit reformulations Writer, zéro liste/read/find ; le secours après repères n’a donc pas été exercé dans cet essai réel. 94 identifiants visibles au backend, 48 opérations encore possibles à la revue ; pas de refus d’outil. Oracle indépendant PASS, capsule de 59 fichiers. Registre prudent : 117 charges soldées, zéro réservation ouverte, 0,05746574 $ pour cet essai et 1,18222710 $ cumulés. Le calcul indicatif aux tarifs publics actuels vaut environ 0,02986957 $, sans attestation de facturation. Les tarifs de contrôle historiques restent inchangés.
- Transport du raisonnement PAID29 : store=false/include encrypted_content présents ; l’unique retour d’outils rejoue intégralement le raisonnement chiffré et les huit appels du Writer. Les phases terminales ne sont pas conservées comme nouvel historique de raisonnement ; incidence qualitative non démontrée. Préparation d’un comparatif à SynthesisReasoningEffort=medium, Planner maintenu low/1024, même binaire et corpus. Configurations dédiées signées/vérifiées sur le PC ; pas de modification de la configuration de production. Rapport Telegram 112 envoyé. TESTE_NON_APPROUVE.
- PAID30 (même Luna, synthèse medium, Planner low) : sept appels, six listes recoupées par cartes puis dix demandes de lecture. La limite de planification vaut huit pour cette grille ; le normaliseur refuse le lot entier avant SQL, code advanced_native_tool_protocol_invalid. Les dix schémas/sources/fenêtres ont été vérifiés indépendamment, 1–3 pages, topK20, 4–11 chunks chacune, 50 opérations et 11 appels modèle encore possibles. Le parseur C# reste à rejouer après correction. Zéro lecture et zéro affirmation finales. Capsule réelle de 58 fichiers / oracle PASS. Registre prudent : 124 charges soldées, zéro réservation ouverte, 0,07465588 $ pour ce test, cumul 1,25688298 $ sous plafond1,50. Rapport Telegram113 envoyé.
- Correctif de lot natif en cours : validation complète avant effets, préfixe borné conservé, sorties corrélées de report pour la queue non exécutée, reprise soumise aux budgets. Aucun relèvement du plafond d’opérations, règle documentaire ou métier ajouté. Les tests élargis ont détecté une régression de revalidation du prompt enrichi pour les lectures avancées ; correction avant build35. Le planning reste TESTE_NON_APPROUVE.
- Correctif de lot natif stabilisé : ordre original et raisonnement conservés ; les arguments de tout le lot sont vérifiés avant effet, seules les opérations du préfixe sont appliquées, et chaque appel différé reçoit non_executed_batch_limit, aucune preuve et une reprise conditionnée aux budgets. Les écritures mémoire/candidats et lectures de preuves en queue restent sans effet. Sonde llm.native_batch_limit demandé/admis/différé ajoutée. Validation réutilise la normalisation du vrai prompt enrichi, ce qui préserve les lectures natives/visuelles avancées. Les 10 arguments réels PAID30 ont été rejoués dans les parseurs C# finaux : 10/10 valides, préfixe exact8 et queue2, aucun réseau/gateway. Suite fournisseur1060/1060, ciblés11/11, puis Unit complet4136 réussis, deux facultatifs ignorés, aucun échec et source inchangée pendant exécution.
- PAID30 clôturé normalement et vérifié indépendamment : un DELETE, un audit, aucun job/session, paire arrêtée sans OOM, 32 tables /78 migrations /4 versions /510 pages conservées. W30 gratuit préparé sur sept phases pour vérifier la reprise8+2 dans le runtime Linux. Le planning réel reste TESTE_NON_APPROUVE.

- Build35 Linux construit et relu : commit d05158e94d9471d01cd79597b1925ba2e41e1ed6, image 7fe4fb02, DLL60f4f490 ; six fichiers de preuve et inspections indépendantes vérifiés. Workflow GitHub41794058939 réussi (toutes étapes, dont suites hors ligne). Les contrôles SQL32 avant/après démarrage gratuit confirment 78 migrations et la conservation du corpus.
- W30 réel gratuit Stage53 : une seule soumission, sept échanges simulés, 21 opérations SQL réussies. Dix lectures demandées : huit exécutées, deux sorties explicites non exécutées sans preuve/effet, puis reprise exacte des deux lectures. L'oracle indépendant vérifie 141 observations Worker, 18 corps canoniques distincts et le raisonnement opaque conservé après les trois lots. Capsule40 fichiers ; preuve private/strict-free-w30-closed-v1/w30-wire-worker-sql-verification-v1.json (89c95505). Insuffisance volontaire zéro claim : qualification technique du chemin, pas acceptation métier. Aucun appel payant supplémentaire ; registre prudent reste cumul1,25688298 USD, 124 charges, zéro réservation ouverte. Fermeture normale de W30 en préparation avant PAID31 sur le même modèle/configuration medium.
- W30 fermé : un DELETE normal / un audit, zéro session/job, arrêt sans OOM des deux conteneurs. Comparaison documentaire et inspection indépendantes PASS (conservation72c36e52, reçu indépendant a6e1fcd6) ; 32 tables, 78 migrations, quatre versions et 510 pages conservées. Stage54 PAID31 préparé sans démarrer ; registre124 lignes soldées / zéro réservation ouverte inchangé, authentification existante GET/models200 sans génération ni corpus. Plafond interne1,50 USD conservé, aucune facture ni balance fournisseur déduite de ce registre.
- PAID31 réel Stage54 terminé : une soumission, huit appels Luna5.6 (Planner low/synthèse medium), 49,734s, zéro claim et insuffisance documentaire. Quatre listes de titres et quatre lectures demandées ; trois lectures réussies, une fenêtre de quatre pages refusée car24 chunks réels pour topK20. Retry plus étroit correctement signalé, aucun lot dépassé/différé cette fois. Coût interne prudent réconcilié0,09275868 USD, cumul1,34964166 ; 132 charges et réservations soldées, zéro ouverte. Rapport Telegram114 envoyé à08:20UTC (reçu exit0).
- Capsule PAID31 : 61 fichiers ; inspection legacy V15 négative conservée car elle ne connaît pas le statut failed/récupérable d'une lecture canonique. Réinspection indépendante identique, oracle Worker/SQL séparé6b73fde8 et preuve sparse fffb7313 authentifient ce refus et les vrais appels, sans approbation métier ni réécriture du false initial.
- Audit causal P31 : dernier Writer researchAllowed=true, huit appels modèle et40 opérations documentaires encore possibles. Il voit21 pièces/9309 caractères de contenu sur128 entrées ; les titres/procédures de deux pages explicitement lues sont omis. Vérification visuelle de quatre pages originales hashées, puis preuve indépendante a057a421 sur les25 blocs des deux pages : tous sont dans Worker/units/chunks canoniques et retours SQL. Ce défaut ne justifie pas une réingestion : la perte survient dans la projection vers le modèle. Correctif générique de priorité des corps lus et d'honnêteté des omissions en cours ; compteur de revue inchangé avant cette correction.
- Client : citations message→preuves préservées et tableaux Markdown rendus génériquement ; revue indépendante a corrigé trois défauts supplémentaires (plusieurs locators même page, fences, noms JSON réels UI). Core99/Transport29/voisins218 verts ; build WinUI Release x64 sans hooks0warning0error. Dix fichiers seuls committés/poussés4b1012a ; validation native visuelle encore à réaliser, aucune approbation du parcours complet.
- PAID31 fermé normalement et vérifié indépendamment : un DELETE/un audit, zéro job/session, paire arrêtée sans restart/OOM ; configurations/images/neuf montages préservés. Conservation après4f65a8f3 et reçu indépendant fbd16a67 : 32 tables/78 migrations/4 versions/510 pages inchangées. Inspection legacy négative et contrôle sparse explicatif conservés, aucun override d'approbation ni achat.
- Projection corrigée génériquement : cohorte primaire du NativeTurn courant revalidée avant anciens hits ; vues natives/read_evidence conservent leurs priorités ; inventaire secondaire seul ne masque plus une omission primaire ; signalement des corps omis et premier gap de continuation, sondes null-safe. Ciblage final21PASS (18 nouveaux), voisins90PASS/1skip. Les six intégrations de régression échouent sur le parent inchangé dans un worktree séparé, aucun swap de DLL/source partagé. Sources gelées73b1a58b ; suite Unit officielle et replay indépendant en cours avant commit/build36, aucun nouvel appel payant.
- Première suite Unit : quatre régressions de continuité détectées (ancienne carte initiale pertinente évincée),4150PASS/2skip. Correction sans affaiblir tests : premier corps par lecture, continuité bornée des sources distinctes déjà admises/non rejetées, reste des corps courants avant vieux hits. Dernier voisinage105PASS/1skip. Suite Unit finale officielle :4159PASS/2skip/0FAIL,2263 sources identiques avant/après (manifest private/build36-local-validation-v1/unit-isolated-v2/manifest.json). Revue indépendante sans finding bloquant, replay étroit128 références/24IDs lus annoncéPASS avec limites de reconstruction explicites ; contrôle complet Linux à venir. Le lot client4b1012a a aussi passé GitHub41802232096. Statut métier inchangé.
- Build36 Linux construit et vérifié (source2d1de489, imagea2743f7b, DLLf8f47773, receipt52d07b2d) ; GitHub41806717900 vert surc84c67e. W31 réel Stage55 est un échec conservé : trois échanges, erreur400 du faux fournisseur car son assertion exigeait huit recherches réussies. Pression128 réellement atteinte ; capsule24 fichiers et oracle négatif9af5da76 vérifient16 tentatives SQL/10 succès/6 refus récupérables accumulated_evidence_limit_exceeded sans preuve admise. Les lectures primaires prévues n'ont pas été exercées : qualification/runtimeAccepted/APIguard restent false. W32 corrige uniquement le scénario pour admettre ces refus exacts, maintient128 et tous critères corps/fenêtres ;17 tests purs PASS, aucun appel payant.
- Sonde de volume : le compteur ignorait les fenêtres complémentaires pourtant livrées. Correctif strictement diagnostic, main+supporting et sous-compteurs explicites, chevauchements comptés par fenêtre (pas couverture unique). Revue indépendante et8 ciblés verts ; trois échecs attendus sur ancien code ; messages tool identiques avec/sans émission. Suite Unit officielle finale4162PASS/2skip/0FAIL, sources inchangées pendant exécution. Build37 prévu avec cette correction avant contrôle W32. Préparation UI20 file-only et copie native250 fichiers vérifiées ; aucun nouveau parcours natif lancé. Coût payé inchangé1,34964166 USD interne prudent/132 soldées/zéro ouverte. Rapport115 envoyé09:20UTC, statut métier TESTE_NON_APPROUVE.

- Build37 final44529c9 : Linux vérifié (source55be7853, image9c1e9f16, DLLb7cf7771, reçud9f2fcd2), GitHub41813179427 vert. W31 fermé normalement :1DELETE/1audit, paire arrêtée et9montages exacts ; comparaison32tables/78migrations/4versions/510pages intacte, reçuindépendant81b8ba41. Son ancien échec400 reste explicitement nonqualifié.
- W32 Stage56 réel :7échanges/40fichiers,21tentativesSQL (15succès/6refus récupérables exacts, aucune preuve admise pour les refus). Oracle indépendantb019708b : pression128 et plafond documentaire65536 inchangés (maximum65532 observé),15/15chunks primaires entiers/9832caractères, dont2paragraphes substantiels et4contextes physiques. Fenêtre explicite512caractères et ancienne source conservées ; raisonnement opaque rejoué, Plannerlow/Synthesismedium. Qualification du transport/projection PASS ; insuffisance volontaire0claims, aucune qualité métier du planning validée. Le scénario n'exerce pas l'avertissement d'omission puisque les corps retournés sont tous projetés. Fermeture applicative W32 en cours avant paiement.
- Sous l'autorisation utilisateur existante d'utiliser les crédits OpenAI déjà achetés, sous-plafond interne cumulé2,50USD préparé puis signé par root : seuls3plafonds payés1.49321314→2.49321314 changent, perjob0.49321314 et prévolmedium inchangés. Revue indépendante/6testsPASS ; signaturevérifiée, cléprivée jamais transférée, aucun achat ni génération effectué par la signature. Coût historique prudent inchangé1.34964166USD/132soldées/zéro réservation ouverte ; cette valeur n'est pas le solde fournisseur. Rapport116 envoyé10:21UTC. Le produit reste TESTE_NON_APPROUVE.
- W32 fermé normalement et vérifié : 1 DELETE / 1 audit, deux conteneurs arrêtés, 32 tables / 78 migrations / 4 versions / 510 pages conservées ; reçu indépendant21704e66. UI20 réel exécuté ensuite sur copies Windows indépendantes : 20 cellules visibles, C20 ouvre le PDF exact page2, même grille et même lien après redémarrage. Persistance20claims/mêmesbindings sans nouvelle génération contrôlée. Adaptateur privév2 corrige uniquement les doubles tableaux PowerShell et les8 noms PascalCase du DTO de capture ; sept négatifsPASS et captures/sealv3 inchangés. Qualification root42df0e76 ; arrêt owned vérifié, cinq ports de test absents et5432/PID7844 préservé.
- PAID32 Stage57, Build37, Luna5.6 low/medium : 8 appels /47,227s /0claims /insufficient_documentation. Coût exact prudent0,09117756USD, cumul1,44081922USD, 140charges/140soldées/zéro réservation ouverte ; ce registre n'est pas le solde fournisseur. API unique et readback réels ; timeout du transport SSH après lancement mais reçus terminaux complets récupérés sans resoumission. Capsule60fichiers fermée, inspection legacytrue, archive563031b5/indexe3dbbdfa. Nouvelle limite constatée : quatre lectures retournent33chunks dont14visibles et19omis sous65536 ; mémoire93/128 et44opérations encore disponibles, donc aucun épuisement de quota. Ordre favorisant des tableaux avant paragraphes et coût des métadonnées/historique à mesurer ; aucune nouvelle génération avant cause/contrôle gratuit. Rapport117 envoyé10:48UTC et118 envoyé11:16UTC. TESTE_NON_APPROUVE.
- PAID32 fermé normalement après correction d'un garde de clôture : le premier lanceur a refusé avant DELETE car le pin du conteneur pointait encore sur P31 ; cet échec reste conservé. Nouveau binding V2 vérifié avant I/O, une suppression applicative/un audit, paire arrêtée sans OOM. Reçu df5dcc17 et vérification indépendante eea3c8e9 : 32 tables/78 migrations/4 versions/510 pages conservées, zéro discussion/job de campagne. Cumul prudent inchangé1,44081922USD/140soldées/zéro réservation ouverte.
- Cause P32 mesurée : 33 corps canoniques retournés,14 projetés/19 omis ; ces19 IDs étaient annoncés dans le feedback mais absents des choix read_evidence. Correctif générique borné64 repères actifs/navigationOnly/non citables, revalidation source/révision/corps et ambiguïtés refusées, relecture sans nouvel accès corpus. Deux protocoles éprouvés par ExecuteAsync complet ; les deux cas échouent sur parent44529 exactement au repère absent, sans permutation de DLL. Métadonnées qualité/aperçu identiques partagées uniquement dans la collection courante avec hash, jamais les textes ni les inventaires historiques.
- Comptage documentaire : les nouveaux repères sont inclus dans l'ajustement et le contrôle final du vrai HTTP. Suite V1 négative pour deux anciens appels reflection à trois paramètres, compatibilité rétablie ; V2 négative pour quatre comparaisons dont l'oracle indépendant ne retirait pas encore le champ des repères (écart3007 caractères). Oracle corrigé sans relever son plafond ni affaiblir les exigences de coexistence/corps. Suite Unit officielle V3 :4199PASS/2skips optionnels/0FAIL, source avant/après identique2268fichiers ; manifest private/build38-local-validation-v1/unit-isolated-v3/manifest.json. Revue indépendante confirme le diagnostic ; contrôles ciblés handles/métadonnées/historique44PASS.
- Contexte98k préparé et signé pour un prochain contrôle gratuit : seuls MaximumEvidencePromptCharacters et NativeResearchMaximumHistoryCharacters passent65536→98304 dans les deux phases. Modèle/plafonds financiers/prix/corpus inchangés ; signatures Ed25519 publiques et quatre altérations/croisements vérifiés indépendamment. Borne configurable historique portée262144/default16384 conservé, aucun déploiement ni succès runtime98k encore revendiqué. Build38/Linux et contrôles gratuits larges restent à exécuter avant P33 payé ; aucune approbation métier. Rapport118 envoyé11:16:59UTC, reçu exit0.
- Build38 c99e7349 committé/poussé :616fichiers publics/source021eeabd, Linux imagee2783bfb/DLL700d13a9 ; readback20229b4f et vérification indépendante3396bca9 cohérents (78migrations inchangées,33écarts EOL seuls conservés). GitHub41826782481 terminé success à12:18UTC. Rapport119 envoyé12:14:38UTC/reçu exit0 ; aucune génération payante depuis P32.
- W33A gratuit Stage58 : admission initiale refuse avant runtime car ancien hash de plan W30 dans argv. Correction V2 crée uniquement un compose externe et un reçu distinct ; fichiers Stage58/reçu initial conservés, seule empreinte argv corrigée et sept contrôles purs PASS. Runtime réellement démarré ensuite, source-before/after32tables/78migrations conservées avant soumission unique.
- W33A terminal négatif conservé : jobe36e1cc5/sessionb03571a4 failed advanced_llm_http_400, quatre HTTP/traces et16SQL tous réussis (8search+8list), zéro read_source. Faux fournisseur refuse three_previous_substantive_other_source_anchors_required avant le lot ciblé. Capsule28fichiers authentifiée/oracle négatif5acfbebe : captureVerified=true MAIS qualification/APIflow=false. Cause de préparation mesurée : bootstrap W22 de mode repeatable différent du Planner P32 distinct_named_items ; source4 active avec un paragraphe1070caractères omis dèsHTTP2, alors que P32 disposait d'autres rangs/contextes. Aucune régression à code seul démontrée avec entrées différentes ; pas d'assouplissement de l'assertion33corps+3ancres.
- W33A fermé normalement :1DELETE/1audit, paire arrêtée sans OOM/restart/dérive config/montages. Receipt96f57d69, conservationef20ebf2 et indépendant18e47057 :32tables/78migrations/4versions/510pages intactes, zéro job/session/message ; scénario négatif jamais rebaptisé PASS. Reproduction A2 Stage59 préparée avec octets exacts du completionJson Planner P32 (8topK20 bruts, topKSQL effectifs gardés séparément) ; runtime futur non lancé, contrôles larges puis98k toujours nécessaires avant paiement. Rapport120 envoyé12:48:27UTC/reçu exit0 ; cumul prudent1,44081922USD/140soldées/zéro réservation ouverte, solde fournisseur non vérifié. TESTE_NON_APPROUVE.
- A2 Stage59 réel, même Build38/plafond65536 : Planner P32 copié byte-exact, correctif ACL du stager vérifié avant toute écriture (ancien prototype JSON absent détecté avant transfert, prototypes complets/pinnés contrôlés avant mkdir). Une soumission,8HTTP/8traces,20SQL tous réussis (8search+8list+4read_source),33corps retournés dont12visibles/21omis,3ancres anciennes. Relecture exacte d'un ID omis sans SQL supplémentaire ;139résolutions de métadonnées hashées vérifiées. Oracle réel574c54cf, capsule44fichiers94693ab5/indexd3624c06 ; qualification de cette seule réouverture PASS, insuffisance volontaire0claims et aucune validation métier.
- A2 fermé normalement :1DELETE/1audit, paire arrêtée sans OOM/restart/dérive image/config/montages. Cleanupb200952e/conservationbaae16cd/indépendant81ac4bd2, checkpoint498b3ef1 :32tables/78migrations/4versions/510pages intactes, zéro job/session/message. Toutes preuves SourceSQL/Worker/wire et le premier W33A négatif restent conservées séparément.
- Limite supplémentaire reproduite sur DLL c99 puis observée dans les vrais HTTP A2 : le NativeTurn suivant une première read_evidence faisait disparaître d'autres IDs omis encore actifs ; sept restaient invisibles/nonofferts àHTTP6. Avec recherche épuisée et lecture stockée encore permise, impasse réelle ; avec recherche disponible, détour topK modifié/find possible mais inutilement coûteux. Registre contextuel générique64 désormais indépendant de ce seul tour, sans corps historique : snapshots identité et SHA256 privé, source/texte/ambiguïtés revalidés au rendu et parse, essais fitting purs et comptés comme le vrai message, aucune citation avant corps visible. Sonde omitted_evidence_read_handle_hint_count sur le vrai prompt, sans texte ni identifiant.
- Lot registre :37ciblés PASS dont4ExecuteAsync (deux protocoles avec quota disponible/épuisé), deux fenêtres successives sans nouveau corpus IO, retrait/contenu même longueur/cap64/FIFO/cross-job/overflow avantHTTP. Première Unit complète4213PASS ; dernier bord null/empty détecté en revue puis corrigé (grouper toutes copies avant validation), deux négatifs supplémentaires. Unit officielle finale V2 :4215PASS/2skips optionnels/0FAIL,2269sources identiques avant/après ; architecture PASS. Revue indépendante finale571658a7 sur gel66f219b9, tests/source manifestd844ed1d. Contre-épreuve des4parcours sur parent c99 terminée :4échecs attendus après la première fenêtre canonique512, second repère absent dans la vraie boucle ExecuteAsync ; reçuf9a577aa, compilation réussie/worktree isolé/aucune permutation de DLL. Build39/Linux puis contrôle séquentiel A3 Stage60 et contrôle complet98k Stage61 restent à exécuter avant paiement. Rapport121 envoyé13:47:32UTC/reçu exit0 ; coût prudent inchangé1,44081922USD/140soldées/zéro réservation ouverte, solde OpenAI non déduit. TESTE_NON_APPROUVE.
- Build39 cba7916f committé/poussé, GitHub Offline validation41841991212 success :616fichiers/490C#/78migrations, image Linux5f857736/DLLc10f355b/readback9268ef7b et revue indépendante98869234. Qualification locale finale4215PASS/2exclusions facultatives/0FAIL, aucun changement source depuis le gel testé.
- A3 Stage60, faux fournisseur gratuit : préparation V1 refusée avant runtime parce que son reçu Build39 ciblait encore le nom de fichier Build38. V2 change uniquement ce chemin vers un nom neuf ; ancien fichier et manifeste V1 conservés. V2c309a3e2 admis, transfert existant identique sauté sans réécriture. Une seule soumission :9HTTP/9traces/20SQL réussies/33corps retournés,12initialement visibles et21omis ; insuffisance volontaire0claims, aucun appel OpenAI.
- Oracle A3 réel66a82958 PASS :48fichiers de capture authentifiés, deux IDs omis distincts relus successivement (16 puis512 caractères exacts de leurs corps canoniques), seconde poignée encore offerte après première lecture et première fenêtre toujours visible après seconde, sans SQL supplémentaire. Vérification du scope via catalogue de source et référence SQL (les items wire ne portent pas directement docId/revision/path). Adaptation locale de l'oracle au manifeste effectifV2, ancien oracle et gelV4 archivés ;19contrôles purs dont refus V1PASS, aucun critère produit assoupli. Fermeture applicative A3 et contrôle complet98k avant P33 restent à faire. TESTE_NON_APPROUVE ; coût prudent inchangé1,44081922USD/140soldées/zéro réservation ouverte.
- A3 fermé normalement : un DELETE / un audit, deux conteneurs arrêtés sans OOM/restart/dérive image/config/montages. Conservation505488ea et vérification indépendantebe703d93 :32tables/78migrations/4révisions/510pages inchangées, zéro session/job/message de campagne. Cette clôture débloque B98k ; les deux relectures qualifiées ne constituent pas une validation métier du planning.
- B98k Stage61 réellement préparé puis démarré sur le même Build39 : seuls MaximumEvidencePromptCharacters et NativeResearchMaximumHistoryCharacters passent65536→98304 dans la paire signée. Revue root puis indépendante détecte avant transfert un OLD_CFG de stager resté sur la cible98k ; correction minimale vers le vrai source65k, neuf contrôles purs PASS. Premier transfert refuse faute de parent, sans création de fichier ; parents owned1000 créés après vérification de toutes les cibles. Stage72407efc/18fichiers ; runtime3d2217b7 admis sans mutation puis lancé une fois, gate source aprèsda5e2131 PASS.
- B API unique : job335c1791/session3f19dc14, succeeded/insufficient_documentation/0claims,20,151s ;7HTTP/7traces/20SQL réussies, capsule40fichiers02bfa19e/indexfd34b7a4 authentifiée et extraite sans écart. Oracle indépendant réel2dd8e521 PASS :33/33corps canoniques intégraux visibles,0omis,11contextes physiques,15paragraphes substantiels et3ancres de sources précédentes conservées ; tous les corps rapprochés de Worker/SQL et du vrai HTTP,98k/efforts/raisonnement rejoué vérifiés. Validation du transport/coexistence uniquement ; aucune recette écrite par un vrai modèle dans cet essai. Fermeture normale B en cours avant P33 réel. Cumul prudent1,44081922USD/140soldées/0ouverte inchangé ; solde fournisseur non déduit. Rapport122 envoyé14:47:47UTC exit0, surveillance horaire réactivée sur le dossier de reprise actuel. TESTE_NON_APPROUVE.
- B fermé normalement :1DELETE/1audit, paire arrêtée sans restart/OOM/dérive montage ou configuration. Conservation71fc2499 et indépendantc349bb4d confirment32tables/78migrations/4révisions/510pages intactes et zéro job/session/message. Le checkpointd70a3c23 conserve qualification documentaire33/33 distincte de la sémantique. P33 est désormais en liaison locale sur les vrais reçus A3+B, même Luna et plafonds ; aucune génération payée nouvelle avant Stage62 et captures fraîches ledger/auth/SQL32.
- PAID33 Stage62 réel sur Build39, Luna5.6/98k : auth fraîche GET/models200 sans génération, ledger140soldées/0ouverte, gates SQL32/78 avant/après et signature Production vérifiés avant soumission unique. Timeout du transport SSH après lancement, mais récupération readonly retrouve tous les reçus complets sans resoumission : jobc68259a5/sessiona18a5a79 succeeded/insufficient_documentation/0claims,9appels/76,314s. Coût terminal réconcilié0,1525595USD ;149charges/149soldées/0ouverte ; cumul prudent1,59337872USD, distinct du solde fournisseur.
- Capture P33 :75fichiers/index16e59ac4/archive29ba1f99, extraction bornée/authentifiée puis réinspection47c03ea4. L’inspecteur historique reste evidenceConsistent=false pour canonical_sparse_status_projection_mismatch ; cause précise unsupported_prior_execution_status_for_sparse_binding : il ne connaît pas le refus read_source SQL17 (fenêtre trop dense). Aucun refus n’est réécrit comme succès. Oracle indépendantcb41d311 PASS séparément :314occurrences visibles/296référencesmetadata exactes, API/Worker/SQL et refus natifs liés, zéro recette approuvée. Tous reçus initiaux restent inchangés.
- P33 fermé normalement :1DELETE/1audit, deux conteneurs arrêtés sans restart/OOM ; conservation17d83ea6 et indépendant0acce1ee :32tables/78migrations/4révisions/510pages identiques et zéro job/session. Checkpoint5e830948 conserve legacyfalse, oracleindépendanttrue et semanticfalse séparés.
- Diagnostic P33 :24SQL (8search,7listes,9read_source dont1refus récupérable/8succès) ; huit relectures de corps omis demandées par Luna sont réellement projetées. Après cela trois listes offset0 déjà exécutées sont redemandées puis refusées avant SQL ; récupération/revue/critique concluent insuffisance alors que7appelsmodèle/40opérations restent possibles. Les prochains offsets réellement observés19/18/18 disparaissent après les reads, seuls les offsets historiques restent : correctif de continuité générique en développement, précédé d’un rouge2/2 sur parentcba isolé.
- Volumétrie réelle P33 :78retours read_source=70IDs uniques tous encore actifs,66déjà vus au moins une fois,43visibles auCritic/27omis et8handles encore proposés ;21803UTF16 de corps canoniques uniques. C# mesure9/9sondes àl’identique :FU3=97954wire dont53053evidence/33549historique/5301handles/3918inventaire/1607catalogue ;Critic=97538wire dont87009evidence (20528texte et66481métadonnées/identité). Pression de projection mesurée mais aucune preuve d’épuisement global ; priorité curseurs sans hausse arbitraire du cap ni altération du raisonnement/historique. Futur vérificateur sparseV36 distinct en préparation, anciennes preuves conservées. Aucun nouveau paiement avant correction, tests et prévol W34/Stage63 ; futur P34/Stage64 réservé par coordination uniquement. TESTE_NON_APPROUVE.
- Correctif générique curseurs : registre privé32 de pages réellement réussies, scope source/révision/hash/chemin et filtres exacts, sans corps/citation. Prochains offsets observés conservés après plusieurs read_source/read_evidence ; pruning des scopes retirés et snapshot privé. Même catalogue typé pour fitting/rendu/HTTP, sous-borne4096 comptée dans le cap98k ; en-tête nonfittable omis, historique opaque/function outputs inchangés. Revue trouve puis corrige la régression de frontière par lot chevauchant limit1 (conserver19 plutôt que16), sans inventer de curseur public.
- Gel7fichiers2a9c348e :15ciblés PASS/0skip, dont2protocoles ExecuteAsync et sondes réelles retenus/listés/omis sans contenu ;0diagnostic rejeté. Deux contre-épreuves au même test final échouent exactement sur parentcba détaché. Revue indépendanteb614de4c sans bloqueur. Unit Backend officielle Release finale :4230PASS/2exclusions facultatives/0FAIL, sources avant/après identiques790cadec ; manifest67f0ba30. ArchitecturePASS2169sources/21fichiers en revue de taille, diff whitespace clean. Aucun changement de modèle/caps/corpus, ni nouveau paiement ; Linux40 puis W34/Stage63 à exécuter avant P34.
- Diagnostic sparse distinctV36 figé96978b88 : même capsule P33,156priors tous traités,5occurrences de statut failed liées au seul SQL17 et8reads réussis/1refus préservés ;5tests/15falsifications PASS. Reçuf507abfe ajoute sparseStatusDiagnosticPassed=true sans modifier les bytes ni les anciens capturefalse/issues ; aucune approbation métier. Rapport123 envoyé15:47:29UTC exit0, delta seul. Cumul prudent1,59337872USD/149soldées/0ouverte. TESTE_NON_APPROUVE.
