# Qualité d’extraction et admission des métadonnées en contexte

Statut : correctif préparé après un RED causal, sources gelées ; vérification GREEN à effectuer par le coordinateur.

## Preuve initiale

La campagne `tests/quality-context-red/manifest.json` a exécuté seize cas sur des sources inchangées durant l’essai : quatre échecs et douze succès. Les quatre échecs concernent l’admission puis la publication de deux métadonnées fiables : une carte de durées/coût et une classification de niveau. Les deux contre-contrôles de qualité passent ; les dix autres succès couvrent le correctif de disposition PDF distinct.

Les six tests de `ExtractionQualityPolicyContextTests.cs` construisent une page puis utilisent réellement `DocumentUnitExtractor`. Aucun état de qualité n’est forcé dans les unités. Les métadonnées des pages riches ont `ExtractionTextStatus=ok`, `ExtractionTextSparse=false`, `ExtractionOcrCandidate=false` et le signal `text_extraction_ok`. Malgré ces preuves, elles étaient exclues des fenêtres de recherche avant le correctif.

## Cause

`ShouldUseUnitForRetrievalWindow` appelait directement la politique de restriction des candidats autonomes. Cette dernière mélangeait les signaux de texte peu fiable avec des règles de forme sur les métadonnées de classification ou de planning. Une restriction acceptable pour une carte isolée supprimait ainsi des informations valides qui qualifient le corps voisin.

## Correction minimale

Les gardes existantes sur le texte non fiable sont regroupées dans `RequiresExtractionQualityRestriction`. Elles restent utilisées par les deux chemins.

L’admission dans une fenêtre de recherche utilise uniquement ces gardes de fiabilité. Les règles de carte de planning ou de classification restent appliquées aux candidats autonomes, aux profils et aux références ciblées. Le correctif n’invente aucune information, ne change pas le texte source et ne modifie pas les règles de contexte du projecteur.

Les tests exigent simultanément que les valeurs fiables atteignent un chunk publié avec leur corps substantiel, puis le contexte documentaire, et qu’elles ne deviennent pas seules une carte publiée. Une page physiquement peu fournie et une page contenant un caractère de remplacement restant restent refusées dans les contre-contrôles.

## Limites

Le RED confirme la causalité de cette barrière ; le GREEN et le nouveau corpus natif doivent confirmer le comportement corrigé. Ces tests de politique partent de pages textuelles contrôlées : ils ne prouvent pas à eux seuls l’extraction fidèle d’un PDF réel ou l’ingestion complète, les embeddings ou la réponse du fournisseur.

Aucun build, service, appel API ou modèle n’a été lancé par l’auteur du correctif. La compilation et l’exécution des tests sont réservées au coordinateur.
