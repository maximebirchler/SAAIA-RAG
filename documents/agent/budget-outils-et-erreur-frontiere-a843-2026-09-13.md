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

Contrôles exécutés ensuite : base temporaire isolée PostgreSQL 16, test initial
RED (erreur de persistance à la place de l'erreur de limite), correctif puis
PASS avec deux événements durables conservés et erreur de limite intacte.
Chaque cluster est vérifié et arrêté, port 55432 libre, variables restaurées.
Douze scénarios provider passent en Responses et Chat Completions : batch
excédentaire totalement refusé puis corrigé par le modèle, admission exacte,
zéro appels ou temps restant, aucun appel de correction hors réserve, premier
batch du Planner refusé atomiquement. Les outils natifs sont déjà indisponibles
si researchAllowed est faux ; ne pas changer le protocole ou l'effort par phase.
Suite complète finale : 2 410 réussites, zéro échec, trois live ignorés. Les
échecs intermédiaires de compilation/fixtures et de suite sont conservés ;
ils ne sont pas présentés comme une reproduction causale du défaut gateway.

Correction de diagnostic après lecture directe et examen visuel de la page 108
du PDF original, hash identique : la liste des ingrédients autorise « poulet ou
de dinde ». Le fragment modèle omettait cette information. Retirer la conclusion
d'incohérence source et conserver l'évaluation A841 v1, remplacée par v2 :
rejet pour une catégorie ouverte C19 ; dix-neuf choix nommés documentés. Ne pas
prétendre que le modèle avait vu les ingrédients supplémentaires. A842 reste
une demande généraliste d'investigation du contexte d'un désaccord apparent.
Copie et rendu privé non versionnés, aucune modification du corpus ni appel API.

Amendement avant correction de reprise, 19h43 UTC : le budget d'opérations
est partagé avec le compteur durable, qui survit aux tentatives, tandis que
le compteur gateway local repartait à zéro. Restaurer appels et temps consommés
depuis l'historique déjà chargé et revalidé par le worker. Adapter la factory
sans inventer un budget dans les gateways de test qui ne le fournissent pas.
Étendre le contrôle PostgreSQL à un bail expiré : le gateway repris conserve
le reste zéro et n'écrit pas une nouvelle opération refusée. Aucun nouveau
pilote ni nouvelle attribution causale avant cette vérification et la suite.

Contrôle de reprise terminé : deux tests PostgreSQL réels réussis sur nouveau
cluster isolé, dont frontière et compteur/temps restaurés ainsi que la reprise
canonique historique. Le premier essai de cette extension omettait la récupération
du bail expiré avant revendication ; fixture corrigée, échec conservé. Suite
complète après modification worker/factory : 2 410 réussites, zéro échec, trois
ignorés (tool-a843-backend-full-v3). La reproduction RED initiale concerne le
masquage d'erreur ; pas de prétention à une mesure RED antérieure de la reprise.
Un budget nul empêche aussi le batch initial d'un job repris ; aucun résultat
answered après reprise à budget nul n'est démontré par ces tests. Les informations
de budget sont présentées pendant la synthèse/recherche et les batches initiaux
restent contrôlés mécaniquement avant I/O.
