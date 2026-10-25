# Complément : ordre réel des colonnes et du texte incliné

Les treize tests layout passent après un second correctif géométrique. Une nouvelle projection réelle confirme ensuite l’ordre des phrases inclinées et la séparation des colonnes jusque dans les chunks publiés pour les témoins contrôlés. Trois pertes distinctes de métadonnées restent ouvertes dans le projecteur. La première série de dix tests ne suffisait pas : une projection intermédiaire conservait les caractères, mais mélangeait encore les colonnes et l’ordre du paragraphe incliné. Le rapport v1 et les preuves intermédiaires restent conservés ; cette version complète leur portée sans transformer le premier GREEN en validation globale.

## Pourquoi la première correction ne suffisait pas

La preuve privée `native-layout-green-checkpoint.private.json` utilise le backend `9C5ADBB75669DC461A18E53FC6CAECA6E92D16AB226D7193EBF6FA5D15069AF4` sur le même PDF source. Son SHA-256 est `A9ECD22166545C67AEF3F334DD7A34F10515AC3D31E35799FAAB6557F782182F`.

La page 5 conserve alors les 1 555 caractères hors espaces et le titre de l’astuce. Pourtant la quantité située dans la colonne gauche est encore accolée à la fin d’une instruction de droite. Le début du paragraphe incliné ne forme pas sa phrase source complète dans l’ordre attendu.

Deux causes restantes ont été isolées :

- **Le regroupement en colonnes n’était jamais atteint.** Le garde préalable exigeait au moins 30 % de lignes à plusieurs segments sur la page entière. Une recomposition de diagnostic des coordonnées observées donne 57 lignes, 67 segments et 10 lignes partagées ; le seuil global demandait 18 lignes. De vraies colonnes stables étaient présentes, mais un encart et une longue colonne diluaient le ratio. Le code revenait donc à une lecture ligne par ligne avant même d’utiliser le nouvel ancrage gauche.
- **Le centre des boîtes de glyphes ne définit pas la ligne d’un texte incliné.** Sur une même phrase, les mots de droite montent ou descendent avec l’inclinaison. Le regroupement par moyenne `CenterY`, limité à la dernière ligne ouverte, séparait ces mots en fragments. Une couverture exacte des mots n’empêche pas cette permutation.

Le recomptage des coordonnées est un diagnostic indépendant effectué sur le JSON figé, pas une nouvelle sonde produite par le backend. Les témoins exécutables ci-dessous établissent les défauts de comportement.

## RED complémentaire puis correctif

Trois cas supplémentaires ont été préparés avant toute modification produit :

| Témoin | Avant correction |
|---|---|
| Deux colonnes stables, dont une beaucoup plus longue | FAIL : leur asymétrie désactive à tort la lecture par colonnes. |
| Vrai PDF synthétique avec trois directives à +4° | FAIL : l’ordre des mots ne restitue pas toutes les phrases. |
| Même PDF synthétique à −4° | PASS : contre-témoin conservé. |

Le PDF de test est construit localement avec une police Helvetica et des matrices de texte explicites. Il contient des directives techniques neutres ; aucun mot de recette ou corpus privé n’est ajouté aux tests.

Preuve RED : `tests/native-layout-ocr-red-v2/manifest.json`, run `76da9eb127e947e486b7a87ab500257a`. Le TRX `Backend-76da9eb127e947e486b7a87ab500257a.trx` a pour SHA-256 `00D93D5AADEBA585EAC70AB278A8EFC94034B08768F182A1A80E204343BDB607`. Les sources sont restées stables pendant cette exécution. Les autres tests OCR présents dans la campagne ont leur propre portée ; ils ne sont pas comptés dans ce RED layout.

Le delta suivant reste limité à `PdfExtractor.cs` et à son nouveau fichier de tests :

1. Trois lignes réellement partagées restent nécessaires pour établir des colonnes simultanées. Les critères d’ancrage, de stabilité et de couverture restent actifs. Le ratio de 30 % sur l’ensemble de la page est retiré.
2. Les mots internes transportent facultativement les coordonnées de baseline fournies par PdfPig. Les appels existants sans ces coordonnées restent possibles.
3. Les mots dotés d’une baseline sont regroupés par orientation compatible et distance perpendiculaire à cette ligne. Le lecteur peut retrouver une ligne déjà ouverte. Des orientations opposées ne sont pas fusionnées.
4. Les gardes de multiensemble de caractères bruts, de tokens et de géométrie complète, ainsi que le repli sur le texte brut, restent en place.

