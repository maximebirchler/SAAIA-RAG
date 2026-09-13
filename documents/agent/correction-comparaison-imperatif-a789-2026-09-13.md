# A789 — Reconnaître « comparez » avant la clarification locale

La demande BH6-020 nomme deux manuels et demande de comparer leurs voies de signalement et protections. À `3a5d0a313dafd8637b05eca337a6e95baa09884b`, le petit modèle de ce PC demande encore de choisir un seul manuel (8,365 secondes). Le registre fournisseur reste inchangé ; le serveur avancé est désactivé pour ce diagnostic local.

Le détecteur commun de comparaison reconnaissait `compare` et `comparer`, mais omettait `comparez`. Le routage de frontière ne pouvait donc pas corriger la mauvaise interprétation locale de cette demande. Le détecteur reconnaît maintenant aussi cette forme impérative.

Trois nouveaux cas ont échoué avant correction : la demande réelle Howard/modèle de manuel, une comparaison de deux normes nommées et une comparaison de deux documents non identifiés. Ils passent après correction : les deux demandes identifiées vont vers la capacité avancée ; la demande déictique non identifiée exige les références, sans supprimer cette clarification nécessaire. Suite client complète : 2 298 réussites, zéro échec, un test réel non exécuté.

Preuves sous `artifacts/reprise-pc-20260908/a788-comparison-sources-20260913` : `comparison-imperative-red.trx` (3 échecs, 36 réussites) et `client-comparison-imperative.trx`. Un diagnostic local après gel reste requis pour confirmer le chemin réel de BH6-020. Il devra vérifier un transfert avant recherche et l'absence de demande de choix entre les deux manuels ; il ne prouvera pas encore une réponse serveur correcte. Produit TESTE_NON_APPROUVE.
