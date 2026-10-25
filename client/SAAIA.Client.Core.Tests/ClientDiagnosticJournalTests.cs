using System.Text.Json;
using System.Diagnostics;
using SAAIA.Client.Core.Diagnostics;
using Xunit;

namespace SAAIA.Client.Core.Tests;

[Collection("Diagnostic activity callbacks")]
public sealed class ClientDiagnosticJournalTests
{
    [Fact]
    public void Terminal_failure_in_borrowed_agent_cannot_be_overwritten_by_outer_success()
    {
        var journal = new ClientDiagnosticJournal();
        using (var ui = journal.BeginTurn("session"))
        {
            using (var agent = journal.BeginTurn())
            {
                journal.ReportTerminalFailure("local_model_unavailable");
                agent.Complete();
            }
            Assert.Empty(journal.Last());
            ui.Complete();
        }
        var record = Assert.Single(journal.Last());
        Assert.Equal("failed", record.Outcome);
        Assert.Equal("local_model_unavailable", record.Reason);
        Assert.Equal("failed", record.Events.Last().Outcome);
        using (var next = journal.BeginTurn()) next.Complete();
        Assert.Equal("success", journal.Last().Last().Outcome);
    }

    [Theory]
    [InlineData("model.call", "failed", "recovered_transport_failure")]
    [InlineData("evidence.judge", "success", "insufficient_documentation")]
    public void Recoverable_call_and_expected_insufficiency_do_not_mark_a_successful_turn_failed(string operation, string outcome, string reason)
    {
        var journal = new ClientDiagnosticJournal();
        using (var turn = journal.BeginTurn())
        {
            journal.Emit(operation, outcome, reason);
            turn.Complete();
        }
        Assert.Equal("success", Assert.Single(journal.Last()).Outcome);
    }

    [Fact]
    public void Explicit_user_cancellation_keeps_its_outcome_after_a_reported_failure()
    {
        var journal = new ClientDiagnosticJournal();
        using (var turn = journal.BeginTurn())
        {
            journal.ReportTerminalFailure("local_model_unavailable");
            turn.Complete("cancelled", "user_cancelled");
        }
        var record = Assert.Single(journal.Last());
        Assert.Equal("cancelled", record.Outcome);
        Assert.Equal("user_cancelled", record.Reason);
    }

    [Fact]
    public void Completed_turn_history_survives_new_orchestrators_and_honors_last_n()
    {
        var journal = new ClientDiagnosticJournal(capacity: 3);
        for (var index = 0; index < 5; index++)
        {
            using var turn = journal.BeginTurn("session-" + index);
            journal.Phase("router", index);
            turn.Complete();
        }
        Assert.Equal(new[] { "session-3", "session-4" }, journal.Last(2).Select(x => x.SessionId));
        Assert.Equal(3, journal.Last(500).Count);
    }

    [Fact]
    public void Unfinished_turn_is_not_returned_as_previous_performance()
    {
        var journal = new ClientDiagnosticJournal();
        using var turn = journal.BeginTurn();
        journal.Phase("router", 99);
        Assert.Empty(journal.Last());
    }

    [Fact]
    public async Task Ui_owner_covers_handoff_and_persistence_and_borrowed_agent_cannot_finalize_it()
    {
        var journal = new ClientDiagnosticJournal();
        using (var ui = journal.BeginTurn("conversation"))
        {
            using (var agent = journal.BeginTurn())
            {
                journal.Phase("localPipeline", 1);
                agent.Complete();
            }
            Assert.Empty(journal.Last());
            await Task.Delay(25);
            journal.Phase("advanced", 10);
            journal.Phase("uiPersistence", 5);
            ui.Complete();
        }
        var record = Assert.Single(journal.Last());
        Assert.True(record.TotalMilliseconds >= 20);
        Assert.Equal(10, record.Phases["advanced"]);
        Assert.Equal(5, record.Phases["uiPersistence"]);
    }

