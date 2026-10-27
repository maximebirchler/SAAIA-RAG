# Qualification du transport documentaire avec un faux fournisseur

Ce harnais vérifie le chemin **API → PostgreSQL → worker → outils du produit → corps HTTP effectivement reçu par le fournisseur**. Il utilise le runtime WinUI isolé existant, sans ouvrir le client ni toucher au profil habituel. Le faux fournisseur écoute uniquement sur `127.0.0.1`, ne possède aucun client réseau et refuse toute clé API fournisseur.

Il rejoue les publications réelles NIST et recette du corpus source27. Le SHA256 du fichier de capture est figé dans les préparateurs. Les identités de document/révision, textes, métadonnées, ancres et contenu canonique sont conservés. Le tenant est explicitement celui de la campagne source27. Les compteurs de tokens et le petit index de pages sont dérivés pour la fixture ; ce ne sont pas des résultats d'une nouvelle ingestion.

Le faux fournisseur ne fournit aucune preuve documentaire. Il demande une recherche, une fenêtre de texte à offset non nul avec `read_evidence`, une lecture de page volontairement trop étroite (`topK=1`) qui doit être explicitement refusée, une lecture canonique légère qui réussit, une recherche littérale, un inventaire natif puis deux fenêtres de lignes natives. Si la seconde fenêtre est partielle à cause du budget, il suit uniquement son curseur visible pour récupérer le reste des trois lignes prévues avant les essais. L'appel initial `offset=1, limit=2` reste identique ; la reprise utilise le `nextOffset` effectivement reçu. Les pages principales et natives restent 40 pour NIST et 5 pour la recette ; la lecture légère porte explicitement sur 19 et 3. La fenêtre primaire contrôle les offsets ; elle ne prouve pas la récupération d'une fin de texte auparavant omise, les chunks choisis faisant moins de 2400 caractères. Il répond finalement par une citation littérale d'une ligne réellement reçue. Le vérificateur compare ces requêtes reçues aux publications originales, aux sondes `llm.call.request_sha256`, aux captures applicatives exactes, aux événements SQL et à la réponse relue par API. Chat Completions et Responses se lancent dans deux runtimes indépendants.

## Exécution

Construire d'abord le backend Release et le petit importateur, dans le créneau de build prévu. L'importateur utilise le nom d'assembly ami `SAAIA.IngestionGold` et son propre dossier de sortie ; il ne remplace aucun outil existant.

```powershell
dotnet build tools/validation-wire/ImportPublishedCorpus/ImportPublishedCorpus.csproj -c Release

./tools/validation-wire/invoke-wire-qualification.ps1 `
  -OutputDirectory artifacts/provider-wire-qualification-20261027/chat-runtime-v1 `
  -SourceCapturePath artifacts/ingestion-fidelity-fixes-20261026/private/source27-main-actual-v1/evidence/fidelity-corpus-v27/private-observations/sql-publication.private.json `
  -ImporterDll tools/validation-wire/ImportPublishedCorpus/bin/Release/net8.0/SAAIA.IngestionGold.dll `
  -Protocol chat-completions
```

Sans `-Execute`, seule une description est produite. Ajouter `-Execute` pour lancer la qualification. Pour Responses, choisir un nouveau dossier et `-Protocol responses`. Les ports doivent être libres ; leur prévalidation ne constitue pas une réservation.

Les sources et les DLL effectivement utilisées doivent être gelées par l'opérateur de campagne avant exécution. Les captures contiennent des extraits documentaires : les garder dans les artefacts privés ignorés par Git. Les identifiants de fixture ne sont pas des clés de fournisseur. Les scripts de démarrage nettoient l'environnement fournisseur hérité.

Chaque exécution arrête uniquement ses processus vérifiés avec le script existant `validation-winui/stop-environment.ps1`, conserve les preuves et signale séparément une erreur de nettoyage. Aucun changement n'est apporté aux services habituels ou au serveur Linux.

`-ScenarioVariant` sélectionne aussi des contrôles négatifs : `source-hash-forged` (refus avant tout fournisseur), `output-limit` (JSON lisible mais sortie interrompue), `invented-citation` (citation inconnue maintenue pendant la réparation) ou `native-raw-sha-correction` (mauvais pointeur natif refusé avant lecture, puis correction). Le résultat distingue un refus attendu d'une réponse autorisée. Chaque variante exige son propre dossier de preuves.

Les journaux console peuvent employer la page de codes OEM de Windows. Le vérificateur conserve leurs octets et lit uniquement le sous-enregistrement JSON ASCII des sondes `measurements=`, échappé par le sérialiseur du produit. Il ne remplace aucun octet illisible. Les captures applicatives et les corps HTTP sont vérifiés indépendamment en UTF-8 strict.

## Portée du résultat

Une réussite prouve le transport et le protocole dans ces parcours. Elle ne prouve ni la compréhension d'un vrai modèle, ni la fidélité OCR de tous les PDF, ni la validité métier d'un planning. Le statut du produit reste `TESTE_NON_APPROUVE`. Les cas négatifs et les limites (budget, citation inconnue, Unicode, mauvaise source) sont qualifiés séparément par les régressions causales backend/client ; ils ne doivent pas être présentés comme des réponses sémantiques réussies.

La fixture ne propose aucune redirection et les POST capturés arrivent sur le socket loopback attendu. Cette campagne ne qualifie pas une interdiction générale des proxys ou des redirections HTTP dans la configuration de production.
