# Cycle de vie et GC sur la pile Linux déjà qualifiée

`invoke-linux-lifecycle.py` est séparé du runner de corpus. Il ne construit,
n'installe et ne démarre rien. Il consomme les cinq CID exacts déjà déclarés par
l'opérateur et les arrête dans son `finally`, sans supprimer les volumes, les
images, la collection, les données SQL ni les preuves.

Le mode par défaut est **NOEXEC** : lecture des manifestes/proofs d'entrée et
impression du plan seulement. La clé synthétique du backend, la configuration,
les PDF, Docker et HTTP ne sont accessibles qu'après `--execute`, sur Linux.
Cette clé bootstrap isolée fournit les deux en-têtes administrateur attendus ;
aucune clé de fournisseur LLM n'est utilisée.

## Prérequis

- Le `execution-result.json` d'un vrai corpus Linux doit être PASS : worker,
  SQL, publication vectorielle et recherche validés. Son owner hash, son runId
  et son manifeste doivent être exactement ceux fournis ici. Un test de
  transports simulés ou un simple plan ne remplace pas ce PASS.
- Les cinq CID du corpus doivent être redémarrés par l'opérateur. La DLL
  effectivement exécutée doit avoir le hash de son `backend-binary-identity.json`.
  La configuration montée doit avoir le hash de son `runtime-ownership.json`.
  Ces deux fichiers publics voisins sont requis, sans symlink.
- `VectorGc.Enabled=true`, `PollSeconds=1`, `RecheckSeconds=2`, aucun
  `ApprovedTargets` historique/externe. La cible courante doit être le service
  Qdrant réellement inspecté ou son endpoint loopback déclaré, dans la collection
  exacte du namespace. `/ready` doit confirmer que GC est activé.
- La configuration du corpus désactive déjà tous les fournisseurs/LLM, scanner
  et watcher automatiques. Aucun job ne doit être actif au début du parcours.
- Seul `fr-scan-critical-v2.pdf`, généré et hashé par le manifeste synthétique,
  est modifiable. Le tenant est explicitement
  `77777777-7777-4777-8777-777777777777`. La source originale et la copie de
  travail doivent être distinctes, sous la racine possédée, sans symlink.
- Une configuration unsigned Development conserve cette qualification explicite :
  même PASS, `deploymentQualificationPassed=false`.

Les versions initiales A/B/C sont des noms de phases. Le runner lit leurs
numéros réels et exige leur progression stricte ; il ne suppose pas que la base
commence forcément à la génération 1.

## Parcours obligatoire

1. Lire la publication A, son manifeste scellé et ses vrais vecteurs/payloads.
   Vérifier checksums UTF8, génération, révision, source, IDs physiques dérivés
   de chaque chunk canonique et de l'empreinte complète du plan.
2. Réingérer le même fichier par `/ingest/enqueue`, attendre son vrai job DONE,
   lire la publication B et exiger une génération, révision, plan et des IDs
   physiques nouveaux. Recherche positive, contre-témoins et recherche→lecture
   doivent retrouver exactement cette publication courante.
3. Attendre à la fois le sweep SQL complet de A et l'absence réelle de ses IDs
   dans Qdrant. Relire B pendant ces observations : elle doit rester complète.
4. Réinjecter **un seul ancien point A capturé**, avec son vrai vecteur et son
   payload inchangés, par PUT exact avec ACK `completed`. Observer sa présence,
   puis un nouveau sweep SQL portant `reappearance_removed` et son absence.
   B doit encore être intacte. C'est une simulation contrôlée d'écriture tardive,
   **pas** la preuve d'annulation d'une requête HTTP du worker en vol.
5. Retirer uniquement la copie de travail connue et demander sa suppression par
   l'API. Exiger le job DONE, indexedVersion=0, plan actif NULL, recherche vide,
   puis le sweep/absence des points B.
6. Recopier les octets originaux hashés, réingérer et relire C. Exiger une
   génération strictement supérieure à celle de suppression et aucun ID déjà
   utilisé par A/B. Effectuer à nouveau recherche→lecture courante.
7. Comparer la publication SQL/checksums des autres documents et leurs vrais
   vecteurs/payloads entre baseline et fin. Une disparition de point ou une
   modification de vecteur/payload fait échouer la campagne.

Chaque attente de job/GC est bornée (`--maximum-wait-seconds`, 30 à 1800,
défaut 300). Les captures privées sont neuves et ordonnées : jobs/API, SQL,
points complets, ACK, observations de présence/absence. Le résumé public ne
contient ni texte PDF, ni vecteur, ni payload documentaire, ni clé.

`--attempt-cancel` ajoute une tentative sur une nouvelle génération après C.
Si l'API confirme l'annulation et que le job est canceled, la publication
précédente doit être conservée, avec son écart de générations assumé. La source
actuelle n'est pas déclarée fraîche sur cette seule observation. Si la fin du
job gagne la course, la tentative est `NON_EXECUTED` et sa nouvelle publication
est vérifiée ; ce cas ne devient jamais une preuve d'annulation. Le scénario de
remplacement par un PDF tronqué est explicitement NON_EXECUTED dans ce périmètre.

## Commande

Utiliser d'abord cette commande sans `--execute` et relire le plan :

```bash
root=/home/maxime/saaia-ingestion-qualification/48e5eba2-ea16-41da-98a7-65472c7f6dd6
python3 -B tools/validation-ingestion/invoke-linux-lifecycle.py \
  --owner "$root/owner.json" \
  --manifest "$root/corpus/manifest.json" \
  --corpus-proof "$root/evidence/CORPUS-PASS/execution-result.json" \
  --source-directory "$root/corpus/sources" \
  --output "$root/evidence/lifecycle-gc-01" \
  --api-key-file "$root/private/backend-api-key.txt" \
  --maximum-wait-seconds 300
```

Les chemins exacts de la préparation opérateur remplacent ces noms illustratifs.
Ne pas renommer ni réécrire une ancienne preuve pour la faire correspondre.

Le cleanup peut observer/annuler même si le PDF est devenu anormal : il conserve
les gardes d'ownership et de chemins, mais ne fait plus du hash documentaire une
condition à l'annulation. Il ne vise que les job IDs réellement admis ici et ce
docPath. Tout job actif inconnu reste intact et rend la clôture non validée.
Il attend zéro job actif puis arrête les cinq CID exacts. La seule copie de
travail retirée par ce runner est restaurée si nécessaire et vérifiée. Le
contrôle des ports concerne les bindings Docker, pas tous les listeners hôte.

Les tests `tools.ingestion_gold.tests.test_linux_lifecycle` sont **purs** : leurs
transports et vecteurs sont simulés, sans moteur OCR, worker, API, SQL ou Qdrant
réels. Ils valident le protocole et les refus de faux PASS ; une exécution
opérateur distincte doit encore produire les preuves réelles du runtime.
