static class Chunker
{
    public static List<Chunk> MakeChunks(List<WordToken> tokens, int maxWords, int overlapWords, int minWords)
    {
        var chunks = new List<Chunk>();
        int idx = 0;
        int chunkIndex = 0;

        while (idx < tokens.Count)
        {
            int end = Math.Min(idx + maxWords, tokens.Count);
            var slice = tokens.GetRange(idx, end - idx);

            if (slice.Count >= minWords)
            {
                var pageStart = slice.Min(t => t.Page);
                var pageEnd = slice.Max(t => t.Page);
                var text = string.Join(' ', slice.Select(t => t.Word));
                chunks.Add(new Chunk(chunkIndex++, pageStart, pageEnd, text));
            }

            if (end >= tokens.Count) break;

            idx = Math.Max(0, end - overlapWords);
            if (idx == end) idx++; // safety
        }

        return chunks;
    }
}
