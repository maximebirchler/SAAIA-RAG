using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed partial class SourceBackedAgentV2Tests
{

    [Theory]
    [InlineData(false, 3, false, true)]
    [InlineData(true, 3, false, false)]
    [InlineData(true, 0, false, true)]
    [InlineData(false, 3, true, false)]
    public void SemanticYieldTerminalDecision_DoesNotWaitForCandidatesWithoutAnAuditPath(
        bool candidateAuditEnabled,
        int pendingSemanticCandidateCount,
        bool dedicatedCandidateAuditTurn,
        bool expected)
    {
        var actual = SourceBackedAgentV2Runner
            .ShouldRequestSemanticYieldTerminalDecisionForTests(
                flatEvidenceGapTerminalDecisionNextTurn: false,
                semanticYieldResolutionPending: true,
                semanticYieldResolutionContinuationCount: 0,
                semanticYieldResolutionContinuationLimit: 0,
                candidateAuditEnabled,
                pendingSemanticCandidateCount,
                dedicatedCandidateAuditTurn);

        Assert.Equal(expected, actual);
    }
}
