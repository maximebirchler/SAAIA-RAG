Revue finale du correctif de routage, réalisée en lecture seule après la suite complète Client/Core et le build natif 10. Aucun test supplémentaire, build, appel modèle ou changement de source n'a été réalisé pour cette revue.

Les deux gardes appliquent la même règle déjà présente dans le produit : une route venant du LLM, acceptée comme `chat.general`, sans outil et sans clarification demandée, conserve son parcours général. Le repli de recherche d'un élément précis et la sonde documentaire ne peuvent plus réinterpréter cette décision à partir des seuls mots de la question. La garde ne s'applique ni à `rag.answer`, ni à une route contenant un outil, ni au routeur local de secours. Le pipeline documentaire explicite reste exécuté avant ces replis dans `RunPipeline.cs`.

Le risque de bloquer une route documentaire légitime est donc borné par l'origine et le contenu de la décision acceptée. Si le routeur classe à tort une vraie question documentaire en conversation générale sans outil, ce correctif respecte cette classification ; l'évaluation de cette erreur sémantique reste une question de qualité du routeur. Il ne prétend pas rendre toute classification correcte. Les chemins déterministes avant le routeur et le repli spécialisé de traçabilité de version n'ont pas été modifiés sans reproduction causale applicable.

Les sondes sont cohérentes avec ce comportement : `source_backed_fallback.skipped` enregistre le motif `router_general_no_tools`, le point d'entrée et l'intention. Elle ne contient ni texte de la question, ni contenu documentaire. Elle décrit une exclusion de politique ; elle ne prétend pas qu'une recherche a eu lieu. Le helper est appelé par les deux branches concernées, et ces branches restent accessibles aux cas documentaires et aux replis locaux. Aucun code mort ajouté ou commentaire matériellement faux n'a été identifié dans ce delta.

Les preuves comportementales existantes distinguent précisément leurs limites :

- La phrase native « Bonjour, peux-tu afficher la réponse de contrôle du streaming ? » et « Comment vas-tu ? » traversent le véritable `RunAsync`, deux décisions natives du routeur, puis le writer en streaming. Les assertions vérifient les deux fragments reçus, le texte final, l'absence d'appel HTTP, de recherche et de transfert avancé. Les dépendances LLM/HTTP sont contrôlées ; ces tests ne constituent pas des observations de l'interface native.
- Le contrôle « Quelle est la valeur du code ORION ? » utilise une route documentaire explicite et atteint la frontière HTTP réelle du composant de recherche. Il s'interrompt volontairement à cette frontière. Il prouve la conservation du branchement documentaire, sans prétendre valider la qualité d'une réponse générée.
- La reproduction confirmée était rouge sur les deux questions générales, puis verte sur les trois cas. Les 163 régressions voisines incluent ces trois cas ; elles ne s'ajoutent pas à la couverture complète.

La vérification indépendante complète est conservée dans `full-core-client-build10-production-anchor.json`, produite par `verify-core-client-build10-production-anchor.ps1` :

| Périmètre | Exécution retenue | Résultat | Justification actuelle |
| --- | --- | --- | --- |
| Client.Core | Nouvelle suite complète | 80 PASS | TRX et DLL testées vérifiés ; sources avant/après et actuelles identiques |
| Client | Nouvelle suite complète | 2 396 PASS | TRX et DLL testées vérifiés ; sources avant/après et actuelles identiques |
| Backend | Suite complète antérieure | 2 430 PASS réutilisés | Périmètre source et dépendances inchangés ; DLL de test et produit identiques à celles enregistrées |
| Contracts | Suite complète antérieure | 20 PASS réutilisés | Même contrôle de sources et de DLL ; aucun nouveau lancement de ces tests |

Cela représente **2 476 tests réexécutés** et **2 450 résultats antérieurs conservés**, soit une couverture composée de **4 926 cas** sur les quatre projets. **Ce n'est pas une exécution unique de 4 926 tests.** Les 163 tests ciblés ne s'ajoutent pas à ce total.

Les 1 658 fichiers de l'inventaire complet sont toujours identiques à l'empreinte `9BC818B8741CB814190E3593CC3FB2AF3E44EFD8E73A5963A9A833B3B7AE02D5`. Les inventaires avant/après et leurs empreintes ont été recalculés, les deux TRX relus et leurs SHA-256 vérifiés. Le périmètre commun de réutilisation Backend/Contracts contient 623 fichiers et ne présente aucun ajout, retrait ou changement. Cette vérification reprend la sélection de sources de la campagne d'origine ; elle ne couvre pas rétroactivement les fichiers exclus de cet inventaire.

Le build de production 10 est réussi, avec 0 avertissement et 0 erreur en 21,90 secondes. Sa DLL WinUI native est `579E5F02D869E1780D86248B67EE762405D8F156452068F14BF7F306CDCBEB7F`. L'ancrage relie ses sources aux sources testées ; il ne confond pas les DLL AnyCPU de test avec la compilation native x64. Les preuves de lancement et d'observation WinUI 10 restent nécessaires séparément. Le backend de production correspond aussi à la DLL testée et au manifeste de la fixture actuelle.

Cette revue ne vaut pas acceptation Terra ou planning de repas. Le statut produit reste `TESTE_NON_APPROUVE` jusqu'à réunion des preuves d'acceptation requises.
