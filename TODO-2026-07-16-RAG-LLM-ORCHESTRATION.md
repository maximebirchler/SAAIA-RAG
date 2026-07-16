# TODO - RAG source-backed pilote par le LLM

> Cree le : 2026-07-16
> Statut : chantier actif
> Branche observee : `SAAIA_V3.1`
> Objectif : rendre fiables et professionnelles les reponses RAG simples et les demandes complexes structurees, sans retirer au LLM son role d'orchestrateur semantique principal.
> References prioritaires :
> - `ADR-2026-07-08-rag-llm-orchestration-source-backed.md`
> - `ANALYSE-2026-07-16-CARTOGRAPHIE-COMPLETE-RAG.md`
> - `CDC Agent AI - RAG - V3.1.md`, notamment memoires, assistant conversationnel, context budgeting, evidence pack, isolation du corpus et criteres d'acceptation

## 1. Regle d'architecture non negociable

Le LLM est l'orchestrateur et le decideur semantique principal.

Le LLM doit notamment decider :

- de l'intention reelle de l'utilisateur ;
- des contraintes, axes et slots a couvrir ;
- du besoin de clarification ;
- de la strategie de recherche ;
- des outils documentaires a appeler ;
- des reformulations, pivots et recherches complementaires ;
- de la legitimite et de la pertinence semantique des preuves ;
- de l'affectation des preuves aux parties de la reponse ;
- du caractere suffisant ou insuffisant du dossier de preuves ;
- de la redaction, de la synthese, de la reponse partielle ou du refus motive ;
- de l'adequation finale de la reponse avec la demande.

Le code est autorise a :

- exposer et executer les outils ;
- appliquer les droits, schemas, budgets, delais et limites d'iteration ;
- transporter sans perte les preuves et leur provenance ;
- detecter les erreurs mecaniques : JSON invalide, identifiant inconnu, page invalide, doublon exact, citation absente, structure incomplete ;
- verifier qu'une sortie respecte le contrat demande par le LLM ;
- renvoyer au LLM des erreurs structurees pour reparation ;
- tracer et rendre replayable chaque decision.

Le code ne doit pas :

- decider qu'un contenu est semantiquement une recette, une procedure, une norme ou une bonne reponse ;
- choisir arbitrairement les premiers `EvidenceId` lorsque le LLM n'en selectionne aucun ;
- inventer une requete metier de remplacement ;
- remplir une case ou reconstruire une reponse metier a la place du LLM ;
- contenir des listes de mots, titres ou categories propres a la cuisine dans le runtime produit ;
- transformer la memoire en preuve documentaire ;
- supprimer silencieusement une preuve jugee faible ou hors sujet par une heuristique metier.

## 2. Definition de la mission terminee

La mission ne pourra etre declaree terminee que lorsque tous les blocs suivants seront verifies.

### 2.1. Reponses RAG simples

- [ ] Une question factuelle simple obtient une reponse directe, claire et sourcee.
- [ ] Les affirmations documentaires concretes renvoient a des `EvidenceId` connus.
- [ ] Les sources affichees proviennent du meme `EvidenceBundle` que celui utilise par le Writer.
- [ ] Les cartes de sources conservent document, page, extrait, score et provenance utiles.
- [ ] Une question sans preuve suffisante n'entraine aucune invention.
- [ ] Les suivis conversationnels utilisent correctement le contexte precedent sans le transformer en preuve.
- [ ] Les questions simples respectent la cible CDC de deux appels LLM maximum dans 90 % des tours documentaires standard.
- [ ] Les cas simples ne passent pas par les boucles de planning complexe.

### 2.2. Reponses complexes structurees

- [ ] Le LLM extrait une structure generique depuis la demande utilisateur.
- [ ] Le LLM construit une strategie de couverture et un dossier de preuves adaptes a cette structure.
- [ ] Le LLM sait identifier les lacunes et demander des recherches complementaires executables.
- [ ] Le LLM decide lui-meme de l'affectation d'une preuve a un slot.
- [ ] Le Writer recoit une selection stabilisee et non un ensemble ambigu de candidats bruts.
- [ ] La reponse finale respecte toutes les contraintes structurelles explicites.
- [ ] Les reponses complexes restent generalisables a d'autres domaines que la cuisine.
- [ ] Les boucles sont bornees et ne durent pas quinze minutes sans progression utile.

### 2.3. Scenario de reference - planning de repas

Question canonique :

