# Export public scellé des campagnes Linux

Cet outil fait uniquement des lectures/écritures de fichiers JSON publics. Il ne
lance ni SSH, Docker, API, sous-processus, moteur, .NET ou Git. Il n’ouvre jamais
owner, configuration de déploiement, clé, PDF, SQL, DB, payload, vecteur ou log
brut. Les anciens outils et preuves v1/v2 restent intacts.

Il faut transférer ensemble `export-linux-public-proofs-v3.py` et
`public_projection_base_v2.py`. La base copiée est immuable et son SHA est vérifié
à l’import. Les tests et ce protocole sont facultatifs sur le serveur.

## Sélection explicite et familles séparées

Chaque argument ci-dessous désigne un dossier direct
`/home/maxime/saaia-ingestion-qualification/UUID/evidence/campagne`. Chaque UUID
peut être différent ; aucun owner privé n’est lu pour le deviner.

| Argument de dossier | Fichiers publics exacts | Nombre |
| --- | --- | ---: |
| `--production-gold` | execution-result, publication-verification, backend-binary-identity, bound-runtime-provenance, empty-baseline, text-parser-conformance, sql-text-conformance, tei-attestation, tous `.json` | 8 |
| `--production-recipe` | Les mêmes huit noms | 8 |
| `--production-lifecycle` | lifecycle-result.json, runtime-identity.json | 2 |
| `--production-failed-replacement` | failed-replacement-result.json, runtime-identity.json | 2 |
| `--production-retrieval-gold` | retrieval-facts-result.json, runtime-identity.json, sql-text-conformance.json | 3 |
| `--production-retrieval-recipe` | Les mêmes trois noms | 3 |
| `--production-auto-scanner` | automatic-ingestion-result.json, backend-binary-identity.json, bound-runtime-provenance.json, text-parser-conformance.json, sql-text-conformance.json, tei-attestation.json | 6 |
| `--production-auto-watcher` | Les mêmes six noms | 6 |
| `--production-auto-defaults` | Les mêmes six noms | 6 |

Les neuf rôles sélectionnés donnent **44 projections**, plus le sceau. Chaque
rôle a aussi un argument `--development-...` distinct. Une preuve réellement
Development ne peut pas être sélectionnée sous le rôle Production, et
inversement. Les familles et UUID figurent dans chaque enveloppe et dans le sceau.
Choisir seulement une partie des rôles est permis ; l’inventaire le rend visible
et l’exporteur ne conclut jamais que les autres campagnes ont été exécutées.

Sans `--execute`, les chemins peuvent ne pas encore exister : aucun JSON de
preuve, source documentaire ou secret n’est lu et aucun dossier n’est créé.
L’export effectif exige tous les fichiers de chaque rôle sélectionné. Chaque
résultat principal doit être terminal, exécuté et porter son propre verdict ;
un échec terminal reste un échec. Un échec de préflight sans qualification
observée conserve cette absence, sans fabriquer une signature ou un mode.

Pour une réussite Production, mode Production et éligibilité sont requis ; les
runners qui observent `/ready` doivent avoir l’environnement Production et la
signature vérifiée. Les probes retrieval héritent explicitement du mode et de
l’éligibilité du corpus antérieur : l’exporteur ne transforme pas cet héritage en
nouvelle observation de `/ready` ou nouvelle vérification cryptographique.

## Contenu des projections

Sont conservés les verdicts, compteurs, flags, dates réellement présentes,
identités de cas/documents/révisions/chunks/plans, SHA, image/DLL, provenance liée
et clôture. Les **90 + 16 faits** retrieval restent des enregistrements complets
de contrôle : factId, caseId, page physique, SHA d’annotation et de source,
identités courantes, rang, latence et résultats exacts de recherche→lecture.
`exportedFactCount` expose leur nombre. Les requêtes et textes de faits ne sont
pas copiés. Les négatifs et leurs flags restent distincts des positifs.

Les états NON_EXECUTED de concurrence, course et annulation ne deviennent pas
PASS. Les tentatives de remplacement invalide gardent leur terminal `failed`
attendu, les limites de fraîcheur et la récupération séparée. Les champs de
cleanup du producteur et l’observation zéro job après arrêt sont conservés.

