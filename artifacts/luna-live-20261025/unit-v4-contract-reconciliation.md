# Rapprochement des contrats après Unit v4

La suite `tests/unit-final-v4` a exécuté 2 534 cas : 2 530 succès et quatre échecs. Trois échecs concernent les fichiers de cet agent ; le quatrième relève du projecteur et est traité séparément. Les corrections décrites ici portent uniquement sur les attentes de tests. Aucun comportement produit supplémentaire n’est modifié.

## Référence ciblée et carte de profil

La nouvelle attente qui interdisait toute carte de profil pour `Classification: isolation level 4` était incorrecte : `SpacedCodeReferenceRegex` reconnaît déjà `level 4` et le chemin historique `LooksLikeTargetedReferenceCarrier` autorise les cartes contenant une référence ciblée. Le delta de qualité n’a pas créé cette exception.

Le test distingue désormais les deux données : la carte de durées/coût reste non admissible comme carte autonome ; la classification conserve son droit préexistant à une référence ciblée. Il vérifie explicitement la présence de cette référence, tout en exigeant dans les deux cas l’admission en contexte sur la base d’une extraction fiable.

## Métadonnées devant un titre composé

Le test historique supposait qu’une seule unité devait survivre à l’extraction. La suite montre deux unités : les durées/qualification, puis le corps. Le test exige maintenant la conservation exacte des durées avec une qualité `ok` et l’association à la section connue, ainsi que le corps. Les assertions d’absence des lignes de titre dans toutes les unités restent présentes : l’objectif du test sur les titres n’est pas supprimé.

## Ligne OCR avec marqueurs incertains

L’ancienne assertion classait comme bruit une ligne contenant encore des durées lisibles de 50 et 40 minutes, sur la seule présence de marqueurs courts. Son attente de suppression globale n’était pas une preuve que tout le contenu était corrompu. Elle attend désormais que la classification ne condamne pas l’ensemble de ces valeurs. Les autres assertions du même test sur de vrais mélanges de glyphes, lettres isolées et fragments endommagés restent inchangées.

Cette dernière assertion concerne la classification, pas la fidélité de tous les nettoyeurs de préfixe. Les nettoyeurs de métadonnées endommagées restent une limite signalée au coordinateur ; aucun succès de conservation intégrale n’est revendiqué sur cette seule assertion.

## Vérification restante

Les nouvelles attentes doivent être exécutées par le coordinateur. Aucune compilation, requête API, dépense fournisseur ou modification de service n’a été effectuée ici.
