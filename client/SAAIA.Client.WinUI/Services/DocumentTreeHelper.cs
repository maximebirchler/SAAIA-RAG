using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using SAAIA.Client.WinUI.Services.ToolAgent;

namespace SAAIA.Client.WinUI.Services;

internal static class DocumentTreeHelper
{
    private sealed class Node
    {
        public Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<ToolMemory.DocumentItem> Docs { get; } = new();
    }

    public static string BuildMarkdownTree(List<ToolMemory.DocumentItem> docs)
    {
        var root = new Node();

        foreach (var d in docs ?? new List<ToolMemory.DocumentItem>())
        {
            var dp = (d.DocPath ?? "").Replace('\\', '/').Trim().TrimStart('/');
            if (dp.Length == 0) continue;

            var segs = dp.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segs.Length == 0) continue;

            var node = root;
            for (var i = 0; i < segs.Length - 1; i++)
            {
                var seg = segs[i];
                if (!node.Children.TryGetValue(seg, out var child))
                {
                    child = new Node();
                    node.Children[seg] = child;
                }
                node = child;
            }

            node.Docs.Add(d);
        }

        var sb = new StringBuilder();
        Render(root, sb, depth: 0);
        return sb.ToString().TrimEnd();
    }

    private static void Render(Node node, StringBuilder sb, int depth)
    {
        foreach (var kv in node.Children.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var indent = new string(' ', depth * 2);
            sb.AppendLine($"{indent}- {kv.Key}");
            Render(kv.Value, sb, depth + 1);
        }

        foreach (var d in node.Docs.OrderBy(x => (x.DocName ?? Path.GetFileName(x.DocPath ?? "")), StringComparer.OrdinalIgnoreCase))
        {
            var indent = new string(' ', depth * 2);
            var dp = (d.DocPath ?? "").Replace('\\', '/').Trim().TrimStart('/');
            var label = (d.DocName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(label))
                label = Path.GetFileName(dp);

            label = label.Replace("|", " ").Replace("]", ")");
            sb.AppendLine($"{indent}- [[open|{dp}|1|{label}]]");
        }
    }
}