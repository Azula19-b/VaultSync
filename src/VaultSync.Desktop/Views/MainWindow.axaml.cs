using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VaultSync.Desktop.ViewModels;

namespace VaultSync.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private async void SelectSource_Click(object? sender, RoutedEventArgs args)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Choose a folder to protect", AllowMultiple = false });
        if (folders.Count > 0 && DataContext is MainViewModel model) model.SourcePath = folders[0].Path.LocalPath;
    }

    private async void SelectVault_Click(object? sender, RoutedEventArgs args)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Choose a backup vault location", AllowMultiple = false });
        if (folders.Count > 0 && DataContext is MainViewModel model) model.VaultPath = folders[0].Path.LocalPath;
    }

    private async void SelectRecovery_Click(object? sender, RoutedEventArgs args)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Choose a recovery destination", AllowMultiple = false });
        if (folders.Count > 0 && DataContext is MainViewModel model) model.RecoveryPath = folders[0].Path.LocalPath;
    }
}

