# Contrats OCR hérités après Unit v5

La campagne Unit v5 est terminée : 5 091 succès, un échec, aucun test ignoré. Côté backend, 2 570 cas sur 2 571 passent. L’unique échec est l’ancienne exigence de chaîne vide pour un fragment qui contient encore des durées lisibles de 10 et 8 minutes.

Le test regroupait de nombreuses entrées dans un seul `Fact`. Son premier échec masquait les assertions suivantes. Une revue de toutes les variantes a séparé les deux contrats :

- Les six scénarios de suppléments réellement corrompus qui ont passé avant l’assertion en échec restent dans un test de nettoyage : mélange de glyphes, mots cassés ou chiffres séparés. Leurs assertions de retrait ne sont pas affaiblies.
- Les six entrées de métadonnées incertaines sont placées dans une théorie distincte qui exige la conservation exacte de chaque entrée. Elles contiennent des durées lisibles de 10/8, 10/25, 10/30, 15 ou 40 minutes, parfois une instruction supplémentaire. Un marqueur abîmé ne démontre pas que toutes ces informations sont inutiles.

La nouvelle théorie ne prétend pas réparer un `I5` incertain en `15`, ni qualifier tout le texte de parfaitement extrait. Elle empêche sa suppression destructrice sans preuve. Les contrôles génériques de corruption OCR restent distincts.

Cette réconciliation modifie uniquement les tests. Aucun nouveau correctif produit, build, service ou appel API n’a été exécuté par l’auteur. La théorie doit maintenant être exécutée sur ses six cas, afin que toute éventuelle perte restante apparaisse séparément.
