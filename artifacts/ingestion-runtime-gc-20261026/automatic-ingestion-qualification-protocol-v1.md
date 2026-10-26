# Qualification bornée scanner, watcher et deux workers

Préparation uniquement ; aucun service, API, .NET ou Git exécuté par l’auteur. Ne modifier ni configuration, ni manifeste, ni volume d’un corpus déjà PASS. L’opérateur crée des namespaces et des profils neufs, avec le backend final après les correctifs scanner/cache/delete conditionnel et leurs GREEN centraux.

## Pourquoi ce gate est nécessaire

Les defaults du code et de l’installation activent Watcher/Scanner et deux workers (`IngestionOptions.cs:14,31,36`, template signé40/42, compose166/169). Les corpus réels précédents désactivent les deux déclencheurs et emploient un profil borné. Ils qualifient l’ingestion via API, pas le chemin automatique installé.

Le GC ne crée pas les branches scanner/watcher. Il rend néanmoins nécessaire une admission sûre : une fausse absence peut désactiver une publication et retirer son plan, puis le GC supprime légitimement les anciens points. Le RED central `scanner-bounded-red-v1` a reproduit deux erreurs : un PDF omis par Take(1) devient missing ; un PDF déjà missing mais physiquement revenu déclenche un delete. Le correctif d’inventaire doit aussi éviter la starvation du même premier lot.

Le watcher ne fait **pas** de suppression finale : il marque missing après sa grâce (`FileWatcherService.cs:274`). Le scanner finalise la suppression. Une preuve watcher-only ne doit donc pas exiger deleted ni prétendre qualifier cette étape. Le nombre configuré de workers ne prouve pas deux consommateurs réellement actifs : HeavyComputeMaxConcurrency1 peut sérialiser l’extraction tout en permettant deux jobs running.

## Conditions communes aux trois sous-campagnes

- Nouvelle racine possédée, nouveaux volumes/DB/collection/réseau interne et cinq CID exacts ; mêmes images/modèles/DLL finaux observés, jamais un backend antérieur sous un nouveau label. Aucun port, volume ou namespace de production.
- Fichier de configuration neuf et hashé avant/après ; providers, CapabilityB, jobs avancés et reranker restent désactivés. Signature Production requise seulement si cette qualification est revendiquée ; sinon Development est explicite. **Ne pas relâcher la garde du runner corpus existant**, qui refuse scanner/watcher : créer un protocole automatique distinct avec ses propres gardes strictes.
- Trois sources Gold de contenus/SHA réellement distincts, par exemple les cas FR natif, scan et mixte. Leur identité et leurs annotations viennent du manifeste. Une copie binaire identique sous deux noms serait volontairement dédupliquable et n’est donc pas un bon témoin de préservation.
- Un chemin cible connu, une source de mise à jour différente et un document témoin intact. Les seules mutations sont des copies/remplacements/retraits de ces fichiers sandbox connus ; aucune mutation SQL, aucun enqueue API pour créer artificiellement l’événement à prouver. Les API sont réservées à la lecture et, au cleanup, à l’annulation des seuls jobs admis dans ce scope.
- Attendre un état final stable, zéro job actif, checksums SQL exacts, plan/révision/source attendus, points physiques exacts, recherche→lecture source courante et vecteurs du témoin inchangés. Lire les jobs/payload source pour attribuer scanner ou watcher ; ne pas déduire cette attribution du seul timing.
- Attentes de phase bornées, captures privées ordonnées, résumé public compteurs/IDs/hashes. Enfin arrêter seulement les CID possédés, conserver données/preuves, vérifier zéro job actif et bindings Docker libérés. Un timeout ou contrôle manquant est un échec/non-exécuté explicite.

## A — Scanner seul, WorkerConcurrency2

Profil neuf : Scanner=true, Watcher=false, workers2 ; scanner2s, MinFileAge2s, MissingGrace5s possibles pour la campagne (durées accélérées explicitement distinctes des defaults10s/120s). MaxFilesPerScan1 avec au moins trois fichiers permet d’exercer réellement le curseur, la limite et les tours suivants.

