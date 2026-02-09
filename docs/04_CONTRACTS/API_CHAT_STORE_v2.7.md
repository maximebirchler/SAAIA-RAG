# API — Chat store (historique serveur v2.7)

## Objectif
Stocker l'historique **par tenant et par user**.

## user_id (obligatoire)
- Généré côté client (GUID stable ou UUID)
- Envoyé à chaque opération chat (création session / listing / messages)
- Utilisé pour scoper l'accès : un user ne voit que ses sessions

---

## Endpoints (contrat v2.7)

### POST /chat/sessions
**Crée une nouvelle session pour un utilisateur**

**Request :**
```json
{
  "userId": "550e8400-e29b-41d4-a716-446655440000",
  "title": "Conversation sur l'API",
  "clientUser": "john.doe"
}
```

**Response :**
```json
{
  "sessionId": "660e8400-e29b-41d4-a716-446655440001",
  "title": "Conversation sur l'API",
  "clientUser": "john.doe",
  "createdAtUtc": "2026-02-08T22:50:00Z"
}
```

### GET /chat/sessions?userId=UUID&limit=50&offset=0
**Liste les sessions de l'utilisateur**

**Response :**
```json
[
  {
    "sessionId": "660e8400-e29b-41d4-a716-446655440001",
    "title": "Conversation sur l'API",
    "clientUser": "john.doe",
    "createdAt": "2026-02-08T22:50:00Z",
    "updatedAt": "2026-02-08T23:15:00Z",
    "lastMessageAt": "2026-02-08T23:15:00Z"
  }
]
```

### GET /chat/sessions/{sessionId}?userId=UUID
**Récupère une session (avec validation userId)**

**Response :**
```json
{
  "sessionId": "660e8400-e29b-41d4-a716-446655440001",
  "title": "Conversation sur l'API",
  "clientUser": "john.doe",
  "createdAt": "2026-02-08T22:50:00Z",
  "updatedAt": "2026-02-08T23:15:00Z",
  "lastMessageAt": "2026-02-08T23:15:00Z"
}
```

### POST /chat/sessions/{sessionId}/messages
**Ajoute un message à une session**

**Request :**
```json
{
  "userId": "550e8400-e29b-41d4-a716-446655440000",
  "role": "user",
  "content": "Quelle est la configuration recommandée?",
  "sourcesJson": "{\"items\": [...]}",
  "sourcesJson": null
}
```

**Response :**
```json
{
  "messageId": "770e8400-e29b-41d4-a716-446655440002",
  "createdAtUtc": "2026-02-08T23:15:00Z"
}
```

### GET /chat/sessions/{sessionId}/messages?userId=UUID&limit=200
**Récupère l'historique d'une session (avec validation userId)**

**Response :**
```json
[
  {
    "messageId": "770e8400-e29b-41d4-a716-446655440002",
    "role": "user",
    "content": "Quelle est la configuration recommandée?",
    "sources": {
      "items": [
        {
          "docName": "Manual.pdf",
          "pageStart": 12,
          "excerpt": "..."
        }
      ]
    },
    "createdAt": "2026-02-08T23:15:00Z"
  },
  {
    "messageId": "880e8400-e29b-41d4-a716-446655440003",
    "role": "assistant",
    "content": "Selon le manuel...",
    "sources": null,
    "createdAt": "2026-02-08T23:15:30Z"
  }
]
```

---

## Règles
- **user_id obligatoire** : isolation par user (CDC v2.7)
- **Isolation tenant** : déjà appliquée via middleware `GetTenantId()`
- **Validation userId** : chaque opération doit inclure userId et vérifier l'accès
- **Roles autorisés** : "user", "assistant", "system", "tool"
- **Index DB** : `chat_sessions(tenant_id, user_id, updated_at)`, `chat_messages(session_id, created_at)`

## Test (curl)
```bash
# Créer une session
curl -X POST http://localhost:8080/chat/sessions \
  -H "Content-Type: application/json" \
  -H "X-API-Key: YOUR_API_KEY" \
  -d '{
    "userId": "550e8400-e29b-41d4-a716-446655440000",
    "title": "Test"
  }'

# Lister sessions
curl -X GET "http://localhost:8080/chat/sessions?userId=550e8400-e29b-41d4-a716-446655440000" \
  -H "X-API-Key: YOUR_API_KEY"

# Ajouter message
curl -X POST http://localhost:8080/chat/sessions/SESSION_ID/messages \
  -H "Content-Type: application/json" \
  -H "X-API-Key: YOUR_API_KEY" \
  -d '{
    "userId": "550e8400-e29b-41d4-a716-446655440000",
    "role": "user",
    "content": "Hello"
  }'

# Lister messages
curl -X GET "http://localhost:8080/chat/sessions/SESSION_ID/messages?userId=550e8400-e29b-41d4-a716-446655440000" \
  -H "X-API-Key: YOUR_API_KEY"
```
