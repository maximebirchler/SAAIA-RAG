using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

internal sealed record NvidiaGpuInfo(string Name, int VramMiB, string DriverVersion);

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
    private const long   MaxScanBytes = 2 * 1024 * 1024; // 2 MB
    private const ulong  MaxKvPairs   = 512;

    // GGUF value type constants (gguf_type_t)
    private const uint TUint8   = 0;
    private const uint TInt8    = 1;
    private const uint TUint16  = 2;
    private const uint TInt16   = 3;
    private const uint TUint32  = 4;
    private const uint TInt32   = 5;
    private const uint TFloat32 = 6;
    private const uint TBool    = 7;
    private const uint TString  = 8;
    private const uint TArray   = 9;
    private const uint TUint64  = 10;
    private const uint TInt64   = 11;
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

            uint? blockCount   = null;
            uint? headCountKv  = null;
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

                var keyBytes  = br.ReadBytes((int)keyLen);
                var key       = Encoding.UTF8.GetString(keyBytes);
                var valueType = br.ReadUInt32();

                if (string.Equals(key, "llm.block_count", StringComparison.Ordinal) && valueType == TUint32)
                    blockCount = br.ReadUInt32();
                else if (string.Equals(key, "llm.attention.head_count_kv", StringComparison.Ordinal) && valueType == TUint32)
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
                var count    = br.ReadUInt64();
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
            TUint16 or TInt16        => 2,
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

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return false;

            var line = proc.StandardOutput.ReadLine();
            proc.WaitForExit(3000);

            if (string.IsNullOrWhiteSpace(line)) return false;

            var parts = line.Split(',').Select(p => p.Trim()).ToArray();
            if (parts.Length < 2) return false;

            var name = parts[0];
            _ = int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var vramMiB);
            var drv = parts.Length >= 3 ? parts[2] : "";

            info = new NvidiaGpuInfo(name, vramMiB, drv);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Try to detect the "best" GPU on the machine.
    /// - NVIDIA: uses nvidia-smi (reliable VRAM).
    /// - Otherwise: uses Win32_VideoController via PowerShell (CIM) to get Name + AdapterRAM + PNPDeviceID.
    /// Notes:
    /// - iGPU/APU often reports shared memory; we treat integrated GPUs as DedicatedVramBytes=0 (conservative).
    /// </summary>
    public static async Task<GpuInfo?> TryGetBestGpuAsync(CancellationToken ct)
    {
        // 1) NVIDIA
        if (TryGetNvidia(out var n) && n.VramMiB > 0)
        {
            return new GpuInfo(
                Vendor: GpuVendor.Nvidia,
                Name: n.Name,
                DedicatedVramBytes: (long)n.VramMiB * 1024L * 1024L,
                IsIntegrated: false,
                DetectionSource: "nvidia-smi");
        }

        // 2) CIM (PowerShell)
        try
        {
            // JSON list of video controllers
            // Using PowerShell avoids System.Management dependency.
            var cmd = "Get-CimInstance Win32_VideoController | Select-Object Name,AdapterRAM,PNPDeviceID | ConvertTo-Json";

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
            if (proc is null) return null;

            var json = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(json)) return null;

            using var doc = JsonDocument.Parse(json);

            // Could be a single object or an array
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToArray()
                : new[] { doc.RootElement };

            if (items.Length == 0) return null;

            GpuInfo? best = null;

            foreach (var el in items)
            {
                var name = el.TryGetProperty("Name", out var pn) ? (pn.GetString() ?? "") : "";
                var pnp = el.TryGetProperty("PNPDeviceID", out var pp) ? (pp.GetString() ?? "") : "";

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

                var info = new GpuInfo(vendor, string.IsNullOrWhiteSpace(name) ? "Unknown GPU" : name, dedicated, integrated, "cim");

                // Choose the controller with highest dedicated VRAM.
                if (best is null || info.DedicatedVramBytes > best.DedicatedVramBytes)
                    best = info;
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

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
        var batch   = BatchFromVram(vramMiB);
        var ngl     = NglFromGgufOrVram(ggufPath, vramMiB, nvidia.Name);
        return (threads, batch, ngl);
    }

    /// <summary>
    /// Auto-tuning for any GPU vendor (NVIDIA / AMD / Intel discrete / iGPU / CPU).
    /// Pass <paramref name="ggufPath"/> so <c>ngl</c> is read from the model's
    /// <c>llm.block_count</c> metadata (CDC v3.1 LLM-005).
    /// </summary>
    public static (int threads, int batch, int ngl) ComputeAutoTuning(GpuInfo? gpu, string? ggufPath = null)
    {
        var vramMiB  = gpu?.DedicatedVramMiB ?? 0;
        var hasGpu   = gpu is not null && !gpu.IsIntegrated && vramMiB > 0;

        if (!hasGpu)
            return (Math.Clamp(Environment.ProcessorCount - 2, 4, 10), 128, 0);

        var threads = ThreadsFromVram(vramMiB);
        var batch   = BatchFromVram(vramMiB);
        var ngl     = NglFromGgufOrVram(ggufPath, vramMiB, gpu!.Name);
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
        if (vramMiB <= 0)     return 0;
        if (vramMiB <= 5120)  return 24;
        if (vramMiB <= 7168)  return 32;
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
        if (vramMiB <= 0)     return 128;  // CPU-only — no LLM-009 constraint
        if (vramMiB > 14336)  return 1024; // High-VRAM cards — match bench Profile B/C
        return 512;                        // All other GPU tiers (LLM-009 minimum)
    }

    /// <summary>Thread count per VRAM tier (unchanged from original).</summary>
    private static int ThreadsFromVram(int vramMiB)
    {
        var cpu = Environment.ProcessorCount;
        if (vramMiB <= 5120)  return Math.Clamp(cpu - 2, 4, 8);
        if (vramMiB <= 7168)  return Math.Clamp(cpu - 3, 4, 8);
        if (vramMiB <= 10240) return Math.Clamp(cpu - 3, 4, 10);
        return Math.Clamp(cpu - 4, 4, 10);
    }
}
