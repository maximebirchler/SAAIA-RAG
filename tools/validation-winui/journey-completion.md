# Compléments natifs WinUI après la campagne scellée

Ces variantes s'exécutent dans une **nouvelle racine**, par exemple
`artifacts/winui-journey-completion-20261025/local-fixture`.
Les campagnes antérieures ne sont ni relancées ni modifiées. Les seuls modèles
utilisés sont les fournisseurs déterministes loopback ; aucune clé payante n'est
nécessaire. Les actions WinUI restent réservées à Computer Use.

## Préparer les données et les profils

Réutiliser `start-environment.ps1`, `configure-client.ps1`, `seed_documents.py`,
l'application du SQL synthétique et `refresh-catalog.ps1` décrits dans README.
Choisir des ports libres distincts de la campagne de suppression encore active.
Les profils nommés sont préparés uniquement sous la nouvelle racine
`runtime/client-profiles/`, puis lancés avec `start-client.ps1 -ProfileName ...`.
Ne pas recopier les clés, paramètres ou clusters d'une campagne scellée.

Les six séquences complémentaires sont :

1. Configuration initiale : valeurs modifiées puis Annuler, réouverture,
   mauvaise puis bonne clé dans le même assistant, application et redémarrage.
2. Alternative et préférences : primaire indisponible, alternative utilisable,
   anglais/clair appliqués, redémarrage, ordre des URL inchangé et valeurs reprises.
3. Deux discussions : renommage de l'inactive avec titre vide puis saisie de
   121 caractères ; observer le titre par défaut et la limite120 sans changer
   de discussion active.
4. Réponse de contrôle longue en deltas : remonter, vérifier le maintien du
   scroll volontaire, Aller en bas, annuler après deltas visibles, recharger
   l'interruption puis terminer un nouveau tour court.
5. GET messages retardé : vérifier les contrôles bloqués puis libérés ; vérifier
   aussi la navigation clavier derrière un dialogue sans déclencher de suppression.
6. Lien ambigu puis carte exacte ; résultat avancé terminal avec carte source,
   fermeture et redémarrage, même message/job/version/source sans nouvel appel.

Pour la séquence4, conserver le mode normal `success`, choisir une `response`
longue synthétique et `chunkDelaySeconds` entre0.1 et0.3. Le routage par outil
reste disponible comme dans la fixture initiale. Le contenu long peut contenir
des retours à la ligne. Ces délais et tokens ne mesurent aucune performance LLM.

## Retarder exactement le chargement d'une discussion

Démarrer une **nouvelle** instance `start-storage-proxy.ps1 -InstanceName ...`
dans la nouvelle racine, puis choisir son URL dans le profil client. Le contrôle
initial désarme toutes les injections. Après identification de la discussion :

```json
{
  "failUserMessagesRemaining": 0,
  "delayUserMessagesSeconds": 0,
  "failGetMessagesRemaining": 0,
  "failReadyRemaining": 0,
  "delayGetMessagesSessionId": "UUID-de-la-discussion-synthetique",
  "delayGetMessagesRemaining": 1,
  "delayGetMessagesSeconds": 12
}
```

Seul un GET de `/chat/sessions/<UUID>/messages` ou de
`/chat/messages?sessionId=<UUID>` correspondant consomme le compteur sous verrou.
L'injection refuse les identifiants manquants/invalides ou les paramètres CDC
dupliqués. Le délai est borné à30s et le compteur à10000. Les POST, autres
discussions et inventaires ne sont pas retardés. Une panne GET500 armée garde
priorité ; elle ne consomme pas le compteur de délai. L'événement
`synthetic_message_load_delay` enregistre la session, le délai et la corrélation
HTTP sans corps. Vérifier ensuite succès du GET, réactivation UI et état SQL,
puis désarmer les contrôles.

## Résultat avancé avec source canonique

Démarrer `start-advanced-worker.ps1` dans la nouvelle racine. Sa configuration
initiale reste `mode=advanced`, issue `insufficient_documentation` sans source.
Pour la nouvelle variante seulement, choisir dans son contrôle privé :

```json
{"mode":"advanced_answered","delaySeconds":2}
```

