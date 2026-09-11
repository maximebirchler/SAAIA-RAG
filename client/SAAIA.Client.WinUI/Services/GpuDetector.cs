using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

internal sealed record NvidiaGpuInfo(string Name, int VramMiB, string DriverVersion)
{
    public int? Index { get; init; }
    public string? Uuid { get; init; }
    public string? PciBusId { get; init; }
}

internal enum GpuVendor
{
    Unknown = 0,
    Nvidia = 1,
    Amd = 2,
    Intel = 3,
}

internal sealed record GpuInfo(
    GpuVendor Vendor,
    string Name,
    long DedicatedVramBytes,
    bool IsIntegrated,
    string DetectionSource)
{
    public int DedicatedVramMiB => DedicatedVramBytes > 0 ? (int)(DedicatedVramBytes / 1024 / 1024) : 0;
    public string? PnpDeviceId { get; init; }
    public string? DriverVersion { get; init; }
    public string? RuntimeDeviceHint { get; init; }
    public string? StableDeviceId { get; init; }
}

/// <summary>
/// Reads llama.cpp GGUF metadata keys without loading tensor weights.
/// Only scans the KV section (up to 2 MB) — safe for multi-GB model files.
/// Spec: https://github.com/ggerganov/ggml/blob/master/docs/gguf.md
/// </summary>
internal static class GgufMetadataReader
{
    internal sealed record GgufKeys(uint BlockCount, uint HeadCountKv);

    // "GGUF" as little-endian uint32: bytes 0x47,0x47,0x55,0x46
    private const uint GgufMagicLE = 0x46554747u;

    // Safety caps — we never need to scan more than a handful of KV pairs
    private const long MaxScanBytes = 2 * 1024 * 1024; // 2 MB
    private const ulong MaxKvPairs = 512;

    // GGUF value type constants (gguf_type_t)
    private const uint TUint8 = 0;
    private const uint TInt8 = 1;
    private const uint TUint16 = 2;
    private const uint TInt16 = 3;
    private const uint TUint32 = 4;
    private const uint TInt32 = 5;
    private const uint TFloat32 = 6;
    private const uint TBool = 7;
    private const uint TString = 8;
    private const uint TArray = 9;
    private const uint TUint64 = 10;
    private const uint TInt64 = 11;
    private const uint TFloat64 = 12;

