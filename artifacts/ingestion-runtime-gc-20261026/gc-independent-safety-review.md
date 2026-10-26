# Contre-conception indépendante : GC vectoriel et qualification du runtime

État de cette note : **conception et revue de code en lecture seule**. Aucun produit ou test modifié, aucun test exécuté, aucun service, réseau, build ou Git utilisé. Les matrices ci-dessous sont proposées et **NON_EXECUTED** pour le futur GC. Les anciennes preuves d'ingestion restent immuables.

## Conclusion de sûreté

Une purge est sûre seulement si elle vise un ensemble exact de points, dans une cible historiquement identifiée, dont le retrait ne peut plus être annulé par une reprise autorisée. La différence avec la configuration actuelle, l'âge des points, une perte de bail ou l'absence d'un plan parmi les résultats visibles ne constituent pas cette preuve à eux seuls.

Le socle actuel isole les points par plan et filtre les résultats selon la publication SQL active. Il protège la lecture et l'écrasement inter-plans ; il n'implémente pas encore le GC physique. Je recommande de commencer par la collecte des générations **irréversiblement dépassées**, avec registre durable et suppressions par IDs. Les cas ambigus doivent être reportés explicitement, jamais traités par un filtre large.

## Ce que le code actuel établit

| Constat vérifié | Conséquence pour le GC |
|---|---|
| [IngestionVectorPlan.cs:12](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Ingestion/IngestionVectorPlan.cs:12>) inclut identité tenant/document/chemin/catégorie/version, cible et collection, modèle/révisions/runtime, format, dimensions, code et entrées dans l'empreinte. | Deux configurations ou générations différentes produisent normalement des plans et IDs distincts. L'empreinte est opaque : elle ne permet pas de retrouver l'ancienne URL/collection ou la liste de points. |
| [IngestionVectorPlan.cs:36](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Ingestion/IngestionVectorPlan.cs:36>) dérive l'ID physique du chunk canonique et du plan. | Le GC doit supprimer l'ID physique de ce plan, tout en conservant le chunk canonique servant aux citations. |
| [IngestionWorker.cs:1435](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Ingestion/IngestionWorker.cs:1435>) calcule le plan et tous les IDs avant les upserts ; [ligne 1552](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Ingestion/IngestionWorker.cs:1552>) réalise les écritures Qdrant avant la publication SQL. | Un registre écrit avant le premier upsert peut couvrir aussi les plans interrompus qui n'ont jamais été publiés. Se limiter aux anciennes révisions publiées ne couvre pas ces orphelins. |
| [JobRepo.Completion.cs:39](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Ingestion/JobRepo.Completion.cs:39>) contrôle le bail sous transaction ; [ligne 203](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Ingestion/JobRepo.Completion.cs:203>) exige la génération courante ; [ligne 264](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Ingestion/JobRepo.Completion.cs:264>) publie le plan actif dans cette transaction. | Le retrait de l'ancien plan et son ticket durable doivent appartenir à cette même décision SQL, pas à une action HTTP préparée avant le commit. |
| [JobRepo.Completion.cs:421](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Ingestion/JobRepo.Completion.cs:421>) désactive la publication lors du delete tout en conservant la monotonie de génération. | L'ancien plan peut être déclaré retiré avec un témoin de suppression ; une recréation ultérieure ne doit pas faire réutiliser ses IDs. |
| [JobRepo.cs:284](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Ingestion/JobRepo.cs:284>) reporte un travail transitoire sans changer sa génération ; une reprise administrée remet un job paused en queued [Control.cs:582](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Endpoints/SummaryEndpoints.AdminJobs.Control.cs:582>). | Un plan non actif mais encore reprenable peut redevenir nécessaire. Aucun chemin standard de reprise d'un job done n'a été identifié ; le risque décrit ici vise les états réellement reprenables ou non prouvés retirés, pas une résurrection supposée de tous les terminaux. |
| [RetrievalCandidateSignals.cs:2062](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Retrieval/RetrievalCandidateSignals.cs:2062>) lit la version et le plan SQL actifs avant admission d'un match dense. | Ce filtre doit rester actif pendant et après le GC. Le nettoyage ne remplace pas la règle d'admission des preuves. |
| [DenseActiveCandidateSearch.cs:11](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/backend/SAAIA.Backend/Retrieval/DenseActiveCandidateSearch.cs:11>) borne l'élargissement à trois requêtes et signale une fenêtre incomplète. | Le GC améliore l'espace et le rappel effectif, mais son existence ne justifie pas de retirer le diagnostic d'incomplétude. |

