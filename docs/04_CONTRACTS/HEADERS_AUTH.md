# Headers & Auth (contrat)

## Headers communs
- `X-Request-Id` (optionnel en entrée, obligatoire en sortie)
- `Content-Type: application/json` pour endpoints JSON

## Auth API key
- Header obligatoire : `X-Api-Key: <key>`
- Le serveur résout : `tenant_id`, `api_key_id`, `is_admin`

## Multi-tenant
- Toutes les requêtes doivent être scoppées par `tenant_id` (sauf endpoints publics / admin).