    [Fact]
    public void Completed_record_is_immutable_and_events_are_bounded_with_drop_count()
    {
        var journal = new ClientDiagnosticJournal(eventCapacity: 2);
        var measurements = new Dictionary<string, long?> { ["candidates"] = 4 };
        using (var turn = journal.BeginTurn())
        {
            journal.Emit("search", "success", measurements: measurements);
            measurements["candidates"] = 90;
            turn.Complete();
        }
        var record = Assert.Single(journal.Last());
        Assert.Equal(3, record.EventCount);
        Assert.Equal(1, record.DroppedEvents);
        Assert.Equal(4, record.Events[0].Measurements["candidates"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, long?>)record.Phases).Add("fake", 1));
    }

    [Fact]
    public void Collector_failure_does_not_change_answer_or_drop_terminal_status_silently()
    {
        var journal = new ClientDiagnosticJournal { EventSink = _ => throw new IOException("disk unavailable") };
        using (var turn = journal.BeginTurn()) turn.Complete("cancelled", "user_cancelled");
        Assert.Equal("cancelled", Assert.Single(journal.Last()).Outcome);
        Assert.Equal(2, journal.CollectorFailures);
    }

    [Fact]
    public void Uncompleted_scope_is_failed_and_w3c_correlation_is_stable_across_awaited_child()
    {
        var journal = new ClientDiagnosticJournal();
        string? trace;
        using (journal.BeginTurn("s"))
        {
            trace = journal.CurrentTraceId;
            Assert.Equal(32, trace!.Length);
            Assert.StartsWith("00-" + trace + "-", journal.CurrentTraceParent);
            using var child = journal.BeginTurn();
            Assert.Equal(trace, journal.CurrentTraceId);
        }
        var record = Assert.Single(journal.Last());
        Assert.Equal(trace, record.TraceId);
        Assert.Equal("failed", record.Outcome);
        Assert.Equal("scope_exited_without_completion", record.Reason);
        Assert.Null(journal.CurrentTraceId);
    }

    [Fact]
    public void Default_schema_does_not_collect_questions_answers_or_document_paths()
    {
        var journal = new ClientDiagnosticJournal();
        using (var turn = journal.BeginTurn())
        {
            journal.Emit("search", "success", "no_candidates", new Dictionary<string, long?> { ["count"] = 0 });
            turn.Complete();
        }
        var json = JsonSerializer.Serialize(journal.Last());
        Assert.DoesNotContain("Question", json);
        Assert.DoesNotContain("DocumentPath", json);
        Assert.DoesNotContain("AnswerText", json);
    }

    [Fact]
    public void Historical_performance_includes_failed_and_cancelled_questions_but_excludes_source_open_operations()
    {
        var journal = new ClientDiagnosticJournal();
        using (var turn = journal.BeginTurn()) turn.Complete("success");
        using (var turn = journal.BeginTurn()) turn.Complete("failed", "transport_failed");
        using (var turn = journal.BeginTurn(operation: "source_open")) turn.Complete("success");
        using (var turn = journal.BeginTurn()) turn.Complete("cancelled", "user_cancelled");
        using var active = journal.BeginTurn();
        Assert.Equal(new[] { "failed", "cancelled" }, journal.Last(2, "chat_turn").Select(record => record.Outcome));
        Assert.Equal(4, journal.Last(64).Count);
    }

    [Fact]
    public void Metadata_measurement_and_phase_limits_are_enforced_and_omissions_are_declared()
    {
        var journal = new ClientDiagnosticJournal();
        using (var turn = journal.BeginTurn())
        {
            journal.Configuration(Enumerable.Range(0, 100).ToDictionary(index => "field" + index, _ => new string('m', 2_000)));
            for (var index = 0; index < 100; index++) journal.Phase("phase" + index, index);
            journal.Phase("phase0", -1);
            journal.Emit(new string('o', 2_000), "invalid outcome", "free text omitted",
                Enumerable.Range(0, 100).ToDictionary(index => "count" + index, index => (long?)index),
                Enumerable.Range(0, 100).ToDictionary(index => "id" + index, _ => new string('x', 2_000)));
            turn.Complete();
        }
        var record = Assert.Single(journal.Last());
        Assert.Equal(ClientDiagnosticJournal.MaximumMetadataFields, record.Configuration.Count);
        Assert.All(record.Configuration.Values, value => Assert.Equal(ClientDiagnosticJournal.MaximumMetadataCharacters, value.Length));
        Assert.Equal(ClientDiagnosticJournal.MaximumMetadataFields, record.Phases.Count);
        Assert.Null(record.Phases["phase0"]);
        var entry = record.Events[1];
        Assert.Equal("unavailable", entry.Outcome);
        Assert.Equal("unstructured_reason_omitted", entry.Reason);
        Assert.Equal(ClientDiagnosticJournal.MaximumMeasurements, entry.Measurements.Count);
        Assert.Equal(ClientDiagnosticJournal.MaximumMetadataFields, entry.Identifiers.Count);
        Assert.True(record.OmittedMetadataFields > 0);
        Assert.True(record.TruncatedMetadataFields > 0);
    }

    [Fact]
    public async Task Background_resume_owns_its_lifetime_after_foreground_parent_finishes()
    {
        var journal = new ClientDiagnosticJournal();
        var continueBackground = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task background;
        using (var foreground = journal.BeginTurn("session"))
        {
            background = ResumeAsync();
            foreground.Complete();
        }
        continueBackground.SetResult();
        await background;
        Assert.Equal(2, journal.Last(64).Count);
        var resumed = Assert.Single(journal.Last(64), record => record.Operation == "advanced_resume");
        Assert.Contains(resumed.Events, entry => entry.Operation == "resume.after_parent_finished");
        Assert.Equal("success", resumed.Outcome);

        async Task ResumeAsync()
        {
            using var scope = journal.BeginTurn("session", "advanced_resume", independent: true);
            await continueBackground.Task;
            journal.Emit("resume.after_parent_finished", "success");
            scope.Complete();
        }
    }

    [Fact]
    public void Parent_disposed_before_child_records_each_terminal_event_in_the_correct_turn()
    {
        var journal = new ClientDiagnosticJournal();
        var parent = journal.BeginTurn();
        var parentId = journal.CurrentTurnId;
        var child = journal.BeginTurn(operation: "advanced_resume", independent: true);
        var childId = journal.CurrentTurnId;
        parent.Complete();
        parent.Dispose();
        Assert.Equal(childId, journal.CurrentTurnId);
        child.Complete("cancelled", "window_tracker_stopped");
        child.Dispose();
        var records = journal.Last(64);
        var parentRecord = Assert.Single(records, record => record.TurnId == parentId);
        var childRecord = Assert.Single(records, record => record.TurnId == childId);
        Assert.Equal("success", Assert.Single(parentRecord.Events, entry => entry.Operation == "turn.end").Outcome);
        Assert.Equal("cancelled", Assert.Single(childRecord.Events, entry => entry.Operation == "turn.end").Outcome);
        Assert.All(childRecord.Events, entry => Assert.Equal(childId, entry.TurnId));
        Assert.Null(journal.CurrentTurnId);
        Assert.True(Activity.Current is null || !Activity.Current.IsStopped);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Activity_callback_failure_cannot_destroy_completed_history_or_escape_disposal(bool failOnStart)
    {
        var journal = new ClientDiagnosticJournal();
        var callbacks = 0;
        void ThrowFromActivity(object? sender, ActivityChangedEventArgs args)
        {
            if ((failOnStart ? args.Current : args.Previous)?.OperationName != "saaia.client.turn") return;
            callbacks++;
            throw new IOException("injected collector failure");
        }
        Activity.CurrentChanged += ThrowFromActivity;
        try
        {
            using (var turn = journal.BeginTurn()) turn.Complete();
            Assert.Equal("success", Assert.Single(journal.Last()).Outcome);
            Assert.True(callbacks > 0);
            Assert.True(journal.CollectorFailures > 0);
            Assert.Null(journal.CurrentTurnId);
        }
        finally { Activity.CurrentChanged -= ThrowFromActivity; }
    }
}

[CollectionDefinition("Diagnostic activity callbacks", DisableParallelization = true)]
public sealed class DiagnosticActivityCallbacksCollection;
