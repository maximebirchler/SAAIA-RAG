using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Text.Json;

var root = args[0];
if(args.Length>1 && args[1]=="verify-splits")
{
    var evidence=new List<object>();
    foreach(var (folder,typeName) in new[]{("backend/SAAIA.Backend.Tests","RetrievalRuntimeSwitchTests"),("backend/SAAIA.Backend.Tests","DocumentFoundationIntegrationTests"),("client/SAAIA.Client.ToolAgent.Tests","SourceBackedAgentV2Tests")})
    {
        string Git(params string[] command) { var p=new System.Diagnostics.ProcessStartInfo("git"){WorkingDirectory=root,RedirectStandardOutput=true,UseShellExecute=false};foreach(var arg in command)p.ArgumentList.Add(arg);using var process=System.Diagnostics.Process.Start(p)!;var output=process.StandardOutput.ReadToEnd();process.WaitForExit();if(process.ExitCode!=0)throw new Exception("git failed");return output; }
        IEnumerable<string> Tests(string source)=>CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Where(t=>t.Identifier.Text==typeName).SelectMany(t=>t.Members.OfType<MethodDeclarationSyntax>()).Where(m=>m.AttributeLists.SelectMany(a=>a.Attributes).Any(a=>a.Name.ToString() is "Fact" or "Theory" or "ProfileFact" or "ProfileTheory")).Select(m=>m.Identifier.Text+"("+string.Join(",",m.ParameterList.Parameters.Select(p=>p.Type?.ToString()))+")");
        var oldPaths=Git("ls-tree","-r","--name-only","HEAD","--",folder).Split('\n',StringSplitOptions.RemoveEmptyEntries).Where(p=>p.EndsWith(".cs"));
        var before=oldPaths.SelectMany(path=>Tests(Git("show","HEAD:"+path.Trim()))).Order().ToArray();
        var after=Directory.EnumerateFiles(Path.Combine(root,folder),"*.cs").SelectMany(path=>Tests(File.ReadAllText(path))).Order().ToArray();
        var removed=before.Except(after).ToArray();var added=after.Except(before).ToArray();
        evidence.Add(new{type=typeName,beforeMethodCount=before.Length,afterMethodCount=after.Length,removed,added,unchangedMethodSet=before.SequenceEqual(after)});
        Console.WriteLine($"{typeName}: {before.Length} -> {after.Length}, removed={removed.Length}, added={added.Length}");
    }
    File.WriteAllText(Path.Combine(root,"artifacts/validation-refactor/test-split-preservation.json"),JsonSerializer.Serialize(evidence,new JsonSerializerOptions{WriteIndented=true}));
    return;
}
if(args.Length>1 && args[1]=="split")
{
    SplitTests.Run(root,"backend/SAAIA.Backend.Tests/RetrievalRuntimeSwitchTests.cs");
    if(File.Exists(Path.Combine(root,"client/SAAIA.Client.ToolAgent.Tests/SourceBackedAgentV2Tests.cs")))
        SplitTests.Run(root,"client/SAAIA.Client.ToolAgent.Tests/SourceBackedAgentV2Tests.cs");
    return;
}
if(args.Length>1 && args[1]=="split-documents")
{
    SplitTests.Run(root,"backend/SAAIA.Backend.Tests/DocumentFoundationIntegrationTests.cs");
    return;
}
var changes = new List<object>();
foreach (var folder in new[]{"backend/SAAIA.Backend.Tests", "client/SAAIA.Client.ToolAgent.Tests"})
foreach (var path in Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs"))
{
    var source = File.ReadAllText(path);
    var tree = CSharpSyntaxTree.ParseText(source);
    var methods = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().ToArray();
    var liveFile = Path.GetFileName(path).StartsWith("Live") && !path.EndsWith("LiveAdvancedValidationOwnershipTests.cs");
    var gateMethods = methods.Where(m=>m.Body != null && m.Body.Statements.OfType<IfStatementSyntax>().Any(IsGate)).Select(m=>m.Identifier.Text).ToHashSet();
    bool progressed;
    do { progressed=false; foreach(var method in methods) if(!gateMethods.Contains(method.Identifier.Text) && method.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i=>gateMethods.Contains(i.Expression.ToString()))) progressed |= gateMethods.Add(method.Identifier.Text); } while(progressed);
    var edits = new List<TextChange>();
    foreach(var method in methods)
    {
        var attr = method.AttributeLists.SelectMany(l=>l.Attributes).FirstOrDefault(a=>a.Name.ToString() is "Fact" or "Theory");
        var hasPg = method.DescendantNodes().OfType<IfStatementSyntax>().Any(i=>IsNullDbGuard(i));
        var live = liveFile && gateMethods.Contains(method.Identifier.Text);
        if (attr != null && (hasPg || live))
        {
            var profile = hasPg ? "IntegrationPg" : IsCertainlyLocal(path) ? "LiveLocal" : "LivePaid";
            if(attr.ArgumentList == null)
                edits.Add(new TextChange(attr.Span,$"Profile{attr.Name}(\"{profile}\")"));
            edits.Add(new TextChange(new TextSpan(method.AttributeLists.First().SpanStart,0),$"[Trait(\"Profile\", \"{profile}\")]\n    "));
            changes.Add(new{file=Path.GetRelativePath(root,path).Replace('\\','/'), method=method.Identifier.Text, profile});
        }
        else if(attr != null && liveFile)
        {
            edits.Add(new TextChange(new TextSpan(method.AttributeLists.First().SpanStart,0),"[Trait(\"Profile\", \"Unit\")]\n    "));
            changes.Add(new{file=Path.GetRelativePath(root,path).Replace('\\','/'), method=method.Identifier.Text, profile="Unit"});
        }
        foreach(var conditional in method.DescendantNodes().OfType<IfStatementSyntax>())
        {
            if(IsNullDbGuard(conditional))
            {
                foreach(var ret in conditional.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Where(r=>r.Expression==null))
                    edits.Add(new TextChange(ret.Span,"throw new InvalidOperationException(\"IntegrationPg selected but isolated PostgreSQL fixture was not created.\");"));
            }
            else if(liveFile && IsGate(conditional))
            {
                foreach(var ret in conditional.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Where(r=>r.Expression==null))
                    edits.Add(new TextChange(ret.Span,"throw new InvalidOperationException(\"Selected live test requires its explicit enable flag and configuration. See validation manifest.\");"));
            }
        }
    }
    if(edits.Count>0) File.WriteAllText(path, SourceText.From(source).WithChanges(edits).ToString());
}
File.WriteAllText(Path.Combine(root,"artifacts/validation-refactor/profile-inventory.json"),JsonSerializer.Serialize(changes,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"Classified {changes.Count} test methods.");

static bool IsNullDbGuard(IfStatementSyntax i) => i.Condition.ToString() is "db is null" or "database is null" && i.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any(r=>r.Expression==null);
static bool IsGate(IfStatementSyntax i)
{
    if (!i.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any(r=>r.Expression==null)) return false;
    var condition=i.Condition.ToString();
    return condition.Contains("Environment.GetEnvironmentVariable") || i.Statement.ToString().Contains("Skipped:") || condition.Contains("IsEnabled(");
}
static bool IsCertainlyLocal(string path) => new[]{"LiveHardware", "LiveLocal", "LiveRuntimeProvisioning", "LiveCanonicalVertical", "LiveRagRetrievalInventory", "LiveDocumentStructuralOverview", "LiveDocumentPageStratifiedOverview", "LiveDocumentNavigation", "LiveContentCardInventory", "LiveAdvancedRetrievalPacket"}.Any(p=>Path.GetFileName(path).StartsWith(p));
