using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ProxMonitor.Core;

namespace ProxMonitor.App;

/// <summary>One command and its answer, shown as a card.</summary>
public partial class TerminalBlock(string command) : ObservableObject
{
    public static readonly FuncValueConverter<bool, IBrush> FooterBrush =
        new(failed => new SolidColorBrush(Color.Parse(failed ? "#F87171" : "#6B7280")));

    public string Command { get; } = command;
    [ObservableProperty] private string _output = "";
    [ObservableProperty] private string _footer = L.T("выполняется…");
    [ObservableProperty] private bool _failed;
}

public partial class TerminalWindow : Window
{
    readonly ResourceRow? _row;
    readonly MainViewModel? _vm;
    readonly ObservableCollection<TerminalBlock> _blocks = [];
    readonly List<string> _history = [];
    ITerminal? _terminal;
    CancellationTokenSource? _running;
    int _historyPos;
    string? _host, _seenHostKey;

    public TerminalWindow()
    {
        InitializeComponent();
        this.UseOwnTitleBar(TitleBar, captionButtons: true);
    }

    public TerminalWindow(ResourceRow row, MainViewModel vm) : this()
    {
        (_row, _vm) = (row, vm);
        Title = L.F("Терминал: {0}", row.Name);
        TargetText.Text = row.Name;
        Blocks.ItemsSource = _blocks;
        string[] common = ["uptime", "df -h", "free -h", "ip -br a", "systemctl --failed", "journalctl -n 30 --no-pager"];
        Chips.ItemsSource = row.IsNode ? ["pveversion", "qm list", "pct list", .. common] : common;
        Opened += async (_, _) => await StartAsync();
        Closed += (_, _) =>
        {
            _running?.Cancel();
            _terminal?.Dispose();
        };
    }

    static (TerminalTarget Target, int Vmid) TargetOf(ResourceRow r) => r.IsNode
        ? (TerminalTarget.Node, 0)
        : (r.LocalId.StartsWith("lxc/") ? TerminalTarget.Container : TerminalTarget.Vm, int.Parse(r.LocalId.Split('/')[1]));

    static string LabelOf(ResourceRow r) => r.IsNode ? r.Node : r.Name.Split(' ').Last();

    /// <summary>A terminal without asking anything: demo, or SSH with a saved login and an already confirmed host key.</summary>
    public static async Task<(ITerminal? Terminal, string? Problem)> ConnectSavedAsync(ResourceRow row, MainViewModel vm)
    {
        if (vm.SourceOf(row) is DemoSource) return (new DemoTerminal(LabelOf(row)), null);
        if (vm.SourceOf(row) is not ProxmoxSource source || vm.ConfigOf(row) is not { } cfg) return (null, L.T("Сервер не подключён"));
        var firstLogin = L.T("Нужен вход по SSH: откройте «Терминал» один раз, чтобы сохранить его");
        if (cfg.SshUser is not { } user || Secrets.Unprotect(cfg.ProtectedSshPassword) is not { } password) return (null, firstLogin);
        var host = await source.NodeAddressAsync(row.Node, default);
        if (!cfg.SshHostKeys.TryGetValue(host, out var key)) return (null, firstLogin);
        var (target, vmid) = TargetOf(row);
        var r = await SshTerminal.ConnectAsync(host, user, password, key, LabelOf(row), target, vmid, default);
        return (r.Terminal, r.Terminal is null ? r.Error ?? L.T("Ключ сервера изменился, откройте «Терминал»") : null);
    }

    async Task StartAsync()
    {
        if (_vm!.SourceOf(_row!) is DemoSource)
        {
            Connected(new DemoTerminal(LabelOf(_row!)), L.T("демо-режим"));
            return;
        }
        if (_vm.SourceOf(_row!) is not ProxmoxSource source || _vm.ConfigOf(_row!) is not { } cfg)
        {
            StatusText.Text = L.T("сервер не подключён");
            return;
        }
        StatusText.Text = L.T("поиск адреса ноды…");
        _host = await source.NodeAddressAsync(_row!.Node, default);
        LoginHint.Text = L.F(_row.IsNode ? "Команды выполняются на ноде {0} ({1})."
            : _row.LocalId.StartsWith("lxc/") ? "Команды выполняются на ноде {0} ({1}) внутри контейнера через pct exec."
            : "Команды выполняются на ноде {0} ({1}) внутри VM через qemu-guest-agent.", _row.Node, _host);
        if (cfg.SshUser is { } user && Secrets.Unprotect(cfg.ProtectedSshPassword) is { } password)
        {
            UserBox.Text = user;
            PasswordBox.Text = password;
            await ConnectAsync(cfg);
        }
        else
        {
            ShowLogin();
        }
    }

