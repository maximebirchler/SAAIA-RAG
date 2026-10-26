using System.Text.Json;

/// <summary>A vector is usable only when it is finite, nonempty and bound to exactly one input.</summary>
internal static class TeiEmbeddingResponse
{
    public static float[][] Parse(JsonElement root, int inputCount)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != inputCount)
            throw new InvalidDataException("tei_embedding_count_mismatch");
        var indexed = data.EnumerateArray().Any(item => item.TryGetProperty("index", out _));
        var result = new float[inputCount][];
        var ordinal = 0;
        var dimensions = 0;
        foreach (var item in data.EnumerateArray())
        {
            var index = ordinal++;
            if (indexed && (!item.TryGetProperty("index", out var supplied) || !supplied.TryGetInt32(out index)))
                throw new InvalidDataException("tei_embedding_index_missing");
            if (index < 0 || index >= inputCount || result[index] is not null)
                throw new InvalidDataException("tei_embedding_index_invalid");
            if (!item.TryGetProperty("embedding", out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() == 0)
                throw new InvalidDataException("tei_embedding_empty");
            if (dimensions != 0 && dimensions != values.GetArrayLength())
                throw new InvalidDataException("tei_embedding_dimensions_inconsistent");
            dimensions = values.GetArrayLength();
            var vector = new float[dimensions];
            var offset = 0;
            var nonzero = false;
            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out var number) || !float.IsFinite(number))
                    throw new InvalidDataException("tei_embedding_component_invalid");
                vector[offset++] = number;
                nonzero |= number != 0;
            }
            if (!nonzero) throw new InvalidDataException("tei_embedding_zero_vector");
            result[index] = vector;
        }
        return result;
    }
}
