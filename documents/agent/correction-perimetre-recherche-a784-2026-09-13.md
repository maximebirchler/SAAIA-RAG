# A784 — changer le périmètre d'une recherche sans perdre la déduplication

13 septembre 2026. Statut produit : **TESTE_NON_APPROUVE**.

Après A783, trois questions BH6 déjà consommées ont été rejouées une fois avec
Qwen sur ce PC, le backend temporaire actuel et Terra. Il s'agit de diagnostics
de développement, pas d'une nouvelle banque aveugle. Les trois jobs ont terminé
mais leurs réponses restent rejetées. TLS conserve une insuffisance citant un
document IEC sans rapport ; FOMC conserve une insuffisance alors que les minutes
sont présentes ; OAuth échoue sur une sélection distincte du tableau. Le runner
rejette mécaniquement la campagne à cause du drapeau d'insuffisance TLS. Les
traces des trois réponses sont conservées malgré ce rejet.

Le coût estimé total est de 0,275706 USD : 5 appels TLS, 4 FOMC, 6 OAuth.
Les plafonds étaient sept appels et 0,40 USD par job, avec un seul essai de job.
Le provider produit utilisait `low`, comme le runner BH6 original. Le niveau
`medium` concernait le constructeur et l'évaluateur distincts de la banque.
Le corpus, le verdict BH6 et les secrets restent inchangés ; backend temporaire
et modèle local sont arrêtés, ports 5123 et 1234 libres, aucun job non terminal.

Les événements montrent des requêtes TLS filtrées sur `normes` ou
`documents techniques` et des requêtes FOMC filtrées sur `finance - compta`.
Ces recherches ne donnent pas la preuve attendue. Le catalogue confirme pourtant
la présence de `RFC_8446_TLS_1_3.pdf` dans Manuels logiciel/PDF et de
`FOMC_Minutes_2025_05_07.pdf` dans Emails - Réunion/PDF. Une catégorie déduite du
sujet ne prouve donc pas où le document est classé. A783 ouvre la recherche
complémentaire ; il ne corrige pas à lui seul le choix de son périmètre.

A784 rend la recherche pilotable de façon plus précise :

- les catégories sont décrites comme des classements documentaires ; pour un
  fait direct, le planner doit conserver un périmètre ouvert sauf contrainte
  utilisateur ou classement établi par les observations ;
- `documentHint`, déjà validé et résolu par le gateway, est maintenant exposé
  au modèle et lu dans ses requêtes ; il ne déclenche un filtre de fichier que
  pour un match fort et unique dans les documents indexés du tenant ;
- les requêtes antérieures sont transmises avec leurs filtres dans `priorSearches` ;
- la déduplication compare la requête et son périmètre, ses pages et ses limites
  de résultats. Une même phrase avec une catégorie retirée est une nouvelle
  recherche. Un doublon identique, y compris avec une casse différente, reste
  ignoré ;
- les recherches complémentaires conservent l'identité des documents exigés
  par la demande sans réinjecter toutes les requêtes initiales.

L'historique local conserve ses phrases pour informer le planner. Une phrase
seule, sans filtres, ne constitue plus une identité suffisante pour interdire
la même recherche au serveur avec un autre périmètre. Les événements serveur
antérieurs fournissent les identités complètes utilisées pour dédupliquer.
Les limites d'appels, de coût, de recherches et l'isolation du tenant ne sont
pas augmentées.

Quatre nouveaux tests échouaient avant correction : changement de catégorie
sur la même phrase, deux catégories dans le même plan, hint de document émis
par le modèle, conservation du document dans une recherche complémentaire.
Ils passent après correction. Un cinquième test contrôle qu'une recherche
complémentaire strictement identique ne repart pas. Le test de deux catégories
contient également un doublon avec une casse différente, toujours éliminé.
La suite backend Release complète passe : **2 201 réussites, zéro échec,
trois tests live ignorés**.

Les preuves synthétiques valident le mécanisme. La reprise sur A784+A785 donne
maintenant une réponse TLS soutenue par le RFC page 72 et une réponse FOMC
soutenue par les minutes page 2, au lieu des deux insuffisances précédentes.
Les deux passages canoniques ont été lus ; les deux sources passent également
le contrôle indépendant de fichier, hash, révision, ancre et pages. Il s'agit
de deux diagnostics consommés réussis une fois, sans nouvelle approbation
aveugle. OAuth nécessite encore de distinguer les faits sur
des lignes fixées d'une sélection de nouveaux objets nommés. Aucun score BH6
ni aucune approbation produit ne sont révisés par cette correction.

Preuves locales : `artifacts/reprise-pc-20260908/a783-adaptive-research-20260913`
et `artifacts/reprise-pc-20260908/a784-research-scope-20260913`.
