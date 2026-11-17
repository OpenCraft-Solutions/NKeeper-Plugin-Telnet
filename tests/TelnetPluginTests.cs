using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NKeeper.Testing;

namespace NKeeper.Plugins.Telnet.Tests;

/// <summary>
/// docs/architecture.md §7.23 in NKeeper: the real <c>telnet</c> plugin, packaged by its build and loaded like any
/// plugin into NKeeper's test server (NKeeper.Testing), opening terminals on a fake network device through core's
/// Telnet connections and stream sessions. Ported from 1.x's <c>TelnetSessionTests</c>.
/// </summary>
public sealed class TelnetPluginTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private readonly FakeTelnetServer _device = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _device.DisposeAsync();

    [Fact]
    public async Task The_plugin_contributes_its_service_and_session_types_and_two_actions()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync(Token);
        var plugins = await admin.Http.GetFromJsonAsync<JsonElement>(new Uri("/api/plugins", UriKind.Relative), Token);
        var plugin = plugins.EnumerateArray().Single(p => p.GetProperty("id").GetString() == "telnet");
        Assert.Equal("loaded", plugin.GetProperty("state").GetString());
        var serviceType = Assert.Single(plugin.GetProperty("serviceTypes").EnumerateArray());
        Assert.Equal(("Telnet (unencrypted)", 23, "tcp"), (serviceType.GetProperty("label").GetString(), serviceType.GetProperty("defaultPort").GetInt32(), serviceType.GetProperty("health").GetString()));
        Assert.Equal(
            [("telnet.terminal", "Telnet", "telnet", "required"), ("telnet.typed", "Telnet (sign in yourself)", "telnet", "none")],
            plugin.GetProperty("hostActions").EnumerateArray().Select(a => (
                a.GetProperty("id").GetString(),
                a.GetProperty("label").GetString(),
                a.GetProperty("sessionType").GetString(),
                a.GetProperty("credential").GetString())).ToArray());

        // Its frontend, the terminal view, is served from the verified package.
        var entry = plugin.GetProperty("frontendEntry").GetString()!;
        using var module = await admin.Http.GetAsync(new Uri(entry, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, module.StatusCode);
        Assert.Contains("@nkeeper/sdk", await module.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        var sessionType = Assert.Single(plugin.GetProperty("sessionTypes").EnumerateArray());
        Assert.Equal(("telnet", "Telnet"), (sessionType.GetProperty("id").GetString(), sessionType.GetProperty("label").GetString()));
    }

    [Fact]
    public async Task With_a_credential_core_signs_in_and_the_terminal_resizes_and_ends_when_the_server_closes()
    {
        await using var server = await ServerAsync();
        var (client, cookie) = await SignInAsync(server);
        using var _ = client;
        var host = await CreateHostAsync(client, withCredential: true);

        await using var terminal = await SessionClient.ConnectAsync(server, cookie, "telnet");
        await terminal.SendAsync(new
        {
            type = "start",
            hostId = host.GetProperty("id").GetGuid(),
            serviceId = host.GetProperty("services")[0].GetProperty("id").GetGuid(),
            credentialId = host.GetProperty("credentials")[0].GetProperty("id").GetGuid(),
            options = new { columns = 100, rows = 30 },
        });
        await terminal.ExpectAsync("connected");
        await terminal.ReadUntilAsync($"Welcome {FakeTelnetServer.UserName}");

        // A resize with dimensions of the wrong kind falls back, rather than ending the terminal.
        await terminal.SendAsync(new { type = "resize", columns = "120", rows = true });
        await terminal.SendAsync(new { type = "resize", columns = 120, rows = 40 });
        await terminal.SendBinaryAsync(Encoding.UTF8.GetBytes("show version\r"));
        await terminal.ReadUntilAsync("you said: show version");
        Assert.Equal((120, 40), _device.WindowSizes.Last());
        Assert.Equal("XTERM-256COLOR", _device.TerminalType);

        await terminal.SendBinaryAsync(Encoding.UTF8.GetBytes("exit\r"));
        Assert.Equal("closed", (await terminal.ExpectAsync("closed")).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Without_a_credential_the_user_signs_in_in_the_terminal()
    {
        await using var server = await ServerAsync();
        var (client, cookie) = await SignInAsync(server);
        using var _ = client;
        var host = await CreateHostAsync(client, withCredential: false);

        await using var terminal = await SessionClient.ConnectAsync(server, cookie, "telnet");
        await terminal.SendAsync(new
        {
            type = "start",
            hostId = host.GetProperty("id").GetGuid(),
            serviceId = host.GetProperty("services")[0].GetProperty("id").GetGuid(),
            options = new { columns = 80, rows = 24 },
        });
        await terminal.ExpectAsync("connected");
        await terminal.ReadUntilAsync("login: ");
        await terminal.SendBinaryAsync(Encoding.UTF8.GetBytes(FakeTelnetServer.UserName + "\r"));
        await terminal.ReadUntilAsync("Password: ");
        await terminal.SendBinaryAsync(Encoding.UTF8.GetBytes(FakeTelnetServer.Password + "\r"));
        await terminal.ReadUntilAsync($"Welcome {FakeTelnetServer.UserName}");
    }

    private static async Task<(TestServerClient Client, string Cookie)> SignInAsync(TestServer server)
    {
        var client = server.CreateClient();
        using var login = await client.SendAsync(HttpMethod.Post, "/api/auth/login", new { userNameOrEmail = TestServer.AdminUserName, password = TestServer.AdminPassword, rememberMe = false }, Token);
        login.EnsureSuccessStatusCode();
        return (client, string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])));
    }

    private async Task<JsonElement> CreateHostAsync(TestServerClient client, bool withCredential)
    {
        using var created = await client.SendAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = $"switch-{Guid.NewGuid():N}",
            ipAddress = "127.0.0.1",
            services = new[] { new { type = "telnet", port = _device.Port, settings = new { } } },
            credentials = withCredential ? new[] { new { username = FakeTelnetServer.UserName, password = FakeTelnetServer.Password } } : [],
        }, Token);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Token));
        return await created.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private async Task<TestServer> ServerAsync()
    {
        var options = new TestServerOptions { ConnectionString = await postgres.CreateDatabaseAsync() };
        foreach (var package in DevelopmentKeys.BuiltPackages())
        {
            options.PluginPackages.Add(package);
        }

        return await TestServer.StartAsync(options, Token);
    }
}
