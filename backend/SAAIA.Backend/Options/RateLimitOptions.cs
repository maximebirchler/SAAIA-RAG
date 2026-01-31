sealed class RateLimitOptions
{
    /// <summary>Nombre de requêtes autorisées par fenêtre (par clé API).</summary>
    public int PermitLimit { get; set; } = 120;

    /// <summary>Durée de la fenêtre en secondes.</summary>
    public int WindowSeconds { get; set; } = 60;

    /// <summary>Nombre de requêtes en queue si la limite est atteinte.</summary>
    public int QueueLimit { get; set; } = 0;
}
