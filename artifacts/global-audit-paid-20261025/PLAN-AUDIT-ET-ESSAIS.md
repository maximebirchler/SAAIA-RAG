# Audit global puis essais économiques — 25 octobre 2026

Point de départ : branche `SAAIA_V3.1`, commit `1e6b7e51c7a1fd9ae0a57e4b903bff57a71a1bb2`, arbre suivi propre. Les preuves précédentes restent immuables.

La demande actuelle autorise l'analyse globale, des essais avec un modèle payant économique de famille au plus 5.6, puis les corrections établies par l'analyse et les essais. Les anciens documents sont des références historiques, pas des ordres à réexécuter. Aucun achat ni changement de fournisseur n'est prévu.

## Ordre de travail

1. Auditer backend, client, contrats, structure, sondes et harnais. Chaque constat distingue défaut établi, hypothèse à reproduire et limite volontaire. Contrôler les appels réels avant de retirer du code.
2. Figer la synthèse d'audit et les critères de la petite campagne. Examiner les requêtes effectivement transmises, outils offerts, sources/révisions et troncatures.
3. Exécuter un contrôle payant minimal de disponibilité, puis les scénarios discriminants si le compte le permet. Conserver réponses, usage, coût et première rupture. Un test ignoré ne vaut pas une exécution.
4. Corriger les défauts établis et extraire les responsabilités utiles avec des tests causaux hors réseau. Ne pas réécrire les règles métier pour obtenir artificiellement un résultat vert.
5. Rejouer les régressions appropriées et, dans le budget restant, les essais réels utiles. Vérifier le parcours natif si les changements le nécessitent.
6. Arrêter les ressources possédées, vérifier l'isolation, livrer les sources et preuves publiables, puis faire le rapport dans le chat et sur Telegram.

## Modèle et dépense

- Modèle initial : `gpt-5.6-luna`, sous réserve de son accès effectif dans le compte.
- Documentation officielle consultée le 25 octobre : https://developers.openai.com/api/docs/models/gpt-5.6-luna — entrée 0,20 USD/M, entrée en cache 0,02 USD/M, sortie 1,20 USD/M, appels de fonctions et sorties structurées disponibles.
- Plafond incrémental choisi pour cette première campagne : **0,50 USD**, sur les seuls crédits déjà achetés et autorisés. Aucun achat, aucune montée automatique vers Terra/Sol ou la famille 6.
- Le registre historique local est conservé. Sa somme n'est pas le solde fournisseur. Un refus de crédits du fournisseur arrête les essais concernés sans achat ni répétitions aveugles ; les corrections hors réseau restent possibles.
- Une seule instance payante et un registre de campagne explicitement borné. Le journal historique reste une pièce distincte ; un nouveau modèle ne doit pas effacer la consommation passée.

## Isolation et statut

PostgreSQL et données de test dédiés ; aucun changement de `/opt/saaia`, `saaia-repo`, des conteneurs `infra-*` ou du profil client habituel. Les secrets restent dans leur magasin protégé ou dans les seuls paramètres privés de processus. Prompts et réponses exacts sont des pièces privées, distinctes des sondes ordinaires sans contenu.

Le produit demeure `TESTE_NON_APPROUVE`. Une réponse de fixture, une insuffisance sûre ou une compilation réussie ne prouvent pas l'acceptation sémantique du planning.
