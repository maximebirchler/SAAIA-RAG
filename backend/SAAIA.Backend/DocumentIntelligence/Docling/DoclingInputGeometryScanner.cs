using System.Text;
using System.Text.Json;

/// <summary>Selects bounded geometry tokens without materializing image/text subtrees.</summary>
internal sealed class DoclingInputGeometryScanner(DoclingInputGeometryCapture owner, CancellationToken ct)
{
    private readonly DoclingInputGeometrySnapshot data = owner.Data;
    private int cells;
    private bool tablesSeen;
    internal void Scan(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 128 });
        if (!reader.Read())
            throw new DoclingInputGeometryRefusal("geometry_shape_invalid");
        ReadObject(ref reader, "root", "", null, null);
        if (reader.Read() || !tablesSeen)
            throw new DoclingInputGeometryRefusal("geometry_inventory_unavailable");
        foreach (var pair in data.Pages)
        {
            if (!int.TryParse(pair.Key, out var page) || page <= 0 || !pair.Value.Fields.TryGetValue("page_no", out var token) || token.RawToken != page.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new DoclingInputGeometryRefusal("page_binding_inconsistent");
        }

        data.PageCount = data.Pages.Count;
        data.TableCount = data.Tables.Count;
        data.CellCount = cells;
        data.OmittedCount = 0;
        data.SourceInventoryComplete = true;
    }

    private void ReadObject(ref Utf8JsonReader r, string kind, string path, DoclingInputGeometryFields? fields, DoclingInputGeometryTable? table, string prefix = "")
    {
        if (r.TokenType != JsonTokenType.StartObject)
            throw new DoclingInputGeometryRefusal("geometry_shape_invalid");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            owner.Budget.Check(ct);
            if (r.TokenType != JsonTokenType.PropertyName || r.ValueSpan.Length > 2048)
                throw new DoclingInputGeometryRefusal("geometry_key_limit");
            var key = r.GetString()!;
            if (key.Length > 512)
                throw new DoclingInputGeometryRefusal("geometry_identifier_limit");
            if (!keys.Add(key))
                throw new DoclingInputGeometryRefusal("duplicate_geometry_key");
            if (keys.Count > (kind == "pages" ? 1024 : 128))
                throw new DoclingInputGeometryRefusal("geometry_key_limit");
            if (!r.Read())
                throw new DoclingInputGeometryRefusal("geometry_shape_invalid");
            var pointer = path + "/" + key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
            if (kind == "root" && key == "document")
                ReadObject(ref r, "export", pointer, null, null);
            else if (kind == "export" && key == "json_content")
                ReadObject(ref r, "document", pointer, null, null);
            else if (kind == "document" && key == "pages")
                ReadObject(ref r, "pages", pointer, null, null);
            else if (kind == "document" && key == "tables")
            {
                tablesSeen = true;
                ReadArray(ref r, "tables", pointer, null);
            }
            else if (kind == "pages")
            {
                if (data.Pages.Count >= 1024)
                    throw new DoclingInputGeometryRefusal("geometry_page_limit");
                var page = new DoclingInputGeometryFields();
                data.Pages.Add(key, page);
                ReadObject(ref r, "page", pointer, page, null);
            }
            else if (kind == "page" && key == "size")
                ReadObject(ref r, "size", pointer, fields, null, "size.");
            else if (kind == "table" && key == "data")
                ReadObject(ref r, "data", pointer, fields, table);
            else if (kind == "table" && key == "prov")
                ReadArray(ref r, "prov", pointer, table);
            else if (kind == "data" && key == "table_cells")
                ReadArray(ref r, "cells", pointer, table);
            else if ((kind is "cell" or "prov") && key == "bbox")
            {
                if (r.TokenType == JsonTokenType.Null)
                    Store(ref r, pointer, fields!, "bbox");
                else
                    ReadObject(ref r, "bbox", pointer, fields, table, "bbox.");
            }
            else if ((kind == "page" && key == "page_no") || (kind == "size" && key is "width" or "height") || (kind == "table" && key == "self_ref") || (kind == "data" && key is "num_rows" or "num_cols") || (kind == "prov" && key == "page_no") || (kind == "bbox" && key is "l" or "t" or "r" or "b" or "coord_origin") || (kind == "cell" && key is "row_span" or "col_span" or "start_row_offset_idx" or "start_col_offset_idx"))
                Store(ref r, pointer, fields!, prefix + key);
            else
                r.Skip();
            owner.Budget.Check(ct);
        }

        if (r.TokenType != JsonTokenType.EndObject)
            throw new DoclingInputGeometryRefusal("geometry_shape_invalid");
    }

    private void ReadArray(ref Utf8JsonReader r, string kind, string path, DoclingInputGeometryTable? table)
    {
        if (r.TokenType != JsonTokenType.StartArray)
            throw new DoclingInputGeometryRefusal("geometry_shape_invalid");
        var index = 0;
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            owner.Budget.Check(ct);
            var pointer = path + "/" + index;
            if (kind == "tables")
            {
                if (index >= 1024)
                    throw new DoclingInputGeometryRefusal("geometry_table_limit");
                var next = new DoclingInputGeometryTable
                {
                    ArrayIndex = index
                };
                data.Tables.Add(next);
                ReadObject(ref r, "table", pointer, next.Values, next);
            }
            else
            {
                if ((kind == "cells" && ++cells > 100000) || index >= 100000)
                    throw new DoclingInputGeometryRefusal("geometry_cell_limit");
                if (kind == "prov" && index >= 1024)
                    throw new DoclingInputGeometryRefusal("geometry_provenance_limit");
                var fields = new DoclingInputGeometryFields();
                (kind == "cells" ? table!.Cells : table!.Provenance).Add(fields);
                ReadObject(ref r, kind == "cells" ? "cell" : "prov", pointer, fields, table);
            }

            index++;
        }

        if (r.TokenType != JsonTokenType.EndArray)
            throw new DoclingInputGeometryRefusal("geometry_shape_invalid");
    }

    private void Store(ref Utf8JsonReader r, string pointer, DoclingInputGeometryFields fields, string key)
    {
        if (r.ValueSpan.Length > 512 || r.TokenType is not (JsonTokenType.Number or JsonTokenType.String or JsonTokenType.Null))
            throw new DoclingInputGeometryRefusal("geometry_token_limit");
        var value = r.TokenType == JsonTokenType.String ? r.GetString()! : r.TokenType == JsonTokenType.Null ? "null" : Encoding.UTF8.GetString(r.ValueSpan);
        if (value.Length > 512)
            throw new DoclingInputGeometryRefusal("geometry_token_limit");
        // Strings are decoded values, not raw lexemes; offsets/lengths cover the full token including quotes.
        var token = new DoclingInputGeometryToken(pointer, r.TokenType.ToString(), r.TokenType == JsonTokenType.String ? null : value, r.TokenType == JsonTokenType.String ? value : null, r.TokenStartIndex, checked((int)(r.BytesConsumed - r.TokenStartIndex)));
        fields.Fields.Add(key, token);
        data.Tokens.Add(token);
    }
}
