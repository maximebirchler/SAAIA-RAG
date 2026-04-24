using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ProvisioningLocalizationTests
{
    [Fact]
    public void FormatAlreadyAppliedMessage_is_localized()
    {
        Assert.Equal(
            "Provisioning already applied (provisioning.json).",
            Provisioning.FormatAlreadyAppliedMessage("en", "provisioning.json"));

        Assert.Equal(
            "Provisioning deja applique (provisioning.json).",
            Provisioning.FormatAlreadyAppliedMessage("fr", "provisioning.json"));
    }

    [Fact]
    public void FormatInvalidEmptyMessage_is_localized()
    {
        Assert.Equal("Provisioning file invalid (empty).", Provisioning.FormatInvalidEmptyMessage("en"));
        Assert.Equal("Fichier de provisioning invalide (vide).", Provisioning.FormatInvalidEmptyMessage("fr"));
    }

    [Fact]
    public void FormatAppliedMessage_is_localized()
    {
        Assert.Equal(
            @"Provisioning applied from C:\temp\provisioning.json.",
            Provisioning.FormatAppliedMessage("en", @"C:\temp\provisioning.json"));
    }

    [Fact]
    public void FormatApplyFailedMessage_is_localized()
    {
        Assert.Equal(
            "Provisioning apply failed: boom",
            Provisioning.FormatApplyFailedMessage("en", "boom"));

        Assert.Equal(
            "Echec du provisioning : boom",
            Provisioning.FormatApplyFailedMessage("fr", "boom"));
    }
}
