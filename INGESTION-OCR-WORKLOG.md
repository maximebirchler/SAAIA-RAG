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

Le commit `0b2ce0ac40fd66e9cbbac2f7501a266a3fa02bfa` est poussé ; GitHub Actions40966967712 est terminé avec succès. Le backend Linux est reconstruit à partir des603fichiers de production inventoriés, puis ses six métadonnées de build, l'image et la DLL sont revérifiées au retour. La discussion fictive du deuxième essai payant est supprimée une seule fois par l'API normale ; GET404, cascades et audit+1 sont vérifiés. Une capture indépendante conserve les32tables documentaires,77migrations et4PDF/510pages ; archive fermée de29fichiers revérifiée intégralement. Aucun SQL d'écriture direct ni appel fournisseur pour ce nettoyage.

Le premier démarrage gratuit de la nouvelle DLL échoue avant toute soumission : `UnauthorizedAccessException` sur la configuration signée copiée en mode0600, propriétaire1000, alors que le backend utilise UID0 avec toutes ses capacités retirées. Le code139 n'est pas un OOM ; les deux applications sont arrêtées. Le correctif de préparation touche exactement9fichiers copiés et4dossiers neufs : ACL de lecture/écriture UID0 et héritage privé sur les seuls deux dossiers de traces, pour leur collecte par UID1000. Aucun contenu de fichier, clé payante, ancien environnement ou corpus n'est modifié. Un premier garde trop sensible à l'ordre des objets Mounts refuse avant mutation ; V2 compare les objets complets triés par destination unique, sans changer le contrôle intégral HostConfig.

À23:19UTC, le correctif ACLV2 et son vrai probe Docker sans réseau passent :12empreintes lues sous UID0/cap-dropALL, trois créations exclusives puis retrait, deux relectures des sentinelles de traces par l'opérateur1000 ; dossiers de traces et ledger gratuit à nouveau vides. Les13chemins, propriétaires, inodes, modes et empreintes sont revérifiés sur PC. Les applications restent arrêtées ; redémarrage dédié et qualification des11échanges du faux fournisseur restent à effectuer. Les préparations du faux fournisseur, de l'inspecteurV3 et du readback ne constituent pas une qualification réelle anticipée. Rapport61 envoyé avec succès en une partie à23:23UTC ; aucun nouvel appel payant, dernier cumul fermé estimé0,05030316USD. Le planning reste `TESTE_NON_APPROUVE`.


## 2026-10-30 — Qualification gratuite réelle et nouveau défaut de navigation

Après correction du garde sur le seul verrou vide du ledger gratuit (0644 créé réellement, ancien oracle0660), le runtime Linux possède les bonnes images/DLL, readiness et trois contrôles TLS. Aucun changement du ledger payant. Le faux fournisseur V2 réalise onze échanges et seize opérations documentaires réussies ; le STOP final HTTP400 est volontaire. L’inspecteur V3 vérifie177comparaisons canoniques visibles,209identifiants distincts admis,128maximum actifs et82retraits, avec instantanés durables, références protégées et poursuite read/find/search après saturation. Les95fichiers fermés sont tous vérifiés sur PC avant et après extraction. Ces résultats ne valent ni qualification RAW, ni redémarrage Linux, ni réponse métier.

L’audit indépendant des vrais prompts trouve un blocage amont avant même la première rotation : à123/128, quatre sources ont encore des preuves actives, mais le dernier corps lu domine la projection ; les trois enums natifs et le parser ne reconnaissent alors que cette source visible. Les trois autres sources sont actives mais inaccessibles au modèle. Le premier Writer reçoit20preuves complètes sur97 (10435caractères de contenu), contre10dans l’ancien essai ; il manque néanmoins un catalogue de navigation indépendant des corps affichés. Les noms de documents sont également absents au premier Writer. Le nouveau correctif reste en cours : catalogue léger de sources actives revalidées, mêmes handles dans schéma/parser, noms seuls pour orientation, budget strict incluant ce catalogue, aucune citation autorisée depuis un corps invisible.

