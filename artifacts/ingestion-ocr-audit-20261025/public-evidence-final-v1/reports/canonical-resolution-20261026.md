# Résolution canonique/Docling — état de travail du 26 octobre 2026

Ce nouveau document complète l’audit scellé ; il ne réécrit ni les traces ni le résultat des essais précédents. Les sources et tests ci-dessous sont gelés pour compilation/exécution centralisée par root. Aucun service, modèle, appel réseau ou commande Git exécuté par cet agent.

## Preuves disponibles

- Capture root `tests/docling-fidelity-red-v2/manifest.json` : 11 cas exécutés, 10 FAIL causaux, un témoin PASS. La capture v1 contient uniquement une erreur de compilation de fixture (`ExtractedPdfPage[]` au lieu de `List`) corrigée séparément ; ce n’est pas un RED comportemental.
- Python pur, `test_execution_metadata.py` : 3/3 PASS ; syntaxe des quatre fichiers Python touchés vérifiée. Ces contrôles ne lancent ni Docling, ni RapidOCR, ni FastAPI, ni un service.
- Le manifeste `tests/fidelity-first-build-v2/manifest.json` a été relu : 79 cas, 65 PASS/14 FAIL, sources stables pendant le run. Dans ce lot, les **19 cas ciblés fidélité/attestation Docling passent**, tandis que les trois cas de capacité C10 restent RED ; les 11 autres FAIL appartiennent aux RED TEI gérés par root. Ce résultat n’est pas présenté comme une suite globalement verte.
- Le manifeste ultérieur `tests/unit-all-v2/manifest.json` a été relu indépendamment : **5206 PASS, un non exécuté**, zéro échec, `sourceChangedDuringRun=false`. Répartition : Backend2685 PASS/un non exécuté, Contracts20, ClientCore82, Client2419. Les cas C10, confidentialité HTTP/status/schema, cohérence unité/SectionTree et couverture littérale passent à cet ancrage. Cette preuve précède le nouveau chantier bail/fencing/overfetch ; elle ne valide pas ces dernières modifications.
- L’intégration du vrai worker reste **en attente**. Les nouveaux tests complémentaires non présents dans la capture RED initiale ne sont pas présentés comme des RED exécutés.

## Correctifs présents et portée de la validation

C01 : la fréquence en marge est une annotation. Elle ne change plus le texte de retrieval et ne suffit plus à jeter un bloc. Les rôles furniture/header/footer ne retirent plus les avertissements ; leur contenu non attribué est conservé comme contexte physique. Le compteur égal au numéro physique d’une page, déjà déclaré header/footer/furniture, conserve une garde distincte.

C02/C11 : un ajout natif sans relation canonique et les textes de marge incertains portent `physical_page_context_v1`, une composition `canonical_physical_context_not_asserted`, leurs identifiants de blocs et aucun titre/section inféré. Une table d’index située ailleurs sur la page n’impose plus son rôle à l’ajout natif. Les alternatives de layout restent séparées ; leur ordre mécanique n’est pas une preuve de relation sémantique.

C03 : la similarité ordonnée/sac de mots reste un diagnostic et un outil d’extension du contexte ; elle ne prouve plus l’absence de différence littérale. Le composant distinct compare des observations canoniques complètes à des fenêtres de lignes natives complètes, avec simple normalisation d’espaces. Il ne confond plus une valeur avec sa variante négative/décimale ou un statut nié. Les données d’un mot ou chiffrées ne sont plus rejetées en amont uniquement pour leur longueur. Une occurrence canonique ne représente qu’une occurrence native ; les anchors exacts des anciennes récupérations maintiennent l’idempotence. Les variantes contradictoires restent observables ; aucune valeur vraie n’est choisie par ce mécanisme.

C04 : une page native connue absente du canonique Docling entraîne un refus explicite `docling_page_inventory_incomplete`. Si l’audit natif n’est pas disponible, ce fait est séparément marqué `unavailable` ; aucune couverture complète n’en est déduite.

