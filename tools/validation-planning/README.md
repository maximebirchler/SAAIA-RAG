# Validation séparée du planning de repas

Ce harnais traite la demande historique complète : cinq jours, quatre créneaux et vingt cellules concrètes et sourcées. La question demande des sources utiles non dupliquées, sans exiger vingt recettes différentes. Le profil `five-day-20` déclare donc explicitement `selectionPolicy=structured_layout` : une recette documentée peut revenir dans plusieurs cellules adaptées, sous son vrai nom et avec les mêmes preuves dédupliquées. Le reçu indique le nombre réel de recettes distinctes et les coordonnées des répétitions ; il ne les cache pas et ne transforme pas une grille répétée en approbation sémantique. Une cellule vide, un corps non lu, une recette inventée ou une citation incorrecte reste refusé. La pertinence de chaque repas et des répétitions reste à vérifier.

Il ne réemploie pas le contrat du microtest factuel, ses huit pages ou ses quatre appels. Le diagnostic `one-day-4` demande explicitement quatre propositions distinctes et conserve `selectionPolicy=distinct_structured_layout` ; il ne valide jamais le planning final. Ces deux cas exigent une policy présente et cohérente dans le profil, le handoff, le prévol et le dernier message réellement envoyé au modèle. Les anciennes captures/profils sans cette déclaration ne sont pas requalifiés silencieusement : un nouvel essai doit figer son profil et son prévol.

Le profil décrit Responses, `gpt-5.6-luna`, un raisonnement `low`, les outils natifs et les lectures RAW. Ses bornes sont explicites : jusqu'à 64 appels et 128 outils, Writer 8192 tokens, documentaire 65536 caractères, historique 131072. Ces maxima ne sont pas des objectifs de consommation : la réserve monétaire durable peut arrêter le travail avant.

La topologie de ce premier essai est l'agent natif monolithique : recherche initiale puis Writer capable de réclamer d'autres outils. L'explorateur par étapes, le workspace et le Critic sont explicitement désactivés. Ce choix décrit l'essai ; il ne constitue pas une conclusion sur la meilleure architecture pour les cas complexes.

L'enveloppe autorisée reste **0,50 USD cumulés**, dont une baseline déjà consommée de **0,00678686 USD**. Toutes les tentatives utilisent le même fichier `usage.jsonl` et ses réservations durables. Le profil conserve séparément le tarif de base et le barème prudent de contexte long utilisé pour arrêter la dépense ; le coût calculé à ce barème est une borne locale, pas une facture fournisseur.

Le runtime doit être préparé séparément dans `CampaignRoot/attempt/owned-environment`, via les outils possédés existants et une publication de PDF complets. Le manifeste `saaia-planning-complete-corpus.v1` doit indiquer `completeDocuments=true` et, pour chaque document : `docPath`, `docId`, `revisionId`, `sourceSha256`, `canonicalHash`, `pageCount`, `retrievalChunkCount`, `sourceAnchorCount`, `completeCanonicalBundle=true`, `physicalPdfPath`. Le runner compare ces identités et comptes au SQL réel, vérifie les PDF immuables et exige zéro ancien job. Le mode exact de projection, la qualité/OCR et les projections SQL disponibles doivent aussi être déclarés par le producteur du manifeste ; un replay ne constitue pas une nouvelle preuve d'OCR.

Le profil commence `PROPOSED`. L'opérateur principal le revoit et le fige avant tout essai. Le premier passage `-Preflight` utilise uniquement un faux fournisseur loopback : il enregistre le planner puis la première enveloppe Writer réelle, avec les contrats d'outils, et renvoie volontairement une erreur avant toute réponse de modèle. `verify_planning_inputs.py` produit un reçu lié au profil exact, aux SHA reçus, aux traces et aux sondes. Ce prévol ne prouve pas la qualité d'un vrai modèle ni que tous les futurs tours auront la même taille.

Le prévol emploie le profil technique `customer-server/internal`, nécessaire au socket HTTP loopback sans clé. Le produit n'émet `reasoning.effort` que pour `openai-dev` : cette option est donc absente du corps local malgré la configuration `low`. La première capture applicative payée devra confirmer `reasoning.effort=low`, le modèle et l'URI HTTPS OpenAI. Cette différence est déclarée ; aucun champ n'est ajouté artificiellement à la capture locale.

Le passage `-Execute` exige le profil `AUTHORIZED`, les deux autorisations explicites, le plafond 0,50, un reçu de prévol réussi du même profil et un chemin DPAPI fourni par l'opérateur. Les clés ne sont jamais inscrites dans le profil, les rapports ou les captures. Il faut un nouvel environnement préparé par tentative ; le runner annule ses jobs restants, attend la clôture des réservations et arrête uniquement ses processus vérifiés. Il ne retente jamais automatiquement.

Le résultat reste `TESTE_NON_APPROUVE` jusqu'à la revue des vingt cellules et de leurs preuves. Une réponse `answered`, une insuffisance sûre ou une sortie bornée ne constituent pas à elles seules une acceptation fonctionnelle et sémantique.