Le faux fournisseur V2 avait aussi envoyé six includeVisualSources:null alors que le schéma déclare un booléen ; le parser les avait acceptés comme false. Le test gratuit établit donc l’exécution/fidélité observée, pas la conformité stricte des arguments. Une V3 et un inspecteur V4 sont préparés : false réel, validation du schéma et retour vers une autre source initialement vue dont les corps ne sont plus affichés. Aucun essai payant avant ce contre-test et les contrôles du correctif.

La seule session fictive W1 est supprimée une fois par l’API normale : GET404, cascades et audit+1 réussis, paire backend/faux fournisseur arrêtée, source34 inchangée. Capture SQL32 après nettoyage terminée à00:05UTC,11mesures,23591octets, pic mémoire observé997773312octets sous1GiB, aucun OOM/redémarrage. Comparaison finale et archive du nettoyage encore en cours à ce point. Aucun nouveau crédit dépensé ; dernier cumul fermé estimé0,05030316USD et aucune réservation ouverte dans ce journal fermé. Produit toujours TESTE_NON_APPROUVE.

Le nettoyage W1 est fermé à00:10UTC :32tables,77migrations,4PDF/510pages inchangés ; zéro tâche/session/message et aucun OOM ni redémarrage PG. Le reçu réel de suppression, la capture après et leurs preuves sont fermés dans34fichiers, tous revérifiés sur PC avant et après extraction. La lecture indépendante du closer ne trouve pas de défaut matériel ; celui-ci ne lit que des fichiers et ne contacte aucun service. Les applications possédées demeurent arrêtées. Le code du catalogue est en validation locale, sans nouvel appel payant.

Le catalogue actif F1 est validé localement : 3 845 tests backend réussis, aucun échec et deux exclusions explicites (OCR réel et sensibilité Linux sous Windows). Les 832 cas ciblés sont inclus dans ce total. La compilation fraîche V6 et le test complet V7 ont les mêmes empreintes de sources et d’assemblages ; 18 fichiers produit/tests sont gelés et revérifiés par root. Quatre sources actives avec une seule visible conservent leurs handles dans les outils et le parser ; les sources retirées, inconnues ou ambiguës sont refusées. Un carrier affiché au catalogue sans son corps reste interdit comme citation finale. Aucun plafond de production n’est relevé.

Les régressions rouges restent conservées : anciennes attentes interdisant même le basename, nombreuses sources distinctes sous 8 k, coutures natives qui ne réservaient pas le vrai tour d’outils. Les six frontières de qualité synthétiques conservent un refus explicite à 14 k et un succès intégral à 20 k, avec dix codes de qualité, huit descripteurs, lignes et empreintes inchangés ; taille réellement mesurée entre 14 892 et 14 935 caractères sérialisés selon le protocole. Les neuf codes du précédent cas réel Moulinex ne sont pas renommés en dix. Architecture et 17 contrôles comportementaux des sondes passent. Faux fournisseur V3 et inspecteur V5 passent 48 tests purs : l’égalité catalogue/sources SQL actives est exigée, même pour une source jamais visible ; les anciennes versions restent gelées. Tout cela ne qualifie pas encore le nouveau runtime Linux ni le planning métier.

Rapport Telegram 62 envoyé avec succès en une partie à 00:23:35 UTC, reçu local conservé. Le staging V5 est créé avec les onze fichiers signés/TLS/faux accès identiques ; aucune clé privée de signature ni clé OpenAI n’est copiée. Les trois nouveaux dossiers de traces/ledger gratuit attendent encore leur qualification ACL et leur probe sans réseau avant tout démarrage. Aucun nouvel appel payant.

Le commit catalogue `c21e9201764701f00b2cc42ec01cd60a91ec683c` est poussé ; GitHub Actions 40979821187 est terminé avec succès. La nouvelle image Linux V6 est construite depuis les 604 fichiers de production, puis ses métadonnées, image et DLL sont revérifiées au retour sur PC. Les ACL V3 sont qualifiées sans réseau : neuf lectures exactes, trois écritures de probe exclusives ensuite retirées, treize cibles bornées. Le premier démarrage W2 refuse avant mutation parce qu'un sous-dossier de capture vide existe déjà ; seul ce dossier vide possédé est retiré après vérification. Le même contrôleur réussit ensuite à 00:38 UTC : configuration signée, DLL, TLS, readiness et limites de ressources conformes, source34 inchangée. Aucun appel fournisseur réel.

