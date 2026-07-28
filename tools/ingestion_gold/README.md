# Corpus gold de mise en page

Ce dossier contient un harnais reproductible pour qualifier la première étape
de l'ingestion documentaire sans dépendre d'une catégorie métier ni conserver
de document privé dans Git.

## Ce qui est mesuré

Le manifeste `config/ingestion-layout-gold.v1.json` décrit des PDF synthétiques
et leurs invariants :

- présence et fidélité de marqueurs textuels ;
- ordre de lecture ;
- types de blocs ;
- géométrie normalisée ;
- dimensions, cellules et spans des tableaux ;
- OCR de texte raster et scans pleine page.
- sortie canonique réelle après réconciliation, identité des preuves et
  inventaire géométrique des images PDF natives.

Les PDF sont générés de façon déterministe et leur SHA-256 est versionné. Les
attentes du corpus servent uniquement aux tests : elles ne sont jamais chargées
par le runtime d'ingestion.

## Commandes principales

Tests unitaires du harnais :

```powershell
python -m unittest tools.ingestion_gold.tests.test_evaluator -v
```

Qualification du sidecar déployé :

```powershell
python -m tools.ingestion_gold.runner `
  --ssh-target maxime@100.80.213.61 `
  --output tmp/pdfs/ingestion-layout-gold-run/report.json
```

Par défaut, le runner construit le petit projecteur C# de qualification, puis
évalue séparément la réponse brute Docling et la sortie canonique réellement
publiée par le backend. Le rapport v2 ne réussit que si la couche canonique
réussit. `--skip-canonical` reste disponible pour un diagnostic volontairement
limité au sidecar ; `--case-id` permet de rejouer un ou plusieurs cas précis.

Comparaison directe d'un modèle Docling dans l'environnement qui possède
Docling :

```powershell
python -m tools.ingestion_gold.layout_model_probe `
  first.pdf second.pdf `
  --model egret-large `
  --ocr `
  --output-dir tmp/model-response
```

Réévaluation de réponses déjà capturées :

```powershell
python -m tools.ingestion_gold.evaluate_responses `
  --response-dir tmp/model-response `
  --output tmp/model-response/report.json
```

Export de métriques structurelles sans recopier le texte des documents :

```powershell
$responses = (Get-ChildItem tmp/model-response/*.response.json).FullName
python -m tools.ingestion_gold.structural_metrics `
  $responses `
  --output tmp/model-response/structural-metrics.json
```

Validation de la seconde lecture géométrique du backend :

```powershell
dotnet test backend/SAAIA.Backend.Tests/SAAIA.Backend.Tests.csproj `
  --filter "FullyQualifiedName~PdfNativeLayoutExtractorTests|FullyQualifiedName~CanonicalNativeLayoutReconcilerTests|FullyQualifiedName~CanonicalNativePdfImageInventoryReconcilerTests"
```

Cette seconde lecture utilise les algorithmes standards PdfPig
`NearestNeighbourWordExtractor`, `RecursiveXYCut` et
`UnsupervisedReadingOrderDetector`. Elle ne remplace pas Docling :

- Docling reste la source structurée des tableaux, figures, titres et ancres ;
- PdfPig conserve une lecture géométrique alternative lorsque le même calque
  natif contient les mêmes mots dans un ordre sensiblement différent ;
- les fragments situés dans une table canonique sont écartés quand la région
  hors table est suffisamment représentative ;
- une garde de fragmentation empêche une page de cellules isolées de devenir
  une pseudo-narration ;
- les blocs alternatifs gardent page, polygone, moteur, version, algorithme,
  accords mesurés et le marqueur `semanticDecisionOwner=llm_client`.
- PdfPig inventorie aussi chaque placement d’image embarquée avec sa page, son
  polygone et ses dimensions en pixels ; aucune légende ni interprétation
  sémantique n’est inventée par le backend, et le LLM client reste le décideur.

Le corpus mesure séparément la sortie brute du sidecar et la sortie canonique.
Une sortie Docling peut donc échouer l'invariant d'ordre ou omettre une figure
tandis que la représentation backend restaure une surface de lecture ou un
ancrage mécanique. Les deux niveaux ne doivent pas être confondus dans un
rapport.

## Discipline de promotion

Un modèle ou un correcteur n'est jamais promu sur un seul PDF. La décision doit
séparer au minimum :

1. fidélité du texte ;
2. ordre de lecture ;
3. tables et cellules ;
4. figures et légendes ;
5. provenance et géométrie ;
6. latence froide et chaude ;
7. mémoire et matériel utilisés.

Une amélioration d'ordre ne justifie pas la perte d'une table ou d'une figure.
Les résultats réels et les overlays restent sous `tmp/`, qui est ignoré par
Git, et sont supprimés à la fin d'une campagne.
