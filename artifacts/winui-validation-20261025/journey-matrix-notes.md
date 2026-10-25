# Préparation des parcours WinUI réels — 25 octobre 2026

Cette revue est une **lecture du code**. Elle n'a ni lancé le client, ni manipulé de fenêtre, ni appelé de modèle, ni accédé au serveur. Les 30 scénarios de `journey-matrix.json` sont tous initialement `NOT_EXECUTED`. Ils ne deviennent validés qu'après des observations de la vraie fenêtre, consignées séparément par l'exécutant. Le rapport du 24 octobre et ses tests portables/compilés constituent une base de régression ; ils ne prouvent pas les interactions WinUI.

## Ordre recommandé

1. **W01–W05** : démarrage, configuration, refus et reprise réseau/authentification. Ces parcours déterminent si le client peut être lancé et corrigé par son utilisateur.
2. **W07–W11** : identité de discussion, création, renommage, suppression et échec de chargement. Utiliser uniquement des sessions synthétiques jetables.
3. **W12–W17 et W29** : saisie, admission, progression, annulation au premier await, après streaming et enchaînement de tours. Faire ralentir la fixture permet de rendre les fenêtres de concurrence observables sans appel payant.
4. **W18–W20** : source dans la bulle, panneau, lien, fichier absent, révision ambiguë et source valide. Voir le PDF à la page cible reste une observation distincte d'un retour de lancement Windows.
5. **W21–W24** : job durable et reprise à partir d'états synthétiques prédéfinis ; aucun worker payant. La fenêtre doit réellement être fermée et rouverte.
6. **W25–W28 et W30** : paramètres, langue/thème, support, disposition, aide déterministe et configuration sans connexion automatique.

## Contrôles vérifiés dans les sources

Les AutomationId **explicitement déclarés** sont `InputBox`, `SendCancelButton`, `MessagesList`, `SourcesPanel` et `SourcesCardsItems`. Ce dernier appartient au contrôle de cartes réutilisé dans les bulles et dans le panneau ; il ne faut pas supposer son unicité globale.

Les boutons d'entête ont des noms accessibles localisés grâce à `ApplyIconButtonText` : **Discussions**, **Paramètres**, **Aide** et **Configuration**. Les noms de champs C#/XAML (`HeaderSettingsButton`, `SessionsList`, etc.) permettent de retrouver le code ; ils ne garantissent pas un AutomationId observable.

La liste propose **Nouveau** puis un menu **…** avec **Renommer** et **Supprimer**. La confirmation de renommage porte **Enregistrer** ; les fenêtres peuvent être fermées sans confirmer. Le bouton principal alterne **Envoyer / Annuler** et possède son nom accessible.

La configuration initiale expose **Tester /ready**, **Tester la clé API**, **Appliquer** et **Annuler**. Les tests de disponibilité et d'authentification doivent rester séparés. Les champs `ServerUrlBox`, `ApiKeyBox` et `ConnectButton` de la barre principale historique sont dans une grille `Visibility=Collapsed` : ce ne sont pas des points d'entrée normaux.

Le diagnostic utilisateur est sous **Paramètres → Options avancées → Exporter diagnostic…**. Le support généré est dans le répertoire de support local du profil ; le code actuel utilise `%LOCALAPPDATA%/SAAIA/support` à la base de cette revue, mais la mission prépare son isolation. Ne pas déduire ce chemin si le profil de validation le redirige.

## Risques de parcours à vérifier réellement

### W29 — Paramètres admis pendant un tour

**Écart confirmé par lecture, reproduction UI encore attendue.** `StartupSetup.UpdateUiState` désactive saisie, liste, Nouveau, Aide et Console admin pendant le tour/changement de discussion. Il ne désactive pas `HeaderSettingsButton`. `AdminAndSettings.UserSettings_Click` n'a ni garde de tour ni admission dans `ConversationActivityGate`.

