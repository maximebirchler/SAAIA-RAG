# Comparaison de la boucle native — A826, préenregistrement

## État mesuré avant modification

A825 sur `96ced458`, job `c1f99926-e8b3-483e-b65d-f8ccf018f74d` : sept
appels, 0,3754115 USD. Le contrat Responses natif fonctionne. Vingt-six
recherches exécutées, aucune lecture physique ni recherche littérale. Le
Writer choisit huit recherches natives, puis déclare l'insuffisance avec
cinquante-deux observations visibles alors qu'un nouveau tour de recherche
reste autorisé. Critic invalide, réparation vers une insuffisance finale
sans citation. Job succeeded techniquement ; planning rejeté sémantiquement.

La comparaison C1 est close : deux pilotes, 0,479344 USD cumulés, sous 1,50 USD,
plus 0,000838 USD de sonde synthétique acceptée. Premier pilote refusé avant
opération native ; deuxième insuffisant. Les sondes refusées sont gratuites.
Aucun bénéfice suffisant démontré pour le planning. Pas de troisième pilote C1.

La perte de candidats ou de corps entre les tours n'est pas encore établie
causalement. Le budget actuel comporte deux ResearchReview avant Writer. Le
nombre de tours disponibles pour une recherche après rédaction est limité,
bien que le modèle ait choisi de s'arrêter avant de consommer le dernier.
Tester d'abord l'allocation de ces appels, sans ajouter une mémoire d'éléments
dans le même changement. Les preuves sources, usages, paramètres et ressources
clos sont conservés ; aucun job possédé non terminal.

## Hypothèse C2a

Conserver le Planner initial et sa décision de sélection. Après exécution de
ses recherches, passer directement à la boucle de synthèse native. Le modèle
choisit ses fonctions et sa réponse ; une critique finale reste prévue. Les
deux ResearchReview ne sont plus des phases séparées dans ce mode configuré.

Même plafond global de sept appels : les appels économisés deviennent
disponibles à la synthèse. Aucun appel additionnel, outil imposé, ordre de
recherche fixe, contexte augmenté, recette connue ou règle de créneau.
La synthèse, son retour outil compact, le raisonnement low, le parseur,
l'EvidenceBundle et les contrôles restent ceux de A825.

Mode `NativeResearchTopology=agent` optionnel ; défaut `reviewed`. Il ne
s'applique qu'avec les outils natifs activés. Configuration indépendante de
la localisation du modèle, installateurs différés. Le chemin non natif
conserve son fonctionnement. Le modèle peut toujours demander clarification
ou indiquer une insuffisance honnête, sans transformer ses limites en absence
démontrée du corpus.

## Mesures et arrêt

Même demande publique 5 × 4/corpus/runtime, Responses natif, critique,
raisonnement low, sept appels, HTTP une tentative, 0,75 USD par pilote.
Maximum deux pilotes C2a, 1,50 USD cumulés, arrêt après premier échec sans
nouvelle hypothèse enregistrée. Relire les demandes réellement choisies et
les associations finales : vingt propositions valides ne prouvent pas vingt
recettes. Mesurer opérations, passages visibles, coût, durée et résultat
durable. Un gain de tours disponibles ne garantit pas leur bon usage.

Si une réponse passe, figer et exiger trois réussites consécutives sur ce
candidat avant holdout inédit et WinUI. Si C2a ne suffit pas, préenregistrer
C2b avec une mémoire typée des éléments choisis par le modèle et leurs
preuves actuelles ; ne pas la mélanger à C2a. Le produit demeure
`TESTE_NON_APPROUVE`.

Registre clos : 32,07890890 USD sur 40 USD achetés, reste 7,92109110 USD.
Aucun nouveau financement ni location.

## Implémentation et contrôles avant le pilote

Le mode optionnel est implémenté : le Planner initial est conservé, seuls
les ResearchReview séparés sont omis en mode natif agent. Les plafonds et la
critique finale restent inchangés. Le runner expose et trace ce choix.
Une topologie native inconnue est refusée avant tout HTTP ou accès outil.
Le chemin non natif conserve ses revues même si cette option vaut agent.

Cinq nouveaux cas vérifient la séquence et la critique pour les deux
transports, le comportement non natif et le rejet de configuration.
Suite backend Release : 2 348 réussites, aucun échec, trois ignorés.
Cela valide le contrat technique ; aucun résultat réel C2a n'est encore acquis.

## Résultat réel C2a et arrêt

Version `95bebd84`, job `896452bf-c288-48f5-a284-dee32f04e687` : six appels,
0,2666888 USD. Quinze recherches, aucune lecture ni recherche littérale.
Writer choisit deux tours puis s'arrête avec deux tours encore possibles.
Critic et récupération aboutissent à neuf options, aucune grille complète.
Les neuf attestations sont relues ; neuf cartes passent l'audit physique.
Le déficit de onze options annoncé n'est pas approuvé comme absence du corpus.

Le résultat demeure rejeté. Pas de deuxième répétition C2a. Les projections
perdent 24 puis 11 passages visibles, dont certains corps nommés utiles ;
la causalité de l'échec reste à tester. C2b est préenregistré séparément.
Ressources possédées fermées. Registre : 32,34559770 USD sur 40 USD.
