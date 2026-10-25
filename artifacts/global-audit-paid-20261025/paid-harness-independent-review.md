# Revue indépendante du harnais économique local

Observation au 25 octobre 2026, 19:41 UTC. Revue du code et des preuves déjà produites, sans exécution de script, build, test, requête HTTP, lecture de clé ni démarrage de service par ce relecteur. Aucune source modifiée par ce relecteur pendant cette revue.

**Conclusion : aucun blocage matériel supplémentaire trouvé dans le parcours relu.** Un défaut réel du payload a été signalé puis corrigé par l'agent propriétaire ; son admission réelle sans worker est maintenant prouvée. Cela autorise à examiner le premier essai économique lorsque les autres gates du responsable sont satisfaits. Cela ne valide ni la réponse d'un modèle payant ni le planning de repas, et ne change pas `TESTE_NON_APPROUVE`.

## Défaut trouvé, correction et contrôle causal

Le payload initial plaçait les deux droits de transmission externe à `true` côté client. `AdvancedAnalysisEndpoints.cs:460` rejette explicitement ce cas avec `external_provider_authorization_must_be_server_controlled`. Le job aurait donc été refusé avant toute évaluation du modèle : ce n'était pas une limite de capacité du modèle.

Le chemin effectif `invoke-local-luna-campaign.ps1:79` utilise désormais `New-LunaCampaignHandoff` (`PaidCampaign.Common.ps1:118`), qui place les deux droits à `false`, et indique `server_controlled`. Les droits sont dérivés de la configuration serveur revue (`PaidRuntime.ps1:69`). Le test offline vérifie ce builder effectif, pas une copie indépendante du payload.

La preuve `real-corpus-admission-preflight-v2/corpus-preflight.json`, produite par l'opérateur parent et relue ici, référence exactement le profil v3 :

- HTTP 202, état initial `queued`, puis annulation vérifiée `canceled` ;
- worker désactivé, clé synthétique, zéro début de réservation et zéro ligne terminale fournisseur ;
- un job d'admission conservé, zéro job actif, environnement proprement arrêté ;
- catalogue réel à un document, deux résultats de recherche canonique, page physique attendue retrouvée, identité document/révision/hash exacte et deux motifs textuels présents.

La conclusion « aucun appel fournisseur » repose sur le worker désactivé et les journaux de réservation ; aucune capture réseau n'est revendiquée. L'ancien texte console « zero provider calls/jobs » a été signalé au propriétaire : il doit parler de zéro appel et d'un job annulé conservé. Le JSON de preuve donne déjà le bon nombre.

## Contrôles du chemin effectif

| Frontière | Résultat de la lecture |
|---|---|
| Autorisation avant clé | Validation du profil, SHA du backend, `Execute`, autorisation de dépense/transmission et cap explicite précèdent `ProtectedData.Unprotect` (`invoke-local-luna-campaign.ps1:21,58`). Le chemin sans `Execute` retourne avant le chargement du runtime. |
| Profil borné | Modèle exact `gpt-5.6-luna`, URL officielle exacte, `reasoningEffort=none`, plafond de campagne au plus 0,50 USD incluant le coût antérieur, un job et une tentative HTTP/job, bornes de tokens/appels/temps. Le profil v3 reste `PROPOSED`, ce qui est refusé à l'exécution. |
| Consommation du profil | Le runtime transmet réellement le modèle, l'URL et `ReasoningEffort` à la configuration du backend. Le provider sérialise ce dernier dans `reasoning_effort` sur son chemin OpenAI. Les prix datés sont copiés dans le guard ; cette revue ne revalide pas les tarifs sur Internet. |
| Ownership SQL | Avant SQL : chemin du manifeste, chemin `data_directory`, PID, heure de création, exécutable, fichier `postmaster.pid`, port et listeners loopback doivent concorder. Une requête réelle `current_setting('data_directory')` confirme ensuite le cluster visé. `PGPASSWORD` est restauré en `finally`. Aucune base habituelle n'est ciblée. |
| Source documentaire | Le vrai PDF est relu et comparé au SHA gelé ; les pages physiques viennent de `pypdf`. Les UUID de document/révision/unités/chunks et les checksums sont cohérents dans une transaction SQL. Le nombre de pages est celui du PDF d'origine. Les octets du PDF et le texte extrait sont conservés sans écrasement des preuves. |
| Réservations financières | `AdvancedAnalysisBudgetLedger` sérialise l'admission entre processus avec `FileShare.None`, relit l'historique sous verrou et flush le début de réservation avant HTTP. Toute réservation sans terminal reste comptée après panne. Le guard réserve le payload UTF-8 entier plus marge, puis ajoute la charge durable avant de libérer son état mémoire. Corruption et double clôture sont refusées. |
| Projection financière | `PaidLedger.ps1` conserve les réservations non soldées et les charges de borne supérieure en `UNCERTAIN_RESERVED`. Elle vérifie identités de modèle et politique de tentatives. C'est une estimation locale, sans prétention de connaître le solde ou la facture du fournisseur. |
| Sortie et panne | Le `finally` retrouve les jobs du nouvel utilisateur même si l'ACK de soumission est perdu, annule ceux encore actifs, vérifie zéro job actif et attend une stabilité du ledger/réservations. Il arrête ensuite les processus exacts via le stopper qui recontrôle identité, parent/enfant, ports et cluster avant action ; les données sont conservées. Une erreur ou une réservation ambiguë empêche le succès public. Aucune relance payante automatique. |

