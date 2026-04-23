# CODEX — Brief d'execution Phase 0A + 0B
## SAAIA — Quick fixes LLM + endpoint support bundle

> Base CDC : v3.1  
> Reference TODO : `TODO.md` (Patch 1 → 3)  
> Commandes de validation apres chaque patch : voir section "Validation" en bas de chaque patch  
> Ordre d'execution impose : **Patch 1 → Patch 2 → Patch 3** (Patch 2 depend du lecteur GGUF de Patch 1)

---

## Patch 1 — Lecteur GGUF + ngl depuis metadata + ctx 3072

### Contexte

`GpuDetector.ComputeAutoTuning` calcule `ngl` depuis une table statique VRAM (24/32/48/72/99).
Le CDC v3.1 §18.4 impose `ngl = llm.block_count` depuis les metadonnees GGUF du modele.
Pour Qwen 2.5 3B Q4_K_M : `block_count = 36`, `head_count_kv = 2`.

### Fichiers a modifier

| Fichier | Changement |
|---|---|
| `client/SAAIA.Client.WinUI/Services/GpuDetector.cs` | Ajouter `GgufMetadataReader` + refactorer `ComputeAutoTuning` |
| `client/SAAIA.Client.WinUI/Services/AppSettings.cs` | Ligne 101 : default `ExtraArgs` |
| `client/SAAIA.Client.WinUI/MainWindow.xaml` | Ligne 464 : `PlaceholderText` |

### Travaux exacts

**A. Ajouter `GgufMetadataReader` dans `GpuDetector.cs` (ou fichier separe)**

Spec GGUF (format binaire) :
```
magic[4]  = "GGUF"
version[4] = uint32
n_tensors[8] = uint64
n_kv[8]    = uint64
kv pairs   = cle (string) + type (uint32) + valeur
```
Types utiles : `uint32 = 4`, `uint64 = 5`, `string = 8`.

Cles a lire :
- `llm.block_count` → type uint32 → `ngl`
- `llm.attention.head_count_kv` → type uint32 → budget KV cache

Ne pas charger les poids. Arreter la lecture apres les cles necessaires ou apres un timeout de N octets (securite).

```csharp
internal static class GgufMetadataReader
{
    internal record GgufKeys(uint BlockCount, uint HeadCountKv);

    // Returns null if the file is not readable or not a valid GGUF.
    public static GgufKeys? TryRead(string ggufPath)
    {
        // Implementation : open FileStream, read magic/version/n_tensors/n_kv,
        // iterate kv pairs until both keys found or EOF.
        // Cle "llm.block_count" -> BlockCount
        // Cle "llm.attention.head_count_kv" -> HeadCountKv
    }
}
```

**B. Refactorer `GpuDetector.ComputeAutoTuning(GpuInfo?)` — chemin principal**

- Accepter un parametre optionnel `string? ggufPath`
- Si `ggufPath` fourni et lisible : `ngl = ggufMeta.BlockCount`
- Fallback si GGUF illisible : conserver la table tier VRAM actuelle + logguer un warning
- Garantir `batch >= 512` pour tout profil GPU (corriger tiers 192/256/384)

```csharp
// AVANT (extrait)
if (vramMiB <= 5120) { batch = 192; ngl = 24; ... }

// APRES
var ggufMeta = string.IsNullOrWhiteSpace(ggufPath) ? null : GgufMetadataReader.TryRead(ggufPath);
var ngl = ggufMeta?.BlockCount ?? FallbackNglFromVram(vramMiB);
var batch = Math.Max(512, BatchFromVram(vramMiB));  // batch >= 512 toujours
```

- Mettre a jour aussi `ComputeAutoTuning(NvidiaGpuInfo?)` pour coherence (meme logique, meme fallback).

**C. `AppSettings.cs` ligne 101**

```csharp
// AVANT
public string ExtraArgs { get; set; } = "--ctx-size 4096";

// APRES
public string ExtraArgs { get; set; } = "--ctx-size 3072";
```

**D. `MainWindow.xaml` ligne 464**

```xml
<!-- AVANT -->
PlaceholderText="--ctx-size 4096"

<!-- APRES -->
PlaceholderText="--ctx-size 3072"
```

### Pieges a eviter