Les diagnostics inconnus et limites en texte libre sont exclus. Les codes ne
peuvent pas contenir de chemin ; seuls les noms symboliques de sources scellées
et le `model_id` Hugging Face relatif `organisation/modèle` peuvent contenir `/`.
Un identifiant modèle absolu, Windows, URL, traversal ou chemin local est refusé.
Les argv, chemins de montage et chemins de cache sont exclus. Les SHA des
artefacts modèle viennent des champs attestés, pas d’une lecture des modèles.
`codeRevisionObservedByReadiness` est conservé quand présent et conforme.
Le sous-objet TEI container conserve uniquement imageId, modelRevision,
modelFileSha256, autoTruncate et ownedContainerVerified, sans copier ses argv.

## Attestation Docker publique facultative

Les anciens fichiers Gold/AUTO publics ne portent pas nécessairement les cinq
CID. `runtime-ownership.json` reste exclu. L’opérateur peut fournir un nouveau
fichier **public déjà curaté**, exactement nommé
`runtime-container-attestation.json` dans le dossier du rôle sélectionné :

```sh
--container-attestation production-gold="$GOLD/evidence/gold-production-v1/runtime-container-attestation.json"
```

Le schéma minimal est construit à partir d’observations existantes, sans aucune
valeur vraie présumée. Ce pseudo-code est un gabarit, pas une preuve exécutée :

```python
public = {
    "schemaVersion": "saaia-linux-public-runtime-attestation.v1",
    "runId": observed_run_uuid,
    "containers": [
        {"role": row["role"], "id": row["id"], "imageId": row["imageId"]}
        for row in observed_five_owned_containers
    ],
}
```

Les cinq rôles doivent être exactement backend, docling, tei, qdrant, postgres,
avec cinq CID distincts de 64 hexadécimaux minuscules et imageId de forme
`sha256:` + 64 hexadécimaux minuscules. Il ne faut copier ni inspection Docker
complète ni owner. Tout champ non listé dans ce schéma d’attestation est refusé.

Champs racine facultatifs, copiés seulement s’ils ont été observés :
`backendAssemblySha256`, `configurationSha256`, `codeRevisionObservedByReadiness`,
`qualificationMode`, `environmentObservedFromContainer`,
`configurationSignatureMode`, `configurationSignatureVerified`, `observedAtUtc`.
Les SHA sont des chaînes de 64 hexadécimaux ; le booléen doit être un booléen et
la date porter son fuseau. L’environnement/mode, s’ils sont présents, doivent
rester cohérents avec la famille sélectionnée.

Champs facultatifs par conteneur : `memoryLimitBytes`, `nanoCpus`, `running`,
`loopbackPorts`, `externalEgressBlockedByDockerInternalNetwork`. Les ports sont
des entiers non privilégiés ; ce résumé n’est pas une nouvelle inspection réseau.
Un champ absent reste absent. Cette attestation opérateur est projetée et scellée,
elle n’est pas vérifiée de nouveau contre Docker par l’exporteur.

## Contrôles moteurs et signature facultatifs

Les arguments `--docling-proof`, `--docling-operator`, `--docling-attestation`,
`--ocr-v1-proof/operator/attestation`, `--ocr-v2-...` et `--ocr-v3-...` sont
optionnels et indépendants. Ils ne créent pas une qualification worker complète.
Leurs chemins sont bornés aux dossiers `docling-offline-12hex-v1` ou
`ocr-e2e-12hex-vN` du UUID concerné, directement ou dans son sous-dossier evidence.
Les noms sont `probe-result.json`, `result-recovered.json` pour OCR v1,
`result.json` pour OCR v2/v3, `operator-result.json`, `runtime-attestation.json`.
Les textes de logs/erreurs et probes incorporés dans l’opérateur sont exclus.

Le contrôle réel de rejet d’une configuration altérée peut être sélectionné tel
quel, sans renommer l’original :

```sh
--signature-control "$SIGNATURE_ROOT/signature-negative-final-v1/signature-negative-result.json"
```

