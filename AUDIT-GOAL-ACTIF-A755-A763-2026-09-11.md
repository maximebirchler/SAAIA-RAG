# Audit du Goal actif — frontière locale et capacité avancée A755–A763

Date : 11–12 septembre 2026

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

Une étude provisoire a utilisé comme hypothèse le Public Endpoint RunPod
`Qwen/Qwen3-32B-AWQ`, exposé à
`https://api.runpod.ai/v2/qwen3-32b-awq/openai/v1`. RunPod documente une fenêtre
de 32 768 tokens et un prix uniforme de 10 USD par million de tokens :

- https://docs.runpod.io/public-endpoints/models/qwen3-32b
- https://docs.runpod.io/public-endpoints/ai-coding-tools

Sur les 39 jobs externes multi-appels déjà terminés, la médiane observée est de
10 190 tokens et le percentile 90 de 11 713 tokens. Au tarif RunPod annoncé,
douze jobs coûteraient environ 1,22 USD à la médiane et 1,41 USD au percentile
90. Une enveloppe théorique de 3 USD couvrirait donc les variations et une
éventuelle réparation de protocole. Cette estimation n'est ni une autorisation
d'achat, ni une sélection finale de modèle, d'endpoint ou de tarif.

Le garde-budget persistant s'applique désormais à tout fournisseur externe, et
pas seulement à OpenAI. Le profil RunPod utilise son propre registre, ses propres
tarifs et refuse de démarrer sans budget explicitement autorisé. Le lanceur
commun est `tools/test-advanced-product-path-provider.ps1`. Les façades
`tools/test-advanced-product-path-openai.ps1` et
`tools/test-advanced-product-path-runpod.ps1` isolent les valeurs par
fournisseur. Depuis `107fffe`, la façade RunPod exige explicitement l'URL, le
modèle, les trois tarifs et le budget ; elle n'embarque plus cette hypothèse
historique. Aucun compte, crédit, endpoint privé, secret ou appel payant RunPod
n'a été créé à ce stade.

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
2. Sélectionner puis qualifier un candidat open source autour de 32B sur RunPod
   avec le même contrat. Le lanceur est prêt ; il manque le choix documenté du
   profil, l'autorisation de dépense dédiée, les crédits et une clé RunPod.
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

## Affinité durable du fournisseur et audit de l'architecture hybride

L'audit SQL a trouvé une bascule implicite possible pendant la reprise d'un job
avancé. `TryClaimAsync` réécrivait `provider_key` lors de chaque nouvelle prise
de bail. Un job commencé avec OpenAI puis récupéré après redémarrage sous RunPod
ou `customer-server` pouvait donc mélanger deux modèles et réutiliser la trace
d'outils du premier essai.

Le commit `3658d9cf` lie maintenant le job au couple fournisseur/modèle lors de
sa première prise de bail. La migration 067 persiste `provider_model`. Une
reprise avec un couple différent termine explicitement le job avec
`provider_configuration_changed`, conserve l'identité initiale et n'appelle pas
le fournisseur de remplacement. Le client affiche une explication spécifique,
ne publie aucun contenu non validé et conserve le couple lié dans son état
durable.

La preuve PostgreSQL réelle exécute trois tests sur des bases temporaires : le
scénario `openai-dev/terra-v1` vers `customer-server/qwen-v2` échoue fermé avec
zéro appel du remplacement, tandis que la reprise avec une identité inchangée
continue et revalide les preuves. Les trois tests passent et les bases sont
supprimées. La solution Release totalise ensuite 4 391 réussites, zéro échec et
deux sondes live opt-in non exécutées. Le vrai provider HTTP loopback repasse sur
le SHA exact avec trois appels, vingt claims et vingt preuves.

Le commit `4cbca2d5` complète cette fermeture : l'identifiant de modèle n'est
plus tronqué pour l'affinité. Une valeur de plus de 256 caractères échoue avant
l'appel HTTP avec `advanced_llm_model_invalid`. Les 2 147 tests backend passent,
zéro échec et une sonde live opt-in ignorée. Le loopback exact de ce commit
repasse avec trois appels, vingt claims, vingt preuves, zéro sortie réseau et
les ports 1234, 5123 et 18081 libres.

