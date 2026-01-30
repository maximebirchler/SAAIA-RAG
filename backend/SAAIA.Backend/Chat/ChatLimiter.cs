using Microsoft.Extensions.Options;

namespace SAAIA.Backend.Chat;

public sealed class ChatLimiter
{
    private readonly SemaphoreSlim _sem;
    private readonly int _maxQueue;
    private int _waiting;

    public ChatLimiter(IOptions<ChatOptions> opt)
    {
        var o = opt.Value;
        var conc = Math.Clamp(o.MaxConcurrentGenerations, 1, 64);
        _maxQueue = Math.Clamp(o.MaxQueueLength, 0, 1_000_000);
        _sem = new SemaphoreSlim(conc, conc);
    }

    public async Task<Lease?> TryAcquireAsync(CancellationToken ct)
    {
        // Fast path: slot dispo
        if (_sem.Wait(0))
            return new Lease(this);

        // Pas de queue autorisée
        if (_maxQueue == 0)
            return null;

        // Queue limitée
        var w = Interlocked.Increment(ref _waiting);
        if (w > _maxQueue)
        {
            Interlocked.Decrement(ref _waiting);
            return null;
        }

        try
        {
            await _sem.WaitAsync(ct);
            return new Lease(this);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    private void Release() => _sem.Release();

    public sealed class Lease : IAsyncDisposable
    {
        private readonly ChatLimiter _owner;
        private int _disposed;

        internal Lease(ChatLimiter owner) => _owner = owner;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _owner.Release();
            return ValueTask.CompletedTask;
        }
    }
}