> Prepare-moi un planning de repas du lundi au vendredi. Pour chaque jour, propose un petit-dejeuner, un dejeuner - repas de midi -, une collation et un souper - repas du soir. Appuie chaque proposition sur les documents disponibles, n'invente rien, evite les repetitions inutiles et presente le resultat dans un tableau clair avec uniquement les sources reellement utilisees.

Variantes obligatoires a comprendre :

- [ ] `dejeuner`, `diner`, `repas de midi` ;
- [ ] `collation`, `gouter`, `encas` ;
- [ ] `souper`, `diner du soir`, `repas du soir` ;
- [ ] formulations sans accents ou avec fautes mineures ;
- [ ] ordre different des contraintes ;
- [ ] demande implicite equivalente, sans phrase canonique copiee.

Contrat de reussite :

- [ ] cinq lignes correspondant a lundi, mardi, mercredi, jeudi et vendredi ;
- [ ] quatre colonnes de contenu correspondant a petit-dejeuner, dejeuner, collation et souper ;
- [ ] vingt cases remplies ;
- [ ] chaque case contient une proposition concrete et comprehensible ;
- [ ] aucune case vide, uniquement sourcee, manifestement OCR-bruitee ou reduite a un fragment ;
- [ ] chaque proposition concrete est soutenue par une ou plusieurs preuves valides ;
- [ ] aucune invention d'ingredient, de recette ou de propriete non soutenue ;
- [ ] diversite jugee par le LLM, sans repetition inutile ;
- [ ] reutilisation d'une source autorisee si elle soutient reellement plusieurs cases ;
- [ ] liste finale limitee aux sources utilisees et dedupliquees ;
- [ ] cartes de sources ouvrables a la bonne page dans WinUI ;
- [ ] trois executions live consecutives reussies avec la question canonique ;
- [ ] plusieurs reformulations live reussies ;
- [ ] temps de reponse borne et compatible avec une utilisation professionnelle.

## 3. Etat de depart verifie le 2026-07-16

- [x] Branche locale : `SAAIA_V3.1`, alignee sur `origin/SAAIA_V3.1` au moment de la photographie (`0 0`).
- [x] Worktree tres volumineux : 261 entrees de statut, dont 34 modifications, 6 suppressions et 221 entrees non suivies.
- [x] Diff suivi observe : 40 fichiers, environ 6 875 insertions et 61 132 suppressions.
- [x] Aucun `AGENTS.md` trouve dans le depot.
- [x] Build solution obtenu le 2026-07-16 : succes, 0 avertissement, 0 erreur.
- [x] Tests canoniques SourceBacked obtenus le 2026-07-16 : 215/215 verts.
- [ ] Suite backend completement verte : cinq echecs restent a traiter.
- [ ] Suite client completement terminee : un harnais large reste sujet a un blocage/timeout.
- [ ] Scenario live planning valide : dernier essai du 2026-07-15 echoue apres 16 min 26 s.
- [x] Le dernier essai live recupere 12 puis 17 preuves, mais le juge LLM renvoie plusieurs fois `decision=answer` avec zero `selected_ids`.
- [x] Les reparations suivantes produisent encore citations manquantes, cases manquantes ou cellules repetitives.
- [x] L'ancienne derive de requete `Diner co` n'est plus le symptome principal dans le dernier essai : la requete initiale recente est complete.

## 4. Jalon A - Assainir la base Git et la validation deterministe

### 4.1. Comprendre les changements existants

- [ ] Classer les changements par lot fonctionnel : backend retrieval, ingestion/OCR, pipeline canonique, ancien runtime archive, tests, UI sources, runtime LLM, documentation, artefacts.
- [ ] Identifier les fichiers generes, temporaires ou de test live qui ne doivent pas etre versionnes.
- [ ] Verifier que les suppressions correspondent a des deplacements ou a une sortie explicite de compilation.
- [ ] Verifier les 221 entrees non suivies avant toute operation de nettoyage.
- [ ] Ne supprimer aucun fichier utilisateur non compris.
- [ ] Produire un inventaire des lots commitables et des blocages de chaque lot.

### 4.2. Retablir une base verte

- [ ] Corriger les quatre regressions backend liees aux routes/titres precis.
- [ ] Corriger le test de neutralite metier sans reintroduire de hardcoding cuisine.
- [ ] Rejouer la suite backend complete.
- [ ] Diagnostiquer le timeout ou blocage de la suite client.
- [ ] Rejouer les tests canoniques SourceBacked.
- [ ] Rejouer les tests memoire CDC.
- [ ] Rejouer `git diff --check` et distinguer erreurs reelles des avertissements de fin de ligne.
- [ ] Verifier qu'aucun processus `dotnet`, `vstest` ou `testhost` orphelin ne reste apres timeout.

