# Audit complet des entrées modèle — A899

Date : 24 octobre 2026. Base : `3c24c380`, branche `SAAIA_V3.1`.

## Conclusion et portée réelle

La seconde analyse a trouvé des défauts que le premier passage n'avait pas
éliminés. Ils concernaient autant les données envoyées que les consignes,
les permissions d'outils, la validation des preuves et certains états de reprise.
Ils sont corrigés et accompagnés de reproductions déterministes.

L'examen couvre le parcours avancé depuis la demande transférée jusqu'aux appels
HTTP, puis la liaison des réponses à leurs preuves : Planner, Navigator, Judge,
Writer, Critic, projection des extraits, mémoire opérationnelle, lectures
complémentaires, contrôle des identités et résolution canonique. Les deux styles
de consignes (`contract` et `agent`) et les deux protocoles sont vérifiés.

Cette étape n'utilise aucun modèle réel : aucune requête OpenAI ou RunPod,
aucune nouvelle dépense et aucune transmission externe du corpus. Le profil
Terra des tests emploie un faux gestionnaire HTTP et une clé factice.
Les services serveur et les secrets de production ne sont pas modifiés.
L'implémentation est prête pour une validation réelle ; elle ne constitue pas
une preuve de réussite sémantique du planning. **TESTE_NON_APPROUVE** reste le
statut du produit.

## Les défauts supplémentaires et leurs corrections

### 1. Consignes contradictoires entre styles et entre rôles

Le style `contract`, utilisé par défaut, conservait des interdictions culinaires
rigides et des obligations de recopier le titre provisoire. Le Judge pouvait
pourtant établir une identité plus complète à partir du même corps. Le modèle
recevait donc des instructions incompatibles selon son rôle ou sa configuration.

Les deux styles demandent maintenant la même chose : un titre observé est un
repère documentaire ; le LLM identifie les objets réels et juge leur adaptation
à la demande. La source doit prouver l'objet et ses contraintes factuelles,
sans devoir prescrire un classement ou un planning demandé à SAAIA.
Le Critic conserve les unités étayées lorsqu'il corrige un défaut local.
Les indices `retrievedFor` et `targetColumns` restent des orientations de
recherche, sans valeur de preuve de pertinence.

Une règle commune distingue les instructions de l'utilisateur des données
contenues dans les documents, métadonnées, résultats d'outils et anciens
candidats. Les éventuelles instructions présentes dans ces données ne changent
ni les droits d'outils ni la tâche réelle.

### 2. Des corps sans titre n'arrivaient jamais à la qualification

L'observation automatique exigeait un titre source explicite. Dans le parcours
staged, un passage substantif sans ce titre pouvait rester devant le Navigator,
qui n'a pas le rôle de qualifier les objets. Le Judge ne le recevait jamais.

Ces passages sont maintenant soumis au Judge comme observations provisoires,
étiquetées par un préfixe littéral visible. Ce préfixe ne devient pas une identité
canonique, ne prouve aucune pertinence et ne compte jamais comme objet validé.
Le modèle peut identifier, renommer, subdiviser ou écarter l'observation.

Les préfixes longs ont aussi révélé un cas particulier : un Judge ne pouvait
pas toujours recopier un préfixe de plus de 24 mots pour le rejeter, à cause du
contrôle strict d'identité. Un label déjà fourni peut désormais être conservé
pour `needs_body` ou `rejected` si son texte est un préfixe littéral visible du
même seed et de la même source. Cette exception ne s'applique jamais à une
qualification `body_verified`. La coupe des labels évite de terminer au milieu
d'un mot quand une frontière existe.

### 3. Des métadonnées pouvaient évincer tout le texte utile

Une reproduction utilisant 50 000 caractères de métadonnées faisait disparaître
la phrase documentaire utile du prompt. Les titres et chemins de section sont
maintenant bornés à 240 et 500 caractères ; `metadataTruncated` signale la coupe.
Les identifiants techniques des chunks voisins ne sont pas envoyés au modèle.
Le corps utile reste prioritaire dans l'enveloppe de contexte.

