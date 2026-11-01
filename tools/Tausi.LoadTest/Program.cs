// A busy Friday at a four-counter bar, against a running Tausi Server (task 16, Part B):
//   4 terminals, 12 waiters, 2 cashiers and a supervisor; each terminal opens 3 tickets a (simulated)
//   minute, each with 3 rounds of 2–5 items and a Print each round; 3% of tickets void an item by a
//   request the supervisor approves; every ticket is settled (cash, M-Pesa or both).
// It uses the real HTTP and SignalR APIs, then checks the database agrees with what it did.
//
//   tausi-loadtest --server http://localhost:5080 --admin-password ... --seed-output seed.txt \
//                  --provider Postgres --db "Host=...;Database=..." [--hours 6] [--speed 3] [--out result.json]
//
// The server must run in Development with the demo seed (`seed --demo`): File printers, and the demo
// staff's PINs in the seed's output. Exit code 0 when every target is met and nothing differs.
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR.Client;
using Tausi.LoadTest;

var options = Options.Parse(args);
var stats = new Stats();
// Shared by every terminal's tasks at once: an ordinary Random isn't thread-safe and got stuck returning 0.
var random = Random.Shared;
Console.WriteLine($"Load test: {options.Hours} h at {options.Speed}× ({options.Hours / options.Speed * 60:0} min), {options.Server}");

// ---- Setup: the admin, four paired terminals and an observer, 12 waiters, shifts ----
var admin = new Api(options.Server);
await admin.Post("/api/admin/login", new { userName = "admin", password = options.AdminPassword });
var pins = Regex.Matches(File.ReadAllText(options.SeedOutput), @"^\s+(\S+)\s+\S+\s+(\d{4,6})\s*$", RegexOptions.Multiline)
    .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
var roles = (await admin.Get("/api/admin/permissions")).GetProperty("roles").EnumerateArray()
    .ToDictionary(r => r.GetProperty("name").GetString()!, r => r.GetProperty("id").GetGuid());
var terminalIds = (await admin.Get("/api/admin/terminals")).EnumerateArray()
    .ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("id").GetGuid());

async Task<Api> Pair(Guid? terminalId, string? newName)
{
    var device = new Api(options.Server);
    var started = await device.Post("/api/terminals/pairing");
    await admin.Post("/api/admin/terminals/pair", new
    {
        code = started.GetProperty("code").GetString(), terminalId, newTerminalName = newName, confirmReplace = true,
    });
    var claim = await device.Post("/api/terminals/pairing/claim", new
    {
        pairingId = started.GetProperty("pairingId").GetGuid(), secret = started.GetProperty("secret").GetString(),
    });
    return new Api(options.Server, claim.GetProperty("deviceToken").GetString());
}

string[] counters = ["Counter A", "Counter B", "Pillar 1", "Pillar 2"];
var terminals = new List<Api>();
foreach (var name in counters) terminals.Add(await Pair(terminalIds[name], null));
var observer = await Pair(null, $"Observer {DateTime.UtcNow:HHmmss}");

// 12 waiters: the demo's 4 and 8 more, each with their own PIN (chosen at first sign-in, as staff do).
var used = new HashSet<string>(pins.Values);
string NewPin()
{
    while (true)
    {
        var pin = random.Next(1000, 10000).ToString();
        // The server refuses runs up or down (1234, 9876).
        var steps = pin.Zip(pin.Skip(1), (x, y) => y - x).Distinct().ToArray();
        if (steps is [1] or [-1]) continue;
        if (used.Add(pin) && pin.Distinct().Count() > 2) return pin;
    }
}
var waiters = new List<string> { "edna", "CYNTHIA", "Ruth", "WAMBOO" };
for (var i = 1; waiters.Count < options.Waiters; i++)
{
    var name = $"Load waiter {i:00}";
    var start = NewPin();
    await admin.Post("/api/admin/staff", new { name, roleId = roles["Waiter"], startingPin = start, adminPassword = (string?)null });
    await terminals[0].SignIn(name, start);
    var chosen = NewPin();
    await terminals[0].Post("/api/auth/pin/change", new { newPin = chosen });
    pins[name] = chosen;
    waiters.Add(name);
}
var cashierB = "Load cashier";
{
    var start = NewPin();
    await admin.Post("/api/admin/staff", new { name = cashierB, roleId = roles["Cashier"], startingPin = start, adminPassword = (string?)null });
    await terminals[1].SignIn(cashierB, start);
    var chosen = NewPin();
    await terminals[1].Post("/api/auth/pin/change", new { newPin = chosen });
    pins[cashierB] = chosen;
}
// Cashiers: CAROL on Counter A, the second on Counter B. The supervisor works on Pillar 2.
string[] cashierOf = ["CAROL", cashierB, "CAROL", cashierB];
int[] tillOf = [0, 1, 0, 1];
await terminals[0].SignIn("CAROL", pins["CAROL"]);
await terminals[0].Post("/api/pos/shifts", new { @float = new { total = 5000 } });
await terminals[1].SignIn(cashierB, pins[cashierB]);
await terminals[1].Post("/api/pos/shifts", new { @float = new { total = 5000 } });
await observer.SignIn("SAM", pins["SAM"]);

