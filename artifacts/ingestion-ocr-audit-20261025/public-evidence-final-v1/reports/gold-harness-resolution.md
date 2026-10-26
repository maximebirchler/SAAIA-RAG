# Résolution du harnais Gold — 26 octobre 2026

Le harnais ne confond plus la conversion d'un PDF et la validation de son
ingestion complète. Cette étape a exécuté uniquement Python, génération locale
et rendu de PDF : aucun build .NET, service, OCR, appel modèle ou réseau.

## Corrections réalisées

- Corpus vide, ID inconnu ou répété, cas absent et annotations malformées sont
  refusés. Une qualification demande exactement les cas sélectionnés, leur couche
  canonique réussie et un manifeste de rendu épinglé. `--skip-canonical` garde un
  résultat diagnostique, avec qualification fausse et code de sortie 1.
- Les faits critiques ont une page physique et un texte exact indépendant du
  générateur. NFC et espaces sont harmonisés ; signes, ponctuation, décimales,
  accents, unités, fractions et négations sont préservés. La présence dans un
  chunk chevauchant une autre page ne suffit pas si la page source attendue ne
  porte pas le fait. Source canonique et contexte de recherche sont contrôlés.
- Le projecteur prend les défauts réels d'`IngestionOptions`, actuellement
  200/25 mots, puis le vrai filtre `ShouldPublishRetrievalChunk`. Le manifeste v2
  exige ces défauts et les hashes des DLL backend/projecteur chargées. Plus de
  compilation ou restauration implicite dans le runner Python.
- Le rapport v3 annonce explicitement que worker, publication SQL et vecteurs
  ne sont pas validés par cette projection. La réponse brute reste visible même
  lorsqu'une réconciliation canonique rétablit une surface native.
- Police fournie explicitement, versions Python/ReportLab/Pillow et sources du
  générateur sont hashées. Aucun remplacement de police ni téléchargement.
  Sources/réponses/preuves sont conservées ; les dossiers existants ne sont pas
  écrasés. Le manifeste v1 historique n'est pas modifié.

## Corpus disponible

`config/ingestion-acceptance-gold.v2.json` contient 12 cas et 16 pages : les
8 cas historiques anglais de géométrie et 4 compléments français :

| Cas | Pages | Témoins principaux |
| --- | ---: | --- |
| fr-native-critical-v2 | 2 | métadonnées répétées, 1,5/15 bar, -5/+5 °C, négation, ½ litre, m² |
| fr-mixed-critical-v2 | 3 | natif/raster/natif, valeurs voisines différentes, AC/DC, OFF/ON |
| fr-table-critical-v2 | 2 | tableau continu, cellules fusionnées/vides, association module/valeur/page |
| fr-scan-critical-v2 | 1 | scan sans texte natif, durées, négation, décimale et deux signes |

Les 12 PDF sont sous `gold-corpus-v2-final/sources`. Les annotations restent dans
le manifeste, jamais dans la configuration du produit. Les images PNG de
génération/rendu et les PDF sont des pièces locales distinctes de toute sélection
publique future. La police Windows n'est pas copiée dans le dépôt.

## Vérifications réellement exécutées

`gold-corpus-v2-final/offline-validation.json` et les deux journaux Python
attestent **24 tests purs réussis**, sans skip. Des mutations de signes,
décimales et négations échouent dans les deux évaluateurs. Les contrôles vérifient
aussi le voisin de page, le manque en recherche, les hashes/source/DLL, les bornes
de production, les corpus incomplets et la dérive du renderer.

Les 12 sources ont été générées deux fois avec comparaison octet pour octet :
identiques. Le nombre de pages est contrôlé indépendamment avec pypdf. Les faits
français natifs sont présents sur leurs pages exactes ; les deux pages raster
françaises n'ont aucun texte natif et portent chacune une image intégrale. Cela
ne constitue pas un test OCR. Les huit pages françaises ont été rendues avec
pdftoppm puis inspectées : aucun texte coupé ni chevauchement constaté.

SHA-256 du manifeste à cette vérification :
`24cc69860eeec3804e903a1e190ee4292cbda389ec271aaa293bf12eff74ae35`.
La preuve de génération conserve les pins détaillés.

## Restant à valider

La modification C# est préparée et figée, mais sa compilation et ses tests sont
réservés à l'orchestrateur. Le correctif du test OCR opt-in qui retournait sans
exécution est pris en charge séparément par l'agent backend : cette note ne
prétend pas le valider.

Le runtime Docling local réel, son OCR et les modèles ne sont pas exercés par ces
24 tests. La prochaine preuve doit convertir au minimum scan et document mixte,
puis parcourir API d'enqueue → worker → canonique/SQL → points Qdrant → lecture
sourcée. Aucun résultat de cette phase ne vaut approbation générale des PDF,
des scans difficiles ou de la publication vectorielle.
