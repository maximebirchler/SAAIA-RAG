using System.Text.RegularExpressions;

namespace SAAIA.Backend.Chat;

/// <summary>
/// Sélectionne les meilleures sources à partir d'un pool de candidats, en évitant les quasi-doublons.
/// Objectif: limiter la redondance due au chunking/overlap, sans interdire des chunks adjacents
/// qui contiennent des informations différentes.
/// </summary>
internal static class RagHitSelection
{
    internal sealed record Info(
        int RequestedTopK,
        int CandidateK,
        int SelectedK,
        double BestScore,
        double? SecondScore,
        double ScoreGap,
        string Confidence // "ok" | "warn" | "low"
    );

    internal sealed record Result(List<RagHit> Selected, Info SelectionInfo);

    public static Result Select(List<RagHit> candidates, int requestedTopK, ChatOptions o)
    {
        candidates ??= new List<RagHit>();
        requestedTopK = Math.Max(0, requestedTopK);

        if (requestedTopK == 0 || candidates.Count == 0)
        {
            var emptyInfo = new Info(
                RequestedTopK: requestedTopK,
                CandidateK: candidates.Count,
                SelectedK: 0,
                BestScore: 0,
                SecondScore: null,
                ScoreGap: 0,
                Confidence: "low"
            );
            return new Result(new List<RagHit>(), emptyInfo);
        }

        // Scores
        var ordered = candidates.OrderByDescending(h => h.Score).ToList();
        var best = ordered[0].Score;
        double? second = ordered.Count >= 2 ? ordered[1].Score : null;
        var gap = second.HasValue ? (best - second.Value) : 0;

        // Confidence
        var low = o.RetrievalLowConfidenceScore;
        var ok = o.RetrievalOkScore;
        if (ok < low) (low, ok) = (ok, low);

        var conf = best <= 0 ? "low" : (best < low ? "low" : (best < ok ? "warn" : "ok"));

        // Déduplication
        var selected = new List<RagHit>(requestedTopK);
        var shingleWords = Math.Clamp(o.RetrievalShingleWords, 2, 10);
        var dupThreshold = Math.Clamp(o.RetrievalNearDuplicateJaccard, 0.5, 0.99);

        var shingleCache = new Dictionary<RagHit, HashSet<string>>();

        foreach (var h in ordered)
        {
            if (selected.Count >= requestedTopK) break;

            var ht = h.Text ?? "";
            if (string.IsNullOrWhiteSpace(ht))
            {
                selected.Add(h);
                continue;
            }

            var hSet = GetShingles(shingleCache, h, shingleWords);

            var isDup = false;
            foreach (var s in selected)
            {
                var sSet = GetShingles(shingleCache, s, shingleWords);
                var j = Jaccard(hSet, sSet);
                if (j >= dupThreshold)
                {
                    isDup = true;
                    break;
                }
            }

            if (!isDup)
                selected.Add(h);
        }

        // Si on n'a pas assez de chunks (rare), on complète sans déduplication stricte
        if (selected.Count < requestedTopK)
        {
            foreach (var h in ordered)
            {
                if (selected.Count >= requestedTopK) break;
                if (!selected.Contains(h))
                    selected.Add(h);
            }
        }

        var info = new Info(
            RequestedTopK: requestedTopK,
            CandidateK: candidates.Count,
            SelectedK: selected.Count,
            BestScore: best,
            SecondScore: second,
            ScoreGap: gap,
            Confidence: conf
        );

        return new Result(selected, info);
    }

    private static HashSet<string> GetShingles(Dictionary<RagHit, HashSet<string>> cache, RagHit hit, int shingleWords)
    {
        if (cache.TryGetValue(hit, out var set)) return set;
        set = BuildShingles(hit.Text ?? "", shingleWords);
        cache[hit] = set;
        return set;
    }

    private static HashSet<string> BuildShingles(string text, int shingleWords)
    {
        var norm = Normalize(text);
        var tokens = Tokenize(norm);
        var set = new HashSet<string>(StringComparer.Ordinal);

        if (tokens.Count == 0) return set;

        // si chunk très court, on met le chunk entier comme "shingle"
        if (tokens.Count <= shingleWords)
        {
            set.Add(string.Join(' ', tokens));
            return set;
        }

        for (int i = 0; i <= tokens.Count - shingleWords; i++)
        {
            var sh = string.Join(' ', tokens.Skip(i).Take(shingleWords));
            set.Add(sh);
        }

        return set;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;

        // itère sur le plus petit set
        if (a.Count > b.Count) (a, b) = (b, a);

        int inter = 0;
        foreach (var x in a)
            if (b.Contains(x))
                inter++;

        var union = a.Count + b.Count - inter;
        return union <= 0 ? 0 : (double)inter / union;
    }

    private static string Normalize(string s)
    {
        s = s.ToLowerInvariant();
        s = Regex.Replace(s, @"[^\p{L}\p{N}\s]+", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    private static List<string> Tokenize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new List<string>();

        var m = Regex.Matches(s, @"[\p{L}\p{N}]+");
        var list = new List<string>(m.Count);
        foreach (Match x in m)
            list.Add(x.Value);
        return list;
    }
}
