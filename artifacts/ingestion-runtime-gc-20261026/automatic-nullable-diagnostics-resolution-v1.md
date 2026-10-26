# Diagnostic réel et correction du contrat nullable de recherche

Le premier parcours automatique scanner Production s’est arrêté au contrôle
recherche→lecture, avec cleanup réussi. Les captures originales restent intactes.
Le diagnostic de l’opérateur a établi que l’identité courante, le fait trouvé,
la lecture exacte et le fait physique canonique étaient tous vrais. Le seul
contrôle faux était `noDegradedRetrievers` : l’API n’émettait pas ce champ vide.

Le contrat C# est explicite : `DegradedRetrievers` est une liste nullable et
`DegradedRetrieverErrors` un dictionnaire nullable. `CanonicalRetrieval.cs:317`
retourne null si ces collections sont vides ; la sérialisation omet alors les
propriétés. Exiger uniquement `[]` était donc un faux échec du harnais. À
l’inverse, l’ancien test par simple falseness dans le vérificateur corpus
acceptait des mauvais types vides et ignorait le dictionnaire d’erreurs.

La garde pure commune `retrieval_metrics_healthy` dans
`canonical_search_checks.py` exige maintenant un objet `metrics` explicite :

- liste de retrievers : propriété omise, null ou tableau vide acceptés ;
- dictionnaire d’erreurs : propriété omise, null ou objet vide acceptés ;
- tout contenu non vide ou mauvais type est refusé, même booléen faux ou chaîne
  vide. Un objet n’est pas une liste et un tableau n’est pas un dictionnaire.

La même garde est appelée par les positifs et négatifs corpus, les faits
retrieval et la recherche d’un document supprimé dans AUTO. Aucune comparaison
d’identité, de texte exact, de source/révision/chunk ou de preuve physique n’est
relâchée. Aucun code C# ni contrat API produit n’est modifié.

La preuve RED `tests/automatic-ingestion-nullable-red-v1` comporte 69 méthodes,
dont 5 échouent causalement, et 12 assertions échouées en comptant les sous-tests.
Le compteur générique précédent comptait ces assertions comme des méthodes :
`counting-clarification.json` explicite 64 méthodes PASS et 5 FAIL, sans modifier
le manifeste ni le log d’origine. Il n’y a aucune erreur ou aucun skip ; les
21 fichiers inventoriés sont stables.

La preuve GREEN `tests/automatic-ingestion-nullable-green-v1/manifest.json`
comporte 69/69 PASS, 0 échec, 0 erreur, 0 skip et les 21 fichiers inchangés
avant/après. Les réponses de test sont passées par un aller-retour JSON qui
reproduit l’omission réelle, distinct du cas nul et des collections vides. Les
contre-témoins couvrent les dégradations explicites et les valeurs mal typées.

L’auteur n’a exécuté aucun service, HTTP réel, SSH, .NET ou Git. La reprise
scanner se fait dans un nouveau namespace par l’opérateur ; l’échec initial ne
devient pas rétroactivement un succès. La sonde retrieval reçoit les mêmes
helpers gelés avant sa première exécution réelle.
