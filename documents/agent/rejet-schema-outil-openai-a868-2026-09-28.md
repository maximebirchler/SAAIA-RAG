# A868 — rejet du schéma d'outil avant inférence Terra

## Verdict

Le premier pilote réautorisé ne constitue pas un échec de Terra. Le Planner a
été exécuté et a produit un plan cohérent, mais l'API OpenAI a refusé la requête
du Candidate Explorer avant toute inférence. La cause est un défaut de notre
contrat d'outil : le schéma JSON de `save_candidate_inventory` contenait
`uniqueItems`, mot-clé que le sous-ensemble de schéma accepté par les fonctions
Responses refuse.

Il serait donc incorrect d'utiliser A868 pour conclure que le modèle ne sait pas
naviguer dans le document, comprendre un sommaire, retrouver des recettes ou
composer le planning. Le modèle n'a jamais reçu l'occasion de réaliser ces
opérations pendant ce rôle.

## Ce qui a réellement fonctionné

Le Planner a choisi le mode `distinct_named_items` et généré huit recherches
complémentaires, deux par rôle demandé. Les formulations couvraient notamment
les petits-déjeuners et leurs familles de recettes, les déjeuners, les
collations et les soupers. Cette sortie est cohérente avec les vingt cellules du
planning et avec le corpus Cuisine contrôlé avant l'essai.

Le préflight avait également confirmé :

- dépôt propre et descendant du minimum enregistré ;
- banque, runtime local, modèle et configuration conformes à leurs empreintes ;
- corpus de référence sain, sans ingestion active, avec 305 documents et
  120 130 vecteurs ;
- présence en lecture seule des vingt corps canoniques historiques ;
- Tier 1 observé et marge budgétaire suffisante.

## Cause exacte et coût

L'API a retourné HTTP 400 avec `invalid_function_parameters` sur
`tools[4].parameters`. Le contexte signalé visait le tableau `targetRoles` et le
message indiquait que `uniqueItems` n'était pas permis. Les mêmes déclarations
étaient présentes sur `selectedRoles` et sur les listes d'EvidenceId ; elles
auraient exposé le même risque.

Deux écritures ont été ajoutées au registre :

- Planner : 1 365 tokens d'entrée, 247 tokens de sortie, succès, 0,006375 USD ;
- Candidate Explorer : requête refusée, zéro token comptabilisé, 0 USD.

Le registre après A868 contient 1 129 écritures et totalise 40,00073560 USD. La
marge jusqu'au plafond autorisé de 45 USD est 4,99926440 USD. Son SHA-256 est
`A5ECF521308FBFC9E1EECF9EF420BF1BC9BD50689CFCB70A615595BE38C287B0`.

## Correction générale

Les trois occurrences de `uniqueItems` sont retirées du schéma envoyé au
provider. La contrainte fonctionnelle reste appliquée par le parseur interne,
qui refuse déjà les doublons lors de la lecture de l'inventaire. La correction
ne spécialise ni Cuisine, ni les repas, ni une banque de test : elle rend le
contrat général Candidate Inventory compatible avec l'API réellement utilisée.

Le test d'intégration du contrat inspecte maintenant le schéma produit pour les
protocoles Chat Completions et Responses et interdit toute réapparition de
`uniqueItems`. La suite ciblée complète du provider réussit : 299 tests sur 299,
zéro échec.

## État final et prochaine expérience autorisée

Le backend temporaire a été arrêté, la configuration locale restaurée et les
ports de test libérés. Le scellement postflight est identique au scellement
initial ; aucun changement de corpus n'a été observé. Les traces privées, audits
et requêtes exactes sont conservés sous
`artifacts/reprise-pc-20260908/a868-terra-candidate-explorer-pilot-20260928-180827/`.

Une répétition du même pilote après commit propre est causalement justifiée :
elle mesure pour la première fois le Candidate Explorer au lieu de répéter un
appel déjà interprétable. Aucun paramètre sémantique, prompt, corpus, banque,
topologie ou critère d'acceptation n'est modifié. Le produit reste
`TESTE_NON_APPROUVE`.
