# Rapport correctif ultra-complet — SAAIA RAG — dernières 24 heures

## 1. Périmètre, objet et statut de ce rapport

- Fenêtre couverte : **2026-08-30 17:02 → 2026-08-31 17:02**, heure de Zurich.
- Dépôt : `C:\Users\MBirchler\Desktop\ecom\SAAIA\GIT\RAG`.
- Branche : `SAAIA_V3.1`.
- HEAD vérifié : `5f35881cdc67d12a076fcd2a7a1004656ac9a37a`.
- Paliers couverts : **A571 à A629**.
- Ce rapport remplace, pour la période nocturne, le rapport envoyé le 30 août à
  23:04 : ce premier document s'arrêtait au checkpoint A608 et ne pouvait donc
  pas décrire A609–A629.

Le statut global reste volontairement inchangé :

- produit : **`TESTE_NON_APPROUVE`** ;
- phase 5 : **active** ;
- Goal : **actif** ;
- aucun commit, reset, clean, checkout destructif ou mass-stage n'a été fait ;
- A630 est le prochain palier autorisé, mais il n'a pas encore été exécuté.

## 2. Résumé exécutif

Les dernières 24 heures ont produit quatre types de résultats.

1. **Cinq défauts génériques ont été isolés ou corrigés par contrats.** Les
   travaux ont porté sur la récupération après un scope sans résultat, la
   distinction sujet/document, la provenance forte des cartes, le budget LLM
   cumulatif, la terminalité sous réserve de budget et la conservation d'une
   référence documentaire canonique décidée par Qwen.
2. **Cinq campagnes live distinctes ont été menées sans replay** : C002, C003,
   C004, C005 et C006 ont échoué et ont été conservées comme échecs probants ;
   C007 a ensuite réussi sur les axes fonctionnel, provenance et WinUI, mais a
   échoué sur la performance.
3. **Le premier succès live réellement complet sur la couture documentaire
   récente a été obtenu avec C007.** La réponse contenait les quatre catégories
   attendues, la preuve canonique page 11, une citation `[E1]`, une carte WinUI
   cliquable et l'ouverture du bon PDF au bon passage. La durée de 137,055 s
   interdit toutefois toute approbation globale.
4. **Une nouvelle architecture proportionnelle writer → reviewer a été
   préenregistrée, implémentée derrière un flag désactivé par défaut et validée
   déterministement.** Elle vise à réduire les six appels LLM observés sur C007
   sans retirer à Qwen la décision sémantique. Les régressions A629 passent,
   mais la comparaison appariée Qwen A630 reste à faire.

L'état honnête à la fin de la fenêtre est donc : **architecture et contrats
nettement renforcés, premier PASS live fonction/provenance/UI obtenu, mais
performance non conforme et nouveau chemin proportionnel pas encore validé en
conditions Qwen appariées.**

## 3. Fil directeur architectural conservé

Tous les travaux ont été conduits sous les contraintes suivantes :

- Qwen reste l'orchestrateur sémantique et le décideur final ;
- le code ne choisit ni la réponse métier, ni la bonne source par heuristique de
  domaine ;
- le code gère les contrats mécaniques, états, budgets, traces, identités,
  EvidenceIds et conditions de publication ;
- l'`EvidenceBundle` canonique doit rester cohérent du retrieval au writer, au
  vérificateur puis aux cartes WinUI ;
- une source finale doit être exacte, réconciliée, visible et cliquable ;
- les échecs live ne sont ni rejoués ni requalifiés après coup ;
- aucune règle spécifique à Cuisine, NIST, Microsoft, Adelphi, à une langue ou
  à un canari n'est autorisée dans le produit.

## 4. Chronologie détaillée

### 4.1. A571–A575 — C002 TF6220, défaut backend et liveness

Le canari C002 a été sélectionné en lecture seule sur un document TF6220, puis
préenregistré avec sa question, sa preuve et ses critères avant exécution.

Le préflight A572 a découvert un défaut backend réel autour de `Math.Clamp` :
la première matrice a fait **5/6**, puis la correction a porté la matrice à
**6/6**. Les suites suivantes ont passé **866/866** sur le périmètre concerné et
**2 054/2 054** sur le backend canonique. Le redéploiement et l'audit final ont
réussi, avec **90/90** contrôles.

Le live C002 A573 a néanmoins échoué de façon probante :