### 4.3. Strategie de commits

- [ ] Commit 1 : ADR, analyse, reprises et TODO de reference.
- [ ] Commit 2 : refactor structurel deja valide, deplacements vers `OLD` et decoupage des fichiers.
- [ ] Commit 3 : corrections backend et retour au vert.
- [ ] Commit 4 : pipeline canonique, contrats LLM et EvidenceBundle.
- [ ] Commit 5 : memoire et observabilite.
- [ ] Commit 6 : UI et cartes de sources.
- [ ] Commit 7 : tests, harnais live et artefacts de validation strictement utiles.
- [ ] Chaque commit doit construire et passer ses tests cibles.
- [ ] Aucun secret, log volumineux, modele, base de donnees ou artefact machine ne doit etre commite.
- [ ] Le worktree final doit etre propre, ou chaque reliquat doit etre explicitement documente comme non committe.

## 5. Jalon B - Memoire : modele, cycle de vie et garanties

### 5.1. Les quatre espaces de memoire

- [ ] Memoire de question courante : objectif, contraintes, axes, slots, lacunes, tolerance a une reponse partielle.
- [ ] Journal de recherche : requetes, outils, resultats, pivots, echecs, non-progres et raisons.
- [ ] Inventaire de candidats : preuves retenues, faibles, rejetees par le LLM, doublons, bruit, contexte et lineage.
- [ ] Memoire longue duree : langue, style, preferences, decisions explicites, erreurs connues et contexte de reprise.

### 5.2. Alignement avec les memoires CDC

- [ ] M0 Policy Memory <= 300 tokens.
- [ ] M1-lite Workspace Canonical Memory <= 200 tokens.
- [ ] M3 Session Working Memory <= 150 tokens.
- [ ] M5 Corpus Memory utilisable pour orienter la recherche sans devenir une preuve.
- [ ] M6 Execution / Observability Memory exploitable pour traces et support.
- [ ] Historique conversationnel respecte son budget et ses invariants.
- [ ] Les invariants `activeLanguage`, `focalDocument`, `resolvedCategory`, `pendingClarification`, `lastUserMessage` et `lastAnswerPackage` ne sont pas supprimes par compaction.

### 5.3. Contrat d'usage de la memoire

- [ ] Toute information de memoire injectee dans un prompt est etiquetee comme contexte non probant.
- [ ] Le Planner sait distinguer memoire, catalogue d'outils et preuves actuelles.
- [ ] Le Writer ne peut citer la memoire comme source documentaire.
- [ ] Une preference utilisateur peut influencer format et strategie, jamais etablir un fait documentaire.
- [ ] Une source utilisee dans un tour precedent doit etre resolue ou relue avant de soutenir une nouvelle affirmation.
- [ ] Les usages de memoire sont traces avec type, provenance, age, budget et finalite.
- [ ] Les donnees sensibles ne sont jamais journalisees en clair dans les traces de support.

### 5.4. Compaction et continuite

- [ ] Remplacer ou encadrer l'estimation transitoire `chars / 4` par le tokenizer reel du profil actif.
- [ ] Tester les seuils de compaction 70 %, 85 % et 92 %.
- [ ] Verifier que la compaction ne supprime pas une contrainte structurante de la question complexe.
- [ ] Verifier qu'un suivi simple comprend les pronoms et references au tour precedent.
- [ ] Verifier qu'une nouvelle question non liee ne herite pas abusivement d'un document ou d'une categorie precedente.
- [ ] Verifier la persistance inter-session de l'historique, de la langue et du style selon le CDC.
- [ ] Verifier la non-persistance de `focalDocument`, `resolvedCategory`, `pdfMap` et du mode selon le CDC.

### 5.5. Tests memoire obligatoires

- [ ] Question simple puis suivi elliptique.
- [ ] Planning complexe interrompu par une clarification puis repris.
- [ ] Changement de langue entre deux tours.
- [ ] Source precedente modifiee ou invalidee avant un suivi.
- [ ] Memoire longue duree contenant un fait non retrouve dans le corpus actuel.
- [ ] Compaction sous forte pression de contexte.
- [ ] Redemarrage d'application et reprise de session.
- [ ] Absence de fuite entre deux utilisateurs ou sessions.

