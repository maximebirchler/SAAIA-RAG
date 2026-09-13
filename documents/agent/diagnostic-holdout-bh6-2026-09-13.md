# Diagnostic BH6 : portée réelle du résultat 1/24

Date : 13 septembre 2026. Candidat historique : `2296a4a85fae0df4186e0667d071451e96f4549b`.

Statut produit : **TESTE_NON_APPROUVE**. Le verdict BH6 enregistré reste **REJETE**, avec un cas entièrement accepté sur 24. La banque est consommée. Ce document explique les résultats après divulgation ; il ne remplace pas le verdict et ne constitue pas une nouvelle acceptation.

## Ce que mesurait le test

Une banque associe à chaque question un corrigé attendu : informations, preuves documentaires, langue et comportement. Les banques connues servent à corriger le produit. Une banque cachée jusqu'au verdict vérifie ensuite des formulations nouvelles sur un candidat figé.

Le score BH6 exigeait la réussite simultanée du comportement, de la sémantique, des preuves, du nombre d'éléments demandé et de la langue. **1/24 ne signifie donc pas que Terra a donné 23 réponses factuellement fausses.** Il décrit la conformité complète selon un corrigé automatique dont plusieurs défauts sont désormais démontrés.

La construction et l'évaluation ont utilisé des appels séparés à Terra sans historique de développement. Cette séparation limite la contamination ; elle ne rend pas les corrigés automatiques infaillibles. Ils doivent être contrôlés contre les faits et le périmètre réellement accessible au produit.

## Exemples vérifiés après divulgation

| Cas | Observation | Conclusion du diagnostic |
|---|---|---|
| BH6-001 | Deux usages vapeur corrects, sourcés, en français ; réponse produite par Terra au lieu du local attendu | Qualité de réponse acceptée par l'évaluateur ; attente de routage à vérifier contre la frontière locale qualifiée |
| BH6-007 | Demande pertinente de préciser le document ; job serveur réussi avec `clarification_required` | Le harnais a ignoré le résultat final et compté une réponse avancée : erreur de classement confirmée |
| BH6-011 et BH6-014 | Insuffisances sans fait inventé, mais texte anglais pour des demandes française et italienne | Défaut produit de langue ; la trace historique ne suffisait pas à établir la langue d'intake. Une reproduction locale révèle ensuite une langue d'intake allemande pour la demande française |
| BH6-013 | Valeur `0.2` soutenue par le manuel PostgreSQL 18, page 773 ; réponse portugaise au lieu d'italienne | Le corrigé « information absente » est invalide : il ne regardait qu'un échantillon d'extraits. La mauvaise langue reste un défaut produit |
| BH6-015 | Tableau de cinq étapes OAuth ; l'étape E traite l'accès à une ressource au lieu de la validation du code et de l'émission du jeton attendues | Erreur de contenu et de relation aux sources ; le tableau bien formé ne suffit pas |
| BH6-017 | Tableau AR5/AR6, mais les deux sources rendues sont AR6 WG2 | Provenance de la comparaison AR5 non démontrée par les sources affichées |
| BH6-020 | La question demande de comparer deux manuels ; le système demande de choisir un seul des deux | Clarification qui réduit injustement le périmètre demandé |
| BH6-022 | Décision positive sur la nécessité de valider une fonction de sécurité « dès maintenant », sans données de cette fonction | Application au cas utilisateur non soutenue ; une règle générale ne démontre pas l'état concret du système |
| BH6-024 | L'utilisateur donne explicitement l'édition 2023 ; le système demande de choisir entre 2015 et 2023 | Clarification inutile sur une information déjà fournie |
| BH6-019 | Comparaison documentaire de deux restrictions de sentiers, correctement distinguées et sourcées | Seul cas entièrement accepté par le verdict original |

BH6-002 cite une norme sans édition dans une famille à plusieurs versions : la clarification peut être justifiée. BH6-009 demande seulement la norme au premier tour, alors que le corrigé exige tous les renseignements manquants immédiatement : la pertinence d'une clarification progressive doit être examinée. Ces points ne sont pas tranchés en faveur du produit.

## Mesures séparées, sans nouvelle note d'acceptation

L'inspecteur `tools/inspect-consumed-holdout.py` lit uniquement les résultats déjà divulgués et produit des compteurs et identifiants opaques. Il ne contacte aucun service, ne réévalue pas la sémantique et ne modifie pas le verdict scellé.

- Les 24 sorties sont : 7 réponses documentaires, 8 clarifications et 9 insuffisances.
- Le type final correspond au corrigé déclaré pour 12/24 cas. Ce chiffre ne garantit ni le contenu ni l'équité de ce corrigé.
- Pour les 14 cas dont le corrigé attend une réponse locale ou avancée, le chemin d'exécution correspond pour 6 cas. Cela ne garantit pas qu'une réponse complète a été donnée.
- L'évaluateur original note les affirmations vérifiables soutenues pour 16/24 cas et la langue conforme pour 21/24. Un refus sans fait documentaire peut réussir le premier critère sans répondre à la question.
- Les 37 sources passent le contrôle physique/canonique. Cela ne garantit pas qu'elles prouvent les affirmations auxquelles elles sont associées.

