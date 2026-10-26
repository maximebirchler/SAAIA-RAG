internal sealed record IngestionFileInventoryBatch(IReadOnlyList<string> Files, bool Complete,
    bool ContinuesNextScan, string? FailureCode);

/// <summary>Visits a bounded batch, retaining the cursor instead of repeatedly starving later files.</summary>
internal sealed class IngestionFileInventory : IDisposable
{
    private IEnumerator<string>? _cursor;
    private string? _root;
    private string? _lookahead;
    internal static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal IngestionFileInventoryBatch Read(string root, int maximumFiles,
        Func<string, IEnumerable<string>>? enumerate = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFiles, 1);
        var files = new List<string>();
        try
        {
            if (!PathComparer.Equals(_root, root)) Dispose();
            _root = root;
            _cursor ??= (enumerate?.Invoke(root) ?? Directory.EnumerateFiles(root, "*.pdf", new EnumerationOptions
            {
                RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive,
                IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint
            })).GetEnumerator();
            while (files.Count < maximumFiles)
            {
                if (_lookahead is not null)
                {
                    files.Add(_lookahead);
                    _lookahead = null;
                }
                else if (_cursor.MoveNext()) files.Add(_cursor.Current);
                else
                {
                    Dispose();
                    return new(files, Complete: true, ContinuesNextScan: false, FailureCode: null);
                }
            }
            if (_cursor.MoveNext())
            {
                _lookahead = _cursor.Current;
                return new(files, Complete: false, ContinuesNextScan: true, FailureCode: null);
            }
            Dispose();
            return new(files, Complete: true, ContinuesNextScan: false, FailureCode: null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Dispose();
            return new(files, Complete: false, ContinuesNextScan: false,
                FailureCode: error is UnauthorizedAccessException ? "inventory_access_denied" : "inventory_io_error");
        }
    }

    public void Dispose()
    {
        _cursor?.Dispose();
        _cursor = null;
        _root = null;
        _lookahead = null;
    }
}
