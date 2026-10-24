using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

var root = Path.GetFullPath(args[0]);
if (args[1] == "--describe-run")
{
    var source = Path.Combine(root, "client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentV2Runner.cs");
    var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(source));
    var run = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == "RunCoreAsync");
    var loop = run.Body!.Statements.OfType<ForStatementSyntax>().Single();
    foreach (var statement in run.Body.Statements.TakeWhile(statement => statement != loop).Concat(((BlockSyntax)loop.Statement).Statements))
    {
        var line = statement.GetLocation().GetLineSpan();
        Console.WriteLine($"{line.StartLinePosition.Line + 1}-{line.EndLinePosition.Line + 1} {statement.Kind()} "
            + (statement is LocalDeclarationStatementSyntax local ? local.Declaration.ToString().Replace("\r", "").Replace("\n", " ") : statement.ToString().Split('\n')[0]));
    }
    return;
}
if (args[1] == "--inspect-production")
{
    using var stream = File.OpenRead(args[2]);
    using var pe = new PEReader(stream);
    var metadata = pe.GetMetadataReader();
    var types = metadata.TypeDefinitions.Select(handle => metadata.GetTypeDefinition(handle)).ToArray();
    var names = types.Select(type => metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name)).ToArray();
    var methods = metadata.MethodDefinitions.Select(handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name)).ToArray();
    var references = metadata.AssemblyReferences.Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name)).ToArray();
    var source = Path.Combine(root, "client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentV2Runner.cs");
    var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(source)).GetRoot();
    var runnerFiles = Directory.EnumerateFiles(Path.GetDirectoryName(source)!, "SourceBackedAgentV2Runner*.cs")
        .Select(path => new { path, syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot() }).ToArray();
    var allRunnerParts = Directory.EnumerateFiles(Path.GetDirectoryName(source)!, "*.cs")
        .Where(path => File.ReadAllText(path).Contains("partial class SourceBackedAgentV2Runner"))
        .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot()).ToArray();
    var proof = new
    {
        assembly = Path.GetFullPath(args[2]), inspectedAtUtc = DateTimeOffset.UtcNow,
        assemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(args[2]))).ToLowerInvariant(),
        clientDiagnosticsPresent = names.Contains("SAAIA.Client.WinUI.Services.ClientDiagnostics"),
        portableCoreReferenced = references.Contains("SAAIA.Client.Core"),
        conditionalTestHookPresent = methods.Contains("RemoveTrailingModelEmittedSourceListForTests"),
        runnerPhysicalLines = File.ReadLines(source).Count(),
        partialTypeFilesInspected = allRunnerParts.Length,
        reusableRunnerFieldsAreReadonly = allRunnerParts.SelectMany(part => part.DescendantNodes().OfType<FieldDeclarationSyntax>())
            .Where(field => field.Ancestors().OfType<TypeDeclarationSyntax>().First().Identifier.ValueText == "SourceBackedAgentV2Runner")
            .All(field => field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword) || field.Modifiers.Any(SyntaxKind.ConstKeyword)),
        methods = runnerFiles.SelectMany(file => file.syntax.DescendantNodes().OfType<MethodDeclarationSyntax>().Select(method => new
        {
            file = Path.GetRelativePath(root, file.path).Replace('\\', '/'),
            name = method.Identifier.ValueText,
            lines = method.GetLocation().GetLineSpan().EndLinePosition.Line - method.GetLocation().GetLineSpan().StartLinePosition.Line + 1
        })).ToArray(),
        limitation = "Metadata inspection and compilation, no UI or model execution. Other intentionally unconditional legacy ForTests members may still exist."
    };
    File.WriteAllText(args[3], JsonSerializer.Serialize(proof, new JsonSerializerOptions { WriteIndented = true }));
    if (!proof.clientDiagnosticsPresent || !proof.portableCoreReferenced || proof.conditionalTestHookPresent)
        throw new InvalidOperationException("production_probe_or_hook_contract_failed");
    Console.WriteLine(JsonSerializer.Serialize(proof, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
if (args[1] == "--find-empty")
{
    var empty = new List<string>();
    foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "client", "SAAIA.Client.WinUI"), "*.cs", SearchOption.AllDirectories)
        .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")))
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(preprocessorSymbols: ["DEBUG", "TRACE", "SAAIA_TEST_HOOKS"]));
        var types = tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>().ToArray();
        if (types.Length == 1 && types[0].Members.Count == 0 && types[0].AttributeLists.Count == 0
            && types[0].BaseList is null && types[0].Modifiers.Any(SyntaxKind.PartialKeyword)
            && !tree.GetRoot().DescendantTrivia().Any(trivia => trivia.IsDirective))
            empty.Add(Path.GetRelativePath(root, path).Replace('\\', '/'));
    }
    Console.WriteLine(JsonSerializer.Serialize(empty, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
var entries = JsonSerializer.Deserialize<List<Candidate>>(File.ReadAllText(args[1]))!;
var removed = new List<object>();
foreach (var group in entries.GroupBy(x => x.Path))
{
    var path = Path.GetFullPath(Path.Combine(root, group.Key));
    if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("outside_workspace");
    var original = File.ReadAllText(path);
    var tree = CSharpSyntaxTree.ParseText(original, new CSharpParseOptions(preprocessorSymbols: ["DEBUG", "TRACE", "SAAIA_TEST_HOOKS"]));
    var syntax = tree.GetRoot();
    var targets = new List<MethodDeclarationSyntax>();
    foreach (var candidate in group)
    {
        var methods = syntax.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(x => x.Identifier.ValueText == candidate.Name && x.Modifiers.Any(SyntaxKind.PrivateKeyword)).ToArray();
        if (methods.Length != 1) throw new InvalidOperationException("method_not_unique:" + candidate.Name);
        var method = methods[0];
        targets.Add(method);
        removed.Add(new { candidate.Path, candidate.Name, startLine = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            lines = method.GetLocation().GetLineSpan().EndLinePosition.Line - method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            reason = "private_method_one_declaration_no_identifier_or_string_or_xaml_reference_no_production_reflection_dispatch" });
    }
    var updated = syntax.RemoveNodes(targets, SyntaxRemoveOptions.KeepNoTrivia)!.ToFullString();
    File.WriteAllText(path, updated);
}
File.WriteAllText(args[2], JsonSerializer.Serialize(removed, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Removed {removed.Count} proved-unreferenced private methods; no generated member or test hook removed.");

sealed record Candidate(string Path, string Name);
