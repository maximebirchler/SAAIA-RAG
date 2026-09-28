# A881 — replay privé A817 dans le pipeline staged

## Verdict

Le vrai provider staged traverse hors ligne le dossier privé A817 avec les
vingt corps historiques déjà audités. Le résultat est `answered`, avec vingt
claims, vingt choix distincts, vingt candidats acceptés et une affectation
globale de 20/20. Le Candidate Judge traite deux lots de 12 puis 8 et le
Navigator n'est jamais appelé, puisque les corps nécessaires sont déjà
présents.

Ce palier valide la projection des preuves existantes vers le Candidate Judge,
la fusion du registre, le matching A879 et le transport du dossier vers le
Writer. Il ne valide pas encore le choix autonome des recherches par Terra ni
la qualité sémantique d'un Judge live. Le produit reste
`TESTE_NON_APPROUVE`.

## Méthode privée et preuve publique

Le harnais local lit à l'exécution trois artefacts A817 privés déjà conservés :

- l'entrée de replay et son handoff structuré ;
- la revue fermée des vingt claims et de leurs liaisons aux corps ;
- la sortie Writer fermée déjà contrôlée.

Il reconstruit en mémoire 23 preuves privées, puis exécute le vrai
`OpenAiCompatibleAdvancedAnalysisProvider` avec un transport HTTP déterministe
local et un gateway documentaire en mémoire. Aucun contenu documentaire, nom
de choix, identifiant de preuve, chemin privé ou sortie Writer n'est recopié
dans Git. Le harnais et ses entrées restent sous le répertoire ignoré.

Seul le manifeste public
`artifacts/reprise-pc-20260908/a881-staged-a817-private-replay-20260928/assessment.public.json`
est publiable. Il contient des labels, hashes, nombres d'appels, tailles de
prompts et verdicts agrégés. Il déclare explicitement
`privateContentEmbedded: false`, `networkCalls: 0` et `paidCalls: 0`.

## Défaut produit découvert et corrigé

Le premier replay qui atteignait le Judge produisait des candidats correctement
classés, mais les vingt choix du Writer étaient ensuite refusés par
`candidate_role_not_verified`.

La cause se trouvait dans `MergeCandidateInventory`. Le parseur validait qu'un
titre plus complet provenait du même corps, puis construisait
`canonicalUpdate.ExactTitle`, mais l'expression `prior with { ... }` ne copiait
jamais ce titre. Le registre gardait donc l'ancien titre automatique ou OCR.
Les rôles et EvidenceId étaient conservés, tandis que l'identité exacte attendue
par le Writer était perdue au moment de la fusion.

Le correctif conserve maintenant `canonicalUpdate.ExactTitle`. La régression
ciblée contrôle le checkpoint après remplacement d'un titre subordonné par
l'identité complète visible dans le même corps. Le commit
`19d12ec6ee3c96d05fc4c87d43fb55179b7c0d9c` est poussé sur `SAAIA_V3.1`.

## Reconstruction fidèle des anciens artefacts

La revue A817 contient 23 liaisons, dont 21 classées `content` et deux
`navigation`, mais ses champs `candidateTitle` sont tous vides. Cette absence
est propre à l'ancien format d'artefact ; elle ne signifie pas que les corps
sont absents. Pour reproduire le passage staged actuel, le harnais prend pour
chaque claim son corps `content` déjà validé dans la revue fermée et lui associe
en mémoire l'identité choisie par A817.

Cette étape n'est pas une règle de production et n'est pas committée. Elle sert
d'oracle fermé pour tester le transport actuel sur les mêmes vingt liaisons.
Sans cette reconstruction, l'observateur actuel ne peut pas créer de candidat
source-exact à partir d'un ancien champ de titre vide et demande logiquement au
Navigator de poursuivre.

## Mesures du replay final

| Mesure | Résultat |
|---|---:|
| commit candidat | `19d12ec6ee3c96d05fc4c87d43fb55179b7c0d9c` |
| preuves privées chargées | 23 |
| appels Planner | 1 |
| appels Navigator | 0 |
| appels Candidate Judge | 2 |
| tailles des lots Judge | 12, 8 |
| caractères des prompts Judge | 21 976, 18 197 |
| candidats acceptés | 20 |
| candidats rejetés | 0 |
| affectation maximale | 20/20 |
| affectations proposées | 20 |
| appels Writer | 1 |
| caractères du prompt Writer | 49 751 |
| preuves visibles du Writer | 23 |
| résultat | `answered` |
| claims / choix distincts | 20 / 20 |
| réseau / appels payants | 0 / 0 |

SHA-256 du manifeste public :
`FBFD10B6F9A8EE9DDDBCF4A097DA441C2E015EFE6B2ED2A79BB49BCB1E85C304`.

## Validation du code

Après le correctif :

- les deux scénarios ciblés d'identité passent 2/2 ;
- la suite du provider passe 311/311 ;
- la solution complète passe 10 tests Contracts, 2 475 tests Backend et
  2 338 tests Client ;
- zéro test échoue ;
- trois tests live Backend et un test live Client restent explicitement
  ignorés.

Le total externe demeure 44,42531530 USD sur le hard stop local de 45 USD. Le
reliquat calculé de 0,57468470 USD n'a pas été utilisé.

## Portée réelle et prochaine porte

A881 prouve que, lorsque les vingt corps connus sont déjà disponibles, la
nouvelle séparation Judge/solveur/Writer ne les perd plus et sait former un
dossier complet. Elle ne prouve pas que Terra saura retrouver ces corps depuis
un sommaire, choisir les bonnes opérations, reformuler une recherche sans
rendement, juger les rôles à l'aveugle ou répéter le résultat en production.

La prochaine étape logique est de préenregistrer un pilote live staged unique
qui mesure séparément Planner, Navigator, opérations documentaires, Judge,
matching et Writer. Son entrée, son commit, ses plafonds et ses critères doivent
être figés avant toute dépense. Le reliquat actuel est inférieur au coût des
deux derniers parcours complets A877 et A878 ; il ne permet pas de promettre un
cycle complet plus sa preuve de clôture. Aucun appel tronqué ne doit être lancé.

Le mode `StagedCandidateExplorerEnabled` reste donc désactivé par défaut jusqu'à
ce pilote et ses répétitions. Un succès unique ne suffira pas à approuver le
planning : la qualité des vingt placements doit être relue, puis répétée trois
fois sur un état figé avant le holdout et WinUI.
