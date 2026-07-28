using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SAAIA.Contracts.DocumentIntelligence;

var pdfPath = RequireArgument(args, "--pdf");
var responsePath = RequireArgument(args, "--response");
var outputPath = RequireArgument(args, "--output");
var maxWords = ReadIntegerArgument(args, "--chunk-max-words", 220);
var minWords = ReadIntegerArgument(args, "--chunk-min-words", 1);

if (!File.Exists(pdfPath))
    throw new FileNotFoundException("The PDF fixture does not exist.", pdfPath);
if (!File.Exists(responsePath))
{
    throw new FileNotFoundException(
        "The captured Docling response does not exist.",
        responsePath);
}

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};
var response = JsonSerializer.Deserialize<DoclingConvertResponse>(
        File.ReadAllText(responsePath, Encoding.UTF8),
        jsonOptions)
    ?? throw new InvalidDataException("The Docling response is empty.");
var source = response.Document.JsonContent
    ?? throw new InvalidDataException(
        "The Docling response does not contain document.json_content.");

var sourceBytes = File.ReadAllBytes(pdfPath);
var sourceSha256 = Convert.ToHexString(SHA256.HashData(sourceBytes))
    .ToLowerInvariant();
var canonical = DoclingCanonicalDocumentAdapter.Project(
    new(
        StableGuid(sourceSha256, "document"),
        StableGuid(sourceSha256, "revision"),
        sourceSha256,
        sourceBytes.LongLength,
        Path.GetFileName(pdfPath),
        new string('0', 64),
        "canonical_projection",
        string.IsNullOrWhiteSpace(source.Version)
            ? "docling"
            : source.Version,
        "ingestion_gold"),
    source);
var nativeExtraction = PdfExtractor.Extract(pdfPath);
var reconciliation =
    CanonicalNativeTextCoverageReconciler.Apply(
        canonical,
        nativeExtraction);
var imageInventory =
    CanonicalNativePdfImageInventoryReconciler.Apply(
        canonical,
        nativeExtraction);
CanonicalContractValidator.ValidateOrThrow(canonical);

var retrievalChunks = DoclingCanonicalRetrievalProjector.Project(
        canonical,
        source,
        maxWords,
        minWords)
    .Where(IngestionWorker.ShouldPublishRetrievalChunk)
    .ToArray();
var snapshot = new
{
    schemaVersion = "ingestion_layout_canonical_snapshot_v1",
    sourceSha256,
    document = canonical,
    reconciliation,
    imageInventory,
    nativeLayoutPages = nativeExtraction.Pages
        .OrderBy(static page => page.PageNumber)
        .Select(static page => new
        {
            pageNumber = page.PageNumber,
            page.WidthPoints,
            page.HeightPoints,
            algorithm = page.NativeLayoutAlgorithm,
            blocks = (page.NativeLayoutBlocks
                    ?? Array.Empty<ExtractedPdfLayoutBlock>())
                .OrderBy(static block => block.ReadingOrder)
                .Select(static block => new
                {
                    block.ReadingOrder,
                    block.Text,
                    block.Left,
                    block.Right,
                    block.Top,
                    block.Bottom
                })
                .ToArray()
        })
        .ToArray(),
    retrievalChunks
};

var resolvedOutput = Path.GetFullPath(outputPath);
Directory.CreateDirectory(
    Path.GetDirectoryName(resolvedOutput)
    ?? throw new InvalidOperationException("The output path has no directory."));
File.WriteAllText(
    resolvedOutput,
    JsonSerializer.Serialize(snapshot, jsonOptions) + Environment.NewLine,
    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

static string RequireArgument(
    IReadOnlyList<string> arguments,
    string name)
{
    for (var index = 0; index < arguments.Count - 1; index++)
    {
        if (string.Equals(
                arguments[index],
                name,
                StringComparison.Ordinal))
        {
            var value = arguments[index + 1];
            if (!string.IsNullOrWhiteSpace(value))
                return Path.GetFullPath(value);
        }
    }

    throw new ArgumentException($"Missing required argument {name}.");
}

static int ReadIntegerArgument(
    IReadOnlyList<string> arguments,
    string name,
    int fallback)
{
    for (var index = 0; index < arguments.Count - 1; index++)
    {
        if (!string.Equals(
                arguments[index],
                name,
                StringComparison.Ordinal))
        {
            continue;
        }

        if (int.TryParse(arguments[index + 1], out var value)
            && value > 0)
        {
            return value;
        }

        throw new ArgumentException(
            $"Argument {name} must be a positive integer.");
    }

    return fallback;
}

static Guid StableGuid(string sourceSha256, string role)
{
    var digest = SHA256.HashData(
        Encoding.UTF8.GetBytes($"{sourceSha256}:{role}"));
    return new Guid(digest.AsSpan(0, 16));
}
