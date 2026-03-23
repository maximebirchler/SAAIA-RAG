# SAAIA – Alignement contractuel v2.8.2 (phase 0 / patch 1)

## Décisions figées dans ce patch

### 1) Champ canonical du routeur
- Le nom canonical conservé pour le routeur reste `responseFormat`.
- `responseStyle` n'est pas adopté côté runtime dans ce patch pour éviter une régression de schéma.

### 2) Sémantique catégorie
- `categoryPath` = vérité métier documentaire côté backend.
- `displayOrder` = ordre stable UI pour les catégories top-level.
- `categoryRef` = référence publique de transition dérivée de `displayOrder` au format `cat_###`.
- Les alias restent une aide de résolution backend ; ils ne remplacent pas la vérité canonique.

### 3) Surface API
- Les routes historiques `/documents/*` et `/admin/summaries/*` restent supportées.
- Une nouvelle surface contractuelle est introduite en parallèle :
  - `GET /auth/capabilities`
  - `GET /catalog/snapshot`
  - `GET /catalog/categories`
  - `GET /catalog/documents`
  - `GET /catalog/stats`
  - `GET /catalog/summaries` (admin seulement)

### 4) Pagination
- La nouvelle surface catalogue utilise `value + nextLink`.
- `nextLink` est absolu et opaque.
- La dernière page n'expose pas `nextLink`.
- Les routes legacy restent en `limit/offset`.

### 5) Sécurité
- Les commandes admin restent pilotées par `X-Admin-Key` côté serveur.
- `GET /auth/capabilities` n'accorde aucun droit : il ne fait qu'exposer les capacités déduites du rôle déjà validé.

## Portée de ce patch
- Préparer le backend et le client pour la future bascule `PendingCommand` / `directIntent`.
- Ne pas supprimer encore les shortcuts client existants.
- Ne pas casser les routes historiques déjà utilisées par WinUI et les tests.

## Ce qui n'est volontairement pas fait dans ce patch
- Pas de suppression massive des heuristiques client.
- Pas encore de `POST /chat/turn` avec `directIntent`.
- Pas encore de migration complète BCP47 dans les schémas router/writer.