La recherche des suppressions directes de `documents` dans le code courant n'a pas trouvé de purge physique de ces lignes ; le delete d'ingestion conserve la ligne. Cette observation n'autorise pas une suppression administrative future, un reset SQL ou une restauration de sauvegarde à effacer la preuve de génération.

## Invariants à rendre explicites

1. **Publication active intouchable.** Ne jamais supprimer un point appartenant au tuple actif tenant/document/génération/plan/cible. Une génération d'ingestion plus élevée encore en cours ne rend pas l'ancienne publication invalide : un échec ou une annulation doit pouvoir la conserver.
2. **Retrait irréversible.** Critère conservateur proposé : `candidate.version < documents.ingestion_version`, et candidat différent de la publication active. La génération du document ne doit plus diminuer et sa preuve ne doit pas être détruite. Ce critère exige aussi l'identité exacte de cible et de point décrite ci-dessous.
3. **Même génération : report par défaut.** Un simple `candidate.plan != active.plan` ne suffit pas pour un plan encore repris ou en construction. Pour collecter immédiatement de tels plans, il faut une preuve de terminalité irréversible ou un tombstone durable contrôlé à l'enregistrement, à la reprise et à la publication sous transaction. Sinon attendre une génération supérieure ; ne pas déclarer ces points collectés.
4. **Identité physique.** Les tickets contiennent les IDs exacts à supprimer. Aucun delete par document, catégorie, borne ouverte de version, modèle incompatible ou « tout sauf plan actuel ». Les futurs points sont exclus même s'ils portent le même doc_id.
5. **Cible historique.** Endpoint/collection/namespace du plan retiré, jamais substitution de la configuration courante. Une cible non reconnue ou inaccessible bloque la collecte correspondante.
6. **Isolation.** Vérifier tenant, document, génération, plan et chunk canonique attendus dans les observations de points. Une incohérence est mise en quarantaine ; elle n'élargit pas le périmètre de suppression.
7. **Répétabilité.** Une suppression de batch exact est idempotente. Son acquittement et l'absence observée sont deux états distincts. Un timeout ne prouve ni suppression réussie ni absence de suppression.
8. **Aucun effet de reconstruction.** Le GC ne crée pas une collection manquante, ne republie pas une révision et ne modifie pas les citations ou les textes SQL pour « réparer » un écart.
9. **Écritures tardives assumées.** Un ancien upsert déjà accepté par Qdrant peut finir après l'annulation locale. La collecte doit pouvoir vérifier et supprimer de nouveau le même ensemble retiré, sans toucher au nouveau plan. Une vérification d'absence à un instant ne prouve pas une absence permanente.
10. **Baux indépendants.** Le collecteur possède son propre token d'exécution et ses checkpoints sous garde. Il ne conserve pas un verrou du job/document pendant un appel HTTP : cela réintroduirait le type de blocage de heartbeat déjà corrigé.

## Proposition durable minimale

### Registre de cible et de génération

Enregistrer avant tout upsert un manifeste sans texte documentaire : target_id stable, tenant/document/génération, plan complet, IDs canoniques et physiques attendus, nombre et checksum de l'ensemble, job/tentative d'origine. L'association cible→endpoint/collection doit être immuable pour cet enregistrement ; les credentials sont des références protégées séparées, jamais des valeurs dans les sondes ou les tickets publics.

Le format concret peut employer une table de générations et une table de points, ou un manifeste compact vérifiable. Le besoin essentiel est la récupération des IDs même si le worker meurt avant son premier checkpoint. Les seuls compteurs de progression ou une empreinte sans liste ne suffisent pas à retrouver tous les points.

Le changement d'état active→retired et la mise à disposition du travail de GC sont transactionnels avec la publication SQL ou le delete. Un rollback ne doit pas laisser un ticket autorisant la suppression de la publication qui reste active. Un simple calcul préalable de l'ancien plan ne doit jamais produire un ticket exécutable.

Le GC peut ensuite réclamer un petit batch sous son propre bail, vérifier sa preuve de retrait et libérer la transaction avant le réseau. La preuve doit rester vraie sans verrou : c'est la raison de la barrière de génération irréversible ou du tombstone imposé à la publication.

