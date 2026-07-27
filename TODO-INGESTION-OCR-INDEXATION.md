# TODO vivant — ingestion, OCR et indexation

Dernière mise à jour : 2026-07-27

Ce TODO est la liste exécutable de la refonte décrite dans :

- `INGESTION-OCR-WORKLOG.md`
- `AUDIT-2026-07-26-INGESTION-OCR-INDEXATION.md`

## Légende

- `[ ]` à faire
- `[-]` en cours
- `[x]` terminé et vérifié
- `[!]` bloqué avec raison documentée

## P0 — Maintenance et inventaire serveur

- [x] Inspecter le serveur réel plutôt que le matériel client.
- [x] Capturer OS, CPU, RAM, GPU, stockage, Docker et runtimes.
- [x] Identifier le mismatch du pilote NVIDIA.
- [x] Redémarrer le serveur avec autorisation utilisateur.
- [x] Vérifier `nvidia-smi` après redémarrage.
- [x] Vérifier le retour de PostgreSQL, Qdrant, backend et llama.
- [x] Vérifier le retour stable de TEI après warmup et conserver le reranking dans le chemin `infra` qualifié.
- [x] Comprendre la stack Docker `saaia-rag` distincte de `infra`.
- [x] Supprimer la stack redondante uniquement après preuve d’absence de consommateurs.
- [x] Documenter les volumes, réseaux et dépendances des stacks SAAIA.
- [x] Retirer le profil d’alimentation local qui imposait silencieusement
  `no_turbo=1` et `max_perf_pct=25` la nuit ; conserver le turbo autorisé,
  `max_perf_pct=100` et le profil thermique Dell Performance.
- [x] Éliminer le polling actif du worker au repos : délai effectif de 500 ms
  borné, journalisé et exposé par `/ready`, alias `PollSeconds` défectueux
  retiré et prise atomique d’un job sans transaction explicite superflue.
- [x] Mettre en cache les SHA-256 du scanner par chemin/taille/mtime et charger
  les chemins de jobs actifs en une requête par cycle ; après amorçage,
  200/200 hashes sont servis par le cache sur le serveur de référence.

## P0 — Remplacement LLM strictement limité

- [x] Télécharger `Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf`.
- [x] Vérifier le SHA-256 attendu `66713ce35a58a82fe87642d4ec13425bf9b9a46800fff5c49a665ef5701439dc`.
- [x] Capturer la configuration llama précédente avant remplacement.
- [x] Remplacer le modèle serveur par Qwen3 sans lui ajouter de rôle fonctionnel.
- [x] Aligner `ubatch=128` pour le profil Qwen3 de référence.
- [x] Qualifier puis épingler llama.cpp CUDA `b10098`.
- [x] Redéployer seulement la stack `saaia-llm`.
- [x] Attendre `/v1/models`.
- [x] Vérifier `/ready`.
- [x] Mesurer VRAM, TTFT et débit court.
- [x] Vérifier que les changements backend restent limités au transport/configuration LLM.
- [x] Supprimer physiquement Qwen2.5 après validation, conformément à la décision utilisateur.
- [x] Supprimer les runtimes Vulkan, CUDA rolling et CPU devenus inutiles.

## P0 — Corpus et baseline

- [x] Inventorier les documents disponibles sans exposer leur contenu sensible.
- [x] Définir une première liste cuisine/scans.
- [ ] Ajouter PDF natifs avec images contenant du texte.
- [ ] Ajouter multi-colonnes, tableaux, listes, figures et légendes.
- [ ] Sélectionner ensuite documents techniques et autres catégories.
- [ ] Définir les annotations gold minimales.
- [ ] Capturer texte, blocs, ordre, tables et citations attendus.
- [x] Versionner les sélecteurs du corpus et exporter localement les hashes sans nom, chemin, texte ou ID interne.
- [x] Créer un runner d’inventaire reproductible et rapide.
- [ ] Capturer la baseline PdfPig/OCRmyPDF/Tesseract.
- [ ] Capturer temps CPU/RAM par page.
- [ ] Capturer qualité de retrieval et réponses.

## P0 — Contrats canoniques

