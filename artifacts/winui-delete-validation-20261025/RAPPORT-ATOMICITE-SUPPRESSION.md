# Suppression des discussions : correction de l'atomicité

La suppression du backend comportait un défaut distinct de l'interface : les messages étaient effacés avant la session, sans transaction commune. Une erreur sur la session pouvait donc laisser une discussion vide. Une erreur d'audit pouvait renvoyer un échec alors que la suppression avait déjà été enregistrée.

## Preuve avant correction

Trois nouveaux cas ont été compilés contre le code initial et exécutés sur de nouvelles bases PostgreSQL isolées. Résultat : **deux échecs, un succès**.

- Un trigger contrôlé refuse le DELETE de la session : les trois sessions restent présentes, mais il ne reste que deux messages sur trois. Le message de la discussion visée a été perdu.
- Un trigger contrôlé refuse l'INSERT de l'audit : il ne reste que deux sessions sur trois, alors que l'appel a échoué. Le test s'arrête à cette première assertion ; ses assertions suivantes ne sont pas présentées comme exécutées.
- Le parcours nominal avec vérification tenant/utilisateur passe déjà.

Les triggers n'ont jamais été installés dans la base `postgres` utilisée par WinUI. Chaque cas disposait de sa propre base `saaia_chat_<UUID>`.

## Correction et preuve après correction

Les clés étrangères existantes suppriment déjà les messages et les jobs avancés par cascade. Le code supprime maintenant uniquement la session correspondant au tenant, à son identifiant et à l'utilisateur. Le DELETE et son audit sont exécutés dans une transaction explicite commune. Le backend confirme le succès seulement après confirmation du commit. Aucune migration n'est nécessaire.

La classe complète de tests du stockage des discussions passe : **7/7, zéro échec et zéro test ignoré**. Elle comprend les quatre tests existants et les trois nouveaux cas.

Les deux erreurs contrôlées préservent les trois sessions, les trois messages et le job avancé ; aucun audit de suppression réussie n'est créé. Le cas nominal vérifie les refus pour mauvais utilisateur et mauvais tenant, la cascade des seules données visées, la conservation d'une session ayant le même UUID dans un autre tenant, un unique audit et l'absence de nouvel audit lors d'un second DELETE.

Une sonde `chat.session.delete` fournit le nombre de sessions correspondant à la demande, la phase d'échec et la confirmation du commit. Le champ `commit_acknowledged=false` ne prétend pas qu'un commit serveur est impossible si sa réponse réseau est perdue.

## Isolation et limites

L'identité du cluster de test a été contrôlée par son manifeste, le PID et l'heure de démarrage du processus, le fichier `postmaster.pid`, le port loopback 52785 et le `data_directory` retourné par PostgreSQL. Les bases dédiées ont toutes été retirées : les inventaires avant/après contiennent uniquement `postgres`, `template0`, `template1`. Le nettoyage des tests vérifie le préfixe et l'UUID généré, l'OID de la base et son propriétaire avant toute suppression.

Les compilations utilisent des répertoires `bin/deletion-atomicity-*` et `obj/deletion-atomicity-*`. Les DLL backend Debug et Release habituelles sont restées strictement identiques. Le backend déjà chargé pour les tests WinUI n'a pas été remplacé pendant ceux-ci. Cette preuve teste la vraie méthode de production et PostgreSQL, sans automatiser l'interface et sans appel LLM.

Le correctif doit être chargé dans une nouvelle exécution backend contrôlée avant de lui attribuer une validation HTTP ou WinUI sur cette nouvelle version. Les tests ne simulent pas la perte de la réponse réseau du COMMIT. Le produit reste `TESTE_NON_APPROUVE`.

## Fichiers de preuve

- `atomicity-causal-proof.json` : résultats, limites, hashes et noms des bases des nouveaux cas GREEN.
- `atomicity-red/atomicity-red.trx`, `run.log`, `run-proof.json` : exécution avant correction.
- `atomicity-green/atomicity-green.trx`, `run.log`, `run-proof.json` : exécution après correction, sources et DLL vérifiées.
