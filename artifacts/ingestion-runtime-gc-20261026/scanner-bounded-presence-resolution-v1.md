# Scanner : inventaire borné, présence et suppression conditionnelle

État : correctif scanner gelé ; intégration worker conditionnelle en cours chez son propriétaire ; GREEN central final et qualification Linux automatique à rattacher séparément. Aucun test .NET, service, SQL, API, SSH ou Git exécuté par l’auteur de ce lot.

## Défaut reproduit

`Directory.EnumerateFiles(...).Take(MaxFilesPerScan)` reprenait le même premier lot à chaque scan. Les fichiers omis restaient donc potentiellement affamés. Surtout, la boucle de suppression interprétait non vu comme absent : un document physiquement présent devenait missing ; après grâce, un delete était admis. Le worker désactivait ensuite sa publication et le GC pouvait retirer le plan ancien, sans être lui-même la cause initiale de la fausse absence.

Le coordinateur a capturé `tests/scanner-bounded-red-v1/tests/manifest.json` : **2FAIL causaux et3PASS témoins**, zéro skip. Avec deux PDF distincts toujours présents et MaxFilesPerScan1 : l’état indexed devenait missing ; l’état déjà missing provoquait un delete. Les témoins sous la limite et la vraie absence réussissaient. Ces tests ne font appel à aucun moteur de rendu/OCR.

## Correction scanner

- `IngestionFileInventory` garde un curseur d’énumération et au plus un élément d’avance. Un lot ne sélectionne pas plus que la limite ; le suivant reprend après lui. Fin de cycle et interruption IO sont distinctes. Le curseur repart à zéro après fin, changement de root ou erreur, sans nouvelle table SQL.
- L’énumération admet les extensions .PDF avec une comparaison explicite ; les comparateurs des chemins scanner suivent la sensibilité de l’OS. Le cache/hash et la déduplication sont un lot séparé, pour ne pas supprimer à tort des chemins case-distincts sous Linux.
- `IngestionFilePresence` vérifie un root accessible et une cible résolue dans ce root. Seuls FileNotFound/DirectoryNotFound explicites pour la cible constituent une absence ; IO, accès refusé, root disparu et traversée restent inconnus.
- Un fichier omis mais présent n’est jamais déclaré missing. Un document missing revenu peut être restauré avec l’ancienne publication, sans transformer un lot borné en relecture illimitée de tous les contenus.
- Un lot partiel/incomplet n’admet aucune absence destructive. Une seconde vérification ponctuelle précède l’admission d’un delete après grâce.
- Seuls les deletes d’absence reçoivent `requiresSourceAbsence=true` et `source=scanner_missing`. Les suppressions explicites et les décisions de déduplication gardent leur contrat distinct.

La sonde `ingestion.scanner.inventory` commence avant l’énumération et expose uniquement compteurs/codes : files_selected, inventory_complete, inventory_truncated, next_batch, inventory_error, deletions_deferred et presence_unknown. Ni texte documentaire ni chemin ne sont ajoutés à ce signal. Une absence inconnue entraîne un outcome deferred, pas un succès implicite.

## Retour du fichier pendant la file d’attente

La contre-revue a identifié une autre fenêtre : un fichier peut revenir **après** l’admission et avant le worker. La vérification dans le scanner ne suffit pas ; il faut un delete conditionnel transporté dans le payload et le job jusqu’à l’exécution. Une garde universelle casserait les deletes explicites/dédoublonnage qui visent volontairement un fichier présent.

Le nouveau test PG appelle le vrai ProcessDeleteAsync sur un job réellement dequeued, après une publication SQL et un plan scellé. Le coordinateur a capturé **1FAIL et8PASS** dans le ciblage combiné annoncé : le cas conditionnel revenu échoue, les deux témoins explicite-présent/conditionnel-absent et les six cas scanner réussissent. Le correctif worker/JobRepo est confié à son propriétaire après ce RED. Le test exige la conservation de l’ancienne révision et du plan actif, un delete annulé et une réingestion fraîche queued dans une génération supérieure ; un simple état done/missing ne suffit pas.

La reprise doit être atomique et protégée par le bail/CAS : réserver la nouvelle génération hors verrous, puis annuler seulement le delete possédé, préserver la publication, rétablir l’état et planifier le nouvel upsert dans une transaction. Un état de présence inconnu doit différer le travail ; il ne doit jamais autoriser la suppression. La reconnaissance terminale du heartbeat doit rester limitée au propre jeton d’exécution.

## Preuves et limites

Les sept tests purs du curseur et des probes ont été exécutés centralement et passent, selon le coordinateur ; ils incluent IO partielle, accès refusé, root disparu, traversée, absence explicite et rotation. Un sixième cas PG vérifie les deux admissions sur deux tours de limite1. Le résultat final commun avec le worker conditionnel, puis le vrai scénario scanner/watcher, restent à produire.

La fairness démontrée est celle d’une arborescence lisible et stable. Une erreur persistante pendant une sous-arborescence réinitialise le curseur : une autre branche peut encore attendre ; le diagnostic demeure incomplete et les deletes sont différés. Aucun débit sous panne de stockage ni fairness universelle n’est promis. Un recheck filesystem n’est pas une transaction atomique FS↔SQL ; le garde worker conditionnel ferme la fenêtre de queue observée sans prétendre abolir toute course physique.

Le protocole réel borné se trouve dans `automatic-ingestion-qualification-protocol-v1.md` : nouveaux namespaces/configurations, scanner seul, watcher seul, puis chemin combiné à deux workers. Le runner est préparé par un autre agent ; les services sont réservés au coordinateur. Aucune configuration de corpus déjà PASS n’est réécrite.
