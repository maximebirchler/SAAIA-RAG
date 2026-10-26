# Remplacement par un PDF tronqué, puis reprise fraîche

`invoke-linux-failed-replacement.py` complète un **lifecycle Linux réellement
PASS**. Il réutilise `Lifecycle` sans modifier son runner ni réexécuter le
late-write/GC déjà qualifié. Il ne construit, installe, démarre ou joint rien
par SSH. Le défaut est **NOEXEC** : seules les preuves publiques d'entrée sont
lues, sans clé, Docker, PDF, HTTP ou SQL.

## Conditions d'exécution

L'opérateur redémarre les cinq CID exacts déjà possédés. Les mêmes owner,
configuration, DLL, collection et manifeste doivent correspondre au corpus
PASS **et** au lifecycle PASS. Les configurations externes ou les fournisseurs
LLM restent interdits. Le scope est uniquement le tenant synthétique
`77777777-7777-4777-8777-777777777777` et `fr-scan-critical-v2.pdf`.

La source originale et la copie de travail sont distinctes, sans symlink,
hashées par le manifeste. La dernière publication du lifecycle doit encore
correspondre exactement à SQL et aux points physiques. Le probe ne confond pas
`indexedVersion` avec `ingestionVersion` : la publication 4 conservée après
l'annulation 5 reste sa baseline ; le remplacement devra obtenir une génération
strictement supérieure à 5. Aucun numéro de génération n'est codé en dur.

## Contrôle réel préparé

1. Vérifier la publication précédente, sa recherche→lecture, ses chunks et
   checksums, ainsi que les vecteurs/payloads complets des autres documents.
2. Remplacer uniquement la copie de travail connue par les **16 premiers
   octets exacts** de l'original, sans objet complet ni xref. L'original reste
   intact. La préparation et le remplacement de fichier sont séparés pour
   éviter une copie de travail partiellement écrite.
3. Admettre un seul upsert par API et observer le vrai worker. Exiger un
   terminal `failed`, entre 1 et 3 tentatives, dans le délai borné. La
   publication précédente, ses chunks, sa révision, ses vecteurs et payloads
   doivent rester exactement identiques pendant les observations.
4. Observer encore ce terminal inchangé et zéro job actif pendant une fenêtre
   bornée (10 secondes par défaut). Une réussite, une annulation, une pause,
   un redémarrage ou un dépassement du délai fait échouer ce scénario.
5. Restaurer les octets exacts de l'original, réingérer par API et exiger une
   génération, révision, plan et des IDs physiques nouveaux. La recherche
   canonique avec contre-témoins puis la lecture doivent prouver cette
   publication fraîche. Vérifier que les autres publications SQL et leurs
   vecteurs/payloads n'ont pas changé.

Pendant la corruption, le probe ne réclame pas de recherche ou de lecture
canonique positive : le contrôle de hash de source peut légitimement les
refuser. Il prouve la conservation de la publication SQL et vectorielle sans
affirmer que la source courante est fraîche. Après restauration et nouvelle
publication, la recherche→lecture positive est obligatoire.

Le `finally` annule seulement les jobs admis par ce probe et sur ce document,
attend zéro job actif, restaure uniquement les octets connus, puis arrête les
cinq CID exacts. Si le scénario a été interrompu avant sa réingestion de
récupération, il tente celle-ci après le cleanup borné, sans transformer le
résultat du scénario échoué en PASS. Des octets inattendus de la copie de
travail ne sont jamais écrasés. Ni ligne SQL, volume, collection, image ou
preuve précédente n'est supprimé.

## Commande opérateur

Commencer sans `--execute`. Remplacer les noms illustratifs de preuves par les
chemins réels, sans renommer une ancienne preuve.

```bash
root=/home/maxime/saaia-ingestion-qualification/48e5eba2-ea16-41da-98a7-65472c7f6dd6
python3 -B tools/validation-ingestion/invoke-linux-failed-replacement.py \
  --owner "$root/owner.json" \
  --manifest "$root/corpus/manifest.json" \
  --corpus-proof "$root/evidence/CORPUS-PASS/execution-result.json" \
  --lifecycle-proof "$root/evidence/LIFECYCLE-PASS/lifecycle-result.json" \
  --source-directory "$root/corpus/sources" \
  --output "$root/evidence/failed-replacement-01" \
  --api-key-file "$root/private/backend-api-key.txt" \
  --maximum-wait-seconds 300 \
  --terminal-stability-seconds 10
```

Les captures privées gardent le wire, SQL, chunks et points complets. Le résumé
public contient les identités, empreintes et compteurs, sans clé, texte PDF,
vecteur ou payload documentaire. La stabilité du terminal est une observation
temporelle bornée, pas une preuve éternelle d'absence de retry. Les tests purs
simulent le protocole et ne qualifient pas le worker ou les moteurs réels. Un
PASS unsigned Development ne qualifie toujours pas Production.
