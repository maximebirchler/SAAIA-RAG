# Runbook — Télécharger le modèle LLM via Hugging Face (Option B)

Objectif : lors d'une installation (ou réparation), télécharger automatiquement le modèle GGUF et démarrer le serveur llama.cpp en Docker.

## Pré-requis
- Docker Desktop + accès GPU (NVIDIA) si `server-cuda`.
- Accès Internet.
- (Optionnel) `HF_TOKEN` si le repo est gated ou si vous voulez éviter le rate limit.
- L’image CUDA épinglée construite par
  `infra/scripts/llm/build-llama-cuda-runtime.sh`.

## Script

Depuis la racine du repo :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\llm\install-llm.ps1
```

Le script :
- crée `C:\SAAIA\models` (par défaut)
- télécharge `Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf` depuis la révision
  Hugging Face épinglée
- vérifie le SHA256
- génère `C:\SAAIA\deploy\docker-compose.llm.yml`
- lance `docker compose up -d`
- attend `/v1/models`, puis exécute une vraie inférence de qualification

## Variables utiles

### HF_TOKEN

```powershell
$env:HF_TOKEN = "hf_..."
```

### Variante ou miroir contrôlé

Les paramètres `-Repo`, `-Revision`, `-File` et `-Sha256` doivent toujours être
fournis ensemble. Une variante n’est promue qu’après ajout au catalogue
gouverné et qualification matérielle.

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
