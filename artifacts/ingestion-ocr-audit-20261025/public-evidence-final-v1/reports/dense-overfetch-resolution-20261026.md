# Fenêtres de candidats denses et plans de publication

État : correction présente, sources/tests gelés, GREEN centralisé en attente. Aucun service, réseau réel, modèle, commande Git ou .NET exécuté par l’agent auteur. Les appels HTTP des tests sont remplacés par des handlers en mémoire ; PostgreSQL est uniquement lancé par le pilote de campagne root.

## Défaut et séparation des responsabilités

La recherche Qdrant renvoyait un top-K puis éliminait les versions/périmètres non courants via SQL. Les points obsolètes occupant ce top-K pouvaient masquer un point actif situé juste après, même si le corpus actif suffisait. La recherche retournait alors zéro candidat sans distinguer cette fenêtre tronquée d’un véritable épuisement.

Le fencing d’écriture est une responsabilité distincte : IDs de points spécifiques au plan, plan publié SQL atomique, propriétaire de tentative non réutilisable et refus du stale worker. L’autre agent possède ces modifications. Ajouter uniquement un champ dans le payload ne protège pas un ID stable écrasé tardivement ; le lecteur ne remplace pas ce fencing.

## Correction de lecture

`DenseActiveCandidateSearch` possède désormais les recherches et leur fenêtre. Le calcul de l’embedding reste une seule opération dans `SearchDenseMatchesAsync`. Chaque tour conserve vecteur et filtres de tenant/document/modèle, augmente seulement la limite, puis refait le postfiltre SQL courant. Le résultat du tour précédent n’est jamais accumulé : une nouvelle publication SQL peut avoir changé le plan actif entre deux tours.

Les bornes sont explicites :

- Au plus trois POST de recherche. Un retry404 consomme lui aussi une tentative ; la vérification/création de collection n’est effectuée qu’une fois.
- Doublement de fenêtre, limité à1024 candidats d’expansion. Si l’appelant possédait déjà une limite initiale supérieure, elle est conservée mais n’augmente pas.
- Une échéance partagée reprend le timeout HttpClient existant lorsqu’il est fini, pour toutes les requêtes et les relectures SQL ; le délai n’est pas multiplié par le nombre de tours. Si ce timeout est explicitement infini, l’annulation de l’appelant demeure la limite temporelle.
- Arrêt dès qu’assez de candidats actifs sont obtenus ou que Qdrant renvoie moins d’éléments que la limite demandée. L’exhaustion se calcule depuis la taille brute du tableau Qdrant, avant décodage/compatibilité/postfiltrage.

Quand une fenêtre pleine perd encore des candidats et que le budget est épuisé, le code `dense_active_plan_candidate_budget_exhausted` est propagé par le mécanisme existant de dégradation. Les candidats actifs partiels restent utilisables avec cette limite explicite. Le même mécanisme alimente la réponse/metrics ; le gateway conserve le retriever dégradé dans ses observations et les prompts existants interdisent de déduire l’absence documentaire d’un canal dégradé. Le nouveau témoin HTTP de cette propagation reste à exécuter.

Les erreurs HTTP/JSON du transport ne copient plus le corps ou un chemin JSON documentaire dans l’exception. Le nombre HTTP demeure disponible via le statut ; les autres erreurs utilisent un code borné.

## Sondes

La sonde `retrieval.dense_active_candidates` expose tentative, limite initiale/finale/maximale, brut Qdrant, décodé, compatible embedding, actif et rejeté. `completed_window_attempt` identifie le dernier tour dont le filtrage SQL est terminé ; les compteurs ne somment pas plusieurs top-K qui se chevauchent. Annulation, échec, succès et plafond dégradé ont chacun une fin distincte. Les deux entrées principales canonical/interactive transmettent leur logger ; aucune requête ni contenu documentaire n’est ajouté à ces champs.

## Preuves et limites

Root a capturé les quatre premiers cas de `DocumentFoundationIntegrationTests.DenseOverfetch` avant le correctif : **4 FAIL sur4** dans `tests/dense-overfetch-red-v1`. Ce manifeste extérieur décrit aussi la création/retrait du cluster isolé ; les résultats comportementaux sont dans son lot de tests associé. Le cinquième témoin PG de réponse HTTP et les six témoins Unit ont été ajoutés ensuite et ne sont pas présentés comme des RED capturés.

Le lot prêt couvre : actif derrière des points obsolètes, plafond et stats, vrai épuisement sans retry, annulation du retry, métriques HTTP, plafond1024,404, remplacement des anciens résultats, refus embedding et confidentialité HTTP/JSON. La contre-revue indépendante de l’agent runtime n’a trouvé aucun défaut matériel dans le delta, sans exécution supplémentaire.

Cette correction ne supprime aucun point Qdrant. Le GC automatique n’est pas annoncé comme sûr : l’ancien cycle de suppression réinitialisait les versions, et la réutilisation d’un plan/ID doit aussi être considérée. Les deletes larges de worker pouvaient atteindre une publication plus récente ; ils sont traités séparément avec le fencing. Même avec l’overfetch, un nombre suffisamment grand d’orphelins peut encore dépasser le plafond ; cette situation devient explicitement dégradée au lieu d’une fausse recherche exhaustive.

L’ingestion complète, le comportement des nouveaux plans et la validation du planning demeurent distincts de ces tests de transport/recherche.
