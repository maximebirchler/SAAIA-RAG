# Ingestion SAAIA — bilan du 25/26 octobre 2026

Les correctifs ont passé les campagnes décrites ci-dessous. Les deux titres manquants découverts par contre-relecture sont corrigés et vérifiés dans le vrai livre : 16 faits, 160 chunks/points. Aucun nouvel appel LLM payant. Le code est poussé dans 12202cb sur SAAIA_V3.1. Produit : TESTE_NON_APPROUVE ; les limites restantes sont explicites.

## Ce qui a été compris et corrigé

Le problème ne se limitait pas au modèle de réponse. L’amont pouvait supprimer ou réécrire une observation, attribuer une mauvaise section à un fait, reprendre les vecteurs d’un autre plan ou laisser un ancien worker intervenir après la reprise du travail. Les reproductions RED et les erreurs de fixture sont séparées dans les preuves ; une compilation échouée n’est jamais présentée comme une preuve produit.

| Domaine | Défaut et effet | Correction livrée |
|---|---|---|
| Source | Extracteurs susceptibles de lire des états différents du fichier | Copie immuable partagée, SHA256/taille des octets lus, mutation observée avant publication bloquante. |
| OCR/nettoyage | Valeurs courtes, signes, négations, variantes, avertissements ou caractères incertains réécrits/écartés | Conservation littérale et des observations brutes ; aucune correction lexicale devinée. Résultats partiels réconciliés sans effacer les pages déjà exploitables. |
| Canonique | Similarité prise pour présence exacte ; vues Unicode divergentes | Couverture littérale avec bornes et multiplicité ; projection commune des pages/unités/chunks/contexte. |
| Colonnes/titres | Ordre de lecture erroné ; faits de gauche rattachés au titre de droite | Alternative native lorsque démontrée ; texte, titres et polygones conservés ; association sémantique non prouvée retirée. |
| Titres sans contenu associé | Deux titres existaient dans le canonique mais disparaissaient du retrieval | Émission des seules observations non couvertes comme contexte physique ancré, avec signal persistant ; huit régressions, pas de déduplication abusive d’occurrences identiques ni section inventée. |
| Tableaux | Séparateurs cassant un fait ; tirets typographiques divergents | Séparateurs de cellules conservés ; réconciliation native uniquement sur correspondance géométrique unique et variation alphabétique de tiret. Aucun nombre, signe moins ou accent corrigé par supposition. Original et provenance conservés. |
| Reprise | Checkpoint accepté malgré changement de plan | Empreinte de source, identité/version, entrées exactes, modèle/révisions/runtime, cible, dimensions et code ; relecture des points attendus. |
| Concurrence | Ancien worker pouvant encore modifier/publier | Nouveau bail opaque à chaque dequeue ; mutations et publication sous contrôle exact du propriétaire et verrou transactionnel. Perte du bail : annulation puis observation. |
| Heartbeat | Attente de son propre verrou de publication | Renouvellement sans auto-blocage ; distinction entre TTL réellement renouvelé, propriétaire observé avec renouvellement différé et terminal propre. |
| Générations/vecteurs | Réutilisation d’identités après annulation/delete ; écriture tardive écrasant un plan récent | Versions monotones ; point physique dérivé du chunk ET du plan ; identité canonique des citations conservée ; plan actif publié atomiquement en SQL. |
| Recherche dense | Anciens points saturant la première fenêtre | Filtre SQL du plan/révision actifs ; élargissement borné ; diagnostic dense_active_plan_candidate_budget_exhausted si la recherche reste incomplète. |
| Embeddings | Réponse réordonnée ou invalide associée au mauvais texte | Indices complets/uniques, dimensions cohérentes, valeurs finies, vecteurs non nuls. Erreurs techniques sans recopier les corps documentaires. |
| Diagnostics | Annulation 5/publication 3 présentée comme backlog | États annulé, actif, échec et indéterminé distincts. Avertissements de génération abandonnée et source non revérifiée conservés ; différence source observée toujours visible. Aucun I/O disque dans ces diagnostics. |
| Qualification | Contrôles incomplets ou attentes incohérentes | Corpus complet et épinglé, faits/pages exacts, vrais points. Trois annotations corrigées depuis le PDF générateur, sans changer les PDF ni retirer les faits critiques. |

Le contrôle négatif de recherche hybride était également erroné : un nonce absent n’interdit pas un voisin dense. Il exige maintenant zéro match exact/sparse et vérifie que les éventuels voisins correspondent exactement aux chunks SQL courants autorisés. Le contrôle d’un document inexistant exige toujours zéro résultat total.

Les sondes distinguent pages candidates, tentées, réussies, échouées et inconnues ; une heuristique de lisibilité ne devient pas une confiance moteur. Le wrapper Docling compte les vrais threads HTTP encore en vol après annulation de l’attente. Il ne prétend pas prouver l’arrêt du calcul en amont. Les observations documentaires restent privées ; elles ne passent pas dans les logs ordinaires.

## Structure du code

