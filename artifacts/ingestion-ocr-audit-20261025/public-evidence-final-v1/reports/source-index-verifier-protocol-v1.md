# Vérificateur des sources testées et des blobs de l’index

`verify-tested-source-index.py` est un outil de vérification, pas un outil de staging. Il ne lance que `rev-parse`, `ls-files --stage -z` et `cat-file --batch-check/--batch`, avec sorties binaires. Aucun add, checkout, reset, commit, hook, filtre de contenu ou commande réseau n’est exécuté par le vérificateur. Les variables Git héritées pouvant rediriger l’index ou le dépôt sont neutralisées ; les verrous optionnels et fsmonitor sont désactivés. Le lazy fetch et tous les transports Git sont interdits dans l’environnement enfant afin qu’un objet manquant d’un partial-clone ne déclenche pas un téléchargement implicite.

## Contrat et verdict

Entrée obligatoire : l’inventaire final `source-files-…-after.json`, tableau exact d’objets `path`/`sha256`, et le fingerprint `sourceAfter.sha256` de son manifeste. Le fingerprint compact est vérifié avant comparaison. Les chemins doivent être relatifs au dépôt explicite et sans traversée. La sortie contient hashes, catégories et résultats, jamais le contenu des sources ou le stderr brut de Git.

Pour chaque fichier, le rapport compare :

- octets actuels ↔ SHA du run ;
- blob binaire de l’index ↔ SHA du run ;
- octets actuels ↔ blob de l’index ;
- éventuel SHA déclaré séparément pour une configuration/preuve.

Les statuts distinguent `EXACT`, `EOL_ONLY_PROVEN`, différence non expliquée par une conversion uniforme LF/CRLF, divergence réelle d’octets entre deux contenus disponibles, fichier absent de l’index, index non fusionné et fichier courant manquant. `testedFileChangedAfterRun` concerne tous les membres de l’inventaire ; `testCodeChangedAfterRun` et son compteur isolent les sources de tests modifiées.

`strictPassed=true` exige **l’égalité exacte**, des inputs stables et un index stable pendant les observations. Une seule conversion EOL ne passe jamais ce verdict : des littéraux multilignes ou générateurs peuvent dépendre des octets physiques. Les versions avant/après de l’index et les fichiers courants sont réobservés pour détecter une mutation pendant le contrôle.

Un inventaire de hashes ne contient pas les octets historiques : une différence dite EOL est prouvée seulement si une conversion candidate reproduit exactement le SHA du run, ou si l’autre contenu disponible est lui-même exactement celui du run. Sans cette preuve, une ancienne combinaison arbitraire de fins de ligne mixtes ne peut pas être reconstruite ; le rapport ne prétend pas que toute différence de SHA est une différence sémantique.

## Configurations et preuves déclarées

`--declared` est répétable et accepte soit une liste JSON, soit un objet avec un tableau `files`. Chaque entrée contient `path`, `sha256` et éventuellement `role` parmi `source`, `test_source`, `configuration`, `evidence`, `evidence_tool`. Une déclaration ne remplace jamais le SHA testé. Un conflit de baseline fait échouer le verdict.

Le format accepte notamment la liste `files` d’une future sélection publique exportée. Une configuration/preuve présente seulement dans la déclaration porte `DECLARED_ONLY_NOT_CLAIMED_TESTED` : son égalité d’octets avec l’index ne devient pas une affirmation d’exécution de tests. Les fichiers non énumérés ne sont pas évalués.

## Commande de clôture à exécuter par le parent

Après le gel final et le staging déjà autorisé, remplacer les trois entrées entre chevrons par leurs valeurs réelles :

```powershell
python artifacts/ingestion-ocr-audit-20261025/verify-tested-source-index.py --repo "C:\Users\maxim\Desktop\ecom\AAA\SAAIA - RAG" --inventory "<chemin source-files-...-after.json>" --expected-source-fingerprint "<sourceAfter.sha256 du manifeste final>" --declared "<liste configs et preuves déclarées.json>" --output "artifacts/ingestion-ocr-audit-20261025/source-index-anchor-final-v1.json"
```

Omettre `--declared` pour contrôler seulement les sources inventoriées. Le rapport de sortie doit être neuf ; le mode exclusif refuse tout écrasement. Code de sortie 0 : égalité stricte ; 1 : divergences classées dans le rapport ; un input invalide ou une commande Git échouée ne produit pas de faux PASS.

Le rapport est une observation ponctuelle sous gel : tout changement ultérieur des sources ou du staging exige de recontrôler les chemins concernés. Il n’affirme pas que tout l’index, les fichiers privés ou des fichiers non déclarés ont été revus.

## Validation réellement exécutée

`tests/source-index-anchor-v1/green-v3.log` : **17 tests Python PASS, zéro skip**, uniquement sur des dépôts Git temporaires neufs dans cette racine d’artéfacts. Les tests créent et alimentent leurs propres index synthétiques ; aucun index du dépôt réel n’a été lu ou modifié et aucun commit n’a été créé.

Couverture : familles C#/Python/PowerShell/SQL, chemins Unicode, blobs non UTF-8, transport binaire des CRLF, EOL dans le working tree et dans l’index, valeur signée réellement changée, fichier non staged, source de test modifiée, configuration/preuve déclarée, conflit de SHA, fingerprint incorrect, traversée, fichier courant supprimé, index non fusionné, variables Git alternatives ignorées, EOL mixtes avec témoin exact disponible, mutation concurrente de l’index et mutation concurrente d’une source après sa première lecture. Une comparaison binaire avant/après confirme que le vérificateur laisse l’index synthétique inchangé.

L’ancrage `tests/source-index-anchor-v1/proof-v2.json` conserve les SHA du vérificateur, des tests et du journal. Il ne constitue pas une vérification du vrai staging, qui reste à exécuter par le parent.