La cartographie finale distingue deux flux. Le chemin simple reste entièrement
local et diffuse les chunks via `ILlmProvider.StreamAsync`. Le chemin complexe
crée un handoff explicite puis un job backend durable ; il expose des révisions,
la progression des outils et une reprise WinUI, puis publie la réponse complète
après validation du JSON, des claims et des citations. Il ne diffuse donc pas
encore les tokens du Writer avancé. Cet écart UX est documenté comme ouvert :
une évolution ne devra jamais afficher un JSON incomplet ou des citations non
validées.

ADR et matrice complète :
`ADR-2026-09-12-A763-AFFINITE-FOURNISSEUR-MODELE.md`. Les portes sémantiques ne
changent pas : banque Terra 3/3 sur `b20fcc2` ou descendant, RunPod autorisé,
serveur client réel, inspection terminale WinUI et holdout aveugle. Produit
`TESTE_NON_APPROUVE`.

## A763 — plafond OpenAI Free persistant malgré l'achat — 2026-09-12

Le tableau de bord OpenAI a été relu après actualisation. La facture du 11
septembre est marquée `Paid` pour 27,03 USD TTC, le solde prépayé est de 23,92
USD et les coûts enregistrés sont de 1,08 USD. La page Limits décrit le passage
automatique au Tier 1 dès 5 USD d'achats cumulés, mais l'organisation reste au
`Free tier`. Terra conserve donc 10 000 TPM, 3 RPM et 50 RPD.

Le bouton `Upgrade tier` ouvre seulement un nouvel achat de crédits. Aucun achat
n'a été confirmé. La limite de dépense de l'organisation est déjà à 100 USD et
ne contrôle pas le plafond RPD. Il n'existe pas de réglage utilisateur visible
pour supprimer 50 RPD : la promotion automatique attendue n'a pas été appliquée
par OpenAI. La campagne Terra 3/3 reste suspendue pour préserver les crédits et
la causalité jusqu'à activation du Tier 1 ou réponse du support.

## A755 — non-régression locale après l'architecture avancée — 2026-09-12

La banque adversariale déjà vue de quatorze cas a été rejouée trois fois sur le
SHA `5516cc1a`, avec le Qwen3-4B Q5_K_M et le runtime CUDA qualifiés sur ce PC.
Le backend distant est resté limité au retrieval en lecture seule. Les trois
harnesses terminent avec 42/42 lignes, zéro erreur, zéro appel externe, aucune
mutation serveur et le port 1234 libéré.

La revue séparée accepte les quatorze cas et les quarante-deux lignes. VACUUM
reste exact et sourcé 3/3 en français et en anglais. Les six fonctions NIST
restent dans l'ordre et sans surplus descriptif 3/3. Les deux limites de budget,
les six transferts précoces, les deux clarifications et l'insuffisance du fichier
absent conservent leurs terminaux attendus, sans source publiée à tort. Les
quatorze médianes passent leurs seuils préinscrits; la plus lente est P02 à
22 152 ms et le transfert pré-retrieval le plus lent est P11 à 4 941 ms.

Assessment :
`artifacts/reprise-pc-20260908/a755-client-model-capability-boundary/e-lane-a755-frontier-regression-5516cc1-20260912-01/semantic-assessment.v1.json`,
SHA-256 `F9830737A07B33FA85EEAD87B23AF0254C7E57EEFF6B69F32FEE7861C29C38A8`.
Cette preuve confirme une non-régression sur une banque connue; elle ne remplace
pas le nouveau holdout aveugle exigé pour l'acceptation finale.

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

## Audit de clôture contre la mission LLM — 2026-09-12

La mission initiale demandait un fournisseur interchangeable directement dans
le client. La clarification produit ultérieure demande en plus un parcours
hybride : le petit modèle reste sur le poste, puis transfère les cas complexes
à un grand modèle piloté par le backend. Le code conserve donc deux frontières
qui ne se concurrencent pas :

```text
Sonde DEV/BENCH directe
  Agent Windows -> ILlmProvider -> Local | OpenAiDev | RunPodBench

Parcours produit hybride
  Agent Windows Local -> handoff -> job backend
                      -> IAdvancedAnalysisProvider
                      -> openai-dev | runpod-bench | customer-server
```

`ILlmProvider` permet de comparer Router, tools et Writer sans réécrire l'agent.
`IAdvancedAnalysisProvider` porte la durabilité, l'isolation tenant, la
revalidation des preuves et la cible serveur client. PostgreSQL, Qdrant,
l'ingestion et le retrieval restent dans SAAIA. Cette séparation matérialise la
vision actuelle sans déplacer le RAG chez OpenAI, RunPod ou le serveur LLM.

