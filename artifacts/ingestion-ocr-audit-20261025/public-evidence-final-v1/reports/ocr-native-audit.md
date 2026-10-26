# Audit OCR et extraction native — 2026-10-25

## Portée et niveau de preuve

Audit du code actif en lecture seule : `PdfExtractor`, `PdfOcrTextExtractor`, `OcrNoiseFilter`, `PdfTextSanitizer`, options/readiness, admission dans `IngestionWorker`, diagnostics conservés et tests correspondants. L'orchestration générale, Docling, réconciliation canonique et publication SQL/vector font l'objet de l'audit séparé de l'autre agent.

**Aucun test, build, processus OCR, service, SQL, appel modèle ou réseau n'a été lancé pendant cet audit. Aucun fichier produit n'a été modifié.** Les contre-exemples ci-dessous sont des chemins déterministes déduits du code ; ils restent à capturer en RED sur la chaîne réelle. Ils ne sont pas présentés comme des incidents observés sur le PDF réel. Aucun `AGENTS.md` applicable trouvé dans le chemin du dépôt ou sous `backend`/`tools` ; le fichier global `C:/Users/maxim/.codex/AGENTS.md` lu est vide.

Les 14 faits du contrôle natif précédent démontrent leur conservation sur les pages sélectionnées. Ils ne valident ni tout le document, ni le parcours OCR scanné/mixte, ni les tableaux/figures, ni toutes les langues, rotations et dégradations possibles.

## Conclusion

Le chantier structurel est justifié. Les principales pertes restantes ne se situent pas dans Tesseract lui-même : les décisions **après reconnaissance** peuvent supprimer une valeur nouvelle, remplacer une extraction plus complète par une extraction partielle, effacer une ligne entière pour un mot incertain ou réécrire une identité documentaire dans du texte natif parfaitement valide. Ces comportements peuvent retirer les preuves avant tout appel du modèle.

Priorités recommandées : conservation des résultats acquis par page ; identité/provenance du texte brut ; suppression des décisions sémantiques irréversibles fondées sur une ressemblance ou une faible confiance ; diagnostics qui distinguent couverture tentée, réussite, échec et incertitude.

## Constats établis par lecture du chemin d'exécution

### OCR-01 — P1 — Un deuxième OCR partiel peut écraser le premier OCR complet réussi

**Chemin établi.** [IngestionWorker.cs:1257](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Ingestion/IngestionWorker.cs:1257>) admet le résultat OCR complet. [ResolveImageOcrMergeBase:1904](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Ingestion/IngestionWorker.cs:1904>) choisit pourtant le natif vide lorsque toutes ses pages sont vides. L'admission du résultat image compare alors ce résultat au natif vide, puis remplace `ocrExtraction` sans comparaison avec l'OCR complet déjà acquis : [1290](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Ingestion/IngestionWorker.cs:1290>), [1296](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Ingestion/IngestionWorker.cs:1296>).

**Contre-exemple reproductible proposé.** PDF de trois pages sans texte natif ; premier moteur : texte valide sur trois pages ; deuxième moteur : une page reconnue et deux échecs ou deux pages hors budget. Son texte contient plus de mots que le natif vide : il est accepté et les deux pages précédemment remplies redeviennent vides. Le test existant [PdfOcrTextExtractorTests.cs:1343](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend.Tests/PdfOcrTextExtractorTests.cs:1343>) exige actuellement le choix du natif vide ; il ne teste pas la perte aval.

**Correction à préparer.** Réconciliation des candidats **par page/zone**, gardant tout résultat acquis non réfuté. Une amélioration locale ne doit pas annuler les autres pages. Le test doit traverser la sélection finale des deux moteurs et vérifier les trois pages, leurs textes et leurs sources.

### OCR-02 — P1 — Les ressemblances utilisées pour dédupliquer peuvent éliminer un fait différent

**Chemin établi.** [ExtractNovelOcrLines:1650](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:1650>) ignore une ligne si `LooksLikeLowNoveltyOcrLine` l'estime suffisamment couverte. [1671–1721](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:1671>) utilise des ensembles distincts, des seuils allant de 55 % à 80 %, une fenêtre locale et une comparaison approchée de mots ; aucune identité des valeurs, négations ou relations n'est requise.

