# Audit du Goal actif — frontière locale et capacité avancée A755–A763

Date : 11 septembre 2026

Branche : `SAAIA_V3.1`

Statut produit : **TESTE_NON_APPROUVE**

## Verdict actuel

La frontière du petit modèle local est qualifiée sur la banque connue A755 et
reste stable après l'intégration de la capacité avancée. L'architecture hybride
est implémentée de bout en bout : décision locale, handoff typé, job serveur
durable, retrieval SAAIA, fournisseur OpenAI-compatible, validation des preuves,
reprise WinUI et affichage des sources.

Les appels réels à OpenAI démontrent que Terra peut produire mécaniquement les
livrables complexes visés. La dernière revue humaine accepte une fois les cinq
repas étudiant et la comparaison CEN/IEC, mais rejette le planning de vingt
cellules car le placement de plusieurs recettes n'est pas soutenu par les
preuves. Les essais ont ainsi révélé puis permis de corriger des défauts
génériques de requêtage, de sélection des preuves, de validation sémantique et
de protocole. L'état courant n'a pas encore trois réussites consécutives sur
l'ensemble de la banque avancée après le dernier gel. Le produit reste donc
`TESTE_NON_APPROUVE`.

## Reprise documentaire et dépôt

Le registre de reprise rapporte 43 documents sur 43 lus, soit 15 847 677
caractères sur 15 847 677. Le dépôt du laptop a déjà remplacé l'ancien checkout ;
ce transfert ne doit pas être rejoué.

Les fondations avancées déjà poussées sont :

- `62b4215e` : reprise durable des jobs avancés dans WinUI ;
- `b30096ac` : contrat de fournisseur et profils OpenAI, RunPod et serveur client ;
- `9101e5f5` : exécution du grand modèle par le backend dans le parcours produit ;
- `0e991044` : topologie interne ou externe liée à la configuration signée ;
- `5ddcc5ec` : enveloppe de coût Terra vérifiée.

## Frontière du petit modèle local

La configuration qualifiée est la machine cliente actuelle : RTX 4060,
Qwen3-4B Instruct Q5_K_M, llama.cpp CUDA b10098 et contexte de 4 096 tokens.
La banque A755 connue comporte quatorze cas répétés trois fois. Le dernier
artefact de non-régression contient 42 résultats sur 42, quatorze routes stables
sur quatorze et aucune erreur.

Les quatre issues de production sont :

- `local_source_backed_answer` pour une demande directe dans l'enveloppe mesurée ;
- `clarification` lorsqu'une identité ou contrainte indispensable manque ;
- `insufficient_evidence` lorsque le corpus est réellement insuffisant ;
- `advanced_analysis_required` lorsque le nombre d'unités, les comparaisons, la
  structure ou la profondeur de recherche dépassent l'enveloppe locale.

Le planning de cinq jours par quatre repas est volontairement hors de
l'enveloppe locale. Le bon comportement du petit modèle est de détecter sa charge
avant retrieval et de créer un handoff avancé, sans produire un faux planning.
Cette frontière est une preuve de régression sur une banque connue. Le holdout
historique est contaminé ; une généralisation aveugle reste à démontrer après le
gel final.

## Architecture avancée implémentée

`IAdvancedAnalysisProvider` est la frontière unique du grand modèle. Les profils
`openai-dev`, `runpod-bench` et `customer-server` utilisent le même cycle :
planificateur, tools SAAIA, preuves revalidées, rédacteur et validateur final.
Le code RAG ne dépend donc pas de l'emplacement futur du modèle.

La configuration signée distingue le fournisseur de la topologie : OpenAI et
RunPod sont des services externes ; le serveur final du client est interne. Les
droits de transmission de contenu et de métadonnées sont explicites. Une
combinaison incohérente est rejetée avant l'appel HTTP.

Le job avancé est durable et isolé par tenant. Il possède lease, heartbeat,
retry, annulation, revalidation des preuves et trace bornée. WinUI persiste
l'identifiant du job avant le polling, reprend le même job après redémarrage et
ne l'annule pas lors de la fermeture de l'application.

