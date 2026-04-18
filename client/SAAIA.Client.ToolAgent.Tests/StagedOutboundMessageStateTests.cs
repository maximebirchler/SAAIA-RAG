using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class StagedOutboundMessageStateTests
{
    [Fact]
    public void ResolveForSend_uses_staged_prompt_when_visible_text_still_matches()
    {
        var resolution = StagedOutboundMessageState.ResolveForSend(
            pendingWireText: "Liste les catégories présentes sur le serveur.",
            pendingDisplayText: "Liste les catégories présentes sur le serveur.",
            currentVisibleText: "Liste les catégories présentes sur le serveur.");

        Assert.True(resolution.UsePending);
        Assert.Equal("Liste les catégories présentes sur le serveur.", resolution.EffectiveWireText);
        Assert.Equal("Liste les catégories présentes sur le serveur.", resolution.EffectiveDisplayText);
    }

    [Fact]
    public void ResolveForSend_drops_stale_staged_prompt_when_user_has_typed_a_real_question()
    {
        var resolution = StagedOutboundMessageState.ResolveForSend(
            pendingWireText: "Liste les catégories présentes sur le serveur.",
            pendingDisplayText: "Liste les catégories présentes sur le serveur.",
            currentVisibleText: "Où trouve-t-on EN 15281 ?");

        Assert.False(resolution.UsePending);
        Assert.Equal("Où trouve-t-on EN 15281 ?", resolution.EffectiveWireText);
        Assert.Equal("Où trouve-t-on EN 15281 ?", resolution.EffectiveDisplayText);
    }

    [Fact]
    public void ShouldRetainPendingAfterTextChange_returns_false_for_partial_edit_of_staged_prompt()
    {
        var shouldRetain = StagedOutboundMessageState.ShouldRetainPendingAfterTextChange(
            pendingWireText: "Catégories présentes sur le serveur",
            pendingDisplayText: "Catégories présentes sur le serveur",
            currentVisibleText: "Catégories présentes sur le serveur et aussi EN 15281");

        Assert.False(shouldRetain);
    }
}
