using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SAAIA.Backend;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Db;
using SAAIA.Backend.Endpoints;
using SAAIA.Backend.Models;
using SAAIA.Backend.Security;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class LiveCanonicalVerticalBackendProbeTests
{
    [Fact]
    public Task Real_pdf_backend_hosts_live_client_agent()
        => DocumentFoundationIntegrationTests.RunLocalVerticalProbeAsync();
}

public sealed partial class DocumentFoundationIntegrationTests
{
    internal static async Task RunLocalVerticalProbeAsync()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SAAIA_RUN_LOCAL_VERTICAL_PROBE"));
        var export = Environment.GetEnvironmentVariable("SAAIA_TEST_CONTRACT_EXPORT_DIR")!;
        var repo = Environment.GetEnvironmentVariable("SAAIA_PROBE_REPO_ROOT")!;
        Assert.True(Path.IsPathFullyQualified(export) && Directory.Exists(export));
        Assert.True(Path.IsPathFullyQualified(repo) && Directory.Exists(repo));
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        var serverRoot = Path.Combine(export, "server-documents");
        const string docPath = "Knowledge/Calibration-record.pdf";
        var pdfPath = Path.Combine(serverRoot, "Knowledge", "Calibration-record.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(pdfPath)!);
        File.Copy(Path.Combine(repo, "client", "SAAIA.Client.ToolAgent.Tests", "Fixtures", "Calibration-record.pdf"), pdfPath);
        var bytes = await File.ReadAllBytesAsync(pdfPath);
        var hash = SHA256.HashData(bytes);
        var extraction = PdfExtractor.Extract(pdfPath);
        Assert.Equal(2, extraction.Pages.Count);
        var sections = DocumentSectionExtractor.Extract(extraction.Pages);
        var units = DocumentUnitExtractor.Extract(extraction.Pages, sections);
        var ingestion = new IngestionOptions();
        var chunks = RetrievalChunkProjector.ProjectStructureAware(sections, units,
            ingestion.ChunkMaxWords, ingestion.ChunkOverlapWords, ingestion.ChunkMinWords);
        Assert.Equal(2, chunks.Count);
        var tenant = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        await db.SeedRunningJobAsync(tenant, docId, jobId, docPath, 1, 0);
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(ds, tenant, jobId, docPath,
            hash, bytes.Length, File.GetLastWriteTimeUtc(pdfPath), 1, extraction.Pages,
            sections, units, chunks, ExactMatchEntryExtractor.Extract(units),
            ContextualTextProjector.Project(docPath, sections, units, chunks), CancellationToken.None));
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Guid revisionId;
        IEnumerable<dynamic> publishedCards;
        IEnumerable<dynamic> publishedAnchors;
        await using (var conn = await ds.OpenConnectionAsync())
        {
            revisionId = await conn.ExecuteScalarAsync<Guid>(
                "SELECT revision_id FROM document_revisions WHERE tenant_id=@tenant AND doc_id=@docId AND indexed_version=1;", new { tenant, docId });
            await conn.ExecuteAsync("""
                INSERT INTO api_keys(api_key_id, tenant_id, key_prefix, key_hash, label, is_admin)
                VALUES(@id, @tenant, @prefix, @hash, 'isolated-local-vertical', FALSE);
                """, new { id = Guid.NewGuid(), tenant, prefix = ApiKeyAuth.Prefix(key), hash = ApiKeyAuth.Sha256Bytes(key, "") });
            publishedCards = await conn.QueryAsync(
                "SELECT content_card_id::text AS id, page_start, page_end, title FROM document_profile_content_cards WHERE tenant_id=@tenant AND revision_id=@revisionId;", new { tenant, revisionId });
            publishedAnchors = await conn.QueryAsync(
                "SELECT title_anchor_id::text AS id, page_start, page_end, title FROM document_title_anchors WHERE tenant_id=@tenant AND revision_id=@revisionId;", new { tenant, revisionId });
        }
        await File.WriteAllTextAsync(Path.Combine(export, "published-pdf.json"), JsonSerializer.Serialize(new
        {
            docId,
            revisionId,
            docPath,
            sourceHash = Convert.ToHexString(hash).ToLowerInvariant(),
            syntheticCorpus = true,
            extraction.Source,
            cards = publishedCards,
            anchors = publishedAnchors,
            chunks = chunks.Select(chunk => new
            {
                chunkId = DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 1, chunk.ChunkIndex),
                chunk.PageStart,
                chunk.PageEnd,
                chunk.Text
            }),
            pages = extraction.Pages.Select(page => new { page.PageNumber, page.Text })
        }, new JsonSerializerOptions { WriteIndented = true }));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = serverRoot
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = db.ConnectionString,
            ["Auth:Pepper"] = "",
            ["Ingestion:DocumentsRoot"] = serverRoot,
            ["Ingestion:WatcherEnabled"] = "false",
            ["Ingestion:ScannerEnabled"] = "false",
            // Preflight verifies that these dependencies are unavailable. No
            // fabricated dense/vector result is supplied to the client agent.
            ["Rag:EmbeddingsBaseUrl"] = "http://127.0.0.1:8081/",
            ["Rag:QdrantBaseUrl"] = "http://127.0.0.1:6333/",
            ["Rag:EnableRerank"] = "false"
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddSaaiaServices(builder.Configuration, builder.Environment);
        // Program normally registers this via SignedConfigLoader. This test
        // host does not run that loader and must report that fact explicitly.
        builder.Services.AddSingleton(new SignedConfigStatus { Mode = "test-harness-unverified" });
        foreach (var service in builder.Services.Where(service => service.ServiceType == typeof(IHostedService)
                     && service.ImplementationType?.Assembly == typeof(RagEndpoints).Assembly).ToArray())
            builder.Services.Remove(service);
        await using var app = builder.Build();
        app.UseSaaiaPipeline();
        app.MapSaaiaEndpoints();
        await app.StartAsync();
        try
        {
            var address = Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses);
            using (var preflight = new HttpClient(new HttpClientHandler { UseProxy = false })
            { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) })
            {
                preflight.DefaultRequestHeaders.Add("X-Api-Key", key);
                using var catalogResponse = await preflight.GetAsync("/catalog/documents?pageSize=100&orderby=name_asc&q=Calibration-record.pdf");
                Assert.True(catalogResponse.IsSuccessStatusCode, $"Catalog preflight HTTP {(int)catalogResponse.StatusCode}");
                var catalogBody = await catalogResponse.Content.ReadAsStringAsync();
                Assert.Contains(docId.ToString(), catalogBody, StringComparison.OrdinalIgnoreCase);
                using var searchResponse = await preflight.PostAsJsonAsync("/rag/search", new RagSearchRequestDto(
                    "calibration measurement", DocId: docId.ToString(), TopK: 20, SourceBackedCanonical: true));
                Assert.True(searchResponse.IsSuccessStatusCode, $"Search preflight HTTP {(int)searchResponse.StatusCode}");
                var searchBody = JsonSerializer.Deserialize<JsonElement>(await searchResponse.Content.ReadAsStringAsync());
                Assert.Equal(2, searchBody.GetProperty("items").GetArrayLength());
                await File.WriteAllTextAsync(Path.Combine(export, "documentary-preflight.json"), JsonSerializer.Serialize(new
                {
                    catalog = JsonSerializer.Deserialize<JsonElement>(catalogBody),
                    search = searchBody,
                    qwenHasNotYetBeenCalled = true
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            var clientOutput = Path.Combine(export, "client");
            Directory.CreateDirectory(clientOutput);
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = repo,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { "test", "client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj",
                "-c", "Debug", "-p:Platform=x64", "--no-build", "--no-restore", "--nologo",
                "--filter", "FullyQualifiedName=SAAIA.Client.ToolAgent.Tests.LiveCanonicalVerticalClientProbeTests.Collect_real_agent_responses_and_exact_source_cards",
                "--results-directory", clientOutput, "--logger", "trx;LogFileName=local-vertical-client.trx" })
                start.ArgumentList.Add(argument);
            start.Environment["SAAIA_PROBE_BACKEND_URL"] = address;
            start.Environment["SAAIA_PROBE_API_KEY"] = key;
            start.Environment["SAAIA_PROBE_OUTPUT"] = clientOutput;
            start.Environment["SAAIA_PROBE_PDF_MANIFEST"] = Path.Combine(export, "published-pdf.json");
            start.Environment["SAAIA_DOCUMENTS_ROOT"] = serverRoot;
            start.Environment.Remove("SAAIA_TEST_PG_CONN");
            start.Environment.Remove("PGPASSWORD");
            using var client = Process.Start(start)!;
            var stdout = client.StandardOutput.ReadToEndAsync();
            var stderr = client.StandardError.ReadToEndAsync();
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(11));
                await client.WaitForExitAsync(deadline.Token);
            }
            finally
            {
                if (!client.HasExited)
                    client.Kill(entireProcessTree: true);
                await client.WaitForExitAsync();
                await File.WriteAllTextAsync(Path.Combine(clientOutput, "client.stdout.log"), await stdout);
                await File.WriteAllTextAsync(Path.Combine(clientOutput, "client.stderr.log"), await stderr);
            }
            await File.WriteAllTextAsync(Path.Combine(export, "host-observation.json"), JsonSerializer.Serialize(new
            {
                address,
                childPid = client.Id,
                clientExitCode = client.ExitCode,
                realHttp = true,
                realQwen = true,
                vectorServicesAvailable = false,
                programStartupTested = false,
                winUiWindowTested = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            Assert.Equal(0, client.ExitCode);
            Assert.True(File.Exists(Path.Combine(clientOutput, "observations.json")));
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