**Contre-exemples déterministes proposés, non exécutés.**

- Natif : `Maximum pressure 10 bar during normal operation.` ; OCR d'une autre zone : `Maximum pressure 20 bar during normal operation.`. Six des sept tokens sont couverts ; la valeur nouvelle est perdue comme doublon.
- Natif : `The pump may operate during scheduled maintenance windows.` ; OCR : `The pump may not operate during scheduled maintenance windows.`. La négation ajoutée reste sous le quota de nouveauté et la ligne peut être éliminée.

**Autre remplacement risqué du même groupe.** [HasSufficientNativeCoverageForReplacement:1105](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:1105>) accepte 70 % de couverture des tokens natifs distincts ; [HasStrongBidirectionalCoverageForLayoutReplacement:1126](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:1126>) en accepte 82 %. [ExtractComparableTokens:1170](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:1170>) ignore tous les tokens de moins de deux caractères après normalisation, donc une valeur isolée `5` ne compte même pas dans la conservation. Les ponctuations et diacritiques sont aussi retirés pour ces comparaisons : [2070](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:2070>).

**Correction à préparer.** La ressemblance peut proposer un alignement ou une incertitude ; elle ne suffit pas à prouver qu'un fait est déjà conservé. Garder les variantes non équivalentes et leur origine ; séparer la déduplication certaine d'une réparation candidate. Tester changements de nombres, unités, signes, décimales, négations et identifiants, avec témoins de vrais doublons.

### OCR-03 — P1 — Une faible confiance sur un seul mot peut supprimer toute une ligne

**Chemin établi.** [ParseLowConfidenceTsvLines:1067](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:1067>) classe une ligne faible dès qu'un mot d'au moins cinq caractères a une confiance inférieure à 45, même lorsque tous les autres mots sont fiables. [FilterLowConfidenceImageOcrText:1022](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:1022>) supprime ensuite toute ligne `.txt` partageant au moins 67 % des tokens avec cette ligne TSV. Le rapprochement ignore les boîtes et la position ; les tokens numériques seuls ne servent pas à constituer la ligne de confiance.

**Conséquence établie.** Une ligne contenant une restriction, plusieurs valeurs ou un statut peut disparaître à cause d'un mot incertain. Deux lignes distinctes qui se ressemblent peuvent aussi être affectées par le même groupe TSV. Il n'existe pas de stockage des mots exclus et de leurs confiances dans ce résultat ; le répertoire temporaire est supprimé. Si tout est retiré, le diagnostic devient simplement `ocr_empty/no_text` : [449–467](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:449>).

Le test existant [564](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend.Tests/PdfOcrTextExtractorTests.cs:564>) confirme l'intention de supprimer une ligne mal reconnue ; il ne démontre pas que tous les faits de cette ligne sont inutiles.

**Correction à préparer.** Conserver le texte OCR brut et les tokens/boîtes/confiances ; marquer l'incertitude et restreindre l'emploi comme preuve lorsque nécessaire. Une ligne incertaine n'est pas une ligne vide. RED : une directive avec un seul nom peu confiant et plusieurs mots/valeurs fiables ; deux lignes proches à des positions différentes ; sortie tout filtré distincte d'une sortie moteur réellement vide.

### OCR-04 — P1 — Des substitutions lexicales changent des références sans preuve de corruption

**Chemin établi.** [PdfTextSanitizer.ForStorage:45](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfTextSanitizer.cs:45>) applique des réparations métier/lexicales à tout texte de stockage. Cette fonction est appelée aussi sur le **texte natif**, avant extraction des unités : [PdfExtractor.cs:29](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfExtractor.cs:29>).

- `TEC 123` et `IEG 123` sont réécrits `IEC 123` sans connaissance de l'organisme réel : [69–95](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfTextSanitizer.cs:69>).
- `NEPA 70…` devient `NFPA 70…`, `DOF HDBK` devient `DOE HDBK`.
- Des mots/codes tels que `recommende`, `groundfault` ou `shortcircuit` sont remplacés via dictionnaire : [154](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfTextSanitizer.cs:154>).

