# Contre-revue du jalon A21D64DA

Relecture indépendante des captures déjà enregistrées, avec calculs locaux en mémoire. Aucun modèle, appel API, service, build ou Git exécuté par l'agent chargé de cette revue. Les notes des jalons précédents restent historiques et ne sont pas réécrites.

## Ancrage du code et des essais

Le SHA backend est **`A21D64DA8465CCB39449008FDD5106C81860B4D90E1290A2FA83340F3F827BC7`** dans le test ciblé des titres, la suite `unit-all-final-v3`, le worker synthétique/lifecycle v2 et le worker du livre enrichi v3. Les manifestes embarqués des douze documents synthétiques enregistrent également ce SHA.

La suite unitaire v3 donne 5 255 PASS, zéro échec et un non-exécuté explicite, sources stables. Les huit tests ciblés des titres donnent huit PASS après six échecs causaux et deux témoins verts.

La suite PostgreSQL finale **v3**, run `5b1a83c0911147a2b549323575e02856`, donne ensuite **310 PASS, zéro échec et deux non-exécutés historiques sur le même SHA A21D64DA**. Sources stables, empreinte avant/après `A01F65BC99F5AE780381643BFF76D672ED247D20A55ED7687E8912806E92E496`. Son cluster isolé est arrêté et supprimé ; environnement restauré, aucun cluster existant modifié et aucun listener restant, d'après son manifeste parent.

La précédente suite PostgreSQL v2 avait le SHA **00A011D0…107ABD7** ; cette preuve reste distincte. La comparaison complète des inventaires capturés (`final-source-delta-v2-v3-review.json`) trouve exactement trois chemins changés entre les deux jalons : nouvelle fixture des titres, nouveau helper de rétention et appel/partial dans le projecteur. Les sources du bail, worker, JobRepo, enqueue, publication et du protocole lifecycle sont identiques, hash pour hash. Aucun comportement d'annulation nouveau n'est déduit d'une simple proximité de version.

## Worker complet et publication

Le run `worker-full-e5-lifecycle-final-v2` passe ses douze cas, seize pages, quatre-vingt-dix faits physiques, trente-sept chunks et trente-sept points. Les douze évaluations canoniques, les faits des seize captures de contexte, les trente-sept checksums et les trente-sept UUID de points ont été recalculés sans écart. Les bundles correspondent à leurs tailles et hashes stockés. Les points gardent le chunk canonique et le plan SQL actif attendus.

Les contrôles de recherche→lecture et négatifs du run sont verts. Cette vérification concerne le corpus et le profil réellement exécutés ; `vectorValidated=false` est conservé pour ne pas transformer ce contrôle en mesure de rappel sémantique général.

## Deux captures d'annulation distinctes

| Campagne | Versions constatées | Résultat réel de l'annulation |
|---|---|---|
| Lifecycle v1, SHA 00A011D0 | 1 → 2 → suppression 3 → recréation 4 → job 5 annulé, publication 4 conservée | **PASS** : running/document_intelligence observé, ACK booléen true, terminal canceled ; révision, plan et point de 4 inchangés |
| Lifecycle v2, SHA A21D64DA | 1 → 2 → suppression 3 → recréation 4 → publication 5 terminée | **NON_EXECUTED** : `job_completed_before_cancel_effect`, la fin du job a gagné la course |

Les snapshots SQL privés du run v2 confirment chaque version et zéro job actif aux points de contrôle. À la suppression : indexedVersion 0, état deleted et plan nul. À la fin : indexedVersion 5, état indexed. On ne décrit donc pas ce run comme une conservation de V4 après annulation réussie.

La preuve de cancellation effective reste celle du v1. Le contrôle de source identique permet de rattacher ce résultat au même code de bail/annulation, **sans transformer le NON_EXECUTED du v2 en PASS**. Dans les deux campagnes, `upstreamEngineCancellationVerified=false` : aucune coupure du calcul Docling amont n'est attestée.

## Livre enrichi : les deux titres sont maintenant couverts

`worker-recipe-e5-final-v3` porte le même PDF source original de 36 pages et un nouveau manifeste de seize faits physiques, ajoutant les deux observations auparavant absentes. Son manifeste correspond au hash du plan. Il publie **160 chunks, 160 ancrages et 160 points**. Les seize faits, les 160 checksums et les 160 UUID ont été recalculés sans écart.

Pour chacun des deux block IDs exacts des pages 2 et 4 :

- un seul chunk de la bonne page référence l'observation ;
- son type est `physical_page_context_v1` ;
- le signal persistant `canonical_uncovered_observed_heading_context` est présent ;
- SectionOrdinal, SectionTitle et HeadingPath ne sont pas affirmés ;
- le texte attendu est présent dans les captures réelles de contexte utilisées pour valider les seize faits.

Ces résultats prouvent la rétention indexée et la lecture des deux occurrences, sans leur inventer un sujet voisin. La recherche positive générale enregistrée du livre reste le fait p5-servings, suivie de lecture et de deux contrôles négatifs. Le run ne contient pas, à lui seul, une recherche positive distincte consacrée à chacun des deux nouveaux titres ; cette variante doit garder sa propre preuve si elle est exécutée ensuite.

Les comptes 261 puis 158 appartiennent aux anciennes campagnes ; ils ne décrivent pas ce lancement à 160. Le changement de compte ne signifie pas une modification du PDF source.

## Nettoyage et limites

Les deux runs A21D64DA enregistrent `ownedEnvironmentStopped=true`, zéro job actif avant l'arrêt et arrêt de leurs environnements backend/PostgreSQL possédés. Leur champ `externalDependenciesStopped=false` reste exact à la fin de ces runs : les dépendances OCR/embeddings/Qdrant ont été clôturées séparément ensuite.

La preuve achevée `dependencies-v7/dependency-stop-f5c8d29552564802a770a27f7c0e6b6b.json` enregistre `executeRequested=true`, `prevalidationPassed=true`, `processesStopped=true`, `listenersReleased=true`, `wrapperLogsFlushed=true`, avec achèvement à `2026-10-26T01:21:26.0322492+00:00`. Le manifeste des dépendances est `stopped`. Ces valeurs sont relues dans les captures ; aucun nouveau contrôle de service en ligne n'a été effectué par cet agent.

Restent hors de cette qualification : GC des anciens plans vectoriels, rappel sémantique exhaustif, binaire TEI, image Docker Docling, OCR externe historique et interprétation générale des figures. Le profil exécuté est local Windows CPU avec Docling/RapidOCR et E5 Transformers ; son succès ne s'étend pas implicitement à un autre moteur ou déploiement. L'overfetch dense demeure borné, avec diagnostic de fenêtre incomplète si nécessaire. Les titres explicitement de navigation ne sont pas promus par le nouveau repli de contenu.

Aucun planning de repas ni réponse de modèle payant n'est approuvé par ces seules preuves. Le statut global reste `TESTE_NON_APPROUVE`.