- réponse prudente mais aucune carte source publiable ;
- **42 269 tokens**, au-delà du futur plafond de 12 000 ;
- **385 911 ms**, au-delà du hard stop de 240 000 ms ;
- boucle sémantique jusqu'au tour 16 ;
- aucune seconde tentative.

A574–A575 ont ensuite corrigé deux contrats génériques :

- canonisation de `explicit_set + count=1` vers `single_item` ;
- possibilité pour Qwen de terminer lorsque plus aucun audit de candidat n'est
  mécaniquement réalisable.

Les suites ont passé **397/397**, puis **1 676/1 676** côté client. Une première
suite client avait fait 1 675/1 676 uniquement parce que le runner mesurait
2 540 lignes ; le contrat a été extrait dans un module et le runner est revenu
à 2 496 lignes. Le seuil de modularité n'a pas été augmenté. Ces corrections
n'ont pas été revalidées par replay de C002.

### 4.2. A576–A585 — C003 et BUG-135, sujet nommé versus document nommé

Le canari C003 TF1750 a été sélectionné, audité et préflighté. Les contrôles ont
passé **78/78**, **80/80**, puis la suite client **1 676/1 676**.

Le live C003 A579/A580 a échoué après **42 303 ms** et **2 708 tokens**, sans
exécuter d'outil documentaire. La cause : Qwen avait placé le sujet
`PTFE TF 1750` dans le champ réservé à un titre ou fichier documentaire exact.
Le résolveur exact se comportait donc correctement en refusant la résolution,
mais le contrat d'état ne distinguait pas assez clairement sujet et document.

BUG-135 a été traité par une chaîne test-first complète :

- préenregistrement A581 ;
- RED A582 : **0/11** ;
- GREEN A583 : **12/12** ;
- portefeuille A584 : **440/440** ;
- client complet : **1 687/1 687** ;
- audit final A585 : **68/68**.

La correction ne rend pas la résolution floue. Qwen doit déclarer si la
référence est un sujet/une entité ou un document/titre/fichier ; le code ne fait
que vérifier la cohérence de cet état et empêcher une fausse insuffisance
immédiate. BUG-135 est corrigé déterministement, pas revalidé live.

### 4.3. A586–A595 — C004 et BUG-136, provenance des cartes

Le canari C004 ABB 266 HART a passé sa sélection, son audit et son préflight :
**60/60**, **79/79** et portefeuille **440/440**.

Le live C004 A589/A590 a échoué :

- la recherche contenait la preuve utile `Easy Setup` ;
- Qwen a retenu une carte faible `Integrated LCD display available` ;
- le hash catalogue ne comptait que 32 caractères et a été correctement refusé
  par le vérificateur SHA-256 ;
- durée **247 218 ms** ;
- consommation **17 518 tokens** ;
- aucune promotion et aucun replay.

BUG-136 a renforcé la provenance sans affaiblir le vérificateur :

- RED : **7/17** ;
- GREEN : **17/17** ;
- ciblé : **377/377** ;
- backend : **2 054/2 054** ;
- client : **1 704/1 704** ;
- audit : **127/127**.

Le code peut hydrater l'identité forte depuis une source compatible du même
document et de la même révision. Une carte sans texte probant reste utilisable
comme pivot de navigation, mais ne peut pas devenir l'unique preuve finale.
BUG-136 est corrigé déterministement, pas revalidé live.

### 4.4. A596–A600 — BUG-137, budget LLM cumulatif

L'échec C004 a aussi révélé que plusieurs limites locales ne constituaient pas
un budget global. BUG-137 a introduit un ledger unique pour tous les appels LLM :

- plafond : **12 000 tokens / 240 000 ms** ;
- réservation avant l'I/O ;
- réconciliation après l'appel ;
- réserve terminale : **4 800 tokens / 60 000 ms** ;
- traces communes, sans reprise de décision sémantique par le code.

Preuves :

- RED A597 : **2/18** ;
- GREEN A598 : **18/18** ;
- portefeuille A599 : **622/622** ;
- client complet : **1 722/1 722** ;
- audit A600 : **146/146**.

Une première cible A599 avait fait 621/622 parce que le runner atteignait
2 599 lignes. L'initialisation du ledger a été extraite dans un module de
207 lignes ; le runner est revenu exactement à 2 500 lignes. Le test de
modularité n'a pas été assoupli.

