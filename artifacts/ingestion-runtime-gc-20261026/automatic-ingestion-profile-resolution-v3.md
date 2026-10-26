# Gardes effectives des profils AUTO et qualification Production

Ce delta est préparé avant toute exécution réelle AUTO. Aucune clé de signature,
clé fournisseur ou valeur de configuration privée n’a été lue par l’auteur.
Aucun Docker, service, HTTP réel, SSH, .NET, Git ou LLM n’a été exécuté.

## Cause, RED et GREEN

Le fichier de configuration était strictement vérifié, mais les observations
runtime pouvaient encore accepter un job watcher en profil scanner seul, ou un
job scanner en profil watcher seul. La disponibilité canonique dans `/ready`
n’exigeait pas non plus que la concurrence effectivement liée soit égale à 2.

La capture `tests/automatic-ingestion-profile-red-v1/manifest.json` porte
47 tests : 3 échecs causaux attendus, 44 PASS, 0 erreur, 0 skip. Les trois échecs
sont les deux provenances inversées et un `/ready` déclarant un seul worker.
L’inventaire de 21 fichiers est stable pendant la capture. L’extraction de la
garde de readiness en fonction pure conserve d’abord le comportement précédent ;
ces RED ne sont donc pas des échecs de compilation ou de fixture.

Après correction, `tests/automatic-ingestion-profile-green-v1/manifest.json`
porte 49/49 PASS, 0 échec, 0 erreur, 0 skip et 21 fichiers identiques avant/après.
Les deux cas supplémentaires vérifient les sources scanner de suppression/retour
et la comparaison indépendante des identités image/DLL. Les témoins readiness
refusent aussi une valeur absente, booléenne, chaîne ou flottante. Ces preuves
restent exclusivement des tests purs, avec transports et observations simulés.

## Gardes ajoutées

- Chaque snapshot fonctionnel refuse les sources de jobs incompatibles avec le
  profil : watcher exclu en scanner seul ; scanner, scanner_missing et
  scanner_source_returned exclus en watcher seul. Le combiné conserve les quatre
  provenances automatiques connues. L’attribution de publication réapplique ce
  contrôle. Le cleanup reste fondé sur le scope possédé, afin qu’un déclencheur
  automatique inattendu ne bloque pas l’annulation des seuls jobs sandbox.
- La sonde `/ready` doit exposer l’entier exact
  `details.ingestion_worker_concurrency=2`. Cette mesure de liaison est conservée
  dans le résumé public. Elle ne remplace pas la preuve de deux leases running
  w0/w1 simultanées, toujours exigée par le verdict global du profil combiné.
- Le SHA de DLL Linux et l’ID de configuration d’image Docker sont deux identités
  distinctes, comparées séparément et validées au format SHA256 attendu.

## Contrat de l’entrée backend-identity

Deux champs sont requis : `assemblySha256`, chaîne de 64 hexadécimaux minuscules,
et `imageId`, chaîne `sha256:` suivie de 64 hexadécimaux minuscules. Les anciens
fichiers corpus contenant ces deux champs restent compatibles. L’opérateur
renseigne le premier depuis `sha256sum /app/SAAIA.Backend.dll` dans l’exact
conteneur backend, et le second depuis son champ Docker `Image`, relié à l’ID
d’image inspectée. Un tag ou un RepoDigest ne remplace pas cet ID. Le runner
recontrôle ensuite les valeurs sur le CID possédé avant de lire la clé bootstrap.
Les métadonnées optionnelles d’un ancien PASS ne remplacent pas ces comparaisons.
Le hash de DLL Windows n’est jamais utilisé comme identité de la DLL Linux.

## Production et limites conservées

Le runner appelle toujours la garde commune `corpus.qualification_identity`.
Pour `owner.qualificationMode=production`, elle exige l’environnement Production
réellement observé et `config_signature_verified=true` dans `/ready`. Elle ne
charge pas de clé privée et ne modifie pas le mode du backend. Un test positif
de Production signé et un contre-témoin Development non signé sont verts.
Development garde son inéligibilité et ne devient pas Production par un résumé.

Les limites du scénario restent celles du README : trois sources distinctes,
timings accélérés explicités, faits physiques et publications actuelles vérifiés,
recherche→lecture, vecteurs témoins préservés, fenêtre de concurrence réellement
observée, course conditionnelle NON_EXECUTED si manquée, et cleanup des cinq CID
exactement possédés. L’observation échantillonnée ne prétend pas tracer chaque
instant de disponibilité entre deux SELECT. Aucun runner corpus/lifecycle gelé
ni aucune source produit C# n’est modifié par ce delta.
