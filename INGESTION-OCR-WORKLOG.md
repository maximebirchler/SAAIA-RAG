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
