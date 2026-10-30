using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProxMonitor.Core;

namespace ProxMonitor.Tests;

/// <summary>HTTPS server with a self-signed cert that answers like the Proxmox VE API.</summary>
sealed class FakeProxmox : IAsyncDisposable
{
    public const string TokenId = "root@pam!monitor";
    public const string Secret = "11111111-2222-3333-4444-555555555555";

    // Trimmed from a real /cluster/resources answer: a template and a storage entry must be skipped.
    const string Resources = """
        {"data":[
          {"id":"node/pve","type":"node","node":"pve","status":"online","cpu":0.12,"maxcpu":8,"mem":4294967296,"maxmem":17179869184,"disk":10737418240,"maxdisk":107374182400,"uptime":3600,"level":""},
          {"id":"qemu/100","type":"qemu","vmid":100,"name":"web","node":"pve","status":"running","cpu":0.5,"maxcpu":2,"mem":1073741824,"maxmem":2147483648,"disk":0,"maxdisk":34359738368,"netin":1000,"netout":500,"uptime":120,"template":0},
          {"id":"qemu/9000","type":"qemu","vmid":9000,"name":"tpl","node":"pve","status":"stopped","cpu":0,"mem":0,"maxmem":2147483648,"disk":0,"maxdisk":34359738368,"uptime":0,"template":1},
          {"id":"lxc/101","type":"lxc","vmid":101,"name":"dns","node":"pve","status":"stopped","cpu":0,"mem":0,"maxmem":536870912,"disk":0,"maxdisk":8589934592,"uptime":0},
          {"id":"storage/pve/local","type":"storage","node":"pve","storage":"local","status":"available"}
        ]}
        """;

    readonly WebApplication _app;
    public X509Certificate2 Cert { get; }
    public string Url { get; }
    /// <summary>POSTs the server got, as "node/type/vmid/action[?snapname]".</summary>
    public IReadOnlyList<string> Received { get; }

    FakeProxmox(WebApplication app, X509Certificate2 cert, string url, List<string> received) =>
        (_app, Cert, Url, Received) = (app, cert, url, received);

    public static async Task<FakeProxmox> StartAsync()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=pve", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        var cert = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null); // SChannel needs a persisted key

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.UseHttps(cert)));
        var app = builder.Build();
        var posts = new List<string>();

        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Headers.Authorization != $"PVEAPIToken={TokenId}={Secret}")
            {
                ctx.Response.StatusCode = 401;
                return;
            }
            await next();
        });
        app.MapGet("/api2/json/cluster/resources", () => Results.Text(Resources, "application/json"));
        app.MapGet("/api2/json/cluster/status", () => Results.Text(
            """{"data":[{"type":"cluster","name":"lab"},{"type":"node","name":"pve","ip":"10.0.0.5","online":1}]}""", "application/json"));
        app.MapGet("/api2/json/nodes/pve/lxc/101/interfaces", () => Results.Text(
            """{"data":[{"name":"lo","inet":"127.0.0.1/8"},{"name":"eth0","inet":"10.0.0.21/24","hwaddr":"bc:24:11:00:00:01"}]}""", "application/json"));
        app.MapGet("/api2/json/nodes/pve/qemu/100/agent/network-get-interfaces", () => Results.Text(
            """{"data":{"result":[{"name":"lo","ip-addresses":[{"ip-address":"127.0.0.1","ip-address-type":"ipv4"}]},{"name":"ens18","ip-addresses":[{"ip-address":"10.0.0.20","ip-address-type":"ipv4","prefix":24},{"ip-address":"fe80::1","ip-address-type":"ipv6"}]}]}}""", "application/json"));
        app.MapPost("/api2/json/nodes/{node}/{type}/{vmid}/{**action}", async (HttpContext ctx, string node, string type, string vmid, string action) =>
        {
            var form = ctx.Request.HasFormContentType ? await ctx.Request.ReadFormAsync() : null;
            posts.Add($"{node}/{type}/{vmid}/{action}{(form?["snapname"] is { Count: > 0 } s ? "?" + s : "")}");
            return Results.Json(new { data = "UPID:pve:00001234:task" });
        });

        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new FakeProxmox(app, cert, url, posts);
    }

    public ServerConfig Config(bool pinned) => new()
    {
        Name = "fake", Url = Url, TokenId = TokenId,
        PinnedCert = pinned ? Cert.GetCertHashString(HashAlgorithmName.SHA256) : null,
    };

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        Cert.Dispose();
    }
}

public class ProxmoxSourceTests
{
    [Fact]
    public async Task Unknown_self_signed_cert_is_reported_with_its_fingerprint()
    {
        await using var pve = await FakeProxmox.StartAsync();
        var result = await ProxmoxSource.TestAsync(pve.Config(pinned: false), FakeProxmox.Secret);
        Assert.False(result.Ok);
        Assert.Equal(pve.Cert.GetCertHashString(HashAlgorithmName.SHA256), result.UntrustedCert);
    }

    [Fact]
    public async Task Pinned_cert_and_valid_token_connect()
    {
        await using var pve = await FakeProxmox.StartAsync();
        Assert.True((await ProxmoxSource.TestAsync(pve.Config(pinned: true), FakeProxmox.Secret)).Ok);
    }

    [Fact]
    public async Task Wrong_token_is_explained()
    {
        await using var pve = await FakeProxmox.StartAsync();
        var result = await ProxmoxSource.TestAsync(pve.Config(pinned: true), "wrong");
        Assert.Equal("Неверный токен", result.Message);
    }

    [Fact]
    public async Task Resources_are_parsed_and_templates_skipped()
    {
        await using var pve = await FakeProxmox.StartAsync();
        using var source = new ProxmoxSource(pve.Config(pinned: true), FakeProxmox.Secret);
        var list = await source.FetchAsync(default);

        Assert.Equal(["node/pve", "qemu/100", "lxc/101"], list.Select(r => r.Id));
        var vm = list[1];
        Assert.Equal("100 · web", vm.Name);
        Assert.True(vm.Running);
        Assert.Equal(0.5, vm.Cpu);
        Assert.Equal(ResourceKind.Container, list[2].Kind);
        Assert.False(list[2].Running);
    }

    [Fact]
    public async Task Addresses_come_from_cluster_status_lxc_interfaces_and_guest_agent()
    {
        await using var pve = await FakeProxmox.StartAsync();
        using var source = new ProxmoxSource(pve.Config(pinned: true), FakeProxmox.Secret);

        Assert.Equal("10.0.0.5", await source.AddressAsync("node/pve", "pve", default));
        Assert.Equal("10.0.0.21", await source.AddressAsync("lxc/101", "pve", default));
        Assert.Equal("10.0.0.20", await source.AddressAsync("qemu/100", "pve", default));
        Assert.Null(await source.AddressAsync("qemu/999", "pve", default)); // no guest agent answer
    }

    [Fact]
    public async Task Actions_post_to_the_right_endpoints()
    {
        await using var pve = await FakeProxmox.StartAsync();
        using var source = new ProxmoxSource(pve.Config(pinned: true), FakeProxmox.Secret);
        await source.FetchAsync(default); // learns which node each guest lives on

        await source.ApplyAsync("qemu/100", PowerAction.Stop, default);
        await source.ApplyAsync("lxc/101", PowerAction.Start, default);
        await source.ApplyAsync("qemu/100", PowerAction.Snapshot, default);

        Assert.Equal("pve/qemu/100/status/stop", pve.Received[0]);
        Assert.Equal("pve/lxc/101/status/start", pve.Received[1]);
        Assert.StartsWith("pve/qemu/100/snapshot?pm", pve.Received[2]);
    }
}
