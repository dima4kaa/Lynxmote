using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Lynxmote.Core;

namespace Lynxmote.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var dir = Path.GetDirectoryName(AppSettings.DefaultPath)!;
            AppSettings.AdoptFolder(Path.Combine(Path.GetDirectoryName(dir)!, "ProxMonitor"), dir); // the app's name before 1.7.0
            Log.Start(Path.Combine(dir, "lynxmote.log"));
            Log.Write("app", $"Lynxmote {typeof(App).Assembly.GetName().Version} started on {Environment.OSVersion}");
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
