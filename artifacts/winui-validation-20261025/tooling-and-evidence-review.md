# Revue de l'outillage et des preuves WinUI —25octobre2026

Cette revue est distincte des observations visuelles effectuées par l'opérateur
du client. Elle n'a lancé aucun modèle, modifié aucune donnée métier, restauré
aucun profil et arrêté aucun processus. Les exceptions d'écriture de cette revue
sont ses outils et preuves locales, ainsi que la correction explicitement
demandée du lanceur de tests.

## Preuves à conserver

`versionable-evidence-candidates-intermediate.json` fournit une liste explicite
de fichiers avec taille et SHA256. Elle exclut les sous-arbres `runtime`,
`private`, `client-data`, les inventaires originaux du profil, les manifestes
bruts des processus/modèles et leurs sorties, les captures14/16/32/45 ainsi
que les images PDF privées et Explorer. La liste doit être régénérée après le
gel final : elle a été produite pendant la poursuite des tests/UI.

Les éléments privilégiés sont les manifestes de build/tests, TRX et inventaires
de sources, les preuves compactes de scénario, snapshots du chat-store ne
conservant que les empreintes, événements HTTP sans corps/clé et observations
natives portant exclusivement sur les documents et conversations synthétiques.
Les réponses API documentaires contiennent le corpus synthétique déclaré ; elles
ne sont pas des exports du corpus réel.

Les captures03,14b,25,32 et33 ont été regardées pour cette revue. La capture32
masque la clé mais expose des exemples de topologie réseau privée ; elle reste
exclue. La14b expose un chemin local de diagnostic et est exclue sur demande.
La45 contient également des placeholders réseau privés et est exclue. Les
autres PNG de la liste sont des candidats à sélectionner avec l'opérateur :
l'index ne prétend pas les avoir tous approuvés visuellement.

Les JSON bruts de profil, fichiers settings/secure, configurations backend,
clusters PostgreSQL, bundles support complets, sorties brutes des modèles et
captures privées restent locaux. Leur présence n'autorise jamais un ajout Git
récursif du lot. Le scanner de l'index contrôle les deux credentials synthétiques
connus sans publier leurs valeurs ; ce contrôle ne certifie pas l'absence de tout
secret inconnu. Les preuves anciennes échouées/rejetées sont signalées et ne
doivent pas être additionnées aux tests acceptés.

## Cohérence démontrée

- Le catalogue a été reconstruit par le vrai builder de production après le
  seed :2 catégories,2 PDF homonymes dans des chemins distincts,1 document par
  catégorie. Le premier résultat UI à0 catégorie provenait d'une fixture
  incomplète ; il ne constitue pas une validation documentaire.
- Le snapshot du chat-store accepté comporte10 messages avec compteurs cohérents
  lors de sa capture. Les snapshots ultérieurs conservent IDs, rôles, hash UTF8,
  caractères et retours à la ligne. La comparaison ne transforme pas un texte
  volontairement répété en verdict de doublon.
- `persistence-deadline-proof.json` relie la trace du timeout30s au POST proxy
  retardé45s, puis au seul message utilisateur effectivement conservé dans
  PostgreSQL. Une transaction READ ONLY vérifie le répertoire du cluster, l'ID,
  le hash et l'unicité de ce contenu. Aucun retry automatique et aucune sonde
  modèle ne sont observés pour ce tour. Ce résultat n'est pas une garantie
  d'idempotence d'un renvoi manuel ultérieur.
- `trace-integrity-storage-proxy-intermediate.json` valide la corrélation wire
  avec au moins1 requête liée à un tour. La collecte utilise `AllowInFlight` car
  la campagne était encore en cours ; le tour du timeout est, lui, terminal.

Les anciennes fixtures ne capturent pas toutes les mêmes champs : le helper
HTTP18087 a démarré avant l'ajout des headers aux événements. Aucun rapport ne
doit lui attribuer ces headers rétroactivement. Le proxy5157 actuel les capture.

## Profil réel et isolation des tests

