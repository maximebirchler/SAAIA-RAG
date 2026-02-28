using System;
using System.Collections.Generic;

namespace SAAIA.Client.WinUI.Models;

// NOTE: backend columns can be NULL depending on ingestion/migration state.
// Keep these properties nullable to avoid JSON deserialization failures.
public sealed class DocumentCatalogItem
{
    public Guid DocId { get; set; }
    public string? DocPath { get; set; }
    public string? DocName { get; set; }
    public string? Category { get; set; }
    public string? Status { get; set; }
    public int? PageCount { get; set; }
    public DateTime? LastIngestedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public sealed class DocumentsCatalogResponse
{
    public List<DocumentCatalogItem> Items { get; set; } = new();
    public int Limit { get; set; }
    public int Offset { get; set; }
}
