# Barrière de dépendances avant création du backend

Le bootstrap `bootstrap-owned-linux-v5.py` reprend sans modification les octets privés et la configuration signée préparés par v4. Le schéma de préparation reste `saaia-owned-signed-bootstrap.v4`. Les anciennes versions des outils et leurs résultats sont conservés.

Avant de créer le backend, v5 vérifie à nouveau le réseau et les quatre CID/images possédés, puis attend dans une fenêtre globale de 300 secondes : PostgreSQL `pg_isready`, Qdrant HTTP 200 et enveloppe collections, TEI HTTP 200 et identité de modèle non vide, Docling HTTP 200 avec `status=ok` et `upstream=true`. Chaque commande et requête de cette barrière reçoit le temps restant, plafonné à cinq secondes. Les proxies sont désactivés et toute redirection est refusée. La preuve ne contient que dates, identités, statuts et codes : aucun corps de réponse, clé ou contenu documentaire.

La preuve exclusive `evidence/dependency-readiness-pre-backend.json` est écrite avant `docker create` du backend. Une seconde preuve compare cette fin d'observation avec le véritable `State.StartedAt` du backend et lie le SHA de la première preuve. Une reprise d'un backend déjà enregistré vérifie ces preuves et les mêmes CID/images. Un runtime ancien sans preuve préalable ne peut pas recevoir rétroactivement cette qualification. Une tentative de barrière échouée demeure terminale pour ce namespace : aucun écrasement de preuve ni redémarrage automatique d'un conteneur sorti ; utiliser un nouveau namespace après diagnostic. Un timeout ou une redirection déclenche l'arrêt des seuls quatre CID de dépendances encore vérifiés, sans supprimer de volume.

L'opérateur `run-production-sequential-v4.py` exige le registre et l'identifiant d'image réellement observé. Il épingle v5 et les runners déjà gelés ; aucun tag futur n'est inventé. Il vérifie aussi la preuve de chronologie lorsqu'un backend existe déjà. Son nouveau dossier opérateur est `evidence/production-operator-ROLE-v3`, distinct des échecs historiques ; les sorties métier restent `automatic-ROLE-production-v1` dans les nouveaux namespaces.

Commande à exécuter **uniquement par l'opérateur root**, après téléchargement des scripts, contrôle des SHA et construction/inspection de la nouvelle image :

```text
python3 -B CATALOG/run-production-sequential-v4.py \
  --registry CATALOG/production-config-signing-inputs-final-v4.json \
  --backend-image sha256:IDENTIFIANT_REEL_64_HEXA \
  --role gold-production --role recipe-production --role scanner --role watcher --role defaults \
  --execute
```

Sans `--execute`, la sélection est inspectée mais aucun Docker, fichier privé ni HTTP n'est utilisé. Chaque rôle doit avoir son propre namespace, sa configuration signée et ses caches publics déjà préparés. Les sources, corpus, identité binaire Linux et profils attendus doivent être présents selon le protocole du runner gelé. Le préchargement du cas defaults reste strictement avant le backend. Les runners et le `finally` de l'opérateur vérifient/arrêtent les seuls CID exacts possédés.

Preuves hors réseau : le RED valide v2 exécute le vrai v4 avec quatre conteneurs synthétiques Running et un service Docling synthétique 503 : v4 crée quand même le backend, donc l'assertion d'interdiction échoue. La première capture RED v1 échoue au montage d'import du test et n'est pas une preuve causale. V5 passe 23 tests purs ; l'opérateur v4 passe 12 tests purs. Aucun moteur, service, réseau, SSH, signature réelle ni build .NET n'a été lancé dans ces captures.

Ces contrôles attestent une disponibilité préalable instantanée, pas la conversion OCR, le maintien permanent de disponibilité ni une équivalence avec toutes les options de production. Les vraies campagnes restent à exécuter sur la nouvelle image ; les résultats historiques ne sont pas requalifiés.
