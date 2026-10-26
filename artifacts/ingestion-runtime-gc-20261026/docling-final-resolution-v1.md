# Résolution du démarrage Docling et qualification des reprises

Le défaut observé est corrigé sur le périmètre qualifié : le backend de campagne attend désormais des dépendances réellement disponibles, et le client Docling peut reprendre une indisponibilité de transport temporaire sans changer de document ni déclarer un faux succès. Les 26 cas de reprise passent sur Windows et Linux. Les cinq campagnes finales d'ingestion signées en mode Production passent également. Ces résultats ferment ce défaut identifié ; ils ne constituent pas une preuve de perfection de l'ingestion pour tout PDF ni une approbation générale du produit.

Ce document a été établi à partir des preuves locales citées ci-dessous. Son auteur n'a exécuté aucun Git, service, .NET, SSH ou appel API pour le rédiger.

## Défaillance causale observée

La première campagne automatique `defaults` préchargeait un PDF avant le démarrage du backend, afin d'éprouver l'admission d'un document déjà présent. Le bootstrap lançait les conteneurs sans attendre que le service de conversion interne de Docling soit prêt. Le backend a donc pu traiter ce document alors que l'upstream Docling refusait encore les connexions.

La [dérivation publique du défaut de démarrage](public-supplement-final-v4/startup-race-causal-observation.json) conserve trois jobs réellement échoués, avec HTTP 502 et `docling_conversion_http_error`. Ils se sont terminés à **10:20:39.918983**, **10:20:41.458703** et **10:20:43.486921 UTC** le 26 octobre 2026. L'upstream n'a commencé à écouter qu'à **10:20:46.382177702 UTC**. Le refus de connexion est observé et les trois échecs précèdent cette écoute. L'ancien verdict reste `FAIL_PRESERVED`.

Le problème mis en évidence ici est donc un ordre de démarrage et un traitement trop définitif d'une indisponibilité de transport temporaire. Cette preuve ne montre ni un échec du raisonnement d'un LLM ni une mauvaise reconnaissance OCR du PDF : la conversion n'avait pas encore pu joindre son moteur.

## Corrections apportées

### Attendre la disponibilité avant de démarrer le backend

Le [bootstrap de qualification v5](bootstrap-owned-linux-v5.py) vérifie PostgreSQL, Qdrant, TEI et Docling **avant de créer le conteneur backend**. Pour Docling, il exige une réponse HTTP 200 dont le JSON indique `status=ok` et `upstream=true`. Les redirections sont refusées, le proxy est désactivé, l'attente globale est bornée et les identités exactes des dépendances sont enregistrées. Une preuve séparée relie la fin de cette attente au démarrage effectif du backend. Ce bootstrap reste un outil de qualification isolée, pas un nouvel installateur de production.

