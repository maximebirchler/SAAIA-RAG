# Contre-revue indépendante des preuves publiques finales

Revue du 26 octobre 2026, exclusivement en lecture seule sur les exports publics, les manifests/TRX locaux et les octets source explicitement inventoriés. Aucun Git, réseau, service, build .NET, magasin de secrets ou original privé n'a été utilisé. Aucun écart matériel d'intégrité, d'acceptation déclarée ou de liaison de sources n'est établi dans ce périmètre. Cette conclusion conserve les limites ci-dessous ; elle ne prononce pas l'approbation globale du RAG ni du planning.

Le fichier JSON compagnon fournit les contrôles structurés et les SHA des 88 entrées lues. Les exports et leurs sceaux n'ont pas été modifiés.

## Intégrité et confidentialité des exports

`public-export-production-final-v4/public-export-seal.json` couvre exactement 54 projections, sans fichier supplémentaire ni manquant. Les 54 tailles et SHA correspondent. Son SHA est `c43d581287694cd6a43bcf278205699422d12db4603dc18303cdd0c5c9f789a7`.

`public-supplement-final-v4/supplement-seal.json` couvre exactement 16 enregistrements, également sans écart de taille, SHA ou inventaire. Son SHA est `ae1eed5d0f1b10aa702d9afbb2a4e65eb57b99f35a1512b3400582b9ef24ff43`.

Les 72 JSON, sceaux compris, ne présentent aucun des marqueurs de clé privée/API/DPAPI recherchés, aucun chemin hôte et aucun champ de contenu sensible recherché : mot de passe, chaîne de connexion, corps documentaire, extrait, commande, environnement ou montages. Il s'agit d'un examen des structures publiques et de formats reconnaissables ; ce n'est pas le contre-scan des valeurs privées effectives. Aucun de ces magasins ou originaux privés n'a été ouvert pour cette revue. Le contre-scan final des valeurs connues relève de la revue d'export/sélection autorisée par root.

Le sceau distingue neuf rôles Production, répartis sur cinq namespaces et cinq configurations, ainsi qu'un contrôle de signature indépendant. Il ne s'agit donc pas de neuf installations ou configurations indépendantes. Les fichiers restent des projections, avec SHA de leurs sources et mention explicite de l'absence de validation runtime par l'exporteur.

## Sources, binaires et index

Les 445 entrées du snapshot `build-final-v4/manifest.json` correspondent toutes aux octets source courants. Le calcul indépendant `path|bytes|sha256`, joint par LF selon le vrai helper de déploiement, retrouve :

`source-sha256:dca75ae2906275ab1120aa316aab0c4d5d492fa74a5853dcd5eeb56bb3111a5a`.

Cette révision correspond aux neuf attestations runtime Production et au précontrôle de l'image. Les identités finales à employer sont celles des preuves, et non les tags historiques :

- Image Linux backend : `sha256:d272895b502e7cd902afa7c197eac34b1227ffacfde80204c02f4b444b3587cd`.
- DLL backend Linux : `f6825b8c36bc26a5da8308eb4d4a5ec87c62d6962f57f644c725e8e606eb18c4`.
- DLL backend AnyCPU des tests : `36d369a7ae567b73698324de1e76c078267edf386ecfd16f45a23c76513cf5a0`.
- DLL tests : `583c9042fb813784d405311e0dacdd68ec04637c9c4f4e8ce7a44b057299523f`.

Les DLL Windows/AnyCPU et Linux sont volontairement distinguées. Aucune égalité de leurs octets ni reproductibilité de compilation n'est prétendue. Les 436 fichiers communs entre le snapshot Docker et l'inventaire Unit final ont tous le même SHA. Les neuf entrées restantes du snapshot sont les Dockerfiles/ignore, configurations de référence, HTML, `launchSettings.json` et `global.json` ; leur validation n'est pas attribuée à l'inventaire Unit.

Le rapport `build-final-v4/index-source-comparison-v1.json`, observé par root à 10:50:15 UTC, est cohérent avec les 445 entrées : 443 identités exactes et deux écarts de fins de ligne déclarés, dans `launchSettings.json` et `wwwroot/index.html`. Cette revue n'a pas consulté Git : elle vérifie le rapport et ses liaisons, sans certifier à nouveau l'index vivant.

## Tests finaux

Les SHA des cinq TRX finaux correspondent aux manifests. Les comptages ci-dessous sont aussi vérifiés sur les résultats individuels `UnitTestResult`, sans se fier au seul compteur global `notExecuted` des adaptateurs.

| Capture | Résultat observé | Portée |
|---|---:|---|
| Unit finale, quatre projets, run `4e87ad51184f4572966e89a099f9b221` | 5 323 PASS, 0 FAIL, 2 NotExecuted | Backend 2 802, Contracts 20, Core 82, Client 2 419 |
| PostgreSQL final, run `bc44cc73f4214458bc9e1e52c86e501a` | 350 PASS, 0 FAIL, 2 NotExecuted | Les deux tests runtime-ready historiques restent non exécutés |
| Vrai OCR Linux opt-in | 1 sélectionné, 1 exécuté, 1 PASS, 0 skip | Moteurs réels et fixture embarquée ; composant, pas worker complet |
| Vrai cas de casse du filesystem Linux | 1 sélectionné, 1 exécuté, 1 PASS, 0 skip | Composant filesystem/cache/planner |
| Protocole retry Docling sous Linux | 26 exécutés, 26 PASS | HTTP synthétique dans `network=none`, pas faute injectée au vrai service Docling |

