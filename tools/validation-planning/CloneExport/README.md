Export de la source Linux réingérée, exclusivement en lecture SQL et sous owner explicite.

`python3 export_owned_linux_corpus.py --owner <owner.json> --owner-sha256 <sha256> --worker-observation <worker-corpus.private.json> --worker-observation-sha256 <sha256> --output <nouveau-dossier-owned>` prépare seulement le contrôle. Ajouter `--execute` uniquement lors de l'exécution Linux autorisée. Sans ce flag : aucun Docker/processus, aucune observation documentaire lue, aucun fichier créé. Le manifeste owner est lu et son SHA est contrôlé.

Le dossier réussi contient :

- `metadata.json`, contrat `saaia-owned-documentary-sql-clone.v1`, publié seulement après tous les contrôles et la fermeture du snapshot ; `result.json` distingue réussite et échec.
- `documents.dump` : format custom PostgreSQL 16, données seules, 30 tables explicites. Aucun rôle, clé API, migration de schéma, chat, tâche avancée ni historique d'administration.
- `schema.json` : versions des migrations, version PG/encodage/locale, colonnes/types/expressions générées et clés primaires réelles des 32 tables.
- `tables/<table>.jsonl` : toutes les colonnes des 32 tables, sous le même snapshot que le dump. Chaque ligne est le texte exact PostgreSQL `to_jsonb(full_row)::text`, UTF8, délimiteur LF. Les champs documentaires ne sont jamais réécrits. Ordre indépendant de la locale : chaque composant de clé primaire est trié par les octets UTF8 hexadécimaux de sa représentation JSON, collation `C`.
- `scope.private.json` et `errors/` : preuves et diagnostics privés, permissions restreintes.

Une transaction `REPEATABLE READ READ ONLY` conserve `pg_export_snapshot()` pendant chaque lecture et `pg_dump --snapshot`. Chaque sous-lecture importe ce snapshot avant les autres réglages. UTF8, UTC, DateStyle ISO/YMD, intervalstyle postgres, bytea hex et extra_float_digits=3 sont imposés. Le manifest fournit chemins relatifs, SHA256, tailles, nombre de lignes, ordre des colonnes, ordre de PK et empreinte de leur séquence.

Les en-têtes `ingestion_vector_plans` et leurs points sont exclus du dump : la restauration cible doit insérer les plans avec `manifest_sealed=false`, insérer les points exacts, puis sceller selon l'état source. Les triggers restent actifs ; aucune identité, URL cible, révision ou valeur finale n'est modifiée. L'export refuse les plans non scellés, retirés, incomplets ou sans publication courante. Les barrières de génération restent incluses. Une cible déjà remplie ne convient pas.

`contextual_text_entries.search_tsv` et `document_profile_search_entries.search_tsv` sont recalculées par PG lors du COPY puis comparées strictement au readback, au même titre que les autres colonnes. Une différence de locale Linux/Windows ne justifie aucune tolérance. Le clone cible doit comparer les octets des 32 JSONL après restauration, conserver les fichiers PDF originaux exacts sous son DocumentsRoot et utiliser seulement sa propre authentification synthetic tenant. Les endpoints TEI/Qdrant se configurent pour la lecture par tunnel ; les endpoints immuables du registre ne se réécrivent pas et les workers de mutation/GC restent arrêtés sur la cible.

Le succès de cet export ne valide ni la restauration Windows, ni une réponse de modèle, ni la qualité sémantique des recettes. L'exécution réelle n'est pas implicite dans les tests purs.
