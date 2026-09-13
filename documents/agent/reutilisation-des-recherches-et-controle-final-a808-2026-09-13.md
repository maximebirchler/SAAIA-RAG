# Réutilisation des recherches et contrôle final — 13 septembre 2026

L'essai réel A807 sur be969b7d07d6f6a26deb7342aa88b56b637d56b9 échoue avec `advanced_synthesis_research_no_progress`. Les cinq appels réussis coûtent 0,22905120 USD ; 24 recherches documentaires sont enregistrées et aucune réponse finale n'est publiée. Le backend arrête le job dès que le modèle demande uniquement des recherches déjà exécutées. Le détail de cette demande rejetée n'est pas archivé et n'est pas deviné.

Les entrées après le planificateur mesurent 19 663 à 22 085 tokens, contre 45 326 à 50 339 dans l'essai précédent. Les requêtes et le nombre d'appels diffèrent entre ces runs : ce constat ne constitue pas une comparaison appariée du coût total. La compacité fonctionne effectivement au niveau des entrées ; le planning reste rejeté au niveau de la livraison.

## Comportement corrigé

Lorsqu'une recherche tardive est déjà exécutée, le backend fournit au même modèle un feedback explicite et réutilise les preuves correspondantes, sans appeler de nouveau le moteur documentaire. Le modèle peut s'appuyer dessus ou reformuler sa demande. Une seule récupération est autorisée par phase ; une nouvelle répétition sans progrès conserve l'erreur typée. Le plafond global d'appels et la réserve du critique restent appliqués avant la récupération.

Après une recherche tardive, les preuves concernées reçoivent la priorité dans le contexte suivant. Le focus conserve le scope documentaire et les pages, préfère les passages substantiels aux renvois, et répartit au plus 20 observations entre les requêtes, avec au plus trois par requête. Les autres observations restent ensuite candidates au budget JSON de contexte. Le focus du rédacteur est conservé pour le critique. La priorité ne prouve pas la pertinence sémantique d'un passage.

Le contrôle des titres et renvois seuls s'applique aussi avant le retour final, après toute réparation de JSON et toute récupération de synthèse. Une réparation de format ne peut donc plus contourner le contrôle de contenu. Ce dernier garde ne prétend pas valider toutes les relations sémantiques d'un choix.

## Vérifications

- Quatre nouveaux tests de comportement échouent avant correction puis passent : réutilisation de preuves, reformulation, réparation du rédacteur et réparation du critique.
- Deux contrôles supplémentaires vérifient le scope document/page, la préférence pour le contenu, le plafond de 20 observations et la diversité entre requêtes.
- L'ancien test de répétition vérifie désormais l'échec après le feedback unique, sans deuxième recherche identique. Les erreurs de source inconnue et de réserve d'appels restent immédiates.
- `backend-research-recovery-final.trx` : 2 277 réussites, aucun échec et trois tests live ignorés.
- Les replays `meal-a808-unfocused-prompt-replay.v1.json` et `meal-a808-focused-prompt-replay.v1.json` utilisent les 24 recherches A807 consommées, le resolver réel en lecture seule et les quatre dernières requêtes réellement enregistrées. Le focus rend notamment visible le corps de l'omelette page physique 62. Le writer reconstruit reste d'environ 66 600 caractères avec le budget complet. Cette reconstruction ne reproduit pas la demande rejetée inconnue et ne constitue pas une réponse réelle du modèle corrigé.

Tous les artefacts sont dans `artifacts/reprise-pc-20260908/a800-meal-planning-continuity-20260913`. Les crédits calculés après A807 sont de 1,41377770 USD. Aucun nouvel achat, aucune ingestion, aucun déploiement et aucun changement d'installateur. Un essai réel après gel du code doit encore vérifier la récupération et les vingt choix distincts du scénario signé. Le résultat historique aveugle reste 1/24 et le produit TESTE_NON_APPROUVE.