Les secrets externes sont importés depuis le presse-papiers dans un stockage
DPAPI hors Git. Le script efface ensuite le presse-papiers et fonctionne aussi
avec Windows PowerShell 5.1. Ni la clé, ni les prompts, ni le contenu des preuves
ne sont écrits dans le journal de coût.

## Corrections issues des essais live

Les essais ont conduit aux protections générales suivantes :

- queries limitées au corpus privé, sans URL, domaine ou catégorie inventée ;
- une recherche ciblée par document explicitement demandé ;
- résolution du `DocumentHint` vers un unique `docPath` indexé, sinon absence de
  cloisonnement plutôt qu'une sélection ambiguë ;
- exclusion mécanique des documents voisins avant rédaction ;
- conservation du chemin réellement résolu dans la trace du tool ;
- requêtes du planificateur rattachées au bon document ;
- reclassement lexical des passages par rapport au sujet de chaque requête, en
  retirant les tokens de l'identifiant documentaire du score ;
- équilibrage des preuves entre recherches et documents ;
- maximum de 700 caractères par preuve dans le prompt et 14 000 caractères de
  preuves par rédaction ;
- validation du nombre d'unités demandées, des doublons, des marqueurs de claims
  et des EvidenceIds autorisés ;
- relation ligne/colonne/rôle/catégorie incluse dans le claim et soutenue par sa
  preuve, au lieu de traiter le placement comme une synthèse non factuelle ;
- une seule réparation bornée d'un JSON de rédacteur mal formé ou de marqueurs
  manquants ;
- aucune réparation autorisée pour une citation vers un EvidenceId non revalidé ;
- une réponse finale ne transporte que les preuves effectivement citées.

Aucune règle de production propre aux repas, à IEC, à NIST, à un document ou à
une réponse attendue n'est conservée. Une variante déterministe spécialisée sur
le planning a donné de bons résultats expérimentaux, puis a été retirée parce
qu'elle violait l'exigence d'architecture généraliste. Ses résultats ne servent
pas à approuver l'état courant.

## Résultats OpenAI observés

La clé restreinte SAAIA a été créée, importée dans DPAPI et testée sans l'écrire
dans le dépôt. Le parcours complet utilise le petit modèle local pour router,
le backend local temporaire pour exécuter le job, le corpus PostgreSQL/Qdrant du
serveur pour rechercher, puis OpenAI pour planifier et rédiger.

Terra a démontré :

- plusieurs plannings répondus avec vingt claims et vingt cellules ;
- exactement cinq idées de repas sourcées ;
- une comparaison CEN/IEC avec deux claims ;
- sept points NIST sourcés.

Ces succès ne forment pas une banque finale trois sur trois sur l'état courant.
Des runs intermédiaires ont aussi produit une insuffisance trop vague, une
discordance de citations ou une détection de langue incertaine. Ils sont
conservés comme preuves négatives et ont réouvert les composants responsables.

Luna, utilisé pour continuer à faible coût pendant la limite Terra, montre une
frontière utile :

- les cinq repas étudiants et les sept points NIST sont généralement corrects ;
- le planning vingt cellules aboutit le plus souvent à une insuffisance plutôt
  qu'à une réponse complète ;
- après le reclassement générique des preuves, deux exécutions consécutives de la
  comparaison CEN/IEC ont cité la page IEC 238 et répondu complètement ;
- l'exécution suivante a échoué proprement parce que le JSON du rédacteur ne
  respectait pas le contrat. Une réparation bornée couvre maintenant ce défaut,
  mais elle n'a pas encore été rejouée live faute de quota disponible.

Cette observation confirme que Luna peut servir aux tâches avancées modestes ou
à la planification économique, mais ne constitue pas le modèle de référence pour
le planning complexe. Terra reste le candidat API principal de validation.

## Coût et limites du compte

Le journal local enregistre désormais 52 appels Terra, dont 46 réussis et 6
rejets, pour 226 635 tokens d'entrée, 39 974 tokens de sortie et 0,8846442 USD.
Il enregistre
48 appels Luna réussis, 175 176 tokens d'entrée, 19 768 tokens de sortie et
0,05709756 USD. Le total journalisé est 0,94174176 USD. L'interface OpenAI
affiche 0,92 USD consommé et un solde actualisé de 24,08 USD ; le faible écart correspond
aux appels ou arrondis hors journal applicatif.

