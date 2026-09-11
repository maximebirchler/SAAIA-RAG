# Rapport ultra complet — SAAIA RAG — dernières 24 heures

## 1. Périmètre temporel et méthode

- Fenêtre couverte : du **2026-08-29 22:56 +02:00** au **2026-08-30 22:56 +02:00** (Europe/Zurich).
- Dépôt : `C:\Users\MBirchler\Desktop\ecom\SAAIA\GIT\RAG`.
- Branche : `SAAIA_V3.1`.
- HEAD : `5f35881cdc67d12a076fcd2a7a1004656ac9a37a`.
- Sources recoupées : plan d'action dynamique, protocoles préenregistrés, rapports de palier, sorties d'audit, TRX, artefacts live, sessions/traces persistées, reçus Telegram, état Git et état des ressources locales.
- Ce rapport distingue systématiquement : **fait**, **testé**, **observé live**, **corrigé déterministement**, **non revalidé live** et **non approuvé**.

## 2. Résumé exécutif honnête

Les dernières 24 heures ont produit un travail massif de diagnostic, de test-first, de correction générique et de validation. Elles n'ont toutefois **pas rendu le produit RAG globalement approuvé**.

Les résultats les plus importants sont les suivants :

1. Les contrats historiques BUG-130 et BUG-132 ont été validés déterministement, puis le cas Q106 a obtenu un **PASS fonctionnel et sourcé en live**, mais avec une latence inacceptable de **290,155 secondes**.
2. Une longue série de canaris sémantiques V2/V3/V5 a montré que les contrats étaient trop redondants ou que Qwen mélangeait décision, preuve principale, preuve de support et portée. Les échecs ont été conservés au lieu d'être requalifiés.
3. Un recul architectural a conduit au contrat V6 `lead/support`, qui laisse la décision de sens à Qwen et réserve au code les contrôles mécaniques. Le live A545 a obtenu **4/4 cas exacts**, **4/4 protocoles valides**, **7 210/10 000 tokens** et **138 830/180 000 ms**. BUG-133 a été fermé sur ce périmètre causal précis.
4. Le vertical slice réel Q016 sur corpus/WinUI a ensuite échoué. BUG-115 a été corrigé déterministement par la politique de sélection unique V2, mais **n'a pas été revalidé live**.
5. Les canaris réels C001 à C005 ont successivement révélé des défauts génériques que les tests synthétiques ne suffisaient pas à montrer : récupération après scope à rendement nul, contrat routeur, confusion sujet/document, provenance des cartes, budget LLM cumulatif et transition terminale.
6. BUG-134, BUG-135, BUG-136 et BUG-137 ont chacun reçu une chaîne préenregistrement → RED causal → GREEN minimal → régressions → audit → Telegram. Ils sont **corrigés déterministement mais non revalidés live**, sauf la composante terminalité de BUG-134 observée dans une campagne appariée antérieure.
7. Le dernier canari C005 a échoué honnêtement après **112 629 ms** et **7 156 tokens**, sans réponse ni carte source. Il a ouvert BUG-138 :
   - transition incorrecte lorsque seul le budget terminal reste ;
   - coût excessif avant l'étape documentaire ;
   - réparation routeur trop large pour une incohérence limitée à `namedReferenceKind/document`.
8. A606 et A607 sont clos. A608 est en cours. Les trois tests du repair routeur passent et la transition progresse désormais jusqu'au writer, mais le scénario ciblé reste à **4/5** : la dernière exécution atteint une revue sémantique finale non fournie par le double de test. **A608 n'est donc pas encore GREEN ni auditée.**

Verdict global actuel :

- Produit : **`TESTE_NON_APPROUVE`**.
- Phase 5 : **active**.
- Goal : **actif**.
- Canaris C001 à C005 : **interdits au replay**.
- Prochain travail légitime : terminer A608, puis seulement A609 et A610.

## 3. Comment lire les statuts

| Statut | Signification exacte |
|---|---|
| RED causal | Le test échoue avant la modification de production et reproduit le défaut visé. |
| GREEN ciblé | Les mêmes oracles passent après le correctif minimal. |
| PASS déterministe | Les tests et contrats passent sans Qwen ni WinUI live. |
| PASS live causal | Une campagne live préenregistrée a franchi les portes exactes de son protocole. |
| FAIL probant | L'échec live est authentifié par session, trace, logs, budgets et artefacts ; ce n'est pas une absence de preuve. |
| Non revalidé live | Le code est corrigé et testé, mais aucun nouveau canari distinct n'a encore confirmé le comportement réel. |
| TESTE_NON_APPROUVE | Des éléments passent, mais l'expérience produit complète n'est pas encore suffisamment démontrée. |

