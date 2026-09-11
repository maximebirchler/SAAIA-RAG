# Analyse de latence du RAG simple

Date : 2026-07-29  
Statut : mesures intermédiaires, chantier actif  
Machine : Quadro P520 4 Gio, 32 Gio de RAM, Intel UHD à mémoire partagée  
Modèle principal : `Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf`  
Runtime : llama.cpp CUDA `b10098`

## Objet

Cette note suit les expériences réalisées sur la question réelle :

> Donne-moi un dessert simple à faire.

Elle ne remplace pas la campagne finale. Son but est de conserver les mesures,
les échecs utiles et les décisions afin d’éviter de répéter des variantes déjà
réfutées.

## Critères de qualité

Un run n’est pas considéré comme réussi uniquement parce que le test
d’intégration est vert. La lecture humaine vérifie aussi :

- que le routeur ne crée pas de critère absent de la demande ;
- que le candidat est réellement un dessert et pas un conseil, un composant ou
  un titre ;
- que la preuve montre une méthode actionnable ;
- que l’identité exacte de la recette est conservée ;
- qu’aucune recette voisine de la même page n’est mélangée ;
- que les composants et étapes essentiels visibles sont fidèlement restitués ;
- que la citation pointe vers le bon fichier et la bonne page.

## Référence correcte

La réponse de référence actuellement obtenue est :

> Dessert simple : nid d’abeille. Tapisser un plat en verre de papier
> parchemin, mélanger 250 ml de sucre, 15 ml de sirop de maïs et 45 ml d’eau,
> chauffer jusqu’à coloration, ajouter 2,5 ml de bicarbonate de soude, verser
> et laisser reposer 45 minutes [E5].

Provenance :

- fichier : `Je_cuisine_simplement.pdf` ;
- page : 53 ;
- fenêtre de source ancrée sur le chunk de la recette ;
- citation vérifiée mécaniquement.

## Résultats

| Run | Variante | Temps pipeline | Verdict humain |
|---|---|---:|---|
| v55 | 8K, KV f16, fenêtres ancrées | 65,7 s | Correct et complet |
| v56 | jugement et réponse courte fusionnés | 45,6 s | Source correcte, méthode trop pauvre |
| v57 | réponse actionnable forcée dans 144 tokens | interrompu | Sorties tronquées et reprise de boucle |
| v58 | LLM choisit réponse courte, writer ou recherche | 69,4 s | Correct et complet |
| v59 | routeur et preuves trop compactés | interrompu | Multiples approfondissements, gain annulé |
| v60 | routeur trop compact, preuve complète | 54,1 s | Échec sémantique malgré test vert |
| v61 | règle subjective restaurée | 54,6 s | Correct, mais caches partiellement chauds |
| v62 | 4K, KV f16, run froid | 73,2 s | Correct et complet |
| v63 | 4K, KV q8_0, run froid | 79,0 s | Procédure incohérente |
| v64 | 8K, KV q8_0, run froid | 78,5 s | Procédure incohérente |
| v65 | 4K, KV f16, ngram-simple, froid | 74,4 s | Correct, mais plus lent |

## Défauts découverts

### Mélange de recettes d’une même page

Une clé de fenêtre limitée au fichier et à la page fusionnait des recettes
distinctes. Le nid d’abeille a ainsi été mélangé avec le crumble voisin.

Correction :

- identité de fenêtre ancrée sur le chunk demandé ;
- ordre réel par `chunkIndex` ;
- titre, composants et méthode présentés ensemble au LLM ;
- provenance exacte conservée par chunk.

### Réponse combinée trop courte

Le run v56 a montré qu’un appel unique peut répondre rapidement, mais Qwen3 a
compressé une recette à sa première action. Le run v57 a ensuite montré
qu’augmenter les exigences sans augmenter le contrat provoque une sortie
tronquée.

Décision :

- `choose_evidence_answer` pour une réponse qui tient réellement en une ou deux
  phrases sans procédure ;
