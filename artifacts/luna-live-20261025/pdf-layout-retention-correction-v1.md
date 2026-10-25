# Extraction PDF : conservation du contenu et lecture des colonnes

Les dix tests ciblés du correctif passent. Quatre défauts ont d’abord été reproduits en échec avant correction. Cette validation porte sur la reconstruction du texte natif ; elle ne valide pas encore la nouvelle projection du PDF réel jusqu’aux passages publiés, ni une réponse du modèle.

## Causes établies

Le diagnostic privé `native-word-geometry-diagnostic-v1.private.json` conserve les mots et coordonnées réellement extraits de la page physique 5. Sa projection publique minimale est `native-layout-retention-review-v1.json`.

| Surface de la page 5, avant correction | Observation |
|---|---|
| Texte brut du PDF | 1 555 caractères hors espaces. |
| Mots du segmenter par défaut | 507 mots non blancs, dont 221 mots d’une seule lettre ; même multiensemble exact de caractères que le texte brut. |
| Mots du segmenter nearest-neighbour existant | 331 mots non blancs, dont 15 mots d’une seule lettre ; même multiensemble exact de caractères que le texte brut. |
| Blocs XY-cut existants | Même multiensemble des 331 mots nearest-neighbour ; ordre des mots du bloc incliné dégradé. |
| Ancien texte de page publié | 1 337 caractères hors espaces ; paragraphe d’astuce absent et deux colonnes entremêlées. |

La couverture des caractères et des mots a été calculée en lecture seule sur les JSON figés. Elle n’est pas une mesure de justesse sémantique ou d’ordre de lecture.

Les causes suivantes ont été isolées :

1. **Fusion de colonnes par l’enveloppe d’un groupe.** Le regroupement acceptait un segment qui chevauchait l’enveloppe horizontale déjà occupée par un groupe. Un titre large pouvait ainsi relier deux colonnes. Le repli ligne par ligne associait ensuite un ingrédient à la fin d’une instruction voisine. Le cas synthétique avec titre couvrant deux colonnes reproduisait cet entrelacement.
2. **Suppression des régions minoritaires.** Le lecteur de colonnes ne rendait que les groupes d’au moins trois segments, tout en acceptant seulement 60 % de couverture. Le lecteur de préfixe imbriqué ne rendait que les groupes d’au moins deux segments avec 75 % de couverture. Des mots sources pouvaient donc disparaître malgré une reconstruction jugée suffisante. Deux témoins distincts reproduisaient les pertes.
3. **Acceptation d’une géométrie partielle.** Les anciens contrôles de quantité de mots et de longueur pouvaient accepter une reconstruction privée d’un fait présent dans le texte brut. Un témoin avec géométrie manquante reproduisait cette omission.
4. **Segmentation défectueuse du texte incliné.** Dans le PDF réel, le segmenter par défaut transformait une partie du paragraphe incliné en lettres isolées. Le nettoyage des suites de lettres supprimait ensuite ces fragments. Le segmenter nearest-neighbour déjà disponible reconstitue des mots entiers sans changer le multiensemble des caractères. Les blocs XY-cut n’ont pas été choisis comme remplacement global : leur ordre de lecture du même paragraphe reste dégradé.

Les trois premières causes sont couvertes par les quatre tests RED. La quatrième repose sur les deux surfaces de géométrie du PDF réel ; les gardes de sélection du segmenter ont ensuite reçu des tests spécifiques.

## Correctif appliqué

Le delta produit est limité à `backend/SAAIA.Backend/Pdf/PdfExtractor.cs`.

- La segmentation nearest-neighbour est proposée comme entrée du lecteur existant lorsque ses mots comportent moins de lettres isolées. Son admission exige une géométrie complète et l’égalité exacte des multiensembles de caractères avec les mots d’origine **et** le texte brut. La casse, les chiffres et la ponctuation sont conservés ; les espaces peuvent varier.
- Si les deux segmenters omettent le même fait brut, leur accord ne suffit pas : le texte brut complet reste le repli.
- La géométrie incomplète déclenche le repli sur le texte brut. La reconstruction doit couvrir chaque caractère brut. Si les objets mots exposent davantage de contenu que l’agrégation brute, leurs mots sources ne sont pas supprimés pour cette seule différence.
- Chaque rendu reconstruit doit conserver exactement le multiensemble des mots sélectionnés, répétitions comprises. Un rendu qui omet, ajoute ou répète un mot est refusé au profit du texte brut.
- Le regroupement des colonnes utilise l’ancrage gauche déjà employé par le lecteur imbriqué. Le helper à enveloppe devenu sans appel a été supprimé.
- Les seuils de stabilité servent à décider si une lecture en colonnes est suffisamment étayée. Tous les groupes, y compris les petites régions, participent au rendu.

Aucune règle de recette ou de quantité attendue n’a été ajoutée. Les blocs XY-cut, le filtre OCR et les contrats publics n’ont pas été modifiés par ce delta. `NativeLayoutAlgorithm` continue de décrire la surface diagnostique des blocs natifs ; il ne désigne pas l’algorithme de `Page.Text`.

