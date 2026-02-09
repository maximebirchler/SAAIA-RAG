# API — Retrieval (RAG) : POST /rag/search

## But
Retourner des **chunks citables** (doc/pages/extrait) pour permettre une reponse sourcee cote client.

⚠️ Le serveur ne genere pas la reponse finale (NO LLM serveur).

## Request (CDC v2.7)
```json
{
  "query": "Question utilisateur",
  "category": "general",
  "topK": 12,
  "minScore": 0.2,
  "mode": "balanced"
}
```

**Parametres optionnels :**
- `category` : filtre par categorie
- `topK` : nombre de resultats (1-50, defaut: 10)
- `minScore` : score minimum (0-1, defaut: 0.25)
- `mode` : "focused" | "balanced" | "broad" (defaut: "balanced")

## Response (CDC v2.7)
```json
{
  "requestId": "00-550e8400-e29b-41d4-a716-446655440000-1",
  "query": "Question utilisateur",
  "queryNormalized": "question utilisateur",
  "category": "general",
  "topK": 12,
  "minScore": 0.2,
  "candidates": 72,
  "maxPerDoc": 6,
  "maxPerPage": 1,
  "metrics": {
    "tookMs": 54,
    "returned": 4
  },
  "items": [
    {
      "score": 0.89,
      "docId": "550e8400-e29b-41d4-a716-446655440001",
      "docName": "Manual.pdf",
      "docPath": "/docs/Manual.pdf",
      "category": "general",
      "pageStart": 12,
      "pageEnd": 13,
      "chunkId": "chunk-42",
      "chunkIndex": 42,
      "text": "Ceci est un extrait du document..."
    }
  ]
}
```

## Test (curl)
```bash
curl -X POST http://localhost:5122/rag/search \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME" \
  -d '{"query":"configuration","topK":5,"minScore":0.3}'
```

## Notes
- `requestId` = HttpContext.TraceIdentifier (alimenté par middleware X-Request-Id)
- `items[]` structure stable : pas de champs experimentaux sans versioning
- Chaque item contient contexte complet (doc/page/extrait) pour citation
