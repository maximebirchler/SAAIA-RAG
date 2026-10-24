# Extraction du protocole natif — 24 octobre 2026

## Responsabilités obtenues

Le protocole natif ne dépend plus de la classe partielle `ToolAgentOrchestrator` pour construire ses messages, ses schémas ou lire les arguments JSON.

- `Client.Core/NativeRouting/NativeRouterResponseReader.cs` : lecture JSON typée, champs optionnels et validation mécanique d'une grille. Aucun état d'application ni reconnaissance d'intention.
- `Client.Core/NativeRouting/NativeRouterRouteNames.cs` : identifiants du protocole transmis entre le classificateur, les schémas et les constructeurs de plans.
- `WinUI/ToolAgent/NativeRouting/NativeRouterPrompts.cs` : construction des messages à partir de la langue et de la règle explicite reçues.
- `WinUI/ToolAgent/NativeRouting/NativeRouterToolCatalog.cs` : schémas, liste des routes annoncées, sélection des outils entre les étapes et contrôle des appels annoncés.

Les deux premiers propriétaires sont portables dans `SAAIA.Client.Core.NativeRouting`. Les deux autres restent dans `SAAIA.Client.WinUI.Services.ToolAgent.NativeRouting` : leurs types de protocole et la localisation appartiennent encore au client. Aucun propriétaire ne rappelle `ToolAgentOrchestrator`, ne reçoit une instance de l'orchestrateur ou ne lit `_mem`/`_settings`.

Les plans qui utilisent la mémoire et les réglages restent dans l'orchestrateur. Les trois fichiers concernés passent de 2 450 à 1 419 lignes physiques, soit **1 031 lignes sorties de l'agrégat**. Le plus gros des quatre nouveaux composants contient 581 lignes ; il conserve ensemble les schémas du protocole. Le grand fichier initial passe de 2 135 à 1 182 lignes.

## Préservation vérifiable

`semantic-preservation.json` couvre **59/59 membres originaux** :

- 32 membres de production déplacés vers leurs vrais propriétaires ;
- 17 membres restants dont tous les jetons sont inchangés ;
- 10 adaptateurs de tests retirés du produit.

Les corps des méthodes déplacées et les déclarations de constantes conservent leurs jetons. Toutes les valeurs littérales, y compris les messages et schémas, restent identiques. L'unique normalisation admise est le remplacement de deux appels `NormalizeLanguageCode` par `LocalizedStrings.NormalizeLanguage` : le premier était déjà un simple wrapper d'une ligne vers le second. La visibilité et les imports évoluent pour la nouvelle frontière d'assembly.

Le mapping d'origine et les trois copies sources antérieures permettent de reproduire ce contrôle avec `Program.cs` et `Extract.csproj`. `dotnet run --project artifacts/extract-native-routing-20261024/Extract.csproj -- <racine-du-repo>` ne modifie aucun fichier produit : il réécrit seulement la preuve JSON. L'égalité AST ne remplace pas les tests d'exécution.

`removed-test-adapters.json` recense les 10 anciens hooks et la recherche de références après migration : **846 fichiers C# du client et de ses tests examinés, aucune déclaration ni référence restante**, hors sorties bin/obj. Les tests appellent les vrais propriétaires. Les petites adaptations d'entrées nécessaires vivent dans `NativeRoutingContractFixtures.cs` du projet de tests.

## Résultats réellement exécutés

Campagne `targeted-tests-corrected/manifest.json` : compilation et tests **Release**, **10/10 ClientCore + 113/113 Client**, aucun échec ni cas ignoré. La compilation n'a produit aucun avertissement affiché. L'inventaire des sources est identique avant et après la campagne (`sourceChangedDuringRun=false`). Les manifestes contiennent les empreintes des DLL testées et des TRX ; les DLL ne sont pas à versionner.

Les 10 nouveaux tests portables du lecteur couvrent les types JSON, la cardinalité, les doublons, les caractères de contrôle et la présence/absence des axes. Les tests existants migrés couvrent les messages, schémas, contrats de références nommées et grilles. Un garde vérifie que l'ancien propriétaire ne conserve pas les méthodes déplacées.

Le premier essai, conservé sous `targeted-tests/`, a arrêté la compilation client sur des imports restés vers l'ancien namespace après le passage à Core ; ses 10 tests Core passaient déjà. Les imports ont été corrigés, puis la campagne complète ciblée ci-dessus est passée. Aucun résultat de cet essai initial n'est présenté comme une validation client réussie.

## Limites

Ce lot réduit une responsabilité cohérente de l'agrégat ; il ne solde pas à lui seul la concentration restante de `ToolAgentOrchestrator`. Il n'a modifié ni message métier, ni règle de routage, ni budget, ni le moteur `SourceBackedRag`. Les suites complètes client et la compilation de production finales sont pilotées par l'agent client après gel commun.

Aucun appel payant, envoi de corpus, accès au serveur ou commit. Les validateurs natifs gardent leur sémantique existante : cette extraction ne vaut pas approbation du produit ou du planning de repas.
