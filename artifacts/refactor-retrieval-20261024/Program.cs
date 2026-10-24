using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var repo = Path.GetFullPath(args[0]);
if (args.Length > 1 && args[1] == "remove-unused")
{
    var targets = new (string Path, string Name)[] {
        ("Endpoints/DocumentsEndpoints.CatalogSupport.cs", "ParseJsonOrEmptyObject"),
        ("Endpoints/DocumentsEndpoints.CatalogSupport.cs", "ParseJsonOrEmptyArray"),
        ("Endpoints/DocumentsEndpoints.ExtractionQualityPages.cs", "RangesOverlap"),
        ("Retrieval/RetrievalCandidateSignals.cs", "ContainsPrimarySpecificSelectionAnchor"),
        ("Retrieval/RetrievalCandidateSignals.cs", "BuildMatchedContentCardCalibrationSignal"),
        ("Retrieval/RetrievalCandidateSignals.cs", "ComputeRawOrderedTitleScore"),
        ("Llm/CapabilityBBackofficeSummaryService.cs", "NormalizePreferredLanguage"),
        ("Pdf/DocumentTitleNavigationProjector.cs", "ContainsNormalizedPhrase"),
        ("Pdf/PdfOcrTextExtractor.cs", "RunProcessAsync"),
        ("Retrieval/RetrievalResultProjector.cs", "LoadRagDocumentSourceHashesAsync"),
        ("Retrieval/RetrievalResultProjector.cs", "ResolveDocumentSourceHash")
    };
    var checkedPaths = new[] { "backend", "client", "contracts" }.SelectMany(area =>
        Directory.EnumerateFiles(Path.Combine(repo, area), "*", SearchOption.AllDirectories)
            .Where(path => (path.EndsWith(".cs") || path.EndsWith(".xaml"))
                && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                && !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))).ToArray();
    var proof = new List<object>();
    foreach (var target in targets)
    {
        var path = Path.Combine(repo, "backend/SAAIA.Backend", target.Path);
        var before = File.ReadAllText(path);
        var root = CSharpSyntaxTree.ParseText(before).GetRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(node => node.Identifier.ValueText == target.Name);
        if (method.AttributeLists.Count > 0) throw new InvalidOperationException("Attributed method requires separate audit: " + target.Name);
        var occurrences = checkedPaths.SelectMany(source => Regex.Matches(File.ReadAllText(source),
            @"\b" + Regex.Escape(target.Name) + @"\b").Select(match => new { source, match.Index })).ToArray();
        if (occurrences.Length != 1) throw new InvalidOperationException($"Expected declaration-only occurrence: {target.Name}; observed {occurrences.Length}");
        var updated = root.RemoveNode(method, SyntaxRemoveOptions.KeepNoTrivia)!.ToFullString();
        File.WriteAllText(path, updated, new UTF8Encoding(false));
        proof.Add(new { path = Path.GetRelativePath(repo, path).Replace('\\', '/'), method = target.Name,
            productionAndTestOccurrencesBefore = occurrences.Length,
            attributes = 0, checkedSourceFiles = checkedPaths.Length,
            removedLines = method.ToFullString().Split('\n').Length,
            removedBodySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(method.ToFullString()))),
            decision = "No C#/XAML reference or exact method-name string, no attribute; removed explicit audited helper only." });
    }
    File.WriteAllText(Path.Combine(repo, "artifacts/refactor-retrieval-20261024/dead-code-removal-proof.json"),
        JsonSerializer.Serialize(proof, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Removed {proof.Count} explicitly audited unused helpers; no automatic candidate sweep.");
    return;
}
var inputDir = Path.Combine(repo, "backend/SAAIA.Backend/Endpoints");
var outputDir = Path.Combine(repo, "backend/SAAIA.Backend/Retrieval");
Directory.CreateDirectory(outputDir);
var httpNames = new HashSet<string>(StringComparer.Ordinal) {
    "Map", "CategoriesAsync", "QueryAsync", "SearchAsync", "TestRetrievalAsync", "ScrollAsync",
    "BuildRagSearchBusyResult", "ComputeRagSearchRetryAfterSeconds", "AddRagSearchCapacityHeaders", "AddRagSearchAdmissionHeaders" };
var entries = new List<Entry>();
var otherTypes = new List<MemberDeclarationSyntax>();
var paths = Directory.GetFiles(inputDir, "RagEndpoints*.cs").OrderBy(path => path).ToArray();
foreach (var path in paths)
{
    var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path));
    var root = (CompilationUnitSyntax)tree.GetRoot();
    var ns = (FileScopedNamespaceDeclarationSyntax)root.Members.Single();
    foreach (var member in ns.Members)
    {
        if (member is not ClassDeclarationSyntax declaration || declaration.Identifier.ValueText != "RagEndpoints")
        { otherTypes.Add(member); continue; }
        foreach (var item in declaration.Members)
        {
            var names = Names(item);
            var line = tree.GetLineSpan(item.Span).StartLinePosition.Line + 1;
            var owner = Path.GetFileName(path) != "RagEndpoints.cs" ? "CanonicalRetrieval"
                : httpNames.Contains(names[0]) ? "RagEndpoints" : Owner(names[0], line);
            entries.Add(new(item, names, owner, path, line));
        }
    }
}
// All overloads share an owner. They are an API family, not independent chunks.
var owners = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var entry in entries)
    foreach (var name in entry.Names) owners.TryAdd(name, entry.Owner);
