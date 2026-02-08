# API — Chat store (historique serveur v2.7)

## Objectif
Stocker l’historique **par tenant et par user**.

## user_id (obligatoire)
- Généré côté client (GUID stable)
- Envoyé à chaque opération chat (création session / listing / messages)
- Utilisé pour scoper l’accès : un user ne voit que ses sessions.

---

## Endpoints (contrat v2.7)
### POST /chat/sessions
```json
{
  "userId": "uuid",
  "title": "Optionnel",
  "clientUser": "Optionnel (nom humain)"
}
```

### GET /chat/sessions?userId=uuid
Retourne les sessions du user dans le tenant.

### POST /chat/messages
```json
{
  "sessionId": "uuid",
  "userId": "uuid",
  "role": "user|assistant|system|tool",
  "content": "texte",
  "sourcesJson": "{...}" 
}
```

### GET /chat/messages?sessionId=uuid&userId=uuid&limit=50
Retourne l’historique d’une session.

---

## Règles
- Isolation tenant + user obligatoire
- Migration DB : index tenant_id+user_id+updated_at
