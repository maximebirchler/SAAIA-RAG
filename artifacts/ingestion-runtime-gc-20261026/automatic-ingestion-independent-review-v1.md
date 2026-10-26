# Contre-revue du harnais automatique Linux

Le harnais est prêt pour une exécution possédée par le coordinateur. Cette revue ne qualifie aucun parcours réel : elle lit le code, les tests et le manifeste hors ligne, sans lancer Docker, service, HTTP, PostgreSQL, .NET ni Git, et sans modifier les sources du propriétaire.

Ancrage relu : `tests/automatic-ingestion-offline-v2/manifest.json`, **42/42 tests purs PASS, zéro skip**, transports simulés, aucun Docker/HTTP et `liveRuntimeQualified=false`. Les 21 fichiers d'entrée ont les mêmes tailles et SHA avant/après ; leurs octets actuels correspondent à cet inventaire. Le runner est `a7327fe78e35a62d42b8ff7b08a23b95af8e4f60e159b0a8dbf4224e7c233130`, le helper `05d02d34e4e635363d551992a6d9023cf76eefd543efab167b6d938570af1f8b`. Les trois profils fonctionnels simulés sont des contrôles du protocole, pas des observations serveur.

## Gardes et prétentions vérifiées

Le runner ne construit ni ne démarre les conteneurs. Son mode par défaut n'accède pas à la clé, aux PDF ou à la configuration privée. L'exécution exige un owner de namespace exact, les cinq CID/images/labels/montages/réseaux possédés, le backend final identifié par SHA et un profil TEI réellement lié à ses poids, tokenizer, commande et `/info`. La configuration canonique et les trois SHA source distincts sont recontrôlés. Le cache/modèle n'est pas remplacé par une équivalence supposée.

Les seules mutations de parcours sont les trois fichiers PDF connus, préparés intégralement puis remplacés atomiquement ou retirés après contrôle de leur SHA. Les originaux sont préservés. Les observations SQL utilisent des SELECT statiques dans une transaction READ ONLY. L'allowlist HTTP ne contient aucun endpoint d'enqueue ; l'annulation admin est limitée au cleanup et aux jobs des trois chemins synthétiques.

Scanner seul, watcher seul et combiné ont des préconditions séparées. Le combiné exige un fichier réellement antérieur au boot ; les autres commencent avec une base et une racine vides. Les mises à jour successives évitent la présence simultanée de deux copies identiques qui produirait un résultat de déduplication au lieu d'un test de publication. Le scanner doit traiter les sources au-delà du lot de limite 1. Le watcher seul doit préserver publication et vecteurs au statut missing, sans prétendre qu'il réalise la suppression finale du scanner.

Une publication exige les faits annotés dans les objets canoniques et les chunks, leurs SHA UTF8 et identités source/révision/version, un manifeste scellé et ses vrais points/vector768, puis une recherche littérale suivie d'une lecture du chunk courant. Les publications et les octets complets des vecteurs/payloads des témoins B/C doivent rester inchangés pendant les mutations de A. Les suppressions scanner exigent indexed0/planNULL, recherche vide non dégradée et ancienne génération collectée. La recréation doit avancer version, révision, plan et IDs physiques.

Les deux workers ne sont PASS qu'avec un même instantané SQL montrant deux jobs running, des baux cohérents et les consommateurs w0/w1 distincts. Le profil combiné échoue si ce témoin manque ; configurer WorkerConcurrency=2 ou observer deux jobs à des instants différents ne suffit pas. Cela ne prouve aucune exécution simultanée de deux moteurs OCR.

Le cleanup ne dépend pas de la validité finale des PDF. Il limite les annulations aux jobs possédés, arrête le backend producteur avant l'ultime SELECT des jobs actifs, puis arrête les cinq CID exacts et vérifie les bindings de ports. Un défaut de preuve zéro job ou d'arrêt empêche le PASS. Les volumes et modèles sont conservés. Les délais de fermeture sont distincts du budget de qualification ; le rapport ne présente pas leur somme comme une stricte durée universelle de 1800 secondes.

## Deux observations de revue corrigées avant gel

La première garde des overrides d'environnement était sensible à la casse. Elle aurait accepté `ingestion__scannerenabled` ou `Ingestion__workerconcurrency`, contrairement au binding de configuration .NET. Le propriétaire a ajouté la comparaison `casefold` après normalisation `__` vers `:` et les témoins de ces variantes, y compris `INGESTION:WORKERENABLED`. La fermeture est vérifiée dans le code et les tests du snapshot final.

La variante de retour après admission d'un delete pouvait laisser croire que l'état postterminal prouvait une disponibilité continue. Elle expose maintenant `terminalPublishedGenerationPreserved=true` et `continuousPublicationAvailabilityProven=false`. Le README distingue explicitement les états échantillonnés de l'atomicité transactionnelle prouvée par les tests PostgreSQL du produit. Si les timestamps ne prouvent pas le retour avant exécution, la variante reste NON_EXECUTED ; le harnais ne convertit pas une course manquée en PASS.

Aucun autre défaut matériel n'a été établi dans cette contre-revue. Cela ne garantit ni la durée réelle des trois profils, ni la capture effective d'une concurrence ou d'une course ; ces résultats appartiennent aux campagnes serveur à venir.

## Limites d'acceptation

Les timings accélérés qualifient ce protocole sur ce runtime, pas les délais par défaut de toutes les installations. Une fenêtre calme de six secondes ne prouve pas l'absence de toute admission future. Les contrôles ponctuels ne prouvent pas tous les états entre deux observations, ni toutes les reformulations d'une recherche, ni une disponibilité sous panne de filesystem. Les échecs bornés de capture de concurrence restent des échecs de qualification, pas un motif de les renommer PASS.

Le mode Development non signé demeure inéligible à la validation Production signée, même en cas de réussite fonctionnelle. Aucun test du planning ou appel payant n'est exécuté par ce harnais. La preuve machine de cette revue est `automatic-ingestion-independent-review-v1.json` ; les résultats réels futurs doivent rester des artefacts distincts.
