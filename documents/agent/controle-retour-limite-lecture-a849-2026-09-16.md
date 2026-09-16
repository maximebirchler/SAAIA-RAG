# A849 — contrôle du retour corrigible de limite de lecture

Préenregistrement : `retour-limite-lecture-a849-2026-09-13.md`, commit
`faecb979`. Le pilote A848 s'est arrêté sur
`canonical_read_window_result_limit_exceeded` : la fenêtre 136–139 demandait
`topK=30`, tandis que la requête bornée observait au moins 31 chunks. Aucun
chunk de cette lecture n'a été admis. Il ne s'agissait pas de la limite globale
d'accumulation A848.

Le gateway conserve son refus atomique et transporte maintenant un diagnostic
typé : `requestedTopK`, `observedAtLeastChunkCount` et `stopsResearch=false`.
L'identité canonique de la source est vérifiée avant de classer le dépassement
comme capacité locale. Une identité modifiée reste donc fatale. Aucun résultat
n'est tronqué et aucun plafond n'est augmenté.

Pendant une recherche de synthèse avec sorties d'outils natives, le provider
rend ce refus au call ID concerné, sans preuve. Les autres opérations valides du
batch continuent. La tentative échouée est retirée du registre de réussite afin
que le modèle puisse retenter le même intervalle avec une fenêtre plus petite ou
un `topK` supérieur dans la borne déclarée. La nouvelle tentative consomme les
budgets existants ; il n'existe ni retry automatique ni réserve supplémentaire.
La limite globale d'accumulation A848 continue, elle, de suspendre la recherche.

La récupération est limitée à l'opération exacte `read_source` et à un tour natif
capable de recevoir le résultat lié. Le Planner initial, le chemin JSON ancien
sans sorties natives et tout autre type d'opération conservent un refus fatal.
Le descriptif de l'outil explique que `topK` borne tous les chunks qui croisent
la fenêtre et indique les deux corrections permises au modèle.

Preuve causale : avant changement, la même fixture échoue dans Responses et
Chat Completions sur l'exception brute. Après changement, elle passe dans les
deux transports : succès de la première lecture, refus sans preuve de la
deuxième, poursuite de la troisième, puis nouvelle I/O choisie par le modèle sur
le même intervalle avec `topK=24`, Writer et Critic terminaux. Le prompt suivant
conserve `researchAllowed=true` et ne publie aucune suspension globale.

Quinze contrôles ciblés passent après durcissement : les deux transports, la
correction locale, les chemins qui doivent rester fatals, le comportement global
A848 et le contrôle PostgreSQL déclaré. 302 régressions provider/worker ont
passé avant le dernier durcissement. La suite backend finale, sur les sources
actuelles, compte 2 433 réussites, zéro échec et trois tests live ignorés.

Le contrôle PostgreSQL réel final utilise PostgreSQL 16.14 dans un cluster
jetable possédé. Une fenêtre de deux chunks avec `topK=1` est refusée sans
nouvelle admission et journalisée `failed` avec `[]`. La même fenêtre avec
`topK=2` réussit et admet deux preuves. Après modification du hash de révision,
la lecture redevient fatale sur `canonical_read_source_identity_changed`, même
avec `topK=1`, et n'admet rien. Le cluster est arrêté, le port 55432 est libre,
les variables sont restaurées et les binaires restent identiques pendant le run.
Artefacts : `a671-backend-postgres/a849-canonical-read-limit-v3` et TRX/logs
`read-a849-*` sous `a815-completion-audit-20260913`.

Ces preuves valident le mécanisme, pas la qualité du planning. Aucun appel OpenAI
n'a été exécuté pour A849 à ce stade. Le produit reste `TESTE_NON_APPROUVE` ; un
seul pilote connu sur candidat figé doit être lu avant toute répétition.
