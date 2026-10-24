using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var repository = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var files = JsonSerializer.Deserialize<List<SourceFile>>(File.ReadAllText(Path.Combine(output,
    "tracked-source-metrics.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
var types = new List<TypeMetric>();
var methods = new List<MethodMetric>();
var references = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
var stringReferences = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
var xamlReferences = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
var parseErrors = new List<object>();
var parseOptions = new CSharpParseOptions(LanguageVersion.Preview,
    preprocessorSymbols: ["DEBUG", "TRACE", "SAAIA_TEST_HOOKS"]);
foreach (var file in files.Where(file => file.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)))
    foreach (System.Text.RegularExpressions.Match token in System.Text.RegularExpressions.Regex.Matches(
        File.ReadAllText(Path.Combine(repository, file.Path)), @"\b[A-Za-z_][A-Za-z_0-9]*\b"))
        AddReference(xamlReferences, token.Value, file.Path);
foreach (var file in files.Where(file => file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
{
    var text = File.ReadAllText(Path.Combine(repository, file.Path));
    var tree = CSharpSyntaxTree.ParseText(text, parseOptions);
    var root = tree.GetRoot();
    foreach (var diagnostic in tree.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error))
        parseErrors.Add(new { file.Path, diagnostic.Id, message = diagnostic.GetMessage(),
            line = diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1 });
    foreach (var name in root.DescendantNodes().OfType<IdentifierNameSyntax>())
        AddReference(references, name.Identifier.ValueText, file.Path);
    foreach (var literal in root.DescendantTokens().Where(token => token.IsKind(SyntaxKind.StringLiteralToken)))
        AddReference(stringReferences, literal.ValueText, file.Path);
    foreach (var declaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
    {
        var location = tree.GetLineSpan(declaration.Span);
        var name = QualifiedName(declaration);
        var partial = declaration.Modifiers.Any(SyntaxKind.PartialKeyword);
        types.Add(new(name, file.Path, location.StartLinePosition.Line + 1,
            location.EndLinePosition.Line - location.StartLinePosition.Line + 1, partial, file.Test));
    }
    foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
    {
        var location = tree.GetLineSpan(method.Span);
        var enclosing = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        var decisionNodes = method.DescendantNodes().Count(node => node is IfStatementSyntax
            or ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax
            or DoStatementSyntax or CatchClauseSyntax or SwitchSectionSyntax or ConditionalExpressionSyntax
            || node.IsKind(SyntaxKind.LogicalAndExpression) || node.IsKind(SyntaxKind.LogicalOrExpression));
        methods.Add(new(enclosing is null ? "<global>" : QualifiedName(enclosing),
            method.Identifier.ValueText, file.Path, location.StartLinePosition.Line + 1,
            location.EndLinePosition.Line - location.StartLinePosition.Line + 1,
            decisionNodes, string.Join(" ", method.Modifiers.Select(token => token.Text)),
            method.ParameterList.Parameters.Count, file.Test));
    }
}
var aggregateTypes = types.GroupBy(type => (type.Name, type.Test)).Select(group => new
{
    name = group.Key.Name, test = group.Key.Test, parts = group.Count(),
    files = group.Select(type => type.Path).Distinct().Count(), lines = group.Sum(type => type.Lines),
    methods = methods.Count(method => method.Type == group.Key.Name && method.Test == group.Key.Test),
    locations = group.Select(type => new { path = type.Path, line = type.Line }).ToArray()
}).OrderByDescending(type => type.lines).ToArray();
var unreferenced = methods.Where(method => !method.Test && method.Modifiers.Split(' ').Contains("private")
    && !method.Modifiers.Split(' ').Contains("partial") && !method.Modifiers.Split(' ').Contains("extern"))
    .Select(method => new
    {
        method.Type, method.Name, method.Path, method.Line,
        identifierReferenceFiles = references.GetValueOrDefault(method.Name)?.Order().ToArray() ?? [],
        exactStringReferenceFiles = stringReferences.GetValueOrDefault(method.Name)?.Order().ToArray() ?? [],
        xamlReferenceFiles = xamlReferences.GetValueOrDefault(method.Name)?.Order().ToArray() ?? []
    }).Where(method => method.identifierReferenceFiles.Length == 0).ToArray();
WriteJson("type-metrics.json", aggregateTypes);
WriteJson("method-metrics.json", methods.OrderByDescending(method => method.Lines));
WriteJson("unreferenced-private-candidates.json", unreferenced);
WriteJson("parse-errors.json", parseErrors);
WriteJson("methodology.json", new
{
    files = files.Count, sourceCs = files.Count(file => file.Path.EndsWith(".cs")),
    parser = typeof(CSharpSyntaxTree).Assembly.GetName().Version?.ToString(),
    symbols = new[] { "DEBUG", "TRACE", "SAAIA_TEST_HOOKS" },
    syntaxErrorCount = parseErrors.Count,
    warning = "Syntax inventory only. Physical LOC is not a quality score. Identifier/string reference absence is a candidate, not a reachability proof; compiler profiles, delegates, reflection and generated code require manual review. Nested type lines overlap with enclosing type lines; aggregate only same named type.",
    decisionNodes = "Count of branching/loop/catch/switch/conditional/short-circuit syntax nodes; not certified cyclomatic complexity."
});
Console.WriteLine($"Parsed {files.Count(file => file.Path.EndsWith(".cs"))} C# files; {types.Count} type parts; {methods.Count} methods; {unreferenced.Length} private candidates for manual review.");

void WriteJson(string name, object value) => File.WriteAllText(Path.Combine(output, name),
    JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
static void AddReference(Dictionary<string, HashSet<string>> map, string name, string path)
{
    if (!map.TryGetValue(name, out var paths)) map.Add(name, paths = new(StringComparer.Ordinal));
    paths.Add(path);
}
static string QualifiedName(TypeDeclarationSyntax declaration)
{
    var ns = declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse()
        .Select(item => item.Name.ToString());
    var containers = declaration.Ancestors().OfType<TypeDeclarationSyntax>().Reverse()
        .Select(item => item.Identifier.ValueText);
    return string.Join(".", ns.Concat(containers).Append(declaration.Identifier.ValueText));
}
internal sealed record SourceFile(string Path, int Lines, string Area, bool Test);
internal sealed record TypeMetric(string Name, string Path, int Line, int Lines, bool Partial, bool Test);
internal sealed record MethodMetric(string Type, string Name, string Path, int Line, int Lines,
    int DecisionNodes, string Modifiers, int Parameters, bool Test);
