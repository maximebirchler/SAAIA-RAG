using Microsoft.Extensions.Hosting;

namespace SAAIA.Backend.Chat;

/// <summary>
/// Warmup du provider LLM au démarrage.
/// Objectif: éviter le "cold start" (TTFT élevé) sur la première vraie requête utilisateur.
/// Ne bloque pas l'app: si le warmup échoue, /ready indiquera llm=false.
/// </summary>
public sealed class LlmWarmupService : BackgroundService
{
    private readonly ILogger<LlmWarmupService> _log;
    private readonly LlmClient _llm;

    public LlmWarmupService(ILogger<LlmWarmupService> log, LlmClient llm)
    {
        _log = log;
        _llm = llm;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Laisse le temps aux conteneurs (llama.cpp) de monter
        try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
        catch { return; }

        const int attempts = 3;
        for (var i = 1; i <= attempts && !stoppingToken.IsCancellationRequested; i++)
        {
            _log.LogInformation("LLM warmup: attempt {Attempt}/{Attempts}...", i, attempts);

            var ok = await _llm.ProbeAsync(timeoutSeconds: 60, ct: stoppingToken);
            if (ok)
            {
                _log.LogInformation("LLM warmup: OK (provider is responsive).");
                return;
            }

            _log.LogWarning("LLM warmup: not ready yet.");
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch { return; }
        }

        _log.LogWarning("LLM warmup: failed after retries (app continues; /ready will report llm=false).");
    }
}
