# A848 — contrôle du retour de limite d'accumulation

Préenregistrement bf7fb40f avant changement. Le gateway garde sa limite et son
refus atomique de nouveaux extraits. Son budget expose maintenant les capacités
réelles d'extraits ; unknown reste null pour les adaptateurs sans information.
L'exception d'accumulation transporte les compteurs maximal, admis et demandés.
Le journal reste failed sans preuve si l'admission est refusée.

La boucle de synthèse transforme uniquement accumulated_evidence_limit_exceeded
en retour opérationnel sans preuve. Les sorties réussies du même batch restent
conservées ; les suivantes sont marquées non exécutées et ne passent pas au
gateway. Chaque call ID reçoit son état honnête. Le diagnostic commun suspend
les recherches, reste visible au Writer et au Critic, et rappelle qu'une limite
opérationnelle ne démontre pas l'absence du corpus. Utiliser seulement la réserve
d'appels existante pour la décision sémantique. Une demande de recherche après
suspension est refusée avant I/O. Pas de hausse de capacité ni de sélection métier.

Les erreurs de révalidation, tenant, scope, persistance et annulation restent
fatales ; le Planner initial conserve son comportement existant. Le seul catch
nouveau filtre le code exact d'accumulation pendant la recherche de synthèse.

Preuves : baseline-v1 échoue dans les deux transports sur la limite fatale, avant
la prochaine synthèse. Après correction, mêmes fixtures avec preuve conservée,
trois sorties (réussite, refus d'admission, non-exécution) et Writer/Critic
terminaux passent ; état opaque simulé intact côté Responses. Quatre cas de
révalidation ou refus initial restent fatals. Six tests provider, plus deux
contrôles réellement exécutés sur PostgreSQL : admission refusée sans nouveau
corpus, compteurs exacts et trace failed vide ; quotas et reprise de bail conservés.
Cluster/PID possédés arrêtés, port 55432 libre, variables restaurées, fichiers
bootstrap retirés. Artefacts dans a671-backend-postgres/a848-evidence-capacity.

102 régressions ciblées réussies ; suite backend 2 424 réussites, aucun échec,
trois live ignorés. La suite sans environnement PostgreSQL ne constitue pas une
campagne PostgreSQL complète : les deux contrôles réels ont leur preuve séparée.
Logs/TRX capacity-a848-* dans a815-completion-audit-20260913.

A847 reste sans terminal ni qualité mesurée : trente opérations réussies et
un refus, 504 IDs uniques admis. Historiques réellement transmis : maximum
14 686 caractères ; l'effet causal live de la borne 32 768 n'a pas été observé.
Registre clos 38,18713150 USD / 40, reste calculé 1,81286850 USD. Un pilote
après version propre figée avec le profil A847, puis analyse avant répétition.
Trois réussites figées restent nécessaires avant inédit et WinUI.
TESTE_NON_APPROUVE, Goal actif, aucun nouvel achat.
