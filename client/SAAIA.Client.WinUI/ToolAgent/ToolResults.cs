using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed class ToolResults
{
    public List<Item> Items { get; } = new();

    public sealed class Item
    {
        public string ToolName { get; set; } = "";
        public JsonElement Result { get; set; }
        public string? Error { get; set; }
        public long DurationMs { get; set; }
    }
}