## 4. Chronologie complète condensée des paliers A420 à A608

### 4.1. BUG-130/BUG-132 et Q106

| Palier | Travail | Résultat |
|---|---|---|
| A420–A424 | Unité de réponse BUG-130 et writer structuré BUG-132, test-first. | RED 0/7, puis filtre final 10/10 et audit statique 47/47. Corrections déterministes closes. |
| A425–A429 | Recherche d'un chemin Banque exploitable pour live. | Aucun live : le chemin réel n'était pas suffisamment valide. Client 1 506/1 506 ; palier clos sans inférence. |
| A430–A434 | Wrapper fail-fast et live unique Q106. | Fonction, faits, identité, sources, mémoire et cartes PASS ; performance FAIL à 290,155 s. |
| A435–A439 | Attribution de latence et transaction sémantique unifiée. | 66/66 ; réduction simulée de deux décisions vers une. Live non remesuré. |

### 4.2. Canaris sémantiques V2/V3 et recul architectural

| Palier | Travail | Résultat |
|---|---|---|
| A440–A444 | Premier canari Qwen de transaction sémantique. | FAIL officiel 0/5 ; faits/IDs bruts corrects mais contrat de décision invalide ; fail-closed PASS. |
| A445–A449 | Alignement `leadEvidenceId` et métriques structurées. | PASS déterministe, audit 78/78 ; live non remesuré. |
| A450–A454 | Second canari distinct. | FAIL 3/5. |
| A455–A459 | Pool global borné et adéquation sémantique V2. | PASS déterministe, audit 104/104 ; dépassement de modularité détecté puis corrigé. |
| A460–A464 | Troisième canari V2. | FAIL 3/5 ; les deux frontières restantes sont sémantiques. |
| A465–A469 | Déclaration explicite du besoin de résolution V3. | PASS déterministe, audit 122/122. |
| A470–A474 | Quatrième canari V3. | FAIL 4/5. |
| A475–A479 | Portée canonique des preuves V3. | PASS déterministe, audit 100/100 ; Qwen reste propriétaire de la complétude et de la portée. |
| A480–A484 | Cinquième canari V3. | FAIL 2/5 ; EvidenceBundle, provenance et fail-closed fonctionnent, mais mauvaise conclusion sémantique possible. |
| A485–A489 | Recul comparatif sur 25 cas officiels. | Décision architecturale PASS : arrêter d'empiler des clauses locales et préenregistrer une V2 plus simple. |
| A490–A494 | Prototype V2 déterministe. | PASS déterministe ; `answer/research/clarify/context`, writer/reviewer et publication fail-closed. |
| A495–A499 | Canari live V2 apparié. | Non probant ; aucune promotion. |
| A500–A504 | Correction du harnais et nouveau canari V2. | FAIL live 0/5, fail-closed ; seulement 2/5 décisions brutes correctes. |
| A505–A509 | Contrat V5 discriminant minimal. | RED partiel, puis 31/31 ; 538/538 ciblés et client 1 571/1 571. Live V5 encore non exécuté. |
| A510–A514 | Premier live V5. | Contrat 5/5 PASS ; décisions+IDs 4/5 ; sémantique FAIL, writer positif non atteint, sécurité publication PASS. |

### 4.3. BUG-134 initial, expériences d'ordre et handoff juge→writer

