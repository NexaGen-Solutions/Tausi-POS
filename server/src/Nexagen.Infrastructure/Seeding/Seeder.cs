using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexagen.Application.Abstractions;
using Nexagen.Application.Security;
using Nexagen.Application.Settings;
using Nexagen.Domain.Devices;
using Nexagen.Domain.Ledger;
using Nexagen.Domain.Money;
using Nexagen.Domain.People;
using Nexagen.Domain.Settings;

namespace Nexagen.Infrastructure.Seeding;

/// <summary>A user created by the seed, with the PIN shown once.</summary>
public sealed record SeededUser(string Name, string Role, string Pin);

/// <summary>
/// Idempotent seeding. The base seed (roles, default permissions, ledger accounts) is needed by
/// every business. The demo seed adds the first customer's business, staff, terminals and printers.
/// Existing rows are never changed, so running it again keeps any edits.
/// </summary>
public sealed class Seeder(INexagenDb db, IPinHasher hasher, ILogger<Seeder> log)
{
    public const string AdminPasswordVariable = "NEXAGEN_ADMIN_PASSWORD";

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

        var codes = await db.LedgerAccounts.Select(a => a.Code).ToListAsync(ct);
        foreach (var (code, name, type) in LedgerAccountCodes.Defaults.Where(d => !codes.Contains(d.Code)))
            db.LedgerAccounts.Add(new LedgerAccount { Code = code, Name = name, Type = type });

        await db.SaveChangesAsync(ct);
    }

    /// <param name="filePrinters">
    /// Development: demo printers use the File connection, so prints land in print-output/.
    /// Otherwise they are Windows-share placeholders to be set on site.
    /// </param>
    public async Task<IReadOnlyList<SeededUser>> SeedDemo(string? adminPassword, CancellationToken ct, bool filePrinters = false)
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
                OtherJson = PosSettings.DefaultJson,
            });
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
        return created;
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
}
