# A895-A897 — clôture OpenAI et transition contrôlée vers Runpod

Date : 29 septembre 2026  
État produit : `TESTE_NON_APPROUVE`

## Décision

La campagne OpenAI est close. A896 a reçu l'erreur fournisseur HTTP 429
`insufficient_quota` / `credit_balance_exhausted`. Aucun nouvel appel OpenAI ne
doit être tenté et aucun nouvel achat n'est proposé. Le reliquat de 4,7731210
USD affiché par le journal SAAIA est un reliquat d'autorisation locale, pas un
solde fournisseur : le fournisseur a fait foi et a refusé trois appels sans les
facturer.

La suite retenue est un passage progressif vers Runpod en conservant le même
pipeline produit. Le premier candidat est l'endpoint public
`Qwen/Qwen3-32B-AWQ`. Il permet de tester le protocole et le modèle sans créer
immédiatement une infrastructure GPU. Si ce candidat démontre la compatibilité
et une qualité utile, la campagne répétée passera sur un endpoint privé vLLM
Serverless Flex, puis sur l'infrastructure serveur client. Le code SAAIA reste
indépendant du lieu d'hébergement et parle au fournisseur avancé par un contrat
OpenAI-compatible.

## A895 : résultat complet et sûr, mais insuffisant

Profil : `config/openai-terra-staged-candidate-pilot.a895.json`  
Artefact privé :
`artifacts/reprise-pc-20260908/a895-terra-staged-candidate-pilot-20260929-173137`  
Job : `ef06f79a-56e0-42d2-931c-087026c3565c`

- 24 appels fournisseur et 39 opérations documentaires ;
- 237 360 tokens d'entrée, 9 899 de sortie, dont 1 418 tokens d'entrée cachés ;
- coût enregistré : 0,7088906 USD ;
- 54 candidats observés, 17 corps validés, 37 rejets ;
- capacité maximale d'affectation distincte : 15/20 ;
- manque réel : trois petits-déjeuners et deux collations ;
- Writer, Critic et la reprise du Critic ont terminé sans inventer ;
- l'erreur de convergence des identités vue en A894 n'est pas réapparue.

Le verdict sémantique est un échec fonctionnel sûr. Le système a expliqué
exactement le manque au lieu de fabriquer cinq réponses. Le goulot observé
était l'enveloppe de tours fournisseur : 24/24 appels consommés alors que neuf
opérations documentaires restaient disponibles. A896 a donc augmenté uniquement
les enveloppes d'appels de 24 à 32 et d'opérations de 48 à 64, sans modifier le
modèle, le corpus, les contrats de preuve, les prompts ou le cap de 1,10 USD.

## A896 : progrès du registre, interruption par le solde fournisseur

Profil : `config/openai-terra-staged-candidate-pilot.a896.json`  
Artefact privé :
`artifacts/reprise-pc-20260908/a896-terra-staged-candidate-pilot-20260929-174039`  
Job : `9c40ae1c-9362-431c-b200-bb99e06a55e6`

Avant l'interruption :

- 19 appels fournisseur réussis et trois refus gratuits ;
- 38 événements documentaires ;
- 137 863 tokens d'entrée, 10 863 de sortie, 1 418 tokens cachés ;
- coût enregistré : 0,4717236 USD ;
- dernier checkpoint durable : 62 candidats, 18 corps validés, 41 rejets et
  trois candidats dans un autre état ;
- couverture des corps validés : 3 petit-déjeuner, 9 déjeuner, 4 collation et
  11 dîner.

Le dixième appel Candidate Judge a été refusé trois fois. Les deuxième et
troisième tentatives du worker ont ensuite été refusées dès le Planner avec la
même erreur. Le Writer n'a donc jamais été appelé et aucune réponse sémantique
finale ne peut être évaluée. L'interface a renvoyé une erreur de limite
fournisseur sans exposer de réponse ou de source non validée.

Le journal persistant compte après A896 1 394 entrées et 50,2268790 USD, SHA-256
`F8225E76F8E86F5EA1EC6402407A83FE3EF028B39662D4B1CF58485B175527C0`.
Le coût A895 + A896 est de 1,1806142 USD. Le fait que le fournisseur ait épuisé
le crédit avant le plafond local de 55 USD indique que les cinq dollars annoncés
n'étaient pas tous disponibles pour cette organisation/projet/clé, ou qu'une
autre consommation les a absorbés. Le code ne doit pas substituer son journal
au solde réel du compte.

Postflight vérifié : environnement et configuration restaurés, sceau du corpus
inchangé, aucun secret dans les artefacts, backend temporaire arrêté, ports 5123
et 1234 libres, aucun job de validation non terminal.

## Ce que Terra a démontré et ce qui reste non prouvé

Terra n'est pas incapable de produire le planning : A891 et A893 ont atteint un
Writer 20/20. Les échecs suivants concernent la répétabilité de la constitution
du dossier de preuves et la stabilité de la boucle jusqu'au verdict final. A895
a produit une insuffisance vraie ; A896 a progressé mais a été interrompu avant
le Writer. Il n'existe donc toujours pas trois réussites consécutives sur le
même état figé, et le produit ne peut pas être approuvé.

