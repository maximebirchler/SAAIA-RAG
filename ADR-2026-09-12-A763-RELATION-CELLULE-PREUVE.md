# A763 — La relation de cellule fait partie du fait à prouver

Date de décision : 2026-09-12
Statut : `SAFE_INSUFFICIENCY_VALIDATED_2X_REQUIRES_COMPLETE_TERRA_RERUN`

## Problème observé

La première campagne Terra partielle produisait un planning 5 × 4 complet,
vingt noms distincts et vingt citations mécaniquement valides. La revue humaine
des claims et des chunks canoniques rejette pourtant le résultat. Plusieurs
claims prouvent uniquement l'existence d'une recette, sans prouver son
adéquation au créneau où elle est placée. Le cas le plus direct est celui des
pancakes : la preuve indique qu'ils peuvent être servis au petit-déjeuner,
alors que la réponse les place dans la collation du vendredi.

La cause est dans le contrat Writer. Il indiquait qu'un candidat documenté
pouvait être arrangé dans une cellule et que cet arrangement ne constituait pas
un nouveau fait. Cette règle rendait invisible la relation sémantique créée par
la ligne, la colonne, le rôle ou la catégorie.

## Décision

Dans tout livrable structuré, la relation créée par le placement fait partie de
l'unité factuelle. Le Writer doit l'énoncer dans le claim et citer une preuve qui
la soutient. Un index ou un titre peut prouver l'existence et l'orthographe d'un
élément, mais pas une relation absente de la preuve. Lorsque les unités
obligatoires ne peuvent pas être reliées aux axes demandés, le résultat correct
est une insuffisance précise.

La règle est générale. Le code ne contient aucun vocabulaire de repas, de
normes ou de scénario de benchmark. Le jugement de soutien reste au LLM; le code
continue de valider mécaniquement le schéma, le nombre d'unités, les ClaimIds et
les EvidenceIds.

## Coût et architecture

Aucun Critic additionnel n'est ajouté à ce stade. Le premier correctif renforce
le contrat du Writer existant et conserve deux appels par job lorsque le
protocole est valide. Cela permet de mesurer causalement si Terra respecte la
relation explicite avant d'augmenter systématiquement le coût. La réparation
JSON reste bornée à un appel supplémentaire uniquement après une sortie writer
mal formée.

## Preuves

- revue privée en transaction PostgreSQL `REPEATABLE READ ONLY` : quatre jobs,
  douze chunks, aucune mutation;
- planning 5 × 4 : `REJECT_SEMANTIC_SLOT_GROUNDING`;
- cinq repas étudiant : `PASS_SEMANTIC_ON_THIS_RUN`;
- comparaison CEN/IEC : `PASS_SEMANTIC_ON_THIS_RUN`;
- NIST : échec 429 sûr, sans texte ni source non validée;
- correction produit : commit `b20fcc2`;
- tests fournisseur ciblés : 30/30;
- validation Release complète : contrats 10/10, backend 2 145/2 145,
  client 2 234/2 234, zéro échec et deux probes live opt-in non exécutées.

Artefacts locaux :

- `artifacts/reprise-pc-20260908/a763-partial-terra-semantic-review-20260912`;
- `artifacts/reprise-pc-20260908/a763-local-validation-b20fcc26-20260912`;
- `artifacts/reprise-pc-20260908/a763-local-protocol-repair-b20fcc26-20260912`.

## Porte de validation

Le changement reste `TESTE_NON_APPROUVE` tant qu'une nouvelle banque Terra sur
ce contrat n'a pas passé les critères mécaniques et la revue humaine trois fois
sur état gelé. Un Writer qui continue à produire des relations non étayées
imposera de comparer une étape de critique sémantique bornée à ce simple
renforcement de prompt.

## Campagne après correction de la composition documentaire

