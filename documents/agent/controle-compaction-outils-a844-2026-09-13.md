# A844 — contrôle de la compaction opérationnelle native

Préenregistrement : `compacter-retours-outils-natifs-a844-2026-09-13.md`,
commit fb1a55ea avant changement produit. A843 reste clos sans réponse : trois
appels, 0,12947970 USD, huit recherches et huit lectures réussies après correction
du batch par le modèle. Le budget visible indique huit consommées / vingt-quatre
restantes. L'arrêt ultérieur est `advanced_native_tool_history_limit_exceeded`.
Le registre cumulé est 36,46125760 USD / 40 ; solde calculé 3,53874240 USD.

Modification : les sorties documentaires liées conservent une instruction commune
dans le premier retour, retirent ses répétitions identiques et omettent uniquement
les propriétés de premier niveau nulles. Identités résolues, compteurs, IDs visibles,
nombre omis, diagnostics et feedback non nuls restent conservés. Chaque call ID
garde sa sortie. Les éléments Responses originaux, y compris le raisonnement
opaque, restent intacts. La mémoire de recherche conserve sa consigne distincte.
Plafond 16 384 caractères, contexte, preuves et quotas inchangés.

Contrôle causal : un état opaque simulé de 8 000 caractères et huit lectures
valides échoue sur l'erreur de taille avec le code antérieur (baseline-v3), puis
réussit après modification (after-v2), avec toutes les sorties, l'état opaque
exact et la preuve actuelle. Un état simulé de 20 000 reste refusé avant un nouvel
appel modèle ; aucune troncature. Ce sont des fixtures de transport, sans verdict
sur un planning réel ou sur le raisonnement du fournisseur.

Les tentatives antérieures sont conservées : baseline-v1 utilisait une limite
de six requêtes incompatible avec les huit appels et échouait sur le protocole.
Baseline-v2 atteignait bien la limite de taille, mais son état simulé de 10 000
restait trop grand après compaction (after-v1 : un succès, un échec). Aucun de
ces essais ne constitue une preuve de passage causal à 10 000 caractères.

Résultats : deux contrôles dédiés réussis ; 88 régressions ciblées réussies
(protocoles, liaisons natives, mémoire, budgets) ; suite backend 2 412 réussites,
zéro échec, trois tests live ignorés. Artefacts TRX/logs dans
`artifacts/reprise-pc-20260908/a815-completion-audit-20260913/history-a844-*`.
Les tests PostgreSQL qui nécessitent un environnement explicite ne constituent
pas une nouvelle campagne PostgreSQL réelle ; les deux contrôles réels A843
restent leur preuve spécifique. A844 ne change ni compteur ni persistance.

Prochain contrôle : un seul pilote connu sur version propre figée avec le profil
A843 inchangé, puis lecture de la réponse et des vingt liaisons, audit physique
si un résultat terminal existe. Aucune réussite actuelle à attribuer à A844.
Trois réussites consécutives figées restent nécessaires avant banc inédit et
WinUI. Produit TESTE_NON_APPROUVE, Goal actif, aucun achat ou recharge automatique.
