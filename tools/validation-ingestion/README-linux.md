# Qualification Linux des vrais conteneurs

Ces scripts n'installent, ne construisent et ne démarrent aucun service. Ils
consomment une installation dédiée déjà préparée par l'opérateur. Ils n'utilisent
ni SSH, ni contexte Docker distant, ni clé de fournisseur LLM. `--execute` est la
frontière avant Docker, HTTP et lecture de la clé synthétique du backend. Sans ce
paramètre, le runner vérifie seulement les fichiers publics d'entrée et imprime
un plan. Il ne lit ni la clé, ni les poids, ni la configuration signée.

## Manifeste d'ownership

Le fichier JSON `saaia-linux-ingestion-runtime.v1` comporte :

- `runId` UUID et `root` exact `/home/maxime/saaia-ingestion-qualification/<UUID>` ;
- `project` égal à `saaiaqual_` suivi des 12 premiers hexadécimaux de l'UUID ;
- `documentsRoot=<root>/documents`, vide avant le premier passage ;
- `configurationPath=<root>/deploy/deployment.config.json` ;
- `database` et `databaseUser`, chacun préfixé `saaia_qual_` ;
- `qdrantCollection=ingestion_<UUID complet sans tirets>` ;
- `endpoints` contenant exactement `backend`, `tei`, `qdrant`, `docling`, avec
  chacun une origine `http://127.0.0.1:<port élevé distinct>`, sans PostgreSQL ;
- `networkPolicy=internal` ou, explicitement, `dedicated_bridge` lorsque les ports
  publiés ne sont pas accessibles avec le bridge interne. Ce second mode ne
  prouve **aucun blocage d'egress** ; les observations le disent ;
- `qualificationMode=production` par défaut exige le vrai environnement
  Production et une signature vérifiée. `development_diagnostic` permet un test
  explicite en Development, avec `deploymentQualificationEligible=false` et
  `deploymentQualificationPassed=false`, même si l'ingestion fonctionne ;
- `containers` : cinq objets `role`, `service`, `id` complet 64 hexadécimaux et
  `imageId=sha256:<64 hexadécimaux>`, pour postgres/qdrant/tei/docling/backend.

Chaque conteneur doit avoir les labels `com.docker.compose.project`,
`com.docker.compose.service`, `com.saaia.qualification.run-id`. Ses seuls réseaux
doivent porter le préfixe du projet et être labellisés par ce projet. Les CPU et
la mémoire sont bornés. Le mode privilégié, host PID/IPC, l'accès GPU et les
devices hôte sont refusés. Tous les binds doivent être sous la racine exacte,
sans symlink. Les mounts backend `/app/documents` et
`/app/deployment.config.json` doivent désigner les deux chemins exacts ci-dessus.
PostgreSQL ne publie aucun port ; les SELECT utilisent son CID avec `docker exec
psql`, dans une transaction `READ ONLY`, via stdin UTF8 et sans shell.

## Profil TEI natif

Le schéma est `saaia-tei-embedding-profile.v1`, `adapter=tei_cpu`. Il contient
`dimensions=768`, `imageId`, `modelFileSha256`, `truncationPolicy` (`reject` ou
`auto_truncate`), `expectedInfo` et `Rag`. `expectedInfo` utilise les **vrais**
champs natifs de `/info` : au minimum `model_id`, `model_sha`, `version`,
`max_input_length`. Des champs natifs supplémentaires observés peuvent être
figés. Aucun champ `adapter`, `offline` ou `runtime_revision` fictif n'est ajouté
à la réponse TEI.

Le client Docker impose le socket Unix local `/var/run/docker.sock`, sans
reposer sur un contexte distant ni modifier un contexte global. L'entrypoint
backend ne peut pas ajouter des arguments de configuration à la DLL publiée.

`Rag` contient l'URL loopback TEI, `EmbeddingsModel`, sa révision immuable et
`EmbeddingsRuntimeRevision` égal à l'image ID Docker figée. `model_sha=null`
n'est accepté que lorsque le profil dit explicitement
`modelRevisionAttestation=owned_command_and_weights` ; ce null est conservé. La
révision n'est alors pas attribuée à `/info` : elle vient séparément de l'argv
`--revision` et du snapshot possédé. Même lorsque `/info` donne la révision, le
runner vérifie aussi les argv et les fichiers réellement montés.

