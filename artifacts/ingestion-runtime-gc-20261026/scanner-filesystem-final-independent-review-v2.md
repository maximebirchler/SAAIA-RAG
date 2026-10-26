# Revue finale du scanner, des suppressions conditionnelles et de la fixture OCR

Cette note complète les preuves historiques sans les réécrire. Elle repose sur la lecture des manifestes et TRX locaux, des quatre inventaires de sources, des empreintes des DLL et du manifeste de fermeture PostgreSQL. Aucun service, test .NET, appel réseau, Git ni campagne payante n'a été exécuté par le réviseur.

## Ancrage commun vérifié

- Backend Unit : run `b4c728073f114333baddf5eda91e1e62`, **2776 PASS, 0 échec, 2 non exécutés**. Le build Release est demandé dans ce manifeste.
- PostgreSQL isolé : run `2257ea2321bb4112bacdffbd831dbe81`, **350 PASS, 0 échec, 2 non exécutés**. Ce run sans rebuild utilise les mêmes quatre DLL que le précédent.
- Les deux campagnes ont `sourceChangedDuringRun=false`, le même inventaire de **1842 fichiers**, empreinte `3edd882d8486e1d2042734b7eea695485549a0a35b8baab9d8c6dda2878efef1`. Les SHA des deux TRX correspondent à leurs manifestes.
- Backend AnyCPU : `AFE4D78EF471DA577AF31EFF35C6B1D42700DA0D5FBE8358DC7EA74F9838371B`.
- Tests : `1E92BA08B736C43D6FC8C10552D419D7F02DE1EFCEB66AA9178612654382F70B`.
- Le PDF OCR embarqué et les trois fichiers du dernier delta d'outcome figurent avec les mêmes SHA dans les inventaires Unit avant/après et PG avant/après. Ils correspondent aussi aux octets présents au moment de cette revue.

Ces résultats sont ceux du projet Backend et de sa suite PG ; ils ne sont pas présentés comme une nouvelle exécution complète de tous les projets du dépôt. La limite habituelle d'un simple run sans rebuild est ici recoupée par le build Unit précédent, les mêmes empreintes binaires et l'absence de changement de sources.

Le manifeste extérieur PG confirme un cluster dédié PostgreSQL 16.14, aucun cluster existant modifié, environnement restauré, cluster et mot de passe bootstrap supprimés, aucun listener restant. Le journal d'arrêt annonce la fermeture du serveur. Aucun chemin de profil ni secret n'est reproduit ici.

Le détail machine et les SHA des preuves sont dans `scanner-filesystem-final-independent-review-v2.json`.

## Régressions causales fermées par les tests

Le RED scanner initial démontrait deux erreurs : un fichier encore présent, mais omis du premier lot limité, devenait missing ; un document déjà missing et toujours présent provoquait un delete. Le curseur de lots et la vérification explicite de présence corrigent cette confusion entre non observé et absent. Le GREEN final comprend les quatre variantes limitées/non limitées et indexed/missing, une vraie absence après grâce, et l'admission successive de deux fichiers distincts sous limite 1.

La deuxième cause était le retour du fichier après admission du delete scanner, avant son exécution. Les deletes scanner portent maintenant une exigence d'absence ; les suppressions explicites et de déduplication conservent leur intention propre. Le worker vérifie au début et après acquisition des verrous, avant la publication de suppression. Unknown ne devient pas absent.

Si la source est revenue, la transaction sous bail conserve l'ancienne publication, annule seulement le delete possédé et planifie une nouvelle génération. Le GREEN final vérifie la conservation de révision et de plan, les deux témoins delete explicite/fichier présent et delete conditionnel/fichier absent, les observations Present/Unknown après attente du verrou, le rollback si l'insertion de reprise échoue, le refus du propriétaire périmé et la conservation de la dernière intention conditionnelle lors d'un supersede. La réservation de génération précède les verrous ; un trou de numérotation après rollback n'est pas une réutilisation de version.

Le dernier correctif était diagnostique : `FreshGenerationQueued` et `Superseded` sont distincts. Une génération B déjà courante ne déclenche plus une sonde prétendant qu'une nouvelle reprise vient d'être admise. Le résultat de publication du delete reste false dans les deux branches. Le test `Returned_source_reports_superseded_without_claiming_another_fresh_generation` est PASS dans le run PG final.

