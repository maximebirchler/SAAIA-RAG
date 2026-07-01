# Observation du fonctionnement d'un agent unique

Date: 2026-06-30
Repo: `C:\Users\MBirchler\Desktop\ecom\SAAIA\GIT\RAG`
Agent observe: `019f1a20-98be-7fc0-b4a5-030e75e0f134` (`Kant`)
Mission donnee: audit read-only client + backend dans une seule mission.

## Correction de cadrage

Ce rapport ne vise pas principalement a lister les hardcodings cuisine/repas.
Le rapport technique produit par l'agent existe comme sortie secondaire.
Le sujet principal ici est: comment l'agent a fonctionne pendant une mission large, comment il a organise son travail, ce qu'il a garde en memoire operationnelle, comment il a choisi ses outils, et ce que cela nous apprend pour SAAIA si le LLM doit devenir l'orchestrateur principal.

Limite importante: je n'ai pas acces a la pensee cachee de l'agent. Ce que j'observe est donc uniquement:
- ses messages operationnels publics,
- l'ordre de ses etapes,
- les points qu'il dit mettre de cote puis reprendre,
- ses hypotheses de travail explicites,
- son rapport final,
- son respect ou non des contraintes donnees.

## Resume court

L'agent n'a pas traite client et backend comme deux audits separes. Il a gere une seule mission large en creant une carte mentale observable:

1. figer le contexte reel du repo;
2. separer les couches du probleme;
3. explorer large avec des recherches rapides;
4. lire les points centraux;
5. formuler deux hypotheses concurrentes;
6. verifier ces hypotheses ligne par ligne;
7. revenir aux tests pour comprendre ce qui protege et ce qui fige;
8. produire une synthese.

Le comportement interessant n'est pas seulement qu'il a utilise `rg` et `git`. C'est surtout qu'il a garde une memoire de travail explicite: "runtime backend", "runtime client", "tests/fixtures", puis deux hypotheses H1/H2. Il n'a pas essaye de tout resoudre en une seule passe.

Faiblesse observee: l'agent a bien collecte et structure, mais il est reste bloque a la phase de finalisation. Il a annonce "je finalise", puis n'a pas rendu son rapport avant que je l'interrompe pour lui demander de sortir ce qu'il avait deja. Pour un orchestrateur produit, cela montre qu'il faut une mecanique de checkpoints/finalisation, pas seulement une bonne capacite de recherche.

## Chronologie observable

### 1. Il a commence par cadrer la mission

Premier message operationnel:

> "Je vais traiter ca comme un audit statique borne: d'abord memoire + etat repo, puis une passe d'inventaire, puis des lectures ciblees..."

Observation:
- Il ne s'est pas jete directement sur le code.
- Il a reformule une strategie d'execution.
- Il a limite volontairement le scope: audit statique, read-only, pas Docker, pas serveur local.
- Il a annonce comment il allait rendre son travail observable: commandes, fichiers, lignes, hypotheses.

Enseignement pour SAAIA:
- Le LLM orchestrateur doit commencer par expliciter un mode d'action operationnel: recherche simple, recherche multiple, exploration categories/documents, clarification ou reponse directe.
- Cette decision doit etre tracee comme une etape du deroule, pas seulement cachee dans le prompt.

### 2. Il a fige l'etat du monde avant d'analyser

Message:

> "Premier constat utile: le depot est deja sale... Je vais donc d'abord isoler ce qui est dans les diffs non commites..."

Observation:
- Il a considere l'etat courant comme potentiellement different de sa memoire.
- Il a priorise `git status`, `git diff`, commits recents.
- Il a identifie que les diffs actuelles etaient probablement importantes pour comprendre le probleme.

Enseignement pour SAAIA:
- Le LLM ne doit pas raisonner seulement depuis son souvenir ou depuis la question utilisateur.
- Pour le RAG, l'equivalent est: inspecter d'abord le corpus disponible, les categories, les surfaces de recherche et les premiers hits avant de choisir une strategie definitive.