| Palier | Travail | Résultat |
|---|---|---|
| A515–A519 | Terminalité mécanique quand le budget outil est épuisé. | RED 1/3 puis GREEN ; 551/551 ciblés, client 1 584/1 584. BUG-134 déterministe PASS ; BUG-133 reste ouvert. |
| A520–A525 | Expérience appariée `decision-first` vs `reason-first`. | Contrôle 4/5, variante 3/5 : ordre `reason-first` rejeté. BUG-134 terminalité observée live dans cette campagne. |
| A526–A530 | Handoff sémantique du juge vers writer. | RED 1/3 puis GREEN 3/3 ; couture 23/23 ; portefeuille 574/574 ; client 1 607/1 607. |
| A531 | Live unique du handoff. | FAIL fonctionnel partiel : 1/3 décisions exactes, une publication correcte, zéro fausse publication ; 4 804 tokens, 102 332 ms. |
| A532–A536 | Politique V5 générique et prompt-only. | RED 0/3 puis GREEN 3/3 ; 580/580 ; client 1 613/1 613. |
| A537 | Live neutre V5. | 4/4 protocoles valides, 3/4 décisions brutes, 2/4 cas stricts ; 6 735 tokens, 133 120 ms. FAIL partiel conservé. |
| A538–A540 | Variante de prompt appariée. | Candidat rejeté : 3/4 contre 3/4, malgré 4/4 raisons courtes et 2/2 sûreté ; 8 145 tokens, 149 619 ms. |
| A541 | Recul architectural. | Contrat de preuve principale explicite `lead/support` retenu ; audit 60/60. |
| A542 | Réparation de deux dettes test-only du harnais A539. | 13/13 et client 1 629/1 629 ; aucun replay live. |

### 4.4. V6 et fermeture causale de BUG-133

| Palier | Travail | Résultat |
|---|---|---|
| A543 | RED du contrat sémantique V6. | 16/16 FAIL attendus avant production ; audit 128/128. |
| A544 | GREEN V6 `lead/support`. | Première itération 14/16, puis 16/16 ; 111/111 sémantique/writer ; 615/615 source-backed ; client 1 646/1 646. |
| A545–A546 | Préenregistrement et préflight live V6. | 32/32, 25/25, 122/122, 999/999, client 1 657/1 657 ; audit 3 858/3 858. |
| A547–A548 | Live V6 unique. | **4/4 cas exacts, 4/4 protocoles, 4/4 décisions ; 7 210 tokens ; 138 830 ms.** BUG-133 fermé sur le périmètre lead/support, handoff et publication. |

Cette fermeture est volontairement bornée : elle ne signifie pas que tout le produit RAG est approuvé sur le corpus réel.

### 4.5. Vertical slice Q016 et BUG-115

| Palier | Travail | Résultat |
|---|---|---|
| A549–A550 | Matrice de clôture et préflight corpus/WinUI Q016. | Préflight readonly 79/79. |
| A551–A553 | Vertical slice réel Q016. | FAIL : aucune réponse/source ; 7 appels, 8 416 tokens, 146 123 ms. Audit 138/138, Telegram, post-audit 38/38. |
| A554–A558 | Politique de clarification/sélection unique V2. | RED 0/6 → GREEN 6/6 ; 313/313, 70/70, client 1 662/1 662 ; audit 140/140. BUG-115 corrigé déterministement, non revalidé live. |

Incidents non promus conservés : 311/313, 69/70 avec runner à 2 502 lignes, puis 5/6 sur une assertion sensible au retour ligne. Aucun seuil n'a été assoupli ; le runner est revenu à 2 500 lignes.

### 4.6. Canari C001 et correction générique de récupération après scope à zéro preuve

| Palier | Travail | Résultat |
|---|---|---|
| A559–A562 | Sélection, audit, préflight et armement C001 PTFE TF1620. | 86/86, 105/105 et 76/76 ; une seule exécution autorisée. |
| A562–A564 | Live C001. | FAIL probant : deux recherches dans `Certifications` à zéro hit, doublon, arrêt ; 13 764/12 000 tokens et 171 059 ms. |
| A565 | Erratum. | `Certifications` existait bien ; la cause correcte est un scope catalogue valide mais à rendement nul qui reste collant. Erratum Telegram audité. |
| A566–A570 | BUG-134 récupération scope zéro-yield. | RED 0/8 → GREEN 8/8 ; 313/313, 70/70, client 1 670/1 670 ; audit 174/174. |

La correction n'impose aucune catégorie : elle expose à Qwen le scope exécuté, son rendement, les chemins visibles et les options de récupération. `semanticDecisionOwner=llm` reste explicite.

### 4.7. C002, backend et contrats de liveness