| Exigence | Verdict | Preuve ou limite restante |
|---|---|---|
| Fournisseur client commun Local/OpenAI/RunPod | Validé mécaniquement | `ILlmProvider`, factory unique, 20 scénarios d'architecture incluant constructeur provider obligatoire, Router structuré et Writer SSE |
| Fournisseur serveur commun OpenAI/RunPod/client | Validé mécaniquement | `IAdvancedAnalysisProvider` et une implémentation OpenAI-compatible partagée |
| Un couple fournisseur/modèle par exécution avancée | Validé causalement | Affinité SQL persistante, reprise avec identité différente fermée sans appel du remplacement |
| Aucun fallback implicite | Validé | Échec typé du fournisseur sélectionné ; changement de configuration refusé |
| Petit modèle sur ce PC | Validé sur banque connue | 14 cas × 3, 42/42 acceptés, seuils de latence respectés, zéro appel externe |
| Frontière local/clarification/insuffisant/avancé | Validée sur banque connue | Terminaux attendus 3/3 ; holdout aveugle encore requis |
| Terra réel sur le parcours produit | Validé partiellement | Handoff, Planner, tools, Writer, tokens, coût et persistance observés ; banque finale incomplète |
| Planning 5 × 4 | Rejeté sur la dernière observation | Forme complète mais relation cellule/preuve insuffisante ; contrat corrigé dans `b20fcc2`, nouveau 3/3 requis |
| RunPod | Prêt mécaniquement | Endpoint, modèle, profil et harnais configurables ; aucun endpoint ni budget RunPod autorisé |
| Serveur LLM du client | Prêt architecturalement | Profil `customer-server` et compose préparés ; matériel et modèle final absents |
| Structured output et citations | Validé mécaniquement | JSON refusé s'il est invalide, réparation unique bornée, claims et preuves revalidés |
| Streaming direct Local/OpenAI/RunPod | Validé mécaniquement | Chunks SSE normalisés par le même contrat jusqu'à l'UI |
| Chemin avancé durable | Validé mécaniquement | Progression et résultat atomique après validation ; reprise réelle du même job après deux lancements WinUI |
| Streaming token par token avancé | Ouvert | La réponse avancée reste atomique pour ne pas exposer un JSON ou des citations non validés |
| Timeout, annulation, HTTP et réseau | Validé mécaniquement | `4744d81` normalise aussi les incidents pendant la lecture du corps HTTP ; annulation appelant distincte |
| Secrets et politique externe | Validé mécaniquement | modes externes désactivés par défaut, autorisations explicites, secretRef/DPAPI, redaction du support bundle |
| Budget Terra 25 USD | Validé mécaniquement | journal persistant, réservation avant appel, plafonds global/job/appels, coût issu de l'usage fournisseur |
| Licence et installateurs | Vision consignée, hors lot | droits, topologie et profil sont séparés ; assistant d'installation à implémenter après qualification |

Le commit `4744d81` corrige le dernier défaut de résilience trouvé pendant cet
audit. Avant ce commit, une annulation interne ou une rupture réseau après les
en-têtes HTTP pouvait échapper à la normalisation du fournisseur avancé. Après
correction, un timeout devient `advanced_llm_timeout`, une rupture devient
`advanced_llm_transport_error`, les détails privés de transport ne remontent
pas, et une annulation utilisateur reste une `OperationCanceledException`.
Les 33 tests fournisseur ciblés passent. La suite backend Release rapporte
2 150 réussites, zéro échec et une sonde live opt-in ignorée. L'assessment
`artifacts/reprise-pc-20260908/a763-backend-resilience-20260912/assessment.v1.json`
a pour SHA-256
`0AA1F4C2DFA56CC4AE75AA3D227035CD69E7AC0067B34686F817BC7F91191B0B`.

Les portes qui empêchent encore honnêtement l'approbation produit sont :

1. promotion OpenAI Tier 1 ou réponse du support, puis banque Terra complète
   3/3 sur `b20fcc2` ou un descendant documentaire ;
2. revue sémantique source par source, avec trois plannings 5 × 4 acceptés ;
3. inspection terminale dans WinUI d'une réponse avancée réussie et de chaque
   carte source ;
