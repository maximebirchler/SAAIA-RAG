# Parcours documentaire jusqu’au modèle — 27 octobre 2026

**Le transport contrôlé passe sur Chat Completions et Responses.** Les publications réelles de deux documents — NIST et un recueil de recettes — ont été rejouées dans une base isolée. Une vraie session API déclenche le worker et ses outils de recherche et de lecture. Le fournisseur de test reçoit les véritables requêtes HTTP du produit, puis la réponse est relue dans l’API et PostgreSQL.

La comparaison porte sur les octets effectivement reçus, avant leur décodage JSON. Elle relie chaque requête à sa trace applicative et à sa sonde, puis vérifie les extraits contrôlés, identités documentaires, pages, lignes natives, avertissements, provenance et citations par rapport aux publications d’origine. Le corpus reste inchangé après les lectures.

Plusieurs défauts ont été corrigés pendant cette campagne :

- Des coupures de texte pouvaient séparer un caractère Unicode ; les fenêtres backend et client respectent maintenant ses limites, tout en conservant les offsets UTF-16.
- L’assemblage des fragments Responses pouvait supprimer un espace entre deux fragments ; leur texte est désormais concaténé sans cette altération.
- Des avertissements de tableaux ou de résumés disparaissaient lors des projections. Les diagnostics conservés, les omissions connues et les états inconnus ou malformés sont maintenant distingués.
- Une ancienne lecture pouvait prendre le budget réservé à une lecture plus récente. Les derniers résultats explicitement demandés passent en priorité.
- Le budget documentaire était mesuré avant le dernier échappement JSON. Il est maintenant calculé sur la représentation envoyée ; les copies strictement identiques d’inventaires utilisent une référence explicite vers leur exemplaire complet dans le message courant. Une différence d’identité, de fenêtre ou de qualité conserve le contenu complet.
- Un pointeur RAW syntaxiquement valide mais non observé pouvait faire échouer le job au niveau du Reader. Le lot est maintenant refusé avant exécution, avec un retour permettant une correction, également dans la voie JSON historique.
- Après une projection partielle, deux curseurs visibles pouvaient désigner des positions différentes. Ils désignent maintenant tous deux la reprise après les lignes réellement admises dans le message. Le Reader et les données d’origine restent inchangés.
- Les sorties Chat interrompues pouvaient être refusées sans conserver leur trace exacte ; ces refus sont désormais tracés.

Les budgets restent identiques : 14 000 caractères pour la surface documentaire finale et la réserve existante de 7 000. Le maximum constaté dans les six campagnes finales est de 13 147 caractères. Une omission imposée par le budget reste déclarée ; elle ne devient pas une preuve d’absence dans le document.

Le faux fournisseur demande d’abord une fenêtre native de deux lignes. Si une seule est admise, il suit exclusivement le curseur réellement reçu pour récupérer la dernière. Le vérificateur exige toujours les trois lignes d’origine au terme du parcours et refuse les curseurs contradictoires. Il contrôle aussi une lecture primaire à offset non nul, une page légère lue avec succès et une lecture de page dépassant la limite de résultats demandée, explicitement refusée avant admission.

| Contrôle | Résultat |
|---|---|
| Parcours positifs, deux documents × deux protocoles | 4 réponses contrôlées, 40 POST |
| Pointeur RAW erroné puis corrigé | 4 parcours réussis, 44 POST ; aucun mauvais pointeur exécuté par le Reader |
| Citation inventée maintenue pendant réparation | 4 refus attendus, 44 POST ; aucune réponse persistée |
| Intégrité des six campagnes finales | 128 corps HTTP = 128 traces exactes = 128 sondes ; aucun compteur de diagnostic perdu ou tronqué |
| Vérificateur volontairement trompé sur des copies | 14/14 altérations détectées au motif exact, originaux intacts |
| Suites unitaires applicables | 6 236 réussites, 0 échec, 2 exclusions connues |
| PostgreSQL isolé | 418 réussites, 0 échec, 2 exclusions historiques ; cluster temporaire supprimé, port fermé, environnement restauré |
| Client WinUI de production | Release x64, options de test désactivées, 0 erreur et 0 avertissement |
| Coût fournisseur payant | 0 |

Les deux exclusions unitaires sont un test exigeant de vrais fichiers Linux différenciés par la casse et un test OCR réel soumis à activation explicite. Les deux exclusions PostgreSQL sont d’anciens oracles de récupération et de rédaction déterministe, déjà désactivés avant cette campagne. Les tests client, cœur et contrats avaient déjà passé sur leurs sources finales ; leurs 959 fichiers ont été revérifiés inchangés. Le backend complet a été rejoué après les dernières corrections. Les 649 tests ciblés fournisseur/table font partie de cette suite : ils ne sont pas ajoutés une seconde fois au total. Les profils unitaires et PostgreSQL totalisent 6 654 réussites et quatre exclusions, sans échec.

Les quatorze altérations couvrent les octets, le protocole, la qualité native, les références natives, la trace, la citation finale et les arguments liés aux appels d’outils, sur chacun des deux protocoles. Pour les altérations de qualité, de références et d’arguments, les liaisons SHA/trace/sonde sont recalculées avant vérification afin de ne pas se contenter d’un échec au premier contrôle de transport. Cela éprouve ces corruptions précises ; ce n’est pas une preuve d’absence de tout défaut possible du vérificateur.

Quatre contrôles courts supplémentaires, effectués sur une version antérieure explicitement identifiée, restent séparés : source falsifiée refusée avant tout appel fournisseur et sortie interrompue refusée malgré un JSON lisible. Ils représentent huit cas et huit POST. Ils ne sont pas présentés comme des exécutions de la DLL finale. Les anciens échecs, les preuves RED puis GREEN et les défauts d’outillage corrigés restent conservés localement.

**La portée reste bornée.** Cette campagne rejoue des publications existantes ; elle ne refait pas l’OCR. Elle utilise le véritable backend en mode Development dans une configuration de test isolée, avec PostgreSQL réel et les chemins SQL/canoniques du produit. TEI et Qdrant sont des adaptateurs déterministes de test : la pertinence vectorielle n’est pas qualifiée ici. Le transport client est réellement exercé sur socket local ; l’interface WinUI n’a pas été réouverte pour cette campagne. La récupération d’une fin de chunk auparavant invisible n’est pas démontrée par ces deux documents, dont les chunks choisis font moins de 2 400 caractères ; les limites et caractères sont aussi éprouvés par les régressions ciblées.

Les captures privées restent locales, hors Git. Aucun corpus n’a été envoyé à un fournisseur externe et aucun appel payant n’a été effectué. Les environnements de parcours sont arrêtés. Cette preuve ne démontre ni la compréhension d’un vrai modèle, ni la qualité métier d’un planning, ni la fidélité parfaite de tous les PDF. Les limites documentaires de la qualification d’ingestion précédente restent applicables. Le produit demeure **TESTE_NON_APPROUVE**.

La suite logique est une campagne progressive avec un vrai modèle : examiner d’abord les messages et les outils réellement disponibles, vérifier quelques réponses sourcées, puis élargir aux tâches multisources et au planning. Le harnais permet désormais de distinguer une dégradation du transport d’un problème de recherche ou de raisonnement.

Les empreintes des sources, des DLL, des tests et des reçus locaux sont consignées dans [qualification.json](qualification.json). Les preuves privées ne sont pas reproduites dans ce bilan public.
