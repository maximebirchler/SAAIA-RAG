# Backup / Restore

## Objectif
Sauvegarder :
- Postgres (tenants, docs, chat store, jobs)
- Qdrant (vectors) ou capacité de rebuild via ingestion

## Stratégie recommandée
- Backup Postgres quotidien
- Snapshot Qdrant hebdo (ou rebuild + hash docs)

## Restore
- Restaurer Postgres → relancer backend → vérifier `/ready`
- Restaurer Qdrant ou déclencher rebuild (selon stratégie)
