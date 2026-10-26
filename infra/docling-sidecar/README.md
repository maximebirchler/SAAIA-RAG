# Sidecar Docling SAAIA

Le conteneur doit être lancé avec un init (`init: true` dans Compose, `--init`
avec Docker). Il récolte les descendants orphelins après l'arrêt d'un groupe OCR.
Le superviseur continue de refuser une clôture tant qu'un membre du groupe,
y compris un zombie, reste observable. Une qualification qui remplace l'entrée
normale par Python PID 1 sans init ne respecte pas cette exigence de lancement.
Cette configuration ne prouve pas l'annulation du moteur Docling amont.

La réparation régionale des tableaux partage maintenant le superviseur de
processus de la réparation de texte. Un seul créneau OCR est partagé entre les
deux opérations. Les tableaux utilisent RapidOCR seul, avec les poids locaux et
les sessions CPU effectivement contrôlés ; aucun Tesseract supplémentaire n'est
exécuté pour cette opération. Le trajet historique dans un thread non interruptible
a été supprimé après vérification de ses références.

Par document, les limites de réparation de tableaux sont : huit régions au plus,
60 secondes au total, 15 secondes par région et 20 millions de pixels pour la
page rendue. Les dimensions physiques sont contrôlées avant allocation. Les
variables `SAAIA_DOCLING_TABLE_REPAIR_MAX_REGIONS`, `TOTAL_TIMEOUT_SECONDS`,
`REGION_TIMEOUT_SECONDS` et `MAX_RENDERED_PIXELS` utilisent toutes ce même préfixe.
Les réglages existants `SCALE`, `PADDING_POINTS` et `MINIMUM_SCORE` sont conservés.
Les valeurs hors limites sont refusées au démarrage.

Une annulation tue et récolte le seul groupe de processus créé. Un résultat arrivé
après le budget n'est pas appliqué ; les cellules originales restent disponibles
avec une raison d'indisponibilité, d'échec ou de limite. `/health` expose séparément
les réglages et compteurs réels de réparation de tableaux. Ces sondes ne prouvent
pas l'annulation du processus Docling amont, ni la fidélité sémantique du résultat.
Si l'absence du groupe ne peut pas être confirmée, le créneau partagé est bloqué
pour les deux modes et `/health` devient indisponible. Un redémarrage du service
est requis ; une région suivante ne contourne jamais cette incertitude.

Ce service conserve Docling Serve comme parseur principal et ajoute trois
compléments génériques et traçables :

- la hiérarchie native Docling des titres, calculée à partir des signets, de la
  numérotation et du style typographique ;
- une réparation OCR régionale et bornée pour les tableaux dont la grille est
  mécaniquement incomplète.
- une seconde lecture ciblée des préfixes de texte manifestement fragmentés,
  activée explicitement et soumise à l'accord de RapidOCR et Tesseract.

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
   observation OCR admise dans l'enveloppe des centres observés. Une ligne très
   éloignée n'est pas rattachée automatiquement à la dernière cellule.
   La scission d'un texte absorbé exige sa composition littérale dans l'ordre
   des lignes : casse, signes et valeurs sont conservés ; seuls les blancs
   sont normalisés. La géométrie seule ne permet pas cette réécriture.
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

## Seconde lecture des textes fragmentés

Cette option est désactivée par défaut. Elle ne remplace pas le moteur principal
et ne tente pas de corriger les étiquettes courtes, les formules ou les blocs de
code. Un candidat doit commencer par un préfixe comportant au moins trois mots
minuscules isolés et une lacune typographique d'au moins trois espaces. La fin
du préfixe est bornée par une nouvelle lacune après ces fragments, ou par le
premier mot de plus de deux lettres. Un suffixe lexical suffisamment long doit
rester disponible. Les mots courts de ce suffixe sont protégés comme les autres.

Chaque candidat est relu sur le même crop, à échelle 4 et avec une marge de deux
points, par les petits modèles RapidOCR déjà présents et par Tesseract en anglais
avec `--psm 6`. Les trois chemins de modèles RapidOCR sont explicitement fournis ;
leur absence refuse la tentative avant initialisation. Les empreintes des poids
réellement présents et les versions des deux moteurs accompagnent la provenance.
Aucun modèle n'est téléchargé par ce chemin.
Les trois sessions ONNX réellement créées doivent confirmer le fournisseur CPU
et les limites de deux threads intra-opération et un thread inter-opération ;
une option ignorée refuse la tentative. `runtime_settings` conserve cette
observation dans la preuve RapidOCR. Le modèle Tesseract `eng.traineddata` est
résolu dans un répertoire local explicite ou parmi trois emplacements système
bornés. Il doit être unique ; ce même répertoire est imposé avec `--tessdata-dir`
à l'inventaire des langues et à la reconnaissance, puis le fichier est hashé.
`SAAIA_DOCLING_TEXT_REPAIR_TESSDATA_DIR` ou `TESSDATA_PREFIX` permet de choisir un
répertoire local absolu. Aucun texte de l'en-tête Tesseract n'est traité comme
une preuve du modèle réellement exécuté.