La baseline originale couvre556 fichiers sous4 périmètres du profil SAAIA :
`client`, `governance`, `llm-dev`, `logs`. Elle contient le secure-store et ne
contient pas de settings.json. Elle ne permet pas de conclure sur Models,
runtime, ProgramData, provisioning ni les credentials Windows hors de ces
périmètres.

La première comparaison révèle555 fichiers identiques et une extension du log
normal de démarrage ; le secure-store est inchangé. L'extension contient les
sondes synthétiques des tests de diagnostics et commence avant le premier
lancement WinUI isolé. Le lanceur commun n'injectait pas de racine de données
pour le processus de tests, d'où ces écritures dans le log normal.

`tools/test-project.ps1` impose maintenant une racine absolue pour les suites
Client et ClientCore : réutilisation de l'environnement explicite ou dossier
`client-data` owned sous le résultat de campagne. Il restaure l'environnement
après exécution. `test-client-profile-isolation.ps1` a réellement exécuté les
2tests de diagnostics dans chacun des2 modes :15 sondes sont produites dans
chaque racine dédiée ; une racine relative est rejetée avant les tests. Le
profil normal reste556/556 identique pendant ces vérifications. L'append
historique est conservé, sans restauration ni effacement.

`compare-user-profile.ps1` permet la comparaison finale : elle ne publie ni
chemins privés ni contenu, seulement périmètres connus, compteurs et identifiants
de fichiers hachés. Une comparaison avec la baseline initiale restera honnêtement
négative à cause de l'append historique ; elle ne doit pas être réécrite en
réussite globale.

## Contrôles à refaire après le gel

1. Lancer les suites nécessaires via `test-project.ps1` avec racine dédiée,
   puis vérifier compteurs, SHA des binaires et `environmentRestored=true`.
2. Construire le client Release x64 sans hooks et relier le SHA exécuté aux
   derniers scénarios UI nécessaires. Un ancien succès UI ne valide pas à lui
   seul une correction ajoutée ensuite.
3. Relancer `verify-evidence.ps1` sans `AllowInFlight` lorsque tous les tours sont
   terminés, avec bundle support de l'UI et events du proxy corrélé. Examiner
   les pièces manquantes/échouées et les compteurs de collecteurs.
4. Prendre le snapshot durable et la comparaison de profil finaux, puis
   régénérer la liste explicite de preuves et leurs empreintes. Vérifier le diff
   Git sélectionné et l'absence de sous-arbre privé avant tout commit.

Les tests du vérificateur de traces et les profils de fixtures restent des
preuves d'outillage ; ils ne remplacent jamais l'observation de l'interface,
le rendu de la page PDF ou la qualification du modèle.

## Précautions de nettoyage des auxiliaires

Aucun teardown n'a été effectué par cette revue. Les helpers5158 supplémentaires
déclarés par l'opérateur figurent dans le manifeste owned. Ne jamais arrêter un
service à partir du seul numéro de port : vérifier PID, heure de création,
chemin exécutable, parent propriétaire et port effectivement détenu.

Les wrappers backend/Qwen déjà actifs utilisent encore la redirection initiale,
qui écrit stdout/stderr à la fin de leur child. Terminer le wrapper et tout son
arbre simultanément peut perdre ces sorties. Si ces logs sont nécessaires,
arrêter d'abord le child dédié après vérifications, laisser le wrapper sortir
normalement et écrire ses fichiers, puis terminer les autres auxiliaires.
Le source du wrapper a été amélioré pour les lancements suivants ; les processus
déjà lancés n'ont pas été remplacés.

Fermer le client et vérifier qu'aucun job avancé n'est actif avant d'arrêter les
adapters ; sinon un arrêt de fixture peut fabriquer un faux échec en teardown.
PostgreSQL doit être arrêté avec `pg_ctl` sur son seul data_directory dédié.
Enfin, vérifier la fermeture effective de tous les ports owned et l'absence
d'orphelins : la sortie du parent seule ne suffit pas. Les données locales et
preuves restent conservées ; aucune suppression récursive n'est nécessaire.

