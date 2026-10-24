# Banc historique et validation du produit

La commande de validation courante est décrite dans [VALIDATION.md](VALIDATION.md). Aucun ancien profil « final » n'est sélectionné automatiquement : `config/validation-profiles.json` déclare les profils disponibles et ne désigne actuellement aucune campagne payante active.

`run-llm-validation.ps1` est conservé pour reproduire et comparer les anciennes campagnes. Ses modes ont des garanties différentes :

| Mode | Ce qui est exécuté | Limite de la preuve |
|---|---|---|
| `plan` | Sélection d'une banque, préparation des questions et fichiers | Aucun backend ni modèle interrogé. |
| `retrieval` | Recherche HTTP réelle avec le profil de retrieval déclaré | Ne valide ni raisonnement du modèle, ni parcours UI. |
| `llm` | Comparateur historique, récupération et modèle compatible OpenAI sur loopback | Des branches déterministes de réponse et faits ajoutés sont actifs. Ce n'est pas l'oracle d'acceptation du produit actuel. |
| `agent` | Adaptateur vers le test du vrai `RagChatAgent` | Exige le profil de campagne explicite et ses prérequis. Une banque réussie ne constitue pas une approbation sémantique automatique. |

Le mode `llm` conserve notamment ses anciens calculs de quantités, exclusions et réponses `deterministic_*`. Ce code est encore appelé ; il n'a pas été supprimé comme « mort ». Les décisions sémantiques du produit actuel ne doivent pas être évaluées à partir de ces réponses du comparateur.

Préparation sans aucun appel :

```powershell
pwsh -NoProfile -File tools/run-llm-validation.ps1 -Mode plan -Limit 10
```

Les sorties du comparateur identifient `validationEngine`, le commit, l'empreinte de la banque et `currentProductAcceptanceEvidence=false`. Les rapports distinguent les recherches supplémentaires de diagnostic (`queryExpansionUsed`, `retrievalQueryCount`) du profil de retrieval demandé. Les drapeaux de qualité sont des aides à la revue ; ils ne constituent pas une note de qualité certaine.

Les anciens fichiers `config/openai-*.json` restent des preuves immuables de campagnes. Pour en reproduire une, passer explicitement `-ProfilePath` au vérificateur approprié, ou `-CampaignProfilePath` au lanceur commun. Une préparation de profil, un compteur de tests vert ou un ancien nom « final » n'autorise pas une nouvelle dépense et n'approuve pas le produit.

Le profil courant `Unit` désactive les drapeaux live hérités et n'utilise pas de clé API. Le profil `LivePaid` passe par le runner gardé, avec autorisation, plafond et transmission explicites. Les endpoints dont la gratuité ne peut pas être prouvée sont classés conservativement dans ce dernier profil ; ce classement concerne le banc de tests, pas la licence ou la topologie commerciale du produit.
