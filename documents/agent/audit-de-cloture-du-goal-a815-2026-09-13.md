# Audit de clôture du Goal — A815, 13 septembre 2026

Le produit reste **TESTE_NON_APPROUVE**. Cet audit rapproche les exigences du Goal des preuves closes ; il ne constitue pas une approbation globale. Le candidat de départ est `ad7fe14e70949db733578978f47cd5723cdec032`. Les instructions actuelles de l'utilisateur priment sur celles des discussions et plans archivés.

## Situation utile

L'architecture locale → handoff → job backend → grand modèle → résultat durable fonctionne comme chaîne technique. Cela ne suffit pas à obtenir le planning correct. Avant recharge, A811 se termine en insuffisance et ne produit aucune des vingt cellules demandées. Le modèle avait reçu un sommaire contenant des candidats utiles, mais n'avait pas approfondi certains titres et n'avait demandé aucune lecture native. Les corrections A812 explicitent cet usage du sommaire ; A814 corrige la perte de corps de recettes pendant la sélection des extraits de lectures.

Les mesures poursuivies pendant cet audit distinguent maintenant deux états. A816, vrai parcours produit sur `ad7fe14e`, choisit des recherches par titres mais se termine encore sans planning après 31 recherches et sept appels (0,3669042 USD). A817, un seul appel du Writer de production avec les lectures canoniques retrouvées extérieurement et le même prompt système, produit vingt choix distincts en 20,223 s (0,0768015 USD). La revue des vingt choix/23 références et le parseur/policy de production passent. Ce diagnostic de rédaction ne comprend aucun critique, choix autonome de lectures, job durable ou WinUI ; il prouve cette capacité de synthèse sur le cas connu, pas l'acceptation E2E.

Le diagnostic A813 a obtenu des contenus canoniques dans vingt fenêtres de lecture, soit 209 chunks uniques. Deux coordonnées ont été choisies manuellement à partir des sources déjà consommées. Le replay A814 conserve exactement ces 209 chunks et les vingt lectures ; quatre passages auparavant invisibles atteignent désormais le prompt. Il ne prouve ni que le modèle choisit seul ces lectures, ni que les vingt propositions sont toutes appropriées et correctement liées à leurs propres preuves.

La banque locale connue sur `8437c345` donne 42/42 comportements conformes, dont neuf réponses locales terminées. Le reste comprend aussi clarification, insuffisance et transfert sûr. Quatre préparations et quatre valeurs JSON ne sont pas qualifiées comme réponses locales terminées. Ce résultat ne signifie pas que le petit modèle répond à 42 questions directes ou qu'il est éprouvé sur toutes les formulations possibles. Le verdict aveugle historique BH6 sur `2296a4a` demeure rejeté à 1/24 ; il n'est pas modifié par les contrôles connus ultérieurs.

## Priorités de reprise

| Exigence | Preuve ou état constaté | Limite de clôture |
|---|---|---|
| Lecture du paquet documentaire | Registre D001–D043 et rapport de lecture complète A657 ; couverture textuelle et hashes conservés | Lecture textuelle ne certifie pas la revue visuelle de tous les PDF ni celle de chaque artefact technique |
| Remplacement depuis le laptop | Import et sauvegarde de reprise déjà réalisés ; historique Git conservé | Aucun nouvel import, reset ou nettoyage global nécessaire |
| Petit modèle sur ce PC | Campagnes Qwen locales et profil gelé ; résultats distincts de P520 | Nouvelle preuve matérielle/performance à chaque campagne, aucune substitution silencieuse Q4/Q5 |
| Expériences et gates | Anciennes campagnes conservées, A755 pilote désormais les choix | Les tests mécaniques ne rouvrent pas automatiquement les gates produit |
| Rapports compréhensibles | Rapports Telegram découpés et reçus conservés | Continuer sur les changements utiles et au minimum à l'heure pendant le travail actif |

## Amendement A755

