# Fidélité littérale des tableaux français — correction ciblée

## Défauts démontrés

Le vrai worker a traité le PDF français `fr-table-critical-v2.pdf` (source SHA256 `c9482adbfd47104a1a81fb1d1087c602b2f0275a8860deb8373aec076a697371`). Les tableaux canoniques contiennent bien les nombres signés, les unités, les accents et la négation. Les sept chunks SQL portent des checksums exacts et leurs identités concordent avec l’API. La cause n’est donc pas une confusion de document, de révision ou de stockage.

Deux conversions distinctes expliquent les écarts :

- Le rendu ajoutait ` | ` entre les cellules. Une ligne factuelle continue dans les annotations de source n’était plus continue dans le texte de recherche ni dans la lecture API. Les quatre lignes critiques du tableau échouaient à ces deux niveaux.
- Docling a observé un tiret ASCII à la place du tiret cadratin de deux titres fusionnés. La sortie native conserve le caractère original ; ses deux blocs de titre sont isolés dans les mêmes régions physiques que les cellules. La ligne native globale récupérée n’avait pas corrigé la vue de la cellule.

L’inspection hors ligne des captures des douze documents porte sur **90 annotations critiques** : aucune absence factuelle au niveau canonique, quatre écarts retrieval et quatre écarts API limités à ce tableau. Les deux titres de tableaux présentent un écart typographique supplémentaire. Les autres écarts d’alternative de lecture sont traités séparément. Voir `fr-table-offline-source-inspection-v1.json` ; il s’agit d’une réévaluation des captures, pas d’une nouvelle ingestion.

## Correction

Les trois vues de tableau (legacy, canonique page/unité, retrieval) séparent maintenant les cellules par une tabulation. Les limites de lignes et les identités des cellules restent disponibles. Les normalisations de texte conservent cette séparation ; un pipe réellement présent dans une cellule reste littéral. Aucun consommateur du corpus de code ne découpe les cellules en interprétant le pipe de ces vues comme contrat.

Le nouveau composant `CanonicalNativeTableCellReconciler` sélectionne un texte natif seulement si un bloc et une cellule s’associent de façon unique dans les deux sens, avec au moins 70 % de recouvrement dans chaque direction, sur la même page physique, sans rotation. La différence autorisée reste limitée à un caractère de tiret séparateur entre des mots alphabétiques, sans changement de casse, d’accents, de mots, de nombres ou de négation. C’est une sélection du littéral natif observé, jamais une réécriture globale de caractères.

Un témoin hors ligne sur les véritables polygones capturés (`fr-table-spatial-source-witness-v1.json`) confirme deux associations uniques : recouvrements natif/cellule de 79,90 % / 77,58 % en page 1 et 98,39 % / 77,58 % en page 2. Il prédit l’admission des deux titres sans nouvelle exécution du composant ; il ne remplace pas la prochaine ingestion réelle.

Le texte Raw et le checksum de l’observation Docling sont conservés. Le littéral natif, son polygone, son moteur, son ordre et la cellule associée sont archivés dans un bloc canonique lié à la cellule. Ce bloc auxiliaire a un texte Retrieval vide ; la cellule porte la vue sélectionnée utilisée par le RAG. Il n’ajoute pas une preuve indexée autonome. Le manifeste mesure `reconciledTableCellCount` et nomme la politique de sélection sans journaliser de contenu documentaire.

## Preuves exécutées par le parent

- RED `table-literal-red-layout-green-v2` : **6 échecs table / 4 témoins PASS** ; les quatre lignes et deux titres reproduisent les défauts. La précédente tentative v1 ne compilait pas une construction de fixture ; elle n’est pas une preuve comportementale.
- GREEN `layout-association-red-table-green-v1` : **12 tests table PASS** ; les témoins supplémentaires vérifient le signe numérique et la négation. Les tests de titre vérifient Raw/hash conservés, lien au bloc natif, validité canonique et idempotence. Le lot complet comporte 25 cas, 20 PASS et 5 échecs attendus appartenant à la correction distincte d’association de sections. Les sept cas de layout sont GREEN.
- Les deux manifestes sont stables côté empreinte source et les SHA256 de leurs TRX ont été revérifiés indépendamment. Les résultats ci-dessus ne constituent pas une suite complète ni une nouvelle validation worker réelle du correctif.

## Limites et prochaine vérification

Les pages scannées sans texte natif, les cellules ambiguës, les blocs trop larges, les rotations, les valeurs contradictoires ou les différences de mots ne sont pas réinterprétés par ce composant. Une observation alternative peut être conservée sans que le logiciel choisisse automatiquement la bonne valeur. Le taux de recouvrement est une garde d’association, pas un score de confiance OCR.

Il reste à relancer l’ingestion réelle du PDF avec la DLL finale et à vérifier les deux titres canoniques, les quatre lignes retrieval/API, les ancrages et les identités SQL/Qdrant. Les attentes factuelles n’ont pas été assouplies. Le correctif ne qualifie ni tous les tableaux possibles, ni toute l’ingestion, ni un planning produit par un modèle.