var menu = await terminals[0].Get("/api/pos/menu");
var items = menu.GetProperty("categories").EnumerateArray().SelectMany(c => c.GetProperty("items").EnumerateArray())
    .Select(i => (Id: i.GetProperty("id").GetGuid(), Options: i.GetProperty("groups").EnumerateArray()
        .Where(g => g.GetProperty("minSelect").GetInt32() > 0)
        .Select(g => (g.GetProperty("options").EnumerateArray().FirstOrDefault(o => o.GetProperty("isDefault").GetBoolean()) is { ValueKind: JsonValueKind.Object } d
            ? d : g.GetProperty("options")[0]).GetProperty("id").GetGuid()).ToArray()))
    .ToArray();
var tableMap = (await terminals[0].Get("/api/pos/tables")).GetProperty("tables").EnumerateArray().ToArray();
var tables = tableMap.Select(t => t.GetProperty("id").GetGuid()).ToArray();
// One waiter per table (ticket.others): each waiter works their own section of the floor. A table
// someone already has a ticket on (the demo's open tickets) is theirs, or left out if they don't wait here.
var heldBy = tableMap.Where(t => t.GetProperty("tickets").GetArrayLength() > 0)
    .ToDictionary(t => t.GetProperty("id").GetGuid(), t => t.GetProperty("tickets")[0].GetProperty("openedBy").GetString());
var free = tables.Where(t => !heldBy.ContainsKey(t)).ToArray();
var sections = waiters.Select((w, i) => free.Where((_, k) => k % waiters.Count == i)
    .Concat(heldBy.Where(h => h.Value == w).Select(h => h.Key)).ToArray()).ToArray();
if (sections.Any(s => s.Length == 0)) throw new InvalidOperationException("Not enough free tables for a section per waiter.");
Console.WriteLine($"Ready: {terminals.Count} terminals, {waiters.Count} waiters, {items.Length} menu items, {tables.Length} tables.");

var before = await Database.Snapshot(options);
var printed = new PrintWatcher(options.PrintOutput, stats);
var sw = Stopwatch.StartNew();

// ---- The table map on another terminal: a change shows there within 500 ms ----
// As the screen does: "ticket.changed" names the table, and the map fetches just that table again.
// Each sample runs from the start of the action on the table to that fetch's answer.
var changes = new System.Collections.Concurrent.ConcurrentDictionary<Guid, System.Collections.Concurrent.ConcurrentQueue<long>>();
void Changing(Guid table) => changes.GetOrAdd(table, _ => new()).Enqueue(sw.ElapsedTicks);
var hub = new HubConnectionBuilder()
    .WithUrl(new Uri(options.Server, $"/hubs/pos?device_token={Uri.EscapeDataString(observer.DeviceToken!)}"))
    .WithAutomaticReconnect()
    .Build();
hub.On<JsonElement>("ticket.changed", payload =>
{
    stats.Count("map events");
    if (!payload.TryGetProperty("tableId", out var id) || id.ValueKind != JsonValueKind.String) return;
    var table = id.GetGuid();
    if (!changes.TryGetValue(table, out var waiting) || !waiting.TryDequeue(out var since)) return;
    _ = Task.Run(async () =>
    {
        try
        {
            await observer.Get($"/api/pos/tables?tableIds={table}");
            stats.Record("map", (sw.ElapsedTicks - since) * 1000.0 / Stopwatch.Frequency);
        }
        catch (Exception e) { stats.Error($"map refresh: {e.Message}"); }
    });
});
await hub.StartAsync();

