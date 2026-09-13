# Navigation autonome et rédaction isolée — A816/A817

Date : 13 septembre 2026. Candidat : `ad7fe14e70949db733578978f47cd5723cdec032`.
Produit : **TESTE_NON_APPROUVE**. Le diagnostic Writer est accepté sur le cas
connu ; le planning produit autonome reste rejeté.

## Ce que la mesure établit

Terra sait composer ce planning lorsque les contenus adéquats lui sont
présentés. Un appel du Writer de production, avec le même prompt système que
l'essai E2E rejeté, produit les vingt cases et les cite correctement. Il choisit
des recettes documentées, les répartit de façon ordinaire et annonce que cette
répartition est sa proposition. Par exemple, des muffins documentés peuvent
être proposés au petit-déjeuner sans que le livre ait prescrit ce créneau.
Cela préserve la distinction entre fait documentaire et synthèse créée.

Cette preuve n'établit pas que le RAG choisit seul les bonnes lectures. Le
diagnostic utilise les vingt fenêtres A813, dont deux coordonnées choisies
manuellement, et la projection A814 de 209 chunks canoniques en 58 preuves
visibles. Aucune liste obligatoire de recettes ne remplace les vingt cases de
la demande. Le modèle sélectionne aussi des recettes voisines rencontrées,
telles que la crêpe, le cheesecake au thon et le poulet basquaise. Un oracle
parfait, un critique, un job durable, WinUI et trois répétitions ne sont pas
revendiqués.

## Comparaison des exécutions closes

| Mesure | A816 : parcours produit | A817 : Writer isolé |
|---|---:|---:|
| Demande | Planning connu 5 × 4 | Même demande et coordonnées |
| Prompt système Writer | Production actuelle | Exactement identique, SHA vérifié |
| Exploration | 31 recherches réellement choisies | Vingt lectures retrouvées extérieurement |
| Lectures natives choisies par le modèle | 0 | Aucun choix d'outil mesuré |
| Appels modèle | 7 | 1 |
| Coût enregistré | 0,3669042 USD | 0,0768015 USD |
| Durée instrumentée provider | 107,161 s | 20,223 s |
| Tokens entrée/sortie | 129 417 / 4 869, somme des appels | 21 412 / 2 432 |
| Résultat | Insuffisance, un exemple, zéro cellule complète | Réponse de vingt cellules distinctes |
| Validation | Planning rejeté | 20/20 choix et liaisons revus ; parseur/policy passent |

Le plafond global passe à 40 USD après la recharge utilisateur. A816 réserve
un plafond de job de 0,75 USD, A817 0,30 USD. Les sept appels maximum, le contexte,
les outils et les limites temporelles d'A816 ne sont pas augmentés. A817 garde
4 096 tokens maximum de sortie et `reasoning_effort=low`, avec un seul essai
HTTP maximum. Les réponses reçues et tokens/cache-write sont corrélés aux coûts
du journal ; aucune nouvelle tentative automatique ou facturation en double.

## Exploration et visibilité

A816 approfondit réellement des titres : frittata, barres de céréales,
muffins, brioche, compote et macaroni, chacun dans une source observée. Ce
comportement est plus proche de l'usage attendu des sommaires que l'ancien
enchaînement de reformulations générales. Pourtant, plusieurs titres choisis
se trouvent dans le sommaire d'un extrait de 22 pages ; la présence du titre
ne garantit pas celle du corps dans ce fichier indexé. Le modèle ne demande
aucune lecture native pour vérifier une fenêtre physique. Writer et critique
restent finalement sur l'insuffisance.

Pour mesurer aussi la sélection du contexte, les 23 références exactes
utilisées par A817 sont recherchées dans les résultats réellement retournés
par A816 et son prompt final : huit avaient été retournées et trois étaient
visibles. Des contenus retournés n'atteignent donc pas cette rédaction, tandis
que d'autres n'ont pas été récupérés par cette exploration. Cette comparaison
utilise les choix d'un diagnostic différent ; elle n'est pas appariée au hasard
et l'absence de ces IDs ne prouve pas l'absence de preuves alternatives.

