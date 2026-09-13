# Comparaison préenregistrée de l'exploration documentaire — A819

Protocole enregistré avant implémentation et avant tout nouvel appel payant.
Baseline source : `4b09a054` (sémantique RAG identique à `ad7fe14e` ; A818
change seulement le suivi des validations). Diagnostic connu : A816 autonome
échoue avec 31 recherches et zéro lecture ; A817 Writer isolé réussit vingt
choix lorsque les passages sont fournis extérieurement. A817 n'est ni une
baseline autonome ni un oracle aveugle.

## Hypothèses et ordre de comparaison

1. B : ajouter une recherche littérale paginable dans une révision déjà
   observée, explicitement appelée par le modèle. Elle retourne les chunks
   canoniques qui contiennent le texte demandé et leurs pages physiques.
   Les résultats peuvent être des sommaires : le modèle doit distinguer
   navigation et contenu, puis appeler `read_source` si nécessaire. La
   projection du Writer et les règles sémantiques actuelles restent constantes.
2. C, seulement si B ne suffit pas : comparer une mémoire de travail centrée
   sur les éléments choisis et leurs corps de preuve à la priorité actuelle
   des vingt recherches. Préciser et figer ce protocole avant implémentation ;
   ne pas changer B et C dans une seule expérience.

La disponibilité du nouveau moyen de recherche nécessite une description
dans l'inventaire d'outils et leur protocole JSON. Aucune recette, créneau,
requête de test, mot métier, source préférée ou liste de vingt titres n'est
introduite dans le code ou les consignes de production. Le modèle choisit
ses textes, ses documents, ses lectures et ses propositions. Pas de fallback
automatique vers cet outil et pas de recherche externe.

## Vérifications sans coût

Tester sur PostgreSQL réel : séparation tenant, révision observée et hash,
texte traité littéralement (caractères `%` et `_` inclus), ordre stable,
pagination bornée, texte absent et scope invalide. Les résultats gardent les
identifiants du même resolver canonique et passent par le budget/trace des
outils existants. Le texte absent dans cette révision n'est pas une absence
dans le corpus. Aucun offset ni page imprimée n'est converti implicitement.

Mesurer hors API la disponibilité de contenus du diagnostic connu et les
passages accessibles par chaque moyen. Les titres de A817 peuvent servir
uniquement à ce contrôle diagnostic connu ; ils ne pilotent pas la campagne
autonome ni une banque aveugle. Les traces brutes restent privées.

## Pilotage autonome et critères

Premier pilote B : même demande connue de cinq jours × quatre repas, même
corpus/révisions et paramètres modèle que A816 (Terra low, critique activé,
7 appels maximum, 4 096 tokens de critique, une tentative HTTP). Ne pas
augmenter le contexte, les appels, les délais ou les résultats pour masquer
un échec. Run figé neuf dérivé du runner source A818 et manifeste propriétaire.

Comparer : outils et textes réellement choisis, passages retournés, passages
visibles au Writer, propositions valides, références exactes, pertes de
projection, durée totale et coût. Revue humaine obligatoire du résultat et
des sources. Une liste de vingt noms, un parseur vert ou un succès isolé ne
suffit pas : vingt choix concrets distincts, adaptés et reliés à leurs propres
contenus sont nécessaires. Si le corpus ou l'exploration ne suffisent pas,
une insuffisance précise est préférable à des cartes non probantes.

Premier pilote plafonné à 0,75 USD ; au plus deux pilotes B avant diagnostic
et nouvelle décision documentée, enveloppe B maximale 1,50 USD. Les crédits
calculés disponibles à l'enregistrement sont 9,63337120 USD sur 40 achetés.
Aucun achat par l'agent. Réserver le solde aux répétitions critiques, au futur
holdout aveugle et à WinUI ; ne pas épuiser le budget en répétant un même échec.

Si une variante donne un résultat autonome acceptable, figer son code et
réunir ensuite trois réussites live consécutives. Le futur holdout reste
inconnu jusqu'au gel ; BH6 reste historiquement rejeté à 1/24. Les exigences
générales, locales et WinUI du Goal restent nécessaires. Le produit reste
**TESTE_NON_APPROUVE** pendant la comparaison.