## 6. Jalon C - Intake et planification LLM uniques

- [ ] Definir un contrat d'intake generique : objectif, langue, format, axes, contraintes, sources attendues, besoin de clarification, tolerance partielle.
- [ ] Faire produire ce contrat par le LLM Router/Planner.
- [ ] Adapter le plan Router au pipeline canonique sans jeter puis recreer la decision semantique.
- [ ] Eliminer la double planification inutile.
- [ ] Preserver les libelles utilisateur dans la sortie tout en permettant au LLM de normaliser les synonymes dans son plan.
- [ ] Fournir au LLM le registre d'outils reel et compact, genere depuis les contrats actifs.
- [ ] Ne pas injecter les tools admin dans le rail conversation libre.
- [ ] Traiter le corpus comme donnees d'evidence, jamais comme instructions.
- [ ] Ajouter une instruction anti-injection explicite dans les prompts systeme concernes.
- [ ] Tester les questions simples, comparatives, extractives, de synthese et structurees.

## 7. Jalon D - EvidenceBundle canonique sans perte

### 7.1. Contrat minimal cible

- [ ] `evidenceId`
- [ ] `sourceKind`
- [ ] `toolName`
- [ ] `queryUsed`
- [ ] `docId`
- [ ] `docName`
- [ ] `docPath`
- [ ] `sourceHash`
- [ ] `revisionId` ou `indexedVersion`
- [ ] `pageStart` / `pageEnd`
- [ ] `chunkId` / `chunkIndex`
- [ ] `excerpt` / `normalizedExcerpt` / `contextualSnippet`
- [ ] `score` / `rerankScore` / `rank`
- [ ] `categoryPath` / `categoryRef`
- [ ] langue documentaire et langue de profil
- [ ] `exactMatchHit`, `chunkType`, `headingPath`, `hasTable`, `hasWarning`, `hypQuestionsMatched`
- [ ] `matchedContentCards`
- [ ] `selectionHints`, `riskFlags`, `extractionQuality`
- [ ] `lineage` et provenance/offsets

### 7.2. Propagation

- [ ] Raw tool results -> `EvidenceBundle` sans perte silencieuse.
- [ ] `EvidenceBundle` -> Evidence Judge avec format compact mais traçable.
- [ ] Decision du juge -> Writer avec toutes les preuves autorisees.
- [ ] Writer -> Source Contract Verifier avec les memes identifiants.
- [ ] Verifier -> Repair LLM sans reconstruction metier.
- [ ] Terminal answer -> payload UI derive du meme bundle.
- [ ] Payload UI -> cartes de sources avec metadonnees utiles conservees.
- [ ] Replay capable de reconstruire le tour depuis les tool results et sorties LLM enregistrees.

## 8. Jalon E - Evidence Judge LLM et couverture semantique

### 8.1. Contrat de decision

- [ ] Le juge renvoie une decision explicite : `answer`, `need_more_evidence`, `clarify`, `partial`, `refuse`.
- [ ] `answer` exige une selection LLM non vide et coherente avec les claims prevus.
- [ ] `need_more_evidence` exige au moins une action d'outil executable, ou une explication finale d'impossibilite.
- [ ] Le juge distingue preuve directe, contexte, navigation, doublon et bruit.
- [ ] Le juge fournit les lacunes restantes.
- [ ] Le juge cite le passage exact soutenant chaque proposition semantique importante.
- [ ] Le code verifie la presence de ce passage sans juger sa pertinence.

### 8.2. Structure generique de couverture

- [ ] Introduire une representation generique `axes -> slots -> besoins -> preuves candidates` issue de l'intake LLM.
- [ ] Ne pas introduire une classe produit specifique `MealPlan`.
- [ ] Laisser le LLM affecter les preuves aux slots.
- [ ] Verifier mecaniquement que tous les slots declares sont couverts avant le Writer.
- [ ] Renvoyer au LLM la liste exacte des slots manquants.
- [ ] Tracer chaque acceptation/rejet semantique et sa justification LLM.
- [ ] Eviter tout fallback qui selectionne automatiquement les premiers `EvidenceId`.

### 8.3. Recherche iterative