- [x] Créer un module de contrats documentaires indépendant du backend dans `SAAIA.Contracts`.
- [x] Définir `CanonicalDocument`.
- [x] Définir `CanonicalPage`.
- [x] Définir `CanonicalBlock`.
- [x] Définir `CanonicalSpan`.
- [x] Définir `CanonicalTable` et `CanonicalTableCell`.
- [x] Définir `CanonicalFigure` et relations de légende.
- [x] Définir le section tree.
- [x] Définir `SourceAnchor`.
- [x] Définir `IngestionManifest`.
- [x] Séparer raw/canonical/normalized/retrieval/display text.
- [x] Définir des IDs stables et délimités.
- [x] Versionner tous les schémas canoniques initiaux.
- [x] Ajouter tests de sérialisation et de hash canonique.
- [x] Ajouter tests de reconstruction et d’intégrité des preuves.
- [x] Garder les fichiers de contrats courts et centrés.

## P0 — Persistance et provenance

- [-] Concevoir les migrations pour pages, blocs, spans et tables ; bundle immuable compressé et ancres requêtables terminés, modèle relationnel détaillé à décider après benchmark.
- [x] Conserver bboxes/polygons normalisés dans le document canonique Docling.
- [x] Conserver méthode, moteur, version, matériel et confiance dans les contrats canoniques.
- [x] Attacher le manifeste complet à la révision dans le bundle canonique.
- [x] Inclure le hash du manifeste dans le run de traitement, le document canonique et chaque ancre.
- [ ] Rendre incompatibles les checkpoints produits par un autre pipeline.
- [x] Stocker les artefacts canoniques compressés avec taille et SHA-256 vérifiés.
- [x] Ajouter validation mécanique de complétude, géométrie, références et identités par page.
- [x] Ajouter réconciliation SQL/Qdrant ; jointure ancres/chunks et signaux de qualité comparés live sur les deux stockages.
- [x] Auditer la couverture de la couche texte native indépendamment de
  Docling, restaurer seulement les lacunes prouvées et conserver moteur,
  version, page, lignes, couvertures et propriétaire de la décision
  sémantique dans le bundle et le manifeste.
- [ ] Préparer staging et promotion.
- [ ] Tester rollback.

## P1 — Sidecar document intelligence

- [x] Créer un composant/service séparé du backend C#.
- [x] Définir une API interne compatible Docling et versionner le schéma de réparation.
- [x] Ajouter health/readiness du wrapper et du Docling interne.
- [-] Ajouter timeouts et cancellation ; timeouts bornés terminés, annulation du thread proxy à compléter.
- [x] Aligner le budget synchrone Docling Serve sur le timeout document
  (`900 s`) et borner les conversions HTTP au nombre de workers réellement
  configuré ; validation live avec deux jobs concurrents et démarrage à froid.
- [x] Ajouter limites mémoire/CPU/PID et garder le GPU hors du profil CPU actuel.
- [x] Réutiliser le cache et les modèles RapidOCR présents dans l’image Docling épinglée.
- [x] Ajouter un mode offline sans téléchargement de modèle au runtime.
- [x] Ajouter profiling par stage dans le manifeste canonique.
- [ ] Ajouter overlays de debug.
- [x] Ajouter nettoyage sûr des temporaires.
- [x] Séparer proxy, rendu PDF, OCR, décision mécanique et reconstruction.

## P1 — Adaptateur pipeline actuel

- [-] Envelopper PdfPig dans l’interface de parseur ; projection isolée créée, interface multi-parseur encore à extraire.
- [x] Projeter le résultat dans `CanonicalDocument`.
- [-] Conserver les coordonnées PdfPig ; dimensions de page conservées, géométrie fine de blocs/spans encore absente du pipeline historique.
- [x] Conserver texte brut et ordre de page reconstruit.
- [x] Marquer les éléments non disponibles plutôt que les inventer.
- [x] Reproduire une ingestion cuisine live sans régression et publier le bundle atomiquement.
- [x] Réconcilier le texte natif PdfPig avec le canonique Docling sans
  remplacer la structure principale, avec échec ouvert si l'extraction
  indépendante n'est pas disponible.

## P1 — Docling

