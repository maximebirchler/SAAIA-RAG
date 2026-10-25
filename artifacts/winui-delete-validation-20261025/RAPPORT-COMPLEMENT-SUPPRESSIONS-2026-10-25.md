# Validation réelle des suppressions WinUI et corrections associées

Après l'autorisation explicite de supprimer les seules discussions fictives, les trois parcours manquants ont été exécutés dans la vraie fenêtre Windows : suppression inactive, active et dernière discussion. Deux défauts supplémentaires ont été trouvés, reproduits puis corrigés : une suppression backend non atomique et une mauvaise récupération du client après suppression réussie suivie d'un échec de rafraîchissement. Le client et le backend corrigés ont ensuite été recompilés et les parcours ont été rejoués.

Ce complément valide ces chemins dans le périmètre décrit ci-dessous. Il ne transforme pas les dix variantes complémentaires de l'ancienne campagne en tests exécutés et ne valide pas le planning de repas. Aucun appel Terra, modèle local réel ou fournisseur payant n'a été nécessaire.

## Ce qui a été réellement constaté dans WinUI

Le premier environnement contenait trois discussions A/B/C créées uniquement pour ces tests, avec deux messages marqueurs chacune. Leur utilisateur, leur base PostgreSQL et leur profil client étaient isolés. Les actions de suppression ont été faites dans les menus et dialogues réels du client ; les scripts de vérification n'ont pas supprimé les discussions à sa place.

| Action | Résultat observé sur le client corrigé | Vérification durable |
| --- | --- | --- |
| Supprimer A inactive pendant que B est chargée | A disparaît. B reste sélectionnée avec les mêmes deux messages ; C reste présente et la saisie fonctionne. |31 contrôles API/SQL réussis, aucun échec. |
| Supprimer B active | C est automatiquement sélectionnée avec ses propres messages. Aucun marqueur de B ne reste dans la vue. L'ID sauvegardé devient C. |23 contrôles réussis, aucun échec. |
| Supprimer C, la dernière discussion | Une nouvelle discussion vide R est créée avec un nouvel ID, sélectionnée et sauvegardée. La saisie accepte un brouillon ; Envoyer devient actif sans qu'on clique dessus. |17 contrôles réussis, aucun échec. |

Les observations natives12 à19 et les fichiers `local-fixture-fixed/deletion-step-*-verification.json` relient ces résultats aux états réellement enregistrés. Les compteurs ci-dessus sont des contrôles d'état de chaque étape, dont certains se recouvrent ; ils ne sont pas additionnés comme autant de tests unitaires différents.

**Limite importante :** A/B/C avaient des messages mais aucune carte source. Cette séquence prouve la conservation du bon historique et de la sélection, pas la préservation visuelle d'une carte source non vide pendant une suppression inactive. Le code correspondant et les tests de sources offrent une couverture distincte. La mémoire privée complète de l'agent n'a pas été directement inspectée.

## Défaut client : suppression réussie mais discussion encore affichée

Le code initial attendait l'acquittement du DELETE puis rafraîchissait la liste. Si cette lecture échouait, la cible restait dans l'ancienne liste locale alors qu'elle avait réellement disparu du backend. Pour une discussion active, la vue était vide et la saisie désactivée. Le bandeau disait exactement **« Suppression non confirmée »**, avec le texte générique invitant à vérifier l'historique et réessayer.

Ce défaut a été reproduit sur build 08 avec une discussion D jetable. Un proxy local a laissé passer le vrai DELETE, constaté sa réponse200, puis injecté une seule réponse500 sur le GET de l'inventaire du même utilisateur. La base et l'API confirmaient déjà l'absence de D et de ses deux messages. L'observation10 avait d'abord une description imprécise du bandeau ; sa capture et son arbre, explicitement rectifiés par 10b, donnent le texte exact. Aucune preuve ancienne n'a été réécrite.

Le correctif retire désormais la cible de la liste locale immédiatement après l'acquittement, avant toute nouvelle lecture susceptible d'échouer. Il garde l'historique chargé si la cible était inactive. Si la cible était active et que le rafraîchissement échoue, il tente de charger une discussion survivante déjà connue. La suppression n'est pas relancée automatiquement. Un échec de cette récupération reste distinct et enregistré.