Il s'agit d'une transformation certaine de l'entrée, pas d'une preuve que le document actuel contient une référence erronée. Les tests [TextEncodingSanitizerTests.cs:229](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend.Tests/TextEncodingSanitizerTests.cs:229>) exigent aujourd'hui ces substitutions ; ils valident la règle, pas son exactitude sur une référence inconnue. L'identité PDF et un checksum du texte transformé ne prouvent pas que ce remplacement est fidèle au document.

**Correction à préparer.** Garder les normalisations de transport déterministes distinctes des hypothèses de réparation. Les variantes lexicales peuvent alimenter la recherche, avec provenance, sans réécrire la preuve canonique. RED sur un texte natif déclarant explicitement un code produit `TEC 123`, un organisme `IEG 123`, un identifiant exact `shortcircuit`, ainsi que sur les véritables réparations proposées devenues variantes non probantes.

### OCR-05 — P1/P2 — Les faits courts restent filtrés selon leur forme ou une liste anglaise

**Chemin établi.** [ExtractNovelOcrLines:1628](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:1628>) élimine les lignes de moins de huit caractères ou moins de `minWords` sauf catégorie « importante ». L'exception repose sur une liste anglaise de commandes [19–46](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:19>), quelques codes ou un titre comportant au moins deux mots [1832](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:1832>), [1949](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:1949>).

Avec le seuil par défaut de trois mots, `Prohibited`, `Approved`, `5 bar`, `24 V` ou un statut français d'un mot n'entrent pas dans ces exceptions et sont perdus dans la branche d'ajout. Le statut anglais `STOP` est conservé : la qualité de lecture ne justifie pas cette différence.

**Autre suppression native à tester.** [OcrNoiseFilter.cs:1163](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/OcrNoiseFilter.cs:1163>) retire une ligne de un à trois tokens constitués de lettres isolées si au moins une est minuscule, et retire deux tokens minuscules isolés en début de ligne [1250](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/OcrNoiseFilter.cs:1250>). Une formule native `x y z`, une légende de variables ou `p q are independent variables` perd donc du texte sans preuve géométrique de bruit. Les gardes de conservation du layout précèdent ce filtre et ne protègent pas de cette suppression ultérieure.

**Correction à préparer.** Garder le contexte court avec son corps/zone fiable, ou l'exposer comme texte incertain ciblé ; ne pas en faire automatiquement un candidat autonome. Tester statuts multilingues, valeurs avec unités, variables, commandes connues et vrais débris de glyphes. Le maintien de restrictions de qualité est justifié ; la suppression irréversible de faits uniquement courts ne l'est pas.

### OCR-06 — P2 — La couverture et la confiance agrégées peuvent paraître bonnes malgré un échec OCR

**Chemin établi.** [ResolveImagePageOcrCoverageStatus:791](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:791>) retourne `complete` dès qu'aucune page n'est sautée, indépendamment des échecs de rendu/reconnaissance. `BuildImagePageOcrDiagnostics` ne remonte pas automatiquement les timeouts individuels en `TimedOut` agrégé : [770](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:770>). Une campagne d'une page tentée puis `render_failed/timeout` peut donc avoir `CoverageStatus=complete`, `TimedOut=false`, `FailureReason=null`, tout en conservant l'échec dans le détail de page.

Les détails sont réellement conservés en SQL : [DocumentFoundationRepo.cs:1826](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Ingestion/DocumentFoundationRepo.cs:1826>). Le problème n'est donc pas une absence complète de sondes, mais l'ambiguïté de leurs niveaux agrégés.

De plus, [ExtractionQualityDiagnostics.cs:86](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/ExtractionQualityDiagnostics.cs:86>) produit des valeurs fixes de `ExtractionConfidence` telles que 1,00/0,86/0,78 à partir de quantités/statuts. Ce n'est pas une confiance OCR calibrée. L'appel [DocumentsEndpoints.ExtractionQualityPages.cs:282](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Endpoints/DocumentsEndpoints.ExtractionQualityPages.cs:282>) n'intègre pas l'échec image dans cette décision de revue ; il ajoute son diagnostic à côté ensuite.

