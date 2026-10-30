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
        var t = vm.GlobalThresholds;
        (CpuBox.Value, TempBox.Value, DiskBox.Value) = ((decimal)t.CpuPct, (decimal)t.TempC, (decimal)t.DiskPct);
    }

    void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    static readonly FilePickerFileType JsonFile = new("Список серверов") { Patterns = ["*.json"] };

    async void Export_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Экспорт списка серверов", SuggestedFileName = "proxmonitor-servers.json",
            DefaultExtension = "json", FileTypeChoices = [JsonFile],
        });
        if (file is null) return;
        await using (var stream = await file.OpenWriteAsync())
        await using (var writer = new StreamWriter(stream))
            await writer.WriteAsync(_vm.ExportServers());
        ListStatus.Text = $"Сохранено: {file.Name}";
    }

    async void Import_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Импорт списка серверов", FileTypeFilter = [JsonFile],
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
        ListStatus.Text = $"Добавлено серверов: {added}" + (skipped > 0 ? $", пропущено: {skipped}" : "");
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
