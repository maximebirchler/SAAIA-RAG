# Générations et GC vectoriel — résolution du lot

État à la rédaction : **sources gelées, GREEN central et qualification runtime en attente**. Cette note décrit ce qui est implémenté et les RED réellement capturés ; elle n’anticipe pas la validation finale. Aucun build, test .NET, service, réseau ou Git n’a été exécuté par l’auteur du correctif. Les captures indiquées ci-dessous ont été lancées par le coordinateur et leurs compteurs TRX ont été relus hors ligne.

## Causes démontrées et corrections

| Cause | Preuve causale centrale | Correction |
|---|---|---|
| Une suppression physique document/tenant effaçait la génération ; recréer le même chemin réutilisait version1, chunk, plan et PointId | `tests/gc-generation-red-v1` : 8FAIL, 2 témoins PASS | Maximum tenant/document durable sans cascade ; réservation partagée aux admissions upsert/delete ; backfill des histoires lisibles |
| Une réservation A retardée pouvait reprendre la génération2 déjà attribuée à l’action opposée B | `tests/gc-admission-race-red-v1` : 2FAIL causaux, 18PASS | Admission CAS : seule la génération réservée peut avancer la ligne ; réservation dépassée → nouvelle réservation, jamais adoption de celle de B |
| La ligne d’un chemin pouvait être remplacée par un autre docId autorisé par le schéma entre résolution et admission ; réservation appliquée à la mauvaise identité | `tests/gc-custom-identity-red-v1` : 1FAIL Actual11/floorB50, 20PASS dont les deux races précédentes corrigées | CAS docId + version ; nouvelle résolution d’identité avant chaque réservation. Ce témoin d’import/maintenance ne prétend pas qu’un parcours WinUI modifie les IDs |
| HTTP206/202 avec JSON `result=[]` plausible était pris pour une lecture complète d’absence | `tests/gc-partial-http-red-v1` : 4FAIL initial/readback | HTTP200 complet requis pour les lectures et le delete ; ACK `completed` puis relecture exacte |
| Le registre empêchait UPDATE/DELETE de points mais permettait leur ajout après enregistrement | `tests/gc-manifest-append-red-v1` : 1FAIL NoException | Scellement irréversible après validation du nombre ; INSERT ultérieur interdit. Il s’agit du contrat manifeste immuable, sans prétendre qu’un actif a été corrompu dans un parcours applicatif |

Ces campagnes sont distinctes et se recouvrent partiellement ; leurs PASS ne sont pas additionnés en une nouvelle suite. Les changements d’autres outils pendant certaines captures sont déclarés dans les ancrages de campagne ; ils ne sont pas présentés comme des sources globalement inchangées.

## Architecture implémentée

- Migration072 : maximums conservés indépendamment des cascades, manifests et IDs exacts sans texte/vecteur, protections SQL contre régression/retrait annulé/manifeste étendu.
- `IngestionGenerationRegistry` : réservation SQL courte, incrément bigint, épuisement explicite sans retour à zéro. Une réservation consommée puis abandonnée peut laisser un trou ; aucune ancienne valeur ne redevient disponible.
- Admissions : CAS sur identité et génération, retries bornés à32, conservation de la publication précédente pendant une nouvelle ingestion. Les reprises du même job gardent leur génération et renouvellent le bail.
- `IngestionVectorRegistry` : enregistrement complet/scellé avant le premier upsert ; cible et manifeste immuables ; refus d’un plan retiré ou d’une empreinte d’une autre génération. Contrôles de reprise/écriture et garde de publication. Les anciennes publications non enregistrées restent non gérées et non supprimables.
- Retrait dans la transaction de publication/delete ; réconciliation bornée des plans jamais publiés devenus strictement anciens. La génération actuelle et la publication active restent protégées, y compris le legacy actif sans plan.
- `VectorGcQueue` : bail opaque de collecteur, curseur borné, checkpoints refusés après perte de bail, rechecks permanents des plans retirés.
- Transport : ancienne cible approuvée seulement ; credentials de son propre binding ; aucune redirection/proxy/cookie automatique ; seuls IDs exacts relus/vérifiés/supprimés ; tenant/document/version/plan/chunk contrôlés ; réponse limitée à1MiB ; aucun corps d’erreur brut dans les codes.
- Collecteur séparé : limites de plans/IDs/durée, timeout par lot, isolement d’un manifeste fautif, distinction transport/protocole/identité, relecture et recollecte des writes tardifs.
- Diagnostic : /ready sans changement de signature ni dégradation du critère global de lecture ; inventaire sûr + requête SQL readonly ; traces de ronde terminées et logs sans texte/URL/secret.

