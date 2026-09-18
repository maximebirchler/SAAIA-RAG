# A860 — Explorer pour les collections nommées plates

## Verdict du palier

L'inventaire et le Candidate Explorer ne sont plus limités aux grilles avec
colonnes. Ils couvrent maintenant aussi les collections bornées de plusieurs
candidats nommés, telles que le scout A855 demandant sept éléments d'un même
type.

Cette extension utilise le même contrat, le même checkpoint et les mêmes
preuves canoniques. Elle ne spécialise aucun domaine et n'ajoute aucun rôle
artificiel. Le produit reste `TESTE_NON_APPROUVE`.

## Admission

Une collection plate est éligible lorsque :

- elle demande plus d'une unité ;
- `atomicEvidenceMode` contient `named_item` ;
- la politique de sélection est `explicit_set` ou `open_set`.

Les demandes directes à une unité, les faits de contenu et les chemins sans
outils natifs gardent leur fonctionnement existant.

## Couverture sans fausse colonne

Une liste plate n'a pas de coordonnées de colonne. Ses candidats conservent donc
des tableaux `targetRoles` et `selectedRoles` vides. Le schéma d'outil autorise
explicitement ces tableaux vides et interdit d'y injecter une valeur qui
n'existe pas dans la demande.

La couverture est globale : le dossier devient mécaniquement complet lorsque le
nombre de candidats distincts en `body_verified` atteint `answerUnitCount`.
L'Explorer garde le jugement sémantique. Pour une liste plate,
`body_verified` signifie qu'il considère le candidat adapté à la demande entière
et qu'un corps canonique visible le documente ; un élément nommé mais inadapté
doit être `rejected`. Le code vérifie le compte, le titre, la source et le corps,
sans décider de l'adéquation.

Cette distinction est celle qui manquait au scout A855 : trois corps vérifiés
sur sept ne constituent pas un dossier prêt. Tant qu'une recherche reste
autorisée, l'Explorer doit continuer. À la borne réelle, le Writer reçoit un
`bounded_gap` avec le compte global au lieu d'une prétendue absence exhaustive.

## Contrôles

Un scénario synthétique de collection plate demande deux candidats distincts,
enregistre deux titres et deux corps avec des rôles vides, remet un dossier
`ready` au Writer et obtient deux claims sourcés. Le dossier expose deux corps
vérifiés, une cible globale de deux et aucune couverture de rôle.

Le banc Candidate Explorer compte sept réussites ciblées. La suite provider
complète compte 298 réussites, zéro échec. La suite backend complète, exécutée
avec PostgreSQL local actif sur l'état exact avant commit, compte 2 457
réussites, zéro échec et trois tests live ignorés en 13 min 53 s. Aucun appel
fournisseur n'est exécuté.

## Limites et suite

Le test valide le protocole plat, pas l'autonomie sémantique de Terra sur les
sept petits-déjeuners A855. La trace historique démontre trois corps trouvés et
une fin prématurée ; un futur pilote live devra montrer que le nouveau handoff
de couverture force effectivement la poursuite vers les quatre corps manquants
ou une borne opérationnelle exacte.

Le prochain pilote payant reste celui du planning 5 × 4, car il exerce à la fois
la couverture par rôle et le total de vingt candidats. Le chemin plat sert de
contrôle causal plus petit si le planning échoue encore : il permet de séparer
la découverte d'une seule famille de candidats de leur affectation dans une
grille.
