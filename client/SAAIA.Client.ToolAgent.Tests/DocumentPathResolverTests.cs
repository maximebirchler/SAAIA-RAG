using SAAIA.Client.WinUI.Services;
using System.Security.Cryptography;
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

    [Fact]
    public void Resolve_exact_revision_requires_and_verifies_a_sha256()
    {
        var documentsRoot = Path.Combine(_tempRoot, "documents");
        var sourceFile = Path.Combine(documentsRoot, "Category", "manual.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        File.WriteAllText(sourceFile, "exact indexed revision");
        Environment.SetEnvironmentVariable("SAAIA_DOCUMENTS_ROOT", documentsRoot);
        Environment.SetEnvironmentVariable("SAAIA_INSTALL_ROOT", null);
        var sourceHash = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(sourceFile)))
            .ToLowerInvariant();
        var method = typeof(DocumentPathResolver).GetMethod(
            "ResolveExactRevision",
            System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(method);
        Assert.Null(method!.Invoke(null, new object?[]
        {
            "Category/manual.pdf",
            "e2f23af762e16714266050b3ad56078f"
        }));
        Assert.Equal(
            Path.GetFullPath(sourceFile),
            method.Invoke(null, new object?[]
            {
                "Category/manual.pdf",
                sourceHash
            }));
    }

    [Fact]
    public void Resolve_finds_a_moved_source_by_filename_and_sha256()
    {
        var documentsRoot = Path.Combine(_tempRoot, "documents");
        var canonicalFile = Path.Combine(documentsRoot, "Canonical", "manual.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(canonicalFile)!);
        File.WriteAllText(canonicalFile, "canonical revision");
        Environment.SetEnvironmentVariable("SAAIA_DOCUMENTS_ROOT", documentsRoot);
        Environment.SetEnvironmentVariable("SAAIA_INSTALL_ROOT", null);
        var sourceHash = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(canonicalFile)))
            .ToLowerInvariant();

        var resolved = DocumentPathResolver.Resolve(
            "Old/Removed/manual.pdf",
            sourceHash);

        Assert.Equal(Path.GetFullPath(canonicalFile), resolved);
    }

    [Fact]
    public void Resolve_uses_sha256_to_disambiguate_moved_sources_with_the_same_filename()
    {
        var documentsRoot = Path.Combine(_tempRoot, "documents");
        var wrongFile = Path.Combine(documentsRoot, "A", "manual.pdf");
        var expectedFile = Path.Combine(documentsRoot, "B", "manual.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(wrongFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(expectedFile)!);
        File.WriteAllText(wrongFile, "another revision");
        File.WriteAllText(expectedFile, "expected revision");
        Environment.SetEnvironmentVariable("SAAIA_DOCUMENTS_ROOT", documentsRoot);
        Environment.SetEnvironmentVariable("SAAIA_INSTALL_ROOT", null);
        var sourceHash = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(expectedFile)))
            .ToLowerInvariant();

        var resolved = DocumentPathResolver.Resolve(
            "Old/Removed/manual.pdf",
            "sha256:" + sourceHash);

        Assert.Equal(Path.GetFullPath(expectedFile), resolved);
    }

    [Fact]
    public void Resolve_refuses_an_ambiguous_filename_without_an_immutable_hash()
    {
        var documentsRoot = Path.Combine(_tempRoot, "documents");
        var firstFile = Path.Combine(documentsRoot, "A", "manual.pdf");
        var secondFile = Path.Combine(documentsRoot, "B", "manual.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(firstFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(secondFile)!);
        File.WriteAllText(firstFile, "first revision");
        File.WriteAllText(secondFile, "second revision");
        Environment.SetEnvironmentVariable("SAAIA_DOCUMENTS_ROOT", documentsRoot);
        Environment.SetEnvironmentVariable("SAAIA_INSTALL_ROOT", null);

        var resolved = DocumentPathResolver.Resolve("Old/Removed/manual.pdf");

        Assert.Null(resolved);
    }
}