| Palier | Travail | Résultat |
|---|---|---|
| A571–A572 | Sélection C002 TF6220 et préflight. | Un bug backend `Math.Clamp` a été reproduit 5/6 puis corrigé 6/6 ; 866/866 et backend canonique 2 054/2 054 ; redéploiement réussi ; audit 90/90. |
| A573 | Live C002. | FAIL probant : réponse prudente sans carte ; 42 269/12 000 tokens, 385 911/240 000 ms, boucle jusqu'au tour 16. |
| A574–A575 | Diagnostic et corrections génériques. | Canonisation `explicit_set + count=1` vers `single_item`, et ouverture de la terminaison Qwen quand aucun audit candidat n'est possible ; 397/397, puis client 1 676/1 676. Non revalidé live. |

Le premier client complet A574 avait fait 1 675/1 676 parce que le runner atteignait 2 540 lignes. Le contrat a été extrait dans un module partiel ; le runner final est revenu à 2 496 lignes.

### 4.8. C003 et BUG-135 sujet nommé versus document nommé

| Palier | Travail | Résultat |
|---|---|---|
| A576–A578 | Sélection/audit/préflight C003 TF1750. | 78/78, 80/80 et client 1 676/1 676. |
| A579–A580 | Live C003. | FAIL en 42 303 ms et 2 708 tokens, sans aucun outil documentaire : Qwen a placé le sujet `PTFE TF 1750` dans le champ réservé au titre/fichier exact. |
| A581–A585 | Correction BUG-135. | RED 0/11 → GREEN 12/12 ; portefeuille 440/440 ; client 1 687/1 687 ; audit 68/68. |

Le résolveur exact n'a pas été rendu flou. Le contrat fait déclarer à Qwen s'il s'agit d'un sujet/entité ou d'un document/titre/fichier ; le code vérifie seulement la cohérence de l'état et empêche une insuffisance immédiate incohérente.

### 4.9. C004, BUG-136 provenance des cartes et BUG-137 budget cumulatif

| Palier | Travail | Résultat |
|---|---|---|
| A586–A588 | Sélection/audit/préflight C004 ABB 266 HART. | 60/60, 79/79 et portefeuille 440/440. |
| A589–A590 | Live C004. | FAIL : la recherche avait la preuve `Easy Setup`, mais Qwen a choisi une carte faible `Integrated LCD display available`; hash catalogue 32 caractères refusé par le vérificateur SHA-256. 247 218 ms, 17 518 tokens. |
| A591–A595 | BUG-136 cartes canoniques. | RED 7/17 → GREEN 17/17 ; ciblé 377/377 ; backend 2 054/2 054 ; client 1 704/1 704 ; audit 127/127. |
| A596–A600 | BUG-137 budget LLM cumulatif. | RED 2/18 → GREEN 18/18 ; portefeuille 622/622 ; client 1 722/1 722 ; audit 146/146. |

BUG-136 hydrate l'identité forte depuis une source compatible du même document/révision, maintient les cartes sans texte comme pivots de navigation et refuse qu'elles soient l'unique preuve finale. Le vérificateur SHA-256 n'a pas été assoupli.

BUG-137 introduit un ledger unique pour tous les appels LLM : maximum **12 000 tokens / 240 000 ms**, réservation avant I/O, réconciliation après appel, et réserve terminale **4 800 tokens / 60 000 ms**. Le code ne reprend aucune décision sémantique à Qwen.

Incident de modularité important : une première cible A599 a fait 621/622 parce que le runner faisait 2 599 lignes. L'initialisation a été extraite dans un module de 207 lignes ; le runner est revenu à 2 500 et la cible finale a passé 622/622.

### 4.10. C005 et ouverture de BUG-138

| Palier | Travail | Résultat |
|---|---|---|
| A601–A603 | Sélection, audit indépendant et préflight C005 Microsoft 2024. | 96/96, 129/129, portefeuille 622/622 et audit 156/156. La source fraîche page 2 contient `over $245 billion` et `up 16 percent year-over-year`. |
| A604 | Live WinUI unique C005. | FAIL honnête après 112 629 ms ; 7 156 tokens, cinq appels LLM, une recherche, dix preuves matérialisées, mais aucune réponse ni carte source. |
| A605 | Audit/Telegram. | Audit 147/147, message 63/63, post-audit 96/96. Un seul appel notificateur, transport en deux parties. |
| A606 | Préenregistrement BUG-138. | 85/85 ; budgets inchangés ; aucun live/Qwen/WinUI autorisé. |
| A607 | RED BUG-138. | 4 FAIL / 1 PASS, audit 76/76. |
| A608 | Implémentation en cours. | Repair routeur 3/3 fonctionnel ; scénario terminal encore 1 échec, soit total 4/5. Pas encore GREEN. |

