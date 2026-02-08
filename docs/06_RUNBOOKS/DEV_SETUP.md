# Setup Dev (Windows)

## Prérequis
- Docker Desktop
- .NET SDK compatible avec le repo
- Visual Studio 2026 + GitHub Copilot (optionnel mais recommandé)

## Démarrage rapide
1) Lancer l’infra (`infra/docker-compose.dev.yml` ou équivalent)
2) Lancer le backend (dotnet run ou service docker)
3) Vérifier :
   - `GET /health`
   - `GET /ready`
4) Lancer WinUI (client) et configurer :
   - ServerUrl
   - ApiKey
   - Mode (UI-only / RAG-only / LLM)

## Debug
- Toujours regarder `X-Request-Id` en cas d’erreur
