# Validation locale de l'ingestion

Ces outils gardent distincts le vrai worker, les moteurs d'extraction, le
stockage vectoriel et la qualité de recherche. Ils ne contactent aucun LLM
payant et ne configurent aucun client utilisateur.

`start-local-dependencies.ps1` / `stop-local-dependencies.ps1` appartiennent à
la gestion séparée de Docling, Qdrant et éventuellement Transformers CPU. Ils
conservent leur propre manifeste d'ownership. Le runner du worker ne les arrête
pas. Leur démarrage ne prouve ni readiness ni conversion ; l'opérateur doit
vérifier le runtime puis marquer le manifeste de dépendances `ready`.

## Parcours du worker

Le runner `invoke-owned-ingestion.ps1` ne fait rien de distant par défaut : sans
`-Execute`, il valide les sources/configurations locales et affiche son plan.
Il ne lit alors aucun credential, ne lance aucun processus et n'effectue aucune
requête HTTP ou SQL. Les PDF synthétiques sont générés par
`tools.ingestion_gold.generate` avec le manifeste épinglé courant. Le schéma
`ingestion_real_acceptance_v1` accepte aussi un vrai PDF existant : SHA256 de la
source, nombre de pages physiques, faits exacts indépendants et
`sourceAnnotations.kind=source_pdf_review`, `sourceSha256`, `basis`. Il ne
demande ni générateur ni pins de rendu inventés. Les annotations physiques sont
revues sur le PDF source avant d'observer la sortie du worker.

```powershell
& tools/validation-ingestion/invoke-owned-ingestion.ps1 `
  -OutputDirectory artifacts/ingestion-ocr-audit-20261025/worker-run-neuf `
  -SourceDirectory artifacts/ingestion-ocr-audit-20261025/gold-corpus-v2-final/sources `
  -DoclingProfilePath CHEMIN_PROFIL_OBSERVE.json `
  -DependenciesManifestPath CHEMIN_DEPENDANCES_ACTIVES/dependencies.json `
  -PythonExecutable CHEMIN_ABSOLU_PYTHON
