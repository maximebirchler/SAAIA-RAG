# Preuves du parcours WinUI

`SAAIA_CLIENT_DATA_ROOT` désigne une racine **SAAIA directement**, absolue, définie dans l'environnement du processus avant son lancement. Par exemple : `C:\evidence\client-data`, puis `client\settings.json`, `client\secure.json`, `logs`, `governance`, `Models`, `llm\runtime`, `exports` et `support`. Sans cette variable, les emplacements historiques sont inchangés.

Avec une racine explicite, les paramètres et secrets utilisent uniquement les fichiers dédiés, y compris dans une application packagée. Le provisioning machine et les anciennes LocalSettings ne sont pas lus ou migrés. Le seul provisioning découvert automatiquement est `provisioning.json` dans cette racine. Les secrets restent chiffrés par DPAPI pour l'utilisateur Windows courant. Une racine relative est rejetée, sans repli volontaire sur le profil normal.

Cette isolation de stockage n'est pas un bac à sable réseau. Pour la campagne sans dépense, l'environnement de lancement impose `SAAIA_LLM_PROVIDER_MODE=Local`, `SAAIA_LLM_EXTERNAL_POLICY=ProductionLocal`, un backend local, un LLM local et un fichier de configuration fournisseur contrôlé. Les clés fournisseurs héritées doivent être retirées de l'environnement enfant. Le profil utilisateur existant n'est ni copié ni modifié.

## Minimum de preuves à conserver

- Empreintes de l'exécutable et de l'assembly Release x64 exécutés ; build sans hooks, avertissements et erreurs.
- Manifeste de l'environnement local : endpoints, processus propriétaires, base PostgreSQL isolée, corpus synthétique et arrêt des seuls auxiliaires créés pour la campagne.
- Captures de l'interface et observations par scénario : connexion, message et streaming, annulation, reprise, création/chargement/renommage/suppression de conversation, sources, export et redémarrage. Une observation doit nommer son scénario et son résultat effectivement constaté.
- Logs `CLIENT_PROBE` avec trace, tour, session, séquence, résultat final et phases. L'annulation et l'erreur attendues sont des issues de scénario ; elles ne doivent pas être reformulées en réponses réussies.
- Bundle support obtenu par l'interface, avec `collection-manifest.json`, `diagnostics/client-turns.json`, santé des collecteurs, pièces manquantes ou échouées déclarées et mode métadonnées.
- Pour une source, identité SHA-256, page demandée, sonde de résolution, résultat de lancement et capture du document réellement affiché. `launch_accepted_page_unverified` seul ne prouve pas la page rendue.

Le fixture HTTP est un répondant déterministe de protocole. Il ne valide ni la compréhension d'un modèle, ni la sélection de preuves par un LLM, ni la recherche vectorielle réelle, ni le planning de repas. Un backend réel avec PostgreSQL et deux documents synthétiques valide les chemins effectivement parcourus dans ce périmètre, sans devenir une validation du corpus client complet.

La confirmation du premier message possède son propre délai de **30 secondes maximum**. L'annulation utilisateur n'annule pas cette écriture en cours ; elle empêche le démarrage du modèle après confirmation. Si le serveur reste silencieux jusqu'au délai, le client conserve le brouillon, affiche l'avertissement de sauvegarde non confirmée et libère le tour. Ce délai ne prouve jamais que le serveur n'a pas sauvegardé le message et n'entraîne aucun retry automatique. Il ne modifie pas les contrats des appels avancés ou des autres requêtes API. La sonde `chat.user.persistence` expose `confirmationTimeoutMs` et le motif `confirmation_timeout`.

## Vérification des traces

```powershell
& tools/validation-winui/verify-evidence.ps1 `
    -ClientDataRoot 'C:\evidence\client-data' `
    -OutputPath 'C:\evidence\trace-integrity.json' `
    -SupportBundle 'C:\evidence\client-data\support\support-bundle.zip' `
    -FixtureEvents 'C:\evidence\fixture-events.jsonl' `
    -MinimumCorrelatedFixtureRequests 1 `
    -RequiredOperations @('turn.begin', 'turn.end') `
    -RequiredTerminalOutcomes @('success', 'cancelled', 'failed')
```

Le vérificateur refuse d'écraser une preuve. Il contrôle le schéma, la stabilité de la corrélation, l'unicité des séquences et de l'issue finale, l'absence d'événement après la fin, la santé du support et la correspondance des requêtes fixture corrélées. `-AllowInFlight` sert seulement à une collecte intermédiaire explicitement annoncée. Une rotation ayant supprimé le début du tour est une preuve incomplète et n'est pas transformée en réussite.

Son verdict `TRACE_INTEGRITY_VALIDATED` concerne exclusivement les traces. Les champs `visualUiValidated`, `modelQualityValidated` et `pdfPageRenderingValidated` restent faux : seules les observations distinctes du parcours peuvent établir ces points. Le statut produit reste `TESTE_NON_APPROUVE`.

`test-evidence-verifier.ps1` vérifie hors réseau que le journal synthétique valide passe et que l'absence de fin, la duplication de séquence et un événement après fin sont refusés. Ces auto-tests ne constituent pas une exécution WinUI.
