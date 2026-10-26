static class IngestionFileSystemPathIdentity
{
    // Paths identify distinct source files on Linux. Windows retains its
    // existing case-insensitive identity; hash strings have a separate policy.
    internal static StringComparer Comparer(bool windowsFileSystem)
        => windowsFileSystem ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