- [x] Épingler version, dépendances et hash de l’image.
- [x] Installer dans un environnement isolé.
- [x] Tester CPU sur PDF natif, scan réel et tableau.
- [ ] Tester NVIDIA T2000.
- [x] Tester le backend PDF natif.
- [x] Tester OCR par régions et l’intégrer sélectivement au sidecar.
- [x] Tester les tables ; structure TableFormer V1 conservée et correction régionale validée live.
- [-] Tester hiérarchie de titres ; projection réelle validée, corpus gold multi-colonnes à compléter.
- [x] Exporter DoclingDocument vers `CanonicalDocument`.
- [x] Conserver bboxes, provenance de page et charspans exacts.
- [x] Mesurer CPU, RAM et absence d’usage VRAM du profil actuel.
- [-] Comparer à la baseline ; scan/tableau de référence corrigé et mesuré, corpus complet encore à annoter.
- [x] Prouver sur un sommaire complexe que les lignes perdues par Docling
  sont restaurées, projetées en navigation et résolues vers une ancre et un
  chunk exacts.

## P1 — RapidOCR/OpenVINO/ONNX

- [ ] Installer OpenCL Intel sur l’hôte si compatible.
- [ ] Vérifier l’UHD 630 avec `clinfo`.
- [ ] Passer `/dev/dri` au sidecar uniquement.
- [x] Inventorier et qualifier RapidOCR ONNX déjà présent dans l’image Docling épinglée.
- [ ] Installer OpenVINO.
- [x] Tester CPU sur une région de tableau réelle : 38 lignes OCR en 0,727 s après initialisation.
- [ ] Tester Intel GPU.
- [ ] Tester NVIDIA via backend compatible si pertinent.
- [x] Comparer précision et vitesse sur le tableau de référence ; RapidOCR gagne et est intégré.
- [ ] Choisir le device par mesure.

## P1 — PaddleOCR/PP-Structure

- [x] Installer PaddlePaddle/PaddleOCR/PaddleX dans un environnement isolé distinct.
- [ ] Tester PP-OCRv6 tiny/small/medium.
- [x] Tester le pipeline de structure de table PP-TableMagic avec modules minimaux.
- [-] Tester tables et multi-colonnes ; tableau scanné mesuré, multi-colonnes à ajouter.
- [-] Mesurer CPU/RAM/VRAM ; durée CPU mesurée, pic RAM à instrumenter.
- [!] Convertir vers `CanonicalDocument` ; inutile pour le candidat rejeté avant intégration.
- [x] Comparer à Docling et au pipeline actuel sur le tableau de référence.
- [x] Ne conserver que les variantes gagnantes ; conteneurs, caches Paddle/TATR et TableFormer V2 expérimentaux purgés.

## P1 — Réparation régionale des tableaux

- [x] Ajouter des signaux mécaniques génériques pour grilles clairsemées ou chevauchées.
- [x] Propager ces signaux uniquement aux chunks de table, puis les vérifier dans PostgreSQL et Qdrant.
- [x] Qualifier TableFormer V1/V2 et TATR v1.1 comme estimateurs de structure.
- [x] Qualifier RapidOCR PP-OCRv6 comme moteur OCR régional CPU.
- [x] Prouver hors production la reconstruction exacte des 27 cellules du tableau de référence.
- [x] Intégrer la réparation ciblée en conservant structure, texte brut, géométrie, confiance et provenance.
- [ ] Ajouter TATR seulement comme challenger de structure lorsqu’un signal mécanique le justifie.
- [-] Rejouer le corpus cuisine/scans, puis les autres catégories sans règle spécifique au document ; scan de référence et PDF simple terminés.

## P1 — Routage adaptatif

- [x] Définir uniquement des signaux mécaniques génériques ; couverture et chevauchement de grille ajoutés sans décision sémantique.
- [ ] Mesurer couverture d’image.
- [x] Mesurer la qualité de couche texte page par page avant toute
  réconciliation ; les cinq pages natives non fiables du scan SMS 1145 sont
  toutes refusées.
- [-] Détecter structure complexe ; tables clairsemées/chevauchées couvertes, colonnes et figures à compléter.
- [x] Détecter la divergence native/canonique par couverture ordonnée et
  couverture multiensemble, avec nettoyage préalable des ligatures PDF
  éclatées et traitement des continuations de mots.
