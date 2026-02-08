# Matrice de tests (v2.7)

## Serveur
- Auth
  - [ ] 401 sans `X-Api-Key` (retourne `X-Request-Id` + JSON error)
  - [ ] 401 clé invalide (idem)
- RAG
  - [ ] 400 query vide (retourne requestId)
  - [ ] 200 query valide (items citables)
- Chat store
  - [ ] création session (user_id obligatoire)
  - [ ] listing sessions scoppé (tenant+user)
  - [ ] ajout message + lecture historique

## Client
- [ ] UI-only : Send ajoute message assistant mock
- [ ] RAG-only : Send affiche sources (et pas de LLM requis)
- [ ] LLM : streaming + citations