C05 : `DoclingTextRegionProjector` partage le découpage Unicode exact entre adaptation canonique et legacy. `CanonicalExtractionViewProjector` produit les pages et unités depuis le même canonique réconcilié ; l’ordre des blocs/tableaux vient des références de lecture Docling. Les ajouts non localisés restent des observations terminales explicitement non attribuées. Les unités ne reçoivent une section que via un lien canonique non ambigu. Root a raccordé `CanonicalExtraction` dans le worker avant no-text et publication.

C08 : `RequestedForceOcr` conserve l’override envoyé et sert au hash des options effectives. Le sidecar atteste les réglages exécutés et les versions des packages installés via `importlib.metadata`. Le client refuse un désaccord de configuration connu ou une version déclarée comparable différente de celle observée. Les manifests privilégient les versions observées et distinguent la configuration non attestée et le deploymentRevision déclaratif. Les versions de packages ne sont pas un checksum des poids de modèle ; cette limite reste à raccorder au profil d’exécution contrôlé par root/runtime.

C09 : les tentatives repair `error/not_applied` ne disparaissent plus du manifeste. Le canonique peut conserver leur statut, leur raison bornée et un signal de dégradation tout en gardant la table originale. Le warning Python ne journalise plus une exception documentaire brute. Les timings OCR sont séparés des nombres de pages ; quand aucun ID de page n’est observé, les nombres/listes de pages sont nuls dans le payload durable, avec `pageCountsAvailable=false`. Les nouveaux champs de confiance heuristique et couverture OCR sont raccordés à la publication SQL en coordination avec l’agent OCR.

C06/reprise/vector et C07/snapshot source : responsabilité root, non réimplémentée par cet agent. Leur existence ne doit pas être déduite du seul correctif Docling.

C10 : trois cas RED ont été capturés dans le lot `fidelity-first-build-v2`, puis passent dans `unit-all-v2` : refus explicite `docling_unprojected_structured_items` pour les objets form/key-value non projetés ; flag `figure_semantics_not_extracted` et statut `not_performed` sur les figures inventoriées. Aucune prise en charge multimodale n’est inventée. La projection de tableaux multi-provenance reste à éprouver avec un témoin réel de schéma.

Complément confidentialité : les trois cas HTTP/status/schema passent dans `unit-all-v2` et vérifient l’absence d’un sentinel privé dans l’exception complète. Le client conserve code/statut sans corps HTTP, valeur status/schema retournée ou exception parser interne pouvant publier un chemin JSON documentaire.

Contre-revue : le trou où un bloc de marge pouvait être non attribué dans les chunks mais lié dans SectionTree/ProjectUnits a été reproduit dans `final-gaps-red`, puis corrigé par la politique partagée `CanonicalSourceAssociationPolicy` sur les trois vues. Le cas correspondant et le témoin de titre explicitement déclaré passent dans `unit-all-v2`.

## Fichiers du périmètre

Produit : `CanonicalRepeatedPageFurnitureClassifier.cs`, `CanonicalNativeTextCoverageReconciler.cs`, nouveaux `CanonicalLiteralTextCoverage.cs`, `CanonicalSourceAssociationPolicy.cs` et `CanonicalExtractionViewProjector.cs`, `IngestionProjectionStage.cs`, `DocumentExtractionPolicy.cs`, la sérialisation/qualité de `DocumentFoundationRepo.cs` ; côté Docling `DoclingCanonicalDocumentAdapter.cs`, `DoclingCanonicalRetrievalProjector.cs`, `DoclingLegacyExtractionAdapter.cs`, `DoclingModels.cs`, `DoclingClient.cs`, `DoclingIngestionStageManifestFactory.cs`, nouveaux `DoclingTextRegionProjector.cs` et `DoclingExecutionMetadataPolicy.cs`.

Sidecar : nouveau `execution_metadata.py`, raccord dans `app.py`, warning borné dans `response_repair.py`.

Tests : `DoclingCanonicalDocumentAdapterTests.SourceFidelity.cs`, `DoclingClientTests.ExecutionMetadata.cs`, mot-clé partial sur leurs deux fichiers de fixtures existants, nouveau `test_execution_metadata.py`.

Prochaine étape : fermer et vérifier séparément les nouveaux correctifs de bail/fencing et de recherche dense bornée, puis vérifier l’ingestion complète par le vrai worker, SQL/Qdrant et les endpoints. La conversion Docling isolée et la suite unitaire ne constituent pas cette validation complète.
