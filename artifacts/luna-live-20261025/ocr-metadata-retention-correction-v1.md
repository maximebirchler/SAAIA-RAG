# Conservation des valeurs opérationnelles dans le filtre OCR

Statut : correctif préparé et sources gelées. La compilation et les essais GREEN sont exécutés par le coordinateur ; aucun résultat GREEN n’est revendiqué ici.

Le checkpoint réel `tests/retention-green-checkpoint` a validé 38 cas sur 40. Les deux échecs restants étaient `Cycle 2 h 45 minutes` et `Sampling 15 min 25 min` : les unités étaient encore effacées après le retrait des filtres dangereux de `DocumentUnitExtractor`. Le troisième témoin `Duration 4 h` passait déjà.

## Cause

`OcrNoiseFilter.LooksLikeShortStandaloneLayoutMetadataOnlyLine` considérait une ligne courte comme du bruit dès qu’elle réunissait plusieurs durées et peu de mots longs. Aucune corruption du texte n’était exigée. La même classification retirait un suffixe après une phrase complète, avec des marqueurs décoratifs. Des branches voisines considéraient aussi des labels, acronymes et unités comme une preuve suffisante de bruit.

Cette logique pouvait effacer une donnée recherchée au lieu d’éliminer un artefact OCR. La répétition ou la forme d’une métadonnée n’établit pas son inutilité.

## Modification

Les quatre classifications purement sémantiques de ligne courte ou de métadonnées sont retirées : `LooksLikeShortMetadataResidue`, `LooksLikeNoisyMetadataOnlyLine`, `LooksLikeShortMetadataScheduleOnlyLine`, `LooksLikeShortStandaloneLayoutMetadataOnlyLine`. Le retrait de suffixe décoratif associé est supprimé. Les helpers et expressions régulières utilisés uniquement par ces branches sont supprimés.

Le filtre conserve ses contrôles de texte réellement endommagé : lettres isolées, symboles, séquences incohérentes et fragments. La règle des lettres isolées reste volontairement inchangée ; le défaut qui produisait artificiellement de telles lettres dans le PDF relève du découpage de mots, traité séparément.

## Vérification préparée

Le nouveau fichier `OcrNoiseFilterRetentionTests.cs` contient six valeurs génériques (durées, pression, qualification, marqueurs de planning) qui doivent être acceptées par les deux classifications publiques et conservées exactement par le nettoyage. Deux autres cas conservent un suffixe de planning après une phrase complète courte ou longue.

Trois attentes historiques sont corrigées explicitement : une ligne de durées, un acronyme suivi d’une qualification, et un suffixe de planning n’étaient pas des preuves de corruption suffisantes pour autoriser leur suppression. Les assertions de scans réellement corrompus restent en place.

Les deux échecs préexistants constituent le RED causal. Les huit nouveaux contrats directs sont des contrôles supplémentaires, dont l’exécution reste à faire par le coordinateur. Aucun appel API, modèle, service ou compilation n’a été effectué par l’auteur de cette modification.

## Frontière connue

`ExtractionQualityPolicy.LooksLikeShortStandaloneMetadataScheduleCard` classe indépendamment des cartes de labels/durées comme `low_content`. Cette barrière a été signalée au coordinateur et à l’agent du projecteur ; elle ne doit pas être confondue avec une nouvelle perte dans l’extracteur OCR. La conservation doit être vérifiée séparément dans le texte physique, les unités, les chunks et les contextes publiés.

La politique peut conserver davantage de texte accessoire. Ce compromis est explicite : un marqueur ou une métadonnée incertaine est conservé plutôt que supprimé sans preuve qu’il est inutile.
