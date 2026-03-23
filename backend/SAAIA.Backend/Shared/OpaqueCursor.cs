using System.Text;
using System.Text.Json;

namespace SAAIA.Backend.Shared;

public static class OpaqueCursor
{
    public static string Encode<T>(T payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        return ToBase64Url(bytes);
    }

    public static bool TryDecode<T>(string? cursor, out T? payload)
    {
        payload = default;
        if (string.IsNullOrWhiteSpace(cursor))
            return false;

        try
        {
            var bytes = FromBase64Url(cursor.Trim());
            payload = JsonSerializer.Deserialize<T>(bytes);
            return payload is not null;
        }
        catch
        {
            payload = default;
            return false;
        }
    }

    private static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string encoded)
    {
        var normalized = encoded.Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 2:
                normalized += "==";
                break;
            case 3:
                normalized += "=";
                break;
        }

        return Convert.FromBase64String(normalized);
    }
}
