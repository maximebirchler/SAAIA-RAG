# ADR - RAG LLM Orchestration Source-Backed

Date: 2026-07-08
Status: Source de reference projet pour la discussion RAG/orchestration
Scope: SAAIA client RAG, ToolAgent, source-backed answers, memory, retrieval planning, final UI validation

## But

Ce document fixe la direction d'architecture a suivre pour sortir d'un pipeline RAG fragile, trop heuristique et trop specialise, tout en gardant les garanties source-backed indispensables.

La finalite n'est pas seulement de reussir un exemple de plan de repas. La finalite est un pipeline generaliste, stable et professionnel, capable de traiter plusieurs categories de questions et de produire des reponses fiables avec sources verifiables depuis l'UI.

## Decision principale

Le LLM doit etre l'orchestrateur et le decideur semantique final.

Le code ne doit pas decider semantiquement si une source "repond" a la question utilisateur, sauf pour des cas mecaniques evidents:

- source absente;
- source inventee;
- doublon exact ou source/page a fusionner;
- outil invalide ou JSON invalide;
- resultat hors tool result;
- injection ou contenu unsafe;
- absence totale de preuve exploitable.

Le code doit fournir:

- des outils documentes;
- des contrats types;
- des budgets;
- des traces;
- des validations mecaniques;
- une boucle de reparation LLM si le contrat source-backed echoue.

## Pourquoi cette decision

Les audits locaux convergent: le pipeline actuel contient des morceaux LLM utiles, mais le code reprend souvent le pouvoir avant ou apres le LLM. Cela peut mener a des pertes de candidats, a des reponses fallback et a des reconstructions deterministes.

Le symptome important observe dans le live est la perte entre inventaire RAG brut et writer/final gate: des candidats existent au niveau raw, mais ne survivent pas forcement jusqu'au writer ou aux sources finales.

La bonne correction n'est pas de coder plus d'heuristiques metier. La bonne correction est de rendre le chemin de preuve explicite et impossible a perdre.

## Principes non negociables

1. La memoire n'est pas une preuve.
   - La memoire aide a chercher, cadrer et eviter de repeter des erreurs.
   - La preuve finale vient des sources actuelles: pages, chunks, resultats RAG, documents, revisions.

2. Le LLM decide du sens.
   - Il choisit la strategie de recherche.
   - Il juge si les preuves repondent a la demande.
   - Il decide de pivoter, elargir, repondre partiellement, demander clarification ou refuser.

3. Le code verifie le contrat.
   - Le code ne remplace pas le writer par une reponse metier sauf echec repete et explicite.
   - Le code verifie que les affirmations finales concretes sont liees a des preuves visibles.

4. Un seul objet canonique porte les preuves.
   - Les resultats RAG, les candidates, les sources writer et les sources UI doivent deriver du meme EvidenceBundle.
   - Aucune transformation ne doit supprimer silencieusement docId, page, sourceHash, chunkId, content cards ou quality flags.

5. Le pipeline doit etre replayable.
   - Les decisions, tool calls, candidats acceptes/rejetes, erreurs de contrat et repairs doivent etre traces.

## Architecture cible

### 1. Intake

Le LLM reformule la demande et produit un objet structure:

- objectif utilisateur;
- type de tache;
- niveau de sources attendu;
- contraintes explicites;
- slots ou axes a couvrir;
- besoin de clarification ou non;
- tolerance a une reponse partielle.

### 2. Working Memory

Le systeme assemble une memoire courte utile a la demande:

- preferences utilisateur;
- decisions projet importantes;
- echecs connus;
- vocabulaire utile;
- contexte conversationnel.

Cette memoire doit etre annotee comme contexte, jamais comme preuve finale.

### 3. Tool Capability Registry

Le LLM recoit une description compacte et actuelle des outils disponibles:

- nom de l'outil;
- usage attendu;
- schema d'entree;
- schema de sortie;
- limites;
- erreurs possibles;
- exemples d'usage;
- difference avec les outils proches.

Ce registre doit venir du code ou de contrats maintenus, pas d'un prompt historique qui derive.

### 4. Retrieval Planner LLM

Le LLM choisit les recherches:

- requete directe;
- requete reformulee;
- synonymes;
- recherche large;
- recherche par categorie;
- lecture de sommaire;
- lecture de pages;
- recherche multi-axes;
- pivot si les resultats sont faibles.