### 4. Une lecture complémentaire pouvait perdre ce qui avait déjà été lu

Une lecture de fin de chunk remplaçait auparavant le début de l'extrait. Le
modèle pouvait alors perdre le titre ou des conditions déjà vues.

La projection conserve maintenant séparément :

- la fenêtre courante demandée, avec son contenu exact et son offset ;
- le préfixe canonique, jusqu'à 2 400 caractères lorsqu'il n'est pas couvert ;
- au plus trois fenêtres antérieures distinctes, avec leurs offsets.

Le budget global inclut ces fenêtres et leurs métadonnées. Si nécessaire, les
fenêtres historiques les plus anciennes sont retirées, puis le préfixe est
raccourci avant de réduire la fenêtre courante. Les omissions sont explicites.
Les lectures précédentes reprennent aussi après interruption via le checkpoint.

Les fenêtres disjointes ne sont jamais concaténées pour inventer une identité.
Chaque contrôle lexical s'effectue dans une fenêtre individuelle : la fin
« alpha » d'un passage et le début « beta » d'un autre ne prouvent pas le nom
« alpha beta ». Les consignes expliquent également que les intervalles absents
ne sont pas du texte lu.

L'arrivée à la fin d'un chunk ne ferme plus la navigation si un intervalle
intermédiaire reste invisible. Cet intervalle peut être lu même lorsque le
budget des recherches documentaires est épuisé, dans le budget modèle restant.
Demander une autre longueur qui donne exactement les mêmes derniers caractères
ne compte plus comme un progrès.

### 5. Certains contrôles utilisaient du texte caché au modèle

La vérification d'un corps documentaire pouvait analyser le chunk canonique
complet, même si la requête du modèle ne contenait qu'un titre. Elle évalue
désormais les fenêtres réellement présentes dans le prompt. Une ancre de source
entièrement vide reste un moyen de navigation, jamais une preuve à citer.

La liaison d'un nom sélectionné à ses preuves s'applique aussi aux sélections
uniques, indépendamment d'une exigence de diversité. Un nom non vide doit être
présent dans les preuves visibles de sa sélection. Cela ne rend pas le nom
obligatoire pour les anciens contrats qui l'autorisent vide et n'interdit pas
les répétitions demandées.

Le plafond de 24 mots réservé à la recherche approximative ne refuse plus un
long titre littéral présent sur une ligne complète de la source, dans la limite
de 240 caractères. Un nom modifié reste refusé ; aucune recherche approximative
plus coûteuse n'a été ajoutée pour contourner ce plafond.

### 6. Permissions d'outils et correction des erreurs d'arguments

Une fonction connue pouvait être exécutée alors qu'elle n'était pas proposée à
ce tour. La liste effectivement offerte est maintenant contrôlée.

Les arguments invalides d'une fonction autorisée provoquaient aussi un abandon
avant que le modèle puisse les corriger. Un appel dont l'enveloppe et les
identifiants sont valides reçoit maintenant un rejet lié à son vrai identifiant,
puis une possibilité bornée de correction. Aucun préfixe d'un lot invalide
n'est exécuté. Les outils inconnus, non offerts et les enveloppes malformées
restent refusés. Les deux chemins, général et staged, sont vérifiés.

Le catalogue textuel décrit `read_evidence` uniquement lorsqu'un parcours natif
peut réellement le proposer. Sa disponibilité est distinguée de celle des
nouvelles recherches documentaires.

### 7. Le protocole configuré n'était pas utilisé par tous les rôles

En configuration Responses, les rôles sans outils, notamment Planner et Judge,
restaient sur Chat Completions. Une vraie enveloppe Responses au Planner était
donc rejetée. Tous les rôles utilisent maintenant le protocole configuré ;
leur droit d'appeler des outils reste contrôlé séparément.

