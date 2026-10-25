using System.Text.Json;
using SAAIA.Client.Core.NativeRouting;
using Xunit;

namespace SAAIA.Client.Core.Tests;

public sealed class NativeRouterResponseReaderTests
{
    [Fact]
    public void Reader_is_portable_and_uses_the_normal_core_assembly()
    {
        var assembly = typeof(NativeRouterResponseReader).Assembly;
        Assert.Equal("SAAIA.Client.Core", assembly.GetName().Name);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            reference.Name?.Contains("WinUI", StringComparison.OrdinalIgnoreCase) == true
            || reference.Name?.StartsWith("Microsoft.UI", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Typed_router_values_allow_case_insensitive_keys_without_coercing_strings_or_floats()
    {
        using var document = JsonDocument.Parse("""{"COUNT":4,"floatCount":4.5,"stringCount":"4","ready":true,"stringReady":"true","title":"  Named subject  "}""");
        var root = document.RootElement;
        Assert.True(NativeRouterResponseReader.TryReadNativeRouterInteger(root, "count", out var count));
        Assert.Equal(4, count);
        Assert.False(NativeRouterResponseReader.TryReadNativeRouterInteger(root, "floatCount", out _));
        Assert.False(NativeRouterResponseReader.TryReadNativeRouterInteger(root, "stringCount", out _));
        Assert.True(NativeRouterResponseReader.TryReadNativeRouterBoolean(root, "READY", out var ready));
        Assert.True(ready);
        Assert.False(NativeRouterResponseReader.TryReadNativeRouterBoolean(root, "stringReady", out _));
        Assert.Equal("Named subject", NativeRouterResponseReader.ReadNativeRouterString(root, "TITLE"));
        Assert.Equal(string.Empty, NativeRouterResponseReader.ReadNativeRouterString(root, "count"));
    }

    [Fact]
    public void Grid_preserves_literal_axes_and_requires_their_actual_cartesian_cardinality()
    {
        var root = JsonSerializer.SerializeToElement(new { rowHeader = "Jour", rows = new[] { "Lundi", "Mardi" }, columns = new[] { "Matin", "Soir" } });
        Assert.True(NativeRouterResponseReader.TryReadNativeRouterLayout(root, "grid", 4, out var header, out var rows, out var columns));
        Assert.Equal("Jour", header);
        Assert.Equal(new[] { "Lundi", "Mardi" }, rows);
        Assert.Equal(new[] { "Matin", "Soir" }, columns);
        Assert.False(NativeRouterResponseReader.TryReadNativeRouterLayout(root, "grid", 3, out _, out _, out _));
        Assert.False(NativeRouterResponseReader.TryReadNativeRouterLayout(root, "list", 4, out _, out _, out _));
    }

    [Theory]
    [InlineData("""{"rowHeader":"Jour","rows":["Lundi","lundi"],"columns":["Matin"]}""")]
    [InlineData("""{"rowHeader":"Jour","rows":["Lundi",17],"columns":["Matin"]}""")]
    [InlineData("""{"rowHeader":"Jour","rows":["Lundi"],"columns":["Jour","Matin"]}""")]
    [InlineData("""{"rowHeader":"Jour","rows":["Lundi("],"columns":["Matin","Soir"]}""")]
    [InlineData("""{"rowHeader":"Jour","rows":["Lundi\nMardi"],"columns":["Matin","Soir"]}""")]
    [InlineData("""{"rowHeader":"Jour","rows":"Lundi","columns":["Matin","Soir"]}""")]
    public void Malformed_axes_cannot_be_accepted_as_a_grid(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.False(NativeRouterResponseReader.TryReadNativeRouterLayout(document.RootElement, "grid", 2,
            out _, out _, out _));
    }

    [Fact]
    public void A_non_grid_route_accepts_absent_axes_without_inventing_a_layout()
    {
        using var document = JsonDocument.Parse("{}");
        Assert.True(NativeRouterResponseReader.TryReadNativeRouterLayout(document.RootElement, "list", 0,
            out var header, out var rows, out var columns));
        Assert.Empty(header);
        Assert.Empty(rows);
        Assert.Empty(columns);
    }

    [Theory]
    [InlineData(12, true)]
    [InlineData(13, false)]
    public void Grid_column_limit_is_explicit_and_never_clips_the_model_axes(int count, bool accepted)
    {
        var supplied = Enumerable.Range(1, count).Select(i => "Column " + i).ToArray();
        var root = JsonSerializer.SerializeToElement(new { rowHeader = "Row", rows = new[] { "A" }, columns = supplied });
        Assert.Equal(accepted, NativeRouterResponseReader.TryReadNativeRouterLayout(root, "grid", count, out _, out _, out var columns));
        Assert.Equal(supplied, columns);
    }
}