Le code execute, trace et applique les budgets. Il ne bloque pas une recherche parce qu'elle "semble peu pertinente" si elle est valide et safe.

### 5. EvidenceBundle

Tous les resultats deviennent des EvidenceItems canoniques.

Champs minimum recommandes:

- evidenceId;
- sourceKind;
- toolName;
- queryUsed;
- docId;
- docName;
- sourceHash;
- revisionId ou indexedVersion;
- pageStart;
- pageEnd;
- chunkId;
- excerpt;
- normalizedExcerpt;
- score;
- rank;
- categoryPath;
- docLanguage;
- profileLanguage;
- extractionQuality;
- matchedContentCards;
- selectionHints;
- codeHints;
- riskFlags;
- lineage.

Les codeHints/riskFlags servent a informer le LLM, pas a supprimer semantiquement le candidat.

### 6. Evidence Judge LLM

Le LLM examine l'inventaire large:

- preuves directes;
- preuves de contexte;
- navigation/sommaire;
- doublons;
- bruit;
- preuves faibles mais utiles;
- lacunes restantes.

Il decide:

- utiliser;
- ignorer;
- chercher encore;
- lire une page;
- elargir;
- reduire;
- repondre partiellement;
- refuser faute de preuve.

### 7. Iteration Controller

Le code gere:

- nombre maximum de tours;
- budget tokens;
- budget temps;
- detection de non-progres mecanique;
- checkpoint final.

Il ne juge pas la pertinence finale des sources. Il demande au LLM d'expliciter son etat: assez de preuves, preuves insuffisantes, nouvelle recherche utile, ou reponse partielle.

### 8. Writer LLM

Le writer recoit uniquement:

- la demande utilisateur;
- la decision de scope;
- l'EvidenceBundle compact;
- les decisions de l'Evidence Judge;
- les contraintes de citation.

Chaque affirmation concrete doit pointer vers un ou plusieurs evidenceId.

### 9. Source Contract Verifier

Le code verifie mecaniquement:

- tout evidenceId cite existe;
- les sources visibles viennent des tool results;
- pas de source inventee;
- pas de doublon source/page inutile;
- les pages sont valides;
- les claims concrets sont lies a des preuves;
- les sources UI portent les memes ids/metadonnees que le writer.

Si le contrat echoue, le verifier retourne des erreurs structurees.

### 10. Repair Loop

Le LLM recoit les erreurs du verifier:

- item non source;
- source manquante;
- source dupliquee;
- page invalide;
- citation absente;
- source non visible.

Le writer repare sa reponse. Le code ne reconstruit pas la reponse a sa place sauf apres echecs repetes, timeout ou impossibilite explicite.

### 11. UI Final Validation

La validation finale doit passer par l'UI reelle:

- demarrage de l'application;
- question saisie depuis l'interface;
- attente de la reponse;
- verification que la reponse n'est pas fallback abusive;
- verification des sources affichees;
- verification qu'elles correspondent au EvidenceBundle.

## Role exact de la memoire

La memoire doit etre separee en quatre espaces:

1. Memoire de question courante
   - objectif;
   - contraintes;
   - axes;
   - lacunes.

2. Journal de recherche
   - requetes essayees;
   - resultats;
   - pivots;
   - raisons.

3. Inventaire de candidats
   - preuves retenues;
   - preuves faibles;
   - doublons;
   - bruit;
   - contexte.

4. Memoire longue duree
   - preferences utilisateur;
   - decisions projet;
   - erreurs connues;
   - chemins de reprise.

Regle absolue: une information issue de la memoire longue duree ne peut pas devenir une affirmation finale source-backed sans preuve actuelle.

## Ce que le code peut faire

- valider des schemas;
- imposer des budgets;
- dedupliquer des sources exactes;
- nettoyer legerement OCR sans changer le sens;
- refuser sources inventees;
- refuser citations vers preuves absentes;
- verifier que chaque claim final a une preuve;
- tracer chaque decision;
- relancer le LLM avec erreurs explicites.

## Ce que le code doit eviter

- juger qu'une source est "hors sujet" par heuristique metier;
- supprimer silencieusement des candidats;
- remplir des slots a la place du LLM;
- reconstruire une reponse finale metier;
- injecter des termes de domaine dans le runtime produit;
- specialiser le pipeline sur cuisine, recette, PDF historique ou categorie concrete;
- transformer une source riche en simple label/path/page avant l'UI.