    /// <summary>
    /// Returns null if the file is missing, unreadable, or not a valid GGUF.
    /// <c>HeadCountKv</c> is 0 when the key is absent (no GQA or metadata missing).
    /// </summary>
    public static GgufKeys? TryRead(string ggufPath)
    {
        if (string.IsNullOrWhiteSpace(ggufPath) || !File.Exists(ggufPath))
            return null;

        try
        {
            using var fs = new FileStream(
                ggufPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536);
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);

            // --- Header ---
            var magic = br.ReadUInt32();
            if (magic != GgufMagicLE) return null;

            var version = br.ReadUInt32();
            if (version < 1 || version > 3) return null;

            // GGUF v1 uses uint32 for counts; v2/v3 use uint64
            ulong nKv;
            if (version == 1)
            {
                _ = br.ReadUInt32(); // n_tensors
                nKv = br.ReadUInt32();
            }
            else
            {
                _ = br.ReadUInt64(); // n_tensors
                nKv = br.ReadUInt64();
            }

            uint? blockCount = null;
            uint? headCountKv = null;
            var startPos = fs.Position;

            // --- KV pairs ---
            for (ulong i = 0; i < nKv && i < MaxKvPairs; i++)
            {
                if (fs.Position - startPos > MaxScanBytes) break;
                if (blockCount.HasValue && headCountKv.HasValue) break;

                // Key: uint64 length + UTF-8 bytes
                var keyLen = br.ReadUInt64();
                if (keyLen > 512)
                {
                    // Suspicious key length — skip this entry entirely
                    fs.Seek((long)keyLen, SeekOrigin.Current);
                    SkipValue(br, br.ReadUInt32());
                    continue;
                }

                var keyBytes = br.ReadBytes((int)keyLen);
                var key = Encoding.UTF8.GetString(keyBytes);
                var valueType = br.ReadUInt32();

                if (IsGgufKey(key, "llm.block_count", ".block_count") && valueType == TUint32)
                    blockCount = br.ReadUInt32();
                else if (IsGgufKey(key, "llm.attention.head_count_kv", ".attention.head_count_kv") && valueType == TUint32)
                    headCountKv = br.ReadUInt32();
                else
                    SkipValue(br, valueType);
            }

            return blockCount.HasValue
                ? new GgufKeys(blockCount.Value, headCountKv ?? 0)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsGgufKey(string key, string exact, string architectureSuffix)
        => string.Equals(key, exact, StringComparison.Ordinal)
           || key.EndsWith(architectureSuffix, StringComparison.Ordinal);

    private static void SkipValue(BinaryReader br, uint vType)
    {
        switch (vType)
        {
            case TUint8:
            case TInt8:
            case TBool:
                br.ReadByte();
                break;

            case TUint16:
            case TInt16:
                _ = br.ReadUInt16();
                break;

            case TUint32:
            case TInt32:
            case TFloat32:
                _ = br.ReadUInt32();
                break;

            case TUint64:
            case TInt64:
            case TFloat64:
                _ = br.ReadUInt64();
                break;

            case TString:
                {
                    var len = br.ReadUInt64();
                    br.BaseStream.Seek((long)len, SeekOrigin.Current);
                    break;
                }

            case TArray:
                {
                    var itemType = br.ReadUInt32();
                    var count = br.ReadUInt64();
                    SkipArrayItems(br, itemType, count);
                    break;
                }

            default:
                // Unknown type — propagate so TryRead returns null
                throw new InvalidDataException($"Unknown GGUF value type: {vType}");
        }
    }

    private static void SkipArrayItems(BinaryReader br, uint itemType, ulong count)
    {
        // Fixed-size types: one seek, no loop
        int fixedSize = itemType switch
        {
            TUint8 or TInt8 or TBool => 1,
            TUint16 or TInt16 => 2,
            TUint32 or TInt32 or TFloat32 => 4,
            TUint64 or TInt64 or TFloat64 => 8,
            _ => 0
        };

        if (fixedSize > 0)
        {
            br.BaseStream.Seek((long)count * fixedSize, SeekOrigin.Current);
            return;
        }

        // Variable-size (strings, nested arrays): iterate, capped for safety
        var cap = Math.Min(count, 65536UL);
        for (ulong j = 0; j < cap; j++)
            SkipValue(br, itemType);
    }
}

internal static class GpuDetector
{
    // Backward-compatible helpers (some call sites pass CancellationToken)
    public static Task<bool> HasNvidiaGpuAsync() => Task.FromResult(TryGetNvidia(out _));
    public static Task<bool> HasNvidiaGpuAsync(CancellationToken ct) => Task.FromResult(TryGetNvidia(out _));

    public static bool TryGetNvidia(out NvidiaGpuInfo info)
    {
        info = new NvidiaGpuInfo("Unknown NVIDIA GPU", 0, "");
        if (!TryGetNvidiaAdapters(out var adapters) || adapters.Count == 0)
            return false;

        info = adapters[0];
        return true;
    }

