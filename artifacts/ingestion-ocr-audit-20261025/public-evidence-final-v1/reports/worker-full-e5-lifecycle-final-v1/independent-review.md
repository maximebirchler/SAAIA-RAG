# Contre-revue du jalon réel 00A011D0

Cette note porte sur le binaire historique `00A011D0DB86BFDAEF82972DDE6B1C7CD0B142C79D43C871C1128E4CF107ABD7`, les captures déjà terminées et leur cohérence. Aucun service, API, build ou Git n'a été exécuté pour cette relecture. Les textes privés sont comparés en mémoire et ne sont pas reproduits dans cette note.

## Même binaire dans les preuves

| Preuve | Résultat observé | Identité |
|---|---|---|
| `tests/unit-all-final-v2/manifest.json` | 5 247 PASS, 1 non exécuté, aucun échec ; sources stables pendant le run | SHA backend 00A011D0…107ABD7 |
| `tests/postgres-all-final-v2/tests/manifest.json` | 310 PASS, 2 non exécutés historiques, aucun échec ; sources stables | même SHA |
| `worker-full-e5-lifecycle-final-v1` | publication réelle des douze documents et cycle de vie réussis | même SHA dans plan, environnement et douze manifestes embarqués |
| `worker-recipe-e5-final-v2` | livre réel de 36 pages, 14 faits annotés, recherche→lecture réussie | même SHA dans plan et bundle |

Le PostgreSQL isolé est marqué arrêté et supprimé dans son manifeste parent : `existingClusterModified=false`, `environmentRestored=true`, `clusterRemoved=true`, `remainingListener=false`.

## Publication du corpus synthétique

Douze jobs API réels, seize pages, quatre-vingt-dix faits physiques, trente-sept chunks, trente-sept ancrages et trente-sept points Qdrant. Les douze évaluations canoniques, les faits des seize captures de contexte, les trente-sept checksums et les trente-sept UUID de points ont été recalculés sans écart. Les points sont dérivés du chunk canonique et du plan complet ; ils ne remplacent pas les identités canoniques des citations.

Les identités de publication, les contrôles positifs/négatifs et recherche→lecture sont acceptés sur ce profil. L'état initial vide et l'absence de seeding sont établis par les preuves distinctes du runner ; un simple snapshot de publication ne prouve pas à lui seul qu'un worker a réellement tourné.

## Cycle de vie réel contrôlé séparément

Le PDF synthétique `fr-scan-critical-v2` garde le même hash source. Les snapshots SQL privés, les réponses API et les points attendus confirment :

| Étape | Version d'ingestion | Version publiée | État / effet |
|---|---:|---:|---|
| Départ | 1 | 1 | publication active |
| Upsert des mêmes octets | 2 | 2 | nouvelle révision, nouveau plan et nouveau point |
| Suppression API | 3 | 0 | état deleted, plan nul, recherche vide |
| Recréation / upsert | 4 | 4 | nouvelle révision, nouveau plan et nouveau point |
| Annulation du job suivant | 5 | 4 | génération 5 abandonnée ; révision, plan et point de 4 conservés exactement |

La capture avant annulation porte le job exact en état `running`, phase `document_intelligence`. L'API acquitte avec un **booléen true**, puis le job devient `canceled`. Le défaut de faux PASS sans acquittement ne s'applique donc pas à cette preuve réelle.

Quatre snapshots de publication ont été contrôlés indépendamment : quatre checksums de chunk, quatre calculs d'UUID de point, quatre recherches retournant la révision publiée attendue. La suppression est contrôlée séparément. Aucun hit de génération obsolète n'apparaît dans ces réponses bornées, et aucun retriever dégradé n'est signalé. Les snapshots terminaux comportent zéro job actif.

Cette séquence ne contient pas une nouvelle réingestion après l'annulation V5 : elle prouve la génération consommée et la conservation de V4 à cet instant, complétée par les contrats PostgreSQL de monotonie. `upstreamEngineCancellationVerified=false` reste juste : observer l'annulation du worker n'atteste pas que l'inférence Docling distante a été interrompue.

## Livre réel : réussite factuelle et défaut supplémentaire découvert

La campagne du livre publie **158 chunks et 158 points**, et non le chiffre 261 d'un lancement antérieur. Les 36 pages, les quatorze faits indépendants, les 158 checksums et les 158 UUID de points ont été recalculés sans écart. Les captures API des trois pages portant les faits annotés satisfont ces attentes. Le manifeste du PDF original est lié au hash enregistré dans le plan.

Une vérification de couverture au-delà de ces quatorze faits a cependant trouvé deux observations `section_header`, pages physiques 2 et 4, absentes des chunks : le titre intermédiaire remplacé avant contenu et le titre terminal suivi seulement d'observations physiques récupérées. Elles restent dans le bundle canonique et le SectionTree, mais la recherche et les lectures fondées sur les chunks ne disposent pas de leur texte complet. Un texte présent dans le bundle n'est pas automatiquement un texte récupérable par le RAG.

Cette limite a déclenché la reproduction synthétique `tests/uncovered-heading-red-v1` : six échecs causaux et deux témoins verts. Le correctif de rétention des titres observés, sans section inventée et avec un signal persistant, appartient à un jalon postérieur au SHA de cette note. Il demande ses propres captures vertes et nouvelles preuves ; les anciens PASS ne sont pas réattribués à ce futur binaire.

Les vingt-cinq pieds de page non indexés du livre sont des compteurs. Une occurrence alternative supplémentaire non référencée possède déjà son texte dans un chunk de la même page. Ces cas ne sont pas assimilés aux deux titres effectivement non couverts.

## Nettoyage et limites conservées

Les deux workers sont marqués arrêtés par leurs preuves d'exécution, avec zéro job actif ; leurs environnements possédés sont `stopped`. Le champ `externalDependenciesStopped=false` est exact au terme de chacun de ces runs, puisque les dépendances ont été réutilisées.

Leur arrêt ultérieur est enregistré dans **`dependencies-v6/dependency-stop-814cb2e6bb034229bdeef599177b0efc.json`** : `processesStopped`, `listenersReleased`, `wrapperLogsFlushed` sont vrais, avec date d'achèvement. Le manifeste des dépendances est `stopped`. Le fichier distinct `stop-executed-final-v1.json` est un instantané préalable dont les trois indicateurs sont encore faux ; il ne doit pas servir de preuve d'arrêt accompli.

Les limites du profil demeurent : ancien OCR externe et binaire TEI non qualifiés ; adaptateur E5 Transformers CPU distinct du binaire TEI ; Docling Windows CPU distinct de l'image Docker épinglée ; aucun GC des générations vectorielles anciennes ; overfetch borné et diagnostic explicite en cas de fenêtre incomplète ; aucun rappel sémantique global ou raisonnement de planning de repas démontré. Les deux cellules vides et les quatre images de texte du corpus synthétique ont été examinées dans la contre-revue du run v2 précédent ; cette observation ne vaut pas qualification générale des tableaux, figures ou PDF arbitraires.

Le statut global reste `TESTE_NON_APPROUVE`. Aucun appel LLM payant n'a été nécessaire pour ce jalon d'ingestion.
