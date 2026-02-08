# Troubleshooting (diagnostic rapide)

## 1) /health OK, /ready KO
- DB down ?
- Qdrant down ?
- TEI down ?
- config signée invalide ?

## 2) RAG renvoie 0 résultat
- documents ingérés ?
- category correcte ?
- embeddings TEI OK ?
- minScore trop haut ?

## 3) Send “ne fait rien” côté client
- mode UI-only fonctionne ?
- Connect effectué ?
- backend reachable ?
- LLM local démarré ?