Avec Responses, `store:false`, les éléments de raisonnement chiffrés et le
traitement des enveloppes sont cohérents sur tout le parcours. Les sorties
tronquées, filtrées, refusées ou non terminées ne deviennent pas des réponses
acceptées simplement parce qu'une partie de leur JSON est parseable.
Les champs d'arguments non textuels ne sont plus convertis silencieusement
en chaîne vide avant rejeu.

Les tests réutilisent certaines réponses métier indépendantes du protocole via
un adaptateur exclusivement présent dans le faux serveur HTTP et réservé aux
fixtures explicitement marquées. Une enveloppe Chat réellement reçue par un
Planner Responses reste refusée. Aucun fallback supplémentaire n'a été ajouté
en production pour contourner les tests.

### 8. Une diversité non demandée pouvait être imposée

Le solveur de candidats distincts pouvait être lancé pour une grille autorisant
explicitement des répétitions. Le parseur refusait aussi une répétition nommée
pourtant permise par ce contrat.

Ce parcours distinct est désormais réservé aux demandes qui l'exigent. Les
tests réutilisent réellement un même objet documenté sur vingt coordonnées
lorsque le contrat l'autorise. L'unicité des identifiants de claims, les
coordonnées, les marqueurs et la provenance restent contrôlés.
Pour le planning exigeant vingt propositions distinctes, cette exigence demeure.

### 9. Révision, disponibilité et pages des preuves

- Une recherche ciblée par `sourceKey` conserve la révision observée. Un handle
  devenu ancien ne peut pas chercher silencieusement une nouvelle révision
  derrière la même identité. Les résultats sont vérifiés avant admission.
- Une lecture de pages vide vérifie aussi l'identité de la source, dans une
  transaction cohérente, au lieu de masquer une modification de document.
- La revalidation ne réintroduit pas les documents indisponibles ou désindexés.
  La lecture explicite d'une ancienne révision légitime demeure possible.
- Une référence de page sans chunk conserve les vraies bornes des chunks
  récupérés. Un chunk couvrant les pages 1 à 3 ne devient pas artificiellement
  une preuve limitée à la page 2. Les validations de chunk, carte et ancre
  gardent leurs exigences exactes.

Un cas de structure textuelle a également été corrigé : une phrase se terminant
par un nombre suivi d'un point n'est plus ignorée comme prose lorsqu'elle
accompagne un repère de page. Le mélange repère/contenu reste explicitement
marqué ; le modèle conserve le jugement de pertinence.

### 10. Un état contradictoire envoyé au Writer et au Critic

L'inspection des requêtes complètes a montré un dossier annonçant deux candidats
qualifiés, mais un inventaire vide et un compteur zéro. Le filtre éliminait les
candidats sans rôles de colonnes, pourtant normaux dans une liste plate.
Le filtrage et la priorité des preuves conservent désormais ces candidats.
Un test vérifie l'accord des deux compteurs et les deux entrées effectivement
envoyées au Writer et au Critic.

## Ce qui est réellement envoyé : preuve reproductible

Les quatorze requêtes JSON synthétiques sont conservées dans
`artifacts/reprise-pc-20260908/a899-complete-payload-audit-20261024/synthetic-wire-payloads/`.
Il s'agit de sept appels pour chacun des deux protocoles :

1. Planner ;
2. Judge initial ;
3. Navigator produisant des arguments invalides ;
4. Navigator recevant le rejet lié, puis corrigeant son appel ;
5. Judge recevant la lecture complémentaire ;
6. Writer ;
7. Critic.

La demande synthétique porte sur deux procédures techniques documentées.
La requête initiale, la langue et les identités de preuve sont conservées ;
une contrainte du début reste visible avec la fin du texte. L'inventaire et
le dossier sont cohérents. Le profil utilise `gpt-5.6-terra`, effort `low`,
avec transport HTTP totalement simulé et clé factice. Aucune instruction oracle
ni extrait du corpus privé n'est intégré à ces artefacts publics de test.

