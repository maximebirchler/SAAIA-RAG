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
