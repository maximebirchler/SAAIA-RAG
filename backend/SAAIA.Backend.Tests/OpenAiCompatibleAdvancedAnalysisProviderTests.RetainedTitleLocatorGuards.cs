using System.Collections;
using System.Reflection;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("retired")]
    [InlineData("revision")]
    [InlineData("hash")]
    [InlineData("path")]
    [InlineData("in-place-scope")]
    public void Retained_title_locators_expire_with_their_observed_source_scope(string change)
    {
        var body = NavigationContinuityBody("ORIGINAL", 1, 1);
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.NativeSourceTitleNavigationEnabled = true;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var context = AccumulatedReadContext(new AccumulatedReadGateway([body]));
        RememberRetainedLocatorPage(provider, context, body.Reference, 80);
        Assert.Single(RetainedLocators(context));
        AdvancedAnalysisResolvedEvidence[] active = [body];
        if (change == "retired") active = [];
        else
        {
            var reference = CopyAdvancedResultEvidence(body.Reference);
            if (change == "revision") typeof(AdvancedAnalysisResultEvidence).GetProperty("RevisionId")!.SetValue(reference, "99999999-2222-4222-8222-222222222222");
            if (change == "hash") typeof(AdvancedAnalysisResultEvidence).GetProperty("SourceHash")!.SetValue(reference, new string('f', 64));
            if (change == "path") typeof(AdvancedAnalysisResultEvidence).GetProperty("DocPath")!.SetValue(reference, "other.pdf");
            if (change == "in-place-scope")
            {
                typeof(AdvancedAnalysisResultEvidence).GetProperty("SourceHash")!.SetValue(body.Reference, new string('f', 64));
                reference = body.Reference;
            }
            active = [body with { Reference = reference }];
        }
        InvokeSourceCatalogue(provider, "PruneRetainedTitleLocators", BuildDirectRequest(), context, active);
        Assert.Empty(RetainedLocators(context));
    }

    [Theory]
    [InlineData("missing-result")]
    [InlineData("deferred")]
    [InlineData("failed")]
    [InlineData("changed-scope")]
    [InlineData("invented-next")]
    public void Retained_title_locators_do_not_create_capabilities_from_unexecuted_or_unbound_results(string kind)
    {
        var body = NavigationContinuityBody("ORIGINAL", 1, 1);
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.NativeSourceTitleNavigationEnabled = true;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var context = AccumulatedReadContext(new AccumulatedReadGateway([body]));
        RememberRetainedLocatorPage(provider, context, body.Reference, 80, kind);
        Assert.Empty(RetainedLocators(context));
    }

    [Fact]
    public void Retained_title_locators_are_bounded_metadata_without_body_text_and_disabled_flags_clear_them()
    {
        var body = NavigationContinuityBody("ORIGINAL", 1, 1);
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.NativeSourceTitleNavigationEnabled = true;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var gateway = new AccumulatedReadGateway([body]);
        var context = AccumulatedReadContext(gateway);
        for (var page = 1; page <= 80; page++) RememberRetainedLocatorPage(provider, context, body.Reference, page);
        var retained = RetainedLocators(context);
        Assert.Equal(64, retained.Length);
        Assert.Equal(Enumerable.Range(17, 64), retained.Select(row => (int)row.GetType().GetProperty("Page")!.GetValue(row)!));
        var serialized = JsonSerializer.Serialize(retained);
        Assert.DoesNotContain(body.Content, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Documented checkpoint", serialized, StringComparison.Ordinal);
        Assert.Empty(RetainedLocators(AccumulatedReadContext(gateway)));
        options.NativeSourceTitleNavigationEnabled = false;
        InvokeSourceCatalogue(provider, "PruneRetainedTitleLocators", BuildDirectRequest(), context, new[] { body });
        Assert.Empty(RetainedLocators(context));
        RememberRetainedLocatorPage(provider, context, body.Reference, 99);
        Assert.Empty(RetainedLocators(context));
        Assert.Empty(factory.Requests);
    }

    private static object[] RetainedLocators(object context)
        => ((IEnumerable)context.GetType().GetProperty("RetainedTitleLocators")!.GetValue(context)!).Cast<object>().ToArray();

    [Fact]
    public void Retained_title_locator_consumption_rejects_ambiguous_alias_and_disabled_or_direct_modes()
    {
        var body = NavigationContinuityBody("ORIGINAL", 1, 1);
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.NativeSourceTitleNavigationEnabled = true;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var context = AccumulatedReadContext(new AccumulatedReadGateway([body]));
        RememberRetainedLocatorPage(provider, context, body.Reference, 80);
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, null);
        var projection = SourceCatalogueProjection(provider, [body]);
        var load = BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option").Handoff.Load;
        object[] Pending() => ((IEnumerable)InvokeSourceCatalogue(provider, "FindPendingTitleLocatorsForReview", load, projection, context)!).Cast<object>().ToArray();
        Assert.Single(Pending());
        var handles = SourceCatalogueHandles(projection);
        var duplicate = Array.CreateInstance(handles[0].GetType(), 2);
        duplicate.SetValue(handles[0], 0); duplicate.SetValue(handles[0], 1);
        var catalogueProperty = projection.GetType().GetProperty("SourceCatalogue")!;
        var original = catalogueProperty.GetValue(projection);
        catalogueProperty.SetValue(projection, duplicate);
        Assert.Empty(Pending());
        catalogueProperty.SetValue(projection, original);
        options.NativeSourceTitleNavigationEnabled = false;
        Assert.Empty(Pending());
        options.NativeSourceTitleNavigationEnabled = true;
        Assert.Empty(((IEnumerable)InvokeSourceCatalogue(provider, "FindPendingTitleLocatorsForReview",
            BuildDirectRequest().Handoff.Load, projection, context)!).Cast<object>());
        Assert.Empty(factory.Requests);
    }

    private static void RememberRetainedLocatorPage(OpenAiCompatibleAdvancedAnalysisProvider provider, object context,
        AdvancedAnalysisResultEvidence scope, int physicalPage, string? kind = null)
    {
        RememberNavigationCursor(provider, context, scope, offset: 0, next: 1, kind: kind);
        if (kind is null)
        {
            var turn = context.GetType().GetProperty("NativeTurn")!.GetValue(context)!;
            var results = (Dictionary<string, AdvancedAnalysisSearchObservation>)turn.GetType().GetProperty("Results")!.GetValue(turn)!;
            var old = Assert.Single(results);
            results[old.Key] = new(AdvancedAnalysisSourceTitlePage.Query, [], [], 0, 1,
                SourceTitlePage: new(0, null,
                    [new("Documented checkpoint", "title_anchor", null, physicalPage, physicalPage)], false));
        }
        InvokeSourceCatalogue(provider, "RememberRetainedTitleLocators", BuildDirectRequest(), context);
    }
}
