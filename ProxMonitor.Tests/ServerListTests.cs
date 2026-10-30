using ProxMonitor.Core;

namespace ProxMonitor.Tests;

public class ServerListTests
{
    [Fact]
    public void Export_keeps_secrets_out_and_round_trips()
    {
        var cfg = new ServerConfig
        {
            Name = "Офис", Url = "https://10.0.0.5:8006", TokenId = "root@pam!monitor", SshUser = "root",
            ProtectedSecret = "SECRET", ProtectedSshPassword = "PASSWORD", PinnedCert = "AB12",
        };
        var json = ServerList.Export([cfg]);

        Assert.DoesNotContain("SECRET", json);
        Assert.DoesNotContain("PASSWORD", json);
        Assert.DoesNotContain("AB12", json); // a pinned cert from a file would let that file vouch for a server
        Assert.Equal(new ServerEntry("Офис", "https://10.0.0.5:8006", "root@pam!monitor", "root"), Assert.Single(ServerList.Import(json)));
    }

    [Fact]
    public void Import_drops_invalid_entries()
    {
        var json = """
            {"format":"proxmonitor-servers","version":1,"servers":[
              {"name":"ok","url":"https://pve:8006","tokenId":"a@pam!b"},
              {"name":"http","url":"http://pve:8006","tokenId":"a@pam!b"},
              {"name":"bad token","url":"https://pve:8006","tokenId":"nope"},
              {"url":"https://pve:8006","tokenId":"a@pam!b"}
            ]}
            """;
        Assert.Equal("ok", Assert.Single(ServerList.Import(json)).Name);
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("""{"format":"other","servers":[]}""")]
    public void Import_rejects_foreign_files(string json) =>
        Assert.Throws<FormatException>(() => ServerList.Import(json));

    [Fact]
    public void Ss_output_is_parsed()
    {
        var list = NetConnections.Parse("""
            tcp   0      0      10.0.0.5:8006   10.0.0.9:51234 users:(("pveproxy worker",pid=1234,fd=12))
            udp   0      0      10.0.0.5:123    10.0.0.1:123

            """);
        Assert.Equal(new NetConnection("tcp", "10.0.0.5:8006", "10.0.0.9:51234", "pveproxy worker"), list[0]);
        Assert.Equal("", list[1].Process);
    }

    [Fact]
    public async Task Demo_terminal_answers_ss()
    {
        using var t = new DemoTerminal("pve-01");
        Assert.NotEmpty(NetConnections.Parse((await t.RunAsync(NetConnections.Command, default)).Output));
    }
}
