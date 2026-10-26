# Qualification de l’ingestion automatique sous Linux

Le runner `invoke-linux-automatic-ingestion.py` ne démarre aucun conteneur et
n’appelle aucun LLM. L’opérateur prépare trois namespaces Docker neufs, puis lance
un scénario par namespace. Les runners corpus, lifecycle et retrieval restent
inchangés. Sans `--execute`, ce nouvel outil ne lit ni clé, ni PDF, ni configuration
privée et ne construit aucun accès Docker/HTTP.

## Trois profils et sources

Chaque owner porte un `admissionProfile` strict : `scanner`, `watcher` ou
`defaults`. Les cinq CID, leurs images, labels, montages, limites, réseaux propres
et ports loopback sont vérifiés par `OwnedDocker` avant les opérations. SQL est
limité aux SELECT statiques en transaction READ ONLY. Le backend doit utiliser
l’image et le SHA de DLL final fournis par `--backend-identity`, ainsi qu’un
profil TEI natif figé : argv, snapshot réellement monté, inventaire des artefacts
hashés, `/info`, dimension et provenance liée par `/ready` sont contrôlés.

Les trois originaux du manifeste Gold v2 doivent être présents dans `sources/` :

- `fr-native-critical-v2.pdf` ;
- `native-two-column-v1.pdf` ;
- `fr-scan-critical-v2.pdf`.

Leurs SHA doivent être distincts. Les seules copies de travail mutables sont
`documents/auto-a.pdf`, `auto-b.pdf`, `auto-c.pdf`. Les remplacements sont préparés
dans un fichier frère `.tmp` complet, vérifiés puis renommés atomiquement. Aucun
fichier de contenu inconnu n’est remplacé ou supprimé. Les originaux sont vérifiés
à nouveau pendant le scénario.

`scanner` et `watcher` démarrent avec documents et base vides. Pour `defaults`,
l’opérateur copie **avant le démarrage du backend** la source FR native vers
`documents/auto-a.pdf`. Le runner exige SHA exact et mtime antérieure au
`State.StartedAt` Docker observé ; les autres copies sont ajoutées après boot.

La configuration autorisée est explicite : WorkerEnabled=true,
WorkerConcurrency=2, MaxFilesPerScan=1, MinFileAgeSeconds=1,
ScanIntervalSeconds=2, MissingGraceSeconds=5, WatcherDebounceMs=250. Les flags
scanner/watcher correspondent exactement au profil. Les overrides d’environnement
de ces options sont refusés. La garde LLM existante reste stricte : tous les
providers, transmissions, workers avancés, CapabilityB et backoffice désactivés.
Ces timings accélérés ne sont pas les defaults d’installation ni un SLA.
Le backend doit aussi exposer un entier `ingestion_worker_concurrency=2` dans
`/ready`. Ce contrôle de liaison ne remplace pas les deux leases running mesurées.
Les jobs observés doivent provenir exclusivement du profil demandé : aucun job
watcher en scanner seul, aucun job scanner/scanner_missing/scanner_source_returned
en watcher seul. Les sources scanner de suppression conditionnelle et de retour
restent permises dans les profils scanner et combiné. Ce contrôle est actif sur
tous les snapshots fonctionnels ; le cleanup conserve son scope possédé pour
pouvoir annuler un job automatique inattendu sans l’autoriser comme preuve.

## Identité du backend et mode Production

L’entrée `--backend-identity` contient au minimum deux chaînes exactes :

```json
{"assemblySha256":"SHA256_DLL_LINUX_EN_64_HEXA_MINUSCULES","imageId":"sha256:SHA256_IMAGE_CONFIG_EN_64_HEXA_MINUSCULES"}
```

L’opérateur renseigne ces empreintes à partir de son Docker exact, avant le
scénario. `assemblySha256` est celui de `/app/SAAIA.Backend.dll` exécuté dans le
conteneur backend Linux ; `imageId` est le champ Image/Id de configuration Docker,
pas un tag ni un RepoDigest de manifest. Le runner recontrôle les deux contre le
conteneur possédé et sa DLL réellement lue, puis en conserve une preuve séparée.
Les anciens fichiers d’identité corpus ayant ces deux champs restent compatibles.
Des métadonnées additionnelles peuvent décrire le PASS Gold et son hash ; elles
ne remplacent jamais ces deux comparaisons et ne sont pas interprétées comme une
nouvelle qualification de code. Aucun hash de build Windows n’est réutilisé.

