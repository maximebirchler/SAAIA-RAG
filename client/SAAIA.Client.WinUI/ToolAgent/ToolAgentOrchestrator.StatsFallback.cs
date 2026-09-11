using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using SAAIA.Client.WinUI.Localization;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private static string BuildStatsFallbackAnswer(JsonElement result, string language)
    {
        language = NormalizeLanguageCode(language);
        var totalDocuments = TryGetInt(result, "totalDocuments") ?? 0;
        var maxDepth = TryGetInt(result, "maxDepth") ?? 0;
        var totalFolders = TryGetInt(result, "totalNonEmptyFolders") ?? 0;
        var scopePath = NormalizeCategoryPathArg(TryGetString(result, "scopePath"));
        var sb = new StringBuilder();
        sb.AppendLine(DeterministicAgentText.StatsTitle(language, scopePath));
        sb.AppendLine(DeterministicAgentText.StatsIndexedDocuments(totalDocuments, language));
        sb.AppendLine(DeterministicAgentText.StatsTotalFolders(totalFolders, language));
        var emptyFolders = TryGetInt(result, "totalEmptyFolders")
                           ?? TryGetInt(result, "emptyFolderCount")
                           ?? TryGetInt(result, "emptyFolders")
                           ?? TryGetInt(result, "emptyCount");
        if (emptyFolders.HasValue)
            sb.AppendLine(DeterministicAgentText.StatsEmptyFolders(emptyFolders.Value, language));
        AppendFolderDepthLines(sb, result, language);
        sb.AppendLine(DeterministicAgentText.StatsMainStructure(language));

        var appendedRootLine = false;
        if (result.TryGetProperty("rootFolders", out var rf) && rf.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in rf.EnumerateArray())
            {
                var name = TryGetString(x, "name") ?? TryGetString(x, "path") ?? string.Empty;
                var total = TryGetInt(x, "totalDocuments") ?? 0;
                var direct = TryGetInt(x, "directDocuments") ?? 0;
                var sub = TryGetInt(x, "subfolderCount") ?? 0;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                sb.AppendLine(DeterministicAgentText.RootFolderLine(name, total, direct, sub, language));
                appendedRootLine = true;
            }
        }

        if (!appendedRootLine)
            sb.AppendLine(DeterministicAgentText.NoSubfoldersInScope(language));

        return sb.ToString().TrimEnd();
    }

    private static void AppendFolderDepthLines(StringBuilder sb, JsonElement result, string language)
    {
        if (!result.TryGetProperty("foldersByDepth", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            var topLevel = TryGetInt(result, "topLevelFolderCount");
            if (topLevel.HasValue)
            {
                sb.AppendLine(DeterministicAgentText.FirstLevelFolders(topLevel.Value, language));
            }
            return;
        }

        var separator = NormalizeLanguageCode(language) == "fr" ? " : " : ": ";
        foreach (var item in arr.EnumerateArray().OrderBy(x => TryGetInt(x, "depth") ?? 0))
        {
            var depth = TryGetInt(item, "depth") ?? 0;
            var count = TryGetInt(item, "folderCount") ?? 0;
            if (depth <= 0)
                continue;

            sb.AppendLine($"- {DeterministicAgentText.FolderDepthLabel(depth, language)}{separator}{count}");
        }
    }


}
