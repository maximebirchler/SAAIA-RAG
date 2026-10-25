# Limite d’encodage de la fixture PDF générée

La suite Unit v3 a passé 2 494 tests et échoué sur un seul des 2 495 : la fixture PDF générée attendait la chaîne `Reference capacity ½ litre`. Le test pur de conservation du même texte Unicode passait.

Un contrôle ajouté avant toute extraction de production a ouvert les octets générés directement avec PdfPig et exigé la présence du glyphe `½` dans `page.Text`. Ce contrôle a lui-même échoué. La fixture Standard14 n’établissait donc pas son propre encodage Unicode ; cet échec ne démontre pas une nouvelle perte causée par le nettoyage de production.

La fixture portable utilise désormais la fraction ASCII `1/2`, vérifiée dans le texte PdfPig brut avant `PdfExtractor.Extract`. Le nom du test précise sa couverture ASCII. La conservation d’une fraction Unicode reste testée séparément sur le chemin textuel pur ; **aucune couverture de glyphe Unicode dans un vrai PDF généré n’est revendiquée**. Aucun correctif produit, police système ou dépendance supplémentaire n’a été ajouté pour contourner cette limite de fixture.

Le helper Gold ajoute aussi au snapshot privé `RawText`, `NativeLayoutText`, `NativeLayoutBlocks` et `NativeLayoutAlgorithm`. Ces surfaces servent uniquement au diagnostic. Le texte `page.Text` retenu, le seed et le contenu envoyé au modèle ne changent pas par cet ajout. La compilation du helper et le nouveau préflight restent coordonnés par l’opérateur.