Une réparation exige la même chaîne après normalisation Unicode NFC et des
espaces uniquement. Ni casse, ni accents, ni décimales, ni ponctuation ne sont
assimilés. Chaque ligne RapidOCR doit atteindre 0,98 et chaque mot Tesseract 0,95.
Le suffixe fiable doit être intégralement identique, contigu et terminal ; aucune
insertion, même validée par les deux moteurs, n'y est admise. Les nombres,
opérateurs et identifiants explicites du préfixe restent aussi protégés. Un garde
générique préserve tous ses mots entièrement minuscules de deux lettres, sans
dictionnaire métier. Un garde
linguistique fini protège en plus des négations françaises, anglaises et
allemandes dans ce préfixe. Le `n` isolé d'une série de fragments n'est pas une
négation ; la contraction explicite `n'` ou `n’` reste protégée. Ce garde ne constitue pas une validation multilingue
universelle. L'accord de deux moteurs n'est pas une preuve de vérité sémantique.

Seul `item.text` est modifié. `item.orig`, les boîtes et l'ancien texte sont
conservés ; `saaia_text_repair` contient les deux observations avec leurs mots,
confiances et polygones. Une tentative incertaine conserve le texte principal et
ajoute `saaia_text_repair_attempt`. Les résumés comptables du document sont dans
`saaia_text_repairs` et `saaia_text_repair_summary`. Ces preuves de conversion
contiennent du texte documentaire et ne sont pas des journaux publics.

Le maximum est de quatre crops et 60 secondes de travail par document, avec un
sous-processus Linux par crop limité à 15 secondes, terminaison comprise. Un slot
sérialise ces processus et son attente consomme le même budget. Le rendu complet
de la page est refusé au-delà de 20 millions de pixels. Les processus forment un
groupe propre : annulation ou délai provoque son arrêt puis sa récolte avant le
nettoyage temporaire. Un arrêt non confirmé reste visible comme tel et n'est
jamais qualifié de succès. Les sondes `regionalTextRepairRuntime` ne prouvent pas
l'arrêt d'un calcul Docling Serve amont. Les tests purs simulent les processus ;
une conversion réelle Linux reste nécessaire pour qualifier moteurs et délais.
La disparition réelle de tous les descendants doit aussi être contrôlée lors
de cette qualification : le compteur de récolte observe les processus leaders.

Les variables `SAAIA_DOCLING_TEXT_REPAIR_` sont suivies de `ENABLED`, `MAX_REGIONS`,
`TOTAL_TIMEOUT_SECONDS`, `REGION_TIMEOUT_SECONDS`, `RENDER_SCALE`, `PADDING_POINTS`,
`MINIMUM_RAPID_SCORE`, `MINIMUM_SECONDARY_SCORE` et `MAX_RENDERED_PIXELS`.
Les valeurs moins strictes que les bornes ci-dessus sont refusées au démarrage.
`saaia_execution.text_repair_settings` atteste ces réglages effectifs ; il
n'affirme pas que des régions ont été relues avec succès.

## Observation des conversions

`GET /health` expose `conversionTransport` : nombre de transmissions HTTP
démarrées, actives et terminées, résultats techniques et durée de la dernière
transmission. Une attente HTTP annulée ne retire pas artificiellement le
thread de transmission du compteur actif. Ces sondes ne contiennent aucun
texte, nom ou identifiant documentaire. Elles observent le wrapper ;
`upstreamModelExecutionCancellationProven=false` signifie qu'elles ne prouvent
pas l'arrêt du calcul dans Docling Serve après une annulation du client.

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

Le profil de production initial reste CPU. L'ancienne mesure d'environ
1,05 seconde pour une seconde lecture du tableau de référence concernait le
chemin historique exécuté dans un thread. Elle ne mesure pas le nouveau
superviseur par région, qui recharge les moteurs dans un processus jetable.
Ses délais et performances doivent être qualifiés dans l'image Linux exacte.

## Validation locale

```powershell
$env:PYTHONPATH = (Resolve-Path 'infra/docling-sidecar').Path
python -m unittest discover -s 'infra/docling-sidecar/tests' -v
```

Le déploiement calcule un hash des seuls fichiers runtime du sidecar. L’image
n’est reconstruite et le conteneur n’est recréé que lorsque ce hash change.
