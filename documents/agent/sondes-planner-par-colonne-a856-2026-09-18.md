# A856 — sondes Planner par colonne

A855 montre qu'un job borné à trois appels trouve trois petits-déjeuners bien
prouvés, puis refuse correctement d'en inventer quatre. Son Planner a proposé
six recherches génériques pertinentes, mais le Writer n'a disposé que d'un tour
de recherche supplémentaire. Le registre laisse 0,02950240 USD.

A856 utilise ce reliquat pour observer séparément la stratégie initiale du
Planner pour les trois autres colonnes du planning : déjeuner, collation et
souper. Chaque cas demande exactement sept candidats avec titre exact et preuve
de corps, mais le job est volontairement limité à un seul appel fournisseur.
Le résultat attendu du job est donc un arrêt `advanced_external_budget_job_call_limit`
avant Writer ; seules les requêtes Planner et la collecte documentaire locale
sont étudiées. Ces sondes ne produisent aucune réponse utilisateur validable.

Trois jobs au plus sont autorisés, un par colonne, avec Planner plafonné à 512
jetons, transport Responses et modèle Terra. Le plafond cumulé reste 40 USD et
le plafond par job 0,02950240 USD. Le garde doit empêcher automatiquement tout
appel qui ne tient plus dans le reliquat. Aucune recharge n'est autorisée.

Le contrôle compare la diversité, la spécificité, les langues et l'usage de
termes de navigation des requêtes. Il sert à décider si la prochaine version
doit conserver un Planner libre ou lui fournir un inventaire déterministe par
colonne. Il ne valide ni les candidats, ni le planning 5 x 4. Le produit reste
`TESTE_NON_APPROUVE`.