### 3. Il a utilise la memoire comme repere, pas comme preuve

Message:

> "Je mets de cote pour l'instant les anciens rollouts sauf comme repere..."

Observation:
- Il n'a pas ignore la memoire.
- Il ne l'a pas non plus traitee comme verite finale.
- Il l'a utilisee pour orienter les premiers fichiers a inspecter, puis il est revenu au repo actuel.

Enseignement pour SAAIA:
- Une memoire LLM est utile, mais elle doit etre separee des preuves.
- Memoire = indices, strategies qui ont marche, echecs precedents, vocabulaire probable.
- Preuve = sources actuelles, resultats RAG, pages, chunks, categories, dates.

### 4. Il a decoupe le probleme en couches

Message:

> "Je separe maintenant trois couches: code runtime client/backend, tests qui figent un comportement, et artefacts/fixtures de validation..."

Observation:
- Il n'a pas classe tous les matches `rg` de la meme maniere.
- Il a cree une taxonomie temporaire pour eviter le bruit.
- Il a distingue ce qui fait partie du produit de ce qui sert a tester le produit.

Enseignement pour SAAIA:
- Pour une recherche documentaire, le LLM doit pouvoir creer une taxonomie temporaire:
  - sources directes,
  - sources de contexte,
  - navigation/index/sommaire,
  - doublons,
  - candidats faibles,
  - candidats utiles pour une autre sous-question.
- Cette taxonomie doit etre dynamique et generique, pas codee en dur pour "cuisine".

### 5. Il a repere une asymetrie avant de conclure

Message:

> "Point de bascule: cote runtime, le backend ne semble pas avoir de logique repas massive... la densite dangereuse est surtout dans ToolAgentOrchestrator..."

Observation:
- Il a cherche dans client et backend, mais n'a pas force une symetrie artificielle.
- Il a reduit son attention vers la zone qui semblait porter le risque.
- Il a garde backend en contexte sans continuer a tout lire a parts egales.

Enseignement pour SAAIA:
- Le LLM orchestrateur doit pouvoir changer son allocation d'effort.
- Si une categorie ou un document ne donne pas de bons candidats, il doit pouvoir pivoter.
- Il ne faut pas imposer une repartition fixe des recherches; il faut tracer pourquoi l'effort se deplace.

### 6. Il a formule deux hypotheses concurrentes

Message:

> "Je garde deux hypotheses en memoire de travail: H1 le domaine cuisine est cantonne... H2 cette detection a contamine le planner generique..."

Observation:
- C'est le moment le plus important de l'audit.
- L'agent n'a pas seulement accumule des observations.
- Il a cree deux modeles explicatifs concurrents, puis il a lu le code pour trancher.

Enseignement pour SAAIA:
- C'est probablement ce qu'on attend du LLM orchestrateur principal:
  - construire des hypotheses,
  - choisir des recherches pour les confirmer ou les infirmer,
  - ne pas suivre aveuglement la premiere recherche,
  - noter ce qu'il cherche a verifier.
- Le code ne doit pas remplacer cette pensee par une cascade d'heuristiques fixes.

### 7. Il a cartographie le flux avant de juger les details

Message:

> "Je viens de confirmer le squelette d'orchestration: un routeur LLM JSON decide d'un plan..."

Observation:
- Avant de juger les fonctions isolees, il a reconstitue le pipeline global.
- Il a identifie les points de decision: router LLM, corrections code, retrieval, evidence planner, writer, finalizers.
- Ensuite seulement il a zoome sur les mecanismes qui corrigent le LLM.

Enseignement pour SAAIA:
- L'orchestrateur doit maintenir une carte du workflow:
  - ce que l'utilisateur demande,
  - ce que les outils savent faire,
  - ce qui a deja ete cherche,
  - ce qui manque,
  - ce qui est assez fiable pour rediger.
