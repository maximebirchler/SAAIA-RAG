namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed class SourceBackedLlmStepTimeoutPolicy
{
    private readonly TimeSpan? _fixedTimeout;

    public SourceBackedLlmStepTimeoutPolicy()
    {
    }

    private SourceBackedLlmStepTimeoutPolicy(TimeSpan fixedTimeout)
    {
        _fixedTimeout = fixedTimeout;
    }

    public static SourceBackedLlmStepTimeoutPolicy Default { get; } = new();

    public static SourceBackedLlmStepTimeoutPolicy Fixed(TimeSpan timeout)
        => new(timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : timeout);

    public TimeSpan Resolve(SourceBackedPipelineStep step, string eventName, int promptChars)
    {
        if (_fixedTimeout is { } fixedTimeout)
            return fixedTimeout;

        var overrideSeconds = TryReadTimeoutSeconds("SAAIA_SOURCE_BACKED_LLM_STEP_TIMEOUT_SECONDS")
                              ?? TryReadTimeoutSeconds("SAAIA_SOURCE_BACKED_LLM_TIMEOUT_SECONDS");
        if (overrideSeconds is { } seconds)
            return TimeSpan.FromSeconds(seconds);

        var baseSeconds = ResolveDefaultSeconds(step, eventName);
        var promptBufferSeconds = promptChars switch
        {
            > 14000 => 60,
            > 10000 => 30,
            _ => 0
        };
        return TimeSpan.FromSeconds(baseSeconds + promptBufferSeconds);
    }

    internal static int ResolveDefaultSeconds(SourceBackedPipelineStep step, string eventName)
    {
        // Live 46 slowed to 1.49 generated tokens/s and was cancelled at the
        // former 210 s deadline after only 255 tokens. At that measured rate,
        // the bounded 480-token contract needs about 322 s plus prompt and
        // scheduling overhead. Keep a finite margin on this initial step only;
        // do not inflate the focused Planner repairs below.
        if (step == SourceBackedPipelineStep.Planner
            && string.Equals(eventName, "planner", StringComparison.OrdinalIgnoreCase))
        {
            return 360;
        }

        // Live 48 was cancelled after 240.1 seconds and about 347 generated
        // audit tokens while the local runtime had slowed to roughly 1.4
        // tokens/s. This typed audit has a bounded 480-token contract, which can
        // therefore need about 343 seconds plus prompt overhead. Keep the same
        // finite 360 s margin as the initial Planner without widening repairs.
        if (step == SourceBackedPipelineStep.Planner
            && string.Equals(eventName, "planner_intake_review", StringComparison.OrdinalIgnoreCase))
        {
            return 360;
        }

        // Live 50 reached the former 150 s deadline after roughly 166 tokens
        // of the bounded column-axis object. At the measured 1.1 token/s, the
        // focused 256-token contract can need about 233 s. Keep a finite 240 s
        // margin on the initial column opinion only; its repairs remain at 150 s.
        if (step == SourceBackedPipelineStep.Planner
            && string.Equals(
                eventName,
                "planner_intake_column_axis_adjudication",
                StringComparison.OrdinalIgnoreCase))
        {
            return 240;
        }

        // The keyed structured-term planner can emit up to 640 tokens. Keep a
        // finite slow-machine margin so the output budget is reachable instead
        // of being pre-empted by the generic 150 s planner timeout.
        if (step == SourceBackedPipelineStep.Planner
            && Contains(eventName, "planner_compact_facet_plan"))
        {
            return 480;
        }

        if (Contains(eventName, "final_selection_atomic_repair"))
            return 90;

        if (Contains(eventName, "final_selection_repair"))
            return 120;

        if (Contains(eventName, "selection_repair"))
            return 150;

        if (string.Equals(
                eventName,
                "evidence_status_review_action_repair",
                StringComparison.OrdinalIgnoreCase))
        {
            return 120;
        }

        if (Contains(eventName, "evidence_status_review"))
            return 180;

        if (Contains(eventName, "structured_value_type_fit_decision_batch"))
            return 90;

        if (Contains(eventName, "structured_value_type_fit_atomic_repair"))
            return 90;

        if (Contains(eventName, "structured_value_type_fit"))
            return 180;

        if (Contains(eventName, "structured_value_type_follow_up_repair"))
            return 120;

        if (Contains(eventName, "structured_value_type_repair"))
            return 210;

        if (Contains(eventName, "structured_thin_cell_atomic_repair"))
            return 120;

        // A large typed table serializes substantially more output than the
        // generic writer or repair. Live 20-cell runs reached the former 180 s
        // focused-repair limit, so both typed-table producers get the same small
        // dedicated margin without inflating unrelated LLM steps.
        if (Contains(eventName, "structured_writer")
            || Contains(eventName, "structured_cell_repair"))
            return 210;

        if (Contains(eventName, "action_repair"))
            return 120;

        return step switch
        {
            SourceBackedPipelineStep.Planner => 150,
            SourceBackedPipelineStep.EvidenceJudge => 120,
            SourceBackedPipelineStep.Writer => 180,
            SourceBackedPipelineStep.AnswerAdequacyJudge => 120,
            SourceBackedPipelineStep.Repair => 180,
            _ => 120
        };
    }

    private static int? TryReadTimeoutSeconds(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (!int.TryParse(raw, out var seconds))
            return null;

        return Math.Clamp(seconds, 5, 900);
    }

    private static bool Contains(string value, string fragment)
        => value.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
