# Préfixe de métadonnées : perte confirmée et correction

Statut : correctif préparé après RED causal, sources gelées ; validation GREEN réservée au coordinateur.

La campagne `tests/native-layout-ocr-red-v2` contient cinq cas : trois échecs et deux succès, dont des témoins de disposition traités séparément. Pour le filtre OCR, le planning lisible sans marqueur passe. La même exigence de conservation échoue avec les marqueurs incertains du cas historique : le nettoyage transforme une ligne commençant par une préparation de 50 minutes en un corps commençant par une durée de 40 minutes. La première durée est donc réellement perdue ; l’inversion de la seule attente de classification aurait masqué ce défaut.

`LeadingNoisyMetadataSupplementRegex` choisissait la frontière entre préfixe et corps à partir de labels et de marqueurs comme `QI`. `RemoveLeadingNoisyMetadataSupplement` retournait seulement le corps, voire une chaîne vide si ce corps était trop court. La présence d’un label et d’un marqueur ne prouvait pas l’inutilité du préfixe.

Le correctif retire cet appel, le helper et son expression régulière, sans remplacement par une liste de mots, de recettes ou d’unités. Une recherche des deux symboles dans les sources C# ne trouve plus de référence. `NoisyMetadataLeadMarkerRegex` reste utilisé par d’autres diagnostics de dommage ; il n’est pas supprimé comme code mort.

Le témoin positif exige classification non destructive et conservation exacte après nettoyage pour les deux entrées. Les règles distinctes de corruption de glyphes, de lettres isolées et de fragments restent en place. Aucun succès de nettoyage arbitraire ni de fidélité complète du PDF n’est déduit de ce correctif ciblé.

Aucun service, build ou appel API n’a été exécuté par l’auteur ; le coordinateur doit lancer le GREEN puis vérifier le corpus natif.
