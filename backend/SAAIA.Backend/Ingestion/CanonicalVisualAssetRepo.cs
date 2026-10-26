using System.Security.Cryptography;
using Dapper;
using Npgsql;
using SAAIA.Contracts.DocumentIntelligence;

/// <summary>Immutable revision-scoped pixels published in the same transaction as their canonical identities.</summary>
internal static partial class CanonicalVisualAssetRepo
{
    internal static async Task StoreAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid tenantId, CanonicalDocument document, CancellationToken ct)
    {
        if (document.Assets.Count > 512 || document.Assets.Sum(asset => asset.SizeBytes) > 128L * 1024 * 1024)
            throw new InvalidDataException("canonical_visual_asset_document_budget_exceeded");
        foreach (var asset in document.Assets)
        {
            if (asset.PublicationBytes is null)
                throw new InvalidDataException("canonical_visual_asset_publication_payload_missing");
            var png = CanonicalPngAsset.Validate(asset.PublicationBytes);
            if (asset.MediaType != "image/png" || asset.PageNumber is not > 0
                || asset.PixelWidth != png.Width || asset.PixelHeight != png.Height
                || asset.SizeBytes != png.Bytes.Length
                || !string.Equals(asset.Sha256, Convert.ToHexString(SHA256.HashData(png.Bytes)), StringComparison.OrdinalIgnoreCase)
                || !document.Pages.Any(page => page.PageNumber == asset.PageNumber
                    && (asset.FigureId is null || page.Figures.Any(figure => figure.FigureId == asset.FigureId))))
                throw new InvalidDataException("canonical_visual_asset_identity_invalid");
            var scope = new
            {
                tenant = tenantId, revision = document.RevisionId, document = document.DocumentId,
                source = Convert.FromHexString(document.Source.Sha256), asset = asset.AssetId,
                page = asset.PageNumber, figure = asset.FigureId, hash = Convert.FromHexString(asset.Sha256),
                width = png.Width, height = png.Height, payload = png.Bytes
            };
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO document_revision_visual_assets(tenant_id,revision_id,asset_id,page_number,figure_id,
                    source_hash,content_hash,media_type,pixel_width,pixel_height,payload)
                SELECT @tenant,@revision,@asset,@page,@figure,@source,@hash,'image/png',@width,@height,@payload
                FROM document_revisions dr WHERE dr.tenant_id=@tenant AND dr.revision_id=@revision
                    AND dr.doc_id=@document AND dr.source_hash=@source
                ON CONFLICT (tenant_id,revision_id,asset_id) DO NOTHING;
                """, scope, transaction, cancellationToken: ct));
            var exact = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS(SELECT 1 FROM document_revision_visual_assets a
                    JOIN document_revisions dr ON dr.revision_id=a.revision_id AND dr.tenant_id=a.tenant_id
                    WHERE a.tenant_id=@tenant AND a.revision_id=@revision AND dr.doc_id=@document
                    AND dr.source_hash=@source AND a.asset_id=@asset AND a.page_number=@page
                    AND a.figure_id IS NOT DISTINCT FROM @figure AND a.source_hash=@source
                    AND a.content_hash=@hash AND a.media_type='image/png' AND a.pixel_width=@width
                    AND a.pixel_height=@height AND a.payload=@payload);
                """, scope, transaction, cancellationToken: ct));
            if (!exact) throw new InvalidDataException("canonical_visual_asset_immutable_publication_conflict");
        }
    }
}
