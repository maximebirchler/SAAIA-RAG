# Correction préparée du corpus de validation natif

État : implémenté et contrôlé hors ligne ; **compilation du helper et préflight réel encore à effectuer par l’opérateur**. Aucun appel fournisseur, secret payant, service ou build exécuté par l’auteur de cette correction. Les anciennes campagnes et leurs seeds sont conservés.

## Divergences constatées

L’ancien seed plaçait une page complète dans chaque unité, chunk et entrée exacte ; `normalized_text` était seulement `text.lower()` et le compte de tokens estimé par caractères/4. La production extrait et normalise le PDF avec `PdfExtractor`, découpe les unités/candidats exacts, normalise leur ponctuation, compte les mots et calcule le checksum du texte normalisé. Les quatre valeurs exactes de l’ancien seed différaient de cette normalisation ; la page 5 entière dépassait aussi le budget exact de 1 800 octets UTF-8.

L’ancien contexte indexé était la page brute. `ContextualTextProjector` ajoute normalement le nom du document, les titres, le chemin de titres et le contexte voisin. Cette différence compte lorsque les requêtes incluent le nom du fichier. Elle ne prouve pas, seule, la cause de chaque recherche vide du premier essai payé : le diagnostic SQL des requêtes figées reste une preuve distincte.

La colonne `search_tsv` est générée par la migration 056 ; son absence dans les INSERT n’était pas un index manquant. Le substitut Qdrant renvoie toujours une liste vide : un protocole sans erreur ne constitue pas une validation du rappel vectoriel.

## Chemin corrigé

Le mode `--native-paid-corpus` de l’outil existant `SAAIA.IngestionGold` appelle directement les classes du backend : extraction native du PDF complet, sélection des pages explicitement choisies, extraction des sections/unités, projection structurelle des chunks, puis `IngestionProjectionStage.ProjectIndex`. Le même filtre de publication conserve les chunks et contextes publiables. Les algorithmes ne sont pas réécrits en Python.

`prepare_real_corpus.py` vérifie l’identité du PDF et du backend chargé, les pages, les checksums et les liens, puis transporte les vrais objets projetés et leurs métadonnées en SQL. Les `TokenCount` repris de production sont des comptes de mots séparés par espaces, pas des mesures du tokenizer du modèle : ils restent explicitement marqués comme estimations avec leur sémantique. Le manifeste conserve les hashes du helper et du snapshot privé. Les sorties/erreurs du helper sont conservées seulement sous `runtime/` dans des fichiers privés. Il n’existe aucun fallback vers l’ancien seed ou vers un build automatique.

Les entrées directes exigent `-ProjectionAssemblyPath` vers le helper précompilé. Le runner central transmet `-PaidProjectionAssemblyPath`. Avant toute lecture de clé, un contrôle exige que le backend voisin du helper ait le hash du profil ; le helper vérifie aussi l’assembly réellement chargée avant extraction et avant création du snapshot. Le test CLI de mauvais hash exige l’absence du snapshot comme du seed.

## Contrôles exécutés

- Neuf contrats Python de transport : PASS. Ils utilisent des objets synthétiques et ne prétendent pas valider l’ingestion.
- Trois tests CLI natifs : explicitement SKIP, car aucun helper précompilé n’a été fourni à cette exécution. Ils seront activés par `SAAIA_PAID_PROJECTION_DLL` après compilation coordonnée.
- Sept gardes PowerShell de compatibilité/transport : PASS, sur fichiers synthétiques jamais exécutés.
- AST des cinq scripts modifiés/ajoutés : zéro erreur lors du contrôle.
- Deux contrôles dry-run/absence de lecture de clé : PASS avec une nouvelle proposition `PROPOSED` compatible avec le backend du moment. L’ancien profil a correctement bloqué sur différence de hash ; il n’a pas été modifié. Cette proposition de contrôle devra être régénérée si le binaire change.

Une première invocation des tests dry-run sans leurs arguments obligatoires n’a pas exécuté ces tests ; le résultat PASS ci-dessus concerne l’invocation complète suivante. Aucun succès n’est déduit d’un simple code de sortie ou de tests ignorés.

## Validation suivante et limites

L’opérateur compilera le projet Gold, activera les trois tests CLI natifs puis lancera un nouveau préflight local sur le vrai PDF. Le rejeu des requêtes figées doit comparer les canaux et scopes avant tout nouvel essai payant. Aucun nombre de hits nouveau n’est annoncé ici.

Cette fixture réutilise les projections natives de production ; sa publication SQL reste contrôlée. Elle **ne valide pas** le worker d’ingestion de bout en bout, Docling, l’OCR, les profils documentaires, les embeddings, le rappel vectoriel, le routage du client ou WinUI. La projection et les voisins sont limités aux pages sélectionnées, même si l’extraction native considère le PDF entier. Le produit conserve le statut `TESTE_NON_APPROUVE`.
