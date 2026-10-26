# Audit de la chaîne canonique et de sa publication — 25 octobre 2026

Périmètre : orchestration du worker, extraction Docling, réconciliation du texte/layout natif, sections/unités, chunks et publication PostgreSQL/Qdrant. Audit de source **en lecture seule**, sans appel modèle, réseau, service, compilation, test ou commande Git. Seul ce nouveau rapport est écrit ; les campagnes et leurs preuves historiques sont conservées. Les conclusions ci-dessous ne constituent pas une nouvelle exécution de reproduction. Les défauts marqués « établi statiquement » reposent sur un chemin de code et une entrée déterministe ; leur RED reste à capturer avant correction.

Les recherches des `AGENTS.md` dans les parents du checkout et dans le dépôt, fichiers cachés inclus hors `.git`, n'ont trouvé aucun fichier applicable. Le contrôle de stabilité des sources pendant PostgreSQL final-v3 reste à la charge de l'exécution root. Aucun résultat de cette suite n'est déduit de cet audit.

## Conclusion et priorité

La fidélité du chemin natif amélioré ne permet pas de déclarer le chemin Docling validé. Il existe encore des décisions qui retirent une donnée documentaire ou lui attribuent une relation non prouvée, et une reprise qui peut déclarer une publication complète avec un index vectoriel partiel. Les corrections doivent commencer par des RED de fidélité et de reprise, puis par un vrai document traversant le worker et ses deux stockages, sans fournisseur LLM payant.

Priorités proposées : **C01–C04 et C11** pour la fidélité et l'association des sources ; **C06–C07** pour l'identité et la publication ; **C05/C08/C09** pour la cohérence des vues et des diagnostics ; les capacités non prises en charge de **C10** doivent devenir explicites avant toute promesse de prise en charge générale.

## Chemin réellement atteint

Quand `DocumentIntelligence.Enabled=true` et provider Docling :

1. `IngestionWorker.ProcessUpsertAsync` calcule le SHA du fichier puis appelle `IngestionExtractionStage`. Celui-ci effectue l'audit natif optionnel, convertit avec Docling et produit une vue legacy du JSON (`IngestionWorker.cs:1092–1128`, `IngestionExtractionStage.cs:19–49`).
2. `IngestionProjectionStage.ProjectStructure` adapte le document canonique, applique la réconciliation native et l'inventaire d'images. Les sections viennent du canonique ; les unités viennent encore des pages legacy (`IngestionProjectionStage.cs:30–55`).
3. Le worker projette les chunks depuis le canonique réconcilié et construit des unités de profil à partir des chunks publiables (`IngestionWorker.cs:1407–1433`). L'index exact utilise ces unités de profil lorsqu'elles existent ; il serait donc incorrect de prétendre que tout l'index exact passe nécessairement par les unités legacy (`IngestionProjectionStage.cs:79`).
4. Embeddings et points Qdrant sont écrits par lots avant la transaction de publication SQL (`IngestionWorker.cs:1627–1719`). La transaction SQL publie révision, pages, sections, unités, chunks, liens, index exact/contextuel, profil et bundle canonique (`DocumentFoundationRepo.cs:154–190`).
5. En mode natif, les adaptateurs/projecteurs Docling ne sont pas atteints. Les essais natifs récents et les réponses réelles 02/03 ne sont pas une validation de ces branches Docling.

## Défauts concrets

### C01 — Une donnée répétée en marge est éliminée par fréquence

**Établi statiquement ; priorité haute.** [CanonicalRepeatedPageFurnitureClassifier.cs](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/DocumentIntelligence/CanonicalRepeatedPageFurnitureClassifier.cs:83>) : un groupe exact présent sur au moins `max(3, ceil(50 % des pages))` et à position verticale stable devient `IsRepeatedFurniture=true` sans preuve que son texte est du bruit. Les blocs `text` ordinaires sont éligibles. Le projecteur les exclut ensuite avant toute construction d'atome : [DoclingCanonicalRetrievalProjector.cs](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/DocumentIntelligence/Docling/DoclingCanonicalRetrievalProjector.cs:247>).

