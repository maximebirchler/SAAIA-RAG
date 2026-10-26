# Qualification de l’ingestion — clôture du 26 octobre 2026

L’ingestion passe les contrôles de cette campagne sur la pile Linux CPU réellement exécutée, en environnement **Production avec configuration signée**. Les corrections couvrent la détection des fichiers, les générations, la publication, la purge ciblée, le démarrage des dépendances et la reprise des conversions Docling transitoirement indisponibles. Les résultats finaux ci-dessous portent sur les mêmes sources finales ; les échecs intermédiaires sont conservés et expliqués.

Cela autorise à passer à la campagne du planning. Cela ne constitue ni une approbation générale du produit, ni une garantie pour tous les PDF et toutes les machines. **Le statut produit reste `TESTE_NON_APPROUVE`. Aucun appel de LLM payant et aucun nouveau test du planning n’ont été effectués dans ce lot.** Aucun déploiement dans la pile de production existante n’a été réalisé.

## Résultats finaux réellement observés

| Contrôle | Résultat final | Ce qu’il établit |
|---|---|---|
| Suite .NET des quatre projets | **5 323 PASS, 0 échec, 2 non exécutés** | Non-régression Backend, Contracts, ClientCore et Client sur les sources finales |
| Suite PostgreSQL isolée | **350 PASS, 0 échec, 2 non exécutés historiques** | Transactions, baux, générations, publication, suppressions conditionnelles et registre GC sur un vrai PostgreSQL |
| Suite Python d’ingestion | **180/180 PASS** | Contrats des outils de qualification et des vérificateurs ; ce nombre ne remplace pas les tests réels |
| Corpus Gold Linux | **12 PDF, publication et contrôles PASS** | Passage par le vrai worker, extraction/Docling, TEI, SQL, Qdrant et recherche canonique |
| Livre de recettes Linux | **36 pages, publication et contrôles PASS** | Ingestion du document long et cohérence de sa publication |
| Recherche puis lecture sourcée | **90/90 faits Gold et 16/16 faits recettes PASS** | 106 faits annotés retrouvés et lus avec les identités document/révision/plan/chunk attendues ; 163 tentatives HTTP locales |
| Scanner seul | **PASS** | Admissions automatiques, modifications et suppressions du scénario, sans admission manuelle par l’API |
| Watcher seul | **PASS** | Admissions et remplacements par événements ; fichier manquant signalé avec ancienne publication conservée |
| Scanner + watcher, deux workers | **PASS** | Parcours combiné ; deux jobs simultanément `running` avec deux propriétaires distincts réellement observés |
| Cycle de vie et GC sur vrai Qdrant | **PASS** | Nouvelles générations, ancienne publication protégée, retrait, suppression exacte, écriture tardive puis recollecte |
| Remplacement échoué | **PASS** | Une nouvelle ingestion défectueuse ne détruit pas la publication précédente utilisable |
| OCR réel dans Linux | **1 sélectionné, 1 exécuté, 1 PASS** | OCRmyPDF, Ghostscript et Tesseract traitent la fixture raster réaliste, avec les mots et l’identifiant exact attendus |
| Identité des chemins Linux | **1 sélectionné, 1 exécuté, 1 PASS** | Deux fichiers ne différant que par la casse, de même taille/date, gardent des identités distinctes |
| Reprise Docling dans Linux | **26 sélectionnés, 26 PASS** | Cas déterministes de reprise, délais, annulation, poignée de fichier et classification des erreurs |
| Signature Production | **5 configurations signées acceptées ; témoin altéré rejeté** | Vraie signature locale et vrai refus de démarrage d’une copie modifiée sur l’image finale |

Les deux non-exécutés de la suite Windows concernent précisément l’OCR externe opt-in et le filesystem Linux : ils ont été exécutés séparément sous Linux, sans réécrire les compteurs Windows. Les deux non-exécutés PostgreSQL sont d’anciens scénarios runtime de recherche, conservés comme tels. Les campagnes se recouvrent : leurs compteurs ne doivent pas être additionnés en une prétendue suite unique. Le watcher seul n’a pas qualifié la suppression finale après grâce ; les profils ont des sous-contrôles distincts. Le scanner seul n’a observé qu’un job simultané, donc son sous-contrôle deux consommateurs reste non exécuté. Le watcher et le profil combiné ont bien observé deux propriétaires simultanés, sans prétendre mesurer du calcul OCR parallèle.

## Ce qui était incorrect et ce qui a changé

### 1. Le scanner pouvait confondre « non parcouru » et « absent »

Avec une limite de fichiers par scan, l’ancien code reprenait toujours le début de l’arborescence. Un fichier encore présent, mais hors de ce premier lot, pouvait devenir `missing`, puis provoquer une suppression. Ce défaut a été reproduit avant correction avec des témoins qui, eux, passaient.

