# A843 — budget documentaire réel et erreur conservée à sa frontière

Préenregistrement avant modification produit et nouveau pilote.

A842 est clos sur 31b6e096, job a38390a4-b08f-4436-9f18-b72c46d232bb :
sept appels, 0,43543640 USD, vingt recherches, neuf lectures, trois recherches
littérales, trente-deux événements réussis ; aucune réponse finale ni résultat
publié. Dernière erreur durable tool_trace_persistence_failed. Ressources closes,
version stable. Registre clos 36,33177790 USD / 40, reste calculé 3,66822210 USD.
Le prompt sémantique A842 n'est donc pas évalué sur une grille finale.

Le dernier retour du modèle demandait quatre opérations alors que deux places
restaient. Les deux premières réussissent ; la troisième atteint la limite
réelle de trente-deux opérations. SearchAsync refuse avant admission, mais son
catch tente de persister un trente-troisième événement avec le même plafond.
Le store refuse cette place et masque tool_call_limit_exceeded par une erreur
de persistance. Ce diagnostic est établi par la séquence close et le code,
la reproduction contrôlée précède le correctif.

Le prompt transmet les appels LLM restants, pas le budget du gateway documentaire.
Intervention généraliste : exposer un instantané du gateway avec maximum, appels
consommés/restants et temps consommé/restant ; ne pas inventer ce budget dans les
adaptateurs qui ne le fournissent pas. Présenter ces valeurs au modèle pendant
la recherche. Refuser atomiquement avant I/O un batch de nouvelles opérations
qui dépasse le reste et utiliser une correction bornée dans la réserve LLM
existante. Aucun clamp, exécution partielle ni appel offert hors plafond.

Préserver l'erreur de quota lorsqu'une requête est refusée avant admission,
sans essayer de la faire passer pour une nouvelle opération exécutée. Les
événements des opérations effectivement admises et leurs exigences de bail
restent inchangés ; pas de hausse du plafond documentaire de trente-deux,
aucun assouplissement de preuve ni décision métier par le backend.

Tests causaux : gateway réel avec store à la frontière conserve le nombre
d'événements et l'erreur d'origine après correction ; mesurer son échec initial
sur une base PostgreSQL temporaire isolée. Batch natif excédant le reste refuse
tout I/O puis laisse le modèle choisir un batch admissible ou une réponse finale.
Reprendre les deux protocoles, les limites zéro/exacte, réserves de correction
et la suite complète. Distinguer les suites mécaniques du jugement sémantique.

Un pilote connu sur nouvelle version propre figée après contrôles, même profil
A837 high/8192, low/512 Planner, douze appels, une tentative HTTP, 1,25 USD max.
Prompt A842 inchangé. Analyse avant toute répétition ; trois résultats acceptés
figés avant banc inédit et WinUI. Aucun achat automatique, TESTE_NON_APPROUVE,
Goal actif. L'essai séparé d'extraction physique de la page 108 a échoué : il
ne prouve pas encore le contenu textuel direct du PDF, distinct de la page/hash
et du texte canonique déjà contrôlés. Préserver cet échec sans le cacher.
