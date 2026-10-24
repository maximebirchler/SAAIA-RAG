# Corrections causales du parcours avancé — A898

Date du contexte client : 24 octobre 2026. Branche : `SAAIA_V3.1`.
Référence de départ : `12ccb0b22824883cf01a3bdebbd28006672965ca`.

## Résultat et portée

Les défauts mécaniques identifiés par l'audit ont été corrigés dans le parcours
de recherche avancée. Le modèle dispose désormais d'un accès à la suite des
extraits tronqués, d'une continuité de navigation et d'un cycle de qualification
qui distingue un contenu incomplet d'un contenu réellement écarté.

La consigne de cette étape est respectée : implémentation et validation hors
ligne, puis arrêt avant Terra. Aucun appel à un fournisseur LLM, aucune dépense
API et aucune transmission du corpus à un nouveau fournisseur n'ont été effectués
pendant cette étape. Les échanges HTTP des tests du fournisseur sont simulés.
Les tests PostgreSQL utilisent uniquement un cluster local jetable distinct des
services existants. Le petit modèle n'a pas fait l'objet d'une nouvelle campagne.

Le produit reste **TESTE_NON_APPROUVE**. Une suite déterministe verte ne démontre
ni la qualité du raisonnement réel, ni vingt propositions adaptées dans WinUI.

## Ce qui provoquait les difficultés et ce qui change