- Ne pas inclure la couche output dans `ngl` : `ngl = block_count` (pas `block_count + 1`)
- Pour Qwen 2.5 3B Q4_K_M : `block_count = 36`, `head_count_kv = 2` — verifier que le lecteur retourne ces valeurs exactes
- L'overload `ComputeAutoTuning(NvidiaGpuInfo?)` doit aussi etre mis a jour (sinon regression sur les chemins NVIDIA legacy)
- Le lecteur GGUF ne doit jamais charger les tensors (fichier de plusieurs Go) — arreter apres la section kv

### Nouveaux tests a ajouter

```
// Dans SAAIA.Client.ToolAgent.Tests ou un nouveau projet de test client :

[Fact]
public void GgufMetadataReader_Qwen25_3B_Q4KM_Returns_BlockCount36_HeadCountKv2()
{
    var modelPath = "/* chemin vers le .gguf sur la machine de dev */";
    var meta = GgufMetadataReader.TryRead(modelPath);
    Assert.NotNull(meta);
    Assert.Equal(36u, meta!.BlockCount);
    Assert.Equal(2u, meta.HeadCountKv);
}

[Fact]
public void ComputeAutoTuning_WithGgufMeta_NglFromBlockCount()
{
    var gpu = new GpuInfo(GpuVendor.Nvidia, "Quadro P520", 2_000 * 1024L * 1024L, false, "test");
    var ggufPath = "/* chemin .gguf */";
    var (threads, batch, ngl) = GpuDetector.ComputeAutoTuning(gpu, ggufPath);
    Assert.Equal(36, ngl);       // block_count = 36
    Assert.True(batch >= 512);   // LLM-009
}

[Fact]
public void ComputeAutoTuning_WithoutGguf_FallbackToVramTier_BatchAtLeast512()
{
    var gpu = new GpuInfo(GpuVendor.Nvidia, "Test GPU", 2_000 * 1024L * 1024L, false, "test");
    var (_, batch, ngl) = GpuDetector.ComputeAutoTuning(gpu, ggufPath: null);
    Assert.True(batch >= 512);
    Assert.True(ngl > 0);
}
```

### Validation Patch 1

```bash
dotnet build client/SAAIA.Client.WinUI/SAAIA.Client.WinUI.csproj -p:Platform=x64 -p:Configuration=Debug
dotnet test client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj -p:NuGetAudit=false
dotnet test backend/SAAIA.Backend.Tests/SAAIA.Backend.Tests.csproj -p:NuGetAudit=false  # verif aucune regression
```

**Criteres de sortie Patch 1 :**
- ngl = 36 pour Qwen 2.5 3B Q4_K_M (lu depuis GGUF)
- batch >= 512 garanti pour tout profil GPU
- ctx default = 3072 dans AppSettings et placeholder XAML
- Tests GGUF verts
- 333 tests backend inchanges

---

## Patch 2 — ubatch + threads-batch + flash-attn conditionnel + logs + strictMode cleanup

**Dependance : Patch 1 termine (lecteur GGUF disponible).**

### Contexte

`LocalLlmBootstrapper.ApplyAutoTuningFlags` n'injecte que `-t`, `-b`, `-ngl`.
Les parametres `--ubatch-size`, `--threads-batch`, `--flash-attn` manquent.
Le runtime `llama-server` ne logge pas sa ligne de commande complete.
Le legacy `strictMode` subsiste en 3 endroits.

### Fichiers a modifier

| Fichier | Changement |
|---|---|
| `client/SAAIA.Client.WinUI/Services/AppSettings.cs` | Ajouter champs `UbatchSize`, `ThreadsBatch`, `FlashAttn` + supprimer legacy strictMode |
| `client/SAAIA.Client.WinUI/Services/LocalLlmBootstrapper.cs` | Injecter ubatch + threads-batch + flash-attn dans `ApplyAutoTuningFlags` |
| `client/SAAIA.Client.WinUI/Services/LlamaCppProcessManager.cs` | Logger la ligne de commande complete |
| `client/SAAIA.Client.WinUI/Services/Provisioning.cs` | Supprimer `LegacyStrictMode` |

### Travaux exacts

**A. `AppSettings.cs` — nouveaux champs**