Le mode vient de `owner.qualificationMode`. `production` exige réellement
Production observé dans le conteneur et `config_signature_verified=true` dans
`/ready`. Le runner ne charge ni clé de signature ni secret de licence. Le mode
`development_diagnostic` conserve Development et son inéligibilité Production.
Aucun chemin du scénario ne force Development ou ne transforme une configuration
non signée en configuration vérifiée.

## Parcours et verdicts

L’outil ajoute A, publie une source différente sur le même chemin, restaure A,
puis ajoute B et C sans duplication binaire simultanée. Il vérifie la publication
des trois fichiers avec la limite de scan1, tout en surveillant qu’un témoin
présent ne devienne jamais missing/deleted. Pour le profil combiné, C est ajouté
après boot ; A est mis à jour après observation running/done de C. Les sources
`scanner` et `watcher` sont prouvées dans les jobs réellement terminés, pas
déduites de la configuration ou du timing seul.

Chaque publication vérifie toutes les annotations physiques du PDF attendu dans
le bundle canonique actuel et les chunks, les SHA UTF8, source/révision/version,
manifeste de points scellé, UUID physiques dérivés et vecteurs768 réels. Une
requête littérale indépendante est suivie d’une lecture exacte du chunk courant.
Cela ne prétend pas mesurer toutes les reformulations naturelles.

Le retrait de A exige des comportements différents :

- scanner/combiné : suppression finale, indexed0, planNULL, recherche vide et
  ancienne génération réellement collectée par GC ; recréation avec génération
  strictement supérieure et nouveaux IDs physiques ;
- watcher seul : missing avec publication et vecteurs antérieurs inchangés ;
  rétablissement puis convergence indexed, sans prétendre à une suppression finale.

Le combiné tente une fois de capturer un delete scanner queued avant retour de
source. S’il finit avant cette fenêtre, la variante est `NON_EXECUTED`. Si la
fenêtre est prouvée par timestamps, le delete doit être canceled avec la raison
prévue, garder la publication et permettre une génération fraîche. Les preuves
PostgreSQL causales distinctes ne deviennent jamais un PASS runtime.
L’observation prouve les états capturés et la publication terminale, pas une
disponibilité continue entre deux SELECT ; la propriété transactionnelle reste
portée séparément par les tests PostgreSQL du produit.

Deux workers sont prouvés uniquement par **un même instantané SQL** comportant
deux jobs running, leases cohérentes et owners w0/w1 distincts. Le profil combiné
reste non validé si cette preuve manque ; scanner/watcher peuvent être
fonctionnellement valides avec concurrence `NON_EXECUTED`. Cela ne prouve pas
deux moteurs OCR exécutés en parallèle. B et C sont protégés par leur publication
SQL et le SHA de tous leurs vecteurs/payloads avant/après les mutations de A.
Une dernière fenêtre calme de6secondes refuse les nouvelles admissions spontanées.

## Commande opérateur

Après démarrage de la pile possédée et copie des seuls outils nécessaires :

```sh
python3 -B tools/validation-ingestion/invoke-linux-automatic-ingestion.py \
  --owner "$ROOT/owner.json" --manifest "$ROOT/manifest.json" \
  --embedding-profile "$ROOT/tei-profile.json" \
  --backend-identity "$ROOT/backend-binary-identity.json" \
  --source-directory "$ROOT/sources" --api-key-file "$ROOT/private/api-key.txt" \
  --output "$ROOT/evidence/automatic-scanner-v1" --admission-profile scanner
```

Ajouter `--execute` autorise uniquement cette campagne isolée. Le chemin de clé
doit appartenir au namespace ; seul le bootstrap admin synthétique est lu après
les gardes d’exécution. Les deux headers admin/API sont fournis à cette clé locale,
jamais à un compte client habituel.

Enfin, seules les tâches actives des trois chemins connus peuvent recevoir une
annulation API. Le backend producteur est arrêté avant le dernier SELECT du
nombre de jobs actifs, puis les cinq CID exacts sont arrêtés et les bindings
Docker contrôlés. Les DB, volumes, modèles et preuves sont conservés. L’observation
zéro job manquante rend le résultat false ; un défaut de source ne bloque pas ce
cleanup borné. La clôture a sa propre limite et peut dépasser la fenêtre de
qualification1800secondes/phase300secondes, sans réécrire sa durée.

Les détails SQL, bundles, requêtes et réponses restent dans `private-observations`.
Le résumé public ne contient ni texte PDF, ni clé, ni payload/vecteur brut. Une
exécution Development non signée demeure explicitement inéligible à une
qualification de signature Production.
