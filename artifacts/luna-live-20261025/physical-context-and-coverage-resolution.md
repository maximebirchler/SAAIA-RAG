# Fidélité du contexte physique et couverture de projection

Rapport du 25 octobre 2026. Ce document décrit les changements de projection et de sondes de cette phase. Les snapshots natifs détaillés restent privés. Aucun contenu documentaire, identifiant de profil, secret ou clé n'est reproduit ici. Les exécutions citées ont été orchestrées par root ; l'auteur de cette revue n'a lancé ni modèle, ni service, ni compilation.

## Ce qui a été corrigé

La récupération perdait des faits courts pourtant conservés dans les unités source. Les filtres utilisaient l'intérêt d'un fragment comme résultat autonome pour décider s'il pouvait figurer dans le contexte de son corps. Mesures nommées, restrictions courtes et valeurs d'un mot disparaissaient ainsi avant la constitution des fenêtres. Le projecteur distingue désormais ces responsabilités : les unités fiables peuvent rester avec un corps de mêmes section et page sans devenir des candidats autonomes artificiels. Les compteurs nus, quantités sans sujet, connecteurs et signaux de corruption continuent d'être traités séparément.

Le retour des métadonnées fiables a aussi révélé une frontière intermédiaire qui séparait un titre de son corps. La correction garde le titre et les métadonnées lorsqu'un vrai corps suit sur les mêmes section et page. Un nouveau titre reste une frontière ; aucune métadonnée factuelle n'est supprimée pour rétablir artificiellement l'ancien résultat.

La projection réelle a ensuite montré une autre perte : des contextes courts situés au début ou à la fin d'une page pouvaient rester dans une fenêtre trop courte, sans corps, puis être rejetés. Leur section héritée ne suffit pas à établir leur relation au titre voisin. Une récupération physique distincte conserve ces faits encore non représentés avec un corps déjà publiable de la même page.

Ces fenêtres portent le type `physical_page_context_v1`. Leur section, unité représentative et heading ne sont pas attribués ; leur composition indique un préfixe ou suffixe physique non associé. Les unités originales, leurs ordinals, pages et offsets sont conservés. Le texte contextuel indique explicitement `context_scope: physical_page` et `section_association: not_asserted`. Chaque fenêtre respecte `maxWords` et le prédicat normal de publication. Il n'y a ni emprunt à une autre page, ni contexte factuel isolé promu sans corps.

## Les sondes distinguent projection et publication

La première sonde de couverture comptait les unités de tous les chunks projetés, y compris les chunks ensuite rejetés. Elle pouvait donc annoncer zéro unité non représentée alors qu'un texte ne faisait pas partie des chunks publiés.

Deux axes indépendants corrigent cette ambiguïté :

- `projected_*` décrit tous les chunks produits par le projecteur.
- `published_*` décrit ceux admis par le même prédicat que le worker et le dépôt de publication.

Une provenance absente ou étrangère rend l'axe concerné indisponible et ses compteurs restent absents. L'absence effective de chunks constitue au contraire une couverture nulle connue. Une provenance inconnue d'un chunk rejeté ne rend pas inconnue la couverture de chunks publiés dont la provenance est complète.

Les données écrites par cette sonde sont des nombres et booléens. Elle n'ajoute ni extrait, nom de fichier, chemin, heading, question ou identifiant documentaire au journal. La couverture mesure la présence d'ordinals source : ses sommes de caractères représentent la longueur des unités référencées, pas une preuve de comparaison textuelle ou de fidélité sémantique.

## Preuves exécutées et portée

Les manifests des campagnes de tests ci-dessous conservent les empreintes des sources et binaires et indiquent `sourceChangedDuringRun=false`.

