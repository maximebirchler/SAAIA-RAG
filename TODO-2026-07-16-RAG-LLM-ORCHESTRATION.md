# TODO - RAG source-backed pilote par le LLM

> Cree le : 2026-07-16
> Statut : chantier actif
> Branche observee : `SAAIA_V3.1`
> Objectif : rendre fiables et professionnelles les reponses RAG simples et les demandes complexes structurees, sans retirer au LLM son role d'orchestrateur semantique principal.
> References prioritaires :
> - `ADR-2026-07-08-rag-llm-orchestration-source-backed.md`
> - `ANALYSE-2026-07-16-CARTOGRAPHIE-COMPLETE-RAG.md`
> - `CDC Agent AI - RAG - V3.1.md`, notamment memoires, assistant conversationnel, context budgeting, evidence pack, isolation du corpus et criteres d'acceptation

## Point courant - 2026-07-26 20:20 : refus live correctement justifie, vivier semantique encore trop pauvre

### Valide

- [x] L'identite mecanique d'une carte canonique ne depend plus de la longueur
  de l'extrait retourne par l'outil : une meme carte stable observee par
  `documents.content_cards` puis `rag.search` ne devient plus deux preuves.
- [x] Lors d'une fusion mecanique, la representation paginee, structuree et la
  plus riche est conservee sans que le code decide de sa pertinence semantique.
- [x] `kind`, `profileVersion`, `hasGroundedEvidence`, `queryScore` et
  `contentCardId` suivent maintenant les cartes jusqu'au contexte compact du
  LLM et au juge.
- [x] Le juge semantique peut retourner `need_more_evidence`. Cette decision
  rend de nouveau les outils documentaires disponibles au LLM, y compris dans
  les tours auparavant reserves a la finalisation.
- [x] Apres une nouvelle observation utile, le LLM peut refaire sa selection
  en tenant compte du retour du juge et des nouvelles preuves.
- [x] 40/40 tests `SourceBackedAgentV2Tests` passent, dont :
  - fusion d'une carte stable observee par deux outils ;
  - conservation de la variante paginee la plus riche ;
  - reouverture de la recherche apres `need_more_evidence` ;
  - nouvelle selection puis acceptation de la preuve obtenue ;
  - audit LLM exhaustif de 24 cartes avant selection ;
  - retrait exact des cartes refusees du contexte de travail ;
  - echec ouvert sans rejet semantique implicite si le contrat d'audit est
    incomplet.
- [x] Le live du 2026-07-26 a mesure la saturation de la sortie d'action :
  Qwen3 a tente quatre appels paralleles apres `need_more_evidence`, mais le
  quatrieme JSON a ete tronque par la limite de 320 tokens.
- [x] Porter la valeur par defaut de `MaximumActionTokens` a 480 : la limite
  reste bornee, mais ne coupe plus un lot raisonnable de quatre appels natifs.
- [x] Inclure arguments et cle mecanique dans les traces d'appels rejetes ou
  dupliques afin de distinguer repetition semantique et simple erreur de
  serialisation.
- [x] Auditer le troisieme live
  `artifacts/client-live-final-weekly-meal-plan-20260726-155150` : Qwen3 a
  construit plusieurs grilles mecaniquement sourcees mais le juge a refuse
  cinq valeurs generiques, incoherentes ou dupliquees; le pipeline a termine
  sans source plutot que de publier une reponse fabriquee.
- [x] Confirmer sur l'inventaire live que la cause dominante est la pollution
  semantique des cartes (`Servir cette recette`, `Astuce Realiser la recette`,
  fragments et mobilier de page), et non une perte des identifiants
  fichier/page dans l'`EvidenceBundle`.
- [x] Migrer dans v2 un audit generique de candidats pilote par le meme Qwen3 :
  il classe chaque `EvidenceId` en accepte ou rejete; le code verifie
  uniquement couverture exhaustive, unicite, ensemble autorise et absence de
  chevauchement.
- [x] Limiter cet audit aux demandes structurees multi-instances, auditer
  chaque page non vide par lots de 24 a temperature 0 et conserver les
  demandes simples hors de ce cout supplementaire.
- [x] Compiler WinUI puis rejouer la suite v2 : build reussi et 38/38 tests
  verts, puis 40/40 apres fermeture des contournements de pagination et de
  doublons inter-lots le 2026-07-26.
- [x] Auditer le live
  `artifacts/client-live-final-weekly-meal-plan-20260726-164558`, trace
  `rag-20260726164559253-cdd5ca40` : 40 cartes auditees en deux lots,
  20 acceptees et 20 rejetees; echec final apres 18 min 50 et 93 preuves.
- [x] Identifier les deux contournements restants de ce live :
  - les pages suivantes de 20 cartes passaient sous l'ancien seuil de 24 ;
  - les doublons situes entre deux lots preliminaires n'etaient jamais compares
    globalement.
- [x] Auditer desormais toute page de cartes d'une demande structuree, quelle
  que soit sa taille; `24` est uniquement la taille maximale d'un lot.
- [x] Ajouter une reconciliation globale LLM des candidats preapprouves par
  plusieurs lots, sans quota d'acceptation et sans deduplication semantique
  codee.
- [x] Mettre en cache pendant le run les decisions valides par `EvidenceId`
  afin qu'une carte stable reobservee ne repaie pas un audit identique.
- [x] Etendre la trace `source_fields` a 1 200 caracteres pour conserver les
  listes de rejets et motifs semantiques utiles, tout en gardant la rotation
  des journaux a 10 Mo.
- [x] Auditer le live
  `artifacts/client-live-final-weekly-meal-plan-20260726-171324`, trace
  `rag-20260726171325139-806a3243` : test reussi en 17 min 12 s, tableau
  5 x 4, 20 citations et 20 sources resolues vers fichier, page et chunk.
- [x] Ne pas confondre ce succes mecanique avec une reponse parfaite :
  `Sel Poivre du moulin` a ete accepte a tort comme recette et plusieurs
  affectations de petit-dejeuner ne sont pas professionnelles. Le juge LLM
  final a donc produit un faux positif semantique.
- [x] Remplacer les deux listes d'audit fragiles par une table
  `decisions` dont le schema exige explicitement chaque `EvidenceId`; le
  parseur controle de nouveau l'exhaustivite, les valeurs autorisees et
  l'ensemble exact.
- [x] Recentrer la revue finale sur les preuves effectivement citees, avec
  valeur, fichier, page et extrait plus riche, puis montrer separement au plus
  24 alternatives afin de reduire la dilution du jugement.
- [x] Expliciter au LLM qu'un ancrage structure ou un nombre de portions ne
  transforme pas un ingredient ou fragment de continuation en instance, et
  que l'ordre des EvidenceId dans une grille est lui-meme une decision
  semantique d'affectation.
- [x] Garder le libre choix des outils tout en clarifiant leur contrat :
  un axe purement visuel ne doit pas devenir une requete, mais une contrainte
  qui change reellement la nature ou l'usage du contenu peut guider `q` ou
  `rag.search`.
- [x] Rejouer apres ces changements la suite x64 : 40/40 tests
  `SourceBackedAgentV2Tests` passent sans avertissement ni erreur de build.
- [x] Corriger le harnais reproductible `tools/run-live-model-e2e.ps1` :
  v2, audit, taille de lot et budget sont maintenant explicites dans les
  parametres et l'artefact; `--no-build` n'est plus implicite.
- [x] Auditer le live
  `artifacts/client-live-final-weekly-meal-plan-20260726-175605`, trace
  `rag-20260726175606006-a6733362` : echec propre apres 22 min 21 s,
  91 preuves observees et aucune source publiee.
- [x] Confirmer que le juge semantique ne se contente plus d'une grille
  mecaniquement valide : il a refuse successivement `PUMSTECK AUX OIGNONS
  GRILLES`, `RECETTE DU QUEBEC`, `PETITS PAIN DORE DEJ`, un titre de document,
  une liste d'ingredients et plusieurs preuves trop faibles.
- [x] Localiser la cause dominante de cet echec en amont : l'audit de la
  seconde page a rejete douze cartes sur vingt, dont plusieurs desserts et
  collations nommes et utilisables. Le LLM final ne disposait donc plus d'un
  vivier propre suffisant pour remplacer les mauvaises cellules.
- [x] Corriger le prompt d'audit sans coder de categorie culinaire : auditer
  uniquement le type atomique du plan et accepter une recette nommee
  independamment de son service futur; la decision d'affectation reste au LLM.
- [x] Corriger la reconciliation globale pour preferer, entre variantes
  semantiques dupliquees, le titre autonome et linguistiquement propre plutot
  qu'une variante OCR choisie par ordre de source.

### Validation live en cours

- [x] Terminer et auditer le live lance a 19:56 dans
  `artifacts/client-live-final-weekly-meal-plan-20260726-175605`, trace
  `rag-20260726175606006-a6733362`, avec audit exhaustif par cle, contexte de
  juge recentre et binaire x64 explicitement reconstruit.
- [x] Verifier dans sa trace les lots audites, les listes acceptees/refusees,
  les recherches ou paginations choisies ensuite par Qwen3, les preuves
  distinctes, la decision finale du juge, les 20 cellules, les 20 citations
  et les cartes source fichier/page.
- [x] Comparer objectivement ce run au precedent : duree, appels LLM, tokens,
  appels d'outils, candidats propres exposes, diversite fichier/page et motif
  terminal. Il est plus lent que le succes mecanique precedent, mais le juge
  a supprime le faux positif : la qualite finale est refusee explicitement.
- [x] Rejouer les 40 tests v2 apres compilation du prompt d'audit corrige :
  40/40 passent en x64 Debug, sans avertissement ni erreur de build.
- [ ] Relancer ensuite un live repas avec le vivier atomique non surfiltre et
  verifier manuellement chaque cellule, pas seulement le statut du test.
- [x] Invalider et interrompre le faux depart de 18:39 : `--no-build` pointait
  vers un ancien dossier RID x64. Le protocole live reconstruit maintenant
  explicitement cette cible avant lancement.
- [ ] Ne pas promouvoir v2 sur ce seul essai : jouer ensuite question simple,
  document nomme, hors corpus et chemin WinUI.

### Dette prouvee, a supprimer sans conserver de cadavres

- [x] Supprimer `client/SAAIA.Client.WinUI/ToolAgent/OLD` : ces anciennes
  branches etaient exclues de la compilation et gardees uniquement comme
  parking de reference.
- [x] Retirer du projet les regles `Compile Remove` / `None Include` propres a
  ce parking.
- [x] Remplacer les tests qui exigeaient la presence des archives par des
  garanties d'absence des anciens symboles dans le chemin actif.
- [ ] Compiler et rejouer les tests d'architecture apres la fin du live.
- [ ] Promouvoir v2 avant de supprimer v1 : v1 reste encore le fallback actif
  tant que le feature flag v2 est desactive par defaut.
- [ ] Apres promotion, supprimer les prompts, sous-juges et reparations v1
  devenus inaccessibles, puis prouver par compilation, tests et recherche de
  references qu'aucun consommateur ne subsiste.
- [ ] Supprimer egalement tous les cadavres indirects de v1 : feature flags
  sans effet, options, branches de routage, adaptateurs, enregistrements DI,
  tests couples a l'implementation, fixtures, scripts, documentation et
  variables d'environnement qui n'ont plus de consommateur.
- [ ] Ne conserver aucun fallback dormant « au cas ou ». Une garantie encore
  necessaire doit etre migree vers un contrat public v2 et couverte par test;
  le reste doit etre supprime.
- [ ] Apres chaque lot de suppression : rechercher les references par symbole
  et chaine, compiler, rejouer les tests cibles puis la suite elargie, et
  verifier que le chemin de production n'offre plus qu'une architecture.
- [ ] Nettoyer les microprobes et artefacts de benchmark redondants en
  conservant uniquement le protocole reproductible, les resultats de
  promotion et les preuves necessaires aux regressions.
- [ ] Dette mesuree au 2026-07-26 : supprimer, apres promotion, les 159
  fichiers v1 `SourceBackedRagPipeline*`, `SourceBackedRagPrompts*` et
  `SourceBackedRagJson*` (environ 24 142 lignes), ainsi que les 16 fichiers
  de tests encore couples a ces symboles.

### Ingestion a remettre a plat, sans nouvelle accumulation d'heuristiques

- [x] Le diagnostic live de `documents.content_cards` prouve que des cartes
  historiques deterministes peu pertinentes restent dans l'inventaire, en
  particulier des `exact_lead` non rattachees a une preuve structuree.
- [x] `DocumentProfileEnrichmentService` ne suffit pas encore a les remplacer :
  il ne voit qu'un echantillon du document et fusionne ensuite toutes les
  cartes deterministes de fallback.
- [ ] Concevoir une extraction semantique paginee/batchable pilotee par le LLM
  pour les unites nommees, avec identite document/page/chunk stable.
- [ ] Comparer cette ingestion a l'existant sur un corpus annote avant
  remplacement; ne pas ajouter un nouveau filtre lexical propre aux recettes.
- [ ] Une fois la couverture prouvee, supprimer le projecteur et les lexiques
  deterministes devenus sans consommateur au lieu de les conserver en mode
  legacy.

## Decision d'architecture - 2026-07-25 : passer a un agent source-backed v2

Reference complete :
`AUDIT-RAG-QWEN3-2026-07-25.md`.

### Verdict prouve

- [x] La direction produit reste LLM-first : Qwen3 choisit librement zero, un
  ou plusieurs outils selon la demande, ses observations et ses lacunes.
- [x] Le code conserve les schemas, permissions, budgets, traces, identites de
  document/page/chunk, doublons exacts, structure demandee et verification des
  citations. Il ne choisit pas les sources ou recettes sur le fond.
- [x] Le correctif de visibilite des ancres tardives de navigation est compile
  et couvert par 4/4 tests cibles Debug x64. Il reste une garantie mecanique
  utile, mais ne repare pas l'architecture a lui seul.
- [x] Le live
  `artifacts/live-navigation-visible-orchestrator-x64-20260725-2133`
  a echoue apres exactement 24 minutes, sans reponse, pendant un nouvel appel
  LLM de compatibilite canonique.
- [x] Sa trace `rag-20260725193218537-1f653fed` contient 1 navigation,
  6 recherches RAG et 1 ouverture de contexte, pour environ 1,21 million de
  caracteres de resultats d'outils bruts. Le pipeline ne manquait donc pas de
  donnees ; il ne savait plus converger.
- [x] Le seul contexte cible de ce live a utilise un `docRef` opaque, pages
  5-7, et n'a retourne que 520 caracteres, tandis que les bonnes ancres etaient
  diluees par les recherches generales et les sous-juges.
- [x] Le banc natif reproductible
  `tools/benchmark-native-agent-loop-existing-server.ps1` expose les outils
  OpenAI a Qwen3 sans imposer leur nombre ni leur ordre.
- [x] Avec les parametres officiels Qwen3 et des observations compactes, le
  banc natif a reussi :
  - une reformulation sans outil en 2,354 s ;
  - une question de document nomme en 16,076 s, avec 85 N m,
    `HydraulicPumpManual.pdf`, page 42 ;
  - le plan de repas 5 x 4 en 95,175 s, avec 5 appels choisis par le modele,
    zero doublon et les 20 cases.
- [~] Le plan natif cite les quatre fichiers et plages de pages, mais regroupe
  encore les sources par colonne. Chaque cellule doit conserver son
  `evidenceId` individuel jusqu'a l'interface.
- [x] Une variante qui renvoyait vingt cartes repetees a chaque tour a produit
  une erreur HTTP 500. La compaction des observations et l'absence de
  duplication sont donc des exigences de protocole.

### Regle immediate

- [ ] Geler toute nouvelle heuristique metier ou micro-etape propre au plan de
  repas dans le chemin legacy.
- [ ] N'accepter sur le legacy que les corrections de provenance, securite,
  schema, observabilite ou contrat mecanique.
- [ ] Ne plus poursuivre les anciennes taches qui ajoutent un nouveau juge,
  une nouvelle facette codee en dur ou une nouvelle cascade avant le banc
  comparatif v1/v2.

### P0 - figer le temoin legacy et le mesurer

- [ ] Nommer/configurer le chemin actuel `legacy-source-backed-v1`.
- [ ] Versionner les traces de reference : question simple, document nomme,
  multi-source et plan de repas.
- [ ] Capturer par run : appels LLM, prompts/completions, tokens, contexte,
  temps, outils, volume d'observations, preuves retenues et raison terminale.
- [ ] Ajouter un detecteur mecanique de saturation avant envoi : tokens
  d'entree + sortie reservee ne doivent jamais depasser le contexte.
- [ ] Tester `--no-context-shift` sur le profil agent pour interdire une
  eviction silencieuse des instructions ou preuves.

### P1 - golden set et metriques

- [ ] Creer un corpus versionne de questions simples, document nomme,
  multi-sources, hors corpus et planifications structurees.
- [ ] Annoter document, page, passage minimal, reponse attendue, abstention,
  contraintes et erreurs critiques.
- [ ] Mesurer Recall@k document/page, MRR, nDCG, precision des ancres et bruit.
- [ ] Mesurer choix d'outil, arguments, abstention, boucles, tokens et latence.
- [ ] Mesurer exactitude, completude, fidelite, citation precision/recall et
  resolvabilite fichier/page.
- [ ] Calibrer tout juge automatique sur une lecture humaine ; aucun score LLM
  unique ne doit decider seul de la promotion.

### P2 - construire `source-backed-agent-v2` derriere un feature flag

- [ ] Implementer une boucle agent compacte :
  demande + outils + working memory + EvidenceBundle -> action(s) ou reponse.
- [ ] Exposer les outils actuels sans nombre, ordre ni parcours impose :
  navigation, recherche hybride, contexte cible, sources et memoire.
- [ ] Renvoyer au LLM seulement les observations utiles et compactes, avec
  labels temporaires stables relies aux IDs canoniques.
- [ ] Maintenir une working memory explicite : objectif, contraintes, actions,
  observations, preuves retenues, lacunes et budget restant.
- [ ] Permettre au LLM de choisir les recherches, leur ciblage et l'arret.
- [ ] Alimenter un seul `EvidenceBundle` partage par agent, writer,
  verificateur, cartes de sources et traces.
- [ ] Verifier mecaniquement chaque cellule/affirmation vers un `evidenceId`
  existant, un fichier et une page.
- [ ] Autoriser une seule reparation LLM apres echec mecanique ; sinon produire
  une reponse partielle honnete.
- [ ] Ne supprimer aucune garantie de permission, confidentialite ou isolation
  du corpus pendant la simplification.

### P3 - qualifier le protocole Qwen3

- [ ] Comparer avec memes seeds et memes requetes :
  - A1 JSON Schema actuel, tres simplifie ;
  - A2 `tools` OpenAI natifs via `llama.cpp --jinja` ;
  - A3 template/parseur Qwen-Agent `nous/Hermes`.
- [ ] Tester le multi-tour, les appels multiples, l'abstention, les arguments
  complexes, la reprise apres erreur et l'historique non empoisonne.
- [ ] Tester par role les temperatures 0/0,2/0,7, puis `top_p`, `top_k`,
  `min_p` et penalties actuels face au profil officiel.
- [ ] Tester 8K, 12K et 16K sur la machine reelle avec sortie reservee et
  sans context shift silencieux.
- [ ] Comparer Q5_K_M, Q6_K et eventuellement Q8_0 sur qualite agent/citations,
  pas seulement tokens/s.
- [ ] Executer au moins trois seeds pour toute variante stochastique promue.

### P4 - retrieval, ingestion et materiel

- [ ] Activer et qualifier un reranker plutot que compenser avec des `topK`
  toujours plus grands.
- [ ] Comparer `multilingual-e5-base` au
  `Qwen3-Embedding-0.6B`, avec nouvel index versionne.
- [ ] Comparer `Qwen3-Reranker-0.6B` et un reranker leger de reference.
- [ ] Ajouter un adaptateur de fournisseur TEI/OpenVINO :
  `/v1/embeddings`/`/rerank` versus `/v3/embeddings`/`/v3/rerank`.
- [ ] Qualifier OpenVINO Model Server sur Intel UHD pour embeddings/reranking,
  Quadro P520 pour Qwen3 et CPU pour BM25/Qdrant/SQL.
- [ ] Conserver un profil CPU-only et des profils materiels adaptes aux autres
  machines.
- [ ] Comparer ingestion SAAIA, Docling Hierarchical/Hybrid et arbre
  PageIndex-like sur rappel de page, tableaux, OCR, citations, temps et RAM.
- [ ] Tester une variante de Contextual Retrieval sans alterer le texte source
  ni sa provenance.

### P5 - promotion, nettoyage et commits

- [ ] Promouvoir v2 seulement s'il bat v1 sur qualite, citations, stabilite et
  latence, sans regression simple RAG.
- [ ] Valider au moins trois plans de repas complets et plusieurs
  reformulations, plus une question simple et un document nomme.
- [ ] Valider le chemin WinUI reel et l'ouverture des cartes source.
- [ ] Modulariser progressivement `RagEndpoints.cs` en services exact,
  sparse, dense, fusion, rerank et projection.
- [ ] Supprimer les prompts/classes propres aux repas du coeur generique.
- [ ] Conserver les tests de regression mais les rebrancher sur les contrats
  publics v2.
- [ ] Nettoyer les artefacts non utiles sans toucher aux changements utilisateur
  ou aux captures necessaires.
- [ ] Committer par lots reversibles : instrumentation, banc, agent v2,
  provenance/citations, retrieval/OpenVINO, migration et nettoyage.

### Gates de sortie

- [ ] Question simple correcte, rapide, source-backed.
- [ ] Document et page resolvables depuis chaque citation visible.
- [ ] Plan 5 x 4 avec 20 cellules coherentes ou explication exacte des preuves
  manquantes.
- [ ] LLM libre de choisir ses outils et d'arreter.
- [ ] Aucune memoire utilisee comme preuve.
- [ ] v2 objectivement meilleur que v1 sur le golden set.
- [ ] Latence p50/p95 mesuree et acceptable.
- [ ] Profils Quadro + Intel, NVIDIA seule, Intel seule et CPU-only qualifies.
- [ ] UI alimentee uniquement depuis l'EvidenceBundle canonique.
- [ ] Tests live et WinUI pertinents verts.
- [ ] Depot nettoye et changements verifies committes.

## Point courant - 2026-07-25 : navigation documentaire et inventaire canonique Qwen3

### Résultats désormais prouvés

- [x] Le modèle retenu pour le pipeline local est
  `Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf`; les essais applicatifs utilisent
  le serveur externe qualifié sur `http://127.0.0.1:12662/v1`.
- [x] Le Planner LLM choisit réellement `documents.navigation` en complément
  des recherches de contenu pour le planning de repas.
- [x] La sonde directe
  `artifacts/live-document-navigation-20260725-141053.txt` prouve que la
  catégorie `Cuisine`, interrogée avec `recette`, expose 200 entrées sur 200
  avec un document et une page mécaniquement identifiables.
- [x] Le live
  `artifacts/client-live-final-weekly-meal-plan-20260725-135258` prouve le
  parcours complet sommaire vers contenu : le LLM a sélectionné
  `Cuisine/Je_cuisine_simplement.pdf`, page 38, depuis la carte de navigation,
  puis a demandé un `rag.search` ancré sur ce fichier et cette page.
- [x] Le live
  `artifacts/client-live-final-weekly-meal-plan-20260725-142906` a reproduit
  le même parcours avec la plage 38-39. La perte historique entre « bonne
  source repérée » et « fichier/page utilisable » n'est donc plus le blocage
  principal de ce chemin.
- [x] Les entrées `navigation_map` restent de l'orientation, jamais une preuve
  finale. Seul le contenu relu par RAG devient citable.
- [x] Le jugement carte-colonne est maintenant confié à un LLM focalisé :
  le code ne décide pas si une recette est un petit-déjeuner, un déjeuner, une
  collation ou un souper; il valide seulement le JSON, les références connues,
  les comptes distincts exigés par la politique LLM et la faisabilité de la
  grille.
- [x] Les deux derniers lives ont refusé de fabriquer un faux tableau :
  le premier ne disposait que de 3 collations et 4 soupers compatibles; le
  second de 4 collations et 2 soupers. Le garde-fou de qualité fonctionne,
  même si la réponse finale n'est pas encore obtenue.
- [x] Une insuffisance d'inventaire canonique est désormais classée comme une
  défaillance récupérable. Les déficits exacts sont transmis au LLM de reprise.
- [x] La reprise finale utilise un contrat plat et court demandant au LLM une
  seule nouvelle action sûre. Le code n'invente ni la requête, ni l'outil, ni
  le document, ni la page.
- [x] Le live
  `artifacts/client-live-final-weekly-meal-plan-20260725-145804` a terminé
  proprement en 29 min 30 : 181 éléments initiaux, puis 185, 188 et 189 après
  trois lectures supplémentaires. Il a refusé la réponse finale avec seulement
  3 collations et 3 soupers compatibles. Les deux reprises de l'ancien binaire
  copiaient encore des notations de page dans la requête et la première visait
  à tort un petit-déjeuner déjà suffisant.
- [x] Le contrat plat de reprise structurée reçoit maintenant les déficits
  exacts, interdit de pivoter vers une facette déjà suffisante et demande une
  recherche non ancrée lorsque l'ancre visible ne correspond pas au manque.
- [x] Le live diagnostique
  `artifacts/client-live-final-weekly-meal-plan-20260725-154128` a été arrêté
  volontairement dès que le défaut recherché a été prouvé : le plan compact
  avait choisi quatre recherches de contenu, mais aucun
  `documents.navigation`. La navigation fonctionnait donc quand elle était
  choisie, sans être fiable entre deux reformulations.
- [x] Une revue LLM dédiée décide désormais `use` ou `skip` après le plan
  compact canonique. Si elle choisit `use`, elle possède aussi la requête de
  sommaire et le chemin exact du catalogue; le code ne contrôle que le JSON,
  le chemin connu, la compacité et le budget maximal d'une navigation.
- [x] Validation ciblée du 25 juillet après ces changements : build
  0 avertissement/0 erreur, puis 9/9 tests réussis pour la revue de navigation,
  la reprise structurée, le jugement de compatibilité, l'inventaire complet et
  la rédaction des vingt références exactes.

### Limites observées et prochaines améliorations

- [ ] Relancer le live avec la nouvelle revue LLM de navigation et le contrat
  de reprise structurée compilé. Vérifier dans la trace la décision `use` ou
  `skip`, la requête choisie, le résultat du sommaire et l'action ciblée après
  un éventuel déficit.
- [ ] Vérifier que le nouvel inventaire contient au moins cinq valeurs
  compatibles et distinctes pour chacune des quatre colonnes, puis que le
  Writer produit exactement vingt cellules avec des citations visibles.
- [ ] Vérifier chaque source finale : intitulé réellement supporté, bon PDF,
  bonne page, absence de doublon inutile et carte UI ouvrable.
- [ ] Valider en live la nouvelle passe LLM focalisée de navigation : Qwen3
  choisissait encore `planning de repas hebdomadaire`, alors que le sommaire
  doit chercher la classe d'objets source ou une famille de candidats. La passe
  est implémentée sans réécriture lexicale codée en dur; sa décision et sa
  requête réelles restent à mesurer.
- [ ] Mesurer une sélection LLM de plusieurs ancres de sommaire avant lecture.
  Le backend expose jusqu'à 120 entrées au pipeline, mais le prompt générique
  de statut n'en montre qu'un petit échantillon. Pour une grille de vingt
  cellules, un juge de navigation borné pourrait sélectionner plusieurs
  fichiers/pages complémentaires, puis les faire relire en parallèle.
- [ ] Décider, après mesure, si une page demandée par navigation doit être une
  fenêtre stricte ou une préférence de classement. Le backend peut actuellement
  retourner une page proche du même document lorsqu'aucun passage de la plage
  ne répond à la requête; le résultat doit rester visible et ne jamais être
  confondu avec la page initialement choisie.
- [ ] Après le premier succès, répéter au moins trois runs complets et plusieurs
  reformulations de la demande de planning.
- [ ] Valider ensuite une question RAG simple : réponse directe, citation
  fichier/page, carte source UI, mémoire conversationnelle non probante et
  latence nettement inférieure au scénario 5 x 4.

## Point courant - 2026-07-24 : benchmark modèle équitable

- [x] Séparer officiellement le classement principal des modèles du pipeline
  historique compensatoire Qwen2.5.
- [x] Formaliser le protocole dans
  `MODEL-BENCHMARK-PROTOCOL-2026-07-24.md`.
- [x] Conserver le live Qwen3 de 14 min 29 s comme témoin de biais : le modèle
  avait produit de bonnes requêtes par facette, ensuite réécrites par la cascade
  historique, puis le pipeline a demandé une clarification erronée.
- [x] Rendre le banc de qualité paramétrable par modèle : température, `top_p`,
  `top_k`, `min_p`, graine, budget de sortie, traitement du rôle système et mode
  JSON contraint ou seulement demandé.
- [x] Capturer les templates et paramètres natifs effectifs des GGUF comparés.
- [x] Exécuter le régime natif avec au moins trois graines pour les scénarios
  stochastiques.
- [x] Exécuter le régime commun déterministe avec des budgets non tronquants.
- [x] Ajouter les appels d'outils natifs au corpus de comparaison.
- [x] Publier qualité, stabilité, latence et ressources séparément, sans score
  global opaque.
- [x] Introduire un profil d'orchestration fondé sur des capacités mesurées, pas
  sur le nom du modèle.
- [x] Tester le pipeline canonique minimal avec reranker, sans les micro-audits
  sémantiques hérités de Qwen2.5.
- [x] Comparer les candidats dans ce pipeline adapté et retenir Qwen3 4B
  Instruct 2507 Q5_K_M.
- [x] Classer l'ancien pipeline et les modèles non retenus comme historiques,
  puis supprimer leurs configurations et artefacts exécutables.

## Point courant - 2026-07-21 20:00

- Branche `SAAIA_V3.1`, chantier toujours actif : aucun succès fonctionnel final n'est revendiqué.
- Dernier live analysé : Live61. La grille 5 x 4, le plan compact, quatre recherches et un `EvidenceBundle` de 37 éléments sont atteints, mais le writer n'est pas appelé faute de sélection finale d'identifiants.
- Cause Live61 traitée : toute stratégie structurée mécaniquement acceptée, mais non déjà approuvée par une revue sémantique focalisée, passe désormais par `PlannerAcceptedQueryAudit` avant exécution.
- Frontière d'architecture préservée : le LLM conserve ou réécrit les requêtes et décide de leur qualité; le code garde seulement les index/outils/scopes/facettes immuables et rejoue les contrats mécaniques.
- État déterministe du lot : 299/299 tests SourceBacked, 379/379 SourceBacked/OpenAI/ApiClient, build 0 avertissement/0 erreur. Une régression supplémentaire d'arrêt sûr lorsque deux audits indépendants refusent l'approbation est ajoutée et doit être recompilée après le live en cours.
- Live62 démarré à 19:57. Gate immédiat : aucune requête faible du type `Facette Cuisine sources` ne doit atteindre le RAG sans être approuvée ou réécrite par l'audit LLM indépendant.
- Gates fonctionnels suivants : sélection explicite de preuves, writer, vingt cellules concrètes, audit des types, éventuel suivi RAG compact, citations vérifiées, au moins deux cartes de sources utiles, puis répétabilité sur trois runs et validation d'une question RAG simple.

## Mise a jour operationnelle du 2026-07-18

Etat valide apres le refactor LLM-first du pipeline canonique :

- [x] Build `SAAIA.Client.ToolAgent.Tests` : 0 avertissement, 0 erreur.
- [x] Suite cible `FullyQualifiedName~SourceBacked` : 281/281 tests verts apres la reparation LLM focalisee d'affectation des facettes issue du sixieme run live.
- [x] Couverture elargie `SourceBacked|OpenAi|ApiClient` : 360/360 tests verts.
- [x] Le Planner LLM produit un intake type : `taskKind`, contraintes, lignes, colonnes, libelle de premiere colonne, langue, tolerance partielle, focus de question et document explicitement demande.
- [x] Le code ne reconstruit plus la structure lundi-vendredi ni les creneaux depuis des listes de mots ou des regex metier.
- [x] Le code ne deduit plus la famille documentaire ni le nom du fichier depuis la question avec des regex.
- [x] Le code ne transforme plus une decision LLM `need_more_evidence` en `answer` ou en `clarify`.
- [x] Le code ne declare plus une preuve non citable parce que son role contient lexicalement `summary`, `profile`, `toc`, `index` ou `navigation`.
- [x] Les decisions de pertinence, de diversite et d'adequation finale sont confiees aux etapes LLM explicites ; le code conserve les controles de schema, de budget, de provenance, de citations et de structure.
- [x] Premier run live 5 x 4 analyse : `artifacts/client-live-final-weekly-meal-plan-20260718-072213` ; echec apres 7 min 28 s, reponse terminale de 223 caracteres et zero source.
- [x] Cause immediate prouvee par la trace `rag-20260718072242496-0fab0a5f` : quatre appels `rag.multi_search` sequentiels ont chacun expire a 35 s ; l'`EvidenceBundle` etait vide avant le juge.
- [x] Diagnostic backend direct : `cabillaud recette` repond en 27,8 s, tandis que la requete litterale complete du planning reste active au-dela de 180 s ; le probleme combine marge de delai trop faible et strategie de requetes trop litterale/couteuse.
- [x] Deuxieme run live 5 x 4 analyse : `artifacts/client-live-final-weekly-meal-plan-20260718-081312`, trace `rag-20260718081342993-6689370b` ; echec terminal apres 11 minutes, mais les quatre recherches ont reussi et l'`EvidenceBundle` contient 19 preuves, 4 tentatives, zero timeout et zero recherche degradee.
- [x] Nouveau blocage prouve : le relecteur LLM a restaure les axes 5 x 4 mais a accepte quatre requetes limitees aux creneaux du lundi ; le juge a ensuite retourne `decision=answer` avec zero `selected_ids`, puis `need_more_evidence` sans action executable.
- [x] La trace du deuxieme run montre aussi un `rowHeaderLabel` vide et des lacunes hallucinees hors contrat (`dessert` non demande) : l'audit du LLM doit etre rattache explicitement aux axes types avant le prochain run.
- [x] Le LLM de revue de strategie relit maintenant aussi `intakeDecision` et peut corriger lignes, colonnes et `rowHeaderLabel` avant execution.
- [x] Les requetes `rag.multi_search` consecutives compatibles, toujours choisies par le LLM, sont regroupees mecaniquement dans un seul appel afin d'utiliser le fan-out parallele existant.
- [x] L'`EvidenceBundle` conserve maintenant chaque tentative de recuperation : requetes, categorie, duree, resultat, erreur, surcharge et retrievers degrades.
- [x] Les prompts du juge distinguent explicitement un echec technique inconclusif d'une recherche terminee sans preuve ; le terminal ne dit plus que les documents sont absents apres un timeout.
- [x] La memoire est tracee par metadonnees et compteurs seulement ; son contenu sensible n'est pas journalise et reste du contexte non probant.
- [x] Le relecteur LLM doit maintenant declarer sa strategie de couverture des lignes (`row_independent_candidate_pool` ou `row_specific_evidence`) et la justifier ; le code verifie uniquement la presence et la coherence structurelle de cette decision.
- [x] Un intake tabulaire sans `rowHeaderLabel`, avec prefixes techniques visibles ou axes chevauchants est refuse mecaniquement et renvoye au LLM pour reparation.
- [x] Une grande grille typee conserve au moins une vraie iteration documentaire avant le budget-stop fonde sur l'abondance ; les quatre tours maximum restent la borne dure.
- [x] Les prompts Planner, selection de preuves et budget-stop sont compactes et proteges par des tests avec catalogue de 31 categories et option bank riche : Planner <= 8 500 caracteres, selection <= 10 000, budget-stop <= 9 000.
- [x] Troisieme run live 5 x 4 execute : `artifacts/client-live-final-weekly-meal-plan-20260718-085621`, trace `rag-20260718085652569-7d432de7` ; echec terminal apres 7 min 12 s, reponse d'insuffisance de 297 caracteres et zero carte source.
- [x] Le prompt Planner live mesure 7 951 caracteres, la revue de strategie 6 249 et la selection-repair 9 844 : les budgets corriges tiennent dans la fenetre locale.
- [x] La recuperation live est saine : une recherche `rag.multi_search` dans `Cuisine`, 10 hits issus de 10 documents/pages distincts, `EvidenceBundle` de 10 elements, zero timeout et zero degradation.
- [x] Nouveau blocage prouve : l'intake live a inverse/perdu la structure demandee (`row=Jour`, colonnes=Lundi a Vendredi, `rowHeaderLabel=Ligne`) et a omis les quatre creneaux repas ; le controle mecanique actuel ne detecte pas encore cette perte de libelles explicites.
- [x] Le relecteur LLM a choisi `row_independent_candidate_pool`, mais a accepte une seule requete large recopiant toute la grille ; le contrat doit verifier la coherence entre cette decision de couverture et les facettes/requetes annoncees, sans imposer de vocabulaire metier en code.
- [x] Le juge a de nouveau retourne `decision=answer` avec zero preuve selectionnee. La relecture Status a correctement juge une seule preuve utile et sept faibles, puis recommande `read_documents`, mais deux reparations LLM n'ont produit aucune action executable.
- [x] Une passe LLM focalisee `PlannerIntakeReview` reaudit maintenant une grille suspectement ecrasee depuis la question brute, declare `two_axis_grid|single_axis_table|non_tabular` et repare les axes avant la strategie de recherche.
- [x] Le pipeline canonique n'injecte plus les jours/creneaux detectes par les anciennes fonctions lexicales dans `SourceBackedIntake`; la structure active provient du LLM, puis le code ne controle que son contrat type.
- [x] Une strategie de recherche ne peut plus modifier silencieusement un intake semantique deja audite; elle doit l'omettre ou recopier exactement ses axes et son libelle de ligne.
- [x] Le prompt de strategie LLM audite maintenant la couverture des colonnes separement de l'independance des lignes et refuse explicitement de confondre vivier reutilisable par ligne avec facettes de colonnes interchangeables.
- [x] Le second essai de recuperation n'est plus une repetition du meme prompt : `EvidenceJudgeActionContractRetry` recoit les problemes mecaniques exacts et exige du LLM une action unique executable ou une clarification honnete.
- [x] La recommandation du `EvidenceStatusReview` est transportee vers la reparation d'action comme contexte LLM, sans etre transformee en choix d'outil par le code.
- [x] Quatrieme live canonique analyse : `artifacts/client-live-final-weekly-meal-plan-20260718-093009`, trace `rag-20260718093040513-59b4dc1b`; echec produit en 4 min 28 s, reponse creuse de 180 caracteres et deux cartes source.
- [x] Nouveau cas prouve : le Planner n'a pas ecrase une dimension, il a omis tous les axes (`effective_requested_axes` vide, `rowHeaderLabel` vide, `taskKind=rag.answer`), ce qui a contourne l'audit cible et la revue de strategie.
- [x] Le pipeline a alors traite la demande comme une reponse simple : une recherche large a retourne 9 preuves sans erreur, le Status a choisi `write_partial`, et le Writer a produit seulement une phrase d'introduction citee, acceptee mecaniquement faute de contrat 5 x 4 actif.
- [x] Le Planner initial, ses reparations de format/action et le parseur portent maintenant `structureReviewDecision`, `structureReviewKind` et `structureReviewReason`; une omission totale d'axes sans auto-audit LLM declenche la passe focalisee.
- [x] Prouver sur le cinquieme live que l'omission totale d'axes est reparee avant toute recherche et que la demande atteint le chemin structure 5 x 4.
- [ ] Obtenir un premier tableau 5 x 4 complet, concret et source apres ces corrections.
- [ ] Apres le premier succes : obtenir trois runs 5 x 4 consecutifs et plusieurs reformulations.
- [ ] Executer ensuite une question RAG simple live et verifier reponse directe, preuves, cartes UI et cout en appels LLM.

Decision de conception confirmee : la structure 5 x 4 n'est pas un objet metier `MealPlan`. Elle est un contrat generique de grille produit par le LLM (`rowLabels`, `columnLabels`, `rowHeaderLabel`) et verifie mecaniquement par le pipeline.

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

- [x] Un contexte memoire source-backed type et borne est construit depuis `ToolMemory` pour le Planner et le juge.
- [x] Le Writer canonique ne recoit pas le contenu de la memoire conversationnelle comme materiau de redaction.
- [x] Les prompts imposent qu'une source memorisee soit relue et presente dans l'`EvidenceBundle` courant avant selection ou citation.
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
- [x] Un live diagnostique du planning avec traces completes.
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
7. [x] Retablir les tests backend verts.
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

### 2026-07-16 - Premier lot memoire source-backed

- Ajout d'un `SourceBackedMemoryContext` type et borne : preferences, dernier tour, document focal, categorie resolue, clarification, ancres de sources et notes de recherche recentes.
- Injection uniquement dans les etapes LLM de planification/jugement qui ont besoin de continuite.
- Isolation volontaire du Writer : aucune reponse precedente ni ancre memorisee n'est fournie comme preuve de redaction.
- Contrat prompt explicite : la memoire sert a la continuite et a la strategie de recherche, jamais a prouver un fait.
- Une source precedente doit etre relue et reapparaitre dans l'`EvidenceBundle` courant avant toute selection/citation.
- Validation ciblee : `SourceBackedMemoryContextTests` 2/2 verts.
- Validation memoire combinee : 12/12 tests verts (`SourceBackedMemoryContextTests`, `MemoryCdcAlignmentTests`, `ToolMemoryCdcAlignmentTests`).
- Prochaine action : retablir les cinq tests backend rouges puis revalider le pipeline canonique.

### 2026-07-16 - Pipeline canonique, budgets locaux et live de 24 minutes

- Suite backend complete validee : 1954/1954 tests verts.
- Suite canonique `SourceBacked` validee apres compilation WinUI : 221/221 tests verts.
- `EvidenceBundle` canonique, memoire d'echec de redaction et fenetre top + observations recentes raccordes au cycle Judge -> Writer -> verifier -> recuperation.
- Le contexte memoire reste explicitement non probant et n'est jamais injecte dans le Writer comme source.
- Le minimum code de huit preuves a ete retire : le LLM choisit semantiquement le jeu utile, le code verifie seulement identite, provenance, budget et contrats.
- Les prompts de selection/reparation sont bornes pour le contexte local de 4096 tokens : dix preuves maximum pour la selection et 400 tokens de sortie pour le reparateur.
- Un garde-fou mecanique supprime les copies exactes d'une meme requete RAG et conserve la premiere priorite classee par le LLM. Les requetes distinctes et les outils documentaires restent intacts.
- Live canonique execute dans `artifacts/client-live-final-weekly-meal-plan-20260716-210627/` pendant 24 minutes.
- Le fan-out initial observe auparavant a ete ferme : une seule recherche `Cuisine` a ete executee au lieu de quatre copies inter-categories.
- La recherche restante etait toutefois encore une copie presque litterale de la question et a retourne notamment `Cuisine/facilitemps.pdf` p.63 (`Nos outils`).
- Les recuperations suivantes ont seulement navigue le catalogue puis lu `facilitemps.pdf` p.63 et `livre-recette-sist-2025-web.pdf` p.10.
- Les Writers n'ont selectionne que deux ou trois preuves et ont produit des tableaux essentiellement composes de marqueurs `[E1]` / `[E2]`.
- Les reparateurs structures ont remplace les marqueurs par du texte, mais ont repete trop peu de valeurs distinctes sur les vingt cellules.
- Echec live actuel : timeout propre dans `RepairStructuredCellsAsync` apres quatre cycles, avec plusieurs prompts proches de 4095 tokens et deux reparations structurees successives par cycle.
- Le prochain correctif doit rester generique et LLM-first : demander au LLM un plan de diversite lorsque des doublons exacts ont ete supprimes, exiger une auto-evaluation de writeability des options avant `decision=answer`, puis differer vers la recuperation documentaire des le premier echec mecanique `repetitive_structured_cells`.
- Ne pas considerer le planning valide avant trois lives consecutifs complets, sources utiles incluses.

### 2026-07-17 - Strategie de recherche LLM et point d'arret propre

- Le live diagnostique court dans `artifacts/client-live-final-weekly-meal-plan-20260717-105934/` a confirme qu'une simple deduplication exacte ne suffisait pas : le Planner pouvait reformuler superficiellement la question complete et lancer plusieurs recherches presque equivalentes.
- Le garde-fou reste strictement mecanique et generique : pour une grande sortie structuree, il mesure les doublons exacts et combien de libelles d'axes demandes ont ete recopies dans une meme requete. Il ne tente aucune classification semantique, ne choisit aucun aliment et ne contient aucune heuristique metier Cuisine.
- Lorsque ce signal structurel est depasse, un second appel LLM `PlannerDiversityRepair` revoit la strategie et doit proposer deux a quatre angles documentaires reellement distincts. Le LLM conserve donc la decision semantique; le code borne, trace, deduplique a l'identique et execute son ordre.
- Les prompts Judge, Selection, Status et Action exigent desormais une verification de la `writeability` : des identifiants de preuve ou un grand nombre de resultats ne suffisent pas pour remplir des cellules; le LLM doit identifier des cartes/options/formulations concretes et diversifiees avant de choisir `answer`.
- La boucle de reparation structuree differe vers une nouvelle recuperation documentaire des le premier echec mecanique qui reproduit des cellules repetitives, au lieu de depenser une seconde tentative sur le meme lot de preuves.
- Validation ciblee du nouveau contrat : 3/3 tests verts, dont le scenario avec trois variantes superficielles de la question complete.
- Validation de non-regression apres recompilation : `dotnet test client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~SourceBacked|FullyQualifiedName~OpenAi"` -> 238/238 tests verts en 22 secondes.
- Aucun live complet n'a ete relance a ce jalon. La prochaine preuve discriminante est un live canonique avec apparition de `planner_query_strategy_repair`, inspection des angles proposes par le LLM, puis verification du planning final et de ses sources.
- Etat au point d'arret : aucun processus `dotnet`, MSBuild, compilateur ou serveur LLM laisse par cette validation; modifications non commitees, car le lot fonctionnel n'a pas encore franchi la preuve live requise.

### 2026-07-17 - Recuperation structuree LLM, live discriminant et timeout de recherche

- La recuperation apres echec mecanique du Writer est desormais confiee a un appel LLM specialise. Le code exige seulement une action executable de contenu et ne choisit ni requete, ni categorie, ni preuve a la place du modele.
- Un ancien fallback pouvait encore synthetiser `documents.navigation` apres une decision terminale du LLM. Il est maintenant bloque pour les echecs d'ecriture structuree et trace par `evidence_iteration.verification_follow_up_synthesis_blocked_by_structured_contract`.
- La non-regression SourceBacked/OpenAI a d'abord atteint 243/243 tests verts apres ce correctif.
- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260717-125159/`, trace `rag-20260717125225241-ad80bfb7` : echec apres 17 min 30 s. Le Planner a execute `petit-dejeuner souper` puis `diner collation`, obtenu zero puis un seul resultat faible, et le Writer a produit vingt cellules citation-only/repetitives. La recuperation LLM specialisee a choisi `clarify`; aucune reussite n'est revendiquee.
- Les prompts du retry Planner exigent maintenant une famille documentaire coherente et une seule facette demandee par requete. La recuperation LLM distingue explicitement insuffisance de preuves et ambiguite utilisateur, et recoit toujours un contexte catalogue compact.
- Live diagnostique `artifacts/client-live-final-weekly-meal-plan-20260717-133612/`, trace `rag-20260717133639993-d3fb312a` : le retry a effectivement produit quatre requetes a une seule facette au lieu de fusionner les slots. Le choix semantique restait cependant faible (`petit-dejeuner documents techniques`, puis les autres slots, `categoryPath=null`).
- La premiere recherche simple globale a pris 223,6 secondes et retourne zero resultat. Le live a ete interrompu proprement au debut de la deuxieme requete, car les trois appels restants ne pouvaient plus fournir une validation utile dans le plafond de 24 minutes.
- Cause mecanique fermee : le timeout de 35 secondes de `source_exploration`, auparavant limite au fan-out `rag.multi_search`, couvre maintenant aussi `rag.search`. Un timeout produit `rag_search_query_timeout`, conserve la distinction avec une absence documentaire et reste visible dans les traces/diagnostics.
- Le retry Planner recoit aussi les noms et alias compacts du catalogue. Il doit utiliser `categoryPath` pour le scope et reserver les mots de requete au contenu recherche, sans recopier un nom de depot/catalogue comme remplissage.
- Validation ciblee apres compilation : 4/4 tests verts (timeout simple, timeout multi, prompt Action Repair, prompt Planner Diversity Retry).
- Validation elargie : `dotnet test client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~SourceBacked|FullyQualifiedName~OpenAi|FullyQualifiedName~ApiClientDocumentsTransitionTests"` -> 305/305 tests verts en 40 secondes.
- Rapport Telegram envoye apres ce jalon.
- Prochaine preuve discriminante : un nouveau live canonique doit montrer (1) une categorie semantiquement plausible choisie par le LLM, (2) des requetes de contenu sans labels de depot, (3) aucun appel RAG simple superieur a 35 secondes en source-exploration, puis (4) des preuves concretement ecrivable avant le Writer.
- Le planning reste non valide tant que la reponse 5 x 4, les citations utiles et les cartes de sources n'ont pas passe trois lives consecutifs.

### 2026-07-17 - Contrats de retry mono-facette et de sortie de navigation repetitive

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260717-135556/`, trace `rag-20260717135635292-083482be` : execution terminee proprement en 15 min 19 s, mais reponse finale refusee et zero source exposee. Ce live est diagnostique, pas valide.
- Le timeout de 35 secondes est bien apparu dans les recherches `rag.search`; les deux recherches initiales se sont terminees rapidement. Le blocage des recherches longues est donc prouve dans le chemin live.
- Le retry Planner a encore accepte deux requetes qui combinaient chacune deux facettes (`petit-dejeuner et diners`, puis `souper et collation`). Le premier contrat en langage naturel etait insuffisant avec le modele live.
- Le LLM de recuperation a ensuite consomme deux tours avec des navigations seules (`Cuisine`, puis `Documents techniques`) avant de lancer une recherche de contenu globale. Celle-ci a retrouve douze candidats, dont plusieurs cartes et faits exploitables, mais trop tard et avec une selection insuffisamment diversifiee.
- Le Writer a produit un tableau pseudo-technique; le verificateur mecanique a detecte `missing_inline_citation` et `repetitive_structured_cells`. Les reparations ont alterne cellules manquantes, valeurs repetees et contenu domine par les identifiants de citation. Le terminal a correctement refuse de livrer une reponse non prouvee.
- Le retry Planner dispose maintenant d'un troisieme appel LLM borne lorsque le premier retry viole encore le contrat. Le code ne choisit aucun theme : il verifie seulement la forme observable `au plus une facette demandee par requete`, puis renvoie la strategie polluee au LLM. Si le second retry reste non conforme, aucune requete polluee n'est executee.
- Apres une premiere `documents.navigation`, une nouvelle decision composee uniquement de navigation est elle aussi renvoyee au LLM avec le contrat `NAVIGATION_ONLY_CONTRACT`. Le modele doit choisir une vraie recherche de contenu, une ancre documentaire exacte, repondre ou clarifier. Le code n'invente pas la requete de remplacement.
- La logique de navigation repetitive a ete extraite dans `SourceBackedRagPipeline.NavigationOnlyRecoveryContract.cs`; `SourceBackedRagPipeline.IterationRepair.cs` reste sous la limite d'architecture avec 491 lignes.
- Validation ciblee apres recompilation : 6/6 tests verts, couvrant budget du prompt, modularite, double retry Planner, refus d'executer une strategie polluee et recuperation apres navigation repetitive.
- Validation elargie : `dotnet test client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~SourceBacked|FullyQualifiedName~OpenAi|FullyQualifiedName~ApiClientDocumentsTransitionTests"` -> 306/306 tests verts en 21 secondes.
- Prochaine preuve discriminante : rejouer le live canonique et verifier dans la trace que le LLM produit des recherches mono-facette, qu'une seconde navigation seule n'est pas executee, que des preuves concretement ecrivables sont selectionnees avant le Writer et que les vingt cellules passent le contrat de sources.
- Le planning reste non valide : les 306 tests prouvent les contrats deterministes, pas la qualite semantique ni la convergence du modele live.

### 2026-07-17 - Live court refuse avant execution et decomposition LLM explicite

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260717-143129/`, trace `rag-20260717143156390-8674ce5c` : echec propre en 3 min 13 s, avant toute execution d'outil, avec zero source.
- Le premier repair a encore copie toute la grille. Le retry a produit `petit-dejeuner souper` et `diner collation`; le contract retry a recopie ces deux requetes exactement. Le signal mecanique `maxRequestedAxisLabelsCopiedByOneQuery=2` etait donc legitime.
- Le pipeline a correctement refuse d'executer ces requetes et a rendu une insuffisance explicite. Ce comportement evite une recherche polluee, mais il ne repond pas encore au besoin utilisateur.
- Cause du dernier retry identifiee : il reutilisait pratiquement le meme prompt que le retry precedent et omettait volontairement les requetes rejetees. A temperature faible, le modele reproduisait sa sortie sans voir concretement ce qu'il devait decomposer.
- Le dernier appel est maintenant un contrat LLM distinct `PlannerDiversityContractRetry`. Il recoit les facettes exactes extraites de l'intake et les requetes invalides, puis doit les decomposer en recherches independantes. Le LLM conserve le choix du vocabulaire documentaire, des categories et de l'ordre.
- Le code continue uniquement a verifier la forme observable : zero ou une facette demandee par requete, au moins deux recherches distinctes, categorie connue et budget de quatre requetes. Il ne genere ni aliment, ni requete, ni categorie de remplacement.
- Validation ciblee apres compilation : 3/3 tests verts. Validation elargie : 306/306 tests verts en 38 secondes.
- Prochaine preuve discriminante : nouveau live canonique avec trace `planner_query_strategy_contract_retry`, puis execution effective de recherches mono-facette choisies par le LLM.

### 2026-07-17 - Quatre recherches live executees, puis depassement de contexte ferme

- Deuxieme live court `artifacts/client-live-final-weekly-meal-plan-20260717-144523/`, trace `rag-20260717144551083-083e33e6` : malgre un prompt distinct, le contract retry a conserve les deux paires `petit-dejeuner souper` et `diner collation`; arret propre avant outil en 3 min 8 s.
- Le contrat final a ensuite ete explicite sous forme de quatre emplacements nommes, un par facette extraite de l'intake. Le code verifie maintenant la couverture complete : quatre facettes impliquent quatre recherches distinctes et chacune doit apparaitre dans exactement une requete.
- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260717-150124/`, trace `rag-20260717150158204-ecec10b8` : le contract retry a enfin ete accepte avec `semanticFacets=4`, `coveredSemanticFacets=4` et quatre requetes mono-facette executees.
- Les recherches ont respecte le timeout : `petit-dejeuner` a retourne 6 hits, `diner` 6, `souper` 0 et `collation` 12. Le Planner a toutefois disperse les scopes entre `Cuisine`, `Documents avec versions multiples`, `Documents contradictoires` et le corpus global.
- Cette dispersion a introduit des candidats hors sujet (manuel PostgreSQL, norme de recipients sous pression, rapports IPCC, cours MIT, etc.). Le Status Review n'a retenu que trois preuves utiles et une faible, puis le Writer a produit un tableau domine par `[E1]`, `[E2]` et `[E4]` avec cellules manquantes ou trop minces.
- Le live a ensuite echoue proprement sur HTTP 400 : le prompt `structured_cell_repair` comptait 4 121 tokens pour une fenetre locale de 4 096. Aucune reponse non verifiee n'a ete livree; duree totale 13 min 57 s, zero carte source finale.
- Le contract retry demande desormais au LLM de choisir une seule categorie partagee pour la famille de contenu. Le code verifie seulement que les quatre requetes reutilisent la meme valeur; il ne choisit pas cette categorie a la place du modele.
- Le prompt de reparation structuree a ete compacte sans retirer la forme 5 x 4, les vingt cles obligatoires ni les huit meilleures preuves. Son budget de regression maximal passe de 13 000 a 9 500 caracteres pour garder une marge sous 4 096 tokens.
- Validation ciblee combinee Planner/reparateur : 10/10 tests verts. Validation etendue apres ajustement d'une assertion textuelle : 307/307 tests verts en 20 secondes.
- Prochaine preuve discriminante : nouveau live avec quatre requetes mono-facette dans un scope partage choisi par le LLM, puis `structured_cell_repair` sous budget et verification finale des vingt cellules.
- Le planning reste non valide : ce live prouve une amelioration du Planner, pas encore une reponse professionnelle source-backed.

### 2026-07-17 - Contournement par synonymes ferme et ultime relance LLM de recuperation

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260717-152925/`, trace `rag-20260717152953745-7f66f04a` : echec propre apres 9 min 58 s, zero source exposee et refus terminal honnete.
- Le Planner initial a contourne la reparation de diversite avec une seule requete large `plan de repas semaine du lundi au vendredi, matin, dîner, souper, collation`, dans la bonne categorie `Cuisine`. Les synonymes `matin` et formulations voisines evitaient le comptage exact des facettes `Petit-dejeuner`, `Diner`, `Souper`, `Collation`.
- Cette unique recherche a tout de meme trouve dix pages Cuisine, dont plusieurs cartes et faits concrets. Le Status Review a retenu cinq preuves, mais le Writer a produit une grille repetitive et dominee par les sources. Le verificateur a detecte `missing_inline_citation` et `repetitive_structured_cells`.
- Le prompt de reparation structuree compacte a bien tenu dans le contexte local et s'est execute; le depassement HTTP 400 observe au live precedent est donc ferme sur ce chemin.
- La recuperation generique puis la recuperation structuree specialisee n'ont produit aucune action de contenu executable; la seconde a retourne `clarify`. Le pipeline a correctement refuse de synthetiser une requete dans le code et a termine sans reponse non prouvee.
- Le declencheur Planner couvre maintenant le contournement lexical : pour une grande grille d'au moins huit cellules comprenant trois ou quatre facettes semantiques, un nombre de recherches RAG distinctes inferieur au nombre de facettes exige une nouvelle strategie LLM. Le code ne reconnait aucun synonyme, ne choisit aucune requete et ne contient aucune regle Cuisine; il compare uniquement des cardinalites de contrat deja extraites par l'intake LLM.
- Une regression generique prouve qu'une requete unique en synonymes (`matin midi soir pause`) est renvoyee au LLM et remplacee par quatre recherches distinctes avant execution.
- Lorsque l'ecriture structuree a deja echoue, que l'intention utilisateur est connue et qu'un tour de recherche reste disponible, `clarify` ne compte plus comme un contrat d'action satisfait. Un ultime appel LLM compact `EvidenceJudgeStructuredRecoveryActionContractRetry` doit choisir exactement une nouvelle recherche de contenu ou une ancre documentaire exacte non lue.
- Le retry recoit la question, les facettes semantiques, les chemins de catalogue, les codes d'echec d'ecriture, les requetes deja tentees et un vocabulaire de preuve compact. Le code verifie seulement unicite, nouveaute, executabilite, budget et ancre; il ne fabrique jamais le contenu de l'action.
- Si cet ultime appel retourne encore `clarify`, une requete repetee ou une action non executable, le pipeline s'arrete honnetement. Une regression negative confirme qu'aucune navigation ou recherche generique n'est synthetisee.
- Les nouveaux prompts et controles ont ete extraits dans des fichiers dedies; tous les fichiers `SourceBackedRag` restent sous 500 lignes (`SourceBackedRagPipeline.IterationRepair.cs` : 482 lignes).
- Validation ciblee apres compilation : 6/6 tests verts.
- Validation complete de la famille SourceBacked : 233/233 tests verts en 17 secondes.
- Validation elargie : `dotnet test client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBacked|FullyQualifiedName~OpenAi|FullyQualifiedName~ApiClient"` -> 309/309 tests verts en 22 secondes.
- Aucun live post-correctif n'est encore revendique. Prochaine preuve discriminante : verifier que la requete large initiale declenche bien `PlannerDiversityRepair`, que le LLM conserve un scope partage pertinent et que, si le premier Writer echoue, l'ultime retry LLM produit une action de contenu nouvelle avant le terminal.
- Le planning reste non valide tant que les vingt cellules, les citations utiles, les cartes de sources et la reproductibilite sur trois lives consecutifs ne sont pas prouves.

### 2026-07-17 - Recherche live saine, contradiction du statut LLM et Writer structure primaire

- Live canonique post-correctif `artifacts/client-live-final-weekly-meal-plan-20260717-163535/`, trace `rag-20260717163603470-491c47ff` : timeout propre apres 24 minutes dans un second `structured_cell_repair`. Aucune reponse incomplete n'a ete livree et le runtime local a ete arrete.
- Le Planner est maintenant prouve sur le vrai modele : apres deux strategies rejetees, `PlannerDiversityContractRetry` a produit exactement `petit-dejeuner`, `diner`, `souper`, `collation`, toutes dans le scope partage `Cuisine` choisi par le LLM.
- Les quatre recherches ont rendu respectivement 6, 12, 12 et 8 hits en 8,5 s, 2,8 s, 2,6 s et 3,0 s. Aucun timeout RAG, aucune dispersion de categorie et aucun candidat hors corpus Cuisine n'ont ete observes.
- Le premier `EvidenceStatusReview` repare a identifie huit preuves utiles, une faible et zero exigence manquante, mais a recommande `need_more_evidence`. Cette contradiction interne a provoque une selection-repair puis une navigation `Cuisine`, alors qu'une option bank etait deja disponible.
- Deux tours supplementaires ont ensuite relu `chefbot_livre_de_recettes_fr.pdf` p.131 puis un manuel Moulinex p.132. Le statut final n'a selectionne que trois preuves (`E1`, `E2`, `E4`) et le Writer a produit vingt cellules de simples marqueurs.
- Le premier reparateur structure, execute avec un prompt de 7 497 caracteres, a produit une vraie table de 1 331 caracteres. Elle restait toutefois repetitive et n'utilisait que deux preuves; le second reparateur a ete annule par le plafond global apres 35 secondes.
- Un nouveau contrat LLM `EvidenceStatusReviewDecisionContractRepair` traite maintenant la coherence du statut : si le LLM annonce des preuves utiles, zero manque et demande encore des preuves, il doit reevaluer lui-meme son action. Il peut choisir `answer`/`write_partial`, ou conserver la recherche seulement en nommant des manques et une cible concrete. Le code ne choisit ni les preuves utiles ni le sens de la cible.
- Le `EvidenceStatusReviewFormatRepair` applique aussi ce contrat de coherence pendant la remise en forme, ce qui evite un appel supplementaire lorsque le modele corrige directement sa propre contradiction.
- Pour les tables d'au moins seize cellules, le premier Writer utilise maintenant le contrat LLM type `WriterStructuredTable`. Le modele produit directement `structuredTableRows` avec chaque ligne, colonne, texte concret et evidenceId; le code se limite a parser, rendre le Markdown et verifier les vingt cellules.
- Cette voie primaire reutilise le meme `EvidenceBundle`, la selection semantique du Judge et les cartes d'options. Elle ne genere aucune valeur metier dans le code et ne contient aucun exemple Cuisine.
- Regression deterministe ajoutee : un statut `need_more_evidence` avec huit preuves utiles et zero manque est renvoye au LLM; sa correction `write_partial` conduit directement a une table typee 5 x 4 validee sans `StructuredCellRepair`.
- Modularite preservee par extraction de `SourceBackedRagPipeline.StructuredWriterStep.cs` et `SourceBackedStructuredWriterPrompt.cs`; les limites d'architecture restent vertes.
- Validation ciblee : 4/4 tests verts. Validation SourceBacked : 235/235. Validation elargie SourceBacked/OpenAI/API : 311/311 tests verts en 22 secondes.
- Prochaine preuve discriminante : live canonique avec selection des huit preuves utiles au premier tour et apparition de `structured_writer`; verifier ensuite table 5 x 4, citations, cartes de sources et absence de boucle longue de reparations.
- Le planning demeure non valide : la recherche est maintenant saine, mais la convergence du statut et du Writer primaire doit etre prouvee en live puis repetee trois fois.

### 2026-07-17 - Contrat Planner type avec categorie partagee choisie une seule fois

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260717-174651/`, trace `rag-20260717174719915-d7db7c16` : echec propre en 3 min 33 s avant toute execution d'outil. Aucune requete polluee et aucune source trompeuse n'ont ete exposees.
- La trace precise ferme l'ambiguite du diagnostic : le dernier Planner couvrait les quatre facettes, mais renvoyait six requetes et deux scopes. Il conservait les deux paires rejetees (`petit-dejeuner souper`, `diner collation`) dans `Documents avec versions multiples`, puis ajoutait quatre recherches mono-facette dans `Cuisine`.
- La cause etait contractuelle, pas documentaire : le schema generique `RetrievalPlan.requests` repetait `categoryPath` dans chaque objet et le prompt remontrait les requetes invalides. Le petit modele pouvait donc recopier les anciennes paires tout en ajoutant la decomposition demandee.
- Le dernier appel LLM utilise maintenant un contrat specialise : `sharedCategoryPath` est choisi semantiquement une seule fois par le modele, et `facetRequests` contient exactement un objet type par facette. Les objets de facette ne peuvent pas exprimer un second scope.
- Les requetes rejetees ne sont plus reinjectees textuellement dans le dernier prompt. Le LLM recoit les facettes exactes, le catalogue, les alias et le rapport mecanique, puis choisit toujours lui-meme le corpus partage, le vocabulaire des requetes, les types d'outil et les objectifs.
- Le code effectue uniquement les controles de contrat : nombre et ordre des emplacements, correspondance lexicale de `targetFacetExact`, presence de la facette declaree dans sa requete, outil autorise, chemin catalogue connu, unicite et budget. Il recopie ensuite mecaniquement la valeur `sharedCategoryPath` choisie par le LLM dans les quatre `RetrievalRequest`.
- Une regression reproduit exactement la sortie live contaminee a six requetes et deux scopes : elle est refusee sans execution. La voie positive prouve qu'un contrat type a quatre facettes produit quatre recherches distinctes, toutes dans le scope unique choisi par le LLM.
- Validation ciblee apres compilation : 4/4 tests verts. Validation SourceBacked : 236/236. Validation elargie SourceBacked/OpenAI/API : 312/312 tests verts en 22 secondes.
- Prochaine preuve discriminante : nouveau live canonique. Le Planner doit produire `sharedCategoryPath` et quatre `facetRequests`, les recherches doivent s'executer dans un scope coherent, puis `EvidenceStatusReviewDecisionContractRepair` et `WriterStructuredTable` doivent converger vers une table 5 x 4 verifiee avec sources utiles.
- Le planning reste non valide : le contrat deterministe du Planner est maintenant plus robuste, mais la reponse professionnelle finale doit encore etre prouvee en live puis repetee.

### 2026-07-17 - Choix de corpus LLM focalise et fuite d'instructions fermee

- Live diagnostique `artifacts/client-live-final-weekly-meal-plan-20260717-181244/`, trace `rag-20260717181313855-15bd3a8d` : arret volontaire et propre apres environ 6 minutes, une fois la nouvelle cause prouvee. Le processus de test et le `llama-server` gere ont ete fermes; aucune reponse finale n'est revendiquee.
- Le nouveau schema type a bien impose quatre facettes, quatre requetes et un seul scope. Il a donc ferme la contamination precedente a six requetes et deux categories.
- Le LLM a toutefois choisi `Documents techniques` comme scope partage et a recopie une valeur indicative du schema dans chaque requete : `repas petit-dejeuner 2-8 mots`, puis les variantes diner, souper et collation. Les quatre recherches ont rendu zero hit en 1,0 a 1,3 seconde.
- Le Judge puis son reparateur ont continue dans le meme mauvais scope avec `documents.navigation: Documents techniques`. Cette confirmation a rendu inutile la poursuite jusqu'au plafond de 24 minutes.
- La decision de corpus est maintenant isolee dans un appel LLM focalise `PlannerSharedCategorySelection`. Il recoit la question utilisateur complete, les contraintes, les facettes, tous les chemins et alias du catalogue, ainsi que la memoire non probante; il doit choisir un seul `sharedCategoryPath` ou `null` avec une raison semantique.
- Le code valide uniquement que le chemin choisi existe dans le catalogue. Il ne remplace pas le choix, ne privilegie aucune categorie et ne contient aucune regle Cuisine.
- Le LLM de decomposition recoit ensuite ce scope deja choisi et doit seulement le recopier exactement, puis produire les requetes mono-facette. Il ne recoit plus de faux exemple JSON contenant un texte de requete a completer.
- Un controle mecanique generique refuse aussi les fuites d'instructions observables (`2-8 keywords`, `2-8 mots`, descriptions de proprietes ou placeholders) avant toute execution d'outil. Il ne juge pas la pertinence semantique de la requete.
- Regression exacte ajoutee pour les quatre requetes live `repas ... 2-8 mots` : elles sont refusees sans execution. Les regressions du scope partage, de la sortie a six requetes et du contrat incomplet restent vertes.
- Validation ciblee apres compilation : 6/6 tests verts en cumulant les cinq contrats Planner et la fuite d'instructions exacte. Validation SourceBacked : 237/237. Validation elargie SourceBacked/OpenAI/API : 313/313 tests verts en 21 secondes.
- Prochaine preuve discriminante : nouveau live canonique. `PlannerSharedCategorySelection` doit choisir un scope semantiquement lie a la question; `PlannerDiversityContractRetry` doit produire quatre requetes de contenu sans texte d'instruction; les recherches doivent fournir des preuves avant le Status Review et le Writer structure.
- Le planning reste non valide tant que la table 5 x 4, les citations utiles, les cartes de sources et la reproductibilite live ne sont pas prouvees.

### 2026-07-17 - Revue semantique LLM du corpus et separation structure/contenu

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260717-183548/`, trace `rag-20260717183615038-0c46198f` : echec propre en 4 min 17 s, avant toute execution d'outil, avec arret automatique du runtime local et zero source exposee.
- Le premier appel focalise a choisi `Catalogue commercial` en affirmant sans preuve que cette categorie contenait des recettes et plans de repas. L'inspection directe de `/catalog/snapshot` montre 31 categories, des comptes comparables et aucun alias; `Cuisine` est pourtant le libelle qui nomme litteralement le domaine demande.
- Le dernier LLM a ensuite recopie la question complete dans chaque requete (`semaine du lundi au vendredi`) et le nom du depot `Catalogue commercial`. Le garde mecanique a compte trois labels de grille par requete et a refuse le plan avant outil.
- Une nouvelle etape LLM `PlannerSharedCategoryReview` revoit independamment la premiere proposition. Elle recoit la question, les facettes, la proposition, sa raison et tous les libelles/alias. Elle doit ignorer les affirmations sur des contenus non vus, comparer le sens litteral et conserver ou remplacer le scope.
- Le code n'effectue aucun score lexical, aucune correspondance repas/categorie et aucune preference pour `Cuisine`. Il valide seulement que la categorie finale renvoyee par le second LLM est un chemin catalogue exact.
- Le contexte de categorie a ete simplifie pour la decision : chaque candidat expose uniquement son chemin et ses alias. Les comptes de documents identiques et les metadonnees administratives non discriminantes ne polluent plus la comparaison semantique.
- Le prompt final de decomposition ne recoit plus la question utilisateur complete. Il recoit les facettes exactes, le scope choisi par le LLM et la liste des labels structurels interdits; cela separe le contenu a rechercher de la forme finale du tableau.
- La regression positive reproduit le live : premier choix LLM `Catalogue commercial` avec justification inventee, revue LLM `replace` vers `Cuisine`, puis quatre requetes mono-facette dans le scope revu. Les gardes negatifs precedents restent verts.
- Validation ciblee : 6/6 tests verts. Validation SourceBacked : 237/237. Validation elargie SourceBacked/OpenAI/API : 313/313 tests verts en 22 secondes.
- Prochaine preuve discriminante : live canonique avec observation separee de `planner_shared_category_selection`, `planner_shared_category_review` et `planner_query_strategy_contract_retry`; la revue doit corriger ou confirmer un scope litteralement pertinent, puis les quatre recherches doivent atteindre le corpus sans recopier les jours ni le nom du depot.
- Le planning reste non valide; aucune table 5 x 4 professionnelle source-backed n'a encore passe le chemin live complet.

### 2026-07-17 - Arbitrage LLM des scopes divergents et synonymes de requete autorises

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260717-185557/`, trace `rag-20260717185628002-fef24946` : echec propre en 3 min 2 s avant les outils, runtime local arrete automatiquement, zero source exposee.
- La premiere selection LLM a choisi correctement `Cuisine` avec une raison liee aux repas. La revue LLM l'a pourtant remplacee par `Catalogue commercial` avec une affirmation contradictoire sur le contenu suppose des categories.
- Une revue n'est donc plus consideree comme meilleure par sa seule position. Si selection et revue divergent, `PlannerSharedCategoryAdjudication` recoit les deux chemins, leurs raisons, la question, les facettes et tous les libelles/alias, puis un troisieme LLM tranche semantiquement. Le code detecte seulement la divergence exacte et valide le chemin final.
- Le dernier contrat a ensuite ete refuse parce que la requete du premier slot ne contenait pas litteralement `Petit-dejeuner`. Le serveur local ne journalise pas le texte des completions; la trace du contrat inclut desormais la requete rejetee pour les prochains diagnostics.
- `targetFacetExact` constitue deja la declaration semantique du LLM. Le texte de recherche peut maintenant utiliser un synonyme pour sa propre facette. Le code exige l'ordre et l'exhaustivite des quatre `targetFacetExact`, mais ne force plus le vocabulaire metier de la requete.
- Les protections mecaniques restent strictes : une requete est refusee si elle copie une autre facette, un jour/label structurel, une instruction du prompt, un outil invalide, une requete vide ou un objectif vide. Le controle de diversite, l'unicite, le scope partage et le budget restent actifs.
- La regression positive utilise maintenant `targetFacetExact=Petit-dejeuner` avec la requete synonyme `options matinales cereales fruits`; elle est acceptee sans que le code connaisse le sens de `matinales`. La selection, la revue et l'arbitrage LLM produisent le scope final.
- Compilation complete monoprocessus : succes, zero avertissement et zero erreur. Validation ciblee : 6/6. Validation SourceBacked : 237/237. Validation elargie SourceBacked/OpenAI/API : 313/313 tests verts en 23 secondes.
- Prochaine preuve discriminante : nouveau live. Si les deux premiers LLM divergent, l'arbitre doit choisir un scope; le contrat doit accepter des requetes mono-facette semantiques sans structure de tableau; les outils doivent enfin s'executer.
- Le planning final reste non valide tant que les recherches, le Status Review, le Writer type, les citations et les cartes de sources n'ont pas converge en live.

### 2026-07-17 - Point d'arret propre : le vote LLM sans preuve ne suffit pas

- Live interrompu proprement a la demande de l'utilisateur : `artifacts/client-live-final-weekly-meal-plan-20260717-191451/`, trace `rag-20260717191519086-0aba9895`. Le lanceur, son processus enfant `vstest` et le `llama-server` gere ont tous ete arretes; aucune validation finale n'est revendiquee.
- La selection LLM initiale a choisi correctement `Cuisine`. La revue a choisi `Catalogue commercial`, puis l'arbitre a confirme ce second choix avec la meme justification semantiquement erronee. Ajouter des votes ou des appels de consensus sur les seuls libelles ne constitue donc pas une correction robuste avec le modele local actuel.
- Le contrat type final a ete accepte et a produit quatre recherches mono-facette dans le scope choisi par l'arbitre. La premiere recherche, `Quels petits-dejeuners proposer dans le catalogue commercial ?`, a retourne 10 hits en environ 39,2 secondes; la seconde avait commence lorsque le live a ete arrete.
- Ces hits ne prouvent pas que le corpus est pertinent : le test a ete coupe avant le jugement de preuves et l'ecriture. La table 5 x 4, ses citations et ses cartes de sources restent non validees.
- Prochaine piste prioritaire a la reprise : remplacer le consensus LLM fonde uniquement sur les noms de categories par une boucle orchestree par le LLM et fondee sur des preuves d'outil. Le LLM formule une hypothese de scope et ses requetes, observe les resultats reels, puis conserve ou revise lui-meme le scope. Le code reste limite a l'execution, aux contrats, aux budgets et aux controles mecaniques.
- Evaluer aussi explicitement si le modele local Qwen 2.5 3B est assez fiable pour tenir le role d'orchestrateur semantique. Un modele plus capable peut etre necessaire; cette evaluation doit etre comparee sur les memes traces et ne doit pas etre masquee par une categorie `Cuisine` codee en dur ou un score lexical metier.
- Etat de reprise : code compile, 237/237 tests SourceBacked et 313/313 tests elargis etaient verts avant ce live. Le depot reste volontairement non nettoye et non commite a ce palier; aucune operation Git destructive n'a ete effectuee.

### 2026-07-17 - Le scope Planner devient une hypothese verifiee par les preuves d'outil

- Le live precedent a montre que deux avis LLM supplementaires, fondes uniquement sur les libelles du catalogue, pouvaient remplacer un bon choix initial par le meme mauvais choix. Les etapes `PlannerSharedCategoryReview` et `PlannerSharedCategoryAdjudication` ont donc ete retirees au lieu d'ajouter un nouveau vote.
- `PlannerSharedCategorySelection` reste une decision semantique du LLM, mais son resultat est maintenant explicitement une hypothese de premiere recherche. Le code valide seulement le chemin exact et execute les requetes; il ne confirme pas la pertinence du corpus.
- Chaque preuve montree a l'`EvidenceJudge` expose desormais son `categoryPath`. Lorsque toutes les preuves ont le meme scope, celui-ci est compacte dans un unique `EVIDENCE_CATEGORY_PATH`; lorsqu'elles viennent de plusieurs scopes, l'association reste visible item par item.
- La memoire de travail conserve aussi le `categoryPath` des requetes RAG deja executees. Le Judge peut ainsi relier requete, corpus, extraits et chemins alternatifs du catalogue avant de choisir `answer` ou un pivot.
- Le contrat du Judge precise qu'un nombre de hits ou un score de retrieval ne prouve pas l'adequation semantique. Il doit conserver le scope seulement si le contenu reel soutient la demande; sinon il ignore les preuves inadequates et choisit lui-meme une nouvelle recherche dans un autre chemin exact ou dans le corpus global.
- Une regression generique reproduit ce comportement sans Cuisine : le Planner choisit `Catalogue commercial`, les preuves sont des fiches commerciales, puis l'`EvidenceJudge` demande une recherche `Operations`, selectionne uniquement les nouvelles preuves et produit une reponse source-backed verifiee.
- Les prompts volumineux restent sous leurs budgets. La repetition des categories identiques est compacte, et le prompt de reparation d'action ne duplique plus les requetes deja presentes dans `PREVIOUS_RETRIEVAL_REQUESTS`.
- Compilation monoprocessus : succes, zero avertissement, zero erreur. Validation ciblee : 7/7 puis 3/3. Validation SourceBacked : 238/238. Validation elargie SourceBacked/OpenAI/API : 314/314 tests verts.
- Prochaine preuve discriminante : live canonique du planning 5 x 4. Verifier le choix initial, les resultats reels avec leur scope, un pivot LLM si necessaire, puis la convergence du Status Review, du Writer type, du verificateur et des cartes de sources.
- Le planning reste non valide tant que cette chaine complete n'a pas produit une table professionnelle, puis reussi les repetitions live et le passage WinUI.

### 2026-07-17 - Le live atteint une table valide puis echoue sur un rejet d'adequation hors scope

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260717-194506/`, trace `rag-20260717194534423-e85f6c7b` : echec terminal apres 18 min 42, zero carte source livree. Le runtime local a ete arrete proprement.
- Le verrou du corpus est ferme sur ce run : `PlannerSharedCategorySelection` a choisi `Cuisine`, le contrat type a produit quatre recherches mono-facette dans ce seul scope, et aucun vote non fonde n'a remplace ce choix.
- La recherche `petit-dejeuner` a atteint son timeout RAG de 35 secondes avec zero hit. Les trois autres recherches ont rendu 10, 9 et 10 hits en environ 21,1, 19,4 et 22,8 secondes. L'`EvidenceBundle` canonique a conserve 20 preuves issues de quatre resultats d'outil.
- L'`EvidenceJudge` a repondu `answer` sans selection. L'`EvidenceStatusReview`, apres une reparation de format, a retenu dix preuves utiles, zero faible et zero manque. La reparation de selection a transmis ces dix preuves au Writer.
- Le `structured_writer` primaire a depasse son timeout exact de 180 secondes et a rendu un contrat vide. Le `structured_cell_repair` a ensuite genere en 174,9 secondes une table 5 x 4 de 769 caracteres, avec vingt cellules, huit ids visibles distincts et zero erreur mecanique.
- L'`AnswerAdequacyJudge` a pourtant retourne `need_more_evidence` sans aucune action. Ses raisons exigeaient la recette exacte de chaque plat, affirmaient que les citations manquaient et reprochaient l'absence de suivi demande. Ces trois criteres depassent ou contredisent la demande et les faits du contrat mecanique.
- Le prompt d'adequation expose maintenant explicitement `sourceContractPassed=true`, `requestedShapePassed=true`, le nombre de cellules demandees, les ids visibles et `missingCitationErrors=0`. Il interdit d'exiger des sous-etapes, composants, quantites, explications ou un artefact plus detaille que celui demande.
- `need_more_evidence` exige desormais une a trois actions executables. Si le premier Judge le choisit sans action, un appel LLM focalise `AnswerAdequacyActionContractRepair` reevalue la decision : accepter, reviser integralement depuis les preuves, ou fournir de vraies recherches. Le code detecte seulement le contrat non actionnable et ne transforme jamais le rejet en acceptation.
- Une regression generique non-Cuisine reproduit exactement un rejet qui reclame un detail non demande et nie des citations visibles. Le second LLM accepte la table a partir des faits mecaniques et des preuves; les traces exposent tentative et acceptation de la reparation.
- Le timeout du seul evenement `structured_writer` passe de 180 a 210 secondes. La mesure live justifie cette marge : la generation structuree equivalente a abouti en 174,9 secondes, alors que le Writer primaire a ete annule au plafond exact. Le but est d'eviter un second appel complet, sans modifier les autres timeouts.
- Compilation monoprocessus : succes, zero avertissement, zero erreur. Validation ciblee : 4/4. Validation SourceBacked : 239/239. Validation elargie SourceBacked/OpenAI/API : 315/315 tests verts.
- Prochaine preuve discriminante : nouveau live canonique. Le Writer primaire doit soit finir sous 210 secondes, soit etre repare; l'adequation doit respecter le niveau de detail demande et, si elle refuse, produire une action executable ou corriger sa decision par le LLM.
- Le planning final reste non valide : la table intermediaire etait mecanquement valide, mais elle n'a pas ete livree ni inspectee avec ses cartes de sources.

### 2026-07-17 - Point d'arret propre apres le second live : reparation Writer differee trop tot

- Second live canonique apres la correction d'adequation : `artifacts/client-live-final-weekly-meal-plan-20260717-202041/`, trace `rag-20260717202109020-3b68552c`. Execution terminee proprement en 18 min 09, mais reponse finale refusee et zero carte source exposee.
- Le chemin de recherche a de nouveau choisi `Cuisine`, produit quatre requetes mono-facette et constitue un `EvidenceBundle` de 36 items. L'`EvidenceJudge` a choisi `answer`; apres reparation du contrat de selection, dix preuves ont ete transmises au Writer.
- Le `structured_writer` a cette fois abouti. Son brouillon de 1 194 caracteres comportait quatre ids de preuve et une table presque complete, mais le verificateur mecanique a detecte trois cellules structurees manquantes et des cellules repetitives.
- Le pipeline n'a pas tente la reparation structuree ciblee qui avait produit une table valide au live precedent. Il a emis immediatement `source_contract.local_repair_deferred_to_evidence_recovery`, puis demande au LLM une nouvelle action documentaire.
- Cette recuperation a retourne une decision terminale sans action de contenu executable. Le contrat structure a correctement bloque toute requete inventee par le code via `evidence_iteration.verification_follow_up_synthesis_blocked_by_structured_contract`; le pipeline a donc termine honnetement sans livrer le brouillon invalide.
- Cause confirmee dans `SourceBackedRagPipeline.RepairDeferral.cs` et `SourceBackedRagPipeline.WriteVerifyRepair.cs` : `ShouldDeferLocalRepairToEvidenceRecovery` assimile actuellement tout `repetitive_structured_cells` a un besoin de nouvelles preuves et le deferage est evalue avant `RunFocusedStructuredRepairsAsync`.
- Cette equivalence est trop forte : les codes du verificateur prouvent un defaut mecanique du brouillon, pas une insuffisance semantique du corpus. Elle contredit ici la decision LLM `answer` et empeche le Writer de corriger trois cases avec les preuves deja retenues.
- Correction prioritaire a la reprise : tenter d'abord une reparation structuree LLM ciblee lorsque le Judge a choisi `answer`; seulement si cette tentative reste invalide, soumettre au LLM les erreurs et l'historique pour qu'il decide semantiquement entre nouvelle recherche et nouvelle redaction. Le code doit continuer a verifier forme, citations, nouveaute et budget, sans deduire lui-meme que davantage de preuves sont necessaires.
- Les regressions historiques de deferage doivent etre adaptees avec prudence : conserver la recuperation documentaire lorsqu'une premiere reparation ciblee prouve effectivement que le lot est insuffisant, mais supprimer le deferage avant toute tentative. Ajouter une regression generique reproduisant exactement trois cellules manquantes plus repetition, avec reparation ciblee valide et aucune seconde execution d'outil.
- Etat valide avant ce live : compilation sans avertissement ni erreur, 239/239 tests SourceBacked et 315/315 tests elargis verts. Aucun changement de code n'a ete applique apres le diagnostic de ce second live et aucune nouvelle validation n'est revendiquee.
- Point d'arret demande par l'utilisateur : ne pas relancer de live long. A la reprise, modifier l'ordre Writer/reparation/deferage, executer d'abord les tests cibles puis les suites 239 et 315, et seulement ensuite rejouer le live canonique.
- Le planning reste non valide et les objectifs WinUI, trois lives consecutifs, nettoyage du depot et commits coherents restent ouverts.

### 2026-07-17 - La reparation Writer precede maintenant toute recuperation documentaire

- Le raccourci initial de `SourceBackedRagPipeline.WriteVerifyRepair.cs` a ete retire : un brouillon structure invalide n'est plus immediatement transforme en tentative de recuperation documentaire sur la seule base des codes `missing_structured_cell` ou `repetitive_structured_cells`.
- Le premier traitement est maintenant `RunFocusedStructuredRepairsAsync`, donc un LLM Writer specialise recoit le brouillon, les erreurs mecaniques et les preuves selectionnees, puis tente de corriger la forme et le contenu des cellules.
- Le deferage existant apres cette premiere reparation est conserve. Si le brouillon repare reste repetitif ou largement incomplet, le pipeline memorise l'echec et demande alors au LLM de choisir une action documentaire executable. Le code ne conclut toujours pas lui-meme quelle preuve ou quelle requete semantique est necessaire.
- Le helper de construction d'un resultat non repare, devenu inaccessible avec ce nouvel ordre, a ete supprime de `SourceBackedRagPipeline.WriteVerifyOutcomeFactory.cs`.
- Une regression generique 5 x 4 reproduit le verrou live : vingt cellules demandees, trois cellules manquantes et de nombreuses repetitions dans le Writer initial. La reparation structuree LLM fournit les vingt cellules valides; le resultat est source-verifie, `WasRepaired=true`, une seule execution d'outil a lieu et aucune trace de recuperation documentaire n'apparait.
- Les regressions de recuperation ont ete rendues explicites : une premiere reparation qui reste repetitive ou conserve un grand nombre de cellules manquantes declenche toujours `source_contract.local_repair_deferred_to_evidence_recovery`; la protection contre une selection de preuves identique apres echec reste active, y compris avec un workspace abondant.
- Validation ciblee des quatre chemins critiques : 4/4 tests verts. Une regression supplementaire de selection identique a aussi ete revalidee seule : 1/1 verte.
- Validation finale du lot apres nettoyage : compilation monoprocessus reussie avec zero avertissement et zero erreur; 239/239 tests SourceBacked verts; 315/315 tests SourceBacked/OpenAI/API verts.
- Prochaine preuve discriminante : rejouer le live canonique. Si le Writer reproduit un tableau presque complet comme dans `rag-20260717202109020-3b68552c`, la trace doit montrer `StructuredCellRepair` avant tout deferage. Une reparation mecaniquement valide doit ensuite atteindre l'adequation, la reponse finale et les cartes de sources sans nouvelle recherche.
- Ce jalon ferme le verrou deterministe observe, mais pas encore la validation produit : le planning professionnel 5 x 4 reste a prouver en live, puis a reproduire et a verifier dans WinUI.

### 2026-07-17 - Point d'arret propre : premier live mecaniquement vert, audit semantique rouge

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260717-210149/`, trace `rag-20260717210217884-35e1dfec` : test reel termine avec succes en 20 min 48 s, runtime local arrete proprement, reponse de 20 cellules livree avec huit cartes de sources et zero erreur mecanique.
- Le nouvel ordre Writer est prouve en situation reelle : apres le timeout du `structured_writer` a 210 secondes, `structured_cell_repair` a ete execute avant toute recuperation documentaire et a produit une table 5 x 4 complete en 176,5 secondes.
- L'`AnswerAdequacyJudge` initial a ensuite expire a son timeout de 120 secondes. `AnswerAdequacyActionContractRepair` a accepte la reponse en 17,7 secondes, sans raison detaillee et sans audit explicite de chaque cellule contre son extrait cite.
- La reponse ne peut pas etre consideree professionnelle malgre son contrat mecanique valide : elle propose notamment une bechamel au petit-dejeuner, repete plusieurs repas et reutilise les memes preuves pour des plats differents.
- L'audit direct des huit chunks via `/documents/context` confirme des attributions non soutenues. `E1` decrit une sauce bechamel mais pas un petit-dejeuner ni une omelette; `E2` est une introduction generique et ne donne ni risotto aux champignons ni saumon citron-creme; `E3` traite d'hygiene et d'ateliers de cuisine, pas de salade de fruits ou de quinoa; `E4` introduit la cuisine neerlandaise et un gratin d'endives, pas des desserts aux poires ou bananes.
- Les quatre autres preuves sont egalement mal alignees : `E5` parle de preparation anticipee et de cuisson, pas de smoothie; `E6` fournit des ressources de batch cooking, pas un poulet grille; `E7` est une couverture mentionnant d'autres recettes, pas une tarte aux poires; `E8` contient une recette de couscous, pas une collation de noix grillees.
- Cause prioritaire : apres le timeout du controle semantique complet, le contrat de reparation d'action peut accepter sur la seule validite mecanique. `SatisfiesAnswerAdequacyActionContract` accepte actuellement toute decision autre que `need_more_evidence`, meme lorsque `reasons` est vide; la reparation courte ne recoit pas une matrice explicite cellule -> affirmation -> ids cites.
- Correction prioritaire a la reprise, toujours LLM-first et generique : extraire mecaniquement les vingt cellules et leurs ids visibles dans une matrice `CELL_CLAIM_TO_CITATION_MAP`, puis demander au LLM d'auditer semantiquement chaque affirmation contre l'extrait correspondant. Le code ne jugera ni les repas ni leur pertinence; il construira le contrat, verifiera sa completude et refusera seulement une acceptation typee sans justification.
- Renforcer aussi `AnswerAdequacyActionContractRepair` : une mention generique d'un domaine ne prouve pas une option concrete; un meme `E#` ne peut couvrir plusieurs affirmations que si son extrait les soutient toutes; apres un timeout, la reparation ne doit jamais prendre la validite de forme pour une preuve de contenu.
- Ajouter des regressions generiques pour la matrice cellule-preuve, l'acceptation avec raisons obligatoires et une reponse dont les citations existent mais dont les affirmations concretes sont absentes des extraits. Conserver le choix final `accept`, `revise` ou `need_more_evidence` au LLM.
- Le live constitue le premier succes mecanique de bout en bout, mais zero succes qualite. Il ne compte pas parmi les trois repetitions professionnelles exigees. Les validations WinUI, memoire, question RAG simple, nettoyage du depot et commits coherents restent ouvertes.
- Point d'arret demande par l'utilisateur : aucune nouvelle modification de la porte d'adequation et aucun nouveau live long ne sont lances. Le Goal reste actif pour une reprise ulterieure a partir de ce diagnostic source-verifie.

### 2026-07-17 - Contrat LLM cellule-affirmation-preuve avant toute acceptation structuree

- La porte d'adequation recoit maintenant une matrice mecanique `CELL_CLAIM_TO_CITATION_MAP`. Chaque cellule demandee est identifiee dans l'ordre par `C01` a `C20` avec son libelle de ligne, sa colonne, son affirmation concrete et les ids `[E#]` visibles dans cette cellule.
- L'extraction est generique dans `SourceContractVerifier.StructuredCells.cs`. Le code parse uniquement le tableau et les citations; il ne juge ni le domaine, ni la pertinence d'un plat, ni le sens d'une recommandation.
- `SourceBackedAnswerClaimAuditPrompt.cs` rappelle au LLM qu'un apercu de domaine, une categorie, un composant, un ingredient, un outil ou un conseil generique ne soutient pas automatiquement une option concrete complete. La reutilisation d'une preuve n'est valable que si son extrait soutient separement chaque affirmation qui la cite.
- Le resultat type `AnswerAdequacyReview` expose `auditedClaimRefs` et `unsupportedClaimRefs`. Pour accepter une reponse structuree, le LLM doit declarer avoir audite exactement tous les refs attendus, ne declarer aucun ref non soutenu et fournir au moins une raison semantique non vide.
- Le code verifie seulement ce contrat : couverture exacte, absence de doublon, presence explicite des deux champs, absence de contradiction entre `accept` et `unsupportedClaimRefs`. Il ne remplace jamais le verdict semantique du LLM.
- Une acceptation initiale incomplete declenche `AnswerAdequacyActionContractRepair`. Cette reparation peut etre la premiere porte semantique valide apres un timeout; son prompt interdit donc explicitement de confondre validite mecanique et preuve du contenu.
- Si la reparation accepte encore sans raisons, omet une cellule, duplique des refs ou omet `unsupportedClaimRefs`, le pipeline conserve un rejet source-backed au lieu de livrer la table. Si le LLM identifie des affirmations non soutenues, il peut choisir `revise` ou fournir une recherche executable `need_more_evidence`.
- La validation independante d'une revision applique le meme contrat avant toute acceptation. Une revision polie mais non auditee ne peut plus contourner cette porte.
- Les traces exposent maintenant la decision et les raisons avant reparation, le nombre de cellules auditees, le nombre de cellules non soutenues et l'acceptation ou le rejet du contrat repare. Le timeout ou le rejet initial n'est donc plus masque par la seule decision finale.
- Regressions ajoutees : extraction ordonnee de dix cellules et de leurs citations propres; prompt complet de vingt cellules sous budget; conservation des doublons JSON; rejet d'une acceptation sans raisons, avec audit incomplet ou champ obligatoire absent; execution d'une recherche choisie par le LLM lorsque dix affirmations citees sont absentes de leurs extraits.
- Les anciennes fixtures d'acceptation implicite ont ete migrees : elles doivent maintenant declarer les dix ou vingt refs reellement audites. Les reponses documentaires simples doivent au minimum fournir une justification semantique non vide.
- Validation finale du lot : compilation monoprocessus reussie, zero avertissement et zero erreur; 245/245 tests SourceBacked verts; 321/321 tests SourceBacked/OpenAI/API verts. Les fichiers SourceBacked restent sous la limite architecturale de 500 lignes.
- Prochaine preuve discriminante : rejouer le live canonique. L'ancien resultat hallucine ne doit plus etre accepte. Le LLM doit soit marquer les cellules non soutenues et choisir une recuperation/revision, soit accepter uniquement apres avoir rendu les vingt refs audites contre les extraits effectivement cites.
- Ce jalon ferme le contournement contractuel identifie dans `rag-20260717210217884-35e1dfec`, mais la qualite produit reste a prouver en live. Il ne constitue pas encore l'une des trois repetitions professionnelles finales.

### 2026-07-17 - Point d'arret propre apres le live protege par l'audit cellule-preuve

- Nouveau live canonique : `artifacts/client-live-final-weekly-meal-plan-20260717-220408/`, trace `rag-20260717220436727-091c582f`, journal `artifacts/live-validation/live-final-weekly-meal-plan-after-claim-audit-20260717.log`. L'execution s'est arretee proprement apres environ 15 min 25 s avec une reponse d'insuffisance honnete et zero carte source.
- Le verrou de securite a fonctionne : aucun planning trompeur n'a ete livre. Le Writer initial a produit un tableau incomplet et repetitif; sa reparation a aggrave les repetitions. Le verificateur mecanique a bloque la synthese avant l'audit semantique `C01` a `C20`, qui n'a donc pas encore ete exerce en live.
- Le Planner a choisi la categorie partagee `Cuisine` et quatre recherches mono-facette. La recherche petit-dejeuner a expire; les recherches dejeuner, souper et collation ont ramene des resultats. Le bundle final contenait vingt preuves, mais le `EvidenceJudge` initial n'en a selectionne aucune et le `EvidenceStatusReview` repare en a ensuite declare dix utiles.
- Le Writer a transforme des libelles de documents ou de sections en repas, notamment `Osez les boites de conserve!` et `PASSIONNEMENT CUISINE DEPUIS 1877 DES RECETTES POUR`. Il a aussi fortement repete ces libelles. Ce n'est pas un simple probleme de mise en forme : le LLM a recu une representation qui presentait chaque `contentCard.title` comme `option=...`.
- Un audit direct et sanitise de la reponse backend pour `Quels sont les plats de diner dans la cuisine` confirme que `contentCards.title` est heterogene. Il peut designer une recette concrete, mais aussi un titre de section, une accroche de page, une couverture de livre ou un libelle parasite. Le champ `kind` distingue notamment `section`, `exact_lead` et `page_embedded_title`; il est actuellement retire par le format compact transmis aux LLM.
- Exemples confirmes dans les chunks bruts : `Gratin d'endives` et `Couscous royal` sont accompagnes d'extraits de recette exploitables, tandis que `OSEZ LES BOITES DE CONSERVE!`, `PASSIONNEMENT CUISINE...`, `MES INDISPENSABLES` et `INTRO INTRO` sont des titres ou conseils generiques et ne prouvent pas une option de repas. `Garbure des Midi-Pyrenees` apparait comme titre de section alors que l'extrait visible porte sur le couscous.
- Cause racine immediate : `SourceBackedRagPrompts.ContentCards.cs` renomme mecaniquement le premier texte disponible en `option=...` et omet `kind`. Le code ne choisit pas semantiquement les bonnes cartes, mais il deforme le sens des donnees avant que le LLM puisse exercer ce jugement.
- Premiere correction a la reprise, generique et LLM-first : transmettre les cartes brutes sous une forme fidele telle que `candidateTitle`, `kind` et `proof`, sans jamais appeler automatiquement un titre `option`. Les prompts du Judge, du StatusReview, de ses reparations, du Writer et de la reparation structuree doivent exiger du LLM qu'il confronte le titre, le type, la preuve et l'extrait parent.
- Le LLM devra rejeter semantiquement un titre de livre, de document, de section, une accroche, un composant ou un conseil general lorsqu'il ne soutient pas directement la valeur concrete demandee. Le code conservera uniquement les responsabilites mecaniques : transporter les champs, verifier les contrats types, les citations, la couverture et les budgets.
- Deuxieme point a verifier avant tout nouveau live : les prompts de revue semblent tronquer le bundle a un nombre maximal d'elements pris dans l'ordre. Si les vingt preuves sont groupees par requete, certaines facettes, notamment la collation, peuvent etre absentes du contexte LLM. Inspecter `AppendSelectionRepairEvidence`; si la troncature est lineaire, representer mecaniquement chaque branche de recherche par un parcours round-robin, sans classement semantique dans le code.
- Regressions obligatoires avant reprise live : fidelite de la carte compacte avec conservation de `kind` et de `proof`; absence totale de `option=` derive d'un titre brut; refus LLM d'un titre de section sans preuve concrete; conservation d'une vraie valeur soutenue; representation d'au moins un element de chaque groupe de requete sous budget.
- Dernier socle valide avant ce live : compilation sans avertissement ni erreur, 245/245 tests SourceBacked et 321/321 tests SourceBacked/OpenAI/API verts. Le live suivant a seulement fourni un diagnostic; aucun nouveau changement de code ni nouveau resultat de tests n'est revendique apres ce diagnostic.
- Point d'arret demande par l'utilisateur : ne pas modifier maintenant la representation des cartes et ne pas relancer de test long. A la reprise, corriger d'abord la fidelite `candidateTitle/kind/proof` et la couverture des branches, executer les tests cibles puis les suites completes, et seulement ensuite rejouer le planning canonique.
- Le Goal reste actif. Restent non valides : un planning professionnel 5 x 4, trois repetitions live coherentes, une question RAG simple, le comportement memoire, le parcours WinUI, le nettoyage prudent du depot et les commits par lots coherents.

### 2026-07-18 - Cartes de contenu fideles et couverture des branches sous budget

- Le defaut confirme par `rag-20260717220436727-091c582f` est corrige dans `SourceBackedRagPrompts.ContentCards.cs`. Le format compact ne transforme plus automatiquement un titre backend en `option=...`.
- Chaque carte transmet maintenant explicitement `candidateTitle`, `kind` et `proof`; lorsqu'aucun titre n'existe, le texte exploitable reste expose comme `candidateText`. Le format non compact utilise les memes libelles semantiques afin d'eviter deux interpretations concurrentes du meme objet brut.
- `candidateTitle` reste un candidat extrait, jamais une preuve implicite. Les prompts du `EvidenceJudge`, de sa reparation JSON, des reparations de selection, du `EvidenceStatusReview`, de ses reparations de format/decision, du Writer, du Writer structure et de la reparation structuree imposent au LLM d'inspecter `kind`, `proof`, `facts` et l'extrait parent.
- Les regles LLM rejettent un titre de source, livre, document ou section, un slogan, une accroche, un composant, un outil ou un conseil general lorsque la preuve ou l'extrait ne soutient pas directement ce libelle comme valeur concrete demandee. Le runtime ne classe pas ces cas lui-meme; il transporte fidèlement les champs et controle uniquement les contrats.
- Une requete, une categorie, un `candidateTitle` ou un `kind` sont explicitement de la provenance, pas une preuve finale. Le Writer ne doit jamais copier ces champs dans une cellule seulement parce qu'ils existent.
- `AppendSelectionRepairEvidence` ne fait plus `Take(maxItems)` directement sur le bundle groupe. Les preuves sont entrelacees mecaniquement, en conservant l'ordre interne, par branche `(toolName, queryUsed, categoryPath)` avant application du budget.
- Le meme ordre round-robin alimente la fenetre initiale du `EvidenceJudge`. Apres un echec de verification, la reservation existante pour les observations recentes est conservee, mais le reste de la fenetre reste equilibre entre branches.
- Cette repartition ne constitue aucun classement semantique : elle garantit seulement que chaque recherche dispose d'une chance egale d'etre vue sous le budget. Le LLM reste seul responsable de juger quelle branche et quelle preuve sont utiles.
- Le `EvidenceStatusReview` affiche aussi la requete de chaque preuve compacte. Cela rend visibles les facettes representees et evite qu'un ensemble groupe soit confondu avec une couverture homogene.
- Regression de fidelite : une carte `General guide`, `kind=section`, sans preuve reste exposee comme titre candidat sans etre promue en option; une carte `Perform concrete control`, `kind=exact_lead`, conserve sa preuve; une carte sans titre conserve son `candidateText`. Aucun rendu ne contient `option=`.
- Regression pipeline generique : une revue de statut initialement invalide voit une carte titre-seul `E1` et une carte directement prouvee `E2`; la reparation LLM place `E1` dans `weakEvidenceIds`, selectionne seulement `E2`, et le Writer ne recoit plus le faux candidat.
- Regression de couverture : seize preuves groupees en quatre requetes sont reordonnees `E1, E5, E9, E13` au premier tour. Le prompt de statut tronque contient donc au moins une preuve des quatre branches au lieu d'omettre la derniere.
- Les budgets de prompts existants ont ete preserves. Le Judge large reste sous son plafond strict de 10 500 caracteres et les prompts 20 cellules restent sous leurs plafonds historiques; aucune limite n'a ete augmentee pour masquer la nouvelle information.
- Validation finale : compilation monoprocessus avec `UseSharedCompilation=false`, zero avertissement et zero erreur; 6/6 regressions ciblees; 249/249 tests SourceBacked; 325/325 tests SourceBacked/OpenAI/API.
- Hygiene locale : `git diff --check` ne signale aucune erreur d'espacement dans les changements suivis; aucun fichier `SourceBackedRag/*.cs` ne depasse 500 lignes; la recherche de `option=` dans le pipeline SourceBacked ne retourne plus aucun resultat.
- Prochaine preuve discriminante : relancer le planning canonique 5 x 4. La trace doit montrer que les titres/sections generiques restent faibles, que les facettes de recherche sont toutes visibles et que le Writer ne transforme plus une metadonnee de carte en repas.
- Le succes attendu n'est pas seulement un refus sur : le pipeline doit soit produire vingt cellules semantiquement soutenues qui passent l'audit `C01` a `C20`, soit identifier les cellules non soutenues et choisir une nouvelle recherche executable ou une insuffisance honnete.

### 2026-07-18 - Point d'arret propre apres le live cartes fideles : saturation de l'audit 4K

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260717-230059/`, trace principale `rag-20260717230127117-317c2442`, trace SourceBacked `sbrag-95313550c70d4c7f82cbc9a56c1aba2`, journal `artifacts/live-validation/live-final-weekly-meal-plan-after-card-fidelity-20260718.log`.
- L'execution s'est arretee proprement apres 17 min 03 s. Aucun planning trompeur ni aucune carte source n'ont ete livres : le pipeline a retourne une insuffisance honnete parce que le Judge d'adequation n'a pas fourni un JSON valide apres reparation de format.
- Le Writer a pourtant produit un brouillon de 986 caracteres, cite trois preuves (`E1`, `E10`, `E2`) et passe entierement le contrat mecanique. Ce live ne permet pas d'affirmer que son contenu etait semantiquement professionnel, car le brouillon n'a pas ete persiste et l'audit final n'a pas abouti.
- Le verrou immediat est la taille reelle du contexte local de 4 096 tokens. Le prompt initial `answer_adequacy_judge` atteignait 11 014 caracteres et sa reparation 10 498 caracteres; les sorties respectives de 3 070 et 3 060 caracteres n'etaient pas des contrats JSON exploitables. La reparation atteint la limite de contexte dans le journal du serveur local.
- Les budgets actuels bases uniquement sur le nombre de caracteres ne garantissent donc pas assez d'espace de sortie. La prochaine correction prioritaire est un audit semantique structure compact qui conserve la matrice `C01` a `C20` et les extraits cites, mais supprime les duplications du tableau, des axes, de la reponse complete et de la sortie invalide.
- La decision semantique restera au LLM. Le code pourra seulement choisir une representation compacte, verifier la couverture exacte des vingt refs et refuser un contrat incomplet ou invalide.
- Deux contrats secondaires restent a renforcer apres cette porte : la revue de statut a place `E2` simultanement dans `usefulEvidenceIds` et `weakEvidenceIds`, et le Planner a fait accepter comme quatre recherches distinctes des variantes obtenues principalement par suppression de mots plutot que quatre vraies facettes.
- La diversite du Planner doit rester une decision LLM typee, mais son contrat mecanique doit refuser une reparation structurellement contradictoire avec les axes qu'elle pretend couvrir. De meme, les listes utiles et faibles doivent etre mecaniquement disjointes puis, en cas de conflit, renvoyees au LLM pour reparation.
- Etat valide avant ce live : compilation sans avertissement ni erreur; 249/249 tests SourceBacked et 325/325 tests SourceBacked/OpenAI/API verts. Le live suivant a fourni le diagnostic ci-dessus; aucune correction de la porte 4K et aucune nouvelle validation ne sont revendiquees apres ce diagnostic.
- Point d'arret demande par l'utilisateur : aucun nouveau test, live ou changement d'architecture n'est lance. Le Goal reste actif pour une reprise depuis l'audit compact, puis les contradictions de statut et la vraie diversite des recherches.
- Restent ouverts : un planning 5 x 4 professionnel et source-backed, trois repetitions live coherentes, une question RAG simple, le comportement memoire, le parcours WinUI, le nettoyage prudent du depot et les commits par lots coherents.

### 2026-07-18 - Audit semantique 4K compact et contrats live rendus non ambigus

- La porte `AnswerAdequacyJudge` ne duplique plus un tableau structure complet dans `CANDIDATE_ANSWER` puis dans `CELL_CLAIM_TO_CITATION_MAP`. Pour une reponse structuree, la matrice `C01` a `C20` est maintenant la representation canonique compacte du brouillon a auditer.
- Chaque ligne de matrice conserve les cinq informations necessaires au LLM : ref, ligne, colonne, affirmation concrete et ids de preuve cites. Le contrat exige toujours un audit exact de toutes les refs et interdit une acceptation avec une affirmation non soutenue.
- Les preuves effectivement visibles dans le brouillon sont placees en premier dans `ALLOWED_EVIDENCE`; les preuves selectionnees restantes completent ensuite la fenetre. Le code ne classe pas leur pertinence : il garantit seulement que le LLM voit d'abord les extraits qu'il doit verifier.
- Les extraits d'audit sont compacts et restent associes a leur document et page. Les chemins non structures, notamment les questions de famille documentaire, conservent la reponse candidate et leurs metadonnees specifiques.
- Les reparations de format et de contrat ne reinjectent plus la sortie LLM invalide. Elles indiquent explicitement qu'elle a ete omise parce qu'elle n'est pas une preuve et demandent une reevaluation depuis la question, la matrice et les extraits actuels.
- Une regression 20 cellules utilise dix preuves aux chemins et extraits volontairement longs, dont `E10`, et simule une sortie invalide de 3 070 caracteres comme le live. Le prompt initial reste sous 8 000 caracteres; les reparations restent sous 8 300 caracteres et ne contiennent pas le faux texte.
- La revue `EvidenceStatusReview` impose maintenant que `usefulEvidenceIds` et `weakEvidenceIds` soient disjoints. Un chevauchement est une contradiction mecanique : le pipeline le nomme, renvoie toute la revue au LLM et exige que celui-ci reclasse semantiquement chaque id depuis sa preuve et son extrait.
- Une reparation de statut encore incoherente n'est plus silencieusement reutilisee. Elle est rejetee comme contrat invalide, ce qui declenche la reparation JSON existante; si aucun contrat coherent n'est obtenu, aucune selection ambigue ne peut atteindre le Writer.
- Une regression pipeline reproduit `E2` a la fois utile et faible. Le prompt de reparation expose exactement l'overlap; le LLM conserve `E1` utile, classe `E2` faible, puis cette decision reparee alimente la selection sans classement semantique dans le code.
- Le Planner dispose maintenant d'un signal mecanique generique contre les fausses diversites par suppression de mots. Quatre requetes differentes restent invalides si l'ensemble des tokens de l'une est un sous-ensemble strict de celui d'une autre.
- Ce signal ne juge pas le sens des mots. Il detecte uniquement une relation formelle de requetes imbriquees, bloque leur execution, puis demande au LLM de produire de vrais angles semantiques independants. Les prompts interdisent explicitement les variantes obtenues par simple suppression de termes.
- Une regression rejoue les quatre requetes du live (`plan de repas...`, puis suppressions successives). Les six relations de sous-ensemble sont detectees; la premiere reparation LLM encore imbriquee est refusee; un second plan a quatre facettes independantes est accepte et seul celui-ci atteint l'executeur RAG.
- Validation finale du lot : compilation monoprocessus avec `UseSharedCompilation=false`, zero avertissement et zero erreur; 9/9 regressions ciblees; 252/252 tests SourceBacked; 328/328 tests SourceBacked/OpenAI/API.
- Hygiene : `git diff --check` ne signale aucune erreur d'espacement. Tous les fichiers SourceBacked modifies restent sous 500 lignes.
- Prochaine preuve discriminante : nouveau live canonique 5 x 4. Les traces doivent montrer des recherches non imbriquees, des listes utile/faible disjointes, un prompt d'adequation sensiblement sous le plafond 4K et un JSON final exploitable.
- Le planning ne comptera comme succes que si ses vingt cellules sont semantiquement soutenues contre les extraits cites et si les cartes de sources visibles correspondent au meme `EvidenceBundle`.

### 2026-07-18 - Live contrats 4K : Planner et statut prouves, reparation mince reroutee

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260717-235543/`, trace `rag-20260717235610747-70316846`, journal `artifacts/live-validation/live-final-weekly-meal-plan-after-4k-contracts-20260718.log`. Le test s'est termine apres 18 min 47 sans livrer de planning ni de carte source trompeuse.
- Le Planner initial puis sa premiere reparation ont ete refuses avant execution. Le second essai melangeait encore deux facettes par requete et quatre categories; il a aussi ete refuse.
- Le LLM a ensuite choisi explicitement le corpus partage `Cuisine`. Le contrat type final a produit exactement quatre requetes independantes : `Petit dejeuner cuisine`, `Diner cuisine`, `Souper cuisine`, `Collation cuisine`. La trace confirme une facette copiee au maximum, zero relation de sous-ensemble et un seul scope.
- La recherche petit-dejeuner a expire apres 35 secondes. Les trois autres ont retourne respectivement 12, 1 et 1 hits. Ce timeout reste qualifie comme requete degradee, pas comme preuve d'absence dans la base.
- Le `EvidenceStatusReview` initial de 14 645 caracteres a rendu une sortie invalide. Sa reparation de format, 11 876 caracteres, a produit un JSON apres 146,8 secondes mais classait des ids dans des listes contradictoires : quatre utiles et trois faibles.
- Le nouveau contrat disjoint a fonctionne en live. `EvidenceStatusReviewDecisionContractRepair` a renvoye la contradiction et les preuves au LLM; celui-ci a retourne quatre utiles, zero faible et zero probleme mecanique restant. Le code n'a classe aucune source a sa place.
- La selection finale a transmis `E1` a `E4` au Writer. Le Writer structure a termine en 176,5 secondes avec vingt cellules, mais le verificateur a bloque `Noix [E4]` et `Banane [E4]` comme cellules trop minces.
- La premiere reparation generale de 11 552 caracteres a termine, mais a conserve les deux cellules invalides. Elle a place les citations en debut de cellule, ce qui a ajoute douze taches detaillees au passage suivant.
- La deuxieme reparation generale a ainsi grossi a 15 337 caracteres. Le serveur local l'a refuse avant generation avec l'erreur exacte : `request (4159 tokens) exceeds the available context size (4096 tokens)`.
- Cause dans `SourceBackedStructuredRepairPrompt.HasFocusedRepairWork` : `thin_structured_cell` etait reconnu comme erreur structuree, mais n'activait pas le chemin cible si la cellule contenait encore un mot. Le pipeline tombait donc a tort dans `BuildRepairMessages`, beaucoup plus volumineux.
- Correction : sur une grille typee, `thin_structured_cell` active maintenant directement `StructuredCellRepair`. Le LLM recoit le contrat des vingt cellules, les erreurs actuelles, un resume court du brouillon et les preuves compactes; le code ne choisit toujours pas le contenu de remplacement.
- Le nombre d'indices detailles `CITATION_ONLY_CELLS_TO_REWRITE` passe de douze a six pour laisser de la marge au contexte 4K. Cette limite ne reduit pas le livrable : les vingt `REQUIRED_CELL_KEYS` restent obligatoires et `structuredTableRows` doit toutes les fournir.
- Deux regressions couvrent le cas : une mesure de prompt 20 cellules avec citations en tete et quatre preuves longues, et un pipeline reproduisant exactement deux cellules d'un mot. Le second appelle `StructuredCellRepair`, jamais la reparation generale, puis retourne vingt cellules mecaniquement valides.
- La regression severe refusait initialement 10 009 caracteres sous un plafond de 9 000; la compaction des seuls indices l'a fait passer sans relever le plafond.
- Validation finale : compilation monoprocessus avec `UseSharedCompilation=false`, zero avertissement et zero erreur; 2/2 tests cibles; 254/254 tests SourceBacked; 330/330 tests SourceBacked/OpenAI/API.
- Prochaine preuve discriminante : rejouer le live canonique. Si le Writer reproduit des cellules minces, la trace doit contenir `structured_cell_repair` sous 9 000 caracteres et aucun second `repair` general depassant le contexte.
- Le gate produit reste l'audit semantique compact `C01` a `C20`. Une table mecaniquement valide ne sera acceptee que si le LLM relie chaque affirmation a l'extrait cite ou demande une recuperation executable.

### 2026-07-18 - Live du routage cible : timeout structure distingue du depassement 4K suivant

- Second live canonique apres le reroutage des cellules minces : `artifacts/client-live-final-weekly-meal-plan-20260718-003005/`, trace `rag-20260718003032157-bf7fbcc5`, journal `artifacts/live-validation/live-final-weekly-meal-plan-after-thin-structured-route-20260718.log`. Le test s'est termine proprement apres 20 min 04 sans livrer de planning ni de carte source non verifies.
- La protection contre les requetes imbriquees est de nouveau prouvee : la premiere reparation de diversite a ete refusee avec `outcome=rejected_strict_query_token_subset` et quatre relations de sous-ensemble. Le LLM a ensuite produit quatre facettes typees acceptees dans `Cuisine`.
- La recherche `Petit dejeuner cuisine` a expire a 35 secondes. Les trois autres facettes ont retourne 12, 1 et 1 hits. Le statut initial et sa reparation ont converge vers dix preuves utiles, zero faible et une decision `answer` coherente apres reparation du contrat disjoint.
- Le `structured_writer` de 6 685 caracteres a atteint son timeout de 210 secondes sans brouillon exploitable. Le reroutage corrige a ensuite appele exactement `structured_cell_repair`, avec un prompt de 7 870 caracteres : aucun second `repair` general volumineux n'a ete utilise.
- `StructuredCellRepair` n'a pas depasse le contexte. Il a atteint son propre timeout encore fixe a 180 secondes. Cette distinction est importante : le depassement 4K s'est produit seulement dans l'appel de recuperation suivant.
- Apres cet echec de redaction, `evidence_judge_action_repair` a demarre avec 16 030 caracteres. Le serveur l'a refuse immediatement avec l'erreur exacte `request (4113 tokens) exceeds the available context size (4096 tokens)`.
- Le timeout du seul evenement `structured_cell_repair` passe donc de 180 a 210 secondes, comme le Writer structure. Les reparations generiques restent a 180 secondes et les autres etapes ne sont pas artificiellement ralenties.
- `EvidenceJudgeActionRepair` utilise maintenant une representation de recuperation dediee : question bornee, forme demandee compacte, contraintes, tours restants, derniers appels, catalogue exact, decision precedente et six preuves equilibrees par branche avec source, requete, carte de contenu et extrait.
- Le LLM conserve le choix semantique complet entre `answer`, `need_more_evidence` et `clarify`, ainsi que le choix des ids, requetes et chemins. Le code compacte le transport et verifie seulement les limites, ids et contrats executables.
- La memoire reste presente mais explicitement `context_only_never_final_proof`. Le prompt conserve les preferences, le document focalise, le scope resolu, un ancrage et une note de recherche recents; aucune ancienne reponse ni aucun ancien extrait ne peut etre selectionne comme preuve sans reapparaitre dans `CURRENT_EVIDENCE`.
- La regression de budget reproduit un cas plus severe que le live : grille de vingt cases, dix preuves longues avec cartes, memoire riche, dix categories, douze requetes precedentes et un echec de verification. Elle a d'abord mesure 15 286 caracteres, puis passe sous le plafond maintenu a 9 000 caracteres apres suppression des duplications; le plafond n'a pas ete releve.
- Deux autres budgets de recuperation existants sont eux aussi resserres a 9 000 caracteres. La fenetre conserve six preuves concretes et annonce explicitement les omissions; le test verifie aussi que la memoire et le brouillon invalide volumineux ne sont pas reinjectes.
- Validation finale du lot : compilation reussie avec zero avertissement et zero erreur; 256/256 tests SourceBacked; 332/332 tests SourceBacked/OpenAI/ApiClient.
- Hygiene : les quatre fichiers SourceBacked concernes font entre 85 et 475 lignes, aucun espace terminal n'est detecte et `git diff --check` ne signale aucune erreur, seulement les avertissements de fins de ligne deja presents dans le depot.
- Prochaine preuve discriminante : relancer le live canonique 5 x 4. La trace doit montrer `structured_cell_repair` avec 210 secondes, puis soit une table verifiee et auditee, soit une recuperation `evidence_judge_action_repair` nettement sous 9 000 caracteres et executable sans erreur 4K.
- Le planning professionnel reste non valide tant que vingt cellules semantiquement soutenues, leurs cartes de sources, l'audit `C01` a `C20` et les repetitions live/WinUI ne sont pas effectivement observes.

### 2026-07-18 - Live recovery 4K validee, nouvelle boucle de statut isolee

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-013513/`, trace `rag-20260718013540365-1d62d164`, journal `artifacts/live-validation/live-final-weekly-meal-plan-after-action-repair-4k-20260718.log`. Le test a atteint son budget global de 24 minutes et s'est termine par `OperationCanceledException`; aucun fichier de reponse n'a ete produit et aucune reponse trompeuse n'a ete livree.
- La correction prioritaire est prouvee en conditions reelles. Trois appels `evidence_judge_action_repair`, de 7 202, 7 409 et 7 368 caracteres, ont tous termine normalement en 57,1 s, 22,7 s et 28,0 s. Aucun depassement du contexte 4 096 tokens n'est reapparu.
- Les decisions de recuperation sont restees au LLM. Il a d'abord choisi `documents.navigation` sur `Cuisine`, puis deux lectures `documents.context` de `Cuisine/si-on-cuisinait.pdf`, pages 7 et 62. Le code n'a choisi ni ces outils, ni ces documents, ni ces pages.
- Le premier blocage du live est maintenant la boucle couteuse `EvidenceStatusReview`. Les deux premiers statuts, de 14 056 et 14 262 caracteres, ont produit des sorties non parsables; leurs reparations de format, de 11 861 et 11 796 caracteres, sont elles aussi restees invalides apres respectivement 134,5 s et 150,4 s.
- Le troisieme statut, de 13 797 caracteres, a enfin ete parse : `need_more_evidence`, sept preuves utiles, zero faible. Sa reparation de contrat a ajoute une lacune explicite et a conserve cette decision semantique. Le pipeline a donc execute une nouvelle lecture choisie par le LLM.
- Le statut suivant, de 13 212 caracteres, a rendu 1 920 caracteres invalides. Sa reparation de format, encore volumineuse a 11 309 caracteres, a ete annulee par le budget global apres 40,5 s. La pile terminale pointe exactement `ReviewEvidenceStatusAsync`, et non `EvidenceJudgeActionRepair` ni le Writer.
- Le runtime local s'est arrete proprement apres le test. Le serveur n'est plus actif; les artefacts et journaux ont ete conserves.
- Nouveau travail prioritaire : inspecter le contrat type, le parseur et les prompts de `EvidenceStatusReview`; rendre cette revue et sa reparation compactes sous 4K avec une marge de sortie suffisante, sans transferer au code le classement utile/faible, la detection des lacunes ni la decision `answer/need_more_evidence/write_partial`.
- La correction devra limiter les cycles de statut redondants par un contrat mecanique explicite et testable, tout en laissant le LLM reevaluer la preuve actuelle. Elle devra aussi conserver la memoire comme contexte seulement et les extraits courants comme seules preuves finales.
- Avant le prochain live : ajouter une regression severe reproduisant une grille de vingt cellules, un bundle multibranche et une premiere sortie de statut invalide; verifier les budgets des prompts initial, format-repair et decision-contract-repair; executer les suites SourceBacked et etendues puis une compilation sans avertissement ni erreur.

### 2026-07-18 - EvidenceStatusReview compact, JSON contraint et boucle live prete a rejouer

- Les trois portes `EvidenceStatusReview`, `EvidenceStatusReviewFormatRepair` et `EvidenceStatusReviewDecisionContractRepair` utilisent maintenant une representation compacte commune. Elle conserve la question bornee, les contraintes, les cinq lignes et quatre colonnes, les tours restants, les recherches et lectures deja tentees, les echecs de verification et huit preuves equilibrees entre branches.
- Chaque preuve compacte conserve son id, son document, sa page, sa requete, ses risques, une carte fidele `candidateTitle/kind/proof` et un extrait parent. La fenetre ne classe rien semantiquement dans le code; elle preserve l'ordre interbranche, annonce les preuves omises et demande au LLM de classer seulement les ids visibles.
- Le LLM reste le controleur semantique : lui seul decide `usefulEvidenceIds`, `weakEvidenceIds`, les lacunes, l'etat de la base et `recommendedNextAction`. Le code controle uniquement les ids connus, la disjonction utile/faible, la coherence retrieval/tours/lacunes et la syntaxe du contrat.
- La memoire est presente dans les trois variantes sous `role: context_only_never_final_proof`. Le profil, les preferences, le document focalise, le scope resolu, un ancrage et une note de recherche recents restent disponibles pour la continuite; l'ancienne reponse assistant et les anciens extraits ne sont jamais reinjectes comme preuve.
- Une sortie Status invalide n'est plus recopiee dans la reparation. Le prompt indique uniquement `omitted_non_evidence` et sa longueur, puis exige une reevaluation depuis `CURRENT_EVIDENCE`. Cela supprime jusqu'a plusieurs milliers de caracteres non fiables observes en live.
- Le schema de sortie impose un seul objet JSON compact avec les dix champs existants, des listes bornees et des raisons courtes. Le budget de generation Status passe de 560 a 480 tokens pour limiter les longues derivations et garder une marge dans le contexte 4 096 tokens.
- Le client local honore maintenant reellement `forceJson` avec `response_format: { type: json_object }`. Il ne change aucune valeur semantique : llama.cpp genere toujours le contenu, mais sa syntaxe est contrainte. Si un endpoint OpenAI-compatible refuse ce parametre par HTTP 400 ou 422, le client retente automatiquement avec l'ancien contrat JSON par prompt.
- La regression severe reutilise le cas 20 cellules avec memoire riche, dix preuves longues, dix categories, douze recherches anterieures et un echec structure. Les prompts Status initial, format-repair et decision-contract-repair restent tous sous le plafond maintenu a 9 000 caracteres, conservent huit preuves, les cartes et la memoire, et excluent la grande sortie invalide, l'ancienne reponse et le brouillon echoue.
- Deux tests du client HTTP confirment l'ajout de `response_format` en mode JSON et sa suppression lors du repli compatible. Les appels non JSON restent inchanges.
- Validation finale : 15/15 tests cibles, 256/256 tests SourceBacked, 334/334 tests SourceBacked/OpenAI/ApiClient; compilation monoprocessus reussie avec zero avertissement et zero erreur.
- Hygiene : `git diff --check` ne detecte aucune erreur d'espacement sur les fichiers concernes; seul l'avertissement de fin de ligne preexistant de `RagChatAgent.cs` subsiste. Les fichiers SourceBacked de ce lot font 71, 102 et 180 lignes, sous la limite architecturale de 500 lignes.
- Prochaine preuve discriminante : nouveau live canonique 5 x 4. Les traces doivent montrer des prompts Status sous 9 000 caracteres, du JSON parsable au premier appel ou apres une reparation courte, aucune boucle de plusieurs minutes, puis l'acces au Writer et a l'audit semantique cellule-preuve.

### 2026-07-18 - Live court : grammaire JSON globale refusee, portee limitee au Status

- Live canonique court `artifacts/client-live-final-weekly-meal-plan-20260718-022616/`, trace `rag-20260718022648674-b262f682`, journal `artifacts/live-validation/live-final-weekly-meal-plan-after-status-compaction-json-20260718.log`. Le test s'est termine apres 4 min 26 avec la reponse de securite source-backed et zero source affichee.
- Le routeur a correctement choisi `rag.multi_search`, puis le Planner de 8 214 caracteres a rendu 2 336 caracteres apres 124,4 s. Sa reparation de format, 6 960 caracteres, a rendu 2 342 caracteres apres 87,3 s. Les deux sorties sont restees invalides et le pipeline s'est arrete avant toute recherche.
- Cette regression n'est pas causee par la compaction Status, qui n'a jamais ete atteinte. Elle a ete introduite par l'application de `response_format=json_object` a tous les appels `forceJson`. Le serveur n'a pas refuse le parametre, mais la grammaire native a pousse le petit modele a produire de longs objets jusqu'a son budget sur les prompts Planner.
- La reponse de securite a fonctionne : `Le pipeline source-backed n'a pas pu terminer sa verification...`; aucun ancien chemin, planning invente ou carte source trompeuse n'a pris le relais. Le runtime local s'est arrete proprement.
- Correction de portee : le mode prompt-only historique est restaure pour Planner, Judge, Writer, reparations structurees et audit. `response_format=json_object` est maintenant demande uniquement lorsque le marqueur d'etape commence par `EvidenceStatusReview`, donc pour le statut initial et ses deux reparations.
- Cette restriction est mecanique et cible le defaut live observe. Elle ne change ni le contenu des prompts, ni les decisions semantiques, ni les budgets des autres etapes. Le client conserve son repli HTTP 400/422 si un endpoint refuse la grammaire JSON native.
- Regression de routage : les trois marqueurs Status activent la grammaire; `Planner` et `WriterStructuredTable` ne l'activent pas. Les tests directs du client continuent de verifier l'envoi de `response_format` et le repli sans ce champ.
- Validation apres correction : 12/12 tests cibles, 335/335 tests SourceBacked/OpenAI/ApiClient; compilation reussie avec zero avertissement et zero erreur.
- Prochaine preuve : rejouer le live canonique. Le Planner doit retrouver une sortie courte et son ancien chemin de diversite; le premier Status doit ensuite mesurer moins de 9 000 caracteres et tester la grammaire native dans la seule zone qui bouclait.

### 2026-07-18 - Live avec JSON limite au Status : Planner retabli, nouveau plafond localise au EvidenceJudge

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-023646/`, trace `rag-20260718023710466-341b2890`, journal `artifacts/live-validation/live-final-weekly-meal-plan-after-status-scoped-json-20260718.log`. Le test s'est termine proprement apres environ 4 min 41 avec la reponse de securite et zero carte source; aucun planning non verifie n'a ete livre.
- La restriction de `response_format` a fonctionne : le Planner est revenu a une sortie courte de 680 caracteres en 29,6 secondes, au lieu de remplir son budget comme dans le live precedent.
- Le chemin de diversite LLM a refuse la copie de question et les plans multi-facettes/multi-categories, puis le LLM a choisi le corpus partage `Cuisine`. Quatre recherches semantiquement distinctes ont ete acceptees : petit-dejeuner (3 hits), diner (10 hits), souper (9 hits) et collation (10 hits).
- Le prochain blocage est precis : le premier prompt `EvidenceJudge`, long de 15 697 caracteres, a ete refuse avant generation avec `request (4165 tokens) exceeds the available context size (4096 tokens)`. Le `EvidenceStatusReview` compact n'a donc pas encore ete atteint dans ce run.
- Ce resultat ferme le diagnostic Planner/JSON global et deplace la priorite sur la representation initiale du Judge. Il ne justifie ni davantage de volume RAG ni une decision semantique codee en dur.

### 2026-07-18 - EvidenceJudge compact sous 4K, fidelite des petits bundles preservee

- Les prompts `EvidenceJudge` et `EvidenceJudgeFormatRepair` utilisent maintenant la meme representation compacte et equilibree que le Status : intake borne, memoire non probante, workspace, deux hints catalogue, huit preuves interbranche et contrat de sortie borne.
- La sortie Judge invalide n'est plus reinjectee dans la reparation de format. Elle est declaree `omitted_non_evidence` avec sa longueur, puis le LLM rejuge depuis `CURRENT_EVIDENCE`.
- Les huit preuves visibles conservent id, source, page, requete, risques, carte `candidateTitle/kind/proof/facts` et extrait. Le code ne choisit toujours pas les preuves utiles : il dimensionne et entrelace seulement la fenetre, puis le LLM decide `answer`, `need_more_evidence`, `clarify`, les ids et les actions de suivi.
- La compaction est adaptative sans logique metier : un gros bundle live reste fortement borne; un petit bundle conserve le chemin complet, une categorie partagee ou les categories individuelles et jusqu'a quatre cartes lorsqu'une seule preuve porte une banque d'options. Cela evite de perdre les informations necessaires aux questions simples ou aux pivots de corpus.
- Les quatre regressions revelees par la premiere compaction sont fermees : separation famille documentaire/famille sujet, conservation des quatre options d'une page riche, chemin document complet pour une lecture de contexte et visibilite du changement de `categoryPath`.
- La regression severe 20 cellules avec memoire, echec anterieur et preuves multibranches confirme que Judge initial, Judge apres echec, Judge format-repair et les trois variantes Status restent chacun sous 9 000 caracteres, avec huit preuves visibles et sans ancienne reponse ni sortie invalide.
- Optimisation strictement comportement-identique : l'ensemble constant des termes de bruit etait reconstruit a chaque comparaison de candidat. Il est maintenant partage statiquement; le test concerne passe de plus de huit minutes de CPU a moins d'une milliseconde apres compilation, sans changer un seul terme, score ou choix.
- Validation finale de ce palier : 4/4 regressions de fidelite, 1/1 regression severe, puis 335/335 tests `SourceBacked|OpenAi|ApiClient` verts en 22 secondes. Compilation WinUI x64 Debug reussie avec 0 avertissement et 0 erreur.
- Un essai exploratoire de toute la suite client a expose des echecs legacy deja hors du perimetre canonique, surtout dans `RagContextBudgetRegressionTests`; il a ete arrete car il n'etait pas discriminant pour ce correctif. Le journal partiel est conserve sous `artifacts/test-results/source-backed-extended-20260718-051923/` et ne doit pas etre presente comme une suite complete verte.
- Prochaine preuve discriminante : rejouer le live 5 x 4. Le premier Judge doit cette fois entrer sous la fenetre 4 096 tokens, produire ou reparer un JSON court, puis atteindre le `EvidenceStatusReview` compact, le Writer structure et l'audit cellule-preuve. La reponse finale reste non validee tant que ce parcours reel n'est pas vert puis repete.

### 2026-07-18 - Live Judge/Status sous 4K prouve, echec de diversite de la selection Writer

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-032607/`, trace principale `rag-20260718032634031-818d4648`, trace SourceBacked `sbrag-ef819bb153124aaca48392adc46ebb84`, journal `artifacts/live-validation/live-final-weekly-meal-plan-after-judge-compaction-20260718.log`. Le test s'est termine proprement apres 17 min 49 avec la reponse de securite, zero carte source et le runtime local arrete.
- La correction Judge est prouvee en conditions reelles : le premier `EvidenceJudge` mesurait 6 913 caracteres au lieu de 15 697 et a rendu 219 caracteres en 25,4 secondes. Les trois Judge suivants sont aussi restes entre 6 772 et 6 959 caracteres et ont tous termine normalement.
- La correction Status est egalement prouvee : quatre `EvidenceStatusReview` de 6 789 a 6 961 caracteres ont produit des JSON parsables de 685 a 743 caracteres en 28,6 a 59,8 secondes. La reparation de coherence finale mesurait 6 085 caracteres et a confirme `write_partial` sans probleme contractuel restant.
- Le Planner a finalement choisi `Cuisine` et quatre recherches independantes. `Petit dejeuner cuisine` a expire a 35 secondes sans etre traite comme preuve d'absence; `Diner cuisine`, `Souper cuisine` et `Collation cuisine` ont retourne 12, 1 et 1 hits.
- Les decisions de recuperation sont restees au LLM : une navigation `Cuisine`, puis deux lectures exactes de `Cuisine/si-on-cuisinait.pdf`, pages 7 et 62. Aucun document ni page n'a ete choisi par le code.
- Au dernier tour, le Status a nomme `E1`, `E16`, `E17`, `E18` comme utiles, mais la selection transmise au Writer n'a conserve que deux ids visibles, `E1` et `E18`. Le Writer structure de 4 814 caracteres a donc rempli les vingt cellules a partir de deux preuves seulement.
- La premiere table contenait vingt cellules mais le verificateur a bloque `Noix [E1]` et `Banane [E18]` comme valeurs trop minces. La premiere reparation ciblee les a conservees; la deuxieme a cree quatre cellules vides le mardi; la reparation generale a retabli la ligne mais a laisse `Noix` et `Fraises` trop minces.
- Le verrou de securite a fonctionne : le pipeline n'a expose ni la table mecaniquement invalide, ni des cartes source trompeuses. L'audit semantique cellule-preuve n'a pas ete atteint car le contrat mecanique devait echouer avant lui.
- Nouveau blocage principal : comprendre pourquoi une revue Status qui voit quatre preuves utiles aboutit a seulement deux preuves visibles pour le Writer, puis verifier si les preuves retenues exposent reellement assez de cartes/faits legitimes pour vingt cellules. La prochaine correction doit ameliorer le passage EvidenceBundle -> selection LLM -> Writer, sans classer les repas dans le code et sans simplement relacher `thin_structured_cell`.

### 2026-07-18 - Contrats Planner/Status renforces, blocage de decouverte documentaire isole

- Le live precedent a ete reanalyse jusqu'aux resultats backend. `Diner cuisine` retournait surtout des pages generales ou techniques; la page 62 contenait bien une fondue chinoise, tandis que `Souper cuisine` et `Collation cuisine` ne fournissaient aucun ensemble credible d'options. Le Writer avait donc invente l'essentiel des vingt repas a partir de preuves inadaptees; le verrou mecanique l'a correctement empeche de publier cette table.
- Le Planner refuse maintenant une requete composee uniquement du libelle de facette et du chemin de categorie. Le controle est lexical et generique : il exige deux termes de contenu independants hors de ces libelles, puis rend au LLM la responsabilite de choisir leur sens au moyen d'une reparation ciblee.
- La regression rejoue exactement `Petit dejeuner cuisine`, `Diner cuisine`, `Souper cuisine` et `Collation cuisine`. Ces recherches sont bloquees avant execution; seules les nouvelles requetes semantiquement enrichies choisies par le LLM peuvent atteindre le RAG.
- La fenetre Status expose des groupes de sources stables par document/page et reserve les lectures exactes les plus recentes. Le contrat refuse les ids invisibles et plusieurs ids utiles representant la meme page; il renvoie le probleme au LLM, qui reste seul responsable du choix du meilleur representant.
- Les regressions couvrent un id invente `E99`, deux ids de la meme page et la conservation de la lecture recente de la page 62. Le code controle uniquement l'appartenance aux preuves visibles et l'unicite mecanique du groupe; il ne classe pas la pertinence des pages.
- Validation du lot : 338/338 tests `SourceBacked|OpenAi|ApiClient` verts en 25 secondes; `git diff --check` sans erreur sur les fichiers concernes. Aucun nouveau live long n'a ete lance apres cette validation.
- Le diagnostic backend suivant empeche de considerer la correction Planner comme suffisante : `diner plats recettes ingredients` puis `recettes ingredients preparation` retournent zero hit dans `Cuisine`, avec `no_relevant_source_found` pour la seconde recherche. Enrichir lexicalement les requetes evite le bruit, mais ne decouvre pas encore les titres ou pages de recettes.
- Prochaine etape prioritaire a la reprise : auditer le chemin `documents.navigation` et son filtre `q`, puis tester une phase generique de decouverte de titres/ancrages pilotee par le LLM. Le LLM devra choisir les requetes de navigation et les candidats a lire; le code pourra seulement exiger un contrat executable, transporter les ancres et verifier que chaque affirmation finale est rattachee a une preuve courante.
- Il est premature de relancer le live de 17 a 24 minutes avant d'avoir obtenu, par navigation ou autre outil existant, une vraie banque de candidats source-backed. Apres ce palier seulement : live canonique 5 x 4, audit semantique `C01` a `C20`, question RAG simple, memoire, parcours WinUI, repetitions live, nettoyage prudent et commits par lots coherents.
- Point d'arret demande par l'utilisateur : aucun nouveau test, processus, nettoyage ou commit n'est lance. Le depot reste tres sale et melange des changements historiques; un commit automatique global risquerait d'embarquer du travail non isole. Le Goal reste actif pour une reprise depuis la decouverte `documents.navigation`.

### 2026-07-18 - Troisieme live : budgets fermes, perte d'axe et action LLM non executable

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-085621/`, trace principale `rag-20260718085652569-7d432de7`, journaux processus `artifacts/live-test-process-20260718-105615/`. Le test s'est termine proprement en environ 7 min 12 s; le runtime local a ete arrete et aucun planning ni aucune source non verifies n'ont ete exposes.
- Les budgets de contexte du palier precedent sont prouves sur le modele reel : Planner 7 951 caracteres, revue de strategie 6 249, Judge 7 683, Status 7 906, selection-repair 9 844 et action-repair 7 294. Aucun HTTP 400 de contexte et aucun timeout RAG ne sont apparus.
- Le LLM a declare `queryCoverageDecision=row_independent_candidate_pool`, mais son intake effectif ne conservait que `row:Jour` et les cinq jours comme colonnes, avec `rowHeaderLabel=Ligne`. Les quatre creneaux explicitement demandes ont disparu avant la recuperation. Le nombre apparent de cellules n'etait donc plus 20 et le contrat mecanique n'a pas identifie la perte semantique.
- La strategie acceptee ne contenait qu'une requete large, `plan de repas semaine Lundi a Vendredi petit-dejeuner diner souper collation`, dans `Cuisine`. Elle a retourne 10 hits en 14,8 secondes : 10 documents/pages distincts, aucune surcharge, aucun echec et un resultat brut de 123 332 caracteres compacte ensuite dans 10 preuves canoniques.
- Le premier Judge a rendu `decision=answer`, zero `selected_ids` et la note contradictoire indiquant que toutes les cases etaient soutenues. La reparation mecanique a refuse l'ecriture sans preuve; aucun id n'a ete choisi automatiquement par le code.
- Le `EvidenceStatusReview` a apporte un signal semantique plus plausible : `useful=E2`, sept preuves faibles, une lacune, evaluation `Sources are fragmented and lack detailed meal plans`, prochaine action `read_documents`.
- La selection-repair n'a pas fourni une selection acceptable. Deux appels `EvidenceJudgeActionRepair`, chacun sous budget, ont rendu la meme sortie courte mais aucun appel d'outil executable. Le controleur a donc termine `reason=no_executable_follow_up` au premier tour au lieu d'inventer une navigation ou un document.
- La reponse terminale contient 297 caracteres et zero carte source. Elle melange encore des notes contradictoires en anglais dans le message utilisateur; cette surface devra etre nettoyee apres la convergence de l'orchestration.
- Prochaine experience discriminante : faire auditer par le LLM la preservation exacte des axes/facettes qu'il a lui-meme extraits, puis imposer seulement des invariants mecaniques generiques (libelles non perdus/non dupliques, premiere colonne informative, decision de couverture accompagnee d'une strategie executable). Rejouer d'abord les regressions deterministes, puis le live canonique.
- Le planning professionnel reste non valide. Les trois repetitions live, les reformulations, la question RAG simple, la memoire, les cartes UI/WinUI, le nettoyage prudent et les commits par lots restent ouverts.

### 2026-07-18 - Quatrieme live : omission totale de structure et faux succes mecanique simple

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-093009/`, trace `rag-20260718093040513-59b4dc1b`, journaux processus `artifacts/live-test-process-20260718-113003/`. Le test produit echoue apres environ 4 min 28 s sur l'absence de `Diner`; le runtime local est arrete proprement.
- Le Planner initial a termine en 56,9 s avec un prompt de 6 761 caracteres, mais son intake effectif etait entierement non structure : `taskKind=rag.answer`, `questionFocus=repas semaine`, zero axe et zero `rowHeaderLabel`.
- Comme l'audit ajoute apres le troisieme live ciblait une grille dont les deux axes existaient mais dont l'un avait moins de deux valeurs, cette omission totale n'a declenche ni `PlannerIntakeReview` ni `PlannerDiversityRepair`.
- Une unique recherche `plan de repas semaine lundi au vendredi` dans `Cuisine` a retourne 9 hits en 13,6 secondes, issus de neuf pages/documents. Le backend et le budget RAG ne sont toujours pas la cause de l'echec.
- Le Judge a retourne `answer` avec zero id et trois lacunes explicites : collation, souper et diner. Le Status a ensuite choisi `write_partial` avec `E1` et `E5`; la selection depuis la memoire de travail LLM a ete acceptee.
- Le Writer a rendu une phrase d'introduction de 180 caracteres annoncant un plan, sans aucun repas ni tableau. Le verificateur source a confirme seulement la coherence des deux ids cites et a donc retourne `valid=True`; sans intake structure, les contrats de vingt cellules ne pouvaient pas s'activer.
- Le payload final expose deux sources reelles, mais il ne constitue pas une reponse utile. Ce live prouve qu'une citation valide ne suffit pas a garantir la realisation de la forme explicitement demandee lorsque l'intake LLM a omis cette forme.
- Correction appliquee : le Planner initial doit maintenant produire `structureReviewDecision`, `structureReviewKind` et `structureReviewReason`. Si son intakeDecision existe mais que les axes sont vides et que cette auto-revue manque, la passe LLM focalisee est obligatoire. Les reparations Planner portent le meme contrat.
- Regression ajoutee : intake de depart sans axes, premier Planner `rag.answer` avec axes vides et recherche large, audit LLM restaurant cinq lignes et quatre colonnes, puis strategie LLM a quatre recherches. Le code ne detecte aucun mot repas/jour; il controle seulement la presence et la coherence du contrat LLM.
- Validation deterministe apres compatibilite des anciens contrats non tabulaires : build 0 avertissement/0 erreur, suite SourceBacked 278/278, couverture etendue 357/357. Le prompt Planner reste sous son budget et conserve l'instruction explicite de verifier la demande avant d'accepter `non_tabular`.
- Prochaine preuve discriminante : cinquieme live. La premiere trace doit montrer soit un auto-audit Planner `two_axis_grid` correct, soit `planner_intake_review` restaurant 20 cellules avant toute recherche.

### 2026-07-18 - Cinquieme live : grille 5 x 4 restauree, strategie et action encore insuffisantes

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-095136/`, trace `rag-20260718095210755-73d67303`, journaux processus `artifacts/live-test-process-20260718-115131/`. Le test se termine proprement apres environ 9 min 55 s avec une insuffisance de 158 caracteres et zero carte source.
- Le garde-fou issu du quatrieme live fonctionne sur le modele reel : `PlannerIntakeReview` se declenche avant tout outil. Sa premiere sortie `two_axis_grid` est refusee car elle ne contient pas assez de lignes/colonnes; le retry LLM est accepte.
- L'intake effectif restaure exactement vingt cellules : lignes `Lundi, Mardi, Mercredi, Jeudi, Vendredi`; colonnes `Petit-dejeuner, Diner, Souper, Collation`. Le `rowHeaderLabel=Ligne` reste semantiquement mediocre mais ne supprime plus la structure.
- La revue de strategie LLM declare `queryCoverageDecision=row_independent_candidate_pool`, mais une seule recherche large est acceptee : `Plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner,...` dans `Cuisine`.
- La recuperation n'est pas en panne : 12 hits, 12 preuves canoniques, une tentative, zero timeout et zero degradation; la recherche dure 17,5 secondes.
- Le juge choisit `need_more_evidence`, zero preuve et zero action, avec la lacune `Additional recipes are needed for each meal type.` La premiere reparation d'action puis `EvidenceJudgeActionContractRetry` ne produisent toujours aucune requete executable; arret honnete au premier tour.
- Le prochain correctif doit rendre observable et coherent le lien LLM entre `row_independent_candidate_pool`, les quatre facettes de colonnes et les requetes annoncees. Le code ne doit choisir ni vocabulaire, ni aliment, ni outil; il doit seulement refuser une declaration de couverture dont les cardinalites/assignations typees sont incompletes.
- Le contrat d'action doit aussi faire declarer au LLM une seule action typee avec motif de validite; en cas de sortie non executable, la trace doit conserver les problemes mecaniques exacts afin de distinguer JSON incomplet, outil absent, requete vide, doublon ou categorie invalide.

### 2026-07-18 - Corrections deterministes avant le sixieme live

- Le relecteur de strategie LLM doit maintenant declarer `columnCoverageDecision=facet_specific|shared_candidate_family` et expliquer sa decision dans `columnCoverageReason`; le code ne choisit aucune facette et ne fait qu'en verifier le contrat type.
- En mode `facet_specific`, chaque requete porte `targetFacetExact`, doit viser exactement une colonne declaree par l'intake LLM et l'ensemble des requetes doit couvrir toutes les colonnes dans le budget maximal de quatre appels.
- En mode `shared_candidate_family`, aucune affectation de facette n'est autorisee et une requete qui recopie plusieurs libelles de colonnes types est refusee. Le controle repose uniquement sur les libelles produits par le LLM, apres normalisation generique des accents et de la ponctuation; il ne contient aucun vocabulaire cuisine.
- La seconde reparation d'action utilise maintenant un contrat JSON plat `actionDecision=execute|clarify` avec une seule requete eventuelle. Le parseur et le validateur conservent la decision semantique du LLM, puis controlent seulement outil connu, requete executable, categorie autorisee et absence de repetition d'une tentative deja faite.
- Les problemes mecaniques exacts de ce dernier contrat sont traces dans `llm_action_contract_retry_problems`; aucune requete de remplacement n'est inventee par le code.
- Regression discriminante ajoutee : un relecteur LLM qui accepte une requete copiant les quatre colonnes est refuse avec `query_copies_multiple_typed_columns`; le retry LLM rend ensuite quatre requetes avec les quatre `targetFacetExact`, qui sont bien celles executees.
- Validation obtenue avant le sixieme live : build 0 avertissement/0 erreur, suite SourceBacked 279/279, couverture etendue SourceBacked/OpenAI/ApiClient 358/358.
- Prochaine preuve discriminante : sixieme live canonique. La trace doit conserver la grille 5 x 4 avant les outils, puis montrer soit quatre affectations de facettes exactes, soit une famille partagee qui ne recopie pas les colonnes. Si le juge demande encore des preuves, le contrat d'action plat doit produire une action executable ou une clarification explicite et tracee.

### 2026-07-18 - Sixieme live : structure conservee, deux strategies non executables refusees

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-103611/`, trace `rag-20260718103650107-2cde0226`, journaux processus `artifacts/live-test-process-20260718-123604/`. Le run se termine proprement en environ 4 min 32 s, avec une insuffisance de 298 caracteres et zero carte source.
- Le chemin structure reste correct avant tout outil : le premier audit LLM `two_axis_grid` est refuse pour lignes, colonnes et libelle de ligne incomplets; son retry restaure cinq lignes et quatre colonnes, soit vingt cellules.
- La premiere revue de strategie annonce une approbation LLM mais produit quatre requetes identiques. La suppression mecanique des doublons exacts n'en conserve qu'une et le contrat refuse l'execution avec `uncovered_typed_columns: Diner, Souper, Collation`.
- Le retry LLM produit ensuite quatre requetes distinctes, une pour chaque creneau, mais omet les quatre `targetFacetExact`. Le contrat refuse a nouveau avec `missing_target_facet_assignments` et `uncovered_typed_columns` au lieu d'inferer les affectations depuis le texte des requetes.
- Aucun outil documentaire n'est appele, aucun resultat ambigu n'est expose et le terminal reste honnete. Le correctif de couverture fonctionne donc comme porte de securite, mais le petit modele local ne respecte pas encore de maniere fiable le champ d'affectation dans le prompt general de strategie.
- Prochaine correction discriminante : ajouter une reparation LLM focalisee et compacte qui ne choisit ni contenu ni outil, mais doit recopier les requetes candidates et leur affecter exactement les libelles types fournis. Le code continuera seulement a verifier cardinalite, valeurs exactes, couverture complete, executabilite et absence de doublon.
- Correction appliquee : `PlannerFacetAssignmentRepair` recoit les requetes immuables, leurs indices et les libelles types exacts, puis le LLM rend uniquement `assignmentDecision`, `reason` et les couples `requestIndex -> targetFacetExact`. Le code refuse toute valeur inconnue, indice absent/duplique, cardinalite incoherente ou couverture incomplete; il ne lit pas les mots des requetes pour deviner une facette.
- Regression reproduisant le sixieme live ajoutee et verte : quatre requetes distinctes sans affectation declenchent la passe focalisee, les quatre decisions LLM sont appliquees sans reecriture, puis le plan est execute. Prompt focalise inferieur a 4 000 caracteres.
- Validation apres correction : build mono-noeud 0 avertissement/0 erreur, suite SourceBacked 281/281, couverture etendue SourceBacked/OpenAI/ApiClient 360/360.
- Prochaine preuve discriminante : septieme live canonique; la trace doit montrer `SBRAG_PLANNER_FACET_ASSIGNMENT_REPAIR accepted=true`, quatre affectations exactes, puis au moins une tentative documentaire reelle.

### 2026-07-18 - Septieme live : passe focalisee activee, JSON d'affectation incomplet

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-105610/`, trace `rag-20260718105641496-f01b9b76`, journaux processus `artifacts/live-test-process-20260718-125605/`. Le run se termine proprement apres environ 4 min 57 s de pipeline actif, avec une insuffisance de 298 caracteres et zero carte source.
- L'intake suit le chemin attendu : premier audit `two_axis_grid` refuse, retry accepte avec cinq lignes et quatre colonnes. Le Planner initial prend 73,8 s, l'audit 47,3 s et son retry 26,2 s.
- La premiere strategie repete quatre fois la meme requete et est refusee apres suppression de trois doublons exacts. Le retry produit quatre requetes distinctes sans `targetFacetExact`, exactement comme au sixieme live.
- La nouvelle passe `planner_facet_assignment_repair` se declenche bien avec un prompt de seulement 1 964 caracteres et repond en 11,2 s. Sa sortie de 313 caracteres ne contient toutefois pas un objet JSON complet; le parseur retourne `parsed=false` et aucun outil n'est execute.
- Le serveur local confirme que la generation n'a pas ete tronquee par la fenetre de contexte (`truncated=0`) : le petit modele a termine volontairement une sortie de format invalide. Une simple augmentation de budget ne reglerait donc pas le probleme prouve.
- Prochaine correction discriminante : reduire encore le contrat de sortie vers une table JSON compacte d'indices, accepter mecaniquement l'ancien tableau pour compatibilite, puis donner au LLM une unique reparation de format focalisee si le premier objet reste invalide. Aucune association ne sera deduite par le code.
- Correction appliquee : la forme prioritaire est maintenant `assignments={"1":"LIBELLE_EXACT",...}`; le parseur conserve aussi l'ancien tableau d'objets. Si le premier objet est incomplet, `PlannerFacetAssignmentFormatRepair` redemande une seule fois la meme decision dans ce format compact, sans reinjecter ni interpreter la sortie invalide.
- Regression et compatibilite validees : premiere sortie volontairement tronquee, seconde sortie compacte acceptee, quatre affectations exactes executees; ancien tableau toujours lisible; prompts d'affectation et de format chacun sous 4 000 caracteres.
- Validation avant le huitieme live : build 0 avertissement/0 erreur, suite SourceBacked 281/281 et couverture etendue SourceBacked/OpenAI/ApiClient 360/360.

### 2026-07-18 - Huitieme live : affectations et action LLM prouvees, requetes faibles puis double timeout Writer

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-112008/`, trace `rag-20260718112039171-9e2d5c30`, journaux processus `artifacts/live-test-process-20260718-132004/`. Le test atteint exactement son budget global de 24 minutes et se termine par `OperationCanceledException`; aucun fichier de reponse, aucun planning et aucune carte source ne sont produits. Le runtime local est arrete proprement.
- L'audit d'intake conserve le progres acquis : la premiere revue `two_axis_grid` est refusee, puis le retry LLM restaure les cinq jours et les quatre creneaux avant tout outil.
- La premiere reparation de strategie dure 130,1 s et reste invalide : elle renvoie notamment l'affectation inconnue `Déjeune` et oublie `Collation`. Le retry suivant dure 88,6 s et produit quatre requetes distinctes, mais sans `targetFacetExact`.
- La nouvelle passe focalisee ferme sa porte en conditions reelles. `PlannerFacetAssignmentRepair` utilise un prompt de 2 100 caracteres, repond en 13,2 s avec le format compact et est acceptee au premier appel : `1:Petit-dejeuner`, `2:Diner`, `3:Souper`, `4:Collation`. Aucune affectation n'est inferee par le code.
- Les quatre recherches sont executees sans timeout RAG : 10 hits en 22,4 s pour `Petit-dejeuner`, puis un hit en environ 7,7 s pour chacune des trois autres facettes. La disponibilite technique du backend est donc confirmee, mais les requetes `... sources for Lundi in Cuisine` sont semantiquement faibles.
- L'inspection des resultats confirme cette faiblesse : le hit `Diner` est une page de services communautaires, le hit `Souper` une introduction de livre de recettes et le hit `Collation` une recommandation generale. Ces pages ne constituent pas une banque de vingt propositions concretement ecrivables.
- Le premier `EvidenceStatusReview` conclut `read_documents`, avec trois preuves utiles, cinq faibles et une lacune. La reparation de selection aboutit, mais son prompt remonte a 12 639 caracteres : il ne provoque pas de HTTP 400 sur ce run, tout en depassant le plafond de compaction vise et en consommant 81,8 s.
- Le contrat plat d'action est lui aussi prouve en live. Apres une premiere reparation, `EvidenceJudgeActionContractRetry` rend `actionDecision=execute` avec `rag.multi_search` et la requete `sources de repas pour lundi au vendredi en français`. L'outil s'execute en 13,8 s, ramene six hits, six documents/pages distincts et zero degradation.
- Apres ce complement, le second statut choisit honnetement `write_partial` avec seulement deux preuves utiles, une faible et une lacune. Le pipeline atteint donc le Writer avec un corpus encore trop pauvre pour soutenir vingt cellules.
- `structured_writer` expire a son plafond de 210 s sans JSON exploitable. `structured_cell_repair` expire a son tour apres 210 s. Le verificateur conserve `empty_answer`, `missing_citations` et `requested_structure_not_realized`; aucune sortie partielle n'est publiee.
- Une ultime reparation d'action se termine en 68,2 s avec une sortie de 905 caracteres, puis son contrat structure est annule apres 6,4 s par le budget global. Ce run depense ainsi plus de sept minutes dans deux generations structurees consecutives alors que le statut LLM avait deja declare des preuves insuffisantes.
- Diagnostic principal : les deux nouveaux contrats LLM focalises fonctionnent reellement, mais le Planner general continue de fabriquer des requetes peu porteuses de contenu et le chemin Writer/reparation est trop couteux quand le bundle ne permet pas une table complete. Le prochain correctif ne doit pas ajouter de vocabulaire cuisine au code.
- Prochaine correction discriminante : faire revoir par un LLM focalise la qualite documentaire de chaque requete immuable ou proposer une strategie compacte de remplacement avant la premiere recherche, avec un contrat semantique `answer-bearing` et des controles mecaniques generiques. En parallele, eviter deux generations de vingt cellules lorsque le statut reconnait explicitement une couverture insuffisante, et remettre la reparation de selection sous budget.
- Le planning professionnel reste non valide. Il faut encore obtenir vingt cellules semantiquement soutenues, puis trois lives coherents, une question RAG simple, la memoire, les cartes de sources, le parcours WinUI, le nettoyage prudent et les commits par lots.

### 2026-07-18 - Revue LLM focalisee des facettes et de la qualite des requetes avant le neuvieme live

- L'ancien `PlannerFacetAssignmentRepair` a ete remplace par `PlannerFacetQueryReview`. La cause du huitieme live n'etait plus seulement l'absence de `targetFacetExact` : les quatre requetes immuables acceptees contenaient encore la structure de sortie, un jour et des mots generiques au lieu de viser des passages porteurs de candidats concrets.
- La nouvelle passe recoit la question, la decision de couverture, les facettes exactes, les requetes candidates et les problemes mecaniques. Le LLM doit rendre pour chaque indice `targetFacetExact`, une requete finale et son objectif, puis declarer explicitement `reviewDecision=accept`.
- Le contrat explique de maniere generique la notion de requete `answer-bearing` : les passages trouves doivent pouvoir fournir directement des valeurs ou faits concrets, pas seulement parler de la mise en page ou du depot. Aucun aliment, vocabulaire cuisine, categorie de remplacement ou requete de secours n'est code en dur.
- `toolName` et `categoryPath` restent immuables pendant cette passe. Le code valide uniquement la decision, la raison, les indices, la couverture exacte des facettes, les champs non vides, la limite de 140 caracteres, puis reutilise les controles existants d'executabilite, de doublons, de categorie et de budget.
- Le format prioritaire est un objet compact indexe. Le parseur accepte aussi un tableau d'objets pour compatibilite; une seule reparation de format est disponible si le JSON initial est incomplet.
- La passe est volontairement bornee au cas prouve : un plan `facet_specific` deja approuve par le LLM, avec exactement une requete par facette, mais rejete uniquement pour affectations manquantes, inconnues ou incompletes. Elle ne rajoute aucun appel aux plans deja complets et ne tente pas de masquer les autres problemes mecaniques.
- L'ordre a ete optimise : cette revue focalisee est tentee des le premier candidat eligible, avant le second gros retry general. Sur une sortie analogue au huitieme live (`Déjeune` inconnu et `Collation` non couverte), elle peut donc corriger affectations et requetes sans depenser les 88,6 secondes du retry general.
- Regression discriminante : quatre requetes distinctes sans affectation sont remplacees par quatre requetes source-domain choisies par le LLM; l'outil et le scope sont conserves; les quatre facettes exactes sont executees. Une premiere sortie JSON volontairement incomplete declenche la reparation compacte.
- L'ancien parseur, les anciens prompts et l'ancien pipeline d'affectation seule ont ete supprimes apres migration afin de ne conserver qu'un chemin actif.
- Validation finale du lot : build mono-processus 0 avertissement/0 erreur; suite SourceBacked 281/281; couverture etendue SourceBacked/OpenAI/ApiClient 360/360; `git diff --check` sans erreur sur les fichiers du lot.
- Prochaine preuve discriminante : neuvieme live canonique. Si le premier relecteur reproduit ses affectations invalides, la trace doit passer directement par `planner_facet_query_review`, produire quatre requetes sans jour ni transcription de grille, puis fournir des candidats concretement ecrivables avant le Status et le Writer.

### 2026-07-18 - Neuvieme live : grille restauree, deux timeouts Planner avant toute requete candidate

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-120939/`, trace `rag-20260718121014982-2df1f271`, trace SourceBacked `sbrag-61928edeef744fe8a670e8171ad93286`, journaux processus `artifacts/live-test-process-20260718-140932/`. Le pipeline termine en 10 min 05 s et le test complet en 10 min 40 s avec une reponse de securite de 298 caracteres, zero outil et zero source.
- Le routeur termine normalement en 17,8 s. Le Planner initial prend 95,5 s et rend 1 145 caracteres.
- L'audit de structure reste fonctionnel mais exceptionnellement lent : premiere revue refusee apres 93,7 s, puis retry accepte apres 94,9 s avec les cinq jours, les quatre creneaux et `rowHeaderLabel=Jour`.
- Le premier relecteur general de strategie, prompt de 6 872 caracteres, expire exactement a 150 s. Sa sortie de timeout ne contient aucune requete candidate parsable; le contrat la refuse sans execution.
- Le second relecteur general, prompt de 6 993 caracteres, expire lui aussi a 150 s et ne livre aucune requete candidate. `PlannerFacetQueryReview` ne peut donc pas se declencher : son entree exige quatre requetes indexees dont elle puisse conserver l'outil et le scope.
- Le journal `llama-server` confirme deux annulations, sans troncature de contexte. Le modele avait traite les prompts et generait encore tres lentement, autour de 2,3 tokens/s sur la fin; il n'a pas livre le JSON general avant les plafonds.
- La reponse terminale reste honnete et explique que le Planner n'a produit aucune action executable. Aucune requete partielle, aucune source trompeuse et aucun Writer ne sont executes.
- Le correctif post-huitieme live n'est pas invalide, mais il n'a pas ete exerce par ce run. Le nouveau cas discriminant est l'absence totale de candidat apres timeout des deux gros relecteurs.
- Prochaine correction : ajouter un Planner LLM compact de dernier recours pour une grille deja auditee. Il doit recevoir uniquement les facettes exactes, le catalogue borne, les contraintes de couverture et, si disponible, le scope du plan initial; il choisit lui-meme le scope et produit directement les quatre requetes answer-bearing dans un JSON court. Le code ne doit generer ni vocabulaire, ni categorie, ni requete.
- La reduction d'appels devient prioritaire : deux relecteurs generaux de 150 s ne doivent plus etre l'unique chemin vers une petite decision de quatre requetes typees.

### 2026-07-18 - Planner compact LLM valide avant le dixieme live

- Le nouveau `PlannerCompactFacetPlan` cible exactement le blocage du neuvieme live : il n'est eligible que pour une grille structuree d'au moins huit cellules, deux a quatre facettes typees et un premier candidat de strategie sans aucune requete executable.
- Il est tente immediatement apres le premier relecteur general vide, avant le second appel general de 150 secondes. Un seul essai compact est autorise par preparation de plan; si son contrat echoue, le retry general historique reste disponible.
- Le prompt est volontairement focalise et borne. Il contient l'objectif, les contraintes, les facettes et indices exacts, les lignes marquees comme cibles de placement et non comme texte de recherche, les scopes initiaux du LLM et les chemins catalogue disponibles.
- Le LLM conserve toutes les decisions semantiques : `planDecision`, strategie lignes reutilisables ou preuves propres a chaque ligne, justification de couverture, scope partage, outil, requete, objectif et facette exacte de chaque action.
- Les requetes doivent viser des passages `answer-bearing` avec du vocabulaire du domaine source. Aucun vocabulaire cuisine, aliment, categorie de repli ou requete n'est fabrique par le code.
- Le code ne controle que le contrat mecanique : decision approuvee, raisons non vides, chemin catalogue exact ou nul, quatre indices et facettes uniques, outils autorises, champs non vides, requetes de 140 caracteres maximum, executabilite, doublons et budget existant.
- Le format prioritaire est un objet JSON indexe; l'ancien tableau d'objets reste parse pour compatibilite. Une unique reparation de format compacte est disponible si le premier objet est invalide.
- La regression discriminante reproduit la sortie de timeout du neuvieme live : le grand relecteur rend un plan vide, puis le LLM compact choisit `Cuisine`, quatre requetes distinctes et les quatre facettes exactes. Elles sont executees sans appeler `PlannerDiversityRetry`.
- Validation finale de ce palier : build monoprocessus reussi avec 0 avertissement et 0 erreur; 3/3 tests cibles; 283/283 tests SourceBacked; 362/362 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur d'espacement.
- Prochaine preuve discriminante : dixieme live canonique 5 x 4. Apres le premier timeout general, la trace doit montrer `planner_compact_facet_plan`, quatre requetes choisies par le LLM et une execution documentaire reelle, sans payer le second timeout general de 150 secondes.

### 2026-07-18 - Dixieme live : revue focalisee rapide mais clarification sans plan

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-124417/`, trace principale `rag-20260718124450159-34c998c3`, trace SourceBacked `sbrag-3e054d1e324e44048be0cfee4b4955dc`, journaux processus `artifacts/live-test-process-20260718-144411/`. Le test termine proprement en 8 min 53 s avec une insuffisance de 298 caracteres, zero outil et zero source.
- Le routeur prend 39,0 s. Le Planner initial termine en 101,7 s avec 1 309 caracteres. L'audit d'intake refuse sa premiere interpretation apres 69,4 s, puis accepte le retry apres 85,7 s avec cinq jours et quatre creneaux.
- Le premier relecteur general ne timeoute pas. Apres 108,5 s, il approuve quatre copies exactes de la meme requete qui transcrit toute la grille. La suppression mecanique en conserve une; le contrat refuse `query_copies_multiple_typed_columns` et trois facettes non couvertes.
- Le Planner compact n'est pas eligible a ce stade car le candidat conserve une requete. Le retry general historique est donc appele et termine en 71,0 s.
- Ce retry produit quatre requetes distinctes mais sans `targetFacetExact`, et recopie encore un jour dans chacune : `Petit-dejeuner sources for Lundi`, `Diner sources for Mardi`, `Souper sources for Mercredi`, `Collation sources for Jeudi`.
- `PlannerFacetQueryReview` se declenche correctement avec un prompt de 3 383 caracteres et repond rapidement en 21,0 s. Le LLM renvoie cependant `reviewDecision=clarify`, aucune raison et conserve les termes faibles `sources for` avec les jours.
- Le contrat mecanique refuse cette sortie avec `facet_query_review_not_approved` et `missing_facet_query_review_reason`. Aucun outil n'est execute et aucune reponse non sourcee n'est montree.
- Le nouveau Planner compact n'est pas appele apres cet echec car sa premiere condition exige encore zero requete. Le live prouve donc une seconde forme de meme blocage : quatre candidats existent, mais la revue focalisee les refuse sans proposer de remplacement executable.
- Prochaine correction precise : autoriser le Planner compact, sans changer son pouvoir semantique, apres un echec explicite de `PlannerFacetQueryReview`. Les retry generaux deja acceptes restent inchanges; le code ne deduit toujours ni scope, ni facette, ni vocabulaire.

### 2026-07-18 - Seconde opinion compacte apres refus de la revue focalisee

- `PlannerCompactFacetPlan` devient eligible dans deux cas mecaniques et traces : aucun candidat general n'existe, ou `PlannerFacetQueryReview` a explicitement echoue son contrat. Il ne remplace pas un plan accepte et ne s'insere pas apres un retry general deja valide.
- La condition ne cherche aucun terme dans les requetes et ne juge pas leur pertinence. Elle reconnait seulement l'issue structuree `facet_query_review` puis confie une nouvelle decision semantique complete au LLM compact.
- La regression reproduit toute la sequence du dixieme live : quatre doublons de grille au premier relecteur, quatre requetes `sources for [jour]` sans facette au retry, `reviewDecision=clarify` sans raison au relecteur focalise, puis plan compact approuve avec quatre requetes answer-bearing.
- Le plan execute provient exclusivement de la derniere sortie LLM : categorie partagee, outils, termes, objectifs et facettes exactes. Le code conserve les memes verifications de catalogue, cardinalite, unicite, executabilite et budget.
- Validation finale : build monoprocessus avec 0 avertissement et 0 erreur; 3/3 regressions ciblees; 284/284 tests SourceBacked; 363/363 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur d'espacement.
- Prochaine preuve discriminante : onzieme live canonique. Si le chemin du dixieme essai se reproduit, la trace doit enchainer `planner_facet_query_review` refuse puis `planner_compact_facet_plan` accepte et executer quatre recherches documentaires.

### 2026-07-18 - Onzieme live : cinquieme colonne inventee par l'audit d'intake

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-130253/`, trace principale `rag-20260718130324081-056a8f10`, trace SourceBacked `sbrag-8ff9c87579d046479ee6ec186bc169bb`, journaux processus `artifacts/live-test-process-20260718-150249/`. Le test termine proprement en 7 min 42 s avec l'insuffisance de securite, zero outil et zero source.
- Le routeur termine en 28,4 s et le Planner initial en 96,9 s. L'audit d'intake accepte cette fois sa premiere sortie en 56,5 s, ce qui evite le retry structurel du live precedent.
- Cette acceptation est cependant semantiquement fausse. La trace effective contient cinq colonnes : `Petit-dejeuner`, `Dejeuner`, `Diner`, `Souper`, `Collation`. La demande utilisateur n'exige que `Petit-dejeuner`, `Diner`, `Souper` et l'alternative unique `gouter / collation`; `Dejeuner` a ete invente par le LLM.
- Le premier relecteur general expire a 150 s et rend zero requete. Le Planner compact ne se declenche pas car son contrat volontaire exige deux a quatre facettes afin de rester dans le budget maximal de quatre actions.
- Le retry general termine apres 98,6 s avec 2 369 caracteres non parsables. Le pipeline refuse l'execution et conserve la reponse terminale honnete.
- La seconde opinion compacte apres `PlannerFacetQueryReview` n'est pas en cause : cette branche n'est jamais atteinte. Le blocage se situe un niveau avant, dans l'acceptation d'une structure qui ajoute un attribut absent de la question.
- Prochaine correction : renforcer le contrat LLM de `PlannerIntakeReview` pour exiger une justification d'ancrage a la demande pour chaque ligne et colonne, traiter les formulations alternatives comme un seul besoin lorsque le LLM le decide, et interdire explicitement tout libelle seulement plausible mais absent. Le code doit verifier la forme et la tracabilite declaree, sans dictionnaire de repas ni synonymes.

### 2026-07-18 - Intake LLM ancre a la question avant le douzieme live

- `PlannerIntakeReview` doit maintenant rendre `structureAnchorGroups`. Chaque groupe declare l'axe, les libelles exacts qu'il justifie, une citation courte copiee de `USER_QUESTION` et la relation semantique choisie par le LLM.
- Les trois relations generiques sont `exact`, `range_expansion` et `alternative_group`. Le LLM utilise une valeur exacte pour un libelle ecrit, une plage pour plusieurs membres explicites et une alternative pour un seul besoin exprime par slash, `or` ou alias.
- Le prompt interdit d'utiliser plage ou alternative pour justifier une valeur seulement plausible dans le domaine. Si un libelle n'a pas d'ancrage fidele, le LLM doit le retirer ou demander une clarification.
- Le code ne connait aucun repas ni synonyme. Il verifie seulement que les citations apparaissent dans la question, que chaque ligne et colonne est couverte exactement une fois, que les relations respectent leur cardinalite et que les ancrages `exact` correspondent a leur libelle apres normalisation technique.
- Une affectation non chevauchante des occurrences exactes est exigee. Ainsi `Petit-dejeuner -> petit-dejeuner` et `Dejeuner -> dejeuner` ne peuvent pas exploiter le meme fragment imbrique; deux occurrences reellement distinctes resteraient acceptables.
- Les plages peuvent couvrir plusieurs lignes depuis une meme expression comme `lundi au vendredi`. Une alternative peut representer un seul libelle depuis une expression comme `gouter / collation`; cette interpretation reste produite par le LLM, pas par le code.
- Le parseur accepte une liste de groupes et une forme objet compacte. Les ancrages sont traces avec l'issue de l'audit afin que les prochains lives expliquent chaque axe accepte ou refuse.
- Regression live : un premier audit ajoute `Dejeuner` et tente de l'ancrer dans `petit-dejeuner`; il est refuse pour chevauchement. Le retry LLM rend quatre colonnes et cinq jours correctement ancres, puis le plan de recherche est execute.
- Validation : build monoprocessus 0 avertissement/0 erreur; 3/3 tests cibles; 284/284 tests SourceBacked; 363/363 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur d'espacement.
- Prochaine preuve discriminante : douzieme live canonique. L'audit doit soit rendre directement quatre colonnes ancrees, soit refuser une cinquieme colonne et la corriger au retry; le Planner compact doit ensuite pouvoir s'activer sur quatre facettes.

### 2026-07-18 - Douzieme live : garde d'ancrage actif, retry LLM repete le mauvais format

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-132458/`, trace principale `rag-20260718132528825-3d2a0910`, trace SourceBacked `sbrag-507262b9b1fc4c4090e5dcd7cc7430bf`, journaux processus `artifacts/live-test-process-20260718-152454/`. Le pipeline termine en environ 5 min 08 s avec une insuffisance de 281 caracteres, zero outil et zero source.
- Le routeur termine en 18,3 s et le Planner initial en 121,9 s. Le prompt du nouvel audit ancre mesure 4 621 caracteres, sous la fenetre locale.
- Le premier audit rend encore cinq colonnes, dont `Dejeuner`, et seulement deux groupes : toutes les lignes dans un groupe `exact` cite `Lundi | Mardi | Mercredi | Jeudi | Vendredi`, puis toutes les colonnes dans un groupe `exact` analogue.
- Le nouveau garde refuse correctement cette sortie. Les citations jointes par `|` n'existent pas dans la question et une relation `exact` ne peut couvrir plusieurs libelles. La trace expose les groupes et les problemes precis.
- Le retry recoit le rapport mecanique dans un prompt de 5 065 caracteres et termine en 73,2 s. Il repete pourtant exactement les memes deux groupes invalides et conserve la cinquieme colonne.
- Le pipeline rejette donc l'intake apres deux tentatives. Le Planner compact et les outils ne sont jamais atteints; aucune structure non ancree n'est executee.
- Le contrat de provenance est valide par le live, mais le second appel est trop semblable au premier pour le petit modele. Une simple repetition du grand audit ne repare pas son schema mental.
- Prochaine correction : remplacer le retry general d'intake par `PlannerIntakeAnchorRepair`, une micro-passe LLM dediee. Elle recevra uniquement la question, les labels refuses et les problemes, avec un gabarit explicite : un groupe par valeur exacte, un groupe multi-labels seulement pour une plage et un groupe mono-label pour une alternative. Elle pourra retirer tout label non ancre et devra rendre le nouvel intake complet; le code gardera les memes controles mecaniques.

### 2026-07-18 - Micro-reparation LLM des ancrages prete pour le treizieme live

- La seconde tentative d'audit n'utilise plus le meme grand prompt. Elle appelle maintenant l'evenement dedie `PlannerIntakeAnchorRepair` avec la question, les lignes et colonnes refusees, le libelle de ligne, les contraintes et le rapport mecanique.
- Le prompt exige explicitement un groupe separe pour chaque valeur `exact` et interdit de joindre des citations par `|`, virgules ou prose. Une plage reste le seul cas multi-labels; une alternative reste mono-label.
- Le LLM doit retirer tout libelle dont la seule citation est imbriquee dans un autre libelle exact, sauf occurrence distincte dans la question. Il rend encore le nouvel intake complet, la decision et les relations; le code ne corrige aucun libelle.
- La micro-passe remplace uniquement le retry apres rejet. Le premier audit semantique complet, les plans deja acceptes et les chemins non tabulaires restent inchanges.
- La regression du douzieme live conserve un premier audit a cinq colonnes refuse pour chevauchement, puis fait rendre a `PlannerIntakeAnchorRepair` cinq jours, quatre colonnes et cinq groupes valides. La strategie de recherche execute ensuite quatre requetes.
- Validation finale : build monoprocessus 0 avertissement/0 erreur; 3/3 tests cibles; 284/284 tests SourceBacked; 363/363 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur; fichiers principaux a 479 et 134 lignes.
- Prochaine preuve discriminante : treizieme live canonique. Apres un premier audit invalide, la trace doit montrer `planner_intake_anchor_repair`, quatre colonnes ancrees, puis `planner_compact_facet_plan` si la strategie generale ne rend aucun candidat.

### 2026-07-18 - Treizieme live : micro-passe atteinte, schema encore trop riche

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-134408/`, trace principale `rag-20260718134449498-9db80b1d`, trace SourceBacked `sbrag-5564045f5b574bc3b9794ff3af05130f`, journaux processus `artifacts/live-test-process-20260718-154400/`. Le pipeline termine en environ 4 min 42 s avec une insuffisance de 281 caracteres, zero outil et zero source.
- Le routeur termine en 15,9 s et le Planner en 81,3 s. Le premier audit ancre prend 91,8 s, rend encore deux groupes `exact` concatenees et ne fournit pas d'axes exploitables dans `intakeDecision`; il est refuse.
- La nouvelle branche est prouvee : `planner_intake_anchor_repair` demarre avec seulement 2 670 caracteres, contre 4 621 pour l'audit complet, et termine normalement en 91,9 s sans timeout ni troncature.
- Sa sortie reste toutefois incompatible avec le contrat : `structureReviewDecision` et `structureReviewKind` absents, groupes sans `axis` ni `relation`, libelles minuscules isoles et citations beaucoup trop longues recopiees depuis la question.
- Le code refuse correctement l'approbation manquante, les axes invalides, les libelles inconnus, les relations absentes et les citations non conformes. Aucune tentative RAG n'est lancee.
- Le serveur confirme que la generation de 407 tokens a termine volontairement avec `truncated=0`. Augmenter le temps ou la sortie ne corrigera pas ce schema mental.
- Diagnostic : meme allege, le contrat demande encore au 3B de repeter `axis`, `labelsExact`, `anchorQuote`, `relation` dans chaque objet et de reconstruire en plus tout `intakeDecision`. Il change les noms de champs et perd les metadonnees globales.
- Prochaine correction : reduire la micro-sortie a un mapping type. `rowAnchors` et `columnAnchors` seront des objets dont les cles sont directement les labels finaux et chaque valeur contient seulement `quote` et `relation`. Les cles definiront les axes et les labels sans inference; le code regroupera mecaniquement les membres partageant la meme plage. Le LLM gardera `decision`, `kind`, `reason`, `rowHeaderLabel` et `needsClarification`, tandis que les autres champs d'intake deja connus seront preserves.

### 2026-07-18 - Mapping d'ancrages minimal valide avant le quatorzieme live

- `PlannerIntakeAnchorRepair` rend maintenant uniquement `decision`, `kind`, `reason`, `rowHeaderLabel`, `needsClarification`, `rowAnchors` et `columnAnchors`.
- Les deux collections sont des objets JSON. Chaque cle est directement un libelle final choisi par le LLM; chaque valeur contient seulement `quote` et `relation`. Il n'y a plus d'`axis`, `labelsExact` ou `intakeDecision` repetes.
- L'axe provient mecaniquement de la collection et le libelle de sa cle. Le code ne renomme, ne fusionne et n'ajoute aucune cle.
- Pour une plage, le LLM repete la meme citation et `range_expansion` sous chaque membre final. Le code regroupe ensuite seulement les entrees dont axe, relation et citation sont strictement identiques avant d'appliquer le contrat canonique multi-labels.
- Les metadonnees non structurelles deja presentes dans l'intake rejete sont preservees. Si le premier audit a perdu tous ses axes, la micro-passe repart mecaniquement de l'intake propose par le Planner plutot que de propager une coquille vide.
- Le prompt contient un exemple de forme generique, insiste sur les cles finales, interdit les citations jointes et retire les champs non necessaires. Le LLM conserve la decision, la nature de structure, les libelles, les citations et relations.
- Regression mise a jour : `rowAnchors` contient cinq cles partageant `lundi au vendredi`; `columnAnchors` contient quatre cles exactes ou alternatives. La conversion canonique produit une plage de cinq lignes et quatre groupes de colonnes valides, puis la recherche s'execute.
- Validation : build 0 avertissement/0 erreur; 3/3 tests cibles; 284/284 tests SourceBacked; 363/363 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur; fichiers a 484, 117 et 135 lignes.
- Prochaine preuve discriminante : quatorzieme live canonique. La micro-passe doit produire les mappings types, restaurer quatre colonnes et permettre enfin l'acces a la strategie compacte puis aux outils.

### 2026-07-18 - Quatorzieme live : mapping respecte et quatre colonnes, lignes de plage encore fausses

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-140407/`, trace principale `rag-20260718140449398-4275066a`, trace SourceBacked `sbrag-06f5437b8eaf495aa77dce4add6380f7`, journaux processus `artifacts/live-test-process-20260718-160400/`. Le pipeline termine en environ 4 min 33 s avec l'insuffisance de 281 caracteres, zero outil et zero source.
- Le routeur termine en 55,3 s et le Planner en 90,3 s. Le premier audit ancre prend 91,8 s, rend encore ses groupes concatenees et perd les axes; il est refuse.
- Le mapping minimal constitue un progres live net. `planner_intake_anchor_repair` utilise 2 938 caracteres, termine en 34,1 s et rend seulement 708 caracteres, contre 91,9 s et 1 451 caracteres avec l'ancien schema riche.
- Les quatre colonnes finales sont cette fois correctes et separees : `Petit-dejeuner`, `Diner`, `Souper`, `Collation`, chacune avec un ancrage `exact` ou alternatif. La cinquieme colonne inventee a disparu.
- Les lignes restent invalides : le LLM cree les cles `Jour 1` a `Jour 5`, leur associe les citations `Lundi`, `Mardi`, `Mercredi`, `Jeudi`, `Vendredi` et declare `exact`. `Jour 1` ne correspond pas a `Lundi`, et les jours intermediaires ne figurent pas litteralement dans la question; ils sont impliques par la plage.
- Le champ global `decision` est aussi absent, bien que `kind=two_axis_grid` soit present. Le garde refuse l'approbation manquante, les correspondances exactes label-citation et les citations intermediaires absentes.
- Prochaine correction : renforcer le prompt de plage. Tout membre implique par `X a Y` doit repeter la citation complete de plage avec `range_expansion`; les cles ordinales `Row 1`, `Day 1`, `Item 1` sont interdites sauf si l'utilisateur les a ecrites. Une relation `exact` reste reservee a un libelle effectivement present. Le parseur acceptera aussi les alias mecaniques `structureReviewDecision` ou `reviewDecision` pour `decision`, sans transformer leur valeur.

### 2026-07-18 - Contrat de plage renforce avant le quinzieme live

- Le prompt de `PlannerIntakeAnchorRepair` stipule maintenant qu'une plage continue `X through Y` impose `range_expansion` pour chaque membre intermediaire implique et la repetition de la meme citation complete sous chaque cle.
- Il interdit explicitement les substituts ordinaux `Row 1`, `Day 1`, `Item 1`, `Jour 1` et `Etape 1` lorsque l'utilisateur ne les a pas ecrits. Une relation `exact` ne peut donc plus maquiller un membre de plage sous un libelle artificiel.
- Le parseur accepte `decision`, `structureReviewDecision` ou `reviewDecision`, ainsi que les alias correspondants de `kind` et `reason`. Cette compatibilite est strictement syntaxique : aucune valeur n'est changee et `accept` reste obligatoire au contrat.
- La regression utilise volontairement `structureReviewDecision=accept` dans la micro-sortie minimale. Elle restaure cinq cles de jours, chacune rattachee a la meme plage, et quatre colonnes ancrees.
- Une non-regression du `AnswerAdequacyJudge` a ete ajoutee au filtre cible apres correction d'un fixture de test, afin de confirmer qu'aucun champ `decision` d'une autre etape n'est confondu avec le nouvel alias.
- Validation : build 0 avertissement/0 erreur; 4/4 tests cibles; 284/284 tests SourceBacked; 363/363 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur; prompt 137 lignes, parseur 49 lignes.
- Prochaine preuve discriminante : quinzieme live canonique. La micro-passe doit conserver les quatre colonnes acquises et remplacer `Jour 1..5` par les membres semantiques de la plage, tous ancres a la citation complete.

### 2026-07-18 - Quinzieme live : micro-sortie presente mais mappings non exploitables

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-142253/`, trace principale `rag-20260718142326806-ba322497`, trace SourceBacked `sbrag-b17235a392514dea8df68184f720cd4e`, journaux processus `artifacts/live-test-process-20260718-162243/`. Le test termine proprement en 3 min 36 s avec l'insuffisance de 281 caracteres, zero outil et zero source.
- Le routeur reconnait `rag.multi_search` en 19,3 s et le Planner initial termine en 83,2 s. Le premier audit d'intake prend 43,2 s et repete les deux groupes `exact` concatenees; le contrat d'ancrage les refuse correctement car leurs citations jointes n'apparaissent pas dans la question et une relation exacte ne peut couvrir plusieurs libelles.
- `PlannerIntakeAnchorRepair` est bien appele avec un prompt de 3 277 caracteres et termine en 35,4 s avec une sortie de 776 caracteres. Il ne s'agit donc ni d'un timeout, ni d'une absence de generation.
- Le parseur ne trouve toutefois aucune entree dans `rowAnchors` ou `columnAnchors`; le candidat reconstruit contient zero ligne, zero colonne et aucune approbation reconnue. Le controle mecanique refuse `missing_structure_review_approval`, `two_axis_grid_requires_multiple_rows` et `two_axis_grid_requires_multiple_columns`.
- Aucun contenu semantique n'est infere par le code pour combler ce format. Le pipeline termine avant les outils et ne montre aucune fausse source.
- Le journal actuel ne conserve que la taille de cette micro-sortie, pas sa forme exacte. La prochaine correction doit d'abord rendre observable, de facon bornee, la reponse JSON brute de cette seule frontiere. Une compatibilite supplementaire ne sera ajoutee que si la preuve montre une variation syntaxique univoque; le code ne devra toujours choisir ni libelle, ni axe, ni relation.
- Instrumentation ajoutee : quand la micro-sortie est invalide ou ne contient aucune ancre parseable, `SBRAG_PLANNER_INTAKE_ANCHOR_REPAIR_RAW` conserve un extrait monoligne borne a 1 200 caracteres. Les sorties valides et le comportement utilisateur ne changent pas.
- Validation de l'instrumentation : build 0 avertissement/0 erreur; 4/4 tests focalises; 284/284 tests SourceBacked; 363/363 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur d'espacement. Le fichier de micro-reparation reste a 120 lignes.
- Prochaine preuve discriminante : seizieme live canonique. Si la micro-sortie reste inexploitable, sa forme exacte sera disponible dans le journal pour une correction syntaxique fondee sur preuve; si elle devient valide, le live doit poursuivre jusqu'a la strategie compacte puis aux recherches documentaires.

### 2026-07-18 - Seizieme live : mapping parseable, semantique d'ancrage encore invalide

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-143548/`, trace principale `rag-20260718143619184-d033d010`, trace SourceBacked `sbrag-77dd0d21ac2f43e39f8963290cb829d4`, journaux processus `artifacts/live-test-process-20260718-163542/`. Le pipeline termine en environ 3 min 54 s et le test complet en 4 min 25 s, avec l'insuffisance de 281 caracteres, zero outil et zero source.
- Le routeur termine en 16,6 s et le Planner initial en 89,3 s. Le premier audit prend 80,0 s, rend cinq jours et cinq colonnes dont `Dejeuner`, puis concatene chaque axe dans un unique groupe `exact`; le contrat le refuse.
- `PlannerIntakeAnchorRepair` termine en 47,2 s avec 674 caracteres. Contrairement au quinzieme live, son mapping est parseable : cinq entrees de ligne et au moins cinq entrees de colonne sont reconstruites.
- La decision semantique reste fausse. Les lignes `Lundi`, `Mardi`, `Mercredi`, `Jeudi`, `Vendredi` sont chacune declarees `exact` avec une citation individuelle, alors que seuls les bords de la plage sont litteraux dans la question. Les citations intermediaires sont donc absentes de la question et sont refusees.
- La micro-sortie reintroduit aussi `Dejeuner` avec un ancrage exact imbrique dans `Petit-dejeuner`, et omet encore toute valeur d'approbation reconnue. Le garde refuse donc a la fois la provenance de plage, le chevauchement de sous-fragment et `missing_structure_review_approval`.
- L'instrumentation brute ne se declenche pas, ce qui est attendu : le JSON et les mappings sont valides syntaxiquement. Le blocage n'est ni le parseur, ni la fenetre de contexte, ni le timeout, mais une auto-reparation semantique incomplete du petit modele.
- Prochaine correction : ajouter une seconde micro-passe LLM de reparation du contrat qui recoit uniquement le mapping candidat et ses problemes mecaniques exacts. Elle doit corriger ou clarifier le candidat complet; le code n'expansera aucune plage, ne supprimera aucun libelle metier et ne choisira aucune relation a la place du LLM.

### 2026-07-18 - Seconde micro-reparation du contrat prete pour le dix-septieme live

- Apres l'audit complet puis `PlannerIntakeAnchorRepair`, un candidat parseable mais encore invalide peut maintenant declencher `PlannerIntakeAnchorContractRepair`. Cette troisieme opinion n'est appelee que si le premier mapping minimal existe et echoue les memes verifications canoniques.
- Le prompt recoit la question, la decision candidate, la nature de structure, le libelle de ligne, chaque cle avec sa citation et sa relation, puis les problemes mecaniques exacts. Il exige un mapping complet corrige ou une clarification, jamais un patch.
- Le LLM reste seul responsable des membres de plage, des alternatives, des libelles finaux, des suppressions semantiques et de la decision `accept|clarify`. Le code ne fait que transmettre, parser et reexecuter les contrats de citation, cardinalite, non-chevauchement et structure.
- La regression reproduit le seizieme live : premiere micro-sortie sans approbation, cinq jours faussement `exact` et un libelle imbrique supplementaire; la seconde micro-passe rend ensuite une plage partagee, quatre colonnes ancrees et une approbation explicite, puis la strategie documentaire s'execute.
- L'orchestration de la seconde micro-passe est isolee dans `SourceBackedRagPipeline.PlannerIntakeAnchorContractRepair.cs`; son prompt est isole dans `SourceBackedRagPrompts.PlannerIntakeAnchorContractRepair.cs`. Le controleur d'audit principal reste a 481 lignes, sous la limite architecturale de 500.
- Validation : build 0 avertissement/0 erreur; 4/4 tests focalises; 284/284 tests SourceBacked; 363/363 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur d'espacement.
- Prochaine preuve discriminante : dix-septieme live canonique. La trace doit montrer `planner_intake_anchor_contract_repair` apres le mapping invalide, puis un intake 5 x 4 accepte et l'entree dans le Planner de strategie.

### 2026-07-18 - Dix-septieme live : troisieme opinion atteinte, clarification semantique erronee

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-145157/`, trace principale `rag-20260718145227886-66783a19`, trace SourceBacked `sbrag-ff6862198cf147dd97e165ad06ce1a25`, journaux processus `artifacts/live-test-process-20260718-165151/`. Le pipeline termine en environ 4 min 26 s et le test complet en 4 min 56 s, avec une insuffisance de 293 caracteres, zero outil et zero source.
- Le routeur termine en 21,8 s et le Planner initial en 79,4 s. Le premier audit prend 85,1 s et conserve cette fois quatre colonnes, mais concatene encore chaque axe dans un groupe `exact`; il est refuse.
- La premiere micro-passe termine en 30,1 s. Elle comprime les lignes sous la cle `Semaine` avec une relation `alternative_group` et joint les quatre colonnes dans une seule cle `exact`; elle omet aussi l'approbation. Les citations concatenees ne sont pas dans la question et le candidat est refuse.
- La nouvelle branche `planner_intake_anchor_contract_repair` est bien atteinte avec un prompt de 3 369 caracteres. Elle termine en 47,8 s, sans timeout.
- Sa trace brute bornee montre exactement la sortie : `decision=clarify`, `kind=two_axis_grid`, `rowHeaderLabel=Semaine`, puis un unique objet singulier `rowAnchor` pour `Lundi` et un unique `columnAnchor` pour `Petit-dejeuner`. Le modele affirme que la question ne specifie pas assez les libelles, ce qui est une decision semantique trop prudente et incomplete.
- Le pipeline respecte cette clarification et ne la transforme pas en acceptation. Meme si les noms singuliers etaient toleres syntaxiquement, le candidat ne couvrirait qu'une ligne et une colonne; ajouter un alias de parseur ne resoudrait donc pas le blocage prouve.
- Prochaine correction : decomposer l'adjudication finale en deux micro-decisions LLM independantes. Une passe choisira exclusivement les membres, la citation et la relation de l'axe ligne; l'autre choisira exclusivement les groupes de l'axe colonne. Le code fusionnera ensuite leurs sorties sans ajouter ni retirer de contenu et reappliquera le contrat canonique complet.

### 2026-07-18 - Adjudications LLM separees par axe pretes pour le dix-huitieme live

- Si la reparation combinee echoue encore sur une structure deja decidee `two_axis_grid`, `PlannerIntakeRowAxisAdjudication` demande au LLM uniquement la decision, le libelle d'en-tete, la citation, la relation et la liste ordonnee des membres de ligne.
- Le prompt ligne explique generiquement qu'une plage nommee continue peut etre developpee par le LLM dans la langue de la question sans redemander a l'utilisateur chaque membre intermediaire. Il interdit les substituts numerotes et ne contient aucun vocabulaire repas.
- Seulement si cette decision ligne est `accept` et non vide, `PlannerIntakeColumnAxisAdjudication` demande au LLM uniquement la liste complete des attributs repetes. Chaque entree declare son libelle final, sa citation et sa relation; les alternatives par slash restent un seul besoin et les sous-fragments imbriques sans occurrence distincte sont interdits.
- Le code ne produit aucun membre ni attribut. Il transforme mecaniquement les deux sorties typees en entrees d'ancrage, conserve la nature de structure deja decidee par le LLM, puis reapplique le verificateur canonique complet avant toute recherche.
- La regression reproduit toute la chaine du dix-septieme live, y compris la clarification combinee singuliere, puis fait rendre cinq lignes par l'adjudicateur ligne et quatre colonnes par l'adjudicateur colonne. La fusion restaure vingt cellules et la strategie documentaire recoit cet intake.
- Les parseurs acceptent seulement les alias syntaxiques `decision|axisDecision`, `quote|anchorQuote` et `anchors|columnAnchors`; ils ne changent aucune valeur semantique.
- Validation : build 0 avertissement/0 erreur; 4/4 tests focalises; 284/284 tests SourceBacked; 363/363 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur d'espacement. Tous les nouveaux fichiers restent sous 160 lignes et le controleur principal reste a 481 lignes.
- Prochaine preuve discriminante : dix-huitieme live canonique. La trace doit montrer l'adjudication ligne puis colonne acceptees, `planner_intake_axis_adjudication accepted=True` avec cinq lignes et quatre colonnes, puis le Planner de strategie.

### 2026-07-18 - Dix-huitieme live : adjudication ligne melange encore les deux axes

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-150832/`, trace principale `rag-20260718150907768-2c180639`, trace SourceBacked `sbrag-3fc85752c507471db98c72ab57d088f6`, journaux processus `artifacts/live-test-process-20260718-170825/`. Le pipeline termine en environ 7 min 21 s et le test complet en 7 min 57 s, avec une insuffisance de 293 caracteres, zero outil et zero source.
- Le routeur termine en 29,8 s, le Planner initial en 72,7 s et le premier audit en 92,0 s. L'audit concatene encore les deux axes et ajoute `Dejeuner`; il est refuse.
- La premiere micro-passe prend 56,9 s et rend `Semaine` comme ligne, cinq colonnes dont `Dejeuner`, plusieurs relations incoherentes et aucune approbation. Elle est refusee.
- La reparation combinee atteint 118,8 s sans timeout et rend 1 307 caracteres. Sa trace brute montre `clarify`, cinq objets singuliers `rowAnchor` et cinq `columnAnchor`, avec des jours faussement exacts et des repas faussement alternatifs. Le parseur ne perd pas une bonne decision : la sortie est semantiquement et structurellement incomplete.
- `PlannerIntakeRowAxisAdjudication` est ensuite bien appele avec un prompt de 2 197 caracteres et termine en 68,2 s. Il choisit pourtant `clarify` et place dans `labels` les cinq jours puis les quatre attributs repas. La passe colonne n'est donc pas appelee.
- Diagnostic : le prompt specialise recevait encore le rapport mecanique complet et la raison de la reparation combinee, qui mentionnaient les deux axes. Le petit modele reabsorbe ce contexte contradictoire et melange les dimensions malgre l'instruction systeme.
- Correction engagee : chaque adjudication repartira du premier audit LLM, qui avait deja separe les candidats de lignes et de colonnes. Le prompt ligne ne recevra que les candidats ligne; le prompt colonne seulement les candidats colonne. Aucun rapport de l'autre axe ni raison combinee ne sera reinjecte.
- Contrainte materielle verifiee : machine de reference avec Quadro P520 4 Gio et 31,8 Gio de RAM. Le CDC qualifie explicitement Qwen 2.5 3B Q4_K_M sur cette machine; les variantes Mistral 7B sont classees profil client large/10 Gio dans le catalogue. Un basculement aveugle vers 7B ne serait ni qualifie ni compatible avec le budget interactif actuel.

### 2026-07-18 - Isolation stricte des candidats par axe avant le dix-neuvieme live

- L'adjudication par axe est maintenant tentee immediatement apres l'echec du premier mapping minimal, avant la reparation combinee qui a coute 118,8 s au dix-huitieme live. La reparation combinee reste un dernier recours generique seulement si les adjudications specialisees n'aboutissent pas.
- Les deux adjudications repartent du premier audit LLM, pas du candidat combine vide. Ce premier audit avait deja separe semantiquement les lignes et colonnes, meme si ses citations etaient invalides.
- Le prompt ligne recoit uniquement `PRIOR_LLM_CANDIDATE_LABELS_FOR_THIS_AXIS_ONLY` pour les lignes. Il ne recoit plus le rapport mecanique, la raison combinee ni les candidats colonne. Le prompt systeme interdit explicitement de rendre un attribut de colonne dans `labels`.
- Le prompt colonne suit la meme isolation : uniquement les candidats colonne du premier audit, sans jours, sans rapport de l'axe ligne et avec interdiction explicite de rendre une entite ou periode de ligne.
- Les candidats restent non fiables : le LLM peut les corriger, completer ou retirer depuis la question. Le code ne considere pas la separation candidate comme une acceptation; la fusion finale doit toujours passer les contrats canoniques d'ancrage et de structure.
- Les sorties `clarify` ou invalides conservent maintenant un extrait brut borne dans `SBRAG_PLANNER_INTAKE_AXIS_ADJUDICATION`, afin de distinguer une vraie ambiguite d'un nouveau melange d'axes.
- Regression actualisee : le premier audit fournit cinq jours et cinq colonnes dont une imbriquee; le prompt ligne contient seulement les cinq jours, le prompt colonne seulement ses cinq candidats, puis les deux decisions LLM restaurent cinq par quatre avant la strategie.
- Validation finale : build 0 avertissement/0 erreur; 4/4 tests focalises; 284/284 tests SourceBacked; 363/363 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur d'espacement. Le controleur principal est redescendu a 482 lignes et l'ordre des fallback est isole dans un fichier de 25 lignes.
- Prochaine preuve discriminante : dix-neuvieme live canonique. La trace doit demarrer directement `planner_intake_row_axis_adjudication` apres le premier mapping invalide, montrer exactement cinq candidats jours, puis une adjudication colonne distincte et enfin l'entree dans la strategie documentaire.

### 2026-07-18 - Dix-neuvieme live : axe ligne isole mais clarification hors perimetre

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-153146/`, trace principale `rag-20260718153219327-f310740e`, trace SourceBacked `sbrag-31796c5d87cc4c6db201bcc43334f9b4`, journaux processus `artifacts/live-test-process-20260718-173141/`. Le pipeline termine en environ 5 min 08 s et le test complet en 5 min 40 s, avec une insuffisance de 293 caracteres, zero outil et zero source.
- Le routeur termine en 16,7 s, le Planner initial en 80,5 s et l'audit en 91,5 s. Le premier audit fournit comme prevu cinq jours et cinq colonnes dont `Dejeuner`, avec ses groupes concatenees invalides.
- La premiere micro-passe prend 62,5 s et rend un JSON mal ferme. Sa trace brute montre egalement `clarify`, cinq jours exacts et cinq colonnes exactes; elle est refusee au parseur sans inference.
- Le nouvel ordre fonctionne : `PlannerIntakeRowAxisAdjudication` demarre directement ensuite avec un prompt de 2 001 caracteres. La reparation combinee de 118,8 s n'est pas appelee.
- L'isolation des candidats corrige le melange du dix-huitieme live. La sortie ligne contient exactement `Lundi, Mardi, Mercredi, Jeudi, Vendredi` et aucun repas.
- Le LLM choisit neanmoins `clarify` pour une raison hors perimetre : `The user's question is not clear about the format and sources to be used.` Il cite toute la question et declare `relation=exact`, malgre les cinq membres corrects.
- La passe colonne n'est pas appelee, car le pipeline respecte cette decision `clarify`. L'echec n'est plus une confusion d'axes mais un champ de decision encore trop global et une consigne de citation/relation insuffisamment focalisee.
- Prochaine correction : redefinir explicitement la portee de `decision` dans le prompt ligne. Seule l'ambiguite des membres de ligne peut produire `clarify`; format de sortie, sources, recherche, contenu et autres contraintes sont hors scope. Exiger la plus courte citation contigue et rappeler qu'une plage claire doit utiliser `range_expansion`, jamais `exact` pour plusieurs membres.

### 2026-07-18 - Decision d'axe strictement bornee avant le vingtieme live

- Les prompts ligne et colonne definissent maintenant `decision` comme la reponse a une seule question : les membres complets de cet axe sont-ils semantiquement impliques par la demande ?
- Le format final, le choix des sources, la strategie de recherche, le contenu de la reponse et l'autre axe sont explicitement hors perimetre et ne peuvent pas justifier `clarify`.
- `quote` doit etre la plus courte citation contigue qui justifie les membres. Le prompt interdit de recopier toute la question lorsqu'une expression de plage ou de slot plus courte suffit.
- Pour l'axe ligne, plusieurs membres impliques par une unique plage imposent explicitement `relation=range_expansion`; `exact` est interdit dans ce cas. Cette decision reste produite par le LLM puis verifiee mecaniquement.
- Le meme cadrage preventif est applique a l'axe colonne afin que les sources ou le format ne provoquent pas une future clarification hors sujet apres acceptation des lignes.
- Validation : build 0 avertissement/0 erreur; 4/4 tests focalises; 284/284 tests SourceBacked; 363/363 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur d'espacement. Le fichier des deux prompts reste a 79 lignes.
- Prochaine preuve discriminante : vingtieme live canonique. La passe ligne doit transformer ses cinq candidats deja corrects en `decision=accept`, citation courte de plage et `range_expansion`, puis la passe colonne doit enfin etre observee.

### 2026-07-18 - Vingtieme live : deux axes approuves, fusion canonique encore refusee

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-154802/`, trace principale `rag-20260718154842878-bfd3e9a2`, trace SourceBacked `sbrag-079173fbde7748e280fedcdec6e14076`, journaux processus `artifacts/live-test-process-20260718-174751/`. Le pipeline termine en environ 4 min 03 s et le test complet en 4 min 43 s, avec une insuffisance de 293 caracteres, zero outil et zero source.
- Le routeur termine en 16,2 s, le Planner initial en 84,9 s et le premier audit en 65,4 s. L'audit rend encore cinq jours et cinq colonnes dont `Dejeuner`, avec groupes concatenees invalides.
- La premiere micro-passe ne coute que 18,2 s mais reste invalide : `Semaine` comme ligne, colonnes concatenees et approbation absente.
- `PlannerIntakeRowAxisAdjudication` constitue le premier succes live complet sur cet axe : prompt 2 445 caracteres, reponse en 9,7 s, `decision=accept`, exactement cinq jours et aucun attribut colonne.
- `PlannerIntakeColumnAxisAdjudication` est atteinte pour la premiere fois : prompt 2 419 caracteres, reponse en 20,7 s, `decision=accept`. Elle rend toutefois cinq colonnes et conserve `Dejeuner` a cote de `Petit-dejeuner`.
- La fusion canonique refuse correctement deux problemes. La decision ligne a cinq bons labels, mais la relation parseable est vide et sa citation couvre `J'ai besoin ... lundi au vendredi` au lieu du plus court fragment; `invalid_structure_anchor_relation` est leve. Les colonnes echouent `overlapping_exact_structure_anchors` car `Dejeuner` reutilise le sous-fragment de `Petit-dejeuner`.
- La reparation combinee historique est tentee ensuite en dernier recours, termine en 26,0 s et rend encore un schema singulier/incomplet; elle est refusee. Aucun outil n'est appele.
- Prochaine correction : verifier chaque decision d'axe separement avant fusion. Si ses propres ancrages echouent, une micro-reparation LLM de cet axe seulement recevra le candidat et ses problemes mecaniques propres, puis sera reparsee et reverifiee. Le code ne choisira toujours ni relation de plage ni suppression du libelle imbrique.

### 2026-07-18 - Reparations LLM locales par axe pretes pour le vingt-et-unieme live

- Chaque decision ligne est maintenant controlee avant la passe colonne : approbation, raison, en-tete, cardinalite, citation, relation et couverture passent le meme contrat d'ancrage canonique que la fusion finale.
- Si la decision ligne echoue, `PlannerIntakeRowAxisContractRepair` recoit uniquement la question, le candidat ligne complet et ses problemes propres. Le LLM doit rendre une nouvelle decision complete; le code ne choisit pas `range_expansion` et ne raccourcit pas la citation.
- La decision colonne suit exactement le meme principe avec `PlannerIntakeColumnAxisContractRepair`. Son rapport peut exposer un chevauchement de sous-fragment, mais seul le LLM peut retirer, conserver ou renommer un candidat.
- Les deux reparations possedent un gabarit JSON explicite et un vocabulaire d'axe strict. Elles excluent sources, strategie, format final, contenu et autre axe de leur perimetre.
- Toute sortie reparee est reparsee puis reverifiee. Une seconde violation ne declenche aucune correction deterministe; le chemin revient au terminal sur ou au dernier fallback LLM generique.
- Regression du vingtieme live : la premiere decision ligne accepte cinq jours avec relation vide et citation trop large; la reparation LLM rend une plage valide. La premiere decision colonne accepte cinq attributs chevauchants; sa reparation rend quatre ancrages, puis la fusion vingt cellules et la strategie documentaire sont acceptees.
- Validation : build 0 avertissement/0 erreur; 4/4 tests focalises; 284/284 tests SourceBacked; 363/363 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur d'espacement. Les nouveaux fichiers font 160 et 97 lignes.
- Prochaine preuve discriminante : vingt-et-unieme live canonique. La trace doit montrer les deux adjudications `accept`, les problemes locaux, leurs deux reparations contractuelles acceptees, puis `planner_intake_axis_adjudication accepted=True` et l'entree dans le Planner de strategie.

### 2026-07-18 - Vingt-et-unieme live : reparateur ligne atteint mais rouvre les champs valides

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-161010/`, trace principale `rag-20260718161045456-dcf89cfb`, trace SourceBacked `sbrag-7b238171400e4aa6b6bb0aaeec200353`, journaux processus `artifacts/live-test-process-20260718-181005/`. Le pipeline termine en environ 8 min 36 s et le test complet en 9 min 10 s, avec une insuffisance de 293 caracteres, zero outil et zero source.
- Le routeur termine en 28,1 s et le Planner initial en 81,5 s. L'audit d'intake termine en 89,9 s avec cinq jours et cinq colonnes dont `Dejeuner`; ses citations concatenees sont correctement refusees.
- La premiere micro-reparation dure 88,1 s, separe les membres mais omet encore l'approbation, cite des jours absents individuellement de la question et conserve la cinquieme colonne imbriquee.
- L'adjudication ligne isolee termine en 37,1 s avec `decision=accept`, exactement `Lundi, Mardi, Mercredi, Jeudi, Vendredi`, aucun attribut colonne et l'en-tete `Semaine`. Son unique probleme mecanique est `invalid_structure_anchor_relation` parce que `relation` est absent.
- La nouvelle reparation contractuelle ligne est donc atteinte comme prevu. Elle choisit bien `relation=range_expansion` et conserve les cinq jours, mais rouvre a tort des champs qui n'etaient pas rejetes : `decision=clarify`, `rowHeaderLabel=Lundi`, raison contradictoire et pseudo-JSON a guillemets simples contenant `J\'ai`. Le parseur la refuse comme `invalid_json` sans inference.
- La reparation combinee de dernier recours dure 131,7 s, rend `clarify`, cinq objets `rowAnchor`, cinq objets `columnAnchor` dont `Dejeuner`, et un `rowHeaderLabel` objet. Elle est refusee avant toute recherche.
- Diagnostic : le prompt local disait encore que le reparateur pouvait garder, retirer, renommer ou ajouter des labels, meme lorsque le rapport mecanique ne rejetait que la relation. Le LLM a donc reouvert une decision semantique deja acceptee et a degrade trois champs valides au lieu de corriger le seul champ signale.
- Correction suivante : rendre la reparation locale strictement differentielle. Tout champ candidat non cite dans `MECHANICAL_REJECTION_REPORT` doit etre preserve exactement; une decision `accept`, des labels et un en-tete non rejetes ne peuvent pas etre reouverts. Le LLM conserve la responsabilite de choisir la relation ou toute autre valeur explicitement rejetee. Le contrat exigera en plus du JSON RFC 8259 a guillemets doubles et interdira l'echappement des apostrophes.
- Prochaine preuve discriminante : regression reproduisant la sortie du vingt-et-unieme live, validation complete, puis vingt-deuxieme live. La passe ligne doit conserver l'acceptation, l'en-tete et les cinq jours, choisir une relation valide, atteindre ensuite l'adjudication colonne et sa reparation locale.

### 2026-07-18 - Reparation differentielle et retry JSON LLM prets pour le vingt-deuxieme live

- Les prompts de reparation ligne et colonne declarent maintenant explicitement qu'il s'agit d'une reparation differentielle, pas d'une nouvelle adjudication. Le candidat a deja passe tous les controles absents du rapport mecanique.
- Tout champ non rejete doit etre recopie exactement. En particulier, une decision `accept` ne peut pas devenir `clarify` sans probleme `row_axis_not_approved` ou `column_axis_not_approved`; un en-tete, une raison, une citation, des labels ou des ancres non signales ne peuvent pas etre reouverts.
- Le LLM garde la decision sur toute valeur rejetee : relation, citation, membre ou ancre impliques par le rapport. Le code ne choisit ni `range_expansion`, ni le fragment cite, ni la suppression de `Dejeuner`.
- Le format de sortie exige maintenant du JSON RFC 8259 strict : cles et chaines a guillemets doubles, aucun commentaire, aucune virgule terminale et aucune apostrophe echappee comme en pseudo-JSON Python.
- Si la premiere reparation est syntaxiquement invalide, le code ne tente aucune normalisation heuristique. Il lance une unique seconde passe LLM du meme contrat avec `STRICT_JSON_FAILURE`, la sortie refusee bornee et le candidat original; la nouvelle sortie est ensuite reparsee et reverifiee completement.
- La regression principale reproduit desormais la sortie exacte du live 21 : `clarify`, en-tete `Lundi`, apostrophes echappees et guillemets simples. Elle prouve le refus syntaxique, l'appel du retry LLM, la conservation de l'acceptation et des cinq jours, puis la reparation colonne et l'entree dans la strategie documentaire.
- Validation : build en 2 min 32 s, 0 avertissement/0 erreur; 2/2 tests focalises; 284/284 tests SourceBacked; 363/363 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur d'espacement, seulement les avertissements EOL deja connus.
- Tailles : prompt differentiel 121 lignes, controle par axe 172 lignes, retry JSON isole 86 lignes. Le retry reste distinct du controleur principal et ne cree aucune decision semantique en code.
- Prochaine preuve discriminante : vingt-deuxieme live canonique. La ligne doit etre acceptee directement ou apres un seul retry JSON, puis la colonne doit etre adjudiquee et reparee avant la fusion 5 x 4 et le Planner de strategie.

### 2026-07-18 - Vingt-deuxieme live : sortie parseable mais contrat differentiel viole

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-163211/`, trace principale `rag-20260718163242816-d9ee1ec7`, trace SourceBacked `sbrag-ce3b2f8afdc54fc1b01142059e299b4c`, journaux processus `artifacts/live-test-process-20260718-183207/`. Le pipeline termine en environ 4 min 41 s et le test complet en 5 min 12 s, avec une insuffisance de 293 caracteres, zero outil et zero source.
- Le routeur termine en 17,6 s, le Planner initial en 85,2 s et l'audit en 65,6 s. L'audit reproduit cinq jours et cinq colonnes concatenees; il est refuse comme attendu.
- La premiere reparation d'ancrages termine en 28,6 s, puis l'adjudication ligne en seulement 12,7 s avec `decision=accept`, les cinq jours exacts, l'en-tete `Semaine` et toujours la seule relation absente.
- Le nouveau prompt differentiel raccourcit fortement la reparation ligne a 13,7 s, mais le modele ne respecte pas encore la preservation : il rend une sortie parseable avec `decision=clarify`, conserve les cinq jours et choisit `range_expansion`, mais modifie `fasses` en `fasse` dans la citation.
- Le verificateur signale exactement `row_axis_not_approved` et `structure_anchor_quote_not_in_user_question`. Comme la sortie etait syntaxiquement parseable, le retry reserve a `invalid_json` n'est pas appele.
- La reparation combinee de dernier recours termine en 55,5 s, demande encore une clarification injustifiee, rend des tableaux singuliers `rowAnchor` et `columnAnchor`, puis est refusee avant toute recherche.
- Diagnostic : la distinction syntaxe/contrat est trop etroite pour la boucle locale. Une sortie peut etre du JSON parseable tout en violant le contrat differentiel. Le meme second essai LLM doit etre declenche apres toute reparation locale encore invalide, avec le candidat original comme base autoritative, la sortie refusee et son nouveau rapport mecanique.
- Contrainte d'architecture : le code ne corrigera ni `clarify`, ni `fasse`, ni la relation. Il constatera les violations, demandera au LLM une seconde decision complete, puis reparsera et reverifiera sans troisieme tentative locale.
- Prochaine preuve discriminante : etendre le retry a toute violation contractuelle residuelle, reproduire la sortie du live 22 dans la regression, revalider puis lancer le vingt-troisieme live. La ligne doit enfin passer et permettre la premiere reparation colonne live.

### 2026-07-18 - Retry LLM generalise a toute violation contractuelle avant le vingt-troisieme live

- Le second essai local n'est plus limite a `invalid_json`. Une premiere reparation parseable mais encore rejetee par le contrat canonique declenche maintenant le meme retry LLM borne.
- Le retry repart toujours du candidat original comme base autoritative. Il recoit separement les problemes originaux a reparer, la sortie precedente a ne pas copier et le nouveau `PREVIOUS_REPAIR_REJECTION_REPORT` qui expose les regressions introduites.
- Cette separation evite de promouvoir une sortie degradee au rang de nouveau candidat. Une decision `accept`, un en-tete et des labels initialement valides restent visibles comme etat de reference, tandis que `clarify` ou une citation alteree apparaissent uniquement dans la tentative refusee.
- Une seule seconde passe est autorisee. Sa sortie est reparsee puis reverifiee integralement; si elle reste invalide, aucun troisieme essai local et aucune correction semantique en code ne sont appliques.
- La regression reproduit maintenant le live 22 avec du JSON strict mais `decision=clarify` et une citation typo `fasse`. Elle prouve l'appel du retry general, la presence de `row_axis_not_approved` et `structure_anchor_quote_not_in_user_question`, puis la restauration LLM d'une ligne valide avant la colonne.
- Validation : build en 2 min 52 s, 0 avertissement/0 erreur; 2/2 tests focalises; 284/284 tests SourceBacked; 363/363 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur d'espacement, seulement les avertissements EOL connus.
- Tailles : prompt differentiel/retry 133 lignes, controle local 194 lignes, retry isole 90 lignes. Aucun fichier de ce correctif ne depasse 200 lignes.
- Prochaine preuve discriminante : vingt-troisieme live canonique. La trace doit montrer `planner_intake_row_axis_contract_retry` accepte si la premiere reparation regresse encore, puis atteindre l'adjudication et la reparation colonne.

### 2026-07-18 - Vingt-troisieme live : retry actif, divergence entre deux champs du meme audit LLM

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-164703/`, trace principale `rag-20260718164733247-a225048a`, trace SourceBacked `sbrag-62035bbc8d5d4f9b9801b6e0380f763e`, journaux processus `artifacts/live-test-process-20260718-184659/`. Le pipeline termine en environ 7 min 23 s et le test complet en 7 min 53 s, avec une insuffisance de 293 caracteres, zero outil et zero source.
- Le routeur termine en 17,4 s, le Planner initial en 104,9 s et l'audit en 123,7 s. L'audit est incoherent entre ses champs : `structureAnchorGroups` journalise correctement cinq jours en ligne et cinq attributs repas en colonne, mais son `intakeDecision` ne fournit pas de candidats d'axe exploitables.
- La premiere reparation d'ancrages termine en 18,0 s et demande une clarification sans ancre. L'adjudication ligne demarre ensuite avec un prompt plus court de 2 402 caracteres, signe que sa liste `IntakeDecision.RowLabels` etait vide.
- Sans les labels pourtant disponibles dans `StructureAnchorGroups`, l'adjudicateur ligne repart de la question et choisit en 41,0 s les quatre repas comme lignes : `Petit-dejeuner, Diner, Souper, Gouter / Collation`. Il accepte cette mauvaise dimension, sans en-tete ni relation.
- La premiere reparation locale termine en 25,7 s et reste `clarify` avec une citation typo. Le nouveau retry general est bien appele, termine en 43,7 s et restaure `decision=accept`, ce qui prouve le fonctionnement du correctif du live 22. Il conserve cependant le mauvais axe et la citation typo, donc le contrat le refuse encore.
- La reparation combinee finale termine en 66,9 s et reste inexploitable. Aucun appel documentaire n'est lance.
- Diagnostic de raccordement : le prompt d'adjudication ne lit actuellement que `IntakeDecision.RowLabels` ou `ColumnLabels`. Il ignore les labels du meme axe deja produits par le LLM dans `StructureAnchorGroups`, alors que la trace de ce live montre que ce second champ contient la bonne separation.
- Correction suivante : exposer separement a l'adjudicateur les deux opinions LLM du meme axe : labels de `IntakeDecision` et labels des `StructureAnchorGroups` filtres mecaniquement par `axis`. Le code ne fusionnera ni ne choisira les valeurs; le LLM verra les deux listes eventuellement contradictoires et tranchera directement depuis `USER_QUESTION`.
- Prochaine preuve discriminante : regression avec `IntakeDecision` vide mais ancres de structure correctes, validation complete, puis vingt-quatrieme live. Le prompt ligne doit montrer les cinq jours issus des groupes d'ancrage et le prompt colonne les attributs repas, sans melanger les axes.

### 2026-07-18 - Deux opinions LLM par axe exposees avant le vingt-quatrieme live

- Le prompt d'adjudication ne depend plus d'un seul champ de l'audit. Pour l'axe demande, il expose separement `PRIOR_LLM_INTAKE_DECISION_LABELS_FOR_THIS_AXIS_ONLY` et `PRIOR_LLM_STRUCTURE_ANCHOR_LABELS_FOR_THIS_AXIS_ONLY`.
- Les groupes d'ancrage sont filtres uniquement par leur champ type `axis=row|column`, puis leurs labels sont recopies sans classement semantique. Le code ne fusionne pas les listes et ne choisit pas laquelle est correcte.
- Le prompt explique qu'il s'agit de deux opinions LLM non fiables, eventuellement vides, incompletes ou contradictoires. L'adjudicateur doit produire sa decision finale directement depuis `USER_QUESTION`, pour cet axe seulement.
- La regression reproduit le live 23 : `IntakeDecision` ne contient aucun label ni en-tete, tandis que `StructureAnchorGroups` contient cinq jours en ligne et cinq candidats repas en colonne. Elle verifie que les deux lignes de contexte restent distinctes et que chaque axe recoit uniquement ses propres groupes.
- L'ancienne assertion de chevauchement a ete remplacee par le diagnostic reel `non_tabular_structure_has_anchor_groups`, attendu lorsque les ancres existent mais que l'intake LLM est vide.
- Validation : build complet en 3 min 08 s et build test incremental en 21 s, 0 avertissement/0 erreur; 2/2 tests focalises; 284/284 tests SourceBacked; 363/363 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur d'espacement.
- Le fichier de prompt d'adjudication reste a 91 lignes. Aucune heuristique de jour, repas, cuisine ou calendrier n'est ajoutee.
- Prochaine preuve discriminante : vingt-quatrieme live canonique. Si l'intake est de nouveau vide ou contradictoire, la trace doit montrer les cinq jours dans l'opinion des groupes d'ancrage du prompt ligne, puis une decision ligne semantiquement correcte.

### 2026-07-18 - Vingt-quatrieme live : axe ligne correct, reecriture complete introduit toujours une typo

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-170522/`, trace principale `rag-20260718170555037-6dd33677`, trace SourceBacked `sbrag-ad1df849a6414154975ed8c81a3ab51e`, journaux processus `artifacts/live-test-process-20260718-190518/`. Le pipeline termine en environ 4 min 16 s et le test complet en 4 min 48 s, avec une insuffisance de 293 caracteres, zero outil et zero source.
- Le routeur termine en 16,4 s, le Planner initial en 67,1 s et l'audit en 53,2 s. L'audit rend a nouveau cinq jours et cinq candidats repas dans ses groupes d'ancrage, avec des citations concatenees refusees.
- La premiere reparation termine en 28,6 s. Le nouvel adjudicateur a deux opinions termine en 14,8 s et choisit correctement exactement `Lundi, Mardi, Mercredi, Jeudi, Vendredi`, sans aucun repas. Le correctif du live 23 est donc prouve en live.
- Cette decision ligne rend toutefois `clarify` et omet la relation. Ses seuls problemes mecaniques sont `row_axis_not_approved` et `invalid_structure_anchor_relation`; l'en-tete, la citation exacte et les cinq labels ont passe leurs controles.
- La reparation complete termine en 12,5 s, choisit `accept` et `range_expansion`, mais reecrit la citation valide en remplacant `fasses` par `fasse`. Le retry termine en 20,5 s et recopie exactement la meme faute; les deux sorties sont refusees par `structure_anchor_quote_not_in_user_question`.
- Diagnostic : une reparation qui doit changer seulement la decision et la relation ne devrait pas demander au petit modele de regenerer raison, en-tete, citation et labels. Le contrat complet augmente inutilement la surface de regression et le modele recopie sa propre faute au retry.
- Correction suivante : introduire un patch LLM type et minimal lorsque les seuls codes rejetes sont l'approbation et/ou la relation. Le LLM rendra uniquement `decision`, `reason` et `relation`; le code fusionnera mecaniquement ces valeurs LLM avec la citation, l'en-tete et les labels du candidat LLM deja valides, puis reverifiera l'objet complet.
- Contrainte d'architecture : cette fusion ne choisit aucune valeur semantique. Le LLM decide `accept|clarify`, la raison et `exact|range_expansion|alternative_group`; le code applique le patch type et controle le contrat. Toute erreur de citation ou de labels continuera d'utiliser une reparation LLM complete.
- Prochaine preuve discriminante : regression du live 24 avec decision/relation seules invalides, validation complete, puis vingt-cinquieme live. La ligne doit passer sans reemettre sa citation et l'adjudication colonne doit enfin etre atteinte.

### 2026-07-18 - Patch LLM decision/relation type avant le vingt-cinquieme live

- Lorsque tous les problemes ligne sont strictement limites a `row_axis_not_approved` et/ou `invalid_structure_anchor_relation`, le pipeline utilise maintenant `PlannerIntakeRowAxisDecisionRelationPatch` au lieu de regenerer l'objet ligne complet.
- Le prompt du patch expose la question, l'en-tete, la citation et les labels deja valides, mais interdit de les rendre, reecrire ou discuter. Sa sortie type contient uniquement `decision`, `reason` et `relation`.
- Le LLM conserve toute la decision semantique : approbation ou clarification, justification et type de relation. Le code applique mecaniquement ces trois valeurs a la decision LLM existante avec un `with`, sans modifier citation, en-tete ni labels, puis reapplique le verificateur canonique complet.
- Si une citation, un en-tete, une cardinalite ou un label est aussi invalide, cette voie minimale est ineligible et la reparation LLM complete reste utilisee. La selection du contrat se fonde uniquement sur les codes mecaniques generiques, pas sur un lexique metier.
- Un parseur type distinct `ParseIntakeRowAxisDecisionRelationPatch` et le contrat `SourceBackedIntakeRowAxisDecisionRelationPatch` empechent toute mutation implicite des autres champs.
- La regression reproduit le live 24 : axe ligne avec cinq jours, citation exacte et en-tete valides, mais `decision=clarify` et relation vide. Le patch LLM rend `accept` et `range_expansion`; la citation contenant `fasses` est preservee octet pour octet, puis la colonne et la strategie sont atteintes.
- Validation : build en 4 min 13 s, 0 avertissement/0 erreur; 2/2 tests focalises; 284/284 tests SourceBacked; 363/363 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur d'espacement.
- Tailles : controle local 199 lignes, pipeline du patch 57 lignes, prompt du patch 48 lignes. Aucun fichier du correctif ne depasse 200 lignes.
- Prochaine preuve discriminante : vingt-cinquieme live canonique. `planner_intake_row_axis_decision_relation_patch` doit accepter la ligne sans faute de citation, puis `planner_intake_column_axis_adjudication` doit etre observe pour la premiere fois depuis le live 20.

### 2026-07-18 - Vingt-cinquieme live : patch minimal preserve les champs mais rend une decision hors vocabulaire

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-172344/`, trace principale `rag-20260718172416096-1bc44dfa`, trace SourceBacked `sbrag-301aff7ac4d44180be32ad93f5ed8efd`, journaux processus `artifacts/live-test-process-20260718-192338/`. Le pipeline termine en environ 6 min 42 s et le test complet en 7 min 13 s, avec une insuffisance de 293 caracteres, zero outil et zero source.
- Le routeur termine en 15,5 s, le Planner initial en 99,6 s et l'audit en 82,5 s. La premiere reparation prend 64,2 s et reste invalide.
- L'adjudication ligne termine en 38,9 s avec exactement les cinq jours, une citation exacte et un en-tete valide, mais encore `clarify` et aucune relation. La voie minimale est donc eligible et appelee comme prevu.
- `PlannerIntakeRowAxisDecisionRelationPatch` utilise un prompt de seulement 1 746 caracteres et termine en 10,9 s. Il preserve parfaitement la citation, l'en-tete et les labels parce qu'il ne peut pas les rendre. Le correctif du live 24 est donc prouve.
- Le LLM choisit correctement `range_expansion`, mais rend `decision=reject`, valeur hors vocabulaire, et affirme que la question ne specifie pas les jours malgre la plage visible. Le verificateur le refuse via `row_axis_not_approved`.
- La reparation combinee finale prend 88,3 s et reste inexploitable. La colonne n'est pas atteinte.
- Diagnostic : le prompt minimal n'interdit pas encore explicitement `reject` et n'explique pas assez que les membres ordinaires d'une plage continue nommee sont semantiquement impliques sans enumeration separee. Le petit modele recree donc une fausse ambiguite malgre le contexte valide.
- Correction suivante : borner `decision` a exactement `accept|clarify`, interdire toute autre valeur et definir `clarify` comme l'existence de plusieurs ensembles de membres plausibles. Rappeler generiquement qu'une plage continue a extremites nommees implique ses membres ordinaires. Ajouter une unique seconde tentative du meme patch si la premiere sortie reste hors contrat, avec la sortie et son rapport de rejet.
- Contrainte d'architecture : le code ne convertira jamais `reject` en `accept`. Il demandera au LLM de reconsiderer sa decision dans le vocabulaire type, puis verifiera. La relation reste elle aussi choisie par le LLM.
- Prochaine preuve discriminante : regression reproduisant `decision=reject`, validation complete, puis vingt-sixieme live. Le retry du patch doit rendre une decision type et permettre l'appel de l'adjudication colonne.

### 2026-07-18 - Retry du patch minimal valide avant le vingt-sixieme live

- Le contrat minimal ferme maintenant explicitement `decision` a exactement `accept|clarify` et interdit `reject`, `rejected`, `deny`, `need_more_evidence` ou toute autre valeur. `clarify` est reserve au cas ou plusieurs ensembles de membres restent reellement plausibles.
- Le prompt rappelle de maniere generique qu'une plage continue claire a extremites nommees implique ses membres intermediaires ordinaires et qu'une enumeration separee de chacun d'eux n'est pas necessaire. Aucun vocabulaire de jour, calendrier, repas ou cuisine n'est introduit.
- Si le premier patch minimal reste hors contrat, une seule seconde passe LLM `planner_intake_row_axis_decision_relation_patch_retry` est appelee. Elle repart du candidat LLM original, recoit la sortie refusee et son rapport mecanique, puis rend a nouveau seulement `decision`, `reason` et `relation`.
- Le code ne transforme toujours aucune decision. Il applique mecaniquement les trois champs du patch LLM a la base autoritative, puis execute le verificateur complet. Une seconde sortie invalide est refusee sans troisieme essai local.
- La regression reproduit exactement la nouvelle panne du live 25 : premier patch `decision=reject` avec `relation=range_expansion`, puis retry `decision=accept`. Elle prouve que les champs valides restent intacts, que le contexte de rejet est visible au second LLM et que l'adjudication colonne puis la strategie documentaire sont atteintes.
- Validation : build complet en 4 min 13 s puis build incremental en 41 s, 0 avertissement/0 erreur; 2/2 tests focalises; 284/284 tests SourceBacked; 363/363 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur d'espacement, seulement les avertissements EOL connus.
- Tailles : controle local 199 lignes, pipeline du patch et de son retry 115 lignes, prompt minimal 65 lignes. Aucun fichier du correctif ne depasse 200 lignes.
- Prochaine preuve discriminante : vingt-sixieme live canonique. La ligne doit etre acceptee par le premier patch ou son unique retry; la trace doit ensuite montrer pour la premiere fois l'adjudication colonne live et permettre d'observer son eventuelle reparation avant le Planner de strategie.

### 2026-07-18 - Vingt-sixieme live : le patch minimal est contourne par des metadonnees ligne invalides

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-174810/`, trace principale `rag-20260718174846631-fd6bc120`, trace SourceBacked `sbrag-8dd813ff67b1496b9a0adabb860e0079`, journaux processus `artifacts/live-test-process-20260718-194805/`. Le pipeline termine en environ 6 min 38 s et le test complet en 7 min 14 s avec une insuffisance sure de 293 caracteres, zero outil et zero source.
- Le routeur termine en 92,5 s, le Planner initial en 113,5 s et l'audit d'intake en 96,4 s. L'audit identifie les cinq jours et cinq candidats repas, mais les rend sous des ancres concatenees et un intake non tabulaire incoherent.
- La premiere reparation d'ancrages termine rapidement en 9,4 s mais abandonne toutes les ancres. L'adjudication ligne recoit alors les bonnes opinions LLM et choisit bien les cinq jours en 16,4 s.
- Cette fois, l'adjudicateur rend simultanement quatre metadonnees invalides : `decision=clarify`, en-tete egal a la liste concatenee des jours, citation typo `fasse` absente de la question et `relation=exact` incompatible avec plusieurs membres.
- Le patch minimal decision/relation est correctement juge ineligible parce que la citation est aussi rejetee. La reparation complete prend 18,4 s, passe la decision a `accept` mais recopie sans changement la mauvaise citation, le mauvais en-tete et la relation `exact`. Son retry de 28,7 s reproduit exactement la meme sortie.
- Le dernier contrat combine de 20,5 s rend encore `clarify`, des pseudo-ancres et aucune entree exploitable. L'adjudication colonne n'est donc pas atteinte.
- Diagnostic : le probleme n'est plus la decision hors vocabulaire du live 25, mais la regeneration complete d'un objet dont les cinq labels sont deja valides. Le petit modele recopie ses metadonnees erronees quand on lui redemande aussi les labels et tous les champs.
- Correction suivante : generaliser le patch type en patch de metadonnees ligne. Le rapport mecanique determinera uniquement quels champs rejetes doivent etre redemandes parmi decision/raison, en-tete, citation et relation; les labels valides ne seront jamais regeneres. Le LLM choisira toutes les nouvelles valeurs et le code ne fera que les appliquer aux champs autorises puis reverifier l'objet complet.
- Ajouter aussi un controle mecanique generique empechant un en-tete d'etre identique a un membre ou a la concatenation des membres de son propre axe. Ce controle ne connait aucun vocabulaire metier : il distingue seulement le nom d'une dimension de ses valeurs.
- Prochaine preuve discriminante : regression exacte du live 26, validation complete, puis vingt-septieme live. Le patch de metadonnees doit reparer la citation verbatim, la relation, la decision et l'en-tete sans reemettre les labels, puis atteindre la colonne.

### 2026-07-18 - Patch LLM selectif des metadonnees valide avant le vingt-septieme live

- Le patch minimal decision/relation est generalise en `PlannerIntakeRowAxisMetadataPatch`. Il peut demander au LLM uniquement les champs rejetes parmi `decision`, `reason`, `rowHeaderLabel`, `quote` et `relation`; les labels ordonnes deja valides ne figurent jamais dans son schema de sortie.
- La liste `FIELDS_TO_PATCH` est derivee exclusivement des codes du verificateur mecanique. Un probleme de decision autorise decision et raison, un probleme d'en-tete autorise seulement l'en-tete, un probleme de citation seulement la citation et un probleme de relation seulement la relation. Tout probleme de labels ou code inconnu rend cette voie ineligible.
- L'application est elle aussi selective : meme si le LLM renvoie par erreur une valeur pour un champ non autorise, cette valeur est ignoree et la valeur du candidat autoritatif est preservee. Une regression directe prouve notamment qu'une citation et un en-tete parasites ne peuvent pas etre appliques lors d'un patch limite a decision/raison/relation.
- Le verificateur detecte maintenant deux faux en-tetes generiques : un en-tete identique a un membre de l'axe et un en-tete egal a la concatenation ordonnee de tous ses membres. Cette verification utilise seulement la tokenisation canonique et ne contient aucun lexique metier.
- Le prompt exige une citation verbatim contigue, interdit toute reecriture des labels, borne la decision a `accept|clarify`, definit la clarification comme une ambiguite reelle de membership et conserve la regle generique des plages continues a extremites nommees.
- Une unique seconde passe `planner_intake_row_axis_metadata_patch_retry` repart du candidat original et recoit la sortie refusee avec son rapport mecanique. Le code ne corrige aucune valeur semantique et ne fait aucune troisieme tentative locale.
- La regression principale reproduit le live 26 : cinq labels valides, decision `clarify`, en-tete concatene, citation typo et relation `exact`; premier patch encore faux, puis second patch avec `accept`, en-tete dimensionnel, citation exacte et `range_expansion`. Elle prouve ensuite l'acces a la colonne et a la strategie documentaire.
- Validation : build complet en 2 min 59 s puis build incremental en 1 min 49 s, 0 avertissement/0 erreur; 2/2 tests focalises; 284/284 tests SourceBacked; 363/363 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur d'espacement, seulement les avertissements EOL connus.
- Tailles : controleur d'axe 169 lignes, verification ligne 59 lignes, pipeline du patch 160 lignes, prompt 81 lignes et parseur 58 lignes. Les anciens symboles decision/relation ne subsistent plus dans le code ou les tests.
- Prochaine preuve discriminante : vingt-septieme live canonique. Si l'adjudicateur reproduit la panne du live 26, le patch doit modifier exactement les cinq metadonnees demandees, conserver les cinq labels et atteindre `planner_intake_column_axis_adjudication`.

### 2026-07-18 - Vingt-septieme live : ligne acceptee, premier blocage exclusivement colonne

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-181112/`, trace principale `rag-20260718181144911-9b12de0c`, trace SourceBacked `sbrag-2fcef3b7647244f1adb82d4e94b4e33e`, journaux processus `artifacts/live-test-process-20260718-201107/`. Le pipeline termine en environ 5 min 34 s et le test complet en 6 min 06 s, avec une insuffisance sure de 293 caracteres, zero outil et zero source.
- Le routeur termine en 19,2 s, le Planner initial en 84,6 s et l'audit en 67,7 s. L'audit produit encore des ancres concatenees, puis la premiere reparation abandonne ses ancres en 15,5 s.
- L'adjudication ligne termine en 30,7 s avec exactement les cinq jours, un en-tete valable et une citation verbatim, mais `decision=clarify` et relation absente. Le nouveau patch de metadonnees est appele avec seulement decision/raison/relation.
- `planner_intake_row_axis_metadata_patch` termine en 6,2 s, rend une ligne integralement valide et conserve les cinq labels. Aucun retry n'est necessaire. Le correctif du live 26 est donc prouve en conditions reelles.
- `planner_intake_column_axis_adjudication` est atteint pour la premiere fois et termine en 27,4 s. Il identifie cinq candidats : `Petit-dejeuner`, `Dejeuner`, `Diner`, `Souper`, `Collation`. Le verificateur detecte correctement que `Dejeuner` n'a pas d'occurrence independante et chevauche l'occurrence de `Petit-dejeuner`.
- La reparation colonne complete passe la decision a `accept` mais recopie les cinq ancres, donc le chevauchement subsiste. Son retry revient a `clarify` et recopie encore les cinq ancres. Le dernier fallback combine echoue ensuite.
- Diagnostic : la colonne presente maintenant le meme probleme de surface de regeneration que la ligne, mais l'operation semantique requise est encore plus etroite : selectionner un sous-ensemble des ancres candidates deja typees, sans regenerer leurs citations ni relations.
- Correction suivante : ajouter un patch LLM type de selection colonne qui rend `decision`, `reason` et `selectedLabelsExact`. Le LLM decide quels candidats correspondent a des occurrences semantiques distinctes dans la question; le code selectionne mecaniquement les objets ancres existants par identite exacte puis reverifie le contrat complet.
- Le prompt devra expliquer generiquement qu'un fragment imbrique dans l'occurrence d'un autre candidat n'est pas un slot distinct sans occurrence independante. Aucun exemple de repas ou vocabulaire metier ne sera encode. Une unique seconde passe recevra la selection refusee et son rapport.
- Prochaine preuve discriminante : regression exacte du live 27, validation complete, puis vingt-huitieme live. La selection LLM doit conserver quatre ancres distinctes, permettre le Planner de strategie et lancer les premiers outils documentaires de ce nouveau chemin.

### 2026-07-18 - Patch LLM de selection colonne valide avant le vingt-huitieme live

- `PlannerIntakeColumnAxisSelectionPatch` remplace la regeneration complete lorsque les seuls problemes colonne sont l'approbation et un chevauchement d'ancres exactes. Sa sortie type contient uniquement `decision`, `reason` et `selectedLabelsExact`.
- Le LLM reste seul responsable du choix semantique du sous-ensemble. Le prompt lui demande d'identifier les slots distincts directement dans la question, de refuser comme slot autonome un fragment dont la seule occurrence est imbriquee dans un autre candidat et de conserver l'ordre des candidats.
- Le code ne supprime aucun label par heuristique. Il controle seulement que les labels choisis existent exactement parmi les candidats, sont uniques et gardent leur ordre, puis reutilise les objets ancres originaux correspondants. Les citations et relations ne sont jamais regenerees ni normalisees.
- Une selection vide, inconnue, dupliquee ou reordonnee est refusee avant acceptation. Le verificateur canonique complet est ensuite reapplique au sous-ensemble et detecte encore tout chevauchement residuel.
- Une unique seconde passe `planner_intake_column_axis_selection_patch_retry` repart du candidat original, recoit la selection refusee et son rapport mecanique. Une sortie JSON invalide declenche aussi ce retry; aucune troisieme tentative locale n'est permise.
- La regression reproduit le live 27 : adjudication colonne `clarify` avec cinq ancres, premiere selection `accept` gardant encore les cinq labels et donc refusee, puis retry selectionnant quatre labels distincts. Elle prouve l'acces au Planner de strategie avec exactement quatre requetes.
- Le test d'architecture prouve en plus que l'application du patch reutilise la meme instance d'ancre candidate, garantissant la fidelite de citation et de relation.
- Le retry du patch de metadonnees ligne est egalement retabli en cas de JSON invalide, sans modification de sa frontiere semantique.
- Validation : build complet en 3 min 15 s, 0 avertissement/0 erreur; 2/2 tests focalises; 284/284 tests SourceBacked; 363/363 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur d'espacement, seulement les avertissements EOL connus.
- Tailles : controleur d'axe 174 lignes, pipeline selection colonne 163 lignes, prompt colonne 66 lignes, pipeline patch ligne 170 lignes et parseur 72 lignes. Tous restent sous 200 lignes.
- Prochaine preuve discriminante : vingt-huitieme live canonique. La selection colonne doit produire quatre ancres sans chevauchement, puis la trace doit montrer une strategie acceptee et au moins un appel `rag.search` ou `rag.multi_search` reel.

### 2026-07-18 - Vingt-huitieme live : audit initial correct, adjudicateur ligne abandonne tous les candidats

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-182828/`, trace principale `rag-20260718182900085-022cc583`, trace SourceBacked `sbrag-da512991dd694b4a900b79c74ff5804a`, journaux processus `artifacts/live-test-process-20260718-202824/`. Le pipeline termine en environ 6 min 41 s et le test complet en 7 min 12 s, avec une insuffisance sure de 293 caracteres, zero outil et zero source.
- Le routeur termine en 22,1 s, le Planner initial en 95,0 s et l'audit en 85,6 s. Cette fois l'audit propose directement les cinq bons jours et les quatre bonnes colonnes, sans candidat `Dejeuner` imbrique.
- La reparation generale d'ancrages prend 41,6 s et reste invalide. L'inspection du flux confirme toutefois que l'adjudication par axe utilise bien le premier audit, pas cette reparation refusee : les deux opinions LLM initiales restent donc disponibles dans son prompt.
- Malgre ces cinq candidats ligne corrects, l'adjudicateur ligne termine en 42,6 s avec `labels=[]`, en-tete vide et `clarify`. Le blocage de ce live est donc un abandon des candidats par le petit modele, pas une perte de contexte en code.
- La reparation complete de 18,9 s invente ensuite les labels anglais `Monday` a `Friday`, un en-tete anglais et une citation typo `fasse`. Son retry de 46,2 s recopie exactement cette sortie; les deux sont refuses. Le fallback combine echoue en 46,3 s.
- Le patch de selection colonne ajoute avant ce live n'est pas exerce, car la ligne ne passe pas. Sa validation deterministe reste verte mais aucune preuve live n'est encore revendiquee.
- Diagnostic : lorsque l'adjudicateur rend trop peu de labels alors que ses opinions LLM amont contiennent deja un ensemble candidat complet, la reparation complete repart d'une sortie vide et perd le meilleur support semantique disponible.
- Correction suivante : ajouter un patch LLM type de selection des membres ligne. Il exposera separement le pool de labels produit par les opinions LLM amont et demandera au LLM de choisir `selectedLabelsExact`, decision, raison, en-tete, citation et relation. Le code verifiera seulement que la selection est un sous-ensemble exact, unique et ordonne du pool, puis reutilisera ces valeurs LLM sans traduction ni heuristique.
- Cette voie sera eligible uniquement pour une cardinalite ligne insuffisante avec au moins deux candidats LLM amont. Si le pool est vide ou lui-meme insuffisant, la reparation complete actuelle restera le fallback.
- Prochaine preuve discriminante : regression exacte du live 28, validation complete, puis vingt-neuvieme live. Le patch ligne doit selectionner les cinq candidats francais, puis permettre au patch colonne ou a la colonne deja correcte d'atteindre la strategie documentaire.

### 2026-07-18 - Patch LLM de selection des candidats ligne valide avant le vingt-neuvieme live

- `PlannerIntakeRowAxisCandidateSelectionPatch` est maintenant eligible lorsqu'une adjudication ligne rend moins de deux labels alors que les opinions LLM amont contiennent au moins deux candidats ligne.
- Les candidats de `IntakeDecision.RowLabels` et des `StructureAnchorGroups` ligne restent exposes separement au LLM. Le code construit seulement un pool borne par union exacte pour verifier la sortie; il ne choisit, ne traduit ni ne classe aucun membre semantiquement.
- Le patch LLM rend `decision`, `reason`, `rowHeaderLabel`, `quote`, `relation` et `selectedLabelsExact`. Il doit selectionner uniquement des chaines exactes des pools amont, dans leur ordre, et decider depuis la question si la plage implique tous les membres intermediaires.
- Le contrat mecanique partage avec la colonne refuse toute selection vide, inconnue, dupliquee ou reordonnee. L'application reutilise les chaines du pool original, pas celles emises librement par le patch; une traduction ou une invention ne peut donc pas entrer dans l'intake.
- Cette voie reste ineligible si le pool amont est insuffisant ou si les problemes depassent une cardinalite/mise en forme reparable. Le fallback complet existant reste alors disponible.
- Une unique seconde passe repart de l'adjudication originale et recoit la selection refusee avec son rapport. Le JSON invalide declenche aussi ce retry; aucune troisieme tentative de selection n'est autorisee.
- La regression reproduit le live 28 : adjudicateur `clarify` avec labels vides, premiere selection traduite `Monday` a `Friday` refusee par cinq codes `unknown_row_candidate_selection_patch_label`, puis retry selectionnant exactement `Lundi` a `Vendredi`. Elle atteint ensuite la selection colonne et quatre requetes de strategie.
- Les tests d'architecture couvrent le prompt, le parseur, la fidelite de la selection et l'absence de vocabulaire metier dans le contrat generique.
- Validation : build complet en 4 min 24 s puis build incremental en 2 min 06 s, 0 avertissement/0 erreur; 2/2 tests focalises; 284/284 tests SourceBacked; 363/363 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur d'espacement, seulement les avertissements EOL connus.
- Tailles : orchestration selection ligne 153 lignes, contrats ligne 68 lignes, prompt ligne 73 lignes, selection colonne 149 lignes, contrat partage 37 lignes et parseur 89 lignes. Tous restent sous 200 lignes.
- Prochaine preuve discriminante : vingt-neuvieme live canonique. Une sortie ligne vide doit etre recuperee par le nouveau patch, puis la colonne doit passer directement ou via son patch de selection et lancer les outils documentaires.

### 2026-07-18 - Vingt-neuvieme live : bons axes amont, adjudication ligne semantiquement permutee

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-185401/`, trace principale `rag-20260718185428957-0deea99d`, trace SourceBacked `sbrag-7b098c77e9f44008a6f887e7d7fdef83`, journaux processus `artifacts/live-test-process-20260718-205356/`. Le pipeline termine en environ 8 min 04 s avec une insuffisance sure de 293 caracteres, zero outil et zero source.
- Le routeur termine en 30,7 s, le Planner initial en 124,5 s et l'audit d'intake en 88,3 s. L'audit contient pourtant les bons groupes d'ancrage : cinq jours en ligne et quatre moments de repas en colonne.
- La premiere reparation d'ancrages abandonne les ancres en 40,1 s. L'adjudication ligne recoit ensuite les opinions amont mais choisit en 101,6 s les quatre moments de repas comme membres de ligne et concatene les cinq jours dans `rowHeaderLabel`.
- Comme quatre labels sont presents, la voie de selection ajoutee apres le live 28 n'est pas eligible : elle etait volontairement limitee au cas `row_axis_requires_multiple_labels`. Le patch de metadonnees corrige seulement la relation en 10,3 s et preserve donc cette mauvaise attribution semantique, conformement a sa frontiere.
- L'adjudication colonne identifie correctement les quatre moments de repas en 33,9 s. Sa reparation d'approbation passe en 21,3 s. La fusion mecanique detecte alors justement le chevauchement entre les deux axes (`Petit-dejeuner`, `Souper`) et refuse le mapping.
- Le fallback combine rend encore une clarification sans ancres en 30,6 s. Aucune strategie documentaire ni recherche n'est lancee.
- Diagnostic : le probleme general n'est ni la cardinalite ni la disponibilite des candidats, mais un conflit de roles entre trois opinions LLM. Deux opinions amont assignent les jours a l'axe ligne, tandis que l'adjudicateur assigne les repas au meme axe. Le code ne peut ni ne doit trancher ce conflit semantiquement.
- Correction en cours : rendre le patch de selection ligne eligible lors d'un desaccord exact entre opinions non vides, meme lorsque l'adjudicateur fournit plusieurs labels. Le pool borne contient les labels des trois opinions; le LLM selectionne le role ligne depuis `USER_QUESTION`, et le code controle seulement selection exacte, unicite, ordre et contrat canonique.
- Cette voie ne remplace pas silencieusement un adjudicateur valide : si les opinions s'accordent, elle n'est pas appelee; si la selection LLM echoue alors que le candidat initial n'avait aucun probleme mecanique, celui-ci reste le fallback. Aucun vocabulaire de jour, repas ou cuisine n'est encode.
- Prochaine preuve discriminante : regression exacte du live 29, validation complete, puis trentieme live. La trace doit montrer `planner_intake_row_axis_candidate_selection_patch` apres le conflit, selectionner les cinq membres ligne corrects, accepter les quatre colonnes et entrer enfin dans la strategie documentaire.

### 2026-07-18 - Adjudication LLM des opinions d'axe conflictuelles validee avant le trentieme live

- Le patch de selection ligne considere maintenant trois opinions LLM separees : `IntakeDecision.RowLabels`, les groupes `StructureAnchorGroups` marques `axis=row` et les labels rendus par l'adjudicateur courant. Leur simple desaccord exact est detecte mecaniquement; aucune opinion n'est classee comme semantiquement correcte par le code.
- Le pool de verification est l'union exacte, ordonnee et bornee de ces trois opinions. Le LLM doit choisir les membres de l'axe ligne depuis `USER_QUESTION`; le code refuse seulement les valeurs absentes du pool, dupliquees ou reordonnees, puis reapplique le contrat canonique complet.
- Cette selection est appelee avant le retour d'un candidat sans probleme lorsque des opinions non vides divergent. Si le LLM ne produit aucune selection valide et que l'adjudicateur initial etait mecaniquement valide, le candidat initial reste le fallback : un echec du nouveau juge ne degrade donc pas un chemin auparavant executable.
- La regression principale reproduit le live 29 : les groupes amont offrent cinq membres ligne, l'adjudicateur renvoie quatre membres de l'autre axe avec un en-tete concatene, la premiere selection invente des traductions et est refusee, puis le retry choisit exactement les cinq valeurs candidates amont. La colonne et les quatre requetes documentaires simulees sont ensuite atteintes.
- Le prompt nomme explicitement les trois sorties comme opinions LLM non fiables et rappelle qu'une opinion peut avoir attribue les valeurs de colonne au role ligne. Il ne contient aucun vocabulaire de calendrier, repas, cuisine ou domaine produit.
- Validation : build en 2 min 43 s, 0 avertissement/0 erreur; 2/2 regressions focalisees; 284/284 tests SourceBacked; 363/363 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur d'espacement, seulement les avertissements EOL historiques du depot.
- Tailles : orchestration de selection ligne 173 lignes, contrats de selection 80 lignes, prompt 73 lignes et controleur d'axe 185 lignes. Tous les fichiers du correctif restent sous 200 lignes.
- Prochaine preuve discriminante : trentieme live canonique. Le conflit observe au live 29 doit declencher la selection LLM, produire un axe ligne distinct de l'axe colonne, puis atteindre la strategie et au moins un appel documentaire reel.

### 2026-07-18 - Trentieme live : axes semantiques corrects, copie des labels du patch colonne invalide

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-191428/`, trace principale `rag-20260718191509539-afef5202`, trace SourceBacked `sbrag-ad186c52112240e59b02fdcc75c27824`, journaux processus `artifacts/live-test-process-20260718-211421/`. Le pipeline termine en environ 5 min 24 s avec une insuffisance sure de 293 caracteres, zero outil et zero source.
- Le routeur termine en 25,4 s, le Planner initial en 96,4 s et l'audit en 56,6 s. L'audit fournit cinq jours en ligne et cinq candidats colonne, dont le fragment `Dejeuner` imbrique dans `Petit-dejeuner`.
- L'adjudicateur ligne conserve correctement les cinq jours en 18,5 s. Son unique probleme est `decision=clarify`; le patch de metadonnees passe a `accept` en 9,1 s sans modifier les membres. Comme les opinions ligne concordent, la nouvelle voie de conflit reste justement inactive.
- L'adjudication colonne termine en 39,2 s avec cinq candidats et les deux problemes attendus : approbation et chevauchement exact. Le patch de selection colonne est donc appele en live pour la premiere fois.
- Sa premiere sortie ajoute le prefixe textuel `label=` a chacun des cinq libelles dans `selectedLabelsExact`. Le contrat refuse les cinq valeurs comme inconnues. Le retry recoit ce rapport mais recopie exactement les memes valeurs prefixees; il est refuse a son tour.
- Le fallback combine termine en 30,5 s avec une clarification et aucun mapping exploitable. La strategie et les outils ne sont pas atteints.
- Diagnostic : le choix semantique par sous-ensemble est au bon endroit, mais demander au petit modele de recopier des libelles exacts avec accents reste une surface de format fragile. Deux tentatives ont choisi la meme liste mais l'ont encodee comme des representations `label=...` plutot que comme les valeurs du contrat.
- Correction suivante : exposer chaque candidat avec un identifiant ordinal type et demander au LLM de rendre uniquement les identifiants selectionnes. Le code resoudra mecaniquement ces identifiants vers les objets ancres originaux et reappliquera le contrat complet. Il ne retirera aucun candidat par heuristique et ne corrigera aucun libelle libre.
- Prochaine preuve discriminante : regression exacte du live 30 avec une premiere selection invalide, puis selection LLM par identifiants gardant quatre objets ancres. Apres validation complete, le trente-et-unieme live doit entrer dans la strategie documentaire et lancer un outil RAG reel.

### 2026-07-18 - Selection colonne par identifiants LLM validee avant le trente-et-unieme live

- Le contrat `SourceBackedIntakeColumnAxisSelectionPatch` ne transporte plus des libelles a recopier. Il contient `selectedCandidateIds`, une liste d'entiers ordinaux propres a la passe de selection courante.
- Le prompt enumere chaque objet immuable avec `CANDIDATE_ID: n`, son libelle, sa citation et sa relation. Le LLM inspecte la question et choisit les identifiants semantiquement pertinents; le code resout seulement ces identifiants vers les objets ancres originaux.
- La validation refuse une liste vide, des identifiants dupliques, hors plage ou reordonnes. Elle ne connait ni le contenu des libelles ni leur domaine. Le verificateur canonique des ancres est ensuite reapplique au resultat complet.
- Le parseur exige de vrais nombres JSON dans `selectedCandidateIds`. Des chaines, objets ou representations `label=...` ne sont pas convertis silencieusement : la selection reste vide, est refusee, puis peut etre reconsideree par l'unique retry LLM.
- La regression reproduit le live 30 : la premiere passe rend encore `selectedLabelsExact` avec cinq valeurs `label=...`, donc le nouveau contrat la refuse. Le retry rend `[1,3,4,5]`, conserve quatre ancres et reutilise les memes instances d'objets, citations et relations avant d'atteindre quatre requetes documentaires simulees.
- Validation : build en 2 min 46 s, 0 avertissement/0 erreur; 2/2 regressions focalisees; 284/284 tests SourceBacked; 363/363 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur d'espacement, seulement les avertissements EOL historiques.
- Tailles : orchestration de selection 129 lignes, contrats de resolution 36 lignes, prompt 69 lignes et parseur d'axes 96 lignes. Aucun vocabulaire de calendrier, repas, cuisine ou domaine produit n'est present dans ces fichiers.
- Prochaine preuve discriminante : trente-et-unieme live canonique. La selection colonne doit rendre des identifiants valides, fusionner un axe 5 x 4 sans chevauchement, entrer dans la strategie puis lancer au moins un outil documentaire reel.

### 2026-07-18 - Trente-et-unieme live : identifiants appliques fidelement, conflit mecanique non localise pour le LLM

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-192950/`, trace principale `rag-20260718193026106-6a2d2e64`, trace SourceBacked `sbrag-d216e600d5a342479d98e82c9005fafd`, journaux processus `artifacts/live-test-process-20260718-212943/`. Le pipeline termine en environ 11 min 12 s avec une insuffisance sure de 293 caracteres, zero outil et zero source.
- Le live est lent mais sans timeout : routeur 51,3 s, Planner 140,2 s, audit 146,9 s, reparation generale 35,3 s, adjudication ligne 67,7 s et colonne 94,7 s.
- Les cinq jours sont preserves puis acceptes par le patch de metadonnees ligne en 17,1 s. L'adjudication colonne rend cinq candidats et le meme chevauchement exact.
- La premiere selection par identifiants termine en 20,9 s avec `selectedCandidateIds=[]` et `clarify`. Le contrat numerique est donc bien actif et refuse cette selection vide sans alterer les ancres.
- Le retry termine en 40,1 s avec `decision=accept` et `[1,2,3,4,5]`. Le code resout correctement ces identifiants vers les cinq objets d'origine; aucun prefixe, accent, libelle, citation ou relation n'est regenere.
- Le verificateur canonique refuse uniquement `overlapping_exact_structure_anchors`, preuve que la representation par identifiants fonctionne mais que le sous-ensemble LLM reste trop large. Le fallback combine echoue ensuite en 54,3 s.
- Diagnostic : le prompt explique generiquement les occurrences imbriquees, mais le rapport transmis au petit modele ne localise pas les candidats en conflit. Il doit inferer seul, parmi cinq ancres, quelle paire partage la meme occurrence; deux passes n'y parviennent pas de maniere fiable.
- Correction suivante : calculer les paires d'identifiants dont les spans exacts se chevauchent dans `USER_QUESTION` avec le meme mecanisme que le verificateur, puis les exposer comme faits mecaniques au LLM. Le LLM reste responsable de choisir quel identifiant conserver; le code ne supprime automatiquement aucun membre.
- Prochaine preuve discriminante : regression exacte du live 31 dans laquelle la premiere selection est vide puis le retry conserve les cinq identifiants. Le prompt de retry doit montrer la paire conflictuelle et une nouvelle selection LLM testee doit produire quatre ancres. Apres validation, le trente-deuxieme live doit atteindre les outils.

### 2026-07-18 - Paires de conflits mecaniques exposees au LLM avant le trente-deuxieme live

- Le pipeline calcule maintenant les conflits deux a deux entre candidats colonne `exact` avec les memes fonctions `TokenizeStructureAnchorText`, `FindStructureAnchorTokenRanges` et `CanAssignNonOverlappingStructureAnchorRanges` que le verificateur canonique.
- Une paire n'est signalee que si ses citations existent dans la question et qu'aucune affectation a deux spans disjoints n'est possible. Cette detection ne compare aucun sens metier et ne choisit aucun gagnant.
- Le prompt rend chaque fait sous la forme `MECHANICAL_OVERLAP_CONFLICT: candidateIds=1,2`. Il interdit seulement de selectionner simultanement les deux identifiants; le LLM decide depuis la question s'il garde le premier, le second ou aucun.
- La meme liste de conflits est presente dans la premiere passe et le retry. Une tentative vide n'efface donc pas le contexte mecanique dont le second LLM a besoin pour former un sous-ensemble.
- La regression reproduit le live 31 avec une premiere selection vide. Elle verifie la paire `1,2` dans les deux prompts, puis un retry LLM `[1,3,4,5]` qui reutilise quatre ancres et atteint les quatre requetes documentaires simulees.
- Validation : build en 2 min 51 s, 0 avertissement/0 erreur; 2/2 regressions focalisees; 284/284 tests SourceBacked; 363/363 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur d'espacement.
- Tailles : orchestration de selection 133 lignes, detection/contrats 73 lignes et prompt 79 lignes. Aucun vocabulaire de calendrier, repas, cuisine ou domaine produit n'est encode.
- Prochaine preuve discriminante : trente-deuxieme live canonique. Le prompt doit journaliser le conflit mecanique, le LLM doit eviter la paire complete et la fusion 5 x 4 doit enfin entrer dans la strategie documentaire puis les outils.

### 2026-07-18 - Trente-deuxieme live : pool ligne amont insuffisant, selection exacte incapable de regenerer l'axe

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-195039/`, trace principale `rag-20260718195112897-ad45360f`, trace SourceBacked `sbrag-91d9b1f00f0d435f8c59beb2f5594227`, journaux processus `artifacts/live-test-process-20260718-215034/`. Le pipeline termine en environ 8 min 20 s avec une insuffisance sure de 293 caracteres, zero outil et zero source.
- Le routeur termine en 24,8 s, le Planner en 109,9 s et l'audit en 112,5 s. Contrairement aux lives 29 a 31, l'audit amont ne fournit aucun ensemble de cinq jours : son seul candidat ligne est `Jour`, cite par le fragment invalide `Jeu`; ses quatre candidats colonne sont en revanche les moments demandes.
- La reparation generale abandonne tous les axes en 30,0 s. L'adjudicateur ligne repart de la question en 80,5 s mais rend `Jour` et les quatre candidats colonne comme cinq membres de ligne, avec `rowHeaderLabel=Jour`.
- La voie de conflit entre opinions se declenche bien. Cependant son pool exact est l'union du seul candidat ligne amont et de la sortie adjudicateur : il ne contient aucun des cinq membres de la plage demandee. Le contrat de selection ne peut donc pas les inventer, conformement a sa frontiere.
- La premiere selection et son retry conservent le meme mauvais ensemble; le second introduit en plus une faute de citation. Le patch de metadonnees tente ensuite de reparer l'en-tete mais le modele recopie `Jour` aux deux passes. Le fallback combine echoue finalement.
- La nouvelle localisation des conflits colonne n'est pas exercee dans ce live, car l'axe ligne n'est jamais valide. Sa validation deterministe reste verte mais aucune preuve live n'est revendiquee ici.
- Diagnostic : deux regimes doivent etre distingues. Si un pool amont complet existe, la selection exacte est la frontiere la plus sure. Si le pool ligne contient moins de deux membres et que la sortie adjudicateur chevauche plusieurs candidats que le meme audit a classes dans l'autre axe, une re-adjudication generative LLM est necessaire avant toute selection exacte.
- Correction suivante : introduire un rejuge LLM de conflit de roles, eligible uniquement dans ce second regime. Il recevra les opinions ligne pauvres, les opinions colonne separees, la sortie rejetee et les chevauchements exacts; il reconstruira librement l'axe ligne depuis `USER_QUESTION`. Le code ne generera aucun membre et reappliquera le contrat canonique complet.
- Prochaine preuve discriminante : regression du live 32 avec pool ligne `Jour`, quatre candidats colonne et sortie ligne melangee. Le rejuge LLM doit produire une ligne valide distincte, puis la colonne doit atteindre la selection avec sa paire mecanique avant la strategie documentaire.

### 2026-07-18 - Re-adjudication LLM generative d'un conflit de roles validee avant le trente-troisieme live

- Une nouvelle voie `planner_intake_row_axis_role_conflict_repair` intervient avant la selection exacte et avant le retour d'un adjudicateur mecaniquement valide. Elle est eligible uniquement lorsque l'union des deux opinions ligne amont contient moins de deux valeurs et que la sortie ligne courante recopie au moins deux valeurs presentes dans les opinions colonne du meme audit.
- Le declencheur ne connait aucun domaine : il compare des roles types `row` et `column`, des chaines normalisees, des cardinalites et des intersections exactes. Aucun jour, repas, calendrier, cuisine ou lexique produit n'est encode dans l'orchestration ou son prompt.
- Le nouveau LLM n'est pas limite au pool amont insuffisant. Il recoit `USER_QUESTION`, les candidats ligne pauvres, les candidats de l'autre axe, les chevauchements exacts et le rapport mecanique, puis reconstruit librement la semantique de l'axe ligne complet. Cette generation est precisement la decision qui ne peut pas etre deleguee au code.
- Le code parse seulement le contrat type `decision/reason/rowHeaderLabel/quote/relation/labels`, reapplique `BuildPlannerIntakeRowAxisProblems` et signale un conflit non resolu si plusieurs libelles restent simultanement dans les deux roles. Il n'invente, ne traduit, ne supprime et ne selectionne aucun membre semantique.
- Une relance LLM unique recoit la sortie rejetee et son rapport exact. En cas de second echec, les fallbacks existants restent disponibles; le nouveau chemin ne transforme donc pas un echec en faux succes silencieux.
- Le prompt initial d'adjudication mono-axe expose maintenant separement `PRIOR_LLM_OTHER_AXIS_CANDIDATES`. Ces valeurs sont explicitement decrites comme une opinion non fiable servant uniquement a reveler un conflit possible; le LLM doit toujours decider le role depuis la question originale.
- La regression du live 32 est reproduite : audit avec le seul candidat ligne `Jour`, quatre candidats colonne, adjudicateur ligne melangeant ces quatre valeurs, premiere re-adjudication qui repete encore le conflit et est refusee, puis retry qui regenere un axe de cinq membres avec citation de plage valide. L'adjudication colonne produit ensuite quatre ancres et la strategie simulee atteint quatre requetes documentaires.
- Validation : build en 4 min 06 s, 0 avertissement/0 erreur; 3/3 regressions focalisees; 284/284 tests SourceBacked; 363/363 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur d'espacement pour les fichiers suivis et zero espace terminal dans les deux nouveaux fichiers. Les avertissements EOL historiques du depot restent inchanges.
- Tailles : orchestration de re-adjudication 187 lignes et prompt 78 lignes. Les deux nouveaux fichiers restent sous 200 lignes et ne contiennent aucun terme de domaine du cas repas.
- Prochaine preuve discriminante : trente-troisieme live canonique. La trace doit montrer le conflit de roles, une reconstruction ligne valide distincte des colonnes, la validation 5 x 4, puis l'entree dans la strategie documentaire et au moins un appel d'outil RAG reel.

### 2026-07-18 - Trente-troisieme live : timeout du Planner initial avant toute adjudication

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-202034/`, trace principale `rag-20260718202105689-62d0498c`, trace SourceBacked `sbrag-77ece1b14d0f425189b2205320053227`, journaux processus `artifacts/live-test-process-20260718-222029/`.
- Le routeur termine normalement en environ 26,8 s et active le pipeline SourceBacked. Le Planner initial demarre avec un prompt de 7 128 caracteres et une limite de 150 000 ms.
- Le serveur local traite les 1 581 tokens du prompt, initialise l'echantillonneur, puis ne journalise aucun token de sortie ni aucune fin de requete. Le pipeline enregistre exactement `planner.timeout` a 150 023 ms puis `planner.late_cancelled`.
- Le run termine en 3 min 29 s avec une clarification sure de 237 caracteres, zero outil, zero requete RAG et zero source. Le test echoue volontairement sur l'absence de source plutot que de presenter cette clarification comme une reponse reussie.
- Ce live ne traverse ni l'audit d'intake, ni l'adjudication des axes, ni la nouvelle re-adjudication de conflit. Il ne fournit donc aucune preuve positive ou negative sur le correctif du live 32; son echec est entierement en amont.
- Le runtime etait seul, le prompt tenait dans le contexte de 4 096 tokens et aucun processus concurrent n'a ete lance. Les quatre lives precedents avaient franchi le Planner; un second live propre est donc la prochaine verification proportionnee avant toute modification de code ou de timeout.
- Prochaine preuve discriminante : trente-quatrieme live canonique sans recompilation ni changement semantique. Si le Planner repond, reprendre le critere 5 x 4 puis outil reel. Si le meme gel se repete, isoler le transport/runtime local avant de modifier le pipeline.

### 2026-07-18 - Trente-quatrieme live : gel du Planner initial reproduit

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-202705/`, trace principale `rag-20260718202753090-5fd12476`, journaux processus `artifacts/live-test-process-20260718-222645/`.
- Le runtime est relance a froid sans recompilation ni changement de contrat. Le routeur repond correctement en environ 40,6 s et active de nouveau le pipeline SourceBacked.
- Le Planner traite encore les 1 581 tokens de son prompt et initialise l'echantillonneur. Comme au live 33, le serveur ne journalise ensuite ni fin de requete ni statistique de tokens de sortie avant l'annulation a 150 s.
- Le run termine en 4 min 01 s avec la meme clarification sure de 237 caracteres, zero outil et zero source. Le nouveau chemin de conflit d'axes n'est toujours pas atteint.
- Deux gels consecutifs au meme appel rendent l'incident reproductible. Une troisieme relance identique n'apporterait plus d'information; la prochaine etape est d'isoler la borne de generation et le transport de la completion Planner locale.
- Frontiere d'architecture : cette correction ne doit ni fabriquer de plan ni prendre de decision semantique. Elle peut borner mecaniquement une sortie JSON attendue, journaliser la raison d'arret, parser le contrat et laisser les reparations LLM existantes traiter une sortie incomplete.
- Prochaine preuve discriminante : regression du budget de sortie par etape, build et suites completes, puis live 35. Le Planner doit terminer sous sa limite et rendre un JSON parsable avant que l'analyse des axes puisse reprendre.

### 2026-07-18 - Budget de transport du Planner initial calibre avant le trente-cinquieme live

- Les journaux `llama-server` des quatre derniers Planners live reussis donnent une mesure directe : 422 tokens en 124,4 s, 312 tokens en 96,3 s, 325 tokens en 140,1 s et 304 tokens en 109,9 s. La precedente borne de 640 tokens pouvait donc depasser 150 s lorsqu'une generation n'emettait pas spontanement de fin.
- `SourceBackedLlmOutputBudget` reconnait maintenant le marqueur de ligne exact `SAAIA_SOURCE_BACKED_STEP=Planner` et lui attribue 480 tokens. Cette borne conserve 58 tokens de marge au-dessus du maximum live observe. Les marqueurs specialises tels que `PlannerIntakeReview`, `PlannerFormatRepair` et `PlannerDiversityRepair` restent a 640 tokens.
- La detection exacte controle la fin du marqueur par une fin de ligne ou une fin de prompt. Elle ne confond donc plus le Planner initial avec les sous-etapes dont le nom commence par `Planner`.
- La politique de timeout attribue 210 s uniquement a `step=Planner,eventName=planner`. Les autres sous-etapes Planner restent a 150 s, et les timeouts des juges, writers et reparations ne changent pas.
- Cette correction agit uniquement sur un budget de transport et une horloge. Elle ne genere aucun plan, ne choisit aucune requete, ne modifie aucun contrat semantique et laisse au LLM toute l'orchestration du contenu.
- Deux adaptateurs sont couverts : le test de `RagChatAgent.LlmAdapter` verifie 480 pour le Planner exact et 640 pour les marqueurs specialises; un nouveau test HTTP de `OpenAiCompatLlmClient` inspecte les deux payloads `max_tokens` reels.
- Validation : build en 2 min 26 s, 0 avertissement/0 erreur; 5/5 tests discriminants; 284/284 tests SourceBacked; 364/364 tests SourceBacked/OpenAI/ApiClient, le nouveau total incluant la regression HTTP; `git diff --check` sans erreur d'espacement suivie et zero espace terminal dans les cinq fichiers controles.
- Tailles : budget 97 lignes et politique de timeout 94 lignes. Les changements restent localises et sous 100 lignes par fichier de production.
- Prochaine preuve discriminante : trente-cinquieme live canonique. Le serveur doit terminer le Planner initial au plus tard a la borne de 480 tokens dans sa fenetre de 210 s, puis le pipeline doit reprendre l'audit d'axes et tenter le mapping 5 x 4 jusqu'aux outils.

### 2026-07-18 - Trente-cinquieme live : Planner debloque, sous-selection colonne acceptee

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-204253/`, trace principale `rag-20260718204326386-5c20b909`, trace SourceBacked `sbrag-81ac8bacc4064ef6a8e7d42db16d4b7f`, journaux processus `artifacts/live-test-process-20260718-224247/`.
- Le Planner initial termine en 60,0 s avec 291 tokens et 1 090 caracteres. Le journal affiche la nouvelle limite de 210 000 ms; la borne de 480 tokens n'est pas atteinte. La correction de transport des lives 33-34 est donc validee en live.
- L'audit d'intake rend cinq lignes sous une mauvaise relation `exact` et cinq colonnes incluant un fragment imbrique. La reparation compacte abandonne les ancres, puis l'adjudication mono-axe reprend correctement.
- L'adjudicateur ligne produit `Lundi` a `Vendredi`; son seul probleme est la relation absente. Le patch de metadonnees ajoute la relation et preserve exactement les cinq membres.
- L'adjudicateur colonne produit cinq candidats et le verificateur localise le chevauchement entre `Petit-dejeuner` et `Dejeuner`. La premiere selection par ID rend une clarification vide.
- Le retry selectionne seulement les ID correspondant a `Souper` et `Collation`. Cette liste est non vide, ordonnee et sans chevauchement; le contrat actuel l'accepte donc, bien qu'elle ait aussi supprime `Diner` et tous les candidats de la paire conflictuelle.
- Le mapping 5 x 2 entre ensuite dans la strategie. La reparation generale fusionne ses requetes, le retry omet `targetFacetExact`, la revue de qualite clarifie et le plan compact assigne des jours plutot que les deux facettes. Aucun plan executable ni outil n'est atteint.
- Le run termine en 9 min 30 s avec une insuffisance sure de 298 caracteres, zero outil et zero source. Le gain Planner et l'axe ligne sont reels, mais aucune reponse produit n'est revendiquee.
- Diagnostic : le patch de selection colonne est une reparation differentielle d'un conflit localise. Les candidats qui n'appartiennent a aucune paire de conflit doivent etre preserves mecaniquement; le LLM reste responsable de choisir quel membre de chaque paire garder. Accepter la suppression d'ID hors conflit elargit indument le mandat du patch.
- Correction suivante : ajouter au contrat et au prompt la liste des `REQUIRED_UNCONFLICTED_CANDIDATE_IDS`. Refuser toute selection qui omet un de ces ID; ne jamais choisir automatiquement entre les ID conflictuels.
- Prochaine preuve discriminante : regression exacte du live 35 ou une selection `[4,5]` est refusee pour ID hors conflit manquant, puis un retry `[1,3,4,5]` valide quatre ancres, construit 20 cellules et atteint quatre requetes documentaires simulees avant le live 36.

### 2026-07-18 - Preservation differentielle des candidats colonne validee avant le trente-sixieme live

- Le contrat de selection recoit maintenant les paires de conflit deja calculees et derive deux obligations purement mecaniques : chaque ID n'appartenant a aucun conflit doit rester selectionne, et exactement un ID de chaque paire conflictuelle doit rester selectionne.
- Une omission hors conflit produit `missing_required_unconflicted_column_candidate_id`. Une paire entierement supprimee produit `unresolved_column_conflict_selection_gap`; une paire conservee en entier produit `conflicting_column_candidate_ids_selected_together` avant meme la reapplication du contrat canonique.
- Le code ne choisit jamais lequel des deux ID conflictuels conserver. Le prompt enumere `REQUIRED_UNCONFLICTED_CANDIDATE_IDS`, rappelle le caractere differentiel de la reparation et confie explicitement le choix dans chaque paire au LLM depuis `USER_QUESTION`.
- Cette frontiere preserve l'opinion semantique precedente sur tous les candidats qui n'ont cause aucun probleme, tout en laissant le LLM corriger la seule ambiguite localisee. Elle ne contient aucun vocabulaire de repas, calendrier, cuisine ou domaine produit.
- La regression reproduit le live 35 : apres le conflit `1,2`, la premiere selection `[4,5]` est refusee parce qu'elle omet l'ID requis `3` et ne garde aucun membre de la paire. Le retry recoit ces deux diagnostics et rend `[1,3,4,5]`, qui preserve quatre ancres puis atteint quatre requetes documentaires simulees.
- Le test de prompt verifie aussi l'obligation `exactly one id from every MECHANICAL_OVERLAP_CONFLICT` et l'exposition de la liste des ID non conflictuels.
- Validation : build en 4 min 10 s, 0 avertissement/0 erreur; 3/3 regressions focalisees; 284/284 tests SourceBacked; 364/364 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur d'espacement suivie et zero espace terminal dans les cinq fichiers controles.
- Tailles : contrats 100 lignes, orchestration 139 lignes et prompt 87 lignes. Tous les fichiers de production restent sous 140 lignes.
- Prochaine preuve discriminante : trente-sixieme live canonique. Une sortie equivalente a `[4,5]` doit declencher le retry, qui doit produire quatre colonnes, conserver les cinq lignes, former 20 cellules puis atteindre une strategie a quatre facettes et au moins un outil RAG reel.

### 2026-07-18 - Trente-sixieme live : garde-fou differentiel actif, contrat JSON encore trop large

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-210420/`, trace principale `rag-20260718210504874-b6fc97ab`, trace SourceBacked `sbrag-8cca52d272824b6a9670141fc7d374cd`, journaux processus `artifacts/live-test-process-20260718-230412/`.
- Le test termine en 7 min 30 s et le pipeline en 406,3 s avec une insuffisance sure de 293 caracteres, zero outil, zero requete documentaire et zero source. Aucune reponse incomplete ou inventee n'est presentee comme un planning.
- La correction de transport est confirmee une seconde fois : le Planner initial termine en 62,6 s, avec 1 164 caracteres, dans sa nouvelle fenetre de 210 s. Les gels des lives 33 et 34 ne se reproduisent plus.
- L'audit initial rend une grille a deux axes mais utilise des citations agregees invalides. La reparation compacte reste invalide, puis l'adjudication ligne reconstruit correctement `Lundi` a `Vendredi` en 15,1 s; son patch de metadonnees ajoute la relation manquante en 3,4 s.
- L'adjudication colonne retrouve cinq candidats, dont la paire imbriquee deja localisee, mais rend `clarify`. La premiere selection differentielle repond elle aussi par une clarification textuelle sans `selectedCandidateIds`; le nouveau contrat refuse cette liste vide et signale les ID non conflictuels manquants ainsi que la paire non resolue.
- Le retry recoit ces diagnostics, passe sa decision textuelle a `accept`, mais omet encore entierement le tableau d'identifiants. Il est donc refuse pour les memes raisons. Cette preuve montre que la preservation differentielle fonctionne : la chaine ne peut plus accepter silencieusement une grille 5 x 2 comme au live 35.
- Le fallback combine rend ensuite une ancienne forme d'objet et reste invalide. Le pipeline s'arrete avant la strategie, ce qui localise le blocage dans l'execution du micro-contrat par le modele local, pas dans le Planner, la ligne, le RAG ou le writer.
- Diagnostic : demander au LLM de re-rendre la decision, la raison et l'ensemble complet des ID elargit inutilement une reparation localisee. Les candidats hors conflit ont deja ete proposes semantiquement et ne doivent plus faire partie du choix demande a cette sous-etape.
- Prochaine correction : demander au LLM uniquement l'ID retenu dans chaque paire de conflit; le code preservera mecaniquement tous les candidats hors conflit, resoudra les ID choisis vers les objets immuables et reappliquera le contrat canonique. Le LLM reste seul responsable du choix semantique entre les membres de chaque paire.

### 2026-07-18 - Micro-contrat LLM de resolution des conflits colonne valide avant le trente-septieme live

- `PlannerIntakeColumnAxisSelectionPatch` rend maintenant un seul champ : `selectedConflictCandidateIds`. Il ne re-rend plus `decision`, `reason` ni tous les candidats non conflictuels; le format minimal attendu est `{"selectedConflictCandidateIds":[1]}`.
- Le prompt n'expose en detail que les candidats appartenant aux paires localisees. Il indique separement les ID non conflictuels deja preserves par contrat, sans demander au modele de les recopier. Le raw de la tentative precedente n'est plus reinjecte dans le retry, afin de ne pas ancrer le petit modele sur une clarification textuelle rejetee.
- Le LLM inspecte toujours `USER_QUESTION` et choisit exactement un membre de chaque paire. Une liste vide signifie qu'il ne peut pas trancher et maintient le fallback sur; aucune heuristique de domaine ne choisit le gagnant.
- Le code calcule seulement l'union ordonnee entre les candidats hors conflit et les ID conflictuels choisis par le LLM. Il refuse les ID inconnus, dupliques, reordonnes, non conflictuels ou toute paire avec zero/deux gagnants, puis reutilise les objets label/citation/relation originaux et le verificateur canonique.
- Le budget de sortie exact de cette micro-etape est borne a 256 tokens au lieu du budget Planner generique de 640. Cette borne agit sur le transport seulement; le JSON utile attendu tient en quelques tokens.
- La regression principale reproduit la sortie du live 36 avec `decision/reason` mais aucun champ de selection. Cette ancienne forme est parsement vide et refusee; le retry au nouveau format choisit l'ID `1`, le code preserve les ID `3,4,5`, reconstruit quatre colonnes et atteint quatre requetes documentaires simulees.
- Les tests d'architecture verifient aussi que l'ancien champ `selectedCandidateIds` n'est pas converti silencieusement et que le prompt ne recopie pas le raw rejete. Les tests de transport couvrent la borne 256 dans l'adaptateur et dans le payload HTTP compatible OpenAI.
- Validation : build complet en 4 min 13 s puis build incremental en 42 s, 0 avertissement/0 erreur; 5/5 preuves focalisees; 284/284 tests SourceBacked; 364/364 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur d'espacement, seulement les avertissements EOL historiques.
- Prochaine preuve discriminante : trente-septieme live canonique. Le LLM doit rendre un ID conflictuel valide, la fusion doit produire cinq lignes et quatre colonnes, puis la strategie doit lancer au moins un outil RAG reel. Une entree dans les outils ne suffira pas encore : la qualite des candidats, des preuves, des 20 cellules et des cartes de sources devra ensuite etre inspectee.

### 2026-07-18 - Trente-septieme live : cinq bonnes lignes bloquees par un titre qui recopie leurs membres

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-213044/`, trace principale `rag-20260718213118000-936cbe4c`, trace SourceBacked `sbrag-abc67978838e4507b4bc3f5a7773553c`, journaux processus `artifacts/live-test-process-20260718-233040/`.
- Le test termine en 3 min 58 s et le pipeline en 204,8 s avec une insuffisance sure de 293 caracteres, zero outil et zero source. Le correctif de selection colonne n'est pas atteint et n'est donc pas evalue par ce run.
- Le routeur termine en 16,7 s, le Planner initial en 63,6 s et l'audit d'intake en 51,5 s. Le Planner franchit ainsi son appel sur trois lives consecutifs depuis le correctif de transport.
- L'audit propose les cinq bons jours et cinq candidats colonne mais conserve des citations agregees invalides. La reparation compacte est rejetee, puis l'adjudicateur ligne reconstruit exactement `Lundi` a `Vendredi`, avec citation et relation `range_expansion` valides, en 19,8 s.
- Le seul probleme restant sur cette decision ligne est `row_header_repeats_axis_members` : `rowHeaderLabel` contient `Lundi | Mardi | Mercredi | Jeudi | Vendredi` au lieu de nommer leur dimension.
- Le patch de metadonnees est correctement limite au seul champ `rowHeaderLabel`, mais sa premiere sortie recopie la meme liste en 5,1 s. Le retry recoit le rapport exact et rend encore la meme liste sans espaces en 2,6 s. Les deux sont refuses.
- Le fallback combine rend ensuite une ancienne forme d'ancres sans contrat complet et reste invalide. La colonne, la strategie et les outils ne sont pas appeles.
- Diagnostic : le format mono-champ fonctionne, mais le nom `rowHeaderLabel`, l'exposition immediate de la liste et la reinjection du raw rejete ancrent le petit modele sur les valeurs plutot que sur leur role semantique commun.
- Correction suivante : separer ce cas du patch de metadonnees general. Un prompt `RowAxisHeaderRolePatch` demandera uniquement `rowDimensionRole`, exposera les valeurs et sorties interdites, ne reinjectera pas le raw rejete et laissera le LLM nommer la dimension depuis la question.

### 2026-07-18 - Nom semantique de dimension ligne valide avant le trente-huitieme live

- Lorsque `rowHeaderLabel` est le seul champ invalide, le pipeline utilise maintenant le marqueur specialise `PlannerIntakeRowAxisHeaderRolePatch` au lieu du patch de metadonnees general.
- Le contrat LLM ne contient qu'un champ `rowDimensionRole`. Le LLM recoit la question, les valeurs ordonnees immuables, le titre courant interdit et chaque valeur interdite comme titre individuel; il doit produire un court groupe nominal dans la langue de la question.
- Le prompt interdit les noms de mise en page generiques et les concatenations, mais ne fournit aucun terme de domaine attendu. Le code ne genere jamais `Jour`, `Periode` ou un autre titre : il parse la decision LLM, l'applique au seul champ et relance `BuildPlannerIntakeRowAxisProblems`.
- Le retry ne voit plus le raw rejete. Il recoit seulement le fait qu'une tentative a ete refusee et les codes mecaniques; cela evite de reamorcer la copie de la liste fautive.
- Le parseur accepte `rowDimensionRole` comme representation specialisee de `RowHeaderLabel`. Les autres reparations multi-champs conservent leur contrat historique `rowHeaderLabel`, sans elargissement implicite.
- Le budget exact de cette micro-etape est borne a 256 tokens. Comme pour la selection de conflit colonne, cette borne transporte une petite decision LLM sans intervenir sur son contenu semantique.
- La nouvelle regression reproduit le live 37 : adjudicateur avec cinq bons membres et titre concatene, premiere reparation recopiant encore ce titre, rejet `row_header_repeats_axis_members`, puis retry `rowDimensionRole` valide. Le pipeline obtient ensuite 20 cellules et quatre requetes documentaires simulees.
- Validation : build en 4 min 04 s, 0 avertissement/0 erreur; 5/5 preuves focalisees; 285/285 tests SourceBacked; 365/365 tests SourceBacked/OpenAI/ApiClient. Les fichiers de prompt et d'orchestration restent respectivement a 145 et 170 lignes.
- Prochaine preuve discriminante : trente-huitieme live. Le titre LLM doit passer sans recopier les membres, la colonne doit atteindre le micro-selecteur conflictuel, puis le pipeline doit construire le 5 x 4 et entrer dans les outils reels.

### 2026-07-19 - Trente-huitieme live : ligne valide, cinq libelles colonne avec metadonnees inutilisables

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-214726/`, trace principale `rag-20260718214756166-0c8f79e9`, trace SourceBacked `sbrag-c76c944d50bc475fa577e96f7e13cde4`, journaux processus `artifacts/live-test-process-20260718-234721/`.
- Le pipeline termine en 298,5 s avec une insuffisance sure de 293 caracteres, zero outil et zero source. Le Planner initial termine en 52,9 s, soit un quatrieme passage consecutif sans gel depuis la correction de transport.
- L'audit d'intake termine en 45,4 s et les reparations conduisent a un axe ligne entierement valide : cinq jours, titre LLM `Jours de la semaine`, citation et relation `range_expansion`.
- L'adjudicateur colonne rend cinq bons libelles, mais une citation de phrase entiere pour le premier et quatre citations vides sous `alternative_group`. Le rapport contient donc plusieurs defauts de citation/relation, au-dela du simple chevauchement localise.
- Le selecteur de conflits reste correctement ineligible : il ne doit pas accepter un sous-ensemble dont les objets d'ancrage sont eux-memes invalides. La reparation generale et son retry recopient exactement les memes metadonnees, puis le fallback combine echoue.
- Diagnostic : les libelles candidats sont exploitables et ne doivent plus etre regeneres. En revanche selection, citation et relation doivent etre re-adjuges par le LLM sous un contrat type, avec resolution mecanique des identifiants vers les libelles originaux.

### 2026-07-19 - Patch LLM d'ancres colonne par identifiants valide avant le trente-neuvieme live

- Une nouvelle voie `PlannerIntakeColumnAxisAnchorPatch` est eligible lorsque le pool contient au moins deux libelles non vides et uniques, et qu'au moins deux erreurs mecaniques concernent citations ou relations. Les cas d'approbation seule restent sur leur chemin existant.
- Le LLM recoit `USER_QUESTION` et chaque libelle immuable sous `candidateId`. Il choisit les slots distincts et rend uniquement `anchors:[{candidateId,quote,relation}]`; il peut donc exclure un fragment imbrique et reconnaitre une expression d'alias sans recopier ni traduire les libelles.
- Le code resout chaque ID vers le libelle original, applique seulement citation et relation, preserve l'ordre, refuse ID inconnu/duplique/reordonne ou moins de deux candidats, puis reapplique le contrat canonique complet.
- Le retry ne recoit pas le raw rejete. Il voit la question, les libelles immuables, les problemes originaux et le rapport de rejet. La sortie reste entierement une decision semantique LLM; aucune extraction automatique de repas ou de jour n'est introduite.
- Le budget de cette etape est borne a 480 tokens, suffisant pour un tableau compact de quatre ou cinq objets tout en evitant une generation Planner de 640 tokens sans fin.
- La regression combine les lives 37 et 38 : titre ligne concatene puis repare, adjudicateur colonne avec phrase entiere et citations vides, premiere ancienne sortie sans ID refusee, retry avec ID `1,3,4,5`, citations verbatim et alias. Le pipeline construit ensuite 20 cellules et atteint quatre requetes documentaires simulees.
- Validation : build en 2 min 33 s, 0 avertissement/0 erreur; 5/5 preuves focalisees; 285/285 SourceBacked; 365/365 SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur d'espacement. Orchestration 153 lignes, prompt 65 lignes.
- Prochaine preuve discriminante : trente-neuvieme live. La ligne puis la nouvelle reparation d'ancres doivent produire le mapping 5 x 4 et permettre le premier appel RAG reel. Ensuite seulement commencer l'audit des candidats et preuves.

### 2026-07-19 - Trente-neuvieme live : reparation multi-champs ne bascule pas vers le titre specialise

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-220427/`, trace principale `rag-20260718220458940-e8318569`, trace SourceBacked `sbrag-f02885fe7ea642dbaea4ebb388797db2`, journaux processus `artifacts/live-test-process-20260719-000422/`.
- Le pipeline termine en 228,0 s avec une insuffisance sure, zero outil et zero source. Le Planner termine en 59,1 s, cinquieme passage consecutif sans gel.
- L'audit rend directement les bons groupes 5 x 4 mais un sous-objet intake vide. L'adjudicateur ligne recupere cinq jours corrects avec deux problemes : titre concatene et relation absente.
- Le patch general multi-champs ajoute correctement `range_expansion` mais recopie le titre. Son retry conserve la liste de champs originale et rend `Lundi` comme titre; le contrat specialise `rowDimensionRole` n'est donc jamais appele.
- Diagnostic : une reparation differentielle peut corriger un sous-ensemble de ses champs. Le pipeline doit recalculer les problemes et repartir sur le contrat minimal correspondant au reliquat, plutot que repeter automatiquement le meme schema initial.

### 2026-07-19 - Chaine de reparations partielles validee avant le quarantieme live

- Apres une premiere reparation de metadonnees ligne, le pipeline recalcule maintenant `BuildPlannerIntakeRowAxisProblems` puis `BuildPlannerIntakeRowAxisMetadataPatchFields`.
- Si l'ensemble restant est non vide et strictement different de l'ensemble initial, une nouvelle reparation demarre avec le candidat partiellement corrige et le contrat minimal du reliquat. Une recursion identique est interdite par comparaison ordonnee des champs.
- Dans le cas live 39, la premiere passe corrige `relation`; le reliquat contient uniquement `rowHeaderLabel`, ce qui declenche alors `PlannerIntakeRowAxisHeaderRolePatch`. Le code orchestre les contrats mais le titre reste genere par le LLM.
- La regression combine maintenant titre concatene plus relation vide, premiere sortie `rowHeaderLabel+relation`, verification partielle, puis sortie LLM `rowDimensionRole`. Elle poursuit ensuite la reparation d'ancres colonne par ID et quatre requetes simulees.
- Validation : build en 2 min 32 s, 0 avertissement/0 erreur; 2/2 discriminants; 285/285 SourceBacked; 365/365 SourceBacked/OpenAI/ApiClient.
- Prochaine preuve discriminante : quarantieme live. Le passage general vers le titre specialise doit etre visible si le double probleme se reproduit, puis la colonne et les outils doivent etre atteints.

### 2026-07-19 - Quarantieme live : selecteur colonne valide, bonne selection ligne abandonnee pour metadonnees

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-221528/`, trace principale `rag-20260718221554620-70f2bd8e`, trace SourceBacked `sbrag-c18e00f7982d44119a3d5f4d2fd3aeaa`, journaux processus `artifacts/live-test-process-20260719-001524/`.
- Le pipeline termine en 235,9 s avec zero outil et zero source. Routeur 14,1 s, Planner 44,7 s.
- L'adjudicateur ligne permute les roles et choisit les repas. Le patch de selection ligne retrouve ensuite correctement les cinq jours, mais omet le titre et utilise une citation typo; son retry conserve les jours mais reste invalide.
- Le flux abandonne alors cette selection semantique correcte et applique un patch de metadonnees a l'ancien candidat permute. La colonne est ensuite correctement reduite de cinq a quatre membres par le nouveau micro-selecteur conflictuel en 3,2 s, premiere preuve live positive de ce contrat.
- La fusion finale refuse justement le chevauchement entre repas en ligne et en colonne. Le fallback combine echoue sans outil.
- Diagnostic : selection des membres et validite de leurs metadonnees sont deux contrats differents. Une selection exacte valide ne doit pas etre perdue seulement parce que titre/citation/relation demandent encore une reparation LLM.

### 2026-07-19 - Preservation d'une selection ligne valide pendant les reparations de metadonnees

- Apres chaque patch de selection ligne, le pipeline separe maintenant les problemes propres au sous-ensemble choisi des problemes canoniques de l'axe repare.
- Si le sous-ensemble est exact, unique et ordonne mais que les problemes restants sont tous reparables par metadonnees, le candidat selectionne est transmis a `RepairPlannerIntakeRowAxisMetadataAsync` au lieu de relancer la selection ou de revenir au candidat anterieur.
- Cette transition existe apres la premiere selection comme apres son retry. Le LLM reste auteur des membres, citation, relation et titre; le code ne fait que conserver l'objet choisi et router les codes mecaniques vers le contrat minimal.
- La regression principale conserve sa premiere selection traduite invalide, puis un retry avec cinq jours corrects mais titre vide et citation typo. Elle verifie ensuite patch citation+titre, reliquat titre, `rowDimensionRole`, quatre colonnes et quatre requetes simulees.
- Validation : build en 2 min 30 s, 0 avertissement/0 erreur; 2/2 regressions; 285/285 SourceBacked; 365/365 SourceBacked/OpenAI/ApiClient.
- Prochaine preuve discriminante : quarante-et-unieme live. Si la permutation se reproduit, la bonne selection ligne doit survivre a ses reparations, puis le selecteur colonne deja valide doit permettre d'atteindre les outils.

### 2026-07-19 - Quarante-et-unieme live : intake 5 x 4 valide, affectations de facettes absentes

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-222726/`, trace principale `rag-20260718222757029-753c8183`, trace SourceBacked `sbrag-03d01791f5ed491b82887137c68e9844`, journaux processus `artifacts/live-test-process-20260719-002722/`.
- Le pipeline termine en environ 450,4 s avec une insuffisance sure de 298 caracteres, zero outil et zero source. Le test echoue volontairement sur l'absence de preuve au lieu de presenter la clarification comme une reponse correcte.
- Le Planner termine en 54,9 s. L'axe ligne contient les cinq jours demandes; son seul probleme de relation est repare par le patch de metadonnees. L'axe colonne contient cinq candidats avec le chevauchement localise attendu.
- Le micro-selecteur de conflit colonne repond en 3,2 s avec une sortie de 36 caracteres et conserve quatre colonnes distinctes. Pour la premiere fois, le vrai pipeline accepte ainsi l'intake canonique complet de cinq lignes par quatre colonnes, soit vingt cellules demandees.
- La premiere reparation de strategie rend quatre requetes quasi identiques; la normalisation mecanique supprime trois doublons. Le retry LLM rend ensuite quatre requetes distinctes et executables, respectivement orientees vers les quatre colonnes, mais omet tous les `targetFacetExact`.
- Le contrat rejette exactement `missing_target_facet_assignments` et `uncovered_typed_columns`. La revue LLM plus large retourne une clarification dont les requetes restent sans libelles cibles, puis le plan compact derive vers les lignes et un nom d'outil invalide. Aucun outil n'est execute.
- Diagnostic : les requetes du retry constituent deja quatre opinions semantiques distinctes. Redemander requetes, buts et libelles dans une revue generale elargit le contrat et favorise la derive. La sous-decision restante est seulement la bijection semantique entre quatre requetes immuables et quatre facettes immuables.
- Prochaine correction : micro-contrat LLM `requestId -> facetId`. Le modele voit requete, but et facette, choisit la bijection; le code valide seulement identifiants, bornes, unicite et couverture, resout les libelles immuables puis relance tous les contrats de strategie.

### 2026-07-19 - Bijection semantique LLM requetes-facettes validee avant le quarante-deuxieme live

- Une nouvelle voie `PlannerFacetAssignmentPatch` intervient avant la revue de requetes generale lorsque le plan contient autant de requetes distinctes et executables que de facettes et que ses seuls problemes sont `missing_target_facet_assignments`, `unknown_target_facet_assignments` ou `uncovered_typed_columns`.
- Le LLM recoit des `REQUEST_ID` immuables avec outil, portee, requete et but, ainsi que des `FACET_ID` immuables avec leur libelle exact. Sa seule sortie est `{"assignments":[{"requestId":1,"facetId":2}]}`; il reste seul auteur de la correspondance semantique.
- Le code ne compare aucun mot et ne devine aucun repas. Il verifie seulement le nombre d'affectations, les doublons, les identifiants absents et les bornes, resout chaque `facetId` vers le libelle type d'origine, conserve les requetes byte-for-byte puis reapplique `EvaluatePlannerStrategyCandidate` et tous ses contrats.
- Une relance LLM unique recoit seulement les codes de rejet, jamais le raw precedent. Si les deux micro-sorties restent invalides, la revue LLM generale puis le plan compact existants conservent exactement leur role de fallback; aucun echec n'est transforme en execution silencieuse.
- La regression principale reproduit la sortie du live 41 avec quatre requetes `... sources Cuisine` sans cible. Un mapping ID-only les associe aux quatre facettes, les textes restent inchanges et le plan atteint l'executeur simule avec l'outcome `accepted_after_retry_facet_assignment_patch`.
- Une seconde regression force un mapping incomplet, puis un retry vide. Elle verifie l'absence de reinjection d'un marqueur secret du raw rejete et le retour vers `PlannerFacetQueryReview`. Une regression historique supplementaire couvre le refus de cette revue puis l'acceptation du plan compact.
- Le budget exact de cette micro-etape est borne a 256 tokens dans les deux adaptateurs. Le parser, le prompt generique et la politique de transport sont couverts sans vocabulaire cuisine dans les tests d'architecture.
- Validation : build en 3 min 21 s, 0 avertissement/0 erreur; 5/5 preuves focalisees; 286/286 tests SourceBacked; 366/366 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur d'espacement sur les fichiers suivis touches et zero espace terminal dans les trois nouveaux fichiers.
- Tailles : orchestration 236 lignes, prompt 71 lignes et parser 32 lignes. Le changement reste localise; aucune heuristique produit, calendrier ou cuisine n'a ete ajoutee.
- Prochaine preuve discriminante : quarante-deuxieme live canonique. Apres l'intake 5 x 4, la trace doit montrer `planner_facet_assignment_patch` accepte, quatre `targetFacetExact`, puis au moins un appel RAG reel. Si les requetes sont peu productives, cette qualite sera jugee en aval par le LLM a partir des resultats et non par une heuristique lexicale dans le code.

### 2026-07-19 - Quarante-deuxieme live : premier RAG reel, puis decision de lecture documentaire non executee

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-225318/`, trace principale `rag-20260718225352285-610bc6b8`, trace SourceBacked `sbrag-05cbafcdb8574923aefb95bc3d63b128`, journaux processus `artifacts/live-test-process-20260719-005312/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260719_005334.log`.
- Le test termine en environ 19 min 48 s avec une insuffisance sure de 455 caracteres. Il ne produit ni payload de sources ni carte de source; cette sortie n'est donc pas consideree comme une reponse reussie.
- Le routeur termine en environ 25,5 s, le Planner en 132,4 s. L'adjudication construit cinq lignes `Lundi` a `Vendredi`; le patch de relation passe en 4,6 s. L'adjudication colonne rend directement les quatre facettes demandees. L'intake canonique 5 x 4 est accepte pour un second live consecutif.
- La premiere strategie contient quatre requetes quasi dupliquees et se reduit mecaniquement a une requete. Son retry termine en 90,0 s avec quatre requetes distinctes et executables mais sans facette cible.
- Le premier patch de bijection rend une sortie vide de 18 caracteres et est refuse. Son unique retry rend 129 caracteres et la bijection `1->1, 2->2, 3->3, 4->4`; le code resout les libelles immuables et la strategie complete est acceptee. Il s'agit de la premiere preuve live positive du micro-contrat requete-facette.
- Les quatre recherches RAG initiales sont enfin executees. Une requete expire apres 35 s et trois produisent chacune un hit. Le premier `EvidenceBundle` contient donc trois items; une recherche large demandee au tour suivant ajoute douze hits et porte le second bundle a quatorze items.
- A chacun des deux tours, l'EvidenceJudge rend `decision=answer` mais omet `evidenceIdsToUse`. La revue de statut LLM identifie pourtant semantiquement des preuves utiles puis recommande `read_documents`; au second tour elle choisit explicitement `E1`, `E5` et `E8`.
- Le pipeline n'exploite alors cette memoire que pour les actions `answer` ou `write_partial`. La reparation generale de selection reste invalide, puis la reparation generale d'action derive vers une recherche large au lieu de convertir la decision `read_documents + usefulEvidenceIds` en appels `documents.context`.
- Apres le second bundle, la meme sequence se repete. La reparation d'action ne fournit plus d'appel sur et le pipeline s'arrete avec `no_executable_follow_up`. Le nouveau blocage est donc situe apres la recherche, dans le transport d'une decision LLM de lecture documentaire deja typee.
- Frontiere d'architecture : le code ne doit ni choisir les sources utiles ni forcer l'ecriture. Le LLM a deja choisi l'action et les EvidenceIds; le code peut seulement resoudre ces identifiants vers les ancres immuables `docId/docPath/chunkId/page` et executer l'outil demande.

### 2026-07-19 - Execution mecanique des lectures documentaires decidees par la memoire LLM

- `SourceBackedRagPipeline.AgentWorkingMemoryDocumentReads.cs` transforme maintenant une revue de statut valide avec `recommendedNextAction=read_documents` et des `usefulEvidenceIds` en requetes `documents.context`.
- La decision semantique demeure entierement celle du LLM : l'action et les IDs proviennent de sa memoire de travail typee. Le code verifie seulement que les IDs existent dans l'`EvidenceBundle`, que chaque item expose une ancre concrete, que la lecture n'est pas une repetition mecanique et qu'un tour reste disponible.
- Chaque requete preserve exactement `docId`, `docPath`, `chunkId`, `pageStart`, `pageEnd` et `categoryPath`. Le but de transport mentionne l'`EvidenceId`; aucune comparaison lexicale, note de pertinence ou connaissance de repas n'est introduite.
- Un maximum mecanique de quatre lectures par tour borne l'execution. Les ancres identiques sont dedupliquees sans arbitrer leur contenu. Les IDs inconnus, sans ancre ou deja lus ne sont pas transformes silencieusement en recherche large.
- `RepairAnswerSelectionContractIfNeededAsync` reconnait ce contrat comme un succes de lecture distinct d'une selection prete a ecrire. Il preserve la decision `need_more_evidence` et ses follow-ups au lieu de la faire passer par la normalisation ou la reparation generale de selection.
- La trace `evidence_judge.selection_contract_repaired` expose maintenant `agent_memory_document_reads_used=True`, les IDs utiles de la revue et les requetes documentaires resolues. Les chemins historiques `agent_memory_selection_used` pour `answer/write_partial` restent inchanges.
- La regression reproduit le point exact du live 42 : bundle abondant, EvidenceJudge `answer` sans IDs, revue LLM `read_documents` sur `E1`, `E5`, `E8`, puis trois lectures exactes des documents/pages/chunks correspondants. Aucun prompt `EvidenceJudgeSelectionRepair` ou `EvidenceJudgeActionRepair` n'est appele; le tour suivant selectionne la nouvelle preuve `E13` et produit une reponse verifiee.
- Validation : regression ciblee reussie; 287/287 tests SourceBacked; 367/367 tests SourceBacked/OpenAI/ApiClient; zero espace terminal dans les trois fichiers controles. Le build compile sans avertissement ni erreur.
- Prochaine preuve discriminante : quarante-troisieme live canonique. La trace doit reproduire l'intake 5 x 4 et le mapping de facettes, puis montrer `agent_memory_document_reads_used=True`, un ou plusieurs appels `documents.context`, une selection de preuves approfondies et enfin une reponse de vingt cellules verifiee avec cartes de sources. Si l'EvidenceJudge ou le writer reste insuffisant apres les lectures, le prochain correctif devra cibler ce contrat aval sans revenir a des heuristiques metier.

### 2026-07-19 - Quarante-troisieme live : audit d'intake coupe a sa borne avant le correctif aval

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-233518/`, trace principale `rag-20260718233550056-7b60b998`, trace SourceBacked `sbrag-63dab427d68f4a97a5e48a8dfb7ebdf8`, journaux processus `artifacts/live-test-process-20260719-013513/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260719_013533.log`.
- Le run termine en environ 6 min 55 s avec une insuffisance sure de 293 caracteres, zero outil et zero source. Il ne traverse pas la strategie, l'EvidenceBundle ou le nouveau transport `read_documents`; il ne constitue donc ni une validation ni une invalidation de ce correctif aval.
- Le routeur termine en environ 21,4 s. Le Planner initial termine normalement en 104,5 s avec 291 tokens serveur et 1 090 caracteres de reponse; sa borne de transport a 480 tokens et 210 secondes reste stable.
- `PlannerIntakeReview` traite son prompt de 988 tokens puis genere 346 tokens a seulement 2,38 tokens/s. L'appel atteint exactement sa limite generique de 150 000 ms et est annule avant que le pipeline recoive un objet JSON utilisable.
- La premiere reparation compacte termine en 52,0 s mais rend un ancien dictionnaire de 25 couples jour/creneau sans le contrat type. Elle est refusee avec `no_anchor_entries` et les problemes canoniques de revue.
- La reparation de contrat termine en 53,6 s mais rend encore une ancienne forme : `decision/kind/reason`, un objet `rowHeaderLabel`, un objet `columnAnchors` et une clarification injustifiee. Elle est refusee pour absence d'approbation, de lignes et de colonnes typees.
- Le pipeline ne convertit aucun de ces objets en axes, ne genere aucun repas et s'arrete avec `The LLM intake auditor did not approve a mechanically executable output structure after focused contract repairs.` Ce comportement est sur mais ne satisfait pas la demande.
- Diagnostic : ce run n'expose pas une nouvelle decision semantique manquante. Il montre une completion de l'audit deja a 346 tokens, coupee exactement par une borne de transport trop courte pour la vitesse live observee, alors que le meme appel reste sur le budget generique Planner de 640 tokens.

### 2026-07-19 - Borne de transport dediee a l'audit d'intake avant le quarante-quatrieme live

- `SourceBackedLlmOutputBudget` reconnait maintenant exactement `SAAIA_SOURCE_BACKED_STEP=PlannerIntakeReview` et borne cette seule completion a 480 tokens au lieu du plafond Planner generique de 640.
- `SourceBackedLlmStepTimeoutPolicy` accorde 240 secondes au seul evenement `planner_intake_review`, contre 150 auparavant. Le Planner initial reste a 210 secondes; les autres sous-etapes Planner restent a 150 secondes sauf leurs exceptions deja mesurees.
- Ce changement ne modifie ni prompt, ni parseur, ni contrat, ni axe. Il agit uniquement sur le transport d'une decision LLM et laisse les verificateurs mecaniques refuser une sortie incomplete ou invalide.
- Calibrage : le Live43 a consomme environ 150,1 s pour 346 tokens. Le couple 480 tokens/240 s borne encore une generation sans EOS tout en donnant une marge realiste au contrat type complet sur le runtime local 3B.
- Les adaptateurs natif et compatible OpenAI sont couverts : le payload `PlannerIntakeReview` transporte maintenant `max_tokens=480`; le plafond initial Planner reste 480 et les micro-contrats 256/480 conservent leurs valeurs.
- Validation : build en 3 min 25 s, 0 avertissement/0 erreur; 2/2 preuves focalisees; 367/367 tests SourceBacked/OpenAI/ApiClient; zero espace terminal detecte dans les fichiers controles.
- Prochaine preuve discriminante : quarante-quatrieme live canonique avec le binaire recompile. L'audit doit terminer sans timeout, produire ou reparer un intake 5 x 4, puis reprendre les criteres aval du live 43 : mapping de facettes, recherches RAG, `agent_memory_document_reads_used=True`, preuves approfondies, vingt cellules et cartes de sources.

### 2026-07-19 - Quarante-quatrieme live : vingt cellules verifiees, puis contrat `revise` incomplet non repare

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260718-235052/`, trace principale `rag-20260718235128378-4bbf07f7`, trace SourceBacked `sbrag-557b6ba10cd148bab2e9678e694762c2`, journaux processus `artifacts/live-test-process-20260719-015044/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260719_015112.log`.
- Le test termine en 16 min 31 s et echoue sur `Assert.NotNull` parce que le payload de sources final est absent. La reponse visible est une insuffisance sure de 677 caracteres; elle ne doit pas etre classee comme reussite fonctionnelle.
- Le routeur termine en environ 14,8 s et le Planner initial en environ 96,0 s. `PlannerIntakeReview`, auparavant coupe a 150 s, termine maintenant en environ 48,8 s avec 1 259 caracteres : le correctif de transport 480 tokens/240 s est donc prouve sur le runtime reel.
- L'audit initial rend encore des ancres imparfaites, mais les micro-contrats LLM recuperent cinq lignes `Lundi` a `Vendredi`, un titre de ligne repare et cinq candidats colonne. Le selecteur par identifiants conserve `Petit-dejeuner`, `Diner`, `Souper` et `Collation`; l'intake canonique 5 x 4 est accepte.
- La premiere strategie large est refusee. Son retry rend quatre requetes distinctes `Petit-dejeuner Cuisine`, `Diner Cuisine`, `Souper Cuisine`, `Collation Cuisine`. Le premier `PlannerFacetAssignmentPatch` est accepte en environ 8,2 s avec `1->1, 2->2, 3->3, 4->4`.
- Les quatre recherches RAG sont executees. La recherche petit-dejeuner expire, tandis que les trois autres rendent des resultats; le premier `EvidenceBundle` contient douze items.
- Le premier EvidenceJudge choisit `answer` sans aucun ID. La revue de statut LLM recommande `read_documents`, marque `E1,E3,E4,E5` utiles et `E2,E6,E7,E8` faibles.
- La trace montre `agent_memory_document_reads_used=True` et quatre appels `documents.context` resolus depuis les IDs choisis par le LLM : `Cuisine/si-on-cuisinait.pdf` page 7, `Cuisine/facilitemps.pdf` page 36 et `Cuisine/chefbot_livre_de_recettes_fr.pdf` pages 15 et 14.
- Le second `EvidenceBundle` contient 35 items. Le second EvidenceJudge choisit encore `answer` sans IDs; apres la borne d'iteration, une nouvelle revue de statut recommande `write_partial` et selectionne `E1,E13,E2,E3,E33,E34,E35`. Le pipeline utilise cette selection LLM avec `agent_memory_selection_used=True`.
- Le writer termine en environ 79,2 s. Sa sortie brute compte 1 473 caracteres; le draft final compte 1 191 caracteres, vingt claims audites et les citations visibles `E1,E2,E13,E3`.
- `SourceContractVerifier` accepte le draft : zero code d'erreur, zero citation inconnue, zero cellule manquante ou trop mince. Cette verification prouve la forme et le contrat de source, pas encore l'adequation semantique finale.
- `AnswerAdequacyJudge` termine en environ 70,8 s et choisit `revise`. Il fournit les vingt `auditedClaimRefs`, mais `has_revision=False`, zero follow-up et aucune `revisedAnswer`. Ses raisons etendent en outre la demande vers ingredients, quantites, preparation et ustensiles, bien que le prompt interdise d'exiger ces details non demandes.
- L'ancien predicate ne traitait pas `revise` sans draft comme un contrat d'action incomplet : `action_contract_repair_attempted=False`. La reparation generale ulterieure ne produit aucun suivi executable et le pipeline termine surement avec `llm_answer_adequacy_failed`, sans cartes de sources.
- La trace confirme aussi la frontiere memoire : `memory_context_present=True` et `memory_finality=context_only_never_final_proof`. Aucun souvenir n'est transforme en preuve; toutes les citations finales proviennent de l'`EvidenceBundle` courant.
- Diagnostic : l'ecart restant est localise apres une reponse de vingt cellules mecaniquement valide. Il ne faut ni contourner le juge ni coder que le plan est adequat. Le LLM doit reparer sa propre decision incomplete sous un micro-contrat qui permet `accept` avec audit complet, `revise` avec une reponse complete, ou `need_more_evidence` avec des appels executables.

### 2026-07-20 - Reparation LLM d'une decision d'adequation `revise` sans `revisedAnswer`

- `RequiresAnswerAdequacyActionContractRepair` reconnait maintenant trois contrats incomplets : `need_more_evidence` sans suivi executable, `revise` sans `RevisedDraft`, et `accept` sans audit complet des claims attendus.
- Dans le cas `revise` incomplet, le pipeline appelle `AnswerAdequacyActionContractRepair`. Le prompt rappelle au LLM que la decision semantique lui appartient et exige l'une des trois sorties completes : audit accepte, `revisedAnswer` complete et citee, ou un a trois suivis RAG/document executables.
- Le code ne transforme pas automatiquement `revise` en `accept`, ne supprime pas les raisons du juge et n'ajoute aucun critere de repas. Il parse la nouvelle opinion LLM, valide uniquement son contrat et conserve l'arret sur si la reparation reste incomplete.
- La regression `Run_reasks_the_llm_when_adequacy_requests_revision_without_revised_answer` reproduit les vingt claims, une premiere decision `revise` avec raison hors perimetre et draft vide, puis une reparation LLM `accept` avec les dix claims attendus dans son scenario generique. Elle exige `action_contract_repair_attempted=True`, `action_contract_repair_accepted=True` et `pre_action_contract_repair_decision=revise`.
- Validation : regression exacte 1/1; suite SourceBacked 288/288; suite SourceBacked/OpenAI/ApiClient 368/368. Une assertion d'architecture a ete alignee sur le nouveau message qui mentionne explicitement l'audit complet, la `revisedAnswer` complete ou les suivis executables.
- Prochaine preuve discriminante : quarante-cinquieme live canonique. La trace doit atteindre de nouveau vingt cellules, puis montrer soit `action_contract_repair_attempted=True` et une reparation acceptee, soit une `revisedAnswer` complete suivie de sa validation independante, soit un suivi documentaire executable. La reussite exige une reponse finale utile, `IsSourceVerified=True`, un payload de sources non nul et des cartes non dupliquees; un simple tableau mecaniquement valide ne suffit pas.

### 2026-07-20 - Quarante-cinquieme live : repair aval declenche, mais premiere reparation encore incomplete

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260720-054451/`, trace principale `rag-20260720054542819-60a6f70f`, trace SourceBacked `sbrag-c662625ac51c4ec4b11ec69d2f1630fd`, journaux processus `artifacts/live-test-process-live45-20260720-074444/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260720_074513.log`.
- Le test termine en 19 min 26 s et echoue sur l'absence de payload de sources. La reponse visible est une insuffisance sure de 685 caracteres; aucune carte de source n'est exposee et ce live n'est pas une reussite fonctionnelle.
- Le routeur termine en environ 29,8 s. Le Planner initial termine en environ 136,4 s avec 1 497 caracteres. `PlannerIntakeReview` termine dans sa borne dediee en environ 137,0 s avec 1 120 caracteres, sans timeout.
- L'audit initial contient les bons groupes mais un intake vide. La reparation compacte derive vers une clarification; les adjudications specialisees recuperent ensuite cinq lignes `Lundi` a `Vendredi` et quatre colonnes `Petit-dejeuner`, `Diner`, `Souper`, `Collation`. L'intake 5 x 4 est accepte.
- La premiere strategie est rejetee avec trois requetes sans facette. Son retry rend quatre requetes distinctes mais encore sans cible : `Petit-dejeuner sources for Lundi`, `Diner sources for Lundi`, `Souper sources for Lundi`, `Collation sources for Lundi`.
- `PlannerFacetAssignmentPatch` termine en environ 12,6 s et accepte directement `1->1, 2->2, 3->3, 4->4`. Les quatre recherches RAG sont executees et rendent respectivement 1, 10, 2 et 4 hits, sans timeout ni degradation. Le premier `EvidenceBundle` contient seize items.
- Le premier EvidenceJudge choisit `answer` sans ID. La revue de statut LLM choisit `read_documents`, avec `E1,E3,E5,E8` utiles et quatre IDs faibles. Le transport mecanique execute quatre appels `documents.context` : `Cuisine/livre-recette-sist-2025-web.pdf` page 10 sur deux chunks, `Cuisine/nobilia-recettes-internationales-FR.pdf` page 25 et `Cuisine/Je_cuisine_simplement.pdf` page 17.
- Le second `EvidenceBundle` contient 35 items. Le second EvidenceJudge choisit encore `answer` sans ID et signale des lacunes sur plusieurs cases. Les revues de statut suivantes redemandent `read_documents` sur des ancres deja consommees ou produisent un contrat incomplet; les reparations de selection et de budget refusent ces incoherences sans inventer d'action.
- `EvidenceJudgeFinalSelectionRepair` finit par fournir une selection executable. Le writer structure termine en environ 92,5 s avec 3 030 caracteres bruts. Le draft final contient vingt cellules, 828 caracteres et les citations `E1,E2,E32,E35,E34`.
- `SourceContractVerifier` retourne `valid=True`, sans code d'erreur, citation inconnue, cellule manquante ni source visible dupliquee. Comme au live 44, cette preuve est mecanique et ne suffit pas a conclure sur l'adequation semantique.
- `AnswerAdequacyJudge` termine en environ 45,4 s avec 1 142 caracteres, vingt claims audites, `decision=need_more_evidence` et zero suivi. Ses raisons exigent encore que les sources associent des recettes a des jours precis et critiquent la repetition, alors que les jours sont une affectation de mise en page et que la demande n'exige pas vingt valeurs distinctes.
- Le correctif issu du live 44 est bien actif : `action_contract_repair_attempted=True`. La premiere reparation termine en environ 21,8 s avec 269 caracteres, mais reste incomplete et ne contient aucun suivi executable; elle est refusee avec `action_contract_repair_accepted=False`.
- Le chemin general ulterieur ne produit pas non plus de suivi autorise. Le pipeline termine avec `llm_answer_adequacy_failed`, `sources_payload=none`, sans exposer le tableau intermediaire. L'arret est sur, mais la demande reste insatisfaite.
- La trace confirme encore `memory_context_present=True` et `memory_finality=context_only_never_final_proof`. Les souvenirs aident l'orchestration mais aucune information memorisee n'est promue en preuve.
- Diagnostic : le premier repair est maintenant appele au bon endroit, mais il ne recevait aucun retour mecanique detaille lorsqu'il echouait. Une relance LLM minimale est justifiee; elle doit voir uniquement les codes du contrat incomplet, jamais son raw, et doit pouvoir changer elle-meme la decision semantique.

### 2026-07-20 - Retry focalise du contrat d'adequation apres le quarante-cinquieme live

- `BuildAnswerAdequacyActionContractProblems` rend des problemes mecaniques explicites pour les trois decisions : raisons absentes, audit accepte incomplet, `revisedAnswer` absente, IDs non soutenus sous `accept`, suivi executable absent ou decision inconnue.
- Si `AnswerAdequacyActionContractRepair` reste invalide ou incomplet, le pipeline effectue exactement une relance `AnswerAdequacyActionContractRetry`. Il transmet l'intake, le claim map, les preuves autorisees et les codes de rejet; la sortie brute du premier repair est entierement omise.
- Le retry ne choisit aucune decision par code. Le LLM peut encore accepter avec audit complet, reviser avec un draft complet et cite, ou demander davantage de preuves avec un a trois appels RAG/document executables. Une seconde sortie incomplete reste refusee et conduit au meme arret sur.
- Les traces distinguent maintenant `action_contract_repair_retry_attempted` et `action_contract_repair_retry_accepted`. Les logs de rejet exposent decision, nombre de suivis, presence d'une revision, nombres d'IDs audites/non soutenus et codes mecaniques, sans journaliser le raw.
- Le prompt generique precise que lignes et jours sont des affectations de mise en page : une source doit soutenir la valeur concrete, pas nommer son jour, sauf si l'utilisateur demande un calendrier defini par la source. Il ne faut pas exiger de diversite ni rejeter une repetition soutenue sauf contrainte explicite de valeurs distinctes; le LLM reste juge de la pertinence.
- La regression `Run_reasks_the_llm_when_adequacy_requests_revision_without_revised_answer` couvre maintenant deux paliers : premier repair encore incomplet avec marqueur secret, puis retry accepte avec audit complet. Elle verifie l'absence du marqueur dans le prompt de retry et les deux nouveaux champs de trace.
- Les regressions negatives ajoutent un second repair incomplet et prouvent qu'il reste refuse, sans requete de recuperation synthetisee par le code. Le scenario historique ou le LLM fournit deja un suivi executable de diversite reste intact et execute son outil sans passer par le retry.
- Validation : build en 4 min 25 s, 0 avertissement/0 erreur; 10/10 tests discriminants; 288/288 tests SourceBacked; 368/368 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur d'espacement.
- Prochaine preuve discriminante : quarante-sixieme live canonique. Apres le tableau de vingt cellules, la trace doit montrer `action_contract_repair_retry_attempted=True` si le premier repair reste incomplet. Le retry doit soit accepter avec les vingt refs et zero unsupported, soit produire une revision complete validee independamment, soit autoriser un suivi RAG/document precis. La reussite exige toujours reponse utile, `IsSourceVerified=True`, payload de sources non nul et cartes utiles.

### 2026-07-20 - Quarante-sixieme live : Planner coupe par une baisse de debit avant l'intake

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260720-063036/`, trace principale `rag-20260720063117103-c201d302`, trace SourceBacked `sbrag-969b614a64f649bc84ac05c5fd946c04`, journaux processus `artifacts/live-test-process-live46-20260720-083030/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260720_083056.log`.
- Le test termine en 5 min 15 s avec une clarification sure de 237 caracteres, zero outil et zero source. Il echoue sur l'absence de payload et ne constitue pas une validation du retry d'adequation ajoute apres le live 45.
- Le routeur termine en environ 58,1 s. Le Planner initial demarre normalement mais le runtime local ne produit qu'environ 1,49 token/s, contre environ 3 token/s aux lives precedents.
- Le serveur atteint 255 tokens de completion et le client annule exactement a la borne de 210 000 ms. La trace rend `planner.timeout` puis `planner.late_cancelled`; aucun JSON exploitable, intake, strategie ou outil ne suit.
- Le pipeline ne reconstruit aucune demande depuis le code. Il retourne `needs_clarification=True` avec `The retrieval planner did not produce executable retrieval requests` et `LLM step 'planner' timed out before returning a usable source-backed response.`
- La memoire reste `context_only_never_final_proof`; aucun contenu memorise n'est utilise pour masquer l'absence de decision Planner.
- Diagnostic : le contrat du Planner est borne a 480 tokens. Au debit live observe de 1,49 token/s, sa seule generation peut demander environ 322 s, auxquels s'ajoutent traitement du prompt et ordonnancement. La borne 210 s, calibree sur 304-422 tokens en 96-140 s, n'absorbe pas cette variance du runtime local.

### 2026-07-20 - Marge de transport du Planner initial calibree sur le debit lent du live 46

- `SourceBackedLlmStepTimeoutPolicy` accorde maintenant 360 secondes au seul evenement exact `planner`, contre 210 auparavant. Le calcul couvre environ 322 s pour 480 tokens a 1,49 token/s et conserve une marge finie pour le prompt et l'ordonnancement.
- `PlannerIntakeReview` reste a 240 s, le writer structure et la reparation de cellules restent a 210 s, les revues de statut a 180 s et les reparations Planner generiques a 150 s. Aucun timeout global n'est elargi silencieusement.
- Le budget transport reste exactement 480 tokens. Prompt, parseur, schema, validation mecanique et pouvoir de decision du LLM sont inchanges; le code ne fait qu'eviter d'annuler une decision encore en generation.
- Validation : build 0 avertissement/0 erreur; regression de politique 1/1; 368/368 tests SourceBacked/OpenAI/ApiClient. Le build focalise a subi une attente systeme inhabituelle mais s'est termine avec succes; la suite sans reconstruction a ensuite dure 57 s.
- Prochaine preuve discriminante : quarante-septieme live. Le Planner doit soit terminer dans la nouvelle borne, soit fournir une nouvelle mesure de debit. Si le pipeline atteint l'adequation, les criteres du live 46 restent les memes : retry visible si necessaire, decision LLM complete, vingt cellules semantiquement valides et cartes de sources.

### 2026-07-20 - Quarante-septieme live : chemin complet et cartes atteints, valeurs semantiques encore impropres

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260720-064855/`, trace principale `rag-20260720064949821-614de63f`, trace SourceBacked `sbrag-3df01250ff944a9d940faf802ab83b99`, journaux processus `artifacts/live-test-process-live47-20260720-084849/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260720_084917.log`.
- Le run dure environ 21 min 55 s. Le pipeline produit pour la premiere fois une reponse finale source-backed de 720 caracteres, un `EvidenceBundle` final de 29 items et un payload non nul de deux sources : `Cuisine/Je_cuisine_simplement.pdf` page 31 et `Cuisine/nobilia-recettes-internationales-FR.pdf` page 106.
- Le test live echoue neanmoins sur une assertion de surface : il cherche litteralement `Diner`, tandis que le tableau francais rend correctement `Dîner`. Cette assertion doit tolerer la graphie accentuee; elle ne remet pas en cause le transport du payload.
- Le routeur termine en environ 54,4 s. Le Planner initial termine en 264,0 s avec 1 165 caracteres : la nouvelle borne exacte de 360 s est prouvee, car l'ancienne borne de 210 s aurait coupe cette sortie exploitable.
- `PlannerIntakeReview` termine en 213,2 s sous sa borne de 240 s. Il propose d'abord cinq colonnes en separant `Dejeuner` et `Diner`, ainsi que des citations d'ancres invalides. Les adjudications LLM par axe et le selecteur par identifiants recuperent cinq lignes `Lundi` a `Vendredi` et exactement quatre colonnes `Petit-dejeuner`, `Diner`, `Souper`, `Collation`; l'intake 5 x 4 est accepte.
- La premiere strategie se deduplique en une requete trop large. Son retry rend quatre requetes distinctes sans `targetFacetExact`; le patch de bijection LLM accepte `1->1,2->2,3->3,4->4` en 7,1 s.
- Les quatre recherches RAG terminent sans timeout avec 10, 10, 1 et 1 hits. Le premier `EvidenceBundle` contient 22 items. Le premier EvidenceJudge choisit `answer` sans ID; la revue de statut classe trois IDs utiles mais derive vers `clarify`.
- Les reparations de selection et d'action restent d'abord incompletes. Le retry de contrat d'action LLM rend finalement un `rag.multi_search` executable, `sources Cuisine pour plan de repas semaine lundi vendredi`, qui ajoute dix hits. Le second bundle contient 29 items.
- La seconde revue de statut choisit `write_partial` avec `E1,E23`. Le writer structure termine en 148,4 s avec 2 922 caracteres bruts. Le draft final contient vingt cellules, 720 caracteres et les citations `E1,E23`; `SourceContractVerifier` le declare mecaniquement valide.
- Le juge d'adequation initial choisit `need_more_evidence`. La premiere reparation bascule vers `accept`, audite les vingt claims et rend zero unsupported, mais omet ses raisons; elle est correctement refusee. Le nouveau retry focalise est atteint et retourne `accept`, une raison, les vingt refs, zero unsupported, mais conserve aussi un suivi executable. Le contrat courant accepte a tort cette combinaison incoherente `accept + followUpRequests`.
- La trace finale expose `action_contract_repair_attempted=True`, `action_contract_repair_retry_attempted=True`, `action_contract_repair_retry_accepted=True`, puis `source_backed_pipeline.active.accepted` avec deux sources. La memoire reste explicitement `context_only_never_final_proof`.
- La reponse ne satisfait pourtant pas le niveau professionnel demande : `Remplir chacun des moules avec` est une instruction fragmentaire utilisee comme collation, `Courgette dejeuner` est un intitule douteux pour le souper et le petit ensemble de valeurs est repete. Les citations existent, mais le LLM final a mal juge si chaque claim etait une instance complete du type de colonne demande.
- Bilan : Live47 prouve le chemin technique complet jusqu'aux cartes et le retry ajoute apres Live45, mais pas la qualite semantique finale. Le prochain correctif doit rester generique : rendre `accept` mecaniquement incompatible avec tout suivi ou revision residuelle, rappeler au writer et a tous les audits LLM qu'une cellule doit etre une instance complete de sa colonne et qu'une instruction, un composant, un ingredient, un titre large ou un fragment ne l'est pas. Le code ne doit pas reconnaitre les repas lui-meme.

### 2026-07-20 - Coherence terminale et legitimite semantique des cellules apres le live 47

- `BuildAnswerAdequacyActionContractProblems` refuse maintenant `accept` avec un `followUpRequests` non vide ou une revision residuelle. Il refuse aussi `revise` avec un suivi et `need_more_evidence` avec une revision. Ces regles sont des invariants de schema generiques : une decision terminale ne peut pas annoncer simultanement une action restante.
- `SatisfiesAcceptedAnswerAdequacyContract` applique la meme coherence a une decision `accept` initiale. Le code ne juge toujours ni un intitulé, ni une recette, ni une option; il verifie seulement que le contrat LLM ne se contredit pas.
- Les prompts `AnswerAdequacyJudge`, `AnswerAdequacyActionContractRepair`, `AnswerAdequacyActionContractRetry`, la reparation de format et la validation independante d'une revision demandent maintenant deux controles LLM par claim : soutien direct par l'extrait cite et adequation au type semantique de la colonne ou du champ.
- La regle reste volontairement transversale : une instruction fragmentaire, un ingredient, un composant, un outil, un titre, une categorie ou un conseil generique ne constitue pas une instance complete du champ demande uniquement parce que ses mots apparaissent dans l'extrait.
- `SourceBackedAnswerClaimAuditPrompt` porte cette regle dans le claim map commun a toutes les passes. `SourceBackedStructuredWriterPrompt` l'applique aussi avant la redaction, afin d'eviter de produire une cellule invalide puis de compter uniquement sur le juge aval.
- Le test live accepte maintenant `Diner` ou la graphie francaise correcte `Dîner`; il continue d'exiger les cinq jours, les quatre colonnes, au moins deux citations visibles, au moins deux cartes et l'absence de fallback d'insuffisance.
- La nouvelle regression `Run_rejects_action_contract_retry_that_accepts_with_a_residual_follow_up` reproduit le contrat final du live 47 : vingt refs auditees, zero unsupported et une raison, mais un suivi RAG encore present sous `accept`. Le retry reste refuse, aucune requete n'est synthetisee ou executee et le pipeline conserve l'arret sur.
- Validation : build sans erreur; 4/4 regressions discriminantes; 289/289 tests SourceBacked; 369/369 tests SourceBacked/OpenAI/ApiClient. La phase de compilation a pris environ cinq minutes sur l'environnement lent, alors que les quatre tests ont dure huit secondes.
- Prochaine preuve discriminante : quarante-huitieme live canonique. La reussite ne se limite pas au passage du test : chaque cellule visible doit etre une valeur complete et plausible pour sa colonne, directement soutenue par son EvidenceId. Le live doit conserver les vingt cellules, le payload et les cartes tout en eliminant les fragments comme `Remplir chacun des moules avec`.

### 2026-07-20 - Quarante-huitieme live : audit d'intake coupe a 347 tokens avant la preuve aval

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260720-072831/`, trace principale `rag-20260720072923877-18e01b5c`, trace SourceBacked `sbrag-a06437ea204e4dda96b356161e8477bc`, journaux processus `artifacts/live-test-process-live48-20260720-092823/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260720_092856.log`.
- Le test termine en environ 11 min 55 s avec une insuffisance sure de 293 caracteres, zero outil, zero preuve et zero source. Il echoue sur `Assert.NotNull(sourcesPayload)` et n'atteint ni le writer renforce ni le contrat d'adequation corrige apres le live 47.
- Le routeur termine en environ 61,0 s. Le Planner initial termine en 228,8 s avec 1 050 caracteres et environ 285 tokens generes; sa borne de 360 s reste saine.
- `PlannerIntakeReview` est annule exactement a 240,1 s. Le serveur avait traite un prompt de 988 tokens et atteint environ 1 335 tokens totaux, soit approximativement 347 tokens de completion, sans avoir encore livre le JSON complet au client.
- La premiere reparation d'ancres termine en 112,7 s mais rend un dictionnaire de 25 couples jour/creneau, avec `Dejeuner` et `Diner` separes. Elle est refusee pour `no_anchor_entries` et absence du contrat type.
- La reparation de contrat termine en 17,1 s mais rend encore l'ancien objet `decision/kind/reason`, des dictionnaires `rowHeaderLabel` et `columnAnchors`, et une clarification injustifiee. Elle est refusee sans conversion par code.
- Le pipeline termine avec `The retrieval planner did not produce executable retrieval requests` et `The LLM intake auditor did not approve a mechanically executable output structure after focused contract repairs.` La memoire reste `context_only_never_final_proof`.
- Diagnostic : ce live est un echec de transport amont, pas un verdict sur la legitimite des cellules. Avec un contrat borne a 480 tokens et un debit observe proche de 1,4 token/s, une completion sans EOS peut demander environ 343 s, hors traitement du prompt.

### 2026-07-20 - Borne de l'audit d'intake alignee sur le debit lent du live 48

- `SourceBackedLlmStepTimeoutPolicy` accorde maintenant 360 secondes au seul evenement exact `planner_intake_review`, contre 240 auparavant. Le Planner initial reste lui aussi a 360 s; les reparations Planner generiques restent a 150 s.
- Aucun budget de sortie ne change : `PlannerIntakeReview` reste borne a 480 tokens. Prompt, schema, parseur, adjudications et decisions LLM sont inchanges; seule l'annulation prematuree d'une completion encore active est evitee.
- Le test d'architecture attend explicitement 360 s pour le Planner initial et l'audit d'intake, 210 s pour writer/reparation structures, 180 s pour la revue de statut et 150 s pour les autres reparations Planner.
- Prochaine preuve discriminante : quarante-neuvieme live. L'audit doit franchir son ancien seuil de 240 s ou terminer plus tot, reconstruire l'intake 5 x 4 et atteindre de nouveau le writer puis l'adequation. La cible reste une reponse professionnellement utile; un tableau techniquement accepte avec une instruction fragmentaire reste un echec.

### 2026-07-20 - Quarante-neuvieme live : 360 secondes et 300 tokens ne suffisent pas au contrat d'intake actuel

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260720-075355/`, trace principale `rag-20260720075440844-3914c1fc`, trace SourceBacked `sbrag-e4462b40f5b84e7aa1655fd26a0b1f4d`, journaux processus `artifacts/live-test-process-live49-20260720-095348/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260720_095417.log`.
- Le pipeline actif dure environ 13 min 6 s et le test complet environ 13 min 52 s. Il termine avec l'insuffisance sure de 293 caracteres, zero outil, zero preuve, zero source et un payload nul. L'assertion live echoue sur `Assert.NotNull(sourcesPayload)`; le writer et les audits semantiques renforces apres le live 47 ne sont pas atteints.
- Le routeur termine en environ 54,5 s. Le Planner initial termine correctement en 285,8 s avec 1 125 caracteres et environ 293 tokens serveur. Il prouve une nouvelle fois la necessite de sa borne exacte de 360 s.
- `PlannerIntakeReview` demarre avec un prompt serveur de 984 tokens et reste actif jusqu'a sa nouvelle limite. Le client l'annule a 360,2 s; le serveur a alors atteint 1 284 tokens totaux, soit exactement environ 300 tokens de completion, sans objet JSON utilisable remis au pipeline.
- Cette mesure invalide l'hypothese d'une simple petite marge manquante : le debit tombe sous un token par seconde sur cette passe et la sortie n'est toujours pas terminee apres six minutes. Augmenter encore aveuglement la duree rendrait le parcours trop lent sans corriger la propension du modele a produire un contrat trop volumineux.
- La premiere reparation d'ancres termine en 56,5 s avec 446 caracteres, mais renvoie encore un dictionnaire historique `Lundi/Mardi/.../Petit-dejeuner/Diner/...` sans `structureReviewDecision`, sans listes de lignes/colonnes typees et sans ancres. Elle est refusee avec `outcome=no_anchor_entries`.
- La seconde reparation termine en 24,6 s avec 449 caracteres. Elle renvoie encore l'ancienne forme `decision/kind/reason`, des dictionnaires `rowHeaderLabel` et `columnAnchors`, et demande une clarification alors que les cinq jours et quatre creneaux sont explicites. Elle est refusee pour absence d'approbation et de listes d'axes executables.
- Le pipeline ne convertit pas ces sorties obsoletes en decisions valides, ne reconstruit pas les axes par heuristique et retourne `The LLM intake auditor did not approve a mechanically executable output structure after focused contract repairs.` La memoire reste `context_only_never_final_proof` et n'est pas utilisee pour masquer l'absence d'une decision courante.
- Diagnostic : le prochain changement ne doit pas retirer au LLM sa responsabilite semantique. Il doit reduire la taille du micro-contrat de revue d'intake et rendre les prompts de reparation assez explicites et compacts pour que le modele local produise directement la forme canonique, sans recopier des dictionnaires historiques ni justifier une clarification hors sujet.
- Prochaine preuve discriminante : ajouter une regression du timeout/incomplete JSON et des deux anciennes formes observees, simplifier le schema de sortie de la revue sans perdre la decision LLM, valider le parseur et les verificateurs sur cette forme, puis executer un cinquantieme live. La reussite doit atteindre l'intake 5 x 4, le RAG, le writer, l'audit semantique des vingt cellules et les cartes de sources.

### 2026-07-20 - Micro-audits LLM par axe avant le cinquantieme live

- Lorsqu'un premier Planner LLM fournit explicitement `structureReviewKind=two_axis_grid` mais que son intake reste incomplet ou incoherent, `ReviewPlannerIntakeIfNeededAsync` orchestre maintenant en priorite deux decisions semantiques bornees : `PlannerIntakeRowAxisAdjudication`, puis `PlannerIntakeColumnAxisAdjudication`.
- Le LLM conserve toute la responsabilite semantique. Le premier micro-audit decide les membres de ligne, leur ordre, leur citation d'ancrage, leur relation et le nom de dimension; le second decide les colonnes et leurs ancres. Le code ne choisit, ne traduit et ne complete aucun label : il assemble les deux contrats types et applique les verifications mecaniques existantes.
- Ce chemin evite la duplication couteuse de la revue monolithique : l'ancien `PlannerIntakeReview` demandait simultanement un intake complet, les memes labels dans les groupes d'ancres, plusieurs metadonnees Planner, des raisons, des requetes vides et une clarification. Il reste disponible comme repli si une des deux opinions focalisees ne produit pas un contrat acceptable.
- Le routage est isole dans `SourceBackedRagPipeline.PlannerIntakePrimaryAxisAdjudication.cs`. Le fichier principal `SourceBackedRagPipeline.PlannerIntakeReview.cs` reste a 491 lignes et le nouveau fichier a 36 lignes; la garde d'architecture sous 500 lignes est respectee.
- La regression `Run_uses_bounded_axis_adjudications_before_the_large_intake_review_for_a_planner_identified_grid` part d'un Planner qui reconnait la grille mais omet tous ses axes. Deux sorties LLM focalisees reconstruisent cinq lignes et quatre colonnes, le contrat compte vingt cellules, la revue de strategie continue normalement et aucun appel `PlannerIntakeReview` ou `PlannerIntakeAnchorRepair` n'est effectue.
- Validation : build sans erreur; regression et garde modulaire 2/2; suite SourceBacked 290/290; suite combinee SourceBacked/OpenAI/ApiClient 370/370; `git diff --check` sans erreur sur les fichiers touches.
- Prochaine preuve discriminante : cinquantieme live canonique. Si le Planner initial identifie `two_axis_grid`, la trace doit passer directement de `planner.end` aux deux adjudications d'axes sans appel `planner_intake_review`. Le resultat doit ensuite atteindre la strategie, le RAG, le writer et l'audit semantique renforce; les vingt cellules et cartes restent les criteres de succes final.

### 2026-07-20 - Cinquantieme live : micro-audits prouves, mais l'adequation accepte encore des instructions comme repas

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260720-082704/`, trace principale `rag-20260720082747278-d9c8ba76`, trace SourceBacked `sbrag-8c2947f104b64b658b36cae12fc76010`, journaux processus `artifacts/live-test-process-live50-20260720-102657/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260720_102725.log`.
- Le test automatisé passe en 28 min 49 s avec une reponse de 1 078 caracteres, vingt cellules, trois cartes de sources et trois EvidenceIds visibles. Cette issue est toutefois un echec fonctionnel : satisfaire les assertions de surface ne signifie pas produire un plan professionnel.
- Le routeur termine en environ 65,2 s. Le Planner initial termine en 252,9 s avec 1 163 caracteres et identifie `structureReviewKind=two_axis_grid`.
- Le nouveau routage est actif : aucun `planner_intake_review` ne suit immediatement le Planner. `PlannerIntakeRowAxisAdjudication` termine en 82,5 s et accepte directement `Lundi, Mardi, Mercredi, Jeudi, Vendredi`.
- Le premier `PlannerIntakeColumnAxisAdjudication` est en revanche coupe a sa borne generique de 150,1 s. Le serveur a traite 647 tokens de prompt et atteint 813 tokens totaux, soit environ 166 tokens de completion, sans livrer l'objet au client. Les deux reparations focalisees derivent vers des contraintes de presentation (`plan de repas`, `format clair`, `sources utiles`, `ne pas inventer`) au lieu des creneaux.
- Le repli monolithique termine rapidement mais rend des groupes d'ancres invalides. Sa reparation reste incomplete; les adjudications d'axes de repli recuperent ensuite cinq jours puis, en 30,0 s, les quatre colonnes `Petit-dejeuner`, `Diner`, `Souper`, `Collation`. L'intake 5 x 4 est finalement accepte.
- La premiere strategie se deduplique en une recherche large. Son retry fournit quatre requetes `... sources for Lundi` sans facette typee; le patch LLM de bijection accepte `1->1,2->2,3->3,4->4`. Les recherches rendent 1, 10, 2 et 4 hits; le premier bundle contient seize items.
- La premiere revue de statut LLM choisit `read_documents` et quatre EvidenceIds. Le transport mecanique execute quatre lectures exactes : deux chunks de `Cuisine/livre-recette-sist-2025-web.pdf` page 10, `Cuisine/chefbot_livre_de_recettes_fr.pdf` page 21 et `Cuisine/Je_cuisine_simplement.pdf` page 17. Le second bundle contient 36 items.
- Plusieurs decisions aval restent incoherentes (`answer` sans IDs, `read_documents` sur des ancres deja lues). Les reparations bornees finissent par fournir une selection finale et le writer structure termine en 109,7 s avec 3 096 caracteres bruts.
- Le premier draft compte 1 032 caracteres mais laisse `Vendredi/Souper` et `Vendredi/Collation` vides. Le verificateur mecanique le refuse; `StructuredCellRepair` termine en 90,2 s et produit un tableau complet de 1 078 caracteres, cite par `E1,E2,E33`, que le contrat de structure accepte.
- Le contenu reste pourtant impropre. Chaque jour repete : `Cerealiere 1 portion de legumes 1 boisson chaude` en petit-dejeuner, `Creme de pois et avocat` au diner, `Melanger tous les ingredients a l'aide d'un fouet` au souper et `Melanger tous les ingredients` en collation. Les deux dernieres valeurs sont des instructions, pas des instances completes des colonnes demandees; la premiere ressemble a une consigne nutritionnelle fragmentaire, pas a un repas professionnel.
- `AnswerAdequacyJudge` choisit d'abord `need_more_evidence`. La premiere reparation est refusee pour raison absente et suivi inexecutable. Le retry focalise bascule ensuite vers `accept`, audite les vingt claims, rend zero suivi et permet au pipeline d'exposer la mauvaise reponse avec trois cartes. Les nouvelles phrases de prompt ne suffisent donc pas : le modele local confond encore soutien lexical et legitimite du type de valeur lorsqu'il doit simultanement gerer vingt claims et une action terminale.
- La memoire reste correctement `context_only_never_final_proof`. Toutes les citations et cartes proviennent du bundle courant; le probleme n'est pas une promotion illegitime de souvenirs mais le jugement de legitimite semantique des valeurs candidates.
- Diagnostic : le prochain garde-fou doit rester LLM-first et generique. Ajouter un micro-audit LLM focalise sur chaque valeur distincte apres le writer : pour un identifiant stable, un libelle de champ, une valeur visible et son extrait cite, le LLM doit declarer explicitement `complete_instance` ou `wrong_type` avec une raison. Le code ne reconnaitra aucun repas; il verifiera seulement la couverture du contrat et interdira `accept` tant qu'une valeur est classee instruction, fragment, ingredient, composant, outil, titre ou conseil au lieu d'une instance complete.
- Ce micro-audit doit dedupliquer les valeurs identiques avant l'appel : le tableau du live 50 contient seulement quatre couples valeur/colonne distincts malgre vingt cellules. Une reparation semantique focalisee devra ensuite remplacer uniquement les cellules rejetees a partir des EvidenceIds autorises, puis repasser par le verificateur mecanique et le micro-audit avant l'adequation globale.

### 2026-07-20 - Micro-audit LLM de legitimite des valeurs structurees apres le live 50

- `PlannerIntakeColumnAxisAdjudication` dispose maintenant d'une borne exacte de 240 secondes pour sa premiere opinion. Les reparations de contrat restent a 150 secondes. Cette calibration vient de la mesure du live 50 : environ 166 tokens produits en 150 secondes pour un contrat borne a 256 tokens.
- Un nouveau contrat transverse `SourceBackedStructuredValueTypeReview` transporte des avis LLM `complete_instance` ou `wrong_type` pour des candidats immuables. Chaque candidat contient le champ demande, la valeur visible, les refs de claims, les EvidenceIds et les extraits cites.
- `SourceBackedStructuredValueTypeFitPrompt` donne explicitement au LLM la responsabilite du jugement semantique. Il lui demande de refuser une instruction, un fragment, un ingredient, un composant, un outil, un titre, une categorie large, un conseil generique ou une regle de quantite lorsqu'il ne s'agit pas d'une instance complete du champ demande.
- Le code ne contient aucune liste de repas et ne decide pas ce qui constitue une bonne recette. Il deduplique mecaniquement les couples colonne/valeur/preuves, verifie que le LLM a couvert exactement tous les identifiants attendus, puis bloque l'acceptation si au moins un avis est `wrong_type` ou si le contrat est incomplet.
- Le micro-audit s'execute avant l'adequation globale pour les tableaux d'au moins vingt cellules. Dans le tableau repete du live 50, vingt claims deviennent seulement quatre candidats distincts, ce qui garde l'appel focalise et borne.
- Une seule reparation de format est autorisee si le JSON est mecaniquement invalide. Une decision semantique `wrong_type` n'est jamais transformee en acceptation par le code; elle produit `llm_answer_adequacy_failed` et repasse par le chemin de recuperation de preuves existant.
- La regression exacte du live 50 fournit vingt cellules mais seulement quatre valeurs distinctes. Le LLM de test accepte la creme de pois et classe la cerealiere fragmentaire ainsi que les deux instructions `Melanger...` comme `wrong_type`; le pipeline refuse la reponse et trace les IDs rejetes `V1,V3,V4`.
- Le double de test a ete adapte pour fournir un avis positif neutre uniquement aux anciennes fixtures qui ne testent pas ce nouveau palier. Les regressions dediees avec une reponse `checks` explicite restent prioritaires et prouvent toujours le refus semantique.
- Validation finale : build 0 avertissement/0 erreur; 291/291 tests SourceBacked; 371/371 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur.
- Prochaine preuve discriminante : cinquante-et-unieme live canonique. Il doit prouver la borne de 240 secondes de l'adjudication des colonnes et atteindre `structured_value_type_fit_judge`. Une reponse composee des fragments du live 50 doit etre refusee et recuperer des preuves, tandis qu'une reponse professionnellement acceptable doit conserver vingt cellules, des citations exactes et des cartes de sources utiles.

### 2026-07-20 - Cinquante-et-unieme live : le mauvais tableau est bloque, mais aucune revision semantique ciblee n'est encore tentee

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260720-093127/`, trace principale `rag-20260720093228756-ac74bc94`, trace SourceBacked `sbrag-8a3ab8a9adf3432c99181129cb35dbf3`, journaux processus `artifacts/live-test-process-live51-20260720-113117/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260720_113157.log`.
- Le test termine en environ 16 min 07 s et echoue sur `Assert.NotNull(sourcesPayload)`. La reponse visible est une insuffisance sure d'environ 898 caracteres, sans payload ni carte. Ce live n'est donc pas une reussite fonctionnelle, mais il apporte une preuve de securite importante : le tableau semantiquement impropre n'est plus expose a l'utilisateur.
- Le routeur termine en environ 14,8 s et le Planner en 52,7 s. L'adjudication des lignes termine en 17,8 s mais omet la relation; le patch de metadonnees la repare en 3,5 s.
- La premiere adjudication des colonnes franchit pour la premiere fois son ancienne borne de 150 secondes et termine en 32,3 s sous la nouvelle limite de 240 secondes. Elle propose une clarification injustifiee et cinq libelles en separant `Dejeuner` et `Diner`; le selecteur LLM focalise conserve ensuite, en environ 3 s, exactement les quatre colonnes demandees. L'intake 5 x 4 est accepte.
- La premiere reparation de strategie termine en 61,2 s mais renvoie une recherche trop large, sans assignations typees. Son retry termine en 39,5 s avec quatre requetes `... sources Cuisine` encore depourvues de facette cible. Le patch de bijection termine en 6,9 s et accepte `1->1, 2->2, 3->3, 4->4` sans que le code choisisse semantiquement les requetes.
- Les quatre recherches rendent respectivement 10, 10, 1 et 1 hits; le premier `EvidenceBundle` contient 22 items. La revue de statut LLM demande ensuite trois lectures documentaires exactes : `Cuisine/Je_cuisine_simplement.pdf` page 31, `Cuisine/14911887_9001116052_NFFS4I_fr_fm.pdf` page 28 et `Cuisine/chefbot_livre_de_recettes_fr.pdf` pages 73-74. Le second bundle contient 36 items.
- Les decisions de preuve aval restent instables et la selection finale ne conserve que `E1`. Le writer structure termine en environ 102,7 s avec un tableau plausible en surface, mais laisse `Vendredi/Souper` et `Vendredi/Collation` vides. Le verificateur mecanique le refuse.
- `StructuredCellRepair` termine en environ 116,3 s et reconstruit les vingt cellules. Le tableau compte alors six candidats distincts et cite uniquement `E1`; il franchit le contrat de forme, mais cette verification mecanique ne prouve pas encore la legitimite semantique des valeurs.
- Le nouveau `structured_value_type_fit_judge` est atteint en live pour la premiere fois. Il termine en environ 48,2 s et rejette quatre candidats sur six : `V1,V2,V4,V5`. Ses raisons identifient des ecarts reels entre la valeur visible, le type de champ demande et l'extrait cite; par exemple, une valeur visible de smoothie vert ne correspond pas a l'extrait cite sur un smoothie aux fruits rouges.
- Le pipeline bloque correctement la reponse apres cet avis `wrong_type`. Contrairement au live 50, aucune table impropre ni carte trompeuse n'est exposee. La memoire reste `context_only_never_final_proof`; aucun souvenir n'est promu en preuve.
- Le blocage suivant est localise : apres le rejet focalise, le chemin general `evidence_judge_action_repair` ne produit aucun suivi executable. La trace montre ensuite `llm_action_contract_retry_allowed=False` puis `verification_follow_up_not_authorized_by_llm`. Le LLM a correctement diagnostique les cellules, mais le pipeline ne lui demande pas encore de les reviser a partir des preuves deja autorisees et lui interdit la relance plate lorsque sa premiere action de recuperation est incomplete.

### 2026-07-20 - Revision LLM focalisee des seuls types de valeur rejetes apres le live 51

- `SourceBackedStructuredValueTypeRepairPrompt.cs` introduit le micro-contrat `SAAIA_SOURCE_BACKED_STEP=StructuredValueTypeRepair`. Le LLM reste l'orchestrateur semantique et choisit exclusivement entre `revise` et `need_more_evidence`.
- Avec `revise`, le LLM doit fournir la table complete, modifier seulement les claims rejetes, conserver a l'identique le texte et les citations des claims deja acceptes, et utiliser uniquement les EvidenceIds de la selection courante. Avec `need_more_evidence`, il ne fournit aucun brouillon et rend un a trois appels RAG/document executables.
- Le prompt transporte les candidats rejetes et leurs raisons, les refs acceptees immuables, les extraits des preuves autorisees, la reponse complete courante, les recherches deja tentees et les problemes mecaniques. Il ne contient aucune recette, liste de repas, comparaison lexicale ou regle metier codee.
- `SourceBackedRagPipeline.StructuredValueTypeRepair.cs` borne la boucle a une premiere opinion et un seul retry de contrat si la sortie est invalide. Le code parse la decision LLM et verifie uniquement les invariants : raisons non vides, table complete sans suivi sous `revise`, suivi executable et non repete sous `need_more_evidence`, citations autorisees et claims acceptes inchanges.
- Toute revision passe de nouveau par `SourceContractVerifier`, puis par un second `StructuredValueTypeFitJudge` independant. Elle n'est acceptee que si ce second LLM audite tous les candidats distincts et les classe `complete_instance`. Une revision partielle, une mutation d'un claim accepte, une citation hors selection ou un nouvel avis `wrong_type` reste un echec sur.
- `SourceBackedRagPipeline.AnswerAdequacy.cs` remplace le draft courant uniquement apres ces deux preuves. Si la revision echoue ou si le LLM demande plus de preuves, ses raisons et suivis alimentent le chemin de recuperation existant au lieu d'etre ignores.
- `SourceBackedRagPipeline.VerificationRecovery.cs` autorise maintenant le retry plat du contrat d'action uniquement lorsqu'un code `llm_answer_adequacy_failed` est present. Cette relance reste une decision du LLM : le code ne synthetise aucune requete generique et n'invente ni outil, ni categorie, ni requete.
- Le budget de sortie de `StructuredValueTypeRepair` est borne a 1 000 tokens et son delai exact a 210 secondes, en coherence avec les autres ecritures structurees longues. Le nouveau fichier pipeline compte 327 lignes et `SourceBackedRagPipeline.AnswerAdequacy.cs` 455 lignes; la garde architecturale sous 500 lignes est respectee.
- La regression negative du live 50 prouve qu'un LLM qui ne trouve pas de remplacement prouvable peut demander une recherche concrete et que la mauvaise table reste refusee. La nouvelle regression positive prouve qu'il peut modifier uniquement `V1,V3,V4`, conserver tous les claims de `V2`, produire une table complete et obtenir l'acceptation du second audit.
- Les anciens tests de contrats invalides ont ete alignes sur la nouvelle sequence complete : reparation du contrat d'adequation, retry focalise, revue de recuperation puis retry plat autorise. Ils verifient explicitement `EvidenceJudgeActionContractRetry` et l'absence de suivi generique synthetise.
- Validation deterministe : build en 0 avertissement et 0 erreur; 5/5 tests discriminants; 4/4 regressions historiques reajustees; 292/292 tests SourceBacked; 372/372 tests SourceBacked/OpenAI/ApiClient.
- Prochaine preuve discriminante : cinquante-deuxieme live canonique. La trace doit atteindre le premier `structured_value_type_fit_judge`, puis soit `StructuredValueTypeRepair` avec une revision complete acceptee par le second audit, soit un suivi RAG/document explicitement choisi par le LLM. La reussite fonctionnelle exige toujours une table professionnellement plausible de vingt cellules, des citations exactes, `IsSourceVerified=True`, un payload non nul et des cartes de sources utiles; une insuffisance sure restera classee comme echec fonctionnel meme si elle protege correctement l'utilisateur.

### 2026-07-20 - Cinquante-deuxieme live : echec d'intake avant le RAG, donc non discriminant pour la revision semantique

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260720-101735/`, trace principale `rag-20260720101821443-2600e40e`, trace SourceBacked `sbrag-f60f4725a884406ca664bb60fb401195`, journaux processus `artifacts/live-test-process-live52-20260720-121729/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260720_121755.log`.
- Le test termine en environ 28 min 02 s avec une insuffisance sure de 293 caracteres. Le payload est nul, le nombre de sources est zero, aucun outil n'est execute et aucun `EvidenceBundle` n'est construit. Le live n'atteint ni writer, ni `structured_value_type_fit_judge`, ni `StructuredValueTypeRepair`; il ne valide donc pas et n'invalide pas le correctif aval issu du live 51.
- Le routeur termine en environ 65,2 s. Le Planner termine en environ 258,1 s avec 1 164 caracteres, sous sa borne de 360 s, puis declenche correctement le chemin LLM-first d'adjudication des axes.
- La premiere adjudication des lignes termine en 121,5 s et rend les cinq jours. Sa citation contient toutefois `tu me fasse` au lieu de la sous-chaine exacte `tu me fasses`; le verificateur mecanique refuse donc l'ancre. Deux patches de metadonnees puis leur retry conservent la meme faute et restent rejetes apres environ 37,5 s, 39,0 s et 46,3 s.
- Le repli `PlannerIntakeReview` termine en 211,1 s et reconnait une grille a deux axes avec les bons labels, mais cite `Lundi | Mardi | ...` et `Petit-dejeuner | Diner | ...` comme ancres exactes alors que ces sous-chaines concatenees n'existent pas dans la question. Il est refuse sans que le code ne transforme ces ancres.
- `PlannerIntakeAnchorRepair` termine en 87,3 s et separe les labels en ancres exactes individuelles. Cette forme reste fausse pour la plage `lundi au vendredi` : les mots `Mardi`, `Mercredi` et `Jeudi` ne sont pas presents individuellement dans la question et devraient etre relies par une ancre de plage choisie par le LLM. La sortie omet aussi l'approbation de revue obligatoire.
- L'adjudication de lignes de secours termine en 95,2 s et cite correctement la phrase `du lundi au vendredi`; son patch de metadonnees termine en 7,6 s et fournit enfin la relation necessaire. L'axe des lignes devient alors exploitable sans heuristique metier.
- `PlannerIntakeColumnAxisAdjudication` est ensuite annule exactement a 240,1 s. Le serveur local avait un prompt d'environ 662 tokens et produit environ 157 tokens sans livrer le contrat complet. La reparation compacte expire a son tour apres 150,3 s. Le retry termine en 115,3 s mais choisit `clarify`, renvoie le seul label `USER_QUESTION` et recopie presque toute la demande dans une ancre `exact`; le contrat est mecaniquement refuse.
- La derniere `PlannerIntakeAnchorContractRepair` expire apres 150,0 s. Le pipeline termine avec `The retrieval planner did not produce executable retrieval requests` et `The LLM intake auditor did not approve a mechanically executable output structure after focused contract repairs.` La memoire reste `context_only_never_final_proof` et n'est pas utilisee pour contourner l'absence d'une decision courante.
- Diagnostic : ce live est domine par un micro-contrat de colonnes encore trop lent et trop propice aux sorties verbeuses ou obsoletes sur le modele local. Le prochain changement doit rester LLM-first : demander une decision de colonnes plus compacte, avec des identifiants stables ou une forme minimale, puis laisser le code verifier et transporter cette decision. Le code ne doit ni extraire les quatre repas par lexique, ni supposer qu'une grille de repas a toujours ces colonnes.
- Prochaine preuve discriminante : ajouter une regression reproduisant le timeout puis le retry `clarify/USER_QUESTION`, compacter le contrat de decision des colonnes sans retirer le choix semantique au LLM, valider les suites deterministes, puis executer le live suivant. Le vrai gate aval reste inchange : atteindre `StructuredValueTypeRepair`, une revision ou un suivi de preuve choisi par le LLM, vingt cellules plausibles, citations exactes, payload et cartes utiles.

### 2026-07-20 - Contrat LLM compact de colonnes et recuperation d'une opinion vide apres le live 52

- `PlannerIntakeColumnAxisAdjudication` demande maintenant une decision compacte avec cinq champs : `decision`, `reason`, puis les tableaux paralleles ordonnes `labels`, `quotes` et `relations`. La position `i` transporte une seule decision semantique complete de colonne et son ancre verbatim.
- Cette forme remplace quatre objets repetitifs par trois tableaux courts. Le LLM decide toujours la liste finale, l'ordre, les citations de question et les relations `exact` ou `alternative_group`; le code ne reconnait aucun nom de repas et ne complete aucun label.
- `SourceBackedRagJson.ParseIntakeColumnAxisDecision` accepte le nouveau format compact et conserve l'ancien tableau `anchors` pour compatibilite. La lecture compacte preserve strictement l'ordre, les doublons et les positions vides; elle ne reutilise pas le lecteur generique qui deduplique volontairement les tableaux d'identifiants.
- Ce detail est essentiel : plusieurs colonnes ordinaires partagent legitiment la relation `exact`. Une deduplication de `exact, exact, exact, alternative_group` casserait l'alignement et doit donc rester visible au verificateur plutot que d'etre corrigee silencieusement.
- Si la premiere opinion revient sans au moins deux ancres, notamment sous la forme synthetique observee apres timeout, `PlannerIntakeColumnAxisContractRepair` ne pretend plus reparer differentiellement un candidat vide. Il demande au LLM une nouvelle adjudication complete et compacte directement depuis `USER_QUESTION`.
- Cette reparation fraiche n'injecte pas le raw timeout ou la sortie rejetee. Elle expose seulement les eventuels labels incomplets et les problemes mecaniques, interdit les lignes dans l'axe colonne et exige des tableaux paralleles. Un candidat deja suffisamment peuple conserve le chemin differentiel historique.
- `SourceBackedLlmOutputBudget` attribue exactement 256 tokens a l'adjudication initiale des colonnes et a sa reparation de contrat, au lieu du budget Planner generique de 640. Les bornes temporelles restent 240 s puis 150 s : le but est d'obtenir une decision courte, pas d'allonger encore un parcours deja trop lent.
- La regression `Run_recovers_a_timed_out_column_axis_opinion_with_a_fresh_compact_llm_contract` reproduit la sortie Live52 `plannerReasoningSummary/requests=[]/needsClarification=true`, puis fournit une opinion LLM compacte. Elle prouve que le pipeline recupere `Petit-dejeuner`, `Diner`, `Souper`, `Collation`, construit vingt cellules, conserve quatre recherches typees et evite `PlannerIntakeReview`.
- Les tests de transport prouvent `max_tokens=256` dans l'adaptateur natif et dans l'adaptateur compatible OpenAI. Le test d'architecture prouve les deux formats de parsing et le maintien du jugement LLM; les anciens scenarios complexes de conflit, ancre et role de ligne restent verts.
- Validation finale : build 0 avertissement/0 erreur; 5/5 preuves ciblees; 3/3 anciens parcours d'axes; 293/293 tests SourceBacked; 373/373 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans nouvelle erreur de contenu.
- Prochaine preuve discriminante : live suivant avec binaire recompile. L'adjudication des colonnes doit soit terminer directement sous le contrat compact, soit etre recuperee par sa reparation fraiche sans repli monolithique. Le run doit ensuite atteindre le RAG puis le vrai gate aval `StructuredValueTypeRepair`; les criteres professionnels de vingt cellules, citations, payload et cartes restent inchanges.

### 2026-07-20 - Cinquante-troisieme live : la compaction supprime le timeout, puis un conflit de candidats apparait apres le patch d'ancres

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260720-111059/`, trace principale `rag-20260720111142147-136d86fe`, trace SourceBacked `sbrag-dedb72b98240466583864182fa4ea03f`, journaux processus `artifacts/live-test-process-live53-20260720-131052/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260720_131119.log`.
- Le test termine en environ 19 min 16 s avec la meme insuffisance sure de 293 caracteres, zero outil, zero `EvidenceBundle`, zero source et un payload nul. Il reste donc non discriminant pour `StructuredValueTypeRepair` et ne constitue pas une reussite fonctionnelle.
- Le routeur termine en environ 54,0 s et le Planner en 203,8 s avec 1 271 caracteres. Le Planner reconnait la grille et declenche le chemin primaire d'axes.
- La premiere adjudication de lignes termine en 95,1 s avec les cinq jours, mais omet la relation. Son patch de metadonnees termine en 6,1 s et rend l'axe ligne exploitable.
- Le nouveau `PlannerIntakeColumnAxisAdjudication` utilise effectivement le prompt compact : environ 2 737 caracteres cote client et environ 496 tokens cote serveur, contre 2 907 caracteres et environ 662 tokens dans Live52.
- Surtout, cette passe termine en environ 30,0 s au lieu d'expirer a 240 s. La compaction corrige donc le blocage de transport mesure dans Live52.
- Sa sortie contient `labels=[Petit-dejeuner, Dejeuner, Diner, Souper, Collation]`, cinq citations, mais seulement deux relations `exact, alternative_group`. Le parseur preserve les positions manquantes et le verificateur expose trois relations invalides au lieu de dedupliquer ou completer silencieusement.
- `PlannerIntakeColumnAxisAnchorPatch` complete ensuite les cinq metadonnees en 37,5 s. Cette reparation fait apparaitre le vrai probleme restant : `Dejeuner` n'est qu'une sous-chaine imbriquee dans `Petit-dejeuner`, donc les deux ne peuvent pas representer deux occurrences `exact` independantes.
- Le retry du patch d'ancres repete les cinq identifiants en 54,8 s et laisse le meme `overlapping_exact_structure_anchors`. Le pipeline abandonne alors le chemin primaire et retombe sur l'audit monolithique.
- Le repli coute environ 259,3 s et reproduit encore des groupes concatenees invalides. Sa reparation d'ancres demande une clarification injustifiee. Un second chemin d'axes finit par confondre les cinq jours avec les colonnes, puis ses patches d'ancres repetent cette erreur. Le dernier contrat d'ancre ne recupere pas l'intake.
- Diagnostic localise : le selecteur LLM `PlannerIntakeColumnAxisSelectionPatch` sait deja arbitrer un chevauchement par identifiants immuables et a ete prouve dans les lives 50/51. Il est appele lorsque le chevauchement existe dans la premiere opinion, mais pas lorsqu'il apparait seulement apres `PlannerIntakeColumnAxisAnchorPatch`.
- Prochain changement : apres un patch d'ancres valide au niveau syntaxe, si les seuls problemes restants sont des chevauchements exacts, transporter le candidat repare vers `PlannerIntakeColumnAxisSelectionPatch` au lieu de repeter un patch de metadonnees. Le LLM choisira l'identifiant semantiquement legitime; le code se limitera au routage, a la preservation des candidats non conflictuels et a la verification.

### 2026-07-20 - Composition du patch d'ancres et du selecteur LLM apres le live 53

- `RepairPlannerIntakeColumnAxisAnchorsAsync` re-evalue maintenant les seuls problemes mecaniques apres application du patch de metadonnees. Si ces problemes satisfont le contrat localise `overlapping_exact_structure_anchors`, le candidat repare est transmis directement a `PlannerIntakeColumnAxisSelectionPatch`.
- Le meme transfert existe apres l'unique retry d'ancres. Il evite qu'un chevauchement nouvellement visible soit abandonne simplement parce qu'il n'existait pas encore dans la premiere opinion.
- Le code ne choisit aucun label. Le patch d'ancres vient du LLM; le conflit est calcule sur des occurrences de question; le second LLM selectionne un identifiant immuable dans chaque paire; le code preserve mecaniquement tous les candidats hors conflit.
- La regression `Run_routes_a_post_anchor_overlap_to_the_llm_candidate_id_selector` reproduit la sortie du live 53 : cinq labels dont `Dejeuner` imbrique dans `Petit-dejeuner`, relations paralleles incompletes, puis patch de cinq ancres.
- Le verificateur revele le conflit `1,2`; le prompt de selection expose uniquement ces deux candidats et declare `3,4,5` deja preserves. La decision LLM choisit `1`, puis l'intake final contient exactement `Petit-dejeuner`, `Diner`, `Souper`, `Collation` et vingt cellules.
- La regression interdit aussi le retry d'ancres historique apres ce conflit. Elle exige le passage par `PlannerIntakeColumnAxisSelectionPatch`, une strategie de quatre facettes et un plan executable.
- Validation : build 0 avertissement/0 erreur; 3/3 regressions focalisees; 294/294 tests SourceBacked; 374/374 tests SourceBacked/OpenAI/ApiClient.
- Prochaine preuve discriminante : live suivant. Il doit conserver le gain de transport du contrat compact, enchainer anchor patch puis selection par IDs si le cinquieme candidat imbrique reapparait, accepter l'intake 5 x 4 et atteindre le RAG. Le gate aval reste `StructuredValueTypeRepair` puis une reponse professionnellement utile avec cartes.

### 2026-07-20 - Cinquante-quatrieme live : correction de grille prouvee, strategie de recherche encore non executable

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260720-114247/`, trace principale `rag-20260720114338698-fb9673d9`, trace SourceBacked `sbrag-64314e9f192a43f6a25a2c3d7274294c`, journaux processus `artifacts/live-test-process-live54-20260720-134240/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260720_134310.log`.
- Le test termine en 14 min 57 s avec une insuffisance sure de 298 caracteres, zero outil, zero preuve, zero source et un payload nul. Il echoue sur `Assert.NotNull(sourcesPayload)` et n'atteint ni le writer, ni `StructuredValueTypeFitJudge`, ni `StructuredValueTypeRepair`.
- Le routeur termine en environ 46,8 s. Le Planner termine en 190,1 s avec 1 121 caracteres, puis le chemin primaire d'adjudication des axes s'execute.
- L'adjudication des lignes termine en 64,6 s et rend les cinq jours mais omet la relation de l'ancre. Le patch de metadonnees ajoute cette relation en 7,3 s; l'axe ligne devient valide.
- L'adjudication compacte des colonnes termine en 49,1 s avec cinq labels et seulement deux relations. Le patch d'ancres complete les cinq metadonnees en 38,4 s et revele, comme au live 53, le chevauchement exact entre `Petit-dejeuner` et `Dejeuner`.
- Le nouveau chaînage est effectivement pris en live : au lieu de repeter le patch d'ancres, le pipeline appelle `PlannerIntakeColumnAxisSelectionPatch`. Le LLM choisit l'identifiant legitime en 7,0 s; le candidat final conserve exactement `Petit-dejeuner`, `Diner`, `Souper`, `Collation`.
- `PlannerIntakeReview` journalise ensuite `accepted=True`, `structureReviewKind=two_axis_grid`, cinq lignes, quatre colonnes et aucun probleme mecanique. Live54 prouve donc le correctif issu de Live53 dans le vrai runtime, sans heuristique metier et sans repli monolithique.
- Le blocage se deplace vers la strategie de recherche. `PlannerQueryStrategyRepair` atteint sa borne de 150 s sans sortie utilisable. Le repli `PlannerCompactFacetPlan`, avec un prompt de 3 442 caracteres, atteint lui aussi 150 s avant de remettre un contrat complet.
- Le dernier `PlannerQueryStrategyRetry` termine en 134,2 s et approuve semantiquement une seule recherche large qui recopie les quatre colonnes. Le verificateur mecanique la refuse avec `query_copies_multiple_typed_columns`, `missing_target_facet_assignments` et les problemes de couverture associes. Le code ne decoupe pas cette requete et n'invente aucune facette.
- Le pipeline termine proprement avec `The retrieval planner did not produce executable retrieval requests` et `The LLM strategy reviewer did not approve an executable retrieval plan after two attempts, so no request was executed.` La memoire reste presente mais `context_only_never_final_proof`; elle ne sert pas de substitut aux preuves courantes.
- Bilan : le changement Live53 est valide reellement, mais Live54 reste non discriminant pour la revision semantique aval. Le nouveau goulot est un contrat de strategie par facettes encore trop lent et trop volumineux sur le modele local. Le prochain correctif doit conserver le LLM comme decideur : produire ou reparer quatre affectations courtes `(candidateId, query)` liees aux quatre colonnes deja approuvees, puis laisser le code verifier la bijection, le caractere executable et l'absence de copie multi-colonnes.

### 2026-07-20 - Strategie LLM positionnelle compacte apres le live 54

- `PlannerCompactFacetPlan` ne demande plus au modele de repeter pour chaque requete le libelle exact de facette, l'outil et sept champs de couverture. Les facettes deja approuvees sont exposees sous forme d'identifiants immuables ordonnes, par exemple `1=Petit-dejeuner | 2=Diner | 3=Souper | 4=Collation`.
- Le LLM conserve toutes les decisions semantiques utiles : `decision`, raison, couverture des lignes, scope exact ou nul, outil partage, une requete par identifiant et un but par identifiant. Le format court utilise deux tableaux paralleles `queries` et `purposes`.
- Le code ne fabrique aucune requete et ne reconnait aucun type de repas. Il rattache seulement la position `i` au libelle de facette deja choisi par les adjudications LLM anterieures, puis reutilise les validateurs existants de bijection, d'outil, de scope, de longueur, de couverture et de qualite.
- Le parseur accepte le nouveau format positionnel et preserve le format historique `requests` pour compatibilite. Une absence de requete, de but, d'outil ou de position reste visible comme probleme mecanique; rien n'est complete par un lexique ou une heuristique metier.
- Le prompt retire les labels de lignes, les repetitions de schema et les instructions de sortie inutiles. Il conserve la question, les contraintes, les identifiants de facettes, les scopes disponibles et les codes de rejet necessaires a la decision courante.
- `SourceBackedLlmOutputBudget` borne exactement `PlannerCompactFacetPlan` et `PlannerCompactFacetPlanFormatRepair` a 256 tokens, contre le budget Planner generique de 640 auparavant. Les autres etapes et delais restent inchanges.
- La regression principale utilise maintenant le contrat positionnel et prouve que les quatre requetes choisies par le LLM sont rattachees dans l'ordre aux quatre facettes, executees sous le scope `Cuisine`, et qu'aucun `targetFacetExact` n'est demande dans le prompt compact.
- Les tests d'architecture prouvent le parsing positionnel, l'ancien format, l'outil partage, les buts paralleles et le caractere `facet_specific` inherant au contrat accepte. Les deux adaptateurs LLM prouvent le transport de `max_tokens=256`.
- Validation : build en 4 min 52 s, 0 avertissement/0 erreur; 5/5 puis 4/4 tests discriminants; 294/294 tests SourceBacked; 374/374 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur de contenu. Une selection exploratoire sur le mot `compact` a retrouve deux echecs hors perimetre deja lies aux anciens chemins OCR/context-role; ils ne touchent aucun fichier modifie par ce correctif et la suite SourceBacked ciblee reste entierement verte.
- Prochaine preuve discriminante : cinquante-cinquieme live canonique. Il doit reproduire l'intake 5 x 4 validee au live 54, terminer le plan compact sous 256 tokens, executer quatre recherches typees et atteindre le writer puis `StructuredValueTypeRepair`. Les criteres fonctionnels finaux restent inchanges.

### 2026-07-20 - Cinquante-cinquieme live : decision de lignes perdue avant l'adjudication des colonnes

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260720-121848/`, trace principale `rag-20260720121933419-b2a3ecc0`, trace SourceBacked `sbrag-deb50d2e410145768b24be59179dc5c6`, journaux processus `artifacts/live-test-process-live55-20260720-141841/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260720_141908.log`.
- Le test termine en 17 min 35 s avec l'insuffisance sure de 293 caracteres, zero outil, zero preuve, zero source et un payload nul. Il n'atteint pas `PlannerCompactFacetPlan`; il ne valide donc pas et n'invalide pas le correctif de strategie positionnelle.
- Le routeur termine en 51,2 s et le Planner en 205,7 s. Contrairement au live 54, la sortie initiale conduit d'abord a `PlannerIntakeReview`, qui termine en 260,0 s.
- L'audit monolithique reconnait correctement la grille et les deux ensembles de labels, mais produit deux ancres exactes concatenees inexistantes. `PlannerIntakeAnchorRepair` separe les membres en 135,8 s, mais traite `Mardi`, `Mercredi` et `Jeudi` comme des occurrences litterales et omet l'approbation de structure; il est refuse.
- Le repli par axes atteint ensuite une bonne decision de lignes en 93,6 s : `Lundi` a `Vendredi`, `Jour`, citation `lundi au vendredi`, relation `range_expansion`, sans probleme mecanique.
- L'adjudication des colonnes termine rapidement en 26,6 s mais renvoie ces memes cinq jours avec `decision=clarify`. Elle a donc reclassifie l'axe ligne courant au lieu de choisir les creneaux repetes.
- Le pipeline ne transportait pas la decision de lignes acceptee dans le prompt de colonnes. Il repassait seulement le `rejectedCandidate.Plan`, c'est-a-dire des opinions anterieures non fiables. Le deuxieme micro-LLM ne savait pas qu'une decision immediatement precedente avait deja verrouille ces labels comme lignes.
- Comme les cinq mauvais candidats avaient des labels non vides et plusieurs problemes d'ancres, le pipeline choisissait a tort `PlannerIntakeColumnAxisAnchorPatch`. Deux patches de 54,7 s et 44,3 s ne pouvaient changer que les metadonnees et ont donc conserve `Lundi` a `Vendredi` comme colonnes.
- La derniere reparation generique termine en 133,1 s, retourne d'anciens champs singuliers et une clarification injustifiee, puis est refusee avec `no_anchor_entries`. Le run se termine avant toute strategie ou recherche.
- Diagnostic : il ne s'agit ni d'un probleme de donnees, ni du nouveau plan compact, mais d'une perte de composition entre deux decisions LLM successives. Une appartenance d'axe deja acceptee doit devenir une entree immuable du micro-contrat suivant; le code peut verifier un conflit entre contrats, mais ne doit pas choisir le nouvel axe.

### 2026-07-20 - Verrouillage et transport de l'axe ligne LLM apres le live 55

- `TryCompletePlannerIntakeAxisAdjudicationAsync` transmet maintenant les labels de la decision de lignes acceptee au prompt d'adjudication des colonnes et a toute reparation de son contrat.
- Le prompt expose `LOCKED_OTHER_AXIS_LABELS_FROM_CURRENT_ACCEPTED_LLM_DECISION`. Il precise que ces labels ont ete attribues a l'autre axe par le LLM immediatement precedent et qu'ils ne doivent pas etre repetes ou reclasses comme colonnes.
- `BuildPlannerIntakeColumnAxisProblems` compare mecaniquement la nouvelle opinion aux labels de lignes verrouilles. Tout recouvrement devient `column_axis_reuses_locked_row_labels`; il s'agit d'un conflit entre deux contrats LLM, pas d'une classification semantique faite par le code.
- Ce probleme desactive le patch d'ancres : modifier citation ou relation ne peut pas reparer une mauvaise appartenance d'axe. Le pipeline retire uniquement l'opinion rejetee de la position d'autorite et demande au LLM une adjudication fraiche, complete et compacte depuis `USER_QUESTION`.
- Le repair frais recoit aussi `LOCKED_ROW_LABELS_FROM_CURRENT_ACCEPTED_LLM_DECISION`. Le LLM choisit seul les nouvelles colonnes, leurs citations et leurs relations; le code ne fournit aucun libelle de repas, lexique ou valeur de remplacement.
- Le retry du contrat transporte le meme verrou et reverifie le conflit. Une seconde sortie qui reutilise les lignes reste refusee; aucune colonne n'est synthetisee.
- La regression `Run_freshly_readjudicates_columns_when_the_llm_repeats_the_accepted_row_axis` reproduit la sortie Live55. Elle exige le code de conflit, le prompt frais, l'absence totale de `PlannerIntakeColumnAxisAnchorPatch`, les quatre colonnes finalement choisies par le second LLM et une intake de vingt cellules.
- Validation : build en 4 min 22 s, 0 avertissement/0 erreur; 4/4 regressions focalisees; 295/295 tests SourceBacked; 375/375 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur de contenu.
- Prochaine preuve discriminante : cinquante-sixieme live. Si le modele repete les jours en colonnes, la trace doit montrer `column_axis_reuses_locked_row_labels` puis un contrat frais. S'il choisit directement les bons creneaux, aucune reparation n'est necessaire. Dans les deux cas, le run doit ensuite atteindre et tester le plan positionnel compact ajoute apres Live54.

### 2026-07-20 - Cinquante-sixieme live : audit d'intake sans forme apres deux timeouts de transport

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260720-124948/`, trace principale `rag-20260720125035498-196a04d3`, trace SourceBacked `sbrag-ba49345bae554a18ab64dcd0ae99b063`, journaux processus `artifacts/live-test-process-live56-20260720-144941/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260720_145010.log`.
- Le test termine en 17 min 45 s avec l'insuffisance sure de 293 caracteres, zero outil, zero preuve, zero source et un payload nul. Il n'atteint ni les micro-adjudications d'axes, ni `PlannerCompactFacetPlan`, ni les etapes aval.
- Le routeur prend 79,1 s. Le Planner initial termine en 334,0 s avec 1 194 caracteres, seulement 26 s avant sa borne exacte de 360 s; cette execution confirme une forte baisse de debit du runtime local.
- Le Planner n'indique pas de `structureReviewKind` exploitable. `PlannerIntakeReview` demarre avec 4 622 caracteres de prompt puis expire exactement a 360,1 s sans JSON complet ni `intakeDecision`.
- La reparation d'ancres suivante termine en 87,6 s avec un ancien dictionnaire associant jours et valeurs. Le parseur ne trouve aucune entree d'ancre typee et retourne `no_anchor_entries`; aucune conversion heuristique n'est appliquee.
- `PlannerIntakeAnchorContractRepair` expire a son tour apres 150,0 s et ne fournit qu'un contrat de timeout synthetique. Il reste refuse avec absence d'approbation, de raison, de kind, de lignes, de colonnes et d'ancres.
- La memoire est encore presente avec `memory_finality=context_only_never_final_proof`; elle ne sert pas a inventer une forme ou des preuves lorsque la decision courante manque.
- Diagnostic : lorsque l'audit complet expire avant de fournir un `RetrievalPlan`, le fallback d'axes ne peut pas demarrer car il exige deja `StructureReviewKind=two_axis_grid`. Les repairs generiques d'ancres essaient alors de reconstruire trop de responsabilites et ne recuperent pas la seule decision minimale manquante : la forme.

### 2026-07-21 - Micro-adjudication LLM de forme apres le live 56

- Le nouveau contrat `PlannerIntakeShapeAdjudication` est appele uniquement lorsque l'audit d'intake requis ne fournit aucun plan exploitable. Il ne remplace pas un audit valide et ne s'execute pas sur les parcours qui possedent deja une decision typee.
- Le LLM choisit exclusivement `two_axis_grid`, `single_axis_list` ou `free_form`, avec `decision=accept` et une raison courte; il peut choisir `clarify` si la question courante ne permet reellement pas de determiner la forme.
- Le prompt interdit explicitement de choisir des labels de lignes, des labels de colonnes, des sources, des recherches ou le contenu de la reponse. Il transporte uniquement la question, les contraintes, des indices Planner non fiables et les codes de rejet de l'audit complet.
- Si et seulement si le LLM choisit `two_axis_grid`, le code cree un contrat de routage portant cette decision puis appelle les adjudications LLM separees des lignes et des colonnes. Le code ne deduit aucun axe et ne fournit aucun jour, repas ou valeur candidate.
- Une clarification de forme choisie par le LLM est respectee comme arret sur. Les formes acceptees autres que la grille restent confiees aux chemins existants; elles ne sont pas transformees en axes par code.
- Le parseur `ParseIntakeShapeDecision` et le verificateur mecanique refusent decision inconnue, raison vide, kind inconnu et combinaison `clarify` avec kind residuel. Les traces `SBRAG_PLANNER_INTAKE_SHAPE` exposent parsing, acceptation, decision, kind et codes.
- Le budget exact de la micro-decision est 256 tokens; son delai reste la borne Planner focalisee de 150 s. Le schema attendu tient en trois champs et evite la sortie de 480 tokens qui a expire au live 56.
- La regression `Run_recovers_a_timed_out_intake_review_with_focused_shape_and_axis_llm_decisions` reproduit l'absence d'`intakeDecision`, fait choisir `two_axis_grid` au LLM, obtient ensuite cinq lignes, quatre colonnes et un plan de quatre recherches sans appeler les repairs d'ancres historiques.
- Le test d'architecture prouve un prompt generique sous 3 200 caracteres et le parsing du contrat. Les deux adaptateurs LLM prouvent `max_tokens=256`. La limite de modularite a signale `PlannerIntakeReview.cs` a 501 lignes; la mise en forme de l'appel a ete compactee et le fichier reste sous 500 sans assouplir la garde.
- Validation : build initial en 3 min 24 s puis incremental en 27 s, 0 avertissement/0 erreur; 6/6 regressions focalisees; 297/297 tests SourceBacked; 377/377 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur de contenu.
- Prochaine preuve discriminante : cinquante-septieme live. Si l'audit complet expire ou reste sans structure, la trace doit montrer `planner_intake_shape_adjudication`, puis les decisions de lignes et colonnes, puis le plan positionnel compact. Le gate final reste toujours le writer, `StructuredValueTypeRepair`, vingt cellules plausibles et les cartes de sources.

### 2026-07-21 - Cinquante-septième live : bonne décision LLM présente, mais désynchronisée entre deux champs du contrat

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260721-151723/`, trace principale `rag-20260721151806743-c2edb8b1`, trace SourceBacked `sbrag-a19cb18286074b2a802b1d5b56672f80`, journaux processus `artifacts/live-test-process-live57-20260721-171716/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260721_171741.log`.
- Le test termine en 7 min 55 s avec une insuffisance sûre de 293 caractères, zéro outil, zéro preuve, zéro source et un payload nul. Il n'atteint toujours ni la stratégie compacte, ni le RAG, ni le writer, ni les juges et réparations de valeurs structurées.
- Le routeur termine en 32,7 s et le Planner initial en 173,8 s. `PlannerIntakeReview` termine en 162,6 s avec un JSON parseable au lieu d'expirer; la nouvelle micro-adjudication de forme n'a donc pas à s'exécuter et n'est ni validée ni invalidée par ce run.
- L'audit LLM choisit explicitement `two_axis_grid` et déclare dans `structureAnchorGroups` un groupe ligne `Lundi` à `Vendredi` ainsi qu'un groupe colonne contenant les créneaux de repas. La décision sémantique utile existe donc déjà dans la sortie courante du LLM.
- Les tableaux homologues `intakeDecision.rowLabels` et `intakeDecision.columnLabels` restent cependant vides. Le code construisait l'intake uniquement depuis ces tableaux, puis validait les groupes d'ancres contre cette intake vide. Il produisait simultanément `non_tabular_structure_has_anchor_groups`, `two_axis_grid_requires_multiple_rows` et `two_axis_grid_requires_multiple_columns`.
- Comme l'intake effective rejetée ne contenait aucun axe, `PlannerIntakeAnchorRepair` recevait l'ancienne proposition plutôt que les décisions présentes dans les groupes d'ancres. Il retournait alors une clarification injustifiée et aucun contrat d'ancres type.
- Le fallback par axes récupère ensuite correctement les cinq jours; le patch de métadonnées ajoute la relation manquante en 3,3 s. Le verrou introduit après Live55 fonctionne réellement : l'adjudication de colonnes et ses deux réparations réutilisent `Lundi` à `Vendredi`, et les trois sorties sont refusées avec `column_axis_reuses_locked_row_labels`.
- La dernière réparation d'ancres retourne finalement d'anciens dictionnaires et `decision=clarify`; elle reste non parseable par le contrat type. Le pipeline s'arrête proprement sans exécuter une requête douteuse.
- Diagnostic précis : la panne n'est pas une absence de décision sémantique, mais une perte de composition entre deux représentations du même contrat LLM. Les labels, leur axe et leurs ancres existaient déjà; le code ne les transportait pas vers l'intake soumise aux vérificateurs et aux repairs suivants.

### 2026-07-21 - Composition mécanique des labels déjà décidés dans les groupes d'ancres LLM

- Le nouveau module `SourceBackedRagPipeline.PlannerIntakeContractComposition.cs` compose les deux champs d'un même résultat LLM avant de construire l'intake effective.
- Il ne s'active que lorsque le LLM a rendu `structureReviewDecision=accept`, choisi une forme structurée reconnue (`two_axis_grid` ou `single_axis_table`), fourni une `intakeDecision` et déclaré des `structureAnchorGroups`.
- Si le tableau d'un axe est vide, le code y transporte, dans l'ordre, les `labelsExact` des groupes dont le LLM a lui-même déclaré `axis=row` ou `axis=column`. Un tableau non vide n'est jamais remplacé. Aucun jour, repas, langue, plage, alias ou valeur métier n'est reconnu par le code.
- Cette composition n'accepte pas les ancres. Après transport, tous les contrôles existants restent exécutés : axe connu, couverture exacte, citation présente dans la question, relation autorisée, cardinalité de relation, doublon, chevauchement d'occurrences, nombre de lignes et colonnes et en-tête de ligne.
- Une trace dédiée `SBRAG_PLANNER_INTAKE_CONTRACT_COMPOSITION` rend le transport observable en live avec les deux listes réellement composées.
- La régression qui reproduit la désynchronisation du Live57 exige maintenant que `PlannerIntakeAnchorRepair` reçoive `Lundi` à `Vendredi` et les cinq candidats de colonnes, et qu'il ne voie plus le faux problème `non_tabular_structure_has_anchor_groups`.
- Validation : build implicite du projet en 4 min 41 s, 0 erreur; régression Live57 1/1; tests intake/axes/ancres 11/11; suite SourceBacked 297/297; lot combiné SourceBacked/OpenAI/ApiClient 376/376; `git diff --check` sans erreur de contenu, seulement les avertissements de fins de ligne hérités.
- Prochaine preuve discriminante : Live58 doit montrer la trace de composition immédiatement après l'audit si le modèle répète cette forme de JSON. La réparation d'ancres doit conserver les candidats LLM initiaux, résoudre leurs relations ou conflits, puis atteindre `PlannerCompactFacetPlan`. Le succès final exige toujours des recherches exécutées, des preuves actuelles, le writer, les juges/réparations de valeurs, vingt cellules plausibles et des cartes de sources utiles.

### 2026-07-21 - Cinquante-huitième live : grille 5 x 4 et RAG atteints, mais requêtes dites indépendantes limitées à Lundi

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260721-153945/`, trace principale `rag-20260721154020872-443c2007`, trace SourceBacked `sbrag-19971b900f2f457cbc4111f00749bad0`, journaux processus `artifacts/live-test-process-live58-20260721-173939/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260721_174001.log`.
- Le test xUnit termine en 17 min 47 s. Le pipeline après disponibilité du LLM termine en 1 031,9 s avec une insuffisance sûre de 328 caractères, zéro carte source et aucun writer. Contrairement aux lives 54 à 57, il exécute réellement `rag.search` et `documents.context`.
- Le routeur termine en 44,5 s et le Planner en 145,5 s. Le Planner identifie assez bien la grille pour déclencher directement les micro-adjudications, donc ni l'audit monolithique ni la composition ajoutée après Live57 ne sont utilisés sur cette trajectoire.
- L'adjudication des lignes termine en 62,4 s et valide directement `Lundi` à `Vendredi`. L'adjudication des colonnes termine en 35,4 s avec cinq candidats et des relations parallèles incomplètes; le patch d'ancres termine en 38,4 s et expose le chevauchement exact `Petit-déjeuner` / `Déjeuner`.
- Le sélecteur LLM par identifiants termine en 9,6 s, élimine le candidat imbriqué et conserve exactement `Petit-déjeuner`, `Dîner`, `Souper`, `Collation`. L'intake est acceptée sans problème mécanique avec vingt cellules.
- `PlannerQueryStrategyRepair` expire à sa borne de 150 s. Pour la première fois, le nouveau `PlannerCompactFacetPlan` s'exécute réellement : prompt de 2 657 caractères, réponse en 7,5 s. Le modèle choisit cependant `clarify`, une couverture invalide et une seule requête vide; le contrat compact est correctement refusé.
- `PlannerQueryStrategyRetry` termine en 38,4 s avec quatre requêtes et approuve `row_independent_candidate_pool`, mais omet les quatre `targetFacetExact`. Le micro-patch LLM d'affectation termine en 6,8 s et lie correctement `1->1,2->2,3->3,4->4`.
- Le plan accepté contient toutefois `Petit-déjeuner sources for Lundi`, `Dîner sources for Lundi`, `Souper sources for Lundi` et `Collation sources for Lundi`. Cette copie d'une seule ligne contredit la décision LLM de réserve réutilisable indépendante des lignes, mais le contrat mécanique ne contrôlait jusque-là que les copies de plusieurs colonnes.
- Les quatre recherches retournent respectivement 1, 10, 2 et 4 hits. Le premier `EvidenceBundle` contient 16 éléments. Un repair d'action du juge ajoute une recherche `sources recettes souper vendredi Cuisine`, qui retourne un hit; le bundle passe à 17 éléments.
- Le juge rend ensuite `decision=answer` sans aucun `evidenceIdsToUse`. L'état de travail LLM choisit `read_documents`, avec `E1,E5` utiles et `E2` faible; deux appels `documents.context` lisent le même livre de recettes autour des pages 10-11. Le bundle final contient 24 éléments provenant de 7 appels d'outils, sans timeout ni erreur de retrieval.
- Après lecture, le juge répète `decision=answer` avec zéro ID. Les états LLM signalent seulement une exigence couverte, une exigence manquante et une base ne contenant pas un plan hebdomadaire complet. Les repairs de sélection ne produisent aucun ensemble d'IDs mécaniquement acceptable; le dernier repair final est refusé et l'itération s'arrête avec `no_executable_follow_up`.
- Bilan honnête : Live58 prouve l'intake 5 x 4, la sélection d'axes, la stratégie retry, les affectations de facettes, le RAG, l'`EvidenceBundle`, la lecture documentaire et les garde-fous de sélection. Il ne prouve ni le writer, ni `StructuredValueTypeFitJudge`, ni `StructuredValueTypeRepair`, ni une réponse professionnelle. Le corpus récupéré reste trop étroit parce que les quatre requêtes prétendument réutilisables ont toutes été ancrées sur une seule ligne.

### 2026-07-21 - Contrat de cohérence LLM entre couverture des lignes et texte des requêtes après Live58

- Le nouveau module `SourceBackedRagPipeline.PlannerRowCoverage.cs` compare mécaniquement deux décisions du même LLM : `queryCoverageDecision` et le texte final de chaque requête.
- Si le LLM choisit `row_independent_candidate_pool`, une requête qui recopie n'importe quel label de ligne de l'intake acceptée produit maintenant `row_independent_query_copies_typed_rows`. Le code ne reconnaît aucune date, aucun jour et aucun type métier : il compare uniquement les labels de lignes déjà approuvés par le LLM après normalisation mécanique.
- Le problème expose les indices de requêtes et les labels copiés. Il ne retire aucun terme et ne fabrique aucune requête; il indique au LLM de supprimer les labels de placement ou de changer explicitement sa décision vers `row_specific_evidence`.
- `PlannerFacetAssignmentPatch` accepte ce problème comme résiduel et composable : il peut d'abord lier les IDs de requêtes aux IDs de facettes, puis réévaluer le plan. La contradiction de ligne reste ouverte et déclenche ensuite `PlannerFacetQueryReview`.
- `PlannerFacetQueryReview` reçoit désormais `TYPED_ROW_LABELS` en plus des colonnes et du rapport mécanique. Le LLM conserve seul la responsabilité de réécrire query et purpose. `EvaluatePlannerStrategyCandidate` rejoue ensuite le nouveau contrôle; une réécriture qui conserverait `Lundi` serait encore refusée.
- La priorité d'observabilité a aussi été corrigée : quand affectation puis review des requêtes s'enchaînent, `planner_diversity_repair_outcome` attribue le succès à `accepted_after_retry_facet_query_review`, dernière décision LLM réellement appliquée, et non au patch intermédiaire.
- La régression `Run_rewrites_row_independent_queries_that_copy_one_typed_row_before_execution` reproduit exactement les quatre requêtes Live58, leurs affectations par IDs et une review LLM de remplacement. Elle exige l'absence de `Lundi` dans le plan exécuté et la présence du problème, des quatre indices et des cinq lignes dans le prompt de review.
- Validation : régression Live58 1/1; stratégie, affectations, review et plan compact 5/5; suite SourceBacked 298/298; lot combiné SourceBacked/OpenAI/ApiClient 377/377; compilation 0 erreur; `git diff --check` sans erreur de contenu, seulement les avertissements de fins de ligne hérités.
- Prochaine preuve discriminante : Live59 doit retrouver une intake 5 x 4, refuser tout plan `row_independent_candidate_pool` qui copie un jour, appeler `PlannerFacetQueryReview`, exécuter quatre recherches de domaine réutilisables et livrer au juge un `EvidenceBundle` plus riche. Le gate demeure le writer, les réparations de valeurs, vingt cellules plausibles et les cartes de sources utiles.

### 2026-07-21 - Cinquante-neuvième live : les deux nouveaux garde-fous fonctionnent, mais la première révision LLM conserve encore les jours

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260721-161421/`, trace principale `rag-20260721161456547-66b4e4cc`, trace SourceBacked `sbrag-b50807b201184ba899731119419340c4`, journaux processus `artifacts/live-test-process-live59-20260721-181416/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260721_181437.log`.
- Le test xUnit termine en environ 10 min 37 s. Le pipeline termine en 602,1 s avec une insuffisance sûre de 298 caractères, sans outil exécuté, sans `EvidenceBundle`, sans source et avec un payload nul. Live59 n'est donc pas une réussite fonctionnelle, mais il valide en conditions réelles les deux corrections issues de Live57 et Live58.
- Le routeur termine en 29,5 s et le Planner initial en 166,6 s. `PlannerIntakeReview` termine en 154,1 s et reproduit exactement la désynchronisation de Live57 : il choisit `two_axis_grid` et place les cinq jours ainsi que les quatre créneaux dans les groupes d'ancres, tandis que les tableaux homologues de l'`intakeDecision` restent vides.
- La nouvelle composition mécanique s'exécute réellement et trace `SBRAG_PLANNER_INTAKE_CONTRACT_COMPOSITION` avec `Lundi | Mardi | Mercredi | Jeudi | Vendredi` pour les lignes et `Petit-déjeuner | Dîner | Souper | Collation` pour les colonnes. Les décisions sémantiques du même LLM ne sont plus perdues entre deux champs de son contrat.
- Les ancres concaténées du premier audit restent correctement refusées. `PlannerIntakeAnchorRepair` termine en 72,8 s mais propose à tort `Semaine` comme ligne; cette proposition n'est pas acceptée. L'adjudication focalisée des lignes termine ensuite en 46,9 s et valide les cinq jours.
- L'adjudication des colonnes termine en 7,9 s. Son patch compact termine en 8,3 s et conserve exactement `Petit-déjeuner`, `Dîner`, `Souper`, `Collation`. L'intake 5 x 4 est finalement valide, sans heuristique de repas ni de calendrier dans le code.
- La première réparation de stratégie termine en 46,9 s et copie la mise en page ainsi que plusieurs jours et colonnes dans les requêtes. Le nouveau contrat de cohérence détecte `Lundi` et `Vendredi` dans les quatre requêtes; le contrôle historique signale aussi la copie de plusieurs colonnes. Le plan n'est pas exécuté.
- `PlannerQueryStrategyRetry` termine en 41,7 s. Il choisit explicitement `row_independent_candidate_pool` mais produit quatre requêtes encore liées respectivement à `Lundi`, `Mardi`, `Mercredi` et `Jeudi`, sans affectations de facettes.
- `PlannerFacetAssignmentPatch` termine en 7,4 s et affecte mécaniquement les IDs `1->1,2->2,3->3,4->4`. Comme prévu, cette composition ne masque pas le problème résiduel de lignes : le plan reste refusé et passe à la revue sémantique focalisée.
- `PlannerFacetQueryReview` termine en 17,1 s. Le LLM rend un JSON complet et réécrit les requêtes en anglais, mais conserve toujours `Lundi`, `Mardi`, `Mercredi` et `Jeudi` malgré les lignes verrouillées et la décision de réserve indépendante. `EvaluatePlannerStrategyCandidate` rejoue le contrat et refuse donc correctement cette révision.
- La panne est désormais très localisée : une sortie de revue parseable et structurellement complète mais encore sémantiquement contradictoire n'avait droit à aucun second avis borné. Le pipeline s'arrêtait avant le RAG, alors que la première révision avait déjà prouvé qu'elle comprenait le format et qu'une correction focalisée restait possible.
- Bilan honnête : Live59 prouve en live la composition des axes déclarés dans les groupes d'ancres, la reconstruction exacte de la grille 5 x 4, la détection des jours copiés dans une stratégie prétendument indépendante, la préservation du problème après affectation des facettes et le refus d'une révision LLM encore fautive. Il ne prouve toujours ni retrieval, ni writer, ni réparations de valeurs, ni réponse professionnelle.

### 2026-07-21 - Un retry sémantique borné pour une revue de requêtes lisible mais encore contradictoire

- `SourceBackedRagPipeline.PlannerFacetQueryReviewRetry.cs` introduit un seul second avis sémantique après `PlannerFacetQueryReview`. Il ne s'exécute que lorsque la première revue est parseable, respecte le contrat structurel, a été appliquée, reste mécaniquement refusée et ne contient que des problèmes explicitement reviewables.
- Le retry reçoit l'intake acceptée, le plan rejeté et les problèmes précis issus de sa propre première révision. Pour `row_independent_candidate_pool`, le prompt exige qu'aucun `TYPED_ROW_LABEL` n'apparaisse dans les requêtes finales; le LLM peut réécrire toutes les requêtes ou choisir `clarify` s'il ne peut pas produire un ensemble défendable.
- Le code conserve immuables l'outil, la catégorie et l'index de chaque requête. Il transporte seulement les choix LLM `targetFacetExact`, `query`, `purpose`, `QueryQualityDecision` et `QueryQualityReason`, puis relance exactement les mêmes vérifications de facettes, de couverture et de lignes. Il ne retire aucun jour, ne fabrique aucun terme de recherche et ne transforme aucune décision négative en acceptation.
- Le retry est borné à une seule tentative. Un JSON incomplet, une couverture de facettes invalide, un `clarify` ou une seconde révision qui recopie encore une ligne reste un arrêt sûr avant exécution.
- La régression Live59 fournit d'abord les quatre révisions encore fautives observées en live (`Lundi`, `Mardi`, `Mercredi`, `Jeudi`), vérifie qu'elles sont refusées, puis fournit une seconde décision LLM utilisant des termes de domaine réutilisables. Elle exige qu'aucun jour n'atteigne l'exécuteur et que la trace attribue l'acceptation à `accepted_after_retry_facet_query_review`.
- Validation : régression Live59 1/1; contrats stratégie/facettes 5/5; suite SourceBacked 298/298; lot combiné SourceBacked/OpenAI/ApiClient 378/378; compilation 0 erreur; `git diff --check` sans erreur de contenu, seulement les avertissements de fins de ligne hérités.
- Prochaine preuve discriminante : Live60 doit atteindre l'intake 5 x 4 puis, si la première revue reproduit des jours, tracer `planner_facet_query_review_retry`. Seules quatre requêtes sans ligne de placement peuvent être exécutées. Le prochain vrai palier reste ensuite un `EvidenceBundle` suffisamment riche, une sélection d'IDs explicite, le writer, les audits/réparations de valeurs et une table de vingt cellules avec payload et cartes de sources utiles.

### 2026-07-21 - Soixantième live : writer et audit sémantique atteints, mais suivi RAG vide après le rejet des valeurs

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260721-163522/`, trace principale `rag-20260721163601728-3929a027`, trace SourceBacked `sbrag-e5de2774fc9e4d73b15e9ed2be75df9a`, journaux processus `artifacts/live-test-process-live60-20260721-183516/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260721_183539.log`.
- Le test xUnit échoue proprement après 18 min 44 s sur l'absence de payload de sources. Le pipeline actif prend environ 1 084,5 s et livre uniquement la réponse de sécurité de 703 caractères. Aucun planning ni aucune carte source non vérifiés ne sont exposés.
- Le routeur est particulièrement lent à 155,0 s et le Planner prend 186,3 s. Les micro-adjudications reconstruisent néanmoins une intake exacte de cinq lignes par quatre colonnes : la décision de lignes est réparée par métadonnées, le chevauchement `Petit-déjeuner` / `Déjeuner` est résolu par le sélecteur LLM et les vingt cellules demandées sont reconnues.
- La première réparation de stratégie copie toute la grille ainsi que `Lundi` et `Vendredi`; les garde-fous de lignes et de colonnes la refusent. Le retry choisit ensuite quatre requêtes courtes (`Petit-déjeuner sources Cuisine`, `Dîner sources Cuisine`, `Souper sources Cuisine`, `Collation sources Cuisine`) sans affectations de facettes. Le patch LLM d'affectation ajoute les quatre IDs et le plan devient mécaniquement exécutable.
- Cette trajectoire ne déclenche pas le nouveau `PlannerFacetQueryReviewRetry`, car aucune ligne n'est copiée dans les quatre requêtes finales. Live60 ne valide donc pas ce retry ajouté après Live59. Il révèle en revanche que des requêtes lexicalement faibles peuvent encore être acceptées lorsque leur structure est correcte.
- Les quatre recherches retournent respectivement 10, 10, 1 et 1 hits. Le premier `EvidenceBundle` contient 22 éléments et quatre tentatives. Deux cycles de lectures documentaires portent ensuite le bundle à 27 puis 47 éléments, issus de dix appels d'outils sans timeout de retrieval.
- À chaque tour, l'Evidence Judge demande de répondre mais omet les IDs. Les réparations de sélection transportent les décisions du working memory : deux lectures au premier tour, quatre au second, puis une réponse finale autorisée uniquement avec `E1`. La mémoire conversationnelle reste `context_only_never_final_proof` et ne remplace jamais les preuves courantes.
- Le writer est atteint et termine en 101,6 s. Il produit un tableau complet de 977 caractères, avec vingt cellules, citation visible `E1` et contrat mécanique valide.
- `StructuredValueTypeFitJudge` termine en 69,2 s et rejette les dix valeurs distinctes `V1` à `V10`. Le diagnostic du modèle est parfois formulé trop catégoriquement, mais le problème de fond est réel : le writer a composé vingt cellules différentes à partir d'une seule preuve sélectionnée, insuffisante pour soutenir chaque valeur demandée.
- Le premier `StructuredValueTypeRepair` termine en 66,4 s et choisit correctement `need_more_evidence`, mais renvoie `followUpRequests=[]`. Son retry historique, encore volumineux, termine en 20,9 s et reproduit exactement le tableau vide. Le contrat est refusé avec `missing_follow_up_request`.
- Le repair générique d'action du juge termine ensuite en 32,8 s sans action exécutable. L'itération s'arrête avec `llm_answer_adequacy_failed` et `verification_follow_up_not_authorized_by_llm`. Le goulot Live60 est donc précis : la décision sémantique de chercher davantage est présente, mais sa traduction en action RAG manque.

### 2026-07-21 - Réparation LLM compacte de l'action après rejet des types structurés

- Le prompt principal `SourceBackedStructuredValueTypeRepairPrompt` distingue maintenant deux formes de sortie explicites. `decision=revise` doit fournir une réponse complète et zéro suivi; `decision=need_more_evidence` doit fournir au moins une vraie requête RAG et ne peut plus présenter `followUpRequests=[]` comme exemple visuel contradictoire.
- Si le premier réparateur choisit `need_more_evidence`, explique son choix, ne fournit aucune révision et échoue uniquement sur le contrat d'action, le second avis borné utilise désormais `StructuredValueTypeFollowUpRepair` au lieu de répéter le prompt complet d'environ 7 000 caractères.
- Ce micro-contrat expose au LLM les champs rejetés, leurs raisons sémantiques, les catégories connues et les recherches déjà tentées. Le LLM choisit seul une unique action plate `rag.search` ou `rag.multi_search`, sa requête, son scope et son but, ou choisit `clarify`. Le code ne choisit aucun aliment, repas, terme de recherche ou catégorie.
- Le code vérifie uniquement le contrat mécanique : décision exécutable, requête non vide, scope transporté et absence de répétition avec le plan initial ou les tentatives réellement enregistrées dans l'`EvidenceBundle`. Un suivi vide, non exécutable ou répété demeure refusé; aucune requête générique n'est synthétisée.
- Le parseur réutilise le contrat existant `SourceBackedToolActionDecision`. Les seules exceptions absorbées sont les échecs JSON/contrat attendus (`JsonException` et `InvalidOperationException`); les autres défauts ne sont pas masqués.
- Le budget exact de ce second avis est 256 tokens et son délai est 120 s. La réponse attendue est un objet JSON plat, court et adapté au modèle local 3B.
- La nouvelle régression `Run_repairs_an_empty_structured_value_type_follow_up_with_one_flat_llm_action` reproduit la table incorrecte, le rejet ciblé, la décision `need_more_evidence` vide, puis une action LLM `complete breakfast supper snack recipe titles`. Elle prouve cinq appels LLM, le prompt compact, l'absence de la réponse complète dans ce prompt et le transport d'un suivi exécutable sans synthèse par code.
- Validation déterministe post-correctif : nouvelle régression 1/1; quatre régressions de réparation/refus 4/4; suite SourceBacked 299/299; lot combiné SourceBacked/OpenAI/ApiClient 379/379; compilation WinUI et tests réussie; `git diff --check` sans erreur de contenu, uniquement les avertissements de fins de ligne hérités.
- Prochaine preuve discriminante : Live61 doit atteindre à nouveau le writer et le rejet sémantique, puis tracer `structured_value_type_follow_up_repair`. Si le LLM fournit une nouvelle requête valide, elle doit être exécutée dans un tour de preuves supplémentaire avant une nouvelle rédaction et un nouvel audit. Le succès fonctionnel exige toujours une table professionnelle de vingt cellules, des citations visibles vérifiées, au moins deux cartes source utiles et aucun fallback d'insuffisance.

### 2026-07-21 - Soixante-et-unième live : le plan compact atteint le RAG, mais ses requêtes génériques passent sans second avis sémantique

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260721-171524/`, trace principale `rag-20260721171558437-beda2f35`, trace SourceBacked `sbrag-20709167910641769d2e4ec4a461094e`, journaux processus `artifacts/live-test-process-live61-20260721-191519/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260721_191540.log`.
- Le test xUnit échoue proprement après environ 17 min 04 s sur l'absence de payload de sources. Le runtime s'arrête correctement et n'expose qu'une insuffisance sûre de 285 caractères, sans carte ni citation non vérifiée.
- L'intake exacte de cinq jours par quatre créneaux est reconstruite. L'adjudication des lignes conserve les cinq jours et reçoit son patch de métadonnées. L'adjudication des colonnes propose cinq candidats, dont `Déjeuner` imbriqué dans `Petit-déjeuner`; le patch d'ancres puis le sélecteur LLM par identifiants retirent le candidat conflictuel et préservent les quatre facettes demandées.
- La première stratégie copie encore toute la grille et les jours; elle est refusée. Le retry copie encore les jours et omet les affectations. Le patch d'affectation LLM lie les quatre requêtes aux quatre facettes, puis `PlannerFacetQueryReview` conserve encore les jours.
- Le nouveau `PlannerFacetQueryReviewRetry` ajouté après Live59 s'exécute donc pour la première fois en conditions réelles. Le LLM choisit `clarify`; le pipeline respecte ce refus et ne force aucune requête.
- Le repli `PlannerCompactFacetPlan` termine ensuite et rend quatre requêtes positionnelles : `Petit-déjeuner Cuisine sources`, `Dîner Cuisine sources`, `Souper Cuisine sources`, `Collation Cuisine sources`. Leur structure, leur outil, leur scope et leur bijection de facettes sont valides, mais leur vocabulaire ne cherche pas explicitement des recettes, titres, ingrédients ou autres valeurs complètes.
- Ces quatre requêtes sont exécutées sans autre avis sémantique. Elles retournent respectivement 8, 12, 12 et 12 hits. Deux lectures documentaires supplémentaires portent le bundle à 37 éléments et six appels d'outils; aucun timeout de retrieval ne survient.
- L'Evidence Judge choisit deux fois `answer` sans fournir d'identifiants. La seconde revue de statut, les réparations de sélection et la réparation de fin de budget n'obtiennent ni sélection finale d'IDs ni suivi exécutable. Le writer n'est donc pas appelé et l'audit des types structurés n'est pas atteint.
- La mémoire reste correctement cantonnée à `context_only_never_final_proof`; elle n'est jamais utilisée pour combler le manque de preuve sélectionnée.
- Bilan honnête : Live61 valide le retry de revue sémantique, le repli compact, quatre recherches RAG, six appels d'outils et un `EvidenceBundle` de 37 éléments. Il ne valide pas encore le nouveau repair d'action après rejet des types, car le pipeline s'arrête avant le writer. Le nouveau défaut précis est qu'un plan compact mécaniquement parfait pouvait encore être exécuté sans contrôle LLM indépendant de la qualité sémantique de ses requêtes.

### 2026-07-21 - Audit LLM indépendant de toute stratégie structurée acceptée sans revue sémantique préalable

- `PlannerAcceptedQueryAudit` ajoute un avis LLM indépendant entre l'acceptation mécanique d'une stratégie de grille et son exécution. Il ne s'applique qu'aux grilles structurées de deux à quatre facettes, avec une requête par facette, une couverture `facet_specific` et une stratégie déjà approuvée.
- L'audit est déclenché pour un plan compact accepté ou pour une stratégie directe/retry qui n'a pas déjà été acceptée par `PlannerFacetQueryReview`. Une stratégie déjà approuvée par cette revue focalisée n'est pas auditée une seconde fois.
- Le prompt qualifie explicitement le plan de provisoire et interdit le rubber-stamping. Le LLM doit décider si chaque requête a de bonnes chances de retrouver des passages contenant des valeurs candidates concrètes et complètes pour sa facette exacte.
- Le LLM peut conserver une requête forte, la réécrire avec un vocabulaire de domaine orienté vers le contenu des réponses, ou choisir `clarify`. Les mots génériques de dépôt, le scope, le seul nom de facette et les formulations du type `sources for` sont présentés au LLM comme insuffisants; aucun de ces jugements n'est encodé comme heuristique d'acceptation dans le code.
- Les index, outils, scopes et affectations de facettes restent immuables. Le code applique uniquement les choix `query` et `purpose` du LLM, puis rejoue les contrats mécaniques existants de bijection, couverture, outil, catégorie, longueur et exclusion des lignes de placement.
- Un JSON invalide, un `clarify`, une facette absente, une requête vide ou un contrat mécanique encore invalide reste un refus sûr. Le code n'ajoute aucun terme de domaine, ne corrige aucune requête par lexique et ne transforme jamais un refus en acceptation.
- Les traces distinguent désormais `accepted_after_query_strategy_query_audit`, `accepted_after_compact_facet_plan_query_audit` et leurs variantes retry. Un échec final après l'audit devient `rejected_after_independent_query_audit_no_execution`.
- Le budget exact de l'audit est limité à 480 tokens. Le test d'architecture vérifie un prompt générique sous 5 000 caractères, son indépendance, le caractère provisoire du plan, l'absence de vocabulaire Cuisine codé en dur et la déclaration que l'application ne juge pas le sens des requêtes.
- La régression principale reproduit les quatre requêtes faibles de Live61, exige l'appel à `PlannerAcceptedQueryAudit`, fait réécrire ces requêtes par le LLM vers des recherches de recettes/candidats concrètes et prouve que seules les versions révisées atteignent l'exécuteur.
- Les quatorze scénarios dont les files LLM de test devaient intégrer ce nouvel appel ont été adaptés sans assouplir le comportement. Validation : 14/14 régressions concernées; 3/3 contrats prompt/budget; suite SourceBacked 299/299; lot combiné SourceBacked/OpenAI/ApiClient 379/379; compilation 0 avertissement/0 erreur; `git diff --check` sans erreur de contenu, uniquement les avertissements de fins de ligne hérités.
- Prochaine preuve discriminante : Live62 doit appeler `PlannerAcceptedQueryAudit` avant toute exécution d'un plan compact ou direct non déjà revu. Les requêtes faibles de type `Facette Cuisine sources` ne doivent plus atteindre le RAG inchangées. Si le LLM les transforme en recherches porteuses de contenu et que le bundle permet enfin une sélection d'IDs, le run doit atteindre le writer, `StructuredValueTypeFitJudge`, puis valider en live `StructuredValueTypeFollowUpRepair` si une preuve supplémentaire est encore nécessaire.

### 2026-07-21 - Soixante-deuxième live : grille valide, mais réparation du plan compact désynchronisée avant l'audit indépendant

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260721-175719/`, trace principale `rag-20260721175757581-28c58042`, trace SourceBacked `sbrag-25ece3075e264fad862b85767592ee30`, journaux processus `artifacts/live-test-process-live62-20260721-195711/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260721_195736.log`.
- Le test échoue proprement après environ 14 min 02 s sur le payload nul. Le pipeline termine en 801,9 s avec l'insuffisance sûre de 298 caractères, zéro outil, zéro preuve et zéro carte. Le runtime local est arrêté automatiquement.
- Le routeur termine en 42,5 s. Le Planner initial termine en 214,2 s avec 1 067 caractères et fournit assez de contexte pour déclencher les adjudications focalisées.
- L'adjudication des lignes rend les cinq jours en 35,4 s; le patch de métadonnées ajoute la relation manquante en 6,3 s. Le libellé de dimension devient `Jours de la semaine` et l'axe ligne est valide.
- L'adjudication des colonnes rend cinq candidats en 19,4 s. Le patch d'ancres termine en 52,6 s et expose le chevauchement exact `Petit-déjeuner` / `Déjeuner`. Le sélecteur par identifiants tranche en 7,7 s et conserve exactement `Petit-déjeuner`, `Dîner`, `Souper`, `Collation`. L'intake 5 x 4 est donc à nouveau prouvée en live sans heuristique métier.
- La première stratégie termine en 137,7 s, approuve une seule requête `plan de repas semaine lundi au vendredi`, et est correctement refusée pour copie de `Lundi`/`Vendredi`, couverture de facettes invalide et affectation inconnue. Aucun outil n'est exécuté.
- Le retry de stratégie atteint sa borne exacte de 150 s et ne livre aucun contrat utilisable. Le pipeline bascule alors vers `PlannerCompactFacetPlan`.
- Le plan compact initial termine en 22,6 s avec seulement 168 caractères et n'est pas un objet JSON exploitable. `PlannerCompactFacetPlanFormatRepair` est donc appelé avec un prompt de 1 551 caractères.
- Cette réparation termine en 109,8 s avec 837 caractères. Le serveur local indique exactement 256 tokens générés, soit la totalité du budget. L'objet est parseable, mais ses champs sont désynchronisés : `decision=ACCEPT`, `rowCoverage` invalide, `tool=Search and selection of recipes`, requêtes vides et buts incomplets.
- Les validateurs refusent mécaniquement ce contrat avec `invalid_compact_query_coverage_decision`, `invalid_compact_facet_tool`, `empty_compact_facet_query` et les problèmes homologues. Aucun mot, outil ou requête de remplacement n'est inventé par le code.
- `PlannerAcceptedQueryAudit` n'est jamais atteint parce qu'aucun plan compact acceptable ne lui est présenté. Live62 ne valide donc ni n'invalide le nouvel audit indépendant; il déplace le blocage juste avant lui, dans le micro-contrat de réparation du plan compact.
- La mémoire reste `context_only_never_final_proof`. Elle n'est utilisée ni pour fabriquer une grille, ni pour produire une recherche, ni pour répondre sans preuve.

### 2026-07-21 - Schéma LLM non ambigu et marge bornée pour réparer le plan compact après Live62

- `PlannerCompactFacetPlanFormatRepair` rappelle désormais toutes les valeurs autorisées : `decision=accept|clarify`, `rowCoverage=row_independent_candidate_pool|row_specific_evidence`, `tool=rag.search|rag.multi_search`, scope égal à un chemin disponible exact ou `null`.
- Le prompt interdit explicitement de placer un mot de décision, une description en prose ou un but de recherche dans les champs `rowCoverage`, `scope` ou `tool`. Il exige des clés double-quotées, sans alias ni champ supplémentaire.
- Un squelette JSON positionnel complet est fourni. Pour `accept`, `queries` et `purposes` doivent contenir exactement une chaîne non vide par `FACET_ID`; chaque requête garde 3 à 12 termes porteurs de contenu et chaque but reste court.
- Le LLM garde toutes les décisions sémantiques : acceptation ou clarification, mode de couverture des lignes, outil, scope, requêtes et buts. Le code ne complète aucun de ces champs et continue uniquement à lier la position aux facettes LLM déjà approuvées puis à vérifier le contrat.
- Le budget exact de la réparation passe de 256 à 320 tokens. La première tentative compacte reste à 256. Cette marge cible précisément la sortie Live62 qui a épuisé son budget, sans revenir au budget Planner générique de 640.
- La régression principale force maintenant une première sortie compacte non JSON, vérifie l'appel de `PlannerCompactFacetPlanFormatRepair`, son rappel des enums et de l'outil, puis fait passer le plan réparé par `PlannerAcceptedQueryAudit` avant toute exécution.
- Une nouvelle régression prouve qu'un auditeur qui choisit `clarify` sur les deux tentatives empêche toute exécution. Un refus sémantique valide est désormais tracé comme `accepted_query_audit_not_approved` plutôt que comme une fausse erreur de nombre de requêtes; sa raison est transportée au retry.
- Validation post-correctif : 4/4 tests discriminants; suite SourceBacked 300/300; lot SourceBacked/OpenAI/ApiClient 380/380; compilation réussie; `git diff --check` sans erreur de contenu, uniquement les avertissements de fins de ligne hérités.
- Prochaine preuve discriminante : Live63 doit obtenir un plan compact parseable sous le schéma renforcé, puis appeler `PlannerAcceptedQueryAudit`. Aucune requête faible ne doit être exécutée sans approbation ou réécriture sémantique indépendante. Le gate aval reste la sélection explicite d'IDs, le writer, vingt cellules concrètes, l'audit de types, l'éventuel suivi RAG compact et au moins deux cartes source utiles.

### 2026-07-21 - Soixante-troisième live : grille exacte, mais confusion entre index de requêtes et index de lignes

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260721-182344/`, trace principale `rag-20260721182417837-63da992b`, trace SourceBacked `sbrag-8606133f3e9a4746885ea0bb0e5f69e0`, journaux processus `artifacts/live-test-process-live63-20260721-202338/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260721_202359.log`.
- Le test xUnit échoue proprement après 10 min 11 s sur le payload de sources nul. Le pipeline actif dure 577,5 s, arrête le runtime local et rend seulement l'insuffisance sûre de 298 caractères : zéro outil, zéro requête RAG exécutée, zéro preuve et zéro carte source.
- Le routeur termine en 28,2 s. Le Planner initial termine en 163,6 s avec 1 161 caractères.
- L'adjudication des lignes termine en 49,7 s et conserve exactement `Lundi`, `Mardi`, `Mercredi`, `Jeudi`, `Vendredi`. L'adjudication des colonnes termine en 27,7 s; son patch d'ancres termine en 35,1 s et établit exactement `Petit-déjeuner`, `Dîner`, `Souper`, `Collation`. La grille 5 x 4 est donc à nouveau validée sans heuristique métier.
- La première réparation de stratégie atteint sa borne de 150 s et ne produit aucun contrat exécutable. Le pipeline bascule sur `PlannerCompactFacetPlan`.
- Le plan compact initial termine en 29,0 s avec 169 caractères non parseables. La nouvelle réparation de format termine rapidement en 7,0 s avec un JSON parseable et des champs cohérents; contrairement à Live62, il n'y a plus de désynchronisation des enums ou de l'outil. Le modèle choisit toutefois `clarify` et ne fournit qu'une requête `sources for meal plans`. Le contrat mécanique détecte le refus, le nombre attendu `4` contre `1` et les trois facettes absentes; aucun outil n'est exécuté.
- Le retry de stratégie termine en 39,4 s avec quatre requêtes, mais associe positionnellement `Lundi` au petit-déjeuner, `Mardi` au dîner, `Mercredi` au souper et `Jeudi` à la collation. Le choix déclaré est pourtant `row_independent_candidate_pool`. Les garde-fous détectent `row_independent_query_copies_typed_rows` sur les quatre requêtes.
- Le patch d'affectation relie correctement les quatre requêtes aux quatre facettes mais ne peut, par contrat, réécrire leur sens. La première revue focalisée conserve encore les jours dans les requêtes. Son retry choisit ensuite explicitement `clarify`; le pipeline respecte ce refus et s'arrête sans RAG.
- `PlannerAcceptedQueryAudit` n'est pas atteint, car aucune stratégie n'a franchi les contrats antérieurs. Live63 valide donc le nouveau format et la sûreté du refus, mais pas encore l'audit indépendant en conditions réelles.
- La mémoire est présente et reste `context_only_never_final_proof`; elle ne sert ni à autoriser une requête invalide ni à fabriquer une réponse.
- Diagnostic précis : le modèle local comprend les deux axes au stade intake, mais le micro-contrat de requêtes ne rendait pas assez saillante l'absence de correspondance positionnelle entre les index de requêtes et les index de lignes. L'exemple JSON à une seule position favorisait aussi une sortie compacte à une seule facette malgré un `FACET_COUNT` implicite de quatre.

### 2026-07-21 - Cardinalité dynamique et séparation explicite des axes après Live63

- `PlannerCompactFacetPlan` et sa réparation de format exposent maintenant `FACET_COUNT`, `PLACEMENT_ROWS` et une liaison explicite de chaque position de `queries`/`purposes` vers son `FACET_ID`. Les facettes sont déclarées comme des colonnes, jamais comme des lignes de placement.
- Le squelette `EXACT_OUTPUT_SHAPE` est généré mécaniquement avec exactement autant de positions que de facettes déjà approuvées par le LLM. Pour quatre facettes, il contient quatre placeholders de requêtes et quatre placeholders de buts; l'exemple trompeur à une seule entrée a disparu.
- Le prompt rappelle qu'en `row_independent_candidate_pool`, chaque requête porte sur une facette réutilisable dans toutes les lignes et qu'aucune étiquette de ligne ne doit apparaître. Il interdit explicitement l'appariement `requête 1 -> ligne 1`, `requête 2 -> ligne 2`, etc.
- Le retry stratégique et les deux passes de `PlannerFacetQueryReview` reçoivent la même clarification structurelle près de leur zone de réponse. Cette information provient uniquement de l'intake typée déjà approuvée; le code ne choisit toujours ni vocabulaire métier, ni requête, ni catégorie, ni décision `accept/clarify`.
- Trois régressions discriminantes passent : contrat générique de prompt, réparation compacte puis audit indépendant, et réécriture de requêtes row-independent copiant une ligne. Les assertions prouvent la cardinalité quatre, les cinq lignes de placement, les liaisons position-facette et l'interdiction index-ligne.
- Validation post-correctif : 3/3 tests ciblés; suite SourceBacked 300/300; lot SourceBacked/OpenAI/ApiClient 380/380; compilation WinUI réussie; `git diff --check` sans erreur de contenu, uniquement les avertissements de fins de ligne hérités.
- Prochaine preuve discriminante : Live64 doit produire quatre requêtes par facette sans jour de placement, atteindre `PlannerAcceptedQueryAudit`, puis seulement exécuter le RAG si ce second LLM les approuve. Le succès fonctionnel exige toujours le passage complet jusqu'au writer, vingt cellules concrètes, des citations vérifiées et au moins deux cartes source utiles.

### 2026-07-21 - Soixante-quatrième live : cardinalité corrigée et audit indépendant atteint, mais buts d'audit omis

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260721-184524/`, trace principale `rag-20260721184558379-0dc30d39`, trace SourceBacked `sbrag-86470ba1efbf4ade81e34c9748ee43a4`, journaux processus `artifacts/live-test-process-live64-20260721-204518/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260721_204539.log`.
- Le test xUnit échoue proprement après 14 min 32 s sur le payload nul. Le pipeline actif dure 838,2 s et rend uniquement l'insuffisance sûre de 298 caractères. Le runtime est arrêté; aucun outil, aucune preuve, aucune citation et aucune carte source ne sont exposés.
- Le routeur termine en 40,4 s. Le Planner initial termine en 208,8 s avec 1 306 caractères.
- L'adjudication des lignes termine en 71,6 s avec les cinq jours exacts. L'adjudication des colonnes termine en 36,6 s avec cinq candidats; le patch d'ancres termine en 49,8 s et le sélecteur LLM retire en 6,1 s le `Déjeuner` imbriqué dans `Petit-déjeuner`. L'intake finale contient exactement cinq lignes et quatre colonnes.
- La première réparation de stratégie atteint sa borne de 150 s et ne fournit aucun contrat utilisable. Le repli compact est alors appelé avec le nouveau prompt dynamique de 3 586 caractères.
- `PlannerCompactFacetPlan` termine en 68,3 s avec 491 caractères. Pour la première fois après les blocages Live62/63, il produit directement un contrat parseable, accepté et complet de quatre requêtes, une par facette, sans copier aucun jour : `Petit-déjeuner sources for Cuisine`, `Dîner sources for Cuisine`, `Souper sources for Cuisine`, `Collation sources for Cuisine`.
- Les requêtes restent lexicalement faibles, mais elles ne sont pas exécutées. `PlannerAcceptedQueryAudit` est atteint en live pour la première fois sur ce chemin et termine en 53,5 s.
- L'auditeur retourne une décision `accept` et quatre entrées, mais omet les quatre `purpose`. Le code refuse mécaniquement le contrat avec quatre `empty_facet_query_purpose`; cette omission ne peut pas être complétée automatiquement.
- Après ce refus, le pipeline historique repart sur le retry stratégique complet de 7 121 caractères. Il atteint inutilement sa borne de 150 s sans produire de contrat, puis le run s'arrête sans exécution.
- La mémoire reste présente avec `memory_finality=context_only_never_final_proof`. Elle n'est utilisée ni pour inventer les buts manquants, ni pour contourner l'audit, ni pour répondre.
- Bilan discriminant : Live64 valide le squelette dynamique, la séparation index/facette versus index/ligne, quatre requêtes compactes et l'appel de l'audit indépendant. Le nouveau blocage est beaucoup plus local : réparer la complétude JSON de l'audit sans refaire toute la stratégie et sans que le code écrive les buts à la place du LLM.

### 2026-07-21 - Réparation LLM focalisée du contrat de l'audit indépendant après Live64

- `PlannerAcceptedQueryAudit` reçoit maintenant `AUDIT_REQUEST_COUNT` et un `EXACT_AUDIT_OUTPUT_SHAPE` généré depuis le plan provisoire mécaniquement valide. Pour quatre requêtes, l'objet contient exactement les clés `1` à `4`, chaque `targetFacetExact` immuable, ainsi qu'un placeholder `QUERY` et `PURPOSE` obligatoire par entrée.
- Si l'audit est parseable mais mécaniquement incomplet, `PlannerAcceptedQueryAuditContractRepair` demande au LLM de refaire l'audit indépendant avec les erreurs exactes et les révisions précédentes. Le LLM doit réévaluer les requêtes, écrire chaque but manquant et rendre le contrat complet, ou choisir un `clarify` propre.
- Un `clarify` valide reste terminal et ne déclenche aucun repair. Le code ne transforme jamais un refus en acceptation, ne génère aucun `purpose`, ne choisit aucun terme de domaine et ne modifie ni index, ni outil, ni scope, ni affectation de facette.
- Un JSON de repair invalide, un second contrat incomplet ou un nouveau `clarify` demeure un refus sûr. Les outcomes distinguent désormais JSON de repair invalide, refus après repair et contrat encore invalide.
- L'observabilité de l'échec initial inclut les quatre révisions avec leur `query` et leur `purpose`, ce qui permet de séparer une faiblesse sémantique d'une simple omission de propriété.
- Le budget de l'audit initial et de son repair est fixé à 480 tokens dans les deux adaptateurs LLM; les tests vérifient le budget résolu et le `max_tokens` réellement sérialisé.
- La régression principale reproduit Live64 : quatre requêtes faibles, audit indépendant qui les réécrit mais laisse quatre buts vides, repair focalisé, puis quatre requêtes/buts complets. Elle prouve qu'aucune exécution ne précède le repair. Le test de double `clarify` prouve toujours que les refus sémantiques ne sont pas contournés.
- Validation post-correctif : 13/13 contrôles ciblés; suite SourceBacked 300/300; lot SourceBacked/OpenAI/ApiClient 380/380; compilation WinUI réussie; `git diff --check` sans erreur de contenu, uniquement les avertissements de fins de ligne hérités.
- Prochaine preuve discriminante : Live65 doit atteindre `PlannerAcceptedQueryAuditContractRepair`, obtenir quatre requêtes et quatre buts ou s'arrêter proprement sur `clarify`. Si le repair est approuvé, le run doit enfin franchir la frontière RAG avec les requêtes auditées, puis prouver la sélection d'IDs avant le writer.

### 2026-07-21 - Soixante-cinquième live : quatre facettes conservées, mais les lignes réapparaissent dans les requêtes compactes

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260721-191010/`, trace principale `rag-20260721191043754-803f3466`, trace SourceBacked `sbrag-1eef18bc0b8349ffbfc5e82cb0085360`, journaux processus `artifacts/live-test-process-live65-20260721-211004/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260721_211025.log`.
- Le test xUnit échoue proprement après 12 min 38 s. Le pipeline actif dure 724,6 s et rend l'insuffisance sûre de 298 caractères avec zéro outil, zéro preuve et zéro carte; le runtime local est arrêté.
- Le routeur termine en 39,9 s et le Planner en 154,2 s. Les adjudications reconstruisent à nouveau exactement les cinq jours et quatre types de repas; le chevauchement `Petit-déjeuner` / `Déjeuner` est résolu par le sélecteur LLM.
- La réparation de stratégie atteint sa borne de 150 s. Le plan compact dynamique termine en 67,5 s avec 511 caractères et conserve bien quatre positions, une par facette.
- Contrairement à Live64, les quatre requêtes incluent cette fois `pour la semaine du lundi au vendredi`. Elles déclarent pourtant `row_independent_candidate_pool`. Le garde-fou détecte `Lundi` et `Vendredi` dans les quatre requêtes et refuse le contrat avant l'audit indépendant.
- Le pipeline repart alors sur le retry stratégique général de 7 116 caractères, qui atteint à nouveau sa borne de 150 s sans contrat utilisable. Le nouveau repair d'audit Live64 n'est donc pas atteint dans ce run, non parce qu'il échoue, mais parce que le plan compact est rejeté plus tôt.
- La mémoire reste `context_only_never_final_proof` et ne sert pas à corriger ou autoriser les requêtes.
- Diagnostic : le squelette dynamique stabilise désormais le nombre et l'affectation des facettes, mais le modèle varie encore sur la copie des lignes. Ce défaut est déjà précisément réparable par `PlannerFacetQueryReview`; relancer un Planner général est inutilement long et moins ciblé.

### 2026-07-21 - Revue LLM focalisée immédiate des plans compacts rejetés localement

- Après `PlannerCompactFacetPlan`, un candidat non accepté est désormais proposé immédiatement à `TryReviewPlannerFacetQueriesAsync` lorsque ses problèmes sont ceux que ce reviewer sait traiter, notamment `row_independent_query_copies_typed_rows`.
- Le reviewer conserve les outils, scopes, index et facettes, mais le LLM réécrit seul les requêtes et leurs buts. Le code ne retire aucun jour, n'ajoute aucun terme métier et ne convertit aucun `clarify`.
- Si la revue focalisée réussit, `PlannerAcceptedQueryAudit` reste obligatoire parce que le plan provient du chemin compact. Cette seconde barrière indépendante peut encore conserver, réécrire ou refuser les requêtes avant le RAG.
- Le même routage existe pour un plan compact construit après le retry. Un plan non réparable ou un refus LLM peut toujours poursuivre vers la stratégie de repli sûre; aucune requête invalide n'est forcée.
- La nouvelle régression reproduit Live65 : quatre requêtes compactes contenant `Lundi`/`Vendredi`, revue focalisée qui les remplace par des recherches réutilisables, audit indépendant puis exécution. Elle prouve l'absence totale de `PlannerDiversityRetry` sur cette trajectoire locale et l'absence des jours dans le plan exécuté.
- Validation : 3/3 scénarios discriminants; lot complet SourceBacked/OpenAI/ApiClient 381/381; compilation WinUI réussie; `git diff --check` sans erreur de contenu, uniquement les avertissements de fins de ligne hérités.
- Prochaine preuve discriminante : Live66 doit, selon la variation du compact planner, soit atteindre directement l'audit et son repair de contrat, soit passer par `PlannerFacetQueryReview` puis l'audit, sans retry stratégique général de 150 s. Le gate aval reste l'exécution RAG de quatre requêtes auditées et la sélection explicite de preuves.

### 2026-07-21 - Soixante-sixième live : le routage focalisé fonctionne, mais son contexte recopie encore les requêtes contaminées

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260721-193059/`, trace principale `rag-20260721193136543-1e2a39b8`, trace SourceBacked `sbrag-61b717b3fb72405ab28d1a7b492e3b94`, journaux processus `artifacts/live-test-process-live66-20260721-213052/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260721_213116.log`.
- Le test xUnit échoue proprement après 22 min 14 s sur le payload nul. Le pipeline actif dure 1 296,8 s, arrête le runtime local et rend l'insuffisance sûre de 298 caractères avec zéro outil, zéro preuve, zéro citation et zéro carte source.
- Le routeur termine en 61,5 s. Le Planner termine en 192,8 s et déclenche cette fois la revue complète de l'intake. `PlannerIntakeReview` termine en 201,3 s, comprend les deux axes mais groupe les libellés dans des ancres exactes invalides. `PlannerIntakeAnchorRepair` termine en 107,2 s sans rendre ces ancres valides.
- Les micro-adjudications récupèrent correctement la structure : lignes en 48,3 s puis patch de métadonnées en 5,4 s; colonnes en 15,7 s puis patch d'ancres en 44,8 s. L'intake finale contient exactement `Lundi` à `Vendredi` et `Petit-déjeuner`, `Dîner`, `Souper`, `Collation`, soit vingt cellules attendues.
- La stratégie générale atteint sa borne de 150 s. Le plan compact termine en 46,7 s avec quatre requêtes correctement affectées aux facettes, mais chacune recopie `Lundi`/`Vendredi` malgré `row_independent_candidate_pool`.
- Le routage ajouté après Live65 s'active donc réellement : `PlannerFacetQueryReview` est appelé immédiatement au lieu de passer directement au retry général. Il termine en 68,4 s, mais conserve les jours. Son retry termine en 91,9 s et conserve encore les jours.
- Le retry stratégique général finit ensuite en 125,8 s avec quatre requêtes toutes liées à `Lundi` et sans affectations de facettes. Le patch d'affectation rétablit les quatre liaisons en 21,3 s, mais ne modifie pas les requêtes par contrat.
- Une seconde revue focalisée termine en 38,6 s avec un JSON incomplet. Sa réparation de format termine en 72,9 s avec seulement trois requêtes, toujours contaminées par `Lundi`, sans `reviewDecision` ni raison. Le contrat est refusé et aucun RAG n'est exécuté.
- Le diagnostic précis est désormais contextuel : les consignes interdisent correctement les lignes, mais le prompt de revue transporte en même temps `ANSWER_OBJECTIVE` et chaque `currentQuery` déjà contaminée. Sur le modèle local 3B, ces occurrences concrètes de `Lundi`/`Vendredi` dominent l'instruction négative et sont recopiées.
- La mémoire reste `context_only_never_final_proof`; elle ne crée ni requête de remplacement ni réponse sans preuve.

### 2026-07-21 - Micro-réparation LLM sans contamination des requêtes row-independent après Live66

- Le nouveau micro-contrat `PlannerRowIndependentQueryRepair` est choisi uniquement lorsque tous les problèmes mécaniques sont `row_independent_query_copies_typed_rows`. Les défauts mixtes, affectations manquantes et autres révisions continuent d'utiliser `PlannerFacetQueryReview`.
- Le prompt spécialisé omet volontairement la question complète, les requêtes rejetées et leurs buts. Il fournit seulement le `QUESTION_FOCUS` déjà décidé par le LLM, les facettes typées exactes, les libellés de lignes interdits et les liaisons immuables index/outil/scope/facette.
- Le LLM garde l'entière décision sémantique : il choisit les termes de domaine, le but de chaque recherche, l'acceptation ou `clarify`. Le code ne retire aucun jour, ne remplace aucun mot et n'ajoute aucun terme lié aux repas.
- Le code se limite au routage de la violation mécanique, au parsing du JSON, à la vérification des index et facettes, à la longueur maximale et à l'absence des libellés interdits. Une sortie invalide, incomplète, encore contaminée ou `clarify` reste un refus sûr.
- Le schéma JSON est généré avec une entrée obligatoire par requête et la facette exacte préremplie. Des variantes propres existent pour un retry sémantique et une réparation de format sans réintroduire la question ou les anciennes requêtes.
- Le budget exact de ces micro-passes est 480 tokens. Il permet quatre couples `query`/`purpose` complets tout en restant borné pour le runtime local.
- Les deux régressions reproduisant les trajectoires Live65 et Live66 prouvent l'appel direct à `PlannerRowIndependentQueryRepair`, l'absence de la question complète, de `ANSWER_OBJECTIVE` et des requêtes contaminées, puis l'exécution uniquement des requêtes LLM révisées. Le scénario à deux passes prouve aussi que le retry reste lui-même non contaminé.
- Le test d'architecture vérifie un prompt générique sous 3 500 caractères, le focus sémantique, les cinq lignes interdites, les liaisons immuables, les quatre entrées JSON exactes et l'absence du texte utilisateur et des requêtes rejetées. Les tests des deux adaptateurs vérifient le budget effectif de 480 tokens.
- Validation post-correctif : compilation WinUI et tests réussie; 4/4 contrôles ciblés sélectionnés; lot complet SourceBacked/OpenAI/ApiClient 381/381; `git diff --check` sans erreur de contenu, uniquement les avertissements de fins de ligne hérités.
- Prochaine preuve discriminante : Live67 doit transformer directement les quatre requêtes compactes contaminées en quatre recherches réutilisables sans passer par le retry stratégique général. Il doit ensuite atteindre `PlannerAcceptedQueryAudit`, n'exécuter le RAG qu'après cet audit et produire des EvidenceIds sélectionnés avant tout writer. Le succès fonctionnel reste vingt cellules professionnelles, citations fichier/page vérifiées et au moins deux cartes source utiles.

### 2026-07-21 - Soixante-septième live : le micro-prompt retire les jours, puis bute sur quatre buts de recherche vides

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260721-201027/`, trace principale `rag-20260721201105796-c800f603`, trace SourceBacked `sbrag-32696f0b86094256b6ebb4f7f863c2cf`, journaux processus `artifacts/live-test-process-live67-20260721-221020/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260721_221045.log`.
- Le test xUnit échoue proprement après 12 min 35 s. Le pipeline actif dure 716,7 s, rend l'insuffisance sûre de 298 caractères et arrête le runtime local avec zéro outil, zéro preuve et zéro carte source.
- Le routeur termine en 32,1 s. Le Planner initial termine en 134,3 s. Contrairement à Live66, il n'impose pas la revue complète de l'intake : les micro-adjudications suffisent.
- Les lignes sont adjugées en 39,1 s et leur métadonnée manquante est patchée en 5,4 s. Les colonnes sont adjugées en 33,6 s, les ancres en 36,3 s puis le conflit `Petit-déjeuner`/`Déjeuner` est résolu en 7,5 s. La grille exacte de vingt cellules est validée.
- La stratégie générale atteint sa borne de 150 s. Le plan compact termine en 68,4 s avec quatre requêtes sémantiquement orientées vers les repas, mais chacune contient `pour la semaine du lundi au vendredi`. Le garde-fou refuse les quatre requêtes.
- `PlannerRowIndependentQueryRepair` est appelé immédiatement en live avec un prompt de 2 714 caractères, sans `PlannerDiversityRetry` intermédiaire. Il termine en 55,8 s et retire bien `Lundi`/`Vendredi` des quatre requêtes, ce qui valide le diagnostic de contamination du prompt Live66.
- La sortie reste cependant incomplète : les quatre requêtes deviennent `sources for <facette>` et les quatre `purpose` sont vides. Le parseur récupère la décision `accept`, les quatre index et les quatre facettes, puis le contrat mécanique refuse quatre `empty_facet_query_purpose`.
- Le code antérieur repart alors sur `PlannerDiversityRetry`, qui atteint inutilement sa borne de 150 s. Aucun audit indépendant et aucun RAG ne sont atteints dans ce run.
- Le modèle a produit 112 tokens à environ 2,14 tokens/s sur le micro-prompt; l'échec ne vient donc ni du timeout ni de la limite de 480 tokens, mais d'une omission de propriétés malgré le squelette JSON.
- La mémoire reste `context_only_never_final_proof` et n'est jamais utilisée pour remplir les buts manquants ou fabriquer une réponse.
- Bilan discriminant : Live67 prouve le routage local, la réduction de contexte et l'élimination des jours. Le défaut restant est désormais un contrat LLM local et parseable, pas un problème de compréhension de la grille ni de recherche de sources.

### 2026-07-21 - Retry spécialisé d'un accept row-independent parseable mais incomplet après Live67

- Lorsqu'une première `PlannerRowIndependentQueryRepair` retourne `reviewDecision=accept` mais échoue au contrat typé, le pipeline appelle désormais une seule fois `PlannerRowIndependentQueryRepairRetry` au lieu du Planner stratégique général.
- Le retry reste entièrement non contaminé : il ne reçoit ni la question complète, ni les requêtes compactes avec les jours, ni la sortie faible `sources for`. Il reçoit les erreurs mécaniques exactes, les facettes, les liaisons immuables, le focus et les lignes interdites.
- Le prompt précise qu'un accept exige, pour chaque entrée, une requête non vide et un but non vide décrivant la preuve concrète attendue. Le LLM doit refaire le jeu complet ou choisir `clarify`.
- Un `clarify` initial reste terminal et ne déclenche jamais ce retry. Le code n'utilise la seconde passe que pour une intention `accept` parseable mais contractuellement incomplète; il ne convertit aucune décision sémantique et ne rédige aucun but lui-même.
- La régression Live67 force quatre `purpose` vides, exige le marqueur `PlannerRowIndependentQueryRepairRetry`, vérifie l'absence des anciennes requêtes et de la question, puis ne permet `PlannerAcceptedQueryAudit` et l'exécution qu'après quatre couples `query`/`purpose` produits par le LLM.
- Validation : compilation réussie; 3/3 régressions ciblées; lot complet SourceBacked/OpenAI/ApiClient 381/381; `git diff --check` sans erreur de contenu, uniquement les avertissements EOL historiques.
- Prochaine preuve discriminante : Live68 doit faire suivre l'omission éventuelle des buts par le retry spécialisé, sans grand retry de 150 s. Une sortie complète doit ensuite atteindre l'audit indépendant et le RAG. Le gate aval reste la sélection explicite d'EvidenceIds, le writer de vingt cellules, l'audit de types et les cartes fichier/page vérifiées.

### 2026-07-21 - Soixante-huitième live : l'épinglage fichier/page fonctionne, mais le vocabulaire de présentation détourne les quatre recherches

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260721-202929/`, trace principale `rag-20260721203006769-1becd429`, trace SourceBacked `sbrag-344c49d4a35e46349d460a69a42aebf5`, journaux processus `artifacts/live-test-process-live68-20260721-222923/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260721_222946.log`.
- Le test xUnit échoue proprement après 25 min 48 s. Le pipeline actif dure 1 508,5 s et rend une insuffisance sûre de 277 caractères. Aucun writer n'est appelé, aucune citation ni carte source n'est exposée et le payload final de sources est nul.
- Le routeur termine en 35,7 s. Le Planner initial termine en 183,1 s. La revue complète de l'intake puis les micro-adjudications rétablissent exactement les cinq jours et les quatre types de repas, soit la grille attendue de vingt cellules.
- La stratégie générale atteint sa borne de 150 s. Le plan compact termine en 121,2 s avec quatre requêtes liées aux bonnes facettes, mais chacune recopie la semaine du lundi au vendredi.
- `PlannerRowIndependentQueryRepair` s'active immédiatement, termine en 91,6 s et réussit dès la première passe. Il retire toutes les lignes, fournit quatre buts non vides et évite le retry spécialisé ajouté après Live67. Les quatre requêtes restent toutefois génériques : `What foods are typically eaten for breakfast?`, puis les variantes dinner, supper et snack.
- `PlannerAcceptedQueryAudit` termine en 54,9 s et accepte les quatre requêtes, mais les dégrade en leur ajoutant `in a user-friendly format?`. Le prompt d'audit transportait encore `ANSWER_OBJECTIVE`, donc la préférence d'affichage de la question utilisateur a dominé la consigne sémantique sur le petit modèle local.
- Les quatre recherches RAG sont réellement exécutées en 24,9 s, 27,7 s, 29,7 s et 26,2 s. Elles retournent 48 hits bruts, consolidés en 22 éléments dans l'`EvidenceBundle`.
- Le premier hit des quatre recherches est le même document hors sujet : `Catalogue commercial/Actreg - CAT-Pneumatic Actuators-EN.pdf`, page 15, dont l'extrait contient précisément `user-friendly screen interface`. D'autres hits concernent NIST, les atmosphères explosives, HTTP, URI, PTFE ou l'IPCC; ils n'apportent aucun candidat de repas légitime.
- Cette observation prouve que la chaîne mécanique d'identité fonctionne déjà : chaque hit transporte `docId`, `docPath`, `pageStart`, `pageEnd` et l'extrait exact. Le système sait donc épingler le fichier et la page. Le défaut est en amont et sémantique : la requête choisit le mauvais passage, qui est parfaitement localisable mais impropre à justifier un repas.
- L'`EvidenceJudge` termine en 100,5 s, ne sélectionne aucun ID et choisit `need_more_evidence`. Le garde-fou empêche donc correctement un writer de fabriquer un plan ou des liens décoratifs à partir de ces documents.
- La décision reste néanmoins imparfaite : elle décrit des cases manquantes pour mardi à jeudi au lieu de reconnaître que l'ensemble du corpus retourné est hors domaine. Son action repair puis son retry de contrat ne produisent aucune recherche exécutable; l'itération s'arrête proprement avec `selected_count=0`, `follow_up_count=0` et `reason=no_executable_follow_up`.
- La mémoire reste présente avec `memory_finality=context_only_never_final_proof`. Elle n'est utilisée ni pour inventer des repas, ni pour légitimer les documents hors sujet, ni pour fabriquer des ancrages source.
- Bilan discriminant : Live68 franchit pour la première fois la frontière RAG avec quatre requêtes typées et confirme que l'épinglage fichier/page n'est pas le blocage. Le blocage actuel est la pureté sémantique du dernier audit de requêtes; `user-friendly` est une contrainte de sortie, pas un terme de recherche.

### 2026-07-21 - Audit LLM final des requêtes isolé des contraintes de présentation après Live68

- `PlannerAcceptedQueryAudit` n'utilise plus le prompt générique `BuildPlannerFacetQueryReviewUserPrompt`, qui réinjectait la question complète sous `ANSWER_OBJECTIVE`.
- Son nouveau contexte propre contient uniquement le `QUESTION_FOCUS` déjà décidé par le LLM, la décision de couverture, les facettes typées, les lignes interdites et les liaisons immuables index/outil/scope/facette avec les requêtes provisoires à auditer.
- La question utilisateur et ses préférences de présentation sont explicitement omises. Le prompt rappelle qu'une requête décrit le contenu recherché et doit exclure format, clarté, mise en page, tableau, convivialité, lisibilité, style ou acte de répondre.
- Le LLM demeure l'unique arbitre sémantique. Il conserve ou réécrit chaque requête avec des termes nominaux du domaine susceptibles de retrouver des valeurs nommées, faits, composants, ingrédients, procédures ou autres candidats concrets adaptés à la facette. Le code ne contient aucun vocabulaire culinaire, ne retire aucun mot après coup et ne transforme aucun `clarify` en acceptation.
- Les outils, scopes, index et affectations de facettes restent immuables. L'application continue uniquement de parser le JSON, vérifier la complétude du contrat et transporter la décision LLM vers l'exécution.
- La régression d'architecture reproduit une question contenant `user-friendly`, `weekly table` et `readable formatting`. Elle exige le focus sémantique et les quatre requêtes provisoires dans le prompt, mais interdit la question complète, `ANSWER_OBJECTIVE` et `user-friendly`, y compris dans la réparation de contrat.
- Validation post-correctif : compilation réussie; 121/121 tests d'architecture; 3/3 trajectoires discriminantes du pipeline; lot complet SourceBacked/OpenAI/ApiClient 381/381.
- Prochaine preuve discriminante : Live69 doit atteindre le même audit sans aucune contrainte de présentation dans son prompt, produire quatre requêtes nominales orientées vers des candidats de repas concrets, puis retrouver des passages culinaires réellement légitimes. Le gate aval reste une sélection explicite d'EvidenceIds, un writer de vingt cellules, l'audit des types de valeurs et au moins deux cartes source vérifiées avec fichier et page.

### 2026-07-21 - Soixante-neuvième live : vraies sources culinaires épinglées et IDs sélectionnés, puis double timeout du writer de vingt cellules

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260721-210619/`, trace principale `rag-20260721210707418-05b4a2eb`, trace SourceBacked `sbrag-81f316be9f9d4c70a7188d29e23f749a`, journaux processus `artifacts/live-test-process-live69-20260721-230612/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260721_230642.log`.
- Le test xUnit échoue proprement après 28 min 2 s. Le pipeline actif dure 1 633,6 s et rend une insuffisance sûre de 341 caractères. Aucun payload de sources ni aucune carte n'est exposé parce que le writer n'a jamais produit de réponse vérifiable.
- Le routeur termine en 33,2 s et le Planner initial en 94,8 s. Les micro-adjudications reconstruisent exactement les cinq jours et, après sélection du faux `Déjeuner` imbriqué, les quatre colonnes `Petit-déjeuner`, `Dîner`, `Souper`, `Collation`.
- La stratégie générale atteint 150 s. Le plan compact termine en 66,1 s et produit directement quatre requêtes complètes sous `categoryPath=Cuisine`; aucun micro-repair de lignes n'est nécessaire.
- Le nouvel audit sans question complète termine en 92,8 s. Il ne réintroduit ni `user-friendly`, ni tableau, ni mise en page. Le correctif Live68 est donc validé en vrai.
- L'audit dégrade toutefois la spécificité des trois premières facettes : les requêtes finales deviennent des variantes de `Quels repas ... peuvent être préparés en cuisine ?`, tandis que seule la quatrième conserve `snacks`. Les requêtes restent distinctes lexicalement mais sont sémantiquement trop interchangeables entre petit-déjeuner, dîner et souper.
- Les quatre recherches terminent en 37,6 s, 29,8 s, 38,0 s et 37,4 s. Elles retournent 48 hits bruts, consolidés en 26 éléments dans l'`EvidenceBundle`, sans timeout ni erreur de retrieval.
- Les hits sont désormais réellement culinaires et mécaniquement épinglés : `Cuisine/chefbot_livre_de_recettes_fr.pdf` p.102, `30-recettes-preferees-des-francais.pdf` p.35, `si-on-cuisinait.pdf` p.4, `Je_cuisine_simplement.pdf` p.11, `facilitemps.pdf` p.95 et d'autres livres/recueils de recettes. Cela confirme définitivement que le transport fichier/page/extrait fonctionne lorsque le LLM choisit un scope et des termes de contenu corrects.
- L'`EvidenceJudge` choisit `answer` mais omet les IDs, donc le contrat refuse l'écriture directe avec `missing_answer_evidence_ids`. L'`EvidenceStatusReview` termine en 111,9 s, décrit les sources comme variées mais peu spécifiques, classe `E1` et `E4` utiles, `E2` faible, une exigence manquante, et recommande `write_partial`.
- La réparation de sélection utilise mécaniquement les deux IDs choisis par le LLM. Aucun fallback de sélection codé en dur n'est employé. Un writer structuré est enfin lancé avec des preuves explicitement sélectionnées.
- `structured_writer` atteint sa borne de 210 s après avoir généré environ 419 tokens à près de 2 tokens/s. Son JSON n'est pas terminé; le parseur reçoit uniquement le contrat de timeout et la vérification refuse `empty_answer`, `missing_citations` et `requested_structure_not_realized`.
- `structured_cell_repair` atteint ensuite la même borne de 210 s après environ 445 tokens produits. Il reste lui aussi incomplet et les trois erreurs mécaniques persistent.
- Les repairs d'action suivants ne fournissent aucune recherche exécutable avant leurs propres bornes. L'itération se termine par `verification_follow_up_not_authorized_by_llm`; aucune action n'est synthétisée par le code.
- La mémoire reste `context_only_never_final_proof`. Elle contribue au contexte et aux traces, mais ne crée aucun repas, aucune sélection de secours et aucun lien source.
- Bilan discriminant : Live69 prouve la recherche dans le bon domaine, l'épinglage de vraies pages de recettes et la première sélection d'IDs utiles. Le blocage immédiat est désormais mesuré : le schéma JSON verbeux du writer dépasse le nombre de tokens que le modèle 3B peut produire en 210 s, tandis que les requêtes auditées manquent encore de discrimination par facette.

### 2026-07-21 - Matrice LLM positionnelle compacte et maintien sémantique des facettes après Live69

- Pour les grandes grilles d'au moins seize cellules, `WriterStructuredTable` ne demande plus vingt objets répétant `rowLabel`, `column`, `text` et `evidenceIds`. Il demande une matrice compacte `rows`, ordonnée selon `ROW_ORDER` et `COLUMN_ORDER`, où chaque chaîne contient uniquement la valeur concrète choisie par le LLM et son `[E#]` visible.
- Le LLM conserve toute la responsabilité sémantique : il choisit chaque valeur des vingt cellules, l'ID qui la prouve, la diversité et l'acceptabilité du contenu. Le code associe seulement les indices aux lignes/colonnes typées déjà acceptées, rend le Markdown et déduit mécaniquement la liste unique d'IDs visibles.
- La matrice interdit la répétition des libellés et des noms de propriétés dans la sortie. À contenu égal, elle retire l'essentiel de l'enveloppe qui avait consommé les 419 à 445 tokens incomplets de Live69.
- `StructuredCellRepair` utilise le même contrat compact pour une grande grille. Les petites structures et l'ancien schéma `structuredTableRows` restent entièrement compatibles afin de ne pas casser les réponses simples ni les anciens repairs.
- Le parseur accepte désormais les rangées positionnelles, leur assigne mécaniquement les lignes typées, conserve l'ordre des colonnes et extrait les citations choisies dans chaque chaîne. Une rangée/cellule manquante continue de produire une case vide que le vérificateur refuse; le code ne complète aucun contenu.
- L'audit indépendant des requêtes exige maintenant qu'une requête finale ne puisse pas servir une autre facette inchangée. Il interdit de remplacer les facettes par de simples variantes adjectivales autour d'un même nom générique et demande de conserver une requête provisoire déjà forte.
- La régression de matrice prouve qu'un JSON compact de chaînes produit le tableau typé et les citations visibles attendus. Le test d'architecture vérifie aussi l'absence des clés répétées `rowLabel`/`evidenceIds` dans le prompt des grandes grilles et l'absence de vocabulaire culinaire codé en dur.
- Validation post-correctif : compilation WinUI réussie; 122/122 tests d'architecture; 3/3 trajectoires discriminantes; lot SourceBacked/OpenAI/ApiClient 382/382.
- Prochaine preuve discriminante : Live70 doit conserver quatre requêtes réellement spécifiques aux facettes, sélectionner davantage de preuves concrètes si le bundle le permet, puis terminer la matrice de vingt cellules avant 210 s. Le succès final exige toujours vingt valeurs professionnelles, citations visibles vérifiées et au moins deux cartes source fichier/page utiles.

### 2026-07-22 - Soixante-dixième live : matrice terminée et épinglage relu, mais imitation des placeholders puis rejet sémantique

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260721-214827/`, trace principale `rag-20260721214917329-db5aa927`, trace SourceBacked `sbrag-4cbced2d061f4d558d87474976f78bb0`, journaux processus `artifacts/live-test-process-live70-20260721-234823/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260721_234845.log`.
- Le test xUnit échoue proprement après 27 min 15 s sur le payload de sources nul. Le pipeline actif dure 1 585,7 s et rend une insuffisance sûre de 738 caractères; aucune carte source n'est exposée parce que l'audit qualitatif final refuse le contenu du tableau.
- Le routeur termine en 49,1 s et le Planner initial en 157,4 s. Les adjudications reconstruisent exactement les cinq jours et, après élimination du faux `Déjeuner` imbriqué dans `Petit-déjeuner`, les quatre colonnes demandées. La grille 5 x 4 reste correctement ancrée dans la question.
- La stratégie initiale recopie les jours et oublie les affectations de facettes. Les repairs focalisés corrigent finalement ces contrats, mais l'audit interprète d'abord les lignes interdites comme du texte à écrire (`exclude Lundi`, etc.). Les garde-fous refusent ces variantes. La dernière réparation row-independent finit par autoriser quatre requêtes sous `categoryPath=Cuisine`, mais leur qualité est faible : `sources for Petit-déjeuner`, `sources for Dîner`, `sources for Souper`, `sources for Collation`.
- Malgré ce vocabulaire pauvre, les quatre recherches terminent sans erreur et retournent respectivement 6, 12, 11 et 8 hits. Le premier passage petit-déjeuner est `Cuisine/chefbot_livre_de_recettes_fr.pdf` p.131, dont l'extrait décrit explicitement une boisson au yaourt et au fruit adaptée au goûter ou au petit-déjeuner. Le premier passage dîner est `Cuisine/nobilia-recettes-internationales-FR.pdf` p.25. La recherche collation remonte `Cuisine/livre-recette-sist-2025-web.pdf` p.10 avec une structure de repas/collation.
- L'`EvidenceBundle` initial contient 25 éléments. L'`EvidenceJudge` choisit encore `answer` sans ID; l'`EvidenceStatusReview` désigne alors trois preuves utiles et demande de relire exactement `chefbot_livre_de_recettes_fr.pdf` p.131, le même livre p.124 et `livre-recette-sist-2025-web.pdf` p.10. Les trois appels `documents.context` réussissent avec `anchorFound=true`; le bundle cumulatif atteint 44 éléments.
- Cette trajectoire constitue une preuve live complète de la mécanique d'épinglage : le hit transporte `docId`, `docPath`, page et extrait; le LLM choisit les IDs; l'application relit les documents autour de ces pages. Le problème n'est donc pas une incapacité à conserver fichier/page.
- Après plusieurs décisions d'évidence incohérentes (`read_documents` demandé alors que le budget de relecture est épuisé), la réparation de sélection réutilise les IDs `E1,E2` choisis par le LLM et lance le nouveau writer compact.
- `structured_writer` termine en 51,3 s avec 638 caractères, contre deux timeouts de 210 s dans Live69. La réponse rendue fait 629 caractères, cite `E1`, déclare `E1`, et le `SourceVerifier` la valide sans aucune erreur mécanique. Le format positionnel compact corrige donc réellement le blocage de génération longue.
- Le contenu reste toutefois impropre. L'audit `structured_value_type_fit_judge`, terminé en 106,3 s, ne trouve que huit valeurs distinctes et en rejette six. Les diagnostics montrent que le petit modèle a copié des métadonnées et placeholders du prompt (`Lundi`, `Mardi`, `Mercredi`, `Jeudi`, `short sourced phrase [E#]`) à la place de repas concrets.
- La réparation sémantique ciblée termine en 146,7 s mais conserve encore quatre valeurs génériques; le second juge termine en 123,0 s et en rejette trois. Le pipeline n'obtient ensuite aucune recherche complémentaire exécutable autorisée par le LLM et s'arrête sans publier le brouillon.
- Diagnostic immédiat : le prompt compact expose encore deux exemples imitables, `{"answer":"","rows":[["2-8 concrete words [E#]"]]}` et la `REQUESTED_TABLE_SHAPE` remplie avec `short sourced phrase [E#]`. Le modèle 3B a pris ces métavariables pour du contenu. Le prochain correctif doit conserver le contrat positionnel mais remplacer ces exemples textuels par un squelette non copiable, interdire explicitement toute reproduction de token de contrat et retirer la table d'exemple du contexte compact.
- La mémoire reste `context_only_never_final_proof`. Elle n'a ni fabriqué les valeurs, ni contourné le rejet qualitatif, ni créé de lien source.

### 2026-07-22 - Squelette compact non imitable et audit final sans exposition des lignes après Live70

- Le writer compact et son repair ne montrent plus une chaîne d'exemple telle que `2-8 concrete words [E#]`. Ils reçoivent un squelette positionnel exact contenant uniquement des `null`, par exemple cinq rangées de quatre positions pour une grille 5 x 4, et doivent remplacer chaque position par une vraie valeur sourcée.
- Le contrat précise que `null`, les mots du contrat, les exemples et les libellés de lignes/colonnes ne sont jamais des valeurs de sortie. Chaque cellule doit rester une décision sémantique du LLM : une instance concrète et supportée de la facette, suivie d'un ID `[E#]` autorisé.
- Pour les grandes grilles, `AppendCompactIntake` n'injecte plus `REQUESTED_TABLE_SHAPE` avec le placeholder `short sourced phrase [E#]`. `ROW_ORDER`, `COLUMN_ORDER`, les cardinalités et le squelette de positions suffisent à transporter la forme sans fournir de faux contenu à imiter.
- Les limites du writer n'emploient plus `REQUIRED_CELL_KEY`, `rowLabel` ou `column` sur ce chemin compact. Elles demandent de remplir chaque position et d'utiliser les deux ordres uniquement comme mapping mécanique. Les petites structures conservent leur schéma historique.
- L'audit final des requêtes n'expose plus les noms exacts des lignes de placement lorsqu'il reçoit un plan row-independent déjà mécaniquement propre. Il reçoit seulement leur nombre et le fait qu'elles ont été omises. Cette réduction empêche le modèle d'écrire `exclude Lundi` ou une variante tout en conservant l'interdiction des dates, jours, périodes et instructions de placement.
- Si un audit précédent a malgré tout recopié des termes de placement, sa réparation de contrat ne reçoit ni les mauvaises révisions ni les noms copiés dans le diagnostic. Elle réévalue les requêtes provisoires propres; le code ne retire toujours aucun terme d'une requête acceptée et ne choisit aucun vocabulaire documentaire.
- Les nouvelles assertions vérifient le squelette exact 5 x 4, l'absence de `short sourced phrase`, de l'ancien exemple `2-8 concrete words [E#]`, de `REQUESTED_TABLE_SHAPE`, de `REQUIRED_CELL_KEYS` et des noms de jours dans l'audit final et son repair contaminé.
- Validation post-correctif : compilation réussie; 122/122 tests d'architecture; 101/101 tests de `SourceBackedRagPipelineTests`; lot élargi SourceBacked/OpenAI/ApiClient 374/374; `git diff --check` sans erreur de contenu, uniquement les avertissements EOL hérités.
- Prochaine preuve discriminante : Live71 doit conserver la réussite temporelle de la matrice compacte tout en produisant vingt vraies valeurs au lieu de recopier les placeholders. L'audit final ne doit plus générer `exclude <jour>`. Le gate fonctionnel reste un tableau professionnel accepté sémantiquement, avec des IDs visibles et au moins deux cartes source fichier/page réellement utiles.

### 2026-07-22 - Soixante-et-onzième live : pollution des jours supprimée, mais portée documentaire nulle et recherches globales en échec

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260721-222853/`, trace principale `rag-20260721222929695-6fdfeaf6`, trace SourceBacked `sbrag-78f8ecbb37044887a3ca87277bc7fc2a`, journaux processus `artifacts/live-test-process-live71-20260722-002847/` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260722_002910.log`.
- Le test xUnit échoue proprement après environ 24 min 7 s. Le pipeline actif dure 1 410,7 s et rend une insuffisance sûre de 163 caractères : la recherche n'a pas abouti dans le délai prévu et l'application demande de la relancer. Aucun payload de sources ni aucune carte fichier/page n'est publié, car aucune preuve n'a été récupérée.
- La compréhension de forme reste correcte : cinq jours, quatre facettes et exactement vingt cellules demandées. Le faux `Déjeuner` imbriqué dans `Petit-déjeuner` ne réapparaît pas. Le routeur termine en 28,6 s, le Planner initial en 168,5 s, l'adjudication des lignes en 49,5 s, celle des colonnes en 35,0 s, le patch d'ancres en 40,5 s et le sélecteur en 6,3 s.
- La première stratégie LLM ne fournit qu'une requête générale qui recopie une ligne. Son retry général atteint le timeout de 150 s. Le plan compact termine ensuite en 45,3 s et produit quatre facettes, mais recopie encore des jours dans les requêtes.
- La réparation spécialisée `row_independent` termine en 69,1 s et enlève correctement tous les jours. Elle produit quatre requêtes sémantiquement distinctes : `meals for breakfast`, `dinner recipes`, `supper ideas` et `snack suggestions`.
- L'audit indépendant termine en 79,1 s et accepte ces quatre requêtes sans réintroduire `Lundi`, `Mardi`, une exclusion ou une instruction de placement. Le correctif de non-exposition des lignes est donc validé en live.
- Le défaut décisif se déplace vers le corpus : les quatre requêtes conservent `categoryPath=null`, parce que l'audit final avait l'ordre de préserver la portée choisie auparavant. Les recherches partent donc sur l'ensemble du catalogue : petit-déjeuner atteint 35 s, dîner atteint 35 s, souper ne retourne aucun hit après 30,7 s et collation atteint 35 s.
- L'`EvidenceBundle` initial reste vide. L'`EvidenceJudge` demande davantage de preuves; une recherche LLM `meals for lunch` sans portée atteint encore 35 s. Une tentative ultérieure `sources for souper and collation recipes` sous une catégorie choisie termine rapidement en 9 s, mais fusionne deux facettes et ne retourne aucun hit. Après six tentatives cumulées, dont quatre dégradées ou expirées, le bundle reste vide et le pipeline s'arrête sans writer.
- Cette exécution ne remet pas en cause l'épinglage fichier/page : Live69 et Live70 ont déjà prouvé que les recherches dans une catégorie pertinente transportent `docPath`, pages et extraits, puis que `documents.context` relit exactement ces ancres. Live71 échoue avant ce stade parce qu'aucun passage candidat n'est obtenu.
- Cause contractuelle retenue : l'audit final peut corriger le sens des mots de recherche, mais pas la portée documentaire qui détermine où ces mots sont exécutés. Une requête sémantiquement acceptable avec `categoryPath=null` n'est pas nécessairement une stratégie de récupération acceptable lorsque le catalogue propose des catégories plausibles et que la recherche globale expire.
- Correctif suivant : permettre au même audit LLM indépendant de conserver ou réviser chaque `categoryPath` à partir des chemins réellement fournis par le catalogue. Le code doit uniquement exiger une décision explicite, vérifier qu'une portée non nulle est un chemin autorisé et appliquer mécaniquement le choix. Aucune catégorie métier, y compris `Cuisine`, ne doit être choisie ou injectée par une heuristique.
- Prochaine preuve discriminante : une régression générique doit montrer qu'un audit LLM remplace une portée nulle par une catégorie autorisée, qu'une catégorie inventée est refusée, puis Live72 doit exécuter les quatre requêtes dans le corpus choisi par le LLM et atteindre à nouveau les vraies pages avant de valider le squelette compact non imitable.

### 2026-07-22 - Portée documentaire révisable par l'audit LLM indépendant après Live71

- L'audit final ne considère plus `categoryPath` comme une donnée immuable. Les seules liaisons immuables restent l'index de requête, l'outil et la facette typée; le LLM réévalue maintenant conjointement le sens de la requête et le corpus dans lequel elle sera exécutée.
- Pour limiter les erreurs de copie du petit modèle, le prompt expose une table générique `AVAILABLE_SCOPE_IDS` construite à partir des `CatalogHints` réels : `0` représente explicitement le corpus global et `1..N` les chemins disponibles dans leur ordre de catalogue. Le LLM choisit un `scopeId` pour chaque requête; il ne doit jamais recopier ou inventer lui-même un chemin.
- La décision reste sémantique et appartient au LLM. Le prompt lui demande de choisir où les valeurs recherchées sont susceptibles de vivre, indépendamment du format final demandé, et de réserver `scopeId=0` aux cas où aucune catégorie ne convient ou lorsqu'une recherche globale est réellement voulue.
- Le code se limite au contrat et au transport : il extrait l'identifiant, exige une décision explicite lorsqu'un catalogue est disponible, refuse les valeurs négatives ou hors catalogue, résout mécaniquement l'identifiant vers le chemin exact, puis applique ce chemin à la requête correspondante. Il ne contient aucune préférence pour `Cuisine` ni aucun vocabulaire métier.
- Les audits génériques de requêtes conservent leur comportement historique et leur portée immuable. Seul `PlannerAcceptedQueryAudit`, qui constitue le dernier contrôle sémantique indépendant avant exécution, est autorisé à appliquer les révisions de portée.
- La régression discriminante reproduit le défaut de Live71 : le plan compact fournit quatre requêtes sous portée nulle; le premier audit renvoie `scopeId=99` et des buts incomplets, ce que le pipeline refuse; le repair LLM renvoie ensuite `scopeId=1`, et les quatre requêtes reçues par l'exécuteur portent bien le chemin catalogue `Cuisine` résolu à partir du fixture.
- Les tests d'architecture vérifient la table `0/null`, les identifiants de catégories génériques `Operations` et `Policies`, la portée provisoire, le placeholder obligatoire `scopeId=-1`, le parsing des identifiants et le refus mécanique de `-1` ou d'un identifiant supérieur au catalogue. Le prompt reste sous le budget maximal de 5 000 caractères.
- Validation post-correctif : compilation WinUI réussie; 122/122 tests d'architecture; scénario discriminant combiné 123/123; 101/101 tests de pipeline; lot élargi SourceBacked/OpenAI/ApiClient 382/382; `git diff --check` sans erreur de contenu, uniquement les avertissements EOL hérités.
- Prochaine preuve discriminante : Live72 doit montrer dans les traces `provisionalScopeId=0`, puis une décision LLM non nulle correspondant à une catégorie du catalogue, quatre recherches exécutées sous le chemin résolu, des hits munis de `docPath` et pages, et enfin l'entrée dans le writer compact non imitable.

### 2026-07-22 - Soixante-douzième live : protection mécanique effective, mais audit combiné trop chargé et portée globale conservée

- Journaux processus `artifacts/live-test-process-live72-20260722-011442/`, progression client `artifacts/client-live-final-weekly-meal-plan-20260721-231448/progress.log`, trace principale `rag-20260721231522634-588b79c3` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260722_011503.log`.
- Le test est annulé par son budget global exact de trente minutes et termine en `OperationCanceledException` pendant `RepairEvidenceStatusDecisionContractIfNeededAsync`. L'exception arrive avant l'écriture des artefacts de réponse : aucun `answers.txt`, aucun payload de sources, aucun writer et aucune carte source ne sont produits. Le serveur local est arrêté à 01:44:48.
- Le routeur termine en 43,7 s et utilise encore l'ancien binaire où `allows_partial_answer=true`. Le Planner termine en 186,6 s. Les adjudications reconstruisent les cinq jours et exactement les quatre colonnes `Petit-déjeuner`, `Dîner`, `Souper`, `Collation`; le faux `Déjeuner` imbriqué ne réapparaît pas.
- Le retry de stratégie générale atteint sa borne de 150 s. Le plan compact termine en 81,9 s avec quatre positions et une portée nulle. La réparation row-independent termine en 89,7 s et produit quatre requêtes sans jours de placement : `What dishes are served for breakfast?`, `What dishes are served for dinner?`, `What dishes are served for supper?`, `What dishes are served for snack?`.
- L'audit combiné requête/portée révèle sa surcharge. Sa première passe dure 117,2 s, choisit `clarify` et recopie `scopeId=-1` pour les quatre positions. Le garde-fou refuse bien ces identifiants placeholders : aucun chemin inventé ou hors catalogue n'est exécuté.
- Le repair focalisé dure 123,6 s et rend un contrat mécaniquement valide, mais choisit `scopeId=0` pour les quatre requêtes. La résolution mécanique fonctionne exactement comme prévu; elle applique donc honnêtement `categoryPath=null`, alors que ce choix sémantique du LLM est mauvais pour cette question.
- Les recherches globales petit-déjeuner, souper et collation retournent chacune douze hits; celle du dîner expire à 35 s et ne retourne rien. Le pipeline dispose ainsi de 36 hits globaux, mais pas encore d'un ensemble de preuves assez cohérent pour le plan demandé.
- Les boucles d'adjudication ne convergent pas. Un repair d'action finit par choisir une recherche supplémentaire qui recopie pratiquement toute la question (`plan de repas pour la semaine du lundi au vendredi avec petit-déjeuner, dîner, souper et collation chaque jour`) sous `Cuisine`. Elle retourne douze hits supplémentaires, soit 48 hits bruts au total, mais fusionne les quatre besoins et les lignes de présentation dans une seule requête faible.
- L'`EvidenceStatusReview`, après 139,5 s, classe six éléments utiles, trois faibles et un besoin manquant, puis demande `read_documents`. Le contrat de cette décision part en réparation, mais l'annulation globale intervient avant qu'une relecture documentaire exacte ou une sélection finale d'IDs puisse être exécutée.
- Le résultat distingue clairement les deux responsabilités. La mécanique d'identifiants a empêché l'exécution de `-1` et a résolu correctement `0` vers le corpus global; elle n'est pas la cause de l'échec. Le défaut est la décision sémantique du petit LLM dans un prompt combinant réécriture des quatre requêtes, buts et quatre choix de corpus.
- Ce live ne réfute pas l'épinglage fichier/page : Live69 et Live70 ont déjà relié de vraies pages de recettes et effectué des `documents.context` exacts. Live72 accumule des candidats, mais n'atteint pas la sélection finale d'IDs puis la relecture qui transforment ces candidats en preuves publiables.

### 2026-07-22 - Séparation de l'audit des requêtes et du micro-audit LLM de portée après Live72

- `PlannerAcceptedQueryAudit` redevient volontairement un auditeur de requêtes seulement. Il peut accepter, clarifier ou réécrire `query` et `purpose`, mais ne reçoit plus la table du catalogue et ne modifie plus `categoryPath`. Cette étape garde un seul objectif sémantique à la fois.
- Lorsqu'un plan accepté contient encore au moins une portée nulle et que le catalogue expose de vrais chemins, le pipeline appelle ensuite le nouveau `PlannerAcceptedScopeAudit`. Ce micro-audit ne peut ni réécrire les requêtes, ni modifier les outils ou les facettes, ni répondre à l'utilisateur : il choisit uniquement un `scopeId` par requête.
- Le prompt focalisé expose `QUESTION_FOCUS`, `TASK_KIND`, les facettes typées, les requêtes déjà acceptées avec leur portée courante et une table compacte `0=null`, `1..N=categoryPath`. Les alias sont volontairement omis; avec les 31 catégories du catalogue live, le prompt reste sous 5 000 caractères.
- Le LLM demeure l'unique décideur sémantique du corpus pertinent. Le code exige seulement `decision`, une raison courte, le bon nombre d'identifiants et des valeurs connues. Il rejette les placeholders négatifs et les IDs hors catalogue, puis traduit mécaniquement chaque ID valide vers le chemin exact correspondant.
- Si la première réponse est incomplète ou invalide, un seul `PlannerAcceptedScopeAuditContractRepair` focalisé reçoit les problèmes de contrat. Un second échec ou un `clarify` reste terminal; le code ne choisit aucune catégorie de remplacement.
- Le budget exact de l'audit de portée et de son repair est limité à 256 tokens. Cette séparation remplace l'audit combiné long de Live72, qui avait consommé 117,2 s puis 123,6 s sans choisir de corpus spécialisé.
- La régression discriminante reproduit une portée initiale nulle, fait rendre `99` par le premier micro-audit, prouve son refus avant exécution, puis fait choisir `1` par le repair LLM. L'exécuteur reçoit ensuite le chemin `Cuisine` provenant uniquement du catalogue du fixture, jamais d'une règle métier du code.
- Validation post-correctif : compilation réussie; 122/122 tests d'architecture; scénario discriminant 1/1; 101/101 tests de pipeline; lot élargi SourceBacked/OpenAI/ApiClient 382/382; `git diff --check` sans erreur de contenu, uniquement les avertissements EOL hérités.

### 2026-07-22 - Suppression de l'heuristique lexicale qui autorisait à tort les réponses partielles

- Le diagnostic de Live72 a également révélé `allows_partial_answer=true` en mode strict. La cause était une liste de phrases codées en dur dans `ToolAgentOrchestrator.SourceBackedPartialAnswerPolicy.cs` : des formulations telles que `n invente rien`, `only from sources` ou `uniquement les sources` étaient interprétées comme une permission de ne répondre qu'à une partie de la demande.
- Cette équivalence est fausse. Exiger des sources, des citations ou l'absence d'invention impose une discipline de preuve; cela n'autorise pas à omettre des lignes, des colonnes ou des affirmations demandées.
- La liste lexicale, sa normalisation et sa recherche de phrases sont supprimées. En mode strict, l'application fournit désormais seulement la valeur par défaut `false`. En mode non strict, elle conserve la valeur par défaut permise par le routeur.
- Le Planner LLM et l'auditeur d'intake gardent la décision sémantique : ils peuvent choisir `allowsPartialAnswer=true` uniquement lorsque la demande autorise explicitement une réponse incomplète, un sous-ensemble ou des omissions. Une simple instruction de signaler les informations manquantes ne suffit plus.
- Les prompts distinguent explicitement `APPLICATION_PARTIAL_ANSWER_DEFAULT` d'une politique imposée. Le code ne recherche plus de mots français ou anglais pour décider l'intention de l'utilisateur.
- Le test historique qui attendait `True` pour une demande stricte anti-fabrication a été renommé et inversé; il vérifie maintenant `False` dans le Planner et l'`EvidenceJudge`. Les tests d'architecture vérifient aussi l'absence de l'ancienne liste et de la phrase métier codée en dur.
- Prochaine preuve discriminante : Live73 doit afficher `allows_partial_answer=false`, obtenir quatre requêtes acceptables, appeler le micro-audit de portée séparé, choisir par LLM une catégorie existante pertinente, puis exécuter les recherches sous les chemins résolus. Le gate reste la sélection explicite d'IDs, la relecture des vraies pages, le writer de vingt cellules concrètes, l'audit sémantique et au moins deux cartes source utiles.

### 2026-07-22 - Soixante-treizième live : portée Cuisine choisie en 17 secondes et pages réelles retrouvées, mais preuve utile trop faible puis suivi contaminé

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260722-000216/answers-readable.txt`, progression dans le même dossier, journaux processus `artifacts/live-test-process-live73-20260722-020211/`, trace principale `rag-20260722000251583-fd05493a`, trace SourceBacked `sbrag-0ca6a6f17af94fe6b4ee043786998c76` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260722_020232.log`.
- Le test échoue proprement après 25 min 20 s sur le payload de sources nul. Le pipeline actif dure 1 485,6 s et rend une insuffisance sûre de 444 caractères. Il n'expose aucun planning ni aucune carte, puis arrête le runtime local.
- Le premier gate du correctif est validé : la trace démarre avec `mode=strict` et `allows_partial_answer=false`. La consigne `N'invente rien` n'est donc plus transformée par une heuristique en autorisation de produire une grille partielle.
- Le routeur termine en 44,5 s et le Planner principal en 192,0 s. L'audit d'intake dure 126,8 s; il comprend correctement la grille mais concatène d'abord les ancres. Le repair d'ancres, les adjudications d'axes et leurs patches établissent finalement les cinq jours comme expansion de la plage `lundi au vendredi` et exactement `Petit-déjeuner`, `Dîner`, `Souper`, `Collation` comme colonnes. La forme 5 x 4 est acceptée sans prétendre que les jours intermédiaires étaient écrits mot pour mot.
- La stratégie générale atteint sa borne de 150 s. Le plan compact dure 88,1 s puis son repair de format 38,6 s. Il fournit quatre requêtes faibles mais correctement liées aux quatre facettes, toutes avec une portée nulle : `Petit-déjeuner Cuisine`, `Dîner Cuisine`, `Souper Cuisine`, `Collation Cuisine`.
- L'audit de requêtes séparé termine en 98,3 s et conserve quatre besoins distincts sous la forme `Petit-déjeuner Recettes`, `Dîner Recettes`, `Souper Recettes`, `Collation Recettes`. Aucune requête provisoire n'est encore exécutée.
- Le nouveau `PlannerAcceptedScopeAudit` reçoit ensuite uniquement la décision de corpus. Il termine en 17,0 s, choisit explicitement `scopeIds=[1,1,1,1]`, et le code résout mécaniquement l'ID 1 vers le vrai chemin catalogue `Cuisine`. Cette trajectoire valide en live la séparation requête/portée et confirme que ni le code ni une heuristique métier n'ont choisi `Cuisine`.
- Les quatre recherches sont bien exécutées avec `categoryPath=Cuisine`. Elles retournent respectivement 3, 12, 4 et 0 hits en 29,8 s, 27,2 s, 25,4 s et 23,9 s. Aucun timeout ni résultat dégradé n'est enregistré.
- Les hits transportent déjà leurs ancres mécaniques. Exemples : `Cuisine/Je_cuisine_simplement.pdf` p.55 pour le petit-déjeuner, le même fichier p.19 pour le dîner, et `Cuisine/chefbot_livre_de_recettes_fr.pdf` p.7 pour le souper. Le bundle contient 19 éléments issus de quatre tentatives réussies. L'absence de lien final ne vient donc pas d'une perte du fichier ou de la page.
- La qualité sémantique reste insuffisante. La recherche collation ne retourne rien; le premier hit dîner montre surtout des instructions de cuisson et le premier hit souper est une page introductive sur le robot. Le fait qu'un passage soit épinglé ne le rend pas légitime pour remplir une cellule.
- L'`EvidenceJudge` choisit `answer` mais ne sélectionne aucun ID. Il invente aussi une note sur l'absence de repas pour samedi et dimanche, alors que ces jours ne sont pas demandés. Le contrat `missing_answer_evidence_ids` empêche correctement tout writer.
- L'`EvidenceStatusReview` termine en 123,3 s et recommande `clarify`. Il ne classe que `E5` comme utile, classe `E1,E2,E3,E4,E6,E7,E8` comme faibles et signale un besoin manquant. Sa sortie révèle une contamination de contrat : `missingRequirements` contient une variante de la chaîne d'exemple `max 4 short strings`, rendue comme `max 0 short strings` dans la réponse terminale.
- La réparation de sélection dure 68,7 s sans produire de sélection writeable. L'action repair dure 79,6 s, puis son retry 96,1 s. Le dernier LLM choisit `execute` avec `rag.multi_search`, mais recopie presque toute la question et la grille dans `query`.
- Le garde-fou de requête compacte refuse ce suivi avec `non_executable_follow_up_requests`; aucun outil supplémentaire n'est exécuté. L'itération s'arrête sur `no_executable_follow_up`, avec `decision=need_more_evidence`, 19 éléments internes et zéro source publiée.
- La mémoire demeure `context_only_never_final_proof`; elle ne fournit aucun repas, aucun ID de secours et aucune source finale.
- Bilan discriminant : Live73 valide complètement la nouvelle portée LLM, la résolution mécanique vers `Cuisine`, quatre recherches spécialisées et le maintien fichier/page. Le blocage se trouve désormais après le retrieval : fenêtre de candidats peu writeable, placeholder copié par la revue de statut, hallucination d'exigences hors grille et repair d'action contaminé par la question complète.

### 2026-07-22 - Statut non imitable et repair de recherche fondé sur l'intake typée après Live73

- Le schéma `EvidenceStatusReview` ne contient plus de valeurs textuelles imitables comme `max 4 short strings`, `max 2 concrete targets or assessments` ou un enum concaténé dans une valeur. Il expose un objet compact dont les chaînes et tableaux sont vides, tandis que limites et valeurs autorisées sont décrites hors JSON.
- Le contexte de statut transporte `TYPED_STRUCTURE_EXHAUSTIVE=true` : le LLM doit considérer les lignes et colonnes typées comme la frontière complète et ne plus inventer une période, une ligne, un slot ou un champ absent de la demande.
- Le contrat mécanique refuse désormais une revue dont `objective`, `answerShape` ou `databaseAssessment` est vide, ainsi qu'un `recommendedNextAction` hors des six valeurs autorisées. Il ne remplit aucun champ; il demande au LLM de refaire sa décision sémantique via `EvidenceStatusReviewDecisionContractRepair`.
- Les repairs de retrieval structurés ne reçoivent plus la question complète ni les libellés de lignes de placement. Ils reçoivent `QUESTION_FOCUS`, `TASK_KIND`, les contraintes bornées, `PLACEMENT_ROW_COUNT`, les facettes typées et le nombre de cellules. Les lignes sont signalées comme omises et ne peuvent plus être recopiées dans une requête row-independent.
- Le LLM reste le seul auteur de l'action : il choisit outil, termes, portée et but à partir du focus approuvé, du statut des tentatives, des preuves visibles et des notes manquantes. Le code vérifie seulement l'outil, l'ancre, la compacité, la nouveauté et l'appartenance du chemin au catalogue.
- Les prompts précisent que les notes de manque précédentes sont provisoires et qu'une ligne, période, facette ou exigence hors de la structure exhaustive doit être ignorée. Cette règle vise directement l'invention de samedi/dimanche de Live73 sans coder aucun jour ni domaine métier.
- Les nouvelles regressions vérifient l'absence de la question complète, de `Monday` et de `Friday` dans le repair d'action d'une grille générique 5 x 4, la présence du nombre de lignes et des facettes typées, l'absence des trois anciens placeholders dans tous les prompts de statut et le respect des budgets locaux.
- Une régression comportementale force la copie exacte du nouveau squelette vide. Le pipeline refuse cette revue, expose les problèmes de champs vides et d'enum, obtient une seconde décision LLM complète, puis utilise uniquement l'ID choisi par ce LLM jusqu'au writer vérifié.
- Validation post-correctif : compilation réussie; 122/122 tests d'architecture; scénario de copie du squelette 1/1; 102/102 tests de pipeline; lot élargi SourceBacked/OpenAI/ApiClient 383/383; `git diff --check` sans erreur de contenu, uniquement les avertissements EOL hérités.
- Prochaine preuve discriminante : Live74 doit conserver `allows_partial_answer=false`, la grille 5 x 4 et le choix rapide de `Cuisine`; la revue ne doit plus citer de placeholder ni samedi/dimanche. Si un facet manque, le repair d'action doit produire une requête courte et ciblée issue de l'intake typée, puis atteindre soit une sélection d'IDs et des relectures exactes, soit un arrêt honnête fondé sur un manque réel.
- Dette générique à traiter après cette preuve : `PlannerAcceptedScopeAudit` est actuellement déclenché lorsqu'au moins une portée reste nulle. Il faudra aussi permettre une revue LLM d'une portée non nulle choisie par le Planner mais potentiellement erronée, tout en préservant un scope explicitement imposé par l'utilisateur ou l'interface; cette extension ne doit reposer sur aucune détection sémantique codée en dur.

### 2026-07-22 - Soixante-quatorzième live : repair non contaminé validé, mais audit sémantique trop faible et zéro preuve candidate

- Journaux processus `artifacts/live-test-process-live74-20260722-025159/`, progression client `artifacts/client-live-final-weekly-meal-plan-20260722-005205/progress.log`, trace principale `rag-20260722005240304-5290b6b0` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260722_025222.log`.
- Le test est annulé par son budget global exact de trente minutes. Il termine en `OperationCanceledException` pendant `EvidenceJudgeActionContractRetry`, avant tout writer, toute réponse, tout payload de sources ou toute carte UI. Cette annulation ne masque pas une sélection : le bundle est resté vide pendant toute l'exécution.
- Le mode strict et `allows_partial_answer=false` sont conservés. Le routeur termine en 34,5 s. Le Planner principal termine en 169,0 s, puis `PlannerIntakeReview` en 195,3 s. Les réparations focalisées reconstruisent correctement les cinq lignes `Lundi` à `Vendredi` et les quatre colonnes `Petit-déjeuner`, `Dîner`, `Souper`, `Collation`, soit exactement vingt cellules.
- La stratégie générale atteint sa borne de 150 s. Le plan compact termine en 81,9 s avec quatre requêtes françaises, mais chacune recopie encore `pour la semaine du lundi au vendredi`. `PlannerRowIndependentQueryRepair` termine en 61,6 s, retire les jours mais omet les quatre buts. Son retry spécialisé termine en 47,3 s et rend quatre couples complets.
- Le retry spécialisé dégrade cependant les termes en anglais et en formulations abstraites : `sources for breakfast recipes`, `sources for dinner recipes`, `sources for supper recipes` et `sources for snack recipes`. Il ne reçoit actuellement pas la langue dans son intake compact, et le modèle local privilégie la formulation anglaise du contrat.
- `PlannerAcceptedQueryAudit` reçoit pourtant la règle explicite selon laquelle `source`, `sources for`, `information`, `document` et `example` ne sont pas des termes suffisants. Sa première passe dure 67,9 s et retourne `clarify`, mais avec les quatre révisions remplies, ce qui viole le contrat `clarify => requests={}`. Le repair de contrat dure 94,1 s et convertit cette sortie en `accept` sans améliorer aucune requête. L'audit indépendant n'a donc pas rempli sa fonction sémantique sur ce run.
- Le micro-audit de portée reste rapide : 19,2 s. Il choisit néanmoins `scopeIds=[1,0,0,0]`, soit `Cuisine` uniquement pour le petit-déjeuner et le corpus global pour les trois autres facettes. Le code traduit fidèlement ces IDs sans ajouter de règle culinaire; cette incohérence est entièrement une décision LLM et révèle que le prompt doit exiger une justification cohérente entre facettes sémantiquement apparentées.
- Les quatre recherches initiales rendent zéro preuve : petit-déjeuner sous `Cuisine`, zéro hit en 9,9 s; dîner global, timeout à 35 s; souper global, zéro hit en 31,1 s; collation globale, timeout à 35 s. Aucun `docPath`, aucune page et aucun extrait candidat n'existent donc dans l'`EvidenceBundle` de Live74.
- L'`EvidenceJudge` demande davantage de preuves. Le correctif post-Live73 est validé sur son objectif précis : l'action repair ne reçoit plus la question complète ni les jours, et son retry produit une action compacte au lieu de recopier la grille.
- La première relance choisit `rag.search`, `sources for dinner recipes`, sous `Cuisine`; elle retourne zéro hit en 10,1 s. La seconde choisit `sources for souper recipes`, également sous `Cuisine`; elle retourne zéro hit en 10,1 s. La relance a donc corrigé la portée mais répète le vocabulaire abstrait déjà inefficace au lieu de produire un angle réellement nouveau et concret.
- Une troisième boucle d'action démarre. Son repair termine en 78,2 s, puis le budget global annule le retry de contrat après 10,2 s. Le pipeline n'a jamais disposé d'une seule preuve à sélectionner, relire ou transmettre au Writer.
- Ce live confirme à nouveau que l'épinglage mécanique n'est pas en cause. Quand un hit existe, `EvidenceBundleBuilder` conserve `docId`, `docPath`, `pageStart`, `pageEnd` et l'extrait; `SourceContractVerifier` exige ensuite une source visible, une page valide et un ID sélectionné; `SourceBackedUiPayloadMapper` transforme seulement ces preuves citées en cartes UI. Ici, la chaîne s'arrête avant le premier maillon candidat.
- Prochaine correction générique : renforcer les décisions LLM de requête et de portée sans ajouter d'heuristique métier. Le contexte compact doit exposer la langue; l'audit et son repair doivent interdire explicitement l'acceptation inchangée de requêtes abstraites déjà signalées; le micro-audit de portée doit réévaluer chaque facette et expliquer toute divergence; les relances doivent choisir un vocabulaire source-domain réellement nouveau après zéro hit.

### 2026-07-22 - Langue, qualité de requête, cohérence de portée et diversification après Live74

- `PlannerRowIndependentQueryRepair` et son retry reçoivent maintenant `LANGUAGE_HINT`. Le LLM doit employer la langue de l'intake sauf indice explicite d'une autre langue documentaire; le fait que le contrat système soit écrit en anglais ne justifie plus un basculement implicite vers l'anglais.
- Le même micro-contrat rappelle qu'une requête composée de mots génériques comme `source`, `sources for`, `information`, `document` ou `example` plus le seul libellé de facette n'est pas answer-bearing. Le LLM doit la remplacer par des termes de contenu susceptibles d'apparaître dans les passages utiles.
- `PlannerAcceptedQueryAuditContractRepair` réapplique désormais explicitement le gate sémantique du premier audit. Une sortie parseable mais incomplète ne peut plus être réparée en simple `accept` sans réexaminer les formulations provisoires génériques. `LANGUAGE_HINT` est également présent dans l'audit initial et son repair.
- `PlannerAcceptedScopeAudit` examine maintenant l'ensemble des requêtes comme une décision cohérente. Lorsque plusieurs facettes apparentées cherchent des familles candidates pour une même tâche, le LLM ne doit pas choisir une catégorie plausible pour la première puis laisser les autres à `0/null` sans expliquer une vraie divergence sémantique. Le repair focalisé porte la même règle.
- L'intake compact des actions de récupération structurées expose lui aussi `LANGUAGE_HINT`. Les deux passes LLM d'action savent qu'après zéro hit, changer seulement le libellé de facette ou `categoryPath` ne constitue pas un nouvel angle de recherche; elles doivent choisir de nouveaux termes porteurs de contenu.
- Aucune de ces règles ne classe un terme, une catégorie ou une source dans le code. Le LLM choisit toujours la langue effective, le vocabulaire, la portée, l'outil et le but. Le code continue uniquement de transporter le contexte typé, vérifier la forme, résoudre les IDs catalogue et refuser les actions non exécutables ou répétées.
- Les règles ont été compactées après que les tests ont détecté deux dépassements de prompts, respectivement 465 et 40 caractères. Les plafonds n'ont pas été augmentés : la répétition a été supprimée et les consignes ont été condensées afin de préserver la marge du petit modèle local.
- Validation finale : 122/122 tests d'architecture et de budgets; 102/102 tests du pipeline; 383/383 tests SourceBacked/OpenAI/ApiClient; compilation WinUI réussie; `git diff --check` sans erreur de contenu, uniquement les avertissements EOL hérités.
- Prochaine preuve discriminante : Live75 doit conserver la grille 5 x 4 et `allows_partial_answer=false`, produire des requêtes dans une langue cohérente avec l'intake ou justifier une autre langue, refuser/réécrire toute variante `sources for <facette>`, choisir un scope cohérent pour les quatre facettes puis obtenir au moins un bundle non vide. Si une recherche rend zéro hit, sa relance doit changer réellement de vocabulaire avant toute nouvelle exécution.

### 2026-07-22 - Soixante-quinzième live : arrêt précoce sur une ancre sémantiquement correcte mais recopiée avec une faute

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260722-014050/answers-readable.txt`, journaux processus `artifacts/live-test-process-live75-20260722-034045/`, trace principale `rag-20260722014125194-696138ce`, trace SourceBacked `sbrag-ad17ba31cc4947bbada943bcb9a53aaa` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260722_034106.log`.
- Le run termine en 9 min 9 s et échoue sur le payload nul. Le pipeline actif rend l'insuffisance sûre de 293 caractères après environ 8 min 34 s, avec zéro outil, zéro preuve et zéro carte. Il ne constitue donc aucune validation des nouvelles règles de requête ou de portée, qui n'ont jamais été atteintes.
- Le mode strict et `allows_partial_answer=false` restent corrects. Le routeur termine en 29,0 s, le Planner en 184,3 s et `PlannerIntakeReview` en 204,3 s. La revue comprend exactement les lignes `Lundi` à `Vendredi` et les colonnes `Petit-déjeuner`, `Dîner`, `Souper`, `Collation`, sans faux `Déjeuner`.
- Le défaut vient des ancres verbatim. L'audit agrège d'abord les deux axes dans des citations invalides. `PlannerIntakeAnchorRepair` choisit ensuite `clarify` sans ancres. L'adjudication de lignes choisit bien les cinq jours et la plage continue, mais recopie `J'ai besoin que tu me fasse...` au lieu du vrai texte `tu me fasses...`.
- Le premier patch de métadonnées rétablit `relation=range_expansion` mais conserve la faute. Le second ajoute un point qui ne correspond pas exactement à la question, puis son retry reproduit la même chaîne. Le repair d'ancres global revient enfin à `clarify` et le pipeline s'arrête honnêtement avec `The retrieval planner did not produce executable retrieval requests`.
- Le vérificateur mécanique a raison de refuser : une ancre de preuve ou d'intake dite verbatim ne doit jamais être corrigée approximativement ou acceptée par similarité. Mais demander au petit LLM de recopier plusieurs fois toute la phrase crée une fragilité de transport inutile après que le même LLM a déjà approuvé les labels et la relation sémantique.
- Bilan discriminant : Live75 révèle une régression indépendante et antérieure au retrieval. Les décisions sémantiques de structure sont bonnes; la seule information invalide est l'orthographe de la chaîne d'ancre. La correction doit rester mécanique et générique, sans faire déduire les jours ou la plage par le code.

### 2026-07-22 - Résolution mécanique d'une ancre unique depuis les sémantiques LLM approuvées après Live75

- Après un patch de métadonnées, le pipeline peut maintenant canoniser l'ancre uniquement lorsque le LLM a déjà rendu `decision=accept`, choisi les labels ordonnés et choisi `relation=exact` ou `range_expansion`.
- Pour `exact`, le code résout l'unique occurrence littérale du seul label approuvé. Pour `range_expansion`, il résout l'unique sous-chaîne allant de la première à la dernière valeur approuvée. Dans Live75, `Lundi...Vendredi` devient ainsi exactement `lundi au vendredi`, prélevé dans la question et jamais réécrit.
- Cette opération ne comporte aucune connaissance des jours, des repas ou du français. Elle fonctionne sur les labels et la relation fournis par le LLM et se limite à une résolution ordinale insensible à la casse. Le code ne choisit ni les membres intermédiaires, ni la nature de l'axe, ni la relation.
- Si l'un des deux labels n'existe pas littéralement, si leur ordre est inversé ou si une extrémité apparaît plusieurs fois, la résolution retourne `null`. Le pipeline conserve alors le chemin LLM de repair/clarification; il ne choisit jamais arbitrairement entre plusieurs ancres.
- Une trace `SBRAG_PLANNER_INTAKE_ROW_AXIS_QUOTE_RESOLUTION` rend la canonisation observable avec la relation et la chaîne exacte retenue.
- La régression de pipeline utilise maintenant volontairement `tu me fasse` dans la sortie du metadata patch et prouve que le flux complet atteint encore les quatre recherches. Le test d'architecture prouve `lundi au vendredi` dans le cas unique et `null` lorsque la même plage apparaît deux fois.
- Validation : compilation WinUI réussie; 224/224 tests architecture + pipeline; 383/383 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur de contenu, uniquement les avertissements EOL hérités.
- Prochaine preuve discriminante : Live76 doit franchir l'adjudication d'axe même si le LLM paraphrase légèrement son ancre, enregistrer la résolution mécanique exacte puis atteindre enfin les nouvelles règles de langue, qualité de requête et cohérence de portée prévues pour Live75.

### 2026-07-22 - Soixante-seizième live : ancre canonisée et requêtes françaises, puis placeholders de portée recopiés

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260722-020206/answers-readable.txt`, journaux processus `artifacts/live-test-process-live76-20260722-040200/`, trace principale `rag-20260722020240319-70318b52`, trace SourceBacked `sbrag-f93eaa5f7e284d389cada092991b8120` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260722_040222.log`.
- Le run termine après environ 17 min 42 s avec une insuffisance sûre de 298 caractères, zéro outil, zéro preuve et zéro carte. Il franchit cependant les nouveaux jalons d'ancre et de langue avant de s'arrêter sur le contrat de portée.
- Le routeur termine en 27,6 s et le Planner en 186,9 s. L'adjudication des lignes dure 84,3 s et reproduit exactement le défaut de Live75 : cinq labels corrects, sémantique de plage correcte, mais citation `tu me fasse...` avec un point supplémentaire.
- Le premier metadata patch dure 17,1 s, choisit `relation=range_expansion`, puis la nouvelle résolution mécanique s'active immédiatement. La trace live `SBRAG_PLANNER_INTAKE_ROW_AXIS_QUOTE_RESOLUTION` enregistre exactement `quote="lundi au vendredi"`. L'axe passe sans second patch ni retry.
- L'adjudication des colonnes propose d'abord cinq labels avec le faux `Déjeuner` imbriqué. Le patch d'ancres expose le conflit, puis la sélection LLM conserve `Petit-déjeuner`, `Dîner`, `Souper`, `Collation`. La structure finale contient exactement vingt cellules et l'ancre canonique de plage.
- La stratégie générale atteint sa borne de 150 s. Le plan compact dure 69,9 s et produit quatre requêtes françaises contaminées par la semaine. `PlannerRowIndependentQueryRepair` dure 118,1 s et valide la nouvelle règle de langue : il retire les jours, reste en français et ne produit aucun `sources for`.
- Ses requêtes sont `Repas rapide pour petit-déjeuner`, `Menu déjeuner savoureux`, `Recettes faciles pour souper` et `Repas rapide pour collation`. Elles restent sémantiquement imparfaites : en particulier la facette exacte `Dîner` devient `déjeuner` et plusieurs formulations sont générales.
- `PlannerAcceptedQueryAudit` dure 124,5 s. Il conserve la langue et modifie légèrement le petit-déjeuner, mais ne corrige pas `Dîner -> déjeuner`. Cette défaillance demeure une décision sémantique du petit LLM; aucun dictionnaire ou classement métier n'est introduit dans le code.
- Le micro-audit de portée dure 43,3 s puis son repair 36,6 s. La première sortie conserve `scopeId=-1` pour trois positions; le repair reproduit encore des placeholders. Le garde-fou refuse correctement ces IDs hors table, et aucune requête provisoire n'est exécutée.
- Le pipeline repart alors sur `PlannerQueryStrategyRetry`, qui atteint inutilement sa borne de 150 s et ne produit aucune requête. Il s'arrête honnêtement avec `The LLM strategy reviewer did not approve an executable semantic retrieval plan after focused repairs`.
- Bilan discriminant : la résolution d'ancre est validée en live, la langue française et l'absence de `sources for` sont validées, mais le schéma `scopeIds=[-1,...]` reste une valeur imitable qui empêche la nouvelle règle de cohérence de portée d'être réellement évaluée.

### 2026-07-22 - Contrat de portée sans aucun ID d'exemple après Live76

- `PlannerAcceptedScopeAudit` et son repair ne contiennent plus de squelette `scopeIds` rempli par des valeurs négatives. Aucun exemple d'ID n'est fourni dans la forme de sortie.
- Le prompt décrit seulement les trois clés obligatoires `decision`, `reason`, `scopeIds`, puis exige un tableau JSON de longueur exactement égale au nombre de requêtes, composé exclusivement d'entiers présents dans `AVAILABLE_SCOPE_IDS` et ordonnés par requête.
- Le repair interdit les IDs négatifs ou placeholders sans écrire littéralement une valeur susceptible d'être copiée. Les diagnostics `unknown_accepted_scope_audit_scope_id ... scopeId=<valeur>` sont compactés en `unknown_scope_id` avant réinjection; ni `-1` ni un ID hors table comme `99` ne réapparaissent dans le contexte LLM.
- La décision reste entièrement sémantique : aucune valeur par défaut n'est insérée, `0/null` n'est pas converti, et le code ne propage une catégorie que lorsque le LLM renvoie lui-même un identifiant présent dans la table.
- Les tests d'architecture et de pipeline interdisent maintenant explicitement `-1` dans les prompts initial et de repair, ainsi que `scopeId=99` dans le repair. Ils exigent le contrat de cardinalité et le diagnostic neutralisé.
- Validation : compilation WinUI réussie; 122/122 tests d'architecture; 102/102 tests de pipeline; 383/383 tests SourceBacked/OpenAI/ApiClient; `git diff --check` sans erreur de contenu, seulement les avertissements EOL hérités.
- Prochaine preuve discriminante : Live77 doit retourner quatre IDs réels sans exemple à copier, appliquer une portée cohérente ou expliquer une divergence, puis exécuter enfin les quatre recherches françaises. Le gate aval redevient un `EvidenceBundle` non vide avec ancres fichier/page.

### 2026-07-22 - Soixante-dix-septième live : portée cohérente validée, PDF/page transportés, mais requêtes ancrées sur le brouillon et sélection vide

- Live canonique `artifacts/client-live-final-weekly-meal-plan-20260722-023217/answers-readable.txt`, journaux processus `artifacts/live-test-process-live77-20260722-043210/`, trace principale `rag-20260722023255688-4756b44d`, trace SourceBacked `sbrag-35c026046aeb4ea58f941f759ef81322` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260722_043235.log`.
- Le test termine en 26 min 56 s et échoue sur le payload de sources nul. Le pipeline actif dure 1 578,5 s, rend une insuffisance sûre de 432 caractères et n'expose aucune carte. Il n'a toutefois pas atteint sa borne globale de trente minutes.
- Le routeur choisit correctement le RAG strict, le français et `allows_partial_answer=false` en 35,1 s. Le Planner principal dure 256,3 s et la revue d'intake 200,9 s.
- La revue d'intake reproduit les deux erreurs du petit modèle : liste de jours reconstruite comme ancre exacte et faux `Déjeuner` imbriqué dans `Petit-déjeuner`. Les contrats les refusent. L'adjudication de lignes choisit les cinq jours; son metadata patch choisit `range_expansion`; la résolution mécanique enregistre encore exactement `lundi au vendredi`. Le micro-flux des colonnes retire ensuite `Déjeuner`. La structure acceptée contient bien cinq lignes, quatre colonnes et vingt cellules.
- La stratégie générale atteint encore sa borne de 150 s sans plan. Le plan compact termine en 66,0 s avec quatre requêtes françaises mécaniquement valides mais sémantiquement faibles : `Petit-déjeuner sources de Cuisine/Documents techniques`, puis les variantes Dîner, Souper et Collation.
- L'audit sémantique indépendant dure 117,9 s mais reste lexicalement ancré sur ce brouillon. Il ne fait que déplacer les mots en `sources de Cuisine/Documents techniques pour <facette>` et accepte les quatre requêtes, alors que son propre contrat interdit précisément la méta-formulation de recherche et le nom de portée comme contenu principal.
- Le correctif du Live76 est entièrement validé en live. L'audit de portée termine en 24,4 s; son repair de contrat en 25,3 s; la décision finale est `scopeIds=[1,1,1,1]`. Les quatre IDs sont réels et tous résolus vers `Cuisine`, sans `-1`, placeholder, défaut codé ou divergence incohérente entre facettes.
- Les quatre recherches sont exécutées séquentiellement sous `Cuisine`. Petit-déjeuner atteint le timeout de 35 s et retourne zéro hit. Dîner retourne deux hits en 30,6 s. Souper et Collation retournent chacun un hit en environ 28 s.
- Les trois recherches réussies convergent vers la même ancre mécanique : `Cuisine/si-on-cuisinait.pdf`, page 4. L'extrait visible parle surtout de techniques, de préparation d'activité et de cuisine « vite faite »; ce n'est pas une liste de repas concrète. Le diagnostic du test confirme néanmoins `si-on-cuisinait.pdf p.4`, score 1,02, rôle `actionable_item`, une content card et deux facts.
- Après déduplication et construction canonique, l'`EvidenceBundle` contient deux éléments, quatre tentatives, un timeout et un retrieval dégradé. Le transport fichier/page fonctionne donc; l'absence de carte ne vient pas d'une perte du `docPath` ou de la page.
- Le premier `EvidenceJudge` choisit contradictoirement `decision=answer` avec `selected_count=0`, tout en affirmant qu'aucune source supplémentaire n'a été trouvée pour Souper et Collation. Le garde-fou `missing_answer_evidence_ids` interdit correctement le Writer.
- La revue de statut identifie deux IDs comme potentiellement utiles, mais rend d'abord `clarify` sans besoin manquant. Ses repairs convergent vers `need_more_evidence`, deux utiles et un manque, tout en violant encore trois contraintes de cohérence. Aucun code ne choisit ces deux IDs à la place du LLM.
- La réparation finale de sélection ne produit toujours pas une sélection `answer` valide. L'action de récupération et son retry proposent ensuite exactement `sources de Cuisine/Documents techniques pour Souper`, déjà exécutée. Le contrôleur refuse cette répétition comme non nouvelle et s'arrête sur `no_executable_follow_up`.
- Bilan discriminant : Live77 valide la portée LLM cohérente, quatre recherches réelles, un bundle non vide et l'ancre PDF/page. Il invalide l'hypothèse d'un problème mécanique d'épinglage. Le blocage dominant est l'ancrage lexical de l'auditeur sur un mauvais brouillon, suivi d'une sélection LLM contradictoire et d'une relance non diversifiée.

### 2026-07-22 - Auteur de requêtes aveugle au brouillon et à la portée après Live77

- `PlannerAcceptedQueryAudit` devient un auteur/auditeur LLM indépendant et aveugle. Il ne reçoit plus la requête provisoire, son but, le nom de l'outil ni `categoryPath`; il reçoit uniquement l'objectif compact, la langue, les facettes typées et la liaison immuable index-vers-facette.
- Chaque requête doit être rédigée de zéro avec des termes susceptibles d'apparaître dans un passage qui contient réellement les valeurs demandées. Le LLM ne peut donc plus simplement déplacer `sources de Cuisine/Documents techniques` ou être attiré par le nom du corpus.
- Cette séparation ne remplace aucune décision sémantique par du code. Le LLM choisit toujours tout le vocabulaire de contenu et décide si les quatre requêtes sont acceptables; le code ne fait que masquer un brouillon contaminant, préserver les index/facettes et appliquer les contrats mécaniques.
- Le repair de contrat repart lui aussi des facettes typées. La raison et toutes les révisions lexicales précédentes sont systématiquement omises, pas seulement lorsqu'elles contiennent une ligne de placement. Un repair ne peut donc plus recopier une mauvaise formulation parce qu'elle figurait dans sa propre sortie antérieure.
- Les tests d'architecture interdisent maintenant la présence des requêtes, buts, outils et catégories provisoires dans les deux prompts. Une régression de pipeline prouve que `Petit-dejeuner Cuisine sources` n'est visible ni dans l'audit ni dans son repair, tandis que les quatre requêtes LLM réécrites restent transportées vers l'exécuteur.
- Validation : compilation WinUI et tests réussis; 122/122 tests d'architecture et budgets; 102/102 tests du pipeline; lot élargi SourceBacked/OpenAI/ApiClient 383/383.
- Prochaine preuve discriminante : Live78 doit conserver la grille 5 x 4, `scopeIds=[1,1,1,1]` ou une divergence explicitement justifiée, mais produire quatre requêtes de contenu réellement indépendantes du mauvais brouillon. Au moins une requête doit retrouver une page qui contient des noms de recettes/repas concrets; le juge doit ensuite sélectionner un ou plusieurs `E#` connus ou demander une relance véritablement nouvelle.

### 2026-07-22 - Soixante-dix-huitième live : retrieval nettement meilleur, mais jours encore recopiés depuis le focus puis revue de statut contradictoire

- Journaux processus `artifacts/live-test-process-live78-20260722-051055/`, progression client `artifacts/client-live-final-weekly-meal-plan-20260722-031100/progress.log`, trace principale `rag-20260722031135294-8ddc442e` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260722_051116.log`. Aucun `answers-readable.txt` n'est produit, car l'annulation globale survient avant le chemin terminal normal.
- Le test atteint exactement sa borne de trente minutes et se termine en `OperationCanceledException` pendant `EvidenceStatusReviewFormatRepair`. Aucun Writer, aucune réponse finale et aucun payload de sources ne sont atteints. L'absence de cartes dans ce run ne constitue donc pas un échec du mapping final : cette étape n'a jamais été appelée.
- Le routeur termine en 25,1 s avec `mode=strict`, le français et `allows_partial_answer=false`. Le Planner principal dure 199,4 s; la revue d'intake 173,9 s. Le premier résultat contient encore des listes d'ancres et un faux `Déjeuner` imbriqué, mais les micro-flux éliminent ce faux axe et reconstruisent exactement cinq lignes et quatre colonnes.
- La structure finale est correcte : `Lundi` à `Vendredi` croisés avec `Petit-déjeuner`, `Dîner`, `Souper`, `Collation`. Dans ce run, l'ancre de plage conservée est la question littérale complète plutôt que la citation minimale `lundi au vendredi`; elle reste verbatim et valide, mais elle augmente inutilement le contexte transmis aux étapes suivantes.
- La stratégie générale atteint encore son timeout de 150 s. Le plan compact termine en 66,7 s et recopie la période dans ses requêtes. Son repair row-independent dure 115,8 s et produit quatre formulations génériques mais correctement séparées : `repas petit-déjeuner semaine`, `repas déner semaine`, `repas souper semaine`, `repas collation semaine`.
- Le nouvel auteur aveugle est réellement indépendant du brouillon : son prompt de 4 230 caractères ne contient ni ces requêtes provisoires, ni les buts, ni les outils, ni `categoryPath`. En 103,1 s, le LLM remplace effectivement le vocabulaire générique par des termes de contenu, par exemple `Ingredients pour le petit-déjeuner...`.
- Le défaut résiduel vient d'une autre entrée du prompt : `QUESTION_FOCUS` contient encore `pour la semaine du lundi au vendredi`. Le LLM recopie cette plage dans chacune de ses quatre nouvelles requêtes. Le garde-fou row-independent les refuse correctement avant exécution. Live78 valide donc l'aveuglement au brouillon et isole une seconde source de contamination, le focus lui-même.
- Une relance de stratégie générale dure 138,5 s et régresse vers `sources for Lundi`; un patch de facettes dure 31,3 s; un nouveau repair row-independent dure 139,1 s et recopie encore les jours; le retry suivant dure 106,8 s et revient finalement aux quatre requêtes génériques `repas ... semaine` sous `Cuisine`.
- Deux dettes d'orchestration sont exposées par cette trajectoire. Premièrement, l'audit indépendant n'est pas rejoué après qu'un repair tardif a de nouveau modifié les requêtes. Deuxièmement, le micro-audit de portée n'est appelé que pour les portées nulles; comme la relance avait déjà remis `Cuisine`, cette portée non nulle n'est pas réévaluée par le dernier auditeur focalisé.
- Malgré ces régressions, le retrieval est matériellement meilleur que Live77. Les quatre recherches retournent respectivement 2, 11, 4 et 4 hits, soit 21 hits bruts sans timeout : `repas petit-déjeuner semaine` en 27,2 s, `repas déner semaine` en 20,5 s, `repas souper semaine` en 19,3 s et `repas collation semaine` en 19,1 s.
- Les ancres mécaniques sont immédiatement disponibles. Le petit-déjeuner et la collation retrouvent notamment `Cuisine/livre-recette-sist-2025-web.pdf` p.10, avec un passage décrivant la composition d'un repas. Le dîner retrouve `Cuisine/Je_cuisine_simplement.pdf` p.12, avec un passage lié à une recette et à son accompagnement. Le souper retrouve `Cuisine/facilitemps.pdf` p.18, passage plus orientatif évoquant des idées et des soupes-repas.
- L'`EvidenceJudge` termine en 56,2 s sans autoriser le Writer. L'`EvidenceStatusReview` dure 79,3 s et rend pourtant `action=clarify`, `useful=3`, `weak=0`, `missing=0`. Son repair de contrat dure 40,2 s et conserve la même contradiction : trois preuves utiles, aucun besoin manquant, mais une clarification et trois problèmes restants. Le format repair démarre puis est annulé après environ 62,8 s par le budget global.
- Bilan discriminant : l'auteur aveugle améliore réellement le vocabulaire quand il ne voit plus le mauvais brouillon, et les requêtes même imparfaites récupèrent déjà plusieurs pages plus prometteuses. Le run échoue pour deux raisons distinctes : contamination restante par les jours présents dans `QUESTION_FOCUS`, puis incohérence sémantique de la revue de statut en présence de preuves qu'elle classe elle-même comme utiles.

### 2026-07-22 - Focus de rédaction assaini mécaniquement depuis les lignes typées après Live78

- `PlannerRowIndependentQueryRepair` et `PlannerAcceptedQueryAudit` n'exposent plus directement le `QuestionFocus` brut. Ils utilisent un focus de rédaction borné dont les libellés de lignes de placement sont retirés avant de demander au LLM d'écrire les requêtes réutilisables.
- Les termes retirés ne proviennent d'aucune liste codée en dur. Le code lit uniquement les `RowLabels` de la structure que le LLM a déjà approuvée. Lorsqu'au moins deux labels existent, il retire d'abord la portion comprise entre la première occurrence du premier label et la dernière occurrence du dernier label, puis supprime mécaniquement les occurrences résiduelles de chaque label et compacte les espaces.
- Pour l'exemple générique `weekly operations controls from Monday to Friday`, le contexte devient `weekly operations controls from`. La fonction ignore la nature des valeurs : elle ne sait pas que `Monday` et `Friday` sont des jours, ne connaît aucun repas, aucune langue et aucun terme de recherche souhaitable.
- Le LLM reste l'unique auteur sémantique des requêtes. Le code ne propose ni ingrédient, ni recette, ni action opérationnelle; il empêche seulement qu'une dimension de placement déjà typée soit recyclée comme contenu de retrieval. Une balise explicite `QUESTION_FOCUS_PLACEMENT_ROWS_OMITTED: true` rend ce traitement observable dans les deux prompts.
- Les tests d'architecture vérifient séparément la ligne `QUESTION_FOCUS` afin de confirmer l'absence des labels retirés, tout en conservant légitimement `FORBIDDEN_ROW_LABELS` dans le repair : le LLM doit encore savoir quels termes précis ne pas générer. Cette distinction a corrigé une première assertion trop large sans affaiblir le contrat.
- Validation post-correctif : compilation WinUI réussie; 122/122 tests d'architecture; 102/102 tests du pipeline; 383/383 tests SourceBacked/OpenAI/ApiClient. Le changement reste générique et n'ajoute aucune heuristique alimentaire.
- Prochaine preuve discriminante : Live79 doit montrer un `QUESTION_FOCUS` sans plage de jours dans les deux micro-flux, faire rédiger par l'auditeur aveugle quatre requêtes de contenu sans ligne de placement, puis exécuter ces requêtes. Le gate suivant reste une décision cohérente du juge/statut qui sélectionne des `E#` connus et permet la relecture exacte des pages avant Writer.

### 2026-07-22 - Soixante-dix-neuvième live : focus assaini validé, fichier/page présents, puis arbitrage LLM contradictoire jusqu'au timeout

- Journaux processus `artifacts/live-test-process-live79-20260722-055449/`, progression client `artifacts/client-live-final-weekly-meal-plan-20260722-035458/progress.log`, trace principale `rag-20260722035543430-aa0ac629` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260722_055522.log`. Aucun `answers-readable.txt` n'est produit : le test atteint sa borne globale de trente minutes et lève `OperationCanceledException` pendant `EvidenceJudgeActionRepair`.
- Le routeur termine en 29,5 s avec `mode=strict`, le français et `allows_partial_answer=false`. Le Planner principal dure 200,6 s et la revue d'intake 259,0 s. Les micro-flux reconstituent finalement les cinq lignes `Lundi` à `Vendredi`, les quatre colonnes `Petit-déjeuner`, `Dîner`, `Souper`, `Collation`, et l'ancre canonique `lundi au vendredi`.
- La stratégie générale atteint encore son timeout de 150 s. Le plan compact réussit en 54,3 s avec quatre facettes et quatre portées nulles. L'audit indépendant dure 84,6 s et fournit ensuite quatre requêtes entièrement rédigées par le LLM : `Quels aliments peuvent être consommés au petit-déjeuner ?`, puis les variantes dîner, souper et collation.
- Le correctif de focus est donc validé en live : aucune de ces quatre requêtes ne contient `Lundi`, `Vendredi`, la semaine ou une autre ligne de placement. Le prompt n'a fourni ni vocabulaire alimentaire codé en dur, ni brouillon lexical, ni nom de portée; le LLM a choisi les termes de recherche.
- L'audit de portée dure 57,6 s puis son repair de contrat 26,5 s. Il rend `scopeIds=[1,1,1,1]`, tous résolus mécaniquement vers le chemin réel `Cuisine`. Aucun défaut codé ni heuristique métier ne choisit cette catégorie.
- Les quatre recherches retournent respectivement 12, 10, 10 et 12 hits en 29,7 s, 27,6 s, 28,3 s et 25,7 s, sans timeout. Elles convergent cependant toutes vers le même premier résultat : `Cuisine/si-on-cuisinait.pdf`, page 43. Le passage visible mentionne notamment le jambon, le lard et le mélange œufs-lait-crème, mais ne constitue pas un réservoir assez diversifié pour vingt repas.
- Live79 confirme à nouveau que l'ancre mécanique est présente avant l'arbitrage : `docPath`, `docName`, `pageStart=43` et `pageEnd=43` sont transportés dans les hits et le bundle. Le problème n'est ni de retrouver le fichier, ni de retrouver la page, ni de conserver ces champs.
- Le `EvidenceJudge` termine en 74,3 s sans ouvrir le Writer. La revue de statut dure 138,0 s et rend exactement la contradiction déjà vue dans Live78 : `action=clarify`, trois IDs utiles, zéro ID faible et zéro besoin manquant.
- Le premier repair de décision dure 56,0 s mais répète `clarify`, trois utiles et aucun manque; trois problèmes de contrat restent ouverts. Le format repair dure 36,8 s, mais produit une action vide, huit utiles et trois manques. Son second repair de décision dure 59,5 s et reste invalide.
- Le pipeline revient alors au repair de sélection du juge, qui dure 72,2 s, puis démarre un repair d'action. Celui-ci est annulé après 108,9 s par la borne globale à 06:24:58. Aucun Writer, aucune vérification finale et aucun mapping UI de cartes sources ne sont atteints.
- La chaîne d'affichage est désormais précisément établie : un hit possède déjà fichier/page, mais une carte visible n'est créée qu'après sélection explicite de son `EvidenceId` par le LLM, citation par le Writer et vérification de la réponse. Publier automatiquement tous les hits contournerait l'arbitrage sémantique et présenterait comme preuves des passages seulement voisins du sujet.
- Bilan discriminant : l'assainissement du focus, la portée LLM et le transport mécanique fichier/page sont validés. Le blocage dominant se situe après le retrieval : quatre requêtes trop générales convergent vers un passage peu diversifié, puis le petit LLM ne rend pas une décision sélection/action conforme malgré plusieurs réparations. La dette de performance est également critique : la première recherche ne commence qu'après 18 min 16 s.

### 2026-07-22 - Audit final systématique et contrat de statut cohérent après Live79

- L'audit aveugle `PlannerAcceptedQueryAudit` est maintenant exécuté pour tout plan structuré finalement accepté, y compris lorsqu'un repair focalisé tardif a produit les dernières requêtes. Les conditions qui permettaient à un plan réparé de contourner ce dernier audit ont été retirées.
- Deux régressions prouvent l'ordre des appels : l'audit aveugle intervient après le repair tardif, ne voit pas la requête provisoire réparée et transmet uniquement sa propre réécriture à l'exécuteur. Les outcomes observables distinguent les trajectoires `accepted_after_facet_query_review_query_audit` et `accepted_after_retry_facet_query_review_query_audit`.
- Le contrat `EvidenceStatusReview` exige maintenant que toute action qui n'écrit pas déclare explicitement au moins un `missingRequirement`. Si des IDs sont utiles et qu'aucun manque n'est déclaré, le LLM doit corriger sa décision vers `answer` ou `write_partial`; le code ne choisit ni l'action ni les preuves à sa place.
- Une nouvelle régression reproduit `clarify + useful E1 + missing vide`, expose le problème de contrat au repair LLM, obtient `answer + E1`, puis vérifie la sélection, le Writer et le résultat final. La mémoire reste uniquement du contexte et ne fournit aucun ID ou fait de secours.
- Le premier passage de la suite d'architecture a détecté un dépassement de budget de 67 caractères dans un prompt live-like de contexte 4K : 9 067 pour une limite de 9 000. La règle a été reformulée plus compactement sans en changer la sémantique; la compilation WinUI réussit ensuite sans avertissement ni erreur.
- Validation finale du palier : 3/3 régressions ciblées; 122/122 tests d'architecture et de budgets; 103/103 tests de `SourceBackedRagPipelineTests`; 384/384 tests SourceBacked/OpenAI/ApiClient.
- Prochain axe prioritaire : réduire le coût avant retrieval en faisant tenter plus tôt au LLM le plan compact de facettes lorsqu'une structure exhaustive à deux axes est déjà approuvée. Cette optimisation doit supprimer le timeout général de 150 s et les réparations redondantes sans transférer au code les choix de requête, de portée, de pertinence ou de suffisance.

### 2026-07-22 - Plan compact LLM prioritaire pour les grandes grilles strictes déjà auditées

- Après validation sémantique de l'intake, une grille stricte d'au moins huit cellules et de deux à quatre facettes tente désormais `PlannerCompactFacetPlan` avant `PlannerDiversityRepair` lorsque le plan initial ne porte encore aucune approbation de stratégie. Le seuil repose uniquement sur la structure typée par le LLM et le contrat `AllowsPartialAnswer`; il ne connaît aucun repas, jour, domaine ou vocabulaire de recherche.
- Le chemin prioritaire est réservé aux sorties complètes (`AllowsPartialAnswer=false`). Lorsqu'une réponse partielle est explicitement autorisée, le planificateur général conserve la priorité afin que le LLM puisse choisir une stratégie plus nuancée qu'un pool par facette.
- Le LLM compact reste l'auteur de `planDecision`, de la stratégie de couverture, des requêtes, de leurs buts, de l'outil et de la portée partagée éventuelle. Le code vérifie seulement la cardinalité, les facettes exactes, les outils autorisés, les chemins du catalogue et les contraintes de taille.
- Tout plan compact accepté passe encore par `PlannerAcceptedQueryAudit`, auteur aveugle indépendant. Si le plan compact ou son audit est refusé, le pipeline conserve intégralement `PlannerDiversityRepair`, ses patches focalisés et son retry comme repli; le changement d'ordre ne transforme donc jamais un refus LLM en acceptation mécanique.
- L'outcome `accepted_after_early_compact_facet_plan_query_audit` rend ce chemin observable. Deux scénarios stricts issus des réparations d'intake vérifient que le plan compact intervient immédiatement après l'adjudication, que l'audit indépendant est exécuté et qu'aucun `PlannerDiversityRepair` général n'est appelé.
- Les scénarios dont `AllowsPartialAnswer=true` ont d'abord révélé sept doubles LLM incompatibles avec le nouvel ordre. Le déclenchement strict a conservé cinq de ces trajectoires inchangées. Les deux trajectoires complètes ont été migrées vers le nouveau contrat; leurs portées restent nulles lorsque le test ne fournit aucun catalogue, afin de ne jamais inventer `Cuisine`.
- Validation : compilation WinUI réussie sans avertissement; 2/2 trajectoires strictes discriminantes; 103/103 tests de pipeline; 122/122 tests d'architecture et budgets; 384/384 tests SourceBacked/OpenAI/ApiClient.
- Gain attendu pour la trajectoire de Live79 : le timeout systématique de 150 s de `PlannerQueryStrategyRepair` disparaît avant le plan compact. Le gain réel et l'absence de nouvelle régression de séquencement doivent être mesurés en Live80; les coûts antérieurs du Planner et de l'adjudication d'intake restent une dette séparée.

### 2026-07-22 - Quatre-vingtième live : gain du plan compact confirmé, épinglage mécanique prouvé, mais requêtes et portées sémantiquement divergentes

- Journaux processus `artifacts/live-test-process-live80-20260722-065153/`, progression client `artifacts/client-live-final-weekly-meal-plan-20260722-045159/progress.log`, trace principale `rag-20260722045234870-12f34b92` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260722_065215.log`. Aucun `answers-readable.txt` n'est produit : le test atteint la borne globale de trente minutes pendant `EvidenceJudgeActionRepair`.
- Le routeur termine en 41,8 s. Le Planner principal dure 203,3 s et la revue d'intake 212,4 s. Cette dernière invente encore un `Déjeuner` et des ancres non verbatim; les contrats les refusent. L'anchor repair échoue en 128,3 s, puis les micro-adjudications de lignes et colonnes reconstruisent cinq lignes, quatre facettes et l'ancre exacte `lundi au vendredi`.
- Le nouveau chemin rapide est validé en conditions réelles : après l'intake accepté, le pipeline appelle directement `planner_compact_facet_plan` en 55,3 s, sans exécuter auparavant le grand `PlannerQueryStrategyRepair` borné à 150 s. La première recherche débute environ 15 min 49 s après le démarrage du pipeline, contre environ 18 min 16 s dans Live79, soit un gain mesuré d'environ 2 min 27 s.
- L'audit aveugle des requêtes dure 100,7 s. Ses formulations ne recopient plus les jours, mais restent trop générales ou dérivent entre facettes : une question sur les aliments du petit-déjeuner, une question de repas complet en soirée pour la deuxième facette, puis deux formulations proches de la collation. Une question large d'éligibilité peut être satisfaite par une phrase générique et ne vise pas un réservoir contenant assez de valeurs distinctes pour cinq lignes.
- L'audit de portée dure 35,8 s et son repair 14,7 s. Il retourne quatre IDs réels mais divergents : catégorie globale, `Cuisine`, `SOP - GMP - Quality` et `RH`. Le code respecte ce choix du LLM; il ne remplace pas automatiquement les portées par `Cuisine`.
- La recherche sans catégorie atteint son timeout de 35 s sans hit. La recherche sous `Cuisine` retourne douze hits en 30,6 s, avec `Cuisine/si-on-cuisinait.pdf` page 34 comme premier résultat. La recherche sous `SOP - GMP - Quality` atteint son timeout de 35 s sans hit. La recherche sous `RH` retourne onze hits en 25,2 s, avec `RH/PDF/APS_Employee_Handbook_2025.pdf` page 16 comme premier résultat.
- L'exemple RH constitue une preuve discriminante sur l'épinglage : le hit possède un chemin de fichier exact et une page exacte, mais son extrait parle d'un environnement de travail sûr et sain pour les employés. Il n'étaye pas une collation saine. Publier automatiquement toute ancre disponible produirait donc une citation mécaniquement précise mais sémantiquement fausse.
- Le Judge dure 77,6 s. La revue de statut dure 155,1 s et demande `read_documents` avec huit IDs utiles, aucun faible et quatre manques. Les repairs suivants oscillent entre zéro et huit preuves utiles, quatre puis un besoin manquant, sans satisfaire le contrat d'action. Après un repair de sélection de 73,9 s, le repair d'action est annulé par la borne globale. Aucun Writer, aucun vérificateur final et aucun mapper UI ne sont atteints.
- Bilan discriminant : Live80 confirme le gain du plan compact et démontre que `docPath`, `pageStart` et `pageEnd` sont déjà disponibles même pour un mauvais corpus. Le verrou n'est pas la capacité de fabriquer le lien, mais la cohérence sémantique de la requête, de la portée et de la sélection d'`EvidenceId` avant que le passage puisse devenir une source vérifiée de la réponse.

### 2026-07-22 - Cible de candidats distincts et revue LLM de cohérence des portées après Live80

- `PlannerAcceptedQueryAudit` reçoit maintenant, pour chaque facette réutilisée sur plusieurs lignes de placement, le nombre de valeurs distinctes nécessaires. Son contrat demande au LLM de viser des passages contenant plusieurs candidats nommés et d'éviter les questions générales d'éligibilité ou de conseil qu'une seule affirmation générique pourrait satisfaire.
- Ce nombre est dérivé mécaniquement de la grille typée déjà approuvée. Le code ne propose aucun candidat, aliment, recette, terme de requête ou corpus. Le LLM reste seul auteur du vocabulaire et seul juge de la qualité sémantique des requêtes.
- Une seconde revue LLM focalisée `PlannerAcceptedScopeCoherenceReview` intervient lorsque plusieurs facettes homologues d'une même tâche reçoivent des chemins de catégorie différents. Elle voit la facette exacte, la portée proposée et l'objectif commun; la requête n'est pas considérée comme autorité car elle peut elle-même avoir dérivé.
- Cette revue n'impose pas un corpus unique. Le LLM peut confirmer une divergence réellement justifiée ou choisir de nouvelles portées parmi les IDs réels du catalogue. Le code se limite à détecter la divergence observable, sérialiser les choix, valider les IDs et appliquer mécaniquement la décision LLM.
- Une régression générique construit quatre facettes réparties entre trois corpus, fait réévaluer leur cohérence par le LLM puis vérifie que son choix commun est appliqué aux quatre recherches. Le premier échec du test provenait uniquement d'une fixture dont le chemin complet de catégorie avait été placé dans `DisplayName`; la donnée a été corrigée dans le bon champ `CategoryPath`.
- Validation finale : compilation réussie sans avertissement ni erreur; 1/1 régression focalisée; 122/122 tests d'architecture et budgets; 104/104 tests de `SourceBackedRagPipelineTests`; 385/385 tests SourceBacked/OpenAI/ApiClient.
- Prochaine preuve discriminante : Live81 doit conserver le gain du plan compact, produire quatre requêtes visant chacune plusieurs candidats concrets, faire réexaminer toute divergence de portée par le micro-audit LLM, puis sélectionner des `E#` réellement pertinents. Le succès reste une table 5 x 4 écrite, vérifiée et affichée avec des cartes de sources fichier/page; un seul live ne suffira pas à prouver la reproductibilité.

### 2026-07-22 - Quatre-vingt-unième live : arrêt sûr avant retrieval après destruction tardive d'un axe LLM correct

- Artefact final `artifacts/client-live-final-weekly-meal-plan-20260722-053600/answers-readable.txt`, progression `artifacts/client-live-final-weekly-meal-plan-20260722-053600/progress.log`, journaux processus `artifacts/live-test-process-live81-20260722-073554/`, trace principale `rag-20260722053638908-12b776ca`, trace SourceBacked `sbrag-ad6ef7458685466b81663ab875550a3` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260722_073616.log`.
- Le pipeline termine proprement après 17 min 55,8 s; le test complet échoue après 18 min 34 s sur le payload de sources nul. La réponse de sécurité contient 293 caractères, aucun outil n'est exécuté et aucune carte source n'est exposée. Le runtime LLM géré est arrêté automatiquement.
- Le routeur termine en 39,5 s. Le Planner principal dure 192,7 s. La revue d'intake dure 239,7 s et produit une ancre inventée `Jeu de la semaine du lundi au vendredi` ainsi qu'un faux `Déjeuner`; les contrats mécaniques refusent ces valeurs.
- Le premier repair d'ancre dure 90,1 s et reste invalide. L'adjudication de lignes dure 114,5 s mais prend les repas pour les lignes. Le repair de conflit de rôle dure 71,9 s et demande à tort une clarification; son retry de 55,5 s rend enfin la bonne décision sémantique : `Lundi`, `Mardi`, `Mercredi`, `Jeudi`, `Vendredi` avec `rowHeaderLabel=Jour de la semaine`.
- Ce retry correct contient seulement une citation non verbatim : il écrit `fasse` au lieu de `fasses` et ajoute une ponctuation absente. La faute restante est donc strictement mécanique et ne concerne plus l'appartenance à l'axe.
- Le flux pré-correctif rejette pourtant toute la décision, puis lance `PlannerIntakeRowAxisCandidateSelectionPatch` sur les trois anciennes opinions contradictoires. En 68,2 s, ce nouvel appel remplace les cinq jours corrects par `Petit-dejeuner`, `Déjeuner`, `Dîner`, `Souper`, `Collation`.
- L'adjudication des colonnes détecte ensuite que ces mêmes valeurs sont déjà verrouillées comme lignes. Son repair et son retry ne récupèrent pas la structure. L'ultime repair d'ancre renvoie un objet sans les champs de contrat attendus. Le pipeline choisit correctement `need_more_evidence` avant tout outil, plutôt que d'exécuter une grille incohérente.
- Live81 n'invalide pas les deux corrections post-Live80 : ni `PlannerAcceptedQueryAudit` enrichi, ni `PlannerAcceptedScopeCoherenceReview` ne sont atteints. Il révèle un verrou antérieur distinct : une bonne décision sémantique ne doit pas être jetée parce qu'un champ de citation mécanique reste fautif.

### 2026-07-22 - Verrouillage de l'axe sémantique après réparation de conflit de rôle

- Après `PlannerIntakeRowAxisRoleConflictRepair` ou son retry, si les labels et leur rôle ne présentent plus de conflit et que les seuls problèmes restants sont des métadonnées réparables, le pipeline conserve désormais la décision LLM comme autoritative.
- L'appel suivant est le micro-patch LLM `PlannerIntakeRowAxisMetadataPatch`. Il ne peut modifier que les champs explicitement listés, par exemple `quote`; les labels ordonnés sont présentés sous `AUTHORITATIVE_ORDERED_LABELS` et ne peuvent pas être réécrits.
- Le code ne déduit aucun jour et ne corrige aucune sémantique à la place du modèle. Il séquence seulement deux responsabilités déjà décidées par le LLM : appartenance de l'axe d'abord, conformité mécanique de la citation ensuite.
- La régression issue de Live81 fait choisir au retry `Lundi` à `Vendredi` avec la même faute verbatim que le live, puis fournit `quote=lundi au vendredi` au patch. Elle vérifie que les cinq labels restent immuables, que le sélecteur de candidats contradictoires n'est pas rappelé et que le pipeline poursuit jusqu'aux colonnes et aux recherches simulées.
- Validation : 1/1 régression Live81; 122/122 tests d'architecture et budgets; 104/104 tests de pipeline; 385/385 tests SourceBacked/OpenAI/ApiClient.
- Prochaine preuve discriminante : Live82 doit franchir l'intake en conservant toute décision d'axe sémantiquement correcte, puis atteindre réellement le plan compact, l'audit de requêtes avec cible de candidats distincts et la revue de cohérence des portées. Le planning professionnel 5 x 4 avec sources vérifiées reste non validé.

### 2026-07-22 - Quatre-vingt-deuxième live : structure 5 x 4 conservée, mais deux JSON de cohérence invalides épuisent le chemin avant retrieval

- Journaux processus `artifacts/live-test-process-live82-20260722-080459/`, artefact lisible `artifacts/client-live-final-weekly-meal-plan-20260722-060504/answers-readable.txt`, progression `artifacts/client-live-final-weekly-meal-plan-20260722-060504/progress.log`, trace principale `rag-20260722060538974-ac55ac61`, trace SourceBacked `sbrag-ab5dd9b3af954e5d94f871788a3ccf58` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260722_080520.log`.
- Le pipeline termine proprement après 1 401,6 s, soit 23 min 21,6 s; le test complet échoue après 23 min 56 s parce que le payload de sources est nul. La réponse sûre contient 298 caractères, aucun outil n'est exécuté, aucune requête RAG n'est lancée et aucune carte source n'est exposée. Le runtime LLM géré est arrêté automatiquement.
- Le routeur termine en 45,0 s et le Planner principal en 161,8 s. La revue d'intake dure 172,4 s et comprend déjà sémantiquement les cinq jours ainsi que les quatre repas, mais ses ancres de groupe ne sont pas verbatim. L'anchor repair de 92,8 s ne résout pas les jours intermédiaires.
- L'adjudication de lignes dure 51,1 s et conserve correctement `Lundi`, `Mardi`, `Mercredi`, `Jeudi`, `Vendredi`; seule la relation de plage manque. Le nouveau `PlannerIntakeRowAxisMetadataPatch` termine en 5,5 s, choisit `range_expansion`, résout mécaniquement la citation exacte `lundi au vendredi` et préserve les cinq labels. Live82 valide donc directement le correctif issu de Live81.
- L'adjudication de colonnes dure 31,1 s et choisit correctement `Petit-déjeuner`, `Dîner`, `Souper`, `Collation`. Son patch d'ancre dure 39,0 s puis l'intake 5 x 4 est accepté. Aucune décision de jour ou de repas n'est fabriquée par le code.
- Le plan compact prioritaire termine en 37,2 s. Le premier audit indépendant des requêtes dure 93,7 s, mais accepte quatre questions générales du type `Quels aliments peuvent être consommés au petit-déjeuner ?`, déclinées pour chaque facette. L'instruction demandant un passage comportant au moins cinq valeurs nommées distinctes n'a donc pas suffi à modifier le jugement live du petit modèle.
- Le premier audit de portée dure 49,3 s, puis son repair de contrat 26,4 s. Il propose les IDs divergents `[1,2,3,28]`. La nouvelle `PlannerAcceptedScopeCoherenceReview` se déclenche bien en 43,0 s et empêche l'exécution immédiate de cette cartographie, mais sa réponse de 437 caractères n'est pas un objet JSON complet.
- Faute de repair focalisé sur ce nouveau micro-contrat, le flux pré-correctif rejette le plan entier et appelle `PlannerQueryStrategyRepair`, qui consomme sa borne complète de 150 s sans sérialiser de plan exécutable. Un second plan compact dure 53,2 s.
- Le second audit de requêtes dure 87,0 s et reproduit le même défaut sémantique avec `Quels aliments peuvent être servis au petit-déjeuner ?`, puis les trois variantes homologues. Le second audit de portée et son repair durent 35,0 s et 36,5 s, avec une nouvelle divergence `[0,1,2,3]`.
- La seconde revue de cohérence se déclenche en 38,0 s mais rend à nouveau un JSON invalide, cette fois de 298 caractères. `PlannerQueryStrategyRetry` consomme alors à son tour les 150 s complètes. Le pipeline retourne une insuffisance sûre : le verrou n'est pas l'épinglage fichier/page, car aucun hit n'a été demandé; c'est la perte d'une décision de portée réparable avant l'exécution.
- Bilan discriminant : la structure 5 x 4 et le micro-patch de métadonnées sont validés en live; le plan compact reste plus rapide; la détection de portées divergentes fonctionne. Deux défauts distincts restent ouverts : le contrat JSON de la revue de cohérence doit être réparé localement, et l'auteur/auditeur de requêtes s'auto-approuve encore sur une forme de question qui ne promet pas plusieurs valeurs complètes distinctes.

### 2026-07-22 - Repair LLM focalisé de la cohérence des portées après Live82

- Une `PlannerAcceptedScopeCoherenceReview` invalide ne renvoie plus vers `PlannerDiversityRepair` ou son retry. Le pipeline appelle désormais `PlannerAcceptedScopeCoherenceReviewContractRepair` en conservant l'intake typé, les facettes exactes, les IDs réels du catalogue et la cartographie divergente initialement proposée.
- Le repair ne reçoit jamais le texte brut malformé. Il voit uniquement les codes de rejet mécaniques bornés et doit rendre `decision`, `reason` et un `scopeIds` complet. Il peut confirmer une divergence réellement sémantique ou choisir une portée commune; le code ne décide d'aucune catégorie et applique seulement les IDs LLM validés.
- La revue initiale et son repair sont maintenant bornés à 256 tokens. Ce budget couvre largement un objet de quatre IDs tout en empêchant une petite décision sans fin de monopoliser les 150 s du transport local.
- La régression Live82 injecte un objet de cohérence tronqué après une proposition `[0,1,2,3]`. Elle vérifie que le repair reçoit le code `invalid_accepted_scope_coherence_review_json`, les chemins divergents dont `RH`, qu'il ne reçoit pas le texte brut fautif, puis que son choix `[1,1,1,1]` est appliqué aux quatre requêtes sans aucun `PlannerDiversityRetry`.
- Validation : 1/1 régression Live82; 2/2 contrôles ciblés de prompt et de budget; 122/122 tests d'architecture et budgets; 105/105 tests de pipeline; 386/386 tests SourceBacked/OpenAI/ApiClient.
- Prochaine correction discriminante : séparer l'écriture d'une requête de son jugement de rendement attendu. Une micro-revue LLM indépendante doit décider explicitement si chaque requête est susceptible de ramener, dans un même passage exploitable, assez de valeurs nommées complètes et distinctes pour les lignes à remplir; si elle refuse, un auteur LLM focalisé doit réécrire la requête. Le code ne doit ni reconnaître `Quels aliments`, ni injecter de vocabulaire culinaire, ni choisir les valeurs.

### 2026-07-22 - Critique LLM indépendant du rendement sémantique des requêtes après Live82

- L'auteur aveugle `PlannerAcceptedQueryAudit` n'est plus autorisé à auto-approuver définitivement ses propres formulations. Tout plan structuré par facettes qu'il accepte passe ensuite par `PlannerAcceptedQueryYieldReview`, un second rôle LLM qui voit les requêtes finales mais ne peut ni les réécrire ni répondre à l'utilisateur.
- Le critique reçoit la facette exacte, la requête, son but et le nombre de lignes de placement déjà approuvé par le LLM. Il doit juger la forme probable du passage obtenu : une requête de pool réutilisable doit viser assez de valeurs nommées, complètes et distinctes pour remplir séparément les lignes, pas seulement plusieurs hits, une classe générale, un conseil, une condition d'éligibilité ou une unique valeur.
- Cette décision reste sémantique et appartient exclusivement au LLM. Le code ne recherche aucune expression comme `Quels aliments`, ne classe aucun type de repas et ne dispose d'aucun vocabulaire métier. Il valide seulement `reviewDecision=accept|clarify`, une raison non vide et `requests={}`, puis respecte le refus éventuel.
- Si le critique refuse, `PlannerAcceptedQueryYieldRepair` réécrit les quatre requêtes depuis les facettes typées et le focus assaini. Les requêtes rejetées et le texte du critique sont volontairement omis afin d'éviter tout ancrage lexical; le repair sait seulement qu'il doit viser plusieurs valeurs complètes dans un même passage utile.
- Le repair ne peut pas s'auto-approuver à son tour : sa proposition passe par `PlannerAcceptedQueryYieldRetryReview`, qui revoit les requêtes effectivement réparées. Un second refus empêche l'exécution et rend la décision au repli LLM général; une approbation permet seulement de poursuivre vers l'audit de portée.
- Les JSON invalides du critique disposent de leur propre `PlannerAcceptedQueryYieldReviewContractRepair`. Les sorties du critique sont bornées à 256 tokens; l'auteur et son repair de contrat disposent de 480 tokens. Le code conserve donc des contrats rapides et typés sans raccourcir le jugement sémantique.
- La régression dérivée de Live82 fait d'abord accepter par l'auteur les quatre variantes de `Quels aliments peuvent être servis ... ?`. Le critique les refuse explicitement car une affirmation générique peut satisfaire chaque formulation. Le repair aveugle ne reçoit ni ces textes ni le motif lexical, produit quatre nouvelles requêtes, le retry les accepte, puis l'exécuteur reçoit uniquement les requêtes réparées sans `PlannerDiversityRetry`.
- Les 19 scénarios structurés existants fournissent désormais explicitement l'avis du critique. Les premiers tests complets ont révélé seize compteurs d'appels décalés d'une unité et deux files écrites en ligne sans réponse de critique; les fixtures ont été réalignées sans modifier leurs décisions ou leurs résultats métier.
- Validation finale : 1/1 régression de refus/réécriture/réapprobation; 122/122 tests d'architecture; budget du critique et du repair vérifié; 106/106 tests de pipeline; 387/387 tests SourceBacked/OpenAI/ApiClient; `git diff --check` propre hors avertissements de fins de ligne hérités.
- Prochaine preuve discriminante : Live83 doit conserver l'intake 5 x 4, faire refuser ou accepter explicitement par le critique la forme des requêtes, réparer localement tout JSON de cohérence de portée puis atteindre le retrieval avec des catégories cohérentes. Le succès final exige encore une sélection LLM de preuves légitimes, un Writer vérifié et des cartes source fichier/page; aucune de ces étapes n'est déclarée acquise avant le live.

### 2026-07-22 - Quatre-vingt-troisième live : grille correcte et requêtes finalement assainies, mais verdicts du critique tronqués avant retrieval

- Journaux processus `artifacts/live-test-process-live83-20260722-090251/`, dossier client terminal vide `artifacts/client-live-final-weekly-meal-plan-20260722-070259/`, trace principale `rag-20260722070345160-79917ab3` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260722_090320.log`. Le dossier client ne contient pas d'artefact de réponse, car l'annulation globale survient avant le chemin terminal normal.
- Le test atteint sa borne exacte de trente minutes et lève `OperationCanceledException` pendant l'ultime audit des requêtes. Aucun outil RAG, aucun hit, aucun `EvidenceBundle` peuplé, aucun Writer et aucune carte source ne sont atteints. Le serveur et le `testhost` sont arrêtés proprement; aucune réponse insuffisamment prouvée n'est exposée.
- Le routeur termine en 41,5 s et le Planner principal en 143,7 s. La revue d'intake dure 104,2 s mais reconstruit des ancres fautives et ajoute un faux `Déjeuner`. L'anchor repair de 62,1 s reste invalide.
- L'adjudication de lignes termine en 59,6 s avec les cinq jours corrects mais sans relation. Le patch de métadonnées dure 6,0 s, résout l'ancre exacte `lundi au vendredi` et préserve `Lundi`, `Mardi`, `Mercredi`, `Jeudi`, `Vendredi`. L'adjudication de colonnes choisit d'abord cinq valeurs, dont le faux `Déjeuner`; après un patch d'ancre, le patch de sélection retire uniquement cette valeur et accepte `Petit-déjeuner`, `Dîner`, `Souper`, `Collation`.
- Le premier plan compact termine en 56,4 s. Son audit accepte encore quatre requêtes génériques commençant par `Quels aliments peuvent être consommés...`. Le nouveau `PlannerAcceptedQueryYieldReview` se déclenche bien, mais dure 137,8 s et produit 957 caractères, tronqués exactement à la borne de 256 tokens. Son repair de contrat dure 89,0 s, produit 1 101 caractères et est lui aussi tronqué. Le repli stratégique général expire ensuite à 150 s.
- Le second plan compact termine en 57,5 s. Son audit retourne les variantes `Quels aliments peuvent être servis...`. Le critique dure 117,9 s et son repair 92,0 s; leurs réponses de 973 et 1 077 caractères sont de nouveau tronquées. Le retry stratégique général finit par produire quatre requêtes contenant `Lundi`, correctement refusées par les contrats de placement.
- Le patch d'affectation de facettes restaure les quatre facettes, puis le repair row-independent termine en 100,9 s avec quatre requêtes sans jour : `Plats de petit-déjeuner populaires`, `Recettes de dîner faciles et rapides`, `Plats de souper originaux et innovants`, `Recettes de collation saines et gourmandes`. L'audit final de ces requêtes démarre, mais la borne globale l'annule après environ 73 s, avant toute recherche.
- Bilan discriminant : la structure 5 x 4, la résolution mécanique de l'ancre et l'élimination du faux `Déjeuner` fonctionnent en live. Le critique indépendant est bien appelé, mais son ancien contrat trop bavard consomme toute sa fenêtre de sortie avant de sérialiser un JSON complet. Live83 n'évalue donc ni la cohérence de portée réparée après Live82, ni le retrieval, ni le Judge, ni le Writer.

### 2026-07-22 - Verdict JSON atomique et mode JSON natif après Live83

- Le contrat du critique de rendement ne demande plus `reviewDecision`, `reason` et `requests`. Il est volontairement réduit à un verdict sémantique atomique, soit `{"decision":"accept"}`, soit `{"decision":"revise"}`. Le modèle reste seul juge du rendement probable de la requête; le code ne connaît ni les repas, ni les formulations à privilégier, ni la catégorie documentaire.
- Le nouveau contrat typé `SourceBackedQueryYieldVerdict` et son parseur dédié refusent toute décision différente de `accept|revise`. Le repair de format utilise le même objet minimal. Si le verdict est `revise`, l'auteur LLM aveugle réécrit toujours les requêtes depuis les facettes typées et le focus assaini; le critique ne rédige aucune requête à sa place.
- Les sorties brutes bornées du critique sont désormais tracées avant validation, ce qui permettra au prochain live de distinguer un rejet sémantique, une erreur de transport et une erreur de contrat sans exposer cette sortie à l'auteur de la réparation.
- `RagChatAgent` active maintenant le `response_format=json_object` natif de l'adaptateur OpenAI-compatible pour `PlannerAcceptedQueryYieldReview`, son repair, son retry, ainsi que pour `PlannerAcceptedScopeCoherenceReview` et son repair. `OpenAiLlmClient` conserve son repli prompt-only si un endpoint refuse explicitement ce paramètre en HTTP 400 ou 422.
- La régression Live82/83 injecte d'abord une réponse de critique volontairement tronquée, fait réparer son contrat en `{"decision":"revise"}`, vérifie la réécriture aveugle, puis accepte la seconde proposition avec `{"decision":"accept"}`. Les tests d'adaptateur vérifient aussi que le mode JSON reste limité aux micro-étapes explicitement autorisées.
- Validation ciblée initiale : compilation réussie et 3/3 tests focalisés passent.
- Validation complète de reprise : 122/122 tests d'architecture et budgets, 106/106 tests du pipeline, puis 387/387 tests SourceBacked/OpenAI/ApiClient avec le filtre historique exact. Une première commande trop précise avait compté 386 tests tout en restant verte; la relance avec `FullyQualifiedName~SourceBacked|FullyQualifiedName~OpenAi|FullyQualifiedName~ApiClient` a réintégré le test transversal manquant et confirmé les 387 cas attendus. `git diff --check` reste propre hors avertissements de fins de ligne hérités.
- Prochaine preuve discriminante : Live84 doit rendre les deux verdicts atomiques sans troncature, conserver quatre requêtes visant réellement des pools de valeurs distinctes, obtenir une portée LLM cohérente puis commencer le retrieval assez tôt pour laisser un budget utile au Judge et au Writer.

### 2026-07-22 - Quatre-vingt-quatrième live : JSON atomique validé et retrieval atteint, mais deux critiques LLM approuvent une stratégie sémantiquement illégitime

- Journaux processus `artifacts/live-test-process-live84-20260722-103405/`, progression client `artifacts/client-live-final-weekly-meal-plan-20260722-083412/progress.log`, trace principale `rag-20260722083453715-be8da2fb` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260722_103430.log`. Aucun `answers-readable.txt` n'est créé, car l'annulation globale intervient avant le chemin terminal normal.
- Le test atteint sa borne exacte de trente minutes et lève `OperationCanceledException` pendant `EvidenceStatusReviewFormatRepair`. Aucun Writer, aucune réponse finale, aucun payload de sources et aucune carte UI ne sont atteints. Le `testhost` et le serveur LLM géré sont arrêtés proprement.
- Le routeur dure 83,9 s et choisit correctement `rag.multi_search`, le français, le mode strict et `allows_partial_answer=false`. Le Planner principal dure 207,3 s, nettement plus que les 143,7 s de Live83, puis la revue d'intake dure 200,9 s.
- La revue d'intake retrouve les cinq jours mais ajoute encore un faux `Déjeuner` et fournit des ancres non verbatim. Son repair d'ancre dure 111,9 s et reste invalide. L'adjudication de lignes dure 67,2 s, choisit correctement `Lundi` à `Vendredi`, puis le micro-patch de métadonnées termine en 6,7 s avec `relation=range_expansion` et l'ancre exacte `lundi au vendredi`.
- L'adjudication de colonnes dure 35,3 s et propose cinq valeurs, dont le faux `Déjeuner`. Son patch d'ancres dure 26,4 s et expose le chevauchement entre `Petit-déjeuner` et `Déjeuner`. Le patch de sélection termine en 6,7 s, retire uniquement la valeur indue et conserve `Petit-déjeuner`, `Dîner`, `Souper`, `Collation`. La grille exacte 5 x 4 est acceptée après environ 12 min 30.
- Le plan compact dure 58,4 s et propose quatre recherches contenant encore des noms de corpus. L'audit aveugle dure 132,4 s et les remplace par quatre questions génériques de la forme `Quels aliments peuvent être servis pour ... ?`.
- Le nouveau verdict atomique est directement validé en live : `PlannerAcceptedQueryYieldReview` termine en seulement 5,9 s, retourne exactement `{"decision":"accept"}` et est parseable sans repair ni troncature. Le défaut Live83 de transport est donc résolu. Sa décision sémantique reste toutefois fausse : ces quatre questions peuvent être satisfaites par une phrase générale et ne promettent pas cinq valeurs complètes distinctes.
- L'audit de portée dure 66,7 s et son repair 85,4 s. Il propose les IDs divergents `[0,1,4,28]`. La revue de cohérence sous `response_format=json_object` dure 93,8 s, mais son objet reste contractuellement invalide; le repair spécialisé dure 65,7 s et devient parseable. Le critique confirme alors à tort les mêmes IDs, résolus vers portée globale, `Cuisine`, `Assurance - CG - Police` et `RH`.
- Le code respecte cette décision LLM et exécute les quatre recherches. Elles terminent toutes sans erreur avec respectivement 12, 10, 12 et 12 hits. Le premier hit global est une norme d'hygiène alimentaire, le premier hit `Cuisine` est `Cuisine/si-on-cuisinait.pdf` page 43, le premier hit Assurance est une police de responsabilité civile page 10 et le premier hit RH est un manuel d'employé page 45.
- Live84 confirme donc une nouvelle fois que l'épinglage mécanique fonctionne : `docId`, `docPath`, `pageStart`, `pageEnd` et l'extrait exact sont présents même pour les trois corpus illégitimes. Les publier automatiquement fabriquerait des cartes précises mais sémantiquement fausses.
- L'Evidence Judge dure 96,2 s puis ouvre la revue de statut. Cette revue atteint son timeout de 180 s; sa sortie tardive est néanmoins prudente avec `action=stop_insufficient_database`, zéro preuve utile et un manque. Le repair de décision dure 30,2 s mais dégrade l'action en valeur vide. Le format-repair suivant est annulé après 140,5 s par la borne globale.
- Bilan discriminant : le transport JSON atomique est validé et le pipeline atteint le retrieval avec environ neuf minutes de marge, mais les deux gardiens sémantiques approuvent encore la mauvaise stratégie. Le prochain correctif doit renforcer leurs rôles et leur indépendance sans coder de catégorie, de repas ou de formulation métier; la bonne décision tardive du statut doit également pouvoir être conservée quand seuls des champs mécaniques restent fautifs.

### 2026-07-22 - Verdict positionnel de rendement et cartographie de portée aveugle après Live84

- Le verdict global du critique de rendement est remplacé par un contrat positionnel compact : `decisions` contient exactement une valeur `accept|revise` par requête, dans l'ordre. Le LLM doit ainsi juger chaque facette indépendamment; il ne peut plus approuver implicitement les quatre requêtes parce qu'une seule lui paraît plausible.
- Le code ne juge toujours pas le sens des requêtes. Il vérifie seulement la cardinalité du tableau, les deux valeurs autorisées et l'ordre. Si une seule position vaut `revise`, l'auteur LLM aveugle réécrit l'ensemble des requêtes depuis les facettes typées et le focus assaini, puis un nouveau verdict positionnel revoit les formulations réellement réparées.
- Le contrat reste court et compatible avec `response_format=json_object`. Il ne demande ni raison, ni réécriture, ni vocabulaire. Les sorties brutes bornées conservent le tableau de décisions afin que le prochain live montre précisément quelle facette a été refusée.
- La revue finale de cohérence des portées est désormais aveugle à la cartographie divergente précédente et aux requêtes provisoires. Elle reçoit uniquement le focus parent assaini, le type de tâche, les facettes exactes, les chemins réels du catalogue et les positions à remplir. Elle doit reconstruire une cartographie complète depuis zéro.
- Cette séparation supprime le biais d'ancrage observé dans Live84, où le repair avait simplement recopié `[0,1,4,28]`. Le prompt précise que les facettes typées d'une même tâche partagent normalement une famille documentaire, tout en laissant le LLM préserver une divergence réellement exigée par la question ou la sémantique des facettes. Le code n'impose aucune catégorie commune et ne connaît pas `Cuisine`, `Assurance` ou `RH`.
- Le repair de contrat de cette revue reste aveugle lui aussi : il ne reçoit ni la sortie brute malformée, ni les IDs proposés, ni les requêtes. Une balise observable `PROVISIONAL_QUERIES_AND_SCOPE_PROPOSAL_OMITTED: true` protège cette frontière dans les tests.
- Les sorties brutes initiale et réparée de la cohérence des portées sont maintenant journalisées sous `SBRAG_PLANNER_ACCEPTED_SCOPE_COHERENCE_REVIEW_RAW`, avec le bornage existant de trace. Le prochain live pourra donc distinguer une erreur JSON, une erreur de schéma et une décision sémantique fautive.
- La régression de rendement fait échouer le premier objet de critique, le répare avec une seule position `revise` et trois `accept`, vérifie que cette seule opposition déclenche la réécriture aveugle, puis exige quatre `accept` sur la proposition réparée. Les régressions de portée vérifient que ni `proposedScopeId`, ni le mapping divergent, ni les requêtes initiales ne sont visibles par le reviewer ou son repair.
- Validation : 4/4 régressions Live84; 122/122 tests d'architecture et budgets; 106/106 tests de pipeline; 387/387 tests SourceBacked/OpenAI/ApiClient. La compilation est réussie et `git diff --check` doit rester la dernière vérification mécanique avant Live85.
- Prochaine preuve discriminante : Live85 doit faire apparaître quatre décisions positionnelles rapides. Si les questions génériques sont refusées, l'auteur aveugle doit produire des requêtes de pool plus discriminantes. Si la portée initiale diverge, la revue aveugle doit choisir une cartographie fraîche depuis le catalogue sans recopier la proposition; le retrieval ne doit plus interroger des corpus sans rapport uniquement parce qu'ils possèdent un ID valide.

### 2026-07-22 - Quatre-vingt-cinquième live : grille plus rapide, critique positionnel toujours permissif et audit de portée incomplet avant la revue aveugle

- Journaux processus `artifacts/live-test-process-live85-20260722-112623/`, artefact lisible `artifacts/client-live-final-weekly-meal-plan-20260722-092628/answers-readable.txt`, progression dans le même dossier, trace principale `rag-20260722092706954-1ec21e44`, trace SourceBacked `sbrag-e437cb776af04b0fa01b8a7c824f7f70` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260722_112645.log`.
- Le pipeline terminal dure environ 22 min 30 et le test complet 23 min 08. Il retourne l'insuffisance sûre de 298 caractères avec zéro outil, zéro hit et zéro source; le test produit échoue sur le payload nul. Le runtime LLM est arrêté automatiquement et aucun processus ne reste actif.
- Le routeur termine en 34,1 s, contre 83,9 s dans Live84. Il conserve le français, `rag.multi_search`, le mode strict et `allows_partial_answer=false`. Le Planner principal dure 164,9 s, soit environ 42 s de moins que Live84.
- La revue d'intake dure 175,8 s et choisit directement les cinq jours et les quatre colonnes exactes, sans faux `Déjeuner`. Seules ses ancres concaténées sont invalides. L'anchor repair dure 29,4 s et demande à tort une clarification.
- L'adjudication de lignes dure 60,2 s et accepte `Lundi` à `Vendredi` avec une relation valide. L'adjudication de colonnes dure 30,4 s, conserve `Petit-déjeuner`, `Dîner`, `Souper`, `Collation`; son patch d'ancres dure 34,6 s. La grille 5 x 4 est acceptée après environ 9 min 32, près de trois minutes plus tôt que Live84.
- Le premier plan compact dure 39,9 s. L'audit aveugle dure 142,4 s et produit quatre requêtes `Produits alimentaires pour <facette>`. Le nouveau critique positionnel termine en 17,8 s avec `{"decisions":["accept","accept","accept","accept"]}`. Le contrat positionnel est donc valide en live, mais le même petit modèle approuve encore quatre formulations qui ne demandent pas explicitement plusieurs valeurs complètes et distinctes.
- L'audit de portée initial dure 31,7 s et rend un contrat invalide. Son repair dure 55,2 s, mais produit seulement un `scopeId` au lieu des quatre positions requises. Le vérificateur mécanique refuse correctement `accepted_scope_audit_count_mismatch: expected=4; actual=1`. La nouvelle revue de cohérence aveugle n'est jamais atteinte; Live85 ne la valide ni ne l'invalide.
- Le flux pré-correctif retourne alors au repair stratégique général, qui expire à 150 s sans plan. Un second plan compact dure 77,9 s, mais recopie `lundi au vendredi` dans les quatre requêtes. Le garde-fou générique refuse ces lignes typées et lance le repair row-independent, qui expire à son tour après 150 s. Le retry stratégique général consomme encore 150 s sans contrat.
- Le pipeline choisit `need_more_evidence` avant tout retrieval et explique que le Planner n'a pas produit de requêtes exécutables après deux tentatives. Aucun corpus incomplet ou contaminé n'est donc interrogé.
- Bilan discriminant : la structure s'améliore et les nouveaux formats sont robustes, mais changer un verdict global en quatre verdicts n'a pas modifié le jugement du modèle. Le prochain critique doit être cadré comme recherche adversariale de contre-exemples et retourner les index insuffisants, sans règle lexicale dans le code. L'audit de portée initial doit activer le JSON natif, tracer ses sorties et rendre explicite qu'un scope commun doit être répété à chaque position.

### 2026-07-22 - Procureur adversarial du rendement et retry cardinal de portée après Live85

- Le critique de rendement n'émet plus un `accept|revise` par position. Live85 a prouvé que cette formulation l'incitait encore à approuver quatre requêtes génériques. Il devient un procureur sémantique adversarial et doit chercher, pour chaque requête, le plus court passage plausible qui satisfait littéralement sa formulation.
- Le nouveau contrat atomique est `{"insufficientRequestIndexes":[...]}`. Le LLM inscrit l'index 1-based de toute requête dont le contre-exemple pourrait contenir moins de valeurs nommées, complètes et distinctes que le nombre de lignes à remplir. Il ne peut pas renforcer la requête en empruntant l'intention au focus parent, à la facette, au but, aux requêtes voisines ou à la table finale.
- Un tableau vide n'est autorisé sémantiquement que si aucune requête n'admet un passage générique, une valeur unique ou un autre contre-exemple sous-dimensionné. Le LLM conserve donc tout le jugement de rendement; le code ne contient aucun mot-clé de repas, de recette, de pluralité ou de domaine.
- Le parseur JSON est volontairement strict et purement mécanique : l'objet doit posséder cette unique clé, chaque élément doit être un entier JSON, chaque index doit appartenir à `1..N` et ne peut pas être dupliqué. Un tableau non vide déclenche l'auteur LLM aveugle existant; après réécriture, le même procureur examine les nouvelles formulations.
- `PlannerAcceptedScopeAudit`, son repair et son nouveau retry activent maintenant `response_format=json_object` dans l'adaptateur OpenAI-compatible. Les sorties brutes bornées des trois stades sont journalisées sous `SBRAG_PLANNER_ACCEPTED_SCOPE_AUDIT_RAW`, ce qui distingue enfin une troncature, une erreur de forme et une cardinalité fautive.
- Les prompts de portée disent explicitement qu'une catégorie commune doit être répétée à chaque position du tableau. Un singleton ne diffuse jamais sa valeur. Si le premier repair reproduit exactement le défaut Live85 avec un seul ID, `PlannerAcceptedScopeAuditContractRetry` reçoit uniquement le problème mécanique et redemande au LLM un objet complet.
- Ce retry n'introduit aucun fallback sémantique : il ne duplique pas lui-même l'ID, ne choisit pas `Cuisine`, ne transforme pas `0` et ne remplace aucun corpus. Après deux repairs LLM invalides, le plan reste refusé en sécurité. Le budget reste borné à 256 tokens, largement suffisant pour quatre IDs et une raison courte.
- La régression de rendement reproduit les questions larges de Live84/85, répare un premier JSON tronqué par `insufficientRequestIndexes=[1]`, vérifie la réécriture aveugle puis exige `[]` sur les quatre requêtes réparées. La régression de portée injecte successivement des IDs inconnus, un singleton, puis `[1,1,1,1]`; seule cette dernière décision LLM atteint l'exécuteur.
- Validation : 125/125 tests ciblés et d'architecture, 106/106 tests du pipeline, 387/387 tests SourceBacked/OpenAI/ApiClient. `git diff --check` est propre hors avertissements de fins de ligne hérités.
- Prochaine preuve discriminante : Live86 doit faire apparaître les index insuffisants pour les formulations génériques et obtenir une réécriture réellement multi-valeurs. L'audit de portée doit produire quatre IDs ou récupérer localement un singleton, puis la revue aveugle doit corriger toute divergence avant retrieval. La preuve finale reste un `EvidenceBundle` sémantiquement légitime, un Writer atteint et vingt cellules sourcées avec leurs fichiers/pages.

### 2026-07-22 - Quatre-vingt-sixième live : procureur adversarial validé, première réécriture encore insuffisante

- Journaux processus `artifacts/live-test-process-live86-20260722-121310/`, artefact lisible `artifacts/client-live-final-weekly-meal-plan-20260722-101316/answers-readable.txt`, trace principale `rag-20260722101354076-aaa675c2`, trace SourceBacked `sbrag-942bf5c20e86461d9f01267ed972a009` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260722_121333.log`.
- Le pipeline termine proprement après 916,4 s, soit 15 min 16 s; le test complet échoue après 15 min 55 s sur le payload de sources nul. La réponse sûre contient 298 caractères, aucun outil n'est exécuté et aucune carte n'est publiée. Le `testhost` et le serveur géré sont arrêtés automatiquement.
- Le routeur termine en 43,2 s avec `rag.multi_search`, français, mode strict et `allows_partial_answer=false`. Le Planner principal dure 189,1 s. La revue d'intake est inhabituellement longue à 261,4 s et reproduit les ancres concaténées ainsi que le faux `Déjeuner`.
- L'anchor repair dure 97,1 s et reste invalide. Les micro-flux récupèrent ensuite très vite la structure : lignes en 16,3 s, colonnes en 9,1 s, patch d'ancres en 10,0 s et patch de sélection en 3,3 s. La grille finale contient exactement `Lundi` à `Vendredi` et `Petit-déjeuner`, `Dîner`, `Souper`, `Collation`; elle est acceptée environ 9 min 47 après l'entrée du pipeline.
- Le premier plan compact dure 22,5 s mais recopie `lundi au vendredi` dans les quatre requêtes. Le repli stratégique général dure 72,3 s et fusionne la demande dans une seule requête sous un chemin de catégorie inconnu; son retry dure 81,9 s et rend un JSON invalide. Aucun de ces brouillons n'est exécuté.
- Le second plan compact dure 18,6 s et recopie encore la plage. Le repair row-independent termine en 29,3 s avec quatre requêtes génériques `repas <facette> semaine`, dont une faute `déner`. L'auteur/auditeur aveugle les remplace en environ 24,3 s par quatre questions homologues `Quels plats préparables pour <facette> de la semaine?` et les auto-approuve.
- Le nouveau procureur adversarial est alors validé exactement sur le défaut visé. En 5,0 s, sous JSON natif et sans repair de contrat, il retourne `{"insufficientRequestIndexes":[1,2,3,4]}`. Les quatre questions génériques sont donc toutes refusées sémantiquement avant retrieval; Live85 renvoyait encore quatre `accept` à ce même stade.
- L'auteur LLM aveugle est déclenché et termine en 24,7 s. Il tente de matérialiser la cible par des formulations du type `Plan de repas Petit-déjeuner pour la semaine du, 5 valeurs distinctes`. Ces requêtes montrent que le modèle a compris le nombre cinq, mais elles recopient le résidu incomplet `pour la semaine du` du focus assaini et ne garantissent toujours pas un passage contenant cinq valeurs complètes.
- Le second passage du procureur termine en 4,6 s avec `insufficientRequestIndexes=[1,3,4]`. Il refuse donc encore trois des quatre formulations, même s'il traite la variante `Dîner` de manière incohérente. Le pipeline respecte cette décision LLM et s'arrête en sécurité sans appeler l'audit de portée, le retrieval, le Judge ou le Writer.
- Bilan discriminant : le nouveau contrat n'est ni tronqué ni permissif. Il détecte en live la faiblesse que le verdict global puis positionnel avaient laissée passer et il contrôle aussi la réécriture. Le verrou suivant n'est plus le critique, mais la limite actuelle d'une seule tentative d'auteur aveugle et la contamination de son focus par un fragment de placement incomplet.
- Prochaine correction : conserver le refus résiduel du procureur comme observation sémantique, lancer une seconde réécriture LLM bornée et de nouveau aveugle au vocabulaire rejeté, interdire explicitement de recopier un horizon de placement ou un fragment incomplet du focus dans une requête de pool, puis effectuer une troisième et dernière revue adversariale. Le code ne choisira toujours aucun terme ni aucune requête; il bornera seulement deux tentatives et appliquera leurs contrats.

### 2026-07-22 - Seconde réécriture LLM bornée après le refus résiduel de Live86

- `AuditAcceptedPlannerFacetQueryYieldAsync` autorise désormais au maximum deux passages de l'auteur LLM aveugle. La première revue adversariale peut déclencher l'auteur initial; si la deuxième revue signale encore des index insuffisants, un auteur de retry repart une dernière fois de zéro avant une troisième revue finale.
- Il n'existe aucune boucle ouverte. Deux tentatives d'auteur et trois verdicts constituent la borne absolue. Si la revue finale conserve un seul index insuffisant, le pipeline refuse le plan et n'exécute aucune recherche. Le code ne promeut jamais automatiquement une requête pour épuiser le retry.
- Le deuxième auteur reçoit `PRIOR_ADVERSARIAL_INSUFFICIENT_REQUEST_INDEXES`, c'est-à-dire la décision sémantique structurée du procureur. Il ne reçoit ni les formulations rejetées, ni son raisonnement, ni les buts précédents. `PRIOR_AUTHORING_WORDING_OMITTED: true` rend cette séparation observable et empêche une simple paraphrase du brouillon de Live86.
- Les deux auteurs sont maintenant avertis que `QUESTION_FOCUS` est un contexte de domaine, pas un texte de requête. Ils ne doivent recopier ni horizon de planification, ni conteneur de placement, ni label de ligne, ni description de tableau, ni fragment suspendu comme `pour la semaine du` dans un pool réutilisable.
- Le prompt précise également qu'un nombre écrit dans la requête ne suffit pas si le reste demande encore au corpus de construire le plan final ou peut être satisfait par une seule généralité. Le LLM doit rechercher les valeurs source réutilisables qui rempliront ensuite le plan, et non demander directement le planning au moteur RAG.
- Les nouvelles étapes observables sont `PlannerAcceptedQueryYieldRetryRepair` et `PlannerAcceptedQueryYieldReviewFinal`. Le retry et son éventuel repair de contrat disposent du même budget borné de 480 tokens que le premier auteur; la revue finale conserve le JSON atomique natif et le budget de 256 tokens.
- La régression issue de Live86 enchaîne un JSON initial tronqué, un refus sur l'index 1, une première réécriture, un refus résiduel `[1,3,4]`, une seconde réécriture qui ne voit pas le vocabulaire précédent, puis une revue finale vide. L'exécuteur ne reçoit que la dernière proposition approuvée.
- Validation : 3/3 tests focalisés, 122/122 tests d'architecture et budgets, 106/106 tests du pipeline, 387/387 tests SourceBacked/OpenAI/ApiClient. `git diff --check` reste propre hors avertissements EOL hérités.
- Prochaine preuve discriminante : Live87 doit reproduire le premier refus rapide, améliorer la deuxième proposition sans copier `semaine du`, obtenir `insufficientRequestIndexes=[]` à la revue finale, puis atteindre pour la première fois le nouvel audit de portée JSON natif et son retry cardinal si nécessaire.

### 2026-07-22 - Quatre-vingt-septième live : seconde boucle exécutée, mais les auteurs restent ancrés sur le livrable final

- Journaux processus `artifacts/live-test-process-live87-20260722-124343/`, artefacts client `artifacts/client-live-final-weekly-meal-plan-20260722-104349/`, trace principale `rag-20260722104424839-5423fc14`, trace SourceBacked `sbrag-942bf5c20e86461d9f01267ed972a009` et serveur local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260722_124406.log`.
- Le test global expire après 30 min 01 pendant `PlannerQueryStrategyRetry` et se termine par `TaskCanceledException`. Contrairement à Live86, le pipeline n'a donc pas eu le temps de rendre sa réponse d'insuffisance terminale. Il n'a néanmoins exécuté aucun outil, aucune recherche et n'a exposé aucune source. `testhost` et le serveur local sont arrêtés après l'essai; aucun processus ne reste actif.
- Le routeur termine en 25,1 s avec la bonne décision `rag.multi_search`, français, mode strict et `allows_partial_answer=false`. Le Planner principal dure 191,4 s. La revue d'intake dure 158,1 s et identifie correctement cinq jours et quatre créneaux sans inventer de `Déjeuner`; seules les ancres de groupes sont invalides.
- L'anchor repair dure 93,6 s et reste invalide. L'adjudication de lignes dure 41,4 s, son patch de métadonnées 4,7 s et résout `lundi au vendredi`. L'adjudication de colonnes dure 33,4 s et son patch d'ancres 39,6 s. La grille exacte 5 x 4 est acceptée vers 12 h 54, environ 9 min 23 après l'entrée dans le pipeline.
- Le premier plan compact dure 37,7 s. L'audit de requêtes dure 135,2 s et remplace les chemins parasites `Cuisine/Documents techniques` par quatre questions naturelles du type `Quels aliments peuvent être utilisés pour préparer un petit-déjeuner complet et équilibré ?`.
- Le premier procureur termine en 8,8 s et refuse les index `[1,3]`. Le premier auteur aveugle dure 77,4 s et produit `Plan de repas pour la semaine avec 5 options de <facette>`. Le deuxième procureur dure 10,8 s et refuse `[1,3,4]`. Le nouvel auteur de retry dure 93,2 s et reproduit exactement la même famille malgré l'absence du texte précédent. Le procureur final dure 11,5 s et refuse encore `[1,3,4]`.
- Le repair stratégique général atteint son timeout de 150 s. Le second plan compact dure 81,3 s mais recopie `lundi au vendredi`; le garde mécanique refuse ces lignes typées. Le repair row-independent dure 115,3 s et produit `repas <facette> semaine`. Un nouvel audit de 142,9 s l'améliore en `Quels aliments peuvent être servis au <facette> ?`.
- Sur ce second parcours, le procureur initial refuse les quatre index en 11,3 s. Le premier auteur dure 63,8 s et revient à `Plan de repas pour la semaine avec 5 options de <facette>`. Le procureur retry refuse `[1,3,4]` en 8,0 s; le deuxième auteur dure 93,9 s et reproduit encore exactement cette même famille; le procureur final refuse `[1,3,4]` en 9,2 s.
- Live87 valide donc mécaniquement les deux tentatives d'auteur et les trois revues, mais réfute l'hypothèse selon laquelle une tentative aveugle supplémentaire suffirait. Le même petit modèle converge deux fois sur le livrable final parce que le prompt d'auteur lui transmet encore `QUESTION_FOCUS: Plan de repas pour la semaine du` comme contexte de domaine.
- Le garde adversarial reste la partie fiable : il refuse systématiquement les formulations dont le plus court passage littéral pourrait être générique ou ne contenir qu'une valeur. Le code respecte tous ses refus et n'exécute pas une recherche seulement parce que la requête est grammaticalement plausible.
- Prochaine correction discriminante : demander d'abord à un LLM distinct un bref `sourceDomain` décrivant uniquement la famille d'objets et les détails à trouver dans les documents, sans calendrier, table, placement ni forme du livrable. Les auteurs de requêtes verront ce domaine LLM, la langue, les facettes typées et la cible de diversité, mais plus `QUESTION_FOCUS`. Le code ne validera que le JSON, la présence et la longueur; il ne choisira aucun domaine, aliment, recette, corpus ou terme de recherche.

### 2026-07-22 - Bref de domaine documentaire décidé par LLM après Live87

- Après le premier refus non vide du procureur, le pipeline appelle désormais `PlannerAcceptedQuerySourceDomainBrief`. Ce rôle LLM reçoit la demande utilisateur, le type de tâche, la langue, le nombre de valeurs nécessaires et les facettes exactes du plan typé déjà accepté; il ne reçoit aucune requête rejetée, aucun motif du procureur et aucune portée provisoire.
- Sa responsabilité sémantique est de nommer la classe d'objets documentaires réutilisables dont les instances peuvent remplir le livrable, ainsi que les détails qui rendent chaque instance complète. Il doit décrire le contenu source à retrouver et exclure la forme finale : plan, calendrier, table, rapport, horizon de placement, labels de lignes et nombre de cases.
- Le contrat atomique contient une seule chaîne `sourceDomain`. Le code ne sait pas ce qu'est une recette, un repas, une procédure ou un corpus pertinent. Il vérifie uniquement que le JSON possède cette seule clé, que la chaîne n'est pas vide et qu'elle ne dépasse pas 240 caractères.
- Une sortie invalide déclenche un seul `PlannerAcceptedQuerySourceDomainBriefContractRepair`. Le repair revoit les mêmes données sémantiques et les seuls codes mécaniques; la sortie brute fautive lui est cachée afin d'éviter l'ancrage. Après deux contrats invalides, le plan reste refusé avant retrieval.
- Les facettes du brief proviennent des `targetFacetExact` du plan LLM accepté, et non d'une nouvelle inférence du code. Cette provenance garantit que le domaine analyste travaille sur les positions autoritatives sans voir le texte des anciennes requêtes.
- Les deux auteurs de rendement reçoivent maintenant `SOURCE_DOMAIN_LLM_BRIEF`, la langue, les facettes exactes et la cible de diversité. `QUESTION_FOCUS`, la question utilisateur et les contraintes de présentation sont entièrement absents et remplacés par `USER_QUESTION_AND_PRESENTATION_FOCUS_OMITTED: true`.
- Le second auteur réutilise le même bref sémantique, mais reste aveugle aux formulations du premier auteur. Le procureur conserve ses trois décisions indépendantes et peut toujours arrêter le flux si le domaine ou les requêtes demeurent insuffisants.
- Le bref et son repair utilisent `response_format=json_object`, un budget borné de 256 tokens et une trace `SBRAG_PLANNER_ACCEPTED_QUERY_SOURCE_DOMAIN_BRIEF`. Le contenu choisi par le LLM reste donc observable sans élargir une sortie atomique.
- La régression dérivée de Live87 injecte d'abord un objet de brief invalide contenant une propriété parasite, vérifie que le texte brut n'atteint pas le repair, puis fournit un domaine valide. Elle prouve que les deux auteurs reçoivent ce domaine, ne voient jamais `QUESTION_FOCUS`, ne recopient pas la question utilisateur et que seule la seconde proposition approuvée atteint l'exécuteur.
- Validation : compilation réussie; 124/124 tests ciblés et d'architecture; 106/106 tests du pipeline; 387/387 tests SourceBacked/OpenAI/ApiClient.
- Prochaine preuve discriminante : Live88 doit faire refuser les questions générales, produire un `sourceDomain` centré sur les éléments alimentaires ou préparations réellement documentés plutôt que sur le planning, puis obtenir des requêtes de pool capables de vaincre le contre-exemple du procureur. Le seuil suivant est l'audit de portée, puis des hits légitimes avec `docPath` et pages; la réponse 5 x 4 reste à prouver jusqu'au Writer et à l'UI.

### 2026-07-22 - Quatre-vingt-huitième live : bref atomique rapide, mais son sens décrit encore le livrable

- Journaux processus `artifacts/live-test-process-live88-20260722-133648/`, artefact lisible `artifacts/client-live-final-weekly-meal-plan-20260722-113653/answers-readable.txt`, trace principale `rag-20260722113734023-7d6f199c`, trace SourceBacked `sbrag-074c72b418d549c98c688691125caddf` et serveur local lancé à 13 h 37 min 11 s.
- Le pipeline termine proprement après environ 10 min 34 et le test complet échoue après 11 min 15 sur le payload de sources nul. La réponse d'insuffisance sûre contient 298 caractères, aucun outil n'est exécuté et aucune carte source n'est publiée. Les processus `testhost` et `llama-server` sont arrêtés automatiquement.
- Le routeur termine en 25,0 s et le Planner principal en 118,2 s. La revue d'intake dure 141,4 s et reproduit le faux `Déjeuner`; l'anchor repair dure environ 78,8 s et reste invalide.
- L'adjudication de lignes dure 29,9 s et récupère les cinq jours. L'adjudication de colonnes dure 8,4 s, conserve d'abord cinq labels, puis son patch d'ancres de 9,7 s révèle le chevauchement `Petit-déjeuner`/`Déjeuner`. Le sélecteur focalisé retire le faux label en 2,9 s. La grille 5 x 4 est acceptée vers 13 h 44 min 30, environ 6 min 30 après l'entrée du pipeline.
- Le premier plan compact dure 19,4 s mais recopie `Lundi` à `Jeudi`. Le repair stratégique général dure 55,1 s et reste invalide avec des jours et une catégorie inconnue; son retry ne produit pas de plan accepté. Le second plan compact ne dure que 9,5 s et atteint l'audit de requêtes.
- L'audit de requêtes dure 24,6 s et propose quatre questions `Quels plats peuvent être préparés pour <facette> cette semaine ?`. Le procureur adversarial refuse les quatre index en 4,8 s.
- La nouvelle étape `PlannerAcceptedQuerySourceDomainBrief` utilise bien le JSON natif et termine en 3,8 s avec un contrat parfaitement valide : `{"sourceDomain":"Menu de repas pour la semaine du lundi au vendredi"}`. La preuve mécanique est donc positive, mais la décision sémantique est exactement celle que le prompt interdisait : elle nomme le livrable final et son horizon, pas une classe d'objets documentaires réutilisables.
- Le code ne remplace pas cette phrase par une règle culinaire. Le premier auteur la consomme comme décision LLM et, en 29,9 s, produit quatre requêtes `Quels aliments pour <facette> pour la semaine du lundi au vendredi ?`. Le garde mécanique refuse immédiatement les jours typés copiés; le plan s'arrête avant retrieval.
- Bilan discriminant : séparer le rôle et cacher `QUESTION_FOCUS` aux auteurs ne suffit pas si le bref LLM lui-même transporte le focus final. Comme pour les requêtes, l'auteur d'un bref sémantique ne doit pas être son propre approbateur.
- Prochaine correction : ajouter un procureur LLM atomique du bref. Il devra chercher si la phrase littérale peut encore désigner le livrable, un calendrier, un sujet générique ou un objet unique sans détails par instance. En cas de refus, un second auteur LLM réécrira le domaine depuis la demande et les facettes, sans voir la phrase rejetée; un dernier verdict décidera. Le code ne vérifiera que le booléen, le JSON et les bornes.

### 2026-07-22 - Procureur LLM et réécriture aveugle du domaine après Live88

- Tout `sourceDomain` mécaniquement valide passe désormais par `PlannerAcceptedQuerySourceDomainBriefReview`, un LLM adversarial indépendant qui ne peut ni réécrire le domaine, ni produire les requêtes, ni répondre à l'utilisateur.
- Le critique juge la phrase littérale et cherche à réfuter qu'elle nomme une classe de plusieurs objets documentaires indépendamment sourçables avec leurs détails utiles. Il doit retourner `insufficient=true` si le plus court document correspondant pourrait être le livrable final, un calendrier, une table, un rapport, un sujet générique, un objet unique ou des objets sans détail exploitable par instance.
- Le reviewer ne voit pas la demande utilisateur ni la forme finale. Il reçoit seulement la phrase à poursuivre, les facettes typées comme contexte non autoritatif et le nombre d'instances distinctes nécessaires. Il ne peut donc pas renforcer un mauvais brief en empruntant l'intention à la question.
- Son contrat atomique contient l'unique booléen `insufficient`. Le code ne détermine aucune sémantique; il valide seulement cette forme JSON et respecte le verdict. Un repair de contrat focalisé existe si le booléen est mal sérialisé.
- Si le premier procureur refuse, `PlannerAcceptedQuerySourceDomainBriefSemanticRepair` réécrit une seule fois le domaine. Ce second auteur voit la demande, le type de tâche, la langue, les facettes autoritatives et la cible de diversité, mais pas la phrase rejetée. `PRIOR_SOURCE_DOMAIN_WORDING_OMITTED: true` rend cette étanchéité testable.
- Le repair demande explicitement quel type d'entrée documentaire préexistante et nommée pourrait être inséré dans une position du résultat après retrieval. Il doit nommer cette classe et ses détails par instance, pas le conteneur final. Aucun exemple culinaire ni terme métier n'est injecté par le code.
- Le domaine réparé passe par `PlannerAcceptedQuerySourceDomainBriefReviewFinal`. Si ce second procureur retourne encore `insufficient=true`, le pipeline refuse le plan avant toute recherche. La borne absolue reste donc deux auteurs de domaine et deux procureurs, chacun avec une éventuelle réparation de forme locale.
- Les étapes initiales, sémantiques et adversariales utilisent toutes le JSON natif, un budget maximal de 256 tokens et des traces bornées. L'auteur de requêtes ne reçoit que le domaine ayant obtenu `insufficient=false`.
- La régression Live88 fait réparer un premier JSON de domaine invalide, fournit ensuite un domaine final explicitement mauvais, obtient `insufficient=true`, vérifie que le second auteur ne voit pas cette phrase, fournit un domaine d'objets réutilisables, obtient `insufficient=false`, puis poursuit les deux auteurs et trois procureurs de requêtes jusqu'à l'exécuteur.
- Validation : compilation réussie; 124/124 tests ciblés et d'architecture; 106/106 tests du pipeline; 387/387 tests SourceBacked/OpenAI/ApiClient.
- Prochaine preuve discriminante : Live89 doit faire refuser `Menu de repas pour la semaine du lundi au vendredi`, obtenir d'un second LLM un domaine réellement centré sur des objets sources, puis générer des requêtes qui ne recopient ni les jours ni le livrable et que le procureur de rendement approuve. Le seuil reste ensuite l'audit de portée et des hits sémantiquement légitimes.

### 2026-07-22 - Quatre-vingt-neuvième live : le verdict booléen du domaine reproduit le biais permissif

- Journaux processus `artifacts/live-test-process-live89-20260722-140420/`, artefact lisible `artifacts/client-live-final-weekly-meal-plan-20260722-120426/answers-readable.txt`, trace principale `rag-20260722120502031-ea492816` et trace SourceBacked `sbrag-2a2c6801844c4e2d979c9d9821fb0883`.
- Le pipeline termine proprement après 1 160,5 s, soit 19 min 20,5 s; le test complet échoue après 19 min 56 sur le payload de sources nul. La réponse sûre contient 298 caractères, aucun outil ou hit n'est exécuté et aucun processus géré ne reste actif.
- Le routeur termine en 23,1 s, le Planner principal en 136,1 s et la revue d'intake en 121,7 s. Cette revue choisit directement les cinq jours et les quatre créneaux sans faux `Déjeuner`, mais ses ancres de groupe sont invalides.
- L'anchor repair dure 54,5 s et reste invalide. L'adjudication de lignes dure 28,0 s, son patch de métadonnées 4,8 s, l'adjudication de colonnes 21,8 s et son patch d'ancres 22,0 s. La grille exacte 5 x 4 est acceptée environ 6 min 30 après l'entrée du pipeline.
- Le premier plan compact dure 47,3 s et recopie quatre jours. Le repair stratégique général consomme son timeout complet de 150 s. Le second plan compact dure 44,8 s, recopie encore `lundi au vendredi`, puis le repair row-independent dure 84,9 s et produit `repas <facette> semaine`.
- Le premier audit de requêtes améliore ces formulations en variantes `Quels aliments peuvent être servis...`; le procureur refuse les quatre index en 8,3 s.
- Le premier auteur de domaine dure 9,7 s et renvoie `Menu de repas pour la semaine du lundi au vendredi avec petit-déjeuner, dîner, souper et collation chaque jour.`. Le nouveau reviewer booléen termine en 4,5 s mais retourne à tort `{"insufficient":false}`.
- L'auteur de requêtes respecte ce domaine et produit des formulations avec `lundi au vendredi`; le garde mécanique les refuse. Le retry stratégique général dure 88,2 s, puis un patch de facettes de 15,6 s rend une seconde stratégie exploitable pour audit.
- Le second parcours reproduit exactement le résultat : quatre requêtes générales refusées par le procureur, le même `sourceDomain` final en 12,9 s, puis le reviewer booléen répond encore `insufficient=false`, cette fois en 1,9 s. L'auteur recopie à nouveau les jours et le pipeline s'arrête avant retrieval.
- Bilan discriminant : le défaut est reproductible sur deux stratégies dans le même live. Le rôle séparé, le JSON natif et le prompt adversarial ne suffisent pas quand la sortie demande un acquittement booléen global. C'est le même biais que celui observé avec l'ancien `accept|revise` global des requêtes.
- Correction suivante déjà engagée : remplacer le booléen par une liste atomique `failureModes` dans laquelle le LLM doit poursuivre séparément `final_deliverable_or_placement`, `not_reusable_named_item_class` et `missing_per_item_details`. Une liste vide ne sera admise par le LLM que si aucun de ces contre-exemples ne s'applique. Le code ne vérifiera que les trois chaînes autorisées, l'unicité et le JSON.

### 2026-07-22 - Verdict adversarial par modes d'échec après Live89

- Le booléen global `insufficient` est remplacé par l'unique tableau `failureModes`. Live89 ayant reproduit deux acquittements erronés, le LLM doit maintenant juger trois hypothèses de réfutation indépendantes au lieu de rendre une approbation globale.
- `final_deliverable_or_placement` s'applique si le plus court document correspondant à la phrase peut être le plan, calendrier, tableau, rapport, conteneur de présentation, horizon ou arrangement final lui-même.
- `not_reusable_named_item_class` s'applique si la phrase peut désigner un sujet générique, un thème large, un seul objet ou autre chose qu'une classe réutilisable possédant plusieurs instances nommées.
- `missing_per_item_details` s'applique si la phrase ne dit pas elle-même quels détails rendent chaque instance complète et exploitable pour la réponse.
- Le prompt exige tous les modes applicables dans cet ordre et n'autorise `[]` que si aucun contre-exemple ne tient. Le reviewer ne voit toujours ni la demande utilisateur ni le livrable final; les facettes restent marquées comme contexte et non comme autorité permettant de renforcer la phrase.
- Le parseur mécanique exige un objet à clé unique, un tableau de chaînes, uniquement les trois codes autorisés et aucune duplication. Il ne détermine jamais quel code devrait apparaître. Toute liste non vide déclenche le second auteur LLM aveugle; seule une liste vide permet de transmettre le domaine aux auteurs de requêtes.
- La régression Live89 fournit les trois modes sur le premier domaine final, vérifie la réécriture sans la phrase rejetée, puis fournit `[]` sur un domaine d'objets et de détails réutilisables. Les tests de prompt vérifient que les trois poursuites sont explicitement présentes.
- Validation : compilation réussie; 124/124 tests ciblés et d'architecture; 106/106 tests du pipeline; 387/387 tests SourceBacked/OpenAI/ApiClient.
- Prochaine preuve discriminante : Live90 doit transformer le domaine final reproduit par Live88/89 en une liste non vide de modes d'échec, exercer pour la première fois le second auteur sémantique en réel, puis n'autoriser les requêtes qu'après `failureModes=[]` sur un domaine documentaire autonome.

### 2026-07-22 - Quatre-vingt-dixième live : le tableau adversarial refuse le domaine, mais l'auteur ne décompose pas encore sa sortie

- Journaux processus `artifacts/live-test-process-live90-20260722-143352/`, artefact lisible `artifacts/client-live-final-weekly-meal-plan-20260722-123358/answers-readable.txt`, trace principale `rag-20260722123434204-0b9a143e` et trace SourceBacked `sbrag-288ccab824ae4be58678ddccc29b8f56`.
- Le pipeline termine proprement après 654,6 s, soit environ 10 min 55; le test complet échoue après 11 min 33 sur le payload de sources nul. La réponse sûre contient 298 caractères, aucun outil ou hit n'est exécuté et le runtime local est arrêté.
- Le routeur termine en 28,4 s, le Planner principal en 142,5 s et la revue d'intake en 129,0 s. L'anchor repair dure 21,5 s et demande à tort une clarification. Les adjudications récupèrent les cinq jours en 51,7 s et les quatre créneaux en 21,5 s; le patch de colonnes dure 25,9 s. La grille 5 x 4 est acceptée environ 6 min 33 après l'entrée du pipeline.
- Le premier plan compact termine en 25,2 s et est directement accepté avec quatre requêtes, sans le détour de 150 s observé dans Live89. L'audit de requêtes dure 23,6 s et propose quatre questions générales `Quels aliments peuvent être utilisés pour <facette> de la semaine ?`; le procureur refuse les quatre index en 4,7 s.
- L'auteur initial de domaine dure 5,5 s et reproduit le domaine final de Live89 : `Menu de repas pour la semaine du lundi au vendredi avec petit-déjeuner, dîner, souper et collation chaque jour.`
- Le nouveau procureur `failureModes` termine en 3,2 s et retourne `not_reusable_named_item_class`. Live90 valide donc la correction principale : le mauvais domaine n'est plus acquitté et le second auteur sémantique est déclenché pour la première fois en réel.
- Le second auteur ne dure que 5,9 s, mais il remplace seulement `Menu` par `Plan` et conserve le calendrier ainsi que les créneaux : `Plan de repas pour la semaine du lundi au vendredi avec petit-déjeuner, dîner, souper et collation chaque jour.`
- Le procureur final termine en 3,2 s et refuse à nouveau `not_reusable_named_item_class`. Le code respecte ce refus, n'appelle aucun auteur de requêtes sur le mauvais domaine et retourne au repli stratégique général.
- Le repair et le retry stratégiques durent respectivement 45,7 s et 53,9 s, puis un patch de facettes de 7,8 s et un repair row-independent de 28,7 s obtiennent une seconde base. Son audit de 23,5 s recopie toutefois `lundi au vendredi`; le garde mécanique l'arrête avant une seconde boucle de domaine.
- Bilan discriminant : le reviewer par modes d'échec fonctionne et protège le retrieval. Le verrou est maintenant la forme monolithique `sourceDomain`, qui laisse au petit modèle la possibilité de renommer le même livrable au lieu de produire explicitement une classe d'objets et ses détails par instance.
- Prochaine correction : décomposer le brief LLM en deux champs atomiques `sourceItemClass` et `perItemDetails`, transmettre les modes d'échec structurés au second auteur sans la phrase rejetée, puis faire juger ces deux champs séparément par le même procureur. Le code ne concaténera pas un sens métier; il transportera les deux décisions LLM vers les auteurs de requêtes.

### 2026-07-22 - Décomposition structurelle du domaine source après Live90

- Le contrat monolithique `sourceDomain` est remplacé par deux décisions sémantiques LLM distinctes : `sourceItemClass` nomme la classe réutilisable d'entrées documentaires préexistantes dont plusieurs instances peuvent alimenter le livrable, et `perItemDetails` nomme les attributs qui rendent chaque instance complète et exploitable.
- Cette séparation reste conforme à l'ADR d'orchestration : le code ne choisit aucune classe métier, aucun détail, aucun aliment, aucune recette, aucun corpus et aucun terme de recherche. Il valide uniquement un objet JSON à deux chaînes, leur présence et leurs longueurs maximales respectives de 160 et 240 caractères.
- Le procureur LLM juge désormais les deux champs littéraux séparément. `final_deliverable_or_placement` poursuit une classe qui décrit encore le plan, le calendrier, le tableau, l'horizon ou une structure de placement; `not_reusable_named_item_class` poursuit une classe sans instances nommées réutilisables; `missing_per_item_details` poursuit des détails vides, circulaires, positionnels ou incapables de rendre chaque instance utilisable.
- Après un premier refus, le second auteur reçoit les codes `failureModes` exacts, mais jamais les deux formulations rejetées. Il doit donc corriger les dimensions sémantiques identifiées sans pouvoir simplement remplacer un mot dans la phrase précédente. Les problèmes de forme restent traités séparément par un repair de contrat borné.
- Les auteurs de requêtes ne voient toujours ni `QUESTION_FOCUS`, ni la demande utilisateur, ni la forme du livrable. Ils reçoivent uniquement `SOURCE_ITEM_CLASS_LLM`, `PER_ITEM_DETAILS_LLM`, la langue, les facettes typées et la cible de diversité, puis le procureur de rendement rejuge leurs requêtes avant toute recherche.
- La régression dérivée de Live90 commence par un JSON de brief invalide, répare sa forme, fait produire une classe et des détails décrivant encore le planning final, exige les trois modes d'échec, vérifie que le second auteur reçoit ces modes sans les formulations rejetées, puis accepte une classe d'objets nommés et des détails par instance avant les deux boucles adversariales de requêtes.
- Validation : compilation réussie; 124/124 tests ciblés et d'architecture; 106/106 tests du pipeline; 387/387 tests SourceBacked/OpenAI/ApiClient avec le filtre historique exact. `git diff --check` ne signale aucune erreur, seulement les avertissements de fins de ligne hérités.
- Prochaine preuve discriminante : Live91 doit faire poursuivre le domaine final reproduit par Live89/90, puis obtenir du second auteur une véritable classe d'objets sources et des détails par instance. Le seuil suivant est `failureModes=[]`, puis des requêtes sans calendrier approuvées par le procureur, un audit de portée cohérent et enfin des hits légitimes possédant `docPath` et pages. Le Writer, la réponse 5 x 4 et les cartes source restent non prouvés tant que le live ne les atteint pas.

### 2026-07-22 - Quatre-vingt-onzième live : la décomposition corrige le niveau d'objet, mais le retry se replie sur une seule facette

- Journaux processus `artifacts/live-test-process-live91-20260722-150305/`, artefact lisible `artifacts/client-live-final-weekly-meal-plan-20260722-130310/answers-readable.txt`, trace principale `rag-20260722130348801-96c50379` et trace SourceBacked `sbrag-47b7f7d751bd4f84bfb3c8bee930a680`.
- Le pipeline termine proprement après 1 083,9 s, soit environ 18 min 04; le test complet échoue après 18 min 43 sur un payload de sources nul. La réponse sûre contient 298 caractères, aucun outil n'est exécuté et le runtime local est arrêté.
- Le routeur choisit `rag.multi_search` strict en 25,0 s. Le Planner principal dure 138,8 s et la revue d'intake 106,0 s. Les repairs et adjudications corrigent les ancres; la grille autoritative de cinq jours par quatre facettes est acceptée à 15 h 11 min 10, environ 7 min 21 après l'entrée du pipeline.
- Le premier plan compact dure 39,9 s mais associe les quatre premiers jours aux quatre requêtes de pool. Le garde mécanique le refuse. Le repair stratégique général consomme son timeout complet de 150 s.
- Le second plan compact dure 45,2 s et recopie encore `semaine du lundi au vendredi`; un repair row-independent de 81,6 s produit quatre requêtes mécaniquement valides mais larges : `repas <facette> semaine`.
- L'audit de requêtes dure 97,0 s et transforme ces recherches en quatre questions `Quels aliments peuvent être servis au <facette> selon les régimes alimentaires ?`. Le procureur de rendement refuse correctement les quatre index en 11,9 s.
- Le premier auteur du nouveau contrat à deux champs dure 25,1 s et retourne `sourceItemClass="RepasPlan"` avec `perItemDetails="Petit-déjeuner, Dîner, Souper, Collation pour la semaine du lundi au vendredi"`. Les deux champs séparent désormais les décisions, mais décrivent encore une pseudo-classe de livrable et ses positions.
- Le procureur indépendant termine en 10,1 s et retourne exactement `not_reusable_named_item_class` et `missing_per_item_details`. La décomposition atteint donc sa première preuve réelle : les deux défauts sont poursuivis séparément et le retry sémantique reçoit ces codes sans voir les formulations rejetées.
- Le second auteur dure 20,9 s et améliore le niveau d'abstraction vers `sourceItemClass="Recette de petit-déjeuner"`, mais se replie sur une seule facette au singulier. Son `perItemDetails="Liste de recettes de petit-déjeuner pour la semaine du lundi au vendredi"` décrit encore une collection placée dans le calendrier, pas les attributs nécessaires à chaque recette.
- Le procureur final refuse `not_reusable_named_item_class` en 8,5 s, mais omet `missing_per_item_details` encore applicable. Le pipeline respecte le refus, ne transmet pas ces champs aux auteurs de requêtes et n'exécute aucun retrieval. Le retry stratégique général consomme ensuite son timeout de 150 s et mène au repli terminal sûr.
- Bilan discriminant : la décomposition et la transmission des modes d'échec améliorent réellement l'auteur et maintiennent la sécurité, mais le second auteur traite la première facette comme la classe globale et confond encore `perItemDetails` avec une liste à placer. Le prochain contrat doit forcer un jugement LLM explicite de couverture de toutes les facettes par la classe et de nature attributaire des détails, sans que le code choisisse la réponse métier.

### 2026-07-22 - Classe parent partagée et attributs probants après Live91

- Les noms atomiques deviennent `sharedSourceItemClass` et `answerBearingAttributesPerItem`. Le premier oblige le LLM auteur à choisir une seule classe parent possédant des instances nommées utilisables à travers toutes les facettes typées; le second lui demande uniquement des faits ou attributs attachés à une instance individuelle.
- Le prompt auteur impose un test grammatical générique : « For each named [sharedSourceItemClass], retrieve its [answerBearingAttributesPerItem] ». Une liste d'objets, une collection demandée, un horizon ou des positions de sortie ne peuvent pas tenir la place d'attributs par élément. Aucun exemple culinaire ni réponse métier n'est fourni.
- Les facettes peuvent filtrer ou spécialiser les instances de la classe parent, mais ne peuvent plus remplacer cette classe par la première facette ou un sous-type isolé. Cette règle vise directement la sortie Live91 `Recette de petit-déjeuner` tout en restant indépendante du domaine métier.
- Le procureur LLM dispose maintenant de quatre poursuites séparées : `final_deliverable_or_placement`, `not_reusable_named_item_class`, `not_shared_across_typed_facets` et `missing_answer_bearing_attributes`. Les facettes servent uniquement à tester la couverture littérale de la classe proposée; le reviewer ne peut pas les utiliser pour enrichir ou réécrire la proposition.
- Le code continue de vérifier uniquement les clés JSON, les deux chaînes non vides, leurs longueurs, les quatre codes autorisés et leur unicité. Il ne détermine jamais si la classe couvre réellement les facettes ou si les attributs sont suffisants; ces deux décisions restent celles des LLM auteur et procureur.
- Les auteurs de requêtes reçoivent seulement `SHARED_SOURCE_ITEM_CLASS_LLM` et `ANSWER_BEARING_ATTRIBUTES_PER_ITEM_LLM` avec les facettes et la cible de diversité. Ils doivent chercher plusieurs instances nommées de la classe commune, chaque facette pouvant filtrer ces instances sans recréer le livrable final.
- Validation : après réalignement d'une assertion textuelle, compilation réussie; 124/124 tests ciblés et d'architecture; 106/106 tests du pipeline; 387/387 tests SourceBacked/OpenAI/ApiClient avec le filtre historique exact.
- Prochaine preuve discriminante : Live92 doit produire une classe commune aux quatre types de repas et de véritables attributs par recette ou élément documentaire sans que le code nomme ce domaine, obtenir `failureModes=[]`, puis faire écrire des requêtes de pools que le procureur juge capables de rendre plusieurs valeurs complètes.

### 2026-07-22 - Quatre-vingt-douzième live : attributs probants trouvés, mais auteur et procureur globaux restent couplés

- Journaux processus `artifacts/live-test-process-live92-20260722-153425/`, artefact lisible `artifacts/client-live-final-weekly-meal-plan-20260722-133431/answers-readable.txt`, trace principale `rag-20260722133508587-ec998b01` et trace SourceBacked `sbrag-18c2d1956df24e3dbcef743cd6636f07`.
- Le pipeline termine proprement après 680,6 s, soit environ 11 min 21; le test complet échoue après 11 min 58 sur un payload de sources nul. La réponse sûre contient 298 caractères, aucun outil ou hit n'est exécuté et le runtime local est arrêté.
- Le routeur choisit `rag.multi_search` strict en 24,4 s. Le Planner principal dure 105,1 s et la revue d'intake 130,7 s. Les repairs d'ancres et adjudications récupèrent la grille 5 x 4 à 15 h 42 min 01, environ 6 min 53 après l'entrée du pipeline.
- Le premier plan compact dure 32,6 s et est directement accepté avec quatre requêtes très courtes `<facette> Cuisine`. L'audit de 25,4 s les transforme en quatre questions génériques `Quels aliments peuvent être consommés au <facette> ?`; le procureur de rendement refuse correctement les quatre index en 4,6 s.
- Le premier auteur du contrat renforcé termine en 5,2 s et retourne `sharedSourceItemClass="MenuPlan"`, mais trouve déjà des attributs probants cohérents : `ingredients, cooking instructions, nutritional information`.
- Le procureur global refuse la classe, mais retourne aussi à tort `missing_answer_bearing_attributes` alors que le second champ nomme littéralement trois attributs par instance. Le second auteur conserve ces bons attributs et réduit seulement la classe à `Menu`; le procureur final refuse encore la classe et maintient le faux positif sur les attributs.
- Le repli stratégique produit d'abord une requête de plan dupliquée, puis quatre recherches copiées sur tous les jours; un patch de facettes et un repair row-independent obtiennent quatre requêtes `repas <facette>`. L'audit revient aux quatre questions génériques et le procureur les refuse en 3,3 s.
- Sur ce second parcours, l'auteur initial propose `sharedSourceItemClass="Repas"` avec `nom du plat, ingrédients, temps de préparation, source`. Le second auteur propose `Menu items for each meal` avec `name, ingredients, preparation time, portion size`. Dans les deux cas, le procureur refuse correctement une classe trop large ou encore liée au menu, mais retourne incorrectement `missing_answer_bearing_attributes` malgré des attributs littéraux exploitables.
- Bilan discriminant : le renommage améliore nettement `answerBearingAttributesPerItem` et accélère les micro-étapes, mais le reviewer global couple encore le défaut de classe au verdict sur les attributs. Deux auteurs sont également insuffisants pour converger de `MenuPlan` ou `Repas` vers une classe d'entrées documentaires nommées.
- Prochaine correction : séparer le procureur de classe et le procureur d'attributs en deux décisions LLM atomiques indépendantes, puis autoriser un troisième auteur sémantique borné qui reçoit les derniers modes d'échec sans aucune formulation rejetée. Le code agrégera seulement les verdicts LLM validés et ne choisira toujours aucun sens métier.

### 2026-07-22 - Deux procureurs indépendants et troisième auteur borné après Live92

- Chaque tentative de domaine passe désormais par deux micro-jurys LLM distincts. Le procureur de classe ne voit que `sharedSourceItemClass`, les facettes typées et la cible de cardinalité; `answerBearingAttributesPerItem` lui est explicitement caché.
- Le procureur d'attributs ne voit que `answerBearingAttributesPerItem`. La classe, les facettes, la question et la forme de sortie lui sont explicitement cachées afin qu'un défaut de classe ne contamine plus son verdict. Il doit retourner `[]` dès que la phrase littérale nomme des propriétés, faits, spécifications, ingrédients, instructions, mesures ou autres informations attachées à un élément individuel.
- Les contrats restent atomiques : le procureur de classe peut retourner uniquement `final_deliverable_or_placement`, `not_reusable_named_item_class` et `not_shared_across_typed_facets`; le procureur d'attributs peut retourner uniquement `missing_answer_bearing_attributes`. Le code valide chaque sous-ensemble séparément puis concatène les décisions LLM sans en ajouter ni en supprimer.
- La boucle de domaine est portée de deux à trois auteurs sémantiques au maximum. Chaque auteur suivant reçoit uniquement l'union validée des derniers modes d'échec; aucune formulation de classe ou d'attribut rejetée n'est réinjectée. Après trois refus, le pipeline reste en sécurité avant retrieval.
- Cette troisième tentative remplace un abandon prématuré suivi d'un repair stratégique général beaucoup plus coûteux. Live92 montre que les auteurs et procureurs de domaine durent environ 3 à 6 s chacun, alors que le repli général dure 55 à 57 s et peut encore produire une stratégie mécaniquement invalide.
- La régression issue de Live92 exerce désormais trois auteurs : un livrable final et ses positions, une classe limitée à une seule facette et une liste hebdomadaire, puis une classe partagée et des attributs par instance. Les jurys séparés poursuivent leurs dimensions respectives; le troisième candidat seul obtient deux verdicts vides et atteint les auteurs de requêtes.
- Les nouvelles étapes de classe et d'attributs conservent `response_format=json_object`, un budget de 256 tokens, un repair de contrat local et des traces indiquant séparément les modes de classe, les modes d'attributs et leur union.
- Validation : compilation réussie; 124/124 tests ciblés et d'architecture; 106/106 tests du pipeline; 387/387 tests SourceBacked/OpenAI/ApiClient avec le filtre historique exact. `git diff --check` reste propre hors avertissements de fins de ligne hérités.
- Prochaine preuve discriminante : Live93 doit conserver les attributs concrets déjà trouvés dans Live92, faire évoluer la classe sur la troisième tentative si nécessaire, obtenir deux verdicts vides indépendants, puis atteindre les auteurs de requêtes et le procureur de rendement sans replonger dans le calendrier.

### 2026-07-22 - Quatre-vingt-treizième live : découplage des attributs prouvé, faux refus du parent grammatical au singulier

- Journaux processus `artifacts/live-test-process-live93-20260722-160159/`, artefact lisible `artifacts/client-live-final-weekly-meal-plan-20260722-140206/answers-readable.txt`, trace principale `rag-20260722140240718-57ee9601` et trace SourceBacked `sbrag-b879c82b0e30421ba5de2d70da98055a`.
- Le pipeline termine proprement après 826,2 s, soit environ 13 min 46; le test complet échoue après 14 min 22 sur `Assert.NotNull()` parce que le payload de sources reste nul. La réponse sûre contient 298 caractères, aucun outil, aucune requête RAG et aucune carte source ne sont exposés. Le processus de test et le serveur local sont arrêtés.
- Le routeur choisit `rag.multi_search` strict en 33,3 s. Le Planner principal dure 191,5 s et la revue d'intake 192,3 s. Après les repairs et adjudications d'ancres, la grille autoritative de cinq jours par quatre facettes est acceptée vers 16 h 12 min 19.
- Le plan compact suivant est accepté en 12,0 s avec quatre requêtes `<facette> Cuisine`. L'audit de 23,1 s les reformule en `Quels plats préparer pour <facette> de la semaine ?`; le procureur de rendement refuse les index `[1,3,4]` en 4,6 s, puis déclenche correctement l'analyse du domaine source.
- Le premier auteur propose `sharedSourceItemClass="Recette de repas"` et `answerBearingAttributesPerItem="nom du plat, ingrédients, temps de préparation, nombre de personnes"`. C'est la première proposition live qui sépare clairement une classe parent de vrais attributs attachés à chaque élément.
- Le nouveau procureur d'attributs retourne `failureModes=[]` à chacune des trois tentatives. Il ne reproduit donc plus le faux `missing_answer_bearing_attributes` de Live92 lorsque la classe est critiquable. Le découplage des deux jurys est validé en conditions réelles.
- Le procureur de classe refuse toutefois `Recette de repas` avec `not_reusable_named_item_class` et `not_shared_across_typed_facets`. Cette décision est sémantiquement incohérente : une classe grammaticale au singulier peut désigner une catégorie possédant plusieurs instances nommées, et un parent générique peut couvrir des facettes qui sont ses sous-types sans les énumérer.
- Privés de la formulation rejetée mais guidés par ces deux codes erronés, les deuxième et troisième auteurs régressent tous deux vers `Menu de repas pour la semaine`. Leurs attributs restent concrets et sont acquittés par le jury d'attributs, tandis que le jury de classe refuse correctement ces nouveaux livrables. Après trois auteurs, le pipeline s'arrête en sécurité avant retrieval.
- Le repli stratégique général produit ensuite des plans contaminés par les jours. Le repair row-independent obtient `repas <facette> semaine`, mais l'audit recopie des jours précis et le garde mécanique arrête le flux. Aucun résultat potentiellement mal cadré n'atteint le Judge ou le Writer.
- Bilan discriminant : Live93 prouve que les attributs sont désormais jugés indépendamment et correctement. Le verrou exact est le contrat linguistique du procureur de classe, qui confond le nombre grammatical avec la cardinalité des instances et exige implicitement que le parent énumère ses facettes.
- Prochaine correction : expliquer génériquement à l'auteur et au procureur qu'une étiquette de classe peut être au singulier, qu'elle doit être distinguée d'un nom propre ou d'une instance particulière, et qu'une catégorie parent peut couvrir ses sous-types sans les citer. `not_reusable_named_item_class` ne doit viser qu'une phrase sans lecture de catégorie réutilisable; `not_shared_across_typed_facets` ne doit viser qu'une restriction littérale à une facette ou un sens qui en exclut une. Le code ne doit ni reconnaître ni accepter une classe métier lui-même.

### 2026-07-22 - Contrat générique catégorie-instance et couverture parent-enfants après Live93

- L'auteur LLM est maintenant averti qu'une étiquette de catégorie peut employer le singulier ou le pluriel grammatical de la langue demandée. Le singulier grammatical ne signifie pas une instance unique lorsque la phrase désigne une catégorie possédant plusieurs membres nommés.
- L'auteur doit choisir une catégorie plutôt qu'un nom d'instance. Une classe parent peut couvrir les facettes typées comme sous-types ou filtres sans devoir recopier leurs noms dans `sharedSourceItemClass`; rendre la classe concrète en la rétrécissant à une seule facette reste interdit.
- Le procureur de classe distingue désormais explicitement une catégorie nominale d'un nom propre, identifiant ou syntagme désignant un membre particulier. Il ne doit plus produire `not_reusable_named_item_class` sur la seule base du nombre grammatical.
- `not_reusable_named_item_class` est borné aux phrases sans lecture de catégorie d'éléments, aux sujets génériques sans instances documentaires, aux instances particulières et aux autres classes non réutilisables. Cette décision reste entièrement sémantique et appartient au LLM.
- `not_shared_across_typed_facets` est borné à un qualificatif littéral qui restreint la classe à une facette ou un sous-type, ou à un sens de classe qui exclut une facette. L'absence d'énumération des facettes n'est plus assimilée à une absence de couverture.
- Aucun mot Cuisine, recette, repas ou autre classe métier n'a été ajouté aux prompts ou aux gardes. Le code ne reconnaît aucune proposition et ne peut toujours pas transformer un refus du procureur en acceptation.
- Les tests d'architecture figent les distinctions génériques « singulier grammatical versus instance », « catégorie versus nom propre » et « parent versus sous-type explicitement restreint », ainsi que l'absence d'inférence de facettes.
- Une première compilation a rencontré un verrou de DLL XAML laissé par une commande de build interrompue; la relance mono-processus avec compilation partagée désactivée a compilé WinUI et les tests sans erreur.
- Validation finale : 122/122 tests d'architecture, 106/106 tests du pipeline et 387/387 tests SourceBacked/OpenAI/ApiClient. `git diff --check` retourne 0; seuls les avertissements EOL hérités subsistent lors de l'inspection du worktree.
- Prochaine preuve discriminante : Live94 doit produire ou conserver une véritable classe parent, obtenir `failureModes=[]` du jury de classe sans affaiblir le jury d'attributs, puis atteindre les auteurs de requêtes, le procureur de rendement et l'audit de portée. Aucun succès final ne sera revendiqué avant des hits légitimes portant fichier et page, un Writer 5 x 4 vérifié et des cartes source utiles.

### 2026-07-22 - Quatre-vingt-quatorzième live : protection maintenue, mais l'auteur copie les identifiants internes

- Journaux processus `artifacts/live-test-process-live94-20260722-163123/`, artefact lisible `artifacts/client-live-final-weekly-meal-plan-20260722-143128/answers-readable.txt`, trace principale `rag-20260722143202756-c765f01f`, trace SourceBacked `sbrag-f661eb0b31dc4ce78420c1939ee64899` et journal runtime `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260722_163145.log`.
- Le pipeline termine proprement après 1 706,6 s, soit 28 min 27; le test complet échoue après 29 min 02 sur le payload de sources nul. La réponse sûre contient 298 caractères, aucun outil n'est exécuté, aucune requête RAG n'atteint le backend et aucune carte source n'est affichée. `testhost` et le serveur géré sont arrêtés.
- Le routeur choisit `rag.multi_search` strict en 22,4 s. Le Planner principal dure 155,3 s et la revue d'intake 186,5 s. La revue retrouve les cinq jours mais invente `Déjeuner` en plus de `Dîner` et concatène les ancres.
- L'anchor repair dure 132,4 s et retourne une clarification invalide. L'adjudication des lignes récupère les cinq jours en 67,1 s. L'adjudication des colonnes garde d'abord cinq labels en 38,4 s, puis le patch d'ancres de 51,1 s expose le chevauchement exact. Le sélecteur focalisé retire `Déjeuner` en 8,2 s; la grille 5 x 4 est acceptée à 16 h 43 min 06, environ 10 min 40 après l'entrée du pipeline.
- Le premier plan compact dure 33,4 s et produit quatre requêtes `<facette> Cuisine/Documents techniques`. L'audit extrêmement lent de 135,8 s les transforme en quatre questions générales `Quels aliments peuvent être utilisés pour <facette> ?`. Le procureur de rendement refuse correctement les quatre index en 12,8 s.
- Le premier auteur de domaine dure 16,1 s et propose `sharedSourceItemClass="MenuPlannerItem"` avec `ingredients, cookingInstructions, nutritionalValues`. Le nom de classe est une pseudo-entité logicielle manifestement dérivée des noms de schéma, tandis que les attributs sont concrets.
- Les jurys séparés se comportent correctement : la classe reçoit `not_reusable_named_item_class,not_shared_across_typed_facets`; les attributs reçoivent `[]`. Les deuxième et troisième auteurs reviennent cependant tous deux à `Plannings de repas pour la semaine`, avec des attributs de calendrier. Le jury de classe les refuse, mais omet `final_deliverable_or_placement`, pourtant littéralement applicable; le retry reçoit donc un diagnostic moins précis qu'il ne devrait.
- Le repair stratégique général expire après 150 s. Le second plan compact dure 97,6 s et recopie lundi-vendredi dans les quatre requêtes; le repair row-independent dure 138,8 s et obtient `repas <facette> semaine`. Son audit de 73,3 s propose ensuite `Recettes de <facette> dans Cuisine/Documents techniques`; le procureur de rendement refuse à nouveau les quatre index.
- Sur ce second parcours, le premier auteur produit d'abord un contrat invalide. Son repair de forme place ensuite littéralement `PlannerAcceptedQuerySourceDomainBriefContractRepair` dans `sharedSourceItemClass`, tout en conservant `ingredients, cooking instructions, nutritional information` comme attributs. Le jury de classe refuse correctement l'identifiant interne et le jury d'attributs acquitte encore les attributs.
- Les deux auteurs suivants reproduisent `Plannings de repas pour la semaine`; ils sont refusés. Le dernier retry stratégique expire encore après 150 s, puis le pipeline retourne sa réponse d'insuffisance sans jamais exposer une recherche mal cadrée.
- Bilan discriminant : Live94 ne présente pas une classe parent grammaticale valide et ne permet donc pas de tester directement le correctif de Live93. Il révèle un verrou antérieur plus précis : le petit modèle confond les marqueurs `SAAIA_SOURCE_BACKED_STEP`, les noms de champs camelCase/PascalCase et le contenu documentaire à nommer. Le jury bloque cette fuite, mais les retries continuent faute d'instruction explicite et d'un code `final_deliverable_or_placement` complet.
- Prochaine correction : déclarer dans le prompt auteur que tout identifiant d'étape, clé de schéma, nom logiciel, token camelCase/PascalCase ou autre métadonnée d'application est du contrôle et ne peut jamais devenir le contenu d'un champ sémantique. Exiger une expression nominale naturelle susceptible d'apparaître dans les documents et dont plusieurs membres distinctement nommés pourraient être énumérés. Sur retry, expliquer génériquement chaque mode d'échec sans montrer la formulation rejetée.
- Le procureur de classe doit aussi appliquer `final_deliverable_or_placement` à toute classe dont les membres seraient eux-mêmes des plans, calendriers, tables, listes ou autres conteneurs terminaux, même si la demande utilisateur est masquée. Il doit cumuler ce code avec les autres modes applicables, pas le remplacer par un diagnostic moins précis.

### 2026-07-22 - Préflight Live95 : plan de contrôle caché, jurys atomiques et boucle auteur stable avec Qwen réel

- Les microsondes post-Live94 ont établi que `SAAIA_SOURCE_BACKED_STEP=...` n'était pas seulement une balise de journalisation : l'adaptateur s'en servait pour choisir le budget de sortie et le mode JSON, puis la transmettait au modèle. Une sonde isolée a montré que sa présence suffisait à faire retourner `excludedFacetIndexes=[]` au juge de couverture sur une classe pourtant limitée au petit-déjeuner.
- `SourceBackedLlmPromptSanitizer` sépare désormais le plan de contrôle du contenu visible par le modèle. L'adaptateur `RagChatAgent.LlmAdapter` et `OpenAiCompatLlmClient` calculent toujours le budget et `response_format` à partir du prompt original, puis retirent uniquement les lignes autonomes `SAAIA_SOURCE_BACKED_STEP=...` des messages système envoyés au LLM. Les messages utilisateur ne sont jamais modifiés.
- Un test HTTP vérifie simultanément que le budget spécialisé reste à 400 tokens dans son cas témoin et que le marqueur n'apparaît plus dans le payload. Les sept tests `OpenAiCompatLlmClientTests` passent.
- Le reviewer global puis les deux reviewers classe/attributs ont été remplacés par des rôles atomiques courts : livrable ou placement final, catégorie source réutilisable, couverture littérale des facettes, attributs applicables à un item et fuite de valeurs candidates. Le code n'évalue pas leur sens; il valide leurs petits contrats et agrège les verdicts LLM.
- Les verdicts livrable, réutilisabilité et attributs utilisent `decision=pass|fail`. La couverture retourne uniquement les index de facettes littéralement exclus. La fuite de valeurs retourne le booléen nommé `containsCandidateValues`, plus stable que l'inversion mentale d'un `pass|fail` générique sur le petit modèle.
- Le champ d'attributs reste jugé par deux LLM indépendants : le premier vérifie qu'il complète naturellement « pour chaque item, récupérer ses propriétés », le second détecte des valeurs concrètes rangées par facette. Seule la syntaxe `_` ou camelCase est refusée mécaniquement, parce que le contrat promet du langage naturel et que Qwen ne détecte pas fiablement le caractère underscore même lorsqu'on le lui demande directement.
- L'auteur de domaine est maintenant formulé comme bibliothécaire de récupération. Il doit choisir la catégorie d'entrées documentaires placées dans le résultat final, et non le plan, menu, calendrier ou horizon. Les exemples n'enseignent que le changement de niveau ontologique; les attributs doivent être dérivés de la classe choisie et rester des noms de propriétés, sans valeurs candidates.
- Trois auteurs sémantiques restent la borne absolue. Chaque retry reçoit uniquement les étiquettes de refus validées, jamais le texte rejeté. `final_deliverable_or_placement` demande de remplacer le conteneur par les entrées réutilisables qu'il contient; les autres étiquettes demandent respectivement une catégorie nommable, un parent commun ou de vrais attributs par item.
- Une microsonde live réutilisable et désactivée par défaut, `LiveSourceDomainMicroprobeTests`, exécute les prompts compilés avec `Qwen2.5-3B-Instruct-Q4_K_M`, température 0,1, budget 256, JSON natif et exactement la sanitation de production. Elle reproduit aussi le repair de contrat et les trois auteurs bornés avant d'exercer des témoins positifs et négatifs.
- Les itérations v1 à v9 ont été conservées sous `artifacts/live95-preflight-sanitized-domain-microprobe-20260722-175924/`. Elles ont successivement exposé : réponses neutres dues aux prompts trop longs; biais du code d'échec d'attributs; répétition de `repas de la semaine`; mélange d'exemples inter-domaines; confusion singulier/pluriel et langue; valeurs candidates prises pour attributs; génération snake_case puis camelCase; faux positifs sur `nom du plat`; et inversion du `pass|fail` du détecteur de valeurs.
- Les versions v10, v11 et v12 passent trois fois consécutivement sans modification. Dans v10, `menus` est refusé comme livrable, le second auteur produit `recettes` avec `nom de la recette, ingrédients, temps de cuisson`, puis les cinq jurys l'acceptent. Les témoins refusent le plan final, l'identifiant interne, `repas_plan`, la classe limitée au petit-déjeuner, les valeurs organisées par repas, l'underscore et le camelCase; ils acceptent la recette au singulier/pluriel, sa traduction anglaise et les propriétés naturelles.
- Le fichier de pipeline dépassant 500 lignes a été scindé : les validations de forme du brief sont dans `SourceBackedRagPipeline.PlannerAcceptedQuerySourceDomainBriefContract.cs`. La limite modulaire d'architecture est de nouveau respectée.
- Validation finale avant Live95 : 122/122 tests d'architecture, 106/106 tests du pipeline et 387/387 tests SourceBacked/OpenAI/ApiClient. La tentative de lancer les 2 052 tests hors live a atteint dix minutes sans test rouge et a été interrompue proprement; ce périmètre plus large inclut des tests longs étrangers à cette modification et ne remplace pas le filtre historique RAG terminé en 36 secondes.
- Prochaine preuve discriminante : Live95 doit faire franchir au vrai pipeline la boucle de domaine en une à trois tentatives, produire des requêtes de pools à partir de `recettes` et de propriétés naturelles, obtenir l'accord du procureur de rendement, puis atteindre le premier retrieval légitime. Le succès final exigera toujours des hits avec fichier et page, un plan 5 x 4 vérifié, des citations visibles et des cartes source utiles.

### 2026-07-22 - Live95 : domaine source validé, mais faux refus systématique du rendement multi-hit

- Journaux processus `artifacts/live-test-process-live95-20260722-192729/`, artefact lisible `artifacts/client-live-final-weekly-meal-plan-20260722-172738/answers-readable.txt`, trace principale `rag-20260722172813316-faa39bb1`, trace SourceBacked `sbrag-229c3269874d40e5ab293923e1ec31ea` et journal runtime `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260722_192754.log`.
- Le test réel termine en 33 min 34 s. Le pipeline produit une abstention sûre de 298 caractères, aucun outil, aucune requête RAG, aucun hit et aucune carte source. Le runtime géré est arrêté proprement à 20 h 01 min 12.
- Live95 franchit le verrou de Live94. Le premier auteur de domaine produit `menus` avec des attributs concrets; le jury du livrable le refuse. Le second auteur produit `recettes` avec `nom de la recette, temps de cuisson et ingrédients`; les cinq jurys atomiques l'acceptent. La séparation des marqueurs de contrôle et la nouvelle boucle de domaine sont donc validées sur le chemin client réel.
- Le Planner principal dure 171,7 s. La revue d'intake dure 276,3 s et invente d'abord `Jour`/`Déjeuner`; les réparations et adjudications récupèrent finalement les cinq jours et les quatre colonnes exactes vers 19 h 42 min 56. Cette latence et les faux départs d'ancrage restent un chantier distinct.
- Le compact planner produit quatre requêtes et l'audit LLM les transforme en questions générales du type `Quels aliments ... ?`. Le procureur de rendement refuse correctement les quatre index. Après le domaine `recettes`, deux auteurs successifs produisent pourtant des recherches portant explicitement sur des recettes, leur nom, leurs ingrédients et leur cuisson.
- Le procureur refuse encore les quatre index après chacune de ces réécritures. Sa règle demandait d'imaginer le plus court passage satisfaisant une requête et exigeait implicitement que ce passage unique contienne cinq valeurs complètes. Cette hypothèse ne correspond pas au moteur RAG, qui agrège plusieurs hits pouvant chacun porter un item complet.
- Le repli stratégique qui suit est coûteux et inutile : une première stratégie recopie `lundi au vendredi` et les quatre facettes dans une requête, le retry expire après 150 s, le compact planner recopie encore les jours et son repair row-independent expire lui aussi après 150 s. Le pipeline respecte tous les refus et s'arrête avant retrieval.
- Deux microsondes du reviewer agrégé, conservées sous `artifacts/live-query-yield-microprobe-20260722-181329/` et `...-181912/`, refusent à la fois les quatre questions génériques et les quatre requêtes fortes. La reformulation multi-hit ne corrige donc pas le biais de décision globale.
- Deux microsondes atomiques `pass|fail`, sous `...-182435/` et `...-182744/`, acquittent à la fois le cas faible et le cas fort. Comme pour les anciens verdicts globaux de domaine, le petit modèle transforme encore l'approbation générale en réponse par défaut.
- Le contrat stable pose une seule propriété positive : `containsItemIdentifierAndDetails`. Le LLM retourne `true` uniquement si la requête mentionne explicitement un identifiant par item, tel que nom ou titre, et au moins un détail substantiel par item, tel que composants, ingrédients, quantités, prérequis, étapes, mesures ou durée. Les qualificatifs `sain`, `adapté`, `recommandé` ou `idéal` ne sont pas des champs de preuve.
- La microsonde finale `artifacts/live-query-yield-microprobe-20260722-183203/report.txt` passe trois répétitions consécutives avec le vrai Qwen et les prompts de production sanitizés : six verdicts exacts, `false` pour la question générique et `true` pour `recettes petit-déjeuner noms ingrédients temps de cuisson`.
- La production exécute désormais ce jury séparément pour chaque requête. Chaque appel ne voit que `EXPECTED_EXACT_FACET`, `QUERY_TO_CLASSIFY` et l'absence explicite du purpose. Une réparation JSON locale est autorisée; le code parse l'unique booléen, agrège les index dont le verdict est faux et ne décide jamais lui-même quels mots constituent un identifiant ou un détail.
- `SourceBackedQueryAnswerShapeDecision` et son parseur imposent une clé unique et une vraie valeur booléenne. Les fixtures historiques de pipeline traduisent leurs anciens verdicts agrégés en quatre décisions atomiques sans masquer les scénarios de refus, de repair ou de retry.
- Validation après intégration : 122/122 tests d'architecture, 106/106 tests du pipeline et 388/388 tests SourceBacked/OpenAI/ApiClient avec le filtre RAG élargi incluant la nouvelle microsonde. Aucun résultat live n'est assimilé à une validation finale tant que retrieval, EvidenceBundle, Writer et sources UI ne sont pas atteints.
- Prochaine preuve discriminante : Live96 doit accepter les requêtes `recettes + facette + identifiant + détails`, éviter le repli stratégique de Live95 et exécuter enfin les quatre recherches. Une fenêtre plus longue sera utilisée pour laisser le retrieval, le Judge et le Writer terminer; le seuil de réussite reste 20 cellules substantielles, des identifiants de preuve visibles, des hits légitimes avec fichier et page et des cartes source non dupliquées.

### 2026-07-22 - Live96 : la grille perd Souper et le plan compact confond facettes et lignes

- Journaux processus `artifacts/live-test-process-live96-20260722-204937/`, artefact lisible `artifacts/client-live-final-weekly-meal-plan-20260722-184944/answers-readable.txt`, trace principale `rag-20260722185020228-9c92b08e`, trace SourceBacked `sbrag-4e5d4efa5d174cb1b404013849d78f85` et journal runtime `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260722_205001.log`.
- Le test se termine proprement après 20 min 25 s. Le pipeline s'arrête après 1 189,3 s avec l'abstention sûre de 298 caractères, sans outil, hit, source ni carte. Le serveur local géré est arrêté. Le nouveau jury atomique de rendement n'est jamais atteint; Live96 ne le réfute donc pas.
- Le routeur termine en environ 25,9 s, le Planner initial en 175,9 s et la revue d'intake en 177,4 s. L'intake propose d'abord les lignes Lundi à Vendredi et les colonnes `Petit-déjeuner | Déjeuner | Dîner | Collation` : il invente `Déjeuner` et omet l'explicite `Souper`.
- Les réparations d'ancres et de sélection retirent correctement le faux `Déjeuner`, mais ne disposent d'aucune opinion indépendante sur l'exhaustivité du résultat restant. Elles acceptent donc une grille à trois colonnes `Petit-déjeuner | Dîner | Collation`. Les gardes mécaniques savent réfuter une ancre fausse, pas décider qu'une facette sémantique absente devait exister.
- Le premier plan compact dure 146,7 s et produit 891 caractères non parseables. Son repair de format dure 31,3 s et reste non parseable. Le repli stratégique général dure 142,7 s, recopie les lignes et toutes les colonnes dans une requête, puis son retry expire après 150 s.
- Le second plan compact est parseable, mais mappe cinq positions de lignes sur seulement trois facettes : la première requête vise le petit-déjeuner avec Lundi, les suivantes associent les positions Mardi à Vendredi, et les requêtes 4 et 5 sortent de la plage autorisée. Le code retourne `compact_facet_request_count_mismatch expected=3 actual=5` et `out_of_range_compact_facet_indices: 4,5`, puis s'arrête sans retrieval.
- Live96 isole donc deux défauts amont indépendants : aucune preuve de complétude après réparation d'un axe valide mais incomplet, et aucune reprise LLM d'un plan compact JSON valide mais contractuellement faux.

### 2026-07-22 - Préflight Live97 : comptage LLM aveugle, récupération de Souper et réparation compacte complète

- L'audit de complétude n'utilise aucune liste de mots métier et ne laisse pas le code choisir une colonne. Un premier LLM compte à l'aveugle les membres distincts de l'axe colonne explicitement demandés. Les expressions séparément énumérées par virgules ou conjonctions restent distinctes même si un dialecte pourrait les considérer synonymes; seule une alternative explicitement écrite avec slash ou `ou` peut former un groupe unique.
- Le contrat de comptage contient uniquement l'entier `count`. Le nombre d'ancres candidates est conservé dans `SAAIA_SOURCE_BACKED_CURRENT_COLUMN_COUNT` pour les fixtures, mais `SourceBackedLlmPromptSanitizer` retire maintenant toute ligne système autonome commençant par `SAAIA_SOURCE_BACKED_` avant l'appel réel. Le modèle ne voit donc ni cette cardinalité ni le marqueur d'étape et ne peut pas s'y ancrer.
- Si le compte LLM égale le nombre d'ancres déjà validées, le pipeline conserve l'axe. S'il est supérieur, un second rôle LLM doit fournir exactement la différence sous `missing`, avec `label`, `quote` et `relation`. Le code compare uniquement les cardinalités, rejette les doublons, valide les citations dans la question, fusionne les ancres et les remet dans l'ordre textuel de la question.
- Les premières microsondes ont été conservées comme preuves négatives : `artifacts/live-planner-recovery-microprobe-20260722-193103/` et `...-193448/` montrent les clés déformées `missingAnches`, `missingAnchsors` et `missingAnchs`; `...-194115/`, `...-194415/` et `...-194751/` montrent qu'un booléen direct reste faux sur l'axe incomplet, notamment parce que Qwen fusionne `dîner` et `souper`.
- La décomposition finale par comptage passe dans `artifacts/live-planner-recovery-microprobe-20260722-195932/report.txt` puis dans la sonde complète `artifacts/live-planner-recovery-microprobe-20260722-201202/report.txt`. Sur trois tours, le LLM retourne `count=4`, sélectionne uniquement `Souper` avec la citation `souper`, conserve un axe complet à quatre membres et ne double jamais l'alternative `goûter / collation` : neuf décisions d'axe correctes sur neuf.
- `PlannerCompactFacetPlanContractRepair` est maintenant appelé lorsqu'un plan `accept` est parseable mais viole les contrats de cardinalité, d'index, de facette, d'outil, de portée ou de chaîne vide. Il repart des facettes autoritatives, des bindings positionnels et des seuls codes mécaniques; la sortie fautive est cachée. Live microprobe ramène cinq positions erronées à exactement trois requêtes de facettes dans trois tours sur trois.
- Cette réparation révèle systématiquement un défaut sémantique suivant : Qwen produit encore une requête pour Lundi, une pour Mardi et une pour Mercredi. Le garde `row_independent_query_copies_typed_rows` les refuse. Le tout premier plan compact rejeté est donc désormais relié à `PlannerRowIndependentQueryRepair`, alors que ce repair focalisé n'était auparavant exécuté que sur les chemins compacts ultérieurs.
- La sonde réelle complète de 9 min 38 s vérifie la chaîne : trois plans de cardinalité correcte mais contaminés sont détectés, trois repairs LLM retirent tous les jours, puis la vérification mécanique ne trouve plus aucune ligne copiée. Les formulations nettoyées telles que `Repas matinal` ou `Plats du dîner` restent assez générales; elles ne sont pas déclarées aptes au retrieval avant l'audit indépendant, le domaine source et le jury de rendement.
- Nouveaux éléments principaux : `SourceBackedRagPrompts.PlannerIntakeColumnAxisCompleteness.cs`, `SourceBackedRagPipeline.PlannerIntakeColumnAxisCompleteness.cs`, `SourceBackedRagJson.IntakeColumnAxisCompleteness.cs`, `SourceBackedRagRecoveryArchitectureTests.cs` et `LivePlannerRecoveryMicroprobeTests.cs`. Le plan compact dispose aussi d'un prompt de repair contractuel, d'un budget de 320 tokens et d'une trace dédiée.
- Validation à ce palier : 230/230 tests d'architecture et de pipeline, 391/391 tests du filtre RAG élargi, microsonde réelle finale réussie, et `git diff --check` sans erreur hors avertissements EOL hérités.
- Prochaine preuve discriminante : Live97 doit récupérer `Souper` dans le vrai pipeline, réparer précocement le plan compact et ses lignes copiées, puis faire améliorer ou refuser les requêtes encore générales par les jurys existants. Le succès final exige toujours retrieval réel, hits légitimes, `EvidenceBundle`, Writer 5 x 4, ancrage fichier/page, liens et cartes source utiles.

### 2026-07-22 - Live97 : grille 5 x 4 et retrieval atteints, puis boucle de statut de preuve jusqu'au timeout global

- Journaux processus `artifacts/live-test-process-live97-20260722-222555/`, artefact client `artifacts/client-live-final-weekly-meal-plan-20260722-202606/answers-readable.txt`, trace principale `rag-20260722202654808-8650dc0f` et runtime local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260722_222635.log`.
- Le test est annulé après exactement 55 min par sa borne globale. L'exception remonte de `EvidenceStatusReview` à travers `SelectionRepair`, `DecisionPreparation` et le pipeline principal. Aucun texte de réponse n'est écrit dans `answers-readable.txt`; le runtime géré est arrêté proprement à 23 h 21 min 06 et aucun processus du live ne reste actif.
- Le routeur choisit correctement `rag.multi_search`, le français et le mode strict. Le Planner initial dure 152,1 s. La revue d'intake dure 229,7 s et propose d'abord cinq colonnes en ajoutant `Déjeuner` à `Dîner`, mais conserve bien `Souper`.
- L'anchor repair global refuse de reconstruire les axes. L'adjudication de lignes récupère ensuite `Lundi` à `Vendredi`, puis son patch mécanique fixe la relation `range_expansion` sur la citation `lundi au vendredi`.
- L'adjudication de colonnes propose encore cinq labels. Son patch d'ancres expose le chevauchement `Petit-déjeuner` / `Déjeuner`; le sélecteur focalisé conserve exactement `Petit-déjeuner | Dîner | Souper | Collation`. Le nouveau comptage aveugle retourne `count=4`, ce qui confirme l'exhaustivité de l'axe sans nouvelle récupération. La structure 5 x 4 est acceptée avec des citations présentes dans la question.
- Le plan compact produit directement quatre requêtes, une par facette, sans recopier les jours. L'audit de requêtes atteint son timeout de 150 s, mais sa réparation contractuelle termine en 143,5 s et fournit quatre questions naturelles encore trop générales.
- Les quatre jurys atomiques de rendement retournent `containsItemIdentifierAndDetails=false`. Le premier auteur de domaine propose `menus`; le jury du livrable le refuse. Le second auteur propose `recettes` avec `nom de la recette, temps de cuisson et ingrédients`; les cinq jurys atomiques l'acceptent.
- Le repair de rendement produit quatre recherches `recettes + nom + temps de cuisson + ingrédients + facette`. Les quatre jurys retournent alors `true`, et l'audit de portée rattache les quatre requêtes à `Cuisine`.
- Le retrieval réel est atteint pour la première fois sur ce parcours. La recherche Petit-déjeuner expire après 35 s avec zéro hit. Les recherches Dîner, Souper et Collation retournent respectivement 8, 9 et 10 hits. Les objets récupérés portent bien `docId`, `docPath`, `docName`, `pageStart`, `pageEnd` et `excerpt`; par exemple `Cuisine/facilitemps.pdf`, page 59, et `Cuisine/14911887_9001116052_NFFS4I_fr_fm.pdf`, page 28.
- Cette observation tranche le problème d'épinglage : la mécanique fichier/page n'est pas perdue à l'entrée de l'`EvidenceBundle`. Le défaut Live97 est postérieur, dans la sélection et l'action de preuve qui doivent transporter ces ancres vers les lectures documentaires puis le Writer.
- Le premier juge de preuves ne retient qu'un id et la revue de statut annonce jusqu'à vingt besoins manquants. Après réparations, une recherche Souper supplémentaire retourne un hit. La revue suivante voit jusqu'à huit ids utiles et un seul manque, et recommande `read_documents`, mais son contrat est rejeté.
- Les journaux montrent une signature stable : chaque réparation de décision conserve une action plausible et des ids utiles, mais termine avec exactement trois problèmes mécaniques. Ces trois problèmes correspondent aux champs narratifs obligatoires `objective`, `answerShape` et `databaseAssessment`, absents des réponses Qwen alors qu'ils ne pilotent aucune action du pipeline.
- `ReviewEvidenceStatusAsync` attrape ensuite l'`InvalidOperationException` de contrat dans le même bloc que les erreurs de parsing JSON. Une revue syntaxiquement valide est donc faussement journalisée `parse_failed`, puis entièrement régénérée par `EvidenceStatusReviewFormatRepair`. La chaîne alterne `clarify`, `need_more_evidence`, `read_documents` et `stop_insufficient_database`, relance les réparations et consomme le budget global avant toute lecture exacte ou rédaction.

### 2026-07-22 - Préflight Live98 : mémoire de statut décisionnelle, erreurs typées et réparations bornées

- Le contrat de la mémoire de statut ne rend plus obligatoires les résumés narratifs redondants `objective`, `answerShape` et `databaseAssessment`. L'objectif et la forme sont déjà portés par l'intake immuable; leur absence ne doit pas annuler une décision LLM complète sur les ids, les manques et l'action suivante.
- Le schéma demandé au LLM est réduit aux six champs qui pilotent réellement la suite : `missingRequirements`, `usefulEvidenceIds`, `weakEvidenceIds`, `searchAssessment`, `recommendedNextAction` et `rationaleNotes`. Le modèle reste seul responsable de la pertinence des preuves, de la classification utile/faible, des manques et de l'action sémantique.
- Le code conserve ses contrôles strictement mécaniques : action appartenant à l'énumération, ids connus et visibles, listes utile/faible disjointes, une preuve utile par groupe document/page, présence d'un manque pour une action non rédactionnelle et disponibilité d'un tour pour une nouvelle récupération.
- Une revue JSON correctement parsée n'entre plus dans le repair de format si sa réparation de contrat échoue. Le repair contractuel est tenté une fois; s'il reste incohérent, la mémoire est abandonnée pour ce passage et le réparateur de sélection prend la suite. Seule une vraie erreur syntaxique du premier JSON peut déclencher `EvidenceStatusReviewFormatRepair`.
- Les timeouts de `EvidenceStatusReview` retournent maintenant le même schéma décisionnel compact. Le budget de sortie de la famille passe de 480 à 320 tokens; la limite temporelle reste finie et inchangée. Cela réduit le coût potentiel sans fabriquer de sélection sémantique dans le code.
- Les journaux exposent désormais les noms bornés des problèmes initiaux et résiduels, et non plus seulement leur nombre. Un prochain live permettra donc de distinguer immédiatement chevauchement d'ids, ids invisibles, doublons de page et incohérence d'action.
- La régression Live97 construit huit pages distinctes, laisse les trois anciens champs narratifs vides, fournit un manque explicite et l'action LLM `read_documents`; le contrat est accepté sans problème. Un témoin avec action non rédactionnelle sans manque et `E8` à la fois utile et faible reste refusé.
- Une seconde régression fait retourner deux fois un JSON syntaxiquement valide mais contractuellement invalide. Elle vérifie que le pipeline appelle le repair de décision puis directement le repair de sélection, sans appeler le repair de format, et qu'une sélection LLM valide atteint ensuite le Writer.
- Validation à ce palier : compilation complète sans avertissement ni erreur; 6/6 tests focalisés; 232/232 tests d'architecture, de récupération et de pipeline; 391/391 tests du filtre historique `SourceBacked|OpenAi|ApiClient`. Le premier passage large a détecté un prompt de 9 071 caractères pour une limite de 9 000; le contrat a été raccourci, recompilé puis validé dans la passe 232/232. Le filtre historique a ensuite détecté l'assertion de budget restée à 480; elle a été alignée sur les 320 tokens du contrat compact, puis le test ciblé et les 391 tests ont réussi.
- Prochaine preuve discriminante : terminer le filtre RAG/OpenAI/ApiClient, puis lancer Live98. Le live doit reproduire l'axe 5 x 4 et les quatre recherches, mais accepter directement une mémoire centrale valide ou borner son échec, ouvrir les pages sélectionnées, reconstruire un `EvidenceBundle` enrichi et atteindre le Writer avant le timeout global.

### 2026-07-23 - Live98 : lecture exacte fichier/page validée, mais décision `answer` sans ids après enrichissement

- Journaux processus `artifacts/live-test-process-live98-20260722-234358/`, artefact lisible `artifacts/client-live-final-weekly-meal-plan-20260722-214402/answers-readable.txt`, trace principale `rag-20260722214437984-a5db58d9`, trace SourceBacked `sbrag-a548f89c075b400ba9d1484443b69004` et runtime local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260722_234418.log`.
- Le pipeline termine proprement après 2 654,2 s, soit environ 44 min 14 s; le test échoue après 44 min 49 s sur `Assert.NotNull()` parce que le payload de sources est nul. La réponse terminale sûre contient 498 caractères, le runtime géré est arrêté et aucun processus Live98 ne reste actif.
- Le routeur choisit `rag.multi_search`, le français et le mode strict. Le Planner initial dure 311,8 s et la revue d'intake 205,8 s. Cette revue ne réinvente pas `Déjeuner`; après les adjudications et patches d'ancres, le comptage aveugle confirme exactement quatre colonnes et la structure finale conserve Lundi à Vendredi avec `Petit-déjeuner | Dîner | Souper | Collation`.
- Le plan compact contient quatre requêtes. L'audit les reformule d'abord en questions génériques, et les quatre jurys atomiques retournent correctement `containsItemIdentifierAndDetails=false`. Le premier domaine `menus` est refusé; le second domaine `recettes` avec `nom de la recette, temps de cuisson et ingrédients` obtient les cinq verdicts positifs. Le repair de rendement produit quatre requêtes fortes, leurs quatre jurys retournent `true`, puis l'audit de portée les rattache à `Cuisine`.
- Les recherches Petit-déjeuner, Dîner, Souper et Collation commencent environ 22 min après le début du test. Petit-déjeuner atteint encore le timeout de 35 s et retourne zéro hit; Dîner, Souper et Collation retournent respectivement 8, 9 et 10 hits. Une recherche générale supplémentaire, décidée par le LLM au premier tour du Judge, retourne 10 hits.
- Après cette cinquième recherche, le Judge passe de `need_more_evidence` à `answer`, mais sans sélectionner d'id. La nouvelle mémoire compacte retourne alors directement `recommendedNextAction=read_documents`, sept ids utiles et un manque explicite. Son contrat est accepté sans repair de format ni repair de décision.
- Le pipeline exécute quatre lectures `documents.context` choisies par le LLM avec leurs ancres complètes : `chefbot_livre_de_recettes_fr.pdf` page 34, `si-on-cuisinait.pdf` page 56, `Tag249008277_1_MOULINEX_HF93D810_8020007485_IFU.pdf` page 135 et `chefbot_livre_de_recettes_fr.pdf` page 11. Les quatre appels retournent `found=true`, `anchorFound=true` et un contexte `around_chunk` portant document, page et texte indexé.
- Cette étape valide le chaînon qui manquait à Live97 : les bons candidats de recherche peuvent maintenant être transformés en lectures exactes avec `docId`, `docPath`, `chunkId`, `pageStart` et `pageEnd`. L'`EvidenceBundle` reconstruit après lecture contient 52 éléments, 9 tentatives de récupération, une seule opération dégradée ou expirée, et les outils `rag.search` plus `documents.context`.
- Au troisième tour, le Judge retourne encore `decision=answer` mais zéro id sélectionné. La mémoire de statut voit six ids utiles et aucun manque, mais omet `recommendedNextAction` et contient deux ids du même groupe document/page (`E2` et `E27`). Le code relève exactement trois défauts mécaniques : action hors enum, action non rédactionnelle sans manque, et duplication de groupe visible.
- Une seule réparation décisionnelle est tentée et retourne exactement le même statut invalide. Contrairement à Live97, cet échec ne déclenche jamais le repair de format : la mémoire est abandonnée, puis le repair de sélection prend la suite. La séparation des erreurs et la borne locale sont donc validées en live.
- Le repair de sélection, le repair d'action du Judge, l'arbitrage de budget puis le repair final ne réussissent pas à associer les ids utiles à la décision `answer`. La dernière décision est repliée sur `need_more_evidence` sans requête exécutable; le contrôleur s'arrête à la troisième ronde avec `reason=no_executable_follow_up`.
- La réponse finale affirme que les sources sont insuffisantes et cite des messages de diagnostic internes en anglais. Elle expose zéro source malgré les pages effectivement lues. Ce résultat reste incorrect pour l'utilisateur et ne constitue pas un succès, mais il déplace précisément le verrou : la provenance mécanique est présente et lisible; la panne se situe maintenant dans l'atomicité de l'action finale et le transfert des ids utiles vers la sélection du Writer.
- Prochaine correction : conserver la décision sémantique avec le LLM, mais isoler la récupération d'une action manquante dans un micro-jury à enum unique au lieu de lui faire régénérer les six champs. La déduplication de plusieurs ids représentant le même document/page peut être normalisée mécaniquement, comme le fait déjà le contrat de sélection pour les sources visibles dupliquées. Une mémoire disant « six preuves utiles, aucun manque » devra alors recevoir une action LLM explicite, garder au plus un id par groupe visible et alimenter la sélection `answer` sans nouvelle boucle de Judge.
- Régression attendue avant Live99 : un statut complet sauf action, contenant deux ids du même groupe document/page, déclenche exactement un micro-jury d'action; le verdict `answer` est conservé, les doublons mécaniques sont réduits sans choix de pertinence, les ids restants atteignent le Writer, et aucun repair de statut complet ni nouveau tour de retrieval n'est exécuté.

### 2026-07-23 - Préflight Live99 : action finale atomique et transfert direct des ids LLM vers le Writer

- Une nouvelle décision atomique `SourceBackedEvidenceStatusActionDecision` contient uniquement `recommendedNextAction`. Son parseur refuse toute clé supplémentaire, toute valeur non textuelle et toute action hors de l'enum `answer|write_partial|need_more_evidence|read_documents|clarify|stop_insufficient_database`.
- Le micro-jury `EvidenceStatusReviewActionRepair` n'est appelé que lorsque l'action de la mémoire est absente ou inconnue, que des ids utiles sont déjà présents et que le même statut serait mécaniquement valide avec une action autorisée. Un chevauchement utile/faible, un id inconnu, un id non visible ou toute autre incohérence continue de passer par le repair complet existant.
- Le prompt atomique traite les listes d'ids, les manques, l'évaluation de recherche et les raisons du premier LLM comme immuables. Il expose seulement l'autorisation de réponse partielle, les tours de retrieval restants et le nombre de lectures documentaires déjà terminées. Le second LLM choisit l'action; le code ne peut ni déclarer les preuves suffisantes ni changer leur pertinence.
- Avant ce micro-jury seulement, le code applique une normalisation physique : lorsque plusieurs ids utiles ont le même `VisibleSourceKey` document/page, il garde le premier id dans l'ordre classé par le LLM et retire les suivants. Cette opération réutilise la règle déjà appliquée avant écriture; elle ne compare ni le contenu, ni la recette, ni la qualité sémantique.
- Si le micro-jury retourne une action cohérente, le statut original est conservé avec ce seul champ remplacé. Si sa sortie est invalide ou si l'action reste incohérente avec les manques et le budget, la mémoire est abandonnée pour ce passage; aucun repair des six champs n'est lancé après ce micro-échec.
- Après au moins une lecture exacte, un statut LLM `answer` avec ids utiles et sans manque alimente maintenant `TryBuildAnswerSelectionFromAgentWorkingMemory` avant le repair général de sélection, même lorsque `AllowsPartialAnswer=false`. `write_partial` reste strictement interdit dans ce cas. Les ids sont ensuite normalisés par le contrat mécanique d'écriture et transmis au Writer.
- Le contrat atomique dispose de 96 tokens réels, y compris dans les deux adaptateurs LLM : le plancher générique de 256 reste inchangé pour toutes les autres étapes. Son timeout dédié est de 120 s; le statut complet conserve 320 tokens et 180 s.
- La régression Live98 exécute une recherche, une lecture `documents.context` produisant deux chunks de la même page, un Judge `answer` sans ids, puis un statut avec `E2,E3`, aucun manque et aucune action. Elle vérifie un seul appel atomique, la réponse LLM `answer`, la réduction mécanique à `E2`, l'absence de `EvidenceStatusReviewDecisionContractRepair` et de `EvidenceJudgeSelectionRepair`, puis l'arrivée de `selectedEvidenceIds: E2` au Writer et une réponse source-vérifiée.
- Validation : compilation initiale réussie avec 0 avertissement et 0 erreur. Après l'abaissement du plancher à 96, la seconde commande de build a dépassé sa fenêtre de 240 s après avoir produit les DLL WinUI et tests à jour; aucun processus n'est resté actif. Les binaires frais ont ensuite passé 4/4 tests discriminants, 266/266 tests d'architecture/récupération/pipeline/itération et 392/392 tests du filtre historique `SourceBacked|OpenAi|ApiClient`. `git diff --check` retourne 0; seuls les avertissements EOL hérités apparaissent.
- Prochaine preuve discriminante : Live99 doit reproduire les lectures exactes de Live98, faire retourner une action atomique valide si le statut principal l'omet, sélectionner les ids utiles sans repair général, atteindre le Writer structuré, produire les 20 cellules demandées, conserver les citations `[E#]`, puis exposer un payload de sources dédupliqué avec fichier, page et liens UI.

### 2026-07-23 - Live99 : le Writer conserve enfin les ids, mais `read_documents` est refusé à tort puis le contrôle de type dérive hors domaine

- Journaux processus `artifacts/live-test-process-live99-20260723-005155/`, artefact client `artifacts/client-live-final-weekly-meal-plan-20260722-225202/`, trace principale `rag-20260722225236110-c894c3ed` et runtime local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260723_005218.log`.
- Le test atteint exactement sa borne globale de 55 min et se termine par `OperationCanceledException` dans `EvidenceStatusReview`. Aucun `answers-readable.txt` n'est produit parce que le résultat final n'a pas été atteint. Le runtime géré est arrêté à 01 h 47 min 02 et aucun processus Live99 ne reste actif.
- Le routeur atteint son timeout de 180 s, mais le pipeline SourceBacked s'active correctement. Le Planner reconstruit la grille autoritative de cinq jours par quatre colonnes. Le premier domaine `menus` est refusé; sa réparation sémantique produit `recettes` avec `nom de la recette, temps de cuisson et ingrédients`, puis les cinq jurys de domaine et les quatre jurys de rendement acceptent les requêtes corrigées.
- L'audit de portée choisit `Cuisine`. La recherche Petit-déjeuner expire après 35 s; Dîner, Souper et Collation retournent 8, 9 et 10 hits. Les éléments portent bien les ancres `docId`, `docPath`, page et extrait. Le premier Judge dure 91,8 s.
- Le premier statut LLM sélectionne huit preuves utiles, aucune faible, aucun manque et l'action `read_documents`. Cette décision est sémantiquement raisonnable : les preuves paraissent suffisantes, mais le contrôleur veut relire leurs ancres exactes avant rédaction. Le contrat mécanique la refuse pourtant parce qu'il exige actuellement un `missingRequirement` pour toute action non rédactionnelle.
- La réparation complète conserve exactement `read_documents`, huit ids utiles et zéro manque; elle est donc refusée une seconde fois. Le pipeline enchaîne ensuite repair de sélection, repair d'action et retry de contrat. Une recherche Souper supplémentaire retourne un hit, puis la même signature réapparaît avec sept ids utiles, aucun manque et `read_documents`.
- La deuxième réparation complète reproduit encore cette décision. Après un repair de sélection, un repair d'action, un arbitrage de budget, son repair, un troisième statut et de nouvelles réparations, le Writer finit néanmoins par démarrer à 01 h 35 min 55. Environ 18 minutes ont été consommées principalement parce qu'une lecture exacte voulue par le LLM sans manque sémantique a été déclarée incohérente.
- Le Writer termine en 171,3 s. Sa réponse est mécaniquement valide : 866 caractères après normalisation, ids visibles et déclarés identiques `E9,E4,E5,E6`, aucune erreur du `SourceContractVerifier`. C'est la première preuve live que des ids sélectionnés survivent jusqu'au Writer sur ce parcours; Live98 les perdait avant la rédaction.
- Le contrôle focalisé des types de valeurs construit six candidats distincts pour les vingt cellules répétées. Son premier appel consomme 172,7 s et son retry 175,3 s. Les deux sorties sont longues — 906 puis 1 015 caractères — mais le parseur ne trouve aucun `candidateId`; le contrat final signale donc les six `missing_candidate_check`.
- Le pipeline commet alors une seconde erreur de contrôle : il transmet ce contrat incomplet au réparateur sémantique comme si de véritables verdicts `wrong_type` existaient. La liste `REJECTED_CANDIDATES` est vide. Le réparateur choisit `need_more_evidence` et copie presque littéralement le vocabulaire générique de l'exemple de schéma dans une requête `complete supported supported recipe replacements`, sans catégorie.
- Cette requête hors domaine retourne un hit de `Documents contradictoires/PDF/NIST_SP_800_53r4_Old_Controls.pdf`, page 340, portant sur le remplacement de composants système non supportés. Ce résultat est manifestement illégitime pour un plan de repas. Le Judge suivant termine, puis le statut est annulé dix secondes plus tard par la borne globale.
- Le micro-jury d'action ajouté après Live98 n'est pas exercé : l'action n'était ni vide ni hors enum. Le transfert des ids vers le Writer est toutefois validé par une autre voie. Le nouveau verrou est double et plus précis : autoriser `read_documents` comme action de vérification de provenance lorsque des ids utiles ancrés existent, même sans manque sémantique; ne jamais lancer une réparation sémantique ou une recherche à partir d'un contrat de type incomplet.
- Corrections attendues avant Live100 : (1) distinguer `read_documents` des recherches de nouvelles preuves dans le contrat de statut et exiger des ids utiles/ancrés plutôt qu'un manque sémantique; (2) rendre le parseur de verdicts de type tolérant aux enveloppes JSON usuelles sans changer les décisions du LLM; (3) si le contrat reste incomplet, réparer uniquement les candidats manquants avec des micro-jurys atomiques et, en cas d'échec, s'arrêter proprement sans fabriquer de follow-up; (4) retirer tout exemple de requête générique que le modèle peut recopier et conserver le domaine/catégorie dans le prompt de réparation.

### 2026-07-23 - Préflight Live100 : lecture exacte sans faux manque et contrôle de type atomique sans dérive

- Le contrat de statut distingue maintenant la recherche de nouvelles preuves de la vérification d'une provenance déjà sélectionnée. `read_documents` exige au moins un `usefulEvidenceId`, un tour restant et, au moment de l'exécution, une ancre document/page concrète non encore lue; il n'exige plus un manque sémantique artificiel. `need_more_evidence`, `clarify` et `stop_insufficient_database` conservent l'obligation de nommer leurs manques.
- Les prompts initial, format-repair, decision-repair et action atomique expliquent cette distinction au LLM. Le code ne choisit aucune page ou pertinence : le statut LLM classe toujours les ids et demande lui-même `read_documents`; l'exécuteur transforme seulement ses ids immuables en appels `documents.context` lorsque leurs ancres existent.
- La régression existante de lecture documentaire a été renforcée : son statut `read_documents` contient désormais zéro `missingRequirement`, sélectionne `E1,E5,E8`, exécute trois lectures exactes et atteint une réponse source-vérifiée sans `EvidenceStatusReviewDecisionContractRepair` ni `EvidenceJudgeActionRepair`.
- Le parseur du jury de type conserve les décisions LLM depuis plusieurs enveloppes JSON usuelles : tableau `checks`, tableau alias contenant `candidateId|candidate_id|id` et `decision|classification`, ou objet indexé par `V1`, `V2`, etc. Les alias de forme sont normalisés mécaniquement; aucune décision `complete_instance` ou `wrong_type` n'est inventée.
- Le prompt batch fournit maintenant une forme JSON explicite et l'adaptateur live active `response_format=json_object` pour `StructuredValueTypeFitJudge` et ses repairs atomiques. Le jury batch garde 640 tokens et 180 s.
- Si un candidat reste absent, dupliqué, sans décision ou sans raison, seuls les ids concernés sont soumis séquentiellement à `StructuredValueTypeFitAtomicRepair`. Chaque micro-jury voit une seule valeur, son champ demandé, ses citations et ses extraits; il retourne seulement `candidateId`, `decision` et `reason`, avec 160 tokens et 90 s. Les verdicts batch déjà valides restent immuables.
- Le résultat interne porte désormais `ContractValid`. Un contrat encore incomplet après les micro-jurys est rejeté immédiatement comme échec d'adéquation sans follow-up. Il ne peut donc plus entrer dans `StructuredValueTypeRepair`, créer une liste vide de candidats rejetés ni déclencher une recherche générique hors domaine.
- Le prompt de réparation sémantique ne contient plus l'exemple copiable `complete supported replacements`. Il expose les catégories déjà connues et exige une requête réellement écrite depuis les candidats rejetés; les termes de schéma `complete`, `supported`, `replacement`, `candidate`, `evidence` ou `source-domain` sont interdits sauf s'ils appartiennent réellement au sujet utilisateur.
- Validation : compilation réussie avec 0 avertissement et 0 erreur; 7/7 régressions discriminantes; 5/5 chemins historiques directement touchés après réalignement d'une assertion tronquée; 268/268 tests d'architecture, récupération, pipeline et itération; 394/394 tests du filtre historique `SourceBacked|OpenAi|ApiClient`; `git diff --check` retourne 0, hors avertissements EOL hérités.
- Prochaine preuve discriminante : Live100 doit accepter immédiatement le premier statut `read_documents` sans repair complet, ouvrir jusqu'à quatre pages exactes, réévaluer les preuves enrichies, atteindre le Writer plus tôt que Live99, puis obtenir un contrat de type valide grâce au format JSON natif ou aux seuls micro-jurys manquants. Aucune requête hors domaine ne doit apparaître; la sortie finale doit contenir les vingt cellules, les citations `[E#]` et un payload de sources fichier/page exploitable par l'UI.

### 2026-07-23 - Live100 : lectures fichier/page confirmées sans faux manque, puis perte de la sélection finale LLM

- Journaux processus `artifacts/live-test-process-live100-20260723-021055/`, artefact lisible `artifacts/client-live-final-weekly-meal-plan-20260723-001101/answers-readable.txt`, trace principale `rag-20260723001137771-40862ee8`, trace SourceBacked `sbrag-d46b612230d6476fbb687237e934dc5b` et runtime local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260723_021118.log`.
- Le pipeline termine en 2 743,7 s, soit environ 45 min 44 s; le test échoue après 46 min 23 s parce que le payload de sources est nul. La réponse terminale prudente contient 490 caractères, 49 éléments de preuve ont été conservés et le runtime géré est arrêté proprement.
- Le routeur réussit en 67 s avec `rag.multi_search`, mode strict et français. Le Planner dure 246 s et la revue d'intake 201,9 s. La première composition concatène les jours et les repas dans deux fausses citations; les adjudications récupèrent correctement `lundi au vendredi` comme expansion de plage, puis les quatre ancres exactes `Petit-déjeuner`, `Dîner`, `Souper`, `Collation`.
- Le plan compact propose d'abord quatre recherches trop générales. Les quatre jurys de rendement retournent correctement `containsItemIdentifierAndDetails=false`. Le premier domaine `menus` est refusé comme livrable terminal; sa réparation produit `recettes` avec `nom de la recette, temps de cuisson et ingrédients`, puis tous les jurys de domaine et de rendement acceptent les requêtes fortes.
- Les recherches commencent vers 02 h 32 min 35. Petit-déjeuner expire après 35 s; Dîner, Souper et Collation retournent respectivement 8, 9 et 10 hits culinaires. Une recherche Souper supplémentaire décidée par le LLM expire elle aussi. Aucun document NIST ou autre source hors domaine n'est récupéré : le correctif de réparation de type de Live99 n'est pas exercé, mais aucune dérive générique n'apparaît.
- Après l'arbitrage de budget, le statut LLM contient huit ids utiles, zéro faible et zéro manque, mais omet son action. Le micro-jury d'action retourne `read_documents` en 7,9 s. Cette action est acceptée sans repair complet de statut : le faux refus de Live99 est donc corrigé en live.
- Le pipeline ouvre exactement quatre ancres choisies par le LLM : `facilitemps.pdf` page 59, `chefbot_livre_de_recettes_fr.pdf` page 34, `si-on-cuisinait.pdf` page 56 et `Tag249008277_1_MOULINEX_HF93D810_8020007485_IFU.pdf` page 135. Tous les appels `documents.context` retournent `found=true`, `anchorFound=true` et leur contexte indexé avec document et page.
- Au tour suivant, le Judge choisit encore `answer` sans id. Le statut retrouve six ids utiles et aucun manque; son micro-jury choisit encore `read_documents`. Quatre nouvelles lectures exactes sont exécutées : `facilitemps.pdf` page 58 et trois pages Moulinex 134, 135 et 136. La lecture documentaire sans manque sémantique fonctionne donc deux fois consécutivement et reconstruit un bundle de 49 éléments.
- Le dernier Judge retourne `decision=answer`, aucun follow-up, `No notes`, mais encore `selected_count=0`. Le dernier statut identifie sept ids utiles et aucun manque, puis omet à nouveau son action. Cette fois le micro-jury d'action retourne une forme invalide de 55 caractères. Le pipeline abandonne alors tout le statut, y compris ses ids utiles valides.
- Le repair général de sélection dure 71,7 s, le repair final 57,7 s, un autre repair d'action 88 s, l'arbitrage de budget 55,9 s, son repair 54,1 s, un nouveau statut 26,8 s puis un dernier repair général d'environ 119 s. Aucun ne remplit `selectedEvidenceIds`. Le contrôleur s'arrête au quatrième tour avec `no_executable_follow_up`.
- Bilan discriminant : le chaînon mécanique « hit → id → docId/docPath/chunkId/page → documents.context » est désormais validé de façon répétée. Le blocage exact est la perte d'une classification LLM pourtant exploitable lorsque seul son champ d'action atomique est mal formé, suivie de gros prompts de sélection qui décident `answer` mais omettent toujours la liste d'ids.
- Live100 n'atteint ni Writer, ni contrôle de type, ni payload UI. Il ne permet donc pas de revendiquer une réponse correcte malgré la réussite des lectures exactes.

### 2026-07-23 - Préflight Live101 : sélection finale atomique depuis la mémoire utile du LLM

- Si le repair atomique de l'action échoue, le pipeline conserve désormais les classifications LLM immuables `usefulEvidenceIds`, `weakEvidenceIds`, les manques et les raisons. Il ne considère pas l'action invalide comme exécutable et ne transforme pas directement les ids utiles en sélection d'écriture.
- La conservation est bornée : le statut incomplet ne peut ni lancer une lecture, ni déclencher une recherche, ni alimenter directement le Writer. Il sert seulement de contexte sémantique à un second micro-jury LLM lorsque le Judge a déjà décidé `answer`, n'a fourni aucun id, qu'aucun manque ne subsiste et que les lectures exactes ont déjà commencé ou que le budget de tours est épuisé.
- `EvidenceJudgeFinalSelectionAtomicRepair` reçoit uniquement les ids que le premier LLM a classés utiles, leurs sources, pages, preuves/cartes et extraits, plus les raisons du statut. Il choisit lui-même un sous-ensemble classé ou `clarify`. Le code vérifie seulement que les ids sont connus, uniques, inclus dans l'ensemble immuable et compatibles avec les bornes de sélection; il ne juge aucune pertinence.
- Le micro-jury retourne exactement `decision`, `evidenceIdsToUse` et une raison sémantique. Son parser accepte les alias de raison `reason|rationale|explanation`, mais refuse `answer` sans id, `clarify` avec id, une décision hors enum ou une raison vide.
- Le chemin utilise `response_format=json_object`, un budget de 256 tokens et un timeout de 90 s. Il est tenté avant les gros repairs de sélection lorsque les conditions de convergence sont satisfaites; en cas d'échec, les repairs généraux historiques restent disponibles.
- La régression reproduit le verrou Live100 : recherche, lecture exacte, Judge `answer` sans id, statut utile sans action, micro-jury d'action rejeté pour clé supplémentaire, puis sélection atomique LLM de `E2`. Le Writer reçoit `selectedEvidenceIds: E2`, cite E2 et la réponse est source-vérifiée; aucun `EvidenceJudgeSelectionRepair` ni `EvidenceJudgeFinalSelectionRepair` général n'est appelé.
- Validation fraîche : compilation réussie avec 0 avertissement et 0 erreur; 6/6 tests discriminants; 50/50 tests récupération, itérations et transport OpenAI; 270/270 tests architecture, récupération, pipeline et itérations; 396/396 tests du filtre historique `SourceBacked|OpenAi|ApiClient`.
- Prochaine preuve discriminante : Live101 doit reproduire les lectures exactes, puis, si le Judge décide encore `answer` sans ids et si l'action du statut reste invalide, appeler un seul `EvidenceJudgeFinalSelectionAtomicRepair`, sélectionner un sous-ensemble des ids utiles, atteindre le Writer et le contrôle de type sans les longues boucles de Live100. Le succès reste conditionné à une grille 5 × 4 complète, des citations vérifiées et un payload de sources fichier/page exploitable par l'UI.

### 2026-07-23 - Live101 : sélection finale atomique validée, puis Writer incomplet et fallback de réparation destructif

- Journaux processus `artifacts/live-test-process-live101-20260723-032019/`, artefact client `artifacts/client-live-final-weekly-meal-plan-20260723-012025/answers-readable.txt`, trace principale `rag-20260723012102867-946584fb`, trace SourceBacked `sbrag-619c26bccc7148449e7c20f3385ad9a1` et runtime local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260723_032041.log`.
- Le pipeline réel termine en 2 800,9 s, soit environ 46 min 41 s; le test échoue après 47 min 18 s sur `sources=null`. La réponse terminale prudente contient 409 caractères. Le runtime géré et le testhost sont arrêtés proprement.
- Le routeur réussit en 33,8 s. Le Planner dure 254,4 s et la revue d'intake 225,2 s. Les adjudications LLM récupèrent la grille autoritative de cinq jours par quatre créneaux; le contrat final porte `lundi au vendredi` comme `range_expansion` et les quatre ancres exactes `Petit-déjeuner`, `Dîner`, `Souper`, `Collation`.
- Le plan compact puis son audit proposent d'abord quatre recherches trop générales. Les quatre jurys de rendement répondent `containsItemIdentifierAndDetails=false`. Le premier domaine `menus` est refusé comme livrable final; la réparation sémantique produit `recettes` avec `nom de la recette, temps de cuisson et ingrédients`, validé par les cinq jurys. Les quatre requêtes finales sont ensuite validées et rattachées par le LLM à `Cuisine`.
- La recherche Petit-déjeuner expire après 35 s. Dîner, Souper et Collation retournent 8, 9 et 10 résultats culinaires. Une recherche complémentaire `recettes ingrédients Souper Collation`, demandée par le LLM, expire également. Aucun candidat NIST ou hors domaine n'apparaît.
- Le premier statut final contient huit ids utiles, aucun faible, aucun manque et choisit `read_documents` après son micro-repair d'action. Quatre lectures exactes réussissent : `facilitemps.pdf` p.59, `chefbot_livre_de_recettes_fr.pdf` p.34, `si-on-cuisinait.pdf` p.56 et le manuel Moulinex p.135.
- Après arbitrage du budget, le statut suivant contient sept ids utiles, normalisés mécaniquement à six groupes visibles, aucun faible et aucun manque. Il choisit encore `read_documents`; quatre lectures exactes supplémentaires réussissent : `facilitemps.pdf` p.58 et le manuel Moulinex p.134, p.135 et p.136. Le bundle atteint 49 éléments.
- Le dernier Judge décide `answer` avec zéro id. Le dernier statut retrouve six ids utiles `E1,E2,E3,E45,E47,E49`, aucun faible et aucun manque, mais omet encore son action. Le micro-repair d'action retourne une forme invalide de 55 caractères.
- Le correctif Live101 fonctionne exactement comme prévu : `evidence_status_review.classification_preserved_after_action_repair_failure` conserve les six ids; `EvidenceJudgeFinalSelectionAtomicRepair` termine en 41,0 s; la trace confirme `final_selection_atomic_repair_used=True` et `final_selection_atomic_repair_accepted=True`. Le second LLM choisit cinq ids `E49,E47,E45,E2,E1`, tous inclus dans l'ensemble utile immuable. Aucun repair général de sélection n'est appelé.
- Le Writer est enfin atteint en direct et cite les cinq ids sélectionnés, mais sa première matrice ne remplit réellement que lundi et mardi; mercredi à vendredi sont vides et quatre cellules de lundi recopient les libellés de colonnes. Le `SourceContractVerifier` refuse `missing_structured_cell` et `thin_structured_cell`.
- Le premier repair structuré remplit les vingt positions, mais laisse les quatre libellés de lundi comme valeurs. Le second repair améliore encore la matrice mais conserve au moins `Petit-déjeuner [E49]` comme cellule mince. Les deux sorties restent citées et beaucoup plus proches d'une réponse acceptable que le draft initial.
- Le pipeline lance ensuite le repair général complet avec un prompt de 12 312 caractères. Cet appel expire après 210 s et retourne le texte technique de timeout. Le code remplace alors la meilleure matrice partielle de 681 caractères par ce résultat vide, ce qui transforme une seule erreur `thin_structured_cell` en trois erreurs `missing_citations` / `requested_structure_not_realized` et force l'abstention finale sans sources.
- Bilan discriminant : la chaîne « ids utiles du LLM → sélection finale LLM → Writer → citations visibles » est validée en conditions réelles. Le verrou fichier/page et la perte d'ids sont tous deux fermés. Le prochain défaut est la réparation de quelques cellules structurées : régénérer toute la matrice deux fois puis lancer un prompt général volumineux est lent, fragile et destructif en cas de timeout.
- Correction suivante : ajouter un micro-repair LLM atomique des seules cellules minces après la première matrice complète. Il doit recevoir les références ligne/colonne fautives, leur valeur actuelle, les preuves autorisées et les cellules valides à préserver; le LLM choisit les remplacements concrets. Le code vérifie seulement les références attendues, les ids autorisés, la non-duplication, les bornes de texte et la conformité mécanique, puis fusionne les patches sans choisir de plat. En cas d'échec ou timeout, conserver le meilleur draft précédent; ne jamais le remplacer par un message technique moins valide.
- Prochaine preuve discriminante : les tests doivent reproduire une grille complète avec une ou plusieurs cellules `thin_structured_cell`, prouver qu'un seul micro-repair corrige uniquement ces cellules et qu'un timeout conserve le meilleur draft. Live102 devra ensuite atteindre le contrôle de type des vingt cellules, puis publier les citations et le payload de sources fichier/page.

### 2026-07-23 - Préflight Live102 : patch LLM atomique des cellules minces et conservation monotone du meilleur draft

- Une nouvelle étape `StructuredThinCellAtomicRepair` traite uniquement une matrice structurée déjà complète d'au moins seize cellules lorsque toutes les erreurs restantes sont des `thin_structured_cell`. Elle n'est donc ni un Planner, ni un Writer bis, ni un mécanisme de choix de repas en code.
- Le `SourceContractVerifier` extrait mécaniquement les cellules minces selon les mêmes axes, bornes de mots et règles de citations que la vérification finale. Chaque cellule reçoit une référence immuable `Cxx`, sa ligne, sa colonne, son texte courant et ses ids de preuve; toutes les autres cellules sont explicitement présentées comme valides et à préserver.
- Le prompt atomique expose au LLM les seules cellules à corriger, les cellules valides à ne pas réécrire et les preuves déjà sélectionnées avec leur source, page, extrait et justification. Le LLM choisit lui-même le contenu concret de chaque remplacement et les ids qui le soutiennent. Le code ne choisit aucun plat, aucune recette et aucune pertinence sémantique.
- Le parseur strict accepte uniquement une racine `patches` et, pour chaque patch, les trois champs exacts `claimRef`, `text` et `evidenceIds`. Il rejette toute clé supplémentaire, référence inconnue ou dupliquée, texte absent, id vide, inconnu ou hors des preuves sélectionnées.
- Le contrat mécanique exige exactement un patch par cellule mince, un texte concret de deux à huit mots sans citation incorporée et au moins un id autorisé. Après validation, la fusion remplace seulement les claims référencés, conserve toutes les autres cellules bit pour bit et reconstruit la matrice avec les citations visibles correspondantes.
- Cette étape est tentée une seule fois après le premier repair focalisé lorsque celui-ci a produit les vingt cellules mais laisse au plus huit cellules minces. Si son résultat est valide, la boucle structurée s'arrête. S'il est invalide, le draft précédent reste intact et le second repair focalisé historique demeure disponible.
- Le repair général final est maintenant monotone sur le contrat mécanique : sa sortie est vérifiée avant tout remplacement. Si elle augmente le nombre d'erreurs — notamment lorsqu'un timeout transforme une matrice presque valide en message technique — le meilleur draft précédent est conservé et la trace porte `previous_draft_preserved=True`.
- L'appel atomique utilise le format JSON natif, un budget de 480 tokens et un timeout de 120 s. Ces bornes sont adaptées à quelques patches courts et évitent le prompt général de 12 312 caractères qui avait expiré pendant Live101.
- Deux régressions de pipeline reproduisent les défauts live : la première part d'une grille 5 × 4 complète dont `C01` contient seulement `Petit-déjeuner [E49]`, vérifie que le LLM la remplace par `Crêpes de base [E49]` et que les dix-neuf autres cellules restent inchangées; la seconde force une sortie atomique invalide puis un timeout général et vérifie que la matrice complète la plus valide est conservée.
- Les tests d'architecture contrôlent en plus la propriété LLM-led du prompt et du parseur strict; les tests OpenAI contrôlent le budget et `response_format=json_object`. Validation fraîche : compilation 0 avertissement / 0 erreur; 3/3 tests discriminants; 243/243 tests architecture, pipeline et transport directement concernés; 399/399 tests du filtre historique `SourceBacked|OpenAi|ApiClient`; `git diff --check` retourne 0 hors avertissements EOL hérités.
- Critère de succès Live102 : reproduire les recherches culinaires et les lectures exactes fichier/page de Live101, conserver la sélection finale atomique des ids utiles, atteindre le Writer, corriger les seules cellules minces par patches LLM sans régression, puis atteindre `StructuredValueTypeFitJudge` avec vingt valeurs sémantiquement recevables. La réponse finale doit contenir les vingt repas demandés, des citations `[E#]` vérifiées et un payload de sources non nul exposant fichier, page et liens UI. Toute abstention prudente, cellule vide, libellé de colonne utilisé comme repas, perte de citation ou absence de payload reste un échec.

### 2026-07-23 - Live102 : provenance et sélection confirmées, garde-fou général validé, mais vingt cellules minces dépassent la borne atomique

- Journaux processus `artifacts/live-test-process-live102-20260723-043251/`, artefact client `artifacts/client-live-final-weekly-meal-plan-20260723-023258/answers-readable.txt`, trace principale `rag-20260723023336486-faa6480a`, trace SourceBacked `sbrag-bb2b856634024b11a9179f314d71bb45` et runtime local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260723_043316.log`.
- Le tour applicatif termine en 3 141,6 s, soit environ 52 min 22 s; le test échoue après 53 min sur `Assert.NotNull()` parce que le payload de sources est nul. Le testhost et le runtime géré sont arrêtés proprement, sans processus résiduel.
- Le routeur dure environ 60 s et choisit `rag.multi_search`, mode strict, français. Le Planner dure 247,3 s et sa revue d'intake 261,0 s. L'intake LLM retrouve correctement les cinq jours et les quatre créneaux; les micro-adjudications réparent seulement leurs citations et relations jusqu'au contrat accepté `row:Lundi..Vendredi:range_expansion:lundi au vendredi` plus quatre ancres de colonne exactes.
- La préparation des requêtes est fonctionnellement sûre mais trop lente. Le premier audit et son repair expirent chacun à 150 s; une stratégie longue expire elle aussi à 150 s. Aucune requête faible n'est exécutée. Un nouveau plan compact sous `Cuisine` est audité; les quatre jurys de rendement refusent les questions génériques. Le domaine `menus` est rejeté comme livrable final, puis le LLM choisit `recettes` avec `nom de la recette, temps de cuisson et ingrédients`, accepté par les cinq jurys.
- Les quatre requêtes fortes sont ensuite acceptées. Petit-déjeuner expire après 35 s; Dîner, Souper et Collation retournent 8, 9 et 10 résultats. Une recherche complémentaire Souper/Collation décidée par le LLM retourne 11 résultats. Tous les résultats restent culinaires; aucun document NIST ou candidat hors domaine n'apparaît.
- Le premier statut de mémoire classe huit ids utiles, zéro faible et zéro manque; le micro-jury d'action choisit `read_documents`. Quatre lectures exactes réussissent avec `found=true` et `anchorFound=true` : `facilitemps.pdf` p.59, `chefbot_livre_de_recettes_fr.pdf` p.34, `si-on-cuisinait.pdf` p.56 et le manuel Moulinex p.135.
- Le deuxième statut classe sept ids utiles, normalisés à six groupes visibles, zéro faible et zéro manque; il choisit encore `read_documents`. Quatre autres lectures exactes réussissent : `facilitemps.pdf` p.58 et le manuel Moulinex p.134, p.135 et p.136. Le bundle final contient 49 éléments et 13 tentatives/outils de retrieval, dont un timeout initial.
- Après le Judge final et l'arbitrage de budget, le dernier statut retrouve sept ids utiles, aucun faible et aucun manque, mais omet son action. Le repair atomique d'action échoue sur une forme invalide de 55 caractères. La classification est préservée; `EvidenceJudgeFinalSelectionAtomicRepair` termine en 45,2 s et la trace confirme `final_selection_atomic_repair_used=True` et `final_selection_atomic_repair_accepted=True`. Le Writer démarre directement et cite quatre ids.
- Le Writer termine en 77,9 s. Il produit seulement quatre lignes et remplit les cellules avec les libellés `Petit-déjeuner`, `Dîner`, `Souper`, `Collation`; vendredi manque. La vérification retourne `missing_structured_cell` et `thin_structured_cell`.
- Le premier repair focalisé termine en 111,1 s et reconstruit les vingt positions, mais les vingt valeurs restent les quatre libellés répétés. La seule catégorie d'erreur restante est `thin_structured_cell`; le draft contient 483 caractères et des citations cohérentes.
- Le patch atomique Live102 ne s'exécute pas parce que sa borne exigeait au plus huit cellules minces. Ce garde-fou avait été conçu pour quelques corrections locales, mais il laisse sans chemin ciblé une matrice complète où les vingt choix sémantiques sont mauvais.
- Le second repair focalisé termine en 95,9 s et régresse : vendredi redevient vide, la réponse descend à 431 caractères et les erreurs redeviennent `missing_structured_cell` plus `thin_structured_cell`. La boucle remplace pourtant le premier draft, car sa monotonie n'était appliquée qu'au repair général.
- Le repair général de 11 903 caractères termine en 65,0 s avec un simple paragraphe de 186 caractères et cinq citations, sans grille. Le nouveau garde-fou fonctionne réellement en live : la trace porte `LLM repair pass regressed mechanically; previous draft was preserved.` La sortie générale destructrice n'est pas adoptée. Le pipeline s'abstient néanmoins, car le draft focalisé conservé reste invalide; la réponse terminale prudente contient 483 caractères et `sources_payload=none`.
- Bilan discriminant : les deux corrections précédentes sont validées en live — sélection finale atomique et repair général non destructif. Le nouveau patch d'une cellule n'est pas invalidé, mais il n'est pas exercé à cause de la borne 1–8. Le verrou suivant est donc générique et mécanique : réparer une grande quantité de cellules minces sans confier leur sens au code, et conserver le meilleur draft entre toutes les passes focalisées.

### 2026-07-23 - Préflight Live103 : lots atomiques LLM bornés et monotonie de toute la boucle structurée

- `StructuredThinCellAtomicRepair` accepte maintenant toute matrice complète d'au moins seize cellules dont les seules erreurs sont des cellules minces. Le code ne demande jamais plus de huit patches par appel et sélectionne mécaniquement les premières références `Cxx` encore fautives dans l'ordre de la grille.
- Après chaque sortie, le parseur et le contrat stricts restent identiques : exactement un patch par référence demandée, deux à huit mots, aucun id dans le texte, au moins un id de preuve connu et sélectionné. Le LLM choisit seul chaque valeur et sa preuve; le code fusionne seulement les champs validés.
- Si le nombre de cellules minces diminue, un nouveau lot atomique peut être demandé sur les références restantes. Quatre lots maximum autorisent jusqu'à trente-deux corrections tout en bornant chaque sortie JSON. Si un lot est invalide, régresse ou ne réduit pas le nombre de cellules fautives, le traitement atomique s'arrête et le draft précédent reste intact.
- Le prompt distingue maintenant `OTHER_CELLS_TO_PRESERVE_UNCHANGED_THIS_BATCH` : les cellules déjà valides et les cellules minces réservées à un lot ultérieur doivent toutes rester inchangées pendant le lot courant. Cette formulation n'affirme plus à tort que toutes les cellules hors lot sont déjà valides.
- Une mesure mécanique commune compare les drafts sur le nombre concret de défauts structurés — cellules demandées manquantes, cellules minces et autres erreurs — puis sur le nombre de catégories d'erreurs. Elle ne juge ni le nom d'un plat ni sa pertinence.
- Cette mesure est appliquée au patch atomique, à chaque passe focalisée et au repair général. Le cas Live102 où le premier repair avait vingt cellules présentes et une seule catégorie d'erreur, puis le second quatre cellules manquantes et deux catégories, conserve maintenant le premier draft et émet une trace `structured_focused` avec `previous_draft_preserved=True`.
- La régression principale part d'un Writer incomplet, produit vingt libellés minces après la première passe focalisée, puis fait choisir au LLM les valeurs `C01–C08`, `C09–C16` et `C17–C20` en trois lots. Elle vérifie une seule passe focalisée, trois appels atomiques, une diminution 20 → 12 → 4 → 0, aucune réparation générale et une grille source-vérifiée.
- Une seconde régression force un lot atomique invalide puis un deuxième repair focalisé qui réintroduit vendredi vide; elle vérifie que le draft complet antérieur est conservé avant le repair général. Validation fraîche : compilation réussie avec 0 avertissement et 0 erreur; 4/4 tests discriminants; 245/245 tests architecture, pipeline et transport directement concernés; 275/275 tests architecture, récupération, pipeline et itérations; 401/401 tests du filtre historique `SourceBacked|OpenAi|ApiClient`.
- Critère Live103 : atteindre de nouveau la sélection finale et le Writer, puis, si la première matrice complète contient jusqu'à vingt libellés minces, observer plusieurs `structured_thin_cell_atomic_repair` dont chaque lot réduit strictement le compteur. La réponse doit ensuite franchir `StructuredValueTypeFitJudge`, conserver vingt instances sémantiques acceptées, publier les citations visibles et fournir un payload de sources fichier/page non nul. Si un lot LLM ne progresse pas, le système doit conserver le meilleur draft et échouer proprement sans le détériorer.

### 2026-07-23 - Live104 : intake 5 x 4 récupéré, mais quatre timeouts client juste avant la réponse réelle du backend

- Live103 avait été interrompu volontairement pendant la deuxième série de lectures exactes et ne constituait ni un succès ni un échec. La reprise a donc commencé par une compilation fraîche, les quatre régressions discriminantes et la suite directe : 0 avertissement / 0 erreur, 4/4 et 245/245 avant tout nouveau live.
- Live104 utilise `artifacts/live-test-process-live104-20260723-204957/`, `artifacts/client-live-final-weekly-meal-plan-20260723-185003/answers-readable.txt`, la trace principale `rag-20260723185042522-e34c10f1`, la trace SourceBacked `sbrag-fa4a9d8f6daf4d61a5247bf9d994bcf7` et le runtime `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260723_205020.log`.
- Le pipeline termine en 1 564,4 s, soit environ 26 min 04 s; le test se termine proprement en 26 min 43 s et échoue sur `sources=null`. Le runtime géré et l'arbre dotnet/testhost sont arrêtés.
- L'intake initial contient cinq jours et quatre colonnes, mais hallucine `Déjeuner` à la place de `Souper` et groupe plusieurs ancres sous une seule citation textuelle. Le contrat mécanique refuse cette forme. Le repair global d'ancres reste invalide, puis les jurys LLM d'axes convergent : l'axe des jours devient `Lundi` à `Vendredi` avec l'ancre de plage `lundi au vendredi`; le patch d'ancres de colonnes supprime le faux `Déjeuner`; le contrôle LLM de complétude compte quatre colonnes attendues et ajoute sémantiquement `Souper`. L'intake final accepté est exactement 5 x 4 : `Petit-déjeuner | Dîner | Souper | Collation`.
- Le premier plan compact recopie `lundi au vendredi` dans chaque requête réutilisable et est refusé par le contrôle mécanique `row_independent_query_copies_typed_rows`. Le repair LLM retire les jours mais produit quatre questions génériques. L'audit LLM les réécrit, les micro-jurys de rendement refusent encore Dîner et Collation, puis le domaine `menus` est rejeté comme livrable final. Le repair sémantique converge vers `recettes` avec les attributs `nom de la recette, temps de cuisson et ingrédients`.
- Le repair LLM final produit quatre requêtes exploitables de la forme `recettes nom de la recette ingrédients temps de cuisson <facette>`. Les quatre micro-jurys de rendement les acceptent et le LLM les scope toutes dans `Cuisine`. Le premier vrai retrieval ne commence toutefois qu'après environ 20 min 19 s; le repair de lignes a pris 134,8 s et l'audit de requêtes 149,2 s, ce qui reste une latence non professionnelle même si les contrats convergent.
- Les quatre appels `rag.search` sont annulés par le client après environ 35 s et retournent chacun `rag_search_query_timeout`, zéro hit. L'`EvidenceBundle` trace exactement `items=0`, `retrieval_attempts=4`, `retrieval_degraded_or_failed=4`, `retrieval_timeouts=4`. L'Evidence Judge demande davantage de preuve; son action répète finalement une requête déjà terminée et est refusée mécaniquement. Le pipeline s'arrête avec `no_executable_follow_up` et répond prudemment : `La recherche documentaire n'a pas abouti dans le délai prévu...`. Il n'invente aucune recette et n'atteint logiquement ni Writer, ni réparation atomique, ni payload de sources.
- Diagnostic hors pipeline, avec le même backend et la même authentification : `/health` répond 200 en 411 ms; `/rag/categories` répond 200 en 324 ms; une requête directe `/rag/search` identique à la facette Dîner répond 200 avec douze items, `queued=0`, `queueWaitMs=0`, un premier ancrage `Je_cuisine_simplement.pdf` page 14 et un payload de 129 767 caractères en 38 583 ms. La preuve décisive est donc que le backend possède les résultats, mais que la borne client de 35 s les annule environ 3,6 s trop tôt.

### 2026-07-23 - Préflight Live105 : timeout d'exploration aligné sur la latence backend mesurée

- `DefaultRagSourceExplorationQueryTimeout` passe de 35 à 60 secondes. Cette correction est exclusivement mécanique et générique : elle ne modifie ni la requête, ni la catégorie, ni les scores, ni les candidats, ni la décision du LLM. Elle conserve une borne finie tout en couvrant la réponse backend réellement mesurée à 38,6 s et une marge de variabilité.
- Un nouveau hook de test expose la valeur effective sans la modifier. La régression exige une fenêtre comprise entre 45 et 90 secondes; les deux tests historiques continuent à imposer artificiellement 50 ms pour prouver que `rag.search` et `rag.multi_search` annulent encore les appels trop lents et publient `rag_search_query_timeout`.
- Validation après changement : compilation réussie avec 0 avertissement et 0 erreur; 3/3 tests ciblés de timeout; 308/308 tests de `SourceBackedRagArchitectureTests`, `SourceBackedRagPipelineTests`, `OpenAiLlmClientTests` et `ApiClientDocumentsTransitionTests`; `git diff --check` retourne 0 hors avertissements EOL hérités.
- Critère Live105 : les quatre recherches identiques à Live104 doivent disposer chacune de 60 s, retourner des hits au lieu d'être annulées juste avant leur réponse et reconstruire un `EvidenceBundle` avec `docId`, `docPath`, `chunkId` et pages. Le test doit ensuite atteindre les lectures exactes, la sélection finale LLM, le Writer et les lots atomiques 20 → 12 → 4 → 0 si le Writer reproduit vingt cellules minces. Le succès reste conditionné à vingt choix sémantiques validés, des citations visibles et un payload de sources fichier/page ouvrable dans l'UI.

### 2026-07-23 - Live105 interrompu après validation réelle des quatre recherches à 60 secondes

- Live105 a été lancé dans `artifacts/live-test-process-live105-20260723-212706/`; son artefact client est `artifacts/client-live-final-weekly-meal-plan-20260723-192711/`, sa trace principale `rag-20260723192744100-26760184` et son runtime local `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260723_212726.log`.
- L'exécution a été interrompue proprement à la demande de l'utilisateur. L'arbre `dotnet` / `vstest` / `testhost`, le runtime `llama-server` et leurs consoles filles ont été arrêtés; aucun processus Live105 connu n'est resté actif. Les traces ont été conservées. Live105 n'est donc ni un succès ni un échec fonctionnel complet.
- Avant l'interruption, le pipeline avait convergé vers quatre requêtes LLM de domaine `Cuisine`, de la forme `recettes nom de la recette temps de cuisson ingrédients <facette>`. Les quatre micro-jurys de rendement les avaient acceptées et l'audit LLM de portée les avait toutes affectées à `Cuisine`.
- La trace confirme que la nouvelle borne effective est bien `timeout_ms=60000` sur chaque appel. Les quatre recherches ont toutes réussi : Petit-déjeuner retourne 12 hits en 38 775 ms, Dîner 8 hits en 21 250 ms, Souper 9 hits en 20 147 ms et Collation 10 hits en 21 637 ms. Les quatre `tool.end` portent `ok=true`.
- Ce résultat discrimine la cause de Live104 : le backend avait effectivement des candidats culinaires et la borne client de 35 secondes était trop courte. Le passage mécanique à 60 secondes supprime les quatre faux timeouts sans modifier les choix sémantiques du LLM.
- L'interruption est survenue immédiatement après le démarrage de `EvidenceJudge`, à `elapsed_ms=680466`. Aucune preuve live n'existe donc encore pour le `EvidenceBundle` final, les lectures `documents.context`, la sélection finale, le Writer, les lots de réparation des cellules minces, le jury de type ou le payload de sources UI.
- Prochaine preuve discriminante : relancer le même scénario sous un nouvel identifiant, vérifier que les quatre recherches réussissent de nouveau, puis laisser l'exécution aller jusqu'à une terminaison naturelle. Toute conclusion sur les vingt cases, les citations fichier/page et les cartes de sources reste interdite avant cette exécution complète.

### 2026-07-23 - Live106 : retrieval corrigé et grille complète, mais Writer batch puis jury de type batch incompatibles avec le petit modèle

- Live106 utilise `artifacts/live-test-process-live106-20260723-214225/`, l'artefact client `artifacts/client-live-final-weekly-meal-plan-20260723-194230/answers-readable.txt`, la trace principale `rag-20260723194301661-948ec076`, la trace SourceBacked `sbrag-bb9969dc032149db8b89b2c5f530b744` et le runtime `C:\Users\MBirchler\AppData\Local\SAAIA\logs\llama-server_20260723_214244.log`.
- Le tour applicatif termine naturellement en 2 978,0 s, soit environ 49 min 38 s; le test termine en 50 min 09 s et échoue à la ligne 95 sur `sources=null`. La réponse terminale de 531 caractères s'abstient au lieu d'inventer. Le runtime géré et l'arbre de test sont arrêtés proprement, sans processus résiduel.
- Le routeur termine en 31,5 s. Le Planner principal dure 107,8 s et la revue d'intake 116,6 s. L'intake reconnaît immédiatement les cinq jours et les quatre créneaux, mais groupe leurs ancres. Le repair global propose à tort une clarification; il est refusé. Les adjudications LLM d'axes et leurs micro-patches convergent vers le contrat exact `Lundi..Vendredi` par expansion de `lundi au vendredi`, puis `Petit-déjeuner | Dîner | Souper | Collation` avec quatre ancres littérales distinctes.
- Le premier plan compact produit quatre requêtes faibles et les quatre jurys LLM de rendement les refusent. Le domaine `menus` est refusé comme livrable final; le repair sémantique choisit `recettes` avec `nom de la recette, temps de cuisson et ingrédients`. Les cinq jurys de domaine, les quatre jurys de rendement et l'audit de portée acceptent ensuite quatre requêtes fortes sous `Cuisine`.
- Le premier retrieval commence néanmoins après environ 12 min 08 s. Les quatre appels utilisent bien `timeout_ms=60000` et réussissent : Petit-déjeuner 12 hits en 40 518 ms, Dîner 8 hits en 22 056 ms, Souper 9 hits en 20 825 ms, Collation 10 hits en 22 336 ms. Le bundle initial contient 25 éléments, 4 tentatives, 0 dégradation et 0 timeout. La correction 35 → 60 secondes est donc confirmée sur deux exécutions live consécutives.
- Le premier Judge décide `answer` sans id tout en écrivant paradoxalement qu'aucune preuve Souper/Collation n'a été trouvée. La revue de mémoire classe `E7` utile, aucun faible, aucun manque, mais omet l'action. Son micro-repair d'action échoue; la classification est conservée. Les gros repairs de sélection et d'action finissent par demander une recherche complémentaire `recettes ingrédients Souper`, toujours sous `Cuisine`; elle retourne 2 hits en 39 563 ms.
- Le bundle enrichi contient 26 éléments et 5 tentatives sans dégradation. Le second Judge décide encore `answer` sans id. La mémoire classe `E7,E8` utiles, aucun faible, aucun manque; son micro-repair choisit `answer`. Le repair de sélection finit par alimenter le Writer (`agent_memory_selection_used=True`), mais aucune lecture `documents.context` n'est demandée sur ce run.
- `StructuredWriter` reçoit un prompt de 4 886 caractères mais expire exactement après 210 s. Sa sortie technique est refusée comme réponse vide, sans citations et sans structure. Le premier repair focalisé dure 58,2 s et produit quatre lignes de libellés répétés, sans vendredi. Le second dure 85,7 s, reconstruit les vingt positions et fournit plusieurs noms de plats, mais laisse quatre libellés minces sur lundi.
- `StructuredThinCellAtomicRepair` reçoit exactement ces quatre cellules; il termine en 52,6 s, retourne 4 patches, réduit les défauts 4 → 0, ne régresse pas et produit une grille mécaniquement valide de 697 caractères avec les citations `E7,E8`. Le mécanisme de patch atomique et la monotonie sont donc validés en live.
- Le jury batch de type de valeur expire exactement après 180 s. Treize micro-jurys atomiques complètent le contrat; dix des treize valeurs distinctes sont rejetées. `StructuredValueTypeRepair` dure 199,0 s, mais le second jury batch expire de nouveau après 180 s et treize nouveaux micro-jurys reproduisent exactement les mêmes dix rejets. La réparation n'apporte donc aucune amélioration mesurable.
- Les raisons LLM montrent une inversion sémantique du contrat : `Smoothie de fruits rouges` est rejeté au motif que `Petit-déjeuner` est un type de repas plutôt qu'un aliment; `Filet mignon aux champignons` est rejeté parce que `Dîner` est un type de repas et la valeur un plat précis. Le modèle confond le nom du champ avec la valeur qu'il doit valider. `Noix de pécan` est en revanche raisonnablement jugé trop mince pour une collation complète. Le résultat mélange donc faux rejets et véritable contrôle de qualité.
- Le repair d'action final ne fournit aucune recherche exécutable. Le pipeline s'arrête avec `llm_answer_adequacy_failed`, publie une abstention prudente et `sources_payload=none`. Live106 prouve le retrieval, l'intake 5 × 4, la grille complète et le patch atomique, mais il ne prouve ni une sélection suffisante de preuves, ni les lectures exactes fichier/page, ni une qualité sémantique acceptable, ni les cartes de sources.

### 2026-07-23 - Recul architectural : génération contrainte, décisions par ids et optimisation de bout en bout

- Le client actuel ne transmet au runtime que `response_format={"type":"json_object"}` lorsque `forceJson=true`. Il ne transmet aucun JSON Schema propre à l'étape. Le petit modèle peut donc produire n'importe quel objet JSON : action absente, enum invalide, cardinalité incorrecte ou ids manquants. Le pipeline compense ensuite par des dizaines d'appels LLM.
- Le runtime live est `llama.cpp build 8149 (a96a1120b)`. La documentation officielle actuelle de `llama-server` expose des réponses contraintes par JSON Schema dans `response_format`, en plus du simple `json_object`. La documentation GBNF précise que le schéma contraint les tokens pendant la génération mais doit aussi être expliqué dans le prompt pour préserver la qualité sémantique.
- La littérature primaire `JSONSchemaBench` rapporte que le décodage contraint peut accélérer la génération et améliorer la qualité aval, tout en rappelant que la couverture des schémas varie selon le moteur. `llguidance`, intégré à llama.cpp lorsqu'il est compilé avec l'option correspondante, fournit une autre voie de grammaire rapide. Ces options doivent être mesurées localement, pas adoptées sur promesse.
- Première décision de recul : ne pas ajouter un nouveau repair à la cascade Live106. Construire d'abord des micro-benchmarks directs contre le runtime réel : `json_object` contre `json_schema`, conformité, exactitude sémantique, tokens, durée et stabilité sur les contrats qui échouent actuellement — action de mémoire, sélection d'ids, grille et jury de type.
- Architecture candidate : transformer les preuves culinaires en cartes canoniques immuables portant `evidenceCardId`, titre exact, document, page, extrait, ingrédients/temps lorsqu'ils sont réellement extraits. Le LLM reste le décideur sémantique et affecte des `evidenceCardId` aux vingt positions. Le code vérifie seulement l'existence et la cardinalité des ids, puis rend mécaniquement le titre exact, la citation, le fichier, la page et le lien UI. Le modèle ne doit plus recopier vingt noms et citations en texte libre.
- Garder un contrôle LLM sémantique compact sur les affectations, mais remplacer les cascades de format-repair par des schémas contraints avec champs requis, enums, bornes d'array et ids autorisés. Un schéma valide ne prouve pas une bonne décision; le SourceContractVerifier, le contrôle de couverture et les preuves restent nécessaires.
- Évaluer au moins `Qwen2.5-3B-Instruct-Q4_K_M` actuel et un candidat Qwen local plus récent sous le même jeu de micro-benchmarks, sans supposer qu'un modèle plus gros ou plus récent est meilleur sur cette machine. La Quadro P520 expose environ 3 376 MiB libres au chargement; toute quantification et tout offload doivent être mesurés avec la latence, la mémoire, la qualité et la stabilité, pas choisis par intuition.
- Étendre l'audit à l'ingestion, l'OCR, les titres, les pages, la segmentation, le recouvrement des chunks, les métadonnées, le retrieval, la diversité et le reranking. L'objectif est la meilleure réponse dans le moins de temps possible, avec une architecture qui améliore aussi les modèles plus grands au lieu d'empiler des compensations propres au 3B.
- Nouvelle règle de conduite : lorsqu'une même classe d'échec se répète, arrêter les rustines locales, prendre du recul, chercher les alternatives primaires, définir des mesures, micro-benchmarker, puis décider. Aucun nouveau live de 40 à 55 minutes ne doit être lancé avant d'avoir une preuve locale que la nouvelle approche réduit réellement les appels et les erreurs.

### 2026-07-23 - Micropreuve JSON Schema sur le build réel

- La microsonde directe utilise le même modèle, le même runtime et les mêmes paramètres essentiels que le live : `Qwen2.5-3B-Instruct-Q4_K_M.gguf`, `llama.cpp b8149 (a96a1120b)`, CUDA, contexte 4 096, 36 couches offloadées, batch 1 024, ubatch 256, 6 threads et flash attention active. Le serveur de sonde est isolé sur le port 1235.
- Le mode actuellement utilisé par l'application, `response_format={"type":"json_object"}`, laisse le modèle inventer le contrat. Sur la question de type de Live106, il comprend correctement le sens mais retourne `{"response":"complete_instance"}` au lieu des clés attendues.
- Deux formes de contrainte ont été distinguées sur le build exact :
  - `response_format={"type":"json_object","schema":<JSON_SCHEMA>}` respecte le schéma ;
  - la forme OpenAI imbriquée `response_format={"type":"json_schema","json_schema":{"name":"...","strict":true,"schema":<JSON_SCHEMA>}}` respecte également le schéma ;
  - le champ racine `json_schema` combiné à un simple `json_object`, ainsi que `response_format={"type":"json_schema","schema":...}`, ne contraignent pas cette route sur ce build et ont rendu `{"isValid":false}`.
- Avec le contrat complet `candidateId + decision + reason`, les deux formes fonctionnelles produisent un objet conforme et la bonne décision `complete_instance`, respectivement en 30,4 s / 56 tokens de sortie et 34,1 s / 57 tokens. Le schéma garantit la forme, pas l'exactitude sémantique.
- Une variante minimale limitée à `candidateId + decision`, avec le prompt français corrigé précisant que le champ est l'emplacement à remplir, a été répétée avec trois seeds. Résultat : 3/3 JSON conformes et 3/3 décisions `complete_instance` correctes pour `Petit-déjeuner: Smoothie de fruits rouges`, en 16,5 s, 13,7 s et 13,8 s. Le coût reste élevé à environ 2 tokens/s, mais il est borné et ne nécessite aucun repair de clés ou d'enum.
- Décision suivante : porter un contrat structuré optionnel dans `ILlmClient`, puis microbenchmarker des décisions groupées par ids. Pour treize valeurs, préférer une partition compacte d'ids ou une liste d'ids rejetés à treize appels séquentiels. Le code ne décide jamais quels ids sont bons ; il valide uniquement que les ids appartiennent à l'ensemble exposé et que le contrat est mécaniquement complet.

### 2026-07-23 - Recul matériel : passer du « meilleur GPU » à une qualification multi-backend mesurée

- Les captures et les sondes locales confirment un i7-10510U, 4 cœurs / 8 threads, 31,8 Gio de RAM, une NVIDIA Quadro P520 avec 4 Gio de VRAM dédiée et un Intel UHD Graphics. Le gestionnaire des tâches affiche jusqu'à 15,9 Gio de mémoire GPU partagée pour l'Intel ; cette valeur est une limite de RAM système partageable, pas 16 Gio de VRAM dédiée et pas une garantie de débit.
- `vulkaninfo --summary` voit les deux adaptateurs : l'Intel UHD comme GPU intégré Vulkan 1.3 et la Quadro comme GPU discret Vulkan 1.4. Le runtime actif est toutefois `win-cuda-x64`. Son `--list-devices` ne voit que `CUDA0: Quadro P520`; il charge `ggml-cuda`, RPC et CPU, jamais l'Intel.
- La limitation est aussi architecturale dans le code. `GpuDetector.TryGetBestGpuAsync` retourne immédiatement la première NVIDIA trouvée par `nvidia-smi`, puis n'énumère pas les autres contrôleurs. Le `HardwareProbeArtifact` ne contient donc qu'un GPU. `LocalLlmBootstrapper` exclut ensuite tout iGPU de `hasDiscreteGpu`; Vulkan n'est sélectionné que pour AMD/Intel discrets ou comme fallback après CUDA.
- La gouvernance locale confirme ce biais : `hardware_probe.json`, capturé le 24 avril, ne décrit que la P520 ; `active-runtime.json` ne contient que `llama.cpp-cuda b8149`; aucun runtime Vulkan n'est installé. Le dernier warmup qualifié date également d'avril et mesure environ 1,16 token/s. Il ne compare ni backend, ni adaptateur, ni modèle.
- Les règles actuelles restent trop statiques pour une adaptation large :
  - la sélection du modèle dépend surtout de paliers de VRAM ;
  - avec exactement 4 096 Mio, la P520 dépasse le seuil `<= 3584` et essaie d'abord la collection appelée `8gb-vram` ;
  - `ngl` devient le nombre total de blocs lu dans le GGUF sans essai de faisabilité préalable ;
  - les paramètres présents dans `ExtraArgs` gagnent silencieusement sur le profil qualifié ;
  - les threads sont déduits de la VRAM et du nombre logique de CPU, sans mesurer topologie, contention, fréquence soutenue, alimentation ou température.
- La cible n'est pas une table contenant des centaines de modèles de PC. Elle est un moteur de capacités générique :
  1. inventorier **tous** les CPU, adaptateurs graphiques, types de mémoire, pilotes, capacités d'instructions et état alimentation/thermique ;
  2. découvrir ce que chaque runtime installé expose réellement via `--list-devices` et un probe de chargement, au lieu d'inférer sa capacité depuis le seul nom du GPU ;
  3. construire les candidats `backend × périphérique(s) × modèle × quantification × contexte × cache KV × batch × ubatch × threads × offload` compatibles ;
  4. éliminer rapidement les candidats qui ne chargent pas ou dépassent une marge RAM/VRAM sûre ;
  5. exécuter un microbenchmark commun comprenant TTFT, tokens/s, mémoire de pointe, température/throttling, stabilité, conformité JSON Schema et quelques décisions sémantiques représentatives du RAG ;
  6. appliquer d'abord des seuils de qualité et de stabilité, puis optimiser la latence, la mémoire et éventuellement l'énergie selon un mode utilisateur `rapide`, `équilibré` ou `qualité` ;
  7. persister le profil gagnant avec l'empreinte de tous les périphériques, pilotes, runtime, modèle et paramètres ; requalifier après toute dérive significative.
- Backends candidats à prévoir par capacités, sans les déclarer gagnants avant mesure :
  - CPU x86 avec le meilleur binaire compatible avec les instructions disponibles ;
  - CUDA pour NVIDIA ;
  - Vulkan comme voie portable pour NVIDIA, AMD et Intel, y compris les iGPU réellement exposés ;
  - SYCL ou une voie Intel spécialisée lorsqu'elle est distribuable et mesurable ;
  - HIP/ROCm pour AMD là où la plateforme le permet ;
  - Metal pour Apple dans une future cible non Windows ;
  - OpenVINO/Intel comme adaptateur optionnel si son contrat de serveur, de modèles et de structured output satisfait le pipeline.
- Le moteur doit aussi benchmarker les formes hybrides utiles : offload partiel GPU + CPU, cache KV quantifié, GPU unique, plusieurs GPU réellement supportés et, sur Vulkan, sélection explicite du périphérique. Il ne faut pas supposer que cumuler un petit iGPU à mémoire partagée et un GPU discret lent accélère l'inférence : les transferts, la bande passante RAM et la contention peuvent rendre cette combinaison plus lente.
- La mémoire partagée doit être budgétée comme de la RAM système, avec une réserve pour Windows, l'UI, le backend, PostgreSQL, l'OCR et l'indexation. Elle ne doit jamais être additionnée naïvement à la VRAM dédiée pour choisir un modèle.
- L'UI et les diagnostics doivent exposer l'inventaire complet, le backend et le périphérique actifs, les raisons du choix, les mesures du profil retenu, les alternatives rejetées et un bouton de requalification. Le mode automatique reste la valeur par défaut ; les paramètres avancés demeurent modifiables explicitement et leur priorité sur le profil doit être visible.
- Critères de validation :
  - tests unitaires avec machines synthétiques CPU seul, NVIDIA 4/8/16/24 Gio, AMD, Intel iGPU, Intel Arc, mémoire unifiée et multi-GPU ;
  - test de détection locale prouvant que P520 **et** Intel UHD figurent dans l'artefact ;
  - installation isolée d'un runtime Vulkan et comparaison contrôlée CUDA/P520, Vulkan/P520, Vulkan/Intel et CPU sur le même modèle ;
  - absence de régression sur une machine sans GPU et fallback propre lorsqu'un backend ne charge pas ;
  - profil final choisi par mesures et contraintes, jamais par une décision sémantique codée.

### 2026-07-23 - Contrat structuré porté dans le client et jury sémantique compact mesuré

- `ILlmClient` accepte maintenant un `LlmStructuredOutputContract` optionnel. Les deux transports OpenAI compatibles envoient d'abord la forme standard imbriquée `json_schema`, réessaient la forme historique `json_object + schema` lorsque le serveur rejette le contrat, puis seulement le `json_object` non contraint en dernier recours. Les chemins sans schéma restent compatibles.
- Le jury de type n'envoie plus treize objets verbeux ni treize appels par défaut. Il partitionne les valeurs immuables par lots de quatre et impose une propriété obligatoire par `candidateId`. Treize valeurs nécessitent donc quatre appels contraints `4 + 4 + 4 + 1`; une réparation atomique n'est déclenchée que pour un identifiant absent, invalide ou explicitement rejeté sans raison.
- La première variante binaire `complete_instance / wrong_type` garantissait la forme, mais reproduisait encore l'inversion sémantique du petit modèle. Une microsonde de quatre valeurs culinaires a rendu quatre faux positifs, y compris l'instruction `Mélanger tous les ingrédients`; une formulation plus sévère a corrigé l'instruction mais rejeté trois véritables plats. Le problème était donc l'espace de décision abstrait, pas le JSON.
- Le contrat expose désormais cinq classes sémantiques explicites : `named_item_suitable_for_slot`, `instruction_or_action`, `isolated_component_or_ingredient`, `heading_or_broad_category` et `incomplete_or_other_wrong_type`. Le LLM choisit la classe; le code ne fait que convertir mécaniquement la première en `complete_instance` et les quatre types incompatibles en `wrong_type`.
- Sur le runtime réel, le lot repas `Smoothie / Filet mignon / Risotto / Mélanger tous les ingrédients` est classé correctement `nommé / nommé / nommé / instruction` en 14,4 s. Un lot multi-domaines `InterCity train / FIT-PTFE_TF_1620-EN.pdf / Tighten... / Industrial components` est classé correctement au niveau acceptation/rejet en 13,1 s après ajout d'exemples contrastifs transport, repas, document et catégorie.
- Les décisions structurées utilisent maintenant un échantillonnage de contrôle déterministe `temperature=0`, `top_p=1`, sans pénalités de fréquence ou de présence. Sur la même microsonde, cette configuration a terminé en 13,9 s contre 25,5 s avec les paramètres de conversation, tout en gardant les quatre décisions correctes. Les réponses rédactionnelles ordinaires conservent leurs paramètres existants.
- Validation ciblée après implémentation : compilation WinUI réussie, puis 22/22 tests transport, fallback, parseur de classes et pipeline par lots réussis. Le serveur de microsonde isolé a été arrêté proprement et le port 1235 est libre.
- Prochaine preuve : exécuter l'ensemble élargi des régressions SourceBacked, puis un live repas seulement après cette validation. Le live doit montrer quatre appels batch au maximum pour treize valeurs normales, aucun faux rejet de plat nommé, des lectures exactes fichier/page et des cartes de sources exploitables.

### 2026-07-23 - Première tranche matérielle implémentée : inventaire hétérogène complet

- `GpuDetector.TryGetGpusAsync` fusionne maintenant les adaptateurs Windows CIM avec toutes les lignes de `nvidia-smi`; la présence d'une NVIDIA ne court-circuite plus l'énumération AMD/Intel. Chaque entrée conserve fournisseur, nom, VRAM réellement dédiée, caractère intégré, pilote, PNP, identifiant stable et indice runtime disponible.
- `TryGetBestGpuAsync` reste temporairement une façade de compatibilité pour les appels non migrés. Il sélectionne un primaire historique dans l'inventaire complet; ce choix n'est pas présenté comme le futur qualificateur de performances.
- `HardwareProbeArtifact` expose désormais `gpuCount` et le tableau `gpus`. Son empreinte inclut tous les adaptateurs, pas seulement le primaire : l'ajout, le retrait ou le remplacement d'un GPU secondaire devient donc visible à la requalification.
- La RAM de l'Intel UHD reste correctement traitée : l'entrée live indique `dedicatedVramMiB=0`; les 15,9 Gio partageables vus par Windows ne sont jamais additionnés aux 4 096 Mio dédiés de la P520.
- Validation déterministe : 23/23 tests ciblés passent après compilation, dont une machine synthétique P520 + Intel UHD et la preuve que le retrait du second adaptateur change l'empreinte.
- Validation locale opt-in `LiveHardwareInventoryProbeTests` : 1/1 passe en 5 s et produit `artifacts/hardware-inventory-live-20260723-2340/hardware-probe.json`. L'artefact courant contient exactement deux adaptateurs :
  - `NVIDIA Quadro P520`, 4 096 Mio dédiés, pilote NVIDIA `582.42`, `CUDA0`, primaire historique ;
  - `Intel(R) UHD Graphics`, 0 Mio dédiés, intégré, pilote `31.0.101.2140`, conservé séparément.
- La sonde locale mesure aussi 32 541 Mio de RAM totale, 16 415 Mio disponibles et un budget DXGI P520 de 3 410 Mio. Ces valeurs sont des observations instantanées, pas des seuils codés.
- `LocalLlmRuntimeCapabilityProbe` interroge maintenant chaque runtime installé avec `--list-devices`, sous un délai de dix secondes, sans déduire ses périphériques du nom Windows. Il parse les ids CUDA/Vulkan/HIP/ROCm/SYCL/Metal/GPU et expose explicitement le backend CPU lorsque le runtime CPU charge son module sans l'énumérer comme périphérique.
- Validation après cette seconde tranche : 25/25 tests ciblés passent. La sonde locale actualisée passe 1/1 en 7 s et produit `artifacts/hardware-inventory-live-20260723-2348/hardware-probe.json` :
  - `llama.cpp-cuda` réussit en 1 670 ms et expose uniquement `CUDA0: Quadro P520 (4095 MiB, 3376 MiB free)` ;
  - `llama.cpp-cpu` réussit en 210 ms et expose le backend `ggml-cpu-haswell.dll` ;
  - aucun runtime Vulkan n'est installé, donc l'Intel UHD est inventorié par le système mais n'est encore exposé par aucun runtime SAAIA.
- Prochaine tranche matérielle : construire un ensemble de candidats de qualification depuis ces périphériques réels, puis installer et comparer Vulkan de manière isolée. Le bootstrap ne devra plus choisir CUDA/Vulkan/CPU depuis le seul primaire historique.

### 2026-07-23 - Profils matériels exprimables et espace de qualification borné

- `QualifiedProfile` peut maintenant exprimer les paramètres qui manquaient pour les machines hétérogènes : liste exacte de périphériques, `split-mode`, répartition `tensor-split`, GPU principal, types de cache KV K/V et parallélisme. Ces champs sont sérialisés, comparés lors de la détection de dérive et matérialisés dans la ligne de commande `llama-server`.
- Un profil qualifié retire d'abord les valeurs manuelles conflictuelles de `ExtraArgs`, y compris `--device`, split, caches et `--parallel`, puis applique ses valeurs validées. Les ids de périphérique et enums CLI sont bornés et normalisés mécaniquement; ils ne peuvent pas injecter de nouveaux arguments.
- Les profils P520 existants ciblent désormais explicitement `CUDA0`, `split-mode=none`, cache `f16/f16` et parallélisme 1. Le profil CPU impose `--device none`. Cela élimine l'ambiguïté « runtime correct mais mauvais périphérique ».
- `LocalLlmQualificationCandidateFactory` ne choisit aucun gagnant. Il construit un espace borné à mesurer à partir des périphériques réellement exposés :
  - CPU minimal, équilibré et contexte long avec cache KV `q8_0` ;
  - accélérateur unique avec offload minimal, partiel, complet et contexte long ;
  - plusieurs accélérateurs avec essais distincts `layer` et `row`, répartition initiale issue de la mémoire libre rapportée, puis validation obligatoire par chargement et benchmark.
- Cette matrice est générique : le même code accepte les ids CUDA, Vulkan, HIP/ROCm, SYCL ou Metal renvoyés par le runtime. Le modèle et la quantification restent une dimension externe; la fabrique est appelée pour chaque modèle gouverné à comparer.
- Validation : 18/18 tests ciblés supplémentaires passent. Les scénarios synthétiques couvrent CPU seul, CUDA, Vulkan Intel + NVIDIA, GPU multiples, limite maximale de candidats, runtime expiré, cache KV quantifié et ligne de commande multi-GPU. Pour un jeu CPU + CUDA simple + Vulkan double, 17 candidats distincts sont produits, sans décision de classement codée.
- Prochaine étape : le qualificateur doit lancer ces candidats en deux phases, d'abord charge/mémoire/stabilité, puis microbenchmarks qualité-latence communs. Seuls les résultats mesurés pourront désigner le profil actif.

### 2026-07-24 - Runtimes actuels isolés, comparaison multi-backend et sonde structurée reproductible

- La dernière release officielle `llama.cpp b10098` a été installée sans écraser le build CUDA précédent : CPU, CUDA 12.4 et Vulkan disposent chacun de leur dossier versionné. Les trois entrées restent `pending_qualification`; l'installation d'un backend ne peut plus le déclarer qualifié avant benchmark et warmup.
- La sonde `--list-devices` du build b10098 observe les capacités réellement utilisables :
  - CUDA : `CUDA0`, Quadro P520, 4 095 Mio dont environ 3 376 Mio libres ;
  - Vulkan : `Vulkan0`, Intel UHD, environ 16 270 Mio de mémoire unifiée rapportée par le runtime, et `Vulkan1`, Quadro P520, environ 4 226 Mio ;
  - CPU : backend x86 présent.
- Le parseur relie maintenant le détail Vulkan `uma: 1` au périphérique correspondant. L'Intel UHD est donc explicitement marqué `MemoryArchitecture=unified` même si la ligne courte `--list-devices` n'emploie pas le mot `shared`; la Quadro Vulkan est marquée `dedicated`. La mémoire Intel reste budgétée comme RAM système partagée.
- Bench bas niveau identique, Qwen2.5 3B Q4_K_M, `p128/n32/r1`, batch 512, ubatch 128, 6 threads, offload complet :
  - CPU : 11,36 tok/s en prefill mais seulement 0,48 tok/s en génération ; éliminé pour le nominal interactif ;
  - CUDA/P520 : 194,32 tok/s en prefill et 12,42 tok/s en génération ;
  - Vulkan/Intel : 15,95 tok/s et 5,38 tok/s ;
  - Vulkan/P520 : 94,86 tok/s et 15,53 tok/s.
- Le résultat Vulkan/P520 illustre pourquoi un seul nombre ne suffit pas : son décodage est plus rapide que CUDA, mais son prefill est environ deux fois plus lent. Sur `p512/n64/r3`, CUDA mesure 208,34 tok/s prefill et 11,90 tok/s génération, contre 93,82 et 15,25 pour Vulkan/P520. Le temps synthétique prefill + génération vaut environ 7,83 s pour CUDA et 9,65 s pour Vulkan.
- Deux essais multi-GPU Vulkan ont réfuté l'hypothèse « plus de GPU = plus rapide » :
  - répartition Intel-majoritaire `4/1` : 17,44 tok/s prefill et 5,23 tok/s génération ;
  - répartition P520-majoritaire `1/4` : 38,82 tok/s et 9,99 tok/s ;
  - les deux restent derrière la P520 seule sur cette charge. Ils sont conservés comme candidats mesurables, jamais promus par règle de marque ou de mémoire.
- `tools/benchmark-llama-structured-runtime.ps1` rend la qualification applicative reproductible pour un exécutable, un modèle, des ids de périphérique, un split, les caches KV, le contexte, l'offload et les tailles de batch. Il lance un serveur isolé, envoie le vrai JSON Schema du jury de type, mesure chaque appel, vérifie le JSON et les quatre décisions attendues, écrit un artefact et arrête toujours le processus.
- La première sonde serveur a découvert une faiblesse masquée par la micropreuve précédente : avec des extraits complets contenant aussi ingrédients et préparation, le 3B classait parfois le texte de l'extrait au lieu de la `VALUE_AS_WRITTEN`. Le prompt général précise désormais que l'extrait prouve le référent sans changer le rôle grammatical de la valeur, avec deux exemples contrastifs non liés à un document particulier.
- Après correction, CUDA/b10098 et Vulkan/P520/b10098 obtiennent chacun 3/3 JSON valides et 3/3 lots entièrement corrects (`Smoothie`, `Filet mignon`, `Risotto` comme objets nommés, `Mélanger...` comme instruction). CUDA termine les trois appels en moyenne en 5,73 s, Vulkan en 9,88 s; le premier appel vaut respectivement 8,06 s et 21,74 s. Les répétitions chaudes de Vulkan profitent davantage du préfixe mis en cache, mais la séquence complète reste plus lente.
- La fabrique multi-GPU ne produit plus une seule répartition basée sur la mémoire libre, qui favorisait excessivement l'iGPU UMA. Elle émet un ensemble borné `capacity`, `balanced` et `prefer-<device>` pour `layer` et `row`; le jeu synthétique CPU + CUDA + Vulkan double passe de 17 à 23 candidats. Aucune variante n'est gagnante avant mesure.
- Validation ciblée après ces changements : 54/54 tests réussissent, couvrant parsing UMA/dédié, espace de candidats, sérialisation/dérive des profils et ligne de commande runtime.
- Prochaine tranche obligatoire : connecter cet espace au qualificateur actif. Un premier étage doit rejeter les échecs de chargement/mémoire avec `llama-fit-params`/`llama-bench`; un second étage doit comparer le petit nombre de survivants sur scénarios serveur froids et chauds, décisions JSON Schema, TTFT, débit, mémoire et stabilité. Le bootstrap actuel garde encore son choix primaire historique et ne constitue donc pas l'adaptation multi-machine finale.

### 2026-07-24 - Premier étage du qualificateur actif et preuve live

- `LocalLlmFitParamsProbe` exécute l'outil livré par le runtime avec le profil exact et parse l'estimation `{modèle, contexte, compute}` par périphérique et pour l'hôte. L'évaluation compare séparément :
  - la VRAM dédiée requise + une marge fournie par la politique au budget libre du périphérique ;
  - les allocations d'un périphérique UMA + les allocations hôte + la réserve système à la RAM système disponible ;
  - un budget absent reste `Indeterminate` et doit passer par un chargement réel, au lieu d'être inventé.
- `LocalLlmQualificationBenchmarkRunner` transforme mécaniquement un `QualifiedProfile` en arguments `llama-bench`, tue l'arbre au timeout, parse les débits prefill/génération et conserve les diagnostics. La présélection emploie `--no-warmup` : le warmup contractuel complet est réservé aux survivants et un CPU lent ne bloque plus plusieurs minutes dans un préchauffage caché.
- `LocalLlmQualificationScreening` groupe les candidats par topologie `runtime + deviceIds + splitMode`, ordonne une échelle de repli et les exécute séquentiellement pour éviter toute contention entre benchmarks. Un échec mémoire essaie le profil suivant; un message explicite de capacité tel que `device Vulkan0 does not support split buffers` ferme immédiatement toute la topologie concernée.
- Un changement de comportement entre builds a été mesuré, sans modifier le profil historique par supposition. Sur b10098/P520, même charge courte alternée et même température :
  - `ngl=36` : environ 4,13 à 5,14 tok/s en génération ;
  - `ngl=37` : 12,40 tok/s ;
  - `ngl=38` : 12,41 tok/s ;
  - `ngl=99` : 12,29 tok/s.
  La fabrique conserve donc deux candidats distincts : `block_count` et `block_count + 1` pour inclure toutes les couches que ce build sait offloader. Le warmup décidera par build; la convention b8149 du CDC n'est pas écrasée.
- L'espace synthétique CPU + CUDA + Vulkan double compte maintenant 26 candidats : trois CPU, cinq par accélérateur unique et huit multi-GPU. Ce nombre reste borné et ne croît pas avec une table de marques ou de machines.
- Preuve live : `artifacts/local-llm-screening-live-20260724-010215/screening.json`, 26 candidats, 6 topologies, 9 tentatives et 5 représentants mesurables. Le test opt-in passe 1/1 et libère tous les processus.
- Classement **de screening court seulement** `p128/n32`, donc non promotionnel :
  1. Vulkan/P520 : 89,11 tok/s prefill, 15,42 tok/s génération, temps projeté 3,51 s ;
  2. CUDA/P520 : 128,10 tok/s, 12,26 tok/s, 3,61 s ;
  3. Vulkan/Intel UHD : 14,82 tok/s, 5,59 tok/s, 14,37 s ;
  4. Vulkan multi-GPU `layer`, pondération capacité : 6,58 tok/s, 2,05 tok/s, 35,05 s ;
  5. CPU : 5,62 tok/s, 0,62 tok/s, 74,78 s.
- Les quatre variantes Vulkan `row` échouent explicitement : l'Intel UHD annonce ne pas supporter les split buffers. La prochaine exécution ne répétera plus les quatre pondérations après ce signal topologique.
- Ce classement ne choisit pas encore Vulkan : les mesures longues précédentes montrent que CUDA gagne sur le prefill lourd. Le second étage doit comparer au moins un scénario de contrôle RAG à sortie courte et un scénario Writer à sortie longue, puis le warmup serveur froid/chaud, la sonde JSON Schema et trois passes stables. Aucun runtime b10098 n'est encore marqué `qualified`.
- Validation actuelle après compilation sans avertissement : 31/31 tests ciblés verts pour inventaire, profils, arguments, estimation mémoire, screening, erreurs de capacité, replis et classement.

### 2026-07-24 - Raffinement multi-charge et validation finale du profil local

- Le second étage ne se contente plus du microbenchmark court. `LocalLlmQualificationRefinement` mesure les deux charges qui représentent le logiciel :
  - contrôle RAG : préfixe long et décision courte, pondération 6 ;
  - Writer : préfixe plus long et génération de 512 tokens, pondération 1.
- Les topologies sont choisies depuis le screening mesuré. Pour chacune, la file compare la famille de base, le nombre de threads de débit, un repli sans Flash Attention et un batch compact. `llama-fit-params` précède chaque benchmark; une topologie explicitement non supportée est arrêtée au lieu d’être répétée.
- Preuve live : `artifacts/local-llm-refinement-live-20260724-0133/refinement.json`, test réussi en 20 min 52 s. Les quatre premiers profils sont :
  1. CUDA/P520, 4 threads, NGL 37, score projeté 170,828 s ;
  2. CUDA/P520, 6 threads, NGL 37, 173,621 s ;
  3. Vulkan/P520, 6 threads, NGL 37, 226,224 s ;
  4. Vulkan/P520, 4 threads, 248,639 s.
- Le résultat explique la différence entre décodage et charge réelle : Vulkan/P520 décode autour de 15 tok/s contre environ 12 tok/s pour CUDA, mais CUDA préremplit environ deux fois plus vite et gagne sur les prompts RAG longs.
- `LocalLlmQualificationFinalValidation` sélectionne les deux meilleurs profils globaux et la meilleure topologie distincte, puis exécute trois rondes avec ordre tournant. Chaque ronde démarre le vrai `llama-server`, mesure son chargement, exécute les trois scénarios de warmup et le contrat JSON Schema sémantique.
- Deux campagnes initiales ont correctement échoué, dans `artifacts/local-llm-final-validation-live-20260724-0210/` et `...-0223-v2/` : les 9 démarrages et warmups passaient, mais le petit modèle classait un ingrédient isolé comme plat nommé. Aucun runtime n’a été promu sur cette preuve imparfaite.
- Après recul et microsonde ciblée, le prompt impose l’ordre de décision : action, composant/ingrédient mesuré, rubrique/incomplet, puis objet nommé seulement s’il remplit indépendamment le champ demandé. La microsonde `artifacts/local-llm-structured-microprobe-20260724-0232-v3/` obtient 4/4 décisions exactes en 8,708 s.
- Campagne finale canonique : `artifacts/local-llm-final-validation-live-20260724-0239-v3/final-validation.json`. Le test live réussit en 6 min 17 s; les 9/9 démarrages, warmups et contrats sémantiques passent. Classement final :
  1. CUDA/P520, 4 threads, NGL 37 : score 179,402 s, médiane structurée 8,574 s, démarrage médian 10,373 s ;
  2. CUDA/P520, 6 threads, NGL 37 : 182,208 s ;
  3. Vulkan/P520, 6 threads, NGL 37 : 238,880 s.

### 2026-07-24 - Promotion gouvernée, profil actif et mémoire unifiée correctement persistée

- `LocalLlmQualificationPromotion` transforme uniquement les finalistes éligibles en profils mesurés. Il dérive des seuils à partir des trois rondes, enregistre les profils avec checksum, exécute `WarmupGate`, qualifie chaque runtime une seule fois et applique le gagnant en dernier afin que le `last-known-good` reste le profil actif.
- Les profils mesurés survivent aux régénérations des valeurs par défaut. `WarmupProfileStore.FindProfileAsync` est utilisé par le statut runtime et le mode maintenance; le démarrage WinUI a été migré vers la même lecture persistée pour ne pas ignorer un profil automatique absent de la table historique.
- Test déterministe : 3/3 tests de promotion, puis 71/71 régressions gouvernance/processus/statut/maintenance. Après ajout du test live et de la persistance du matériel, 14/14 tests ciblés passent.
- Promotion réelle canonique : `artifacts/local-llm-promotion-live-20260724-0321-v4/promotion.json`.
  - profil actif : CUDA0, NGL 37, 4 threads, contexte 4096, batch 1024, ubatch 256, Flash Attention actif ;
  - ligne de commande effective reconstruite depuis le profil et non depuis une heuristique ;
  - runtimes CUDA b10098 et Vulkan b10098 : `qualified` ;
  - runtime CPU b10098 : volontairement `pending_qualification`, car sa campagne finale complète n’a pas été exécutée ;
  - `warmup_profiles.json`, `warmup_results.json`, `last_known_good_profile.json` et `hardware_probe.json` ont tous un checksum logique valide ;
  - le statut runtime rechargé par l’application est sain.
- Le build b10098 n’imprime plus systématiquement `uma: 1` dans `--list-devices`. La sonde corrèle donc mécaniquement les devices runtime avec l’inventaire Windows, d’abord par identifiant runtime puis par nom d’adaptateur normalisé. Elle ne transforme pas une quantité de mémoire en hypothèse de VRAM.
- Preuve live : `artifacts/hardware-inventory-live-20260724-0320-memory-architecture/hardware-inventory.json`, 1/1 réussi. L’artefact gouverné courant conserve :
  - CUDA0/P520 : 4095 Mio, `dedicated`, non partagée ;
  - Vulkan0/Intel UHD : 16270 Mio rapportés par Vulkan, `unified`, mémoire partagée ;
  - Vulkan1/P520 : 4226 Mio, `dedicated`, non partagée.
- Validation après cette correction : build réussi sans avertissement, 16/16 tests déterministes ciblés et 1/1 test d’inventaire matériel live.

### Suite obligatoire pour l’adaptation multi-machine

- [ ] Connecter screening, raffinement, validation finale et promotion dans un service de qualification de production; aujourd’hui la chaîne est complète et prouvée, mais encore déclenchée par tests/maintenance.
- [ ] Remplacer le choix primaire historique du bootstrap par ce service mesuré, avec progression visible, annulation propre et cache par empreinte `{matériel, pilote, runtime, modèle}`.
- [ ] Prévoir deux profondeurs : qualification initiale bornée pour rendre l’application utilisable rapidement, puis optimisation approfondie en arrière-plan/maintenance; aucune promotion ne doit contourner le contrat sémantique.
- [ ] Provisionner les backends applicables par capacité : CPU toujours, CUDA si NVIDIA, Vulkan pour les GPU Windows compatibles; ajouter des adaptateurs distribuables SYCL/Intel et HIP/ROCm sans modifier le cœur du qualificateur.
- [ ] Qualifier un profil CPU de secours sur les machines GPU et le nominal sur une machine CPU seule; ne jamais déclarer le CPU qualifié uniquement parce que l’exécutable démarre.
- [ ] Tester les matrices synthétiques et, quand le matériel est disponible, les preuves live CPU seul, NVIDIA 8/16/24 Gio, AMD, Intel Arc, iGPU UMA, multi-GPU homogène et hétérogène.
- [ ] Exposer dans l’UI la topologie active, les mesures, le profil de repli, la date/empreinte de qualification, les candidats rejetés et une action de requalification.
- [ ] Réduire le coût de compilation du projet de tests : les nombreux fichiers partiels rendent actuellement certaines reconstructions C# longues et opaques; conserver des journaux de build et séparer les assemblages de tests live serait préférable.

### 2026-07-24 - Qualification matérielle adaptative v2 intégrée et prouvée

- [x] `LocalLlmAdaptiveQualificationService` relie maintenant en production l'inventaire, la présélection, le raffinement multi-charge, les rondes serveur/JSON Schema et la promotion gouvernée.
- [x] Le démarrage WinUI exécute un préflight initial borné; la maintenance expose la qualification approfondie. Les deux chemins lisent le même profil mesuré persistant.
- [x] Le provisionnement est piloté par capacités et non par une table de PC :
  - CPU Windows x64 et ARM64 ;
  - CUDA lorsque du matériel NVIDIA est inventorié ;
  - SYCL lorsque du matériel Intel est inventorié ;
  - HIP/ROCm lorsque du matériel AMD est inventorié ;
  - Vulkan pour les adaptateurs graphiques Windows détectés.
- [x] Tous ces runtimes restent des candidats. La marque, la quantité de mémoire annoncée ou l'ordre d'énumération ne désignent jamais le gagnant.
- [x] L'espace borné de 64 candidats est réparti équitablement entre runtime, périphérique et topologie `none/layer/row`; un premier GPU ne peut plus consommer seul le budget.
- [x] L'empreinte de cache comprend maintenant l'architecture du système et du processus, tous les adaptateurs, les pilotes, le runtime et le modèle. Un profil x64 ne peut donc pas être réutilisé comme profil ARM64.
- [x] Le contrat `adaptive_hardware_contract:v2_cpu_cuda_vulkan_sycl_hip_fair` invalide une ancienne qualification qui ne couvrait pas ce nouvel espace.
- [x] La mémoire d'un GPU intégré est qualifiée comme mémoire unifiée et imputée à la RAM système avec réserve; elle n'est jamais additionnée à la VRAM dédiée.
- [x] La politique batterie réutilise le `FallbackProfileRef` réellement mesuré sur la machine au lieu d'un repli P520 codé historiquement.

Preuves déterministes et live :

- [x] Build WinUI Debug x64 après la correction finale : 0 avertissement, 0 erreur, artefact `artifacts/build-production-hardware-backends-20260724-0430-diagnostic-device-fix/`.
- [x] Build du projet de tests synchronisé : 0 avertissement, 0 erreur, artefact `artifacts/build-tests-hardware-backends-20260724-0433-diagnostic-device-fix/`.
- [x] Suite matérielle ciblée : 38/38 tests réussis dans `artifacts/targeted-tests-20260724-0423-hardware-backends-debug-v4/`.
- [x] Inventaire live : 1/1 réussi dans `artifacts/hardware-inventory-live-20260724-0436-multi-backend-v2/`. CUDA expose `CUDA0/P520` en mémoire dédiée; Vulkan expose `Vulkan0/Intel UHD` en mémoire unifiée et `Vulkan1/P520` en mémoire dédiée; CPU est disponible.
- [x] Le runtime Intel SYCL officiel b10099 est installable, mais il est mécaniquement refusé sur cette machine en 206 à 917 ms : Level Zero renvoie `UR_RESULT_ERROR_UNINITIALIZED` (erreur 37). Ce diagnostic reste visible et ne crée plus de faux périphérique.
- [x] Requalification adaptative complète : 1/1 réussie en environ 15 minutes, artefact `artifacts/local-llm-adaptive-qualification-live-20260724-0438-v2/adaptive-qualification.json`.
  - 26 candidats, 6 entrées de screening, 5 topologies mesurables, 4 raffinements, 3 finalistes et 9 rondes serveur/contrat sémantique ;
  - gagnant : CUDA/P520, `CUDA0`, contexte 4096, batch 1024, ubatch 256, 4 threads, NGL 65 et Flash Attention ;
  - repli mesuré : Vulkan/P520, et non un backend choisi par heuristique ;
  - les trois finalistes passent toutes les rondes serveur et structurées.
- [x] Réutilisation du cache : 1/1 réussie dans `artifacts/local-llm-adaptive-cache-live-20260724-0452-v1/adaptive-cache.json`. Décision `qualification_cache_valid`, zéro candidat, aucun benchmark, aucune validation finale, aucune promotion et aucun processus runtime laissé actif.

Reliquats matériels à traiter sans bloquer le retour au RAG :

- [~] Les matrices synthétiques couvrent CPU, NVIDIA, Intel, AMD, UMA et plusieurs GPU; les preuves physiques restent à obtenir sur une vraie machine CPU seule, AMD/HIP, Intel Arc/SYCL et Windows ARM64.
- [ ] Qualifier un vrai profil CPU de secours complet. Le simple démarrage du binaire CPU ne suffit pas pour le déclarer apte à une réponse interactive.
- [ ] Remplacer la présélection historique des collections de modèles par un portefeuille mesurable `{modèle, quantification, backend, profil}`; la taille de VRAM ne doit servir qu'à éliminer les impossibilités mécaniques, jamais à choisir la qualité finale.
- [ ] Prévoir explicitement le repli vers un modèle local plus petit ou un backend distant gouverné lorsque aucun profil local ne satisfait les seuils de qualité et de latence.
- [ ] Exposer dans l'UI la topologie active, les mesures, les candidats refusés et leurs raisons, le profil de repli, l'empreinte/date du cache et l'action de requalification.
- [ ] Séparer à terme les tests live matériels des grandes compilations ToolAgent afin de réduire le coût de validation et rendre la progression plus visible.

### 2026-07-24 - Live 107 : filiation mécanique résolue, alignement sémantique invalidé visuellement

- [x] Régressions pré-live SourceBacked/OpenAI/ApiClient : 407/407 réussies dans `artifacts/sourcebacked-openai-apiclient-regressions-20260724-0458-pre-live107/`.
- [x] Le live 107 termine automatiquement en 13 min 08, contre environ 50 min 09 pour le live 106. Artefact : `artifacts/client-live-final-weekly-meal-plan-20260724-025633/`.
- [x] Les quatre recherches RAG réussissent; le premier appel outil arrive vers 4 min 16 et les sources finales conservent document, page et chunk.
- [x] Le payload UI épingle correctement `Cuisine/Je_cuisine_simplement.pdf`, pages PDF 34 et 35, avec les chunks réels. Le problème historique « bonne source retrouvée mais fichier/page perdus avant l'UI » est donc mécaniquement résolu sur cet essai.
- [ ] Le live 107 **ne vaut pas validation professionnelle** malgré son test automatisé vert.
  - la page PDF 34 rendue visuellement contient `GRUAU TARTE À LA CITROUILLE` et `FRITTATA À FLO`;
  - la page PDF 35 contient `GRUAU CLASSIQUE À JOSIANE` et `FAJITAS DÉJEUNER À JOSIANE`;
  - la réponse leur attribue à tort `Salade de fruits`, `Poulet grillé avec légumes`, `Riz au poulet`, `Banane coco` et `Risotto aux champignons`;
  - les contrôles LLM d'adéquation/type ont produit de faux positifs : ils ont accepté des intitulés libres inexistants sur les pages parce que leurs `EvidenceId` étaient valides.
- [x] Cause localisée : le backend et la compaction transportent déjà `contentCardId`, mais le prompt du writer structuré supprimait cet identifiant et demandait au petit modèle de recréer librement vingt valeurs textuelles.

### 2026-07-24 - Cartes de preuve immuables pilotées sémantiquement par le LLM

- [x] Le planner LLM dispose maintenant de `structuredCellValueMode` et décide entre :
  - `exact_source_item_selection` lorsqu'une cellule doit sélectionner un item nommé complet provenant de la source;
  - `composed_claim` lorsqu'une cellule doit synthétiser une instruction, un attribut ou une explication;
  - `non_tabular` lorsqu'il n'existe pas de grille.
- [x] Le code ne déduit pas ce mode depuis des mots cuisine/repas. Il valide seulement l'enum produit par le LLM et le trace dans l'intake effectif.
- [x] En mode exact, un inventaire canonique associe mécaniquement chaque `V###` à `{contentCardId, titre exact, EvidenceId, document, page, preuve, extrait parent}`.
- [x] Le writer LLM décide quelle référence `V###` convient sémantiquement à chaque cellule. Il ne peut plus générer, paraphraser ou recoller un titre libre.
- [x] Le rendu du tableau remplace mécaniquement chaque référence par le titre immuable de la carte et son `[EvidenceId]`; le code ne choisit aucune valeur métier.
- [x] Le `SourceContractVerifier` refuse toute révision ultérieure dont une cellule n'est pas exactement un titre de carte associé au bon `EvidenceId`. Une carte légitime d'un seul mot n'est plus allongée artificiellement par le contrôle « cellule trop courte ».
- [x] Le sélecteur final LLM reçoit le mode exact et l'instruction de préserver l'inventaire utile et sa variété, au lieu de réduire systématiquement la sélection à la plus petite paire de pages.
- [x] Build après première intégration : 0 avertissement, 0 erreur.
- [x] Cinq tests ciblés prouvent le transport du mode LLM, l'exposition des `contentCardId`, le rendu exact, le rejet du texte libre avec `EvidenceId` valide et le rejet d'une révision tardive non canonique : `artifacts/targeted-tests-20260724-0545-canonical-cards/`.
- [x] Régression Architecture + Pipeline finale : 242/242 réussies dans `artifacts/regressions-20260724-0546-canonical-cards-v3/`. Les deux budgets initialement dépassés ont été corrigés par extraction du parsing dans un partial dédié et recompression du prompt sous sa limite locale.
- [x] Régression élargie SourceBacked, mémoire, orchestration, transports OpenAI, processus llama.cpp et transition ApiClient : 422/422 réussies dans `artifacts/sourcebacked-openai-apiclient-regressions-20260724-0550-canonical-cards/`.
- [x] Preuve pipeline complète ajoutée et réussie : `artifacts/pipeline-tests-20260724-0606-canonical-cards-v3/`, 2/2 tests.
  - le chemin valide transforme vingt choix `V###` en vingt titres source exacts, conserve le fichier, la page et le chunk, puis produit une unique carte source UI vérifiée;
  - le chemin hostile fait inventer explicitement `Salade inventee [E1]` au writer initial, aux deux réparations structurées, puis à la réparation générale;
  - malgré un `EvidenceId` réel et un tableau mécaniquement complet lors de la dernière tentative, le contrat final reste invalide avec `non_canonical_structured_cell`;
  - `SourceBackedUiPayloadMapper.FromVerifiedResult` refuse ce résultat : aucune carte source UI ne peut être produite pour l'invention.
- [x] Régression élargie rejouée après les preuves pipeline : 424/424 tests réussis dans `artifacts/sourcebacked-openai-apiclient-regressions-20260724-0600-canonical-pipeline/`.
- [~] Microtest réel borné du planner/writer exécuté deux fois; il a volontairement empêché le lancement prématuré du live 108.
  - `artifacts/live-canonical-card-microprobe-20260724-0605-v1/` : échec utile. Le Qwen2.5 3B a renvoyé `exact_source_item_selection=named source items` au lieu du jeton exact.
  - Le prompt initial a été rendu atomique et toute grille passe maintenant par un mini-arbitrage LLM focalisé entre `exact_source_item_selection` et `composed_claim`. Le code valide seulement l'enum et effectue une seconde demande LLM bornée si nécessaire.
  - `artifacts/live-canonical-card-microprobe-20260724-0609-v2/` : enum exact et vingt références canoniques mécaniquement valides, mais faux vert sémantique détecté à l'inspection du tableau. Le 3B a notamment placé `Bouchees dattes cacao` dans `Diner` et `Muffin banane avoine` dans `Souper`.
- [x] Contrat LLM de compatibilité carte-colonne ajouté après ce faux vert :
  - un LLM focalisé décide, pour chaque `V###`, les colonnes exactes où l'item source complet est sémantiquement compatible;
  - le code vérifie seulement la complétude du mapping, les ids et les libellés de colonnes;
  - le writer reçoit les ensembles approuvés par colonne;
  - le parseur et le `SourceContractVerifier` refusent ensuite toute référence utilisée dans une colonne non approuvée, y compris après réparation ou révision d'adéquation.
- [x] Build après le verrou carte-colonne : 0 avertissement, 0 erreur.
- [x] Tests ciblés du mode, du mapping LLM et du refus mécanique : 5/5 dans `artifacts/targeted-tests-20260724-0620-canonical-column-compatibility/`.
- [x] Régression Architecture + Pipeline après corrections de fermeture : 247/247 dans `artifacts/regressions-20260724-0635-canonical-column-compatibility-v3/`.
- [ ] Rejouer la microsonde réelle avec le nouveau mapping carte-colonne. Cette étape n'a pas été lancée parce que l'utilisateur a demandé un arrêt propre.
- [ ] Si et seulement si la microsonde prouve les quatre types de repas, lancer le live 108. Le succès exige : mode exact décidé par le LLM, compatibilité carte-colonne approuvée par le LLM, plusieurs cartes canoniques sélectionnées, aucune valeur libre, vingt cellules exactes, pages visuellement cohérentes et variété acceptable.

### 2026-07-25 - Principe directeur : le LLM choisit librement les outils utiles

- [x] Décision utilisateur explicite intégrée : ni le type d'outil, ni un nombre fixe
  d'actions, ni une action par ligne/colonne ne doivent être imposés par du code.
  Le LLM reçoit le registre courant des capacités, la demande, la mémoire de travail,
  les requêtes déjà exécutées, les preuves présentes et les manques encore ouverts.
- [x] Le profil `CanonicalDirect` ne passe plus par le planificateur compact historique.
  Celui-ci reste isolé dans `LegacyCompensated` pour les tests et la transition.
- [x] Le planificateur général peut choisir librement, dans une même décision,
  `rag.search`, `rag.multi_search`, `documents.navigation` et `documents.context`.
  Son tableau d'actions autorise de zéro à cinq actions : zéro est valide pour une
  clarification, et cinq est uniquement un plafond de sécurité, jamais un objectif
  à remplir.
- [x] `targetFacetExact` devient une annotation facultative dans le profil canonique.
  Une recherche générale ou complémentaire n'est plus refusée parce qu'elle
  alimente potentiellement plusieurs colonnes.
- [x] L'inspection lexicale des libellés de lignes dans les requêtes reste un
  mécanisme de compensation du profil historique; elle ne peut plus annuler une
  stratégie sémantique du LLM dans `CanonicalDirect`.
- [x] Une seconde `documents.navigation` n'est plus interdite parce qu'une navigation
  a déjà eu lieu. Seule la répétition mécanique de la même action est rejetée.
- [x] Le juge de preuves reçoit lui aussi le registre d'outils, la mémoire de travail,
  les résultats et les requêtes précédentes. Il choisit les relances utiles sans
  quota par type d'outil. Le runtime conserve un plafond mécanique de cinq actions
  par tour et quatre tours de preuve.
- [x] Le dernier contrat de relance ne demande plus « exactement une action ».
  Il produit le contrat normal `EvidenceJudgeDecision` avec un ensemble libre
  d'actions utiles, structuré par JSON Schema quand le moteur le permet.
- [x] Le dernier réparateur focalisé `StructuredValueTypeFollowUp` utilise maintenant
  le même contrat général `EvidenceJudgeDecision`. Il peut retourner plusieurs
  recherches, navigations ou lectures exactes choisies par le LLM et reçoit le
  manifeste, les lacunes de type, les plans et les tentatives réellement exécutées.
- [x] Les prompts d'adéquation/réparation canoniques ont été audités pour supprimer toute
  formulation prescriptive `1-3`, `exactly one action` ou interdiction générale
  d'un outil; conserver seulement les contraintes de sérialisation, sécurité,
  non-répétition exacte, ancrage documentaire et budget.
- [x] Le planificateur initial et sa réparation de contrat n'imposent plus
  `2-4` recherches, `au plus une navigation`, une combinaison RAG + navigation,
  ni `1-4` actions. Ils demandent au LLM de choisir uniquement les actions utiles.
- [x] Un plan hors budget n'est plus tronqué silencieusement : le contrat mécanique
  le refuse avec le nombre retourné et le plafond, afin que le LLM corrige son plan.
- [x] La mémoire de travail compacte expose jusqu'à dix requêtes/recherches récentes,
  huit lectures documentaires et dix statuts de récupération. La mémoire reste un
  contexte de processus non citable; seules les preuves relues dans le bundle le sont.

### 2026-07-25 - Preuve live du problème avant la correction canonique

- [x] Artefact :
  `artifacts/live-general-tool-contract-x64-20260725-191939/`.
- [x] Le premier plan général du Qwen3 a librement proposé cinq actions mixtes,
  dont `documents.navigation` et plusieurs recherches RAG.
- [x] Ce plan a été refusé uniquement parce qu'une recherche n'avait pas de
  `targetFacetExact`; la relance a ensuite perdu la navigation et produit quatre
  recherches spécialisées. Cette observation prouve que le code détournait encore
  le choix sémantique initial du LLM.
- [x] Le run a échoué après environ 22 min 48 s, cinq recherches, 62 items de preuve
  et zéro source finale. Il a exigé cinq valeurs distinctes compatibles par colonne,
  n'en a trouvé que quatre pour `petit-déjeuner` et deux pour `déjeuner`, puis a
  refusé proprement d'inventer.
- [ ] Ce run n'est pas une validation de qualité. Il démontre trois problèmes :
  contrainte de planification excessive, absence de navigation après réécriture du
  plan et coût prohibitif des classements canoniques séquentiels.
- [ ] Après compilation, relancer le même test avec le profil canonique corrigé et
  vérifier dans la trace :
  - le plan initial du LLM est exécuté sans substitution sémantique;
  - navigation, recherche et contexte peuvent être combinés ou non selon son choix;
  - le juge voit les résultats précédents et cible uniquement les manques restants;
  - un titre de navigation prometteur mène à une relecture exacte fichier/page avant
    de devenir une preuve;
  - aucune source UI n'est créée sans fichier, page et extrait vérifiables.
- [~] Les lots canoniques hérités de Qwen 2.5 ont été agrandis pour Qwen3 :
  `ShortlistBatchSize=30` et `CompatibilityBatchSize=20`. Sur 120 candidats,
  la cible mécanique passe de dix appels de shortlist + quatre de compatibilité
  à quatre + deux, sans déplacer le jugement sémantique dans le code.
  La preuve déterministe de sélection avec vingt cartes passe; la mesure live de
  qualité, latence et robustesse JSON reste obligatoire avant de cocher complètement.

### 2026-07-25 - Validation déterministe du libre choix d'outils

- [x] Compilation Debug x64 réussie avec le nouveau contrat multi-outils.
- [x] Premier lot élargi : 147 tests exécutés, dont 137 réussis. Les dix échecs
  ont séparé des assertions historiques/politiques de taille déjà divergentes des
  défauts fonctionnels; aucun échec de compilation.
- [x] Lot ciblé v2 : 10/11 réussis; l'unique échec attendait encore textuellement
  l'ancienne interdiction de la navigation seule.
- [x] Lot ciblé v3 : 12/12 réussis dans
  `artifacts/targeted-llm-tool-choice-v3-20260725-201425/`.
  Il couvre le profil canonique sans veto lexical/facette, le manifeste complet,
  les réparations multi-outils, une navigation suivie d'une lecture exacte,
  la récupération après action vide, et la sélection canonique rebatchée.
- [ ] Recompiler après les dernières formulations du réparateur de planner, puis
  relancer le test live complet du plan de repas et inspecter directement sa trace.

### 2026-07-26 - Point d'arrêt propre après le live V2 d'audit atomique

- [x] Le live complet a terminé naturellement en 16 min 25 s, sans processus de
  test résiduel. Artefact :
  `artifacts/client-live-final-weekly-meal-plan-20260726-183523/`; trace :
  `rag-20260726183523672-67fff55f`.
- [x] Le pipeline a observé 103 éléments et le LLM a sélectionné une première
  grille de 20 preuves distinctes. Le vérificateur mécanique a validé ses 20
  citations avant la revue sémantique.
- [x] Le système s'est abstenu proprement et n'a publié aucune source lorsque la
  revue sémantique puis les deux sélections corrigées n'ont pas satisfait les
  contrats. Il n'a donc pas transformé un échec de sélection en réponse inventée.
- [ ] Ce live n'est pas une validation du planning. L'audit de candidats a
  surfiltré les cartes : 11/40, puis 7/40, puis 4/20 ont été conservées. La
  réconciliation globale a rejeté de nombreuses recettes uniques déjà approuvées
  dans leur lot, alors que son rôle devait se limiter aux doublons inter-lots.
- [ ] Le juge final a également rejeté à tort neuf titres de recettes nommées au
  motif que leur extrait ou leur description n'était pas complet. Pour une demande
  qui exige seulement des noms de plats, il ne doit pas exiger ingrédients,
  quantités ou méthode lorsque le titre source nommé est déjà prouvé.
- [ ] Après `need_more_evidence`, Qwen3 a tenté deux listes de 20 ids contenant
  respectivement cinq puis six doublons. Le contrat mécanique les a correctement
  refusées, mais le modèle n'a pas convergé avant le tour 12.
- [x] La correction déjà écrite avant la terminaison du live borne désormais la
  réconciliation globale à sa fonction mécanique-sémantique réelle : comparer les
  candidats préalablement approuvés, ne retirer que les doublons sémantiques et
  préserver chaque candidat unique. Elle ne réévalue plus la complétude, le créneau
  de repas ou l'adéquation à la demande.
- [x] Le runner demande immédiatement l'action structurée de sélection lorsque le
  modèle rédige du contenu libre à la place de `submit_evidence_selection`, ou
  lorsqu'une sélection invalide survient malgré un réservoir suffisant.
- [x] Compilation Debug x64 terminée puis suite ciblée
  `SourceBackedAgentV2Tests` rejouée sans rebuild : 40/40 tests réussis en 1 s.
- [ ] À la reprise, renforcer d'abord le contrat du juge final sur la différence
  entre « titre nommé suffisant pour un planning » et « recette détaillée demandée »,
  puis refaire un live avec la réconciliation corrigée. Ne pas commencer une
  nouvelle boucle de rustines si les cartes restent polluées.
- [ ] La passe d'ingestion/indexation/OCR est volontairement différée à la prochaine
  reprise, conformément à la demande d'arrêt. Elle devra partir des défauts live
  mesurés des cartes, pages, titres, limites de blocs et provenances plutôt que
  d'ajouter des heuristiques culinaires.