### 4.5. A601–A610 — C005 Microsoft et BUG-138

Le canari C005 a été sélectionné sur une preuve Microsoft 2024, puis audité et
préflighté : **96/96**, **129/129**, portefeuille **622/622**, audit **156/156**.
La source fraîche page 2 contenait `over $245 billion` et
`up 16 percent year-over-year`.

Le live WinUI unique A604 a échoué honnêtement :

- durée **112 629 ms** ;
- **7 156 tokens** ;
- cinq appels LLM ;
- une recherche et dix preuves matérialisées ;
- aucune réponse finale et aucune carte source ;
- zéro replay.

La chaîne causale observée a été : routeur `document` avec
`namedReferenceKind=none`, réparation complète répétant l'incohérence,
consommation pré-documentaire importante, puis transition terminale incorrecte
lorsque seule la réserve de fin restait.

BUG-138 a été préenregistré en A606 et reproduit en RED A607 : **4 FAIL / 1
PASS**, audit **76/76**. Deux premières tentatives GREEN à 4/5 ont été conservées
comme non promues. Le GREEN A608 a finalement obtenu :

- **5/5 PASS**, zéro échec, zéro skip ;
- audit **80/80** ;
- test byte-identique au RED ;
- transition terminale conservant la décision primaire valide de Qwen lorsque
  seule une revue optionnelle est refusée avant I/O par le budget ;
- micro-repair routeur borné à la paire incohérente
  `namedReferenceKind/document`, 96 tokens et une tentative.

A609 a ensuite scellé les régressions :

- portefeuille source-backed : **627/627 PASS** ;
- client complet x64 : **1 727/1 727 PASS** ;
- audit : **81/81 PASS** ;
- DLL produit et harnais byte-identiques.

A610 a envoyé le rapport de fermeture BUG-138 et a passé ses audits Telegram
avant/après : **41/41** et **37/37**. BUG-138 est corrigé déterministement mais
n'était toujours pas revalidé live à ce stade.

### 4.6. A611–A620 — C006 NIST et BUG-139

C006 a été préenregistré sur le document NIST CSF 2.0 et la fonction GOVERN.
La sélection A611 a passé **58/58**, l'audit A612 **71/71**, puis le préflight
A613 a passé :

- portefeuille source-backed **627/627** ;
- client complet **1 727/1 727** ;
- audit final **172/172** ;
- armement à blanc sans exécution utilisateur.

Le live WinUI C006 A614 a échoué en un seul run :

- réponse déclarant à tort les sources insuffisantes ;
- zéro preuve, zéro source, zéro carte ;
- micro-repair routeur exécuté une seule fois et correctement typé ;
- **4 670/12 000 tokens** ;
- **68 029/240 000 ms** ;
- performance au-dessus de la cible de 45 s ;
- 34 → 35 sessions, une question, un tour, aucun retry ;
- nettoyage complet après le run.

L'audit A615 a passé **220/220** et a authentifié la cause : le RouterPlan
contenait bien la référence canonique `NIST_CSF_2_0.pdf`, mais le transfert vers
la résolution reconstruisait ensuite une autre chaîne. Le `NotFound` était donc
causé par une référence altérée, pas par un jugement de pertinence de Qwen.

BUG-139 a été traité en test-first :

- A616 préenregistrement : **76/76** ;
- A617 RED causal : **6 FAIL / 5 PASS / 11**, audit **67/67** ;
- A618 GREEN : **11/11 PASS**, audit **71/71**, test byte-identique ;
- A619 portefeuille source-backed : **647/647 PASS** ;
- A619 client complet : **1 738/1 738 PASS** ;
- A619 audit : **88/88** ;
- A620 audits Telegram : **75/75** avant, **48/48** après.

La correction transporte la référence canonique décidée par Qwen jusqu'à la
résolution, au lieu de la reconstruire à partir d'une représentation
d'observation. Elle ne remplace pas la décision de Qwen. C006 n'a pas été
rejoué.

### 4.7. A621–A625 — C007 Adelphi, premier PASS fonction/provenance/UI

Un nouveau canari distinct C007 a été sélectionné sur un document Adelphi et
une question concernant quatre catégories de congé maladie payé. La preuve
canonique se trouvait page 11.

Préparation :

