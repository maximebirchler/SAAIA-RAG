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
