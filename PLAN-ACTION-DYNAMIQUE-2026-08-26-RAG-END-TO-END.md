# Plan d'action dynamique — RAG SAAIA end-to-end

Statut du document : **ACTIF — registre opérationnel subordonné au Goal produit**  
Statut du Goal Codex : **ACTIF — phase 5 fonctionnalité, mémoire et généricité**  
Créé le : **2026-08-26**  
Dernière mise à jour : **2026-08-28 après A202 — Evidence Judge structuré rejeté ; aucune intégration produit, comparaison fallback extractif/insuffisance à préenregistrer**  
Branche de départ observée : `SAAIA_V3.1`  
Commit de départ observé : `5f35881cdc67d12a076fcd2a7a1004656ac9a37a`  
Écart observé avec `origin/SAAIA_V3.1` : `15` commits d'avance, `0` de retard  
État Git de départ : `469` entrées (`66 M`, `8 D`, `395 ??`)  
Goal parent : **RAG SAAIA fiable, générique, LLM-first, rapide et entièrement traçable**

Documents normatifs associés :

- `GOAL-2026-08-27-SAAIA-RAG-PRODUIT-END-TO-END.md` — Goal produit actif ;
- `GOAL-2026-07-28-RAG-CANONIQUE-LLM-FIRST.md` ;
- `ADR-2026-07-08-rag-llm-orchestration-source-backed.md` ;
- `ADR-2026-07-28-project-memory-and-context-envelope.md` ;
- `REPRISE-2026-08-26-NOUVELLE-DISCUSSION-RAG-ULTRA-COMPLETE.md` ;
- dernier live de référence :
  `artifacts/client-live-final-weekly-meal-plan-20260807-130111/progress.log` ;
- dernier TRX de référence :
  `artifacts/client-live-final-weekly-meal-plan-20260807-role-memory-anchor-audit/Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf-live.trx`.

---

## 1. Fonction de ce document

Ce fichier est le registre dynamique, vérifiable et durable de l'exécution. Il
est subordonné au Goal produit actif et ne peut ni le réécrire, ni imposer une
solution expérimentale, ni rendre bloquante une branche facultative. Il ne sert
pas uniquement à annoncer ce qui devrait être fait. Il doit décrire, au fil du
travail :

- ce qui est prévu ;
- ce qui est réellement commencé ;
- ce qui a été modifié ;
- ce qui a été compilé ;
- ce qui a été testé ;
- ce qui a réussi mécaniquement ;
- ce qui a été inspecté humainement ;
- ce qui est approuvé ;
- ce qui a été testé mais refusé ;
- ce qui reste incertain ;
- les artefacts permettant de reproduire chaque conclusion ;
- la justification du passage d'une phase à la suivante.

Ce document doit être mis à jour **pendant** le chantier, et non reconstitué de
mémoire à la fin.

La règle fondamentale est :

> Une phase n'est terminée que lorsque ses critères techniques, sémantiques,
> humains et documentaires sont tous prouvés. Une compilation verte, un test
> vert ou une réponse comportant vingt citations ne suffisent pas isolément.

### 1.1 Légende des identifiants techniques

Ces codes servent uniquement à relier les hypothèses, résultats et fichiers ;
ils ne désignent ni des composants SAAIA ni des standards RAG :

- `EXP-050` : cinquantième expérience contrôlée ;
- `Z.0` : première version de la variante Z (`.1`, `.2` = révisions) ;
- `P1`, `P2` : scénarios ou permutations internes à une expérience ;
- `DEC-060` : décision architecturale enregistrée ;
- `PRV-138` : preuve vérifiable (TRX, JSON, log, hash ou rapport) ;
- `MOD-045` : modification tracée, produit ou test-only ;
- `BUG-xxx` : anomalie suivie ;
- `APPROUVE`, `REJETE`, `TESTE_NON_APPROUVE`, `INCONCLUANT` : verdict et non
  simple résultat technique.

Dans les rapports destinés à l'utilisateur, l'intitulé français doit précéder
le code, par exemple « Évaluation du modèle Granite 4.2-3B (`EXP-049/Y.0`) ».

---

## 2. Goal Codex actif et hiérarchie d'autorité

Le texte exact et complet du Goal actif est conservé dans :

`GOAL-2026-08-27-SAAIA-RAG-PRODUIT-END-TO-END.md`.

Hiérarchie obligatoire :

1. le Goal produit fixe l'objectif utilisateur et les invariants durables ;
2. les ADR fixent les décisions d'architecture compatibles avec le Goal ;
3. le présent registre pilote l'exécution et conserve les preuves ;
4. une expérience, une variante ou une décision locale ne peut jamais
   redéfinir le produit ni bloquer le chantier si du travail local pertinent
   reste possible.

Résumé non substitutif : achever un assistant documentaire généraliste piloté
sémantiquement par Qwen3, fondé exclusivement sur des preuves canoniques
transportées sans perte dans un `EvidenceBundle`, et livrant dans WinUI des
réponses utiles avec des sources exactes réellement cliquables. Le planning
Cuisine 5 × 4 est un stress-test, pas l'objectif du logiciel.

---

## 3. Résultat final attendu

Le Goal n'est complet que si les affirmations suivantes sont simultanément
vraies et prouvées par des artefacts :

### 3.1 Orchestration

- Qwen3 reste l'orchestrateur et le décideur sémantique final.
- Le code ne décide pas qu'une source « répond » ou « convient ».
- Le code peut contrôler une identité, une citation, un doublon, un budget, un
  format, une pagination et un matching déjà fondé sur les décisions du LLM.
- Le LLM peut choisir de rechercher, paginer, pivoter, clarifier, répondre,
  répondre partiellement ou déclarer une insuffisance.
- Aucune hypothèse éphémère n'est exécutable comme argument d'outil.
- Aucune logique du runtime n'est spécifique à Cuisine, aux repas, à Q019, à un
  document, à une catégorie, à une langue ou à une liste connue de fichiers.

### 3.2 Preuves et provenance

- Le contenu, le writer, les citations et les cartes UI dérivent du même
  `EvidenceBundle`.
- Chaque `EvidenceId` final résout une unique preuve canonique.
- Chaque source finale possède `docId`, `revisionId`, fichier, page, chunk ou
  ancre, et hash pertinent.
- Aucun chemin, nom de fichier, numéro de page ou chunk n'est reconstruit par le
  LLM à partir du texte.
- La mémoire influence l'orchestration mais n'est jamais utilisée comme preuve
  finale.

### 3.3 Qualité

- Les questions simples ne passent pas par la voie lourde du planning.
- Les documents nommés restent ancrés sur le bon document.
- Les réponses de suivi réutilisent la mémoire sans inventer de preuve.
- Les ambiguïtés matérielles peuvent déclencher une clarification utile ; les
  préférences facultatives ne déclenchent pas de question inutile.
- Les réponses hors corpus sont honnêtes.
- Le planning contient vingt valeurs concrètes, distinctes et adaptées à leur
  rôle, ou déclare honnêtement que le corpus ne permet pas de le produire.

### 3.4 Performance

- Question simple : objectif `<= 30 s`.
- Document nommé : objectif `<= 45 s`.
- Planning complexe : médiane `<= 7 min`.
- Planning complexe : aucun run `> 10 min`.
- Aucun dépassement de contexte.
- Aucune répétition non productive d'une requête ou d'un audit identique.

### 3.5 Livraison

- Builds et tests déterministes verts.
- Probes backend et LLM archivées.
- Trois runs live consécutifs approuvés pour chaque scénario critique demandé.
- Parcours WinUI humain validé avec sources réellement cliquables.
- Dette historique classifiée et nettoyée sans perte.
- Commits cohérents, petits, réversibles et documentés.
- État Git final expliqué et intentionnel.

---

## 4. Principes non négociables

1. **Sémantique au LLM, mécanique au code.**
2. **Une preuve verte n'efface pas un défaut observé humainement.**
3. **Un test déterministe prouve un contrat, pas la qualité réelle de Qwen3.**
4. **Une réponse mécanique 20/20 avec de mauvais choix reste refusée.**
5. **Un gain de temps accompagné d'une baisse de qualité est une variante
   rejetée.**
6. **Une variante lente mais correcte n'est pas automatiquement approuvée.**
7. **Deux échecs de même classe déclenchent une revue d'architecture.**
8. **Aucune règle métier locale n'est ajoutée pour faire passer un scénario.**
9. **Aucune hausse de contexte, timeout ou nombre de tours sans benchmark.**
10. **Aucun `git add -A`, reset massif ou nettoyage non classifié.**
11. **Aucun secret n'est écrit dans les artefacts ou ce document.**
12. **Chaque conclusion importante doit pointer vers une commande ou un
    artefact.**

---

## 5. Machine d'état des phases

Chaque phase porte exactement un des états suivants :

| État | Signification |
|---|---|
| `NON_DEMARREE` | Aucun travail de la phase n'est considéré commencé. |
| `EN_COURS` | La phase est active ; ses résultats sont encore révisables. |
| `TESTEE_NON_APPROUVEE` | Des tests existent, mais au moins un critère manque ou la qualité est refusée. |
| `BLOQUEE` | Une dépendance externe ou une décision utilisateur empêche toute progression utile. |
| `PRETE_POUR_REVUE` | Toutes les preuves sont présentes, mais l'approbation finale n'est pas encore enregistrée. |
| `APPROUVEE` | Tous les critères sont remplis et les preuves sont référencées. |
| `ROUVERTE` | Une régression ultérieure invalide une phase auparavant approuvée. |

Règles de transition :

- une seule phase d'implémentation peut être `EN_COURS` ;
- la phase suivante ne commence qu'après passage de la phase courante à
  `APPROUVEE` ;
- `TESTEE_NON_APPROUVEE` ne permet jamais d'avancer ;
- toute régression place la phase responsable en `ROUVERTE` et bloque les
  validations finales dépendantes ;
- l'approbation doit être inscrite dans le journal de phase avec date,
  résultats, artefacts et risques résiduels ;
- une phase peut contenir plusieurs variantes rejetées sans être elle-même
  rejetée ;
- une phase sans inspection humaine lorsque celle-ci est requise ne peut pas
  être approuvée.

---

## 6. Tableau de pilotage global

| Phase | Objet | État | Dépend de | Artefact d'approbation | Dernière décision |
|---|---|---|---|---|---|
| 0 | Réalignement, inventaire des preuves et baseline reproductible | `APPROUVEE` | — | `artifacts/goal-rag-product-20260827-1041/phase0/phase0-realignment-baseline-and-proof-inventory.md` | Goal réaligné ; 1 264 tests client et 2 046 backend verts ; trois probes live ; preuve historique classée |
| 1 | Baseline verticale locale complète, de la question à WinUI | `APPROUVEE_DIAGNOSTIC` | Phase 0 | `artifacts/goal-rag-product-20260827-1041/phase1/phase1-vertical-baseline-results.md` | Quatre runs canoniques : une chaîne courte aboutit mécaniquement ; trois classes de défauts localisées |
| 2 | Orchestration Qwen3 adaptative et généraliste | `APPROUVEE` | Phase 1 | `artifacts/goal-rag-product-20260827-1041/phase2/phase2-orchestration-qwen3-approval.md` | D1 portée, D2 adéquation et D3 continuité approuvées ; 203/203, Normes et canari Cuisine vérifiés |
| 3 | Recherche multiétape et mémoire de rendement | `APPROUVEE` | Phase 2 | `artifacts/goal-rag-product-20260827-1041/phase3/phase3-yield-memory-termination-approval.md` | 220/220 x64 ; P4 terminal honnête 97 048 ms ; Q156 clarification 39 281 ms ; limites transférées explicitement |
| 4 | Intégrité verticale EvidenceBundle, writer, citations et cartes UI | `APPROUVEE` | Phase 3 | `artifacts/goal-rag-product-20260827-1041/phase4/phase4-evidence-identity-approval.md` | Q011 conserve E1/révision/SHA-256/chunk/page jusqu'à la carte inline et ouvre le PDF réel page 5 ; 1 307/1 307 client et 2 048/2 048 backend |
| 5 | Comportements généraux, mémoire, clarification et généricité | `EN_COURS` | Phase 4 | `artifacts/goal-rag-product-20260827-1041/phase5/` | A314–A318 ferment mécaniquement BUG122 : cause C4, rouge exact, frontière générique FR/EN/DE/ES/PT/IT, référence réelle ajoutée à la trace, 12/12, 151/151 puis suite complète 1 473/1 473. Le binaire extrait désormais le nom Q106 exact sur 10 000/10 000 appels. Serveur stable et piste watcher falsifiée. BUG119/120 restent à fermer live par un unique replay A319–A322 préenregistré ; BUG121 reste fermé mécaniquement ; aucun modèle exécuté dans ce palier |
| 6 | Validation live end-to-end et performance | `NON_DEMARREE` | Phase 5 | À produire | Trois runs des scénarios critiques et objectifs P520 |
| 7 | Consolidation du produit, dette et commits | `NON_DEMARREE` | Phase 6 | À produire | Aucun nettoyage global ; voies mortes supprimées seulement sur preuve |
| 8 | Validation WinUI réelle et clôture | `NON_DEMARREE` | Phase 7 | À produire | Clic source exact, rapport reproductible et état Git intentionnel |

---

## 7. Historique du plan antérieur et preuves candidates

> **Statut après réalignement du 2026-08-27 :** les sections 7 à 24 conservent
> l'intégralité du journal, des variantes et des preuves du plan précédent.
> Elles ne prescrivent plus la prochaine solution et leurs anciens états de
> phase ne remplacent pas le tableau de pilotage courant du §6. Chaque preuve
> doit être classée `VALIDE_ACTUELLE`, `VALIDE_HISTORIQUE`, `A_REVALIDER` ou
> `INVALIDE` pendant la phase 0. Les branches distantes, payantes, matérielles
> et fine-tuning y restent documentées mais sont facultatives et non bloquantes.

### 7.1 Baseline historique initiale — ne vaut pas revalidation

Ces éléments sont des points de départ historiques. Ils doivent être confirmés
dans la phase 0 avant d'être traités comme actuels.

| Élément | Dernière preuve connue | Verdict initial |
|---|---|---|
| Tests déterministes | `1 235/1 235`, 2026-08-07 | Historique, à revalider |
| Dernier full live | Timeout à 12 min | Refusé |
| Candidats audités | 100 | Mesure valide du dernier run |
| Candidats approuvés | 24 | Insuffisant pour conclure |
| Candidats distincts compatibles | 14 | Couverture insuffisante |
| Couverture | 4 petit-déjeuners, 4 déjeuners, 5 collations, 7 soupers | Refusée |
| Writer atteint | Non | Blocage amont |
| Réponse finale | Aucune | Refusée |
| Backend `/ready` | Sain le 2026-08-26 | Vérifié ponctuellement |
| LLM client local | Port 1234 arrêté le 2026-08-26 | À redémarrer |
| Corpus Cuisine | 3 882 ancres/chunks/points, 1 123 cartes | À reprober |

Actualisation approuvée le 2026-08-26 : les **3 882 retrieval chunks SQL**
correspondent exactement aux **3 882 points Qdrant** des dix documents actifs.
Le modèle de navigation actuel comporte **2 971 ancres de titre** et **319
entrées de navigation**. Les **890 cartes** actuelles sont toutes issues de
`deterministic_canonical_v5`; l'ancien total de 1 123 appartenait aux révisions
`deterministic_canonical_v3`. L'écart est donc expliqué par une reprojection de
profil, pas par une perte partielle d'index.

Défaut racine historique à reproduire :

- `candidate_object_type=Produit alimentaire` ;
- `hypothetical_single_position_value=Pain au chocolat au lait` ;
- hypothèse marquée éphémère mais réutilisée comme requête ;
- 30 ancres initiales rejetées ;
- pagination de 100 cartes ;
- audits et compatibilités trop nombreux ;
- sixième lot récupéré trop tard ;
- timeout pendant l'audit de compatibilité.

---

## 8. Phase 0 — Baseline reproductible

État : `EN_COURS_REOUVERTE`  
Approbation de 02:15 conservée comme preuve historique, mais invalidée pour les
demandes documentaires simples à facettes cumulatives par EXP-040 puis EXP-041.

### 8.1 Objectif

Établir une photographie actuelle, reproductible et non ambiguë avant toute
modification du produit.

### 8.2 Actions

- [x] Créer `artifacts/goal-rag-end-to-end-20260826-233646/`.
- [x] Enregistrer branche, HEAD et écart avec origin.
- [x] Enregistrer `git status --short`.
- [x] Enregistrer `git diff --stat`.
- [x] Enregistrer `git diff --numstat`.
- [x] Exécuter `git diff --check`.
- [x] Vérifier `/ready` distant.
- [x] Redémarrer le LLM client avec le profil qualifié.
- [x] Archiver `/health`, `/v1/models` et `/props` sans secret.
- [x] Enregistrer le hash du modèle et le profil matériel.
- [x] Compiler Debug/x64 une fois.
- [x] Enregistrer le hash et l'horodatage de la DLL utilisée par les probes.
- [x] Rejouer les tests SourceBacked ciblés.
- [x] Rejouer les tests d'architecture.
- [x] Rejouer les probes cartes, navigation et retrieval sans LLM.
- [x] Comparer les compteurs du corpus canonique.
- [x] Vérifier qu'aucun processus de test résiduel ne pollue les mesures.

### 8.3 Configuration de référence

- modèle : Qwen3-4B-Instruct-2507 Q5_K_M ;
- backend d'inférence : CUDA qualifié sur P520 ;
- contexte : 4 096 ;
- KV : f16 ;
- parallélisme : 1 ;
- audit candidat : actif ;
- taille de lot : valeur du runtime effectivement tracée ;
- température : valeurs du produit, sans modification opportuniste.

### 8.4 Critères d'approbation

- build x64 vert ;
- tests ciblés verts ;
- probes backend cohérentes avec la révision active ;
- modèle, contexte et profil matériel explicitement confirmés ;
- aucun processus parasite ;
- artefact de baseline complet ;
- aucune modification produit incluse dans la phase.

### 8.5 Causes de refus

- binaire périmé ou hash non identifié ;
- corpus différent sans explication ;
- test exécuté avec mauvais drapeau ;
- LLM ou backend dégradé ;
- résultat reposant sur un ancien artefact ;
- secret présent dans un log.

### 8.6 Journal de phase

#### Exécution

- `23:36` : création du dossier immuable de baseline et capture Git initiale.
- `23:37` : `/ready` distant archivé ; tous les services et la provenance
  canonique sont prêts.
- `23:38` : Qwen3 local redémarré sur le profil qualifié ; modèle, runtime,
  propriétés et hashes archivés.
- `23:39` : premier build sans restauration classé `INCONCLUANT` à cause de
  `NETSDK1112` et du runtime pack win-x64 local absent.
- `23:40–23:43` : restauration explicite win-x64 puis build Debug/x64 réussi,
  0 avertissement et 0 erreur.
- `23:44` : 242/242 tests ciblés réussis.
- `23:44` : 1 235/1 235 tests déterministes réussis.
- `23:45` : probe cartes réussi, 120 cartes matérialisées sur 890 actives.
- `23:45` : probe navigation réussi, 200/200 entrées ancrées, 60 avec chunk
  cible et résolution de contexte valide.
- `23:48` : probe retrieval réussi ; 48 hits bruts, 34 preuves normalisées sur
  les quatre requêtes repas.
- `23:51` : comparaison distante en lecture seule ; 3 882 chunks SQL = 3 882
  points Qdrant exacts, 2 971 ancres, 319 entrées de navigation et 890 cartes
  `deterministic_canonical_v5`.
- `23:53` : contrôle final ; backend et LLM sains, aucun processus de test
  résiduel, `git diff --check` sans erreur.

#### Limites constatées

- les résultats bruts `rag.search` de la probe repas n'exposent pas de
  `matched_content_cards` ;
- certains hits de tête sont des conseils ou des exemples de menu et non des
  recettes nommées directement exploitables ;
- les anomalies sémantiques historiques restent à reproduire avec le LLM ;
- aucun full live n'a été lancé, conformément à `DEC-001`.

#### Approbation

```text
Phase : 0 — Baseline reproductible
Date d'approbation : 2026-08-26 23:54 Europe/Zurich
Résumé des changements : registre et artefacts uniquement; aucun changement produit
Tests déterministes : 242/242 ciblés et 1 235/1 235 complets
Probes réelles : cartes 1/1; navigation 1/1; retrieval 1/1
Runs live : aucun run LLM end-to-end, volontairement
Inspection humaine : JSON, rapports de probes, TRX, compteurs SQL/Qdrant et processus relus
Métriques obtenues : build 0/0; 3 882 chunks SQL = 3 882 points Qdrant; 890 cartes v5; 2 971 ancres; 319 navigations
Artefacts : artifacts/goal-rag-end-to-end-20260826-233646/
Variantes rejetées : build --no-restore initial inconcluant, puis corrigé par restauration explicite
Risques résiduels : worktree très sale; retrieval repas bruité; matched_content_cards absent des hits bruts; dette historique ouverte
Régressions ouvertes : BUG-000 à BUG-004
Pourquoi les critères sont tous satisfaits : identité, runtime, build, tests, probes, corpus, processus et absence de modification produit sont prouvés
Autorisation de passer à la phase suivante : OUI
```

Réouverture :

- `2026-08-27 07:03` : EXP-040/Q.0 invalide la couverture de l'approbation pour
  une demande simple à facettes cumulatives. Qwen3 transforme « ingrédients et
  réglage » en options exclusives, dépense deux appels/3 220 tokens puis s'arrête
  avant tout outil. La phase 1 repasse `EN_COURS_REOUVERTE`; la phase 2 est
  suspendue. EXP-041/R.0 est figée avant test/code pour corriger uniquement le
  contrat générique de clarification et préserver le contrôle ambigu historique.

---

## 9. Phase 1 — Orchestration avant retrieval

État : `EN_COURS_REOUVERTE`

### 9.1 Objectif

Obtenir une unité documentaire, un périmètre et une première observation
corrects en moins de trente secondes, sans qu'une hypothèse inventée contamine
un outil.

### 9.2 Invariants à imposer

- une hypothèse de raisonnement est typée et non exécutable ;
- seuls les champs explicitement autorisés peuvent alimenter un argument
  d'outil ;
- le nombre de preuves requis ne doit pas devenir le type de preuve ;
- les axes de sortie ne doivent pas devenir une requête de document ;
- le livrable complet ne doit pas être recherché lorsqu'il faut composer le
  résultat depuis des instances ;
- le périmètre choisi par le LLM reste visible dans tous les appels ;
- le code ne substitue aucun type métier à la décision du LLM.

### 9.3 Expérience contrôlée

Comparer sur les mêmes demandes, le même modèle et le même corpus :

| Variante | Description | Statut initial |
|---|---|---|
| A | Pipeline actuel | Baseline |
| B | Définition sans exemple hypothétique | À tester |
| C | Observation documentaire compacte avant définition | À tester |
| D | Intake plus court fusionnant mission, type commun et première observation | À tester seulement si B/C insuffisants |

Chaque variante doit être exécutée sur :

- cinq permutations du planning 5 x 4 ;
- une demande structurée non Cuisine ;
- une demande simple ;
- une demande par document nommé ;
- une demande volontairement ambiguë.

### 9.4 Mesures obligatoires

- temps du routeur ;
- temps de définition ;
- temps jusqu'à la première preuve utile ;
- prompt/completion/cache tokens ;
- type candidat produit ;
- provenance de chaque terme de requête ;
- première surface choisie ;
- nombre d'ancres visibles et retenues ;
- nombre de réparations de protocole ;
- présence d'une hypothèse inventée dans les arguments d'outil ;
- stabilité entre permutations.

### 9.5 Tests à ajouter ou renforcer

- reproduction exacte de la fuite `Pain au chocolat au lait` ;
- hypothèse éphémère impossible à sérialiser vers un appel d'outil ;
- type commun couvrant tous les rôles sans coder les rôles ;
- demande non Cuisine ;
- document nommé conservé ;
- clarification uniquement si deux interprétations changent réellement le
  travail.

### 9.6 Critères d'approbation

- zéro hypothèse éphémère utilisée comme requête ;
- zéro recherche du livrable à la place des instances ;
- première observation utile en moins de 30 secondes ;
- aucune disparition du scope ;
- résultat correct sur cinq permutations ;
- comportement non Cuisine correct ;
- variante retenue par mesure qualité + latence ;
- variantes perdantes consignées dans le registre des rejets.

### 9.7 Condition de recul

Si deux variantes conservent la même fuite ou si le type reste instable, ne pas
ajouter une nouvelle consigne locale : réévaluer le découpage des appels et le
format du contrat.

### 9.8 Journal de phase

- `2026-08-26 23:54` : phase ouverte après approbation formelle de la phase 0.
- `EXP-002` créé avant exécution pour instrumenter la variante A sans full
  live et sans modification produit.
- `23:58–00:05` : quatre probes existantes exécutées. Routeur : 40,995 s,
  définition : 26,670 s, stratégie expérimentale : échec en 46,069 s, choix
  d'outil/transition : vert mécanique mais 150,202 s.
- La baseline fragmentée est `TESTEE_NON_APPROUVEE` : le seuil de 30 s est
  impossible puisque le routeur seul le dépasse et aucune probe ne relie tous
  les appels avec leurs latences.
- `EXP-003` préparée pour ajouter une instrumentation de test unifiée avant
  toute variante comportementale.
- `00:07–00:22` : harness unifié ajouté dans
  `LiveSourceBackedIntakeFirstActionBenchmarkTests.cs`, build Debug/x64 vert
  avec zéro warning et smoke test désactivé vert.
- `EXP-003/P1` relie désormais le routeur réel au runner configuré avec
  `ResolveFromEnvironment`, puis interrompt volontairement l'exécution à la
  première frontière documentaire. Relance de référence : routeur 17,187 s,
  rôles de colonnes 34,337 s, définition atomique 21,666 s, observation
  initiale 7,569 s, soit 81,014 s avant `documents.navigation`.
- Les arguments exécutables sont propres
  (`categoryPath=Cuisine`, `kind=navigation_entry`, aucun `q/query`) : aucune
  coordonnée de grille ni l'hypothèse `Pain au chocolat au lait` n'a franchi la
  frontière. Le scope est conservé.
- Le résultat reste `TESTE_NON_APPROUVE` : seuil dépassé de 51,014 s ; les
  rôles ajoutent des aliments et compositions absents de la demande ; le type
  candidat dérive vers `Produit alimentaire` ; la règle d'éligibilité se
  termine par `Les valeurs ne应` mais passe la validation de longueur.
- La correction de télémétrie effectuée entre les deux runs ne modifie aucun
  chemin produit.
- `00:22–00:37` : P2–P5 et les quatre cas génériques exécutés. Les cinq
  permutations prennent 81,014 à 116,287 s, moyenne 106,918 s. P2 et P3
  exécuteraient une query contenant axes, aliments et horaires ajoutés. Les
  cinq types candidats diffèrent.
- Cas simple : `rag.search` en 13,728 s. Document nommé : navigation directe en
  17,882 s avec nom préservé. Ces chemins prouvent que le routeur peut déjà
  choisir un premier outil sous le seuil lorsqu'il ne force pas les trois
  décisions de grille supplémentaires.
- Grille non Cuisine : deux décisions LLM valides en apparence puis fallback
  `chat.general`, car `opération` est simultanément un terme documentaire
  légitime et un libellé de colonne interdit par le validateur de coordonnées.
- Cas ambigu : le LLM demande deux fois une clarification, mais emploie le même
  `userTextAnchor` pour deux choix ; le validateur rejette les doublons et le
  fallback final perd `NeedClarification`.
- `phase1-exp003-baseline-a-analysis.md` prononce le rejet de A. B n'est pas
  retenue comme solution principale : retirer seulement l'exemple hypothétique
  conserve quatre appels et n'élimine pas la contamination issue des rôles.
- `00:38–00:52` : variante C implémentée. Le routeur conserve désormais sa
  première action documentaire ; le runner saute les revues LLM spéculatives
  de rôles, définition et stratégie avant cette observation. Les fallbacks de
  clarification et le faux rejet du type Maintenance sont corrigés sans règle
  métier. Build Debug/x64 vert, 26/26 ciblés puis 268/268 régression élargie.
- `00:53–00:56` : deux runs C/P1. Les quatre appels A deviennent un seul appel
  routeur. Le premier run atteint l'outil en 43,962 s ; le run chaud en
  21,264 s. Les deux choisissent néanmoins `rag.search` avec une query formée du
  livrable, des jours et des créneaux ; le type reste `repas hebdomadaire`.
  Verdict C : `TESTE_NON_APPROUVE`.
- Le benchmark a aussi été corrigé pour inscrire l'expérience et la variante
  actives dans l'artefact au lieu de conserver l'étiquette historique EXP-003.
- `00:56–01:01` : C.1 renforce de manière générique la distinction entre termes
  documentaires et coordonnées de sortie. Après correction d'une assertion
  littérale, build 0/0 et 173/173 tests ciblés. Le run live reproduit exactement
  la même query et le même type, en 43,658 s. C.1 est rejetée.
- La condition de recul §9.7 est atteinte : C et C.1 conservent la même fuite.
  Aucune troisième consigne locale ne sera ajoutée. EXP-007 modifie le format du
  contrat grille : aucune query libre avant observation ; le LLM choisit une
  surface de découverte/navigation sûre et conserve le scope, tandis que le type
  pré-observation reste explicitement provisoire et non exécutable.
- `01:04–01:12` : EXP-007 déterministe vert (174/174 ciblés, 269/269 élargis),
  mais P1 live rejeté. Le LLM contourne le contrat grille restreint en choisissant
  `submit_source_backed_route`, requalifie le livrable en `single_item/count=1`
  et reproduit la query contaminée. Frontière : 30,804 s.
- EXP-008 est ouverte avant modification : classification LLM compacte de la
  famille de contrat, puis exposition du seul contrat spécialisé sélectionné.
  Cette séparation doit empêcher la compétition entre schémas tout en laissant
  la décision sémantique au LLM.
- `01:13–01:19` : EXP-008 déterministe vert (181/181 ciblés, 276/276 élargis).
  P1 choisit correctement la grille en 6,972 s puis les cartes Cuisine sans
  query en 24,727 s. Tous les invariants sémantiques passent ; total 32,245 s,
  donc `TESTE_NON_APPROUVE` pour dépassement de 2,245 s.
- EXP-009 est enregistrée avant modification : conserver exactement les deux
  décisions de E, mais remplacer au second étage le prompt monolithique par les
  seules règles de la famille déjà sélectionnée.
- `01:20–01:26` : EXP-009 build vert, 185/185 ciblés, 280/280 élargis. Les cinq
  permutations repas sont toutes grille/cartes/sans query/sous 30 s
  (23,042–29,055 s). P4 choisit toutefois pool 120 et P5 conserve un type
  provisoire `planning de repas`.
- La matrice générique rejette E.1 comme routeur universel : non-Cuisine retombe
  dans deux appels de planification (98,512 s) à cause d'un faux document égal
  à la catégorie ; simple devient opérationnel ; document nommé devient une
  clarification inventée ; ambigu devient une grille aux axes et scope inventés.
- EXP-010 est ouverte avant modification : fast-path LLM uniquement pour une
  grille dont les axes sont ancrés ; sinon délégation au routeur général. Les
  contrôles ajoutés sont mécaniques : ancrage, collision catégorie/document et
  plafond de pool.
- `01:27–01:33` : EXP-010 build 0/0, 38/38 routeur, 186/186 ciblés et 281/281
  élargis. P1 est toutefois délégué par le classifieur à anchors ; le routeur
  général reproduit la query contaminée. Frontière 33,086 s. F rejetée.
- EXP-011 est ouverte avant modification : même fast-path et mêmes garde-fous au
  second étage, mais le choix binaire initial ne transporte plus aucun argument.
- `01:34–01:37` : EXP-011 build 0/0, 185/185 ciblés, 280/280 élargis. P1 reste
  délégué ; la frontière est rapide (15,977 s) mais la query contaminée revient.
  F.1 rejetée.
- EXP-012 est ouverte avant modification : rétablir les quatre labels du
  classifieur E, déjà prouvés stables pour les grilles, mais n'activer le
  fast-path que pour `submit_source_backed_grid_route`. Tous les autres labels
  délèguent au routeur général ; les axes du second étage restent ancrés.
- `01:38–01:46` : EXP-012 build 0/0, 187/187 ciblés, 282/282 élargis. Repas
  5/5 : grille, cartes, type provisoire `repas`, pool 20, zéro query et
  22,947–27,588 s. Simple et document nommé passent aussi en 15,644 s et
  17,672 s. Ambigu aboutit à une clarification sans outil en 66,615 s.
- Non-Cuisine reste à 81,475 s : le parseur de mission du runner rejette encore
  `opération de maintenance` parce que `opération` est aussi une colonne, puis
  exécute deux planners. EXP-013 est ouverte avant correction pour supprimer ce
  veto lexical sémantique résiduel avec une régression exacte.
- `01:47–01:54` : EXP-013 suit le protocole test-first. La régression exacte
  échoue d'abord par fallback `submit_semantic_plan`, puis devient verte après
  suppression du veto lexical ; build 0/0, 188/188 ciblés et 283/283 élargis.
  Le run non-Cuisine atteint ensuite `documents.content_cards` Maintenance en
  18,938 s, sans query, avec scope conservé et seulement deux appels LLM.
- EXP-014 est ouverte avant exécution pour vérifier le critère encore manquant :
  appeler réellement le backend avec les arguments exacts de G/P1
  (`documents.content_cards`, `categoryPath=Cuisine`, `limit=20`, `offset=0`,
  aucune query), matérialiser l'`EvidenceBundle`, puis juger humainement si cette
  première observation est assez utile pour guider l'étape sémantique suivante.
- `01:58` : EXP-014 est verte mécaniquement (1/1, 20/20 preuves canoniques,
  citables, 10 documents), mais rejetée sémantiquement. Le mode backend par
  défaut `ordered` équilibre les documents tout en prenant leurs premières
  cartes : sommaires, préfaces, introductions, accessoires et crédits dominent ;
  aucune réserve crédible de vingt recettes nommées n'est visible.
- EXP-015 est ouverte avant exécution, sans changement produit, pour mesurer le
  même appel avec `inventoryMode=representative`. Ce mode est un échantillonnage
  mécanique stable des positions internes de chaque document, déjà prévu par le
  contrat backend ; il ne préclasse pas sémantiquement les candidats.
- `02:00` : EXP-015 est verte et utile. Les 20 preuves couvrent 20 pages
  internes distinctes sur 10 documents et exposent notamment cordons-bleus,
  riz, muffins, guacamole, quiche, empanadas, feta, paëlla, sauce toffee et
  trifle. Le lot conserve du bruit, à soumettre ensuite au jugement LLM, mais il
  fournit des objets et termes source réels pour guider la suite.
- EXP-016/G.1 est ouverte avant modification. Puisque `representative` ne
  préclasse aucun candidat et ne fait qu'échantillonner mécaniquement les
  positions d'un inventaire sans query, le mode sera ajouté par le parseur à
  l'action `cards` d'une grille. Le LLM reste propriétaire de la famille, du
  scope, de la surface, du type provisoire et du budget ; aucun champ ni appel
  LLM supplémentaire n'est introduit.
- `02:03–02:07` : EXP-016 passe du rouge exact à 2/2, puis 189/189 ciblés et
  284/284 élargis. P1 live conserve deux appels, scope Cuisine, type `repas`
  provisoire et zéro query ; `inventoryMode=representative` est présent. La
  frontière outil est atteinte en 22,792 s avec un pool LLM de 10.
- L'observation backend exacte de ces arguments renvoie 10/10 preuves citables,
  10 documents et 10 pages internes. Huit candidats au moins exposent un objet
  culinaire exploitable ; deux illustrent le bruit que l'audit LLM devra rejeter.
- EXP-017 est ouverte avant modification test-only : étendre le benchmark avec
  un mode opt-in qui exécute l'action capturée, construit l'`EvidenceBundle`,
  puis interrompt le runner. Cette mesure intégrée doit prouver dans un même
  chronomètre le délai question → première observation, sans engager les audits
  de la phase 2.
- `02:08–02:12` : EXP-017 build et smoke désactivé verts. P1 intégré atteint la
  frontière en 20,779 s, reçoit le backend en 179 ms et matérialise 20 preuves
  issues de 10 documents en 21,010 s depuis la question. Arguments : Cuisine,
  `representative`, limit 20, offset 0, aucune query ; scope et invariants verts.
- EXP-018 est ouverte avant exécution pour la matrice de clôture G.1 : P2–P5 et
  non-Cuisine sur le même binaire. Elle doit confirmer cartes, mode
  représentatif, absence de query, scope exact, type provisoire et seuil de
  30 s ; aucun changement produit supplémentaire n'est autorisé pendant la
  matrice.
- `02:12–02:15` : EXP-018 confirme P2–P5 en 22,803–27,922 s et Maintenance en
  24,639 s. Les cinq actions sont cartes/representative, sans query ni fuite,
  avec scope exact et type provisoire. Build final Debug/x64 : 0 warning,
  0 erreur.
- L'analyse consolidée `phase1-exp012-exp018-variant-g-g1-analysis.md` approuve
  tous les critères de la phase. DEC-017 autorise explicitement l'ouverture de
  la phase 2 ; aucun audit, matching ou writer aval n'est réputé validé.
- `06:57–07:14` : EXP-040/Q.0 révèle que Q019 est clarifiée avant tout outil ; la
  phase 1 est rouverte et la phase 2 suspendue. EXP-041/R.0 est gelée, exécutée
  en TDD puis en live sur Q019, Normes et un contrôle ambigu.
- R.0 passe mécaniquement le TDD et les budgets, mais échoue live : `0/2`
  demandes documentaires atteignent un outil, la vraie ambiguïté nécessite trois
  appels et une grille inventée, `12 588` tokens et `155,965 s` cumulés.
- La règle prompt-only et son test de présence sont rollbackés ; les scénarios
  test-only et toutes les preuves sont conservés. Aucun second run n'est lancé,
  conformément aux portes préenregistrées.
- La condition de recul §9.7 est de nouveau active : aucune retouche de prompt
  locale n'est autorisée. La prochaine expérience doit mesurer le découpage
  classifieur → contrat → validation → réparation → fallback.
- `09:50–10:08` : après rejet de Granite 4.2-3B, EXP-050 prépare sans l'exécuter
  la décision entre référence distante, matériel 24–32 GB et fine-tuning.
  L'exporter test-only matérialise huit requêtes et sépare physiquement les
  oracles `neverSend`. Deux vérificateurs rouges ont d'abord retiré une
  auto-description contenant `EvidenceBundle`, puis remplacé un interdit
  lexical trop large sur le terme légitime `EvidenceId`. Les contrôles
  structurels et le scan final des chemins JSON passent ensuite `1/1`.
- Le paquet sortant fait `114707` octets, contient huit scénarios, deux messages
  et six outils chacun, sans rôle outil, URL, chemin Windows décodé, secret,
  résultat backend ni champ d'oracle. Son SHA-256 est
  `33C5508179F68DBD3074AE89BEA8BF243354392A7A49865923666DB7BD6959FC`.
  Les oracles locaux font `2272` octets, ont les mêmes huit IDs et le SHA-256
  `D49228E36DFBB5DBB1EC281DDB75358B5E0A2026B3D895F85DC78C87125236F7`.
- Le dossier quantifié recommande la référence distante comme diagnostic le
  plus discriminant avant achat ou entraînement. Aucune branche n'est exécutée :
  fournisseur/modèle/coût/rétention/divulgation restent à approuver. Le Q5
  restauré reste seul serveur actif et sain sous PID `4216`.
- `10:10` : rapport de palier envoyé à Telegram en une partie depuis l'artefact
  UTF-8 vérifié : corps `2237` caractères, total `2362`, retour notifier exact.
  Le message place les intitulés français avant les codes et rappelle qu'aucune
  branche, dépense ou divulgation n'est autorisée.
- `10:11–10:16` : la référence distante générique est rendue concrète sans
  appel. Les pages officielles OpenAI qualifient GPT-5.6 Sol comme modèle phare,
  avec function calling et prix affiché `4 USD/M` entrée, `20 USD/M` sortie.
  Le profil proposé conserve huit requêtes exactes, six outils, 256 tokens,
  `reasoning_effort=none`, `tool_choice=required`, `store=false`, aucun retry,
  30 s par appel et zéro exécution backend.
- Le coût canonique maximal calculé depuis les tokens locaux est `0,115092 USD`;
  le plafond proposé est `0,20 USD`. Les seize permutations restent séparément
  interdites. Le choix global implique l'acceptation de logs d'abus jusqu'à
  30 jours ; le choix Europe exige une preuve MAM/ZDR non encore disponible.
  Une clé OpenAI existe en environnement, mais sa valeur n'a été ni affichée,
  copiée, testée ni utilisée ; accès modèle/facturation restent non vérifiés.
- `10:18` : le rapport Telegram de cette proposition précise est accepté en une
  partie : corps `2087` caractères, total `2215`, UTF-8 relu identiquement. Il
  consigne modèle, plafond, identifiants, rétention et absence d'appel API.
- `10:20` : troisième audit consécutif du même blocage. Le manifeste reste
  `NOT_AUTHORIZED`, réseau false, endpoint nul et appels 8+16 non approuvés ;
  Q5 PID `4216` reste seul serveur sain. Les préparations sûres hors ligne sont
  épuisées. Le Goal et la phase passent en attente bloquée plutôt que de
  contourner le consentement sur coût, confidentialité ou architecture.
- `10:20–10:22` : deux invocations avec un titre contenant une apostrophe
  Unicode ne retournent aucune confirmation, dont la seconde avec code `1` ;
  aucun succès n'est revendiqué. Une unique correction technique remplace le
  titre par ASCII : Telegram confirme alors en une partie, corps `1857`, total
  `1974`. L'artefact du message reste UTF-8 et inchangé.

### 9.9 Fiche d'approbation

```text
Phase : 1 — Orchestration avant retrieval
Date d'approbation : 2026-08-27 02:15
Résumé des changements : classifieur LLM quatre voies ; contrat grille isolé sans query/search ; action routeur conservée ; revues spéculatives pré-observation retirées ; axes/scope/budgets contrôlés mécaniquement ; type pré-observation provisoire ; inventaire cartes représentatif.
Tests déterministes : rouge/vert EXP-013 et EXP-016 ; 189/189 ciblés ; 284/284 élargis ; build Debug/x64 0 warning/0 erreur.
Probes réelles : ordered 20 cartes rejeté sémantiquement ; representative 10 et 20 cartes utile ; EvidenceBundle citable.
Runs live : P1 intégré 21,010 s jusqu'à 20 preuves ; P2–P5 22,803–27,922 s ; Maintenance 24,639 s ; simple 15,644 s ; document nommé 17,672 s ; ambigu clarifié sans outil.
Inspection humaine : front matter ordered refusé ; lot representative contient de nombreux objets culinaires source réels et du bruit visible à auditer en phase 2.
Métriques obtenues : 5/5 permutations sous 30 s ; deux appels LLM ; backend P1 179 ms ; 20 preuves/10 documents ; aucune query, fuite, disparition de scope ou terme interdit.
Artefacts : phase1-exp012-exp018-variant-g-g1-analysis.md ; EXP-012 à EXP-018 JSON/TRX ; reports live-content-card-inventory 015849, 020042 et 020709.
Variantes rejetées : A, B principale, C, C.1, D, E au seuil, E.1 universelle, F, F.1 et ordered.
Risques résiduels : bruit des cartes ; type encore provisoire ; audit/compatibilité/retrieval/writer non validés ; clarification ambiguë lente ; worktree très sale.
Régressions ouvertes : BUG-003, BUG-004, BUG-008, BUG-014 et BUG-017 ; elles relèvent des phases suivantes ou d'une optimisation générique séparée.
Pourquoi les critères sont tous satisfaits : huit critères du §9.6 prouvés dans l'analyse consolidée par tests, runs live, observation intégrée et inspection humaine.
Autorisation de passer à la phase suivante : OUI
```

---

## 10. Phase 2 — Audit et compatibilité adaptatifs

État : `EN_COURS`

### 10.1 Objectif

Réduire fortement le nombre d'appels sans dégrader la capacité du LLM à
distinguer une instance, une rubrique, un axe, une instruction et un mauvais
type, puis à décider des rôles compatibles.

### 10.2 Architecture cible

1. Une décision d'identité sémantique par candidat nouveau.
2. Conservation immuable de cette annotation dans l'`EvidenceBundle`.
3. Compatibilité uniquement pour les rôles encore déficitaires.
4. Aucun couple candidat/rôle évalué deux fois sans changement de preuve.
5. Matching mécanique calculé uniquement à partir des annotations LLM.
6. Arrêt d'un audit de rôle dès que sa capacité est suffisante.
7. Reprise uniquement sur les nouveaux candidats ou une preuve révisée.

Les résultats I.0–L.0 imposent une nuance : l'annotation d'identité reste
conservée et traçable par preuve, mais elle ne doit pas devenir un gate dur
capable de supprimer une preuve avant l'affectation tant que cette décomposition
n'est pas approuvée. M.0 mesure donc si la décision LLM candidat×rôle peut être
la décision sémantique opératoire, l'identité autonome restant une annotation
non destructive.

Les résultats N.0–P.0 ferment désormais la recherche d'une identité primaire
query-agnostic comme prérequis offline sur le runtime standard. Même lorsqu'une
brique est grounded et stable sur Cuisine, elle ne généralise pas aux structures
NIST sans arbitrage précision/rappel. La cible de phase devient donc un incrément
vertical EvidenceBundle qui préserve chunks et ancres sans gate, puis remet la
demande réelle au LLM orchestrateur au moment de la décision utile.

### 10.3 Matrice de comparaison

| Variante | Identité | Compatibilité | Décision sémantique | Verdict |
|---|---|---|---|---|
| A | Lot 6 actuel | Tous les rôles | LLM | Baseline rejetée |
| G.0 | Candidats acceptés par C.0 | Pairwise, frontières B.0 | LLM | Référence stable approuvée, non intégrée |
| M.0 | Annotation d'identité conservée mais non bloquante | Pairwise sur les 20 preuves, frontières régénérées | LLM | Rejetée : six faux positifs |
| N.0 | Profil backoffice monolithique actuel | Une completion document entière | LLM puis grounding mécanique | Rejetée : 5 094 tokens > 4 096 |
| N.1 | Annotations offline par fenêtres source immuables | Jusqu'à quatre unités par fenêtre | LLM, provenance/grounding mécaniques | Rejetée : 1/8 JSON, sous-étapes, 581 s |
| N.2 | Identité primaire hiérarchique d'une fenêtre | Zéro ou une unité générée par fenêtre | LLM, provenance/grounding mécaniques | Rejetée mais prometteuse : 7/7 titres, 6/8 evidence |
| N.3 | Sélection d'ancre source existante | Zéro ou un anchorId complet par fenêtre | LLM, texte/provenance mécaniques | Rejetée : 0/8 JSON, instruction OCR engagée |
| N.4 | Sélection d'ancre structurelle compacte | Une anchorKey racine/feuille si structure | LLM, éligibilité/provenance mécaniques | Approuvée comme brique Cuisine : 21/21 stables |
| O.0 | Généralisation NIST inchangée de N.4 | Exactement une clé si structure | LLM, oracle humain hors prompt | Rejetée : 5/9, dont trois sélections forcées |
| O.1 | Abstention sémantique et profondeur neutre | Zéro ou une clé structurelle | LLM, provenance/enum mécaniques | Rejetée : 8/9, hiérarchie encore fausse |
| P.0 | Légitimité indépendante par ancre | Booléen par ancre puis cardinalité mécanique | LLM par ancre, code non sémantique | Rejetée : 5/9, quatre faux négatifs |

Les variantes historiques à biais de position ne doivent pas être réintroduites
sans raison nouvelle : bitmasks, indices compacts ou correspondance ordonnée
implicite.

### 10.4 Mesures obligatoires

- candidats présentés ;
- candidats nouveaux ;
- appels d'identité ;
- appels de compatibilité par rôle ;
- tokens et durée par appel ;
- décisions conservées/réutilisées ;
- taux d'acceptation ;
- faux positifs et faux négatifs inspectés humainement ;
- capacité maximale du matching ;
- rôle bloquant ;
- coût marginal de chaque page.

### 10.5 Critères d'approbation

- aucun candidat réaudité inutilement ;
- aucun rôle déjà couvert réaudité sans motif ;
- décisions stables sur plusieurs permutations d'ordre ;
- absence de biais de position observable ;
- amélioration matérielle du nombre d'appels ou de la durée ;
- précision au moins égale à la meilleure variante historique ;
- matching correct et strictement mécanique ;
- aucune règle métier dans le code.

### 10.6 Journal de phase

- `2026-08-27 02:15` : phase ouverte après approbation formelle de la phase 1.
- Première action autorisée : établir la baseline A réelle des audits d'identité
  et de compatibilité sur le lot représentatif P1, inventorier les caches et
  traces déjà disponibles, puis enregistrer EXP-019 avant toute modification.
- `02:16–02:22` : inventaire code/probes consigné dans
  `phase2/audit-inventory-and-protocol.md`. Le lot de 20 cartes et leurs
  contentCardId/révisions est figé. Le coût maximal actuel est 4 appels
  d'identité + 16 de compatibilité, hors réparations ; les annotations sont
  préservées seulement pour une identité immuable identique.
- La probe live existante mesure l'identité seule parce qu'elle ne fournit aucun
  rôle canonique. EXP-019 est enregistrée avant ajout d'un benchmark test-only
  réunissant snapshot, batches de 6, quatre rôles, timings, tokens, décisions et
  compatibilités. Aucun chemin produit ne sera modifié pour cette expérience.
- `02:23–02:30` : benchmark test-only ajouté, build/smoke Debug/x64 vert
  (`1/1`, zéro warning/erreur), puis baseline A réelle exécutée sur le snapshot
  strictement identique. Résultat : protocole valide, 20 décisions, 7
  approbations, 4 appels d'identité, 8 appels de compatibilité, zéro réparation,
  55,378 s pipeline mesuré.
- `02:30–02:32` : inspection humaine exhaustive consignée dans
  `phase2/exp019-baseline-a-human-review.md`. Deux approbations sont fautives
  (`Collation (vers 16h)` est un axe ; `Pâte levée...` une catégorie/méthode) et
  trois préparations nommées acceptées ne reçoivent aucun rôle. A est fidèle
  comme baseline technique mais refusée comme comportement produit.
- L'optimisation B « rôles déficitaires seulement » n'économiserait aucun appel
  sur ce lot : les quatre rôles restent à 1/5. Avant toute modification produit,
  il faut vérifier le chemin runtime réel de création des définitions de rôle,
  puis enregistrer une expérience B.0 séparant qualité des définitions LLM et
  réutilisation mécanique versionnée.
- `02:33–02:36` : audit runtime confirmé. La présence d'une action initiale
  `llm_router` force `allowPreObservationLlmReview=false`; les rôles label=label
  d'EXP-019 sont donc bien ceux du chemin G.1 actuel et aucune revue postérieure
  ne les enrichit. EXP-020/B.0 est enregistré dans
  `phase2/exp020-b0-role-boundaries-protocol.md` avant adaptation du benchmark.
- `02:37–02:42` : B.0 live est mécaniquement valide sur le lot identique, mais
  ajoute 37,113 s et porte le pipeline de 55,378 à 95,209 s (+71,9 %). Les
  frontières inventent pain/lait/jus, protéines/légumes/céréales et fruit/barre
  de céréales ; elles améliorent Empanadas et Sticks de feta, mais dégradent
  Guacamole et attribuent Petit-déjeuner à `Pâte levée...`. B.0 est rejetée et
  la famille de retouches du prompt de rôles est arrêtée.
- `02:43` : EXP-021/C.0 enregistré avant adaptation du benchmark. L'ablation
  gardera les rôles label=label et testera seulement le vrai contrat LLM de
  définition atomique après observation, en remplacement du type provisoire et
  de la règle vide.
- `02:44–02:49` : C.0 live valide en 56,136 s pipeline (+1,4 % versus A),
  9 appels, 6 approbations. Le type `recette de cuisine` et une règle complète
  corrigent l'axe `Collation (vers 16h)` tout en conservant les cinq préparations
  claires. `Pâte levée...` reste toutefois un faux positif confirmé par son
  passage source ; C.0 échoue donc comme solution complète. La brique de
  définition est conservée pour une nouvelle ablation de contexte de
  compatibilité, sans modification produit.
- `02:50` : EXP-022/D.0 enregistré. C.0 est conservée ; le benchmark restituera
  demande/type/règle LLM au prompt de compatibilité one-role/batch-6, sans
  définition métier, nouveau schéma ou appel supplémentaire.
- `02:51–03:02` : un premier D.0 a été déclaré non causal car la définition
  variait. Trois runs appliquant exactement la définition C.0 ont ensuite été
  exécutés aux rotations 0/7/13. Tous les protocoles sont valides, mais les
  approbations varient 6/7/5 et les couples candidat-rôle ont des Jaccard
  0,40–0,60. D.0 est rejetée pour biais listwise persistant.
- `03:03` : EXP-023/E.0 enregistré avant code test-only. Nouvelle forme : un
  EvidenceId par appel, quatre labels exacts en sortie, aucun index/bitmask ni
  comparaison entre candidats ; trois rotations de l'ordre des rôles prévues.
- `03:07–03:10` : E.0 exécutée sur les rotations de rôles 0/1/2. Les 18
  protocoles sont valides, mais les couples varient 15/7/8 et plusieurs plats
  passent de tous les rôles à un seul. E.0 est rejetée pour biais d'ordre.
- `03:11` : EXP-024/F.0 enregistré comme référence diagnostique pairwise
  booléenne (24 couples). Il ne s'agit pas d'une cible produit implicite : son
  coût et sa stabilité décideront du recul suivant.
- `03:13–03:16` : F.0 exécute 72/72 protocoles identiques sur trois ordres,
  environ 30–31 s pour 24 couples. La position est éliminée, mais le modèle
  marque Muffins, Guacamole et Sticks de feta compatibles avec les quatre rôles.
  F.0 est stable mais refusée comme qualité produit.
- Vérification locale : seul Qwen3-4B Q5_K_M est disponible ; aucun modèle plus
  grand n'est actuellement installé/servi.
- `03:17` : EXP-025/G.0 enregistré comme dernière composition locale autorisée :
  pairwise F.0 + frontières LLM B.0 gelées. Un échec imposera le pivot externe
  modèle/inférence ou affectation finale.
- `03:20–03:24` : G.0 obtient 72/72 décisions identiques sur rotations 0/7/13
  et une matrice humainement cohérente de dix couples. Pairwise : 40,7–41,6 s ;
  avec la génération B.0 des rôles : 77,8–78,7 s. La compatibilité est approuvée
  comme référence, pas comme intégration ni performance end-to-end.
- `03:25` : EXP-026/H.0 enregistré pour rejouer uniquement la génération des
  frontières deux fois et la comparer à B.0 avant toute modification produit.
- `03:34–03:39` : H.0 exécute deux générations indépendantes. Les deux sorties
  sont octet-pour-octet identiques à B.0, y compris arguments bruts, 924 tokens,
  une tentative et hash
  `19b228af180250d7902bd60ac1756b75a39b89926d51cdd342d2cc6eb74478af`.
  Durées : 33,537 s et 33,390 s contre 37,113 s en B.0. La répétabilité runtime
  est approuvée ; le contenu B.0 seul ne l'est toujours pas.
- `03:40` : EXP-027/I.0 enregistré avant code test-only. Il isole le contrat
  d'identité produit existant avec `batch=1`, type/règle C.0 et zéro rôle, sur
  les vingt preuves puis trois rotations si la précision initiale passe.
- `03:43–03:47` : I.0 est mécaniquement valide (20/20, zéro réparation, zéro
  compatibilité, 69,376 s, 21 581 tokens), corrige l'axe et conserve les cinq
  recettes claires, mais accepte 10/20 dont trois faux positifs bloquants. E19
  persiste ; les rotations conditionnelles sont annulées et I.0 est rejeté.
- `03:48` : recul architectural documenté avec Contextual Retrieval et RAPTOR.
  Le corpus possède déjà le contexte hiérarchique nécessaire. EXP-028/J.0 est
  enregistré pour réactiver test-only le résolveur de libellé existant avant le
  même juge mono-candidat, sans nouveau prompt ni code produit.
- `03:51–03:56` : J.0 est mécaniquement valide (13 résolutions + 20 jugements,
  zéro réparation, 142,014 s), mais n'améliore qu'un cas (`De plus` devient
  `Trifle aux cerises`). Les trois faux positifs critiques persistent et deux
  titres OCR sont perdus. J.0 est rejeté sans rotations.
- `03:58` : machine auditée : 31,8 Gio RAM, Quadro P520 4 Gio, 1 352,5 Gio
  libres, runtime CUDA b10098. EXP-029/K.0 est enregistré pour comparer le même
  Qwen3-4B-Instruct-2507 en Q8_0 à I.0, avec restauration obligatoire de Q5.
- `04:01–04:12` : Q8_0 téléchargé/vérifié (4 280 405 216 octets, SHA-256
  `260B5B5B...C19662`), servi sainement à 3 675 MiB VRAM puis testé. Les vingt
  décisions sont identiques à I.0, trois faux positifs compris, mais l'identité
  passe de 69,376 à 147,451 s (+112,5 %). K.0 est rejeté. Q5 est restauré sain
  sur 1234, Q5_K_M/4096/un slot/3 385 MiB.
- `04:13` : EXP-030/L.0 enregistré avant téléchargement. Il mesure un
  Qwen3-8B Q4_K_M officiel, raisonnement désactivé, comme plafond de capacité ;
  le changement de post-training est explicitement non causal.
- `04:14–04:39` : modèle officiel 8B téléchargé et vérifié (5 027 783 488
  octets, SHA-256 `D98CDCBD...5785`). Le premier run n'est pas interprétable :
  le template Qwen émet un bloc `<think>` même en non-thinking et entre en
  conflit avec la grammaire JSON Schema de llama.cpp. Une sonde avec le template
  builtin ChatML valide ensuite exactement le schéma sans changer le prompt
  applicatif.
- Le run L.0 comparable exécute 20/20 jugements, zéro réparation et 21 581
  tokens en 180,846 s d'identité. Il rejette les vingt candidats : les trois
  faux positifs 4B et l'axe sont supprimés, mais les cinq recettes claires sont
  aussi perdues. L.0 est rejeté sans rotations ; la série dense locale est
  fermée. Q5_K_M/4096/un slot est restauré sain sur 1234, PID 8692.
- `04:45` : EXP-031/M.0 enregistré avant modification test-only. Le gate
  d'identité n'éliminera aucun des vingt EvidenceIds ; une génération réelle
  des frontières H.0 précédera 80 jugements pairwise G.0. Les annotations
  d'identité I.0 restent tracées, mais seul le LLM décide l'utilité pour chaque
  rôle et le code ne fera que le matching mécanique.
- `04:46–04:56` : smoke propre sans warning, puis M.0 exécute un appel de rôles,
  zéro identité et 80/80 compatibilités valides en 189,524 s. Les cinq recettes
  claires ont un rôle et la projection G.1+rôles+pairwise vaut 210,168 s, mais
  six non-candidats reçoivent un rôle. L'axe `Collation (vers 16h)` et cinq
  fragments/rubriques sont des faux positifs manifestes : M.0 est rejeté sans
  rotations.
- La génération après redémarrage Q5 produit le hash `3a389d7d...54ecf`, pas le
  hash B.0/H.0 `19b228af...74478af`. H.0 est donc requalifié en répétabilité
  intra-processus seulement. Les gates runtime identité seule et compatibilité
  seule sont tous deux refusés ; le prochain recul audite l'enrichissement LLM
  backoffice et les unités matérialisées à l'ingestion.
- `04:57–05:03` : audit backoffice read-only terminé. Les dix profils Cuisine
  actifs sont `deterministic_canonical_v5` ; 76 anciens jobs indiquent un profil
  `llm_backoffice_v1`, mais leurs révisions sont obsolètes et leurs cartes sont
  uniquement des cartes déterministes recopiées. Le worker charge en dur
  `deterministic_v1`, absent des révisions actives. La projection SQL favorise
  un doublon LLM exact mais laisse coexister les autres cartes déterministes.
- `05:03` : EXP-032/N.0 enregistré avant le harness et avant toute mutation. Il
  appelle le service d'enrichissement produit exact, en mémoire, sur la révision
  active des 30 recettes : 25 cartes v5, 12 sections et 8 extraits, sans écriture
  base.
- `05:03–05:06` : export read-only et test opt-in ajoutés. Smokes verts. Le
  premier live révèle une faiblesse du rapport face à une réponse sans
  `choices`; seule cette capture test-only est rendue sûre, puis le même appel
  est rejoué sans changer entrée, prompt ni paramètres.
- `05:06` : N.0 produit HTTP 400 : 5 094 tokens de prompt pour un contexte 4 096,
  22 498 caractères de requête, zéro completion, zéro profil, zéro carte. Le
  protocole imposait l'arrêt : aucun second document, aucune retouche de prompt,
  aucun enqueue et aucune correction isolée du pointeur de baseline.
- `05:10` : EXP-033/N.1 enregistré avant code. Il remplace le document
  monolithique par huit fenêtres source immuables, pages attachées depuis le
  chunk et evidence exacte. Le LLM reste seul décideur de l'unité sémantique ;
  le code ne fait que budget, schéma, grounding, provenance et déduplication
  stricte.
- `05:10–05:15` : les huit textes N.1 sont prouvés identiques à N.0 ; l'export
  ajoute UUID, ordinal et pages 5/11/12/16/24/30/31/35. Harness test-only ajouté,
  smoke vert sans warning, puis unique canary Q5 lancé.
- `05:15–05:25` : N.1 garde les prompts à 571–715 tokens, mais sept sorties
  saturent 450 tokens et produisent un JSON tronqué. Le seul JSON valide contient
  quatre items, dont trois étapes locales interdites. Total 581,190 s contre un
  seuil 240 s. N.1 est rejeté sans rotation ni retouche.
- L'inspection des sorties brutes constate néanmoins que le premier titre des
  sept fenêtres claires correspond 7/7 à leur unité principale. Cette observation
  ne permet pas une sélection post-hoc par le code, mais justifie une ablation
  de cardinalité LLM explicite.
- `05:28` : EXP-034/N.2 enregistré avant code. Même entrée et même provenance ;
  le LLM doit retourner zéro ou une identité primaire hiérarchique, avec champs
  bornés et 180 tokens maximum. Aucun exemple métier ni filtre lexical n'est
  ajouté.
- `05:28–05:32` : harness N.2 séparé ajouté, smoke vert, puis unique live. N.2
  produit 8/8 HTTP, JSON et `stop`, 947 tokens de sortie et 172,959 s. Les sept
  fenêtres claires ont leur bon titre principal.
- Deux portes restent rouges : l'evidence page 5 est corrompue à la limite
  `maxLength` (`boucherde`) et celle de la page 31 n'est égale qu'après
  normalisation d'espaces. La fenêtre OCR synthétise en plus `Crème pâtissière à
  la vanille avec fraises` au lieu de s'abstenir. N.2 est rejeté sans rerun.
- `05:33–05:38` : recul architectural documenté à partir de Contextual Retrieval,
  GraphRAG, RAPTOR, Late Chunking et llama.cpp. Le constat local confirme que les
  sept bons chunks possèdent déjà un `headingPath` racine exact ; le mauvais
  chunk alternatif n'a ni heading, section ni sectionId.
- `05:39` : EXP-035/N.3 enregistré avant code. Le LLM ne générera plus aucun
  texte : il sélectionnera zéro ou un ID parmi des ancres exactes dérivées du
  heading/section/lignes. Le code garantit identité et provenance, le LLM choisit
  la granularité. Deux permutations sont conditionnées à un run initial parfait.
- `05:39–05:43` : export N.3 produit 64 ancres hashées, smoke vert, puis run
  canonique. Les huit réponses atteignent 50 tokens au milieu du SHA-256 : 0/8
  JSON, 166,339 s > 120 s. Les permutations sont annulées.
- Les préfixes uniques sous grammaire révèlent néanmoins les choix engagés :
  sept racines exactes, mais l'instruction `Dans une casserole...` sur la fenêtre
  OCR. La sélection extractive est validée comme direction ; l'éligibilité de
  lignes brutes comme identité est rejetée.
- `05:48` : EXP-036/N.4 enregistré avant code. Seuls heading/section sont
  éligibles ; une fenêtre sans structure reste dans le corpus mais ne déclenche
  aucun profil primaire. Les IDs complets restent en preuve et des clés stables
  de 16 hex sont exposées au LLM. Rotations conditionnées à un run parfait.
- `05:48–05:55` : harness N.4 et clés compactes ajoutés côté test uniquement.
  Smoke vert. Les ordres canonique, inverse et hashé donnent chacun 7/7 contrats,
  7/7 racines Cuisine et le même contrôle sans appel. Les 21 sélections sont
  identiques ; chaque run reste sous 120 s. N.4 est approuvé comme brique Cuisine,
  pas comme contrat multi-domaine ni comme produit.
- `05:55–06:03` : EXP-037/O.0 enregistré avant tout appel non-Cuisine. Le document
  NIST SP 800-37r2, sa révision, neuf fenêtres et leurs ancres sont figés. La
  vérité humaine est inspectée et écrite avant le run : cinq sélections, trois
  abstentions sémantiques et un contrôle sans ancre/sans appel.
- `06:03–06:06` : harness O.0 compilé puis unique run canonique. Transport et
  contrat sont verts (8/8 HTTP, `stop`, JSON, enum grounded), 4 119 tokens et
  46,063 s, mais la vérité n'est respectée que 5/9. Le niveau 0 de la fenêtre 1
  est une note mal promue ; les fenêtres 4, 5 et 9 forcent respectivement deux
  fragments de rôles et une citation. Ordres inverse/hash annulés.
- `06:07` : O.0 rejeté comme contrat multi-domaine. Cause contractuelle établie :
  `minItems=1` interdit l'abstention dès qu'un pseudo-heading existe et
  `highest-level` favorise indûment la profondeur. Le filtrage mécanique ne sera
  pas étendu à une décision sémantique.
- `06:08` : EXP-038/O.1 enregistré avant modification. Deux changements causaux
  seulement sont autorisés : sortie 0..1 et consigne neutre vis-à-vis de la
  profondeur. Même entrée/oracle NIST ; rotations seulement après 9/9, puis
  régression Cuisine et enfin canary non-Cuisine tenu à l'écart.
- `06:09–06:12` : RFC 9110 choisi par métadonnées et figé avant le premier appel
  O.1 : huit fenêtres, quatre sélections et quatre abstentions humaines. Harness
  O.1 compilé avec exactement les deux changements enregistrés.
- `06:13–06:15` : unique O.1 canonique. 8/8 protocoles, 4 477 tokens et 40,777 s.
  Les trois pseudo-headings deviennent correctement `[]`, mais la fenêtre 1
  sélectionne encore la note niveau 0 : 8/9. Toutes les portes aval annulées et
  O.1 rejeté sans retouche.
- `06:16` : EXP-039/P.0 enregistré avant code pour supprimer la compétition
  listwise : un booléen indépendant par ancre, résolution mécanique 0/1/ambiguë,
  prompt exact sans exemple. Vérité NIST par ancre figée.
- `06:17–06:20` : canary aveugle Coca-Cola 2024 choisi après gel du prompt P.0,
  exporté puis annoté avant code/appel : sept ancres vraies et le faux heading
  `Derivative Instruments` sur un texte de leases. Harness P.0 test-only ajouté ;
  smoke vert.
- `06:21–06:23` : unique P.0 NIST canonique. 9/9 contrats, 4 492 tokens et
  33,203 s, mais 5/9 seulement : le faux niveau 0 est rejeté, tandis que quatre
  des cinq titres légitimes deviennent faux négatifs. Rotations, Cuisine, RFC et
  canary aveugle annulés.
- `06:24` : recherche d'identité primaire query-agnostic offline fermée pour la
  phase/runtime. Aucun nouveau prompt local autorisé. Checkpoint architectural
  vertical `retrieval → EvidenceBundle → writer/finalizer → citations/UI` ouvert
  avant toute modification produit.
- `06:25–06:31` : checkpoint vertical read-only terminé. Le `EvidenceBundle`
  canonique, la fenêtre writer et le vérificateur existent déjà. Trois pertes
  mécaniques sont isolées : structure répartie entre `SelectionHints` et
  `CodeHints`, héritage des hints parent par les voisins, et mapper UI qui
  n'alimente pas les champs riches existants. Diagnostic détaillé dans
  `phase2/evidencebundle-vertical-checkpoint.md`; aucun code produit modifié.
- `06:33` : EXP-040/Q.0 enregistrée avant exécution. Cas Q019, banque et hash,
  runtime, commande, métriques, seuils et règle d'un seul run sont figés dans
  `phase2/exp040-q0-evidence-transport-baseline-protocol.md`.
- `06:35–06:53` : l'utilisateur signale à juste titre l'absence de rapports
  Telegram malgré plusieurs jalons. Q.0 est suspendue, les instructions de
  reprise sont relues et un rapport consolidé UTF-8 de 3 354 caractères est
  envoyé avec succès. SHA-256 du message :
  `EDC0E70DFD346BE72BE2A4E4574D85C9A7CD402E9BBF0B71CD6CFB0AC44DEFDB`.
  La cadence horaire et les déclencheurs de jalon deviennent des portes du plan.
- `06:57–06:58` : EXP-040/Q.0 exécutée une seule fois. VSTest `1/1` en 52 s,
  mais réponse de clarification, zéro outil, zéro source, `no_sources` et payload
  nul. Trace : 2 appels LLM, 3 013 prompt + 207 completion = 3 220 tokens,
  51,724 s total. Le vert ne mesure que la capture du harnais.
- `07:02` : Q.0 rejetée avant retrieval et analysée dans
  `phase2/exp040-q0-evidence-transport-baseline-analysis.md`. La responsabilité
  amont rouvre la phase 1 ; aucun test/code EvidenceBundle n'est autorisé tant que
  le routeur ne conserve pas les facettes cumulatives.

---

## 11. Phase 3 — Recherche adaptative et mémoire de rendement

État : `APPROUVEE`

### 11.1 Objectif

Permettre au LLM de choisir rationnellement entre pagination, navigation,
cartes, recherche ciblée, rédaction ou insuffisance à partir d'une mémoire de
travail compacte et factuelle.

### 11.2 État minimal transmis au LLM

- demande originale ;
- mission structurée active ;
- définition actuelle de l'objet source ;
- rôles et frontières décidés par le LLM ;
- capacité requise et capacité disponible par rôle ;
- exemples d'identités déjà acceptées ;
- preuves refusées pertinentes ;
- routes exécutées ;
- offsets consommés ;
- rendement accepté/refusé par route et par rôle ;
- requêtes déjà exécutées ;
- ancres jugées inéligibles ;
- budget restant.

### 11.3 Règles mécaniques permises

- refuser un appel strictement dupliqué ;
- poursuivre la page suivante d'une pagination déjà ouverte ;
- appliquer le scope décidé par le LLM ;
- préserver les annotations par identité immuable ;
- réserver un tour d'audit après une récupération productive ;
- signaler qu'un cycle complet ne tient plus dans le budget.

### 11.4 Décisions qui restent au LLM

- pertinence d'une route ;
- formulation d'une requête ;
- opportunité de paginer ou pivoter ;
- interprétation du rendement ;
- besoin d'une clarification ;
- décision de rédiger ou poursuivre ;
- déclaration d'insuffisance.

### 11.5 Critères d'approbation

- aucune répétition de requête non productive ;
- aucune navigation générale choisie faute de visibilité sur le rôle manquant ;
- justification traçable de chaque pivot ;
- rendement par route conservé entre les tours ;
- pas de perte des compatibilités après reconstruction du bundle ;
- passage au writer dès que la couverture est réellement possible ;
- arrêt honnête lorsque la couverture ne peut pas être obtenue dans le budget.

### 11.6 Journal de phase

À remplir lors de l'exécution.

---

## 12. Phase 4 — Writer, EvidenceBundle et cartes UI

État : `APPROUVEE`

### 12.1 Objectif

Produire une réponse à partir d'un unique ensemble de preuves approuvées, sans
perte d'identité, puis rendre des cartes UI exactement alignées sur les
EvidenceIds utilisés.

### 12.2 Préconditions du writer structuré

- matching distinct complet ;
- vingt candidats distincts disponibles pour une grille de vingt cellules ;
- identités et compatibilités présentes ;
- provenance mécanique complète ;
- budget suffisant pour writer, revue et éventuelle réparation.

### 12.3 Contrat du writer

- utiliser uniquement les EvidenceIds proposés ;
- une valeur visible concrète par cellule ;
- aucune source réutilisée lorsque l'unicité est exigée ;
- aucune page ou provenance inventée ;
- possibilité de demander de nouvelles preuves si le contrat est impossible ;
- réponse vérifiable mécaniquement avant revue sémantique.

### 12.4 Réparation

- revue focalisée sur les cellules réellement refusées ;
- alternatives locales d'abord si le LLM les juge adaptées ;
- nouvelle collecte seulement si le LLM constate une lacune ;
- aucune concaténation de tentatives ;
- aucun index positionnel ambigu ;
- EvidenceIds directs dans les contrats de révision.

### 12.5 Vérification de provenance

Pour chaque cellule :

- [x] EvidenceId unique ;
- [x] contenu visible non vide ;
- [x] docId ;
- [x] revisionId actif ;
- [x] fichier ;
- [x] page ;
- [x] chunkId ou anchorId ;
- [x] sourceHash ;
- [x] carte UI unique ;
- [x] passage visible soutenant réellement la valeur.

### 12.6 Critères d'approbation

- aucune divergence EvidenceBundle/writer/UI ;
- réparations focalisées et bornées ;
- revue sémantique correcte sur cas adversariaux ;
- tests d'ouverture réelle des sources ;
- inspection humaine du contenu visible ;
- aucune réussite purement mécanique classée comme qualité approuvée.

### 12.7 Journal de phase

- 2026-08-27 : protocole A1 gelé avant correction. Il localise les pertes de
  `revisionId`, l'empreinte courte, l'absence de champs ancre/carte, la
  surcharge `content-card:*` dans `chunkId`, le vérificateur incomplet et le
  lanceur non strict (`PRV-166`).
- TDD rouge : 6/6 client, 2/2 backend, 2/2 vérificateur et 2/2 lanceur échouent
  pour les raisons attendues. Aucun test n'est réécrit pour masquer le défaut.
- Backend : `rag.search` charge la `document_revision` active et expose
  `revisionId` plus `encode(source_hash,'hex')`. Déploiement distant A16 vert et
  sonde A17 : cinq hits sur cinq avec révision et SHA-256 64 hex (`PRV-169`).
- Client : `EvidenceItem`, `SourceRef`, payload, parser, carte, mémoire, writer
  et working state transportent les identités typées. Le vérificateur exige une
  identité corpus complète et le lanceur impose le hash exact (`PRV-167` à
  `PRV-168`).
- Le premier live A18 sans `-p:Platform=x64` a chargé un binaire obsolète. Il
  est `INVALIDE_CONFIGURATION`. Le rerun x64 a utilement refusé
  `missing_revision_id`, puis A19 a isolé une seconde perte :
  `NormalizeRagHits` omettait `revisionId`. Rouge 0/1, vert 1/1 (`PRV-170`).
- Q011 A21 : 67 155 ms, E1 vérifiée, révision
  `320e6aa5-5e2a-5aba-da4d-f1d03fe88e4f`, SHA-256
  `aa11e234...d25c18`, page 5, chunk `88433710-...`, mémoire stable complète.
- P4-NORMES-001 A22 reste `TESTEE_NON_APPROUVEE` : insuffisance honnête,
  zéro source, 127 576 ms. P4-NORMES-073 A23 reste
  `TESTEE_NON_APPROUVEE` : 45 preuves, dérive IEC 82079, préflight
  `4341/4096`, zéro source, 236 178 ms. Q156 A24 clarifie correctement et sans
  source en 49 545 ms, mais BUG-063 reste ouvert (`PRV-171`).
- Inspection de la WinUI réelle : une première exécution a prouvé que le
  payload existait mais qu'aucune carte n'était rendue. A26 rouge a figé le
  défaut ; A27 vert lie `ParsedSources` au template assistant. A28 est vert
  1 307/1 307 (`PRV-172`).
- A29 : la carte E1 est visible inline dans le message, `Ouvrir` lance
  réellement `30-recettes-preferees-des-francais.pdf#page=5`, et Edge affiche
  `5 of 36` sur la page Bœuf bourguignon (`PRV-173`). Le contrôle interactif de
  l'ordinateur est libéré immédiatement après l'observation.
- Régressions finales : backend 2 048/2 048, transversale 240/240, client x64
  1 307/1 307, `git diff --check` sans erreur whitespace autre que les warnings
  EOL connus.
- Décision : phase 4 `APPROUVEE` sur sa responsabilité d'intégrité. La
  complétude Q011, le document nommé, le multisource, BUG-063 et la performance
  sont transférés sans requalification à la phase 5/6 (`PRV-174`).

---

## 13. Phase 5 — Questions simples, mémoire, clarification et généricité

État : `EN_COURS`

### 13.1 Scénarios obligatoires

- dessert simple ;
- question factuelle exacte ;
- document nommé ;
- Q019 ;
- suivi conversationnel sur le document actif ;
- autre planning sans réutilisation involontaire ;
- demande ambiguë nécessitant réellement une clarification ;
- demande large mais suffisamment claire ;
- réponse courte à une clarification ;
- hors corpus ;
- autre catégorie ;
- autre langue prise en charge ;
- scénario structuré non Cuisine.

### 13.2 Mémoire

- la mémoire de conversation conserve l'intention et les clarifications ;
- la mémoire de preuves conserve les identités utilisées/refusées ;
- la mémoire de projet M2 et la `ContextEnvelope` doivent être évaluées selon
  l'ADR avant activation complète ;
- aucune mémoire n'est citable comme preuve finale ;
- une nouvelle discussion ne charge pas l'historique complet dans le prompt ;
- les éléments sont récupérés à la demande et budgétés.

### 13.3 Critères d'approbation

- aucun scénario simple ne prend la voie lourde ;
- document nommé préservé ;
- suivi correctement résolu ;
- clarification utile, fidèle et non répétitive ;
- aucune clarification facultative inutile ;
- aucune fuite de contexte entre projets ;
- sources toujours issues du corpus actif ;
- généricité démontrée hors Cuisine.

### 13.4 Journal de phase

#### Mémoire et clarification — A1 à A4

- protocole fonctionnel/mémoire gelé avant correction ;
- BUG-063 reproduit en rouge puis corrigé : le terminal de clarification écrit
  maintenant `ConversationMemory.Outcome=clarification_requested` ;
- régressions mémoire vertes ;
- Q156 live A4 confirme la clarification en 65 365 ms, zéro source finale,
  sans requalification en insuffisance ; TRX SHA-256
  `491A89EE94D28D20E4DD37F82B80E47E12A3A39535B24B2946A41BBA9A756E15`,
  JSONL SHA-256
  `B7AD750D3A3B0047C3A74EDD3EE304BE290D3E4D739A08AC16CE3C1ACB7C179B`.

#### Couverture plate et contexte — A5 à A20

- A5 rouge/vert : la couverture de la demande originale précède la sélection ;
- A7 live rejeté : outil monolithique trop lourd et préflight 4K ;
- A8–A10 remplacent ce contrat par quatre décisions compactes : sélection,
  manque global, manque de contexte même document, clarification ;
- A11–A14 ajoutent la transition générique `documents.context` ;
- A15 live atteint le contexte puis repart à tort en recherche globale ;
- A16–A19 ajoutent `submit_flat_evidence_context_gap`, gardes mécaniques et
  régressions ;
- A20 live choisit le bon contexte mais matérialise zéro nouvelle preuve : la
  cause suivante est requalifiée en contrat `content_claim`, pas en retrieval.

#### Contrat `content_claim` — A21 à A32b

- analyse A20 : une extraction multi-claims n'est pas un inventaire
  d'instances nommées ;
- A21 rouge/vert : désactivation de l'audit `named_item` pour les claims,
  comptage par EvidenceId, conservation de plusieurs claims de la même page et
  vérification mode-aware ;
- 173/173 AgentV2 et 199/199 source-backed + mémoire ;
- A28 live atteint sélection, writer et vérification mais le juge final applique
  encore la taxonomie « étape nommée » et invoque une recette traditionnelle ;
- A29–A32b rendent le juge mode-aware et interdisent la connaissance externe ;
  199/199 verts, SHA A32b
  `AB7E42E5CBF36D970590E847E34A974FA662FE06D3044A7FD9D0AD094129C80E`.

#### Cohérence, localité et révision — A33 à A46

- A33 termine en 110 359 ms mais fusionne deux documents et déforme une action ;
  verdict produit rejeté ;
- A34 préenregistre un contrat général de cohérence de provenance ;
- A35 rouge puis A36 vert : chaque claim sélectionné reçoit une citation locale,
  sans interdire plusieurs claims d'une même page ;
- A37b 30/30, A38 173/173, A39 199/199 ;
- A40 : le juge détecte correctement la fusion mais `revise` sans preuve rejetée
  rouvre la recherche et finit en refus sûr à 240 668 ms ;
- A41 rouge puis A42b vert : la sélection est conservée et le writer est rappelé
  directement, avant une seconde vérification et une seconde revue ;
- A43 30/30, A44 174/174, A45 200/200 ; SHA A45
  `35C8F6C0B874131016DF1C71EB0741DF16E2875818A09A006E7A8C0823CB1603` ;
- A46 Q011 livre plusieurs étapes, trois claims localement cités, une seule
  recherche et deux cartes sources en 125 336 ms ; la variante du second PDF est
  explicitement distinguée. Verdict : `RESOLU_FONCTIONNEL_AVEC_RESERVE_DE_BANQUE`
  parce que le PDF cible caché n'est pas présent dans la question utilisateur.
  TRX SHA `F8F6743E6A864E836D2119FC520DC5FF2258BBBD7F8D634420C6B1265227AE7D`,
  JSONL SHA `3D1F4E127E4580FD886E76B85F1C5D95FCA76DE7623D49A02A03202A55265A1B`.

#### Document nommé — A47 à A122, défaut courant

- P4-NORMES-001 nomme explicitement ANSI B11.0-2023 et demande sept points ;
- A47 expire à 300 005 ms, sans réponse ;
- le premier contrat du routeur reconnaît le document, mais la réparation d'une
  grille aux axes inventés perd ensuite `RequestedDocumentName` ;
- le checkpoint confond la forme de restitution (« résumé structuré en sept
  points ») avec une forme qui devrait déjà exister dans la source ;
- il choisit ensuite une recherche globale malgré le document explicite, puis
  n'atteint `documents.context` sur ANSI qu'à 275 669 ms ;
- TRX SHA `2ADA743295F8C97488924DDFC3E136CB17DD8695F343EE22D7FBCA255FD64602`,
  JSONL SHA `EDDD4E2BCFEE5921248CDBD6CE5E15CD3A2BA96113BE0FAD31869B1DA71616B7` ;
- A48 établit trois rouges : structure de sortie, continuité de l'ancre pendant
  réparation et correspondance de référence à frontière de nom canonique ;
- A49 passe 3/3, A50 82/82 routeur-vérificateur-architecture, A51 174/174
  AgentV2 et A52 200/200 source-backed + mémoire ;
- correction déterministe approuvée : Qwen3 reste décideur du contexte, le code
  ne restaure que la référence déjà explicite et contrôle la frontière du nom ;
- aucun hardcoding ANSI/Normes. Première analyse :
  `phase5/phase5-a47-named-document-output-shape-analysis.md`.

Suite causale A53–A115 :

- A53–A58 préservent un pool de claims utile distinct de la forme de sortie ;
- A59–A67 comparent navigation, contexte et recherche focalisée, puis ajoutent
  une transition générique vers la recherche dans le document reconnu ;
- A68–A73 bornent les révisions directes du writer ;
- A74–A83 ajoutent le tournoi sémantique Qwen pour remplacer une sélection
  périmée par des preuves nouvellement observées ;
- A84–A90 compactent la récupération de contexte sans dépasser les 4 096
  tokens ; A91 atteint presque le writer mais expire à `894 s` ;
- A92–A96 conservent le pool partiel choisi par Qwen ; A97 montre qu'un pool
  seul ne suffit pas si la boucle sans rendement continue ;
- A98–A103 corrigent la transition sans rendement : un gap Qwen valide ne peut
  plus ouvrir une sélection forcée et la collecte est fermée avant une vraie
  sélection ; `1323/1323` verts ;
- A104 termine en `572653 ms` par insuffisance sûre, mais répète encore trop
  d'actions compactes ;
- A105–A110 ajoutent une décision terminale Qwen obligatoire après une dernière
  route distincte ; `1323/1323` verts ;
- A111 produit sept points et trois sources en `594210 ms`, mais l'audit exact
  rejette quatre points : seuls `1`, `3` et `7` sont soutenus par leur fragment ;
- la cause A111 est un checkpoint ultérieur invalide (`finish_reason=length`)
  qui effaçait à tort le dernier gap sémantique valide et ouvrait une sélection
  prématurée ;
- A112 rouge, A113/A114 verts : un checkpoint invalide ne remplace plus le pool,
  le verdict ni le motif de la dernière décision Qwen valide ; `1/1`, `10/10`,
  `181/181` et `1323/1323` ;
- A115 confirme live la garde : trois verdicts `continue`, pool partiel conservé,
  terminal Qwen explicite, insuffisance sûre, aucune sélection writer forcée ;
  `551387 ms`, zéro source finale, donc **pas d'approbation fonctionnelle ni de
  performance** ;
- A116 audite le pool A115 : `E41` et `E73` sont le même chunk, mais des preuves
  distinctes déjà récupérées (`E77`, `E78`, `E89`, `E90`) n'ont jamais été
  présentées ensemble au juge ;
- BUG-095 : un premier verdict `continue` quittait le tournoi alors que des
  challengers déjà récupérés restaient invisibles ;
- A117 rouge, A118 vert, A119 régressions : le tournoi épuise désormais les
  candidats déjà disponibles avant une nouvelle recherche ; `1/1`, `4/4`,
  `181/181`, `1323/1323` ;
- A120 confirme live la revue des challengers déjà récupérés : deux fenêtres
  d'adéquation, quatre actions, insuffisance honnête en `538559 ms` ; le gain de
  `2,3 %` sur A115 ne satisfait ni fonctionnalité ni performance ;
- A121 mesure le summary flow historique avec ses paramètres de production :
  le premier prompt contient `4360` tokens pour un contexte `4096`, aucune
  réponse n'est produite ;
- A122 borne seulement les lots à `3200` caractères : un résumé est produit en
  `193518 ms`, mais à partir de quatre chunks seulement, dont un profil et trois
  fragments bibliographiques pages 131–132 ;
- l'audit humain rejette A122 : il affirme que la norme est obligatoire alors
  que la page 2 dit explicitement « voluntary use » ; le faux sens provient
  d'un titre de carte tronqué hors de sa phrase ;
- décision : ne pas inverser simplement l'ordre des routes et ne pas promouvoir
  le summary flow historique ; comparer une capacité d'aperçu structurel dont
  tous les chunks exacts entrent dans l'`EvidenceBundle`.

Audit consolidé :
`phase5/phase5-a111-source-alignment-and-a114-state-contract-audit.md`.

Recul architectural détaillé :
`phase5/phase5-a120-a122-named-document-summary-architecture-step-back.md`.

#### Reporting et contrôle de l'ordinateur

- les rapports Telegram confirmés antérieurs couvrent notamment A15, A33, A40,
  A77 et A91 ; la tentative A104 n'est pas comptée comme confirmée faute de
  sortie d'accusé récupérée ;
- le palier A115 consigne l'audit A111, BUG-094, les régressions et l'issue live
  sûre mais encore non fonctionnelle ; envoi Telegram confirmé, corps `1697`
  caractères, total `1805` caractères ;
- le contrôle interactif de l'ordinateur est resté entièrement libéré ; tous les
  travaux A1–A122 ont utilisé fichiers, CLI, tests et services HTTP uniquement ;
- les runs A121/A122 n'ont créé aucun résumé stocké et n'ont muté aucun état
  serveur ; les seuls fichiers produits sont des artefacts locaux de probe.

---

## 14. Phase 6 — Validation live end-to-end

État : `NON_DEMARREE`

### 14.1 Prérequis

- phases 0 à 5 approuvées ;
- aucune régression déterministe ;
- binaire x64 identifié ;
- backend et LLM sains ;
- microprobes stables ;
- aucun processus concurrent ;
- aucune modification non documentée entre les runs.

### 14.2 Protocole planning

Exécuter au minimum cinq permutations, puis retenir trois réussites
consécutives sous la même configuration.

Pour chaque run, enregistrer :

- question exacte ;
- ordre des formulations ;
- modèle et hash ;
- configuration matérielle ;
- appels LLM ;
- tokens, cache et durée par phase ;
- outils et arguments ;
- candidats observés, audités, acceptés et refusés ;
- couverture par rôle ;
- table finale ;
- vingt EvidenceIds ;
- vingt cartes ;
- inspection humaine cellule par cellule ;
- verdict final.

### 14.3 Critères d'approbation

- trois runs consécutifs approuvés ;
- vingt recettes concrètes et distinctes ;
- cinq petits-déjeuners adaptés ;
- cinq déjeuners adaptés ;
- cinq collations adaptées ;
- cinq soupers adaptés ;
- vingt sources exactes et ouvrables ;
- aucune rubrique, instruction ou fragment ;
- médiane `<= 7 min` ;
- maximum `<= 10 min` ;
- aucune saturation 4K ;
- aucune répétition non productive ;
- aucun fallback présenté comme succès.

### 14.4 Règle d'échec

Deux échecs de même classe arrêtent les full lives. La phase repasse en
`TESTEE_NON_APPROUVEE` et la phase responsable est rouverte.

### 14.5 Journal de phase

À remplir lors de l'exécution.

---

## 15. Phase 7 — Consolidation, dette et commits

État : `NON_DEMARREE`

### 15.1 Objectif

Transformer le chantier validé en historique Git lisible sans perdre une
modification utile ni conserver des expériences mortes.

### 15.2 Actions

- [ ] Refaire l'inventaire Git complet.
- [ ] Classifier les fichiers modifiés, supprimés et non suivis.
- [ ] Identifier les déplacements intentionnels.
- [ ] Identifier les expériences supprimées ou rejetées.
- [ ] Prouver l'inaccessibilité des anciens chemins avant suppression.
- [ ] Décomposer `RagEndpoints.cs` par primitives.
- [ ] Décomposer `SourceBackedAgentV2Tests.cs` par responsabilités.
- [ ] Consolider les 134 fichiers SourceBacked en modules cohérents.
- [ ] Migrer les six anciennes cartes par hash.
- [ ] Tester leur ouverture réelle.
- [ ] Sauvegarder avant purge.
- [ ] Purger transactionnellement les tombstones devenus inutiles.
- [ ] Rejouer la validation après chaque groupe.
- [ ] Créer des commits petits, thématiques et réversibles.
- [ ] Vérifier la comparaison finale avec origin.

### 15.3 Interdictions

- pas de `git add -A` ;
- pas de reset destructif ;
- pas de suppression globale du worktree ;
- pas de purge avant migration ;
- pas de commit mélangeant ingestion, orchestration, UI et artefacts sans
  justification explicite.

### 15.4 Critères d'approbation

- chaque groupe de fichiers expliqué ;
- aucune voie morte conservée « au cas où » ;
- tests verts après nettoyage ;
- diff check propre ;
- commits relisibles et réversibles ;
- dette résiduelle explicitement documentée.

### 15.5 Journal de phase

À remplir lors de l'exécution.

---

## 16. Phase 8 — Validation WinUI et clôture

État : `NON_DEMARREE`

### 16.1 Parcours humain obligatoire

- lancer l'application réelle ;
- exécuter les scénarios critiques depuis l'interface ;
- ouvrir plusieurs cartes sources ;
- vérifier fichier, page et passage ;
- vérifier les accents, tableaux et libellés ;
- vérifier le comportement après clarification ;
- vérifier un suivi conversationnel ;
- vérifier l'absence de source fabriquée ;
- vérifier l'expérience utilisateur et la durée ressentie.

### 16.2 Livrables finaux

- rapport de validation ;
- matrice des scénarios ;
- mesures qualité/latence ;
- inventaire des commits ;
- état Git final ;
- liste des risques résiduels ;
- procédure de reproduction ;
- décision explicite `GOAL COMPLETE` ou `GOAL NON COMPLETE`.

### 16.3 Critères d'approbation

- parcours WinUI réel approuvé ;
- sources cliquables exactes ;
- tous les critères du Goal prouvés ;
- aucune phase rouverte ;
- aucune incertitude critique non documentée.

### 16.4 Journal de phase

À remplir lors de l'exécution.

---

## 17. Registre des expérimentations

Ajouter une ligne avant de lancer chaque expérience.

| ID | Date | Phase | Hypothèse | Variante/configuration | Commande ou test | Artefact | Résultat | Verdict | Motif |
|---|---|---|---|---|---|---|---|---|---|
| EXP-001 | 2026-08-26 | 0 | L'état courant peut être figé sans modifier le produit | Git + backend + Qwen3 local + Debug/x64 | Séquence de baseline phase 0 | `artifacts/goal-rag-end-to-end-20260826-233646/` | Build 0/0, 1 235/1 235 tests, 3 probes vertes, runtime qualifié, corpus cohérent | `APPROUVE` | Tous les critères de phase 0 sont prouvés sans modification produit |
| EXP-002 | 2026-08-26 | 1 | Les probes existantes suffisent à qualifier la baseline A avant retrieval | Quatre probes existantes, binaire x64 approuvé, Qwen3 4B/4K | Routeur + définition + stratégie + choix d'outil, séparément | `artifacts/goal-rag-end-to-end-20260826-233646/phase1/probe-inventory-and-protocol.md` | Routeur 40,995 s; définition 26,670 s; stratégie échouée 46,069 s; tool-choice 150,202 s | `TESTE_NON_APPROUVE` | Les probes sont fragmentées, de configurations différentes et sans latence par appel; le seuil de 30 s est déjà manqué |
| EXP-003 | 2026-08-27 | 1 | Une instrumentation de test unifiée peut isoler le coût et les fuites de la variante A sans changer le produit | `LiveSourceBackedIntakeFirstActionBenchmarkTests`, options runtime par défaut, Qwen3 4B/4K, arrêt avant exécution du premier outil | 5 permutations + non Cuisine + simple + document nommé + ambigu | `artifacts/goal-rag-end-to-end-20260826-233646/phase1/phase1-exp003-baseline-a-analysis.md` | Grilles : moyenne 106,918 s, 2/5 queries contaminées, 5/5 types candidats distincts ; simple/nommé < 18 s ; non-Cuisine/ambigu en fallback | `REJETE` | Aucun grid sous 30 s, instabilité et enrichissements non demandés, généricité et clarification défaillantes |
| EXP-004 | 2026-08-27 | 1 | B, retrait de l'hypothèse seule, pourrait réduire la fuite sans changer l'ordre des appels | Ablation ciblée uniquement, quatre appels conservés | Analyse causale sur les timings EXP-003 et la provenance P2/P3 | `artifacts/goal-rag-end-to-end-20260826-233646/phase1/phase1-exp003-baseline-a-analysis.md` | Le minimum A dépasse 81 s et la contamination provient aussi des rôles | `REJETE_COMME_SOLUTION_PRINCIPALE` | B ne peut pas satisfaire le seuil de 30 s ; conserver seulement comme éventuelle ablation de sécurité |
| EXP-005 | 2026-08-27 | 1 | C peut supprimer les décisions spéculatives en faisant choisir la première observation au routeur | Une action initiale LLM dans l'intake ; revues pré-observation désactivées ; scope mécanique préservé | Build + 26 ciblés + 268 régression + P1 deux fois | `artifacts/goal-rag-end-to-end-20260826-233646/phase1/phase1-exp005-exp006-variant-c-analysis.md` | 4 appels deviennent 1 ; 21,264–43,962 s ; query livrable/axes 2/2 ; type `repas hebdomadaire` 2/2 | `TESTE_NON_APPROUVE` | Gain causal conservé, mais canal query et unité sémantique non sûrs |
| EXP-006 | 2026-08-27 | 1 | Une règle générique plus explicite peut empêcher le modèle d'utiliser les axes comme query | C.1 : prompt et description de champ distinguent contenu documentaire et coordonnées de sortie | Build + 173 ciblés + P1 | `artifacts/goal-rag-end-to-end-20260826-233646/phase1/phase1-exp005-exp006-variant-c-analysis.md` | Même outil, même type et même query ; 43,658 s | `REJETE` | Troisième reproduction de la fuite ; la condition de recul impose un changement de contrat |
| EXP-007 | 2026-08-27 | 1 | Retirer le canal query libre du contrat grille empêche mécaniquement l'exécution des axes sans reprendre la décision sémantique au LLM | D : le LLM choisit seulement découverte par cartes ou navigation, scope et budget ; le type avant observation est provisoire et non exécutable | 174 ciblés + 269 élargis + P1 | `artifacts/goal-rag-end-to-end-20260826-233646/phase1/phase1-exp007-variant-d-analysis.md` | Déterministe vert ; live choisit le contrat direct, `single_item/count=1`, même query ; 30,804 s | `REJETE` | Le contrat concurrent permet de contourner la restriction grille |
| EXP-008 | 2026-08-27 | 1 | Séparer le choix de famille du remplissage empêche le contournement sans coder la route | E : classification LLM compacte, puis seul contrat spécialisé exposé ; deux appels mesurés ensemble | 181 ciblés + 276 élargis + P1 | `artifacts/goal-rag-end-to-end-20260826-233646/phase1/phase1-exp008-variant-e-analysis.md` | Sémantique P1 correcte, mais 32,245 s | `TESTE_NON_APPROUVE` | Dépassement de 2,245 s ; second prompt encore monolithique |
| EXP-009 | 2026-08-27 | 1 | Retirer du second appel les règles des familles inaccessibles réduit le coût sans changer la décision | E.1 : même classifieur et mêmes schémas ; prompt système spécialisé après sélection | 185 ciblés + 280 élargis + P1–P5 + génériques | `artifacts/goal-rag-end-to-end-20260826-233646/phase1/phase1-exp009-variant-e1-analysis.md` | Repas 5/5 sous 30 s sans fuite ; 4/4 génériques incorrects | `REJETE_COMME_ROUTEUR_UNIVERSEL` | Le classifieur quatre voies dégrade simple, nommé, ambigu et non-Cuisine |
| EXP-010 | 2026-08-27 | 1 | Un fast-path grille ancré conserve le 5/5 sans remplacer les familles que le classifieur maîtrise mal | F : grille explicite ou délégation au routeur général ; ancrage mécanique des axes ; collision scope/document neutralisée ; pool grille borné | 186 ciblés + 281 élargis + P1 | `artifacts/goal-rag-end-to-end-20260826-233646/phase1/phase1-exp010-variant-f-analysis.md` | P1 délégué au routeur général ; query contaminée ; 33,086 s | `REJETE` | Le classifieur n'identifie pas la grille malgré ses arguments détaillés |
| EXP-011 | 2026-08-27 | 1 | Le choix binaire sans arguments évite la fausse délégation, tandis que le second étage suffit à prouver les axes | F.1 : classifier `grid`/`defer` vide ; contrat grille sans query ; axes finaux ancrés ; fallback général sur rejet | 185 ciblés + 280 élargis + P1 | `artifacts/goal-rag-end-to-end-20260826-233646/phase1/phase1-exp011-variant-f1-analysis.md` | P1 encore délégué ; query contaminée ; 15,977 s | `REJETE` | Le label binaire `defer` biaise le choix de famille et réouvre le contrat général |
| EXP-012 | 2026-08-27 | 1 | Les labels sémantiques quatre voies évitent le biais du bouton générique defer, sans imposer leur erreur aux chemins non-grid | G : classifieur quatre voies sans arguments ; seul grid ouvre le contrat spécialisé ; autres choix repassent au routeur général | 187 ciblés + 282 élargis + P1–P5 + génériques | `artifacts/goal-rag-end-to-end-20260826-233646/phase1/phase1-exp012-variant-g-p1.json` et matrices associées | Repas 5/5, cartes/no-query/scope, 22,947–27,588 s ; simple et nommé corrects ; ambigu clarifié ; non-Cuisine bloqué par un veto aval | `TESTE_NON_APPROUVE` | Meilleure architecture, sous réserve d'EXP-013 et de l'utilité de l'observation réelle |
| EXP-013 | 2026-08-27 | 1 | Le runner doit accepter un type documentaire légitime qui partage un terme avec une colonne, puisque ce type reste provisoire et non exécutable | Suppression du veto `atomic type contains layout coordinate` dans le parseur de mission ; contrats structurels conservés | Régression rouge/verte + 188 ciblés + 283 élargis + non-Cuisine | `artifacts/goal-rag-end-to-end-20260826-233646/phase1/phase1-exp013-overlap-red.trx`, `phase1-exp013-overlap-targeted.trx`, `phase1-exp013-overlap-regression.trx` et `phase1-exp013-non-cuisine.json` | Non-Cuisine : cartes Maintenance, sans query, scope conservé, 18,938 s | `APPROUVE_CORRECTION_CIBLEE` | Le veto lexical retirait au LLM une décision sémantique légitime ; les contrôles structurels restent actifs |
| EXP-014 | 2026-08-27 | 1 | Les vingt premières cartes canoniques Cuisine obtenues par l'action exacte de G constituent une première observation citable et utile | Endpoint réel `documents.content_cards`, Cuisine, aucune query/mode, limit 20, offset 0 ; normalisation `EvidenceBundle` | `LiveContentCardInventoryProbeTests.Live_cuisine_content_card_inventory_is_paged_citable_and_materialized` | `artifacts/live-content-card-inventory-20260827-015849/report.txt` et `phase1/phase1-exp014-content-cards-exact.trx` | 20 preuves citables, 10 documents, mais cartes d'ouverture dominées par sommaires/préfaces/intros/crédits | `REJETE_SEMANTIQUEMENT` | Le vert mécanique ne fournit pas une première observation utile pour composer vingt repas |
| EXP-015 | 2026-08-27 | 1 | Un inventaire représentatif sans query expose des candidats intérieurs plus utiles que l'ordre source, sans décision métier déterministe | Même endpoint, scope et budget qu'EXP-014 ; seul `inventoryMode=representative` varie | Même probe live avec `SAAIA_CONTENT_CARD_INVENTORY_MODE=representative` | `artifacts/live-content-card-inventory-20260827-020042/report.txt` et `phase1/phase1-exp015-content-cards-representative.trx` | 20 preuves citables, 10 documents, 20 pages internes ; nombreux objets culinaires nommés, bruit résiduel visible | `APPROUVE_COMME_MODE_D_OBSERVATION` | Gain d'utilité net sans query ni préclassification métier ; l'audit LLM reste nécessaire après observation |
| EXP-016 | 2026-08-27 | 1 | G.1 peut rendre la première observation réellement utile sans nouvel appel ni nouveau choix demandé au modèle | Pour une grille dont l'action LLM est `cards`, sérialiser mécaniquement `inventoryMode=representative`; navigation inchangée | Régression rouge/verte du parseur, tests ciblés/élargis, puis P1 live et observation exacte | `phase1/phase1-exp016-representative-*.trx`, `phase1-exp016-variant-g1-p1.json` et `artifacts/live-content-card-inventory-20260827-020709/report.txt` | Rouge exact ; 189/189 ciblés ; 284/284 élargis ; P1 22,792 s ; 10 preuves utiles sur 10 documents | `APPROUVE` | Échantillonnage utile sans query, règle métier, champ LLM ni appel supplémentaire |
| EXP-017 | 2026-08-27 | 1 | Un executor test-only peut mesurer en un seul run la latence jusqu'au retour de la première observation exacte sans poursuivre le pipeline | Mode opt-in du benchmark : exécuter uniquement `documents.content_cards`, normaliser en `EvidenceBundle`, enregistrer timings/counts, puis stopper | Build + smoke désactivé + P1 live intégré G.1 | `phase1/phase1-exp017-variant-g1-integrated-p1.json` et `.trx` | Frontière 20,779 s ; backend 179 ms ; 20 preuves/10 documents en 21,010 s depuis la question | `APPROUVE_INSTRUMENTATION` | Ferme le gap de mesure sans altérer le produit ni engager les audits aval |
| EXP-018 | 2026-08-27 | 1 | La stabilité cinq permutations et la généricité non-Cuisine de G survivent à G.1, avec le mode représentatif présent dans chaque action cartes | Binaire G.1 figé ; benchmark frontière uniquement ; scénarios P2–P5 et non-Cuisine | Matrice live unifiée, sans changement entre scénarios | `phase1/phase1-exp018-variant-g1-closing-matrix.json` et `.trx` | P2–P5 22,803–27,922 s ; Maintenance 24,639 s ; tous cartes/representative/sans query/scope exact | `APPROUVE` | Matrice de clôture stable ; les branches simple/nommé/ambigu restent inchangées et couvertes par EXP-012 |
| EXP-019 | 2026-08-27 | 2 | Un benchmark isolé peut mesurer fidèlement la baseline A identité + compatibilité sans lancer le reste du pipeline | Lot P1 representative/20 figé ; vrai audit batch=6 ; quatre rôles de la mission G.1 ; aucun retrieval/writer | Build + smoke désactivé + live A + inspection humaine | `phase2/exp019-baseline-a.json`, `phase2/exp019-baseline-a-human-review.md` et TRX associés | 20/20 IDs identiques ; 4 identité + 8 compatibilité ; 7 acceptés ; 0 réparation ; 55,378 s ; 2 faux positifs et compatibilités trop parcellaires | `TESTE_NON_APPROUVE` | Baseline fidèle et protocole valide, mais A est refusée comme comportement produit ; B simple n'économiserait rien sur ce lot |
| EXP-020 | 2026-08-27 | 2 | Le contrat LLM existant de frontières de colonnes, déplacé après observation, peut améliorer la compatibilité sans règle métier | Même lot/ordre/modèle que A ; une revue `submit_column_semantics` avant le même audit ; aucun candidat dans la revue ; aucun appel supprimé | Build + smoke A/B0 + live B.0 + inspection différentielle | `phase2/exp020-baseline-b0.json`, `phase2/exp020-baseline-b0-human-review.md` et TRX | 13 appels ; +39,831 s/+71,9 % ; 7 acceptés inchangés ; frontières alimentaires ajoutées ; compatibilité mixte | `REJETE` | Répète BUG-006, coût élevé et qualité insuffisante ; B.1 interdit |
| EXP-021 | 2026-08-27 | 2 | Le contrat LLM de définition atomique exécuté après observation peut remplacer `repas`/règle vide et corriger les faux positifs d'identité | Même A ; rôles label=label ; un appel `source_backed_atomic_candidate_definition_v7` avant audit ; aucun candidat dans cet appel | Build + smoke + live C.0 + inspection différentielle identité d'abord | `phase2/exp021-baseline-c0.json`, `phase2/exp021-baseline-c0-human-review.md` et TRX | Type `recette de cuisine` ; 9 appels ; 56,136 s ; axe rejeté ; 5 préparations conservées ; `Pâte levée...` encore acceptée | `REJETE` | Échec du critère 4 ; brique de définition causalement utile autorisée seulement dans l'ablation suivante |
| EXP-022 | 2026-08-27 | 2 | Restituer demande/type/règle LLM au juge one-role/batch-6 peut corriger la compatibilité sans frontières métier ni appel de plus | C.0 + contexte LLM injecté test-only ; définition C.0 gelée ; rotations candidats 0/7/13 | Build + smoke + quatre lives dont trois causaux + inspection par ID | `phase2/exp022-*.json`, `phase2/exp022-d0-permutation-analysis.md` et TRX | Rotation 0 passe ; 6/7/5 acceptés ; Jaccard couples 0,40–0,60 ; faux positif et faux négatif dépendants de l'ordre | `REJETE` | Le contexte améliore la qualité moyenne mais le contrat listwise reste instable |
| EXP-023 | 2026-08-27 | 2 | Un candidat par appel avec rôles exacts nommés peut supprimer le biais listwise sans index positionnel | Six approbations C.0 gelées ; type/règle C.0 ; rotations de rôles 0/1/2 | Build + smoke + trois lives + inspection différentielle | `phase2/exp023-e0-role-rotation*.json`, `phase2/exp023-e0-role-order-analysis.md` et TRX | 18/18 protocoles ; 15/7/8 couples ; décisions fortement dépendantes de l'ordre des rôles | `REJETE` | Les labels nommés n'éliminent pas le biais multi-rôle du 4B |
| EXP-024 | 2026-08-27 | 2 | Un couple candidat×rôle booléen peut établir le plafond de qualité/stabilité sans compétition positionnelle | Six candidats C.0 × quatre rôles ; rotations 0/7/13 | Build + smoke + trois matrices live + inspection | `phase2/exp024-f0-pair-rotation*.json`, `phase2/exp024-f0-pairwise-analysis.md` et TRX | 72/72 identiques ; 24 appels en 30–31 s ; 18 couples, plusieurs trop permissifs | `REJETE` | Stabilité prouvée, qualité labels seuls insuffisante |
| EXP-025 | 2026-08-27 | 2 | Les frontières B.0 gelées peuvent rendre la référence pairwise F.0 discriminante sans réintroduire de position | Candidats/type C.0 ; définitions B.0 ; 24 booléens ; rotations 0/7/13 | Smoke + trois matrices + inspection | `phase2/exp025-g0-pair-rotation*.json`, `phase2/exp025-g0-analysis.md` et TRX | 72/72 identiques ; 10 couples cohérents ; 40,7–41,6 s pairwise ; ~78 s avec rôles | `APPROUVE_COMME_REFERENCE` | Qualité/stabilité compatibilité acquises ; coût, génération runtime, identité et multi-page non approuvés |
| EXP-026 | 2026-08-27 | 2 | Les frontières B.0 peuvent être régénérées de façon équivalente avec les mêmes entrées | Appel rôle uniquement ; B.0 + deux répétitions dans le même processus | Smoke + deux lives + comparaison texte/hash/sortie brute, puis cold-start observé par M.0 | `phase2/exp026-h0-role-repeat*.json`, `.trx`, analyse et run M.0 | Trois sorties intra-processus identiques ; après redémarrage M.0 produit un autre hash valide | `APPROUVE_INTRA_PROCESSUS_SEULEMENT` | Reproductibilité cold-start réfutée ; ne pas utiliser le hash comme contrat durable |
| EXP-027 | 2026-08-27 | 2 | L'identité mono-candidat peut supprimer le biais listwise avec le contrat produit existant | 20 candidats, type/règle C.0, batch=1, zéro rôle ; rotations conditionnelles | Smoke + live initial + inspection | `phase2/exp027-i0-identity-rotation00.json`, `.trx` et analyse | 20 appels ; 69,376 s ; 10 acceptés dont trois faux positifs ; axe corrigé, E19 persiste | `REJETE` | Arrêt avant rotations ; le titre direct est confondu avec l'objet sémantique |
| EXP-028 | 2026-08-27 | 2 | La hiérarchie source existante peut résoudre un fragment vers son parent autonome avant le juge I.0 | Même lot/type/règle ; sourceAnchor non pré-résolu test-only ; batch=1 ; zéro rôle | Smoke + run initial + inspection | `phase2/exp028-j0-hierarchy-rotation00.json`, `.trx` et analyse | 33 appels ; 142,014 s ; une promotion utile ; trois faux positifs inchangés | `REJETE` | Mécanisme pertinent mais insuffisant avec le runtime actuel |
| EXP-029 | 2026-08-27 | 2 | Q8_0 du même Qwen3-4B peut franchir le seuil d'identité raté en Q5_K_M | I.0 exact ; seul GGUF change ; serveur temporaire et restauration Q5 | Hash/props/runtime + smoke + live initial + inspection | `phase2/exp029-k0-q8-identity-rotation00.json`, logs, `.trx` et analyse | Décisions identiques à I.0 ; +112,5 % ; Q5 restauré | `REJETE` | Quantification disculpée ; Q8 non sélectionné |
| EXP-030 | 2026-08-27 | 2 | Un Qwen3-8B peut franchir le plafond sémantique du 4B | I.0 exact ; Qwen3-8B Q4_K_M ; reasoning off ; serveur temporaire ; template ChatML après incompatibilité Qwen/grammar prouvée | Hash/props/runtime + smoke + sonde structurée + live initial + inspection + restauration Q5 | `phase2/exp030-l0-8b-chatml-identity-rotation00.json`, logs, TRX et analyse | 20/20, 0 réparation, 0 accepté ; cinq recettes claires perdues ; 180,846 s (+160,7 %) ; Q5 restauré | `REJETE` | Le rejet universel échoue au rappel ; rotations annulées et série dense locale fermée |
| EXP-031 | 2026-08-27 | 2 | La compatibilité pairwise G.0 peut remplacer le gate d'identité comme décision opératoire sans perdre les preuves utiles | 20 preuves ; frontières réellement régénérées ; 80 couples indépendants ; zéro gate d'identité ; matching non exécuté | Protocole, smoke, run initial, inspection ; rotations conditionnelles annulées | `phase2/exp031-m0-assignment-rotation00.json`, TRX, protocole et analyse | 80/80 valides ; 31 couples ; 5/5 contrôles préservés, mais 6 faux positifs ; hash de rôles cold-start divergent ; 210,168 s projetées | `REJETE` | Compatibilité seule non sûre comme gate ; aucune rotation ni retouche de prompt |
| EXP-032 | 2026-08-27 | 2 | Le profil backoffice actuel peut créer des cartes LLM utiles depuis la baseline active riche | Service produit exact ; 25 cartes v5, 12 sections, 8 extraits ; Q5/4096 ; mémoire seulement | Audit read-only + protocole + export hashé + smokes + live + inspection transport | `phase2/exp032-n0-live-backoffice-profile.json`, TRX, audit et analyse | HTTP 400 ; 5 094 tokens de prompt > 4 096 ; 0 profil et 0 carte | `REJETE_TECHNIQUEMENT` | Contrat monolithique non exécutable ; aucun second document ni retouche locale |
| EXP-033 | 2026-08-27 | 2 | Des décisions LLM par fenêtres source peuvent produire des unités autonomes grounded sous 4096 | Huit chunks représentatifs identifiés/pages ; jusqu'à quatre items ; température 0 ; 450 tokens max | Protocole + export + smoke + live + inspection exhaustive | `phase2/exp033-n1-windowed-source-card-extraction.json`, TRX et analyse | 8/8 HTTP ; prompts 571–715 ; 1/8 JSON ; sous-étapes ; 581,190 s | `REJETE` | Fenêtrage contexte retenu, contrat multi-item direct fermé |
| EXP-034 | 2026-08-27 | 2 | Une identité primaire LLM 0..1 par fenêtre peut préserver 7/7 unités sans sous-décomposition | Entrée N.1 exacte ; schéma borné maxItems=1 ; température 0 ; 180 tokens max | Protocole + smoke + live + inspection | `phase2/exp034-n2-primary-window-identity.json`, TRX et analyse | 8/8 JSON/stop ; 7/7 titres ; 6/8 evidence ; identité OCR discutable ; 172,959 s | `REJETE_MAIS_BRIQUE_PROMETTEUSE` | Cardinalité/fenêtrage retenus, génération libre de preuve fermée |
| EXP-035 | 2026-08-27 | 2 | Le LLM peut choisir une ancre source existante sans générer titre/evidence | Même huit chunks ; options heading/section/lignes hashées ; 0..1 ID ; 50 tokens | Protocole + export + smoke + live + inspection des préfixes | `phase2/exp035-n3-source-anchor-selection-canonical.json`, TRX et analyse | 0/8 JSON, 166,339 s ; préfixes = 7 racines + 1 instruction OCR | `REJETE_TECHNIQUEMENT_ET_SEMANTIQUEMENT` | Pas de permutations ; hashes complets et lignes brutes fermés |
| EXP-036 | 2026-08-27 | 2 | Des clés compactes sur seules ancres structurelles peuvent donner 7 racines sans faux OCR | Entrée N.3 ; heading/section uniquement ; fenêtre sans ancre sans appel ; 50 tokens | Smoke + canonique + inverse + hash + inspection exacte | `phase2/exp036-n4-structural-anchor-*.json`, TRX, protocole et analyse | 21/21 sélections identiques ; 7/7 racines par ordre ; contrôle sans appel ; 40,325–52,121 s/run | `APPROUVE_COMME_BRIQUE_CUISINE` | Généralisation non-Cuisine obligatoire avant produit |
| EXP-037 | 2026-08-27 | 2 | Le contrat N.4 inchangé se généralise à un PDF NIST complexe | NIST SP 800-37r2 ; neuf fenêtres ; oracle humain figé avant appel ; exactement une clé si structure | Export + oracle figé + smoke + unique canonique ; permutations conditionnelles annulées | `phase2/exp037-o0-*`, harness mutualisé et analyse | 8/8 protocoles, 46,063 s, mais 5/9 sémantique ; faux niveau 0 + trois pseudo-headings forcés | `REJETE_COMME_CONTRAT_MULTI_DOMAINE` | `minItems=1` et priorité highest-level fermés ; aucune permutation |
| EXP-038 | 2026-08-27 | 2 | Une sortie 0..1 et une consigne neutre en profondeur corrigent causalement O.0 sans règle code | Même NIST/oracle ; prompt exact ; RFC figé avant premier appel | Protocole + RFC/oracle + smoke + unique NIST canonique | `phase2/exp038-o1-*`, harness mutualisé et analyse | 8/8 contrats, 40,777 s, abstentions 3/3 corrigées, mais faux niveau 0 inchangé ; 8/9 | `REJETE` | Portes aval annulées ; cardinalité 0..1 conservée comme capacité |
| EXP-039 | 2026-08-27 | 2 | Un jugement booléen indépendant par ancre supprime le biais listwise sans perdre le rappel | Même NIST ; vérité 9 ancres ; prompt exact ; canary finance aveugle figé avant code | Protocole + vérités + export canary + harness + smoke + unique canonique | `phase2/exp039-p0-*`, nouveau harness et analyse | 9/9 contrats, 33,203 s ; faux niveau rejeté mais quatre vrais titres rejetés ; 5/9 | `REJETE` | Série identité offline fermée ; aucune porte aval exécutée |
| EXP-040 | 2026-08-27 | 2 | Le chemin actuel peut fournir une baseline verticale réelle Q019 et révéler précisément les pertes EvidenceBundle→LLM→writer→UI sans gate offline | Banque Cuisine hashée ; Q019 seule ; Q5/4096 ; serveur externe ; un run ; aucun code produit | JSON/JSONL/TSV + TRX + réponse/sources/traces + appels/tokens/durée + inspection humaine | `phase2/exp040-q0-*`, output JSON/JSONL/TSV, TRX et analyse | 1/1 harnais en 52 s mais 0 outil/source ; clarification ingrédients OU réglage ; 3 220 tokens ; 51,724 s | `REJETE_AVANT_RETRIEVAL` | Phase 1 rouverte ; ne pas relancer Q.0 ; fixer le contrat cumulatif sous EXP-041 |
| EXP-041 | 2026-08-27 | 1 rouverte | Une règle générique « facettes demandées ensemble = livrable cumulatif » corrige Q019 sans supprimer les vraies clarifications | Une phrase temporaire dans classifier, commun spécialisé et fallback ; Q019 + canary Normes + contrôle ambigu ; aucun override code | TDD rouge/vert + budgets prompt + harness première frontière ; arrêt après premier run rouge | `phase1/exp041-r0-*`, cinq TRX/JSON et analyse | TDD/budgets verts ; live : 0/2 documentaires à l'outil, ambigu après grille inventée ; 12 588 tokens, 155,965 s | `REJETE` | Aucun second run ; règle produit et test de présence rollbackés ; recul architectural obligatoire |
| EXP-042 | 2026-08-27 | 1 rouverte | Un registre d'intake à outil unique séparant corpus, layout et actionnabilité est plus stable que quatre fonctions concurrentes | Shadow test-only ; 5 champs non exécutables ; 8 scénarios ; ordres aval conditionnels | Schéma/spans exacts + exactitude + stabilité + tokens/latence ; zéro produit | `phase1/exp042-s0-*`, harness, JSON et TRX | Canonique : 1/8 sémantique, 7/8 protocole ; médiane 14,391 s ; Q019 surclarifiée ; knowledgeMode 8/8 | `REJETE` | Inverse/rotation annulés ; aucun produit ; seule isolation mono-dimensionnelle autorisée |
| EXP-043 | 2026-08-27 | 1 rouverte | Le seul signal 8/8 de S.0, corpus versus opérationnel, reste exact et devient rapide lorsqu'il est isolé | Un outil/enum shadow ; mêmes 8 questions ; ordres aval conditionnels | 24/24 + protocole + stabilité + sortie ≤32 tokens + médiane ≤5 s/max ≤8 s | `phase1/exp043-s1-*`, harness, JSON et TRX | Canonique 7/8 : inventaire classé corpus ; protocole 8/8 ; médiane 3,391 s, max 5,231 s | `REJETE` | Inverse/rotation annulés ; pré-classifieurs supplémentaires 4B fermés |
| EXP-044 | 2026-08-27 | 1 rouverte | Une clarification qui déclare elle-même `resumeRoute=source_backed` doit obtenir une observation avant de devenir terminale | Invariant d'ordre temporaire ; unique repair source ; cinq canaries | TDD rouge/vert + 45/45 + live canonique ; second conditionnel annulé | `phase1/exp044-t0-*`, JSON/TRX et analyse | Q019 corrigée mais 57,6 s ; Normes scope faux ; ambigu non-grid ignoré/inventé ; 19 276 tokens | `REJETE` | Produit/tests comportementaux rollbackés ; budgets 40/40 ; observation-first par resumeRoute fermée |
| EXP-045 | 2026-08-27 | 1 rouverte | Le 8B local améliore le routeur produit exact sans retouche et dans le budget machine | Serveur temporaire 8B Q4, deux templates techniques bornés, matrice conditionnelle, Q5 restauré | Smoke tool-call + 8/8 ×2 conditionnel + ≤30 s + hashes/logs/restauration | `phase1/exp045-u0-*`, deux dossiers serveur et restauration | Natif : 32 tokens de reasoning, aucun tool call ; ChatML off/none : `<think>` en contenu, aucun tool call ; matrice métier non exécutée ; Q5 PID 18944 restauré, santé/modèle/props exacts | `INCONCLUANT_TECHNIQUE` | Aucun troisième template/budget/prompt ; substitution directe de ce 8B fermée ; ne tirer aucun verdict sémantique |
| EXP-046 | 2026-08-27 | 1 rouverte | Le Q5 choisit mieux et plus vite une capacité réelle qu'une famille puis une méta-route retraduite | Shadow test-only ; six capacités réelles/miroir ; un appel ; huit cas ; zéro backend/mission/produit | 8/8 canonique puis inverse/rotation conditionnels ; exactitude args ; ≤30 s ; tokens/latence | `phase1/exp046-v0-*`, JSON, 3 TRX et harness | Canonique brut 4/8 sémantique, 8/8 transport, médiane 5,479 s/max 23,995 s ; revue stricte 3/8 sémantique et 6/8 protocole ; inventaire/Q019/PDF passent ; Normes/ambigu/simple/P1 échouent | `REJETE` | Inverse/rotation annulés ; aucun produit ; V.0-bis prompt interdite ; harness durci sans relancer le live |
| EXP-047 | 2026-08-27 | 1 rouverte | Le Q5 récupère les cinq premières actions rouges depuis une erreur mécanique ou une observation réelle, sans feedback sémantique | Shadow test-only ; replay exact V.0 ; cinq scénarios ; mêmes six outils/prompt ; ≤2 décisions nouvelles ; backend read-only borné | 5/5 + schémas/anchors/scopes + ≤30 s/appel + zéro mutation/oracle ; réponses/logs/hashes | `phase1/exp047-w0-*`, harness, JSON et deux TRX | 1/5 ; 1/7 décisions protocole, 5/7 latence ; non-grid corrigé au tour 2 ; ambigu échoue/tronque/invente ; trois observations refusées à 4 871–5 173 tokens pour ctx 4 096 | `REJETE` | Trois cas non évalués sémantiquement ; aucun W.0-bis/réduction feedback ; zéro produit/mutation ; orchestrateur plus capable autorisé |
| EXP-048 | 2026-08-27 | 1 rouverte | Qwen3.5-9B Q4_K_M peut transporter puis sélectionner directement les six capacités plus fiablement sur cette machine | Modèle/révision/hash gelés ; runtime b10098 ; profil Jinja non-thinking unique ; smoke tool-call puis V.0 strict conditionnel ; Q5 restauré | Transport exact ≤30 s puis 24/24 trois ordres, stabilité, ≤30 s/cas, logs/props/hashes/restauration | `phase1/exp048-x0-qwen35-9b-direct-router-protocol.md`, état, JSON/TRX, logs, analyse et restauration | Transport 1/1 en 17,836 s ; canonique 2/8 sémantique, 6/8 protocole, 6/8 latence ; médiane 25,519 s, max 45,010 s ; Q5 PID 18760 restauré | `REJETE` | Fermer ce profil, les permutations et les retouches X.0 ; comparer les enveloppes restantes avant tout nouveau modèle ou produit |
| EXP-049 | 2026-08-27 | 1 rouverte | Granite 4.2-3B, spécialisé agentique et entièrement offloadable, peut battre la frontière directe malgré moins de paramètres | GGUF IBM officiel/révision/hash gelés ; runtime produit inchangé ; profil low-effort unique ; température/top-p officiels ; transport puis V.0 strict ; restauration Q5 | Transport exact ≤30 s puis 24/24 trois ordres, stabilité exacte et ≤30 s/cas | comparaison/protocole, état, smoke, JSON/TRX, logs, analyse et restauration sous `phase1/exp049-*` | Smoke exact 5,102 s ; canonique 4/8 sémantique, 5/8 protocole, 7/8 latence ; médiane 20,861 s, max 30,493 s ; Q5 PID 4216 restauré | `REJETE` | Annuler permutations ; fermer substitutions compactes ; demander choix matériel/distant/fine-tuning |
| EXP-050 | 2026-08-27 | 1 rouverte | La prochaine enveloppe peut être choisie sur un dossier quantifié et une référence distante préparée sans divulguer les preuves SAAIA ni engager une exécution | Huit scénarios V.0 exacts, six outils, oracles séparés ; machine P520/4 GB ; Q5 restauré ; métadonnées officielles 30B/LoRA/QLoRA | Export et validation offline ; deux rouges de confidentialité/structure puis vert ; comparaison distant/matériel/fine-tuning ; zéro réseau modèle/backend | `phase1/exp050-z0-remote-reference-*`, `exp050-z0-envelope-options-quantified.md`, quatre TRX et exporter test-only | Paquet 114707 octets/8 requêtes, aucun chemin/URL/secret/oracle ; test final 1/1 ; matériel discriminant estimé 24 GB min/32 GB prudent ; corpus LoRA proposé 1440/240/480 | `PREPARE_NON_AUTORISE` | Référence distante bornée recommandée comme diagnostic ; exécution suspendue à l'autorisation explicite ; achat, téléchargement 30B et entraînement interdits |
| EXP-051 | 2026-08-27 | 1 rouverte | GPT-5.6 Sol peut servir de plafond de capacité distant avec un coût et un périmètre assez bornés pour une autorisation explicite | Paquet EXP-050 inchangé ; modèle/fonctions/prix et rétention sourcés officiellement ; aucune API appelée ; Q5 local maintenu | Fiche et manifeste `NOT_AUTHORIZED` ; Chat Completions ; reasoning none ; 8 appels/30 s/0 retry ; stop premier rouge ; global ou Europe MAM/ZDR exclusifs | `phase1/exp051-aa0-openai-gpt56-sol-authorization-sheet.md` et manifeste JSON | Coût calculé 0,115092 USD, cap proposé 0,20 ; 4 identifiants listés ; clé présente mais non lue/testée ; endpoint non sélectionné ; 16 appels interdits | `PROPOSE_NON_AUTORISE` | Attendre une autorisation textuelle complète global+30 j ou Europe+preuve MAM/ZDR ; aucun runner/appel avant ce choix |
| EXP-000 | 2026-08-07 | Historique | Le pipeline final peut atteindre le writer sous 12 min | Qwen3 4B, 4K, dernier binaire | Full live planning | `artifacts/client-live-final-weekly-meal-plan-20260807-130111/progress.log` | Timeout pendant compatibilité | `REJETE` | 100 audits, aucune réponse |

Valeurs autorisées pour `Verdict` :

- `APPROUVE` ;
- `REJETE` ;
- `INCONCLUANT` ;
- `TESTE_NON_APPROUVE` ;
- `BLOQUE`.

Un résultat `REJETE` ne doit jamais être supprimé du tableau. Il sert à éviter
de répéter une mauvaise piste.

---

## 18. Registre des décisions

| ID | Date | Phase | Décision | Preuves | Conséquence | Réversible ? |
|---|---|---|---|---|---|---|
| DEC-001 | 2026-08-26 | Global | Ne pas relancer de full live avant stabilisation des microprobes amont | Dernier timeout et fuite d'hypothèse | Phase 1 prioritaire | Oui |
| DEC-002 | 2026-08-26 | Global | Préserver le LLM comme décideur sémantique | ADR source-backed | Code limité au mécanique | Non sans nouvel ADR |
| DEC-003 | 2026-08-26 | Global | Une réussite mécanique peut rester non approuvée | Faux verts historiques | Inspection humaine obligatoire | Non |
| DEC-004 | 2026-08-26 | Global | Ne pas nettoyer le worktree avant classification | 469 entrées Git | Consolidation en phase 7 | Oui |
| DEC-005 | 2026-08-26 | 0 | Remplacer la baseline historique de cartes par 890 cartes v5, sans déclarer une perte d'index | 3 882 chunks SQL = 3 882 points Qdrant; dix profils actifs `deterministic_canonical_v5` | Les comparaisons futures utilisent les révisions et profils actifs | Oui, si une nouvelle reprobe prouve une autre baseline |
| DEC-006 | 2026-08-26 | 0 | Approuver la phase 0 et ouvrir la phase 1 | `phase0-evidence-summary.md` et EXP-001 | Les modifications produit sont désormais autorisées uniquement dans le périmètre de la phase 1 | Oui, si une preuve de baseline est invalidée |
| DEC-007 | 2026-08-27 | 1 | Ne pas utiliser la stratégie candidate combinée comme base de correction | Échec `candidate_strategy_scopes_invalid`, type `Planning de repas semanal` et query du livrable | Conserver l'option désactivée ; ne pas la réactiver sans nouvelle architecture | Oui, après preuve comparative |
| DEC-008 | 2026-08-27 | 1 | Instrumenter un chemin unifié avant de changer le comportement | EXP-002 ne relie pas les étapes et le routeur seul dépasse 30 s | EXP-003 devient la prochaine action | Non pour cette phase |
| DEC-009 | 2026-08-27 | 1 | Ne pas corriger le produit après le seul run P1 ; terminer la matrice A avec le même harness | P1 prouve la latence et plusieurs dérives mais pas la stabilité inter-permutations ni la généricité | Le harness reste test-only ; P2–P5 puis cas génériques sont obligatoires | Non jusqu'au verdict A |
| DEC-010 | 2026-08-27 | 1 | Rejeter A et ne pas retenir B comme architecture principale ; concevoir C autour d'une première observation décidée à l'intake | EXP-003 : 81–116 s et quatre appels pour les grids ; simple/nommé atteignent l'outil en 13–18 s via le routeur seul | Déplacer les revues après une observation réelle, sans retirer au LLM la décision sémantique | Oui seulement si C échoue aux invariants |
| DEC-011 | 2026-08-27 | 1 | Conserver de C l'action initiale LLM et la suppression des trois appels spéculatifs, mais ne pas approuver son contrat query | EXP-005 : 1 appel et un run sous 30 s, mais query contaminée 2/2 et type instable/inadapté | Séparer le gain d'ordre d'appel du défaut de contrat | Oui après preuve contraire |
| DEC-012 | 2026-08-27 | 1 | Après l'échec identique de C.1, modifier le format plutôt que le prompt | EXP-006 reproduit exactement C malgré une consigne générique explicite | EXP-007 retire la query libre du contrat grille ; aucune logique Cuisine n'est ajoutée | Oui si le nouveau contrat dégrade les cas génériques |
| DEC-013 | 2026-08-27 | 1 | Rejeter le contrat grille restreint présenté avec les contrats concurrents | EXP-007 : le modèle choisit le contrat direct pour conserver `search` | Séparer classification LLM et remplissage spécialisé ; ne pas ajouter de détection lexicale en code | Oui si un contrat universel mesuré s'avère meilleur |
| DEC-014 | 2026-08-27 | 1 | Retenir G : quatre labels LLM, fast-path uniquement pour grid, routeur général pour les autres labels | EXP-012 : repas 5/5 sous 30 s, simple et nommé corrects, ambigu clarifié | G devient la base ; le cas non-Cuisine et l'observation réelle restent à fermer | Oui si une régression live générique apparaît |
| DEC-015 | 2026-08-27 | 1 | Supprimer le veto lexical qui interdit un type documentaire partageant un terme avec une colonne | EXP-013 rouge/vert et Maintenance live | Le type provisoire du LLM est accepté ; les contrôles structurels restent seuls exécutoires | Oui seulement avec une preuve d'ambiguïté structurelle non couverte |
| DEC-016 | 2026-08-27 | 1 | Utiliser `representative` comme échantillonnage mécanique des cartes d'une grille sans query | EXP-014 ordered refusé ; EXP-015 representative utile ; endpoint sans préclassification | G.1 conserve le jugement sémantique au LLM et améliore la première observation | Oui si un corpus prouve une dégradation mesurée |
| DEC-017 | 2026-08-27 | 1 | Approuver la phase 1 et ouvrir la phase 2 sur la baseline G.1 | Analyse consolidée, 189/189, 284/284, P1 intégré 21,010 s, matrice 5/5 + Maintenance | Toute suite doit auditer les candidats réellement observés ; aucune régression de G.1 admise | Oui uniquement si une preuve de phase 1 est invalidée |
| DEC-018 | 2026-08-27 | 2 | Refuser A comme comportement produit et ne pas implémenter B déficitaire seule avant d'avoir qualifié les définitions de rôle runtime | EXP-019 : 2 faux positifs d'identité, 3 préparations acceptées sans rôle, chaque rôle à 1/5 ; B économiserait 0 appel sur ce lot | Auditer le chemin runtime de création/version des rôles, puis enregistrer B.0 avant modification produit | Oui si une nouvelle mesure comparable invalide le diagnostic |
| DEC-019 | 2026-08-27 | 2 | Rejeter B.0 et arrêter les retouches locales du prompt de rôles ; qualifier d'abord le type atomique post-observation | EXP-020 : +71,9 %, aliments/compositions ajoutés, compatibilité mixte et faux positifs conservés | Prochaine ablation sur `ReviewSemanticCandidateDefinitionAsync`, rôles label=label, aucune optimisation de cache | Oui si une variante de modèle/contrat prouve des frontières stables sans ajout absent |
| DEC-020 | 2026-08-27 | 2 | Refuser C.0 comme solution complète mais conserver sa définition atomique dans l'ablation suivante | EXP-021 : précision 5/6, axe rejeté, faux positif pâte confirmé, coût +1,4 % seulement | Tester la restitution du contexte LLM au juge de compatibilité, sans changer son découpage one-role/batch-6 | Oui si le passage E19 est requalifié par une preuve source contraire |
| DEC-021 | 2026-08-27 | 2 | Rejeter D.0 et supprimer la comparaison listwise entre candidats dans la prochaine sonde | EXP-022 : protocoles verts mais identités et couples variant selon rotations ; Jaccard 0,40–0,60 | Tester un EvidenceId par appel avec labels de rôles nommés ; aucune retouche du prompt listwise | Oui si une matrice répétée prouve ensuite une stabilité absente ici |
| DEC-022 | 2026-08-27 | 2 | Rejeter E.0 et mesurer une référence pairwise avant tout nouveau design | EXP-023 : 18/18 JSON valides mais 15/7/8 couples selon ordre des rôles | F.0 doit séparer capacité sémantique du modèle et coût architectural | Non pour E.0 ; F.0 est uniquement diagnostique |
| DEC-023 | 2026-08-27 | 2 | Conserver la stabilité pairwise de F.0 mais rejeter sa qualité ; autoriser une seule composition avec B.0 | EXP-024 : 72/72 identiques, mais 18 couples et plusieurs rôles manifestement trop larges | G.0 combine deux composants déjà mesurés ; son échec ferme les variantes locales | Non pour F.0 ; G.0 seulement |
| DEC-024 | 2026-08-27 | 2 | Approuver G.0 comme référence de compatibilité, sans intégration, et vérifier la génération de ses frontières | EXP-025 : 72/72 identiques et matrice cohérente, mais ~141 s estimées depuis la question avant matching | H.0 puis cache/capacité/multi-page ; aucun produit avant ces portes | Oui si G.0 régresse avec frontières réellement régénérées |
| DEC-025 | 2026-08-27 | 2 | Approuver la répétabilité H.0 et ouvrir I.0 sur l'identité mono-candidat avant l'adaptatif | B.0 + deux H.0 : texte, sortie brute, tokens et hash strictement identiques ; D.0 a prouvé le biais d'identité listwise | Mesurer le contrat produit batch=1 sur 20 preuves ; aucun prompt ni produit nouveau | Oui si une répétition comparable diverge |
| DEC-026 | 2026-08-27 | 2 | Rejeter I.0 sans rotations ni retouche de prompt et tester le contexte hiérarchique déjà ingéré | I.0 : trois faux positifs, dont deux fragments ayant un parent recette exact dans headingPath et E19 inchangé | J.0 réactive le résolveur existant, conserve l'identité enfant et juge ensuite batch=1 | Oui si l'inspection source invalide les parents observés |
| DEC-027 | 2026-08-27 | 2 | Rejeter J.0 et isoler la quantification avant un modèle Qwen3 plus grand | J.0 double le temps, ne corrige aucun des trois faux positifs et perd deux cas OCR | K.0 reprend I.0 avec le même 4B Q8_0 ; aucune retouche de prompt/produit | Oui si Q8_0 n'est pas le même poids de base ou ne peut être servi de façon comparable |
| DEC-028 | 2026-08-27 | 2 | Rejeter Q8_0, conserver Q5 et mesurer un unique plafond Qwen3-8B | K.0 reproduit les vingt décisions d'I.0 et ajoute 112,5 % de latence ; runtime restauré | L.0 en non-thinking, puis arrêt de la série dense locale si échec | Oui si un run Q8 comparable contredit la matrice exacte |
| DEC-029 | 2026-08-27 | 2 | Rejeter L.0 et fermer la série locale quantification/taille sur le même gate d'identité | Run ChatML comparable : 20/20 protocoles, zéro réparation, mais 0/20 accepté, cinq contrôles perdus et +160,7 % versus I.0 | Q5 restauré ; aucune rotation, intégration 8B ou retouche du prompt d'identité | Oui seulement si un modèle/post-training réellement comparable franchit qualité et latence |
| DEC-030 | 2026-08-27 | 2 | Conserver les annotations d'identité sans les laisser supprimer les preuves avant affectation et tester M.0 | G.0 est stable/cohérent ; I.0–L.0 prouvent qu'un gate autonome peut être faux positif ou destructeur | Générer H.0 puis juger 20×4 couples G.0 ; matching uniquement mécanique si la matrice passe | Oui si M.0 perd une preuve claire, accepte un piège ou dépasse quatre minutes projetées |
| DEC-031 | 2026-08-27 | 2 | Requalifier H.0 en répétabilité intra-processus et ne pas contractualiser son hash à travers un redémarrage | M.0 régénère un autre texte/hash valide après le cycle de serveurs, malgré mêmes entrées/température 0 | Versionner et tracer les frontières LLM ; mesurer leur effet, ne pas exiger une chaîne fixe durable | Oui si une série cold-start contrôlée retrouve une identité stricte |
| DEC-032 | 2026-08-27 | 2 | Rejeter M.0 et fermer les gates runtime « identité seule » et « compatibilité seule » | M.0 préserve 5/5 recettes mais donne des rôles à six titres invalides ; I.0–L.0 ont déjà échoué comme identité autonome | Auditer la qualité des unités et annotations LLM matérialisées offline avant tout nouveau contrat runtime | Oui si une matrice comparable réfute les six faux positifs sans retouche locale |
| DEC-033 | 2026-08-27 | 2 | Ne pas corriger isolément `deterministic_v1` et ne pas réactiver les profils historiques | Profils actifs v5 ; profils LLM obsolètes sans carte LLM ; endpoint pouvant conserver le bruit déterministe | Prouver d'abord un enrichissement source-backed utile en mémoire, sans DB | Oui si l'audit des révisions/profils a omis une voie active réellement LLM |
| DEC-034 | 2026-08-27 | 2 | Rejeter N.0 et tester N.1 fenêtré sans retouche du prompt monolithique | Appel exact 5 094 tokens > 4 096 ; aucune décision sémantique n'a pu être produite | Huit fenêtres chunk/pages, schéma compact, evidence exacte, provenance mécanique | Oui si le même artefact N.0 passe sous 4096 sans supprimer d'information |
| DEC-035 | 2026-08-27 | 2 | Rejeter N.1 multi-item et enregistrer N.2 comme ablation hiérarchique/cardinalité | 7/8 sorties tronquées, sous-étapes manifestes, 581 s ; mais premier titre aligné 7/7 | Le LLM choisit explicitement zéro ou une identité primaire ; le code ne sélectionne rien post-hoc | Oui si l'artefact N.1 a été mal parsé ou si les sous-étapes sont réellement les unités principales |
| DEC-036 | 2026-08-27 | 2 | Rejeter N.2 comme contrat de preuve, conserver son choix primaire et passer à la sélection d'ancres N.3 | 7/7 titres corrects, mais 2 evidence non exactes et une identité OCR synthétique ; sources externes gardent les TextUnits comme autorité | Le LLM sélectionne un anchorId existant ; aucune chaîne source générée ; stabilité par permutations | Oui si les deux evidence N.2 sont ordinalement exactes ou si l'identité OCR est réellement primaire |
| DEC-037 | 2026-08-27 | 2 | Rejeter N.3, fermer les lignes arbitraires comme identités et mesurer N.4 structural | Full hashes tronqués ; 7 racines engagées mais une instruction OCR ; la fenêtre problématique n'a aucune structure parseur | Heading/section seuls ; clé hashée compacte ; aucun appel si absence d'ancre | Oui si le préfixe fenêtre 7 ne mappe pas univoquement l'instruction observée |
| DEC-038 | 2026-08-27 | 2 | Approuver N.4 comme brique Cuisine uniquement et ouvrir O.0 non-Cuisine inchangé | Trois ordres donnent 21/21 choix identiques, 7/7 racines et aucun appel sur l'absence structurelle | Geler document NIST, fenêtres et vérité humaine avant exécution ; aucun produit | Oui si un des artefacts N.4 ne contient pas les mappings/timings annoncés |
| DEC-039 | 2026-08-27 | 2 | Rejeter O.0 multi-domaine et ne pas filtrer sémantiquement les pseudo-headings dans le code | O.0 est mécaniquement 8/8 mais sémantiquement 5/9 ; trois sélections sont forcées par `minItems=1` et la préférence de niveau rate la fenêtre 1 | O.1 autorise 0..1 et rend la profondeur non prioritaire ; deuxième document tenu à l'écart figé avant appel | Oui si l'oracle pré-run ou l'artefact contredit les quatre erreurs exactes |
| DEC-040 | 2026-08-27 | 2 | Rejeter O.1 malgré le gain 5/9→8/9 et tester une seule décomposition indépendante P.0 | O.1 corrige 3/3 abstentions mais choisit encore le faux niveau 0 malgré la consigne explicite ; seuil 9/9 raté | Un booléen par ancre, résolution code limitée à cardinalité, canary aveugle figé après prompt | Oui si la fenêtre 1 O.1 contient réellement la clé `2.2` ou si l'oracle a été écrit après run |
| DEC-041 | 2026-08-27 | 2 | Rejeter P.0 et fermer les classifieurs d'identité primaire query-agnostic offline sur ce runtime | P.0 a 9/9 contrats mais seulement 5/9 sémantique et quatre faux négatifs ; O.1 avait le compromis inverse | Revenir au fil EvidenceBundle sans gate ; auditer le chemin vertical actuel avant code produit | Oui uniquement avec un changement architectural ou modèle radical, mesuré sur NIST/Cuisine/RFC/finance |
| DEC-042 | 2026-08-27 | 2 | Ne pas créer un second EvidenceBundle ; corriger verticalement le transport mécanique du contrat canonique existant après baseline et tests rouges | Checkpoint read-only : bundle, writer et vérificateur présents ; pertes aux frontières hints/voisins/UI | EXP-040 baseline, puis incrément TDD minimal sans ranking ni gate sémantique | Oui si un test vertical prouve que les métadonnées sont déjà conservées à toutes les frontières |
| DEC-043 | 2026-08-27 | Global | Rendre le reporting Telegram obligatoire environ chaque heure active et à chaque changement de verdict, avec artefact et preuve d'envoi | Reprise §16 ; engagement antérieur ; absence constatée par l'utilisateur ; rattrapage envoyé à 06:53 | Aucun long palier ne peut être poursuivi si un rapport dû n'est pas envoyé ou son échec tracé | Non sans demande explicite de l'utilisateur |
| DEC-044 | 2026-08-27 | Global | Rejeter Q.0, rouvrir la phase 1 et suspendre la phase 2 avant tout code EvidenceBundle | Q019 : classifier puis routeur choisissent clarification ; 0 outil/source ; deux facettes cumulatives transformées en alternatives ; faux vert du harnais | EXP-041/R.0 prompt-only, TDD rouge/vert, Q019 + non-Cuisine + vrai ambigu ; nouvelle baseline verticale sous un nouvel ID après réapprobation | Oui si la trace Q.0 montre en réalité une ambiguïté explicite ou un outil exécuté |
| DEC-045 | 2026-08-27 | 1 rouverte | Rejeter et rollbacker R.0 ; interdire une nouvelle retouche locale de prompt avant comparaison architecturale | Q019 clarifie encore ; Normes perd une route source-backed après scope/réparation ; ambigu invente une grille avant de clarifier ; 0/2 outils, 12 588 tokens | Cartographier l'asymétrie des quatre familles et préenregistrer une ablation de découpage avec les mêmes trois canaries | Oui seulement si les artefacts R.0 ou le rollback contredisent l'analyse |
| DEC-046 | 2026-08-27 | 1 rouverte | Tester en shadow un registre d'intake factorisé avant toute nouvelle intégration du routeur | E.1 a déjà réfuté quatre labels contraignants ; monolithique et prompt-only échouent ; le runner possède déjà une clarification post-observation | EXP-042 outil unique, huit canaries et trois ordres conditionnels ; aucun argument exécutable ni code produit | Oui si la cartographie du code ou les preuves EXP-009 sont erronées |
| DEC-047 | 2026-08-27 | 1 rouverte | Rejeter S.0 sans retouche et isoler une seule fois son signal knowledgeMode 8/8 | S.0 : 1/8 sémantique, 7/8 protocole, latence rouge ; mais corpus/opérationnel exact sur tous les cas | EXP-043 retire layout/actionnabilité/spans ; trois ordres conditionnels ; échec ferme la piste | Oui si le JSON S.0 ne contient pas les huit décisions knowledgeMode annoncées |
| DEC-048 | 2026-08-27 | 1 rouverte | Rejeter S.1 et fermer tout nouveau pré-classifieur court sur le Qwen3-4B actuel | Le même champ passe 8/8 dans S.0 puis 7/8 isolé ; inventaire bascule vers corpus malgré protocole et latence verts | Comparer observation-first avec capacité routeur supérieure ; aucune taxonomie/prompt S.1 bis | Oui seulement si l'artefact S.1 a été évalué avec un autre modèle ou une question différente |
| DEC-049 | 2026-08-27 | 1 rouverte | Sélectionner observation-first borné avant un routeur 8B | Le contrat fournit déjà `resumeRoute` et le runner clarifie après observation ; 8B local a coûté +160,7 %, détruit le rappel et exige ChatML/offload partiel | EXP-044 diffère seulement `source_backed`, garde grid/operational terminaux et réutilise un repair | Oui si T.0 échoue sémantiquement ou si un protocole routeur 8B démontre un meilleur compromis |
| DEC-050 | 2026-08-27 | 1 rouverte | Rejeter T.0, rollbacker et ouvrir une unique mesure routeur 8B sans code | Q019 corrigée mais lente ; scope Normes faux ; violation explicite de clarification non-grid avec query/scope inventés | EXP-045 modèle seul, template techniquement qualifié, huit cas, restauration Q5 obligatoire | Oui si T.0-D n'exigeait pas réellement de clarifier avant recherche |
| DEC-051 | 2026-08-27 | 1 rouverte | Classer la substitution directe par le 8B local comme techniquement inconclusive et fermer tout troisième essai de template | Les deux profils préenregistrés n'émettent aucun tool call ; exécuter la matrice aurait comparé une enveloppe non fonctionnelle ; Q5 est restauré | Recul architectural sur la frontière route/mission à partir des traces produit ; aucun nouveau classifieur, prompt local, modèle ou template avant choix documenté | Oui seulement si un profil de transport déjà produit et reproductible est prouvé compatible avec exactement ce runtime et ce modèle |
| DEC-052 | 2026-08-27 | 1 rouverte | Mesurer une frontière d'action directe plutôt qu'un nouveau label ou une retouche de méta-route | Les quatre traces partagent une double traduction : famille abstraite, méta-outil, puis vraie capacité ; elle permet pseudo-outil, scope divergent, override d'une clarification et 2–3 appels avant preuve | EXP-046 expose directement quatre outils documentaires, count et clarification en shadow ; aucune exécution ni mission dérivée | Oui si le code actuel prouve déjà que le second appel choisit et transporte directement les mêmes outils/arguments exécutés |
| DEC-053 | 2026-08-27 | 1 rouverte | Rejeter la frontière directe V.0 malgré son gain de latence et arrêter les variantes de prompt/outils Q5 | V.0 réduit cinq cas communs à 54,422 s mais échoue strictement 5/8 : outil planning faux, cible ratatouille perdue, query P1, identité ANSI perdue, anchors paraphrasés | Comparaison architecturale read-only et sourcée : sélection progressive de capacités, planification structurée sans fonctions concurrentes, orchestrateur plus capable ; aucune intégration avant mesure discriminante | Oui seulement si la revue stricte des anchors/catégories ou les réponses brutes sont erronées |
| DEC-054 | 2026-08-27 | 1 rouverte | Mesurer la récupération multi-tour exacte avant tool-search ou changement de modèle | ReAct, Qwen et les guides agents placent l'observation/retour d'erreur dans la boucle ; trois erreurs V.0 sont read-only et deux sont mécaniquement réparables ; six outils/2 189 tokens ne justifient pas encore tool-search | EXP-047 rejoue les cinq sorties rouges, renvoie seulement erreur mécanique ou résultat backend, maximum deux décisions ; Qwen3.5-9B reste candidat conditionnel | Oui si les sorties V.0 ne peuvent pas être rejouées exactement ou si le feedback contient une décision sémantique cachée |
| DEC-055 | 2026-08-27 | 1 rouverte | Rejeter la récupération Q5 sous la frontière 4K gelée et interdire W.0-bis | 1/5 ; une correction d'anchors au dernier tour ; planning ambigu tronqué puis inventé et >30 s ; trois appels refusés avant inférence à 4 871–5 173 tokens | Conserver les trois branches comme non évaluées sémantiquement, sans réduire le feedback après coup ; fermer Q5 prompt/registre/récupération | Oui seulement si le serveur n'était pas réellement à 4 096, si les prompts envoyés diffèrent ou si un feedback d'oracle est découvert |
| DEC-056 | 2026-08-27 | 1 rouverte | Qualifier Qwen3.5-9B comme unique prochain orchestrateur local, transport avant sémantique | Candidat conditionnel déjà retenu ; BFCL officiel supérieur au Qwen3.5-4B mais non comparable au Qwen3 local ; GGUF communautaire et 4 Gio imposent une qualification stricte | EXP-048 gèle dépôt/révision/fichier/hash, un profil embedded-Jinja non-thinking, smoke minimal, V.0 conditionnel et restauration Q5 | Oui si le hash/base du GGUF est faux, si b10098 ne supporte pas Qwen3.5 ou si la porte technique échoue avant toute sémantique |
| DEC-057 | 2026-08-27 | 1 rouverte | Rejeter Qwen3.5-9B X.0 et arrêter les substitutions locales non comparées | Transport vert mais canonique à 2/8, deux timeouts, médiane 25,519 s ; moins bon sémantiquement que Q5 direct à 3/8 ; 16 couches GPU déjà trop lentes | Restaurer Q5 ; établir une comparaison sourcée petit modèle spécialisé / autre matériel / référence distante sans preuves avant de demander le choix d'enveloppe | Oui seulement si l'artefact canonique n'a pas utilisé le profil gelé ou si les oracles diffèrent de V.0 |
| DEC-058 | 2026-08-27 | 1 rouverte | Autoriser Granite 4.2-3B comme dernière substitution locale compacte justifiée | Famille/post-training/format outil différents ; GGUF officiel 2,244 Go potentiellement full-GPU ; BFCL 52,41, français et low-effort documentés ; Qwen3.5-4B non discriminant, FunctionGemma exige fine-tuning, Phi moins direct | EXP-049/Y.0 gèle runtime actuel, 41 couches, low-effort, paramètres officiels, transport avant matrice et restauration ; échec ferme les modèles compacts | Oui si la révision/hash/base est fausse, si IBM ne documente pas tool calling/français, ou si le fichier ne tient pas dans l'enveloppe annoncée |
| DEC-059 | 2026-08-27 | 1 rouverte | Rejeter Y.0 et fermer les substitutions locales compactes non entraînées SAAIA | Granite améliore à 4/8 mais invente anchors, perd ANSI/scope, tronque deux décisions à 256 tokens et dépasse le cold first-call de 493 ms | Maintenir Q5 restauré ; suspendre nouveaux modèles/prompts ; demander le choix utilisateur entre matériel local, référence distante sans preuves ou fine-tuning spécifique | Oui seulement si le JSON n'a pas reçu les paramètres IBM tracés ou si les oracles/outils ne sont pas ceux de V.0 |
| DEC-060 | 2026-08-27 | 1 rouverte | Recommander une référence distante bornée comme prochaine expérience diagnostique, sans l'autoriser ni en faire une dépendance produit | Trois frontières locales non entraînées restent à 2–4/8 ; le paquet exact peut tester la capacité d'un modèle fort sans documents/résultats ; un achat ou dataset serait plus irréversible | Attendre l'accord explicite sur fournisseur, modèle, coût, rétention/région, identifiants et 8+16 appels ; sinon suivre le choix matériel ou fine-tuning avec son propre protocole | Oui : l'utilisateur peut choisir une autre enveloppe ou refuser toute divulgation |
| DEC-061 | 2026-08-27 | 1 rouverte | Proposer GPT-5.6 Sol comme unique plafond de capacité distant et séparer strictement le consentement global du consentement Europe MAM/ZDR | OpenAI le documente comme flagship avec function calling ; coût canonique max estimé 0,115092 USD ; ajouter Terra ou un autre fournisseur avant ce verdict diluerait la variable capacité | Fiche d'accord à 8 appels/cap 0,20 USD ; global accepte jusqu'à 30 j d'abuse logs, Europe exige preuve MAM/ZDR ; 16 appels, intégration et backend restent interdits | Oui : choix utilisateur matériel/fine-tuning/refus, ou source officielle invalidant modèle/prix/rétention |
| DEC-062 | 2026-08-27 | Global | Placer le Goal en attente bloquée après au moins trois continuations sans autorisation d'enveloppe | EXP-049 demande le choix ; EXP-050 prépare les trois voies ; EXP-051 rend le distant autorisable ; manifeste toujours false/nul et aucune autre action sûre permise par le registre | Aucun appel, achat, téléchargement ou entraînement ; reprise uniquement sur décision utilisateur explicite ; état Q5/worktree préservé | Immédiatement réversible dès réception du choix utilisateur |

---

## 19. Registre des variantes historiques rejetées

| Variante | Résultat observé | Décision actuelle |
|---|---|---|
| Augmenter simplement le contexte à 8K | Plus lent et moins précis sur les cartes réelles | Ne pas reprendre sans nouvelle preuve |
| Bitmask de compatibilité | Biais de position | Rejeté |
| Indices numériques compacts | Choix positionnels et mauvais remapping | Rejeté |
| Garde global de préparation du pool | Pool déclaré prêt malgré des placements absurdes | Supprimé |
| Audit binaire trop compact | Trop de faux positifs | Rejeté |
| Fusion verbeuse résolution + audit | JSON tronqué, lent et erroné | Rejeté |
| Audit par candidat en cascade | Trop d'appels et latence extrême | Rejeté |
| Recherche immédiate après chaque rejet | 221 éléments et dépassement de contexte | Rejeté |
| Révision locale prolongée sans recherche | Faux verts sémantiques | Rejeté |
| Prompting supplémentaire pour deviner « recette » | Dérive vers « planning de repas » | Rejeté |
| Timeout plus long | Masque le problème sans améliorer l'architecture | Interdit sans benchmark |

---

## 20. Registre des preuves

| ID | Phase | Type | Chemin/commande | Date | Ce que cela prouve | Limites |
|---|---|---|---|---|---|---|
| PRV-000 | Historique | Log live | `artifacts/client-live-final-weekly-meal-plan-20260807-130111/progress.log` | 2026-08-07 | Dernier chemin complet et fuite d'hypothèse | Pas une validation actuelle |
| PRV-001 | Historique | TRX | `artifacts/client-live-final-weekly-meal-plan-20260807-role-memory-anchor-audit/Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf-live.trx` | 2026-08-07 | Échec par timeout pendant compatibilité | Un seul run |
| PRV-002 | Baseline | `/ready` | Backend distant | 2026-08-26 | Services serveur sains et provenance canonique prête | Ne prouve pas les counts détaillés du corpus |
| PRV-003 | 0 | État Git | `artifacts/goal-rag-end-to-end-20260826-233646/git-baseline.txt` | 2026-08-26 | Branche, HEAD, avance/retard, status, stat, numstat et diff-check | Le worktree est volontairement très sale |
| PRV-004 | 0 | Runtime LLM | `artifacts/goal-rag-end-to-end-20260826-233646/llm-props.json` et `runtime-hashes.txt` | 2026-08-26 | Modèle, quantification, contexte, backend et hashes exacts | Ne prouve pas encore la qualité sémantique |
| PRV-005 | 0 | Build | `artifacts/goal-rag-end-to-end-20260826-233646/build-x64.txt` et `x64-assembly-hashes.txt` | 2026-08-26 | Debug/x64 vert et assemblages identifiés | Après restauration explicite du runtime pack |
| PRV-006 | 0 | Tests | `artifacts/goal-rag-end-to-end-20260826-233646/test-results/phase0-full-deterministic.trx` | 2026-08-26 | 1 235/1 235 tests déterministes verts | Pas un run LLM réel |
| PRV-007 | 0 | Probe cartes | `artifacts/live-content-card-inventory-20260826-234514/report.txt` | 2026-08-26 | Endpoint citable, 120 cartes matérialisées, total 890 | Inventaire mécanique, sans jugement sémantique |
| PRV-008 | 0 | Probe navigation | `artifacts/live-document-navigation-20260826-234543.txt` | 2026-08-26 | 200 entrées ancrées et contexte résoluble | Requête générique `recette` uniquement |
| PRV-009 | 0 | Probe retrieval | `artifacts/goal-rag-end-to-end-20260826-233646/retrieval-inventory-report.txt` | 2026-08-26 | 48 hits bruts et 34 preuves distinctes sur quatre axes repas | Candidats bruités et cartes appariées absentes |
| PRV-010 | 0 | Corpus SQL/Qdrant | `artifacts/goal-rag-end-to-end-20260826-233646/corpus-baseline.txt` | 2026-08-26 | 3 882 chunks SQL = 3 882 points Qdrant; 2 971 ancres; 319 navigations; 890 cartes v5 | Lecture ponctuelle de l'état actif |
| PRV-011 | 0 | Approbation | `artifacts/goal-rag-end-to-end-20260826-233646/phase0-evidence-summary.md` | 2026-08-26 | Tous les critères et limites de phase 0 sont consolidés | N'approuve aucune qualité live end-to-end |
| PRV-012 | 1 | Routeur live | `artifacts/live-native-router-grid-20260827-000455/report.txt` | 2026-08-27 | Grille 5 × 4 et scope Cuisine préservés en 40,995 s | Type `repas quotidien`; aucun outil choisi |
| PRV-013 | 1 | Définition live | `artifacts/live-source-backed-semantic-granularity-20260826-235905/report.txt` | 2026-08-26 | Type `Plat cuisiné` et hypothèse explicite en 26,670 s | Appel isolé; règle tronquée acceptée |
| PRV-014 | 1 | Stratégie live | `artifacts/live-source-backed-semantic-granularity-20260827-000011/report.txt` | 2026-08-27 | Reproduit type et query du livrable, rejetés après deux essais | Option runtime désactivée |
| PRV-015 | 1 | Frontière outil live | `artifacts/live-source-backed-agent-v2-tool-choice-20260827-000047/report.txt` | 2026-08-27 | Navigation sans query puis context batch, mais 150,202 s et enrichissement non demandé | Executor synthétique; définition désactivée |
| PRV-016 | 1 | Protocole | `artifacts/goal-rag-end-to-end-20260826-233646/phase1/probe-inventory-and-protocol.md` | 2026-08-27 | Inventaire des gaps, cas figés, métriques et invariants d'EXP-003 | À exécuter après ajout du harness |
| PRV-017 | 1 | Harness | `client/SAAIA.Client.ToolAgent.Tests/LiveSourceBackedIntakeFirstActionBenchmarkTests.cs` | 2026-08-27 | Routeur réel, options runtime, chronologie des appels et frontière du premier outil reliés dans une probe read-only | Instrumentation de test, pas une modification produit |
| PRV-018 | 1 | Run unifié P1 | `artifacts/goal-rag-end-to-end-20260826-233646/phase1/phase1-exp003-baseline-a-p1-rerun.json` et `.trx` | 2026-08-27 | 81,014 s jusqu'à navigation ; 4 appels attribués ; zéro fuite d'hypothèse/axe ; scope Cuisine conservé | Une seule permutation ; frontière outil, pas observation backend utile |
| PRV-019 | 1 | Matrice unifiée | `artifacts/goal-rag-end-to-end-20260826-233646/phase1/phase1-exp003-baseline-a-p2-p5.json`, `phase1-exp003-baseline-a-generic.json` et TRX associés | 2026-08-27 | Stabilité, latence, arguments et fallbacks de huit scénarios supplémentaires | Frontière outil uniquement ; cas non-Cuisine synthétique |
| PRV-020 | 1 | Verdict A | `artifacts/goal-rag-end-to-end-20260826-233646/phase1/phase1-exp003-baseline-a-analysis.md` | 2026-08-27 | Consolide métriques, inspection humaine, défauts et décision C | Ne valide encore aucune correction |
| PRV-021 | 1 | Tests C | `phase1/phase1-exp005-variant-c-targeted-approved.trx` et `phase1-exp005-variant-c-regression.trx` | 2026-08-27 | 26/26 puis 268/268 ; action routeur avant revues, scope, non-Cuisine et clarification | Tests déterministes uniquement |
| PRV-022 | 1 | Runs C/P1 | `phase1/phase1-exp005-variant-c-p1.json`, `phase1-exp005-variant-c-p1-rerun.json` et TRX associés | 2026-08-27 | Un seul appel ; 21,264–43,962 s ; même query contaminée 2/2 | Frontière outil, sans observation backend |
| PRV-023 | 1 | Tests et run C.1 | `phase1/phase1-exp006-variant-c1-targeted-approved.trx`, `phase1-exp006-variant-c1-p1.json` et `.trx` | 2026-08-27 | 173/173 déterministes, mais fuite live inchangée et 43,658 s | Un seul run live, suffisant pour déclencher la condition de recul avec C |
| PRV-024 | 1 | Analyse C/C.1 | `artifacts/goal-rag-end-to-end-20260826-233646/phase1/phase1-exp005-exp006-variant-c-analysis.md` | 2026-08-27 | Sépare gain causal, latence variable, échec sémantique et décision de contrat | N'approuve pas D ni la phase 1 |
| PRV-025 | 1 | Tests D | `phase1/phase1-exp007-variant-d-targeted.trx` et `phase1-exp007-variant-d-regression.trx` | 2026-08-27 | 174/174 et 269/269 ; query absente du schéma grille, search rejeté, type provisoire | Ne prouve pas le choix live du bon contrat |
| PRV-026 | 1 | Run et analyse D | `phase1/phase1-exp007-variant-d-p1.json`, `.trx` et `phase1-exp007-variant-d-analysis.md` | 2026-08-27 | Prouve le contournement par le contrat direct et le dépassement de 804 ms | Une seule permutation ; suffisante pour rejeter D |
| PRV-027 | 1 | E/E.1 | `phase1/phase1-exp008-variant-e-analysis.md` et `phase1-exp009-variant-e1-analysis.md` | 2026-08-27 | Deux étages corrigent P1 ; spécialisation rend repas 5/5 sous 30 s mais dégrade les génériques | Variantes rejetées, pas la baseline finale |
| PRV-028 | 1 | F/F.1 | `phase1/phase1-exp010-variant-f-analysis.md` et `phase1-exp011-variant-f1-analysis.md` | 2026-08-27 | Les labels grid/defer délèguent P1 et réouvrent la query contaminée | Un run P1 par variante, suffisant pour leur invariant principal |
| PRV-029 | 1 | G live | `phase1/phase1-exp012-variant-g-p1.json`, `phase1-exp012-variant-g-p2-p5.json`, `phase1-exp012-variant-g-generic.json` et TRX | 2026-08-27 | Repas 5/5 sous 30 s, simple et nommé corrects, ambigu clarifié ; isole le veto Maintenance | Frontière outil seulement ; observation non exécutée |
| PRV-030 | 1 | Correction générique | `phase1/phase1-exp013-overlap-red.trx`, `phase1-exp013-overlap-targeted.trx`, `phase1-exp013-overlap-regression.trx` et `phase1-exp013-non-cuisine.json` | 2026-08-27 | TDD exact, 188/188, 283/283 et Maintenance cartes/sans query en 18,938 s | Type toujours provisoire par conception |
| PRV-031 | 1 | Ablation observation | `artifacts/live-content-card-inventory-20260827-015849/report.txt` et `artifacts/live-content-card-inventory-20260827-020042/report.txt` | 2026-08-27 | Ordered donne du front matter ; representative expose des positions internes et objets culinaires | Inspection humaine d'un lot de 20 par mode |
| PRV-032 | 1 | Tests G.1 | `phase1/phase1-exp016-representative-red.trx`, `phase1-exp016-representative-targeted.trx` et `phase1-exp016-representative-regression.trx` | 2026-08-27 | Rouge exact, puis 189/189 ciblés et 284/284 élargis | Déterministe ; ne prouve pas seul la qualité live |
| PRV-033 | 1 | Observation intégrée | `phase1/phase1-exp017-variant-g1-integrated-p1.json` et `.trx` | 2026-08-27 | 20 preuves/10 documents en 21,010 s depuis la question, backend 179 ms, aucune query/fuite | P1 uniquement ; executor test-only cartes |
| PRV-034 | 1 | Matrice G.1 | `phase1/phase1-exp018-variant-g1-closing-matrix.json` et `.trx` | 2026-08-27 | P2–P5 22,803–27,922 s ; Maintenance 24,639 s ; cartes/representative/scope/type provisoire stables | Frontière outil pour ces cinq scénarios |
| PRV-035 | 1 | Approbation | `phase1/phase1-exp012-exp018-variant-g-g1-analysis.md` | 2026-08-27 | Consolide variantes, métriques, inspection, critères et risques ; autorise la phase 2 | N'approuve aucun audit, matching, retrieval ou writer aval |
| PRV-036 | 2 | Inventaire/protocole | `phase2/audit-inventory-and-protocol.md` | 2026-08-27 | Décrit le chemin actuel, le coût théorique, les annotations/caches, le lot figé et les métriques A/B | Analyse statique ; baseline live non encore exécutée |
| PRV-037 | 2 | Baseline live A | `phase2/exp019-baseline-a.json` et `client/SAAIA.Client.ToolAgent.Tests/TestResults/phase2-exp019-baseline-a.trx` | 2026-08-27 | Snapshot 20/20, 12 appels tracés, tokens, timings, contrats, sorties, décisions et compatibilités | Harness test-only ; s'arrête avant retrieval/matching/writer |
| PRV-038 | 2 | Inspection humaine A | `phase2/exp019-baseline-a-human-review.md` | 2026-08-27 | Sépare validité du protocole et qualité ; documente chaque candidat, chaque rôle et le refus sémantique | Un ordre et un lot ; deux cas d'identité restent explicitement discutables |
| PRV-039 | 2 | Protocole B.0 | `phase2/exp020-b0-role-boundaries-protocol.md` | 2026-08-27 | Prouve le gap runtime label=label et fige hypothèse, invariants, mesures et critère de passage à B.1 | Expérience non encore exécutée |
| PRV-040 | 2 | Run live B.0 | `phase2/exp020-baseline-b0.json` et `client/SAAIA.Client.ToolAgent.Tests/TestResults/phase2-exp020-baseline-b0.trx` | 2026-08-27 | Trace l'appel de rôles, ses sorties, les 13 appels et le différentiel complet avec A | Un ordre ; suffisant pour rejeter les invariants sans passer aux permutations |
| PRV-041 | 2 | Revue B.0 | `phase2/exp020-baseline-b0-human-review.md` | 2026-08-27 | Documente ajouts absents, gains/régressions par candidat et décision de recul | N'approuve aucune alternative suivante |
| PRV-042 | 2 | Protocole C.0 | `phase2/exp021-c0-atomic-definition-protocol.md` | 2026-08-27 | Fige l'ablation, les faux positifs cibles et les critères d'arrêt avant code | Expérience non encore exécutée |
| PRV-043 | 2 | Run live C.0 | `phase2/exp021-baseline-c0.json` et `client/SAAIA.Client.ToolAgent.Tests/TestResults/phase2-exp021-baseline-c0.trx` | 2026-08-27 | Trace définition atomique, 9 appels, 20 décisions et compatibilités sur le lot gelé | Un ordre ; critères initiaux insuffisants pour permutations |
| PRV-044 | 2 | Revue C.0 | `phase2/exp021-baseline-c0-human-review.md` | 2026-08-27 | Prouve le gain d'identité, inspecte E19 dans sa source et identifie le contexte manquant de compatibilité | Brique utile mais expérience rejetée |
| PRV-045 | 2 | Protocole D.0 | `phase2/exp022-d0-compatibility-context-protocol.md` | 2026-08-27 | Fige l'injection générique, comparateurs et critères avant modification test-only | Expérience non encore exécutée |
| PRV-046 | 2 | Runs D.0 | `phase2/exp022-baseline-d0-frozen-c0.json`, `phase2/exp022-d0-rotation07.json`, `phase2/exp022-d0-rotation13.json` et TRX | 2026-08-27 | Trois rotations causales, protocoles/timings/décisions complets | Définition C.0 gelée test-only ; pas un chemin produit |
| PRV-047 | 2 | Analyse D.0 | `phase2/exp022-d0-permutation-analysis.md` | 2026-08-27 | Prouve le biais d'ordre identité/compatibilité et justifie E.0 sans répéter les formats positionnels | Trois rotations d'un lot |
| PRV-048 | 2 | Protocole E.0 | `phase2/exp023-e0-single-candidate-named-roles-protocol.md` | 2026-08-27 | Distingue la nouvelle forme des variantes historiques et fige critères/rotations | Non exécuté |
| PRV-049 | 2 | Runs E.0 | `phase2/exp023-e0-role-rotation00.json`, `phase2/exp023-e0-role-rotation01.json`, `phase2/exp023-e0-role-rotation02.json` et TRX | 2026-08-27 | Mesure 18 appels indépendants, coûts et variations par ordre de rôles | Six candidats gelés, compatibilité seule |
| PRV-050 | 2 | Analyse E.0 | `phase2/exp023-e0-role-order-analysis.md` | 2026-08-27 | Prouve le biais multi-rôle et autorise seulement la référence pairwise | Aucun chemin end-to-end |
| PRV-051 | 2 | Protocole F.0 | `phase2/exp024-f0-pairwise-binary-reference-protocol.md` | 2026-08-27 | Fige matrice, répétitions et décision de recul | Non exécuté |
| PRV-052 | 2 | Runs F.0 | `phase2/exp024-f0-pair-rotation00.json`, `phase2/exp024-f0-pair-rotation07.json`, `phase2/exp024-f0-pair-rotation13.json` et TRX | 2026-08-27 | 72 booléens stables, tokens et latence pairwise | Compatibilité seule, six candidats gelés |
| PRV-053 | 2 | Analyse F.0 | `phase2/exp024-f0-pairwise-analysis.md` | 2026-08-27 | Sépare stabilité parfaite et qualité trop permissive ; inventorie le modèle disponible | N'approuve aucune cible produit |
| PRV-054 | 2 | Protocole G.0 | `phase2/exp025-g0-pairwise-with-llm-boundaries-protocol.md` | 2026-08-27 | Fige la dernière composition et la condition de pivot | Non exécuté |
| PRV-055 | 2 | Runs G.0 | `phase2/exp025-g0-pair-rotation00.json`, `phase2/exp025-g0-pair-rotation07.json`, `phase2/exp025-g0-pair-rotation13.json` et TRX | 2026-08-27 | 72 booléens identiques et dix couples cohérents | Frontières/candidats gelés |
| PRV-056 | 2 | Analyse G.0 | `phase2/exp025-g0-analysis.md` | 2026-08-27 | Consolide qualité, stabilité, coût complet estimé et portes restantes | Pas de page suivante ni intégration |
| PRV-057 | 2 | Protocole H.0 | `phase2/exp026-h0-role-generation-repeatability-protocol.md` | 2026-08-27 | Fige la répétabilité avant code adaptatif | Non exécuté |
| PRV-058 | 2 | Runs H.0 | `phase2/exp026-h0-role-repeat01.json`, `phase2/exp026-h0-role-repeat02.json` et TRX | 2026-08-27 | Deux sorties réelles strictement identiques à B.0, timings et tokens compris | Même modèle/entrée/état runtime figés |
| PRV-059 | 2 | Analyse H.0 | `phase2/exp026-h0-role-generation-repeatability-analysis.md` | 2026-08-27 | Ferme la porte de répétabilité et sépare stabilité de qualité sémantique | N'approuve pas B.0 isolément ni le coût end-to-end |
| PRV-060 | 2 | Protocole I.0 | `phase2/exp027-i0-single-candidate-identity-protocol.md` | 2026-08-27 | Fige le contrat mono-candidat, la vérité humaine, les rotations et l'arrêt | Non exécuté |
| PRV-061 | 2 | Run I.0 | `phase2/exp027-i0-identity-rotation00.json` et `.trx` | 2026-08-27 | 20 audits indépendants, coût, classifications et faux positifs | Arrêt avant rotations selon protocole |
| PRV-062 | 2 | Analyse I.0 | `phase2/exp027-i0-single-candidate-identity-analysis.md` | 2026-08-27 | Rejette le mono-candidat direct et distingue identité de preuve et libellé sémantique | N'évalue pas encore la résolution hiérarchique |
| PRV-063 | 2 | Protocole J.0 | `phase2/exp028-j0-hierarchical-label-resolution-protocol.md` | 2026-08-27 | Fige la réactivation du résolveur existant et les critères source-backed | Non exécuté |
| PRV-064 | 2 | Run J.0 | `phase2/exp028-j0-hierarchy-rotation00.json` et `.trx` | 2026-08-27 | Sépare 13 résolutions, 20 jugements, coût et libellés finaux | Arrêt avant rotations selon protocole |
| PRV-065 | 2 | Analyse J.0 | `phase2/exp028-j0-hierarchical-label-resolution-analysis.md` | 2026-08-27 | Rejette la cascade hiérarchique actuelle et autorise le pivot quantification | Un seul modèle/quantification |
| PRV-066 | 2 | Protocole K.0 | `phase2/exp029-k0-qwen3-4b-q8-identity-protocol.md` | 2026-08-27 | Fige le modèle, la machine, la restauration et les critères Q8_0 | Non exécuté |
| PRV-067 | 2 | Modèle/runtime K.0 | Q8_0 SHA-256 `260B...C19662`, `/props`, logs Q8 et restauration Q5 | 2026-08-27 | Prouve fichier, quantification, offload, santé et retour au runtime initial | Capture ponctuelle locale |
| PRV-068 | 2 | Run K.0 | `phase2/exp029-k0-q8-identity-rotation00.json` et `.trx` | 2026-08-27 | Même entrée/tokens, décisions Q8 et coût comparé | Arrêt avant rotations selon protocole |
| PRV-069 | 2 | Analyse K.0 | `phase2/exp029-k0-qwen3-4b-q8-identity-analysis.md` | 2026-08-27 | Disculpe Q5_K_M et rejette Q8_0 sur qualité/coût | Ne compare pas encore une taille supérieure |
| PRV-070 | 2 | Protocole L.0 | `phase2/exp030-l0-qwen3-8b-capacity-protocol.md` | 2026-08-27 | Fige confondants, non-thinking, machine et critères 8B | Non exécuté |
| PRV-071 | 2 | Modèle/runtime L.0 | 8B SHA-256 `D98C...5785`, logs serveurs natif/non-thinking/ChatML | 2026-08-27 | Prouve fichier officiel, offload 22 couches, contexte 4096 et changement de template borné | Qwen3-8B original, pas Instruct-2507 |
| PRV-072 | 2 | Incident technique L.0 | `phase2/exp030-l0-8b-identity-rotation00.json`, TRX et logs | 2026-08-27 | Isole le conflit `<think>`/JSON Schema avant verdict sémantique | Run interrompu après deux appels ; inconcluant sur la qualité |
| PRV-073 | 2 | Run comparable L.0 | `phase2/exp030-l0-8b-chatml-identity-rotation00.json` et TRX | 2026-08-27 | 20 jugements indépendants, zéro réparation, décisions, tokens et temps | Un ordre suffit puisque le rappel échoue 0/5 |
| PRV-074 | 2 | Analyse L.0 | `phase2/exp030-l0-qwen3-8b-capacity-analysis.md` | 2026-08-27 | Sépare incident runtime et rejet sémantique, ferme la série dense locale | N'attribue pas causalement l'échec à la taille seule |
| PRV-075 | 2 | Restauration Q5 | `phase2/exp030-l0-q5-restore.stderr.log`, `/health`, `/props`, `/v1/models` | 2026-08-27 | Q5_K_M standard restauré sur 1234, 4096, un slot, PID 8692 | Capture ponctuelle après L.0 |
| PRV-076 | 2 | Protocole M.0 | `phase2/exp031-m0-assignment-without-identity-gate-protocol.md` | 2026-08-27 | Fige le pivot, la vérité humaine, 80 couples, coût et arrêt avant code | Non exécuté |
| PRV-077 | 2 | Harness et run M.0 | `LiveCandidateAuditCompatibilityBenchmarkTests.cs`, `phase2/exp031-m0-smoke-clean.trx`, run JSON et TRX | 2026-08-27 | Zéro identité, un rôle, 80 pairwise, protocoles, matrice, tokens et temps | Test échoue volontairement sur le hash après écriture complète |
| PRV-078 | 2 | Analyse M.0 | `phase2/exp031-m0-assignment-without-identity-gate-analysis.md` | 2026-08-27 | Prouve six faux positifs, cinq contrôles préservés, coût et régression cold-start | Un ordre suffit pour rejeter les portes initiales |
| PRV-079 | 2 | Audit profils/backoffice | `phase2/backoffice-profile-audit.md` | 2026-08-27 | Prouve versions actives, jobs, profils historiques, sélection SQL et pointeur `deterministic_v1` | Capture read-only ponctuelle de la base courante |
| PRV-080 | 2 | Protocole et entrée N.0 | `phase2/exp032-n0-live-backoffice-profile-protocol.md`, `phase2/exp032-n0-input-30-recettes.json` | 2026-08-27 | Fige document, révision, service exact, seuils et hash d'entrée | Un document canary ; aucune écriture |
| PRV-081 | 2 | Harness et run N.0 | `LiveBackofficeProfileEnrichmentBenchmarkTests.cs`, smokes, `phase2/exp032-n0-live-backoffice-profile.json` et TRX | 2026-08-27 | Capture requête/réponse et prouve HTTP 400, 5 094/4 096, zéro profil/carte | Le test échoue volontairement après écriture de l'artefact |
| PRV-082 | 2 | Analyse N.0 | `phase2/exp032-n0-live-backoffice-profile-analysis.md` | 2026-08-27 | Sépare échec de contrat monolithique et capacité sémantique inconnue des fenêtres | Pas de second document selon arrêt préenregistré |
| PRV-083 | 2 | Protocole N.1 | `phase2/exp033-n1-windowed-source-card-extraction-protocol.md` | 2026-08-27 | Fige prompt générique, vérité humaine, budgets, provenance et seuil 7/7 avant code | Non exécuté |
| PRV-084 | 2 | Entrée, harness et run N.1 | `phase2/exp033-n1-input-30-recettes-windows.json`, `LiveWindowedSourceCardExtractionBenchmarkTests.cs`, smoke, live et TRX | 2026-08-27 | Prouve textes identiques, UUID/pages, budget contexte, troncatures, tokens et temps | Un seul canary ; test échoue après artefact complet |
| PRV-085 | 2 | Analyse N.1 | `phase2/exp033-n1-windowed-source-card-extraction-analysis.md` | 2026-08-27 | Inspection exhaustive des titres, faux items et causalité cardinalité/sortie | JSON tronqués non récupérés comme résultats valides |
| PRV-086 | 2 | Protocole N.2 | `phase2/exp034-n2-primary-window-identity-protocol.md` | 2026-08-27 | Fige 0..1 item, limites de champs, prompt hiérarchique et seuils avant code | Non exécuté |
| PRV-087 | 2 | Harness et run N.2 | `LivePrimaryWindowIdentityBenchmarkTests.cs`, smoke, `phase2/exp034-n2-primary-window-identity.json` et TRX | 2026-08-27 | Prouve 8 JSON/stop, 7/7 titres, 6 groundings et 173 s | Un seul run ; artefact avant assertion |
| PRV-088 | 2 | Analyse N.2 | `phase2/exp034-n2-primary-window-identity-analysis.md` | 2026-08-27 | Sépare choix primaire réussi, evidence libre défaillante et fenêtre OCR | Pas de normalisation/rerun post-hoc |
| PRV-089 | 2 | Recul architectural | `phase2/architectural-step-back-after-n2.md` et sources primaires liées | 2026-08-27 | Compare génération, hiérarchie, contextualisation, structured output et sélection extractive | Transposition à mesurer, pas preuve produit |
| PRV-090 | 2 | Protocole N.3 | `phase2/exp035-n3-source-anchor-selection-protocol.md` | 2026-08-27 | Fige ancres hashées, enum dynamique, vérité, coût et permutations avant code | Non exécuté |
| PRV-091 | 2 | Entrée, harness et run N.3 | `phase2/exp035-n3-input-source-anchors.json`, `LiveSourceAnchorSelectionBenchmarkTests.cs`, smoke, live et TRX | 2026-08-27 | Prouve 64 ancres, troncature hash, coût et préfixes univoques | JSON incomplets non acceptés comme résultats valides |
| PRV-092 | 2 | Analyse N.3 | `phase2/exp035-n3-source-anchor-selection-analysis.md` | 2026-08-27 | Sépare racines 7/7 et fausse éligibilité de ligne OCR | Aucune permutation |
| PRV-093 | 2 | Protocole N.4 | `phase2/exp036-n4-structural-anchor-selection-protocol.md` | 2026-08-27 | Fige éligibilité structurelle, clé compacte, non-applicabilité et rotations | Non exécuté |
| PRV-094 | 2 | Harness, matrice et analyse N.4 | `LiveStructuralAnchorSelectionBenchmarkTests.cs`, `phase2/exp036-n4-structural-anchor-*.json`, TRX et analyse | 2026-08-27 | Prouve 21/21 choix Cuisine stables, grounding complet, budgets et contrôle sans appel | Validation bornée à Cuisine |
| PRV-095 | 2 | Protocole, entrée et oracle O.0 | `phase2/exp037-o0-non-cuisine-structural-anchor-protocol.md`, entrées NIST et `exp037-o0-nist-human-truth.*` | 2026-08-27 | Fige document/révision/fenêtres et neuf décisions avant tout appel O.0 | Oracle SHA-256 séparé du prompt |
| PRV-096 | 2 | Run et analyse O.0 | `phase2/exp037-o0-nist-structural-anchor-canonical.json`, TRX et analyse | 2026-08-27 | Prouve mécanique 8/8, 46,063 s et quatre erreurs sémantiques exactes | Permutations annulées par protocole |
| PRV-097 | 2 | Protocole O.1 | `phase2/exp038-o1-semantic-abstention-protocol.md` | 2026-08-27 | Fige les deux seules modifications, quatre portes et held-out avant appel | Non exécuté |
| PRV-098 | 2 | Canary RFC préfigé O.1 | `phase2/exp038-o1-heldout-rfc9110-*` | 2026-08-27 | Fige document/révision, huit fenêtres et 4 sélections/4 abstentions avant O.1 | Jamais exécuté car porte NIST rouge |
| PRV-099 | 2 | Run et analyse O.1 | `phase2/exp038-o1-nist-canonical.json`, TRX et analyse | 2026-08-27 | Prouve 8/9, abstentions corrigées et faux niveau persistant | Aucune permutation/relecture aval |
| PRV-100 | 2 | Protocole et vérité P.0 | `phase2/exp039-p0-independent-anchor-legitimacy-protocol.md`, `exp039-p0-nist-anchor-truth.*` | 2026-08-27 | Fige prompt booléen, résolution cardinalité et 9 jugements avant code/appel | Non exécuté au moment du gel |
| PRV-101 | 2 | Canary aveugle finance P.0 | `phase2/exp039-p0-blind-cocacola-*` | 2026-08-27 | Choisi après gel du prompt, vérité 7 true/1 false avant code/appel | Jamais exécuté car porte NIST rouge |
| PRV-102 | 2 | Harness, run et analyse P.0 | `LiveIndependentStructuralAnchorLegitimacyBenchmarkTests.cs`, `phase2/exp039-p0-nist-canonical.json`, TRX et analyse | 2026-08-27 | Prouve 9/9 contrats courts mais quatre faux négatifs et 5/9 résolutions | Série offline fermée |
| PRV-103 | 2 | Checkpoint vertical read-only | `phase2/evidencebundle-vertical-checkpoint.md` | 2026-08-27 | Cartographie retrieval→bundle→LLM→writer→verifier→UI et trois pertes de transport avec fichiers/lignes | Diagnostic statique ; baseline live non encore exécutée |
| PRV-104 | 2 | Protocole Q.0 | `phase2/exp040-q0-evidence-transport-baseline-protocol.md` | 2026-08-27 | Fige Q019, banque/hash, runtime, commande, mesures, seuils et arrêt avant run | Non exécuté au moment du gel |
| PRV-105 | Global | Rapport Telegram | `phase2/telegram-palier-rattrapage-20260827-0635.txt` ; sortie notifier ; SHA-256 `EDC0E70...DEFDB` | 2026-08-27 | Prouve un message UTF-8 de 3 354 caractères accepté et relu identiquement par Telegram | Rattrapage consolidé ; ne remplace pas la cadence future |
| PRV-106 | 2 | Live Q.0 | `phase2/exp040-q0-live-output/*`, `phase2/test-results/exp040-q0-live.trx` | 2026-08-27 | Prouve terminaison 52 s, réponse clarification, 0 source, payload nul et faux vert de capture | N'atteint pas retrieval/EvidenceBundle |
| PRV-107 | 2 | Trace runtime Q.0 | `AppData/Local/SAAIA/logs/client_startup.log`, trace `rag-20260827045725200-c6e32acc`, lignes 33151–33172 observées | 2026-08-27 | Prouve classifier clarification, deux appels, 3 220 tokens, 0 outil et 51,724 s | Log mutable hors repo ; mesures consolidées dans l'analyse |
| PRV-108 | 1 rouverte | Analyse et protocole R.0 | `phase2/exp040-q0-evidence-transport-baseline-analysis.md` et `phase1/exp041-r0-cumulative-facets-router-protocol.md` | 2026-08-27 | Cause, responsabilité de phase, changement causal, scénarios et portes gelés avant code | R.0 non exécutée |
| PRV-109 | Global | Rapport Telegram Q.0 | `phase1/telegram-palier-exp040-reouverture-phase1-20260827-0703.txt` ; sortie notifier ; SHA-256 `713F8392...1B04163` | 2026-08-27 | Prouve 2 639 caractères UTF-8 acceptés et relus identiquement par Telegram | Rapport de réouverture ; R.0 n'était pas encore exécutée |
| PRV-110 | 1 rouverte | TDD, live et analyse R.0 | `phase1/exp041-r0-*`, TRX rouge/vert/budgets/live et `exp041-r0-cumulative-facets-router-analysis.md` | 2026-08-27 | Prouve le vert déterministe mais le rejet live 0/2, les 12 588 tokens, le rollback et l'absence de second run | Harness de capture techniquement vert ; verdict issu des portes et de l'inspection |
| PRV-111 | Global | Rapport Telegram R.0 | `phase1/telegram-palier-exp041-r0-rejete-20260827-0718.txt` ; sortie notifier ; SHA-256 `B7E9573F...5A8433` | 2026-08-27 | Prouve 2 758 caractères UTF-8 acceptés par Telegram après rejet et rollback | Un message consolidé ; prochaine cadence toujours active |
| PRV-112 | 1 rouverte | Recul et protocole S.0 | `phase1/architectural-step-back-after-r0.md` et `phase1/exp042-s0-typed-intake-ledger-protocol.md` | 2026-08-27 | Cartographie les quatre branches, écarte les variantes déjà rejetées et fige 8 scénarios/portes avant code | Analyse statique ; S.0 non exécutée |
| PRV-113 | 1 rouverte | Harness, live et analyse S.0 | `LiveRouterTypedIntakeLedgerBenchmarkTests.cs`, `phase1/exp042-s0-typed-intake-ledger.json`, TRX et analyse | 2026-08-27 | Prouve 1/8 sémantique, 7/8 protocole, latence rouge et knowledgeMode 8/8 | Ordres 2/3 annulés ; résultat composite rejeté |
| PRV-114 | Global | Rapport Telegram S.0 | `phase1/telegram-palier-registre-intake-rejete-20260827-0731.txt` ; sortie notifier ; SHA-256 `20AFBADC...0C08A80` | 2026-08-27 | Prouve 2 119 caractères UTF-8 acceptés avec titre lisible et verdict rejeté | Référence interne secondaire ; cadence active |
| PRV-115 | 1 rouverte | Protocole S.1 | `phase1/exp043-s1-knowledge-mode-isolation-protocol.md` | 2026-08-27 | Isole le seul champ 8/8, fige huit questions, trois ordres et portes coût/stabilité avant code | Non exécuté |
| PRV-116 | 1 rouverte | Harness, live et analyse S.1 | `LiveRouterKnowledgeModeBenchmarkTests.cs`, `phase1/exp043-s1-knowledge-mode-isolation.json`, TRX et analyse | 2026-08-27 | Prouve 7/8, protocole 8/8, latence verte et bascule inventaire | Ordres 2/3 annulés ; piste fermée |
| PRV-117 | Global | Rapport Telegram S.1 | `phase1/telegram-palier-classifieur-binaire-rejete-20260827-0737.txt` ; sortie notifier ; SHA-256 `E8559A70...FFDB06` | 2026-08-27 | Prouve 1 929 caractères UTF-8 acceptés et fermeture explicite des pré-classifieurs 4B | Prochaine décision architecturale annoncée |
| PRV-118 | 1 rouverte | Choix observation-first | `phase1/architectural-choice-observation-first-vs-larger-router-model.md` | 2026-08-27 | Compare preuves 4B/8B, contrat resumeRoute, clarification post-observation, coûts et confondants | Analyse read-only ; T.0 non exécuté |
| PRV-119 | 1 rouverte | Protocole T.0 | `phase1/exp044-t0-source-clarification-after-observation-protocol.md` | 2026-08-27 | Fige changement unique, exclusions, TDD, cinq cas, portes, second run et rollback avant code | Non exécuté |
| PRV-120 | 1 rouverte | TDD, live, analyse et rollback T.0 | `phase1/exp044-t0-*`, JSON/TRX, analyse et budgets post-rollback | 2026-08-27 | Prouve gain Q019, régression ambigu non-grid, scope Normes, coûts et rollback 40/40 | Harness live mécaniquement vert ; verdict produit rejeté |
| PRV-121 | Global | Rapport Telegram T.0 | `phase1/telegram-palier-observation-first-rejete-20260827-0752.txt` ; sortie notifier ; SHA-256 `5F0A09E4...5F3B9C` | 2026-08-27 | Prouve 2 155 caractères UTF-8 acceptés avec gain, rejet et rollback | Prochaine comparaison 8B annoncée |
| PRV-122 | 1 rouverte | Protocole routeur 8B | `phase1/exp045-u0-8b-router-capacity-protocol.md` | 2026-08-27 | Fige modèle/hash, état Q5, templates techniques, huit cas, portes et restauration avant bascule | Préenregistré avant exécution ; contrôles techniques exécutés et clos par PRV-123 |
| PRV-123 | 1 rouverte | Exécution technique et restauration 8B | `phase1/exp045-u0-pre-switch-state.md`, `phase1/exp045-u0-tool-transport-smokes.json`, `phase1/exp045-u0-8b-router-capacity-analysis.md`, logs natif/ChatML/restauration | 2026-08-27 | Prouve hash 8B, double absence de tool call, annulation correcte de la matrice et retour Q5 exact | JSON SHA `B40217E3...78482` ; analyse SHA `C401AEC1...46E0F` ; verdict non sémantique |
| PRV-124 | Global | Rapport Telegram capacité routeur 8B | `phase1/telegram-palier-routeur-8b-inconcluant-20260827-0804.txt` ; sortie notifier ; SHA-256 `52D0C858...F5088E` | 2026-08-27 | Prouve 2 217 caractères UTF-8 acceptés, avec sens clair avant les codes, double échec transport et restauration Q5 | Envoi confirmé ; référence EXP-045/U0 seulement en fin de rapport |
| PRV-125 | 1 rouverte | Recul et protocole frontière directe | `phase1/architectural-step-back-after-8b-transport.md`, `phase1/exp046-v0-direct-capability-boundary-protocol.md` | 2026-08-27 | Met côte à côte Q019/Normes/clarification/inventaire, isole la double traduction et fige six capacités, huit cas, portes et arrêt | Préenregistré avant harness et live |
| PRV-126 | 1 rouverte | Live et analyse frontière directe | `phase1/exp046-v0-direct-capability-boundary.json`, `phase1/exp046-v0-direct-capability-boundary-analysis.md`, trois TRX et harness | 2026-08-27 | Prouve accélération, 4/8 brut puis 3/8 strict, 6/8 protocole strict, zéro backend et arrêt des permutations | JSON SHA `254D9CD1...66708` ; analyse SHA `F954ECBE...C6434` ; live SHA `04F3FB37...4419D` |
| PRV-127 | Global | Rapport Telegram frontière directe | `phase1/telegram-palier-frontiere-directe-rejetee-20260827-0822.txt` ; sortie notifier ; SHA-256 `C57FEF6F...CEC0E7` | 2026-08-27 | Prouve 2 883 caractères UTF-8 acceptés, chiffres stricts 3/8 et 6/8, gain de latence, rejet et prochaine comparaison | Envoi confirmé ; référence EXP-046/V0 uniquement en fin de rapport |
| PRV-128 | 1 rouverte | Comparaison sourcée et protocole récupération | `phase1/architectural-comparison-after-direct-boundary.md`, `phase1/exp047-w0-multiturn-recovery-protocol.md` | 2026-08-27 | Compare boucle agentique, tool-search, structuré et Qwen3.5-9B ; fige replay SHA, cinq cas, feedback, deux tours et portes | Aucun produit ; harness/backend/LLM non encore exécutés |
| PRV-129 | 1 rouverte | Smoke, live et analyse récupération | `phase1/test-results/exp047-w0-smoke-final.trx`, `phase1/exp047-w0-multiturn-recovery.json`, `phase1/test-results/exp047-w0-live.trx`, `phase1/exp047-w0-multiturn-recovery-analysis.md` et harness | 2026-08-27 | Prouve replay exact, 1/5, 1/7 protocole, deux classes réellement exercées, trois refus de contexte, trois lectures et zéro mutation/oracle | JSON SHA `E6A06FB3...2F48AF` ; live SHA `CDF10B06...483C6` ; trois cas explicitement non évalués sémantiquement |
| PRV-130 | Global | Rapport Telegram récupération multi-tour | `phase1/telegram-palier-recuperation-multitour-rejetee-20260827-0855.txt` ; sortie notifier ; SHA-256 `8D004938...4807D9` | 2026-08-27 | Prouve 2 558 caractères UTF-8 acceptés, verdict rejeté, correction d'interprétation et prochaine qualification | Envoi confirmé en une partie ; sens français avant référence EXP-047/W.0 |
| PRV-131 | 1 rouverte | Protocole orchestrateur Qwen3.5-9B | `phase1/exp048-x0-qwen35-9b-direct-router-protocol.md` | 2026-08-27 | Fige dépôt/révision, taille/hash, confondants, profil machine, porte transport, matrice conditionnelle et restauration | Enregistré avant téléchargement/bascule/appel 9B ; aucun produit |
| PRV-132 | 1 rouverte | Intégrité, transport, matrice et restauration Qwen3.5-9B | `phase1/exp048-x0-pre-switch-state.md`, `phase1/exp048-x0-tool-transport-smoke.json`, `phase1/exp048-x0-direct-capability-boundary.json`, TRX, logs/profils et `phase1/exp048-x0-qwen35-9b-direct-capability-analysis.md` | 2026-08-27 | Prouve poids exact, transport 1/1, canonique 2/8, deux timeouts, zéro backend et retour Q5 exact | JSON SHA `89007D18...102482` ; smoke `E2E9C329...AEC3DC` ; TRX `DE5E8714...108C69` ; analyse `3C84EEE6...F8483C` |
| PRV-133 | Global | Rapport Telegram rejet Qwen3.5-9B | `phase1/telegram-palier-qwen35-9b-rejete-20260827-0935.txt` ; sortie notifier ; SHA-256 `2EB0221C...90E3BA` | 2026-08-27 | Prouve 2 724 caractères UTF-8 acceptés, intégrité, transport vert, matrice 2/8, comparaison Q5, restauration et prochaine décision | Envoi confirmé en une partie ; référence EXP-048/X.0 seulement en fin de rapport |
| PRV-134 | 1 rouverte | Comparaison d'enveloppes après 9B | `phase1/architectural-envelope-decision-after-qwen35-9b.md` | 2026-08-27 | Compare Qwen3.5-4B, Phi, FunctionGemma, Granite, matériel et distant avec contraintes machine/confidentialité | Granite seul candidat local suivant ; SHA `64CC06CA...A8D80C` ; distant interdit sans accord ; matériel différé |
| PRV-135 | 1 rouverte | Protocole Granite 4.2-3B | `phase1/exp049-y0-granite42-3b-direct-router-protocol.md` | 2026-08-27 | Fige dépôt officiel, révision/taille/hash, paramètres IBM, profil runtime, transport, matrice, portes et restauration | Enregistré avant téléchargement/bascule/appel ; SHA `20862A69...818F27` ; aucun produit |
| PRV-136 | 1 rouverte | Exécution et analyse Granite 4.2-3B | `phase1/exp049-y0-pre-switch-state.md`, smoke, JSON canonique, TRX, logs/profils, `phase1/exp049-y0-granite42-3b-direct-capability-analysis.md` | 2026-08-27 | Prouve hash officiel, transport 5,102 s, canonique 4/8, paramètres IBM réels, zéro backend et restauration Q5 | JSON SHA `B5FA789A...4AB286` ; smoke `D8FC8052...30723B` ; TRX `ABFC821E...8A2F80` ; analyse `EA84BA25...AD48FE` |
| PRV-137 | Global | Rapport Telegram rejet Granite 4.2-3B | `phase1/telegram-palier-granite42-3b-rejete-20260827-0949.txt` ; sortie notifier ; SHA-256 `0A660AC0...104106` | 2026-08-27 | Prouve 2 900 caractères UTF-8 acceptés, intégrité, paramètres, 4/8, comparaison trois modèles, restauration et choix requis | Envoi confirmé en une partie ; référence EXP-049/Y.0 seulement en fin de rapport |
| PRV-138 | 1 rouverte | Paquet de référence distante hors ligne | `phase1/exp050-z0-remote-reference-preauthorization-protocol.md`, deux JSON séparés, `RemoteReferenceBoundaryPackageTests.cs` et quatre TRX | 2026-08-27 | Prouve 8 requêtes/6 outils, séparation des oracles, deux rouges utiles puis vert 1/1, absence de chemin/URL/secret/résultat et zéro exécution distante/backend | Outbound SHA `33C55081...959FC` ; oracles SHA `D49228E3...236F7` ; dernier TRX SHA `982D1D31...5C27A` |
| PRV-139 | 1 rouverte | Dossier de choix d'enveloppe | `phase1/exp050-z0-envelope-options-quantified.md`, métadonnées officielles IBM/Hugging Face/NVIDIA et inventaire machine | 2026-08-27 | Compare coût informationnel, confidentialité, matériel 24–32 GB et protocole LoRA/QLoRA 1440/240/480 sans prétendre les avoir exécutés | Dossier SHA `29402FA8...59EC` ; recommandation diagnostique réversible, aucune autorisation implicite |
| PRV-140 | Global | Rapport Telegram choix d'enveloppe prêt | `phase1/telegram-palier-choix-enveloppe-pret-20260827-1008.txt` ; sortie notifier ; SHA-256 `3B15BD58...CACD4` | 2026-08-27 | Prouve 2237 caractères UTF-8 acceptés, paquet offline, trois options, recommandation et absence explicite d'autorisation/coût/envoi | Envoi confirmé en une partie ; intitulés français avant EXP-050/Z.0 et DEC-060 |
| PRV-141 | 1 rouverte | Fiche d'autorisation GPT-5.6 Sol | `phase1/exp051-aa0-openai-gpt56-sol-authorization-sheet.md` et manifeste JSON | 2026-08-27 | Fige modèle, API, paramètres, coûts, données, deux régimes de rétention, texte d'accord, portes et interdictions sans appel ni secret | Fiche SHA `091E95AE...DF5A2` ; manifeste SHA `2D6D8B48...55C6B4` ; JSON parse, coût et hash outbound revérifiés |
| PRV-142 | Global | Rapport Telegram autorisation distante précise | `phase1/telegram-palier-reference-distante-autorisation-20260827-1016.txt` ; sortie notifier ; SHA-256 `8652DF7A...65011` | 2026-08-27 | Prouve 2087 caractères UTF-8 acceptés avec modèle, coût, profils de rétention, données divulguées et non-autorisation | Envoi confirmé en une partie ; aucun appel OpenAI associé |
| PRV-143 | Global | Audit d'arrêt contrôlé | `phase1/blocked-awaiting-envelope-authorization-20260827-1020.md`, manifeste EXP-051, santé Q5 et historique EXP-049–051 | 2026-08-27 | Prouve trois continuations du même blocage, état non autorisé, absence d'action sûre restante et conditions exactes de reprise | Blocage de consentement, non panne technique ; Goal réactivable par une décision utilisateur |
| PRV-144 | Global | Rapport Telegram d'arrêt contrôlé | `phase1/telegram-palier-goal-bloque-attente-autorisation-20260827-1020.txt` ; sortie notifier ASCII ; SHA-256 `07712498...64CD4` | 2026-08-27 | Prouve 1857 caractères UTF-8 acceptés, état Q5, absence d'action externe et cinq voies de reprise | Deux titres Unicode sans confirmation tracés ; succès final en une partie avec titre ASCII, total 1974 |
| PRV-145 | 2 / D1 | Analyse causale portée/catégorie | `phase2/d1-scope-diagnostic/d1-scope-correction-analysis.md` et sorties routeur/live associées | 2026-08-27 | Prouve le retrait d'une portée non publiée, la recherche ANSI non confinée et le document correct atteint à 21,892 s | SHA analyse `BEB140E2...E4CB5F` ; scénario complet encore rouge par D3 |
| PRV-146 | 2 / D2 | TDD et analyse adéquation plate adaptative | `phase2/d2-adequacy/d2-adaptive-flat-adequacy-analysis.md`, TRX rouges/verts et six répertoires live | 2026-08-27 | Prouve le checkpoint LLM select/clarify/continue, le writer exhaustif et Q156 fonctionnel 3/3 | SHA analyse `48CD9B83...26F359` ; latence, clic UI et revisionId non approuvés |
| PRV-147 | 2 / D1+D2 | Régression déterministe large | `phase2/phase2-d1-d2-broad-deterministic-regression-r2.trx` | 2026-08-27 | Prouve 200/200 contrats routeur, agent v2 et clarification sur le binaire produit courant | SHA `21D2AFC4...AD9C0` ; ne remplace pas les preuves live |
| PRV-148 | 2 / D2 | Série live Q156 | `phase2/d2-adequacy/scenario-q156-flat-checkpoint-writer/`, `scenario-q156-confirmation-2/`, `scenario-q156-confirmation-3/` | 2026-08-27 | Prouve trois réponses vérifiées avec E1 page 59 et E3 page 22, sans choix arbitraire mono-source | Durées 87 129 / 73 828 / 73 366 ms ; `revisionId: null` |
| PRV-149 | Global | Rapport Telegram D1+D2 | `phase2/telegram-palier-d1-d2-q156-3-sur-3-20260827-1315.txt` ; sortie notifier | 2026-08-27 | Prouve un rapport consolidé avec succès, limites non approuvées et D3 suivant, relu identiquement par Telegram | SHA `5680C0A5...AB815B6` ; corps 2 430 caractères, total 2 538, une partie |
| PRV-150 | 2 / D3 | Protocole, TDD et régression continuité documentaire | `phase2/d3-document-continuity-protocol.md`, cinq TRX D3 et tests `SourceBackedAgentV2Tests.cs` | 2026-08-27 | Prouve rouge 0/1, contrat 3/3 standard et x64, puis régression D1+D2+D3 203/203 | Rouge SHA `3A702A95...A1BC7E9` ; x64 SHA `5E3F7CBE...E28A56A` ; large SHA `AE4B7C14...40B0E7A` |
| PRV-151 | 2 / D3 | Live Normes et canari Cuisine | `phase2/d3-document-continuity/scenario-normes-focused-transition-x64/`, `scenario-normes-003-transition-x64/`, `scenario-q156-canary-x64/` | 2026-08-27 | P4-001 répond sur ANSI ; P4-003 conserve ANSI mais expire ; Q156 répond avec E1/E3 et deux sources vérifiées | 90 675 / 180 015 / 101 235 ms ; timeout, latence et `revisionId: null` non approuvés |
| PRV-152 | 2 | Analyse D3 et approbation de phase | `phase2/d3-document-continuity/d3-document-continuity-analysis.md`, `phase2/phase2-orchestration-qwen3-approval.md` | 2026-08-27 | Fige le contrat `GLOBAL`/EvidenceId, le binaire x64 réellement validé, les limites et le transfert causal vers la phase 3 | D3 approuvée sur sa responsabilité ; phase 2 approuvée ; phase 3 ouverte |
| PRV-153 | Global | Rapport Telegram d'approbation de phase 2 | `phase2/telegram-palier-phase2-d3-approuvee-20260827-1341.txt` ; sortie notifier | 2026-08-27 | Prouve un rapport de 2 624 caractères avec D1–D3, preuves, essai x64 invalide, limites et phase suivante | SHA `0C6A0E85...5C1030E` ; envoi confirmé en une partie, total 2 730 caractères |
| PRV-154 | 3 / A1–A4 | Protocoles, TDD et itérations de mémoire de rendement/terminaison | `phase3-a1-yield-aware-termination-protocol.md`, `phase3-a2-semantic-yield-resolution-protocol.md`, `phase3-a3-persistent-semantic-yield-resolution-protocol.md`, `phase3-a4-bounded-semantic-yield-resolution-protocol.md` et TRX associés | 2026-08-27 | Prouve la progression d'un timeout matériel vers un terminal décidé par Qwen3, borné et auditable | Tous les runs rouges, timeouts et variantes rejetées restent conservés ; aucun n'est requalifié en succès |
| PRV-155 | 3 / A5–A8 | Contrats source-item, evidence mode, mémoire cumulative et sérialisation terminale | `phase3-a5-source-item-contract-protocol.md`, `phase3-a6-semantic-evidence-mode-protocol.md`, `phase3-a7-cumulative-yield-memory-protocol.md`, `phase3-a8-terminal-serialization-budget-protocol.md` et TRX associés | 2026-08-27 | Prouve la séparation des formes de preuve, le transport compact de l'état et une décision terminale structurée | Contrats x64 et canaris live archivés avec leur verdict qualitatif |
| PRV-156 | 3 / A9–A13 | Clarification observée, portée ouverte, politique et matrice de candidats | `phase3-a9-observed-ambiguity-clarification-protocol.md` à `phase3-a13-candidate-match-matrix-protocol.md` et runs Q156/P4 | 2026-08-27 | Prouve que Qwen3 clarifie une ambiguïté réellement observée et ne choisit plus arbitrairement un candidat | Les cas encore faux ou expirés restent `TESTEE_NON_APPROUVEE` dans leurs protocoles |
| PRV-157 | 3 / A14–A16 | Reroutage d'une clarification invalide et routage des formes de preuve | `phase3-a14-invalid-clarification-reroute-protocol.md`, `phase3-a15-a16-evidence-form-routing-protocol.md` et TRX associés | 2026-08-27 | Prouve qu'un élément documentaire alternatif reste une route de recherche et qu'une fausse clarification est refusée | La bonne route P4 restait insuffisante tant que la terminaison n'était pas reliée au rendement |
| PRV-158 | 3 / A17 | Deux verdicts LLM successifs sans rendement déclenchent la seule décision terminale | `phase3-a17-fast-review-yield-memory-protocol.md`, `phase3-a17-adaptive-fast-review-x64-r2.trx`, `phase3-a17-normes-003-final-live-x64.trx` | 2026-08-27 | Prouve 13/13 déterministes et un terminal live en 141 902 ms après deux observations zéro rendement | Sortie A17 non approuvée : explication externe non sourcée ; défaut corrigé en A18 |
| PRV-159 | 3 / A18 | Terminal d'insuffisance mécaniquement fondé et localisé | `phase3-a18-grounded-terminal-response-protocol.md`, `phase3-a18-grounded-terminal-contracts-x64-r2.trx` | 2026-08-27 | Prouve 5/5 contrats et que seul le nombre d'actions, de sources citables et la décision LLM alimentent le message utilisateur/mémoire | La justification libre du LLM reste uniquement dans la trace d'audit |
| PRV-160 | 3 | Régression déterministe finale figée x64 | `phase3-final-source-backed-native-router-regression-x64.trx` | 2026-08-27 | Prouve l'absence de régression sur l'agent source-backed et le routeur natif | 220/220, 0 échec, 0 ignoré ; SHA `C5262670...58FDF1` |
| PRV-161 | 3 | Live final figé P4-NORMES-003 | `phase3-final-normes-003-frozen-live-x64.trx`, `scenario-normes-003-phase3-final-frozen-x64/output/` | 2026-08-27 | Prouve une insuffisance honnête après 3 actions, 2 verdicts zéro rendement et une décision terminale, sans source ni fait inventé | 97 048 ms ; TRX SHA `DF7A562B...963C3` ; JSONL SHA `2A3C5940...024AE` |
| PRV-162 | 3 | Canari final figé Q156 | `phase3-final-q156-frozen-canary-live-x64.trx`, `scenario-q156-phase3-final-frozen-x64/output/` | 2026-08-27 | Prouve une clarification correcte après 1 recherche et 3 candidats, sans activation indue du terminal zéro rendement | 39 281 ms ; TRX SHA `1B7601CD...7E5E0` ; JSONL SHA `6EB7FD9C...67A8B` |
| PRV-163 | 3 | Analyse consolidée et décision d'approbation | `phase3-yield-memory-termination-analysis.md`, `phase3-yield-memory-termination-approval.md` | 2026-08-27 | Fige A1–A18, architecture, inspection humaine, limites non approuvées et responsabilité transférée à la phase 4 | Phase 3 approuvée ; P4 positif, performance, identité/UI et contrat mémoire restent ouverts |
| PRV-164 | 3 | Binaire, services et hygiène finale | DLL WinUI/tests figées, `/ready`, modèle Qwen Q5, `git diff --check` | 2026-08-27 | Prouve le binaire réellement joué, les dépendances prêtes et l'absence d'erreur whitespace | WinUI SHA `9792C589...403CD` ; tests SHA `9D02B853...18774` ; warnings EOL historiques seulement |
| PRV-165 | Global | Rapport Telegram d'approbation de phase 3 | `phase3/telegram-palier-phase3-approuvee-20260827-1858.txt` ; sortie notifier | 2026-08-27 | Prouve un rapport intelligible couvrant défaut initial, architecture conservée, validations, limites et ouverture de phase 4 | SHA `E7404336...07F93` ; envoi complet confirmé : corps 2 673, total 2 779 caractères |
| PRV-166 | 4 / A1–A2 | Protocole gelé et contre-preuves d'identité | `phase4/phase4-a1-evidence-identity-continuity-protocol.md`, A2 client/backend, A4 vérificateur, A6 lanceur | 2026-08-27 | Prouve avant correction les pertes de révision, champs spécialisés, contrôle hash et carte inline | Protocole SHA `34863507...1D3B5` ; rouges client 0/6, backend 0/2, vérificateur 0/2, lanceur 0/2 |
| PRV-167 | 4 / A3–A7 | Propagation canonique et portes mécaniques vertes | A3 client/backend, A5 vérificateur, A7 lanceur et sources produit associées | 2026-08-27 | Prouve les champs explicites, la révision active, le SHA-256 64 hex, l'interdiction de surcharger `chunkId` et l'ouverture exacte | Verts 6/6, 2/2, 8/8 et 8/8 ; hashes référencés dans l'analyse consolidée |
| PRV-168 | 4 / A8–A15 | Régressions transversales, prompt générique et ancre navigation | A10, A11, A12d, A14 et A15 | 2026-08-27 | Prouve transport writer/mémoire/payload/UI, séparation `semanticLabel`/`scopeValue` et ancre affectée au chunk exact seulement | 240/240 transversal ; 2 048/2 048 backend ; 1 305/1 305 client avant live |
| PRV-169 | 4 / A16–A17 | Backend distant et identité live directe | log de déploiement A16, `/ready`, `a17-live-direct-rag-identity.json` | 2026-08-27 | Prouve le code backend réellement déployé et cinq hits réels avec révision active et SHA-256 fort | `/ready` SHA `6F49E33C...180AC` ; sonde SHA `B4CAEA76...6B93E` |
| PRV-170 | 4 / A18–A21 | Diagnostic x64, TDD normalisation et positif Q011 | A18/A18b, A19 rouge, A20 vert, A21 TRX/JSONL | 2026-08-27 | Distingue l'essai obsolète non-x64 du produit, localise `revisionId` perdu par normalisation et prouve E1 complète en live | A20 1/1 ; A21 67 155 ms, TRX `5DD41336...7B55`, JSONL `2A4AFF5F...0180` |
| PRV-171 | 4 / A22–A24 | Matrice live honnête nommée, multisource et clarification | dossiers A22, A23, A24 | 2026-08-27 | Conserve deux échecs fonctionnels sans source inventée et une clarification correcte sans carte ; BUG-063 reste visible | 127 576 / 236 178 / 49 545 ms ; A23 préflight 4 341/4 096 |
| PRV-172 | 4 / A25–A28 | TDD des cartes inline et régression client finale | A25, A26 rouge, A27 vert, A28 complet | 2026-08-27 | Prouve que le payload persisté d'un message est rendu par sa propre carte, après détection de l'absence réelle de cartes | Rouge 0/1 ; vert 1/1 ; final 1 307/1 307, SHA `CA2E1AC9...1C83C` |
| PRV-173 | 4 / A29 | Carte WinUI et clic source réels | `phase4/a29-real-winui-source-card-open-evidence.md` | 2026-08-27 | Prouve carte E1 inline, bouton réel, fichier exact et page réellement ouverte | Edge : `30-recettes-preferees-des-francais.pdf#page=5`, indicateur `5 of 36` ; contrôle ordinateur ensuite libéré |
| PRV-174 | 4→5 | Analyse consolidée et approbation de phase 4 | `phase4/phase4-evidence-identity-analysis.md`, `phase4/phase4-evidence-identity-approval.md` | 2026-08-27 | Ferme la responsabilité d'intégrité sans requalifier Q011 partiel, document nommé, multisource, mémoire Q156 ni performance | Phase 4 approuvée ; phase 5 ouverte avec BUG-063 et BUG-075 à BUG-077 |
| PRV-175 | Global | Rapport Telegram d'approbation de phase 4 | `phase4/telegram-palier-phase4-integrite-approuvee-20260827-2112.txt` ; accusé notifier | 2026-08-27 | Prouve un rapport complet sur pertes d'identité, correctifs, test UI réel, clic exact et défauts transférés | Corps 2 820, total 2 926 caractères ; SHA `41295285...16353` ; envoi confirmé en une partie |
| PRV-176 | 5 / A269 | Protocole TDD R+V préenregistré | `phase5/PROTOCOLE-A269-A275-TDD-RESOLUTION-DOCUMENT-NOMME-RV.md` | 2026-08-28 | Fige statuts catalogue, responsabilités Qwen/code, scope, quarantaine, identité aval, 36 cas et cinq portes avant code | 455 lignes ; SHA `1F577E52...E0C980B` ; live interdit |
| PRV-177 | 5 / A270–A273 | Rouge causal et verts ciblés R+V | A270 rouge valide, A271 intermédiaire, A272 scope/quarantaine, A273 vertical | 2026-08-28 | Prouve l'absence initiale du résolveur, puis résolution exacte, quarantaine, décision Qwen typée et garde downstream | 1/5 puis 9/13, 13/13 et 30/30 ; hashes dans le manifeste A269–A275 |
| PRV-178 | 5 / A274–A275 | Régressions finales de la résolution nommée | `phase5/test-results/A275-affected-regressions-final.trx`, `A275-full-client-final.trx` | 2026-08-28 | Prouve architecture, budgets, routeur, mémoire, clarification, verifier et client complet sur la forme modulaire finale | 405/405 SHA `2BCB1B21...83A5A0` ; 1 419/1 419 SHA `4847222B...3C610` |
| PRV-179 | 5 / A275 | Manifeste et rapport d'approbation déterministe | `phase5/MANIFEST-A269-A275-RESOLUTION-DOCUMENT-NOMME-RV.md`, `phase5/RAPPORT-PALIER-2026-08-28-PHASE5-A269-A275-RESOLUTION-DOCUMENT-NOMME-RV.md` | 2026-08-28 | Liste 29 fichiers, raisons, lignes, hashes, itérations rejetées, portes, limites et prochaine action | Manifest SHA `F7429F21...FBD783` ; rapport SHA `9812A511...C9517B` |
| PRV-180 | Global | Rapport Telegram A269–A275 | `phase5/telegram-palier-a269-a275-resolution-document-nomme-rv-20260828.txt` ; accusé notifier | 2026-08-28 | Rend visible la correction déterministe, les 1 419 tests, les limites live et le prochain protocole | Corps 2 230, total 2 336 ; SHA `1B4D5531...5312653` ; envoi confirmé en une partie |
| PRV-181 | 5 / A276 | Protocole live unique Q023 après R+V | `phase5/PROTOCOLE-A276-A279-REPLAY-LIVE-UNIQUE-Q023-APRES-RV.md` | 2026-08-28 | Fige produit, banque, préflight, un seul run, suites Qwen admissibles, sécurité, fonction, provenance, performance et règle d'arrêt avant modèle | 254 lignes ; SHA `844B1F22...F9D5DD` ; aucun live encore exécuté |
| PRV-182 | 5 / A277 | Préflight read-only Q023 post-R+V | `phase5/A277-PREFLIGHT-REPLAY-LIVE-Q023-APRES-RV.md` | 2026-08-28 | Confirme produit figé, aucun processus/env contaminant, backend prêt, Qwen Q5 exact et cible toujours absente du catalogue actuel | 77 lignes ; SHA `80D30D6D...833CE5` ; A278 autorisé, aucun modèle démarré |
| PRV-183 | 5 / A278 | Replay live unique Q023 post-R+V | `phase5/a278-live-documentation-technique-q023-after-rv/` : TRX, JSON, JSONL, TSV | 2026-08-28 | Prouve NotFound complet 35 ms, quarantaine, décision Qwen clarification, zéro outil/preuve/source/attribution et processus arrêtés | TRX 1/1 SHA `104A641D...60A16` ; JSONL SHA `5AB0D3D6...2A076` ; 60 827/76 305 ms |
| PRV-184 | 5 / A279 | Audit et rapport Q023 post-R+V | `phase5/RAPPORT-PALIER-2026-08-28-PHASE5-A276-A279-Q023-RV-LIVE-PARTIEL.md`, protocole finalisé | 2026-08-28 | Sépare passage harness, sécurité, fonction et performance ; ferme BUG-118 sans masquer la sur-clarification BUG-117 | Rapport SHA `A87A0459...1E9C9` ; protocole final SHA `EA672725...25C750` |
| PRV-185 | Global | Rapport Telegram A276–A279 | `phase5/telegram-palier-a276-a279-q023-rv-live-partiel-20260828.txt` ; accusé notifier | 2026-08-28 | Rend visibles fermeture BUG-118, rejet fonction/performance, absence de rerun et prochaine comparaison | Corps 2 085, total 2 191 ; SHA `68CB4563...1CFD0A` ; envoi confirmé en une partie |
| PRV-186 | 5 / A280 | Comparaison et préenregistrement du portfolio post-résolution | `phase5/PROTOCOLE-A280-A285-PORTFOLIO-OUTILS-TRANSITION-DOCUMENT-NOMME.md` | 2026-08-28 | Compare contrôle, prompt, règle déterministe, juge et portfolio ; retient un appel Qwen avec outils compatibles par statut sans coder le choix | 359 lignes ; SHA `69C94AE1...5B76D7EF` ; aucun code produit/test modifié ; A281 autorisé |
| PRV-187 | 5 / A281–A282 | Fixtures et rouge causal du portfolio | `phase5/A281-A282-PREUVE-ROUGE-PORTFOLIO-TRANSITIONS-DOCUMENT-NOMME.md`, TRX rouge | 2026-08-29 | Prouve que l'ancien outil monolithique est la cause exacte avant modification produit | Compilation verte ; 8/23, 15 échecs ; TRX SHA `40D59932...4F8E09` ; preuve SHA `A8C1BE71...6A415A` |
| PRV-188 | 5 / A283–A285 | Portfolio produit, verts, manifeste et rapport | `phase5/MANIFEST-A280-A285-PORTFOLIO-TRANSITIONS-DOCUMENT-NOMME.md`, `phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A280-A285-PORTFOLIO-TRANSITIONS-DOCUMENT-NOMME.md`, TRX finaux | 2026-08-29 | Prouve transitions par statut, `source_identity`, ambiguïté exacte, retry unique, reprise canonique, budgets et non-régression | 24/24 SHA `91C60D4A...2830D`, 518/518 SHA `6F3C213A...26E19`, client 1 427/1 427 SHA `5A07CA31...B855B`; manifeste `4142DAED...731C60`, rapport `D4129AA3...CDD7A5` |
| PRV-189 | Global | Rapport Telegram A280–A285 | `phase5/telegram-palier-a280-a285-portfolio-transitions-20260829.txt` ; accusé notifier | 2026-08-29 | Rend visibles architecture, TDD, budgets, approbation déterministe, BUG-117 ouvert et replay unique suivant | Corps 2 668, total 2 774 ; SHA `B8838010...E79107` ; envoi confirmé en une partie |
| PRV-190 | 5 / A286 | Protocole du replay Q023 unique après portfolio | `phase5/PROTOCOLE-A286-A289-REPLAY-LIVE-UNIQUE-Q023-APRES-PORTFOLIO.md` | 2026-08-29 | Fige produit, préflight, un run, trois transitions admissibles, critères contrat/fonction/sécurité/performance et règle d'arrêt | 285 lignes ; SHA `F6838A95...BC232A` ; seul A287 read-only autorisé, aucune inférence encore |
| PRV-191 | 5 / A287 | Préflight read-only Q023 post-portfolio | `phase5/A287-PREFLIGHT-REPLAY-LIVE-Q023-APRES-PORTFOLIO.md` | 2026-08-29 | Confirme produit figé, backend prêt, cible path/name/stem absente, runtime Q5 et zéro contamination avant modèle | 96 lignes ; SHA `7F10F79E...77AA65` ; dossier A288 créé vide ; live autorisé |
| PRV-192 | 5 / A288 | Replay live unique Q023 post-portfolio | `phase5/a288-live-documentation-technique-q023-after-portfolio/` : TRX, JSON, JSONL, TSV | 2026-08-29 | Prouve NotFound complet, quarantaine, choix Qwen insuffisance, zéro retrieval/preuve/source et terminal honnête | TRX 1/1 62,161 s SHA `DE3E4C5C...A23E5` ; JSONL SHA `03B310FC...54ECC` ; pipeline/harness 47 933/61 952 ms |
| PRV-193 | 5 / A289 | Audit et rapport Q023 post-portfolio | `phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A286-A289-Q023-PORTFOLIO-LIVE.md` | 2026-08-29 | Sépare contrat, fonction absent, sécurité, provenance, wording et performance ; compare causalement A278 | 242 lignes ; SHA `2A3752AF...0E2B6` ; BUG-117 fermé, BUG-119 ouvert, performance rejetée |
| PRV-194 | Global | Rapport Telegram A286–A289 | `phase5/telegram-palier-a286-a289-q023-portfolio-live-20260829.txt` ; accusé notifier | 2026-08-29 | Rend visibles fermeture BUG-117, gains tokens/temps, rejet performance, réserve wording et canari positif suivant | Corps 2 373, total 2 479 ; SHA `217523AB...063BA4` ; envoi confirmé en une partie |
| PRV-195 | 5 / A290 | Sélection et protocole du canari positif nommé présent | `phase5/PROTOCOLE-A290-A293-CANARI-POSITIF-DOCUMENT-NOMME-PRESENT.md` | 2026-08-29 | Compare Q106–Q110, retient le seul document actuel exact, fige identité/révision/hash/pages/chunks et sépare contrat, fonction, provenance, mémoire, UI et performance avant modèle | Q106 : `docId 9fb65de2...8308`, révision `a2d922d5...0197`, hash `909173f3...18fcf`, 2 pages/9 chunks ; protocole 467 lignes SHA `99D33427...02B58` |
| PRV-196 | 5 / A291 | Préflight read-only du canari positif Q106 | `phase5/A291-PREFLIGHT-CANARI-POSITIF-Q106-DOCUMENT-NOMME.md` | 2026-08-29 | Revalide les faits via `/documents`, détail et contexte ; reclassé après A292 car il n'exerçait pas le port prioritaire exact du produit | 115 lignes SHA `04DCAE9D...314E` ; faits d'identité valides, mais porte produit insuffisante ; dossier A292 créé vide |
| PRV-197 | 5 / A292 | Replay live unique Q106 positif | `phase5/a292-live-documentation-technique-q106-positive-named-document/` : TRX, JSON, JSONL, TSV | 2026-08-29 | Révèle un faux `NotFound` complet avant retrieval malgré le document actuel exact ; Qwen choisit ensuite une insuffisance sûre | TRX 1/1 61,176 s SHA `267A1E12...B1705` ; JSONL `F1D4D32C...F0EB6` ; pipeline/harness 45 698/60 960 ms ; zéro source |
| PRV-198 | 5 / A293 | Audit causal et rapport Q106 positif rejeté | `phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A290-A293-Q106-POSITIF-REJETE.md` | 2026-08-29 | Compare avant/pendant/après, sépare Qwen, résolution, sécurité, provenance, mémoire, UI et performance, et ouvre BUG-120 sans cause inventée | 374 lignes SHA `F679504D...428DE` ; après run `/documents`, `/catalog/documents`, ApiClient et 20/20 sondes retrouvent le même doc |
| PRV-199 | Global | Rapport Telegram A290–A293 | `phase5/telegram-palier-a290-a293-q106-faux-notfound-20260829.txt` ; accusé notifier | 2026-08-29 | Rend visibles le faux `NotFound`, la sûreté conservée, les verdicts rejetés, la cause non isolée et l'interdiction de rerun | Corps 2 930, total 3 036 caractères ; SHA `C1346811...B4ECD4` ; envoi confirmé en une partie |
| PRV-200 | 5 / A294 | Protocole de consensus des observations catalogue | `phase5/PROTOCOLE-A294-A299-CONSENSUS-OBSERVATIONS-CATALOGUE-DOCUMENT-NOMME.md` | 2026-08-29 | Compare contrôle, backend seul, relecture, union, consensus et vérification ; retient l'accord exact F1+F2 et `Inconclusive` sur tout désaccord | 310 lignes SHA `82A53B9A...83750` ; table de vérité, 15 cas TDD, portes A295–A299 ; aucun live autorisé |
| PRV-201 | 5 / A295 | Rouge causal du consensus catalogue | `phase5/A295-PREUVE-ROUGE-CONSENSUS-CATALOGUE-DOCUMENT-NOMME.md`, `phase5/test-results/A295-catalog-consensus-red.trx` | 2026-08-29 | Prouve sur vrai ApiClient/adaptateur/résolveur que F1 seule rend faux positif/négatif et n'observe jamais F2 | Compilation verte, 0/4 ; TRX SHA `C2EEC375...2D47`, preuve 70 lignes SHA `17D59648...AC0B` |
| PRV-202 | 5 / A296 | Implémentation et matrice du consensus exact | Adaptateur, résolveur et trois fichiers de tests consensus | 2026-08-29 | Observe F1+F2, compare les ensembles exacts, conserve les métadonnées F1 après accord et rend une divergence typée sans union/fuzzy | 4/4 puis 16/16 ; TRX finaux SHA `E888D138...CBEF` et `11048732...B141` ; première compilation invalide conservée dans le rapport |
| PRV-203 | 5 / A297–A298 | Intégration runner, régressions, suite complète et mesure réelle | TRX A297/A298, `phase5/a298-measure-catalog-consensus.ps1`, transcript A298 | 2026-08-29 | Prouve retry Qwen unique, reprise bornée au bon docId, arrêt sur désaccord persistant, non-régression et coût backend sans modèle | 18/18, 120/120, 234/234, client 1 445/1 445 ; Q106 11/11, médiane 69 ms ; full TRX SHA `69ECC62D...07D6` |
| PRV-204 | 5 / A299 | Rapport et verdict du consensus catalogue | `phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A294-A299-CONSENSUS-CATALOGUE.md`, protocole clôturé | 2026-08-29 | Sépare correction déterministe, absence de preuve live, performance catalogue, limites UI/provenance et prochaine porte | Rapport 380 lignes SHA `FB152804...A712` ; protocole final 338 lignes SHA `75B18C28...009F` ; BUG-120 corrigé déterministement mais ouvert live |
| PRV-205 | Global | Rapport Telegram A294–A299 | `phase5/telegram-palier-a294-a299-consensus-catalogue-20260829.txt` ; réponse Telegram HTTP 200 | 2026-08-29 | Rend visibles architecture F1+F2, responsabilité Qwen, rouge/verts, 1 445 tests, mesure 69 ms, limites live et prochaine porte | Une partie, corps 2 678 caractères, SHA `3673EF45...477C8` ; envoi confirmé |
| PRV-206 | 5 / A300 | Protocole du replay positif unique Q106 post-consensus | `phase5/PROTOCOLE-A300-A303-REPLAY-LIVE-UNIQUE-Q106-APRES-CONSENSUS-CATALOGUE.md` | 2026-08-29 | Fige vrai résolveur F1+F2, identité/révision/hash/chunks, DLL, banque, runtime, un run et verdicts séparés avant modèle | 507 lignes, SHA `798E86C7...C6E6F` ; dossier A302 absent ; seul A301 read-only autorisé |
| PRV-207 | 5 / A301–A303 | Préflight stoppé et diagnostic de l'identité de révision | `phase5/a301-preflight-q106-consensus.ps1`, diagnostic F1/F2, rapport A300–A303, protocole clôturé | 2026-08-29 | Prouve consensus exact et document stable, mais isole le digest de fraîcheur résumé 32 hex injecté comme hash de révision et son rejet aval certain | Résolution 118–125 ms ; contexte révision `a2d922d5...`/SHA `909173f3...`; rapport 342 lignes SHA `A2FF4123...7F86` ; protocole final SHA `67840B80...44E9`; A302 non exécuté |
| PRV-208 | Global | Rapport Telegram A300–A303 | `phase5/telegram-palier-a300-a303-q106-stoppe-avant-modele-20260829.txt` ; réponse Telegram HTTP 200 | 2026-08-29 | Rend visibles succès existence, échec provenance candidat, absence totale de modèle/run et comparaison suivante | Une partie, corps 2 506 caractères, SHA `83C7A2B6...2D1B` ; envoi confirmé |
| PRV-209 | 5 / A304 | Comparaison et protocole de l'identité canonique post-consensus | `phase5/PROTOCOLE-A304-A309-HYDRATATION-IDENTITE-CANONIQUE-DOCUMENT-NOMME.md` | 2026-08-29 | Compare contrôle, sanitisation, hydratation, backend et vérification tardive ; retient sanitisation + hydratation unique avant contenu | Initial 362 lignes SHA `437BA221...D642E`, final 460 lignes SHA `E593D134...A4A374` ; aucun modèle/live |
| PRV-210 | 5 / A305 | Rouge causal de l'hydratation d'identité | Test canonique, `phase5/A305-PREUVE-ROUGE-HYDRATATION-IDENTITE-CANONIQUE.md`, TRX rouge | 2026-08-29 | Prouve que le produit A303 n'appelle pas le contexte, conserve hash32/révision vide et rend les dérives actives `Resolved` | Compilation verte ; 5/18, 13 échecs ; TRX SHA `A5E46854...AA9986`, preuve SHA `7606D6A7...326B82` |
| PRV-211 | 5 / A306–A307 | Produit, régressions et mesure réelle sans modèle | Port/adaptateur/résolveur/factory, quatre tests, TRX, `a307-measure-canonical-identity-hydration.ps1`, mesure | 2026-08-29 | Sanitise F1, hydrate une fois l'exact unique, exige révision+SHA256, ignore le texte, garde les états non uniques sans contexte | 18/18, 48/48, 355/355, 337/337, client 1 463/1 463 ; hydratation 11/11 médiane 15 ms ; résolution complète 106 ms |
| PRV-212 | 5 / A308 | Préflight réel Q106 sans modèle | `phase5/A308-PREFLIGHT-Q106-IDENTITE-CANONIQUE-SANS-MODELE.md`, script A301 inchangé | 2026-08-29 | Rejoue exactement la porte qui avait stoppé A301 et confirme l'identité active complète et les neuf chunks avant toute inférence | `Resolved`, exact 1, 106 ms, identity gate et préflight PASS ; artefact 141 lignes SHA `7B3061EC...0A988D`; dossier A302 absent |
| PRV-213 | 5 / A309 | Rapport et verdict identité canonique | `phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A304-A309-IDENTITE-CANONIQUE.md` | 2026-08-29 | Sépare fermeture mécanique BUG-121, préflight backend, axes live non testés, passages intermédiaires et prochaine responsabilité | 393 lignes SHA `855C69D4...A878579` ; phase 5 maintenue ouverte ; replay Q106 à préenregistrer |
| PRV-214 | Global | Rapport Telegram A304–A309 | `phase5/telegram-palier-a304-a309-identite-canonique-20260829.txt` ; accusé notifier | 2026-08-29 | Rend visibles cause, architecture, rouge/verts, 1 463 tests, coût, préflight, limites et prochaine porte | Une partie, corps 2 882 caractères, total 2 964, SHA `D8053528...BD2184` ; envoi confirmé |
| PRV-215 | 5 / A310 | Protocole du replay live Q106 après identité canonique | `phase5/PROTOCOLE-A310-A313-REPLAY-LIVE-UNIQUE-Q106-APRES-IDENTITE-CANONIQUE.md` | 2026-08-29 | Fige un seul run, les hashes, le préflight positif obligatoire, les verdicts séparés et l'interdiction de patch/rerun | Initial 511 lignes SHA `B82C3A31...4EA14B`, final 601 lignes SHA `95E0318D...E385FC8` |
| PRV-216 | 5 / A311 | Préflight réel Q106 immédiatement avant modèle | `phase5/A311-PREFLIGHT-Q106-APRES-IDENTITE-CANONIQUE.md` | 2026-08-29 | Confirme banque unique, runtime, backend et vrai résolveur avec identité active et contenu disponible | `Resolved`, complet, exact 1, 172 ms, révision/SHA-256 conformes, deux pages/neuf chunks ; 166 lignes SHA `B181748A...306B` |
| PRV-217 | 5 / A312 | Replay live unique Q106 et artefacts bruts | `phase5/a312-live-documentation-technique-q106-positive-after-canonical-identity/` | 2026-08-29 | Prouve sans rerun la récidive du faux négatif malgré le préflight positif et permet la comparaison exacte avec A292 | 1/1 harnais ; `NotFound`, zéro outil/source, réponse identique SHA `8CBA4F46...E54B6CC`, pipeline 45 872 ms ; TRX SHA `41337556...03166`, JSONL SHA `3C739B47...0AE92` |
| PRV-218 | 5 / A313 | Audit causal et rapport du faux `NotFound` corrélé | `phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A310-A313-Q106-FAUX-NOTFOUND-CORRELE.md` | 2026-08-29 | Sépare sécurité et fonction, compare A292, démontre la dépendance commune F1/F2 sans inventer la cause serveur et rouvre BUG-120 | 491 lignes SHA `28B09C60...3A25C6` ; aucun nouveau live autorisé avant A314 |
| PRV-219 | Global | Rapport Telegram A310–A313 | `phase5/telegram-palier-a310-a313-q106-faux-notfound-correle-20260829.txt` ; accusé notifier | 2026-08-29 | Rend visibles préflight positif, replay négatif, corrélation F1/F2, bugs, ressources et prochaine enquête | Une partie, corps 3 524 caractères, total 3 606, SHA `ED137CBF...E2B540` ; envoi confirmé |
| PRV-220 | 5 / A314 | Protocole causal et inventaire local préserveur | `phase5/PROTOCOLE-A314-A318-DIAGNOSTIC-SERVEUR-ET-PREUVE-NEGATIVE-DOCUMENT-NOMME.md`, `phase5/A314-INVENTAIRE-LOCAL-ET-HYPOTHESES-FALSIFIABLES-PREUVE-NEGATIVE.md` | 2026-08-29 | Fige avant serveur les hypothèses, fenêtres, interdictions de mutation/modèle et mécanismes capables de rendre F1/F2 vides | Protocole 576 lignes SHA `8E434B63...12212F6` ; inventaire clôturé 268 lignes SHA `1BFC20C4...C521B996` ; hypothèse watcher conservée puis explicitement falsifiée par A315 |
| PRV-221 | 5 / A315 | Collecte serveur strictement read-only | `phase5/a315-server-readonly-diagnostic/00-MANIFESTE-A315.md` à `12-VERDICT-CAUSAL-A315.md` | 2026-08-29 | Prouve stabilité des conteneurs, scanner, document, révision, jobs/runs/audits et absence de transition watcher sur la cible | 13 artefacts ; document `indexed` versions 8/8, timestamps juillet, aucun job/run/restart ; transaction PostgreSQL read-only, aucun secret ni mutation |
| PRV-222 | 5 / A315 | Preuve binaire de la query réellement extraite | `phase5/a315-server-readonly-diagnostic/10-PREUVE-BINAIRE-EXTRACTEUR.txt` | 2026-08-29 | Invoque sans compilation les hooks du DLL A307 avec la question Q106 exacte et révèle la frontière lexicale erronée | DLL SHA `32A68729...DC2785` ; obtenu 52 caractères `Retrouve exactement la fiche ...pdf`, attendu 23 caractères ; SHA artefact `534EA771...A794820` après correction de la longueur affichée |
| PRV-223 | 5 / A315 | Matrice discriminante HTTP C4 | `phase5/a315-server-readonly-diagnostic/11-MATRICE-REQUETES-HTTP.tsv`, `12-VERDICT-CAUSAL-A315.md` | 2026-08-29 | Reproduit sans modèle les deux queries du résolveur et les compare au vrai nom/stem sur les deux surfaces | phrase/stem erronés F1=0/F2=0 ; nom/stem corrects F1=1/F2=1 et docId cible ; matrice SHA `33E5867B...491B65CA`, verdict SHA `2B32285C...874748BF` |
| PRV-224 | 5 / A315 | Rapport causal et notification Telegram | `phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A314-A315-CAUSE-RACINE-Q106.md`, `phase5/telegram-palier-a314-a315-cause-racine-q106-20260829.txt` | 2026-08-29 | Corrige la chronologie, retire l'attribution serveur erronée, ouvre BUG122 et rend immédiatement visible le jalon | Rapport 168 lignes SHA `9061414F...1F5EBB94` après correction longueur 23 ; Telegram confirmé en une partie, corps 2 227, total 2 332, SHA `4CCCB27D...16B443E5` |
| PRV-225 | 5 / A316 | Comparaison X0–X4 et T0–T3 | `phase5/A316-COMPARAISON-CONTRATS-EXTRACTION-LITTERALE-ET-TRACE.md` | 2026-08-29 | Retient une frontière syntaxique conservatrice et la référence réelle dans la trace, rejette fuzzy/suffixes pilotés par le corpus | 170 lignes, 7 833 octets, SHA `E51ABD8F...29010077` ; patch interdit avant rouge |
| PRV-226 | 5 / A317 | Rouge, patch générique, trace et verts ciblés | `phase5/A317-PREUVE-ROUGE-EXTRACTION-REFERENCE-FICHIER.md`, `phase5/RAPPORT-A317-TDD-EXTRACTION-LITTERALE-DOCUMENT-NOMME.md`, TRX A317 | 2026-08-29 | Reproduit BUG122 avant produit, corrige seulement la frontière certaine, couvre six langues/contre-exemples et prouve la trace | Rouge 0/1 SHA `2FB1AE49...E48FD30` ; verts 12/12 et 151/151 ; rapport 161 lignes SHA `DC77383A...5271B244` |
| PRV-227 | 5 / A318 | Régression complète et mesure binaire | `phase5/a318-regressions-extraction-litterale/A318-full-client-after-literal-file-extraction.trx`, `phase5/A318-MESURE-BINAIRE-EXTRACTION-SANS-MODELE.txt` | 2026-08-29 | Prouve non-régression, stabilité de la sortie et coût mécanique borné sans modèle | 1 473/1 473, 27 s, TRX SHA `850933BE...31DF639E` ; 10 000/10 000, 97,132 µs réflexion incluse, mesure SHA `6D6D13EA...B6F96E1B` |
| PRV-228 | 5 / A318 | Protocole clôturé et rapport de palier | `phase5/PROTOCOLE-A314-A318-DIAGNOSTIC-SERVEUR-ET-PREUVE-NEGATIVE-DOCUMENT-NOMME.md`, `phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A314-A318-BUG122-EXTRACTION-LITTERALE.md` | 2026-08-29 | Sépare cause serveur rejetée, cause intake C4, déterministe approuvé et axes live encore ouverts | Protocole 641 lignes SHA `860BFF6A...2C4732B6` ; rapport 233 lignes SHA `85EEA212...9658907B` |
| PRV-229 | Global | Rapport Telegram A314–A318 | `phase5/telegram-palier-a314-a318-bug122-ferme-deterministe-20260829.txt` ; accusé notifier | 2026-08-29 | Rend visibles cause, serveur, architecture, TDD, régression, mesure, bugs et prochaine porte live | Une partie, corps 2 761, total 2 866, SHA `F6A56757...35532830` ; envoi confirmé |

---

## 21. Journal des modifications produit

À renseigner à chaque groupe cohérent, avant le test correspondant.

| ID | Date | Phase | Fichiers | Changement | Risque | Test associé | Statut |
|---|---|---|---|---|---|---|---|
| MOD-000 | 2026-08-26 | Planification | Ce document | Création du registre dynamique et liaison au Goal Codex actif | Aucun risque produit | Relecture structurelle et contrôle des espaces | Vérifié |
| MOD-001 | 2026-08-26 | 0 | Registre et `artifacts/goal-rag-end-to-end-20260826-233646/` | Documentation de la baseline et de son approbation ; aucun fichier produit touché | Aucun risque comportemental | Build, tests, probes et inspection | Approuvé |
| MOD-002 | 2026-08-27 | 1 | `client/SAAIA.Client.ToolAgent.Tests/LiveSourceBackedIntakeFirstActionBenchmarkTests.cs` | Ajout d'un benchmark live opt-in, read-only, qui stoppe avant le premier outil et sépare résultat mécanique et approbation | Risque limité au projet de tests ; aucune exécution documentaire réelle | Build Debug/x64 0 warning/0 erreur ; smoke désactivé ; EXP-003/P1 | Vérifié, non approuvé comme solution produit |
| MOD-003 | 2026-08-27 | 1 | Routeur natif, planning/startup du runner, revue des rôles, tests routeur/runner/live | C : action documentaire choisie par le routeur conservée ; trois revues spéculatives sautées avant observation ; faux rejet lexical et fallback clarification corrigés | Changement d'ordre d'appel ; le type provisoire reste présent dans la mission | 26/26 ciblés ; 268/268 régression ; EXP-005/P1 ×2 | Testé, non approuvé |
| MOD-004 | 2026-08-27 | 1 | Prompt/schema routeur et benchmark live | C.1 : règle contenu-versus-axes renforcée ; métadonnées d'expérience dynamiques | Prompt plus long de 93 caractères ; comportement petit modèle incertain | Build 0/0 ; 173/173 ciblés ; EXP-006/P1 | Rejeté en live ; métadonnées benchmark conservées |
| MOD-005 | 2026-08-27 | 1 | `RouterPlan.cs`, schéma/parser routeur, tests | D : suppression de query/search du contrat grille et statut explicite du type provisoire | Le modèle peut choisir un autre contrat concurrent | 174/174 ciblés ; 269/269 élargis ; EXP-007/P1 | Rejeté en live, modifications à réutiliser dans E |
| MOD-006 | 2026-08-27 | 1 | `ToolAgentOrchestrator.RouterCore.cs`, prompt/protocole routeur, tests | E : classification LLM compacte de la famille puis exposition d'un seul contrat spécialisé | Un appel supplémentaire ; erreur du classifieur potentiellement imposée | 181/181 ciblés ; 276/276 élargis ; EXP-008 | Partiellement retenu |
| MOD-007 | 2026-08-27 | 1 | Prompt routeur spécialisé et tests | E.1 : second prompt limité aux règles de la famille sélectionnée | Classifieur universel insuffisant sur les branches non-grid | 185/185 ; 280/280 ; matrices repas/génériques | Spécialisation retenue seulement pour grid |
| MOD-008 | 2026-08-27 | 1 | Routeur, parseur et tests | F/F.1 : ancrage mécanique des axes, collision catégorie/document neutralisée, pool grid borné, essais de délégation | Les classifieurs grid/defer se trompent live | 186/186 puis 185/185 ; matrices P1 | Garde-fous retenus, stratégies de labels rejetées |
| MOD-009 | 2026-08-27 | 1 | `ToolAgentOrchestrator.RouterCore.cs`, protocole routeur et tests | G : labels quatre voies, fast-path seulement pour grid, routeur général pour les autres ; réparation des axes inventés vers clarification | Deux appels pour grid ; clarification ambiguë encore lente | 187/187 ; 282/282 ; P1–P5 + génériques | Approuvé comme architecture de base |
| MOD-010 | 2026-08-27 | 1 | `SourceBackedAgentSemanticPlanParsing.cs`, `SourceBackedAgentV2Tests.cs` | Suppression du veto lexical type/colonne ; ajout de la régression Maintenance exacte | Un type sémantiquement mauvais ne sera plus rejeté par ce raccourci lexical | Rouge/vert ; 188/188 ; 283/283 ; live Maintenance | Approuvé ; sémantique restituée au LLM |
| MOD-011 | 2026-08-27 | 1 | `ToolAgentOrchestrator.NativeRouterPromptAndProtocol.cs`, `NativeRouterPromptBudgetTests.cs` | G.1 : `inventoryMode=representative` sur l'action grille/cartes, navigation inchangée | Échantillon interne encore bruité ; audit LLM requis | Rouge/vert ; 189/189 ; 284/284 ; P1 intégré et matrice | Approuvé phase 1 |
| MOD-012 | 2026-08-27 | 1 | `LiveSourceBackedIntakeFirstActionBenchmarkTests.cs` | Mode opt-in test-only exécutant la première carte, normalisant l'EvidenceBundle puis stoppant le runner | Supporte uniquement content_cards ; ne doit pas être confondu avec un full live | Build 0/0 ; smoke désactivé ; EXP-017 intégré | Vérifié, instrumentation uniquement |
| MOD-013 | 2026-08-27 | 2 | `LiveCandidateAuditCompatibilityBenchmarkTests.cs` | Ajout d'une baseline opt-in, test-only, gelant 20 IDs et instrumentant chaque appel identité/compatibilité ; refus immédiat si dérive corpus | Aucun changement produit ; l'ablation labels=roles n'est pas une validation du full runtime | Build/smoke 1/1 ; EXP-019 live 1/1 en 56 s | Vérifié comme instrumentation ; résultat sémantique non approuvé |
| MOD-014 | 2026-08-27 | 2 | `LiveCandidateAuditCompatibilityBenchmarkTests.cs` | Variante B.0 test-only appelant le vrai contrat de frontières après matérialisation de l'observation et traçant son coût | Aucun chemin produit ; les rôles produits ne sont persistés nulle part | Premier smoke rouge compilation, correction test, smoke vert, EXP-020 live | Instrumentation vérifiée ; variante rejetée |
| MOD-015 | 2026-08-27 | 2 | `LiveCandidateAuditCompatibilityBenchmarkTests.cs` | Variante C.0 test-only appelant la vraie définition atomique après observation et l'injectant dans l'audit existant | Aucun chemin produit ; type/règle non persistés | Smoke 1/1 ; EXP-021 live 1/1 | Instrumentation vérifiée ; solution complète rejetée |
| MOD-016 | 2026-08-27 | 2 | `LiveCandidateAuditCompatibilityBenchmarkTests.cs` | Variante D.0 test-only injectant le contexte de mission, gel optionnel de la définition C.0 et rotations candidates | Le premier run a révélé une contamination de comparaison, ensuite isolée explicitement | Smokes verts ; rotations 0/7/13 live | Instrumentation vérifiée ; variante rejetée |
| MOD-017 | 2026-08-27 | 2 | `LiveCandidateAuditCompatibilityBenchmarkTests.cs` | Variante E.0 test-only : six appels single-candidate, labels nommés, rotations des rôles | Réintroduirait trop d'appels si intégrée sans preuve ; aucun code produit | Premier smoke compilation rouge puis vert ; trois lives | Instrumentation vérifiée ; variante rejetée |
| MOD-018 | 2026-08-27 | 2 | `LiveCandidateAuditCompatibilityBenchmarkTests.cs` | Variante F.0 test-only : matrice de 24 couples booléens et rotations d'exécution | Coût élevé par conception diagnostique ; aucun code produit | Smoke + trois matrices live | Stabilité vérifiée ; qualité rejetée |
| MOD-019 | 2026-08-27 | 2 | `LiveCandidateAuditCompatibilityBenchmarkTests.cs` | Variante G.0 test-only : F.0 avec frontières B.0 gelées | Frontières non régénérées dans ces runs ; aucun code produit | Smoke + trois matrices live | Référence compatibilité approuvée, non intégrée |
| MOD-020 | 2026-08-27 | 2 | `LiveCandidateAuditCompatibilityBenchmarkTests.cs` | Variante H.0 test-only : génération de rôles seule, hash canonique et comparaison stricte B.0 | Aucun code produit ; premier smoke Release non comparable, puis collision locale corrigée | Smoke Debug/x64 + deux lives | Instrumentation et répétabilité approuvées |
| MOD-021 | 2026-08-27 | 2 | `LiveCandidateAuditCompatibilityBenchmarkTests.cs` | Variante I.0 test-only : contrat d'identité produit, batch=1 et zéro compatibilité | 20 appels par page ; aucun code produit | Smoke + run initial | Instrumentation vérifiée ; variante rejetée sémantiquement |
| MOD-022 | 2026-08-27 | 2 | `LiveCandidateAuditCompatibilityBenchmarkTests.cs` | Variante J.0 test-only : retrait de la pré-résolution du titre pour activer les contrats hiérarchiques existants, batch=1 | 33 appels et latence élevée ; aucun code produit | Smoke + run initial | Instrumentation vérifiée ; variante rejetée |
| MOD-023 | 2026-08-27 | 2 | Benchmark K.0 et dossier modèles local | Variante K.0 test-only ; installation manuelle du Q8_0 vérifié, sans catalogue/settings ; bascule/restauration serveur | 4,28 Go disque ; fenêtre LLM contrôlée ; aucun code produit | Hash/props/smoke/live/restauration | Q8 rejeté, Q5 restauré |
| MOD-024 | 2026-08-27 | 2 | Benchmark L.0, modèles locaux et templates runtime temporaires | Variante L.0 test-only ; installation du 8B vérifié ; diagnostic du template Qwen puis run ChatML ; aucune entrée catalogue/settings | 5,03 Go disque ; post-training non comparable ; aucun code produit | Hash/props/smoke/sondes/live/restauration | 8B rejeté, Q5 restauré |
| MOD-025 | 2026-08-27 | 2 | `LiveCandidateAuditCompatibilityBenchmarkTests.cs` | Variante M.0 test-only : génération réelle des rôles, tous les 20×4 couples, zéro identité, hash et phases tracés | 81 appels diagnostiques ; aucun code produit | Smoke zéro warning + run initial | Instrumentation vérifiée ; M.0 rejeté |
| MOD-026 | 2026-08-27 | 2 | `LiveBackofficeProfileEnrichmentBenchmarkTests.cs`, `tools/export-phase2-backoffice-input.ps1` | Export read-only de l'entrée worker et harness N.0 en mémoire avec capture transport/résultat ; lecture sûre des erreurs sans `choices` | Test/export seulement ; UUID locaux dans artefact de phase ; aucune écriture distante | Smokes verts ; run N.0 reproductible et artefact écrit avant assertion | Instrumentation vérifiée ; architecture N.0 rejetée |
| MOD-027 | 2026-08-27 | 2 | `tools/export-phase2-windowed-source-input.ps1`, `LiveWindowedSourceCardExtractionBenchmarkTests.cs` | Export des mêmes fenêtres avec provenance et harness N.1 JSON Schema, budget/usage/grounding par fenêtre | Lecture distante et tests seulement ; aucune mutation produit/base | Égalité 8/8 textes ; smoke vert ; live complet | Instrumentation vérifiée ; N.1 rejeté |
| MOD-028 | 2026-08-27 | 2 | `LivePrimaryWindowIdentityBenchmarkTests.cs` | Harness N.2 test-only 0..1, champs bornés, finish/budget/provenance tracés | Aucune mutation produit/base ; longueurs grammar peuvent altérer la copie | Smoke vert ; live complet | Instrumentation vérifiée ; N.2 rejeté comme contrat final |
| MOD-029 | 2026-08-27 | 2 | `tools/export-phase2-source-anchor-input.ps1`, `LiveSourceAnchorSelectionBenchmarkTests.cs` | Export heading/lignes/metadata et harness N.3 enum d'anchorId avec ordres configurables | Tests/read-only ; IDs complets trop coûteux dans budget 50 | Smoke vert ; run canonique complet | Instrumentation vérifiée ; N.3 rejeté |
| MOD-030 | 2026-08-27 | 2 | `LiveStructuralAnchorSelectionBenchmarkTests.cs` | Harness N.4 test-only : filtrage structural, clés compactes, provenance complète, contrôle sans appel et ordres configurables | Aucun code produit ; préfixe court protégé par contrôle global de collision | Smoke + trois runs N.4 | Instrumentation vérifiée ; brique Cuisine approuvée |
| MOD-031 | 2026-08-27 | 2 | Deux exporters Phase 2 et `LiveStructuralAnchorSelectionBenchmarkTests.cs` | Export NIST représentatif avec contrôle sans structure ; harness O.0 charge un oracle séparé, écrit l'artefact puis échoue sur le score sémantique | Lecture DB distante seulement ; aucune vérité injectée au prompt ; test volontairement rouge | Smoke vert + live O.0 canonique | Instrumentation vérifiée ; O.0 rejeté |
| MOD-032 | 2026-08-27 | 2 | `LiveStructuralAnchorSelectionBenchmarkTests.cs` | Mode O.1 test-only, tableau 0..1 et prompt figé profondeur-neutre ; oracles NIST/RFC hors prompt | Aucun produit ; exactitude évaluée après artefact | Smoke vert + unique live NIST | Instrumentation vérifiée ; O.1 rejeté |
| MOD-033 | 2026-08-27 | 2 | `LiveIndependentStructuralAnchorLegitimacyBenchmarkTests.cs` | Harness P.0 test-only : un booléen par ancre, ordres d'appels, vérité par anchorId et résolution 0/1/ambiguë | 1 appel/ancre ; aucun arbitrage sémantique code ; aucun produit | Smoke vert + unique live NIST | Instrumentation vérifiée ; P.0 rejeté |
| MOD-034 | 2026-08-27 | 2 | Checkpoint et protocole EXP-040 sous `artifacts/.../phase2/` | Diagnostic statique et gel pré-run Q019 ; aucun code produit | Aucun risque runtime ; hypothèses à confronter au live | Relecture fichiers/lignes, banque Q019 et SHA-256 | Vérifié ; Q.0 non exécutée |
| MOD-035 | 2026-08-27 | Global | Plan dynamique et artefact Telegram de rattrapage | Ajout d'une cadence obligatoire, déclencheurs, preuve/hash et suspension des longs travaux si rapport dû | Risque opérationnel seulement ; un échec notifier doit être visible, jamais masquer le RAG | Validation UTF-8 + retour notifier exact | Message envoyé ; règle active |
| MOD-036 | 2026-08-27 | 2→1 rouverte | Analyse Q.0, protocole R.0 et plan dynamique | Documentation du faux vert, réouverture automatique et gel prompt-only ; aucun produit modifié | Aucun risque runtime ; phase 2 suspendue | Hashes JSON/TRX, trace runtime, relecture prompts/scénarios | Vérifié ; TDD rouge prochain |
| MOD-037 | 2026-08-27 | 1 rouverte | Prompts routeur, test de présence et `LiveSourceBackedIntakeFirstActionBenchmarkTests.cs` | Règle R.0 ajoutée puis retirée après rejet ; test de présence retiré ; trois scénarios diagnostiques génériques conservés | Aucun changement prompt produit R.0 ne subsiste ; le harness test-only grandit | TRX rouge/vert, 41/41 budgets, unique run live et contrôle textuel post-rollback | Produit rollbacké ; instrumentation conservée |
| MOD-038 | 2026-08-27 | 1 rouverte | `LiveRouterTypedIntakeLedgerBenchmarkTests.cs` | Harness shadow S.0, outil non exécutable, huit oracles, ordres conditionnels, spans exacts et artefact avant assertion | Test-only ; premier smoke compilation corrigé avant live ; aucun produit | Smoke vert + unique ordre canonique live rouge | Instrumentation vérifiée ; S.0 rejeté et conservé |
| MOD-039 | 2026-08-27 | 1 rouverte | `LiveRouterKnowledgeModeBenchmarkTests.cs` | Harness shadow S.1 mono-enum, huit oracles et trois ordres conditionnels | Test-only ; aucune sortie exécutable ni référence produit | Smoke vert + canonique live rouge | Instrumentation vérifiée ; S.1 rejeté et conservé |
| MOD-040 | 2026-08-27 | 1 rouverte | Routeur natif, tests de disposition et harness première frontière | T.0 ajouté puis retiré ; scénarios ambigu non-grid/inventaire conservés test-only | Comportement temporaire violait une clarification explicite ; aucun produit T.0 ne subsiste | TDD rouge/vert, 45/45, live, post-rollback 40/40 | Produit rollbacké ; instrumentation conservée |
| MOD-041 | 2026-08-27 | 1 rouverte | `LiveDirectCapabilityBoundaryBenchmarkTests.cs` | Harness shadow V.0, six outils, huit oracles, ordres conditionnels, métriques et contrôles d'anchors/catégories | Test-only ; aucun backend, RouterPlan, mission ou produit | Smoke initial 1/1 ; live canonique rouge ; smoke post-revue 1/1 ; diff-check propre | Conservé comme contre-preuve ; SHA `0097DD08...3C6E1` |
| MOD-042 | 2026-08-27 | 1 rouverte | `LiveMultiturnRecoveryBenchmarkTests.cs` et visibilité interne de trois contrôles V.0 | Harness W.0 test-only : replay hashé, erreurs mécaniques, exécuteurs read-only, observation compacte, deux décisions, artefact avant assertion | Test-only ; trois lectures réelles mais aucune mutation ; dix preuves dépassent le contexte après ajout outils/prompt | Smoke 1/1 ; unique live complet et volontairement rouge | Conservé comme contre-preuve ; aucun produit/prompt modifié |
| MOD-043 | 2026-08-27 | 1 rouverte | Overrides d'identité EXP-048 dans le harness V.0 et scripts `exp048-*.ps1` | Permet une preuve X.0 distincte, lancement 9B hashé, smoke mécanique et restauration Q5 hashée | Test/artefacts uniquement ; le premier filtre non-x64 n'a exécuté aucun test ; aucune capacité produit modifiée | Smoke harness 2/2 ; parse PowerShell 3/3 ; porte outil ; live x64 ; restauration vérifiée indépendamment | Conservé pour reproductibilité ; serveur produit revenu au Q5 exact |
| MOD-044 | 2026-08-27 | 1 rouverte | Trace d'environnement sampling dans le harness V.0 et scripts `exp049-*.ps1` | Prouve température/top-p/pénalités/mode structuré réels ; lance, smoke et restaure Granite/Q5 avec hashes | Test/artefacts uniquement ; aucune valeur sampling produit modifiée hors processus de test | Build+smoke x64 1/1 ; parse scripts 3/3 ; smoke outil ; live ; restauration indépendante | Conservé pour reproductibilité ; produit inchangé |
| MOD-045 | 2026-08-27 | 1 rouverte | `RemoteReferenceBoundaryPackageTests.cs` et visibilité interne du catalogue/scénarios V.0 | Exporte hors ligne les huit requêtes exactes et des oracles physiquement séparés ; vérifie rôles, outils, URLs, chemins, secrets et champs interdits | Test-only ; écrit seulement avec deux variables opt-in ; aucune pile réseau ni backend ; quatre identifiants documentaires restent à approuver | Deux TRX rouges avant export, puis deux verts 1/1 ; hashes et inspection structurelle indépendante | Instrumentation vérifiée ; aucun code produit, envoi ou coût |
| MOD-046 | 2026-08-27 | 1 rouverte | Fiche et manifeste EXP-051, registre vivant | Remplace l'option « distant » abstraite par un consentement précis GPT-5.6 Sol, coûts, rétention et profil d'appel | Documentation seulement ; le manifeste porte `NOT_AUTHORIZED`, URL sélectionnée nulle et toutes les autorisations à false | Parse JSON, recalcul coût, hash outbound, scan clé/Bearer et sources officielles ouvertes | Vérifié hors ligne ; aucune API fournisseur ni modification produit |
| MOD-047 | 2026-08-27 | Global | Registre vivant et audit de blocage | Consigne l'arrêt contrôlé, les trois répétitions, le dernier état technique et les cinq voies de reprise | Documentation/état Goal seulement ; aucune mutation produit/runtime/fournisseur | Santé Q5, manifeste false/nul, diff-check et relecture des portes | Vérifié ; attente utilisateur assumée explicitement |
| MOD-048 | 2026-08-27 | 2 / D1 | Prompts/mémoire routeur, sélection des indices de catégorie, protocole natif et tests routeur | Publie des catégories sémantiquement positives ou ex aequo sûres et retire mécaniquement une portée non publiée sans changer route/requête | Une demande explicitement sans portée doit rester ouverte ; aucune heuristique de domaine autorisée | Tests D1 ciblés, live Normes, canari Q156, régression finale 200/200 | D1 approuvée ; terminaison Normes reportée à D3 |
| MOD-049 | 2026-08-27 | 2 / D2 | Agent v2, inventaire candidat, sélection sémantique, nouveau checkpoint d'adéquation, writer et tests | Rend la cible plate provisoire et laisse Qwen3 choisir select/clarify/continue ; impose une citation distincte par preuve sélectionnée | Appel LLM additionnel et latence actuelle 73–87 s ; contrats structurés doivent rester exacts | TDD rouge/vert, 4/4 contrats, 200/200 large, Q156 3/3 live | Fonctionnel approuvé ; performance/UI/revisionId non approuvés |
| MOD-050 | 2026-08-27 | Global | Deux analyses D1/D2, registre vivant et rapport Telegram | Fige causalité, tests, live, approbations partielles, limites et prochaine responsabilité D3 | Documentation seulement ; aucune limite non prouvée ne doit être requalifiée | Hashes, relecture et confirmation notifier exacte | Vérifié ; rapport Telegram envoyé en une partie |
| MOD-051 | 2026-08-27 | 2 / D3 | `SourceBackedAgentDocumentFocus.cs`, transition/navigation, trace et tests agent v2 | Publie au LLM `GLOBAL` plus les documents citables par EvidenceId ; résout mécaniquement le choix vers `docId`/`docPath`/`docRef` et refuse un ID non publié | Le modèle peut toujours choisir `GLOBAL` ; seules les transitions `refine_document_navigation` sont couvertes par ce slice | Rouge 0/1 ; contrat 3/3 standard et x64 ; large 203/203 ; deux live Normes ; canari Q156 | Approuvé sur la continuité documentaire ; rendement et terminaison transférés en phase 3 |
| MOD-052 | 2026-08-27 | 2 | Analyse D3, approbation de phase, registre vivant et rapport Telegram | Distingue l'essai invalide sur binaire x64 obsolète, les preuves valides, les limites non approuvées et l'ouverture de la phase 3 | Documentation seulement ; ne requalifie pas le timeout P4-003 en succès | Hashes TRX/JSONL, relecture des traces, `git diff --check` ciblé et confirmation notifier | Vérifié ; Telegram envoyé en une partie, 2 624/2 730 caractères |
| MOD-053 | 2026-08-27 | 3 / A1–A4 | `SourceBackedAgentV2Runner`, état de rendement, décision terminale, prompts et tests | Rend visibles routes, rendements et doublons ; laisse Qwen3 choisir poursuivre, répondre, clarifier ou déclarer l'insuffisance | La limite reste mécanique ; aucun sens documentaire n'est décidé par le code | TDD, régressions x64 et lives P4 successifs | Conservé après analyse comparative ; variantes rouges archivées |
| MOD-054 | 2026-08-27 | 3 / A5–A8 | Contrats source-item/evidence-mode, mémoire cumulative et sérialisation terminale | Sépare les formes de preuve et transporte un état compact, typé et borné jusqu'au tour terminal | Les schémas structurés ajoutent un coût de prompt ; le texte final doit rester contrôlé | Tests ciblés, canaris Normes/Q156, inspection JSONL | Conservé ; lacunes révélées poursuivies en A9–A18 |
| MOD-055 | 2026-08-27 | 3 / A9–A13 | Revue rapide, clarification observée, portée ouverte, politique de sélection et matrice candidat | Empêche un choix arbitraire lorsque plusieurs options documentées correspondent et réserve la clarification aux ambiguïtés observées | Qwen3 reste propriétaire du jugement d'adéquation ; aucune liste métier codée | TDD et canaris Q156/P4 | Conservé ; Q156 clarifie correctement |
| MOD-056 | 2026-08-27 | 3 / A14–A16 | Reroutage de clarification invalide et classification mécanique des formes d'évidence | Empêche de présenter une action de recherche documentaire comme une question utilisateur | Classification limitée à la forme contractuelle, jamais à la pertinence sémantique | Tests ciblés et lives P4 | Conservé ; terminaison encore ouverte avant A17 |
| MOD-057 | 2026-08-27 | 3 / A17 | Liaison des verdicts de revue rapide à la mémoire de rendement ; retrait d'un EvidenceId rejeté de la file pending | Après deux verdicts Qwen3 `research` sans rendement, le tour suivant n'expose que la décision terminale bornée | Seuil mécanique de répétition ; la décision finale reste celle du LLM | 13/13 x64 ; P4 live 141 902 ms | Mécanisme conservé ; wording non sourcé corrigé en A18 |
| MOD-058 | 2026-08-27 | 3 / A18 | `SourceBackedAgentSemanticYieldTerminalDecision`, compact follow-up, résultat/mémoire et tests localisés | Conserve la justification brute en audit mais construit l'insuffisance utilisateur uniquement avec des compteurs observés et la décision LLM | Message volontairement sobre ; ne peut pas affirmer une cause ou une absence mondiale | 5/5 contrats, 220/220 large, P4/Q156 finaux, inspection humaine | Approuvé sur la responsabilité phase 3 |
| MOD-059 | 2026-08-27 | 3→4 | Protocoles A5–A18, analyse consolidée, approbation et registre vivant | Rend le chantier reprenable et transfère explicitement identité, UI, performance, positif P4 et contrat mémoire | Documentation seulement ; aucune limite transférée n'est marquée résolue | Relecture, hashes, présence artefacts, `git diff --check` | Vérifié ; phase 3 approuvée, phase 4 ouverte |
| MOD-060 | 2026-08-27 | Global | Rapport Telegram phase 3 via fichier UTF-8 et `-MessageFile -RequireMessage` | Transmet le palier complet sans dépendre du binding d'un argument texte multiligne | Le premier appel erroné n'a envoyé que le message par défaut ; il n'est pas compté comme rapport | Comparaison ordinale du texte retourné par Telegram, longueur et SHA du fichier | Envoi complet confirmé : 2 673/2 779 caractères |
| MOD-061 | 2026-08-27 | 4 / backend | DTO `RagItemDto` et chargement de `document_revisions` dans `RagEndpoints` | Expose `revisionId` actif et SHA-256 complet pour le résultat canonique réellement indexé | Jointure limitée à la révision active ; aucune décision de pertinence ajoutée | TDD 0/2→2/2, backend 2 048/2 048, déploiement A16 et sonde A17 | Approuvé |
| MOD-062 | 2026-08-27 | 4 / identité | `EvidenceItem`, builders, `SourceRef`, mémoire, payload, parser et carte | Ajoute `AnchorId`/`ContentCardId` explicites et conserve l'identité immuable à chaque frontière | Aucun identifiant spécialisé encodé dans un autre ; voisins sans héritage d'ancre | TDD 0/6→6/6, navigation 2/2, transversal 240/240 | Approuvé |
| MOD-063 | 2026-08-27 | 4 / vérification | `SourceContractVerifier.EvidenceIdentity.cs`, resolver et launcher | Refuse identité corpus incomplète et fichier dont le SHA-256 ne correspond pas | Vérifications uniquement mécaniques ; aucune appréciation sémantique | Vérificateur 8/8, lanceur 8/8, clic réel A29 | Approuvé |
| MOD-064 | 2026-08-27 | 4 / orchestration | Prompt `semanticLabel`/`scopeValue`, continuité d'ancre et normalisation de `revisionId` | Supprime trois pertes révélées par régressions/live sans heuristique de corpus | Le modèle choisit toujours label, scope et action ; le code copie les champs contractuels | A12d 2/2, A14 2/2, A19 rouge puis A20 1/1 | Approuvé |
| MOD-065 | 2026-08-27 | 4 / WinUI | `ChatMessageItem.ParsedSources` et binding `SourcesCardsControl` dans le template assistant | Rend les cartes à l'endroit annoncé par le layout et les rattache au message persistant | Le panneau global historique reste masqué ; aucune reconstruction positionnelle | A26 rouge, A27 1/1, A28 1 307/1 307, inspection/clic A29 | Approuvé |
| MOD-066 | 2026-08-27 | 4→5 | Protocole, preuve UI, analyse, approbation et registre vivant | Rend la décision reprenable avec limites exactes et classification des runs invalides/non approuvés | Documentation seulement ; ne transforme aucun TRX de harnais en qualité | Hashes, lecture JSONL, tests finaux et diff-check | Vérifié ; phase 5 ouverte |
| MOD-067 | 2026-08-27 | Global | Rapport Telegram phase 4 via `-MessageFile -RequireMessage` | Transmet le palier avec preuve du clic réel et limites non approuvées | Reporting seulement ; titre ASCII pour éviter BUG-053 | Accusé notifier, longueur et SHA du fichier | Envoi confirmé : 2 820/2 926 caractères |
| MOD-068 | 2026-08-27 | 5 / A2–A4 | Sérialisation du terminal de clarification et tests mémoire | Distingue `clarification_requested` de l'insuffisance sans rendre la mémoire citable | Le résultat utilisateur ne change pas ; contrat mémoire seulement | Rouge/vert, régressions, Q156 live | Approuvé ; BUG-063 résolu |
| MOD-069 | 2026-08-27 | 5 / A5–A20 | Checkpoint d'adéquation compact à quatre décisions et transition de contexte même document | Laisse Qwen3 sélectionner, demander du contexte, continuer globalement ou clarifier | La décision sémantique reste LLM ; code limité aux IDs et schémas | TDD, suites source-backed/mémoire, A15/A20 live | Conservé ; a révélé le mauvais contrat `named_item` |
| MOD-070 | 2026-08-27 | 5 / A21–A32b | Mode `content_claim`, comptage par EvidenceId, claims même page et juge mode-aware | Sépare extraction de claims et inventaire d'instances nommées | Aucun vocabulaire Cuisine ; le modèle juge pertinence et fidélité | Rouge/vert, 173/173, 199/199, A28/A33 | Conservé ; cohérence inter-document traitée ensuite |
| MOD-071 | 2026-08-27 | 5 / A34–A39 | Localité mécanique des citations atomiques et règles LLM de cohérence de provenance | Rend chaque claim auditable et interdit la fusion silencieuse de variantes | Le code ne force jamais un document unique ni une compatibilité sémantique | A35 rouge, A36 3/3, A37b 30/30, A38 173/173, A39 199/199 | Approuvé déterministe ; A40 a prouvé la garde sémantique |
| MOD-072 | 2026-08-27 | 5 / A41–A45 | Transition `revise` sans rejet vers writer direct | Préserve la sélection et évite une recherche injustifiée lorsque seules les formulations changent | Aucun auto-accept ; vérificateur et second juge restent obligatoires | A41 rouge, A42b vert, A43 30/30, A44 174/174, A45 200/200 | Approuvé déterministe ; A46 fonctionnel |
| MOD-073 | 2026-08-27 | Global / Phase 5 | Rapports Telegram A15/A33/A40 | Rend visibles les changements de verdict, preuves et blockers | Reporting seulement | Accusés notifier et fichiers UTF-8 | Trois envois confirmés |
| MOD-074 | 2026-08-27 | 5 / documentation | Analyses A15, A20, A33, A34, A40 et journal A1–A47 | Conserve hypothèses, alternatives rejetées, TDD, live et limites | Documentation ; aucun succès créé par le texte | Relecture chemins/hashes et plan dynamique | À maintenir jusqu'à approbation de phase 5 |
| MOD-075 | 2026-08-27 | 5 / A48–A52 | Séparation forme du livrable/claims, continuité de l'ancre documentaire pendant réparation et frontière sûre de nom canonique | Empêche qu'une liste demandée soit recherchée comme objet préexistant et qu'une réparation perde un document explicitement reconnu | Qwen3 choisit toujours recherche, contexte et claims ; le code restaure seulement une valeur textuellement présente et valide l'identité | Trois rouges, A49 3/3, A50 82/82, A51 174/174, A52 200/200 | Approuvé déterministe ; live document nommé requis |
| MOD-076 | 2026-08-28 | 5 / A53–A58 | Pool probant `content_claim` indépendant de la forme du livrable | Permet à Qwen de conserver des claims utiles sans prétendre que la liste finale existe déjà dans le corpus | Aucun vocabulaire métier ; contrat générique de preuves atomiques | TDD ciblé et suites AgentV2 | Conservé ; insuffisant seul en live |
| MOD-077 | 2026-08-28 | 5 / A59–A67 | Transition générique de recherche focalisée sur le document reconnu | Atteint le corps d'un PDF nommé lorsque navigation/front matter ne suffisent pas | Qwen choisit la requête et la capacité ; le code transporte seulement `docId/docPath` | Comparaison trois routes, rouges/verts et live | Approuvé sur sa responsabilité de portée |
| MOD-078 | 2026-08-28 | 5 / A68–A73 | Budget de révision directe pour `content_claim` | Empêche une alternance writer/reviewer non bornée | Aucun auto-accept ; vérificateur et juge restent requis | Tests de boucle et non-régressions | Approuvé déterministe |
| MOD-079 | 2026-08-28 | 5 / A74–A83 | Tournoi sémantique incrémental de preuves plates | Montre à Qwen les nouveaux candidats avec son incumbent au lieu de figer la première fenêtre | Sélection finale exclusivement LLM | TDD tournoi, suites et traces live | Approuvé déterministe ; coût à optimiser |
| MOD-080 | 2026-08-28 | 5 / A84–A91 | Récupération compacte du contexte candidat | Maintient le prompt sous 4 096 tokens pendant le tournoi et la transition | Compaction mécanique, aucune décision sémantique locale | A86 rouge, A87–A90 verts, A91 live | Approuvé sur la garde contexte |
| MOD-081 | 2026-08-28 | 5 / A92–A97 | Conservation du pool partiel choisi par Qwen | Ne perd plus les preuves jugées utiles lors d'un verdict `continue` | Les IDs conservés viennent du protocole Qwen | A92 rouge, A93–A96 verts, A97 live | Approuvé déterministe ; boucle restante révélée |
| MOD-082 | 2026-08-28 | 5 / A98–A104 | Transition sans rendement respectant le gap sémantique actif | Interdit la sélection writer forcée quand Qwen a déclaré des manques et ferme correctement la collecte avant une sélection légitime | État/transition mécaniques ; motif et pool restent LLM | A98 rouge, A99–A103 verts, A104 live | Approuvé sur la sécurité ; terminaison encore lente |
| MOD-083 | 2026-08-28 | 5 / A105–A110 | Décision terminale Qwen obligatoire après une dernière route distincte | Borne les répétitions de récupération sans substituer une décision locale à Qwen | Outil terminal générique clarification/insuffisance | A105 rouge, A106–A110 verts | Approuvé déterministe |
| MOD-084 | 2026-08-28 | 5 / A111–A115 | Un checkpoint invalide ne peut remplacer la dernière décision sémantique valide | Préserve pool, verdict de gap et motif après troncature ou violation de protocole | `ProtocolValid` gouverne uniquement l'acceptation d'un nouvel état, jamais son sens | Audit 3/7, A112 rouge, A113/A114 verts, A115 live sûr | Approuvé comme garde de sécurité ; P4-NORMES-001 reste ouvert |
| MOD-085 | 2026-08-28 | 5 / A116–A120 | Revue exhaustive des candidats déjà récupérés dans le tournoi plat | Empêche un gap provisoire de déclencher une recherche alors que des challengers restent invisibles dans la mémoire | Le code gère uniquement les fenêtres ; Qwen conserve sélection et verdict | Audit du pool, A117 rouge, A118/A119 verts, A120 live | Approuvé sur sa responsabilité ; A120 reste non fonctionnel à 538 s |
| MOD-086 | 2026-08-28 | 5 / A121–A122 | Probe test-only du chemin historique `rag.summarize_live`, avec paramètres configurables et artefact même en cas d'erreur | Compare une architecture existante avant de modifier le routage | Aucune modification serveur ou produit ; Qwen garde la rédaction | A121 overflow 4K, A122 borné 193 s puis audit exact | Variante historique rejetée ; probe conservé pour comparaison |
| MOD-087 | 2026-08-28 | 5 / A123–A145 | Aperçu documentaire page-stratifié fondé sur `documents.context`, identités canoniques et `EvidenceBundle` | Fournit une couverture distribuée et bornée d'un document nommé sans recherche itérative ni résumé historique non canonique | Le code choisit les pages et transporte les preuves ; Qwen reste propriétaire de la sélection sémantique et de la formulation | Microprobes read-only A128–A139, ancres/pages/chunks audités | Architecture retenue pour mesure live ; sortie extractive directe A161 rejetée comme produit final |
| MOD-088 | 2026-08-28 | 5 / A146–A176 | Pool de huit candidats pour sept points, writer Qwen, validation mécanique, sélection Qwen sur candidats valides, réparation unique bornée et traces dédiées | Rend la synthèse terminale, sourcée et observable tout en empêchant placeholders, citations hors ordre et obligations non soutenues | Aucune taxonomie ANSI/Cuisine ; règles grammaticales et contrats d'identité génériques | Runs A157–A176 et audits humains A165/A176 | Conservé comme meilleur état sûr ; fonctionnalité stricte A176 non approuvée à 4/7 |
| MOD-089 | 2026-08-28 | 5 / A157–A165 | Budgets distincts du vrai `RagChatAgent.LlmAdapter` : writer 320, selector 64, candidate repair 96 | Empêche le plafond normalisé 1600 d'annuler les budgets du protocole d'aperçu dans le chemin produit réel | Le miroir `OpenAiCompatLlmClient` reste cohérent ; aucun changement de sens local | A157 révèle 1600 ; A158 prouve 208 borné ; A165 trace writer/selector terminaux | Approuvé sur la borne de génération, latence globale encore non approuvée |
| MOD-090 | 2026-08-28 | 5 / audit A165 | Rapport source par source, séparation fonctionnalité/traçabilité/style/performance et refus d'un faux 7/7 | Empêche que sept cartes exactes ou un harnais vert soient confondus avec sept affirmations exactes | Documentation et verdict seulement | Hashes A134/A165, extraits canoniques, contre-audit terminologique | A165 corrigé en `TESTEE_NON_APPROUVEE` : 5/7, 105698 ms |
| MOD-091 | 2026-08-28 | 5 / A166–A172 | Prompts de fidélité, audit candidat/source, révision locale et comparaison des modèles installés | Détermine si le défaut est un simple manque d'instruction ou de capacité locale | Probes seulement ; aucun modèle distant, téléchargement ou règle corpus | A166/A167 live, A168–A172 microprobes | Variantes rejetées ; Qwen4B et Granite reproduisent les défauts, 8B/9B hors borne |
| MOD-092 | 2026-08-28 | 5 / A173–A175 | Sur-échantillonnage read-only de douze zones puis sélection Qwen 12→7 avant writer | Teste si davantage de passages clairs évitent les tableaux OCR difficiles | Qwen conserve la décision ; code transporte seulement les 12 identités | A173 842 ms ; A174/A175 insuffisances sûres | Variante produit rejetée et retirée : sélection préfixe stable `E1,E3…E8` |
| MOD-093 | 2026-08-28 | 5 / A176 | Rollback sur le pool huit-pour-sept ; sélecteur compact 650 caractères | Restaure le dernier chemin terminal démontré et garde seulement une réduction de prompt mesurée | Consignes A166 sans effet retirées ; aucun empilement expérimental | `14/14`, `45/45`, `1353/1353`, A176 et diff-check | Retenu ; A176 7 cartes, 4/7 strict, 98368 ms internes |
| MOD-094 | 2026-08-28 | 5 / A177 | Microprobe de traduction de faits anglais déjà nettoyés | Mesure une séparation extraction source-language / traduction cible sans changer le produit | Qwen reste l'unique modèle ; pas de dictionnaire ou correction ANSI | Portée mieux préservée en 13201 ms, terminologie encore faible | Signal partiel seulement ; prochaine microprobe causale, pas encore produit |
| MOD-095 | 2026-08-28 | 5 / A178–A181 | Sonde test-only deux-passes avec contrats ordonnés, budgets 320–480 et captures brutes avant validation | Mesure la stabilité et le coût réels d'une normalisation source puis traduction Qwen | Aucun changement produit ; même modèle local ; arrêt garanti dans `finally` | A178/A180/A181 tronquent E8 ; A179 perd une ligne de traduction ; A181 source seule 72528 ms et `finish_reason=length` | Variante rejetée ; sonde conservée comme preuve reproductible |
| MOD-096 | 2026-08-28 | 5 / A182 | Sonde quote-grounded en un appel : extrait source littéral puis formulation française | Teste si une ancre textuelle choisie par Qwen réduit les dérives sans second appel | Le code vérifie seulement ordre, EvidenceId et présence littérale ; aucune règle métier | 1719+480 jetons, 71687 ms, 6/8 lignes, `length` | Variante rejetée ; double contenu trop coûteux et bruit OCR recopié |
| MOD-097 | 2026-08-28 | 5 / A183 | Compaction writer tête+fin à 650 caractères | Teste si une fenêtre plus petite conserve qualifications et conditions tout en réduisant le bruit | Transformation mécanique générique ; Qwen conserve le sens | `15/15`, puis live 3/7 et 97120 ms internes | Rollback complet ; modification et test produit retirés |
| MOD-098 | 2026-08-28 | 5 / A184–A186 | Pool test-only de passages mécaniquement lisibles, répartis sur les pages, puis writers une/deux passes | Sépare qualité structurelle des passages et capacité de traduction | Filtres de forme indépendants du corpus et de la langue ; aucune sélection sémantique par le code | A184 4/8, A185 4/7 à 38379 ms writer, A186 traduction 6/7 et `length` | Non intégré ; qualité structurelle utile mais insuffisante pour la fidélité interlangue |
| MOD-099 | 2026-08-28 | 5 / A187–A189 | Coalescence mécanique de plusieurs appels `overview + summary_doc` strictement homogènes vers un appel canonique | Évite qu'une décomposition de facettes d'un même livrable déclenche réparation, fallback et timeout | Même document, portée, focus et bornes obligatoires ; tout mélange reste rejeté ; la demande originale reste le contexte sémantique | Routeur `62/62`; A189 accepte trois appels sans réparation et termine en 139941 ms | Conservé provisoirement comme garde de terminaison ; synthèse A189 non fonctionnelle |
| MOD-100 | 2026-08-28 | 5 / A190 | Sonde read-only capable de résoudre exactement le `docId`, le nombre de pages et le chemin canonique complet depuis un nom de fichier | Permet d'inspecter les entrées réelles du writer sans reproduire manuellement un identifiant serveur | Correspondance exacte uniquement, échec sur zéro ou plusieurs IDs ; aucun changement produit | PDF Audit 58 pages, cinq chunks complets en 85 ms | Probe conservée ; a prouvé la lacune d'adéquation des preuves |
| MOD-101 | 2026-08-28 | 5 / A191 | Microprobe writer sur les quatre passages français mécaniquement lisibles du canari | Isole la capacité rédactionnelle de la qualité et de la couverture du pool | Test-only ; Qwen reste le rédacteur ; aucun dictionnaire, correction métier ou règle ISO | 4/4 faits fidèles, 28139 ms, 157 tokens, `stop`; parseur de sonde plus strict que le produit sur les crochets | Diagnostic seulement ; ne couvre pas la finalité ni l'usage citationnel demandés |
| MOD-102 | 2026-08-28 | 5 / A192–A196 | Probes page-stratifiées larges, navigation structurelle, selector d'ancres et writer adaptatif | Compare acquisition uniforme et structurelle sans choisir de thème dans le code | Test-only/read-only hors appels Qwen bornés ; identités canoniques complètes | A192 12 pages inadéquates ; A193 42 ancres ; A195 six passages substantiels ; A196 `length` à 320 | Navigation structurelle retenue comme source de candidats ; writer A196 rejeté |
| MOD-103 | 2026-08-28 | 5 / A197 | Transport `overviewFacets` de la coalescence native jusqu'à `rag.summarize_live`, normalisation et manifeste | Préserve les trois décisions sémantiques déjà émises par Qwen au lieu de les remplacer par `query=""` | Ordre, déduplication sans casse, longueur 180 et cardinalité bornée ; aucune sémantique codée | TDD rouge `KeyNotFoundException`, vert exact ; routeur+parité `107/107` | Correction de contrat retenue ; fonctionnalité non déduite |
| MOD-104 | 2026-08-28 | 5 / A198 | Selector Qwen indexé par facette avec matérialisation des ancres choisies | Associe une ou deux preuves à chaque facette explicite | Exclusions de forme seulement ; Qwen choisit les IDs ; code vérifie existence et identité | `F1=A1,A5`, `F2=A6,A7`, `F3=A18,A27`; six contextes en 447 ms | Test-only, preuve d'architecture ; bruit A1 et inférence F3 à juger |
| MOD-105 | 2026-08-28 | 5 / A199–A200 | Writers global par facettes puis appels textuels isolés | Mesure fuite inter-facettes et discipline de sortie sous contexte isolé | Budgets 256 puis 3×80 ; aucune règle corpus | A199 sans citations ; A199b fuite F2→F1 ; A200 F1/F3 `length` | Deux architectures textuelles rejetées |
| MOD-106 | 2026-08-28 | 5 / A201 | Writers isolés via appel d'outil natif `submit_facet_answer` | Sépare texte, EvidenceId et statut d'inférence ; le code rend seulement les citations | Enum d'IDs autorisés, réponse bornée, booléen obligatoire ; décision sémantique Qwen | 3/3 `tool_calls`, 248 tokens, 52251 ms ; contrat mécanique valide | Test-only ; rejet sémantique car F3 conserve « vous devez citer » |
| MOD-107 | 2026-08-28 | 5 / A192–A201 | Protocole évolutif et rapport de palier détaillé | Trace résultats, conditions d'arrêt, hashes et décision d'architecture | Documentation uniquement | Relecture, hashes, hygiène processus et rapport Telegram | Vérifié ; Phase 5 reste ouverte |
| MOD-108 | 2026-08-28 | 5 / A202 | Evidence Judge Qwen structuré, isolé par facette, avec verdict fermé et révision minimale | Vérifie relation, portée et modalité sans règle lexicale code | Une passe, 128 tokens/facette, preuves locales et outil requis | F1 complet ; F2 `length`/arguments vides ; F3 approuve une obligation non sourcée | Variante rejetée ; branche Judge fermée |
| MOD-109 | 2026-08-28 | 5 / A203 | Comparaison offline fallback extractif versus insuffisance honnête | Choisit le terminal le plus utile sans lancer un nouveau writer | Extraits canoniques seuls, facettes et identités conservées, avertissement non-synthèse obligatoire | 3 facettes, 6 identités, 3463 caractères, zéro assertion documentaire générée | Fallback extractif retenu comme candidat de secours uniquement |
| MOD-110 | 2026-08-28 | 5 / A204–A204b | Acquisition produit par navigation structurale, mapping Qwen facette→ancres et matérialisation exacte | Remplace l'échantillonnage aveugle uniquement lorsque le routeur fournit plusieurs facettes explicites | Inventaire borné, filtre de forme, IDs uniques, Qwen propriétaire du choix, identité canonique obligatoire | A204 expose la perte au pont summary ; test rouge/vert puis A204b sélectionne 6/35 ancres | Retenu ; fallback seulement après échec writer vérifié |
| MOD-111 | 2026-08-28 | 5 / A204b–A205b | Fallback extractif multilingue validé par `SourceContractVerifier` | Rend les preuves locales au lieu d'une pseudo-synthèse ou d'une insuffisance opaque | `notSynthesis=true`, citations exactement une fois, cartes complètes, aucune décision documentaire du code | Q016 et Q019 : 3 facettes, 6/6 sources, aucune alerte ; 125272/130390 ms | Approuvé comme terminal sûr, jamais comme succès de synthèse |
| MOD-112 | 2026-08-28 | 5 / A205a | Replay nominal P4-NORMES-001 après intégration du fallback | Vérifie que la branche sans facettes ne change pas de stratégie | Aucun fallback sans mapping complet ; ancien pool huit-pour-sept conservé | 7 points, 7 cartes, IDs E1/E3–E8, 95943 ms internes | Non-régression mécanique approuvée ; qualité stricte toujours non approuvée |
| MOD-113 | 2026-08-28 | 5 / A206 | Probe route-only pour un contrat Qwen spécialisé d'aperçu multi-facettes | Isole les 49–53 s du second étage routeur sans toucher au writer ni à la sélection de preuves | Un outil, document explicite, facettes Qwen ordonnées, validation mécanique seulement | 104 tokens, 15669 ms, gain 68,05 %, contrat fidèle | Approuvé pour intégration A207 |
| MOD-114 | 2026-08-28 | 5 / A207–A208 | `submit_document_overview_route` intégré puis converti vers le `RouterPlan` canonique | Réduit le coût routeur sans créer de branche parallèle de preuve ni retirer le terminal sûr | Classifieur sémantique, 2–5 facettes, 160 tokens, fallback fermé vers route générale si invalide | 90/90, 229/229, 1383/1383 ; Q016 unique 92670 ms, six identités exactement inchangées | Retenu ; gain end-to-end 26,02 %, synthèse toujours non approuvée |
| MOD-115 | 2026-08-28 | 5 / A209–A212 | Matrice des treize familles obligatoires et canari dessert simple Q016 Cuisine | Sort de la sur-optimisation des résumés et reprend la validation générale proportionnée | Un seul live sans changement produit, critères qualité/source/30 s séparés | 230130 ms ; E3 pertinent perdu après protocole invalide ; E40 Profiteroles rendue comme Fondant | Rejeté qualité et performance ; aucune relance |
| MOD-116 | 2026-08-28 | 5 / A213–A216 | Continuité bornée des candidats après revue invalide et garde `corpus_target_not_cited` du harnais | Empêche un candidat non rejeté de disparaître tout en laissant Qwen arbitrer les candidats distincts | Deux carry slots, une place nouveau lot, remplacement par identité mécanique, traces explicites ; aucun vocabulaire métier | Rouge exact puis 1/1, 18/18, 14/14, 182/182 et 1391/1391 avec TRX | Approuvé déterministe ; live causal séparé requis |
| MOD-117 | 2026-08-28 | 5 / A217–A220 | Replay causal unique du dessert simple après correction A214–A215 | Vérifie fonction, source cible, signal du harnais, chemin carry et latence sur le vrai produit | Un seul Q016, catégorie vide, aucun changement avant audit, aucune relance opportuniste | 188057 ms ; flags corrects ; carry E1/E2/E3 mais revue E1/E2/E40 seulement | Dessert rejeté ; A215 approuvé ; A214 partiel |
| MOD-118 | 2026-08-28 | 5 / A221–A225 | Normalisation mécanique d'une réponse inline trop longue vers writer et fenêtre carry dynamique | Conserve la sélection Qwen E#/ancre sans interpréter son texte et expose tous les candidats du jugement invalide précédent | JSON exact, IDs visibles, texte ignoré ; 3 groupes normaux, jusqu'à 6 après carry ; aucun lexique métier | Deux rouges puis 2/2, 16/16, 184/184, 18/18, 1393/1393 | Approuvé déterministe ; BUG-116 fermé |
| MOD-119 | 2026-08-28 | 5 / A226–A229 | Canari factuel exact Q023 Documentation technique sur la qualification d'un document AAF | Mesure une réponse binaire, sourcée et courte hors Cuisine/résumé | Un run, cible fichier concrète, catégorie vide, aucun changement avant audit | 65653 ms ; clarification, zéro outil/source, flags cible corrects | Famille rejetée ; BUG-117 ouvert |
| MOD-120 | 2026-08-28 | 5 / A230–A234 | Contraste sémantique fait à établir par les preuves vs préférence que seul l'utilisateur peut fournir | Empêche une alternative factuelle de devenir un choix utilisateur sans imposer la route par le code | Prompts et descriptions seulement ; vérité/statut/applicabilité/valeur vs objectif/portée/contrainte/livrable ; aucun terme canari | Rouge 0/2, vert 2/2, budgets stricts, routeur+normalisation 162/162, client 1395/1395 | Approuvé déterministe ; BUG-117 reste ouvert live |
| MOD-121 | 2026-08-28 | 5 / A235–A238 | Replay live causal unique de Q023 sur le produit A234 figé | Vérifie si le contraste de prompt change réellement la famille choisie par Qwen | Un run, hashes produit/test/banque figés, aucun build ni changement, aucune relance | 47733 ms harnais, 28781 ms internes ; classifieur encore clarification ; zéro outil/source ; flags corrects | Rejeté live ; prompt seul insuffisant, BUG-117 ouvert |
| MOD-122 | 2026-08-28 | 5 / A239–A244 | Séparation de la famille de travail et du terminal provisoire de clarification | Évite le tunnel classifieur→clarification tout en conservant une vraie demande utilisateur | Classifieur à quatre familles ; second étage famille proposée + clarification ; Qwen choisit, code n'inspecte pas le texte | Rouge 10/16, vert 16/16, vraie préférence préservée, 161/161, 28/28, client 1394/1394 | Approuvé déterministe ; BUG-117 ouvert live |
| MOD-123 | 2026-08-28 | 5 / A245–A248 | Replay live unique Q023 après séparation des étages | Distingue correction du classifieur et décision détaillée du second étage | Produit/test/banque figés, un run, aucun changement ni relance | Classifieur source-backed 6604 ms ; second étage clarification ~32490 ms ; total 39425 ms, zéro outil/source | Premier étage approuvé live ; fonction et performance rejetées |
| MOD-124 | 2026-08-28 | 5 / A249–A253 | Comparaison contrôle actuel, evidence-first et juge compact avant nouveau patch | Évite d'empiler un quatrième correctif local sur la même classe d'échec | Audit clarification aval, tests candidats/préférence/mémoire, métriques live existantes ; aucun produit changé | Contrôle rejeté ; E `29/29` et requête obligatoire ; J ajoute un appel sans preuve | Evidence-first retenu pour le prochain TDD |
| MOD-125 | 2026-08-28 | 5 / A254–A259 | Second étage evidence-first pour les familles documentaires, clarification pré-outil conservée pour operational | Force l'observation documentaire avant une clarification portant sur le corpus sans reprendre la décision sémantique à Qwen | Politique par famille choisie ; aucun texte utilisateur, langue, corpus ou fichier inspecté ; clarification aval inchangée | Rouge 6/9, vert 9/9, régressions 196/196, client 1394/1394, budgets et whitespace propres | Approuvé déterministe ; BUG-117 ouvert live |
| MOD-126 | 2026-08-28 | 5 / A260–A263 | Replay live unique Q023 evidence-first puis audit catalogue/retrieval en lecture seule | Vérifie l'effet causal du nouvel ordre et localise la prochaine frontière sans relance | DLL/banque figées ; un seul Qwen ; deux sondes backend ; aucune règle issue de la banque | rag.search avant clarification, 33 preuves mais aucune cible ; catalogue exact absent ; 58628 ms internes | Séquencement approuvé live ; fonction/source/performance rejetées |
| MOD-127 | 2026-08-28 | 5 / A264–A268 | Comparaison C/R/T/V de la résolution d'un document nommé et décision R+V | Établit l'identité actuelle avant retrieval et empêche sa substitution aval, tout en laissant Qwen choisir pivot, clarification, partiel ou insuffisance | Lecture seule ; contrat générique `resolved/not_found/ambiguous/inconclusive` ; mémoire jamais preuve ; aucun terme de banque/corpus | Chemin causal et 15 fichiers audités ; outil actuel fuzzy/non typé ; `doc_not_found` peut être compacté avec `ok=true` ; matrice, portes TDD et Telegram 2312/2418 produits | Architecture approuvée pour TDD ; aucun produit corrigé, BUG-118 ouvert |
| MOD-128 | 2026-08-28 | 5 / A269 | Préenregistrement TDD de l'architecture R+V | Fige avant produit les contrats, responsabilités Qwen/code, statuts catalogue, scope/quarantaine, pivot alternatif et garde d'identité verticale | 36 cas déterministes ; mémoire jamais preuve ; `not_found` seulement après observation complète ; aucun fuzzy positif ni terme de corpus | Protocole 455 lignes, matrice rouges/verts, cinq portes et critères de rejet ; aucun code produit/test modifié | A269 approuvé comme protocole ; BUG-118 reste ouvert, A270 rouges suivant |
| MOD-129 | 2026-08-28 | 5 / A270–A275 | Résolution actuelle exacte + scope/quarantaine + décision Qwen typée + garde d'identité aval | Empêche qu'un document absent/ambigu/inconclusif soit remplacé par un voisin et laisse Qwen choisir clarification, insuffisance, alternative ou retry | Port catalogue read-only ; exact seulement ; mémoire jamais preuve ; alternative avec disclosure ; aucun terme de corpus ; budgets inchangés | Rouge 1/5, intermédiaire 9/13, ciblés 13/13 et 30/30, régressions finales 405/405, client 1 419/1 419, anti-hardcode/diff-check/modularité verts | Approuvé déterministe ; BUG-118 corrigé déterministement et à valider live ; Phase 5 ouverte |
| MOD-130 | 2026-08-28 | 5 / A276 | Préenregistrement du replay live unique Q023 après R+V | Autorise seulement un préflight read-only, un run si la cible reste exactement absente, puis audit sans patch/rerun | DLL/banque/manifeste figés ; Qwen réel uniquement après portes ; absence, ambiguïté, erreur et dérive ont des branches d'arrêt explicites | Protocole 254 lignes ; quatre suites Qwen, critères séparés BUG-118/117/provenance/performance, règle d'arrêt | A276 approuvé comme protocole ; A277 préflight suivant, aucun live encore lancé |
| MOD-131 | 2026-08-28 | 5 / A277 | Préflight read-only avant l'unique Q023 | Vérifie que l'hypothèse d'absence et l'environnement figé n'ont pas dérivé avant d'allumer Qwen | Aucun build/inférence/mutation ; secrets et URL non consignés ; dossier A278 créé seulement s'il était absent | Quatre hashes identiques ; `/ready` 200/ok ; catalogue path/name/stem 0/0/0 ; modèle/runtime hashes canoniques ; zéro processus | A277 vert ; unique A278 autorisé |
| MOD-132 | 2026-08-28 | 5 / A278–A279 | Replay Q023 unique et audit vertical post-R+V | Vérifie en live que le document absent n'est plus substitué, puis sépare sécurité, fonction et performance | Un run, zéro patch/rerun ; xUnit vert non assimilé à qualité ; arrêt processus obligatoire | NotFound complet 35 ms, action quarantinée, zéro outil/source ; Qwen clarification 936/218 tokens ; 60 827 ms internes | Sécurité R+V approuvée live, BUG-118 fermé ; fonction/performance rejetées, BUG-117 ouvert |
| MOD-133 | 2026-08-28 | 5 / A280 | Comparaison C/P/D/J/T et décision de portfolio typé | Empêche une transition incohérente avec le statut catalogue tout en laissant Qwen choisir insuffisance, correction d'identité, alternative, sélection, retry ou reprise canonique | Lecture seule ; un appel ; pas de règle `NotFound => insufficiency`, pas de juge, pas de hausse budget, pas de lexique corpus | Matrice quatre statuts/six capacités, 18 cas d'acceptation, séquence A281–A285 et critères de rejet préenregistrés | Architecture retenue pour TDD ; produit inchangé ; BUG-117 ouvert |
| MOD-134 | 2026-08-29 | 5 / A281–A282 | Migration des fixtures et tests directs du portfolio par statut | Fige les outils exposés, `source_identity`, options exactes, retry unique, reprise canonique et réparation avant produit | Tests seulement ; rouge doit compiler et échouer sur l'ancien outil, sans backend/modèle | 23 exécutés, 8 verts, 15 rouges ; erreurs `resolve_named_document_observation`/contrat ancien uniquement | Rouge causal approuvé ; A283 autorisé |
| MOD-135 | 2026-08-29 | 5 / A283–A285 | Portfolio dynamique, parseur strict et clarification d'identité | Retire les transitions incohérentes sans choisir la réponse ; Qwen appelle exactement une capacité compatible dans un seul appel | Aucun corpus, juge, budget ou timeout ; propriétés strictes ; résolution hors preuve ; options ambiguës mécaniques | 24/24, 518/518, 1 427/1 427 ; schémas 1303/1638/1365/1365 <1693 ; diff-check/anti-hardcode/processus verts | Approuvé déterministe ; BUG-117 live ouvert ; A286 préenregistrement suivant |
| MOD-136 | 2026-08-29 | 5 / A286 | Préenregistrement du replay live unique Q023 post-portfolio | Mesure causalement le choix Qwen après `NotFound` avec les trois seuls outils compatibles | DLL/tests/banque/manifeste figés ; préflight avant modèle ; un run ; aucun patch/rerun ; document présent séparé | Protocole 285 lignes, transitions et critères séparés, branches d'arrêt et hygiène processus | A286 approuvé comme protocole ; A287 read-only suivant |
| MOD-137 | 2026-08-29 | 5 / A287 | Préflight read-only avant l'unique Q023 post-portfolio | Vérifie que l'absence exacte et le produit n'ont pas dérivé avant d'allumer Qwen | Aucun build/inférence ; secrets non consignés ; création du dossier seulement après toutes les portes | Hashes conformes ; `/ready` 200/ok ; path/name/stem 0/0/0 ; Q5 4K ; zéro processus/env | A287 vert ; unique A288 autorisé |
| MOD-138 | 2026-08-29 | 5 / A288–A289 | Replay Q023 unique et audit du portfolio en live | Vérifie que Qwen choisit une suite cohérente après absence complète sans fausse préférence | Un run, zéro patch/rerun ; fonction/sécurité/performance séparées ; arrêt processus | Insuffisance 1 tentative, 842/115 tokens, zéro outil/source ; 47 933/61 952 ms | Contrat/fonction absent/sécurité approuvés ; BUG-117 fermé ; performance rejetée ; BUG-119 ouvert |
| MOD-139 | 2026-08-29 | 5 / A290 | Choix read-only et préenregistrement du canari positif Q106 | Complète Q023 absent par un document nommé actuel, court, unique et fonctionnellement discriminant | Aucun produit/test/build/Qwen ; comparaison de cinq cas exacts ; inspection canonique bornée, sans hardcode produit | Catalogue 1/0/0/0/0 ; Q106 indexé, 2 pages, révision/hash/chunks figés ; critères et arrêt avant rerun écrits | A290 approuvé comme protocole ; seul A291 read-only est autorisé |
| MOD-140 | 2026-08-29 | 5 / A291 | Préflight read-only avant l'unique Q106 positif | Empêche un live sur binaire, banque, corpus, runtime ou identité ayant dérivé | Aucun build/inférence ; secrets exclus ; dossier créé seulement après toutes les portes | Six hashes d'entrée conformes ; `/ready` 200/ok ; nom/stem exacts ; identité et 9 chunks stables ; zéro processus/env | A291 vert ; unique A292 autorisé |
| MOD-141 | 2026-08-29 | 5 / A292–A293 | Replay positif unique et diagnostic du faux `NotFound` | Vérifie le chemin présent et localise la première perte avant retrieval | Un run consommé, zéro patch/build/rerun ; endpoints et port produit sondés ensuite sans modèle ; cause non inventée | Live : product catalog 0/0 ; après : trois frontières à 1 et 20/20 sondes ApiClient positives ; Qwen conserve le nom et choisit insuffisance | Résolution/fonction/provenance positive/performance rejetées ; sûreté portfolio approuvée ; BUG-120 ouvert ; A291 reclassé porte insuffisante |
| MOD-142 | 2026-08-29 | 5 / A294 | Comparaison des stratégies contre le faux `NotFound` | Supprime le point de décision négatif unique sans forcer un positif depuis une source potentiellement stale | Aucun patch/build/Qwen ; F1 catalogue V2 et F2 unifié restent observations, jamais preuves ; retry reste Qwen et borné | C/B/R/U/V comparés ; consensus K retenu ; accord exact obligatoire, divergence/erreur/incomplet => `Inconclusive` | A294 approuvé comme protocole ; seul rouge A295 autorisé |
| MOD-143 | 2026-08-29 | 5 / A295 | Tests causaux avec vrai ApiClient/adaptateur/résolveur | Rend falsifiable l'hypothèse F1+F2 avant produit | Tests uniquement ; quatre cas doivent compiler et échouer sur A293 | 0/4 : exact/vide produit `Resolved`, vide/exact produit `NotFound`, F2 jamais observée | Rouge causal approuvé ; patch A296 autorisé |
| MOD-144 | 2026-08-29 | 5 / A296 | Consensus exact F1+F2 et divergence typée | Empêche un seul vide de devenir absence complète et un seul exact de forcer un positif | Deux observations paginées bornées ; aucun retry caché, union, fuzzy, corpus, budget, prompt ou preuve ajoutés | Première compilation invalide CS0509/CS0111 conservée ; puis 4/4 et matrice 16/16 | Approuvé déterministe au niveau adaptateur/résolveur |
| MOD-145 | 2026-08-29 | 5 / A297 | Intégration du consensus dans le runner existant | Vérifie que Qwen garde le choix de retry et que l'action originale est seulement bornée après accord | Qwen scripté, aucun modèle réel ; query/tool/topK intacts ; désaccord persistant fail-closed | Consensus+runner 18/18 ; document/API/navigation 120/120 ; source/router/mémoire 234/234 | Approuvé déterministe ; aucun contenu exécuté sur désaccord persistant |
| MOD-146 | 2026-08-29 | 5 / A298–A299 | Régression complète, coût backend sans modèle et verdict | Vérifie non-régression, performance catalogue, scope, hashes, hardcode et ressources | Backend en lecture seule, secrets non consignés ; aucun modèle/WinUI/replay ; worktree historique non nettoyé | Client 1 445/1 445 ; Q106 11/11, médiane 69 ms ; diff-check/anti-hardcode/processus verts | Palier approuvé déterministement ; BUG-120 reste ouvert jusqu'au replay live positif |
| MOD-147 | 2026-08-29 | 5 / A300 | Préenregistrement du replay live Q106 post-consensus | Rend le prochain run causal et empêche d'assimiler résolution catalogue, contenu, provenance, mémoire, UI et performance | Aucun build/Qwen/dossier live ; DLL, sources, banque, identité, runtime, commande, branches d'arrêt et règle d'un run figés | Protocole 507 lignes SHA `798E86C7...C6E6F` ; vrai résolveur F1+F2 obligatoire en préflight | A300 approuvé comme protocole ; seulement A301 read-only autorisé |
| MOD-148 | 2026-08-29 | 5 / A301–A303 | Préflight du vrai consensus et arrêt sur identité de révision divergente | Empêche un replay où le bon docId serait ensuite rejeté par un faux hash candidat | Lecture seule ; aucun build/Qwen/dossier A302 ; détail, contexte et réponses brutes F1/F2 inspectés sans secret | 12/12 hashes, runtime/backend verts ; `Resolved` exact 118–125 ms ; candidat hash32/révision vide, contexte SHA256/révision canonique ; garde aval compare les hashes | A301 rejeté, A302 non exécuté, BUG-121 critique ouvert avant live |
| MOD-149 | 2026-08-29 | 5 / A304 | Contrat sanitisation + hydratation canonique après exact unique | Place l'identité active entre consensus d'existence et première preuve sans donner de décision documentaire au code | Un appel borné après agrégation ; `NotFound`/`Ambiguous`/désaccord sans contexte ; erreur/dérive `Inconclusive` ; texte ignoré | C/S/H/B/V comparés ; H+S retenus ; 18 cas et gates A305–A309 figés avant patch | A304 approuvé comme protocole |
| MOD-150 | 2026-08-29 | 5 / A305 | Test rouge canonique avec vrai ApiClient et handler contrôlé | Rend falsifiables hash32, révision absente, acceptation/rejet de preuve, dérives, cardinalités et annulation | Tests uniquement, aucun backend/modèle ; rouge doit compiler | 18 exécutés, 5 verts, 13 rouges causaux, zéro erreur/timeout | A305 approuvé, patch A306 autorisé |
| MOD-151 | 2026-08-29 | 5 / A306 | Port d'hydratation, adaptateur user-safe, sanitisation, résolveur et factory | Fournit docId/path/name/revisionId/SHA256 actifs avant contenu et conserve la garde aval stricte | Aucun EvidenceItem, mémoire, prompt, retry, corpus, budget ou timeout ; payload contexte textuel non exposé | Noyau 18/18, document nommé 48/48 ; raisons typées et annulation couvertes | Approuvé déterministe |
| MOD-152 | 2026-08-29 | 5 / A307–A309 | Régressions, coût réel, préflight A301 repassé, rapport et Telegram | Prouve non-régression et viabilité backend sans assimiler ce succès à la réponse end-to-end | Aucun modèle/live ; axes fonction/provenance/mémoire/UI/performance séparés ; processus build arrêtés | 355/355, 337/337, client 1 463/1 463 ; hydratation médiane 15 ms ; Q106 identity gate PASS ; Telegram 2 882 caractères | Palier approuvé ; BUG-121 fermé mécaniquement ; A310 live séparé requis |
| MOD-153 | 2026-08-29 | 5 / A310 | Préenregistrement du replay positif Q106 post-identité | Rend le run causal et interdit toute adaptation au résultat | Un run live, aucun patch/rerun, préflight exact obligatoire, verdicts fonction/provenance/mémoire/UI/performance séparés | Protocole initial 511 lignes, hashes et commande figés | A310 approuvé comme protocole |
| MOD-154 | 2026-08-29 | 5 / A311 | Porte réelle immédiatement avant inférence | Vérifie que le document et son identité canonique sont effectivement accessibles avant de dépenser un run Qwen | Lecture seule, aucune inférence ; banque/runtime/backend/résolveur/contexte contrôlés | Q106 `Resolved`, exact 1, 172 ms, révision/SHA conformes, neuf chunks | A311 approuvé ; un seul A312 autorisé |
| MOD-155 | 2026-08-29 | 5 / A312–A313 | Replay unique, comparaison A292 et diagnostic de corrélation catalogue | Teste la fonction positive et localise la limite du consensus négatif | Un seul Qwen ; zéro patch/rerun ; inspection backend en lecture seule ; cause serveur précise laissée inconnue | Faux `NotFound`, zéro source, réponse identique A292, 45 872 ms ; F1/F2 lisent la même table avec le même filtre `indexed` | Fonction/provenance/performance rejetées ; sécurité approuvée ; BUG-120 rouvert/confirmé live ; A314 sans modèle requis |
| MOD-156 | 2026-08-29 | 5 / A314 | Préenregistrement causal et inventaire des transitions possibles | Distingue un mécanisme serveur possible d'une cause effectivement observée et corrige après A315 la chronologie endpoint | Aucun serveur avant gel ; aucun modèle/patch ; hypothèses H1–H7 falsifiables ; section de clôture ajoutée sans réécrire le préenregistrement | Gap HTTP réel 140,122 s ; watcher capable en général mais sans trace/état sur Q106 | A314 approuvé comme protocole ; H1 ensuite rejetée pour A312 |
| MOD-157 | 2026-08-29 | 5 / A315 | Diagnostic serveur read-only et expérience query discriminante | Remplace l'hypothèse d'état corrélé par la query exacte réellement produite par le binaire | SSH/SQL/HTTP bornés, aucune mutation/secret/modèle ; comparaison mauvaise phrase/stem contre nom/stem littéraux | Serveur stable ; 0/0 pour mauvaises queries, 1/1 pour bonnes ; extracteur binaire retourne 52 caractères au lieu de 23 | Cause BUG122 approuvée C4 ; renforcer la preuve négative serveur n'est pas le correctif primaire ; A316 extraction+trace |
| MOD-158 | 2026-08-29 | 5 / A316 | Contrat X3 de frontière certaine et trace T1 | Évite d'utiliser l'existence corpus comme sélecteur sémantique et rend la query intake observable | Lexique uniquement syntaxique multilingue ; aucun suffix portfolio, fuzzy, contenu ou secret | Comparaison X0–X4/T0–T3 et portes rouges/vertes figées | X3/T1 approuvés ; patch conditionné au rouge |
| MOD-159 | 2026-08-29 | 5 / A317 | Cleanup de l'enveloppe documentaire et champs de trace | Retire `verbe + déterminant + type` avant un nom explicite, conserve titres autonomes/chemins/guillemets/comparaisons | Cas ambigus non étendus ; Qwen reste fallback et décideur ; trois fichiers historiquement non suivis préservés | Rouge 0/1 ; matrice 12/12 ; routeur/named-doc 151/151 ; binaire exact | BUG122 fermé déterministement, live non exercé |
| MOD-160 | 2026-08-29 | 5 / A318 | Régression, micro-mesure, rapport et libération ressources | Vérifie le produit complet sans assimiler déterministe et live | Aucun modèle/replay/serveur muté ; réflexion incluse dans la mesure ; worktree non nettoyé | 1 473/1 473 ; 10 000 sorties exactes ; diff-check vert ; processus lourds zéro ; Telegram confirmé | Palier A314–A318 approuvé mécaniquement ; phase 5 ouverte, A319 protocole live requis |

---

## 22. Journal des anomalies et régressions

| ID | Date | Phase | Symptôme | Cause prouvée ou hypothèse | Sévérité | Statut | Artefact |
|---|---|---|---|---|---|---|---|
| BUG-000 | 2026-08-07 | Historique | `Pain au chocolat au lait` utilisé comme requête | Fuite d'une hypothèse éphémère | Critique | À reproduire | Dernier progress.log |
| BUG-001 | 2026-08-07 | Historique | 30 ancres initiales refusées | Type `Produit alimentaire` inadapté | Critique | À reproduire | Dernier progress.log |
| BUG-002 | 2026-08-07 | Historique | Writer non atteint en 12 min | Coût cumulé audits + compatibilités | Critique | Confirmé historique | Dernier progress.log |
| BUG-003 | 2026-08-26 | 0 | Les 48 hits bruts de la probe repas n'exposent aucun `matched_content_cards` | À déterminer : absence d'appariement ou projection/contrat de réponse | Haute | Ouvert, à traiter en phase 3 | `retrieval-inventory-report.txt` |
| BUG-004 | 2026-08-26 | 0 | Plusieurs hits de tête sont des conseils ou exemples de menu plutôt que des recettes nommées | Requêtes trop composites et/ou ranking privilégiant des termes de repas | Haute | Baseline confirmée, responsabilité phases 1 et 3 | `retrieval-inventory-report.txt` |
| BUG-005 | 2026-08-27 | 1 | Le routeur grille prend 16–41 s selon les runs avant tout outil et renvoie `repas quotidien` | Décision native coûteuse et type atomique trop abstrait | Critique | Reproduit par probe isolée et EXP-003/P1 | `live-native-router-grid-20260827-000455/report.txt` et `phase1-exp003-baseline-a-p1-rerun.json` |
| BUG-006 | 2026-08-27 | 1 | La revue de colonnes ajoute horaires, aliments et compositions absents de la demande | Revue sémantique des axes réinterprétant les rôles au lieu de préserver les libellés | Haute | Reproduit dans deux chemins | `live-source-backed-agent-v2-tool-choice-20260827-000047/report.txt` et `phase1-exp003-baseline-a-p1-rerun.json` |
| BUG-007 | 2026-08-27 | 1 | La stratégie combinée transforme l'objet en planning et propose plusieurs scopes hors sujet | Contrat/prompt de stratégie instable avec le modèle 4B | Haute | Reproduit mais voie désactivée | `live-source-backed-semantic-granularity-20260827-000011/report.txt` |
| BUG-008 | 2026-08-27 | 1 | La définition accepte des règles terminées par `Les valeurs` ou `Les valeurs ne应` | Validation limitée aux champs et longueurs face à une completion sémantiquement tronquée | Moyenne | Reproduit en isolation et dans EXP-003/P1 | `live-source-backed-semantic-granularity-20260826-235905/report.txt` et `phase1-exp003-baseline-a-p1-rerun.json` |
| BUG-009 | 2026-08-27 | 1 | La baseline A requiert quatre décisions LLM et 81–116 s avant la première frontière outil sur les cinq grids | Le routeur grille supprime l'action initiale, puis le runner exécute successivement rôles, définition et observation | Critique | Confirmé 5/5 | `phase1-exp003-baseline-a-analysis.md` |
| BUG-010 | 2026-08-27 | 1 | P2/P3 transforment des axes et des aliments/horaires inventés en query exécutable | La décision d'observation consomme les enrichissements non sourcés des rôles et de la définition | Critique | Confirmé 2/5 | `phase1-exp003-baseline-a-p2-p5.json` |
| BUG-011 | 2026-08-27 | 1 | Une grille générique Maintenance tombe en `chat.general` après deux routes identiques | Le validateur interdit `opération` dans le type source parce que le même terme est aussi une colonne, même avec un sens documentaire légitime | Haute | Corrigé par EXP-013 ; rouge/vert et deux runs live Maintenance | `phase1-exp013-overlap-*.trx`, `phase1-exp013-non-cuisine.json`, `phase1-exp018-variant-g1-closing-matrix.json` |
| BUG-012 | 2026-08-27 | 1 | Deux clarifications LLM sont rejetées puis remplacées par `chat.general` sans `NeedClarification` | Les deux choix utilisent le même anchor explicite ; après retry, le fallback perd l'intention de clarification | Haute | Corrigé ; G aboutit à une clarification sans outil | `phase1-exp012-variant-g-generic.json` |
| BUG-013 | 2026-08-27 | 1 | C et C.1 exécuteraient une query composée uniquement du planning et de ses axes | Le contrat grille expose encore un champ query libre au même appel qui décrit le livrable | Critique | Confirmé 3/3 runs | `phase1-exp005-exp006-variant-c-analysis.md` |
| BUG-014 | 2026-08-27 | 1 | Le même appel routeur varie de 21 à 43 s sur P1 | Variance runtime/inférence non encore isolée ; 2 541–2 555 tokens prompt et 158–159 tokens completion | Haute | Confirmé, cause à mesurer | `phase1-exp005-exp006-variant-c-analysis.md` |
| BUG-015 | 2026-08-27 | 1 | Quand le contrat grille ne permet plus search, P1 est reclassé en réponse simple pour utiliser le contrat direct | Compétition simultanée entre contrats spécialisés ; le choix de fonction et le choix d'action se biaisent mutuellement | Critique | Corrigé par classification puis contrat spécialisé G | `phase1-exp012-variant-g-p1.json` et matrice G.1 |
| BUG-016 | 2026-08-27 | 1 | L'action G/P1 atteint 20 cartes citables mais surtout du front matter, sans réserve visible de recettes nommées | `documents.content_cards` sans query utilise le mode `ordered`, qui équilibre les documents mais commence à leur première carte | Haute | Corrigé par G.1 representative ; observation intégrée approuvée | `artifacts/live-content-card-inventory-20260827-020042/report.txt` et `phase1-exp017-variant-g1-integrated-p1.json` |
| BUG-017 | 2026-08-27 | 1 | La demande ambiguë aboutit à la bonne clarification sans outil mais nécessite 66,615 s | Le fast-path grille est d'abord tenté, les axes inventés sont rejetés, puis le routeur général clarifie | Moyenne | Ouvert ; correction non bloquante pour l'approbation de l'observation documentaire | `phase1-exp012-variant-g-generic.json` |
| BUG-018 | 2026-08-27 | 2 | L'audit A accepte `Collation (vers 16h)` comme repas et `Pâte levée...` comme préparation particulière | Le juge d'identité LLM 4B ne respecte pas deux frontières explicites dans ce lot ; cause prompt/contexte/modèle à isoler | Haute | Ouvert ; 2 faux positifs manifestes sur 7 approbations | `phase2/exp019-baseline-a-human-review.md` |
| BUG-019 | 2026-08-27 | 2 | Trois préparations nommées acceptées n'ont aucun rôle et les quatre rôles restent à 1/5 | Dans l'ablation A, les définitions de rôle sont réduites aux labels ; chemin runtime d'enrichissement à qualifier avant correction | Critique | Ouvert ; B déficitaire seule donnerait 0 gain sur le lot | `phase2/exp019-baseline-a.json` et revue humaine |
| BUG-020 | 2026-08-27 | 2 | La revue post-observation des rôles ajoute des aliments/compositions absents et coûte 37,113 s | Le contrat existant surcharge le petit modèle ; répétition de BUG-006 malgré le déplacement temporel | Haute | Confirmé ; B.0 rejetée, pas de nouveau patch de prompt autorisé | `phase2/exp020-baseline-b0-human-review.md` |
| BUG-021 | 2026-08-27 | 2 | Les décisions d'identité varient dans les lots listwise selon la rotation | Compétition entre candidats du contrat ordonné avec Qwen3-4B ; D.0 a produit 6/7/5 approbations et des faux positif/négatif dépendants de l'ordre | Critique | Ouvert ; EXP-027 teste le même juge avec batch=1 | `phase2/exp022-d0-permutation-analysis.md` |
| BUG-022 | 2026-08-27 | 2 | Une carte canonique de sous-section est traitée comme si son titre direct était déjà l'objet atomique résolu | `sourceAnchorLabel=title` court-circuite la résolution existante ; J.0 prouve le chemin mais le 4B conserve deux fragments directs | Critique | Confirmé comme mécanisme, insuffisant comme correction | `phase2/exp028-j0-hierarchical-label-resolution-analysis.md` |
| BUG-023 | 2026-08-27 | 2 | Le 4B accepte les mêmes trois mauvais types en mono-candidat direct et hiérarchique | K.0 reproduit exactement les acceptations en Q8_0 ; L.0 ne résout le problème qu'en rejetant tout | Critique | Confirmé ; gate d'identité autonome non sélectionné | Analyses I.0, J.0, K.0 et L.0 |
| BUG-024 | 2026-08-27 | 2 | Le Qwen3-8B natif émet `<think></think>` malgré non-thinking et casse la grammaire JSON Schema | Incompatibilité connue entre template Qwen embarqué et sampler grammar llama.cpp ; kwargs inefficaces | Haute | Contourné pour diagnostic par template builtin ChatML ; produit inchangé | Logs `exp030-l0-8b-*` et sonde structurée |
| BUG-025 | 2026-08-27 | 2 | L.0 rejette les vingt candidats, dont cinq recettes claires | Le Qwen3-8B original sous le contrat I.0 adopte un rejet excessivement conservateur ; taille/post-training non isolables | Critique | Confirmé ; L.0 rejeté, aucune rotation | `phase2/exp030-l0-qwen3-8b-capacity-analysis.md` |
| BUG-026 | 2026-08-27 | 2 | Les frontières de rôles changent après redémarrage malgré température 0 et mêmes entrées | H.0 ne couvrait que trois appels dans le même processus ; le cold-start M.0 produit un autre texte/hash valide | Haute | Confirmé ; H.0 requalifié, hash non contractualisable | Analyses H.0 et M.0 |
| BUG-027 | 2026-08-27 | 2 | La compatibilité pairwise assigne des rôles à six axes/fragments/rubriques quand le gate d'identité est retiré | Le juge de rôle répond à l'affinité du créneau sans appliquer fiablement le type/règle d'instance | Critique | Confirmé ; M.0 rejeté, prompt pairwise gelé | `phase2/exp031-m0-assignment-without-identity-gate-analysis.md` |
| BUG-028 | 2026-08-27 | 2 | Capability B ne charge pas la baseline déterministe des révisions actives | `BuildGeneratedSummaryAsync` demande littéralement `deterministic_v1`, tandis que les dix profils Cuisine actifs sont v5 et le repo exige l'égalité stricte | Critique | Confirmé ; correction isolée interdite avant preuve d'enrichissement | `phase2/backoffice-profile-audit.md` |
| BUG-029 | 2026-08-27 | 2 | Le profil backoffice monolithique actif dépasse le contexte standard avant génération | 25 cartes + 12 sections + 8 extraits + schéma riche donnent 5 094 tokens de prompt pour 4 096 | Critique | Confirmé ; N.0 rejeté, N.1 fenêtré ouvert | `phase2/exp032-n0-live-backoffice-profile-analysis.md` |
| BUG-030 | 2026-08-27 | 2 | Les anciens profils LLM n'apportent aucune carte LLM et la projection peut conserver les autres cartes déterministes bruitées | Profils historiques recopiés ; déduplication SQL limitée au même document/titre/page | Haute | Confirmé par audit ; aucune réactivation large | `phase2/backoffice-profile-audit.md` |
| BUG-031 | 2026-08-27 | 2 | Le contrat fenêtré multi-item décompose une unité principale en étapes et tronque 7/8 JSON | Q5 remplit les quatre items permis avec titres/evidence verbeux ; chaque completion atteint 449–450 tokens | Critique | Confirmé ; N.1 rejeté, ablation 0..1 N.2 enregistrée | `phase2/exp033-n1-windowed-source-card-extraction-analysis.md` |
| BUG-032 | 2026-08-27 | 2 | Une evidence générée sous `maxLength` peut être corrompue et une fenêtre sans hiérarchie reçoit une identité synthétique | La grammaire borne la chaîne sans rendre la copie extractive ; le petit modèle préfère une identité plausible à l'abstention | Critique | Confirmé ; génération libre fermée, sélection d'ancre N.3 ouverte | `phase2/exp034-n2-primary-window-identity-analysis.md` |
| BUG-033 | 2026-08-27 | 2 | Un anchorId SHA-256 complet dépasse 50 tokens et une ligne brute éligible est choisie malgré la règle d'abstention | Encodage de sortie trop long ; le contrat confond evidence brute et ancre structurale | Critique | Confirmé ; N.3 rejeté, N.4 structural enregistré | `phase2/exp035-n3-source-anchor-selection-analysis.md` |
| BUG-034 | 2026-08-27 | 2 | Un champ `headingPath` présent peut être une citation ou un fragment de liste, mais N.4 force sa sélection | Le parseur fournit une structure syntaxique ; le schéma `minItems=1` ne permet aucune abstention sémantique | Critique | Confirmé sur trois fenêtres NIST ; O.0 rejeté, aucun filtre sémantique code | `phase2/exp037-o0-nist-structural-anchor-analysis.md` |
| BUG-035 | 2026-08-27 | 2 | La préférence `highest-level` choisit une note de bas de page promue plutôt que le vrai titre de section | La profondeur du heading est traitée comme priorité sémantique au lieu d'un indice de provenance | Haute | Confirmé fenêtre NIST 1 ; O.1 profondeur-neutre enregistré | `phase2/exp037-o0-nist-structural-anchor-analysis.md` |
| BUG-036 | 2026-08-27 | 2 | Même avec abstention et profondeur neutralisée, le choix listwise conserve le faux niveau 0 NIST | Le 4B ne relie pas fiablement deux ancres concurrentes au texte dans ce contrat court | Critique | Confirmé ; O.1 rejeté, aucune retouche listwise supplémentaire | `phase2/exp038-o1-semantic-abstention-analysis.md` |
| BUG-037 | 2026-08-27 | 2 | Le jugement booléen indépendant rejette quatre vraies ancres sur cinq | Le contrat strict « fenêtre entière » produit un défaut de rejet ; supprimer la compétition déplace le compromis précision/rappel | Critique | Confirmé ; P.0 rejeté, identité offline query-agnostic fermée | `phase2/exp039-p0-independent-anchor-legitimacy-analysis.md` |
| BUG-038 | 2026-08-27 | 2 | Les métadonnées structurelles d'un EvidenceItem ne suivent pas une représentation unique jusqu'à l'observation et aux cartes UI | Champs directs stockés dans `CodeHints`, consommateurs limités à `SelectionHints`, héritage parent des voisins et mapper UI partiel | Haute | Confirmé statiquement ; baseline Q.0 puis TDD requis | `phase2/evidencebundle-vertical-checkpoint.md` |
| BUG-039 | 2026-08-27 | Global | Aucun rapport Telegram envoyé pendant plusieurs jalons significatifs de la session | Cadence de reprise non intégrée comme porte explicite au plan dynamique | Haute | Corrigé opérationnellement à 06:53 ; surveillance horaire active | `phase2/telegram-palier-rattrapage-20260827-0635.txt` et DEC-043 |
| BUG-040 | 2026-08-27 | 1 rouverte | Q019 « ingrédients et réglage » devient une clarification « ingrédients ou réglage » avant tout outil | Le contrat de clarification n'explicite pas que plusieurs facettes demandées ensemble sur le même objet sont cumulatives | Critique | Confirmé live ; EXP-041/R.0 enregistrée avant code | `phase2/exp040-q0-evidence-transport-baseline-analysis.md` |
| BUG-041 | 2026-08-27 | Validation | Le test live question-bank est vert malgré `no_sources`, payload nul et aucune réponse documentaire | Le harnais capture/écrit les flags mais ne les convertit pas en assertions de qualité | Haute | Confirmé ; toujours interpréter les artefacts et ajouter des portes explicites aux expériences | JSONL et TRX EXP-040 |
| BUG-042 | 2026-08-27 | 1 rouverte | Sous R.0, le classifieur Q019 choisit `operational`, puis le routeur général choisit clarification | Pour trois familles sur quatre, le classifieur est advisory : seul `grid` reçoit réellement un contrat spécialisé au second appel | Critique | Confirmé par code et trace live ; architecture à comparer | `phase1/exp041-r0-cumulative-facets-router-analysis.md` |
| BUG-043 | 2026-08-27 | 1 rouverte | Le canary Normes est classé source-backed, reçoit d'abord le scope `Cuisine`, est réparé vers `cat_002`, puis tombe en `chat.general` sans outil | La validation/réparation peut perdre une décision documentaire correcte au lieu de conserver une mission exécutable | Critique | Confirmé live ; cause exacte du fallback à instrumenter | `phase1/exp041-r0-run1.json` |
| BUG-044 | 2026-08-27 | 1 rouverte | La vraie demande ambiguë invente jours et horaires avant de produire une clarification | Le fast-path grid est tenté sur des axes non ancrés ; la clarification n'arrive qu'après échec et réparation | Haute | Confirmé live ; conserver comme canary de sûreté et latence | `phase1/exp041-r0-run1.json` |
| BUG-045 | 2026-08-27 | 1 rouverte | Même sans compétition entre fonctions, le registre composite surclarifie Q019 et le PDF nommé, rate une grille explicite et paraphrase un span | Le petit modèle ne stabilise pas simultanément corpus, layout, actionnabilité et copie extractive dans ce contrat | Critique | Confirmé ; S.0 fermé, aucune retouche composite | `phase1/exp042-s0-typed-intake-ledger-analysis.md` |
| BUG-046 | 2026-08-27 | 1 rouverte | Le champ knowledgeMode classe l'inventaire correctement dans S.0 puis comme corpus lorsqu'il est isolé dans S.1 | Décision du 4B sensible à la forme globale du contrat même avec température 0 et outil unique | Critique | Confirmé ; pré-classifieurs 4B fermés | `phase1/exp043-s1-knowledge-mode-isolation-analysis.md` |
| BUG-047 | 2026-08-27 | 1 rouverte | Différer toute clarification `source_backed` ignore une instruction explicite de clarifier avant recherche et injecte query/scope | `resumeRoute` indique la famille de reprise, pas que l'observation est autorisée avant le choix utilisateur | Critique | Confirmé ; T.0 rollbacké et fermé | `phase1/exp044-t0-source-clarification-after-observation-analysis.md` |
| BUG-048 | 2026-08-27 | 1 rouverte | Le canary Normes atteint `documents.context` mais sa mission garde `Cuisine`, sans continuité vers l'appel | Le scope LLM incohérent est conservé dans mission alors que l'outil context ne le transporte pas | Critique | Confirmé ; responsabilité routeur toujours ouverte | `phase1/exp044-t0-run1.json` |
| BUG-049 | 2026-08-27 | 1 rouverte | Le premier oracle V.0 acceptait des `userTextAnchor` paraphrasés et une catégorie hors registre | Le harness validait nombre/forme des options sans appartenance exacte, et le schéma count laissait categoryPath libre | Haute pour la preuve, aucun impact produit | Contrôleur durci ; live non relancé ; analyse strictement revue à 3/8 sémantique et 6/8 protocole | `phase1/exp046-v0-direct-capability-boundary-analysis.md` |
| BUG-050 | 2026-08-27 | 1 rouverte | Dix preuves génériquement compactées rendent trois reprises W.0 trop grandes pour le Q5/4K | Prompt+six schémas ≈2 189 tokens ; observations de 6 477–7 053 caractères ; serveur compte 4 871–5 173 tokens et refuse avant inférence | Critique pour la variante, aucun impact produit | Confirmé ; trois cas non évalués sémantiquement ; réduction post-run interdite, W.0 rejetée | `phase1/exp047-w0-multiturn-recovery-analysis.md` |
| BUG-051 | 2026-08-27 | 1 rouverte | Le Qwen3.5-9B transporte les outils mais ne franchit ni la qualité ni la latence de la frontière directe | Profil Q4_K_M partiellement GPU : 2/8 sémantique, deux annulations à 45 s, débit proche de 3 tokens/s ; mauvaises routes clarification, ratatouille, PDF et P1 | Critique pour la voie locale 9B, aucun impact produit | Confirmé ; X.0 fermé, Q5 restauré, comparaison d'enveloppes requise | `phase1/exp048-x0-qwen35-9b-direct-capability-analysis.md` |
| BUG-052 | 2026-08-27 | 1 rouverte | Granite 4.2-3B améliore la route mais reste instable/incomplet sur identité, anchors et décisions longues | 4/8 ; ANSI perdu, trois anchors inventés, deux sorties à 256 tokens sans tool call, premier appel 30,493 s | Critique pour les modèles compacts, aucun impact produit | Confirmé ; permutations et substitutions compactes fermées ; décision utilisateur requise | `phase1/exp049-y0-granite42-3b-direct-capability-analysis.md` |
| BUG-053 | 2026-08-27 | Global | Le notifier sort avec code 1 et sans confirmation lorsque le titre de ce palier contient une apostrophe Unicode | Encodage/transport de l'argument de titre dans l'invocation shell ; le corps UTF-8 n'est pas en cause | Faible, reporting seulement | Aucun succès supposé ; titre converti en ASCII, envoi confirmé 1857/1974 ; conserver les titres notifier ASCII | `phase1/telegram-palier-goal-bloque-attente-autorisation-20260827-1020.txt` et PRV-144 |
| BUG-054 | 2026-08-27 | 3 | P4-NORMES-003 conserve ANSI, atteint des preuves pages 22/42, refuse un doublon, mais expire à 180 015 ms sans réponse | Deux transitions de cartes sans nouvelle preuve, appels Qwen3 séquentiels coûteux, récupération tardive puis répétition ; décision d'insuffisance non atteinte dans le budget | Critique pour terminaison et latence, continuité documentaire non en cause | Ouvert comme scénario causal de phase 3 ; aucun patch spéculatif avant protocole | `phase2/d3-document-continuity/scenario-normes-003-transition-x64/` et analyse D3 |
| BUG-055 | 2026-08-27 | 3 / A1–A4 | La mémoire mécanique de rendement ne suffisait pas à faire atteindre une décision terminale sémantique dans le budget | Audit LLM nul/non résolu, dette effacée par une preuve seulement matérielle puis terminal trop tardif | Critique | Résolu progressivement par dette sémantique persistante et terminal Qwen3 borné ; tous les timeouts conservés | Protocoles et analyses A1–A4 |
| BUG-056 | 2026-08-27 | 3 / A5–A6 | Le nom ou l'identité d'un document pouvait être interprété comme le contenu demandé | Forme de l'item documentaire absente du contrat sémantique | Élevé, routage générique | Résolu par type d'objet et modes `named_item` / `content_claim`, sans liste de domaines | Protocoles A5–A6 et tests x64 |
| BUG-057 | 2026-08-27 | 3 / A7 | Le rendement visible ne couvrait que le dernier lot et perdait l'historique utile | Résumé de tour non cumulatif | Élevé, répétition possible | Résolu par mémoire cumulative compacte et bornée | Protocole A7 et canaris associés |
| BUG-058 | 2026-08-27 | 3 / A8 | Une décision terminale JSON pouvait être tronquée avant d'être exécutable | Budget de génération uniforme et réparation non spécialisée | Critique pour honnêteté terminale | Résolu par budget terminal et réparation bornée ; régression Q156 déclenchée puis traitée A9–A13 | Protocole A8 et traces live |
| BUG-059 | 2026-08-27 | 3 / A9–A13 | Q156 choisissait arbitrairement une option malgré plusieurs candidats documentés | Ambiguïté observée insuffisamment publiée et politique de sélection incomplète | Critique pour exactitude | Résolu par revue rapide, portée ouverte et matrice complète ; clarification finale correcte | Protocoles A9–A13 et canaris Q156 |
| BUG-060 | 2026-08-27 | 3 / A14–A16 | Une forme de preuve alternative pouvait être présentée comme clarification utilisateur ou mal routée | Confusion entre question d'intention et prochaine action documentaire | Critique pour boucle/UX | Résolu mécaniquement sur la forme du contrat ; la pertinence reste au LLM | Protocoles A14–A16 et lives P4 |
| BUG-061 | 2026-08-27 | 3 / A17 | Une preuve rejetée restait simultanément dans la file sémantique pending et empêchait le terminal | `semanticallyRejectedEvidenceIds` mis à jour sans retrait de `pendingSemanticCandidateIds` | Critique pour terminaison | Résolu ; test adaptatif initial rouge puis 13/13 x64 vert | `AdaptiveFastReview_BoundsTwoConsecutiveResearchZeroYieldVerdictsWithTerminalDecision` |
| BUG-062 | 2026-08-27 | 3 / A17–A18 | Le premier terminal A17 affichait et mémorisait une explication externe non soutenue par le corpus | Justification libre du LLM réutilisée comme message documentaire | Critique pour honnêteté | Résolu en A18 : justification brute audit-only, sortie/mémoire mécaniquement fondées ; 5/5 et live inspecté | Protocoles A17–A18 et P4 final |
| BUG-063 | 2026-08-27 | 5 / A2–A4 | Q156 affichait correctement `clarify` mais `ConversationMemory.Outcome` valait `insufficient_evidence` | Sérialisation du résultat conversationnel ne distinguait pas ce terminal de clarification | Moyen ; sortie utilisateur correcte, mémoire potentiellement trompeuse | Résolu : terminal `clarification_requested`, régressions mémoire vertes et Q156 live A4 confirmé en 65 365 ms | A2 rouge/vert, A3 régressions, A4 JSONL/TRX |
| BUG-064 | 2026-08-27 | Global / reporting | Le premier appel du palier phase 3 a utilisé le paramètre inexistant `-Body` et envoyé seulement le message par défaut de 25 caractères | Invocation incompatible avec le contrat réel du notifier (`Message`, `MessageFile`, `ReadStdin`) | Faible, reporting seulement | Détecté par l'accusé `Corps=25`, non compté ; renvoi immédiat avec `-MessageFile -RequireMessage`, 2 673 caractères confirmés | Sorties notifier et PRV-165 |
| BUG-065 | 2026-08-27 | 4 / backend | `rag.search` omettait `revisionId` et exposait une empreinte synthétique courte | DTO sans révision et helper de résumé à la place du hash de `document_revisions` | Critique pour identité/clic | Résolu par la révision active et `encode(source_hash,'hex')` ; 2/2, 2 048/2 048, A17 live | PRV-166, PRV-167, PRV-169 |
| BUG-066 | 2026-08-27 | 4 / EvidenceBundle | Une carte de contenu pouvait stocker `contentCardId` sous `ChunkId = content-card:*` | Modèle canonique sans champ spécialisé | Critique pour provenance | Résolu par `ContentCardId` explicite et vérificateur anti-surcharge | A1, A2/A3, A5 |
| BUG-067 | 2026-08-27 | 4 / transport | Ancre, carte et identité réhydratée pouvaient disparaître entre builder, mémoire, payload et UI | Modèles/projections partiels et héritage implicite | Élevé | Résolu par champs typés et continuité exacte ; tests client/navigation/mémoire verts | PRV-167, PRV-168 |
| BUG-068 | 2026-08-27 | 4 / vérificateur | Le vérificateur acceptait une preuve sans révision, hash fort ou identité typée cohérente | Contrat centré sur citation/page, pas identité canonique | Critique | Résolu ; rejets mécaniques ciblés 8/8 | A4/A5 |
| BUG-069 | 2026-08-27 | 4 / launcher | Le clic pouvait ouvrir un fichier par chemin/page sans prouver la révision exacte | Hash optionnel ou non-SHA-256 ignoré | Critique | Résolu par `ResolveExactRevision` et `requireExactSourceHash`; A29 réel vert | A6/A7/A29 |
| BUG-070 | 2026-08-27 | 4 / validation | Une commande sans `-p:Platform=x64` pouvait charger un ancien binaire Debug et produire une fausse observation | Arborescences de sortie standard/x64 distinctes | Élevé pour la preuve, pas produit | A18 classé `INVALIDE_CONFIGURATION`; tous les runs acceptés suivants explicitement x64 et DLL hashée | A18/A18b, analyse phase 4 |
| BUG-071 | 2026-08-27 | 4 / prompt | Un contrat de prompt générique mélangeait label sémantique et valeur exacte de scope | Instruction ambiguë révélée par la suite complète | Élevé, routage générique | Résolu par deux règles explicites sans valeur métier codée ; 2/2 puis suites complètes vertes | A12–A13b |
| BUG-072 | 2026-08-27 | 4 / navigation | `targetAnchorId` ne survivait que dans des hints et n'atteignait pas la preuve exacte résolue | Copie incomplète navigation→executor→builder | Élevé | Résolu par `sourceAnchorId` et affectation unique au chunk exact ; 2/2 | A14/A15 |
| BUG-073 | 2026-08-27 | 4 / normalisation | Le backend live renvoyait la révision mais `NormalizeRagHits` la supprimait avant le bundle | Liste de champs normalisés incomplète | Critique | TDD A19 rouge 0/1, correction A20 1/1, Q011 A21 vert | PRV-170 |
| BUG-074 | 2026-08-27 | 4 / WinUI | Le layout déclarait les sources inline, mais le template assistant ne rendait aucune carte | Panneau global masqué et absence de binding message→`SourcesCardsControl` | Critique pour traçabilité utilisateur | TDD A26 rouge, A27 vert, A28 complet, A29 visible/cliquable | PRV-172, PRV-173 |
| BUG-075 | 2026-08-27 | 5 / A5–A46 | Q011 répondait seulement par la première étape malgré une demande plurielle « les grandes étapes » | Checkpoint d'adéquation acceptait une preuve unique puis les contrats claims/révision permettaient des réponses incomplètes ou fusionnées | Élevé, qualité fonctionnelle | Résolu fonctionnellement en A46 : plusieurs étapes, trois claims localement cités et variantes distinguées ; réserve explicite car le PDF cible caché n'est pas nommé dans la question de banque | A21, A33, A40, A46 et analyses Phase 5 |
| BUG-076 | 2026-08-27 | 5 / A47–A165 | P4-NORMES-001 n'a pas encore sept points fidèlement sourcés dans le délai cible | Les architectures A120–A122 étaient inadéquates ; l'aperçu canonique A165 atteint désormais sept points/sept sources mais conserve une dérive de traduction et un coût élevé | Élevé, fonctionnalité document nommé | Toujours ouvert : A165 est terminal et traçable, mais l'audit strict valide 6/7 et mesure 105698 ms internes | A47–A122, A134, A157–A165 et rapport de palier A165 |
| BUG-077 | 2026-08-27 | 5/6 | P4-NORMES-073 dérive vers IEC 82079 puis dépasse le contexte après compaction | 45 preuves, transition documentaire inadéquate et entrée 4 341 pour contexte 4 096 | Critique multisource/performance | Ouvert ; writer multisource non approuvé, aucun patch local spéculatif | A23 |
| BUG-078 | 2026-08-27 | 5 / A11–A14 | Un manque de contexte sur une preuve visible ne pouvait pas devenir une lecture du même document | Transition `expand_document_context` absente du chemin plat | Élevé, navigation longue | Résolu génériquement par transition typée vers `documents.context` et régressions | A11–A14 |
| BUG-079 | 2026-08-27 | 5 / A15–A20 | Après avoir identifié un manque local, le checkpoint repartait en recherche globale | Contrat d'adéquation ne distinguait pas manque global et manque de contexte même document | Élevé, rendement/latence | Résolu par `submit_flat_evidence_context_gap`, EvidenceId autorisé et gardes mécaniques | A15–A20 |
| BUG-080 | 2026-08-27 | 5 / A21–A28 | Les claims de contenu étaient audités comme des instances nommées et plusieurs claims d'une page étaient supprimés | Contrat unique `named_item` et déduplication par source visible | Critique, complétude | Résolu par mode `content_claim`, comptage EvidenceId et conservation des claims distincts | A21–A28 |
| BUG-081 | 2026-08-27 | 5 / A28–A33 | Le juge final appliquait une taxonomie d'item nommé et ajoutait de la connaissance externe aux claims | Prompt sémantique non mode-aware | Critique, honnêteté | Résolu par juge `content_claim` fondé uniquement sur demande et EvidenceBundle | A28–A32b ; A33 a révélé la cohérence suivante |
| BUG-082 | 2026-08-27 | 5 / A33–A40 | Des citations groupées permettaient une fusion silencieuse de procédures issues de documents différents | Localité atomique et cohérence de provenance insuffisantes | Critique, exactitude | Résolu par citations locales mécaniques et revue sémantique de compatibilité/variantes | A33–A40 |
| BUG-083 | 2026-08-27 | 5 / A40–A46 | `revise` sans EvidenceId rejeté rouvrait la recherche et saturait le contexte | Transition de révision confondue avec nouvelle collecte | Élevé, terminaison | Résolu par révision writer directe sur sélection inchangée, puis vérificateur et second juge | A41–A46 |
| BUG-084 | 2026-08-27 | 5 / A47–A115 | Le checkpoint exige une liste/table déjà présente dans les sources au lieu d'évaluer les claims nécessaires à la synthèse | Confusion entre structure du livrable et forme des preuves | Critique, document nommé et synthèse | Résolu ; les lives A91–A115 évaluent des claims atomiques et non une liste préexistante | A47 analyse, A48 rouge, A49–A52 verts, A115 trace |
| BUG-085 | 2026-08-27 | 5 / A47–A115 | Une réparation du routeur perd un document explicitement reconnu dans le premier contrat | Remplacement complet du contrat au lieu de conservation des champs valides | Critique, portée et vérification finale | Résolu ; A115 conserve `docId/docPath/revisionId/hash` ANSI dans les recherches et la mémoire de preuves | A47 trace, A48 rouge, A49–A52 verts, A115 JSONL |
| BUG-086 | 2026-08-28 | 5 / A53–A58 | Un claim pouvait être compté plusieurs fois ou être confondu avec la forme finale | Pool probant et cardinalité de livrable insuffisamment séparés | Élevé, complétude | Résolu par pool `content_claim` et identités de preuve distinctes | A53–A58 |
| BUG-087 | 2026-08-28 | 5 / A59–A67 | La navigation d'un document nommé restait sur front matter alors que le corps était indexé | Mauvaise capacité pour une recherche de contenu interne | Critique, document nommé | Résolu par transition Qwen vers recherche focalisée transportant l'identité du document | Comparaison A59 et TDD A60–A67 |
| BUG-088 | 2026-08-28 | 5 / A68–A73 | Writer et juge pouvaient alterner des révisions sans rendement | Aucun budget direct spécifique à la sélection inchangée | Élevé, latence/terminaison | Résolu par budget mécanique de révision, retour à l'orchestrateur ensuite | A68–A73 |
| BUG-089 | 2026-08-28 | 5 / A84–A91 | Le tournoi et les preuves dépassaient le contexte 4 096 avant l'action suivante | Fenêtre de travail non compactée pour la transition | Critique, terminaison | Résolu par récupération compacte ; A91 ne présente plus d'overflow | A86 rouge, A87–A90 verts, A91 live |
| BUG-090 | 2026-08-28 | 5 / A74–A91 | Une sélection de première fenêtre restait figée malgré de meilleures preuves ultérieures | Pas de comparaison sémantique incumbent/nouveaux candidats | Critique, exactitude | Résolu par tournoi Qwen incrémental | A74–A83 et A91 |
| BUG-091 | 2026-08-28 | 5 / A92–A97 | Le pool partiel utile était perdu entre verdicts et provoquait des revues répétées | État du checkpoint non conservé comme décision sémantique | Élevé, coût/qualité | Résolu déterministe ; A97 révèle une boucle de transition distincte | A92–A97 |
| BUG-092 | 2026-08-28 | 5 / A98–A104 | Après un gap Qwen valide, la transition sans rendement pouvait rouvrir une sélection writer et laisser la collecte ouverte | État de gap ignoré par la transition mécanique | Critique, réponse prématurée | Résolu ; pool et feedback conservés, sélection interdite tant que le gap est actif | A98 rouge, A99–A103 verts, A104 live |
| BUG-093 | 2026-08-28 | 5 / A104–A110 | Qwen pouvait répéter indéfiniment une action compacte sans choisir le terminal | Décision terminale facultative après absence de rendement | Élevé, latence | Résolu par un dernier essai distinct puis `resolve_source_yield` obligatoire | A105 rouge, A106–A110 verts |
| BUG-094 | 2026-08-28 | 5 / A111–A115 | Un checkpoint tronqué effaçait le dernier gap Qwen valide et autorisait une sélection prématurée | Affectation d'état exécutée même lorsque `ProtocolValid=false` | Critique, fidélité des sources | Résolu : pool, verdict et motif ne changent que sur protocole valide ; A115 termine honnêtement sans writer forcé | Audit A111, A112 rouge, A113/A114 verts, A115 JSONL/TRX |
| BUG-095 | 2026-08-28 | 5 / A116–A120 | Le tournoi demandait une nouvelle recherche après un gap alors que des candidats déjà récupérés n'avaient jamais été montrés à Qwen | Retour immédiat sur `continue` après la première fenêtre de douze éléments | Critique, complétude/latence | Résolu et exercé live : A120 contient deux fenêtres avant la reprise ; le scénario produit reste distinctement ouvert | Audit pool A115, A117 rouge, A118/A119 verts, A120 |
| BUG-096 | 2026-08-28 | 5 / A121–A122 | Le summary flow historique dépasse 4K puis promeut profil/titres tronqués et bibliographie comme matière de synthèse | Lots non budgétés sur le vrai prompt, recherche générique non représentative et voie séparée de l'`EvidenceBundle` | Critique, exactitude/performance | Confirmé ; route historique rejetée comme solution, aucune inversion de priorité appliquée | Rapport A120–A122, result.json et audits pages 1/2/131 |
| BUG-097 | 2026-08-28 | 5 / A157–A158 | Le plafond spécialisé du writer semblait appliqué mais le live produit générait encore avec la borne normalisée de 1600 tokens | Les marqueurs d'aperçu n'étaient reconnus que dans `OpenAiCompatLlmClient`, alors que le produit utilisait `RagChatAgent.LlmAdapter` | Élevé, terminaison/performance | Résolu par budgets 320/64/96 dans le vrai adaptateur et tests miroir | A157, A158, `OpenAiLlmClientTests`, `OpenAiCompatLlmClientTests` |
| BUG-098 | 2026-08-28 | 5 / A165–A181 | Le writer interlangue omet des qualificatifs/conditions et produit une terminologie française faible (`retrainings`, « minimales », perte de `stationary`) | Limite linguistique du Qwen3-4B sur preuves anglaises OCR/techniques ; A166–A172 excluent un simple manque de prompt, puis A178–A181 montrent qu'une séparation en deux passes est instable et trop lente | Critique, exactitude source et qualité de langue | Ouvert : A176 refusé à 4/7 ; la voie deux-passes est rejetée, prochaine mesure = extrait source exact et formulation cible dans un seul appel Qwen | A134, A165–A181, rapports A165, A166–A177 et A178–A181 |
| BUG-099 | 2026-08-28 | 5 / A173–A175 | Le sélecteur 12→7 choisit deux fois `E1,E3…E8`, incluant sommaire/fragments et ignorant la fin | Le Qwen3-4B ne réalise pas la sélection de clarté attendue sur douze IDs, même avec prompt sous 10K caractères | Élevé, complétude/qualité | Variante rejetée et retirée ; rollback A176 vérifié | A173 result.json, A174/A175 JSONL, rapport A166–A177 |
| BUG-100 | 2026-08-28 | 5 / A178–A181 | La normalisation source en lot peut énumérer un tableau jusqu'au plafond et la traduction peut rendre moins de lignes que demandé | Le contrat deux-passes multiplie deux générations probabilistes ; le Qwen3-4B n'observe pas de manière stable les huit lignes et la première passe seule dépasse déjà la cible document nommé | Élevé, terminaison et performance | Variante rejetée sans intégration ; capture brute ajoutée avant validation pour audit | Rapport A178–A181 et artefact A181 |
| BUG-101 | 2026-08-28 | 5 / A182–A183 | Ajouter un extrait littéral double la sortie jusqu'à troncature ; compacter le contexte writer dégrade une condition d'applicabilité | Le Qwen3-4B ne suit pas simultanément copie exacte, traduction fidèle et huit sorties dans la borne ; une vue tête+fin crée une discontinuité sémantique | Élevé, fidélité et terminaison | Variantes rejetées ; produit rollbacké sur A176 | Artefacts A182/A183 et rapport A182–A186 |
| BUG-102 | 2026-08-28 | 5 / A184–A186 | Des passages structurellement propres réduisent la latence mais `should`, conditions et termes techniques dérivent encore | La mauvaise structure des preuves est un facteur secondaire ; la limite interlangue persiste sur des paragraphes propres et en deux passes | Critique, exactitude source | Ouvert ; A185 égale seulement A176 à 4/7 et A186 n'est pas terminal | Artefacts A184–A186 et rapport A182–A186 |
| BUG-103 | 2026-08-28 | 5 / A187–A189 | Le routeur émet trois appels `overview` pour les trois facettes d'un unique résumé nommé ; le contrat mono-appel les rejetait puis expirait | Décomposition sémantique légitime incompatible avec l'enveloppe d'exécution mono-livrable | Critique, terminaison | Garde mécanique résolue en A189 ; latence routeur 60022 ms et fonctionnalité du résumé restent ouvertes | A187–A189 et rapport A187–A191 |
| BUG-104 | 2026-08-28 | 5 / A189–A190 | Le pool page-stratifié contient un sommaire mal typé comme `content` et aucun passage établissant la finalité ou l'usage citationnel demandés | Échantillonnage uniforme aveugle aux facettes de la question ; métadonnée `contentRole` insuffisante pour exclure le bruit de navigation | Critique, pertinence et complétude | Ouvert ; premier défaut transversal à traiter avant tout nouveau patch writer | A190 result.json, SHA `97F3D93B...`, rapport A187–A191 |
| BUG-105 | 2026-08-28 | 5 / A189–A191 | A189 rejette le writer avec deux candidats et zéro valide, alors que quatre passages français propres produisent quatre faits fidèles en A191 | Le mélange d'une zone de sommaire, d'un pool ne couvrant pas la demande et d'un contrat multi-lignes rend le writer instable ; l'hypothèse d'une incapacité générale française est réfutée | Élevé, rédaction/diagnostic | Ouvert au niveau acquisition et contrat de synthèse ; ne pas corriger par budget ou traduction sans nouvelle preuve | A189 JSONL, A190/A191 result.json |
| BUG-106 | 2026-08-28 | 5 / A192–A197 | Les facettes sémantiques exactes émises par le routeur étaient supprimées pendant la coalescence | Le transport canonique conservait seulement document, compte et pool, avec `query=""` | Critique, adéquation de la recherche | Résolu mécaniquement en A197 ; test exact et `107/107` | Test routeur et rapport A192–A201 |
| BUG-107 | 2026-08-28 | 5 / A192–A198 | L'échantillonnage uniforme ne trouve pas de manière fiable portée, structure et usage d'un document nommé | Position de page n'est pas une décision de pertinence ; navigation structurelle expose les ancres mais exige un selector sémantique | Critique, couverture | Architecture candidate démontrée test-only par A193/A198 ; non intégrée tant que writer non validé | Artefacts A192/A193/A198 |
| BUG-108 | 2026-08-28 | 5 / A196–A200 | Les writers textuels mélangent une preuve par ligne, facettes utilisateur et syntaxe de citation | Contrat partagé favorise fuite inter-facettes ; contrat isolé reste verbeux et tronqué | Critique, fidélité/terminaison | Variantes A196/A199/A200 rejetées ; appel d'outil structuré évalué ensuite | Artefacts A196/A199/A199b/A200 |
| BUG-109 | 2026-08-28 | 5 / A201 | Qwen sait marquer une réponse comme inférence pratique tout en la formulant comme obligation non sourcée | Structure valide ne garantit pas la modalité sémantique de la phrase | Critique, exactitude source | Ouvert ; prochaine frontière = Evidence Judge Qwen structuré, aucune réécriture lexicale code | A201 SHA `D1175D4...` et rapport A192–A201 |
| BUG-110 | 2026-08-28 | 5 / A202 | Le Judge Qwen structuré peut tronquer son outil et approuver une modalité contradictoire avec ses propres champs | La contrainte de schéma ne fournit pas la capacité d'adjudication sémantique ; F3 combine obligation, inférence et `supported_direct` | Critique, arbitre final | Branche fermée ; aucune réparation ou hausse de budget ; comparer fallback extractif sûr et insuffisance honnête | A202 SHA `72DA4427...` |
| BUG-111 | 2026-08-28 | 5 / A204 | Les facettes sont présentes dans le `RouterPlan` mais absentes dans `ExecRagEvidenceOverviewSummaryAsync` | Le flux résumé reconstruisait `rag.summarize_live` depuis le message et le document, sans reprendre les arguments planifiés | Critique, contrat end-to-end | Résolu par pont explicite et test ; A204 non probant, A204b trace `facet_selector.accepted` | A204/A204b JSONL et test `Router_driven_summary_flow_preserves_planned_overview_facets` |
| BUG-112 | 2026-08-28 | 5 / A204b–A205b | Le writer local échoue encore sur obligation ou langue après sélection de preuves adéquates | Limite sémantique Qwen3-4B déjà observée ; le fallback n'essaie pas de masquer ou corriger ces décisions | Critique, synthèse fonctionnelle | Ouvert pour la synthèse ; impact sûr borné par terminal extractif explicitement non synthétique | A204b/A205b JSONL et rapport A203–A205 |
| BUG-113 | 2026-08-28 | 5 / A204b–A205b | Le terminal sûr reste long et peut inclure une preuve de garde/sommaire bruitée | Le selector choisit parmi des ancres structurelles dont le libellé peut annoncer un contenu sous-jacent partiellement bruité | Moyenne, utilité/lecture | Accepté seulement pour le terminal de secours ; ne pas ajouter de filtre sémantique code sans nouvelle expérience | Réponses Q016/Q019, 3875/3807 caractères |
| BUG-114 | 2026-08-28 | 5 / A206–A208 | Le second étage général répétait trois appels `overview`, 382 tokens et environ 53 s sur Q016 | Contrat général trop large pour un aperçu multi-facettes déjà classifié | Élevé, latence | Résolu par contrat Qwen spécialisé ; 104 tokens, ~16 s, -26,02 % end-to-end sans perte source | A206 JSON et A208 JSONL ; A207 retenu |
| BUG-115 | 2026-08-28 | 5 / A209–A225 | Q016 a d'abord rendu les profiteroles comme fondant puis, après A214, une clarification injustifiée sans source | La revue Qwen avait choisi E3/E17 mais son `answer` trop long était jeté ; le carry initial tronquait ensuite E3 | Critique, exactitude source-backed | Causes corrigées déterministement par redirection vers writer et fenêtre six ; reste ouvert live sans rerun Q016 | A211 SHA `7D06BAC2...`; A218 SHA `C74A4276...`; rapport A221–A225 |
| BUG-116 | 2026-08-28 | 5 / A218–A225 | Trois anciens candidats E1/E2/E3 étaient conservés, mais la seconde revue ne voyait que E1/E2/E40 | Politique deux anciens + nouveaux + reliquat, suivie de `Take(3)` | Critique, continuité sémantique | Fermé déterministe : carry d'abord et fenêtre jusqu'à six ; E1–E6 visibles | T2 rouge/vert, 16/16 et 1393/1393 |
| BUG-117 | 2026-08-29 | 5 / A226–A289 | Une question factuelle binaire sur un document absent était transformée en clarification de portée utilisateur | L'outil enum exposait des transitions incohérentes après `NotFound`; R+V empêchait la substitution sans guider l'alphabet de décision | Critique, routage sémantique | Fermé live pour le cas absent : A288 choisit insuffisance en une tentative, sans clarification/retrieval/source ; la famille positive reste distinctement non approuvée | A288 JSONL SHA `03B310FC...54ECC` ; rapport A289 SHA `2A3752AF...0E2B6` |
| BUG-118 | 2026-08-28 | 5 / A261–A279 | Un document nommé absent de l'index était remplacé par des homonymies thématiques, puis une affirmation lui était attribuée sans source | Le nom survivait jusqu'à l'intake, mais l'action `rag.search` initiale partait sans résolution ; les voisins n'étaient pas filtrés avant le juge et la clarification sans EvidenceId contournait le vérificateur | Critique, exactitude et honnêteté | Fermé live pour le cas absent : NotFound complet avant retrieval, action quarantinée, zéro outil/homonyme/source/attribution ; garde déterministe conservée | PRV-176 à PRV-185 ; A278 TRX/JSONL ; rapport A276–A279 |
| BUG-119 | 2026-08-29 | 5 / A288–A318 | Le terminal d'insuffisance A312 dit « sources consultées » avec zéro retrieval et une raison Qwen fondée sur le faux `NotFound` | Pour Q106, le terminal est un effet aval de BUG122 : mauvaise référence intake, zéro catalogue, donc aucune recherche. Le wording générique reste une réserve UX distincte pour de vrais absents | Moyen, précision UX/honnêteté | **Expliqué causalement, ouvert live** : aucun patch wording opportuniste ; le replay A319 doit montrer si la branche est évitée. Si un vrai absent réutilise encore « consultées » sans outil, ouvrir un rouge autonome | A312 JSONL `3C739B47...0AE92` ; cause C4 A315 ; rapport A318 `85EEA212...9658907B` |
| BUG-120 | 2026-08-29 | 5 / A290–A318 | Q106 exact, indexé et résolu au préflight est déclaré `NotFound` complet pendant A292 puis A312 | **Cause réaffectée à BUG122** : A311 injectait directement le bon nom ; A312 extrayait une phrase de 52 caractères. Serveur stable et matrice contrôlée 0/0 mauvaise query, 1/1 bonne query. La corrélation F1/F2 est réelle mais non causale ici | Critique, faux négatif et indisponibilité fonctionnelle | **Corrigé déterministement, reste ouvert jusqu'au replay live unique** : binaire exact, 12/12, 151/151, 1 473/1 473. L'hypothèse de transition serveur/accord négatif indépendant est retirée pour Q106, sans déclarer tous les états transitoires couverts | A315 matrice `33E5867B...491B65CA` ; A317 rapport `DC77383A...5271B244` ; A318 `85EEA212...9658907B` |
| BUG-121 | 2026-08-29 | 5 / A300–A313 | Le candidat Q106 `Resolved` portait `SourceHash=c62f...` 32 hex et aucune révision, tandis que le contexte/preuves canoniques portent SHA-256 `909173...` et révision `a2d922d5...` | Digest de fraîcheur résumé F1 assimilé à une identité de révision ; corrigé par sanitisation catalogue puis hydratation active post-consensus exact unique | Critique, chemin positif et provenance | **Fermé sur l'axe mécanique et le préflight backend réel, non exercé dans le live A312** : rouge 13/18, vert 18/18, full 1 463/1 463 et A311 positif ; A312 n'a produit aucun candidat à hydrater | Protocole final `E593D134...A4A374` ; full `A8291988...3BCA0E` ; A311 `B181748A...306B` ; rapport A313 |
| BUG-122 | 2026-08-29 | 5 / A314–A318 | Une demande contenant un fichier explicite non guillemeté est résolue avec le préfixe impératif de la phrase (`Retrouve exactement la fiche ...pdf`) au lieu du nom littéral | Regex bare trop permissif sur les espaces ; cleanup sans frontière `verbe + déterminant + type documentaire` ; préflight direct A311 contournant l'intake | Critique, faux `NotFound` avant retrieval sur documents nommés | **Fermé déterministement, live à confirmer** : cause C4, rouge exact, règle FR/EN/DE/ES/PT/IT, contre-exemples, trace, 12/12, 151/151, 1 473/1 473 et 10 000 sorties binaires exactes | Ancien DLL `32A68729...DC2785` ; nouveau `8287524E...279DA3D` ; rapport A318 `85EEA212...9658907B` |

---

## 23. Fiche obligatoire pour chaque test ou benchmark

Copier ce bloc avant chaque exécution significative :

```text
ID :
Date/heure :
Phase :
Objectif :
Hypothèse testée :
Question/entrée exacte :
Corpus/révision :
Branche/commit/source hash :
Binaire/hash :
Modèle/hash/quantification :
Backend d'inférence :
Contexte/KV/parallélisme :
Variables d'environnement non secrètes :
Commande exacte :
Timeout :
Résultat mécanique :
Résultat sémantique :
Inspection humaine :
Durée totale :
Appels LLM par phase :
Prompt/completion/cache tokens :
Outils et arguments :
Nombre de preuves observées/auditées/acceptées/refusées :
Couverture/matching :
Sources résolubles :
Artefacts :
Verdict : APPROUVE | REJETE | INCONCLUANT | TESTE_NON_APPROUVE | BLOQUE
Motif du verdict :
Décision suivante :
```

---

## 24. Fiche d'approbation d'une phase

Une phase ne change vers `APPROUVEE` qu'après remplissage de cette fiche :

```text
Phase :
Date d'approbation :
Résumé des changements :
Tests déterministes :
Probes réelles :
Runs live :
Inspection humaine :
Métriques obtenues :
Artefacts :
Variantes rejetées :
Risques résiduels :
Régressions ouvertes :
Pourquoi les critères sont tous satisfaits :
Autorisation de passer à la phase suivante : OUI | NON
```

---

## 25. Protocole de mise à jour du registre

À chaque séance de travail :

1. mettre à jour la date en tête ;
2. vérifier le tableau de pilotage ;
3. placer une seule phase en `EN_COURS` ;
4. créer les entrées d'expérimentation avant exécution ;
5. enregistrer les modifications produit ;
6. lier les artefacts dès leur création ;
7. consigner aussi les essais rejetés ;
8. mettre à jour les anomalies ;
9. remplir la fiche d'approbation ;
10. passer à la phase suivante uniquement si l'autorisation vaut `OUI`.

### 25.1 Reporting Telegram obligatoire

Outil canonique :
`C:\Users\MBirchler\Desktop\ecom\SAAIA\SAAIA - Notifier\notify-codex.ps1`.

Règles :

1. pendant le travail actif, envoyer un point environ toutes les 60 minutes ;
2. envoyer immédiatement lorsqu'un jalon change le diagnostic, le verdict, la
   phase, l'architecture retenue ou le prochain blocage ;
3. envoyer avant et après un live long si un résultat important a été obtenu
   depuis le rapport précédent ;
4. ne pas envoyer chaque petit test ; consolider objectif, actions, preuves,
   mesures, verdict honnête, problème restant et prochaine décision ;
5. écrire le corps dans un artefact UTF-8, contrôler les marqueurs de mojibake,
   puis utiliser `-MessageFile -RequireMessage` ;
6. ne jamais afficher `TELEGRAM_BOT_TOKEN` ni `TELEGRAM_CHAT_ID` ;
7. enregistrer chemin, heure, SHA-256, taille, nombre de parties et résultat du
   notifier dans le journal/preuves ;
8. si l'envoi échoue, le tracer immédiatement et le retenter une fois après
   correction strictement technique ; ne jamais prétendre qu'il est parti ;
9. avant une expérience longue, vérifier si le dernier rapport date de plus de
   60 minutes ou si un jalon non notifié existe ; dans ce cas, suspendre le run
   jusqu'au rapport.

En cas d'interruption :

- laisser la phase `EN_COURS` ou `TESTEE_NON_APPROUVEE` ;
- écrire le dernier point stable ;
- lister les processus encore actifs ;
- donner la prochaine commande exacte ;
- ne jamais écrire « terminé » si une preuve manque.

---

## 26. Point d'exécution antérieur clos par le réalignement

Phase responsable : **Phase 0 — réalignement, inventaire des preuves et
baseline reproductible**.  
État opérationnel : **EN_COURS — aucun blocage utilisateur ou externe**.  
Phases 1 à 8 : **NON_DEMARREES sous le nouveau Goal**, preuves historiques
conservées comme candidates à revalidation.

Décision de réalignement du 2026-08-27 :

1. le Goal actif est désormais
   `GOAL-2026-08-27-SAAIA-RAG-PRODUIT-END-TO-END.md` ;
2. le présent registre est subordonné au Goal et ne peut plus le redéfinir ;
3. le faux blocage sur une enveloppe distante est annulé ;
4. les comparaisons GPT-5.6, matériel et fine-tuning restent archivées comme
   options facultatives, non autorisées et non bloquantes ;
5. aucun appel distant, achat, téléchargement de modèle ou entraînement n'est
   nécessaire pour poursuivre le travail local ;
6. les expériences antérieures conservent leur valeur probatoire exacte, mais
   aucune de leurs solutions locales n'est présélectionnée ;
7. la priorité redevient la chaîne utilisateur complète : question →
   orchestration → outils → retrieval → `EvidenceBundle` → writer →
   vérification → `sourcesPayload` → WinUI ;
8. le premier choix d'outil ne sera de nouveau prioritaire que si la baseline
   verticale démontre qu'il est le goulot causal principal ;
9. le stress-test Cuisine 5 × 4 reste obligatoire en validation finale mais ne
   dicte plus l'ordre du chantier ni l'architecture ;
10. le worktree existant est préservé ; aucune restauration, suppression ou
    opération Git globale n'est autorisée.

Prochaine séquence locale :

1. archiver le nouveau Goal et le présent réalignement avec leurs hashes ;
2. capturer branche, HEAD, divergence, statut, diff stat/numstat et
   `git diff --check`, sans modifier les changements existants ;
3. vérifier le runtime Qwen3 réellement actif, son modèle, son contexte, ses
   propriétés et son hash ;
4. vérifier la santé du backend et les compteurs du corpus/révisions sans
   réingestion ni mutation ;
5. identifier le binaire client et les configurations réellement utilisés par
   les probes actuelles ;
6. inventorier les preuves historiques par exigence du nouveau Goal et les
   classer `VALIDE_ACTUELLE`, `VALIDE_HISTORIQUE`, `A_REVALIDER` ou `INVALIDE` ;
7. approuver la phase 0 seulement si la photographie est reproductible et
   qu'aucune preuve périmée n'est présentée comme actuelle ;
8. ouvrir ensuite la phase 1 avec plusieurs demandes représentatives et une
   trace verticale locale complète jusqu'au payload UI ;
9. envoyer un rapport Telegram clair à l'approbation du réalignement et de la
   baseline, sans utiliser les identifiants internes comme explication.

---

## 27. Journal d'exécution sous le Goal produit du 2026-08-27

### 27.1 Phase 0 — Réalignement et baseline

État : **APPROUVEE le 2026-08-27 à 10:58 Europe/Zurich**.

Actions et preuves :

- Goal actif archivé dans
  `GOAL-2026-08-27-SAAIA-RAG-PRODUIT-END-TO-END.md`, SHA-256
  `0E8C97FDC7DCE269905D483D81EBDF81AFB72710B6E15DABD4A5E668EF732757` ;
- faux blocage distant supprimé ; branches GPT-5.6, matériel et fine-tuning
  reclassées facultatives et non bloquantes ;
- historique complet conservé sans sélection anticipée d'une solution ;
- Git capturé sur `SAAIA_V3.1`, HEAD `5f35881c`, 15 commits d'avance,
  worktree existant préservé ;
- build client Debug/x64 réussi en 2 min 35,64 s, 0 avertissement et 0 erreur ;
- tests client ciblés : 244/244 ;
- tests client déterministes complets : 1 264/1 264 ;
- tests backend : 2 046/2 046 ;
- Qwen3-4B Q5 exact sain, contexte 4 096, hashes runtime et modèle inchangés ;
- backend et provenance canonique prêts ;
- probe cartes : 890 cartes v5, 120 preuves matérialisées ;
- probe navigation : total 3 290, 200/200 ancrées, 60 chunks résolus ;
- probe retrieval : 34 preuves, quatre hashes identiques à la baseline ;
- aucun processus de test résiduel ;
- inventaire de validité des preuves terminé sans transformer une preuve
  historique en validation produit actuelle.

Artefact d'approbation :
`artifacts/goal-rag-product-20260827-1041/phase0/phase0-realignment-baseline-and-proof-inventory.md`  
SHA-256 :
`03BC96717ABC422D0F4A9F47E754568EEA677009B19AE6E61FB813C6DF49D842`.

Rapport Telegram : envoi confirmé en une partie, corps 2 063 caractères, total
2 186 caractères. Fichier :
`artifacts/goal-rag-product-20260827-1041/phase0/telegram-palier-realignement-et-baseline-approuves-20260827-1058.txt`  
SHA-256 :
`AAF8D3E53DF9C3F197511C872D71BEEC3ADB50364807E8DC7A47266BC00CDA22`.

### 27.2 Phase 1 — Baseline verticale locale complète

État : **APPROUVEE COMME BASELINE DIAGNOSTIQUE LE 2026-08-27 À 11:20**.

Objectif : exécuter sur le binaire figé plusieurs demandes représentatives et
tracer, sans modification produit préalable, les frontières suivantes :

1. question et contexte utilisateur ;
2. décision d'orchestration Qwen3 ;
3. appels et arguments d'outils ;
4. observations documentaires et retrieval ;
5. construction et évolution de l'`EvidenceBundle` ;
6. handoff au writer et réponse structurée ;
7. vérification mécanique des citations ;
8. `sourcesPayload` final transmis à l'interface.

Matrice minimale de scénarios :

- question factuelle simple source-backed ;
- demande visant explicitement un document connu, dont Q019 ;
- demande nécessitant plusieurs informations cumulatives ;
- demande ambiguë qui doit clarifier avant une action irréversible ou
  matériellement mal définie ;
- demande non Cuisine ;
- stress-test structuré Cuisine 5 × 4, exécuté seulement après les scénarios
  courts afin de ne pas masquer leur diagnostic.

Critères d'approbation :

- artefact par scénario avec chronologie, outils, preuves, réponse et sources ;
- localisation de chaque perte ou transformation d'identité ;
- distinction explicite entre échec d'orchestration, retrieval, transport,
  writer, vérification et payload UI ;
- aucune conclusion fondée uniquement sur le premier appel ;
- aucune modification produit avant gel du diagnostic causal ;
- choix de la phase 2 fondé sur le premier défaut end-to-end démontré et sur
  son impact à travers les scénarios.

Exécution et verdict :

- instrumentation test-only étendue dans
  `LiveQuestionBankAgentValidationTests.cs` pour capturer `AnswerSource` et
  `LastRagTraceEvents` ; aucun code produit modifié avant les runs ;
- Q011 : `180 007 ms`, bon PDF/page trouvé, puis boucle multi-preuves et timeout
  avant writer ;
- Q019 : `27 884 ms`, clarification abusive « ingrédients ou réglage », zéro
  outil/source ;
- Q156 : `35 385 ms`, chaîne `rag.search -> EvidenceBundle -> writer ->
  SourceVerifier -> sourcesPayload` aboutie, mais choix arbitraire parmi trois
  recettes ;
- P4-NORMES-001 : `180 007 ms`, demande ANSI envoyée à `Cuisine`, 80 cartes du
  mauvais corpus auditées, réponse vide ;
- les faux départs Q019 de harnais ont tous échoué avant appel produit et sont
  explicitement exclus du corpus de preuve.

Rapport consolidé :
`artifacts/goal-rag-product-20260827-1041/phase1/phase1-vertical-baseline-results.md`.

La phase est approuvée uniquement parce qu'elle a atteint son objectif de
diagnostic vertical. Elle n'approuve ni la qualité produit, ni les délais, ni le
clic WinUI réel.

### 27.3 Phase 2 — Orchestration Qwen3 adaptative et généraliste

État : **APPROUVEE** le 2026-08-27.

Ordre causal figé depuis les quatre runs :

1. `D1` — portée/catégorie : expliquer et corriger la sélection générique du
   mauvais corpus ;
2. `D2` — clarification/adéquation : exécuter les facettes cumulatives claires
   et clarifier la pluralité réellement observée ;
3. `D3` — continuité documentaire et contrat de preuves : exploiter le document
   pertinent avant tout élargissement coûteux.

Contraintes :

- Qwen3 reste le décideur sémantique final ;
- aucun mot-clé de domaine, nom de document, ID de question ou langue n'est
  codé en dur ;
- les garde-fous code restent mécaniques, contractuels et traçables ;
- chaque incrément commence par un diagnostic causal et un test déterministe
  du contrat transversal ;
- chaque modification est rejouée en live sur le scénario causal puis sur un
  canari d'un autre domaine avant approbation.

État final au 2026-08-27 à 13:39 : **APPROUVEE**.

- `D1` **APPROUVÉE sur sa responsabilité** : une portée non publiée n'est plus
  crue, la recherche ANSI reste ouverte et atteint le bon document à 21,892 s ;
  rapport `phase2/d1-scope-diagnostic/d1-scope-correction-analysis.md`.
- `D2` **APPROUVÉE fonctionnellement** : le checkpoint Qwen3 choisit entre
  sélectionner, clarifier et continuer ; Q156 termine 3/3 avec deux options et
  deux sources vérifiées. Les durées 87 129 / 73 828 / 73 366 ms, le clic WinUI
  non exercé et `revisionId: null` restent explicitement non approuvés ; rapport
  `phase2/d2-adequacy/d2-adaptive-flat-adequacy-analysis.md`.
- `D3` **APPROUVÉE sur sa responsabilité** : Qwen3 choisit explicitement
  `GLOBAL` ou l'EvidenceId d'un document visible ; le code conserve alors
  `docId`, `docPath` et `docRef`, ou laisse volontairement la recherche globale.
  Contrat 3/3 sur le binaire x64 live, P4-NORMES-001 vérifié sur ANSI en
  90 675 ms et canari Q156 vérifié en 101 235 ms ; rapport
  `phase2/d3-document-continuity/d3-document-continuity-analysis.md`.
- non-régression D1+D2+D3 : 203/203 et `git diff --check` ciblé propre.

Artefact d'approbation :
`phase2/phase2-orchestration-qwen3-approval.md`.

Limites non transférées en succès : latences 90–101 s, timeout P4-NORMES-003 à
180 015 ms, `revisionId: null`, cartes/clic WinUI non validés.

### 27.4 Phase 3 — Recherche multiétape et mémoire de rendement

État : **APPROUVEE** le 2026-08-27.

Entrée causale : P4-NORMES-003. Le document ANSI est conservé depuis
`documents.context` jusqu'à `documents.navigation`, deux appels de cartes ne
produisent aucune nouvelle preuve, une navigation tardive atteint ANSI pages 22
et 42, l'appel identique suivant est refusé, puis le run expire sans réponse à
180 015 ms.

Prochaine action exacte : préenregistrer un protocole de mémoire de rendement et
de terminaison honnête. Le premier test rouge doit prouver que Qwen3 reçoit un
résumé compact des routes déjà tentées, de leur rendement et des doublons, puis
peut décider explicitement entre une action réellement nouvelle, une réponse
partielle sourcée, une clarification ou une insuffisance honnête. Le code ne doit
ni décider du sens à la place du LLM, ni réessayer une route épuisée, ni attendre
le timeout externe pour terminer.

Réalisation complète : A1–A18. Le détail causal, tous les runs non approuvés,
les contrats conservés et les hashes finaux sont dans
`artifacts/goal-rag-product-20260827-1041/phase3/phase3-yield-memory-termination-analysis.md`.

Validation finale :

- 220/220 déterministes agent v2 + routeur natif sur `win-x64` ;
- P4-NORMES-003 termine honnêtement en 97 048 ms après deux verdicts LLM sans
  rendement et une décision terminale explicite ;
- Q156 clarifie en 39 281 ms après observation de trois candidats ;
- aucune justification libre du terminal n'est affichée ou mémorisée comme fait
  documentaire ;
- `git diff --check` sans erreur.

Limites transférées, pas masquées : P4-NORMES-001 positif, latence cible 45 s,
`revisionId`, WinUI/clic source et `Outcome` mémoire de clarification.

### 27.5 Phase 4 — Intégrité verticale EvidenceBundle / writer / UI

État : **APPROUVEE sur sa responsabilité** le 2026-08-27.

Première action : figer une matrice de continuité d'identité sur plusieurs
frontières (`docId`, `revisionId`, `docPath`, page, `chunkId`, `anchorId`,
`contentCardId`, `EvidenceId`, hash), puis rejouer un scénario positif simple,
un document nommé, un multi-source et une clarification. La première preuve
doit montrer où chaque identité entre, se transforme ou disparaît entre
retrieval, EvidenceBundle, writer, vérificateur, `sourcesPayload`, carte WinUI
et ouverture du document. Aucun correctif n'est appliqué avant cette mesure.

Résultat final : continuité canonique de `docId`, `revisionId`, hash, chemin,
page, chunk, ancre et carte jusqu'au `sourcesPayload`, au rendu WinUI et au clic
exact démontrée. Les régressions finales sont backend `2048/2048`, transversales
`240/240` et client x64 `1307/1307`. La complétude du document nommé, le
multisource et la performance restent transférés à la Phase 5/6.

### 27.6 Phase 5 — Fonctionnalité, mémoire et généricité

État : **EN COURS** au 2026-08-29 après verdict live A289.

Acquis actuels :

- mémoire de clarification correcte ;
- Q011 fonctionnel avec réserve explicite de banque ;
- contrat générique `content_claim` et citations locales ;
- document nommé conservé et recherche focalisée sur son corps ;
- tournoi sémantique, compaction 4K et pool partiel conservé ;
- transitions sans rendement bornées et terminales sous décision Qwen ;
- un résultat de checkpoint invalide ne peut plus écraser une décision Qwen
  valide ;
- aperçu canonique page-stratifié construit par `documents.context` et transporté
  dans l'`EvidenceBundle` ;
- pool huit-pour-sept, writer Qwen, vérification mécanique, sélection Qwen limitée
  aux candidats valides et réparation unique bornée ;
- budgets du vrai adaptateur produit : writer `320`, sélecteur `64`, réparation
  `96` tokens ;
- traces `document_overview.writer`, `selector` et `candidate_repair` ;
- suites de référence antérieures : `10/10`, `181/181`, `1323/1323` ; état final
  après rollback : aperçu `14/14`, aperçu+adaptateurs `45/45`, client x64
  `1353/1353`.

Verdict courant P4-NORMES-001 :

- A120 reste la borne sûre mais non fonctionnelle de l'ancienne architecture à
  `538559 ms` ; A121/A122 ont conduit au rejet du summary flow historique ;
- A134 prouve qu'une lecture canonique de huit zones page-stratifiées est
  possible avec identités complètes ;
- A157–A164 ont successivement exposé le mauvais adaptateur de tokens, les
  obligations inversées, les placeholders, le mauvais ordre sélection/writer et
  l'échec d'une réparation isolée ;
- le contre-audit corrige A165 de 6/7 à 5/7 : E5 perd la condition des tâches
  assignées et E6 perd `stationary`/la structure du tableau ;
- A166/A167 prouvent que davantage d'instructions et un audit candidat/source
  global ne modifient pas la sortie ;
- A168–A172 excluent les correcteurs locaux testés : 4B/Granite restent faibles,
  8B/9B et Q8 dépassent les bornes de cette configuration ;
- A173 matérialise douze zones en `842 ms`, mais A174/A175 montrent que Qwen
  sélectionne deux fois le même préfixe et le writer termine sûrement sans
  réponse ; cette variante est retirée ;
- A176 prouve le rollback : sept points/sept cartes sur les pages 9, 43, 60,
  76–77, 93, 110 et 127 ;
- l'audit strict A176 approuve E1, E3, E4 et E7 ; rejette E5, E6 et E8 ;
- verdict fonctionnel : `TESTEE_NON_APPROUVEE`, soit 4/7 et aucun succès de la
  future série de trois runs ;
- verdict traçabilité : approuvé pour A176 ; verdict rédaction/performance : non
  approuvés ;
- latence A176 : routeur `29076 ms`, writer `56316 ms`, sélecteur `12293 ms`,
  tour interne `98368 ms`, banc avec démarrage `112135 ms` ;
- A178–A181 rejettent la séparation normalisation/traduction : trois troncatures
  source sur quatre runs, une traduction à sept lignes, et A181 consomme
  `72528 ms` pour la seule passe source avant toute traduction ;
- A182 rejette l'extrait littéral + français en un appel : 6/8 lignes au
  plafond de 480 jetons ;
- A183 rollbacke la compaction tête+fin, régressive à 3/7 ;
- A184/A185 prouvent qu'un pool structurellement propre réduit le writer à
  `44991/38379 ms`, mais plafonne encore à `4/8` puis `4/7` strict ;
- A186 combine pool propre et deux passes : source complète en `33300 ms`,
  traduction tronquée 6/7 à `39783 ms` et dérives de modalité persistantes.
- A187/A188 exécutent le canari français Q016 hors Normes : Qwen décompose les
  trois facettes en trois appels `overview`; le contrat mono-appel les rejette
  puis les deux runs expirent à `180054/180055 ms`; le prompt-only est retiré ;
- A189 conserve une coalescence strictement mécanique des appels homogènes :
  `62/62` routeur, route acceptée sans réparation, terminal sûr en `139941 ms`,
  mais zéro candidat valide et aucune source ; cette garde n'est pas une
  approbation fonctionnelle ;
- A190 inspecte en lecture seule les cinq passages réels : PDF Audit français
  de 58 pages, pages 6/18/30/41/53, cinq chunks en `85 ms`; le premier est un
  sommaire mal typé `content`, et aucun passage ne couvre directement la
  finalité générale ou les cas où citer le document ;
- A191 montre que les quatre passages français propres donnent quatre faits
  fidèles en `28139 ms`, 157 tokens et `finish_reason=stop`; le writer n'est
  donc pas génériquement incapable, mais le pool ne permet toujours pas de
  satisfaire la demande complète ;
- A192 confirme que douze pages uniformes restent incomplètes ; A193 expose 42
  ancres structurelles résolubles en 873 ms ;
- A194/A194b rejettent la stratégie d'hints seule ; A195 sélectionne six
  passages substantiels après exclusions de forme, mais A196 tronque à 320
  tokens et résume les preuves au lieu des trois facettes ;
- A197 corrige la perte des trois `query` du routeur et transporte exactement
  `overviewFacets` jusqu'à `rag.summarize_live` ; TDD rouge/vert et `107/107` ;
- A198 associe les facettes à A1/A5, A6/A7 et A18/A27 puis matérialise six
  contextes canoniques ;
- A199/A199b rejettent le writer partagé : citations absentes puis fuite de
  preuve entre facettes ;
- A200 rejette trois writers textuels isolés : deux troncatures sur trois au
  plafond agrégé de 240 tokens ;
- A201 réussit mécaniquement trois appels d'outil structurés en 52 251 ms et
  248 tokens, mais échoue sémantiquement : F3 est marquée inférence tout en
  affirmant encore « vous devez citer » ;
- A202 ferme la branche Judge : F2 tronque l'outil à 128 tokens et F3 approuve
  l'obligation fautive avec `isPracticalInference=true` et
  `reasonCode=supported_direct` ;
- A203 favorise offline le fallback extractif contre l'insuffisance opaque,
  tout en l'interdisant comme succès de synthèse ;
- A204 révèle que le pont `summary.flow` perd encore les facettes malgré leur
  présence dans le plan ; A204b corrige ce transport en TDD ;
- A204b sélectionne 6 ancres parmi 35 sur Q016, rejette le writer puis rend
  trois facettes, six citations et six cartes sous avertissement non-synthèse ;
- A205a conserve le chemin nominal P4-NORMES-001 : sept points, sept cartes et
  la sélection E1/E3–E8 en `95943 ms` internes ;
- A205b reproduit le fallback générique sur ISSAI 100 : 6 ancres parmi 12,
  six cartes et aucune alerte ;
- A206 mesure un contrat spécialisé test-only fidèle en `15669 ms`, 104 tokens,
  soit `68,05 %` de gain face au minimum général préenregistré ;
- A207 l'intègre derrière le classifieur et le convertit vers le même
  `RouterPlan`; un contrat invalide libère seulement la route générale ;
- A208 exécute l'unique Q016 autorisé : route acceptée sans réparation, trois
  facettes, six sources strictement identiques à A204b et terminal sûr conservé ;
- A208 réduit le harnais `125272 -> 92670 ms` (`26,02 %`) et le pipeline interne
  `112419 -> 76485 ms` (`31,96 %`) ;
- A209 inventorie les treize familles obligatoires et constate que plusieurs
  n'ont aucun live actuel ; Q016 Cuisine est préenregistré comme canari simple ;
- A211 trouve le vrai fondant E3 dès la première recherche, mais une revue
  compacte invalide le perd du prochain lot comparé ; E40 `PROFITEROLES` est
  ensuite acceptée et rendue sous le titre « fondant au chocolat » ;
- A211 est rejeté sur qualité et performance : `230130 ms` harnais,
  `215713 ms` internes, une source mécaniquement valide mais sémantiquement
  fausse et aucun flag automatique ; BUG-115 ouvert ;
- A213 reproduit en rouge la perte d'E1 après revue invalide ; A214 conserve au
  maximum deux anciens groupes non rejetés tout en réservant le nouveau lot ;
- un nouveau passage de la même identité source/chunk remplace mécaniquement
  l'ancienne représentation, sans éliminer les candidats réellement distincts ;
- A215 ajoute `corpus_target_not_cited` uniquement au harnais pour les cibles
  fichier concrètes ; aucune décision produit n'en dépend ;
- A216 valide `1/1`, `18/18`, `14/14`, `182/182`, puis client complet
  `1391/1391` deux fois, dont un TRX SHA `2E0C0C18...` ; aucun live/Qwen/WinUI.
- A218 exerce le chemin causal : la première revue invalide présente E1/E2/E3
  et le carry conserve bien les trois ; la seconde revue reçoit six inputs mais
  ne présente que E1/E2/E40, car E3 arrive après la limite de trois groupes ;
- la sortie A218 est une clarification inutile entre fondant et « moussure »,
  sans source ; `no_sources` et `corpus_target_not_cited` sont correctement
  émis ;
- A218 dure `188057 ms` harnais et `175134 ms` internes ; le dessert et la
  performance restent rejetés, A215 est approuvé, A214 est seulement partiel.
- A221 retrouve dans A211 la sélection Qwen correcte E3/E17, rejetée uniquement
  parce que le texte `answer` dépasse la borne inline ;
- A222 reproduit en rouge l'abandon de cette sélection et l'absence d'E3 dans
  une seconde revue à six candidats ;
- A223 redirige mécaniquement l'enveloppe trop longue vers le writer et place
  tout le carry avant le nouveau lot dans une fenêtre maximale de six ;
- A224 valide `2/2`, `16/16`, `184/184`, `18/18` et client `1393/1393` avec
  TRX SHA `AB379B4A...` ; BUG-116 est fermé déterministement.
- A226 sélectionne Q023 Documentation technique comme fait binaire court, avec
  cible fichier concrète et catégorie non imposée ;
- A227 est rejeté avant tout outil : le classifieur choisit
  `request_user_clarification`, le routeur redemande conformité ou
  non-conformité à l'utilisateur, zéro source ;
- A227 dure `65653 ms` harnais et `34045 ms` internes ; les flags `no_sources`
  et `corpus_target_not_cited` sont corrects ; BUG-117 est ouvert.
- A230 préenregistre le contraste entre réponses candidates à établir par les
  preuves et préférence que seul l'utilisateur peut fournir ;
- A231 échoue exactement `0/2` avant correction, puis A232 rend le contraste
  `2/2` sans prédicat lexical ou règle métier ;
- les budgets rejettent successivement `1331/1200`, `4124/4000`, `4042/4000`
  et `4019/4000` ; le texte final est compacté sans relever les limites ;
- A233 valide routeur+normalisation `162/162`, puis client x64 `1395/1395`
  avec TRX SHA `152B8B68...` ; aucun live/Qwen/WinUI.
- A235 fige produit, test et banque puis autorise exactement un replay Q023 ;
- A236 choisit encore `request_user_clarification` en `7591 ms`, enferme le
  second étage dans cette famille et rend un choix conformité/non compliance ;
- A236 finit à `28781 ms` internes et `47733 ms` harnais, sans outil/source ;
  `no_sources` et `corpus_target_not_cited` sont corrects ; aucune relance ;
- A238 rejette la correction live : BUG-117 reste ouvert et la prochaine
  hypothèse sépare famille de travail et terminal de clarification.
- A239 préenregistre clarification comme terminal provisoire, distinct des
  quatre familles de travail ;
- A240 échoue `10/16` exactement sur les anciens contrats d'exposition ;
- A241 retire clarification du classifieur et expose famille proposée +
  clarification au second étage, sans inspecter le texte utilisateur ;
- un test réel de préférence tableau/liste confirme que la clarification
  légitime reste possible avec `resumeRoute=source_backed` ;
- A242–A243 valident `16/16`, `161/161`, `28/28` et client `1394/1394`, TRX
  SHA `96A4A77D...` ; aucun live/Qwen/WinUI.
- A245 fige le produit A244 puis autorise exactement un replay Q023 ;
- A246 confirme live le premier étage : `submit_source_backed_route` en
  `6604 ms`, sans fallback ;
- le second étage choisit néanmoins clarification en environ `32490 ms`, avec
  `resume_route=source_backed`, zéro outil/source et les mêmes deux valeurs ;
- A246 dure `52450 ms` harnais et `39425 ms` internes ; flags corrects,
  performance et fonction rejetées ; aucun rerun ;
- A248 impose un recul : comparer evidence-first, juge compact et contrôle
  actuel avant d'ajouter un autre prompt local.
- A249 confirme dans le code que la clarification source-backed aval exige
  `executedRequests.Count > 0` et transporte déjà options, impact, mémoire et
  traces ;
- A251–A252 valident contrat, candidats observés, arbitrage, préférence de
  livrable et mémoire en `29/29`, sans Qwen ;
- A253 rejette le contrôle actuel par A246, retient evidence-first sans nouvel
  appel pré-source et écarte le juge compact pour la prochaine implémentation ;
  aucun produit n'est modifié dans ce palier.
- A254 préenregistre l'application evidence-first uniquement aux familles
  documentaires déjà choisies par Qwen ; operational et fallback restent
  inchangés ;
- A255 obtient le rouge attendu `6/9`, puis A256 passe à `9/9` après correction
  produit et d'une phrase contractuelle séparée par un saut de ligne ;
- A257 valide routeur, normalisation, aperçu, clarification après observation
  et mémoire à `196/196`, TRX SHA `E83B37B7...` ;
- A258 valide le même binaire sur toute la suite client Debug/x64 à
  `1394/1394`, zéro skip, `36 s`, TRX SHA `52017812...` ;
- A259 approuve déterministement evidence-first : budgets stricts inchangés,
  zéro terme canari et whitespace propre ; BUG-117 reste ouvert live.
- A260 fige DLL, banque, critères de décision et interdit toute relance Q023 ;
- A261 confirme live le séquencement evidence-first : `rag.search` s'exécute
  avant la clarification et matérialise 33 EvidenceItems ;
- aucun item ne correspond au PDF AAF cible ; Qwen attribue pourtant au
  « document » un passage d'un guide FDA voisin, sans citation, puis clarifie ;
- A261 est rejeté : zéro source/carte, flags `no_sources` et
  `corpus_target_not_cited`, `58628 ms` internes et `75731 ms` harnais ;
- A262 prouve en lecture seule que le catalogue n'a aucun match exact du chemin
  ou du nom cible et que deux requêtes RAG, dont le nom canonique, ne le
  retrouvent pas ;
- A263 approuve evidence-first comme ordre live, mais rejette fonction,
  provenance et performance ; BUG-118 ouvre la résolution des documents nommés
  absents et l'insuffisance honnête.
- A264 préenregistre quatre variantes C/R/T/V, leurs invariants et interdit tout
  patch produit ou nouveau replay pendant la comparaison ;
- A265 suit le nom demandé jusqu'à l'intake puis prouve que l'action initiale
  `rag.search` est appliquée sans résolution ni scope canonique ;
- le catalogue V2 ne contient aucun outil de résolution zéro/un/plusieurs et
  les outils `docRef` utilisent un résolveur fuzzy historique ;
- un JSON `doc_not_found` n'alimente pas `ToolResults.Item.Error`, si bien que
  l'observation compacte peut encore afficher `ok=true` ;
- A266 constate l'absence de tests absent/ambigu/inconclusif, mémoire stale,
  mismatch avant sélection, pivot alternatif et clarification sans assertion
  non prouvée ;
- A267 rejette C par A261/A262, rejette T seule parce qu'elle n'offre aucune
  garantie mécanique, retient R comme précondition et V comme garde aval ;
- A268 approuve l'architecture R+V à implémenter : revalidation backend
  obligatoire, contrat `resolved/not_found/ambiguous/inconclusive`, scope ou
  quarantaine de l'action initiale, puis décision Qwen et garde `docId` aval ;
- aucun produit/test n'a été modifié, aucun Qwen/WinUI lancé et aucun processus
  `llama`, `testhost` ou `dotnet` n'est resté actif.
- A269 préenregistre avant toute implémentation 10 invariants, le contrat typé,
  la table de vérité catalogue, la politique scope/quarantaine, les deux
  portées `requested_document`/`alternative_sources` et 36 cas TDD ;
- A269 interdit explicitement de transformer une erreur ou pagination
  incomplète en `not_found`, de choisir un homonyme/fuzzy et d'inférer un pivot
  alternatif sans décision Qwen typée ; aucun code produit/test n'a été touché.
- A270 conserve un rouge valide à `1/5` : exact, absent, ambigu et inconclusif
  échouent parce que le résolveur n'est jamais appelé ; sans document nommé
  reste vert ; le premier run de fixture 6/11 est explicitement invalide ;
- A271 implémente le port catalogue actuel, l'exact nom/chemin/docId, la
  pagination complète et le fail-closed `inconclusive` ; résultat intermédiaire
  `9/13`, quatre rouges downstream attendus ;
- A272 borne l'action originale sur l'identité unique ou la met en quarantaine,
  supprime plan/fallback non bornés et passe à `13/13` ;
- A273 expose à Qwen clarification, insuffisance, alternative explicite ou un
  retry catalogue, puis exclut/rejette tout mismatch d'identité ; `30/30` ;
- A274 conserve deux échecs de modularité `404/405` et un build invalide, les
  corrige par extraction sans changer le comportement, puis obtient `405/405`
  et `1419/1419` ;
- A275 scinde le vertical de tests 522 en 376 + 154, rejoue la forme finale à
  `405/405` et `1419/1419`, confirme zéro hardcode, budgets inchangés,
  diff-check propre et aucun processus résiduel ;
- le rapport Telegram A269–A275 est confirmé en une partie : corps 2 230, total
  2 336 caractères, SHA `1B4D5531...5312653`.
- A276 fige avant Qwen l'unique replay Q023 post-R+V : DLL WinUI/tests, banque
  et manifeste, préflight read-only, cible toujours absente comme porte, un seul
  run, quatre suites Qwen admissibles, critères séparés de sécurité, fonction,
  provenance et performance, puis arrêt obligatoire sans patch/rerun ;
- protocole A276 : 254 lignes, SHA `844B1F22...F9D5DD` ; aucun live encore
  exécuté.
- A277 confirme DLL WinUI `92D2801E...`, tests `1E5D1330...`, banque
  `84E675B7...` et manifeste `F7429F21...` inchangés, branche/HEAD exacts et
  aucun processus/env live hérité ;
- `/ready` vaut HTTP 200/`ok=true`, le catalogue actuel rend zéro item exact
  pour chemin, nom et stem, et le modèle/runtime Q5 gardent leurs hashes
  `66713CE3...`/`9555847A...` avec contexte 4 096 ;
- A277 est vert, artefact SHA `80D30D6D...833CE5` ; l'unique A278 est autorisé.
- A278 exécute exactement une fois Q023 : résolution `NotFound` complète en
  `35 ms`, action originale quarantinée, zéro outil/requête/preuve/source et
  aucun homonyme FDA/HEPA ;
- Qwen choisit néanmoins `clarification` en une tentative, prompt/completion
  `936/218` tokens, et redemande rapport spécifique ou catégorie malgré la
  référence exacte déjà fournie ;
- réponse 173 caractères, zéro attribution au PDF absent, flags
  `no_sources,corpus_target_not_cited`, pipeline `60827 ms`, harness `76305 ms`,
  TRX `76,668 s` ;
- A279 approuve live résolution, identité, sécurité et provenance, ferme
  BUG-118 pour le cas absent, mais rejette fonction/performance et garde BUG-117
  ouvert au choix sémantique post-absence ;
- Telegram A276–A279 confirmé : corps 2 085, total 2 191 caractères, SHA
  `68CB4563...1CFD0A` ; aucun rerun et zéro processus résiduel.

Décision courante : le routeur d'aperçu spécialisé A207 est **RETENU** et le
fallback extractif reste **APPROUVÉ COMME TERMINAL SÛR**. Q016/Q019 ne sont pas
des synthèses approuvées ; P4 conserve `TESTÉ_NON_APPROUVÉ` sur la qualité
stricte. Le dessert simple reste **REJETÉ** tant qu'il n'a pas été rejoué. Les
deux causes mécaniques observées de BUG-115 sont maintenant **CORRIGÉES
DÉTERMINISTEMENT** : une sélection Qwen trop longue passe au writer et aucun des
trois candidats du jugement invalide n'est évincé par le nouveau lot. BUG-115
reste ouvert live sans relancer Q016. La famille factuelle exacte reste
**TESTÉE_NON_APPROUVÉE LIVE** : A263 confirme que la clarification pré-outil a
disparu, mais la cible Q023 n'est pas indexée et le pipeline attribue une preuve
  voisine au document absent avant une clarification aval. Evidence-first est
  approuvé comme séquencement ; BUG-117 reste ouvert fonctionnellement. A269–A275
  implémentent et approuvent déterministement R+V : identité catalogue actuelle,
  scope/quarantaine avant retrieval, décision Qwen typée puis garde aval.
  BUG-118 est corrigé déterministement mais n'est pas fermé fonctionnellement
  avant le replay live séparément préenregistré en A276. A278 confirme maintenant
  la correction live : aucune recherche, substitution ou attribution après
  absence exacte, donc BUG-118 est fermé pour ce cas. En revanche Qwen choisit
  encore une clarification de portée inutile ; BUG-117 et la performance restent
  ouverts. R+V est conservé sans rollback ;
- A280 compare sans patch le contrôle actuel, le prompt renforcé, la règle
  déterministe, un juge séparé et un portfolio typé. Seul le portfolio est
  retenu : un appel Qwen, mais uniquement les transitions compatibles avec le
  statut courant. La matrice et 18 cas TDD sont préenregistrés avant produit ;
- A282 produit un rouge causal 8/23 avec 15 échecs tous dus à l'ancien outil ;
- A283–A285 remplacent l'enum par six capacités spécialisées, ajoutent
  `source_identity`, un retry unique, les options ambiguës exactes et la reprise
  canonique après conflit ; les portes finales sont 24/24, 518/518 et client
  1 427/1 427 ;
- chaque portfolio a moins de paramètres JSON que l'ancien schéma 1 693 :
  1 303/1 638/1 365/1 365 ; aucun appel ou budget ajouté ;
- Telegram A280–A285 confirmé en une partie : corps 2 668, total 2 774,
  SHA `B8838010...E79107` ; build servers arrêtés, zéro processus résiduel ;
- A287 revalide les hashes, Q5/4K, `/ready` et l'absence exacte path/name/stem
  avant modèle ;
- A288 exécute une fois Q023 : `NotFound` complet en 94 ms, action
  quarantinée, Qwen choisit `declare_named_document_insufficiency` en une
  tentative avec 842/115 tokens, zéro outil/requête/preuve/source ;
- le pipeline baisse de 60 827 à 47 933 ms et le harnais de 76 305 à 61 952 ms,
  mais la cible 30 s reste rejetée ;
- A289 ferme BUG-117 pour la sur-clarification du cas absent, garde la famille
  positive non approuvée et ouvre BUG-119 sur deux formulations imprécises ;
- Telegram A286–A289 confirmé : corps 2 373, total 2 479, SHA
  `217523AB...063BA4` ; aucun rerun et zéro processus résiduel.
- A290 compare read-only Q106–Q110 et retient Q106, seul candidat actuel exact :
  `docId 9fb65de2...8308`, révision `a2d922d5...0197`, hash
  `909173f3...18fcf`, deux pages et neuf chunks canoniques ;
- le protocole A290–A293 fige avant modèle un seul replay, interdit patch/build/
  rerun et sépare résolution, fonction, provenance, mémoire, contrat UI et
  performance ; 467 lignes, SHA `99D33427...02B58` ; seul A291 est autorisé.
- A291 revalide les six entrées figées, Q5/runtime, contexte 4K, backend,
  unicité nom/stem et toute l'identité Q106 ; zéro processus/env et aucune
  inférence ; artefact 115 lignes SHA `04DCAE9D...314E` ; dossier A292 vide ;
- A292 consomme l'unique live et révèle un faux `NotFound` : deux observations
  productives vides en 156 ms malgré le document indexé ; l'action
  `documents.navigation` correcte est quarantinée, puis Qwen choisit une
  insuffisance sûre en 862/86 tokens ;
- résultat Q106 : zéro outil de contenu/requête/preuve/source, flags
  `no_sources,corpus_target_not_cited`, pipeline 45 698 ms, harness 60 960 ms,
  TRX 61,176 s ; fonction, provenance positive et performance rejetées ;
- après le live, `/documents`, `/catalog/documents` et l'ApiClient productif
  retrouvent le même `docId`; dix nouveaux clients × nom/stem donnent 20/20
  positifs ; l'écart est transitoire et sa cause exacte reste non isolée ;
- A291 conserve ses faits d'identité mais est reclassé porte produit
  insuffisante ; BUG-120 critique est ouvert et BUG-119 reste ouvert ;
- A293 : 374 lignes, SHA `F679504D...428DE` ; aucun rerun/patch/build, zéro
  processus résiduel.
- Telegram A290–A293 confirmé en une partie : corps 2 930, total 3 036,
  SHA `C1346811...B4ECD4`.
- A294 compare six stratégies et retient un consensus exact entre catalogue V2
  productif et catalogue unifié user-safe : accord obligatoire pour
  `Resolved`/`Ambiguous`/`NotFound`, tout désaccord devient `Inconclusive` ;
  protocole 310 lignes SHA `82A53B9A...83750`, aucun modèle/live autorisé.
- A295 conserve le rouge causal compilé `0/4` : F1 seule transforme encore
  vide/exact en `NotFound`, exact/vide en `Resolved` et n'observe jamais F2 ;
- A296 observe les deux frontières avec pagination bornée, compare les
  ensembles d'identités exactes et traduit toute divergence en
  `catalog_observations_disagree` ; après une compilation intermédiaire
  invalide conservée, le noyau passe `4/4` et la matrice `16/16` ;
- A297 prouve avec le vrai consensus et un Qwen scripté que désaccord puis
  accord exécute l'action originale bornée au bon `docId` sans modifier
  query/tool/topK, tandis qu'un désaccord persistant exécute zéro contenu ;
- les portes affectées passent `18/18`, `120/120` et `234/234` ; A298 passe la
  suite client complète `1445/1445`, zéro skip, TRX SHA `69ECC62D...07D6` ;
- onze résolutions backend réelles sans modèle rendent Q106 `Resolved` 11/11,
  médiane 69 ms, min/max 57/305 ms, sous la porte 1 000 ms ;
- A299 approuve déterministement le palier, classe BUG-120 corrigé
  déterministement mais ouvert live et maintient la phase 5 en cours ; rapport
  380 lignes SHA `FB152804...A712` ; protocole final 338 lignes SHA
  `75B18C28...009F` ; aucun Qwen/WinUI/replay et zéro processus résiduel.
- Telegram A294–A299 confirmé en une partie : corps 2 678 caractères, HTTP
  200, SHA `3673EF45...477C8`.
- A300 fige le replay Q106 post-consensus avant modèle : DLL/sources/banque,
  vrai résolveur F1+F2, identité/révision/hash/neuf chunks, runtime Q5/4K,
  dossier A302, commande unique, critères séparés et règle d'arrêt ; protocole
  507 lignes SHA `798E86C7...C6E6F`, aucun build/Qwen et seul A301 autorisé.
- A301 confirme tous les hashes, runtime et backend puis résout le bon Q106 en
  `118–125 ms`, mais stoppe sur candidat sans révision et hash `c62f...` 32 hex
  contre révision `a2d922d5...` et SHA-256 `909173f3...` canoniques ;
- le détail décode son `ContentHash` vers le SHA-256 attendu et le contexte
  confirme révision, hash et neuf chunks : aucune dérive de document ;
- F1 expose sous `sourceHash` le digest de fraîcheur résumé calculé par
  `saaia_document_summary_source_hash`, F2 n'expose aucune identité de révision,
  puis la garde aval compare le digest non vide aux preuves canoniques ;
- A302 n'est ni autorisé ni exécuté, dossier absent et Qwen jamais démarré ;
  BUG-121 critique ouvert, rapport A303 342 lignes SHA `A2FF4123...7F86`,
  protocole final 532 lignes SHA `67840B80...44E9` ;
- Telegram A300–A303 confirmé en une partie : corps 2 506 caractères, HTTP
  200, SHA `83C7A2B6...2D1B`.
- A304 compare contrôle, sanitisation, hydratation, extension backend et
  vérification tardive ; il retient la sanitisation F1 et une hydratation unique
  après agrégation exacte, avant tout contenu ; protocole initial 362 lignes
  SHA `437BA221...D642E`, aucun modèle/live.
- A305 conserve le rouge causal compilé : `5/18`, 13 échecs dus à l'absence
  d'appel contexte, la révision nulle, le hash32 et les dérives encore
  `Resolved` ; TRX SHA `A5E46854...AA9986`.
- A306 ajoute le port mécanique d'identité, l'adaptateur user-safe, la
  sanitisation catalogue, l'appel post-consensus et le câblage factory ; aucun
  texte de contexte, `EvidenceItem`, prompt, budget ou corpus n'est ajouté.
- Les portes A306/A307 passent `18/18`, `48/48`, `355/355`, `337/337` et client
  complet `1463/1463`, exactement les 1 445 précédents plus 18 nouveaux ; full
  TRX SHA `A8291988...3BCA0E`.
- La mesure backend sans modèle rend 11/11 identités canoniques : hydratation
  médiane 15 ms, min/max 14/21 ; consensus+hydratation médiane 106 ms,
  min/max 97/127.
- A308 rejoue le script A301 inchangé : Q106 `Resolved`, complet, exact 1,
  révision `a2d922d5...0197`, SHA-256 `909173f3...18fcf`, deux pages et neuf
  chunks ; identity gate et préflight PASS, aucun `llama-server` ou dossier
  A302.
- A309 ferme BUG-121 sur l'axe mécanique/préflight réel, sans promouvoir la
  fonction, la provenance finale, la mémoire, l'UI ou la performance live ;
  rapport 393 lignes SHA `855C69D4...A878579`, protocole final 460 lignes SHA
  `E593D134...A4A374`.
- Telegram A304–A309 confirmé en une partie : corps 2 882 caractères, total
  2 964, SHA `D8053528...BD2184`.
- A310 préenregistre un seul replay Q106 post-identité, sans patch ni rerun,
  avec préflight positif obligatoire et verdicts fonction/provenance/mémoire/UI/
  performance séparés ; protocole initial 511 lignes SHA `B82C3A31...4EA14B`.
- A311 confirme immédiatement avant modèle Q106 `Resolved`, complet, exact 1 en
  172 ms, docId/chemin/nom/révision/SHA-256 conformes, deux pages et neuf chunks ;
  artefact 166 lignes SHA `B181748A...306B`.
- A312 consomme l'unique run puis reproduit A292 : `NotFound` complet, exact 0,
  zéro outil/hit/EvidenceItem/source/citation et la même réponse de 386 caractères
  SHA `8CBA4F46...E54B6CC` ; pipeline 45 872 ms et aucun rerun.
- L'inspection backend montre que F1 `/catalog/documents` et F2 `/documents`
  interrogent la même table `documents`, le même tenant et le même filtre
  `status='indexed'` : leur accord vide est corrélé et ne prouve pas l'absence.
  La cause temporelle serveur précise reste volontairement inconnue.
- A313 approuve la sûreté anti-substitution mais rejette fonction, provenance
  positive et performance ; BUG-120 est rouvert/confirmé live, BUG-121 reste
  fermé mécaniquement mais non exercé dans A312, BUG-119 est reproduit ; rapport
  491 lignes SHA `28B09C60...3A25C6`, protocole final 601 lignes SHA
  `95E0318D...E385FC8`.
- Telegram A310–A313 confirmé en une partie : corps 3 524 caractères, total
  3 606, SHA `ED137CBF...E2B540`.

Artefact courant :
`artifacts/goal-rag-product-20260827-1041/phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A310-A313-Q106-FAUX-NOTFOUND-CORRELE.md`.

Protocole terminé :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A206-A208-ROUTEUR-APERCU-SPECIALISE-SANS-PERTE-SEMANTIQUE.md`.

Protocole terminé :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A209-A212-DESSERT-SIMPLE-ET-MATRICE-FONCTIONNELLE.md`.

Protocole terminé :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A213-A216-CONTINUITE-CANDIDATS-APRES-REVUE-INVALIDE.md`.

Protocole terminé :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A217-A220-REPLAY-CAUSAL-UNIQUE-DESSERT-SIMPLE.md`.

Protocole terminé :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A221-A225-NORMALISATION-REVUE-ET-CARRY-COMPLET.md`.

Protocole terminé :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A226-A229-FAIT-EXACT-NON-CONFORMITE-AAF.md`.

Protocole terminé :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A230-A234-CONTRASTE-FAIT-SOURCE-VS-PREFERENCE-UTILISATEUR.md`.

Protocole terminé :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A235-A238-REPLAY-LIVE-UNIQUE-Q023-APRES-CONTRASTE-ROUTEUR.md`.

Protocole terminé :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A239-A244-SEPARATION-FAMILLE-TRAVAIL-ET-TERMINAL-CLARIFICATION.md`.

Protocole terminé :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A245-A248-REPLAY-LIVE-UNIQUE-Q023-APRES-SEPARATION-ETAGES.md`.

Protocole terminé :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A249-A253-COMPARAISON-ARCHITECTURES-CLARIFICATION.md`.

Protocole terminé :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A254-A259-EVIDENCE-FIRST-AVANT-CLARIFICATION-DOCUMENTAIRE.md`.

Protocole terminé :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A260-A263-REPLAY-LIVE-UNIQUE-Q023-EVIDENCE-FIRST.md`.

Protocole terminé :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A264-A268-COMPARAISON-RESOLUTION-DOCUMENT-NOMME.md`.

Protocole terminé :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A269-A275-TDD-RESOLUTION-DOCUMENT-NOMME-RV.md`.

Protocole terminé :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A276-A279-REPLAY-LIVE-UNIQUE-Q023-APRES-RV.md`.

Protocole terminé :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A280-A285-PORTFOLIO-OUTILS-TRANSITION-DOCUMENT-NOMME.md`.

Protocole terminé :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A286-A289-REPLAY-LIVE-UNIQUE-Q023-APRES-PORTFOLIO.md`.

Protocole terminé :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A290-A293-CANARI-POSITIF-DOCUMENT-NOMME-PRESENT.md`.

Protocole terminé :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A294-A299-CONSENSUS-OBSERVATIONS-CATALOGUE-DOCUMENT-NOMME.md`.

Protocole terminé :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A300-A303-REPLAY-LIVE-UNIQUE-Q106-APRES-CONSENSUS-CATALOGUE.md`.

Protocole terminé :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A304-A309-HYDRATATION-IDENTITE-CANONIQUE-DOCUMENT-NOMME.md`.

Protocole terminé :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A310-A313-REPLAY-LIVE-UNIQUE-Q106-APRES-IDENTITE-CANONIQUE.md`.

### Reprise A314–A315 — cause racine Q106 fermée au niveau C4

- A314 préenregistre 576 lignes avant toute lecture serveur et conserve H1–H7
  comme hypothèses falsifiables ; son inventaire clôturé corrige explicitement la
  chronologie sans réécrire l'état initial.
- A315 prouve que backend, PostgreSQL, Qdrant, scanner, document, révision,
  jobs, runs et audits sont stables ; aucune transition watcher ne vise Q106.
- Les quatre GET A312 ont réellement lieu vers `00:08:11Z`, environ 140,122 s
  après le dernier GET positif A311 ; l'ancien écart de 44 s était le délai vers
  le début du trace, pas vers la résolution catalogue.
- Le DLL A307 exécuté avec la question exacte extrait une chaîne de 52 caractères
  incluant l'impératif au lieu du nom littéral de 23 caractères.
- La matrice HTTP reproduit la causalité : mauvaises query/stem = 0/0 ; bon
  nom/stem = 1/1 et docId attendu. BUG122 est ouvert C4.
- Le rapport A314–A315 compte 168 lignes, SHA `9061414F...1F5EBB94` après
  correction de la longueur littérale 23 ; Telegram
  est confirmé en une partie, corps 2 227, total 2 332, SHA
  `4CCCB27D...16B443E5`.
- Aucun modèle, replay live, patch produit, ingestion, restart ou mutation
  serveur n'a été exécuté ; la phase 5 reste ouverte.

### Clôture A316–A318 — extraction littérale corrigée

- A316 compare X0–X4 et T0–T3, retient X3/T1 sans fuzzy ni décision pilotée par
  le corpus ; document 170 lignes SHA `E51ABD8F...29010077`.
- A317 conserve le rouge causal 0/1, ajoute la frontière syntaxique générique et
  la référence à la trace, puis passe 12/12 et 151/151 ; rapport SHA
  `DC77383A...5271B244`.
- A318 passe 1 473/1 473 en 27 s, exactement dix tests de plus que la référence
  1 463 ; TRX SHA `850933BE...31DF639E`.
- Le binaire final SHA `8287524E...279DA3D` extrait le nom exact 10 000/10 000
  fois, moyenne harnais 97,132 µs réflexion incluse.
- Rapport final 233 lignes SHA `85EEA212...9658907B` ; protocole final 641
  lignes SHA `860BFF6A...2C4732B6`.
- Telegram confirmé en une partie : corps 2 761, total 2 866, SHA
  `F6A56757...35532830`.
- Aucun modèle/replay/mutation serveur ; build servers arrêtés, processus lourd
  zéro ; phase 5 toujours ouverte.

Artefact courant :
`artifacts/goal-rag-product-20260827-1041/phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A314-A318-BUG122-EXTRACTION-LITTERALE.md`.

Protocole terminé :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A314-A318-DIAGNOSTIC-SERVEUR-ET-PREUVE-NEGATIVE-DOCUMENT-NOMME.md`.

Prochaine action : préenregistrer A319–A322. Le préflight doit traverser la
question Q106 complète et vérifier la référence extraite, puis autoriser un seul
replay live sans patch/rerun. BUG119/120 et les axes fonction, provenance,
mémoire, UI et performance doivent être jugés séparément.

### Clôture A319–A322 — Q106 résolu, preuve bloquée après navigation

- A319 fige avant modèle la question complète, les DLL, Q5/4K, l'identité
  canonique, un seul replay, les critères séparés et l'interdiction de
  patch/rerun ; version pré-live 311 lignes, SHA
  `216BB093...1EA4DA25`.
- A320 traverse réellement question -> référence -> consensus -> hydratation ->
  contexte : nom exact de 23 caractères, `Resolved` complet, exact 1, identité
  `9fb65de2...8308` / `a2d922d5...0197` / `909173f3...18fcf`, neuf chunks et
  treize hashes conformes ; artefact SHA `60415BA7...10453D`.
- A321 exécute exactement une fois Q106. La résolution passe en 194 ms et
  `documents.navigation` matérialise E1 sur le bon PDF/page 1 ; BUG122, BUG120
  et BUG121 sont enfin exercés positivement en live sur cet axe.
- Qwen choisit ensuite une query lexicale abstraite qui rend zéro carte, puis
  `documents.context` avec le chunk synthétique `chunk-1`. Aucun nouvel
  EvidenceItem n'est produit ; Qwen fournit finalement le docId au lieu de E1.
- Le résultat reste fail-closed, sans substitution ni affirmation non soutenue,
  mais fonction et provenance positive sont rejetées : zéro source/citation/
  carte, harnais 182 342 ms, pipeline 168 471 ms, neuf tours LLM.
- A322 reproduit read-only la causalité : l'ancre E1 possède page 1 et un
  `targetAnchorId`, mais aucun `targetChunkId`; `chunk-1` provoque HTTP 400 ; la
  même identité lue par pages 1–2 retourne immédiatement les neuf chunks
  canoniques.
- Cause : le code sépare bien navigation et citation, mais exige encore un
  chunk pour déclarer un pointeur résoluble et exclut tout pointeur non citable
  du focus documentaire. Il ne valide pas non plus localement les chunkId
  inventés.
- L0 prompt seul, L1 lecture automatique globale et L4 rag.search forcé sont
  rejetés. L2/L3 est retenue : localisateur documentaire typé distinct d'une
  preuve citable, résoluble par chunk **ou pages**, sélectionné sémantiquement
  par Qwen puis copié mécaniquement par le code.
- BUG123 ouvre la rupture localisateur/page ; BUG124 ouvre la fuite du protocole
  interne dans la réponse ; BUG125 ouvre l'amplification no-progress et la
  latence. Phase 5 reste en cours et l'UI n'est pas testée.
- Rapport A319–A322 : 520 lignes, SHA `7C6BB67C...A7D5A9F` ; protocole
  clôturé 358 lignes, SHA `8534FAD4...D512E875` ; sortie read-only conservée,
  aucun second live, aucune mutation serveur et zéro processus lourd résiduel.
- Telegram A319–A322 confirmé en une partie : corps 3 406, total 3 488
  caractères, SHA `8FF24A89...32A25118` ; reçu d'envoi conservé dans le dossier
  causal A322.

Artefact courant :
`artifacts/goal-rag-product-20260827-1041/phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A319-A322-Q106-RESOLUTION-OK-PREUVE-BLOQUEE.md`.

Protocole terminé :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A319-A322-REPLAY-LIVE-UNIQUE-Q106-APRES-EXTRACTION-LITTERALE.md`.

Prochaine action : préenregistrer A323–A328. Le palier doit distinguer en TDD
localisateur résoluble et preuve citable, accepter les ancres page-only,
refuser avant HTTP les identifiants synthétiques, préserver le choix sémantique
de Qwen, nettoyer le terminal utilisateur et arrêter les boucles no-progress
répétées. Aucun nouveau replay live avant suites déterministes complètes.

### Ouverture A323 — contrat localisateur/preuve préenregistré

- Le protocole A323–A328 est figé avant test rouge et avant patch produit : 373
  lignes, 15 028 octets, SHA-256
  `CAD75E6C4F0661985098D94908BA3554A5A3BA34D7C916EA2D17075E36BE480D`.
- Le contrat distingue désormais explicitement une preuve citable d'un
  localisateur de navigation résoluble par chunk observé **ou** par pages
  observées ; le second peut guider la récupération sans soutenir une
  affirmation.
- Qwen conserve le choix sémantique de l'ancre, de la requête, des preuves et de
  la réponse ; le code ne fait que valider/coller les coordonnées observées.
- A324 doit conserver quatre rouges causaux minimaux : batch page-only, focus
  page-only, chunk synthétique refusé avant exécuteur et terminal public sans
  protocole. BUG125 recevra un rouge additionnel seulement après audit des
  contrats de récupération existants.
- Aucun modèle, replay live, patch produit ou mutation serveur n'a été exécuté
  pendant A323.

Protocole en cours :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A323-A328-CONTRAT-LOCALISATEUR-PREUVE-ET-TERMINAL-PUBLIC.md`.

Prochaine action : A324, écrire puis exécuter uniquement les tests causaux
déterministes et confirmer que chaque rouge échoue pour la cause préenregistrée.

### Clôture technique A324–A327 — localisateur/preuve, terminal et no-progress

- A324 conserve cinq rouges causaux propres : page-only batch, page-only focus,
  chunk inventé transmis, note interne publiée et répétition consommant 13
  requêtes au lieu de 7 ; TRX SHA `A88EA650...8663A`.
- A325 implémente le contrat générique localisateur = document + chunk observé
  **ou** pages observées, sans rendre la navigation citable ; 5/5 verts, TRX SHA
  `9983E14C...B356E`.
- Le terminal ne concatène plus les notes techniques, qui restent dans le
  résultat/mémoire interne ; BUG124 est fermé déterministement.
- La signature no-progress inclut décision normalisée + état observable du
  bundle : deux décisions identiques après deux tours sans progrès arrêtent le
  run, mais un nouvel EvidenceItem réouvre réellement la récupération ; BUG125
  est fermé sur cet axe déterministe.
- A326 passe 216/216 ciblés puis la suite complète. La garde de modularité a
  imposé l'extraction du contrat hors du runner plutôt que l'augmentation d'une
  limite.
- A327 ajoute un sixième rouge : un chunk observé ne peut pas être mélangé avec
  le docId/docPath d'un autre document. Le tuple complet est maintenant validé
  avant HTTP ; 6/6 verts.
- Suite client finale : 1 479/1 479, zéro échec/skip, TRX SHA
  `C8A98F7B...26E2A7`, exactement six tests de plus que la référence A318 à
  1 473.
- La citabilité existante reste stricte : `orientation_only`, `navigation_map`
  et `documents.navigation` sont exclus. Qwen conserve choix de l'ancre,
  requête, preuves et réponse.
- Recherche hardcode sur huit fichiers produit : zéro occurrence Q106/corpus ;
  whitespace/conflits PASS, garde de modularité PASS, `git diff --check` code 0.
- DLL finale SHA `153140CA...B1495`. Build servers arrêtés et processus lourd
  résiduel zéro.
- Audit A327 : 378 lignes, SHA
  `88C62AD06C9478B6E6334D20D272A47A4259C8651BEDF698A611723D2505273E`.
- Rapport A328 initial avant reçu Telegram : 360 lignes, SHA
  `029571BBAE1B420DE0077B115687D581BBF53E739BE5573D32261578E33904B4`.
- Aucun modèle, replay, ingestion, mutation serveur ou redémarrage backend.

Verdicts actuels : BUG123/124 fermés déterministement ; BUG125 fermé sur la
répétition identique. Fonction Q106, provenance finale, mémoire positive, UI et
performance restent non approuvées après patch faute de replay live.

Artefact courant :
`artifacts/goal-rag-product-20260827-1041/phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A323-A328-LOCALISATEUR-PREUVE-ET-TERMINAL.md`.

Prochaine action : terminer A328 avec notification Telegram et reçu, puis
préenregistrer A329–A332 avant tout unique replay live Q106.

### Clôture A328 — rapport et Telegram confirmés

- Telegram envoyé en une partie : corps 2 753 caractères, total 2 877, fichier
  2 829 octets/45 lignes, mojibake 0, SHA
  `E648E6B668B27DBDC9B834A035BA02BFEEEB87A7378A149FE0603B27F06E939E`.
- Reçu local : 20 lignes, 784 octets, SHA
  `F1F3EEF68D00B4B35DC779E0ABE9598B34263AA5DB8378349469082C01C84BEF`.
- Protocole A323–A328 final : 512 lignes, 20 210 octets, SHA
  `0DCC3404135C0DF3CA0430046B81FED8F9B1709E13B1EA1B69EBF955D787E064`.
- Rapport A323–A328 final : 394 lignes, 12 012 octets, SHA
  `671F74B9394CDE94890EE4C38C20C0586DDCAF852BA8F2EFA4CB546DD1D41424`.
- A323–A328 est clos ; la phase 5 et le goal ne sont pas clos.

Prochaine action : A329 doit préenregistrer le canari live Q106 post-contrat
localisateur. A330 fera le préflight complet. A331 consommera au maximum un run
sans rerun. A332 séparera fonction, provenance, mémoire, UI, performance et
sûreté, puis produira rapport/Telegram.

### Clôture technique A329–A332 — replay non probant, binaire harnais périmé

- A329 fige un seul replay Q106 et sépare explicitement résolution,
  localisateur, preuve, réponse, mémoire, terminal, performance et UI ; version
  pré-A330 finale 450 lignes, SHA `76689565...DF774278`.
- A330 passe question complète, intake exact de 23 caractères, backend ready,
  résolution, identité canonique et neuf chunks pages 1–2. Le rapport initial
  déclare les portes vertes, SHA `2084635E...D65078`.
- A331 consomme exactement un run : xUnit 1/1, mais zéro source, zéro used-item,
  terminal interne exposé, neuf appels LLM et cinq tours sans progrès ; le
  comportement est identique à A321.
- L'audit A332 prouve que VSTest a chargé la dépendance
  `bin/x64/Debug/.../SAAIA.Client.WinUI.dll` SHA
  `8287524E...279DA3D`, ancien binaire A321, au lieu du produit A327 SHA
  `153140CA...B1495`.
- Le full test A327 avait utilisé la sortie `bin/Debug`, où la dépendance WinUI
  possède bien le SHA corrigé. La commande live héritée avec
  `-p:Platform=x64 --no-build` a sélectionné une autre sortie périmée.
- La gate A330 contrôlait le binaire produit principal, pas la copie de
  dépendance réellement chargée. Son PASS global est annulé ; la parité du
  harnais est FAIL.
- BUG122/120/121 restent PASS live. BUG123/124/125 sont **NON REJOUÉS sur le
  binaire corrigé** ; réponse, provenance, mémoire positive, UI et performance
  restent non approuvées.
- Aucun patch produit et aucun rerun A331. Protocole final 529 lignes, SHA
  `F865894D...CC69FBA`; rapport A332 initial 383 lignes, SHA
  `8DEE0C2F...EABA825`.
- Build servers arrêtés et zéro processus lourd résiduel.

Artefact courant :
`artifacts/goal-rag-product-20260827-1041/phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A329-A332-Q106-BINAIRE-HARNAIS-PERIME.md`.

Prochaine action : terminer A332 par Telegram, puis préenregistrer un nouveau
palier de parité du harnais. Il devra vérifier le chemin de storage VSTest et le
SHA de chaque dépendance SAAIA adjacente au DLL de tests avant d'autoriser un
nouveau replay unique. A331 ne sera jamais relancé.

### Clôture A332 — rapport et Telegram confirmés

- Telegram envoyé en une partie : corps 2 582 caractères, total 2 703,
  fichier 2 651 octets/30 lignes, mojibake 0, SHA
  `FBB89235...DF58965`.
- Reçu local : 21 lignes, 726 octets, SHA
  `1629E505...2FE88E7`.
- Le rapport initial pré-reçu avait le SHA `8DEE0C2F...EABA825` ; sa version
  finale conserve append-only les détails d'envoi.
- A329–A332 est clos. Le goal et la phase 5 restent actifs ; aucun correctif
  live A327 n'est encore approuvé.

Prochaine action : A333 doit préenregistrer la preuve de parité binaire réelle
du harnais avant toute construction ou nouveau modèle.

### Clôture technique A333–A337 — parité VSTest prouvée, sonde PowerShell arrêtée

- A333 fige la sortie `bin/Debug`, le storage VSTest, le SHA de la dépendance
  adjacente et un replay unique uniquement après deux préflights ; protocole
  pré-A334 SHA `A1ADB55D...14B872A`.
- A334 charge explicitement la DLL WinUI adjacente SHA
  `153140CA...B1495`, confirme son `Assembly.Location`, quatre méthodes A327 et
  le full TRX 1 479/1 479 issu du même storage.
- Les six contrats page-only/chunk/identité/terminal/no-progress passent 6/6
  sans build depuis `bin/Debug`; TRX SHA `2D4BA09E...02C22E`, storage exact.
- L'inventaire adjacent (tests, WinUI, Contracts) est identique avant/après ;
  rapport A334 SHA `EC5EB7FC...A203D3D`.
- A335 charge banque, Q106 et la bonne DLL, mais la première invocation du hook
  d'intake sous PowerShell échoue dans l'initialiseur `<Module>`. Le backend
  n'est pas atteint, le modèle n'est pas démarré et le dossier A336 reste absent.
- Aucun correctif ni seconde tentative A335 : A336 live est NON REJOUÉ.
- L'échec est celui de l'hôte PowerShell, pas une réfutation de la parité
  VSTest, déjà démontrée dynamiquement.
- Un test existant exerce la question exacte et les hooks d'intake sous VSTest ;
  le prochain palier utilisera cet hôte représentatif plus l'identité binaire et
  la sonde backend indépendante.
- Protocole final 476 lignes, SHA `FB750963...0E3A811`; processus lourd zéro.

Artefact courant :
`artifacts/goal-rag-product-20260827-1041/phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A333-A337-PARITE-VSTEST-PASS-PREFLIGHT-HOTE-FAIL.md`.

Prochaine action : terminer A337 par Telegram, puis préenregistrer un nouveau
palier utilisant VSTest pour l'intake exact Q106. A335 et A336 ne seront jamais
relancés.

### Clôture A337 — Telegram confirmé

- Telegram envoyé en une partie : corps 2 152 caractères, total 2 272,
  fichier 2 213 octets/26 lignes, mojibake 0, SHA
  `997E4BE6...94C0BBE`.
- Reçu : 19 lignes, 613 octets, SHA `5E9D7BFB...42912D2`.
- Rapport pré-reçu SHA `733605E1...A04CCB7F`; version finale append-only.
- A333–A337 est clos, sans live consommé. Phase 5 et goal restent actifs.

Prochaine action : A338 préenregistre le préflight VSTest natif Q106, la sonde
backend indépendante et au maximum un nouveau replay live sur `bin/Debug`.

### Clôture technique A338–A341 — gates fonctionnelles PASS, collision d'assembly

- A338 fige la preuve composée VSTest + identité binaire + sonde backend ;
  protocole pré-A339 SHA `91B2A5E2...0A644BC`.
- A339 passe la parité initiale, puis le test Q106 exact 1/1 sous VSTest ; TRX
  SHA `A2EBF16E...4F3D04D`, storage `bin/Debug` prouvé.
- A320 byte-identique passe les trois frontières, le nom exact de 23 caractères,
  backend ready, résolution complète/exact=1 en 146 ms, identité canonique et
  contexte pages 1–2/neuf chunks.
- La parité finale a été appelée dans le même processus PowerShell qu'A320 ;
  `Assembly.LoadFrom` refuse une seconde `SAAIA.Contracts` homonyme venant d'un
  autre chemin. C'est une collision de contexte, pas un hash divergent.
- Le protocole exigeant toutes les gates vertes, aucune relance : dossier A340
  absent, modèle non démarré, live consommé 0.
- Protocole final 307 lignes, SHA `8091E65D...4F9EA633`; aucun patch/build/
  restore/mutation serveur, processus lourd zéro.

Artefact courant :
`artifacts/goal-rag-product-20260827-1041/phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A338-A341-GATES-FONCTIONNELLES-PASS-COLLISION-ASSEMBLY.md`.

Prochaine action : Telegram A341, puis nouveau protocole imposant un processus
PowerShell frais par sonde avant le replay unique.

### Clôture A341 — Telegram confirmé

- Telegram : une partie, corps 1 827, total 1 955, fichier 1 869 octets/22
  lignes, mojibake 0, SHA `E61BE19D...F05FFE8C`.
- Reçu : 17 lignes, 520 octets, SHA `47297A48...A4E7FDA`.
- Rapport pré-reçu SHA `333054EA...DC214D2`, version finale append-only.
- A338–A341 clos sans live ; phase 5 et goal restent actifs.

Prochaine action : A342 préenregistre l'isolation de processus des quatre gates
avant un nouveau run live unique.

### Clôture A342–A345 — preuve positive sur bon binaire, limites fonctionnelles

- A342 impose quatre gates dans quatre processus frais, un seul replay et une
  parité avant/après ; protocole final 210 lignes, 7 656 octets, SHA
  `E0C3180B...DC94370`.
- A343 passe les deux parités A334, l'intake Q106 VSTest 1/1 depuis
  `bin/Debug`, puis la sonde question/backend/résolution/identité/neuf chunks.
  Rapport SHA `28B3625E...3A29ED1` ; intake TRX SHA
  `2C5CEB49...BAD0A10`.
- A344 consomme exactement un run sur la DLL WinUI A327 SHA
  `153140CA...B1495`. TRX 1/1, SHA `131B19B3...B6ECB1`.
- Q106 résout le PDF exact en 188 ms. Une navigation page 1 non citable mène,
  après une carte lexicale vide, à `documents.context` pages 1–1 sans chunk
  inventé. Quatre passages canoniques citables E2–E5 sont matérialisés.
- La réponse `Fiche technique [E2]` cite le PDF exact, page 1, chunk canonique
  `b969e681...aac4`. Vérification source PASS, contamination 0, `chunk-1` 0,
  notes internes publiées 0.
- BUG123 est PASS live ; BUG124 est PASS sur le chemin accepté ; BUG125 reprend
  productivement après un no-progress. La répétition identique et le terminal
  d'échec restent couverts déterministement.
- Performance : deux appels LLM, 23 événements, harnais 113 442 ms, pipeline
  99 560 ms ; amélioration d'environ 38–42 % face à A321/A331.
- **BUG126 ouvert** : le focus `document_family_or_type` et l'inline answer
  réduisent abusivement une demande d'informations à `Fiche technique`, malgré
  quatre extraits riches.
- **BUG127 ouvert** : E2 est marqué `used`, mais `UsedItems=[]` parce que la
  mémoire n'identifie que les content-cards et omet les passages canoniques
  bruts cités.
- Verdict : mécanique/provenance/parité/performance PASS ; fonction PARTIELLE ;
  mémoire positive FAIL ; UI NON OBSERVABLE. Aucun nouveau live approuvé.
- Rapport initial : 364 lignes, 12 608 octets, SHA
  `E14D71D2...C17227B6`.
- Telegram confirmé : corps 2 444, total 2 576, une partie, mojibake 0, SHA
  `91D08C47...41ABC5A5`; reçu SHA `0A807DC3...8CB3D522`.
- Build servers arrêtés, processus lourd zéro, worktree préservé.

Rapport courant :
`artifacts/goal-rag-product-20260827-1041/phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A342-A345-Q106-PREUVE-POSITIVE-REPONSE-PAUVRE-MEMOIRE-VIDE.md`.

Prochaine action : A346 doit auditer ADR, mémoire, prompts et tests, puis
préenregistrer des rouges génériques pour l'adéquation question/preuves/réponse
et l'identité mécanique des used-items. Qwen reste propriétaire des décisions
sémantiques ; aucune règle Q106/FIT/PTFE et aucun nouveau live avant suites
déterministes complètes.

### Clôture A346–A350 — juge indépendant et mémoire de preuve

- A346 relit l'ADR et isole BUG126 : le raccourci inline était vérifié
  mécaniquement puis exempté de revue sémantique indépendante. Le défaut n'était
  pas l'absence d'un seuil ou d'une regex de richesse.
- A347 reproduit deux rouges causaux 0/2 : deux appels LLM attendus contre un
  observé ; `UsedItems` vide malgré une décision E1 `used`. TRX SHA
  `AF0459FF...42980AA`.
- BUG126 : tout texte inline de l'evidence-review passe désormais un second
  Qwen qui voit demande, plan, brouillon, preuves et alternatives.
- `accept` autorise ; `revise` sans preuve rejetée conserve une fois les mêmes
  EvidenceId vers le writer, puis le brouillon révisé repasse le juge. Aucune
  décision de sens n'est prise par le code.
- BUG127 : une preuve canonique brute citée reçoit
  `evidence:<StableEvidenceKey>` et un label document/page localisé. La
  granularité est le passage ; les autres chunks du document restent libres.
- Les content-cards conservent `content-card:<id>` et leur titre.
- Trois nouveaux contrats : inline+accept, mauvais focus routé mais juge
  obligatoire, `revise → writer → accept` sans nouvelle recherche, plus mémoire
  et modularité dans le lot 5/5 SHA `9F289240...7FB6C6B`.
- Suite runner/mémoire/verifier/architecture : 251/251, SHA
  `22014BE1...5491BD8`.
- La garde de localisation a détecté `p.` en dur ; correction par langue du
  tour, puis suite ciblée 8/8 SHA `9AD49F26...FBBF7C6`.
- Suite complète finale : **1 482/1 482**, zéro échec/skip, TRX SHA
  `7BD5A538...B60A7EA`, soit trois tests de plus que la référence A327.
- WinUI réellement testée et produit x64 : 8 271 360 octets, SHA identique
  `B4C1BCC3...BD877D`.
- Runner exactement 2 500 lignes ; helper partiel 77 lignes ; garde inchangée.
- Scan produit Q106/FIT/PTFE/scénarios tests : zéro. Whitespace, modularité et
  diff check PASS.
- Protocole final 384 lignes, 14 107 octets, SHA
  `FE4EC846...7A1F8A31`; audit A349 SHA `520D2088...76E5EC71`.
- Rapport initial 296 lignes, 8 767 octets, SHA
  `C3C1CBCB...21DCBE38`.
- Telegram confirmé : corps 2 580, total 2 713, une partie, mojibake 0, SHA
  `940CEF16...A7772A1`; reçu SHA `B069418C...60E2555`.
- Aucun modèle/backend/ingestion ; build servers arrêtés, processus lourd zéro,
  worktree préservé à 549 entrées.

Verdicts : BUG126 et BUG127 PASS déterministe. Q106 post-patch, `UsedItems`
live, performance et UI restent non rejoués/non approuvés. Phase 5 et goal
restent actifs.

Rapport courant :
`artifacts/goal-rag-product-20260827-1041/phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A346-A350-REVUE-SEMANTIQUE-ET-MEMOIRE-PREUVE.md`.

Prochaine action : préenregistrer A351–A354, prouver la parité du nouveau binaire
et les gates Q106 dans des processus isolés, puis consommer au maximum un replay
live unique. Le verdict devra séparer réponse utile, provenance, mémoire,
performance et UI.

### Clôture A351–A354 — juge live actif, alternative préférée perdue avant writer

- A351 préenregistre un unique replay Q106 sur le binaire A350 et sépare
  explicitement fonction, provenance, mémoire, performance et UI. Protocole
  initial 303 lignes/9 603 octets, SHA `2B228A81...7002278` ; après clôture A352,
  330 lignes/10 621 octets, SHA `90D7A7A4...0D59C7D`.
- A352 passe quatre gates en quatre processus : parité A350 initiale, intake
  Q106 VSTest natif 1/1, question/backend/résolution exacte/identité/neuf
  chunks, puis parité finale byte-identique.
- Rapport A352 : 248 lignes, 8 921 octets, SHA
  `418F2D6D...C6DF9A80`. Banque, Qwen Q5, runtime, DLL et full TRX 1 482/1 482
  restent conformes. Zéro variable live héritée et zéro processus lourd.
- A353 consomme exactement un live, sans Platform/build/restore/retry. VSTest
  capture 1/1 PASS, exit 0, durée TRX 179,1296549 s.
- Le PDF exact est résolu et E2–E5 sont récupérés page 1 avec identité
  canonique. Le brouillon inline reste `Fiche technique [E2]` et passe le
  vérificateur mécanique.
- **BUG126 PASS live** : `semantic_review.completed=1`, skip=0. Qwen décide
  `revise`, rejette E2 et préfère E3. La réponse pauvre A344 ne fuit plus.
- **BUG128 ouvert** : le runner ne transforme pas
  `PreferredAlternativeEvidenceIds=E3` en transition bornée vers le writer dès
  lors que E2 est rejeté. L'état de sélection est effacé et un tour général
  repart.
- Ce tour appelle à nouveau `documents.content_cards` sans rendement. Le
  preflight contexte bloque ensuite à input 4 012 / contexte 4 096, après deux
  récupérations et une réduction de sortie à 67 tokens.
- Réponse finale : refus technique sûr de 157 caractères, sourceCount 0,
  `sourcesPayload=null`, flags `no_sources,corpus_target_not_cited`.
- Verdict fonction : FAIL. La protection terminal empêche un fallback non
  vérifié, mais aucune information du PDF n'est livrée.
- BUG127 reste PASS déterministe sur le binaire identique, mais **NON DÉMONTRÉ
  live** : l'échec avant `BuildResult` ne produit ni mémoire finale ni
  `UsedItems`.
- Performance A353 : harnais 178 776 ms, pipeline 164 790 ms, soit +57,6 % et
  +65,5 % face à A344 ; sept décisions/appels LLM visibles, quatre outils,
  deux no-progress, 53 événements.
- Provenance retrieval et contamination PASS ; provenance finale non
  applicable ; UI non observable/non approuvée.
- Artefacts A353 : TRX SHA `15AB8237...5ED143E`, JSON
  `9203F4D3...2BE2802`, JSONL `A6EFE03C...3481EBC`, TSV
  `3B34EC5B...9458EFE`.
- Audit A354 : 326 lignes, 12 860 octets, SHA
  `F61E5117...267EE02`. DLL et sources A350 inchangées, diff check ciblé 0,
  processus lourd zéro, worktree 549 entrées préservé.

Verdict : A351–A354 est clos comme palier probant, mais la phase 5 reste
**TESTÉE NON APPROUVÉE**. Aucun second A353 ne sera exécuté.

Rapport courant :
`artifacts/goal-rag-product-20260827-1041/phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A351-A354-Q106-JUGE-ACTIF-TRANSITION-ALTERNATIVE-MANQUANTE.md`.

Prochaine action : A355–A359 doit préenregistrer et tester en rouge le handoff
générique des alternatives préférées. Si Qwen retourne `revise` avec des
EvidenceId alternatifs déjà présents, le code valide seulement leur existence,
citabilité et cohérence, transmet cette sélection au writer une fois, puis
revérifie et réaudite le nouveau brouillon. Le tour général reste réservé au cas
où aucune alternative valide n'existe. Aucun Q106/PDF/langue/contenu en dur et
aucun agrandissement opportuniste du contexte.

### Clôture communication A354 — Telegram confirmé

- message en une partie : corps 2 988, total 3 056, mojibake 0 ;
- message SHA `7362562E...7B3FA0A` ;
- confirmation API true, message id 2692 ;
- reçu local 29 lignes/802 octets, SHA `611DB536...8785A6C` ;
- rapport initial pré-reçu SHA `2BB5218E...E7CF29CE`.

A351–A354 est entièrement clos. Le goal et la phase 5 restent actifs ; A353 ne
sera jamais rejoué. A355 peut maintenant préenregistrer le contrat BUG128 avant
tout patch.

### Palier A355–A359 — BUG128 transformé en transition writer bornée

Date : 2026-08-29, Europe/Zurich  
Statut : **PASS DÉTERMINISTE — LIVE NON APPROUVÉ**

- A355 a préenregistré le handoff générique des alternatives préférées avant
  toute modification produit.
- A356 a obtenu le rouge causal 0/1 : après rejet de E1 et préférence E3, le
  runner repartait dans les contrats généraux au lieu du writer.
- A357 transmet désormais au helper le bundle, les IDs observés/rejetés et la
  sélection active. Le code conserve uniquement les alternatives choisies par
  Qwen qui sont mécaniquement présentes, observées, citables, admissibles pour
  le document demandé, non rejetées et rendables.
- La sélection active est remplacée dans l'ordre de Qwen, le writer est demandé
  une seule fois, puis la vérification mécanique et la revue indépendante sont
  rejouées.
- Aucun jugement de contenu, vocabulaire, domaine, longueur ou richesse n'a été
  ajouté. Aucun budget, timeout, contexte, tour ou nombre d'outils n'a augmenté.
- Le fixture causal final reproduit la topologie live A353 : E1–E3 observés dans
  le même document, sur la même page et sous la même requête. Il évite une revue
  de portée étrangère au contrat testé.
- Vert causal 1/1, gardes 6/6, runner 213/213, lot runner–mémoire–verifier–
  architecture 252/252, client intégral 1 483/1 483.
- Le runner reste exactement à 2 500 lignes. Le diagnostic temporaire est
  absent. Les DLL WinUI testée et x64 sont byte-identiques.
- `git diff --check` ciblé exit 0 ; contrôle direct des fichiers non suivis sans
  espace final ; worktree sale 549 entrées préservé ; branche `SAAIA_V3.1` et
  HEAD `5f35881cdc67d12a076fcd2a7a1004656ac9a37a` inchangés.
- Variables live absentes, build servers arrêtés, processus de validation lourd
  zéro. Aucun replay live n'a été exécuté pendant A355–A359.

Verdicts dynamiques :

| Sujet | Verdict courant |
|---|---|
| BUG126 — juge indépendant inline | PASS live A353 |
| BUG128 — alternative préférée vers writer | PASS déterministe A357–A358 |
| BUG127 — mémoire après succès | non observable live |
| Q106 final source-backed | FAIL live A353 |
| performance | FAIL A353 |
| sources/cartes UI | non observables |
| phase 5 globale | TESTÉE NON APPROUVÉE |

Rapport courant :
`artifacts/goal-rag-product-20260827-1041/phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A355-A359-BUG128-HANDOFF-WRITER-DETERMINISTE.md`.

Prochaine action autorisable : préenregistrer un nouveau palier live, avec
préflight de parité et un unique replay isolé, pour observer la transition E3
vers writer, la réponse finale source-backed, BUG127 après succès, les sources
UI et la latence. Aucun replay n'est implicitement autorisé par les verts
déterministes A358.

### Clôture communication A359 — Telegram confirmé

- message en une partie : 2 116 caractères, mojibake 0 ;
- confirmation API true, message id 2693 ;
- message SHA `DAD50D96...6F93EB7` ;
- reçu local 36 lignes/992 octets, SHA `D39AB83D...9954094` ;
- rapport initial pré-reçu SHA `B448CCC2...2A4F04F`.

A355–A359 est entièrement clos comme palier déterministe. La phase 5 et le goal
restent actifs ; aucun live n'a été rejoué dans ce palier.

### Ouverture A360–A364 — observation live du handoff BUG128

- Protocole préenregistré avant toute gate et avant tout démarrage modèle : 292
  lignes, 10 669 octets, SHA-256
  `BF2A6D0CD49371F965EBAED8D791EAAFAE803DC3D15C4332369463A31DA41553`.
- A361 doit prouver quatre gates isolées sur le binaire A357 : WinUI SHA
  `49251098...4A9BD8`, tests SHA `D63204C1...D3DC5`, TRX 1 483/1 483 SHA
  `096DB294...61D3E`.
- A362 autorise exactement un run live Q106, sans build, restore, Platform ni
  retry. Le dossier réservé est absent à l'ouverture.
- BUG128 live est PASS seulement si le chemin alternatif est réellement
  déclenché et trace handoff → writer → vérification → seconde revue. Une autre
  décision Qwen rend le contrat non observé, jamais artificiellement PASS.
- BUG127, payload UI, réponse utile, provenance et performance ont des verdicts
  séparés. Le rendu visuel WinUI n'est pas inféré du harnais xUnit.
- Aucun patch produit/test n'est permis entre A361-1 et la clôture A364.

Protocole :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A360-A364-REPLAY-LIVE-UNIQUE-Q106-APRES-HANDOFF-ALTERNATIVE.md`.

### A361 — préflight A357 clos

- parité initiale PASS ;
- intake natif 1/1 PASS ;
- résolution Q106 complète exact=1 et neuf chunks ;
- banque/modèle/runtime conformes ;
- parité finale PASS byte-identique ;
- dossier A362 resté absent ;
- rapport 141 lignes/5 657 octets, SHA
  `EC4557876755BAA4E65AC7EC9D5B84B51D3C8904AAFEECE58DBAE2242D231FE3`.

A362 est maintenant autorisé une seule fois, sans retry quelle que soit son
issue.

### A362–A363 — BUG128 observé live, chaîne E5 bloquée

- A362 a été exécuté exactement une fois : VSTest 1/1 PASS, exit 0, aucune
  relance, quatre artefacts.
- Le PDF exact est résolu ; E2–E5 sont récupérés page 1 après deux appels
  `documents.content_cards` sans rendement.
- BUG126 reste PASS : le brouillon `Fiche technique [E2]` est vérifié
  mécaniquement puis revu indépendamment ; aucun skip.
- Première revue : `revise`, E2 rejeté, E3 préféré.
- **BUG128 PASS live** : trace alternative E2→E3, writer direct sans outil,
  E3 seule, vérification mécanique PASS, seconde revue indépendante.
- Le writer E3 ne restitue qu'un type documentaire générique. Le second juge le
  rejette correctement et préfère E5, déjà observé et rendable.
- **BUG129 ouvert** : la borne `inlineFastAnswerDirectRevisionAttempted` est
  déjà consommée ; E5 ne peut pas être transmis au writer. Le chemin général
  reprend puis bloque le contexte à 4 023/4 096.
- Réponse finale : refus sûr de 157 caractères, sourceCount 0,
  `sourcesPayload=null`, flags `no_sources,corpus_target_not_cited`.
- BUG127 reste non observable ; payload UI fail ; rendu visuel UI non
  observable.
- Performance A362 : harnais 219 206 ms, pipeline 205 269 ms, soit +22,6 % et
  +24,6 % contre A353, et +93,2 %/+106,2 % contre A344.
- Dix appels LLM visibles, quatre outils, deux no-progress, 59 événements.
- Sources produit inchangées ; variables live absentes ; processus lourd zéro ;
  worktree 549 entrées préservé.

Audit :
`artifacts/goal-rag-product-20260827-1041/phase5/A363-AUDIT-LIVE-Q106-BUG128-PASS-CHAINE-ALTERNATIVE-BUG129.md`,
SHA `61E9940C25A6B2C7FA791B2922FBE760EFCB797228F47EFC621E7B9232B0123F`.

Verdict : A360–A364 confirme le correctif BUG128 en live, mais la phase 5 reste
**TESTÉE NON APPROUVÉE**. A364 doit maintenant figer et communiquer ce palier.

### Clôture A364 — Telegram confirmé

- rapport initial SHA `D9D9014F...448F2C0` ;
- message Telegram en une partie, 2 151 caractères, mojibake 0 ;
- confirmation API true, message id 2694 ;
- message SHA `A4732004...B035118` ;
- reçu 30 lignes/781 octets, SHA `809DDE32...FBE5C81`.

A360–A364 est entièrement clos. A362 ne sera jamais rejoué. Le goal et la
phase 5 restent actifs ; la prochaine action est un palier TDD BUG129, pas un
nouveau live.

### Ouverture A365–A369 — chaînage borné anti-cycle

- Protocole préenregistré avant test rouge et patch : 263 lignes, 9 043 octets,
  SHA `F50F945E90A69F9CBC02BA61554B49A1032D6A550CC003279DD6B8075A10F49B`.
- Rouge causal attendu : E1 rejeté → E3 writer → E3 rejeté → E5 préféré ; le
  binaire A357 doit échouer avant le second writer.
- La correction utilisera uniquement le budget existant
  `max(1, MaximumSemanticCorrectionTurns)` et des signatures mécaniques de
  sélection déjà tentées.
- L'ordre Qwen est conservé pour le writer, mais la signature anti-cycle est
  indépendante des permutations.
- Chaque handoff impose writer, vérification mécanique et nouvelle revue ;
  aucune alternative n'est choisie par le code.
- Runner limité à 2 500 lignes ; aucun prompt, backend, ingestion, mémoire, UI,
  timeout, contexte, tour ou outil modifié.
- A365–A369 est entièrement déterministe : aucun live autorisé.

Protocole :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A365-A369-CHAINAGE-BORNE-ALTERNATIVES-PREFEREES-ANTI-CYCLE.md`.

### A366 — rouge causal BUG129

- test chaîne E1→E3→E5 : 0/1 ;
- la première révision directe est consommée ; après le second `revise`, le
  runner réexpose les outils généraux et les contrats de sélection ;
- TRX 69 lignes/9 342 octets, SHA
  `4D0192167F005586DD77C9C369B22F8D1949312591A57D6F6B650A40D5E2D7D6` ;
- produit inchangé, DLL WinUI toujours SHA A357 ;
- A367 autorisé, aucun live.

### A367–A368 — BUG129 corrigé et audité déterministement

- Le verrou booléen global est remplacé par un ensemble de signatures de
  sélection déjà transmises au writer.
- La borne est le budget existant
  `max(1, MaximumSemanticCorrectionTurns)` ; aucun budget, timeout, contexte,
  tour, outil ou option n'a été augmenté.
- Qwen conserve la décision et l'ordre des alternatives. Le code vérifie
  seulement présence, observation, citabilité, rejet, rendu et répétition
  mécanique.
- La signature anti-cycle est indépendante de l'ordre, de la casse, des espaces
  et des doublons. La sélection initiale n'est pas préinscrite, donc une
  première révision sur la même preuve reste possible.
- Chaque sélection directe distincte impose writer, vérification mécanique et
  nouveau juge indépendant.
- Causal E1 → E3 → E5 : 1/1 PASS ; deux transitions 1/2 puis 2/2 ; décisions
  `revise`, `revise`, `accept` ; un seul appel documentaire.
- Une fixture écrivait `E3` puis `[E3]` et a été correctement refusée par le
  contrat exact-once. Le texte de test a été corrigé, pas le vérificateur.
- Gardes A357+A367 : 9/9 PASS ; runner 216/216 ; runner–mémoire–verifier–
  architecture 255/255 ; client complet 1 486/1 486.
- DLL WinUI testée et x64 byte-identiques, SHA
  `4CB8421858...4507D6228` ; runner exactement 2 500 lignes.
- Scan hardcodes/diagnostics propre ; prompts, schémas, backend, ingestion,
  mémoire et UI inchangés ; aucun live A365–A369.

Verdicts dynamiques :

| Sujet | Verdict courant |
|---|---|
| BUG126 | PASS live A362 |
| BUG128 | PASS live A362 |
| BUG129 | PASS déterministe A367–A368 ; live non exécuté |
| BUG127 | non observable live |
| Q106 final | FAIL live A362 |
| performance | FAIL A362 |
| payload/rendu UI | FAIL ou non observable |
| phase 5 | TESTÉE NON APPROUVÉE |

Rapport courant :
`artifacts/goal-rag-product-20260827-1041/phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A365-A369-BUG129-CHAINAGE-BORNE-DETERMINISTE.md`.

A369 doit envoyer le rapport Telegram et figer son reçu. Après clôture, un
nouveau palier live distinct pourra être préenregistré avec parité binaire et
un seul run sans retry.

### Clôture A369 — Telegram confirmé

- message en une partie : 2 709 caractères, mojibake 0 ;
- confirmation API true, message id 2695 ;
- message SHA `97C9ED2D...BFD93E3` ;
- reçu local `00-RECU-TELEGRAM-A365-A369.md` ;
- rapport initial SHA `D7D8BF7C...F67F1D2` ;
- build servers arrêtés, variables live absentes, processus lourd zéro.

A365–A369 est entièrement clos. BUG129 reste PASS déterministe sans preuve live.
Le Goal et la phase 5 restent actifs ; le prochain palier devra être un live
distinct, préenregistré et strictement unique.

### Ouverture A370–A374 — replay live unique après chaînage borné

- Protocole préenregistré avant gate et avant modèle :
  `artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A370-A374-REPLAY-LIVE-UNIQUE-Q106-APRES-CHAINAGE-BORNE.md`.
- A371 doit prouver quatre gates sur les DLL A367/A368 et le TRX
  1 486/1 486, puis l'intake exact et la résolution Q106 en lecture seule.
- Le dossier A372 est absent à l'ouverture. Il ne sera créé qu'après quatre
  gates PASS.
- A372 autorise un seul nouveau run Q106, sans build, restore, `Platform` ni
  retry. A362 reste consommé et ne sera jamais relancé.
- BUG129 live exige une deuxième transition directe distincte réellement
  observée, avec tentative 2/2, writer sans outil, vérification et nouvelle
  revue. Sinon le contrat est NON OBSERVÉ.
- Réponse, mémoire, payload UI, rendu visuel et performance gardent des verdicts
  séparés. Un PASS xUnit ne vaut pas approbation produit.
- Aucun patch produit/test n'est permis entre A371-1 et la clôture A374.

### A371–A373 — gate de sonde échouée, live non exécuté

- A371-1 a échoué avant son marqueur PASS sur le contrôle regex du champ
  `storage` VSTest.
- Cause : les backslashes du chemin Windows n'étaient pas doublés dans le motif
  regex. Une lecture XML directe confirme pourtant le bon assembly `bin/Debug`.
- Les hashes comparés avant ce contrôle n'avaient pas dérivé : aucune régression
  binaire n'est observée.
- Le protocole interdisait A372 après une gate rouge. Aucun contournement :
  dossier A372 absent, aucun live, modèle, backend ou patch produit/test.
- BUG129 reste PASS déterministe, live non exécuté ; les verdicts A362 restent
  inchangés.
- Le budget du futur replay unique n'est pas consommé, mais il devra porter de
  nouveaux identifiants et un nouveau protocole.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A370-A374-PREFLIGHT-STOP-SANS-LIVE.md`.

### Clôture A374 — Telegram confirmé

- API true, message id 2696 ;
- une partie, 1 303 caractères, mojibake 0 ;
- message SHA `371972B8...D5C0DE4` ;
- reçu `00-RECU-TELEGRAM-A370-A374.md` ;
- A372 absent et live non consommé.

A370–A374 est clos. Le prochain palier corrigera uniquement la sonde sous de
nouveaux identifiants avant toute gate probante.

### Ouverture A375–A379 — sonde XML canonique et replay unique

- Nouveau protocole préenregistré avant gate :
  `artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A375-A379-REPLAY-LIVE-UNIQUE-Q106-SONDE-XML-CANONIQUE.md`.
- La nouvelle sonde n'utilise plus de regex pour `storage`. Elle parse le TRX,
  canonicalise son unique chemin et le compare à l'assembly attendu.
- A375 permet seulement une qualification syntaxique/statique avant gate ;
  aucun hash n'est validé par cette qualification.
- A376 reprend quatre gates sous de nouveaux identifiants.
- A377, dossier absent à l'ouverture, sera un unique run sans retry après quatre
  gates PASS seulement.
- Produit/test A367/A368 figé ; critères BUG129, réponse, mémoire, UI et
  performance inchangés et séparés.

### A376–A378 — comparaison XML sensible à la casse, live non exécuté

- La sonde XML a extrait le bon `storage`, mais l'a comparé avec `-cne`.
- Le chemin VSTest en minuscules et le chemin disque avec casse préservée sont
  équivalents sous Windows ; l'instrumentation les a refusés.
- Les hashes antérieurs n'avaient pas dérivé, mais la gate complète n'est pas
  PASS. A377 a donc été strictement interdit.
- Dossier A377 absent ; aucun modèle/backend, variable live ou patch
  produit/test ; futur replay non consommé.
- BUG129 reste PASS déterministe et non confirmé live.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A375-A379-PREFLIGHT-STOP-CASSE-SANS-LIVE.md`.

### Clôture A379 — Telegram confirmé

- API true, message id 2697 ;
- message SHA `6E7F16ED...105DD4A` ;
- reçu `00-RECU-TELEGRAM-A375-A379.md` ;
- A377 absent, live non consommé.

A375–A379 est clos. Le prochain palier exercera explicitement la comparaison
insensible à la casse sur le storage XML réel avant sa gate formelle.

### Ouverture A380–A384 — sonde Windows qualifiée

- Protocole préenregistré :
  `artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A380-A384-REPLAY-LIVE-UNIQUE-Q106-SONDE-WINDOWS-QUALIFIEE.md`.
- Avant protocole, diagnostic read-only : storage unique, égalité
  `OrdinalIgnoreCase` vraie, huit méthodes runner, 19 paramètres et deux
  méthodes mémoire présents.
- A381 reprend les quatre gates avec comparaison XML canonique explicitement
  insensible à la casse.
- A382 est le seul live autorisable, sous un nouveau dossier absent, après
  quatre gates PASS ; no-build/no-restore, aucun Platform ni retry.
- Produit/test A367/A368 reste figé ; critères BUG129, produit, mémoire, UI et
  performance restent séparés.

### A381 — quatre gates PASS

- Parité initiale/finale : PASS, sorties byte-identiques SHA
  `4A543A36...18AC32DC`.
- Intake natif : 1/1 PASS, PDF exact longueur 23, WinUI inchangée, TRX SHA
  `133EF939...624A19`.
- A320 : Q106 unique, `/ready`, résolution complète exact=1, identité canonique,
  pages 1–2 et neuf chunks.
- Banque, modèle et runtime : tailles/hashes attendus ; modèle non démarré.
- Dossier A382 absent, variables live absentes, processus lourd zéro.
- A382 autorisé exactement une fois, sans build, restore, Platform ni retry.

### A382–A383 — chaîne et mémoire validées live, réponse rejetée

- A382 a été exécuté exactement une fois, sans build, restore, `Platform` ni
  retry. Le harnais est 1/1 PASS et a produit exactement TRX, JSON, JSONL et
  TSV.
- Durées : 270 280 ms harnais, 255 268 ms au `turn.final`. Les processus lourds
  ont été arrêtés immédiatement après le live.
- Résolution exacte : docId, path, revision et sourceHash canoniques ; E2–E5
  sont quatre chunks page 1 du seul PDF demandé.
- BUG129 PASS live : après E2, Qwen choisit E3, puis E5, puis E4 ; trois
  signatures distinctes, trois writers directs sans outil, quatre
  vérifications mécaniques et quatre revues indépendantes ; zéro skip, cycle,
  récupération ou blocage de contexte.
- BUG127 PASS live : E4 a une décision `used`, un `UsedItem`, une stable key et
  une identité brute de chunk égales, plus le label document/page attendu.
- Payload UI PASS structurel : une source E4 canonique non nulle. Rendu WinUI
  non observable dans le xUnit.
- Réponse finale FAIL : 156 caractères, aucune valeur technique. Elle annonce
  propriétés, traitement et conditionnement avec la seule citation E4, qui ne
  contient que `Processing Information`. E3 contient les propriétés et E5 le
  conditionnement mais sont absents des sources finales.
- Performance FAIL : contre A362, +23,3 % harnais et +24,4 % pipeline ; contre
  A344, +138,3 % et +156,4 %.
- BUG130 ouvert : le pipeline confond la cardinalité de l'objet demandé (un
  document) avec celle des preuves nécessaires à sa synthèse ; les chunks
  complémentaires sont traités comme alternatives exclusives.

Verdicts dynamiques :

| Sujet | Verdict courant |
|---|---|
| BUG126 | PASS live |
| BUG128 | PASS live |
| BUG129 | **PASS live A382** |
| BUG127 | **PASS live A382** |
| résolution/provenance Q106 | PASS live |
| réponse substantielle | **FAIL live A382** |
| alignement affirmation–citation | **FAIL live A382** |
| payload source UI | PASS structurel live |
| rendu WinUI | NON OBSERVABLE |
| performance | **FAIL A382** |
| BUG130 composition multi-preuves | **OUVERT** |
| phase 5 | **TESTÉE NON APPROUVÉE** |

Audit :
`artifacts/goal-rag-product-20260827-1041/phase5/A383-AUDIT-LIVE-Q106-BUG129-BUG127-PASS-REPONSE-INSUFFISANTE.md`.

Rapport de palier :
`artifacts/goal-rag-product-20260827-1041/phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A380-A384-BUG129-BUG127-PASS-LIVE-BUG130-OUVERT.md`.

A384 doit envoyer Telegram, figer le reçu et maintenir les ressources lourdes
à zéro. Le palier suivant sera un recul architectural/TDD BUG130 avant tout
nouveau live : comparer composition multi-passages, sélection multiple Qwen et
séparation objet/preuves, sans hardcode de corpus ni augmentation de budget.

### Clôture A384 — Telegram confirmé

- rapport A380–A384 publié ;
- message Telegram envoyé en une partie via fichier UTF-8 et
  `-RequireMessage` ;
- notifier : corps 2 466 caractères, total 2 548 ; texte retourné vérifié
  ordinalement ;
- message SHA `1413E42D...B62035` ;
- reçu `phase5/00-RECU-TELEGRAM-A380-A384.md` ;
- aucun second A382, variables live absentes, processus lourd zéro.

A380–A384 est entièrement clos. BUG129 et BUG127 sont PASS live. Le Goal et la
phase 5 restent actifs à cause de BUG130, de la réponse finale insuffisante et
de la performance. Aucun nouveau live n'est autorisé avant le prochain
protocole causal déterministe.

### Ouverture A385–A389 — séparation objet cible / preuves de contenu

- Protocole préenregistré avant test rouge et patch :
  `artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A385-A389-BUG130-SEPARATION-OBJET-CIBLE-PREUVES-DE-CONTENU.md`.
- Cause candidate : contradiction du prompt routeur entre unité de réponse
  « jamais un document » et `named_item` pour un document même quand son
  contenu doit être décrit.
- Option retenue : exercer l'expressivité existante — `content_claim`,
  `open_set` 2–3 et overview — avant d'ajouter un nouveau schéma.
- Le document cible peut être singulier tandis que les preuves/claims de sa
  synthèse sont multiples ; Qwen décide cette cardinalité et ces faits.
- Aucun agrégat mécanique de tous les chunks, aucune facette inventée, aucun
  hardcode de banque/corpus, aucun budget augmenté.
- A386 doit obtenir un rouge de contrat de prompt généraliste. A387 ne pourra
  modifier que le contrat textuel minimal si ce rouge est causal.
- A385–A389 est entièrement déterministe : aucun live autorisé.

### A386–A388 — BUG130 corrigé déterministement

- Rouge 1 : le prompt ne séparait pas document cible et claims de réponse,
  0/1, TRX SHA `C64FE6FB...F4CFF`.
- Le contrat routeur demande maintenant `content_claim` pour le contenu interne
  d'un objet nommé, `named_item/single_item` seulement pour l'objet lui-même et
  overview `open_set` 2–3 pour une synthèse ouverte sans quantité/facettes.
- Une première validation a correctement bloqué un prompt général de 4 190
  caractères ; le texte a été compacté sous la limite 4 000, sans augmenter le
  budget. Deux fixtures sensibles aux retours à la ligne ont été corrigées.
- Audit secondaire : le normaliseur réécrivait encore tout overview multiple
  en `explicit_set`.
- Rouge 2 : overview ouvert fictif, attendu `open_set`, observé
  `explicit_set`, 0/1, TRX SHA `FBE241D2...F0D1F5F`.
- Correction mécanique : préserver `open_set` uniquement sans nombre explicite,
  sans facette, count 2–3 et choix Qwen explicite. Quantités/facettes restent
  `explicit_set` ; count1 reste `single_item`.
- Validation finale : causal 1/1 ; routeur 68/68 ;
  routeur+overview+normalisation 179/179 ; source-backed 216/216 ; client
  1 488/1 488.
- DLL WinUI testée/x64 byte-identiques SHA `712A3F14...55F4E3C` ; runner,
  fast review, juge, overview router, mémoire, UI, backend et budgets inchangés.
- Scan produit : zéro Q106/FIT/PTFE/Cuisine/dessert, zéro diagnostic ;
  diff-check vert ; processus lourd zéro ; aucun live A385–A389.

Verdicts dynamiques :

| Sujet | Verdict courant |
|---|---|
| BUG129 | PASS live A382 |
| BUG127 | PASS live A382 |
| BUG130 routeur/transport | **PASS déterministe A388** |
| BUG130 produit end-to-end | NON OBSERVÉ depuis correction |
| réponse finale | FAIL live A382 |
| performance | FAIL A382 |
| phase 5 | TESTÉE NON APPROUVÉE |

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A385-A389-BUG130-ROUTEUR-OPEN-OVERVIEW-DETERMINISTE.md`.

A389 doit envoyer Telegram et figer le reçu. Le palier suivant pourra seulement
préenregistrer une parité fraîche et un replay live unique sous de nouveaux
identifiants.

### Clôture A389 — Telegram confirmé

- message envoyé en une partie via fichier UTF-8 et `-RequireMessage` ;
- notifier : corps 2 338 caractères, total 2 420, texte retourné vérifié
  ordinalement ;
- message SHA `6314D1A6...A83A2239` ;
- reçu `phase5/00-RECU-TELEGRAM-A385-A389.md` ;
- aucun live A385–A389, variables live absentes, processus lourd zéro.

A385–A389 est entièrement clos. BUG130 est PASS déterministe au niveau
routeur/transport, mais non observé live. Le Goal et la phase 5 restent actifs.

### Ouverture A390–A394 — replay live unique après open overview

- protocole préenregistré avant gate et modèle :
  `artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A390-A394-REPLAY-LIVE-UNIQUE-Q106-APRES-OPEN-OVERVIEW.md` ;
- binaire figé sur les 1 488/1 488 tests A388 ;
- A391 impose deux parités Windows byte-identiques, intake natif et A320 ;
- A392 reste absent et n'est autorisé qu'après quatre gates PASS ;
- un seul replay Q106, no-build/no-restore, sans Platform ni retry ;
- `content_claim/open_set` est PASS live seulement si Qwen le choisit et si le
  transport conserve réellement plusieurs claims complémentaires ;
- réponse, citations, mémoire, payload UI, rendu et performance gardent des
  verdicts séparés ;
- aucun patch produit/test entre A391-1 et A394.

### A391–A393 — sonde multiligne rouge, live non exécuté

- A391-1 a validé les hashes initiaux puis échoué sur un marqueur BUG130
  présent mais séparé par un retour à la ligne dans le source.
- Cause : recherche ordinale sur texte brut sans normalisation des espaces.
- A391-1 n'a pas atteint PASS ; A391-2/3/4 et A392 sont interdits.
- Dossier A392 absent, variables live zéro, modèle/backend live zéro, processus
  lourd zéro ; produit/test et hashes A388 inchangés.
- BUG130 reste PASS déterministe, non observé live ; les verdicts A382 restent
  inchangés.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A390-A394-PREFLIGHT-STOP-MARQUEUR-MULTILIGNE-SANS-LIVE.md`.

A394 doit notifier Telegram. Le prochain palier portera de nouveaux
identifiants et une sonde normalisant les espaces, intégralement qualifiée avant
sa gate formelle.

### Clôture A394 — Telegram confirmé

- une partie, corps 1 016, total 1 098 caractères ;
- message SHA `85B2FEC2...107C8676` ;
- reçu `phase5/00-RECU-TELEGRAM-A390-A394.md` ;
- A392 absent, live non consommé.

A390–A394 est clos. Le Goal reste actif.

### Ouverture A395–A399 — sonde espaces qualifiée et replay unique

- La nouvelle sonde A396 normalise uniquement les espaces du source routeur
  avant de rechercher les marqueurs BUG130 ; elle ne modifie aucun contrat
  produit.
- Elle a été entièrement qualifiée hors gate : AST 0, exécution code 0,
  19 lignes, sortie SHA `3F3C9B26...1FDB4`, dossier A397 absent.
- Protocole préenregistré avant gates et live :
  `artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A395-A399-REPLAY-LIVE-UNIQUE-Q106-SONDE-ESPACES-QUALIFIEE.md`.
- A396 impose parité initiale/finale byte-identique, intake natif, A320,
  banque/modèle/runtime figés et zéro patch produit/test.
- A397 autorise exactement un Q106, sans build, restore, `Platform` ni retry.
- A398 sépare route BUG130, vérité documentaire, alignement de chaque claim,
  mémoire, payload UI, rendu et performance.

### A396 — quatre gates PASS

- parité initiale/finale : 19 lignes byte-identiques, SHA
  `3F3C9B26...1FDB4` ;
- DLL tests `8C9A3E85...269A05E`, WinUI `712A3F14...55F4E3C`,
  Contracts `86E911D3...EBAAC` ;
- TRX complet figé 1 488/1 488, storage unique vers la DLL test attendue ;
- intake natif : 1/1 PASS, PDF exact longueur 23, WinUI inchangée, TRX
  `4D70CA55...92012` ;
- A320 : Q106 unique, `/ready`, résolution exacte complète, identité canonique,
  pages 1–2, 9 chunks ;
- banque `84E675B7...FB74C`, modèle `66713CE3...1439DC`, runtime
  `9555847A...25C1650` ;
- variables live 0, processus lourds 0, dossier A397 absent.

### A397–A398 — réponse concrète mais citation fine invalide

- A397 exécuté une seule fois ; TRX 1/1 PASS, quatre artefacts, aucun retry.
- Durées : 140 388 ms harnais et 126 217 ms pipeline, soit environ deux fois
  plus rapide qu'A382 mais encore +23,8/+26,8 % contre A344.
- Réponse : norme ASTM, poudre semi-fluide, densité 850 g/l et résistance
  4000 psi, avec la seule citation E3.
- Route live : `named_item/single_item`, une fiche ;
  `content_claim/open_set` n'a pas été choisi malgré le contrat A387.
- E3 est le chunk `Features and Benefits` : il soutient la norme et la poudre,
  mais ne contient ni 850 g/l ni 4000 psi.
- Les deux valeurs sont dans le chunk voisin E4 `Typical Properties`, absent du
  payload final.
- Trace causale : quatre chunks entrent dans la revue rapide, mais une seule
  fenêtre candidate est construite. Le writer reçoit les voisins E3–E6 sous
  l'identité représentative E3 et peut donc utiliser E4 tout en citant E3.
- `SourceContractVerifier` valide mécaniquement le marqueur existant ; source
  payload et mémoire conservent seulement E3.
- BUG127 PASS live : décision used, UsedItem, stable key et ItemIdentity E3
  cohérents. BUG129 non observé.
- Payload UI PASS mécanique mais FAIL sémantique ; rendu visuel non observé.
- Script read-only A398 conclut explicitement
  `A397_FINE_GRAINED_CITATION_ALIGNMENT=FAIL`.

Verdicts dynamiques :

| Sujet | Verdict courant |
|---|---|
| BUG127 mémoire de preuve | PASS live A397 |
| BUG129 chaînage alternatives | PASS live A382, NON OBSERVÉ A397 |
| BUG130 prompt/transport | PASS déterministe A388 |
| BUG130 route Qwen | **FAIL live A397 : named_item/single_item** |
| BUG130 fenêtre source / claims | **FAIL live A397** |
| résolution et vérité au niveau document | PASS live |
| réponse concrète | PASS partiel |
| alignement claim–chunk–citation | **FAIL live A397** |
| payload UI | PASS mécanique, **FAIL sémantique** |
| rendu WinUI | NON OBSERVÉ |
| performance | améliorée, encore FAIL vs A344 |
| phase 5 | **TESTÉE NON APPROUVÉE** |

Audit :
`artifacts/goal-rag-product-20260827-1041/phase5/A398-AUDIT-LIVE-Q106-BUG130-FENETRE-SOURCE-ALIAS-CITATION.md`.

A399 doit publier le rapport de palier et Telegram, figer le reçu, maintenir
les ressources lourdes à zéro et laisser le Goal actif. Le prochain palier doit
être un TDD déterministe : conserver le contexte de fenêtre nécessaire à Qwen
tout en donnant une identité citable distincte à chaque chunk dont un claim est
repris. Aucun nouveau live n'est autorisé avant ce correctif et ses suites.

### Clôture A399 — rapport et Telegram confirmés

- rapport de palier publié :
  `RAPPORT-PALIER-2026-08-29-PHASE5-A395-A399-Q106-CITATION-FENETRE-SOURCE-INVALIDE.md` ;
- Telegram envoyé en une partie via fichier UTF-8 et `-RequireMessage` ;
- notifier : code 0, corps 2 757, total 2 863 caractères ;
- message SHA `28642D68...10D3A8`, mojibake 0, motif secret 0 ;
- reçu `phase5/00-RECU-TELEGRAM-A395-A399.md` ;
- build servers et runtime arrêtés, processus lourds zéro.

A395–A399 est entièrement clos. Le Goal reste actif. Le prochain travail est
le TDD générique du défaut de fenêtre source, sans nouveau replay live avant
preuve rouge, correction et suites déterministes complètes.

### Ouverture A400–A404 — identités citables dans les fenêtres source

- Protocole préenregistré avant tests et patch produit :
  `artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A400-A404-BUG130-FENETRE-SOURCE-IDENTITES-CITABLES.md`.
- Cause de départ : A397 a exposé E3–E6 au writer sous le seul identifiant E3 ;
  les claims de E4 ont donc été publiés avec E3 puis E4 a disparu du payload.
- Architecture retenue : séparer les identifiants citables, les ancres choisies
  par Qwen et les groupes documentaires sélectionnés dont au moins une citation
  doit être réalisée.
- En `named_item`, chaque extrait de la fenêtre garde son propre `EvidenceId` ;
  Qwen choisit lequel soutient ses claims et le code exige seulement la
  couverture mécanique de chaque groupe sélectionné.
- En `content_claim`, seuls les claims atomiques explicitement sélectionnés
  sont citables et restent distincts, même sur une page identique.
- Toute citation d'un membre de fenêtre différent de l'ancre impose une revue
  sémantique finale indépendante ; le code ne juge pas le sens du claim.
- A401 doit produire des rouges causaux avant A402. Aucun live A400–A404.
- Phase 5 et BUG130 restent **TESTÉS NON APPROUVÉS**.

### A400–A403 — contrat de fenêtre citable corrigé et validé

- A400 a comparé quatre options. La fenêtre à identités distinctes a été
  retenue ; fenêtre limitée à l'ancre, citation automatique des voisins et
  preuve composite opaque ont été rejetées.
- La cartographie a découvert une deuxième perte au mapper UI : deux chunks
  cités d'une même page étaient regroupés par `VisibleSourceKey` et réduits au
  premier.
- A401 a prouvé trois rouges avant patch : voisin named_item sans bloc E#, deux
  claims content_claim de même page fusionnés sous un seul E#, et deux chunks
  cités réduits à une source UI.
- A402 introduit un contexte writer partagé : groupes sélectionnés,
  EvidenceIds citables et groupes requis.
- `named_item` expose chaque chunk mécaniquement citable sous son identité ; au
  moins un ID de chaque unité choisie par Qwen doit être cité, sans promotion
  automatique.
- `content_claim` n'expose que les preuves atomiques explicitement
  sélectionnées ; les voisins de page non sélectionnés restent interdits.
- Une citation d'un membre de fenêtre différent de l'ancre empêche désormais
  le saut de revue sémantique et impose une validation Qwen indépendante.
- Le mapper UI déduplique par EvidenceId et conserve donc plusieurs chunks
  réellement cités sur une même page ; la mémoire reste dérivée uniquement de
  `verification.CitedEvidence`.
- Les traces ajoutent `writer_allowed_evidence_ids`,
  `required_evidence_groups` et `context_evidence_beyond_selection`.
- La modularité a été préservée par extraction dans
  `SourceBackedAgentSelectedWriterVerification.cs` et
  `SourceContractVerifierSemanticSelection.cs` ; runner 2 493 lignes,
  vérificateur principal 423 lignes.
- Validations finales : causaux 7/7 ; vérificateur/frontières 20/20 ; agent
  217/217 ; architecture 21/21 ; noyau source-backed 279/279 ; client complet
  **1 494/1 494**, zéro skipped/failed/error/timeout/aborted.
- Audit statique : 11 gates PASS, aucune règle Q106/PTFE/Dyneon/Cuisine/recette
  dans les sources produit modifiées, TRX complet authentifié.
- DLL WinUI adjacente/tests et produit x64 byte-identiques SHA
  `8400175B...B6FE8EA`; DLL tests `91B6D748...98DC77E`; TRX complet
  `8A181F60...1F522DE`.
- Aucun live A400–A404. BUG130 fenêtre/citation devient PASS déterministe, mais
  la route Qwen et l'alignement end-to-end restent à revalider live. Phase 5
  demeure **TESTÉE NON APPROUVÉE**.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A400-A404-BUG130-IDENTITES-CITABLES-FENETRE.md`.

A404 doit envoyer le rapport Telegram, figer le reçu, arrêter les build
servers/runtime et confirmer zéro processus lourd. Le prochain live éventuel
appartiendra à un nouveau palier préenregistré.

### A404 — Telegram confirmé

- Rapport envoyé en une partie via fichier UTF-8 et `-RequireMessage` ;
- notifier : code 0, corps 2 551, total 2 656 caractères ;
- message SHA `759049D5...08FB54E`, mojibake 0, motif secret 0 ;
- reçu : `phase5/00-RECU-TELEGRAM-A400-A404.md` ;
- aucun identifiant de message inventé, l'interface n'en expose pas.

L'arrêt des ressources et les empreintes finales clôturent A404. Le Goal reste
actif et aucun live A400–A404 n'a été exécuté.

### Clôture ressources A404

- `dotnet build-server shutdown` : MSBuild et compilateur arrêtés avec succès ;
- backend/runtime modèle local : aucun processus actif ;
- processus lourds correspondant au chantier après arrêt : 0 ;
- A400–A404 est clos, le Goal reste actif.

### Ouverture A405–A409 — replay live du contrat d'identités citables

- Protocole préenregistré avant gates et live :
  `artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A405-A409-REPLAY-LIVE-UNIQUE-Q106-IDENTITES-CITABLES.md`.
- Sonde A406 créée et qualifiée hors gate : AST 0, code 0, 23 lignes,
  marqueur PASS, script SHA `5D490C5A...3B643D6`, sortie SHA
  `2939C960...9B675B3`.
- La sonde authentifie DLL tests, WinUI adjacente/produit, TRX 1 494/1 494,
  sources A402, méthodes réfléchies, mapper UI et absence de hardcoding.
- A406 impose quatre gates : parité initiale, intake natif exact, backend A320
  et parité finale byte-identique.
- A407 reste absent jusqu'aux quatre PASS puis autorise exactement un Q106,
  sans build, restore, Platform ni retry.
- Le run peut choisir `named_item` ou `content_claim` ; la route et la qualité
  sont jugées séparément. Le critère central est l'alignement de chaque claim
  avec son chunk exact et son transport dans mémoire/payload.
- Si un voisin de l'ancre est cité, la trace doit montrer l'élargissement et
  une revue sémantique finale, jamais le saut après sélection explicite.

### A406 — quatre gates qualifiées, replay unique autorisé

- Gate 1 parité initiale : PASS, 23 lignes, 1 889 octets, SHA
  `2939C960...9B675B3`.
- Gate 2 intake natif exact : 1/1 PASS sans build/restore/Platform ; référence
  exacte de 23 caractères ; TRX SHA `F2D768D1...85F355`.
- Gate 3 backend/contexte : PASS ; Q106 unique, résolution exacte complète en
  111 ms, identité canonique inchangée, pages 1–2 et neuf chunks.
- Runtime réel persisté relu sans modèle : embedded géré, CUDA b10098,
  contexte 4 096, ubatch 128, threads batch 4, flash on ; runtime, modèle et
  banque conformes aux hashes figés.
- Gate 4 dans un `pwsh` frais : PASS. Les 23 valeurs égalent la gate 1. Une
  différence initiale de fichier LF/CRLF issue de la capture a été normalisée
  sans rerun ; les deux fichiers deviennent byte-identiques, SHA
  `2939C960...9B675B3`.
- Aucune source produit/test modifiée entre les gates ; A407 encore absent au
  moment de la décision.
- Rapport de préflight :
  `artifacts/goal-rag-product-20260827-1041/phase5/A406-PREFLIGHT-Q106-IDENTITES-CITABLES-QUATRE-GATES.md`.

Décision : A407 est autorisé exactement une fois selon le protocole figé.
Aucun résultat xUnit ne sera assimilé à un verdict produit avant l'audit A408.

### A407 — replay unique consommé, timeout sans réponse

- A407 exécuté exactement une fois, sans build/restore/Platform/retry.
- TRX 1/1 `Passed`, mais durée réelle `00:05:00.6781403` ; la ligne produit
  enregistre 300 101 ms, `TaskCanceledException`, réponse vide, 0 source et
  `empty_answer,no_sources`.
- Route Qwen toujours `named_item/single_item`.
- Résolution exacte et identité canonique PASS.
- E3-E6 matérialisés comme quatre EvidenceIds distincts.
- Premier writer sans citation : refus mécanique attendu.
- Writer de réparation : E3-E6 tous cités ; vérification PASS ;
  `writer_allowed_evidence_ids=E3,E4,E5,E6` et
  `context_evidence_beyond_selection=true`.
- Revue sémantique indépendante exécutée, aucun skip. Elle rend `revise`.
- À 228 866 ms, récupération de contexte demandée à 3 847/4 096 tokens ;
  aucun nouvel événement avant l'annulation.
- Aucun résultat final, UsedItem ou payload UI n'est produit.

### A408 — cause BUG131 : juge aveuglé par compaction statique

- Le juge reconnaît 850 g/l en E4 mais déclare absents 220 µm, 4000 psi,
  350 % et 375-380 °C.
- L'inspection canonique de la même révision prouve ces valeurs dans E4
  `Typical Properties` et E5 `Processing Information`.
- Le code force `constrainedContext=true` dès que le contexte configuré est
  inférieur ou égal à 4 096, puis limite chaque preuve citée à 72 caractères
  avant de partir d'une représentation complète et d'en mesurer le coût.
- Prompt réel du juge : 1 784 tokens ; sortie : 221. La compaction était donc
  prématurée et a masqué les lignes à auditer.
- Audit read-only : 46 gates PASS, marqueur
  `A407_TIMEOUT_SEMANTIC_REVIEW_CAUSAL_AUDIT=PASS`.
- BUG130 devient PASS live partiel pour identités/transport/vérification/revue
  forcée, mais reste non fermé end-to-end faute de résultat final.
- BUG131 est ouvert : visibilité des preuves citées au juge puis récupération
  coûteuse jusqu'au timeout.
- Rapport :
  `artifacts/goal-rag-product-20260827-1041/phase5/A408-AUDIT-LIVE-Q106-TIMEOUT-REVUE-SEMANTIQUE-TRONQUEE.md`.

Verdict A409 : **phase 5 TESTÉE NON APPROUVÉE**. Aucun rerun A407. Le prochain
palier doit comparer puis tester une stratégie générique « preuves citées
complètes d'abord, comptage exact, compaction graduelle seulement si le budget
l'exige », et conserver une révision aval directe et bornée.

### A409 — rapport et Telegram confirmés

- Rapport de palier :
  `RAPPORT-PALIER-2026-08-29-PHASE5-A405-A409-Q106-TIMEOUT-REVUE-TRONQUEE.md`.
- Telegram envoyé en une partie, corps 2 229, total 2 356 caractères, code 0.
- Message SHA `475535DB...6D981B`, mojibake 0, motif secret 0.
- Reçu `phase5/00-RECU-TELEGRAM-A405-A409.md`.
- Variables live absentes, processus lourds zéro ; aucun rerun A407.

A405-A409 est clos. Le Goal reste actif. La prochaine phase de travail est un
palier TDD BUG131 sans modèle live.

### Ouverture A410-A414 — BUG131, preuves complètes et révision bornée

- Protocole préenregistré avant tests/patch :
  `artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A410-A414-BUG131-PREUVES-COMPLETES-REVUE-ET-REVISION-BORNEE.md`.
- Architecture retenue après comparaison : fournir d'abord au juge les
  extraits complets des preuves citées, compter exactement le prompt, puis ne
  compacter que si le budget réel l'exige.
- Toute extraction lexicale de claims par le code et toute simple hausse de
  constante sont rejetées.
- Complément retenu : une décision `revise` plate, sans preuve rejetée ni
  alternative, renvoie une fois la même sélection au writer ; vérification et
  nouvelle revue restent obligatoires, signature anti-cycle conservée.
- A411 doit produire les rouges avec des documents fictifs. Aucun live
  A410-A414 et aucun budget/timeout/contexte augmenté.

### A411 — deux rouges causaux confirmés

- `SemanticJudge_At4096ReceivesCompleteCitedEvidenceWhenMeasuredPromptFits`
  échoue parce que le suffixe factuel situé après les anciennes bornes 72/180
  n'atteint pas le juge.
- `AdaptiveFastReview_NamedItemWindowRevisionReturnsSameSelectionDirectlyOnce`
  échoue parce que `revise` sans rejet repart vers l'orchestrateur et épuise la
  file scriptée au lieu de solliciter directement le writer.
- Résultat : 0/2, exactement les deux échecs attendus ; TRX SHA
  `84699AED...B48186E`.
- Aucun modèle, backend ou live.

### A412 — correctif evidence-first et révision bornée

- Le prompt de revue est d'abord construit avec les extraits complets de toutes
  les preuves citées, puis compté exactement avec son outil et sa sortie.
- La forme compacte n'est reconstruite qu'après dépassement mesuré ; modes de
  trace `complete_cited_evidence` et `measured_compact_fallback`.
- Une réponse plate named_item peut être renvoyée une seule fois au writer avec
  la même sélection lorsque Qwen rend `revise`, zéro preuve rejetée et zéro
  alternative préférée. Signature anti-cycle, vérification et nouvelle revue
  obligatoires.
- Aucun budget, contexte, timeout, tour ou outil augmenté ; aucun hardcode
  Q106/corpus.
- Causaux initiaux 2/2 PASS, puis garde d'overflow mesuré ajoutée : 3/3 PASS,
  TRX SHA `38C318E3...49675`.

### A413 — validation complète et échecs intermédiaires tracés

- AgentV2 initial : 220/221. L'invariant content_claim était présent mais
  séparé par un retour de ligne dans le prompt complet ; seule l'assertion test
  a été rendue insensible à cette mise en page. Final : 221/221.
- Architecture initiale : 20/21, prompt sémantique à 516 lignes. La construction
  du contexte a été extraite dans un helper de 138 lignes sans relever la
  limite ; prompt 383, runner exactement 2 500. Final : 21/21.
- Noyau source-backed : 283/283.
- Client complet : **1 497/1 497**, zéro échec, skip, erreur, timeout, abandon
  ou non-exécuté ; TRX SHA `A9EB1FDB...C4E593`.
- Audit statique reproductible : 27/27, AST PowerShell 0, marqueur
  `A413_BUG131_STATIC_CONTRACT_AUDIT=PASS`.
- DLL WinUI VSTest et produit x64 byte-identiques, 8 291 328 octets, SHA
  `78A26D45...5175923`.
- `git diff --check` code 0 ; branche `SAAIA_V3.1`, HEAD `5f35881c...a37a` ;
  worktree historique conservé, 553 lignes de statut.

Rapports :

- `phase5/RAPPORT-A411-ROUGES-CAUSAUX-BUG131.md` ;
- `phase5/RAPPORT-A412-CORRECTIF-BUG131-PREUVES-COMPLETES-REVISION-BORNEE.md` ;
- `phase5/RAPPORT-A413-VALIDATION-DETERMINISTE-BUG131.md` ;
- `phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A410-A414-BUG131-DETERMINISTE.md`.

### A414 — verdict avant communication

- BUG131 : **PASS déterministe / TESTÉ_NON_APPROUVÉ live**.
- Aucun live A410-A414 ; le dernier Q106 reste A407, timeout et réponse vide.
- Phase 5 globale active.
- Telegram, reçu et arrêt des ressources doivent clôturer A414 avant
  préenregistrement du replay A415+.

### A414 — Telegram confirmé

- message envoyé en une partie via fichier UTF-8 et `-RequireMessage` ;
- notifier : code 0, corps 2 481, total 2 587 caractères ;
- message SHA `648EDA52...0BF95C`, mojibake 0, motif secret 0 ;
- reçu : `phase5/00-RECU-TELEGRAM-A410-A414.md` ;
- aucun identifiant de message inventé, le notifier n'en expose pas.

L'arrêt des ressources clôture A414. Le Goal et la phase 5 restent actifs.

### Clôture ressources A414

- serveurs MSBuild et VB/C# arrêtés avec succès ;
- runtime modèle, backend, testhost et VSTest : aucun processus actif ;
- variables live du processus : absentes ;
- processus lourd correspondant au chantier après exclusion de la sonde
  elle-même : 0.

A410-A414 est clos. Le prochain travail appartient au palier A415+.

### Ouverture A415-A419 — replay live unique après BUG131

- Protocole préenregistré avant gates et live :
  `artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A415-A419-REPLAY-LIVE-UNIQUE-Q106-APRES-BUG131.md`.
- État figé : client 1 497/1 497 ; DLL WinUI testée/produit SHA
  `78A26D45...5175923` ; TRX complet SHA `A9EB1FDB...C4E593`.
- A416 exige quatre gates : parité BUG131, intake exact, backend/runtime et
  parité finale byte-identique.
- A417 autorise exactement un Q106, sans build, restore, Platform ni retry.
- A418 doit prouver depuis les artefacts le mode de contexte du juge, la
  visibilité des faits cités, la décision Qwen, toute révision bornée, la
  réponse finale, les sources, mémoire, payload et latence.
- Aucun patch entre la première gate et l'audit. Une gate rouge interdit le
  live.

### A416 — quatre gates PASS

- Parité initiale : PASS ; client 1 497/1 497, DLL et sources BUG131 figées,
  quatre méthodes réfléchies, dossier A417 absent.
- Intake natif exact : 1/1 PASS sans build/restore/Platform ; référence exacte
  de 23 caractères ; TRX SHA `9F080271...5A066`.
- Backend/runtime : PASS ; résolution exacte complète en 126 ms, même docId,
  révision, hash, pages 1-2 et neuf chunks ; banque, Qwen Q5, runtime CUDA et
  réglages 4 096/128 conformes.
- Parité finale fraîche : PASS, capture byte-identique à la gate 1, 2 044
  octets, SHA `B0CAE540...39A74`.
- Rapport : `phase5/A416-PREFLIGHT-Q106-APRES-BUG131-QUATRE-GATES.md`.

A417 est autorisé exactement une fois selon le protocole figé. Aucun xUnit vert
ne sera assimilé à une réponse approuvée avant l'audit A418.

### A417 — replay unique consommé, timeout sans réponse finale

- Une seule commande, Q106 seule, sans build/restore/Platform/retry.
- TRX 1/1 PASS mais durée réelle `00:05:00.3431398` ; produit 300 082 ms,
  `TaskCanceledException`, 0 caractère, 0 source, 44 traces.
- Résolution exacte et neuf EvidenceIds E3-E11 : PASS.
- Premier writer à 114 065 ms : réponse courte, citation E3, vérification PASS.
- Revue à 166 985 ms : `complete_cited_evidence`, entrée exacte 2 919,
  356 caractères cités, aucune récupération de contexte, décision `revise`,
  rejet E3 et préférence E4.
- Writer direct E4 à 280 018 ms : 1 686 caractères, 698 tokens, marqueur
  `UNITE_SELECTIONNEE 1`, zéro citation.
- Vérificateur : refus attendu `missing_citations` et groupe non réalisé.
- Timeout 20 059 ms plus tard ; aucun résultat final, mémoire ou payload.
- Artefacts : TRX SHA `C3B16E64...F01BDE`, JSONL SHA
  `84652A66...7C075D`. Aucun rerun.

### A418 — audit causal : BUG131 visibilité PASS, BUG132 ouvert

- Le chunk E3 canonique fait exactement 356 caractères, comme
  `cited_evidence_characters=356`. Il contient toutes les propriétés du premier
  draft. L'ancienne coupure à 72 n'agit plus.
- `2919 + 640 + 64 = 3623 <= 4096` : le mode complet est cohérent et aucune
  récupération n'est demandée. Frontière compaction BUG131 : **PASS live**.
- Le chemin même sélection BUG131 est inéligible et non observé, car Qwen
  rejette E3 et préfère explicitement E4.
- La décision Qwen reste non approuvée : le routeur `single_item/named_item`
  applique des critères d'« instances distinctes » à une demande ouverte de
  contenu et rejette des propriétés directement soutenues par E3. BUG130 route
  reste ouvert.
- BUG132 ouvert : le writer alternatif riche fuit un marqueur interne, perd
  toutes les citations et consomme environ 113 s ; le vérificateur protège la
  sortie mais le temps restant ne permet pas la réparation.
- Audit read-only : 68 gates PASS, script SHA `BAC0A9F3...1E28F6`, sortie SHA
  `CA57CA85...99B8D5`, marqueur
  `A417_BUG131_LIVE_CAUSAL_AUDIT=PASS gates=68`.
- Rapports :
  `phase5/A418-AUDIT-LIVE-Q106-APRES-BUG131-TIMEOUT-WRITER-SANS-CITATIONS.md`
  et
  `phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A415-A419-BUG131-VISIBILITE-PASS-BUG132-OUVERT.md`.

Verdict A419 avant Telegram : BUG131 compaction PASS live ; révision même
sélection NON OBSERVÉE ; end-to-end TESTÉ_NON_APPROUVÉ ; BUG130 FAIL persistant ;
BUG132 FAIL ouvert ; phase 5 et Goal actifs.

### Clôture A419 — rapport et Telegram confirmés

- Telegram envoyé en une partie via fichier UTF-8 et `-RequireMessage` ;
- notifier code 0, corps 2 588, total 2 694 caractères ;
- message : 19 lignes, 2 648 octets, SHA
  `238424C9...B01EC21`, mojibake 0, motif de secret 0 ;
- reçu : `phase5/00-RECU-TELEGRAM-A415-A419.md` ;
- aucun identifiant de message inventé ; aucun rerun A417.

A415-A419 est clos comme palier de mesure. BUG131 compaction est démontré live,
mais BUG131 end-to-end demeure `TESTÉ_NON_APPROUVÉ`, BUG130 et BUG132 restent
ouverts, et la phase 5 comme le Goal restent actifs. Après arrêt vérifié des
ressources, le prochain palier doit comparer les architectures génériques et
préenregistrer ses rouges avant toute modification produit.

### Clôture ressources A419

- serveurs MSBuild et VB/C# arrêtés avec succès ;
- processus lourds du chantier après arrêt : 0 ;
- variables live dans le processus courant : 0.

Le poste est donc libéré avant l'ouverture du palier TDD suivant.

### Ouverture A420-A424 — BUG130 unité de réponse et BUG132 writer structuré

Protocole préenregistré avant tests et patch :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A420-A424-BUG130-UNITE-REPONSE-ET-BUG132-WRITER-CLAIMS-STRUCTURES.md`.

Décision architecturale provisoire soumise aux rouges :

- Qwen conserve la décision sémantique, mais le contrat non-grid nomme
  explicitement `answerUnitType/answerUnitMode` pour séparer l'objet cible de
  l'unité réalisée dans la réponse ; les anciennes clés restent seulement
  lisibles pour compatibilité ;
- le writer structuré fait rédiger à Qwen chaque claim et lui fait affecter les
  EvidenceIds visibles ; le code valide et rend ces choix en citations locales,
  puis conserve le vérificateur et la revue sémantique existants ;
- aucun mapping par `questionFocus`, mot-clé, document, langue ou corpus ;
- aucun live, budget, contexte, timeout ou tour supplémentaire dans ce palier.

A421 doit produire des rouges causaux fictifs routeur et writer. A422 ne peut
commencer qu'après conservation des TRX rouges. A423 exige routeur, AgentV2,
architecture, noyau et client complet. A424 publie rapport et Telegram ; les
deux bugs resteront `TESTÉS_NON_APPROUVÉS live` jusqu'au palier live séparé.

### A421 — rouges causaux validés

- premier essai non compté : assertion test sur la mauvaise propriété,
  compilation rouge sans TRX, corrigée test-only ;
- essai valide : 7/7 FAIL, 0 erreur/timeout/abandon ;
- routeur : schéma encore sur les anciennes clés et choix explicite
  `answerUnitMode=content_claim` perdu au profit de `named_item` ;
- writer : méthode structurée absente sur cinq cas de rendu/sûreté ;
- TRX 27 700 octets, SHA `D6C1A247...7C05514` ;
- rapport : `phase5/RAPPORT-A421-ROUGES-CAUSAUX-BUG130-BUG132.md`.

A422 est autorisé dans le seul périmètre du protocole. Aucun live n'est
autorisé.

### A422 — correction générique conservée

- Routeur non-grid : `answerUnitType/answerUnitMode` séparent désormais la
  cible documentaire de l'unité réellement attendue dans la réponse ; Qwen
  choisit, le parseur transporte, les anciennes clés restent replay-only.
- Writer `source_backed_flat_writer_v1` : Qwen rédige chaque claim et choisit
  ses EvidenceIds visibles ; le code valide JSON/IDs/bornes/marqueurs/citations
  brutes puis rend uniquement ces affectations avant le vérificateur normal.
- Flag produit activé par défaut, compatibilité explicite conservée quand la
  capacité structurée ou le flag manque.
- Aucun mapping sémantique mécanique, règle corpus, budget, timeout, contexte,
  tour, backend, retrieval, corpus, mémoire ou UI modifié.

Falsifications et corrections observées :

- 8/9 au premier filtre avec intégration : le writer initial contournait encore
  le structuré ; raccordement de tous les writers sélectionnés, puis 1/1 ;
- 68/69 routeur : assertion littérale test-only mise à jour ;
- le nouveau test de protocole invalide a révélé une seconde génération après
  refus ; le motif atteint désormais le vérificateur et le run s'arrête non
  vérifié sans retry/fallback ; test isolé 1/1 ;
- une substitution test trop large a été annulée ; reconstruction sans le
  nouveau bloc identique au SHA antérieur `4FE7CC3E...6E21D2`.

Rapport :
`phase5/RAPPORT-A422-IMPLEMENTATION-BUG130-UNITE-REPONSE-BUG132-WRITER-STRUCTURE.md`.

### A423 — validation déterministe finale

- filtre causal BUG130/BUG132 : **10/10** ; TRX SHA
  `9005B586...B828A4` ;
- routeur natif : **69/69** ; AgentV2 : **229/229** ;
- architecture : **21/21** ; noyau : **291/291** ;
- client complet : **1 506/1 506**, zéro échec, erreur, timeout, abandon,
  skip ou non-exécuté ; TRX SHA `F6438F4F...B97B1E` ;
- audit statique reproductible : **47/47 PASS**, marqueur
  `A423_BUG130_BUG132_STATIC_CONTRACT_AUDIT=PASS gates=47` ;
- DLL WinUI VSTest et produit byte-identiques, 8 310 272 octets, SHA
  `5041D0D2...792E5A` ;
- writer 410 lignes, runner exactement 2 500 ;
- `git diff --check` code 0, whitespace ciblé 0 ; branche `SAAIA_V3.1`, HEAD
  `5f35881c...a37a`, worktree historique 554 lignes préservé ;
- build servers arrêtés, processus ciblés 0, variables live 0.

Rapport :
`phase5/RAPPORT-A423-VALIDATION-DETERMINISTE-BUG130-BUG132.md`.

### A424 — verdict avant Telegram

- BUG130 : **PASS déterministe / TESTÉ_NON_APPROUVÉ live** ;
- BUG132 : **PASS déterministe / TESTÉ_NON_APPROUVÉ live** ;
- aucun live A420-A424 ; réponse Q106 end-to-end toujours non approuvée ;
- phase 5 et Goal actifs.

Rapport de palier :
`phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A420-A424-BUG130-BUG132-DETERMINISTE.md`.

A424 doit maintenant envoyer Telegram, figer le reçu et confirmer à nouveau
l'arrêt des ressources. Le prochain live ne peut appartenir qu'à un nouveau
palier préenregistré.

### Clôture A424 — Telegram confirmé

- message envoyé en une partie via UTF-8 et `-RequireMessage` ;
- notifier : code 0, corps 2 776, total 2 882 caractères ;
- message : 29 lignes, 2 870 octets, SHA
  `FE04C550...F5175EB`, mojibake 0, motif de secret 0 ;
- reçu : `phase5/00-RECU-TELEGRAM-A420-A424.md` ;
- aucun identifiant inventé, le notifier n'en expose pas.

A420-A424 est clos. BUG130 et BUG132 sont déterministiquement verts mais non
approuvés live ; la phase 5 et le Goal restent actifs. Le prochain travail est
le préenregistrement d'un palier live distinct sur état figé.

### Ouverture A425-A429 — replay live unique après BUG130/BUG132

Protocole préenregistré avant requalification, gates et modèle :
`phase5/PROTOCOLE-A425-A429-REPLAY-LIVE-UNIQUE-Q106-APRES-BUG130-BUG132.md`.

- A425 requalifie le chemin `bin/Debug` réellement utilisé par le test live :
  client complet 1 506/1 506, stockage TRX unique et parité DLL produit ;
- le build n'est autorisé que pendant A425, avant gel ;
- A426 exige quatre gates : parité BUG130/BUG132, intake exact, backend/runtime
  et parité finale byte-identique ;
- A427 autorise exactement un Q106, sans build, restore, Platform ni retry ;
- A428 audite route Qwen, `content_claim`, writer structuré, claims/IDs,
  vérificateur, réponse, sources, mémoire, payload et latence ;
- A429 rapporte, notifie Telegram et arrête les ressources.

Le dossier A427 doit rester absent avant les quatre PASS. Toute gate rouge
interdit le live. Aucun xUnit vert n'est assimilé à une réponse approuvée.

### A425 — chemin canonique live requalifié

- client complet Debug sans `Platform` : **1 506/1 506** ;
- stockage TRX unique : DLL tests `bin/Debug` ;
- TRX 2 473 603 octets, SHA `BEAF0E66...68A5FB` ;
- DLL tests SHA `A150D9E3...EDF128` ;
- WinUI adjacente/produit byte-identiques, SHA `1E79D3B4...FDBDCB` ;
- Contracts SHA `86E911D3...EBAAC` ;
- dossier A427 absent.

Rapport : `phase5/A425-REQUALIFICATION-BINAIRE-CANONIQUE-LIVE.md`.

Le binaire et les sources sont figés. Aucun build, restore ou patch
produit/test jusqu'à l'audit A428. A426 peut exécuter les quatre gates.

### A426 — quatre gates PASS

- parité initiale : 1 506/1 506, DLL `bin/Debug` réellement chargée, contrats
  BUG130/BUG132 et réflexions conformes, dossier A427 absent ;
- intake natif exact : 1/1, référence de 23 caractères, TRX SHA
  `9B26EBFA...120A0` ;
- backend/corpus : Q106 unique, `/ready`, résolution exacte complète en 113 ms,
  même docId/révision/hash, pages 1-2, neuf chunks ;
- runtime : banque, Qwen Q5, llama CUDA et réglages 4 096/128 conformes, modèle
  non démarré ;
- parité finale : identique à la gate 1 après CRLF, SHA normalisé
  `5A5E20A1...8E7CB` ;
- processus ciblés 0, variables live 0.

Rapport : `phase5/A426-PREFLIGHT-Q106-APRES-BUG130-BUG132-QUATRE-GATES.md`.

A427 est autorisé exactement une fois selon le protocole figé. Aucun xUnit vert
ne vaudra approbation avant l'audit A428.

### A427-A428 — non-exécution live, erreur de chemin banque

- A427 consommé une fois ; aucune relance ;
- mauvais chemin wrapper sous le dossier Goal ; chemin réel sous
  `artifacts/validation` ;
- variable banque vide, test refusé en 124,6662 ms avant modèle ;
- TRX 0/1, 5 100 octets, SHA `A45584E9...FC60DE` ;
- output vide, aucun JSON/JSONL/TSV, aucun llama-server ;
- processus et variables live 0, binaires figés inchangés ;
- le nettoyage final avait masqué le code rouge externe : le prochain wrapper
  devra être fail-fast et conserver explicitement le code `dotnet test`.

Rapports :

- `phase5/A428-AUDIT-NON-EXECUTION-A427-CHEMIN-BANQUE.md` ;
- `phase5/RAPPORT-PALIER-2026-08-29-PHASE5-A425-A429-AUCUN-LIVE-CHEMIN-BANQUE.md`.

BUG130/BUG132 restent `PASS déterministe / TESTÉ_NON_APPROUVÉ live`. A429 doit
notifier Telegram puis fermer ce palier. Le Goal reste actif.

### Clôture A429 — Telegram confirmé

- notifier code 0, corps 1 663, total 1 772 caractères ;
- message 11 lignes, 1 718 octets, SHA `214EE533...8F4420` ;
- mojibake 0, motif de secret 0 ;
- reçu `phase5/00-RECU-TELEGRAM-A425-A429.md` ;
- aucun identifiant inventé.

A425-A429 est clos sans inférence. Le prochain palier doit être préenregistré
et qualifier un wrapper fail-fast avant toute nouvelle exécution unique.

### Ouverture A430-A434 — wrapper fail-fast puis Q106 unique

Protocole :
`phase5/PROTOCOLE-A430-A434-REPLAY-LIVE-UNIQUE-Q106-WRAPPER-FAIL-FAST.md`.

- A430 crée un wrapper avec résolution/validation du chemin banque avant toute
  réservation, mode `-ArmOnly`, état parent propre et code natif conservé ;
- A431 exige quatre gates fraîches ;
- A432 autorise une seule invocation sous un nouvel identifiant ;
- A433 audite sans relance ; A434 rapporte, notifie et libère les ressources ;
- aucun build, restore, patch produit/test ou réglage modèle.

### A430 — wrapper fail-fast qualifié

- AST 0, 124 lignes, SHA `2275F5A1...B77871` ;
- neuf invariants statiques PASS ;
- `-ArmOnly` PASS : banque absolue correcte et hashée, DLL/sources conformes,
  processus 0, variables live 0, dossier A432 absent ;
- aucune création de dossier ni invocation dotnet.

Rapport : `phase5/A430-QUALIFICATION-WRAPPER-A432-FAIL-FAST.md`. A431 continue
avec les gates fraîches, modèle toujours arrêté.

### A431 — quatre gates PASS

- wrapper armé à blanc PASS ;
- intake exact 1/1, TRX SHA `B1A46611...E4B8C4` ;
- backend exact en 119 ms, même identité/pages/neuf chunks ;
- banque, Qwen, llama et réglages conformes, modèle arrêté ;
- second armement identique au premier, SHA `D43811DA...961DEF` ;
- dossier A432 absent, aucune mutation produit/test/binaire.

Rapport : `phase5/A431-PREFLIGHT-A432-QUATRE-GATES.md`. A432 est autorisé une
fois via le wrapper fail-fast.

### A432 — replay live unique Q106 : PASS fonctionnel

- une seule invocation, aucun retry, build, restore ou `Platform` ;
- TRX 1/1 PASS, durée `00:04:50.4963922`, SHA `B6CD3FEB...CCC708` ;
- JSON/JSONL/TSV uniques, hashes figés ;
- réponse 274 caractères, une source E12, erreur/drapeau vides ;
- plan live `content_claim`, sélection `single_item` ;
- writer structuré réellement exécuté deux fois en révision ;
- Qwen rejette E11, choisit E12, puis accepte après lecture des 784 caractères
  complets de la preuve ;
- payload et mémoire conservent docId, chemin, révision, hash, chunk et page ;
- temps produit 290 155 ms, pipeline accepté à 273 408 ms ;
- performance document nommé 45 s : **FAIL**.

### A433 — audit causal read-only : 112/112 PASS

Script `phase5/a433-audit-a432-q106-live-readonly.ps1` : AST 0, 246 lignes,
16 418 octets, SHA `4A56EC92...A68759`. Il vérifie artefacts, TRX, réponse,
route, outils, writer, revues, vérificateur, source, mémoire, preuve canonique,
parité binaire/source et état des ressources.

Rapport :
`phase5/A433-AUDIT-CAUSAL-A432-Q106-BUG130-BUG131-BUG132-LIVE.md`.

Verdicts :

- BUG130 : **PASS déterministe + PASS live observé** ;
- BUG131 : **PASS déterministe + PASS live observé** ;
- BUG132 : **PASS déterministe + PASS live observé** ;
- identité, exactitude, citation, mémoire et payload : **PASS** ;
- rendu/clic WinUI : **TESTÉ_NON_APPROUVÉ** ;
- performance : **FAIL** ;
- qualification probabiliste : **1/3** ;
- phase 5 et Goal : **actifs**.

### A434 — clôture documentaire et Telegram

Rapport complet :
`phase5/RAPPORT-PALIER-2026-08-30-PHASE5-A430-A434-Q106-PASS-LIVE-PERFORMANCE-FAIL.md`.

Telegram rend visibles le premier PASS live combiné, le FAIL 290 s, la limite
1/3, l'absence de validation WinUI et la prochaine analyse du coût des appels.
Envoi confirmé : code 0, corps 2 009, total 2 134 caractères, message SHA
`47D3DB77...F8AB9BF`, mojibake 0, motif de secret 0. Reçu :
`phase5/00-RECU-TELEGRAM-A430-A434.md`.

A430-A434 est clos. La phase 5 et le Goal restent actifs.

Arrêt ressources A434 : `dotnet build-server shutdown` confirme l'arrêt des
serveurs MSBuild et VB/C# ; contrôle final processus ciblés 0, variables live 0.

Le palier suivant ne relance pas immédiatement Q106. Il commence par une
décomposition reproductible de la latence existante, conserve E12 comme oracle
qualitatif et interdit toute hausse aveugle de budgets, contexte ou timeout.

### Ouverture A435-A439 — réduction générique de latence, sans live

Protocole préenregistré avant diagnostic approfondi et patch :
`phase5/PROTOCOLE-A435-A439-REDUCTION-GENERIQUE-LATENCE-Q106-SANS-LIVE.md`.

- A435 attribue les 290 155 ms aux appels/tours existants ;
- A436 écrit un contrat TDD rouge pour supprimer une redondance générique ;
- A437 implémente la variante minimale sans modifier les budgets ;
- A438 valide et compare déterministiquement le nombre d'appels/tours ;
- A439 rapporte, notifie et décide seulement si un futur live 2/3 est autorisé.

Qwen reste décideur sémantique, E12 complète reste l'oracle qualitatif et aucun
modèle/test live n'est autorisé pendant ce palier.

### A435 — goulot attribué : 11 appels Qwen, 99,416 % du tour

Audit read-only 81/81 PASS. Script
`phase5/a435-audit-latence-a432-readonly.ps1`, SHA `A812A94D...9BA6DCB`.

- 11 appels Qwen : 271 819 ms sur 273 415 ms ;
- 19 199 tokens de prompt, 1 160 de sortie ;
- routeur : 29 199 ms ;
- décisions retrieval : 60 252 ms, avec `content_cards` sans rendement ;
- audit initial réel : 25 432 ms en deux appels ;
- trois revues sémantiques : 110 021 ms ;
- deux writers structurés : 46 915 ms ;
- outils documentaires : 302 ms ;
- chaîne E11 avant writer E12 : 82 495 ms ;
- plancher empirique route détaillée + writer E12 + revue finale : 97 025 ms.

Rapport :
`phase5/A435-RAPPORT-ATTRIBUTION-LATENCE-A432-11-APPELS-QWEN.md`.

Décision : ne pas présenter un micro-patch de boucle comme solution 45 s. A436
compare trois architectures : optimisation locale, transaction sémantique
unifiée en un appel post-retrieval, et accélération runtime/modèle. Les tests
rouges ne commencent qu'après sélection d'une variante générique compatible
avec l'ordre de grandeur visé et l'autorité sémantique de Qwen.

### A436 — architecture V2 sélectionnée et RED figé

Rapport :
`phase5/A436-DECISION-ARCHITECTURE-TRANSACTION-SEMANTIQUE-UNIFIEE.md`.

- V1 locale rejetée comme solution 45 s : plancher empirique > 45 s ;
- V3 runtime différée faute de mesure matérielle autorisée ;
- V2 transaction sémantique unifiée sélectionnée comme expérience réversible ;
- Qwen conserve en un contrat final la décision, les claims et les EvidenceIds ;
- code limité au pool, schéma, rendu et vérification mécaniques ;
- flag préenregistré faux par défaut ;
- RED reproductible : exit 1, cinq `CS1061/CS0117` sur l'option absente ;
- sortie figée dans `phase5/A436-SORTIE-RED-TRANSACTION-SEMANTIQUE.txt`.

### A437 — transaction implémentée derrière flag inactif

Rapport :
`phase5/A437-RAPPORT-IMPLEMENTATION-TRANSACTION-SEMANTIQUE-UNIFIEE.md`.

- nouveau contrat `source_backed_semantic_answer_transaction_v1` ;
- décisions Qwen : answer, clarify, context ou research ;
- Qwen choisit lead, claims et IDs dans le pool canonique borné ;
- code refuse E99, citations, marqueurs, formes invalides et capacité absente ;
- échec fermé : contenu vide, brut diagnostique, aucun retry/fallback ;
- aucun budget augmenté ; température 0, sortie au plus 640 tokens ;
- flag `SAAIA_SOURCE_BACKED_AGENT_V2_SEMANTIC_ANSWER_TRANSACTION=false` ;
- runner 2 500 lignes, audit rapide 500, composant 447.

### A438 — validation déterministe complète

Rapport :
`phase5/A438-RAPPORT-VALIDATION-DETERMINISTE-TRANSACTION-SEMANTIQUE.md`.

- transaction : **7/7** ; voisins : **38/38** ;
- AgentV2 : **236/236** ; routeur : **105/105** ;
- architecture : **21/21** ; noyau : **371/371** ;
- client complet : **1 513/1 513** ;
- audit statique : **66/66 PASS** ;
- DLL WinUI byte-identiques, SHA `EC9B9211...D96D4` ;
- comparaison même scénario : ancien **2 appels**, V2 **1 appel** ;
- réduction déterministe 50 %, mais aucun live et aucune prédiction 45 s ;
- branche/HEAD inchangés, worktree historique préservé ;
- build servers arrêtés, processus ciblés 0, variables live 0 ;
- Ollama utilisateur inspecté puis laissé intact.

### A439 — verdict et prochaine autorisation

Rapport de palier :
`phase5/RAPPORT-PALIER-2026-08-30-PHASE5-A435-A439-TRANSACTION-SEMANTIQUE-DETERMINISTE.md`.

- architecture/sûreté/régressions : **PASS déterministe** ;
- qualité Qwen réelle avec V2 : **TESTÉ_NON_APPROUVÉ** ;
- performance : **FAIL historique 290 s / NON REMESURÉE** ;
- WinUI : **TESTÉ_NON_APPROUVÉ** ; qualification Q106 : **1/3** ;
- flag reste faux ; phase 5 et Goal restent actifs ;
- Q106 2/3 n'est pas encore autorisé comme qualification produit ;
- prochain palier : canari modèle multi-domaines, figé et comparatif, avant
  toute reprise de la qualification Q106.

### Clôture A439 — Telegram confirmé

- notifier code 0, corps 2 259, total 2 397 caractères ;
- message 17 lignes, 2 323 octets, SHA `F352F623...FD09E` ;
- mojibake 0, motif de secret 0 ;
- reçu `phase5/00-RECU-TELEGRAM-A435-A439.md` ;
- aucun identifiant de message inventé.

A435-A439 est clos. La phase 5 et le Goal restent actifs. Le prochain travail
est le protocole distinct du canari multi-domaines ; aucun modèle ne peut être
lancé avant ses gates.

### Ouverture A440-A444 — canari Qwen multi-domaines

Protocole préenregistré avant harnais, gates et modèle :
`phase5/PROTOCOLE-A440-A444-CANARI-QWEN-TRANSACTION-SEMANTIQUE-MULTI-DOMAINES.md`.

- cinq EvidenceBundles synthétiques figés : sécurité industrielle, cuisine,
  politique administrative, type documentaire et preuve insuffisante ;
- exactement une transaction et un appel Qwen par cas ;
- aucun backend, corpus réel ou Q106 ;
- oracles exacts sur décision, claims, EvidenceIds et faits ;
- tokens et latence exportés ; cinq appels maximum, aucun retry ;
- modèle interdit avant quatre gates A442 ;
- Q106 2/3 reste interdit pendant ce palier.

A440 est complet. A441 doit maintenant écrire le harnais opt-in et le wrapper
fail-fast, puis les qualifier sans lancer Qwen.

### A441 — harnais canari qualifié sans modèle

Rapport :
`phase5/A441-QUALIFICATION-HARNAIS-CANARI-TRANSACTION-SEMANTIQUE.md`.

- test live opt-in dédié, cinq cas figés, une transaction structurée par cas ;
- exécution désarmée : **1/1**, aucune sortie live ;
- audit du harnais et du wrapper : **56/56 PASS** ;
- wrapper `-ArmOnly` : PASS, zéro `dotnet`, zéro dossier créé ;
- fallback non structuré interdit ; runtime arrêté dans `finally` ;
- DLL tests figée SHA `575FF965...36B51` ;
- DLL WinUI byte-identiques SHA `EC9B9211...D96D4` ;
- processus ciblés 0, variables live 0 ; aucun modèle lancé.

A441 est complet. A442 doit maintenant franchir les quatre gates fraîches
prévues par le protocole. Le modèle reste interdit jusqu'au PASS des quatre ;
l'invocation canari sera unique, sans retry ni correction d'oracle.

### A442 préflight — quatre gates vertes

Rapport :
`phase5/A442-PREFLIGHT-QUATRE-GATES-CANARI-TRANSACTION-SEMANTIQUE.md`.

1. harnais/oracles : **PASS 56/56**, armement à blanc PASS ;
2. binaire : **PASS 1 514/1 514**, DLL gelée et parité WinUI confirmées ;
3. runtime/modèle : **PASS read-only**, profil et hashes conformes, serveur 0 ;
4. armement final : **PASS**, hashes inchangés, sortie absente, état propre.

Le premier essai de commande de Gate 2 a seulement visé une sortie Release
absente et n'a exécuté aucun test ; la gate est fondée sur le TRX VSTest du
DLL Debug gelé. Les quatre portes effectives sont vertes. Une et une seule
invocation live A442 est désormais autorisée par le protocole.

### A442 — invocation unique exécutée, canari rouge

- une seule exécution ; aucun retry ou relancement partiel ;
- cinq cas exécutés, cinq appels structurés, un par cas ;
- résultat xUnit : **0/1**, artefact fonctionnel : **0/5** ;
- erreur commune : `semantic_answer_transaction_decision_contract_invalid` ;
- aucune réponse ni EvidenceId accepté après rejet ;
- runtime arrêté, processus ciblés 0, variables live 0.

### A443 — cause auditée en lecture seule

Rapport :
`phase5/A443-RAPPORT-AUDIT-READONLY-CANARI-A442-TRANSACTION-SEMANTIQUE.md`.

- audit des artefacts : **32/32 PASS** ;
- JSON bruts syntaxiquement valides : **5/5** ;
- faits et EvidenceIds bruts attendus : **5/5** ;
- décisions strictes brutes : **4/5** ; le cas insuffisant choisit `clarify` ;
- protocole post-validateur : **0/5**, donc verdict officiel FAIL ;
- cause : schéma autorise un lead du pool, parseur exige `NONE` pour
  `answer`/`clarify`, contrainte absente du prompt ;
- fail-closed : PASS, aucune sortie utilisateur publiée ;
- médiane 20 382 ms, max/p95 descriptif 26 014 ms, cinq cas sous 45 s ;
- export tokens JSON incomplet ; métriques retrouvées dans le log serveur ;
- Q106 2/3 reste interdit.

A443 est complet. A444 doit figer le verdict du palier et notifier Telegram,
sans relancer A442. Le prochain palier devra corriger le contrat mécanique et
l'observabilité avant toute nouvelle qualification live.

### A444 — verdict de palier

Rapport :
`phase5/RAPPORT-PALIER-2026-08-30-PHASE5-A440-A444-CANARI-QWEN-TRANSACTION-SEMANTIQUE.md`.

- verdict officiel : **TESTÉ_NON_APPROUVÉ, FAIL 0/5** ;
- architecture unifiée prometteuse, mais contrat live non approuvé ;
- flag produit reste faux ; WinUI non testée ; Q106 reste 1/3 ;
- performance isolée 5/5 sous 45 s, non extrapolée au trajet complet ;
- canari unique figé, aucune relance dans ce palier ;
- prochain palier : RED causal lead, alignement schéma/prompt/parseur, export
  tokens, validations déterministes ;
- ressources arrêtées et worktree historique préservé.

A440-A444 est techniquement clos. Le Telegram de palier doit encore être envoyé
et son reçu figé avant l'ouverture du palier correctif suivant.

### Clôture A444 — Telegram confirmé

- notifier code 0, corps 1 954, total 2 087 caractères ;
- message 19 lignes, 2 008 octets, SHA `78DFDDD6...B604592` ;
- mojibake 0, motif de secret 0 ;
- reçu `phase5/00-RECU-TELEGRAM-A440-A444.md` ;
- aucun identifiant de message inventé.

A440-A444 est clos. La phase 5 et le Goal restent actifs. Le prochain palier
correctif commence par des tests rouges causaux et reste sans modèle jusqu'à
validation déterministe complète.

### Ouverture A445-A449 — alignement lead et métriques, sans modèle

Protocole préenregistré :
`phase5/PROTOCOLE-A445-A449-ALIGNEMENT-LEAD-ET-METRIQUES-STRUCTUREES-SANS-MODELE.md`.

- lead `answer` : NONE ou ID du pool également cité par un claim ;
- lead `clarify`/`research` : NONE ou ID du pool ;
- lead `context` : ID du pool obligatoire ;
- prompt générique distinguant ambiguïté, approfondissement et preuve absente ;
- API structurée conservant usage et timings au lieu de retourner seulement le
  texte ;
- futur artefact canari avec tokens réels ;
- aucun modèle, backend, Q106 ou WinUI dans ce palier ;
- A442 reste figé rouge et ne sera pas réécrit.

A445 doit maintenant produire les tests rouges causaux avant tout patch.

### A445 — RED causal reproductible

Rapport :
`phase5/A445-RAPPORT-RED-ALIGNEMENT-LEAD-ET-METRIQUES-STRUCTUREES.md`.

- **5/5 tests rouges**, chaque échec correspond au contrat préenregistré ;
- leads answer/clarify du pool actuellement refusés ;
- lead answer non cité reçoit une erreur trop générique ;
- distinction clarify/context/research absente du prompt ;
- API de complétion structurée avec usage/timings absente ;
- TRX SHA `487CF258...15DF5C` ;
- code produit transaction et OpenAI encore inchangé au moment du RED ;
- aucun modèle, backend, Q106 ou WinUI.

A445 est complet. A446 peut appliquer le patch minimal sur les seuls chemins
autorisés par le protocole.

### A446 — contrat et métriques implémentés

Rapport :
`phase5/A446-RAPPORT-IMPLEMENTATION-ALIGNEMENT-LEAD-ET-METRIQUES.md`.

- answer accepte NONE ou lead du pool cité par un claim ;
- clarify/research acceptent NONE ou lead du pool ; context exige un lead ;
- lead answer non cité échoue fermé avec une erreur dédiée ;
- prompt distingue ambiguïté, contexte visible et information manquante ;
- nouvelle complétion structurée préserve usage et timings ;
- adaptateur produit et futur harnais propagent ces métriques ;
- mêmes tests causaux : **5/5 GREEN** après **0/5 RED** ;
- aucun modèle, backend, Q106 ou WinUI ; flag toujours faux.

A446 est complet. A447 doit maintenant exécuter les validations déterministes
du plus causal au client complet, puis les audits statiques et l'arrêt des
ressources.

### A447 — validation déterministe complète

Rapport :
`phase5/A447-RAPPORT-VALIDATION-DETERMINISTE-ALIGNEMENT-LEAD-METRIQUES.md`.

- RED 0/5 figé, GREEN causal **5/5** ;
- transaction **12/12**, OpenAI **19/19**, AgentV2 **240/240** ;
- routeur **105/105**, architecture **21/21** ;
- sélection source-backed **393/393**, client complet **1 519/1 519** ;
- audit statique **78/78 PASS** ;
- A442 JSON/TRX/log immuables ;
- parité DLL WinUI SHA `9E0641F9...E5130` ;
- build servers arrêtés, processus ciblés 0, variables live 0.

### A448 — décision d'autorisation

Le correctif satisfait toutes les portes déterministes du protocole A445 :
contrat aligné, échec fermé conservé, métriques exportables, flag faux,
régressions et audit verts, ressources arrêtées. Un **nouveau canari distinct**
peut donc être préenregistré après clôture A449.

Cette autorisation ne concerne pas A442, qui reste rouge et immuable, ni Q106
2/3, qui reste interdit tant que le nouveau canari n'a pas prouvé protocole,
qualité et métriques live.

### A449 — rapport de palier prêt pour notification

Rapport :
`phase5/RAPPORT-PALIER-2026-08-30-PHASE5-A445-A449-ALIGNEMENT-LEAD-METRIQUES.md`.

- verdict : PASS déterministe, live non remesuré ;
- A442 reste officiellement FAIL 0/5 et byte-identique ;
- nouveau canari distinct autorisable, Q106 2/3 toujours interdit ;
- ressources arrêtées et worktree historique préservé.

Le Telegram A445-A449 doit être envoyé et son reçu figé avant l'ouverture du
nouveau protocole live.

### Clôture A449 — Telegram confirmé

- notifier code 0, corps 1 615, total 1 748 caractères ;
- message 15 lignes, 1 662 octets, SHA `351FF140...DB615B` ;
- mojibake 0, motif de secret 0 ;
- reçu `phase5/00-RECU-TELEGRAM-A445-A449.md` ;
- aucun identifiant de message inventé.

A445-A449 est clos. La phase 5 et le Goal restent actifs. Le prochain palier
préenregistrera un nouveau canari multi-domaines distinct avant tout modèle.

### Ouverture A450-A454 — second canari Qwen distinct

Protocole :
`phase5/PROTOCOLE-A450-A454-SECOND-CANARI-QWEN-DISTINCT.md`.

- cinq nouveaux cas : métrologie, logistique contrainte, RH, identité
  documentaire, autonomie insuffisante ;
- exactement cinq appels structurés maximum, aucun retry ;
- cas insuffisant exige `research`, pas `clarify` ;
- tokens réels et timings serveur obligatoires dans le JSON ;
- aucune donnée réelle, backend, Q106 ou WinUI ;
- quatre gates fraîches avant modèle ;
- A442 reste immuable.

A450 est complet. A451 doit adapter le harnais commun et qualifier le nouveau
wrapper sans lancer Qwen.

### A451 — second harnais qualifié sans modèle

Rapport :
`phase5/A451-RAPPORT-QUALIFICATION-HARNAIS-SECOND-CANARI.md`.

- second Fact et variables isolés ; cinq cas A450 distincts ;
- désarmé **1/1**, aucune sortie live ;
- tokens/timings et `research` exact intégrés aux oracles ;
- audit **66/66 PASS** ;
- wrapper `-ArmOnly` PASS, zéro dotnet, zéro sortie ;
- DLL et sources figées ; processus ciblés 0, variables live 0 ;
- aucun modèle, backend, Q106 ou WinUI.

A451 est complet. A452 doit franchir ses quatre gates fraîches ; le modèle reste
interdit jusque-là.

### A452 préflight — quatre gates vertes

Rapport : `phase5/A452-PREFLIGHT-QUATRE-GATES-SECOND-CANARI.md`.

1. harnais/oracles : **PASS 66/66**, armement à blanc PASS ;
2. client : **PASS 1 520/1 520** sur DLL gelé ;
3. runtime/modèle : **PASS read-only**, serveur resté arrêté ;
4. armement final : **PASS**, hashes inchangés, sortie absente, état propre.

Une seule invocation live A452 est autorisée. Aucun retry ou changement
d'oracle ne sera permis quel que soit le résultat.

### A452 — second canari unique exécuté

- résultat officiel : **FAIL 3/5** ; aucune relance ;
- cinq cas, cinq appels, protocole valide **5/5** ;
- métriques structurées présentes **5/5** ;
- métrologie, RH et identité : PASS ;
- logistique : réponse E2 correcte mais E3 absent du pool présenté ;
- insuffisance : réponse négative publiée au lieu de `research` ;
- cinq latences sous 45 s ; ressources arrêtées.

### A453 — audit read-only complet

Rapport : `phase5/A453-RAPPORT-AUDIT-READONLY-SECOND-CANARI.md`.

- audit **51/51 PASS** ;
- protocole A442 0/5 → protocole A452 5/5, mais fonctionnel 3/5 ;
- cause pool : `SourceWindow.Take(2)` supprime silencieusement E3 ;
- cause insuffisance : Qwen choisit answer pour déclarer la valeur absente ;
- tokens prompt 2 649, completion 1 011, cache 971, évalués 1 678 ;
- médiane 27 494 ms, max 31 791 ms, 5/5 sous 45 s ;
- Q106 2/3 reste interdit.

A453 est complet. A454 doit figer le verdict et notifier Telegram sans relance.

### A454 — verdict de palier prêt pour notification

Rapport :
`phase5/RAPPORT-PALIER-2026-08-30-PHASE5-A450-A454-SECOND-CANARI-QWEN.md`.

- verdict : **TESTÉ_NON_APPROUVÉ, FAIL 3/5** ;
- protocole et métriques : PASS 5/5 ;
- qualité stricte : 3/5 ; performance isolée : 5/5 sous 45 s ;
- flag faux, WinUI non testée, Q106 reste 1/3 ;
- prochain palier : pool borné sans perte silencieuse et auto-évaluation Qwen
  structurée de l'adéquation, sans heuristique métier ;
- ressources arrêtées.

Le Telegram A450-A454 doit être envoyé et son reçu figé avant le palier
correctif suivant.

### Clôture A454 — Telegram confirmé

- notifier code 0, corps 1 725, total 1 855 caractères ;
- message 15 lignes, 1 776 octets, SHA `89ABB10E...0F6228D` ;
- mojibake 0, motif de secret 0 ;
- reçu `phase5/00-RECU-TELEGRAM-A450-A454.md` ;
- aucun identifiant de message inventé.

A450-A454 est clos. La phase 5 et le Goal restent actifs. Le prochain palier
correctif est déterministe et reste sans modèle jusqu'à son verdict.

### Ouverture A455-A459 — pool global et adéquation sémantique

Protocole :
`phase5/PROTOCOLE-A455-A459-POOL-GLOBAL-ET-ADEQUATION-SEMANTIQUE.md`.

- aucune relance de Qwen, du backend, de Q106 ou de WinUI ;
- budget de preuve global `clamp(2 × candidats, 2, 12)` ;
- allocation stable en tours entre fenêtres, sans classement sémantique code ;
- compteurs éligibles, présentés et tronqués obligatoires ;
- nouveau champ Qwen `answerAdequacy` et contrat versionné v2 ;
- le code valide seulement la cohérence décision/adéquation ;
- flag produit toujours faux.

### A455 — RED causal figé

Rapport : `phase5/A455-RAPPORT-RED-POOL-GLOBAL-ET-ADEQUATION.md`.

- tests A455 : **0/4**, quatre échecs attendus ;
- E1,E2,E3 matérialisés mais seulement E1,E2 présentés ;
- schéma v1 refuse encore `answerAdequacy` ;
- paire answer/information manquante sans erreur dédiée ;
- prompt et compteurs globaux absents ;
- TRX SHA `1E60B1DD...98E30A` ;
- transaction produit toujours SHA `599B3EA3...4562E0` ;
- ressources arrêtées, variables live 0.

A455 est complet. A456 doit corriger uniquement l'allocation mécanique et son
observabilité ; A457 restera responsable du contrat sémantique v2.

### A456 — frontière mécanique corrigée

Rapport : `phase5/A456-RAPPORT-POOL-GLOBAL-BORNE-TRACEABLE.md`.

- ancien chemin conservé à deux éléments par défaut ;
- transaction : fenêtres complètes puis budget global 2×candidats, plafond 12 ;
- allocation stable en tours et déduplication par EvidenceId ;
- trace : budget, éligibles, présentés, tronqués et IDs ;
- scénario causal : E1,E2 avant, puis E1,E2,E3 après ;
- budget 6, éligibles 3, présentés 3, tronqués 0 ;
- erreur restante isolée au parseur v2 encore absent ;
- TRX intermédiaire SHA `B471B2F2...FC2591A` ;
- aucun modèle, backend, Q106 ou WinUI.

A456 est complet. A457 peut modifier uniquement prompt, schéma et validation
croisée de l'adéquation déclarée par Qwen.

### A457 — auto-évaluation Qwen et contrat v2

Rapport : `phase5/A457-RAPPORT-CONTRAT-V2-ADEQUATION-LLM.md`.

- contrat versionné `source_backed_semantic_answer_transaction_v2` ;
- champ Qwen obligatoire `answerAdequacy`, quatre valeurs génériques ;
- table décision/adéquation validée mécaniquement ;
- aucune inférence sémantique, analyse lexicale ou correction automatique code ;
- mismatch fermé avec erreur dédiée ;
- RED A455 0/4 → GREEN A457 **4/4** ;
- transaction complète **17/17** ;
- TRX finaux SHA `2C46E519...8C39BF` et `A1EC6C7A...9FD2F8` ;
- harnais live aligné v2 mais non armé ;
- aucun modèle, backend, Q106 ou WinUI.

A457 est complet au niveau causal. A458 doit exécuter les régressions et audits
complets avant le verdict A459.

### A458 — validation déterministe complète

Rapport :
`phase5/A458-RAPPORT-VALIDATION-DETERMINISTE-POOL-ADEQUATION-V2.md`.

- transaction **17/17**, OpenAI **19/19**, AgentV2 **244/244** ;
- routeur **105/105**, architecture **21/21** ;
- noyau source-backed **397/397**, client complet **1 524/1 524** ;
- porte architecture initialement 20/21, corrigée par extraction de deux
  composants partiels sans relever les limites ;
- audit final **104/104 PASS** ;
- aucune heuristique métier ou textuelle sémantique ;
- A442 et A452 byte-identiques ;
- parité DLL WinUI exacte ; diff-check ciblé PASS ;
- ressources arrêtées, variables live 0 ;
- branche/HEAD et worktree historique préservés.

A458 est complet. A459 doit rendre le verdict du palier et notifier Telegram.

### A459 — verdict de palier prêt pour notification

Rapport :
`phase5/RAPPORT-PALIER-2026-08-30-PHASE5-A455-A459-POOL-ADEQUATION-V2.md`.

- verdict : **PASS déterministe, live non remesuré** ;
- E3 atteint maintenant le pool global borné et tracé ;
- Qwen possède l'évaluation `answerAdequacy`, code limité à la cohérence ;
- aucune heuristique métier, correction automatique ou fallback ;
- toutes les suites et l'audit final sont verts ;
- A442 reste FAIL 0/5, A452 reste FAIL 3/5 et Q106 reste 1/3 ;
- prochain palier : troisième canari distinct v2, jamais replay A452.

Le Telegram A455-A459 doit être envoyé et son reçu figé avant l'ouverture du
prochain protocole live.

### Clôture A459 — Telegram confirmé

- notifier code 0, corps 1 790, total 1 935 caractères ;
- message 15 lignes, 1 790 octets, SHA `712645CA...D69F4C8` ;
- mojibake 0, motif de secret 0 ;
- reçu `phase5/00-RECU-TELEGRAM-A455-A459.md` ;
- aucun identifiant de message inventé.

A455-A459 est clos. La phase 5 et le Goal restent actifs. Le prochain palier
doit préenregistrer un troisième canari v2 entièrement distinct avant tout
appel modèle.

### Ouverture A460-A464 — troisième canari v2

Protocole :
`phase5/PROTOCOLE-A460-A464-TROISIEME-CANARI-V2-COUVERTURE-DECISIONS.md`.

- cinq cas nouveaux, aucun replay A442/A452 ;
- couverture answer, research, clarify et context ;
- cas E3 dans une fenêtre unique de trois alternatives ;
- adéquation, next capability, leads et compteurs de pool exportés ;
- exactement cinq appels maximum, aucun retry ;
- backend, corpus réel, Q106 et WinUI interdits ;
- quatre gates fraîches avant tout modèle.

A460 est complet. A461 doit adapter et qualifier le harnais sans lancer Qwen.

### A461 — troisième harnais v2 qualifié sans modèle

Rapport :
`phase5/A461-RAPPORT-QUALIFICATION-HARNAIS-TROISIEME-CANARI-V2.md`.

- cinq cas A460 distincts et quatre décisions/adéquations couvertes ;
- pool complet, budget, éligibles, présentés, tronqués, leads et next vérifiés ;
- exactement un appel structuré par cas, fallback non structuré interdit ;
- première preuve `bin\Debug` écartée car DLL WinUI non paritaire ;
- reconstruction officielle Debug/x64 : 0 erreur, 0 avertissement ;
- Fact désarmé x64 **1/1 PASS** sur le FQN exact ;
- audit **82/82 PASS** ; wrapper `-ArmOnly` PASS, zéro dotnet ;
- DLL WinUI produit/adjacent SHA identique `27EC3D92...ECB606` ;
- sortie A462 absente, processus ciblés 0, variables live 0 ;
- aucun modèle, backend, Q106 ou WinUI.

A461 est complet. A462 doit franchir quatre portes fraîches sur les artefacts
x64 gelés avant l'unique invocation live ; aucune relance ne sera autorisée.

### A462 préflight — quatre gates vertes

Rapport : `phase5/A462-PREFLIGHT-QUATRE-GATES-TROISIEME-CANARI-V2.md`.

1. harnais/oracles : **PASS 82/82**, désarmé x64 1/1, ArmOnly PASS ;
2. client : **PASS 1 525/1 525** sur le DLL x64 gelé ;
3. runtime/modèle : **PASS read-only**, serveur resté arrêté ;
4. armement final : **PASS**, hashes inchangés, sortie absente, état propre.

Une seule invocation live A462 est autorisée. Son artefact sera officiel même
en cas d'échec ; aucun retry, replay partiel ou changement d'oracle ne suivra.

### A462 — troisième canari unique exécuté

- résultat officiel : **FAIL 3/5** ; aucune relance ;
- cinq cas, cinq appels, protocole v2 valide 5/5 ;
- pools complets 5/5, troncature 0, métriques présentes 5/5 ;
- E3/Summit, rétention absente et laboratoire : PASS ;
- formation : réponse conditionnelle E1/E2 au lieu de `clarify` ;
- NV-3 : `research` sans lead au lieu de `context` ancré E1 ;
- cinq latences sous 45 s ; ressources arrêtées.

### A463 — audit read-only complet

Rapport :
`phase5/A463-RAPPORT-AUDIT-READONLY-TROISIEME-CANARI-V2.md`.

- audit **87/87 PASS** ;
- JSON SHA `46028BCA...6945F37`, TRX SHA `9B774FDB...EBB5FC`,
  log SHA `3EA09407...C30E00` ;
- pool global et gestion de l'information absente confirmés live ;
- deux défauts isolés à l'arbitrage Qwen : `clarify` et `context` ;
- raisonnement brut reconnaît déjà l'ambiguïté et l'ancre documentaire ;
- tokens prompt 3 240, completion 1 033, cache 1 425, évalués 1 815 ;
- médiane 33 175 ms, max 33 731 ms, 5/5 sous 45 s ;
- A442/A452 immuables, Q106 1/3 toujours interdit ;
- ressources arrêtées, worktree historique préservé.

A463 est complet. A464 doit figer le verdict TESTÉ_NON_APPROUVÉ, notifier
Telegram, puis seulement ouvrir un palier déterministe sur la taxonomie de
décision générique, sans heuristique métier ni replay A462.

### A464 — verdict de palier et Telegram confirmés

Rapport :
`phase5/RAPPORT-PALIER-2026-08-30-PHASE5-A460-A464-TROISIEME-CANARI-V2.md`.

- verdict : **TESTÉ_NON_APPROUVÉ, FAIL 3/5** ;
- protocole, pool, provenance et métriques : PASS 5/5 ;
- E3 et insuffisance factuelle : correctifs confirmés live ;
- `clarify` et `context` : arbitrages Qwen encore non fiables ;
- aucune heuristique métier ou correction automatique autorisée ;
- message Telegram 1 879 caractères, SHA `FED78306...CEB57C` ;
- notifier code 0 : corps 1 879, total 2 010 caractères ;
- reçu `phase5/00-RECU-TELEGRAM-A460-A464.md` ;
- ressources arrêtées ; phase 5 et Goal actifs.

A460-A464 est clos. Le prochain palier est déterministe : tests causaux et
contrat de besoin de résolution déclaré par Qwen avant tout nouveau canari.

### Ouverture A465-A469 — besoin de résolution sémantique v3

Protocole :
`phase5/PROTOCOLE-A465-A469-BESOIN-RESOLUTION-SEMANTIQUE-V3.md`.

- aucun modèle, backend, Q106, donnée réelle ou WinUI ;
- A462 immuable et jamais rejoué ;
- Qwen déclare livrable complet, donnée utilisateur manquante et éventuelle
  ancre visible avant sa décision ;
- code limité à la cohérence champs/décision/adéquation/lead ;
- erreur dédiée et fail-closed, aucune correction automatique ;
- contre-exemples contre la surclarification et le surcontexte ;
- RED causal obligatoire avant tout patch produit.

A465 est complet. A466 doit écrire et figer les tests RED sans lancer Qwen.

### A466 — RED causal figé

Rapport : `phase5/A466-RAPPORT-RED-BESOIN-RESOLUTION-V3.md`.

- neuf Facts génériques, LLM entièrement scripté ;
- résultat attendu : **0/9**, zéro erreur d'infrastructure ;
- contrat encore v2, champs v3 absents, nouveaux objets rejetés à la racine ;
- erreur dédiée, acceptations, prompt et trace encore absents ;
- deux contre-exemples rouges contre la surcorrection ;
- TRX SHA `934A073C...91DF82` ;
- sources produit v2 et DLL WinUI figées avant patch ;
- aucun modèle, backend, Q106 ou WinUI ; ressources arrêtées.

A466 est complet. A467 peut implémenter uniquement le contrat v3 et sa
cohérence mécanique, puis faire passer exactement les mêmes neuf tests.

### A467 — contrat de résolution v3 GREEN causal

Rapport : `phase5/A467-RAPPORT-CONTRAT-BESOIN-RESOLUTION-V3.md`.

- contrat `source_backed_semantic_answer_transaction_v3` ;
- Qwen déclare livrable complet, input utilisateur manquant et ancre visible ;
- code limité à quatre combinaisons de cohérence et au fail-closed ;
- contradiction : erreur dédiée, aucune correction automatique ;
- RED 0/9 → ciblé v3 **13/13 PASS** ;
- transaction complète **24/24 PASS** ;
- harnais v3 désarmé **1/1 PASS**, aucune sortie live ;
- contre-exemples answer et research verts ;
- runner maintenu exactement à 2 500 lignes ;
- DLL WinUI produit/test byte-identiques ;
- aucun modèle, backend, Q106 ou WinUI ; ressources arrêtées.

A467 est complet causalement. A468 doit exécuter toutes les régressions et un
audit statique anti-heuristiques avant le verdict A469.

### A468 — validation déterministe complète

Rapport :
`phase5/A468-RAPPORT-VALIDATION-DETERMINISTE-BESOIN-RESOLUTION-V3.md`.

- causal v3 13/13, transaction 24/24, OpenAI 19/19 ;
- AgentV2 253/253, routeur 105/105, architecture 21/21 ;
- noyau source-backed 406/406, client complet 1 534/1 534 ;
- harnais v3 désarmé 1/1, aucune sortie live ;
- audit final **122/122 PASS** ;
- aucune heuristique textuelle, Regex ou terme des cas live ;
- limites inchangées, runner exactement 2 500 lignes ;
- A442/A452/A462 immuables, parité DLL exacte ;
- ressources arrêtées, worktree historique préservé.

A468 est complet. A469 doit notifier le PASS déterministe sans requalifier les
canaris historiques ni autoriser implicitement un run live.

### A469 — verdict prêt pour notification

Rapport :
`phase5/RAPPORT-PALIER-2026-08-30-PHASE5-A465-A469-BESOIN-RESOLUTION-V3.md`.

- verdict : **PASS déterministe, live non remesuré** ;
- Qwen déclare désormais le besoin de résolution avant sa décision ;
- code limité à la cohérence déclarative et au fail-closed ;
- contre-exemples anti-surcorrection verts ;
- toutes les suites et l'audit sont verts ;
- A442/A452/A462 et Q106 1/3 restent inchangés ;
- prochain palier : quatrième canari distinct v3, jamais replay A462.

Le Telegram A465-A469 doit être envoyé et son reçu figé avant l'ouverture du
prochain protocole live.

### Clôture A469 — Telegram confirmé

- notifier code 0, corps 1 881, total 2 021 caractères ;
- message 17 lignes, 1 881 octets, SHA `05B55357...F1B43F` ;
- mojibake 0, motif de secret 0 ;
- reçu `phase5/00-RECU-TELEGRAM-A465-A469.md` ;
- aucun identifiant de message inventé.

A465-A469 est clos. La phase 5 et le Goal restent actifs. Le prochain palier
doit préenregistrer un quatrième canari v3 entièrement distinct avant tout
appel modèle.

### Ouverture A470-A474 — quatrième canari v3 distinct

Protocole :
`phase5/PROTOCOLE-A470-A474-QUATRIEME-CANARI-V3-DISTINCT.md`.

- cinq domaines et contenus nouveaux ; aucun replay historique ;
- answer contraint E3, clarify, context, research et answer multi-variante ;
- trois déclarations v3, adéquation, next, leads et pool vérifiés exactement ;
- exactement cinq appels et cinq requêtes maximum, aucun retry ;
- backend, corpus réel, Q106 et WinUI interdits ;
- quatre gates fraîches avant toute invocation modèle.

A470 est complet. A471 doit ajouter et qualifier le harnais distinct sans
lancer Qwen.

### A471 — quatrième harnais v3 qualifié sans modèle

Rapport :
`phase5/A471-RAPPORT-QUALIFICATION-HARNAIS-QUATRIEME-CANARI-V3.md`.

- cinq cas distincts, quatre décisions et trois déclarations v3 ;
- Fact désarmé x64 **1/1 PASS** sur le FQN exact ;
- audit **88/88 PASS** ;
- wrapper `-ArmOnly` PASS, zéro dotnet, zéro sortie ;
- DLL et sources figées, parité WinUI exacte ;
- sortie A472 absente, processus ciblés 0, variables live 0 ;
- aucun modèle, backend, Q106 ou WinUI.

A471 est complet. A472 doit franchir quatre portes fraîches sur le binaire gelé
avant l'unique invocation live ; aucune relance ne sera autorisée.

### A472 préflight — quatre gates vertes

Rapport : `phase5/A472-PREFLIGHT-QUATRE-GATES-QUATRIEME-CANARI-V3.md`.

1. harnais/oracles : **PASS 88/88**, désarmé x64 1/1, ArmOnly PASS ;
2. client : **PASS 1 535/1 535** sur le DLL x64 gelé ;
3. runtime/modèle : **PASS read-only**, serveur resté arrêté ;
4. armement final : **PASS**, hashes inchangés, sortie absente, état propre.

Une seule invocation live A472 est autorisée. Son résultat sera officiel sans
retry, replay partiel ou changement d'oracle.

### A472 — quatrième canari v3 unique exécuté

- résultat officiel : **FAIL 4/5**, aucune relance ;
- cinq cas distincts, cinq appels structurés, cinq requêtes serveur ;
- pool complet 5/5, budget 12, troncature 0 ;
- `clarify`, `context`, information absente et answer multi-source : PASS ;
- comparaison E1/E2/E3 : FAIL, Qwen reconnaît Cirrus/41 s mais exige à tort
  une preuve qu'aucune alternative extérieure au pool n'existe ;
- contradiction E3 visible + `research` rejetée fail-closed ;
- aucune sortie invalide publiée ; ressources arrêtées.

### A473 — audit read-only complet

Rapport :
`phase5/A473-RAPPORT-AUDIT-READONLY-QUATRIEME-CANARI-V3.md`.

- audit **87/87 PASS** ;
- JSON SHA `9A8570B3...6598318A`, TRX SHA `CA6BC36E...60BE8EB`,
  log SHA `B419287C...5405A1A4` ;
- quatre succès stricts et un échec isolé exactement ;
- cinq requêtes serveur sans retry, traces JSON/log concordantes ;
- pool complet 5/5, troncature 0, identifiants confinés au pool ;
- défaut qualifié : fermeture de portée sémantique Qwen, pas transport ;
- A442/A452/A462, sources et binaires immuables ;
- processus ciblés 0, variables live 0.

A473 est complet. A474 doit figer le verdict TESTÉ_NON_APPROUVÉ et notifier
Telegram avant tout nouveau travail causal.

### A474 — verdict prêt pour notification

Rapport :
`phase5/RAPPORT-PALIER-2026-08-30-PHASE5-A470-A474-QUATRIEME-CANARI-V3.md`.

- verdict : **TESTÉ_NON_APPROUVÉ — FAIL 4/5** ;
- quatre routes sémantiques correctes sur cas distincts ;
- une sur-exigence d'exhaustivité du monde ouvert empêche la bonne réponse ;
- contradiction correctement bloquée, aucune correction automatique ;
- 4/5 non comparé statistiquement au 3/5 d'A462 ;
- Q106 et WinUI toujours interdits ;
- prochain travail déterministe, aucun live implicite.

Le Telegram A470-A474 doit être envoyé et son reçu figé avant l'ouverture du
prochain protocole.

### Clôture A474 — Telegram confirmé

- notifier code 0, corps 2 072, total 2 203 caractères ;
- message 18 lignes, 2 072 octets, SHA `E3A4A84F...05A11A9` ;
- mojibake 0, motif de secret 0 ;
- reçu `phase5/00-RECU-TELEGRAM-A470-A474.md` ;
- aucun identifiant de message inventé.

A470-A474 est clos. La phase 5 et le Goal restent actifs. Le prochain palier
doit traiter causalement la fermeture de portée des preuves sans modifier une
décision Qwen en code et sans nouveau live avant régressions complètes.

### Ouverture A475-A479 — portée canonique des preuves v3

Protocole :
`phase5/PROTOCOLE-A475-A479-PORTEE-CANONIQUE-DES-PREUVES-V3.md`.

- aucun modèle, backend, Q106, donnée réelle ou WinUI ;
- pool présenté = périmètre documentaire de la transaction, pas monde entier ;
- hypothèses absentes interdites comme motif unique d'incomplétude ;
- absence du pool jamais transformée en preuve de non-existence ;
- hors-périmètre explicite ou fait nécessaire manquant conserve la recherche ;
- prompt LLM uniquement, aucune décision ou correction automatique en code ;
- quatre tests RED causaux obligatoires avant patch.

A475 est complet. A476 doit figer le RED du contrat de prompt sans lancer Qwen.

### A476 — RED causal figé

Rapport :
`phase5/A476-RAPPORT-RED-PORTEE-CANONIQUE-PREUVES-V3.md`.

- quatre Facts génériques, LLM entièrement scripté ;
- résultat attendu : **0/4**, zéro erreur d'infrastructure ;
- quatre clauses de portée absentes du prompt actuel ;
- aucun terme, domaine ou valeur des canaris live ;
- TRX SHA `3BDDFF7F...0CA1915` ;
- transaction pré-patch SHA `0A005F07...2363D0` ;
- aucun modèle, backend, Q106 ou WinUI ; ressources arrêtées.

A476 est complet. A477 peut modifier uniquement le prompt système puis rendre
verts exactement les mêmes tests, sans changer le contrat ou les décisions.

### A477 — doctrine de portée GREEN causale

Rapport :
`phase5/A477-RAPPORT-GREEN-PORTEE-CANONIQUE-PREUVES-V3.md`.

- seul le prompt système a changé ;
- RED 0/4 → mêmes Facts **4/4 PASS** ;
- invariants v3 existants **13/13 PASS** ;
- transaction complète **32/32 PASS** ;
- contrat/parseur v3 et runner byte-identiques ;
- aucune décision automatique, aucun champ ou appel supplémentaire ;
- DLL WinUI produit/test SHA identique `EE607179...AD9301E` ;
- aucun modèle, backend, Q106 ou WinUI ; ressources arrêtées.

A477 est complet causalement. A478 doit exécuter toutes les régressions et
l'audit statique avant le verdict A479.

### A478 — validation déterministe complète

Rapport :
`phase5/A478-RAPPORT-VALIDATION-DETERMINISTE-PORTEE-CANONIQUE-V3.md`.

- portée 4/4, résolution v3 13/13, transaction 32/32 ;
- OpenAI 19/19, AgentV2 257/257, routeur 105/105 ;
- architecture 21/21, noyau source-backed 410/410 ;
- quatre harnais live désarmés 4/4 ;
- client complet **1 539/1 539** ;
- audit final **100/100 PASS** ;
- aucun terme live, Regex, heuristique ou décision automatique ;
- contrat/parseur/runner inchangés, parité DLL exacte ;
- A442/A452/A462/A472 immuables ; ressources arrêtées.

A478 est complet. A479 doit notifier un PASS déterministe sans requalifier le
live ni autoriser implicitement un nouveau run.

### A479 — verdict prêt pour notification

Rapport :
`phase5/RAPPORT-PALIER-2026-08-30-PHASE5-A475-A479-PORTEE-CANONIQUE-V3.md`.

- verdict : **PASS déterministe, live non remesuré** ;
- portée documentaire bornée expliquée à Qwen ;
- monde réel jamais déclaré exhaustif ;
- manque explicite et fait absent conservent leurs routes ;
- Qwen reste décideur, code limité à la cohérence fail-closed ;
- toutes les suites et l'audit sont verts ;
- prochain live possible seulement dans un palier distinct préenregistré.

Le Telegram A475-A479 doit être envoyé et son reçu figé avant toute ouverture
d'un cinquième canari.

### Clôture A479 — Telegram confirmé

- notifier code 0, corps 2 039, total 2 175 caractères ;
- message 18 lignes, 2 039 octets, SHA `76A5E353...0E622896` ;
- mojibake 0, motif de secret 0 ;
- reçu `phase5/00-RECU-TELEGRAM-A475-A479.md` ;
- aucun identifiant de message inventé.

A475-A479 est clos. La phase 5 et le Goal restent actifs. Un nouveau live exige
un protocole distinct, des cas nouveaux, quatre gates fraîches et une invocation
unique sans retry.

### Ouverture A480-A484 — cinquième canari v3 de portée

Protocole :
`phase5/PROTOCOLE-A480-A484-CINQUIEME-CANARI-V3-PORTEE.md`.

- cinq domaines et contenus nouveaux, aucun replay historique ;
- answer sur comparaison bornée ;
- recherche pour exclusivité mondiale explicitement hors périmètre ;
- recherche pour fait nécessaire absent ;
- clarification pour donnée utilisateur manquante ;
- contexte sur ancre documentaire E1 ;
- exactement cinq appels/requêtes, aucun retry ;
- backend, corpus réel, Q106 et WinUI interdits ;
- quatre gates fraîches avant toute invocation modèle.

A480 est complet. A481 doit ajouter et qualifier le harnais sans lancer Qwen.

### A481 — cinquième harnais v3 qualifié sans modèle

Rapport :
`phase5/A481-RAPPORT-QUALIFICATION-HARNAIS-CINQUIEME-CANARI-V3-PORTEE.md`.

- cinq cas distincts et oracles A480 exacts ;
- Fact désarmé x64 **1/1 PASS** sur le FQN exact ;
- audit **80/80 PASS** ;
- wrapper `-ArmOnly` PASS, zéro dotnet, zéro sortie ;
- sources et DLL gelées, parité WinUI exacte ;
- sortie A482 absente, processus ciblés 0, variables live 0 ;
- aucun modèle, backend, Q106 ou WinUI.

A481 est complet. A482 doit franchir quatre portes fraîches sur le binaire gelé
avant l'unique invocation live ; aucune relance ne sera autorisée.

### A482 préflight — quatre gates vertes

Rapport :
`phase5/A482-PREFLIGHT-QUATRE-GATES-CINQUIEME-CANARI-V3-PORTEE.md`.

1. harnais/oracles : **PASS 80/80**, désarmé x64 1/1, ArmOnly PASS ;
2. client : **PASS 1 540/1 540** sur le DLL x64 gelé ;
3. runtime/modèle : **PASS read-only**, serveur resté arrêté ;
4. armement final : **PASS**, hashes inchangés, sortie absente, état propre.

Une seule invocation live A482 est autorisée. Son résultat sera officiel sans
retry, replay partiel ou changement d'oracle.

### A482 — cinquième canari v3 unique exécuté

- résultat officiel : **FAIL 2/5**, aucune relance ;
- cinq cas, cinq appels, cinq requêtes, `truncated=0` 5/5 ;
- délai absent et clarification utilisateur : PASS ;
- drone : comparaison lue mais non résolue, sortie invalide bloquée ;
- rail : bonne route context brute, mauvaise adéquation, sortie bloquée ;
- vanne : conclusion mondiale non impliquée publiée avec contrat valide ;
- pool complet 5/5, troncature 0 ; ressources arrêtées.

### A483 — audit read-only complet

Rapport :
`phase5/A483-RAPPORT-AUDIT-READONLY-CINQUIEME-CANARI-V3-PORTEE.md`.

- audit **88/88 PASS** ;
- JSON SHA `E0C66264...B3AF66E`, TRX SHA `83A41D74...21DC6B2`,
  log SHA `92CC8FF3...9FEEED47` ;
- deux succès stricts, trois classes d'échec isolées ;
- deux sorties invalides correctement fail-closed ;
- une conclusion sémantique non impliquée traverse le contrat mécanique ;
- cinq requêtes sans retry ni troncature serveur ;
- A442/A452/A462/A472 immuables ; ressources arrêtées.

A483 est complet. A484 doit notifier le verdict et interdire une nouvelle
retouche locale ou un sixième canari monolithique immédiat.

### A484 — verdict prêt pour notification

Rapport :
`phase5/RAPPORT-PALIER-2026-08-30-PHASE5-A480-A484-CINQUIEME-CANARI-V3-PORTEE.md`.

- verdict : **TESTÉ_NON_APPROUVÉ — FAIL 2/5** ;
- provenance et cohérence mécanique nécessaires mais insuffisantes ;
- implication preuve→claim non garantie par le contrat actuel ;
- flag, Q106 et WinUI interdits ;
- prochaine étape : recul comparatif end-to-end, pas patch local supplémentaire.

Le Telegram A480-A484 doit être envoyé et son reçu figé avant l'ouverture de
ce comparatif architectural.

### Clôture A484 — Telegram confirmé

- notifier code 0, corps 2 007, total 2 138 caractères ;
- message 18 lignes, 2 007 octets, SHA `06E08CE0...37B1ACF8` ;
- mojibake 0, motif de secret 0 ;
- reçu `phase5/00-RECU-TELEGRAM-A480-A484.md` ;
- aucun identifiant de message inventé.

A480-A484 est clos. La phase 5 et le Goal restent actifs. Le prochain palier
doit agréger les preuves live historiques et comparer des architectures
end-to-end avant toute nouvelle modification du chemin produit.

### Ouverture A485-A489 — recul comparatif architectural

Protocole :
`phase5/PROTOCOLE-A485-A489-RECUL-COMPARATIF-ARCHITECTURES-SEMANTIQUES.md`.

- produit et modèle gelés, aucun nouveau live ;
- baseline descriptive sur 25 cas/25 appels officiels ;
- taxonomie des erreurs de décision, contrat et implication ;
- comparaison V0 monolithique, V1 juge+writer, V2 reviewer+repair, V3 modèle ;
- sécurité de publication prioritaire sur le score brut ;
- coûts, tokens, appels et latence inclus ;
- choix d'une seule expérience minimale avant tout prototype.

A485 est complet. A486 doit produire l'agrégat reproductible des cinq campagnes
immuables sans toucher au produit.

### A486 — baseline des 25 cas figée

Rapport : `phase5/A486-RAPPORT-BASELINE-25-CAS-CANARIS.md`.

- audit **79/79 PASS** sur cinq JSON immuables ;
- strict 12/25, protocole valide 17/25, 70 erreurs oracle ;
- answer 6/13, research 3/6, clarify 2/3, context 1/3 ;
- 13 échecs couverts exactement par cinq catégories primaires ;
- deux faux answers dangereux mais contractuellement valides ;
- v3 moderne : 6/10 stricts, 3 invalides, 1 échec d'implication valide ;
- tokens disponibles 15 092/4 242 sur 20 cas ;
- latence médiane 27 850 ms, p95 39 626 ms, max 40 178 ms ;
- aucune tendance statistique affirmée entre campagnes distinctes ;
- produit/modèle inchangés, ressources arrêtées.

A486 est complet. A487 doit comparer les variantes sur ces risques mesurés et
les coutures réelles du pipeline avant de choisir un prototype.

### A487 — comparaison architecturale sur coutures réelles

Rapport :
`phase5/A487-RAPPORT-COMPARATIF-ARCHITECTURES-SEMANTIQUES.md`.

- cause structurelle confirmée : la transaction marque ses propres réponses
  `AnswerSemanticallyFinal=true`, puis le runner saute le reviewer indépendant ;
- la réponse globale dangereuse A482 a donc passé le contrat mécanique sans
  contrôle d'implication preuve→claim distinct ;
- V0 rejetée : minimale en appels mais sécurité de publication insuffisante ;
- V1 améliore la séparation résolution/writer mais ne bloque pas seule une
  extrapolation du writer ;
- **V2 retenue** : résolution LLM, writer LLM, reviewer LLM indépendant, une
  réparation maximum et aucune publication sans acceptation ;
- V2 réutilise `ReviewSemanticsAsync`, le writer, le verifier mécanique et la
  boucle de réparation existants ; aucun pipeline parallèle n'est créé ;
- budgets théoriques après preuves : 1 appel non-answer, 3 answer accepté,
  5 answer avec l'unique réparation ; ils ne sont pas des mesures live ;
- V3 reste un contrôle futur apparié, après isolation de l'effet architecture ;
- audit read-only A487 **29/29 PASS**, six sources produit conformes à leurs
  empreintes gelées ;
- produit, modèle, backend, corpus, Q106 et WinUI inchangés ; aucun appel live.

A487 est complet. A488 doit préenregistrer le prototype V2 minimal : contrats,
tests RED, oracles de sécurité, budget d'une réparation, métriques, critères
d'arrêt et protocole live apparié, toujours sans implémenter le produit.

### A488 — expérience V2 préenregistrée avant code

Document :
`phase5/A488-PREENREGISTREMENT-V2-RESOLUTION-WRITER-REVIEW.md`.

- question expérimentale unique, cinq hypothèses et H1 sécurité comme gate dur ;
- nouveau flag V2 dédié faux par défaut ; V0 historique conservé et activation
  simultanée interdite avant tout appel ;
- contrat `source_backed_semantic_resolution_v4` sans claims, texte ni
  présentation, avec sélection explicite d'EvidenceIds pour `answer` ;
- writer structuré v1 et reviewer existants réutilisés sans fork ;
- automate fail-closed : publication uniquement après reviewer `accept` ;
- budget dédié d'exactement une réparation, puis nouvelle revue obligatoire ;
- 21 tests RED nommés et sept contre-exemples génériques figés ;
- RED-API puis RED comportemental, tests ciblés, full client, audit statique et
  parité binaire requis avant live ;
- live apparié futur sur les cinq cas A482, V0 officiel utilisé sans replay ;
- PASS live exige 5/5, zéro faux answer, zéro publication sans acceptation,
  traces/coûts complets et artefacts immuables ;
- audit read-only A488 **40/40 PASS**, huit sources produit et contrôle A482
  conformes à leurs empreintes, processus/variables live à zéro ;
- A490-A494 réservés au prototype, A495-A499 au live unique si gates vertes ;
- produit, modèle, backend, corpus, Q106 et WinUI inchangés pendant A488.

A488 est complet et audité. A489 doit figer la décision du palier, notifier
Telegram, conserver les ressources arrêtées et ouvrir A490 sans encore
implémenter le prototype dans ce palier.

### A489 — clôture du recul comparatif et notification

Rapport :
`phase5/RAPPORT-PALIER-2026-08-30-PHASE5-A485-A489-RECUL-COMPARATIF-V2.md`.

- audit pré-Telegram **35/35 PASS** ;
- A486 79/79, A487 29/29 et A488 40/40 rejoués read-only ;
- neuf artefacts conformes, Git exact `SAAIA_V3.1` / `5f35881c...` ;
- worktree historique 561 entrées : 79 suivies, 482 non suivies ;
- message Telegram 1 915 caractères, 19 lignes, mojibake/secret 0 ;
- notifier code 0, corps 1 915, total 2 033 ;
- message SHA
  `6DC857B7F2A784BF356640D7AC7BF1BCF24FCF3E55DBDD7EFC1BA9087B60E9DA` ;
- reçu `phase5/00-RECU-TELEGRAM-A485-A489.md` ;
- processus ciblés et variables live 0 ;
- aucun résultat produit/live requalifié, Q106/WinUI toujours interdits.

A485-A489 est clos. La phase 5 et le Goal restent actifs. A490 ouvre le palier
déterministe A490-A494 exactement préenregistré : RED-API, RED comportemental,
prototype V2 derrière flag faux par défaut, validations et audit, sans live.

### Ouverture A490-A494 — prototype V2 déterministe

Protocole : `phase5/PROTOCOLE-A490-A494-PROTOTYPE-V2-DETERMINISTE.md`.

- A488 est la source de vérité, aucune extension d'oracle ;
- A490 gèle protocole/Git/ressources sans code produit ;
- A491 ajoute les 21 tests et consigne RED-API puis RED comportemental ;
- A492 connecte uniquement résolution→writer→reviewer→repair borné ;
- A493 exécute les régressions, full client, audits et parité ;
- A494 décide, rapporte et notifie ; aucun live dans le palier ;
- toute publication sans reviewer accept, tout skip ou deuxième repair arrête
  le palier en TESTÉ_NON_APPROUVÉ.

A490 est complet : ADR/A488 relus, protocole figé, branche/HEAD inchangés,
processus et variables live à zéro. A491 doit maintenant produire le RED-API
préenregistré avant toute surface produit V2.

### A491 — RED préenregistré obtenu

- RED-API officiel : code 1, CS1061 unique sur l'option V2 absente ;
- seule la surface du flag faux par défaut a ensuite été ajoutée ;
- RED comportemental officiel : **16 échecs, 7 succès, 23 cas** ;
- causes observées : contrat absent, flags non exclusifs, V2 ignorée, reviewer,
  réparation et blocage absents ;
- deux tentatives de plumbing test non comptées sont documentées ;
- aucun oracle sémantique n'a été modifié, aucun modèle/live n'a été appelé.

Artefacts : `phase5/A491-SORTIE-RED-API-V2.txt` et
`phase5/A491-SORTIE-RED-COMPORTEMENTAL-V2.txt`.

A491 est complet. A492 peut raccorder uniquement les coutures préenregistrées.

### A492 — prototype V2 minimal, GREEN ciblé

Rapport : `phase5/A492-RAPPORT-IMPLEMENTATION-MINIMALE-V2-DETERMINISTE.md`.

- flag V2 faux par défaut et activation V0/V2 simultanée interdite ;
- contrat strict `source_backed_semantic_resolution_v4`, sans claims ;
- décision LLM transportée vers le writer structuré existant ;
- reviewer indépendant obligatoire avant publication ;
- une réparation V2 maximum, suivie d'une nouvelle revue ;
- deuxième revise, need_more_evidence et protocoles invalides fail-closed ;
- traces résolution/writer/reviewer/repair/blocage raccordées ;
- aucun hardcode de corpus/fixture dans les nouveaux fichiers runtime ;
- GREEN officiel **23/23**, TRX SHA
  `2A76C865C8A43006B229CCADC6DBDA7FD7AE9059C6AC64BC93EF2307D1DA68E5` ;
- processus et variables live à zéro après tests ; aucun appel modèle/live.

A492 est complet mais non encore approuvé. A493 doit maintenant élargir les
régressions et produire les audits/parités requis avant tout verdict A494.

### A493 — toutes les gates déterministes passent

Rapport : `phase5/A493-RAPPORT-GREEN-REGRESSIONS-AUDIT-PARITE-V2.md`.

- première régression : 529/530, seul gate rouge = runner 2 561 > 2 500 lignes ;
- traces/handoff extraits dans un partial, runner revenu à 2 500 lignes ;
- régressions ciblées officielles : **530/530** ;
- suite client x64 complète : **1 563/1 563** ;
- audit statique/parité : **98/98 assertions**, code 0 ;
- deux TRX chargent l'unique assembly test x64 attendu ;
- DLL WinUI adjacente et produit byte-identiques, SHA
  `1C99B2020EB2C9250F0E776EC9B27BED758ED2F72B77D82808A0A631B6DF873D` ;
- nouvelles méthodes et option V2 prouvées dans la DLL par réflexion ;
- `git diff --check` code 0 et espaces finaux des fichiers non suivis audités ;
- aucun terme de fixture dans les neuf fichiers runtime concernés ;
- serveur Roslyn résiduel arrêté ; processus ciblés et variables live 0.

Verdict A493 : **PASS DÉTERMINISTE — LIVE NON ENCORE EXÉCUTÉ**. A494 doit
notifier ce palier et figer son reçu avant l'ouverture autorisée d'A495.

### A494 — verdict déterministe notifié et reçu figé

Rapport :
`phase5/RAPPORT-PALIER-2026-08-30-PHASE5-A490-A494-PROTOTYPE-V2-DETERMINISTE.md`.

- verdict : **PASS DÉTERMINISTE — LIVE NON EXÉCUTÉ** ;
- audit pré-Telegram : **54/54 PASS** ;
- message : 1 302 caractères, 27 lignes, SHA
  `D4CB0AA7D233B6C66AFCFBFD82666047833F9A181F17EC7A56B5ACC8D5C50C38` ;
- notifier code 0, corps 1 302, total 1 426 caractères ;
- reçu : `phase5/00-RECU-TELEGRAM-A490-A494.md` ;
- aucun identifiant Telegram non exposé n'est inventé ;
- processus ciblés et variables live toujours à zéro au moment de l'envoi.

A490-A494 est clos. La phase 5 et le Goal restent actifs. A495-A499 peut
maintenant s'ouvrir selon le protocole live apparié déjà préenregistré en A488 :
cinq cas A482, V0 historique sans replay, V2 unique, une réparation maximum,
maximum 35 appels structurés, PASS uniquement à 5/5 et zéro faux answer publié.

### Ouverture A495-A499 — canari live V2 apparié A482

Protocole : `phase5/PROTOCOLE-A495-A499-CANARI-LIVE-V2-APPARIE-A482.md`.

- A495 crée et audite un harnais V2 strictement désarmé ;
- A496 compile et valide le harnais sans modèle ;
- A497 franchit quatre gates fraîches puis arme une seule invocation ;
- A498 exécute une campagne officielle unique, sans retry manuel ;
- A499 audite, décide, rapporte et notifie ;
- V0 A482 reste immuable à 2/5 et n'est pas rejoué ;
- PASS live exige 5/5, zéro faux answer et reviewer accept avant publication ;
- tout autre résultat reste TESTÉ_NON_APPROUVÉ, sans Q106/WinUI.

A495 est ouvert avec ressources et variables live à zéro. Aucun appel modèle ne
peut avoir lieu avant création, compilation et audit du harnais désarmé.

### A495-A497 — harnais désarmé et préflight quatre gates PASS

Rapport : `phase5/A497-PREFLIGHT-QUATRE-GATES-CANARI-LIVE-V2-A482.md`.

- fixtures/oracles V2 égaux par réflexion aux cinq cas A482 ;
- harnais live désarmé et parité : **2/2** ;
- audit statique désarmé : **65/65** ;
- régressions ciblées : **532/532** ; client x64 : **1 565/1 565** ;
- assembly, WinUI, sources, profil, llama-server et modèle figés par SHA ;
- wrapper fail-fast : une occurrence dotnet, FQN exact, no-build/no-restore ;
- audit final quatre gates : **31/31** ;
- sortie A498 absente, invocation officielle 0, ressources/variables zéro ;
- V0 A482 immuable et non rejoué.

A497 autorise exactement une invocation officielle A498. Ses JSON, JSONL, TRX
et log seront définitifs quel que soit le verdict ; aucun replay n'est permis.

### A498-A499 — invocation officielle rouge avant LLM

Rapport :
`phase5/RAPPORT-PALIER-2026-08-30-PHASE5-A495-A499-CANARI-V2-NON-PROBANT.md`.

- l'unique invocation A498 autorisée a été exécutée une fois, sans replay ;
- cinq enveloppes de cas exécutées, mais **0/5 approuvé** et **0 appel LLM** ;
- JSONL vide, TRX 1/1 échec, zéro publication et zéro faux answer publié ;
- les cinq cas ont échoué sur la même couture avant résolution : le double
  exigeait `rag_search` alors que le runner transmet son nom interne normalisé
  `rag.search` ;
- le défaut est dans le harnais gelé, pas dans la normalisation runtime ;
- **V2 n'a pas été évaluée sémantiquement** : aucun résultat answer/research/
  clarify/context, writer, reviewer, repair, tokens ou latence n'est mesuré ;
- les quatre artefacts officiels A498 sont conservés avec empreintes figées ;
- audit A499 reproductible requis avant Telegram ;
- processus ciblés et variables live ramenés à zéro ;
- Q106 et WinUI restent interdits.

Verdict : **TESTÉ_NON_APPROUVÉ — CANARI NON PROBANT**. A498 est clos et ne
sera pas rejoué. La phase 5 et le Goal restent actifs. Toute continuation doit
ouvrir un nouveau palier sous de nouveaux identifiants, corriger seulement le
double de test, prouver RED/GREEN et toutes les gates fraîches avant d'autoriser
au plus une nouvelle campagne officielle distincte.

### A499 — clôture notifiée et ressources libérées

- audit pré-Telegram : **109/109 PASS** ;
- message Telegram : 1 305 caractères, 28 lignes, SHA
  `DC46272997A079C8FE7A6BFFAAE4BB1497E5D39D4F8B33847AF1C386B316B932` ;
- notifier code 0, corps 1 305, total 1 425 caractères ;
- reçu : `phase5/00-RECU-TELEGRAM-A495-A499.md`, SHA
  `69406ECC7C5A76320A4FE27EC1C9950AAAD6395054CBB489C8D202983C86C1B0` ;
- sonde après envoi : **12/12 PASS**, SHA
  `59184DFCAC7B4B40CE5993FC5FFF192CC2F16494C5409F56289450D11F79DE25` ;
- processus ciblés et variables live : 0.

A495-A499 est clos. Le Goal reste actif et A498 demeure un résultat historique
rouge non probant, sans replay.

### Ouverture A500-A504 — correction du harnais et canari distinct

Protocole :
`phase5/PROTOCOLE-A500-A504-CORRECTION-HARNAIS-ET-NOUVEAU-CANARI-V2.md`.

- A500 fige une hypothèse unique : le tool executor reçoit `rag.search` ;
- seule la condition du double et son message peuvent être corrigés ;
- trois tests de couture sont ajoutés et doivent produire RED puis GREEN ;
- questions, missions, pools, EvidenceIds, oracles, prompts, runtime, modèle et
  budgets restent inchangés ;
- A498 et V0 A482 sont conservés sans replay ;
- toutes les gates désarmées doivent repasser avant live ;
- A503, si autorisé, utilise un nouveau répertoire et une seule invocation ;
- PASS live reste 5/5, zéro faux answer et aucune publication sans acceptation ;
- tout autre résultat reste TESTÉ_NON_APPROUVÉ ; Q106/WinUI interdits.

A500 est ouvert. Aucun code ni appel modèle n'est autorisé avant l'audit du
préenregistrement, des empreintes historiques et de l'état des ressources.

### A500 — ouverture auditée

- protocole figé : 5 372 octets, SHA
  `8A3CBC9187B2598A126FF3FE7CDB9FEAFB95FC8AA95CB2DB3611D2DA34D549D1` ;
- audit d'ouverture : **40/40 PASS** ;
- sortie audit SHA
  `D4D9C1D4DA25CF0E24EEA5301A593512FD484AE38D702E0FD5167535912C08B6` ;
- harnais A498 encore inchangé SHA `838974E6...F329F` ;
- quatre artefacts A498, reçu A499 et sonde après Telegram immuables ;
- tests RED et wrapper A503 absents au gate ;
- branche/HEAD inchangés, processus et variables live 0.

A500 est complet. A501 peut ajouter exclusivement les trois tests de couture
préenregistrés et capturer RED avant de corriger le double.

### A501 — RED/GREEN de la couture interne

Rapport : `phase5/A501-RAPPORT-RED-GREEN-CORRECTION-HARNAIS.md`.

- trois tests ajoutés avant correction ;
- RED officiel : **0/3**, code 1, causes exactement attendues ;
- correction limitée à `rag_search` → `rag.search` dans la condition du double
  et adaptation du message ;
- GREEN ciblé : **3/3**, code 0 ;
- audit RED/GREEN : **42/42 PASS** ;
- harnais final SHA
  `7DBCA1F038213F0BE12FC31B9EAD372D0108F9DC39D0709BC833C476DAC954E8` ;
- aucune source runtime, fixture, oracle, prompt, option ou budget modifié ;
- aucun appel LLM/live ; serveur Roslyn arrêté après chaque build.

A501 est complet. Ce GREEN valide le banc d'essai, pas V2 live.

### A502 — régressions et préflight quatre gates PASS

Rapport : `phase5/A502-PREFLIGHT-QUATRE-GATES-A503-CORRECTED-HARNESS.md`.

- régressions ciblées : **535/535** ;
- suite client x64 complète : **1 568/1 568** ;
- audit désarmé : **103/103** ; audit final : **52/52** ;
- assembly test SHA `C54DAFDA...75EBA`, DLL WinUI test=produit SHA
  `1C99B202...DF873D` ;
- modèle, llama-server, profil A482, sources runtime et historiques A482/A498
  tous conformes à leurs empreintes ;
- wrapper SHA
  `A93484C2FD6E52142EC120CEA242B5CE928CFA436A355ADA298210887CC26FA2` ;
- une occurrence dotnet, FQN exact, no-build/no-restore ;
- mode `-ArmOnly` PASS, A503 absent, invocation officielle 0 ;
- processus ciblés et variables live 0.

A502 autorise exactement une invocation officielle A503. Ses quatre artefacts
seront définitifs quel que soit le résultat ; aucun replay n'est permis.

### A503-A504 — canari live atteint Qwen mais reste rouge

Rapport :
`phase5/RAPPORT-PALIER-2026-08-30-PHASE5-A500-A504-CANARI-V2-ROUGE.md`.

- A503 exécuté une fois, sans replay, durée TRX 173,8075131 s ;
- cinq cas et cinq appels résolution, 3 123 tokens prompt + 1 081 completion ;
- résultat strict **0/5**, décisions brutes correctes **2/5** ;
- drone research au lieu d'answer ; valve research correcte ; balise research
  correcte ; serre research au lieu de clarify ; rail answer au lieu de context ;
- les cinq JSON sont parseables et terminent `stop`, mais tous posent
  `visibleContextEvidenceId=E1` ;
- les cinq sont rejetés `semantic_resolution_resolution_decision_mismatch` ;
- writer 0, reviewer 0, repair 0, publication 0 ;
- le parser fail-closed bloque notamment l'answer rail incorrect ;
- la boucle writer/reviewer n'est donc pas validée ;
- première cause transversale : sept champs redondants codent la même décision,
  tandis que le prompt n'expose pas la table croisée validée par le code ;
- artefacts A503 figés : JSON `C616E5F3...E6DB81`, JSONL
  `716B9A5D...3EA5A7`, TRX `B363F8C6...52EB8`, log `1522998A...7B363C` ;
- A503 ne sera pas rejoué ; Q106 et WinUI restent interdits ;
- ressources et variables live revenues à zéro.

Verdict : **TESTÉ_NON_APPROUVÉ — 0/5 LIVE, FAIL-CLOSED**. A504 doit auditer ce
résultat, envoyer Telegram et ouvrir un recul architectural sur un contrat de
résolution générique moins redondant, sans correctifs par scénario.

### A504 — audit et notification du palier rouge

- audit pré-Telegram : **137/137 PASS** ;
- message Telegram : 1 628 caractères, 32 lignes, SHA
  `7E741F7AF0946327D8EBC94FD490E39990F591909E496E13A42AAE7CE61EBE9E` ;
- notifier code 0, corps 1 628, total 1 754 caractères ;
- reçu : `phase5/00-RECU-TELEGRAM-A500-A504.md`, SHA
  `697945E23F21808B75C7434C48CD126EA88E4D8AB13B5E87E93A225FBFAEDE7C` ;
- sonde après envoi : **13/13 PASS**, SHA
  `16A1DD130C1570EEA06FAB6EA8B6E1A8BAB06C76759291679CF47DB94E88B2A8` ;
- A503 immuable, processus ciblés et variables live 0.

A500-A504 est clos. Le Goal et la phase 5 restent actifs.

### Ouverture A505-A509 — résolution discriminante minimale

Protocole :
`phase5/PROTOCOLE-A505-A509-RESOLUTION-DISCRIMINANTE-MINIMALE.md`.

- aucun live dans ce palier ; A498/A503 ne sont pas rejoués ;
- A505 fige la forensique des cinq sorties V4 ;
- A506 compare V4, table de vérité, deux appels et discriminant minimal ;
- variante favorite V5-D : `decision`, `evidenceIds`, `reason` ;
- Qwen conserve décision, preuves/ancre et raison sémantiques ;
- le code dérive seulement les alias mécaniquement équivalents ;
- RED avant runtime, GREEN ciblé, régressions et full client obligatoires ;
- writer, reviewer, repair, EvidenceBundle et verifier restent inchangés ;
- aucun hardcode de fixture, Q106 ou WinUI ;
- A509 notifie un verdict déterministe, jamais un PASS live.

A505 est ouvert. Aucune modification runtime n'est autorisée avant la forensique
reproductible et l'audit du protocole.

### A505 — forensique V4 reproductible

Rapport : `phase5/A505-RAPPORT-FORENSIQUE-A503-CONTRAT-V4.md`.

- artefact JSON forensique SHA
  `974573EF3156B7AB99471E6197799CE480E4652A7878E0A1AB0C14A00C7CF9CB` ;
- décisions brutes correctes 2/5, protocole V4 valide 0/5 ;
- sept écarts d'alias : contexte visible 5/5, complet 2/5 ;
- projection V5-D formellement valide 5/5, explicitement non prédictive ;
- score sémantique non requalifié ; answer rail toujours dangereux ;
- audit **43/43 PASS**, runtime inchangé, aucun live.

A505 est complet. A506 doit comparer les quatre variantes préenregistrées.

### A506 — V5-D retenu pour prototype déterministe

Rapport :
`phase5/A506-COMPARAISON-ET-DECISION-CONTRAT-RESOLUTION-V5.md`.

- V4 rejeté : sept champs corrélés, 0/5 protocole A503 ;
- V5-T rejeté : conserve la redondance et dépend du prompt ;
- V5-2 rejeté : deux appels et nouvelle cohérence inter-appels ;
- **V5-D retenu** : `decision`, `evidenceIds`, `reason`, un appel ;
- Qwen garde décision, preuves/ancre et raison ;
- code limité aux enums, visibilité, cardinalité et alias mécaniques ;
- EvidenceBundle, writer, verifier, reviewer et repair inchangés ;
- aucun nouveau flag ou pipeline parallèle ; aucun live ;
- audit **28/28 PASS**.

A506 est complet. A507 doit obtenir RED sur les exigences v5 avant toute
modification runtime, puis implémenter le minimum et obtenir GREEN ciblé.

### A507 — RED/GREEN du contrat V5 minimal

Rapport : `phase5/A507-RAPPORT-RED-GREEN-CONTRAT-V5.md`.

- RED figé avant runtime : **11/31 PASS, 20/31 FAIL** ; compilation réussie,
  échecs comportementaux V4→V5 attendus ;
- V5 exact : `decision`, `evidenceIds`, `reason`,
  `additionalProperties=false` ;
- Qwen conserve décision, sélection des preuves/ancre et raison ;
- code limité à validation de forme, IDs visibles/uniques, cardinalité et
  dérivation des alias mécaniques ;
- answer exige au moins une preuve, context exactement une ancre,
  research/clarify aucune preuve ;
- writer v1, reviewer indépendant, repair, EvidenceBundle et verifier inchangés ;
- trace `derived_alias_source=decision` ;
- GREEN sur la même sélection : **31/31 PASS**, zéro échec ;
- parité DLL WinUI tests/produit exacte, SHA
  `B2663943021BFA9521CF4C19EA70182C41E3018DA10956054CB4CB32444FFC53` ;
- audit de clôture **76/76 PASS** ;
- A498/A503 immuables, aucun live, aucun Q106/WinUI, processus et variables live
  zéro.

A507 est complet avec le verdict **PASS DÉTERMINISTE CIBLÉ — LIVE NON
EXÉCUTÉ**. A508 doit maintenant exécuter les régressions ciblées complètes, la
suite client x64, l’audit statique/binaire final et `git diff --check`. Aucun
live n’est autorisé dans A508.

### A508 — régressions et audit déterministe complet V5

Rapport : `phase5/A508-RAPPORT-REGRESSIONS-ET-AUDIT-V5.md`.

- neuf groupes ciblés historiques A502 plus les trois nouveaux cas V5 :
  **538/538 PASS** ;
- suite client Debug/x64 complète sur la même assembly : **1571/1571 PASS** ;
- delta exact versus A502 : +3 ciblés et +3 complets, aucune gate retirée ;
- deux TRX `Completed`, chacun avec un storage x64 unique ;
- DLL tests SHA
  `E92D37FAFDC455A6DF6A89563D31262EE7D361C6BE8F0AFF0F282E6B1AC65986` ;
- DLL WinUI tests/produit byte-identiques SHA
  `7191EA053B71FCC7D16FFB76D04E460B3DF3F3D510EE6DF06E1116AFF4F4B42E` ;
- schéma trois champs exacts, alias V4 absents du builder, cardinalités
  fail-closed, traces dérivées explicites ;
- V2 toujours faux par défaut et mutuellement exclusif de V0 ;
- EvidenceBundle, writer v1, reviewer, repair et verifier authentifiés et
  inchangés par A507 ;
- audit anti-hardcode/binaire/Git/immutabilité : **115/115 PASS** ;
- A498/A503 inchangés, décisions brutes A503 toujours 2/5 ;
- aucun live, Q106 ou WinUI interactif ; ressources et variables live zéro.

A508 est complet avec le verdict **PASS DÉTERMINISTE COMPLET — LIVE NON
EXÉCUTÉ**. A509 doit figer le verdict de palier, produire le message Telegram,
obtenir un accusé d’envoi et effectuer une dernière sonde post-envoi. Le goal et
la phase 5 restent actifs ; tout futur live V5 exige un nouveau protocole borné.

### A509 — verdict de palier et rapport Telegram

Rapport consolidé :
`phase5/RAPPORT-PALIER-2026-08-30-PHASE5-A505-A509-CONTRAT-V5-DETERMINISTE.md`.

- verdict communiqué : **PASS DÉTERMINISTE — LIVE V5 NON EXÉCUTÉ** ;
- statut produit communiqué : **TESTÉ_NON_APPROUVÉ** ;
- aucune requalification du 2/5 sémantique brut A503 ;
- audit pré-envoi **86/86 PASS** ;
- message Telegram en une partie : corps 2 203 caractères, total 2 317 ;
- message SHA
  `5DBEEA9D8191B0883D73D16BF537F9DD448E5AB38DF1F07A265BBE4CB91B2193` ;
- notifier code 0 et comparaison UTF-8 exacte ;
- reçu `00-RECU-TELEGRAM-A505-A509.md` SHA
  `0E666E6F0027F8E4959501A363F2C5E12420AACA5DD816A7DEF1F7D6A8E9BA18` ;
- sonde post-envoi **44/44 PASS**, sortie SHA
  `96CEC05C7F92ECBDDE297AA868396F50538C0666E5D4C3A8DC9478ABED021E9D` ;
- aucun identifiant Telegram inventé, car le notifier n’en expose pas ;
- A498/A503 non rejoués ; processus ciblés et variables live zéro après envoi.

A505-A509 est clos au niveau **déterministe**. Le goal et la phase 5 restent
actifs. La prochaine action autorisée est la rédaction d’un nouveau protocole
borné avant tout live V5 ; elle doit séparer validité du contrat, décision,
writer, reviewer/publication et performance, sans replay A498/A503 ni activation
produit implicite.

### Ouverture A510-A514 — premier live V5 discriminant

Protocole :
`phase5/PROTOCOLE-A510-A514-PREMIER-LIVE-V5-DISCRIMINANT.md`.

- A498/A503 restent immuables et ne seront pas rejoués ;
- cinq fixtures/oracles A482 identiques, nouveau FQN/flag/répertoire A513 ;
- verdicts séparés : contrat, décision, writer, reviewer/publication, latence ;
- raw V5 trois champs et alias dérivés doivent être tracés séparément ;
- aucun code produit autorisé A510-A514 ; adaptation test-only du harnais ;
- RED/GREEN, ciblés, full x64, parité binaire et wrapper fail-fast avant live ;
- une seule invocation A513 quel que soit son résultat ;
- aucune relance, patch post-observation, Q106, WinUI ou activation produit ;
- performance cible ≤180 s, non approuvée jusqu’à 360 s, FAIL au-delà ;
- A514 doit conserver tous les artefacts et envoyer un Telegram au verdict
  exact.

A510 est ouvert. Aucune inférence n’est autorisée avant l’audit d’ouverture,
l’adaptation RED/GREEN du harnais et le préflight A512 complet.

### A510 — protocole et audit d’ouverture

- protocole de 195 lignes, SHA
  `862492665EC86BA362511414A3D214B03DEFAB476C5B403BE7565B14A36E76FB` ;
- cinq portes et matrice de verdict préenregistrées avant adaptation du harnais ;
- ancien harnais V4, sources produit V5, binaires A508 et historiques A498/A503
  authentifiés ;
- répertoire A513 absent et flag V5 non armé ;
- aucun tarif monétaire, replay, Q106, WinUI ou activation produit ;
- audit d’ouverture **59/59 PASS** ;
- script audit SHA
  `AC62354F22886F4BCA9047FEA5D4B5A9484536CEE5DBE7E5C80DB8FAB5C2C820` ;
- sortie audit SHA
  `45377838BCB36C51F496BE8A818E3406A896D8F707743095EAFDBAF2825703B5` ;
- processus ciblés et variables live zéro.

A510 est complet. A511 peut modifier uniquement le harnais de test pour ajouter
le chemin V5, doit obtenir un RED causal puis un GREEN désarmé, et ne peut lancer
aucune inférence.

### A511 — RED/GREEN du harnais V5

Rapport : `phase5/A511-RAPPORT-RED-GREEN-HARNAIS-LIVE-V5.md`.

- RED causal compilé : **0/4**, quatre alias V5 absents du parseur V4 ;
- profil V4 historique conservé, profil V5 additionnel et désarmé ;
- raw V5 limité aux trois champs, cardinalités et propriété supplémentaire
  refusées dans l’observateur ;
- alias dérivés comparés aux traces produit, pas réinventés comme résultats LLM ;
- artefact V5 enrichi de compteurs contrat, décisions, writer, reviewer,
  publication, tokens et latence ;
- GREEN causal **4/4**, classe harnais complète **15/15** ;
- sources produit et DLL WinUI inchangées ;
- source harnais SHA
  `B74C92A7665CA32A862559DA8E684BA60E789510B9F4E68D4F53A938B60BC2F7` ;
- audit **64/64 PASS**, sortie SHA
  `02155B120CDF761E70B10D3584863918744E43EA68A67FD2E7B4BA160747476C` ;
- répertoire A513 absent, flag live non armé, processus et variables live zéro.

A511 est complet. A512 doit exécuter les régressions et la suite client x64,
figer binaires/modèle/settings, construire le wrapper à invocation unique et
obtenir toutes les portes fail-fast avant tout A513.

### A512 — préflight complet et armement A513

- régressions ciblées Debug/x64 **548/548** ;
- suite client complète **1581/1581** ;
- deux TRX `Completed`, storage x64 unique ;
- DLL tests SHA
  `C2D0FA1D4D97FFB89444500A7DDF7B1CECF63A88969A1B86DDA84923F6996604` ;
- DLL WinUI tests/produit byte-identiques SHA
  `7191EA053B71FCC7D16FFB76D04E460B3DF3F3D510EE6DF06E1116AFF4F4B42E` ;
- modèle, llama-server, settings et profil local authentifiés ;
- wrapper fail-fast : 170 lignes, une occurrence `& dotnet`, FQN exact,
  `--no-build --no-restore`, SHA
  `4482F0B1B206B3AD2B35D53368A1A0505084A15C04EB095BCFFBFF0060F170DC` ;
- `-ArmOnly` code 0, invocation officielle 0, répertoire A513 absent ;
- audit quatre portes **93/93 PASS**, sortie SHA
  `AD5902F5D472BA564C17C0E55C1EA913F69E7AB1D13D2831F175DD2A0B652375` ;
- A498/A503 immuables, processus et variables live zéro.

A512 est complet. Une et une seule invocation officielle A513 est autorisée via
le wrapper figé. Dès l’occurrence `& dotnet`, A513 sera consommé quel que soit
le verdict ; toute relance ou modification post-observation sera interdite.

### A513 — invocation live V5 unique consommée

- wrapper officiel invoqué une seule fois, aucun second lancement ;
- test xUnit : **0/1, Failed**, durée log environ 2 min 48 s ;
- cinq cas exécutés, neuf appels LLM, quatre artefacts finaux conservés ;
- JSON V5 exact et trace protocole : **5/5 PASS** ;
- décisions brutes + EvidenceIds conformes : **4/5** ;
- drone attendu `answer/E3`, observé `research/[]` malgré E1–E3 présentés ;
- writer/reviewer/repair : **0/0/0** ; publication : **0** ;
- aucune fausse publication et aucune publication sans reviewer accept ;
- tokens : 8 002 prompt, 921 completion, 8 923 total ;
- temps LLM 154 130 ms, cas 154 573 ms, fenêtre TRX 170 167 ms ;
- hashes immuables : JSON
  `9CA637F9F5BF7AE2A3B6BEA1A47D5A3CF9EC2863C06E762BD212BD9BAA57FACC`,
  JSONL
  `8B015D01A7CEEDDA041E7390B8774A8C6BB971397FF0AD5F87EE573954EF8289`,
  TRX
  `675A03FA3F217A6E9740786D57D06E461E6102F7EE4514FD898092862E6628FF`,
  log
  `9A18BB6DF6807DCD1CA7596893F973EF2F6B8F155115E884965B5FC8D3E4A876` ;
- processus `llama-server`, `dotnet`, `VBCSCompiler` zéro après le run.

A513 est consommé et **NE DOIT JAMAIS ÊTRE RELANCÉ**. Le FAIL est probant, pas
un défaut d’environnement. A514 doit uniquement auditer, rapporter, actualiser
ce plan et notifier Telegram.

### A514 — audit vertical et verdict du live V5

Artefact forensique :
`phase5/A514-FORENSIQUE-A513-LIVE-V5.json`, 11 204 octets, SHA
`16EFF8F4B2AA8A88E2E46604DA5CD2A54642F0A043398A7F48467A8119245816`.

Rapport :
`phase5/RAPPORT-PALIER-2026-08-30-PHASE5-A510-A514-LIVE-V5-FAIL-SEMANTIQUE.md`.

Verdicts séparés :

| Porte | Verdict A514 |
|---|---|
| P1 contrat V5 | **PASS 5/5** |
| P2 décision + IDs | **FAIL 4/5** |
| P3 writer | **FAIL, non atteint sur le positif** |
| P4 reviewer fonctionnel | **NON OBSERVÉ / NON APPROUVÉ** |
| P4 sécurité publication | **PASS** |
| P5 performance | **PASS, 170 167 ms ≤ 180 000 ms** |
| produit global | **TESTÉ_NON_APPROUVÉ** |

Deux responsabilités génériques sont ouvertes :

- **BUG133** : le juge nomme les valeurs suffisantes du pool drone mais échoue
  à sélectionner le minimum admissible et décide `research` ; le transport est
  innocent sur ce cas (E1–E3 présentés, 0 troncature) ;
- **BUG134** : après une décision `research/context`, le runner peut encore
  planifier une action alors que le budget outil est épuisé ; quatre rejets
  `tool_call_budget_exhausted`, dont deux appels LLM natifs inutiles sur
  valve/beacon.

Écart de mesure conservé : le protocole autorise jusqu’à sept appels/cas et P1
exige un appel V5, tandis que le harnais exige exactement un appel LLM total
pour chaque non-answer. Cela explique deux erreurs xUnit additionnelles sans
transformer P1/P5 en FAIL ni BUG134 en faux positif.

A510–A514 est clos comme **PASS CONTRAT / FAIL SÉMANTIQUE / PASS SÉCURITÉ /
FAIL FONCTION** après audit et Telegram. La phase 5 et le Goal restent actifs.
Le prochain palier doit être préenregistré et déterministe avant tout autre
live : comparaison générique de la résolution sémantique sur pool suffisant,
contrat terminal face aux budgets, TDD BUG134 et stratégie de preuve live du
writer/reviewer. Aucun hardcode des fixtures ou du corpus n’est autorisé.

Clôture Telegram A514 :

- audit pré-envoi **121/121 PASS** ;
- message envoyé en une partie, 2 722 caractères de corps et 2 834 au total ;
- notifier code 0 ; message SHA
  `F4EF14E16EB82C6BFF54CBD5C5206D679DAD0D885A195E949A051833C723C962` ;
- reçu `phase5/00-RECU-TELEGRAM-A510-A514.md` SHA
  `9153F90D40ECF6B299176FE9CD0F1E57E118E74967D64065622AC27C758ECD5C` ;
- sonde post-envoi **25/25 PASS**, sortie SHA
  `93E2897C52637EC405E7CE150224F5AAAE82F4AC2E31C17A55B92C3749BE1199` ;
- aucun identifiant Telegram inventé ; processus ciblés et variables live zéro.

A510–A514 est entièrement clos. Le Goal et la phase 5 restent actifs.

### Ouverture A515–A519 — terminalité budget et recul ordre de décision

Protocole :
`phase5/PROTOCOLE-A515-A519-TERMINALITE-BUDGET-ET-RECUL-ORDRE-DECISION.md`,
168 lignes, SHA
`1931E204BFE0BDFCBF7B19734EB91817F2323AED378C930EB07328829FA84040`.

Le palier sépare strictement :

- BUG133 sémantique, non corrigé : hypothèse future d'ordre
  `reason→evidenceIds→decision`, à comparer live de façon appariée ;
- BUG134 mécanique : aucune planification LLM/outil si la résolution V5 valide
  exige `research/context` mais que le budget outil est déjà épuisé ;
- aucun changement V5, prompt, modèle, fixture, oracle ou live A515–A519 ;
- A513 reste immuable et interdit de relance ;
- code autorisé seulement après RED causal, limité au contrôleur de budget ;
- full client x64, audit anti-hardcode, rapport et Telegram avant clôture.

### A515 — forensique d'ouverture

L'extracteur read-only
`phase5/a515-extract-decision-order-and-budget-forensics.ps1`, SHA
`31DBCD90278B53B1744594038BBDD3BBB417E76D51AB042D4A809328F0DF8A6B`,
relit six comparaisons officielles A442/A452/A462/A472/A482/A513 et les traces
budget A513 sans aucun appel modèle.

Artefact : `phase5/A515-FORENSIQUE-ORDRE-DECISION-ET-BUDGET.json`, 10 859
octets, SHA
`F968CEC72A8878AAF9B8F28EBF8154A30E53436BBAC110125C1C30761D52DBC1`.

Résultats :

- décisions conformes aux oracles historiques : 3/6 ;
- `decision` émise avant `reason` : 6/6 ;
- reason à la limite exacte de 480 caractères : 4/6 ;
- parmi les trois décisions en échec, reason à 480 : 3/3 ;
- l'ordre decision-first existe aussi sur les succès : facteur plausible, pas
  cause unique prouvée ; variante reason-first toujours non testée ;
- A513 : quatre cas avec rejet `tool_call_budget_exhausted`, trois actions
  natives produites par LLM avant rejet et quatre appels `native_other` sur les
  cas rejetés ;
- scope retenu : corriger BUG134 seulement, préserver BUG133 pour expérience
  future préenregistrée.

A515 est complet : audit d'ouverture **59/59 PASS** ; script SHA
`9F09F23DDC88A30C0768CB404EB75604B82C84BC593D3AA110E226EFF32EA266`,
sortie SHA
`CCCEF6912A34D2E4FC11ABC9B9A9137064997F23D50AB0901EEE27A94D33FA21`.
Sources produit, ADR, historiques A442–A513, sceau A514, branche/HEAD,
`git diff --check`, processus et variables live sont conformes.

### A516 — rapport causal et décision de scope

Rapport : `phase5/A516-RAPPORT-CAUSAL-BUG133-BUG134-ET-SCOPE.md`, 148 lignes,
SHA `6F0FE58EED57096AAF61A0754F3AA7D3E8E64AE218E8DB528466089739E17CE2`.

- BUG133 n'est pas une perte de preuves : le pool drone est complet ;
- A472/A482/A513 exposent le résultat comparatif utile dans une raison écrite
  après l'engagement de la décision ;
- l'ordre decision-first est un facteur plausible mais non causalement prouvé,
  puisqu'il existe aussi sur les succès ;
- aucun patch prompt/contrat n'est autorisé ; variante reason-first transférée à
  un futur canari apparié multi-domaines avec contrôle des faux answers ;
- BUG134 est mécanique : décision/capacité/budget sont déjà connus et une action
  supplémentaire ne peut pas devenir exécutable ;
- correctif borné au chemin V5 valide `research/context` avec budget épuisé ;
- `answer`, `clarify`, budget restant, V0, EvidenceBundle, writer/reviewer et
  vérificateur doivent rester inchangés ;
- phase 5 et produit restent `TESTÉ_NON_APPROUVÉ`.

A516 est complet. A517 est autorisé à ajouter uniquement les tests RED
préenregistrés ; le code produit reste gelé jusqu'au RED causal.

### A517 — RED causal de la terminalité budget

Trois scénarios ont été ajoutés au harnais V5, sans changement produit :

- RED research : budget outil 1/1 consommé, continuation sémantique disponible ;
- RED context/E1 : budget outil 1/1 consommé, action directe impossible ;
- contrôle research : budget outil 1/2, continuation native toujours permise.

Commande Debug/x64 avec compilation réelle : **3 total, 1 PASS, 2 FAIL**.
Les deux échecs sont exactement causaux :

- research effectue encore **2 appels LLM** au lieu d'un ;
- context émet encore un rejet
  `tool_call_budget_exhausted` après avoir programmé `documents_context` ;
- le contrôle à budget restant est vert avec deux appels LLM et deux
  `rag.search` exécutés.

Preuve scellée :
`artifacts/goal-rag-product-20260827-1041/phase5/A517-RED-TERMINALITE-BUDGET.trx`,
3 tests, SHA
`C8DECCA33E5F8B41B215974F24F1400AA6199BC6E7DF9510570B5BA6AD1EDB6F`.
Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A517-RAPPORT-RED-CAUSAL-BUG134.md`.

Le runner produit est resté byte-identique au SHA
`39D075CBBAAE07F2B208499E7F3A3F8602B785B279CF2653965D457D34BDD4A9`.
A517 est **complet et probant**. A518 est autorisé uniquement pour le garde
mécanique préenregistré et sa trace ; BUG133, prompt, contrat V5, writer,
reviewer, EvidenceBundle, fixtures et live restent gelés.

### A518 — GREEN déterministe BUG134

Le garde mécanique est implémenté uniquement pour une résolution V5 valide qui
demande `research` ou `documents_context` alors que
`executedToolCalls >= MaximumToolCalls`. Il ne remplace aucune décision Qwen et
émet l'événement préenregistré
`source_backed_agent_v2.semantic_resolution.continuation_blocked` avec capacité,
compteurs, motif et source de décision complets.

Sources finales :

- runner : 2 500 lignes, SHA
  `FAA78D5CC7DB08827BE3DE8C60D4CF7FB5FCCF67D3C81E8B5C0120792A436834` ;
- module budget : 44 lignes, SHA
  `74FF4C020D84411A0E34E8D9B8E0D88B3C14D79524A977783998835B9B2E2F14` ;
- tests : 762 lignes, SHA
  `54B82BEB3A8C58AEB704FDF631E7A162F7C5D94CB428750568D5168DE7E3BEF9`.

Validation finale sur le même binaire Debug/x64 :

- garde + modularité : **4/4 PASS** ;
- neuf groupes source-backed : **551/551 PASS** ;
- suite client complète : **1584/1584 PASS** ;
- DLL WinUI tests/produit byte-identiques, SHA `D4BB4AA…` ;
- audit de clôture : **82/82 PASS** ;
- A513 immuable, live non rejoué, variables live et processus ciblés zéro.

L'échec intermédiaire **550/551** est tracé : le code inline avait dépassé la
limite de 2 500 lignes du runner. L'extraction modulaire a restauré la gate sans
modifier le comportement, puis les campagnes finales ont passé.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A518-RAPPORT-GREEN-TERMINALITE-BUDGET-BUG134.md`.

Verdict : **BUG134 PASS DÉTERMINISTE**. BUG133 reste **FAIL LIVE / NON CORRIGÉ**,
l'ordre reason-first reste non mesuré, le writer/reviewer positif reste non
observé et le produit global demeure `TESTÉ_NON_APPROUVÉ`. A519 est autorisé
uniquement pour rapport dynamique, Telegram, reçu et arrêt des ressources.

### A519 — clôture documentaire et Telegram (pré-envoi)

Rapport consolidé créé :
`artifacts/goal-rag-product-20260827-1041/phase5/RAPPORT-PALIER-2026-08-30-PHASE5-A515-A519-BUG134-TERMINALITE-BUDGET.md`.

Message Telegram préparé :
`artifacts/goal-rag-product-20260827-1041/phase5/telegram-palier-a515-a519-bug134-terminalite-budget-20260830.txt`.

Le message distingue explicitement le PASS déterministe BUG134 du FAIL live
BUG133, mentionne le RED, l'échec de modularité intermédiaire, les campagnes
finales, A513 non rejoué, les limites et la prochaine expérience appariée.

État avant envoi : rapport et message créés, envoi **NON ENCORE EFFECTUÉ**.
Une gate pré-envoi doit authentifier leur contenu, le plan, A515–A518, A513 et
l'arrêt des ressources avant l'unique appel notifier avec
`-MessageFile -RequireMessage`.

État final A519 :

- audit pré-envoi : **40/40 PASS** ;
- Telegram envoyé en une partie le 2026-08-30 à 07:31:33 +02:00 ;
- notifier : `Corps=2242`, `Total=2366`, code 0 ;
- message SHA
  `D3A5FEB8BE5955241EF74EAA028398C31CE2ADD3281660B899558C2246881603` ;
- reçu `artifacts/goal-rag-product-20260827-1041/phase5/00-RECU-TELEGRAM-A515-A519.md`,
  SHA `7970B4F86AE96267F66120FE32118A1730CC2191683A04E676856CD5624E8FF0` ;
- sonde post-envoi : **27/27 PASS**, script SHA
  `6D861E0DBC00DE77B45EDB4FC76DDB6EE0F51FDC523107083ACEF5D6A1E1FFA7`,
  sortie SHA
  `2898C40B05E1A9E303447A49DFCD2EE0312EC9B4ABC499C6CE14B2E9E7A08740` ;
- aucun identifiant Telegram inventé ;
- A513 non rejoué ; ressources ciblées et variables live zéro après l'envoi.

A515–A519 est **clos** : BUG134 PASS déterministe, BUG133 FAIL live non
corrigé, produit global `TESTÉ_NON_APPROUVÉ`. Le Goal et la phase 5 restent
actifs. Le prochain palier doit être un protocole apparié multi-domaines sur
BUG133 avant tout live ; aucune invocation n'est autorisée par cette clôture.

### Ouverture A520–A525 — expérience appariée ordre V5

Le Goal persistant a été relu intégralement avant ouverture : 21 lignes,
12 076 octets, SHA
`6BAE663308460C8F68B0CCEEF3420E8BD30DEDB19B987F9207130DCEF35E63CD`.
Il confirme que Qwen reste le décideur sémantique et qu'une variante liée à son
comportement doit être prouvée live sans devenir une règle métier codée.

Protocole :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A520-A525-EXPERIENCE-APPARIEE-ORDRE-RESOLUTION-V5.md`.

Deux bras strictement appariés sont préenregistrés :

- A : contrat courant `decision→evidenceIds→reason` ;
- B : mêmes trois propriétés et sous-schémas,
  `reason→evidenceIds→decision`.

Le nom de contrat, le prompt, les pools, le modèle, la température, les budgets,
le runner, le writer et le reviewer restent identiques. La variante existe
uniquement dans un adaptateur de harnais ; aucun flag ni changement produit
n'est autorisé. Les cinq cas multi-domaines figés couvrent comparatif suffisant,
exclusivité globale non prouvée, valeur absente, clarification et ancre de
contexte.

A520 interdit tout live. A521 ne peut commencer qu'après un audit d'ouverture
authentifiant le Goal, l'ADR, le sceau A519, les historiques A513, les sources
et l'arrêt des ressources.

### A520 — protocole et audit d'ouverture complets

Le protocole apparié est figé : 233 lignes, SHA
`6CD277ED890DFCE5D104E2939F45611602CDBDD9E918DCC0E36411B3260DD823`.
Il préenregistre les deux ordres, l'unique delta, les cinq cas, l'alternance des
bras, les artefacts, les portes P0–P5, les critères d'interprétation et
l'interdiction de promotion après une seule paire.

Audit d'ouverture : **56/56 PASS**.

- Goal et ADR authentifiés ;
- sceau A519 **37/37** et reçu Telegram authentifiés ;
- contrat V5 courant confirmé decision-first dans `properties` et `required` ;
- contrat, résolution, tracing, options, runner, module BUG134 et ancien harnais
  A513 inchangés ;
- quatre artefacts officiels A513 immuables ;
- répertoire live A524 absent ;
- branche/HEAD, `git diff --check`, worktree préservé ;
- variables live et processus ciblés zéro.

Script audit : SHA
`40F2D795450314EA62FA41FBBEECF9ED430CEBD30300AD91B66FB3DCF8894FC9` ;
sortie : 57 lignes, SHA
`139BE6893EAF5BE5392CB3E8955A04948BD06F773ED528CBAB223E883060DADD`.

A520 est complet. A521 est autorisé à ajouter uniquement l'adaptateur
expérimental côté tests et les tests déterministes prouvant que seul l'ordre du
schéma change. Le runtime produit, le prompt, les oracles et tout live restent
gelés.

### A521 — adaptateur d'ordre isolé et validé

Deux sources limitées au projet de tests matérialisent l'unique variable :

- utilitaire : 86 lignes, SHA
  `838043FB36B79D8C3C2BAF72D28CEC30E0B232DE1E35414A33CCA0F502B7E60A` ;
- tests : 170 lignes, SHA
  `EA956911A965146B06F7EAC4418A7EE035B87C86931CA5A7E2562E872BF6E5F3`.

Le bras contrôle retourne le contrat original. Le bras reason-first conserve
nom, racine, sous-schémas et pool d'IDs, et ne réordonne que `properties` et
`required`. Tout contrat non V5, dont writer/reviewer, est transmis par identité.
Une dérive de forme V5 échoue fermée.

Résultats :

- utilitaire : **7/7 PASS** ;
- périmètre SemanticResolution : **51/51 PASS** ;
- audit : **55/55 PASS** ;
- runtime produit sans aucune occurrence de l'expérience ;
- contrat/résolution/tracing/runner/BUG134 byte-identiques ;
- live A524 absent, variables et processus zéro.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A521-RAPPORT-ADAPTATEUR-ORDRE-V5-DETERMINISTE.md`.

A521 est complet. A522 est autorisé à construire uniquement le harnais apparié,
les cas figés, l'enregistrement et l'évaluation déterministe. Le live reste
interdit jusqu'au préflight A523.

### A522 — harnais apparié figé hors live

Nouveau harnais : 1 606 lignes, SHA
`A401082791521F4859F856EA7C87C7B844BB44C11AC5C539024BAF4D6FAA7543`.

Il préserve la même intake et le même identifiant d'action entre bras, alterne
les dix exécutions, compare prompts/pools/hash invariant, exige deux hashes de
schéma distincts et enregistre prompts, contrats, JSON, traces, tokens, cache et
temps. Les erreurs contrôle restent observées ; intégrité, sécurité et erreurs
reason-first bloquent. Les verdicts appariés sont figés avant observation.

Gates déterministes :

- harnais seul : **13/13 PASS** ;
- adaptateur + harnais : **20/20 PASS** ;
- audit : **70/70 PASS** ;
- cinq cas identiques à A482 ;
- runtime produit et harnais A513 inchangés ;
- live A524 absent et désarmé ;
- processus et variables live zéro.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A522-RAPPORT-HARNAIS-APPARIE-ORDRE-V5.md`.

A522 est complet. A523 est autorisé pour les régressions complètes, la parité,
l'audit et le wrapper fail-fast. Aucune invocation live n'est encore autorisée.

### A523 — préflight complet et invocation A524 armée

Les deux campagnes x64 requises sont vertes sur les binaires ensuite scellés :

- neuf groupes source-backed et les vingt tests expérimentaux : **571/571
  PASS**, échecs/ignorés zéro, TRX SHA
  `174A184D35864F55154826D9FCC72187E2D01688727999D321D70DAEEEEC415D` ;
- suite client complète : **1604/1604 PASS**, échecs/ignorés zéro, TRX SHA
  `5095E7C03CC92EB5999E376C13190894A1409C34CCFC2ADE14092E24CC1F0118`.

Les deux TRX utilisent un stockage x64 unique. La DLL WinUI chargée par les
tests est byte-identique à la DLL produit, SHA
`D4BB4AA46C868C0F0A42CF51790FE4FF31A00A673CC7AAE52A9243687123A15E`.

Le wrapper fail-fast A524 compte 210 lignes et exactement une occurrence
`& dotnet`. Il utilise le FQN figé, x64, `--no-build`, `--no-restore`, refuse
toute dérive des inputs et conserve JSON, JSONL, TRX et log même si le test
échoue. SHA du wrapper :
`73545B95184377D886BEA9AC27C896879E002F9E04C0CB2BF9534647122E964B`.

Le mode `-ArmOnly` est PASS avec compteur officiel **0**, A513 interdit de
rejeu, dix runs attendus, plafond 360 000 ms, répertoire A524 absent et aucune
invocation `dotnet`. Sortie SHA
`F2E04EF0211713A6F6F63CABD8962ED1A48D016858BD9E827AC1FF4E467FABB6`.

Audit du préflight : **94/94 PASS**.

- script : 191 lignes, SHA
  `23C774E4A1353177D4BE84F41915BAD629AFC822A5A1DC35C3AE0405B4E6ACE6` ;
- sortie : 95 lignes, SHA
  `445A56992E1C78E0935E26FD532AE152A9D8DD468E31000ADB55003FD144D07C` ;
- branche `SAAIA_V3.1`, HEAD
  `5f35881cdc67d12a076fcd2a7a1004656ac9a37a`, `git diff --check` code 0 ;
- modèle, serveur, settings, sources, DLL, protocole et quatre artefacts A513
  authentifiés ;
- répertoire live absent, variables live zéro, processus ciblés zéro.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A523-PREFLIGHT-A524-PAIRED-ORDER.md`,
122 lignes, SHA
`1D1B7135AA3CBF8A3F3B3B4950A474C87E1DC13DC7A6FF960057B93199247001`.

A523 est **complet**. Le comportement reason-first n'a encore jamais été testé
live : BUG133 reste FAIL LIVE sur A513, BUG134 reste PASS déterministe et le
produit `TESTÉ_NON_APPROUVÉ`. L'unique exécution A524 est désormais autorisée.
Dès l'unique `& dotnet`, elle est consommée sans relance possible, succès ou
échec ; A525 devra analyser les artefacts intacts avant toute décision.

### A524 — live apparié consommé, reason-first rejeté

L'unique wrapper officiel a atteint son seul `& dotnet` le 2026-08-30. A524 est
donc consommé définitivement et n'a pas été relancé. Le TRX couvre 08:07:58 à
08:12:05 +02:00, dix runs, cinq paires et un seul test x64.

Verdict consolidé :

- contrôle `decision-first` : **4/5** décisions + IDs exacts ;
- variante `reason-first` : **3/5** ;
- fausse publication : 0 ;
- verdict préenregistré : `reason_first_rejected_accuracy` ;
- xUnit : 1 exécuté, 1 FAIL fonctionnel, aucune panne infrastructure/protocole ;
- campagne : 246 052 ms ≤ 360 000 ms.

Sur le drone, B passe de `research/[]` à `answer/E2,E3`, mais l'attendu est
`answer/E3`. Le writer élargit à `E1,E2,E3`, le reviewer décide `revise` et
aucune réponse n'est publiée. Sur l'ancre rail, A réussit `context/E1` tandis
que B régresse à `research/[]`. Les trois autres paires sont exactes sous les
deux ordres.

Les cinq paires ont question, pool, prompt et invariant de contrat identiques ;
seuls les ordres et hashes de schéma diffèrent. Intégrité 0, sécurité 0,
protocole valide 10/10. La causalité du test est donc préservée, sans prétendre
qu'une campagne locale caractérise tous les comportements du modèle.

Les quatre artefacts A524 sont figés :

- JSON SHA `4A1306182B1FCB250A3E3983AA278F803C2CDD60A3D3E5BE3434E3574BAB3BFB` ;
- JSONL SHA `387889D5E88FF0D2B8DF2E183CB844549038E0A537C0301F9A10EB6ECFF94B96` ;
- TRX SHA `2BB9CBB0E5FA46A9389B330E458DF450704E10075790D2E1D8781BB257862E40` ;
- log SHA `B5E79EECFD30E7FF1234517146856FA71BC44BC7C43579DE61893467187B4410`.

### A525 — forensique complète, clôture Telegram en cours

Audit final des artefacts : **289/289 PASS**.

- script : 340 lignes, SHA
  `C09ABE557CA6A8FB91EB88797DFA256E4410741A6EB399B1A55034EB427967B6` ;
- sortie : 290 lignes, SHA
  `4963D55F21731EECA1E6520F5C755818605403A53A79FE2E2FD34B8C3576EEEB` ;
- douze appels LLM : dix résolutions, un writer, un reviewer ;
- 10 805 tokens, 231 520 ms LLM ;
- BUG134 : sept terminalités live exactes, deux `clarify` directs, zéro
  `native_other`, zéro rejet budget ;
- variables live et processus ciblés zéro ; A513 immuable et non rejoué.

Rapport consolidé pré-envoi :
`artifacts/goal-rag-product-20260827-1041/phase5/RAPPORT-PALIER-2026-08-30-PHASE5-A520-A525-ORDRE-V5-REJETE.md`,
219 lignes, SHA
`1EBE614A190581012B829C7696D0CB53F0CDEA56C4389795ABFF96E6AFC72DC3`.

Décision : reason-first est rejeté et ne sera pas activé dans le runtime. BUG134
est maintenant PASS LIVE sur cette campagne ; BUG133 reste ouvert de bout en
bout. Produit `TESTÉ_NON_APPROUVÉ`, Goal et phase 5 actifs.

Telegram A525 envoyé le 2026-08-30 à 08:21:07 +02:00 avec
`-MessageFile -RequireMessage` :

- audit pré-envoi **55/55 PASS** ;
- corps 2 394 caractères, total 2 508, une partie, code 0 ;
- message SHA
  `4A8CEE60ABC44EB1A728843A5EC01898AD565DFB423638F62DE2EB190108C4F7` ;
- reçu `artifacts/goal-rag-product-20260827-1041/phase5/00-RECU-TELEGRAM-A520-A525.md`,
  SHA `E6C28AD6E5011CF22A703B1DEB137DBFB5D16F3CB5C2B7948ED7917B7273ED7D` ;
- texte UTF-8 retourné vérifié ordinalement ; aucun identifiant inventé ;
- A524 non rejoué, ressources et variables live zéro.

Sonde post-envoi : **51/51 PASS**.

- script : 143 lignes, SHA
  `76FF6376883738D16C4AA9AAE30912BA6B4129443687A11D6A5C66A596C16566` ;
- sortie : 52 lignes, SHA
  `F4CA77D9910B41FD61D13435A38E88CE04A069E9E51C236F9B59F83964887791` ;
- message, reçu, rapport, plan, artefacts A524, verdict, Git et ressources
  réauthentifiés.

A520–A525 est **clos** : reason-first rejeté, BUG134 PASS LIVE sur cette
campagne, BUG133 toujours ouvert et produit `TESTÉ_NON_APPROUVÉ`. Le prochain
palier doit prendre du recul sur le contrat entre sélection sémantique,
EvidenceBundle, writer et reviewer, sans nouvelle permutation locale ni live
immédiat.

### A526 — matrice causale du handoff juge → writer

La forensique A524 et le code convergent sur une coupure d'état :

- reason-first décide `answer/E2,E3` et son `reason` identifie correctement
  Cygnus, 36 minutes contre Brio, 58 ;
- le parseur conserve ce texte dans `FastEvidenceReview.Assessment` ;
- `CompleteFastEvidenceWriterAsync` reçoit `review` mais transmet au writer
  uniquement intake, bundle, IDs et mode atomique ; `review.Assessment` disparaît ;
- le marqueur `SELECTION_LLM_AUTORISEE` est volontairement neutralisé comme
  instruction avant construction du prompt writer ;
- l'ADR exige pourtant que le writer reçoive les décisions de l'Evidence Judge.

Une seconde rupture mécanique est confirmée : `E2,E3`, co-localisés sur la
même source/page/requête, sont regroupés en une unité puis élargis à toute la
fenêtre `E1,E2,E3`. Le prompt annonce donc une preuve sélectionnée mais autorise
les trois, et le writer produit trois claims. Le reviewer `revise` rejette E1,
préfère E3 et bloque honnêtement la publication.

L'oracle A524 `E3` seul doit aussi être requalifié : il prouve les attributs de
Cygnus mais pas, seul, le superlatif face à Brio. `E2,E3` est un ensemble de
support comparatif défendable. Le verdict historique A524 n'est pas réécrit ;
les prochains gates distingueront focus de sortie, support et fenêtre.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A526-MATRICE-CAUSALE-HANDOFF-RESOLUTION-WRITER.md`,
219 lignes, SHA
`BD87F8A944880FE6453452DF96371D1BAEF3ED4069D1F26E09987841FFFB573D`.

Audit causal : **76/76 PASS**.

- script SHA
  `327621501590DA7A96E63A1720840E61DA1DB486F8E55A2EDBFC9D005E6E99E9` ;
- sortie SHA
  `15725F2D0616099BC874608661BBA339C6555A164F96940E488C9E005DFA2198` ;
- aucun changement produit, aucun appel modèle, variables/processus zéro.

A526 est **complet**. A527 doit préenregistrer un changement unique : transporter
mécaniquement décision, assessment et IDs explicites du juge jusqu'au writer,
sans modifier l'ordre/contrat V5, sans interprétation métier, sans suppression
immédiate de l'expansion de fenêtre et sans live.

### A527 — protocole du handoff sémantique figé

Protocole :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A527-A531-HANDOFF-SEMANTIQUE-JUGE-WRITER.md`,
245 lignes, SHA
`3E396428A09EE2E1573FA0513EED3BD98E4906ED05333D6A8ED8C8A49D14CED5`.

Le changement autorisé est limité au transport vers le writer de :

- l'alias mécanique `decision=answer` du chemin V5 Ready ;
- `FastEvidenceReview.Assessment` exact et borné seulement en longueur ;
- la liste ordonnée des EvidenceIds sélectionnés par le juge ;
- une distinction explicite entre sélection sémantique et fenêtre citable.

Interdictions : aucun changement d'ordre/contrat V5, aucun second appel de
résolution, aucun hardcode métier, aucune interprétation du reason par le code,
aucun changement immédiat de l'expansion de fenêtre, aucun reviewer contourné,
aucun oracle A513/A524 réécrit et aucun live avant A530 PASS.

Audit d'ouverture : **74/74 PASS**.

- Goal, ADR et A526 authentifiés ;
- onze sources runtime et le test V5 courant figés ;
- ordre `decision,evidenceIds,reason` confirmé dans properties et required ;
- handoff et tests RED confirmés absents avant A528 ;
- A513/A524 immuables, répertoire A531 absent ;
- branche/HEAD, `git diff --check`, variables et processus conformes.

Script : 194 lignes, SHA
`26C2AA6537DA800D6F82E5AADB067F35DC52526917A9CDB7BD3BB2F34A99FF86` ;
sortie : 75 lignes, SHA
`994AC6BE2631F50FA28E270707F71DD7252C552D6F206C9A0EABA11AEAAF088A`.

A527 est **complet**. A528 est autorisé à ajouter uniquement les tests RED
causaux préenregistrés. Le code produit et tout live restent interdits.

### A528 — RED causal du handoff authentifié

Trois tests x64 ont été ajoutés sans toucher au runtime.

Premier run conservé mais invalide : **0 PASS / 3 FAIL**. Les deux FAIL causaux
étaient exacts ; le contrôle `research` a toutefois tenté une continuation avec
son budget par défaut et épuisé la file scriptée. TRX SHA
`9C87D2FE38A635309A16DBE6FB5DCA3EF27D08A28660710C255034D30D6C607D`.

Après avoir borné uniquement le contrôle à `MaximumToolCalls=1`, le RED valide
donne exactement **1 PASS / 2 FAIL** :

- PASS : un non-answer n'émet aucun handoff ;
- FAIL causal : `HANDOFF_SEMANTIQUE_DU_JUGE` absent du prompt writer ;
- FAIL causal : événement `semantic_resolution.writer_handoff.completed`
  absent ;
- compilation x64 réussie, aucun autre motif.

TRX RED valide SHA
`333B8EE527D552D950C2C8A1B98C7874225F7C4B753B063AB570EBD4229D7D9C`.
Source tests : 842 lignes, SHA
`8EBA1C7EE9A5593BFB10849694D704DCB3935261854DC8614E6D99855ED603AC`.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A528-RAPPORT-RED-CAUSAL-HANDOFF.md`.

Audit RED : **69/69 PASS**.

- script SHA
  `7439D8A0B2B4F79E1A6D8CF8B54C456B0AB3479AE013422C8A23EFDD95F75078` ;
- sortie SHA
  `4C2CB76819C691FCCF68EFD40E895CD635B57C1A642D92747D0B00107D7EC5B7` ;
- onze sources runtime inchangées, Git conforme, ressources zéro.

A528 est **complet**. A529 peut implémenter uniquement le handoff et la trace
préenregistrés, sans modifier les attentes RED, le contrat V5, la fenêtre, le
reviewer ou tout live.

### A529 — GREEN minimal du handoff juge → writer

Le transport préenregistré est implémenté dans quatre coutures génériques :

- `CompleteDedicatedWriterAsync` accepte et transmet l'Assessment ;
- le writer structuré le propage au builder de messages ;
- le prompt distingue décision, IDs sélectionnés par le juge, raison et fenêtre
  contextuelle citable ;
- le chemin V5 `answer` émet une trace dédiée de handoff puis transmet
  `review.Assessment` au writer.

Le code ne choisit, ne réordonne et n'interprète ni les IDs ni la raison. Le
contrat V5, l'expansion de fenêtre, le verifier, le reviewer, la réparation et
les budgets restent gelés.

GREEN causal x64 : **3/3 PASS**, TRX SHA
`7D5D82465B9C95877C51072E5BFA77083EF670B57E6C849F748132C3C8F6CF32`.

Régression complète de la couture semantic-resolution/writer-review x64 :
**23/23 PASS**, TRX SHA
`2529ABC15566AD9A9A7F06857DC4F04C7DF3B03D70304521BA7C13F6B853B096`.

Le contrôle legacy vérifie explicitement l'absence du nouveau marqueur lorsque
le flag V5 est désactivé. Les quatre sources runtime ne contiennent aucun terme
de fixture ou de domaine interdit.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A529-RAPPORT-GREEN-HANDOFF-SEMANTIQUE.md`,
200 lignes, SHA
`052F688B7D815A624CD9E565CABC9E55E8827112B4462988A7741D45168640F4`.

Audit GREEN : **116/116 PASS**.

- script : 249 lignes, SHA
  `C0E0AC51CD09B59055C02B2943063775A97FC91EB26F9F36EF192F6FF29331D9` ;
- sortie : 117 lignes, SHA
  `AB19A40A29FB8CCD9BF967CD684F471073A80F49B4220615D5EF47EB98F94E47` ;
- A513/A524 immuables ;
- branche/HEAD et `git diff --check` conformes ;
- processus et variables live : zéro ;
- aucun appel au modèle local et aucun replay A524.

Les essais préliminaires sans tests ou sans cible x64 valide sont explicitement
documentés et non comptés. Les preuves finales chargent l'assembly sous
`bin/x64/Debug`.

A529 est **complet**. A530 doit maintenant exécuter les groupes source-backed
historiques, la suite client x64 complète, la parité binaire et l'audit final,
puis décider explicitement si un live A531 apporte encore une information
nouvelle. Le produit reste `TESTÉ_NON_APPROUVÉ` et BUG133 reste ouvert.

### A530 — régressions complètes, confinement legacy et décision A531

La première régression comparable à A523 a exécuté les onze classes attendues :
**566/574 PASS, 8 FAIL**. Le TRX rouge est conservé, SHA
`9BD815E2A27966AF27CDF26F815BDC300B994AA9619F277F59FCAE45D31109DE`.

Les causes sont distinctes :

- sept helpers de réflexion appelaient les méthodes privées avec l'ancienne
  arité ; le paramètre optionnel C# doit être fourni explicitement à
  `MethodInfo.Invoke` ;
- un test legacy révélait une fuite de confinement réelle : la trace était sous
  gate V5, mais `review.Assessment` était transmis au writer hors du `if`, ce
  qui ajoutait le handoff à un ancien chemin lorsque son Assessment était non
  vide.

La correction produit A530 est strictement mécanique :
`SemanticResolutionWriterReviewAttempted ? review.Assessment : null`.
Le fichier de tracing final fait 118 lignes, SHA
`5F22A410081BBEA72D8EEC3C3DB8CFBFA3A2E10D13D1FEA3D7E8069277004C74`.

Les helpers de réflexion ont reçu leur argument `null` explicite et le test
legacy affirme désormais directement l'absence de
`HANDOFF_SEMANTIQUE_DU_JUGE:` avec un Assessment ancien non vide.

Gates finaux, tous sur le même binaire x64 :

- huit échecs rejoués : **8/8 PASS**, SHA TRX
  `F7CB0AD370338C251D6B055947CFDEF8BF20F01CEF2363913B07B5E501FC9F4F` ;
- couture sémantique : **23/23 PASS**, SHA TRX
  `33457E1F0C866618A598D6D9A254F45B36A77B592EBE47442137BEC9009E48DF` ;
- groupes historiques/expérimentaux : **574/574 PASS**, SHA TRX
  `6FBBD9AAB7F77A861D4A38AFC4BF597A18381054FCE395E41CC4CAA8CC963774` ;
- suite client complète : **1 607/1 607 PASS**, SHA TRX
  `E256BE5C4ADC99327F7D1B851743548555D8DC6B247500F22B0D3882FD6FCA71` ;
- DLL WinUI tests/produit byte-identiques, SHA
  `431D575AA6672697872B9AE74E81240A06400ED1AAD860A8E4364F2BF3BDC9FC` ;
- contrat V5, fenêtre, verifier, reviewer, runner et budget inchangés ;
- A513/A524 immuables ; Git conforme ; ressources/variables zéro.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A530-RAPPORT-REGRESSIONS-AUDIT-DECISION-LIVE.md`,
218 lignes, SHA
`3DDF24F35BDFEDD0BAF0069676FBE6D624AC07CC161E690BF111810EC068EC8F`.

Audit A530 : **162/162 PASS**.

- script : 286 lignes, SHA
  `62773029DE62B834CA440AF255BA9C79D09FBEFBA3001B220F198E6B91E7BD19` ;
- sortie : 163 lignes, SHA
  `E4821C6E0B26BC12B07FF10ECABAE466D2C70057335DE2A3858CCA2A30078844`.

Décision : un live A531 apporte une information nouvelle sur le comportement du
vrai Qwen face au handoff et est **conditionnellement autorisable**. Il reste
interdit avant création, scellement et audit d'un wrapper séparé à invocation
unique. Il couvrira exactement un fait direct, un comparatif focus/support et
une ancre contextuelle sans writer/reviewer. Même positif, il ne pourra pas
approuver le produit : BUG133 reste ouvert et le statut global reste
`TESTÉ_NON_APPROUVÉ`.

A530 est **complet**. État Telegram avant envoi : message A526–A530 à créer et
envoi **NON ENCORE EFFECTUÉ**. A531 ne commencera qu'après communication de ce
palier et préflight séparé.

#### Notification Telegram A526–A530

Envoi effectué le **2026-08-30 à 09:15:20 +02:00**, en une seule partie, via le
notifier officiel :

- titre : `SAAIA RAG - A526-A530 handoff juge writer valide` ;
- corps : 2 219 caractères ; total : 2 344 caractères ;
- code de sortie : 0 ;
- message SHA
  `9A91EB1717A553DE115C6A3A8C548E20FF01751118DBD0F1357705200C8EC3BA` ;
- audit pré-envoi : **43/43 PASS** ;
- reçu `00-RECU-TELEGRAM-A526-A530.md` : 48 lignes, SHA
  `1D872E1A2864DAA1D172B2EA1FB50BAFA3407BF45D834999797D31745633DC13` ;
- processus ciblés et variables live après envoi : zéro ;
- répertoire A531 encore absent ; A513/A524 non rejoués.

A530 est **clos et communiqué**. A531 peut désormais créer exclusivement son
harnais trois transactions et son wrapper séparé, puis les auditer en mode
désarmé avant toute invocation live.

### A531 — live unique du handoff juge → writer (consommé)

Le protocole indépendant A531 est figé avant le harnais :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A531-LIVE-HANDOFF-TROIS-TRANSACTIONS.md`,
204 lignes, SHA
`8D278B99DABC096882934A7F7D92EF02FAF35D886D605CB182F9E611B75C22A2`.

Il préenregistre exactement trois transactions :

- `pump_torque_direct` : réponse directe, sélection/citation `E1` ;
- `survey_drone_focus_support` : sélection ordonnée `E2,E3`, focus
  Cygnus/36 et support Brio/58, E1 interdit dans la réponse ;
- `rail_wear_anchor_context` : ancre `E1`, capacité `documents_context`, zéro
  writer/reviewer/handoff/publication.

L'audit d'ouverture passe **59/59**. Le harnais test-only A531 ajoute :

- le jeu de trois fixtures sans modifier les fixtures historiques A482 ;
- les gates exactes de trace et de prompt du handoff ;
- le prompt réellement visible par le modèle, sa longueur et son SHA-256 dans
  chaque enregistrement JSONL ;
- les budgets préenregistrés : 7–11 appels, 12 000 tokens, 360 s maximum ;
- l'interdiction d'un handoff ou writer sur la décision `context`.

Gates désarmés sur le même binaire x64 :

- classe du harnais : **18/18 PASS**, TRX SHA
  `962887C775F072EBD2E117C894E24A9C2DA7D7D28BBE70EFE6F23B5974010371` ;
- périmètre historique comparable A530 : **577/577 PASS**, TRX SHA
  `E1A6E7BA41701CA3BD7A4C162F2CD584DA6C7B58983DD43AD9F502005FAE4063` ;
- suite client x64 complète : **1 610/1 610 PASS**, TRX SHA
  `C8BC217A201854341B3A33AE901E89D0B38656D3CC20D7B78DB8915B17F9A335` ;
- DLL WinUI adjacente/produit byte-identiques, SHA
  `431D575AA6672697872B9AE74E81240A06400ED1AAD860A8E4364F2BF3BDC9FC`.

Le wrapper fail-fast séparé est construit et son mode `-ArmOnly` confirme :

- zéro invocation officielle ; répertoire réservé absent ;
- une seule occurrence d'invocation `dotnet` dans le wrapper ;
- FQN exact A531, x64, `--no-build`, `--no-restore` ;
- modèle, serveur, settings, sources, tests, TRX et DLL authentifiés ;
- A513 et A524 explicitement non rejouables ;
- ressources et variables live à zéro.

L'invocation officielle A531 reste **NON EXÉCUTÉE**. Le prochain gate est
l'audit reproductible complet du wrapper et de l'armement. Un PASS de ce gate
autorisera une et une seule exécution live, sans modification produit ensuite.
Le statut reste `TESTÉ_NON_APPROUVÉ` et BUG133 reste ouvert.

#### Exécution officielle A531 — FAIL live, campagne consommée

L'unique invocation a eu lieu de **09:39:24 à 09:41:24 +02:00**. Elle est
terminée, non rejouable et a produit les quatre artefacts obligatoires.

Verdict : **FAIL LIVE FONCTIONNEL AVEC PREUVE POSITIVE PARTIELLE DU HANDOFF**.

- `pump_torque_direct` : `answer/E1`, handoff trace/prompt exact, writer 1,
  reviewer `accept`, publication source-verified E1 — PASS complet ;
- `survey_drone_focus_support` : `research/[]` avant writer au lieu de
  `answer/E2,E3` — FAIL de résolution ;
- `rail_wear_anchor_context` : `research/[]` au lieu de `context/E1` — FAIL de
  résolution/localisation ;
- 1/3 décisions exactes, 1 publication correcte, 0 fausse publication ;
- 5 appels au lieu du minimum 7, car les deux non-answers erronés arrêtent la
  transaction avant writer/reviewer ;
- 4 804 tokens sur 12 000 ; 102 332 ms, sous la cible 180 s ;
- zéro erreur transport/protocole, zéro réparation.

Artefacts immuables :

- JSON SHA
  `11EA39F15694409884C722E2154B64226A9298503ABBE317BA53B2F5247687EF` ;
- JSONL SHA
  `8D5020AAD6F2E3D36882232D8B22D36A88722CC6878E528667BAD004417A48AF` ;
- TRX FAIL SHA
  `1E8294EC0B835789CC1A50EA46CD516B97EFD4605CE0C6B119030FE3569E71F6` ;
- log SHA
  `7D19FC6314183CE8F61269CF624B033FCF2A6C41DE426C12D9635A43C17D514F`.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A531-RAPPORT-LIVE-HANDOFF-FAIL-SEMANTIQUE.md`,
247 lignes, SHA
`818FE86CBD7E2B174CCCF131F49C3CB9DDD29A74E2C4E00ABB868423C8A94B0F`.

Audit forensique : **122/122 PASS**.

- script : 266 lignes, SHA
  `EA85E6B7756E1D50A012C103E03118B54EAC01A3985FF9D24068DB179B195458` ;
- sortie : 123 lignes, SHA
  `E615ECAC8ACA1B77F91FFD7C8B3630466B4C02C0D4E4077ECDC2E336F966F5AB` ;
- longueurs/SHA des cinq prompts recalculés et exacts ;
- A513/A524 conservés ; Git conforme ; processus et variables live zéro.

La campagne prouve le handoff live direct, mais ne teste pas le handoff
focus/support : la résolution drone bloque avant le writer. Le défaut drone
reproduit la faiblesse décision-first déjà observée en A513/A524. Le rail révèle
en plus une frontière `context/research` fragile. Aucun changement mécanique ne
doit transformer ces décisions : A532 doit comparer les états A513/A524/A531 et
préenregistrer une amélioration où le LLM reste seul arbitre sémantique.

Promotion interdite. Produit `TESTÉ_NON_APPROUVÉ`. BUG133 ouvert. Telegram A531
obligatoire avant l'ouverture de toute nouvelle modification produit.

#### Notification Telegram A531 — confirmée

Envoi effectué le **2026-08-30 à 09:52:02 +02:00**, en une seule partie :

- titre : `SAAIA RAG - A531 live handoff partiel resolution FAIL` ;
- corps : 2 086 caractères ; total : 2 216 caractères ;
- code de sortie : 0 ;
- message SHA
  `6E539EEC3668C7A46DD64C9AF9BF0B07C9503ACFE0B0D077F86A9575EAD31F6D` ;
- audit pré-envoi : **55/55 PASS** ;
- reçu `00-RECU-TELEGRAM-A531.md` créé ;
- processus ciblés et variables live/validation après envoi : zéro.

A531 est **clos, audité et communiqué**. A532 peut maintenant effectuer
uniquement l'analyse causale comparative A513/A524/A531 et préenregistrer la
prochaine hypothèse ; aucun nouveau live et aucune promotion ne sont autorisés.

### A532 — recul causal sur la politique de résolution V5

La matrice A513/A524/A531 est figée dans
`artifacts/goal-rag-product-20260827-1041/phase5/A532-MATRICE-CAUSALE-RESOLUTION-V5-APRES-A531.md`,
251 lignes, SHA
`7A61C8F7BCE67CCB1AAB43967F8C709ED4D12A1DE33AAD26EF828A32CC7AEB42`.

Conclusions :

- décision-first reste meilleur globalement que reason-first : 4/5 contre 3/5
  sur A524 ; l'ordre n'est pas changé ;
- le comparatif confond portée bornée au pool et prétention exhaustive hors
  corpus ;
- `context` et `research` se chevauchent lorsqu'une ancre concrète existe ;
- `single_item` décrit l'unité du livrable et n'interdit pas plusieurs preuves
  de support : la « contradiction » signalée en A531 est requalifiée en
  ambiguïté de consigne ;
- les identités documentaires des harnais fuient des mots d'oracle et devront
  être neutralisées dans toute future campagne ;
- aucun fallback mécanique, aucune sélection codée et aucun replay ne sont
  autorisés.

Alternatives comparées : clarification prompt-only choisie ; reason-first,
fallback mécanique et simple augmentation de reason rejetés ; second arbitrage
LLM conservé seulement comme repli à cause de son coût mesuré de 14–23 s par
résolution.

### A533 — protocole de politique générique V5

Le protocole
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A533-A537-POLITIQUE-RESOLUTION-V5.md`
préenregistre un changement produit limité au seul texte système du juge :

- comparaison bornée versus prétention globale ;
- toutes les preuves nécessaires au claim indépendamment du nombre d'unités ;
- priorité `context` sur `research` pour un localisateur concret non ouvert ;
- raison concise et complète sous la borne existante.

Schéma, ordre `decision,evidenceIds,reason`, parser, appels, budgets, handoff,
writer, verifier, reviewer et fenêtre restent gelés. A533 n'autorise que l'audit
d'ouverture puis A534 RED ; aucun live n'est autorisé.

### A534 — RED causal de la politique de résolution V5

Trois tests test-only capturent le prompt réellement remis au client LLM et
exigent les cinq clauses génériques préenregistrées. Exécution ciblée x64 :
**0/3 PASS, 3/3 FAIL**, exactement sur les clauses encore absentes.

- tests SHA
  `F6444663BBB9765B3F752B10C04D74F54677E8C6066E3942E17DDBA231F72F66` ;
- runtime produit inchangé depuis A533, SHA
  `C35FB1512DCC74DBE3E549F225EAF24F1FEFAE55CA92FE666A4C7CC28909484F` ;
- contrat inchangé, SHA
  `2DA509D25BC4C51C79D89145F7DE70C1B1949816F340FDE616E4B309ACBED722` ;
- TRX RED SHA
  `9F57099994F4010780B2BAEC21589987B47DC92631DFE99E33AA2FBCF9B65364` ;
- `git diff --check` ciblé : PASS ;
- zéro appel modèle/live et zéro modification produit.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A534-RAPPORT-RED-POLITIQUE-RESOLUTION-V5.md`.

A534 est approuvé uniquement comme preuve RED. A535 est autorisé à modifier le
seul texte système du juge ; live et promotion restent interdits.

Audit A534 : **61/61 PASS**.

- script SHA
  `D512A0C3685DA4D55ABDF7D395D8DB80AC81F4A6F01B1D4D342B119F7C6F729F` ;
- sortie SHA
  `D93B8C6AB794155AF0508C241E0C62D69D22606BF8BC5520E9CEA509C715397D`.

### A535 — GREEN prompt-only minimal

Le seul texte système de `BuildSemanticResolutionMessages` porte maintenant
les cinq clauses génériques préenregistrées. Aucun terme de fixture/domaine,
aucun ID attendu et aucun fallback mécanique n'ont été ajoutés.

- runtime avant : SHA
  `C35FB1512DCC74DBE3E549F225EAF24F1FEFAE55CA92FE666A4C7CC28909484F` ;
- runtime après : SHA
  `4D8071C42A815795D51DCEFE8A558AEA241228126F1D8FD27B1325C09C93DE7D` ;
- contrat, budget et runner inchangés ;
- GREEN causal : **3/3 PASS**, TRX SHA
  `66B5427C2D0932F37AD904C9299BD776B3938CE6EF6C1855AC79E5A44568CD60` ;
- couture writer/reviewer : **23/23 PASS**, TRX SHA
  `985BF123C0A391F02AAFECA99ABFC0C4CEB94A64FF71950E88CCEC8A0CC11A36` ;
- processus ciblés après tests : zéro.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A535-RAPPORT-GREEN-MINIMAL-POLITIQUE-RESOLUTION-V5.md`.

A535 autorise A536 à lancer les régressions déterministes complètes et l'audit
de décision live. Aucun live n'est encore autorisé. Produit
`TESTÉ_NON_APPROUVÉ`, BUG133 ouvert.

Audit A535 : **79/79 PASS**.

- script SHA
  `D12FE744C6BEC4997E917088B47F31F26E0F98FEF055B79812230D6ADA80CEB1` ;
- sortie SHA
  `B07FB26B080216921384182D5E344907E6E542C749BAC963F3E150571222DD8D`.

### A536 — régressions finales et décision A537

Tous les gates déterministes passent sur le même binaire x64 :

- GREEN causal : **3/3 PASS** ;
- couture : **23/23 PASS** ;
- onze groupes historiques : **580/580 PASS**, TRX SHA
  `8359F799F359CF8770944E034309BC37ADDE128ED6D5A812D76DCE21CD51D0BF` ;
- suite client complète : **1 613/1 613 PASS**, TRX SHA
  `B96475D09A24C6B8DFCFBCE048BF7574806500CE53AE6EDFCB0989D0899C9A5F` ;
- DLL WinUI tests/produit byte-identiques, SHA
  `9E0D137B996C4B28375504F96E970D9CE750B2724480C8D503A8AB090D7681B5` ;
- A513/A524/A531 immuables ; processus et variables live zéro.

Décision : **A537 autorisé après protocole séparé**, car seul le vrai Qwen peut
montrer si les règles prompt-only corrigent les deux frontières A531 sans faux
`answer`. La campagne devra couvrir comparatif borné, exhaustif global, ancre
concrète et manque sans ancre avec identités source neutres. Le second arbitre
LLM reste un repli plus coûteux.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A536-RAPPORT-REGRESSIONS-AUDIT-DECISION-LIVE-A537.md`.

Aucun live A536. Telegram obligatoire avant A537. Produit
`TESTÉ_NON_APPROUVÉ`, BUG133 ouvert, promotion interdite.

Audit A536 : **134/134 PASS**.

- script SHA
  `BE3D0D9CD0E54E9D6584589E39DBB6D3EDBBC85B1692EDE5A974E7CB2D01DF34` ;
- sortie SHA
  `070E74458813046ACD3E3554BA3AF2B238DEF946E38D8122C0CD42E5FB5D1F33`.

Notification Telegram A532–A536 envoyée le **2026-08-30 à 10:18:58
+02:00** :

- titre : `SAAIA RAG - A532-A536 politique V5 validee, live A537 autorise` ;
- corps : 2 138 caractères ; total : 2 277 caractères ;
- message SHA
  `216B7B3B5E7C5E363807BB0929A02042EF164BF61BA410A893F95CB3E04AA84A` ;
- audit pré-envoi : **49/49 PASS** ;
- reçu :
  `artifacts/goal-rag-product-20260827-1041/phase5/00-RECU-TELEGRAM-A532-A536.md`.

A536 est clos, audité et communiqué. A537 peut maintenant commencer par son
protocole documentaire ; aucune invocation live n'est encore autorisée.

### A537 — protocole live neutre de la politique V5

Le protocole
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A537-LIVE-POLITIQUE-RESOLUTION-NEUTRE.md`
préenregistre quatre transactions multi-domaines :

- `txn_kappa` : comparaison bornée, attendu `answer/E2,E1` avec writer,
  reviewer, handoff et publication vérifiée ;
- `txn_lambda` : prétention mondiale non couverte, attendu `research/[]` ;
- `txn_mu` : localisateur concret non ouvert, attendu `context/E1` ;
- `txn_nu` : information absente sans localisateur, attendu `research/[]`.

Les sept identités `docName/docPath/chunkId` sont explicitement figées et
exemptes de tokens d'oracle. Budgets : 6–8 appels, 10 000 tokens, 360 s maximum.
Artefacts complets JSON/JSONL/TRX/log et prompts authentifiés obligatoires.

Avant live : harnais 21/21, groupes historiques 583/583, suite client
1 616/1 616, parité DLL, wrapper fail-fast et audit d'armement. Une seule
invocation sera autorisée ; le répertoire officiel créé consommera la campagne.

A537 est encore documentaire. Aucun live n'est exécuté à ce stade. Produit
`TESTÉ_NON_APPROUVÉ`, BUG133 ouvert, promotion interdite.

#### Préflight A537 — armé, zéro invocation

Le harnais test-only A537 ajoute quatre transactions et sept identités source
neutres sans modifier le runtime produit.

- harnais : **21/21 PASS**, TRX SHA
  `94D9910581F96C5A0076D82737E328E932AC36E8B4941FE8961D72D7304B650A` ;
- onze groupes historiques : **583/583 PASS**, TRX SHA
  `7A45C46048458838BA838416C67B9B5D55AA2B4B957D5778B9F21C70D9938896` ;
- suite client x64 : **1 616/1 616 PASS**, TRX SHA
  `9E3D5C39A29C398F29C845EC7EDEF14EF02AD8C435190F133A6444F8AC7869F7` ;
- DLL WinUI tests/produit byte-identiques, SHA
  `9E0D137B996C4B28375504F96E970D9CE750B2724480C8D503A8AB090D7681B5` ;
- runtime A535 inchangé, SHA
  `4D8071C42A815795D51DCEFE8A558AEA241228126F1D8FD27B1325C09C93DE7D`.

Wrapper fail-fast : 244 lignes, SHA
`1FC636F15B0E278C531658388BA7FDAC059D12A083AB657FB1835124F5CCB64B`.
`-ArmOnly` confirme zéro invocation et répertoire officiel absent. Audit
indépendant : **397/397 PASS**, sortie SHA
`80C880D264990A2FD2CA5DE288159B8C66609E3441A818EE412570C672DAD4DB`.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A537-RAPPORT-PREFLIGHT-LIVE-POLITIQUE-NEUTRE.md`.

Un dernier sceau documentaire doit authentifier ce rapport et le plan avant
l'unique invocation. Aucun live n'a encore eu lieu. Produit
`TESTÉ_NON_APPROUVÉ`, BUG133 ouvert, promotion interdite.

Sceau avant live : **53/53 PASS**.

- script SHA
  `54347E75AEBA13E5B89A1D3B5A954B098E03E6A8D33C98230B8D827CEC2AC9D0` ;
- sortie SHA
  `AD988A79691EE118DE2A2AF9537F56926F711E924DFCE035FB16F62D10F238E4`.

#### Exécution officielle A537 — FAIL partiel, campagne consommée

L'unique invocation est terminée et ne sera pas rejouée. Verdict : **FAIL LIVE
PARTIEL**.

- 4/4 cas exécutés ; 4/4 protocoles V5 valides ;
- 3/4 décisions brutes conformes ; 2/4 cas sans erreur stricte ;
- `txn_kappa` : `answer/E1,E2`, publication vérifiée et reviewer accept, mais
  attendu `answer/E2,E1` avec lead E2 — ordre/rôle FAIL ;
- `txn_lambda` : `research/[]` — PASS de sûreté globale ;
- `txn_mu` : `research/[]` au lieu de `context/E1` — FAIL contextuel reproduit ;
- `txn_nu` : `research/[]` — PASS de manque sans ancre ;
- une publication, zéro fausse publication, zéro publication sans accept ;
- 6 appels, 6 735 tokens, 133 120 ms, tous budgets respectés ;
- deux reasons atteignent 480 caractères ; celui de `txn_mu` se termine par
  `la缺`, malgré `RAISON_CONCISE` ;
- processus et variables live après wrapper : zéro.

Artefacts immuables :

- JSON SHA
  `15692935140A309C6B1A56C0710DC485A8EB00A4B8231349CA568F595F90B1DC` ;
- JSONL SHA
  `AD060682DA36A24054BA60F464AFED84C15C0C45F5B0848446E9FBD56E12288F` ;
- TRX FAIL SHA
  `AEF484412B0D61A17405EB94808EB49BDC549D97BB4C61479BA36CFAFC784DF4` ;
- log SHA
  `AB0248E1C946DF2B02DE9B19D959202E8674BC376EE6DA7C70699885E0CDE873`.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A537-RAPPORT-LIVE-FAIL-PARTIEL-POLITIQUE-NEUTRE.md`.

A537 prouve un gain comparatif et deux garde-fous, mais pas la priorité
contextuelle ni le rôle lead/support. Promotion interdite. Produit
`TESTÉ_NON_APPROUVÉ`, BUG133 ouvert. Audit forensique et Telegram obligatoires
avant A538.

Audit forensique A537 : **226/226 PASS**.

- script SHA
  `E55B7C141253434C8409756C923D6239FD42986EA06C52F699A44DD9ED68E5DF` ;
- sortie SHA
  `803B78596813AC0745E5171312E8DC76EA326CA139432B92FBC4C654B06CD722` ;
- six prompts complets recalculés et authentifiés ;
- verdicts, ordres, leads, publications, tokens et temps recoupés ;
- A513/A524/A531 immuables ; runtime et harnais inchangés ;
- processus et variables live zéro.

Telegram A537 obligatoire avant A538. Aucun nouveau live et aucune modification
produit ne sont autorisés avant cette communication puis l'analyse causale.

Notification Telegram A537 envoyée le **2026-08-30 à 10:46:43 +02:00** :

- titre : `SAAIA RAG - A537 live V5 neutre FAIL partiel sans replay` ;
- corps : 2 406 caractères ; total : 2 539 caractères ;
- message SHA
  `FA4CA86AEFE41D1C9157955456E40F2D801133F8FEE83122FE9998DF185AD6E1` ;
- audit pré-envoi : **53/53 PASS** ;
- reçu :
  `artifacts/goal-rag-product-20260827-1041/phase5/00-RECU-TELEGRAM-A537.md`.

A537 est close, auditée et communiquée. A538 peut maintenant analyser les
alternatives sans live et sans modification produit immédiate. Produit
`TESTÉ_NON_APPROUVÉ`, BUG133 ouvert, promotion interdite.

### A538 — matrice causale après A537

Le rapport
`artifacts/goal-rag-product-20260827-1041/phase5/A538-MATRICE-CAUSALE-APRES-A537-ET-DECISION-A539.md`
conclut :

- la politique comparative A535 a un effet utile : décision `answer` et
  ensemble E1+E2 au lieu du `research/[]` historique ;
- la sûreté globale et le manque sans ancre restent corrects ;
- le premier ID devient mécaniquement le lead, mais le prompt ne demande pas
  encore « preuve principale puis supports » ;
- `context` reste dominé par `research` malgré un index section/page explicite ;
- `RAISON_CONCISE` ne prévient pas les reasons de 480 caractères.

Alternatives : patch immédiat rejeté ; schéma `leadEvidenceId` et second arbitre
LLM gardés en repli ; fallback mécanique rejeté. Choix : **A539 apparié
test-only**, prompt courant contre candidat générique, quatre nouveaux cas,
huit appels de résolution, sans writer/reviewer ni modification produit.

Le candidat précise ordre preuve principale/supports, localisateurs
document/index/section/page/tableau/annexe/ancre et reason ≤ 240 caractères en
deux phrases complètes. A539 exige un protocole séparé et un nouveau live à
invocation unique. Aucun live n'est lancé en A538.

Produit `TESTÉ_NON_APPROUVÉ`, BUG133 ouvert, promotion interdite.

Audit A538 : **59/59 PASS**.

- script SHA
  `6D4AECC75A1113EDDB8219965809033CC35BB3D087A72989D0B7A81E4A0FE348` ;
- sortie SHA
  `47C55E2D3EBF40418772CA09E14A2438EF0AB321566237371D287BAFDB1EE95C`.

### A539 — protocole live apparié du prompt candidat

Le protocole
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A539-LIVE-APPARIE-PROMPT-POLITIQUE-V5.md`
fige quatre nouveaux cas et huit appels alternés `candidate/current`.

Le contrôle capture le prompt produit courant depuis le vrai runner. Le
candidat ne remplace que trois politiques génériques : ordre preuve principale
puis supports, localisateur document/index/section/page/tableau/annexe/ancre,
et reason ≤ 240 caractères en deux phrases complètes.

Cas : comparatif `answer/E2,E1`, localisateur `context/E1`, global
`research/[]`, manque `research/[]`. Aucun writer/reviewer/publication. Le
candidat n'est soutenu que s'il atteint 4/4 décisions+IDs, 4/4 reasons courtes,
2/2 sûreté et améliore strictement le contrôle.

Budget : huit appels, 12 000 tokens, 360 s. Nouveau harnais test-only, wrapper
fail-fast et audit requis. A539 est encore documentaire ; aucun live n'est
autorisé. Produit `TESTÉ_NON_APPROUVÉ`, BUG133 ouvert, promotion interdite.

#### Audit d'ouverture A539 — protocole scellé, zéro live

L'état antérieur à toute implémentation A539 est audité : **129/129 PASS**.

- protocole : 10 155 octets, 245 lignes, SHA
  `FD4B384F8A98799CFCC79364A6CAF76F37CEC35647C7850EAF79798FBEEAF510` ;
- runtime produit, contrat V5 et harnais A537 byte-identiques aux empreintes
  gelées ;
- JSON/JSONL/TRX/log A537 immuables ; reçu Telegram A537 authentifié ;
- quatre cas, huit positions, six clauses, identités neutres, budgets, flag,
  FQN, classification et invocation unique recoupés ;
- harnais/helper A539 et répertoire officiel absents ; variables live et
  processus `dotnet`/`llama-server`/WinUI à zéro ;
- `git diff --check` sort à zéro, avec treize avertissements historiques de
  normalisation de fins de ligne et aucune erreur d'espace.

Script :
`artifacts/goal-rag-product-20260827-1041/phase5/a539-audit-opening-protocol.ps1`.
Sortie :
`artifacts/goal-rag-product-20260827-1041/phase5/A539-SORTIE-AUDIT-OUVERTURE-PROTOCOLE.txt`.

A539 peut maintenant ajouter exclusivement son helper et son harnais test-only.
Le live reste interdit jusqu'aux tests, régressions, wrapper et audit
d'armement.

#### Préflight A539 — armé, zéro invocation

Le helper et le harnais A539 sont exclusivement test-only. Ils capturent le
prompt courant depuis le vrai runner, transforment exactement trois clauses et
appellent directement Qwen, sans writer/reviewer/handoff/publication.

- harnais A539 désarmé : **11/11 PASS**, TRX SHA
  `ABC8DABA537D5B5E3A42395E8481208C0DC216B15B172F9B594E489461795FCE` ;
- douze groupes ciblés : **594/594 PASS**, TRX SHA
  `59C5D17F2196616CA5B882E78AA96C211464116F0979BCD3CECBE68C8DCFA0E5` ;
- suite client x64 : **1 627/1 627 PASS**, TRX SHA
  `D403170CA93110A07869F6BC9DCA5F20905B04519FCBF7B493F46ED648E96F85` ;
- DLL WinUI tests/produit byte-identiques, SHA
  `9E0D137B996C4B28375504F96E970D9CE750B2724480C8D503A8AB090D7681B5` ;
- runtime, contrat V5, harnais et artefacts A537 inchangés ;
- wrapper fail-fast : 280 lignes, SHA
  `E90066A0460738F1DBBC8C202440C69FF5418228384EF206C36056206DFE286A` ;
- `-ArmOnly` : armé, invocation 0, répertoire officiel absent ;
- audit indépendant : **191/191 PASS**, sortie SHA
  `D9EF2B833EDE230FC2D40B1460E4DD3F69B77926FA42F9BF5FF23DD967FD4B69` ;
- processus ciblés et variables live : zéro.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A539-RAPPORT-PREFLIGHT-LIVE-APPARIE-PROMPT-POLITIQUE.md`.

Le préflight autorise un dernier sceau documentaire. Aucun live n'a encore eu
lieu. Produit `TESTÉ_NON_APPROUVÉ`, BUG133 ouvert, promotion interdite.

#### Exécution officielle A539 — candidat rejeté, campagne consommée

Le sceau final passe **63/63**. L'unique invocation A539 est ensuite terminée et
ne sera pas rejouée. Verdict canonique : **`candidate_rejected_accuracy`**.

- 4 cas, 8 appels, 8 protocoles V5 valides ;
- current : 3/4 décisions+IDs exacts ; candidate : 3/4 ;
- `pair_omicron` reste `answer/E1,E2` dans les deux bras au lieu de
  `answer/E2,E1` : la clause d'ordre n'a aucun effet ;
- `pair_pi` vaut `context/E1` dans les deux bras : décision appariée égale,
  reason 409 caractères current contre 177 candidate ;
- `pair_rho` et `pair_sigma` restent `research/[]` dans les deux bras ;
- reasons candidates : **4/4 PASS** contre **0/4** current ;
- sûreté candidate : **2/2**, zéro faux answer/context ;
- writer/reviewer/handoff/publication : **0/0/0/0** ;
- 8 145/12 000 tokens ; 149 619/360 000 ms ; zéro erreur transport ;
- processus et variables live après run : zéro.

Artefacts : JSON SHA
`755EF8EC4D98E5428A32CF1788B1EC46219387648B81707D240E653E6E604594`,
JSONL SHA
`13726B427DDD6FFD28A82794037AC6EB9233EF587A7E326ACDD9A31D3DEFA646`,
TRX SHA
`EE1ABD2A96308F5E0328ADF09566EB7CA13DE2D0B458365BD4FBBF4FE41DD000`,
log SHA
`5476C228B8978908849CAA735407DAF2F05086EDBC7BB21B2082AF503222E021`.

Le harnais a enregistré `campaign_invalid` parce qu'il mélange l'échec de gate
candidate aux erreurs d'intégrité avant classification. L'audit indépendant
confirme une campagne intègre et applique la classe préenregistrée correcte :
`candidate_rejected_accuracy`.

Audit forensique : **289/289 PASS** ; sortie SHA
`E05F82AFF4FFD670318AFEEF937837CDD35E542956F442C429EB215E9DA79B84`.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A540-RAPPORT-A539-LIVE-FAIL-APPARIE-ET-DECISION-ARCHITECTURALE.md`.

Décision : arrêter l'empilement de clauses sur l'ordre. A541 comparera un champ
sémantique explicite `leadEvidenceId`, un second arbitre LLM ciblé et le témoin
actuel. Aucun fallback mécanique de rôle. La reason courte est soutenue mais
non promue sans protocole isolé ; la décision contextuelle reste inconclusive.
Telegram obligatoire avant A541. Produit `TESTÉ_NON_APPROUVÉ`, BUG133 ouvert.

Notification Telegram A539 envoyée le **2026-08-30 à 11:31:23 +02:00** :

- titre : `SAAIA RAG - A539 candidat rejete, changement d architecture` ;
- corps : 2 478 caractères ; total : 2 614 caractères ;
- message SHA
  `19258033E74F6BFFB81D5658DD149BFE79DC142A5BEFB4567606793AD5031AC3` ;
- audit pré-envoi : **54/54 PASS** ;
- reçu :
  `artifacts/goal-rag-product-20260827-1041/phase5/00-RECU-TELEGRAM-A539.md`.

A539 est close, auditée et communiquée. A541 peut commencer par l'analyse
architecturale sans live. Aucun patch produit n'est encore autorisé.

### A541 — contrat de preuve principale explicite retenu

L'analyse architecturale est close sans appel modèle et sans modification du
runtime. Elle confirme que V5 surcharge `evidenceIds` : le parser prend le
premier ID comme lead alors que Qwen ne dispose d'aucun champ séparé pour
exprimer principal/support. A537 puis A539 ont reproduit ce défaut malgré une
consigne d'ordre explicite.

Décision : préparer un **V6 minimal** à quatre champs :

- `decision` ;
- `evidenceIds`, ensemble complet des preuves nécessaires ;
- `leadEvidenceIds`, sous-ensemble ordonné des preuves principales déclaré par
  Qwen ;
- `reason`.

Le pluriel est retenu plutôt que `leadEvidenceId` singulier : le modèle aval
`FastEvidenceReview` transporte déjà une liste, plusieurs preuves peuvent être
co-principales, et aucune conversion singulier → liste ne doit limiter
artificiellement le contrat. Pour `answer`, les leads sont un sous-ensemble non
vide ; pour `context`, l'unique preuve est aussi l'unique lead ; pour
`research/clarify`, les deux listes sont vides.

Le code validera seulement IDs, unicité, cardinalité, inclusion et cohérence
par décision. Il appliquera au writer l'ordre déclaré `leads + supports
restants`, sans comparer de valeur, score, domaine ou mot métier. Le second
arbitre LLM reste un repli plus coûteux ; toute inférence mécanique du lead
reste interdite.

La reason courte A539 et la frontière `context/research` sont volontairement
séparées de ce test causal.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A541-MATRICE-ARCHITECTURALE-CONTRAT-LEAD-EXPLICITE.md`.

- rapport : 344 lignes, SHA
  `D9006D3A9D18283AD9154E13F64FEBBF17E38BDE508B1B9858BD407B71D595A0` ;
- audit : **60/60 PASS** ;
- script SHA
  `DDF96CC389FD60EEB0166C4212147D370F08CD59C6F7DA6F89A02099EC6FCCFB` ;
- sortie SHA
  `EBFBCCB45FB2E8986C349EDBD0FE3814396CDF9515FF58BE440DD22863633100` ;
- le premier passage avait seulement révélé deux défauts du script d'audit
  lui-même — fragment coupé par une nouvelle ligne et booléens PowerShell non
  parenthésés — corrigés avant le sceau final ; aucun défaut produit n'était
  impliqué ;
- artefacts live A539 immuables ; branche `SAAIA_V3.1`, HEAD
  `5f35881cdc67d12a076fcd2a7a1004656ac9a37a` ;
- `git diff --check` : exit 0, treize avertissements historiques de fins de
  ligne, aucune erreur ;
- variables live et processus de calcul ciblés : zéro.

Séquence suivante : communiquer ce palier sur Telegram, puis A542 corrigera
exclusivement les deux dettes du harnais test-only A539 sans replay. A543
préenregistrera ensuite le RED V6 avant tout patch produit.

Produit `TESTÉ_NON_APPROUVÉ`, BUG133 ouvert, promotion interdite.

Notification Telegram A541 envoyée le **2026-08-30 à 11:44:07 +02:00** :

- titre : `SAAIA RAG - A541 contrat lead explicite retenu` ;
- corps : 2 421 caractères ; total : 2 544 caractères ;
- message SHA
  `88ED25CC68F486EC12B919260923196C7F8267B53EB398726AA191029CF5A745` ;
- audit pré-envoi : **54/54 PASS** ;
- reçu :
  `artifacts/goal-rag-product-20260827-1041/phase5/00-RECU-TELEGRAM-A541.md`,
  SHA
  `27EE559B1D3148D2E618FAB762388621B310E487921497FDF827A77586E28B6A`.

A541 est close, auditée et communiquée. A542 est autorisée, uniquement sur le
harnais test-only A539 et sans replay des artefacts live officiels.

### A542 — dettes du harnais A539 corrigées, sans replay live

A542 est close sur son périmètre technique : deux défauts exclusivement
test-only du harnais A539 ont été corrigés, sans modifier le runtime produit,
le contrat V5, le helper d'appel modèle ni aucun artefact live officiel.

Le protocole a gelé avant modification les deux obligations :

- séparer les erreurs d'intégrité de campagne des échecs de critères du
  candidat, afin qu'un candidat valide mais insuffisant soit classé
  `candidate_rejected_accuracy` et non `campaign_invalid` ;
- sérialiser les messages capturés sous des propriétés JSON nommées `role` et
  `content`, tout en conservant les tuples en mémoire utilisés par le runner.

Correction appliquée uniquement dans
`client/SAAIA.Client.ToolAgent.Tests/LiveSemanticResolutionV5PromptPolicyPairedCanaryTests.cs` :

- SHA avant A542 :
  `17A9EEC6CD9BD4D483A980F3AC1231A67A724C75BCFD091655ABCC3C659B46AC` ;
- SHA après A542 :
  `EDF62B538A43279B5437AC1346D3D817C4C7CF1A0382A945D214BD2C9C7FA513` ;
- taille après correction : 1 358 lignes, 58 144 octets ;
- `CampaignEvaluation` transporte maintenant séparément
  `IntegrityErrors` et `CandidateGateErrors` ;
- la classification utilise uniquement les erreurs d'intégrité pures ;
- `FailureReasons` reste l'agrégat terminal du test afin qu'un candidat rejeté
  continue de faire échouer explicitement un éventuel run live futur ;
- `PromptCapture.Messages` est exclu du JSON et `RecordedMessages` expose des
  objets nommés `RecordedMessage(Role, Content)`.

Deux tests déterministes dédiés verrouillent ces comportements :

- `A542_campaign_outcome_keeps_integrity_and_candidate_gates_separate` ;
- `A542_prompt_capture_serializes_named_messages`.

Résultats :

- classe ciblée Debug x64 : **13/13 PASS**, 0 échec, 0 non exécuté ;
- TRX ciblé SHA
  `52FE1448FECF13A4352C2B9486E70AE5183363B09DFD6AFA9B0B1E6608450930` ;
- suite client complète Debug x64 : **1 629/1 629 PASS**, 0 échec, erreur,
  timeout, abandon ou non exécuté ;
- TRX complet SHA
  `BA67D4A6D70FA74BB2556F623BDE39A2E09FFF266C883F3CCBE192AD4319B7A9` ;
- DLL tests SHA
  `77EFAB3B6A25F30BBB78906A0D5FBAD7647811E61B6333B0C3197633FE08B836` ;
- DLL WinUI copiée et DLL WinUI produit byte-identiques, SHA
  `9E0D137B996C4B28375504F96E970D9CE750B2724480C8D503A8AB090D7681B5`.

Une première invocation accidentelle en configuration `Release` a échoué à la
compilation avant tout test, car les nombreux hooks `ForTests` du dépôt sont
conditionnés par `DEBUG`. Ce résultat est classé erreur de configuration du
runner, sans TRX et sans effet live ; aucun hook produit n'a été ajouté ni
modifié. Les exécutions probantes ont ensuite utilisé la configuration
historique correcte `Debug` et la plateforme `x64`.

Preuves A542 :

- protocole :
  `artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A542-CORRECTION-HARNAIS-TEST-ONLY-A539.md`,
  SHA
  `1E4067E16639390AF66F99733F1311D49A04A86A075BD9AC741F101995768222` ;
- audit d'ouverture : **35/35 PASS**, sortie SHA
  `CB7EDA61DFC927BE447D53B4E86BBCC967C7C34D224B7334EE774482099EA816` ;
- rapport :
  `artifacts/goal-rag-product-20260827-1041/phase5/A542-RAPPORT-CORRECTION-HARNAIS-TEST-ONLY-A539.md`,
  262 lignes, 8 664 octets, SHA
  `8364B555D8A49A8D64E4969D98CEE51FF56F941784A61AFFAC3FB4B738AF274C` ;
- audit final : **79/79 PASS** ;
- script final SHA
  `E90C7344A81F608B7E6D1250C40C32B6F8B2AFFFD7C4C1D034A054D8C94446CB` ;
- sortie finale :
  `artifacts/goal-rag-product-20260827-1041/phase5/A542-SORTIE-AUDIT-FINAL-HARNAIS-CORRIGE.txt`,
  81 lignes, 3 444 octets, SHA
  `3CB1373616DDB1B4D551A5D4C32278EE3F5042E783D67E95A773575085375AF6`.

L'audit final réauthentifie les quatre artefacts live A539 et confirme : aucun
flag live, aucune sortie live A542, aucun processus ciblé, aucune variable live,
branche `SAAIA_V3.1`, HEAD
`5f35881cdc67d12a076fcd2a7a1004656ac9a37a`, `git diff --check` à zéro erreur.

A542 corrige donc le compte rendu futur du banc de test ; elle ne transforme
pas rétroactivement les octets du run A539. Le verdict forensique A539 reste
`candidate_rejected_accuracy`. Produit `TESTÉ_NON_APPROUVÉ`, BUG133 ouvert,
promotion interdite. Telegram obligatoire avant l'ouverture de A543.

Notification Telegram A542 envoyée le **2026-08-30 à 12:02:20 +02:00** :

- titre :
  `SAAIA RAG - A542 harnais A539 corrige et 1629 tests verts` ;
- corps : 2 434 caractères ; total : 2 568 caractères ;
- message SHA
  `84FF360BE72F79607978CA46261A9E46102D5BDCA1B20E5846138C206D6A6EC8` ;
- audit pré-envoi : **71/71 PASS** ;
- script pré-envoi SHA
  `CCE415E2AD7E557E58A07AD662175D696A30497059F299305032ECF28EE70873` ;
- sortie pré-envoi SHA
  `EA9C3CF89D7CB54EE58340ECE4AE641D4AD1169E78F5336B56F7B01D6B276606` ;
- reçu :
  `artifacts/goal-rag-product-20260827-1041/phase5/00-RECU-TELEGRAM-A542.md`,
  47 lignes, 1 786 octets, SHA
  `881404C9F1B7D249A49C16BD2C851FD93226BDA505FA3D359EB1CB1DA883971D`.

A542 est close, testée, auditée et communiquée. A543 est autorisée à ouvrir
exclusivement la spécification et le RED du contrat V6, sans live et avant tout
patch produit. Produit `TESTÉ_NON_APPROUVÉ`, BUG133 ouvert.

### A543 — RED causal du contrat sémantique V6 obtenu

A543 est close et approuvée comme **RED déterministe**, sans aucune modification
produit et sans live. L'ADR source-backed a été relu intégralement avant le gel
du contrat. Le protocole cible un objet
`source_backed_semantic_resolution_v6` à quatre champs obligatoires :

- `decision` ;
- `evidenceIds`, ensemble complet des preuves nécessaires ;
- `leadEvidenceIds`, sous-ensemble ordonné des preuves principales choisi par
  Qwen ;
- `reason`.

Le code futur validera uniquement pool, IDs, unicité, cardinalités, inclusion et
cohérence par décision. Pour le writer, il dérivera mécaniquement
`leads + supports restants`, en exposant sélection et leads séparément dans le
prompt et les traces. Aucune inférence de valeur, score, durée, domaine, mot ou
position n'est autorisée.

Protocole final :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A543-RED-CONTRAT-SEMANTIQUE-V6.md`.

- 266 lignes, 10 922 octets ;
- SHA
  `35F9C282D8370C7B6A375B415827E56F411E2B380487B51801686021C43B0579`.

Audit d'ouverture avant création du test : **49/49 PASS**. Il a gelé six
fichiers produit, le test V5 existant, la suite A542, les artefacts live A539,
la branche, le HEAD et l'absence de processus/variables live.

Deux défauts test-only ont été détectés et rejetés avant le RED probant :

1. le filtre initial reprenait le nom du fichier au lieu du FQN de la classe
   partielle ; il a été corrigé avant toute invocation en
   `FullyQualifiedName~SemanticResolutionV6` ;
2. la première invocation au bon filtre a échoué avant test avec `CS0246` car
   le nouveau fichier n'importait pas localement le namespace de `ToolResults` ;
   seuls deux `using` test-only ont été ajoutés, aucun TRX n'avait été créé et
   aucun fichier produit n'avait changé.

Après correction, l'audit de réarmement a passé **54/54** :

- script SHA
  `6CA7A562FAF5BCED9D92E2E72EC2FD4D52B6998B16189BD7F727590447F02087` ;
- sortie SHA
  `DA807192DAAFF817E7C8B034F9DB2C01AE9BABC7395D036A6B98EBDFB37900B4`.

Fichier RED test-only :
`client/SAAIA.Client.ToolAgent.Tests/SourceBackedAgentSemanticResolutionV6ContractRedTests.cs`.

- 364 lignes, 14 615 octets ;
- SHA
  `37B6183955B63EEADCF88313275BB85194328517D40276FA5EB399F3B01C0BD4` ;
- 16 `[Fact]`, 16 noms uniques.

Campagne RED Debug x64 : build réussi, **16/16 FAIL attendus**, 0 PASS, 0
skipped, 0 erreur infrastructure, timeout, abandon ou non exécuté. Les échecs
couvrent :

- nom de contrat V5 au lieu de V6 ;
- propriété `leadEvidenceIds` absente ;
- rôles sélection/leads absents du prompt ;
- payloads V6 answer/context/research/clarify rejetés à la racine ;
- taxonomie fine lead non disponible ;
- co-leads et ordre writer non disponibles ;
- payload V5 sans leads encore accepté et publié.

TRX canonique : 570 lignes, 84 565 octets, SHA
`5D2A73FABECEA5C5C945E2F7E763B2481ED0B4635983964E3F4BD162885D3C4F`.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A543-RAPPORT-RED-CONTRAT-SEMANTIQUE-V6.md`.

- 271 lignes, 11 024 octets ;
- SHA
  `3DAF2D9ACA9217FD1F1C966312238DB8705D5B0B29BE56F93EA0658C9F951993`.

Audit final : **128/128 PASS**.

- script : 224 lignes, 11 314 octets, SHA
  `41DE7EB56E2A6B995B7F0D425426BA15FAE84C6E67595113E04EFB5E6E228C13` ;
- sortie : 130 lignes, 8 273 octets, SHA
  `340B7039405863E8F8DBE10463052CD82DF337CD3B6CE9D604C1A0DB376A874D`.

Les six fichiers produit restent aux empreintes entrantes V5. Les DLL WinUI
tests/produit sont byte-identiques, SHA
`9E0D137B996C4B28375504F96E970D9CE750B2724480C8D503A8AB090D7681B5`.
Les artefacts live A539 sont immuables ; processus et variables live sont à
zéro ; `git diff --check` sort à zéro erreur avec treize avertissements
historiques de fins de ligne.

Telegram A543 non envoyé conformément au protocole : ce palier ne contient
qu'un échec attendu, immédiatement après le rapport A542. Le prochain palier
intéressant à notifier est le GREEN A544, ou un blocage architectural réel.

A544 est autorisée à implémenter le delta V6 minimal et rien de plus, puis à
faire passer les 16 cas, les régressions ciblées et la suite client complète.
Aucun live n'est autorisé en A544. Produit `TESTÉ_NON_APPROUVÉ`, BUG133 ouvert,
promotion interdite.

### A544 — contrat sémantique V6 GREEN, live non remesuré

A544 est techniquement GREEN sur son périmètre déterministe. Le contrat runtime
est désormais `source_backed_semantic_resolution_v6` avec quatre champs
obligatoires :

- `decision` ;
- `evidenceIds`, sélection complète des preuves nécessaires ;
- `leadEvidenceIds`, sous-ensemble ordonné des preuves principales choisi par
  Qwen ;
- `reason`.

Le code valide uniquement forme, pool, unicité, cardinalités, inclusion et
cohérence par décision. Il transmet au writer `leads + supports restants` sans
comparer de valeur, durée, score, domaine, mot métier ou position. Le prompt et
la trace de handoff exposent séparément sélection et leads. La dérivation du
premier `evidenceId` comme lead a été supprimée.

Fichiers produit modifiés et SHA finaux :

- `SourceBackedAgentSemanticResolutionContract.cs` :
  `23A43A6D3B5272CD247B4378B850C6507F20D0396E7B257C1BF49299ED7769D8` ;
- `SourceBackedAgentSemanticResolutionWriterReview.cs` :
  `B8602CAC44B837516AE2045FCB58B40C9019D0F00314CF402DD24ADEC3387F79` ;
- `SourceBackedAgentSemanticResolutionWriterReviewTracing.cs` :
  `411B82E05D28EAE6A2D3AC36958F263B598A12F6A29452ADE1C502717E9F3E61` ;
- `SourceBackedAgentV2WriterPromptContext.cs` :
  `65F7832D85B0BBAA3073AF7AC2348442B45B5EF5EBC963A80BF1ECAD7D818F6C` ;
- `SourceBackedAgentStructuredFlatWriter.cs` :
  `77A624A458A23C5946FFA549AA6BF0B4DD109BAD318FC9532DDC0E3370F9AFDB` ;
- `SourceBackedAgentV2CompletionSupport.cs` :
  `7BDC2B90E761DF60BE9EB48113F6E7B8B3C98F46D1114E2DDAD9FD2B1C9BCC1A` ;
- `SourceContractVerifierSemanticSelection.cs` :
  `762C8C8A119E470D9BB6987B76E608745408EF28AAF08260E9E221F454F89C11`.

`SourceBackedAgentFastEvidenceReviewModel.cs` reste byte-identique, SHA
`E9FD8B655F42D625A4420EF23AAF90EE5575FA7E6655A1EF878E9D3C10D41AA9`.

Le premier GREEN a atteint **14/16**. Les deux échecs sur les scénarios answer
étaient causés par l'ancien compteur : un libellé synthétique nu `E2` dans le
texte plus l'unique marqueur `[E2]` rendu par le writer étaient comptés comme
deux citations. Le vérificateur compte désormais les marqueurs canoniques
lorsqu'ils existent, tout en conservant le comportement historique pour les ID
nus et en rejetant toujours deux vrais `[E1]`. La classe du vérificateur passe
**18/18**, puis les oracles V6 passent **16/16**.

Les régressions ont imposé des migrations test-only sans changement des oracles
métier :

- fixtures runtime passées au JSON V6 ;
- helper legacy V5 conservé pour prouver son rejet par V6 ;
- expérience d'ordre A524/A525 isolée derrière son constructeur V5 gelé ;
- capture A539 adaptée au contrat V6, sans replay ;
- deux helpers par réflexion adaptés au paramètre optionnel de leads.

Le fichier RED A543 est resté byte-identique : 364 lignes, 14 615 octets, SHA
`37B6183955B63EEADCF88313275BB85194328517D40276FA5EB399F3B01C0BD4`.

Résultats conservés avec distinction stricte :

- premier ciblé : **14 PASS / 2 FAIL** sur 16, non approuvé ;
- vérificateur : **18/18 PASS** ;
- ciblé final V6 : **16/16 PASS** ;
- régression sémantique/writer : **76/111**, puis **109/111**, puis
  **111/111 PASS** ;
- régression source-backed élargie : **614/615**, puis **615/615 PASS** ;
- suite client complète Debug x64 : **1 646/1 646 PASS**, zéro erreur,
  timeout, abandon ou non exécuté.

TRX complet : 10 172 lignes, 2 717 647 octets, SHA
`E7D70A04E642C1A66C93CA6F714595EB4A0FCA8CEC873D3C726C36FCA5B81BA8`.
Les DLL WinUI tests/produit sont byte-identiques, SHA
`F2EA1D15E838A271E00751F170CCB1A01B08375F3F9D430D81ACE0A54CA538B0`.

Protocole A544 : 167 lignes, 5 989 octets, SHA
`204E68F1D6A8E916775077D2FCC8EDF7BAB3C0814BD2E27AF7F851CAC006A0D7`.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A544-RAPPORT-GREEN-CONTRAT-SEMANTIQUE-V6.md`.

- 390 lignes, 13 861 octets ;
- SHA
  `E457633F2423E9F7618CC6C6CD1F3FAFD986770C43E0029F9375D36452EF9939`.

Audit technique : **181/181 PASS**.

- script : 273 lignes, 12 606 octets, SHA
  `1531BB826729CF0FD8E01EE9D9BF111ADAF3FE3BC7A7C5C124E3099F4BA9B4FA` ;
- sortie : 183 lignes, 10 534 octets, SHA
  `8BB58B0C0D7C79E4A62EA1C089C9C68295B3EA1092B6948DC980C8F10AA11702`.

Aucun live A544 n'a été exécuté. Les quatre artefacts live A539 restent
byte-identiques et le verdict `candidate_rejected_accuracy` ne change pas.
Après la suite, les serveurs MSBuild/VB-C# ont été arrêtés ; variables live et
processus ciblés sont à zéro. Branche `SAAIA_V3.1`, HEAD
`5f35881cdc67d12a076fcd2a7a1004656ac9a37a`, `git diff --check` à zéro
erreur avec treize avertissements historiques de fins de ligne.

Ce palier valide le contrat et sa plomberie, pas la qualité live. Produit
`TESTÉ_NON_APPROUVÉ`, BUG133 ouvert, promotion interdite. A545 devra être
préenregistrée puis vérifier end-to-end avec Qwen réel : décision, sélection,
leads, handoff, réponse, citations, EvidenceBundle, traces, cartes sources,
budgets et absence de fallback caché. Un test vert mais sémantiquement faux
restera non approuvé.

Telegram A544 est obligatoire avant l'ouverture opérationnelle d'A545.

Notification Telegram A544 envoyée et confirmée le **2026-08-30 à 12:47:47
+02:00** :

- titre : `SAAIA RAG - A544 contrat V6 green et 1646 tests verts` ;
- corps : 2 101 caractères ; total : 2 231 caractères ;
- une seule partie, code de sortie 0 ;
- message SHA
  `7F544B9CFD07E5C902A18CCB01F0F565C01E3334389183B44D2552F163F6620F` ;
- audit pré-envoi : **64/64 PASS** ;
- script pré-envoi SHA
  `8E6CFB3FE6FA0A8E1D5FABEAF0683EC745B9CE089D9EE3C7051F10FBE5869C26` ;
- sortie pré-envoi SHA
  `44097C280CD040A5E73DF8AA05C90C9563D4D7CCCF948781BD9AD49F39F843F7` ;
- reçu :
  `artifacts/goal-rag-product-20260827-1041/phase5/00-RECU-TELEGRAM-A544.md`,
  45 lignes, 1 789 octets, SHA
  `D5669B9BE96D18F56971AE5AFD7A9BF8CEE4098C0FFCB8DF500035A5B7074A6F`.

A544 est close, testée, auditée et communiquée. A545 est autorisée à ouvrir
uniquement après préenregistrement de son protocole end-to-end V6. Le produit
reste `TESTÉ_NON_APPROUVÉ`, BUG133 ouvert et promotion interdite jusqu'à preuve
live complète.

## 2026-08-30 — A545/A546 — préenregistrement et préflight live V6

Statut : **FAIT et TESTÉ**, non encore exécuté avec Qwen.

A545 a été préenregistrée pour mesurer causalement BUG133 sur le vrai chemin
résolution → handoff → writer → reviewer → vérificateur → publication, avec le
`EvidenceBundle` neutre gelé d'A537. Le verdict est limité à la séparation
lead/support et à son transport end-to-end ; corpus réel, navigation exacte et
WinUI restent hors de ce palier.

Protocole :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A545-LIVE-END-TO-END-CONTRAT-SEMANTIQUE-V6.md`.

- 288 lignes, 11 849 octets ;
- SHA
  `EA29733305918C3D9A9369446A03B19FCE6EC88D67454355F4AAF7D47E8E6782` ;
- une seule invocation Qwen autorisée après `-ArmOnly` et sceau final ;
- produit toujours `TESTÉ_NON_APPROUVÉ`, BUG133 ouvert.

Audit d'ouverture avant adaptation : **61/61 PASS**. Il a prouvé les SHA A544,
l'immuabilité A537/A539, l'absence du répertoire officiel, zéro variable live,
zéro processus ciblé, la branche et le HEAD attendus.

Le seul delta code A545/A546 est test-only :
`LiveSemanticResolutionWriterReviewV2CanaryTests.cs`, 2 334 lignes, 97 843
octets, SHA
`E4395C3C634E0ED42C4D635DB489FDF7936D936D1DC9FB96D9A6D18BE4773637`.

Il ajoute sans supprimer les profils historiques :

- observation explicite de `evidenceIds` et `leadEvidenceIds` ;
- conservation des deux listes brutes dans les artefacts ;
- reconstruction du seul ordre mécanique `leads + supports restants` ;
- vérification exacte de la trace résolution, de la trace handoff, du prompt
  writer, des citations ordonnées et de la réponse ;
- profil, variables, FQN et répertoire exclusifs A545 ;
- réutilisation byte-for-byte des quatre transactions A537.

Tests sans Qwen :

- harnais désarmé : **32/32 PASS** ;
- famille V6 : **25/25 PASS**, contenant les 16 oracles A543 ;
- sémantique/writer : **122/122 PASS**, contenant les 111 résultats A544 ;
- source-backed class-close : **999/999 PASS**, contenant nominativement les
  615 résultats A544 et les onze nouveaux A545 ;
- suite client complète Debug x64 : **1 657/1 657 PASS** en 25 secondes,
  contenant nominativement les 1 646 résultats A544 et les onze nouveaux A545.

Deux commandes intermédiaires ont sélectionné zéro test à cause d'un filtre
VSTest trop spécifique. Leurs TRX sont conservés comme sorties non probantes ;
elles n'ont ni lancé Qwen ni remplacé la preuve finale 25/25.

Audit A546 : **3 858/3 858 PASS**.

- script SHA
  `B38E865001226CF6B4145F7EF4B2634D482BBEC888767F501517E4C0EC0708AC` ;
- sortie SHA
  `BDC3A0C794088BB1A91B57F3D3F08BF89C971D10F58FC165648E1F79AC64E89B` ;
- rapport :
  `artifacts/goal-rag-product-20260827-1041/phase5/A546-RAPPORT-PREFLIGHT-LIVE-CONTRAT-SEMANTIQUE-V6.md`.

Les huit fichiers produit et les DLL WinUI sont inchangés. Les artefacts A537
et A539 restent byte-identiques. Après les tests, les build servers ont été
arrêtés ; variables live et processus ciblés sont à zéro. Le répertoire
officiel A545 est toujours absent.

Étape suivante : créer et auditer le wrapper fail-fast, obtenir un `-ArmOnly`
vert, sceller l'état, puis consommer exactement une campagne officielle Qwen.

## 2026-08-30 — A547/A548 — live V6 PASS causal et clôture de BUG133

Statut : **FAIT, TESTÉ et APPROUVÉ sur le périmètre causal déclaré**.

Le wrapper fail-fast A547 a été créé puis exécuté en `-ArmOnly` :

- `A545_ARMED=YES` ;
- zéro invocation officielle au moment de l'armement ;
- schéma attendu V6 ;
- 4 cas, 6–8 appels et maximum 10 000 tokens ;
- raw selection Kappa `{E1,E2}`, raw lead `[E2]`, sélection résolue
  `[E2,E1]` ;
- répertoire officiel absent, zéro variable et zéro processus ciblé.

Wrapper SHA :
`FBAEA439B449DC6CDDF00FB5CEF74C5BC06F3C60D14E7303BA54CE7BAF9A1DD1`.

Le sceau final a passé **82/82** avant toute création du répertoire officiel.
Sortie SHA :
`2411235BBD8D56BCB3FFD654E38E72BA70C288D9E5BE4A34B3BC345409111B73`.

Une unique invocation Qwen officielle a ensuite été consommée. Aucun replay
A537/A539 et aucune seconde invocation A545 n'ont eu lieu.

Résultat live :

- 4/4 cas exécutés et fonctionnellement exacts ;
- 4/4 protocoles V6 valides ;
- 4/4 décisions brutes conformes ;
- 6 appels LLM ;
- 7 210 tokens sur 10 000 maximum ;
- 138 830 ms de cas, sous la cible 180 000 ms ;
- une seule réponse publiée ;
- zéro fausse publication ;
- zéro publication sans reviewer `accept` ;
- un writer, un reviewer, un handoff, zéro réparation.

`txn_kappa` démontre causalement la correction :

- Qwen brut : `answer`, sélection `E1,E2`, lead explicite `E2` ;
- runtime : sélection writer `E2,E1` ;
- handoff : `decision_source=llm_evidence_judge`, `answer`, `E2,E1`, lead
  `E2`, reason transmise et fenêtre inchangée ;
- writer : Lambda 17/E2 puis Kappa 12/E1 ;
- réponse source-vérifiée et reviewer `accept` ;
- citations ordonnées E2,E1.

Les trois cas de sûreté sont exacts : prétention mondiale `research`, ancre
M-7 `context/E1 → documents_context`, délai absent `research`, sans writer ni
publication.

Artefacts officiels :

- JSON SHA
  `56E510414191B88D3D1BA4526B3D22224E1D9637E9A70791BAB9BF86F607E663` ;
- JSONL SHA
  `2ED0A78721121109296919E6F3F9670A98CF7DF5F17FB312E5C951B9E333893D` ;
- TRX SHA
  `B5A4F8BC07787FDBB62A4A28EA2BB08F36C20C87B13F9171CDAD2C5B0E4C4576` ;
- log SHA
  `B2C4140E5B80A4626A7B8C4A3CB535C799D72AD705B3FDD322D7B5BC7BF5DBAD`.

Audit final A548 : **283/283 PASS**.

- script SHA
  `09F09E7CA1E9552303FA0144A8CF8C7EFE59DB0566605A1ABA2B9B6AA8CB015D` ;
- sortie probante itération 2 SHA
  `845225DDB4ED9459B7E2C7EDF586FA046A56A285E948ED02A0F259093702E794` ;
- rapport :
  `artifacts/goal-rag-product-20260827-1041/phase5/A548-RAPPORT-A545-LIVE-V6-PASS-CAUSAL-BUG133.md`.

Une première exécution du script d'audit a échoué immédiatement sur un helper
PowerShell `SequenceEqual`; elle n'a modifié ni rejoué le live. L'itération 2
est la preuve probante.

Observations non bloquantes tracées : taxonomie de trace `ready/continue`
distincte de la décision V6 brute ; reason `txn_lambda` finissant à la borne ;
attributs TRX unitaires start/end incohérents malgré une duration et une
fenêtre globale correctes.

Verdict : **BUG133 fermé pour séparation lead/support, handoff et publication
comparative**. Le code n'a comparé ni 17 et 12, ni reconnu Lambda par domaine ;
Qwen a déclaré E2 comme lead.

Limite : ce PASS porte sur un `EvidenceBundle` synthétique gelé. Le produit
reste `TESTÉ_NON_APPROUVÉ` et la promotion demeure interdite. La prochaine
phase doit mesurer un vertical slice réel, jusqu'à la carte source WinUI
ouvrant le bon fichier, la bonne révision, la bonne page et le bon passage.

Notification Telegram A545 envoyée et confirmée le **2026-08-30 à 13:25:31
+02:00** :

- titre : `SAAIA RAG - A545 live V6 PASS causal BUG133 ferme` ;
- corps : 2 876 caractères ; total : 3 002 caractères ;
- une seule partie, code de sortie 0 ;
- message SHA
  `94C4A7053C7508E7013B9990BCBBA7F50CF4D9C100EFCF62FDB0D8B5AC331816` ;
- audit pré-envoi : **75/75 PASS** ;
- script pré-envoi SHA
  `1F0B9A2C5C129695CF66BB453AA177DA70B15BAA3149C7064DE678AC21A26F0B` ;
- sortie pré-envoi SHA
  `73B021D6EFC86B29FE016D83A136D485783C025B4671D188D2E6D0C4147B7D33` ;
- reçu :
  `artifacts/goal-rag-product-20260827-1041/phase5/00-RECU-TELEGRAM-A545.md`.

A545/A547/A548 sont clos, audités et communiqués. L'ordinateur n'est plus
utilisé par le runtime Qwen : zéro variable live et zéro processus ciblé. A549
peut ouvrir uniquement après préenregistrement de son vertical slice produit
réel.

## 2026-08-30 — A549 — matrice de clôture Phase 5 et protocole Q016 réel

Statut : **FAIT EN LECTURE SEULE — A550 SEUL AUTORISÉ**.

La matrice historique A209 a été recalculée après A548 sans transformer les
anciens lives ou les tests déterministes en preuves actuelles du binaire V6.
Sur les treize familles obligatoires, aucune ne peut encore être classée
`APPROUVE_ACTUEL` selon la définition stricte corpus + produit + binaire actuel
+ inspection humaine quand requise.

Matrice détaillée :
`artifacts/goal-rag-product-20260827-1041/phase5/A549-MATRICE-DE-CLOTURE-PHASE5-APRES-A548.md`.

Le plus petit vertical slice réellement discriminant est Q016 Cuisine :

- question : `Donne-moi la recette du fondant au chocolat en mode rapide.` ;
- A211 avait publié les profiteroles comme fondant ;
- A218 avait demandé une clarification inutile ;
- A221–A225 ont corrigé les deux causes mécaniques sans replay live ;
- BUG-115 reste donc ouvert live ;
- la preuve historique pertinente est le PDF
  `Cuisine/30-recettes-preferees-des-francais.pdf`, page 21.

Le protocole A549–A553 est gelé avant tout nouveau modèle :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A549-A553-VERTICAL-SLICE-REEL-Q016-CORPUS-WINUI.md`.

Il impose : A550 read-only et fail-closed ; une seule saisie Q016 dans une
conversation WinUI neuve ; aucun live CLI concurrent ; réponse, citations,
mémoire et payload audités ; clic conditionnel sur la carte ; vérification du
fichier, de la révision, de la page et du passage ; verdicts fonction,
provenance, UI et performance séparés ; aucun patch/rerun post-observation ;
Telegram et arrêt complet des ressources à A553.

Produit V6 et DLL WinUI sont gelés aux empreintes A544/A546. La banque Q016 est
gelée au SHA
`E71C24FDA0FD44E53170599DF463491D06C98306860F51D76218812EF459A823`.
Aucun appel Qwen n'a eu lieu pendant A549.

## 2026-08-30 — A550 — preflight Q016 corpus/WinUI 79/79 PASS

Statut : **FAIT EN LECTURE SEULE — A551 UNIQUE AUTORISÉ**.

Le harnais banque désarmé a sélectionné exactement le FQN attendu et terminé
1/1 PASS avec le message d'absence du flag live. Le préflight final a ensuite
passé **79/79** sans modèle ni action UI.

Le document Q016 est unique et inchangé : 36 pages, docId/path historiques,
hash fichier SHA-256 `aa11e234...d25c18`, révision `320e6aa5...d03fe88e4f`,
chunk `43e142f4...fb69aab9`, page 21, texte `fondant` et `chocolat`. La question
exacte retrouve la même preuve.

A550 a révélé trois représentations de hash légitimes mais différentes : hash
court du catalogue, `ContentHash` Base64 du détail et SHA-256 canonique du
contexte. Cinq sorties intermédiaires conservent les arrêts fail-closed dus à
deux confusions de contrat et à l'hypothèse erronée qu'un store de sessions
lisible devait être non vide. Le protocole a été amendé avant live pour exiger
la concordance des trois surfaces. L'itération 6 est la preuve probante.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A550-RAPPORT-PREFLIGHT-READONLY-Q016-CORPUS-WINUI.md`.

Sortie probante SHA :
`DA54E4F14F0492BFF0473A43C52DF4C58F7F520960E969DDCD7DA5A87394E9CF`.
Produit, banque, modèle et llama-server sont gelés ; persistance distante
lisible et vide ; contrôle Windows qualifié sans interaction ; zéro processus,
zéro variable live et dossier A551 absent.

A551 peut maintenant consommer une seule saisie Q016 dans une conversation
WinUI neuve. Aucun live CLI distinct, build, restore, patch post-observation,
second message ou replay n'est autorisé.

## 2026-08-30 — A551–A553 — vertical slice réel Q016 FAIL

Statut : **A551 TERMINÉ FAIL — A552 NON EXÉCUTÉE — A553 EN CLÔTURE**.

Le live officiel a été exécuté une seule fois dans une conversation WinUI
neuve avec la question exacte Q016. Session
`776560ee-f628-46e7-891d-8adb0e036207`, trace
`rag-20260830115811701-d56ce8ff`.

Le retrieval a retrouvé l'oracle canonique : document
`Cuisine/30-recettes-preferees-des-francais.pdf`, révision
`320e6aa5-5e2a-5aba-da4d-f1d03fe88e4f`, chunk
`43e142f4-ed55-c5b9-a399-f9a9fb69aab9`, page 21. La première revue Qwen a
retourné `ready` avec une ancre vérifiée décrivant le fondant, 10 minutes de
préparation et 8 minutes de cuisson.

Après une action `documents.content_cards`, la revue
`single_selection_scope_review_v1` a considéré plusieurs variantes comme
compatibles et a publié une clarification facultative sur le cœur-surprise ou
une recette plus simple. Aucune recette, citation, preuve utilisée ni carte
source n'a été livrée. Verdict : Q016 `FAIL`, BUG-115 `OUVERT`, exactitude
source-backed `FAIL`, identité/mémoire combinées `FAIL` malgré transport
d'identité correct.

A552 n'a pas été exécutée : zéro carte dans la bulle, donc aucun clic vers le
PDF. Carte et ouverture exacte sont `NON_EXECUTE`, sans conclure à un défaut du
contrôle UI.

Mesures officielles : 7 appels LLM, 7 895 tokens prompt, 521 completion,
8 416 total, 146 123 ms. Le seuil simple de 30 s échoue. Le pipeline reste
fail-closed : aucune fausse recette ni fausse citation publiée.

Cause : la politique actuelle impose `clarify` dès qu'au moins deux candidats
conviennent. Elle confond une information manquante qui rendrait la réponse
fausse avec la présence de plusieurs items valides lorsqu'un seul exemple
suffit. La correction doit rester générique et LLM-first ; aucun hardcode
Cuisine/Q016 n'est autorisé.

Le rapport causal est
`artifacts/goal-rag-product-20260827-1041/phase5/A551-AUDIT-CAUSAL-LIVE-WINUI-Q016-FAIL-SURCLARIFICATION.md`.
La décision conditionnelle est
`artifacts/goal-rag-product-20260827-1041/phase5/A552-DECISION-NON-EXECUTION-CARTE-SOURCE-Q016.md`.
Le rapport de palier est
`artifacts/goal-rag-product-20260827-1041/phase5/A553-RAPPORT-VERTICAL-SLICE-REEL-Q016-FAIL.md`.

Le runtime PID 27176 et WinUI ont été arrêtés ; aucun processus ciblé ni
variable live ne reste. Le corrigendum A550 précise que le snapshot à zéro
concernait l'identité aléatoire de préflight, tandis que l'identité persistante
WinUI avait 29 sessions.

Après audit A553 et Telegram, le prochain palier sera déterministe : tests RED
multi-domaines, correction de la politique de clarification, GREEN, régressions
et audit. Aucun replay A551 à l'identique n'est autorisé.

Audit A553 probant : **138/138 PASS**. Script SHA
`C20E8F4D191CC6B12C859F26D7EBE538C716CE082487A4071E9F1C8D5F979388` ;
sortie itération 6 SHA
`6B5294E50CF138385A2C512B95936F6A22245E4D91994CE7F66955BDB3770479`.
Les cinq arrêts antérieurs du vérificateur sont documentés sans Qwen, action UI
ou build. A553 reste en clôture jusqu'au reçu Telegram vérifiable.

Telegram A553 envoyé et confirmé le **2026-08-30 à 14:23:28 +02:00** en une
seule partie : corps 2 798 caractères, total 2 915, code 0. Message SHA
`3F2BD75F15B072F445AC4E2151B4F3E05D2C2D07697FE20BD5B28296723C8C06` ;
audit pré-envoi 55/55 PASS ; reçu
`artifacts/goal-rag-product-20260827-1041/phase5/00-RECU-TELEGRAM-A553.md`.

A549–A553 est **CLOS FAIL PROBANT**. La phase 5 et le Goal restent actifs. Le
prochain palier autorisé est uniquement déterministe et multi-domaines : figer
les contrats RED qui distinguent pluralité non bloquante et ambiguïté réellement
bloquante, corriger sans heuristique de domaine, passer GREEN et les
régressions, puis auditer avant tout nouveau live distinct.

Audit post-Telegram A553 : **38/38 PASS** ; sortie SHA
`2D8BA1C469D25B6A8119182E833F5250F4159B5E849FDC349834273F49CF0C86`.
A553 est entièrement clos, communiqué et sans ressources actives. Le travail
peut maintenant passer au nouveau palier déterministe de correction.

## 2026-08-30 — A554 — protocole clarification sélection unique V2

Statut : **PRÉENREGISTRÉ — A555 TESTS-ONLY AUTORISÉ APRÈS AUDIT A554**.

Le nouveau palier A554–A558 est strictement déterministe. Il corrige la cause
de Q016 sans rejouer le live : le LLM doit pouvoir accepter un item utile parmi
plusieurs matches lorsque la demande n'exige qu'un item, et ne clarifier que si
une information utilisateur manquante empêche réellement une réponse correcte.

Le code ne pourra plus convertir mécaniquement « deux matches » en
clarification. Il validera seulement le schéma, les IDs visibles, la cohérence
match/sélection, `NONE`, question et raison. Le contrat structuré V2 ajoutera
une raison traçable et distinguera pluralité non bloquante, ambiguïté bloquante,
candidat unique et contrat invalide.

Portefeuille obligatoire : procédure technique avec deux réponses valides,
sélection valide non première, conformité dépendante d'une région inconnue,
accept incohérent, clarify incohérent, classification incomplète, candidat
unique et clarification directe existante. Aucun terme de domaine ne sera une
règle du runtime.

Protocole :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A554-A558-POLITIQUE-CLARIFICATION-SELECTION-UNIQUE-V2.md`.

A554 fige la baseline ; A555 produit RED tests-only ; A556 applique le GREEN
minimal ; A557 exécute les régressions ; A558 audite, rapporte et notifie
Telegram. Aucun appel Qwen, WinUI ou live backend n'est autorisé dans ce palier.
Même un PASS déterministe ne fermera pas BUG-115 live et ne rendra pas le
produit approuvé.

Audit baseline A554 : **44/44 PASS**, zéro Qwen et zéro action UI. Script SHA
`83999697D7307143D309BA9549B7C1E813FBC6AC563407D514FE7923D9163AA1` ;
sortie SHA
`7577E5970EB35F71D5EB9704B43C375448F1BF106422EA22C6A4776A84059AB8`.
Les fichiers produit/test sont encore exactement aux empreintes préenregistrées,
la normalisation V1 fautive est présente et la taxonomie V2 absente. A554 est
complet ; A555 tests-only RED est autorisé.

## 2026-08-30 — A555 — RED sélection unique V2

Statut : **RED CAUSAL 0/6 — TESTS-ONLY**.

Six oracles `SingleSelectionScopeV2_*` ont été ajoutés : acceptation d'une
procédure parmi deux matches, acceptation d'un match visible non premier,
clarification de conformité quand la région détermine réellement le bon choix,
clarify avec ID interdit, accept sur nonmatch interdit et classification
incomplète interdite.

L'itération probante bornée à un tour termine 0/6. Les deux acceptations restent
non vérifiées, la clarification bloquante est absente et les contrats invalides
n'ont pas encore la taxonomie `invalid_scope_review`. Le parseur V1 rejette le
cinquième champ `reason` ; la normalisation `deux matches => clarify` reste
également intacte dans le produit.

TRX :
`artifacts/goal-rag-product-20260827-1041/phase5/A555-RED-SINGLE-SELECTION-SCOPE-V2-ITERATION-2.trx`,
SHA `81AE8BB8133FD541507CAFC634186BCE84FE4026BD1B0F98E4DE2F496BF34F0D`.

Les quatre fichiers produit surveillés sont byte-identiques à A554. Seul le
fichier tests change, SHA
`F10754CA704CD452CE10F76A21523FABB9920CCB2862CCFE79F3DF20008AEF30`.
Zéro Qwen/UI/backend live ; build servers arrêtés ; `git diff --check` PASS.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A555-RAPPORT-RED-POLITIQUE-CLARIFICATION-SELECTION-UNIQUE-V2.md`.
A556 GREEN minimal est autorisé après audit du RED.

Audit A555 : **58/58 PASS**. Script SHA
`F87A7481AAD27D544984223122C06716A5951F3DB9E42C445C6F43FB1C9D7F32` ;
sortie probante itération 2 SHA
`4AAF2087AE73E196B977A68FD28D2D2E483205E92B989AF826F6E33B9DEA04B0`.
Le RED 0/6, l'absence de changement produit, les six FQN, les causes V1, les
interdits live et l'arrêt des ressources sont authentifiés. A555 est complet ;
A556 GREEN minimal est ouvert.

## 2026-08-30 — A556–A557 — GREEN sélection unique V2

Statut : **PASS DÉTERMINISTE — BUG-115 NON REVALIDÉ LIVE**.

Le contrat `source_backed_single_selection_scope_review_v2` porte cinq champs,
dont une raison sémantique tracée. Qwen peut accepter le candidat sélectionné
lorsqu'un seul item suffit, même si plusieurs candidats sont `match`. Il peut
clarifier si une information utilisateur manque réellement. Le code valide
uniquement les IDs visibles, match/nonmatch, `NONE`, question et raison ; il ne
convertit plus deux matches en clarification.

GREEN final 6/6 ; classe source-backed 313/313 ; clarification, mémoire,
contrat et architecture 70/70 ; suite complète Debug x64 1 662/1 662. Les DLL
WinUI produit/tests sont identiques au SHA
`17FC484E7BB48A3E21811DA8AF72C212865330852487E5CF4B353A1E8C3BDF77`.

Deux incidents sont conservés : restauration de deux `MaximumTurns=1`
historiques après une première classe 311/313 ; compaction de deux champs de
trace après une garde architecturale 69/70 à 2 502 lignes. Le runner final reste
à 2 500 lignes. Une assertion de prompt sensible au retour ligne a aussi donné
5/6 avant correction de l'oracle ; l'itération finale est 6/6.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A557-RAPPORT-GREEN-POLITIQUE-CLARIFICATION-SELECTION-UNIQUE-V2.md`.

La correction est générique et sans règle de domaine ajoutée. Le mot recette
reste dans une ancienne phrase du prompt fast review, antérieure à A554 et hors
delta ; il n'est pas utilisé par la revue V2. Zéro modèle/UI/backend live et
zéro ressource active. A558 doit auditer et notifier Telegram avant tout nouveau
palier. Produit `TESTE_NON_APPROUVE`.

## 2026-08-30 — A558 — audit final clarification sélection unique V2

Statut : **AUDIT 140/140 PASS — TELEGRAM À SCELLER**.

Le palier A554–A558 démontre le passage causal des six oracles de 0/6 RED à
6/6 GREEN. Les régressions finales passent 313/313, 70/70 et 1 662/1 662. Le
contrat V2 à cinq champs, la taxonomie bloquant/non bloquant, la raison tracée,
`normalized=false`, l'absence de normalisation par nombre de matches et les
empreintes source/binaires sont authentifiés.

Audit :
`artifacts/goal-rag-product-20260827-1041/phase5/a558-audit-final-clarification-selection-v2.ps1`,
SHA
`AAAD6853156248F6C6AE0360306E562C3EE07113A8D08B8E49CD9DA5E3DC6549`.
Sortie 140/140 :
`artifacts/goal-rag-product-20260827-1041/phase5/A558-SORTIE-AUDIT-FINAL-CLARIFICATION-SELECTION-V2.txt`,
SHA
`DFD5030E9DE31529CBCBDCE520417F28C85CE46F6201C46721BAF83577229C8D`.
Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A558-RAPPORT-AUDIT-FINAL-CLARIFICATION-SELECTION-V2.md`.

Une première exécution de l'auditeur a échoué sur une différence d'accents
dans le libellé canonique `TESTE_NON_APPROUVE`. Seule l'assertion d'audit a été
corrigée ; aucun produit, test ou TRX n'a changé. Les incidents 311/313, 69/70
et 5/6 restent conservés comme itérations non promues.

BUG-115 devient `CORRIGE_DETERMINISTEMENT_NON_REVALIDE_LIVE`. Aucun Qwen,
WinUI ou backend live n'a été appelé dans A554–A558. Les ressources sont
arrêtées, `git diff --check` passe et le produit demeure
`TESTE_NON_APPROUVE`. A558 ne sera clos qu'après audit pré-Telegram, envoi,
reçu et audit post-Telegram. Ensuite seulement, un canari live distinct pourra
être préenregistré.

Audit pré-Telegram A558 : **48/48 PASS**, sortie SHA
`3BD678EBD016052A72F33C122C2DB508475C395D39950D93E63BBB6CEDC14C49`.
Telegram envoyé et confirmé le 2026-08-30 à 15:00:37 +02:00 : corps 2 474
caractères, total 2 585, message SHA
`2153F64304AB2A8E47549AB5FD8019AB07B73948D529B6453A80E445DA2996A6`.
Reçu :
`artifacts/goal-rag-product-20260827-1041/phase5/00-RECU-TELEGRAM-A558.md`.
Le statut communiqué est exactement
`CORRIGE_DETERMINISTEMENT_NON_REVALIDE_LIVE`; le produit reste
`TESTE_NON_APPROUVE`. L'audit post-Telegram doit encore sceller ces preuves
avant fermeture d'A558.

Audit post-Telegram A558 : **41/41 PASS**. Script SHA
`A20C729E1996A2338A26547F182341620449C1A9FD5FE301D70DFB0CA3C6A995` ;
sortie SHA
`22C2306B22FE2FC1959764C5950934C7FB2CBD55159ED082529C5CF587F7289F`.
Le reçu, le message, les audits 140/140 et 48/48, les statuts, Git et l'absence
de ressources sont authentifiés. **A554–A558 EST CLOS PASS DÉTERMINISTE.**
BUG-115 reste `CORRIGE_DETERMINISTEMENT_NON_REVALIDE_LIVE`; produit
`TESTE_NON_APPROUVE`. Le prochain palier autorisé est uniquement la sélection
et la préinscription d'un canari live distinct, puis son preflight readonly.

## 2026-08-30 — A559 — sélection et préinscription du canari C001

Statut : **C001 PRÉENREGISTRÉ — A560 AUDIT SEUL AUTORISÉ**.

Deux candidats ont été refusés avant live. Q024 ABB Model 266 n'existe pas dans
le catalogue courant et renvoie seulement des résultats 3M hors cible.
DT-Q016 3M trouve son document exact, mais uniquement comme hit `docmeta:`
unique sans contenu ; il ne démontrerait pas la branche V2 multiple.

Le canari retenu est C001 :
`Donne-moi un exemple de certificat présent dans le corpus pour le PTFE TF1620. Résume en une phrase ce qu'il atteste et cite la source.`
Question SHA
`400A2056C9D2B4A523A5E7F56A6535248A58EF975B4BBAAEA9BD398E858500F3`.

La découverte readonly retourne 20 éléments, huit hits attendus et quatre
documents certificats/réglementaires distincts. La meilleure ancre directe est
le certificat USP Class VI page 1, mais aucun ordre n'est imposé à Qwen. Le
live devra répondre avec exactement un document courant et tracer
`multiple_matching_candidates_non_blocking`, au moins deux matches,
`decision=accept`, une raison, une question vide et `normalized=false`.

Protocole :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A559-A564-CANARI-LIVE-DISTINCT-C001-CERTIFICAT-PTFE.md`.
Rapport de sélection :
`artifacts/goal-rag-product-20260827-1041/phase5/A559-RAPPORT-SELECTION-CANARI-C001-CERTIFICAT-PTFE.md`.
Discovery C001 script SHA
`A8E81D87CAE7DA839AB3A3F2071A6113F5061BF68445A1C20EA21D605537335C` ;
sortie SHA
`C7E8346427585568463FB1AFED83368C4E3522B044CFE0F61790A2EBCE3BB524`.

Budgets préinscrits : une invocation officielle, maximum 9 appels LLM,
12 000 tokens et 240 secondes ; zéro replay. A559 a consommé zéro appel Qwen,
zéro action UI et laisse zéro processus ciblé. A560 doit auditer ce gel avant
tout preflight ou live.

Audit A560 final : **86/86 PASS**. Script SHA
`A40297645D22FBDC287D713C7D4073A60717B7E80F430D00C0FF7C3C149AEEB0` ;
sortie itération 3 SHA
`E3C3AB09F1E634F6BAA050597555672C9D40AEE7A63F5E36B92FF8074E6E2890`.
L'itération 1 a corrigé un oracle statique trop strict sur un extrait tronqué ;
l'itération 2 a imposé la question exacte sur une ligne UTF-8. Aucun produit,
oracle sémantique ou budget n'a changé. **A559–A560 EST CLOS PASS** ; A561
preflight readonly est ouvert, toujours sans Qwen ni UI.

## 2026-08-30 — A561 — préflight readonly C001

Statut : **105/105 PASS — A562 ARM-ONLY AUTORISÉ**.

Le préflight a authentifié le protocole A559–A564, le rapport de sélection,
l'audit A560 86/86, la qualification computer-use, les sources/runtime/UI/ADR,
les binaires validés par la suite 1 662/1 662, la configuration, le modèle et
llama-server. Le backend répond et la clé API est présente sans avoir été
imprimée.

La découverte readonly A561 retrouve 20 éléments, huit hits attendus et quatre
documents distincts. Hors empreintes volatiles `/ready` et réponse RAG, ses 35
lignes sémantiques stables sont byte-identiques à A559.

Audit : **105/105 PASS**. Script SHA
`01895A489A27986EE13D4ECC30DE2D02AA363B2DD332B84E7923371974F73252` ;
sortie SHA
`B614374B83FEA6D724745EDB186C10F61A591B1D948D6D052701D6806ECE1177`.
Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A561-RAPPORT-PREFLIGHT-READONLY-C001-LIVE-WINUI.md`.

A561 a consommé zéro appel Qwen, zéro action WinUI et zéro build. Avant A562 :
zéro processus cible, zéro listener 1234, zéro session persistée et dossier
officiel absent ; branche `SAAIA_V3.1`, HEAD
`5f35881cdc67d12a076fcd2a7a1004656ac9a37a`, `git diff --check` PASS.

A561 est clos. Le seul palier ouvert est A562 ARM-ONLY : sceller et exécuter à
blanc les scripts exacts de démarrage, arrêt et collecte, sans répertoire
officiel, sans Qwen, sans UI et sans question. Le live officiel unique restera
interdit jusqu'au PASS de cet armement.

## 2026-08-30 — A562 — armement à blanc C001

Statut : **76/76 PASS — LIVE OFFICIEL UNIQUE À SCELLER**.

Les scripts de démarrage/snapshot, arrêt et collecte sont parsables, scellés et
ont chacun terminé en mode `ArmOnly`. Aucun dossier officiel, processus,
listener 1234, environnement live, nouvelle session, appel Qwen ou action UI
n'a été créé. Les réglages et le dernier ID sont inchangés.

La collecte ne code aucune réponse attendue : elle conservera la réponse, les
sources brutes et parsées, le tracking, les traces scope V2/writer/verifier et
les budgets réellement observés. Le démarrage mesure les vraies sessions de
l'identité persistante ; l'arrêt valide dynamiquement PID, profil et chemin.

Audit final : **76/76 PASS**. Scripts SHA : démarrage
`6E3846C05C17593C3B5549F32A92DAD2CC392085F0C5D67FD2D5C4E4AEC8D5FA`,
arrêt
`BB61AFD07EF573FD237D9747DBCB425D49CFC0FCF7584BD50C7E8F550E2C37AD`,
collecte
`AD745282E5A734D1B42A270F25D10934750F1BAABAB0EECA272B3C8BAE306BBF`,
auditeur
`0D7B0C60E1A6EB2013051237D0B1C3E1EE5E5170AD11B27B91C29A9F1BE6AE51`.
Sortie SHA
`41C1C45C5E4085AD33240583614567F0C096570A12AFFEFB4D5655E810C26167`.

Le delta journal d'armement mesure 394 octets, SHA
`D078DC94EF39D941C0791BD19336D8DBAD310E2C2410EAB5597C22A092BD6254` :
uniquement deux avertissements `SecureLocalStore` de fallback fichier, sans
LLM, RAG, tour ni question.

Correction de lecture A561 : son compte zéro provenait d'une identité API
aléatoire isolée. La baseline de l'identité persistante WinUI est une session,
IDs SHA
`98C41E8EB74AC0DDD3540853942F7C4906C22692C0458E280F5EA9F303DFA030`.
Erratum :
`artifacts/goal-rag-product-20260827-1041/phase5/A561-ERRATUM-IDENTITE-SESSIONS.md`.

Quatre itérations non promues sont conservées : chemin Contracts erroné,
méthode interne de sessions inadaptée à l'identité persistante, puis deux fois
l'oracle de journal byte-identique trop strict. Aucune n'a lancé Qwen/UI ni
créé le dossier officiel.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A562-RAPPORT-ARMEMENT-C001-LIVE-WINUI.md`.
Après un dernier sceau local de ces preuves et de l'absence de ressources,
l'unique live officiel A562 C001 sera autorisé. Zéro replay reste la règle.

## 2026-08-30 — A562–A563 — live WinUI C001

Statut : **FAIL PROBANT — BUG-134 OUVERT — A563 INAPPLICABLE**.

Le sceau final avant live passe 46/46. Le runtime exact a mesuré 29 sessions
persistantes réelles avant l'invocation. Une seule fenêtre SAAIA a été lancée,
une seule nouvelle discussion créée et la question C001 exacte soumise une
seule fois. La tentative UIA `set_value` a échoué sans écrire ; l'observation a
confirmé le champ vide avant la saisie unique par `type_text`. Aucun replay.

Après 171 059 ms, la WinUI a répondu exactement :
`Les sources consultees ne suffisent pas pour produire une reponse fiable et sourcee.`
Zéro source et zéro bouton `Ouvrir` : A563 est inapplicable. La session
`1d0ceac3-5d93-4d49-a8b8-f381befda2e9` et la trace
`rag-20260830134438808-19d3cbde` sont persistées avec deux messages ; 29
sessions avant, 30 après.

Cause : le planificateur a inventé `categoryPath="Certifications"`. Deux
`rag.search` dans ce scope ont renvoyé zéro hit, puis une troisième identique a
été rejetée comme doublon. Le garde d'absence de progrès a terminé en
`insufficient_evidence`. Cela contredit la découverte A559 non scopée : 20
résultats, huit hits attendus, quatre certificats sous `Documents techniques`,
dont une ancre USP directe au rang 1.

**BUG-134 — scope de catégorie sémantique non validé contre le catalogue** est
ouvert. La revue de sélection V2 n'a jamais été appelée, le writer n'a jamais
été atteint et aucune EvidenceBundle finale n'a été produite. BUG-115 reste
`CORRIGE_DETERMINISTEMENT_NON_REVALIDE_LIVE`.

Budgets : 8/9 appels PASS ; **13 764/12 000 tokens FAIL** (+1 764, +14,7 %) ;
171 059/240 000 ms PASS ; une soumission et zéro replay. Produit
`TESTE_NON_APPROUVE`.

Le collecteur postrun a nécessité quatre passes sur les mêmes octets : CR de
snapshot, wrapper REST révélant la vraie cardinalité 29, titre long abrégé,
puis question Unicode échappée dans `RAG_TRACE`. L'égalité du message utilisateur
persisté et la ligne ToolAgent non échappée authentifient la question. Aucun
produit/test et aucun live n'ont été relancés.

Observation UI :
`artifacts/goal-rag-product-20260827-1041/phase5/A562-OBSERVATION-WINUI-C001.md`.
Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A562-RAPPORT-LIVE-C001-FAIL-SCOPE-CATEGORIE.md`.

La WinUI est fermée, Qwen arrêté, listener 1234 absent et `git diff --check`
PASS. A564 doit auditer, notifier Telegram et clore le palier. Ensuite seulement,
un palier déterministe préenregistré pourra traiter BUG-134 ; aucun second live
C001 n'est autorisé.

## 2026-08-30 — A564 — audit final du live C001

Statut : **120/120 PASS — FAIL LIVE AUTHENTIFIÉ — TELEGRAM À SCELLER**.

L'auditeur final a authentifié le protocole, les sorties A559–A562, l'unique
session persistée, la trace, les deux recherches scopées à zéro hit, le scope
inventé `Certifications`, le doublon rejeté, l'arrêt mécanique, l'absence de
revue V2/writer/carte source, les budgets et l'arrêt de toutes les ressources.
Il confirme `FAIL_PROBANT`, `BUG-134=OPEN`, A563 inapplicable,
`BUG-115=CORRIGE_DETERMINISTEMENT_NON_REVALIDE_LIVE` et produit
`TESTE_NON_APPROUVE`.

Script SHA :
`C1B551411D840973F1F54F1D52D7354A5394D8753E8DBC2319F91CC7BCDBC50E`.
Sortie **120/120 PASS**, SHA :
`2487B1BF431D6FBED488BB027D5F5D35E47BFEE4236227CAEB5BA40470AC1116`.
Deux itérations d'audit non promues sont conservées : chemin de fichier erroné,
puis empreintes source reconstruites à partir d'un affichage tronqué. Elles
n'ont modifié ni le produit ni les octets du live.

Audit pré-Telegram : **54/54 PASS**. Deux itérations non promues se sont
arrêtées sur un oracle textuel trop littéral ; message et preuves sont restés
byte-identiques. Telegram a été envoyé une seule fois et confirmé le
2026-08-30 à 16:04:35 +02:00 : corps 2 560 caractères, total 2 668, message
SHA
`59E956777ECC9A0B6D434DEA793FFD6AC5B3C84DA6DF21A86EB3DE8BAA4F72DA`.
Reçu :
`artifacts/goal-rag-product-20260827-1041/phase5/00-RECU-TELEGRAM-A564.md`.

L'audit post-Telegram doit encore sceller le reçu, le message, les audits et
l'absence de ressources. Aucun Qwen, WinUI, build ou replay C001 n'est
autorisé. Après fermeture, BUG-134 passera dans un palier déterministe
préenregistré qui préservera Qwen comme propriétaire sémantique.

Audit post-Telegram : **44/44 PASS**. Script SHA
`3C52898A6A08D83B403B6D087EEAE3E1B0CD21D08D4B2EFF25F6AD6D528F2429` ;
sortie SHA
`711DB5598A51F24F557C44B2EE2CD5D1895E90BE29D958A680B42B5C5973242E`.
Le reçu, le message, les audits 120/120 et 54/54, les statuts, Git et l'absence
de ressources sont authentifiés. **A559–A564 EST CLOS FAIL PROBANT.**
BUG-134 reste ouvert ; BUG-115 reste
`CORRIGE_DETERMINISTEMENT_NON_REVALIDE_LIVE` ; produit
`TESTE_NON_APPROUVE`. Le prochain palier autorisé est la préinscription
déterministe de la correction générale de BUG-134, sans replay C001.

## 2026-08-30 — A565 — erratum causal BUG-134

Statut : **DIAGNOSTIC CORRIGÉ — ERRATUM TELEGRAM À ENVOYER**.

Contrairement à la formulation A562/A564, `Certifications` n'est pas absent du
catalogue. La preuve A149 le listait avec sept documents ; le catalogue courant
readonly compte 31 catégories et confirme `Certifications=7` ainsi que
`Documents techniques=13`.

La cause corrigée est un **scope catalogue valide à rendement nul qui reste
collant et ne permet pas un pivot informé**. Le live A562 conserve deux
`hits=0` dans `Certifications`, un doublon rejeté puis l'arrêt ; A559 conserve
20 résultats, huit hits attendus et quatre documents sous `Documents
techniques`. Le code ne doit pas décider que l'un de ces scopes est
sémantiquement pertinent. Il doit exposer à Qwen le scope exécuté, son existence
catalogue, le rendement nul, les scopes exacts et les options autre scope/corpus
complet.

Diagnostic readonly : script SHA
`9796E67DE417FDE3A3F195F909567BCB99DFD998F153249B3A9F49000AE9578B` ;
sortie SHA
`84B7D6BDCA9EF6E816188E5587BC241F5BA82864D07307667654A0ED426621B6`.
Les trois recherches de rafraîchissement ont toutes reçu HTTP 500, incident
backend transversal séparé. Zéro Qwen, UI ou contrôle d'écran. L'erratum
`A565-ERRATUM-A562-CATEGORIE-CERTIFICATIONS.md` devient l'autorité sur le
diagnostic. BUG-134 reste ouvert et C001 ne sera pas rejoué.

Audit pré-envoi de l'erratum : **39/39 PASS**. Telegram envoyé une seule fois
et confirmé le 2026-08-30 à 16:20:00 +02:00 : corps 2 202 caractères, total
2 314, message SHA
`375E779BE9C92D0002A25536FBBEF9D18DB9855C6EAD0C5140720C7955A1A367`.
Reçu :
`artifacts/goal-rag-product-20260827-1041/phase5/00-RECU-TELEGRAM-A565-ERRATUM.md`.
L'audit post-envoi doit encore authentifier cette correction avant la
préinscription RED.

Audit post-Telegram de l'erratum : **22/22 PASS**. Script SHA
`9BEA462A06B1AEE9BA6B4857FC8AD90D02F5A0C616A29B5C89491031AE28EE11` ;
sortie SHA
`ADD6323FC8C24EE5B5EB5E6EA60A8B900766B49FC4E1FE1D5CF6FFD9B0E676D6`.
**A565 EST CLOS CORRIGÉ.**

## 2026-08-30 — A566 — préinscription correction BUG-134

Statut : **PROTOCOLE GELÉ — A567 RED SEUL AUTORISÉ**.

Le contrat préinscrit ne valide pas la pertinence d'un scope. Il ajoute à
l'observation compacte, uniquement après succès technique à zéro preuve dans
un scope explicite, le scope exécuté, son statut dans le catalogue visible, les
chemins exacts visibles, trois options de récupération et
`semanticDecisionOwner=llm`. Aucun scope de remplacement ne sera recommandé.

Fichiers produit autorisés : compacteur d'observation, transport/trace du lot
documentaire et prompt système général. Oracle :
`SourceBackedCategoryScopeYieldRecoveryTests.cs`, huit tests attendus **0/8
RED**, puis **8/8 GREEN**. Protocole :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A566-A570-BUG134-RECUPERATION-SCOPE-ZERO-YIELD.md`.
Zéro modification produit n'a encore été effectuée ; C001 reste interdit au
replay.

## 2026-08-30 — A567 — RED causal BUG-134

Statut : **0/8 RED ATTENDU — CAUSE TESTÉE AVANT PRODUIT**.

La commande ciblée du protocole a exécuté huit oracles et produit exactement
huit échecs, tous arrêtés sur l'absence de la surcharge publique `Build` à sept
paramètres. Le RED prouve donc que le contrat `scopeYield` n'existait pas dans
la baseline ; aucune logique produit n'avait encore été modifiée.

Sortie SHA
`72D6DD48CC06EF8D84962168FC75FEC7AA067F7CEAA1E0068DC28F00A48D96B6` ;
TRX SHA
`0DA4680CB88E1E6914EBCB99EF48D077BE2DE1055C299BE88CF01CE5A0EECA23` ;
oracle SHA
`296ACC555DFBDC60FE8DFB2D38AFA03705E41600F4423AC780C395714C8B556C`.
Baselines produit compacteur/lot/prompt :
`124C7545F84D6796C2F23FBE4AF9E230D60D25B388FEBEB03F817F79A0AC2939`,
`71743B94460E081399E18E15BE110DA6279E5068AA68F7672D75142C62EA282D`,
`A8A21B796689000A13D84D8EE1E1E8F40048172409DC1BA8B1675C8DD4112108`.

A568 est autorisé sur les trois fichiers produit préinscrits seulement. Aucun
Qwen, backend, contrôle d'écran ou replay C001 n'est requis.

## 2026-08-30 — A568 — GREEN ciblé BUG-134

Statut : **8/8 PASS — CORRECTION MINIMALE APPLIQUÉE**.

Le même filtre causal passe désormais huit tests sur huit, zéro échec, en
28 ms. Le bloc `scopeYield` est borné aux recherches RAG réussies, avec scope
explicite et zéro preuve ; les erreurs, états busy, appels non scopés et
observations porteuses de preuves ne le reçoivent pas. Il expose le scope
exécuté, son appartenance au catalogue visible, les chemins exacts sans ordre,
trois options de récupération et `semanticDecisionOwner=llm`. Il n'expose
aucun `recommendedCategoryPath`.

Le lot documentaire transporte les arguments exécutés et produit une trace
mécanique. Le prompt explique que zéro preuve dans ce scope ne signifie pas
corpus vide, puis laisse Qwen choisir entre retrait du scope, autre chemin
exact, autre requête/outil ou insuffisance après exploration utile. Aucun terme
PTFE, certificat, catégorie particulière ou réponse attendue n'a été ajouté.

Sortie GREEN SHA
`B398269A99E39F85E8A95379E1138B3654D4025E93E8A91183F9C7503360D1F0` ;
TRX SHA
`0E42A67722DB025FF49EE65927043632CEB2D0BD989A7519CC863028BC5D7CE7`.
Empreintes produit compacteur/lot/prompt :
`9316BCD82F55DACE82D3747520BAF8F92160BC321564A93479A9E520FD28C953`,
`08A4C4AD9315BA2F410EA925C54A87634F593D735FF56A43C08EF18A4D068FB8`,
`C4C281839B5AF62698C7A93ADC22098817162ED55ECDF832E094EC800CE7256F`.

A569 est autorisé. Le contrôle d'écran reste fermé ; aucun Qwen, backend ou
WinUI n'a été lancé.

## 2026-08-30 — A569 — régressions BUG-134

Statut : **PASS DÉTERMINISTE — A570 AUDIT AUTORISÉ**.

Les portes finales passent : classe `SourceBackedAgentV2Tests` **313/313**,
portefeuille clarification/mémoire/contrat/architecture/source **70/70**, suite
cliente complète Debug x64 **1 670/1 670** en 26 secondes. Le total augmente
exactement des huit nouveaux oracles par rapport à 1 662.

Une première itération du portefeuille est conservée à 69/70. Elle a détecté
le lot documentaire à 528 lignes. Le delta de transport/trace et le record ont
été reformatés sans retirer d'information ni relever la limite ; le fichier
final compte exactement 500 lignes et l'itération 2 passe 70/70.

Empreintes TRX promues : classe
`02855E2CBC8434BE33501522B9A4E98154B4CADCA4A0DAE7CE116DFA2DFBF1E2`,
portefeuille
`1B68B10239C60C9B33F2E20B4320F6498D1C409043314D2CCE742B53DB942BFD`,
suite
`03BA1E5412D4332709C65656150CA51F9117FDD13A7B134DFBE90CBC047F83A3`.
DLL WinUI produit/tests byte-identiques, 8 368 640 octets, SHA
`A2B3DE381D2EB82C6B145C3B87FDB768E01DB5925F89E712822AF70DF3796226`.

Le serveur de compilation PID 33604 a été arrêté après identification exacte.
Zéro processus cible et zéro listener 1234. `git diff --check` PASS ; branche
`SAAIA_V3.1`, HEAD `5f35881cdc67d12a076fcd2a7a1004656ac9a37a`.
Zéro Qwen, backend, WinUI, contrôle d'écran ou replay C001.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A569-RAPPORT-REGRESSIONS-BUG134-RECUPERATION-SCOPE-ZERO-YIELD.md`.
A570 doit encore auditer indépendamment les preuves, communiquer le palier sur
Telegram, sceller le reçu et conserver le produit `TESTE_NON_APPROUVE`.

## 2026-08-30 — A570 — audit final BUG-134 avant Telegram

Statut : **174/174 PASS — TELEGRAM À SCELLER**.

L'auditeur final authentifie l'erratum A565, le protocole A566–A570, le RED
0/8, le GREEN 8/8, les régressions 313/313, 70/70 et 1 670/1 670, les huit
oracles nommés dans RED/GREEN/suite complète, les sources finales, la parité
des DLL, les bornes architecturales, l'anti-hardcoding, l'autorité sémantique
LLM, le statut Git et l'arrêt des ressources.

Script SHA
`2F240F9333DB60BAA1D9DA0A65344FCF5A6DBE7C1895355FDE933B13891EB912` ;
sortie **174/174 PASS**, SHA
`36CFAA7D4EF188AFEE97B17FC3E9D99572922A3596F4BB5367E745DDFFA34F0B`.

Cinq sorties d'auditeur non promues sont conservées. Elles ont révélé deux
incompatibilités Windows PowerShell 5.1, deux comparaisons Unicode trop
littérales, une erreur réelle de transcription du SHA de la DLL tests dans le
rapport, puis les avertissements CRLF de Git traités comme exception. Le SHA a
été corrigé dans le rapport ; aucun produit, test, TRX ou binaire n'a changé.

Décision technique : BUG-134 devient
`CORRIGE_DETERMINISTEMENT_NON_REVALIDE_LIVE`. BUG-115 conserve ce statut.
Produit `TESTE_NON_APPROUVE`. C001 non rejoué. Zéro Qwen, backend, WinUI,
contrôle d'écran, processus cible ou listener 1234.

Message Telegram préparé :
`artifacts/goal-rag-product-20260827-1041/phase5/telegram-palier-a566-a570-bug134-scope-zero-yield-20260830.txt`.
Un audit pré-envoi doit encore sceller son contenu, puis un unique appel au
notificateur sera autorisé. A570 restera ouvert jusqu'au reçu et à l'audit
post-Telegram.

Audit pré-Telegram : **49/49 PASS**. Script SHA
`210670360C579F2FB67EBFC62D03C8228899257D633D3072873D4BB1A2F3C029` ;
sortie SHA
`71BA4758C106D6FF9860B7F9DD27B3C481105D3548C751C4DB5807F941F14DC3`.
Message SHA
`AC6DD20C33CC44A7431D9B2CD61E92E056928CFD5D5FE63DD68671FFD4D6199C`,
corps 3 007 caractères, sans secret ni mojibake.

Telegram envoyé une seule fois et confirmé le 2026-08-30 à 16:45:58 +02:00.
Sortie exacte : `Notification Telegram envoyee. Corps=3007 caracteres.
Total=3129 caracteres.` Reçu :
`artifacts/goal-rag-product-20260827-1041/phase5/00-RECU-TELEGRAM-A570.md`.

L'audit post-Telegram doit encore authentifier le reçu, le message, les audits,
les statuts et l'absence de ressources avant la fermeture d'A570. Aucun nouvel
envoi et aucun live ne sont autorisés pendant ce scellement.

Audit post-Telegram : **42/42 PASS**. Script SHA
`B3411711B3414F13BC61D4F3BCA6207F2525D239A7575AFD6CDA0DE5E2961E6E` ;
sortie SHA
`82FD872F22699B8ACE3FA969F953E17B2954B2FFE1D7014A61971C8C9D630F01` ;
reçu SHA
`92A75EC0BC8D1560CF47260564BB6C3FFCB2427545D5798B3938790F82B5B8D2`.

Le reçu, le message, les audits 174/174 et 49/49, les quatre sources du
contrat, les statuts, Git et l'absence de ressources sont authentifiés.
**A566–A570 EST CLOS PASS DÉTERMINISTE.** BUG-134 et BUG-115 sont
`CORRIGE_DETERMINISTEMENT_NON_REVALIDE_LIVE`; produit
`TESTE_NON_APPROUVE`. C001 reste interdit au replay.

La prochaine étape autorisée est A571 : choisir un canari distinct, en figer
la question, la vérité source attendue, les critères de réussite/arrêt et les
budgets. Aucun live n'est autorisé avant la clôture d'un préflight readonly
séparé.

## 2026-08-30 — A571 — sélection C002 TF6220

Statut : **PRÉENREGISTRÉ — AUDIT A571 REQUIS — LIVE INTERDIT**.

C002 demande un seul exemple de réglementation de contact alimentaire pour le
PTFE-AS TF6220. La preuve fraîche est le règlement `(EU) No 10/2011` dans
`CER-FDA_EU Sleeve PTFE-AS TF6220-EN.pdf`, page 1, sous `Documents
techniques`. La question SHA est
`7394A425033ECEE5100B362E0CD265F0186226A30052316C72A5BCDA66D40C09`.

Le catalogue courant contient 31 catégories, dont `Certifications=7` documents
NIST et `Documents techniques=13`. La bonne frontière retourne 14 éléments et
deux hits cibles directs. `Certifications` renvoie toutefois HTTP 500 ; le
corpus complet retourne 12 éléments mais zéro cible. A572 ne pourra pas passer
tant que l'erreur n'est pas remplacée par un résultat sain et la frontière
correcte revalidée dans 90 secondes.

TF1750 est rejeté comme canari BUG-134 : `Normes` retourne 16 éléments non
cibles, donc ce n'est pas un zéro-rendement ; les probes corrects ont expiré à
45 puis 90 secondes. Une tentative initiale non bornée interrompue après plus
de 5 min 30 a conduit à borner chaque probe avec un `CancellationToken`.

Protocole :
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A571-A575-CANARI-LIVE-DISTINCT-C002-TF6220.md`.
Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/A571-RAPPORT-SELECTION-CANARI-C002-TF6220.md`.
Zéro Qwen, WinUI, contrôle d'écran ou processus cible. L'audit A571 doit
authentifier ces preuves avant l'ouverture d'A572.

Audit A571 : **81/81 PASS**. Script SHA
`6869B9D8F3FF3F003C047E02872C896747E3D05C5564674652C80142C4991411` ;
sortie SHA
`ACB053E8303277869EA959AF5E94A126384E7B8AB48EF24866F5A82C3120E5A9`.
Le protocole, le rapport, la question, la vérité source, l'erreur backend, les
candidats rejetés, le harnais borné, les sources BUG-134 inchangées, Git et
l'absence de ressources sont authentifiés.

**A571 EST CLOS PASS DE SÉLECTION.** C002 est préenregistré mais
`A571_LIVE_AUTHORIZED=0`. A572 readonly est ouvert ; il devra exécuter une
mesure fraîche et ne pourra autoriser A573 que si toutes ses gates passent.

## 2026-08-30 — A572 — préflight C002, correction backend et déploiement

Statut : **90/90 PASS — A573 LIVE UNIQUE AUTORISÉ**.

La première mesure A572 a échoué fermé comme exigé : `Certifications`
renvoyait HTTP 500 alors que `Documents techniques` renvoyait 14 éléments et
deux hits du document TF6220. Aucun Qwen ni WinUI n'a été lancé dans cet état.

Le diagnostic direct puis les logs du conteneur ont isolé une erreur générique
dans `SearchTitleAnchorRouteMatchesAsync` : un fallback interne passait
`topK=80` à `Math.Clamp(topK * 3, topK, 48)`, donc la borne minimale 80
dépassait la maximale 48. RED : 5/6, échec exact `'80' cannot be greater than
48`. GREEN : 6/6 après saturation explicite du sous-budget de navigation à 48.
Aucune catégorie, réglementation ou réponse n'est codée dans le produit ; Qwen
reste propriétaire du pivot sémantique.

Régressions : `RetrievalRuntimeSwitchTests` **866/866** et suite backend
canonique AnyCPU **2054/2054**. Deux passes x64 non promues ont révélé neuf
tests de chemins relatifs dépendants du layout de sortie ; aucun échec produit
ne subsiste dans la passe canonique. TRX promus :
`B8A5249048806CBCC98B412D9696B3D903A7554E5391D78ECECB8EB05B6DBD0A`
et `4CE342AF97ECA175337721B9FCC9A4AA228ABF6E32E2847565AB66C8DE40450B`.

Le backend a été redéployé sans reprovisionnement ni synchronisation de
configuration. Image
`sha256:0a493a05024c6c9a8b3ed9c5eef98d818088ea58e3d2b5807a0c340716db0555`,
révision
`5f35881cdc67d12a076fcd2a7a1004656ac9a37a-source-fdcc544e0df81bdba59b6368cbe6daf138d560309ce909173bbd074d83c1ea4d`.
`/ready`, DB, TEI, Qdrant, LLM, provenance canonique et signature sont verts ;
Docling est inchangé.

Mesure finale : `Certifications` HTTP 200, zéro élément en 7 442 ms ;
`Documents techniques` HTTP 200, 14 éléments, deux hits cibles en 3 683 ms.
La vérité page 1 est fraîche : révision
`f26c2848-6dc9-3fb2-a0ab-18fa242eeebb`, source SHA-256 fort
`a23af72549f11ddbd6a176c6e0648fdd8b147ea9414ece55a8d4da6eae74d789`,
chunk `2c93a130-9066-d936-99ba-9b1a54b2f5fc`, mention directe `(EU) No
10/2011`.

Les trois scripts A573 démarrage/arrêt/collecte sont AST propres et `ArmOnly`
sans effet. Le binaire client reste byte-identique au lot 1670/1670, SHA
`A2B3DE381D2EB82C6B145C3B87FDB768E01DB5925F89E712822AF70DF3796226`.
Audit final : **90/90 PASS**, script SHA
`2E3D551E1B1D75F972563B80696B2E5B0B6F0EF4BE8D99B8ED47F7871715EB74`,
sortie SHA
`47C1BB987ADF0A64CB0DDA4E3878C88B24C0AD8B680F525E21825CDB74CBF9D4`.
Une première itération d'audit non promue s'est arrêtée sur une assertion
textuelle coupée par un retour ligne ; les preuves et le rapport n'ont pas
changé.

**A572 EST CLOS PASS READONLY.** A573 peut créer exactement une nouvelle
discussion, saisir et soumettre C002 une fois, sans deuxième message ni replay.
Budgets : 9 outils documentaires, 12 000 tokens, 240 000 ms. Le contrôle
d'écran ne sera ouvert que pour l'interaction WinUI puis fermé immédiatement.

## 2026-08-30 — A573 — canari live unique C002

Statut : **FAIL PROBANT — C002 INTERDIT AU REPLAY**.

Une seule nouvelle discussion WinUI a été créée et la question C002 exacte a
été soumise une seule fois. La réponse terminale est : `Les sources consultees
ne suffisent pas pour produire une reponse fiable et sourcee.` Zéro carte
source et zéro preuve cible ont atteint l'interface.

Mesures : 8 appels documentaires dans la borne de 9, mais 42 269 tokens contre
12 000 et 385 911 ms contre 240 000. La session persistée contient exactement
deux messages. La trace n'a émis aucun `scope.zero_yield` : `Normes` avait
matérialisé des preuves non pertinentes, puis `documents.content_cards` a
matérialisé cinq cartes du mauvais document. Le juge Qwen a rejeté deux lots,
après quoi la pipeline a bouclé jusqu'au tour 16.

Session `2073e031-bee7-4773-bfff-cb67437c08d6`, trace
`rag-20260830154454224-b6330191`, réponse SHA
`76353B43CA2569892AE127CA8FAC4A1CAA217130DAB5F448B43C6F90534D4238`.
Artefacts :
`phase5/a573-live-winui-c002-tf6220-food-contact/`.

WinUI, runtime local, listener 1234 et contrôle d'écran ont été arrêtés après
collecte. Produit et phase 5 restent `TESTE_NON_APPROUVE`. A574 est ouvert pour
un diagnostic générique ; aucun replay C002 n'est autorisé.

## 2026-08-30 — A574 — causes et corrections génériques C002

Statut : **PASS DÉTERMINISTE — NON REVALIDÉ LIVE — A575 AUDIT OUVERT**.

Deux causes distinctes sont prouvées. Premièrement, le routeur Qwen avait
conservé la requête, la cible et `content_claim`, mais `count=1` avec
`selectionPolicy=explicit_set` faisait rejeter toute la route deux fois. Le
contrat canonise désormais ce seul conflit en `single_item`, avec trace ;
`open_set` reste strict. Deuxièmement, après deux lots rejetés et une limite de
zéro continuation, trois candidats bloquaient la terminaison alors que
`candidateAuditEnabled=false` rendait leur audit impossible. Ils ne bloquent
plus l'ouverture de la décision terminale Qwen lorsqu'aucun chemin d'audit
n'existe.

RED : helper absent `CS0117` et payload C002 rejeté
`native_source_route_contract_invalid`. GREEN : contrats liveness 6/6,
contrats routeur 3/3, portefeuille ciblé 397/397. La première suite complète a
été rejetée à 1 675/1 676 car le runner comptait 2 540 lignes. Le contrat a été
extrait dans un module partiel dédié ; runner final 2 496 lignes, garde 6/6,
suite finale **1 676/1 676**.

TRX final SHA
`E393116FEE36BC60280DAA101DF27D0F6EA30FFE37FC28C4EBCB3B2250B93CBD` ;
DLL WinUI SHA
`9E6446CE7438B2A779063147D9702C121D17EF19535E552FA89BD6CEE0AD4F0A`.
Rapport :
`phase5/A574-RAPPORT-DIAGNOSTIC-ET-CORRECTIONS-C002.md`.

A573 reste FAIL et C002 ne sera pas rejoué. A575 doit authentifier le rapport,
l'absence de hardcoding, les TRX, Git et l'arrêt des ressources, puis envoyer
un Telegram consolidé. Une future preuve live nécessitera un nouveau canari et
un nouveau protocole ; le produit reste `TESTE_NON_APPROUVE`.

## 2026-08-30 — A575 — audit et Telegram C002

Statut : **CLOS — FAIL LIVE SCELLÉ, CORRECTIONS DÉTERMINISTES AUTHENTIFIÉES**.

L'audit pré-envoi passe **57/57**. Il authentifie les TRX 397/397,
1 675/1 676 rejeté et 1 676/1 676 promu, la DLL WinUI, le delta/session live,
les deux contrats corrigés, `open_set` strict, le runner à 2 496 lignes,
l'absence des termes C002 dans le code de production, les statuts du rapport,
Git, zéro processus cible et zéro listener 1234. Une première itération non
promue s'était arrêtée après 32 portes sur une assertion coupée par un retour
ligne ; seule l'assertion a été rendue tolérante aux espaces.

Telegram envoyé exactement une fois le 2026-08-30 à 18:18:20 +02:00. Sortie :
`Notification Telegram envoyee. Corps=2504 caracteres. Total=2627
caracteres.` Message SHA
`5A9E48B528121ECF15204FFB8F54A1B7A71993645261F041EB4AB4532FEF2E6E`.
Reçu : `phase5/00-RECU-TELEGRAM-A575.md`.

L'audit post-envoi passe **19/19** : un message, un reçu, autorisation
pré-envoi 57/57, aucun second envoi, Git attendu, ressources cibles et port
1234 absents. **A571–A575 EST CLOS FAIL PROBANT.** Les corrections A574 sont
`CORRIGEES_DETERMINISTEMENT_NON_REVALIDEES_LIVE`; C002 reste interdit au
replay ; produit et phase 5 restent `TESTE_NON_APPROUVE`.

La prochaine étape est A576 : sélectionner readonly un canari C003 distinct,
avec autre question, autre fait et vérité source fraîche, puis préenregistrer
un nouveau protocole avant tout modèle ou WinUI. Le contrôle de l'ordinateur
reste fermé pendant cette sélection.

## 2026-08-30 — A576 — sélection readonly C003 TF1750

Statut : **C003 SÉLECTIONNÉ — A577 AUDIT REQUIS — LIVE INTERDIT**.

Le canari retenu est `C003_ONE_TF1750_BULK_DENSITY`. Question exacte :
`Pour le PTFE TF 1750, quelle densite apparente est indiquee ? Reponds en une
phrase et cite la source.` SHA-256 UTF-8 :
`5C786A54FE36D765A957D3E9C7A3914D50EB6B2105B462AA60F28773AB0A6578`.

La vérité fraîche est `Bulk density 370 g/l`, méthode ASTM D 4894-98a, dans
`FIT-PTFE_TF_1750-EN.pdf`, page 1, docId
`dc8a22e6-be8d-7d86-108b-0b367690153b`, révision
`89d93b19-8d19-28a6-d5bd-202855306893`, chunk
`9948dadf-79d2-e4d0-de5d-73819c1adefd`.

Avec la question exacte, `Documents techniques` répond sainement avec 17
éléments et trois hits du document cible ; le corpus complet répond avec huit
éléments et trois hits cibles. Le contexte canonique retourne quatre chunks et
porte directement le fait page 1. Zéro Qwen, WinUI, contrôle d'écran, processus
cible, variable live ou listener 1234.

TF9205, également probant à 400 g/l, reste en réserve ; FIT-Dyneon est écarté
car trop proche du thème réglementaire de C002. TF1750 apporte un fait
quantitatif distinct et confirme que la portée correcte qui expirait avant A572
répond désormais.

Le protocole
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A576-A580-CANARI-LIVE-DISTINCT-C003-TF1750.md`
sépare le verdict fonctionnel de la revalidation causale des deux corrections
A574. Budgets inchangés : un envoi, neuf appels documentaires, 12 000 tokens,
240 000 ms et six tours agent maximum. A576 n'autorise aucun live. A577 doit
auditer le préenregistrement, puis A578 refaire un préflight readonly frais.

## 2026-08-30 — A577 — audit du préenregistrement C003

Statut : **78/78 PASS — A576/A577 CLOS — A578 READONLY OUVERT**.

L'auditeur authentifie le protocole, le rapport A576, la capture readonly, les
harnais bornés, les corrections A574, la clôture A575, la question et son SHA,
la vérité source page 1, les budgets et tous les verdicts fonctionnels/causaux.
La recherche anti-hardcoding retourne zéro occurrence de TF1750, 370 g/l,
docId ou chunk cible dans le code C# de production.

Audit final : **78/78 PASS**, script SHA
`311426E30A185A0B757A404C16B1BC997137D1D3FAE1B9C697C912BDC8D00B42`.
Deux itérations non promues ont détecté uniquement une assertion sensible au
retour ligne puis le nom exact du drapeau A575 ; seul l'auditeur a changé.

À l'ouverture d'A578, une première mesure a produit toutes les preuves source
attendues puis le wrapper a échoué à tort sur `$LASTEXITCODE` non défini. Le
wrapper a été corrigé sans changer le probe, la question ni le produit, SHA
`4625A6EA5AFFD81EE8FA42A23E1127D04A666540984359BD1063E5C56AA3FF64` ;
A577 a été réexécuté et repasse 78/78.

Branche `SAAIA_V3.1`, HEAD
`5f35881cdc67d12a076fcd2a7a1004656ac9a37a`, `git diff --check` PASS, zéro
processus cible, listener 1234 ou environnement live. **A579 reste interdit**.
A578 doit maintenant revalider à neuf la source, le backend, les binaires et
les scripts live désarmés, toujours sans Qwen ni WinUI.

## 2026-08-30 — A578 — préflight readonly final C003

Statut : **80/80 PASS — A579 LIVE UNIQUE AUTORISÉ**.

La mesure fraîche termine avec le code 0. Le catalogue conserve une cible
unique TF1750 ; `Documents techniques` retourne 17 éléments et trois hits
cibles, le corpus complet huit éléments et trois hits cibles. Le contexte
canonique expose toujours `Bulk density 370 g/l` dans le chunk
`9948dadf-79d2-e4d0-de5d-73819c1adefd`, page 1. Sortie fraîche SHA
`F3EA26F93125D9159ACAEB4A4D9DB192498F391D2C3566F19DD57BB092D8FF53`.

La suite cliente complète A574 est réauthentifiée **1 676/1 676**, TRX SHA
`E393116FEE36BC60280DAA101DF27D0F6EA30FFE37FC28C4EBCB3B2250B93CBD` ;
DLL WinUI SHA
`9E6446CE7438B2A779063147D9702C121D17EF19535E552FA89BD6CEE0AD4F0A`.
Les trois scripts A579 sont AST propres, sans résidu C002/A573, et leurs modes
`ArmOnly` passent sans completion, UI, action d'arrêt ni fichier écrit.

Audit final : **80/80 PASS**, script SHA
`AAF233AAF4A0911B6279167C574BEF17C0769A740F44193C08B2B97D25659395`.
Zéro processus cible, listener 1234, environnement live ou répertoire officiel
après audit. Git attendu et `git diff --check` PASS.

**A578 EST CLOS PASS.** A579 est autorisé exactement une fois : une nouvelle
discussion WinUI, la question C003 exacte et un seul envoi, sans régénération
ni replay. Les budgets restent neuf appels documentaires, 12 000 tokens,
240 000 ms et six tours agent. Le contrôle de l'ordinateur ne sera actif que
pendant l'interaction WinUI, puis fermé immédiatement ; le runtime sera arrêté
avant la collecte finale.

## 2026-08-30 — A579 — canari live unique C003 TF1750

Statut : **FAIL FONCTIONNEL PROBANT — C003 INTERDIT AU REPLAY**.

Une nouvelle discussion a été créée, la question exacte saisie et envoyée une
seule fois. La session persistée
`4480b20e-43f7-41c9-a352-0e7b5ad2b33c` contient exactement deux messages ;
trace `rag-20260830164314870-d75ce9ad`. Réponse : `Les sources consultees ne
suffisent pas pour produire une reponse fiable et sourcee.` Zéro carte source,
zéro outil documentaire et writer source-backed non atteint.

Les budgets passent : 42 303/240 000 ms, 2 708/12 000 tokens, 0/9 appels
documentaires et terminaison au tour source-backed 1. L'échec est donc
sémantique/contractuel, pas un timeout, une boucle ou une saturation.

La route native est acceptée avec `single_item`, `rag.search` et la bonne
requête, mais Qwen place `PTFE TF 1750` dans le champ réservé au titre/nom de
fichier exact. Le résolveur exact ne peut l'égaler au vrai fichier
`FIT-PTFE_TF_1750-EN.pdf`, retourne `NotFound`, puis le code met le `rag.search`
en quarantaine. Qwen choisit `declare_named_document_insufficiency`, accepté
avant toute exploration.

Qualification A574 : route
`ROUTE_A574_NON_CONCLUSIVE_CHEMIN_NON_EXERCE` car Qwen a directement émis
`single_item`; terminalité
`TERMINAL_A574_NON_CONCLUSIVE_CHEMIN_NON_EXERCE` car aucun candidat n'a été
créé. Bilan A574 : `NON_CONCLUSIVE_LIVE`.

WinUI fermée, contrôle de l'ordinateur relâché pendant le calcul puis après le
constat, runtime local arrêté et listener 1234 absent. Delta SHA
`E08D7FAA640017C037DBB2C42F610DE7866A2BE5B66A32BAAB5DA35E3CD9BAC3` ;
session SHA
`D57D1DEAD91B55302284159A854172BF5B6A4928ECF3182F5B77802978C3BCBD`.

## 2026-08-30 — A580 — audit, cause BUG-135 et Telegram

Statut : **CLOS — AUDITS 113/113, 34/34 ET 32/32 — TELEGRAM CONFIRMÉ**.

L'audit technique authentifie l'unicité, les artefacts live, les budgets, la
trace, les six sources de contrat, les verdicts, Git et l'arrêt des ressources :
**113/113 PASS**. Cause descriptive : **BUG-135 — confusion entre sujet nommé
et document explicitement nommé, suivie d'une insuffisance avant recherche**.

Le résolveur exact n'est pas en faute et ne doit pas devenir flou. La correction
recommandée est générique : rendre explicite dans le contrat Qwen la nature
`sujet/entité` versus `document/titre/fichier`, puis faire rejeter comme
incohérente une insuffisance immédiate à zéro requête lorsqu'une action
documentaire valide vient d'être mise en quarantaine. Qwen conserve le choix
sémantique ; le code impose uniquement la cohérence de l'état.

Audit pré-Telegram **34/34 PASS**. Une première exécution non promue s'était
arrêtée à l'analyse AST parce que PowerShell traite l'apostrophe typographique
comme délimiteur ; seule l'assertion a changé. Telegram envoyé exactement une
fois le 2026-08-30 à 18:53:29 +02:00, corps 2 691 caractères, total 2 806.
Message SHA
`DC89E15F2E894F99969B5A0975A80377291449A9EEC83F6D1DC0DAF027AC5CBF` ;
reçu `00-RECU-TELEGRAM-A580.md`.

Audit post-Telegram **32/32 PASS**, script SHA
`56B450EEE86C625A4816FC2091FF42EE61547C339E930438A687FB7761D29335`.
Un message, un reçu, aucun second envoi autorisé, zéro processus cible,
listener 1234 ou environnement live.

**A576–A580 EST CLOS FAIL PROBANT.** C003 reste interdit au replay, A574 reste
`CORRIGEE_DETERMINISTEMENT_NON_REVALIDEE_LIVE`, produit et phase 5
`TESTE_NON_APPROUVE`. La prochaine étape autorisée est A581 : préenregistrer un
lot RED/GREEN générique BUG-135, sans Qwen, WinUI ni nouveau live.

## 2026-08-30 — A581 — préenregistrement générique BUG-135

Statut : **PASS PREENREGISTREMENT — A582 RED OUVERT**.

Le protocole
`artifacts/goal-rag-product-20260827-1041/phase5/PROTOCOLE-A581-A585-BUG135-SUJET-VS-DOCUMENT.md`
a été figé avant tout test ou changement de production BUG-135. Il interdit le
live, WinUI, Qwen et tout replay C002/C003 dans A581-A585.

Le contrat cible conserve Qwen comme propriétaire sémantique. Les routes
source-backed devront transporter `namedReferenceKind=none|subject|document` ;
le code vérifiera seulement la cohérence avec le champ `document`. Les titres
sans extension restent valides et le résolveur exact reste inchangé.

Une seconde barrière est préenregistrée pour les actions mises en quarantaine :
une première insuffisance immédiate à zéro recherche sera contestée une fois et
Qwen pourra appeler `reinterpret_named_reference_as_subject`, qui restaurera
l'action originale sans la réécrire. Une seconde insuffisance restera possible
pour un vrai document absent.

Huit familles d'oracles R1-R8 couvrent schéma/prompt, sujet, incohérences, vrais
documents, réparation, contestation, reclassification et insuffisance légitime.
Rapport : `A581-RAPPORT-PREENREGISTREMENT-BUG135.md`.

**A581 EST CLOS PASS DE PREENREGISTREMENT.** Aucun test ni code de production
BUG-135 n'a encore changé. A582 doit ajouter les tests, capturer un RED probant,
puis seulement autoriser A583.

## 2026-08-30 — A582 — RED causal BUG-135

Statut : **RED PROBANT — 0/11 PASS, 11/11 FAIL — A583 AUTORISÉ**.

Les onze oracles `Bug135*` ont été ajoutés avant toute modification de
production. La passe canonique `Debug x64` compile puis échoue exclusivement
sur les lacunes préenregistrées : schéma sans type de référence, absence de
transport, quatre incohérences acceptées, sujet réinjecté pendant la réparation
et insuffisance acceptée dès le premier passage dans les deux scénarios de
transition.

TRX brut : `phase5/a582-bug135-red/A582-BUG135-RED.trx`, SHA
`911EB9A353EF630D519D74E4C279160998B831AE709FFC7610B9A957FB65FE01`.
Oracle SHA
`B2550E2B327D1D0B6B679306BE195D5ABF69984090AFFF665D3E3640FBC7BEA3`.
Rapport : `phase5/A582-RAPPORT-RED-BUG135.md`.

Une première commande `Release`, non promue, s'est arrêtée sur les hooks
`#if DEBUG` et n'est pas une preuve produit. La relance `Debug x64` n'a requis
aucune modification de test. Les sept fichiers de production surveillés ont
conservé leurs timestamps A581 jusqu'au RED ; `git diff --check` passe.

**A582 EST CLOS RED PROBANT.** A583 peut maintenant implémenter le contrat
minimal. Aucun Qwen, WinUI, live ou contrôle d'ordinateur n'est autorisé.

## 2026-08-30 — A583 — GREEN minimal BUG-135

Statut : **GREEN CIBLÉ — 12/12 PASS — A584 RÉGRESSIONS OUVERT**.

Le routeur Qwen transporte désormais une décision explicite
`namedReferenceKind=none|subject|document`. Le code vérifie l'énumération et la
cohérence de `document`, mais ne classifie aucun texte. Produit, modèle ou
entité restent des sujets de requête ; seuls titre, fichier ou chemin
documentaires explicitement nommés deviennent des ancres exactes. Les titres
sans extension et le document focalisé restent supportés.

La continuité de réparation ne réinjecte plus une chaîne classée `subject`.
Dans un état document typé où une action disponible a été mise en quarantaine,
la première insuffisance est contestée une fois. Qwen peut appeler
`reinterpret_named_reference_as_subject`, qui restaure l'action inchangée et
efface l'état documentaire. Une seconde insuffisance reste valide pour un vrai
document absent. Le résolveur exact est byte-identique.

RED A582 : 0/11. GREEN final A583 : **12/12**, TRX SHA
`48E8C9A71597F7738403A7B31E337C6C32B021241FC91C9092144ACF5844B30D`.
L'ancien test C002 a été corrigé pour ne plus encoder produit=document. Une
itération intermédiaire 9/11, non promue, a seulement révélé deux limites de
harnais ; la revue sémantique post-recherche n'a pas été contournée.

Le parser de transition reste à 490/500 lignes ; le nouveau module générique en
compte 69. Aucune donnée C003 n'apparaît dans le produit, `git diff --check`
passe et aucune ressource live n'a été ouverte. Rapport :
`phase5/A583-RAPPORT-GREEN-MINIMAL-BUG135.md`.

**A583 EST CLOS GREEN CIBLÉ.** Le statut reste
`CORRIGE_DETERMINISTEMENT_NON_REVALIDE_LIVE` en attente des régressions A584 ;
le produit reste `TESTE_NON_APPROUVE`.

## 2026-08-30 — A584 — régressions BUG-135

Statut : **PASS DETERMINISTE — 440/440 ET 1687/1687 — A585 OUVERT**.

Le portefeuille routeur/source-backed/verifier passe **440/440** et la suite
client complète `Debug x64` passe **1687/1687**. Les deux budgets de prompt
initialement dépassés ont été respectés par compaction du texte, sans relever
les plafonds et sans retirer le contrat `namedReferenceKind`.

Le résolveur exact reste byte-identique, le parser de transition reste à
490/500 lignes et le nouveau module générique à 69 lignes. Zéro donnée C003
n'apparaît dans le code produit. Les DLL WinUI du produit et du harnais sont
identiques. `git diff --check` passe ; build servers arrêtés, listener 1234 et
variables live absents, contrôle de l'ordinateur désactivé.

Rapport : `phase5/A584-RAPPORT-REGRESSIONS-BUG135.md`.

**A584 EST CLOS PASS DETERMINISTE.** BUG-135 reste
`CORRIGE_DETERMINISTEMENT_NON_REVALIDE_LIVE`, produit et phase 5
`TESTE_NON_APPROUVE`. A585 doit sceller le palier, auditer les preuves puis
envoyer exactement un Telegram consolidé.

## 2026-08-30 — A585 — scellement BUG-135 et Telegram

Statut : **CLOS — AUDIT 68/68, PRE-TELEGRAM 41/41, RAPPORT OFFICIEL CONFIRME**.

L'audit final authentifie le protocole, les quatre rapports, les TRX RED 0/11,
GREEN 12/12, ciblé 440/440 et complet 1687/1687, les sources, oracles, DLL,
limites de lignes, absence de durcissement C003, Git et ressources : **68/68
PASS**. Aucun live n'a été lancé et C003 reste interdit au replay.

Une première passe d'audit, non promue, cherchait l'outil de reclassification
dans le mauvais module ; seul l'audit a été corrigé. Pendant l'inspection de
l'interface du notificateur, `-?` a déclenché une notification parasite de 25
caractères. L'incident est documenté dans
`00-INCIDENT-TELEGRAM-A585-DIAGNOSTIC.md` et divulgué dans le rapport officiel.

Audit pré-envoi **41/41 PASS**. Rapport officiel Telegram envoyé une fois le
2026-08-30 à 19:34:56 +02:00 : corps 2 395 caractères, total 2 512, code 0.
Message SHA
`46E31034AB531E5EABA1ED1672AC9CAC57FF603CA3A4BDA5B6141B05380CA88B` ;
reçu `00-RECU-TELEGRAM-A585.md`. Aucun second rapport officiel autorisé.

**A581-A585 EST CLOS PASS DETERMINISTE.** BUG-135 est
`CORRIGE_DETERMINISTEMENT_NON_REVALIDE_LIVE`; produit et phase 5 restent
`TESTE_NON_APPROUVE`. Prochaine étape autorisée : préenregistrer un canari live
distinct C004, sans replay C003, avec réponse, sources, trace, UI et performance
comme verdicts séparés.

Audit post-Telegram : **37/37 PASS**. Un rapport officiel, un reçu, un incident
diagnostique déclaré, aucun second rapport autorisé, aucune ressource cible ou
exécution live. Sortie SHA
`1115C8900B8BD461F73692219DC8869722EB64F2C0F052A22E7AEA218B84D181`.

## 2026-08-30 — A586 — sélection readonly C004 ABB 266 HART

Statut : **CLOS SELECTION READONLY — A587 AUDIT OUVERT**.

C004 est `C004_ONE_ABB266_HART_LCD_MENU`. Question gelée, une ligne, SHA
`A0AF326B4A62EADA8A395C40E0E2FA68171C47D42A7CD5EFFBEBE7530BEE0EF0` :

> Sur un transmetteur de pression ABB 266 HART standard, quel menu est
> disponible sur l'ecran LCD ? Reponds en une phrase et cite la source.

Le corpus `Support client - FAQ` contient une occurrence du manuel ABB cible.
La recherche readonly scoped produit 14 éléments, trois hits du document cible
et la preuve pages 37-38 : la version HART standard propose uniquement le menu
`Easy Setup`.

C004 est distinct des canaris PTFE : nouvelle catégorie, nouveau fabricant,
manuel de support et fait d'interface. Il revalide la frontière sujet/document
sans nom de fichier dans la question. Deux chemins sont autorisés : sujet
direct, ou reclassification Qwen bornée après résolution exacte infructueuse ;
une insuffisance à zéro recherche reste un FAIL.

Protocole :
`phase5/PROTOCOLE-A586-A590-CANARI-LIVE-DISTINCT-C004-ABB266.md`. Rapport :
`phase5/A586-RAPPORT-SELECTION-CANARI-C004-ABB266.md`.

Un candidat « tension d'alimentation » sans preuve assez haute a été rejeté et
une première requête scoped + non scoped a dépassé le yield sans résultat
promu. Le wrapper final scoped a terminé avec zéro Qwen, UI, processus cible,
listener ou environnement live. Contrôle de l'ordinateur désactivé.

**A586 EST CLOS.** A587 doit auditer le gel avant A588 ; A589 reste la seule
phase pouvant autoriser une soumission live unique.

## 2026-08-30 — A587 — audit du préenregistrement C004

Statut : **CLOS 60/60 PASS — A588 AUTORISE — LIVE TOUJOURS INTERDIT**.

L'audit authentifie question 138 caractères/SHA, scripts, sorties, document
unique, chunk, pages 37-38, claim `Easy Setup`, protocole, budgets, clôture A585
et plan. Les deux chemins BUG-135 restent Qwen-first : sujet direct ou
reclassification bornée. L'insuffisance à zéro recherche est explicitement un
FAIL.

Zéro hardcode C004 dans le produit, processus cible, listener, environnement
live, Qwen ou UI. `git diff --check` passe. Rapport :
`phase5/A587-RAPPORT-AUDIT-PREENREGISTREMENT-C004-ABB266.md`.

**A587 EST CLOS PASS.** A588 peut lancer tests et readonly, puis construire et
auditer les scripts à blanc. Aucune soumission live avant un PASS A588.

## 2026-08-30 — A588 — préflight et armement C004

Statut : **CLOS 79/79 PASS — UN LIVE C004 AUTORISE EN A589**.

Le portefeuille déterministe repasse **440/440**. La mesure readonly fraîche
retrouve une cible, 14 résultats, trois hits du seul manuel ABB et la preuve
`Easy Setup` pages 37-38. Les DLL WinUI produit/harnais sont byte-identiques.

Les scripts start/stop/collect A589 ont été clonés du harnais robuste puis
nettoyés. Une première analyse AST a détecté l'apostrophe non échappée de
`l'ecran`; le délimiteur a été corrigé avant live. Passe promue : trois AST sans
erreur, start/stop/collect armés, 32 sessions de référence, zéro appel modèle,
action UI, arrêt ou fichier écrit.

Audit final **79/79 PASS**, zéro résidu C003 dans les scripts C004, zéro
hardcode produit, processus cible, listener ou environnement live. Build
servers arrêtés et contrôle de l'ordinateur désactivé.

Rapport : `phase5/A588-RAPPORT-PREFLIGHT-READONLY-C004-ABB266.md`.

**A588 EST CLOS.** A589 autorise exactement une nouvelle session et une
soumission C004. Aucun rerun ; C001-C003 interdits. Le contrôle graphique sera
activé seulement pour la WinUI puis relâché dès sa fermeture.

## 2026-08-30 — A589 — live unique C004 ABB 266 HART

Statut : **CLOS FAIL_FONCTIONNEL_PROBANT — AUCUN REPLAY**.

Une seule nouvelle discussion et une seule soumission de la question gelée ont
été exécutées. La collecte authentifie 32 -> 33 sessions, deux messages,
exactement un tour, session `6f78dae8-7eae-48e5-92d4-721bfdeecfdc` et trace
`rag-20260830175614032-68b1d482`.

Le routeur a choisi `rag.answer`, plan `single_item`, référence nommée `none`,
puis `rag.search`. Trois appels documentaires ont terminé. La recherche avait
déjà trouvé le manuel ABB cible et la preuve `Easy Setup` pages 37-38, mais le
LLM a finalement sélectionné uniquement une carte `E31` intitulée
`Integrated LCD display available`. Le writer a répondu deux fois que le menu
était « l'affichage intégré », réponse de travail sémantiquement incorrecte.

Le vérificateur a bloqué cette réponse avec `invalid_source_sha256` : la carte
porte le hash catalogue 32 caractères
`bbf146b98eafa201cb480f5c590af05f`, alors que le contrat exige le SHA-256 de
révision 64 caractères. Après une réparation sans progrès, la réponse terminale
visible et persistée est l'insuffisance prudente, sans source. Zéro carte UI.

Verdicts : fonction FAIL, provenance FAIL, UI FAIL, unicité/trace PASS mais
probantes du FAIL, performance FAIL avec 247 218 ms > 240 000 et 17 518 tokens
> 12 000 ; 3 appels documentaires <= 9. La capture UI a été observée pendant le
run mais n'a pas été exportée localement ; cette lacune est déclarée et ne sera
pas masquée par un replay.

WinUI fermée, contrôle de l'ordinateur réinitialisé immédiatement, runtime PID
18564 et listener 1234 arrêtés. Rapport :
`phase5/A589-RAPPORT-LIVE-C004-ABB266-FAIL.md`.

**A589 EST CLOS FAIL PROBANT.** C004 rejoint C001-C003 dans les canaris
interdits au replay. A590 doit auditer séparément identité mécanique, sélection
sémantique et budgets, puis envoyer le rapport Telegram obligatoire.

## 2026-08-30 — A590 — audit causal C004

Statut : **CLOS FAIL LIVE PROBANT — TELEGRAM CONFIRMÉ — A591 AUTORISÉ**.

Le probe readonly indépendant de l'endpoint `documents.content_cards` confirme
le payload exact sélectionné pendant le live : carte
`Integrated LCD display available`, hash de 32 caractères, score 0,185,
confiance 0,55, aucun `sourceText`, fait ou extrait. Zéro Qwen et zéro UI ont été
utilisés pour ce probe.

Trois défauts génériques sont séparés :

1. **identité mécanique** : le builder copie un hash catalogue 32 caractères
   dans `EvidenceItem.SourceHash`, puis le vérificateur SHA-256 le refuse ;
2. **admissibilité/sémantique** : une carte sans preuve textuelle est traitée
   comme preuve autonome, et le LLM a préféré son titre à la preuve exacte déjà
   présente ;
3. **budget** : 12 appels LLM dépassent temps et tokens, alors que les trois
   outils documentaires ne sont pas le goulot principal.

La correction recommandée ne contient aucune règle ABB : hydrater une carte à
partir d'un unique SHA-256 compatible du même document/révision déjà présent
dans l'EvidenceBundle ; conserver une carte sans preuve comme pivot de
navigation avec risque explicite, mais pas comme unique preuve finale ; faire
respecter un budget cumulatif par le contrôleur sans reprendre au LLM son choix
sémantique. Le vérificateur SHA-256 ne doit pas être assoupli.

Rapport :
`phase5/A590-RAPPORT-AUDIT-C004-FAIL-IDENTITE-CARTE-ET-SELECTION.md`.

Après Telegram et audit post-envoi, A591-A595 pourront préenregistrer puis
corriger ces contrats en RED/GREEN déterministe. Aucun replay C004. Un éventuel
live ultérieur devra être C005, distinct et préenregistré.

Telegram A590 envoyé exactement une fois le 2026-08-30 à 20:12:53 +02:00 :
corps 2 903 caractères, total 3 014, code 0. Message SHA
`DECF668356CC49F16F3A8BB6EC5416C4608E66FA882BD23B122F1CC784BE61FE` ;
reçu `phase5/00-RECU-TELEGRAM-A590.md`. Audit technique **137/137**, audit du
message **39/39**. Aucun second envoi autorisé.

Audit post-Telegram **46/46 PASS** : un message officiel, un reçu, aucun second
envoi, aucun replay C004 et aucune ressource live. **A586-A590 EST CLOS FAIL
PROBANT.** Produit et phase 5 restent `TESTE_NON_APPROUVE`; le Goal reste actif.

## 2026-08-30 — A591 — préenregistrement BUG-136 cartes canoniques

Statut : **PASS PREENREGISTREMENT — 52/52 — A592 RED OUVERT**.

Le protocole
`phase5/PROTOCOLE-A591-A595-BUG136-CARTES-CANONIQUES-PREUVE-ET-SHA.md`
a été figé avant tout test ou changement de production BUG-136. Il interdit
Qwen, WinUI, live, contrôle de l'ordinateur et tout replay C001-C004 dans
A591-A595.

Le lot corrige deux frontières génériques de provenance : l'endpoint doit
projeter le SHA-256 de la révision indexée ; une carte sans preuve textuelle
effective reste visible comme pivot de navigation, mais n'est plus admissible
comme preuve finale. Qwen conserve toute décision de pertinence. Le code ne
vérifie que `docId/revisionId/sourceHash`, présence de preuve et cohérence.

L'hydratation client est strictement limitée à un SHA-256 unique déjà présent
pour le même couple `docId/revisionId`. Aucun hash n'est fabriqué et
`invalid_source_sha256` reste strict. Douze familles d'oracles R1-R12 couvrent
backend, builder direct/embarqué, conflit, sélection, état de travail,
vérificateur et neutralité domaine.

Le dépassement cumulatif temps/tokens est séparé en `BUG-137` pour un lot
ultérieur, car son contrôleur traverse routeur, agent, writer et réparations ; il
n'est pas couplé à cette correction de provenance.

Audit final **52/52 PASS**. Deux premières exécutions non promues se sont
arrêtées dans le script d'audit (échappement PowerShell, puis assertion coupée
par un retour de ligne) ; seuls ces contrôles ont été corrigés. Zéro test ou
production BUG-136 modifié, zéro ressource live, contrôle de l'ordinateur coupé.
Rapport : `phase5/A591-RAPPORT-PREENREGISTREMENT-BUG136.md`.

**A591 EST CLOS PASS DE PREENREGISTREMENT.** A592 doit ajouter uniquement les
tests, capturer le RED exact, puis seulement autoriser A593. Produit et phase 5
restent `TESTE_NON_APPROUVE`; le Goal reste actif.

## 2026-08-30 — A592 — RED causal BUG-136

Statut : **RED PROBANT — 7/17 PASS, 10/17 FAIL — A593 AUTORISÉ**.

Les dix-sept oracles `Bug136*` ont compilé et été exécutés en `Debug x64` avant
toute modification de production BUG-136. Dix échecs reproduisent exactement
les lacunes attendues : SHA backend de résumé, absence d'hydratation au même
`docId/revisionId`, confiance dans le booléen grounded au lieu du payload,
cartes directes et embarquées à titre seul encore citables, observation/état de
travail sans statut non citable, et vérificateur sans erreur de preuve manquante.

Les sept passes protègent les cas de non-hydratation par mismatch, identité
incomplète ou conflit, la stricte validation SHA-256, une carte réellement
prouvée et la neutralité domaine. Aucun échec inattendu.

TRX brut : `phase5/a592-bug136-red/A592-BUG136-RED.trx`, SHA
`71049F8DCEF2EF24D16A35B71BDA6D3C51FC89C46E262BA3DC4B1285724245D2`.
Audit RED **53/53 PASS** : les huit fichiers de production surveillés sont
byte-identiques à A591, `git diff --check` passe, zéro ressource live et
contrôle de l'ordinateur coupé. Rapport : `phase5/A592-RAPPORT-RED-BUG136.md`.

**A592 EST CLOS RED PROBANT.** A593 peut maintenant implémenter le contrat
minimal, sans changer les oracles, sans hardcode et sans assouplir
`invalid_source_sha256`. Produit et phase 5 restent `TESTE_NON_APPROUVE`.

## 2026-08-30 — A593 — GREEN minimal BUG-136

Statut : **GREEN CIBLÉ — 17/17 PASS — A594 RÉGRESSIONS OUVERT**.

L'endpoint `documents.content_cards` projette désormais le SHA-256 de la
révision publiée active, et non le hash de résumé. Le client conserve un SHA-256
valide ou hydrate un hash invalide uniquement depuis l'unique SHA-256 déjà
présent au même couple exact `docId/revisionId`, avec lignée explicite. Aucun
hash n'est fabriqué ou rapproché par chemin/titre.

Les builders dérivent `hasGroundedEvidence` du payload effectif. Une carte à
titre seul reçoit `missing_grounded_content_card_evidence`, reste visible comme
ancre de navigation, mais sort de la sélection mécanique et de l'ensemble des
EvidenceId citables. Observation, état de travail et vérificateur final
appliquent la même frontière. Le hit parent reste intact ;
`invalid_source_sha256` reste strict.

RED A592 : 7/17. GREEN A593 : **17/17**, tests byte-identiques. TRX SHA
`DCA9389A06E5D48981901A2A64B1C980417FED71A6AD836D2906EA99E54E6EAB`.
Le module partagé générique compte 59 lignes, aucun marqueur C004 n'apparaît
dans le produit, `git diff --check` passe et l'audit GREEN fait **62/62 PASS**.
Zéro Qwen, WinUI, live ou contrôle de l'ordinateur. Rapport :
`phase5/A593-RAPPORT-GREEN-MINIMAL-BUG136.md`.

**A593 EST CLOS GREEN CIBLÉ.** BUG-136 est
`CORRIGE_DETERMINISTEMENT_NON_REVALIDE_LIVE`. A594 doit exécuter les
régressions ciblées et complètes backend/client. Produit et phase 5 restent
`TESTE_NON_APPROUVE`; BUG-137 reste ouvert.

## 2026-08-30 — A594 — régressions BUG-136

Statut : **PASS DETERMINISTE — 377/377, 2054/2054 ET 1704/1704 — A595 OUVERT**.

Le backend ciblé passe **2/2**. La première passe client ciblée fait 327/377 :
cinquante tests historiques déclaraient leurs cartes grounded sans sérialiser
de preuve. Le produit et les oracles BUG-136 n'ont pas été relâchés ; seules
deux fabriques partagées ont reçu un `evidence.sourceText` générique cohérent
avec leur intention. La relance ciblée passe **377/377**.

Les suites complètes passent ensuite : backend canonique AnyCPU **2054/2054**,
client Debug x64 **1704/1704**. Les DLL WinUI produit/harnais sont
byte-identiques. Les neuf fichiers de production restent byte-identiques à
A593, aucun marqueur C004 n'apparaît, `git diff --check` passe et les build
servers sont arrêtés.

Limite déclarée : le harnais d'intégration backend peut retourner tôt sans
PostgreSQL ; les 2 ms du ciblé ne prouvent pas une exécution SQL réelle. A594
prouve compilation, oracle source et non-régression, pas déploiement ni live.
Cette preuve attendra un C005 distinct.

Audit A594 **86/86 PASS**. Zéro Qwen, WinUI, live ou contrôle de l'ordinateur.
Rapport : `phase5/A594-RAPPORT-REGRESSIONS-BUG136.md`.

**A594 EST CLOS PASS DETERMINISTE.** BUG-136 reste
`CORRIGE_DETERMINISTEMENT_NON_REVALIDE_LIVE`; BUG-137 reste ouvert. A595 doit
auditer et sceller le palier, puis envoyer exactement un Telegram consolidé.
Produit et phase 5 restent `TESTE_NON_APPROUVE`.

## 2026-08-30 — A595 — scellement BUG-136 et Telegram

Statut : **CLOS — AUDIT 127/127, MESSAGE 48/48, TELEGRAM CONFIRMÉ, POST 63/63**.

Le rapport technique final scelle le protocole, la chaîne RED 7/17 vers GREEN
17/17, les régressions 377/377, backend 2054/2054 et client 1704/1704, les neuf
fichiers de production, les fixtures, les DLL, les limites SQL/live, Git et les
ressources. Audit technique pré-envoi : **127/127 PASS**.

Le message officiel, 2 917 caractères, SHA
`46CBC072B2177D3EDABFF6668C45D35E423FED946DB193F3452FFE315073E4E8`,
communique explicitement `CORRIGE_DETERMINISTEMENT_NON_REVALIDE_LIVE`, produit
`TESTE_NON_APPROUVE`, limite PostgreSQL, BUG-137 ouvert et C005 distinct requis.
Audit du message : **48/48 PASS**. Une première passe non promue s'était arrêtée
sur un compteur de titre attendu à 42 au lieu de 40 ; seul l'audit a changé, le
message est resté byte-identique et aucun envoi n'avait eu lieu.

Telegram envoyé exactement une fois le 2026-08-30 à 20:46:51 +02:00, titre
`SAAIA RAG - BUG-136 corrige deterministe` : corps 2 917 caractères, total
3 034, code 0. Reçu : `phase5/00-RECU-TELEGRAM-A595.md`, SHA
`F57728734F97B74A0B11D2955A982C1941C2B3A6F76478B63F9E77143F593146`.
Aucun second envoi autorisé.

Audit post-Telegram : **63/63 PASS**. Deux exécutions non promues de ce seul
audit avaient utilisé `Contains` sur des phrases coupées par un retour à la
ligne dans le reçu ; elles n'appellent jamais le notificateur. Le script final
SHA `81D3F97495975F05AFAD95B324B85CF688D4D6750401664435F8028619ABDCF4`
et la sortie SHA
`D8FC882BCCBDBC127E31F356A6C3CDD3910EF4FE52AFBC0593F1908FDD8D7EC0`
authentifient un message, un reçu, produit inchangé et zéro ressource live.

**A591-A595 EST CLOS PASS DETERMINISTE.** BUG-136 est
`CORRIGE_DETERMINISTEMENT_NON_REVALIDE_LIVE`; produit et phase 5 restent
`TESTE_NON_APPROUVE`. BUG-137 reste ouvert et devient la prochaine correction
autorisée, dans un lot séparé de budget cumulatif. Le Goal reste actif et le
contrôle de l'ordinateur reste coupé.

## 2026-08-30 — A596 — préenregistrement BUG-137 budget LLM cumulatif

Statut : **PASS PREENREGISTREMENT — 66/66 — A597 RED OUVERT**.

Le protocole
`phase5/PROTOCOLE-A596-A600-BUG137-BUDGET-LLM-CUMULATIF.md` a été figé avant
tout test ou changement de production BUG-137. Il interdit Qwen, WinUI, live,
contrôle de l'ordinateur et tout replay C001-C004 dans A596-A600.

La cartographie confirme que les limites actuelles sont locales, alors que le
classifieur, le routeur, l'agent, les juges, le writer et les repairs partagent
le même adaptateur. Le contrat cible ouvre donc un ledger atomique par tour :
`12 000` tokens, `240 000 ms`, réservation préalable puis réconciliation par
usage réel, deadline monotone et absence de remboursement silencieux.

Une réserve de `4 800` tokens et `60 000 ms` est protégée des explorations. Si
l'enveloppe normale ferme, Qwen conserve la décision : sélection/writer à
partir des preuves visibles, ou terminal compact `resolve_source_yield`. Si
même ce chemin ne tient plus, le produit rend un arrêt technique typé sans
prétendre que le corpus est insuffisant. Aucun fallback routeur ne peut sortir
du ledger.

Quinze familles d'oracles R1-R15 couvrent configuration, partage, admission,
usage absent, erreur/retry, concurrence, temps, réserve terminale, transitions
avec/sans preuves, writer/repair, routeur, traces et neutralité domaine.

Audit final **66/66 PASS** : les dix fichiers surveillés sont byte-identiques,
aucun test, ledger ou scope BUG-137 n'existe encore, `git diff --check` passe et
zéro ressource live est active. Rapport :
`phase5/A596-RAPPORT-PREENREGISTREMENT-BUG137.md`.

**A596 EST CLOS PASS DE PREENREGISTREMENT.** A597 doit ajouter uniquement les
tests `Bug137*`, capturer le RED exact, puis seulement autoriser A598. Produit
et phase 5 restent `TESTE_NON_APPROUVE`; le Goal reste actif et le contrôle de
l'ordinateur reste coupé.

## 2026-08-30 — A597 — RED causal BUG-137

Statut : **RED PROBANT — 2/18 PASS, 16/18 FAIL — A598 AUTORISÉ**.

Les dix-huit oracles `Bug137*` ont compilé et été exécutés en `Debug x64`
avant toute modification de production BUG-137. Les deux passes protègent la
décision terminale par Qwen et la neutralité domaine. Les seize échecs
reproduisent exactement les absences attendues : options, ledger/scope,
admission/réconciliation, usage manquant, retry, concurrence, deadline,
réserve terminale, enveloppe adaptateur, scope de tour, catch routeur,
transition runner, partage writer/repair et traces cumulatives.

Test RED SHA
`CD90535C975EA195F4EC50DABFE6F589F33E87378266DA79BC2E39B397EBB06D`.
TRX brut : `phase5/a597-bug137-red/A597-BUG137-RED.trx`, SHA
`9471799DC8C2D9578D579DCEA7D94659D32FA26320770FDD56E01FAD9844473B`.

Audit **61/61 PASS** : les dix fichiers de production sont byte-identiques à
A596, tous les échecs RED nommés sont présents, `git diff --check` passe,
build servers arrêtés et zéro ressource live ou contrôle de l'ordinateur.
Rapport : `phase5/A597-RAPPORT-RED-BUG137.md`.

**A597 EST CLOS RED PROBANT.** A598 peut implémenter le contrat minimal sans
modifier les oracles ni augmenter les budgets. Produit et phase 5 restent
`TESTE_NON_APPROUVE`; le Goal reste actif.

## 2026-08-30 — A598 — GREEN minimal BUG-137

Statut : **GREEN CIBLÉ — 18/18 PASS — A599 RÉGRESSIONS OUVERT**.

Un ledger atomique cumulatif est désormais ouvert une seule fois par tour
ToolAgent natif et partagé par routeur, agent source-backed, writer, revue et
repair via l'adaptateur commun. Les valeurs préenregistrées restent `12 000`
tokens et `240 000 ms`, avec réserve terminale de `4 800` tokens et `60 000 ms`.

Chaque appel réserve avant I/O les tokens d'entrée comptés exactement et la
sortie maximale. L'usage serveur valide réconcilie la réservation ; usage absent
ou erreur facture pessimiste. L'admission concurrente est verrouillée et le
temps est monotone. Les traces n'exposent aucun prompt ou contenu de preuve.

Quand l'exploration ferme, le code ne choisit pas les sources : Qwen conserve
la sélection/writer si la cardinalité minimale est atteinte, ou reçoit
`resolve_source_yield` si seules des observations partielles existent. Sans
observation ou sans réserve terminale, l'arrêt est technique et typé. Le routeur
le relance sans fallback. Aucun marqueur Cuisine, Q019 ou C004 dans les nouveaux
modules.

Le test reste byte-identique entre RED et GREEN. RED A597 : 2/18. GREEN promu :
**18/18**, TRX final SHA
`864F816075D6C64E556EC0CA32149980FB5A3D61178FB4A59D618C084DE82444`.
Une première exécution 17/18 et deux passes 18/18 intermédiaires sont conservées
mais non promues ; seule la dernière suit les durcissements temps/cardinalité.

La première cible A599 a ensuite fait 621/622 : le runner atteignait 2 599
lignes pour une limite architecturale de 2 500. A598 a été rouvert, sans changer
le seuil ou les tests. L'initialisation du tour a été extraite dans un module
partiel de 207 lignes ; le runner revient à 2 500. Le TRX fautif est conservé,
SHA `B8BD65BDF35192B99EFF9399745CDCB08B1F2E4811EB5EEA339667D3CB55C604`.

Audit final post-réouverture **113/113 PASS**. Cinq lancements non promus de ce seul audit ont
corrigé des assertions trop littérales sur les noms internes ; production,
tests et TRX sont restés inchangés. `git diff --check` passe, build servers
arrêtés, zéro Qwen, WinUI, live ou contrôle de l'ordinateur. Rapport :
`phase5/A598-RAPPORT-GREEN-MINIMAL-BUG137.md`.

**A598 EST CLOS GREEN CIBLÉ.** BUG-137 est
`CORRIGE_DETERMINISTEMENT_NON_REVALIDE_LIVE`. A599 doit exécuter les régressions
ciblées et la suite client complète. Produit et phase 5 restent
`TESTE_NON_APPROUVE`; le Goal reste actif.

## 2026-08-30 — A599 — régressions BUG-137

Statut : **PASS DETERMINISTE — 622/622 ET 1722/1722 — A600 OUVERT**.

La première cible source-backed a fait **621/622**. L'unique échec était la
limite existante de modularité : `SourceBackedAgentV2Runner.cs` faisait 2 599
lignes au lieu de 2 500 maximum. Les 621 autres tests passaient. Le TRX fautif
est conservé, SHA
`B8BD65BDF35192B99EFF9399745CDCB08B1F2E4811EB5EEA339667D3CB55C604`.

A598 a été rouvert plutôt que d'assouplir le test. La préparation du tour est
maintenant dans `SourceBackedAgentV2RunInitialization.cs` (207 lignes) et le
runner revient à 2 500. Après cette correction, BUG137 repasse 18/18 et l'audit
A598 post-réouverture 113/113.

La cible finale passe **622/622** : 18 BUG137, 320 agent V2, 32 clients LLM,
181 routeur/normalisation/budgets, 21 architecture, 18 vérificateur et 32 canaris
writer-review sans environnement live. La suite cliente complète passe
**1 722/1 722**, sans skip, soit exactement les 1 704 de la baseline A594 plus
les 18 nouveaux oracles.

Les DLL WinUI produit/harnais sont byte-identiques, SHA
`F10E385BEAF22A140756CAD3815132BA98451C4EFAF77FC86838D1A63D68F4C5`.
Audit A599 **91/91 PASS** : TRX, familles, production, tailles, anti-hardcode,
Git et ressources. `git diff --check` passe, build servers arrêtés, zéro Qwen,
WinUI, live ou contrôle de l'ordinateur. Rapport :
`phase5/A599-RAPPORT-REGRESSIONS-BUG137.md`.

**A599 EST CLOS PASS DETERMINISTE.** BUG-137 reste
`CORRIGE_DETERMINISTEMENT_NON_REVALIDE_LIVE`. A600 doit sceller le palier puis
envoyer exactement un Telegram consolidé après audits pré-envoi. Produit et
phase 5 restent `TESTE_NON_APPROUVE`; le Goal reste actif.

## 2026-08-30 — A600 — scellement BUG-137 et Telegram

Statut : **CLOS — AUDIT 146/146, MESSAGE 56/56, TELEGRAM CONFIRMÉ, POST 77/77**.

Le rapport technique final scelle le protocole, le RED 2/18 vers GREEN 18/18,
la régression de modularité initiale 621/622, sa correction sans relâcher le
seuil, la cible finale 622/622, le client complet 1 722/1 722, les onze fichiers
de production, les DLL, Git, la neutralité domaine et les limites live. Audit
technique pré-envoi : **146/146 PASS**.

Le message officiel fait 3 184 caractères, SHA
`60DE4BEF78783360221F83EBCED1AF46691BEB274601A435B271353050418135`.
Il explique en langage clair le problème des douze appels LLM, le ledger unique,
les limites 12 000/240 000, la réserve 4 800/60 000, la propriété sémantique de
Qwen, les transitions terminales, la chaîne test-first, l'incident de modularité
et l'absence de validation live. Audit du message : **56/56 PASS**.

Telegram envoyé exactement une fois le
`2026-08-30T21:44:00.9414131+02:00`, titre
`SAAIA RAG - BUG-137 corrige deterministe` : corps 3 184 caractères, total
3 301, code 0. Reçu : `phase5/00-RECU-TELEGRAM-A600.md`, SHA
`58369D8F5928A3076B679E4300CB23E527B3306BA757A4FFF4752A957A9BF424`.
Aucun second envoi autorisé.

Le premier lancement non promu de l'audit post-envoi s'est arrêté sur une
assertion `Contains` visant une phrase coupée par un retour de ligne dans le
reçu. Seul ce contrôle a été remplacé par une expression multi-ligne. Aucun
message, reçu, fichier produit ou binaire n'a changé et aucun retry Telegram n'a
été exécuté.

Audit post-Telegram final : **77/77 PASS**. Script SHA
`B57FCCB0A71EB5CBA4930D66C58D85A3EEE4980E6215A2B072F796FA0A7A701B`,
sortie SHA
`D54CEB080AE0276F01F0072E9F38EB29BA93EF1D24D32C7574CBCBD57DAE5764`.
Un message, un reçu, produit et DLL inchangés, `git diff --check` propre, zéro
ressource live et contrôle de l'ordinateur coupé.

**A596-A600 EST CLOS PASS DETERMINISTE.** BUG-137 est
`CORRIGE_DETERMINISTEMENT_NON_REVALIDE_LIVE`; produit et phase 5 restent
`TESTE_NON_APPROUVE`. Le prochain palier doit être distinct et préenregistré
avant toute validation live ; aucun replay C004. Le Goal reste actif.

## 2026-08-30 — A601 — sélection readonly et préenregistrement C005

Statut : **PASS PREENREGISTREMENT READONLY — 96/96 — LIVE INTERDIT**.

C005 est `C005_ONE_MICROSOFT_2024_ANNUAL_REVENUE_GROWTH`. La question exacte,
181 caractères et SHA
`83003E3F5A43EE40EDDE7F5D149B2947EF77EA392E798505BD22ECBB5D680636`,
demande le revenu annuel dépassé par Microsoft en 2024 et sa croissance sur un
an, en une phrase sourcée.

La découverte scoped `Finance - Compta` trouve un unique rapport Microsoft et
trois hits de sa révision. Le chunk probant
`28f2feff-200c-3097-0cea-bd3b6e08e313`, page 2, porte dans le même extrait
`over $245 billion in annual revenue, up 16 percent year-over-year`. Le SHA-256
de révision est
`96de32a720641251b44c3e27e25ce2aa7ab035a15182b6260ef0ef06aa041dc0`.

Deux formulations ont été rejetées avant gel : la première ne matérialisait pas
la valeur totale dans ses hits et exposait en plus un défaut scalaire du probe
context ; la seconde ne montrait pas ensemble les deux valeurs Microsoft Cloud.
Aucune n'a appelé Qwen, créé de session ou été promue.

Le protocole A601-A605 sépare sélection, audit, préflight, unique live et audit
final. Il fixe : réponse >245 milliards et +16 %, identité/carte SHA-256, ledger
12 000/240 000 avec réserve 4 800/60 000, objectif document nommé 45 s, hard
stop 240 s, une seule soumission, capture et ouverture de carte obligatoires.
C001-C004 restent interdits au replay.

Audit A601 **96/96 PASS** : question, preuve, identités, protocole, A600 clos,
onze fichiers produit byte-identiques, aucun marqueur C005 et zéro processus,
listener, environnement live, Qwen, UI ou contrôle de l'ordinateur. Rapport :
`phase5/A601-RAPPORT-SELECTION-CANARI-C005-MICROSOFT.md`.

**A601 EST CLOS PASS READONLY.** Aucun live n'est autorisé. A602 doit effectuer
une mesure fraîche indépendante puis auditer le gel. Produit et phase 5 restent
`TESTE_NON_APPROUVE`; le Goal reste actif.

## 2026-08-30 — A602 — audit indépendant du préenregistrement C005

Statut : **PASS AUDIT INDÉPENDANT — 129/129 — A603 AUTORISÉ, LIVE INTERDIT**.

Une mesure readonly fraîche a été exécutée le
`2026-08-30T21:55:00.5907179+02:00`. Ses réponses `/ready` et retrieval ne sont
pas des replays byte-identiques d'A601 : le SHA de réponse retrieval passe de
`1DF19DA8B036701A9E66AF3987420EB23EECE5606D59D2E034F5465389CE7864` à
`2714B3DB3F0B94EF1EF891623077155409B5325BD754B5F62667842B95EE73B7`.

L'identité et l'oracle restent stables : catégorie `Finance - Compta`, 20
résultats, trois hits du seul rapport Microsoft, docId
`b1d56467-dd72-2453-8174-719d79745032`, révision
`050eef08-1f41-ffb4-488f-f7268358f0ba`, SHA-256
`96de32a720641251b44c3e27e25ce2aa7ab035a15182b6260ef0ef06aa041dc0`,
chunk `28f2feff-200c-3097-0cea-bd3b6e08e313`, page 2 et extrait >245 milliards
avec +16 % sur un an.

Audit indépendant A602 **129/129 PASS** : clôture A600, artefacts et audit A601,
question/SHA, mesure fraîche, protocole, budgets, provenance, UI attendue,
non-rejeu C001-C004, onze fichiers produit byte-identiques, anti-hardcode, Git
et ressources. Script SHA
`B34218E94FB3E6C9E8ADAD46D06D0B26100266B060E54496CCD9190520000AD5`,
sortie SHA
`B28FB37EA998AB8C00F2C9CBE02A079DDA3A59A2E26762B9D3EFFA3F7737B546`.
Rapport : `phase5/A602-RAPPORT-AUDIT-PREENREGISTREMENT-C005-MICROSOFT.md`.

**A602 EST CLOS PASS.** A603 peut exécuter le préflight frais, les régressions,
la vérification des DLL, la découverte figée des ressources et l'armement à
blanc des scripts. Aucune soumission, aucun Qwen, aucune WinUI et aucun contrôle
de l'ordinateur ne sont encore autorisés. Produit et phase 5 restent
`TESTE_NON_APPROUVE`; le Goal reste actif.

## 2026-08-30 — A603 — préflight readonly et armement à blanc C005

Statut : **PASS PREFLIGHT — 622/622 ET AUDIT 156/156 — UN LIVE A604 AUTORISÉ**.

Le portefeuille source-backed repasse **622/622**, sans échec ni skip. Les DLL
WinUI produit/harnais sont byte-identiques, SHA
`F10E385BEAF22A140756CAD3815132BA98451C4EFAF77FC86838D1A63D68F4C5`.
TRX SHA
`36001DEBA1EEC13C56641F6148B6AA632A5E28847B06C69593075BBE32D07C66`.

La mesure readonly A603, distincte par son SHA de réponse retrieval
`467E84CAC293CA06652A9130CE2211EA01743EA93FD9CB705A3CC8F48A2606AF`,
retrouve encore le même docId, la même révision SHA-256, le même chunk
`28f2feff-200c-3097-0cea-bd3b6e08e313`, la page 2 et les deux valeurs >245
milliards / +16 %. Zéro Qwen et zéro UI.

Le runtime et le modèle locaux sont figés par SHA. Trois scripts A604 start,
stop et collecte ont zéro erreur AST. Leur armement à blanc confirme 33 sessions
initiales, répertoire officiel absent, zéro appel modèle, zéro action UI, zéro
arrêt et zéro fichier live. Le collecteur impose une session, un tour, la
question exacte, les payloads sources/tracking, les comptes outils et les lignes
du ledger 12 000/240 000 avec réserve 4 800/60 000.

Audit final A603 **156/156 PASS** : protocole, A602, mesure fraîche, dix familles
de tests, binaires, configuration, modèle/runtime, AST, armement, scripts,
onze fichiers produit, anti-hardcode, Git et ressources. Script SHA
`2A4094F7EED704734CD342B0B852F549702D0CA560C3681451ECE760DC6F45F1`,
sortie SHA
`8004ADD4E4FFBF90F256125D1DB1B56707D7A99F36FC8C0ED24E95377D90BA84`.
Rapport : `phase5/A603-RAPPORT-PREFLIGHT-READONLY-C005-MICROSOFT.md`.

**A603 EST CLOS PASS.** A604 peut lancer exactement une nouvelle discussion et
une seule soumission C005. Aucun retry, aucun replay C001-C004. Le contrôle de
l'ordinateur est autorisé uniquement pendant l'interaction WinUI et doit être
coupé dès la fermeture. Produit et phase 5 restent `TESTE_NON_APPROUVE`; le Goal
reste actif.

## 2026-08-30 — A604 — unique live WinUI C005 Microsoft

Statut : **FAIL END-TO-END — ARRÊT HONNÊTE — AUCUN RETRY**.

La question C005 exacte a été soumise une seule fois dans une nouvelle
discussion WinUI. Session `ba153476-a0cd-49f9-aca7-02e2065a8acc`, trace
`rag-20260830201321602-067b0248`. Les sessions persistées passent de 33 à 34 et
la discussion contient exactement deux messages.

Après 112 629 ms, SAAIA affiche un arrêt technique honnête : le pipeline
source-backed n'a pas pu terminer sa vérification. Aucune valeur >245 milliards
ou +16 %, aucun payload source et aucune carte ne sont produits. Verdicts :
fonctionnel FAIL, provenance FAIL, UI FAIL, performance FAIL ; budget/trace
PASS, unicité PASS et nettoyage PASS.

Le ledger unique reste à 7 156/12 000 tokens, cinq appels LLM terminés, 11 lignes
budget et une recherche documentaire réussie. Cette recherche matérialise dix
preuves mais pas le chunk oracle Microsoft page 2. Elle est précédée de deux
routes Qwen incohérentes `document` présent avec `namedReferenceKind=none`, puis
d'un plan coûteux. Quand il reste 4 844 tokens, la prochaine revue non terminale
est refusée correctement sous `terminal_budget_only` pour préserver 4 800 tokens.
Le runner remonte toutefois ce refus en panne générique au lieu de basculer vers
une décision terminale.

Capture terminale JPEG SHA
`3AB6F50799D8215FCE093F5B06CDB2191D1242BCBDF31604F79AF97A2777D06B`,
log client delta SHA
`95FDE5DA3AB3A77D7DA8566023A5351443191F0748D74C0DFB7682F378E47D39`,
session assainie SHA
`DE5EBDB393BE189EF44E7E5AE2E0030DB4F53221445F4D47F4BF82349FFD9122`.
Rapport : `phase5/A604-RAPPORT-LIVE-C005-MICROSOFT-FAIL.md`.

Le contrôle de l'ordinateur a été coupé immédiatement après fermeture de WinUI.
Runtime et listener 1234 ont été arrêtés. Aucun second live ni patch post-live.

## 2026-08-30 — A605 — audit du FAIL C005 avant Telegram

Statut : **147/147 PASS — VERDICT AUTHENTIFIÉ — TELEGRAM AUTORISÉ**.

L'audit authentifie le protocole, A603, la question, l'unicité, la session, la
capture, les sources vides, les métriques, chaque étape causale, le refus budget
typé, les binaires et onze fichiers produit inchangés, Git et l'arrêt complet des
ressources. Script SHA
`526FBEB5D0852D45F02650421FE0EA695375A34DB6027091BEA50FAF6D2DE461`,
sortie SHA
`B1C6035DB6B24393A7D79EB1A505FE4A596ADA392F6E50E06DD4CD809E4D991C`.

Une première passe non promue a seulement détecté l'extension `.png` incorrecte
pour un flux JPEG ; l'extension et l'assertion de signature ont été corrigées,
sans modifier le live ni son verdict.

**A604 EST CLOS FAIL ET A605 EST AUDITÉ.** Le prochain lot est BUG-138 :
transition terminale et réduction du coût pré-documentaire, test-first et sans
hardcode. C005 ne sera pas rejoué ; tout nouveau live utilisera un canari
distinct. Produit et phase 5 restent `TESTE_NON_APPROUVE`; le Goal reste actif.

### A605 — Telegram et clôture post-envoi

Le message officiel A605 fait 3 907 caractères, SHA
`F6BA0CEA981DEAF0C4F4C23580DDB95B28F96954AE58291FEE668D48B39921AD`.
Il explique le test Microsoft en langage clair, le FAIL honnête, les verdicts
séparés, les 112,629 secondes, les 7 156 tokens, la route incohérente répétée,
la recherche sans page 2, le refus `terminal_budget_only`, la lacune de
transition et le prochain lot BUG-138. Audit du message : **63/63 PASS**.

Le notificateur a été appelé exactement une fois le
`2026-08-30T22:24:07.2411173+02:00` avec le titre
`SAAIA RAG - C005 live en echec, cause isolee`. Il a découpé automatiquement le
rapport en deux parties de transport : `Notification Telegram envoyee en 2
parties. Corps=3907 caracteres.` Aucun retry ni second appel.

Reçu : `phase5/00-RECU-TELEGRAM-A605.md`, SHA
`E5F3476FB253C8E0FD0D7CE5AE8AD62A74D63E675E9FB50069992F64F6FDB20D`.
Audit post-envoi **96/96 PASS** : script SHA
`EDBC00FB36A518411A663D17DC90241DC5B8677DFE499B9AC904CD47D997DE0F`,
sortie SHA
`918B92D5D5CA4F27BFD3C8EAB07DEE716473AF0024E9C5C23339CEE7C5A24CBF`.
Un reçu, un fichier message, un seul live, produit et DLL inchangés, Git propre,
zéro ressource live et contrôle de l'ordinateur coupé.

**A601-A605 EST CLOS AVEC FAIL LIVE AUTHENTIFIÉ.** C005 ne sera pas rejoué.
BUG-138 devient le prochain lot déterministe : transition terminale correcte et
coût pré-documentaire, sans augmentation aveugle des budgets ni spécialisation
Microsoft. Produit et phase 5 restent `TESTE_NON_APPROUVE`; le Goal reste actif.

## 2026-08-30 — A606 — préenregistrement BUG-138

Statut : **PASS PREENREGISTREMENT — 85/85 — A607 RED AUTORISÉ**.

Le protocole `A606-A610` sépare deux défauts authentifiés par C005. BUG-138/T
raccorde le refus `terminal_budget_only` levé dans la revue rapide composée à
la transition terminale déjà existante : EvidenceBundle préservé, sélection et
writer laissés à Qwen, ou `resolve_source_yield` après observation sans preuve
finalisable. BUG-138/R remplace uniquement le repair complet du couple
`namedReferenceKind/document` incohérent par une micro-décision Qwen portant
sur ces deux champs, bornée à 96 tokens et tentée une seule fois.

Les budgets restent `12 000/240 000`, réserve `4 800/60 000`. Aucun choix de
document ou d'EvidenceId ne passe au code. C001-C005 sont interdits au replay,
et A606-A610 n'utilisent ni Qwen, ni WinUI, ni live, ni contrôle de
l'ordinateur.

Audit A606 **85/85 PASS** : preuves A604/A605, protocole, quinze oracles, HEAD,
onze fichiers baseline, défauts présents, absences BUG-138, budgets,
modularité, anti-hardcode, Git et ressources. Protocole SHA
`C4ED4413E748DE7D86897A09D85D3B48E1D6B8B1FBFE1B8A61BED3AC7CA6DF7E`,
script SHA
`9D61CAE945A71ED7ABF0D6C302E1A0EB3A6496246B8E1EF6E5F302A611CDD442`,
sortie SHA
`E4D97118DC14464AA2F99CBCF3A88DAD58E4067CDEC9E4CA69EF6B06EDE01123`.
Rapport : `phase5/A606-RAPPORT-PREENREGISTREMENT-BUG138.md`.

**A606 EST CLOS PASS.** A607 peut ajouter uniquement les tests BUG-138 et
capturer un RED causal avant toute modification production. Produit et phase 5
restent `TESTE_NON_APPROUVE`; le Goal reste actif.

## 2026-08-30 — A607 — RED causal BUG-138

Statut : **RED PROMU — 4 FAIL / 1 PASS — AUDIT 76/76**.

Le test budget-aware reproduit exactement le défaut C005 : une fast review
admise ferme l'enveloppe normale, puis
`source_backed_single_selection_scope_review_v2` est refusée avant I/O sous
`terminal_budget_only`. L'exception traverse scope review, fast review et
runner au lieu d'activer la sélection terminale. Les trois autres échecs
authentifient l'absence du contrat compact routeur, de son activation étroite et
de l'overlay Qwen limité au couple `namedReferenceKind/document`.

Le premier run non promu avait 5 échecs : le test de modularité comptait à tort
le saut de ligne final. Seul ce comptage a été corrigé. Le RED promu exécute 5
tests : 4 échecs causaux, 1 PASS de budgets/neutralité/modularité, zéro skip.

Test SHA
`5CE3776011155440D9231E64DE45329A4602856E28EEA0A33E807CE22CC276F3`,
TRX promu SHA
`913F8BDC4CDB2690A34E5E96B9FD36C9D2066B2E7A4435C37F11A6D4F02A068A`.
Audit A607 **76/76 PASS**, script SHA
`63F7064E83465E63F5DA45AFCC63BC5D543D58F1022B7CB4E676E0CC405D3A0F`,
sortie SHA
`77755CC70FDEFA60C54F5AFE8062714DE1956C7351081F5A047197C827AC4A09`.
Production byte-identique à A606, zéro Qwen/WinUI/live et contrôle ordinateur
coupé. Rapport : `phase5/A607-RAPPORT-RED-BUG138.md`.

**A607 EST CLOS RED CAUSAL.** A608 peut implémenter le GREEN minimal sans
modifier les oracles, les budgets ou la propriété sémantique de Qwen. Produit
et phase 5 restent `TESTE_NON_APPROUVE`; le Goal reste actif.

## 2026-08-30 — rapport consolidé des dernières 24 heures et checkpoint A608

À la demande de l'utilisateur, un rapport consolidé couvrant la fenêtre
2026-08-29 22:56 → 2026-08-30 22:56 +02:00 a été créé :
`RAPPORT-2026-08-30-24-DERNIERES-HEURES-RAG-ULTRA-COMPLET.md`, 343 lignes,
27 092 octets, SHA
`EBE1EAA23CA2779AA4051003B4982BBB9B5FE5413F92FB2C5362D3B23DDC190C`.

Le rapport recoupe A420–A608, les campagnes live, les RED/GREEN, les suites,
les audits, les 42 reçus Telegram de la fenêtre, les incidents non promus, Git,
les ressources et les limites d'approbation. Il ne requalifie aucun verdict.

Checkpoint A608 : deux itérations ciblées ont fait **4/5**, donc aucune n'est
promue. Les trois contrats de repair routeur passent. Après propagation du
statut terminal de la sélection au writer, le scénario budget atteint désormais
le writer puis une revue sémantique finale absente du double de test. A608 reste
`EN_COURS`, sans audit final, sans A609 et sans Telegram. Produit et phase 5
restent `TESTE_NON_APPROUVE`; le Goal reste actif.

À la demande de l'utilisateur, la session de contrôle Windows a été
explicitement réinitialisée. Le rapport et les vérifications finales ont été
effectués sans interaction graphique ; aucune ressource SAAIA/Qwen ni listener
1234 n'est actif.

### Transmission Telegram corrective du rapport 24 h

Après constat explicite de l'utilisateur que le rapport n'avait pas été envoyé,
le fichier complet a été transmis sur Telegram le
`2026-08-30T23:04:45.8904810+02:00`, titre
`SAAIA RAG - rapport complet des dernieres 24 heures`. Le notificateur a été
appelé exactement une fois et a confirmé :
`Notification Telegram envoyee en 9 parties. Corps=26312 caracteres.` Aucun
retry ni second appel.

Audit avant envoi : rapport et notificateur présents, reçu antérieur absent,
SHA du rapport conforme, zéro marqueur de secret et zéro caractère Unicode de
remplacement. Audit post-envoi : rapport inchangé, un seul reçu, SHA du reçu
`3EB9D4847483AA39F8B43D8C3E1D9999734701E22F6952D17106B34B78956105`.
Reçu : `phase5/00-RECU-TELEGRAM-RAPPORT-24H-20260830.md`.

### A608 — GREEN minimal BUG-138

Après deux itérations diagnostiques non promues à **4/5**, la cause résiduelle a
été corrigée sans ajouter de résultat au faux LLM : la décision primaire valide
de Qwen est désormais conservée lorsque seule la revue optionnelle de portée est
refusée avant I/O par `terminal_budget_only`. Le code ne choisit aucune preuve ;
il conserve seulement l'identifiant et le libellé vus par Qwen, puis exige la
sélection terminale explicite de Qwen avant le writer.

Le trajet terminal promu exécute trois appels LLM : fast review, sélection
terminale, writer. La tentative de scope review reste observée mais non exécutée.
Le micro-repair routeur est borné à `namedReferenceKind/document`, 96 tokens,
une activation exacte et une revalidation canonique.

TRX A608 : **5/5 PASS**, 0 échec, 0 skip, SHA
`6F76D54C457090BDE71F1A39FFE6CFB2E493A93F3277672335E6317182093528`.
Test BUG-138 byte-identique au RED. Runner : 2 500 lignes. Budgets et fichiers
gelés inchangés. DLL produit/harnais byte-identiques, SHA
`D7CA153F0435669407EB95D3751EFFC2E9183EF5F4247482EB61CCD5AD5B3EF6`.

Audit A608 **80/80 PASS**, script SHA
`83EE4649915A2549319E361A4D6558173CF803D59EFB840690DE9B8E42559895`,
sortie SHA
`2044F0E6B1E3F9E0FEAF304F1F0C1DA75A4DD3B99DE5EE41375F6453CF360044`.
Une première invocation d'audit non promue s'est arrêtée sur une empreinte A607
mal recopiée dans le nouveau script ; elle a été corrigée par la valeur calculée,
sans assouplir aucun oracle, puis l'audit entier a réussi.

Rapport : `phase5/A608-RAPPORT-GREEN-MINIMAL-BUG138.md`, 123 lignes, SHA
`C654413C92F0A38EEEA55ACC5AAC558686E4F7680B0184EF6F1AD918CFEBD1C3`.

**A608 EST CLOS GREEN DÉTERMINISTE NON REVALIDÉ LIVE.** A609 peut exécuter les
régressions ciblées puis la suite cliente complète. Produit et phase 5 restent
`TESTE_NON_APPROUVE`; le Goal reste actif.

### A609 — régressions BUG-138

Le portefeuille source-backed ciblé passe **627/627**, zéro échec, zéro skip.
Sa partition exacte est : BUG-137 18, BUG-138 5, agent V2 320, clients LLM 32,
normalisation routeur 105, document overview 6, budgets prompt routeur 70,
architecture 21, verifier 18 et canaris déterministes désarmés 32. TRX SHA
`44F7858564F9963D4D086EB5CFCE4C9E710ED7F8498F0847BF0FC07F69885E83`.

La suite cliente canonique `Debug x64` passe **1 727/1 727**, zéro échec, zéro
skip. L'écart exact de 5 avec A599 correspond aux cinq oracles BUG-138. TRX SHA
`BDA1743294233ACD34D41012C84712E40463D6EAF0DCDD2C11F31ADB62962BC1`.

DLL produit/harnais byte-identiques, 8 443 904 octets, SHA
`6A0A825E2C233B95BA53346D61CA3BEC113E5C75B68D6507EC012E2C6F1D6498`.
Runner 2 500 lignes, budgets et fichiers gelés inchangés, anti-hardcode et
`git diff --check` conformes. Serveurs de build arrêtés, zéro ressource live,
listener 1234 ou contrôle ordinateur.

Audit A609 **81/81 PASS**, script SHA
`1B9C1DBBCDF7E61B7AC90B81FE50A6FAE63B24CE0BF36A3B9A945C3244D90AED`,
sortie SHA
`5A2A49C8A6D521D878888C29D2324B6ED1FEFB84CF9F6F631C94C3D59D004CB2`.
Une première invocation non promue s'est arrêtée sur une capture PowerShell qui
mélangeait logs et XML ; seul le script d'audit a été corrigé, puis rejoué sans
changer aucun oracle ni artefact produit.

Rapport : `phase5/A609-RAPPORT-REGRESSIONS-BUG138.md`, 111 lignes, SHA
`026DC3C01EA1C4550037701975BBAD80927FCF4D401CB559D95BD21C93459052`.

**A609 EST CLOS GREEN DÉTERMINISTE NON REVALIDÉ LIVE.** A610 doit maintenant
sceller BUG-138 et envoyer exactement un Telegram audité. Produit et phase 5
restent `TESTE_NON_APPROUVE`; le Goal reste actif.

### A610 — scellement et Telegram BUG-138

Le rapport technique final borne le verdict à
`CORRIGE_DETERMINISTEMENT_NON_REVALIDE_LIVE`. Il rappelle la perte de la
décision Qwen au seuil terminal, le micro-repair routeur, les preuves A606-A609,
les invariants et tout ce qui n'est pas approuvé. Rapport SHA
`6207AC0A72F05D010F6B2D50F1B92E128DFB4A52E215D2B9B007072413EEB9F1`.

Le message Telegram fait 2 270 caractères, SHA
`F19A3DDE2E943AE5BB955A64C644C230CAC7D6A7035A0A135D921D67D94AF495`.
Audit pré-envoi **41/41 PASS**, script SHA
`B1FC57F86B347287D55EF2F33932838342C51D3F8F758C44CF301A90CE3C05EE`,
sortie SHA
`2BC7FB799DCB063734FEDB5A95A502D98ACFAE000DBFBE878406A13DDD7E6102`.

Le notificateur a été appelé exactement une fois le
`2026-08-30T23:21:58.1305961+02:00`, titre
`SAAIA RAG - BUG-138 corrige deterministement`. Sortie confirmée :
`Notification Telegram envoyee. Corps=2270 caracteres. Total=2391 caracteres.`
Aucun retry et aucun second appel.

Reçu SHA
`00DA721BE427034B27ABFFDC2D7BEBF07F01DC4145412C7ACA7DCC1EBDE07119`.
Audit post-envoi **37/37 PASS**, script SHA
`A9CBAFD7F0BEEE0183DCABA6AA8A1B1EBEDAF567B635F7E01C28A34C5F1C252F`,
sortie SHA
`721C2711A34A2F412A83E1638C2E49A87D811CF51E48399379E69A463F25289C`.

Rapport de scellement : `phase5/A610-RAPPORT-SCELLEMENT-BUG138.md`, 69
lignes, SHA
`075F1B2739015149B79653A9F8281FBA71DA53974F73669E2325B1F7B5D53CCA`.

**A610 ET LE LOT DÉTERMINISTE BUG-138 SONT CLOS.** BUG-138 est corrigé et
couvert par 5/5, 627/627 et 1 727/1 727, mais n'est pas revalidé live. Produit
et phase 5 restent `TESTE_NON_APPROUVE`; le Goal global reste actif. Tout futur
live doit appartenir à un protocole distinct avec canari neuf.

### A611 — sélection readonly et préenregistrement C006 NIST

Le prochain canari distinct est `C006_NIST_CSF_GOVERN_ROLE` :
« Selon NIST_CSF_2_0.pdf, quel rôle la fonction GOVERN joue-t-elle par rapport
aux cinq autres fonctions ? Réponds en une phrase et cite la source. » Question
145 caractères, SHA
`A27E5092BE78596E48C2EFBE0F1B2EEF6FF82C1125528D8C9C4659749D711BB8`.

La découverte readonly authentifie un document unique dans `Certifications`,
docId `8ad2e14a-471d-2457-031c-98c99616ee17`, révision
`69beed47-50e2-47e7-6f5f-ad01605ed318`, SHA-256
`3c31f46fee98cac0c4323453e5109291a213b4de7fef8c058af9bf67f717433c`,
chunk `2c7582e8-ae1b-e53e-1f64-8de0522cbc5d`, page 8. L'extrait contient
l'oracle complet du rôle de GOVERN envers les cinq autres fonctions.

Deux formulations exploratoires ont été rejetées avant gel parce que leur
fenêtre visible ne portait pas l'oracle complet. Elles n'ont lancé ni Qwen, ni
UI, ni session live. La troisième formulation seule est promue.

Protocole A611-A615 : 267 lignes, SHA
`89E8B6AE4ABF375BDC559B20A5C9E3CEC5898318841E0B17739BF1952FF93CA0`.
Discovery SHA
`066742F0D0A740320EE65686C1C0A68FE9EA5A87AFC498CB16DB0D684FEDA3BA`.
Audit A611 **58/58 PASS**, script SHA
`951B574128E835D724CF6D1701AAE413B809DDA7CCD0796E1876D1D03663F597`,
sortie SHA
`438D465139A924E9FA2F1C41B16483995D12DBD9D84916DFB119B7C4F913C03A`.
Rapport A611 : 85 lignes, SHA
`21B499BA30683EB049B31C5AD3E7B0694644B74C3CC15EEB2C5CF63EBCE52EBB`.

**A611 EST CLOS, LIVE INTERDIT.** A612 doit auditer indépendamment le gel avant
tout préflight lourd. Produit et phase 5 restent `TESTE_NON_APPROUVE`; le Goal
reste actif, contrôle ordinateur coupé.

### A612 — audit indépendant du préenregistrement C006

A612 a parsé directement les objets JSON bruts de la découverte A611 : une
ligne catalogue, un probe `Certifications`, trois hits, un seul docId et une
seule révision. Un seul hit oracle porte le chunk
`2c7582e8-ae1b-e53e-1f64-8de0522cbc5d`, page 8, avec les deux clauses requises.
Le hit `docmeta` est sans page et reste navigation-only.

L'audit authentifie aussi le Goal, A610, A611, les TRX 5/5, 627/627 et
1 727/1 727, le binaire x64, les budgets 12 000/240 000 et la réserve
4 800/60 000. Zéro ressource live ou contrôle ordinateur.

Audit A612 **71/71 PASS**, script SHA
`55B07C7070F8A8595101F194B3E85E7E8282EB1B31B378D426968D53DA8EF4FA`,
sortie SHA
`22BFB38855BBD218F9C96DB93D1AFB46BC99314E7B9B49C7379E4BCAF0441191`.
Une première invocation non promue s'est arrêtée parce qu'une variable
PowerShell écrasait la liste de logs par insensibilité à la casse ; seul le
script d'audit a été corrigé, puis rejoué sans changer un oracle.

Rapport A612 : 70 lignes, SHA
`2A94735209DD1C632D3E5EB34C1C320C50B87694E93A41E59E84279A8738C334`.

**A612 EST CLOS, LIVE ENCORE INTERDIT.** A613 doit effectuer le préflight frais
et l'armement à blanc. Produit et phase 5 restent `TESTE_NON_APPROUVE`; le Goal
reste actif.

### A613 — préflight frais et armement à blanc C006

La mesure readonly du `2026-08-30T23:34:41.3456902+02:00` retrouve le même
document NIST, la même révision, le même SHA, le même chunk oracle et la page 8
qu'A611. Aucun appel Qwen ou WinUI n'est intervenu.

Le portefeuille source-backed ciblé passe **627/627** et la suite cliente
canonique complète passe **1 727/1 727**, sans échec ni skip. TRX SHA respectifs
`0EF200622CD8C60F575CFA8E5B2AA31A0130511C7B25AFB29D342373F00D9D56`
et `603C1EEC8C0ADA6E5D213203E5F5B3317B64721BA0F2DF2698CC262CA1447BD1`.
La DLL produit et celle du harnais sont byte-identiques, 8 443 904 octets, SHA
`6A0A825E2C233B95BA53346D61CA3BEC113E5C75B68D6507EC012E2C6F1D6498`.

Le harnais A614 adapte en mémoire les trois scripts C005 historiques après
vérification de leurs empreintes. L'armement à blanc parse les wrappers et le
texte réellement adapté, vérifie la question, le backend, les sessions, le
runtime et la collecte, sans créer le répertoire officiel ni démarrer de
processus. Zéro completion, zéro action UI, zéro fichier collecté, zéro listener
1234 et contrôle ordinateur coupé. Sortie d'armement SHA
`05FA20427F10F1602CEB6A12D8A04DCD6CB3A0A8E8FECAAE015789A9C9045A2B`.

Audit final A613 **172/172 PASS**, script SHA
`554A39541B806735CA78E31EBB1BEEB1ADEF2CDCF2BBB4F07C5AEA001DCFE107`,
sortie SHA
`603182B66FFFBFD8423983BCB94E32FA91DE436DAF3942C575A7BEE60FBD095B`.
Deux passages non promus ont révélé uniquement une empreinte A610 mal recopiée
puis un libellé de journal inexact ; seules ces constantes d'audit ont été
corrigées avant le passage complet, sans assouplissement.

Rapport : `phase5/A613-RAPPORT-PREFLIGHT-READONLY-C006-NIST.md`.

**A613 EST CLOS PASS.** A614 est autorisé à exécuter exactement **un** live
C006, sans retry ni autre canari, puis à arrêter Qwen et le contrôle ordinateur
et à collecter tous les artefacts. A615 seul pourra auditer et décider. Produit
et phase 5 restent `TESTE_NON_APPROUVE`; le Goal reste actif.

### A614–A615 — live WinUI C006 NIST et audit causal

La question C006 exacte a été soumise une seule fois dans une nouvelle
discussion WinUI. Session `218d9be0-c404-42b7-9aaf-78b6e34f5d5c`, trace
`rag-20260830214857806-5bdf767d`, inventaire de sessions **34 → 35**, exactement
deux messages et aucun retry.

Réponse visible et persistée : « Les sources consultees ne suffisent pas pour
produire une reponse fiable et sourcee. » Verdicts : fonction FAIL, provenance
FAIL, UI FAIL, performance FAIL ; unicité PASS, budget/trace PASS, nettoyage
PASS. BUG-138 terminal reste `NON_EXERCE_LIVE`; son micro-repair routeur est
exercé une fois et réussit.

Cause authentifiée : la route Qwen acceptée porte
`source_requested_document="NIST_CSF_2_0.pdf"` et
`source_named_reference_kind="document"`, mais le résolveur reçoit ensuite
`requested_reference=Selon NIST_CSF_2_0.pdf`, longueur 22. Ce faux nom produit
`NotFound`, met en quarantaine l'action `rag.search` choisie par Qwen et mène à
une insuffisance sur zéro observation documentaire. La mesure fraîche A613
prouvait pourtant le document, sa révision, le chunk oracle et la page 8.

Mesures : **68 029 ms**, **4 670/12 000 tokens**, 5/5 appels LLM, 10 lignes
ledger cohérentes, **0** appel documentaire, **0** source, **0** carte et **0**
writer. Capture terminale SHA
`F9B7CA6313040B3D6D890E8875D4890524F5C0927E835E46E099CEE1DA7FF3F6`.

Audit A615 **220/220 PASS**, script SHA
`E899717721CAD9F1A777C08CFB05E758D6DE0488C2CD59B841FC42AE902AAE4A`,
sortie SHA
`93BD8C5B3B5DF2C1920A6B17313606B27008A6C0EFB7B36F323E0BDDC571559B`.
WinUI, Qwen, listener 1234 et contrôle ordinateur sont arrêtés ; aucun patch
post-live n'a précédé le verdict.

Rapports : `phase5/A614-RAPPORT-LIVE-C006-NIST-FAIL.md` et
`phase5/A615-RAPPORT-AUDIT-C006-FAIL-REFERENCE-CANONIQUE.md`.

**A614 EST CLOS FAIL ET A615 EST AUDITE AVANT TELEGRAM.** La classe candidate
`BUG139_CANONICAL_NAMED_REFERENCE_TRANSPORT` est ouverte. Le replay C006 reste
interdit. Après Telegram et audit post-envoi, le prochain lot devra
préenregistrer BUG-139 et construire un RED/GREEN généraliste avant tout nouveau
live. Produit et phase 5 restent `TESTE_NON_APPROUVE`; Goal actif.

#### Clôture communication A615 — Telegram confirmé

Le message `telegram-a615-c006-fail-bug139.md`, 2 801 caractères, SHA
`2279798BC2493321C3B7C0F8E4559C9BEA578FF975729389462FCD075332A5A9`,
a été audité **58/58 PASS** avant envoi. Il explique en langage clair l'objectif,
la réponse insuffisante, la référence altérée, les axes PASS/FAIL, les mesures,
l'absence de retry et le prochain lot généraliste.

Le notificateur a été appelé exactement une fois du
`2026-08-30T23:59:59.5190632+02:00` au
`2026-08-31T00:00:00.1523128+02:00`, titre
`SAAIA RAG - C006 echec causal BUG-139`. Confirmation exacte :
`Notification Telegram envoyee. Corps=2801 caracteres. Total=2915 caracteres.`
Code 0, une partie, zéro retry.

Reçu : `phase5/00-RECU-TELEGRAM-A615-C006-FAIL-BUG139.md`, SHA
`6D02BDA5EC4DD8D00049EB6A4A4D86199CCA381759AA1210890AE82ABEE8AB54`.
Audit post-envoi **54/54 PASS**, script SHA
`99EF8DC5C692C7BD3CF81B4F98D29F8AABF96A08DD1F2E3E9255004BA72B7A2D`,
sortie SHA
`2E0F6B1CCBD19CF92BCDFF54E32AE14CE50C04398703052EBBFCEDB7411097F9`.
L'audit confirme un seul message, un seul reçu, les artefacts live inchangés,
Git conforme et toutes les ressources arrêtées.

**A615 EST CLOS FAIL END-TO-END C006, TELEGRAM CONFIRME.**
`BUG139_CANONICAL_NAMED_REFERENCE_TRANSPORT` est désormais la première lacune
active. Le prochain palier doit préenregistrer ses invariants, construire un RED
causal généraliste, faire le GREEN minimal puis les régressions. Aucun replay
C006 et aucun nouveau live avant cette chaîne. Produit et phase 5 restent
`TESTE_NON_APPROUVE`; le Goal reste actif ; contrôle ordinateur coupé.

### A616 — préenregistrement BUG-139 transport canonique

Le protocole `A616-A620` fige la cause avant tout test ou patch de production.
Le plan Qwen C006 finalement accepté portait exactement
`namedReferenceKind=document` et `RequestedDocumentName=NIST_CSF_2_0.pdf`,
mais le constructeur du `SourceBackedIntake` a donné priorité à une extraction
regex de la question brute et transmis `Selon NIST_CSF_2_0.pdf`. Le faux
`NotFound` est donc une perte de contrat entre routeur et intake, pas un défaut
de corpus ni une insuffisance décidée à partir d’observations documentaires.

Le contrat préenregistré est généraliste : pour `Origin=Llm`, la mission typée
validée est l’autorité ; le code applique seulement `Trim` et
`Path.GetFileName`. `subject` et `none` ne peuvent pas être promus par une regex
tardive. Le document focalisé et les fallbacks locaux/historiques sont
préservés. Aucun wrapper lexical, hardcode de langue, document, catégorie ou
canari ne sera ajouté.

Audit A616 **76/76 PASS**. Protocole : 256 lignes, SHA
`6C40A38F81071E172A91FC54D7480A63BAD9C1DD28B6624E153B15A26C58D1A0`.
Script SHA
`EE64A7E7FAD6895246882BC47E9B24A558A7EEDB4AA9B52552A8101D34B9B3E0`,
sortie SHA
`89CCE16641A86BEE7AE0984BB40BF88DEABF6ECCA712D38AE664F5B752AD0F25`.
Trois invocations non promues ont corrigé uniquement des assertions du script
d’audit ; production et oracles sont restés inchangés.

Rapport : `phase5/A616-RAPPORT-PREENREGISTREMENT-BUG139.md`.

**A616 EST CLOS PASS_PREENREGISTREMENT.** A617 est autorisé à ajouter le hook
minimal et les tests BUG-139, puis à produire un RED causal tout en conservant
`ToolAgentOrchestrator.SourceBackedRagRouting.cs` byte-identique. Qwen, WinUI,
live, replay C006 et contrôle ordinateur restent interdits. Produit et phase 5
restent `TESTE_NON_APPROUVE`; Goal actif.

### A617 — RED causal BUG-139

Le filtre BUG-139 compile en `Debug x64` et produit **6 FAIL / 5 PASS / 11
TOTAL**, zéro skip. Les échecs reproduisent deux documents LLM écrasés par une
capture plus large, les missions `subject` et `none` promues à tort, une
identité focalisée remplacée et une mission LLM défensivement invalide
reconstruite depuis le texte brut. TRX promu SHA
`A9A3508C1057C05F765DF78C47BD9467F1F6F23E2474E932177A9054EFA74710`.

Les cinq contrôles verts prouvent que le basename mécanique et les fallbacks
locaux `question`, `docPath`, `docRef`, `document` doivent rester fonctionnels.
Le fichier causal de production est byte-identique à A616, SHA
`250A26DE233FCE8E85E0883D4BD546A136AC3C3050E91019D0FAD76AA3099B15`.
Seuls deux hooks DEBUG et 197 lignes de tests ont été ajoutés.

Une première exécution non promue attendait trop du nettoyage historique du
fallback local ; seule sa fixture a été citée explicitement. Un premier audit
non promu utilisait `.Count` sur un scalaire sous StrictMode ; seul le script
d’audit a été corrigé. Aucun oracle causal ou code de production n’a changé.

Audit A617 **67/67 PASS**, script SHA
`47CC6D3E06029D48783DF0BB2FE058F1EC2A61D766F042B506BEFCFA498B11E3`,
sortie SHA
`59AECDAA485347DFBED9F1B3C77E53257BEBAD39C43C4395857D2F5CBBC44DAE`.
Rapport : `phase5/A617-RAPPORT-RED-BUG139.md`.

**A617 EST CLOS PASS_RED_CAUSAL.** A618 est autorisé à modifier uniquement la
frontière de transport pour donner priorité à la mission LLM typée, sans regex,
hardcode ou changement sémantique. Le GREEN doit rendre 11/11 tout en préservant
les cinq contrôles. Qwen, WinUI, live, replay C006 et Computer Use restent
interdits. Produit et phase 5 restent `TESTE_NON_APPROUVE`; Goal actif.

### A618 — GREEN minimal BUG-139

Les deux résolveurs du `SourceBackedIntake` donnent désormais priorité au
couple de la mission quand `Origin=Llm`. `document` transporte le filename de
mission après seulement `Trim`/`Path.GetFileName`; `subject` et `none` ne sont
plus promus par le texte brut ; le document focalisé préserve son identité. Les
routes hors contrat LLM conservent l’extraction et les arguments historiques.

Le RED **6 FAIL / 5 PASS** devient **11/11 PASS**, zéro skip, avec le test
byte-identique. TRX final SHA
`C9EF7D7EF3DC271626ED25530D5316D1CFBD622A1D21D386E67CA35606CD8676`.
Un seul fichier fonctionnel change, 187 lignes, SHA
`BB4EDCAE9FC50EAD86E499AAF7215604C86EB7FABA1AC1FE608A8880B35ED5AC`.
Regex, routeur natif, pipeline, contrats, budgets et tests BUG-135/138 restent
byte-identiques. DLL produit/harnais : 8 444 416 octets, SHA identique
`D888C52A241E0054E9A93CAAAA8BAAB21FFE463CC63720549A2B3E1E0D5F2A67`.

Audit A618 **71/71 PASS**, script SHA
`875F823C0576CD7387B44C22CB3C3CE211C781D5FF5500AC80B238D50D0996F0`,
sortie SHA
`7605AB18875F28DDD0E81874083400931BE37E515E55863B7B89807A25E1A781`.
Une exécution GREEN non finale et une correction de constante d’audit sont
tracées ; aucun oracle n’a été relâché. Rapport :
`phase5/A618-RAPPORT-GREEN-BUG139.md`.

**A618 EST CLOS GREEN DETERMINISTE NON REVALIDE LIVE.** A619 doit exécuter les
régressions BUG-135/138/139, le portefeuille source-backed ciblé puis la suite
cliente complète, avec contrôle des DLL, de l’anti-hardcode, de Git et des
ressources. Qwen, WinUI, live, replay C006 et Computer Use restent interdits.
Produit et phase 5 restent `TESTE_NON_APPROUVE`; Goal actif.

### A619 — régressions BUG-139

Le portefeuille source-backed étendu passe **647/647**, zéro échec et zéro
skip. Il couvre BUG-135/137/138/139, 320 tests agent V2, 32 clients LLM, 105
normalisations routeur, 6 contrats overview, 70 budgets de prompt, 21 contrats
d’architecture, 18 vérifications de sources et 32 canaris déterministes
désarmés. TRX SHA
`D3DA668D3CD2EE4668539780E37A0897901F7F07449575307E4C76F395B2643E`.

La suite cliente canonique complète passe **1 738/1 738**, zéro échec et zéro
skip. L’écart exact de 11 avec A613 correspond aux onze oracles BUG-139. TRX
SHA `C3C2EE4C2A5005EAACC9BB35C0D008D0BB105E5C36A1D6E4EE1336DA377C2237`.

DLL produit/harnais byte-identiques, 8 444 416 octets, SHA
`D888C52A241E0054E9A93CAAAA8BAAB21FFE463CC63720549A2B3E1E0D5F2A67`.
Routing, test BUG-139, runner, budgets et frontières gelées inchangés ; runner
2 500 lignes ; anti-hardcode et `git diff --check` conformes ; toutes les
ressources arrêtées.

Audit A619 **88/88 PASS**, script SHA
`A969B1022717DDA214186086F08C3F4D341524F3A831C76C19644E818AA92B33`,
sortie SHA
`BBF4AB37CA5C5719109BDFF2F88B7A23F9A7B804EBBB1B323BDC25B18BC40A8A`.
Une première invocation non promue a corrigé uniquement la capture XML du
script d’audit. Rapport : `phase5/A619-RAPPORT-REGRESSIONS-BUG139.md`.

**A619 EST CLOS PASS_REGRESSIONS_DETERMINISTES.** BUG-139 est corrigé
déterministement mais non revalidé live. A620 doit sceller la chaîne A616-A619,
auditer puis envoyer exactement un Telegram et auditer le reçu. Aucun live,
replay C006 ou Computer Use avant la clôture. Produit et phase 5 restent
`TESTE_NON_APPROUVE`; Goal actif.

### A620 — scellement et Telegram BUG-139

Le rapport technique final borne le verdict à
`CORRIGE_DETERMINISTEMENT_NON_REVALIDE_LIVE`. Le message Telegram explique en
langage clair le faux nom documentaire, la priorité rendue à la mission Qwen,
la préservation des fallbacks, les preuves RED/GREEN, 647/647 et 1 738/1 738,
ainsi que l’absence de validation live.

Message : 2 122 caractères, SHA
`AD81D6D160A484C29A040DB21AB7C4CFA7FE90CB42FA201D6E8365BD97B80C5D`.
Audit pré-envoi **75/75 PASS**, script SHA
`CDB8D7E8E21B67407803F05A5A4399646A784AF6701D14EF8D1CF216A8FD715B`,
sortie SHA
`76467B7CCEA6584893F4FD1D212370EC654F88B00BC00DF806EA9EE504B63DEF`.

Le notificateur a été appelé exactement une fois du
`2026-08-31T00:33:28.3203620+02:00` au
`2026-08-31T00:33:28.6888828+02:00`, titre
`SAAIA RAG - BUG-139 corrige deterministement`. Confirmation :
`Notification Telegram envoyee. Corps=2122 caracteres. Total=2243 caracteres.`
Code 0, une partie, zéro retry.

Reçu SHA
`24E5F3A82824DC622E16003CFB69E8774F81B586EB12A43A52F694414F5F8D97`.
Audit post-envoi **48/48 PASS**, script SHA
`16F9830761B5840A02392B24CFA8833F3CBC0ED4E14109653D2A6FB985910D9D`,
sortie SHA
`39BB43AC21DF3E131274425CF6F8FA298F0DE94CEA9EC296D9D58F5DC01CC8F4`.
Un seul message et un seul reçu A620 existent ; toutes les ressources sont
arrêtées.

Rapport de scellement : `phase5/A620-RAPPORT-SCELLEMENT-BUG139.md`.

**A620 ET LE LOT BUG-139 SONT CLOS.** BUG-139 est corrigé au niveau code et
tests, non revalidé live. Le prochain lot doit sélectionner readonly un canari
neuf, figer son oracle et son état corpus, puis suivre un protocole live
distinct ; C006 reste interdit au replay. Produit et phase 5 restent
`TESTE_NON_APPROUVE`; Goal actif ; Computer Use coupé.

### A621 — sélection readonly et préenregistrement C007 Adelphi

Le canari neuf `C007_RH_ADELPHI_PAID_SICK_LEAVE` est gelé sur la question
anglaise exacte demandant quelles catégories d'employés reçoivent le congé
maladie payé selon `Adelphi_Employee_Handbook_2026.pdf`. Question : 147
caractères, SHA
`72D511E881875EFC56042C3D450E7641F9550CAAB8CCA0C7537CC283357EC28D`.

L'inventaire readonly contient 200 documents et une seule correspondance exacte
Adelphi : docId `85035756-a926-8374-70d5-4ba6de51e4ab`, chemin
`RH/PDF/Adelphi_Employee_Handbook_2026.pdf`, 12 pages. Le contexte actif porte
la révision `b4e60d57-e3df-7789-cad8-0a7fe2676df1` et le SHA-256 canonique
`7bd296aec7594e515ac1d6cb4b20f80b01f2b7ae06e1324b38a9a4b477c4fbe8`.

La question nommée brute renvoie seulement un `docmeta` sans page : il résout
l'identité mais ne constitue pas une preuve. Une requête sémantique readonly
sans réponse injectée retrouve le chunk
`65826cb4-47be-84b3-5541-2e7bfe11d34c`, page 11, qui énumère exactement les
employés à temps plein, à temps partiel, payés à l'heure et étudiants. Le
contexte page 11 authentifie le même chunk et la même révision.

Le risque de provenance est gelé explicitement : la carte de contenu la mieux
classée porte le bon titre/page mais expose un hash catalogue de 32 caractères
et une preuve vide. Elle reste navigation-only ; le live devra produire une
source hydratée au SHA-256 canonique de 64 caractères.

Le protocole A621-A625 compte 375 lignes, SHA
`4D52A3E7821F2D9D55D436EEE439DADB92E98EDD0AA59F68114F25FB619ABBB7`.
Il impose A622 audit indépendant, A623 préflight frais et armement à blanc,
A624 un unique live WinUI, puis A625 audit et Telegram. C001-C006 restent
interdits au replay ; aucun hardcode Adelphi/oracle/page n'est autorisé.

Audit A621 **126/126 PASS**, script SHA
`2BD2C8808B258341C11E41E9C85D0741A12A9BFFE6E6087BE3C532B20F7D66F5`,
sortie SHA
`709EBE04708C27AF59A2A63901A18C230FFD3C77B046CD5F637034D2C26BE275`.
Zéro Qwen, UI, live, listener 1234, variable live ou Computer Use.

Rapport : `phase5/A621-RAPPORT-SELECTION-C007-ADELPHI.md`.

**A621 EST CLOS PASS SÉLECTION.** Le live reste interdit avant A624. A622 doit
maintenant reconstruire et authentifier indépendamment toutes les preuves,
figer les budgets/binaires/artefacts et confirmer l'absence de live. Produit et
phase 5 restent `TESTE_NON_APPROUVE`; Goal actif ; Computer Use coupé.

### A622 — audit indépendant du préenregistrement C007

L'audit reconstruit les objets JSON des sorties A621 et confirme sans appel
réseau : un seul `docmeta` initial sans page, trois hits documentaires sur la
requête de preuve, un seul chunk oracle page 11, puis un contexte de neuf chunks
portant le même texte et la même identité canonique. La carte de contenu page 11
au hash 32 et à preuve vide est conservée comme risque explicite, jamais promue
en preuve.

Le Goal, le plan dynamique, A619-A621, A620 et son reçu Telegram, la question,
le protocole, les budgets et toutes les identités sont authentifiés. La baseline
A619 reste **647/647** et **1 738/1 738**, zéro fail/skip. DLL produit et harnais
byte-identiques : 8 444 416 octets, SHA
`D888C52A241E0054E9A93CAAAA8BAAB21FFE463CC63720549A2B3E1E0D5F2A67`.

Audit A622 **125/125 PASS**, script SHA
`FAB0FC049979E24445ECF23A43CCE67D81E260D60A30BB0B87C25C61C1737891`,
sortie SHA
`5A497B400CC7F7E8B08B757E9CD5C0AB685130134B7ED14D682FC94DAA45D95A`.
Zéro Qwen, UI, live, listener, variable live, répertoire live ou Computer Use ;
Git conforme.

Rapport : `phase5/A622-RAPPORT-AUDIT-PREENREGISTREMENT-C007-ADELPHI.md`.

**A622 EST CLOS PASS AUDIT.** A623 doit maintenant produire une mesure corpus
fraîche, rejouer les suites déterministes, figer le runtime et armer à blanc les
scripts d'unique live. Le live reste interdit jusqu'au PASS complet A623.
Produit et phase 5 restent `TESTE_NON_APPROUVE`; Goal actif ; Computer Use
coupé.

### A623 — préflight frais et armement à blanc C007

Les trois mesures corpus fraîches retrouvent le même `docmeta` initial, les
trois mêmes hits de preuve, le chunk
`65826cb4-47be-84b3-5541-2e7bfe11d34c` page 11, la révision
`b4e60d57-e3df-7789-cad8-0a7fe2676df1` et le SHA canonique
`7bd296aec7594e515ac1d6cb4b20f80b01f2b7ae06e1324b38a9a4b477c4fbe8`.

Les régressions fraîches passent **647/647** puis **1 738/1 738**, zéro
fail/skip. TRX SHA respectifs
`5E81D0A8A3FBD00BCEA30FCB55D3E60CD53DA9B5F4872A5E708FDF7AD83852CF`
et `155F24A7817062C29ED7A2E6EC82F25F8EFA76FA1F1E7C0775B39BEBD0D905C3`.
Les serveurs de build ont ensuite été arrêtés.

DLL produit/harnais : 8 444 416 octets, SHA
`D888C52A241E0054E9A93CAAAA8BAAB21FFE463CC63720549A2B3E1E0D5F2A67`.
EXE, Contracts, Qwen 4B Q5_K_M et `llama-server.exe` sont figés. Paramètres :
local géré, `127.0.0.1:1234`, `deep`, température 0,1, sortie 1 600 tokens,
budget cumulatif 12 000/240 000 ms et réserve 4 800/60 000 ms.

L'adaptateur A624 vérifie les scripts historiques, remplace question, hashes et
artefacts en mémoire, rejette tout résidu C005/C006 puis parse réellement les
trois scripts. Armement à blanc : zéro erreur AST, 35 sessions avant live,
répertoire officiel absent, zéro completion, UI, fichier collecté, listener ou
Computer Use. Sortie SHA
`36C222264F314D5F17BAB14834530DED283F8C0B664BAB94E5BAE9CCC40BF339`.

Audit final A623 **188/188 PASS**, script SHA
`2C08AF76B39A945702981602EEC6CCD312289CA00129D6A9822EC3E88A56FAA9`,
sortie SHA
`A8FB4FEA7AF4107D590FFAC9F42F184E420E6DFE98ADC9676559C2EDD96D6C5E`.

Rapport : `phase5/A623-RAPPORT-PREFLIGHT-C007-ADELPHI.md`.

**A623 EST CLOS PASS.** A624 est autorisé à exécuter exactement **un** live
WinUI C007, sans retry : démarrage du runtime, nouvelle discussion, question
byte-identique, collecte complète, fermeture immédiate WinUI/Qwen/Computer Use.
A625 seul pourra prononcer les verdicts et envoyer Telegram. Produit et phase 5
restent `TESTE_NON_APPROUVE`; Goal actif.

### A624 — unique live WinUI C007 Adelphi

Le runtime figé a été démarré une seule fois sous PID `26288`, avec 35 sessions
persistées avant live et zéro completion antérieure. Le vrai binaire WinUI a
été ouvert dans une nouvelle discussion. La question de 147 caractères a été
saisie byte-identique puis soumise une seule fois. Un premier appel de clic a
été refusé par l'API de contrôle avant toute interaction en raison d'un
paramètre invalide ; l'état frais a confirmé la question non soumise, puis le
clic correct `Envoyer` a eu lieu exactement une fois. Aucun retry C007 et aucun
replay C001-C006.

Réponse terminale visible et persistée : `Adelphi University provides paid
sick leave for full-time, part-time, hourly, and student employees [E1].` Les
quatre catégories sont exactes, le writer termine, le vérificateur rend
`valid=true` et la revue sémantique finale Qwen accepte.

La source unique E1 est le chunk oracle
`65826cb4-47be-84b3-5541-2e7bfe11d34c`, page 11, docId
`85035756-a926-8374-70d5-4ba6de51e4ab`, révision
`b4e60d57-e3df-7789-cad8-0a7fe2676df1`, SHA canonique
`7bd296aec7594e515ac1d6cb4b20f80b01f2b7ae06e1324b38a9a4b477c4fbe8`.
Le hash catalogue 32 et le `docmeta` ne sont pas utilisés comme preuve finale.

La carte WinUI visible `Adelphi_Employee_Handbook_2026.pdf`, `p. 11`, a été
cliquée une seule fois. Edge a ouvert le bon PDF sur `#page=11`; le lecteur
affichait `11 of 12` et le passage `SICK DAYS` avec les quatre catégories.
Captures SHA : terminal
`0495ACBB9057BD57E9A7066DB755FD94909568FA8BBA7B36D2A8E2D349008218`,
PDF page 11
`F3196BECDBBE71EE4442618310A3A5BB1E98BB55DD2913F1B4874F42C24827DE`.

Trace `rag-20260830231812825-df691957` : 6/6 appels LLM, 7 753 prompt
+ 417 completion = 8 170 tokens, un `rag.search`, 10 preuves matérialisées,
ledger 12 lignes. Temps pipeline `137055 ms`. Les plafonds 12 000/240 000 et la
réserve 4 800/60 000 sont respectés, mais la cible performance 45 000 ms échoue.

Après les captures, seul l'onglet PDF créé a été fermé, SAAIA a été fermée, le
noyau Computer Use réinitialisé, le runtime arrêté et le port 1234 libéré.
Sessions après : 36, soit exactement une session nouvelle. Collecte complète :
session SHA `D73AAD6D65EAB69946E8BB039D6C218F20566F0ECFC3251A744084A27C84C7D1`,
payload sources SHA
`E67810CBC15DCD31AA2AA2E29809F83D2AA2BA0A3ADB16D219C68495FF0B64C6`.

**A624 EST CLOS SANS RETRY.** Aucun patch produit n'a été effectué après live.
A625 doit auditer séparément fonction, provenance, BUG-139, budget, UI,
performance, unicité et nettoyage, puis envoyer le Telegram obligatoire.

### A625 — audit du live C007 avant Telegram

L'audit final réconcilie protocole, préflight, session, mémoire, payload,
carte, trace, ledger, captures, binaires, Git et ressources. Résultat :
**243/243 PASS**. Script SHA
`E466D5DA16C02C5B42157C43F665709763680CFF97D4CEF1B989AC90A9399E79`,
sortie SHA
`645B4B427CE7E2CB82B7739EEA7187208358AE673695DC9F67500C10666259B5`.

Verdicts : fonction PASS ; provenance PASS ; BUG-139 revalidé live ; budget et
trace PASS ; UI ouvrable page 11 PASS ; unicité/nettoyage PASS ; performance
FAIL à 137 055 ms > 45 000 ms. Verdict officiel :
`PASS_FUNCTIONAL_C007_FAIL_PERFORMANCE_C007`. L'endpoint optionnel de tracking
répond 404 `tracking_not_configured`, limite d'observabilité déclarée, sans
perte de la session, du payload ou de la trace persistés.

Les premières passes de l'auditeur ont échoué uniquement sur des attentes du
script d'audit (marqueur start, date localisée, Markdown, noms/hashes binaires,
échappement regex). Elles n'ont modifié ni produit, ni live, ni données, ni
verdict ; seule la sortie 243/243 est promue.

Rapport : `phase5/A625-RAPPORT-FINAL-C007-ADELPHI.md`.

Telegram A625 envoyé exactement une fois du
`2026-08-31T01:35:18.3472733+02:00` au
`2026-08-31T01:35:18.7044256+02:00`, titre
`SAAIA RAG - A625 C007 fonctionnel PASS performance FAIL`. Confirmation :
`Notification Telegram envoyee. Corps=3197 caracteres. Total=3329 caracteres.`
Code 0, une partie, zéro retry. Message SHA
`17C868EE4DDF9FDE31BB2E90CAC11B39E2ADB5C13D6CD6413EF83C598ECA34DA` ;
rapport SHA
`F12D8A290BE39748427954E6114D3DD0FEF94E32927242218E770FA2D8B921EA`.

Reçu : `phase5/00-RECU-TELEGRAM-A625-C007-ADELPHI.md`. Le rapport 24 h signalé
manquant avait déjà été renvoyé séparément à 01:17 en neuf parties, un appel et
zéro retry.

**A625 ET C007 SONT CLOS SANS REPLAY.** BUG-139 est revalidé live ; les gates
fonction, provenance, budget, UI, unicité et nettoyage passent. La performance
reste FAIL. Le produit et la phase 5 restent `TESTE_NON_APPROUVE`; le Goal
reste actif. La prochaine dette est la latence des six appels, à analyser sans
hardcode Adelphi et sans reprendre la décision sémantique à Qwen. Tout nouveau
live utilisera un C008 distinct.

### A625 — scellement après Telegram

Le contrôle post-envoi authentifie le message, le rapport, les audits live et
pré-Telegram, le reçu A625, le rapport 24 h et son reçu de renvoi. Il confirme
également l'unicité des fichiers A625, l'absence de retry, le découpage du
rapport 24 h en neuf parties et l'arrêt de WinUI, de Qwen et du listener 1234.

Audit final après Telegram : **65/65 PASS**. Script : 130 lignes, SHA
`EB678C7E0557419268B4E2CC8CFC1F8EFA8E47A8B54B2F517822E2BD8063AC52`.
Sortie : 69 lignes, SHA
`6B2D24B9836D718188BB4C47325C9C8B703E0276FFCC5862FA2AAF7254E44B47`.
Reçu A625 : SHA
`0D1B76DAD0E80BCC140AFC54A02B5AA4E1AB3F55566C794BF5589A8B8CBA04ED`.

Verdict de transport scellé :
`ENVOI_CONFIRME_EXACTLY_ONCE_NO_RETRY`. Aucun nouvel envoi n'a été effectué
pendant cet audit. Le rapport 24 h et le rapport A625 sont confirmés reçus par
le notificateur ; aucun identifiant Telegram non exposé n'est inventé.

**A625 RESTE CLOS.** Le prochain travail est A626, attribution causale de la
latence sur la trace C007 existante, en lecture seule et sans Computer Use.

### A626 — attribution causale de la latence C007

L'analyse en lecture seule de la trace
`rag-20260830231812825-df691957` réconcilie les six appels Qwen, les temps
serveur, les buckets client, les tokens et les étapes non-LLM. L'extracteur
indépendant rend **79/79 PASS**.

Le pipeline de `137055 ms` contient `131588 ms` d'appels LLM, soit **96,01 %**,
contre `3329 ms` de retrieval, soit **2,43 %**. Le non-LLM complet vaut
`5467 ms`. Les six appels totalisent 7753 tokens de prompt et 417 tokens de
sortie ; le serveur passe `73309,27 ms` en évaluation de prompts et
`57452,79 ms` en génération.

Attribution par appel : classifieur `6895 ms`, route détaillée `23649 ms`,
réparation de référence nommée `9979 ms`, adéquation des preuves `40284 ms`,
writer `13180 ms`, revue sémantique terminale `37601 ms`. Les trois rôles
post-retrieval adéquation + writer + reviewer coûtent déjà `91105 ms`.

Atteindre 45 s impose de retirer `92055 ms`, donc environ **69,96 %** du temps
LLM. La génération seule et l'évaluation des prompts seule dépassent chacune
le budget LLM disponible de `39533 ms` : une compaction unilatérale ne suffit
pas. Les suppressions unitaires restent toutes au-dessus de la cible.

La seule soustraction sous 45 s (`42296 ms`) retire classifieur, réparation,
adéquation et reviewer. Elle est explicitement **CONTREFACTUELLE, NON MESURÉE ET
INTERDITE** : A483/A486 ont déjà montré deux faux `answer` dangereux mais
contractuellement valides, et A487 a retenu la séparation des responsabilités
sémantiques. Aucun raccourci ne doit réactiver la transaction monolithique V0,
supprimer le reviewer ou transférer la décision au code.

Les variantes historiques Q4_K_M, brouillon Qwen3-0.6B CPU, n-grammes,
`cache-reuse` et writer dans la conversation du juge restent rejetées par leurs
mesures de qualité ou de latence. Le retrieval n'est pas le goulot et ne doit
pas devenir la priorité par erreur. Il n'est proposé ni achat, ni matériel, ni
service distant.

L'expérience suivante retenue est
`PROPORTIONAL_WRITER_REVIEWER_SAFE_FALLBACK`, derrière un flag désactivé par
défaut et d'abord hors live : pour une route simple bornée décidée par Qwen, le
writer choisit les EvidenceIds et rédige ; un reviewer Qwen indépendant voit
le brouillon et toutes les preuves bornées. Seul `accept` permet la publication.
`revise`, `need_more_evidence`, erreur, timeout ou citation invalide replient
vers V6 ou interdisent la publication. Une correction du reviewer ne peut
jamais s'auto-approuver.

Gates A627 : préenregistrement, tests RED de sûreté et de repli, flag isolé,
suites complètes, comparaison appariée sur les 25 cas A486 et leurs deux
contre-exemples dangereux, microbenchmark par rôle, puis seulement un C008 neuf.
Rejet immédiat au premier faux `answer` dangereux, à toute décision sémantique
codée, à tout hardcode, si la qualité stricte baisse ou si le gain médian
post-retrieval est inférieur à 25 %. Même la soustraction de l'adéquation
laisserait `96771 ms` avant redistribution : 45 s reste une cible de Phase 7 et
non une autorisation à réduire la sûreté de Phase 5.

Artefacts :

- `phase5/PROTOCOLE-A626-ATTRIBUTION-LATENCE-C007.md`, 131 lignes, SHA
  `56016969D96C56D75919C61B8D080C0DAC02C093122F0503A89777DD3E6C0484` ;
- `phase5/a626-extract-latency-c007.ps1`, 250 lignes, SHA
  `8D040E4A559D9A3922A6ECD78470C4FDCF20594334D43C0F3061053FBED8DBA8` ;
- `phase5/A626-SORTIE-EXTRACTION-LATENCE-C007.txt`, 109 lignes, SHA
  `42F8D19C11767B3352457D8FC328E5BD6EDC7C2F95DE800D2090DD3C96D413CE` ;
- `phase5/A626-RAPPORT-ATTRIBUTION-CAUSALE-LATENCE-C007.md`, 274 lignes, SHA
  `07A5BD2573FDD9E31B9843E88339ACED9B6E27383280858678C60C9093D38CA0` ;
- `phase5/a626-audit-final-latency-c007.ps1`, 149 lignes, SHA
  `99304B6A93319821A69417F7BA59D9187A089C63737E30C9AEDC320962C82A22`.

**A626 : DIAGNOSTIC CAUSAL PASS.** Performance C007 : **FAIL**. Aucun patch
produit, aucun replay, aucun C008, aucun Qwen, aucune WinUI et aucun Computer Use
pendant ce lot. Qwen/SAAIA restent arrêtés et le port 1234 est libre. Produit et
phase 5 : `TESTE_NON_APPROUVE`. Goal actif. Cette décision architecturale
majeure déclenche un rapport Telegram après audit final, sans doublon A625.

### A626 — audit final avant Telegram

L'auditeur final authentifie les deux logs C007, le protocole, l'extracteur, la
sortie 79/79, l'ADR, les rapports historiques A435/A483/A486/A487, l'analyse de
latence, le rapport A626, les calculs, les contrefactuels, le plan dynamique,
Git et les ressources. Résultat : **107/107 PASS**.

Script : 149 lignes, SHA
`99304B6A93319821A69417F7BA59D9187A089C63737E30C9AEDC320962C82A22`.
Sortie : 118 lignes, SHA
`46218122A2BB9554110B15AFBE5DF7C9B0194669C2F1BC7DDCF9B4C8525CF065`.
Rapport : 274 lignes, 14 662 octets, SHA
`07A5BD2573FDD9E31B9843E88339ACED9B6E27383280858678C60C9093D38CA0`.

Les premiers lancements de l'auditeur ont échoué uniquement sur le calcul du
chemin racine et sur deux assertions textuelles traversant un retour de ligne.
Ils n'ont modifié ni produit, ni C007, ni Qwen, ni WinUI, ni verdict. Le seul
artefact promu est la sortie finale **107/107 PASS**.

Verdict scellé :
`DIAGNOSTIC_PASS_PERFORMANCE_FAIL_NO_SAFE_SHORTCUT_TO_45S`. Le palier est une
décision architecturale majeure et exige donc un unique Telegram A626. Aucun
message A625 ou rapport 24 h ne doit être renvoyé.

### A626 — Telegram envoyé et reçu

L'audit pré-envoi rend **46/46 PASS** et autorise exactement un appel, sans
retry, en une partie. Script SHA
`A44C604D9F2FBC499815E1722C80551DDF9FB92133ACDF612046895B661F4F86` ;
sortie SHA
`4C68BED0DB4AB834E1900E308B9A4A45024508E8251F1571A48C8C458F2162DF`.

Telegram A626 envoyé exactement une fois du
`2026-08-31T01:59:37.0230224+02:00` au
`2026-08-31T01:59:37.3740712+02:00`, titre
`SAAIA RAG - A626 diagnostic causal latence C007`. Sortie exacte :
`Notification Telegram envoyee. Corps=3456 caracteres. Total=3580 caracteres.`
Code 0, une partie, zéro retry.

Message : 3 456 caractères, SHA
`833C13CE3678E5C10EAF600976E3B32926F51118E46B07497908C08F0EC136DA`.
Reçu : `phase5/00-RECU-TELEGRAM-A626-ATTRIBUTION-LATENCE-C007.md`, 34 lignes,
SHA `E979CED8892E0ED83C27D49BF6C37ED8AEE86A2554E0AFEB1214936BBAA17871`.

Le notificateur confirme le texte UTF-8, mais n'expose aucun identifiant de
message ; aucun identifiant n'est inventé. Aucun A625 et aucun rapport 24 h n'a
été renvoyé pendant A626.

### A626 — scellement après Telegram

L'audit post-envoi authentifie message, rapport, reçu, audit 107/107, audit
pré-envoi, plan, branche/HEAD et ressources. Résultat : **35/35 PASS**.

Script : 69 lignes, SHA
`E7304F502E4D28FDE18625F3C3D810DF56E3DC7D130D2DF60BF08A09630C22DD`.
Sortie : 42 lignes, SHA
`BB842E74FE8CB3C49A500D3C33F582454525756C265EA6CAAF7795BB5298567B`.

Verdict de transport : `ENVOI_CONFIRME_EXACTLY_ONCE_NO_RETRY`.

**A626 EST CLOS.** C007 reste clos sans replay. Le produit et la phase 5 restent
`TESTE_NON_APPROUVE`, le Goal reste actif et la performance reste FAIL. A627
doit préenregistrer l'expérience
`PROPORTIONAL_WRITER_REVIEWER_SAFE_FALLBACK`, sans lancer Qwen, WinUI ou
Computer Use avant ses gates déterministes.

### A627 — préenregistrement du chemin proportionnel writer → reviewer

Le protocole A627–A631 transforme la décision A626 en expérience falsifiable
`PROPORTIONAL_WRITER_REVIEWER_SAFE_FALLBACK`. État :
`PREENREGISTREMENT_PASS_IMPLEMENTATION_NON_AUTORISEE` sous réserve de l'audit
final A627.

Le chemin est désactivé par défaut et mutuellement exclusif avec les deux flags
expérimentaux antérieurs. Il n'est éligible que lorsque les décisions Qwen et
l'état mécanique établissent : `content_claim`, sélection requise supérieure à
1, aucun layout, référence de type document, identité canonique résolue, pool
complet et homogène de 1 à 12 preuves citables, aucune troncature, aucune
tentative ou révision antérieure, budget terminal et client structuré
disponibles.

Le writer Qwen reçoit toutes les preuves autorisées et choisit les EvidenceIds
de ses claims. Le vérificateur mécanique bloque toute sortie invalide. Le
reviewer Qwen indépendant voit le brouillon et tout le pool. Seul `accept` avec
contrat valide permet de publier le brouillon byte-identique du writer.
`revise`, `need_more_evidence`, protocole invalide, timeout, annulation ou budget
interdisent la publication rapide et replient vers V6 si le repli reste sûr.
Le texte du reviewer n'est jamais publié et aucun skip de revue n'est autorisé.

A628 devra commencer par 25 tests RED : flag/default, incompatibilités,
éligibilité et non-éligibilité, pool complet, tentative unique, writer,
vérification, accept/revise/need-more, erreurs/budgets, anti-auto-approbation,
anti-skip, repli V6, portée mondiale, négation, provenance, annulation, traces
et parité flag off. L'implémentation restera dans un partial dédié ; le runner à
2500 lignes ne recevra aucun nouveau bloc substantiel.

A630 est préenregistré comme comparaison Qwen appariée hors WinUI : baseline V6
et variante sur les mêmes 25 cas normalisés, ordre A/B alterné. Les cinq cas
A483, dont les deux faux dangereux historiques, passent en premier avec arrêt
immédiat au premier faux dangereux. Gates cumulatives : zéro faux dangereux,
aucune publication sans accept indépendant, strict et validité protocolaire non
inférieurs au témoin, provenance intacte, exactement deux appels post-retrieval
sur fast accept et gain médian post-retrieval d'au moins 25 %. Les replis sont
rapportés séparément.

C007 reste interdit au replay. Un live WinUI ne pourra être envisagé qu'après
A631 positif et sur un C008 neuf. A627 n'envoie pas de Telegram : A626 a déjà
notifié cette décision et le présent lot ne la modifie pas.

Protocole :
`phase5/PROTOCOLE-A627-A631-CHEMIN-PROPORTIONNEL-WRITER-REVIEWER.md`, 451
lignes, 20 379 octets, SHA
`BAF5D710996F27049A07F3D3DEBC92FAD35D0A36BF818771E4C8B1400D419DD5`.
Auditeur préenregistré :
`phase5/a627-audit-preregistration.ps1`, 123 lignes, SHA
`02FAAF6113D20E1A6CD782C8AE0AF89603F942BA94B01B71A0CB531DE78245A8`.

Pendant A627 : aucun patch produit, aucun test, aucun Qwen, aucune WinUI, aucun
live, aucun listener 1234 et aucun Computer Use. Produit et phase 5 restent
`TESTE_NON_APPROUVE`, Goal actif.

### A627 — audit et clôture du préenregistrement

L'auditeur authentifie protocole, ADR, A486/A487/A626, dix coutures produit,
deux suites de tests existantes, 32 marqueurs de contrat, les 25 tests RED, la
comparaison appariée, les seuils, le plan, Git et les ressources. Résultat :
**114/114 PASS**.

Audit : `phase5/A627-SORTIE-AUDIT-PREENREGISTREMENT.txt`, 123 lignes, SHA
`F677EC5CB96A461C51F0087FC3A14D68E086A87442B62F0DDDEFED7E0438945C`.
Rapport : `phase5/A627-RAPPORT-PREENREGISTREMENT-CHEMIN-PROPORTIONNEL.md`,
112 lignes, SHA
`817243C0B41273C3760F7413017AB8D936FE59160CE2F1D4712BA0A3D848CE60`.

Le premier lancement de l'auditeur a échoué seulement parce qu'une assertion
était sensible à la casse ; le deuxième sur un marqueur traversant un retour de
ligne. L'auditeur a été normalisé, sans modification du produit, des tests ou du
protocole, et seule la sortie finale 114/114 est promue.

**A627 EST CLOS PASS.** Verdict :
`PREENREGISTREMENT_PASS_IMPLEMENTATION_NON_AUTORISEE`. A628 peut maintenant
écrire uniquement les tests RED préenregistrés. Aucun Telegram A627 n'est dû.
Produit/phase 5 `TESTE_NON_APPROUVE`, Goal actif, ressources arrêtées.

### A630 — campagne Qwen appariée officielle

L'unique campagne autorisée a exécuté les **50/50 bras** du manifeste, sans
replay, du `2026-08-31T17:57:26+02:00` au
`2026-08-31T19:12:46+02:00`. Durée TRX : `01:15:18.1890477`, test **1/1
PASS**, campagne non interrompue, zéro erreur transport/harnais, zéro faux
`answer` dangereux et zéro publication sans reviewer `accept`.

Résultats :

- BASELINE_V6 : strict 11/25, protocole 20/25, provenance 20/25, 109 appels,
  médiane 67 563 ms ;
- PROPORTIONAL : strict 9/25, protocole 17/25, provenance 17/25, 112 appels,
  médiane 65 767 ms ;
- six fast accepts : 6/6 stricts, 6/6 protocoles, exactement deux appels,
  médiane 37 849,5 ms contre 60 637 ms, gain 37,58 % ;
- neuf replis : seulement 1/9 strict et 1/9 protocole, médiane 155 776 ms.

Artefact campagne SHA
`1E942ED6F616D235550312313F2A87FA398A629580234AFD8D9FB790558874A8` ;
appels JSONL SHA
`58439C3DB56BDE09FCB34EABF8FBB64B4C2932A06387C5EB5AA0426F5BACAAE3` ;
TRX SHA
`CE58173A5548BE6BCA75D9C7B958E8D85C92BE8B47BEA1FB4C30E137EFBE81D5`.

Le listener 1234 s'est fermé avant la fin du processus géré ; le lanceur strict
a signalé cette courte course de terminaison. Le contrôle immédiat suivant a
confirmé zéro Qwen et zéro port 1234, sans arrêt forcé et sans toucher à Ollama.

### A631 — audit aveugle, rejet et retrait propre

Les 30 runs non stricts ont été revus dans un paquet aveugle sans bras, chemin,
position, latence ni nombre d'appels. Les jugements ont été hashés avant la
levée : 30/30 oracles maintenus, zéro oracle modifié et zéro faux dangereux.
Jugements SHA
`7B338C2C0AC0D770E131408DBBD013040B20911FE4C16D3EB562E380DF1D4DB0`.

Les gates cumulatives rejettent la variante : strict 9 < 11, protocole 17 < 20
et provenance 17 < 20. Deux cas stricts baseline sont perdus et aucun cas n'est
gagné. Les fast accepts sont prometteurs isolément, mais une tentative rapide
suivie de V6 ajoute du coût, modifie l'état de repli et dégrade le résultat.

Verdict :
`REJECTED_EXPERIMENT_REMOVED_BASELINE_RESTORED_C008_NOT_AUTHORIZED`.
Le flag, les trois composants produit, les coutures runner et les deux fichiers
de tests spécifiques ont été retirés. Zéro référence source expérimentale
subsiste ; runner 2 479/2 500 lignes.

Deux incidents de retrait ont été conservés : deux callbacks résiduels détectés
à la compilation, puis une suite **1 736/1 738** montrant que l'ancien arrêt
baseline sur protocole writer invalide devait être restauré explicitement. Le
garde-fou générique terminal a été rétabli ; ciblés **2/2**, client final
**1 738/1 738**, serveur **2 054/2 054**, zéro skip.

Rapport : `phase5/A631-RAPPORT-DECISION-RETRAIT-PROPORTIONNEL.md`.

**A631 EST CLOS REJECTED.** C008 n'est pas autorisé. Le Goal reste actif. La
phase suivante doit comparer des architectures end-to-end qui évitent la
composition tentative rapide + repli V6, sans empiler une nouvelle prépasse.

### A631 — Telegram de décision envoyé

Le verdict A631 a été envoyé exactement une fois le `2026-08-31` de
`19:48:02.0163535` à `19:48:02.4659842` +02:00, titre
`SAAIA RAG - A631 chemin proportionnel rejete`. Sortie exacte :
`Notification Telegram envoyee. Corps=3406 caracteres. Total=3527 caracteres.`
Code 0, une partie, zéro retry.

Message SHA
`2E8741AC13AC944401436A1DA3E450A213B1105A5E4A5F24BDE1C49DFC78B888` ;
reçu `phase5/00-RECU-TELEGRAM-A631-DECISION-RETRAIT.md`. Verdict transport :
`ENVOI_CONFIRME_EXACTLY_ONCE_NO_RETRY`.

### A632 — recul architectural après A631

L'analyse forensique des 15 premiers couples writer/reviewer A630 montre six
`accept` stricts et neuf `revise`. Dans les neuf rejets, le reviewer décrit déjà
la bonne classe de défaut : cinq claims comparatifs insuffisamment étayés par
leurs EvidenceIds, une prétention mondiale exigeant `research` et trois choix
utilisateur exigeant `clarify`. Le contrat générique `revise` perd cette
information et déclenche le repli V6 destructeur.

Les architectures V0, V2/V6, fast+fallback, heuristique code et simple modèle
plus grand ont été comparées contre les historiques live. La candidate retenue
est `TWO_CALL_OUTCOME_PROPOSER_ADJUDICATOR_V1` : un proposer choisit et rédige
l'une des quatre issues, puis un adjudicateur indépendant accepte la réponse ou
rend une issue non publiable. Il ne peut jamais rédiger une réponse de
remplacement et aucun fallback V6 n'existe.

Rapport A632 : 260 lignes, SHA
`62A9D2098C0495726B09D56E1F899BB5FC5C903A00A7FE9FB5E370AA99C9BA69`.
Verdict : `SELECT_TWO_CALL_OUTCOME_PROPOSER_ADJUDICATOR_NO_FALLBACK`. Aucun
code produit, Qwen, WinUI ou Computer Use à A632.

### A633 — protocole Outcome Proposer + Independent Adjudicator

Le protocole est figé avant RED et avant tout appel Qwen. Contrats discriminés
par noms d'outils, deux appels fixes, snapshot EvidenceBundle identique,
publication uniquement sur `accept_answer_proposal`, actions non-answer
finalisées par Qwen, sortie invalide fail-closed, zéro réparation/repli/V6.

Gates live cumulatives : 0 faux dangereux, 25/25 protocoles candidats, strict
≥ baseline +3 et ≥14/25, answers ≥9/13, non-answers ≥10/12, provenance ≥
baseline et ≥20/25, médiane ≤45 s, p95 ≤90 s, maximum ≤180 s, exactement 50
appels candidats et audit humain aveugle. Tout FAIL impose le retrait.

Protocole A633–A637 : 434 lignes, SHA
`5420666E6C53A2D05A7FAF1629B327C98047C33731DF810238F6DE91E79BFBCB`.
A634 RED était interdit avant audit A633 vert. Goal actif, C008 interdit.

### A633 — scellement de préenregistrement

Le premier passage de l'audit a produit **67/71 PASS** : les quatre échecs
étaient des faux négatifs de recherche littérale sur des clauses pourtant
présentes dans le protocole (snapshot commun, second appel après proposition
invalide, impossibilité pour l'adjudicateur d'inventer une réponse, absence de
troisième appel). Cette sortie est conservée sans être promue en PASS, SHA
`DAC363AC7352C0EDE9B29A9669B89B2431086D95FCCEA568EB79F85BC318EF89`.

Seules les quatre aiguilles du script d'audit ont été alignées sur le texte
figé ; ni le protocole, ni le rapport A632, ni le code produit n'ont changé.
Script final SHA
`EF2ACFE7088AA52F693B697D008661615B19050EB75E955603FA8EB6AEC66AC0`.
Le second passage donne **71/71 PASS**, zéro échec, verdict
`PASS_A633_PREREGISTRATION_A634_RED_AUTHORIZED`, sortie SHA
`FF96AFC02EB3E0B11EECB18E4C863BD1A883E96A9C760BCA9B4E0C7FD97038E2`.

Les contrôles finaux confirment runner sous limite, `git diff --check` code 0,
branche/HEAD attendus, absence du code candidat avant RED et zéro Qwen, port
1234, testhost, VBCSCompiler, WinUI ou Computer Use. **A633 EST CLOS PASS.**
A634 RED est maintenant autorisé ; A635, A636, A637, C008 et tout live restent
interdits. Produit/phase 5 `TESTE_NON_APPROUVE`.

### A634 RED — absence du protocole candidat prouvée

Un fichier neuf contient exactement 15 `[Fact]`, un pour chacune des quinze
obligations TDD figées à A633 : flag, incompatibilités V0/V2, quatre outils
proposer, huit outils adjudicateur, publication asymétrique, absence d'auteur
answer côté adjudicateur, second appel sûr après proposition invalide,
non-answer sans citations, terminal sans V6, signature tentée une fois,
snapshot identique, claims multi-preuves, writer invalide terminal, annulation
et ordre baseline flag off.

Test :
`client/SAAIA.Client.ToolAgent.Tests/SourceBackedOutcomeProposerAdjudicatorRedTests.cs`,
378 lignes, SHA
`9D177719354388EBA95883B7F8912214632F187DBA582FA2516DE4B41F8BD824`.

La compilation RED donne le code 1 avec exactement deux erreurs CS0246,
limitées aux types volontairement absents
`SourceBackedOutcomeProposerAdjudicator` et
`OutcomeProposerAdjudicatorExecution`. Le projet produit compile avant l'échec
du projet de tests. Sortie 6 lignes, SHA
`999952297ECBF4DD15A7490C67BDA013865BBD21E2A40F8733978CA93E3D6A12`.

Les huit fichiers produit de référence sont byte-identiques aux hashes A633 ;
le runner reste à 2 479 lignes et aucun fichier produit Outcome/Adjudicator
n'existe encore. Audit RED : **52/52 PASS**. Script SHA
`7ECA9884BB2A5B8A8196A7E677144F3C07D91224BE65C124CB23CCC969434C9B` ;
sortie normalisée SHA
`1F80349EE070083796E89AB4B39D7ABA0E34B76ED97277F21E3C7F25A6672B27`.

Verdict :
`PASS_EXPECTED_FAILURE_FEATURE_ABSENT_PRODUCT_UNCHANGED_A634_GREEN_AUTHORIZED`.
Le GREEN minimal derrière flag faux par défaut est maintenant autorisé. A635,
A636, A637, C008 et tout live restent interdits. Ollama utilisateur reste
intact ; notre Qwen géré, le port 1234, WinUI et Computer Use sont arrêtés.

### A634 GREEN — deux opinions fixes sans fallback

Le candidat `TWO_CALL_OUTCOME_PROPOSER_ADJUDICATOR_V1` est implémenté derrière
`OutcomeProposerAdjudicatorEnabled=false` et la variable
`SAAIA_SOURCE_BACKED_AGENT_V2_OUTCOME_PROPOSER_ADJUDICATOR`. Le constructeur
refuse toute coexistence avec V0 ou V2 avant appel.

Le snapshot canonique est sérialisé et hashé une fois, puis remis identique aux
deux appels. Les portes refusent mécaniquement pool vide ou >12 sans troncature,
identité/page/extrait incomplet, EvidenceId dupliqué, document nommé non résolu,
signature déjà tentée et réserve insuffisante. La mémoire longue est exclue.

Le proposer expose quatre outils discriminés. L'adjudicateur en expose jusqu'à
huit, ne peut ni rédiger ni remplacer un answer et ne reçoit
`accept_answer_proposal` que si le draft du proposer passe le
`SourceContractVerifier`. Une proposition invalide reçoit tout de même le second
appel sûr. Toute issue non-answer ou bloquée est terminale, sans answer,
CitedEvidence, réparation, troisième appel, replay ou fallback V6.

Composants produit : 265, 488, 262 et 167 lignes ; runner 2 488/2 500. Aucun
hardcode de domaine. Le fichier RED original reste byte-identique. Dix tests
d'intégration supplémentaires couvrent le runner, les portes, anglais/français
et plusieurs domaines. Matrice finale : **25/25 PASS**, zéro skip, TRX SHA
`0B66CBA824A0653DBC3B111D34CF8D89AAAD7EB96FEECF56D4BD6717A8C4F0A0`.

Audit GREEN : **77/77 PASS**. Script SHA
`C8D3C360D0A7446565B24D96EBC052648FFF44966F38BA51BEB5EDB00DAE2232` ;
sortie SHA
`C17C21F2723F22843075FC14FA9640CA3256AB09A4107D96DB994656E2C816B2`.
Rapport A634 : 279 lignes, SHA
`2B0B9ADD68E514BF1A77358FE00CC9B25505987985BC948D19007FAC5C6EAA21`.

**A634 EST CLOS PASS ciblé.** Verdict :
`PASS_A634_GREEN_TARGETED_FLAG_OFF_BY_DEFAULT_A635_REGRESSIONS_REQUIRED`.
Le candidat reste `TESTE_NON_APPROUVE`, flag off. A635 est maintenant autorisé ;
A636, A637, C008 et tout live restent interdits. Aucun Telegram A634 : le
protocole réserve l'envoi à une impossibilité A634, un danger A636 ou la
décision A637. Qwen géré, port 1234, WinUI et Computer Use sont arrêtés.

### A635 — régressions déterministes complètes

Les dix étapes préenregistrées ont été exécutées dans l'ordre sur le même
binaire candidat, flag off par défaut :

- candidat A634 : **25/25** ;
- transaction V0 historique : **33/33** ;
- V6/resolution/writer/reviewer : **80/80** ;
- writer structuré + Source Contract Verifier : **24/24** ;
- BUG-139/document nommé : **57/57** ;
- provenance/EvidenceBundle/UI : **57/57** ;
- client complet : **1 763/1 763** ;
- serveur complet : **2 054/2 054**.

Tous les TRX ont zéro échec et zéro skip. Le total client est exactement le
baseline A631 1 738 + les 25 tests A634 ; le serveur inchangé reste à 2 054.
Le runner est à 2 488/2 500, aucun fichier SourceBackedRag ne dépasse sa limite,
trailing whitespace 0, hardcode 0, sites d'appel candidat 2, fallback V6 0 et
`git diff --check` code 0 avec les 13 avertissements historiques.

Audit final : **78/78 PASS**. Script SHA
`2AB4B32C49574306E3DC10AEEB6BD4EF0C9A00BA25B3B5422684D7CD06727A85` ;
sortie SHA
`E0A9FD3FF2BE2EE45864BDD9D823032ED792CC6F7F9D237FCF251FCAE4B5763D`.
Rapport A635 : 148 lignes, SHA
`8F32DB5A55C4997B63186A64ACB3B5E18A4EA5A8AB6D7C1CDCC1866992C4BF58`.

**A635 EST CLOS PASS.** Verdict :
`PASS_A635_DETERMINISTIC_REGRESSIONS_A636_QWEN_PAIRED_AUTHORIZED`. A636 Qwen
apparié sans WinUI est maintenant autorisé selon A633 ; A637, C008 et WinUI
restent interdits. Build servers, Qwen géré, port 1234, testhost, VBCSCompiler,
WinUI et Computer Use sont arrêtés. Aucun Telegram A635 conformément au palier
préenregistré.

Scellement A627 : **18/18 PASS**. Script 40 lignes, SHA
`8BA984CE3056E698B94120BD7CBC2299DD79A88A3E6E9E7CADB9DB141D673C18` ;
sortie 22 lignes, SHA
`B298EA3828307F8FB96F684FC407E48A4CE94155A3455ADD99B2C024227D436D`.
État final : `CLOSED_RED_TESTS_AUTHORIZED`.

### A628 RED — absence de la fonction prouvée avant implémentation

Un fichier de test neuf contient exactement 25 `[Fact]`, un par scénario
préenregistré de politique d'éligibilité et de publication. Aucun produit n'a
été modifié avant la preuve RED.

Test :
`client/SAAIA.Client.ToolAgent.Tests/SourceBackedAgentProportionalWriterReviewerRedTests.cs`,
240 lignes, SHA
`5F553287F1E57638E63291B989E1FE456C7D7E0C0A0AA76C2C34FE24CCF72BA5`.

Compilation RED : code 1, zéro warning, quatre erreurs uniques CS0246, toutes
limitées aux deux contrats volontairement absents
`ProportionalWriterReviewerEligibilityInput` et
`ProportionalWriterReviewerPublicationInput`. Aucun fichier produit n'apparaît
dans les erreurs. Sortie 16 lignes, SHA
`7D0E38B831333B92C2B249AED33212FD275D5059676A55EE452CC500F02AB17A`.

L'audit RED authentifie les 25 tests, les deux contrats, les incompatibilités de
flags, le pool, la tentative unique, les trois issues reviewer, l'absence de
hardcode et huit hashes produit inchangés. Résultat : **40/40 PASS**. Script 72
lignes, SHA
`16A1A2D5FC80006AA290F7B09A8F85C87557602AEEDC9F99EA029938A26C0B23` ;
sortie 47 lignes, SHA
`2A65A4A3753E14E599A925442E06E0F6D0F1972E53C9B88E70761DC2D2B03E2E`.

Verdict : `PASS_EXPECTED_FAILURE_FEATURE_ABSENT_PRODUCT_UNCHANGED`. Les serveurs
de build sont arrêtés ; Qwen/SAAIA/port 1234/Computer Use restent coupés.
L'implémentation GREEN minimale derrière flag off est maintenant autorisée.

### A628 GREEN — chemin proportionnel implémenté et ciblé

Le chemin `PROPORTIONAL_WRITER_REVIEWER_SAFE_FALLBACK` est implémenté derrière
`ProportionalWriterReviewerEnabled=false` et la variable
`SAAIA_SOURCE_BACKED_AGENT_V2_PROPORTIONAL_WRITER_REVIEWER`. Toute coexistence
avec V0 ou V2 est rejetée avant appel.

La politique mécanique applique les douze portes A627 : `content_claim`,
nombre >1, aucun layout, référence Qwen `document`, identité canonique résolue,
pool homogène, citable et rendable, complet de 1 à 12 sans troncature, aucune
révision/gap/tentative, budget et writer structuré. Aucun domaine ou document
n'est reconnu par lexique produit.

L'automate dédié donne tout le pool au writer structuré, conserve le writer
comme auteur exclusif du brouillon, applique le vérificateur, puis impose un
reviewer indépendant voyant toutes les preuves. Seul `accept` strict avec
listes vides, brouillon et bundle inchangés publie. Toute autre issue replie
vers V6 ou bloque ; une signature n'est jamais retentée et le skip de reviewer
reste interdit.

Preuves : le premier test d'intégration a d'abord échoué sur l'ancien appel
d'adéquation, puis la matrice finale rend **37/37 PASS**. TRX SHA
`373CA560BAF32DEF99CBC5C142485FD499E6720ADC4606D221066566F937D773`.
Elle couvre fast accept deux appels, toutes les portes, writer invalide, gate
mécanique, `revise`, `need_more_evidence`, listes non vides, reviewer tronqué,
repli V6, pool 13 non tronqué, flag off, annulation et provenance canonique.

Audit GREEN : **37/37 PASS**, sortie SHA
`DC1FD8BAE17F97CC9EEA4E801F5E845F4C7C1C589F091969AC9A7A3647B7D352`.
Rapport :
`phase5/A628-RAPPORT-GREEN-CHEMIN-PROPORTIONNEL.md`.

**A628 EST CLOS PASS ciblé.** Verdict :
`PASS_GREEN_TARGETED_FLAG_OFF_BY_DEFAULT_A629_REGRESSIONS_REQUIRED`. A629 peut
lancer les régressions déterministes. A630, A631 et tout live restent interdits.
Produit/phase 5 `TESTE_NON_APPROUVE`, Goal actif. Aucun Qwen, WinUI, listener
1234 ou Computer Use n'est actif.

### A628 — Telegram de palier envoyé

Le palier GREEN ciblé est suffisamment important pour notification. Telegram
envoyé exactement une fois le `2026-08-31` de `02:41:48.0686137+02:00` à
`02:41:48.4683641+02:00`, titre
`SAAIA RAG - A628 GREEN chemin proportionnel`. Sortie exacte :
`Notification Telegram envoyee. Corps=2899 caracteres. Total=3019 caracteres.`
Code 0, une partie, zéro retry.

Message SHA
`E248AB3BC2F00615597B0515B295F35378EC5BE1CE00968E7AA05496CAC7DD09` ;
reçu `phase5/00-RECU-TELEGRAM-A628-GREEN-CHEMIN-PROPORTIONNEL.md`, SHA
`4D56D3EB62FFA7465859A0CC9E45AE0B1F938B474E50847732739F807E77AB92`.
Le notificateur confirme le texte UTF-8 mais n'expose aucun identifiant de
message ; aucun identifiant n'est inventé. Verdict :
`ENVOI_CONFIRME_EXACTLY_ONCE_NO_RETRY`.

### A629 — régressions déterministes complètes

L'ordre A627 a été respecté. La première famille provenance/UI a détecté une
vraie régression structurelle : **56/57**, seul échec
`Canonical_source_backed_files_stay_modular`, runner à 2 724 lignes pour une
limite de 2 500. Le test n'a pas été contourné. L'automate a été extrait en
partials dédiés ; tailles finales : runner 2 491, automate 433, transitions 244,
support 178, aucun fichier SourceBackedRag hors limite.

Toutes les familles ont ensuite été rejouées sur le nouveau binaire :

- proportionnel : **37/37** ;
- writer structuré : **8/8** ;
- résolution/writer/reviewer : **55/55** ;
- BUG-139/document nommé : **57/57** ;
- provenance/EvidenceBundle/UI : **57/57** ;
- client complet : **1 775/1 775** ;
- serveur complet : **2 054/2 054**.

Tous les TRX finaux ont 0 échec et 0 skip. L'incident 56/57 reste conservé et
authentifié, jamais promu comme PASS. `git diff --check` code 0 ; zéro espace
final et zéro hardcode de domaine dans les nouveaux composants ; quatre DLL
hashées. Audit A629 : **69/69 PASS**, sortie SHA
`294CFE5D953AE34C8AF64C24C8100146831C1B3A2C6A60201BBA0F9E280FA569`.

Rapport : `phase5/A629-RAPPORT-REGRESSIONS-DETERMINISTES.md`.

**A629 EST CLOS PASS.** Verdict :
`PASS_DETERMINISTIC_REGRESSIONS_A630_AUTHORIZED_LIVE_STILL_FORBIDDEN`. A630 est
autorisé selon le protocole apparié ; A631, C008 et tout live restent interdits.
Produit/phase 5 `TESTE_NON_APPROUVE`, Goal actif. Build servers, Qwen, WinUI,
port 1234 et Computer Use sont arrêtés.

### A629 — Telegram de palier envoyé

Le rapport A629 a été envoyé exactement une fois le `2026-08-31` de
`02:57:23` à `02:57:24` +02:00, titre
`SAAIA RAG - A629 regressions deterministes`. Sortie :
`Notification Telegram envoyee. Corps=2768 caracteres. Total=2892 caracteres.`
Code 0, une partie, zéro retry. Reçu :
`phase5/00-RECU-TELEGRAM-A629-REGRESSIONS-DETERMINISTES.md`, SHA
`325294202362775C756A8FD7ED8AB822A1967D39A569CC9D0F8D331D23BC4EC1`.

### Rapport correctif complet des dernières 24 heures — envoi demandé

À la demande explicite de l'utilisateur, le travail A630 a été suspendu le
temps de corriger la transmission du rapport complet. L'ancien rapport envoyé
à 23:04 s'arrêtait au checkpoint A608 et ne couvrait donc pas A609–A629.

Un nouveau rapport couvrant exactement `2026-08-30 17:02` →
`2026-08-31 17:02` Europe/Zurich a été créé :
`RAPPORT-2026-08-31-24-DERNIERES-HEURES-RAG-CORRECTIF-ULTRA-COMPLET.md`,
521 lignes, 24 106 octets, 23 317 caractères, SHA
`5A2084CEBBE5AEEB1B5044920BC6C5020709D7046D28B69057C6CF9A36026A76`.

Il retrace A571–A629 : C002–C007, BUG-135–BUG-139, tous les RED/GREEN,
régressions, incidents non promus, le PASS fonction/provenance/UI de C007, son
FAIL performance, l'attribution 96,01 % LLM, le chemin proportionnel A627–A629,
les limites d'approbation et le plan A630/A631.

Audit pré-envoi : fichier/notificateur/configuration présents, 25 sections,
zéro mention obligatoire absente, zéro marqueur de secret, zéro caractère de
remplacement et aucun reçu antérieur du même identifiant.

Telegram envoyé exactement une fois de
`2026-08-31T17:11:05.5982907+02:00` à
`2026-08-31T17:11:07.4960680+02:00`, titre ASCII
`SAAIA RAG - rapport complet 24h actualise au 31 aout 17h02`. Sortie exacte :
`Notification Telegram envoyee en 8 parties. Corps=23317 caracteres.` Code 0,
zéro retry, aucun second envoi.

Reçu :
`phase5/00-RECU-TELEGRAM-RAPPORT-24H-20260831-CORRECTIF-COMPLET.md`.
A630 reste le prochain palier actif ; aucun Qwen, WinUI, live ou Computer Use
n'a été lancé pour produire ou transmettre ce rapport.

### A630 — préflight du comparatif Qwen apparié

Le préflight a découvert avant modèle une contradiction dans A627 : les cinq
cas A483 ne contiennent qu'un des deux faux `answer` dangereux A486. Un erratum
préenregistré conserve A483 positions 1–5, place le second danger
`insufficient_sensor_battery_autonomy` en position 6 puis exécute les 19 cas
restants. Toute fausse publication dangereuse arrête immédiatement la campagne.

Le manifeste normalisé contient 25 cas, 50 bras alternés, 13 `answer`,
6 `research`, 3 `clarify`, 3 `context`, 15 cas multi-unités et 10 mono-unités.
Questions, extraits et oracles historiques sont conservés ; une identité
documentaire synthétique canonique identique est ajoutée aux deux bras pour
exercer le pipeline courant. Manifeste 2 425 lignes, SHA
`D1CCCA29AC3704B3058D934202379BADBAFEA80CB7865D084520E45CBDC3B7F6`.

Le nouveau harnais collecte décision, strict, protocole, EvidenceIds,
provenance, appels par rôle, cache, tokens, temps serveur/client, fast accepts et
replis. Il exige le hash du manifeste avant tout appel et ne change que le flag
proportionnel entre les bras.

Tests chemin proportionnel + harnais : **41/41 PASS**, zéro skip, TRX SHA
`59FCE0DEB74F6046B1F5D979F61CE8F5118790D8B59E506D57B372B330A259F0`.
Audit indépendant : **458/458 PASS**, sortie SHA
`EC9498E52DE8E3CF6DF686B761A003EBA902D2291B01C51131305F1BA976B257`.
DLL produit byte-identique A629 SHA
`330E961A17C0CC93C3080378F9775AC3F80675A487A3C4635D66F5F6D5557F79`.

Rapport : `phase5/A630-RAPPORT-PREFLIGHT-HARNAIS-ET-ORACLES.md`.
Verdict : `PASS_PREFLIGHT_QWEN_PAIRED_AUTHORIZED_NO_CALL_YET`. A630 Qwen sans
WinUI est autorisé ; A631 et C008 restent interdits avant résultat complet.
Produit/phase 5 `TESTE_NON_APPROUVE`, Goal actif, ressources arrêtées.

### A636 — préflight du comparatif Qwen Outcome Proposer + Adjudicator

Le harnais A636 a été reconstruit depuis le patch local exact du harnais A630
retiré à A631, puis spécialisé sans réintroduire l'expérience proportionnelle.
Le même ordre des 25 cas et des 50 bras est conservé ; seul le label
`PROPORTIONAL` du schedule live devient `OUTCOME_ADJUDICATOR`. Un schedule oracle
séparé garantit que le manifeste A630 reste caractère par caractère et
SHA-identique (`D1CC...B7F6`).

Le bras baseline garde le flag candidat faux ; le bras candidat ne change que
`OutcomeProposerAdjudicatorEnabled=true`. Le harnais impose 25 runs candidats,
deux appels chacun, températures 0, budgets 640/384, même snapshot, une seule
proposition, zéro EvidenceId inventé, zéro publication sans
`accept_answer_proposal`, zéro texte adjudicateur publié et zéro V6/writer/
reviewer/repair/fallback. Timeout candidat : 180 s. Fail-fast : premier faux
`answer` dangereux, avec les deux dangers totalement exécutés avant les cas
7–25.

Le préflight a détecté et corrigé avant modèle deux défauts de plomberie : les
seams test-only A630 avaient été retirés avec l'ancienne expérience, puis le
nouveau label de bras modifiait le JSON oracle. Les seams exacts ont été
restaurés ; les schedules oracle et live sont désormais séparés.

Tests ciblés candidat + harnais : **30/30 PASS**, zéro skip, TRX SHA
`5BC0BEE21CA5AEB3D8BDF5FEBFA8A65D6387FD092D9824478C399C5768147DAE`.
Audit indépendant : **181/181 PASS**, sortie SHA
`8882DE87E96CA8782AAFBF9E9B7B87F313C037C7C4E830651B91905F00C41170`.
DLL produit byte-identique A635 SHA
`991D9D4F47983342EA063C67FB67D6AB2AE66D2A575FE136A6AF7D2512592966`.

Rapport : `phase5/A636-RAPPORT-PREFLIGHT-HARNAIS-ET-BINAIRE.md`. Lanceur
fail-fast syntaxiquement valide : `phase5/a636-run-qwen-paired-fail-fast.ps1`,
SHA `C1DD2466C0EF91D3B4CFB8DB718B8FF1D6E0F9932C6FDF1D01755533CC9E17F2`.

Verdict : `PASS_PREFLIGHT_QWEN_PAIRED_AUTHORIZED_NO_CALL_YET`. La campagne Qwen
A636 sans WinUI est autorisée. A637, C008 et WinUI restent interdits. Aucun
Telegram à ce palier conformément au protocole ; envoi seulement pour un faux
`answer` dangereux A636 ou la décision finale A637. Produit/phase 5
`TESTE_NON_APPROUVE`, Goal actif, ressources arrêtées.

### A636 — campagne officielle Qwen appariée interrompue et invalide

L'unique campagne officielle A636 a été lancée depuis le wrapper scellé, sans
WinUI et sans replay. Elle a duré environ 38 min 46 s, puis le fail-fast l'a
arrêtée au run 26 sur `candidate_timeout_180000ms`.

État exact :

- 25 cas / 50 runs attendus ;
- **26/50 runs exécutés**, soit 13 baseline et 13 candidat ;
- **24 runs manquants** ;
- baseline partielle : strict 6/13, protocole 10/13, provenance 10/13,
  59 appels, médiane 72 105 ms, p95/max 207 012 ms ;
- candidat officiel partiel : strict 2/13, protocole 4/13, provenance 12/13,
  49 appels, médiane 63 361 ms, p95/max 179 703 ms ;
- réponse fausse dangereuse : **0** ;
- texte de l'adjudicator publié : **0**.

Artefacts officiels immuables :

- campagne JSON : 1 084 116 octets, SHA
  `0ADB745BCB298ADA71885F20D15D67B4CFDEEFA77E7CA69E694A19945085C8D7` ;
- appels JSONL : 567 048 octets, SHA
  `F3516260CFFA2B4FA40F5DEA0CBC7E55470604EE3DC060B8A3104657C1CFED9E` ;
- console : 6 519 octets, SHA
  `133FD36D5090959B287650308DEF191691F9C0DC6C596D7B2B52B324007E241F` ;
- TRX : 16 011 octets, SHA
  `027628AB7690CE34BAD3CCDCB7BB0CD7259130CA2924DB4C7B855503EDE60FED`.

Aucun Telegram d'urgence n'a été envoyé : la condition préenregistrée était une
fausse réponse dangereuse et elle n'est jamais survenue.

### A637 — audit causal avant audit aveugle

L'analyse a démontré un défaut architectural déterministe. Le candidat est
appelé depuis le fast evidence review, lui-même accessible seulement lorsque
`requiredEvidenceCount == 1` et sans layout sémantique. Résultat observé :

- **5/5 cas mono-unité** : candidat tenté, exactement 2 appels chacun ;
- **0/8 cas multi-unités** : candidat jamais tenté ;
- **8/8 cas multi-unités** : chaîne V6 historique rouverte ;
- vrais appels candidat : 10 ; appels V6 des contournements : 39 ; total : 49.

Les cinq vraies tentatives donnent, après correction de deux faux diagnostics
du harnais : strict **3/5**, protocole **5/5**, médiane **55 782 ms**, p95/max
**71 253 ms**. La médiane dépasse la gate préenregistrée de 45 000 ms.

Corrections d'interprétation, sans modification du JSON officiel :

1. run 19 : vrai `accept_answer_proposal`, deux appels, réponse exacte ; les
   traces writer mécaniques ont été prises à tort pour un fallback ;
2. run 26 : timeout avec résultat absent ; les E1/E2/E3 signalés ensuite par
   le harnais ne sont pas des identifiants inventés par le modèle.

La campagne étant incomplète et déjà rejetée par des gates fatales, aucun
audit aveugle final n'a été fabriqué. Verdict :
`REJECTED_PRE_BLIND_AUDIT_INCOMPLETE_CAMPAIGN_AND_ARCHITECTURAL_FALLBACK`.
C008 est interdit et A636 ne sera pas rejoué.

### A637 — retrait du candidat et restauration de la baseline

Les sept fichiers expérimentaux, le flag, les hooks, états, traces et coutures
de harnais A636 ont été retirés. Recherche finale dans `client/` : zéro
référence candidat.

Les hashes de référence sont restaurés :

- runner : `84749A73A0D8E93665A4A0423C09C204072D4B2E2872AA50072CFB0B80F72FF1` ;
- options : `D4858D4D7DA07297736ADBFA2B0FE100F0568CF8BFD63920E7102FC39BA48383` ;
- construction : `1CE24DC8128437694407DD6340AF206F3653F120ED9C46C998447E8A1398F1F0` ;
- fast review prompt : `7C10E09BE40309C128CAFE8BAFA6100775A1F306AB50CAF6C0FE017B57F51B7D` ;
- fast review model : `71D1DE7511C397CD31996B463641A0C40F77A7EC1723FEC3C4E4029E18A48E17`.

Régressions finales :

- client Debug/x64 complémentaire : **1 738/1 738**, zéro skip ;
- client canonique A631 : **1 738/1 738**, zéro skip ;
- serveur complet : **2 054/2 054**, zéro skip.

Deux incidents sont conservés et non promus :

- premier lancement client en `Release`, invalide car les hooks `ForTests`
  sont compilés uniquement sous `DEBUG` ; le produit avait compilé, aucune
  suite n'avait démarré ;
- premier audit final 105/106, seule une empreinte de rapport mal recopiée ;
  corrigée sans changement produit ni résultat.

Audit final A637 : **106/106 PASS**. Script 256 lignes SHA
`8173465C7527D53B4139B86AB2D7B722527D8E66E137B284F54CF43CDCBA75EC` ;
sortie 108 lignes SHA
`937DA6F9CF5337E84186E217C5AA23BF19EB6BF09B4AB4C803177A6F9686123E`.
Verdict exact :
`PASS_A637_REJECTED_PRE_BLIND_AUDIT_CANDIDATE_REMOVED_BASELINE_RESTORED`.

Rapport final :
`phase5/A637-RAPPORT-DECISION-FINALE-REJET-RETRAIT-BASELINE.md`, SHA
`1823109063504109476DD713D634DBDE1D3DB8876999867A337D8AD042B60934`.

`git diff --check` code 0 avec 13 avertissements de fins de ligne historiques.
Branche/HEAD inchangés. Build servers, Qwen géré, port 1234, testhost, WinUI et
dotnet sont tous arrêtés. Produit/phase 5 reste `TESTE_NON_APPROUVE`, Goal
actif. Le prochain travail doit être un recul architectural depuis la baseline
A631, pas un patch ni un replay du candidat rejeté.

### A637 — rapport Telegram final envoyé

Le rapport A637 complet a été envoyé sur Telegram exactement une fois le
`2026-08-31`, de `23:09:24.1599026+02:00` à
`23:09:25.1810955+02:00`, sous le titre
`SAAIA RAG - A637 decision finale et baseline restauree`.

Corps envoyé : le rapport final de 289 lignes, 11 707 caractères et 11 963
octets, SHA
`1823109063504109476DD713D634DBDE1D3DB8876999867A337D8AD042B60934`.
Le notificateur l'a transmis en quatre parties et a confirmé le texte UTF-8
retourné par Telegram. Sortie exacte :
`Notification Telegram envoyee en 4 parties. Corps=11707 caracteres.`

Un seul appel du notificateur, code processus 0, zéro retry. Audit pré-envoi :
**33/33 PASS**, sortie SHA
`214740B4FE4A68398FBD252B5F952D2B96616BD5A3D90EE5FF6AF35628E9C6E9`.
Reçu : `phase5/00-RECU-TELEGRAM-A637-DECISION-FINALE.md`, 48 lignes,
SHA `62C6A3D1A6E3336866DF9818DE3BDD37FCBA2D2A155FCF77E14FDF5B334B31B6`.

Verdict transport : `ENVOI_CONFIRME_EXACTLY_ONCE_NO_RETRY`. Tout second envoi
A637 est interdit. Le Goal reste actif ; le prochain palier doit repartir de
la baseline restaurée et sélectionner une alternative end-to-end avant toute
nouvelle implémentation.

Audit post-envoi : première passe 33/34, seul contrôle trop strict sur une
phrase Markdown coupée par un retour à la ligne ; incident conservé sous
`phase5/A637-INCIDENT-AUDIT-POST-TELEGRAM-RETOUR-LIGNE.txt`, SHA
`953FBFADAA523925306FD08C5E3426DD6DD4B1A54F9EA622D98AD8C4E03C0925`.
Après correction du contrôle par une expression régulière tolérant les espaces,
sans nouvel envoi : **34/34 PASS**, verdict
`PASS_TELEGRAM_A637_EXACTLY_ONCE_NO_RETRY` et
`A637_TELEGRAM_SECOND_SEND_ALLOWED=0`.

**A637 EST CLOS REJECTED, CANDIDAT RETIRÉ, BASELINE RESTAURÉE, TELEGRAM
CONFIRMÉ EXACTEMENT UNE FOIS.** Le palier suivant n'est pas encore sélectionné.

### A638 — recul architectural après A637

L'ADR source-backed, l'attribution de latence A626, les rejets A631/A637 et les
dix appels des cinq vraies tentatives A636 ont été relus. A638 refuse de
rebrancher A634 devant la branche multi-unité : même correctement placé, ses
deux appels avaient une médiane de 55 782 ms et ses issues research/context
restaient terminales au lieu d'exécuter des actions documentaires.

Extraction A638 read-only : **25/25 PASS**. Faits authentifiés :

- 5 tentatives candidat, 8 contournements V6 ;
- médiane proposer : 20 098 ms, prompt moyen 1 765,6 tokens ;
- médiane adjudicateur : 34 678 ms, prompt moyen 2 461,4 tokens ;
- cache adjudicateur : `7,7,1327,7,7` ;
- checkpoint commun présent après tout outil documentaire utile, avant le fast
  gate mono-unité et le prochain tour flat-adequacy multi-unités.

Architecture sélectionnée :
`COMMON_EVIDENCE_CONTROLLER_WITH_CONDITIONAL_ANSWER_REVIEW_V1`.

Le contrôleur Qwen commun à toutes les cardinalités choisit sur le snapshot
EvidenceBundle courant : draft answer, recherche concrète, contexte concret,
clarification ou insuffisance honnête autorisée. Recherche et contexte sont
exécutés ; leurs observations créent un nouveau snapshot puis rendent la main
au même contrôleur. Aucun fallback V6 n'est permis sous le flag candidat.

Un draft answer passe le verifier mécanique puis un reviewer Qwen indépendant.
Seul `accept_source_backed_answer` publie le draft intact. Le reviewer peut
demander recherche, contexte, clarification ou blocage, mais jamais écrire une
réponse. Les actions non publiables ne consomment pas de reviewer.

Pour favoriser le cache sans céder le sens au code, les deux appels answer
partagent system prompt, union d'outils, demande et snapshot byte-exacts ; le
second prolonge le transcript du premier. La relation de préfixe devra être
prouvée en test, mais seule la latence live décidera du gain.

Sur les 13 answers/12 non-answers historiques, minimum théorique : 38 appels ;
plafond : 50. Ce n'est ni un benchmark ni une promesse. Gates proposées :
25/25 checkpoint commun, 10/10 mono et 15/15 multi, zéro trace legacy pour une
signature gérée, zéro faux dangereux, strict ≥baseline+3 et ≥14/25, answers
≥9/13, non-answers ≥10/12, médiane ≤45 s, p95 ≤90 s, max ≤180 s.

Phases sélectionnées : A639 protocole ; A640 RED ; A641 GREEN ; A642
régressions ; A643 campagne appariée unique ; A644 audit/décision/retrait.
Qwen, WinUI et live restent interdits avant leur gate. Aucun Telegram A638 :
A637 vient d'envoyer la décision majeure.

Rapport : `phase5/A638-RAPPORT-RECUL-ARCHITECTURAL-APRES-A637.md`, 380 lignes,
SHA `ADE2C3FB43A6C56DA33871047A7455373BC16443661639B0AC0BBAACCAE87A64`.
Extraction : `phase5/A638-SORTIE-EXTRACTION-RECUL-ARCHITECTURAL.txt`, 53 lignes,
SHA `B424B8CC717B7A179504E2CCA10768B9B6245E8D3E4B5E1EF4A1BB8CE5ECA9CE`.

**A638 SÉLECTIONNE UNE EXPÉRIENCE, PAS UN PRODUIT.** Baseline restaurée,
produit `TESTE_NON_APPROUVE`, Goal actif, C008 interdit.

### A638 — audit de clôture et autorisation A639

La première passe de l'audit A638 a produit **53/56 PASS**. Les trois échecs
étaient exclusivement causés par des contrôles `.Contains(...)` qui ne
toléraient pas les retours à la ligne Markdown au milieu de trois phrases déjà
présentes et exactes sur le fond : revue conditionnelle, préfixe commun et
projection théorique de 38 appels. Aucun fichier produit, aucun protocole et
aucune décision architecturale n'ont été modifiés. L'incident est conservé
sous `phase5/A638-INCIDENT-AUDIT-PHRASES-MARKDOWN.txt`, SHA
`DB6B07940C5890099606F1362193344CD811E20F7962B7EE8275C2304550343D`.

Les trois contrôles ont été rendus tolérants aux espaces Markdown au moyen
d'expressions régulières ciblées. La passe suivante donne **56/56 PASS**,
zéro échec, avec le verdict exact
`PASS_A638_ARCHITECTURE_SELECTED_A639_PROTOCOL_AUTHORIZED`.

Les contrôles authentifient notamment les sources A626/A631/A632/A637, les
artefacts A638, les hashes restaurés du runner, du fast prompt et des options,
l'absence de tout code candidat ancien ou nouveau, les régressions baseline
1 738/1 738 et 2 054/2 054, le Git attendu ainsi que l'arrêt de Qwen, du port
1234, de testhost, VBCSCompiler, WinUI et dotnet.

**A638 EST CLOS : A639 EST AUTORISÉ UNIQUEMENT POUR FIGER LE PROTOCOLE.**
Aucune implémentation candidat, aucun live Qwen et aucun test WinUI ne sont
encore autorisés.

### A639 — protocole préenregistré avant code et modèle

Architecture conservée :
`COMMON_EVIDENCE_CONTROLLER_WITH_CONDITIONAL_ANSWER_REVIEW_V1`.

Le protocole A639–A644 fige avant toute implémentation : checkpoint commun,
éligibilité mécanique, `CommonEvidenceSnapshot`, double empreinte observation
et sérialisation, union ordonnée de dix outils, relation de préfixe byte-exacte,
contrats contrôleur/reviewer, validation Source Contract, automate, exécution
des actions, anti-cycle, budgets, RED/GREEN, régressions, campagne appariée,
audit aveugle, gates de décision, politique de ressources et Telegram.

Point de méthode explicitement résolu : la boucle produit peut consommer plus
de deux appels lorsqu'une action ajoute des preuves. A642 testera cette boucle
productive avec exécuteurs déterministes et signatures réellement nouvelles.
A643 restera une arène de première décision post-retrieval : toute action est
validée et exécutée, mais son exécuteur instrumenté ne fabrique aucune nouvelle
preuve ; une signature inchangée termine le run. Ainsi A643 mesure honnêtement
1 appel sur non-answer et 2 sur draft/review, sans prétendre valider le corpus.

Flag proposé, faux par défaut :
`SAAIA_SOURCE_BACKED_AGENT_V2_COMMON_EVIDENCE_CONTROLLER`. Dès qu'une signature
est prise en charge, aucun fast review, flat adequacy, V6, repair ou fallback
historique n'est accessible.

Gates cumulatives gelées : 25/25 checkpoint, 10/10 mono, 15/15 multi, zéro
trace legacy, zéro faux answer dangereux, zéro publication sans accept, 25/25
protocoles, strict candidat ≥ baseline apparié +3 et ≥14/25, answers ≥9/13,
non-answers ≥10/12, provenance ≥baseline et ≥20/25, médiane ≤45 s, p95 ≤90 s,
maximum ≤180 s. Une seule gate FAIL impose rejet et retrait.

Artefacts :

- `phase5/PROTOCOLE-A639-A644-CONTROLEUR-COMMUN-REVUE-CONDITIONNELLE.md`,
  671 lignes, SHA
  `97BC93A2266A558BB15FF8CFA9B80F172EFC8B6796EB4D80C855310ACEAE2152` ;
- `phase5/A639-MANIFESTE-SURCOUCHE-CONTROLEUR-COMMUN.json`, 107 lignes,
  SHA `EB35BE6AC1463803E481BED6CCAD0B9837A255A493D4DEA9443C18856CEBF434`.

La surcouche a été comparée champ par champ au manifeste A630 : **0
divergence**, 13 answers, 6 research, 3 clarify, 3 context, 10 mono, 15 multi,
2 dangers positions 2 et 6. Elle ne copie pas les questions ni les extraits :
elle les référence par le manifeste A630 scellé.

Aucun code candidat, appel Qwen, WinUI, navigateur ou Computer Use n'a été
autorisé par cette rédaction. **A640 demeure interdit jusqu'au PASS de l'audit
de scellement A639.**

### A639 — audit final et autorisation limitée A640

Deux incidents de harnais sont conservés sans les promouvoir en résultat :

1. premier lancement arrêté au parsing avant tout contrôle, à cause d'une
   interpolation PowerShell incorrecte des backticks Markdown ; incident SHA
   `3A2829BADB7B340A5DF210003506622C4337BC641F41EAA0466E7E34FEE33D42` ;
2. première passe exécutable **94/98**, avec quatre seuls échecs de chaînes
   coupées par des retours à la ligne Markdown ; sortie conservée SHA
   `06C0CDBF15FEB815CF8F267B0141EBF0D8ECDE28914E3CE037B9B7E9FDB20C5A`.

Les expressions d'audit ont été corrigées sans modifier le protocole, la
surcouche, le logiciel ou une décision. La passe de fond obtient **98/98 PASS**,
zéro échec, verdict exact :
`PASS_A639_PROTOCOL_FROZEN_A640_RED_AUTHORIZED`.

Elle vérifie notamment : Goal et ADR ; sources A630/A631/A637/A638 ; 671 lignes
de protocole ; dix outils ; checkpoint commun ; double signature ; préfixe
byte-exact ; exécution documentaire ; anti-cycle ; anti-fallback ; budgets ;
25 exigences RED ; gates qualité/performance ; 25/25 cas identiques au
manifeste A630 ; décisions 13/6/3/3 ; cardinalités 10/15 ; dangers 2 et 6 ;
ordre apparié ; zéro code candidat ; hashes baseline ; Git ; zéro ressource.

**A639 EST CLOS. A640 EST AUTORISÉ UNIQUEMENT POUR ÉCRIRE ET CONSTATER LE RED
DÉTERMINISTE, SANS IMPLÉMENTATION GREEN, SANS QWEN ET SANS WINUI.**

### A640 — RED déterministe constaté

Un seul fichier de tests a été ajouté :
`client/SAAIA.Client.ToolAgent.Tests/SourceBackedCommonEvidenceControllerRedTests.cs`,
444 lignes, 25 `[Fact]`, SHA
`A4F076A7453B58687791E86218681DF6C6545D4BDA8C2A4ECBC25A37F406BC93`.

Le test ciblé Debug/x64 compile les projets produit et tests puis donne le RED
attendu : **25 exécutés, 25 échecs, 0 succès, 0 skip**. Répartition causale :

- RED01 : option `CommonEvidenceControllerEnabled` absente ;
- RED02–RED25 : type
  `SourceBackedCommonEvidenceControllerContract` absent ;
- aucune erreur de compilation, fixture, transport ou ressource.

TRX SHA
`376818E92484B32CEBC8BCCAC09FEEC8E045F5AA250B794098F8296B03EF604A` ;
console SHA
`0AB02C0A46D6A61C66F77B81DAEC5FE0DE49C7B533D5A51D800E739CADB16373`.

Le fichier couvre les 25 contrats A639 : flag, incompatibilités, checkpoint
commun, union/schémas des outils, signatures, préfixe, draft/provenance,
publication/reviewer, exécution search/context, boucle/anti-cycle,
anti-fallback, non-answers, insuffisance, budgets et fail-closed.

Recherche dans le produit : zéro référence candidat. Runner/options/fast prompt
restent aux hashes baseline. `git diff --check` code 0 avec 13 avertissements
historiques. Build servers, Qwen, port 1234, testhost, VBCSCompiler, dotnet et
WinUI sont arrêtés.

Rapport : `phase5/A640-RAPPORT-RED-CONTROLEUR-COMMUN.md`, 194 lignes, SHA
`F18CF515369C25C7D604A051F4812E112D05CC757AD1C703CF6306CC7BE2A4B6`.

Pas de Telegram A640 conformément au protocole. Produit `TESTE_NON_APPROUVE`,
Goal actif. **A641 reste interdit jusqu'au PASS de l'audit A640.**

### A640 — audit final et ouverture du GREEN

Audit A640 : **98/98 PASS**, zéro échec. Le script relit les artefacts A639,
les hashes du test/TRX/console/rapport, les 25 noms RED01–RED25, les compteurs
XML du TRX, la répartition 1 option absente + 24 type absent, la compilation
des deux projets, les marqueurs des contrats, l'absence de questions de canari,
les hashes baseline, l'absence de code produit candidat, Git et les ressources.

Verdict exact : `PASS_A640_RED_AUTHENTIC_A641_GREEN_AUTHORIZED`.

**A640 EST CLOS. A641 EST AUTORISÉ POUR LE GREEN MÉCANIQUE MINIMAL SOUS FLAG
FAUX PAR DÉFAUT. QWEN, WINUI, NAVIGATEUR ET COMPUTER USE RESTENT INTERDITS.**

### A641 — GREEN mécanique minimal clôturé

Le candidat commun est implémenté derrière
`CommonEvidenceControllerEnabled=false`. Les deux anciens finalisateurs
expérimentaux sont incompatibles à la construction lorsque ce flag est actif.
Le runner prend le checkpoint commun après une observation documentaire utile,
trace une ObservationSignature SHA-256 puis s'arrête en fail-closed dans ce
scaffold A641 : aucune branche fast review, flat adequacy, V6 ou repair et
aucune publication sans vérification.

Les composants dédiés restent modulaires : contrat 357 lignes, outils 202
lignes, runner principal 2 496 lignes. Les dix outils stricts, double
signature, préfixe byte-exact, validation de draft, reviewer non rédacteur,
reçus search/context, transitions, anti-cycle, anti-fallback, non-answers,
insuffisance, budgets et fail-closed sont matérialisés mécaniquement. Aucun
identifiant de question live ni donnée de test n'est présent dans le runtime.

Résultats :

- premier passage contractuel : **25/25 PASS** ;
- intégration runner initiale : **30/30 PASS** ;
- passage final multi-domaines et multilingue : **33/33 PASS**, zéro skip ;
- baseline historique flag faux : **5/5 PASS**, zéro skip ;
- `git diff --check` code 0, 13 avertissements de fins de ligne historiques ;
- zéro processus Qwen, llama-server, WinUI, testhost, VBCSCompiler ou dotnet
  après `dotnet build-server shutdown`.

Un incident de fixture est conservé : 30 PASS et 3 échecs car les trois cas
multilingues n'avaient pas reçu leur mission sémantique routeur. La correction
n'a touché que la fixture ; le replay est 33/33. Deux incidents de harnais
d'audit sont également conservés : une erreur de parsing avant contrôle, puis
une passe 60/61 dont l'unique échec comptait à tort les
`additionalProperties:false` imbriqués comme des racines. RED06 parse les
schémas réels ; l'audit corrigé obtient **61/61 PASS**, zéro échec.

Verdict exact :
`PASS_A641_GREEN_MECHANICAL_A642_PREFLIGHT_AUTHORIZED`.

Rapport : `phase5/A641-RAPPORT-GREEN-MECANIQUE-CONTROLEUR-COMMUN.md`, 263
lignes, SHA
`7F7E540776CBB669FB96BD2FE9383DF72A870F3EF34A9929D7F662547299E219`.
Audit SHA
`42E8753C09913D7ABC1AD9C539C2991F3EDAFF686C0616167C3E83DEE5932AD6` ;
sortie SHA
`FC552BEF36144740328E6E7104C8BAC2CEB56602560010016662A581EF5836E1`.

Pas de Telegram A641 conformément au protocole. Produit toujours
`TESTE_NON_APPROUVE`. **A641 EST CLOS. A642 EST AUTORISÉ POUR COMPLÉTER LA
BOUCLE PRODUCTIVE ET EXÉCUTER LES RÉGRESSIONS/PREFLIGHT. QWEN, WINUI,
NAVIGATEUR ET COMPUTER USE RESTENT INTERDITS JUSQU'AU PASS A642.**

### A642 — boucle productive, régressions et préflight A643 clôturés

Le contrôleur commun exécute désormais réellement les actions search/context,
reconstruit l'`EvidenceBundle`, recalcule l'ObservationSignature et poursuit
uniquement lorsqu'une observation matérielle nouvelle existe. Les scénarios
controller search, controller context, reviewer search, no-yield, signature
canonique, budgets et annulation sont prouvés déterministement. Une action sans
nouvelle preuve termine sans second contrôleur ni retour legacy.

Le harnais live A643 désérialise directement le manifeste A630 scellé : aucune
question, aucun extrait ni identifiant de cas n'est recopié dans son source. Il
conserve l'ordre apparié des 50 bras et remplace seulement le bras expérimental
par `COMMON_CONTROLLER`. Le candidat voit les mêmes preuves que la baseline ;
toute action est invoquée mais reçoit un résultat vide, donc un reçu
`completed_no_new_observation` sans preuve synthétique ajoutée.

Le harnais écrit un checkpoint après chaque run et encode toutes les gates
A643 : couverture 25/10/15, zéro legacy/réentrée, reçus, préfixe byte-exact,
sûreté, protocoles, provenance, appels, qualité appariée et latence. Il refuse
un replay si le dossier officiel existe déjà et interrompt immédiatement la
campagne sur un faux answer dangereux ou une erreur de transport.

Résultats finaux sur le binaire scellé : **57/57**, **125/125**,
**1 795/1 795 client** et **2 054/2 054 backend**, zéro échec et zéro skip.
TRX respectifs :
`49B51FB61D3B45F1C8A7D33509BD664E002C021171C6C73E8B8B26C89B19CE15`,
`23BD142B7A2CA824B1913BB295E6E5C4715B1F3BBF3BA2E9C7608D6E85427816`,
`9B7BA65FC88CD99A96D99176A40AF9BB43F5C97BED97F3102998C9120EFED908`
et `60D51FA02AFC575AC1E867448939483D1C7CAD7B5E853DB30A704A8FC40EFDFB`.

DLL produit :
`177E990632BDD3D99049CCE50F942B440959F0635240160FE66C5BE1936D93B3` ;
DLL tests :
`F6E8AB75F4AF7390A988712A859F9D8DCAFF5FAF2DF3A388F59CF779E92CEDF1` ;
harnais A643 :
`D62D863DB1100C9D84B783B5D51C17F7C493595A5A90C5A7308484832AABB726` ;
script live fail-fast :
`4E922249243E99E6C99386CA5E13B716F0F2B21B11AB80B95EF100526DB013B7`.

L'audit indépendant obtient **80/80 PASS**, zéro échec, verdict exact :
`PASS_A642_REGRESSIONS_PREFLIGHT_A643_LIVE_AUTHORIZED`.
Script audit SHA
`AA82D2F1B249AAC4A206EB86151F5E9A7560EC46064DC84382827FECADFF75B8` ;
sortie SHA
`5DF458E0F03785FC7C078036F79FB0D9740B9B5E3369DB8387EA92C143A07585`.

Un incident final de harnais est conservé : un ancien chemin de projet serveur
a produit MSB1009 avant compilation ; le chemin actuel résolu depuis le dépôt a
ensuite donné 2 054/2 054 PASS. `git diff --check` code 0 avec 13 avertissements
historiques. Après extinction : zéro dotnet/testhost/compiler/Qwen/WinUI et
port 1234 libre. Aucun processus Ollama utilisateur n'a été touché.

Rapport :
`phase5/A642-RAPPORT-REGRESSIONS-PREFLIGHT-CONTROLEUR-COMMUN.md`, 240 lignes,
SHA `B674C2DEF12C0ACAC4B77D1104A279879D8186158695B791AE068F7F95BDE654`.

Pas de Telegram A642 conformément au protocole. Produit toujours
`TESTE_NON_APPROUVE`. **A642 EST CLOS. L'UNIQUE CAMPAGNE QWEN A643 EST
AUTORISÉE SANS WINUI, NAVIGATEUR NI COMPUTER USE. AUCUN REPLAY A643 N'EST
AUTORISÉ.**

### A643 — campagne appariée Qwen exécutée, candidat non éligible

La campagne officielle unique a exécuté 50/50 runs en 41 min 53 s : 25 bras
`BASELINE_V6` et 25 bras `COMMON_CONTROLLER`, selon l'ordre préenregistré.
Aucun transport n'a échoué, aucun faux answer dangereux n'a été publié et la
campagne n'a pas été interrompue.

La baseline obtient 13/25 stricts, 21/25 protocoles, 21/25 provenances et 109
appels Qwen. Elle reste particulièrement faible sur context (0/3 strict) et
research (1/6 strict) et ne constitue pas une version approuvée.

Les 25/25 candidats atteignent le checkpoint commun, conservent leur
EvidenceBundle/signature/snapshot, n'emploient aucune trace legacy et ne
publient rien. Toutefois, tous sont arrêtés avant le premier appel Qwen avec
`context_budget_exhausted` : dix schémas d'outils, prompt et snapshot ne
laissent pas les 640 tokens de sortie dans le contexte scellé de 4 096 tokens.
Le candidat totalise donc 0 appel, 0/25 protocole et 0/25 strict.

Les sept gates cumulatives sont rouges : protocole, nombre d'appels, delta
strict, strict global, answers, non-answers et comparaison protocolaire. La
latence candidat à zéro est non interprétable. Le fail-closed a fonctionné,
mais aucune décision sémantique du candidat n'a été testée.

Les 37 runs non stricts ont ensuite été projetés à l'aveugle. Les 37 jugements
confirment le non-strict et ne modifient aucun oracle. Un défaut de collection
est conservé sans réécriture : le JSON final A643 inverse les libellés SHA du
protocole et de la surcouche ; le checkpoint les libelle correctement et les
entrées avaient été authentifiées avant le live.

Rapport A643 :
`phase5/A643-RAPPORT-CAMPAGNE-QWEN-CONTROLEUR-COMMUN.md`, 170 lignes, SHA
`BBF6D9C5169A20A9A99072D69CC12C2E8739FD9488AC2AD202297E135FEB74FB`.

**A643 EST CLOS EN ÉCHEC CUMULATIF. AUCUN REPLAY A643 N'EST AUTORISÉ. A644
DOIT RENDRE LA DÉCISION, RETIRER LE CANDIDAT EN CAS DE REJET ET REJOUER LA
BASELINE.**

### A644 — rejet, retrait complet et baseline verte

L'audit de décision authentifie campagne, checkpoint, appels, TRX, paquet
aveugle, jugements, clé de révélation, gates et ressources : **53/53 PASS**.
Verdict exact :
`REJECTED_A644_REMOVE_CANDIDATE_AND_REPLAY_BASELINE`.

Le flag et la variable d'environnement ont été retirés, les hooks runner et
construction supprimés, ainsi que sept fichiers produit, cinq fichiers de
tests et le runner live spécifiques. Les artefacts historiques A639–A644 sont
conservés comme preuves, sans possibilité de replay. Recherche finale dans le
client : zéro référence `CommonEvidenceController`,
`common_evidence_controller` ou variable d'environnement associée.

Après recompilation de la baseline :

- client : **1 738/1 738 PASS**, 0 échec, 0 skip ;
- backend : **2 054/2 054 PASS**, 0 échec, 0 skip ;
- audit post-retrait : **40/40 PASS**, zéro échec ;
- verdict : `A644_REJECTED_CANDIDATE_REMOVED_BASELINE_GREEN` ;
- `git diff --check` : code 0, 13 avertissements EOL historiques ;
- dotnet, testhost, compilateurs, Qwen et WinUI : arrêtés ;
- port 1234 : libre ;
- Ollama utilisateur : non touché.

TRX client SHA
`62B7415D166AE65B5FFD1D928AF148FC258ED81FDA52596DA7555B982681AFD0` ;
TRX backend SHA
`AA90D8F294A3D67600D83ED9888AF3E73905FB0FFDDFFE7C1E696E657175AFBA`.
Audit post-retrait script SHA
`A3611C4CFEE9B0D0FC74C5D61A3DA910BE7BB3BF5D094498079B1D8D85A4844C` ;
sortie SHA
`6B0BE2BCD2B8D81F04F9633215242FE942A670E233CC6BDE3539DFFA9D329548`.

Rapport A644 :
`phase5/A644-RAPPORT-DECISION-REJET-RETRAIT-BASELINE.md`, 320 lignes, SHA
`4BAE39B115F3C28855EF4A7EC5FB982592279BB6C97AEBCA5D852B648681B489`.

Produit toujours `TESTE_NON_APPROUVE`. **A644 EST CLOS. LE RAPPORT TELEGRAM
A644 DOIT ÊTRE ENVOYÉ EXACTEMENT UNE FOIS. APRÈS PREUVE D'ENVOI, A645 EST
AUTORISÉ UNIQUEMENT POUR UN RECUL ARCHITECTURAL ET UN NOUVEAU PROTOCOLE DE
BUDGET D'ENTRÉE. AUCUN CODE CANDIDAT, AUCUN LIVE QWEN, AUCUN REPLAY A643 ET
AUCUNE VALIDATION WINUI NE SONT ENCORE AUTORISÉS.**

### A644 — rapport Telegram confirmé exactement une fois

Le rapport décisionnel A644 complet a été envoyé à Telegram en quatre parties,
corps 11 770 caractères, par un seul appel du notificateur et sans retry. Le
notificateur a comparé le texte UTF-8 retourné par Telegram à chaque partie
envoyée. Sortie exacte :
`Notification Telegram envoyee en 4 parties. Corps=11770 caracteres.`

Le pré-audit obtient **40/40 PASS** et autorise exactement un appel, zéro
retry. Le post-audit obtient **22/22 PASS**, verdict exact :
`PASS_A644_TELEGRAM_CONFIRMED_EXACTLY_ONCE_NO_RETRY`.

Reçu : `phase5/00-RECU-TELEGRAM-A644-DECISION-FINALE.md`, 57 lignes, SHA
`CCC6AA4A3E65F468DC29B28D4321643CDD866E1CEF7AAFC9BAC6B6229A767ABF`.
Audit post-envoi script SHA
`574A9C67CB52E34068D04498B2A3D1E55D54F77D9321A7120F6ECA0608100A0F` ;
sortie SHA
`4DE36F80482E3A5DC35CA72A1878DB5D116AB4A932D519EA941B93E198D24FE5`.

**TOUT SECOND ENVOI A644 EST INTERDIT. A645 EST MAINTENANT AUTORISÉ POUR LE
RECUL ARCHITECTURAL ET LE PROTOCOLE DE BUDGET D'ENTRÉE, SANS CODE CANDIDAT,
SANS LIVE QWEN, SANS REPLAY A643 ET SANS WINUI.**

### A645 — recul architectural sur le budget d'entrée clôturé

Le diagnostic read-only prouve que l'échec A643 précède toute décision du
modèle : 25/25 bras candidats bloqués avant `CompleteAsync`, zéro appel Qwen,
avec une entrée réelle supérieure à la limite A639 de 3 392 tokens. La valeur
exacte n'ayant pas été persistée, elle n'est pas inventée. Les données
observables minimales des cas ne font que 380 à 694 caractères et les quatre
familles d'oracle sont touchées : la cause est l'enveloppe commune à dix outils,
pas un domaine ni un cas long.

Trois contrats génériques sont gelés sans code produit :

- V1 : un outil partagé contrôleur/reviewer, préfixe commun conservé ;
- V2 : un outil par rôle et reviewer indépendant sans transcript contrôleur ;
- V3 : structured output, contrôle soumis à preuve de support sans fallback.

V2 devient seulement le **leader de mesure**. V1 est le comparateur et V3 le
contrôle ; aucune n'est approuvée. La vue compacte préserve question, plan,
document, EvidenceItems avec identité/provenance/pages/locator, rejets, actions,
capacités, budgets et cycle. Qwen garde chaque décision sémantique ; le code
reste limité aux validations mécaniques.

Tailles statiques exactes : V1 1 213 octets d'outil ; V2 1 196 octets au
contrôleur et 791 au reviewer. Ce ne sont pas des tokens et aucune conclusion
de fit n'en est tirée. Les seuils futurs ajoutent 128 tokens de marge aux 64 de
réserve : contrôleur ≤3 392 tokens d'entrée avec 512 de sortie, reviewer ≤3 648
avec 256 de sortie.

Preuves :

- diagnostic 31/31, SHA sortie
  `622E54C4B8219990F5EE1878622457B27B7597DF4F2BD3617B9797D6875C60BA` ;
- contrats gelés SHA
  `BD6ECE287F3AF3F4448F187604725D461097122DA0BE1AD762041DEE01104DA0` ;
- audit contrats 50/50, SHA sortie
  `25D66A10D8A45D493F2B50CA67DC419061C460189B9002A638A4A252E56A1055` ;
- rapport 365 lignes, SHA
  `B68F0FBFB8754A950625F74F162353035151ABE1544E44B1F8E664A0856ECD60` ;
- audit final 36/36, verdict
  `PASS_A645_ARCHITECTURE_STEP_BACK_A646_PROTOCOL_ONLY_AUTHORIZED` ;
- audit final script SHA
  `328CD10269D007CAC418DF6E4C4CD5032BDBDD46984A2791CA47ED15B600958D` ;
- sortie SHA
  `B9D32B20BCC66C5648ED15EDCD1C13920621B4F82EDBA1A03C76BF7A7FA7396A`.

Qwen, WinUI, dotnet et port 1234 sont restés à zéro. Pas de Telegram A645 : le
rapport décisionnel A644 vient d'être confirmé exactly-once ; le prochain
Telegram intéressant est préenregistré après décision native A649.

**A645 EST CLOS. A646 EST AUTORISÉ UNIQUEMENT POUR ÉCRIRE ET AUDITER LE
PROTOCOLE TOKENIZER-ONLY. AUCUN HARNAIS, SERVEUR QWEN, CODE CANDIDAT, LIVE OU
WINUI N'EST ENCORE AUTORISÉ.**

### A646 — protocole tokenizer-only et reprise A650–A654 gelés

Le protocole A646–A654 fige avant harnais, runtime et code produit : entrées et
hashes ; hypothèses V1/V2/V3 ; reconstruction déterministe des 25 snapshots ;
EvidenceItems diagnostics avec provenance complète ; messages contrôleur et
reviewer ; drafts représentatif/minimal ; projection conservative d'un draft
512 tokens ; gates contrôleur 3 392 et reviewer 3 648 ; deux comptages exacts
par payload ; ordre alterné ; endpoint guard ; cycle de vie runtime ; artefacts
et checkpoint ; 20 exigences TDD ; décisions A649 ; Telegram exactly-once ;
séquence RED/GREEN/live A650–A654.

A648 autorisera seulement GET health/models et POST
`/v1/chat/completions/input_tokens`. Toute tentative POST
`/v1/chat/completions` interrompt le protocole. La projection reviewer ajoute
32 tokens au plafond de 512, en plus des réserves 64+128. V3 ne peut pas être
sélectionnée en A649 sans une preuve de transport distincte.

Deux incidents d'audit sont conservés : parsing PowerShell avant tout contrôle,
puis passe de fond 127/131 dont les quatre seuls FAIL étaient des recherches de
chaînes coupées par Markdown. Les assertions ont été corrigées sans modifier le
protocole. La passe finale authentifie aussi en direct settings, modèle 2,89 Go
et runtime : **131/131 PASS**, zéro échec, verdict exact :
`PASS_A646_PROTOCOL_FROZEN_A647_HARNESS_RED_AUTHORIZED`.

Artefacts :

- protocole 539 lignes, SHA
  `2F1B9F406AE5871715E766314CD2C8A74B19653F74B7787ACBE5D22D0C0DBE58` ;
- audit 161 lignes, SHA
  `EF7DB43747613CFCC28C4E7040616E874122CDDDDA25CAE806716416D113F2E2` ;
- incident 127/131 SHA
  `14986EC032977814BEAE62FA1432D8243126ADB10E23B66A88B8A9AC0F0B83BA` ;
- sortie finale SHA
  `B27C69CFE4ACBD9CFE4240D92E3546C8A750D7D8E8B29BC9B21889DD2E24E82B`.

Qwen, WinUI, dotnet et port 1234 restent à zéro. **A646 EST CLOS. A647 EST
AUTORISÉ POUR LE RED PUIS LE GREEN DU HARNAIS OFFLINE, SANS SERVEUR QWEN,
SANS MESURE NATIVE, SANS CODE PRODUIT ET SANS WINUI.**

### A647 — harnais natif de comptage hors ligne clôturé

Le TDD RED est authentique : le projet compilait, puis les 20/20 contrats ont
échoué uniquement parce que le type harnais n'existait pas encore. Le TRX RED
est conservé, 0 PASS, 20 FAIL, zéro skip, SHA
`02A1AE6E78B0D746A570FEFDDC52B7C2B4E937CAE690FC011F725609DDD031F7`.

Le harnais ajouté exclusivement dans le projet de tests reconstruit les 25
snapshots A630 par hashes scellés, préserve toutes les identités/provenances,
exclut les oracles des messages comptés, construit les enveloppes contrôleur et
reviewer V1/V2, prouve préfixe V1 et indépendance V2, calcule la projection
reviewer conservative, et produit l'ordre exact de 204 comptages. Le runner
hors ligne a exercé 102 payloads uniques exactement deux fois chacun avec un
faux compteur déterministe.

Le garde-transport autorise seulement GET health/models et POST
`/v1/chat/completions/input_tokens`. Un POST `/v1/chat/completions` est prouvé
bloqué avant le faux transport interne. Checkpoint atomique, refus de répertoire
officiel préexistant, fail-closed des gates et extinction en `finally` sont
testés.

Résultats :

- ciblé A647 : **43/43 PASS**, TRX SHA
  `B7EA00CB87A9A5CC6D8DC7F566A15FB39158A8E97B7E1D8C29647F8505F3612A` ;
- `OpenAiLlmClientTests` : **19/19 PASS**, TRX SHA
  `9C6B67AB9ED1140FD29BC5271D6A4087D028FCF67BB9A28A5D6E665022E46A0E` ;
- client complet : **1 781/1 781 PASS**, TRX SHA
  `CA9195F48A66D7A930F284B8B529B6ECFA88932A62F6959405DF715DCBD1258D` ;
- backend complet : **2 054/2 054 PASS**, TRX SHA
  `30399A08C359FFA59D0396B5D861C6DA7C6E1F9E8F0B26C5A85547AE623D90FF` ;
- audit final : **133/133 PASS**, zéro échec, verdict exact
  `PASS_A647_HARNESS_OFFLINE_A648_NATIVE_TOKENIZER_ONLY_AUTHORIZED`.

Les deux erreurs du script d'audit ont été conservées : collection vide sous
`StrictMode` après 124 PASS, puis deux recherches de phrases Markdown coupées
par un retour ligne après 131 PASS. Aucun invariant du harnais n'était rouge.
Sorties incidents SHA
`3F57A13ABA3F71175E90E5C32E42F5A5A0CBCDF7273115406C0201EF67CAD565`
et `9742629456D9033432DA671BEDC76C80EADBF81F9993B29CE6C37273B5A02877`.

Rapport A647 : 382 lignes, SHA
`BBD3DF3CAA46A4D33993A4F1871C71058EC786D4704E830CB503C13B9B0D17DD`.
Audit script SHA
`FC97A985680A49C4042A156146E794D1E7FD507FE5728B41918CD1ADA39A6F83` ;
sortie finale SHA
`3EA32188147F7856A0654348EF93F37D32327CFCDC1250EAD9C8F86D2BC5B4BE`.

Un doublon diagnostique non officiel sous `phase5/` est déclaré hors gate ; sa
suppression a été refusée par la politique d'exécution avant toute mutation.
Après arrêt des build servers : zéro dotnet/testhost/compiler/Qwen/WinUI et
port 1234 libre. Ollama utilisateur n'a pas été touché. Pas de Telegram A647 :
le prochain palier préenregistré reste la décision A649.

**A647 EST CLOS. A648 EST AUTORISÉE UNIQUEMENT POUR LE TOKENIZER NATIF, AVEC
204 REQUÊTES, ZÉRO GÉNÉRATION, ENDPOINT GUARD, DOSSIER OFFICIEL UNIQUE ET ARRÊT
DU RUNTIME EN FINALLY. AUCUN CHOIX V1/V2, CODE PRODUIT, WINUI, NAVIGATEUR OU
COMPUTER USE N'EST AUTORISÉ AVANT L'AUDIT A649.**

### A648–A649 — mesure tokenizer native et sélection V2

Le préflight final A648 authentifie branche, HEAD, Goal, ADR, manifeste,
contrats, protocole, settings, modèle, runtime, source live, runner et
compilation sèche : **74/74 PASS**, zéro échec. Sortie SHA
`51F263B91770E79A09C847E6288898D3ECFC11DF68D7ACBC182D285A94390199`.

Le runtime Qwen scellé a été lancé caché sur `127.0.0.1:1234`, contexte 4 096,
un seul slot. `/v1/models` annonce `n_ctx=4096` et le log confirme
`n_slots=1, n_ctx_slot=4096`. L'unique mesure officielle exécute exactement
204 POST tokenizer : 100 contrôleurs, 52 reviewers représentatifs et 52
reviewers minimaux ; 102 payloads uniques répétés deux fois ; zéro divergence,
zéro code non-200, zéro endpoint interdit, zéro requête/completion/output
modèle. TRX live 1/1 PASS, durée individuelle 6,232 s.

Résultats recomputés :

- V1 : max contrôleur 1 478, max reviewer représentatif 1 691, max reviewer
  projeté 2 127, marge minimale 1 521, éligible ;
- V2 : max contrôleur 1 440, max reviewer représentatif 1 492, max reviewer
  projeté 1 889, marge minimale 1 759, éligible ;
- avantage de marge minimale V2 : **238 tokens**, supérieur au seuil de 32 ;
- le reviewer V2 est en plus mécaniquement incapable de rédiger un draft.

L'audit indépendant relit les dix artefacts, 204 bodies complets, 25 snapshots,
provenance, hashes, ordre, répétitions, schémas, ledger, projection, TRX, profil,
logs et extinction : **145/145 PASS**, zéro échec. Décision exacte :
`SELECT_V2_STAGE_SPECIFIC_INDEPENDENT_ENVELOPES_FOR_RED`.

Script audit A649 SHA
`5FDC9A3F4848E72351C88B2658E8A4C078FD0E2ABB8B7159E247D87BD4D51559` ;
sortie SHA
`6732A369974937B27F40DC791B2DC998D1A899AE570FD87FDA42A90DF0F2DE69`.
Rapport complet A649 : 384 lignes, 14 982 caractères, SHA
`D613860F5FF088751F794BF6A79EFAD5BFF8F8D24C30B79472D8B2CCF7817E7B`.

Incidents conservés : trois erreurs de typage lors de la première compilation
sèche ; pré-lancement stoppé avant dossier/serveur sur collection vide
`StrictMode` ; préflight v1 conservé puis entièrement rejoué ; résumé console
`<1 ms` contredit par le TRX 6,232 s et les 204 JSONL. Aucun incident n'a été
masqué ni utilisé comme preuve positive.

Après extinction : zéro dotnet/testhost/compiler/Qwen/WinUI, port 1234 libre,
Ollama utilisateur non touché. Le produit reste `TESTE_NON_APPROUVE` : aucune
qualité sémantique n'est inférée des tokens.

**A648 ET A649 SONT CLOS. V2 EST AUTORISÉE UNIQUEMENT POUR LE RED PRODUIT
A650. LE RAPPORT TELEGRAM A649 EST MAINTENANT DÛ EXACTEMENT UNE FOIS, SANS
RETRY. AUCUN CODE PRODUIT A650 NE COMMENCE AVANT CONFIRMATION DE CET ENVOI.**

### A649 — rapport Telegram envoyé exactement une fois

Le rapport complet A649, SHA
`D613860F5FF088751F794BF6A79EFAD5BFF8F8D24C30B79472D8B2CCF7817E7B`,
a été envoyé par un appel unique du notificateur, zéro retry, en cinq parties.
Sortie exacte :
`Notification Telegram envoyee en 5 parties. Corps=14982 caracteres.`

L'audit pré-envoi final obtient **45/45 PASS** et autorise un appel, aucun
retry. Reçu : `phase5/00-RECU-TELEGRAM-A649-DECISION-ENVELOPPE-V2.md`,
65 lignes, SHA
`BBB50754F3C55418E8278EF09075095397932B8F606CF75878EF3012CA2473D7`.

**TOUT SECOND ENVOI A649 EST INTERDIT. L'AUDIT POST-TELEGRAM DOIT MAINTENANT
SCELLER L'EXACTLY-ONCE AVANT LE RED PRODUIT A650.**

L'audit post-envoi obtient **33/33 PASS**, zéro échec. Il authentifie le reçu,
le corps inchangé, le pré-audit 45/45, l'appel unique, les cinq parties, zéro
retry, le plan et les ressources éteintes. Verdict exact :
`PASS_A649_TELEGRAM_CONFIRMED_EXACTLY_ONCE_NO_RETRY_A650_RED_AUTHORIZED`.
Script SHA
`0A51250C97852F3B0EDEFF137F8F5568BF2246B524D0F6CB201F38151E97EBA4` ;
sortie SHA
`93A1FAD75CC11B623CB10C313915FD5CD36DBACA004D6E0A97617CCD102B8C05`.

**A649 ET SA COMMUNICATION SONT DÉFINITIVEMENT CLOS. A650 EST AUTORISÉ
UNIQUEMENT POUR UN RED PRODUIT V2 AUTHENTIQUE, SANS LIVE QWEN, SANS WINUI ET
SANS SECOND TELEGRAM A649.**

### A650 — RED produit V2 authentique clôturé

Le contrat produit A650 a été scellé avant le test : flag faux par défaut,
composant central réellement utilisé par le runner, outils V2 contrôleur et
reviewer indépendants, checkpoint compact commun dérivé du EvidenceBundle,
provenance complète, loop search/context, anti-cycle, Source Contract,
publication du seul draft figé vérifié, budgets natifs et interdiction de tout
fallback historique une fois le candidat actif.

Le fichier RED contient exactement 32 tests `[Fact]`, RED01–RED32, avec des
fixtures génériques et sans chargement du corpus A630. Le projet compile puis
le run ciblé obtient le résultat attendu : **32 exécutés, 0 PASS, 32 FAIL, 0
skip, 32 noms uniques**. Causes indépendamment relues : deux `flag absent`, une
variable invalide encore ignorée, vingt-neuf `product contract type absent` ;
aucune erreur de compilation, fixture, transport ou ressource.

Preuves :

- contrat A650 254 lignes, SHA
  `412927F574478C596D5197E9379A07EA34021ED58ADF0BB4DCA9F102A4A6A84D` ;
- test RED 582 lignes, SHA
  `21959CF3CFE4F22028F901085575EA308213771CC2A5BBE55D9E13D11B58BE73` ;
- TRX SHA
  `65F41159480A33EBAC4E98E968D947188A4786D2A13FEFDCE2D3DCC8CBEA8C9A` ;
- audit indépendant **77/77 PASS**, sortie SHA
  `A0E69D45492FEDB7B3C025650677C325D633B8587BC0920BC998FA370D167D87` ;
- script audit SHA
  `5D57D668386C08B0ACE674744698AD7870595CF6DC2EC651F25EA566E2A21CC3` ;
- rapport A650 312 lignes, SHA
  `34614095D7EF3DDDB75E96181D37FBDFD0D53BF39ADD1DA1A1B550A982E85B5C` ;
- verdict exact
  `PASS_A650_AUTHENTIC_RED_32_OF_32_FAILED_A651_GREEN_AUTHORIZED`.

Une première commande ad hoc de lecture XML a été refusée par la politique
d'exécution avant lancement et sans mutation ; la lecture native reformulée et
l'audit indépendant ont ensuite réussi. Aucun fichier produit candidat n'a été
créé ou modifié pendant A650. Après extinction : zéro
dotnet/testhost/compiler/Qwen/WinUI et port 1234 libre. Ollama utilisateur n'a
pas été touché.

Pas de Telegram A650 : A649 a déjà été envoyé exactly-once et le prochain
rapport décisionnel obligatoire reste A654, sauf incident critique
préenregistré. Le produit demeure `TESTE_NON_APPROUVE`.

**A650 EST CLOS. A651 EST AUTORISÉ UNIQUEMENT POUR LE GREEN MÉCANIQUE MINIMAL
V2 DERRIÈRE FLAG FAUX PAR DÉFAUT, OFFLINE, SANS QWEN, SANS BACKEND LIVE, SANS
WINUI ET SANS FALLBACK HISTORIQUE UNE FOIS LE CANDIDAT ACTIF.**

### A651 — GREEN mécanique minimal V2 clôturé

Le candidat V2 est maintenant implémenté derrière le flag
`SAAIA_SOURCE_BACKED_AGENT_V2_STAGE_SPECIFIC_SEMANTIC_ENVELOPE`, faux par
défaut. Le runner bifurque vers un chemin `candidate_only` avant
l'initialisation baseline et interdit tout fallback historique une fois le
candidat actif. Les anciennes expériences sémantiques incompatibles sont
refusées au constructeur.

Le composant central reproduit les prompts et schémas V2 A645, maintient deux
requêtes indépendantes contrôleur/reviewer, un checkpoint compact commun,
l'EvidenceBundle canonique et sa provenance complète. `rag.search` et
`documents.context` sont validés contre les schémas autoritaires, exécutés par
le vrai executor, rematérialisés dans un nouveau checkpoint et soumis à une
gate de progrès. Le draft contrôleur figé passe le Source Contract puis ne peut
être publié que sur `accept` du reviewer indépendant ; le texte reviewer ne
peut jamais devenir la réponse.

Budgets scellés : contexte 4 096, contrôleur 3 392/512, reviewer 3 648/256,
réserve 64, headroom 128, température 0, tool call obligatoire, maximum quatre
appels modèle/outils/cycles et 240 secondes. Le compteur natif et le contexte
runtime sont requis avant génération.

Résultats :

- suite officielle A651 : **44/44 PASS**, dont RED01–RED32 exactement une fois
  et douze résultats d'intégration, TRX SHA
  `587ACE9CCB85FB073852FD6990DA0FEB35481B97CD3C6FC8D0B44BF832CCB4C1` ;
- transport OpenAI/tokenizer : **19/19 PASS**, TRX SHA
  `F759BDD3EDB328E5CACD2DBD49487BA2ED3EDA5D11486E02289CEBD72E853FEB` ;
- Source Contract : **18/18 PASS**, TRX SHA
  `F47771526D9827D651DCA4BFC2A74724EBA009A3DBFC2652604A88812943E7F4` ;
- audit indépendant : **149/149 PASS**, sortie SHA
  `1228F6AD65422F63CB5A701C7CCA4A493B1F91E655569AD88C4A2B4AEF78DAB7` ;
- script audit SHA
  `1814225C28C1DBBB99CEFCEA294976169CEA1BFB7DE4FF29C3AC04741754E47E` ;
- rapport A651 : 414 lignes, 16 603 octets, SHA
  `5748442D1E47F5953E99CA01F7E4009B3BAE088AB371CBC2824CB59AC84004E3` ;
- verdict exact
  `PASS_A651_GREEN_MINIMAL_OFFLINE_A652_REGRESSIONS_PREFLIGHT_AUTHORIZED`.

Incidents conservés : un test initial confondait vérification mécanique et
vérité sémantique ; la fixture structurée initiale n'était pas discriminante ;
une comparaison JSON comparait indentation et compact ; une commande ad hoc de
synthèse TRX contenait un pipe PowerShell vide ; le premier lecteur d'audit
cherchait treize noms symboliques inexacts et obtint 136 PASS/13 FAIL. Les
corrections ont porté sur les tests/fixtures ou le lecteur d'audit, sans
hardcoding sémantique ni altération des TRX officiels.

Après extinction : zéro dotnet/testhost/compiler/Qwen/WinUI et port 1234
libre. Ollama utilisateur n'a pas été touché. Aucun Telegram A651 : le prochain
rapport obligatoire reste A654, le reçu A649 demeure unique.

**A651 EST CLOS. A652 EST AUTORISÉE UNIQUEMENT POUR LES RÉGRESSIONS COMPLÈTES
CLIENT/BACKEND ET LE PREFLIGHT D'UN HARNAIS LIVE DISTINCT, AVEC GATES
QUALITÉ/LATENCE PRÉENREGISTRÉES. QWEN, LE BACKEND LIVE ET LA WINUI RESTENT
INTERDITS PENDANT LE PREFLIGHT. LE PRODUIT RESTE TESTE_NON_APPROUVE.**

### A652 — régressions complètes et preflight A653 clôturés

Le protocole distinct A652–A654 est figé sous SHA
`37BBAD997C2AA6BDE4CBA8BA2CCF811DECB72D5B6C28417882624D6DF787C459`.
Il préenregistre l'identifiant
`A653-STAGE-SPECIFIC-V2-PAIRED-20260901-V1`, 25 cas A630, 50 bras
baseline/candidat alternés, deux dangers positions 2/6, scoring strict,
fail-fast, gates qualité/provenance/latence, artefacts, absence de replay,
audit aveugle A654 et Telegram A654 exactly-once.

Le harnais `LiveStageSpecificSemanticEnvelopePairedCanaryTests.cs` charge le
manifeste par hash, ne recopie aucune question, ne change que le flag candidat,
matérialise les preuves une seule fois puis n'invente aucune observation. Il
collecte messages, outils, schémas, payloads, tokens, temps, décisions,
EvidenceBundle, traces, drafts, vérification et actions. Checkpoint atomique et
JSONL append-only sont écrits après chaque run. Source : 1 585 lignes, SHA
`106B9F796BAEFF1E20E2DDF83ACEC78647F80BE8A1071A5B657AFD9DF5CC39DD`.

Le premier client complet a authentiquement détecté la limite modulaire :
**1 837/1 838 PASS**, un seul échec
`Canonical_source_backed_files_stay_modular`, TRX SHA
`8E89D7C0213EEA99A9F79AAF0CBDBA99BDEC8992AA76F65BF92BD44B8038B042`.
Les fichiers de 821/559/506 lignes ont été scindés mécaniquement en six
partiels de 99 à 467 lignes, sans changement de contrat ou comportement.

Preuves finales sur les sources et DLL scellées :

- ciblé A651 + harnais + transport + Source Contract : **93/93 PASS**, SHA
  `9EF762D600D12C9FF560765EC91C8A0A1B3CCF58354043B97FFAF074B9DCF643` ;
- client complet : **1 838/1 838 PASS**, SHA
  `AA3691202C26CEF80051608FD5C6AEA517CEE708EE356E5945A1C7E0DBB95D3B` ;
- backend complet : **2 054/2 054 PASS**, SHA
  `0F25CCAB6A822A83A88DCF50E012B8E2A153744AB5EEEF8FA8EB55583C8923CA` ;
- DLL produit SHA
  `8754504BAF685A6467D9958A6169834626B911DD5499BA3C5C4BAF7365DAD6FB` ;
- DLL tests SHA
  `983E0F825FC19653CEC07A33F772FF1E35D57CB6A87AB431FE664F1224FFFD64` ;
- script A653 parsé, sans placeholder, SHA
  `A1BD53D362595AE7F1A8DA35C6538C9EA75F8B4965BFB1E602A4D79448F996C1` ;
- audit indépendant **116/116 PASS**, sortie SHA
  `3F4BD80D282375231E2733BD84C143032905102C9E165CE30F4FC3BC2D94E253` ;
- script audit SHA
  `A80D34D288FB324FFBCF595AA94AF56E1F7EBA7FC1A54DB2B30BEBE20F83F72A` ;
- rapport A652 : 326 lignes, 12 031 octets, SHA
  `0710B6E2950382D14A0FA387B8BD3CFFEA527EF17E72E2803CB2FAA18CE861CE` ;
- verdict exact
  `PASS_A652_REGRESSIONS_PREFLIGHT_A653_DISTINCT_QWEN_AUTHORIZED`.

Le modèle Qwen, le runtime et les settings sont réauthentifiés sous leurs
hashes historiques. Qwen n'a pas été lancé en A652. Après extinction : zéro
dotnet/testhost/compiler/Qwen/WinUI et port 1234 libre. Ollama utilisateur n'a
pas été touché. Aucun Telegram A652/A653.

**A652 EST CLOS. A653 EST AUTORISÉE POUR UNE EXÉCUTION UNIQUE DU DOSSIER
`a653-live-qwen-stage-specific-envelope-paired-v1`, AVEC LE SCRIPT ET LES DLL
SCELLÉS. TOUT REPLAY EST INTERDIT. LA WINUI RESTE INTERDITE ET LE PRODUIT RESTE
TESTE_NON_APPROUVE JUSQU'À A654.**

### A653 — campagne live officielle arrêtée en fail-fast

La campagne unique `A653-STAGE-SPECIFIC-V2-PAIRED-20260901-V1` a été lancée
sur le binaire, le modèle Qwen, le runtime, les settings, le protocole et le
harnais scellés en A652. Le dossier officiel a été créé une seule fois sous
`phase5/a653-live-qwen-stage-specific-envelope-paired-v1` et ne doit jamais
être rejoué.

La campagne s'est arrêtée à **4/50 runs** :

- run 1 baseline, drone : `answer`, Cygnus, 36 minutes, E3, strict, sept appels,
  167 603 ms ;
- run 2 candidat, drone : Qwen choisit `search` mais envoie
  `decision/title/message` sans `capability/actionArguments`; le produit écrit
  `protocol_valid=false`, `controller_contract_invalid`, zéro executor et zéro
  réponse ;
- run 3 candidat, danger vanne : même classe d'arguments invalides, refus fermé,
  zéro faux answer, zéro executor et zéro réponse ;
- run 4 baseline, vanne : après `rag.search`, Qwen demande
  `documents.navigation`; l'executor gelé du harnais refuse cet outil par
  `A653_UNEXPECTED_EXECUTOR_TOOL:documents.navigation`, ce qui déclenche la gate
  fail-fast.

Le résumé du harnais affiche superficiellement 2/2 protocoles candidats valides,
mais les traces produit autoritaires disent 0/2 : le harnais ne comptait que le
transport et le nombre d'appels. Sa provenance 2/2 est vacue, car aucune preuve
n'a été exécutée. Les deux temps candidat de 19 938/31 512 ms ne mesurent pas
une tâche terminée et ne satisfont pas la gate imposant 25 runs.

Les neuf artefacts A653 sont présents. Le JSON campagne et le checkpoint sont
identiques sous SHA
`D374F0522DF0A846A5D3D2152F4020E9E5FF97F6F765B7A701EA1C1FDC9C46EF`.
Le TRX FAIL est sous SHA
`FA01020C05695D180B8B809B7C13FE02D5ED9EE8A135D3C50B316D424FDE9261`.
Le shutdown confirme test code 1, zéro processus géré et zéro listener 1234.

**A653 EST CLOS SUR FAIL-FAST. AUCUN REPLAY N'EST AUTORISÉ. A654 DOIT AUDITER
LES TROIS RUNS NON STRICTS, APPLIQUER LA DÉCISION PRÉENREGISTRÉE ET RESTAURER LA
BASELINE AVANT TELEGRAM.**

### A654 — audit aveugle, rejet, retrait et restauration clôturés

Un paquet aveugle de trois items a masqué bras, ordre, latence, tokens, traces
et chemins. Il a été hashé avec ses jugements avant création de la clé de
révélation :

- paquet : 98 lignes, SHA
  `68E79E39EB47C1EB7D24BDEBC8755086FD5989C1D324388CF08F36F7ECF75B27` ;
- jugements : 29 lignes, SHA
  `9B0E63320886B826316289B08718451B9B13F6795C4344F684BF8B8EFBDAAB11` ;
- résultat : **0/3 strict**, trois oracles confirmés, zéro oracle modifié ;
- clé de révélation : SHA
  `C92094A8CDE1DE1C6566FB423F92B5DB286397830CA6F54E2DAFF3264AEF0DD5` ;
- après révélation : candidat 0/2, baseline 0/1, verdict inchangé.

La limite de procédure est déclarée : l'opérateur avait vu les traces causales
avant la projection ; l'aveuglement est celui du paquet scellé, pas celui d'un
second humain. Cela ne change pas le jugement, car les trois runs n'ont produit
aucune sortie sémantique.

Au moins quatre familles de gates sont rouges : campagne 4/50, exception
runtime du harnais, protocole produit candidat 0/2 au lieu de 25/25 et qualité
non démontrée. La décision exacte est :

`FAIL_A654_STAGE_SPECIFIC_V2_REJECTED_BASELINE_RESTORATION_REQUIRED`.

Le candidat a été intégralement retiré avant notification : six fichiers
produit, trois fichiers tests, option/variable d'environnement, validation de
compatibilité et bifurcation runner. La recherche récursive dans les sources
client trouve zéro symbole ou flag stage-specific. Les artefacts historiques
sont conservés.

Régressions après retrait :

- client : **1 782/1 782 PASS**, zéro skip, TRX SHA
  `6AFBAB4C31C7FEDD8C186B67ED262A6AA5C51535C28E8C10E0D68DFD2126822C` ;
- backend : **2 054/2 054 PASS**, zéro skip, TRX SHA
  `7027584440400EECCC27F30043CC428346AD59DA205FFE2E0C178C5333B1CEB6` ;
- total : **3 836/3 836 PASS** ;
- `git diff --check` code 0 avec treize avertissements EOL historiques ;
- zéro dotnet/testhost/compiler/Qwen/WinUI et port 1234 libre ;
- Ollama utilisateur, navigateur, Computer Use et bureau non touchés.

Rapport décisionnel : `phase5/A654-RAPPORT-DECISION-RETRAIT-ENVELOPPE-V2.md`,
427 lignes, 18 788 caractères, SHA
`F267C7F4A8D9A81FFB924F7B42264299604838BF12E22762DD6E8754ED191AC2`.

Le premier pré-audit a été bloqué à 109/110 par une assertion qui lisait
`masking.arm=true` comme si un item aveugle contenait le bras ; zéro Telegram a
été appelé. Après correction du lecteur seulement, le pré-audit final est
**110/110 PASS**, sortie SHA
`FB6FD11A9D1CF1D735C8CEA82D891769B818A5EB88473BF6D7B35746919ECC78`.

Telegram A654 a été envoyé exactement une fois le 2026-09-01 de
05:36:18.1302949+02:00 à 05:36:19.2537519+02:00, par un seul appel du
notificateur, zéro retry, en six parties. Sortie exacte :
`Notification Telegram envoyee en 6 parties. Corps=18788 caracteres.`
Le notificateur a vérifié le texte UTF-8 retourné. Reçu :
`phase5/00-RECU-TELEGRAM-A654-DECISION-RETRAIT.md`, SHA
`EFDBE770799FEABB626510D12369841AA1310EF1BDD754CC44BD572D68CD2E7C`.
Tout second envoi A654 est interdit.

**A654 EST CLOS SUR FAIL. L'ENVELOPPE V2 EST REJETÉE, LE CODE CANDIDAT EST
RETIRÉ, LA BASELINE EST RESTAURÉE ET VERTE. LE PRODUIT RESTE
TESTE_NON_APPROUVE, LE GOAL RESTE ACTIF ET LA WINUI RESTE INTERDITE. A655 EST
AUTORISÉE UNIQUEMENT POUR UN RECUL ARCHITECTURAL GÉNÉRIQUE : CORRIGER D'ABORD
LA FIDÉLITÉ DU HARNAIS BASELINE, CARTOGRAPHIER LES ÉCHECS RÉCURRENTS DE SORTIES
STRUCTURÉES QWEN ET PRÉENREGISTRER UNE COMPARAISON D'AU MOINS DEUX ARCHITECTURES
END-TO-END. AUCUN NOUVEAU CODE CANDIDAT OU LIVE QWEN AVANT CE PROTOCOLE.**

### Correctif de communication — renvoi du rapport complet de 24 heures

Le 2026-09-01, l'utilisateur a signalé ne pas avoir reçu le rapport complet
des dernières 24 heures. Le reçu local initial attestait pourtant un premier
appel accepté le 2026-08-31 à 17:11 en huit parties. La déclaration utilisateur
a donc autorisé un renvoi explicite du même corps, sans le confondre avec un
retry automatique.

Le rapport exact, SHA
`5A2084CEBBE5AEEB1B5044920BC6C5020709D7046D28B69057C6CF9A36026A76`,
a été renvoyé par un seul appel du notificateur le 2026-09-01 de
`05:59:30.1374188+02:00` à `05:59:31.8372558+02:00`, sous le titre
`SAAIA RAG - RENVOI DEMANDE rapport complet 24h au 31 aout 17h02`.
Sortie exacte :
`Notification Telegram envoyee en 8 parties. Corps=23317 caracteres.`
Zéro retry. Reçu :
`phase5/00-RECU-TELEGRAM-RAPPORT-24H-20260831-RENVOI-UTILISATEUR-20260901.md`.
Aucun troisième envoi n'est autorisé sans nouvelle demande explicite.

### A655 — recul architectural et comparaison de formats

A655 a analysé les échecs A631/A637/A644/A654 et les traces A630/A636/A643/A653.
La forensique établit que **277/277** arguments d'outils historiques sont du
JSON syntaxiquement valide : la panne répétée porte sur le schéma complet et
la couture end-to-end, pas sur la simple syntaxe JSON. A636 avait atteint 10/10
appels directs discriminés parsables et 5/5 protocoles produit sur les essais
réels ; A653 a produit 2/2 wrappers parsables mais 0/2 protocoles produit, car
les objets ne contenaient que `decision,title,message`.

La forensique a aussi prouvé que l'exécuteur réel de
`LiveMultiturnRecoveryBenchmarkTests.cs` couvre cinq routes internes :
`rag.multi_search`, `rag.search`, `documents.navigation`,
`documents.content_cards`, `documents.context`. Le prochain harnais gelé devra
toutes les accepter pour ne pas reproduire l'exception artificielle A653.

Artefacts :

- `A655-SORTIE-FORENSIQUE-ECHECS-RECURRENTS-ET-HARNAIS.txt`, 48/48 PASS,
  SHA `8835242C629FBAA8BF7584375919EF253E4894E62F83180823A0CB2E9631079E` ;
- `A655-RAPPORT-RECUL-ARCHITECTURAL-APRES-A654.md`, 311 lignes, SHA
  `9D857D6085F1CC2E8C15DCC28CAA3731E657484EBEB2D0F62DE782B63B487C00`.

Décision : comparer avant tout code produit
`DIRECT_NATIVE_PALETTE`, `ROUTE_THEN_SPECIALIZED_PAYLOAD` et
`GRAMMAR_CONSTRAINED_JSON`, avec la baseline V6 comme contrôle.

### A656 — protocole et contrats trois formats scellés

Le protocole A656–A665, le manifeste exact des trois contrats et l'ordre latin
25 × 3 ont été écrits avant tout harnais et tout Qwen. L'audit initial a bloqué
à 90/92 sur deux défauts documentaires réels : un mot excédentaire dans la
description de `documents_navigation` et une instruction A655 devenue obsolète.
L'incident complet est conservé sous SHA
`D64348DC2C317515DFE2AA1EF0BD83FF4911CB7A3C7202C2D7FD8761CA1A68E7`.

Après correction et rescèlement avant toute mesure :

- protocole : SHA
  `D8A2BD1271383BB3E344449CDF651C4D478AA79582E53478C1D86E9B9B9FF2BB` ;
- manifeste : SHA
  `3190AA1C431EABF052FF7398280EE88F1EDC3DD90D6FD7332F767BBE348F667D` ;
- ordre : SHA
  `577C007590AED08E40E348565A8D91642D887C1A4BD018B90835EF2D0F904FF0` ;
- audit final : **92/92 PASS**, sortie SHA
  `3C7D9A3272EA61E874B96481940EB03ACD3886FC7AB9F6758A93754CD868E3D2` ;
- rapport : `A656-RAPPORT-SCELLEMENT-PROTOCOLE-CONTRATS-TROIS-FORMATS.md`.

Les gates sont figées : 25/25 protocoles, strict ≥14/25, réponses ≥9/13,
non-réponses sûres ≥10/12, médiane ≤45 s, p95 ≤90 s, max ≤180 s, zéro faux
answer dangereux, zéro fallback, audit aveugle. Aucun Telegram A656, aucun
Qwen, aucune WinUI, aucun code produit. Les ressources lourdes gérées sont
arrêtées et le port 1234 est libre.

**A656 EST CLOS SUR PASS. A657 EST AUTORISÉE UNIQUEMENT POUR LE TDD HORS LIGNE
DU HARNAIS ET DE L'EXÉCUTEUR FIDÈLE. LE PRODUIT RESTE TESTE_NON_APPROUVE, LE
GOAL RESTE ACTIF ET AUCUN LIVE QWEN N'EST AUTORISÉ AVANT A659.**

### Passage de relais demandé le 2026-09-07 — arrêt avant GREEN A657

L'utilisateur a décidé de poursuivre le développement dans une nouvelle
discussion et a demandé de recréer le dispositif de reprise utilisé au début
de la présente discussion. Aucun développement supplémentaire n'est lancé
pendant la préparation du handoff.

État revérifié le `2026-09-07T16:28:55+02:00` :

- Goal Codex : `PAUSED` pour le passage de relais, non terminé et non bloqué ;
- produit et phase 5 : `TESTE_NON_APPROUVE` / `EN_COURS` ;
- branche/HEAD : `SAAIA_V3.1` / `5f35881cdc67d12a076fcd2a7a1004656ac9a37a` ;
- remote vérifié par `git ls-remote` :
  `d2797b806541ee6ac2be1faa45c564d49bf40ecd`, soit 15 commits locaux
  d'avance et 0 de retard par rapport à `origin/SAAIA_V3.1` ;
- worktree : 599 entrées, soit 71 modifications, 8 suppressions et 520 non
  suivis ; 0 fichier stagé ;
- diff suivi : 79 fichiers, 9 664 insertions et 86 563 suppressions ;
- `git diff --check` : code 0, treize avertissements EOL historiques ;
- processus SAAIA/Qwen/WinUI/test/build gérés : 0 ; listener 1234 : 0 ;
- aucun contrôle graphique de l'ordinateur utilisé.

Le point de reprise est **après le RED A657 et avant son GREEN** :

- fichier de test RED :
  `client/SAAIA.Client.ToolAgent.Tests/ThreeFormatQwenContractHarnessRedTests.cs`,
  SHA `06FD50E1B1A4A3AFD1FEA68679AA9F865DE35EAE6D48B1C3AFC822BFF1980B88` ;
- compilation x64 Debug réussie ;
- exécution ciblée : 11 tests, 11 FAIL attendus, 0 PASS, 0 skip ;
- TRX :
  `phase5/a657-red-three-format-harness/a657-red-three-format-harness.trx`,
  SHA `63B3D6FA5036B377D3E7B4FFF27CE3A80DAA4FDB1DC7A97190CB69FAACEA969F` ;
- une tentative préalable non promue avait échoué à compiler faute de
  `using Xunit;` ; l'incident est conservé et ne compte pas comme RED ;
- `ThreeFormatQwenContractHarness.cs` n'existe pas encore ; aucun GREEN A657,
  aucun A658, aucun Qwen et aucun code produit A657 n'ont été exécutés.

Le dispositif de reprise comprend aussi l'export Markdown des messages visibles
de cette discussion :
`DISCUSSION-2026-08-26-AU-2026-09-07-EXPORT-VISIBLE.md`, 15 105 lignes,
1 464 749 octets, 3 218 messages et SHA
`B5234A4417C9C21DE867C2F321E0B0237FF09BC453EDE7CE540795F7C483084F`.
Il exclut les instructions système/développeur, le raisonnement interne et les
appels/sorties d'outils.

La nouvelle discussion doit reprendre par la construction du harnais A657
strictement dans le projet de tests, conformément au protocole A656–A665. Elle
ne doit ni refaire le RED, ni démarrer Qwen, ni lancer WinUI, ni restaurer un
candidat rejeté.

### Reprise sur la machine de destination — 2026-09-07, GREEN A657

La demande actuelle de Maxime a autorisé le remplacement intégral du dépôt par
l'archive projet du laptop. Le transfert est vérifié et l'ancien contenu est
sauvegardé séparément. Le HEAD et les changements du handoff ont été préservés.
Maxime a précisé que le fichier Goal est une **proposition** : aucun Goal n'a
été créé ou activé dans cette nouvelle discussion. Les mentions historiques
de Goal actif ou en pause ci-dessus ne décrivent pas son état actuel ici.

A657 a été implémenté uniquement dans le projet de tests : cinq fichiers
nouveaux, aucun changement du code produit. Les 11 tests RED originaux sont
inchangés. Rebuild x64 Debug réussi ; première suite ciblée 60/60 PASS, puis
suite complétée 67/67 PASS (11 tests historiques et 56 cas fonctionnels),
zéro skip et zéro échec. Les contrats A656, les oracles et les seuils restent
inchangés. Mille fichiers source/projet importés ont été rehashés sans dérive.

Verdict : **PASS_A657_OFFLINE_MULTI_FORMAT_HARNESS_AND_FAITHFUL_EXECUTOR**.
Le produit reste **TESTE_NON_APPROUVE**. Aucun Qwen, backend live, WinUI,
Telegram, commit, stage ou push n'a été lancé pendant ce palier.

Rapport :
`artifacts/goal-rag-product-20260827-1041/phase5/a657-green-three-format-harness/A657-RAPPORT-GREEN-PC-2026-09-07.md`.
Audit et hashes : `A657-AUDIT-GREEN-PC.json` dans le même répertoire.
TRX final : `a657-green-attempt-02.trx`, SHA
`E211B35209B5B9F3A834A68AECB47A7A9F90395A98D744F7922DDBD915D51477`.

Prochaine étape : préparer les dépendances externes du laptop et qualifier
l'environnement de cette machine avant A658. Le Q5 actif, le runtime b10098
et les settings AppData ne font pas partie du dossier projet ; ils sont
absents aux chemins attendus ici. Le Q4 inclus ne les remplace pas. A658 n'a
pas commencé ; aucune génération avant A659 et aucune WinUI à ce stade.

### Activation du Goal et reprise documentaire intégrale — 2026-09-08

À la demande explicite de Maxime (« met en place le goal »), le Goal est
désormais **ACTIF**, sans budget de tokens imposé. Cette décision remplace
l'état « proposition non activée » de l'entrée du 7 septembre ci-dessus.
Le texte détaillé est conservé dans `GOAL-ACTIF-2026-09-08-REPRISE-PC.md` :
il reprend la proposition originale et précise l'ordre de reprise sur ce PC.

La première phase obligatoire est la **lecture intégrale des pièces et des
documents désignés par le prompt**, avant toute nouvelle implémentation
produit. Le transfert vérifié et A657 restent acquis, sous réserve des écarts
que cette lecture pourrait révéler. A658 demeure non commencé.

Le suivi réel est `REGISTRE-LECTURE-REPRISE-2026-09-08.md`, appuyé par les
hashes, lots affichés, confirmations et notes dans
`artifacts/reprise-pc-20260908/lecture/`. Extraction et indexation ne sont pas
comptées comme lecture. Les discussions et le plan complet restent en cours ;
aucune affirmation de lecture exhaustive achevée n'est faite.

Les mesures ultérieures identifieront la RTX 4060 actuelle séparément des
références P520. Les contrats et gates A656 restent inchangés. Les instructions
historiques de communication ne sont pas rejouées automatiquement.

### Reprise PC — 2026-09-08, lecture intégrale, réaudit A657 et PASS A658

Le Goal reste ACTIF. Les 43 documents désignés sont lus intégralement :
15 847 677 caractères couverts, registre avec SHA/plages/notes/répétitions
strictement identiques. Les limites visuelles et la couverture distincte des
artefacts techniques restent déclarées. Le rapprochement est consigné dans
`MATRICE-REPRISE-CDC-DECISIONS-CODE-2026-09-08.md` ; la condition préalable
de lecture et de rapprochement est satisfaite. Le transfert n'est pas refait.

Le réaudit A657 a reproduit deux défauts par 12 tests RED : rejet d'un docRef
réellement observé et acceptation d'un extrait modifié après exécution avec
identités conservées. Correction exclusivement dans le harnais de tests ;
79/79 GREEN, zéro skip. Les preuves initiales 67/67 restent conservées.
Rapport : `artifacts/reprise-pc-20260908/a657-reaudit/rapport.md`.

Les dépendances externes manquantes ont été installées depuis les publications
du modèle et de b10098, avec tailles/SHA vérifiés. La campagne reste Qwen3-4B
Q5_K_M, 4096, t4, batch512, ubatch128, ngl65, Jinja, FA, parallel1, CUDA0,
KV f16, sur RTX4060 ; aucune mesure P520 n'est revendiquée.

A658 : **PASS_A658_AT_LEAST_TWO_FORMATS_NATIVE_BUDGET_ELIGIBLE**.
510 payloads préparés hors ligne, 1020 mesures natives stables, zéro génération.
Palette directe : contrôleur max3096, reviewer projeté max3475.
Route/payload : max1572/max2153. Limites inchangées 3392/3648.
Grammaire éliminée : comptage identique avec/sans schéma et après amplification,
donc inclusion du response_format non démontrée. Aucun fallback.
Runtime arrêté en finally, port1234 libre, aucun processus utilisateur arrêté.
Rapport : `artifacts/reprise-pc-20260908/a658-native-pc-01/RAPPORT-A658.md`.

Suite : préparer A659 sur les deux familles éligibles, conserver sorties brutes,
distinguer erreurs ordinaires de protocole et ruptures fatales, juger le paquet
avant révélation des bras. Aucun candidat produit ni backend live/WinUI à ce palier.
Le produit reste **TESTE_NON_APPROUVE**. Rapports Notifier envoyés au jalon
lecture complète/réaudit ; fréquence horaire actuelle autorisée par Maxime.

### Reprise PC — 2026-09-08, A659/A660 rejetés, diagnostic A666 à préparer

**FAIL_A660_NO_FORMAT_ELIGIBLE_NO_PRODUCT_CODE**. Six transactions,
douze appels Qwen réels : les deux familles ont utilisé un fichier comme
categoryPath non observé. Fail-fast effectif, aucun texte publié, zéro retry.
Les demandes de clarification observées répétaient aussi des faits à rechercher ;
une réponse déjà visible entraînait une relecture sans utilité.

Le paquet sans bras a été jugé puis hashé avant révélation de la clé. Les
oracles sont conservés. La projection d'une transaction interrompue au reviewer
ne conservait pas le draft contrôleur : limitation constatée, réponses brutes
préservées, pas de sauvetage a posteriori du verdict. Aucun produit candidat
n'ayant été ajouté, aucun retrait produit n'est nécessaire. A661–A665 ne sont
pas ouverts par ce résultat. Runtime arrêté, port libre.

Rapport : `artifacts/reprise-pc-20260908/a659-microcampaign-pc-01/RAPPORT-A659-A660.md`.
Notification actuelle envoyée via Notifier, reçu dans le journal des rapports.

Le Goal général reste ACTIF et ne se bloque pas sur cette famille facultative.
Prochain diagnostic A666 : comparer, sur des cas nouveaux scellés avant appel,
les instructions de décision succinctes et une explication générique des rôles
answer/research/context/clarify et des identités. Modèle, budgets, catalogue,
format et garde de provenance inchangés ; un seul facteur d'instruction.
Ce diagnostic n'est pas une réactivation produit des candidats rejetés et ne
change aucun verdict A659/A660. Préparer son protocole et ses preuves hors ligne
avant toute génération, puis décider la suite d'après le résultat effectif.

### Reprise PC — 2026-09-08, A666 rejeté, audit mécanique A667

A666 : FAIL_A666_NO_PRODUCT_PROMOTION. Deux bras arrêtés, 18 transactions
sur 36 prévues, 40 appels Qwen effectifs. Contrôle 1/7 strict, traitement 2/11.
Quatre textes finaux du harnais, dont trois corrects et un incomplet accepté
à tort. Rejet distinct du contrôle sur un PDF utilisé comme catégorie.
Douze completions avec texte libre hors appel natif. Aucun oracle assoupli.
Jugements des 18 sorties scellés avant révélation des bras, puis hashes HTTP
et comptages natifs vérifiés. Runtime arrêté, port1234 libre.
Rapport : artifacts/reprise-pc-20260908/a666-guidance-pc-01/RAPPORT-A666.md.

A667 commence par examiner le gabarit embarqué du modèle et le parseur b10098.
Le gabarit du GGUF et le gabarit officiel Qwen diffèrent ; la cause des erreurs
reste à établir. Diagnostic mécanique séparé, sans requalifier les échecs,
sans paraphrases successives, sans nouveau candidat produit. Goal toujours actif.

### Reprise PC — 2026-09-08, A667–A669 et retour à la baseline verticale

A667 : audit sans génération des40 requêtes A666 sous deux gabarits. Prompts
et comptages identiques. La grammaire native b10098 autorise un préambule
avant l'appel malgré tool_choice required : écart de contrainte établi.
A668 : préfixe structurel de début d'appel, aucune décision préremplie ;
18/18 contrôles mécaniques contre12/18, trois répétitions,36 appels.
A669 : nouveau jeu18cas, préfixe commun aux deux bras, mêmes instructions.
16 transactions/56 appels, zéro texte hors outil ; dix réponses correctes.
Les deux bras sont néanmoins rejetés :5 stricts chacun, mauvaises décisions
sur faits absents et catégorie inventée par le contrôle. Aucune hauteur
inventée ni publication dans le cas portant le code dangerous_false_answer.
93 tests ciblés PASS, audit aveugle intégral des transactions puis révélation.
Rapports A667/A668/A669 conservés ; pas de nouveau candidat produit.

Le Goal prime sur la branche expérimentale facultative. La suite revient
à la baseline du chemin de production et des preuves jusqu'au rendu, en
commençant par restauration/build/tests locaux et inventaire des accès au
serveur réel. Aucun backend5122/Qdrant/embeddings local n'est actif ; Docker
et WSL ne sont pas installés. Le Postgres existant est laissé intact. Une
question sur l'adresse/configuration du serveur a été envoyée à Maxime ;
les vérifications locales continuent sans attendre cette réponse.

La reconstruction complète a d'abord échoué sur des caches NuGet du laptop.
Restore --force a régénéré les assets locaux ; rebuild solution Debug/x64
réussi avec zéro erreur/avertissement. Suites hors tests Live en cours,
les tests d'intégration conditionnels sans SAAIA_TEST_PG_CONN ne prouveront
pas une exécution PostgreSQL réelle. Ne pas transformer un vert local en
validation du corpus ou de WinUI. Dernier Notifier envoyé à12:47UTC.

### Reprise PC — 2026-09-08, A670–A677 : contrats et vraie intégration PostgreSQL

A670 retire l'avancement automatique de pagination après un appel strictement dupliqué : le client renvoie duplicate_tool_call et Qwen choisit la suite. Deux régressions rouges avant correction ; 320 tests ciblés puis 1710 tests client hors Live passent. Aucun candidat de prompt A669 n'est intégré.

A671 révèle que la connexion PostgreSQL absente faisait sortir plusieurs tests sans exécuter leurs scénarios. La baseline 3754 tests annoncés verts ne validait donc pas ces intégrations. Une base PostgreSQL16.14 distincte est créée et arrêtée à chaque série dans LocalAppData/SAAIA, sur 55432, avec vérification du data_directory. Le service Odoo5432 et ses données restent intacts. Série initiale :1893 réussites/142 échecs. Six classes ciblées :123/93 puis161/55 puis177/42. Les chiffres ne s'additionnent pas. La dernière série globale ciblée est en cours de mise à jour dans les artefacts.

Corrections produit démontrées sur cette base : A672 projection SQL du message modifié et qualification des colonnes du statut de résumé ; A673 création du parent JSON quality manquant sans perte des autres propriétés ; A674 métadonnées facultatives d'un résumé stockées comme objet vide ; A675 stabilité du cache de snapshot vide et invalidation après publication ; A676 seize quantificateurs SQL absorbés par l'interpolation C# ; A677 diagnostic de la tentative OCR échouée courante sans remplacement de la version publiée.

Les fixtures sont réparées uniquement sur preuve : paramètres SQL oubliés, FK tenant absente, signatures modifiées, casse effective des objets JSON, ordre des arguments, index des cartes, versions de profils et contenu trop court pour le seuil d'ingestion. Les attentes sémantiques rouges restent à instruire ; aucune règle métier n'est ajoutée pour les faire passer.

Preuves : artifacts/reprise-pc-20260908/a670-pagination-contract et dossiers a671 à a677 ; rapports, TRX, préimages, hashes et audit cumulatif. Aucun commit/staging. Goal actif, TESTE_NON_APPROUVE. La configuration serveur du laptop reste demandée à Maxime ; elle n'interrompt pas le travail local.

## Mise à jour après A679 et round23 — 8 septembre 2026

La passe globale round18 utilise le filtre FullyQualifiedName!~SAAIA.Backend.Tests.Live :2052 tests,2035 réussites,17 échecs,3min48. Les trois DLL sont inchangées pendant la série, le cluster est arrêté et le port55432 libéré. Depuis cette passe, trois échecs de préparation/contrat sont validés séparément : suppression de résumé et projection SQL (round19), cartes conservées par révision avec sélection courante distincte (round20), publication des dix artefacts avec métadonnées cohérentes et identité de source (round23). Il reste14 échecs dans current-frontier.json ; ce suivi ne constitue pas une nouvelle passe globale.

A678 ajoute la validation PostgreSQL du chemin canonique : quatre requêtes, versions et tenants concurrents, catégorie/path/ref/page, hashes/chunks/texte/provenance, champs de consigne serveur null. A679 corrige les compteurs d'alertes de page qui omettaient le diagnostic publié. Série dédiée round17 :10/10, y compris exclusion des pages d'une ancienne révision.

Les dernières corrections de fixtures distinguent les mots uniques d'un résumé et les mots du nom du document ; conservent les anciennes cartes dans leur propre révision ; utilisent un vrai titre de section et des phrases indexables ; comparent JSONB après parsing. Aucune règle sémantique de production n'a été changée. La baseline restante contient encore des attentes du chemin de recherche historique ; leur classement en dette ne prouve pas que ce chemin est mort.

Rapport consolidé et Notifier : rapports/RAPPORT-CONSOLIDE-A670-A679-20260908.md et rapports/notifier-a675-a679-20260908.txt. Envoi confirmé à14:36UTC, reçu horodaté14:36:41 ; prochaine échéance horaire15:36UTC. Le script écrit via Write-Host : capturer 6>&1 pour un futur accusé automatisé, ne pas renvoyer ce rapport. La première détection du reçu avait échoué après un envoi effectif ; ce n'était pas un échec Telegram.

Audit cumulatif :19 préimages contrôlées,15 fichiers distincts originaux vérifiés contre l'archive laptop,4 préimages intermédiaires identifiées. Les patches courants sont cumulatifs et ne remplacent pas les préimages. Aucun commit/staging. Goal actif, TESTE_NON_APPROUVE. Les essais réels de Qwen, des PDF privés et de WinUI restent à effectuer ; la configuration du backend laptop est toujours demandée.

## Mise à jour A680–A683 — 8 septembre 2026, après 15h UTC

La chaîne backend → normaliseur WinUI → EvidenceBundle a été vérifiée avec des réponses JSON exportées des scénarios PostgreSQL réels, copiées à l'identique et scellées par SHA. Les contrôles utilisent le code du vrai projet client. Les transports des tests client sont simulés : ce ne sont pas des validations live de Qwen ou de WinUI.

Corrections : A680 conserve la requête exécutée et supprime le calcul sémantique de selectionHints dans le normaliseur canonique ; A681 contrôle identité/révision/ancre avant d'attacher les extraits voisins, et conserve les refus dans CodeHints ; A682 empêche la fusion de chunks complets portant des identités distinctes malgré chemin/page/texte identiques, et conserve toutes les observations des doublons exacts ; A683 sépare le contexte de recherche du texte cité et transmet les diagnostics de dégradation à RetrievalAttempts.

A681 reproduit une réextraction réelle entre recherche et contexte : même hash, nouvelle révision, ancienne ancre absente. A683 utilise désormais le projecteur de contexte de production ; ses trois réponses réelles étaient déjà fidèles avant correction, tandis qu'une mutation explicite de contexte plus long révélait la promotion incorrecte dans fullText. Les variantes de contrat ne sont pas présentées comme des mesures sur corpus privé.

Validation après A683 :34 tests ciblés passent ;1762 tests client hors classes Live passent ; solution complète zéro avertissement/zéro erreur (28,43s). Nouvelle passe globale backend round28 en cours ; ne pas annoncer de nouveau total backend avant son résultat. Les précédents14 échecs restent ouverts jusqu'à vérification. Rapports détaillés et patches sous artifacts/reprise-pc-20260908/a680 à a683. La vérification A684 des méthodes d'exécution réelles rag.search/multi_search est préparée.

Notifier A680–A682 envoyé et reçu consigné à15:16:07UTC, SHA du message A185865914D305F800A3DFDB538C987DCCE70FCD5C914D4C6CA294FCEA4B897D ; prochaine échéance au plus tard16:16UTC. Le Goal reste actif et le produit TESTE_NON_APPROUVE. Aucun commit/staging ni changement de runtime Qwen. L'adresse/configuration du serveur documentaire reste demandée ; les travaux locaux continuent.

## Mise à jour A684–A687 — 8 septembre 2026, 16h18 UTC
A684 conserve la requête propre à chaque hit fusionné. A685 empêche de publier un ancien chunk avec la nouvelle révision si une réindexation intervient pendant la recherche ; le refus reste visible dans les diagnostics. A686 corrige la provenance du canal canonique PostgreSQL : sparse_bm25 au lieu de dense_qdrant. Cette correction peut modifier le représentant choisi par la fusion RRF hybride et donc son score ; les poids sont inchangés.

A687 suit un vrai PDF synthétique jusqu'au vérificateur, au payload des cartes sources et au résolveur du fichier local/page. Ce contrôle a révélé puis corrigé l'aplatissement et la coupure à420caractères du texte canonique. Désormais fullText conserve le chunk complet ; les projections et budgets des prompts restent séparés. La sélection et la rédaction du test sont scriptées, sans Qwen ni ouverture WinUI. Un fichier local modifié est refusé par le hash exact. Rapport détaillé : artifacts/reprise-pc-20260908/a687-pdf-citation-contract/RAPPORT-A687-PDF-CITATIONS-20260908.md.

Validation actuelle :43/43 contrôles client canoniques ciblés ;1771/1771 client hors classes Live ; solution0avertissement0erreur27,84s. Backend global round34 achevé :2042PASS/14FAIL/2056, mêmes14échecs que round28. Nouveau test PDF round37 :1/1 séparément. Aucun runtime PostgreSQL de test ou Qwen actif. Les14échecs restent ouverts et leur qualification reprend ; pas de verdict global vert. Dernier Notifier confirmé15:59:43UTC, prochaine échéance au plus tard16:59UTC. Goal actif, TESTE_NON_APPROUVE, aucun staging/commit.

## Résultat global et rapport Notifier — 16h27 UTC
Round40 achevé :2044PASS/13FAIL/2057,3m54s. Nouveau testPDF réussi dans la suite globale, testqualitéA688 réparé,13échecs antérieurs restent. resource-shutdown confirme PID30812 arrêté,port55432libre,envrestauré,failure=null. summarize-trx.py et record-global-frontier.py exécutés ; current-frontier.json est désormaisround40.
NotifierA687–A688 envoyé à16:27:27.5953160UTC, reçu Notification Telegram envoyee, SHA5F45B1838C19BF5A413EF970E239A417B4DC30F3F469B09558F7394A98D30922. Prochain au plus tard17:27UTC. Rapport/message dansrapports/notifier-a687-a688-20260908.txt. Continuer sans fin de tour.

## État vérifié A689–A694 — 8 septembre 2026, 17h UTC
Round48 est terminé :2054PASS/7FAIL/2061. Round52-full-backend-after-a694 est maintenant la référence globale mesurée :2055PASS/7FAIL/2062, environ4min, fin16:58:55.6983712Z. summarize-trx.py et record-global-frontier.py exécutés. resource-shutdown :PID32408 arrêté,port55432libre,envrestauré,failure=null,testsExit1. Plus aucune session de test en cours. Solution aprèsA694 :0warn/0error,5,81s. Client inchangé depuisA687 :1771/1771 hors classesLive.
A689–A693 corrigent six échecs de fixtures/attentes mécaniques et ajoutent quatre variantes ; aucun changement produit aprèsA687. A694 ajoute un vrai transportHTTP surKestreléphémère avec middlewareauth et endpoints, huit observations de clés/tenant/revocation ;TEI/Qdrantsimulés,Program.cs/worker/configsignée nonexécutés, données synthétiques. Rapport consolidé :artifacts/reprise-pc-20260908/rapports/RAPPORT-CONSOLIDE-A689-A694-20260908.md. Les sept échecs restants restentouverts ; nepasaffaiblir lesattentessémantiques. NativeSourceBackedToolExecutor.PrepareNativeSourceBackedArguments force sourceBackedCanonical=true pourrag.search/multi_search (ligne351),disableAutomaticCategoryScoping=true etincludeResearchSurfaces=true. Cesrouteslegacynepeuventpasêtresuppriméessanspreuveglobale.
DernierNotifier16:27:27UTC, prochain≤17:27UTC. AucunQwenactif. Adresse/configurationserveurattendues ; poursuivrelestravauxlocauxutiles. Goalactif,TESTE_NON_APPROUVE,pasdefindetouràcejalon.

## A696 baseline verticale mesurée et terminée —17h23UTC
Round56-local-vertical-catalog-preflight terminé, session47001 close. Deuxshutdownsvalides :PGPID19092arrêté,QwenPID32344arrêté,ports55432/1234libres,envrestaurés,DLLinchangées. Hôte/clientcollecte1/1 chacun≠approbationsémantique. Catalogue200et2chunkspreflight. Cas1 23,474s=47millimetres[E1],carte/page1/hashfichierexacts ; cas2 21,301s=fauxrefusmalgré2mesures+conditionsprésentes ; cas3 22,463s=refusgénériquesansinvention,couvertureincomplète. RapportA696 écrit.
CauseobservéeC2 :requêtelonguelexicale0hit,puis2cartestitresidentitéscomplètes sanschampEvidence(sourcekindcanonical_content_card,noncitables). BuildDocumentFocusOptions/IsMechanicallyResolvableNavigationLocator n'acceptentpasceslocalisateurs(uniquementnavigation_map) ;promptsuivantGLOBALseuletancresvides. Ensuitebudget4987+(1995+256)=7238>limiteordinaire7200(total12000réserve4800) =>terminalinsuff. Nepasajouter38tokensarbitrairement ; vérifiertransmissiondeslocalisateursetlecturebornéeavantfinalisation toutengardantQwenmaîtredeschoix.
Autredéfautàtraiterséparément :SearchExactMatchesAsync emploieExactMatchEntryIdcommeChunkId,voirepseudoidentitésmetadata ; gardeA685lesécartecanoniquement,préflightsource_identitydégradé. NejamaisréautoriserunmauvaisChunkId. Latence15sparrecherchevientembeddingsabsents ;pasdemesureP520.
Round55harnessincomplet500avantcorrectionSignedConfigStatus,12artefactsexpurgéscléAPIaléatoirejetablereflétéeparerreurDev(audithashes),pasdecléutilisateur. SourcescodeA696finales scelléeschange-audit,versionsinitialesdansinitial-round55.patch. Aucunruntimeactif,pasdecompilationencours. DernierNotifier17:14:29UTC,prochain≤18:14UTC. A697àpréparer :testdéterministeàpartirduHTTPcontentcardsréelC2versbundle+promptlocalisateurs+expansioncontextuelle. AucunecorrectionproduitA697encore. Septéchecsbackendhistoriquesrestentouvertsdernièreglobaleround52=2055/7/2062. ContinuerGoal sansfindetour.

## A698 mesuré —19h01UTC
Round58 terminé, session9126close. PG9172/Qwen29372arrêtés, ports55432/1234libres, environnementrestauré,DLLinchangées. Q1exact47mm/E1/cartepage1,23,970s ;Q2fauxrefus21,970s ;Q3refusgénérique22,890s. Inputnatiffull2845→compact2295, mais4987+2295+256=7538>7200ordinaire. Pasd'améliorationE2E :candidatnonapprouvé. RapportA698etnative-cost-case02.json écrits. Premierchoixrecherche2473+36tokens ;route628+19,1170+117,réparation511+33=4987cumul. Les6tools6115caractères, messages4462→2199. Ne pasajouter338tokensauseuil ni réduirepromptaveuglément. Client1784/1784,ciblé37/37. Dernierbackendglobalround52=2055/7/2062. Pasdetest/runtime/buildactif. DernierTelegram16parties18:50:11UTC,prochain≤19:50UTC.
Lecture indépendante :SearchSourceBackedCanonicalCoreAsync appelle encoreSearchExactMatchesAsync legacy ; celui-ci exposeExactMatchEntryIdcommeChunkId, peut joindreuncontextevoisinparpage etajoutermetadataMatches. A685filtrecesidentitésincohérentes. Il faut établir des résultats canoniques depuislesvraischunkscourants, sansunitéinventée ni retraitdugarde. Sources :RagEndpoints.SourceBackedCanonical.cs141, RagEndpoints.cs10873/11092 ; unitésviaexact_match_entries.unit_id et retrieval_chunks.unit_id/sourceUnitOrdinals. AucunA699créé ouproduitchangéencore. ContinuerGoal.

## A699 candidat et globale en cours —19h13UTC
A699 nouveau canal exactcanonique depuisretrieval_chunks. Round59RED0/4 :bonsrésultatsabsents etdiagnosticidentitéincohérente. NouveauSQLégaliséindexmd5/textenormalisé, lienunitédirectoucompositionSourceUnitOrdinals etprésencelittéraledeexactentrytextdanschunk ;texte/ID/métadonnéesduvraichunkcourant. PasdelegacyprédicatsBuildExactMatchLookupTerms, pasdecontains/metadata/backfill/voisinpage. Scorelittéral1, peutchangerfusion/couverture ;legacyinchangé,gardeA685inchangée. Round60ciblé10/10,builde0warn0error,diffpropre. AuditpréimageCanonical.csidentiquelaptop+2new :testF38514E68A018C496A211D9D871054D1C68D3CCA92B69B5082EFAC101F310D8F ;canalB5430BA4E9502E86EBF27834743E9C69FB518B31EE588B3C30FACB51770EEEFF.
Round61livefin19:12:11UTC PG34276/Qwen14868arrêtés,ports/env/DLLvalidés. Q1correct23,860s ;Q2fauxrefus21,799s ;Q3refusgénérique22,881s. Source_identitydisparaît;denseabsenttoujours15s. A697/A698toujourscandidatsnonapprouvés,aucuneaméliorationQ2. RapportA699écrit.
Round62-full-backend-after-a699 en cours, session89528. NEPASmodifierlessources/compileravantfin. Aprèsfinsummarize-trx.py etrecord-global-frontier.py round62, lirefailures/shutdown. Currentfrontierréférenceround52jusquelà. Clientdernier1784/1784aprèsA698.
ProchainA700àconcevoir, RIENcrééencore :captureC2llm011premierchoixrecherchecontent_claim contient6045chars deconsignesgrille/rôles/nomsinstances/contraintesalimentairesmalgréfaitsmesures. ConstruirecontextefactuelgénériquepilotéuniquementparReadAtomicEvidenceMode(semanticPlan), conserverquestion/mission/observations/focus/pagination/localisateurs/feedback/mémoirerôles, toolsinchangés,budgetsinchangés. Ne pasajusterunesuppressionà338tokens. TesterintégritéducontextesurfixturesnovellesavantcomparaisonE2E. Autrelimitefuture :DocIdcatalogueconnudansintake/requêteinitiale n'estpasfocusbundleaprès0hit ;nonqualifiéeencore. CountEvidenceIds vsfaits3dans2chunkségalementhypothèseouverte.
DernierTelegramrapportcomplet16parties18:50:11UTC,next≤19:50UTC. ContinuerGoal sansclôture.

## A700 rejeté et A701 RED en cours —19h34UTC
A700round63 a fini19:26:41UTC, PG38620/Qwen37260arrêtés,ports/env/DLLvalidés. Q1correct23,908s ;Q2fauxrefus39,085s (baselineA69921,799s), Q3refusgénérique22,777s. Premierchoix1878tokensvs2473maisQwenchoisitstart_document_search querylongue0hit(+15sembeddings),puis1538+76tokenssubmit_research_action verscartes scope/categoryPath/docRefCalibration-record.pdf ;ensuitecumul6007etbudgetfermeavantlecture. REJET A700 :rollback19:29:27UTC hashavant/aprèscontrôlés, ResearchTransition.csrestauréSHA69FDA6535ECDDC112DD9CFEFD407BC5371DF2667A6269F10116D243AE50E07A4 ;nouveauxfichierssourceettestdéplacésdansrejected-sources, pasdansprojetscompilés. Patches/TRX/auditconservés ;NEPASréactivervariante. Défautscontrainte/questionlonguerestentouverts. Étatrestauréclient1784/1784,buildunderlyingréussi,session10023close. RapportA700-REJETécrit. A697/A698candidatsencoreactifsnonapprouvés, A699exactactif backendround62=2059/7/2066.
RapportNotifierA698–A700envoyé19:31:32.6551347UTC,receiptconfirmé,SHAA04B3F46ABC5EAFC14B3D39EF27FB77E25E602989172A4C28807D09A51D3256A, fichierrapports/notifier-a698-a700-20260908.txt. Nexthoraire≤20:31UTC. Legrandrapport16parties avaitétéconfirmé18:50:11UTC. ContinuerGoal sansfinal.
A701 préparé :a701-retrieval-conditions,3préimagesscellées(csproj,ResearchTransition,ResearchTransitionContextRecovery), nouveaufixtureCanonicalDegradedSearchContract.json=responseBody exactround61/case02/backend007(858chars), origine/hashfixture-origin.json. NouveauResearchRetrievalConditionsTests4cas(normal/compact×réeldégradé/mutationsaine), normaliseur→bundle→messagesQwen. ExpectedRED2FAIL2PASS :bundlecontientdense_qdrantmaisprochainsmessagesl'ignorent. Testen cours session60912, compiler/clientx64. AucunproduitA701modifiéencore ;seulementcsprojfixtureettest. Futurhelperdevraitexposerseulementconditionsobservées/querystatus/channels/counts, aucuneinstructionchoisissantquery/outils/pistes. Ne jamaistransformererreurtechniqueenpreuvecitable. PasQwen/PGen cours.
Autrefrontièrepotentielle:SourceBackedIntake.RequestedDocumentResolution esttypécatalogueet soncontratexpliciteinterditd'insérerEvidenceId/EvidenceBundle ;BuildDocumentFocusOptionsnevoitquebundleetGLOBALaprès0hit,bienqueDocIdexactcatalogueconnudansintake. Sitraiterultérieurement, nepasinsérercatalogueenpreuve ;offrirfocus/outilsidentitédistincts. Hypothèsecount3faits/2chunksencoreNONatteintecaraucunelecturecontextuellejusqu'ici. Ne pasprésentercomme causeexécutée.

## Point consolidé de reprise PC — 9 septembre, après A746

Ce point actualise l'état opérationnel des entrées historiques précédentes ; il ne modifie pas leurs observations ni le Goal. La reprise documentaire et le remplacement du dépôt restent terminés. Les accès fournis à l'infrastructure ont permis de vérifier le serveur réel : 305 documents, corpus et révisions observés, sans modification de service serveur ni déploiement des correctifs locaux. Les secrets restent hors du dépôt.

Le journal détaillé des A701 à A746, les préimages, contrôles de hash, tests et sorties natives sont conservés dans artifacts/reprise-pc-20260908 et son checkpoint opérationnel. Les correctifs de transport des preuves, des identités, du routage natif, de la clarification et de la revue indépendante sont testés localement. Trois répétitions réelles ont été qualifiées sur des versions figées distinctes pour VACUUM et l'osmose, le fichier explicitement absent et la comparaison sans noms de documents. Ces succès limités ne qualifient pas le Goal entier ni automatiquement les binaires ultérieurs.

A740 à A743 ont ajouté la revue indépendante au parcours de résumé, clarifié son contrat de phrases, borné sa correction, comptabilisé ses rédactions dans le budget natif, conservé les extraits complets et ajouté une admission du contexte réel. Les derniers essais de résumé restent refusés : erreurs de formulation et erreurs du juge sont conservées. Aucun résultat qualitativement incorrect n'est approuvé.

A744 reste une expérience non intégrée : la nouvelle consigne du juge obtient 19/23 contre 18/23, sans corriger les faux refus de résumés. A745 n'adopte pas Q8 : même Qwen3-4B, mêmes requêtes et mêmes décisions 18/23 que Q5 ; médiane isolée 3187 ms contre 2484 ms. Le Q5 reste la référence, les paramètres de l'application sont inchangés et les deux runtimes d'essai sont arrêtés. Les résultats sont propres à la RTX 4060.

A746 bloque le remplacement silencieux d'un EvidenceId inconnu par celui attendu à la position d'une ligne et conserve les suffixes ordinaires tels que MODE1. Après les RED et corrections documentées, la suite client hors Live compte 1901 réussites sur 1901 ; audit de trois préimages et diff-check valides. Le dernier full backend reste round62, 2059 réussites et sept échecs sur 2066. Les nouveaux correctifs client ne justifient pas de prétendre avoir relancé cette suite backend.

Prochain travail engagé : examiner causalement l'échec backend où le passage de la page voisine devance celui de la page portant une ancre de titre, sans relâcher l'assertion de contenu attendu. Restent aussi les autres échecs backend, les heuristiques de modalité et les replis historiques, les scénarios multi-source et de suivi, la mémoire, les vingt propositions documentées et la validation réelle des cartes/PDF dans WinUI. Le produit demeure TESTE_NON_APPROUVE et le Goal actif.

Le rapport Telegram détaillé en seize parties a été confirmé le 8 septembre. Dernier point périodique confirmé : 9 septembre à 01:08:30.5668621 UTC, « SAAIA — résumés sécurisés et comparaison Q5/Q8 », SHA256 4EA526B990CA8D8196F1EAA8C88CB02701D22B8D9A31FD2F469A9317C725A38D. Le prochain point est dû au plus tard à 02:08 UTC, ou plus tôt si une étape utile le justifie.

## A747 — ancre de titre exacte vers page déclarée — 2026-09-09T06:14:40.9040748Z

Deux rouges PostgreSQL ont isolé le défaut : le corps de page 2 était rejeté parce qu'il ne répétait pas tout le titre, puis un chunk_lead partiel de page 1 gagnait. La route directe désactivée reproduisait le même défaut. Le correctif introduit une provenance interne bornée aux ancres section/content_card dont le titre normalisé égale exactement la phrase demandée, non liées à un chunk, et dont le chunk recouvre la plage de pages déclarée. Les ancres floues, partielles, voisines et liées gardent le contrôle lexical antérieur. Une première suppression trop large a créé puis conservé deux rouges voisins ; elle a été resserrée aux seules routes hors page. Validation finale : unité 1/1, PostgreSQL réel voisin 6/6, build backend 0 warning/0 erreur, diff-check code 0, port 55432 libre. Rapport : artifacts/reprise-pc-20260908/a747-exact-title-page-anchor/RAPPORT-A747-ANCRE-TITRE-PAGE-20260909.md. La globale reste round62=2059/7/2066 ; front candidat six, à confirmer globalement. Les quatre fichiers .env de l'infra NextCloud sont inventoriés sans valeurs dans infra-env-manifest-no-values.json ; accès serveur encore à valider en lecture. Goal actif, produit TESTE_NON_APPROUVE, aucun commit/staging/déploiement.

## A748/A749 — inventaire serveur et recherche exacte publique — 2026-09-09T06:44:19.6429108Z

A748 lecture seule : les quatre fichiers de configuration NextCloud ont été inventoriés par noms de clés, sans valeur copiée dans les artefacts. Connexion SSH validée sans écrire le mot de passe dans une commande ou un fichier. Serveur Ubuntu 24.04, déploiement sous `/opt/saaia` hors Git, backend `/health` HTTP 200 et conteneurs documentaires actifs. L'assembly backend déployé diffère du local : la mise à jour devra suivre le workflow d'image/artefact et non `git pull`. Aucun fichier, service ou conteneur serveur modifié. Preuve expurgée : `a748-server-readonly-inventory/server-inventory-no-secrets.json`.

A749 : le test renforcé prouve que le canal exact canonique retrouve déjà le vrai chunk courant IND570, mais `SearchCoreAsync` appelait encore le canal legacy puis, après branchement, ne court-circuitait pas sur ce vrai chunk et laissait gagner le profil. La phase publique appelle désormais `SearchSourceBackedCanonicalExactMatchesAsync`. `ShouldShortCircuitAfterExact` accepte un chunk source courant avec GUID, index valide, texte non vide et hors profil ; gardes navigation, relation interdocuments, score et ambiguïté inchangées. RED initial fuzzy, RED intermédiaire profile, puis unités 6/6, PostgreSQL public 1/1 et voisinage canonique 5/5 ; build 0 warning/0 erreur, diff-check bon, PG arrêté, port 55432 libre, env restauré, aucun secret temporaire. Rapport `a749-legacy-search-canonical-exact/RAPPORT-A749-RECHERCHE-EXACTE-CANONIQUE-20260909.md`, SHA256 `79ED59FF46201DF43E4D9A2666543A341F9656EFC0DAFDAA46582AC358DDD04D`.

La globale reste round62=2059/7/2066. A747+A749 validés ciblés donnent un front candidat de cinq rouges, non encore confirmé globalement. Suite : corriger ces attentes sémantiques sans les affaiblir, puis relancer la globale. Client 1901/1901. Produit TESTE_NON_APPROUVE, Goal actif, aucun commit/staging/déploiement.

Notifier A748-A749 confirmé 2026-09-09T06:45:42.3847963Z, reçu exact `Notification Telegram envoyee.`, message `rapports/notifier-a748-a749-20260909.txt` SHA256 `9AECF1F80EE142E4F571DA79F576569329994AFC25AF15F6938FB82A3F3DCE16`. Prochaine échéance au plus tard 2026-09-09T07:45:42.3847963Z.

## A750 — provenance du profil LLM conservée — 2026-09-09T07:12:10Z

Le profil direct de `Generic/LlmProfileRecall.pdf` était correct, mais la recherche publique sautait le canal profil pour une requête courte de titre et publiait un chunk sparse dont seul l'index enrichi portait `Pressure envelope validation`. Le correctif détecte uniquement la correspondance exacte multi-termes `Matched profile title` absente du texte source, recharge la fiche du même document, puis remplace le chunk faible seulement si cette fiche existe. Une page portant réellement le titre exact reste source prioritaire. Temps de récupération comptabilisé dans ProfileMs et phase diagnostique dédiée.

RED public1/1, GREEN ciblé1/1, famille PostgreSQL profils8/8, voisinage unitaire32/32, build0warning/0erreur, diff-check0. Sortie scellée : un résultat document_profile, page1, llm_backoffice_v1, langueen, carte exacte. PG arrêté, port55432libre, envrestauré, bootstrap password supprimé. Rapport `a750-profile-candidate-retention/RAPPORT-A750-RETENTION-CANDIDAT-PROFIL-20260909.md`, SHA256 `6ED0D40ACE4765FD62169DB94A573B8538BC08D8B9600F24CFF1994E2F6CE105`. Globale toujours round62=2059/7/2066 ; A747+A749+A750 verts ciblés donnent quatre rouges candidats restants. Goal actif, TESTE_NON_APPROUVE, aucun commit/staging/déploiement. Prochain : instruire les quatre rouges restants puis globale.

## A751/A752 — clarification placeholder et sélection documentaire croisée — 2026-09-09T07:40:00Z

A751 rend la clarification `missing_standard_identifier` accessible avant le retour sans source : `Le projet respecte-t-il la norme xxx ?` demande maintenant l'identifiant exact même avec retrieval vide. RED unitaire 0/1, famille guidance 15/15, PostgreSQL ciblé 1/1, build et diff-check verts. Rapport `artifacts/reprise-pc-20260908/a751-placeholder-standard-guidance/RAPPORT-A751-CLARIFICATION-NORME-PLACEHOLDER-20260909.md`, SHA256 `A7E9338169EE21E1A2CAB25E86B694F3D13388F24D14AC748FC930230CF34608`.

A752 corrige deux classifications successives de la demande `Quel document faut il citer ... inerting ... integration plc ?`. L'intention existante de sélection documentaire empêche la sonde de titre non cité de court-circuiter la recherche, puis empêche l'élagage de titre précis de supprimer les deux candidats retrouvés. RED unitaires 0/1 + 0/1 et reproductions PostgreSQL rouges conservées ; validation finale 11/11 unités, 2/2 PostgreSQL cible et voisin, build 0 avertissement/0 erreur, diff-check0. Sortie : EN 15281 et IND570, guidance `answer`. Diagnostics temporaires retirés ; test d'intégration inchangé octet pour octet. Rapport `artifacts/reprise-pc-20260908/a752-cross-document-guidance/RAPPORT-A752-SELECTION-DOCUMENTAIRE-CROISEE-20260909.md`, SHA256 `076D84C4191F9EAAA873135D69A7053F1C1961FAEC817472F682845E24CC0365`.

Hashes après A752 : RagEndpoints `06F848C60E5A6AD118FFD2E58C3C0EF6D65C85D8032D44078D8FCAD667A24DDE`, intégration `9A3AA02E3A873E4FC10C1CB00E61B39C548EF87975D05543053BBE546E4667CB`, unités `15954C93EB5EE222A3CBD83A639E5380A51140D4EBDE0514DBEAC4E1ED6CE86B`. PG arrêté, port55432 libre, environnement restauré. La globale de référence reste round62=2059/7/2066 jusqu'à remesure ; front exact non présumé. Produit TESTE_NON_APPROUVE, Goal actif, aucun commit/staging/déploiement.

Notifier confirmé 2026-09-09T07:33:32.4978533Z, titre `SAAIA — trois défauts RAG réparés`, message `artifacts/reprise-pc-20260908/rapports/notifier-a750-a752-20260909.txt`, SHA256 `46FD2FEA391FF0ABCD6F2B03F95379B9C95F0D361B265724DF7CE8D87C77B016`, reçu exact `Notification Telegram envoyee.`. Prochaine échéance au plus tard 2026-09-09T08:33:32.4978533Z.

## A753 — sujet de requête et sources valides conservés — 2026-09-09T08:26:32Z

A753 corrige trois défauts de présélection : fragments de méta-consigne pris pour le sujet, questions métier avec `ce document` supprimées comme ambiguës, et référence sans extension `EN 15281` non reconnue dans le contenu d'un document nommé `CEN TR 15281`. Les fichiers explicitement nommés avec extension restent contraints au nom. RED1/9,1/3,0/1 ; final33/33unités et3/3PostgreSQL, build0/0,diff-check0,ressources restaurées. La banqueV5 diagnostique désormais49/60cas non conformes et14vides, contre52/60 et21vides avantA753. Le prochain lot doit traiter l'expansion lexicale techniqueFR/EN de façon bornée avant de modifier classement, responseShape ou guidance. Rapport `artifacts/reprise-pc-20260908/a753-runtime-bank-guidance/RAPPORT-A753-REQUETE-ET-CONSERVATION-SOURCES-20260909.md`, SHA256 `C91E3506B891FEA2A3731FFFF01D514F961B3FC6A2A390EE49115E886496DB6A`. ProduitTESTE_NON_APPROUVE,Goalactif,globaleinchangéeround62,aucundéploiement.

Notifier A753 confirmé 2026-09-09T08:30:44.7619410Z, message `artifacts/reprise-pc-20260908/rapports/notifier-a753-20260909.txt`,2853caractères,SHA256 `4A444779EFEAAB7E36925FE75550FA974F7AD470554AE26FC096A5D0483D523A`, reçu exact observé sur sortie hôte. Prochain point avant09:30:44UTC ou jalon utile.

## A754 interrompu, A755 ouvre la qualification locale/avancée — 2026-09-09

Maxime a demandé de suspendre les tests puis a autorisé la nouvelle direction :
mesurer d'abord les limites réelles du petit modèle sur la machine cliente,
l'améliorer et l'éprouver dans une enveloppe qualité/temps cohérente, puis
concevoir une capacité serveur pour les demandes complexes. Une API ou une
machine GPU louée pourra ultérieurement qualifier cette capacité avancée sur le
planning 5 × 4 et d'autres demandes complexes.

Le Goal actif contient maintenant un amendement prioritaire à deux capacités.
Le local doit répondre, clarifier, déclarer une insuffisance ou demander une
analyse avancée selon une frontière mesurée. Le planning 5 × 4 n'est plus une
obligation de réussite du petit modèle ; il reste obligatoire comme test de
dépassement local et comme test de réussite du futur moteur avancé. Les deux
capacités partagent retrieval, outils, EvidenceBundle, writer, vérificateur et
cartes WinUI.

A754 est **INTERROMPU AVANT RED ET AVANT CODE PRODUIT**. Seuls ses préimages et
son protocole existaient ; aucun test A754 n'a été exécuté. Son expansion
lexicale ne reprendra que si la nouvelle matrice causale démontre un défaut du
retrieval commun. Aucun changement produit A754 n'est à restaurer.

A755 est la phase active. Sa décision et son protocole sont scellés dans
`artifacts/reprise-pc-20260908/a755-client-model-capability-boundary/`. La
qualification sépare obligatoirement trois voies : retrieval seul, petit modèle
avec EvidenceBundle oracle, puis end-to-end réel. Elle annote la charge par faits,
preuves, documents, navigation, comparaisons, contraintes et taille du livrable.
Les seuils de routage ne seront dérivés qu'après mesure et ne pourront dépendre
d'un domaine, d'un document, d'une langue ou d'un cas connu.

Ordre actif : sceller et auditer l'amendement ; inventorier les cas/oracles déjà
existants sans les dupliquer ; produire la matrice de charge hors Qwen ; qualifier
la voie retrieval ; préenregistrer les voies oracle et end-to-end ; comparer les
petites variantes matériellement admissibles jusqu'au plateau ; figer la matrice
de capacités et le contrat `local_answer` / `clarify` /
`insufficient_evidence` / `advanced_analysis_required` ; valider WinUI ; préparer
ensuite seulement la campagne API/serveur. Toute dépense ou transmission à un
tiers sera présentée avec coût et données concernées avant exécution.

État au changement de phase : client 1901/1901 ; référence backend globale
round62 2059/7/2066 ; correctifs A747–A753 validés ciblés mais non requalifiés
globalement ; banque runtime V5 11/60 conforme et 14/60 sans source après A753 ;
produit `TESTE_NON_APPROUVE` ; aucun commit, staging ou déploiement.

## A755–A763 — frontière locale qualifiée et architecture avancée prête pour la validation réelle — 2026-09-11

La reprise documentaire est complète dans le registre : 43/43 documents et
15 847 677/15 847 677 caractères effectivement couverts. A755 qualifie la
frontière locale sur la RTX 4060, Qwen3-4B Instruct Q5_K_M et llama.cpp CUDA
b10098. La banque adversariale connue donne 42/42 résultats acceptés sur trois
répétitions, quatorze routes stables et toutes les portes de latence respectées.
La preuve n'est pas un holdout aveugle ; l'ancien holdout contaminé reste exclu.

Les lots A756 à A762 rendent exécutable le terminal
`advanced_analysis_required` : handoff typé, job PostgreSQL idempotent soumis à
la licence, worker durable, tools SAAIA partagés, revalidation des preuves,
trace bornée, transport client, cartes source strictes et reprise du même job
après redémarrage WinUI. La reprise A762 est confirmée par 4/4 tests ciblés et
reste à observer sur un vrai processus WinUI pendant un vrai job long.

Les commits `b30096ac` et `9101e5f5` ajoutent le contrat fournisseur et raccordent
le parcours produit au grand modèle backend. Les profils `openai-dev`,
`runpod-bench` et `customer-server` utilisent le même provider, le même cycle
planner/tools/writer, le même EvidenceBundle, le même validateur et les mêmes
métriques. Le budget Terra persiste avec 25 USD autorisés, une alerte à 20 USD,
un arrêt à 24 USD, 0,50 USD et quatre appels maximum par job.
Les tarifs officiels revérifiés restent 2 USD/M en entrée, 0,20 USD/M en cache
et 12 USD/M en sortie. Le scénario 7 000 tokens d'entrée et 1 000 de sortie
coûte 0,026 USD par appel, soit environ 961 appels et non 961 jobs garantis,
puisqu'un job réussi comporte normalement une planification et une rédaction.

Le durcissement suivant sépare dans la configuration signée `Provider` et
`LlmLocation`. OpenAI et RunPod sont `external-service`; le serveur client est
`internal`. Les autorisations de transmission dépendent de cette localisation
et toute incohérence est rejetée avant HTTP. Cette décision aligne le lot sur la
vision future où la licence encode les capacités, l'installation choisit une
topologie autorisée et le profil technique fournit URL, secret, modèle et
dimensionnement. Les assistants d'installation et le format commercial final
restent hors du lot actuel.

Validation mécanique courante : fournisseur 12/12, suite solution 4 360/4 360
avec deux sondes live opt-in ignorées, build 0 avertissement/0 erreur, syntaxes
PowerShell et Bash valides, 36/36 placeholders de production couverts et JSON
rendu valide. Les trois profils produisent chacun une configuration signée
cohérente et le cas incohérent est rejeté. Aucun appel externe, aucune dépense
et aucune transmission de corpus n'ont eu lieu.

A763 est donc **PRÊT AU NIVEAU PROTOCOLE ET ARCHITECTURE, NON EXÉCUTÉ AVEC UN
FOURNISSEUR RÉEL**. Il manque la sonde Terra, le planning 5 × 4 via le parcours
produit, trois réussites live consécutives sur état gelé, la banque avancée, la
comparaison RunPod, le modèle final sur serveur client, le test WinUI réel et le
nouveau holdout aveugle. OpenAI affiche actuellement zéro crédit ; l'achat des
25 USD autorisés et la création de la clé restreinte attendent la confirmation
immédiate exigée par l'outil. Le produit reste `TESTE_NON_APPROUVE` et le Goal
reste actif.

Audit consolidé : `AUDIT-GOAL-ACTIF-A755-A763-2026-09-11.md`. Prochaine action
après confirmation : achat et clé, sonde synthétique sans donnée privée, puis un
seul essai produit du planning avant toute répétition payante.

## A763 — exécution OpenAI réelle, corrections causales et gel mécanique — 2026-09-11

L'achat de 25 USD et la création de la clé restreinte SAAIA sont terminés. La
clé est stockée sous DPAPI hors Git. La sonde synthétique et le parcours produit
complet local → backend → OpenAI ont été exécutés. Le journal applicatif totalise
0,89830376 USD ; l'interface OpenAI affiche 0,92 USD consommé et 24,09 USD de
solde. Le compte reste toutefois classé `Free tier` avec 50 requêtes par jour et
par modèle malgré le seuil Tier 1 annoncé à 5 USD. Aucun achat supplémentaire
n'a été effectué.

Terra a effectivement produit des plannings de vingt cellules, cinq repas
sourcés, une comparaison CEN/IEC et sept points NIST. Ces succès ne constituent
pas encore trois passages consécutifs de toute la banque sur le dernier état.
Les échecs intermédiaires ont isolé des requêtes externes ou trop littérales, des
documents voisins, une fenêtre de preuves mal ordonnée, des insuffisances trop
vagues et des sorties structurées invalides.

Le correctif courant reste généraliste : requêtes privées assainies, résolution
univoque des documents explicitement nommés, recherches planifiées rattachées à
leur document, filtre du jeu documentaire, reclassement des preuves par sujet,
équilibrage entre recherches, fenêtre de 14 000 caractères, contrôle du nombre
d'unités et une réparation de protocole bornée. Une citation vers un EvidenceId
non revalidé reste irréparable et provoque un échec. La trace durable conserve
maintenant le `docPath` réellement résolu.

Deux exécutions Luna consécutives de la comparaison CEN/IEC ont ensuite répondu
complètement avec la page IEC 238. Une troisième a échoué proprement sur le JSON
du rédacteur ; sa classe de défaut est maintenant couverte mécaniquement, sans
rejeu live faute de quota. Luna reste insuffisant pour le planning vingt cellules
et n'est pas le candidat principal de ce stress-test.

État de vérification après gel : 44/44 tests ciblés fournisseur/worker, 1/1
résolution live du catalogue, 4 388 tests Debug réussis avec deux sondes live
ignorées, build Release zéro avertissement/zéro erreur, quatre scripts
PowerShell valides, diff-check et scan de secrets propres, ports 5123/1234
libres. Le rapport complet Telegram demandé a été envoyé en cinq parties et
confirmé par le notifier.

Le produit reste `TESTE_NON_APPROUVE`. Suite irréductible : attendre Tier 1 ou
la réinitialisation du quota, rejouer la banque Terra trois fois sans changer les
critères, qualifier le même contrat sur RunPod après fourniture d'un endpoint et
d'un budget, exécuter la reprise dans le vrai WinUI, puis ouvrir un holdout
aveugle après gel définitif. Audit détaillé :
`AUDIT-GOAL-ACTIF-A755-A763-2026-09-11.md`.

## A763 — garde-budget multi-provider et préflight RunPod — 2026-09-11

L'interface OpenAI confirme une anomalie de palier : la facture de 25 USD de
crédits API est payée, le solde est actif et des appels sont facturés, mais
l'organisation reste au `Free tier` à 50 RPD alors que le seuil Tier 1 affiché
est de 5 USD d'achats cumulés. Le bouton `Upgrade tier` ouvre uniquement un
nouvel achat. Un dossier de support sans secret et un message prêt à transmettre
sont conservés dans les artefacts locaux ; aucun nouvel achat et aucun message
externe n'ont été effectués.

La préparation RunPod est maintenant concrète. Le premier candidat est le Public
Endpoint `Qwen/Qwen3-32B-AWQ`, fenêtre 32 768 tokens, contrat OpenAI-compatible et
tarif officiel de 10 USD par million de tokens. Les 39 jobs multi-appels déjà
mesurés donnent une projection de 1,22 USD pour douze jobs au volume médian et
1,41 USD au percentile 90. Une autorisation locale de 3 USD avec arrêt à
2,88 USD est suffisante même si l'achat minimal de crédits RunPod est de 5 USD.

Le garde-budget persistant couvre désormais tous les fournisseurs externes. Il
utilise un registre et des tarifs propres au provider et le lanceur RunPod refuse
toute exécution sans budget explicite. Les lanceurs OpenAI et RunPod appellent le
même parcours produit générique. Validation : 55/55 tests ciblés, 4 388 tests
Debug réussis, deux sondes live ignorées, build Release sans avertissement ni
erreur, scripts PowerShell valides et rejet du RunPod sans budget vérifié. Aucun
appel RunPod payant n'a été exécuté.

## A763 — plafond Terra prouvé, support escaladé et planning courant réussi — 2026-09-11

Une réponse 429 fraîche confirme sans ambiguïté la limite d'organisation : type
`requests`, code `rate_limit_exceeded`, 50 RPD utilisés sur 50, zéro restant.
La clé SAAIA active appartient au `Default project` de l'organisation qui porte
les crédits et l'application ne fixe aucun en-tête `OpenAI-Organization` ou
`OpenAI-Project`. Le problème ne vient donc ni du budget SAAIA, ni des crédits,
ni d'un mauvais projet. Le ticket authentifié contient l'identifiant de requête
et les en-têtes de limite demandés. Après confirmation du plafond par le support
automatique, une escalade vers un agent humain a été demandée : la page Limits
reste au `Free tier` et son seul bouton d'upgrade ouvre un nouvel achat de
crédits.

Le dernier cas complexe encore exécutable avec le quota Terra a réussi via le
parcours produit courant : planning français de cinq jours et quatre repas,
vingt propositions distinctes, vingt claims, neuf sources, deux appels,
36 503 ms et 0,043438 USD. Le journal SAAIA totalise désormais 0,94174176 USD.
Cette unique répétition reste insuffisante pour le verdict trois sur trois.

Un premier lancement s'était arrêté avant HTTP : la casse insensible de
PowerShell confondait le paramètre `$BaseUrl` du fournisseur et `$baseUrl` du
backend local. Le contrôle HTTPS a évité toute transmission erronée et toute
consommation de quota. Le paramètre interne est renommé `$ProviderBaseUrl`, son
alias public est conservé et la façade RunPod a été alignée. Analyse syntaxique,
contrôle de collision et `git diff --check` sont verts.

La suite dépend maintenant de la correction Tier 1 ou du reset du bucket Terra
pour terminer la banque gelée trois fois. RunPod reste prêt mais aucun crédit ni
appel n'est engagé sans autorisation de dépense propre. Le serveur client, le
vrai redémarrage WinUI et le nouveau holdout aveugle restent nécessaires avant
l'approbation. Produit `TESTE_NON_APPROUVE`, Goal actif.

## A763 — limite fournisseur rendue explicite dans WinUI — 2026-09-11

Le code 429 du fournisseur était déjà conservé dans le job durable, mais le texte
utilisateur restait générique. WinUI reconnaît désormais tout échec avancé
`*_http_429`, explique que la limite de requêtes est temporairement atteinte et
propose le reset du quota ou un autre fournisseur autorisé. Le comportement est
général à OpenAI, RunPod et au futur serveur client. Le contenu rejeté reste
invisible et aucune carte source non validée n'est créée.

Validation après ce changement : transport avancé 21/21, client complet
2 234 réussis avec une sonde live ignorée, solution complète 4 389 réussis avec
deux sondes live ignorées, build Release zéro avertissement/zéro erreur. Aucun
appel externe et aucune dépense n'ont été nécessaires. Le statut produit reste
`TESTE_NON_APPROUVE` jusqu'aux preuves live déjà listées.

## A763 — évaluateur de banque compatible avec la télémétrie avancée — 2026-09-11

Le lecteur mécanique de la banque attendait encore les métriques de l'ancien
appel direct et seulement les références `[E…]`. Le parcours produit conserve le
petit modèle dans les champs principaux et expose le grand modèle dans les champs
`advanced*`, avec des ClaimIds `[C…]`. L'évaluateur sélectionne maintenant la
télémétrie avancée lorsqu'elle existe, tout en gardant la lecture directe
historique, et compte les deux contrats de référence.

Rejeu hors coût sur l'artefact Terra courant : fournisseur `openai-dev`, modèle
`gpt-5.6-terra`, deux appels, vingt références, vingt cellules distinctes, zéro
contrôle échoué et verdict `PASS_MECHANICAL_REQUIRES_SEMANTIC_REVIEW`. Ce verdict
ne promeut ni la qualité factuelle ni le produit ; il évite seulement un faux
rejet lors de la future banque trois fois.

Le lanceur commun OpenAI/RunPod appelle maintenant l'évaluateur après la banque
et échoue si la porte mécanique est rouge. La revue sémantique reste séparée et
obligatoire même lorsque cette commande termine avec succès.

## A763 — reprise partielle du quota et preuve d'interruption — 2026-09-12

Le quota Terra a libéré assez de créneaux pour terminer trois cas avancés sur
quatre : planning 5 × 4, cinq repas et comparaison CEN/IEC. NIST a échoué sur
429 après son plan et ses recherches. La répétition entière reste rejetée. Les
sept appels réussis de cette tentative ont porté le registre local à
1,03689176 USD ; la campagne reste très loin de l'arrêt à 24 USD.

Le lanceur de banque accepte désormais un délai configurable entre cas et
répétitions et scelle le commit Git, l'état des fichiers suivis et le délai
choisi. Une tentative sur le commit propre `4bd38c98` avec un délai de 60
secondes a produit un plan à 0,003202 USD, puis deux appels consécutifs refusés
par 429. Elle a été arrêtée afin de ne pas gaspiller les créneaux RPD libérés
progressivement. Le registre local atteint 1,04009376 USD. La cadence résout le
risque RPM, mais ne remplace pas le Tier 1 ni un bucket RPD suffisamment libre.

Les artefacts de shutdown distinguent maintenant `completed: true` d'une
interruption. Si PowerShell reçoit Ctrl-C avant la fin, ils inscrivent un motif
d'interruption même lorsque le bloc `catch` n'est pas exécuté. Le nettoyage a
été vérifié : backend temporaire et Qwen arrêtés, ports 5123/1234 libres,
configuration restaurée et aucun secret persisté.

Prochaine action externe inchangée : attendre un quota permettant vingt-quatre
appels utiles ou l'activation du Tier 1, puis lancer la banque complète 3/3 sur
un commit propre avec le même runner et exécuter la revue sémantique. Les preuves
RunPod et serveur client, le vrai redémarrage WinUI et le holdout aveugle restent
nécessaires. Produit `TESTE_NON_APPROUVE`.

Le commit `4b04aa94` a ensuite passé la validation locale complète en Release :
4 389 tests réussis, aucun échec et deux probes live explicitement ignorées. Les
trois TRX distincts et l'assessment `PASS_MECHANICAL` sont conservés dans
`artifacts/reprise-pc-20260908/a763-provider-comparison/local-validation-4b04aa94-20260912`.

## A763 — cycle réel de reprise WinUI validé — 2026-09-12

Le runner `tools/test-advanced-winui-restart-resume.ps1`, figé au commit
`e7587810`, a lancé deux fois le véritable exécutable WinUI autour d'une
fermeture gracieuse. Les deux lancements ont repris le même job durable et le
même message assistant. Un seul job a été créé; les traces backend montrent les
`GET` de reprise et les deux `PATCH` du même message. Après chaque fermeture, le
job était toujours `queued`, révision 1, sans annulation demandée. Verdict :
`PASS_REAL_WINUI_RESTART_RESUME`.

La preuve isole le cycle client : fournisseur `disabled`, worker désactivé,
zéro appel externe et zéro coût. La session et le job temporaires ont été
nettoyés après observation; configuration, réglages, magasin sécurisé et journal
utilisateur ont été restaurés; aucun processus ou port de test n'est resté
actif. L'artefact complet est
`artifacts/reprise-pc-20260908/a763-winui-restart-resume-e7587810-20260912`.

La porte de reprise réelle est donc fermée. L'inspection visuelle de l'état
terminal et des cartes source exactes sera faite sur une réponse avancée retenue
pendant la banque Terra. Les autres portes ne changent pas : banque Terra 3/3 et
revue sémantique, qualification RunPod autorisée, serveur client, puis nouveau
holdout aveugle après gel sémantique. Produit `TESTE_NON_APPROUVE`.

## A763 — réparation de protocole validée en loopback — 2026-09-12

Le commit `d0a844d5` ajoute une fixture OpenAI-compatible locale et un runner
qui exerce le vrai provider HTTP avec le profil `customer-server` interne. La
séquence imposée contient un plan valide, un writer JSON tronqué, puis une seule
réparation valide. L'exécution sur un worktree propre observe exactement trois
appels, répond avec vingt claims liés à vingt EvidenceIds distincts et produit
le verdict `PASS_BOUNDED_PROTOCOL_REPAIR_LIVE_LOOPBACK`.

Cette preuve ne coûte rien et ne transmet rien à l'extérieur : endpoint
`127.0.0.1`, données synthétiques, variables restaurées, fixture arrêtée et port
18081 libéré. Les 30 tests Release ciblant le provider passent. Le mécanisme de
réparation bornée est donc fermé indépendamment du modèle hébergé; la qualité
Terra reste soumise à la banque 3/3 et à sa revue humaine. Artefact :
`artifacts/reprise-pc-20260908/a763-local-protocol-repair-d0a844d5-20260912`.

## A763 — le vert mécanique du planning est rejeté sémantiquement — 2026-09-12

La relecture des résultats durables et des douze chunks réellement cités ferme
deux cas sur cette exécution : cinq repas étudiant et comparaison CEN/IEC. Le
planning 5 × 4 est en revanche rejeté. Les vingt noms sont documentés, mais leur
placement n'est pas toujours soutenu. Le cas causal est une preuve recommandant
les pancakes au petit-déjeuner alors que la grille les place en collation.

Le prompt Writer autorisait ce défaut en déclarant que l'arrangement des
candidats dans les cellules n'était pas un fait. Le commit `b20fcc2` pose la
règle générique inverse : toute relation ligne/colonne/rôle/catégorie fait partie
du claim et exige une preuve. Un titre ou un index ne peut soutenir qu'une
relation qu'il exprime; sinon le Writer doit déclarer l'insuffisance exacte. Il
n'y a aucune heuristique Cuisine et aucun troisième appel systématique.

Les 30 tests fournisseur et les 4 389 tests Release complets passent, zéro
échec, avec deux probes live opt-in non exécutées. Le coût de cette correction
est nul. Le gel sémantique est déplacé à `b20fcc2`; la banque Terra 3/3 et sa
revue humaine doivent maintenant éprouver cette règle avant toute décision sur
un Critic additionnel. ADR :
`ADR-2026-09-12-A763-RELATION-CELLULE-PREUVE.md`.

## A763 — reprise avancée liée au fournisseur et au modèle — 2026-09-12

Un audit de la reprise durable a identifié que la prise de bail réécrivait
`provider_key`. Après expiration d'un bail, un changement de configuration
pouvait donc poursuivre le même job sur un autre fournisseur ou modèle. Le
commit `3658d9cf` ajoute la migration 067, persiste `provider_model` et lie le
couple lors de la première exécution. Toute incompatibilité ultérieure échoue
avec `provider_configuration_changed`, sans appel du nouveau fournisseur et
sans écraser l'identité initiale.

La preuve PostgreSQL réelle couvre le changement
`openai-dev/terra-v1` vers `customer-server/qwen-v2`, la reprise positive avec
identité inchangée et la revalidation des preuves : 3/3. La validation complète
Release est à 4 391 réussites, zéro échec et deux probes live ignorées. Le
provider HTTP loopback repasse sur le commit exact : plan, Writer tronqué,
réparation unique, vingt claims et vingt EvidenceIds. Les ports temporaires sont
libres et aucun secret n'est présent dans le diff.

Le flux local conserve son streaming normalisé. Le flux avancé durable publie
des états de progression et une réponse atomique après validation ; le streaming
token par token du Writer avancé reste un choix UX ouvert afin de ne jamais
exposer de JSON ou de citations non validés. La matrice d'acceptation complète
est dans `ADR-2026-09-12-A763-AFFINITE-FOURNISSEUR-MODELE.md`.

La prochaine porte sémantique reste la banque Terra complète 3/3 sur le prompt
d'ancrage corrigé, dès que le palier fournisseur le permet. RunPod n'est pas
appelé sans autorisation de dépense dédiée et le produit demeure
`TESTE_NON_APPROUVE`.

## A763 — identité de modèle exacte et plafond OpenAI — 2026-09-12

Le commit `4cbca2d5` rejette avant HTTP tout identifiant de modèle dépassant 256
caractères. Le modèle persisté pour l'affinité est donc exactement celui envoyé
au fournisseur, sans troncature possible. Les 2 147 tests backend passent,
zéro échec, avec une sonde live opt-in ignorée. Le loopback réel du provider
repasse sur ce SHA : trois appels, une réparation bornée, vingt claims et vingt
preuves, aucune sortie externe et ports temporaires libres.

Le contrôle live OpenAI confirme simultanément une incohérence fournisseur :
facture de 27,03 USD TTC marquée payée, solde 23,92 USD, mais organisation encore
en Free tier. La page annonce un Tier 1 automatique à 5 USD d'achats cumulés;
Terra reste pourtant limité à 3 RPM et 50 RPD. `Upgrade tier` propose un nouvel
achat et la limite de dépense de 100 USD est indépendante du RPD. Aucun nouvel
achat n'a été effectué. La banque Terra 3/3 attend donc la correction du palier
ou la réponse du support, tandis que les validations sans dépense continuent.

## A755 — frontière locale rejouée après intégration A763 — 2026-09-12

La banque adversariale locale connue de quatorze cas a été exécutée trois fois
sur le SHA `5516cc1a` avec le Qwen3-4B Q5_K_M et le runtime CUDA qualifiés sur
ce PC. Les trois harnesses passent : 42/42 lignes, zéro erreur, zéro appel
externe, aucun changement serveur, environnement restauré et port 1234 libre.

La revue sémantique accepte 14/14 cas et 42/42 lignes : trois réponses locales
sourcées, deux handoffs après budget, six handoffs avant retrieval, deux
clarifications et une insuffisance documentaire exacte. Aucun fait non soutenu,
aucune substitution de source et aucune source sur les terminaux sans réponse
documentaire. Toutes les médianes respectent les seuils préinscrits, de 4 ms
pour P14 à 22 152 ms pour P02. Assessment SHA-256 :
`F9830737A07B33FA85EEAD87B23AF0254C7E57EEFF6B69F32FEE7861C29C38A8`.

Cette exécution prouve la non-régression de la frontière sur une banque déjà
vue. Elle ne transforme pas ce lot en holdout aveugle et ne valide pas la
qualité Terra ou le produit complet, qui reste `TESTE_NON_APPROUVE`.

## A763 — audit de clôture mécanique multi-provider — 2026-09-12

L'audit ligne par ligne de la mission distingue désormais deux parcours qui
partagent le même RAG. Les probes DEV/BENCH injectent `ILlmProvider` dans le
client afin de comparer Local, OpenAI et RunPod. Le parcours produit garde le
petit modèle local puis crée un job backend durable confié à
`IAdvancedAnalysisProvider`. Cette séparation est documentée dans
`documents/agent/llm-provider-architecture-v1.md` et
`documents/agent/advanced-analysis-server-provider-v1.md` ; elle évite de
confondre une sonde directe avec la preuve du handoff, des citations et de la
reprise durable.

Le commit `4744d813` normalise les timeouts et ruptures réseau survenant aussi
pendant la lecture du corps HTTP avancé. L'annulation demandée par l'utilisateur
reste distincte. Les 33 tests provider couvrent le timeout après en-têtes, la
rupture pendant le corps et l'annulation appelant. La suite backend complète
compte alors 2 150 réussites, zéro échec et une probe live ignorée.

Le commit `09207d68` ajoute une ligne de télémétrie par appel externe : identités
requête/trace/job, durée, tentatives et retries, tokens, coût et erreur typée.
Le registre exclut prompt, EvidenceBundle, endpoint et secret. Un scénario
429 -> retry -> succès vérifie les compteurs et les identifiants sans appel
externe. Le TTFT avancé reste nul tant que le Writer produit un JSON atomique
validé avant publication.

Le commit `76140189` rend les erreurs `advanced_llm_timeout` et
`advanced_llm_transport_error` actionnables dans les six langues du client. Un
échec ne publie ni payload fournisseur non validé ni carte source. Sur ce SHA,
la validation Release totalise 4 397 réussites, zéro échec et deux probes live
ignorées. L'assessment est conservé sous
`artifacts/reprise-pc-20260908/a763-client-provider-failure-ux-7614018-20260912`.

Le commit `107fffe4` retire les valeurs RunPod implicites du lanceur produit.
Une campagne doit fournir endpoint, modèle, budget et tarifs ; son preflight
peut sceller runtime, profil, GPU, quantification, hash du modèle, contexte et
coût horaire. Les scripts passent l'analyseur PowerShell et aucun appel ou achat
RunPod n'a été effectué.

Enfin, `690d7d14` supprime le dernier constructeur public de `RagChatAgent` qui
acceptait directement `OpenAiLlmClient` avec une identité Local/llama.cpp
implicite. L'unique constructeur public exige `ILlmProvider`; les probes live
passent par la factory avec endpoint, modèle et profil explicites. Un test par
réflexion verrouille cette frontière. Sur le SHA exact, les 20 scénarios
d'architecture et les 2 238 tests client passent, zéro échec, une probe live
ignorée. Assessment :
`artifacts/reprise-pc-20260908/a763-explicit-client-provider-690d7d1-20260912/assessment.v1.json`,
SHA-256 `47A293EBABE2BA90374014EEFE86EA8770B1408B93550A797FB61CADF47F9ADA`.

Ces fermetures sont mécaniques et sans dépense. Les portes sémantiques restent
la banque Terra trois fois sur le descendant de `b20fcc2`, les trois plannings
acceptés avec relation cellule/preuve, l'inspection terminale WinUI et des
cartes sources, puis un nouveau holdout aveugle. RunPod attend une autorisation
de dépense et un candidat concret. Le produit reste `TESTE_NON_APPROUVE`.

Un dernier contrôle de l'identité avancée a ensuite trouvé que `ProviderKey`
était tronquée à 100 caractères tandis que `ModelId` était déjà rejeté au-delà
de sa capacité. `d960cdb8` supprime cette troncature : l'identité exacte reste
observable et toute clé surdimensionnée échoue avant HTTP. Les 50 tests ciblés
provider/worker et les 2 151 tests backend passent, zéro échec, une probe live
ignorée, sans appel externe. Assessment :
`artifacts/reprise-pc-20260908/a763-exact-provider-identity-d960cdb-20260912/assessment.v1.json`,
SHA-256 `1B7CEAD5AD73B001C784182361A5A2D1DF37CB97E3EDD79B6289E74EB24AE833`.

## A763 — sélection documentée du premier candidat RunPod — 2026-09-12

La voie RunPod est maintenant concrète sans engager de dépense. Le premier
candidat est l'endpoint public OpenAI-compatible Qwen3 32B AWQ, facturé 10 USD
par million de tokens. La campagne proposée conserve les quatre cas et trois
répétitions, avec une autorisation totale de 5 USD, une alerte à 4 USD, un arrêt
à 4,80 USD et un plafond de 0,40 USD par job.

Ce choix sert à mesurer la qualité au coût minimal sans créer un worker GPU.
Il ne peut pas prouver le GPU, le hash des poids ou la révision du runtime que
l'endpoint public ne publie pas. Un endpoint Serverless privé et le candidat
Qwen3 30B A3B Instruct 2507 restent conditionnels à un échec sémantique ou au
besoin d'une infrastructure entièrement scellée.

Le profil, le calcul de coût, l'invocation préparée, les sources et les portes
de confidentialité sont dans
`documents/agent/runpod-benchmark-candidates-a763-2026-09-12.md`. Aucun compte,
crédit, endpoint, pod, clé ou appel RunPod n'a été créé. La prochaine action
RunPod sera une demande d'autorisation explicite portant sur la transmission
des extraits de preuve et un maximum de 5 USD. Terra, l'inspection WinUI, le
holdout aveugle et la preuve serveur client restent ouverts. Le lanceur RunPod
expose et transmet désormais le délai inter-cas déjà supporté par le moteur
commun, afin que la cadence puisse être scellée sans modifier le harnais. Produit
`TESTE_NON_APPROUVE`.

## A763 — identité du modèle observée dans la télémétrie — 2026-09-12

Le commit `a0202c8` conserve désormais, pour chaque succès du provider avancé,
le modèle demandé dans `modelId` et le champ `model` annoncé par la réponse dans
`observedModelId`. Une valeur observée absente ou invalide reste nulle ; elle ne
remplace jamais l'identité demandée et ne casse pas les alias résolus vers un
snapshot. Le test RunPod simulé prouve que les deux appels journalisent
`Qwen/Qwen3-32B-AWQ` comme identité observée.

La validation backend Release sur ce SHA exact compte 2 151 réussites, zéro
échec et une probe live ignorée. Aucun appel externe ni coût n'a été engagé.
Assessment :
`artifacts/reprise-pc-20260908/a763-observed-model-a0202c8-20260912/assessment.v1.json`,
SHA-256 `2378E7BB55EECA1F2F89BE9C55D13D62922DE022048454736F32AA0AF4DF9DDD`.
