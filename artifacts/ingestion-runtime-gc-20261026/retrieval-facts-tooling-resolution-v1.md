# Probe des 106 faits — implémentation et tests purs

Statut : nouvel outillage prêt et gelé pour contre-revue/exécution par
l'opérateur. Aucun service, API, SQL, SSH, build ou fournisseur payant exécuté
par le préparateur. Les runners corpus/lifecycle/replacement existants restent
inchangés.

Fichiers ajoutés :

- `tools/validation-ingestion/invoke-linux-retrieval-facts.py` : runner NOEXEC,
  recherche/lecture uniquement, aucun enqueue/cancel ni canonical seed.
- `tools/validation-ingestion/linux_retrieval_fact_checks.py` : gardes pures,
  baseline courante, bundle/UTF8/source/page, résultats et contre-témoins.
- `tools/validation-ingestion/README-linux-retrieval-facts.md` : commande et
  limites d'exécution ; même deadline UTC pour les deux namespaces.
- `tools/ingestion_gold/tests/test_linux_retrieval_facts.py` : 16 tests nouveaux.

Chaque namespace doit présenter un PASS réel du corpus complet et du même
owner/manifeste. La configuration montée et la DLL réellement chargée restent
identiques à ce PASS. Les sources originales et copies de travail sont hashées
avant/après. Les publications **courantes** peuvent avoir avancé après les
scénarios lifecycle/recovery ; plusieurs jobs historiques ne sont pas assimilés
à une contamination. Zéro job actif est exigé.

Avant toute recherche, les 106 annotations sont confrontées à leurs pages dans
les bundles canoniques courants, liés par sourceHash/docId/revisionId et checksum.
Les chunks SQL doivent eux aussi conserver leur checksum UTF8 exact. Le probe
ne déduit pas une présence physique du texte renvoyé par la recherche seule.

Il recherche chaque annotation puis lit un hit courant, vérifie toutes ses
identités et les contenus retournés. Une lecture déjà effectuée du même tuple
doc/révision/chunk/sourceHash est réutilisable pendant l'observation stable ;
les annotations suivantes restent toutes recherchées. Les deux contre-témoins
par document distinguent document absent et voisins denses d'un nonce absent.
Les métriques doivent déclarer explicitement l'absence de retriever dégradé.

Le plafond est de 204 appels Gold +34 recette =238, sans retry. Il ne contient
aucun GET /ready caché : la provenance est liée au PASS précédent et aux CID/
images/configuration/DLL inspectés. Une échéance absolue partagée, au plus
15 minutes dans le futur, empêche d'ouvrir une nouvelle fenêtre pour le second
namespace. Le cleanup de sécurité peut dépasser la fenêtre de recherche, sans
aucun nouvel appel de recherche. Il observe zéro job actif sans annuler de job,
puis arrête exactement les cinq CID possédés, en conservant les données.

Capture pure neuve : `tests/linux-retrieval-facts-offline-v1/manifest.json`.
81 PASS, 0 échec/erreur/skip, 23 fichiers identiques avant/après,
`sourceChangedDuringRun=false` ; Python 3.12.14. Les 16 nouveaux tests s'ajoutent
aux 65 tests purs précédents. Les HTTP/SQL/vecteurs sont simulés : ce PASS ne
prouve pas encore 106 recherches réelles ou le runtime final.

Empreintes gelées :

| Fichier | SHA256 |
|---|---|
| invoke-linux-retrieval-facts.py | 85c68ef502c60a16d2b8d28c26c655fafa5dd3e38d20040c5b89da02be01d041 |
| linux_retrieval_fact_checks.py | 0db0d56b7be40d3caedf95fb97c3288a243cb776d7dc33888396bab636b7c999 |
| test_linux_retrieval_facts.py | c19784803814d56eaac11a39d5d891b5995d032dc6782530bfa647745410f4d3 |
| README-linux-retrieval-facts.md | 080a09f2a14094103e658e5a1d38699341b234ad1804637907d1fd82c51030f2 |

Le résultat public n'inclut ni requête brute, ni texte PDF, ni clé. Il conserve
factId, annotation hash, source/révision/plan/génération, page, rang, checks,
latences et compteurs. Les réponses privées permettent une analyse causale de
chaque échec. Les questions naturelles et l'abstention LLM ne sont pas évaluées
ici. Un runtime unsigned Development ne devient pas Production par ce probe.