- [ ] Détecter confiance basse.
- [-] Implémenter escalade par page/région ; région de table couverte, autres structures à venir.
- [x] Enregistrer chaque déclenchement, refus, erreur, durée et motif de réparation de table.
- [ ] Ne jamais router selon catégorie.
- [x] Tester qu’un PDF simple sans tableau ne déclenche aucune seconde lecture.
- [x] Tester que le tableau difficile de référence est escaladé et réparé.

## P1 — Gestion matérielle

- [x] Créer un premier probe serveur générique et testé.
- [-] Détecter CPU, RAM et instructions ; CPU/RAM terminés, ISA à ajouter.
- [-] Détecter NVIDIA, VRAM, compute capability et pilote ; inventaire/VRAM/pilote terminés, compute capability à ajouter.
- [-] Détecter Intel GPU/OpenVINO ; DRM Intel terminé, capacité OpenVINO à mesurer.
- [-] Exécuter microbenchmarks par moteur ; Docling/TableFormer, RapidOCR, Paddle et TATR couverts sur le tableau de référence.
- [ ] Persister le profil retenu.
- [ ] Créer un bail GPU.
- [ ] Gérer coexistence llama/OCR/layout.
- [ ] Décharger les modèles inactifs.
- [-] Configurer batch et concurrence dynamiquement ; le client Docling
  respecte `ServiceWorkerConcurrency` et un gouverneur partagé sérialise
  désormais Docling/OCR et les embeddings TEI sur le profil CPU commun.
  L’auto-profilage multi-accélérateurs reste à implémenter.
- [ ] Prévoir profil GPU 4 GiB.
- [ ] Prévoir profils 8/12/16/24 GiB et plus.
- [ ] Toujours conserver fallback CPU.

## P1 — Sections et unités

- [x] Construire le section tree depuis les blocs ; hiérarchie native Docling
  persistée dans Foundation et validée live sur le PDF cuisine.
- [x] Conserver le bloc titre exact et le chemin complet dans les chunks,
  PostgreSQL, la contextualisation et Qdrant.
- [ ] Créer unités typées.
- [ ] Gérer paragraphes.
- [ ] Gérer listes imbriquées.
- [ ] Gérer warnings.
- [ ] Gérer tables/cellules.
- [ ] Gérer figures/légendes.
- [ ] Gérer code/formules comme types.
- [ ] Supprimer les reconstructions regex remplacées.

## P1 — Chunking

- [ ] Utiliser le tokenizer réel.
- [ ] Créer chunks enfants de retrieval.
- [ ] Créer parents de preuve.
- [x] Ne pas traverser les sections sans justification ; le chunker canonique
  suit tous les niveaux Docling actifs et refuse la fusion de deux chemins
  hiérarchiques différents.
- [ ] Répéter les en-têtes dans les slices de table.
- [ ] Garder les cellules sources.
- [ ] Garder warnings indivisibles.
- [x] Transporter `sectionTitle`, `headingPath` et IDs des titres de contexte
  en metadata tout en conservant le contexte utile dans le texte d'index.
- [x] Conserver SourceAnchor pour chaque chunk canonique publié.
- [ ] Benchmarker tailles et overlap.
- [ ] Séparer texte de preuve et texte d’index.

## P1 — Contextualisation et profils

- [ ] Conserver contextualisation désactivable.
- [ ] Comparer contexte déterministe et contexte LLM futur.
- [ ] Versionner prompt/modèle.
- [ ] Ne jamais utiliser mémoire comme preuve.
- [-] Produire candidats de cartes ancrés ; les cartes des révisions canoniques
  sont maintenant une projection mécanique des vrais titres Docling avec plage
  de pages, l’ancre de bloc explicite par carte reste à matérialiser.
- [x] Laisser le LLM classifier le sens ; le profil canonique ne cherche plus
  de faux « titres » dans le texte des chunks et publie les en-têtes source
  sans décision sémantique codée.
- [x] Vérifier mécaniquement les block/span/table-cell IDs des chunks
  canoniques et leur correspondance exacte avec les SourceAnchors.
