# Contre-revue du probe de remplacement invalide

Mode : lecture seule des sources gelées et de la preuve de tests préparée par
l'agent propriétaire. Aucun test, service, API, SQL, SSH ou build exécuté par ce
contre-relecteur. Aucun nouveau faux PASS matériel établi sur ce snapshot.

Sources relues :

| Fichier | SHA256 |
|---|---|
| invoke-linux-failed-replacement.py | 0ac15d5803f6d533d6af130b52dd9093c67941f29e6b4dd48abd01e92cbcadda |
| linux_failed_replacement_checks.py | a7a8239b9a76ef56646178327a9b0b8f05f126b3fcd6a30a8918041b6ca1d75c |
| test_linux_failed_replacement.py | 6022b93064558a22dd653db3ee60d6902094063abe4fcd8eb47be8478876c1ee |
| README-linux-failed-replacement.md | d11699cf866e3201f64a83e832175baf2e3b74d0a1dbba115a9d5d6ad8765e51 |

La garde exige un vrai PASS corpus et lifecycle du même owner, source et
namespace. La configuration et la DLL courantes sont liées aux deux preuves
précédentes. Le baseline final, y compris l'écart publication 4 / génération
abandonnée 5 lorsqu'il existe, est comparé à l'état actuel et aux vrais vecteurs.

Le seul fichier mutable est la copie synthétique connue. La source originale
hashée est séparée. Le risque de copie de travail partiellement écrite signalé
en première relecture est fermé par la préparation complète d'un fichier frère
unique puis remplacement atomique ; une erreur de préparation ne tronque plus
la copie courante. Des octets inattendus ne sont pas écrasés silencieusement.

Le probe exige un terminal failed borné avec zéro job actif, observe sa stabilité
et compare les anciens chunks, révision, plan actif et vecteurs/payloads
complets. Il ne cherche pas à présenter l'ancienne source comme fraîche pendant
que la copie physique est invalide. Après restauration, une nouvelle génération
doit réellement être publiée et repasser recherche→lecture courante. Une
récupération de cleanup ne transforme pas l'échec initial du probe en succès.

Le cleanup réutilise les seuls job IDs admis par le runner et le docPath connu,
restaure les octets originaux, puis arrête les cinq CID possédés sans suppression
de base, volume ou preuve. Les autres publications et leurs vecteurs sont
comparés. La Production unsigned reste explicitement non qualifiée.

Preuve enregistrée relue :
`tests/linux-failed-replacement-offline-v1/manifest.json` — 65 PASS, 0 échec,
0 erreur, 0 skip, `sourceChangedDuringRun=false`, dont 14 nouveaux tests. Ces
tests utilisent de faux transports et fichiers temporaires synthétiques ; ils
ne prouvent pas encore un échec réel de moteur ni la récupération du worker.

Limites à conserver dans le rapport réel : l'absence de retry est observée sur
une fenêtre bornée, pas pour toujours ; la limite d'essais du probe est 3 ; la
restauration prouve le fichier connu et la publication suivante, pas un rollback
magique d'une requête moteur distante. Les préflights refusés avant l'entrée
dans le try/finally nécessitent le stop owned indépendant de l'opérateur.