Le second parcours gratuit W2 soumet une seule tâche fictive, sans retry, et s'arrête volontairement au onzième échange HTTP. Un premier readback refuse la représentation textuelle de `createdAtUtc` : `.292220` envoyé devient `.29222` en SQL, soit exactement le même instant. L'oracle V4 compare désormais cet unique champ à 100 ns sans flottant ; tous les autres champs restent stricts. Ses 29 tests et une contrelecture indépendante passent, y compris le refus d'un tick de différence. Le readback réel V4 passe à 00:51 UTC : onze traces, deux flux de logs complets, zéro écriture SQL directe, aucune clé fournisseur ni contenu de ledger lu.

L'archive W2 ferme 95 fichiers, 27 811 525 octets de preuves ; toutes les tailles et empreintes sont vérifiées avant puis après extraction sur PC. L'inspecteur V5 est réellement exécuté sous neuf gardes réseau/processus : PASS pour les onze corps HTTP, 170 fenêtres documentaires canoniques, 210 identifiants distincts admis, 84 retraits de transition et plafond actif 128 respecté. Quatre sources actives restent exactement présentes au catalogue ; le dixième échange relit la page 10 d'un autre PDF dont aucun corps n'est alors affiché, et trois extraits reviennent dans le onzième Writer, identiques aux données canoniques. Une contrelecture indépendante directement depuis le tar confirme ces faits. Cela ne valide ni une décision du modèle réel ni le planning, le RAW ou un redémarrage de processus. Produit toujours TESTE_NON_APPROUVE.

La capture SQL32 avant nettoyage W2 passe à 00:59 UTC : 12 mesures, 23 860 octets, pic mémoire observé 998 682 624 octets sous 1 GiB, aucune nouvelle erreur OOM, aucun redémarrage PostgreSQL ni session SQL résiduelle. Sa comparaison documentaire et le nettoyage de la seule session fictive restent à fermer avant le nouvel essai payant. Aucun crédit supplémentaire dépensé ; le dernier cumul estimé fermé demeure 0,05030316 USD, distinct du solde fournisseur.

La conservation W2 avant nettoyage passe réellement :32tables,77migrations,4PDF/510pages inchangés. La seule session fictive est supprimée une fois par l'API normale ; GET404, cascades, audit+1 et arrêt de la paire possédée sont vérifiés. Capture SQL32 après nettoyage à01:05:49UTC,11mesures,23591octets, pic999931904octets sous1GiB, aucun OOM/redémarrage. Le closer fichiers V2 passe et ferme34fichiers, tous revérifiés dans le tar et sur PC. Aucune clé fournisseur lue ni appel payant pour ces opérations. Le manifeste du runtime payé est contrelu sur ses15références réelles ; les corps du ledger sont identiques au dernier snapshot fermé, six anciennes charges et aucune réservation ouverte.

Le nouveau runtime payé démarre réellement à01:11UTC et passe les contrôles de signature, DLL, image, readiness et ressources ; le faux fournisseur reste arrêté. Une seule tâche réelle est lancée à01:15:49UTC avec 5.6 Luna, sans retry, dans le plafond cumulatif0,50USD. Résultat :succeeded/answered,20claims,7références,4appels,57358tokens d'entrée et2650de sortie,39718ms mesurés par le résultat. Coût estimé0,0334478USD. Le journal fermé contient dix charges, dont quatre nouvelles ; ses six anciennes lignes sont inchangées octet pour octet, zéro réservation ouverte. Cumul estimé0,08375096USD, distinct du solde/facture OpenAI. Aucun autre appel payé n'est lancé après cette tâche.