**Correction à préparer.** Distinguer couverture planifiée/tentée/réussie/échouée/partielle, texte moteur reçu, texte retenu, incertitude et besoin de revue. Nommer les scores heuristiques comme tels et ne pas les assimiler à une probabilité de fidélité. RED : toutes pages échouées, timeout individuel, zéro texte après filtrage, page avec corps natif indexé mais tableau image non reconnu.

### OCR-07 — P2 — L'origine des fragments et le texte brut ne survivent pas à la fusion

**Chemin établi.** Dès qu'une page change, [MergeImageOcrText:875–902](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:875>) affecte `RawText = text` au texte fusionné, puis recalcule les signaux à partir de celui-ci. L'ancien `RawText` et ses statistiques d'encodage ne sont plus dans le résultat. Les champs `NativeLayoutText/Blocks` restent ceux de la couche native, tandis que `Text` inclut un texte OCR ajouté ou remplacé. Le TSV n'est employé que pour le filtrage, ses boîtes et confiances ne sont pas transportées. Les lignes nouvelles sont ajoutées en fin de page [859–865](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:859>), sans ordre géométrique ni association à une figure/tableau.

**Limite certaine.** On peut conserver une page/source PDF, mais on ne peut plus expliquer précisément quel moteur/zone a apporté chaque assertion ni reconstruire l'avant/après à partir de ce seul objet. L'effet concret sur l'ordre d'un document mixte reste à mesurer ; aucune corruption d'un document réel n'est revendiquée ici.

**Correction à préparer.** Modèle de candidats/segments conservant page physique, zone, moteur/version, texte brut, texte normalisé, confiance, décision et motif ; rendre le texte final comme une projection. Aucune duplication de tous les pixels n'est nécessaire pour démarrer : référence au PDF immuable et coordonnées suffisent pour l'ancrage initial. Les images déjà inventoriées ne deviennent pas automatiquement du contenu compris.

### OCR-08 — P2 — Une exception tardive jette les succès des pages précédentes

**Chemin établi.** Le `try` englobe toute la boucle. Une exception de lecture/traitement d'une page après plusieurs réussites atteint [504–516](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:504>) : les diagnostics mentionnent `pageText`, mais `Extraction=null` est retourné et aucun résultat partiel n'est fusionné. Les échecs normaux des processus, eux, utilisent `continue` et conservent les pages précédentes.

**Correction à préparer.** Isoler les échecs par page ; conserver les résultats acquis, marquer la couverture partielle et appliquer une politique explicite de publication/revue. Une annulation utilisateur doit continuer à être propagée. RED avec page1 reconnue, page2 fichier de sortie illisible, puis conservation du résultat de page1 sans inventer le contenu de page2.

### OCR-09 — P2 — L'OCR complet est admis sans conservation des pages ni qualité du texte réel

**Chemin établi.** [ShouldApplyOcrExtraction:294](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:294>) compare nombre global de tokens et rang de qualité ; aucun contrôle page par page ne garantit les faits natifs. [BuildExtractionFromSidecarText:219](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:219>) retire tous les formfeeds finaux, numérote les pages par leur position et ne reçoit pas le nombre physique attendu. [WithImageCountsFromNative:2207](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:2207>) enrichit les seules pages présentes ; il ne restaure pas celles absentes. Un sidecar incomplet ou ambigu peut être admis s'il contient assez de mots. Le comportement exact de la version OCRmyPDF installée sur les PDF mixtes doit être capturé ; il n'a pas été vérifié dans cet audit.

Autre incohérence établie : la qualité de chaque page sidecar est calculée par `FromCounts` [241](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:241>) alors que la couche native utilise `FromSanitizedText` et contrôle les caractères de remplacement/contrôle [PdfExtractor.cs:986](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfExtractor.cs:986>). Un sidecar suffisamment long contenant encore des caractères de remplacement peut ainsi recevoir le statut `ok` par comptage.

