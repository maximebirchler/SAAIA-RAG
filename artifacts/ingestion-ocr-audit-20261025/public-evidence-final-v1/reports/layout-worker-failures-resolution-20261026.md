# Analyse des six échecs de mise en page du worker réel

Ce lot part des observations immuables `worker-full-e5-071-v1`, exécutées sur la DLL `62D5776E…144FC4A`. Il distingue deux défauts du produit, un défaut de sélection des preuves géométriques et trois annotations de test incorrectes ou dépendantes du découpage du moteur. Les anciennes captures ne sont ni modifiées ni requalifiées en exécution réussie.

## Causes et corrections

| Cas réel | Cause établie dans les captures et le code | Traitement |
|---|---|---|
| Deux colonnes | La première région Docling contenant le marqueur couvrait les deux colonnes. L'évaluateur la choisissait avant le bloc natif alternatif précisément ancré. | Choix d'une observation ancrée portant le texte le plus spécifique ; aucune utilisation de la position attendue pour décider quelle preuve retenir. |
| Trois colonnes | Les marqueurs existaient dans des cellules avec coordonnées, mais une récupération textuelle sans polygone les masquait dans l'évaluateur. L'alternative de lecture était également absente. | Les cellules sont des observations géométriques consultables ; correction distincte de la garde d'admission de l'alternative. |
| Colonnes inégales | Les dix faits sont présents et l'alternative restitue les neuf marqueurs dans l'ordre attendu. Le contrat exigeait néanmoins deux blocs primaires `text` alors que le moteur en produit un fusionné. | Maintien des dix faits et de l'ordre alternatif complet ; remplacement du comptage de segmentation par deux ancrages physiques indépendants, gauche et droite, page 1, avec au moins un bloc primaire. |
| Titre écrit tard dans le flux PDF | Le contrat exigeait une table alors que le générateur figé ne dessine que deux récits, un titre et un pied de page. | Retrait de cette exigence sans objet. Faits exacts, titre, ordre et géométrie restent exigés. |
| Colonnes et véritable tableau | Le texte natif est déjà dans l'ordre géométrique, mais l'observation canonique fusionne les deux colonnes par ligne. La garde écartait l'alternative dès que son ordre égalait le texte natif. | Une concordance avec le texte natif ne masque plus un désaccord matériel avec l'ordre canonique. Le tableau et ses cellules restent intacts. |
| Tableau structuré | Le contrat d'ordre exigeait `LAYOUT GOLD STRUCTURED TABLE`, alors que le générateur et le document capturé portent le tiret littéral. | Annotation corrigée avec le titre exact. Les signes ne sont pas effacés globalement ; forme 5×4, cellule fusionnée et cellule vide restent obligatoires. |

Le générateur est inchangé : SHA-256 `830b398bd98101f688bc72d951dc797376f3a03ef6f76970b610f18465b11d1b`, identique au pin du manifeste. Les douze PDF et leurs hashes attendus restent inchangés. L'ancien manifeste exact est conservé séparément dans `gold-manifest-before-layout-contract-fix.json` par le parent.

Les deux centres ajoutés au cas des colonnes inégales proviennent de ses positions de dessin (18 et 111 mm, ligne à 44 mm du haut) et de la métrique Helvetica 8,8 points du générateur. Ils n'ont pas été ajustés sur les boîtes produites par Docling : `(0.167465, 0.145477)` et `(0.616058, 0.145477)`, tolérance 0,04, page physique 1.

## Défaut supplémentaire : fausse attribution à la section de droite

`DoclingCanonicalDocumentAdapter.BuildSections` construit les liens de section avant la réconciliation. En présence de deux titres suivis d'un bloc fusionné, il rattache ce bloc au dernier titre. La surface primaire réelle du cas deux colonnes pouvait donc exposer les faits LEFT sous RIGHT COLUMN. Une alternative correcte ne suffisait pas à rendre cette autre assertion honnête.

