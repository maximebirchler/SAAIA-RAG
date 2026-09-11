# A762 — Reprise durable de l'analyse avancée dans WinUI v1

Date de décision : 2026-09-11  
Statut : `ACCEPT_DURABLE_FIRST_SNAPSHOT_AND_RESTART_RESUME_KEEP_LIVE_UI_VALIDATION_OPEN`

## Problème fermé

A761 conservait un état de reprise dans `sources_json`, mais le message
assistant n'était écrit dans le chat store qu'après le retour complet de
l'agent. Une fermeture de WinUI entre la création du job et cette écriture
pouvait donc laisser un job serveur durable sans message client capable de le
retrouver.

A762 rend désormais le premier instantané bloquant pour le polling : après la
création et la validation de l'identité du job, l'état `queued`, `running` ou
terminal est transmis au trajet de persistance avant le premier délai et avant
le premier `GET`. Le message assistant est créé une seule fois. Les instantanés
suivants modifient son contenu et son `sources_json` avec `PATCH` en conservant
le même `messageId`.

## Reprise après redémarrage

Au chargement d'une session, WinUI inspecte les messages assistant et ne retient
que le contrat exact `saaia.advanced-analysis-client-state.v1`. Le parseur exige
des GUID non vides, la session courante, un état connu et une révision positive.
Un état `queued` ou `running` démarre un tracker unique par `jobId`.

Le tracker effectue uniquement des `GET` sur le job existant, exige les mêmes
`jobId`, `handoffId` et `sessionId`, refuse une régression de révision, puis
patche le message d'origine jusqu'à `succeeded`, `failed` ou `canceled`. Une
coupure de transport laisse le message reprenable. La fermeture de la fenêtre
annule seulement le polling local : elle n'appelle jamais l'annulation du job
serveur. Une annulation explicite du tour utilisateur conserve, elle, le
comportement A761 qui demande l'annulation serveur.

## Évolution du contrat de chat

`PATCH /chat/messages/{messageId}` accepte maintenant `sourcesJson` ou
`sources`. L'absence du champ conserve la valeur précédente; un champ `null`
la supprime; une valeur JSON la remplace. L'audit indique seulement quels champs
ont changé et ne copie pas le contenu des sources.

## Preuves exécutées

- compilation WinUI x64 : réussie, zéro avertissement et zéro erreur;
- `AdvancedAnalysisClientTransportTests` : 19/19;
- première persistance vérifiée avant le premier délai de polling;
- reprise vérifiée avec uniquement deux `GET`, sans nouveau `POST` de création;
- arrêt du tracker de redémarrage vérifié sans appel `/cancel`;
- parseur de reprise vérifié sur contrat valide, mauvaise session et mauvais
  schéma;
- transport du patch de `sourcesJson` vérifié sur le même `messageId`;
- suite complète : contrats 10/10, backend 2 113 réussis et 1 probe live
  ignorée, client 2 194 réussis et 1 probe live ignorée, zéro échec;
- format Roslyn et contrôle whitespace : propres.

## Limites encore ouvertes

Le trajet est couvert mécaniquement et compilé dans l'application, mais un test
visuel avec fermeture réelle du processus WinUI pendant un véritable job long
reste à exécuter lorsque le fournisseur avancé sera activé. Aucun fournisseur
réel n'est encore branché et aucune qualité sémantique avancée n'est déduite de
ces tests de transport.

Le test PostgreSQL optionnel du `PATCH sources_json` a été lancé avec la
configuration d'infrastructure locale. Il a bien atteint le PostgreSQL Windows,
mais celui-ci a refusé l'identifiant de la configuration fournie. L'assertion du
nouveau payload est présente dans le test d'endpoint, mais l'exécution du SQL sur
PostgreSQL réel n'est pas requalifiée comme réussie dans cette passe. Cette
limite ne concerne pas les tests A762 de transport, reprise et arrêt local.

La reprise rend les réponses d'erreur dans la langue d'interface configurée au
redémarrage. Le texte final réussi vient du résultat serveur et n'est pas
retraduit côté client.
