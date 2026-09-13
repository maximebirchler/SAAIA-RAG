# A783 — permettre une recherche complémentaire pour un seul fait

Date : 13 septembre 2026. Statut produit : **TESTE_NON_APPROUVE**.

Le serveur disposait d'une phase de recherche adaptative mais l'ouvrait seulement
aux livrables structurés, aux demandes d'au moins quatre unités ou aux
comparaisons. Une question courte transférée après épuisement du budget local
ne pouvait donc pas demander de reformulation après un premier résultat
inadapté. La phase était également sautée si la première recherche était vide.
Ces deux contrôles pouvaient provoquer une insuffisance prématurée, quelle que
soit la capacité réelle du modèle serveur.

La phase est maintenant éligible pour toute mission avancée lorsque la recherche
adaptative est activée. Elle peut examiner une première observation vide. Le
modèle décide toujours `ready` ou `search_more`; le code conserve la limite
d'appels, les budgets monétaires, la déduplication des requêtes et les réserves
nécessaires à la réponse, au critic et à une éventuelle réparation. La correction
n'augmente aucun plafond et ne garantit pas une recherche supplémentaire quand
le plafond ne laisse aucun appel disponible. Le profil de validation avancée
actuel autorise sept appels ; un profil plus petit peut sauter cette phase.

Le prompt exige pour une demande directe un passage soutenant le fait exact,
avant de conclure `ready`. Un index, des résultats hors sujet ou une recherche
vide ne prouvent pas l'absence documentaire. Il rappelle de conserver la
sous-section ou la séquence demandée au lieu d'utiliser un aperçu voisin.

Trois nouvelles simulations contrôlent le mécanisme :

- premier résultat hors sujet, puis nouvelle recherche trouvant la preuve ;
- première recherche vide, puis nouvelle recherche trouvant la preuve ;
- seulement deux appels disponibles : aucun dépassement pour lancer une revue.

Avant correction, les deux premières simulations échouaient et la troisième
passait. Après correction, les trois passent. La suite ciblée provider, worker
et garde budgétaire compte 94 réussites et zéro échec. Une assertion du test a
été corrigée pour lire le JSON réellement sérialisé au lieu de rechercher une
chaîne accentuée dans son encodage brut ; elle vérifie le contenu exact de la
nouvelle preuve reçue par le writer.
La suite backend Release complète passe : 2 196 réussites, zéro échec et trois
tests live ignorés.

Ces preuves sont mécaniques et synthétiques. Aucun appel OpenAI n'a été fait
pour elles. Les réponses TLS, FOMC et OAuth de la banque consommée restent à
rejouer comme diagnostics avec Terra et à réviser contre leurs sources. Le
verdict BH6 reste inchangé ; une nouvelle banque aveugle nécessitera ensuite
un nouveau gel et des corrigés documentaires contrôlés.

Une première reprise live a ensuite été exécutée : les trois réponses restent
rejetées malgré quinze appels estimés à 0,275706 USD au total. Les recherches
complémentaires sont réellement utilisées, mais leurs catégories déduites du
sujet conservent un mauvais périmètre pour TLS et FOMC. OAuth rencontre également
un contrat de sélection distincte inadapté à sa séquence. Le diagnostic et la
correction du périmètre sont documentés dans
`correction-perimetre-recherche-a784-2026-09-13.md`. L'éligibilité de la recherche
est donc prouvée ; son résultat sémantique n'est pas encore validé.
