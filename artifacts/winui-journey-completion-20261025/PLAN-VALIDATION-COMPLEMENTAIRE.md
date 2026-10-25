# Compléter les parcours WinUI

Ce document prépare **six séquences pour les dix parcours partiellement couverts** dans la matrice précédente. Leur état initial est `PLAN_NON_EXECUTE`. Les suppressions W09/W10 appartiennent au complément séparé `winui-delete-validation-20261025` ; elles ne sont pas rejouées par ce plan.

| Séquence | Parcours | Ce qu'il reste à constater dans la vraie fenêtre |
| --- | --- | --- |
| J01 | W02, W04 | Annuler l'assistant sans enregistrer les changements, redémarrer le même profil, puis corriger une clé refusée dans la même instance. |
| J02 | W05, W25 | Conserver l'ordre des URL alternatives et les choix de langue/thème après redémarrage ; observer l'URL réellement utilisée et les états d'affichage inspectés. |
| J03 | W08 | Renommer une discussion inactive avec un titre vide, puis à la limite de120 caractères, sans modifier la discussion chargée. |
| J04 | W15, W27 | Voir les deltas, remonter volontairement sans saut forcé, revenir en bas, annuler les deltas déjà visibles et réussir le tour suivant. |
| J05 | W16 | Tenter les actions clavier pendant un GET lent et derrière un dialogue ; constater absence de nouvel envoi ou changement de discussion, puis récupération. |
| J06 | W20, W21 | Refuser ou faire choisir une révision ambiguë, puis retrouver après redémarrage la source d'un résultat avancé terminal et ouvrir la bonne page. |

Le fichier `journey-completion-plan.json` donne les sous-contrôles, les critères d'acceptation et les preuves nécessaires. Chaque statut PASS futur doit citer une observation native explicite de l'opérateur, ses captures ou arbres, l'état durable ou le protocole correspondant et le binaire/profil réellement employés. Les contrôles offline de l'outillage et les tests unitaires restent des preuves distinctes.

L'outillage préparé utilise seulement un backend PostgreSQL isolé et des fournisseurs HTTP déterministes loopback. Le GET lent cible un UUID explicite et un compteur borné ; le fournisseur avancé sourcé cite l'identifiant opaque qu'il reçoit réellement. L'ambiguïté comprend une identité concurrente synthétique déclarée, pas une nouvelle révision canonique prétendument ingérée.

Une variante non exécutée conserve ce statut. Par exemple,403 ou une401 tardive ne deviennent pas des observations natives grâce aux seuls tests unitaires ; l'apparence de toutes les cartes dans tous les thèmes n'est pas déduite d'un échantillon. Les captures contenant des données privées restent exclues des preuves publiques.

Aucun appel Terra, aucun test de planning de repas et aucune approbation globale ne sont réalisés ou autorisés par ce plan. Le produit reste `TESTE_NON_APPROUVE`.
