# Matrice d’exécution WinUI — preuves finales du lot

Périmètre actuel : observations natives 01–89, preuves de lecture PDF60/64/66, stockage/transport/worker isolés et suite Unit acceptée. Ce document décrit des résultats exécutés. Il ne constitue pas une approbation globale du produit, qui reste `TESTE_NON_APPROUVE`.

La matrice machine lisible est [execution-matrix-final.json](execution-matrix-final.json). Le plan initial est [journey-matrix.json](journey-matrix.json). L’auteur de ce rapprochement n’a exécuté aucun clic UI ; il a lu les observations et métadonnées produites par l’opérateur, puis les a rapprochées des preuves techniques.

## État final et portes restantes

Les correctifs de stockage, annulation, configuration, chargement de discussion et modèle indisponible ont des preuves dans le vrai client. W06 a été retesté sur build08 : erreur claire, récupération via Configuration, puis vrai Qwen qui retrouve le code 4826 avec la source Validation page 2. L’export diagnostic final du profil primaire a été exécuté et ses 14 contrôles passent.

La suite complète finale donne **2458 tests réussis**, zéro échec ni test ignoré, avec empreinte inchangée pendant l’exécution. Ce résultat reste une preuve offline distincte des clics et du rendu.

**W09 suppression effective et W10 suppression active/dernière discussion ne sont pas exécutés.** L’opérateur attend une confirmation utilisateur qui n’a pas été reçue. W09 Annuler/Fermer est observé et conserve bien la discussion. Il reste donc interdit d’affirmer « tous les parcours WinUI validés ».

Les variantes complémentaires non exécutées restent déclarées : titre vide/120 caractères, scroll pendant deltas/Aller en bas, chemins Unicode/espaces et autres limites précisées ci-dessous. Aucun défaut de code n’est inventé à partir de ces absences de preuve.

Le produit reste `TESTE_NON_APPROUVE`. Aucun appel Terra/RunPod/OpenAI payant ni test sémantique du planning de repas n’a été exécuté dans ce lot.

## Statut des parcours

