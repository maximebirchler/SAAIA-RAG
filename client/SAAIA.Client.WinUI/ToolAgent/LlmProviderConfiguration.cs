using System.Globalization;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

internal sealed class LlmProviderConfiguration
{
    internal const string ModeEnvironmentVariable = "SAAIA_LLM_PROVIDER_MODE";
    internal const string PolicyEnvironmentVariable = "SAAIA_LLM_EXTERNAL_POLICY";
    internal const string ConfigPathEnvironmentVariable = "SAAIA_LLM_CONFIG_PATH";
    internal const string OpenAiKeyEnvironmentVariable = "SAAIA_OPENAI_API_KEY";
    internal const string RunPodKeyEnvironmentVariable = "SAAIA_RUNPOD_API_KEY";

    internal required LlmProviderMode Mode { get; init; }
    internal required LlmExternalExecutionPolicy ExternalPolicy { get; init; }
    internal required OpenAiConfiguration OpenAi { get; init; }
    internal required RunPodConfiguration RunPod { get; init; }
    internal required string SourcePath { get; init; }

    internal sealed record OpenAiConfiguration(
        string BaseUrl,
        string ModelId,
        string ApiKeyEnvironmentVariable,
        string ReasoningEffort,
        int RequestTimeoutSeconds,
        LlmPricingMetadata Pricing,
        LlmBudgetOptions Budget);

    internal sealed record RunPodConfiguration(
        string BaseUrl,
        string ModelId,
        string ApiKeyEnvironmentVariable,
        string Runtime,
        string RuntimeProfile,
        LlmRuntimeProfileMetadata RuntimeParameters,
        int RequestTimeoutSeconds);

    internal static LlmProviderConfiguration Load(string? explicitPath = null)
    {
        var path = ResolveConfigurationPath(explicitPath);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var openAi = root.GetProperty("openAi");
        var pricing = openAi.GetProperty("pricingUsdPerMillionTokens");
        var budget = openAi.GetProperty("budget");
        var runPod = root.GetProperty("runPod");
        var runPodRuntimeParameters = runPod.GetProperty("runtimeParameters");

        var modeText = Environment.GetEnvironmentVariable(ModeEnvironmentVariable)
                       ?? ReadRequiredString(root, "mode");
        var policyText = Environment.GetEnvironmentVariable(PolicyEnvironmentVariable)
                         ?? ReadRequiredString(root, "externalPolicy");
        var ledgerPath = Environment.GetEnvironmentVariable("SAAIA_OPENAI_USAGE_LEDGER_PATH");
        if (string.IsNullOrWhiteSpace(ledgerPath))
        {
            ledgerPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SAAIA",
                "llm-dev",
                "openai-terra-usage.jsonl");
        }

