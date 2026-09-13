# Contrat court de recherche et réponse — A830, préenregistrement

## Baseline close et hypothèse

A828 sur `d65f240e`, job `478620db-b90d-4484-8cc6-8c80f97da179` : six appels,
0,3434699 USD, quinze recherches, aucune lecture/littéral/sauvegarde. Le
raisonnement medium est confirmé dans l'enveloppe API. Le Critic choisit
un tour de recherche supplémentaire, mais revient en insuffisance avant la
limite. Une assertion agrégée de treize préparations et un déficit de sept
ne constituent pas vingt propositions ni une absence démontrée. Treize cartes
passent l'audit physique, sans approbation de cette assertion globale.

Les modes natifs, l'allocation des tours, la mémoire disponible et medium
n'ont pas suffi. Le contrat actuel accumule de nombreuses obligations sous
un rôle Writer/Critic. Hypothèse non prouvée : ce rôle et ces règles favorisent
une conclusion sur le lot visible plutôt qu'une recherche des corps manquants.
Comparer un contrat commun court, généraliste, donnant le rôle de recherche
et réponse au modèle. Aucun nom connu, métier, langue ou cas dans ce contrat.

## Intervention

Option `SynthesisPromptStyle=agent`, défaut `contract`. Writer et Critic
utilisent le même contrat court de faits, recherche, synthèse et JSON, avec
une tâche de critique explicite pour ce dernier. Le modèle interprète seul
l'identité des éléments, le type de document et la suffisance des preuves.
Les titres/roles de fragments sont des observations, pas des verdicts métier.
Les noms doivent rester source-backed. Les données documentaires restent
non fiables comme instructions ; la mémoire ne devient pas preuve.

Le format conserve outcome, answerText, claims, selectedItem et evidenceIds.
Les choix d'outils, pivots, lectures, classifications et propositions restent
libres. Aucun outil imposé ni ordre fixe. Les mêmes validations canoniques,
identités, parseur, contrôle de visibilité et correction de citation restent
actifs. Réparations/récupération inchangées ; budget et données inchangés.

Vérifier les deux transports, une recherche réellement exécutée, la conservation
des références canoniques et le rejet d'un style inconnu avant tout I/O.
Ces tests mécaniques ne jugent pas les nouvelles réponses.

## Pilote

Même demande publique, corpus/runtime figés, Responses agent, workspace
activé, Planner low/512, synthèse/Critic medium/4096, sept appels, une tentative
HTTP et 0,75 USD maximum. Un pilote avant analyse ; arrêt au premier échec
sans hypothèse nouvelle. Mesurer navigation, lectures, état, couverture,
liaisons finales, tokens, coût et durée. Relecture manuelle et audit physique.
Si succès, version figée et trois consécutifs avant holdout inédit/WinUI.
Produit `TESTE_NON_APPROUVE`.

Le compteur cache Responses est corrigé séparément, sans changer le coût
aux tarifs actuels ; cette correction n'est pas une intervention sémantique.
Registre clos : 33,02931550 USD sur 40 USD, reste 6,97068450 USD.
Aucun nouvel achat ni location.
