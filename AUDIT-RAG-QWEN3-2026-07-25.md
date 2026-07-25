# Audit contradictoire Qwen3 et RAG SAAIA

Date de l'audit : 2026-07-25
Statut : recherche externe et confrontation au code reel terminees pour la direction d'architecture ; campagne experimentale et migration encore a executer
Perimetre : Qwen3-4B-Instruct-2507, outils, orchestration, memoire, ingestion PDF, retrieval, reranking, preuves, citations, materiel et evaluation

## 1. Verdict executif

La direction fondamentale du projet est bonne :

- le LLM doit prendre les decisions semantiques ;
- les outils doivent exposer les donnees et les actions possibles sans imposer un parcours ;
- les preuves doivent garder leur identite mecanique, notamment `docId`, fichier, page et `chunkId` ;
- la memoire peut aider le raisonnement, mais ne doit jamais remplacer une preuve actuelle ;
- un verificateur deterministe peut controler la forme, la provenance et la couverture sans choisir quelles sources sont semantiquement pertinentes.

En revanche, l'implementation actuelle a depasse le seuil ou des correctifs locaux restent rentables. Le probleme principal n'est plus Qwen3 lui-meme. Le pipeline source-backed est devenu trop vaste, trop segmente et trop specialise :

- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag` contient 215 fichiers C# et environ 32 000 lignes ;
- 33 sites d'appel LLM ont ete reperes dans ce sous-systeme ;
- 16 fichiers du coeur generique contiennent encore des instructions propres aux repas, recettes, petits-dejeuners ou collations ;
- `backend/SAAIA.Backend/Endpoints/RagEndpoints.cs` depasse 36 000 lignes, environ 1,56 Mo et 1 182 declarations de methodes ;
- 183 familles distinctes de methodes ou regles de type `Should...`, `LooksLike...`, `Prefer...`, `Suppress...` ou `Calibrate...` y ont ete comptees ;
- le test reel du plan de repas peut depasser 18 minutes et effectuer de nombreuses etapes LLM avant de produire une reponse ou d'echouer.

La conclusion n'est pas de supprimer les garanties construites. Il faut conserver les bons invariants et reconstruire un chemin nominal beaucoup plus court autour d'eux.

La cible recommandee est un agent compact de type ReAct :

1. le LLM recoit la demande, les outils disponibles et une memoire de travail compacte ;
2. il choisit librement zero, un ou plusieurs outils ;
3. l'application execute uniquement les appels valides et retourne leurs observations avec leur provenance ;
4. le LLM met a jour ses lacunes et choisit l'action suivante ;
5. quand il estime les preuves suffisantes, il redige depuis un `EvidenceBundle` canonique ;
6. un controle mecanique verifie structure, identifiants, pages, doublons et citations ;
7. un seul passage de reparation LLM est autorise si le contrat mecanique echoue.

Cette direction correspond au principe ReAct, qui entrelace raisonnement et actions au lieu de figer un long plan avant d'avoir observe les resultats ([papier et projet ReAct](https://react-lm.github.io/)). Elle correspond aussi au protocole officiel Qwen : l'application expose des fonctions, le LLM choisit s'il en utilise zero, une ou plusieurs, puis les resultats lui sont rendus pour poursuivre ([documentation Qwen Function Calling](https://qwen.readthedocs.io/en/stable/framework/function_call.html)).

## 2. Ce qui est valide et doit etre conserve

### 2.1 LLM comme orchestrateur semantique

L'ADR `ADR-2026-07-08-rag-llm-orchestration-source-backed.md` pose la bonne frontiere :

- le LLM juge le sens, l'utilite et la suffisance ;
- le code valide les schemas, autorisations, identifiants, doublons exacts, pages et contrats ;
- le code ne decide pas qu'une recette, un produit ou un passage est le meilleur choix semantique.

La documentation Qwen confirme explicitement que le modele doit choisir lui-meme de ne pas appeler, d'appeler une ou d'appeler plusieurs fonctions. Qwen recommande le format Hermes et Qwen-Agent pour maximiser les performances de tool calling ([Qwen Function Calling](https://qwen.readthedocs.io/en/stable/framework/function_call.html), [Qwen-Agent](https://github.com/QwenLM/Qwen-Agent)).

### 2.2 Navigation documentaire comme carte, pas comme preuve

L'outil `documents.navigation` est une bonne abstraction. PageIndex suit une idee voisine : transformer un document long en arbre semantique comparable a un sommaire, puis laisser un agent raisonner sur cet arbre pour localiser les sections a lire ([PageIndex](https://github.com/VectifyAI/PageIndex)).

Le correctif ajoute le 25 juillet, qui rend visibles au LLM des ancres exactes tardives avec fichier et page, va dans la bonne direction. Il ne choisit pas une recette a la place du LLM ; il evite qu'une ancre mecanique deja trouvee disparaisse de son contexte.

### 2.3 EvidenceBundle et provenance immuable

Le `EvidenceBundle` est le bon fil rouge. Il doit etre la seule origine des preuves envoyees :

- au writer ;
- au verificateur ;
- aux cartes de sources de l'interface ;
- aux traces d'audit.

Docling Graph formalise la meme exigence : chaque noeud doit pouvoir etre relie aux chunks, pages et, lorsque possible, aux positions exactes dans le document source ([provenance Docling](https://docling-project.github.io/docling-graph/fundamentals/graph-management/provenance/)). LlamaIndex illustre le risque inverse : si la relation avec le document source n'est pas construite, le `ref_doc_id` peut disparaitre ([issue LlamaIndex sur `ref_doc_id`](https://github.com/run-llama/llama_index/issues/9209)).

### 2.4 Retrieval hybride et structure de document

Le backend possede deja plusieurs briques pertinentes :

- correspondances exactes ;
- recherche lexicale/BM25 ;
- recherche dense ;
- fusion ;
- titres et ancres ;
- contexte lie ;
- pages ;
- sections, unites et chemins de titres ;
- cartes de contenu ;
- suppression de bruit de navigation ;
- Qdrant.

Il ne faut donc pas jeter le retrieval actuel sans comparaison. Les meilleures pratiques modernes combinent rappel hybride et reranking de precision ; Qdrant documente notamment la combinaison dense, sparse et late-interaction reranking ([tutoriel Qdrant](https://qdrant.tech/documentation/advanced-tutorials/reranking-hybrid-search/)).

## 3. Problemes confirmes

### 3.1 Le pipeline est devenu plus complique que le probleme

La multiplication des micro-etapes LLM a trois effets :

1. chaque etape peut perdre une information presente a l'etape precedente ;
2. chaque nouveau contrat ajoute latence, tokens et possibilites de reparation ;
3. le petit modele depense sa capacite a satisfaire l'echafaudage plutot qu'a comprendre la demande et explorer les documents.

Le code comporte maintenant des phases de classification, extraction d'axes, definition de classe d'objets, audit de requetes, shortlist, compatibilite de colonnes, selection, verification de valeur, adequation, writer et reparation. Plusieurs sont raisonnables isolees ; leur accumulation ne l'est plus.

Le benchmark DeepPlanning de Qwen montre que meme les modeles de frontiere ont des difficultes avec la planification globale sous contraintes. Il insiste sur l'acquisition proactive d'informations, les contraintes locales et la verification globale ([DeepPlanning](https://qwenlm.github.io/Qwen-Agent/en/benchmarks/deepplanning/)). Cela plaide pour un agent qui observe et corrige progressivement, accompagne d'un verificateur mecanique, pas pour une cascade toujours plus longue de prompts specialises.

### 3.2 Les contraintes historiques de Qwen2.5 sont encore presentes

Qwen3-4B-Instruct-2507 est un modele non-thinking de 4B, 36 couches et 262 144 tokens de contexte natif. Qwen annonce des gains importants en instruction, raisonnement, comprehension, outils et contexte long. Ses scores agents officiels atteignent 61,9 sur BFCL-v3, 48,7 sur TAU Retail et 32,0 sur TAU Airline : il est nettement meilleur que l'ancien 4B Qwen3, mais il reste imparfait ([fiche officielle Qwen3-4B-Instruct-2507](https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507)).

Le projet lui impose pourtant encore :

- de nombreuses decisions JSON intermediaires ;
- un echantillonnage structure deterministe par defaut ;
- des budgets parfois tres courts ;
- des prompts qui codent des exemples ou exclusions propres aux repas ;
- des normalisations de `topK` dependantes de detections de type de demande ;
- des chemins de recuperation herites des echecs de Qwen2.5.

Ces elements peuvent masquer les progres du nouveau modele. Ils doivent etre remis en concurrence, pas simplement ajustes.

### 3.3 Le protocole d'outils n'est pas celui recommande pour Qwen3

Le code actuel ne transmet pas les outils dans le champ OpenAI `tools`. Il insere un manifeste dans les messages, puis demande une decision via `response_format=json_schema`.

Cette methode a un avantage reel : la grammaire JSON evite beaucoup d'erreurs de syntaxe. Des utilisateurs de petits modeles locaux rapportent d'ailleurs qu'un schema contraint peut etre plus stable que le function calling brut. Ce retour reste anecdotique, pas une preuve generale ([discussion LocalLLaMA](https://www.reddit.com/r/LocalLLaMA/comments/1sp631h/are_you_guys_actually_using_local_tool_calling_or/)).

Mais Qwen recommande :

- un protocole Hermes ;
- le template et le parseur Qwen-Agent ;
- le type `fncall_prompt_type=nous` pour Qwen3 ;
- des appels multi-etapes, multi-tours et paralleles lorsque le modele les choisit.

Qwen-Agent sait analyser les appels lui-meme si le serveur ne possede pas le bon parseur ([fonctionnalites Qwen-Agent](https://qwenlm.github.io/Qwen-Agent/en/guide/get_started/features/), [configuration Qwen-Agent](https://github.com/QwenLM/Qwen-Agent)).

`llama.cpp` prend en charge les outils OpenAI avec `--jinja`, mais avertit que :

- le format generique est plus couteux en tokens ;
- le template et le parseur doivent correspondre au modele ;
- les appels paralleles doivent etre explicitement supportes ;
- une quantification KV extreme peut fortement degrader les outils ([documentation function calling llama.cpp](https://github.com/ggml-org/llama.cpp/blob/master/docs/function-calling.md)).

Des bogues reels existent encore, par exemple sur les schemas `array<object>` dans certaines variantes Qwen3, ou sur la representation des arguments ([issue llama.cpp 21771](https://github.com/ggml-org/llama.cpp/issues/21771), [issue llama.cpp 20198](https://github.com/ggml-org/llama.cpp/issues/20198)). Il serait donc imprudent de remplacer immediatement le JSON Schema actuel.

Decision : construire trois adaptateurs experimentaux et promouvoir uniquement le gagnant mesure :

- A : contrat d'action JSON Schema actuel, simplifie ;
- B : `tools` OpenAI natifs via `llama.cpp --jinja` ;
- C : template et parseur compatibles Qwen-Agent `nous/Hermes`.

### 3.3.1 Ce que les forums confirment et ce qu'ils ne prouvent pas

Les retours communautaires ne doivent pas etre traites comme un benchmark, mais ils sont utiles pour identifier les echecs qui ne figurent pas toujours dans les documentations :

- plusieurs utilisateurs obtiennent de tres bons appels d'outils avec Qwen3 4B, alors que d'autres rencontrent des appels ignores, tronques ou invalides avec exactement la meme famille de modele ;
- la difference vient souvent du couple template/parseur/runtime, et non du seul poids du modele ;
- des utilisateurs signalent de meilleurs resultats apres correction du template, utilisation d'un autoparseur ou grammaire contrainte ;
- un comparatif personnel de RAG local a trouve Qwen3 4B suffisamment bon pour remplacer un modele distant dans son pipeline agentique, mais ce resultat n'est ni scientifique ni directement transposable au corpus SAAIA ([retour LocalLLaMA sur Qwen3 4B et RAG](https://www.reddit.com/r/LocalLLaMA/comments/1kc6wqm/local_llm_rag_comparison_can_a_small_local_model/)) ;
- une discussion sur les modeles locaux d'environ 4B cite Qwen3 parmi les rares candidats plausibles pour le function calling, tout en rappelant que la fiabilite baisse avec les petits modeles ([discussion LocalLLM](https://www.reddit.com/r/LocalLLM/comments/1kdva3y/best_small_llm_4b_for_functiontool_calling_with/)).

Les issues `llama.cpp` rendent ce point plus concret :

- un schema `array<object>` peut provoquer une erreur du parseur Qwen3, laisser un appel partiel dans l'historique puis empoisonner les tours suivants ([issue 21771](https://github.com/ggml-org/llama.cpp/issues/21771)) ;
- de longs contextes et de nombreux parametres optionnels peuvent degrader de facon repetee la validite des appels ([issue 20164](https://github.com/ggml-org/llama.cpp/issues/20164)) ;
- les appels multiples/paralleles ont leur propre support et ne doivent pas etre supposes actifs par defaut ([documentation llama.cpp](https://github.com/ggml-org/llama.cpp/blob/master/docs/function-calling.md)).

Conclusion pratique : le test A/B/C doit verifier le protocole complet, y compris plusieurs tours, les arguments complexes, l'historique apres erreur, l'abstention et la reprise. Un test simple ou Qwen3 appelle correctement un seul outil ne suffit pas a qualifier l'agent.

### 3.4 Les parametres d'inference ne correspondent pas au profil officiel Qwen3

Qwen recommande pour Qwen3-4B-Instruct-2507 :

- temperature 0,7 ;
- `top_p=0,8` ;
- `top_k=20` ;
- `min_p=0` ;
- un budget de sortie genereux, jusqu'a 16 384 tokens pour les requetes qui le justifient.

Le chemin reel observe utilise selon l'etape :

- temperature 0 ou 0,1 ;
- `top_p` 1 ou 0,85 ;
- `top_k=40` herite du serveur ;
- `min_p=0,05` herite du serveur ;
- environ 400 tokens pour plusieurs decisions ;
- penalties de frequence/presence appliquees de facon transversale.

Une temperature basse n'est pas automatiquement mauvaise pour un schema. Le probleme est que la meme philosophie est appliquee a des decisions semantiques, a l'exploration et a la redaction. Il faut des profils par role, qualifies sur des taches reelles :

- appel d'outil ;
- jugement de suffisance ;
- redaction ;
- reparation mecanique.

Les valeurs officielles constituent le point de depart du test, pas une valeur a imposer aveuglement a tous les roles.

### 3.5 Le contexte effectif est beaucoup plus petit que le contexte annonce du modele

Le serveur reel tourne avec `--ctx-size 8192`, alors que le modele accepte nativement 262 144 tokens. Ce choix est comprehensible sur la Quadro P520 4 Go, mais il change la conception necessaire.

Les metriques du serveur ont deja observe des prompts proches de 7 976 tokens. Le serveur n'utilise pas actuellement `--no-context-shift`. Qwen avertit que la rotation de contexte de `llama.cpp`, lorsqu'elle evince des tokens precedents, peut perturber le modele si le contexte et la sortie ne sont pas dimensionnes correctement ([guide officiel Qwen3 pour llama.cpp](https://github.com/QwenLM/Qwen3)).

Le papier Lost in the Middle montre en outre que les modeles utilisent moins bien une information placee au milieu d'un long contexte, meme lorsqu'ils supportent officiellement ce contexte ([Lost in the Middle](https://arxiv.org/abs/2307.03172)).

Modifications necessaires :

- mesurer les tokens avant chaque requete ;
- reserver explicitement la place de sortie ;
- ne jamais laisser le serveur evincer silencieusement les instructions ou les preuves ;
- compacter d'abord les anciennes observations d'outils ;
- conserver en debut ou fin de prompt les exigences et les lacunes actives ;
- tester 8K, 12K et 16K sur la machine reelle ;
- qualifier `--no-context-shift` pour le chemin agent.

### 3.6 Le retrieval n'est pas pilote par un benchmark de reference

Le backend est tres sophistique, mais la sophistication n'est pas une metrique. Sans jeu de verite terrain, il est impossible de savoir si une nouvelle branche de fallback augmente le rappel ou ajoute seulement du bruit.

RAGAS distingue notamment :

- `context recall` : les preuves necessaires ont-elles ete retrouvees ?
- `context precision` : les preuves pertinentes sont-elles placees avant le bruit ?
- `faithfulness` : les affirmations viennent-elles du contexte ?
- `answer relevance` : la reponse traite-t-elle la question ?

Voir [RAGAS Context Recall](https://docs.ragas.io/en/stable/concepts/metrics/available_metrics/context_recall/) et [RAGAS Context Precision](https://docs.ragas.io/en/stable/concepts/metrics/available_metrics/context_precision/).

RAGChecker va plus loin en diagnostiquant separement retrieval et generation, avec une meilleure correlation humaine rapportee par ses auteurs ([papier RAGChecker](https://arxiv.org/abs/2408.08067), [code RAGChecker](https://github.com/amazon-science/RAGChecker)).

Le projet doit posseder un jeu d'evaluation versionne, derive de questions reelles et annote avec :

- documents pertinents ;
- pages pertinentes ;
- passages minimaux ;
- contraintes de reponse ;
- assertions attendues ;
- sources acceptables ;
- erreurs critiques.

La recherche contextuelle est une variante prioritaire a mesurer. Anthropic rapporte, sur ses corpus, une reduction des echecs de retrieval top-20 de 49 % avec embeddings contextuels plus BM25 contextuel, et de 67 % en ajoutant un reranker. Ces chiffres ne constituent pas une promesse pour SAAIA, mais ils justifient une variante ou chaque chunk recoit avant indexation un bref contexte documentaire, tout en conservant son texte original et sa provenance ([Contextual Retrieval](https://www.anthropic.com/engineering/contextual-retrieval)).

### 3.7 Le reranker existe dans le code mais est desactive

La configuration nominale contient :

- `EmbeddingsModel=intfloat/multilingual-e5-base` ;
- `EnableRerank=false` ;
- aucun modele de reranking ;
- TEI CPU 1.8.1 dans le compose.

Le retrieval hybride peut donc produire un bon ensemble de candidats, mais il ne beneficie pas d'un cross-encoder ou d'un reranker instruction-aware pour ordonner les passages selon la question complete.

Qwen3 propose :

- `Qwen3-Embedding-0.6B`, 1024 dimensions, 32K, multilingue et instruction-aware ;
- `Qwen3-Reranker-0.6B`, 32K, multilingue et instruction-aware.

Sur les chiffres publies par Qwen, le 0.6B embedding obtient 64,33 de moyenne sur MTEB multilingue, contre 63,22 pour `multilingual-e5-large-instruct`. Ce gain publie est reel mais modeste, et notre modele actuel est `e5-base`, pas `e5-large`. Il faut donc mesurer sur le corpus SAAIA plutot que citer le leaderboard comme verdict ([Qwen3-Embedding](https://github.com/QwenLM/Qwen3-Embedding)).

Le reranker 0.6B publie de meilleurs scores que plusieurs rerankers BGE/Jina sur les bancs de Qwen, mais ces resultats doivent aussi etre reproduits sur nos documents.

### 3.8 L'iGPU Intel est une ressource RAG pertinente, pas une fausse VRAM de 16 Go

La machine de reference contient :

- Intel Core i7-10510U, 4 coeurs / 8 threads ;
- Intel UHD Graphics, memoire partagee ;
- NVIDIA Quadro P520, 4 Go de VRAM ;
- environ 32 Go de RAM.

Les 15,9 Go affiches par Windows pour l'Intel UHD sont une limite de memoire partagee, pas 16 Go de VRAM dediee. Cela ne permet pas de traiter l'iGPU comme une carte discrete de 16 Go.

En revanche, OpenVINO 2026 supporte officiellement :

- les processeurs Intel Core de 6e a 14e generation ;
- Intel UHD Graphics ;
- Windows 10 et 11 ;
- Qwen3-Embedding et Qwen3-Reranker via OpenVINO Model Server.

Voir [prerequis OpenVINO](https://docs.openvino.ai/2026/about-openvino/release-notes-openvino/system-requirements.html) et [demo RAG OpenVINO](https://docs.openvino.ai/2026/model-server/ovms_demos_integration_with_open_webui.html).

La demo officielle fournit sur Windows :

- `/v3/embeddings` pour `OpenVINO/Qwen3-Embedding-0.6B-*` ;
- `/v3/rerank` pour `OpenVINO/Qwen3-Reranker-0.6B-*` ;
- `--target_device GPU`.

C'est probablement la meilleure utilisation complementaire de l'iGPU :

- Quadro P520 : generation Qwen3 via CUDA ;
- Intel UHD : embeddings et reranking via OpenVINO ;
- CPU : BM25, SQL, Qdrant et orchestration.

L'API SAAIA est aujourd'hui codee pour TEI (`/v1/embeddings`, `/rerank`, champ `texts`). Un adaptateur de fournisseur est donc necessaire pour OpenVINO (`/v3/embeddings`, `/v3/rerank`, champ `documents`).

### 3.9 L'ingestion doit etre comparee, pas remplacee sur reputation

Le pipeline actuel preserve deja :

- pages ;
- sections ;
- unites ;
- titres ;
- chemins hierarchiques ;
- ancres ;
- contexte projete ;
- signaux OCR et qualite.

Docling propose un `HierarchicalChunker` qui conserve la structure, les titres, les legendes et les metadonnees, ainsi qu'un `HybridChunker` et un chunker ligne pour les tableaux ([Docling Chunking](https://docling-project.github.io/docling/concepts/chunking/)).

Une etude recente comparant 19 pipelines PDF rapporte que Docling avec splitting hierarchique et descriptions d'images a obtenu le meilleur resultat automatique de son banc, et que l'enrichissement des metadonnees et la hierarchie ont compte davantage que le seul choix du parseur ([etude PDF-to-RAG](https://arxiv.org/abs/2604.04948)).

PageIndex fournit une autre variante interessante pour les documents longs : arbre de titres avec plages de pages et resume par noeud. Mais ses meilleurs resultats publics concernent surtout des documents financiers et son indexation peut demander un LLM couteux. Les forums signalent aussi qu'un arbre ne remplace pas toujours le retrieval hybride. Il doit donc etre traite comme une variante, pas comme une solution magique.

Les forums RAG convergent sur trois points, avec des avis contradictoires sur les tailles exactes :

- une taille de chunk unique fonctionne mal sur des PDF de structures variees ;
- la structure, les tableaux, titres, listes et limites de pages doivent etre conserves avant de raffiner semantiquement les chunks ;
- le bon choix doit etre mesure sur des questions et pages de reference plutot qu'adopte par reputation ([discussion recente sur les PDF heterogenes](https://www.reddit.com/r/Rag/comments/1uxwqsh/best_chunking_strategy_for_different_pdf/), [discussion Hugging Face sur les ruptures de pages et en-tetes](https://discuss.huggingface.co/t/challenges-of-using-pdf-documents-as-input-for-rag-text-flow-tokenization-and-semantic-coherence/115662)).

Un retour de production recent sur les citations recommande de creer une identite deterministe de chunk des l'ingestion, de la garder immuable, d'exposer au LLM seulement de petits labels temporaires et de verifier ensuite la correspondance label vers document/page/chunk. Cette experience communautaire correspond directement au `EvidenceBundle` cible et explique pourquoi une bonne source peut etre retrouvee mais perdre son fichier ou sa page en traversant le pipeline ([discussion sur la degradation des citations](https://www.reddit.com/r/Rag/comments/1uuke7m/the_rag_citation_problem_why_i_ended_up_tracking/)).

Campagne recommandee :

- pipeline SAAIA actuel ;
- Docling Hierarchical/Hybrid ;
- arbre PageIndex-like ajoute au pipeline SAAIA, sans supprimer les vecteurs.

Comparer sur un corpus representatif :

- rappel de la bonne page ;
- rappel de l'ancre/titre exact ;
- conservation des tableaux ;
- coherence des recettes/procedures ;
- taux de texte OCR corrompu ;
- temps et RAM d'ingestion ;
- taille de l'index ;
- qualite finale des citations.

### 3.10 La memoire doit etre hierarchique et separee des preuves

Qwen-Agent compacte les anciens tours et observations d'outils lorsque le contexte approche de la limite ; il conserve la requete et les elements les plus recents, mais reconnait que sa memoire durable reste perfectible ([gestion de contexte Qwen-Agent](https://qwenlm.github.io/Qwen-Agent/en/guide/core_moduls/context/)).

MemGPT/Letta separe :

- memoire de travail visible dans le contexte ;
- memoire persistante compacte ;
- memoire d'archive interrogee par outil ;
- RAG documentaire externe.

Voir [papier MemGPT](https://arxiv.org/abs/2310.08560) et [hierarchie Letta](https://docs.letta.com/guides/core-concepts/memory/context-hierarchy).

Pour SAAIA :

- la memoire de travail contient objectif, contraintes, actions, observations, preuves retenues et lacunes ;
- la memoire conversationnelle contient preferences, corrections et contexte utilisateur ;
- la memoire episodique contient des traces et lecons de sessions passees ;
- le RAG contient les documents ;
- seule une preuve RAG actuelle peut soutenir une affirmation source-backed.

Le LLM peut choisir de consulter ou mettre a jour la memoire. Le code controle schema, taille, portee, confidentialite et interdiction de transformer une memoire en citation.

### 3.11 Le test live du 25 juillet confirme un probleme d'architecture, pas un manque de donnees

Le test live cible du plan de repas, execute avec `Qwen3-4B-Instruct-2507-Q5_K_M` apres le correctif de visibilite des ancres de navigation, a echoue :

- duree exacte : `00:24:00.2727986` ;
- resultat : `OperationCanceledException` ;
- point d'annulation : nouvel appel LLM de compatibilite des cartes canoniques ;
- reponse utilisateur : aucune ;
- sources finales : aucune.

Trace : `rag-20260725193218537-1f653fed`. Resultat de test : `artifacts/live-navigation-visible-orchestrator-x64-20260725-2133/Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf-live.trx`.

Avant l'annulation, le pipeline avait execute :

- 1 appel `documents.navigation` ;
- 6 appels `rag.search` ;
- 1 appel `documents.context` ;
- environ 1 206 913 caracteres de resultats d'outils bruts.

Les recherches ont rapporte respectivement 20, 13, 20, 9, 20 et 19 hits. Ce n'est donc pas un cas ou le modele n'avait rien trouve. Pourtant :

- les requetes sont restees tres generales et se sont repetees sous des formulations voisines ;
- le seul appel de contexte a utilise un `docRef` opaque sur les pages 5 a 7 ;
- cet appel n'a retourne que 520 caracteres ;
- les meilleures ancres de navigation n'ont pas ete transformees en lectures ciblees ;
- le pipeline a poursuivi ses jugements, recherches et controles au lieu de converger vers une redaction.

Cette execution invalide l'hypothese qu'un nouveau correctif local de navigation suffirait. Le correctif reste utile comme garantie mecanique, mais le chemin nominal doit changer :

- observations d'outils compactes ;
- lacunes actives explicites ;
- labels de preuves stables ;
- boucle agent courte ;
- aucune retransmission massive de tous les candidats ;
- arret lorsque le LLM declare une preuve suffisante et que le contrat mecanique est satisfait ;
- un seul passage de reparation au lieu d'une cascade de sous-juges.

### 3.12 Une boucle Qwen3 native et compacte reussit la meme structure en 95 secondes

Un second banc a isole Qwen3 du pipeline SAAIA. Le serveur existant a recu les outils dans le champ OpenAI `tools`, avec :

- choix `auto` ;
- appels paralleles permis mais non imposes ;
- aucune consigne de nombre ou d'ordre d'outils ;
- parametres officiels Qwen3 : temperature 0,7, `top_p=0,8`, `top_k=20`, `min_p=0` ;
- observations simulees, mais avec des `evidenceId`, fichiers et pages mecaniquement exacts.

Artifact : `artifacts/native-agent-loop-qwen3-q5km-official-retry2-20260725/qwen3-4b-2507-q5km-official-seed42-retry2.json`.

Resultats :

| Scenario | Resultat | Temps | Appels choisis par Qwen3 |
|---|---:|---:|---|
| reformulation sans preuve | reussie | 2,354 s | aucun |
| question sur document nomme | reussie | 16,076 s | navigation, puis contexte exact |
| plan de repas 5 x 4 | structure reussie, citations encore partielles | 95,175 s | navigation, puis 4 recherches ciblees |

Pour le document nomme, Qwen3 a conserve `HydraulicPumpManual.pdf`, lu le contexte cible, puis repondu `85 N m`, fichier et page 42. Pour le plan de repas, il a :

- choisi seul une navigation ;
- exploite les quatre ancres fichier/page exposees ;
- lance une recherche distincte et ciblee pour chaque type de repas ;
- evite tout appel duplique ;
- produit les 20 cases ;
- cite les quatre fichiers et leurs plages de pages.

La lacune restante est importante mais circonscrite : le writer a regroupe les citations par type de repas au lieu de rattacher chaque case a `S1` jusqu'a `S20`. La nouvelle architecture doit donc imposer mecaniquement la conservation du label de preuve jusqu'a chaque cellule, sans choisir le contenu semantique de la cellule.

Une premiere variante du banc renvoyait les 20 cartes a chaque recherche, donc plusieurs copies massives de la meme observation. Elle a fini sur une erreur HTTP 500 au quatrieme tour. Apres correction du mock pour ne rendre que les cinq cartes demandees, la boucle a termine. Ce resultat ne prouve pas encore le backend reel, mais il confirme deux points :

1. Qwen3 sait orchestrer une recherche multi-tour et rediger une grille complete lorsqu'il n'est pas enferme dans la cascade actuelle ;
2. la compaction des observations et la non-duplication des preuves sont des exigences de fiabilite du protocole, pas seulement des optimisations de vitesse.

## 4. Architecture cible

```text
Demande utilisateur
        |
        v
