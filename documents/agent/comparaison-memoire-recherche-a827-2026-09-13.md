# Mémoire de recherche choisie par le modèle — A827, préenregistrement

## Baseline close

A826, version `95bebd84`, job `896452bf-c288-48f5-a284-dee32f04e687` : six
appels, 0,2666888 USD, quinze recherches sémantiques, aucune lecture ni
recherche littérale. Terra choisit deux tours puis s'arrête avec deux tours
encore disponibles. Le résultat durable est une insuffisance avec neuf
options ; leurs attestations sont relues et leurs neuf cartes physiques
vérifiées. La grille demandée n'est pas livrée. Le déficit annoncé de onze
options dans les sources n'est pas une absence documentaire démontrée.

Entre les trois projections Writer, 24 puis 11 références visibles
disparaissent. Certaines contiennent des unités nommées et des corps utiles,
notamment un sorbet et un poulet au curry. Ce constat porte sur la visibilité,
pas sur une perte du corpus. Il ne démontre pas causalement que cette rotation
explique l'insuffisance. Le modèle ne dispose pas encore d'un état explicite
des éléments qu'il veut conserver. C2a s'arrête après ce premier échec.

## Hypothèse C2b, distincte de C2a

Ajouter un espace de travail typé, optionnel et propre au job. Une fonction
native permet au modèle de remplacer sa liste d'éléments de recherche :
identifiant, nom choisi, rôle proposé, état, motif compact et références
actuellement visibles. Le modèle décide seul des éléments, rôles, états et
opérations suivantes. Aucun nom connu, domaine, créneau ou ordre d'outil ne
figure dans la logique de production.

Le backend vérifie seulement structure, tailles, unicité et présence des
références dans l'observation actuelle du tenant et de la révision. Un lot
mal formé ne déclenche aucune opération documentaire. Cet espace n'atteste
aucun fait : seul le contenu canonique reprojeté peut justifier la réponse.
Les références choisies par le modèle sont prioritaires dans le même budget
de passages, avant la priorité de recherche existante. Un rejet d'élément ne
supprime rien du corpus. Aucune récupération ou lecture automatique.

Maximum 32 éléments, quatre références par élément, état sérialisé limité
à 8 192 caractères. Les textes de mémoire sont bornés. La sauvegarde consomme
le même tour d'appel que les autres fonctions ; un appel de sauvegarde seul
consomme aussi un appel modèle. Tous les appels natifs du lot restent limités
par le plafond existant. Historique natif et transport inchangés.

## Contrôles et pilote

Vérifier les références invisibles ou inventées, clés dupliquées, tailles,
absence d'I/O pour un lot invalide, associations entre appels et retours,
sauvegarde seule, sauvegarde avec recherche, reprojection canonique et mode
désactivé. Défault désactivé ; client local et installateurs inchangés.

Même demande publique, corpus/runtime figés, Responses, topologie agent,
raisonnement low, critique, sept appels, une tentative HTTP. Un pilote au plus
0,75 USD avant toute répétition. Arrêt au premier échec sans hypothèse nouvelle.
Mesurer si le modèle utilise réellement cet espace, les passages conservés,
opérations, associations finales, coût et temps. Ne pas compter un workspace
plein comme vingt réponses valides. Si succès, figer puis trois consécutifs,
holdout inédit et validation WinUI. Produit `TESTE_NON_APPROUVE`.

Registre clos avant C2b : 32,34559770 USD sur 40 USD, reste 7,65440230 USD.
Aucun nouvel achat ni location.

## Implémentation vérifiée avant pilote

`NativeResearchWorkspaceEnabled` est désactivé par défaut. La fonction
`save_research_state` remplace l'état du job ; tous ses champs et références
sont vérifiés avant les opérations du lot. Une sauvegarde est appliquée après
la validation et l'exécution documentaire du lot. Le retour natif est lié à
son identifiant d'appel, quel que soit son ordre parmi les recherches.
La mémoire est reconstruite à chaque job, même sur la même instance provider.

Dix-neuf nouveaux cas couvrent sauvegarde seule pour les deux transports,
ordre des fonctions, IDs invisibles, doublons, tailles, états invalides,
projections selected/rejected, séparation des jobs, désactivation, corpus
vide et non-application d'une sauvegarde lors d'un lot de lecture refusé.
Un test a révélé que la priorité des recherches ne contenait qu'un sous-ensemble
des preuves : la mémoire doit résoudre ses IDs dans l'ensemble canonique
filtré, puis précéder cette priorité. Ce défaut d'intégration est corrigé.
Le test conserve sa condition de visibilité et le plafond de 8 000 caractères.

Build sans avertissement. Suite backend Release finale : 2 367 réussites,
aucun échec, trois ignorés. Ces tests ne prouvent ni l'usage de l'espace par
Terra ni vingt propositions adaptées. Aucun appel réel C2b à ce stade.

## Résultat réel et clôture

Version `503117e6`, job `59a97600-ac7c-44b2-a4af-12c196c072e2` : six appels,
0,3402479 USD. La fonction est réellement offerte, jamais appelée. Quinze
recherches sémantiques, aucune lecture ni recherche littérale. Writer s'arrête
avec deux tours disponibles. Critic partiel, récupération finale en
insuffisance sans citation. Le déficit de sept options annoncé n'est pas
approuvé comme absence dans le corpus.

Aucun gain réel de mémoire démontré, aucune preuve de causalité concernant
la rotation des passages. C2b s'arrête sans nouvelle répétition ; son option
reste expérimentale et désactivée par défaut. Ressources fermées, registre
32,68584560 USD sur 40 USD. Comparer maintenant, séparément, le raisonnement
de synthèse, avec les mêmes outils, contexte et plafond de sept appels.
