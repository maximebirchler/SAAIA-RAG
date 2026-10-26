# Index final des preuves publiques — clôture ingestion/OCR

Le code a été validé et poussé dans `12202cb` (141 chemins explicitement sélectionnés). Le présent dossier prépare le commit séparé de preuves. Son identifiant sera ajouté au compte rendu après sa création : il ne peut pas être inséré dans son propre contenu à l’avance. Le staging est déjà autorisé par l’utilisateur ; la sélection relève de notre revue interne, sans nouvelle demande de permission.

## Lecture minimale

1. `RAPPORT-INGESTION.md` et `PLAN-ACTION.md` : résultat final, causes et corrections, limites restantes, statut TESTE_NON_APPROUVE.
2. `tests/unit-all-final-v3/manifest.json` et `tests/postgres-all-final-v3/tests/manifest.json` : **5 255 PASS + 1 skip** et **310 PASS + 2 skips**, zéro échec et sources stables. Le futur export produit leurs TRX publiques explicites, avec empreintes originales.
3. `worker-full-e5-lifecycle-final-v2` : vrai worker, douze PDF, 90 faits, 37 points ; SQL/canonique/Qdrant/recherche/lecture et cycle de vie. Annulation **NON_EXECUTED**, car le job a terminé avant son effet : ce résultat n’est pas transformé en annulation validée.
4. `worker-full-e5-lifecycle-final-v1` et `final-source-delta-v2-v3-review.json` : vraie annulation antérieure, ACK explicite et publication conservée ; réutilisation bornée sous comparaison des chemins critiques inchangés. L’arrêt du calcul du moteur amont reste non vérifié.
5. `worker-recipe-e5-final-v3` : livre réel de 36 pages, **16 faits et 160 chunks/points PASS** sur la DLL finale. Les deux attentes de titre sont des régressions ajoutées après découverte du défaut, pas une évaluation aveugle. Aucune recherche dédiée à chacun de ces deux titres n’est revendiquée.
6. `canonical-block-coverage-public-summary-final-v1.json` : seulement comptages, classifications et hashes de l’audit de couverture historique ; aucun texte ou titre du PDF. Les deux titres manquants du run à 158 chunks restent visibles comme défauts historiques, avec renvoi à leur correction finale.
7. `source-index-anchor-final-v2.json` entier et `code-staging-acceptance-final-v1.json` : 1 705 codes/tests et manifeste critique du corpus exacts, aucune source changée après le run. Le **strict global reste false** pour 31 configurations historiques EOL_ONLY_PROVEN ; aucune égalité exacte ni recompilation n’est inventée pour ces différences.
8. `dependencies-v6-cleanup-public-v1.json`, `dependencies-v7-cleanup-public-v1.json`, terminaisons des workers et contre-revues : services et PG possédés arrêtés, zéro job actif et ports libérés selon leurs preuves.
9. Résolutions OCR/canonique, bail/plan/vecteurs, titres, tableaux/layout, consommateurs monotones et harnais ; RED/GREEN causaux conservés avec leurs skips et limites.
10. Preuves synthétiques du vérificateur d’index (**17 PASS**) et de l’exporteur (**12 PASS**), puis `RAPPORT-CLOTURE-TELEGRAM.md` et son accusé de livraison borné.

## Bornes de l’export

La proposition finale hash chaque rapport et chaque campagne terminée. Les captures stoppées en compilation sans compteurs/TRX utilisables restent exclues des campagnes comportementales. Les enveloppes de processus ne sont pas confondues avec les campagnes de tests qu’elles référencent. Les run historiques et réévaluations hors ligne gardent leur statut.

L’exporteur reconstruit un XML explicite ; les arguments des noms de tests sont retirés, leur SHA original est conservé et les compteurs/IDs restent vérifiés. Son propre fichier source n’est pas copié par ce mécanisme : sa déclaration de regex déclenche conservativement son scan. Son empreinte exacte et ses tests restent référencés, sans contourner le scanner.

Ne sont jamais sélectionnés : `private-observations`, snapshots/bundles ou textes PDF bruts, titres/extraits de l’audit de blocs, PDF/images/poids, `local-fixture`, runtime, connexions, fichiers `.env`, clés et stores, bases ou répertoires de données, `dependencies.json` brut et manifestes privés, logs bruts, TRX originales avec sorties capturées. Les résumés publics conservent seulement les informations nécessaires et les hashes de provenance.

Le présent agent ne réalise aucun staging, export réel ou mutation Git. Le parent effectuera l’export dans une racine neuve, puis vérifiera les octets sélectionnés/stagés. Le seul identifiant restant à renseigner est celui du futur commit de preuves ; aucune validation non exécutée n’est proposée comme tâche de clôture encore attendue.
