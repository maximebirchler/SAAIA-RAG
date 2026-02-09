# API — Chat store (historique serveur v2.7)

## Objectif
Stocker l'historique **par tenant et par user**.

## user_id (obligatoire)
- Genere cote client (GUID stable)
- Envoye a chaque operation chat (creation session / listing / messages)
- Utilise pour scoper l'acces : un user ne voit que ses sessions.

---

## Endpoints (contrat v2.7)

### POST /chat/sessions
Cree une session pour un utilisateur.

**Request :**
```json
{
  "userId": "550e8400-e29b-41d4-a716-446655440000",
  "title": "Configuration questions",
  "clientUser": "john.doe"
}
```

**Response :**
```json
{
  "sessionId": "660e8400-e29b-41d4-a716-446655440001",
  "title": "Configuration questions",
  "clientUser": "john.doe",
  "createdAtUtc": "2026-02-08T22:50:00Z"
}
```

### GET /chat/sessions?userId=UUID&limit=50&offset=0
Liste les sessions de l'utilisateur.

**Response :**
```json
[
  {
    "sessionId": "660e8400-e29b-41d4-a716-446655440001",
    "title": "Configuration questions",
    "clientUser": "john.doe",
    "createdAt": "2026-02-08T22:50:00Z",
    "updatedAt": "2026-02-08T23:15:00Z",
    "lastMessageAt": "2026-02-08T23:15:00Z"
  }
]
```

### POST /chat/sessions/{sessionId}/messages
Ajoute un message a une session.

**Request :**
```json
{
  "userId": "550e8400-e29b-41d4-a716-446655440000",
  "role": "user",
  "content": "Quelle est la configuration?",
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
Recupere l'historique (avec validation userId).

**Response :**
```json
[
  {
    "messageId": "770e8400-e29b-41d4-a716-446655440002",
    "role": "user",
    "content": "Quelle est la configuration?",
    "sources": null,
    "createdAt": "2026-02-08T23:15:00Z"
  }
]
```

---

## Test (curl PowerShell)

```powershell
# POST /chat/sessions avec userId (OBLIGATOIRE)
@'
{"userId":"550e8400-e29b-41d4-a716-446655440000","title":"Test"}
'@ | curl.exe -i -X POST "http://localhost:5122/chat/sessions" `
  -H "Content-Type: application/json" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME" `
  --data-binary "@-"

# GET /chat/sessions avec userId query param (OBLIGATOIRE)
curl.exe -i -X GET "http://localhost:5122/chat/sessions?userId=550e8400-e29b-41d4-a716-446655440000" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME"

# POST /chat/sessions/{sessionId}/messages avec userId
@'
{"userId":"550e8400-e29b-41d4-a716-446655440000","role":"user","content":"Hello"}
'@ | curl.exe -i -X POST "http://localhost:5122/chat/sessions/SESSION_ID/messages" `
  -H "Content-Type: application/json" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME" `
  --data-binary "@-"
```

---

## Regles
- **user_id obligatoire** : isolation par user (CDC v2.7)
- **Isolation tenant** : appliquee via middleware GetTenantId()
- **Validation userId** : chaque operation filtre et valide par userId
- **Roles autorises** : "user", "assistant", "system", "tool"
- **Index DB** : tenant_id + user_id + updated_at
