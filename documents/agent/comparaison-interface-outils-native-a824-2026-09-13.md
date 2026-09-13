# Comparaison de l'interface d'outils — A824, préenregistrement

## Diagnostic clos avant toute modification

A823, candidat `3f9e8d7d`, job `4b6cbdf9-5144-463b-9324-d3d1f34d7a6e` :
sept appels, 0,3645506 USD. Trente et une recherches exécutées, aucune lecture
de pages et aucune recherche littérale. Aucun retour de fenêtre invalide n'a
été nécessaire. Le Writer conclut insuffisant ; le Critic crée vingt objets
de claims dont onze vides, sans preuves. Le résultat durable est un échec
`advanced_critic_protocol_invalid`, pas une réponse partielle approuvée.

Le dernier Writer voit cinquante-quatre observations, 28 206 caractères de
contenu, avec titres, sommaires, corps propres et fragments voisins. Ses
recherches ciblent des titres observés mais restent toutes `search_corpus`.
La présence d'un titre ne prouve pas le corps. La conclusion d'insuffisance
visible ne démontre pas l'absence dans le corpus. Les identités, les requêtes
réelles et les traces sont conservées ; zéro job possédé non terminal et
ports temporaires libres. Le défaut A821 de fenêtre reste corrigé mécaniquement,
sans preuve live de récupération dans A823.

Cette réapparition arrête les répétitions. Ne pas ajouter une consigne métier
pour les collations ou obliger une recette connue. Le diagnostic A817 montre
que la rédaction peut construire vingt propositions à partir de corps exacts,
mais cette sélection de preuves était externe, pas autonome.

## Hypothèse C1 et différence contrôlée

Comparer l'interface textuelle JSON actuelle avec les fonctions effectivement
déclarées dans `tools` de Chat Completions, pour la recherche pendant la
synthèse Writer/Critic. Le choix reste `auto`. Les trois fonctions sont les
mêmes : recherche sémantique, lecture physique et recherche littérale.
Le Planner et le ResearchReview restent inchangés dans cette comparaison.

Les appels natifs sont traduits sans interprétation sémantique vers le contrat
existant, puis soumis aux mêmes validations de source, portée, arguments et
lot atomique. Le tour suivant conserve le dernier appel assistant et un retour
outil borné, avec état/diagnostic et identités des résultats. Les passages
actuels restent uniquement dans l'EvidenceBundle visible. Ce retour compact
n'est jamais une preuve et ne réintroduit pas des textes devenus invisibles.

Le protocole peut être configuré sur un endpoint OpenAI-compatible capable de
fonctions ; aucune dépendance à la localisation ni règle OpenAI obligatoire
pour le futur serveur client. Mode optionnel désactivé par défaut, activé
explicitement par le pilote. Aucun installateur ajouté maintenant.

Les arguments sont déclarés par JSON Schema, mais le backend reste l'autorité
de validation. Un nom inconnu, un lot malformé, une source non observée ou une
demande interdite n'est jamais exécuté. Le retour A823 reste borné. Tous les
appels API, y compris les appels natifs, sont facturés et tracés avec leur
enveloppe brute et une normalisation explicitement identifiée.

## Mesures, budget et arrêt

Même demande publique 5 × 4, même corpus/runtime, sept appels, critique,
contexte/délais/outils inchangés, une tentative HTTP et 0,75 USD par pilote.
Maximum deux pilotes C1, 1,50 USD cumulés. Registre après A823 :
31,59872690 USD sur 40 USD, 8,40127310 USD restants. Aucun achat.

Mesurer les opérations choisies et réellement exécutées, arguments rejetés,
visibilité des corps retenus, propositions/citations exactes, résultat durable,
coût et durée. Relire les associations ; les fruits nature ne deviennent pas
des recettes. Un changement de protocole n'est pas une preuve de qualité.

Si un pilote passe, figer puis exiger trois réussites consécutives sur le
nouveau candidat avant holdout inédit et WinUI. Si C1 échoue, remesurer la
chaîne et préenregistrer séparément une mémoire de travail centrée sur les
éléments choisis par le modèle ; ne pas mélanger ce changement au pilote C1.
Le produit demeure `TESTE_NON_APPROUVE`.

Schéma vérifié dans la documentation officielle OpenAI :
https://developers.openai.com/api/docs/guides/function-calling ; Terra indique
le support de fonctions dans https://developers.openai.com/api/docs/models/gpt-5.6-terra.
Ces capacités documentées ne garantissent pas l'acceptation du cas SAAIA.

## Implémentation vérifiée, avant premier pilote

Mode `NativeResearchToolsEnabled` optionnel, sans changement du défaut. Le
runner de validation expose `EnableNativeResearchTools` et le scelle. Fonctions
strictes, paramètres tous déclarés et sans propriété supplémentaire ; noms,
identités d'appel uniques, taille de lot et arguments vérifiés avant outils.
Le dernier tour natif est conservé, avec retour compact et au plus huit
identités visibles par résultat ; aucune copie de passage dans l'historique.
Un résultat omis reste explicitement omis, pas absent du corpus. Taille du
tour bornée à 16 384 caractères. Les requêtes n'ajoutent ni budget d'appels ni
limite de recherche supplémentaire ; elles consomment l'enveloppe existante.

