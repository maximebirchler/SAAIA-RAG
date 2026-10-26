# Campagne ingestion / GC / runtime — 26 octobre 2026

Point d’entrée courant : [rapport final](RAPPORT-FINAL-QUALIFICATION-INGESTION.md). Les notes antérieures sont des états datés, notamment lorsqu’elles annoncent un runtime encore à qualifier.

## Preuves finales

- [.NET et PostgreSQL : acceptation commune](docling-final-central-acceptance-v1.json), [Unit final](tests/unit-all-docling-final-v1/manifest.json), [PG final](tests/postgres-all-docling-final-v1/tests/manifest.json) et [nettoyage PG](tests/postgres-all-docling-final-v1/manifest.json).
- [54 projections Production](public-export-production-final-v4/public-export-seal.json) : 12 PDF, livre36pages, recherche/lecture106faits, trois parcours automatiques, cycleGC, remplacement échoué, contrôle signature. Le sceau atteste l’intégrité des projections ; l’acceptation métier dépend des contrôles à l’intérieur de chaque résultat.
- [16 suppléments scellés](public-supplement-final-v4/supplement-seal.json) : santé avant backend, binaire réel, OCR, chemins Linux, repriseDocling26cas et arrêt final des environnements.
- [Sources du build Linux](build-final-v4/manifest.json), [comparaison avec l’index Git](build-final-v4/index-source-comparison-v1.json), [signatures : reçus publics](production-signatures-public-final-v4.json).
- [Résolution Docling](docling-final-resolution-v1.md), [exploitation GC](vector-gc-operations-v1.md), [inventaire SQL en lecture seule](vector-gc-inventory-readonly-v1.sql).

Les archives de transfert, clés, configurations privées, captures brutes SQL/HTTP/logs, poids des modèles, données de test exécutables et corpus réels ne font pas partie du commit public. Les inventaires et empreintes permettent de rattacher les projections aux preuves privées conservées. Le PDF synthétique OCR embarqué est une fixture source explicite, distincte du corpus réel.

Le statut produit reste **TESTE_NON_APPROUVE**. Le planning et les appels LLM payants n’ont pas été exécutés dans cette campagne. Les vecteurs legacy sans manifeste fiable restent protégés et le GC est désactivé par défaut.