- Sans cette carte, il compensera avec des heuristiques locales.

### 8. Il a distingue "outiller le LLM" et "corriger le LLM"

Message:

> "Le code donne bien un role au LLM planner, mais il est entoure de beaucoup de rails deterministes..."

Observation:
- L'agent a identifie que les garde-fous ne sont pas tous mauvais.
- Il a distingue les rails utiles des rails qui imposent une strategie.
- Il n'a pas conclu "tout le code est mauvais"; il a demande si les rails etaient generiques ou specialisateurs.

Enseignement pour SAAIA:
- Le LLM a besoin de rails, mais les rails doivent porter sur la fiabilite:
  - sources suffisantes,
  - absence de doublons,
  - alignement item-source,
  - couverture des axes demandes,
  - refus des sources decoratives.
- Les rails ne doivent pas porter sur une solution metier fixe du type "si plan semaine, alors petit-dejeuner/diner/souper".

### 9. Il a identifie une condition d'activation comme point critique

Message:

> "Le facteur cle est la condition d'activation: si elle est etroite... si elle attrape des plannings generiques, c'est dangereux."

Observation:
- Il ne s'est pas arrete a "il y a des termes cuisine".
- Il a compris que le vrai sujet est: quand cette logique s'active-t-elle?
- C'est un raisonnement de controle de flux, pas seulement une recherche de mots.

Enseignement pour SAAIA:
- Dans le produit, la question n'est pas seulement "quelles heuristiques existent?"
- La question est "qui decide de les activer?"
- Idealement: le LLM choisit une strategie; le code valide que la strategie est autorisee et source-backed.

### 10. Il a connu une faiblesse de finalisation

Observation:
- Apres plusieurs messages "je finalise", l'agent n'a pas rendu son rapport final dans le delai attendu.
- J'ai du lui envoyer une instruction d'interruption: "rends maintenant le rapport final avec ce que tu as deja".
- Apres cette instruction, il a sorti un rapport complet en environ 90 secondes.

Ce que ca revele:
- L'agent savait quoi dire.
- Le blocage semblait etre dans la phase de synthese/finalisation, pas dans la recherche.
- Il avait besoin d'un checkpoint externe pour accepter de livrer un rapport imparfait mais utile.

Enseignement pour SAAIA:
- Un orchestrateur autonome doit avoir des checkpoints de sortie:
  - "j'ai assez pour une reponse partielle",
  - "je continue une passe supplementaire",
  - "je m'arrete car le gain marginal est faible",
  - "je rends une reponse avec limites".
- Pour ton exigence "on ne doit pas croire qu'il est coince alors qu'il fait autre chose", il faut tracer les phases longues de synthese, pas seulement les appels RAG.

## Mode de gestion de taches observe

L'agent a gere sa mission avec une structure implicite mais visible:

1. Backlog initial:
   - etat repo,
   - inventaire large,
   - runtime client,
   - runtime backend,
   - tests,
   - diff recent,
   - rapport.

2. Memoire de travail:
   - trois couches: runtime, tests, fixtures;
   - deux hypotheses H1/H2;
   - un point de vigilance: ne pas confondre amelioration de routage/logs avec alignement source-reponse.

3. Strategie d'exploration:
   - commencer large avec `rg`;
   - reduire vers les fichiers centraux;
   - revenir aux tests apres le runtime;
   - utiliser le diff pour comprendre ce qui est recent.

4. Strategie de decision:
   - ne pas conclure depuis un seul match;
   - regarder la condition d'activation;
   - classer les elements par risque;
   - separer garde-fous utiles et contraintes trop metier.

5. Strategie de communication:
   - messages courts et frequents;
   - annonce des bascules;
   - formulation d'hypotheses explicites;
   - pas de chain-of-thought cachee, mais un journal operationnel.

## Ce que l'agent a bien fait