LLM agent Qwen3
  - demande originale
  - outils disponibles
  - working memory compacte
  - EvidenceBundle courant
        |
        +--> aucun outil : reponse conversationnelle
        |
        +--> appel(s) choisi(s) par le LLM
                 |
                 v
          Validateur mecanique
          - nom/schema/permission/budget
                 |
                 v
          Execution des outils
          - navigation
          - RAG hybride
          - contexte page/chunk
          - sources
          - memoire
                 |
                 v
          Observations + provenance
                 |
                 +------ retour au LLM

Quand le LLM declare les preuves suffisantes :

EvidenceBundle canonique
        |
        v
Writer LLM
        |
        v
Source Contract Verifier
  - structure demandee
  - identifiants existants
  - pages/fichiers reels
  - citations supportees
  - doublons exacts
        |
        +--> conforme : UI + cartes de sources
        |
        +--> non conforme : une reparation LLM, sinon reponse partielle honnete
```

### 4.1 Ce que le code peut decider

- outil connu et autorise ;
- arguments conformes au schema ;
- limites de ressources et timeouts ;
- identite d'un document, chunk ou page ;
- source absente ou inventee ;
- doublon mecanique ;
- couverture d'une grille demandee ;
- presence d'une citation pour une affirmation qui en exige une ;
- conservation de la provenance ;
- trace et observabilite.

### 4.2 Ce que le code ne doit pas decider

- quelle recette est adaptee a un petit-dejeuner ;
- quel document est intellectuellement le meilleur ;
- quels mots de recherche sont semantiquement optimaux ;
- combien d'outils il faut appeler ;
- si une source est suffisamment pertinente sur le fond ;
- si une nouvelle recherche semantique est utile ;
- comment repartir les plats dans les cases.

Ces decisions appartiennent au LLM. Le verificateur peut signaler qu'une case ou une source manque ; il ne choisit pas son remplacement.

## 5. Banc d'essai obligatoire avant migration

### 5.1 Corpus

Creer trois banques versionnees :

1. questions simples :
   - fait unique ;
   - document nomme ;
   - page exacte ;
   - question sans outil ;
   - question hors corpus ;
2. questions multi-sources :
   - comparaison ;
   - synthese ;
   - version/date ;
   - information repartie sur plusieurs pages ;
3. planification :
   - plan de repas 5 x 4 ;
   - planning de maintenance ;
   - tableau comparatif de produits ;
   - contraintes manquantes ou contradictoires.

Le plan de repas est un test important, mais il ne doit pas etre le seul domaine qui guide l'architecture.

### 5.2 Metriques

Retrieval :

- Recall@k des documents ;
- Recall@k des pages ;
- MRR ;
- nDCG@k ;
- precision des ancres ;
- taux de bruit de navigation ;
- taux de duplications.

Agent :

- appel d'outil syntaxiquement valide ;
- bon choix d'outil selon annotation humaine ;
- abstention correcte ;
- arguments corrects ;
- boucles/repetitions ;
- nombre d'appels ;
- tokens ;
- temps avant premiere preuve ;
- temps total.

Reponse :

- exactitude ;
- completude ;
- honnetete/abstention ;
- fidelite aux preuves ;
- pertinence ;
- structure demandee.

Citation :

- citation precision : la source citee soutient-elle l'affirmation ?
- citation recall : toutes les affirmations qui necessitent une preuve sont-elles citees ?
- fichier/page resolvables ;
- concordance `EvidenceBundle -> writer -> UI`.

ALCE separe justement fluidite, correction et qualite des citations, et montre qu'une reponse correcte n'implique pas des citations completes ([ALCE](https://github.com/princeton-nlp/ALCE), [papier ALCE](https://arxiv.org/abs/2305.14627)). La verification doit donc travailler au niveau des affirmations, pas seulement au niveau de la reponse entiere.

Latence :

- ingestion ;
- embedding ;
- lexical ;
- dense ;
- rerank ;
- chaque appel LLM ;
- time-to-first-token ;
- temps jusqu'a la premiere preuve ;
- temps jusqu'a la reponse ;
- p50, p95 et p99.

Materiel :

- RAM ;
- VRAM NVIDIA ;
- memoire partagee Intel ;
- CPU ;
- utilisation des deux GPU ;
- temperature ;
- throttling ;
- consommation si disponible.

## 6. Matrice d'experiences prioritaire

### Experience A - protocole d'outils

Comparer sur les memes requetes et memes seeds :

| Variante | Description |
|---|---|
| A1 | JSON Schema actuel, prompts fortement reduits |
| A2 | `tools` OpenAI natifs avec parseur `llama.cpp` |
| A3 | protocole `nous/Hermes` compatible Qwen-Agent |

Promotion seulement si une variante gagne sur :

- succes de tache ;
- choix d'outil ;
- validite des arguments ;
- citations finales ;
- latence ;
- absence de boucle.

### Experience B - echantillonnage Qwen3

Tester par role :

| Role | Variantes initiales |
|---|---|
| outil/action | temperature 0, 0,2 et 0,7 |
| jugement de suffisance | 0,1, 0,3 et 0,7 |
| writer | 0,2, 0,5 et 0,7 |
| reparation mecanique | 0 et 0,1 |

Pour chaque variante, comparer aussi :

- `top_p` 0,8 vs 0,85 ;
- `top_k` 20 vs 40 ;
- `min_p` 0 vs 0,05 ;
- penalties 0 vs valeurs actuelles.

### Experience C - contexte

- 8K avec budget strict et `--no-context-shift` ;
- 12K ;
- 16K ;
- mesure RAM/VRAM, TTFT, tokens/s et succes.

L'objectif n'est pas le contexte maximal. L'objectif est le meilleur contexte utile sans eviction silencieuse ni chute disproportionnee de performance.

### Experience D - quantification

Comparer le Qwen3 retenu :

- Q5_K_M actuel ;
- Q6_K ;
- eventuellement Q8_0 si la RAM et la latence restent acceptables.

Mesurer le comportement agent et citations, pas seulement la perplexite ou les tokens/s. La quantification peut degrader des competences fragiles de format et d'outil avant de degrader fortement les scores generaux.

### Experience E - embeddings et reranking

| Variante | Embedding | Reranker | Materiel |
|---|---|---|---|
| E1 | multilingual-e5-base | aucun | TEI CPU |
| E2 | multilingual-e5-base | candidat leger | CPU |
| E3 | Qwen3-Embedding-0.6B | aucun | OpenVINO Intel GPU |
| E4 | Qwen3-Embedding-0.6B | Qwen3-Reranker-0.6B | OpenVINO Intel GPU |
| E5 | meilleur embedding | meilleur reranker | CPU fallback |

Une migration d'embedding exige une nouvelle collection Qdrant ou un nouvel index versionne. Il ne faut jamais melanger des vecteurs produits par deux modeles.

### Experience F - ingestion

- SAAIA actuel ;
- Docling Hierarchical/Hybrid ;
- SAAIA + arbre PageIndex-like ;
- variantes de chunk et contexte.

Ne retenir une refonte que si elle gagne sur page recall, citations et cout d'exploitation.

## 7. Plan d'action recommande

### P0 - stopper l'empilement de correctifs locaux

- geler les nouvelles heuristiques propres au plan de repas ;
- conserver uniquement les correctifs de provenance et de contrat mecanique ;
- etiqueter le pipeline actuel comme `legacy-source-backed-v1` ;
- instrumenter exactement ses appels, tokens, latences et pertes d'evidence.

### P1 - creer le banc d'essai

- constituer le golden set ;
- extraire des traces actuelles ;
- ajouter les metriques retrieval, agent, reponse et citation ;
- calibrer les juges automatiques sur une lecture humaine ;
- rendre les resultats reproductibles par configuration et seed.

### P2 - introduire le nouvel agent derriere un feature flag

- `source-backed-agent-v2` ;
- boucle ReAct compacte ;
- working memory explicite ;
- outils actuels exposes sans parcours impose ;
- `EvidenceBundle` partage ;
- un verifier mecanique ;
- une reparation maximum.

Le v1 reste disponible pour comparaison et retour arriere jusqu'a ce que le v2 gagne objectivement.

### P3 - qualifier Qwen3 correctement

- protocole A1/A2/A3 ;
- profils d'echantillonnage ;
- contexte ;
- Q5 vs Q6 ;
- test simple, multi-source et planification ;
- promotion par score composite qualite/latence.

### P4 - moderniser retrieval et materiel

- adaptateur TEI/OpenVINO ;
- OpenVINO Model Server Windows ;
- Qwen3 Embedding/Reranker sur Intel UHD ;
- nouvelle collection Qdrant versionnee ;
- bake-off ingestion ;
- promotion uniquement avec preuves.

### P5 - nettoyer et migrer

- supprimer les anciennes branches du pipeline seulement apres victoire du v2 ;
- reduire `RagEndpoints.cs` en services modulaires ;
- separer retrieval exact, sparse, dense, fusion, rerank et projection ;
- supprimer les prompts et classes propres aux repas du chemin generique ;
- conserver les tests de regression, en les rebranchant sur les contrats publics ;
- committer par lots coherents et reversibles.

## 8. Criteres de sortie

Le travail ne sera considere termine que lorsque :

- une question simple produit une reponse source-backed correcte et rapide ;
- un document et une page sont resolvables depuis chaque citation visible ;
- le plan de repas contient 20 cases coherentes ou explique precisement les preuves manquantes ;
- le LLM choisit ses outils sans nombre ou sequence imposes ;
- aucune memoire n'est presentee comme preuve ;
- le nouvel agent bat le legacy sur le golden set ;
- la latence est mesuree et acceptable ;
- les deux GPU sont utilises lorsqu'un profil materiel qualifie le permet ;
- un mode CPU-only reste fonctionnel ;
- l'interface affiche exactement les sources du `EvidenceBundle` ;
- les tests live et WinUI pertinents sont verts ;
- le depot est nettoye et les changements verifies sont committes.

## 9. Ce qu'il ne faut pas faire

- continuer a ajouter une classe de detection par nouveau cas de repas ;
- conclure qu'un meilleur benchmark modele garantit une meilleure application ;
- promouvoir Qwen3 Embedding ou OpenVINO sans reindex et sans mesures ;
- augmenter `topK` pour compenser aveuglement un mauvais ranking ;
- injecter tous les resultats dans le contexte ;
- laisser `llama.cpp` evincer silencieusement les instructions ;
- confondre RAM partagee Intel et VRAM dediee ;
- utiliser la memoire conversationnelle comme citation ;
- demander au meme juge LLM non calibre de certifier seul la qualite ;
- supprimer le legacy avant que le nouveau chemin ait gagne objectivement.

## 10. Decision de direction

La meilleure direction n'est ni « tout garder » ni « tout recommencer ».

Il faut :

1. conserver les invariants forts : outils, securite, EvidenceBundle, provenance, sources UI et verification mecanique ;
2. remplacer le coeur orchestrationnel nominal par une boucle LLM beaucoup plus courte ;
3. mettre l'ancien et le nouveau chemin en concurrence ;
4. qualifier Qwen3 selon ses propres templates, parametres et capacites ;
5. moderniser embeddings/reranking en exploitant l'iGPU Intel ;
6. n'accepter une refonte d'ingestion ou de retrieval que sur un benchmark page/source reel.

Cette approche permet de changer profondement ce qui doit l'etre sans perdre les garanties deja durement acquises.
