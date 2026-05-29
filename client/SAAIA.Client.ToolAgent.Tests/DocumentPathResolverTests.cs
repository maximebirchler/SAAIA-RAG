using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

[Collection("RuntimeRootSerial")]
public sealed class DocumentPathResolverTests : IDisposable
{
    private readonly string? _previousDocumentsRoot = Environment.GetEnvironmentVariable("SAAIA_DOCUMENTS_ROOT");
    private readonly string? _previousInstallRoot = Environment.GetEnvironmentVariable("SAAIA_INSTALL_ROOT");
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "SAAIA.DocumentPathResolver.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SAAIA_DOCUMENTS_ROOT", _previousDocumentsRoot);
        Environment.SetEnvironmentVariable("SAAIA_INSTALL_ROOT", _previousInstallRoot);

        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for the test sandbox.
        }
    }

    [Fact]
    public void Resolve_accepts_existing_absolute_paths_before_relative_normalization()
    {
        var documentsRoot = Path.Combine(_tempRoot, "documents");
        Directory.CreateDirectory(documentsRoot);
        Environment.SetEnvironmentVariable("SAAIA_DOCUMENTS_ROOT", documentsRoot);
        Environment.SetEnvironmentVariable("SAAIA_INSTALL_ROOT", null);

        var absoluteFile = Path.Combine(_tempRoot, "external-source.pdf");
        File.WriteAllText(absoluteFile, "pdf placeholder");

        var resolved = DocumentPathResolver.Resolve(absoluteFile);

        Assert.Equal(Path.GetFullPath(absoluteFile), resolved);
    }

    [Fact]
    public void Resolve_rejects_relative_paths_that_escape_documents_root()
    {
        var documentsRoot = Path.Combine(_tempRoot, "documents");
        Directory.CreateDirectory(documentsRoot);
        Environment.SetEnvironmentVariable("SAAIA_DOCUMENTS_ROOT", documentsRoot);
        Environment.SetEnvironmentVariable("SAAIA_INSTALL_ROOT", null);

        var outsideFile = Path.Combine(_tempRoot, "outside.pdf");
        File.WriteAllText(outsideFile, "pdf placeholder");

        var resolved = DocumentPathResolver.Resolve(@"..\outside.pdf");

        Assert.Null(resolved);
    }

    [Fact]
    public void Resolve_finds_relative_sources_inside_documents_root()
    {
        var documentsRoot = Path.Combine(_tempRoot, "documents");
        var sourceFile = Path.Combine(documentsRoot, "Category", "manual.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        File.WriteAllText(sourceFile, "pdf placeholder");
        Environment.SetEnvironmentVariable("SAAIA_DOCUMENTS_ROOT", documentsRoot);
        Environment.SetEnvironmentVariable("SAAIA_INSTALL_ROOT", null);

        var resolved = DocumentPathResolver.Resolve("Category/manual.pdf");

        Assert.Equal(Path.GetFullPath(sourceFile), resolved);
    }
}