    internal static bool TryGetNvidiaAdapters(out IReadOnlyList<NvidiaGpuInfo> adapters)
    {
        var detected = new List<NvidiaGpuInfo>();
        adapters = detected;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=index,name,memory.total,driver_version,uuid,pci.bus_id --format=csv,noheader,nounits",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return false;

            var output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(3000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            foreach (var line in output.Split(
                         new[] { "\r\n", "\n" },
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(',').Select(static part => part.Trim()).ToArray();
                if (parts.Length < 4)
                    continue;

                _ = int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index);
                _ = int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var vramMiB);
                if (string.IsNullOrWhiteSpace(parts[1]) || vramMiB <= 0)
                    continue;

                detected.Add(new NvidiaGpuInfo(parts[1], vramMiB, parts[3])
                {
                    Index = index,
                    Uuid = parts.Length >= 5 ? EmptyToNull(parts[4]) : null,
                    PciBusId = parts.Length >= 6 ? EmptyToNull(parts[5]) : null
                });
            }

            adapters = detected;
            return detected.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Enumerates every GPU reported by the available vendor and Windows probes.
    /// NVIDIA telemetry is merged with CIM rather than short-circuiting the other
    /// adapters, so heterogeneous machines retain their complete inventory.
    /// Runtime device discovery and measured qualification decide what is usable;
    /// this inventory alone must not be interpreted as a performance ranking.
    /// </summary>
    public static async Task<IReadOnlyList<GpuInfo>> TryGetGpusAsync(CancellationToken ct)
    {
        var cimAdapters = await TryGetCimAdaptersAsync(ct).ConfigureAwait(false);
        if (!TryGetNvidiaAdapters(out var nvidiaAdapters))
            return cimAdapters;

        var merged = cimAdapters.ToList();
        var matchedCimIndices = new HashSet<int>();
        foreach (var nvidia in nvidiaAdapters)
        {
            var cimIndex = FindMatchingNvidiaCimIndex(merged, matchedCimIndices, nvidia);
            if (cimIndex >= 0)
            {
                var cim = merged[cimIndex];
                matchedCimIndices.Add(cimIndex);
                merged[cimIndex] = cim with
                {
                    DedicatedVramBytes = (long)nvidia.VramMiB * 1024L * 1024L,
                    DetectionSource = "nvidia-smi+cim",
                    DriverVersion = EmptyToNull(nvidia.DriverVersion) ?? cim.DriverVersion,
                    RuntimeDeviceHint = nvidia.Index is { } matchedCudaIndex ? $"CUDA{matchedCudaIndex}" : cim.RuntimeDeviceHint,
                    StableDeviceId = EmptyToNull(nvidia.Uuid)
                                     ?? EmptyToNull(nvidia.PciBusId)
                                     ?? cim.StableDeviceId
                                     ?? cim.PnpDeviceId
                };
                continue;
            }

            merged.Add(new GpuInfo(
                Vendor: GpuVendor.Nvidia,
                Name: nvidia.Name,
                DedicatedVramBytes: (long)nvidia.VramMiB * 1024L * 1024L,
                IsIntegrated: false,
                DetectionSource: "nvidia-smi")
            {
                DriverVersion = EmptyToNull(nvidia.DriverVersion),
                RuntimeDeviceHint = nvidia.Index is { } standaloneCudaIndex ? $"CUDA{standaloneCudaIndex}" : null,
                StableDeviceId = EmptyToNull(nvidia.Uuid) ?? EmptyToNull(nvidia.PciBusId)
            });
        }

        return merged;
    }

    /// <summary>
    /// Returns one legacy primary adapter for call sites not yet migrated to the
    /// measured multi-backend qualifier. It never suppresses inventory capture.
    /// </summary>
    public static async Task<GpuInfo?> TryGetBestGpuAsync(CancellationToken ct)
        => SelectLegacyPrimaryGpu(await TryGetGpusAsync(ct).ConfigureAwait(false));

    internal static GpuInfo? SelectLegacyPrimaryGpu(IReadOnlyList<GpuInfo> adapters)
        => adapters
            .OrderByDescending(static gpu => !gpu.IsIntegrated && gpu.DedicatedVramBytes > 0)
            .ThenByDescending(static gpu => gpu.DedicatedVramBytes)
            .ThenByDescending(static gpu => gpu.Vendor == GpuVendor.Nvidia)
            .FirstOrDefault();

    /// <summary>
    /// Reads Win32_VideoController via PowerShell to retain all Windows adapters.
    /// Notes:
    /// - iGPU/APU often reports shared memory; we treat integrated GPUs as DedicatedVramBytes=0 (conservative).
    /// </summary>
    private static async Task<IReadOnlyList<GpuInfo>> TryGetCimAdaptersAsync(CancellationToken ct)
    {
        try
        {
            // JSON list of video controllers
            // Using PowerShell avoids System.Management dependency.
            var cmd = "Get-CimInstance Win32_VideoController | Select-Object Name,AdapterRAM,PNPDeviceID,DriverVersion | ConvertTo-Json";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{cmd}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return Array.Empty<GpuInfo>();

            var json = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<GpuInfo>();

            using var doc = JsonDocument.Parse(json);

            // Could be a single object or an array
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToArray()
                : new[] { doc.RootElement };

            if (items.Length == 0) return Array.Empty<GpuInfo>();

            var adapters = new List<GpuInfo>(items.Length);
            foreach (var el in items)
            {
                var name = el.TryGetProperty("Name", out var pn) ? (pn.GetString() ?? "") : "";
                var pnp = el.TryGetProperty("PNPDeviceID", out var pp) ? (pp.GetString() ?? "") : "";
                var driverVersion = el.TryGetProperty("DriverVersion", out var pd)
                    ? EmptyToNull(pd.GetString())
                    : null;

                long adapterRam = 0;
                if (el.TryGetProperty("AdapterRAM", out var pr))
                {
                    if (pr.ValueKind == JsonValueKind.Number)
                    {
                        _ = pr.TryGetInt64(out adapterRam);
                    }
                    else if (pr.ValueKind == JsonValueKind.String)
                    {
                        _ = long.TryParse(pr.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out adapterRam);
                    }
                }

                var vendor = ParseVendorFromPnp(pnp, name);
                var integrated = IsIntegratedHeuristic(vendor, name);

                var dedicated = integrated ? 0 : Math.Max(0, adapterRam);

                var info = new GpuInfo(vendor, string.IsNullOrWhiteSpace(name) ? "Unknown GPU" : name, dedicated, integrated, "cim")
                {
                    PnpDeviceId = pnp,
                    DriverVersion = driverVersion,
                    StableDeviceId = EmptyToNull(pnp)
                };
                adapters.Add(info);
            }

            return adapters;
        }
        catch
        {
            return Array.Empty<GpuInfo>();
        }
    }

    private static int FindMatchingNvidiaCimIndex(
        IReadOnlyList<GpuInfo> adapters,
        IReadOnlySet<int> alreadyMatched,
        NvidiaGpuInfo nvidia)
    {
        for (var index = 0; index < adapters.Count; index++)
        {
            if (alreadyMatched.Contains(index))
                continue;
            var adapter = adapters[index];
            if (adapter.Vendor != GpuVendor.Nvidia)
                continue;
            if (string.Equals(adapter.Name.Trim(), nvidia.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                return index;
        }

        for (var index = 0; index < adapters.Count; index++)
        {
            if (!alreadyMatched.Contains(index) && adapters[index].Vendor == GpuVendor.Nvidia)
                return index;
        }

        return -1;
    }

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static GpuVendor ParseVendorFromPnp(string pnpDeviceId, string name)
    {
        var p = (pnpDeviceId ?? "").ToUpperInvariant();
        var n = (name ?? "").ToUpperInvariant();

        if (p.Contains("VEN_10DE") || n.Contains("NVIDIA")) return GpuVendor.Nvidia;
        if (p.Contains("VEN_1002") || p.Contains("VEN_1022") || n.Contains("AMD") || n.Contains("RADEON")) return GpuVendor.Amd;
        if (p.Contains("VEN_8086") || n.Contains("INTEL")) return GpuVendor.Intel;

        return GpuVendor.Unknown;
    }

    private static bool IsIntegratedHeuristic(GpuVendor vendor, string name)
    {
        var n = (name ?? "").ToUpperInvariant();

        // Intel iGPU : UHD / Iris / HD Graphics are typically integrated; Arc is discrete.
        if (vendor == GpuVendor.Intel)
        {
            if (n.Contains("ARC")) return false;
            return true;
        }

        // AMD iGPU / APU : often "Radeon(TM) Graphics" without RX/XT.
        if (vendor == GpuVendor.Amd)
        {
            if (n.Contains("RADEON(TM) GRAPHICS")) return true;
            if (n.Contains("VEGA")) return true;
            if (n.Contains("RX") || n.Contains("XT") || n.Contains("PRO")) return false;
        }

        return false;
    }

    // -------------------------------------------------------------------------
    // Auto-tuning — CDC v3.1 §18.4
    //
    // LLM-005 : ngl = llm.block_count from GGUF metadata (not from VRAM tiers)
    // LLM-009 : batch >= 512 for any GPU profile
    //
    // Both overloads accept an optional ggufPath. When supplied, ngl is read
    // from the file's block_count. Falls back to the VRAM tier table when
    // the file is absent or unreadable (e.g. model not yet downloaded).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Auto-tuning for NVIDIA GPUs (legacy overload, kept for backward compatibility).
    /// Prefer <see cref="ComputeAutoTuning(GpuInfo?, string?)"/> for new code.
    /// </summary>
    public static (int threads, int batch, int ngl) ComputeAutoTuning(NvidiaGpuInfo? nvidia, string? ggufPath = null)
    {
        if (nvidia is null || nvidia.VramMiB <= 0)
            return (Math.Clamp(Environment.ProcessorCount - 2, 4, 10), 128, 0);

        var vramMiB = nvidia.VramMiB;
        var threads = ThreadsFromVram(vramMiB);
        var batch = BatchFromVram(vramMiB);
        var ngl = NglFromGgufOrVram(ggufPath, vramMiB, nvidia.Name);
        return (threads, batch, ngl);
    }

    /// <summary>
    /// Auto-tuning for any GPU vendor (NVIDIA / AMD / Intel discrete / iGPU / CPU).
    /// Pass <paramref name="ggufPath"/> so <c>ngl</c> is read from the model's
    /// <c>llm.block_count</c> metadata (CDC v3.1 LLM-005).
    /// </summary>
    public static (int threads, int batch, int ngl) ComputeAutoTuning(GpuInfo? gpu, string? ggufPath = null)
    {
        var vramMiB = gpu?.DedicatedVramMiB ?? 0;
        var hasGpu = gpu is not null && !gpu.IsIntegrated && vramMiB > 0;

        if (!hasGpu)
            return (Math.Clamp(Environment.ProcessorCount - 2, 4, 10), 128, 0);

        var threads = ThreadsFromVram(vramMiB);
        var batch = BatchFromVram(vramMiB);
        var ngl = NglFromGgufOrVram(ggufPath, vramMiB, gpu!.Name);
        return (threads, batch, ngl);
    }

    // ---- Private helpers ----

    /// <summary>
    /// Returns ngl from GGUF block_count when possible; falls back to VRAM tier.
    /// </summary>
    private static int NglFromGgufOrVram(string? ggufPath, int vramMiB, string gpuName)
    {
        if (!string.IsNullOrWhiteSpace(ggufPath))
        {
            var meta = GgufMetadataReader.TryRead(ggufPath);
            if (meta is not null)
            {
                ClientLog.Info($"[GpuDetector] ngl={meta.BlockCount} from GGUF block_count" +
                               $" (head_count_kv={meta.HeadCountKv}, gpu={gpuName})");
                return (int)meta.BlockCount;
            }

            ClientLog.Warn($"[GpuDetector] GGUF unreadable — ngl fallback to VRAM tier" +
                           $" (path={ggufPath}, vramMiB={vramMiB})");
        }

        return FallbackNglFromVram(vramMiB);
    }

    /// <summary>VRAM-tier fallback for ngl (used when GGUF is not yet available).</summary>
    private static int FallbackNglFromVram(int vramMiB)
    {
        if (vramMiB <= 0) return 0;
        if (vramMiB <= 5120) return 24;
        if (vramMiB <= 7168) return 32;
        if (vramMiB <= 10240) return 48;
        if (vramMiB <= 14336) return 72;
        return 99;
    }

    /// <summary>
    /// Batch size per VRAM tier. Enforces batch >= 512 for all GPU profiles (LLM-009).
    /// CPU-only (vramMiB == 0) keeps batch = 128 (no GPU constraint).
    /// </summary>
    private static int BatchFromVram(int vramMiB)
    {
        if (vramMiB <= 0) return 128;  // CPU-only — no LLM-009 constraint
        if (vramMiB > 14336) return 1024; // High-VRAM cards — match bench Profile B/C
        return 512;                        // All other GPU tiers (LLM-009 minimum)
    }

    /// <summary>Thread count per VRAM tier (unchanged from original).</summary>
    private static int ThreadsFromVram(int vramMiB)
    {
        var cpu = Environment.ProcessorCount;
        if (vramMiB <= 5120) return Math.Clamp(cpu - 2, 4, 8);
        if (vramMiB <= 7168) return Math.Clamp(cpu - 3, 4, 8);
        if (vramMiB <= 10240) return Math.Clamp(cpu - 3, 4, 10);
        return Math.Clamp(cpu - 4, 4, 10);
    }
}
