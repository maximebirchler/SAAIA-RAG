# Transmission du contexte physique au modèle

Correction issue de la contre-revue de l'essai réel 02, le 25 octobre 2026. Ce document ne modifie pas les traces historiques de ce qui a été envoyé pendant les essais. Aucun nouvel appel fournisseur n'a été effectué par l'auteur de cette correction.

## Défaut établi

Le contexte physique conservé dans l'index avait un type et une composition indiquant une association de section non affirmée. Ces champs étaient déjà persistés dans `retrieval_chunks.metadata`, mais le résolveur d'évidence et son contrat ne les transportaient pas. Les prompts recevaient donc le texte marginal sans la prudence structurale correspondante : `sourceStructure` était nul pour le fragment concerné dans l'essai 02.

La réponse de cet essai était correcte et citait une autre preuve contenant explicitement les deux valeurs demandées. Le défaut restait matériel pour les demandes suivantes : une co-localisation sur une page ne suffit pas à garantir qu'une valeur marginale qualifie un sujet ou titre voisin.

## Correctif

Le contrat interne de structure conserve désormais le type et la composition du chunk canonique. Le résolveur les relit pour les références de chunk, les sources canoniques de cartes, les ancres et les fenêtres de pages. Une fenêtre agrégée contenant un contexte physique non attribué conserve ce signal sans attribuer à tout l'agrégat le type ou la composition d'un unique chunk. Aucun identifiant de document, tenant, révision, source ou citation n'est changé.

Les prompts exposent des métadonnées structurales séparées du corps : type et composition bornés, portée de page ou fenêtre physique, et `sectionAssociation: not_asserted`. Les codes trop longs ou de forme invalide sont omis, avec signal explicite d'omission ; ils ne sont pas tronqués en codes pouvant changer leur sens. Les identifiants internes de voisinage restent remplacés par des booléens de présence.

La directive documentaire commune au contrôleur, au Writer et aux modes qui utilisent les mêmes preuves explique cette portée : seule la co-localisation physique est établie. Elle ne fournit ni heading, ni identité, ni relation sémantique. Si l'association est nécessaire et ambiguë, le modèle doit lire d'autres éléments canoniques avec les outils disponibles, dans le budget existant.

Les promotions de première ligne exécutées après `read_source` et `find_source_text` respectent ce signal : un contexte physique non attribué ne devient pas un titre exact par simple copie de sa première ligne. Le comportement ordinaire d'un heading observé est conservé.

## Preuves causales et état

Avant correction, root a capturé les tests suivants :

- `tests/physical-metadata-unit-red/manifest.json` : six cas, cinq échecs causaux et un témoin ordinaire PASS. Les échecs montrent les métadonnées absentes dans le payload et les deux fausses promotions de première ligne.
- `tests/physical-metadata-pg-red/manifest.json` : deux cas PostgreSQL isolés en échec, le résolveur retournant une structure nulle malgré les métadonnées physiques présentes en base.

Les tests de payload contrôlent également la portée inconnue, les bornes de composition, l'absence de contenu ajouté et de heading fabriqué ainsi que l'absence d'identifiant privé de voisinage. Les tests PostgreSQL maintiennent la correspondance d'identité et le refus d'un tenant étranger. Les assertions du prompt système vérifient la consigne de co-localisation, pas seulement les valeurs JSON.

La correction et les tests sont gelés pour les exécutions GREEN orchestrées par root. Ce document ne présente pas encore ces exécutions comme réussies. La suite complète, les autres routes de résolution déjà existantes et le prochain payload réel devront être rapprochés de leurs résultats.

Fichiers produit concernés : `AdvancedAnalysisProvider.cs`, `AdvancedAnalysisEvidenceResolver.cs`, `AdvancedAnalysisToolGateway.cs` et `OpenAiCompatibleAdvancedAnalysisProvider.PromptsAndEvidence.cs`. Deux fichiers de tests nouveaux, suffixés `PhysicalContextMetadata.cs`, portent les cas ciblés. Aucun changement de schéma SQL ni extension des budgets d'appels n'est introduit.

## Limites

Ces marqueurs rendent une incertitude visible ; ils ne prouvent pas automatiquement une association sémantique ni la qualité d'une réponse future. Une ancienne preuve sans ces métadonnées ne doit pas recevoir de contexte physique inventé. Les essais historiques 02 et 03 ne sont pas réécrits par ce correctif.

L'écart de retours à la ligne du transport SQL d'essai et son contrôle de checksum sont traités séparément. Ce correctif de métadonnées ne prétend pas les résoudre, ni valider le planning de repas.