4. nouveau holdout aveugle produit sans exposer questions et oracles à la
   session de développement ;
5. benchmark RunPod autorisé, puis essai du modèle retenu sur un serveur client.

La promotion Tier 1 est la seule porte externe qui bloque actuellement la
campagne Terra. Le dashboard indique encore `Free tier`, 50 RPD et un solde
prépayé disponible. Aucun réglage utilisateur ne retire ce plafond ; l'achat
supplémentaire proposé par `Upgrade tier` n'a pas été confirmé. Le produit reste
`TESTE_NON_APPROUVE`.

## A763 — télémétrie d'appel avancée et erreurs compréhensibles — 2026-09-12

Le commit `09207d6` complète l'observabilité du chemin serveur externe. Chaque
ligne du registre Terra/RunPod contient désormais un `requestId` unique, le
`traceId` et le `jobId`, le fournisseur, le modèle, le rôle, la durée, le nombre
de tentatives HTTP et de retries, les tokens, le coût et l'erreur normalisée.
Le champ TTFT reste nul parce que ce chemin demande un JSON complet non streamé.
Le registre ne reçoit toujours ni prompt, ni extrait de preuve, ni endpoint, ni
secret. Le test 429 -> retry -> succès vérifie deux tentatives pour Planner,
zéro retry pour Writer et deux identifiants d'appel distincts.

Le commit `7614018` remplace le message générique WinUI par une explication
actionnable pour `advanced_llm_timeout` et
`advanced_llm_transport_error`, dans les six langues déjà supportées. Le client
continue de masquer tout payload fournisseur non validé et ne crée aucune carte
source sur un échec. Les 24 tests ciblés du transport client et les 33 tests du
fournisseur backend passent.

La validation Release sur le SHA exact `7614018` rapporte 10 tests contrats,
2 150 backend et 2 237 client réussis, soit 4 397 réussites, zéro échec et deux
probes live opt-in ignorées. L'assessment
`artifacts/reprise-pc-20260908/a763-client-provider-failure-ux-7614018-20260912/assessment.v1.json`
a pour SHA-256
`2CA13D8C48B34894B5FBCDF648454ACC58306DC773506BE1087254BDEDF8C0EA`.
Les ports 1234, 5123 et 18081 sont libres et aucun appel externe n'a été lancé.

## A763 — identité RunPod explicitement scellée — 2026-09-12

Le commit `107fffe` retire du lanceur produit RunPod les valeurs par défaut qui
figeaient un endpoint, un modèle et un tarif provisoires. Une campagne exige
maintenant `BaseUrl`, `ModelId`, le budget autorisé et les tarifs entrée, cache
et sortie. Le preflight consigne aussi le runtime, le profil, le GPU, la
quantification, le hash du modèle, la fenêtre de contexte et le coût horaire
lorsqu'ils sont fournis. Changer de candidat reste un changement de paramètres,
sans modification du Router, des tools, du RAG ou du Writer.

Les deux scripts PowerShell passent l'analyseur syntaxique avec zéro erreur. La
façade ne contient plus d'endpoint, de modèle ou de prix RunPod implicite.
L'assessment
`artifacts/reprise-pc-20260908/a763-runpod-explicit-profile-107fffe-20260912/assessment.v1.json`
a pour SHA-256
`2ED735EDD70C6B5D4CB3B9CF1980D2173A4EF7E7F79D736CDABFD6A4A3AABCB6`.
Aucun appel externe et aucune dépense RunPod n'ont été effectués.

## A763 — suppression du dernier constructeur LLM implicite — 2026-09-12

`RagChatAgent` n'expose plus le constructeur de compatibilité qui recevait un
`OpenAiLlmClient` puis lui attribuait silencieusement l'identité
Local/llama.cpp. Son unique constructeur public exige désormais
`ILlmProvider`. Le démarrage WinUI et tous les harnais concernés créent donc le
provider avec `LlmProviderFactory`, y compris lorsque l'URL et le modèle d'une
probe live diffèrent des réglages locaux persistés.

Un test d'architecture vérifie par réflexion que cette frontière ne peut pas
être réintroduite sans faire échouer la suite. Les 20 scénarios
`LlmProviderArchitectureTests`, les 47 tests ciblés provider/mémoire/transport
et la suite cliente complète passent. Le dernier résultat complet compte 2 238
réussites, zéro échec et une probe live opt-in ignorée. Aucun appel externe n'a
été exécuté pendant cette correction.