1. Présenter les trois PDF hashés sans API d’enqueue. Observer des admissions source=scanner et leur publication complète, y compris les fichiers au-delà du premier lot. L’inventaire partiel doit apparaître dans les sondes ; il ne doit pas déclarer les autres sources missing.
2. Remplacer la cible par la source de mise à jour connue, en changeant réellement octets et mtime. Observer nouvelle admission scanner, génération/révision/plan neufs et lecture des nouveaux faits. Vérifier le témoin inchangé ; aucun ancien plan ne doit remonter dans la recherche courante.
3. Retirer la cible connue. Observer missing puis suppression finale après grâce, indexed0, plan NULL, recherche vide et collecte des IDs exacts anciens si GC activé. Recréer les octets d’origine, puis observer une génération strictement supérieure et une publication complète.
4. Observer au moins un instantané SQL avec deux jobs running, des IDs distincts et deux owners w0/w1. À défaut, conserver la réussite fonctionnelle scanner mais marquer la preuve de concurrence runtime NON_EXECUTED. Ce témoin ne prouve pas deux moteurs OCR en calcul parallèle.

## B — Watcher seul, WorkerConcurrency2

Namespace/configuration neufs : Watcher=true, Scanner=false. Démarrer avant l’ajout des fichiers ; préexistants au boot, ils ne démontrent pas Created. Écrire vers un nom temporaire non-PDF puis renommer atomiquement en .pdf dans le root possédé. Les fichiers publiés doivent porter une admission source=watcher.

1. Ajouter deux sources distinctes, puis mettre à jour la cible. Exiger leurs publications et la présence du document témoin inchangé.
2. Retirer la cible et attendre sa grâce. Attendre missing avec conservation des anciennes données publiées ; **ne pas annoncer suppression finale**, puisqu’aucun scanner n’est activé.
3. Rétablir le fichier connu. Vérifier convergence vers indexed et source courante, sans perte du témoin. Le même fichier revenu peut être restauré sans réingestion ou produire une admission sûre selon l’ordre des événements ; le test porte sur la conservation et la convergence, pas sur un nombre arbitraire de jobs.
4. Capturer les événements/sondes et owners. Un nom temporaire, une opération trop rapide ou un watcher non démarré ne devient pas un PASS à partir d’un job créé par une autre voie.

## C — Chemin installé combiné

Troisième namespace/configuration : Scanner=true, Watcher=true, workers2, valeurs fonctionnelles identiques au profil prévu. Les durées accélérées éventuelles restent déclarées. Cette sous-campagne ferme les interactions ; elle ne réexécute pas le corpus complet.

1. Un PDF présent avant boot doit être découvert sans API ; un PDF créé après boot doit déclencher au moins une admission watcher observée. Si une course fait gagner le scanner, la fonction converge mais ce prélèvement seul ne prouve pas le watcher.
2. Mettre à jour une source pendant que l’autre job est running, puis attendre deux publications conformes, générations monotones, source fraîche et absence d’anciens points dans la recherche. Vérifier la préservation du témoin et une période calme sans nouvelles admissions spontanées.
3. Exercer le retrait temporaire/retour, puis le retrait durable/recréation. Le retour **après admission du delete scanner mais avant son exécution** est couvert causalement par un test PG du vrai worker ; pour le provoquer réellement il faut observer le job queued puis rétablir le fichier sans manipuler SQL. Si la fenêtre n’est pas capturée, marquer cette variante NON_EXECUTED et garder la preuve PG distincte, sans la transformer en runtime PASS.
4. Sur cette variante, le delete conditionnel doit être annulé, l’ancienne publication et son plan doivent rester actifs, puis une réingestion fraîche doit être planifiée dans une génération nouvelle. Un simple done ou un status missing caché ne satisfait pas le gate. Les deletes explicites/dédoublonnage restent autorisés à viser un fichier présent et ne reçoivent pas une garde universelle.

## Portée des résultats

Ces trois sous-campagnes qualifient les déclencheurs locaux et leur convergence sur quelques sources distinctes, avec deux consommateurs observés si la preuve existe. Elles ne qualifient pas SMB/NFS, tous les événements inotify/Windows, overflow de watcher, millions de fichiers, starvation sous une erreur d’énumération persistante, OOM/disque plein, tous les timings ou la performance générale. La politique de suppression doit rester failclosed sur IO/accès inconnu ; un inventaire incomplet n’est jamais un inventaire d’absences.
