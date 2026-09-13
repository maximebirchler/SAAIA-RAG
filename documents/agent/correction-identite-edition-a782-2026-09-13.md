# A782 — conserver l'édition explicitement demandée

Date : 13 septembre 2026. Statut produit : **TESTE_NON_APPROUVE**.

Le diagnostic BH6-024 reproduit une clarification inutile : la demande nomme
explicitement `ISO 13849-1:2023`, mais le catalogue est déclaré ambigu entre
2015 et 2023. La trace montre `exact_match_count=2` et
`multiple_formal_designator_current_catalog_matches`, puis une demande de
choisir l'édition. La précision n'a pas été perdue par le modèle ; le contrôle
d'identité lui a transmis une ambiguïté incorrecte.

Le résolveur retirait les années plausibles de l'identifiant numérique afin de
ne pas utiliser une année seule pour identifier une norme. Il ne vérifiait
ensuite plus l'année explicite du document demandé. La correction conserve
ce contrôle contre les années seules et ajoute la contrainte d'édition sur
l'identifiant formel. Le numéro et l'année doivent se suivre dans le nom, avec
des séparateurs explicites usuels. Une date de publication ailleurs dans le
titre ne prouve pas l'édition demandée.

La correction ne choisit pas l'édition la plus récente : une référence sans
édition reste ambiguë si le catalogue contient plusieurs éditions. Elle ne
change pas le choix sémantique des sources, les index ni le corpus.

Preuve causale hors ligne : huit nouveaux cas, dont sept échouaient avant
correction et un contrôlait l'ambiguïté légitime ; ils passent après correction.
La suite ciblée d'identité/catalogue/résolution comporte 69 réussites et zéro
échec. Elle vérifie les variations de séparateur, l'absence de l'édition
demandée, la date de commentaire sans rapport et l'édition non précisée.
La suite client Release complète passe également : 2 295 réussites, zéro
échec et une sonde live ignorée.

Les tests mécaniques ne prouvent pas que le modèle produit ensuite la bonne
réponse documentaire. La vérification locale du parcours réel est prévue sur
le cas consommé à titre diagnostique, sans serveur avancé ni appel OpenAI.
Ce rejeu ne modifiera pas le verdict historique BH6.

Vérification locale au commit `550ff692e356d67365c0b8453dec24ac3711f070` :
BH6-024 donne une résolution complète et unique, `status=Resolved`,
`exact_match_count=1`, pour la référence intacte `ISO 13849-1:2023`. Aucune
clarification 2015/2023 n'est émise. Le pipeline atteint ensuite le budget
local et propose la capacité avancée, en 23,303 s. Cette preuve valide la
résolution d'identité et la disparition de cette clarification, pas le contenu
d'une réponse avancée. Le journal fournisseur est inchangé, le modèle arrêté
et le port 1234 libéré.
