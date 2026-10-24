# Reproduire l'audit de structure du 24 octobre 2026

Référence de départ : `5131dee30131c1206f6a2881d615ce53a0764682`.
Rapport : `documents/agent/audit-structure-et-sondes-2026-10-24.md`.

Ces outils lisent les sources et écrivent des métriques locales. Ils n'exécutent pas le produit, ne contactent aucun modèle et ne suppriment aucun fichier. Le test du harnais n'exécute que deux définitions de fonctions extraites de son AST ; le transport HTTP est remplacé par une fonction locale.

Depuis la racine du dépôt, avec PowerShell et le SDK .NET 8 :

```powershell
$audit = Join-Path (Get-Location) 'artifacts/architecture-audit-20261024'
& "$audit/New-SourceInventory.ps1"
New-Item -ItemType Directory -Path "$audit/empty-feed" -Force | Out-Null
dotnet restore "$audit/inspector/Inspector.csproj" --source "$audit/empty-feed"
dotnet run --project "$audit/inspector/Inspector.csproj" --no-restore -- (Get-Location).Path $audit
& "$audit/Invoke-HarnessCounterexamples.ps1"
```

`New-SourceInventory.ps1` choisit les chemins suivis à la référence de départ, mais lit leur contenu dans le checkout courant. Il fournit les empreintes SHA-256 : comparer celles-ci avant de comparer deux résultats. Pour un nouveau périmètre, passer explicitement `-SourceRevision <commit>`. Les résultats peuvent varier si le code a changé. L'inspecteur n'est pas un graphe sémantique des projets compilés ; les symboles de parsing sont dans `methodology.json`.

Les sorties complètes `type-metrics.json` et `method-metrics.json` se régénèrent localement. Le résumé versionné conserve les concentrations majeures. Les compteurs de branchements syntaxiques ne sont pas une mesure certifiée de complexité cyclomatique. Les longueurs de types imbriqués se recouvrent ; ne pas les additionner pour calculer le volume du dépôt.

`unreferenced-private-candidates.json` liste des candidats, jamais une autorisation de suppression automatique. Il inclut les correspondances lexicales XAML et chaînes exactes ; ni l'absence ni la présence d'un tel texte ne prouve à elle seule la joignabilité. Génération, profils de compilation, réflexion et handlers doivent être examinés.

Les trois contre-exemples sont attendus sur la référence auditée : leur reproduction confirme les défauts du harnais. Après correction du harnais, le script doit échouer si ces contre-exemples disparaissent ; il faudra alors mettre à jour l'audit ou conserver ces preuves historiques sans les traiter comme des tests d'acceptation courants.
