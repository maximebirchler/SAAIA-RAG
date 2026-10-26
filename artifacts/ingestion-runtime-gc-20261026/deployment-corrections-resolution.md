# Configuration et provenance du déploiement — corrections hors ligne

État : **correctifs implémentés, tests hors ligne verts ; pile Docker non qualifiée par ce lot**. Aucun build .NET, conteneur, service, appel réseau, opération Git ou lecture d’un secret existant. Les sources sont gelées avec leurs tailles/SHA dans `deployment-corrections-source-manifest-v1.json`.

## Écarts reproduits puis corrigés

Les tests exécutent le vrai `New-SignedConfig` et le vrai installateur PowerShell dans un dépôt synthétique. Les frontières dotnet/signature, Docker, HTTP et attente sont remplacées par des fonctions ; elles ne constituent donc aucune preuve cryptographique ni de service disponible.

- **Rendu effectif** : les identités TEI/Qdrant ne restent plus fixées dans le template signé. Le digest provient de l’image effective, avec priorité des variables de processus identique à Compose. Une révision déclarée différente d’un digest de l’image est refusée avant signature. Une image mutable demande une déclaration explicite de digest, qui reste une déclaration à contrôler sur l’image réelle.
- **Découpage cohérent** : le défaut signé passe de 220 à **200/0/25**, identique au compose. Les overrides valides sont conservés ; un minimum supérieur au maximum et un recouvrement supérieur ou égal au maximum sont refusés. Les entiers opérationnels prennent aussi la priorité du processus en compte.
- **Entrées de compilation** : `global.json` et `Directory.Build.props` sont admis dans le contexte Docker et copiés avant restore. Le SDK utilise **8.0.420**, déjà exigé par le checkout ; il ne s’agit pas d’un choix de nouvelle version de moteur. L’archive de sources distante inclut désormais les propriétés communes.
- **Identité du code** : un inventaire de contenu des sources et entrées de build fournit `source-sha256:…`, sans dépendre de Git ni d’un checkout propre. Install/update/remote le transmettent au build ; le Dockerfile refuse une identité vide ou `unknown` avant restore. Le runtime et un label de l’image conservent la déclaration. La source du wrapper Docling reçoit aussi une identité de contenu. La valeur Compose peut rester vide pour permettre inspection/arrêt ; elle échoue au build Docker, au lieu de bloquer les opérations qui n’en construisent aucun.
- **Disponibilité** : l’installateur PowerShell, l’installateur shell et la vérification distante terminent désormais en erreur si la disponibilité n’est jamais confirmée. Un avertissement ne devient plus un succès de déploiement.

Les fonctions d’identité PowerShell et Python produisent le même résultat sur des entrées synthétiques avec fins de lignes conservées. L’installateur Linux utilise Python 3 standard pour cette identité et vérifie sa présence sans l’installer. Les fichiers générés/privés exclus du contexte ne doivent pas modifier l’identité du code ; les sources et les propriétés communes la modifient.

## Preuves

| Capture | Résultat | Portée |
| --- | --- | --- |
| `tests/deployment-offline-red-v1/result.json` | historique partiellement invalide | Une portée de variable du mock a provoqué un échec prématuré ; le témoin disponibilité de cette première capture n’est pas causal. |
| `tests/deployment-offline-red-v2/result.json` | **10 échecs, 1 témoin vert** | Reproduction correcte avant corrections : runtime, découpage, provenance, entrées Docker et vraie boucle des 40 tentatives de disponibilité. |
| `tests/deployment-archive-red-v1/result.json` | **1 échec, 14 verts** | La commande `tar` réelle est extraite de l’AST puis exécutée avec transport simulé ; elle omettait `Directory.Build.props`. |
| `tests/deployment-offline-green-final-v1/result.json` | **15/15 verts** | Corrections et témoins, dont contexte de compilation, archive, priorité Compose, source modifiée et disponibilité. |
| `deployment-identity-python-green-v1.log` | **7/7 verts** | Identité Linux hors ligne, refus des divergences et témoin interlangage sur les deux inventaires. |
| AST PowerShell, manifeste de sources | **6 scripts, 0 erreur** | Syntaxe des scripts modifiés ; aucune interprétation Linux ni compilation Docker implicite. |

Le contrat initial qui aurait rendu la variable de build obligatoire à toute interpolation Compose a été ajusté : la vérification appartient au build, car les commandes de diagnostic/arrêt n’ont pas à inventer une identité pour inspecter un conteneur déjà construit. Ce choix est couvert par un témoin distinct et ne retire pas le refus Docker avant restore.

## Limites conservées

Ce lot ne valide ni l’existence du tag SDK dans un registre ni le résultat d’un build Linux. Les digests et versions historiques TEI/Qdrant/Docling sont inchangés. Les paquets apt, l’image runtime .NET et PostgreSQL demandent toujours une observation/qualification de pile exacte ; aucune compatibilité n’est déduite des numéros.

L’identité est celle des entrées lues au moment du calcul. Elle ne remplace pas un instantané de build figé, l’ID/digest de l’image effectivement créée, le SHA de la DLL effectivement chargée ni l’observation des paquets/modèles. Aucun SHA de DLL Windows n’est réutilisé pour une DLL Linux. Une image mutable accompagnée d’un digest déclaré demande encore une vérification indépendante de cette déclaration.

La signature réelle est volontairement simulée ; ni clé privée réelle ni ConfigSigner .NET ne sont exécutés. L’installateur shell n’a pas été exécuté dans Linux ici. Son rendu historique par `sed` reste une limite pour des valeurs contenant des caractères spéciaux de shell/sed/JSON ; cette capture ne qualifie pas toutes les formes de credentials. Les commandes shell et le démarrage réel seront à exercer dans la pile CPU isolée, avec preuves propres à celle-ci.

L’update ne régénère pas automatiquement une ancienne configuration signée : lors d’un changement d’image/identité, l’opérateur doit utiliser le parcours de génération/signature et vérifier les valeurs effectivement chargées. `/ready` ne remplace pas la validation documentaire de bout en bout. Les capacités payantes restent désactivées dans les témoins ; les montants historiques du template n’ont aucun statut d’autorisation ou de solde.

Aucune source d’ingestion, de bail, de job, de GC ni aucun fichier `tools/validation-ingestion` n’est modifié par ce lot.