## A897 : profil Runpod prêt, dépense bloquée

Profil : `config/runpod-staged-candidate-pilot.a897.json`

Le profil propose :

- endpoint public `https://api.runpod.ai/v2/qwen3-32b-awq/openai/v1` ;
- modèle exact `Qwen/Qwen3-32B-AWQ`, AWQ 4 bits, contexte 32 768 ;
- protocole `chat-completions` ;
- topologie agent, workspace, Navigator/Judge, Candidate Explorer staged,
  Writer et Critic conservés ;
- 32 appels fournisseur et 64 opérations documentaires au maximum ;
- budget proposé de 5 USD, soft stop 4 USD, hard stop 4,80 USD et 3,20 USD par
  job ;
- uniquement le cas meal-grid, une répétition.

Le statut est volontairement
`PROPOSED_PENDING_USER_CONFIRMATION`. Le runner refuse toute exécution v2 tant
que ce statut n'est pas devenu `AUTHORIZED` dans un commit revu. Les autres
portes restent obligatoires : `-Execute`, autorisation de transmission externe,
clé sécurisée et, pour le meal-grid, chemin d'environnement serveur. Un dry-run
n'accède à aucune clé et n'effectue aucun appel.

Le profil historique v1 reste compatible. Le wrapper Runpod transporte
maintenant toutes les options staged vers le runner fournisseur commun, y
compris l'enveloppe de preuve de 64 000 caractères. Runpod n'utilise pas l'API
Responses : sa compatibilité documentée passe par `/chat/completions`, et le
nom du modèle doit correspondre au modèle servi ou à son override.

Sources officielles :

- compatibilité OpenAI vLLM :
  https://docs.runpod.io/serverless/vllm/openai-compatibility
- endpoint public Qwen3 32B AWQ et tarif :
  https://docs.runpod.io/public-endpoints/models/qwen3-32b
- configuration Qwen3 et estimation VRAM :
  https://docs.runpod.io/serverless/vllm/configuration
- fonctionnement de la facturation Serverless :
  https://docs.runpod.io/serverless/pricing

## Coût probable et ordre d'exécution

Runpod annonce 10 USD par million de tokens sur l'endpoint public. Au même
volume que A895, 247 259 tokens totaux coûteraient environ 2,47259 USD. Le
fragment A896, 148 726 tokens, représenterait environ 1,48726 USD. Un budget de
5 USD suffit donc pour une sonde courte puis vraisemblablement un seul vrai
meal-grid complet, avec marge de sécurité ; il ne suffit pas à établir trois
répétitions complètes.

Ordre préparé :

1. autoriser un plafond Runpod concret et importer la clé dans le magasin
   sécurisé SAAIA ;
2. exécuter la sonde de deux appels synthétiques et vérifier authentification,
   modèle exact, format `chat-completions` et comptage d'usage ;
3. après revue de la sonde, exécuter un seul meal-grid A897 ;
4. juger séparément mécanique, qualité sémantique et coût réel ;
5. si le modèle est apte, créer un endpoint privé vLLM Flex pour les répétitions
   et mesurer la facturation GPU ;
6. reprendre ensuite les répétitions, le holdout aveugle et WinUI avant toute
   promotion produit.

## Endpoint privé après la sonde publique

Le fichier
`config/runpod-serverless-vllm-qwen3-32b-awq.template.json` prépare le déploiement
privé avec `ENABLE_AUTO_TOOL_CHOICE=true`, `TOOL_CALL_PARSER=hermes`,
`QUANTIZATION=awq`, `MAX_MODEL_LEN=32768` et le modèle exact. La documentation
Runpod recommande 80 à 180 Go pour la classe 30B-70B tout en indiquant qu'INT4
utilise environ 0,5 octet par paramètre et que le KV cache consomme encore de la
VRAM. La première mesure privilégie donc un GPU 80 Go pour éviter un faux échec
OOM ; un GPU 48 Go ne sera retenu qu'après preuve de chargement et de contexte.

Serverless facture de la mise en route du worker jusqu'à son arrêt complet,
incluant chargement du modèle, exécution et délai d'inactivité. Le garde actuel
par tokens ne peut pas prétendre plafonner cette facture. Avant un endpoint
privé, il faut un compteur GPU-seconde, une procédure de scale-to-zero et un
contrôle externe de la dépense Runpod. Le template reste donc non déployé.

## Verdict actuel

- OpenAI : campagne close sur épuisement réel du crédit ;
- architecture agentique : fonctionnelle et sûre, mais répétabilité du planning
  non approuvée ;
- Runpod : code, profil, budget proposé et template prêts à tester à sec ;
- dépense Runpod : aucune ;
- prochain verrou externe : un plafond Runpod explicite et une clé importée ;
- produit : `TESTE_NON_APPROUVE`.
