# Générations abandonnées : résolution et preuves vérifiées

Le correctif des trois consommateurs est validé sur PostgreSQL isolé : sept
tests réussis, aucun échec ni skip. Les sources restent gelées pour les suites
complètes et les validations worker suivantes. Aucun client WinUI n'a été
modifié dans ce lot.

## Défaut démontré avant correction

Une génération allouée 5 peut être annulée alors que la publication valide
reste à 3. L'ancien contrôle du seul écart `ingestion_version > indexed_version`
créait un candidat de réindexation et un faux backlog, puis le script qualité
qualifiait cette situation d'erreur sans job actif. Un échec de tentative ne
portait pas non plus de diagnostic distinct.

La capture RED v2, après correction de la fixture, a exécuté cinq tests :
trois contrôles réussis et deux échecs causaux. Le run est
`fbb1fada79f84a2396af166cef978b64`, sous
`tests/monotonic-consumers-red-v2/tests/manifest.json`. Les deux échecs sont
`Abandoned_generation_is_not_pending_backlog_or_automatic_reindex` et
`Failed_generation_has_an_explicit_failure_diagnostic_instead_of_implicit_pending`.
La capture v1 interrompue sur une contrainte SQL de fixture ne constitue pas un
RED produit et reste historique.

## Comportement corrigé

Les deux lecteurs de capacité A partagent leurs observations SQL et leur
classification : job de la génération courante, vrais jobs actifs,
publication courante et différences de source effectivement observées.

| Situation | Travail et diagnostic |
| --- | --- |
| Génération annulée, aucun job actif | Aucun candidat au seul motif de cet écart ; raisons `ingestion_generation_abandoned` et `source_freshness_unverified` accessibles dans les diagnostics existants. |
| Job queued/running | Signal conservé, candidat bloqué par le vrai job actif. |
| Tentative échouée | Raison distincte `ingestion_attempt_failed`. |
| Aucune tentative correspondante connue | Raison explicite `ingestion_generation_unresolved`. |
| Source observée différente de la publication | Avertissement `indexed_version_outdated` conservé, même après annulation. |
| Échec sans publication valide | Problème toujours visible : `never_indexed`, échec et blocage de politique. |

Les avertissements sont conservés dans les `ReasonCounts` de la surface de
diagnostic, même lorsqu'une génération annulée ne doit plus être comptée comme
candidat d'enqueue. Le script qualité distingue les générations abandonnées
non revérifiées et les tentatives échouées des sources observées différentes
et générations inexpliquées. Les deux premières sont des avertissements
explicites ; les deux dernières restent des erreurs. La politique indépendante
des échecs récents et des vrais jobs actifs demeure en place.

## Vérification du GREEN

Le run `a0b1142a7a854625b76ac18ed5ab8ff8`, sous
`tests/monotonic-consumers-green-v1/tests/manifest.json`, contient sept tests
réussis, zéro échec, zéro skip. Aux cinq états initiaux s'ajoutent une
génération inconnue sans job et un échec sans publication valide.

La relecture indépendante a contrôlé :

- Les résultats réels des TRX, égaux aux compteurs des manifestes.
- Les SHA256 des TRX, égaux aux hashes enregistrés.
- Les inventaires avant/après et leurs empreintes composites : identiques dans
  chaque run, `sourceChangedDuringRun=false` sur RED et GREEN.
- Le contrôle PowerShell `quality-script-parse-final-v1.json` : zéro erreur et
  hash correspondant au script actuel.

Le résultat durable de cette relecture est
`monotonic-generation-consumer-proof-review-v1.json` : zéro anomalie de hash,
de compteur ou d'inventaire. Le test de qualité exécute le SELECT du vrai
script, et la fixture valide ses quatre tables de texte, checksums UTF8, pages
et offsets avant les assertions métier.

## Limite de fraîcheur

Ces diagnostics n'effectuent aucune lecture de fichier documentaire. Enqueue
met à jour taille/date mais ne remplace pas forcément le hash de contenu
publié. L'égalité du hash du document avec celui de la révision ne prouve donc
pas l'état du fichier actuel. Le hash de checkpoint décrit seulement une
observation de la tentative. Une annulation sans observation suffisante reste
une génération abandonnée dont la source présente n'a pas été revérifiée.

Le lot ne prouve pas une nouvelle exécution native WinUI ni une ingestion
complète du corpus. Les suites complètes et le cycle de vie réel restent des
preuves séparées. Le statut produit demeure `TESTE_NON_APPROUVE`.

La relecture finale n'a effectué aucun build, test .NET, SQL, HTTP, service,
lecture de credentials ou opération Git ; elle a seulement relu les preuves
produites par l'orchestrateur et écrit ce rapport et sa projection JSON.
