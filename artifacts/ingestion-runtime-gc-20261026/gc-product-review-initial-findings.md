# GC produit — constats de contre-revue intermédiaire

Revue en lecture seule, sans test .NET, réseau, service, Git ou modification produit. Le CAS d’admission et les diagnostics sont encore en correction chez leur propriétaire. Cette capture n’est donc pas une approbation finale ni une affirmation portant sur une révision future.

## R01 — Une réponse HTTP partielle peut devenir une preuve d’absence

`VectorGcQdrantTransport.ReadVerifiedAsync` accepte tout `IsSuccessStatusCode`, puis un tableau vide devient une absence pour tous les IDs demandés. **HTTP206 + `{status:"ok",result:[]}`** satisfait donc le chemin de succès, bien que Partial Content ne garantisse pas une observation complète. La même réponse au readback après DELETE permettrait de confirmer à tort la purge. Une réponse HTTP202 plausible est aussi acceptée par ce test de statut trop large.

Reproduction hors réseau proposée : handler HTTP synthétique répondant 206/202 avec cette forme, avant DELETE puis après ACK `completed`. Exiger `AbsenceVerified=false` et un code indiquant la réponse incomplète. Le statut complet du contrat Qdrant réellement qualifié (200 pour ce parcours) doit précéder l’interprétation des IDs absents. Aucun de ces tests n’est exécuté par cette revue.

Ancrage : `backend/SAAIA.Backend/Ingestion/VectorGcQdrantTransport.cs`, SHA256 **`28ba18901c1153f2f8c930b40064d307672b3a7691134cf1a8b39a26eedbbb37`** ; lectures aux appels autour des lignes 62/74 et contrôle HTTP dans `ReadVerifiedAsync` autour de 96.

## R02 — Un manifeste incomplet n’atteint pas son diagnostic durable

`VectorGcQueue.ReadBatchAsync` refuse correctement un nombre/ordre de points incompatible. Mais son appel dans le collecteur est placé **avant** le `try` par plan. Une telle erreur remonte donc hors de la ronde sans checkpoint, code `last_gc_code` ni backoff/quarantaine de ce plan. Après expiration du bail, le même candidat peut refaire échouer la ronde entière.

Il s’agit du chemin d’un manifeste incomplet/importé/corrompu ; aucune corruption d’un registre créé atomiquement n’a été constatée en production. La sécurité de suppression reste conservatrice : aucun DELETE n’est envoyé. Le défaut porte sur le diagnostic durable et la reprise des autres candidats.

Ancrages : `VectorGarbageCollector.cs` SHA256 **`a8ebedd52c92f149b35d043411b370bd947826f1e4934c00dfc083ba5dd1da4b`**, appel vers ligne45 ; `VectorGcQueue.cs` SHA256 **`ef78d7cd53d4b3d21956da7cae6207e4d9379f25423e12fe347c4762ba3137a0`**, contrôle vers ligne35.

## R03 — Certains refus ne sont classés dans aucun compteur d’erreur

Les codes `read_response_invalid`, `response_json_invalid` et `response_or_manifest_invalid` ne sont inclus ni dans `TransportFailures` ni dans `QuarantinedPlans` de l’inventaire relu. Le dernier résultat de ronde reste dégradé et son code durable peut exister, mais ces deux compteurs peuvent rester zéro malgré le refus d’un lot. La correction des diagnostics est déjà dans le périmètre du propriétaire ; le constat lui a été transmis avant gel.

Ancrage : `VectorGcRuntimeState.cs`, SHA256 **`db82084710a0cb82353d97647c3a0a91b4f2370f5b3480cd7582f397eb3fb903`**, SQL autour de25–26.

## Propriétés positives relues et limites

Le transport utilise les seuls IDs du manifeste, vérifie tenant/document/génération/plan/chunk avant DELETE, sépare ACK `completed` et lecture d’absence, et ne crée pas une collection manquante. Le client dédié interdit proxy, redirect et cookies ; une cible historique non approuvée ne récupère pas l’URL ou la clé de la cible courante. Aucun texte source, corps HTTP ni secret n’est écrit dans les logs du lot relu.

La retraite reste strictement plus ancienne que la génération courante et protège la publication active, y compris son cas legacy sans plan. Le registre est persistant, la retraite irréversible, les IDs physiques dépendent du plan et le checkpoint exige owner+expiry. Les requêtes HTTP sont hors transaction SQL et les manifestes retirés sont revérifiés périodiquement pour traiter une écriture tardive. Je n’ai pas identifié de bypass normal réactivant un plan retiré dans ces surfaces ; la preuve définitive d’admission demeure le lot CAS encore en cours.

Les délais HTTP/SQL et le token de ronde sont bornés, avec une courte finalisation possédée. Une référence de secret `FILE:` est toutefois lue synchroniquement par le resolver partagé : une précondition de fichier local/petit reste nécessaire pour prétendre borner également cet I/O ; un partage de fichiers bloqué ne devient pas annulable grâce au seul token HTTP. Aucune référence de secret réelle n’a été lue pour cette observation.

Ces constats sont transmis à l’agent GC et à l’agent principal. Un rapport final devra porter les nouveaux hashes et distinguer résolution statique, tests réellement capturés et limites encore ouvertes.
