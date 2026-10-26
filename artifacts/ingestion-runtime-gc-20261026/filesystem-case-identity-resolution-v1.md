# Identité des chemins dans le cache et la déduplication

## Défaut causal

Le cache de hash, sa rétention et le planificateur de doublons utilisaient
`OrdinalIgnoreCase` sur toutes les plateformes. Sur Linux, `deep/a.pdf` et
`deep/A.PDF` peuvent pourtant être deux sources distinctes. Avec taille et
mtime identiques, le deuxième chemin pouvait reprendre le hash du premier.
Un troisième fichier réellement identique au premier, choisi comme canonique
plus court, pouvait alors entraîner la suppression de la publication du
deuxième malgré un contenu différent. Le planificateur ignorait aussi un vrai
doublon lorsque deux fichiers identiques ne différaient que par la casse.

La capture centrale `tests/filesystem-case-red-v1/manifest.json`, run
`02276533c32d4cbea74bcc59d0db5191`, a réellement compilé et exécuté les tests :
**4 échecs causaux, 8 témoins PASS et 1 skip Linux explicite**. Les inventaires
source avant/après et le manifeste indiquent `sourceChangedDuringRun=false`.
Les 4 échecs concernent le hash X/Y confondu, la rétention d'une autre casse,
la chaîne de suppression erronée à trois fichiers et le doublon réel ignoré.
Les 8 PASS incluent 7 cas de l'autre lot scanner et le contrôle Windows.

Pour ce RED sur Windows, un filesystem virtuel fournit des hashes distincts
aux chemins Linux. Les seams de plateforme et de hash ont d'abord conservé
strictement l'ancien comportement effectif. Cette capture démontre la chaîne
de décisions ; elle ne prétend pas avoir créé des fichiers réellement
case-distincts sur Windows.

## Correctif gelé

Une politique partagée distingue maintenant les chemins selon la plateforme :
`OrdinalIgnoreCase` sous Windows, `Ordinal` ailleurs. Le cache, `RetainOnly`,
le dictionnaire des doublons, le choix canonique et son test d'égalité utilisent
la même politique. Le groupe par hash conserve sa comparaison propre : une
empreinte SHA n'est pas un chemin.

La dépendance de hash injectable permet des tests causaux sans moteur ou
service ; le comportement normal continue de calculer le SHA256 des octets.
Aucun fichier GC, watermark ou publication n'est modifié dans ce lot.

Fichiers gelés :

- `backend/SAAIA.Backend/Ingestion/IngestionFileSystemPathIdentity.cs`
- `backend/SAAIA.Backend/Ingestion/IngestionFileContentHashCache.cs`
- `backend/SAAIA.Backend/Ingestion/IngestionDuplicateFilePlanner.cs`
- `backend/SAAIA.Backend.Tests/IngestionDuplicateFileCaseIdentityTests.cs`

## Qualification restante à cet ancrage

Le GREEN central après correctif n'est pas encore revendiqué dans ce document.
Le filtre dédié est `FullyQualifiedName~IngestionDuplicateFileCaseIdentityTests` ;
les anciens contrôles sont `FullyQualifiedName~IngestionDuplicateFilePlannerTests`.

Un test séparé crée réellement les trois fichiers sur Linux avec même
taille/mtime, calcule leurs hashes et vérifie cache, rétention et seuls vrais
doublons. Son attribut marque explicitement SKIP sur une autre plateforme.
Il doit encore être exécuté sur Linux avec les binaires de test compilés ;
le RED virtuel ne le remplace pas. Aucune exécution .NET, service, API ou Git
n'a été lancée par l'auteur de ce lot.