- sélection readonly A621 : **126/126** ;
- audit indépendant A622 : **125/125** ;
- portefeuille frais A623 : **647/647** ;
- client complet A623 : **1 738/1 738** ;
- audit préflight A623 : **188/188** ;
- armement à blanc, sans résidu C005/C006.

Le live unique, consolidé dans A625, a produit le verdict :

**`PASS_FUNCTIONAL_C007 / FAIL_PERFORMANCE_C007`**.

Résultats séparés :

- fonction : **PASS**, réponse exacte avec les quatre catégories et `[E1]` ;
- provenance : **PASS**, chunk oracle page 11, révision et SHA-256 canonique ;
- BUG-139 : **PASS live**, filename exact conservé après micro-repair ;
- budget/trace : **PASS**, 8 170/12 000 tokens et 137 055/240 000 ms ;
- WinUI : **PASS**, carte visible et cliquable, bon PDF ouvert page 11 ;
- unicité : **PASS**, une session, une soumission, une réponse, zéro retry ;
- nettoyage : **PASS**, WinUI/Qwen arrêtés et port 1234 libéré ;
- performance : **FAIL**, 137 055 ms, soit 92 055 ms au-dessus de 45 s.

L'audit final A625 a passé **243/243**. Ce run est la preuve la plus forte de la
fenêtre : la couture EvidenceBundle → writer → vérification → source card → PDF
fonctionne réellement. Il ne permet pas d'approuver le produit parce que la
latence reste environ trois fois supérieure à la cible.

### 4.8. A626 — attribution causale de la latence C007

A626 a apparié les six appels LLM à leurs traces serveur et a distingué les
temps d'inférence des autres coûts.

- Temps live total : **137 055 ms**.
- Évaluation des prompts : **73 309,27 ms**.
- Génération : **57 452,79 ms**.
- Temps LLM total : environ **130 762 ms**, soit **96,01 %** du live.
- Surcoût hors LLM résiduel : environ **6,3 s**.
- Écart d'appariement wrapper/serveur : **825,94 ms**.

Conclusion : ni le réseau, ni WinUI, ni le backend documentaire ne constituent
le principal goulot. Supprimer un seul petit appel ne suffit pas à atteindre
45 s. Réactiver les anciens raccourcis a été rejeté car l'ancienne baseline de
25 cas ne donnait que 12/25 stricts et comportait deux faux `answer` dangereux.

Le verdict A626 est donc :

**`DIAGNOSTIC_PASS_PERFORMANCE_FAIL_NO_SAFE_SHORTCUT_TO_45S`**.

L'expérience minimale retenue est un chemin proportionnel writer → reviewer :
le writer reçoit le pool complet, choisit ses EvidenceIds, un contrôle mécanique
valide la forme, puis un reviewer indépendant accepte strictement ou provoque un
repli vers V6. Aucune validation fonctionnelle n'est retirée silencieusement.

### 4.9. A627–A629 — nouveau chemin proportionnel

A627 a figé le protocole A627–A631 avant code :

- audit de préenregistrement **114/114 PASS** ;
- aucun changement produit pendant le préenregistrement ;
- flag désactivé par défaut ;
- incompatibilité explicite avec les chemins expérimentaux antérieurs ;
- cinq cas dangereux exécutés en premier pendant A630 ;
- arrêt au premier faux `answer` dangereux ;
- zéro dégradation stricte ou protocolaire autorisée ;
- gain médian post-retrieval exigé : au moins 25 % ;
- live WinUI interdit avant A631 ;
- C007 interdit au replay, C008 seulement après gates positifs.

A628 a implémenté le chemin derrière
`SAAIA_SOURCE_BACKED_AGENT_V2_PROPORTIONAL_WRITER_REVIEWER`, désactivé par
défaut. Les portes d'éligibilité exigent notamment une demande de contenu, une
référence documentaire nommée et résolue, un pool complet de 1 à 12 preuves du
même document, aucune troncature et un budget suffisant.

Le writer voit toutes les preuves et choisit les EvidenceIds ; le code contrôle
uniquement leur existence et leur cohérence ; le reviewer indépendant voit le
bundle complet. Toute sortie invalide, demande de révision, besoin de recherche,
liste non vide, mutation inattendue ou troncature déclenche un repli sûr vers V6
ou un blocage. Sept événements de trace ont été ajoutés.

Résultats A628 :

- suite ciblée : **37/37 PASS** ;
- audit GREEN : **37/37 PASS** ;
- aucun Qwen, WinUI ou live ;
- statut : PASS ciblé, A629 requis.

