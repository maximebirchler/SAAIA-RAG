using SAAIA.Client.WinUI;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ComposerPersistenceNoticeTests
{
    [Fact]
    public void Notice_persists_for_the_same_draft_until_confirmed()
    {
        var notice = new ComposerPersistenceNotice();
        notice.ReportUnconfirmed("session-a", "session-a", "same draft");
        notice.ObserveDraft("session-a", " same draft ");
        Assert.True(notice.IsVisibleFor("session-a"));
        notice.Confirmed("other-session");
        Assert.True(notice.IsVisibleFor("session-a"));
        notice.Confirmed("session-a");
        Assert.False(notice.IsVisibleFor("session-a"));
    }

    [Fact]
    public void Meaningful_draft_edit_clears_the_notice()
    {
        var notice = new ComposerPersistenceNotice();
        notice.ReportUnconfirmed("session-a", "session-a", "old draft");
        notice.ObserveDraft("session-a", "new question");
        Assert.False(notice.IsVisibleFor("session-a"));
    }

    [Fact]
    public void Session_change_clears_the_notice_and_returning_cannot_revive_it()
    {
        var notice = new ComposerPersistenceNotice();
        notice.ReportUnconfirmed("session-a", "session-a", "draft");
        Assert.False(notice.IsVisibleFor("session-b"));
        notice.ObserveSession("session-b");
        notice.ObserveSession("session-a");
        Assert.False(notice.IsVisibleFor("session-a"));
    }

    [Fact]
    public void Late_failure_from_another_session_cannot_create_or_replace_current_notice()
    {
        var notice = new ComposerPersistenceNotice();
        notice.ReportUnconfirmed("old-session", "session-a", "draft");
        Assert.False(notice.IsVisibleFor("session-a"));
        notice.ReportUnconfirmed("session-a", "session-a", "draft");
        notice.ReportUnconfirmed("old-session", "session-a", "other draft");
        notice.ObserveDraft("session-a", "draft");
        Assert.True(notice.IsVisibleFor("session-a"));
    }

    [Fact]
    public void Compiled_handlers_connect_failure_edit_and_session_changes_to_notice_lifecycle()
    {
        Assert.Contains(CompiledArchitectureContract.CallsForMethod(typeof(MainWindow), "SendAsync"),
            call => call.DeclaringType == typeof(ComposerPersistenceNotice) && call.Name == "ReportUnconfirmed");
        Assert.Contains(CompiledArchitectureContract.CallsForMethod(typeof(MainWindow), "InputBox_TextChanged"),
            call => call.DeclaringType == typeof(ComposerPersistenceNotice) && call.Name == "ObserveDraft");
        Assert.Contains(CompiledArchitectureContract.CallsForMethod(typeof(MainWindow), "LoadSessionCoreAsync"),
            call => call.Name == "RefreshComposerPersistenceNoticeForSession");
        Assert.Contains(CompiledArchitectureContract.CallsForMethod(typeof(MainWindow), "SessionDelete_Click"),
            call => call.Name == "RefreshComposerPersistenceNoticeForSession");
    }
}