Les garde-fous SAAIA restent : 25 USD autorisés, alerte à 20 USD, arrêt local à
24 USD, 0,50 USD maximum par job et quatre appels maximum par job. Ils protègent
le budget demandé et sont indépendants des limites de débit OpenAI.

Le compte OpenAI affiche encore `Free tier` alors que la facture API créée le
11 septembre 2026 à 16:06 est payée et correspond à 25 USD de crédits,
27,03 USD taxe comprise. Cet achat dépasse le seuil de
5 USD annoncé par la page Limits pour le Tier 1 automatique. Les modèles Terra
et Luna restent pourtant limités à 50 requêtes par jour. Cette limite
fournisseur ne peut pas être supprimée dans l'interface. Le bouton de changement
de palier propose seulement un nouvel achat de crédits ; aucun achat
supplémentaire n'a été effectué.

Quatre rejets Terra `advanced_llm_http_429` sont horodatés entre 17:05 et 18:15.
Un dossier sans secret est prêt sous
`artifacts/reprise-pc-20260908/a763-provider-comparison/openai-account-tier-support-20260911`.
Il contient les preuves minimales et un message anglais prêt à transmettre au
support. Une conversation authentifiée a été ouverte avec le support OpenAI et
le message a été transmis. À sa demande, une réponse 429 fraîche et nettoyée a
été fournie avec l'identifiant de requête, le type `requests`, le code
`rate_limit_exceeded`, la limite 50, le restant 0, le reset et `Retry-After`.
La clé active appartient au `Default project` de l'organisation financée et le
code n'envoie ni `OpenAI-Organization` ni `OpenAI-Project`. Le support a confirmé
qu'il s'agit bien d'un plafond RPD d'organisation, puis une escalade vers un
agent humain a été demandée car la page Limits ne propose qu'un nouvel achat de
crédits.

## Préparation RunPod sans dépense

Le premier candidat reproductible est le Public Endpoint RunPod
`Qwen/Qwen3-32B-AWQ`, exposé à
`https://api.runpod.ai/v2/qwen3-32b-awq/openai/v1`. RunPod documente une fenêtre
de 32 768 tokens et un prix uniforme de 10 USD par million de tokens :

- https://docs.runpod.io/public-endpoints/models/qwen3-32b
- https://docs.runpod.io/public-endpoints/ai-coding-tools

Sur les 39 jobs externes multi-appels déjà terminés, la médiane observée est de
10 190 tokens et le percentile 90 de 11 713 tokens. Au tarif RunPod annoncé,
douze jobs coûteraient environ 1,22 USD à la médiane et 1,41 USD au percentile
90. Une enveloppe autorisée de 3 USD couvre donc les variations et une éventuelle
réparation de protocole sans ouvrir un budget large. RunPod demande au moins
5 USD de crédits pour utiliser ces endpoints, mais SAAIA peut conserver une
autorisation locale de 3 USD et un arrêt dur calculé à 2,88 USD.

Le garde-budget persistant s'applique désormais à tout fournisseur externe, et
pas seulement à OpenAI. Le profil RunPod utilise son propre registre, ses propres
tarifs et refuse de démarrer sans budget explicitement autorisé. Le lanceur
commun est `tools/test-advanced-product-path-provider.ps1`. Les façades
`tools/test-advanced-product-path-openai.ps1` et
`tools/test-advanced-product-path-runpod.ps1` préservent une invocation simple
et isolent les valeurs par fournisseur. Aucun compte, crédit, endpoint privé,
secret ou appel payant RunPod n'a été créé à ce stade.

## Vérifications de l'état courant

- tests ciblés fournisseur, worker et validations : 55/55 ;
- résolution live des documents FD CEN, IEC et NIST dans le vrai catalogue :
  1/1 ;
- suite Debug complète : 4 388 réussis, deux sondes live explicitement ignorées,
  aucun échec ;
- build Release complet : zéro avertissement et zéro erreur ;
- syntaxe des quatre scripts PowerShell modifiés : valide ;
- `git diff --check` : propre ;
- scan des changements suivis : aucune clé OpenAI, aucun mot de passe serveur et
  aucun secret de configuration détecté ;