Le planner garde la recherche ORION. Le writer doit recevoir une charge non
structurée avec `answerUnitCount=1` et une unique preuve contenant exactement
`Code de validation ORION : 4826.`. La fixture cite **l'evidenceId réellement
présent dans ce JSON**, renvoie une assertion C1 et un seul marqueur `[C1]`.
Elle refuse une preuve absente/ambiguë, un identifiant vide ou une autre forme
de charge. Aucun docId, hash ou numéro de version n'est fabriqué : le backend
résout et valide lui-même la preuve canonique et construit ses cartes.

Un précontrôle API séparé est disponible :

```powershell
& ./tools/validation-winui/test-advanced-worker.ps1 -OutputDirectory '<nouvelle racine>' -ProofName 'advanced-answered-preflight.json' -ExpectedOutcome answered
```

Ce précontrôle crée sa propre session synthétique avec une seule assertion et
vérifie le résultat, la page2, le hash/version/docId du guide canonique, le lien
claim→evidence et l'unicité du marqueur. Il ne teste pas WinUI. Pour la séquence
native, le handoff doit conserver cette charge à une assertion non structurée.
Le résultat contrôlé ne qualifie pas la qualité sémantique du modèle serveur.

Pour W21, dont le prérequis est un job déjà terminal, on peut ensuite préparer
la réhydratation de ce même job sans provoquer artificiellement un routage local
vers l'avancé :

```powershell
& ./tools/validation-winui/prepare-advanced-terminal-resume.ps1 -OutputDirectory '<nouvelle racine>' -Execute
```

Ce helper exige le précontrôle `answered` réussi et son utilisateur distinct du
profil natif principal. Il crée le profil nommé `advanced-terminal` et un seul
assistant contrôlé dans la session de précontrôle encore vide. Le message porte
un jeton **périmé queued/révision1 réellement observé au début du précontrôle**
avec les vrais jobId/handoffId/sessionId. Son texte est un simple avertissement
de reprise ; ses sources sont vides. Aucun texte final, source terminale ou
originTraceId natif n'est fabriqué. Le job existant reste succeeded/révision4.

Après fermeture du profil courant, lancer ce profil avec `start-client.ps1
-ProfileName advanced-terminal` et un nouveau nom de preuve. Le vrai tracker du
client doit récupérer le job, valider son résultat, puis remplacer et PATCHer
le **même assistant** avec la réponse et la carte canonique. Après fermeture et
redémarrage, comparer jobId/messageId/révision/hash et cartes. Le compteur de
jobs et les appels au fournisseur doivent rester inchangés. Cette variante
qualifie la reprise/présentation/persistance ; elle ne prétend pas qu'une nouvelle
création de job ou son routage initial ont eu lieu dans cette séquence UI.

## Lien ambigu entre deux versions

Après préparation des deux PDF et du catalogue :

```powershell
& ./tools/validation-winui/seed-source-ambiguity-session.ps1 -OutputDirectory '<nouvelle racine>' -Execute
```

Le seed crée une seule discussion et un seul assistant explicitement contrôlé.
Dans ce même message, un lien `[[open|...|2|...]]` rencontre deux identités pour
le même chemin/page : la version canonique réelle et une version concurrente
fictive, absente et clairement déclarée telle. La seconde n'est jamais présentée
comme une preuve indexée réelle. Les deux cartes ont des evidenceId distincts.

L'oracle natif est : lien ambigu refusé, puis carte canonique ouvrant le vrai PDF
page2 ; la carte concurrente doit être refusée. Aucun PDF ni document SQL n'est
modifié. Le seed réserve une preuve avant écriture et refuse toute relance avec
le même nom, même après un résultat incertain. Sans `-Execute`, aucune requête
réseau ni mutation n'est faite.

## Vérifications offline

```powershell
python -m unittest discover -s tools/validation-winui -p test_journey_completion_tools.py -v
```

Ces tests sont purs : aucun listener, subprocessus, modèle, UI ou PostgreSQL
n'est démarré. Ils vérifient le ciblage et les bornes du délai, le compteur, le
contrat de réponse sourcée et la conservation du comportement avancé initial.
La syntaxe PowerShell des trois scripts modifiés/nouveaux est vérifiée séparément
sans les exécuter. Les empreintes de ces outils sont ancrées **après** la fullsuite
produit précédente ; elles n'en changent pas rétroactivement le fingerprint.
