# Transport SQL Windows : conservation exacte du texte projeté

Un défaut de la fixture Python modifiait les retours de ligne de certains textes entre la projection C# et le fichier SQL. Le checksum restait celui du texte initial. Le défaut est reproduit sur les octets réellement écrits, puis corrigé ; les douze tests Python passent. Les anciennes traces confirment que les retours de ligne supplémentaires ont atteint le modèle dans les cas 02 et 03. Ce constat ne démontre pas que leurs réponses factuelles étaient fausses.

## Cause et correction relues

`canonical_seed` valide d’abord chaque checksum sur le texte UTF-8 du snapshot de production. Il construit ensuite une chaîne SQL contenant ces textes dans des littéraux et conservant les checksums validés.

L’écriture précédente ouvrait le seed en mode texte avec la traduction de nouvelles lignes par défaut. Sous Windows, les caractères LF écrits deviennent CRLF, y compris lorsqu’un LF est déjà précédé d’un CR dans le texte fourni par C#. Un retour `CRLF` pouvait donc devenir `CRCRLF` **à l’intérieur d’un littéral SQL**, après le calcul et la vérification du checksum.

Les tests limités à la chaîne SQL en mémoire ne pouvaient pas observer cette transformation ultérieure. Des contrôles de faits utilisant des espaces ou des expressions régulières tolérantes peuvent également réussir malgré un retour de ligne supplémentaire.

La correction de l’opérateur parent écrit désormais le fichier neuf en mode binaire exclusif : `seed.open("xb")`, suivi de `sql.encode("utf-8")`. Aucune traduction de nouvelles lignes ni BOM n’est ajoutée. Les checksums de la projection ne sont pas recalculés pour légitimer un texte altéré.

Le nouveau contrat CLI examine les octets du fichier effectivement créé et exige la présence exacte des littéraux UTF-8 attendus pour les quatre champs concernés :

| Table | Champ couvert par son checksum |
|---|---|
| `document_units` | `text_content` |
| `retrieval_chunks` | `text_content` |
| `exact_match_entries` | `normalized_text` |
| `contextual_text_entries` | `text_content` |

Le choix de `normalized_text` pour les entrées exactes est volontaire : leur checksum porte sur le texte normalisé de production, pas sur l’extrait brut.

Le chemin d’ingestion produit utilise des paramètres Npgsql et calcule le checksum du texte préparé pour le stockage dans `DocumentFoundationRepo`. Le défaut démontré ici est celui de l’écriture du seed de validation Python sous Windows ; il ne constitue pas une preuve que cette même traduction est effectuée par l’ingestion produit.

## Reproduction et GREEN relus

L’opérateur parent a exécuté le nouveau contrat avant correction : **1 test, 1 FAIL**. L’assertion indique que le littéral UTF-8 exact du contexte projeté est absent du SQL écrit ; la capture privée montre les retours `CRCRLF` ajoutés.

Après correction, `python-transport-green-v3.log` rapporte **12 tests, 12 PASS**, comprenant les trois contrôles CLI natifs avec le helper précompilé. Il n’y a aucun test ignoré dans ce résultat.

- RED privé : `sql-byte-retention-red.private.log`, SHA-256 `DE06539475F651357DE73135A70D0AD3CF702E0D958AD99228DAB1D2C5F300F4`.
- GREEN : `python-transport-green-v3.log`, SHA-256 `8B9FCA367FBA61B076E1197FFCBC340A61A89DE64CFD2B5849BDF7F09554F734`.
- Python corrigé relu : `tools/validation-paid/prepare_real_corpus.py`, SHA-256 `28F8EBC09DEB49E7E7B430200F23C015DBB3E6D75652361F1C1509548F9EAB2E`.
- Tests relus : `tools/validation-paid/test_paid_corpus.py`, SHA-256 `7D166E11F59CC6E3729AEF46405F9704FE578EEAA742E6850D7B64EEA939FF12`.

Les logs Python ne constituent pas un manifeste exhaustif de sources/binaires comparable aux campagnes .NET. Les hashes ci-dessus identifient les fichiers relus ; cette revue n’invente pas un tel ancrage manquant.

## Portée historique constatée sur les cas 02 et 03

Une comparaison gratuite en lecture seule a analysé les littéraux des seeds sauvegardés, sans exécuter leur SQL ni redémarrer PostgreSQL. Elle respecte les apostrophes SQL doublées et les parenthèses des valeurs. Les valeurs sont rapprochées du snapshot par leurs ordinaux. Les deux campagnes possèdent le même snapshot C# `F372A3F63CEB1C4AA631E3EA09EFB191E59A9791B50702FB61D4BFB73DFC6DAF`.

Les résultats sont identiques pour chacune des deux campagnes :

| Lignes inspectées dans chaque seed | Texte du littéral différent du snapshot | Checksum conservé du snapshot | SHA-256 du littéral incompatible avec ce checksum |
|---|---:|---:|---:|
| 133 unités | 0 | 133 | 0 |
| 8 chunks | 8 | 8 | 8 |
| 169 entrées exactes normalisées | 0 | 169 | 0 |
| 8 contextes | 8 | 8 | 8 |

Les seize littéraux modifiés de chaque seed comportent des retours `CRCRLF`. Cette comparaison porte sur le contenu du **fichier SQL**, pas sur un SELECT historique de la base arrêtée.

- Seed cas 02 : SHA-256 `278ECE3062BCF391F778D730A31EABF887C79F87791222259AE09F35C521905F`.
- Seed cas 03 : SHA-256 `0DC5C33E8280CE9470A614E580D1FB5E01C9FA4AB07D18050165D474B62D9F9C`.

Les JSON privés des requêtes fournisseur ont aussi été décodés en lecture seule. Leur contenu confirme que ces retours de ligne ont été transmis dans les observations de recherche et au Writer : 46 occurrences `CRCRLF` dans le Writer du cas 02, 144 dans celui du cas 03. Les comptes ne sont pas des nombres de faits ou de citations ; une même observation peut être reprise dans plusieurs messages. Aucun texte source ni secret n’est copié dans ce rapport.

Les deux campagnes restent historiquement terminées avec `outcome=answered` et `canonicalSourceVerified=true`. La vérification d’identité documentaire contrôle document, révision, hash du PDF, identifiants et pages ; elle n’atteste pas à elle seule le recalcul du checksum de chaque texte SQL. Ce résultat ne doit donc pas être renommé en preuve d’égalité des octets de stockage. Inversement, le défaut de retours de ligne ne suffit pas à déclarer leurs réponses fausses.

## Garde supplémentaire et limites

L’agent runtime prépare un contrôle après publication du seed et avant création d’un nouveau job : compteurs et SHA-256 calculés en SQL sur les quatre tables ci-dessus. Il doit vérifier le texte **réellement stocké**, avec le bon champ pour les entrées exactes, et refuser toute divergence. Ce garde complète le contrat de fichier ; son exécution et son verdict devront être rattachés à leur propre preuve.

La présente revue n’a modifié aucun script, source produit, ancien seed, trace ou profil. Elle n’a lancé ni service, ni build, ni test, ni requête SQL/API, ni modèle, et n’a lu aucune configuration sensible. Seules une analyse locale des captures autorisées et la création de ce rapport public ont été effectuées.

La fidélité native obtenue avant transport reste une preuve distincte. La correction des octets et le futur garde SQL ne valident seuls ni l’ingestion complète, ni tous les formats, ni le planning de repas. Le produit conserve le statut `TESTE_NON_APPROUVE`.
