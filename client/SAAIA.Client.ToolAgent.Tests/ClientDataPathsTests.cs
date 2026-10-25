using System.IO.Compression;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

[Collection("EnvironmentVariables")]
public sealed class ClientDataPathsTests : IDisposable
{
    private readonly string? _previous = Environment.GetEnvironmentVariable(ClientDataPaths.RootEnvironmentVariable);
    private readonly string _ownedRoot = Path.Combine(Path.GetTempPath(), "saaia-isolated-profile-" + Guid.NewGuid().ToString("N"));

    public ClientDataPathsTests()
        => Environment.SetEnvironmentVariable(ClientDataPaths.RootEnvironmentVariable, _ownedRoot);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ClientDataPaths.RootEnvironmentVariable, _previous);
        if (Directory.Exists(_ownedRoot)) Directory.Delete(_ownedRoot, recursive: true);
    }

    [Fact]
    public void Default_profile_path_is_unchanged_and_relative_override_is_rejected()
    {
        Environment.SetEnvironmentVariable(ClientDataPaths.RootEnvironmentVariable, null);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SAAIA"), ClientDataPaths.Root);
        Assert.True(ClientDataPaths.UsePackagedSettings);
        Environment.SetEnvironmentVariable(ClientDataPaths.RootEnvironmentVariable, "relative-profile");
        Assert.Throws<InvalidOperationException>(() => ClientDataPaths.Root);
        Assert.False(ClientDataPaths.UsePackagedSettings);
    }

    [Fact]
    public void Separate_profiles_have_independent_settings_user_identity_and_dpapi_secrets()
    {
        Assert.False(ClientDataPaths.UsePackagedSettings);
        var firstRoot = Path.Combine(_ownedRoot, "first");
        var secondRoot = Path.Combine(_ownedRoot, "second");
        Environment.SetEnvironmentVariable(ClientDataPaths.RootEnvironmentVariable, firstRoot);
        new AppSettings { BackendUrl = "http://127.0.0.1:15127", AutoConnect = false }.Save();
        var firstUser = SecureLocalStore.GetOrCreateUserId();
        SecureLocalStore.SetServerApiKey("synthetic-first-profile");
        SecureLocalStore.SetOpenAiApiKey("synthetic-openai-first-profile");
        Assert.DoesNotContain("synthetic-first-profile", File.ReadAllText(Path.Combine(firstRoot, "client", "secure.json")));

        Environment.SetEnvironmentVariable(ClientDataPaths.RootEnvironmentVariable, secondRoot);
        Assert.Null(SecureLocalStore.GetServerApiKey());
        Assert.Null(SecureLocalStore.GetOpenAiApiKey());
        Assert.NotEqual(firstUser, SecureLocalStore.GetOrCreateUserId());
        new AppSettings { BackendUrl = "http://127.0.0.1:25127", AutoConnect = false }.Save();
        SecureLocalStore.SetServerApiKey("synthetic-second-profile");

        Environment.SetEnvironmentVariable(ClientDataPaths.RootEnvironmentVariable, firstRoot);
        Assert.Equal("http://127.0.0.1:15127", AppSettings.Load().BackendUrl);
        Assert.Equal(firstUser, SecureLocalStore.GetOrCreateUserId());
        Assert.Equal("synthetic-first-profile", SecureLocalStore.GetServerApiKey());
        Assert.Equal("synthetic-openai-first-profile", SecureLocalStore.GetOpenAiApiKey());
    }

    [Fact]
    public void Explicit_profile_only_discovers_its_own_provisioning_and_installer()
    {
        Assert.Null(Provisioning.FindProvisioningPath());
        Assert.Null(LlmRepairScriptRunner.FindInstallScriptPath());
        Directory.CreateDirectory(_ownedRoot);
        var provisioning = Path.Combine(_ownedRoot, "provisioning.json");
        File.WriteAllText(provisioning, "{}");
        Assert.Equal(provisioning, Provisioning.FindProvisioningPath());
        var script = Path.Combine(_ownedRoot, "install-llm.ps1");
        File.WriteAllText(script, "# fixture only; never executed");
        Assert.Equal(script, LlmRepairScriptRunner.FindInstallScriptPath());
    }

    [Fact]
    public async Task Logs_exports_governance_and_support_use_the_explicit_profile()
    {
        ClientLog.Info("isolated-profile-fixture-marker");
        Assert.Contains("isolated-profile-fixture-marker", File.ReadAllText(Path.Combine(_ownedRoot, "logs", "client_startup.log")));
        var export = ExportService.Create("txt", "synthetic-fixture", "synthetic export");
        Assert.Equal(Path.Combine(_ownedRoot, "exports"), Path.GetDirectoryName(export));
        await GovernanceArtifactStore.WriteAsync("fixture.json", new { fixture = true });
        Assert.True(File.Exists(Path.Combine(_ownedRoot, "governance", "fixture.json")));
        UserPrefsStore.SaveLanguage("de");
        Assert.True(File.Exists(Path.Combine(_ownedRoot, "user-prefs.bin")));
        Assert.Equal("de", UserPrefsStore.Load().Language);
        Assert.Equal(Path.Combine(_ownedRoot, "Models"), ModelLibrary.ModelsDir);
        Assert.Equal(Path.Combine(_ownedRoot, "llm", "runtime"), LlamaCppReleaseDownloader.RuntimeRoot);
        new AppSettings { BackendUrl = "http://127.0.0.1:15127", AutoConnect = false }.Save();
        var probeUris = new List<Uri>();
        var bundle = await SupportBundleBuilder.BuildAsync(new AppSettings(),
            probeWriter: (path, uri) =>
            {
                probeUris.Add(uri);
                return File.WriteAllTextAsync(path, "{\"fixture\":true}");
            });
        Assert.Equal("/ready", probeUris[0].AbsolutePath);
        Assert.Equal("/v1/models", probeUris[1].AbsolutePath);
        Assert.Equal(Path.Combine(_ownedRoot, "support"), Path.GetDirectoryName(bundle));
        using var archive = ZipFile.OpenRead(bundle);
        using var reader = new StreamReader(archive.GetEntry("settings.json")!.Open());
        using var settings = JsonDocument.Parse(await reader.ReadToEndAsync());
        Assert.Equal("http://127.0.0.1:15127", settings.RootElement.GetProperty("BackendUrl").GetString()!.TrimEnd('/'));
        Assert.NotNull(archive.GetEntry("logs/client_startup.log"));
        Assert.NotNull(archive.GetEntry("collection-manifest.json"));
        Assert.Null(archive.GetEntry("secure.json"));
    }
}