for (var i = 0; i < entries.Count; i++) entries[i] = entries[i] with { Owner = owners[entries[i].Names[0]] };
var externallyUsed = new HashSet<string>(StringComparer.Ordinal);
foreach (var entry in entries)
    foreach (var name in entry.Node.DescendantNodes().OfType<IdentifierNameSyntax>().Select(node => node.Identifier.ValueText))
        if (owners.TryGetValue(name, out var target) && target != entry.Owner) externallyUsed.Add(name);

var usings = "using System.Diagnostics;\nusing System.Diagnostics.CodeAnalysis;\nusing System.Globalization;\nusing System.Net;\nusing System.Text;\nusing System.Text.Json;\nusing System.Text.RegularExpressions;\nusing System.Security.Cryptography;\nusing Dapper;\nusing Microsoft.Extensions.DependencyInjection;\nusing Microsoft.Extensions.Options;\nusing Npgsql;\nusing SAAIA.Backend.Auth;\nusing SAAIA.Backend.Middleware;\nusing SAAIA.Backend.Models;\nusing SAAIA.Backend.Retrieval;\n";
foreach (var group in entries.GroupBy(entry => entry.Owner))
{
    var dependencies = group.SelectMany(entry => entry.Node.DescendantNodes().OfType<IdentifierNameSyntax>())
        .Select(name => owners.GetValueOrDefault(name.Identifier.ValueText)).Where(name => name is not null && name != group.Key)
        .Distinct().Order().ToArray();
    var header = usings + string.Concat(dependencies.Select(name => "using static " +
        (name == "RagEndpoints" ? "SAAIA.Backend.Endpoints." : "SAAIA.Backend.Retrieval.") + name + ";\n"));
    var text = new StringBuilder(header).Append("\nnamespace SAAIA.Backend.")
        .Append(group.Key == "RagEndpoints" ? "Endpoints" : "Retrieval").Append(";\n\n")
        .Append(group.Key == "RagEndpoints" ? "public" : "internal").Append(" static partial class ").Append(group.Key).Append("\n{\n");
    foreach (var entry in group)
    {
        var node = entry.Node;
        if (entry.Names.Any(externallyUsed.Contains))
        {
            var modifiers = node switch {
                MethodDeclarationSyntax method => method.Modifiers,
                FieldDeclarationSyntax field => field.Modifiers,
                TypeDeclarationSyntax type => type.Modifiers,
                _ => default };
            var privateToken = modifiers.FirstOrDefault(token => token.IsKind(SyntaxKind.PrivateKeyword));
            if (privateToken != default) node = node.ReplaceToken(privateToken,
                SyntaxFactory.Token(SyntaxKind.InternalKeyword).WithTriviaFrom(privateToken));
        }
        var code = node.ToFullString();
        if (entry.Names[0] is "SearchCoreAsync" or "SearchSourceBackedCanonicalCoreAsync" or "BuildSearchResponseDtoAsync")
        {
            code = code.Replace("HttpContext ctx", "RetrievalExecutionContext context")
                .Replace("ctx.GetTenantId()", "context.TenantId").Replace("ctx.RequestAborted", "context.CancellationToken")
                .Replace("ctx.RequestServices.GetService<TeiWorkloadGovernor>()", "context.TeiGovernor")
                .Replace("ctx.GetRequestId()", "context.RequestId");
            code = Regex.Replace(code, @"\bctx\b", "context");
            if (entry.Names[0] == "SearchCoreAsync") code = code.Replace("SearchCoreAsync(", "SearchInteractiveAsync(")
                .Replace("internal static async Task", "private static async Task");
            if (entry.Names[0] == "BuildSearchResponseDtoAsync") code = code.Replace("private static async Task", "internal static async Task")
                .Replace("SearchCoreAsync(context,", "RetrievalEngine.SearchAsync(context,");
        }
        foreach (var pair in owners) code = code.Replace("RagEndpoints." + pair.Key, pair.Value + "." + pair.Key);
        text.Append(code).Append('\n');
    }
    text.Append("}\n");
    var result = text.ToString();
    if (group.Key == "RagEndpoints") result = result.Replace("public static partial class", "public static partial class");
    File.WriteAllText(group.Key == "RagEndpoints" ? Path.Combine(inputDir, "RagEndpoints.cs")
        : Path.Combine(outputDir, group.Key + ".cs"), result, new UTF8Encoding(false));
}
// Model identity is independent of routes. Preserve names, move their definitions together.
var modelCode = usings + "using SAAIA.Backend.Endpoints;\n\nnamespace SAAIA.Backend.Models;\n\n"
    + string.Join("\n", otherTypes.Select(node => node.ToFullString()));
