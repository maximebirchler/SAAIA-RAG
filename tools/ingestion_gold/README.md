# Corpus Gold de conversion et de projection

Le manifeste courant est `config/ingestion-acceptance-gold.v2.json` : douze PDF
synthétiques, seize pages physiques. Les huit cas de mise en page historiques
sont complétés par quatre documents français : natif, mixte natif/scan/natif,
tableau sur deux pages et scan intégral. Le manifeste historique v1 reste
conservé ; son rendu non épinglé ne constitue pas une qualification actuelle.

Ce harnais mesure la conversion Docling puis la projection canonique du backend.
Il **ne lance pas le worker d'ingestion**, ne passe pas par sa file, ne publie pas
dans PostgreSQL et ne vérifie pas Qdrant. Un rapport réussi ne valide donc pas
l'ingestion de bout en bout. Les champs `ingestionWorkerExecuted`,
`sqlPublicationValidated` et `vectorValidated` restent explicitement faux.

## Contrats de fidélité

Les annotations sont écrites dans le manifeste indépendamment de toute sortie
d'extraction. Le générateur ne les lit pas pour construire le texte du document.
Elles couvrent les marqueurs, l'ordre, la géométrie, les types de blocs, les
cellules vides/fusionnées et des faits critiques exacts liés à leur page physique.
Les faits conservent signes, décimales, accents, unités, fractions et négations.
Seuls NFC et les espaces sont harmonisés ; aucun score flou ne valide ces faits.
Une valeur trouvée sur une page voisine ne valide pas la page attendue. La couche
canonique exige aussi leur présence dans un chunk ancré sur la bonne page.

Le projecteur C# utilise les valeurs courantes de `IngestionOptions` (200 mots
maximum, 25 minimum au moment de cette campagne), applique le filtre de publication
réel et conserve les SHA-256 des DLL backend/projecteur chargées. Le manifeste v2
exige ces défauts de production. Il ne rend pas publiables des cartes courtes en
abaissant artificiellement le minimum à un mot.

Le SHA-256 de chaque PDF, celui de la police et ceux des trois fichiers du
générateur sont épinglés, ainsi que Python, ReportLab et Pillow. La police doit
être fournie explicitement : aucun téléchargement, aucune substitution silencieuse.
Les PDF français et tous les rasters utilisent cette police. Les parties natives
des huit documents historiques gardent leurs polices PDF standard, dont le rendu
reste lié à la version épinglée de ReportLab. Le fichier de police Windows n'est
pas redistribué dans le dépôt. Toute dérive demande un nouveau manifeste relu ;
le harnais ne réapprend jamais ses attentes à partir de la sortie évaluée.

## Commandes locales

Les dépendances Python sont `reportlab` et `Pillow` pour la génération. `pypdf`
sert au contrôle indépendant des fichiers générés dans cette campagne. Le runner
HTTP n'importe pas Docling : son moteur et ses modèles sont gérés séparément.
Les tests purs ne lancent aucun service, OCR, téléchargement ou modèle :

```powershell
python -B -m unittest discover -s tools/ingestion_gold/tests -t . -v
```

Générer seulement les sources locales dans un dossier neuf :

```powershell
python -B -m tools.ingestion_gold.generate `
  --font-path C:/Windows/Fonts/arial.ttf `
  --output-dir artifacts/ma-campagne-neuve/sources
```

La génération refuse une version/police différente des pins du manifeste. Elle
préserve les PDF et écrit `generation-proof.json`. Cette preuve concerne seulement
les sources ; elle ne prétend pas avoir exécuté l'OCR.

La DLL `SAAIA.IngestionGold.dll` doit être compilée et vérifiée séparément avant
la campagne. Le runner ne construit et ne restaure aucun projet implicitement.
Une fois le service Docling local prêt, utiliser un autre dossier neuf :

```powershell
python -B -m tools.ingestion_gold.runner `
  --base-url http://127.0.0.1:15101 `
  --canonical-assembly CHEMIN_EXPLICITE/SAAIA.IngestionGold.dll `
  --font-path C:/Windows/Fonts/arial.ttf `
  --output artifacts/ma-conversion-neuve/report.json
```

`--case-id` permet de sélectionner un ou plusieurs cas, sans nom inconnu ni
doublon. Le rapport décrit exactement ce sous-ensemble. `--skip-canonical`
produit uniquement un diagnostic brut : `diagnosticOk` peut être vrai, mais
`ok`/`qualificationComplete` restent faux et le code de sortie est 1.

Réévaluer des réponses et snapshots déjà capturés, sans requête HTTP :

```powershell
python -B -m tools.ingestion_gold.evaluate_responses `
  --response-dir artifacts/ma-conversion-neuve `
  --require-canonical `
  --output artifacts/ma-conversion-neuve/reevaluation.json
```

Le schéma de rapport v3 refuse un corpus vide, un cas manquant, la couche canonique
absente ou en échec, les sources différentes et un rendu non épinglé. La réussite
canonique et le résultat brut restent distincts : une réconciliation mécanique
peut restaurer une surface native que Docling a mal ordonnée. La commande de
réévaluation vérifie les hashes PDF capturés par la projection contre le manifeste ;
elle ne relance pas le rendu ni ne valide la configuration d'un service disparu.

Le mode SSH historique demeure disponible uniquement avec `--ssh-target`
explicite. Il n'est utilisé par aucune commande ci-dessus et ne fait pas partie
de la campagne locale actuelle. `layout_model_probe.py` est un outil historique
lié aux API des versions Docling qui exposent ses modèles ; ce n'est pas le
bootstrap du runtime actuel. Aucun endpoint distant implicite n'est contacté.

## Preuves et limites

Conserver sources, réponses brutes, snapshots canoniques, rapport et versions du
runtime dans un dossier de campagne neuf. Les preuves ne sont pas supprimées ni
écrasées automatiquement. La sélection publique doit être revue séparément.

Un verdict de production exige ensuite un parcours distinct : API d'enqueue,
vrai worker, OCR local réellement exécuté sur pages raster, publication atomique
du canonique et des chunks, récupération effective des points Qdrant, puis lecture
API sourcée. L'absence d'un moteur OCR doit produire un skip explicite ou un échec,
jamais un test réussi par retour anticipé. Corruptions, scans difficiles,
pages manuscrites et tableaux non représentés restent hors de la preuve obtenue
avec ce petit corpus. Ni un sous-ensemble ni une projection hors worker ne valide
toutes les formes de PDF.