- `choose_evidence_for_writer` lorsqu’une méthode, plusieurs composants ou
  plusieurs étapes sont nécessaires ;
- `continue_evidence_research` lorsque la preuve est insuffisante ;
- le LLM choisit la voie ; le code ne décide pas du type de réponse.

### Compaction sémantique excessive du routeur

Le run v60 a transformé « simple » en « avec ingrédients courants ». Cette
qualité n’était pas demandée. La recherche a alors remonté un conseil Moulinex,
et le pipeline a produit une crème au beurre insuffisamment établie.

Correction :

- les qualités subjectives restent dans les mots et le sens de l’utilisateur ;
- elles ne sont jamais remplacées par un temps, un nombre d’étapes, des
  ingrédients courants ou un autre proxy ;
- une demande d’action devient un besoin de méthode visible ;
- l’exemple de reformulation générique est conservé dans le prompt du routeur.

Le run v61 confirme :

- besoin : `une methode visible permettant de faire un dessert simple` ;
- requête : `recette dessert simple preparation etapes` ;
- aucune qualité ajoutée.

### KV q8_0

Le KV q8_0 libère de la VRAM, mais les deux runs 4K et 8K ont produit une
procédure qui mélange le bicarbonate avec les ingrédients puis demande de
l’ajouter une seconde fois.

Mesures :

- 4K/f16 : environ 3 385 MiB utilisés, 629 MiB libres ;
- 4K/q8_0 : environ 3 119 MiB utilisés, 895 MiB libres ;
- 8K/q8_0 : environ 3 443 MiB utilisés, 571 MiB libres ;
- 8K/f16 : environ 3 903 MiB utilisés, 111 MiB libres.

Décision :

- q8_0 n’est pas qualifié comme profil nominal pour ce modèle ;
- il ne doit pas être promu sur la seule base de la VRAM économisée.

### Spéculation n-grammes

`ngram-simple` a accepté seulement 3 jetons sur 48 propositions pendant le
jugement et n’a pas accéléré le rédacteur. Le run est environ 1,2 seconde plus
lent que le témoin 4K/f16.

Décision :

- ne pas intégrer cette variante ;
- l’option expérimentale ajoutée au lanceur a été retirée immédiatement.

La documentation officielle llama.cpp indique que cette variante est surtout
adaptée aux séquences déjà répétées dans l’historique, par exemple la
réécriture de code :

<https://github.com/ggml-org/llama.cpp/blob/master/docs/speculative.md>

## Décomposition froide 4K/f16

Run v62 :

| Étape | Prompt | Sortie | Temps serveur |
|---|---:|---:|---:|
| Routeur | 1 744 tokens | 61 tokens | 21,8 s |
| Juge de preuves | 2 203 tokens | 47 tokens | 26,6 s |
| Rédacteur | 1 073 tokens | 93 tokens | 22,3 s |

Le backend de recherche et les fenêtres de contexte représentent seulement
une petite fraction du total. Le coût dominant est le LLM local.

## Plancher physique actuel

Les trois sorties totalisent environ 201 tokens. À environ 7,2 tokens/s, leur
génération seule coûte presque 28 secondes, avant même :

- l’évaluation des prompts ;
- la recherche backend ;
- les transitions ;
- la vérification.

La cible de 30 secondes ne peut donc pas être atteinte par une réduction
supplémentaire du contexte uniquement.

## Direction architecturale à mesurer

La prochaine variante structurante doit réduire le nombre de sorties LLM tout
en conservant le rôle d’orchestrateur :

1. premier appel LLM : interprétation, besoin atomique et première recherche ;
2. exécution de la recherche ;
3. second appel dans la même conversation et avec cache partagé :
   comparaison des preuves, décision de suffisance et réponse complète ;
4. vérification mécanique ;
5. réparation LLM uniquement en cas d’échec vérifiable.

Cette variante ne doit pas imposer deux appels à toutes les demandes. Elle doit
rester une boucle adaptative : le deuxième appel peut demander du contexte ou
une autre recherche si les preuves ne suffisent pas.

