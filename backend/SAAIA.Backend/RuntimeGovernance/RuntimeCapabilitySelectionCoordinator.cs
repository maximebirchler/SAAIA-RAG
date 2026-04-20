using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeCapabilitySelectionCoordinator
{
    internal static RuntimeCapabilityStaleReconciliationPlan? BuildStaleReconciliation(
        AdminRuntimeCapabilityStateDto current,
        string actor)
    {
        if (!current.Stale)
            return null;

        var staleReason = current.Details is not null
            && current.Details.TryGetValue("staleQualificationReason", out var value)
            && value is string reason
                ? reason
                : "qualification_inputs_changed";

        var details = new Dictionary<string, object?>(current.Details ?? new Dictionary<string, object?>(), StringComparer.Ordinal)
        {
            ["staleReconciledAt"] = DateTimeOffset.UtcNow,
            ["staleReconciledBy"] = actor
        };

        var updated = current with
        {
            Authorized = false,
            Selected = false,
            LastError = "qualification stale: requalify required",
            Details = details
        };

        var eventDetails = new Dictionary<string, object?>
        {
            ["persistedAuthorized"] = current.PersistedAuthorized,
            ["persistedSelected"] = current.PersistedSelected,
            ["effectiveAuthorized"] = updated.EffectiveAuthorized,
            ["effectiveSelected"] = updated.EffectiveSelected,
            ["lastError"] = updated.LastError
        };

        return new RuntimeCapabilityStaleReconciliationPlan(updated, staleReason, eventDetails);
    }

    internal static RuntimeCapabilitySelectionDecision EvaluateSelectionUpdate(
        AdminRuntimeCapabilityStateDto current,
        AdminRuntimeCapabilitySelectionRequestDto? req)
    {
        var desiredEnabled = req?.DesiredEnabled ?? current.DesiredEnabled;
        var authorized = req?.Authorized ?? current.Authorized;
        var selected = req?.Selected ?? current.Selected;
        var explicitlyRequestedAuthorized = req?.Authorized == true;
        var explicitlyRequestedSelected = req?.Selected == true;
        var authorizationContext = req?.Authorized ?? current.Authorized;

        RuntimeCapabilitySelectionDecision Reject(string error)
            => RuntimeCapabilitySelectionDecision.CreateRejected(
                current,
                error,
                desiredEnabled,
                authorized,
                selected,
                new Dictionary<string, object?>
                {
                    ["requestedDesiredEnabled"] = req?.DesiredEnabled,
                    ["requestedAuthorized"] = req?.Authorized,
                    ["requestedSelected"] = req?.Selected,
                    ["currentDesiredEnabled"] = current.DesiredEnabled,
                    ["currentAuthorized"] = current.Authorized,
                    ["currentSelected"] = current.Selected,
                    ["currentStale"] = current.Stale,
                    ["currentQualified"] = current.Qualified
                });

        if (explicitlyRequestedAuthorized && !desiredEnabled)
            return Reject("capability must be desired-enabled before it can be authorized");

        if (explicitlyRequestedSelected && !desiredEnabled)
            return Reject("capability must be desired-enabled before it can be selected");

        if (explicitlyRequestedSelected && !authorizationContext)
            return Reject("capability must be authorized before it can be selected");

        if (!desiredEnabled)
        {
            authorized = false;
            selected = false;
        }
        else if (!authorized)
        {
            selected = false;
        }

        if (current.Stale && (explicitlyRequestedAuthorized || explicitlyRequestedSelected))
            return Reject("capability must be requalified because its qualification is stale");

        if (authorized && !desiredEnabled)
            return Reject("capability must be desired-enabled before it can be authorized");

        if (authorized && !current.Qualified)
            return Reject("capability must be qualified before it can be authorized");

        if (selected && !desiredEnabled)
            return Reject("capability must be desired-enabled before it can be selected");

        if (selected && !authorized)
            return Reject("capability must be authorized before it can be selected");

        if (selected && !current.Qualified)
            return Reject("capability must be qualified before it can be selected");

        var details = new Dictionary<string, object?>(current.Details ?? new Dictionary<string, object?>(), StringComparer.Ordinal)
        {
            ["selectionUpdatedAt"] = DateTimeOffset.UtcNow,
            ["selectionPolicy"] = new Dictionary<string, object?>
            {
                ["desiredEnabled"] = desiredEnabled,
                ["authorized"] = authorized,
                ["selected"] = selected,
                ["requiresQualifiedForAuthorization"] = true,
                ["requiresDesiredEnabledForAuthorization"] = true,
                ["requiresDesiredEnabledForSelection"] = true
            }
        };

        var updated = current with
        {
            DesiredEnabled = desiredEnabled,
            Authorized = authorized && current.Qualified,
            Selected = selected && current.Qualified && authorized && desiredEnabled,
            Details = details
        };

        var eventDetails = new Dictionary<string, object?>
        {
            ["previousDesiredEnabled"] = current.DesiredEnabled,
            ["previousAuthorized"] = current.PersistedAuthorized,
            ["previousSelected"] = current.PersistedSelected,
            ["desiredEnabled"] = updated.DesiredEnabled,
            ["authorized"] = updated.Authorized,
            ["selected"] = updated.Selected,
            ["stale"] = updated.Stale
        };

        return RuntimeCapabilitySelectionDecision.CreateAccepted(
            updated,
            desiredEnabled,
            authorized,
            selected,
            eventDetails);
    }

    internal sealed record RuntimeCapabilityStaleReconciliationPlan(
        AdminRuntimeCapabilityStateDto UpdatedState,
        string StaleReason,
        IReadOnlyDictionary<string, object?> EventDetails);

    internal sealed record RuntimeCapabilitySelectionDecision(
        bool Accepted,
        AdminRuntimeCapabilityStateDto State,
        string? Error,
        bool DesiredEnabled,
        bool Authorized,
        bool Selected,
        IReadOnlyDictionary<string, object?> EventDetails)
    {
        internal static RuntimeCapabilitySelectionDecision CreateRejected(
            AdminRuntimeCapabilityStateDto state,
            string error,
            bool desiredEnabled,
            bool authorized,
            bool selected,
            IReadOnlyDictionary<string, object?> eventDetails)
            => new(
                Accepted: false,
                State: state,
                Error: error,
                DesiredEnabled: desiredEnabled,
                Authorized: authorized,
                Selected: selected,
                EventDetails: eventDetails);

        internal static RuntimeCapabilitySelectionDecision CreateAccepted(
            AdminRuntimeCapabilityStateDto state,
            bool desiredEnabled,
            bool authorized,
            bool selected,
            IReadOnlyDictionary<string, object?> eventDetails)
            => new(
                Accepted: true,
                State: state,
                Error: null,
                DesiredEnabled: desiredEnabled,
                Authorized: authorized,
                Selected: selected,
                EventDetails: eventDetails);
    }
}
