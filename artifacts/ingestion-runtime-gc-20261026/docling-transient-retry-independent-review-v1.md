# Contre-revue indépendante du retry Docling

Statut : **revue de source favorable, exécution centrale et qualification Linux encore requises**.

Périmètre : `DoclingClient.cs`, `DoclingRetryPolicy.cs`, enregistrement DI dans `ServiceCollectionExtensions.cs`, `DoclingClientTests.TransientRetry.cs`, et lecture du collecteur `BackendDiagnostics`. Aucune source n'a été modifiée par le relecteur ; aucun .NET, Git, service, SSH ou appel API n'a été exécuté par lui.

## Conclusion

Aucun défaut bloquant ouvert dans le snapshot gelé ci-dessous. La boucle autorise cinq tentatives au total, avec quatre pauses de 1, 2, 4 et 8 secondes. Un seul token lié porte l'échéance globale, incluant attente du sémaphore, HTTP, lecture de la réponse et pauses. Le token est revérifié après conversion et validation avant de déclarer le succès. L'annulation reste une annulation, et l'épuisement des reprises reste un échec.

La conversion ouvre un seul handle de fichier et conserve ce handle entre tentatives. Chaque tentative recrée requête, multipart et contenu PDF ; la sérialisation repositionne le handle à zéro. La destruction d'un contenu de tentative ne ferme pas le handle détenu par l'opération. Le partage de suppression permet le remplacement atomique du chemin sans que la tentative suivante ne bascule sur le nouveau document. Le slot de conversion est rendu dans le `finally`, après succès, échec ou annulation.

La classification ne reprend que les exceptions réseau sans code HTTP, les réponses 503/504 et les réponses 502 dont l'objet JSON contient exactement un discriminateur `detail` de type chaîne égal à `Docling upstream conversion failed.`. Les doublons, mauvais types, valeurs voisines, JSON invalide et autres codes HTTP restent terminaux. La désérialisation, le statut Docling, les erreurs de conversion, le schéma et les métadonnées d'exécution sont encore validés avant succès.

Les nouvelles sondes n'ajoutent ni corps, URI, chemin source ni message/exception interne. Elles conservent compteurs, statut HTTP, délai, raison contrôlée et durée. `BackendDiagnostics.Fail` ne journalise que le type d'exception. L'erreur réseau finale est reconstruite sans exception interne. Le logger est désormais réellement injecté par la DI.

## Points trouvés et fermés pendant la revue

1. **Logger DI absent** : la construction du singleton ne transmettait pas `ILogger<DoclingClient>`, ce qui aurait laissé les journaux structurés dans `NullLogger`. Injection explicite ajoutée.
2. **Succès après annulation pendant la validation synchrone** : un contrôle du token a été ajouté après `ConvertAttemptAsync` et avant les deux `Complete` de succès.
3. **Preuves unitaires incomplètes** : ajout de témoins conservant le document original après remplacement atomique, vérifiant la libération du slot après annulation et échec réseau terminal, l'expurgation des erreurs/sondes et le refus de quatre formes 502 supplémentaires.

## Preuves et limites

Le RED central antérieur est conservé : `tests/docling-transient-retry-red-v1/manifest.json`, run `2696658f65ad40a18b2afbd39896f791`, 8 échecs / 12 succès, source stable. Il précède les six témoins supplémentaires ; il ne prouve donc pas leur exécution.

Le snapshot final contient 26 cas attendus dans le filtre ciblé. Le relecteur n'a exécuté aucun de ces cas. Leur GREEN central, puis l'image Linux reconstruite et sa qualification effective restent à fournir par l'opérateur principal. Une revue favorable n'est pas une validation du runtime ni une approbation produit.

La borne temporelle repose sur l'annulation coopérative de .NET : la désérialisation synchrone ne peut pas être interrompue au milieu par ce correctif, mais ne peut plus déclarer un succès si le token a été annulé avant son retour. Les remplacements atomiques du chemin sont couverts par la stratégie de handle ; une mutation en place du même inode n'est pas une capture immuable du document.

## SHA-256 du snapshot relu

| Fichier | SHA-256 |
|---|---|
| backend/SAAIA.Backend/DocumentIntelligence/Docling/DoclingClient.cs | 987432567179b8a9d16eb55c9cc622137d481251599da93f060748749f142dee |
| backend/SAAIA.Backend/DocumentIntelligence/Docling/DoclingRetryPolicy.cs | 948260eca9a832d1e8cf7dd6b393ae0735c6ce9a338ac4438688732b16e061ec |
| backend/SAAIA.Backend/Extensions/ServiceCollectionExtensions.cs | da48f05abd161bfcb4978020b7c8549a9b9a8448e532fc9f97e46fcf5d0691a1 |
| backend/SAAIA.Backend.Tests/DoclingClientTests.TransientRetry.cs | b3674b15ab43834bdf7f9cb1c0379da33604c4d486a00839aba1b83f98647950 |