## Migration recommandee

Ne pas faire un rewrite total.

Strategie recommandee: strangler architecture derriere feature flag.

Phase 0 - Spec et inventaire
- garder ce document comme ADR de reference;
- inventorier chemins actuels raw candidates, writer candidates, final sources, UI cards;
- identifier les endroits ou des candidats disparaissent.

Phase 1 - EvidenceBundle minimal
- creer le contrat EvidenceItem;
- adapter les hits RAG existants vers ce contrat;
- conserver docId, docName, sourceHash, page, chunkId, contentCards, quality, selectionHints.

Phase 2 - Writer depuis EvidenceBundle
- faire consommer au writer le meme inventaire que le verifier et l'UI;
- supprimer les chemins ou raw candidates et writer candidates divergent silencieusement.

Phase 3 - Semantic filters vers hints
- convertir les filtres semantiques deterministes en codeHints/riskFlags;
- garder seulement les hard filters mecaniques.

Phase 4 - Evidence Judge LLM
- ajouter une etape LLM distincte pour juger l'inventaire;
- lui demander les lacunes et les recherches suivantes.

Phase 5 - Source Contract Verifier + Repair
- verifier les citations et sources;
- reprompt writer avec erreurs structurees;
- limiter le rebuild deterministe aux echecs repetes.

Phase 6 - Memory policy
- definir quand injecter memoire courte ou longue;
- tracer chaque usage de memoire;
- empecher la memoire de servir de preuve finale.

Phase 7 - Replay et tests
- snapshots de tool results;
- replay du pipeline sans UI;
- tests multi-domaines;
- tests non-cuisine;
- tests multilingues;
- test UI reel final.

## Criteres de succes

- raw_candidates utiles ne deviennent jamais writer_candidates=0 sans trace explicite;
- chaque claim concret final a un evidenceId;
- les source cards UI affichent les memes metadonnees que le EvidenceBundle;
- le LLM peut repondre partiellement au lieu de remplir artificiellement;
- aucun hardcoding metier dans le runtime produit;
- les tests couvrent plusieurs domaines;
- un test UI reel passe depuis l'application.

## Risques

Risque: trop de liberte LLM cree lenteur ou boucles.
Mitigation: budgets, iteration controller, checkpoints, traces.

Risque: EvidenceBundle trop gros.
Mitigation: formats compacts, tiers de preuve, excerpts limites, drill-down pages.

Risque: verifier trop faible.
Mitigation: evidenceId obligatoire, claims concrets detectes, repair loop.

Risque: migration trop large.
Mitigation: feature flag et premier slice sur le chemin source-backed planning, puis generalisation.

## References locales importantes

- `AUDIT-2026-07-06-llm-orchestration-vs-deterministic-restrictions.md`
- `OBSERVATION-AGENT-ORCHESTRATION-2026-06-30.md`
- `AUDIT-ORCHESTRATION-2026-05-06.md`
- `REPRISE-2026-07-06-rag-meal-plan-live-failure-handoff.md`
- `REPRISE-2026-07-06-rag-retrieval-planning-checkpoint.md`
- `TODO-2026-05-05-ingestion-llm-retrieval.md`

## References externes consultees

- Anthropic, "Building effective agents": https://www.anthropic.com/engineering/building-effective-agents
- Lewis et al., "Retrieval-Augmented Generation for Knowledge-Intensive NLP Tasks": https://arxiv.org/abs/2005.11401
- Yao et al., "ReAct: Synergizing Reasoning and Acting in Language Models": https://arxiv.org/abs/2210.03629
- Asai et al., "Self-RAG: Learning to Retrieve, Generate, and Critique through Self-Reflection": https://arxiv.org/abs/2310.11511
- Packer et al., "MemGPT: Towards LLMs as Operating Systems": https://arxiv.org/abs/2310.08560

## Instruction de reprise pour Codex

Avant de modifier le pipeline RAG, ToolAgent, source-backed planning, memoire ou sources UI, relire ce document.

La priorite immediate est de stabiliser le chemin:

question utilisateur -> LLM intake/planner -> tools RAG -> EvidenceBundle -> Evidence Judge LLM -> Writer LLM -> Source Contract Verifier -> Repair Loop -> UI source cards.

Ne pas repartir dans des heuristiques metier ponctuelles tant que ce chemin canonique n'est pas en place.
