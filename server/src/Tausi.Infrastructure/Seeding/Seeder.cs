using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tausi.Application.Abstractions;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Application.Tickets;
using Tausi.Domain.Cash;
using Tausi.Domain.Devices;
using Tausi.Domain.Ledger;
using Tausi.Domain.Menu;
using Tausi.Domain.Money;
using Tausi.Domain.People;
using Tausi.Domain.Service;
using Tausi.Domain.Settings;

namespace Tausi.Infrastructure.Seeding;

/// <summary>A user created by the seed, with the PIN shown once.</summary>
public sealed record SeededUser(string Name, string Role, string Pin);

/// <summary>
/// Idempotent seeding. The base seed (roles, default permissions, ledger accounts) is needed by
/// every business. The demo seed adds the first customer's business, staff, terminals and printers.
/// Existing rows are never changed, so running it again keeps any edits.
/// </summary>
public sealed partial class Seeder(ITausiDb db, IPinHasher hasher, ILogger<Seeder> log, ITicketDatabase? ticketNumbers = null)
{
    public const string AdminPasswordVariable = "TAUSI_ADMIN_PASSWORD";
    public const string OldAdminPasswordVariable = "NEXAGEN_ADMIN_PASSWORD";

    /// <summary>The admin-site password for the demo Administrator, from the environment (old name accepted).</summary>
    public static string? AdminPasswordFromEnvironment(Func<string, string?>? read = null) =>
        Legacy.LegacyNames.Environment(AdminPasswordVariable, OldAdminPasswordVariable, read);

    public async Task SeedBase(CancellationToken ct)
    {
        var roles = await db.Roles.Include(r => r.Permissions).ToListAsync(ct);
        foreach (var (name, roleKey, adminSite) in DefaultPermissions.Roles)
        {
            var role = roles.FirstOrDefault(r => r.SystemKey == roleKey)
                ?? roles.FirstOrDefault(r => r.Name == name && r.SystemKey == null);
            if (role is null)
            {
                role = new Role { Name = name, CanUseAdminSite = adminSite };
                db.Roles.Add(role);
                roles.Add(role);
            }
            // Roles from before built-in keys existed get theirs here.
            role.SystemKey ??= roleKey;
            foreach (var key in PermissionKeys.All)
            {
                if (role.Permissions.Any(p => p.PermissionKey == key)) continue;
                role.Permissions.Add(new RolePermission
                {
                    RoleId = role.Id,
                    PermissionKey = key,
                    Value = DefaultPermissions.For(name, key),
                });
            }
        }

        if (!await db.PriceLists.AnyAsync(ct))
        {
            db.PriceLists.Add(new PriceList { Name = PriceList.StandardName, IsDefault = true });
            db.PriceLists.Add(new PriceList { Name = PriceList.Price2Name });
        }

        var codes = await db.LedgerAccounts.Select(a => a.Code).ToListAsync(ct);
        foreach (var (code, name, type) in LedgerAccountCodes.Defaults.Where(d => !codes.Contains(d.Code)))
            db.LedgerAccounts.Add(new LedgerAccount { Code = code, Name = name, Type = type });

        await SeedStockLocations(ct);

        // Pay-out categories (Task 12 adds the admin screen for them).
        if (!await db.ExpenseCategories.AnyAsync(ct))
            for (var i = 0; i < ExpenseCategory.Defaults.Count; i++)
                db.ExpenseCategories.Add(new ExpenseCategory { Name = ExpenseCategory.Defaults[i], SortOrder = (i + 1) * 10 });

        await db.SaveChangesAsync(ct);
    }

    /// <param name="filePrinters">
    /// Development: demo printers use the File connection, so prints land in print-output/.
    /// Otherwise they are Windows-share placeholders to be set on site.
    /// </param>
    /// <summary>Mau's Village is in Kenya.</summary>
    public const string DemoTimeZone = "Africa/Nairobi";

