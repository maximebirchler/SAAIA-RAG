# A849 — retour corrigible de limite par fenêtre de lecture

Préenregistrement avant changement produit, contrôles et pilote.

A848 (8dfb030f, cbdaf115-b9bd-48b1-8879-3ff53ddc47a2) : cinq appels,
0,26274510 USD, seize opérations réussies (douze recherches, quatre finds),
puis lecture refusée sur canonical_read_window_result_limit_exceeded. Le
modèle corrige son premier batch de pages, puis demande une fenêtre valide
136–139 avec topK 30. La requête SQL trouve au moins trente et un chunks ;
aucun n'est admis pour cette lecture. Ce n'est pas une limite d'accumulation,
donc aucun effet live de son feedback A848 n'est mesuré. Aucun terminal/save/
critique, aucune qualité des vingt repas. Registre 38,44987660 USD / 40,
reste calculé 1,55012340 USD, ressources closes.

Cette limite locale est corrigible par une requête choisie par le modèle :
topK supérieur dans la borne déclarée ou fenêtre plus petite. Ne pas tronquer
les chunks, augmenter les limites ou faire cette correction à sa place.
Retourner une observation typée sans preuve, requestedTopK et nombre de chunks
observés au moins (LIMIT topK+1 ne donne pas le total). Le gateway conserve
son refus atomique et l'événement failed. Vérifier l'identité de source avant
de classer ce refus comme une limite locale : une modification d'identité reste
fatale, même si le résultat dépasse également topK.

La recherche native de synthèse transmet ce refus par call ID et poursuit les
autres opérations valides du batch. Cette limite ne suspend pas toute la
recherche, contrairement à l'accumulation A848. Retirer cette tentative échouée
du registre des recherches réussies permet au modèle de corriger le même
intervalle avec un autre topK. Chaque nouvelle tentative reste chargée aux
plafonds existants de douze appels modèle et trente-deux opérations ; aucune
nouvelle réserve, retry HTTP ou correction automatique. Les états opaques,
preuves, identités et sorties réussies restent intacts.

Ce seul code local est récupérable quand un retour natif lié est disponible.
Le Planner initial et le chemin JSON sans sorties natives conservent leurs
refus existants pour cette limite locale. Accumulation A848 conserve sa
suspension globale ; sécurité, révalidation, persistance et annulation restent
fatales. Contrat outils : préciser que topK borne tous les chunks de la fenêtre,
et qu'un refus demande une fenêtre plus petite ou un topK autorisé plus grand.

Contrôle causal dans les deux transports : fixture fatale avant changement,
puis trois sorties honnêtes (succès, refus, succès), demande corrigée par le
modèle sur le même intervalle, véritable nouvel I/O, terminal prouvé et Critic.
Contrôle PostgreSQL réel : dépassement topK sans nouvelle preuve, compteurs
observés, lecture corrigée qui réussit ; identité changée toujours fatale avant
classification du dépassement. Régressions natifs/ressources/budgets, suite
complète après contrôles ciblés, ressources propres arrêtées.

Un pilote connu après gel propre, mêmes enveloppes A848 : historique 32 768,
512 extraits, trente-deux opérations, high/8192, low/512, douze appels,
une tentative HTTP et 1,25 USD/job maximum. Lire le résultat avant répétition.
Aucune réussite actuelle ; trois consécutives figées exigées avant inédit et
WinUI. TESTE_NON_APPROUVE, Goal actif, aucun achat par l'agent.
