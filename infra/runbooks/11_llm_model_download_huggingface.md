# Runbook — Télécharger le modèle LLM via Hugging Face (Option B)

Objectif : lors d'une installation (ou réparation), télécharger automatiquement le modèle GGUF et démarrer le serveur llama.cpp en Docker.

## Pré-requis
- Docker Desktop + accès GPU (NVIDIA) si `server-cuda`.
- Accès Internet.
- (Optionnel) `HF_TOKEN` si le repo est gated ou si vous voulez éviter le rate limit.

## Script

Depuis la racine du repo :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\llm\install-llm.ps1
```

Le script :
- crée `C:\SAAIA\models` (par défaut)
- télécharge `Mistral-7B-Instruct-v0.3-IQ3_M.gguf` depuis Hugging Face
- vérifie le SHA256
- génère `C:\SAAIA\deploy\docker-compose.llm.yml`
- lance `docker compose up -d`
- attend `/v1/models`

## Variables utiles

### HF_TOKEN

```powershell
$env:HF_TOKEN = "hf_..."
```

### Override du repo / fichier

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\llm\install-llm.ps1 -Repo "bartowski/Mistral-7B-Instruct-v0.3-GGUF" -File "Mistral-7B-Instruct-v0.3-IQ3_M.gguf"
```

### Skip docker / skip wait

```powershell
# Ne fait que télécharger + vérifier
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\llm\install-llm.ps1 -NoDockerUp

# Lance docker mais ne wait pas /v1/models
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\llm\install-llm.ps1 -NoWait
```

## Validation

```powershell
curl.exe -s http://127.0.0.1:1234/v1/models
```

## Logs

```powershell
docker logs -f saaia-llama
```
