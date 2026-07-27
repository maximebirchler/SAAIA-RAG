# Sidecar Docling SAAIA

Ce service conserve Docling Serve comme parseur principal et ajoute deux
compléments génériques et traçables :

- la hiérarchie native Docling des titres, calculée à partir des signets, de la
  numérotation et du style typographique ;
- une réparation OCR régionale et bornée pour les tableaux dont la grille est
  mécaniquement incomplète.

## Chemin d’exécution

1. Le wrapper FastAPI reçoit la requête compatible avec
   `POST /v1/convert/file`.
2. Le PDF est copié dans un répertoire temporaire avec une limite de taille.
3. La requête est transmise à Docling Serve, isolé sur le port interne `5002`.
4. L’option officielle `HeadingHierarchyOptions` restaure les niveaux de titres
   à partir des signets, de la numérotation et des cellules PDF stylées.
5. Les tableaux complets sont renvoyés sans seconde lecture.
6. Une grille clairsemée et non chevauchée déclenche l’OCR de la seule région
   du tableau avec RapidOCR déjà inclus dans l’image épinglée.
7. La réparation n’est acceptée que si chaque coordonnée manquante possède une
   observation OCR et si le texte absorbé par une cellule voisine peut être
   séparé par sa géométrie ou sa composition textuelle exacte.
8. En cas d’ambiguïté ou d’erreur, le résultat Docling original est conservé.

Ce composant ne choisit ni la pertinence d’une source, ni une recette, ni la
réponse à produire. Ces décisions sémantiques restent la responsabilité du LLM
client. Le sidecar ne traite que des faits géométriques et des observations OCR.

## Provenance

Une réparation acceptée conserve notamment :

- le texte Docling original et le texte corrigé ;
- les boîtes OCR sources et leur confiance ;
- le moteur, sa version et les modèles ;
- les cellules ajoutées ou scindées ;
- la durée et le motif de déclenchement ;
- un stage `regional_table_ocr_repair` dans le manifeste canonique.

Le backend propage ensuite
`canonical_table_regional_ocr_repaired` uniquement aux chunks du tableau
concerné.

## Configuration

Les variables principales sont :

- `SAAIA_DOCLING_TABLE_REPAIR_ENABLED` ;
- `SAAIA_DOCLING_TABLE_REPAIR_SCALE` ;
- `SAAIA_DOCLING_TABLE_REPAIR_PADDING_POINTS` ;
- `SAAIA_DOCLING_TABLE_REPAIR_MINIMUM_SCORE`.
- `SAAIA_DOCLING_HEADING_HIERARCHY_ENABLED` ;
- `SAAIA_DOCLING_HEADING_HIERARCHY_USE_BOOKMARKS` ;
- `SAAIA_DOCLING_HEADING_HIERARCHY_USE_NUMBERING` ;
- `SAAIA_DOCLING_HEADING_HIERARCHY_USE_STYLE` ;
- `SAAIA_DOCLING_HEADING_HIERARCHY_MAX_LEVEL` ;
- `SAAIA_DOCLING_HEADING_HIERARCHY_BOOKMARK_THRESHOLD`.

Docling 2.113 contient cette hiérarchie mais Docling Serve 1.27 ne l’expose pas
encore dans son contrat HTTP. Le lanceur SAAIA active donc explicitement
l’option native au moment où Docling Serve construit ses options de pipeline.
Il ne réécrit aucun titre et ne contient aucun vocabulaire métier.

Le profil de production initial reste CPU. La seconde lecture du tableau de
référence prend environ 1,05 seconde sur le serveur, dont 0,68 à 0,73 seconde
pour RapidOCR, sans décharger Qwen3 de la T2000.

## Validation locale

```powershell
$env:PYTHONPATH = (Resolve-Path 'infra/docling-sidecar').Path
python -m unittest discover -s 'infra/docling-sidecar/tests' -v
```

Le déploiement calcule un hash des seuls fichiers runtime du sidecar. L’image
n’est reconstruite et le conteneur n’est recréé que lorsque ce hash change.