Le même classifieur peut aussi retirer une signature marginale du début/de la fin d'un autre bloc (`CanonicalRepeatedPageFurnitureClassifier.cs:232–294`). Le texte brut/canonique demeure dans l'artefact, mais son texte de retrieval est amputé. Ce n'est donc pas une perte du PDF original : c'est une perte d'accessibilité dans le RAG.

La réconciliation ne répare pas cette décision : `BuildCanonicalSegments` et `BuildCanonicalPageTokens` comptent également ces blocs exclus (`CanonicalNativeTextCoverageReconciler.cs:290–295/421–425`). Ils paraissent couverts à 100 % et ne sont pas récupérés. Un avertissement réel répété en tête de trois pages peut ainsi disparaître des chunks. Le témoin existant `Project_InfersRepeatedMarginFurnitureAndOmitsItFromRetrieval` place son avertissement substantiel au centre des pages et ne couvre pas cet avertissement **en marge** (`DoclingCanonicalDocumentAdapterTests.cs:1287–1374`).

**RED à capturer :** trois pages, bloc `text/body` identique « Disconnect power before opening enclosure. » dans les 15 % supérieurs, corps fiable distinct en dessous. Adapter → réconcilier → projecteur → garde de publication ; exiger la donnée littérale accessible sur chaque page sans la promouvoir en titre. Ajouter un cas de même avertissement en suffixe d'un bloc fiable. Conserver un témoin de compteur de page véritable, sans confondre répétition et inutilité.

### C02 — Une récupération native peut recevoir le dernier titre d'une autre zone

[Ancrage C02](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/DocumentIntelligence/Docling/DoclingCanonicalRetrievalProjector.cs:100>).

**Établi statiquement ; priorité haute.** Les blocs `native_text_recovery` sont ajoutés après les blocs Docling, avec un reading-order terminal (`CanonicalNativeTextCoverageReconciler.cs:152–166`). Le projecteur leur impose à nouveau un ordre terminal (`DoclingCanonicalRetrievalProjector.cs:250–269`). Ils ne portent pas de section prouvée, mais `ResolveChunkType` leur donne le type ordinaire `docling_canonical_content_v1` (`:766–781`).

`StartBuffer` réinjecte les headings actifs dans tout type sauf l'alternative native de layout (`:100–119`). En fin de page, le heading actif est celui du dernier sujet Docling. Les chunks récupérés peuvent donc afficher `SectionTitle/HeadingPath` de ce sujet et le texte du heading dans leur corps (`:595–664`). S'ils fusionnent dans le buffer précédent, le problème existe aussi sans création d'un nouveau buffer.

Ce chemin ne porte pas `physical_page_context_v1` ni le signal d'association inconnue récemment transmis aux prompts. Il faut étendre la prudence à la récupération réellement non localisée, pas seulement renommer son titre.

**RED :** une page, titres A puis B avec corps, une ligne native manquante située sous A. Réconcilier puis projeter. Exiger que cette ligne reste lisible avec sa page, ses identifiants de blocs et une association non affirmée ; elle ne doit ni recevoir B comme `SectionTitle`, ni contenir B comme contexte sémantiquement attribué. Témoins : véritable continuation de section prouvée et alternative de layout conservées.

### C03 — Une couverture approximative masque une différence de valeur et les faits d'un mot

[Ancrage C03](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/DocumentIntelligence/CanonicalNativeTextCoverageReconciler.cs:117>).

**Établi pour la règle ; effet réel sur un corpus à reproduire.** `CanonicalNativeTextCoverageReconciler.cs:117–123` ne récupère que les lignes dont la couverture est strictement inférieure au seuil (0,90 par défaut). La couverture retenue est le **maximum** entre ordre et sac de mots (`:381–395`). Une ligne de vingt tokens dont une valeur numérique est perdue ou remplacée conserve 19/20 = 95 % de couverture et n'est pas récupérée. Une couverture élevée n'est pas une preuve d'égalité des faits.

