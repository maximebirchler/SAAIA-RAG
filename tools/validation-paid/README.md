# Campagne Luna locale et bornée

Les scripts de ce dossier ne changent pas les profils historiques Terra. Le seul modèle autorisé ici est `gpt-5.6-luna`, à l’endpoint officiel exact `https://api.openai.com/v1`. L’enveloppe incrémentale totale est au maximum 0,50 USD, frais de disponibilité précédents inclus. Aucun achat, recharge, Terra, RunPod ou serveur SAAIA distant.

Sans `-Execute`, `invoke-local-luna-campaign.ps1` valide le profil, le PDF et le hash du backend et écrit uniquement un préflight : aucune lecture de clé, aucun service/build ou appel réseau. L’exécution exige les deux autorisations explicites, le cap courant, un profil neuf `AUTHORIZED` daté et motivé et le chemin explicite d’un magasin DPAPI. Une proposition est toujours créée `PROPOSED` ; la promotion est une décision de l’opérateur et doit produire un **nouveau fichier**, jamais réécrire une proposition déjà utilisée comme preuve.

Les prix sont des données datées de campagne vérifiées par l’opérateur le 25 octobre 2026 : entrée 0,20, cached 0,02, sortie 1,20 USD/M. `cacheWrite=0,20` signifie qu’aucune majoration hypothétique de 25 % n’est ajoutée pour cet essai. Il ne signifie pas que des tokens de cache ont été observés. Le backend conserve son fallback prudent pour les autres configurations ; les profils historiques ne sont pas réécrits. Le journal local n’est jamais un solde fournisseur.

Pour ce premier cas factuel, le profil fige explicitement `reasoningEffort=none`, dont le support Luna a été vérifié dans la documentation par l’opérateur. Les 512 tokens Planner et 1 024 Writer ne doivent pas être consommés par une phase de raisonnement inutile. La date des prix/capacités est rattachée à la vérification du probe du 25 octobre à 19:15:28 UTC. Une tâche complexe future demandera un autre profil mesuré, sans élargissement automatique de celui-ci.

## Préparer sans fournisseur

Depuis la racine du dépôt, utiliser un nouveau chemin pour chaque preuve :

```powershell
& tools/validation-paid/new-luna-campaign-profile.ps1 `
  -OutputPath artifacts/ma-campagne/proposition.json `
  -CorpusPdfPath "$PWD/out/remote-deploy/30-recettes-preferees-des-francais.pdf" `
  -PageNumbers 2,5,6,7 `
  -Question 'Dans la recette du bœuf bourguignon, pour combien de personnes les ingrédients sont-ils prévus et quelle quantité de vin rouge faut-il ?' `
  -ExpectedAnswerDescription '4 personnes ; 1,5 litre de vin rouge ; citation canonique page 5.'

& tools/validation-paid/invoke-local-luna-campaign.ps1 `
  -ProfilePath artifacts/ma-campagne/proposition.json `
  -OutputDirectory artifacts/ma-campagne/dry-run
```

Le profil fige le backend de production déjà compilé, aucun build n’est déclenché. Si ce binaire change, régénérer une proposition dans un nouveau fichier. Les anciennes preuves de rejet restent conservées.

## Vérifier localement le corpus avant toute clé

`Test-OwnedRealCorpus.ps1` a un gate séparé `-ExecuteLocal`. L’opérateur doit coordonner son lancement après stabilisation du binaire ; ce script ne compile pas et n’ouvre aucun magasin de clé payante.

```powershell
& tools/validation-paid/Test-OwnedRealCorpus.ps1 `
  -ProfilePath artifacts/ma-campagne/proposition.json `
  -OutputDirectory artifacts/ma-campagne/preflight-local `
  -PythonExecutable 'CHEMIN_ABSOLU_PYTHON_LOCAL_AVEC_PYPDF' `
  -SearchQuery 'bourguignon' -ExpectedPageNumber 5 `
  -ExpectedTextPatterns @('(?is)1[,.]5\s*l','(?is)4\s*personnes') `
  -ExecuteLocal
