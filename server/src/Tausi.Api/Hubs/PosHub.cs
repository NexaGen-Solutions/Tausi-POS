using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Tausi.Application.Security;

namespace Tausi.Api.Hubs;

/// <summary>
/// Live updates at /hubs/pos. Every client joins the group "business"; terminals also join their
/// own group so the server can send them print jobs for a printer plugged into them.
/// A paired terminal may connect before anyone logs in, so its login screen updates live.
/// </summary>
[Authorize(Policy = Tausi.Api.Auth.TausiClaims.DeviceOrSessionPolicy)]
public sealed class PosHub(TerminalConnections terminals) : Hub
{
    public const string Path = "/hubs/pos";
    public const string BusinessGroup = "business";

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, BusinessGroup);
        if (Context.GetHttpContext()?.Features.Get<DeviceTerminal>() is { } terminal)
            terminals.Add(terminal.TerminalId, Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        terminals.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