- Il a respecte le scope read-only.
- Il n'a pas lance Docker ni serveur local.
- Il a audite client et backend dans une seule mission.
- Il a garde le contexte transversal: backend, client, tests, docs.
- Il a utilise la memoire comme orientation, pas comme preuve.
- Il a formule des hypotheses explicites.
- Il a distingue runtime produit, tests et fixtures.
- Il a cherche les conditions d'activation plutot que seulement les mots interdits.
- Il a transforme un audit large en carte operationnelle comprehensible.

## Ce que l'agent a moins bien fait

- Il a fini par produire surtout un rapport technique sur les hardcodings, parce que la mission contenait aussi beaucoup de demandes techniques.
- Il n'a pas spontanement fourni un rapport meta sur son propre fonctionnement, alors que c'etait une intention utilisateur importante.
- Il est reste bloque a la derniere phase jusqu'a intervention.
- Il n'a pas cree de livrable durable car il etait explicitement en read-only.
- Ses messages montrent sa methode, mais pas assez sa "memoire active" sous forme de tableau stable.

## Traduction directe pour SAAIA

Si le LLM doit etre orchestrateur principal, il lui faut au minimum quatre espaces distincts:

1. Memoire de travail de la question courante:
   - objectif utilisateur,
   - contraintes explicites,
   - hypotheses,
   - axes/slots demandes,
   - recherches deja faites,
   - resultats utiles,
   - resultats rejetes,
   - lacunes restantes.

2. Journal de recherche:
   - pourquoi telle recherche a ete lancee,
   - outil utilise,
   - requete exacte,
   - categorie ou scope,
   - nombre/type de resultats,
   - decision apres resultats.

3. Inventaire de candidats:
   - candidat concret,
   - source,
   - page/chunk,
   - role possible dans la reponse,
   - score de legitimite,
   - doublon ou non,
   - raison de rejet si rejete.

4. Etat de finalisation:
   - assez pour repondre,
   - besoin d'une recherche supplementaire,
   - besoin d'une clarification,
   - reponse partielle possible,
   - refus strict necessaire.

Ce qui est important: ces espaces doivent etre generiques. Ils ne doivent pas connaitre "cuisine" ou "recette". Une demande de plan repas, un planning de maintenance, un planning de formation ou une comparaison juridique doivent utiliser la meme architecture mentale.

## Difference entre LLM orchestrateur et heuristiques

Ce que l'agent a fait ressemble a une orchestration LLM saine:

- il choisit un ordre de recherche;
- il cree des hypotheses;
- il change d'allocation d'effort;
- il utilise les outils selon ce qu'il apprend;
- il garde des notes operationnelles;
- il produit une synthese.

Une heuristique codee ferait plutot:

- si mot X alors categorie Y;
- si plan semaine alors requetes fixes;
- si pas assez de hits alors fallback fixe;
- si source partielle alors message standard.

La philosophie a garder pour SAAIA:
- le code fournit les outils, les garde-fous, les budgets, les traces et les validateurs;
- le LLM choisit la strategie, les recherches, les pivots et la structure finale;
- le code verifie que le LLM ne ment pas, ne recycle pas les sources, ne cite pas du bruit, et ne remplit pas les trous avec de l'imagination.

## Balises et observabilite a ajouter cote produit

Pour reproduire ce qu'on a pu observer chez l'agent, SAAIA devrait tracer:

- `orchestrator.goal_detected`: objectif reformule.
- `orchestrator.strategy_selected`: direct_answer, basic_search, multi_search, category_exploration, clarification.
- `orchestrator.working_memory.updated`: axes, hypotheses, contraintes, lacunes.
- `orchestrator.search_plan.created`: requetes prevues et raison de chaque requete.
- `orchestrator.search_result.reviewed`: ce que la recherche a change dans le plan.
- `orchestrator.hypothesis.created`: hypothese ou piste de recherche.
- `orchestrator.hypothesis.resolved`: confirmee, infirmee, gardee en attente.
- `orchestrator.candidate.accepted`: candidat utilisable avec source.
- `orchestrator.candidate.rejected`: raison lisible du rejet.
- `orchestrator.finalization.started`: passage de recherche a synthese.
- `orchestrator.finalization.checkpoint`: "je synthetise", "il manque X", "je peux repondre partiellement".
- `orchestrator.answer_scope.decided`: complet, partiel, insuffisant, clarification.

