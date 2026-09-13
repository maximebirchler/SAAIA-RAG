# A847 — budget borné de l'historique opérationnel natif

Préenregistrement avant modification d'admission et nouveau pilote.

A846 sur 0426ce2c : neuf appels, 0,55869260 USD ; vingt recherches et douze
lectures réussies, trente-deux opérations. Premier vrai save observé : deux
entrées de mémoire regroupent plusieurs candidats, dans le dernier batch avec
deux lectures. Aucun tour de synthèse suivant, aucun bénéfice qualité observé,
aucune réponse terminale ni critique. Le save n'est pas une preuve de vingt
candidats individuellement prouvés ou d'une mémoire durable.

Le plafond natif arrête le job. Dernier output Responses compact : 13 933
caractères, trois états opaques de 4 428, 4 236 et 2 956 caractères, trois appels.
Reconstruction avec les deux diagnostics de lecture et identités réelles,
serialization System.Text.Json/encodeur du provider : 16 344 caractères sans
ID visible ; 17 350 avec huit par lecture. Ce sont des bornes reconstruites,
pas une reconstruction de la visibilité effective après lecture. Le plafond
16 384 laisse quarante caractères au scénario minimal. Les états et retours
doivent être conservés ; ne pas les tronquer ou ignorer pour passer.

Hypothèse mécanique : la limite d'historique doit appartenir au profil de capacité
et de transport, distincte du budget d'extraits documentaires. Ajouter
NativeResearchMaximumHistoryCharacters, défaut 16 384, plage 16 384–65 536,
contrôlée avant I/O. Profil expérimental 32 768 : borne explicite, supérieure
aux 17 350 mesurés, marge pour une nouvelle continuation ; pas d'historique
illimité ni promesse de passage de tous les futurs états. Les deux protocoles
utilisent cette borne ; toutes les sorties et l'état natif restent intacts.

La réservation financière porte déjà sur tout le payload réellement transmis,
fonctions et historique compris. Ce chemin reste inchangé et sera contrôlé.
Budgets des preuves, nombre d'opérations, pages, effort, tokens, appels et coût
A846 inchangés : trente-deux opérations, high/8192, low/512, douze appels,
une tentative HTTP, 1,25 USD/job. Registre 37,85846860 USD / 40, reste calculé
2,14153140 USD avant prochain pilote ; pas d'achat automatique.

Contrôle causal : déclarer l'option à son défaut, fixture configurée à 32 768
encore refusée par l'ancienne admission 16 384, puis la même fixture passe
après branchement de la borne. Tester défaut inchangé, refus au-delà de 32 768,
option invalide avant I/O, état opaque et liaisons inchangés, réservation sur
payload complet. Contrôles natifs/workspace/budget puis suite complète.
Pas de fixture miroir transformée en approbation sémantique.

Un pilote connu après version propre figée, sans nouvelle consigne ou hausse
documentaire ; analyser mémoire, résultat, vingt unités et sources physiques
avant répétition. Pas d'attribution qualitative à la seule borne d'historique.
Trois réussites consécutives restent exigées avant banc inédit et WinUI.
TESTE_NON_APPROUVE, Goal actif, autorisation limitée aux crédits déjà achetés.