Après application, ce handler appelle `_agent.ApplySettings`. Dans `RagChatAgent.ApplySettings`, les changements touchent `_effectiveSettings`, la langue et le mode de la mémoire, les paramètres de génération et `_llm.ConfigureGeneration` sur le provider utilisé. L'onglet avancé permet également une réparation du runtime. Une observation du bouton et du dialogue pendant une réponse ralentie doit confirmer le chemin réellement accessible avant correction.

**Critère après correction** : ouverture refusée/désactivée dès l'admission ; aucune mutation du provider/mémoire pendant le tour. Après fin/erreur/annulation effective, les paramètres redeviennent accessibles. Aucun téléchargement ni redémarrage de runtime n'est nécessaire pour prouver le défaut initial.

### W17 — Question non durable après échec de sauvegarde

Dans `Messages.SendAsync`, la saisie est vidée et la bulle utilisateur ajoutée avant le POST de sauvegarde. Si ce POST échoue, le catch affiche un statut, sans restaurer explicitement le texte ni annoter la bulle comme non envoyée. Le statut général se masque après environ 1,8 seconde hors génération. **Hypothèse de perte de récupérabilité à reproduire** avec un POST initial qui échoue, puis rechargement.

Attendre une indication récupérable de l'échec ou le texte rendu disponible pour réessayer ; un seul POST durable après reprise et aucun appel modèle avant sauvegarde de la question. Ne pas conclure que le défaut existe à partir du seul code si une autre couche UI restaure la saisie.

### W30 — AutoConnect=false en mode utilisateur

`AppSettings` permet `AutoConnect=false`. `InitializeUserModeAsync` n'appelle alors pas ConnectAsync et ne montre l'erreur de connexion que si AutoConnect=true. En mode utilisateur standard, le Connecter de la barre historique est masqué. **Hypothèse de cul-de-sac à reproduire** : profil avec clé connue, AutoConnect=false, ShowAdvancedUi=false. Un état déconnecté et une action visible pour se connecter doivent exister.

### Mesure d'interactivité Envoyer/Annuler

`ThemeChrome.UpdateSendCancelButtonVisualState` force `IsEnabled=true` puis définit `IsHitTestVisible=canInvoke`. Par conséquent, une lecture UIA de IsEnabled ne démontre pas l'acceptation d'un clic. Les handlers recontrôlent l'état et le gate. Il faut vérifier l'effet observé et le nombre de tours réellement admis, y compris pendant l'annulation.

## Scripts historiques : limites et non-exécution

`tools/test-advanced-winui-terminal-sources.ps1` charge UIAutomationClient/UIAutomationTypes, recherche des AutomationElement et invoque des InvokePattern. Il démarre aussi un backend temporaire et injecte un état SQL terminal issu d'un export. **Ne pas le lancer pour cette mission** : il contournerait le pilotage Computer Use autorisé et mélangerait preuve de fixtures et interaction.

`tools/test-advanced-winui-restart-resume.ps1` n'est pas un simple observateur de fenêtre : il lit un fichier .env serveur, démarre un backend configuré à partir de ces secrets, remplace temporairement settings.json et s'appuie sur les traces HTTP de GET/PATCH pour la reprise. Son succès ne démontrerait pas à lui seul le rendu des bulles ni les clics. Il ne doit pas être réutilisé ici comme substitut aux parcours contrôlés.

## Preuves minimales

Pour chaque scénario : SHA et architecture du binaire lancé, profil/cible locale, actions réelles via le moyen Computer Use autorisé, captures avant/après, résultat PASS/FAIL/BLOCKED/NOT_EXECUTED et lien vers diagnostics correspondants. Relier message/session/job/trace/tour aux observations ; une capture seule n'établit pas la persistance et un HTTP 200 seul n'établit pas l'affichage.

Les tests réels sur fixture démontrent les contrats d'interface/transport et les réactions aux états contrôlés. Ils ne démontrent pas le raisonnement d'un modèle ni le planning sur le corpus réel. Terra demeure hors de cette campagne et le statut produit reste TESTE_NON_APPROUVE.