**Correction à préparer.** Contrat explicite des pages, positions/vides/skips et marqueurs du moteur ; qualité calculée sur le texte reçu ; admission conservatrice par page. RED : page native critique absente d'un OCR plus long, dernière page vide, page OCR sans séparateur attendu, sidecar avec caractères de remplacement. Ne pas déduire la qualité du seul volume.

## Risques conditionnels à confirmer avant correction

### OCR-R1 — Délais et ressources des processus

Les appels principaux drainent stdout/stderr concurremment, utilisent des annulations liées et tentent `Kill(entireProcessTree:true)` : ce sont des protections présentes, pas du code manquant. Cependant [RunProcessWithDiagnosticsAsync:2327](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:2327>) lit entièrement les deux flux en mémoire, puis ne tronque qu'à la fin. Après timeout, le kill avale ses exceptions [2367](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:2367>) et les lectures/drains n'ont pas de délai propre [2117](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:2117>). Un descendant conservant les handles ou un kill refusé peut prolonger la fin au-delà du délai configuré. Les fichiers `.txt/.tsv` sont aussi lus sans borne de taille. Ce risque n'a pas été reproduit ici.

La découverte de langues [1483](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:1483>) attend la fin avant de lire les deux flux ; le délai de deux secondes limite l'attente du processus mais un outil très bavard peut être tué et être pris pour une liste de langues vide. À unifier avec un lanceur borné, sans multiplier les wrappers.

Tests utiles : enfant produisant beaucoup de sortie ; descendant conservant stderr ouvert ; annulation ; timeout ; erreur de démarrage ; sortie trop volumineuse ; aucune persistance de contenu OCR dans les sondes publiques.

### OCR-R2 — Langue unique choisie pour un document multilingue

[ResolveLanguagesForDocument:1183](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:1183>) peut réduire une liste explicite `fra+eng+deu` à `deu+eng` lorsqu'un allemand dominant est détecté. [BuildLanguageCorpus:1589](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:1589>) prend le nom de fichier, son dossier et les huit premières pages natives ; la même liste est appliquée à chaque page OCR. Le test existant [751](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend.Tests/PdfOcrTextExtractorTests.cs:751>) exige cette optimisation. L'impact réel sur les pages minoritaires n'est pas démontré ici. Conserver le choix explicite de l'administrateur et/ou détecter par page avec repli, puis comparer précision et temps sur corpus multilingue réel avant de changer les défauts.

### OCR-R3 — Ordre et rotation OCR sur documents complexes

La branche image rend la page entière en gris puis lance Tesseract avec un seul mode de segmentation, par défaut `--psm 11` : [400](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:400>), [425](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Pdf/PdfOcrTextExtractor.cs:425>), [IngestionOptions.cs:91](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Options/IngestionOptions.cs:91>). Cette branche n'a pas les options explicites de rotation/deskew du premier moteur. Elle peut ensuite supplanter ce premier résultat (OCR-01). Sans test réel de scan à 90°, inclinaison, tableaux et colonnes, aucune validation d'ordre universel n'est possible. Ne pas changer de mode à l'aveugle : comparer les sorties et leurs boîtes sur les mêmes pixels, conserver la variante sûre et exprimer l'incertitude.

## Tests et état réellement validé