    public async Task<IReadOnlyList<SeededUser>> SeedDemo(string? adminPassword, CancellationToken ct, bool filePrinters = false, bool openDemoDay = false)
    {
        await SeedBase(ct);

        if (!await db.BusinessSettings.AnyAsync(ct))
        {
            db.BusinessSettings.Add(new BusinessSettings
            {
                Name = "Mau's Village",
                VatRate = 16m,
                Currency = "KES",
                Rounding = RoundingRule.NearestOne,
                PrintMode = PrintMode.OneButton,
                ReceiptFooter = "Thank you. Karibu tena!",
                OtherJson = "{}",
                TimeZone = DemoTimeZone,
            });
        }
        else
        {
            // Settings from before the time-zone setting: the demo business is in Kenya.
            var settings = await db.BusinessSettings.OrderBy(s => s.Id).FirstAsync(ct);
            if (string.IsNullOrWhiteSpace(settings.TimeZone)) settings.TimeZone = DemoTimeZone;
        }

        var roles = await db.Roles.ToDictionaryAsync(r => r.Name, ct);
        var created = new List<SeededUser>();
        var people = new (string Name, string Role, string? UserName)[]
        {
            ("Administrator", DefaultPermissions.Admin, "admin"),
            ("MAU", DefaultPermissions.Supervisor, null),
            ("CAROL", DefaultPermissions.Cashier, null),
            ("edna", DefaultPermissions.Waiter, null),
            ("CYNTHIA", DefaultPermissions.Waiter, null),
            ("Ruth", DefaultPermissions.Waiter, null),
            ("WAMBOO", DefaultPermissions.Waiter, null),
        };
        var usedLookups = (await db.Users.Where(u => u.Active).Select(u => u.PinLookup).ToListAsync(ct)).ToHashSet();
        foreach (var (name, role, userName) in people)
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Name == name, ct);
            if (user is null)
            {
                string pin, lookup;
                do
                {
                    pin = SecureTokens.Digits(4);
                    lookup = hasher.Lookup(pin);
                } while (Pin.IsTooSimple(pin) || !usedLookups.Add(lookup));

                user = new User
                {
                    Name = name,
                    RoleId = roles[role].Id,
                    PinHash = hasher.Hash(pin),
                    PinLookup = lookup,
                    PinLength = pin.Length,
                    UserName = userName,
                };
                db.Users.Add(user);
                created.Add(new SeededUser(name, role, pin));
            }
            if (userName is not null && user.PasswordHash is null)
            {
                if (string.IsNullOrEmpty(adminPassword))
                    log.LogWarning("{Variable} is not set, so {Name} has no admin-site password yet", AdminPasswordVariable, name);
                else
                    user.PasswordHash = hasher.Hash(adminPassword);
            }
        }

        await SeedDevices(filePrinters, ct);
        await db.SaveChangesAsync(ct);
        await SeedDemoMenu(ct);
        await SeedDemoTemperature(ct);
        await SeedDemoTables(ct);
        await SeedDemoStock(ct);
        await SeedDemoTickets(ct, openDemoDay);
        return created;
    }

    /// <summary>Mau's layout: tables 1–16 in Main, Pool 1–2 in Pool, Bar 1–2 in Bar. Only into an empty layout.</summary>
    public async Task<bool> SeedDemoTables(CancellationToken ct)
    {
        if (await db.Areas.AnyAsync(ct) || await db.Tables.AnyAsync(ct)) return false;
        var layout = new (string Area, string Prefix, int Count)[] { ("Main", "", 16), ("Pool", "Pool ", 2), ("Bar", "Bar ", 2) };
        var areaSort = 0;
        foreach (var (name, prefix, count) in layout)
        {
            var area = new Area { Name = name, SortOrder = areaSort += 10 };
            db.Areas.Add(area);
            for (var n = 1; n <= count; n++)
                db.Tables.Add(new DiningTable { Name = prefix + n, AreaId = area.Id, SortOrder = n });
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// A few open tickets with lines, so the demo map shows occupied tables, totals, two tickets on
    /// Table 7 (to try merging) and an idle table. They belong to the open day (one is opened if needed).
    /// Run again, it adds only the ones that are missing: a demo ticket counts as there when its
    /// waiter already has that many open tickets on its table.
    /// </summary>
    /// <param name="openDayIfNeeded">Open a demo day when none is open (the <c>seed --demo</c> command); otherwise add nothing then.</param>
    public async Task<bool> SeedDemoTickets(CancellationToken ct, bool openDayIfNeeded = false)
    {
        var tables = await db.Tables.ToDictionaryAsync(t => t.Name, ct);
        var users = await db.Users.ToDictionaryAsync(u => u.Name, ct);
        var terminals = await db.Terminals.ToDictionaryAsync(t => t.Name, ct);
        var items = await db.MenuItems.Include(i => i.Prices).ToDictionaryAsync(i => i.Name, ct);
        var vat = (await db.BusinessSettings.OrderBy(s => s.Id).FirstOrDefaultAsync(ct))?.VatRate ?? 16m;
        if (tables.Count == 0 || items.Count == 0 || !terminals.ContainsKey("Counter A")) return false;

        var now = DateTimeOffset.UtcNow;
        // A day only closes with no open tickets, so demo tickets belong to the open day: the seed
        // opens a demo day when none is open.
        var day = await db.WorkPeriods.FirstOrDefaultAsync(w => w.IsOpen, ct);
        if (day is null && !openDayIfNeeded) return false;
        if (day is null)
        {
            var opener = users.GetValueOrDefault("MAU") ?? users.GetValueOrDefault("Administrator");
            if (opener is null) return false;
            day = new WorkPeriod { OpenedAt = now.AddHours(-2), OpenedByUserId = opener.Id };
            db.WorkPeriods.Add(day);
            await db.SaveChangesAsync(ct);
        }
        // Earlier seeds left open tickets in a day that was closed again: they belong to this one now.
        await db.Tickets.Where(k => k.Status == TicketStatus.Open && k.WorkPeriodId != day.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(k => k.WorkPeriodId, day.Id), ct);

        var demo = new (string Table, string Waiter, string Terminal, int MinutesAgo, int IdleMinutes, (string Item, int Qty)[] Lines)[]
        {
            ("1", "WAMBOO", "Counter A", 70, 4, [("Tusker Lager", 4), ("Guiness", 2), ("Mau's Water 1L", 2)]),
            ("3", "CYNTHIA", "Pillar 1", 40, 9, [("Gilbeys 350ml", 1)]),
            ("5", "Ruth", "Counter B", 95, 48, [("Heineken", 2), ("Pineapple Punch", 2), ("Sodas 300ml", 1)]),
            ("7", "edna", "Counter A", 60, 6, [("Tusker Lager", 3), ("Pineapple Punch", 1), ("Sodas 300ml", 3)]),
            ("7", "edna", "Pillar 1", 25, 3, [("White Cap Lager", 1), ("Mau's Water 1L", 1), ("Faxe Can", 1)]),
            ("8", "WAMBOO", "Counter A", 30, 12, [("Guiness", 2)]),
            ("11", "CYNTHIA", "Pillar 1", 50, 15, [("Viceroy 375ml", 1), ("Sodas 300ml", 3)]),
            ("Pool 1", "MAU", "Counter B", 35, 2, [("Pool Table", 6)]),
            ("Bar 1", "Ruth", "Counter B", 20, 5, [("Tusker Lager", 1), ("Delmonte", 1)]),
        };
        var beersId = await db.Categories.Where(c => c.Name == "Beers").Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        var coldOption = await db.ModifierOptions.FirstOrDefaultAsync(o => o.Name == "Cold", ct);
        var open = await db.Tickets.Where(k => k.Status == TicketStatus.Open && k.TableId != null)
            .Select(k => new { TableId = k.TableId!.Value, k.OpenedByUserId }).ToListAsync(ct);
        var wanted = new Dictionary<(Guid, Guid), int>();
        var added = 0;
        foreach (var (tableName, waiter, terminalName, minutesAgo, idle, lines) in demo)
        {
            if (!tables.TryGetValue(tableName, out var table) || !users.TryGetValue(waiter, out var user)
                || !terminals.TryGetValue(terminalName, out var terminal)) continue;
            var key = (table.Id, user.Id);
            wanted[key] = wanted.GetValueOrDefault(key) + 1;
            if (open.Count(o => o.TableId == table.Id && o.OpenedByUserId == user.Id) >= wanted[key]) continue;
            added++;
            var opened = now.AddMinutes(-minutesAgo);
            var ticket = new Ticket
            {
                Number = ticketNumbers is null ? await NextNumberWithoutSequence(ct) : await ticketNumbers.NextNumber(ct),
                TableId = table.Id,
                OpenedByUserId = user.Id,
                OpenedAt = opened,
                TerminalId = terminal.Id,
                WorkPeriodId = day.Id,
                LastActivityAt = now.AddMinutes(-idle),
                PrintRound = 1,
            };
            foreach (var (itemName, qty) in lines)
            {
                if (!items.TryGetValue(itemName, out var item) || item.Prices.Count == 0) continue;
                var price = item.Prices[0].Price;
                var rate = item.EffectiveVatRate(vat);
                var amounts = VatCalculator.ForLine(price, qty, rate, item.VatExempt);
                var cold = item.CategoryId == beersId ? coldOption : null;
                ticket.Lines.Add(new OrderLine
                {
                    TicketId = ticket.Id, MenuItemId = item.Id, Name = item.Name, ShortName = item.ShortName,
                    Options = cold?.Name, OptionIds = cold?.Id.ToString() ?? "",
                    Quantity = qty, UnitPrice = price,
                    VatRate = rate, VatExempt = item.VatExempt, Gross = amounts.Gross, Net = amounts.Net, Vat = amounts.Vat,
                    AddedByUserId = user.Id, AddedTerminalId = terminal.Id, AddedAt = opened, PrintedAt = opened,
                    Status = OrderLineStatus.Printed, PrintRound = 1,
                });
            }
            db.Tickets.Add(ticket);
        }
        if (added == 0) return false;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private long _lastNumberWithoutSequence;

    private async Task<long> NextNumberWithoutSequence(CancellationToken ct)
    {
        if (_lastNumberWithoutSequence == 0)
            _lastNumberWithoutSequence = await db.Tickets.MaxAsync(t => (long?)t.Number, ct) ?? 1000;
        return ++_lastNumberWithoutSequence;
    }

    private async Task SeedDevices(bool filePrinters, CancellationToken ct)
    {
        var terminals = await db.Terminals.ToDictionaryAsync(t => t.Name, ct);
        foreach (var name in new[] { "Counter A", "Counter B", "Pillar 1", "Pillar 2" })
        {
            if (terminals.ContainsKey(name)) continue;
            var terminal = new Terminal { Name = name };
            db.Terminals.Add(terminal);
            terminals[name] = terminal;
        }
        // Terminals and printers point at each other, so they are saved in steps.
        await db.SaveChangesAsync(ct);

        var printers = await db.Printers.ToDictionaryAsync(p => p.Name, ct);

        // One printer per terminal. "Dispense A" / "Dispense B" are templates (headed "Counter A" /
        // "Counter B"), shared by a counter and its pillar. The counter printers also print receipts.
        var plan = new (string Printer, string Terminal, string Share, string Template, string Header)[]
        {
            ("Ticket Printer 1", "Counter A", @"\\COUNTER-A\TicketPrinter1", "Dispense A", "Counter A"),
            ("Pillar 1", "Pillar 1", @"\\PILLAR-1\Pillar1", "Dispense A", "Counter A"),
            ("Ticket Printer 2", "Counter B", @"\\COUNTER-B\TicketPrinter2", "Dispense B", "Counter B"),
            ("Pillar 2", "Pillar 2", @"\\PILLAR-2\Pillar2", "Dispense B", "Counter B"),
        };

        foreach (var p in plan)
        {
            var terminal = terminals[p.Terminal];
            if (!printers.TryGetValue(p.Printer, out var printer))
            {
                printer = new Printer
                {
                    Name = p.Printer,
                    ConnectionMode = filePrinters ? PrinterConnection.File : PrinterConnection.WindowsShare,
                    Address = filePrinters ? "" : p.Share,
                    OwningTerminalId = terminal.Id,
                    PaperWidthMm = 80,
                };
                db.Printers.Add(printer);
                printers[p.Printer] = printer;
            }
            else if (filePrinters && printer.ConnectionMode == PrinterConnection.WindowsShare && printer.Address == p.Share)
            {
                // A development database seeded before File printers existed: move the untouched
                // placeholders over, so test prints land in print-output/.
                printer.ConnectionMode = PrinterConnection.File;
                printer.Address = "";
            }
            printer.DispenseTemplate ??= p.Template;
            printer.DispenseHeader ??= p.Header;
        }
        await db.SaveChangesAsync(ct);

        // Each terminal prints dispense slips on its own printer. Receipts print at the counter, as at
        // Mau's: Counter A and Pillar 1 on Ticket Printer 1, Counter B and Pillar 2 on Ticket Printer 2.
        // Only demo routes still as an earlier seed left them move (the two-printer setup shared the
        // counter's printer for dispensing; the next one put pillar receipts on the pillar printer);
        // anything set by hand stays.
        foreach (var p in plan)
        {
            var terminal = terminals[p.Terminal];
            var own = printers[p.Printer];
            var counterPrinter = p.Template == "Dispense A" ? printers["Ticket Printer 1"] : printers["Ticket Printer 2"];
            if (terminal.DispensePrinterId is null || (terminal.DispensePrinterId == counterPrinter.Id && own != counterPrinter))
                terminal.DispensePrinterId = own.Id;
            if (terminal.ReceiptPrinterId is null || (terminal.ReceiptPrinterId == own.Id && own != counterPrinter))
                terminal.ReceiptPrinterId = counterPrinter.Id;
            // Pillars' dispense slips say which counter they belong to.
            terminal.DispenseHeader ??= p.Header;
        }
    }

    /// <summary>
    /// Development: makes a user choose a new PIN at their next login (as after an admin reset).
    /// Returns false when there is no active user with that name.
    /// </summary>
    public async Task<bool> RequirePinChange(string name, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Name == name && u.Active, ct);
        if (user is null) return false;
        user.MustChangePin = true;
        db.AuditEntries.Add(new Domain.Audit.AuditEntry
        {
            At = DateTimeOffset.UtcNow,
            Action = Application.Auditing.AuditActions.PinChangeRequired,
            EntityType = "User",
            EntityId = user.Id.ToString(),
            NewValueJson = "{\"mustChangePin\":true,\"by\":\"command line\"}",
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// The demo menu (Mau's Village items and Standard prices), only when the menu is empty.
    /// Returns false when there was already a menu.
    /// </summary>
    public async Task<bool> SeedDemoMenu(CancellationToken ct)
    {
        await SeedBase(ct);
        if (await db.Categories.AnyAsync(ct) || await db.MenuItems.AnyAsync(ct)) return false;

        var standard = await db.PriceLists.FirstAsync(l => l.IsDefault, ct);
        var categories = new Dictionary<string, Category>();
        var order = 0;
        foreach (var name in new[] { "Beers", "Spirits", "Soft drinks", "Water & juice", "Pool", "Food" })
        {
            var category = new Category { Name = name, SortOrder = order += 10 };
            categories[name] = category;
            db.Categories.Add(category);
        }

        var items = new (string Name, string Category, decimal Price)[]
        {
            ("Tusker Lager", "Beers", 250), ("Guiness", "Beers", 300), ("White Cap Lager", "Beers", 280),
            ("Balozi Lager", "Beers", 250), ("Heineken", "Beers", 300), ("Faxe Can", "Beers", 175),
            ("Viceroy 375ml", "Spirits", 900), ("Gilbeys 350ml", "Spirits", 900),
            ("Sodas 300ml", "Soft drinks", 100),
            ("Mau's Water 1L", "Water & juice", 100), ("Pineapple Punch", "Water & juice", 270), ("Delmonte", "Water & juice", 400),
            ("Pool Table", "Pool", 40),
        };
        var sort = new Dictionary<string, int>();
        foreach (var (name, category, price) in items)
        {
            sort[category] = sort.GetValueOrDefault(category) + 10;
            var item = new MenuItem
            {
                Name = name,
                ShortName = MenuItem.DefaultShortName(name),
                CategoryId = categories[category].Id,
                SortOrder = sort[category],
            };
            item.Prices.Add(new MenuItemPrice { MenuItemId = item.Id, PriceListId = standard.Id, Price = price });
            db.MenuItems.Add(item);
        }

        await db.SaveChangesAsync(ct);
        await SeedDemoTemperature(ct);
        return true;
    }

    /// <summary>
    /// Beers ask Temperature (Cold / Warm, choose 1, Cold chosen already); water, sodas and spirits
    /// don't. Also brings a menu seeded earlier (which asked it for soft drinks too) into line.
    /// </summary>
    public async Task SeedDemoTemperature(CancellationToken ct)
    {
        var beers = await db.Categories.FirstOrDefaultAsync(c => c.Name == "Beers", ct);
        if (beers is null) return;
        var temperature = await db.ModifierGroups.Include(g => g.Options).FirstOrDefaultAsync(g => g.Name == "Temperature", ct);
        if (temperature is null)
        {
            temperature = new ModifierGroup { Name = "Temperature", MinSelect = 1, MaxSelect = 1, SortOrder = 10 };
            temperature.Options.Add(new ModifierOption { GroupId = temperature.Id, Name = "Cold", SortOrder = 10, IsDefault = true });
            temperature.Options.Add(new ModifierOption { GroupId = temperature.Id, Name = "Warm", SortOrder = 20 });
            db.ModifierGroups.Add(temperature);
        }
        else if (!temperature.Options.Any(o => o.IsDefault) && temperature.Options.FirstOrDefault(o => o.Name == "Cold") is { } cold)
        {
            cold.IsDefault = true;
        }

        var assigned = await db.ModifierGroupAssignments.Where(a => a.GroupId == temperature.Id).ToListAsync(ct);
        if (!assigned.Any(a => a.CategoryId == beers.Id))
            db.ModifierGroupAssignments.Add(new ModifierGroupAssignment { GroupId = temperature.Id, CategoryId = beers.Id });
        var softDrinks = await db.Categories.Where(c => c.Name == "Soft drinks").Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        foreach (var extra in assigned.Where(a => a.CategoryId is not null && a.CategoryId == softDrinks))
            db.ModifierGroupAssignments.Remove(extra);
        await db.SaveChangesAsync(ct);
    }
}
