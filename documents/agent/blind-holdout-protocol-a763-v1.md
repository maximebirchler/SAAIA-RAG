# Protocole du nouveau holdout aveugle A755/A763

Date : 12 septembre 2026

Statut : **PREINSCRIT — HOLDOUT NON CREE**

Statut produit : **TESTE_NON_APPROUVE**

Ce protocole prépare la dernière preuve de généralisation sans générer, lire ou
exécuter de nouvelle question. L'ancien holdout v3 reste disqualifié parce que
ses questions ont été affichées pendant le développement. Ses résultats, s'il
en existait, ne pourraient pas servir à l'acceptation.

## 1. Porte d'ouverture

Le nouveau holdout ne peut être créé qu'après réunion de toutes les conditions
suivantes :

1. la banque connue locale passe trois fois sur le candidat final ;
2. la banque avancée passe trois fois avec des réponses et relations
   claim/preuve acceptées sémantiquement ;
3. le planning 5 x 4 passe trois fois avec vingt cellules justifiées ;
4. le fournisseur avancé final de la campagne est choisi et son identité est
   scellée ;
5. le parcours terminal WinUI et les cartes source ont été inspectés ;
6. le commit candidat est gelé et le worktree suivi est propre ;
7. les seuils de décision ci-dessous sont figés sans connaître les cas.

Une correction produit, prompt, routeur, retrieval, validation, UI ou
configuration sémantique après création invalide le holdout pour ce candidat.
Il faut alors jeter la banque sans l'ouvrir et en faire produire une nouvelle
après le prochain gel.

## 2. Séparation des rôles

Le constructeur-évaluateur doit être une tâche indépendante qui n'a pas reçu
les questions, oracles ou résultats des banques de développement. La session de
développement ne reçoit avant verdict que le manifeste public défini plus bas.
Elle ne doit jamais lister, rechercher, ouvrir, désarchiver ou afficher le
payload scellé.

Le constructeur-évaluateur peut lire le corpus et les critères publics du
produit. Il ne peut pas modifier le dépôt, les prompts, les seuils, les index ou
la configuration. Il génère les cas, leurs oracles, exécute exactement une fois
le commit gelé, puis publie le verdict agrégé. Les détails deviennent visibles
seulement après enregistrement irrévocable de ce verdict ; la banque est alors
consommée et ne peut plus servir de holdout.

Sur une seule machine et sous un même compte ayant accès à tous les fichiers,
une simple convention de nommage ne constitue pas une séparation suffisante.
Le payload doit être conservé dans un espace auquel la session de développement
n'a pas accès, ou confié à un opérateur indépendant. Une nouvelle tâche Codex
ne suffit que si elle reçoit un contexte sans historique et si ses artefacts
privés restent inaccessibles jusqu'au verdict.

## 3. Portée et équilibre minimaux

La banque doit contenir 24 cas nouveaux, sans paraphrase superficielle d'un cas
connu :

| Terminal attendu | Nombre |
|---|---:|
| réponse locale directe sourcée | 6 |
| clarification avant réponse documentaire | 4 |
| insuffisance documentaire honnête | 4 |
| capacité avancée multisource | 6 |
| frontière locale/avancée particulièrement proche | 4 |

Les quatre cas de frontière reçoivent tout de même un terminal unique dans
l'oracle et comptent dans les métriques de ce terminal. La banque doit couvrir
les six langues du client : au moins douze cas français, au moins quatre cas
anglais et au moins deux cas dans chacune des langues allemande, espagnole,
italienne et portugaise.

Les cas doivent couvrir au moins : définition atomique, valeur ou condition
technique, explication causale courte, document nommé, comparaison explicite,
question déictique réellement ambiguë, absence documentaire, grille bornée,
collection bornée, synthèse multisource et décision qui nécessite des données
utilisateur. Au moins quatre cas avancés doivent utiliser plusieurs sources et
au moins deux doivent relier dix unités de réponse ou davantage à leurs preuves.

Le constructeur doit vérifier qu'aucune chaîne distinctive de la question ne se
trouve dans les banques, tests, prompts, documents d'audit ou historique Git du
commit gelé. Il peut utiliser les mêmes familles fonctionnelles et le même
corpus ; il ne peut pas réutiliser les formulations, canaris ou oracles connus.