## Tests complémentaires prêts pour le GREEN

Les nouveaux contrôles couvrent aussi transaction publication/retrait et rollback, actif ancien conservé pendant échec/annulation de la génération suivante, plans alternatifs courants, tombstone non réactivable, survie aux cascades, bail de collecteur réaffecté, timeout sans faux checkpoint, recheck après écriture tardive, manifeste incomplet mis en quarantaine sans bloquer les autres, isolation tenant, épuisement de génération, cible historique et absence de clé courante sur celle-ci, payloads incohérents, ACK non terminal, HTTP401/503/404 distincts et collection manquante non créée.

Ils utilisent PostgreSQL réel centralement pour l’état/les transactions et un transport déterministe pour les contrats HTTP. Aucun succès de ce transport ne constitue une qualification du vrai Qdrant. Le vrai cycle en namespace isolé reste requis.

## Ancrage et exploitation

Le [gel source v1](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/artifacts/ingestion-runtime-gc-20261026/gc-source-freeze-v1.json>) contient18 fichiers, tous LF, agrégat `c5c5cf08f30a4f4d2febbae7e8dd390be3c51e1002152aafdf3c112181981732`. Cet ancrage précède les GREEN finaux et sera à rapprocher de leur inventaire ; il ne remplace pas leur vérification.

La [notice d’exploitation](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/artifacts/ingestion-runtime-gc-20261026/vector-gc-operations-v1.md>) précise l’opt-in Production, les cibles historiques, les rechecks, le diagnostic et le protocole distinct de restauration. Le défaut reste `Enabled=false`, visible dans /ready. Le [SQL readonly](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/artifacts/ingestion-runtime-gc-20261026/vector-gc-inventory-readonly-v1.sql>) ne modifie aucun état.

## Limites maintenues explicitement

Les vecteurs historiques sans manifeste/cible prouvés restent en quarantaine non complètement inventoriée. Les plans alternatifs de la génération courante restent reportés. Une absence est une observation datée, suivie de rechecks, jamais une promesse éternelle. La cohérence des barrières après restauration/reset/clone et l’arrêt des anciens writers sont un protocole d’exploitation distinct. La référence FILE du résolveur commun doit viser un petit fichier local : le présent lot ne prouve pas un délai d’arrêt universel sur UNC/NFS ou filesystem bloqué.

Ces limites sont conservées dans l’acceptation ; le produit ne devient pas approuvé par la seule existence du code et de ces tests. Aucun planning ni appel de modèle payant n’a été lancé dans ce lot.

## Addendum — GREEN central relu après le gel

La [contre-vérification des artefacts GREEN](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/artifacts/ingestion-runtime-gc-20261026/gc-central-green-review-v1.json>) confirme séparément :

| Campagne réellement exécutée par le coordinateur | Résultat TRX relu | Sous-ensemble du nouveau GC | Sources |
|---|---|---|---|
| `unit-all-gc-final-v1`, run `f43e20340bbf4e74b0dea3653089bc54` | 5 283 PASS, 0FAIL, 1 non-exécuté OCR externe | 28 PASS | `sourceChangedDuringRun=false` |
| `postgres-all-gc-final-v1/tests`, run `a4e71dac7aa049e08cbc8a47f09f1eb5` | 334 PASS, 0FAIL, 2 non-exécutés legacy | 24 PASS sur PostgreSQL réel isolé | `sourceChangedDuringRun=false` |

Les18 fichiers du gel ont été comparés à nouveau aux octets actuels : aucune divergence. Le JSON de revue conserve les SHA256 des manifestes et des cinq TRX relus, ainsi que les compteurs des sous-ensembles. Les non-exécutés restent distincts des PASS. Les anciens RED et les témoins recouvrants ne sont pas additionnés à ces deux campagnes.

Le coordinateur confirme aussi le passage des12 PDF Gold dans le nouveau runtime Linux natif TEI/Docling/Qdrant/PostgreSQL et l’arrêt de ses cinq containers ; cette revue ne l’a pas réexécuté. Cette campagne est explicitement en Development non signé : elle ne prouve pas une qualification Production signée. Le cycle réel spécifique GC (publication suivante, collecte, write tardif, recheck) et la reprise du livre36 pages demeurent à rattacher à leurs preuves propres avant la clôture globale.
