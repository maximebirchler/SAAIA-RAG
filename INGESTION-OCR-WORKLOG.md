# Journal de travail vivant — ingestion, OCR et indexation

Dernière mise à jour : 2026-07-26

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

### Stack SAAIA supplémentaire à clarifier

Une seconde stack nommée `saaia-rag` a été observée :

- `saaia-rag-postgres`
- `saaia-rag-qdrant`
- `saaia-rag-embeddings`

Après redémarrage, `saaia-rag-embeddings` redémarrait en boucle.

À déterminer :

- stack encore utile ou ancienne ;
- consommateurs ;
- volumes et données ;
- conflit éventuel avec la stack `infra` ;
- possibilité de suppression après preuve.

Ne rien supprimer avant d’avoir établi les dépendances.

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

Manques :

- manifeste complet ;
- tables de blocs/spans/cellules ;
- texte brut conservé ;
- bboxes ;
- staging vectoriel explicitement validé ;
- réconciliation Qdrant/SQL.

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
| 2026-07-26 | Docling premier candidat, pas vainqueur automatique | Meilleur contrat initial, mais benchmark local obligatoire | Acté |
| 2026-07-26 | Sidecar document intelligence | Isolation Python/modèles et code C# plus propre | À valider par prototype |
| 2026-07-26 | Intel UHD et NVIDIA choisies par mesure | Éviter les suppositions matérielles | Acté |
| 2026-07-26 | Redémarrage serveur pour réparer le pilote | Driver/library mismatch | Terminé |

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

## Questions ouvertes

1. La stack `saaia-rag` est-elle encore utilisée ?
2. L’Intel UHD 630 bat-elle le CPU pour RapidOCR/OpenVINO sur ce serveur ?
3. Docling standard tient-il confortablement avec la T2000 4 GiB ?
4. Comment arbitrer la T2000 entre Qwen3 (3,42 GiB mesurés) et les moteurs documentaires GPU sans provoquer d’OOM ?
5. Faut-il suspendre/décharger le LLM pendant les ingestions lourdes ?
6. Quels documents peuvent légalement être inclus dans un corpus gold versionné ?
7. Quel format d’artefact canonique offre le meilleur compromis JSON/MessagePack/Parquet ?
8. Quelle stratégie de shadow index minimise le risque de migration ?

## Prochain jalon

1. Clarifier les stacks Docker SAAIA.
2. Capturer une baseline de documents.
3. Créer les contrats canoniques.
4. Prototyper le sidecar Docling sans modifier encore le chemin actif.
5. Mesurer une politique explicite de partage/déchargement GPU entre Qwen3 et l’OCR.
