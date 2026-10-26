# Correctif borné d'attente de disponibilité

`run-production-sequential-v3.py` conserve les versions précédentes. La v2 a rencontré un ConnectionResetError pendant le démarrage, avant tout lancement du runner watcher. Son échec et son cleanup restent historiques ; aucune preuve AUTO n'a été créée par cette tentative.

Le seul changement de logique est l'admission des OSError, dont ConnectionResetError, comme erreurs transitoires pendant la boucle `/ready`. Le délai maximal, les contrôles d'ownership à chaque tentative et toutes les conditions de vraie disponibilité Production signée restent inchangés. Une erreur de signature ou de provenance ne devient pas un succès. Le test causal simule une réinitialisation de connexion, puis une vraie réponse attendue ; il exige deux inspections et deux tentatives avant le retour signé.

Le dossier opérateur est désormais `evidence/production-operator-ROLE-v2`, afin de conserver le premier échec dans ROLE-v1. Les sorties du runner AUTO demeurent `evidence/automatic-ROLE-production-v1`, qui doivent être inexistantes avant reprise. Les mêmes empreintes gelées des helpers nullable et du runner restent imposées.

Commande après transfert du script et redémarrage **explicite par le coordinateur** des cinq CID watcher arrêtés :

```text
python3 -B run-production-sequential-v3.py --role watcher --role defaults --execute
```

Le script contrôle et réutilise les cinq CID déjà running, sans tenter de redémarrer automatiquement un conteneur exited. Defaults conserve son propre root neuf ; son PDF A doit être copié avant création du backend par l'opérateur. Aucun changement produit, .NET, SSH, Docker ou service n'a été exécuté par l'auteur de ce correctif.
