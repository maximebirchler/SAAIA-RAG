# Reprise bornée des essais Luna — plan sans exécution

Vérifications de préparation effectuées en lecture seule : HEAD `842ab118a2ccacad038577dd5df5e0dc5ee6b90a`, arbre Git propre ; backend Release `90AF887987448FE9B6ED5D166236D2CA00A051E9FCB7D1D833A90C1AD7E90469`, identique au hash de la proposition v3. Le PDF conserve le hash attendu. Ports 5157, 5158 et 18107 observés libres à cet instant ; le runner les revérifie au lancement. Aucun secret, presse-papiers, réseau, build ou processus produit n’a été ouvert/lancé par l’agent préparateur.

Les anciens rapports et propositions restent inchangés. Tout résultat nouveau va dans cette nouvelle racine, avec un dossier neuf par exécution. Le responsable renouvelle et protège lui-même la clé ; aucun secret ne doit apparaître ici dans un fichier public.

**1. Vérification d’accès au modèle sans génération.** Le script opérationnel `test-model-availability-get.ps1` est préparé mais n’a pas été exécuté. Il fait exclusivement un GET vers l’URL fixe `https://api.openai.com/v1/models/gpt-5.6-luna`, une seule tentative logique, aucune redirection/retry, zéro token de génération et aucun corpus. Sans `-Execute`, il ne lit pas le magasin DPAPI. Seuls statut/code connu/request-id/id modèle confirmé sont projetés ; corps brut et clé restent en mémoire et ne sont jamais publiés.

Commande réservée au responsable, après définition du chemin de son magasin DPAPI autorisé :

```powershell
& artifacts/luna-live-20261025/test-model-availability-get.ps1 `
  -OutputPath "$PWD/artifacts/luna-live-20261025/availability-model-get-v1.json" `
  -Execute -SecretStorePath $approvedDpapiStorePath
```

Un HTTP200 confirme l’accès catalogue au modèle ; il ne confirme ni crédit, ni quota ou droit de génération. Un HTTP403 peut venir du droit `ModelsRead` absent sur une clé restreinte, sans que la clé soit expirée. Une erreur est conservée et examinée sans nouvelle tentative automatique. Ce GET ne génère pas de tokens : le coût de génération précédent reste zéro. Le vrai job révèle ensuite la disponibilité effective du quota de génération.

**2. Nouveau profil explicitement autorisé.** La proposition v3 est encore `PROPOSED`. Son code et ses hashes sont compatibles, mais son statut interdit l’exécution. Le responsable doit produire un nouveau fichier dans cette racine, conservant modèle exact Luna, `reasoningEffort=none`, prix datés, quatre appels logiques au maximum, un job, une tentative HTTP/job, Planner512/Writer1024, pages2/5/6/7 et les hashes. Le nouveau fichier doit porter `AUTHORIZED`, une date UTC et la base de l’autorisation utilisateur reçue. Aucun ancien profil ne doit être réécrit. `authorizedUsd=hardStopUsd=maximumCostPerJobUsd=0.50`, `priorCampaignCostUsd=0`, tant qu’aucune génération payante n’a eu lieu. Si une autre génération consomme C avant ce job, son coût ou sa réserve incertaine doit être intégré à `priorCampaignCostUsd` et le plafond du job réduit à `0.50-C`.

**3. Un seul vrai cas, sous le plafond global de 0,50 USD.**

```powershell
& tools/validation-paid/invoke-local-luna-campaign.ps1 `
  -ProfilePath artifacts/luna-live-20261025/luna-real-corpus-authorized-v1.json `
  -OutputDirectory artifacts/luna-live-20261025/real-case-01 `
  -Execute -AuthorizePaid -AuthorizeCorpusTransmission -PaidCapUsd 0.50 `
  -SecretStorePath $approvedDpapiStorePath `
  -PythonExecutable 'C:/Users/maxim/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe'
```

La commande est donnée pour préparation ; elle n’a pas été lancée. La variable de magasin DPAPI doit être définie par le responsable, sans afficher son contenu. L’interpréteur fourni existe ; pypdf a été validé dans les tests précédents et est revérifié avant accès au secret. Le runner refuse un dossier de résultat existant, un binaire/PDF différent, un profil non autorisé ou une limite trop grande, avant le travail payé.

Question figée : quantité de vin rouge et nombre de personnes dans la recette du bœuf bourguignon. Acceptation connue avant essai : **1,5 litre et 4 personnes, page5**, avec identité canonique doc/révision/hash. Le préflight réel précédent a déjà prouvé catalogue, recherche/lecture, admission202 puis annulation avec worker désactivé ; il n’a pas exécuté le modèle. Ce premier essai payant doit donc encore prouver génération, validation et persistance effectives.

**Limites et points d’arrêt.** Le cluster PostgreSQL, le contentRoot et les outils canoniques sont réels et neufs ; le texte provient du vrai PDF. L’ingestion/OCR et le rappel vectoriel ne sont pas validés : TEI/Qdrant restent des substituts de protocole déclarés. Aucun client ni Qwen n’est lancé et le profil habituel reste intact. Un succès ne valide ni le planning complet ni le routage local→serveur côté WinUI.

Après le job, lire la preuve publique et les payloads privés avant tout nouvel essai. Arrêter sur erreur de clé/quota, mauvais modèle observé, source fausse, échec de capture stricte ou résultat `UNCERTAIN_RESERVED`. Une annulation SQL ne garantit pas l’arrêt distant : les réservations non réglées restent comptées, et le runner attend la stabilité puis arrête uniquement ses processus. Aucun élargissement de limites, retry, nouvel achat ou passage à Terra/RunPod n’est automatique.

Le préparateur ne lance aucun probe ou test payant ; seul le responsable exécute ces étapes et contrôle les secrets.
