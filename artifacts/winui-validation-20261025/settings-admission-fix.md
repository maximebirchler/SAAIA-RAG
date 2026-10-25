# W29 — admission des paramètres pendant un tour

Le défaut a été reproduit par le parent dans la vraie fenêtre : le dialogue Paramètres et son bouton Appliquer restaient accessibles pendant une réponse, alors que le bouton de chat affichait Annuler. Preuves initiales : `native-ui/03-settings-during-active-turn-before-fix.json` et sa capture PNG.

## Correction ciblée

- `StartupSetup.UpdateUiState` applique le même verrou visuel à HeaderSettingsButton qu'aux actions susceptibles de modifier la conversation.
- `UserSettings_Click` tente l'admission dans ConversationActivityGate avant le chargement des préférences et avant tout await. Un appel tardif/programmatique est refusé si un tour ou un autre changement possède déjà la conversation.
- La lease reste possédée pendant la fenêtre de paramètres, les opérations de diagnostic/réparation et l'application des options au provider, à la mémoire, à la langue et au thème.
- Le finally libère la lease avant le dernier rafraîchissement UI. Une erreur ou une fermeture sans application ne laisse pas les contrôles verrouillés.
- Pendant cette lease, IsChangingSession rend aussi les handlers de saisie/envoi et changement de discussion indisponibles : une touche Entrée derrière l'overlay ne peut pas lancer un nouveau tour.
- Le suivi d'un job avancé en arrière-plan n'est pas un tour de premier plan. Il ne bloque donc pas l'ouverture des paramètres et l'export de diagnostic quand la conversation est libre.

La sonde d'admission refusée utilise l'issue normalisée `skipped` et la raison `conversation_busy`, sans inventer une panne métier.

## Régression

`SettingsAdmissionContractTests` comprend quatre cas : dépendances et ordre des appels du handler réellement compilé ; refus pendant un tour actif ou en cours d'annulation ; conservation de l'admission pendant les awaits du dialogue et de l'application ; libération également si l'application échoue. Les tests utilisent les sorties testhooks séparées et ne reconstruisent pas le binaire de production alors que la fenêtre est ouverte.

Le manifeste `settings-admission-unit-final/manifest.json` fait foi pour le dernier test ciblé. L'observation UI après recompilation production demeure une étape séparée à renseigner par le parent. Le correctif ne prétend pas valider le planning ni un modèle.