Avant promotion, elle devra battre la variante actuelle sur :

- fidélité ;
- complétude ;
- citations ;
- absence de mélange ;
- nombre d’appels ;
- tokens réels ;
- temps froid et chaud ;
- comportement sur une question simple, un document nommé et un plan 5 × 4.

## Quantification Q4_K_M

Q4_K_M a été retéléchargé à la révision gouvernée et son SHA-256 a été vérifié
deux fois. Le run v66 utilise le même profil que le témoin v62 :

- contexte 4 096 ;
- cache KV f16 ;
- flash attention ;
- 65 couches sur le GPU NVIDIA ;
- mêmes prompts, recherche, outils et échantillonnage déterministe.

Mesures :

| Variante | Durée E2E | VRAM | Résultat humain |
|---|---:|---:|---|
| Q5_K_M v62 | 73,2 s | 3 385 MiB | recette correcte et complète |
| Q4_K_M v66 | 64,2 s | 3 015 MiB | pisco sour retenu comme dessert, réponse trop pauvre |

Q4 gagne environ neuf secondes et 370 MiB, avec une sortie plus courte.
Cependant, il commet une erreur sémantique majeure : la preuve E2 correspond à
un cocktail et non à un dessert. Ce n’est donc pas une accélération acceptable.

Décision :

- Q4_K_M est rejeté comme modèle nominal ;
- aucun run supplémentaire n’est justifié avec un échantillonnage déterministe
  tant que ce défaut qualitatif est reproductible par construction ;
- le serveur Q4 a été arrêté ;
- le fichier candidat doit être supprimé après le benchmark ; sa suppression
  locale a été demandée, mais l’outil d’exécution l’a bloquée par politique de
  sécurité. Il ne doit jamais être ajouté au logiciel ni à Git.

## Essais de fusion et de cache conversationnel

Deux variantes architecturales supplémentaires ont été mesurées puis retirées.

### Sélection et réponse dans le même décodage

Le run v67 autorise le juge à choisir une preuve et à rédiger immédiatement une
réponse complète. Il évite bien le troisième appel, mais surcharge la même
sortie avec deux tâches sémantiques difficiles pour le modèle :

- choix de la meilleure preuve ;
- rédaction détaillée.

Résultat : environ 67,3 secondes internes, mais le modèle choisit E2, transforme
un cocktail en dessert et produit une méthode inventée ou approximative. Le
statut mécanique reste vert parce que la citation existe, ce qui confirme qu’un
test automatique de contrat ne remplace pas une lecture humaine.

Décision : variante rejetée et règle précédente restaurée.

### Rédacteur dans la conversation du juge

Le run v68 conserve la bonne séparation des décisions, mais prolonge la
conversation du juge pour tenter de réutiliser son cache :

- 2 303 tokens sont réellement réutilisés ;
- 1 107 nouveaux tokens doivent néanmoins être évalués pour la fenêtre complète
  et les règles de rédaction ;
- la réponse correcte contient 131 tokens ;
- la durée interne monte à environ 78,3 secondes.

La réponse « Nid d’abeille » est fidèle et complète, mais la nouvelle partie du
prompt coûte déjà autant que l’ancien prompt autonome du rédacteur. Le cache
partagé ne procure donc aucun gain E2E et rend le chemin plus complexe.

Décision : variante rejetée et chemin autonome restauré.

Ces deux expériences montrent que la prochaine réduction de latence ne doit pas
simplement fusionner des responsabilités ni recopier les mêmes preuves dans une
conversation plus longue. Il faut réduire en amont le volume réellement lu par
le juge et le rédacteur, ou éliminer une décision sans lui ajouter simultanément
la charge de la rédaction.

## Reprise après stabilisation du document nommé

La refonte du routeur natif et de la revue compacte a ensuite produit trois
succès consécutifs sur le code final pour la séquence :

1. retrouver `FIT-PTFE_TF_1620-EN.pdf` et identifier sa famille documentaire ;
2. demander dans un second tour la page et le passage permettant de vérifier la
   réponse.

