# Contrôle indépendant de l’exporteur de preuves publiques

L’exporteur `prepare-public-evidence.py` a été testé et corrigé exclusivement avec des fixtures synthétiques neuves. Aucun document réel, coffre de secrets, service, réseau, modèle ou Git n’a été utilisé pour cette validation ; aucune preuve réelle n’a été exportée.

## Défauts reproduits

La suppression des sous-arbres Output, Deployment et RunConfiguration laissait les arguments des noms de tests dans les attributs TRX `testName` et `UnitTest.name`, ainsi que dans les noms des cases du manifeste. Une valeur opaque synthétique en argument passait le scan de motifs connu. Des nœuds XML non prévus restaient également publiés.

Deux champs pouvaient construire une destination qui sortait du dossier d’export : le nom de projet et le chemin d’inventaire. Un nom de projet `../../escaped` écrivait un fichier à côté de ce dossier ; un inventaire `../../inventory.json` tentait d’y écrire avant de rencontrer un fichier synthétique préexistant. Le lecteur acceptait aussi des chemins source contenant une traversée `..` tant qu’ils résolvaient à l’intérieur de la campagne.

## Corrections

Le TRX public est désormais reconstruit à partir d’une liste explicite de résultats, définitions de méthodes, identités, listes de tests, dates et compteurs. Les textes arbitraires, attributs non prévus, sorties, identités locales, chemins de déploiement et nœuds inconnus ne sont pas recopiés. Les IDs de run/test/exécution/liste restent identiques ; les champs typés copiés sont validés.

Chaque nom expose uniquement le nom de méthode sans arguments, accompagné de `originalTestNameSha256` calculé sur le nom original exact en UTF-8. Deux variantes d’une même théorie restent distinguables par ce SHA. Les cases du manifeste sont reliées par ce SHA aux noms effectivement projetés dans le TRX ; leurs champs capturés non prévus sont éliminés. Les SHA256 du TRX et du manifeste originaux restent dans la provenance ; le fichier public reste explicitement une projection différente de l’original.

Les chemins sources et destinations absolus, vides, de type Windows drive ou contenant des composants `.`/`..` sont refusés. La destination résolue doit rester dans la nouvelle racine d’export. Un projet ne peut plus être un chemin. Les sources résolues sont également vérifiées contre les composants interdits, y compris après résolution d’un lien.

## Preuves exécutées

`tests/public-evidence-export-v1/red-v2.log` reproduit neuf cas : deux témoins PASS, six assertions en échec et une erreur causale de destination (`FileExistsError` après tentative hors racine, au lieu du refus préalable attendu). La toute première capture `red.log` comportait aussi une erreur de fixture d’inventaire dupliqué ; elle ne doit pas remplacer la capture v2.

`tests/public-evidence-export-v1/green-v3.log` : **12 tests exécutés, 12 PASS, aucun skip**. Ils couvrent les arguments opaques ou de forme API, les nœuds XML inconnus, le manifeste public, les SHA distinguant les variantes, la conservation des IDs et outcomes, le refus d’un rapport contenant une clé synthétique, les traversées de projet/inventaire/source, les formes absolues Windows et le rejet d’un fichier dont l’empreinte a changé.

L’ancien fichier source n’a pas été scellé par SHA séparément avant le RED. Les journaux attestent la séquence exécutée ; l’ancrage `tests/public-evidence-export-v1/proof-v1.json` scelle les octets actuels de l’exporteur, des tests et des deux journaux retenus. Le test de traversée d’inventaire vérifie maintenant que le fichier synthétique préexistant est inchangé ; son absence n’aurait pas été une attente correcte de fixture.

## Portée

Le scan de motifs reste une défense supplémentaire ; il ne démontre pas l’absence de toute forme imaginable de secret dans un rapport libre. Le contrat vérifié ici supprime les valeurs de paramètres des tests avant publication, conserve les résultats et leur provenance, et empêche les traversées testées. L’exécution sur la véritable proposition de preuves et son éventuel staging restent sous coordination du parent.
