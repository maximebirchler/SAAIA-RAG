# A866 — garde Candidate Explorer sur la décision sémantique

## Verdict du palier

La chaîne de revue est fermée jusqu'à la décision finale. Le finaliseur refuse
maintenant de publier une décision sémantique Candidate Explorer si le manifeste
ne prouve pas l'intégrité A863, le nombre exact de jobs et de checkpoints, la
présence d'événements d'outils et une quantité minimale de traces compatible
avec Explorer, Writer et le Critic demandé.

Aucun appel modèle n'a été exécuté et le produit reste
`TESTE_NON_APPROUVE`.

## Garde ajoutée

Le préparateur A864 reporte désormais dans son manifeste public :

- le nombre de checkpoints audités ;
- l'obligation ou non du Critic ;
- les comptes de jobs, événements et traces déjà exposés ;
- le verdict et les empreintes du paquet A863.

Lorsque `candidateExplorerEnabled=true`, le finaliseur exige :

- le verdict exact
  `PASS_PRIVATE_EVIDENCE_INTEGRITY_REQUIRES_SEMANTIC_REVIEW` ;
- une empreinte non vide pour l'assessment d'intégrité et l'annexe privée ;
- autant de jobs et de checkpoints que de lignes attendues ;
- au moins un événement d'outil par job ;
- au moins deux traces par job, ou trois lorsque le Critic est obligatoire.

La vérification A863 reste responsable des rôles exacts et de chaque empreinte.
Le finaliseur contrôle les agrégats scellés avant d'accepter les décisions
humaines. Une réponse sémantiquement marquée `PASS` ne peut donc pas contourner
une preuve Explorer absente ou rejetée.

## Contrôles

La suite du finaliseur compte neuf contrôles réussis :

- acceptation de deux lignes sémantiquement approuvées ;
- rejet si une ligne est refusée ;
- rejet en présence de preuve canonique non résolue ;
- refus d'un motif vide ;
- refus d'une identité de job inconnue ;
- impossibilité de finaliser un diagnostic comme acceptation ;
- verdict diagnostic non approbateur ;
- acceptation d'un manifeste Explorer avec intégrité positive et comptes
  cohérents ;
- refus du même paquet avec un verdict d'intégrité négatif.

Les trois scripts modifiés passent l'analyse syntaxique PowerShell et
`git diff --check` est propre. Aucun fichier .NET n'a changé depuis la suite
backend A862 à 2 459 réussites, zéro échec et trois live ignorés.

## Limite

Cette garde prouve le refus d'un paquet incohérent, pas la qualité d'un futur
planning. Le premier pilote A861 et sa revue humaine restent nécessaires. Une
décision positive issue de cette chaîne restera un diagnostic ciblé à une
répétition ; elle n'autorisera les répétitions deux et trois qu'après inspection
effective des vingt propositions et de leurs sources.