Le produit demeure `TESTE_NON_APPROUVE`. Aucun appel Terra ou RunPod n'a été
effectué par ce lot de validation de mécanique WinUI.

## Compléments observés après la première revue

Le bundle exporté réellement par l'UI build04 a été inspecté sans extraction ni
versionnement du zip. La preuve `support-bundle-review-build04-accepted.json`
valide14 contrôles :51 entrées,452 sondes,5 tours, aucun credential plaintext ou
DPAPI connu, aucun texte documentaire/chat connu, aucun collecteur défaillant,
aucune pièce échouée et11 pièces optionnelles absentes déclarées. Le probe du
modèle utilise bien `/v1/models`, avec succès HTTP200. Les chemins de données
restent isolés ; le chemin du binaire applicatif sous le repo est distinct.

Deux règles de l'inspecteur initial ont été corrigées avant acceptation : les
checksums originaux de gouvernance portent sur le texte UTF8 sans BOM, alors que
les sidecars des copies expurgées portent sur les octets du fichier ; appBaseDir
est un chemin de binaire en lecture seule et n'est pas un dataRoot. Le premier
rapport d'inspection échoué reste une preuve historique d'outillage, pas un
défaut produit.

La reprise avancée réelle est liée aux snapshots avant/durant/après dans
`advanced-resume-proof.json` : même job, même assistant patché en place,2messages
dans la discussion d'origine à chaque étape,2jobs persistés avant/après et aucun
job créé dans l'intervalle. Les4 autres discussions communes conservent les
mêmes empreintes de messages et métadonnées. Une nouvelle discussion fixture de
carte ajoutée séparément explique l'augmentation du compteur global ; elle ne
constitue pas un nouvel envoi du job.

La trace de reprise contient104 événements,50 réponses de polling, une seule fin
success, aucun create et aucun appel local au modèle. La chaîne persistée
originTraceId/traceId et le parent SQL relient origine et reprise. L'inspection
directe de Configuration.originTraceId nécessitera un export support après cette
reprise ; le bundle build04 précédent ne couvre pas cette phase ultérieure.

La vérification globale stricte post-reprise conserve son rejet : un seul tour
historique n'a pas de fin, précisément celui dont le processus a été fermé
volontairement pendant le job. `trace-interruption-accounting.json` exige une
correspondance exacte avec cette interruption documentée :1incomplète attendue,
0incomplète inexpliquée et0autre échec strict, sur820 événements/129 tours au
moment du contrôle. Le statut de comptabilité est explicitement distinct du
succès du vérificateur strict. Aucune fin n'est fabriquée ni cachée.

Les fixtures de carte positive et négatives sont des discussions séparées,
créées via l'API du même utilisateur synthétique. Les cas hash incorrect et
chemin manquant conservent les identités canoniques pour éprouver le refus de
références incohérentes ; aucun PDF n'est modifié. Leur présence ne prouve pas à
elle seule un clic ou l'affichage d'une page : ces observations sont fournies
séparément par l'opérateur.

Correction d'oracle après observation du cas MissingPath : le chemin absent
conserve ici une identité de version SHA256 valide. Le resolver peut donc
retrouver le fichier exact par basename et hash, tout en excluant l'archive
homonyme dont le hash diffère. L'ouverture du guide exact est un comportement
correct ; le refus systématique réclamé par le message fixture initial était
une attente trop stricte de l'outillage. Le seed futur décrit désormais
« récupération exacte ou refus sûr ». Les messages et preuves historiques ne
sont pas réécrits ; aucun défaut produit n'est attribué à cette récupération.

## Audit des auxiliaires et polling W24

L'audit `runtime-ownership-network-audit-build06.json` observe10racines owned,
31PID avec leurs enfants directs et10listeners, tous en loopback. Aucune
connexion établie de ces PID ne sort du loopback à l'instant de capture. Ce
contrôle ponctuel n'est pas une interception réseau ni une preuve exhaustive
de l'historique réseau. Le PostgreSQL existant5432 reste hors du manifeste
owned, PID7844 ; ses détails d'exécutable et d'heure de démarrage ne sont pas
lisibles sous le compte courant, limite conservée dans la preuve.

