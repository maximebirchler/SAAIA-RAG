namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    private readonly HashSet<string> _loggedSoftUiFailureScopes = new(StringComparer.Ordinal);

    private void TrySoftUi(string scope, Action action, bool logOnce = true)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            if (!logOnce || _loggedSoftUiFailureScopes.Add(scope))
                ClientLog.Exception(scope, ex);
        }
    }

    private async Task TrySoftUiAsync(string scope, Func<Task> action, bool logOnce = true)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (!logOnce || _loggedSoftUiFailureScopes.Add(scope))
                ClientLog.Exception(scope, ex);
        }
    }

    private T? TrySoftUi<T>(string scope, Func<T> action, T? fallback = default, bool logOnce = true)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            if (!logOnce || _loggedSoftUiFailureScopes.Add(scope))
                ClientLog.Exception(scope, ex);
            return fallback;
        }
    }
}
