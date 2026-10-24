using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class SplitTests
{
    public static void Run(string root, string relative)
    {
        var path=Path.Combine(root,relative);
        var source=File.ReadAllText(path);
        var tree=CSharpSyntaxTree.ParseText(source);
        var type=tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().First();
        var header=source[..type.SpanStart];
        var typeName=type.Identifier.Text;
        var groups = new Dictionary<string,List<string>>();
        foreach(var member in type.Members)
        {
            string family="Fixtures";
            if(member is MethodDeclarationSyntax method && method.AttributeLists.SelectMany(a=>a.Attributes).Any(a=>a.Name.ToString() is "Fact" or "Theory" or "ProfileFact" or "ProfileTheory"))
            {
                var name=method.Identifier.Text;
                family=name.Contains('_') ? name[..name.IndexOf('_')] : name;
                // Families describe the contract under test; shared scripted fixtures stay in one place.
                family=family switch {
                  "ResolveSearchMode" or "ResolveRetriever" or "NormalizeRetriever" or "SelectRetriever" or "BuildRetrievalOptions" or "ReadRuntimeRetrievalOptions" => "RuntimeSelection",
                  "BuildAnswerGuidance" or "ExtractMatchedDocHints" => "AnswerGuidance",
                  "OrderMatchesForSelection" or "PrioritizeFinalSelections" or "CalibrateFusedMatches" or "PrioritizeQuotedTitleSelections" or "PreferRequestedPageWindowMatches" or "PrioritizeOperationalSettingSelections" => "Ranking",
                  "RouterGridInitialObservation" or "RouterGridInitialMission" or "RouterGridMission" or "StructuredPlanner" => "Planning",
                  "NativeLoop" or "ResearchTransition" or "DocumentLocatorContract" => "Navigation",
                  "AdaptiveFastReview" or "SemanticJudge" or "TerminalAnswer" or "SplitActionWriter" => "ReviewAndAnswer",
                  _ => family
                };
                if(typeName=="RetrievalRuntimeSwitchTests" && family is not "Ranking" and not "RuntimeSelection" and not "AnswerGuidance")
                {
                    family = name.Contains("Page",StringComparison.Ordinal) || name.Contains("Window",StringComparison.Ordinal) ? "PageWindows"
                        : name.Contains("Anchor",StringComparison.Ordinal) || name.Contains("Title",StringComparison.Ordinal) || name.Contains("Route",StringComparison.Ordinal) ? "SourceRouting"
                        : name.Contains("Exact",StringComparison.Ordinal) || name.Contains("Lookup",StringComparison.Ordinal) ? "ExactLookup"
                        : name.Contains("Query",StringComparison.Ordinal) || name.Contains("Tokens",StringComparison.Ordinal) || name.Contains("Token",StringComparison.Ordinal) ? "QueryPolicy"
                        : name.Contains("Profile",StringComparison.Ordinal) || name.Contains("Summary",StringComparison.Ordinal) ? "DocumentProfiles"
                        : name.Contains("Budget",StringComparison.Ordinal) || name.Contains("Runtime",StringComparison.Ordinal) || name.Contains("Retry",StringComparison.Ordinal) ? "RuntimePolicy"
                        : "EvidenceSelection";
                }
                if(typeName=="DocumentFoundationIntegrationTests")
                {
                    family = name.Contains("Canonical",StringComparison.Ordinal) || name.Contains("canonical",StringComparison.Ordinal) ? "CanonicalSearchContracts"
                        : name.Contains("Migration",StringComparison.Ordinal) || name.Contains("DbMigrator",StringComparison.Ordinal) ? "Migrations"
                        : name.Contains("Docling",StringComparison.Ordinal) || name.Contains("Ocr",StringComparison.Ordinal) || name.Contains("Ingestion",StringComparison.Ordinal) ? "IngestionContracts"
                        : name.Contains("Profile",StringComparison.Ordinal) || name.Contains("Summary",StringComparison.Ordinal) ? "ProfilesAndSummaries"
                        : name.Contains("Context",StringComparison.Ordinal) || name.Contains("Anchor",StringComparison.Ordinal) || name.Contains("Chunk",StringComparison.Ordinal) || name.Contains("Units",StringComparison.Ordinal) ? "SourceContextContracts"
                        : "InteractiveSearchContracts";
                }
            }
            if(!groups.TryGetValue(family,out var list)) groups[family]=list=new();
            list.Add(member.ToFullString());
        }
        foreach(var (family,members) in groups)
        {
            int part=1, lines=0;
            var batch=new List<string>();
            void Save()
            {
                if(batch.Count==0)return;
                var suffix=part==1?family:$"{family}{part}";
                var target=Path.Combine(Path.GetDirectoryName(path)!,typeName+"."+suffix+".cs");
                if(File.Exists(target))throw new Exception("Existing split target: "+target);
                File.WriteAllText(target,header+$"public sealed partial class {typeName}\n{{\n"+string.Join("",batch)+"}\n");
                part++;batch.Clear();lines=0;
            }
            foreach(var member in members) { var count=member.Count(c=>c=='\n'); if(lines+count>4500)Save(); batch.Add(member);lines+=count; }
            Save();
        }
        File.Delete(path);
        Console.WriteLine($"Split {relative}: {groups.Count} contract families, fixtures preserved once.");
    }
}
