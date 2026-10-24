using Microsoft.Extensions.Logging;

namespace SAAIA.Backend.Tests;

internal sealed class DiagnosticRecordingLogger<T> : ILogger<T>
{
    internal sealed record Entry(string Text, IReadOnlyDictionary<string, object?> Values);
    internal List<Entry> Events { get; } = [];
    internal List<IReadOnlyDictionary<string, object?>> Scopes { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        if (state is IEnumerable<KeyValuePair<string, object?>> fields)
            Scopes.Add(fields.ToDictionary(pair => pair.Key, pair => pair.Value));
        return null;
    }
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var values = state is IEnumerable<KeyValuePair<string, object?>> fields
            ? fields.ToDictionary(pair => pair.Key, pair => pair.Value) : new Dictionary<string, object?>();
        Events.Add(new(formatter(state, exception), values));
    }
}