```csharp
// Ajouter apres ExtraArgs :
public int UbatchSize { get; set; } = 256;          // LLM-010
public int ThreadsBatch { get; set; } = 6;           // LLM-010  
public bool? FlashAttn { get; set; } = null;         // null = auto (propose si CUDA), false = desactive, true = force

// Ajouter dans FileDto et Save/Load correspondants
```

**B. `LocalLlmBootstrapper.ApplyAutoTuningFlags` — injecter les 3 parametres**

```csharp
// ubatch
if (!ContainsArg(extra, "--ubatch-size") && !ContainsArg(extra, "-ub"))
    extra = AppendArg(extra, "--ubatch-size", s.UbatchSize.ToString());

// threads-batch
if (!ContainsArg(extra, "--threads-batch") && !ContainsArg(extra, "-tb"))
    extra = AppendArg(extra, "--threads-batch", s.ThreadsBatch.ToString());

// flash-attn - conditionnel CUDA uniquement
var isCuda = IsGpuRuntimePath(s.LlamaExePath) &&
             s.LlamaExePath.Contains("cuda", StringComparison.OrdinalIgnoreCase);
if (isCuda)
{
    if (!ContainsArg(extra, "--flash-attn") && !ContainsArg(extra, "-fa"))
        extra = AppendArg(extra, "--flash-attn", s.FlashAttn == false ? "off" : "on");
}
```

Note : sur le build llama-server local benchmarke, `flash-attn` attend une valeur explicite (`--flash-attn on` ou `--flash-attn off`). Ne pas revenir au flag sans valeur.

**C. `LlamaCppProcessManager.BuildArgs` — logger la commande complete**

```csharp
// Apres construction de baseArgs :
ClientLog.Info($"[LlamaCppProcessManager] Starting: \"{exePath}\" {baseArgs}");
// (le log file existe deja via PipeToFileAsync — s'assurer que la premiere ligne du log = commande complete)
```

**D. Suppression strictMode legacy — PERIMETRE EXACT**

- `AppSettings.cs` lignes 189-192 : supprimer bloc `legacyStrict`
  ```csharp
  // SUPPRIMER :
  var legacyStrict = (ls.Values["llm.strictMode"] as bool?) ?? false;
  if (legacyStrict)
      s.ActiveMode = "strict";
  ```
- `AppSettings.cs` lignes 255-259 : supprimer lecture JSON `StrictMode`
  ```csharp
  // SUPPRIMER :
  if (legacyDoc.RootElement.TryGetProperty("StrictMode", ...))
      s.ActiveMode = ...;
  ```