En complément, `IsUsefulRecoveryLine` rejette systématiquement une ligne d'un mot (`:556–578`). Des valeurs ou restrictions courtes fiables telles que « Approved » peuvent donc être présentes dans la couche native, absentes de Docling et ne jamais rejoindre le canonique réconcilié. Le bon correctif récent du projecteur natif ne s'applique pas ici. Le garde de layout cherche surtout un avantage d'ordre et ne garantit pas la récupération de cette différence ponctuelle.

**RED :** ligne fiable ≥20 mots identique sauf `20` remplacé par `30`, puis ligne d'un mot non couverte adossée à un corps fiable. Exiger une divergence explicite conservant les deux observations/provenances ; ne pas choisir arbitrairement la valeur vraie, fabriquer une phrase, ou ajouter un dictionnaire de mots autorisés. Un témoin de seule variation d'espaces/césure doit rester sans duplication inutile.

### C04 — Une page absente du JSON Docling est ignorée par les réconciliations

[Ancrage C04](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/DocumentIntelligence/CanonicalNativeTextCoverageReconciler.cs:90>).

**Établi statiquement ; entrée partielle à tester.** Quand une page native n'existe pas dans `document.Pages`, la réconciliation fait `continue` (`CanonicalNativeTextCoverageReconciler.cs:90–98`). L'inventaire d'images fait de même (`CanonicalNativePdfImageInventoryReconciler.cs:46–54`). Le client vérifie HTTP/status/errors/JSON/schema-name, mais ne rapproche pas les identifiants et nombres de pages de la source (`DoclingClient.cs:113–126`). Les autres pages contenant du texte peuvent satisfaire la garde de publication ; l'absence n'est pas forcément terminale.

**RED :** audit natif de trois pages, réponse Docling `success/errors=[]` ne contenant que 1 et 3. Traverser la projection puis l'admission : exiger un état incomplet/erreur de couverture, un compteur `missing_source_pages=1`, la page manquante explicite et aucune publication prétendue complète. Un document dont la deuxième page est réellement blanche doit être distingué d'une page non retournée. Ne pas inventer son contenu ni son résultat OCR.

### C05 — Deux vues documentaires peuvent diverger dès l'adaptation multi-provenance

[Ancrage C05](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/DocumentIntelligence/Docling/DoclingLegacyExtractionAdapter.cs:23>).

**Établi statiquement.** Le canonique découpe correctement le texte d'un item multi-provenance avec les charspans Unicode de `orig` (`DoclingCanonicalDocumentAdapter.cs:414–445` ; témoin existant `:247–284` dans les tests). La vue legacy copie au contraire l'intégralité de `OriginalText` et de `Text` pour chaque provenance (`DoclingLegacyExtractionAdapter.cs:23–33`). Deux régions/pages d'un même item reçoivent donc toutes deux le texte complet ; les tableaux sont aussi rendus intégralement pour chaque provenance (`:36–46`, portée des tableaux multi-pages à vérifier).

Les pages/unités publiées ne sont pas reconstruites depuis le canonique réconcilié (`IngestionProjectionStage.cs:49–55` ; `IngestionWorker.cs:1815`). Les nombres de caractères, checksums de pages, unités, contexte fondé sur ces unités et diagnostics peuvent par conséquent décrire une autre vue que les chunks canoniques. Un ajout récupéré dans le canonique n'est pas ajouté aux pages legacy. **L'index exact Docling dispose heureusement du chemin `profileUnits` canonique : ne pas lui attribuer cette régression sans preuve.**

**RED :** réutiliser l'item « Alpha 😀 Beta / Gamma » à deux charspans/pages et comparer page/units/canonical/chunks après `ProjectStructure`, puis SQL. Exiger que Gamma ne soit pas présenté comme texte provenant de la première page et réciproquement. Ajouter un fait natif récupéré absent du JSON initial et vérifier que le contrat de chaque vue explique explicitement son inclusion ou son absence. La solution structurelle est une vue page/unités dérivée du même canonique avec provenance, pas un second découpage indépendant des charspans.

### C06 — Une reprise peut publier un index vectoriel incomplet ou un mélange d'anciennes projections

