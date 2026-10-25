# Validation du client WinUI sur la machine locale

Ces outils préparent un environnement jetable pour le **vrai exécutable WinUI**.
Ils ne contrôlent aucune fenêtre. L'observation visuelle et les interactions
restent effectuées séparément via Computer Use.

Le backend et PostgreSQL16 sont réels. Les documents sont synthétiques, puis
insérés dans le catalogue et les structures canoniques SQL de ce cluster.
TEI et Qdrant sont des adaptateurs HTTP contrôlés : dimension3 constante,
résultats vectoriels vides. La recherche lexicale et la lecture documentaire
utilisent le code de production et SQL. Ce montage ne qualifie pas les embeddings
ni l'ingestion ni la qualité sémantique d'un modèle.

## Préparation

Depuis la racine du dépôt, avec PowerShell7.4 :

```powershell
& ./tools/validation-winui/start-environment.ps1 -OutputDirectory '<nouveau dossier absolu>'
& ./tools/validation-winui/configure-client.ps1 -OutputDirectory '<même dossier>'
python ./tools/validation-winui/seed_documents.py --output '<même dossier>'
```

Appliquer ensuite `runtime/seed-documents.sql` avec le psql16 du manifeste,
sur son **port PostgreSQL dédié**, compte `saaia_winui`, base `postgres`.
Le mot de passe synthétique se trouve uniquement dans
`runtime/fixture-secrets.json` : ne jamais afficher ni versionner ce fichier.
Le script `start-environment.ps1` vérifie réellement `data_directory`.

**Après l'application SQL et avant les parcours documentaires**, exécuter :

```powershell
& ./tools/validation-winui/refresh-catalog.ps1 -OutputDirectory '<même dossier>'
```

Le worker périodique CatalogSnapshot est volontairement désactivé dans cet
environnement. L'insertion SQL des documents ne remplit donc pas à elle seule
les projections de catégories. Ce script appelle le vrai builder de production
via `POST /admin/catalog/snapshot/refresh`, puis exige2 catégories,2 documents,
et1 document dans chaque catégorie. Il n'invente aucune projection à la main.
`GET /catalog/categories` est le point d'entrée du client ;
`GET /catalog/documents?categoryPath=Validation` sélectionne le guide actif,
et `categoryPath=Archive` l'homonyme distinct.

Lancer le client dans un processus dédié avec :

- `SAAIA_CLIENT_DATA_ROOT` = champ `clientDataRoot` du manifeste ;
- `SAAIA_DOCUMENTS_ROOT` = champ `documentsRoot` ;
- `SAAIA_LLM_PROVIDER_MODE=Local` et `SAAIA_LLM_EXTERNAL_POLICY=ProductionLocal` ;
- aucune variable de clé OpenAI, RunPod, Azure ni de profil fournisseur externe.

La racine cliente explicite isole settings, secureDPAPI, provisioning, logs,
diagnostics et exports. La configuration réelle de l'utilisateur est préservée.
Le client Release x64 doit avoir été construit séparément sans hooks de test.

## Ports et scénarios

| Service | Port par défaut | Nature |
| --- | --- | --- |
| Backend initial |5127| Backend de production, workers désactivés |
| LLM / TEI / Qdrant de contrôle |18087| Fixture HTTP synthétique |
| Qwen3-4B existant |18088| Vrai petit modèle, poids existants en lecture seule |
| Worker avancé |5137| Vrai worker et même cluster SQL isolé |
| Fournisseur du worker avancé |18089| Fixture interne contrôlée |
| Proxy de fautes stockage |5147| Proxy loopback vers5127 |

`start-local-model.ps1` démarre uniquement le poids Qwen3-4B déjà installé,
sans téléchargement, sur un processus dédié Hidden. Le client peut ensuite
choisir ce port et l'alias `Qwen3-4B-Instruct-2507` dans ses paramètres.

`start-advanced-worker.ps1` active un vrai worker avec profil `customer-server`
**interne** pointant sur loopback. La fixture lui renvoie un plan ORION et un
résultat contrôlé `insufficient_documentation`. `test-advanced-worker.ps1`
vérifie via API les transitions queued→running→succeeded. Ce test n'est pas
un test de l'interface ; aucune transition terminale n'est injectée en SQL.

Le fichier `runtime/fixture-control.json` est relu à chaque appel LLM :

```json
{"mode":"success","delaySeconds":0,"chunkDelaySeconds":0.03}
```