Le bandeau indique maintenant **« Discussion supprimée, liste non actualisée »**, puis **« La suppression est confirmée. Choisissez une autre discussion ou Nouveau pour continuer. La liste sera actualisée à la prochaine connexion. »** Les six langues possèdent ce message spécifique ; il ne prétend plus que la suppression est inconnue ou que le brouillon d'une discussion supprimée serait préservé.

Le même défaut contrôlé a été rejoué sur le client corrigé build 09 et le nouveau backend. Résultat : D disparaît immédiatement, R survivante est automatiquement chargée vide, la saisie fonctionne et le bandeau distingue correctement les deux opérations. Un brouillon saisi active Envoyer sans appel de modèle. Les 16 contrôles durables/protocole/sondes passent.

Enfin, le même profil a été fermé puis redémarré. R reste seule, sélectionnée et vide ; aucune discussion supprimée ne revient, aucun avertissement ne reste affiché et la saisie est disponible. L'ID persistant est bien celui de R. Ce résultat est observé dans la capture 24 et vérifié dans `delete-fixed-post-restart-proof.json`.

## Défaut backend : perte partielle possible lors d'une suppression échouée

La lecture du code a révélé un défaut distinct : les messages étaient supprimés séparément de leur session, puis l'audit était écrit après la suppression. Une erreur entre ces étapes pouvait laisser une discussion vidée ou faire remonter un échec alors que les données avaient déjà été supprimées.

Deux erreurs contrôlées ont été reproduites sur de nouvelles bases PostgreSQL dédiées aux tests : le refus du DELETE parent et le refus de l'écriture d'audit. Avant correction, les deux tests échouaient ; le parcours nominal passait. Les bases de la validation WinUI n'ont pas reçu ces triggers de panne.

Le backend supprime maintenant le parent `chat_sessions` correspondant au tenant et à l'utilisateur. Les cascades déjà présentes suppriment ses messages et jobs avancés. Le DELETE et son audit sont dans une même transaction explicite ; le succès n'est annoncé qu'après acquittement du commit. Aucune migration supplémentaire n'est nécessaire.

La classe d'intégration du stockage passe **7/7**, dont les deux erreurs contrôlées. Dans ces cas, sessions, messages et job avancé sont conservés et aucun faux audit de suppression réussie n'est enregistré. Le cas nominal vérifie aussi l'isolation utilisateur/tenant, une session portant le même UUID dans un autre tenant, l'audit unique et le second DELETE404. Les bases temporaires de ces tests ont été retirées après vérification de leur identité.

Le backend corrigé a ensuite été réellement démarré pour les tests WinUI build 09 ; les résultats natifs ne sont donc pas attribués à un ancien processus déjà chargé. La perte éventuelle de la réponse réseau d'un commit n'a pas été simulée : une absence d'acquittement conserve à juste titre un statut d'opération non confirmée.

## Sondes et capacité de diagnostic

La nouvelle opération client `session_delete` sépare l'acquittement, le rafraîchissement et la récupération. Le cas D corrigé fournit exactement cette suite dans la même trace :

1. `turn.begin` ;
2. `session.delete.acknowledged`, succès ;
3. `session.delete.refresh_failed`, dégradé ;
4. `session.delete.recovery`, succès avec discussion connue disponible ;
5. `turn.end`, dégradé avec motif `deleted_refresh_failed`.

La trace reste dégradée parce que l'inventaire n'a pas pu être actualisé, même si la récupération a rendu le client utilisable. Les cinq événements sélectionnés ont été comparés au fichier brut capturé, dont le SHA a été vérifié : ils n'ont pas été réécrits ou synthétisés. Les sondes ne contiennent ni noms de discussions, ni contenu de messages/documents, ni clé.

Côté backend, `chat.session.delete` indique notamment la confirmation du commit, le nombre de sessions visées, la phase d'échec et le type d'exception. `commit_acknowledged=false` signifie absence d'acquittement ; il ne prouve pas à lui seul qu'un commit serveur n'a pas eu lieu.

La preuve après redémarrage compte 8 événements dans 2 traces dont les débuts et fins sont équilibrés. Ce contrôle de balance est utile mais n'est pas présenté comme un vérificateur complet des schémas et relations parent/enfant.

## Tests, binaires et livraison

La suite complète finale passe **4 923 tests unitaires, zéro échec et zéro test ignoré** : backend 2 430, contrats 20, cœur client 80 et client 2 393. Les quatre TRX ont été vérifiés contre leurs SHA et leurs compteurs du manifeste. Les 20 tests ciblés du correctif sont inclus dans cette suite ; ils ne s'ajoutent pas au total.

