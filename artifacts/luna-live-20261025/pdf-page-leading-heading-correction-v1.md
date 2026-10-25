# Titres de source en tête de page physique

Le détecteur reconnaît désormais un titre source en tête de page lorsque sa forme et le corps structuré qui suit le confirment, même si les métadonnées sont rendues après lui. Les onze nouveaux cas passent, ainsi que les vingt et un tests existants du détecteur. La projection réelle du backend comprenant cette correction reste à rapprocher de sa preuve propre.

## Cause constatée

Le lecteur de mise en page corrigé restitue les titres des pages avant leurs corps ; les métadonnées d’une colonne latérale peuvent être rendues en fin de page. `TryClassifyContextualMixedCaseHeading` exigeait pourtant deux lignes de métadonnées **avant** le candidat dans les cinq lignes précédentes.

Un titre source avec une seule initiale capitale, ou un titre d’un mot, ne franchissait pas la voie classique fondée sur les majuscules. La voie contextuelle le refusait ensuite faute de métadonnées précédentes, même si le corps structuré était immédiatement présent. Dans la projection réelle intermédiaire, les titres des sujets des pages 5 et 6 étaient conservés dans le texte, mais ne devenaient pas des sections. Une section du sommaire page 2 continuait donc d’englober des sujets ultérieurs : la conservation verbatim des mots ne suffisait pas à rendre correctes les métadonnées de section.

Le défaut concerne l’ordre de lecture et le rattachement documentaire. Il ne constitue pas une incapacité de raisonnement du modèle et ne nécessite aucun appel fournisseur pour être reproduit.

## Correction générique bornée

Le delta produit est limité à `DocumentSectionExtractor.TryClassifyContextualMixedCaseHeading` :

- Le garde de navigation est appliqué explicitement à cette voie, comme à la classification classique.
- La première ligne utile de la page peut être reconnue sans deux métadonnées antérieures.
- La forme de candidat existante et la confirmation par le corps structuré existant restent obligatoires.
- Les autres positions de la page conservent la condition antérieure sur les métadonnées.

Les exclusions de phrases, compteurs, valeurs de métadonnées et bruit demeurent. Aucun nom de recette, catégorie métier ou titre attendu n’est codé. Le texte source n’est pas réécrit ; les niveaux et bornes sont produits par le mécanisme de sections existant.

Le début de page désigne ici la première ligne non vide du texte sélectionné. Cette correction ne prétend pas résoudre tous les titres précédés d’éléments parasites ou toutes les mises en page arbitraires. La revue indépendante de l’agent client n’a identifié aucun défaut matériel supplémentaire dans ce delta.

## Preuve causale exécutée

Les tests ont été préparés avant modification produit dans `DocumentSectionExtractorPageLeadingHeadingTests.cs`. L’opérateur parent a exécuté les campagnes ; cette revue a relu les manifestes, les compteurs et les empreintes des TRX.

| Campagne | Résultat du périmètre Section |
|---|---|
| `tests/page-heading-red` | 11 exécutés : 3 FAIL positifs attendus, 8 PASS négatifs. |
| `tests/format-cues-red-context-heading-green` | 11 nouveaux PASS et 21 existants PASS, soit 32 Section PASS. |

Les trois positifs couvrent un titre mélangé, un titre d’un mot et la séparation entre pages avec rattachement des unités à leur propre sujet. Les huit négatifs couvrent une phrase, un impératif, des compteurs/valeurs, une navigation, l’absence de corps et un candidat intérieur à la page.

Le second run combiné contient **41 cas : 37 PASS et 4 FAIL**. Les quatre échecs sont le RED distinct de conservation des marqueurs de format ; les cinq autres PASS sont des contrôles de contexte physique. La campagne globale n’est donc pas déclarée entièrement réussie.

### RED

- Run : `ad7bca8ab780493d9a3fe611971129d6`.
- Sources stables pendant compilation/exécution : `DF9B2DCC7B586354318CB5D0316B5EFCF63193D6D0ABFC6801FEFDD50AF7147B`.
- TRX : `Backend-ad7bca8ab780493d9a3fe611971129d6.trx`.
- SHA-256 du TRX relu : `C944AACF01E7C042B9C121F9A0DDE92B43D1EBD9B182348F984EC2E5D5634151`.

### GREEN Section et non-régression existante

- Run : `f17800c97c8c4907b3e5ff4fcceef582`.
- Sources stables pendant compilation/exécution : `D744F0032D6E8D411833E82CC3AB211162723A0956B5467AD017F0E5AC6EFAAC`.
- DLL backend chargée : `9C548943D0F606062378EDAC67C398561D5AFD1E7FEEA976DFC995BE6FDC5277`.
- TRX : `Backend-f17800c97c8c4907b3e5ff4fcceef582.trx`.
- SHA-256 du TRX relu : `2AD9164B3101FE3157F442A4B56258675F1EB5F51251719152A76CC4D4B241D1`.
- Source `DocumentSectionExtractor.cs` retrouvée dans l’inventaire compilé : `FD9B6C16737163C2BB77D85BBEAED00FBA615F2E51470738F869D81E930E4ED9`.
- Tests nouveaux retrouvés dans le même inventaire : `F60C7E7B4091B88FADA819A0B8D1BF261929C441474B396141107A40B14269EF`.

## Portée et suite

Le contrôle suivant doit relire la projection native réelle : titres exacts, bornes des pages et association des unités/chunks au bon sujet. Il doit être distingué des 32 PASS Section et de la validation précédente de l’ordre du texte.

Aucun build, service, requête API ou modèle n’a été lancé pour rédiger cette revue. Ce correctif et ces tests ne valident pas seuls un parcours WinUI, une réponse payante ou le planning de repas. Le statut produit reste `TESTE_NON_APPROUVE` tant que les autres preuves d’acceptation ne sont pas réunies.