## Implémentation B et contrôles sans API exécutés

`find_source_text` est ajouté au gateway et au protocole JSON du modèle. Le
modèle doit fournir un `sourceKey` observé, un texte littéral et éventuellement
l'offset renvoyé. La portée tenant/révision/hash/chemin est vérifiée même en
cas de résultat vide, dans une transaction PostgreSQL en lecture seule et
snapshot stable. Les identités des chunks sont ensuite revalidées par le même
resolver. Aucun résultat n'est fusionné, aucune requête n'est réécrite en
liste de mots et aucun choix de recette n'est codé.

Neuf contrôles du protocole passent. Deux contrôles PostgreSQL réels passent,
dont une autre révision du même document contenant le même texte, les autres
documents/tenants, la pagination stable, les caractères littéraux et le refus
du changement de hash. Suite backend Release : 2 313 réussites, zéro échec,
trois tests live ignorés. PostgreSQL temporaire arrêté, environnement restauré.

Sur les vingt `selectedItem` réellement produits par A817, un diagnostic
externe en lecture seule dans leurs sources retrouve des références exactes
de dix-neuf choix : 58 chunks au total, somme des durées des outils 1 965 ms.
Huit scopes document/révision sont revalidés. La recherche du titre complet
du porridge ne retrouve pas de match littéral. Ce contrôle ne cherche pas
d'alternative automatiquement et ne transforme pas ce zéro en absence du
contenu. Les titres proviennent du résultat connu et non d'une sélection
autonome dans B. Aucun appel API ni lecture de corps supplémentaire ; ce
diagnostic ne valide pas le planning ni la disponibilité de toutes les
recettes dans un corpus aveugle.

Le runner source expose désormais deux paramètres optionnels de validation :
répertoire privé de traces et nombre maximum de tentatives HTTP (défaut
historique trois, pilote enregistré une). Les budgets et paramètres de
contexte/outils restent ceux du protocole. Les anciennes captures sont
préservées. Preuves du diagnostic :
`artifacts/reprise-pc-20260908/a800-meal-planning-continuity-20260913/meal-a819-literal-find-known-diagnostic.v1.json` ;
contrôles mécaniques : `a815-completion-audit-20260913/exploration-a819-*.trx`
et `a671-backend-postgres/a819-canonical-find-final-20260913/`.

## Pilote B1 clos et protocole B2 avant modification

Job `23bac570-6337-4b79-8438-d3b5471ff505`, candidat `9cfcd1e6` : sept appels,
0,3793323 USD, 108,186 secondes de provider. Vingt-huit recherches sémantiques,
zéro `read_source`, zéro `find_source_text`, zéro cellule de planning. Résultat
final insuffisant avec une affirmation agrégée « seize options, quatre
manquantes » dont le décompte n'est pas approuvé. Les ressources possédées sont
arrêtées et le nouveau garde vérifie le vrai GUID enregistré. Le verdict est
**REJECT_COMPLETE_PLANNING_NOT_ESTABLISHED**, sans amélioration autonome démontrée.

La requête exacte du premier Writer contient le contrat littéral et son
inventaire, mais conserve `researchTools.tool='search_corpus'`. Une consigne
dit encore « Your tool is search_corpus ». Cela ne démontre pas causalement
pourquoi le modèle n'a pas choisi les autres opérations. B2 comparera seulement
un catalogue commun où les trois opérations sont présentées comme outils de
premier niveau, avec les mêmes paramètres et une description cohérente.
Retirer le singulier donnant une opération comme outil principal ; aucune
instruction métier, recette ou décision automatique n'est ajoutée.

B2 constitue le second et dernier pilote B prévu : même demande/corpus,
projection, sept appels, une tentative HTTP, critique et cap 0,75 USD. La
consommation cumulée B1+B2 reste au plus 1,50 USD. Préenregistrement présent
avant édition du catalogue et avant nouvel appel. Si le planning ne passe
pas, diagnostiquer la chaîne et enregistrer C séparément ; pas de troisième
pilote répétitif de B. Le solde calculé après B1 est 9,2540389 USD.