| Point | État | Preuve ou prochaine exigence |
|---|---|---|
| 1. Deux capacités | Architecture réalisée ; qualité globale non approuvée | Petit modèle client et job avancé backend ; final grand modèle serveur client |
| 2. Chaîne commune | Contrats, identités canoniques et cartes source utilisés par l'avancé | Contrôler l'intégrité de la preuve jusqu'à chaque affirmation et à WinUI ; aucune seconde vérité RAG |
| 3. Mesure causale | Mesures retrieval, replay du contexte et essais réels E2E/Writer distincts | A813/A814 isolent disponibilité/visibilité ; A816 échoue en E2E et A817 réussit la synthèse avec lectures externes ; navigation autonome finale non qualifiée |
| 4. Difficulté générale | Routage structurel et contrôles multilingues connus exécutés | Généralisation aveugle actuelle non démontrée ; pas de règle spéciale recettes/documents/langue |
| 5. Réponse ou transfert sûr | 42/42 décisions connues conformes, neuf réponses locales terminées | Le transfert sûr n'est pas une réponse locale réussie ; préserver la distinction calcul limité/documents absents |
| 6. Épreuve maximale | Corrections avec contrôles rouges puis verts et campagnes connues | La robustesse n'est pas certifiée à 100 % ; nouveau holdout après gel requis |
| 7. Protocole qualité/temps | Profils, TRX, captures, durées et tokens disponibles | Dernières suites backend A814 : 2 299 réussites, 0 échec, 3 live ignorés ; ne pas en déduire une latence utilisateur validée |
| 8. Quatre routes | Local, clarification, insuffisant et avancé éprouvés sur contrôles connus | Acceptation complète des deux côtés de la frontière et UX actuelle restent à prouver |
| 9. Planning 5 × 4 | E2E rejeté ; vingt propositions du Writer isolé acceptées sur diagnostic connu | Choix autonomes et vingt preuves propres dans le produit ; trois réussites live consécutives sur état figé |
| 10. API puis serveur | OpenAI autorisé, utilisé et mesuré | RunPod/location non autorisés financièrement ; aucune location à lancer automatiquement |
| 11. Sécurité/contexte | Auth et isolation tenant/user testées, sources revalidées ; purge de rétention démontrée en PostgreSQL isolé | TLS/tunnel, volumes et sauvegardes chiffrés sont des préconditions de déploiement non certifiées sur le serveur cible |
| 12. Fin de mission | Non remplie | Matrice locale finale, frontière dans WinUI, tâches avancées fiables, holdout et cible serveur restent nécessaires |

## Phases produit

| Phase | Ce qui est conservé | Ce qui reste ouvert |
|---|---|---|
| 1. Baseline et suivi | Lecture, reprise, profils, corpus et versions tracés | Actualiser le suivi ancien Free tier ; ne pas recommencer les preuves encore valides |
| 2. Chaîne locale verticale | Campagnes E2E connues, sources exactes et routes | Acceptation générale sur candidat final et corpus/configuration figés |
| 3. Orchestration adaptative | Contrats typés, identité fournie et clarification corrigés | Le choix autonome de lectures/candidats avancés reste à prouver |
| 4. Recherche/mémoire | Historique de recherches, focus par source, limite de doublons/non-progression | Vérifier les décisions live de pagination, pivot et arrêt ; mémoire jamais seule preuve |
| 5. Intégrité des preuves | Resolver canonique, contrôle des citations, audit physique, priorité des corps A814 | Un fichier/page ouvrable peut néanmoins être la mauvaise preuve d'une affirmation |
| 6. Variété et mémoire | Contrôles connus FR/EN et de référence conversationnelle | Robustesse aveugle, multi-utilisateur/projet et compaction sur parcours final |
| 7. Performance | Durées/tokens des appels et local connus ; comptes techniques distincts | Trois réussites critiques et capacité serveur finale ; 203 ms de lectures diagnostiques n'est pas une latence produit |
| 8. Consolidation | Modules du provider, petits commits vérifiés/poussés ; worktree préservé | Audit final du chemin unique et migration des anciennes cartes avant toute purge |
| 9. WinUI et clôture | Vraie preuve historique d'affichage de deux cartes et ouverture exacte, résultat durable rejoué sans appel modèle | Nouveau planning accepté dans WinUI avec ouverture de toutes ses cartes et gates finaux satisfaits |

