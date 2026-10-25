using System;
using System.IO;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services;

public sealed partial class ApiClient
{
    private static void ValidateDurableMessageAcknowledgement(JsonElement response, string sessionId,
        string userId, string role, string content)
    {
        if (response.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("message_acknowledgement_not_object");
        var messageId = RequiredString("messageId", "MessageId");
        if (!Guid.TryParse(messageId, out var id) || id == Guid.Empty)
            throw new InvalidDataException("message_acknowledgement_invalid_identity");
        if (!string.Equals(RequiredString("role", "Role"), role.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            throw new InvalidDataException("message_acknowledgement_role_mismatch");
        if (!string.Equals(RequiredString("content", "Content"), content.Trim(), StringComparison.Ordinal))
            throw new InvalidDataException("message_acknowledgement_content_mismatch");

        // The current ChatMessageDto does not echo session/user identity. If a
        // compatible backend supplies either field, reject a conflicting echo.
        ValidateOptionalIdentity("sessionId", "SessionId", sessionId);
        ValidateOptionalIdentity("userId", "UserId", userId);
        return;

        string RequiredString(string camel, string pascal)
        {
            if (!TryGet(camel, pascal, out var value) || value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("message_acknowledgement_required_field_missing");
            return value.GetString()!;
        }

        void ValidateOptionalIdentity(string camel, string pascal, string expected)
        {
            if (!TryGet(camel, pascal, out var value)) return;
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } actual
                || !(Guid.TryParse(actual, out var actualGuid) && Guid.TryParse(expected, out var expectedGuid)
                    ? actualGuid == expectedGuid : string.Equals(actual, expected, StringComparison.Ordinal)))
                throw new InvalidDataException("message_acknowledgement_owner_mismatch");
        }

        bool TryGet(string camel, string pascal, out JsonElement value)
            => response.TryGetProperty(camel, out value) || response.TryGetProperty(pascal, out value);
    }
}
