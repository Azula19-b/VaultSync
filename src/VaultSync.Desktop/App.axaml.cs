using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using VaultSync.Infrastructure;
using VaultSync.Desktop.ViewModels;
using VaultSync.Desktop.Views;

namespace VaultSync.Desktop;

public partial class App : Avalonia.Application
{
    private ServiceProvider? _services;
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = new ServiceCollection().AddVaultSync().AddSingleton<MainViewModel>().BuildServiceProvider();
            desktop.MainWindow = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainViewModel>(),
            };
            desktop.Exit += (_, _) => _services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