```

Ajouter `-Execute` seulement quand le backend est compilé et les dépendances
réellement prêtes. Aucun build ou téléchargement implicite n'est effectué. Le
profil Docling est un objet `DocumentIntelligence` ou un objet l'enveloppant ;
ses versions/déploiement doivent décrire les packages réellement observés.
Le sous-ensemble par défaut est le scan français et le document mixte français.
`-CaseId` permet une sélection explicite différente. Un cas inconnu ou un PDF de
hash différent est refusé avant lancement.

Pour un PDF original, fournir `-CorpusManifestPath`, `-SourceDirectory` et
`-CaseId` explicitement. Le contrôle réel préparé pour cette campagne utilise
`real-source-preparation-v1/manifest.private.json`, son dossier `sources` et le
cas `recipe-real-v1` (36 pages, 14 faits revus indépendamment). Ces fichiers
privés et leur contenu ne deviennent pas des preuves publiques par défaut.

Le parcours démarre un nouveau cluster PostgreSQL et un backend compilé via le
bootstrap WinUI réutilisé avec un overlay strict. Aucun défaut du bootstrap
WinUI n'a changé. L'overlay n'admet que trois sections, des URLs loopback
littérales et les paramètres nécessaires au worker canonique. Auth, base,
provider LLM, secret refs et commandes OCR externes ne peuvent pas être injectés.
Watcher, scanner et ancien OCR sont désactivés ; un seul worker s'exécute.

Après preuve de tables vides, les PDF sont copiés puis soumis par une seule
requête `/ingestion/scan`. Aucun seed de document, page, unité ou chunk n'est
possible dans ce runner. Les jobs réels doivent finir `done`. La capture SQL
READ ONLY conserve bundle gzip, chunks et ancres privés. Le vérificateur Python
contrôle les hashes, identités, faits critiques/pages et contexte API. Il relit
chaque point de la collection Qdrant neuve. Depuis la migration 071, l'ID du
point est le Guid .NET des 16 premiers octets de
`SHA256(UTF8("qdrant|" + chunkGuidN + "|" + planFingerprint))`, avec les règles
little endian du constructeur Guid. Son payload `chunk_id` reste l'identité
SQL canonique ; le fingerprint complet doit être celui du document publié et
du job terminé. Un ID legacy, un point d'un ancien plan, une collection vide ou
un stub renvoyant `[]` ne passe pas.

`-VerifyCanonicalSearch` ajoute pour chaque cas une vraie requête POST
`/rag/search` à partir du premier fait critique annoté, bornée au document et à
sa page physique, avec `sourceBackedCanonical=true`. Le nonce absent doit donner
zéro correspondance exacte ou sparse ; les voisins denses éventuels doivent
rester des chunks SQL courants, de texte et d'identité exacts dans ce périmètre.
Le document UUID inexistant exige zéro résultat total. Un hit portant
le fait est ensuite relu par `/documents/context?chunkId=...`. Les textes et
identités des hits sont confrontés aux chunks SQL. Le résumé public ne publie
ni requêtes ni corpus. Ce contrôle mesure une recherche canonique littérale ;
il ne suffit pas à qualifier le rappel sémantique dense ou toutes les questions
possibles.

`-ValidateLifecycle` est réservé au cas synthétique jetable
`fr-scan-critical-v2` du corpus Gold v2. Le helper de cycle de vie est appelé
après la preuve de publication initiale et avant le nettoyage final. Ce switch
ne doit pas être utilisé pour le PDF original.

En `finally`, seuls les jobs du backend/cluster possédés sont annulés au besoin,
puis le stop possédé existant arrête les helpers et PG. PID/date/exécutable,
parent dotnet et loopback sont revérifiés avant les opérations de contrôle.
Le résultat échoue si zéro job actif ou l'arrêt ne peuvent pas être prouvés.
DB et preuves sont conservées. Les dépendances partagées de cette campagne
restent sous le contrôle de l'opérateur ; une conversion locale encore en vol
après annulation nécessite son observation séparée.

## Embeddings réels ou substitués

Sans `-EmbeddingProfilePath`, l'adaptateur WinUI fournit des vecteurs 3D
déterministes. Le worker et le stockage Qdrant sont réels ; l'inférence
d'embeddings et la recherche dense ne sont pas validées.

Le second mode utilise un modèle réel local via un adaptateur Transformers CPU
compatible avec le protocole d'embeddings. Le profil attendu est :

```json
{
  "schemaVersion": "saaia-local-embedding-profile.v1",
  "adapter": "transformers_cpu",
  "modelFileSha256": "SHA256_HEXADECIMAL_MINUSCULE_64_CARACTERES",
  "Rag": {
    "EmbeddingsBaseUrl": "http://127.0.0.1:15103",
    "EmbeddingsModel": "intfloat/multilingual-e5-base",
    "EmbeddingsModelRevision": "COMMIT_IMMUABLE_40_OU_64_CARACTERES_HEXADECIMAUX",
    "EmbeddingsRuntimeRevision": "IDENTITE_RUNTIME_OBSERVEE"
  }
}
```

Avec `-Execute`, `/info` doit confirmer exactement `adapter`, `model_id`,
`model_revision`, `model_file_sha256` et `runtime_revision` avant tout job. Le
rapport distingue `embeddingsReal=true`, l'adaptateur Transformers et le binaire
TEI. Même avec ce modèle réel, `vectorValidated=false` reste correct tant que la
qualité sémantique de recherche n'a pas été mesurée. La relecture des points
est exposée séparément par `vectorStorePublicationValidated`.

La sonde indépendante `probe-local-vectors.py` exige le profil d'embeddings
attendu et vérifie les cinq champs de `/info`, la forme des vecteurs, les
erreurs de contrat ainsi que trois permutations de classement. Elle refuse
les vecteurs constants et les ex aequo ; la marge minimale est 0,001 en cosinus
et dans Qdrant. Sa collection neuve est supprimée puis son absence vérifiée.
Cette petite mesure synthétique ne remplace pas l'acceptation documentaire.

Avant tout démarrage E5, le bootstrap des dépendances contrôle aussi le SHA256
réel du fichier de poids épinglé. Le stop valide toutes les identités et ports
avant mutation, arrête les enfants de calcul exacts, attend la sortie des
wrappers pour laisser leurs logs se vider, puis vérifie processus et ports.
Sans `-Execute`, le stop ne fait qu'établir son plan, y compris pour les anciens
manifestes de cette campagne à quatre rôles.

## Tests sans moteur

```powershell
& tools/validation-ingestion/Test-OwnedIngestionGuards.ps1 `
  -OutputDirectory artifacts/mon-nouveau-test-offline
python -B -m unittest discover -s tools/ingestion_gold/tests -t . -v
```

Les données du vérificateur Python sont explicitement des captures synthétiques
de test : leur réussite ne prétend pas avoir exécuté PostgreSQL, OCR ou Qdrant.
Les preuves privées du runner ne sont pas destinées à un staging automatique.
Seuls les résumés publics sans corpus/credentials doivent être sélectionnés
après revue. Le statut produit demeure `TESTE_NON_APPROUVE` jusqu'aux preuves
d'acceptation réelles.
