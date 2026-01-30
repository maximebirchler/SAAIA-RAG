using System.Text;
using System.Text.Json;

static class TeiClient
{
    public static async Task<int> GetVectorDimAsync(HttpClient tei, string model, CancellationToken ct)
    {
        var vecs = await EmbedAsync(tei, model, new[] { "ping" }, ct);
        return vecs[0].Length;
    }

    public static async Task<float[][]> EmbedAsync(HttpClient tei, string model, string[] inputs, CancellationToken ct)
    {
        var body = new
        {
            model,
            input = inputs,
            encoding_format = "float"
        };

        var resp = await tei.PostAsync("/v1/embeddings",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"TEI embeddings failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        var data = doc.RootElement.GetProperty("data");
        var list = new List<float[]>();
        foreach (var item in data.EnumerateArray())
        {
            var emb = item.GetProperty("embedding");
            var v = new float[emb.GetArrayLength()];
            int i = 0;
            foreach (var n in emb.EnumerateArray())
                v[i++] = n.GetSingle();
            list.Add(v);
        }
        return list.ToArray();
    }
}