Ces balises repondent directement a ton exigence: on ne doit jamais se demander "est-il coince ou fait-il autre chose?".

## Complement: observation sur la question RAG du plan de repas

Agent observe: `019f1a32-40f6-79f0-930e-812c7ab4cb01` (`Hilbert`)
Question donnee:

> J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi en y mettant petit-dejeuner, diner, souper et gouter/collation. Je veux une reponse user-friendly avec des vraies bonnes sources, sans sources inutiles, sans doublons, et sans inventer de recettes ou d'elements non soutenus par les documents.

L'objectif de cette observation n'etait pas seulement d'obtenir une bonne reponse finale. Le but etait de voir comment un agent gere une demande RAG large, multi-slot, avec exigence de sources propres.

### Limite volontaire

On ne peut pas demander a un agent de reveler sa chaine de pensee cachee mot pour mot. A la place, l'agent a ete missionne pour fournir un journal operationnel public:

- hypotheses visibles;
- choix de strategie;
- recherches lancees;
- raisons courtes;
- resultats obtenus;
- pivots;
- candidats gardes ou rejetes;
- lacunes;
- decision de finalisation.

C'est cette couche observable qui est pertinente pour SAAIA.

### Ce qu'il a fait dans l'ordre

1. Il n'a pas commence par chercher "plan repas semaine" aveuglement.
   Il a d'abord verifie le contexte repo, les endpoints disponibles, le serveur distant et les contrats RAG.

2. Il a identifie les endpoints reels:
   - `/rag/categories`;
   - `/rag/search`;
   - `/rag/query`.

3. Il a verifie l'acces:
   - `/health` OK;
   - Swagger OK;
   - `/rag/categories` sans cle = `401 Unauthorized`;
   - cle API retrouvee dans la configuration locale, puis utilisee masquee.

4. Il a recupere les categories et constate que `Cuisine` existe, mais il n'a pas traite cela comme une categorie codee en dur.
   Il a d'abord annonce vouloir observer le bruit hors categorie, puis borner a `Cuisine` si les resultats le justifiaient.

5. Il a choisi une strategie de recherche large controlee:
   - question exacte;
   - requetes par axes explicites;
   - limitation volontaire du `topK`, `maxPerDoc` et `maxPerPage`;
   - `researchMode=source_exploration`;
   - `includeResearchSurfaces=true`.

6. Le premier batch large a depasse 240 secondes.
   Il n'a pas continue a boucler. Il a transforme le timeout en information de strategie et a reduit la charge.

7. Il est passe a des appels isoles:
   - question exacte, categorie `Cuisine`: 37.0 s, 5 resultats;
   - `petit-dejeuner dejeuners brunch recettes`: 45.3 s, 1 resultat;
   - `diner souper repas complet plat principal recettes`: 39.2 s, 5 resultats;
   - `gouter collation encas snack recettes`: 45.4 s, 0 resultat;
   - `plan repas semaine lundi mardi mercredi jeudi vendredi petit-dejeuner diner souper gouter collation`: 38.6 s, 5 resultats profil;
   - pivot `collation fruit yaourt`: 46.6 s, 1 resultat;
   - pivot `plats principaux recettes poulet poisson legumes vegetarien soupe`: 57.4 s, 8 resultats.

8. Il a observe que les recherches etaient lentes mais exploitables.
   Le temps typique etait entre 37 et 57 secondes par appel, avec `profileMs` souvent dominant.