- ports temporaires 5123 et 1234 libérés après les campagnes.

## Preuves manquantes avant approbation

1. Rejouer la banque avancée complète trois fois sur l'état gelé avec Terra,
   après application du Tier 1 ou réinitialisation du quota journalier.
2. Qualifier `Qwen/Qwen3-32B-AWQ` via le Public Endpoint RunPod avec le même
   contrat. Le candidat, l'URL, les tarifs et le lanceur sont prêts ; il manque
   l'autorisation de dépense RunPod, au moins 5 USD de crédits et une clé RunPod.
3. Exécuter le même protocole sur le serveur final du client lorsque son matériel
   et son modèle seront disponibles.
4. Pendant une réponse avancée terminale acceptée, ouvrir les cartes source
   exactes dans WinUI. La fermeture puis relance du vrai WinUI et la reprise du
   même job sont désormais prouvées séparément.
5. Ouvrir un nouveau holdout aveugle après le gel définitif du code.

Le palier RunPod, l'hébergement client et le holdout aveugle dépendent de moyens
externes encore absents. Le contrat unique permet de changer d'endpoint sans
changer la logique documentaire, et le garde-budget externe suit désormais ce
contrat pour chaque fournisseur facturé.

## Prochaine séquence

L'état courant doit être commité et poussé avec ses tests et son audit. Ensuite,
dès que le quota Terra est disponible, la banque avancée est rejouée trois fois
sans modifier les critères. Un résultat sémantiquement incorrect réouvre la
cause précise ; trois passages complets autorisent le gel du candidat API. La
qualification RunPod, l'inspection WinUI des sources terminales et le holdout
aveugle restent ensuite les dernières preuves avant toute approbation produit.

## Dernière exécution Terra sur l'état courant et incident du lanceur

Les deux dernières requêtes Terra disponibles ont servi à une exécution produit
du planning 5 × 4 après le gel mécanique. La première tentative s'est arrêtée
avant tout appel externe avec `advanced_external_llm_requires_https`. La cause
était une collision de variables PowerShell insensible à la casse : le paramètre
externe `$BaseUrl` et l'URL locale `$baseUrl` désignaient la même variable. Le
lanceur remplaçait donc par erreur l'URL HTTPS du fournisseur par l'URL HTTP du
backend local. Le contrôle de sécurité a correctement refusé cette valeur et
aucun quota Terra n'a été consommé par ce faux départ.

Le lanceur générique emploie maintenant `$ProviderBaseUrl`, avec l'alias public
`BaseUrl` conservé pour la compatibilité. La façade RunPod passe explicitement ce
nouveau nom. Les deux scripts sont valides pour l'analyseur PowerShell, le scan
de collision confirme une seule définition de l'URL locale et
`git diff --check` reste propre.

Le rejeu a ensuite abouti en 36 503 ms : statut avancé `succeeded`, outcome
`answered`, deux appels Terra, 9 749 tokens d'entrée, 1 995 de sortie et
0,043438 USD. La réponse française contient cinq jours, quatre repas par jour,
vingt propositions distinctes, vingt claims et neuf cartes source. Elle ne porte
aucun drapeau d'erreur de réponse. Cette preuve valide une répétition du cas
complexe sur l'état courant ; elle ne remplace pas la banque trois sur trois.

Une sonde minimale exécutée après ce rejeu reçoit HTTP 429 avec
`x-ratelimit-limit-requests: 50`, `x-ratelimit-remaining-requests: 0` et le code
`rate_limit_exceeded`. Le plafond fournisseur est donc maintenant épuisé. Le
budget SAAIA demeure disponible et son arrêt dur à 24 USD est conservé pour
respecter l'autorisation de dépense de 25 USD.

## Explication produit des limites fournisseur

L'incident réel a montré que WinUI conservait bien le code
`advanced_llm_http_429` dans l'état durable, mais affichait le même message
générique que pour toute panne avancée. Le client distingue maintenant les codes
HTTP 429 sans dépendre d'OpenAI : il explique dans les six langues supportées que
le service avancé a temporairement atteint sa limite de requêtes et propose de
réessayer après le reset ou avec un autre fournisseur autorisé. Le payload du
fournisseur reste masqué et aucune source non validée n'est rendue.