        var configuration = new LlmProviderConfiguration
        {
            Mode = ParseMode(modeText),
            ExternalPolicy = ParsePolicy(policyText),
            SourcePath = path,
            OpenAi = new OpenAiConfiguration(
                Environment.GetEnvironmentVariable("SAAIA_OPENAI_BASE_URL")
                ?? ReadRequiredString(openAi, "baseUrl"),
                Environment.GetEnvironmentVariable("SAAIA_OPENAI_MODEL")
                ?? ReadRequiredString(openAi, "modelId"),
                ReadRequiredString(openAi, "apiKeyEnvironmentVariable"),
                Environment.GetEnvironmentVariable("SAAIA_OPENAI_REASONING_EFFORT")
                ?? ReadRequiredString(openAi, "reasoningEffort"),
                ReadIntEnvironment("SAAIA_OPENAI_REQUEST_TIMEOUT_SECONDS", openAi, "requestTimeoutSeconds"),
                new LlmPricingMetadata(
                    ReadDecimal(pricing, "input"),
                    ReadDecimal(pricing, "cachedInput"),
                    ReadDecimal(pricing, "cacheWrite"),
                    ReadDecimal(pricing, "output")),
                new LlmBudgetOptions(
                    ReadDecimalEnvironment("SAAIA_OPENAI_BUDGET_USD", budget, "authorizedUsd"),
                    ReadDecimalEnvironment("SAAIA_OPENAI_SOFT_LIMIT_USD", budget, "softLimitUsd"),
                    ReadDecimalEnvironment("SAAIA_OPENAI_HARD_LIMIT_USD", budget, "hardLimitUsd"),
                    ReadDecimalEnvironment("SAAIA_OPENAI_MAX_COST_PER_TURN_USD", budget, "maximumCostPerTurnUsd"),
                    ReadIntEnvironment("SAAIA_OPENAI_MAX_CALLS_PER_TURN", budget, "maximumCallsPerTurn"),
                    ledgerPath)),
            RunPod = new RunPodConfiguration(
                Environment.GetEnvironmentVariable("SAAIA_RUNPOD_BASE_URL")
                ?? ReadOptionalString(runPod, "baseUrl"),
                Environment.GetEnvironmentVariable("SAAIA_RUNPOD_MODEL")
                ?? ReadOptionalString(runPod, "modelId"),
                ReadRequiredString(runPod, "apiKeyEnvironmentVariable"),
                ReadRequiredString(runPod, "runtime"),
                Environment.GetEnvironmentVariable("SAAIA_RUNPOD_RUNTIME_PROFILE")
                ?? ReadOptionalString(runPod, "runtimeProfile"),
                new LlmRuntimeProfileMetadata(
                    ReadOptionalEnvironment("SAAIA_RUNPOD_MODEL_PATH", runPodRuntimeParameters, "modelPath"),
                    ReadOptionalEnvironment("SAAIA_RUNPOD_QUANTIZATION", runPodRuntimeParameters, "quantization"),
                    ReadOptionalPositiveIntEnvironment("SAAIA_RUNPOD_CTX_SIZE", runPodRuntimeParameters, "contextSize"),
                    ReadOptionalPositiveIntEnvironment("SAAIA_RUNPOD_BATCH_SIZE", runPodRuntimeParameters, "batchSize"),
                    ReadOptionalPositiveIntEnvironment("SAAIA_RUNPOD_UBATCH_SIZE", runPodRuntimeParameters, "ubatchSize"),
                    ReadOptionalPositiveIntEnvironment("SAAIA_RUNPOD_THREADS", runPodRuntimeParameters, "threads"),
                    ReadOptionalPositiveIntEnvironment("SAAIA_RUNPOD_THREADS_BATCH", runPodRuntimeParameters, "threadsBatch"),
                    ReadOptionalNonNegativeIntEnvironment("SAAIA_RUNPOD_GPU_LAYERS", runPodRuntimeParameters, "gpuLayers"),
                    ReadOptionalBooleanEnvironment("SAAIA_RUNPOD_FLASH_ATTN", runPodRuntimeParameters, "flashAttention")),
                ReadIntEnvironment("SAAIA_RUNPOD_REQUEST_TIMEOUT_SECONDS", runPod, "requestTimeoutSeconds"))
        };
        configuration.Validate();
        return configuration;
    }

    internal void Validate()
    {
        if (Mode == LlmProviderMode.OpenAiDev)
        {
            if (ExternalPolicy is not (LlmExternalExecutionPolicy.DevelopmentExternalAllowed
                or LlmExternalExecutionPolicy.BenchmarkExternalAllowed))
            {
                throw new InvalidOperationException(
                    "OpenAiDev requires DevelopmentExternalAllowed or BenchmarkExternalAllowed policy.");
            }
            ValidateEndpoint(OpenAi.BaseUrl, "OpenAI");
            if (string.IsNullOrWhiteSpace(OpenAi.ModelId))
                throw new InvalidOperationException("OpenAI modelId is required.");
            if (!string.Equals(OpenAi.ModelId, "gpt-5.6-terra", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SAAIA_OPENAI_MODEL")))
            {
                throw new InvalidOperationException(
                    "The committed OpenAiDev baseline must remain gpt-5.6-terra; use SAAIA_OPENAI_MODEL for an explicit experiment.");
            }
        }
        else if (Mode == LlmProviderMode.RunPodBench)
        {
            if (ExternalPolicy != LlmExternalExecutionPolicy.BenchmarkExternalAllowed)
            {
                throw new InvalidOperationException(
                    "RunPodBench requires BenchmarkExternalAllowed policy.");
            }
            ValidateEndpoint(RunPod.BaseUrl, "RunPod");
            if (string.IsNullOrWhiteSpace(RunPod.ModelId))
                throw new InvalidOperationException("RunPod modelId is required.");
        }
    }

    private static void ValidateEndpoint(string raw, string provider)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
        {
            throw new InvalidOperationException(
                provider + " baseUrl must be HTTPS (HTTP is accepted only for a loopback test endpoint).");
        }
    }

    private static LlmProviderMode ParseMode(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "local" => LlmProviderMode.Local,
            "openaidev" or "openai-dev" => LlmProviderMode.OpenAiDev,
            "runpodbench" or "runpod-bench" => LlmProviderMode.RunPodBench,
            _ => throw new InvalidOperationException(
                $"{ModeEnvironmentVariable} must be Local, OpenAiDev or RunPodBench.")
        };

    private static LlmExternalExecutionPolicy ParsePolicy(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "productionlocal" or "production-local" => LlmExternalExecutionPolicy.ProductionLocal,
            "developmentexternalallowed" or "development-external-allowed" =>
                LlmExternalExecutionPolicy.DevelopmentExternalAllowed,
            "benchmarkexternalallowed" or "benchmark-external-allowed" =>
                LlmExternalExecutionPolicy.BenchmarkExternalAllowed,
            _ => throw new InvalidOperationException(
                $"{PolicyEnvironmentVariable} has an invalid value.")
        };

    private static string ResolveConfigurationPath(string? explicitPath)
    {
        var configured = explicitPath;
        if (string.IsNullOrWhiteSpace(configured))
            configured = Environment.GetEnvironmentVariable(ConfigPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.GetFullPath(configured);
            if (!File.Exists(full))
                throw new FileNotFoundException("LLM provider configuration was not found.", full);
            return full;
        }

        var direct = Path.Combine(AppContext.BaseDirectory, "config", "llm-providers.dev.json");
        if (File.Exists(direct))
            return direct;

        var current = new DirectoryInfo(Environment.CurrentDirectory);
        for (var depth = 0; current is not null && depth < 8; depth++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "config", "llm-providers.dev.json");
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            "config/llm-providers.dev.json was not found. Set " + ConfigPathEnvironmentVariable + ".");
    }

    private static string ReadRequiredString(JsonElement parent, string name)
    {
        var value = ReadOptionalString(parent, name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"LLM provider configuration '{name}' is required.")
            : value;
    }

    private static string ReadOptionalString(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static decimal ReadDecimal(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var property)
           && property.TryGetDecimal(out var value)
           && value >= 0
            ? value
            : throw new InvalidOperationException($"LLM provider configuration '{name}' must be a non-negative number.");

    private static decimal ReadDecimalEnvironment(string variable, JsonElement parent, string name)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(raw))
            return ReadDecimal(parent, name);
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
               && value >= 0
            ? value
            : throw new InvalidOperationException(variable + " must be a non-negative decimal number.");
    }

    private static int ReadIntEnvironment(string variable, JsonElement parent, string name)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return parent.TryGetProperty(name, out var property)
                   && property.TryGetInt32(out var configured)
                   && configured > 0
                ? configured
                : throw new InvalidOperationException($"LLM provider configuration '{name}' must be a positive integer.");
        }
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
               && value > 0
            ? value
            : throw new InvalidOperationException(variable + " must be a positive integer.");
    }

    private static string? ReadOptionalEnvironment(string variable, JsonElement parent, string name)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        var value = string.IsNullOrWhiteSpace(raw) ? ReadOptionalString(parent, name) : raw.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int? ReadOptionalPositiveIntEnvironment(string variable, JsonElement parent, string name)
        => ReadOptionalIntEnvironment(variable, parent, name, allowZero: false);

    private static int? ReadOptionalNonNegativeIntEnvironment(string variable, JsonElement parent, string name)
        => ReadOptionalIntEnvironment(variable, parent, name, allowZero: true);

    private static int? ReadOptionalIntEnvironment(
        string variable,
        JsonElement parent,
        string name,
        bool allowZero)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                && (allowZero ? value >= 0 : value > 0))
            {
                return value;
            }
            throw new InvalidOperationException(
                variable + (allowZero ? " must be a non-negative integer." : " must be a positive integer."));
        }

        if (!parent.TryGetProperty(name, out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (!property.TryGetInt32(out var configured))
            throw new InvalidOperationException($"LLM provider configuration '{name}' must be an integer.");
        if (configured == 0)
            return null;
        if (configured < 0)
            throw new InvalidOperationException($"LLM provider configuration '{name}' cannot be negative.");
        return configured;
    }

    private static bool? ReadOptionalBooleanEnvironment(string variable, JsonElement parent, string name)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (bool.TryParse(raw, out var value))
                return value;
            throw new InvalidOperationException(variable + " must be true or false.");
        }
        if (!parent.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
            return null;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidOperationException($"LLM provider configuration '{name}' must be true, false or null.")
        };
    }
}

