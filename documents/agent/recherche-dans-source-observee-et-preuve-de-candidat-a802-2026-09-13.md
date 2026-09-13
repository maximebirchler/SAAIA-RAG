# Recherche dans une source observée et preuve du candidat — 13 septembre 2026

Statut produit : **TESTE_NON_APPROUVE**.

Le contrôle de continuité du planning 5 × 4 sur le commit
`8437c345766494306a299eac4e3f856e5153f5b2` a exécuté trois jobs réussis au sens
du protocole : vingt claims, une grille complète et des sources dans chaque
réponse. Les durées sont 95,144 s, 94,505 s et 81,595 s ; seize appels Terra
totalisent 0,9888875 USD. Les 49 cartes ont passé les contrôles canoniques,
révisions, hashes physiques et bornes des pages PDF. Cela ne valide pas la
pertinence sémantique de chaque choix ni l'ouverture graphique WinUI.

La validation de trois réponses correctes consécutives est refusée. Les deux
premières réponses présentent des identités issues d'index comme des options
utilisables sans leur contenu propre. La première cite aussi un smoothie seul
comme petit-déjeuner alors que son passage réel indique de le servir comme
accompagnement. La troisième n'est pas approuvée tant que le choix d'un beurre
de fruits secs seul comme collation reste non résolu ; cette préparation existe
réellement et il ne s'agit pas d'un défaut de hash ou d'une recette fabriquée.
Le résultat est donc deux rejets et un cas non résolu, sans trois acceptations.

Une recherche de diagnostic indépendante, sans appel LLM payant, a retrouvé les
corps réels du gruau, des fajitas, de la crêpe et du smoothie dans le même PDF,
aux pages physiques 35, 36 et 38. Les renvois imprimés de l'index ne sont pas
ces numéros physiques. Une tentative de relire les jobs terminés sur le backend
de référence a reçu HTTP404 ; aucun payload complet de claims n'a été obtenu
par ce chemin. Les verdicts conservés reposent sur les réponses closes et leurs
textes canoniques audités, sans prétendre avoir obtenu ce payload.

Le contrôleur de recherche recevait des sources opaques, mais son outil ne
permettait pas de réutiliser leur identité pour cibler un document. Il peut
désormais transmettre un `sourceKey` exactement présent dans ses observations.
Le backend résout ce handle vers le `DocId` et le `DocPath` canoniques de
l'observation courante ; ces identités sont exclues du JSON envoyé au modèle.
Un handle inconnu, une casse différente, un type JSON incorrect ou une demande
combinant handle et `documentHint` provoque un refus de contrat avant recherche.
L'appel initial, qui n'a pas d'observations, ne peut pas inventer de handle.
Le même modèle et le même fournisseur restent actifs pendant le job.

Le rôle structurel du texte et sa raison de navigation sont désormais transmis
à la recherche et aux rédacteurs. Ces informations sont calculées sur le texte
canonique complet avant clipping ; elles décrivent sa structure, sans approuver
sa pertinence métier. Une politique commune distingue un index qui prouve une
identité du contenu nécessaire pour sélectionner une option utilisable dans un
livrable. Elle s'applique à la recherche, à la rédaction, à la critique et aux
réparations. Elle demande aussi de distinguer une option complète d'un composant,
d'un accompagnement ou d'un gabarit, sans inventer de combinaison pour le faire
convenir. Une liste d'identités explicitement demandée peut toujours s'appuyer
sur un index ; la nouvelle règle n'exige pas une prescription documentaire des
placements que SAAIA doit proposer.

Les quatre tests initiaux reproduisaient le défaut avant correction. Huit
contrôles passent ensuite pour la recherche ciblée, l'absence de fuite
d'identité privée et le refus des scopes inconnus, ambigus ou mal typés. La
suite backend finale compte 2 232 réussites, zéro échec et trois tests live
ignorés. Les réponses locales du client ne sont pas modifiées par ce lot.

La prochaine preuve est un nouveau job de planning sur le code figé, avant
décision de financer les répétitions suivantes ou la nouvelle banque aveugle.
Aucune future question aveugle n'a été générée pour préparer cette correction.
Les anciens résultats restent immuables ; aucun achat, déploiement serveur,
installateur ou appel RunPod ne fait partie de ce lot.

## Complément après la preuve réelle

Le job A802 a terminé avec une réponse partielle : dix-neuf choix et une case
explicitement non documentée. Il a consommé cinq appels, 0,362257 USD et environ
122 secondes. Les dix-huit cartes sources passent les contrôles physiques et
canoniques ; ce résultat ne valide pas un planning complet. La correction A803
traite la possibilité de retourner chercher une preuve pendant la rédaction
ou la critique, absente du protocole de ce job.

Les trois anciens résultats complets ont aussi été récupérés en lecture seule
pendant la fenêtre de backend temporaire. Les premiers 404 provenaient du
mauvais identifiant d'utilisateur : le client de validation emploie un UUID
aléatoire, distinct du libellé de création de session. L'identifiant exact
retrouvé dans chaque événement de mise en file permet la lecture autorisée.
Ces captures conservent les liens Claim → Evidence de chaque job ; les anciens
verdicts ne sont pas réécrits. Elles confirment notamment les index cités pour
plusieurs choix et le caractère distinct du cas de collation à examiner.