**Établi statiquement ; priorité haute.** [IsResumeCheckpointCompatible](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Ingestion/IngestionWorker.cs:597>) compare seulement hash du PDF, nombre de chunks, nom du modèle embeddings et format d'entrée. Il ignore les checksums/textes projetés, les options/révisions de projection/OCR/Docling, la révision effective du modèle, la collection et son contenu réel.

Le worker peut donc reprendre à N après avoir recalculé des chunks différents mais de même nombre (`:1513–1524`), saute les N premiers chunks (`:1627`) puis publie **tous** les chunks SQL (`:1815`). L'exemple le plus direct ne dépend d'aucune subtilité de projection : changer la collection vers une collection vide entre deux tentatives laisse le checkpoint compatible ; seules les positions N..fin sont écrites. Les appels Qdrant peuvent tous répondre 2xx et la transaction déclarer le document indexé.

Le payload dense et sa compatibilité de recherche utilisent également le nom du modèle et le format, pas `EmbeddingsModelRevision` (`IngestionWorker.cs:2050–2053`, `RetrievalChannels.cs:3100–3105/3220–3229`), alors que cette révision est annoncée dans le manifeste (`DoclingIngestionStageManifestFactory.cs:170–172`). Deux variantes de même nom/dimension peuvent se mélanger sans refus mécanique.

**RED :** checkpoint après le premier lot ; reprise avec (a) nouvelle collection vide, (b) même nombre de chunks mais checksum d'un chunk changé, (c) révision embeddings changée à nom/dimension constants, (d) disparition d'un point déjà acquitté. Faux TEI/Qdrant hors réseau d'abord, puis stockage local isolé. Exiger invalidation et réindexation ou réparation vérifiée avant activation. La reprise identique intégrale doit continuer à éviter le travail déjà vérifié. Le fingerprint doit couvrir les entrées effectives et la cible ; vérifier l'inventaire de points, pas seulement le dernier compteur local.

### C07 — Le hash et les extractions peuvent viser des octets différents

[Ancrage C07](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Ingestion/IngestionWorker.cs:1097>).

**Fenêtre de concurrence établie ; occurrence réelle non démontrée ici.** Le worker ouvre/ferme le fichier pour son SHA (`IngestionWorker.cs:1097–1101`), puis les extracteurs et le client Docling le rouvrent (`IngestionExtractionStage.cs:28/46`, `DoclingClient.cs:33–39`). Avant publication, il contrôle l'existence et relit le mtime, sans relire le hash ni comparer un snapshot immuable (`IngestionWorker.cs:1730–1733`).

Un remplacement du fichier entre ces lectures peut attacher le hash A au texte B, ou faire comparer une couche native A à une conversion Docling B. La version SQL peut détecter un changement déjà observé par le scanner, mais cette détection asynchrone ne prouve pas que les octets de toutes les extractions étaient les mêmes.

**RED :** barrière après hash, remplacer le fichier par un PDF B au même chemin avant conversion ; exiger que toutes les étapes utilisent le snapshot A identifié ou que l'ingestion soit invalidée, sans publier un mélange. Cas additionnel entre audit natif et conversion. Une copie de travail immuable adressée par SHA et transmise à toutes les étapes est préférable à plusieurs vérifications tardives.

### C08 — Les métadonnées de provenance ne décrivent pas toujours les options effectives

[Ancrage C08](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/DocumentIntelligence/Docling/DoclingIngestionStageManifestFactory.cs:69>).

**Établi statiquement.** Le force-OCR calculé à partir de l'audit natif est passé comme override au transport (`IngestionExtractionStage.cs:41–46`, `IngestionWorker.cs:1121`, `DoclingClient.cs:44`). Le manifeste OCR hash ensuite `documentIntelligence.ForceOcr`, l'option déclarée, plutôt que cette valeur effective (`DoclingIngestionStageManifestFactory.cs:69–77`). L'option false et un vrai appel forcé peuvent ainsi partager le même hash d'options annoncé.

