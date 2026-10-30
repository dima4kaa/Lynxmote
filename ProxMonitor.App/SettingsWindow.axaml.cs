using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ProxMonitor.Core;

namespace ProxMonitor.App;

public partial class SettingsWindow : Window
{
    readonly MainViewModel? _vm;

    public SettingsWindow()
    {
        InitializeComponent();
        this.UseOwnTitleBar(this, captionButtons: false);
    }

    public SettingsWindow(MainViewModel vm) : this()
    {
        _vm = vm;
        LanguageBox.SelectedIndex = vm.Language == "en" ? 1 : 0;
        var t = vm.GlobalThresholds;
        (CpuBox.Value, TempBox.Value, DiskBox.Value) = ((decimal)t.CpuPct, (decimal)t.TempC, (decimal)t.DiskPct);
    }

    void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    void Language_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        var language = LanguageBox.SelectedIndex == 1 ? "en" : "ru";
        _vm.Language = language;
        // Texts are built once at startup, so a switch needs a restart.
        RestartButton.IsVisible = LanguageHint.IsVisible = (language == "en") != L.English;
    }

    void Restart_Click(object? sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(Environment.ProcessPath!);
        (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    static readonly FilePickerFileType JsonFile = new(L.T("Список серверов")) { Patterns = ["*.json"] };

    async void Export_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("Экспорт списка серверов"), SuggestedFileName = "proxmonitor-servers.json",
            DefaultExtension = "json", FileTypeChoices = [JsonFile],
        });
        if (file is null) return;
        await using (var stream = await file.OpenWriteAsync())
        await using (var writer = new StreamWriter(stream))
            await writer.WriteAsync(_vm.ExportServers());
        ListStatus.Text = L.F("Сохранено: {0}", file.Name);
    }

    async void Import_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("Импорт списка серверов"), FileTypeFilter = [JsonFile],
        });
        if (files.Count == 0) return;

        List<ServerEntry> entries;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            entries = ServerList.Import(await new StreamReader(stream).ReadToEndAsync());
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            ListStatus.Text = ex.Message;
            return;
        }

        // Each new server asks for its secret, which never travels in the file.
        var (added, skipped) = (0, 0);
        foreach (var entry in entries)
        {
            if (_vm.HasServer(entry) || await new ConnectWindow(entry).ShowDialog<NewServer?>(this) is not { } server)
            {
                skipped++;
                continue;
            }
            _vm.AddServer(server.Config, server.Secret);
            added++;
        }
        ListStatus.Text = L.F("Добавлено серверов: {0}", added) + (skipped > 0 ? L.F(", пропущено: {0}", skipped) : "");
    }

    void Save_Click(object? sender, RoutedEventArgs e)
    {
        _vm?.SetGlobalThresholds(Read(CpuBox, TempBox, DiskBox));
        Close();
    }

    /// <summary>Reads the boxes' text: NumericUpDown only commits typed digits on Enter or focus loss.</summary>
    public static Thresholds Read(NumericUpDown cpu, NumericUpDown temp, NumericUpDown disk)
    {
        static double Of(NumericUpDown b) =>
            (double)Math.Clamp(decimal.TryParse(b.Text, out var v) ? v : b.Value ?? b.Minimum, b.Minimum, b.Maximum);
        return new Thresholds(Of(cpu), Of(temp), Of(disk));
    }
}
