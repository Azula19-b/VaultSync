namespace VaultSync.Application;

public sealed class BackupScheduler : IAsyncDisposable
{
    private CancellationTokenSource? _loopCancellation;
    private Task? _loop;
    public bool IsRunning => _loop is { IsCompleted: false };

    public void Start(TimeSpan interval, Func<CancellationToken, Task> backup)
    {
        if (interval < TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(interval));
        Stop();
        _loopCancellation = new();
        _loop = RunLoopAsync(interval, backup, _loopCancellation.Token);
    }

    public void Stop()
    {
        _loopCancellation?.Cancel();
        _loopCancellation?.Dispose();
        _loopCancellation = null;
        _loop = null;
    }

    private static async Task RunLoopAsync(TimeSpan interval, Func<CancellationToken, Task> backup, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await backup(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}