A629 a exécuté les régressions dans l'ordre préenregistré. Une première famille
provenance/UI a fait **56/57** : le seul échec était le contrat de modularité,
car le runner faisait 2 724 lignes. Le test n'a pas été changé. Le code a été
extrait dans des modules génériques et les mesures finales sont :

- runner : **2 491 lignes**, limite 2 500 ;
- automate proportionnel : **433 lignes**, limite 500 ;
- transitions : **244 lignes**, limite 500 ;
- support runner : **178 lignes**, limite 500 ;
- aucun fichier `SourceBackedRag/*.cs` au-dessus de sa limite.

Toutes les suites ont ensuite été rejouées sur le nouveau binaire :

| Famille | Résultat final |
|---|---:|
| Chemin proportionnel | **37/37 PASS** |
| Structured writer | **8/8 PASS** |
| Résolution/writer/reviewer | **55/55 PASS** |
| BUG-139/document nommé | **57/57 PASS** |
| Provenance/UI après extraction | **57/57 PASS** |
| Client complet | **1 775/1 775 PASS** |
| Backend complet | **2 054/2 054 PASS** |
| Audit final A629 | **69/69 PASS** |

Le TRX 56/57 initial reste conservé comme incident non promu. Les sept TRX
finaux ont zéro échec et zéro test ignoré. `git diff --check` reste à code 0,
avec seulement les avertissements historiques CRLF/LF. A629 autorise A630 mais
n'autorise toujours ni live WinUI ni approbation produit.

## 5. Communications Telegram de la fenêtre

Les reçus locaux suivants ont été créés pendant la fenêtre :

1. A575 — corrections après C002.
2. A580 — échec C003 et cause BUG-135.
3. A585 — fermeture déterministe BUG-135.
4. A590 — échec C004 et causes provenance/budget.
5. A595 — fermeture déterministe BUG-136.
6. A600 — fermeture déterministe BUG-137.
7. A605 — échec C005 et ouverture BUG-138.
8. Rapport 24 h initial du 30 août.
9. A610 — fermeture déterministe BUG-138.
10. A615 — échec C006 et ouverture BUG-139.
11. A620 — fermeture déterministe BUG-139.
12. Renvoi du rapport 24 h à 01:17.
13. A625 — C007 PASS fonction/provenance/UI, FAIL performance.
14. A626 — attribution causale de la latence.
15. A628 — GREEN ciblé du chemin proportionnel.
16. A629 — régressions déterministes PASS.

Le point important est que les reçus techniques A628/A629 ne remplaçaient pas
le présent rapport narratif consolidé. Le présent envoi corrige cette lacune.

## 6. Ce qui est prouvé, ce qui est testé et ce qui n'est pas approuvé

### Prouvé

- Les chaînes RED → GREEN des BUG-135 à BUG-139 sont authentifiées.
- Les suites client et backend passent dans leurs campagnes finales respectives.
- Le budget global arrête les dérives avant les anciens dépassements massifs.
- La référence documentaire canonique de Qwen peut maintenant traverser le
  pipeline sans être reconstruite incorrectement.
- C007 prouve en live une réponse exacte, une provenance canonique, une carte
  WinUI cliquable et l'ouverture du bon document au bon passage.
- La latence C007 est attribuée majoritairement aux six inférences LLM.
- Le nouveau chemin proportionnel est sûr au niveau déterministe derrière un
  flag désactivé par défaut.

### Testé mais non approuvé

- Le produit complet : les succès locaux ne suffisent pas à l'approuver.
- BUG-135, BUG-136, BUG-137 et BUG-138 sur de nouveaux canaris live distincts.
- Le chemin proportionnel avec Qwen réel : A630 reste à exécuter.
- L'objectif de performance sous 45 s : C007 prend 137,055 s.
- La généralisation à des questions simples et complexes variées du corpus.
- La stabilité de toutes les cartes source et de leur ouverture au-delà du
  canari C007.

### Explicitement refusé ou non promu

- les canaris C002–C007 ne seront pas rejoués pour fabriquer un meilleur score ;
- les TRX intermédiaires en échec restent conservés ;
- aucun seuil de modularité, provenance, budget ou qualité n'a été relâché ;
- aucun shortcut ancien présentant des faux `answer` dangereux n'a été réactivé ;
- aucun PASS déterministe n'est présenté comme une validation utilisateur live.