- [ ] Supprimer les cartes sans ancre.
- [-] Éliminer le bruit upstream plutôt que par migrations successives ;
  mobilier répété et cartes heuristiques du chemin canonique supprimés,
  relecture du reste du corpus encore requise.

## P1 — Indexation et retrieval

- [x] Ajouter block/span/table-cell IDs au payload PostgreSQL et Qdrant.
- [ ] Ajouter bbox et type de bloc.
- [ ] Ajouter manifeste et checksums.
- [ ] Renommer `sparse_bm25` en `postgres_fts` ou implémenter BM25 réel.
- [ ] Conserver exact+dense+lexical+reranker.
- [ ] Mesurer RRF/DBSF.
- [ ] Tester staging Qdrant.
- [ ] Valider comptes/checksums avant activation.
- [ ] Ajouter rollback.

## P1 — Tests

- [ ] Marquer clairement le test OCR E2E lorsqu’il n’est pas exécuté.
- [ ] Créer profil CI OCR réel.
- [ ] Ajouter vrais PDF fixtures légales.
- [ ] Tester bboxes.
- [ ] Tester ordre multi-colonnes.
- [ ] Tester tables.
- [ ] Tester images avec texte.
- [ ] Tester documents mixtes.
- [ ] Tester scans faibles.
- [ ] Tester multilingue.
- [x] Tester les ancres canoniques et la correspondance mécanique avec les chunks publiés.
- [x] Tester que le profil canonique conserve les vrais titres courts,
  déduplique les libellés répétés et n’invente plus de cartes à partir du
  contenu interne des chunks.
- [x] Tester la récupération d'un texte natif absent, la conservation de sa
  provenance, le refus du texte réordonné/bruité, les mots coupés et la
  reconstruction prudente d'une entrée de sommaire sur deux lignes.
- [x] Tester que le client Docling ne dépasse jamais la capacité déclarée du
  sidecar.
- [ ] Tester reprise incompatible.
- [ ] Tester publication partielle.
- [ ] Tester rollback.

## P2 — Interface d’administration

- [ ] Afficher route de parsing.
- [ ] Afficher versions et manifeste.
- [ ] Afficher overlays de blocs.
- [ ] Afficher ordre de lecture.
- [ ] Afficher confiance OCR.
- [ ] Afficher pages douteuses.
- [ ] Comparer deux parseurs.
- [ ] Retraiter page/plage.
- [ ] Forcer langue/parseur avec audit.
- [ ] Afficher cartes et ancres.

## P2 — Formats

- [ ] Généraliser la découverte par adaptateurs.
- [ ] PDF.
- [ ] Images.
- [ ] DOCX.
- [ ] PPTX.
- [ ] XLSX.
- [ ] HTML.
- [ ] Markdown/texte.
- [ ] Définir politique e-mails/pièces jointes.
- [ ] Garder un même `CanonicalDocument`.

## P3 — VLM et schémas techniques

- [ ] Préparer interface sans dépendance runtime.
- [ ] PaddleOCR-VL sur matériel adapté.
- [ ] MinerU VLM.
- [ ] olmOCR.
- [ ] Compréhension de graphiques.
- [ ] Schémas techniques.
- [ ] Formules.
- [ ] Traiter seulement les régions difficiles.
- [ ] Valider contre preuves visuelles.
- [ ] Activer uniquement si gain mesuré.

## Nettoyage et livraison

- [ ] Inventorier tous les anciens fallback.
- [ ] Retirer les chemins remplacés.
- [ ] Retirer les regex devenues inutiles.
- [ ] Conserver les migrations nécessaires aux installations existantes.
- [ ] Consolider le baseline d’une nouvelle installation si pertinent.
- [ ] Unifier toutes les configurations.
- [ ] Supprimer les scripts ad hoc transférés dans le harness.
- [ ] Mettre à jour CDC.
- [ ] Mettre à jour ADR.
- [ ] Mettre à jour le journal vivant.
- [ ] Mettre à jour ce TODO.
- [ ] Vérifier `git diff --check`.
- [x] Lancer les tests ciblés du contrat canonique Docling et des signaux de table.
- [x] Lancer la suite backend complète : 2 018/2 018 réussis après la
  réconciliation native, la navigation multi-ligne, le gouverneur de calcul,
  le cache scanner et la correction du polling worker.
