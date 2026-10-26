# Résolution de l'export public Linux v3

L'exporteur est gelé et prêt pour le scellement des campagnes réelles. Il projette seulement des champs publics explicitement autorisés ; il ne réévalue ni le produit ni les faits ni les signatures cryptographiques de configuration.

## Périmètre livré

Le dossier `public-export-tools-v3` contient l'exporteur, une copie immuable de la base v2 dont l'empreinte est contrôlée au chargement, le protocole, les tests purs et leurs captures RED/GREEN. Les anciens outils et leurs preuves ne sont pas remplacés.

Les neuf rôles Production sont distincts : corpus Gold, livre réel, cycle de vie, remplacement défaillant, recherche des faits Gold, recherche des faits du livre, puis trois profils automatiques scanner, watcher et defaults. Chaque rôle est facultatif dans une sélection partielle. Une sélection complète de ces rôles produit 44 projections JSON ; les 90 et 16 résultats factuels individuels sont conservés, avec leurs identifiants et empreintes, sans texte des requêtes ni texte documentaire. Les variantes Development ont des noms et qualifications séparés.

Des entrées optionnelles bornées permettent les contrôles Docling/OCR, les attestations publiques des cinq conteneurs et le contrôle négatif de signature. Ce dernier conserve notamment son vrai code de sortie 139 et ne se transforme pas en preuve d'ingestion réussie. Son schéma historique sans runId ni date reste explicite.

Sont exclus : propriétaires privés, configuration, clés, journaux bruts, observations privées, SQL, documents, vecteurs et payloads. Les identités de modèle publiques sont limitées à la forme relative Hugging Face `organisation/modèle` ; aucun chemin absolu n'est admis comme model_id.

## Vérifications exécutées

| Capture | Résultat | Portée |
| --- | --- | --- |
| `public-export-tools-v3/tests-red-v1/manifest.json` | 37 tests, 35 PASS, 2 FAIL, aucun error/skip | Deux défauts de garde reproduits avant correction : même CID accepté pour plusieurs rôles ; préfixe de clé synthétique non refusé. |
| `public-export-tools-v3/tests-green-v1/manifest.json` | 42/42 PASS, sept fichiers stables | 29 tests v3 et 13 tests v2, uniquement JSON fabriqués et répertoires temporaires ; aucun réseau, sous-processus ni secret lu. |
| Capture centrale directe communiquée par root | 29/29 PASS | Suite v3 seule ; elle ne constitue pas un nouveau total de 42 tests. |
| Projection réelle partielle communiquée par root | PASS, 20 fichiers | Quatre rôles Production : Gold, livre, cycle de vie, remplacement défaillant. La commande a été exécutée par root, pas par l'auteur de cette note. |

Le sceau de cette première projection réelle partielle, sous `public-export-partial-v3`, a pour SHA256 `3a6c96a8024e371dcac14845f15b814a7805a4f5144560253e42cf71c96de2c74`. Ce résultat valide la compatibilité des quatre formes reçues avec la projection ; il n'affirme pas que les cinq rôles encore absents ont déjà été exportés.

La contre-revue indépendante de `public_export_v3_review` a conclu à l'absence de défaut matériel restant sur le snapshot gelé. Le relecteur n'a exécuté aucun test ni modifié de fichier. Il a confirmé les limites de portée ci-dessous.

## Empreintes gelées

| Fichier | SHA256 |
| --- | --- |
| `export-linux-public-proofs-v3.py` | `bab8f2b5e165cecf949e94ca5b962866bd7e35f8d58c89d8769fef7f1d11f5d8` |
| `public_projection_base_v2.py` | `27d709df697eab65d8ba835634954e4290753a83859ac1417d18d7d9da5dc862` |
| `PROTOCOL-v3.md` | `5bc23b703966a7449d15f5eadd46e7f8c66618daab7666bec65ce86500d32ebb` |
| `test_public_export_v3.py` | `5ffd84d4a23da60eb478259c3c119125bce133df927dbd1c4b2dc26360f27243` |

Les trois premières empreintes ont été relues depuis le disque après la capture GREEN et concordent avec son inventaire.

## Scellement et limites

L'exécution exige un nouveau répertoire de sortie. Elle contrôle les fichiers sources avant projection puis avant scellement, produit les SHA256 des sources et des exports, et inscrit les associations rôle, namespace, schéma et famille de qualification. Une source changeante provoque un refus ; une sortie partielle sans sceau reste invalide. La vérification d'une copie exige le SHA256 du sceau transmis séparément et l'inventaire exact, sans fichiers supplémentaires.

Les lectures Linux utilisent les vérifications de fichier régulier, lien, descripteur et stabilité d'inode/métadonnées décrites dans le protocole. La vérification locale Windows exige que la copie soit figée et non hostile ; elle ne promet pas les mêmes primitives Linux. Le sceau prouve l'intégrité des copies et leurs associations, pas la vérité intrinsèque d'un résultat fourni, la validation du modèle, ni une nouvelle validation cryptographique de configuration.

Aucun service, appel API, SSH, construction .NET ou opération Git n'a été effectué pour ce lot par cet agent. Les producteurs sont restés gelés. L'export complet des résultats réels restants sera un nouveau contrôle à effectuer par root lorsque ces résultats seront disponibles.