Les traces natives distinguent l'enveloppe réelle `tool_calls` et le JSON
normalisé ; aucun contenu fictif n'est présenté comme `message.content` reçu.
La réservation monétaire inclut le payload sérialisé complet avec fonctions
et historique. Un contrôle de panne sans usage prouve cette réservation.

Quatorze nouveaux cas automatisés couvrent les trois opérations, lien appel /
retour, rejet atomique, correction bornée, portée inconnue, paramètres malformés,
fonction indisponible, budget final, lot trop grand, trace brute/normalisée et
comptabilité. Suite backend finale Release : 2 332 réussites, zéro échec,
trois ignorés ; build sans avertissement, runner PowerShell parsé et diff propre.
Une première suite a échoué sur la sélection de deux fichiers par le nouveau
test de traces ; le test cible désormais le rôle exact. Cette capture est
préservée séparément. Aucun appel API depuis la clôture A823.

## Refus de transport A824 et amendement avant deuxième pilote

A824 a été refusé au premier Writer natif : HTTP 400, coût nul pour cet appel.
Ses trois préparations ont coûté 0,1039325 USD. Une sonde synthétique des mêmes
schémas a reproduit le refus sans coût : Terra interdit les fonctions avec
`reasoning_effort=low` sur Chat Completions et demande Responses ou un effort
`none`. Le corps du refus original n'était pas capturé ; le diagnostic précis
vient de la sonde reproduisant ce contrat. Aucun comportement natif évalué.

Conserver `low` : configurer le protocole natif `responses` pour la synthèse.
Planner et Review gardent Chat Completions ; sources/outils/limites identiques.
L'adaptateur transport et usage doit conserver les identités de fonctions,
les sorties de raisonnement nécessaires, les résultats liés et les enveloppes
réelles. Responses est stateless (`store=false`), sans utiliser une conversation
hébergée ; reporter les items de raisonnement avec les retours outils, sous
limite explicite. Le protocole natif reste configurable pour les endpoints
compatibles ; pas de raisonnement désactivé silencieusement ni de fallback.

Faire une sonde synthétique Responses au plus 0,03 USD et un appel avant le
deuxième pilote C1. Le refus A824 compte dans les deux pilotes/plafond de C1,
mais pas comme preuve sémantique. Total clos : 31,70265940 USD, reste
8,29734060 USD sur 40 USD. Aucun nouveau financement.

## Adaptateur Responses vérifié avant A825

La sonde Responses corrigée est acceptée, 0,000838 USD, sans corpus transmis.
Une première conversion de cette sonde avait conservé `temperature` du
transport Qwen synthétique ; refus 400 gratuit conservé. Ce paramètre n'était
pas présent dans le payload OpenAI de production. Les sondes refusées ne
constituent pas une évaluation sémantique et ne consomment pas de crédit.

Configuration `NativeResearchApiProtocol` : défaut `chat-completions`, option
`responses`. Elle ne change que la synthèse lorsque le mode natif est activé.
Les sorties de raisonnement et appels natifs sont reportés avec leurs retours,
dans le dernier tour borné, sans `previous_response_id` ni stockage fournisseur.
Identités documentaires résolues et handles actuels distingués de l'historique.
Les usages Responses input/output/cached sont convertis en métriques communes.
Un résultat incomplete, failed, queued ou in_progress ne peut pas être publié,
même si son texte contient du JSON parsable. Les refus HTTP sont désormais
capturés avec le vrai payload et le diagnostic privé, taille bornée, clé exclue.

Onze nouveaux cas Responses, ajoutés aux quatorze natifs, passent. Suite finale
backend Release : 2 343 réussites, zéro échec, trois ignorés. Build propre et
diff sans erreur. Le prochain pilote A825 reste le deuxième et dernier C1,
sur une nouvelle version figée, mêmes budgets et raisonnement low. Total clos
31,70349740 USD, 8,29650260 USD disponibles ; zéro achat nouveau.

## Clôture de C1 après le pilote A825

Sur `96ced458`, sept appels réussis coûtent 0,3754115 USD. Responses accepte
huit fonctions natives choisies par Writer, toutes des recherches sémantiques.
Le job exécute 26 recherches au total, aucune lecture ou recherche littérale.
Writer s'arrête en insuffisance alors qu'un tour supplémentaire reste autorisé.
Critic invalide puis réparation vers une insuffisance sans citation : statut
durable succeeded, zéro proposition livrée. Le planning reste rejeté ; ce
résultat ne démontre pas une absence de contenu dans le corpus.

Les deux pilotes C1 totalisent 0,479344 USD, plus 0,000838 USD pour la sonde
acceptée. C1 est clos sans troisième pilote. La perte de mémoire n'est pas
établie causalement. C2a est préenregistré séparément dans le document de
comparaison de la boucle native. Ressources possédées fermées, aucun job
possédé non terminal. Total du registre : 32,07890890 USD sur 40 USD.