Cause C005, dans l'ordre :

1. Le routeur Qwen a produit deux fois `document` avec `namedReferenceKind=none`.
2. Le repair complet a répété l'incohérence au lieu de corriger uniquement le couple concerné.
3. La planification pré-documentaire a consommé une part importante du temps et des tokens.
4. La recherche a réussi mais n'a pas matérialisé le chunk oracle Microsoft page 2.
5. Après la fast review, il restait 4 844 tokens : la revue non terminale suivante a été correctement refusée pour protéger la réserve de 4 800.
6. Le runner a remonté `terminal_budget_only` en erreur générique au lieu d'activer sélection/writer/issue terminale.

## 5. État technique exact d'A608 au moment du rapport

### 5.1. Changements de production déjà présents

1. `SourceBackedAgentFastEvidenceBudgetTransition.cs`
   - capture uniquement `terminal_budget_only` autour de la fast review ;
   - recalcule l'existence de preuves mécaniquement finalisables après matérialisation ;
   - permet d'activer la transition terminale existante sans choisir les EvidenceIds dans le code.
2. `SourceBackedAgentV2Runner.cs`
   - utilise cette transition ;
   - conserve la limite architecturale exacte de 2 500 lignes.
3. `ToolAgentOrchestrator.NativeRouterNamedReferenceRepair.cs`
   - micro-outil Qwen `repair_named_reference_pair` ;
   - seulement `namedReferenceKind` et `document` ;
   - maximum 96 tokens ;
   - une seule tentative ;
   - aucun fallback de repair complet en cas d'échec de ce repair ciblé.
4. `ToolAgentOrchestrator.RouterCore.cs`
   - active ce chemin uniquement pour `native_source_route_named_reference_inconsistent`.
5. `SourceBackedAgentSemanticSelectionSubmission.cs`
   - propage désormais le statut terminal de la sélection au writer dédié.

### 5.2. Empreintes courantes

| Fichier | Lignes | SHA-256 |
|---|---:|---|
| `SourceBackedAgentFastEvidenceBudgetTransition.cs` | 80 | `370AF56224ACBF72D952EA9DBEAE32FF54904AA9A9961120E98313A3C7419605` |
| `SourceBackedAgentV2Runner.cs` | 2 500 | `51EDD9FF2332675E22FBFEEC9EB2AB254141B43A6CC37C7AA5C6750DCB81EE1D` |
| `ToolAgentOrchestrator.NativeRouterNamedReferenceRepair.cs` | 214 | `C755AD8D7635010579D8CAF781F61B9D25779F3FC19B19FB9671686F2692A7FD` |
| `ToolAgentOrchestrator.RouterCore.cs` | 742 | `C2E5A5DA7CE1B04C0B21289E975B069D12F5E9E08C443D189713B4A1364CB1EC` |
| `SourceBackedAgentSemanticSelectionSubmission.cs` | 266 | `B94B9D4A4AC9C6D1710D0A27AB9B0CE48FBC254BD7F20B51A7B5258A2C96688E` |
| `Bug138TerminalTransitionAndRouterCostContractTests.cs` | 561 | `5CE3776011155440D9231E64DE45329A4602856E28EEA0A33E807CE22CC276F3` |

### 5.3. Résultats A608 non promus

- Première exécution après le correctif principal : **4/5**. Les trois contrats du repair routeur passent. La transition terminale atteint la sélection, mais le writer était encore classé non terminal et recevait `terminal_budget_only`.
- Correction appliquée : `PushTerminalCall()` autour du writer issu d'une sélection terminale explicite.
- Deuxième exécution : **4/5**. Le writer passe désormais et produit la réponse sourcée simulée. L'exécution atteint ensuite une revue sémantique finale pour laquelle le double de test n'avait plus de réponse, d'où `Queue empty`.
- Le fichier porte le nom `A608-BUG138-GREEN-PROMOTED.trx`, mais **il n'est pas promu**, car la commande a terminé avec code 1 et 4/5.
- Aucun audit A608 final n'a encore été créé.
- Aucun Telegram A608 n'a été envoyé.
- Aucun test de régression A609 n'a encore été lancé.

Conclusion A608 : la cause initiale est nettement mieux contenue, mais il faut encore expliquer ou corriger le passage à la revue finale, obtenir **5/5**, puis seulement promouvoir le TRX.