L’inventaire garde maintenant un curseur borné et continue au lot suivant. Il distingue fin de parcours, parcours partiel et erreur. Une absence destructive exige une observation explicite ; accès refusé, root inaccessible et erreur d’entrée-sortie deviennent « inconnu », puis report. Un fichier omis mais présent conserve sa publication. Les comparaisons de chemins suivent la sensibilité du système ; le cache et la déduplication respectent également cette identité.

Le retour du fichier après l’admission d’une suppression nécessitait aussi une protection dans le worker. Seules les suppressions scanner pour absence portent désormais cette condition. Sous bail et verrous, une source revenue conserve sa publication ; le delete est annulé et une nouvelle génération peut être planifiée atomiquement. Une suppression explicite garde son intention propre. Les cas de retour, état inconnu, perte de propriétaire, rollback et supersede sont couverts ; **16 cas PostgreSQL causaux associés sont verts**. Les sondes distinguent une reprise effectivement admise d’un job simplement dépassé.

### 2. Une génération ou un manifeste ne devait jamais redevenir réutilisable

Une suppression physique du document pouvait faire perdre son compteur et permettre de réutiliser des identifiants d’une ancienne génération. D’autres courses d’admission pouvaient appliquer une réservation périmée à une nouvelle identité. La migration 072 conserve maintenant un maximum durable indépendant des cascades. Les admissions utilisent une comparaison atomique de l’identité et de la génération ; une réservation dépassée est remplacée, jamais adoptée comme si elle appartenait au job courant.

Chaque nouveau plan enregistre les identifiants exacts de ses points et scelle son manifeste avant écriture. Le registre refuse l’extension après scellement et les réactivations d’un plan retiré. Le retrait de l’ancienne publication appartient à la transaction de publication ou de suppression. Un échec de la nouvelle génération conserve la précédente. Les réservations abandonnées peuvent laisser des trous de numérotation : elles ne recyclent pas une ancienne valeur.

### 3. La purge est ciblée et vérifie son effet

Le collecteur ne supprime que des points appartenant à un manifeste complet, scellé, retiré et strictement ancien, sur une cible identifiée et autorisée. Il protège la publication active et la génération courante. Les credentials restent liés à leur cible ; une clé courante n’est pas envoyée automatiquement à une ancienne URL.

Un accusé de réception HTTP ne suffit plus : le transport exige une réponse complète HTTP 200, un état `completed`, puis une relecture des identifiants exacts. Les réponses 202/206 plausibles et les payloads incohérents sont refusés. Baux, limites de lots, timeouts, checkpoints et quarantaine empêchent qu’un manifeste fautif bloque tous les autres ou qu’un collecteur périmé valide le travail d’un successeur.

Le cycle réel a confirmé la disparition des anciens points avec conservation de la publication active. Une écriture ancienne réinjectée de manière contrôlée a ensuite été détectée et recollectée. Ce témoin démontre la reprise du GC après réapparition ; il ne prouve pas l’annulation d’une requête HTTP worker déjà en vol.

**La purge reste désactivée par défaut.** Les anciens vecteurs sans manifeste/cible fiables restent une dette protégée, signalée par `vector_gc_legacy_inventory_complete=false`. La campagne n’a pas effacé tous les vecteurs historiques du serveur existant. La notice `vector-gc-operations-v1.md` décrit l’activation, les cibles anciennes et la restauration ; l’inventaire SQL fourni est en lecture seule.

### 4. Le démarrage à froid de Docling avait révélé un vrai défaut de reprise

Un ancien scénario combiné avait démarré le backend dès que les containers des dépendances étaient « running ». Trois conversions ont reçu HTTP 502 avant que le service Docling amont écoute réellement. Les timestamps et le refus de connexion ont été rapprochés : ce n’était pas un problème de compréhension du LLM. Le Compose de production attendait déjà un état healthy ; l’opérateur de qualification n’appliquait pas une attente équivalente.

Deux corrections distinctes ont été apportées :

1. Le bootstrap vérifie réellement PostgreSQL, TEI, Qdrant et Docling avant de créer le backend. Les preuves de santé précèdent son démarrage ; en cas d’échec, il conserve les diagnostics et arrête ses seules dépendances.
2. `DoclingClient` reprend certaines indisponibilités transitoires, avec au maximum cinq tentatives et des pauses 1/2/4/8 secondes, sous un seul délai global. Il réutilise le même fichier ouvert et reconstruit la requête à chaque tentative. Les erreurs permanentes, réponses ambiguës, erreurs d’authentification ou documents invalides ne deviennent pas des boucles de reprise.

Les codes 503/504, les erreurs réseau et un 502 correspondant exactement à l’enveloppe transitoire connue sont traités de façon bornée. Annulation et délai restent prioritaires. Les erreurs finales et sondes n’exposent ni contenu documentaire, ni URI sensible, ni corps brut.