| Difficulté observée | Correction conservée | Vérification déterministe |
| --- | --- | --- |
| Plusieurs fragments portant un même titre générique pouvaient être fusionnés dans une même source. | Les observations brutes disjointes gardent des identités distinctes fondées sur leurs preuves. La convergence d'identités reste possible après jugement sémantique. Une première ligne reste une observation de titre, pas une qualification automatique. | Deux corps avec un même intitulé générique ne deviennent pas automatiquement un seul objet validé. Provenance et appartenance à la source restent contrôlées. |
| Un contenu incomplet était rejeté, puis difficile à réexaminer. | États `awaiting_review` et `needs_body`, distincts du rejet sémantique. Une nouvelle preuve ou la lecture d'une nouvelle fenêtre peut rouvrir l'examen. Les références complémentaires récentes sont conservées. | Réexamen après complément, absence de requalification automatique des rejets inchangés, mise à jour correcte des états. |
| Une observation contenant plusieurs objets nommés pouvait n'en produire qu'un. | Le Judge doit évaluer chaque observation reçue et peut extraire plusieurs identités documentées du même lot, avec leurs preuves exactes. | Découpage d'un corps multiobjet, refus d'une identité ou d'une source non documentée. |
| La première projection limitait chaque extrait à 2 400 caractères sans outil pour lire sa suite. | Outil natif `read_evidence(evidenceId, offset, length)`, fenêtres exactes de 256 à 8 000 caractères, sans nouvelle recherche et sans changement d'identité canonique. Les bornes et identifiants sont validés avant exécution du lot. | Lecture effective d'un seuil situé après le caractère 3 000, texte identique au sous-texte canonique, aucun appel documentaire supplémentaire, rejet atomique des lots invalides. |
| Une fenêtre complémentaire pouvait être perdue après interruption. | Checkpoint v2 : fenêtres de lecture et observations déjà traitées sauvegardées. Reprise après revalidation des preuves et des bornes. | Reprise d'une demande directe sans répéter l'appel de lecture ; rejet des fenêtres forgées ou hors limites. |
| Les sommaires disparaissaient lors du recentrage sur les corps. | Mémoire de navigation bornée, distincte des preuves finales ; conservation des fenêtres de sommaire déjà observées. Les sources connues restent appelables par des ancres sans contenu. | Sommaire conservé malgré le changement de projection, toutes les sources observées restent accessibles, les corps omis ne deviennent pas des preuves. |
| Le Navigator repartait sans véritable historique natif. | Conservation des appels et résultats liés, ainsi que des éléments Responses de raisonnement chiffré. Éviction par tours complets, omissions signalées. | Les deux protocoles conservent les liens natifs ; un tour trop volumineux est retiré entièrement, sans message orphelin ni interruption du job. |
| Des micro-lots de qualification consommaient trop d'appels. | Après une première qualification, regroupement limité des très petits lots lorsque le budget permet une recherche supplémentaire. La dernière qualification reste prioritaire. | Regroupement borné, vidage du dernier lot avant Writer, pas de boucle de recherche sans qualification. |
| Le Judge pouvait exiger une procédure intégrale alors que la demande portait sur une proposition simple. | Contrats Judge et Critic alignés sur le contenu nécessaire à la demande. Le LLM juge l'adaptation au rôle ; aucune exigence de procédure complète n'est ajoutée par défaut. | Listes simples et grilles passent par une qualification explicite ; aucun préjugement automatique de pertinence. La pertinence réelle reste à mesurer avec Terra. |
| Les rejets occupaient la même capacité que les candidats actifs ; une saturation pouvait masquer les découvertes. | 64 candidats actifs, 32 rejets historiques ; admissions refusées explicitement signalées. Les mises à jour des candidats existants sont appliquées avant les admissions nouvelles. | À saturation, les qualifications antérieures sont conservées ; un rejet peut libérer une place ; aucun dépassement silencieux des 64 actifs. |
| Un arrêt à saturation pouvait empêcher de finir un corps déjà connu. | Une seule opportunité de réparation locale via `read_evidence`, sans admission de nouvelles identités, puis arrêt borné explicite si nécessaire. | Réparation réussie, absence de progrès, maintien d'un plan déjà complet. |
| Les réserves annoncées n'étaient pas recherchées une fois les vingt affectations obtenues. | Une opportunité facultative de navigation/qualification pour les réserves, dans le budget existant. Les vingt affectations valides sont préservées. | Arrêt facultatif ou complément utile sans dégrader une grille déjà complète. |
| Une recherche explicitement limitée à une source héritait des plafonds de diversité de la découverte globale. | Plafonds 8 résultats/document et 2/page conservés pour la découverte globale structurée, retirés pour une recherche délibérément limitée par source ou document. `topK` reste borné. | Une requête ciblée `topK=40` ne reçoit plus ces plafonds implicites ; une découverte globale les conserve. |
| Le contexte de section ingéré n'était pas transmis. | Résolution des titres et chemins de section canoniques. Le modèle reçoit ces repères et l'existence des voisins ; les identifiants techniques des chunks voisins restent internes. | Lecture PostgreSQL réelle des métadonnées, corps et identité inchangés, isolation tenant conservée. |
| Une recherche littérale trouvait un titre sans le reconnaître si sa casse différait. | Comparaison cohérente avec la recherche insensible à la casse ; l'orthographe rendue provient du texte source. | Recherche en minuscules, titre récupéré avec la casse du document. |
| Des identifiants de claims arbitraires pouvaient contourner la vérification des rôles d'une grille. | Ensemble exact des coordonnées attendues avant la liaison aux rôles ; unicité des identifiants pour les listes simples. | Refus des identifiants inconnus, répétés ou mal numérotés ; conservation des listes non structurées valides. |
| Le Critic pouvait remplacer une réponse valide par une substitution mécaniquement invalide. | Consigne de correction ciblée et conservation des choix étayés. Le fallback déjà borné restaure aussi les preuves/projections du Writer lorsqu'il doit conserver son résultat valide. | Conservation de la proposition et de ses preuves ; une insuffisance honnête reste une issue autorisée. |
| Un solde fournisseur épuisé était traité comme une limitation temporaire. | Codes de quota/facturation explicites classés terminaux, sans répétition HTTP ni relance du job. Une vraie limitation de débit conserve son traitement temporaire. | Quota terminal arrêté au premier refus ; limitation transitoire encore réessayée ; refus non facturé dans le journal local. |
| Un lot mêlant mise à jour d'inventaire et lecture complémentaire pouvait annuler la réouverture d'un candidat. | Validation du lot, fusion de l'inventaire, application des fenêtres, puis sauvegarde unique de l'état final. | Deux protocoles et deux ordres d'appels : note finale conservée, état `awaiting_review`, fenêtre exacte et une seule sauvegarde du lot. |

## Bornes et compatibilité

- Le nombre maximal d'appels au modèle et les budgets de recherche configurés
  ne sont pas augmentés pour compenser ces défauts.
- Les états anciens v1 restent lisibles. Toutes leurs preuves sont validées avant
  réduction de l'historique ; un checkpoint historique comportant 37 rejets
  légitimes peut reprendre. Une preuve invalide dans l'historique à réduire reste
  refusée.
- Le checkpoint v2 conserve au plus 256 observations déjà traitées et 256 fenêtres
  de lecture. Son plafond de stockage passe de 64 à 128 Kio, avec retrait prioritaire
  de l'historique rejeté si nécessaire. Les preuves actives ne sont pas supprimées
  silencieusement pour atteindre cette borne.
