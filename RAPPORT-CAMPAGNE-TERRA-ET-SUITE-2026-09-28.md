# Rapport de clôture de la campagne Terra et suite recommandée — 28 septembre 2026

## Décision immédiate

La campagne OpenAI Terra est close à 44,42531530 USD sur l'autorisation locale
de 45 USD. Le reliquat de 0,57468470 USD est conservé : il est inférieur au coût
des deux derniers pilotes comparables et ne permet pas de garantir une chaîne
complète Explorer, Writer et Critic. Aucun nouvel achat n'est recommandé avant
le refactoring décrit ci-dessous.

Le planning 5 × 4 reste rejeté. Le produit et le Goal restent
`TESTE_NON_APPROUVE`. En revanche, la campagne a validé plusieurs briques et
retiré onze défauts locaux qui empêchaient auparavant de mesurer honnêtement le
modèle.

## Ce qui est validé

- Le routage envoie la demande complexe vers la capacité avancée et conserve un
  handoff typé avant retrieval.
- Le provider OpenAI Responses exécute Planner, outils natifs, Candidate
  Explorer, Writer et Critic avec coût, tokens, traces et IDs de requêtes.
- `search_corpus`, `find_source_text` et `read_source` fonctionnent sur les
  identités canoniques et sont effectivement choisis par Terra.
- L'inventaire durable sépare localisateurs et corps, conserve les EvidenceId,
  supporte les titres OCR et les raffinements d'identité bornés au même corps.
- Le Writer ne peut citer que les preuves visibles et une insuffisance peut
  rester sans claims au lieu de fabriquer des cases.
- Les audits mécaniques et privés détectent les incohérences de provenance sans
  transformer un test vert en approbation sémantique.
- Le budget persistant, le hard stop, le sceau corpus et le nettoyage des
  processus ont fonctionné sur chaque pilote.

## Ce qui n'est pas validé

- Aucun pilote autonome n'a livré vingt choix distincts, adaptés et prouvés.
- Les résultats varient fortement : 13 candidats affectés dans A876, 17 dans
  A877, puis 3 dans A878 malgré davantage d'appels.
- L'Explorer répète des recherches, demande parfois des fenêtres invalides et
  reporte trop tard la consolidation sémantique de son inventaire.
- La conversation transporte près de 30 000 tokens sur de nombreux appels ;
  A878 cumule 333 339 tokens d'entrée. Cette croissance augmente coût et bruit.
- Une réussite mécanique et une insuffisance honnête sont sûres, mais ne
  satisfont pas le stress-test lorsque le corpus connu contient les corps
  manquants.
- Les trois réussites live consécutives, le holdout aveugle et le parcours WinUI
  final ne sont pas exécutables avant correction de la découverte.

## Pourquoi Terra n'a pas suffi

Terra n'est pas bloqué par l'incapacité de reconnaître un livre ou un sommaire.
Il l'a fait et a suivi des titres exacts. Le problème vient du protocole qui lui
demande simultanément de naviguer, construire des arguments d'outils, corriger
les bornes, interpréter des centaines d'extraits, tenir un inventaire, compter
les doublons et décider quand arrêter. Une erreur ou un retard de consolidation
consomme plusieurs appels supplémentaires et grossit tous les prompts suivants.

L'hébergement ou un modèle encore plus puissant peut améliorer la probabilité,
mais ne retire pas ce défaut structurel. Le premier investissement doit donc
aller dans un protocole compact et testable, pas dans davantage de tokens sur la
même boucle.

## Architecture suivante

La chaîne avancée doit devenir une orchestration en étapes :

1. **Navigator** : choisit sémantiquement des entrées nommées depuis une vue
   compacte des sommaires et des lacunes.
2. **Document executor** : exécute mécaniquement les recherches, pages et
   paginations valides, puis produit des cartes de corps canoniques.
3. **Candidate Judge** : classe de petits lots de cartes, décide l'identité
   complète et les rôles compatibles, avec justification et EvidenceId.
4. **Coverage solver** : déduplique et calcule mécaniquement un matching entre
   candidats et coordonnées afin de garantir vingt identités distinctes.
5. **Gap controller** : renvoie uniquement les lacunes réelles au Navigator.
6. **Writer et Critic** : reçoivent un dossier compact, réalisable et prouvé.

Le même contrat reste indépendant du fournisseur. OpenAI sert à la validation
fonctionnelle ; RunPod puis le serveur client servent ensuite à comparer coût,
latence, confidentialité et modèles open source. La licence et l'installateur
pourront sélectionner local seul, avancé seul, hybride, endpoint privé ou API,
sans dupliquer l'architecture RAG.

## Séquence de travail recommandée

1. Geler les artefacts A874–A878 et écrire des replays sans réseau.
2. Implémenter le registre compact, le Candidate Judge par lots et le solveur
   d'affectation, avec tests sur chevauchements de rôles et doublons.
3. Rejouer A817, A877 et A878 hors réseau jusqu'à obtenir le même dossier
   déterministe à partir des mêmes décisions enregistrées.
4. Vérifier les cas simples et les handoffs locaux pour éviter toute régression.
5. Préenregistrer un nouveau pilote OpenAI. À ce moment seulement, un petit
   budget de 5 à 10 USD est raisonnable pour trois essais consécutifs.
6. Après réussite fonctionnelle, exécuter le même protocole sur un GPU loué puis
   dimensionner le serveur client.

## État de livraison

Les correctifs A868–A877 sont poussés sur `SAAIA_V3.1`. La suite provider compte
306 tests réussis. La validation finale `dotnet test RAG.sln -c Release
--no-restore` réussit : 10 tests Contracts, 2 466 tests Backend avec 3 live
ignorés, et 2 338 tests Client ToolAgent avec 1 live ignoré. `git diff --check`
passe. Les rapports détaillés et preuves privées sont conservés localement. Le
dépôt doit encore recevoir ce rapport A878 et le manifeste de l'archive avant
le dernier commit de sauvegarde.
