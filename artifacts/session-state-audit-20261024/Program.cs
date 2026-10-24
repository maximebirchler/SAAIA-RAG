using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.Json;

var repo = Path.GetFullPath(args[0]);
var files = Directory.EnumerateFiles(Path.Combine(repo, "client/SAAIA.Client.WinUI/ToolAgent"), "ToolAgentOrchestrator*.cs").ToArray();
var types = files.Select(file => new { file, root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetCompilationUnitRoot() })
    .Select(item => new { item.file, type = item.root.DescendantNodes().OfType<ClassDeclarationSyntax>().SingleOrDefault(t => t.Identifier.ValueText == "ToolAgentOrchestrator") }).Where(item => item.type is not null).Select(item => new { item.file, type = item.type! }).ToArray();
var methods = types.SelectMany(item => item.type.Members.OfType<MethodDeclarationSyntax>().Select(method => new { item.file, method })).ToArray();
var fields = types.SelectMany(item => item.type.Members.OfType<FieldDeclarationSyntax>().Where(field => !field.Modifiers.Any(SyntaxKind.StaticKeyword) && !field.Modifiers.Any(SyntaxKind.ConstKeyword)).SelectMany(field => field.Declaration.Variables.Select(variable => new { name = variable.Identifier.ValueText, type = field.Declaration.Type.ToString(), readOnlyReference = field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword), file = Path.GetRelativePath(repo, item.file) }))).ToArray();
var fieldNames = fields.Select(field => field.name).ToHashSet();
var properties = types.SelectMany(item => item.type.Members.OfType<PropertyDeclarationSyntax>()).ToArray();
var symbols = methods.Select(item => (name: item.method.Identifier.ValueText, node: (SyntaxNode)item.method)).Concat(properties.Select(property => (name: property.Identifier.ValueText, node: (SyntaxNode)property))).GroupBy(item => item.name).ToDictionary(group => group.Key, group => group.Select(item => item.node).ToArray());
var direct = symbols.Where(pair => pair.Value.Any(node => node.DescendantNodes().OfType<IdentifierNameSyntax>().Any(identifier => fieldNames.Contains(identifier.Identifier.ValueText)))).Select(pair => pair.Key).ToHashSet();
var closure = new HashSet<string>(direct);
bool changed;
do
{
    changed = false;
    foreach (var pair in symbols.Where(pair => !closure.Contains(pair.Key)))
        if (pair.Value.Any(node => node.DescendantNodes().OfType<IdentifierNameSyntax>().Any(identifier => closure.Contains(identifier.Identifier.ValueText)))) changed |= closure.Add(pair.Key);
} while (changed);
var nonstatic = methods.Where(item => !item.method.Modifiers.Any(SyntaxKind.StaticKeyword)).ToArray();
var staticCount = methods.Length - nonstatic.Length;
var result = new
{
    schema = "saaia-orchestrator-state-inventory.v1",
    capturedAtUtc = DateTimeOffset.UtcNow,
    productionParseSymbols = Array.Empty<string>(),
    fileCount = types.Length,
    inactiveConditionalFileCount = files.Length - types.Length,
    physicalLines = types.Sum(item => File.ReadAllLines(item.file).Length),
    methods = methods.Length,
    staticMethods = staticCount,
    staticPercentage = Math.Round(100.0 * staticCount / methods.Length, 2),
    instanceMethods = nonstatic.Length,
    instanceMethodsDirectlyReadingOrWritingFields = nonstatic.Count(item => direct.Contains(item.method.Identifier.ValueText)),
    instanceMethodsTransitivelyDependingOnFields = nonstatic.Count(item => closure.Contains(item.method.Identifier.ValueText)),
    instanceFields = fields,
    files = types.Select(item => new { path = Path.GetRelativePath(repo, item.file), physicalLines = File.ReadAllLines(item.file).Length, methods = item.type.Members.OfType<MethodDeclarationSyntax>().Count(), staticMethods = item.type.Members.OfType<MethodDeclarationSyntax>().Count(method => method.Modifiers.Any(SyntaxKind.StaticKeyword)) }).OrderByDescending(item => item.physicalLines),
    limits = new[] { "Static does not prove pure: these methods can read static caches or call external services/loggers.", "Field dependency closure is conservative syntax-name reachability, not semantic dataflow; overloads share names.", "Production preprocessor symbols only; test-only conditional helpers are not activated.", "Inventory measures concentration; it does not by itself justify extraction or prove an absence of races." }
};
File.WriteAllText(Path.Combine(repo, "artifacts/session-state-audit-20261024/orchestrator-state-inventory.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{result.fileCount} files / {result.physicalLines} lines / {result.methods} methods: {result.staticMethods} static ({result.staticPercentage}%), {result.instanceMethods} instance; {fields.Length} instance fields.");
