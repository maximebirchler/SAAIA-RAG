# Compteur d'écriture en cache Responses — A829

Les enveloppes réelles A825–A828 exposent `input_tokens_details.cache_write_tokens`.
L'adaptateur normalisait toujours ce champ à zéro. Corriger les métriques en
lisant le compteur explicitement fourni ; conserver le défaut zéro seulement
quand le champ est absent. Une valeur présente mais invalide reste inconnue.

Le tarif figé des pilotes est deux USD par million pour les entrées et les
écritures, 0,2 pour les entrées en cache et douze pour les sorties. Réconcilier
en lecture seule les charges historiques avec ces champs réels avant édition.
Le défaut est un compteur inexact, pas une économie de dépense autorisée.
Ne pas réécrire les traces ni le journal de charges antérieures.

Ce changement ne touche ni prompts, opérations, contenus, plafonds ni verdicts
sémantiques. Tester une écriture positive avec un tarif synthétique différent
pour que le calcul détecte réellement l'ancien défaut.