Cette validation a été rejouée sur le SHA exact `690d7d14`. L'assessment
`artifacts/reprise-pc-20260908/a763-explicit-client-provider-690d7d1-20260912/assessment.v1.json`
a pour SHA-256
`47A293EBABE2BA90374014EEFE86EA8770B1408B93550A797FB61CADF47F9ADA`.

## A763 — identité fournisseur exacte sans troncature — 2026-09-12

L'audit de l'affinité a trouvé une asymétrie résiduelle : les modèles trop longs
étaient déjà rejetés, mais une `ProviderKey` personnalisée de plus de 100
caractères était encore tronquée. Deux identités partageant le même préfixe
pouvaient alors être confondues lors d'une reprise. Le commit `d960cdb8`
conserve l'identité exacte et rejette une valeur surdimensionnée avec
`advanced_llm_provider_key_invalid` avant tout appel HTTP.

Les 50 tests ciblés provider/worker passent. La suite backend Release rejouée
sur le SHA exact compte 2 151 réussites, zéro échec et une probe live opt-in
ignorée. L'assessment
`artifacts/reprise-pc-20260908/a763-exact-provider-identity-d960cdb-20260912/assessment.v1.json`
a pour SHA-256
`1B7CEAD5AD73B001C784182361A5A2D1DF37CB97E3EDD79B6289E74EB24AE833`.
Aucun appel externe n'a été exécuté.

## A763 — candidat RunPod borné sans dépense — 2026-09-12

La recherche officielle retient comme première voie l'endpoint public
OpenAI-compatible `Qwen/Qwen3-32B-AWQ`. RunPod publie un tarif plat de 10 USD
par million de tokens et une URL dédiée ; l'enveloppe proposée pour les quatre
cas répétés trois fois est limitée à 5 USD, avec alerte à 4 USD, arrêt local à
4,80 USD et plafond de 0,40 USD par job. L'endpoint public évite une location
GPU horaire et permet de valider d'abord la qualité du parcours produit.

La preuve aura une limite assumée : cet endpoint ne publie ni GPU, ni hash des
poids, ni révision de runtime. Il qualifie la fonction et le coût, pas la
reproductibilité matérielle. Un second candidat Serverless privé à base de
`Qwen/Qwen3-30B-A3B-Instruct-2507` n'est envisagé que si le premier échoue pour
une cause sémantique ou si une infrastructure scellée devient nécessaire.

Le profil exact, les calculs, les critères d'acceptation et les sources sont
consignés dans
`documents/agent/runpod-benchmark-candidates-a763-2026-09-12.md`. Aucun compte,
crédit, endpoint, pod, secret ou appel RunPod n'a été créé. Une autorisation
explicite de sortie des extraits de preuve et de dépense maximale de 5 USD reste
requise avant la première requête. La façade RunPod transmet maintenant aussi
le délai borné entre cas au moteur commun, comme la façade Terra ; son analyse
PowerShell rapporte zéro erreur. Produit `TESTE_NON_APPROUVE`.

## A763 — modèle demandé et modèle observé séparés — 2026-09-12

Le commit `a0202c8` ajoute `observedModelId` à chaque ligne de télémétrie d'un
appel avancé réussi lorsque la réponse OpenAI-compatible contient un champ
`model` exploitable. `modelId` reste l'identité demandée, persistée pour
l'affinité du job. Les deux valeurs ne sont pas forcées à être identiques : un
alias OpenAI peut légitimement résoudre vers un snapshot nommé différemment.

Cette observation améliore la preuve RunPod sans surinterpréter l'endpoint
public. Une valeur absente, vide, non textuelle ou supérieure à 256 caractères
n'est pas journalisée. Les prompts, preuves, endpoints et secrets restent
exclus du registre. Le test RunPod simulé vérifie deux appels et deux identités
observées `Qwen/Qwen3-32B-AWQ`.

La suite backend Release sur le SHA exact compte 2 151 réussites, zéro échec et
une probe live opt-in ignorée. L'assessment
`artifacts/reprise-pc-20260908/a763-observed-model-a0202c8-20260912/assessment.v1.json`
a pour SHA-256
`2378E7BB55EECA1F2F89BE9C55D13D62922DE022048454736F32AA0AF4DF9DDD`.
Aucun appel externe n'a été exécuté et les ports 1234, 5123 et 18081 sont libres.
Produit `TESTE_NON_APPROUVE`.