La preuve WinUI `a763-winui-terminal-sources-a85e0a2-20260912/result.v1.json` valide un affichage réel et un clic exact sur un résultat rejoué, avec zéro nouvel appel fournisseur. Elle ne doit être décrite ni comme inexistante, ni comme l'acceptation du nouveau planning complet.

## Garde de campagnes et budget

Le runner sélectionne encore `user_id='automated-validation'`, alors que le test configure `ApiClient` avec un GUID aléatoire et utilise « automated-validation » comme `client_user` de session. Ce garde ne couvre donc pas forcément les jobs du test. L'inventaire complémentaire A815 utilise uniquement les UUID exacts déjà enregistrés dans le journal local : 304 UUID, 294 retrouvés en base, dix manquants, aucun en attente/en cours. Connexion PostgreSQL en lecture seule, zéro mutation et zéro appel API. Les jobs créés sans appel observé restent hors de ce périmètre ; il ne s'agit pas d'un audit global de la file. Le garde doit prendre les identités effectivement créées avant une prochaine campagne aveugle.

Avant la nouvelle recharge, le journal comptabilisait 29,92292310 USD sur 30 USD. Le diagnostic d'admission A814 calculait au moins 0,090647 USD pour son rédacteur, au-delà des 0,07707690 USD restants. Ce blocage monétaire était réel pour cette exécution, mais n'est plus l'état actuel : l'utilisateur confirme dix USD achetés supplémentaires et autorise l'usage de tous les crédits qu'il achète pour la mission. L'enveloppe passe donc à 40 USD. Billing affiche 10,08 USD avant les nouveaux essais et Limits Tier 1, observés en lecture seule le 13 septembre à 15:59 UTC ; auto-reload reste désactivé. Après A816/A817, le journal totalise 30,36662880 USD, avec 9,63337120 USD calculés restants. Aucun achat n'a été fait par l'agent.

L'inventaire après ces deux essais porte sur 306 UUID, 295 retrouvés, onze manquants et aucun job connu en attente/en cours. La corrélation du diagnostic Writer sans job durable explique un de ces UUID supplémentaires manquants. Les deux snapshots sont conservés ; aucun job utilisateur extérieur au périmètre n'est annulé.

Les paramètres d'installateurs interactifs en fonction de la licence restent différés par l'utilisateur. Les profils déjà signés et les contrats doivent être compatibles avec cette vision, sans imposer ce chantier différé comme un faux blocage de la mission actuelle.

## Ordre d'exécution

1. Fait : essai connu E2E A816 sur `ad7fe14e`, limites d'outils/contexte/temps inchangées, plafond monétaire de job 0,75 USD et sept appels maximum ; inputs et sorties réels capturés, verdict rejeté.
2. Fait : isoler la rédaction A817 avec les lectures canoniques retrouvées, plafond 0,30 USD et un appel, sans réduire la demande ; vingt choix acceptés dans ce diagnostic. Comparer maintenant les stratégies générales d'exploration et de conservation des preuves avant une nouvelle intervention produit.
3. Une fois une réponse E2E acceptée obtenue, geler code/configuration/corpus et mesurer trois réussites consécutives des cas critiques, puis le holdout nouveau et le parcours WinUI. Corriger auparavant le garde d'identités de campagnes. Aucun verdict final ni nouvelle location anticipés.

Les hashes et comptes contrôlés sont scellés dans `completion-evidence-audit.v1.json`. Les preuves historiques restent inchangées. Un nouveau crédit permet de poursuivre les mesures ; il ne change aucun résultat rejeté.