Le readback réel V4 payé passe :quatre traces, deux flux de logs complets, SQL en lecture seule, source34 inchangée. Capture SQL32 à01:24:03UTC,12mesures,28174octets, pic998334464octets sous1GiB, aucun OOM/redémarrage. La comparaison conserve32tables,77migrations,4PDF/510pages ; une tâche et une session fictive restent présentes. Un SHA de capture mal recopié dans le closer V1 est refusé avant création de sortie ; V1 est conservé et V2 ne change que ce littéral, dérivé automatiquement du vrai fichier. V2 passe réellement sous gardes réseau/processus. Le stderr SQL vide est conservé et copié à l'identique sous un suffixe .log admis par le collecteur, sans assouplir ses règles.

La fermeture V5 conserve81fichiers et toutes les requêtes de polling, avec quatre traces originales et les corps des dix charges/réservations. Archive et extraction sont revérifiées sur PC. L'inspection V5 réelle passe sans anomalie sous neuf gardes :cohérence des sondes, compteurs, catalogue actif et budget. Ce sont les sérialisations backend, sans interception indépendante des HTTP OpenAI. L'évaluation mécanique distincte compare ensuite le worker qualifié, le corpus SQL complet, les corps effectivement visibles dans l'entrée finale, les citations et les20coordonnées :PASS. Elle constate sept recettes distinctes et treize réutilisations ; les originaux capturés ne sont pas réécrits. Ces contrôles ne constituent pas l'approbation métier.

La revue de contenu indépendante confirme les sept propositions dans trois PDF, sans référence superflue ni chiffre culinaire ajouté. Qualité professionnelle non approuvée :même porridge cinq matins et deux soupes dans sept des dix repas principaux. La demande n'exigeait pas vingt recettes différentes ; aucun quota de diversité nouveau n'est inventé. Rapport Telegram63 envoyé avec succès à01:22:22UTC, une partie, reçu conservé. Le produit demeure TESTE_NON_APPROUVE.

L'audit causal des quatre vraies traces trouve ensuite une clôture prématurée de la recherche :planner,writer,writer-research-followup-1,synthesis-recovery ; aucun Critic, désactivé dans ce profil. Le Writer demande quatre recherches, puis signale une insuffisance après leurs résultats. Il reste alors treize appels modèle, cinquante-deux outils et du temps documentaire, avec researchAllowed=true. ResearchContinuation refuse pourtant la revue d'insuffisance parce qu'une recherche a déjà eu lieu ; le fournisseur bascule vers un completer sans outils sur douze extraits et autorise leur réutilisation pour remplir les cases. La politique existante favorise déjà les alternatives utiles ; le code n'impose pas sept recettes. Un indice de pancake adapté au petit-déjeuner est visible, sans lecture ciblée demandée. Correction ciblée autorisée :permettre l'unique revue bornée d'insuffisance après recherche aussi, dans les budgets et avec un locator déclaré ; préserver les gardes anti-duplicats et l'insuffisance honnête après cette revue. Implémentation et régressions gratuites en cours, aucun verdict anticipé ni nouvelle dépense.

La correction ne change que la transition de revue d'insuffisance : elle devient admissible après une recherche partielle, dans les mêmes limites et une seule fois pour cette synthèse. Trois sondes sans contenu privé précisent la recherche déjà effectuée et les locators disponibles. Le contre-test causal V2 compile réellement l'ancien code puis échoue dans les trois variantes attendues ; les octets corrigés sont restaurés et revérifiés. Les onze nouveaux cas passent ensuite. Le premier montage RED V1 échouait à compiler deux définitions : cette erreur de harnais est conservée et exclue des preuves causales.

La première suite complète révèle deux queues simulées CandidateInventory incomplètes après l'ouverture attendue de cette revue :3854PASS,2FAIL,2ignorés explicites. Une réponse d'insuffisance simulée est ajoutée à chacune, sans réduire budgets ni assertions ; les nouveaux contrôles exigent une revue unique, quatre/cinq appels exacts, le même inventaire et aucun completer fermé. La seconde suite complète recompilée passe :3856PASS,0FAIL,2ignorés explicites sur3858cas. Architecture PASS et17contrôles comportementaux de sondes PASS. Ces preuves gratuites concernent l'orchestration ; elles ne prouvent pas une amélioration autonome du planning.

