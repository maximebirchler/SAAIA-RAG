# Collecte sûre des anciennes générations vectorielles — conception v1

État : **CONCEPTION ET TESTS PRÉPARÉS, NON EXÉCUTÉS**. Aucun produit, service, stockage réel, réseau ou Git modifié dans ce lot. Les premiers tests PostgreSQL doivent être capturés par le coordinateur avant le correctif. Les preuves d’ingestion antérieures ne sont pas modifiées et ne qualifient pas ce futur collecteur.

## Décision retenue

Commencer par les générations strictement dépassées, avec quatre garanties durables : un compteur de génération indépendant de la ligne du document ; un registre exact de chaque plan écrit avant ses vecteurs ; un retrait irréversible sous transaction ; une suppression bornée des seuls IDs du registre dans leur cible historique approuvée.

Une génération actuelle, même échouée ou annulée, reste protégée. Une ancienne génération encore publiée reste protégée tant que sa publication n’est pas remplacée ou supprimée. Les plans de même génération, les vecteurs legacy et les anciennes cibles inconnues ne sont pas rendus supprimables par une heuristique de délai.

Cette décision a été rapprochée de la [contre-conception indépendante](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/artifacts/ingestion-runtime-gc-20261026/gc-independent-safety-review.md>). Elle conserve le filtrage SQL des résultats denses et l’élargissement borné existants : le nettoyage physique ne remplace aucune de ces protections de lecture.

## Défauts et contraintes actuellement vérifiés dans le code

- `IngestionEnqueue.EnqueueUpsertAsync` et `EnqueueDeleteAsync` démarrent à 1 quand la ligne `documents` n’existe plus. Le `doc_id` dépend de tenant/chemin. Le delete métier garde déjà une génération monotone, mais une suppression physique du document ou du tenant efface cette preuve. Le même document recréé peut donc réutiliser version, chunks, plan et IDs Qdrant. Les huit RED préparés visent exactement cette limite ; ils ne prétendent pas reproduire un collecteur qui n’existe pas encore.
- `IngestionVectorPlan.Fingerprint` comprend identité/version, cible, configuration, code et entrées. `PointId` sépare le chunk canonique de l’ID physique. Cette séparation doit rester inchangée pour les citations.
- Le worker calcule tous les IDs avant les upserts. Il ne persiste actuellement ni la cible historique ni le manifeste de points par plan. Un checkpoint seul ne permet donc pas une collecte complète des plans abandonnés.
- `CompleteUpsertAsync` et `CompleteDeleteAsync` contrôlent bail et génération dans leur transaction de publication. C’est le bon point pour retirer les anciens plans et rendre leur collecte durable.
- Une erreur transitoire, une reprise administrée de job `paused` et une reprise après bail périmé gardent la génération. Aucun chemin standard de reprise d’un job `done` n’a été identifié. La protection des générations reprenables n’est pas fondée sur une supposée résurrection de tous les terminaux.
- Le worker n’effectue plus de suppression Qdrant large. Le nouveau collecteur ne doit pas réintroduire de delete par document, version, filtre de catégorie ou « tout sauf l’actuel ».

## Barrière de génération

### Registre durable

Une table de maximum historique, clé `(tenant_id, doc_id)`, conserve la dernière génération réservée. Elle ne référence ni `tenants` ni `documents` avec cascade. Son contenu minimal est constitué d’IDs, du maximum entier et des dates ; aucun chemin ou texte documentaire n’est nécessaire.

Les admissions upsert et delete réservent atomiquement une génération strictement supérieure au maximum connu, après rapprochement des versions document/index/revisions/jobs déjà présentes. Une réservation échouée peut laisser un trou ; elle ne peut jamais permettre la réutilisation d’une valeur. Le dépassement de capacité du type entier est un refus explicite, pas un retour à zéro.

Le même tenant/document recréé physiquement reçoit donc une nouvelle génération. Celle-ci change déjà le chunk canonique et le plan ; il n’est pas nécessaire d’ajouter un UUID d’incarnation à tous les vecteurs. Une reprise du même job garde sa génération existante si elle est toujours courante et non retirée.

### Couverture des entrées

Les deux admissions `EnqueueUpsertAsync`/`EnqueueDeleteAsync` doivent utiliser la même primitive de réservation. `MarkMissingAsync` crée un placeholder sans plan à version 0 : il doit préserver un éventuel maximum historique et ne peut pas devenir une voie d’admission version 1. Les restaurations de statut et stabilisations existantes restent monotones. Les imports/réindexations qui publient une version doivent aussi passer par l’admission, ou avancer le maximum sous la même garantie avant publication.

Le backfill initial utilise les maxima sûrs des documents, révisions et runs, ainsi que les payloads de jobs dont docId/version sont syntaxiquement valides et bornés. Les données illisibles n’autorisent aucune collecte legacy. Ce backfill ne reconstitue pas une histoire déjà perdue : il n’est pas présenté comme une preuve de versions antérieures absentes.