- La mémoire de navigation conserve au plus 32 fenêtres et 64 000 caractères
  sérialisés, avec omissions explicites. L'historique natif respecte le plafond
  configuré et évince uniquement des tours entiers.
- Les fenêtres sont des projections de texte canonique déjà résolu. Elles
  n'ajoutent aucun fait absent du corpus. Les souvenirs de navigation ne sont
  pas citables comme preuves finales.
- Les décisions de pertinence, d'identité réelle et d'adaptation aux rôles
  restent celles du LLM. Aucune règle de production propre à une recette, un
  document de cuisine ou une réponse attendue n'a été ajoutée.
- Les profils historiques de campagne, les secrets et la configuration des
  fournisseurs n'ont pas été remplacés. Pas de déploiement serveur à cette étape.

## Validation finale

Les résultats finaux et les fichiers TRX sont consignés dans
`artifacts/reprise-pc-20260908/a898-offline-causal-remediation-20261024/`.

| Contrôle exécuté | Résultat |
| --- | --- |
| `dotnet test RAG.sln -c Release --no-restore` | 4 907 réussites, 0 échec, 4 ignorés : Contracts 10 ; Backend 2 559 + 3 ignorés ; Client 2 338 + 1 ignoré. Compilation WinUI incluse. |
| Régression finale du fournisseur + checkpoint | 384 réussites, 0 échec. Appels au fournisseur simulés. |
| Worker, lectures canoniques et endpoints avec PostgreSQL 16.14 réel | 59 réussites, 0 échec. Connexion locale vers le seul cluster créé pour ce run. |
| Checkpoint v2 renforcé, sur un second cluster PostgreSQL isolé | 1 réussite : observations et fenêtres conservées après expiration du bail, reprise par un autre worker et refus de lecture intertenant. |
| `git diff --check` | Aucune erreur d'espacement. |
| Ressources PostgreSQL | Port 55439 libéré après chaque run, fichier de mot de passe supprimé, environnement restauré, binaires inchangés pendant chaque exécution. |

Les 59 tests PostgreSQL recouvrent une partie de la suite complète ; ce ne sont
pas 59 scénarios supplémentaires à additionner au total unique. Plusieurs tests
de base de données de la suite ordinaire retournent sans intégration lorsque la
variable dédiée n'est pas définie : leur validation réelle est établie par le
run isolé, pas par le seul compteur global. Les quatre tests explicitement ignorés
sont les deux sondes verticales live et deux scénarios documentaires runtime.
Le contrôle final du checkpoint ajoute des assertions à un test existant après
la suite complète ; les tests du fournisseur et le test PostgreSQL renforcé ont
été réexécutés sur ce dernier état. Aucun code de production n'a changé après
la suite complète.

## Ce qui reste à démontrer avec Terra

Ces corrections suppriment des obstacles prouvés du code. Elles ne démontrent
pas que chaque décision du modèle sera correcte. Restent notamment la capacité
à reconnaître les vrais objets dans l'OCR, l'adaptation aux créneaux demandés,
la qualité des reformulations, le coût et la latence des recherches longues.
Le niveau de raisonnement et le modèle de la campagne n'ont pas été modifiés ici.

Le Critic produit encore une réponse structurée complète ; sa consigne demande
une correction ciblée, mais ce n'est pas une API de patch imposant techniquement
l'immuabilité de chaque choix. L'association des identifiants aux coordonnées
est vérifiée ; le placement visuel et le sens du texte rendu nécessitent encore
une inspection humaine dans WinUI. Les plafonds opérationnels peuvent toujours
arrêter une recherche : ils doivent être distingués d'une absence documentaire.

La suite logique est un premier essai Terra instrumenté sur un état figé, avec
inspection du contenu réellement envoyé à chaque rôle et du résultat final.
Il faudra contrôler particulièrement la conservation du sommaire, les lectures
complémentaires, les motifs `needs_body`, le rendement des lots de qualification,
les vingt identités distinctes et l'ouverture des sources exactes. En cas d'échec,
on localise la première rupture dans cette trace avant de relancer une campagne.

Ensuite seulement : contrôles simples et hors cuisine, ambiguïtés et hors corpus,
puis répétitions d'acceptation. Trois réussites live consécutives sur état figé et
la validation WinUI restent nécessaires à l'approbation du stress-test.
**Aucun essai Terra n'est lancé automatiquement à la fin de cette étape.**