Après revue indépendante et18tests purs du nettoyeur, root supprime une seule fois la discussion fictive paid4 par l'API normale :GET404,cascades et audit+1 confirmés, seul backend possédé arrêté, fake déjà arrêté préservé et source34 inchangée. Le journal actif des coûts n'est ni lu ni modifié par ce nettoyage. La capture SQL32 après nettoyage passe à02:05:04UTC :11mesures,23591octets,pic1000108032octets sous1GiB,aucun OOM/redémarrage ni session résiduelle. La comparaison documentaire finale de cette capture reste à fermer. Aucun nouvel appel payant ; cumul estimé fermé inchangé0,08375096USD,produit TESTE_NON_APPROUVE.

### 2026-10-30 — revue après recherche qualifiée, nouvel essai réel payé5

Le commit `ce9834e530abbbd3f6d976ea9ffbe34792e56a3c` est poussé et GitHub Actions40989814930 est terminé avec succès. Le build Linux7 est reconstruit et revérifié :604fichiers de production, une seule différence produit par rapport au build6, dans ResearchContinuation. La DLL et l'image exécutées correspondent aux métadonnées du build. La comparaison après nettoyage paid4 est également fermée :32tables,77migrations,4PDF/510pages inchangés et aucun job restant.

Le parcours gratuit W3 passe réellement sur cette DLL. Cinq requêtes capturées prouvent la séquence recherche partielle, insuffisance, revue avec outils puis lecture d'un autre document. Les dix opérations documentaires et73fenêtres canoniques visibles sont vérifiées ;73ne désigne pas un nombre de pages PDF distinctes. La cinquième réponse400 est l'arrêt intentionnel du faux fournisseur, vérifié par son corps exact. Les premiers inspecteurs V1/V2 sont conservés comme échecs d'oracle : préfixe Planner historique admissible, puis normalisation LF de la requête de recherche telle qu'effectuée par le code C#. V3 passe indépendamment et à l'identique sur PC, sans modifier les captures ni relâcher les identités, pages, corps ou empreintes. L'archive de61fichiers est revérifiée. Cette preuve ne valide pas la qualité métier ni une nouvelle rotation ou un redémarrage du processus.

La discussion fictive W3 est supprimée une seule fois par l'API normale, avecGET404,cascades et audit+1. Les deux applications possédées sont arrêtées et source34 est inchangée. La capture et la comparaison SQL32 après nettoyage passent :32tables,77migrations,4versions/510pages inchangés ; zéro job, session, message, ingestion, OOM ou redémarrage PostgreSQL. L'horodatage réel de capture03:00:02UTC est conservé séparément de celui de la comparaison.

Un nouvel essai payé5 est ensuite lancé à03:10:57UTC avec la même question, le même corpus et5.6Luna, sans retry ni nouveau plafond. Résultat :succeeded/answered,20claims,8objets sélectionnés,2appels,23812tokens d'entrée et2529de sortie. Le coût mesuré est0,0164576USD ; le cumul est0,10020856USD sur0,50USD, avec12charges et12réservations, aucune ouverte. Les dix anciennes lignes sont conservées comme préfixe exact des fichiers de coûts. Ces chiffres ne sont pas une vérification du solde ou de la facture OpenAI.

Le readback réel conserve les deux traces, les deux flux complets de logs, les événements SQL et le corpus canonique. La fermeture conserve49fichiers ; archive et extraction sont revérifiées, puis inspection et évaluation mécanique passent. Le contrôle compare notamment les corps visibles, le worker qualifié, les citations et les20coordonnées. La comparaison SQL32 payé5 passe également : documents inchangés, une seule tâche et une seule session fictive exactes, aucune ingestion/OOM/reprise PostgreSQL.

La qualité professionnelle reste non approuvée : même porridge cinq matins, rubriques génériques et composants proposés comme collations, répétitions importantes. Aucun quota nouveau de vingt recettes distinctes n'est ajouté. Le Writer a répondu directement sans recherche complémentaire ; la correction de revue d'insuffisance n'a donc pas été exercée par ce nouvel essai. Son entrée réelle comportait les quatre outils de recherche,19observations et la politique de qualité existante. La configuration de campagne désactive le Critic, alors que le produit l'active par défaut. L'audit des consignes, de la sélection initiale et de cette différence de configuration se poursuit avant toute nouvelle dépense. Telegram65 envoyé avec succès à03:18:02UTC, une partie, reçu conservé. TESTE_NON_APPROUVE maintenu.