`containerModelRoot` désigne exactement
`/data/models--intfloat--multilingual-e5-base/snapshots/<révision>` lorsque
`--huggingface-hub-cache /data` est utilisé. Le runner résout ce chemin à travers
les binds réellement inspectés. `modelFileRelativePath` nomme les poids exécutés
(par exemple `onnx/model.onnx` pour TEI CPU1.6). `modelArtifacts` associe les noms
relatifs à leurs SHA256, avec au moins ces poids, `config.json`, `tokenizer.json`
et `1_Pooling/config.json`. L'inventaire doit couvrir exactement les fichiers
présents dans ce snapshot (par exemple `sentence_bert_config.json`). Un
`tokenizer_config.json` n'est exigé que s'il existe réellement. Aucun fichier
inutile n'est inventé ou téléchargé pour satisfaire le harnais. Les fichiers sont contrôlés
avant et après le passage ; le fichier `--model-file` doit être exactement celui
du snapshot réellement monté. Les poids ONNX ne sont jamais attestés par le hash
des safetensors utilisés dans l'ancienne validation Transformers.

La preuve est une association image/argv/cache monté/fichiers hashés et non une
trace kernel d'ouverture de fichier. Les modèles et fichiers de cache sont
publics ; le cache de production ne doit jamais être monté dans cette pile.

## Exécution

Copier les outils et leur package Python `tools/ingestion_gold` dans la racine
possédée, avec le manifeste du corpus et les PDF hashés. Aucun package Python
tiers n'est nécessaire au runner : la bibliothèque standard suffit aux captures
et à l'évaluation de ces manifestes. Utiliser des chemins absolus.

```bash
python3 -B tools/validation-ingestion/invoke-linux-ingestion.py \
  --owner /home/maxime/saaia-ingestion-qualification/UUID/owner.json \
  --embedding-profile /home/maxime/saaia-ingestion-qualification/UUID/tei-profile.json \
  --manifest /home/maxime/saaia-ingestion-qualification/UUID/corpus/manifest.json \
  --source-directory /home/maxime/saaia-ingestion-qualification/UUID/corpus/sources \
  --output /home/maxime/saaia-ingestion-qualification/UUID/evidence/campaign-01 \
  --api-key-file /home/maxime/saaia-ingestion-qualification/UUID/private/backend-api-key.txt \
  --model-file /home/maxime/saaia-ingestion-qualification/UUID/cache/tei/models--intfloat--multilingual-e5-base/snapshots/REV/onnx/model.onnx \
  --case-id fr-scan-critical-v2 --case-id fr-mixed-critical-v2
```

Le plan doit être relu avant de répéter avec `--execute`. La configuration signée
doit désactiver AdvancedAnalysis, son worker, les transmissions externes et la
licence correspondante, ainsi que les workers LLM automatiques/backoffice. Le
scanner et le watcher restent désactivés pour que seuls les PDF sélectionnés
soient admis par le scan API. La Production exige une signature et une provenance
canonique réellement validées par le backend containerisé. Le diagnostic
Development conserve honnêtement une éventuelle signature non vérifiée et reste
inéligible à la qualification Production. L'empreinte de sa DLL
Linux est lue dans le conteneur ; celle de la DLL Windows n'est pas réutilisée.

Le runner exige une DB d'ingestion vide, copie les PDF possédés puis déclenche
le vrai scan API et attend les jobs terminés. Il capture la publication SQL, les
quatre checksums de texte, les lectures physiques, les recherches/contre-témoins
et les points Qdrant de plan actif, et appelle le même vérificateur pur que la
campagne Windows. Aucun canonical seed ni modèle de génération n'intervient.
Chaque campagne exige une nouvelle DB/configuration isolée ; ce runner ne
tronque jamais une DB pour réutiliser le même périmètre.

Le `finally` annule uniquement les jobs actifs de ce corpus après preuve de DB
initialement vide, attend un état sans job actif, puis prévalide et arrête les
cinq CID exacts. Un contrôle manquant produit un échec, pas un succès partiel.
Les volumes, modèles, images et preuves sont conservés. L'absence finale des
bindings Docker est mesurée ; elle ne prétend pas prouver l'absence de tout
listener hôte non Docker. Aucun `prune`, aucun `compose down -v` n'est exécuté.

Si un préflight échoue avant l'entrée dans le parcours, le stop indépendant est :

```bash
python3 -B tools/validation-ingestion/stop-linux-runtime.py \
  --owner /home/maxime/saaia-ingestion-qualification/UUID/owner.json \
  --output /home/maxime/saaia-ingestion-qualification/UUID/evidence/stop-01.json
```

Il est lui aussi NOEXEC sans `--execute`. Toutes les preuves/captures sont neuves
et ne doivent pas écraser une campagne précédente. Les captures privées, clés,
configurations signées, corpus et données SQL restent exclus des preuves Git.
Un PASS sur la publication ou la petite sonde vectorielle n'est pas une
acceptation générale des documents ni de la qualité des réponses LLM.
