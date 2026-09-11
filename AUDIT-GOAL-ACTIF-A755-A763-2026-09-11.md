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

Les appels réels à OpenAI démontrent que Terra peut produire les livrables
complexes visés, y compris un planning de repas de vingt cellules. Ils ont aussi
révélé puis permis de corriger des défauts génériques de requêtage, de sélection
des preuves, de validation sémantique et de protocole. L'état courant n'a pas
encore trois réussites consécutives sur l'ensemble de la banque avancée après le
dernier gel. Le produit reste donc `TESTE_NON_APPROUVE`.

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

Le journal local enregistre 50 appels Terra, dont 44 réussis et 6 rejets, pour
216 886 tokens d'entrée, 37 979 tokens de sortie et 0,8412062 USD. Il enregistre
48 appels Luna réussis, 175 176 tokens d'entrée, 19 768 tokens de sortie et
0,05709756 USD. Le total journalisé est 0,89830376 USD. L'interface OpenAI
affiche 0,92 USD consommé et un solde de 24,09 USD ; le faible écart correspond
aux appels ou arrondis hors journal applicatif.

Les garde-fous SAAIA restent : 25 USD autorisés, alerte à 20 USD, arrêt local à
24 USD, 0,50 USD maximum par job et quatre appels maximum par job. Ils protègent
le budget demandé et sont indépendants des limites de débit OpenAI.

Le compte OpenAI affiche encore `Free tier` alors que l'achat de 25 USD dépasse
le seuil de 5 USD annoncé pour Tier 1. Les modèles Terra et Luna restent limités
à 50 requêtes par jour. Cette limite fournisseur ne peut pas être supprimée dans
l'interface. Le bouton de changement de palier propose seulement un nouvel achat
de crédits ; aucun achat supplémentaire n'a été effectué.

## Vérifications de l'état courant

- tests ciblés fournisseur, worker et validations : 44/44 ;
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
2. Confirmer live la réparation bornée d'une réponse de protocole mal formée.
3. Qualifier un modèle open source via RunPod avec le même contrat, après choix
   concret d'un endpoint, d'un budget et d'un secret RunPod.
4. Exécuter le même protocole sur le serveur final du client lorsque son matériel
   et son modèle seront disponibles.
5. Fermer puis relancer le vrai WinUI pendant un job long, vérifier la reprise du
   même job et ouvrir les cartes source exactes.
6. Ouvrir un nouveau holdout aveugle après le gel définitif du code.

Le palier RunPod, l'hébergement client et le holdout aveugle dépendent de moyens
externes encore absents. Ils ne justifient pas de modifier deux fois
l'architecture : le contrat unique permet de changer d'endpoint sans changer la
logique documentaire.

## Prochaine séquence

L'état courant doit être commité et poussé avec ses tests et son audit. Ensuite,
dès que le quota Terra est disponible, la banque avancée est rejouée trois fois
sans modifier les critères. Un résultat sémantiquement incorrect réouvre la
cause précise ; trois passages complets autorisent le gel du candidat API. La
qualification RunPod, le test WinUI réel et le holdout aveugle restent ensuite
les dernières preuves avant toute approbation produit.