| ID | Statut actuel | Preuve centrale et limite |
|---|---|---|
| W01 | PASS observé | Démarrage et historique utilisables ;01,27,69. |
| W02 | Partiel observé | Premier assistant, Annuler puis reprise/application ;45–47,32. Redémarrage exact du profil initial et absence de valeurs partielles pas documentés séparément. |
| W03 | PASS observé | Réseau absent puis vrai Réessayer après retour ;41–42. |
| W04 | Partiel observé |401 réelle, message français après correction ;43,61.403 et races admin : unités ; remplacement de clé dans le même wizard non documenté. |
| W05 | Partiel observé | Bascule vers alternative joignable ;44. Second démarrage et conservation de l’ordre enregistré non documentés séparément. |
| W06 | PASS observé |86 : message final actionnable.87 : Configuration retrouve le vrai Qwen ;88 : nouveau tour réel, code 4826/E2 et bonne source page 2. Réinstallation/hardware non exercés. |
| W07 | PASS observé | Nouveau, changements et retours sans mélange d’historique ;05,38,55,58. |
| W08 | Partiel observé | Renommer l’inactive et annuler un second renommage ;06,76. Titre vide/120 caractères non exécutés visuellement. |
| W09 | En attente de confirmation | Fermer le dialogue conserve la discussion :39PASS. Suppression effective non exécutée. |
| W10 | En attente de confirmation | Suppression active et dernière discussion non exécutées. |
| W11 | PASS observé |500 conserve le contexte ; notice visible après correction puis retry ;37–38,48. |
| W12 | PASS avec fixture | Deux Shift+Entrée, envoi unique malgré Entrée répétée ;07–08. Client/backend réels, fournisseur déterministe. |
| W13 | PASS observé | RéponseQwen réelle, source et rechargement ;65,69. Un seul cas documentaire direct. |
| W14 | PASS avec fixture | POST retardé, annulation, gate conservée et deadline de30s ;20–22,35–36 et preuve corrélée. |
| W15 | Partiel observé | Appel lent annulé et interruption rechargée ;04,11. Deltas déjà affichés puis arrêtés non observés. |
| W16 | Partiel observé | Contrôles bloqués pendant tour/confirmation puis libérés ;35–36,53. GET lent et clavier derrière overlay : unités seulement. |
| W17 | PASS avec fixture |500, brouillon, notice, retry et deadline réels ;19,28,36,51. ACK malformés en unités seulement. |
| W18 | PASS avec fixture | Deux assistants dans UNE discussion, cartes distinctes et interactionsB puisA sans contamination ;77–78. Aucun panneau Sources séparé exposé. |
| W19 | PASS observé | Lien PDF page 1 ; carte de vraie réponseQwen vers page 2 physique exacte ;26,60,66. Unicode/espaces non exercés. |
| W20 | Partiel observé | Hash refusé, récupérationSHA exacte et absence réelle refusée ;63–64,67. Ambiguïté de révision sans hash : unités. |
| W21 | Partiel observé | Résultat avancé unique terminal ;58. Pas de cartes sources dans le résultat fixture ni redémarrage après terminal. |
| W22 | PASS avec fixture | Vrai processus fermé/rouvert, même job/message, vrai worker ;53–54,58 et preuveSQL. Fournisseur déterministe. |
| W23 | PASS avec fixture | Autre discussion non contaminée pendant/après job puis retour origine ;55,57–58 et empreintes. |
| W24 | PASS avec fixture | Deux503, révision régressive et mauvaise SessionId refusés, puis même job terminal unique ;79–82, preuve corrélée12/12. Autres contrats malformés en unités. |
| W25 | Partiel observé | Apply anglais/clair puis Close sans Apply conserve français/sombre ;15,74–75. Redémarrage exact du choix et tous états/thèmes non documentés. |
| W26 | PASS observé final |89 : export build08 via vraie UI, boutons réactivés. ZIP 51 entrées/1007 sondes,14 contrôles PASS, aucun marqueur connu de clé/texte détecté. |
| W27 | Partiel observé | Normal/maximisé puis1036×641, volet Discussions masqué sans perte du compositeur ;56,83–84. Scroll pendant deltas et Aller en bas non exécutés. |
| W28 | PASS avec fixture | Aide/catalogue et deux homonymes distingués sans LLM ;23–26. |
| W29 | PASS observé | Défaut initial03, puis Paramètres/Configuration désactivés pendant tour ;35,53 et réactivation36. Lease/clavier en unités. |
| W30 | PASS observé | Mode manuel clair, Se connecter réel, AutoConnectfalse conservé ;68–69. |
| W31 | PASS observé | Nouvel endpoint réellement utilisé ;50–51. Échec/reprise manuelle sans ancien état ;70–72. Races admin et trackers en unités. |
| W32 | PASS observé | Notice visible de chargement en échec sans perte de contexte ;48. Erreurs create/rename/delete natives non exécutées. |
| Identité documentaire | PASS observé | Vrai Qwen local → bon code 4826/E2 → bonne source/hash → vrai PDF page 2 ;65–66 et preuve causale. Aucun test repas/Terra. |

Les statuts PASS concernent le comportement central décrit. Les variantes non exécutées ne sont jamais transformées enPASS par un agrégat. La distinction entre partiel etPASS ne remplace pas la lecture des limites de chaque entréeJSON.

## Nature des preuves importantes