Les options de réparation de tableaux du backend ne sont pas transmises par requête : le sidecar utilise ses variables d'environnement (`infra/docling-sidecar/saaia_docling/app.py:39–49`), tandis que le manifeste hash les valeurs du backend (`DoclingIngestionStageManifestFactory.cs:395–401`). Le compose emploie actuellement les mêmes variables pour les deux : **pas de désaccord live constaté**, mais la reproductibilité se brise si un seul côté change. Les versions moteur/modèle sont aussi déclarées par configuration, pas attestées par une réponse runtime contrôlée.

**RED :** force déclaré false mais décidé true ; exiger une trace et un manifeste effectifs distincts. Réparation sidecar configurée différemment du backend : la réponse doit attester la config exécutée ou la divergence doit bloquer/être signalée. Ne pas transformer une valeur déclarée en observation d'exécution.

### C09 — Disponibilité/qualité de l'audit et succès opérationnel ne sont pas la fidélité

[Ancrage C09](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Ingestion/IngestionExtractionStage.cs:33>).

**Dégradation explicitement journalisée, mais état durable ambigu.** Une exception de l'audit natif est capturée ; le sous-probe est `degraded`, puis la conversion continue (`IngestionExtractionStage.cs:33–39`). `CanonicalNativeTextCoverageReconciler.Apply` retourne des zéros avec `Enabled=true` quand l'extraction native est nulle (`:55–73`). Le manifeste peut donc lister une étape activée avec zéro page auditée sans un statut durable qui sépare indisponibilité et absence de travail. Ne pas prétendre que cette dégradation est entièrement silencieuse : le warning et le probe existent.

Le repli de réparation régionale de tableaux présente un autre écart durable : le sidecar garde la table originale si la réparation lève, inscrit un résumé `status=error`, et n’impose pas un échec global (`infra/docling-sidecar/saaia_docling/response_repair.py:87–98`). Préserver l’original est utile. Cependant, le manifeste ne crée l’étape correspondante que si au moins une réparation est `applied` (`DoclingIngestionStageManifestFactory.cs:378–415`) ; les résumés `not_applied/error` et `saaia_table_repair_attempt` ne sont pas projetés dans le canonique examiné. Le résultat publié peut donc perdre le fait qu’une réparation nécessaire a échoué. **RED complémentaire :** table sparse, erreur régionale contrôlée, résultat initial conservé à l’identique et état dégradé durable avec raison ; un simple `done/ok` ne doit pas effacer cet état. Une politique explicite peut autoriser une publication partielle, sans prétendre que la table est complète.

Les diagnostics OCR Docling assimilent aussi la présence d’un timing à une observation de pages : `BuildDoclingOcrDiagnostics` emploie `timings["ocr"].Count` comme `AttemptedPageCount`, sans tenir compte du `Scope`, et met le nombre de pages candidates à zéro en mode automatique (`DocumentExtractionPolicy.cs:57–82`). Aucun identifiant de page candidat/tenté n’est fourni dans ces tableaux. L’absence de timing est rendue par zéro. Il faut distinguer les compteurs de tâches moteur des pages observées, et l’inconnu de zéro réel. Un RED doit fournir un timing `scope=document/count=1` pour plusieurs pages, puis aucun timing, et interdire une fausse précision de couverture par page.

Le projecteur Docling produit `ExtractionTextStatus=ok`, sparse=false et OCR-candidate=false pour tout chunk construit (`DoclingCanonicalRetrievalProjector.cs:627–629`). La garde accepte ensuite tout chunk canonique non vide après le contrôle navigation (`IngestionWorker.cs:410–415`). C'est utile pour ne pas supprimer des faits courts, mais ce marqueur `ok` ne doit pas être présenté comme la vérification de leur lisibilité/fidélité. Les flags de réparation et confiances existent ; leur portée doit rester visible.

La sonde de couverture par unités répond correctement **indisponible** lorsque les chunks Docling n'ont pas `SourceUnitOrdinals` (`IngestionProjectionStage.cs:103–108`), ce qui évite un faux zéro. Elle ne remplace cependant pas une couverture des blocs/cellules/pages canoniques. Aucune sonde ne doit annoncer « 100 % fidèle » sur la seule base du nombre de chunks publiables ou des unités natives représentées.

