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
- [ ] Vérifier le retour stable de TEI et du reranker après warmup.
- [ ] Comprendre la stack Docker `saaia-rag` distincte de `infra`.
- [ ] Supprimer la stack redondante uniquement après preuve d’absence de consommateurs.
- [ ] Documenter les volumes, réseaux et dépendances des stacks SAAIA.

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

- [ ] Inventorier les documents disponibles sans exposer leur contenu sensible.
- [ ] Définir une première liste cuisine/scans.
- [ ] Ajouter PDF natifs avec images contenant du texte.
- [ ] Ajouter multi-colonnes, tableaux, listes, figures et légendes.
- [ ] Sélectionner ensuite documents techniques et autres catégories.
- [ ] Définir les annotations gold minimales.
- [ ] Capturer texte, blocs, ordre, tables et citations attendus.
- [ ] Versionner la liste et les hashes des documents.
- [ ] Créer un runner de benchmark reproductible.
- [ ] Capturer la baseline PdfPig/OCRmyPDF/Tesseract.
- [ ] Capturer temps CPU/RAM par page.
- [ ] Capturer qualité de retrieval et réponses.

## P0 — Contrats canoniques

- [ ] Créer un projet/module de contrats documentaires indépendant.
- [ ] Définir `CanonicalDocument`.
- [ ] Définir `CanonicalPage`.
- [ ] Définir `CanonicalBlock`.
- [ ] Définir `CanonicalSpan`.
- [ ] Définir `CanonicalTable` et `CanonicalTableCell`.
- [ ] Définir `CanonicalFigure` et relations de légende.
- [ ] Définir le section tree.
- [ ] Définir `SourceAnchor`.
- [ ] Définir `IngestionManifest`.
- [ ] Séparer raw/canonical/normalized/retrieval/display text.
- [ ] Définir des IDs stables.
- [ ] Versionner tous les schémas.
- [ ] Ajouter tests de sérialisation.
- [ ] Ajouter tests de reconstruction des preuves.
- [ ] Garder les fichiers de contrats courts et centrés.

## P0 — Persistance et provenance

- [ ] Concevoir les migrations pour pages, blocs, spans et tables.
- [ ] Conserver bboxes/polygons normalisés.
- [ ] Conserver méthode, moteur, version et confiance.
- [ ] Attacher le manifeste complet à la révision.
- [ ] Inclure le hash du manifeste dans la reprise.
- [ ] Rendre incompatibles les checkpoints produits par un autre pipeline.
- [ ] Stocker les artefacts canoniques compressés.
- [ ] Ajouter validation de complétude par page.
- [ ] Ajouter réconciliation SQL/Qdrant.
- [ ] Préparer staging et promotion.
- [ ] Tester rollback.

## P1 — Sidecar document intelligence

- [ ] Créer un composant/service séparé du backend C#.
- [ ] Définir API interne et versioning.
- [ ] Ajouter health/readiness.
- [ ] Ajouter timeouts et cancellation.
- [ ] Ajouter limites mémoire/CPU/GPU.
- [ ] Ajouter stockage/cache des modèles.
- [ ] Ajouter mode offline.
- [ ] Ajouter profiling par stage.
- [ ] Ajouter overlays de debug.
- [ ] Ajouter nettoyage sûr des temporaires.
- [ ] Éviter un script Python monolithique.

## P1 — Adaptateur pipeline actuel

- [ ] Envelopper PdfPig dans l’interface de parseur.
- [ ] Projeter le résultat dans `CanonicalDocument`.
- [ ] Conserver les coordonnées PdfPig.
- [ ] Conserver texte brut et ordre reconstruit.
- [ ] Marquer les éléments non disponibles plutôt que les inventer.
- [ ] Reproduire la baseline sans régression.

## P1 — Docling

- [ ] Épingler version, dépendances et hashes.
- [ ] Installer dans un environnement isolé.
- [ ] Tester CPU.
- [ ] Tester NVIDIA T2000.
- [ ] Tester le backend PDF natif.
- [ ] Tester OCR par régions.
- [ ] Tester tables.
- [ ] Tester hiérarchie de titres.
- [ ] Exporter DoclingDocument vers `CanonicalDocument`.
- [ ] Conserver bboxes et provenance.
- [ ] Mesurer mémoire et VRAM.
- [ ] Comparer à la baseline.