La preuve avant correction comprend **8 échecs et 12 témoins verts**. Le premier passage après correction a donné **25 PASS et un échec de fixture Windows** : `File.Move` ne remplaçait pas un fichier encore ouvert. La fixture utilise ensuite `File.Replace`, dont le comportement a été vérifié séparément. La logique produit n’a pas été assouplie pour masquer cet échec. Les 26 cas passent dans la suite Windows finale et sous Linux. Le parcours combiné réel a été rejoué avec succès sur une nouvelle configuration Production signée et une nouvelle image finale.

### 5. Déploiement, OCR et outils de preuve ont aussi été corrigés

Les sources de build incluent les propriétés communes et le SDK 8.0.420 attendu. Les scripts portent une identité de contenu des sources, refusent certaines divergences runtime déclarées et échouent si la disponibilité n’est jamais confirmée. L’archive de transfert exclut les fichiers privés/générés et conserve les entrées nécessaires au build. Le lecteur `.env` PowerShell accepte un sous-ensemble littéral explicite avec UTF-8 strict ; il refuse les formes qu’il ne peut interpréter fidèlement, sans répéter leur valeur dans l’erreur.

La vieille fixture OCR à police bitmap artificielle a été remplacée par un PDF raster réaliste embarqué dans la DLL de tests. L’oracle exige toujours les mots opérationnels et `XQ42`. Le passage réel dans les moteurs Linux ferme ce test précis.

Certains échecs provenaient aussi du vérificateur : les champs diagnostiques optionnels `null` d’une réponse saine étaient considérés comme des erreurs. L’oracle suit maintenant le contrat réel des DTO et continue de rejeter les valeurs malformées ou les erreurs effectives. Les exports publics, les gardes de namespace possédé, les délais des opérateurs et les contrôles de nettoyage ont été revus séparément. Les anciennes captures erronées restent historiques ; elles ne sont pas comptées comme des défauts produit encore ouverts.

## Diagnostic ajouté et cohérence des preuves

Les sondes rendent observables l’inventaire partiel, les présences inconnues, les deletes différés, l’issue réelle d’une reprise, les générations/plans retirés, les rondes GC, l’absence observée par lot et les tentatives Docling. Elles utilisent des compteurs, identifiants et codes de résultat ; elles ne nécessitent pas de journaliser le texte des documents. `/ready` expose l’état et les identités utiles sans remplacer la vérification documentaire complète.

Les rapports anciens annonçant « runtime en attente » sont volontairement conservés à leur date. **Le présent rapport et les exports `public-export-production-final-v4` / `public-supplement-final-v4` portent la clôture finale.** Les exports comprennent 54 projections publiques scellées et 16 pièces complémentaires scellées. Ils contiennent les empreintes des preuves originales, les identités et les résultats sélectionnés ; les captures privées complètes restent hors Git.

Le build Linux utilise un instantané de **445 fichiers**. Les octets du checkout concordent avec cet instantané. Dans l’index Git, 443 sont identiques octet par octet ; deux diffèrent uniquement par la normalisation déclarée des fins de lignes (`launchSettings.json`, `wwwroot/index.html`). Cela ne constitue pas une reconstruction binaire reproductible depuis un nouveau clone.

| Ancrage final | Valeur |
|---|---|
| Identité du contenu Linux | `source-sha256:dca75ae2906275ab1120aa316aab0c4d5d492fa74a5853dcd5eeb56bb3111a5a` |
| Image Docker réellement contrôlée | `sha256:d272895b502e7cd902afa7c197eac34b1227ffacfde80204c02f4b444b3587cd` |
| DLL Backend Linux | `f6825b8c36bc26a5da8308eb4d4a5ec87c62d6962f57f644c725e8e606eb18c4` |
| DLL Backend AnyCPU des suites Windows/PG | `36d369a7ae567b73698324de1e76c078267edf386ecfd16f45a23c76513cf5a0` |
| DLL Tests des suites Windows/PG et contrôles Linux ciblés | `583c9042fb813784d405311e0dacdd68ec04637c9c4f4e8ce7a44b057299523f` |

Les binaires Linux et AnyCPU sont volontairement distingués. Les suites complètes Unit et PG ont utilisé sans rebuild les assemblies du build Release du ciblage `d1efa9e875324fd8b0a00838dcdadbd8`, avec la même empreinte de sources et les mêmes quatre ancrages Backend ; aucune source n’a changé pendant ces captures. Les preuves runtime lient l’image réellement démarrée, la DLL observée, les CID, la configuration signée et la révision du code.

