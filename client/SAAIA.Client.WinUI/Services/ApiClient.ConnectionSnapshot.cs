using System;

namespace SAAIA.Client.WinUI.Services;

public sealed partial class ApiClient
{
    // A background owner retains its original endpoint and identity even if the UI reconnects.
    internal ApiClient CreateConnectionSnapshot()
    {
        var configuration = GetConfigSnapshot();
        var snapshot = new ApiClient(_http);
        snapshot.Configure(configuration.BaseUrl, configuration.ApiKey, configuration.UserId, configuration.AdminKey);
        snapshot._adminSessionRevision = configuration.AdminSessionRevision;
        return snapshot;
    }

    internal bool HasSameConnection(ApiClient other)
    {
        var current = GetConfigSnapshot();
        var candidate = other.GetConfigSnapshot();
        return string.Equals(current.BaseUrl, candidate.BaseUrl, StringComparison.Ordinal)
            && string.Equals(current.ApiKey, candidate.ApiKey, StringComparison.Ordinal)
            && string.Equals(current.UserId, candidate.UserId, StringComparison.Ordinal);
    }

    internal void SynchronizeAdminSession(ApiClient activeConnection)
    {
        var active = activeConnection.GetConfigSnapshot();
        lock (_configGate)
        {
            if (string.Equals(_baseUrl, active.BaseUrl, StringComparison.Ordinal)
                && string.Equals(_apiKey, active.ApiKey, StringComparison.Ordinal)
                && string.Equals(_userId, active.UserId, StringComparison.Ordinal))
            {
                _adminKey = active.AdminKey;
                _adminSessionRevision = active.AdminSessionRevision;
            }
        }
    }

    internal bool TryInvalidateAdminSession(ApiClient rejectedRequest)
    {
        var rejected = rejectedRequest.GetConfigSnapshot();
        lock (_configGate)
        {
            if (!string.Equals(_baseUrl, rejected.BaseUrl, StringComparison.Ordinal)
                || !string.Equals(_apiKey, rejected.ApiKey, StringComparison.Ordinal)
                || !string.Equals(_userId, rejected.UserId, StringComparison.Ordinal)
                || !string.Equals(_adminKey, rejected.AdminKey, StringComparison.Ordinal)
                || _adminSessionRevision != rejected.AdminSessionRevision)
                return false;
            _adminKey = string.Empty;
            _adminSessionRevision++;
            return true;
        }
    }
}