### Identité du stockage

URL et nom de collection ne prouvent pas, seuls, l'identité d'un stockage après restauration, remplacement de serveur ou recréation d'une collection homonyme. Prévoir une identité de namespace/déploiement enregistrée et attestée, ou borner explicitement la première livraison aux cibles possédées dont cette identité peut être vérifiée. Le mécanisme d'attestation reste à spécifier et à tester contre le runtime retenu ; cette note n'affirme pas qu'une fonction native Qdrant particulière l'assure déjà.

Une ancienne cible sans credentials conservés est un résidu `blocked_target_unavailable`, pas une raison de diriger le delete vers la nouvelle cible. Une URL historique ne doit pas devenir une destination réseau arbitraire : résolution à partir d'un registre approuvé et isolation du profil sont nécessaires.

### Legacy et découverte des orphelins

Un point legacy sans plan n'est pas automatiquement supprimable. La branche de lecture actuelle tolère certains états legacy ; une publication active avec plan nul doit rester protégée. Ne pas inventer le plan ou le format d'ID à partir du dernier code.

Pour une reprise historique, distinguer :

- anciens plans dont cible et IDs exacts sont prouvés par un manifeste ou une migration vérifiable ;
- points découvrables dont payload, calcul de l'ID, tenant/document/génération et retrait irréversible peuvent tous être prouvés ;
- points sans identité suffisante, qui restent en quarantaine/attente d'une migration dédiée.

La découverte peut employer une lecture paginée bornée dans une cible approuvée ; elle ne doit jamais transformer son filtre de découverte en filtre large de suppression. Une découverte incomplète n'est pas renommée purge complète. Les plans encore dans la génération courante peuvent rester reportés même si leurs points sont filtrés à la lecture ; cette dette doit être quantifiée.

### Acquittement, reprise et écritures tardives

Conserver séparément planned/read_verified/delete_requested/delete_acknowledged/absence_verified, avec nombre attendu, nombre relu, nombre absent et hash des IDs. La sémantique de fin réelle de l'API delete du Qdrant retenu doit être qualifiée ; une réponse reçue n'est pas un substitut à cette vérification.

Après timeout, perte de bail du collecteur ou crash entre delete et checkpoint : reprendre les mêmes IDs et vérifier l'absence. Les tickets retirés ne sont pas effacés dès le premier PASS si des upserts tardifs restent possibles. Prévoir un rescan/recheck borné ou un mécanisme prouvé de quiescence. Une TTL seule n'établit pas que tous les serveurs ont terminé une ancienne requête. Le bilan doit parler d'absence **observée à une date**, puis de réapparitions éventuelles traitées, sans promettre une garantie distribuée inexistante.

## Matrice causale proposée — aucun cas exécuté dans cette revue

Chaque ligne doit disposer d'un témoin de publication active conservée, pas seulement d'un compteur de deletes. Les tests HTTP simulés prouvent le contrat ; PostgreSQL prouve les transactions/races ; le vrai Qdrant est requis pour qualifier le stockage, les acquittements et les reprises. Statut de toutes les lignes : **NON_EXECUTED**.