// ---- Server memory, every simulated 10 minutes ----
var memory = new List<(double Hours, long Bytes)>();
using var sampling = new CancellationTokenSource();
var sampler = Task.Run(async () =>
{
    while (!sampling.IsCancellationRequested)
    {
        if (Database.ServerMemory() is { } bytes) lock (memory) memory.Add((Simulated(), bytes));
        try { await Task.Delay(Real(TimeSpan.FromMinutes(10)), sampling.Token); } catch (TaskCanceledException) { }
    }
});

// ---- The terminals: each a queue worked one job at a time, signing staff in and out as needed ----
var queues = terminals.Select(_ => Channel.CreateUnbounded<Func<Task>>()).ToArray();
var workers = terminals.Select((terminal, i) => Task.Run(async () =>
{
    await foreach (var job in queues[i].Reader.ReadAllAsync())
    {
        try { await job(); }
        catch (Exception e) { stats.Error($"{counters[i]}: {e.Message}"); }
    }
})).ToArray();

Task OnTerminal(int i, string person, Func<Api, Task> work)
{
    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    queues[i].Writer.TryWrite(async () =>
    {
        try
        {
            await terminals[i].SignIn(person, pins[person]);
            await work(terminals[i]);
            done.SetResult();
        }
        catch (Exception e) { done.SetException(e); throw; }
    });
    return done.Task;
}

double Simulated() => sw.Elapsed.TotalHours * options.Speed;
TimeSpan Real(TimeSpan simulated) => simulated / options.Speed;

object[] Round(int count) => Enumerable.Range(0, count).Select(_ =>
{
    var item = items[random.Next(items.Length)];
    return (object)new { menuItemId = item.Id, optionIds = item.Options, quantity = 1 };
}).ToArray();

async Task Print(Api api, Guid ticket, long number, Guid table)
{
    Changing(table);
    printed.Expect(number);
    await api.Send(HttpMethod.Post, $"/api/pos/tickets/{ticket}/print", null, stats, "print");
    stats.Count("prints");
}

