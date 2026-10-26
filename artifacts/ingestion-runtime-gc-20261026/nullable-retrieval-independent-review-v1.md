# Contre-revue du diagnostic de retrieval nullable

Le premier scanner automatique réel a atteint une réponse source correcte, mais son harnais a rejeté le seul contrôle noDegradedRetrievers : il exigeait une liste vide présente. Cette preuve d'échec doit rester historique. Elle ne démontre pas une panne du moteur de recherche ni du produit.

Le contrat lu dans `contracts/SAAIA.Contracts/ApiContracts.cs` déclare `DegradedRetrievers` comme `List<string>?` et `DegradedRetrieverErrors` comme `Dictionary<string,string>?`. Une valeur null peut être omise par la sérialisation. Confondre absent/null avec une erreur provoquait le faux échec. À l'inverse, l'ancien prédicat générique `not value` de l'autre vérificateur admettait des types faux et vides : il ne constituait pas un contrat suffisamment strict.

Le helper partagé `retrieval_metrics_healthy` exige maintenant un objet metrics. Il accepte uniquement des noms de retrievers absents/null ou une vraie liste vide, et des erreurs absentes/null ou un vrai dictionnaire vide. Un diagnostic non vide, une liste/dictionnaire intervertis, un booléen, un nombre ou une chaîne ne devient pas sain. Le contrôle est utilisé par les faits positifs, les contrôles négatifs et la recherche après suppression. Les conditions d'identité source/révision/chunk/page, de fait exact et de lecture canonique sont conservées.

Le manifeste central `tests/automatic-ingestion-nullable-green-v1/manifest.json` rapporte **69/69 tests purs PASS**, zéro erreur/skip, avec transports simulés et aucun runtime qualifié par ces tests. Ses 21 sources ont les mêmes empreintes avant/après et correspondent aux octets relus. Le RED causal distinct est conservé par le propriétaire ; cette contre-revue n'a relancé aucun test ni service.

Empreintes finales : AUTO `87a019d8d806d3d98336c51263fbc1dbeee07a3dac0d11daab8b9621067aef7c`, canonical_search_checks `4aa3ba0ae906fefcaad7bf5fe1965f03bda96795f1157e5863ab89f0722f6266`, linux_retrieval_fact_checks `e04b7577cf6801e2113de2a268233acafe1b296d829b54e4c973bd7906a12090`.

Aucun défaut matériel n'a été trouvé dans ce delta. Le nouveau scanner réel, watcher et defaults doivent fournir leurs propres résultats : ce correctif du harnais ne préjuge pas de leur réussite. L'opérateur `run-production-sequential-v2.py` épingle les trois fichiers et conserve la v1 intacte. Ses neuf contrôles purs passent séparément ; aucune exécution réelle, clé, service, .NET, SSH ou Git n'a été utilisé par le réviseur.