## 7. État technique à 17:04, avant création du présent rapport

- Branche : `SAAIA_V3.1`.
- HEAD : `5f35881cdc67d12a076fcd2a7a1004656ac9a37a`.
- État Git : **593 entrées**.
- Modifiés suivis : **71**.
- Supprimés suivis : **8**.
- Non suivis : **514**.
- Cet état correspond à la refactorisation historique volontaire ; aucune
  opération destructive n'a été utilisée.
- `git diff --check` : code **0**, seulement 13 avertissements historiques de
  conversion de fins de ligne.
- Aucun processus SAAIA, Qwen, `llama-server`, `testhost`, compilateur C#/VB ou
  helper de contrôle graphique ChatGPT n'est actif.
- Aucun listener sur le port 1234.
- Deux processus Ollama indépendants ont démarré à 14:48 ; ils ne proviennent
  pas de ce travail et n'ont pas été arrêtés.
- Aucun contrôle WinUI ou Computer Use n'est requis pour ce rapport.

## 8. Incidents et limites conservés dans la preuve

- C002 : boucle 16 tours, 42 269 tokens, 385 911 ms.
- C003 : aucune recherche documentaire à cause de la confusion sujet/document.
- C004 : bonne preuve retrouvée mais carte faible, hash non canonique refusé.
- C005 : recherche effectuée mais transition terminale sans réponse publiée.
- C006 : fausse référence reconstruite, faux NotFound, aucune carte.
- C007 : fonction/provenance/UI passent, performance échoue.
- A608 : deux résultats intermédiaires 4/5 non promus.
- A617 : une première exécution 7 FAIL / 4 PASS corrigée seulement dans le
  contrôle d'oracle, production inchangée ; le RED promu est 6 FAIL / 5 PASS.
- A625 : plusieurs premières passes de l'auditeur ont corrigé l'auditeur, pas le
  résultat live.
- A629 : 56/57 initial causé par la modularité ; extraction produit, puis 57/57.
- Plusieurs scripts d'audit ont nécessité des corrections PowerShell ou
  d'empreinte ; aucun oracle métier ou résultat produit n'a été réécrit pour
  transformer un échec en succès.

## 9. Plan d'action immédiat

### A630 — comparaison appariée Qwen, sans WinUI

1. Normaliser les 25 cas historiques A486 sous un schéma unique et figer les
   oracles attendus.
2. Exécuter les cinq cas dangereux A483 en premier.
3. Comparer sur le même binaire, modèle, paramètres, preuves et ordre :
   baseline V6 versus chemin proportionnel.
4. Alterner l'ordre A/B pour réduire l'effet de chauffe.
5. Arrêter immédiatement au premier faux `answer` dangereux.
6. Exiger zéro dégradation stricte, protocolaire ou de provenance.
7. Mesurer les appels et la latence post-retrieval ; gain médian requis ≥ 25 %.
8. Conserver toutes les sorties, y compris les échecs, sans WinUI et sans C007.

### A631 — décision de gate

- Rejeter et laisser le flag désactivé si un seul critère de sûreté échoue.
- N'autoriser un nouveau canari C008 qu'en cas de gates A630 toutes positives.
- Utiliser C008 une seule fois, avec vérité source fraîche et critères séparés
  fonction/provenance/UI/performance.

## 10. Conclusion

Le travail de cette fenêtre n'a pas consisté à empiler des correctifs locaux.
Chaque échec live a été converti en un contrat générique, préenregistré, testé
en RED, corrigé minimalement, rejoué en régression puis documenté. Cette méthode
a fini par produire C007, premier succès récent réunissant réponse exacte,
preuve canonique et source WinUI cliquable.

Le prochain obstacle n'est plus caché : **96,01 % du temps C007 provient des
appels LLM**. Le chemin proportionnel A628 cherche à réduire ce coût tout en
gardant Qwen comme décideur et un reviewer indépendant comme barrière de sûreté.
A629 prouve que ce chemin ne casse pas les suites déterministes ; seul A630 peut
maintenant dire s'il conserve réellement la qualité sémantique et apporte le
gain de temps requis.

Verdict final de la fenêtre : **progrès technique majeur et preuves solides,
mais produit toujours `TESTE_NON_APPROUVE`, performance non conforme, A630 non
exécuté et Goal toujours actif.**