async Task RunTicket(int t, string waiter)
{
    // Round 1: a new ticket on a table, printed.
    Guid ticket = default;
    long ticketNumber = 0;
    var mine = sections[waiters.IndexOf(waiter)];
    var table = mine[random.Next(mine.Length)];
    var lines = Round(random.Next(2, 6));
    await OnTerminal(t, waiter, async api =>
    {
        Changing(table);
        var created = await api.Send(HttpMethod.Post, "/api/pos/tickets", new { tableId = table, lines }, stats, "start");
        ticket = created.GetProperty("id").GetGuid();
        ticketNumber = created.GetProperty("number").GetInt64();
        stats.Count("tickets");
        stats.Count("quantity", lines.Length);
        await Print(api, ticket, ticketNumber, table);
        await api.Delete($"/api/pos/tickets/{ticket}/lock");
    });

    // Rounds 2 and 3, a few (simulated) minutes apart.
    for (var round = 2; round <= 3; round++)
    {
        await Task.Delay(Real(TimeSpan.FromMinutes(random.Next(3, 9))));
        var more = Round(random.Next(2, 6));
        await OnTerminal(t, waiter, async api =>
        {
            await api.Post($"/api/pos/tickets/{ticket}/lock");
            foreach (var line in more)
                await api.Send(HttpMethod.Post, $"/api/pos/tickets/{ticket}/lines", line, stats, "add");
            stats.Count("quantity", more.Length);
            await Print(api, ticket, ticketNumber, table);
            await api.Delete($"/api/pos/tickets/{ticket}/lock");
        });
    }

    // 3%: a printed item voided by a request the supervisor approves from Pillar 2.
    if (random.NextDouble() < options.VoidShare)
    {
        Guid request = default;
        await OnTerminal(t, waiter, async api =>
        {
            await api.Post($"/api/pos/tickets/{ticket}/lock");
            var detail = await api.Get($"/api/pos/tickets/{ticket}");
            var line = detail.GetProperty("lines").EnumerateArray().First(l => l.GetProperty("status").GetString() == "Printed");
            request = (await api.Post("/api/pos/approvals", new
            {
                kind = "Void", ticketId = ticket, lineId = line.GetProperty("id").GetGuid(),
                payload = new { quantity = 1, reason = "Wrong item", wasPoured = false },
            })).GetProperty("id").GetGuid();
            // The waiter keeps the ticket until the supervisor answers from their own counter (the void runs on it).
            await observer.Send(HttpMethod.Post, $"/api/pos/approvals/{request}/approve", null, stats, "approve");
            stats.Count("voids");
            await api.Delete($"/api/pos/tickets/{ticket}/lock");
        });
    }

    // Settle at the till, by the cashier: cash, M-Pesa, or both.
    await Task.Delay(Real(TimeSpan.FromMinutes(random.Next(2, 6))));
    await OnTerminal(tillOf[t], cashierOf[t], async api =>
    {
        await api.Post($"/api/pos/tickets/{ticket}/lock");
        var due = (await api.Get($"/api/pos/tickets/{ticket}")).GetProperty("remaining").GetDecimal();
        var kind = random.NextDouble();
        if (kind < 0.5)
            await Pay(api, ticket, new { type = "Cash", tendered = Math.Ceiling(due / 100) * 100 }, due);
        else if (kind < 0.85)
            await Pay(api, ticket, new { type = "Mpesa", amount = due, mpesaCode = MpesaCode() }, due);
        else
        {
            var cash = Math.Floor(due / 2);
            await Pay(api, ticket, new { type = "Cash", amount = cash, tendered = cash }, cash);
            await Pay(api, ticket, new { type = "Mpesa", amount = due - cash, mpesaCode = MpesaCode() }, due - cash);
        }
        Changing(table);
        await api.Send(HttpMethod.Post, $"/api/pos/tickets/{ticket}/settle", null, stats, "settle");
        stats.Count("settled");
        stats.Sum("settled amount", due);
    });
}

async Task Pay(Api api, Guid ticket, object payment, decimal amount)
{
    await api.Post($"/api/pos/tickets/{ticket}/payments", payment);
    stats.Count("payments");
    stats.Sum("payments", amount);
}

string MpesaCode() => "L" + new string(Enumerable.Range(0, 9).Select(_ => "ABCDEFGHJKLMNPQRSTUVWXYZ0123456789"[random.Next(34)]).ToArray());

// ---- The evening: each terminal opens 3 tickets a simulated minute, for the whole run ----
var tickets = new List<Task>();
var end = TimeSpan.FromHours(options.Hours);
var perTerminal = TimeSpan.FromMinutes(1.0 / options.TicketsPerMinute);
var openers = Enumerable.Range(0, terminals.Count).Select(t => Task.Run(async () =>
{
    var n = 0;
    while (TimeSpan.FromHours(Simulated()) < end)
    {
        var waiter = waiters[t * (waiters.Count / terminals.Count) + n % (waiters.Count / terminals.Count)];
        n++;
        lock (tickets) tickets.Add(Task.Run(() => RunTicket(t, waiter)).ContinueWith(task =>
        {
            if (task.Exception is { } e) stats.Error($"ticket on {counters[t]}: {e.GetBaseException().Message}");
        }));
        await Task.Delay(Real(perTerminal));
    }
})).ToArray();
await Task.WhenAll(openers);
Task[] all;
lock (tickets) all = [.. tickets];
await Task.WhenAll(all);
foreach (var q in queues) q.Writer.Complete();
await Task.WhenAll(workers);
await Task.Delay(TimeSpan.FromSeconds(10)); // the last slips print, the last map refresh
sampling.Cancel();
await sampler;
await hub.DisposeAsync();
printed.Dispose();

// ---- After the run: what the server holds must match what was done ----
var after = await Database.Snapshot(options);
var check = await admin.Get("/api/admin/system/check");
var result = Report.Build(options, stats, before, after, check, memory, sw.Elapsed);
Console.WriteLine(result.Markdown);
if (options.Out is { } outFile) File.WriteAllText(outFile, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
return result.Passed ? 0 : 1;
