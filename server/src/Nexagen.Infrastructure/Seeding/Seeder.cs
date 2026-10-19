using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexagen.Application.Abstractions;
using Nexagen.Application.Security;
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
        foreach (var (name, adminSite) in DefaultPermissions.Roles)
        {
            var role = roles.FirstOrDefault(r => r.Name == name);
            if (role is null)
            {
                role = new Role { Name = name, CanUseAdminSite = adminSite };
                db.Roles.Add(role);
                roles.Add(role);
            }
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

    public async Task<IReadOnlyList<SeededUser>> SeedDemo(string? adminPassword, CancellationToken ct)
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
                } while (!usedLookups.Add(lookup));

                user = new User
                {
                    Name = name,
                    RoleId = roles[role].Id,
                    PinHash = hasher.Hash(pin),
                    PinLookup = lookup,
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

        await SeedDevices(ct);
        await db.SaveChangesAsync(ct);
        return created;
    }

    private async Task SeedDevices(CancellationToken ct)
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
        Printer AddPrinter(string name, string share, Terminal attachedTo)
        {
            if (printers.TryGetValue(name, out var existing)) return existing;
            var printer = new Printer
            {
                Name = name,
                ConnectionMode = PrinterConnection.WindowsShare,
                Address = share,
                OwningTerminalId = attachedTo.Id,
                PaperWidthMm = 80,
            };
            db.Printers.Add(printer);
            printers[name] = printer;
            return printer;
        }

        // Dispense A = Ticket Printer 1 at Counter A; Dispense B = Ticket Printer 2 at Counter B.
        var dispenseA = AddPrinter("Ticket Printer 1", @"\\COUNTER-A\TicketPrinter1", terminals["Counter A"]);
        var dispenseB = AddPrinter("Ticket Printer 2", @"\\COUNTER-B\TicketPrinter2", terminals["Counter B"]);
        await db.SaveChangesAsync(ct);

        void Route(string terminal, Printer printer)
        {
            var t = terminals[terminal];
            t.DispensePrinterId ??= printer.Id;
            t.ReceiptPrinterId ??= printer.Id;
        }
        Route("Counter A", dispenseA);
        Route("Pillar 1", dispenseA);
        Route("Counter B", dispenseB);
        Route("Pillar 2", dispenseB);
    }
}