La campagne préenregistrée `A763-TERRA-FINAL-POST-COLLECTION-3X`, exécutée sur
le commit propre `771ebbc7`, a produit huit jobs durables : une première
répétition complète des quatre cas, puis une deuxième répétition complète avant
l'arrêt du harnais. Les seize appels Terra ont consommé 65 897 tokens d'entrée
et 7 247 tokens de sortie pour 0,218758 USD. Aucun appel n'a reçu de 429, le
sceau du corpus est resté identique et tous les processus temporaires ont été
arrêtés.

La revue canonique diagnostique accepte les huit réponses : deux passages sur
deux pour le planning, les cinq repas étudiant, CEN/IEC et NIST. Les deux
plannings refusent précisément d'inventer les cellules que le corpus ne permet
pas de remplir. Le premier décrit les catégories encore incomplètes ; le second
identifie les cinq collations et l'unique option concrète trouvée. Ces passages
valident le comportement sûr, sans fermer la porte fonctionnelle du planning
complet.

L'arrêt mécanique de la deuxième répétition était un faux négatif du harnais.
Sa détection générique d'insuffisance reconnaissait `ne peut pas`, mais pas la
formulation explicite `je ne peux pas`. La détection couvre désormais les
formes usuelles de `pouvoir`, `disposer` et `fournir`; un test de régression
reprend la formulation réellement observée. Cette correction ne modifie ni le
Writer, ni les prompts, ni la réponse publiée. Le verdict public diagnostique
reste `DIAGNOSTIC_ROWS_ALL_PASS`, huit passages et aucun rejet,
`approvalEligible=false`.

## Campagne complète après alignement du harnais

La campagne `A763-TERRA-FINAL-POST-HARNESS-3X`, exécutée sur le commit propre
`d42ac2b0`, a terminé les douze jobs prévus. Les vingt-quatre appels Terra ont
consommé 101 700 tokens d'entrée et 11 291 tokens de sortie pour 0,338892 USD,
sans erreur 429. Le sceau du corpus est resté identique et les processus
temporaires ont été arrêtés.

Les trois réponses de planning choisissent une insuffisance documentée. Deux
nomment les catégories de preuve manquantes et une nomme les cellules. Le
premier évaluateur PowerShell exigeait pourtant toujours les cinq noms de jours,
même dans la branche d'insuffisance, et ne reconnaissait pas `je ne peux pas`.
Il contredisait ainsi le critère préenregistré qui accepte les cellules **ou**
les catégories de preuve manquantes.

L'évaluateur partage désormais les formes génériques d'insuffisance du harnais
client et réserve les contrôles de grille complète aux réponses qui prétendent
fournir la grille. Trois tests synthétiques vérifient : insuffisance par
catégorie acceptée, formulation à la première personne acceptée et réponse
partielle non déclarée rejetée. Ils passent 3/3 sous PowerShell 7 et Windows
PowerShell 5.1. La réévaluation des douze sorties immuables passe mécaniquement
12/12. La revue canonique reste nécessaire avant tout verdict sémantique.

La revue canonique accepte les neuf réponses des trois autres familles, mais
rejette les trois insuffisances du planning. Chaque refus évite bien de remplir
la grille, mais présente le sous-ensemble de preuves reçu par le Writer comme
un inventaire exhaustif du corpus. Les réponses déclarent que seules deux ou
quatre options de petit-déjeuner existent, alors que d'autres pages canoniques
en contiennent davantage. Verdict public : `DIAGNOSTIC_ROWS_REJECTED`, neuf
passages et trois rejets, `approvalEligible=false`.

Le contrat Writer distingue désormais explicitement deux affirmations : les
preuves fournies ne suffisent pas à étayer une unité, et le corpus ne contient
pas cette unité. La première est permise lorsque le paquet de preuves est
incomplet; la seconde exige une preuve d'exhaustivité explicite. Une réponse
d'insuffisance doit se limiter au manque décisif, qualifier les éléments
soutenus comme des exemples et ne jamais transformer leur liste en inventaire
exhaustif du corpus. Cette règle est générale et ne contient aucun vocabulaire
de repas ou de benchmark.