Les responsabilités OCR, processus, observations, réconciliation, snapshot, identité de pipeline/plan et bail ont été isolées dans des composants nommés. Le fichier principal OCR passe de 2 404 à 1 325 lignes ; le classifieur supprimant le mobilier répété devient une annotation de 38 lignes. PdfExtractor fait 922 lignes et le worker 2 070. Les suppressions ont été précédées de recherches de références et de régressions de conservation ; des méthodes de purge Qdrant larges dangereuses ont été retirées.

Le contrôle d’architecture trouve 1 706 sources, zéro violation, mais 21 fichiers restent à revoir pour leur taille. Le plus grand fichier observé fait 8 393 lignes. Ces budgets passent ; cela ne signifie pas que tous les agrégats historiques ont été refondus ou que l’architecture entière est parfaite.

## Vérifications exécutées

| Campagne | Résultat et portée |
|---|---|
| unit-all-final-v3 | 5 255 PASS, zéro échec, un skip explicite ; backend 2 734, contrats 20, cœur client 82, client 2 419. Build demandé, sources stables. |
| monotonic-consumers-green-v1 | Sept PASS PostgreSQL, zéro échec/skip, sources stables. Deux lecteurs réels et vrai SELECT qualité ; RED préalable : deux défauts et trois témoins passants. |
| postgres-all-final-v3 | 310 PASS, zéro échec, deux skips historiques, sources stables ; même binaire backend que la suite unitaire finale et les deux workers finaux. Cluster possédé arrêté. |
| Python qualification | 61 PASS ; tests de contrats/géométrie/vérificateur/points, distincts d’une ingestion réelle. |
| Python sidecar | 13 PASS ; métadonnées, réparation et sondes de transport. |
| Gardes hors ligne | 36 dépendances + 35 ingestion + 16 cycle de vie PASS. Aucun service réel revendiqué par ces tests. |
| Annulation mock | Protocole nominal et contrôle négatif PASS : canceled sans ACK explicite n’est plus accepté comme annulation validée. |
| Worker complet post-corrections | 12 PDF, 16 pages, 90 faits, 37 chunks/points PASS. Base vide, 12 vrais jobs API, aucun seed canonique ; SQL/checksums/identités/points/recherche/lecture contrôlés. |
| Cycle réel worker-full-e5-lifecycle-final-v1 | Sur le binaire 00A011…, publication 1 → réingestion 2 → suppression 3 avec zéro résultat → recréation 4 → annulation 5 pendant document_intelligence, ACK true, publication 4 conservée et consultable, zéro job actif. |
| Worker final avec cycle réel v2 | Sur le binaire final A21D64…, 12 PDF/90 faits/37 points et réingestion/suppression/recréation PASS. Annulation NON_EXECUTED : le job a terminé avant son effet. La comparaison des sources confirme que les dix chemins critiques de bail/worker/contrôle sont inchangés depuis l’annulation réelle v1 ; ce NON_EXECUTED n’est pas transformé en PASS. |
| Premier livre réel | 36 pages, 261 chunks/points, 14 faits physiques PASS, environ 131 secondes. Attentes fixées avant ingestion ; pas une validation exhaustive du sens des 36 pages. |
| Livre réel historique worker-recipe-e5-final-v2 | Même livre, 36 pages, **158 chunks/points** et 14 faits : PASS de ces contrôles sur 00A011…, environ 130 secondes. Les 261 sont le nombre historique avant les corrections de regroupement. La contre-relecture de ce run a révélé les deux titres corrigés ensuite dans v3. Backend/cluster possédés arrêtés, zéro job actif. |
| Livre réel final worker-recipe-e5-final-v3 | 36 pages, **160 chunks/points et 16 faits : PASS** sur A21D64…. Les deux titres ont chacun un contexte physique propre, un signal persistant et aucune section affirmée. Les deux attentes supplémentaires proviennent du PDF original, ajoutées après observation du défaut : régressions, pas évaluation aveugle. |
| Sonde E5/Qdrant réelle | Trois passages et trois permutations PASS, marge ≈0,081 ; erreurs de contrat refusées ; collection neuve supprimée et absence confirmée. Petit contrôle sémantique, pas mesure globale du rappel. |
| Build projecteur Gold | Compilation normale : zéro avertissement, zéro erreur. |
| Exporteur public | 12 tests synthétiques PASS : projection XML explicite, arguments retirés et hashés, résultats/identités conservés, traversées de chemins refusées. |

La contre-revue indépendante du premier worker complet recalcule 90 faits, 37 identités/checksums, 16 contextes et 58 identités de hits sans écart. Les deux seules cases absentes des grilles sont les cellules vides annotées indépendamment ; aucun chevauchement ni trou utile constaté. Les quatre pages raster conservent exactement leurs 20 lignes imprimées dans canonique ET retrieval. Elles font partie du corpus : ce n’est pas un ajout artificiel aux 90 faits.

