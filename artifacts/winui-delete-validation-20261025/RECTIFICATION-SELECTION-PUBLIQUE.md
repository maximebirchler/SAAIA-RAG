# Rectification de la sélection des preuves

La sélection `public-evidence-selection-manifest.json` est **rejetée et conservée uniquement comme historique**. Son filtre excluait les répertoires `runtime` et les profils explicitement privés, mais pas les répertoires `client-data` produits par les suites de tests. Il a donc retenu à tort trois journaux `client_startup.log` et un `secure.json` appartenant à ces suites.

Le scan des secrets connus était négatif. Cela ne rendait pas ces fichiers publiables : un fichier de profil ou de journal brut doit être exclu par son rôle, indépendamment de son contenu ou de son origine synthétique.

L'opérateur a détecté cette erreur après le commit local `4472bb9`, **avant tout push**. Ce commit non publié est amendé par l'opérateur pour retirer les quatre chemins. Aucune publication distante de cette sélection incorrecte n'a eu lieu, selon sa confirmation explicite.

Le nouveau manifeste `public-evidence-selection-final-manifest.json` exclut tous les chemins comprenant un segment `client-data`, `runtime` ou commençant par `private`, ainsi que tout fichier de profil `secure.*`. Les rapports et projections gelés, l'ancien manifeste et les anciennes 525 preuves ne sont pas réécrits. L'ancien manifeste reste identifiable dans la nouvelle sélection avec le rôle d'historique rejeté.

La génération du nouveau manifeste vérifie à nouveau les fichiers retenus, les références et leurs hashes, puis scanne en mémoire les secrets des deux fixtures et les chaînes protégées de leurs profils/tests. Elle n'effectue aucune commande Git. Le nouveau manifeste n'inclut pas son propre hash dans sa liste ; l'opérateur l'ajoute explicitement lors du staging.
