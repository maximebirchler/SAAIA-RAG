# A789 — Reconnaître « comparez » avant la clarification locale

La demande BH6-020 nomme deux manuels et demande de comparer leurs voies de signalement et protections. À `3a5d0a313dafd8637b05eca337a6e95baa09884b`, le petit modèle de ce PC demande encore de choisir un seul manuel (8,365 secondes). Le registre fournisseur reste inchangé ; le serveur avancé est désactivé pour ce diagnostic local.

Le détecteur commun de comparaison reconnaissait `compare` et `comparer`, mais omettait `comparez`. Le routage de frontière ne pouvait donc pas corriger la mauvaise interprétation locale de cette demande. Le détecteur reconnaît maintenant aussi cette forme impérative.

Trois nouveaux cas ont échoué avant correction : la demande réelle Howard/modèle de manuel, une comparaison de deux normes nommées et une comparaison de deux documents non identifiés. Ils passent après correction : les deux demandes identifiées vont vers la capacité avancée ; la demande déictique non identifiée exige les références, sans supprimer cette clarification nécessaire. Suite client complète : 2 298 réussites, zéro échec, un test réel non exécuté.

Preuves sous `artifacts/reprise-pc-20260908/a788-comparison-sources-20260913` : `comparison-imperative-red.trx` (3 échecs, 36 réussites) et `client-comparison-imperative.trx`.

Le diagnostic r5 était non probant : le lanceur exécutait un ancien binaire Release avec `--no-build`, après une suite compilée en Debug. Son résultat original est conservé et annoté séparément par `execution-invalidity.json`. Il ne démontre pas un échec du nouveau détecteur.

Après compilation Release et contrôle du binaire exécuté, r6 à `f08473b4d6c8fbde6eac4e9e1b50a14e56f1635f` transfère la demande vers la capacité avancée avant recherche, sans choix erroné entre les manuels : 4,762 secondes, trois appels Qwen locaux, zéro source et zéro appel OpenAI. Le trace indique `explicit_documentary_comparison_outside_local_envelope`. Git et binaire restent inchangés, registre fournisseur intact, port 1234 libre et environnement restauré. Preuves : `local-comparison-imperative-green-r6/execution-seal.json`, `results/*.jsonl` et `resource-shutdown.json` dans le répertoire A781.

Cela valide le transfert local de ce cas consommé, une fois. La réponse serveur et une acceptation aveugle restent à vérifier. Produit TESTE_NON_APPROUVE.
