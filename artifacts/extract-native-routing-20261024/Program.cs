using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.Json;

var repo = Path.GetFullPath(args[0]);
var artifact = Path.Combine(repo, "artifacts/extract-native-routing-20261024");
var agentRoot = Path.Combine(repo, "client/SAAIA.Client.WinUI/ToolAgent");
var mapping = JsonDocument.Parse(File.ReadAllText(Path.Combine(artifact, "member-mapping.json")));
var moves = mapping.RootElement.GetProperty("members").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!, p => p.GetProperty("owner").GetString()!);
var originalMembers = Directory.EnumerateFiles(artifact, "*.before.txt").SelectMany(path => Parse(path).DescendantNodes().OfType<ClassDeclarationSyntax>().Single(t => t.Identifier.ValueText == "ToolAgentOrchestrator").Members.Select(m => new { name = Name(m), member = m, path })).ToArray();
var actualMembers = Directory.EnumerateFiles(Path.Combine(agentRoot, "NativeRouting"), "*.cs").Concat(Directory.EnumerateFiles(Path.Combine(repo, "client/SAAIA.Client.Core/NativeRouting"), "*.cs")).SelectMany(path => Parse(path).DescendantNodes().OfType<ClassDeclarationSyntax>().Single().Members.Select(m => new { name = Name(m), member = m, owner = Path.GetFileNameWithoutExtension(path) })).ToDictionary(p => p.name);
var results = new List<object>();
var missing = new List<string>();
foreach (var original in originalMembers)
{
    if (moves.TryGetValue(original.name, out var owner))
    {
        if (!actualMembers.TryGetValue(original.name, out var actual))
        {
            if (original.name.EndsWith("ForTests", StringComparison.Ordinal))
            {
                results.Add(new { name = original.name, owner, state = "test_adapter_removed", reason = "Tests call real owners or adapt arguments within test code" });
                continue;
            }
            missing.Add(original.name); continue;
        }
        var expected = new LanguageNormalizer().Visit(original.member)!;
        var sameBody = Tokens(Body(expected)) == Tokens(Body(actual.member));
        if (owner != actual.owner || !sameBody) missing.Add(original.name + ":changed_body");
        results.Add(new { name = original.name, owner, state = "moved", bodyTokensEquivalent = sameBody, allLiteralTokensPreserved = Literals(original.member).SequenceEqual(Literals(actual.member)) });
    }
    else
    {
        var path = Path.Combine(agentRoot, Path.GetFileName(original.path).Replace(".before.txt", string.Empty));
        var actual = Parse(path).DescendantNodes().OfType<ClassDeclarationSyntax>().Single(t => t.Identifier.ValueText == "ToolAgentOrchestrator").Members.Single(m => Name(m) == original.name);
        var unchanged = Tokens(original.member) == Tokens(actual);
        if (!unchanged) missing.Add(original.name + ":changed_remaining_member");
        results.Add(new { name = original.name, owner = "ToolAgentOrchestrator", state = "retained_unchanged", allTokensEquivalent = unchanged });
    }
}
File.WriteAllText(Path.Combine(artifact, "semantic-preservation.json"), JsonSerializer.Serialize(new { schema = "saaia-native-routing-preservation.v1", originalMemberCount = originalMembers.Length, movedMemberCount = moves.Count, covered = results.Count, missing, results, limits = "AST token equality for all retained members and moved production bodies; the only normalized call is NormalizeLanguageCode -> LocalizedStrings.NormalizeLanguage, whose original wrapper delegates directly. Test adapters may be removed. Runtime tests remain independently required." }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Covered {results.Count}/{originalMembers.Length} original members, {moves.Count} moved; preservation failures={missing.Count}");
if (missing.Count > 0) { Console.WriteLine(string.Join(Environment.NewLine, missing)); Environment.ExitCode = 1; }

static CompilationUnitSyntax Parse(string file) => CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetCompilationUnitRoot();
static string Name(MemberDeclarationSyntax member) => member switch { MethodDeclarationSyntax m => m.Identifier.ValueText, FieldDeclarationSyntax f => f.Declaration.Variables.Single().Identifier.ValueText, _ => throw new Exception("Unexpected member") };
static SyntaxNode Body(SyntaxNode node) => node switch { MethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody!, FieldDeclarationSyntax f => f.Declaration, _ => node };
static string Tokens(SyntaxNode node) => string.Join("|", node.DescendantTokens().Select(t => t.RawKind + ":" + t.Text));
static IEnumerable<string> Literals(SyntaxNode node) => node.DescendantTokens().Where(t => t.IsKind(SyntaxKind.StringLiteralToken) || t.IsKind(SyntaxKind.MultiLineRawStringLiteralToken) || t.IsKind(SyntaxKind.InterpolatedStringTextToken)).Select(t => t.Text);
sealed class LanguageNormalizer : CSharpSyntaxRewriter
{
    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (node.Expression is IdentifierNameSyntax name && name.Identifier.ValueText == "NormalizeLanguageCode")
            node = node.WithExpression(SyntaxFactory.ParseExpression("LocalizedStrings.NormalizeLanguage").WithTriviaFrom(node.Expression));
        return base.VisitInvocationExpression(node);
    }
}
