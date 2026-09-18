# A862 — audit privé et intègre du pilote Candidate Explorer

## Verdict du palier

Le prochain pilote live du planning 5 × 4 est maintenant préparé pour conserver
les éléments qui permettent d'expliquer causalement son résultat. Le harnais
capture les traces fournisseur, l'état durable de recherche, les appels
d'outils et le résultat final dans des artefacts privés ignorés par Git. Il
refuse de considérer un pilote Candidate Explorer terminé si ces preuves
n'existent pas.

Aucun appel OpenAI ou autre appel payant n'a été exécuté pendant A862. Le
registre reste à 39,9943606 USD sur 40 USD, avec 0,0056394 USD disponibles,
moins que la réservation minimale de 0,0061465 USD pour le premier appel. La
qualité sémantique de Terra sur le planning n'est donc pas requalifiée et le
produit reste `TESTE_NON_APPROUVE`.

## Lacune observée après A861

Le résultat public d'une campagne conservait la réponse et la télémétrie du
job, mais pas tout ce qui explique comment le modèle avait construit cette
réponse. Le checkpoint du Candidate Explorer et les événements d'outils
restaient uniquement dans PostgreSQL. De plus, un profil pouvait demander
l'Explorer sans imposer un répertoire de traces fournisseur.

Cette lacune aurait permis de dépenser un nouveau budget puis de retrouver une
réponse incorrecte sans pouvoir distinguer précisément :

- une mauvaise enveloppe envoyée au fournisseur ;
- une navigation ou une reformulation insuffisante ;
- des candidats découverts mais non conservés ;
- un corps non vérifié ou une preuve perdue ;
- une affectation de rôle incorrecte ;
- une perte entre Explorer, Writer et Critic.

## Audit PostgreSQL privé

`SAAIA.AdvancedValidationJobGuard` accepte maintenant
`--audit-output <fichier>`, uniquement avec le manifeste exact des propriétaires
de validation. Le mode historique par libellé utilisateur ne peut ni annuler ni
auditer des jobs.

La lecture d'audit :

- exige des propriétaires GUID non vides enregistrés avant la création du job ;
- utilise une transaction PostgreSQL en lecture seule ;
- filtre les jobs par ces propriétaires puis les événements par les identités
  des jobs obtenus ;
- borne l'export à 1 000 jobs et 32 768 événements d'outils ;
- conserve le handoff, le résultat, le checkpoint de recherche, les requêtes
  d'outils, leurs références de preuve, leur état, leur latence et leur code
  d'erreur ;
- écrit atomiquement un document
  `saaia-advanced-validation-private-job-audit-v1` ;
- marque explicitement l'artefact `containsPrivateCorpusMetadata=true` et
  `mustNotCommit=true` ;
- calcule et reporte son SHA-256 sans afficher son contenu.

Le rapport du garde expose séparément les nombres de jobs, d'événements et de
checkpoints audités. Le test PostgreSQL crée un job appartenant au pilote et un
job étranger, puis vérifie que seul le premier, son checkpoint et son événement
d'outil sont exportés.

## Traces fournisseur obligatoires

Lorsque Candidate Explorer est actif et qu'aucun chemin n'est fourni, le runner
crée automatiquement `private-traces` dans le répertoire neuf de la campagne.
Les wrappers OpenAI et RunPod transmettent aussi un chemin explicite au runner
générique.

Après une campagne réussie, le runner exige au moins une trace fournisseur. Il
produit un manifeste privé avec le chemin relatif, la taille et le SHA-256 de
chaque trace. Le postflight exige en plus au moins un job durable, un checkpoint
et un événement d'outil. Une campagne interrompue conserve ce qui existe pour
le diagnostic, mais n'est pas transformée en réussite.

Le profil A861 déclare maintenant ces obligations dans `evidenceCapture`. Le
préflight ajoute `candidate_explorer_evidence_capture_invalid` si les traces,
l'audit durable ou les deux marqueurs de confidentialité ne sont pas demandés.
Un contrôle négatif avec une copie du profil sans cette section est bien bloqué
avant tout appel externe.

## Observation de migration locale

Le premier smoke test réel de la CLI a trouvé que la base locale permanente ne
possédait pas encore la colonne `research_checkpoint`, alors que les bases de
test neuves appliquaient déjà la migration 068. Un démarrage contrôlé du backend
actuel, avec bootstrap et workers désactivés, a appliqué les migrations puis a
été arrêté. La CLI a ensuite produit l'audit privé vide attendu pour un nouveau
propriétaire, avec les marqueurs privés et un SHA-256 vérifié.

Ce constat ne change pas le protocole du pilote : son backend applique les
migrations avant de devenir prêt. Il confirme en revanche que le smoke test
devait être fait contre la base réelle et pas seulement contre une base de test
éphémère.

## Contrôles exécutés

- analyse syntaxique réussie pour les quatre runners PowerShell concernés ;
- JSON du profil relu avec succès ;
- build de `SAAIA.AdvancedValidationJobGuard` sans avertissement ni erreur ;
- smoke test réel de la CLI contre PostgreSQL après migration : audit capturé,
  zéro job étranger, marqueurs privés présents et empreinte identique ;
- préflight du profil complet : politique A862 reconnue, aucun appel externe,
  blocage budgétaire conservé ;
- préflight négatif sans `evidenceCapture` : refus
  `candidate_explorer_evidence_capture_invalid`, aucun appel externe ;
- 15 tests `AdvancedAnalysisEndpointsTests` réussis contre PostgreSQL ;
- 299 tests du provider réussis ;
- suite backend complète avec PostgreSQL actif : 2 459 réussites, zéro échec et
  trois live ignorés, en 14 min 47 s.

## Limites et suite

A862 valide la collecte et l'intégrité des preuves, pas l'autonomie sémantique
de Terra. Il n'existe encore aucun audit live A862 du planning, puisque la marge
autorisée ne permet pas de réserver le premier appel. Le pilote reste donc
`PREREGISTERED_BUDGET_BLOCKED`.

Lorsque du calcul avancé redevient disponible, la suite correcte reste un seul
run du profil gelé. Sa réponse doit être revue mécaniquement et humainement avec
les traces et l'audit privé. Si elle échoue, la correction doit viser l'étape
exacte prouvée par ces artefacts, puis le profil est de nouveau gelé. Si elle
réussit, les répétitions deux et trois sont exécutées sur le même état avant un
holdout aveugle, la reprise durable end-to-end et WinUI. Le même protocole
s'applique à OpenAI, RunPod ou au futur serveur client compatible.
