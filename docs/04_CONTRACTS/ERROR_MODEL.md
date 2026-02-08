# Modèle d’erreur (contrat)

Objectif : faciliter debug et corrélation (support client).

## Règle
Toute erreur doit :
- renvoyer `X-Request-Id`
- renvoyer un body JSON au format :

```json
{
  "error": "Human readable message",
  "requestId": "uuid-or-client-provided"
}
```

## Codes attendus
- 400 : validation input
- 401 : missing/invalid API key
- 403 : forbidden (ex: admin)
- 404 : not found (si applicable)
- 429 : rate limit
- 500 : erreur serveur (ne jamais exposer de secrets)
