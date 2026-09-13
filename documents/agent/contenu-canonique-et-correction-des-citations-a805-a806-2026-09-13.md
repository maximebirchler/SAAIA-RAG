# Contenu canonique et correction des citations — 13 septembre 2026

Le dernier essai réel A804 produit un tableau de 20 choix, mais il est rejeté : C5 et C9 citent seulement des renvois vers les recettes ; C13 et C19 citent des pages de titre et de mise en page. L'ouverture des 20 cartes, leur hash et leurs pages sont vérifiés. Ces preuves physiques ne prouvent pas que chaque affirmation est étayée par sa citation. Les autres choix ne sont pas déclarés tous approuvés par déduction.

## Cause et changement

La revalidation des content cards utilisait `search_text`, un texte enrichi pour la recherche contenant des valeurs et des étiquettes générées. Elle transmet désormais le chunk canonique quand la métadonnée `canonical_section_anchor_evidence_v1` fournit son index, avec vérification du tenant, de la révision et de l'intervalle de pages. Si cette référence manque, elle transmet uniquement le titre exact et les `sourceText` des faits, dédupliqués. Les valeurs générées ne servent plus de preuve.

Les annotations du contexte avancé reconnaissent les courts renvois vers des pages, les titres seuls et les artefacts de mise en page. Les quantités avec unités permettent de préserver les ingrédients et le contenu technique. Le classificateur utilisé par l'ingestion reste inchangé. Une annotation de contenu reste un indice de structure ; elle ne valide ni la pertinence ni l'adéquation d'une recette à un repas.

Avant publication d'une grille de sélections nommées, le rédacteur et le critique sont contrôlés : quand toutes les citations d'un choix sont des titres ou renvois seuls, le même modèle reçoit les corrections et sa proposition précédente. Il peut corriger ses citations, remplacer le choix, ou demander `search_corpus` dans le corpus du même job. Le contrôle conserve les références utiles aux titres lorsqu'une citation du contenu est également présente. Il reste borné par le nombre d'appels et la réserve du critique. L'épuisement produit l'erreur technique `advanced_synthesis_candidate_body_not_supported`, jamais une preuve d'absence dans le corpus.

## Preuves reproductibles

Les artefacts se trouvent dans `artifacts/reprise-pc-20260908/a800-meal-planning-continuity-20260913`.

- `meal-a805-canonical-prompt-replay.v1.json` : 17 événements de recherche déjà consommés sont revalidés avec le véritable resolver, en transaction de lecture seule. Le contenu de 168 occurrences de cards change ; les références conservent leur identité. Aucun appel API, aucune ingestion et aucune écriture de données.
- `meal-a806-body-support-replay.v1.json` a révélé deux faux positifs : C11, ingrédients, et C15, recette complète. Cette observation est conservée.
- `meal-a806-body-support-replay.v2.json` : après correction de ces faux positifs, le contrôle retrouve exactement C5, C9, C13 et C19 sur le résultat A804 et les sources canoniques revalidées. Ce replay reconstruit un contexte ; ce n'est pas l'archivage exact de la requête API, ni une réponse réelle du modèle corrigé.
- Les tests de comportement simulent séparément la correction par le rédacteur et par le critique, la limite d'appels avec et sans réserve du critique, et le parcours complet correction → recherche dans la source observée → citation du contenu avec conservation du renvoi complémentaire. Les tests de matériau couvrent l'exclusion des valeurs générées, les renvois multilingues, les titres seuls, les faux positifs numériques et les corps contenant des artefacts de mise en page.
- `backend-source-support-final-v3.trx` : 2 268 réussites, aucun échec, trois tests live ignorés. Les 24 nouveaux cas passent. Les anciens tests de correction OCR et de source de section disposent désormais d'un contenu de recette réel dans leur fixture ; leur objectif est conservé et un index seul n'est plus présenté comme une préparation complète. Cette suite ne remplace pas les essais sémantiques du modèle.

## Limites et suite

Il n'y a encore aucun essai réel du modèle avec cette correction. La pertinence de chaque citation, l'adéquation de chaque repas et la qualité du tableau doivent encore être vérifiées. Un contrôle de structure ne remplace pas cette lecture sémantique. Le budget local vérifié après A804 est de 27,72811160 USD consommés sur les 30 USD déjà achetés, soit 2,27188840 USD disponibles. Aucun nouvel achat n'est autorisé.

Le résultat historique de la banque aveugle BH6 reste 1/24. Les tests sur les cas déjà observés ne permettent pas d'annoncer une amélioration de ce score. Une nouvelle banque aveugle exige un code figé et un évaluateur équitable ; les questions futures n'ont pas été générées. Le produit reste TESTE_NON_APPROUVE.
