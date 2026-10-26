# Titres observés conservés dans la couverture de retrieval

## Cause établie au-delà des faits déjà testés

La campagne `worker-recipe-e5-final-v2` sur la DLL `00A011D0…107ABD7` passe ses quatorze faits annotés. La contre-revue a aussi comparé les références de blocs canoniques aux chunks réellement stockés. Deux observations `section_header`, pages physiques 2 et 4, sont absentes de ces références et leur texte complet n'apparaît pas dans les chunks de leur page.

Les observations et leurs titres existent encore dans le bundle et le SectionTree : ce n'est pas une disparition du fichier brut. Mais une archive canonique ou une liste de sections ne remplace pas la présence dans `retrieval_chunks`, utilisée par les chemins de recherche et de lecture du RAG. Aucune exécution HTTP supplémentaire n'est inventée pour cette conclusion : elle repose sur les captures SQL et les chemins du code.

Le projecteur traitait un heading uniquement en le plaçant dans `activeHeadings`, puis passait à l'atome suivant. Un titre intermédiaire remplacé par un pair avant tout corps, ou un titre final suivi seulement d'observations physiques non rattachées, ne devenait jamais contexte d'un chunk. Il n'était jamais émis par ailleurs.

## Reproduction causale et correctif

La nouvelle partial de tests `DoclingCanonicalDocumentAdapterTests.UncoveredHeadings.cs` emploie uniquement un document synthétique ; aucun texte du livre privé n'est copié dans la fixture. Elle vérifie huit cas :

- titre intermédiaire remplacé avant contenu ;
- titre terminal suivi de récupération native non rattachée ;
- page portant seulement un titre long, avec deux budgets de découpage ;
- deux occurrences de titre identiques, sur la même page puis sur deux pages ;
- témoin d'un titre déjà présent comme contexte, sans duplication ;
- témoin d'un compteur de page prouvé, qui reste exclu.

Capture RED `tests/uncovered-heading-red-v1`, run `10b2db9d82724d9c958c6b9864370a82` : six échecs causaux et deux témoins verts, sources stables. Le binaire reproduisant le défaut est `00A011D0DB86BFDAEF82972DDE6B1C7CD0B142C79D43C871C1128E4CF107ABD7`.

La responsabilité de rétention est extraite dans `DoclingCanonicalRetrievalProjector.UncoveredHeadings.cs`. Après les fusions normales, elle repère les headings observés non couverts par un chunk réellement publiable. La couverture exige leurs **identifiants de bloc et leur page physique** ; l'égalité du texte ne permet pas de confondre deux occurrences.

Chaque occurrence non couverte est conservée avec ses IDs et ses références de provenance, comme `physical_page_context_v1`. Elle ne reçoit ni SectionOrdinal, ni SectionTitle, ni HeadingPath. Le découpage respecte `maxWords` et les fragments restent sur leur page. Le tri par page puis ordre des objets source intervient seulement lorsqu'une rétention est ajoutée ; la réindexation finale existante produit les nouveaux ChunkIndex utilisés par les lectures voisines.

Le signal **`canonical_uncovered_observed_heading_context`** est porté par `ExtractionQualitySignals`, conservé avec le chunk et ses métadonnées. La composition `canonical_physical_context_not_asserted` distingue la co-localisation d'une association de sujet. Aucun titre n'est inventé, aucun rattachement au corps suivant ou précédent n'est ajouté.

Capture GREEN `tests/uncovered-heading-green-v1`, run `0607e056afac4783bac91cfa15dc0556` : huit PASS, aucun échec, sources stables. SHA du binaire testé : `A21D64DA8465CCB39449008FDD5106C81860B4D90E1290A2FA83340F3F827BC7`.

La suite complète `tests/unit-all-final-v3`, run `7aa68db8f4ef45288d2345324a686be7`, confirme ensuite 5 255 PASS, zéro échec et un non-exécuté explicite sur les quatre projets. Les empreintes avant/après sont identiques et le même SHA backend A21D64DA…F827BC7 est enregistré.

## Portée et vérifications à venir

La contre-revue indépendante de l'agent atomicité ne relève pas de défaut matériel dans ce delta. Les titres déjà couverts ne sont pas dupliqués, les compteurs prouvés ne sont pas promus, et les headings explicitement de navigation restent exclus de ce repli de contenu. Cette correction vise les observations de heading ; elle ne constitue pas un contrôle exhaustif de tous les objets documentaires.

Les tests ciblés et la suite complète verts ne sont pas une nouvelle ingestion du livre ni du corpus. Les nouvelles campagnes du worker doivent porter leurs propres hashes et leurs propres nombres de chunks ; les anciens chiffres 261 et 158 restent attachés à leurs lancements historiques. Aucun modèle payant, service, build .NET ou Git n'a été lancé par l'agent chargé du correctif ; les captures sont orchestrées par l'agent principal.

Le produit demeure `TESTE_NON_APPROUVE`.