9. Il a constitue un inventaire de candidats.
   Il a garde des items concrets avec source et page:
   - `SMOOTHIE VERT`, `Je_cuisine_simplement.pdf`, p.38;
   - legumes/lentilles au cari, `Je_cuisine_simplement.pdf`, p.43;
   - boulettes de poulet, `chefbot_livre_de_recettes_fr.pdf`, p.102;
   - poisson blanc vapeur, `chefbot_livre_de_recettes_fr.pdf`, p.106;
   - estouffade du Sud, `chefbot_livre_de_recettes_fr.pdf`, p.107;
   - tomates farcies, `30-recettes-preferees-des-francais.pdf`, p.17;
   - couscous royal, `30-recettes-preferees-des-francais.pdf`, p.11;
   - soupe chinoise au poulet, document Moulinex, p.46;
   - soupe de legumes, document Moulinex, p.47;
   - chaud-froid de poulet, `si-on-cuisinait.pdf`, p.55;
   - fondue chinoise, `si-on-cuisinait.pdf`, p.62.

10. Il a rejete ou marque comme partiels certains resultats:
    - page de presentation `chefbot`, pas une recette;
    - introduction generale `si-on-cuisinait`, pas un candidat;
    - profils documentaires utiles pour s'orienter, mais pas suffisants comme preuve finale;
    - sources OCR faibles;
    - doublons de chemin `Cuisine/...` vs `Cuisine/PDF/...`.

11. Il n'a pas force le slot collation.
    La requete stricte `gouter collation encas snack recettes` a donne 0 resultat.
    Il a pivote vers `collation fruit yaourt`, puis a conclu que le support etait minimal: une source parle de collation et fruits, mais pas assez de gouters distincts.

12. Il n'a pas produit un faux plan complet.
    Sa reponse finale proposee dit explicitement qu'il y a assez de sources pour plusieurs repas, mais pas assez pour remplir honnetement 20 cases uniques sans repetition ni invention.

### Ce que son fonctionnement revele

L'agent s'est comporte comme un orchestrateur plutot que comme une fonction de recherche:

- il a verifie les outils disponibles avant de chercher;
- il a traite l'authentification comme condition d'acces aux sources;
- il a observe les categories avant de les utiliser;
- il a choisi une strategie multi-axes;
- il a ajuste le budget de recherche a la latence reelle;
- il a transforme un timeout en decision de pivot;
- il a distingue preuve concrete, profil documentaire, bruit et doublon;
- il a accepte une reponse partielle plutot qu'une reponse jolie mais fausse.

Ce comportement est tres proche de la philosophie souhaitee:

- le LLM choisit la strategie;
- le code fournit les outils et les garde-fous;
- les sources determinent ce qui peut etre affirme;
- les lacunes sont nommees au lieu d'etre remplies par invention.

### Ce qu'il a mieux fait que le flux produit actuel

1. Il a explicite sa strategie avant les recherches.
   Dans l'UI, on voit souvent les traces techniques, mais pas assez "pourquoi cette recherche maintenant".

2. Il a affiche sa memoire de travail:
   objectif, axes, contraintes, hypotheses, lacunes.

3. Il a garde une matrice mentale de couverture:
   petit-dejeuner, diner/souper, collation.

4. Il a rejete les profils documentaires comme preuves finales.
   C'est essentiel: navigation/profile = aide a chercher, pas source finale.

5. Il a juge la suffisance avant redaction.
   Il n'a pas attendu le writer pour decouvrir qu'il manquait des gouters.

6. Il a deduit qu'un plan complet serait mensonger si on exige 20 cases uniques.
   C'est exactement le genre de decision que le LLM doit pouvoir prendre.

### Ce qu'il a moins bien fait ou ce qui reste limite

1. Il a simule un `multi_search` avec plusieurs appels `/rag/search`, car il n'a pas utilise un endpoint backend multi-search direct.
   Cela rend l'orchestration plus lente et moins consolidee.

