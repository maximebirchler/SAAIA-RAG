# A850 — contrôle du corps procédural faussement classé navigation

Préenregistrement :
`corriger-faux-navigation-corps-procedural-a850-2026-09-16.md`. Le pilote A849
sur le candidat figé `c37f103b` a consommé onze appels pour 0,79183200 USD et
exécuté trente-deux opérations documentaires réussies : vingt-cinq recherches,
sept lectures et aucune limite de fenêtre A849. Il a construit vingt choix et
vingt claims, puis a échoué avant Critic sur
`advanced_synthesis_candidate_body_not_supported`.

Le seul retour de liaison concernait le claim C17, `Charlotte`, et la preuve
`advanced-evidence-7dcb75aff9da074e23af09ef4a7d38ba`. Le modèle a conservé ce
choix et cette preuve pendant le tour de correction. Cette décision était
cohérente avec le texte visible : la preuve de 1 138 caractères comporte une
liste de matériel, la rubrique « Technique », huit opérations, un repos de
quatre à cinq heures, un « Truc du chef » et deux suggestions. Le backend la
marquait pourtant `navigation` avec le motif `numeric_title_catalog`. Le garde
de support la réduisait ensuite à une preuve d'identité, comme si elle ne faisait
que nommer ou localiser Charlotte.

La cause se situe dans `RetrievalContentClassifier.AnalyzeChunk`. L'en-tête
« 57 fiches cuisine », les sept quantités de matériel, les nombreuses puces et
les lignes OCR courtes déclenchaient la forme de liste et au moins huit jetons
numériques courts. La densité calculée restait trop basse pour annuler le score
de navigation. Le contenu procédural n'était pas reconnu parce que l'ancienne
protection des contenus structurés exigeait une forme incompatible avec cette
page à puces.

La correction ajoute une reconnaissance générale d'un corps procédural à
puces. Elle exige au moins soixante-dix mots, six puces, une rubrique de
procédure déjà couverte par le lexique multilingue et quatre signes de fin de
phrase. Elle refuse les points de suite et les blocs ayant au moins deux lignes
de renvoi de pages. Aucun terme propre à la cuisine, à Charlotte, au document ou
à une catégorie produit n'est utilisé. La règle intervient seulement si un
marqueur explicite de table des matières n'a pas déjà fixé le motif.

Preuve causale : le test utilisant l'extrait OCR réel échouait avant changement
avec `Expected: content`, `Actual: navigation`. Après changement, cet extrait
est `content`, sans motif de navigation, et
`IsIdentityOnlyCandidateEvidence(..., "Charlotte")` renvoie faux. Un contre-test
de huit titres numérotés sous « Techniques du livret », sans corps de phrases,
reste `navigation`.

Contrôles exécutés sur les sources actuelles :

- causal après correction et contre-test : 2 réussites, zéro échec ;
- classifieur et matériaux de source : 77 réussites, zéro échec ;
- classifieur, projection de chunks, profils, matériaux de source et provider
  avancé : 492 réussites, zéro échec ;
- suite backend complète : 2 435 réussites, zéro échec et trois tests live
  explicitement ignorés ;
- `git diff --check` : aucune erreur.

Aucun appel OpenAI n'a été exécuté pour A850. Le registre autorisé reste à
39,24170860 USD sur 40 USD, soit 0,75829140 USD calculé. Le pilote A849 reste
rejeté : cette correction retire sa cause fatale observée, mais ne transforme
pas sa grille en réponse approuvée. Il manque toujours une exécution terminale,
le Critic, l'audit sémantique des vingt affectations, les cartes physiques et la
répétition probabiliste sur un candidat figé. Le produit reste
`TESTE_NON_APPROUVE`.
