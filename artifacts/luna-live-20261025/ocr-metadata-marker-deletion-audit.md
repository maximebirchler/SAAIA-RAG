# Audit des décisions fondées sur un marqueur de métadonnées

État de cette lecture : après suppression du découpage de préfixe, avant capture des deux RED de suffixe. Aucun nouveau correctif produit n’est appliqué dans cet audit.

## Suppressions directes dans RemoveTrailingNoisySupplement

La variable `suffixStartsWithNoisyMetadata` n’atteste pas une corruption : elle vient de `NoisyMetadataLeadMarkerRegex`, qui reconnaît un marqueur court suivi d’un label de temps/préparation/durée.

Trois branches peuvent alors supprimer du contenu sans diagnostic de corruption du suffixe :

1. Si le préfixe ne se termine pas comme une phrase complète et paraît technique/structuré, retourner le seul préfixe. Les valeurs lisibles du suffixe disparaissent.
2. Si le préfixe ne se termine pas comme une phrase complète, retourner une chaîne vide sur le seul marqueur. Le préfixe et le suffixe disparaissent.
3. Après un contenu complet, retourner le seul préfixe sur le seul marqueur. Les deux nouveaux témoins positifs ciblent ce cas avec des durées et un avertissement.

Le même marqueur dispense aussi des deux seuils de longueur usuels : suffixe dans les 32 derniers caractères et suffixe inférieur à 32 caractères. Ces deux conditions ne suppriment rien seules mais permettent aux branches précédentes de s’appliquer à des suffixes très courts.

Les autres motifs de cette méthode sont distincts : `LooksLikeProbableNoiseText`, `LooksLikeDamagedLayoutSupplement` et `LooksLikeMetadataLayoutDamagedSupplement`. Leur présence ne transforme pas le marqueur en preuve ; les branches disjonctives doivent être évaluées séparément.

## Vetos indirects de conservation

Le même `NoisyMetadataLeadMarkerRegex` empêche actuellement neuf contrôles de reconnaître du contenu utile :

- `LooksLikePreservableLongProseText`
- `LooksLikePreservableTechnicalReferenceText`
- `LooksLikePreservableTechnicalTopicListText`
- `LooksLikePreservableEntityRosterOrContactListText`
- `LooksLikePreservableTechnicalDecisionMatrixText`
- `LooksLikePreservableTechnicalFigureOrChartText`
- `LooksLikePreservableStandardsGovernanceText`
- `LooksLikeTechnicalFigureLabelFragment`
- `LooksLikeUsefulProseWithStructuredQuantityContinuation`

Ces vetos ne suppriment pas directement le texte. Ils enlèvent une protection avant des heuristiques de bruit ou de fragment. Ils peuvent donc contribuer à une perte, mais cette lecture ne les présente pas comme neuf pertes exécutées.

Les deux derniers contrôles appliquent aussi un veto sur `TrailingSupplementStartRegex`, qui reconnaît un label sémantique de temps/durée/préparation. Là encore, le label ne prouve pas une corruption.

## Autres opérations du chemin de nettoyage

`RemoveLeadingShortLayoutPrefixBeforeStructuredLabels` et `RemoveInlineIndexReferenceTail` restent appelés avant la recherche de suffixe. Ils ont leurs propres critères et ne sont pas justifiés par `suffixStartsWithNoisyMetadata` ; ils ne sont donc pas comptés parmi les trois branches ci-dessus. Le découpage `RemoveLeadingNoisyMetadataSupplement` a déjà été retiré après un RED séparé montrant la perte de la préparation de 50 minutes.

## Direction de correction proposée

Le marqueur ou le label peut aider à repérer une zone, mais ne devrait ni autoriser seul sa suppression, ni invalider seul une preuve positive de contenu utile. Les signaux de corruption et les règles de disposition doivent rester séparés. Les deux RED demandent une conservation exacte, sans liste de domaines, de recettes ou d’unités et sans prétendre qu’un texte véritablement corrompu est devenu fiable.

Les tests, leurs résultats et les vérifications du PDF réel restent distincts de cet inventaire statique. Aucun build, API, modèle ou service n’est exécuté ici.