## Capacités du profil actuel pour les PDF illustrés

Les images PNG conservées sont bornées à 1 024 ressources et 384 Mio par document,
avec 16 Mio et 16 millions de pixels par PNG. Ce budget concerne les images
générées, pas la taille du PDF ou son texte. Chaque provenance est comptée dans
le volume publié ; partager le décodage ne fusionne aucune identité. Ces bornes
de qualification restent à éprouver par une ingestion réelle et ne constituent
pas une capacité universelle. Une capacité configurable et une publication
progressive des images restent nécessaires pour des documents plus volumineux.

Pour un nouvel owner Linux, `--backend-memory-gib 3` demande explicitement
2 CPU, 3 Gio de mémoire et aucun swap supplémentaire. Le défaut reste 2 Gio ;
Docling reste à 3 CPU et 7 Gio. Les ressources sont vérifiées dans les inspections
Docker et liées à l'owner et à son origine. La signature de la configuration
Production ne prouve pas, à elle seule, les limites Docker effectivement appliquées.

Le profil optionnel `--docling-memory-profile cpu7g-bounded-v1` conserve les
3 CPU et 7 Gio de Docling, les mêmes moteurs, images, résolution, pages analysées
et réglages de qualité. Il limite la file interne du pipeline à deux pages et
les batchs OCR/layout/tableaux à une page, désactive le préchauffage aux options
par défaut et limite le cache de convertisseurs à une configuration. Il évite
ainsi du travail simultané et un éventuel jeu de modèles inutilisé. Il ne promet
pas que la mémoire totale du document est bornée à deux pages : les images et
structures utiles restent conservées jusqu'à l'assemblage du document.

Ce choix doit être explicite et identique dans les trois phases Linux
`prepare`, `dependencies`, `backend` et dans le runner d'ingestion. La policy est
liée à l'owner et à son origine ; les six variables, le moteur local, le worker
unique, le partage des modèles, les threads et les limites Docker sont contrôlés
avant l'admission, puis avant chaque requête HTTP du runner. Une variable
dupliquée ou un fichier de configuration Docling supplémentaire refuse ce profil.
Le nettoyage conserve la vérification d'identité d'origine et reste possible
après une dérive des nouveaux réglages. Le défaut `legacy` ne change aucun argv
et n'ajoute aucune clé aux anciens owners. Ces observations d'environnement ne
remplacent ni l'attestation des options effectives du pipeline, ni une mesure
mémoire réelle, ni la comparaison indépendante du résultat avec les PDF.

## Publication neuve depuis les originaux

L'acceptation actuelle exige une réingestion réelle des quatre PDF originaux,
après correction produit. `CorpusProjection` et les paquets natifs précédents
restent des diagnostics de projection/publication ; ils ne satisfont pas cette
exigence. Les nouvelles révisions, ancres et identifiants de chunks proviennent
du SQL publié par le worker, sans formule de reconstruction du paquet natif.

`new-owned-real-ingestion.ps1` prépare une voie Windows distincte et désactivée
par défaut. Sans `-Execute`, il ne lit que les sources/profils publics et
n'accède à aucun processus, credential, HTTP ou SQL. Les dépendances doivent
avoir été lancées séparément et la DLL doit correspondre au SHA revu. À
l'exécution, le script vérifie ownership/readiness/identité des moteurs, démarre
un cluster vide, copie les quatre originaux puis appelle `/ingestion/scan`.
Après quatre vrais jobs terminés et vérification des artefacts, il conserve
l'owner actif pour le parcours suivant. Un échec est conservé et déclenche
l'annulation puis l'arrêt possédé ; aucun état arrêté n'est transformé en ready.

`saaia-planning-worker-corpus-observation.v1` contient le readback SQL exact :
jobs, versions, plans vectoriels, bundle gzip, ancres originales, chunks,
unités, sections, cartes, profils et projections RAW. Des fichiers privés
hashés rattachent également baseline vide, réponse du scan, jobs terminaux,
sondes et observations des dépendances. `verify_worker_corpus.py` contrôle les
identités/checksums/pages et les points Qdrant réels. Il ne juge pas la bonne
association d'une recette avec ses ingrédients : cette revue indépendante du
PDF reste nécessaire avant un appel payant.

Le runtime Windows de diagnostic disponible utilise d'autres versions de
Docling que Linux et ne peut pas qualifier les réparations OCR régionales :
leur isolation de processus requiert Linux. Aucun profil Windows ne doit être
présenté comme une exécution équivalente au profil Linux. La réutilisation
d'une capture neuve dans un deuxième owner doit conserver les octets, GUID,
révisions et preuves de provenance ; ce clone qualifie la reproduction de la
publication et ne constitue jamais une nouvelle ingestion.

La voie Linux préparée est `new_owned_linux_ingestion.py` : elle réutilise le
bootstrap Production signé du profil CPU28 épinglé, ses quatre images de
dépendances et une copie hashée de ses caches publics. Le nouvel UUID possède
sa DB, collection, réseau, ports et ses cinq nouveaux conteneurs. Seule l'image
backend change après le correctif ; l'ancien owner reste arrêté et inchangé.
Les phases `prepare`, `dependencies` et `backend` sont séparées, avec signature
PC de la nouvelle configuration avant le backend. Aucun appel Docker ni accès
aux clés/configurations privées n'est possible sans `--execute`.

