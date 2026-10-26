/// <summary>Use the same SQL files copied with the backend, independently of the build output layout.</summary>
internal static class TestRepositoryPaths
{
    internal static string MigrationsDirectory
    {
        get
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "Db", "Migrations");
            if (!Directory.Exists(directory) || !Directory.EnumerateFiles(directory, "*.sql").Any())
                throw new DirectoryNotFoundException("The backend SQL migrations were not copied to the test output.");
            return directory;
        }
    }
}
