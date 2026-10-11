namespace VaultSync.Infrastructure;

public sealed class SourceChangeMonitor : IAsyncDisposable
{
    private readonly object _sync = new();
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private DateTimeOffset _lastChange;
    private bool _dirty;

    public bool IsRunning => _watcher?.EnableRaisingEvents == true;

    public void Start(string sourcePath, TimeSpan debounce, TimeSpan reconciliationInterval,
        Func<CancellationToken, Task> onBackupRequired)
    {
        if (!Directory.Exists(sourcePath)) throw new DirectoryNotFoundException(sourcePath);
        if (debounce < TimeSpan.FromSeconds(1)) throw new ArgumentOutOfRangeException(nameof(debounce));
        if (reconciliationInterval < TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(reconciliationInterval));
        Stop();

        _cancellation = new();
        _watcher = new(sourcePath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
            EnableRaisingEvents = true
        };
        _watcher.Changed += MarkDirty;
        _watcher.Created += MarkDirty;
        _watcher.Deleted += MarkDirty;
        _watcher.Renamed += MarkDirty;
        _watcher.Error += (_, _) => SetDirty();
        _loop = MonitorAsync(debounce, reconciliationInterval, onBackupRequired, _cancellation.Token);
    }

    public void Stop()
    {
        _cancellation?.Cancel();
        _watcher?.Dispose();
        _cancellation?.Dispose();
        _watcher = null;
        _cancellation = null;
        _loop = null;
    }

    private void MarkDirty(object sender, FileSystemEventArgs args) => SetDirty();

    private void SetDirty()
    {
        lock (_sync)
        {
            _dirty = true;
            _lastChange = DateTimeOffset.UtcNow;
        }
    }

    private async Task MonitorAsync(TimeSpan debounce, TimeSpan reconciliationInterval,
        Func<CancellationToken, Task> onBackupRequired, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var nextReconciliation = DateTimeOffset.UtcNow + reconciliationInterval;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                bool shouldRun;
                lock (_sync)
                {
                    shouldRun = (_dirty && DateTimeOffset.UtcNow - _lastChange >= debounce) || DateTimeOffset.UtcNow >= nextReconciliation;
                    if (shouldRun) _dirty = false;
                }
                if (!shouldRun) continue;
                await onBackupRequired(cancellationToken).ConfigureAwait(false);
                nextReconciliation = DateTimeOffset.UtcNow + reconciliationInterval;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}
