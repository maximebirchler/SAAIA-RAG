# Configuration et exploitation du GC vectoriel

Le collecteur est **désactivé par défaut** (`VectorGc.Enabled=false`). L’enregistrement des nouvelles générations/plans fonctionne indépendamment de cette activation. Désactivé ne signifie donc ni purge en cours ni ancienne dette effacée.

## Activation explicite

Dans la configuration de déploiement effectivement chargée et signée en Production, après qualification sur le namespace possédé :

```json
{
  "VectorGc": {
    "Enabled": true,
    "AllowCurrentTarget": true,
    "RequireTargetRevision": true,
    "PollSeconds": 30,
    "MaxPlansPerRound": 4,
    "MaxPointsPerBatch": 128,
    "RoundTimeoutSeconds": 30,
    "BatchTimeoutSeconds": 10,
    "RecheckSeconds": 3600,
    "FailureBackoffSeconds": 300,
    "ApprovedTargets": []
  }
}
```

L’activation n’ajoute aucun appel de modèle. Les limites effectives sont bornées dans le code : 1–16 plans par tour, 1–256 IDs par lot, 1–120 secondes par tour et 1–30 secondes par lot. Un lot utilise au maximum lecture, suppression exacte, relecture. Chaque ticket a son propre bail ; aucun verrou documentaire n’est maintenu pendant ces appels.

La qualification isolée peut utiliser des intervalles plus courts (poll 1 seconde, recheck 2 secondes). Ces valeurs de campagne ne doivent pas être confondues avec une politique de production souhaitable : un recheck court augmente les lectures répétées de manifestes déjà absents.

`AllowCurrentTarget=true` n’autorise que le descripteur historique strictement identique à la cible Qdrant courante (endpoint, collection, révision). Une ancienne cible différente reste bloquée. `RequireTargetRevision=true` refuse un descripteur sans révision renseignée ; une URL égale ne suffit pas à attester une restauration/recréation de namespace.

## Ancienne cible et credentials

Une cible historique nécessite une approbation explicite indépendante de la nouvelle configuration :

```json
{
  "VectorGc": {
    "Enabled": true,
    "ApprovedTargets": [
      {
        "Endpoint": "https://qdrant.example.invalid:6333",
        "Collection": "ancien_namespace_verifie",
        "Revision": "revision_historique_verifiee",
        "ApiKeyRef": "ENV:SAAIA_GC_OLD_QDRANT_KEY",
        "AuthMode": "api-key",
        "AllowUnauthenticated": false
      }
    ]
  }
}
```

Cet exemple utilise une adresse fictive. Aucune clé n’est stockée dans le manifeste SQL. Le transport résout les credentials de la cible correspondante ; il ne reprend pas la clé courante pour l’envoyer à une ancienne URL. Les redirects, cookies et proxies automatiques du client GC sont désactivés. Les IDs canoniques des citations ne deviennent pas les IDs physiques du stockage.

Le résolveur commun prend en charge les références ENV/FILE. **Limite de délai** : sa lecture FILE est synchrone. Les fichiers de credential employés ici doivent être locaux et petits ; la qualification n’établit pas un arrêt strictement borné pour un chemin UNC/NFS arbitraire ou un filesystem bloqué. ENV est préférable pour la qualification du délai. Aucun fichier/secret réel n’a été consulté pour cette note.

Un changement d’image du runtime et un changement de namespace sont deux opérations distinctes. Avant d’approuver un ancien descripteur, l’opérateur doit établir qu’il vise bien le stockage historique possédé. Le programme n’invente pas cette attestation à partir du seul nom de collection.

## Périmètre effectivement collectable

Seuls les manifestes complets, scellés, retirés irréversiblement et de génération strictement ancienne sont traités. Le retrait exclut toujours la publication active. Une génération courante annulée/échouée et ses plans alternatifs restent protégés. Une publication active legacy sans empreinte protège sa génération entière.