## 4. Manifeste public avant exécution

Le seul artefact visible par la session de développement avant verdict contient :

- le SHA Git gelé et l'état propre du worktree suivi ;
- les identités exactes des providers, modèles, runtimes et profils ;
- les hashes des binaires, configurations et manifests de corpus nécessaires ;
- le nombre total de cas et les agrégats par terminal, langue et famille ;
- le SHA-256 du payload de questions et le SHA-256 séparé des oracles ;
- l'identité et le SHA de l'outil d'exécution ;
- les budgets d'appels, tokens, coût et temps ;
- la date de création et la mention `questionsDisclosed=false` ;
- la mention `oraclesDisclosed=false` et `executionCount=0`.

Il exclut tout texte de question, mot distinctif, titre de source, page, extrait,
réponse attendue, identifiant sémantique ou motif d'échec. Les identifiants
publics sont opaques, par exemple `BH4-001`.

## 5. Exécution unique

L'évaluateur vérifie d'abord les hashes et les ressources. Toute dérive ou
indisponibilité rend la campagne `INCONCLUANTE` avant le premier cas. Ensuite :

1. il lance les 24 cas dans un ordre aléatoire scellé ;
2. il interdit tout patch, relance individuelle ou changement de configuration ;
3. il conserve chaque trace technique, réponse, source, latence, usage et coût ;
4. il évalue mécaniquement puis sémantiquement sans communiquer de résultat
   intermédiaire à la session de développement ;
5. il écrit et hash le verdict agrégé avant de dévoiler un détail ;
6. il marque la banque `CONSUMED` même en cas d'échec.

Une panne externe avant réception d'une réponse peut rendre le cas
`INCONCLUANT` si aucune sortie sémantique n'a été observée. Une sortie erronée,
tronquée, non sourcée ou un mauvais terminal est un échec produit et ne peut pas
être relancé dans le même holdout.

## 6. Critères d'acceptation préinscrits

L'approbation exige simultanément :

- 24/24 terminaux conformes à l'oracle ;
- zéro fait documentaire non soutenu ;
- zéro citation forgée, substituée ou non ouvrable ;
- zéro source affichée pour une clarification ou insuffisance sans réponse
  documentaire ;
- 6/6 réponses locales sémantiquement correctes et suffisamment précises ;
- 6/6 réponses avancées sémantiquement correctes ;
- 100 % des claims vérifiables reliés à une preuve qui soutient réellement le
  claim ;
- toutes les grilles et collections avec la cardinalité demandée ;
- toutes les réponses dans la langue demandée ;
- aucune fuite de provider, prompt, secret ou payload fournisseur dans l'UI ;
- aucun dépassement des enveloppes de coût, appels et temps préinscrites ;
- environnement restauré et ressources temporaires arrêtées.

Un succès de test, un JSON valide ou une citation ouvrable ne compense pas une
erreur sémantique. Un seul fait inventé, une seule citation forgée ou une seule
relation claim/preuve fausse impose `REJETE`. Les pannes exclusivement externes
peuvent imposer `INCONCLUANT`, jamais `APPROUVE`.

## 7. Publication du verdict

Avant dévoilement, l'évaluateur publie un fichier agrégé contenant seulement :

- hashes du manifeste, du payload, des oracles et du résultat complet ;
- compteurs attendus, exécutés, réussis, échoués et inconclusifs ;
- compteurs par terminal et langue ;
- taux de claims soutenus et de citations ouvrables ;
- distributions de latence, tokens, appels et coût ;
- verdict `APPROUVE`, `REJETE` ou `INCONCLUANT` ;
- confirmation que le verdict a été écrit avant divulgation ;
- confirmation que la banque est consommée.

Après hash de ce verdict, les détails peuvent être remis à la session de
développement pour diagnostic. Toute correction consécutive prépare un nouveau
candidat et exige un autre holdout indépendant ; elle ne change pas le verdict
historique.

Ce protocole ferme la méthode, pas la preuve. Aucun nouveau holdout n'a été créé
ou exécuté lors de sa rédaction, et aucun contenu caché n'a été consulté.