Les sources sont restées identiques pendant la suite complète. L'inventaire de 1 069 fichiers de production retenus est également resté identique ensuite. Les outils complémentaires préparés après cette suite ont leurs contrôles et leurs empreintes séparés ; ils ne sont pas présentés comme ayant été exécutés dans les 4 923 tests.

Les compilations backend et WinUI de production réussissent avec zéro avertissement et zéro erreur. Les trois lancements natifs corrigés utilisent la même DLL WinUI build 09, SHA `7CCBEDB63AAE52E9CC0E89C3B7924C04AB0E1E39C81C269985F87BA76DAFE6FE`. Le SHA du backend réellement lancé correspond au binaire de production et à la dépendance backend testée dans la suite. Le client de test AnyCPU avec hooks et le client natif x64 restent des binaires distincts par construction ; leur égalité de hash n'est pas prétendue.

Le contrôle d'architecture passe. L'agrégat `MainWindow` reste à 15319/15376 lignes, sans relever le plafond. L'orchestration du diagnostic et de la récupération se trouve dans un service court et testable. Une relecture croisée du correctif client et une relecture de la transaction backend n'ont pas identifié de défaut matériel supplémentaire dans ce périmètre.

Le code et l'outillage sont livrés dans le commit **`81c8028f6d71384351a6e9000e79bfc852859a20`**, poussé et vérifié par l'opérateur. Les artefacts de preuve sont livrés séparément ; ce rapport ne prétend pas qu'ils étaient déjà dans ce commit de code.

## Isolation et suite

Les vérifications de cette phase retrouvent les 556 fichiers couverts du profil habituel strictement inchangés, sans ajout, absence ou différence. Cette comparaison a un périmètre explicite ; elle ne prétend pas inspecter tout le magasin d'identifiants Windows ni tous les paramètres de la machine.

Le fournisseur local de test enregistre zéro requête de chat modèle et zéro appel externe ; aucun processus de modèle ni clé de fournisseur payant n'a été configuré. Les deux environnements de suppression ont été arrêtés avec contrôle d'ownership. La preuve finale du nouvel environnement constate l'absence de ses processus et de ses écouteurs dédiés, la conservation des données/preuves et la préservation du PostgreSQL habituel 5432. Les 39 fichiers structurés scellés du runtime corrigé ont été revérifiés par hash. Une nouvelle phase peut ensuite réutiliser les mêmes numéros de ports dans un autre environnement propriétaire ; la clôture s'applique aux identités et à l'instant enregistrés.

Les dix variantes encore partielles de la campagne précédente sont préparées dans `../winui-journey-completion-20261025/journey-completion-plan.json` : six séquences et 20 sous-contrôles, initialement `PLAN_NON_EXECUTE`. Elles portent sur configuration/redémarrage, authentification réparée, préférences et alternatives, titres limites, streaming/annulation/scroll, admission pendant lecture lente ou dialogue, ambiguïté de source et source avancée après redémarrage. Leur résultat devra venir de nouvelles observations natives et de leurs preuves durables.

Le statut produit reste **`TESTE_NON_APPROUVE`**. La validation des suppressions et de leurs défauts reproduits est maintenant établie dans son périmètre ; le test payant du planning avec Terra ne se déduit pas de ce complément.

## Points d'entrée des preuves

- `delete-execution-matrix-final.json` : projection finale des suppressions, du défaut corrigé et du redémarrage, avec limites et références précises.
- `delete-execution-matrix-observed-red.json` : état précédent conservé, avec reproduction build 08 et correction encore en attente de retest à cet instant.
- `full-unit-production-anchor.json` : résultats unitaires, SHA TRX, inventaire produit et binaires réels.
- `atomicity-causal-proof.json` et `RAPPORT-ATOMICITE-SUPPRESSION.md` : preuve backend avant/après et limites.
- `local-fixture-fixed/delete-refresh-green-state-proof.json`, `delete-refresh-green-selected-probes.json` et `delete-fixed-post-restart-proof.json` : état durable, séquence de sondes et reprise.
- `local-fixture-fixed/owned-stop-fixed-final-proof.json` et `fixed-runtime-closure-evidence-sha256.json` : arrêt vérifié et scellement des preuves structurées.
