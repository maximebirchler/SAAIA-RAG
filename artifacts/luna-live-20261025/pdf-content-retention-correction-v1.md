# Conservation des faits numériques lors de l’extraction PDF

État : RED causal capturé par l’opérateur, correctif préparé et gelé ; validation GREEN et nouveau préflight natif encore attendus. Aucun appel fournisseur, build ou service exécuté par l’auteur de ce correctif.

## Cause établie

Le PDF source comporte 36 pages. L’ancien nettoyage supprimait les petites lignes présentes sur au moins 35 % des pages, avec un minimum de trois occurrences : ici le seuil vaut treize. La ligne du nombre de convives apparaît quinze fois ; son motif obtenu en remplaçant les chiffres par `#` apparaît sur vingt-neuf pages. La fréquence était donc traitée comme une preuve de texte inutile, sans distinguer une mesure ou un paramètre opérationnel d’un en-tête.

La quantité de vin n’était pas perdue à ce même stade : elle existe dans le texte natif de la page 5, mais est fusionnée avec une phrase de l’autre colonne. La disparition dans les chunks relève d’un filtre de projection distinct, traité séparément. Le mélange des colonnes de cette page reste une limitation connue ; le présent correctif ne prétend pas l’avoir résolu.

## RED et règle corrigée

Deux nouveaux tests génériques ont échoué sur le code initial, tandis que les treize anciens tests ciblés passaient. Le premier utilise quatre pages textuelles ; le second construit de vrais octets PDF puis appelle l’extraction de production. Ils exigent la conservation d’une pression identique, de pressions variables et d’une tolérance, tout en vérifiant que les compteurs de pages sont retirés. Aucun vocabulaire ni réponse du cas de recette n’entre dans les règles produit.

Le correctif exclut les lignes chiffrées des suppressions fondées uniquement sur la répétition. Un motif numérique variable ne suffit plus : la suppression exige une pagination explicitement étiquetée, répétée, correspondant au numéro physique courant et placée au bord du texte. Les en-têtes répétés sans chiffres et les compteurs de pagination déjà reconnus restent traités.

Une autre suppression non justifiée a été retirée : tout nombre isolé de deux à quatre chiffres situé entre deux lignes de prose pouvait être effacé. L’ancien test exigeait notamment l’effacement de `235` sans établir que ce nombre représentait une pagination. Cette attente consacrait une perte de donnée possible ; elle exige désormais sa conservation. Ce changement corrige le contrat erroné et ne masque pas une régression. Les vrais compteurs de pages disposent de témoins distincts.

Les anciens helpers privés de suppression des nombres flottants et leurs références ont été supprimés ; une recherche sur les sources C# backend n’a trouvé aucune référence restante.

## Portée de la validation suivante

L’opérateur doit exécuter la suite ciblée, les suites de régression puis une nouvelle extraction du vrai PDF avec le helper compilé. Les faits doivent être vérifiés dans le texte de la page physique 5, séparément de la lecture de chunks chevauchants. Une lecture couvrant aussi une page voisine ne peut pas compenser un fait manquant dans la page attendue. Aucun résultat GREEN, nouveau nombre de hits ou succès du cas payé n’est annoncé dans ce rapport préparatoire.