```

Il crée un cluster PostgreSQL dédié et un contentRoot neufs, prépare les vraies pages, construit le catalogue via l’endpoint de production, exige un seul document, effectue `/rag/search` en canonical et `/documents/context` sur la page attendue, puis vérifie identités/hashes et texte. Il démarre ensuite la configuration avancée réelle avec **worker désactivé et clé synthétique explicite**, utilise le même constructeur de handoff que le runner payant, exige POST202, annule ce job, conserve son historique et prouve zéro job actif et zéro réservation/appel fournisseur. Les flags d’autorisation du handoff restent false : seule la configuration serveur les accorde. L’arrêt finally ne concerne que les services possédés ; PDF/DB/preuves sont conservés.

La commande `python` ordinaire de la machine d’audit ne contient pas pypdf ; le Python fourni par le runtime Codex le contient. Fournir explicitement ce binaire ou un autre runtime de confiance déjà installé. Aucun script n’installe de dépendance par le réseau.

## Exécution payante ultérieure

Le runner central `tools/test-project.ps1 -Profile LivePaid` accepte le schéma Luna local et conserve le dispatch historique Terra. Il transmet `PaidSecretStorePath` et `PaidPythonExecutable` uniquement à l’exécuteur autorisé. Les wrappers live historiques exigent maintenant les variables d’autorisation LivePaid et un budget/modèle explicites ; ils ne constituent pas une autorisation autonome.

Le nouveau runner démarre un backend de production et un seul worker payant avec un seul job, aucune répétition/retry automatique, au maximum quatre appels logiques. Le client et le petit modèle local ne sont pas démarrés. Le pipeline testé est endpoints→worker→outils canoniques SQL→OpenAI→validation→persistance. Le seed reprend du vrai texte PDF, sans inventer les réponses attendues, mais ne valide pas l’ingestion ni l’OCR. Les services TEI/Qdrant sont des **substituts de protocole explicitement déclarés** : cela ne valide pas un index vectoriel ou son rappel.

La clé est déchiffrée DPAPI seulement après tous les gates Execute/autorisations. Elle reste dans l’environnement du seul processus backend et en mémoire, jamais dans un profil ou une preuve publique. Les réponses et prompts exacts sont sous `runtime/` et restent privés.

Le finally retrouve les jobs du nouvel utilisateur même si la soumission a réussi sans réponse HTTP reçue ; il les annule, exige zéro job actif et attend la stabilité du journal/réservations avant d’arrêter le worker. La terminaison SQL ne prouve pas que le fournisseur distant a arrêté. Toute réservation sans terminal ou charge `reserved_upper_bound` conserve un état `UNCERTAIN_RESERVED`. Aucun retry automatique ne suit une erreur de clé, quota ou contexte.

## Preuves et migration

Le résultat public distingue : cas réellement exécuté, état terminal, source canonique vérifiée, outils réellement appelés, lignes logiques de ledger, tentatives HTTP, modèle observé, somme terminale + réservations non soldées + coûts précédents, cleanup et approbation sémantique toujours séparée. Le runner refuse un succès sans TRX/résultat lorsque l’entrée utilisée est un test live xUnit. Un test ignoré ou une sélection vide n’est jamais une validation.

Les budgets/tarifs/chemin de ledger de production sont désormais explicites ; zéro/vide bloque la dépense. Les montants synthétiques des fixtures Unit restent des données de tests. Ne pas copier une configuration Unit ou un ancien plafond de 25 USD dans une campagne payante.

Tests sans fournisseur : `Test-PaidGuards.ps1`, `Test-LunaDryRun.ps1`, et `test_paid_corpus.py`. Les tests CLI PDF utilisent le Python local explicite avec pypdf ; ils vérifient vrais octets/pages, mauvais hash et page sans texte/OCR manquant. Aucun de ces tests ne fait d’appel modèle, de build ou de démarrage de service.