| Preuve | Résultat établi | Limite du lot complet |
| --- | --- | --- |
| `tests/source-fidelity-red-green-v3/manifest.json` | 16 cas de conservation de contexte, 7 sondes et 6 politiques de qualité PASS | 57 cas au total, 54 PASS et 3 échecs attendus sur des corrections séparées alors en RED |
| `tests/physical-tail-red-ocr-green-v4/manifest.json` | 3 cas positifs physiques reproduits en échec ; 2 témoins négatifs PASS | Capture avant le correctif physique : les 3 échecs sont la preuve causale, pas un état final annoncé vert |
| `tests/format-cues-red-context-heading-green/manifest.json` | Les 5 cas physiques sont PASS après correction | 41 cas au total, 37 PASS et 4 échecs OCR distincts encore en traitement au moment de ce lot |
| `native-fidelity-checkpoint-v4.json` | 14 prédicats natifs sur les pages physiques sélectionnées sont conservés du texte source aux unités, chunks publiés et contextes publiés ; zéro appel fournisseur | Vérification de faits explicitement choisis, sans ingestion complète du worker ni égalité sémantique exhaustive |
| `tests/unit-final-v6/manifest.json` | 5098/5098 PASS : Backend 2577, Contracts 20, ClientCore 82, Client 2419 ; zéro échec et zéro test ignoré | Suite Unit uniquement, sources stables pendant l'exécution ; les profils PostgreSQL, LiveLocal et LivePaid sont exclus |
| `native-fidelity-final-v1.json` | 14/14 prédicats natifs PASS sur le binaire final, zéro appel fournisseur | Même portée sélectionnée que le témoin précédent ; le SHA256 backend correspond à celui du lot Unit final |

Le filtre du premier lot conservait l'ancien nom d'un test historique de titre qui a été renommé. Ce test n'est donc pas compté comme exécuté dans ce lot ; les seize nouveaux cas de contexte ont bien été exécutés. Sa présence et son PASS sont ensuite confirmés dans la suite finale : sept cas physiques, seize cas de conservation de contexte, sept cas de sondes et le test historique de titre renommé ont tous été réellement exécutés et sont PASS.

Une contre-revue indépendante n'a relevé aucun défaut matériel supplémentaire dans le périmètre des bords physiques observés. Elle confirme l'ancrage sur un corps déjà publiable, les bornes de page et de budget, la provenance conservée et l'absence d'association sémantique de section fabriquée.

## Correction de l'ordre de navigation

Le checkpoint réel a exposé un effet de l'ajout des contextes après les fenêtres ordinaires : les indices pouvaient présenter les pages usuelles, puis revenir à une page antérieure pour son contexte ajouté. Les parcours de voisinage ou de défilement qui suivent `ChunkIndex` doivent conserver l'ordre physique.

Un sixième cas physique a été ajouté avant correction. Il exige des indices contigus, une progression physique sans retour arrière, un ordre source cohérent dans une page et des ordinals/offsets inchangés. Root l'a capturé RED : la sélection `physical-order-red-format-green` compte 19 cas, 18 PASS OCR et un échec de navigation avec retour d'une page ultérieure vers une page antérieure.

Le correctif trie et réindexe les chunks après l'ajout effectif de fenêtres physiques, par page, début d'unités source puis ancien index. Les textes, checksums, offsets et ordinals restent inchangés. Les correspondances de stockage et de contexte sont produites après cet ordre final. Un septième cas vérifie la limite de portée : sans fenêtre physique ajoutée, l'ordre préalable des sections reste celui du projecteur existant. Les sept cas sont PASS dans `unit-final-v6` ; le résultat ne repose donc pas sur le seul témoin natif antérieur.

## Limites maintenues

Ces résultats ne valident pas un nouveau raisonnement de modèle ni le planning de repas. Ils prouvent des chemins de projection, d'admission contextuelle et d'observabilité précis. Le témoin natif porte sur des pages et faits sélectionnés ; il ne remplace pas une ingestion complète, la vérification des autres formats ou une validation sémantique exhaustive.

Un contexte qui ne peut tenir avec un corps fiable dans le budget n'est pas tronqué ni publié hors budget. Les unités non représentées restent détectables par la sonde publiée. Les fenêtres physiques ne prétendent pas connaître la relation sémantique entre un fait marginal et le sujet voisin. Une autre orientation ou disposition PDF que celles testées doit conserver sa propre preuve de lecture ; la conservation des mots ne prouve pas à elle seule leur ordre universel.
