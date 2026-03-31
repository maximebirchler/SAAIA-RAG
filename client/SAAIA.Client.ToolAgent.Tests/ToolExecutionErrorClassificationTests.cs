using System;
using System.Net;
using System.Net.Http;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ToolExecutionErrorClassificationTests
{
    [Fact]
    public void Admin_http_401_is_classified_as_admin_invalid_or_forbidden()
    {
        var ex = new HttpRequestException("401 Unauthorized", null, HttpStatusCode.Unauthorized);
        var code = ToolAgentOrchestrator.ClassifyToolExecutionError(ex, isAdminTool: true);
        Assert.Equal("admin_invalid_or_forbidden", code);
    }

    [Fact]
    public void Admin_http_403_is_classified_as_admin_invalid_or_forbidden()
    {
        var ex = new HttpRequestException("403 Forbidden", null, HttpStatusCode.Forbidden);
        var code = ToolAgentOrchestrator.ClassifyToolExecutionError(ex, isAdminTool: true);
        Assert.Equal("admin_invalid_or_forbidden", code);
    }

    [Fact]
    public void Admin_plain_message_with_403_is_classified_as_admin_invalid_or_forbidden()
    {
        var ex = new InvalidOperationException("Backend failed with 403 Forbidden");
        var code = ToolAgentOrchestrator.ClassifyToolExecutionError(ex, isAdminTool: true);
        Assert.Equal("admin_invalid_or_forbidden", code);
    }

    [Fact]
    public void Non_admin_http_403_keeps_generic_tool_failure_classification()
    {
        var ex = new HttpRequestException("403 Forbidden", null, HttpStatusCode.Forbidden);
        var code = ToolAgentOrchestrator.ClassifyToolExecutionError(ex, isAdminTool: false);
        Assert.Equal("tool_failed", code);
    }
}