**16 cas PG directement sélectionnés de scanner, suppression conditionnelle, reprise et témoins sont tous PASS.** Ils s'ajoutent aux autres tests PG de bail, générations, GC et publication ; ils ne substituent pas une simulation au futur parcours automatique Linux.

## OCR et système de fichiers Linux : preuves séparées

Les deux non exécutés Unit sont le vrai test OCR opt-in et le vrai test de chemins sensibles à la casse Linux. Il serait incorrect de les compter comme PASS dans le run Windows. Les deux non exécutés PG sont les anciens scénarios `SearchCoreAsync_runtime_ready_v5_cases_match_expected_behavior_and_primary_document` et `SearchCoreAsync_runtime_ready_writer_cases_keep_prudent_guidance_shape` ; cette revue ne les transforme pas en succès.

La fixture OCR actuelle est une page raster avec police typographique réelle, embarquée dans la DLL de tests : PDF `69441aab874dfb04ac8d015431ff3746564bb8d88b6a6b022327b7f2c37c60c6`. Le test exige toujours les cinq mots opérationnels et l'identifiant exact XQ42. Le remplacement de la police 5×7 n'a pas affaibli cet oracle. Le paquet `linux-ocr-e2e/package-v3` fige ces DLL et les dépendances autorisées ; les paquets et échecs précédents restent historiques.

Le coordinateur a communiqué le résultat réel final OCR : **1 sélectionné, 1 exécuté, 1 PASS, 0 échec, 0 skip**, exit 0, conteneur arrêté, réseau none et image finale identifiée. Le JSON se trouve sur le serveur dans le namespace final ; il n'a pas été relu directement par ce réviseur sans SSH. Ce résultat rapporté ferme le test OCR de cette fixture et de ces moteurs, pas une qualification universelle OCR ni la Production signée. Les trois tentatives réelles précédentes restent distinctes, dont une erreur d'orchestration avant test ; aucune n'est écrasée.

Le test réel Linux des chemins case-distincts possède maintenant un opérateur séparé, `linux-filesystem-case/run-owned-linux-case.py`. Ses **14 tests de protocole purs passent**, dont le timeout suivi d'arrêt du seul CID possédé, la vérification TRX exacte et les refus d'image/commande/montage/port incohérents. Une contre-revue indépendante n'a trouvé aucun défaut matériel. L'opérateur est gelé et prêt pour le coordinateur ; la préparation et les mocks ne sont pas une exécution Linux réelle.

## Ce qui reste à qualifier et limites conservées

Le runner automatique scanner seul, watcher seul, puis scanner+watcher/deux workers est préparé séparément. Sa contre-revue et ses exécutions réelles ne sont pas remplacées par le GREEN PG. Une valeur de configuration workerCount=2 ne prouve pas deux jobs simultanément running ; il faut les observer ensemble avec deux propriétaires distincts. Une course de retour du fichier non capturée en runtime doit rester NON_EXECUTED dans ce runtime, même si la garde est couverte causalement en PG.

La fairness du curseur est démontrée sur une arborescence lisible et stable. Une erreur persistante d'énumération peut réinitialiser le curseur et retarder une autre branche. Les deletes sont alors différés avec diagnostic incomplet, mais aucun débit ni parcours universel sous panne n'est promis. Le filesystem et PostgreSQL ne forment pas une transaction atomique ; les observations avant admission et sous verrou ferment les fenêtres reproduites sans abolir une modification physique après la dernière observation.

Les anciens jobs sans marqueur d'absence restent des suppressions inconditionnelles, indiscernables d'une intention explicite historique. Une mise à niveau doit donc inspecter ou vider les anciennes queues avant d'affirmer qu'elles bénéficient du nouveau contrat ; aucune réinterprétation silencieuse de ces payloads n'est faite.

La qualification installée reste attachée aux images, versions, profils, cache et corpus effectivement observés. Les autres moteurs/modèles, GPU, partages réseau, charge prolongée, chaos de restauration et Production signée ne sont pas approuvés par cette note. Aucun nouveau test sémantique du planning ni appel payant n'a été effectué dans ce lot.