## 6. Ce qui a été prouvé et ce qui ne l'est pas

### 6.1. Prouvé

- Le pipeline peut produire une réponse live correcte avec preuve principale/support et publication contrôlée : A545, 4/4.
- Le mécanisme fail-closed a empêché plusieurs réponses incorrectes ou sans provenance d'être publiées.
- Les transitions, contrats, EvidenceBundle, cartes, provenance, budget et traces peuvent être vérifiés mécaniquement sans coder la réponse métier.
- Les défauts C001–C005 sont reproductibles et attribués à des étapes précises, pas masqués par un message générique.
- Les correctifs BUG-134/135/136/137 passent leurs chaînes RED/GREEN et leurs suites complètes respectives.
- La limite de modularité du runner a été défendue à plusieurs reprises au lieu d'être augmentée.
- Les canaris échoués n'ont pas été rejoués pour fabriquer un meilleur résultat.

### 6.2. Non prouvé ou non approuvé

- Le produit complet sur des questions utilisateur variées et du corpus réel.
- BUG-115, BUG-134, BUG-135, BUG-136 et BUG-137 sur un nouveau canari live distinct après leurs corrections finales.
- BUG-138 : le GREEN ciblé n'est pas encore terminé.
- Une performance produit stable sous 45 secondes ; plusieurs lives dépassent largement ce seuil.
- Une maîtrise live systématique du budget 12 000/240 000 ; C002 et C004 l'ont dépassé avant BUG-137, C005 a respecté le ledger mais n'a pas terminé utilement.
- Une UI source complète et cliquable sur C001–C005 ; ces campagnes n'ont livré aucune carte finale.
- La promotion ou la clôture de la phase 5.

## 7. Communications Telegram sur la fenêtre

La fenêtre contient **42 reçus Telegram** authentifiés. Cela répond au besoin de notifications aux paliers intéressants, mais la densité est très élevée et peut nuire à la lisibilité. Les reçus sont :

`A420-A424`, `A425-A429`, `A430-A434`, `A435-A439`, `A440-A444`, `A445-A449`, `A450-A454`, `A455-A459`, `A460-A464`, `A465-A469`, `A470-A474`, `A475-A479`, `A480-A484`, `A485-A489`, `A490-A494`, `A495-A499`, `A500-A504`, `A505-A509`, `A510-A514`, `A515-A519`, `A520-A525`, `A526-A530`, `A531`, `A532-A536`, `A537`, `A539`, `A541`, `A542`, `A544`, `A545`, `A553`, `A558`, `A564`, `A565-ERRATUM`, `A570`, `A575`, `A580`, `A585`, `A590`, `A595`, `A600`, `A605`.

Les envois les plus importants de la fin de fenêtre sont :

- A595 : BUG-136 corrigé déterministement ; un envoi, audit post 63/63.
- A600 : BUG-137 corrigé déterministement ; un envoi, audit post 77/77.
- A605 : échec live C005 et cause BUG-138 ; un appel notificateur, message transporté en deux parties, audit post 96/96.

Recommandation de gouvernance : conserver les Telegram obligatoires pour live PASS/FAIL, fermeture de bug, changement architectural et blocage réel ; regrouper les micro-paliers déterministes dans un rapport consolidé afin de réduire le bruit.

## 8. Incidents, essais non promus et corrections d'audit

Les éléments suivants ont été conservés explicitement et ne sont pas présentés comme des succès :

- assertions sensibles aux fins de ligne, espaces, accents ou Unicode ; seules les assertions ont été rendues robustes lorsque la preuve produit ne changeait pas ;
- plusieurs premières passes d'auditeurs sous Windows PowerShell 5.1 incompatibles avec certaines constructions ;
- deux suites backend x64 non promues à cause de tests dépendant de chemins de sortie relatifs ; la suite canonique AnyCPU a été utilisée ;
- multiples dépassements du seuil de modularité du runner (2 502, 2 540, 2 599 lignes), tous corrigés par extraction de modules ; seuil jamais relâché ;
- A562 : première tentative UIA `set_value` sans écriture, champ vérifié vide, puis une seule saisie `type_text` ;
- A589 : capture UI observée mais non exportée localement ; lacune déclarée, aucun replay ;
- A565 : diagnostic initial « catégorie inventée » corrigé publiquement en « catégorie valide à rendement nul et collante » ;
- A607 : premier RED 5/5 échoué à cause du comptage du saut de ligne final ; seul le test de comptage a été corrigé, puis RED causal 4 FAIL/1 PASS ;
- A608 : deux résultats 4/5, tous deux non promus malgré le nom du second fichier.