La réservation et l’upsert documentaire doivent être une décision SQL atomique ou une transaction courte avec ordre de verrous explicite. Ne pas ajouter naïvement une transaction longue `document → job` face aux publications `job → document`. Un CTE de réservation/document peut conserver les transactions courtes actuelles ; les trous de réservation sont acceptés. Le collecteur n’utilise jamais le seul maximum réservé pour retirer un plan pendant que `documents.ingestion_version` n’a pas effectivement avancé.

## Registre exact de stockage

Tables cohérentes proposées, dans une migration neuve :

| Ensemble | Données durables | Invariant |
|---|---|---|
| Maximums de génération | tenant/document, maximum, dates | Jamais diminué ni supprimé par les cascades métier |
| Cibles gérées | target_id, endpoint canonique, collection, identité/révision de namespace, référence de credentials approuvée | Cible immuable ; aucune clé en clair |
| Plans | tenant/document/version/plan, target_id, nombre/hash du manifeste, dates d’enregistrement/retrait et motif | Retrait irréversible ; pas de cascade métier |
| Points du plan | plan_id, ordinal, chunk canonique, ID Qdrant physique | Ensemble immuable et vérifiable ; pas de texte, vecteur ou embedding |

Le job qui écrit enregistre le manifeste complet **avant le premier upsert**, après vérification de son bail, de sa génération courante et de l’absence de retrait. Une répétition strictement identique est idempotente. Même clé avec cible, nombre, ordre ou IDs différents : refus explicite ; ne pas remplacer silencieusement le manifeste. Une transaction interrompue ne rend pas un manifeste partiel utilisable.

Le checkpoint de reprise ne remplace pas ce registre. Une reprise vérifie à nouveau le plan et son état avant de considérer les points existants, puis avant chaque nouveau lot d’écriture. Une requête Qdrant déjà partie peut terminer malgré l’annulation ; la section « écritures tardives » couvre cette limite réelle.

## Retrait sous transaction

L’éligibilité conservatrice est :

1. le manifeste enregistré est complet et son identité exacte est connue ;
2. `plan.version < documents.ingestion_version` ;
3. le plan n’est pas la publication SQL active ;
4. la cible est approuvée pour cette opération.

Une publication active legacy avec plan nul protège toute sa `indexed_version`. Un document physiquement absent n’est pas, à lui seul, une preuve de retrait : report explicite jusqu’à une nouvelle admission ou une suppression métier prouvée. Une génération simplement réservée sans avancement documentaire ne rend pas les anciens plans supprimables.

La publication B réussie retire l’ancien plan A strictement plus ancien, dans la même transaction que le changement du plan actif. La suppression métier fait de même avec la publication qu’elle retire. Un rollback ne doit laisser ni retrait ni travail de GC exécutable. Une réconciliation bornée couvre ensuite les autres plans enregistrés jamais publiés dont la génération est devenue strictement ancienne.

Une substitution de plan à l’intérieur de la génération courante reste reportée. Les échecs/annulations/reprises ne suffisent pas à retirer cette génération dans cette première livraison. À l’admission d’un plan, à la reprise et à la publication, un tombstone de retrait interdit toute réactivation. La monotonie indépendante du document empêche qu’une recréation future retrouve les mêmes IDs.

Ordre de verrous attendu : bail du job, document, plans concernés triés de façon stable ; les lectures du maximum historique ne doivent pas introduire l’ordre inverse d’un autre chemin d’admission. Le collecteur ne tient jamais un verrou de document/job pendant un appel HTTP. Il possède son propre bail opaque et ne valide aucun checkpoint après perte de ce bail.

## Collecteur borné et reprenable

Le collecteur réclame un petit nombre de plans retirés, lit un lot ordonné d’IDs et libère la transaction avant le réseau. Chaque appel vise le target_id historique, jamais les options Qdrant courantes par substitution. Les credentials sont résolus pour cette cible seulement ; il est interdit de réutiliser aveuglément le client nommé qui porte la clé de la configuration courante avec une ancienne BaseAddress.

Pour chaque lot :

1. Relire les seuls IDs attendus dans la cible approuvée, avec payload minimal sans vecteur.
2. Valider tenant, document, version, plan et chunk canonique. Un ID manquant est une absence observée ; un ID incohérent est mis en quarantaine et n’est pas supprimé.
3. Demander la suppression des seuls IDs validés, avec attente de fin du runtime qualifié. Aucun filtre de suppression documentaire/version n’est construit.
4. Relire ces IDs et constater l’absence. Un timeout, un ACK non terminal ou une lecture incomplète n’est pas un succès d’absence.
5. Enregistrer le checkpoint avec le bail du collecteur. Un crash après l’HTTP et avant SQL reprend idempotemment le même lot.

Les bornes comprennent plans par tour, IDs par lot, appels par tour, taille de réponse et durée totale ; annulation et temporisation sont propagées et observées. Un endpoint défaillant ne monopolise pas tous les tours : backoff borné et sélection des autres tickets dus. Les 404 n’entraînent jamais `EnsureCollectionAsync` ou création de stockage par le GC.

