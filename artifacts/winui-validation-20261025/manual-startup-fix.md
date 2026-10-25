# W30 — connexion manuelle disponible quand AutoConnect=false

Le parent a reproduit le blocage dans la vraie fenêtre : profil configuré, AutoConnect=false et ShowAdvancedUi=false ; fenêtre vide avec saisie/Nouveau désactivés, sans action visible de connexion ou configuration. Preuves initiales : `native-ui/09-autoconnect-off-before-fix`.

## Correction ciblée

La fin de `InitializeUserModeAsync` conserve un état de démarrage **déconnecté** lorsque l'agent n'a pas été connecté. La fenêtre affiche une présentation neutre « Client déconnecté », la raison de la connexion manuelle, et les actions **Se connecter** et **Configurer**. Elle ne présente pas l'absence volontaire de connexion comme une erreur réseau.

L'action **Se connecter** appelle `ConnectFromStartupOverlayAsync`, qui utilise réellement `ConnectAsync`. Elle ne change pas la préférence AutoConnect. Après succès, la surcouche disparaît et la discussion devient utilisable ; après échec réel, la présentation d'erreur montre la cause et **Réessayer** relance également une connexion explicite, y compris si AutoConnect=false.

Le même chemin conserve l'accès à la configuration quand le premier wizard a été annulé. Il n'essaie pas une connexion sans clé si la configuration est encore requise. Un wizard qui a déjà connecté le client n'est pas suivi d'une deuxième connexion.

Le mode de la surcouche est explicite (chargement, déconnecté, erreur) et conservé lors d'une reconstruction pour changement de thème. Les nouveaux libellés sont disponibles dans les six langues existantes.

## Régression et limites

`ManualStartupConnectionContractTests` vérifie les appels du chemin compilé vers ConnectAsync, l'absence de mutation directe de AutoConnect, le maintien des présentations déconnectée/erreur et les traductions. Ces tests de contrat ne simulent pas le rendu WinUI et ne remplacent pas le clic réel.

Le manifeste `manual-startup-unit/manifest.json` regroupe les tests ciblés W29 et W30. La validation après recompilation de production doit observer **Se connecter**, l'accès **Configurer**, le succès et un vrai échec/retry, puis confirmer que AutoConnect est toujours false. Le binaire de production n'a pas été reconstruit par cet agent pendant que la fenêtre était ouverte.
