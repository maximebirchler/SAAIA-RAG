using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Projects an explicitly selected native PDF subset through production code.
/// No host, database, OCR, Docling, embedding, provider or network is started.
/// This is a projection fixture, not an end-to-end ingestion worker run.
/// </summary>
internal static class NativePaidCorpusProjection
{
    internal static void Run(string[] args)
    {
        var pdf = Path.GetFullPath(Argument(args, "--pdf"));
        var output = Path.GetFullPath(Argument(args, "--output"));
        var docPath = Argument(args, "--doc-path");
        var expectedSource = Argument(args, "--source-sha256");
        var expectedBackend = Argument(args, "--backend-sha256");
        var selected = Argument(args, "--pages").Split(',').Select(int.Parse).ToArray();
        if (selected.Length is < 1 or > 8 || selected.Distinct().Count() != selected.Length
            || selected.Any(page => page < 1) || !File.Exists(pdf) || File.Exists(output)
            || docPath != "Corpus/" + Path.GetFileName(pdf))
            throw new ArgumentException("A new output, real PDF and one to eight distinct physical pages are required.");

        var backendPath = typeof(PdfExtractor).Assembly.Location;
        var backendHash = Hash(File.ReadAllBytes(backendPath));
        var source = File.ReadAllBytes(pdf);
        var sourceHash = Hash(source);
        if (!string.Equals(expectedSource, sourceHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expectedBackend, backendHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Source or loaded production backend differs from the frozen campaign.");

        // Extract the entire source first: production repeated-boilerplate/layout
        // handling requires the document context. Only selected page text is
        // then admitted to the bounded projection and its contextual neighbors.
        var extraction = PdfExtractor.Extract(pdf);
        var pages = extraction.Pages.Where(page => selected.Contains(page.PageNumber))
            .OrderBy(page => page.PageNumber).ToArray();
        if (pages.Length != selected.Length || pages.Any(page => string.IsNullOrWhiteSpace(page.Text)
                || page.Text.Length > 30_000) || pages.Sum(page => page.Text.Length) > 120_000)
            throw new InvalidDataException("Selected native pages are missing, empty/OCR-only or exceed the bounded corpus.");

        var sections = DocumentSectionExtractor.Extract(pages);
        var units = DocumentUnitExtractor.Extract(pages, sections);
        var options = new IngestionOptions();
        var allChunks = RetrievalChunkProjector.ProjectStructureAware(sections, units,
            options.ChunkMaxWords, options.ChunkOverlapWords, options.ChunkMinWords);
        var index = IngestionProjectionStage.ProjectIndex(docPath, sections, units, allChunks,
            profileUnits: null, logger: NullLogger.Instance);
        var chunks = allChunks.Where(IngestionWorker.ShouldPublishRetrievalChunk).ToArray();
        var publishedIndexes = chunks.Select(chunk => chunk.ChunkIndex).ToHashSet();
        var contextual = index.ContextualEntries.Where(entry => publishedIndexes.Contains(entry.ChunkIndex)).ToArray();
        if (units.Count == 0 || chunks.Length == 0 || contextual.Length != chunks.Length)
            throw new InvalidDataException("Production projection returned no searchable canonical corpus.");

        // Private diagnostic alternatives never replace the selected page.Text.
        // Record the exact inputs of both existing layout algorithms so their
        // reading order and retention can be compared without a model call.
        using var diagnosticPdf = UglyToad.PdfPig.PdfDocument.Open(pdf);
        static object DescribeWord(UglyToad.PdfPig.Content.Word word) => new
        {
            word.Text, word.BoundingBox.Left, word.BoundingBox.Right,
            word.BoundingBox.Top, word.BoundingBox.Bottom
        };
        var wordGeometry = pages.Select(page =>
        {
            var sourcePage = diagnosticPdf.GetPage(page.PageNumber);
            return new
            {
                page.PageNumber,
                defaultWords = sourcePage.GetWords().Select(DescribeWord).ToArray(),
                nearestNeighbourWords = sourcePage.GetWords(
                    UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor.NearestNeighbourWordExtractor.Instance)
                    .Select(DescribeWord).ToArray()
            };
        }).ToArray();

        var snapshot = new
        {
            schemaVersion = "saaia-native-paid-corpus-projection.v1",
            backendAssemblySha256 = backendHash,
            sourceSha256 = sourceHash,
            sourcePageCount = extraction.Pages.Count,
            docPath,
            projection = "production_native_pdf_selected_pages",
            chunking = new { options.ChunkMaxWords, options.ChunkOverlapWords, options.ChunkMinWords },
            pages = pages.Select(page => new
            {
                page.PageNumber, page.Text, page.WordCount, page.CharCount, page.Checksum, page.Quality,
                page.RawText, page.NativeLayoutText, page.NativeLayoutBlocks, page.NativeLayoutAlgorithm
            }),
            sections,
            units,
            retrievalChunks = chunks,
            exactEntries = index.ExactEntries,
            contextualEntries = contextual,
            diagnosticWordGeometryByPhysicalPage = wordGeometry,
            boundaries = new
            {
                productionProjectionReused = true,
                entireSourceNativeExtraction = true,
                sectionAndNeighborContextLimitedToSelectedPages = true,
                ingestionWorkerExecuted = false,
                doclingExecuted = false,
                ocrExecuted = false,
                vectorsIndexed = false,
                documentProfilesProjected = false,
                additionalLayoutSurfacesAreDiagnosticsOnly = true
            }
        };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(file, snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        });
    }

    private static string Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            throw new ArgumentException($"Missing explicit argument {name}.");
        return args[index + 1];
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