Cette preuve vérifie le logiciel qui construit les messages et gère les
résultats simulés ; elle n'est pas une réponse réelle de Terra.

## Validation

| Vérification exécutée | Résultat |
| --- | --- |
| Solution complète `Release --no-restore` sur l'état final | 4 968 réussites, zéro échec, quatre ignorés : Contracts 10 ; Backend 2 620 + 3 ignorés ; Client 2 338 + 1 ignoré. Compilation WinUI incluse. |
| Tests du parcours fournisseur inclus dans cette suite | 434 cas, dont transport, données projetées, qualification, répétitions, reprises et scénarios de correction. Aucun appel LLM réel. |
| Requêtes HTTP synthétiques inspectées | 14 payloads complets, sept par protocole, avec profil Terra et demande technique cohérente. |
| PostgreSQL 16.14 réel et isolé | 68 réussites, aucun échec, confirmé sur les binaires finaux dans `real-postgres-sealed/`. |
| Espacement | `git diff --check` sans erreur. |

Les tests PostgreSQL recouvrent une partie du total de la suite ; ils ne sont
pas additionnés comme scénarios uniques supplémentaires. La suite ordinaire
peut retourner sans exécuter le SQL lorsque la connexion dédiée est absente :
la preuve d'intégration vient du cluster isolé. Les quatre tests explicitement
ignorés restent les deux sondes verticales live et deux scénarios documentaires
runtime. Les tests ne prétendent pas avoir validé ces parcours réels.

Les résultats TRX, les logs, les empreintes des binaires et l'arrêt des ressources
sont conservés dans
`artifacts/reprise-pc-20260908/a899-complete-payload-audit-20261024/`.
Le script de reproduction vérifie le répertoire du cluster et ses binaires,
utilise uniquement le port local 55439 puis arrête le cluster qu'il a créé.
L'arrêt final confirme zéro écoute sur ce port, la suppression du fichier de
mot de passe et des empreintes de binaires inchangées pendant l'exécution.

## Limites restantes et prochaine étape

Les messages sont maintenant cohérents sur les cas reproduits et leurs bornes
sont explicites. Les budgets d'appels, l'effort de raisonnement et le modèle
de référence n'ont pas été augmentés pour compenser les défauts.
La relecture des traces A895/A896 ne montre pas de saturation des budgets de
sortie du Planner, du Judge, du Navigator, du Writer ou du Critic.

La qualité sémantique réelle dépend encore du corpus, de son extraction/OCR,
des décisions de recherche, de l'adaptation aux rôles et des choix du modèle.
Les indicateurs de structure documentaire restent des signaux mécaniques.
Le contexte et l'historique restent bornés ; une omission est déclarée, pas
assimilée à une absence documentaire. Une source modifiée pendant le job peut
provoquer un refus de provenance plutôt qu'une poursuite sur une autre révision.

Le Critic produit encore une réponse complète avec des consignes de correction
ciblée ; ce n'est pas une API de patch garantissant l'immuabilité de chaque unité.
La réussite sémantique, la latence, le coût réel et l'ouverture des sources dans
WinUI nécessitent toujours les essais réels. Des tests simulant les décisions
du LLM ne peuvent établir ces propriétés.

La prochaine étape reste un essai Terra instrumenté sur un état figé, avec
inspection du contenu réellement reçu par chaque rôle et de la première rupture
éventuelle. Puis seulement : régressions générales, contrôles hors cuisine,
validation WinUI et répétitions d'acceptation. Aucun essai Terra n'est déclenché
automatiquement par cette étape.

Références officielles consultées pour le transport et le raisonnement :
[Function calling](https://developers.openai.com/api/docs/guides/function-calling),
[Reasoning models](https://developers.openai.com/api/docs/guides/reasoning),
[GPT-5.6 Terra](https://developers.openai.com/api/docs/models/gpt-5.6-terra).