| ID | Scénario / entrelacement à provoquer | Résultat exigé | Preuve minimale |
|---|---|---|---|
| G01 | Publication B remplace A, commit réussi | Ticket A durable, B intact ; seuls IDs A retirés | PG + protocole IDs + vrai stockage |
| G02 | Publication B rollback après préparation | Aucun ticket exécutable A ; A reste lisible | PG transaction |
| G03 | V2 en cours puis échec/annulation, V1 reste publiée | V1 et son plan non collectés | PG + recherche/lecture |
| G04 | Delete à V3, recréation V4 avant exécution GC | IDs anciens supprimés ; plan/points V4 intacts | PG + vrai cycle |
| G05 | A perd son bail, B publie, ancien upsert A arrive tard | A ne publie pas ; aucun écrasement B ; GC répétable A | Barrières déterministes + PG + vrai stockage |
| G06 | Plan A en queued/paused, retry même génération et même plan | GC reporté ; reprise complète possible | PG + protocole |
| G07 | Même génération mais autre plan après config différente | Plan encore reprenable non collecté sans retrait irréversible | PG + contrôle d'admission |
| G08 | Collecte d'un plan de génération strictement plus ancienne, reprise tentée | Publication ancienne rejetée ; publication courante intacte | PG causal |
| G09 | Modèle/révision change, réingestion nouvelle échoue | L'ancien plan actif n'est pas déclaré obsolète sur le seul modèle | PG + protocole |
| G10 | Collection/endpoint change entre A et B | Delete dirigé vers ancienne cible attestée, pas la configuration B | Deux faux serveurs bornés puis vrai namespace isolé |
| G11 | Ancienne cible inaccessible/credentials retirés | Ticket bloqué explicite, aucune destination de repli | Contrat HTTP/config |
| G12 | Collection homonyme recréée ou cible remplacée | Attestation refuse la mutation d'un namespace inconnu | Contrat + vrai runtime |
| G13 | Plan nul legacy encore actif | Zéro delete ; lecture legacy conservée | PG + protocole |
| G14 | Payload absent/incohérent, tenant incorrect, ID non conforme | Quarantaine, aucun élargissement de filtre | Contrats négatifs |
| G15 | Points absents avant la collecte | Absence constatée, progression idempotente sans création | Protocole + vrai stockage |
| G16 | Delete partiellement appliqué puis timeout | Aucun faux PASS ; relecture/reprise des seuls IDs prévus | Fault injection transport |
| G17 | Crash après delete mais avant checkpoint SQL | Retry idempotent, absence vérifiée puis checkpoint sous bon bail | PG + stockage |
| G18 | Bail GC perdu au milieu d'un batch | L'ancien collecteur ne valide pas le checkpoint du nouveau | PG causal |
| G19 | Deux collecteurs ciblent le même ticket | Revendication atomique ou idempotence maîtrisée ; pas de double succès trompeur | PG concurrent |
| G20 | Une nouvelle publication s'intercale entre décision et delete | Aucun ID nouveau supprimé ; preuve de retrait indépendante du snapshot | PG + transport retardé |
| G21 | Upsert ancien réapparaît après absence vérifiée | Nouveau point actif intact ; ancien lot détecté/recollecté | Vrai stockage, requête retardée |
| G22 | Changement de config pendant pagination GC | Target/plan du ticket immuables sur tous les batches | Contrat |
| G23 | Suppression/restauration de la ligne de génération ou backup ancien | GC suspendu sans preuve de floor/namespace valide | PG + scénario de restauration isolé |
| G24 | Beaucoup d'orphelins dépassent la fenêtre dense avant/après GC | Résidu et diagnostic visibles ; pas de fausse absence documentaire | Recherche réelle + compteurs |
| G25 | Batch énorme, réponse trop grande, annulation locale | Bornes requêtes/IDs/temps ; état repris sans perte de publication | Contrat + limites runtime |
| G26 | Faux ACK ou opération encore en cours | Aucun collected tant que la fin/absence n'est pas vérifiée | Contrat négatif + vrai Qdrant |

Les cas de race doivent commander l'ordre via barrières contrôlées, et non dépendre d'un sommeil arbitraire. Le cas G21 vérifie explicitement la limite « écritures tardives » ; ne pas annoncer qu'un delete ponctuel la rend impossible.

## Sondes et preuves proposées

Événements bornés : `vector_gc.plan_registered`, `vector_gc.plan_retired`, `vector_gc.batch_claimed`, `vector_gc.safety_deferred`, `vector_gc.delete_acknowledged`, `vector_gc.absence_verified`, `vector_gc.reappearance_observed`, `vector_gc.lease_lost`. Codes de report distincts : active_publication, current_generation_resumable, target_unattested, legacy_unverifiable, payload_identity_mismatch, target_unavailable, observation_incomplete.

Publier IDs internes/empreintes et compteurs : plans inscrits/retirés/reportés, points attendus/relus/absents, IDs demandés/acquittés, opérations incertaines, réapparitions, dette restante, limite atteinte, dates de dernière vérification. Aucun texte, vecteur, URL avec credentials, chemin documentaire ou réponse d'erreur brute dans les logs ordinaires. La journalisation d'un ACK ne doit pas incrémenter un compteur « points détruits prouvés » sans observation correspondante.

Le registre de GC ne doit pas être purgé par la suppression de l'historique des jobs tant que son travail ou son besoin de recheck subsiste. Un GC inactif doit être une capacité explicitement désactivée, avec dette visible ; l'absence de worker n'est pas une collecte réussie.

