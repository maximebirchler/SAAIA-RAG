# A836 — retour précoce des liaisons et visibilité finale de chaque citation

Préenregistrement avant modification et avant appel réel. Le pilote A835 seul
est différé : la continuité est causalement vérifiée dans neuf tests, mais un
défaut de publication des citations invisibles doit être corrigé avant un
nouveau candidat destiné à l'acceptation. Pas d'appel réel A835 consommé.

ParseResult vérifie l'appartenance au corpus canonique du job. Le contrôle final
d'identité ne garantit pas que chaque ID cité reste dans la projection actuelle,
notamment si plusieurs passages appuient un même item ou si la demande n'est pas
une sélection nommée. Garantir cette visibilité est un invariant mécanique du
contrat de preuves, et non une décision sur la pertinence des documents.

Intervention : contrôler chaque ID cité avant publication, pour tous les
résultats avec affirmations. Une citation invisible ne peut jamais être publiée.
Dans la boucle de recherche, retourner ce défaut au modèle avec sa proposition,
uniquement s'il reste un appel existant après la réserve de Critic. Pas d'appel
supplémentaire ni de preuve réintroduite depuis l'historique.

Une option de retour d'identité, désactivée par défaut, utilise aussi les mêmes
prédicats exacts que la validation finale des sélections distinctes pour prévenir
tôt les noms absents, dupliqués ou non appuyés par leurs citations visibles.
Les règles finales, contenus, outils et limites ne sont pas assouplis. Le modèle
choisit de rectifier sa formulation, changer sa sélection, rechercher, clarifier
ou signaler une insuffisance. Le code ne choisit aucun remplacement.

Tester : citation invisible unique ou additionnelle, résultats simples et grilles,
correction dans la réserve existante, arrêt sans appel disponible, défaut lexical
avec sens documenté, doublons, et absence de recherche/substitution automatique.
Les tests de transport et de continuité restent requis ; le mode historique de
normalisation d'identités reste inchangé quand l'option est inactive.

Un pilote connu sur nouveau candidat propre figé, continuité A835 activée et
retour d'identité activé ; autres paramètres A834 inchangés : Responses
agent/workspace, Planner low/512, Writer/Critic medium/4096, sept appels,
une tentative HTTP et 0,75 USD. La comparaison live comporte deux mécanismes
mécaniques successifs ; leurs effets sont séparés par les tests contrôlés,
pas attribués chacun causalement au seul résultat live. Les préoccupations
sémantiques C15/C20 ne sont pas résolues par ces mécanismes ; relire toutes les
unités et leur portée. Audit physique avant acceptation, puis trois consécutifs
figés avant holdout inédit/WinUI. Arrêt des répétitions si échec.

Registre 33,98436250 USD sur 40 achetés ; aucun nouvel achat ou location.
Goal actif, produit TESTE_NON_APPROUVE.