foreach (var pair in owners) modelCode = modelCode.Replace("RagEndpoints." + pair.Key, pair.Value + "." + pair.Key);
File.WriteAllText(Path.Combine(repo, "backend/SAAIA.Backend/Models/RetrievalModels.cs"), modelCode, new UTF8Encoding(false));
foreach (var path in paths.Where(path => Path.GetFileName(path) != "RagEndpoints.cs")) File.Delete(path);
File.WriteAllText(Path.Combine(repo, "artifacts/refactor-retrieval-20261024/member-owners.json"),
    JsonSerializer.Serialize(owners, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Extracted {entries.Count} members into {entries.Select(entry => entry.Owner).Distinct().Count()} responsibility groups.");

static string[] Names(MemberDeclarationSyntax node) => node switch {
    MethodDeclarationSyntax method => [method.Identifier.ValueText],
    FieldDeclarationSyntax field => field.Declaration.Variables.Select(variable => variable.Identifier.ValueText).ToArray(),
    TypeDeclarationSyntax type => [type.Identifier.ValueText],
    _ => throw new InvalidOperationException(node.Kind().ToString()) };
static string Owner(string name, int line)
{
    if (line < 210) return "RetrievalQueryPolicy";
    if (line < 306) return "RetrievalText";
    if (line < 1863) return "RetrievalResultProjector";
    if (line < 5241) return "RetrievalEngine";
    if (line < 10925) return "RetrievalQueryPolicy";
    if (line < 17600) return "RetrievalChannels";
    if (line < 19803) return "RetrievalCandidateSignals";
    if (line < 20000) return "RetrievalRows";
    if (line < 25000) return "RetrievalSelection";
    if (line < 27000) return "RetrievalTitleSelection";
    if (line < 30500) return "RetrievalCandidateSignals";
    if (line < 34400) return "RetrievalLexicalExpansion";
    if (line < 34933) return "RetrievalNavigation";
    if (line < 35069) return "RetrievalText";
    if (line < 36365) return "RetrievalAnswerGuidance";
    return "RetrievalMetadata";
}
internal sealed record Entry(MemberDeclarationSyntax Node, string[] Names, string Owner, string Path, int Line);
