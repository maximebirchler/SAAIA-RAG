# Validation reproductible et diagnostic des tests

PowerShell 7.4 ou plus récent et .NET 8 sont requis. Les suites WinUI demandent Windows. Le manifeste `config/validation-profiles.json` décrit les profils ; il n'active aucune campagne payante par défaut.

## Porte d'entrée commune

```powershell
# Backend + Contracts + noyau client portable + tests client WinUI.
pwsh -NoProfile -File tools/test-project.ps1 -Profile Unit

# Préparation uniquement : état prepared, aucun scénario annoncé comme exécuté.
pwsh -NoProfile -File tools/test-project.ps1 -Profile Unit -PlanOnly

# Variante portable depuis une session PowerShell (tableau transmis directement).
& ./tools/test-project.ps1 -Profile Unit -Projects @('Backend', 'Contracts', 'ClientCore')

# Sondes testées avec un serveur HTTP synthétique loopback, sans fournisseur.
pwsh -NoProfile -File tools/testing/Test-RuntimeProbes.ps1

# Taille des fichiers et dette agrégée des types partiels.
pwsh -NoProfile -File tools/check-architecture.ps1
```

`-NoRestore`, `-NoBuild`, `-Configuration Release`, `-Filter` et `-OutputPath` sont disponibles. Le manifeste indique explicitement si un build a été demandé ; `-NoBuild` n'établit pas à lui seul que les binaires correspondent aux sources. Chaque résultat conserve le SHA256 du TRX, de l'assembly de tests réellement référencée par ce TRX et des dépendances locales `SAAIA.*.dll`, capturés après exécution. Deux inventaires de sources et leurs empreintes signalent les modifications intervenues pendant la campagne ; ils ne reconstituent pas les entrées d'une compilation antérieure. Les rapports TRX sont conservés, avec nom, issue et durée de chaque test. Un filtre qui n'exécute aucun test échoue. Les variables live héritées sont neutralisées dans les processus `Unit` et `IntegrationPg`, puis restaurées.

Les tests xUnit v2 dépendant d'un profil utilisent `ProfileFact`/`ProfileTheory` et un `Trait("Profile", ...)`. En dehors du profil demandé, leur état est un vrai skip à la découverte. Dans le profil demandé, un prérequis manquant provoque un échec ; un retour normal ne peut plus être utilisé pour simuler un skip. Les méthodes unitaires placées historiquement dans des fichiers `Live*.cs` restent exécutées dans `Unit`.

## PostgreSQL réel et isolé

```powershell
pwsh -NoProfile -File tools/test-postgres-isolated.ps1 -Configuration Release
```

Le script utilise des binaires PostgreSQL 16 déjà installés ; `-PgBinPath` permet de les sélectionner. Il crée un cluster unique dans un dossier réservé de `%LOCALAPPDATA%`, avec secret aléatoire et port loopback éphémère. Il vérifie `data_directory` avant les tests, lance `IntegrationPg`, arrête son processus et vérifie le port. Le nettoyage récursif est limité au dossier portant son identifiant de propriété. `-KeepCluster` conserve le cluster pour une investigation. Aucun cluster existant ni corpus de production n'est utilisé.

Pour un cluster de test déjà isolé, `test-project.ps1 -Profile IntegrationPg` exige `SAAIA_TEST_PG_CONN` avec un unique hôte effectif `Host=localhost`, `Host=127.0.0.1` ou `Host=::1`. Les alias `Server`/`Data Source`, les listes d'hôtes et tout hôte effectif distant sont refusés avant les tests. Les valeurs citées de la chaîne sont parsées, sans copie dans le manifeste. Un serveur indisponible ou des migrations échouées font échouer les scénarios demandés.

## Tests réels du modèle

`LiveLocal` exige un filtre explicite et refuse les endpoints configurés hors loopback. Le test sélectionné exige encore ses drapeaux et fichiers de modèle ; le lanceur ne les devine pas et ne les active pas. Le modèle réellement retourné et le corpus effectif doivent être prouvés par les artefacts du scénario.

`LivePaid` est réservé aux campagnes explicitement gardées. Il exige `-CampaignProfilePath`, `-AuthorizePaid`, `-AuthorizeCorpusTransmission`, un `-PaidCapUsd` positif et `-ExecutePaidCampaign` pour exécuter. Le plafond du profil ne peut pas dépasser l'autorisation passée. Le runner de campagne conserve ses vérifications de coûts, registre, corpus, commit et compte fournisseur. Le lanceur commun ne prétend pas que son paramètre USD remplace ce registre. Aucun ancien profil n'est choisi implicitement ; RunPod reste bloqué tant qu'un runner gardé équivalent n'est pas enregistré dans cette porte d'entrée.

Les anciens tests génériques pouvant utiliser un endpoint externe sont classés conservativement `LivePaid`. Une attribution à ce profil ne signifie pas que le serveur du client facture chaque requête ; elle empêche le banc de supposer la gratuité d'un endpoint dont il ne connaît pas l'hébergement. Les suites explicitement locales restent distinctes.

## Mesures du harnais runtime

`runtime-ci-harness.ps1` appelle les mêmes suites hors ligne puis, si une URL loopback est fournie explicitement, teste génération, concurrence, reproductibilité et disponibilité du catalogue de modèles. `-StrictRuntime` rend l'absence de runtime bloquante.

Le TTFT correspond au premier delta de contenu non vide reçu pendant une réponse SSE. La durée complète est conservée séparément. Une réponse non streamée a un TTFT `unavailable`. Le débit est calculé seulement avec un usage de tokens de sortie réellement retourné et une durée observable ; sa définition inclut le transport. Sans usage, il reste `unavailable`. Zéro ou un échantillon, une mesure absente, NaN, une durée nulle, un échec HTTP ou un flux incomplet ne peuvent pas produire un succès de reproductibilité. Le warmup est séparé des échantillons.

`runtime_availability_probe` vérifie seulement `/v1/models`. Ce contrôle ne prétend pas avoir déclenché puis réparé une panne. Les manifestes indiquent commit, arbre sale, run, modèle demandé/observé, méthode de mesure et limites de corrélation. Le smoke synthétique ne possède pas de corpus RAG ; cette absence est déclarée au lieu d'inventer une empreinte.

## Garde-fous de structure

`check-architecture.ps1` compte les fichiers suivis et nouveaux non ignorés de l'arbre de travail. Aucun fichier source ne peut dépasser 9 999 lignes. Au-delà de 2 500 lignes, une revue est signalée, sans prétendre qu'un seuil constitue une mesure de complexité. Les types partiels historiques ont aussi un budget agrégé explicite : un simple découpage physique n'efface pas leur dette. Réduire ces budgets à chaque extraction de responsabilité ; ne pas les relever pour contourner un échec.

Le workflow `offline-validation.yml` exécute ces contrôles, les tests comportementaux des sondes et les quatre suites `Unit`, sans clé fournisseur. L'activation du workflow distant doit encore être observée sur le service CI ; la présence du fichier ne prouve pas qu'il a tourné.