La pile CPU qualifiée comprend le vrai Qdrant 1.10.1, TEI CPU 1.6.1, `multilingual-e5-base` à la révision `d128750597153bb5987e10b1c3493a34e5a4502a`, et l’image Docling identifiée dans les attestations. Les versions, modèles/cache et empreintes effectives sont conservés dans les preuves. Aucune compatibilité avec une autre image ou un autre matériel n’est déduite de son nom.

## Sécurité de la campagne et nettoyage

La clé privée retrouvée dans le dossier indiqué est restée sur ce PC. Elle a servi au signer existant ; elle n’a été ni affichée, ni transférée sur Linux, ni ajoutée à Git. Les cinq nouvelles configurations ont été vérifiées localement et acceptées par le backend Production. Le témoin altéré utilise une copie dédiée ; l’original est inchangé. Une première tentative mal montée a seulement échoué à trouver le fichier : elle n’est pas comptée comme preuve de rejet cryptographique. Le témoin correctement monté a ensuite démontré ce rejet sur l’image finale. Un complément public séparé, `signature-rejection-safe-diagnostic-final-v2.json`, conserve l’empreinte du log et le diagnostic exact `deployment.config.json signature verification failed.` ; le code de sortie 139 seul n’est pas utilisé pour conclure à un rejet de signature.

Toutes les modifications serveur sont restées dans les namespaces dédiés de `/home/maxime/saaia-ingestion-qualification`. La vérification finale constate **22 containers préexistants inchangés**, **16 namespaces de campagne arrêtés**, aucun container de test encore actif et aucune mutation de la pile de production. Les opérateurs finaux ont constaté zéro job actif avant arrêt et l’absence de leurs ports publiés. Le cluster PostgreSQL local isolé a été supprimé, son environnement restauré et son listener fermé. Les volumes, images et preuves serveur ont été conservés pour analyse ; aucun nettoyage global Docker n’a été exécuté.

Les services de qualification communiquent dans un bridge dédié avec publication loopback. Il ne s’agit pas d’une isolation globale interdisant toute sortie réseau. Les contrôles ciblés OCR, filesystem, retry et signature ont, eux, utilisé `network=none`. Aucun pilote, firmware ou réglage de sécurité système n’a été modifié.

## Limites restantes et suite directe

- **Course réelle source revenue pendant le delete :** le scénario automatique n’a pas capturé cette fenêtre, la suppression s’étant terminée avant observation. Il reste `NON_EXECUTED` pour cette course précise en runtime. Les 16 cas PostgreSQL déterministes et les gardes worker passent ; ils ne sont pas présentés comme une capture de cette course physique.
- **Filesystem et SQL :** ils ne forment pas une transaction unique. Les rechecks ferment les fenêtres reproduites, sans garantir qu’un fichier ne peut changer juste après la dernière observation. Une panne persistante de stockage peut retarder le parcours d’une branche ; les deletes deviennent différés. Les anciennes queues sans marqueur conditionnel demandent une inspection avant mise à niveau.
- **Legacy vectoriel :** absence de manifeste fiable signifie absence de purge automatique. Une migration/qualification ciblée de cette dette sera nécessaire avant d’affirmer que tout l’historique est nettoyé. Les restaurations/clones demandent aussi le protocole d’exploitation documenté.
- **Qualité documentaire :** 106 faits et les corpus exercés ne prouvent pas tous les PDF, tableaux, scans dégradés, langues, paraphrases ou raisonnements. La recherche canonique et la présence physique des vecteurs sont vérifiées ; un classement purement vectoriel universel n’est pas qualifié.
- **Docling :** les 26 cas utilisent un transport HTTP déterministe. Le vrai démarrage à froid et les ingestions ont été rejoués ; une panne réseau réelle injectée n’a pas été faite. La poignée ouverte protège le remplacement atomique du chemin ; elle n’est pas une promesse d’immuabilité contre une modification hostile du même inode.
- **Exploitation :** GPU, charge prolongée, stockage réseau arbitraire et équivalence complète de l’installateur shell avec tous les formats `.env` ne sont pas qualifiés par cette campagne. Les délais accélérés des scénarios ne constituent pas un SLA de production.

La suite logique est maintenant une campagne du **planning avec un modèle payant autorisé**, après inspection de ce qu’il reçoit : outils annoncés, requêtes de recherche, accès au document/sommaire, pages et passages transmis, budget de contexte, recherche complémentaire et critères de réponse. Il faudra conserver la trace de chaque outil et distinguer défaut de récupération, défaut de contexte, défaut d’orchestration et défaut de réponse. Cette étape pourra s’appuyer sur l’ingestion qualifiée ici, sans attribuer par défaut tout échec au modèle. Elle n’a pas été lancée dans cette clôture.

Le reçu Git et le reçu Telegram de clôture sont enregistrés séparément après leur exécution ; ce rapport ne préjuge pas de leur succès.
