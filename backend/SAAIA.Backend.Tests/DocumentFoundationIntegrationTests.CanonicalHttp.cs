using System.Net;
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
using SAAIA.Backend.Endpoints;
using SAAIA.Backend.Models;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class DocumentFoundationIntegrationTests
{
    [Fact]
    public async Task Canonical_http_authentication_scopes_search_and_context_to_the_key_tenant()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;
        var firstTenant = Guid.NewGuid();
        var secondTenant = Guid.NewGuid();
        var firstDoc = Guid.NewGuid();
        var secondDoc = Guid.NewGuid();
        const string docPath = "Knowledge/http-contract.pdf";
        await PublishIndexedDocumentAsync(db, firstTenant, firstDoc, Guid.NewGuid(), docPath, 1,
            [new RuntimeSeedSection("Amber verification", "Amber verification records the first tenant's instrument and its original measurement procedure.")]);
        await PublishIndexedDocumentAsync(db, secondTenant, secondDoc, Guid.NewGuid(), docPath, 1,
            [new RuntimeSeedSection("Violet verification", "Violet verification records the second tenant's instrument and its separate measurement procedure.")]);
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var firstKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var secondKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var firstKeyId = Guid.NewGuid();
        await using (var conn = await ds.OpenConnectionAsync())
        {
            const string insertKey = """
                INSERT INTO api_keys(api_key_id, tenant_id, key_prefix, key_hash, label, is_admin)
                VALUES(@id, @tenant, @prefix, @hash, 'isolated-http-contract', FALSE);
                """;
            await conn.ExecuteAsync(insertKey, new { id = firstKeyId, tenant = firstTenant,
                prefix = ApiKeyAuth.Prefix(firstKey), hash = ApiKeyAuth.Sha256Bytes(firstKey, "") });
            await conn.ExecuteAsync(insertKey, new { id = Guid.NewGuid(), tenant = secondTenant,
                prefix = ApiKeyAuth.Prefix(secondKey), hash = ApiKeyAuth.Sha256Bytes(secondKey, "") });
        }

        var ownedRoot = Directory.CreateTempSubdirectory("saaia-http-contract-");
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = ownedRoot.FullName
            });
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = db.ConnectionString,
                ["Auth:Pepper"] = "",
                ["Ingestion:DocumentsRoot"] = ownedRoot.FullName,
                ["Ingestion:WatcherEnabled"] = "false",
                ["Ingestion:ScannerEnabled"] = "false",
                ["Rag:EmbeddingsBaseUrl"] = "http://tei.test/",
                ["Rag:QdrantBaseUrl"] = "http://qdrant.test/",
                ["Rag:EnableRerank"] = "false"
            });
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Services.AddSaaiaServices(builder.Configuration, builder.Environment);
            // This host tests the real HTTP/auth/endpoint pipeline over already
            // published synthetic data. Workers, signed startup and external
            // embedding/vector services are outside this integration test.
            foreach (var service in builder.Services.Where(service => service.ServiceType == typeof(IHostedService)
                && service.ImplementationType?.Assembly == typeof(RagEndpoints).Assembly).ToArray())
                builder.Services.Remove(service);
            builder.Services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory());
            await using var app = builder.Build();
            app.UseSaaiaPipeline();
            RagEndpoints.Map(app);
            DocumentsEndpoints.Map(app);
            await app.StartAsync();
            try
            {
                var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
                var address = Assert.Single(addresses!.Addresses);
                using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
                    { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20) };
                var request = new RagSearchRequestDto("verification", TopK: 20, SourceBackedCanonical: true);
                var observations = new List<object>();
                async Task<JsonElement> SearchAsync(string? key, RagSearchRequestDto search, HttpStatusCode expected)
                {
                    using var message = new HttpRequestMessage(HttpMethod.Post, "/rag/search") { Content = JsonContent.Create(search) };
                    if (key is not null)
                        message.Headers.Add("X-Api-Key", key);
                    using var response = await client.SendAsync(message);
                    var payload = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
                    Assert.Equal(expected, response.StatusCode);
                    observations.Add(new { operation = "search", status = (int)response.StatusCode, payload });
                    return payload;
                }
                await SearchAsync(null, request, HttpStatusCode.Unauthorized);
                await SearchAsync("invalid-test-key", request, HttpStatusCode.Unauthorized);
                var first = await SearchAsync(firstKey, request, HttpStatusCode.OK);
                var firstHit = Assert.Single(first.GetProperty("items").EnumerateArray());
                Assert.Equal(firstDoc.ToString(), firstHit.GetProperty("docId").GetString());
                Assert.Contains("Amber verification", firstHit.GetProperty("text").GetString());
                var second = await SearchAsync(secondKey, request, HttpStatusCode.OK);
                var secondHit = Assert.Single(second.GetProperty("items").EnumerateArray());
                Assert.Equal(secondDoc.ToString(), secondHit.GetProperty("docId").GetString());
                Assert.Contains("Violet verification", secondHit.GetProperty("text").GetString());
                var crossTenantSearch = await SearchAsync(firstKey, request with { DocId = secondDoc.ToString() }, HttpStatusCode.OK);
                Assert.Empty(crossTenantSearch.GetProperty("items").EnumerateArray());

                async Task<JsonElement> ContextAsync(string key, Guid docId, string chunkId)
                {
                    using var message = new HttpRequestMessage(HttpMethod.Get,
                        $"/documents/context?docId={docId}&chunkId={chunkId}&before=1&after=1&limit=5");
                    message.Headers.Add("X-Api-Key", key);
                    using var response = await client.SendAsync(message);
                    var payload = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    observations.Add(new { operation = "context", status = (int)response.StatusCode, payload });
                    return payload;
                }
                var context = await ContextAsync(firstKey, firstDoc, firstHit.GetProperty("chunkId").GetString()!);
                Assert.True(context.GetProperty("found").GetBoolean());
                Assert.True(context.GetProperty("anchorFound").GetBoolean());
                var otherContext = await ContextAsync(secondKey, firstDoc, firstHit.GetProperty("chunkId").GetString()!);
                Assert.False(otherContext.GetProperty("found").GetBoolean());
                Assert.Equal("doc_not_found", otherContext.GetProperty("error").GetString());
                Assert.False(otherContext.TryGetProperty("items", out _));
                Assert.DoesNotContain("Amber verification", otherContext.GetRawText());
                await using (var conn = await ds.OpenConnectionAsync())
                    await conn.ExecuteAsync("UPDATE api_keys SET revoked_at=now() WHERE api_key_id=@id;", new { id = firstKeyId });
                await SearchAsync(firstKey, request, HttpStatusCode.Unauthorized);

                var export = Environment.GetEnvironmentVariable("SAAIA_TEST_CONTRACT_EXPORT_DIR");
                if (!string.IsNullOrWhiteSpace(export))
                {
                    Assert.True(Path.IsPathFullyQualified(export) && Directory.Exists(export));
                    var artifact = Path.Combine(export, "canonical-http-tenant-contract.json");
                    Assert.False(File.Exists(artifact));
                    File.WriteAllText(artifact, JsonSerializer.Serialize(new
                    {
                        schemaVersion = 1, syntheticCorpus = true, postgresVerified = true,
                        transport = "loopback-kestrel", realApiKeyMiddleware = true,
                        backgroundWorkersEnabled = false, externalRetrieversStubbed = true,
                        firstTenant, secondTenant, firstDoc, secondDoc, observations
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            finally
            {
                await app.StopAsync();
            }
        }
        finally
        {
            ownedRoot.Delete(recursive: false);
        }
    }
}
