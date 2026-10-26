# Scellement de l’outillage AUTO et du tri retrieval

Le lot est gelé après contre-revue des deux remarques concrètes. La preuve
`tests/automatic-ingestion-offline-v2/manifest.json` porte 42/42 PASS, aucun échec,
erreur ou skip et un inventaire de 21 fichiers strictement identique avant/après.
Il s’agit exclusivement de tests purs et de transports simulés ; aucune
qualification de service réel n’est revendiquée.

La première remarque portait sur les noms d’overrides d’environnement .NET,
insensibles à la casse. La garde normalise maintenant `__` vers `:` et compare
sans distinction de casse. Les témoins couvrent notamment les variantes
`ingestion__scannerenabled`, `Ingestion__workerconcurrency` et
`INGESTION:WORKERENABLED`, en conservant la garde LLM stricte existante.

La seconde remarque concernait la portée des observations du retour de source :
le scénario ne trace pas en continu chaque instant de disponibilité. Le résumé
porte donc explicitement `terminalPublishedGenerationPreserved=true` et
`continuousPublicationAvailabilityProven=false`. Les états capturés, l’annulation
conditionnelle et la publication fraîche sont des observations runtime ; la
propriété transactionnelle de conservation relève des tests PostgreSQL distincts.

Les 19 tests retrieval vérifient aussi le tri déterministe des historiques de
jobs du même chemin, tout en refusant UUID malformés, chemins absents et doublons.
La forme SQL est bien `jobId`/`docPath` camelCase explicitement construite par
`json_build_object` ; le PascalCase Dapper de l’API est un autre contrat.

Le protocole et ses limites fonctionnelles sont décrits dans le README du nouvel
outil. Le bootstrap opérateur v3 écrit la clé synthétique dans
`private/api-key.txt` ; cette valeur privée n’a pas été lue. Les espaces DB,
modèles, originals PDF et services réels n’ont été ni lancés ni modifiés par
l’auteur. Les runners corpus et lifecycle gelés restent inchangés.