Le fichier `PdfOcrTextExtractorTests.cs` couvre beaucoup de politiques unitaires, commandes/configuration, plans/budgets, diagnostics et quelques interactions de fusion. Cela apporte de vrais contre-contrôles mais certaines assertions exigent précisément les règles contestées ici (liste de langues réduite, native vide après OCR complet, suppression d'une ligne faible).

Le test nommé [Real_scanned_pdf_ocr_extracts_text_when_opted_in:27](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend.Tests/PdfOcrTextExtractorTests.cs:27>) **retourne normalement sans exécution** si `SAAIA_TEST_OCR_E2E != 1`. Un PASS de suite Unit ne prouve donc pas que le moteur OCR a été lancé. Son cas activé génère un unique scan anglais et cherche quelques mots ; il ne couvre pas les PDF mixtes, les pertes de pages, négations/valeurs, tableaux, les langues minoritaires ou l'ordre géométrique.

Avant toute revendication d'ingestion validée, conserver séparément : tests unitaires causaux ; outils simulés et erreurs contrôlées ; corpus réel local avec moteurs effectivement exécutés et versions enregistrées ; régression de la chaîne publiée/consultable ; limites explicitement non testées. Le protocole doit indiquer `executed` et les prérequis plutôt que transformer une absence d'environnement en validation OCR.

## Travail structurel recommandé

1. **Contrat de fidélité et RED avant refonte.** Reproduire OCR-01 à OCR-05 et OCR-09 sur des documents synthétiques lisibles à l'œil ; garder témoins de bruit réel et doublons certains. Capturer chaque sortie avant/après et la provenance publiée.
2. **Séparer données brutes, candidats et rendu final.** Une page physique stable porte plusieurs segments/candidats natifs/OCR. Les réparations et décisions deviennent explicites et auditables ; le texte final n'écrase pas le texte source. Les sorties douteuses restent accessibles pour revue sans être promues en preuve fiable.
3. **Extraire des composants cohérents du fichier OCR de 2404 lignes.** Un lanceur de processus borné ; un adaptateur OCRmyPDF ; un adaptateur Tesseract TSV ; une politique de langues ; un planificateur de pages ; un réconciliateur conservateur et un constructeur de diagnostics. Interfaces axées sur ces responsabilités, sans simple dispersion en fichiers `partial`.
4. **Raccorder les sondes aux décisions.** Par page/zone : moteur/version/configuration, durée, statut, tailles/hashes bruts et retenus, nombre de tokens incertains/écartés, motif d'alignement/remplacement/repli, liens vers le PDF immuable. Projections publiques limitées à ces métadonnées ; texte et images réservés aux preuves privées autorisées. Ne pas appeler « confiance OCR » un score de densité.
5. **Corpus d'acceptation local.** Natif/scanné/mixte, dernière page vide, page sautée, un/multicolonnes, rotations/inclinaisons, tableaux chiffrés, figures avec labels, variables/codes, accents/non-latin, changements de valeurs/signes/négations, langue minoritaire tardive, sortie de moteur partielle/erronée. Mesurer couverture et exactitude de faits connus, ancrage page/zone, ordre, durée et ressources ; aucune API payante nécessaire pour ces étapes.

La mise en place d'un stockage SQL de segments et décisions peut être justifiée par cette provenance et par la reprise partielle ; elle ne remplace pas les invariants de conservation. La migration précise doit suivre le rapprochement avec le modèle canonique existant, objet de l'audit parallèle, pour éviter deux sources de vérité concurrentes.

## Empreintes des sources principales lues

| Fichier | Lignes | SHA256 |
| --- | ---: | --- |
| PdfOcrTextExtractor.cs | 2404 | `47E5C5EA8BCFD0638AAC46E5C8DCFFB11D51E43213B779E9C453971F527CE56E` |
| OcrNoiseFilter.cs | 1476 | `CC230F960749549DF5A8AEFC3BAFD727FE9E9E0607321411EE8649091B4CFECE` |
| PdfTextSanitizer.cs | 184 | `FFBE2AF2FE622A76D6C35FD10CABAA6C762329205EFCCE44FCBB1AA6A281B448` |
| PdfExtractor.cs | 1227 | `B78408DBA1F8F97F9BA2151DEF30F37BFBA304971855B289A23FB14E113C613C` |
| ExtractionQualityDiagnostics.cs | 111 | `2B8C825AB4F9466E4B73C2DCA4738DE78BFD98F4502C3FF6DD984101468BD47F` |

Ces empreintes ancrent la lecture, sans prétendre ancrer un nouveau build ou une nouvelle validation d'exécution.

## Contre-revue indépendante

L'autre agent a relu les chemins OCR-01, OCR-02 et OCR-04 dans les sources actives et confirme leur causalité statique, sans défaut matériel identifié dans ces conclusions. Il confirme les couvertures 6/7 pour le changement `10` → `20` et 8/9 pour la négation ajoutée. Pour les futurs RED de déduplication, garder explicitement la branche d'ajout (textes natifs courts, sans déclencher préalablement le remplacement de layout), puis tester les remplacements séparément. Cette contre-revue n'est pas une exécution.