La reproduction couvre séparément le bloc, le tableau, les unités, les chunks et le `SectionTree`. Chaque variante possède aussi une deuxième page indépendante qui doit conserver son association explicite. Un témoin sans désaccord conserve son titre et ses liens.

Après acceptation d'une alternative géométrique démontrant un désaccord, la politique commune conserve les observations, les titres, les IDs et les polygones. Elle marque le rattachement des blocs de cette page `not_asserted`, avec contexte `physical_page`, retire leurs seuls liens de contenu déjà inférés et détache le parent inféré des titres concernés. Les titres restent consultables. L'entrée de cette page interrompt l'héritage d'un titre antérieur. Les lignes et cellules des tableaux gardent leur structure interne ; aucun nouveau titre n'est déduit de la géométrie. Les autres pages ne sont pas rétroactivement reclassées.

## Preuves exécutées et portées

- `tests/native-layout-canonical-order-red-v1` : capture .NET du parent, 2 échecs causaux et 5 témoins verts avant correction de la garde.
- `tests/layout-association-red-table-green-v1` : 5 échecs causaux d'association et témoin sans conflit vert. Le même lancement vérifie aussi les 7 tests de layout et les 12 tests de tableau verts après leurs corrections respectives.
- `layout-geometry-red-v1.json` : Python pur, 4 échecs causaux et 2 témoins verts avant correction de la sélection géométrique.
- `layout-source-contract-red-v1.json` : Python/AST pur, 3 annotations en échec et 2 témoins verts avant correction du manifeste.
- `layout-contract-geometry-green-v2.json` : 35 tests Python verts, dont signes, décimales, négations, pages physiques, identité source, pins et contrats d'acceptation. Aucun moteur ni appel réseau exécuté.
- `layout-capture-replay-geometry-v1.json` puis `layout-capture-replay-contracts-v2.json` : relecture exacte des bundles déjà capturés avec l'évaluateur corrigé. Le second passe 4/6 cas ; les deux autres échouent toujours sur les alternatives absentes dans l'ancienne DLL, ce qui est attendu et conservé explicitement.

Les replays ne relancent ni extraction, ni worker, ni SQL, ni Qdrant. Les identités d'assemblage et les valeurs de chunking y restent une preuve du lancement original : elles ne sont pas prétendues revérifiées par le replay. Les traces natives intermédiaires non présentes dans les bundles n'ont pas été inventées pour rejouer le moteur ; le défaut de garde est reproduit séparément par les fixtures C#.

## Fichiers du lot

- Produit : `CanonicalNativeLayoutReconciler.cs`, `CanonicalSourceAssociationPolicy.cs`, transition de page de `DoclingCanonicalRetrievalProjector.cs`.
- Tests .NET : classe existante rendue `partial`, nouvelle partial `CanonicalNativeLayoutReconcilerTests.NativeAlreadyOrdered.cs`, nouvelle partial `DoclingCanonicalDocumentAdapterTests.LayoutAssociation.cs`.
- Validation : `canonical_evaluator.py`, `test_canonical_geometry_selection.py`, `test_layout_source_contracts.py`, annotations de `ingestion-acceptance-gold.v2.json`.

Les changements de cellules/tirets et les séparateurs de tableaux appartiennent au lot indépendant de l'agent atomicité ; la recherche et ses contrôles négatifs appartiennent au lot du harnais. Cette analyse ne les revendique pas.

## Limites restantes à la rédaction

Les nouveaux chemins d'association attendent la capture verte centralisée et la ré-ingestion réelle sur le binaire final. Les replays ne les valident pas. Deux observations littérales identiques et également précises peuvent rester ambiguës géométriquement ; la sélection ne les départage jamais en se rapprochant artificiellement du résultat attendu. La garde conserve les seuils de fidélité lexicale, les protections contre fragments et blocs dominants ; elle ne transforme pas une géométrie incertaine en nouvelle hiérarchie sémantique.

Le statut global reste `TESTE_NON_APPROUVE`. Aucun modèle payant et aucun planning de repas n'ont été testés dans ce lot.
