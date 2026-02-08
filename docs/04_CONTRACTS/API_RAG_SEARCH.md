# API — Retrieval (RAG) : POST /rag/search

## But
Retourner des **chunks citables** (doc/pages/extrait) pour permettre une réponse sourcée côté client.

⚠️ Le serveur ne génère pas la réponse finale (NO LLM serveur).

## Request (CDC v2.7)
```json
{
  "query": "Question utilisateur",
  "category": "general",
  "topK": 12,
  "minScore": 0.2,
  "diversity": {
    "maxChunksPerDoc": 2,
    "preferDistinctPages": true
  }
}
```

## Response (CDC v2.7)
```json
{
  "requestId": "…",
  "items": [
    {
      "score": 0.63,
      "docId": "…",
      "docName": "Manual.pdf",
      "docPath": "…",
      "category": "general",
      "pageStart": 12,
      "pageEnd": 13,
      "chunkId": "…",
      "chunkIndex": 42,
      "text": "…"
    }
  ],
  "metrics": { "tookMs": 54, "returned": 12 }
}
```

## Notes d’implémentation
- `requestId` = `HttpContext.TraceIdentifier` (alimenté par `X-Request-Id` middleware)
- `items[]` doit être stable : pas de champs “expérimentaux” sans versioning.
