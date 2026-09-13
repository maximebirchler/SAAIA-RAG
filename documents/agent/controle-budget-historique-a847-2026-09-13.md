# A847 — contrôles du budget d'historique natif

Préenregistrement fbe17705, `budget-historique-natif-a847-2026-09-13.md`.
L'option NativeResearchMaximumHistoryCharacters est déclarée à 16 384 puis
contrôlée dans l'admission avant I/O, plage 16 384–65 536. Les messages natifs
conservent leur contenu intégral ; refus typé au-delà du plafond, diagnostic
numérique observé/maximal sans état opaque dans le diagnostic. Les extraits
documentaires et les réservations sur tout le payload restent inchangés.

Preuve causale : option 32 768 déclarée mais ancienne admission fixe conservée,
fixture huit lectures/état simulé 20 000 refusée sur l'erreur de taille
(history-a847-baseline-v1). Après branchement, cette fixture passe avec toutes
les sorties liées et l'état identique. Sept contrôles dédiés passent : défaut
inchangé, bornes étendues 32 768 et 65 536, refus au-delà de 16 384 et 32 768,
deux valeurs invalides rejetées avant modèle/corpus. État simulé, pas une mesure
de raisonnement réel. 95 régressions ciblées passent, incluant les deux transports,
mémoire, budgets et réservation complète ; suite backend 2 417 réussites, zéro
échec, trois live ignorés. Aucune nouvelle campagne PostgreSQL réelle revendiquée.

Runner : paramètre borné transmis à la configuration et au sceau de préflight.
Corriger aussi les attributs voisins : le Writer avait deux ValidateRange et le
Critic aucun ; chaque paramètre reçoit désormais son propre contrôle 512–16 384.
Syntaxe correcte et six refus de binding hors limites vérifiés avant exécution,
artefact history-a847-runner-binding.v2.json. La première tentative de capture
utilisait un type d'exception PowerShell interne non résoluble ; ses erreurs et
son fichier v1 ne sont pas des succès. Capture générique avec ErrorId vérifié.

Profil expérimental prévu 32 768, sans hausse des extraits, pages, appels,
effort ou enveloppe monétaire. A846 reste sans synthèse : premier save réel de
deux entrées groupées, aucun bénéfice mesuré avant l'arrêt historique. Registre
clos 37,85846860 USD / 40, solde calculé 2,14153140 USD. Un pilote après gel
propre, pas de répétition avant lecture du résultat. TESTE_NON_APPROUVE, Goal actif.
