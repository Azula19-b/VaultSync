using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VaultSync.Application;
using VaultSync.Core;
using VaultSync.Infrastructure;

namespace VaultSync.Desktop.ViewModels;

public partial class MainViewModel(
    IVaultManager vaultManager, IBackupService backupService, IRecoveryService recoveryService,
    IVaultMetadataStore metadata, IVaultObjectStore objects, SourceChangeMonitor monitor, VaultImporter importer)
    : ViewModelBase, IAsyncDisposable, IDisposable
{
    private UnlockedVault? _vault;
    private CancellationTokenSource? _operationCancellation;

    [ObservableProperty] public partial int SelectedSection { get; set; }
    [ObservableProperty] public partial string SourcePath { get; set; } = string.Empty;
    [ObservableProperty] public partial string VaultPath { get; set; } = string.Empty;
    [ObservableProperty] public partial string RecoveryPath { get; set; } = string.Empty;
    [ObservableProperty] public partial string Password { get; set; } = string.Empty;
    [ObservableProperty] public partial string VaultName { get; set; } = "My Secure Vault";
    [ObservableProperty] public partial string StatusMessage { get; set; } = "Choose a source and vault location to begin.";
    [ObservableProperty] public partial string ExclusionsText { get; set; } = "*.tmp\n.DS_Store\nnode_modules/";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsVaultUnlocked { get; set; }
    [ObservableProperty] public partial bool IsAutomaticBackupEnabled { get; set; }
    [ObservableProperty] public partial bool OverwriteExisting { get; set; }
    [ObservableProperty] public partial int IntervalMinutes { get; set; } = 30;
    [ObservableProperty] public partial double ProgressPercent { get; set; }
    [ObservableProperty] public partial string ProgressLabel { get; set; } = "Ready";
    [ObservableProperty] public partial SnapshotSummary? SelectedSnapshot { get; set; }
    [ObservableProperty] public partial string ProtectedFilesText { get; set; } = "0";
    [ObservableProperty] public partial string SnapshotCountText { get; set; } = "0";
    [ObservableProperty] public partial string LogicalSizeText { get; set; } = "0 B";
    [ObservableProperty] public partial string PhysicalSizeText { get; set; } = "0 B";
    [ObservableProperty] public partial string SavedSizeText { get; set; } = "0 B";
    [ObservableProperty] public partial string SuccessRateText { get; set; } = "—";
    [ObservableProperty] public partial string LastBackupText { get; set; } = "Never";

    public ObservableCollection<SnapshotSummary> Snapshots { get; } = [];
    public ObservableCollection<SnapshotFile> Files { get; } = [];
    public ObservableCollection<DuplicateGroup> DuplicateGroups { get; } = [];
    public ObservableCollection<string> Activity { get; } = [];

    partial void OnSelectedSnapshotChanged(SnapshotSummary? value) => _ = LoadSnapshotFilesAsync(value);

    [RelayCommand]
    private void Navigate(string? section)
    {
        if (int.TryParse(section, out var index)) SelectedSection = index;
    }

    [RelayCommand]
    private async Task InitializeVaultAsync()
    {
        if (string.IsNullOrWhiteSpace(VaultPath)) { StatusMessage = "Choose a vault destination first."; return; }
        await RunOperationAsync(async token =>
        {
            _vault?.Dispose();
            _vault = await vaultManager.CreateAsync(VaultPath, VaultName, Password, VaultManager.DefaultKdfIterations, token);
            Password = string.Empty;
            IsVaultUnlocked = true;
            await metadata.InitializeAsync(_vault.RootPath, token);
            StatusMessage = "Vault initialized and unlocked.";
            Activity.Insert(0, "Secure vault initialized");
            await RefreshCoreAsync(token);
        });
    }

    [RelayCommand]
    private async Task UnlockVaultAsync()
    {
        if (string.IsNullOrWhiteSpace(VaultPath)) { StatusMessage = "Choose an existing vault first."; return; }
        await RunOperationAsync(async token =>
        {
            _vault?.Dispose();
            _vault = await vaultManager.UnlockAsync(VaultPath, Password, token);
            Password = string.Empty;
            IsVaultUnlocked = true;
            await metadata.InitializeAsync(_vault.RootPath, token);
            StatusMessage = "Vault unlocked.";
            await RefreshCoreAsync(token);
        });
    }

    [RelayCommand]
    private async Task StartBackupAsync()
    {
        if (_vault is null) { StatusMessage = "Initialize or unlock the vault first."; return; }
        if (string.IsNullOrWhiteSpace(SourcePath)) { StatusMessage = "Choose a protected folder first."; return; }
        await RunOperationAsync(BackupCoreAsync);
    }

    private async Task BackupCoreAsync(CancellationToken token)
    {
        if (_vault is null) return;
        var progress = new Progress<BackupProgress>(value =>
        {
            ProgressPercent = value.Percent;
            ProgressLabel = value.CurrentPath is null ? value.Stage.ToString() : $"{value.Stage}: {value.CurrentPath}";
        });
        var result = await backupService.RunAsync(_vault, SourcePath, ParseExclusions(), progress, token);
        StatusMessage = $"Snapshot complete: {result.FileCount} files, {FormatBytes(result.NewStoredBytes)} newly stored.";
        Activity.Insert(0, $"Backup completed • {result.FileCount} files • {DateTime.Now:t}");
        await RefreshCoreAsync(token);
    }

    [RelayCommand]
    private void CancelOperation() => _operationCancellation?.Cancel();

    [RelayCommand]
    private void ToggleAutomaticBackup()
    {
        if (IsAutomaticBackupEnabled)
        {
            monitor.Stop();
            IsAutomaticBackupEnabled = false;
            StatusMessage = "Automatic backup paused.";
            return;
        }
        if (_vault is null || string.IsNullOrWhiteSpace(SourcePath))
        {
            StatusMessage = "Unlock a vault and choose a protected folder first.";
            return;
        }
        monitor.Start(SourcePath, TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(Math.Max(1, IntervalMinutes)), async token =>
        {
            try { await BackupCoreAsync(token); }
            catch (InvalidOperationException) { }
            catch (Exception exception) { StatusMessage = "Automatic backup failed: " + exception.Message; }
        });
        IsAutomaticBackupEnabled = true;
        StatusMessage = "Automatic backup is active while VaultSync remains open.";
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (_vault is null || SelectedSnapshot is null) { StatusMessage = "Unlock a vault and select a snapshot."; return; }
        if (string.IsNullOrWhiteSpace(RecoveryPath)) { StatusMessage = "Choose a recovery destination."; return; }
        await RunOperationAsync(async token =>
        {
            var progress = new Progress<BackupProgress>(value =>
            {
                ProgressPercent = value.Percent;
                ProgressLabel = value.CurrentPath is null ? "Verifying restored content" : $"Restoring {value.CurrentPath}";
            });
            var result = await recoveryService.RestoreAsync(_vault, SelectedSnapshot.Id, null, RecoveryPath, OverwriteExisting, progress, token);
            StatusMessage = result.Failures.Count == 0
                ? $"Restored and verified {result.RestoredFiles} files."
                : $"Restored {result.RestoredFiles} files; {result.Failures.Count} failed. {result.Failures[0].Message}";
            Activity.Insert(0, $"Recovery finished • {result.RestoredFiles} files • {DateTime.Now:t}");
        });
    }

    [RelayCommand]
    private async Task RebuildIndexAsync()
    {
        if (_vault is null) { StatusMessage = "Unlock the vault first."; return; }
        await RunOperationAsync(async token =>
        {
            var count = await importer.RebuildIndexAsync(_vault, token);
            StatusMessage = $"Rebuilt the local index from {count} authenticated manifests.";
            await RefreshCoreAsync(token);
        });
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_vault is not null) await RunOperationAsync(RefreshCoreAsync);
    }

    private async Task RefreshCoreAsync(CancellationToken token)
    {
        if (_vault is null) return;
        var snapshots = await metadata.GetSnapshotsAsync(token);
        Snapshots.Clear();
        foreach (var snapshot in snapshots) Snapshots.Add(snapshot);
        SelectedSnapshot = Snapshots.FirstOrDefault(snapshot => snapshot.State == SnapshotState.Completed);
        var analytics = await metadata.GetAnalyticsAsync(objects.GetPhysicalBytes(_vault.RootPath), token);
        ProtectedFilesText = analytics.ProtectedFiles.ToString("N0");
        SnapshotCountText = analytics.SnapshotCount.ToString("N0");
        LogicalSizeText = FormatBytes(analytics.LogicalBytes);
        PhysicalSizeText = FormatBytes(analytics.PhysicalBytes);
        SavedSizeText = FormatBytes(analytics.DeduplicatedBytes);
        SuccessRateText = analytics.SnapshotCount == 0 ? "—" : $"{analytics.SuccessRate:F0}%";
        LastBackupText = analytics.LastSuccessfulBackup?.ToLocalTime().ToString("g") ?? "Never";
    }

    private async Task LoadSnapshotFilesAsync(SnapshotSummary? snapshot)
    {
        Files.Clear();
        DuplicateGroups.Clear();
        if (snapshot is null || _vault is null || snapshot.State != SnapshotState.Completed) return;
        try
        {
            var manifest = await metadata.GetSnapshotAsync(snapshot.Id, CancellationToken.None);
            if (manifest is not null) foreach (var file in manifest.Files) Files.Add(file);
            foreach (var group in await metadata.GetDuplicatesAsync(snapshot.Id, CancellationToken.None)) DuplicateGroups.Add(group);
        }
        catch (Exception exception) { StatusMessage = exception.Message; }
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (IsBusy) { StatusMessage = "Another operation is already running."; return; }
        IsBusy = true;
        _operationCancellation = new();
        try { await operation(_operationCancellation.Token); }
        catch (OperationCanceledException) { StatusMessage = "Operation cancelled safely."; }
        catch (Exception exception) { StatusMessage = exception.Message; Activity.Insert(0, "Error • " + exception.Message); }
        finally { _operationCancellation.Dispose(); _operationCancellation = null; IsBusy = false; }
    }

    private string[] ParseExclusions() => ExclusionsText.Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.#} {units[unit]}";
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _operationCancellation?.Cancel();
        monitor.Stop();
        _vault?.Dispose();
    }
}