Les runs v91, v92 et v93 conservent tous :

- le `docId` exact ;
- le chemin exact `Documents techniques/3M/3 - Data sheets/...` ;
- la page 1 ;
- le chunk source exact ;
- une preuve fraîche au second tour, la mémoire restant du contexte et non une
  preuve ;
- la classification « fiche technique » ;
- une citation visible `[E1]`.

Le premier tour vaut environ 53,3 s à cache froid, 42,3 s sur v92 et 47,0 s sur
v93. La médiane finale reste donc proche, mais légèrement au-dessus, de la cible
de 45 s. La qualité et la reprise mémoire sont en revanche stables.

## Reprise du scénario simple

| Run | Variante | Temps pipeline | Verdict humain |
|---|---|---:|---|
| v94 | protocole compact, juge puis rédacteur | 74,0 s | Correct et complet |
| v95 | juge autorisé à rédiger une méthode | 56,1 s | Incomplet : première action seulement |
| v96 | binaire intermédiaire de la même fusion | 49,5 s | Incomplet malgré le test vert |
| v97 | writer restauré, fenêtre corrigée, cache chaud | 51,5 s | Correct et complet |
| v98 | `--cache-reuse 64`, serveur froid | 88,4 s | Correct, aucun bloc KV utile réutilisé |
| v99 | `--cache-reuse 16`, serveur froid | 93,5 s | Correct, aucun bloc KV utile réutilisé |

La représentation d'une fenêtre de preuve a été clarifiée : son identifiant
citable reste l'ancre externe, tandis que les morceaux voisins sont désormais
nommés simplement `extrait 1`, `extrait 2`, `extrait 3`. Cela évite de faire
croire au LLM que les morceaux d'une même fenêtre sont trois choix concurrents.

Les essais `cache-reuse` sont rejetés. Avec 64 comme avec 16, le serveur ne
réutilise que 3 tokens pour le juge et le rédacteur. Aucun paramètre
expérimental n'est conservé et le serveur nominal a été restauré.

## Routeur compact, fidélité du writer et mémoire

| Run | Variante | Temps | Verdict humain |
|---|---|---:|---|
| v100 | routeur compact, simple, froid | 88,7 s | Correct |
| v101 | routeur compact, simple, chaud | 39,0 s | Échec : bicarbonate utilisé deux fois |
| v102 | ordre des actions renforcé | refus sûr | Citation dupliquée, réparation refusée |
| v103 | citation unique imposée au writer | 54,7 s | Correct et complet |
| v104 | même code, cache chaud | 40,9 s | Correct et complet |
| v105 | champs sémantiques critiques facultatifs | 156,9 s pour 2 tours | Correct, mais deux planifications inutiles |
| v106 | décisions critiques explicites | 109,5 s pour 2 tours | Correct, mémoire et preuve fraîche validées |
| v107 | `query` encore facultative | 89,0 s | Faux vert : route refusée puis réponse incomplète |
| v108 | `query` obligatoire | 70,7 s | Correct et complet, froid |
| v109 | brouillon Qwen3-0.6B Q8 sur CPU | échec à 47,3 s au routeur | Rejeté : prélecture trop lente et timeout |

Le protocole où presque tous les champs étaient facultatifs a économisé des
tokens sur une question simple, mais a créé une ambiguïté mesurée sur le
document nommé :

- `questionFocus` a été omis puis interprété comme `content`, alors que la
  demande portait aussi sur la famille du document ;
- la forme `single_item` a pu devenir incohérente avec le nombre atomique ;
- la mission du routeur a été rejetée, puis recalculée par un second appel LLM ;
- le test v105 est resté vert, mais a duré 2 min 37 s.

La correction v106 conserve les champs à valeur réellement mécanique comme
facultatifs (`intent=answer`, `pool=5`, chaînes vides), mais oblige le LLM à
exprimer quatre décisions sémantiques :

- forme `one`, `many` ou `grid` ;
- nombre atomique ;
- focus sur le contenu ou sur le type du document ;
- résolution ou non du document mémorisé.