- [x] Valider live la migration, le bundle, le manifeste, les SHA et les ancres sur le serveur.
- [x] Valider live la grille clairsemée du scan v34 dans le bundle canonique, PostgreSQL et Qdrant sans contamination du chunk textuel voisin.
- [x] Valider live le scan v35 réparé : grille 9 × 3, 27 cellules, stage RapidOCR, 27 ancres de cellule et concordance PostgreSQL/Qdrant.
- [x] Valider la hiérarchie cuisine : v109, 136/136 chunks liés et munis
  d'un `headingPath`, 145 sections canoniques, 30/30 liens de sommaire exacts.
- [x] Revalider le scan après la filiation canonique et le filtrage du
  mobilier : v38, 8/8 chunks liés, grille 9 × 3 / 27 cellules, RapidOCR,
  8/8 points Qdrant et 27 IDs de cellule.
- [x] Retirer génériquement les petits blocs de mobilier répétés encore
  classés `body` par Docling, sans chaîne ou document codé en dur : fréquence,
  géométrie de marge et corroboration du parseur ; v111 sans mobilier dans les
  114 chunks publiés.
- [x] Nettoyer mécaniquement le mobilier fusionné à un bloc de contenu sans
  détruire le brut canonique : une variante `retrieval` rognée, signalée et
  ancrée ; zéro occurrence parasite dans PostgreSQL et Qdrant sur v111.
- [x] Requalifier la réconciliation native sur quatre familles : cuisine
  v117 (0 récupération, 114/114 vecteurs), NIST v18 (5 blocs légitimes,
  35/35 liens exacts, 146/146 vecteurs), fiche 3M v8 (0 récupération,
  9/9 ancres) et scan SMS 1145 v41 (5/5 pages natives refusées, tableau
  9 × 3 / 27 cellules, 8/8 ancres).
- [x] Requalifier la concurrence après suppression de la limitation CPU :
  cuisine v119 terminée en 222,555 s et NIST v20 en 230,290 s, contre
  564,799 s et 589,788 s sous l’ancien profil bridé ; aucun chevauchement
  durable Docling/TEI, aucun 504 et qualité inchangée (114/114, 30/30,
  146/146 et 35/35).
- [x] Rejouer le harnais strict sur les 14 documents scannés après ces
  changements : `ok=true`, zéro problème, zéro avertissement et tous les
  comptes PostgreSQL/Qdrant exacts.
- [x] Détecter génériquement les couches texte contenant des contrôles C1,
  forcer l'OCR Docling par conversion et empêcher toute réinjection native
  corrompue : ABB v4, 16/16 pages OCRisées, 48 chunks/ancres/points, zéro C1.
- [x] Comparer Heron et Heron101 sur le même tableau PTFE : Heron conserve la
  grille correcte 23 × 25 / 436 cellules ; Heron101 tombe à 23 colonnes /
  431 cellules. Conserver Heron et retirer le challenger temporaire.
- [x] Requalifier cinq familles sans logique de catégorie : ABB v4, cuisine
  v67, PTFE v7, SDG v3 et Yosemite v4, soit 616 chunks, 616 ancres et
  616 points Qdrant ; aucune métadonnée mécanique de source manquante.
- [x] Rejouer 96 tests ciblés puis la suite backend Release complète après le
  correctif de couche native : 96/96 puis 2 023/2 023 réussis.
- [ ] Valider WinUI.
- [ ] Committer par lots cohérents.

## Condition de fin

La refonte ne peut être déclarée terminée que lorsque :

- les documents simples et complexes passent ;
- les scans et documents mixtes passent ;
- les tableaux et colonnes sont structurés ;
- les preuves sont ancrées ;
- le retrieval progresse ou ne régresse pas ;
- les réponses simples et le planning complexe passent ;
- les performances sont adaptées au matériel ;
- le rollback fonctionne ;
- les anciens chemins inutiles sont supprimés ;
- la documentation et les commits sont propres.
