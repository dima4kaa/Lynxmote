using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using ProxMonitor.Core;

namespace ProxMonitor.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Log.Start(Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "proxmonitor.log"));
            Log.Write("app", $"ProxMonitor {typeof(App).Assembly.GetName().Version} started on {Environment.OSVersion}");
            var settings = AppSettings.Load();
            L.English = settings.Language == "en"; // before any text is built
            var vm = new MainViewModel(settings);
            RequestedThemeVariant = vm.Theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
            if (desktop.Args?.Contains("--demo") == true) vm.EnableDemo();
            desktop.MainWindow = new MainWindow { DataContext = vm };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