`mode=error` renvoie503 ; `delaySeconds` diffère la réponse (maximum120s).
`mode=plain` ignore les outils et diffuse le texte de `response`.
`toolName` / `toolArguments` permettent un scénario de routage déterministe.
La fixture n'est jamais présentée comme une intelligence artificielle.

Pour les erreurs de sauvegarde initiale et l'annulation pendant cette sauvegarde,
`start-storage-proxy.ps1` crée le proxy5147. Pointer le backend client sur5147.
Le contrôle `runtime/storage-proxy-control.json` ne vise que les POST chat avec
`role=user` :

```json
{"failUserMessagesRemaining":1,"delayUserMessagesSeconds":0}
```

La valeur1 provoque un unique500 puis est décrémentée atomiquement. Pour différer
la sauvegarde de5s, utiliser0 et5. Remettre0/0 après le scénario. Ces fautes ne
modifient aucun code produit ni schéma SQL.

Une nouvelle instance peut être lancée sans arrêter la précédente :

```powershell
& ./tools/validation-winui/start-storage-proxy.ps1 -OutputDirectory '<même dossier>' -Port 5157 -InstanceName 'ui-faults-v2'
```

Ses fichiers contrôle/événements portent le suffixe `-ui-faults-v2`.
Le source courant accepte `delayUserMessagesSeconds` entre0 et120,
`failGetMessagesRemaining` pour des500 limités aux GET messages et
`failReadyRemaining` pour des503 limités à `/ready`. Les compteurs sont
décrémentés atomiquement. Les valeurs absentes ou0 transmettent normalement.
L'ancienne instance5147 du25octobre conserve la version chargée à son démarrage,
dont le délai maximal est30s.

Un délai avant transmission peut produire un commit tardif : après un timeout
client de30s et un délai proxy de60s, le message peut encore être écrit à60s.
L'observation SQL finale doit donc attendre la fin du délai armé avant de
conclure sur un retry ou un doublon. Ce cas représente une sauvegarde ambiguë,
pas simplement un refus de sauvegarde immédiat.

## Preuves et arrêt

`inspect-chat-store.ps1` observe les sessions et messages persistés du seul
`userId` synthétique déclaré dans `client-isolation.json`. Il effectue uniquement
des GET, sans modifier le client ni la base :

```powershell
& ./tools/validation-winui/inspect-chat-store.ps1 -OutputDirectory '<même dossier>' -Label 'before-double-enter'
& ./tools/validation-winui/inspect-chat-store.ps1 -OutputDirectory '<même dossier>' -Label 'after-double-enter' -BaselinePath '<snapshot précédent>'
```

Les snapshots horodatés contiennent identifiants, rôles, hash SHA256 des textes
UTF8 exacts, nombres de caractères et retours à la ligne, compteurs et état
avancé. Aucun texte de message, titre ou clé n'est sauvegardé. La comparaison
montre les IDs ajoutés/supprimés et les messages dont contenu ou métadonnées ont
changé. Des hash identiques peuvent être des répétitions volontaires : ce sont
des candidats à examiner avec le scénario UI, pas un verdict de doublon.
Le script signale les limites de pagination/API et l'observation séquentielle
non atomique ; capturer à l'arrêt du tour pour les comparaisons.

`verify-persistence-deadline.ps1` relie un tour ayant expiré au POST retardé et
au message durable : traces client, événement wire, snapshot après délai puis
lecture SQL dans une transaction READ ONLY du cluster vérifié. L'absence de
retry automatique dans ce scénario n'est pas une garantie d'idempotence d'un
renvoi manuel futur.

`inspect-support-bundle.ps1` inspecte le zip obtenu par l'UI sans l'extraire :
manifest, santé des collecteurs, sondes, contrats des sidecars, clés synthétiques
plaintext/DPAPI et textes de corpus/chat connus recherchés uniquement en mémoire.
Les sidecars de gouvernance originaux hashent le texte UTF8 sans BOM ; les
copies exportées expurgées hashent leurs octets et conservent la référence
d'origine dans `.source`. La preuve compacte ne publie aucune valeur sensible.

`seed-source-card-session.ps1` crée par l'API une nouvelle discussion du seul
utilisateur synthétique, avec assistant explicitement marqué comme fixture et
une carte de source canonique exacte en page2. Il ne lance pas de modèle et ne
valide pas le rendu UI/PDF ; l'opérateur doit charger cette discussion et cliquer
la carte dans l'application réelle.