## P1 — RapidOCR/OpenVINO/ONNX

- [ ] Installer OpenCL Intel sur l’hôte si compatible.
- [ ] Vérifier l’UHD 630 avec `clinfo`.
- [ ] Passer `/dev/dri` au sidecar uniquement.
- [ ] Installer RapidOCR ONNX.
- [ ] Installer OpenVINO.
- [ ] Tester CPU.
- [ ] Tester Intel GPU.
- [ ] Tester NVIDIA via backend compatible si pertinent.
- [ ] Comparer précision et vitesse.
- [ ] Choisir le device par mesure.

## P1 — PaddleOCR/PP-Structure

- [ ] Installer dans un environnement isolé distinct si conflits.
- [ ] Tester PP-OCRv6 tiny/small/medium.
- [ ] Tester PP-StructureV3 avec modules minimaux.
- [ ] Tester tables et multi-colonnes.
- [ ] Mesurer CPU/RAM/VRAM.
- [ ] Convertir vers `CanonicalDocument`.
- [ ] Comparer à Docling et au pipeline actuel.
- [ ] Ne conserver que les variantes gagnantes.

## P1 — Routage adaptatif

- [ ] Définir uniquement des signaux mécaniques génériques.
- [ ] Mesurer couverture d’image.
- [ ] Mesurer qualité de couche texte.
- [ ] Détecter structure complexe.
- [ ] Détecter divergence native/OCR.
- [ ] Détecter confiance basse.
- [ ] Implémenter escalade par page/région.
- [ ] Enregistrer chaque décision et motif.
- [ ] Ne jamais router selon catégorie.
- [ ] Tester que les pages simples restent rapides.
- [ ] Tester que les pages difficiles sont escaladées.

## P1 — Gestion matérielle

- [ ] Créer un probe serveur générique.
- [ ] Détecter CPU, RAM et instructions.
- [ ] Détecter NVIDIA, VRAM, compute capability et pilote.
- [ ] Détecter Intel GPU/OpenVINO.
- [ ] Exécuter microbenchmarks par moteur.
- [ ] Persister le profil retenu.
- [ ] Créer un bail GPU.
- [ ] Gérer coexistence llama/OCR/layout.
- [ ] Décharger les modèles inactifs.
- [ ] Configurer batch et concurrence dynamiquement.
- [ ] Prévoir profil GPU 4 GiB.
- [ ] Prévoir profils 8/12/16/24 GiB et plus.
- [ ] Toujours conserver fallback CPU.

## P1 — Sections et unités

- [ ] Construire le section tree depuis les blocs.
- [ ] Conserver le bloc titre exact.
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
- [ ] Ne pas traverser les sections sans justification.
- [ ] Répéter les en-têtes dans les slices de table.
- [ ] Garder les cellules sources.
- [ ] Garder warnings indivisibles.
- [ ] Transporter titres/légendes en metadata.
- [ ] Conserver SourceAnchor.
- [ ] Benchmarker tailles et overlap.
- [ ] Séparer texte de preuve et texte d’index.

## P1 — Contextualisation et profils

- [ ] Conserver contextualisation désactivable.
- [ ] Comparer contexte déterministe et contexte LLM futur.
- [ ] Versionner prompt/modèle.
- [ ] Ne jamais utiliser mémoire comme preuve.
- [ ] Produire candidats de cartes ancrés.
- [ ] Laisser le LLM futur classifier le sens.
- [ ] Vérifier mécaniquement les block IDs.
- [ ] Supprimer les cartes sans ancre.
- [ ] Éliminer le bruit upstream plutôt que par migrations successives.

## P1 — Indexation et retrieval

- [ ] Ajouter block/span IDs au payload.
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
- [ ] Tester ancres et citations.
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
- [ ] Lancer tests ciblés.
- [ ] Lancer tests complets pertinents.
- [ ] Valider live.
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