## Vérification exécutée

Les exécutions .NET ont été lancées par l’opérateur parent. Cette revue a relu les résultats et les empreintes ; elle n’a lancé aucun build, service, requête API ou modèle.

**RED initial : 4 exécutés, 4 échecs.** Les quatre témoins couvrent la région isolée, le préfixe minoritaire, le titre reliant les colonnes et la géométrie incomplète.

- TRX : `tests/layout-retention-red/layout-retention-red.trx`
- SHA-256 : `49DD9776DB432D8FBE98927FFA8C1796F0E13C4387DBE8CCBA166BA99BDD08EE`
- Ce dossier ne comporte pas de manifeste de sources : le TRX prouve les échecs comportementaux, sans prétendre fournir seul un ancrage exhaustif de compilation.

**GREEN du correctif : les 10 cas `PdfExtractorLayoutRetentionTests` passent.** Ils couvrent les quatre témoins précédents, le réassemblage d’une instruction négative, les refus de changements de casse/ajout/suppression de caractères, la préservation des chiffres, la géométrie incomplète d’un autre segmenter et l’omission commune d’un fait brut.

- Campagne : `tests/quality-context-red/manifest.json`, run `11fc364b72014f77874838d11c228c8b`.
- TRX : `tests/quality-context-red/Backend-11fc364b72014f77874838d11c228c8b.trx`.
- SHA-256 du TRX relu : `0C01256C5AD4F6A0B57D19317EDF2E77BAEA48F60FFAF5E1EE2858C1B1E29F4E`.
- Compilation demandée, empreinte des sources stable pendant l’exécution : `68DAEC2D9794C6CA20730C6E2E726194242CB76041A9129A2F9AF8C9B798C5B0`.
- DLL backend chargée : `997744B01E8D05D74ADEC48F234EF333192885FEAAF9D6C11F55EB1673EF9611`.

La campagne combinée contient **16 tests, 12 PASS et 4 FAIL**. Les quatre échecs concernent le RED séparé `ExtractionQualityPolicyContextTests`. Ils ne sont ni masqués ni comptés comme une réussite globale. Les dix cas layout sont tous PASS ; les deux autres PASS sont des contre-contrôles de qualité.

Sources du correctif retrouvées dans l’inventaire de cette compilation :

| Fichier | SHA-256 |
|---|---|
| `PdfExtractor.cs` | `F79D18603982F2CAC12B827DD6F0C2FA813ACBC6FB3105A7EE81EFD04E734659` |
| `PdfExtractorLayoutRetentionTests.cs` | `A6440C7235F6E80ACE42E71B9BB02030EF2BD70061B23F31F70930C3CF52697C` |

## Limites et prochain témoin gratuit

La conservation d’un multiensemble prouve la couverture, pas l’ordre correct des phrases. Le correctif empêche les pertes reproduites et refuse une segmentation non complète ; il ne constitue pas une validation universelle des PDF à colonnes, inclinés, tabulaires ou scannés.

Le helper `NativePaidCorpusProjection` est correctement borné : source et DLL attendues sont hashées, extraction entière avant sélection de pages physiques, sortie neuve, surfaces alternatives diagnostiques, absence de modèle/réseau/serveur/OCR. Il faut conserver ces bornes lors de la prochaine projection.

Pour le prochain diagnostic, les ajouts utiles au helper seraient des observations privées calculées avec les méthodes de production, sans modifier le parcours de données :

1. Identifier le découpage de mots effectivement sélectionné, en distinguant `nearest_verified`, `default` et une segmentation équivalente. Ne pas réutiliser `NativeLayoutAlgorithm` pour ce champ.
2. Conserver séparément le rendu immédiatement produit par `BuildLayoutAwareText` et le texte final de page après nettoyage. Comparer les multiensembles de mots à chaque frontière afin de localiser toute nouvelle disparition.
3. Montrer le passage du texte de page aux unités puis aux chunks, avec les pages/sections/ordinaux exacts. Les chunks peuvent se recouvrir : une comparaison de couverture doit employer l’union des références sources, sans additionner naïvement leurs occurrences.
4. Vérifier des séquences et des frontières, pas seulement la présence de deux expressions : quantité séparée de l’instruction voisine, nombre de personnes associé au bon sujet, durées/qualifications conservées, paragraphe incliné encore lisible et accessible. Conserver les extraits seulement dans la preuve privée ; publier des comptes et résultats de contrôles.

Les fusions légitimes de mots coupés, les titres stockés comme sections et les compteurs de page explicitement reconnus doivent être déclarés séparément : ils empêchent d’interpréter une égalité brute de tokens comme unique preuve de couverture entre tous les étages.

À la date de ce rapport, la nouvelle projection réelle du backend corrigé et les suites de non-régression plus larges restent à exécuter ou à relire. Aucune nouvelle réponse payante ni le planning de repas ne sont approuvés par ce rapport. Le statut produit demeure `TESTE_NON_APPROUVE`.