`-Scenario HashMismatch` exige un refus en l'absence de fichier possédant le
SHA256 demandé. `-Scenario MissingPath` conserve un SHA256 canonique valide :
la récupération d'un fichier de même basename et de **version exactement
vérifiée par SHA256**, ou un refus sûr, sont tous deux des résultats admissibles.
L'archive homonyme possède un autre hash et ne doit pas être ouverte. L'oracle
initial de ce dernier scénario exigeait à tort un refus systématique ; ses
messages/preuves historiques sont conservés, sans les convertir en bug produit.

`compare-user-profile.ps1` compare uniquement les périmètres présents dans la
baseline et produit compteurs/IDs hachés sans chemins ni contenu privés. Le
lanceur commun `tools/test-project.ps1` isole aussi les suites Client/Core ;
`test-client-profile-isolation.ps1` vérifie réellement les modes owned/explicite,
le rejet d'une racine relative et la non-écriture du profil normal durant ces
tests. Les inventaires privés utilisés par ce dernier restent exclus de Git.

`list-versionable-evidence.ps1` établit une sélection explicite de candidats avec
empreintes, sans aucun staging. Régénérer cette liste après le gel final et
faire confirmer les captures par l'opérateur ; elle n'est pas une approbation
automatique de tous les visuels ou fichiers.

Ne versionner **aucun** fichier de `runtime/`, cluster, clé synthétique, fichier
secure, capture brute ou binaire. Les manifestes publics, captures visuelles
explicitement vérifiées, réponses API synthétiques, événements sans corps et
rapports constituent les preuves sélectionnées.

Les logs HTTP ne conservent pas les corps ou clés ; ils contiennent des hash,
identifiants et noms d'outils. Les métriques de tokens de la fixture sont des
valeurs synthétiques et ne sont pas des mesures de performance d'un modèle.

Avant toute interruption, produire un plan en lecture seule :

```powershell
& ./tools/validation-winui/stop-environment.ps1 -OutputDirectory '<même dossier>' -WhatIf -PlanOutputPath '<nouvelle preuve JSON>'
```

Le plan prévalide tous les PID, heures, exécutables et ports, puis l'identité
PostgreSQL dans `postmaster.pid` et le répertoire dédié. Les proxies5157,5158
et5167 sont découverts dans le manifeste owned ; aucun service n'est arrêté
par `-WhatIf`. Les commandes d'arrêt ne sont atteintes qu'après ce retour.

Après fermeture du client, fin des jobs et collecte des preuves, uniquement
lorsque l'opérateur a décidé l'arrêt :

```powershell
& ./tools/validation-winui/stop-environment.ps1 -OutputDirectory '<même dossier>'
```

Les identités sont revérifiées juste avant chaque action. L'arrêt termine le
seul enfant dotnet/llama vérifié, puis attend la sortie du wrapper afin de
préserver ses logs tamponnés. Il ne tue jamais un arbre ni un wrapper qui ne
sort pas : ce dernier est conservé pour diagnostic. Vérifier après arrêt
l'existence, les tailles et empreintes des6fichiers stdout/stderr attendus.
PostgreSQL est arrêté en dernier par `pg_ctl` sur son seul répertoire dédié,
puis les PID/enfants et tous les ports sont contrôlés. Le port5432 est exclu
du plan et son observation avant/après doit rester identique. Les données
isolées sont conservées ; aucune suppression récursive n'est effectuée.

## Erreurs de polling avancé contrôlées

`start-advanced-poll-proxy.ps1` démarre un proxy privé5167 vers le vrai worker
5137 et prépare le profil isolé `w24`, sans lancer de client ou de modèle.
Le contrôle `runtime/advanced-poll-control-w24.json` est initialement désarmé :

```json
{"targetJobId":"","mode":"passthrough","remaining":0}
```

Après création du job par la vraie UI, l'opérateur peut armer son identifiant
avec `http503`, `regressive_revision` ou `session_mismatch`, et un compteur
borné `remaining`. Seuls les GET de ce job et de l'utilisateur synthétique
primaire sont modifiés. Les POST et les GET messages restent inchangés ; le
proxy ne change aucun état SQL, ne crée aucun job et ne touche pas au contrôle
du provider. Le contrôle du délai de la fixture avancée reste séparé.

`verify-polling-faults.ps1` relie injections, corrélation HTTP, rejets client,
snapshot avant/après et état SQL READ ONLY. Il distingue les snapshots rejetés
du vrai résultat terminal et constate les hash inchangés des autres chats.
La preuve ne prétend pas disposer d'un audit historique de chaque instruction
SQL : l'exclusion d'une réponse invalide avant `onSnapshot` repose aussi sur
le contrôle de flux du poller inspecté et ses tests.