L’audit de couverture du premier livre à 158 chunks distingue les 2 035 blocs observés : les deux titres sont les seules pertes utiles identifiées ; les neuf blocs recovery sont représentés par une séquence exacte et contiguë de tokens dans leurs seuls chunks liés, dans l’ordre. Les autres exclusions sont 25 compteurs de page, dont un chiffre a aussi une coïncidence littérale ailleurs sans preuve de provenance. Les deux titres sont publiés et lisibles dans le dernier run à 160 chunks. La recherche positive capturée reste celle du premier fait p5 ; une recherche dédiée à chacun des deux titres n’a pas été exécutée et n’est pas revendiquée.

Les captures finales unitaires, PostgreSQL, corpus et livre utilisent le backend SHA256 A21D64DA8465CCB39449008FDD5106C81860B4D90E1290A2FA83340F3F827BC7. Le Git HEAD d’un checkout dirty n’est pas présenté comme une preuve suffisante du code chargé.

## Runtime et limites

Le runtime réellement exécuté est Windows/CPU : Docling Serve 1.38.1, Docling Slim 2.142.1, RapidOCR 3.9.1, ONNX Runtime 1.23.2, Torch 2.14.1+cpu, Transformers 5.20.2, Qdrant 1.20. Le vrai modèle E5-base est épinglé à d128750597153bb5987e10b1c3493a34e5a4502a ; ses poids ont le SHA256 a18a44fad1d0b46ded15928144138cff1135d5cc8233bdd90be5f18822de09a7. Les conversions sont hors ligne. L’adaptateur Transformers CPU n’est pas le binaire TEI ; cette campagne ne qualifie pas automatiquement l’image Docker historique aux versions différentes.

Les limites restantes sont explicites :

- Les anciens plans Qdrant sont isolés et filtrés, mais leur collecte physique sûre n’est pas implémentée. L’accumulation peut atteindre la fenêtre dense bornée ; elle est diagnostiquée, pas rendue impossible. Une collecte vérifiée sûre reste nécessaire pour une exploitation prolongée avec beaucoup de réingestions.
- L’ancien OCRmyPDF/Tesseract reste opt-in et non qualifié. Le contrôle automatique avait refusé son téléchargement/installation silencieuse combinés ; aucune installation réussie n’est revendiquée. Les tests réels utilisent Docling/RapidOCR. La réparation OCR régionale effectivement appliquée n’est pas exercée par ce corpus.
- Manuscrit, scans arbitrairement dégradés, toutes les langues, tous les tableaux et interprétation générale des figures/graphes ne sont pas qualifiés par ces PDF. Les quatre rasters testés contiennent du texte imprimé et des cadres, pas une preuve de compréhension visuelle générale.
- Le job réellement annulé et sa publication conservée sont validés. L’arrêt du calcul Docling en amont ne l’est pas : upstreamEngineCancellationVerified=false.
- Les recherches canoniques contrôlées et 14 faits du livre ne démontrent ni son sens exhaustif ni la réussite du planning de repas. La suite fonctionnelle devra éprouver la recherche/outils/contextes sur ce corpus, puis seulement une petite campagne de réponses avec un modèle économique dans le budget autorisé.

Aucun nouvel achat ni appel LLM payant n’a été effectué dans cette campagne. Tous les environnements sont locaux et possédés. Aucun serveur habituel, profil utilisateur ou PostgreSQL 5432 n’est modifié.

## Preuves et sauvegarde

Les rapports de résolution de ce dossier détaillent OCR/natif, canonique, baux/points, recherche dense, tableaux/colonnes, harnais et diagnostics monotones. Les originaux privés restent conservés localement. Les preuves destinées à Git sont des projections explicites sans contenu PDF, arguments de tests, sorties brutes ni secrets, reliées aux originaux par leurs hashes.

Les deux campagnes finales ont arrêté leurs backends/clusters et confirmé zéro job actif. Les quatre dépendances v7 ont ensuite été arrêtées : PID et ports absents, logs vidés, données conservées. La preuve achevée est dependency-stop-f5c8d29552564802a770a27f7c0e6b6b.json ; son résumé public est dependencies-v7-cleanup-public-v1.json. L’infrastructure habituelle est inchangée.

La comparaison binaire au staging confirme 1 705 fichiers de code/tests exactement identiques aux sources testées et le manifeste critique du corpus exactement conservé. Aucun fichier testé n’a changé après le run. Le vérificateur global reste strictPassed=false pour 31 configurations historiques dont seuls les EOL diffèrent entre working tree et index : ces différences ne sont pas assimilées à une égalité exacte et ces fichiers ne sont pas modifiés dans le commit. La décision de staging séparée accepte le code et le corpus exacts, avec cette limite visible.

Le code, ses tests, les migrations 070/071 et les outils sont commités et poussés dans **12202cb** (141 fichiers sélectionnés explicitement). Les rapports et projections de preuves sont sauvegardés dans un commit séparé, dont l’identifiant est indiqué dans le compte rendu de clôture. Aucune observation privée, PDF, base, modèle ou clé n’est destiné à Git. Les rapports Telegram de 23 h 58 et 00 h 53 UTC couvrent les avancées intermédiaires ; le bilan de clôture présente les seuls développements suivants et leurs limites.