2. Il a peu exploite la navigation comme etape separee.
   Il a utilise les profils et categories, mais un vrai orchestrateur produit devrait pouvoir faire:
   categories -> navigation -> pages candidates -> retrieval concret.

3. Le serveur etait lent.
   Des appels isoles prenaient 37 a 57 secondes, et le batch large a depasse 240 secondes.

4. La recherche collation a montre une vraie fragilite lexicale.
   Le slot "gouter/collation" ne devrait pas dependre d'une seule requete stricte.

5. Il n'a pas pu produire un plan final complet fiable.
   C'est une bonne decision du point de vue source-backed, mais cela montre que le retrieval/candidate inventory n'est pas encore assez robuste.

### Balises supplementaires deduites de ce cas

Les balises deja proposees restent bonnes, mais ce cas ajoute des besoins tres concrets:

- `orchestrator.auth.checked`: acces serveur/source verifie.
- `orchestrator.category.probed`: categories disponibles lues.
- `orchestrator.category.selected`: categorie retenue avec raison.
- `orchestrator.query.batch.started`: batch large lance.
- `orchestrator.query.batch.timeout`: batch trop lent, raison du pivot.
- `orchestrator.query.reduced`: passage a appels isoles.
- `orchestrator.latency.observed`: temps par requete et phase dominante.
- `orchestrator.slot.coverage.updated`: couverture par axe utilisateur.
- `orchestrator.source.kind`: evidence concrete, profile, navigation, bruit, doublon.
- `orchestrator.source.dedup_key`: cle de dedoublonnage normalisee.
- `orchestrator.answer.partial_allowed`: decision de reponse partielle.
- `orchestrator.answer.refused_full_grid`: refus de remplir une grille complete faute de sources.

### Enseignement produit

Pour cette question, la bonne reponse n'est pas seulement de chercher plus large.
La bonne reponse est d'avoir une boucle generique:

1. decomposer la demande en axes visibles;
2. choisir les outils de recherche;
3. observer categories et bruit;
4. lancer des requetes compactes par axe;
5. tenir une matrice de couverture;
6. dedoublonner les sources;
7. rejeter navigation/profil comme preuve finale;
8. decider si la reponse peut etre complete ou seulement partielle;
9. rediger seulement avec les candidats valides.

Cette boucle doit etre generique. Ici elle s'applique aux repas, mais la meme structure doit fonctionner pour un planning de maintenance, une formation, un comparatif juridique ou un plan d'actions.

## Ce que j'en conclus

Les deux observations d'agents convergent. Un agent efficace ne fonctionne pas comme une suite d'heuristiques fixes; il fonctionne comme une boucle d'orchestration:

1. cadrage;
2. verification des outils;
3. decomposition de la demande;
4. hypotheses;
5. recherches adaptees;
6. inventaire des candidats;
7. rejet du bruit;
8. evaluation de la couverture;
9. finalisation complete ou partielle.

L'agent a montre une bonne facon de travailler pour un probleme large: une seule entite garde le contexte global, decoupe, explore, formule des hypotheses, pivote et recoupe. C'est proche de ce que tu veux pour SAAIA.

Mais il a aussi montre deux faiblesses a corriger dans le produit:

1. sans support externe, la memoire de travail reste implicite et fragile;
2. sans checkpoint de finalisation, un LLM peut continuer a "finir" trop longtemps alors qu'il a deja assez pour livrer.
3. sans matrice de couverture source-backed, un plan multi-slot peut etre soit incomplet, soit artificiellement rempli.

Donc la bonne direction n'est pas de coder plus d'heuristiques metier. La bonne direction est de donner au LLM:
- des outils de recherche generiques,
- une memoire de travail explicite,
- un journal de recherche visible,
- un inventaire de candidats,
- une matrice de couverture des axes demandes,
- des validateurs source-backed,
- des checkpoints de finalisation,
- et des traces comprehensibles pour l'utilisateur.