internal static class LlmProviderFactory
{
    internal static ILlmProvider Create(
        OpenAiLlmClient transport,
        AppSettings settings,
        LlmProviderConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(settings);
        configuration ??= LlmProviderConfiguration.Load();
        configuration.Validate();

        return configuration.Mode switch
        {
            LlmProviderMode.Local => CreateLocal(transport, settings),
            LlmProviderMode.OpenAiDev => CreateOpenAi(transport, configuration),
            LlmProviderMode.RunPodBench => CreateRunPod(transport, configuration),
            _ => throw new InvalidOperationException("Unsupported LLM provider mode.")
        };
    }

    internal static ILlmProvider CreateLocal(OpenAiLlmClient transport, AppSettings settings)
        => CreateLocal(
            transport,
            settings.LlmBaseUrl,
            settings.ModelId,
            settings.QualifiedProfile?.ProfileId);

    internal static ILlmProvider CreateLocal(
        OpenAiLlmClient transport,
        string baseUrl,
        string? modelId,
        string? runtimeProfile = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var model = string.IsNullOrWhiteSpace(modelId)
            ? ClientDefaults.LlmModel
            : modelId.Trim();
        transport.ConfigureEndpoint(new OpenAiCompatibleEndpointOptions(
            baseUrl,
            model,
            "local",
            apiKey: null,
            OpenAiCompatibleDialect.LlamaCpp,
            reasoningEffort: null,
            ensureLocalRuntime: true));
        return new LocalLlmProvider(
            transport,
            new LlmProviderDescriptor(
                LlmProviderMode.Local,
                "local",
                "llama.cpp",
                model,
                runtimeProfile,
                IsExternal: false,
                IsDevelopmentOnly: false));
    }