**Tests :** erreur natif contrôlée + Docling réussi, OCR activé sans timing retourné, chunk très court fiable, chunk corrompu canonique conservé avec statut qualité inconnu/dégradé et politique d'accès claire. Comparer disponibilité et counts durablement ; ne pas résoudre le problème en réintroduisant une suppression heuristique de toutes les données courtes.

### C10 — Figures, formulaires et tableaux : conservation structurelle et capacité réelle à distinguer

[Ancrage C10](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/DocumentIntelligence/CanonicalNativePdfImageInventoryReconciler.cs:92>).

**Limites visibles dans les sources, sans extrapolation d'une perte réelle déjà observée.** Le canonique conserve des figures, captions et un inventaire natif ; ce dernier ne fournit que placement/type/provenance, pas une lecture de leur sens (`CanonicalNativePdfImageInventoryReconciler.cs:92–118`). Le projecteur retrieval parcourt les blocs et les cellules de tableaux, pas `page.Figures`. Une figure sans texte/caption extraite n'est donc pas comprise par le RAG textuel. L'inventaire d'images n'est pas une extraction multimodale.

`key_value_items` et `form_items` sont désérialisés en `DoclingContentItem` et présents dans la traversée, mais n'ont pas de projection de valeur dédiée dans les adaptateurs/projecteurs examinés (`DoclingModels.cs:103–107`, `DoclingDocumentTraversal.cs:13–14`, `DoclingCanonicalDocumentAdapter.cs:62–66`). Les éventuels enfants déjà présents dans `texts` peuvent être conservés ; les valeurs propres aux objets non modélisées ne doivent pas être déclarées prises en charge. Un témoin JSON réel de formulaire est nécessaire avant de définir la correction de schéma.

Les cellules sont ancrées et leurs spans conservés ; le texte de retrieval reste une concaténation de cellules par ligne, valeurs vides supprimées (`DoclingCanonicalRetrievalProjector.cs:311–351`). Les cas de cellules fusionnées, en-têtes multi-lignes, plusieurs tableaux voisins et tableaux multi-pages doivent vérifier les **relations**, pas seulement la présence de mots. Ce point est une limite de validation, pas la preuve que tous les tableaux sont faux.

### C11 — La présence d’un sommaire sur une page transforme toute récupération en navigation

**Établi statiquement ; priorité haute pour les pages mixtes.** [PageContainsNavigationStructure](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/DocumentIntelligence/CanonicalNativeTextCoverageReconciler.cs:657>) retourne true dès qu’une table de la page porte le label `document_index`. Le hint est attribué au bloc récupéré indépendamment de son texte ou de sa région (`:206–208`). Le projecteur reprend ce hint comme rôle de l’atome (`DoclingCanonicalRetrievalProjector.cs:259/450–458`), puis le chunk devient navigation (`:589–594`). La garde de publication l’écarte avant même le traitement canonique (`IngestionWorker.cs:401–407`).

Ainsi, sur une page avec une courte table d’index et un vrai paragraphe absent de Docling, la récupération peut contenir exactement le paragraphe recherché tout en étant retirée de l’index. Le hint de page ne prouve pas le rôle de chaque région. Le test existant de récupération de texte de navigation ne couvre pas cette page mixte.

**RED :** document_index et corps substantiel présents sur la même page ; couche native contenant une phrase de contenu supplémentaire sans leader de sommaire. Exiger qu’elle reste accessible avec rôle inconnu/contenu prudent, sans classification fondée uniquement sur l’autre table. Conserver un véritable ajout de ligne de sommaire et ses liens de navigation comme témoin distinct.

## Garde-fous existants à conserver

