using Avalonia.Controls;
using Avalonia.Interactivity;
using Lynxmote.Core;

namespace Lynxmote.App;

/// <summary>
/// Saves an SSH login for a node or a VM without opening its terminal. After one successful login
/// the app reads that machine by itself on every start.
/// </summary>
public partial class LoginWindow : Window
{
    readonly ResourceRow? _row;
    readonly MainViewModel? _vm;
    readonly ProxmoxSource? _source;
    readonly ServerConfig? _cfg;
    string? _host, _seenHostKey;

    public LoginWindow()
    {
        InitializeComponent();
        this.UseOwnTitleBar(this, captionButtons: false);
    }

    public LoginWindow(ResourceRow row, MainViewModel vm, ProxmoxSource source, ServerConfig cfg) : this()
    {
        (_row, _vm, _source, _cfg) = (row, vm, source, cfg);
        if (SshLogin.HasKeys) PasswordBox.Watermark = L.T("Пароль (пусто: вход по SSH-ключу)");
        if (row.IsNode)
        {
            UserBox.Text = cfg.SshUser ?? "root";
            Hint.Text = L.F("Нода {0}. Войти нужно один раз: дальше приложение само читает температуру, сеть и подключения.", row.Node);
        }
        else
        {
            var saved = cfg.GuestLogins.GetValueOrDefault(row.LocalId);
            AddressBox.IsVisible = true;
            AddressBox.Text = saved?.Host ?? source.GuessAddress(row.LocalId);
            UserBox.Text = saved?.User ?? "root";
            Hint.Text = L.F("В {0} нет qemu-guest-agent. Войти нужно один раз: дальше приложение само читает её диск и адрес по SSH.", row.Name);
        }
        // The node's password is never offered to another machine, so the box starts empty.
        Opened += (_, _) => (AddressBox.IsVisible && string.IsNullOrEmpty(AddressBox.Text) ? AddressBox : PasswordBox).Focus();
    }

    void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    async void Connect_Click(object? sender, RoutedEventArgs e)
    {
        if (_row is null || _vm is null || _source is null || _cfg is null) return;
        var user = UserBox.Text?.Trim() ?? "";
        var password = PasswordBox.Text ?? "";

        ConnectButton.IsEnabled = false;
        StatusText.Text = L.T("Подключение…");
        var host = _row.IsNode ? await _source.NodeAddressAsync(_row.Node, default) : AddressBox.Text?.Trim() ?? "";
        if (host != _host)
        {
            _host = host;
            _seenHostKey = null; // a fingerprint belongs to the address it came from
            HostKeyPanel.IsVisible = false;
        }
        if (TrustBox.IsChecked == true && _seenHostKey is not null) _cfg.SshHostKeys[host] = _seenHostKey;

        var result = await SshTerminal.ConnectAsync(host, user, password, _cfg.SshHostKeys.GetValueOrDefault(host),
            host, TerminalTarget.Node, 0, default);
        ConnectButton.IsEnabled = true;

        if (result.Terminal is { } terminal)
        {
            terminal.Dispose();
            if (_row.IsNode) (_cfg.SshUser, _cfg.ProtectedSshPassword) = (user, Secrets.ProtectPassword(password));
            else _cfg.GuestLogins[_row.LocalId] = new(host, user, Secrets.ProtectPassword(password));
            _vm.SaveSettings();
            _source.Relogin(_row.LocalId);
            Close();
        }
        else if (result.UnknownHostKey is { } key)
        {
            var changed = _cfg.SshHostKeys.ContainsKey(host);
            _seenHostKey = key;
            HostKeyText.Text = "SHA256:" + key;
            TrustBox.IsChecked = false;
            HostKeyPanel.IsVisible = true;
            StatusText.Text = changed
                ? L.T("Ключ сервера изменился! Если вы не переустанавливали сервер, не подключайтесь.")
                : L.T("Подтвердите отпечаток и нажмите «Подключиться» ещё раз");
        }
        else
        {
            StatusText.Text = result.Error;
        }
    }
}
