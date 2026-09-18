# A861 — préflight gelé du pilote Candidate Explorer

## Verdict du palier

Le prochain pilote live du planning 5 × 4 est maintenant préenregistré sous
forme d'un unique essai causal. Sa configuration s'arrête avant tout appel tant
que le registre ne peut pas réserver le premier appel fournisseur. Aucun appel
payant n'a été exécuté pendant A861 et le produit reste
`TESTE_NON_APPROUVE`.

Ce palier ne valide pas l'autonomie de Terra. Il retire des causes d'échec
prévisibles dans le harnais afin que le prochain calcul disponible mesure bien
la découverte de candidats, plutôt qu'une option perdue ou une sortie tronquée.

## Deux défauts de préparation corrigés

Le runner générique exposait les options du Candidate Explorer, mais les
wrappers OpenAI et RunPod ne les transmettaient pas. Un essai lancé par ces
wrappers aurait donc gardé silencieusement l'ancienne topologie. Les deux
wrappers transmettent maintenant la recherche native, le protocole, la
topologie, l'espace de travail, l'Explorer, la réserve par rôle, la proposition
active, le retour de liaison, les budgets de tokens et les paramètres de
synthèse.

L'Explorer réutilisait aussi `PlannerMaxTokens`, fixé à 512 dans le parcours
produit. Cette enveloppe convient à un plan court, pas à un appel d'outil qui
peut conserver vingt candidats et dont le contrat accepte jusqu'à 16 384
caractères d'arguments. `CandidateExplorerMaxTokens` est maintenant une option
distincte, bornée entre 512 et 16 384, fixée à 4 096 par défaut et inscrite dans
les configurations et les sceaux de préflight. Le Writer et le Critic gardent
leurs propres plafonds.

Un contrôle exécute Chat Completions et Responses avec une valeur sentinelle de
2 345 tokens et vérifie le champ HTTP réellement envoyé par l'Explorer. Un
autre refuse la configuration à 511 tokens avant tout appel.

## Pilote A861 préenregistré

`config/openai-terra-candidate-explorer-pilot.a861.json` fixe :

- le seul cas `A755-ADV-01-meal-grid-5x4`, une répétition ;
- Terra, Responses, topologie agent et raisonnement faible ;
- espace de travail et Candidate Explorer actifs ;
- 32 768 caractères d'historique natif ;
- 4 096 tokens pour Explorer, Writer et Critic ;
- deux candidats de réserve visés par rôle ;
- sept appels fournisseur au maximum ;
- le commit minimal A860, la branche, la banque, le modèle Qwen local, son
  runtime et la configuration provider par SHA-256 ;
- un corpus scellé avant et après le run ;
- vingt corps distincts, cinq affectations par rôle et vingt sélections finales
  sourcées comme condition fonctionnelle du premier pilote.

Une insuffisance bornée reste un comportement sûr, mais elle ne réussit pas ce
pilote lorsque les corps historiques A813/A817 existent dans le corpus. Une
seule réussite revue sémantiquement autorisera l'enregistrement des répétitions
deux et trois. Le holdout aveugle, la reprise durable et WinUI restent ensuite
obligatoires.

## Garde budgétaire avant démarrage

Le préflight relit maintenant le registre JSONL avant de lancer le backend. Il
refuse un registre invalide, une dérive par rapport au montant enregistré lors
du gel, un plafond de job inférieur à la première réservation ou une marge de
campagne insuffisante pour ce premier appel.

Le contrôle hors réseau A861 observe :

- coût persistant : 39,9943606 USD ;
- plafond autorisé : 40 USD ;
- marge : 0,0056394 USD ;
- réservation minimale du Planner : 0,0061465 USD ;
- registre identique au montant préenregistré ;
- verdict `lifetime_budget_headroom_below_first_call`.

Le sceau reste `NOT_STARTED`, avec `externalCallMayHaveOccurred=false` et
`externalCallExecuted=false`. Il signale aussi correctement que le worktree est
sale pendant la construction et qu'aucune observation récente du tier n'a été
fournie. Après commit, ces deux gardes restent indépendantes du blocage
budgétaire. Aucune clé ni contenu documentaire n'est écrit dans le manifeste.

## Contrôles

- huit contrôles ciblés Candidate Explorer réussis ;
- 299 contrôles du provider réussis, zéro échec ;
- dix contrôles de garde du tier OpenAI réussis ;
- les quatre scripts PowerShell modifiés passent l'analyse syntaxique ;
- les deux wrappers exposent tous les paramètres obligatoires de l'Explorer ;
- les fichiers JSON ordinaires modifiés sont lisibles ; le template de
  production conserve ses marqueurs de substitution et n'est donc pas du JSON
  instancié avant déploiement ;
- la suite backend complète avec PostgreSQL local actif compte 2 458 réussites,
  zéro échec et trois tests live ignorés en 14 min 32 s.

## Suite après épuisement du budget

Tant qu'aucun calcul avancé n'est disponible, le travail utile consiste à
conserver ce profil gelé, vérifier les replays et préparer la revue mécanique et
sémantique. Il ne faut ni desserrer les critères ni confondre le replay A817
avec une découverte autonome.

Lorsque du calcul redevient disponible, le profil budgétaire doit être
réenregistré avec l'enveloppe réellement autorisée, sans modifier les paramètres
sémantiques. Le premier run seul est exécuté. En cas d'échec, ses traces servent
à situer la perte entre navigation, inventaire, corps, rôles, Writer et Critic.
En cas de réussite humaine et mécanique, les deux répétitions restantes sont
enregistrées sur le même état, puis vient un nouveau holdout aveugle. Le même
runner générique peut viser OpenAI ou un endpoint compatible RunPod/client ; la
localisation du modèle ne modifie pas le contrat RAG.
