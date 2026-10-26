# Complément au gel retrieval — fenêtre absolue

La contre-revue parent a identifié un défaut réel dans le premier snapshot :
une inspection Docker pouvait consommer le temps restant, puis l'ancien
`max(.001, ...)` ouvrait quand même une requête après l'échéance. Le snapshot v1
et ses 81 tests demeurent historiques ; ils ne constituent pas la version finale.

Le transport est maintenant factorisé dans `owned_http`. Il refuse l'appel
avant inspection si la fenêtre est expirée, immédiatement après inspection, et
de nouveau juste avant `open`. Il n'existe plus de timeout minimal positif qui
autorise une requête tardive. Une réponse dont la lecture finit hors fenêtre
est aussi refusée. La dernière observation SQL de stabilité doit finir au plus
à l'échéance avant tout verdict PASS.

`retrievalFinishedAtUtc` mesure la fin des vérifications dans la fenêtre ;
`finishedAtUtc` inclut le cleanup de sécurité, qui peut prolonger la fermeture
sans nouvelle recherche. Les deux namespaces doivent recevoir la même échéance
UTC explicite, à 15 minutes maximum du départ.

Preuve neuve : `tests/linux-retrieval-facts-offline-v2/manifest.json` — 83 PASS,
0 échec/erreur/skip, 23 fichiers stables, `sourceChangedDuringRun=false`.
Les deux témoins nouveaux simulent une inspection qui consomme 31 secondes pour
une échéance de 30 secondes sans jamais construire l'opener HTTP, puis une
lecture HTTP / observation finale qui finit trop tard. Ils s'ajoutent aux 16
autres tests retrieval et aux 65 tests purs précédents.

Aucun Docker, HTTP, SQL, moteur, build ou fournisseur réel exécuté par cet
agent. Les SHA finaux exacts sont enregistrés dans le source-before/source-after
de cette nouvelle capture ; les hashes du rapport v1 sont volontairement ceux
de son ancien snapshot.