## A763 — profil RunPod exécutable figé et préflight sans réseau — 2026-09-12

Le commit `0ddaac68` transforme les paramètres proposés en un profil versionné
`config/runpod-benchmark.a763.json` et ajoute
`tools/test-runpod-campaign-profile.ps1`. Le profil scelle l'endpoint public
Qwen3 32B AWQ, le ModelId, le runtime, le profil, la quantification, le contexte,
les trois prix à 10 USD/M, les quatre cas, trois répétitions et les plafonds
5/4/4,80/0,40 USD. Son SHA-256 est
`C1925F2BF6885A10B4A36469CB1AF3E787935143A81BED305A5B74FAF0E22DF6`.

Le lanceur sépare validation et exécution. Sans option, il valide et scelle
uniquement la configuration : aucun secret n'est lu et aucun appel externe
n'est exécuté. Toute exécution exige `-Execute` et
`-ExternalContentAuthorized`; les stages produit exigent aussi le fichier
d'environnement serveur. Une tentative sans autorisation de contenu a été
refusée avant secret et réseau.
L'analyseur PowerShell rapporte zéro erreur ; les douze jobs attendus et le
maximum théorique de quarante-huit appels sont consignés. Assessment :
`artifacts/reprise-pc-20260908/a763-runpod-profile-0ddaac6-20260912/assessment.v1.json`,
SHA-256 `7F075B02C5A034FC05711738592EEE55AE2EEBE90B98571CD8C55E9CC0A0D095`.
Les ports 1234, 5123 et 18081 sont libres. Ce préflight prouve la cohérence de
la campagne, pas la qualité de Qwen ni la disponibilité RunPod. Aucun compte,
crédit, clé ou appel RunPod n'a été créé. Produit `TESTE_NON_APPROUVE`.

## A763 — coût RunPod recalculé sur douze jobs existants — 2026-09-12

Le proxy de coût sélectionne les trois jobs réussis les plus récents pour
chacun des quatre cas avancés dans les artefacts Terra/Luna, déduplique les
résultats par date et cas, puis applique le tarif RunPod public de 10 USD par
million de tokens. Les douze jobs totalisent vingt-quatre appels, 90 590 tokens
d'entrée et 12 820 de sortie. Le coût contrefactuel est 1,0341 USD ; le job le
plus volumineux représente 0,12461 USD.

Cette mesure ne vaut ni exécution RunPod ni prédiction exacte : Qwen peut
produire davantage de tokens et déclencher la réparation bornée. Elle confirme
cependant que l'enveloppe proposée de 5 USD conserve une marge importante par
rapport au volume réellement observé sur le même pipeline. Assessment :
`artifacts/reprise-pc-20260908/a763-runpod-cost-proxy-20260912/assessment.v1.json`,
SHA-256 `2880CE27D14A3926EE7333660B96C2BAB9B06C9A1ABD58A8C459BB29AF50E6AB`.
Aucun appel externe et aucun coût nouveau. Produit `TESTE_NON_APPROUVE`.

## A763 — aucun plafond projet retirable derrière les 50 RPD — 2026-09-12

L'audit authentifié en lecture seule de la page `Default project / Limits`
confirme que le projet ne porte aucun plafond de dépense propre. Les limites de
modèle sont héritées de l'organisation sauf surcharge ; Terra y affiche déjà
10 000 TPM et 3 RPM, exactement les maxima du palier Free de l'organisation.
L'éditeur projet ne propose aucune surcharge RPD. Le plafond de 50 RPD vient
donc exclusivement du palier d'organisation et ne peut pas être retiré dans les
paramètres du projet.

La page organisation reste à `Free tier`, 1,08 USD consommé sur un plafond de
100 USD, et affiche toujours 5 USD comme seuil d'achat du Tier 1. Le plafond de
dépense de 100 USD est une barrière financière indépendante : le supprimer ne
modifierait ni le palier ni les 50 RPD. Aucun réglage, achat ou appel modèle n'a
été effectué. Assessment :
`artifacts/reprise-pc-20260908/a763-openai-limit-audit-20260912/assessment.v1.json`,
SHA-256 `B4F5E5A882818D91E6DC7DEEB8A70219923CA679B3D9B55E6CC3BEC3DCFF0332`.
Produit `TESTE_NON_APPROUVE`.