Le témoin final `defaults` montre concrètement plusieurs réponses Docling 503 pendant le démarrage, puis la disponibilité de toutes les dépendances à **10:45:58.258547 UTC**. Le backend n'a démarré qu'à **10:45:58.418124343 UTC**. Les deux preuves sont [l'attente réelle des dépendances](public-supplement-final-v4/dependency-health/defaults/dependency-readiness-pre-backend.json) et [la chronologie du backend](public-supplement-final-v4/dependency-health/defaults/dependency-readiness-backend-chronology.json). Les quatre autres nouvelles campagnes possèdent les mêmes catégories de preuves dans `public-supplement-final-v4/dependency-health/`.

### Reprendre uniquement les erreurs transitoires reconnues

Dans `DoclingClient.cs` et `DoclingRetryPolicy.cs`, une conversion dispose maintenant d'au plus **cinq tentatives au total**, avec **quatre pauses de 1, 2, 4 et 8 secondes**. Un seul token lié applique l'échéance globale et l'annulation du demandeur à l'attente du slot, aux requêtes, à la lecture des réponses et aux pauses. Il est revérifié après la validation de la réponse et avant de marquer le succès. Le slot est rendu dans le `finally` après succès, échec ou annulation.

La reprise ne concerne que les exceptions réseau sans code HTTP, les réponses **503/504**, et une réponse **502** dont l'objet JSON possède un seul discriminateur `detail`, de type chaîne, exactement égal à `Docling upstream conversion failed.`. Les clés `detail` dupliquées, mauvais types, valeurs approchantes et réponses 502 non classées restent terminaux. Les erreurs d'authentification ou de document, le JSON de succès invalide, un schéma non accepté et les métadonnées d'exécution incohérentes ne sont pas transformés en reprises ni en succès.

Une opération ouvre **un seul handle source**. Chaque tentative recrée requête, multipart et contenu PDF ; la sérialisation repositionne le handle à zéro. La destruction du multipart ne ferme pas le handle détenu par l'opération. Ainsi, un remplacement atomique du chemin n'amène pas la tentative suivante à envoyer un autre document. Le produit conserve `FileShare.Read | FileShare.Delete`, sans autoriser le partage en écriture.

### Rendre les tentatives observables sans exposer le contenu

Les sondes distinguent conversion et tentative, avec compteur de tentatives/reprises, code HTTP, délai, raison contrôlée, durée et verdict. Le logger est effectivement injecté dans le singleton DI. Les sondes n'ajoutent ni corps de réponse, URI, chemin source, message de transport ou exception interne privée. L'erreur finale après épuisement est reconstruite sans exception interne. Un échec permanent ou un épuisement reste un échec ; une annulation reste une annulation.

La [contre-revue indépendante initiale](docling-transient-retry-independent-review-v1.md) avait fait fermer trois points avant l'exécution : logger DI absent, contrôle d'annulation manquant juste avant le succès et témoins incomplets sur le handle, le slot et l'expurgation. Cette revue porte sur le snapshot antérieur à la correction de fixture Windows décrite ci-dessous ; son SHA de fichier de test ne doit pas être utilisé comme SHA final.

## Historique de preuve, y compris les échecs

| Étape | Résultat réellement observé | Preuve |
|---|---|---|
| RED sur l'ancien produit | **8 échecs, 12 succès**, 20 cas ; sources stables | [Manifest RED](tests/docling-transient-retry-red-v1/manifest.json), run `2696658f65ad40a18b2afbd39896f791` |
| Première exécution après correction | **25 succès, 1 échec**, 26 cas ; sources stables ; verdict global échoué | [Premier GREEN non accepté](tests/docling-transient-retry-green-v1/manifest.json), run `c491645cd32f4886b3e563a6c126182a` |
| Témoin de remplacement après correction de fixture | **1/1 succès** ; sources stables | [Témoin corrigé](tests/docling-source-handle-green-v2/manifest.json), run `d1efa9e875324fd8b0a00838dcdadbd8` |
| Suite Windows finale | **5 323 succès, 0 échec, 2 skips**, dont **26/26 nouveaux cas de reprise** | [Suite complète](tests/unit-all-docling-final-v1/manifest.json), run `4e87ad51184f4572966e89a099f9b221` |
| Intégration PostgreSQL finale isolée | **350 succès, 0 échec, 2 skips** ; sources et quatre DLL backend cohérentes avec la suite finale | [Tests PostgreSQL](tests/postgres-all-docling-final-v1/tests/manifest.json), run `bc44cc73f4214458bc9e1e52c86e501a` |
| Reprises Docling sous Linux | **26/26 succès, 0 skip**, conteneur arrêté, sortie 0, aucune OOM, réseau `none` | [Résultat Linux](public-supplement-final-v4/linux-docling-retry-result.json) |
| OCR réel sous Linux | **1/1 succès, 0 skip**, même paquet final et même image OCR, réseau `none` | [OCR réel](public-supplement-final-v4/ocr-real-result.json) |
| Identité de fichiers sensible à la casse sous Linux | **1/1 succès, 0 skip**, même paquet final et même image OCR, réseau `none` | [Système de fichiers Linux](public-supplement-final-v4/linux-case-result.json) |

Le premier « GREEN » est un nom de dossier historique, **pas un résultat accepté**. Son unique échec est une `UnauthorizedAccessException` au `File.Move(..., overwrite:true)` du test de remplacement atomique Windows. Cette primitive de fixture échouait avec le handle source ouvert en partage lecture/suppression. La fixture a été corrigée pour utiliser `File.Replace`, qui exprime le remplacement atomique attendu sur ce système. Aucun ajout de `FileShare.Write` n'a été introduit pour faire passer le test, et les trois fichiers produit du correctif sont restés identiques. Le témoin corrigé a ensuite passé seul, puis les 26 cas ont passé dans la suite complète et sous Linux.

Les deux skips de la suite Windows sont précisément les tests OCR réel opt-in et système de fichiers Linux ; leurs exécutions Linux ci-dessus ferment ces portes spécifiques. Les deux skips PostgreSQL sont des cas historiques `SearchCoreAsync_runtime_ready_v5_cases_match_expected_behavior_and_primary_document` et `SearchCoreAsync_runtime_ready_writer_cases_keep_prudent_guidance_shape` ; ils restent des skips, pas des succès.

La [synthèse centrale Unit/PG](docling-final-central-acceptance-v1.json) confirme les sources stables, la même empreinte source et les quatre DLL backend cohérentes. Le [manifeste du cluster PostgreSQL isolé](tests/postgres-all-docling-final-v1/manifest.json) confirme le cluster existant non modifié, l'environnement restauré, le cluster temporaire et son secret de bootstrap retirés et l'absence de listener résiduel.

## Parcours réels sur le nouveau backend

Après reconstruction du backend final, les cinq nouvelles campagnes ci-dessous ont été exécutées en **Production**, avec signature de configuration vérifiée, dépendances disponibles avant démarrage et conteneurs possédés arrêtés à la fin :

| Campagne | Verdict public | Preuve |
|---|---|---|
| Corpus gold, run `84cf61ff-6fb9-4fd3-a1f6-8b4b559eecff` | PASS | [Ingestion gold](public-export-production-final-v4/production-gold--execution-result.json) |
| Corpus recettes, run `5f40863a-7030-4593-b6b4-efc5bae0dae0` | PASS | [Ingestion recettes](public-export-production-final-v4/production-recipe--execution-result.json) |
| Scanner seul, run `678433d1-fb2a-4c9c-b3a9-04ba93800095` | PASS | [Admission scanner](public-export-production-final-v4/production-auto-scanner--automatic-ingestion-result.json) |
| Watcher seul, run `72dca7da-254d-40de-8f91-84b2c6a94a4e` | PASS | [Admission watcher](public-export-production-final-v4/production-auto-watcher--automatic-ingestion-result.json) |
| Scanner et watcher, PDF préchargé, run `624c1c62-019d-4262-b347-3512bbf4b7ab` | PASS | [Admission defaults](public-export-production-final-v4/production-auto-defaults--automatic-ingestion-result.json) |

Ces JSON sont des projections publiques des preuves opérateur. Leurs champs utiles sont sous `proof` ; l'exporteur ne revendique pas avoir exécuté une seconde validation indépendante. Les preuves de santé, de conteneurs et de binaire complètent ces projections. La [clôture des runtimes possédés](public-supplement-final-v4/runtime-final-closure.json) conserve également la comparaison des 22 conteneurs de référence, indiqués inchangés.

## Ancrages du snapshot final

| Élément | SHA-256 / identité |
|---|---|
| `DoclingClient.cs` | `987432567179b8a9d16eb55c9cc622137d481251599da93f060748749f142dee` |
| `DoclingRetryPolicy.cs` | `948260eca9a832d1e8cf7dd6b393ae0735c6ce9a338ac4438688732b16e061ec` |
| `ServiceCollectionExtensions.cs` | `da48f05abd161bfcb4978020b7c8549a9b9a8448e532fc9f97e46fcf5d0691a1` |
| `DoclingClientTests.TransientRetry.cs`, fixture finale | `b31a58b5ace870429b82d0063889fb4e9c876426053eb12567b4d3f2321c01d6` |
| Backend AnyCPU embarqué dans le paquet de tests | `36d369a7ae567b73698324de1e76c078267edf386ecfd16f45a23c76513cf5a0` |
| DLL de tests finale | `583c9042fb813784d405311e0dacdd68ec04637c9c4f4e8ce7a44b057299523f` |
| Inventaire du paquet de tests Linux | `60d5f834a3526839991b1ec7c024cc1815541f993a8b895a0098835bd5482b72` |
| Backend compilé dans l'image applicative Linux | `f6825b8c36bc26a5da8308eb4d4a5ec87c62d6962f57f644c725e8e606eb18c4` |
| Image applicative Linux finale | `sha256:d272895b502e7cd902afa7c197eac34b1227ffacfde80204c02f4b444b3587cd` |
| Image OCR/test avec SDK, distincte de l'image applicative | `sha256:306dea810850f2aea6d0c11e10fdf65f00f5bc9ecf5f960dd8d18cb1c4700d81` |

Le [précontrôle du binaire Linux](public-supplement-final-v4/backend-binary-preflight.json) relie son image au DLL Linux et à `source-sha256:dca75ae2906275ab1120aa316aab0c4d5d492fa74a5853dcd5eeb56bb3111a5a`. Une empreinte source, un DLL AnyCPU, un DLL Linux et un identifiant d'image sont des identités différentes. Le test Linux conserve les octets exacts du paquet et une trace de résolution par le host .NET ; ce n'est pas une attestation de modules chargés produite par un profiler.

## Limites conservées

- Les 26 cas Linux utilisent des handlers HTTP en mémoire et un conteneur sans réseau. Ils éprouvent la stratégie, les délais, l'annulation, les réponses, le replay, le handle et les sondes. **Aucune injection volontaire de panne TCP/DNS ou de coupure du vrai service Docling en cours de conversion n'a été exécutée.** Le défaut historique contient un vrai refus de connexion ; les nouvelles campagnes réelles passent avec l'attente de santé. Elles ne séparent donc pas causalement l'effet de cette attente et celui des reprises sur une nouvelle panne réelle.
- Le handle unique protège l'identité du document lors du **remplacement atomique du chemin**. Il ne crée pas une copie immuable et ne garantit pas l'absence de modification en place du même inode pendant la lecture.
- La désérialisation JSON synchrone est annulable de manière coopérative à ses frontières : le correctif empêche le succès après annulation observée à son retour, mais n'interrompt pas l'analyse JSON au milieu d'une instruction CPU.
- Une indisponibilité plus longue que le budget de reprise ou l'échéance globale produit encore un échec explicite. Une requête dont la réponse réseau a été perdue peut avoir déjà déclenché un calcul côté service : aucune garantie d'exécution de conversion exactement une fois n'est revendiquée.
- Un test OCR raster et deux corpus qualifiés ne démontrent pas une fidélité universelle sur tous les scans, tableaux, langues, mises en page ou PDF malformés. Le planning de repas et les appels à un LLM payant ne sont pas couverts par cette résolution. Le statut produit reste **`TESTE_NON_APPROUVE`** jusqu'aux portes d'acceptation correspondantes.
