using Dapper;
using SAAIA.Backend.Retrieval;
using Xunit;

namespace SAAIA.Backend.Tests;

[Trait("Profile", "IntegrationPg")]
public sealed class CanonicalIndexProbeIntegrationTests
{
    [ProfileFact("IntegrationPg")]
    public async Task Presence_is_scoped_to_tenant_current_revision_and_exact_page_or_chunk()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid(); var doc = Guid.NewGuid();
        var oldRevision = Guid.NewGuid(); var currentRevision = Guid.NewGuid();
        var firstChunk = Guid.NewGuid(); var lastChunk = Guid.NewGuid();
        var context = new RetrievalExecutionContext(tenant, "canonical-presence-integration", CancellationToken.None);
        Assert.Equal("document_not_found", (await CanonicalIndexProbe.CheckAsync(context, database.DataSource,
            doc, null, null, null)).Status);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("""
            INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'Synthetic diagnostic tenant');
            INSERT INTO documents(tenant_id,doc_id,doc_path,doc_name,category,status,ingestion_version,indexed_version)
              VALUES(@tenant,@doc,'Synthetic/diagnostic.pdf','diagnostic.pdf','synthetic','indexed',2,2);
            INSERT INTO document_revisions(revision_id,tenant_id,doc_id,doc_path,source_hash,ingestion_version,indexed_version)
              VALUES(@old_revision,@tenant,@doc,'Synthetic/diagnostic.pdf',decode(repeat('ab',32),'hex'),1,1),
                    (@current_revision,@tenant,@doc,'Synthetic/diagnostic.pdf',decode(repeat('cd',32),'hex'),2,2);
            INSERT INTO retrieval_chunks(retrieval_chunk_id,tenant_id,revision_id,chunk_index,page_start,page_end,text_content)
              VALUES(@first_chunk,@tenant,@current_revision,0,1,1,'Synthetic calibration first page.'),
                    (@last_chunk,@tenant,@current_revision,1,4,4,'Synthetic calibration last page.');
            """, new { tenant, doc, old_revision = oldRevision, current_revision = currentRevision,
                first_chunk = firstChunk, last_chunk = lastChunk });

        var current = await CanonicalIndexProbe.CheckAsync(context, database.DataSource, doc, null, 4, lastChunk);
        Assert.Equal("canonical_present", current.Status);
        Assert.Equal(currentRevision, current.RevisionId);
        Assert.Equal(2, current.RevisionChunkCount);
        Assert.Equal(1, current.MatchingChunkCount);
        Assert.Equal(4, current.LastPhysicalPage);
        Assert.False(current.VectorPresenceChecked);
        Assert.Equal("canonical-postgres", current.Scope);
        Assert.Equal("requested_locator_not_present", (await CanonicalIndexProbe.CheckAsync(context,
            database.DataSource, doc, currentRevision, 2, null)).Status);
        Assert.Equal("requested_locator_not_present", (await CanonicalIndexProbe.CheckAsync(context,
            database.DataSource, doc, currentRevision, null, Guid.NewGuid())).Status);
        Assert.Equal("revision_not_current", (await CanonicalIndexProbe.CheckAsync(context,
            database.DataSource, doc, oldRevision, null, null)).Status);
        Assert.Equal("revision_not_found", (await CanonicalIndexProbe.CheckAsync(context,
            database.DataSource, doc, Guid.NewGuid(), null, null)).Status);
        Assert.Equal("document_not_found", (await CanonicalIndexProbe.CheckAsync(context with { TenantId = Guid.NewGuid() },
            database.DataSource, doc, null, null, null)).Status);
    }
}