## A763 — entitlement avancé appliqué avant provider et worker — 2026-09-12

L'audit de la vision d'installation a trouvé que l'endpoint refusait les
nouveaux jobs lorsque `AdvancedAnalysisEnabled=false`, mais que le worker ne
revérifiait pas ce droit et que le provider configuré pouvait encore être
résolu. Un ancien job en file risquait donc d'être traité après révocation de
la capacité si `WorkerEnabled` restait actif.

Le commit `56ae3a73` ferme ces deux chemins. La factory DI retourne le provider
désactivé avant d'interpréter l'identité ou le secret du fournisseur. Le worker
vérifie l'entitlement avant toute récupération de bail ou connexion base. Deux
tests prouvent qu'une configuration de provider volontairement invalide est
ignorée sous licence désactivée et qu'aucun accès base ou appel provider n'est
tenté.

Les 62 tests avancés ciblés passent. La suite backend Release sur le SHA exact
compte 2 153 réussites, zéro échec et une probe live opt-in ignorée. Assessment :
`artifacts/reprise-pc-20260908/a763-license-gate-56ae3a7-20260912/assessment.v1.json`,
SHA-256 `C02B7BFB2E2AD7B89A042EB9D721E1849157AE3409F9CA4BADA20F0A9E01E9A6`.
Ce correctif applique le premier entitlement existant ; le schéma commercial
complet et les installateurs restent hors du lot actuel. Aucun appel externe et
aucun coût nouveau. Produit `TESTE_NON_APPROUVE`.

## A763 — identité runtime RunPod rendue obligatoire — 2026-09-12

Le commit `8a3bcd0` retire la dernière attribution implicite de `llama.cpp` aux
profils RunPod. Le template versionné conserve maintenant le mode `Local` mais
laisse vide l'identité runtime RunPod. Un benchmark RunPod doit fournir cette
identité par `SAAIA_RUNPOD_RUNTIME` ou par les paramètres obligatoires des
lanceurs. Le provider direct, la sonde directe, la sonde serveur et le parcours
produit refusent ainsi de sceller une métadonnée inventée.

Cette règle permet d'identifier correctement le candidat public par
`runpod-public-openai`, tout en conservant `llama.cpp`, `vLLM` ou un autre
runtime pour un endpoint privé réellement observé. Les quatre scripts passent
l'analyseur PowerShell. La suite cliente Release sur le SHA exact compte 2 238
réussites, zéro échec et une probe live opt-in ignorée. L'assessment
`artifacts/reprise-pc-20260908/a763-explicit-runpod-runtime-8a3bcd0-20260912/assessment.v1.json`
a pour SHA-256
`D26C9629982F978509E360104F2CC99D194434DB03CDAB03A8AC0786E31EF74A`.
Aucun appel externe n'a été exécuté et les ports temporaires sont libres.
Produit `TESTE_NON_APPROUVE`.

## A755/A763 — méthode du nouveau holdout aveugle préinscrite — 2026-09-12

Le document `documents/agent/blind-holdout-protocol-a763-v1.md` fixe désormais
la porte d'ouverture, la séparation des rôles, la couverture minimale, le
manifeste public sans question, l'exécution unique et les critères de rejet.
Il exige un constructeur-évaluateur indépendant, 24 cas nouveaux couvrant les
terminaux local, clarification, insuffisance, avancé et la frontière proche,
ainsi que les six langues du client. Zéro fait non soutenu, citation forgée ou
relation claim/preuve fausse est toléré pour l'approbation.

Le protocole interdit de créer la banque avant validation des campagnes connues,
inspection WinUI, choix du fournisseur final et gel du commit. Après création,
toute correction sémantique invalide le payload sans l'ouvrir. Seuls les hashes,
comptes agrégés, identités runtime et budgets sont visibles avant le verdict ;
les détails ne sont dévoilés qu'après scellement du verdict et la banque devient
alors consommée. Aucun nouveau cas, oracle ou payload caché n'a été produit ou
consulté pendant ce jalon. Produit `TESTE_NON_APPROUVE`.

## A763 — URL finale RunPod vérifiée avant réseau — 2026-09-12

