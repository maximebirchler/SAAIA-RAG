# A872 — refus OpenAI du dernier appel Explorer au format JSON

## Verdict

A872 confirme que Terra sait explorer le corpus et constituer l'inventaire
attendu. Le Planner, la recherche initiale, la localisation de titres puis
l'enregistrement de vingt candidats ont tous réussi. L'appel terminal du
Candidate Explorer n'a toutefois pas été exécuté par le modèle : l'API OpenAI
l'a refusé avec un HTTP 400 parce que la requête utilisait
`text.format.type=json_object` sans contenir littéralement le mot `json` dans
ses messages.

Cet arrêt ne mesure donc ni la capacité de raisonnement de Terra ni la qualité
du dossier. Il révèle une précondition syntaxique manquante dans le générateur
central des requêtes Responses.

## Coût et état du budget

A872 a consommé les appels ayant précédé le refus. Le total de l'essai est de
0,26018990 USD. Le registre global contient 1 147 écritures, totalise
41,15279920 USD et laisse 3,84720080 USD jusqu'au plafond autorisé de 45 USD.
Son SHA-256 est
`D090AD530B16CBB2D2B315BCEF6FDFFB586903D190F0CC926F73405B51430914`.

Le refus HTTP 400 n'a créé aucune facturation supplémentaire. Le plafond par
job reste 0,75 USD et aucun achat ou rechargement automatique n'est permis.

## Correction

Le constructeur commun de `CompleteJsonAsync` garantit désormais que le
message système contient le mot `json` lorsqu'il combine le transport natif
Responses avec un résultat `json_object`. Il n'ajoute la consigne que si ni le
message système ni le message utilisateur ne contient déjà ce mot :

```text
Return only a valid JSON object.
```

La correction s'applique à tous les appels JSON natifs Responses et ne modifie
ni la banque, ni le corpus, ni les outils, ni les critères sémantiques. Le test
du Candidate Explorer inspecte la troisième requête, qui est précisément
l'appel terminal sans outil, et impose désormais cette précondition.

## Validation et suite

La suite provider réussit 303 tests sur 303, zéro échec. Le corpus postflight
est identique au préflight, la configuration est restaurée, le backend
temporaire est arrêté et aucun job avancé ne reste actif. Les traces privées
sont conservées sous
`artifacts/reprise-pc-20260908/a872-terra-candidate-explorer-pilot-20260928-183740/`.

La prochaine mesure autorisée est un seul pilote A873 sur l'état corrigé. Elle
doit enfin mesurer le passage du dossier Explorer propre vers le Writer au lieu
de répéter un défaut de protocole. Le produit reste `TESTE_NON_APPROUVE`.