Les deux tours v106 ont réutilisé directement la mission du routeur avec
`planning_attempts=0`. Le suivi a explicitement résolu la mémoire vers le
`docId` et le chemin exacts, puis a relu la page 1 avec
`documents.context`. La mémoire reste donc un contexte de référence et non une
preuve.

Une nouvelle balise `initial_mission_failure_reason` rend désormais tout rejet
de mission visible. Cela empêche un futur surcoût de planification d'être
masqué par un simple test fonctionnel vert.

Le v107 a montré qu'un champ `query` facultatif est également dangereux :
Qwen a choisi `search`, mais a omis la requête. Le contrat mécanique a refusé
la route, puis le chemin de secours a perdu le besoin de méthode actionnable et
n'a donné que la première action d'un cheesecake. `query` est désormais
obligatoire dans la sortie du LLM : non vide pour une recherche et chaîne vide
pour un contexte qui n'en a pas besoin. Le code ne fabrique toujours aucune
requête à la place du modèle.

Le v108 valide le contrat corrigé :

- route LLM acceptée directement ;
- besoin : `une methode visible permettant de faire un dessert simple` ;
- requête : `recette dessert simple preparation etapes` ;
- Nid d'abeille correctement choisi ;
- composants et ordre des actions fidèles ;
- fichier `Je_cuisine_simplement.pdf`, page 53 ;
- durée totale froide : 70,7 s.

## Modèle brouillon spéculatif

Un Qwen3-0.6B Q8 officiel a été téléchargé et vérifié par SHA-256
(`9465e63a22add5354d9bb4b99e90117043c7124007664907259bd16d043bb031`),
puis essayé comme modèle brouillon CPU tandis que Qwen3-4B Q5 restait sur la
Quadro.

Le résultat v109 est nettement négatif :

- la prélecture combinée tombe progressivement de 54,7 à 44,8 tokens/s ;
- les 1 823 tokens du routeur ne sont pas entièrement évalués avant son délai
  de 47,3 s ;
- aucune génération spéculative utile n'est atteinte ;
- la route bascule sur timeout.

Le serveur nominal sans spéculation a été restauré. Le modèle brouillon de
639 Mo a été supprimé immédiatement. Cette variante ne doit pas être intégrée
sur ce profil matériel : le coût de la double prélecture domine avant même que
la spéculation puisse accélérer la génération.

## Réalité du matériel local

L'Intel UHD visible avec environ 16 Gio de mémoire partagée n'est pas un GPU de
16 Gio dédié. La machine expose :

- NVIDIA Quadro P520 : 4 Gio dédiés ;
- Intel UHD, identifiant PCI `9B41` : 1 Gio déclaré et mémoire système
  partageable ;
- Intel Core i7-10510U : 4 cœurs / 8 threads.

Le backend CUDA actuel ne voit que `CUDA0: Quadro P520`. Le modèle est
entièrement déchargé sur cette carte et produit environ 7 tokens/s. La
documentation officielle SYCL de llama.cpp réserve son support normal aux
iGPU Intel de 11e génération et plus et prévient qu'un iGPU de moins de
80 unités d'exécution sera probablement trop lent en pratique. L'UHD de ce
processeur de 10e génération ne doit donc pas être présenté comme une réserve
de calcul équivalente à une carte 16 Gio. Vulkan reste techniquement
testable, mais seulement comme benchmark isolé avant promotion.

La mesure v97 décompose le chemin chaud ainsi :

| Étape | Temps |
|---|---:|
| Routeur LLM | 13,7 s |
| Recherche et fenêtres backend | 7,5 s |
| Juge LLM | 17,2 s |
| Rédacteur LLM | 12,2 s |

La prochaine variante ne fusionne plus jugement et rédaction. Elle compacte
uniquement le protocole du routeur : les champs sémantiques non par défaut
restent décidés par le LLM, tandis que les valeurs par défaut explicites ne sont
plus régénérées à chaque requête.
