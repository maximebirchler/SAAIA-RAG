using System.Reflection;
using SAAIA.Client.Core.Conversation;
using SAAIA.Client.WinUI;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SettingsAdmissionContractTests
{
    [Fact]
    public void The_compiled_composer_uses_the_action_state_and_an_explicit_cancelling_label()
    {
        Assert.Contains(CompiledArchitectureContract.CallsForMethod(typeof(MainWindow), "UpdateSendCancelButtonVisualState"),
            call => call.DeclaringType == typeof(ComposerActionState) && call.Name == nameof(ComposerActionState.Resolve));
        Assert.Contains("button.cancelling", CompiledArchitectureContract.StringsForMethod(typeof(MainWindow), "UpdateSendCancelButtonVisualState"));
    }

    [Fact]
    public void The_compiled_settings_handler_owns_admission_before_loading_or_opening_and_through_application()
    {
        var calls = CompiledArchitectureContract.CallsForMethod(typeof(MainWindow), "UserSettings_Click").ToArray();
        var admission = IndexOf(calls, typeof(ConversationActivityGate), nameof(ConversationActivityGate.TryBeginSessionChange));
        var settingsLoad = IndexOf(calls, typeof(SAAIA.Client.WinUI.Services.AppSettings), "Load");
        var dialog = Array.FindIndex(calls, call => call.DeclaringType?.Name == "UserSettingsDialog" && call.IsConstructor);
        var completion = Array.FindIndex(calls, call => call.Name == "get_Completion");
        var application = IndexOf(calls, typeof(SAAIA.Client.WinUI.Services.RagChatAgent), "ApplySettings");
        var release = IndexOf(calls, typeof(ConversationActivityGate.ActivityLease), nameof(IDisposable.Dispose));
        var finalUiRefresh = Array.FindLastIndex(calls, call => call.DeclaringType == typeof(MainWindow) && call.Name == "UpdateUiState");

        Assert.True(admission >= 0 && admission < settingsLoad && admission < dialog && dialog < completion);
        Assert.True(completion < application && application < release && release < finalUiRefresh);
    }

    [Fact]
    public void The_compiled_configuration_wizard_owns_admission_through_application_but_releases_before_connecting()
    {
        var calls = CompiledArchitectureContract.CallsForMethod(typeof(MainWindow), "ShowSetupWizardAsync").ToArray();
        var admission = IndexOf(calls, typeof(ConversationActivityGate), nameof(ConversationActivityGate.TryBeginSessionChange));
        var identity = IndexOf(calls, typeof(SAAIA.Client.WinUI.Services.SecureLocalStore), "GetOrCreateUserId");
        var dialog = Array.FindIndex(calls, call => call.DeclaringType?.Name == "SetupWizardDialog" && call.IsConstructor);
        var completion = Array.FindIndex(calls, call => call.Name == "get_Completion");
        var application = IndexOf(calls, typeof(MainWindow), "LoadSettings");
        var release = IndexOf(calls, typeof(ConversationActivityGate.ActivityLease), nameof(IDisposable.Dispose));
        var connect = IndexOf(calls, typeof(MainWindow), "ConnectAsync");

        Assert.True(admission >= 0 && admission < identity && identity < dialog && dialog < completion);
        Assert.True(completion < application && application < release && release < connect);
    }

    [Fact]
    public void The_configuration_button_handler_also_checks_the_current_conversation_state()
    {
        var calls = CompiledArchitectureContract.CallsForMethod(typeof(MainWindow), "SetupWizard_Click");
        Assert.Contains(calls, call => call.DeclaringType == typeof(MainWindow) && call.Name == "get__isLoadingSession");
        Assert.Contains(calls, call => call.DeclaringType == typeof(MainWindow) && call.Name == "ShowSetupWizardAsync");
    }

    [Fact]
    public void Active_or_cancelling_turns_cannot_admit_settings_that_would_mutate_their_provider()
    {
        var gate = new ConversationActivityGate();
        using var turn = gate.TryBeginTurn("session-a");
        Assert.NotNull(turn);
        Assert.Null(gate.TryBeginSessionChange());
        gate.CancelActiveTurn();
        Assert.Null(gate.TryBeginSessionChange());
        turn.Dispose();
        using var settings = gate.TryBeginSessionChange();
        Assert.NotNull(settings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Settings_admission_blocks_keyboard_turns_until_dialog_and_application_finish_even_on_error(bool failApplication)
    {
        var gate = new ConversationActivityGate();
        var dialogClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applicationFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task OpenAndApplyAsync()
        {
            using var settings = gate.TryBeginSessionChange();
            Assert.NotNull(settings);
            await dialogClosed.Task;
            applying.SetResult();
            await applicationFinished.Task;
            if (failApplication)
                throw new IOException("synthetic settings application failure");
        }

        var pending = OpenAndApplyAsync();
        Assert.True(gate.IsChangingSession);
        Assert.Null(gate.TryBeginTurn("session-a"));
        Assert.Null(gate.TryBeginSessionChange("session-b"));
        dialogClosed.SetResult();
        await applying.Task;
        Assert.Null(gate.TryBeginTurn("session-a"));
        applicationFinished.SetResult();
        if (failApplication)
            await Assert.ThrowsAsync<IOException>(() => pending);
        else
            await pending;

        Assert.False(gate.IsChangingSession);
        using var next = gate.TryBeginTurn("session-a");
        Assert.NotNull(next);
    }

    private static int IndexOf(MethodBase[] calls, Type type, string method)
        => Array.FindIndex(calls, call => call.DeclaringType == type && call.Name == method);
}