Un plan reste retiré après un premier tour complet. La réussite est `absence observée à telle date`, avec prochain recheck. Les listes d’IDs/tombstones ne sont pas effacées après un ACK. Si un ancien upsert apparaît plus tard, le plan reste inactif au filtrage SQL et les mêmes IDs redeviennent collectables lors d’un prochain balayage. Il n’y a pas d’état `done` définitif qui rende la réapparition invisible.

## Cibles changées, legacy et protocole de restauration

L’autorisation porte sur des cibles gérées dont le rattachement SQL/Qdrant est connu. Le résolveur utilise un descripteur historique immuable et une liste de cibles approuvées. Sans ancienne référence de credentials ou sans rattachement de namespace vérifiable : `blocked_target_unavailable`/`blocked_target_unattested`, aucune destination de repli. URLs avec userinfo, query ou fragment sont refusées ; pas de redirection HTTP vers une nouvelle autorité. Les journaux ordinaires n’exposent ni URL ni référence de secret.

Le remplacement d’un serveur, la restauration d’une base ancienne, le clonage d’une base vers un namespace partagé et la recréation d’une collection homonyme sont un **protocole distinct** : GC suspendu, identité de namespace rapprochée/renouvelée, jamais reset du maximum avec réemploi aveugle des IDs. Une URL/collection égale n’est pas une preuve universelle d’identité de stockage. La première qualification runtime doit employer un namespace isolé possédé ; elle ne prouve pas ce protocole de restauration générale.

Les anciennes données sans registre exact restent en quarantaine avec compteurs. Une découverte future peut lire des points dans une cible approuvée puis vérifier tous leurs IDs et payloads ; elle ne peut jamais transformer son filtre de découverte en delete large. Le bilan devra distinguer plans gérés collectés, génération courante reportée, publication active protégée, legacy inconnu et cibles bloquées. « Rien de supprimable prouvé » n’est pas « tous les anciens vecteurs ont disparu ».

## Décomposition de code proposée

- `IngestionGenerationRegistry` : réservation/backfill/monotonie, SQL court partagé par les admissions.
- `IngestionVectorRegistry` : cible et manifeste immuables, admission/reprise, retrait dans la transaction de publication.
- `VectorGarbageCollector` : ordonnancement/bail/checkpoints et bornes ; aucun texte de document.
- `VectorGcTargetResolver` et transport Qdrant exact : résolution des cibles approuvées, lecture/suppression par IDs, annulation/ACK/absence.
- `JobRepo.Completion` : appels transactionnels brefs au registre ; pas de transport HTTP ajouté au monolithe du worker.
- Worker d’ingestion : enregistrement avant upsert et contrôle de reprise ; les citations gardent les chunks canoniques existants.

Options séparées avec activation explicite, limites et cibles approuvées ; aucune activation silencieuse d’un delete historique en migration. Les migrations créent les barrières et effectuent le backfill sans réseau. Le démarrage observe l’état de la barrière/du registre, sans annoncer de purge avant exécution.

## RED prêts et couverture à compléter

Fichier préparé : [DocumentFoundationIntegrationTests.VectorGcLifecycle.cs](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend.Tests/DocumentFoundationIntegrationTests.VectorGcLifecycle.cs>).

Filtre : `FullyQualifiedName~Vector_gc_`. Dix cas préparés, **aucun exécuté par cet agent** : huit variantes attendues RED sur les APIs actuelles (première admission upsert/delete, recréation upsert/delete, suppression physique du document/tenant) ; deux témoins attendus PASS (retry courant et isolation). La suppression SQL n’atteint que les fixtures neuves isolées créées dans chaque test. Aucune fixture historique n’est ouverte.

La [matrice v1](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/artifacts/ingestion-runtime-gc-20261026/vector-gc-red-matrix-v1.json>) distingue ces causalités exécutables des futurs tests du collecteur. Les contrats futurs ne sont pas remplacés par des tests artificiels qui échoueraient seulement parce qu’une classe est absente. Après validation de cette conception, préparer leurs APIs minimales et tests comportementaux avant l’implémentation des effets destructifs, puis faire capturer les RED centralement.

## Sondes et bilan attendu

Événements : `vector_registry.registered`, `vector_registry.retired`, `vector_gc.claimed`, `vector_gc.deferred`, `vector_gc.delete_acknowledged`, `vector_gc.absence_observed`, `vector_gc.reappearance_observed`, `vector_gc.lease_lost`.

Seulement IDs internes/empreintes, codes d’état, durées et compteurs bornés. Aucun texte PDF, embedding, TSV, chemin de fichier, URL ou clé dans les journaux usuels. Un compteur d’IDs acquittés ne s’appelle pas « points supprimés prouvés ». Le bilan expose les dates d’absence observée, résidus, rechecks dus, cibles bloquées et limites atteintes.

La qualification finale nécessitera PostgreSQL réel puis Qdrant réel isolé : ancienne publication protégée pendant échec de la suivante, changement de génération/cible, reprise après timeout/perte de bail, ancien upsert réapparaissant après un premier balayage, nouvelle incarnation pendant un delete retardé. Aucune de ces preuves futures n’est acquise par la seule conception ou par les anciennes campagnes d’ingestion.