Le commit `e88973d2` complète le sceau du profil avec l'URL exacte construite
pour le chat :
`https://api.runpod.ai/v2/qwen3-32b-awq/openai/v1/chat/completions`. Le préflight
valide le schéma HTTPS et l'identité d'hôte. Le test backend ciblé du provider
OpenAI-compatible confirme en parallèle l'ajout de `/chat/completions` à une
base configurée et passe 1/1.

L'analyseur PowerShell rapporte zéro erreur, aucun secret n'est lu, aucun appel
externe n'est exécuté et les ports 1234, 5123 et 18081 restent libres.
Assessment :
`artifacts/reprise-pc-20260908/a763-runpod-final-url-e88973d-20260912/assessment.v1.json`,
SHA-256 `43B8CA39A2D7E0AA7293BA7EB2F771FF52E7A8506F253250DB667C5245EB730E`.
Cette preuve ferme le risque de composition d'URL ; la disponibilité du service
et la qualité de Qwen restent non testées. Produit `TESTE_NON_APPROUVE`.

## A763 — dépense RunPod découpée en trois étapes gardées — 2026-09-12

L'audit a trouvé que le premier profil exécutable pouvait envoyer directement
les douze jobs alors que la préinscription exigeait un contrôle progressif. Le
commit `90e98a24` corrige cet écart. `Probe` est désormais le stage par défaut et
borne la sonde synthétique à deux appels. `MealGrid` lance seulement le planning
5 x 4, une fois et avec quatre appels maximum. `FullBank` lance les douze jobs,
mais exige `-FullBankAuthorized` après revue du planning.

Les trois profils de stage passent le préflight sur le SHA exact. La
compatibilité des paramètres avec les deux runners enfants est contrôlée. Les
gardes refusent avant secret et réseau : une sonde sans autorisation de contenu,
un planning sans environnement serveur et une banque complète sans autorisation
de revue. Les trois scripts passent l'analyseur PowerShell ; les ports 1234,
5123 et 18081 sont libres. Assessment :
`artifacts/reprise-pc-20260908/a763-runpod-stages-90e98a2-20260912/assessment.v1.json`,
SHA-256 `9A241C69BBB03453DAFCB24D0B9B8B21FC526062903C0983134E22046BDBB984`.
Aucun appel externe et aucun coût nouveau. Produit `TESTE_NON_APPROUVE`.

Le commit `1d558f23` ferme ensuite un écart entre le sceau et le budget guard :
`Probe` applique réellement deux appels maximum par job. Une réparation après
les deux appels prévus est refusée avant HTTP, au lieu de pouvoir consommer un
troisième appel puis échouer sur l'assertion du test. `MealGrid` et `FullBank`
restent à quatre appels par job. Les trois sceaux et deux tests ciblés du
provider et du budget guard passent. Assessment :
`artifacts/reprise-pc-20260908/a763-runpod-probe-cap-1d558f2-20260912/assessment.v1.json`,
SHA-256 `ED60FA58743655E999227216CAB2F45F9EFD68D89DCA86DBC8DC3CCCF19E0495`.
Aucun appel externe et aucun coût nouveau. Produit `TESTE_NON_APPROUVE`.

## A763 — préflight RunPod compatible Windows PowerShell 5.1 — 2026-09-12

L'exécution de la commande publiée dans le runbook a révélé que
`Path.GetRelativePath` n'existe pas dans le runtime .NET de Windows PowerShell
5.1. Le préflight s'arrêtait avant d'écrire son sceau, alors que le même script
passait sous PowerShell 7. Le commit `ec954ff6` remplace cet appel par une
conversion URI compatible, sans changer la validation du profil ni les gardes
d'exécution.

La commande exacte `powershell -NoProfile -ExecutionPolicy Bypass` a ensuite
produit les trois sceaux sur le SHA propre `ec954ff6` : Probe 2 appels maximum,
MealGrid 4 et FullBank 48. Les trois indiquent le chemin relatif attendu,
`repositoryTrackedDirty=false`, `secretReadByPreflight=false` et
`externalCallExecutedByPreflight=false`. Version testée : Windows PowerShell
5.1.26100.9444. Assessment :
`artifacts/reprise-pc-20260908/a763-windows-powershell-preflight-ec954ff6-20260912/assessment.v1.json`,
SHA-256 `843EF528C3C13D0B346518CBFF908E667F1957751D5C9B6E7E4C0F667DE00681`.
Produit `TESTE_NON_APPROUVE`.
