# Contre-revue du harnais Linux possédé

État : **revue de sources en lecture seule, aucun nouveau défaut matériel identifié dans le snapshot final relu**. Ce verdict ne constitue aucune qualification de pile réelle, aucun succès d’ingestion Linux ni approbation du planning. Aucun service, API, Docker, test ou source du harnais n’a été exécuté/modifié par cette contre-revue.

## Ancrage

| Fichier | SHA256 relu |
| --- | --- |
| `tools/validation-ingestion/linux_owned_runtime.py` | `013df6c287e0249002cb80154622b17a164f882fb37a13c47f863173f8fcb54d` |
| `tools/validation-ingestion/invoke-linux-ingestion.py` | `0e60f2f820d81f94bde18910cd518d7e55438f65e7bd897130b9583b555cd23e` |
| `tools/validation-ingestion/embedding_attestation.py` | `57ced0573825fcc2a17f019930123e9864256f8b40c90867db35401d8580f294` |

Ces trois empreintes correspondent au manifeste `tests/linux-admin-headers-green-v1/manifest.json`. Cette suite pure, exécutée par son propriétaire, compte **92 tests, zéro échec/erreur/skip et `sourceChangedDuringRun=false`**. Je l’ai relue sans la relancer. Les premières empreintes annoncées pendant l’écriture ne sont pas utilisées comme ancrage final.

## Défauts transmis puis fermés dans les sources

1. Le premier contrôle des poids ne reliait qu’un fichier fourni en argument à un SHA attendu. Le snapshot final raccorde **argv TEI → cache explicite → snapshot de révision → bind réellement inspecté → chaque artefact**. Un sous-montage plus spécifique qui masquerait un fichier fait échouer le contrôle. Le fichier témoin doit être exactement celui du snapshot sélectionné ; l’inventaire doit couvrir tous ses fichiers, sans symlink, avec hashes avant/après. `tokenizer_config.json` n’est pas artificiellement exigé lorsqu’il n’existe pas : le set réellement provisionné est figé, avec config/tokenizer/pooling et poids requis.
2. Les mounts étaient initialement contrôlés seulement lexicalement. `inspect_all` invoque maintenant la garde filesystem pour chaque source de bind. Les demandes de devices/GPU sont également refusées pour cette qualification CPU.
3. Le contrôle de fin d’abord nommé « ports libérés » regardait seulement Docker et ne conditionnait pas le verdict. Il est maintenant nommé **`dockerPublishedPortBindingsAbsent`** et requis avec `zeroActiveJobs` et `ownedContainersStopped`. Un faux/manquant ne donne pas PASS. L’absence de listener hôte reste une observation distincte.
4. La conformité des révisions ne reposait que sur le JSON fourni. Le snapshot compare aussi les valeurs réellement liées exposées par `/ready`. Qdrant est raccordé à l’image inspectée en distinguant son **image config ID** et un **RepoDigest** ; aucun de ces types n’est arbitrairement assimilé à l’autre.

Ces constats étaient établis sur des sources intermédiaires ; ils ne sont pas présentés comme des défauts encore ouverts dans le snapshot final. Les tests purs et les futures captures réelles ont des portées distinctes.

## Garde-fous relus

L’ownership demande le run UUID, la racine exacte sous le répertoire de qualification dédié, le projet dérivé, cinq rôles/IDs complets d’image et conteneur, labels et endpoints loopback distincts. Les ports usuels PostgreSQL, chemins hors scope, volumes partagés et accès privilégié/host PID sont refusés. Chaque interaction API/SQL recontrôle les conteneurs ; les lectures SQL sont en transaction read-only. L’arrêt valide d’abord l’ensemble, puis utilise les seuls IDs possédés, sans suppression de volumes/images ni `compose down` global.

Le plan sans `--execute` ne lit ni clé ni poids et ne lance ni Docker ni HTTP. L’exécution est limitée à Linux, avec fichiers/chemins possédés, sources PDF hashées, sortie neuve et base vide. Les requêtes HTTP restent locales, sans proxy ni redirect autorisé. La clé synthétique n’est ajoutée qu’aux requêtes backend ; les headers API et admin sont nécessaires au parcours bootstrap possédé. Le correctif suit un HTTP401 réellement observé par l’opérateur ; la suite pure ne prétend pas être son témoin HTTP200 réel.

Les capacités LLM avancées et automatiques sont explicitement désactivées et les credentials fournisseur connus absents de l’environnement backend. Les exceptions publiques ne restituent pas corps HTTP, texte documentaire, clé ni SQL ; les observations brutes vont dans le dossier privé. Le SHA de la DLL réellement présente dans le conteneur est enregistré, sans le remplacer par celui d’un build Windows.

## Limites à conserver dans les conclusions

- **Production** exige environnement Production et signature vérifiée. Le mode `development_diagnostic` reste distinct : il peut réussir son diagnostic mais `deploymentQualificationPassed` demeure faux. Les valeurs `passed` et qualification sont toutes deux exposées.
- Réseau **internal** par défaut ; l’option explicite `dedicated_bridge` n’atteste pas une absence d’egress. Le flag observé le précise. Ni l’ownership ni un nom de réseau ne constituent une capture réseau.
- L’attestation TEI garde honnêtement `model_sha=null` s’il s’agit de la réponse native. L’identité complète dépend alors des observations séparées d’image, commande, montage et poids. Le probe vectoriel seul atteste info native et classement synthétique ; il demande encore le runner pour cette provenance.
- Le mapping et les hashes du snapshot ne sont **pas un traçage OS des fichiers ouverts par le moteur** ; `kernelFileOpenTracingPerformed=false` est correct. Les hashes avant/après ne remplacent pas une mesure d’invariance continue de chaque octet en RAM.
- L’arrêt confirme les conteneurs possédés et leurs bindings Docker, pas tous les listeners du serveur. Les ressources conservées volontairement (données, images, réseau) ne sont pas faussement annoncées comme supprimées.
- Une erreur de prévalidation avant acquisition du scope d’exécution ne déclenche pas de mutation de cleanup. Le bootstrap/opérateur qui a créé la pile doit garder son propre arrêt possédé dans ce cas ; le runner ne crée aucune ressource.
- Le test de publication/search/read vérifie les cas/faits sélectionnés et les générations/identités correspondantes. Il ne qualifie ni rappel exhaustif, ni tous moteurs/options historiques, ni planning multisource/LLM.

Les captures réelles de préflight/worker/arrêt restent à relire séparément lorsqu’elles sont terminées. Aucune réussite de runtime en cours n’est déduite de cette revue.