    private static ILlmProvider CreateOpenAi(
        OpenAiLlmClient transport,
        LlmProviderConfiguration configuration)
    {
        var key = ReadRequiredSecret(
            configuration.OpenAi.ApiKeyEnvironmentVariable,
            "OpenAI",
            SecureLocalStore.GetOpenAiApiKey);
        transport.ConfigureEndpoint(new OpenAiCompatibleEndpointOptions(
            configuration.OpenAi.BaseUrl,
            configuration.OpenAi.ModelId,
            "openai",
            key,
            OpenAiCompatibleDialect.OpenAi,
            configuration.OpenAi.ReasoningEffort,
            ensureLocalRuntime: false));
        var descriptor = new LlmProviderDescriptor(
            LlmProviderMode.OpenAiDev,
            "openai",
            "openai-api",
            configuration.OpenAi.ModelId,
            RuntimeProfile: null,
            IsExternal: true,
            IsDevelopmentOnly: true,
            RuntimeParameters: null,
            ContextWindowTokens: 1_050_000);
        return new OpenAiDevLlmProvider(
            transport,
            descriptor,
            new LlmCostBudgetGuard(
                descriptor,
                configuration.OpenAi.Pricing,
                configuration.OpenAi.Budget),
            TimeSpan.FromSeconds(configuration.OpenAi.RequestTimeoutSeconds));
    }

    private static ILlmProvider CreateRunPod(
        OpenAiLlmClient transport,
        LlmProviderConfiguration configuration)
    {
        var key = ReadRequiredSecret(
            configuration.RunPod.ApiKeyEnvironmentVariable,
            "RunPod",
            SecureLocalStore.GetRunPodApiKey);
        transport.ConfigureEndpoint(new OpenAiCompatibleEndpointOptions(
            configuration.RunPod.BaseUrl,
            configuration.RunPod.ModelId,
            "runpod",
            key,
            OpenAiCompatibleDialect.LlamaCpp,
            reasoningEffort: null,
            ensureLocalRuntime: false));
        return new RunPodBenchLlmProvider(
            transport,
            new LlmProviderDescriptor(
                LlmProviderMode.RunPodBench,
                "runpod",
                string.IsNullOrWhiteSpace(configuration.RunPod.Runtime)
                    ? "llama.cpp"
                    : configuration.RunPod.Runtime,
                configuration.RunPod.ModelId,
                configuration.RunPod.RuntimeProfile,
                IsExternal: true,
                IsDevelopmentOnly: true,
                RuntimeParameters: configuration.RunPod.RuntimeParameters,
                ContextWindowTokens: configuration.RunPod.RuntimeParameters.ContextSize),
            TimeSpan.FromSeconds(configuration.RunPod.RequestTimeoutSeconds));
    }

    private static string ReadRequiredSecret(
        string variable,
        string provider,
        Func<string?> secureStoreFallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (value is null)
            value = secureStoreFallback();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                provider + " API key is missing. Set environment variable " + variable
                + " or save it in the SAAIA secure local store.");
        }
        return value.Trim();
    }
}
