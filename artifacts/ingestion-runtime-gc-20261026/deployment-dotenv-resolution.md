# Lecture `.env` et rendu signé — correction complémentaire

État : **source gelée**, inventaire final `deployment-corrections-source-manifest-v3.json` (17 fichiers). Les manifestes v1/v2 et rapports précédents restent les ancrages historiques de leurs captures. Aucun service, build .NET, Docker, transfert serveur, lecture de secret existant ou signature cryptographique réelle pour ce complément.

## Défaut et contrat

L’ancien `Read-DotEnv` conservait les guillemets et commentaires dans les valeurs. Compose et le document destiné à la signature pouvaient donc employer des mots de passe différents. Le booléen `"false"` conservé avec ses quotes devenait même le défaut `true` dans le rendu bootstrap. Les assignations malformées, héritées ou interpolées étaient silencieusement ignorées ou transmises telles quelles.

Les règles ont été vérifiées dans les [instructions Docker Compose](https://docs.docker.com/compose/how-tos/environment-variables/variable-interpolation/) et le [parseur primaire compose-go](https://raw.githubusercontent.com/compose-spec/compose-go/main/dotenv/parser.go). La documentation distingue notamment valeurs entre quotes, commentaires et interpolation ; le parseur primaire permet de vérifier le comportement précis des échappements. Ces deux consultations documentaires sont distinctes des tests sans réseau et ne contactent aucun runtime ni fournisseur de modèles.

Le nouveau composant `DotEnvParser.ps1` garde l’API `Read-DotEnv` et fournit un **sous-ensemble explicite**, sans prétendre implémenter Compose entier :

- Assignations ordinaires, séparateurs `=`/`:`, préfixe `export`, valeurs vides et dernière valeur d’une clé dupliquée.
- Quotes retirées, contenu des valeurs conservé, commentaires distingués des `#` littéraux et `=` internes préservés.
- Backslashes littéraux dans les valeurs simples/non quoted et échappements de chaînes doubles. Pour un chemin Windows, les quotes simples ou les doubles backslashes évitent une séquence interprétable telle que `\a`.
- UTF8 strict, sans repli ANSI de PowerShell 5.1.
- Refus des interpolations non prises en charge, valeurs héritées sans assignation, multiligne, octal et texte invalide. Le `$` final littéral est accepté ; une expression entre quotes simples demeure littérale. Les erreurs contiennent le numéro de ligne, jamais la valeur refusée.

Le rendu JSON utilise la sérialisation native de PowerShell pour préserver les caractères de contrôle décodés. L’ancien remplacement supprimait notamment le retour chariot ; il ne pouvait donc conserver une valeur décodée avec fidélité.

## Preuves hors ligne

| Capture | Résultat |
| --- | --- |
| `tests/dotenv-subset-red-v2/result.json` | **15 échecs causaux, 1 témoin vert**, avant correction du parseur/rendu. |
| `tests/dotenv-subset-green-final-v1/result.json` | **18/18 verts**, lecture réelle de fichiers synthétiques, vrai rendu et signer simulé. |
| `tests/deployment-post-dotenv-green-v1/result.json` | **17/17 verts**, non-régression du lot déploiement/archive. |
| `deployment-identity-python-green-v3.log` | **7/7 verts**, dont identité PS/Python. |
| Manifeste v3 | **8 AST PowerShell, 0 erreur**, SHA/tailles finales. |

La capture `dotenv-subset-red-v1` a précédé le renforcement du témoin `export`/`:` ; v2 le contrôle séparément. La première capture verte avait encore un chemin Windows synthétique entre quotes doubles non échappées : elle a échoué conformément à Compose, à cause de `\a`. La fixture utilise ensuite les quotes simples correctes ; aucune règle produit n’a été assouplie pour masquer ce résultat.

Le test d’intégration vérifie que le mot de passe synthétique décodé est celui placé dans la connexion destinée à la signature, que le pepper est décodé et que le bootstrap reste désactivé. Un autre test vérifie qu’une erreur ne répète pas son contenu synthétique refusé. Les faux fichiers et signatures de travail ne sont pas des preuves cryptographiques.

## Limites

Le mode shell historique utilise encore son propre chargement/rendu ; son équivalence complète à ce sous-ensemble PowerShell n’est pas qualifiée. Une véritable comparaison par `docker compose config` n’a pas été exécutée localement, le PC ne disposant pas de Docker : les attentes viennent des sources primaires et des fixtures contrôlées. L’identité de l’image et la DLL Linux doivent toujours être observées séparément.

La lecture de fichier ne prétend pas résoudre l’ensemble des priorités de secrets hérités du processus. Les identités runtime, modèle et entiers opérationnels ont leur priorité de processus explicite dans le lot précédent. Les formes refusées doivent être reformulées comme valeurs littérales compatibles, plutôt que silencieusement interprétées différemment. Les anciens rapports ne constituent aucune qualification globale de l’installateur Linux ou de tous les formats de credentials.
