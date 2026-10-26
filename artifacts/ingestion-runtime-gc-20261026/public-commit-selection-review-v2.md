# Contre-revue de la sélection publique v2

Avis favorable sur le périmètre et la confidentialité des 400 candidats de `public-commit-selection-v2.json`, avec les ajouts et rafraîchissements explicites ci-dessous. Cette note est une revue en lecture seule ; elle ne constitue ni un staging, ni une approbation Git, ni une nouvelle qualification des services.

Le DRAFT relu a pour SHA256 `bf34d235e75ee7d58ca132ad7611262d913fc9a02642b16c32aad089502a7767`. Il déclare 27 228 318 octets, sans doublon, fichier manquant, lien symbolique candidat, archive, fichier de configuration réel, store de clé, donnée de base ou modèle. Au premier contrôle, les 400 SHA et tailles concordaient. Root a ensuite déclaré la mise à jour du rapport final ; la relecture de fin identifie uniquement `RAPPORT-FINAL-QUALIFICATION-INGESTION.md` en différence avec ce DRAFT. Son contenu courant a été rescanné ; son SHA doit être rafraîchi dans la sélection finale.

Le scan des candidats et des huit ajouts finaux proposés n'a trouvé qu'un motif de clé : une entrée synthétique du test de rejet de secrets `public-export-tools-v3/test_public_export_v3.py`, ligne 150, dans `test_raw_fields_duplicates_secrets_and_nonfinite_values_fail_before_projection`. Aucune valeur n'a été affichée. Les reçus Telegram ne contiennent que `part`, `sentAtUtc` et `status`, sans identifiant de conversation ni jeton. Aucun store privé, clé réelle, blob DPAPI de profil ou fichier d'environnement réel n'a été ouvert pour cette revue. Un scan de motifs n'est pas une garantie contre tous les formats possibles de secrets.

Les exceptions d'extension sont justifiées comme **sources** : migration SQL 072, requête d'inventaire readonly sans données capturées, `.env.example` versionné et PDF synthétique incorporé aux tests OCR. Le SHA et la taille de ce dernier concordent avec son manifeste de génération. Aucun de ces fichiers n'est une capture documentaire privée.

Les deux lots publics finaux sont entièrement sélectionnés et leurs inventaires sont exacts :

| Lot | Fichiers | Vérification |
| --- | ---: | --- |
| `public-export-production-final-v4` | 55 | Tous les SHA et tailles correspondent au sceau ; aucun fichier manquant ou supplémentaire. |
| `public-supplement-final-v4` | 17 | Tous les SHA et tailles correspondent au sceau ; aucun fichier manquant ou supplémentaire. |

Les 72 JSON ne contiennent aucun champ brut de texte documentaire, requête, vecteur, payload, environnement, clé ou journal, ni chemin privé absolu. Les cinq `model_id` sont des identités Hugging Face relatives. Les résultats individuels des 90 faits Gold et des 16 faits du livre sont conservés. Ce contrôle vérifie l'intégrité locale et le périmètre des copies ; il ne renouvelle pas une attestation externe de transfert.

Les preuves finales sont correctement retenues : Unit run `4e87ad51184f4572966e89a099f9b221`, **5 323 Passed et 2 NotExecuted** ; PostgreSQL run `bc44cc73f4214458bc9e1e52c86e501a`, **350 Passed et 2 NotExecuted**. Ces nombres viennent des vrais enregistrements XML `UnitTestResult`, pas du seul compteur `notExecuted` du TRX. Les cinq TRX sont présents et conformes aux SHA de leurs manifestes. Les deux campagnes annoncent une source stable, avec la même empreinte avant et après : `9e7fc24261cad2d2624f0feb053fd93520a519b80aed1830e69d67a6b10f1bc7`.

La provenance du build final v4 est également sélectionnée : manifeste de 445 fichiers et comparaison source/index. Cette dernière déclare 443 égalités exactes et deux différences limitées aux fins de ligne autorisées. Je n'ai pas réinterrogé l'index Git : le contrôle final des blobs stagés reste une opération de root.

Six éléments de reproduction lifecycle manquent encore au DRAFT v2 et sont à ajouter explicitement :

- `run-production-lifecycle-final-v4.py`
- `production-lifecycle-final-v4-source-pins.json`
- `production-lifecycle-final-v4.md`
- `test-production-lifecycle-final-v4.py`
- `tests/production-lifecycle-operator-pure-v1/manifest.json`
- `tests/production-lifecycle-operator-pure-v1/tests.log`

Le dernier journal est celui des tests purs avec transports simulés, sans moteur ni capture privée. Les deux nouveaux fichiers `qualification-final-summary-v1.json` et `signature-rejection-safe-diagnostic-final-v2.json` ont aussi été rescannés et peuvent rejoindre la sélection, ainsi que les deux fichiers de cette revue. Le JSON associé fournit leurs SHA et tailles.

La suppression du lot final des anciens TRX complets Unit/PG redondants réduit le volume tout en conservant leurs manifestes historiques et les preuves causales ciblées. Aucun autre ajout n'est nécessaire pour ce contrôle de sélection. Root peut établir l'allowlist finale, rafraîchir le SHA du rapport modifié, puis effectuer le scan des blobs stagés, le contrôle du diff et le rapprochement des sources avec les règles de fins de ligne. Aucun service, SSH, appel fournisseur, build .NET ou Git n'a été exécuté par ce relecteur.