Ces dimensions se chevauchent ; il ne faut ni additionner leurs réussites ni produire un score de réponses correctes en supprimant arbitrairement les critères échoués.

## Corrections engagées à la reprise

1. Messages fixes des terminaux dans les six langues, avec normalisation des variantes régionales par le normaliseur commun. Les messages de clarification fournis par le LLM sont conservés.
2. Indices linguistiques italiens généraux pour éviter l'interprétation portugaise des questions de valeur ; régression bilingue dédiée.
3. Trace de la langue d'intake au terminal, pour comprendre un éventuel désaccord futur avec la langue utilisateur.
4. Inspecteur hors ligne séparant chemin d'exécution et résultat final, avec tests sur les clarifications et insuffisances de jobs serveur réussis.

Ces corrections ne prouvent pas la résolution des erreurs de recherche ou de synthèse avancée. Une vérification locale réelle puis les campagnes pertinentes seront nécessaires avant un nouveau gel.

Validation mécanique de cette correction : 53 tests ciblés réussis, six tests
Python de diagnostic réussis ; suite client Release complète : 2 282 réussites,
zéro échec et une sonde live ignorée. Ces tests ne font pas d'appel OpenAI et ne
constituent pas une approbation sémantique du produit.

### Reproduction locale après le premier correctif

Six cas consommés sont rejoués à titre diagnostique sur ce PC au commit
`e8b691739b1c3ac43a8888305fc5490178500678`, fournisseur Local, politique
ProductionLocal et serveur avancé désactivé. Quatre cas atteignent la limite
locale et émettent un transfert proposé ; un cas donne une insuffisance ; un
autre conserve une clarification inutile d'édition. Le journal fournisseur est
inchangé, le modèle est arrêté et le port 1234 libéré. Ce rejeu ne mesure pas la
qualité des réponses Terra et ne réouvre pas la validation aveugle.

La demande PostgreSQL de BH6-013 est maintenant détectée en italien ; sa sortie
locale est un transfert en italien, pas une réponse locale sur le paramètre.
BH6-001 atteint réellement le budget local avant le transfert : l'attente
rigide « réponse locale » ne suffit pas à juger ce transfert erroné dans une
architecture qui prévoit précisément le recours au serveur.

BH6-011 expose `language=de` au terminal et une insuffisance allemande malgré
une demande française. Le mot « quelle » est partagé entre la question française
et le nom allemand « Quelle » ; le détecteur pouvait retourner une égalité et
laisser la langue du routeur prendre le dessus. Des indices grammaticaux
français généraux et cinq régressions français/allemand sont ajoutés. Les
messages traduits seuls n'auraient pas corrigé cette erreur en amont. Les 58
tests ciblés de cette deuxième correction passent.

Vérification réelle au commit `32d0dd6521095bd72520627892019feb5915edd0` :
BH6-011 est rejoué seul, avec le modèle local sur ce PC et sans serveur avancé.
L'insuffisance est maintenant rendue en français, avec `language=fr` dans la
trace et langue française détectée dans la réponse, en 16,031 s. Le journal
OpenAI reste inchangé, l'environnement est restauré et le port 1234 libéré.
La suite client Release finale compte 2 287 réussites, zéro échec et une sonde
live ignorée. Cette preuve porte sur la correction de langue de ce cas et ne
valide pas la qualité générale des recherches ni des synthèses avancées.

## Méthode exigée pour la prochaine banque

- Décrire la frontière locale de manière opérationnelle, issue des mesures qualifiées, avant d'imposer un chemin d'exécution. Éviter une définition vague telle que « relativement direct ».
- Distinguer le chemin local/avancé du résultat réponse/clarification/insuffisance. Une réussite de job serveur n'est pas nécessairement une réponse documentaire.
- Juger la sémantique indépendamment du chemin d'exécution ; conserver ensuite la conformité complète comme critère distinct.
- Accepter toute preuve canonique du corpus autorisé qui soutient effectivement la réponse. L'échantillon utilisé pour rédiger le corrigé n'est pas une liste exclusive de sources accessibles au produit.
- Ne déclarer une information absente qu'avec une preuve suffisante sur le périmètre documentaire pertinent. « Absente des extraits sélectionnés » ne démontre pas « absente du corpus ». Exclure les cas d'absence non établis avant gel, sans utiliser la réponse du candidat pour modifier le corrigé.
- Vérifier que la question contient les identités/éditions nécessaires. Une métadonnée `corpusTarget` cachée à l'utilisateur n'est pas une précision transmise au produit.
- Autoriser des sources pour une réponse partielle explicitement soutenue ; conserver l'interdiction pour une clarification ou une insuffisance pure. La couverture incomplète reste mesurée séparément.
- Contrôler les corrigés avant exécution contre les documents, notamment les refus et les contraintes de cardinalité. Une clarification progressive pertinente ne doit pas être rejetée uniquement parce qu'elle ne pose pas toutes les questions en un tour.
- Conserver des seuils figés avant exécution, un candidat propre, un budget borné et une nouvelle banque indépendante. BH6 sert désormais au développement et au diagnostic, jamais à une nouvelle validation aveugle.

La prochaine étape produit porte sur l'identité documentaire, les clarifications abusives et les relations affirmation/preuve. Le corpus et l'architecture restent à améliorer malgré les succès antérieurs sur le planning de repas.