- Le client Docling refuse une réponse HTTP non réussie, un statut non-success, des erreurs déclarées, l'absence du JSON ou un autre schema-name ; sa réponse est bornée et les annulations sont transmises. Il n'y a pas ici de repli silencieux automatique « Docling échoue donc publier natif ».
- Les identités tenant/document/révision/hash sont validées pour le bundle et les anchors. Le canonique multi-provenance sait déjà respecter les charspans Unicode ; cette fonction doit être partagée avec les vues secondaires, sans la remplacer par du comptage de mots.
- La publication relationnelle garde une transaction pour ses artefacts et les contrôles d'état/version ; un résultat de publication refusée ne compte pas comme succès. Les points Qdrant sont écrits avec `wait=true` ; le filtrage des versions réduit le risque d'anciens points de versions différentes.
- Les diagnostics de chunks projetés/publiés sont séparés ; les mappings de provenance inconnus ne sont plus annoncés comme couverture nulle. Le signal d'association physique inconnue récemment ajouté est une bonne base à réutiliser pour les récupérations non localisées.

## Refonte justifiée et bornée

1. **Snapshot source immuable et contrat d'exécution effectif.** Adresser les octets de travail par SHA ; produire un run-id/fingerprint incluant version de code, options réellement exécutées, moteurs/modèles attestés et cibles d'index. Une reprise ne consomme que ce même run.
2. **Canonique comme source des vues de lecture.** Conserver observations Docling et native séparément avec leurs régions, relations, variantes et divergences. Dériver pages/unités/sections/chunks depuis ce même objet ; une hypothèse de layout ou un voisinage de page ne doit pas devenir une association sémantique certaine.
3. **Séparer fidélité et sélection.** Garder la donnée brute et les faits incertains accessibles avec état ; séparer les critères de bruit avéré, d'éligibilité d'un candidat autonome, d'indexation et de preuve. Une répétition ou un score de couverture élevé n'est pas une autorisation de supprimer une valeur.
4. **Ledger de couverture explicite.** Pour chaque page/bloc/cellule/figure : inventorié, extrait, divergent, non pris en charge, projeté, publié SQL, publié vectoriel. Compter les omissions connues et inconnues avec leurs raisons ; ne pas assimiler succès technique, couverture du texte et compréhension des images.
5. **Publication d'un build vérifié.** Indexer dans une génération inactive identifiée par fingerprint, vérifier l'ensemble attendu des points/checksums/modèles, puis activer sa référence transactionnelle. Éviter d'écraser une version déjà visible pendant une reconstruction. La reprise répare les points manquants et refuse les états incompatibles ; une transaction distribuée magique n'est pas présumée.
6. **Responsabilités explicites.** Le worker coordonne snapshot/lease/annulation ; les stages retournent résultats typés et raisons de dégradation ; un validateur de couverture décide si une génération est publiable ; un publisher et un mécanisme de réparation possèdent SQL/Qdrant. Cette séparation répond aux invariants de fidélité et reprise, elle ne vise pas uniquement à réduire le nombre de lignes d'un fichier.

## Validation bout en bout proposée avant nouveau test payant

**Aucune des séquences ci-dessous n'est déclarée exécutée par cet audit.** Capturer d'abord les RED causaux C01–C09 avec services simulés ; ajouter les témoins de non-régression et documenter les limites C10. Puis exécuter le vrai worker sur une base et une collection locales isolées avec le vrai sidecar Docling et ses modèles identifiés.

Corpus de contrôle public/synthétique : PDF texte natif à deux colonnes et plusieurs sujets, scan pur, mixte texte+image, avertissement marginal répété, donnée d'un mot, valeur numérique altérée, page blanche et page absente simulée, item multi-provenance Unicode, tableaux à cellules fusionnées et multi-pages, formulaire et figure avec/sans texte. Chaque fait de contrôle porte une page/région attendue établie depuis le PDF rendu, sans dépendre du résultat d'un modèle.

À chaque étape : comparer données littérales, pages/régions/anchors, composition, scopes et relations inconnues. Après le vrai `done`, lire les chunks canoniques **via les endpoints**, vérifier PostgreSQL et Qdrant, puis faire une recherche de termes distinctifs et une lecture de source. Aucun LLM n'est nécessaire pour démontrer qu'un fait a traversé cette chaîne. Le contrôle ne doit pas semer directement les tables et annoncer ensuite que le worker est validé.