La preuve dédiée injecte un job échoué avec une réponse non fiable et le code
429. Elle vérifie le texte explicite, l'absence du contenu non fiable, zéro carte
source et la conservation du code technique dans les métadonnées. La classe de
transport avancé passe 21/21, la suite client complète 2 234/2 234 avec une sonde
live opt-in ignorée, la solution complète 4 389/4 389 avec deux sondes live
ignorées, et le build Release se termine sans avertissement ni erreur.

## Évaluateur de banque aligné sur le parcours produit

Le préflight de la prochaine banque a détecté que
`tools/assess-advanced-capacity-results.ps1` lisait encore les métriques de
l'ancien appel LLM direct. Dans le parcours courant, le petit modèle local reste
visible dans `providerMode`, tandis que le fournisseur réellement évalué se
trouve dans `advancedProviderKey`, `advancedProviderModel`,
`advancedProviderCallCount` et `advancedEstimatedCostUsd`. L'évaluateur aurait
donc produit un faux rejet de fournisseur, modèle et coût.

Il sélectionne maintenant les métriques avancées lorsqu'elles existent et garde
la compatibilité avec les anciens artefacts directs. Les références de réponse
acceptent les EvidenceIds historiques `[E…]` et les ClaimIds du contrat courant
`[C…]`. Exécuté sur le dernier planning Terra, il observe `openai-dev`,
`gpt-5.6-terra`, deux appels, vingt références et vingt repas distincts. Verdict :
`PASS_MECHANICAL_REQUIRES_SEMANTIC_REVIEW`, une ligne, zéro échec. Le garde-fou
rappelle explicitement que cette réussite mécanique ne remplace pas l'inspection
sémantique ni les trois répétitions.

Le lanceur produit commun exécute désormais cet évaluateur automatiquement
après toute banque OpenAI ou RunPod réussie. Un rejet mécanique rend la campagne
rouge avant sa clôture, tandis qu'un PASS conserve explicitement l'obligation de
revue sémantique. La qualification future ne dépend donc plus d'une commande
manuelle oubliable.

## A763 — créneaux RPD partiels et campagne gelée cadencée — 2026-09-12

Une première tentative de banque complète a été exécutée après la réapparition
de quelques créneaux Terra. Sur la répétition 1, le planning 5 × 4, les cinq
repas étudiant et la comparaison CEN/IEC ont terminé avec le fournisseur
`openai-dev` et le modèle `gpt-5.6-terra`. Leurs durées étaient respectivement
32 577 ms, 32 938 ms et 64 761 ms. Le planning contient vingt claims et huit
cartes source ; les cinq repas contiennent cinq claims et deux cartes ; la
comparaison contient deux claims et les deux documents exigés. Le quatrième cas,
sept points NIST, a obtenu son plan et ses recherches puis a échoué sur
`advanced_llm_http_429` avant la rédaction. La dépense de cette tentative est de
0,09515 USD, dont 0,004714 USD pour le plan NIST sans réponse finale. Le registre
local atteignait 1,03689176 USD après cette première tentative.

Cette exécution invalide la répétition et ne compte pas comme un passage de la
banque. L'évaluateur mécanique scelle trois cas valides et le rejet NIST. Elle
montre aussi que le plafond fournisseur ne s'est pas réinitialisé en un bloc :
des requêtes initialement refusées ont ensuite réussi, puis le plafond est
redevenu indisponible. L'observation est compatible avec des créneaux RPD de la
veille qui expirent progressivement ; elle ne permet pas de disposer des vingt-
quatre appels normalement requis pour 4 cas × 3 répétitions.

Le lanceur commun accepte maintenant un délai explicite entre les cas et entre
les répétitions. Le préflight enregistre ce délai, le commit Git exact et si des
fichiers suivis étaient modifiés. Le commit `4bd38c98` porte cette correction et
permet de distinguer une campagne réellement gelée d'un simple répertoire
d'artefacts. Une seconde tentative a été lancée sur ce commit avec 60 secondes
de délai. Le planificateur du premier cas a réussi pour 0,003202 USD, puis son
rédacteur a reçu 429 ; le planificateur du second cas a ensuite reçu 429. La
campagne a été interrompue pour ne pas consommer les rares créneaux qui se
libèrent. Son assessment est `REJECT_MECHANICAL` et une note d'interruption
explicite complète les fichiers de shutdown. Le registre local atteint ainsi
1,04009376 USD sur 111 lignes, succès et échecs compris.

