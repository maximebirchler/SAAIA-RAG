# Lecture complète d'une publication sans agrégat global PostgreSQL

L'ancien `worker-publication.sql` construit le corpus dans de gros `jsonb_agg`.
Après quatre publications Source34 terminées, cette lecture a tué le processus PG
du conteneur limité à 1 Gio. Cette panne de lecture ne prouve pas un échec OCR ou
une perte des publications. Le nouvel export ne réingère rien.

## Récupération PG seul, explicitement autorisée

Exécuter sous Linux, avec les deux fichiers Python/SQL adjacents et le module
existant `../validation-ingestion/linux_owned_runtime.py`. Les arguments sont les
vrais chemins/hashes du runtime ; aucune valeur future n'est substituée.

```text
python3 -B tools/validation-planning/export_worker_publication.py \
  --owner /absolute/owned-runtime-owner.json \
  --expected-owner-sha256 REAL_OWNER_SHA256 \
  --output /home/maxime/saaia-ingestion-qualification/REAL_RUN/evidence/NEW_DIRECTORY \
  --execute
```

Sans `--execute`, aucun Docker/SQL ne s'exécute. Avec ce flag, les cinq identités,
images, labels, montages et réseaux sont admis par `OwnedDocker`, sans modifier ce
contrat. **Seul** leur PostgreSQL doit être vivant pour cette récupération ; les
quatre autres conteneurs doivent être arrêtés. Le lecteur ne démarre, n'arrête ou
ne supprime aucun conteneur. Le runner normal utilise le même lecteur avec son
admission habituelle stricte : ses cinq services doivent être vivants.

## Lecture et conservation

Une connexion psql tient une seule transaction `REPEATABLE READ READ ONLY` pendant
toutes les requêtes. `FETCH_COUNT=1` évite que psql, lui-même dans le cgroup PG,
tamponne un résultat complet. `work_mem=8MB`, parallélisme0, timeout par requête300s.
`application_name=saaia_worker_publication_stream` et le PID PostgreSQL réel sont
capturés pour le contrôle indépendant de fermeture par root.

Les quatre collections globales restent globales : aucun filtre sur les seules
révisions courantes ne masque les lignes étrangères/historiques. Tous les champs,
nulls, objets imbriqués et tableaux de l'ancien contrat SQL sont préservés. Les
profils/runs conservent leur `to_jsonb` intégral ; les métadonnées RAW emploient les
colonnes explicites du schéma réel, vérifié avant lecture pour refuser une dérive.
Les lexèmes JSON documentaires sont copiés, sans passage par `json.dumps`.

Les BYTEA bundle/catalogue/pages RAW sont lus par morceaux de 58 368 octets
(57×1024). L'alignement conserve exactement le base64 PostgreSQL : lignes de76
caractères, LF après chaque groupe complet de57octets, y compris le dernier si
la longueur binaire positive est un multiple de57. Une dernière ligne avec
padding (par exemple56octets donnant76caractères) n'a pas de LF final. Les
frontières0/1/56/57/58/114/58368 ont été observées sur PostgreSQL16 ; la première
capture réelle a refusé proprement cette différence sur une page RAW, puis la
garde locale a été corrigée. Les octets sont vérifiés par
décodage de chaque petit morceau, comptés et hashés ; le JSON interne des blobs
n'est ni décompressé ni réencodé par ce lecteur.

Le client garde une ligne et un morceau, puis assemble le contrat complet sur
disque. L'index des blobs est lui-même un JSONL, pas une liste croissante en RAM.
Limites : une ligne16Mio, lecture SQL totale2Gio,64documents,500000lignes par
collection, prévol TEXT1Mio par cellule, stockage JSONB observé2Mio par cellule.
Une limite dépassée **refuse** l'export ; aucune troncature ou sélection partielle
n'est présentée comme complète. Prévoir au moins4Gio libres pour les spools et le
fichier final. Le prévol JSONB mesure le stockage éventuellement compressé : il
**ne prouve pas** une borne universelle de mémoire serveur pour une cellule JSONB
géante. La qualification réelle reste liée aux cellules observées et aux sondes
du PostgreSQL1Gio ; ce lecteur supprime l'amplification globale des agrégats.

## Sorties et limites de la preuve

- `publication.private.json` : contrat SQL complet, publié seulement après
  `ROLLBACK` et sortie0 de psql, puis réadmission des identités/états Docker.
- `capture-receipt.json` : propriétaire/snapshot/PID, SHA/tailles des fichiers,
  compteurs complets, index des blobs et maximum de ligne réellement observé.
- `queries.private.sql`, `psql-stderr.private.log`, spools JSONL : diagnostic fermé.
- Sur échec : fichier `.partial` et `capture-failure.json`, jamais de reçu PASS.

La sortie0 du client ayant envoyé `ROLLBACK` est distincte d'un contrôle serveur
indépendant. Root peut constater l'absence du PID/application dans `pg_stat_activity`.
Sur timeout/annulation, le lecteur termine uniquement son propre client Docker,
préserve l'erreur initiale et ne prétend pas avoir observé la fermeture serveur.

L'ancien gel Source34 et son résultat `failed` ne sont pas modifiés. Le nouveau
reçu atteste la capture, pas la validation globale du corpus. Il ne fabrique pas
les champs d'exécution du worker (manifestes, chemins originaux, empreintes de
modèle/assemblage, baseline et terminal jobs) : ils doivent être reliés aux preuves
fermées existantes par la procédure de récupération. La comparaison Moulinex et
les validations worker/vecteurs restent nécessaires.

Comme l'ancien contrat, ce JSON n'exporte ni les PNG de
`document_revision_visual_assets`, ni l'artefact `input_geometry`, ni les vecteurs.
Leur intégrité relève des captures annexes. Les anciens évaluateurs peuvent encore
charger le JSON entier côté hôte ; ce coût distinct n'est pas revendiqué borné ici.

Tests hors ligne :

```text
python -B -m unittest discover -s tools/validation-planning -p test_worker_publication_stream.py -v
```