- `Provisioning.cs` ligne 42 : supprimer `[property: JsonPropertyName("StrictMode")] bool? LegacyStrictMode`
- `Provisioning.cs` ligne 154 : supprimer `else if (dto.Llm?.LegacyStrictMode is bool sm)`
- `AppSettings.cs` ligne 333 : `ls.Values.Remove("llm.strictMode")` — garder (c'est le nettoyage actif, utile)

**NE PAS TOUCHER :**
- `Controls/UserSettingsDialog.cs` (source compilee du dialog) — le toggle `StrictModeToggle` lit/ecrit `ActiveMode` ("strict"/"auto"), systeme valide
- `Controls/UserSettingsDialog.xaml` et `Controls/UserSettingsDialog.xaml.cs` — non compiles (exclus dans le `.csproj`, `<Compile Remove=.../>`)

### Pieges a eviter

- Le build llama-server local attend `--flash-attn on/off`; ne pas ecrire un flag sans valeur.
- Verifier que `IsGpuRuntimePath` detecte correctement le build CUDA (contient "cuda" dans le chemin)
- Ne pas supprimer la ligne `ls.Values.Remove("llm.strictMode")` dans `AppSettings.Save()` — c'est le nettoyage actif des anciennes cles
- `ThreadsBatch` default doit etre coherent avec `Threads` — en Phase 0 : utiliser la meme valeur que `threads` par defaut

### Nouveaux tests a ajouter

```
[Fact]
public void ApplyAutoTuningFlags_CudaRuntime_InjectsUbatchThreadsBatchFlashAttn()
{
    var s = new AppSettings { LlamaExePath = @"C:\...\win-cuda-x64\llama-server.exe", UbatchSize = 256, ThreadsBatch = 6 };
    LocalLlmBootstrapper.ApplyAutoTuningFlags_TestHook(s, cudaGpu);
    Assert.Contains("--ubatch-size 256", s.ExtraArgs);
    Assert.Contains("--threads-batch 6", s.ExtraArgs);
    Assert.Contains("--flash-attn on", s.ExtraArgs);
}

[Fact]
public void ApplyAutoTuningFlags_CpuRuntime_NoFlashAttnNoNgl()
{
    var s = new AppSettings { LlamaExePath = @"C:\...\cpu\llama-server.exe" };
    LocalLlmBootstrapper.ApplyAutoTuningFlags_TestHook(s, cpuGpu);
    Assert.DoesNotContain("--flash-attn", s.ExtraArgs);
    Assert.DoesNotContain("-ngl", s.ExtraArgs);  // ngl=0 pour CPU
}
```

### Validation Patch 2

```bash
dotnet build client/SAAIA.Client.WinUI/SAAIA.Client.WinUI.csproj -p:Platform=x64 -p:Configuration=Debug
dotnet test client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj -p:NuGetAudit=false
dotnet test backend/SAAIA.Backend.Tests/SAAIA.Backend.Tests.csproj -p:NuGetAudit=false
```

**Criteres de sortie Patch 2 :**
- Args CUDA contiennent `--ubatch-size`, `--threads-batch`, `--flash-attn on/off`
- Args CPU ne contiennent pas `--flash-attn`
- Ligne de commande complete loggee au demarrage
- Legacy `strictMode` supprime de AppSettings et Provisioning
- Tests client verts, 333 tests backend inchanges

---

## Patch 3 — POST /admin/support/bundle + SHA-256 modeles connus

**Independant de Patch 1/2. Peut etre fait en parallele.**

### Contexte

L'endpoint `POST /admin/support/bundle` est absent du backend (`AdminRuntimeEndpoints.cs` en a 30+ mais pas celui-la).
`SupportBundleBuilder` client leger existe deja et produit un bundle local.
La chaine SHA-256 (`DownloadManager`) existe mais n'est pas alimentee (`Sha256Hex = null` partout).

### Fichiers a modifier

| Fichier | Changement |
|---|---|
| `backend/SAAIA.Backend/Endpoints/AdminRuntimeEndpoints.cs` | Ajouter `POST /admin/support/bundle` |
| `client/SAAIA.Client.WinUI/Services/LocalLlmBootstrapper.cs` | Renseigner Sha256Hex modeles connus |
| `client/SAAIA.Client.WinUI/Services/LlamaCppReleaseDownloader.cs` | Renseigner Sha256Hex runtimes connus |

### Travaux exacts

**A. Backend — `POST /admin/support/bundle`**

Dans `AdminRuntimeEndpoints.Map()`, ajouter :
```csharp
app.MapPost("/admin/support/bundle", SupportBundleAsync);
```

Implementation :
```csharp
internal static async Task<IResult> SupportBundleAsync(
    HttpContext ctx,
    IHostEnvironment env,
    IOptions<RuntimeGovernanceOptions> options)
{
    AdminAuth.EnsureAdmin(ctx);

    var bundleDir = Path.Combine(env.ContentRootPath, "support-bundles");
    Directory.CreateDirectory(bundleDir);
    var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss");
    var bundlePath = Path.Combine(bundleDir, $"admin-bundle_{stamp}.zip");

    var artifacts = new List<string>();
    var missing = new List<string>();

    // Inclure ce qui existe, ignorer ce qui manque (Phase 0B)
    // Les artefacts governance complets arrivent en Phase 3
    var governanceFiles = new[]
    {
        "warmup_results.json",
        "hardware_probe.json",
        "capability_state.json",
        "last_known_good_profile.json",
        "blacklist.json",
        "acquisition_log.json"
    };

    var staging = Path.Combine(bundleDir, $"staging_{Guid.NewGuid():N}");
    Directory.CreateDirectory(staging);

    try
    {
        // README
        File.WriteAllText(Path.Combine(staging, "README.txt"),
            $"SAAIA admin support bundle (redacted) — {stamp}\r\n" +
            "Contains runtime governance artifacts. Sensitive fields are masked.\r\n");

        // Artefacts governance si existants
        var governanceDir = Path.Combine(env.ContentRootPath, "governance");
        foreach (var f in governanceFiles)
        {
            var src = Path.Combine(governanceDir, f);
            if (File.Exists(src)) { File.Copy(src, Path.Combine(staging, f)); artifacts.Add(f); }
            else missing.Add(f);
        }

        // Config redactee (options runtime)
        var configSnapshot = new { timestamp = stamp, missingGovernanceArtifacts = missing };
        File.WriteAllText(Path.Combine(staging, "runtime-config.json"),
            System.Text.Json.JsonSerializer.Serialize(configSnapshot, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        artifacts.Add("runtime-config.json");

        System.IO.Compression.ZipFile.CreateFromDirectory(staging, bundlePath);

        return Results.Ok(new { bundlePath, artifacts, missingArtifacts = missing });
    }
    finally
    {
        try { Directory.Delete(staging, recursive: true); } catch { }
    }
}
```

**B. SHA-256 — renseigner les valeurs connues**

Dans `LocalLlmBootstrapper.GetModelCandidates`, remplacer `Sha256Hex: null` par les SHA-256 reels.

Pour les obtenir : calculer sur la machine de reference apres telechargement.

```csharp
// Exemple (remplacer par les valeurs reelles apres calcul) :
private const string QwenQ4_K_M_Sha256 = "/* sha256 a renseigner */";
// ...
new ModelSpec(QwenRepo, QwenQ4_K_M, QwenQ4_K_M_Sha256)
```

Si le SHA n'est pas encore connu : garder `null` mais logguer un warning explicite :
```csharp
if (picked.Sha256Hex is null)
    ClientLog.Warn($"[Bootstrap] No SHA-256 known for '{picked.File}' — checksum skipped.");
```

Meme chose dans `LlamaCppReleaseDownloader` pour les runtimes.

### Test contractuel a ajouter (backend)

```csharp
[Fact]
public async Task PostAdminSupportBundle_Returns200_WithBundlePath()
{
    var resp = await _client.PostAsync("/admin/support/bundle",
        null, _adminHeaders);
    Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
    Assert.True(body.TryGetProperty("bundlePath", out _));
    Assert.True(body.TryGetProperty("artifacts", out _));
}

[Fact]
public async Task PostAdminSupportBundle_Without_AdminKey_Returns401()
{
    var resp = await _client.PostAsync("/admin/support/bundle", null);
    Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
}
```

### Validation Patch 3

```bash
dotnet build backend/SAAIA.Backend/SAAIA.Backend.csproj
dotnet test backend/SAAIA.Backend.Tests/SAAIA.Backend.Tests.csproj -p:NuGetAudit=false
# Cible : >= 335 tests verts (333 + 2 nouveaux contractuels)
```

**Criteres de sortie Patch 3 :**
- `POST /admin/support/bundle` repond 200 avec `bundlePath` et liste `artifacts`
- 401/403 sans `X-Admin-Key`
- Warning logge si SHA-256 absent pour un modele connu
- Tests contractuels en place et verts

---

## Rappel — ce qui N'EST PAS dans ces patchs

Les elements suivants sont Phase 3 (Patch 4/5) et ne doivent pas etre touches maintenant :
- Warmup gate formel (TTFT mesure, PASS/FAIL_BLOCK/FAIL_FALLBACK)
- Blacklist / quarantaine
- Rollback / lastKnownGoodProfile
- Requalification triggers
- DXGI budget observe
- QoS Perf/Balanced/Eco
- Artefacts JSON client governance complets
- Sleep/wake idleTimeoutSeconds

---

## Commandes de validation globales (post-Patch 1+2+3)

```bash
# Build complet
dotnet build backend/SAAIA.Backend/SAAIA.Backend.csproj
dotnet build client/SAAIA.Client.WinUI/SAAIA.Client.WinUI.csproj -p:Platform=x64 -p:Configuration=Debug

# Tests
dotnet test backend/SAAIA.Backend.Tests/SAAIA.Backend.Tests.csproj -p:NuGetAudit=false -nologo -m:1
dotnet test client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj -p:NuGetAudit=false -nologo

# Verification smoke (optionnel si env disponible)
# - Lancer llama-server avec les nouveaux args
# - Verifier que ngl=36, batch=1024, ubatch=256, threads-batch=6 apparaissent dans le log
# - Verifier TTFT < 12 000 ms sur le prompt de reference
```
