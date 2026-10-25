# Cause du premier déficit documentaire : parseur PostgreSQL de la fixture

Le premier cas réel Luna a abouti à `insufficient_documentation` avec zéro preuve. L'examen des entrées montre que le modèle a demandé une recherche complémentaire plus courte. Cette recherche suffisait à retrouver la page attendue une fois la configuration Unicode du cluster de test corrigée. Ce cas ne démontre donc pas une incapacité du modèle à lire ou à répondre aux deux faits présents dans le PDF.

La chaîne observée comportait trois recherches. Les deux premières imposaient notamment le nom du fichier et des mots absents du passage. La dernière requête comportait six mots réellement présents sur la page. Pourtant les trois canaux bruts retournaient zéro résultat : aucune preuve n'a été perdue après récupération ou pendant sa revalidation.

La cause de la dernière recherche vide a été reproduite gratuitement dans de nouveaux clusters isolés. Avec `LC_CTYPE=C`, PostgreSQL 16.14 identifie `ingrédients…` comme un seul lexème, ponctuation comprise. Le terme de requête sans ellipse ne correspond pas ; la conjonction entière devient fausse. Le préflight initial recherchait uniquement `bourguignon`, un mot ASCII qui contournait ce défaut et retournait bien deux passages.

## Comparaison causale exécutée par l'opérateur parent

| Expérience | Configuration | Dernière requête, trois scopes | Conclusion |
|---|---|---|---|
| `query-matrix-04` | libc, `LC_CTYPE=C` | 0 résultat SQL et API dans chaque scope | Reproduction du déficit, indépendamment de DocId/DocPath/catégorie. |
| `query-matrix-05` | Même base C, comparaison avec `COLLATE pg_catalog.unicode` | Toujours 0, y compris calcul Unicode explicite | Hypothèse de correction par simple `COLLATE` rejetée. |
| `query-matrix-06` | libc conservé, locale `English_United States.1252`, stockage UTF-8 | 1 résultat SQL et API dans chacun des trois scopes | Le seul changement de locale du cluster rétablit le passage attendu. |

Chaque expérience a exécuté neuf cas SQL et neuf recherches HTTP locales. Les mêmes octets de seed et de pages, le même manifeste de document/révision et le même backend `1619ECFB12B287087098E23D3074317B1B3C77383B8FF5BD5D7B8A89B66BFC0C` sont conservés dans ces trois comparaisons. Les cinq contrôles génériques de casse, ponctuation et négatifs confirment le mécanisme : la ponctuation Unicode et la casse fonctionnent après changement de locale ; l'absence d'accent et un terme obligatoire manquant restent non correspondants. Aucun élargissement en OR n'a été introduit.

Les trois clusters ont conservé zéro job avancé et ont été arrêtés proprement. Aucun appel fournisseur n'a été effectué pendant ces diagnostics. Le cas payé initial et les anciennes preuves n'ont pas été réécrits.

## Corrections de l'outillage

Les deux scripts de fixture Windows prennent un paramètre `PgLocale` et utilisent par défaut la locale non C éprouvée ci-dessus, toujours avec `--encoding=UTF8`. Les paramètres demandés sont enregistrés dans les manifestes. Le paramètre explicite `C` sert uniquement aux expériences négatives ; aucune base habituelle ou distante n'est modifiée.

Un contrôle comportemental gratuit est ajouté à `Start-OwnedPaidBackend`, avant la création du processus pouvant appeler le fournisseur. Il conserve quatre sentinelles génériques et la locale/version réelles dans `text-parser-conformance.json`, puis refuse tout résultat incomplet ou défectueux. La suite offline reçoit quatre cas d'admission/refus supplémentaires ; leur exécution finale est à distinguer de cette correction de source.

Les premiers essais de diagnostic ont aussi révélé deux problèmes de transport PowerShell indépendants de la recherche produit : un argument natif `psql -c` pouvait altérer l'Unicode, et des notices stderr mélangées au stdout empêchaient de lire du JSON. Le helper écrit désormais les requêtes dans des fichiers UTF-8 sans BOM, utilise `-f`, conserve les deux flux séparément et borne leur durée. Le diagnostic de locale lit `pg_database` plutôt que des paramètres de session inexistants. Ces défauts de diagnostic ne sont pas attribués au transport HTTP UTF-8 du cas payé.

## Portée et suite

Ce résultat ferme une cause concrète du déficit de preuves sur la fixture. Les deux premières recherches restent trop exigeantes pour le contenu lexical du passage ; leur contrat de construction fait l'objet d'un correctif séparé. Le seed initial ne reproduisait pas non plus exactement les projections d'ingestion de production ; l'agent propriétaire remplace ce chemin par les vrais projecteurs avant une nouvelle dépense.

Ce rapport ne valide aucune nouvelle réponse payante, aucun parcours WinUI supplémentaire, ni le planning complet de repas. `TESTE_NON_APPROUVE` reste applicable. Aucun changement de `CanonicalRetrieval.cs` ni migration 070 n'a été réalisé au titre de cette hypothèse.

Preuves parentes relues :

- `query-matrix-04/query-diagnostic.json` : SHA-256 `27C7D4A5B59C9A41F0EECF0D16535BB40F34FB44A953D136C5FE03957884139D`.
- `query-matrix-05/query-diagnostic.json` : SHA-256 `477DE4FC66516097BD45014F7AC361A33C4151CC61A4F934F21A3D550AD54E26`.
- `query-matrix-06/query-diagnostic.json` : SHA-256 `A8AC1D57158C2D81EA482CF8B9BDA226163483C22BFD5DF32719D057878304F7`.

Les fichiers ci-dessus se trouvent dans `artifacts/luna-live-20261025/private-diagnostics`. Ce relecteur a préparé les scripts, corrigé l'outillage autorisé et relu les résultats ; les démarrages, requêtes locales et exécutions de tests ont été assurés par l'opérateur parent.