- **Local réel65/66** : Qwen3-4B-Instruct-2507 sur le PC client, backend de production isolé et deux PDF synthétiques homonymes. Le lecteur externe affiche `2 of 2` et le marqueur de la bonne page. Le hash du PDF correspond à la révision citée. C’est un vrai résultat documentaire, pas une carte seedée.
- **Avancé53–58** : vrai client, API, worker etSQL durable ; le fournisseur est une fixture déterministe. Même message, même job, révisions2 → 3 → 4, aucun nouveau job au redémarrage. Cela prouve la reprise et l’isolation, pas le raisonnement du grand LLM.
- **W18** : deux messages seedés sans modèle dans une nouvelle discussion. Le premier contrôle d’outil a échoué parce que `@(Invoke-RestMethod ...)` conservait la réponseJSON comme un tableau imbriqué ; les deux messages étaient déjà stockés. L’échec initial est conservé, aucune mutation n’a été relancée, et une vérification séparée en lecture seule prouve exactement deux assistants et leurs identités. Le rendu natif77 complète ensuite la preuve.
- **Sources manquantes** : un chemin absent peut être récupéré parSHA exact. Le cas64 est donc une récupération correcte ;67 retire réellement la version canonique et observe un refus sûr, puis les octets originaux sont restaurés.
- **Suppression** :39 prouve seulement Annuler/Fermer. Aucun clic final de suppression ni remplacement de dernière discussion n’est affirmé.
- **Disposition** :56 prouve normal/maximisé ;83–84 ajoutent1036×641 et le volet Discussions masqué. Cela ne couvre pas toutes les largeurs arbitraires.

## Suite Unit : preuve séparée

La suite Unit complète finale Core/Client donne **80+2378=2458 PASS**, zéro échec ni test ignoré, sans filtre utilisateur ajouté et sans restore. L’empreinte avant/après est identique :
`AC3A4AB986393CA32487B3385B58BA59A03993AA728231867A11F8412C9CCE9A`.

Les deux SHA des fichiers TRX ont été recalculés et correspondent au manifeste. Le profil client était explicitement isolé sous la sortie de campagne et son environnement a été restauré. WinUI testhooks utilise la sortie AnyCPU distincte du binaire de production x64.

Preuve : [full-client-unit-build08-acceptance-summary.json](full-client-unit-build08-acceptance-summary.json). L’ancienne suite 2449 et son premier échec dû à un oracle de profil ont été préservés. Aucun résultat UI non exécuté ni résultat sémantique de Terra n’est déduit de ces 2458 tests.

## Export final et reprise avancée

L’export final build08, produit depuis la vraie UI du profil primaire, contient 51 entrées et 1007 sondes valides. Les14 contrôles de collecte/confidentialité passent : trois marqueurs de credentials et 31 marqueurs de contenu ont été recherchés sans correspondance. Les pièces optionnelles manquantes sont déclarées ; cela ne signifie pas un échec de collecte.

Son historique borné contient un tour récent et ne confirme pas directement `Configuration.originTraceId` du tour de reprise avancée antérieur. Cette limite demeure explicite dans [support-resume-origin-visibility.json](support-resume-origin-visibility.json). La chaîne origine/reprise reste prouvée par SQL et sondes : aucun nouveau job, même message durable et terminal unique. Il ne faut pas attribuer au ZIP une preuve de rétention qu’il ne fournit pas.

## Limites de l’intégrité des traces

La fermeture volontaire du processus pendant le job avancé laisse sa trace d’origine sans terminal. Le vérificateur strict reste en échec sur cette trace ; une preuve séparée explique exactement cette interruption et relie la reprise au même job/message. Aucun terminal n’a été fabriqué et aucun `allowInFlight` n’a masqué l’écart.

Les capturesPDF pouvant contenir l’environnement externe sont privées et exclues de Git. Les preuves exportables retiennent uniquement les marqueurs synthétiques, pages, identités, traces et empreintes utiles.

Le contrôle des références et captures est conservé dans [execution-matrix-evidence-integrity-final.json](execution-matrix-evidence-integrity-final.json). Il vérifie leur existence et leurs empreintes ; il ne transforme pas un nom de capture en résultat visuel ni un test unitaire en clic exécuté.