## 9. État Git et ressources à 22:56

- État Git total : **587 entrées**.
- Modifiés suivis : **71**.
- Supprimés suivis : **8**.
- Non suivis : **508**.
- Cet état correspond à la grande refactorisation historique en cours ; aucun reset, clean, checkout destructif ou mass-stage n'a été effectué.
- `git diff --check` ne signale pas d'erreur d'espace ; il émet seulement les avertissements historiques de conversion CRLF/LF.
- Aucun processus ciblé `SAAIA`, `llama-server` ou helper `codex-computer-use` n'était actif lors du contrôle final.
- Aucun listener sur le port 1234.
- La session JavaScript de contrôle Windows a été explicitement réinitialisée à la demande de l'utilisateur. Aucun contrôle graphique n'est requis pour le présent rapport.

Volume documentaire direct du répertoire `phase5` sur la fenêtre :

- 848 fichiers directs modifiés/créés ;
- 122 rapports Markdown ;
- 42 protocoles Markdown ;
- 99 TRX ;
- 147 scripts d'audit ;
- 151 sorties d'audit ;
- 42 reçus Telegram.

Ces nombres décrivent le volume de preuve, pas un niveau d'approbation.

## 10. Plan de reprise précis

### A608 — terminer le GREEN BUG-138

1. Examiner pourquoi le scénario terminal passe à une revue sémantique indépendante alors que la sélection explicite devait permettre la clôture plate, ou compléter l'oracle si cette revue est réellement contractuelle.
2. Ne modifier ni les budgets, ni la propriété sémantique de Qwen, ni les critères de provenance.
3. Relancer uniquement les cinq tests BUG-138.
4. Promouvoir un TRX uniquement à **5/5**, zéro skip, code 0.
5. Auditer : activation étroite, max 96 tokens, une tentative, overlay de deux champs, transition terminale, ligne runner ≤ 2 500, anti-hardcode et ressources.

### A609 — régressions

Objectifs préinscrits, encore non exécutés :

- portefeuille source-backed attendu : **627/627** si les cinq nouveaux oracles s'ajoutent proprement au 622/622 A599/A603 ;
- suite client complète attendue : **1 727/1 727** si les mêmes cinq oracles s'ajoutent au 1 722/1 722 A599 ;
- DLL produit/harnais byte-identiques ;
- aucun Qwen, WinUI ou live ;
- `git diff --check` sans erreur ;
- ressources arrêtées.

Ces nombres sont des cibles, **pas des résultats actuels**.

### A610 — clôture technique

1. Rapport technique final BUG-138.
2. Audit indépendant pré-Telegram.
3. Message consolidé expliquant clairement le C005, les deux corrections, les tests et les limites.
4. Un seul appel au notificateur.
5. Reçu et audit post-envoi.
6. Statut attendu même en cas de PASS déterministe : `CORRIGE_DETERMINISTEMENT_NON_REVALIDE_LIVE` et produit `TESTE_NON_APPROUVE`.

### Après A610

Un éventuel live devra utiliser **un nouveau canari distinct C006**, préenregistré, avec vérité source fraîche, budgets, critères fonction/provenance/UI/performance séparés et zéro replay C001–C005.

## 11. Conclusion

Le travail des dernières 24 heures n'est pas une suite de correctifs cosmétiques. Il a progressivement transformé des échecs opaques en contrats mesurables : décision sémantique explicite, EvidenceBundle canonique, lead/support, provenance forte, cartes admissibles, budget cumulatif et transitions terminales.

La discipline de preuve a toutefois montré que le RAG réel reste fragile. Le seul PASS live causal majeur, A545, valide une couture architecturale importante, pas l'ensemble du produit. Les cinq canaris corpus/WinUI C001–C005 ont tous échoué fonctionnellement et ont précisément servi à révéler les prochaines dettes génériques.

L'état honnête est donc : **architecture nettement renforcée, plusieurs bugs corrigés déterministement, un PASS causal important, mais produit non approuvé et BUG-138 encore en cours à 4/5 ciblé.**