`invoke_owned_linux_planning_ingestion.py` admet ensuite les quatre originaux
hashés, un seul scan API et de vrais jobs, avec le profil TEI natif attesté.
Le SQL compagnon est commun aux captures Linux et Windows. Les chemins serveur
des preuves restent dans les JSON ; une carte de transfert explicite relie
leurs octets aux copies locales destinées à l'audit. Aucun fallback par nom
de fichier ni modification silencieuse des captures ne remplace ce lien.

## Clone documentaire et dépendances réelles pour le planning

`CloneExport/export_owned_linux_corpus.py` exporte la publication neuve sous un
snapshot PostgreSQL tenu ouvert. Le paquet contient un dump de trente tables et
les trente-deux readbacks JSONL exacts. `restore-owned-windows-corpus.ps1` admet
uniquement un environnement frais créé avec `start-environment.ps1
-PlanningCloneOnly -NoBuild`. Ce mode conserve ingestion/LLM désactivés et
désactive explicitement le GC vectoriel ; les valeurs historiques par défaut
ne changent pas. Le clone conserve l'authentification synthétique locale et
recopie les quatre PDF originaux hashés dans son propre DocumentsRoot.

Le restore maintient sa transaction ouverte jusqu'à la comparaison des
trente-deux tables, toutes colonnes comprises. Les plans vectoriels sont
temporairement non scellés pour insérer leurs points, puis scellés avec leurs
valeurs originales. Les triggers restent actifs, leurs endpoints ne sont pas
réécrits. Les colonnes tsvector générées sont comparées aussi ; une différence
de locale entre Linux et Windows entraîne un refus. Seul le délimiteur de
transport JSONL CRLF produit par psql Windows est ramené au LF du paquet, sans
changer les chaînes JSON, y compris leurs CR/LF échappés. Un écart provoque
ROLLBACK ; aucun nouvel essai n'écrase les preuves.

`planning_rag_runtime.py register` lie le paquet, le manifeste de réingestion,
la carte de transfert, l'owner Linux, son profil TEI et la preuve de tunnels.
Il ne fait aucun appel réseau. Les forwards possédés sont explicitement
22531→22523 pour TEI et 22532→22524 pour Qdrant. Le profil rebascule seulement
les URLs de lecture ; collection, modèle, images et révisions restent ceux de
la nouvelle ingestion. Le générateur du profil planning exige ce profil Rag
pour tout manifeste `origin=real_ingestion_worker`.

Avant toute lecture de la clé fournisseur, `PlanningRagRuntime.ps1` revérifie
PID, exécutable, heure de lancement, commande hashée et listeners SSH. Puis la
commande d'attestation compare cinq tables SQL critiques au clone exact,
interroge réellement le `/info` TEI natif, la collection Qdrant 768 dimensions,
et tous les liens point→chunk→plan avec des vecteurs finis. Le worker planning
utilise ces dépendances réelles ; le fixture HTTP d'amorçage reste inutilisé
par son Rag. Le faux fournisseur du prévol remplace seulement le LLM.

Les gardes de proxy/redirection concernent ces sondages possédés de
dépendances. Elles ne prétendent pas décrire une interdiction générale des
proxys ou redirections dans tout le produit.

Ordre opérateur, après acceptation indépendante des nouvelles publications et
transfert contrôlé du paquet source :

1. Restaurer chaque owner Windows frais avec `restore-owned-windows-corpus.ps1
   -PreparedEnvironmentDirectory ... -PacketDirectory ... -MetadataSha256 ...
   -CorpusManifestPath ... -CorpusManifestSha256 ... -PythonExecutable ...
   -Execute`. Le succès attendu est `all32TablesByteExact=true`.
2. Enregistrer les dépendances avec `planning_rag_runtime.py register
   --manifest ... --capture-map ... --packet ... --metadata-sha256 ...
   --source-owner ... --tunnel ... --output ...`. Fournir ce résultat à
   `new-planning-campaign-profile.ps1 -RealRagProfilePath ...`. L'opérateur
   principal revoit et autorise le profil ; aucun de ces outils n'autorise une
   dépense automatiquement.
3. Exécuter le runner séparé en `-Preflight`, puis vérifier les vrais octets
   Planner/Writer avec `verify_planning_inputs.py --profile ... --runtime ...
   --output ...`. Le runner arrête son environnement ; le passage payé exige
   un deuxième clone frais, le même profil et le reçu du prévol exact.
4. L'opérateur principal seul peut lancer `-Execute` avec les autorisations
   déjà prévues. Le plafond reste 0,50 USD cumulés, baseline comprise. Le
   résultat mécanique Worker utilise `planning_worker_observation.py
   --profile ... --runtime ... --capture-map ... --output ...` ; revue des
   recettes et validation WinUI restent des acceptations distinctes.
