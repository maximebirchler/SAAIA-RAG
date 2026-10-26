# Harnais du vrai worker — préparation vérifiée

Les nouveaux outils sont prêts pour une exécution réelle par l'orchestrateur.
La dernière capture a exécuté **32 tests Python** et **35 gardes PowerShell**,
tous réussis, après revue indépendante et corrections ciblées. Deux plans
NOEXEC avec les profils observés avaient aussi réussi dans la capture v2,
avant les dernières corrections d'observabilité/provenance. Cette préparation n'a lancé
aucun backend, PostgreSQL, Docling, Qdrant ou modèle et n'a fait aucune requête
HTTP/SQL ni lu de credential. Le seul test réel restant à ce stade de cette
note appartient à la campagne séparée à lancer explicitement avec `-Execute`.

La capture actuelle est `worker-harness-offline-v4/offline-validation.json`,
avec son journal Python et `guards/guards.json`. Les deux plans NOEXEC restent
conservés dans `worker-harness-offline-v2`. Les données des tests du vérificateur sont
synthétiques : elles ne prétendent pas être une capture de moteur exécuté.

## Ce qui a changé

- `start-environment.ps1` accepte un overlay optionnel **avant** le lancement du
  backend. Sans ce paramètre, les défauts WinUI sont identiques. L'overlay doit
  être un JSON borné sous les artifacts du dépôt ; il ne peut toucher ni auth,
  base, clé, provider LLM, commande OCR externe ni valeurs de chunking.
- `OwnedIngestionConfig.ps1` impose worker canonique unique, OCR Docling réel,
  URLs HTTP à IP loopback et versions de moteurs explicites. Watcher, scanner,
  ancien OCR et réindexation automatique sont désactivés dans ce parcours.
- `invoke-owned-ingestion.ps1` reste inactif sans `-Execute`. Il contrôle sources,
  sélection et profils, puis présente le plan. Au réel, il crée un nouveau
  cluster/instance backend, prouve les tables vides, copie les vrais PDF et
  appelle `/ingestion/scan`. Il ne contient aucun seed canonique et utilise
  uniquement la branche SQL READ ONLY du helper d'ownership réutilisé.
- Les jobs doivent finir `done`. Les identités du wrapper backend et de son
  enfant dotnet sont vérifiées avant scan, polling, lectures et annulations.
  La base est protégée par PID/date/data-directory/port/postmaster existants.
- Le vérificateur décompresse le bundle canonique réellement stocké et contrôle
  taille/hash, source/révision/document, checksums UTF-8 des chunks, ancres,
  faits physiques et contexte API. Il ne reconstruit pas des unités attendues
  pour les injecter dans la base.
- Les colonnes SQL vérifiées par checksum restent prioritaires sur les champs
  homonymes de metadata. Un contre-test contient un texte SQL ayant perdu ses
  faits et une metadata trompeuse qui les réintroduit : le verdict échoue.
  `ingestionWorkerExecuted` dépend d'un état réel running/done ou d'une tentative
  observée ; le seul enqueue ne suffit jamais.
- Qdrant est un endpoint réel explicite, dans une collection UUID neuve. Chaque
  ID de chunk est relu ; compte exact, `chunk_id` et fingerprint du plan doivent
  correspondre au job. Les contre-tests rejettent réponse vide, ID incorrect,
  doublon ou ancien plan. Une collection présente ne suffit pas.
- Le `finally` annule au besoin uniquement les jobs de cette instance possédée,
  vérifie les actifs puis arrête seulement les helpers/PG possédés via le stop
  existant. Les dépendances Docling/Qdrant/Transformers ont un autre ownership
  et restent sous le contrôle de l'orchestrateur. DB et preuves sont conservées.

Un premier démarrage réel de l'orchestrateur a révélé une provenance absente
avant tout job. Pour le seul overlay canonique contrôlé, le bootstrap fournit
maintenant `SAAIA_CODE_REVISION=sha256:<DLL réellement chargée>`. Un hash Git HEAD
ne masquerait plus un build issu d'un arbre modifié. Les défauts WinUI restent
inchangés. Deux gardes pures vérifient cette identité et le refus d'un simple
commit de 40 caractères. Le problème distinct de chemin de stockage Qdrant
Windows a été pris en charge séparément par l'orchestrateur.

## Deux modes d'embeddings explicites

Le premier mode utilise le substitut 3D déterministe WinUI pour isoler le cœur
d'ingestion, avec stockage Qdrant réel. Le second accepte
`-EmbeddingProfilePath` pour le modèle réel Transformers CPU local. Il refuse
une révision mutable ou un endpoint distant. Avant tout job, `/info` devra
confirmer adaptateur, modèle, commit, hash des poids et identité du runtime.

Les plans des deux modes ont été validés avec les profils fournis sous
`out/ingestion-runtime-20261025` et le manifeste de dépendances v4 `ready`.
NOEXEC n'a pas interrogé `/info` : l'attestation réseau n'est validée qu'au réel.
`embeddingsReal=true` ne prétend ni être le binaire TEI ni prouver la qualité de
la recherche dense. `vectorValidated=false` demeure explicite ; la publication
effective des points a un champ distinct.

## Exécution suivante

Le README des outils contient la commande complète. Les prérequis restent :
backend produit recompilé et stable, dépendances réellement prêtes sous leur
ownership actuel, profils observés cohérents, dossier de campagne neuf. Le
sous-ensemble par défaut est `fr-scan-critical-v2` et `fr-mixed-critical-v2`.

Le hash de DLL des plans NOEXEC est une observation de préparation, pas une
raison de sauter le build final. Au lancement, le runner recalcule le hash puis
exige que le bootstrap ait chargé cette même DLL. Aucune validation du vrai
worker, de la publication SQL, des points ni de la cancellation en vol n'est
annoncée dans cette note avant sa véritable exécution.

## Limites à garder visibles

Le vérificateur seul ne prouve pas l'exécution ni l'absence de seed : il doit
être rattaché à la preuve du runner (baseline vide et enqueue API). Le contrôle
d'ancres vérifie leur présence et leur cardinalité, pas un audit exhaustif de
chaque polygone. Le parcours de lecture récupère des chunks entiers qui peuvent
chevaucher des pages ; la preuve des faits sur la page canonique reste séparée.
Un arrêt backend n'atteste pas à lui seul qu'une conversion dans un moteur
local indépendant est déjà terminée après cancellation. Ces points ne doivent
pas être transformés en approbation globale du produit.
