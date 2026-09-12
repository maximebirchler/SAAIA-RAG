# A763 — La relation de cellule fait partie du fait à prouver

Date de décision : 2026-09-12
Statut : `ACCEPT_GENERIC_STRUCTURED_RELATION_GROUNDING_REQUIRES_TERRA_RERUN`

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
