# Corrections natives/OCR — état au 26 octobre 2026

Ce document complète [l'audit initial](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/artifacts/ingestion-ocr-audit-20261025/ocr-native-audit.md>). Il distingue les corrections de code, les tests réellement exécutés par l'agent principal et les capacités moteur encore non qualifiées. Aucune API payante, aucun serveur, build ou test .NET n'a été lancé par l'agent chargé de ces corrections ; les campagnes sont orchestrées séparément par l'agent principal.

## Résultats disponibles

- **RED initial : 24 cas exécutés, 24 échecs attendus**, avant modification produit : [manifest OCR RED](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/artifacts/ingestion-ocr-audit-20261025/tests/ocr-fidelity-red/manifest.json>), run `465262d2439446f38a138706f5484e6d`.
- **GREEN ciblé : les 32 cas des classes `PdfOcrSourceFidelityTests` et `OcrObservationProvenanceTests` passent**, sources stables pendant le run `3f217033255e4436b9367b9c6f8d1da9` : [manifest mixte](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/artifacts/ingestion-ocr-audit-20261025/tests/fidelity-first-build-v2/manifest.json>). La campagne entière a 65 PASS et 14 échecs alors attendus dans les autres corrections Tei/C10 ; elle n'est pas présentée comme une campagne entièrement verte. Les huit cas ajoutés après le RED initial ne sont pas revendiqués comme ayant chacun reçu une capture RED indépendante.
- **RED stockage supplémentaire : trois défauts et deux contre-contrôles**, run `89de8830baba41f5b65a4b8edb8888a4` : [manifest SQL RED](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/artifacts/ingestion-ocr-audit-20261025/tests/postgres-text-red-and-tei-green/manifest.json>). Glyphes inconnus, littéraux d'encodage et clés JSON étaient encore réinterprétés par un deuxième nettoyage. La correction est maintenant posée ; son GREEN et la suite générale restent à rattacher à leurs preuves finales.
- **Pas de qualification réelle OCRmyPDF/Tesseract à ce stade.** Le test nécessitant ces moteurs marque désormais une absence de prérequis comme SKIP explicite, jamais comme PASS sans exécution.

## Ce qui change

### Conservation entre moteurs et entre pages — OCR-01, OCR-08, OCR-09

Un OCR image repart du résultat complet déjà acquis lorsqu'il existe. Il ne repart plus du natif vide pour ensuite remplacer toutes les pages par son seul succès partiel. Les tests vérifient trois pages, dont deux non retentées avec succès par le deuxième moteur.

L'admission d'un résultat complet exige les mêmes pages physiques et la conservation littérale du texte natif existant, avec ses bornes de mots. Un volume de mots supérieur, une meilleure densité ou un mode `force` ne permettent plus d'effacer une page ou d'insérer une négation dans une phrase native en la présentant comme sa simple amélioration. Un résultat rejeté n'est pas déclaré appliqué ; la branche image peut conserver des variantes supplémentaires.

Lorsqu'il est présent, le **PDF de sortie OCRmyPDF**, déjà produit par le moteur, devient la source principale de pagination et de texte de ce candidat. Le sidecar n'est plus supposé contenir à lui seul toute la couche native d'un document mixte. Si ses pages ne correspondent pas, il n'est pas associé arbitrairement aux pages du PDF de sortie ; le signal `ocr_sidecar_page_mapping_unverified` le rend explicite. Le test correspondant utilise des extractions contrôlées ; il ne remplace pas un vrai essai moteur.

Un seul formfeed terminal est traité comme terminateur du sidecar ; des pages vides supplémentaires sont conservées. La qualité sidecar est calculée sur son texte et ses caractères de remplacement, au lieu d'un simple comptage.

Une exception tardive conserve maintenant les pages déjà reconnues, marque la page en erreur et les pages non tentées, puis retourne une extraction partielle avec ses diagnostics. Les annulations continuent à être propagées. Ce chemin d'exception est corrigé statiquement ; un témoin de fichier réellement illisible pendant un parcours moteur demeure à exécuter.

### Faits, valeurs, négations et texte court — OCR-02 à OCR-05

Le nouveau `OcrSourceReconciler` n'utilise plus les seuils de similarité, distances d'édition, listes anglaises de commandes ou seuils de longueur pour éliminer une observation différente. Les valeurs `10`/`20`, `+5`/`-5`, `1.5`/`15`, `1 5`/`15`, les négations et les statuts courts restent distincts. Seules les répétitions de lignes littéralement identiques après normalisation des suites d'espaces sont dédupliquées ; les frontières des mots restent significatives (`not able` et `notable` diffèrent).

Une confiance basse dans le TSV ne supprime plus une ligne entière. Le texte brut, les mots et les confiances sont conservés comme observations. Les confusions reconnues par un moteur ne sont pas corrigées automatiquement en dictionnaire. Une variante incertaine peut donc rester visible au lieu d'être remplacée par ce que le code suppose être le bon mot.

`PdfTextSanitizer.ForStorage` ne devine plus les références TEC/IEG/NEPA/DOF, les mots fusionnés, les mots français, les glyphes inconnus ou les réencodages Unicode. Les littéraux qui décrivent eux-mêmes un problème d'encodage restent des littéraux. Les hyphens explicites sont conservés ; le caractère Unicode explicitement discrétionnaire U+00AD garde sa normalisation de mise en page. Les variables représentées par des lettres espacées ne sont plus supprimées par un filtre natif avant projection.

La deuxième réécriture découverte dans `PostgresTextSanitizer.Clean` a également été retirée pour les valeurs, tableaux et chaînes/clés JSON. La contrainte technique PostgreSQL sur le caractère NUL reste traitée ; le PDF source immuable et le bundle canonique gardent leur rôle de référence brute. Les autres appelants de l'utilitaire général `TextEncodingSanitizer.RepairCommonMojibake` conservent leur comportement antérieur : il n'a pas été changé globalement.

Le payload Qdrant utilise maintenant le libellé `source_transport_v3`, raccordé par l'agent principal, pour ne plus prétendre avoir exécuté une réparation lexicale. Les tests d'anciens comportements destructifs ont été réécrits autour du contrat de conservation ; les témoins de vrais doublons et les contrôles de bruit restent distincts.

### Provenance conservée dans le modèle existant — OCR-07

Une fusion n'écrase plus `RawText` natif par son texte final. Les observations OCR sont transportées par `ExtractedPdfPage.OcrObservations`, puis projetées dans les **blocs/spans/mots du bundle canonique existant**, sans deuxième stockage durable concurrent.

Pour chaque observation disponible : texte brut, moteur nommé, langue, TSV brut, mots et confiances observées, dimensions raster et bornes pixels sont conservés. Les versions moteur non mesurées sont nommées `not_attested`. Les bornes raster ne sont pas transformées en faux polygones PDF : leur correspondance géométrique reste explicitement non vérifiée. Les mots conservent une confiance nulle si le TSV ne la fournit pas correctement.

Les observations sont exclues des associations de section du projecteur legacy. Elles portent les marqueurs d'ancrage de page et de relation non affirmée. Leur texte `Retrieval` est vide pour éviter une deuxième indexation de la même observation sélectionnée dans le rendu de page.

**Limite importante :** une observation alternative archivée avec `Retrieval` vide n'est pas automatiquement recherchable comme une deuxième preuve autonome. L'archive de provenance ne garantit ni sa sélection ni sa présence dans l'index. Une divergence réelle entre sidecar et PDF de sortie devra être qualifiée avec un moteur réel et une politique de sélection explicite. Aucune affirmation d'ordre universel ou de correspondance exacte raster→PDF n'est faite.

Les textes et TSV ne sont pas ajoutés aux logs courants ou aux sondes publiques ; ils sont réservés au bundle canonique documentaire privé. Les diagnostics usuels continuent à porter des codes et comptages.

### Diagnostics et limites de processus — OCR-06 et risques certains

Une page tentée mais échouée ne produit plus une couverture agrégée `complete`. Les échecs/timeout individuels remontent dans le diagnostic global. La campagne distingue couverture partielle en erreur, budget et tentative complète sans texte reconnu.

La qualité de la couche native et l'état des zones OCR sont séparés : un corps lisible reste `TextStatus=ok`, mais une zone image échouée ou non évaluée peut rendre la revue recommandée et le statut `page_partial_ocr_coverage`. Le score numérique existant est nommé **heuristic_indexability**, pas probabilité OCR calibrée. Les métadonnées sont raccordées aux projections SQL et à l'endpoint qualité par les deux agents coordonnés.

Le nouveau `OcrProcessRunner` partage la gestion des deux moteurs et de la découverte des langues : flux drainés concurremment avec mémoire capturée bornée, délai de processus, tentative de fermeture de l'arbre, délai indépendant de drainage et observation des fautes. Les sorties texte/TSV sont bornées à 64 MiB et refusées explicitement au-delà. Les lectures ne sont pas tronquées silencieusement pour produire une preuve partielle indétectable. Le test de lecteur vérifie la consommation complète d'un flux de 100000 caractères sans en garder plus de 8192.

Le budget par page s'applique au rendu plus à la reconnaissance ; il ne redémarre plus intégralement pour le deuxième processus. Le budget global reste distinct. Une annulation ou un délai ne prouve pas à eux seuls qu'un outil arbitraire et ses descendants ont été tués : un contrôle réel de processus auxiliaires reste nécessaire avant qualification moteur.

La langue dominante sert à prioriser, sans réduire une liste explicite à cette langue plus l'anglais. Le mode automatique reste un choix borné par la configuration et les paquets disponibles ; il ne garantit pas la reconnaissance de toutes les langues minoritaires d'un document. Les tests multilingues de moteur restent à faire.

## Structure du code

Le fichier principal OCR passe de **2404 à environ 1325 lignes**, avec responsabilités extraites plutôt qu'une dispersion en `partial` :

- `OcrSourceReconciler` : sélection conservatrice et réconciliation des observations.
- `OcrObservationParser` : TSV et confiance observée.
- `OcrProcessRunner` : processus, sorties et délais bornés.
- `PdfExtractionModels` : contrats d'extraction/diagnostic déplacés sans rupture des constructeurs.
- `LegacyOcrObservationProjector` : provenance vers le canonique existant.

`PdfTextSanitizer` devient une petite normalisation de transport. Les branches de remplacement par pourcentage, les listes de commandes, la comparaison approchée et leur machinerie désormais sans référence ont été retirées. Les points d'entrée internes historiques de nettoyage encore appelés restent non destructifs et commentés comme tels.

## Ce qui reste nécessaire avant validation produit

1. Rattacher le GREEN SQL supplémentaire et la suite générale finale à leurs manifestes, hashes de sources et binaires. Les nouveaux résultats ne doivent pas être additionnés comme des cas uniques à ceux déjà rejoués.
2. Exécuter le vrai chemin OCRmyPDF/Tesseract dans un environnement explicitement provisionné : natif, scanné et mixte ; erreurs partielles ; pages vides ; colonnes et rotations ; langues minoritaires ; signes/valeurs/négations ; sorties volumineuses et annulation.
3. Contrôler le worker jusqu'aux textes réellement publiés et retrouvés, leurs hashes, leur page et leur provenance. La conservation d'une observation dans le bundle ne suffit pas à valider le résultat servi au modèle.
4. Vérifier la présentation des états partiels en interface et la politique de preuve des observations incertaines. Un moteur configuré, un test ignoré ou une confiance élevée ne sont pas une qualification.

Le legacy OCR reste **NON QUALIFIÉ en conditions moteur réelles** dans cet état. Les corrections causales et leurs contrôles Unit ne doivent pas être présentés comme une validation complète de l'ingestion ou du produit.