Un plan inconnu, une ancienne cible non prouvée ou des vecteurs historiques sans manifeste exact ne sont pas supprimés. Ils constituent une dette non inventoriée complètement ; `vector_gc_legacy_inventory_complete=false` le rappelle. Ce collecteur ne fait aucun delete documentaire/version large et ne découvre pas implicitement tous les anciens vecteurs.

Un ACK HTTP ne prouve pas l’absence : le contrat exige HTTP200, état Qdrant `completed`, puis relecture complète des IDs. Les réponses206/202 et les bodies plausibles incomplets restent des échecs de vérification. Une lecture401/503 est distincte d’une identité de payload incohérente.

## Rechecks, reprise et incidents

Après un balayage, le manifeste reste retiré et garde une prochaine date de vérification. Un upsert ancien déjà émis peut terminer après le delete ; ses points restent exclus par la publication SQL et peuvent être recollectés au recheck. Il n’existe pas de `done` définitif qui efface cette possibilité.

Après timeout/crash ou perte de bail, les mêmes IDs sont repris. Un ancien collecteur ne peut pas valider le checkpoint de son successeur. Une identité de point incohérente entraîne un report/quarantaine, jamais une extension du filtre de suppression. Un manifeste endommagé est reporté durablement sans empêcher les autres plans de la ronde d’être traités.

Les compteurs distinguent IDs acquittés et absence observée. `last_sweep_completed_at` signifie qu’un balayage borné a parcouru le manifeste ; il ne constitue pas une observation atomique et éternelle de tout le stockage à cet instant. `last_batch_absence_at` date une observation de lot.

## Diagnostic disponible

`/ready` expose :

- `vector_gc_enabled` : activation effective ;
- `vector_gc_state` : démarrage du worker, tour en cours, dernière date/code, dernier bilan et inventaire ;
- `vector_gc_scope=registered_exact_manifests_older_generation` ;
- `vector_gc_legacy_inventory_complete=false`.

Le GC ne transforme pas à lui seul une panne de maintenance en indisponibilité globale de lecture. Ses états de report restent toutefois visibles dans ce diagnostic. Les comptes sont le dernier instantané réussi ; un inventaire absent ou ancien n’est pas présenté comme une dette nulle actuelle.

Le [diagnostic SQL en lecture seule](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/artifacts/ingestion-runtime-gc-20261026/vector-gc-inventory-readonly-v1.sql>) donne les comptes enregistrés/retirés/protégés, rechecks dus, cibles bloquées, erreurs transport/protocole, quarantaines et publications actives sans manifeste. Il détaille au maximum100 plans sans texte, vecteurs, chemins, URL ou références de secret. Un ticket dû peut encore avoir un bail courant ; le diagnostic l’indique.

Les logs/spans `vector_gc.round`, `vector_gc.claimed`, `vector_gc.batch`, `vector_gc.inventory`, `vector_gc.reappearance_observed` et `vector_gc.lease_lost` ne portent que codes, IDs internes, durées et compteurs. Ne jamais confondre le nombre `delete_acknowledged` avec un nombre de suppressions définitivement prouvées.

## Ne pas réinitialiser les barrières

Les tables `ingestion_generation_watermarks`, `ingestion_vector_plans` et `ingestion_vector_plan_points` ne sont pas des caches. Elles survivent aux suppressions ordinaires de document/tenant. Le maximum ne diminue pas, le manifeste scellé ne s’étend pas, et le retrait ne s’annule pas.

Une restauration de base ancienne, une copie de base vers un namespace partagé, la recréation homonyme d’une collection ou un reset intégral relèvent d’un protocole distinct : désactiver le GC, arrêter les anciens writers, rapprocher l’identité du namespace et conserver/reconstituer les barrières prouvées ou choisir un nouveau namespace. Réutiliser une ancienne base avec le même stockage en effaçant ces maximums peut réintroduire les collisions précisément interdites par le correctif. Les anciens binaires d’admission ne doivent pas rester writers d’un namespace dont le GC est activé.
