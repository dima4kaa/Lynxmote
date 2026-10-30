using Avalonia.Controls;
using Avalonia.Interactivity;
using ProxMonitor.Core;

namespace ProxMonitor.App;

/// <summary>Builds widgets that watch several servers at once or run a command.</summary>
public partial class WidgetsWindow : Window
{
    readonly MainViewModel? _vm;

    static readonly (string Kind, string Hint)[] Kinds =
    [
        (WidgetKind.Group, "Несколько серверов в одной карточке: состояние, CPU и память каждого."),
        (WidgetKind.Summary, "Сколько запущено, средняя загрузка, самая высокая температура и число проблем."),
        (WidgetKind.Top, "Серверы с самым высоким выбранным показателем, по убыванию."),
        (WidgetKind.Command, "Кнопка, которая выполняет команду на сервере и показывает ответ. Нужен сохранённый вход по SSH: один раз откройте терминал этого сервера."),
    ];

    static readonly (string Id, string Name)[] Metrics =
        [("cpu", "CPU"), ("ram", "Память"), ("disk", "Диск"), ("temp", "Температура")];

    public WidgetsWindow()
    {
        InitializeComponent();
        this.UseOwnTitleBar(this, captionButtons: false);
    }

    public WidgetsWindow(MainViewModel vm) : this()
    {
        _vm = vm;
        KindBox.ItemsSource = Kinds.Select(k => L.T(WidgetVm.KindName(k.Kind))).ToList();
        MetricBox.ItemsSource = Metrics.Select(m => L.T(m.Name)).ToList();
        MetricBox.SelectedIndex = 0;
        ServerList.ItemsSource = vm.AllResources;
        ShowWidgets();
        KindBox.SelectedIndex = 0;
    }

    string Kind => Kinds[Math.Max(0, KindBox.SelectedIndex)].Kind;

    void ShowWidgets()
    {
        var custom = _vm!.Widgets.Where(w => !w.IsServer).ToList();
        WidgetList.ItemsSource = custom;
        NoWidgets.IsVisible = custom.Count == 0;
    }

    void Kind_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (KindBox.SelectedIndex < 0) return;
        var k = Kinds[KindBox.SelectedIndex];
        KindHint.Text = L.T(k.Hint);
        TopOptions.IsVisible = Kind == WidgetKind.Top;
        CommandOptions.IsVisible = Kind == WidgetKind.Command;
        ServerList.SelectionMode = Kind == WidgetKind.Command ? SelectionMode.Single : SelectionMode.Multiple | SelectionMode.Toggle;
        ServersLabel.Text = L.T(Kind == WidgetKind.Command
            ? "На каком сервере выполнять"
            : "Какие серверы входят в выборку (ничего не выбрано: все VM и контейнеры)");
        StatusText.Text = "";
    }

    void Create_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var keys = ServerList.SelectedItems?.OfType<ResourceRow>().Select(r => r.Key).ToList() ?? [];
        if (Kind == WidgetKind.Command)
        {
            if (keys.Count != 1)
            {
                StatusText.Text = L.T("Выберите один сервер");
                return;
            }
            if (string.IsNullOrWhiteSpace(CommandBox.Text))
            {
                StatusText.Text = L.T("Введите команду");
                return;
            }
        }
        if (Kind == WidgetKind.Group && keys.Count == 0)
        {
            StatusText.Text = L.T("Выберите серверы для группы");
            return;
        }

        var title = TitleBox.Text?.Trim();
        _vm.AddWidget(new WidgetConfig
        {
            Kind = Kind,
            Title = title ?? "", // empty: the kind's name, shown in whatever language is on
            Keys = keys,
            Metric = Metrics[Math.Max(0, MetricBox.SelectedIndex)].Id,
            Count = (int)(CountBox.Value ?? 3),
            Command = CommandBox.Text?.Trim() ?? "",
        });
        TitleBox.Text = CommandBox.Text = "";
        ServerList.SelectedItems?.Clear();
        ShowWidgets();
    }

    void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not WidgetVm w || _vm is null) return;
        _vm.RemoveWidget(w);
        ShowWidgets();
    }

    void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
