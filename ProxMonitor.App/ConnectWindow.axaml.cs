using Avalonia.Controls;
using Avalonia.Interactivity;
using ProxMonitor.Core;

namespace ProxMonitor.App;

public sealed record NewServer(ServerConfig Config, string Secret);

public partial class ConnectWindow : Window
{
    string? _seenCert, _sshUser;

    public ConnectWindow()
    {
        InitializeComponent();
        this.UseOwnTitleBar(this, captionButtons: false);
    }

    /// <summary>For an imported server: everything is filled in except the secret.</summary>
    public ConnectWindow(ServerEntry entry) : this()
    {
        (NameBox.Text, UrlBox.Text, TokenIdBox.Text, _sshUser) = (entry.Name, entry.Url, entry.TokenId, entry.SshUser);
        StatusText.Text = L.F("Вставьте секрет токена для «{0}» или нажмите «Отмена», чтобы пропустить", entry.Name);
        Opened += (_, _) => SecretBox.Focus();
    }

    void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    async void Connect_Click(object? sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text?.Trim() ?? "";
        if (!url.Contains("://")) url = "https://" + url;
        var tokenId = TokenIdBox.Text?.Trim() ?? "";
        var secret = SecretBox.Text?.Trim() ?? "";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            StatusText.Text = L.T("Укажите адрес вида https://адрес:8006");
            return;
        }
        if (!tokenId.Contains('@') || !tokenId.Contains('!'))
        {
            StatusText.Text = L.T("Token ID имеет вид пользователь@realm!имя, например root@pam!monitor");
            return;
        }
        if (secret.Length == 0)
        {
            StatusText.Text = L.T("Вставьте секрет токена");
            return;
        }

        var cfg = new ServerConfig
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? uri.Host : NameBox.Text.Trim(),
            Url = uri.GetLeftPart(UriPartial.Authority),
            TokenId = tokenId,
            PinnedCert = TrustBox.IsChecked == true ? _seenCert : null,
            SshUser = _sshUser,
        };

        ConnectButton.IsEnabled = false;
        StatusText.Text = L.T("Подключение…");
        var result = await ProxmoxSource.TestAsync(cfg, secret);
        ConnectButton.IsEnabled = true;

        if (result.Ok)
        {
            Close(new NewServer(cfg, secret));
        }
        else if (result.UntrustedCert is { } cert)
        {
            _seenCert = cert;
            CertText.Text = string.Join(":", Enumerable.Range(0, cert.Length / 2).Select(i => cert.Substring(i * 2, 2)));
            TrustBox.IsChecked = false;
            CertPanel.IsVisible = true;
            StatusText.Text = L.T("Подтвердите сертификат и нажмите «Подключить» ещё раз");
        }
        else
        {
            StatusText.Text = result.Message;
        }
    }
}
