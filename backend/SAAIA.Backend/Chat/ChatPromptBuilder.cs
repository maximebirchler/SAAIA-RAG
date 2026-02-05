using Microsoft.Extensions.Options;
using System.Text;

namespace SAAIA.Backend.Chat;

public sealed class ChatPromptBuilder
{
    private readonly IOptions<ChatOptions> _opt;

    public ChatPromptBuilder(IOptions<ChatOptions> opt)
    {
        _opt = opt;
    }

    public string BuildUserMessage(List<RagHit> hits, string userQuestion, string? retrievalConfidence = null)
    {
        var o = _opt.Value;
        userQuestion = (userQuestion ?? "").Trim();

        // ===============================
        // Base instructions (RAG “strict”)
        // ===============================
        var sb = new StringBuilder();

        sb.AppendLine("Tu es un assistant technique. Tu dois répondre UNIQUEMENT à partir des SOURCES fournies.");
        sb.AppendLine("Si l'information n'est pas clairement présente dans les sources, dis-le explicitement.");
        sb.AppendLine();

        // Concision
        sb.AppendLine("CONTRAINTES DE STYLE :");
        sb.AppendLine($"- Réponse concise: максимум {o.ResponseMaxWords} mots.");
        sb.AppendLine($"- максимум {o.ResponseMaxSentences} phrases.");
        sb.AppendLine($"- environ {o.ResponseMaxWordsPerSentence} mots par phrase.");
        sb.AppendLine();

        // Handling confidence (retrieval quality)
        if (!string.IsNullOrWhiteSpace(retrievalConfidence))
        {
            var conf = retrievalConfidence.Trim().ToLowerInvariant();

            if (conf == "low")
            {
                sb.AppendLine("IMPORTANT (pertinence faible) : les extraits récupérés semblent PEU pertinents pour la question.");
                sb.AppendLine("Commence par 1 phrase du type : \"Je n'ai probablement pas trouvé l'information pertinente dans la base\".");
                sb.AppendLine("Ensuite, si tu peux, donne une tentative basée sur les extraits, en précisant que c'est incertain.");
                sb.AppendLine("Ne mentionne PAS de scores ni de détails internes.");
                sb.AppendLine();
            }
            else if (conf == "warn")
            {
                sb.AppendLine("NOTE (pertinence incertaine) : les extraits semblent partiellement pertinents.");
                sb.AppendLine("Si la réponse est incomplète, dis-le clairement.");
                sb.AppendLine("Ne mentionne PAS de scores ni de détails internes.");
                sb.AppendLine();
            }
        }


        // Citations rules
        sb.AppendLine("RÈGLES DE CITATION :");
        sb.AppendLine("- Après chaque phrase factuelle, ajoute la/les citations sous forme [S1] [S2].");
        sb.AppendLine("- N'invente jamais de citation.");
        sb.AppendLine("- Si tu n'utilises pas une source, ne la cite pas.");
        sb.AppendLine();

        // Question
        sb.AppendLine("QUESTION :");
        sb.AppendLine(userQuestion);
        sb.AppendLine();

        // Sources injection
        if (hits is null || hits.Count == 0)
        {
            sb.AppendLine("SOURCES :");
            sb.AppendLine("(aucune source retrouvée)");
            sb.AppendLine();
            sb.AppendLine("Tu as le droit d'utiliser tes connaissances générales.");
            sb.AppendLine("Tu NE dois PAS dire que tu ne peux pas répondre faute de sources.");
            sb.AppendLine("N'ajoute AUCUNE citation dans ce mode.");
            return sb.ToString().TrimEnd();
        }

        var maxSources = Math.Clamp(o.PromptMaxSources, 1, 20);
        var maxChars = Math.Clamp(o.PromptMaxCharsPerSource, 200, 5000);

        sb.AppendLine("SOURCES :");
        for (int i = 0; i < hits.Count && i < maxSources; i++)
        {
            var h = hits[i];
            var sid = $"S{i + 1}";
            var title = h.DocName ?? h.DocPath ?? "(document inconnu)";
            var pages = (h.PageStart is not null || h.PageEnd is not null)
                ? $"p.{h.PageStart?.ToString() ?? "?"}-{h.PageEnd?.ToString() ?? "?"}"
                : "p.?";

            var chunk = h.ChunkIndex is not null ? $"chunk {h.ChunkIndex}" : "chunk ?";
            var excerpt = (h.Text ?? "").Trim();

            if (excerpt.Length > maxChars)
                excerpt = excerpt[..maxChars] + "…";

            sb.AppendLine($"[{sid}] {title} ({pages}, {chunk})");
            sb.AppendLine(excerpt);
            sb.AppendLine();
        }

        sb.AppendLine("Réponds maintenant en respectant STRICTEMENT les règles ci-dessus.");
        return sb.ToString().TrimEnd();
    }