L'outil d'arrêt a été durci avec autorisation explicite, sans toucher au
produit : prévalidation globale, mode WhatIf sans mutation, revalidation juste
avant action, arrêt du child avant attente de sortie du wrapper, PG dédié en
dernier et contrôle final des ports/PID. Le plan
`owned-stop-whatif-build06-v2.json` couvre5127/5137/18088,5147/5157/5158/5167,
18087/18089 puis PG62401. Les4gardes négatives refusent5432, un autre data
directory, une heure PID différente et un exécutable PostgreSQL différent.
Le manifeste est resté identique et tous les PID owned actifs après ces tests.
Aucun arrêt n'a été exécuté. Les6logs stdout/stderr backend/avancé/Qwen sont
encore absents : les anciens wrappers les écriront après sortie des enfants.
Il faudra vérifier ces fichiers après l'arrêt, sans tuer les wrappers.

Le proxy W24 transmet au vrai worker5137 ; seules4réponses GET du nouveau job
créé par la vraie UI sont contrôlées. La preuve
`advanced-polling-faults-proof.json` valide12contrôles :2HTTP503 suivis de2retry,
une régression2→1 rejetée avec `job_revision_regressed`, puis une identité de
session invalide rejetée avec `session_identity_changed`. Le même job finit
réellement succeeded/revision4 et le même assistant est persisté dans sa
discussion correcte. Les12discussions précédentes gardent leurs hash ; le
nombre de jobs passe de2à3, uniquement pour le job attendu, sans duplication.
La discussion W24 contient exactement un user et un assistant.

Les2tours de ce scénario ont chacun une fin unique. Leurs `turn.end success`
indiquent la fin du parcours client ; ils ne transforment pas les2validations
explicitement échouées en réponses acceptées. L'absence de mauvaise identité
dans les sources persistées et la révision terminale correcte sont vérifiées
en SQL. Le rejet avant assignation/callback `onSnapshot` est également relié
au contrôle de flux du poller ; aucune journalisation SQL exhaustive des
états transitoires n'est revendiquée. Les contrôles ont ensuite été remis à
passthrough/délai avancé2s par l'opérateur, pas par le vérificateur.

La sélection post-W24 reste intermédiaire :460candidats, dont77visuels à
confirmer, aucune clé synthétique connue détectée et aucun fichier verrouillé.
Elle ne scelle pas les fichiers encore actifs. Le sous-arbre runtime/profils,
les zips support bruts, les captures privées et les inventaires utilisateur
restent exclus. Aucun staging ni commit n'a été effectué par cet outillage.

## Arrêt final autorisé après fin de l'UI

Après fermeture normale du client build08, le contrôle SQL READ ONLY confirme
qu'aucun job n'est actif : les5jobs de la base isolée sont succeeded. Le
snapshot final `20261025T133549558Z-final-before-owned-stop.json` conserve les
14discussions et42messages de l'utilisateur primaire, avec hash et compteurs,
avant l'arrêt. Le dernier plan WhatIf a de nouveau prévalidé toutes les
identités et le cluster dédié ; aucune garde n'a été contournée.

L'arrêt autorisé s'est ensuite terminé normalement. La preuve
`owned-stop-proof-final.json` valide8contrôles : les10ports de test sont
fermés, les10racines et21enfants connus sont sortis, PG5432 conserve son
PID7844 et ses2adresses d'écoute, le manifeste owned est marqué stopped et
toutes les données restent présentes. Le serveur et le profil utilisateur
normal n'ont pas été modifiés par cet arrêt.

Les6fichiers stdout/stderr tamponnés ont été écrits et hashés. Le stdout
backend contient495819octets, le stdout avancé286374octets et le stderr Qwen
40269octets ; les3autres existent et sont vides. Ils sont conservés localement
et exclus de la sélection Git. Les empreintes et tailles compactes sont dans
la preuve d'arrêt. Aucun fichier, profil ou répertoire de données n'a été
supprimé.