Les prompts de production autorisent déjà la synthèse de placements, tout en
exigeant la provenance des objets et des contraintes factuelles. Le nouveau
résultat justifie de comparer l'exploration documentaire et la conservation
des contenus avant d'attribuer les échecs à une incapacité générale de Terra
ou de multiplier les corrections textuelles ciblées.

## Revue du Writer isolé

Les vingt titres sont distincts ; les C1–C20 apparaissent une seule fois dans
leurs cellules prévues. Les 23 références copiées existent dans les 58 preuves
du véritable appel et les 209 chunks canoniques. Aucun ID inventé, aucune
terminaison JSON coupée : `finish_reason=stop`. Les bytes des requests et le
contenu des réponses sont vérifiés dans la trace privée. Le parseur actuel et
la policy d'objet soutenu par son propre corps passent sur les vingt claims,
sans appel supplémentaire.

La revue humaine lit chaque extrait réellement visible. Elle vérifie nom,
contenu substantiel, créneau ordinaire et absence de transfert de preuve entre
recettes. L'omelette et les fruits en beignets citent leur heading et leur corps
voisin dans la même révision ; le macaroni cite son nom et ses propres
ingrédients/méthode. Le chiffre de portions adjacent au macaroni n'est pas
promu en titre. La panna cotta/compote et le smoothie/biscuit gardent leurs
titres composés documentés. Des descriptions ou ingrédients suffisent à
soutenir ces choix nommés lorsque la demande ne requiert pas toute la recette ;
aucune procédure invisible n'est ajoutée. Aucun régime, simplicité ou objectif
nutritionnel absent de la demande n'est inventé.

Les nouvelles références héritent des identités canoniques déjà résolues et
des bornes des fenêtres A813 contrôlées contre les fichiers hashés. Cela ne
revendique pas un nouvel audit physique indépendant de chaque claim ni un clic
WinUI sur ses cartes.

## Suites et limites

La première tentative A816 s'est arrêtée avant transmission pour observation
Tier 1 trop ancienne. Les artefacts de cette tentative sont préservés. Billing
et Limits sont ensuite relus à 15:59 UTC : crédit 10,08 USD avant essais, Tier 1,
500 000 TPM / 500 RPM / 900 000 TPD pour Terra ; auto-reload désactivé. Aucun
achat par l'agent. Après les deux essais, total local 30,36662880 USD sur
40 USD, soit 9,63337120 USD calculés restants.

Comparer maintenant les stratégies générales d'exploration et de rétention
sur des sources réellement disponibles, avec protocole enregistré et coûts
bornés. Corriger le garde de propriété des jobs avant le futur holdout. Une
réponse autonome acceptée devra ensuite être répétée trois fois sur état figé,
suivie du holdout nouveau et du vrai parcours WinUI. Le 1/24 historique reste
rejeté et inchangé. L'installation interactive différée et la location RunPod
ne sont pas lancées.

Preuves locales dans `artifacts/reprise-pc-20260908/a800-meal-planning-continuity-20260913` :

- `meal-a816-semantic-assessment.v1.json` et capture close du job
  `4b3ebec9-ff07-42c2-9bf5-78acd750e031` ;
- `meal-a816-research-events-readonly.v1.json` et
  `meal-a816-a817-evidence-comparison.v1.json` ;
- `meal-a817-native-writer-result.v1.json`,
  `meal-a817-native-writer-claim-review.v1.json`,
  `meal-a817-production-policy-validation.v1.json` et
  `meal-a817-semantic-assessment.v1.json` ;
- harness `native-writer-a817/Replay.csproj`, wrapper
  `run-native-writer-a817.ps1` et validation hors ligne
  `native-writer-validation-a817/Replay.csproj`.

Les traces exactes restent privées sous `%LOCALAPPDATA%/SAAIA/llm-dev/traces`.
Aucun prompt, corpus, header d'authentification ou secret n'est envoyé dans les
rapports Telegram ni ajouté au Git de ce lot.
