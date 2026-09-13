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

Réconciliation exécutée avant édition : quinze réponses clôturées, 351 218
tokens d'écriture réels ; les quinze montants restent exactement identiques.
Le compteur explicite est maintenant lu. Un test avec tarif d'écriture
synthétique de quatre USD confirme 45 tokens écrits et le bon montant commun.
Suite backend après cette correction et le contrat optionnel A830 :
2 375 réussites, aucun échec, trois ignorés. Journal historique inchangé.