L'interruption a mis en évidence une seconde faiblesse de preuve : un Ctrl-C
exécutait bien les blocs `finally`, arrêtait le backend et Qwen, mais laissait
`failure: null`. Les deux lanceurs communs exposent désormais `completed` et
inscrivent `Campaign interrupted before completion.` lorsqu'un arrêt se produit
avant la fin. Les ports 1234 et 5123 sont libres, aucun `llama-server` ne reste
actif, la configuration locale est restaurée et aucun secret n'a été écrit dans
les artefacts.

Le délai est conservé comme paramètre reproductible pour les limites RPM ; il ne
prétend pas supprimer un plafond RPD. La preuve encore requise reste une banque
complète 3/3 sur un quota réellement disponible ou après activation du Tier 1.
Le produit reste `TESTE_NON_APPROUVE`.

## A763 — revue sémantique partielle et relation cellule/preuve — 2026-09-12

Les quatre jobs de la campagne partielle ont été relus en transaction
PostgreSQL `REPEATABLE READ ONLY`. Le bundle contient les résultats durables et
les douze chunks canoniques réellement référencés; son SHA-256 est
`7037FB3B8B50D3515BEC9461D530DE141053AA9B5C9193511AF689D04799BA46`.
Aucune donnée n'a été modifiée.

Deux réponses réussies sont sémantiquement correctes sur cette exécution. Les
cinq idées étudiant sont distinctes et soutenues par deux documents qui
établissent leur caractère étudiant, simple, rapide ou économique. La
comparaison CEN/IEC reprend fidèlement les deux prescriptions et conserve les
sources séparées. Ces PASS unitaires ne satisfont pas encore le seuil 3/3.
NIST reste un échec fonctionnel 429, avec un handoff sûr qui n'affiche aucune
réponse ou source non validée.

Le planning 5 × 4 est rejeté. Il possède bien cinq jours, quatre colonnes, vingt
noms distincts et vingt claims, mais plusieurs citations prouvent seulement
l'existence d'un nom dans un index. Elles ne prouvent pas son adéquation au
créneau choisi. C15 est la preuve causale : le chunk recommande les pancakes au
petit-déjeuner tandis que Terra les place en collation. Le contrat Writer disait
explicitement que l'arrangement des candidats était une synthèse et non un
nouveau fait; il autorisait donc ce trou de grounding.

Le commit `b20fcc2` remplace cette règle par une contrainte générale : la
relation créée par une ligne, une colonne, un rôle ou une catégorie fait partie
de l'unité factuelle. Le claim doit l'énoncer et sa preuve doit la soutenir;
sinon le Writer doit retourner l'insuffisance exacte. Aucune règle Cuisine n'est
codée et aucun appel Critic systématique n'est ajouté avant d'avoir mesuré
l'effet du contrat renforcé. Les 30 tests fournisseur passent.

La validation Release complète sur `b20fcc2` rapporte 10 tests contrats,
2 145 backend et 2 234 client réussis, soit 4 389 réussites et zéro échec. Les
deux probes live opt-in ne sont pas exécutées. La porte mécanique est verte,
mais le gel sémantique est rouvert et la prochaine banque Terra doit partir de
`b20fcc2` ou d'un descendant documentaire. Détails :
`ADR-2026-09-12-A763-RELATION-CELLULE-PREUVE.md` et
`artifacts/reprise-pc-20260908/a763-partial-terra-semantic-review-20260912`.
Le produit reste `TESTE_NON_APPROUVE`.

Après le commit de preuve `4b04aa94`, les trois projets de tests ont été rejoués
séparément en Release afin de conserver un TRX par projet : 10 tests contrats,
2 145 tests backend et 2 234 tests client/agent ont réussi, soit 4 389 réussites
et aucun échec. Les deux tests live canoniques restent explicitement ignorés. La
porte locale est `PASS_MECHANICAL`; elle ne remplace pas la banque Terra 3/3 ni
sa revue sémantique.