    async Task ConnectAsync(ServerConfig cfg)
    {
        var user = UserBox.Text?.Trim() ?? "";
        var password = PasswordBox.Text ?? "";
        if (TrustBox.IsChecked == true && _seenHostKey is not null) cfg.SshHostKeys[_host!] = _seenHostKey;

        LoginButton.IsEnabled = false;
        StatusText.Text = L.T("подключение…");
        var (target, vmid) = TargetOf(_row!);
        var result = await SshTerminal.ConnectAsync(_host!, user, password, cfg.SshHostKeys.GetValueOrDefault(_host!),
            LabelOf(_row!), target, vmid, default);
        LoginButton.IsEnabled = true;

        if (result.Terminal is { } terminal)
        {
            cfg.SshUser = user;
            cfg.ProtectedSshPassword = Secrets.Protect(password);
            _vm!.SaveSettings();
            Connected(terminal, $"ssh {user}@{_host}");
            return;
        }
        ShowLogin();
        if (result.UnknownHostKey is { } key)
        {
            _seenHostKey = key;
            HostKeyText.Text = "SHA256:" + key;
            TrustBox.IsChecked = false;
            HostKeyPanel.IsVisible = true;
            LoginError.Text = cfg.SshHostKeys.ContainsKey(_host!)
                ? L.T("Ключ сервера изменился! Если вы не переустанавливали сервер, не подключайтесь.")
                : L.T("Подтвердите отпечаток и нажмите «Подключиться» ещё раз");
        }
        else
        {
            LoginError.Text = result.Error;
        }
    }

    void ShowLogin()
    {
        LoginPanel.IsVisible = true;
        StatusText.Text = L.T("нужен вход");
        PasswordBox.Focus();
    }

    void Connected(ITerminal terminal, string status)
    {
        _terminal = terminal;
        LoginPanel.IsVisible = false;
        StatusDot.Fill = new SolidColorBrush(Color.Parse("#4ADE80"));
        StatusText.Text = status;
        PromptText.Text = terminal.Prompt;
        Input.IsEnabled = true;
        Input.Focus();
    }

    async void Login_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm?.ConfigOf(_row!) is { } cfg) await ConnectAsync(cfg);
    }

    void Password_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Login_Click(sender, e);
    }

    // ---- running commands ----

    async Task RunAsync(string command)
    {
        command = command.Trim();
        if (_terminal is null || _running is not null || command.Length == 0) return;
        if (command == "clear")
        {
            _blocks.Clear();
            return;
        }
        if (_history.LastOrDefault() != command) _history.Add(command);
        _historyPos = _history.Count;

        var block = new TerminalBlock(command);
        _blocks.Add(block);
        Input.Text = "";
        StopButton.IsVisible = true;
        _running = new CancellationTokenSource();
        ScrollDown();
        try
        {
            var r = await _terminal.RunAsync(command, _running.Token);
            block.Output = r.Output;
            block.Failed = r.ExitCode != 0;
            block.Footer = L.F(r.ExitCode == 0 ? "готово · {1:0.00} с" : "код {0} · {1:0.00} с", r.ExitCode, r.Duration.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            (block.Footer, block.Failed) = (L.T("прервано"), true);
        }
        catch (Exception e)
        {
            (block.Output, block.Footer, block.Failed) = (e.Message, L.T("ошибка"), true);
        }
        finally
        {
            _running.Dispose();
            _running = null;
            StopButton.IsVisible = false;
            PromptText.Text = _terminal.Prompt;
            Input.Focus();
            ScrollDown();
        }
    }

    void ScrollDown() => Avalonia.Threading.Dispatcher.UIThread.Post(Scroll.ScrollToEnd, Avalonia.Threading.DispatcherPriority.Background);

    async void Input_KeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await RunAsync(Input.Text ?? "");
                break;
            case Key.Up when _history.Count > 0:
                e.Handled = true;
                _historyPos = Math.Max(0, _historyPos - 1);
                SetInput(_history[_historyPos]);
                break;
            case Key.Down when _history.Count > 0:
                e.Handled = true;
                _historyPos = Math.Min(_history.Count, _historyPos + 1);
                SetInput(_historyPos < _history.Count ? _history[_historyPos] : "");
                break;
            case Key.L when ctrl:
                e.Handled = true;
                _blocks.Clear();
                break;
            case Key.C when ctrl && _running is not null && string.IsNullOrEmpty(Input.SelectedText):
                e.Handled = true;
                _running.Cancel();
                break;
        }
    }

    void SetInput(string text)
    {
        Input.Text = text;
        Input.CaretIndex = text.Length;
    }

    async void Chip_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Content is string command) await RunAsync(command);
    }

    void Clear_Click(object? sender, RoutedEventArgs e) => _blocks.Clear();

    void Stop_Click(object? sender, RoutedEventArgs e) => _running?.Cancel();
}
