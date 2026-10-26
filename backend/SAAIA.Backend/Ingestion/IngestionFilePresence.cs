internal enum IngestionSourcePresence { Present, Absent, Unknown }

/// <summary>Only explicit not-found under an accessible root is absence; access/IO failures are unknown.</summary>
internal static class IngestionFilePresence
{
    internal static IngestionSourcePresence Probe(string root, string relativePath,
        Func<string, FileAttributes>? attributes = null)
    {
        attributes ??= File.GetAttributes;
        try
        {
            var rootPath = Path.GetFullPath(root);
            var target = Path.GetFullPath(Path.Combine(rootPath, relativePath));
            var relative = Path.GetRelativePath(rootPath, target);
            if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(static segment => segment == "..")) return IngestionSourcePresence.Unknown;
            // A disappearing mount/root is not proof that all its known PDFs were removed.
            if (!attributes(rootPath).HasFlag(FileAttributes.Directory)) return IngestionSourcePresence.Unknown;
            try
            {
                _ = attributes(target);
                return IngestionSourcePresence.Present;
            }
            catch (FileNotFoundException) { return IngestionSourcePresence.Absent; }
            catch (DirectoryNotFoundException) { return IngestionSourcePresence.Absent; }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return IngestionSourcePresence.Unknown;
        }
    }
}
