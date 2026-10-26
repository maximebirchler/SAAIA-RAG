# Harnais Linux de cycle de vie et GC — préparation gelée

Statut : outillage implémenté et testé avec transports simulés ; aucune campagne
Linux réelle exécutée par cet agent. Ce rapport n'approuve ni le produit ni le
déploiement Production. Le runtime déclaré unsigned Development reste explicitement
inéligible à cette qualification.

## Fichiers

- `tools/validation-ingestion/invoke-linux-lifecycle.py` : runner séparé, NOEXEC
  par défaut, sans bootstrap, installation, build ou SSH.
- `tools/validation-ingestion/linux_lifecycle_checks.py` : gardes pures, identité
  des générations/points, observations de sweep et SQL SELECT statique.
- `tools/ingestion_gold/tests/test_linux_lifecycle.py` : 18 tests nouveaux, avec
  vecteurs et transports simulés, aucun appel moteur ou provider.
- `tools/validation-ingestion/README-linux-lifecycle.md` : prérequis, commande,
  phases, cleanup et limites.

Le runner principal `invoke-linux-ingestion.py` est inchangé, SHA256
`eda60c2c8ff2dadfdb19e7f7f9afc4838cf3b59a6bdbdc1b055f83aa45115969`.

## Parcours préparé

Le PASS préalable du corpus doit concerner le même runId, les mêmes cinq CID,
le même manifeste et le cas synthétique français `fr-scan-critical-v2`. Le hash
de la DLL dans le conteneur est comparé à la preuve du corpus ; celui de la
configuration montée est comparé à son `runtime-ownership.json`. Une divergence
est refusée avant admission d'un job.

Le parcours capture A depuis SQL et Qdrant avec ses vrais vecteurs/payloads,
réingère la source identique vers B, exige des générations/révisions/plans/IDs
physiques distincts, attend sweep SQL et absence Qdrant de A en préservant B,
puis réinjecte un seul point A capturé. Sa présence doit être observée, suivie
d'un nouveau sweep `reappearance_removed` et de son absence, B toujours intacte.
La suppression API doit ensuite désactiver la publication et nettoyer B ; la
recréation à partir de la source hashée doit publier une génération supérieure,
avec des IDs jamais réutilisés. Chaque publication est vérifiée par recherche
positive, contre-témoins et recherche→lecture courante.

Le SQL et les checksums des autres documents sont comparés sur le parcours.
Leurs points courants sont aussi relus en entier en baseline et en fin : les
identités, vecteurs et payloads doivent être identiques. Le simple maintien des
lignes SQL ne suffit donc pas à annoncer leur préservation.

Le replay du point A est une écriture tardive contrôlée dans cette collection,
pas la preuve d'annulation d'une requête HTTP du worker. Une tentative optionnelle
de job cancellation indique NON_EXECUTED si la fin du job gagne la course ; la
fraîcheur de la source sur disque n'est jamais inférée d'un canceled seul. Le
remplacement par PDF tronqué n'est pas exécuté dans ce périmètre.

## Contre-revue et corrections

Les trois défauts repérés pendant la contre-revue ont été corrigés avant gel :

1. La configuration courante est liée par SHA256 à celle du corpus PASS.
2. La préservation des autres publications inclut les vrais vecteurs/payloads,
   avec contrôle des identités et empreinte ordonnée, en plus du SQL.
3. Le cleanup ne dépend plus de la validité documentaire. Il garde les contrôles
   d'ownership et de chemins, annule seulement les job IDs admis ici pour ce
   docPath, observe zéro job actif, puis arrête exactement les cinq CID. Un job
   inconnu n'est jamais annulé. L'anomalie documentaire reste un échec de campagne.

La cible courante de GC doit être le service Qdrant possédé ou son endpoint
loopback déclaré, dans la collection du namespace, sans ApprovedTargets
historiques/externes. Aucune suppression par filtre large ni mutation SQL n'est
introduite. La source originale, les volumes, les bases et les preuves sont
conservés ; seule la copie de travail connue est retirée puis restaurée.

## Vérification exécutée

Capture neuve : `tests/linux-lifecycle-offline-v1/manifest.json`.

- 51 tests PASS, 0 échec, 0 erreur, 0 skip ; 18 nouveaux tests lifecycle.
- 4 modules : lifecycle, plan Linux, ownership Linux et contrat TEI.
- Inventaires avant/après identiques : `sourceChangedDuringRun=false`.
- Python 3.12.14 ; observation UTC 2026-10-26T07:02:54.590793+00:00.
- Zéro Docker, HTTP, SQL, OCR, worker ou provider réel dans cette exécution.

Le test protocole rejoue A→B→GC→réapparition→GC avec de faux transports et
vérifie le PUT exact du point capturé, la disparition finale et la préservation
de B. Il ne remplace pas l'exécution opérateur du parcours complet.

Empreintes gelées :

| Fichier | SHA256 |
|---|---|
| invoke-linux-lifecycle.py | ef1fe8dde00c5e6182d45fa9612cd8606518ea7e4a6a89d033f4d660dd8c2662 |
| linux_lifecycle_checks.py | b592022ed7873a7f9693df9c9f5ea827701bd2178343206988af038a0b3f6606 |
| test_linux_lifecycle.py | 41f9261d00e5eeb188b790822c003ff645b5d43bc19ea5eef83bd05384264e98 |
| README-linux-lifecycle.md | 796ea83394440389ebdfd6664b805e5231720e01aeed9fe9d16a32ec3047a086 |

## Limites opérationnelles

`lastGcCode=reappearance_removed` est une observation transitoire : un recheck
ultérieur peut le remplacer par `absence_observed` avant l'échantillon. Le
runner exige ce témoin et échoue à délai borné s'il ne le voit pas ; ce risque
donne un faux échec possible sous charge, jamais un succès supposé.

Les contrôles de clôture prouvent l'arrêt des cinq conteneurs possédés et
l'absence de leurs bindings Docker ; ils ne prétendent ni inventorier tous les
listeners hôte, ni effectuer une capture réseau. La preuve des modèles/engines
vient du corpus réel précédent du même namespace ; ce parcours qualifie leur
continuité et les effets du cycle de vie/GC, pas une nouvelle qualité sémantique
générale du corpus ou des réponses LLM.
