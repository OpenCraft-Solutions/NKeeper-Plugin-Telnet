using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Abstractions;

namespace NKeeper.Plugins.Telnet;

/// <summary>
/// The Telnet plugin (docs/architecture.md §7.23 in NKeeper): the <c>telnet</c> service type, and terminal sessions on
/// it. Core opens the connection, speaks Telnet and signs in at the server's prompts (§7.22); this plugin only carries
/// the terminal. Ported from 1.x's <c>Telnet/</c>.
/// </summary>
public sealed class TelnetPlugin : IPlugin
{
    public const string ServiceTypeId = "telnet";

    public void Contribute(IPluginContributions contributions, PluginContext context)
    {
        contributions.AddServiceType(new ServiceType(ServiceTypeId, "Telnet (unencrypted)") { DefaultPort = 23, Health = HealthProbe.Tcp });
        contributions.AddSessionType(new SessionType(ServiceTypeId, "Telnet", typeof(TerminalSessionHandler)) { ServiceTypes = [ServiceTypeId] });
        // Core signs in with the credential; without one, the user types at the server's prompts.
        contributions.AddHostAction(new HostAction("telnet.terminal", "Telnet", SessionType: ServiceTypeId));
        contributions.AddHostAction(new HostAction("telnet.typed", "Telnet (sign in yourself)", SessionType: ServiceTypeId) { Credential = HostActionCredential.None });
    }

    public void ConfigureServices(IServiceCollection services, PluginContext context) =>
        services.AddScoped<TerminalSessionHandler>();

    public void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context)
    {
    }
}

/// <summary>
/// Opens a terminal over Telnet, of the size the browser asks for. With a credential, core signs in at the server's
/// prompts; without one, the user does, in the terminal.
/// </summary>
public sealed class TerminalSessionHandler(IConnectivity connectivity) : IStreamSessionHandler
{
    public const int MinSize = 1;
    public const int MaxSize = 1000;

    public async Task<IStreamSession> OpenAsync(StreamSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var size = new TerminalSize(
            Columns: Size(request.Options, "columns", 80),
            Rows: Size(request.Options, "rows", 24));
        return new TerminalSession(await connectivity.OpenTelnetAsync(request.Host, request.ServiceId, request.Credential, size, cancellationToken));
    }

    /// <summary>A terminal dimension from the options, kept within bounds.</summary>
    internal static int Size(JsonElement options, string name, int fallback) =>
        options.ValueKind == JsonValueKind.Object && options.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var size)
            ? Math.Clamp(size, MinSize, MaxSize)
            : fallback;
}

/// <summary>
/// A terminal: binary frames carry its bytes both ways, and a text frame <c>{"type":"resize","columns":…,"rows":…}</c>
/// resizes it. It ends when the server closes the connection or the browser goes.
/// </summary>
internal sealed class TerminalSession(ITelnetConnection telnet) : IStreamSession
{
    public async Task RunAsync(IStreamChannel channel, CancellationToken cancellationToken)
    {
        using var done = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // A connection's reads may not heed cancellation; closing it ends them.
        using var close = done.Token.Register(() => telnet.Stream.Dispose());
        var output = PumpOutputAsync(channel, done.Token);
        var input = PumpInputAsync(channel, done.Token);

        // Whichever side finishes first (the server closed, or the browser went) ends the other.
        await Task.WhenAny(output, input);
        await done.CancelAsync();
        await Quietly(output);
        await Quietly(input);
    }

    public ValueTask DisposeAsync() => telnet.DisposeAsync();

    private async Task PumpOutputAsync(IStreamChannel channel, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await telnet.Stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await channel.SendAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private async Task PumpInputAsync(IStreamChannel channel, CancellationToken cancellationToken)
    {
        while (await channel.ReceiveAsync(cancellationToken) is { } frame)
        {
            if (!frame.IsText)
            {
                await telnet.Stream.WriteAsync(frame.Data, cancellationToken);
                await telnet.Stream.FlushAsync(cancellationToken);
            }
            else if (Resize(frame.Data.Span) is var (columns, rows))
            {
                telnet.Resize(columns, rows);
            }
        }
    }

    /// <summary>The size a <c>resize</c> message asks for; null for any other message.</summary>
    internal static (int Columns, int Rows)? Resize(ReadOnlySpan<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "resize"
                ? (TerminalSessionHandler.Size(root, "columns", 80), TerminalSessionHandler.Size(root, "rows", 24))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
        {
            // The session is ending.
        }
    }
}