Injecter pause/restart à chaque lot, arrêt après Qdrant avant SQL, perte d'un point acquitté, nouvelle collection, changement de config/modèle et mutation de source. Vérifier qu'aucun état partiel n'est activé comme complet, que les générations précédentes restent lisibles et que les sondes expliquent le refus ou la réparation sans contenu documentaire dans les journaux ordinaires.

La qualité sémantique d'un planning multisources restera une acceptation ultérieure distincte. L'audit ne la déclare ni réussie ni impossible et n'attribue pas les défauts d'ingestion à la capacité du LLM.

## Contre-revue et ancrage de lecture

Une contre-revue indépendante par l’agent OCR a confirmé les chemins C02 et C11 sur ces sources, sans exécution. Réciproquement, les chaînes OCR-01, OCR-02 et OCR-04 du [rapport OCR natif](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/artifacts/ingestion-ocr-audit-20261025/ocr-native-audit.md>) ont été relues dans les fonctions appelées : résultat complet remplacé par un résultat image partiel, déduplication de valeurs/négation différentes, et substitutions lexicales appliquées au natif. Aucun RED n’est ajouté à la liste des tests exécutés par ces revues.

Les empreintes suivantes ancrent la lecture actuelle, sans constituer un build ni un résultat de test :

| Source | SHA256 |
| --- | --- |
| `Ingestion/IngestionWorker.cs` | `1AD48C3D4F684E921ADEB54FC045A09E8B874054BD08E4F2BC4519A4F482EEE8` |
| `Ingestion/IngestionExtractionStage.cs` | `939B3A0CE98FF4F77FDAD7E266E74F7B2DE380DAE0C4088B94E29ABAC646BD9A` |
| `Ingestion/IngestionProjectionStage.cs` | `BA620646A27C8B64F1E884D3A7B2E89FFB22C7EAB6F8BECBE1268574C1FB70BF` |
| `Ingestion/DocumentExtractionPolicy.cs` | `0C6D87BE6BA9358A5811C84EF965D2682BCD0EE5FA579532B61B53CAEBF57917` |
| `Ingestion/DocumentFoundationRepo.cs` | `7772713D82EC37FB481F6A3489C5ECFF0DFB9DD794D72D20431FAFFC1FC9A329` |
| `DocumentIntelligence/CanonicalRepeatedPageFurnitureClassifier.cs` | `AED82AC81CAE1586AB7A1CAD49100181F0953AA251CCD3E77B82BB36071A0A5B` |
| `DocumentIntelligence/CanonicalNativeTextCoverageReconciler.cs` | `1932AFE53743366C95431DC3309F6DB2BCBCA3DAC9B776F2DE3EC48EC49639AE` |
| `DocumentIntelligence/CanonicalNativeLayoutReconciler.cs` | `CCE7D9C679005B49C794809F276B09A9076BB6BB9145D26A753E37FEB4D9DF03` |
| `DocumentIntelligence/Docling/DoclingCanonicalRetrievalProjector.cs` | `D9FFCD6809EB2B6817AAD6DD800DFEB18D720F9AE0E803FC46826ADBEF7CABA9` |
| `DocumentIntelligence/Docling/DoclingCanonicalDocumentAdapter.cs` | `A443F010E3DFB8476E64447434A9819DC12764977ACD2E4A61ACA83EA77691F8` |
| `DocumentIntelligence/Docling/DoclingLegacyExtractionAdapter.cs` | `4E996F9756C2286E33CFE87EEADA33969C1B8A47056C90FF3739DBD0662D902E` |
| `DocumentIntelligence/Docling/DoclingIngestionStageManifestFactory.cs` | `5FB9656FB9061B93FA203B45230634BC38A99F9140E664EE15052811DB9C97A1` |
| `DocumentIntelligence/Docling/DoclingClient.cs` | `12774B5772587470F9F127581F4368887CCEDB90517495A7CEB10DB7C43941B7` |
| `Retrieval/RetrievalChannels.cs` | `ABE025127B66C7C98C7F2DB79562CC20AB7C80E21A787D4750E7BBAF5CCB99BD` |