## A763 — reprise réelle du même job après redémarrage WinUI — 2026-09-12

Le commit `e7587810` ajoute une preuve reproductible du cycle de vie dans le
véritable exécutable `SAAIA.Client.WinUI.exe`. Le runner crée une session, un
message assistant et un job avancé temporaires, lance WinUI, attend les traces
backend de reprise, ferme la fenêtre gracieusement, vérifie que le job serveur
n'a pas été annulé, puis relance WinUI sur la même session.

Les deux processus ont observé le même `jobId`
`00174700-5608-4c8f-9e27-52cd6fd41591`, le même `handoffId` et le même
`messageId`. Le backend rapporte un seul `POST` de création du job, quatre
`GET` sur ce job, deux `PATCH` sur ce message, puis uniquement pendant le
nettoyage un appel d'annulation et la suppression de la session temporaire.
Après chaque fermeture réelle, l'état relu est `queued`, révision 1 et
`cancelRequested: false`. Le verdict est
`PASS_REAL_WINUI_RESTART_RESUME`.

Le fournisseur est volontairement `disabled` et le worker désactivé pour isoler
la propriété testée. Le compteur fournisseur vaut zéro et aucun contenu n'a été
transmis à l'extérieur. Les fichiers `settings.json`, `secure.json` et le journal
client de l'utilisateur ont été sauvegardés puis restaurés; la configuration
locale temporaire a aussi été restaurée. Le shutdown final confirme zéro
processus WinUI, zéro backend temporaire et zéro écoute résiduelle sur les ports
de test. Aucun secret n'est présent dans l'artefact
`artifacts/reprise-pc-20260908/a763-winui-restart-resume-e7587810-20260912`.

Trois essais précédents restent conservés comme preuve des corrections du
runner : dépendances documentaires absentes, champ RunPod optionnel sous mode
strict, puis détecteur fondé sur un journal client trop indirect. Le dernier
runner observe les requêtes backend réelles, qui constituent l'autorité pour la
reprise. Cette validation ferme la porte arrêt/redémarrage du client. Elle ne
valide ni la réponse sémantique, ni l'état terminal réussi, ni le clic des cartes
source d'une réponse avancée : ces points restent attachés à la campagne Terra
acceptée. Le produit reste `TESTE_NON_APPROUVE`.

## A763 — réparation bornée prouvée sur transport HTTP réel — 2026-09-12

Le commit `d0a844d5` ajoute une fixture OpenAI-compatible limitée au loopback et
le runner `tools/test-advanced-protocol-repair-local.ps1`. La fixture renvoie
successivement un plan valide, une réponse writer JSON volontairement tronquée,
puis un objet réparé valide. Le provider `customer-server`, en localisation
`internal`, emploie le même `OpenAiCompatibleAdvancedAnalysisProvider` que les
profils OpenAI et RunPod; le test traverse donc le vrai client HTTP et le vrai
parseur/réparateur sans appeler un modèle externe.

Sur le commit propre `d0a844d5`, les traces rapportent exactement trois requêtes
vers `127.0.0.1` : `planner`, `writer-malformed`, `writer-repair`. Le résultat
est `answered`, avec vingt claims et vingt EvidenceIds distincts. Le verdict est
`PASS_BOUNDED_PROTOCOL_REPAIR_LIVE_LOOPBACK`. Zéro contenu ou métadonnée n'a été
transmis hors de la machine, aucun registre facturé n'a été écrit, les variables
d'environnement ont été restaurées, le serveur local a été arrêté et le port
18081 est libre. Les 30 tests Release des classes fournisseur concernées passent
sans échec.

Cette preuve ferme la lacune mécanique « réparation bornée d'un protocole
malformé » : un seul appel de réparation suit le writer invalide et le résultat
doit repasser toute la validation des claims et citations. Elle ne qualifie pas
la capacité sémantique d'une fixture déterministe et ne remplace pas la banque
Terra 3/3. L'artefact reproductible est
`artifacts/reprise-pc-20260908/a763-local-protocol-repair-d0a844d5-20260912`.
Le produit reste `TESTE_NON_APPROUVE`.