- [ ] Le LLM genere les recherches complementaires depuis les lacunes de couverture.
- [ ] Le code valide seulement outil, schema, scope, droits et budget.
- [ ] Detecter mecaniquement les requetes vides, identiques ou tronquees.
- [ ] En cas d'action invalide, demander au LLM une action corrigee plutot qu'inventer une recherche metier.
- [ ] Mesurer le progres entre deux tours : nouvelles preuves, nouveaux slots couverts, meilleure provenance.
- [ ] Arreter proprement apres non-progres repete et produire une reponse partielle honnete si le LLM le decide.

## 9. Jalon F - Writer, verifier et boucle de reparation

- [ ] Le Writer recoit la demande, l'intake, la decision du juge, la couverture et les preuves selectionnees.
- [ ] Le Writer ne recoit aucun contenu documentaire comme instruction systeme.
- [ ] Chaque claim documentaire concret est lie a un `EvidenceId`.
- [ ] Le Writer preserve langue, style, libelles et format demandes.
- [ ] Le Source Contract Verifier reste mecanique.
- [ ] Les erreurs du verifier sont typees et exploitables par le LLM.
- [ ] La reparation conserve les parties valides et ne regenere pas inutilement toute la reponse.
- [ ] Le LLM de reparation ne peut utiliser que les preuves autorisees.
- [ ] Les echecs JSON passent par une reparation de format bornee.
- [ ] Le juge d'adequation final evalue semantiquement clarte, utilite, completude et fidelite.
- [ ] Le code ne remplace jamais un echec du juge d'adequation par une decision metier arbitraire.

## 10. Jalon G - Deux rails de complexite, un meme contrat de preuve

### 10.1. Rail standard

- [ ] Intake/Router LLM.
- [ ] Un ou plusieurs tools documentaires.
- [ ] Writer LLM depuis `EvidenceBundle`.
- [ ] Verification mecanique.
- [ ] Critic absent sauf risque explicite.
- [ ] Cible : deux appels LLM maximum dans 90 % des tours standard.

### 10.2. Rail complexe

- [ ] Intake/Planner LLM avec axes et contraintes.
- [ ] Retrieval initial.
- [ ] Evidence Judge LLM avec couverture et lacunes.
- [ ] Iterations de recherche bornees.
- [ ] Writer LLM depuis couverture stabilisee.
- [ ] Verification et reparation ciblee.
- [ ] Adequation finale conditionnelle.
- [ ] Les couts supplementaires sont justifies par la complexite, traces et plafonnes.

### 10.3. Selection du rail

- [ ] La selection repose sur l'intake LLM et des signaux structurels mecaniques, pas sur une liste de domaines.
- [ ] Une question simple ne doit pas etre promue en planning complexe.
- [ ] Une demande multi-axes ne doit pas etre reduite a une synthese simple.
- [ ] La transition entre rails est tracee et testable.

## 11. Jalon H - Sources UI et experience WinUI

- [ ] Deriver le payload final du `EvidenceBundle` canonique.
- [ ] Conserver snippets, scores, pages, hashes, cards et signaux utiles.
- [ ] Afficher uniquement les sources citees ou explicitement utiles a la reponse.
- [ ] Dedupliquer les cartes document/page sans perdre les citations multiples.
- [ ] Ouvrir le bon PDF a la bonne page.
- [ ] Afficher un etat de progression comprehensible pendant recherche, jugement, redaction et reparation.
- [ ] Eviter la repetition pendant quinze minutes du meme message generique de progression.
- [ ] Afficher une insuffisance reelle avec une explication utile, sans exposer le jargon interne.
- [ ] Verifier le rendu du tableau complexe et des reponses simples.
- [ ] Verifier clavier, redimensionnement, themes, langues et accessibilite minimale.

## 12. Jalon I - Securite, confidentialite et observabilite

- [ ] Le contenu documentaire ne peut modifier le rail, les droits ou les outils sensibles.
- [ ] Les tool results sont serialises comme donnees structurees.
- [ ] Les prompts systeme rappellent que le corpus n'est pas une instruction.
- [ ] Chaque tour porte un `traceId` stable.
- [ ] Les appels LLM enregistrent role logique, duree, budget, resultat de parsing et motif de retry.
- [ ] Les decisions de memoire enregistrent type, taille et finalite sans contenu sensible inutile.
- [ ] Les preuves conservent leur lineage.
- [ ] Les logs live produisent un artefact compact et lisible.
- [ ] Les support bundles sont redactes.
- [ ] Les logs, prompts et reponses utilisateur ne sont jamais commites par defaut.

## 13. Jalon J - Campagne de tests

### 13.1. Tests deterministes

