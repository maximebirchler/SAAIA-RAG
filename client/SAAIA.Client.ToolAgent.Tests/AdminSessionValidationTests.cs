using System.Net;
using System.Reflection;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class AdminSessionValidationTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "invalid")]
    [InlineData(HttpStatusCode.Forbidden, "invalid")]
    [InlineData(HttpStatusCode.NotFound, "unavailable")]
    [InlineData(HttpStatusCode.InternalServerError, "unavailable")]
    public void Admin_validation_failure_is_classified_consistently(HttpStatusCode statusCode, string expected)
    {
        var method = typeof(ApiClient).GetMethod("ClassifyAdminSessionValidationFailure", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var actual = (string)method!.Invoke(null, new object?[] { statusCode, false })!;
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Admin_validation_cancellation_is_treated_as_unavailable()
    {
        var method = typeof(ApiClient).GetMethod("ClassifyAdminSessionValidationFailure", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var actual = (string)method!.Invoke(null, new object?[] { null, true })!;
        Assert.Equal("unavailable", actual);
    }
}
