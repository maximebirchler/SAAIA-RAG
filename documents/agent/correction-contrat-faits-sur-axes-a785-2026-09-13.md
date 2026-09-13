# A785 — corriger une sélection d'objets inférée pour des faits sur des lignes fixées

13 septembre 2026. Statut produit : **TESTE_NON_APPROUVE**.

La reprise OAuth A783 reçoit bien cinq lignes A à E et deux colonnes
Actor/Action et Key Item Transferred or Checked. Le petit modèle a pourtant
décrit les dix unités comme `named_item`, `action_with_item` et
`distinct_structured_layout`. Le serveur préservait obligatoirement cette
sélection distincte. Il pouvait ensuite exiger des titres nommés exacts pour
des descriptions d'actions, et rejeter un tableau factuel sans rapport avec
la capacité réelle de Terra.

Le planner serveur peut maintenant retourner `content_claims` avec une base
explicite `selectionBasis: fixed_subject_attributes`. Cette correction est
permise seulement pour une famille factuelle reconnue : action, acteur, étape,
fait, attribut, propriété, exigence, définition, condition ou règle. Le nombre
de libellés de lignes doit correspondre au nombre de lignes et les colonnes
doivent être définies. Une demande explicitement distincte ou différente reste
protégée. Une grille sélectionnant des préparations ou d'autres nouveaux objets
ne bénéficie pas de cette correction.

Le nombre d'unités, les libellés, les colonnes et le type de preuve sont
conservés. Une donnée transférée dans la description d'une action ne suffit
plus à réactiver le contrôle des titres d'objets distincts une fois le contrat
factuel validement corrigé. Les contrôles de citations, le critic, les limites
de recherche et les budgets restent applicables.

Quatre simulations causales contrôlent la correction, sa base obligatoire et
la protection d'une demande explicitement différente. Avec les fixtures
valides, deux échouaient avant correction et deux protections passaient ; les
quatre passent après correction. Le scénario de réponse factuelle vérifie dix
claims conservés et l'absence de contrôle de titres nommés inadapté. La
protection existante du planning de repas passe toujours. Six variantes
françaises, anglaises, allemandes, espagnoles, italiennes et portugaises
vérifient qu'une distinction explicite bloque cette correction.

La suite backend Release complète passe : **2 211 réussites, zéro échec,
trois tests live ignorés**. Aucun appel OpenAI n'a été fait pour ces tests.

L'exporteur sémantique conserve également le handoff entrant dans son bundle.
L'export des trois jobs A783 a été réellement exécuté avec cette extension,
dans une transaction `REPEATABLE READ ONLY`. Il permet de comparer le contrat
reçu au mode de sélection final sans déduire le premier depuis la réponse.

Une reprise avec Terra après A785 a été exécutée. TLS et FOMC répondent
correctement, mais OAuth est rejeté avant affichage avec
`advanced_writer_evidence_id_invalid`. Il a consommé trois appels estimés à
0,044904 USD malgré l'absence de résultat validé. A786 étend la réparation
unique du Writer tout en conservant le rejet des références inconnues.
La réponse OAuth reste à vérifier après cette correction contre le passage
de la séquence authorization-code, notamment
la validation du code et de l'URI à l'étape E. La correction de son contrat
ne prouve ni la récupération du bon passage ni la justesse de la réponse.
Le verdict consommé BH6 reste inchangé et le produit reste non approuvé.

Preuves : `artifacts/reprise-pc-20260908/a785-fixed-subject-facts-20260913`
et `artifacts/reprise-pc-20260908/a783-adaptive-research-20260913/evidence-r1-with-handoff`.