## Limites à conserver dans les conclusions

Le corpus est un vrai PDF avec recherche et lecture SQL canoniques, mais ses pages sont seedées : ingestion, OCR, rappel vectoriel et parcours client local ne sont pas testés ici. TEI et Qdrant sont des substituts de protocole loopback. Les quatre pages sélectionnées constituent le périmètre documentaire du cas factuel ; elles ne prouvent pas une navigation dans le livre entier.

L'état de succès du runner signifie `completed_requires_semantic_review`. Une identité canonique de citation et un outcome `answered` ne suffisent pas à valider le contenu : l'opérateur doit encore comparer la réponse, les assertions et les pages au critère gelé. Aucun résultat sémantique payant n'a été créé ou évalué par cette revue.

La preuve d'admission relue est le résultat du parent. Les contrôles offline annoncés par le propriétaire ne sont pas présentés comme des tests réexécutés par ce relecteur.

## Empreintes relues

Empreintes capturées en lecture seule à 19:41 UTC ; un éventuel changement ultérieur est hors de cet instantané.

| Fichier | SHA-256 |
|---|---|
| `PaidCampaign.Common.ps1` | `4A42C04FD77B405D08A54705B48BD0A7B8688A741214041DA9E594A3DD18F0A3` |
| `invoke-local-luna-campaign.ps1` | `2D5D1793DB2A24377B39F442293AB874CBBE88F44584F9B1B1F07FEE2D82313A` |
| `PaidRuntime.ps1` | `C5C186B2A29E866FD6DD347EF0DC6A4C30C1CDEB95A609F1CD2E75BECB7D2943` |
| `PaidLedger.ps1` | `8C84C370F50CB307F1C308AB1A096329B1F98558D521F320332024ACC772D10D` |
| `prepare_real_corpus.py` | `02FB390EA72ED5818A77D824869B1A60EFC287E639DEC970E72B1AC6E3D1D899` |
| `Test-OwnedRealCorpus.ps1` | `4640ABCFC1805B02FCDC093C9827823AF5F9D44039CD7D5C8CA67D55BE2EE787` |
| `Test-PaidGuards.ps1` | `4C70ADDA91A2D485DD68FD9DC3D34D3044799E1D80DB98B4BB101505A6C28BBB` |
| `../validation-winui/stop-environment.ps1` | `C753E049C22633B5512075C7A839922D7F10BAD80CE0EB3306A1765227990BF1` |
| `AdvancedAnalysisBudgetLedger.cs` | `C29BBC4EC5690D824E0F42D56D9F320E9C5F5676B9FC9C8E10527560E1EB7C82` |
| `AdvancedAnalysisExternalBudgetGuard.cs` | `4D56100EC6BE4A06FCD8AFFCB500BA4316C444CCB23E78CF55A4E4C09C993EA2` |
| `real-corpus-admission-preflight-v2/corpus-preflight.json` | `457DEF860159B6EBE77704DF2A2609EA6B7E6DDDA67EA2658593245A9B97B23A` |
| `luna-real-corpus-proposed-v3.json` | `01509F5DF34A0ECB8D233216040BAEBFB8F941491694066FA1D1AD7854FA0E29` |
