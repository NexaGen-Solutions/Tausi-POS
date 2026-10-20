using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Security;
using Tausi.Application.Terminals;
using Tausi.Infrastructure.Persistence;

namespace Tausi.Api.Hubs;

/// <summary>
/// Live updates at /hubs/pos. Every client joins the group "business". A terminal's connection is
/// also its heartbeat (online status) and the way the server reaches it for "via terminal" printing.
/// A paired terminal may connect before anyone logs in, so its login screen updates live.
/// </summary>
[Authorize(Policy = Auth.TausiClaims.DeviceOrSessionPolicy)]
public sealed class PosHub(TerminalPresence presence, TausiDbContext db) : Hub
{
    public const string Path = "/hubs/pos";
    public const string BusinessGroup = "business";

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, BusinessGroup);
        if (Terminal is { } terminal) presence.Connected(terminal.TerminalId, Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        presence.Disconnected(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>The terminal app reports the printers installed on its PC (for "via terminal" printers).</summary>
    public async Task ReportPrinters(string[] names)
    {
        if (Terminal is not { } terminal) return;
        var clean = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct().Order().Take(100).ToList();
        await db.Terminals.Where(t => t.Id == terminal.TerminalId)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.LocalPrintersJson, JsonSerializer.Serialize(clean)));
    }

    private DeviceTerminal? Terminal => Context.GetHttpContext()?.Features.Get<DeviceTerminal>();
}
