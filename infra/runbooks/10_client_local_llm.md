# M6.1 — LLM local piloté par le client (llama.cpp)

Objectif : permettre au client WinUI de **démarrer/arrêter** un serveur `llama.cpp` local (OpenAI-compat),
et d’utiliser ce LLM pour la génération (le serveur SAAIA reste **sans LLM** en v2.7).

## Pré-requis

- Un binaire `llama-server.exe` (llama.cpp) sur la machine client
- Un modèle GGUF (ex : `Mistral-7B-Instruct-v0.3-IQ3_M.gguf`)

Recommandation de chemins :
- `C:\SAAIA\llm\llama-server.exe`
- `C:\SAAIA\models\Mistral-7B-Instruct-v0.3-IQ3_M.gguf`

## Lancer le client (CLI)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\client\scripts\run-client.ps1
```

## Configurer / Démarrer le LLM dans l’UI

Dans l’application :
1) Cliquer sur **LLM**
2) Cocher **Enable local LLM**
3) Renseigner `llama-server.exe`, le `.gguf`, host/port, extra args
4) Cliquer **Start now**
5) Cliquer **Save**

### Vérifier côté console

Une fois démarré (port 1234 par défaut) :

```powershell
curl.exe -s http://127.0.0.1:1234/v1/models
```

Attendu : JSON avec des modèles.

## Logs

Le client écrit les logs de `llama.cpp` ici :

`%LOCALAPPDATA%\SAAIA\logs\llama-server_YYYYMMDD_HHMMSS.log`

En cas de problème de démarrage, ouvrir ce fichier.

## Notes

- Le client teste la readiness via `/v1/models`, puis fallback `/health` si nécessaire.
- Le modèle utilisé dans les appels OpenAI-compat est `Model ID` (champ dans la config LLM).