Les deux NotExecuted de la suite Unit sont précisément les cas OCR opt-in et filesystem Linux, fermés séparément par les deux captures Linux. Les manifests Unit et PostgreSQL ont des inventaires avant/après identiques et `sourceChangedDuringRun=false`. Ces deux suites sont `NoBuild`, mais une compilation ciblée antérieure, run `d1efa9e875324fd8b0a00838dcdadbd8`, porte le même inventaire source et les mêmes DLL Backend/tests. Les trois captures Linux supplémentaires utilisent exactement ces DLL AnyCPU, le même inventaire de paquet et une résolution d'assemblies tracée ; il ne s'agit pas d'une attestation par profiler de tous les modules chargés.

Le cluster PostgreSQL possédé a été retiré, son mot de passe bootstrap effacé et l'environnement restauré ; aucun listener résiduel et aucune modification du cluster préexistant sont déclarés dans le manifeste de clôture.

## Ce que les campagnes réelles établissent

Les cinq attestations principales concordent sur le mode Production, l'environnement `Production`, la signature vérifiée, l'image et la DLL finales. Les dix preuves de santé/chronologie du supplément correspondent aux mêmes quatre dépendances et au même backend de chaque namespace. Chaque dernier tour de santé est positif, le SHA de cette preuve est exact dans la chronologie, et le vrai `StartedAt` backend est postérieur à la fin de la barrière. Les cinq démarrages ont effectivement attendu neuf ou onze tours de santé. L'ancien échec de démarrage HTTP502 avant l'écoute amont est conservé séparément comme `FAIL_PRESERVED`.

Le worker Gold publie 12 documents, 40 chunks/points et 90 faits physiques annotés. La recette publie un document, 158 chunks/points et 16 faits annotés. Les bundles/checksums, vues SQL, identités des points et fingerprints de plan, lecture API et recherche source-bound sont positifs. Le contrôle de rappel complémentaire retrouve 90/90 puis 16/16 faits et vérifie les contrôles négatifs des 12 cas Gold et du cas recette. Il hérite explicitement de la qualification du corpus signé et ne refait pas `/ready` ; cette portée est conservée dans les projections.

Le booléen historique `vectorValidated` demeure **false** : le runner l'initialise ainsi et ne le promeut pas. Il ne contredit pas `embeddingRuntimeAttested=true` et `vectorStorePublicationValidated=true`, qui portent respectivement sur TEI, ses artefacts/identité/dimension, et les points/payloads effectivement publiés. Aucune qualité sémantique ou qualité de classement du canal dense seul n'est approuvée. Les recherches hybrides utilisent des requêtes et sources bornées ; ces 106 annotations ne démontrent pas un rappel exhaustif.

Le cycle réel conserve les générations, purge les points obsolètes exacts et supprime une réapparition contrôlée ; cette réapparition n'est pas présentée comme l'interruption prouvée d'une écriture HTTP de worker périmé. Le cycle final possède bien un acquittement d'annulation et conserve la publication 4 sous génération abandonnée 5. Il ne prouve ni l'arrêt de calcul amont ni la fraîcheur documentaire à nouveau vérifiée. Le remplacement invalide échoue tout en conservant la publication et les vecteurs précédents, puis une source restaurée donne une nouvelle publication ; la stabilité de retry est une observation bornée de dix secondes.

Les trois profils AUTO ont réussi leur portée fonctionnelle. Scanner seul n'a observé qu'un worker simultané : son sous-contrôle deux consommateurs reste `NON_EXECUTED`. Watcher et defaults ont chacun observé les consommateurs 0 et 1 dans le même snapshot SQL ; cela ne prouve pas du calcul OCR réellement parallèle. Watcher seul marque le fichier manquant et conserve l'ancienne publication, sans prétendre avoir exécuté la suppression finale. Dans defaults final, la course de retour pendant un delete reste `NON_EXECUTED` (`delete_completion_won_before_queued_window_observation`) ; les tests PostgreSQL causaux ne doivent pas être rebaptisés en réussite de cette variante runtime.

La clôture indique les 80 conteneurs enregistrés dans les 16 namespaces possédés arrêtés, les 22 conteneurs de baseline inchangés, et aucun conteneur courant hors baseline. Ces observations proviennent des enregistrements publics datés, sans nouvelle consultation live par cette revue.

## Signature, réseau et approbation

Le contrôle négatif de signature possède un supplément séparé, `signature-rejection-safe-diagnostic-final-v2.json`, sans altération des sceaux. Son SHA de résultat, son CID et son image correspondent exactement à la projection initiale. Il conserve `exitCode=139`, sans OOM, et fournit le diagnostic exact : `Unhandled exception. System.InvalidOperationException: deployment.config.json signature verification failed.` Le refus de signature est donc relié à une raison explicite ; aucun redémarrage, log privé ou clé de signature n'a été ouvert pour cette contre-revue.

Les snapshots des piles finales ne prétendent pas bloquer l'egress par réseau Docker internal : le champ correspondant est explicitement false dans les identités qui l'exposent. Les contrôles de composants `network=none` et le contrôle Docling offline historique sont des preuves distinctes. Aucune qualification air-gap universelle des cinq piles n'est inférée ici.

Les preuves ne couvrent pas toutes les langues, configurations, mises en page, moteurs, charges ou options possibles. Elles ne valident ni une maintenance GPU, ni un nouvel installateur client, ni un test payant du modèle, ni le planning multisource. Le statut produit `TESTE_NON_APPROUVE` reste présent dans les plans ; la décision d'approbation globale ne découle pas de cette seule revue d'ingestion.