    public string BuildSystemPrompt(List<RagHit> hits, string userQuestion, string? retrievalConfidence = null)
    {
        // Reuse the same instructions and sources as the user message builder,
        // but keep the actual question as a separate user role. This increases
        // the chance that the LLM will follow the RAG constraints.
        var o = _opt.Value;
        userQuestion = (userQuestion ?? "").Trim();

        var sb = new StringBuilder();

        // Prompt custom (si fourni), sans retirer les règles RAG strictes
        if (!string.IsNullOrWhiteSpace(o.SystemPrompt))
        {
            sb.AppendLine(o.SystemPrompt.Trim());
            sb.AppendLine();
        }


        sb.AppendLine("Tu es un assistant technique. Tu dois répondre UNIQUEMENT à partir des SOURCES fournies.");
        sb.AppendLine("Si l'information n'est pas clairement présente dans les sources, dis-le explicitement.");
        sb.AppendLine();

        sb.AppendLine("CONTRAINTES DE STYLE :");
        sb.AppendLine($"- Réponse concise: максимум {o.ResponseMaxWords} mots.");
        sb.AppendLine($"- максимум {o.ResponseMaxSentences} phrases.");
        sb.AppendLine($"- environ {o.ResponseMaxWordsPerSentence} mots par phrase.");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(retrievalConfidence))
        {
            var conf = retrievalConfidence.Trim().ToLowerInvariant();
            if (conf == "low")
            {
                sb.AppendLine("IMPORTANT (pertinence faible) : les extraits récupérés semblent PEU pertinents pour la question.");
                sb.AppendLine("Commence par 1 phrase du type : \"Je n'ai probablement pas trouvé l'information pertinente dans la base\".");
                sb.AppendLine("Ensuite, si tu peux, donne une tentative basée sur les extraits, en précisant que c'est incertain.");
                sb.AppendLine("Ne mentionne PAS de scores ni de détails internes.");
                sb.AppendLine();
            }
            else if (conf == "warn")
            {
                sb.AppendLine("NOTE (pertinence incertaine) : les extraits semblent partiellement pertinents.");
                sb.AppendLine("Si la réponse est incomplète, dis-le clairement.");
                sb.AppendLine("Ne mentionne PAS de scores ni de détails internes.");
                sb.AppendLine();
            }
        }

        sb.AppendLine("RÈGLES DE CITATION :");
        sb.AppendLine("- Après chaque phrase factuelle, ajoute la/les citations sous forme [S1] [S2].");
        sb.AppendLine("- N'invente jamais de citation.");
        sb.AppendLine("- Si tu n'utilises pas une source, ne la cite pas.");
        sb.AppendLine();

        // Inject sources (same logic as BuildUserMessage)
        if (hits is null || hits.Count == 0)
        {
            sb.AppendLine("SOURCES :");
            sb.AppendLine("(aucune source retrouvée)");
            sb.AppendLine();
            sb.AppendLine("Tu as le droit d'utiliser tes connaissances générales.");
            sb.AppendLine("Tu NE dois PAS dire que tu ne peux pas répondre faute de sources.");
            sb.AppendLine("N'ajoute AUCUNE citation dans ce mode.");
            return sb.ToString().TrimEnd();
        }

        var maxSources = Math.Clamp(o.PromptMaxSources, 1, 20);
        var maxChars = Math.Clamp(o.PromptMaxCharsPerSource, 200, 5000);

        sb.AppendLine("SOURCES :");
        for (int i = 0; i < hits.Count && i < maxSources; i++)
        {
            var h = hits[i];
            var sid = $"S{i + 1}";
            var title = h.DocName ?? h.DocPath ?? "(document inconnu)";
            var pages = (h.PageStart is not null || h.PageEnd is not null)
                ? $"p.{h.PageStart?.ToString() ?? "?"}-{h.PageEnd?.ToString() ?? "?"}"
                : "p.?";

            var chunk = h.ChunkIndex is not null ? $"chunk {h.ChunkIndex}" : "chunk ?";
            var excerpt = (h.Text ?? "").Trim();

            if (excerpt.Length > maxChars)
                excerpt = excerpt[..maxChars] + "…";

            sb.AppendLine($"[{sid}] {title} ({pages}, {chunk})");
            sb.AppendLine(excerpt);
            sb.AppendLine();
        }

        sb.AppendLine("Respecte STRICTEMENT les règles ci-dessus lorsque tu réponds.");
        return sb.ToString().TrimEnd();
    }
}
