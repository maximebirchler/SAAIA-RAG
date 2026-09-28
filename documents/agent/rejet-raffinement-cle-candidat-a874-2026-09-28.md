# A874 — Terra suit les sommaires, puis le merge refuse son identité raffinée

## Verdict

A874 démontre l'effet attendu de la clarification de navigation. L'Explorer
n'est pas resté sur des requêtes générales : il a appelé `find_source_text`
dans les sources observées pour `Muffins aux myrtilles`, `Barres de céréales`,
`Crêpes épaisses fourrées`, `Fruits en beignets`,
`Omelette aux pommes de terre` et `PORRIDGE`.

Le job s'arrête ensuite avant le Writer avec
`advanced_synthesis_research_protocol_invalid`. La réponse fournisseur était
valide et contenait quatorze mises à jour d'inventaire. Le rejet provient du
merge local, pas de Terra ni d'OpenAI.

## Cause exacte

L'inventaire automatique contenait déjà la clé opaque d'un corps sous son titre
de carte `Les pâtes`. Après la nouvelle consigne, Terra a relu le même corps et
a correctement envoyé :

- la même clé opaque ;
- la même source ;
- le même EvidenceId de corps ;
- l'identité complète `Timbale de pâtes`, visible comme première ligne exacte ;
- le rôle Déjeuner.

Le parseur a validé l'identité complète depuis le corps. Le merge exigeait
encore que toute mise à jour d'une clé conserve le même titre normalisé. Il a
donc refusé précisément le raffinement que le nouveau contrat demandait au
modèle. Les treize autres candidats contrôlés avaient eux aussi des sources,
rôles, EvidenceId et identités cohérents.

## Correction

Une clé existante peut désormais remplacer un sous-titre par une identité
complète seulement si la source reste identique et si l'ancienne et la nouvelle
entrée partagent au moins un même EvidenceId de corps. Le nouveau titre a déjà
été vérifié par le parseur contre le contenu visible. Sans corps partagé, le
changement d'identité reste refusé. Les variantes qui ne diffèrent que par un
glyphe ou la ponctuation conservent toujours le titre canonique du serveur.

Un test causal reconstruit exactement `Les pâtes` vers
`Timbale de pâtes` sur la même clé, la même source et le même corps. Le dossier
atteint Writer puis Critic. La suite provider réussit 305 tests sur 305.

## Coût et intégrité

A874 a consommé quatre appels, pour 0,26732440 USD. Le registre global contient
1 157 écritures, totalise 41,88678500 USD et laisse 3,11321500 USD jusqu'au
plafond de 45 USD. Son SHA-256 est
`029F2DF85D898AFBE3D4DBFBF9F365EF7BEB59A79FE1A25448572617BC0465AA`.

Le corpus avant/après conserve le SHA-256 composite
`7B119CDB072F909236CF3291790B0E287ECCEF48716FC97E338CE8C7EFB798E2`.
Le backend temporaire est arrêté, les ports sont libres et aucun job avancé ne
reste actif. Les quatre traces privées sont conservées sous
`artifacts/reprise-pc-20260908/a874-terra-candidate-explorer-pilot-20260928-165615/`.

Le prochain pilote A875 est autorisé sur ce contrat rendu cohérent. Le produit
reste `TESTE_NON_APPROUVE`.
