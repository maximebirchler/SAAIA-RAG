using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SAAIA.Client.Core.Diagnostics;

/// <summary>Metadata export policy, including legacy snapshots which may contain document or chat text.</summary>
public static class SupportDiagnosticRedactor
{
    public static string RedactText(string text)
    {
        var result = Regex.Replace(text, @"(?im)^(\s*-?\s*[A-Z0-9_]*(?:API[_-]?KEY|ADMIN[_-]?KEY|TOKEN|SECRET|PASSWORD)[A-Z0-9_]*\s*[:=]\s*)[^\r\n]*",
            "$1[REDACTED]");
        result = Regex.Replace(result, @"(?i)(\bBearer\s+)[A-Za-z0-9._~+/\-=]+", "$1[REDACTED]");
        return Regex.Replace(result, @"\bsk-[A-Za-z0-9_-]{16,}", "[REDACTED]");
    }

    public static string RedactJson(string json, bool includeContent = false)
    {
        var node = JsonNode.Parse(json);
        Redact(node, includeContent);
        return node?.ToJsonString(new() { WriteIndented = true }) ?? "null";
    }

    private static void Redact(JsonNode? node, bool includeContent, string? property = null)
    {
        if (node is JsonObject obj)
            foreach (var entry in obj.ToArray())
            {
                var key = Regex.Replace(entry.Key, "[^a-zA-Z]", "").ToLowerInvariant();
                var secret = key.Contains("apikey") || key.Contains("password") || key.Contains("secret")
                    || key.Contains("authorization") || key is "accesskey" or "bearertoken" or "adminkey" or "hftoken";
                secret &= key is not ("hasapikey" or "hasadminkey" or "hassecret");
                var content = key.Contains("query") || key.Contains("queries") || key.Contains("answer")
                    || key.Contains("prompt") || key.Contains("docpath") || key.Contains("docname")
                    || key.Contains("documentpath") || key.Contains("documentname") || key.Contains("filename")
                    || key.Contains("note") || key.Contains("label") || key.Contains("snippet") || key.Contains("traceevents")
                    || key is "text" or "content" or "excerpt" or "topic" or "label" or "headingpath"
                        or "sectiontitle" or "lastfocuseddocument" or "memoryupdate";
                var numericOrBoolean = entry.Value is JsonValue value &&
                    (value.TryGetValue<bool>(out _) || value.TryGetValue<long>(out _) || value.TryGetValue<double>(out _));
                var metadata = key.EndsWith("count") || key.EndsWith("chars") || key.EndsWith("tokens") || key is "answersource";
                if (secret || (!numericOrBoolean && !includeContent && content && !metadata)) obj[entry.Key] = "[REDACTED]";
                else if (entry.Value is JsonValue stringValue && stringValue.TryGetValue<string>(out var text)
                    && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                    obj[entry.Key] = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.AbsoluteUri;
                else Redact(entry.Value, includeContent, key);
            }
        else if (node is JsonArray array)
            for (var index = 0; index < array.Count; index++)
            {
                if (!includeContent && array[index] is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    var safeCodeList = property is "toolnames" or "writertoolnames" or "riskflags" or "capabilities";
                    if (!safeCodeList || !Regex.IsMatch(text, @"^[A-Za-z0-9_.:\-]{1,120}$")) array[index] = "[REDACTED]";
                }
                else Redact(array[index], includeContent, property);
            }
    }
}