- [ ] Build solution complet.
- [ ] Suite backend complete.
- [ ] Suite ToolAgent complete sans timeout.
- [ ] Tests SourceBacked canoniques.
- [ ] Tests memoire CDC.
- [ ] Tests contrat `EvidenceBundle` backend -> client -> UI.
- [ ] Tests Router plan -> pipeline canonique.
- [ ] Tests de reparation JSON et de contradiction de decision.
- [ ] Tests prompt injection documentaire.
- [ ] Tests multilingues et encodage UTF-8.
- [ ] Test de neutralite metier du runtime produit.

### 13.2. Banque de questions simples

- [ ] Fait precis dans un document.
- [ ] Question dont la reponse est absente.
- [ ] Resume d'un document.
- [ ] Comparaison de deux sources.
- [ ] Extraction d'une procedure.
- [ ] Question sur une norme ou reference exacte.
- [ ] Suivi conversationnel elliptique.
- [ ] Changement de langue.
- [ ] Source modifiee ou invalidee.

### 13.3. Banque de questions complexes

- [ ] Planning de repas 5 x 4.
- [ ] Planning de maintenance multi-equipement.
- [ ] Tableau comparatif multi-criteres.
- [ ] Plan d'action multi-etapes base sur plusieurs documents.
- [ ] Synthese multi-source avec contraintes contradictoires.
- [ ] Demande partiellement couverte necessitant clarification ou reponse partielle.

### 13.4. Live et UI

- [ ] Probe direct du corpus Cuisine pour confirmer qu'il contient assez de propositions legitimes.
- [ ] Un live diagnostique du planning avec traces completes.
- [ ] Trois lives consecutifs reussis sur la formulation canonique.
- [ ] Lives reussis sur reformulations.
- [ ] Lives simples reussis avec sources utiles.
- [ ] Parcours WinUI reel pour reponse simple.
- [ ] Parcours WinUI reel pour planning complexe.
- [ ] Ouverture et verification des sources depuis les cartes UI.

## 14. Ordre d'execution concret

1. [x] Creer l'objectif persistant de discussion.
2. [x] Relire l'ADR source-backed et les sections CDC prioritaires.
3. [x] Photographie Git et validation de l'etat existant.
4. [x] Creer ce TODO racine.
5. [ ] Auditer en profondeur l'implementation actuelle de la memoire.
6. [ ] Classer les changements Git par lots commitables.
7. [ ] Retablir les tests backend verts.
8. [ ] Fermer le timeout du harnais client.
9. [ ] Ajouter les tests de contradiction et de couverture generique manquants.
10. [ ] Faire converger le contrat d'intake/plan Router vers le pipeline canonique.
11. [ ] Completer `EvidenceBundle` et sa propagation jusqu'a l'UI.
12. [ ] Structurer la decision LLM de couverture et les recherches complementaires.
13. [ ] Stabiliser Writer, verifier et reparation ciblee.
14. [ ] Revalider les reponses RAG simples et leur budget.
15. [ ] Rejouer le planning live jusqu'a reussite reproductible.
16. [ ] Valider WinUI et les cartes de sources.
17. [ ] Nettoyer fichiers temporaires, artefacts et ancien code prouve mort.
18. [ ] Creer les commits coherents et verifies.
19. [ ] Produire le rapport final avec preuves, limites et commandes de reproduction.

## 15. Regles de suivi du chantier

- Mettre a jour les cases uniquement apres preuve de code ou de test.
- Conserver dans ce fichier la derniere commande de validation et son resultat.
- Ne jamais marquer le planning comme valide sur la seule base de tests simules.
- Ne jamais presenter une amelioration partielle comme une reussite live.
- Envoyer un rapport Telegram a chaque jalon significatif : TODO cree, base verte, memoire fermee, pipeline simple valide, planning live valide, WinUI valide, commits termines.
- En cas de blocage long, documenter le symptome exact, les hypotheses invalidees et la prochaine experience discriminante.

## 16. Journal d'execution

### 2026-07-16 - Initialisation

- Objectif persistant cree pour la discussion.
- ADR relu integralement.
- Sections CDC memoire, pipeline conversationnel, budget contexte, evidence pack, isolation documentaire et criteres d'acceptation relues.
- Depot confirme fortement modifie et non encore commitable en un seul lot sûr.
- Dernier echec live recent rattache a une contradiction du juge LLM et a une reparation non convergente, pas uniquement a l'ancienne derive `Diner co`.
- Prochaine action : audit implementation/test de la memoire puis classification Git avant correction de la base rouge.