Il exige le schéma observé `saaia-real-production-signature-negative.v1`.
L’export préserve les champs présents executed/passed, privateKeyTransferred,
containerId/imageId, originalConfigurationSha256, tamperedConfigurationSha256,
signatureFileSha256, paidProviderCalls, exitCode, oomKilled, networkMode,
tamperedConfigurationRejected, ownedContainerStopped et
sourceConfigurationUnchanged. Par exemple, un exitCode 139 reste 139, même si le
contrôle de rejet est réussi ; aucune sortie propre n’est inventée. L’absence
de runId ou de date dans l’original reste absente. L’enveloppe annonce alors que
la liaison de namespace vient du répertoire sélectionné et n’a pas été attestée
indépendamment contre le CID par l’exporteur. Le contrôle a sa propre famille,
`independent_signature_control`, et ne devient pas le verdict des ingestions.

Une autre entrée explicitement curatée reste possible sous
`UUID/evidence/campagne/signature-rejection-result.json`, schéma
`saaia-linux-signature-rejection-control.v1`, avec runId et date terminale.

## Exécution sur le serveur, puis copie du seul dossier scellé

Variables de chemins d’exemple à remplacer par les namespaces et campagnes
réellement sélectionnés, sans inventer de résultat :

```sh
python3 -B export-linux-public-proofs-v3.py \
  --production-gold "$GOLD/evidence/gold-production-v1" \
  --production-recipe "$RECIPE/evidence/recipe-production-v1" \
  --production-lifecycle "$GOLD/evidence/lifecycle-production-v1" \
  --production-failed-replacement "$GOLD/evidence/failed-replacement-production-v1" \
  --production-retrieval-gold "$GOLD/evidence/retrieval-production-v1" \
  --production-retrieval-recipe "$RECIPE/evidence/retrieval-production-v1" \
  --production-auto-scanner "$SCANNER/evidence/automatic-scanner-production-v2" \
  --production-auto-watcher "$WATCHER/evidence/automatic-watcher-production-v1" \
  --production-auto-defaults "$DEFAULTS/evidence/automatic-defaults-production-v1" \
  --output "$GOLD/public-export-production-v3"
```

Le premier appel sans `--execute` n’accède pas aux preuves futures. Après gel des
preuves et vérification des chemins, répéter avec `--execute`. Le dossier output
doit être neuf et enfant direct d’un namespace sélectionné. Les sources sont
relues avant la création et avant le sceau ; un changement interdit le scellement.
Un échec peut laisser seulement des projections publiques partielles, sans sceau :
ce dossier n’est pas transférable comme export valide.

Le résultat imprime le SHA256 de `public-export-seal.json`. Le conserver via un
canal indépendant de la copie. Copier ensuite **uniquement le dossier public
scellé**, jamais les répertoires sources, owner, caches, private-observations, DB,
configurations, clés, PDF, payloads, vecteurs ou logs. Le SCP éventuel est une
action opérateur distincte ; l’outil ne lance aucun transport.

Sur la copie locale, avec les mêmes deux fichiers d’outil :

```sh
python -B export-linux-public-proofs-v3.py \
  --verify-directory CHEMIN_ABSOLU_DU_DOSSIER_PUBLIC_COPIE \
  --expected-seal-sha256 SHA256_RECU_SEPAREMENT
```

Le sceau relie les SHA/tailles des originaux aux SHA/tailles des projections. La
vérification exige l’inventaire **exact** des JSON sélectionnés et du sceau, sans
fichier supplémentaire, dossier, symlink ou entrée manquante ; elle vérifie aussi
rôle, UUID, famille et source de chaque enveloppe. Elle prouve l’intégrité de la
copie et ses associations, pas une nouvelle exécution du runtime, une vérification
du PDF, du résultat métier ou de la signature. Elle ne réapplique pas le moteur
de projection à la preuve productrice absente de la copie.

Les sources serveur doivent être figées et les répertoires contrôlés. Sous Linux,
le lecteur emploie O_NOFOLLOW, vérifie le descripteur réellement ouvert via
`/proc/self/fd` avant lecture, refuse les hardlinks et vérifie taille/mtime/inode.
La copie locale Windows doit rester figée et non modifiée par un tiers pendant
vérification : elle n’a pas cette même protection Linux par `/proc`. Les hashes
portent toujours sur les octets effectivement lus. Ce workflow ne prétend pas
résister à un administrateur hostile capable de modifier le programme ou son
canal indépendant de SHA.
