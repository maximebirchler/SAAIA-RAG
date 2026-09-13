# Contexte JSON et budget du planning — 13 septembre 2026

L'essai A806, sur le commit 28050810cd9d4fda8d10769f31ce143a861acd5b, s'arrête avec `advanced_external_budget_job_cost_limit`. Les six appels ont réussi et coûté 0,62905950 USD ; deux demandes de recherche ont eu lieu pendant la rédaction et 25 recherches documentaires sont enregistrées. Aucun résultat final n'est publié. L'appel suivant est bloqué avant transmission par le plafond expérimental de 0,65 USD. Le verdict est INCONCLUANT pour la correction sémantique ; ce n'est ni une absence de crédits chez OpenAI ni un planning approuvé ou rejeté après lecture. Le détail du dernier objet rédigé n'est pas archivé, donc son contenu n'est pas déduit de son nombre de tokens.

Les entrées de cette exécution totalisent 244 730 tokens, dont environ 45 000 à 50 000 à chacune des étapes suivant le planificateur. Les crédits locaux restent à 1,64282890 USD sur les 30 USD déjà achetés. Aucun nouvel achat n'est autorisé.

## Correction locale

Le contexte comptait seulement les caractères du contenu des preuves. Les identifiants opaques, titres, types, annotations et recherches associés étaient ajoutés en dehors de ce plafond. Le budget compte désormais la représentation JSON complète de l'array `evidence`, séparateurs et métadonnées compris. La priorité et la diversité des résultats restent calculées sur les observations complètes. Le dernier contenu est borné, lorsque sa métadonnée et un fragment peuvent encore tenir, avec un indicateur de coupure explicite.

La sérialisation des prompts conserve également les lettres Unicode au lieu de les développer en séquences `\uXXXX`, avec l'encodeur JavaScript standard configuré pour les plages Unicode. Les guillemets, retours à la ligne et caractères HTML restent échappés correctement. Le test vérifie un aller-retour exact de texte français, allemand, chinois, guillemets, chemin Windows et balises HTML.

## Preuves et limites

Artefacts : `artifacts/reprise-pc-20260908/a800-meal-planning-continuity-20260913`.

- Les deux tests de budget, direct et grille, échouent sur la taille JSON avant correction (`prompt-metadata-budget-red-v3.trx`), puis passent.
- La suite backend finale `backend-prompt-budget-final.trx` passe : 2 271 réussites, aucun échec, trois tests live ignorés.
- `meal-a807-unicode-prompt-replay.v1.json` revalide les 25 recherches déjà consommées, avec le vrai resolver en lecture seule. Le writer reconstruit mesure 128 971 caractères contre 141 256 avec l'ancien encodage, à informations strictement égales.
- `meal-a807-bounded-prompt-replay.v1.json` applique ensuite le plafond au JSON complet : le writer reconstruit mesure 66 425 caractères, dont au plus 64 000 pour l'array de preuves. Il conserve 59 observations au lieu de 132. Cela change la sélection visible ; cette étape ne conserve donc pas toutes les informations de l'ancien contexte.

Les nombres de caractères ne sont pas des tokens facturés. Ces replays ne sont pas des requêtes API archivées, ni des réponses du modèle. Le gain réel de coût et l'effet sur la qualité du planning restent à mesurer après gel de cette version. Les plafonds global de 30 USD et expérimental par job sont conservés. Le score historique aveugle reste 1/24 et le produit TESTE_NON_APPROUVE.
