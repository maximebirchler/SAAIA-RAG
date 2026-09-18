using System.Text.Json.Nodes;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Fact]
    public async Task Binding_guard_accepts_substantive_ocr_procedure_despite_numeric_equipment_list()
    {
        const string body = """
57 fiches cuisine et un livret pour l'animateur
• 1 assiette plate du diamètre du moule à
charlotte
• 1 presse-agrumes
• 1 saladier
• 1 couteau à découper
• 1 planche à découper
• 1 balance
• 1 verre mesureur
Technique
• Met re le jus des oranges, l'eau et son sucre
dans l'assiette creuse. Bien remuer avec la
cuil ère en bois.
• Y tremper les biscuits et au fur et à mesure
les disposer dans le moule, sur le fond et sur
les côtés.
• Mélanger le fromage blanc avec le sucre
dans le saladier.
• Tail er les pêches en cubes sur la planche et
en déposer la moitié dans le fond du moule.
• Y ajouter la moitié du fromage blanc sucré,
puis une couche de gâteaux puis le reste de
fromage blanc.
• Finir par une couche de biscuits.
• Couvrir le moule avec l'assiette plate.
• Placer au frais 4 à 5 heures avant de
démouler.
Truc du chef
• Presque tous les fruits peuvent être utili-sés pour confectionner une charlotte :
fraises, poires, abricots. Les choisir bien
mûrs car il n'y a pas de cuisson.
Suggestions
• Un coulis de fruits peut accompagner la
charlotte.
• On peut utiliser des morceaux de pêches
pour le décor.
© Ceméa 2003
Charlotte
""";
        const string answer = """{"outcome":"answered","answerText":"Charlotte [C1].","claims":[{"claimId":"C1","selectedItem":"Charlotte","text":"La recette Charlotte est documentée avec sa technique et ses suggestions.","evidenceIds":["E-CHARLOTTE"]}]}""";
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[]}"""),
            Completion(answer),
            Completion(answer));
        var options = CreateOptions();
        options.CandidateBindingFeedbackEnabled = true;
        options.SemanticCriticEnabled = true;
        options.ExternalMaximumCallsPerJob = 3;

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, apiKey: null)
            .ExecuteAsync(
                BuildRequest(atomicEvidenceMode: "named_item"),
                new RecordingToolGateway(BuildEvidence("E-CHARLOTTE", body)),
                CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal("Charlotte", Assert.Single(result.Claims).SelectedItem);
        Assert.Equal(3, result.ProviderCallCount);
        Assert.Equal(3, factory.Requests.Count);
    }

    [Theory]
    [InlineData("responses", "lexical")]
    [InlineData("responses", "duplicate")]
    [InlineData("responses", "missing")]
    [InlineData("chat-completions", "lexical")]
    [InlineData("chat-completions", "duplicate")]
    [InlineData("chat-completions", "missing")]
    public async Task Binding_feedback_lets_model_correct_exact_identity_without_substitution_or_more_research(string protocol,string defect)
    {
        var names=new[]{"les modules et les capteurs","Option beta","Option gamma","Option delta"};
        var claims=new JsonArray(Enumerable.Range(0,4).Select(i=>(JsonNode)new JsonObject
        {
            ["claimId"]="C"+(i+1),["selectedItem"]=names[i],["text"]=names[i]+" est documenté.",
            ["evidenceIds"]=new JsonArray("E"+(i+1))
        }).ToArray());
        var supported=new JsonObject{["outcome"]="answered",["answerText"]="Choix A [C1], B [C2], C [C3], D [C4].",["claims"]=claims}.ToJsonString();
        var broken=JsonNode.Parse(supported)!;
        if(defect=="lexical")broken["claims"]![0]!["selectedItem"]="modules et capteurs";
        if(defect=="missing")broken["claims"]![0]!["selectedItem"]="";
        if(defect=="duplicate")
        {
            broken["claims"]![2]!["selectedItem"]=names[1];broken["claims"]![2]!["evidenceIds"]=new JsonArray("E2");
        }
        HttpResponseMessage Reply(string body)=>protocol=="responses"?NativeResponseAnswer(body):Completion(body);
        using var factory=new QueuedHttpClientFactory(Completion("""{"selectionMode":"distinct_named_items","queries":[{"query":"overview","topK":12}]}"""),
            Reply(broken.ToJsonString()),Reply(supported),Reply(supported));
        var options=WorkspaceOptions(protocol);options.CandidateBindingFeedbackEnabled=true;
        options.SemanticCriticEnabled=true;options.ExternalMaximumCallsPerJob=4;
        var evidence=new[]{BuildEvidence("E1","Pour cet ensemble, les modules et les capteurs sont contrôlés à 17 bar.",exactTitle:"Ensemble")}
            .Concat(Enumerable.Range(1,3).Select(i=>BuildEvidence("E"+(i+1),names[i]+"\nMaintain pressure at 18 bar for three minutes.",exactTitle:names[i]))).ToArray();
        var gateway=new RecordingToolGateway(evidence);
        var result=await new OpenAiCompatibleAdvancedAnalysisProvider(factory,options,null)
            .ExecuteAsync(BuildRequest(answerUnitCount:4,atomicEvidenceMode:"named_item"),gateway,CancellationToken.None);
        Assert.Equal("answered",result.Outcome);Assert.Equal(4,result.ProviderCallCount);Assert.Single(gateway.Searches);
        Assert.Equal(names,result.Claims.Select(c=>c.SelectedItem).ToArray());
        var corrections=WorkspaceUser(factory.Requests[2].Body).GetProperty("candidateSupportCorrections").GetProperty("corrections");
        Assert.Contains(corrections.EnumerateArray(),c=>c.GetProperty("reasonCode").GetString()=="candidate_identity_"+(defect=="lexical"?"not_supported":defect));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Binding_feedback_rejects_an_additional_invisible_citation_and_respects_existing_call_budget(bool spareCall)
    {
        var original=BuildEvidence("E1","Le seuil est 17 bar.");
        var newcomers=Enumerable.Range(2,5).Select(i=>BuildEvidence("E"+i,"Le seuil est 17 bar. "+new string('x',2300))).ToArray();
        var additional=NativeAnswered.Replace("[\"E1\"]","[\"E1\",\"E2\"]");
        using var factory=new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),Completion(NativeAnswered),
            NativeCompletion(("search_corpus",WorkspaceSearch)),Completion(additional),Completion(NativeAnswered.Replace("E1","E2")));
        var options=WorkspaceOptions();options.SemanticCriticEnabled=true;options.MaximumEvidencePromptCharacters=8000;
        options.ExternalMaximumCallsPerJob=spareCall?5:4;
        var gateway=new SequencedToolGateway([original],newcomers);
        var provider=new OpenAiCompatibleAdvancedAnalysisProvider(factory,options,null);
        if(spareCall)
        {
            var result=await provider.ExecuteAsync(BuildDirectRequest(),gateway,CancellationToken.None);
            Assert.Equal("E2",Assert.Single(result.Claims).EvidenceIds[0]);Assert.Equal(5,result.ProviderCallCount);
            var corrections=WorkspaceUser(factory.Requests[4].Body).GetProperty("candidateSupportCorrections").GetProperty("corrections");
            Assert.Equal("candidate_evidence_not_visible",Assert.Single(corrections.EnumerateArray()).GetProperty("reasonCode").GetString());
        }
        else
        {
            var error=await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(()=>provider.ExecuteAsync(BuildDirectRequest(),gateway,CancellationToken.None));
            Assert.Equal("advanced_synthesis_candidate_evidence_not_visible",error.ErrorCode);Assert.Equal(4,factory.Requests.Count);
        }
        Assert.Equal(2,gateway.Searches.Count);
    }

    [Theory]
    [InlineData("answered")]
    [InlineData("insufficient_documentation")]
    [InlineData("clarification_required")]
    public void Final_visible_citation_guard_checks_every_reference_for_every_outcome(string outcome)
    {
        var result=new AdvancedAnalysisProviderResult{Outcome=outcome,Claims=[new AdvancedAnalysisResultClaim
        {ClaimId="C1",Text="Observed fact.",EvidenceIds=["current","hidden"]}]};
        var error=Assert.Throws<AdvancedAnalysisProviderException>(()=>OpenAiCompatibleAdvancedAnalysisProvider.EnsureVisibleClaimCitations(result,["current"]));
        Assert.Equal("advanced_final_evidence_not_visible",error.ErrorCode);
        OpenAiCompatibleAdvancedAnalysisProvider.EnsureVisibleClaimCitations(result,["current","hidden"]);
    }
}
