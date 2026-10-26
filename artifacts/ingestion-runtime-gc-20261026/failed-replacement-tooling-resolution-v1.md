# Probe séparé de remplacement défectueux : préparation et gel

État au 26 octobre 2026 : **outillage préparé et tests purs PASS ; aucune
exécution réelle par son auteur**. Ce document ne transforme pas l'ancien
`failedReplacement: NON_EXECUTED` du lifecycle en résultat exécuté. Une nouvelle
campagne doit écrire son propre `failed-replacement-result.json`.

## Périmètre et oracle

Le probe neuf `tools/validation-ingestion/invoke-linux-failed-replacement.py`
réutilise le composant `Lifecycle` gelé. Il ne modifie aucun produit, migration,
runner antérieur ou ancienne preuve. Il exige le corpus réel PASS et le
lifecycle réel PASS du même owner, manifeste, source, configuration et DLL.
Les cinq CID doivent être remis à disposition par l'opérateur ; le probe ne
les construit ou démarre jamais.

La baseline lie séparément la dernière génération publiée et la dernière
génération admise. Dans le cas réel rapporté par l'orchestrateur, cela conserve
publication 4 et ingestion 5 annulée, puis exige une nouvelle admission
strictement supérieure à 5. Les numéros ne sont pas codés en dur dans le
runner. La publication SQL, la révision, les chunks/checksums et les points
physiques complets doivent correspondre au dernier résultat du lifecycle.

Seule la copie de travail hashée du PDF synthétique français peut être
tronquée, aux 16 premiers octets exacts de son original. L'original reste
intact. Un fichier temporaire unique est entièrement préparé puis remplacé,
afin de ne pas laisser une écriture préparatoire partielle dans la copie de
travail. Aucun texte, vecteur, plan ou enregistrement canonique n'est injecté.

L'admission API doit aboutir à un vrai terminal `failed`, entre 1 et 3
tentatives, dans le délai borné. Ce terminal et zéro job actif doivent encore
rester stables pendant 10 secondes par défaut. Pendant la corruption, le
contrôle porte sur la conservation SQL/révision/chunks et l'identité exacte
des vecteurs/payloads : une recherche canonique positive n'est pas exigée
lorsque le fichier ne correspond plus à sa révision publiée.

Après restauration exacte, une nouvelle admission doit publier une génération,
révision, empreinte de plan et des IDs physiques nouveaux. La recherche avec
contre-témoins et la lecture doivent ensuite prouver cette publication
fraîche. Les publications SQL et points complets des autres documents sont
comparés avant et après.

## Cleanup et limites explicites

Le finally garde les contrôles de chemins/ownership, annule seulement les IDs
admis sur ce document, attend zéro job actif, restaure les octets connus et
arrête les cinq CID exacts. Des octets inconnus ne sont pas écrasés. Si le
probe a été interrompu avant sa récupération, une réingestion de récupération
peut être tentée après le cleanup borné ; son succès ne change jamais le
résultat échoué du scénario en PASS. Aucun SQL direct de mutation, suppression
de collection, volume, image ou preuve n'est autorisé.

La stabilité d'un terminal est une observation temporelle bornée, pas une
preuve éternelle d'absence de retry. Le late-write/GC déjà contrôlé n'est pas
rejoué. Un résultat unsigned Development reste inéligible à une qualification
Production. Les captures textuelles/vectorielles et le wire sont privés ; la
synthèse publique conserve compteurs et empreintes.

## Tests réellement exécutés hors ligne

`tests/linux-failed-replacement-offline-v1/manifest.json` enregistre **65 tests
purs PASS, zéro échec et zéro skip**, sur les 51 contrôles précédents et 14
nouveaux cas. Les 19 empreintes avant et après sont identiques. Aucun worker,
moteur, service, Docker, HTTP ou SQL réel n'a été lancé ; aucun fournisseur
payant n'a été appelé.

Les nouveaux cas couvrent notamment le refus d'un faux lifecycle PASS, le
scope/configuration de preuve, la distinction publication 4/ingestion 5,
l'échec 6 conservant la publication précédente, le refus d'un DONE/annulation/
pause ou d'un retry actif, les vecteurs absents/modifiés, la révision et les
chunks changés, la restauration exacte, le refus d'écraser des octets inconnus,
l'échec d'une préparation temporaire, NOEXEC sans clé/runtime, ainsi que les
chemins nominal et interrompu du driver avec récupération et cleanup simulés.

## Fichiers et commande

- `tools/validation-ingestion/invoke-linux-failed-replacement.py`
- `tools/validation-ingestion/linux_failed_replacement_checks.py`
- `tools/validation-ingestion/README-linux-failed-replacement.md`
- `tools/ingestion_gold/tests/test_linux_failed_replacement.py`

La commande opérateur complète et ses chemins illustratifs figurent dans le
README. Commencer par NOEXEC ; `--lifecycle-proof` est obligatoire en plus des
paramètres du corpus. Le délai par job vaut 300 secondes par défaut et la
stabilité terminale 10 secondes. Chaque campagne exige un répertoire neuf.

Empreintes de gel :

- Runner : `0ac15d5803f6d533d6af130b52dd9093c67941f29e6b4dd48abd01e92cbcadda`
- Contrôles : `a7a8239b9a76ef56646178327a9b0b8f05f126b3fcd6a30a8918041b6ca1d75c`
- README : `d11699cf866e3201f64a83e832175baf2e3b74d0a1dbba115a9d5d6ad8765e51`
- Tests : `6022b93064558a22dd653db3ee60d6902094063abe4fcd8eb47be8478876c1ee`