### 2026-10-30 — contre-épreuve Critic et cohérence du contrat de sélection

La discussion fictive payé5 est supprimée normalement :DELETE unique,audit+1,cascades absentes,backend possédé arrêté,faux fournisseur conservé arrêté. La comparaison SQL32 après nettoyage conserve32tables,77migrations,4versions/510pages ; aucun job,session,message,ingestion,OOM ou redémarrage. Les anciennes configurations sont sauvegardées sur PC et serveur. Une seule option change dans les deux phases signées :SemanticCriticEnabled passe de false à true. Les autres champs privés et publics,question,modèle,prix,corpus,plafonds,profil5,image et DLL restent identiques. L'installation conserve les ACL et les deux applications restent arrêtées jusqu'au démarrage contrôlé. Cette transition ne revendique aucune qualification gratuite nouvelle de la branche Critic.

L'essai payé6 est lancé à03:39:57UTC. Il se termine succeeded mais insufficient_documentation,avec zéro claim et aucun planning. Les cinq appels réels sont Planner,Writer,writer-research-followup-1,writer-insufficiency-review,Critic ; aucun completer ultérieur. Le Writer demande quatre nouvelles recherches générales,puis exige à tort des repas distincts. La revue d'insuffisance corrigée est effectivement atteinte,mais ne demande pas de lecture complémentaire ; le Critic confirme ensuite l'insuffisance. Les outils,les locators de source,la rotation et les budgets restent ouverts. Cela ne démontre ni l'impossibilité du planning ni l'incapacité du modèle.

Coût payé6 :0,04300614USD pour cinq charges. Le journal ferme17charges et17réservations,zéro réservation ouverte,cumul0,14321470USD sur0,50USD. Les douze anciennes lignes sont conservées comme préfixe exact. Archive49fichiers revérifiée et inspection V7 réelle PASS. Un contrôle séparé des cinq entrées vérifie51fenêtres documentaires avec les fonctions canoniques existantes :corps,identités,pages,qualité déclarée,troncatures et curseurs concordent avec le worker qualifié et le corpus SQL. Aucune évaluation de grille20cases n'est inventée :elle est inapplicable à cette réponse sans proposition. Les documents restent inchangés,et la discussion fictive payé6 est ensuite supprimée une seule fois avec audit+1,cascades vérifiées et arrêt des seules applications possédées. La comparaison après nettoyage confirme à nouveau32/77/4/510 et zéro job restant.

L'audit trouve des consignes contradictoires concrètes :certains prompts de revue,relecture et réparation exigent des candidats distincts indépendamment du contrat qui autorise le réemploi. Ces phrases sont conditionnées à une demande explicite de distinction ; aucune exigence de diversité ni heuristique de repas n'est ajoutée. Le passage dossier-ready reste inchangé,car son appel est déjà réservé au mode distinct. Les tests compilent réellement les anciennes consignes contre les nouvelles régressions :six échecs attendus et un succès,puis sept succès après restauration exacte des sources corrigées. Revue indépendante,architecture2109sources et17contrôles comportementaux des sondes PASS.

Le premier appel direct de la suite complète omet par erreur le filtre de profil :3863PASS,371exclusions et deux échecs IntegrationPg faute de connexion de fixture ; aucun SQL n'est exécuté par ces deux cas. Cette invocation est conservée et ne sert pas de validation Unit. Le runner officiel est ensuite exécuté avec Profile=Unit,Backend,Release et les mêmes DLL déjà compilées :3863PASS,0FAIL,2exclusions explicites sur3865cas,sources stables. Les corrections de prompts ne sont pas encore une preuve de meilleur comportement du modèle réel. Les événements SQL fermés révèlent aussi zéro résultat sparse et exact pour les douze recherches de payé6,avec résultats denses disponibles et sans erreur de retriever ; un diagnostic borné des formulations de recherche est préparé avant un nouvel essai payant. TESTE_NON_APPROUVE maintenu.