Ce changement exploite la géométrie native du document ; il ne reconstruit aucune phrase par hypothèse sémantique. La revue indépendante de l’agent client n’a identifié aucun défaut matériel supplémentaire dans ce delta. Elle ne vaut pas validation universelle des pages à rotations fortes, orientations mélangées ou titres centrés complexes.

## GREEN relu

La campagne `tests/source-fidelity-red-green-v3/manifest.json`, run `6b3f8e0d592346de9d9db92cdc746833`, comprend **57 tests : 54 PASS et 3 FAIL**. Les **13 cas layout sont tous PASS**. Les trois échecs restants sont des RED distincts : un préambule de page sans section assignée et deux suffixes OCR fiables encore supprimés.

La campagne combinée n’est donc pas présentée comme entièrement réussie.

- Compilation demandée ; sources stables pendant le run.
- Empreinte globale des sources : `1D32E8A1B6A48C424DE8263E8D182EC9E5DFAC23595DA124C8C73180B7C27EC5`.
- DLL backend chargée : `8C56AADD35FCF98E5FF2884014308A89671159E8CF6CF5C473E8E6A64603EF5B`.
- TRX : `Backend-6b3f8e0d592346de9d9db92cdc746833.trx`.
- SHA-256 du TRX relu : `5AD45CD91607938F483E5612CFE9608ED497F5294CCA19BA26DDF740123C5953`.
- `PdfExtractor.cs` dans l’inventaire compilé : `B78408DBA1F8F97F9BA2151DEF30F37BFBA304971855B289A23FB14E113C613C`.
- `PdfExtractorLayoutRetentionTests.cs` dans l’inventaire compilé : `8A8DA37035A841FEBCC02FD48703C2D07A1CE52EABA3024A781F32E4253B8969`.

Ces résultats ont été exécutés par l’opérateur parent puis relus depuis les manifestes/TRX. Cette rédaction n’a lancé aucun build, API, service ou modèle.

## Projection réelle après correction

La projection `native-layout-green-checkpoint-v2.private.json` a ensuite été exécutée par l’opérateur parent avec la même DLL backend `8C56AADD35FCF98E5FF2884014308A89671159E8CF6CF5C473E8E6A64603EF5B`. Son SHA-256 est `B95AF38B34BCE8098FB4DE0AB751B73C503DDDE4CBD9140D9F6E9DD25201FE0D`. Le PDF source compte 36 pages ; l’extraction entière précède la projection du sous-ensemble physique contrôlé.

La preuve publique `native-fidelity-checkpoint-v3.json`, observée à `2026-10-25T22:04:32.6363925Z`, annonce **11 prédicats réussis sur 14, trois échecs, zéro appel fournisseur**. La revue a relu ses contrôles et vérifié l’empreinte du snapshot privé.

- Le nombre de personnes, la quantité de vin, les durées de préparation/cuisson de la page 5 et l’avertissement sont présents dans le texte sélectionné, les unités, les chunks et le contexte publiés sur cette même page physique.
- Les trois témoins du paragraphe incliné — première phrase, ordre des morceaux et conseil final — passent jusqu’aux chunks et contextes publiés. La lecture privée confirme que la quantité n’est plus accolée à la fin d’une instruction de l’autre colonne.
- Les témoins de durée de préparation page 6 et de durée/qualification page 7 passent également.
- Les trois échecs restants sont la qualification de coût et la difficulté page 5, puis la durée de cuisson page 6. Ces faits sont bien conservés dans le texte de page et dans les unités ; ils disparaissent encore avant les chunks publiés. Leur correction appartient au projecteur, pas au présent lecteur géométrique.

Le résultat public reste explicitement `passed=false` : les onze réussites ne masquent pas les trois pertes restantes. Les unités/chunks doivent avoir les bornes exactes de la page physique ; une page voisine ne peut satisfaire un témoin.

## Limites maintenues

La projection couvre des pages et faits spécifiés. Elle n’est ni une preuve d’égalité sémantique exhaustive du PDF, ni une ingestion complète avec worker, OCR, Docling, vecteurs ou profils documentaires. L’ordre des mises en page arbitraires à orientations mélangées ou fortes rotations reste hors de cette validation.

La présence des caractères est nécessaire ; elle ne suffit pas à valider leur ordre, leur association au bon sujet ou leur accessibilité par les outils. C’est précisément la raison pour laquelle le témoin réel a été conservé après les tests synthétiques.

Les pertes résiduelles du projecteur, les corrections distinctes de préambule physique et de suffixes OCR, les suites plus larges et le préflight réel devront être rapprochés de leurs propres preuves. Aucun appel payant ni planning de repas n’est validé par ce rapport. Le statut produit reste `TESTE_NON_APPROUVE`.