## Limites d'acceptation d'ingestion à conserver avant le planning

Le [bilan livré](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/artifacts/ingestion-ocr-audit-20261025/RAPPORT-INGESTION.md>) et le [résumé d'acceptation](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/artifacts/ingestion-ocr-audit-20261025/final-acceptance-summary-v1.json>) sont cohérents sur le statut `TESTE_NON_APPROUVE`. Les points suivants ne doivent pas être renommés validation globale :

| Preuve existante | Ce qu'elle qualifie | Ce qu'elle ne qualifie pas |
|---|---|---|
| 5 255 unitaires PASS, 1 non-exécuté | Contrats et causalités locales sur A21D64DA | Le non-exécuté OCR externe n'est pas un succès moteur |
| 310 PostgreSQL PASS, 2 skips | Contrats SQL/concurrence réels sur cluster isolé | Les deux anciens oracles legacy-retrieval/deterministic-writer sont volontairement ignorés ; pas une validation sémantique live de leur phrasing |
| 12 PDF / 16 pages / 90 faits / 37 points | Worker, fidélité annotée, publication et recherches contrôlées sur profil figé | Tous PDF, toutes figures, toutes langues ou rappel dense exhaustif |
| Livre 36 pages / 16 faits / 160 points | Source originale, lecture/publication ; deux titres récupérés | Les 2 nouvelles attentes sont des régressions ajoutées après observation, pas une évaluation aveugle ; recherche dédiée à chacun de ces titres non exécutée |
| Annulation réelle v1 | Job running/document_intelligence acquitté et annulé, publication antérieure conservée | Arrêt du calcul Docling amont non vérifié ; v2 reste NON_EXECUTED pour la course gagnée par la fin du job |
| E5-base réel Transformers CPU | Modèle/révision/poids et vecteurs réels, petite sonde sémantique | Binaire TEI, GPU ou profil différent |
| Docling Windows CPU / RapidOCR observés | Versions réellement exécutées et corpus local | Image Docker historique aux versions différentes, OCRmyPDF/Tesseract externe |
| Quatre rasters, vingt lignes exactes | OCR du texte imprimé testé | Compréhension générale de schémas/graphes/photos |
| Deux grilles sparse | Seules cases omises = vides annotés, aucun trou utile établi | Réparation régionale sélectionnée : deux tentatives mais aucune réparation appliquée dans ce corpus |

Le contrôle de staging du dernier jalon accepte le code testé et le corpus exacts, mais son drapeau d'inventaire global reste strictPassed=false pour 31 configurations historiques dont les EOL diffèrent. Ne pas convertir cette décision locale en égalité binaire de tout le dépôt.

### Gates runtime recommandées

Choisir un profil d'exploitation réellement supporté puis le qualifier ; il n'est pas nécessaire d'installer tous les moteurs historiques pour donner un PASS artificiel. Les moteurs non retenus restent désactivés/non qualifiés dans ce profil.

1. Enregistrer les versions/hashes observés, modèle, dimensions, format de préfixe et namespace de stockage ; comparer au profil attendu, sans substituer une simple étiquette déclarative. `DoclingExecutionMetadataPolicy` accepte encore un ancien service sans attestation tout en le déclarant non attesté : cela ne suffit pas pour une gate stricte de runtime.
2. Vérifier la configuration de collection existante (dimensions, métrique, espace vectoriel retenu). `QdrantClient.EnsureCollectionAsync` retourne actuellement dès GET réussi ; ce succès seul ne prouve pas cette compatibilité.
3. Rejouer le petit corpus réel sur le profil retenu, vérifier les erreurs de longueur/transport et l'absence de troncature silencieuse, puis un cycle de générations avec GC et recherche→lecture. Mesurer temps froid/chaud et mémoire sans inventer un SLA à partir de quelques cas.
4. Ajouter panne/reprise/annulation et vérification de dette/GC après écritures tardives. La readiness d'un service ou des modèles installés ne constitue pas cette preuve.
5. Séparer ensuite une campagne de recherche/outils sur le livre d'une campagne de réponses/planning avec le modèle économique autorisé. Les réponses du planning ne sont pas encore approuvées par les preuves d'ingestion.

Aucun nouvel appel LLM payant n'est nécessaire pour la conception du GC, ses tests de concurrence ou cette qualification documentaire locale.
