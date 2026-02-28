using System;
using System.Collections.Generic;

namespace SAAIA.Client.WinUI.Models;

public sealed class DocumentResolveItem
{
    public Guid DocId { get; set; }
    public string DocPath { get; set; } = "";
    public string DocName { get; set; } = "";
    public string Category { get; set; } = "";
    public string Status { get; set; } = "";
    public int PageCount { get; set; }
    public DateTime? LastIngestedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class DocumentResolveResponse
{
    public List<DocumentResolveItem> Items { get; set; } = new();
    public int Limit { get; set; }
}
