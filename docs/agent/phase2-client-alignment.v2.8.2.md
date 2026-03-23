
# Patch P2.1 — client/orchestrateur

## But
Préférer la nouvelle surface backend (`/auth/capabilities`, `/catalog/*`) tout en gardant des fallbacks legacy.

## Inclus
- cache local `capabilities` + `snapshot`
- meilleure résolution des catégories via `categoryRef`
- normalisation des réponses `/catalog/categories`, `/catalog/documents`, `/catalog/summaries`
- préparation d'une `PendingDirectCommand` en mémoire pour les shortcuts déterministes

## Non inclus
- pas encore de refonte UI
- pas encore de suppression massive des shortcuts
- pas encore de transport complet `PendingCommand` -> `directIntent`
