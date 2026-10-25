# Préflight du parseur PostgreSQL

Les fixtures Windows `validation-winui/start-environment.ps1` et `test-postgres-isolated.ps1` créent désormais leurs nouveaux clusters avec `--encoding=UTF8` et `--locale=English_United States.1252`. Le suffixe du nom de locale Windows ne change pas l'encodage de stockage, qui reste UTF-8. Aucune instance existante n'est modifiée. `-PgLocale C` reste disponible pour une expérience négative explicite.

Ce choix découle d'une reproduction PostgreSQL 16.14, avec les mêmes octets SQL/PDF et les mêmes requêtes : dans une base `LC_CTYPE=C`, le parseur full-text reconnaissait `ingrédients…` comme un mot unique incluant l'ellipse Unicode. La requête contenant `ingrédients` ne correspondait donc pas. La locale Windows explicite rétablit la séparation de la ponctuation et la casse Unicode. L'ajout de `COLLATE` à l'expression SQL a été testé et n'a pas corrigé ce parseur ; aucune migration ni heuristique de recherche n'en découle.

Le nom de locale par défaut concerne ces outils Windows, qui utilisent les binaires PostgreSQL locaux et les cmdlets Windows. Il ne constitue pas une recette de configuration Linux. Une nouvelle machine peut fournir explicitement un nom de locale compatible via `-PgLocale` ; le comportement observé prime sur le nom.

Avant de démarrer un backend payé, `Start-OwnedPaidBackend` exécute quatre sentinelles gratuites dans la base owned vérifiée :

- ellipse et tiret cadratin séparant deux mots ;
- conversion de casse Unicode ;
- conjonction full-text stricte, avec deux contrôles négatifs ;
- maintien de la distinction entre un mot accentué et sa variante sans accent.

Le contrat est identique au diagnostic générique de démarrage du backend. Les expressions SQL de ces deux frontières sont petites et indépendantes ; elles n'ajoutent aucun algorithme de normalisation ni règle de domaine. Le résultat, la version PostgreSQL et la locale réelle sont conservés dans `text-parser-conformance.json`. Tout échec, résultat incomplet, doublon ou succès mal typé bloque le démarrage du worker payé. Une base `C` défectueuse ne peut ainsi pas consommer du budget en produisant un faux déficit documentaire.

Les requêtes privées sont conservées comme fichiers UTF-8 sans BOM, exécutés avec `psql -f` dans une transaction `READ ONLY`. Les flux stdout et stderr sont capturés séparément et décodés explicitement en UTF-8 ; les notices PostgreSQL restent privées et ne polluent pas les résultats JSON. Le client SQL a un délai maximal de 60 secondes et ses requêtes un `statement_timeout` de 30 secondes. La preuve financière locale reste distincte du solde ou de la facture du fournisseur.
