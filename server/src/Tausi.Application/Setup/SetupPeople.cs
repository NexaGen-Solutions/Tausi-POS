using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Auth;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Printing;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Application.Staff;
using Tausi.Domain.Common;
using Tausi.Domain.Menu;
using Tausi.Domain.People;
using Tausi.Domain.Settings;
using Tausi.Domain.Setup;

namespace Tausi.Application.Setup;

public static class BusinessTypes
{
    public const string Bar = "Bar";
    public const string Restaurant = "Restaurant";
    public const string BarAndRestaurant = "BarAndRestaurant";
    public static readonly IReadOnlyList<string> All = [Bar, Restaurant, BarAndRestaurant];
}

public sealed record SetupBusinessRequest(
    string Name, string Type, string? Address, string? Phone, decimal VatRate, string Currency, string TimeZone);

/// <summary>
/// Step 1, Your business: saved through the settings registry as ordinary audited setting changes
/// by "setup". The type sets defaults that can be changed later: a bar gets the Temperature
/// options (Cold / Warm) and the one Print button; a restaurant doesn't get Temperature.
/// </summary>
public sealed class SetupBusiness(ITausiDb db, SetupFlow flow, IAuditLog audit, IPosEvents events)
{
    public async Task Save(SetupBusinessRequest r, CancellationToken ct)
    {
        if (!BusinessTypes.All.Contains(r.Type))
            throw new FieldErrorsException(new Dictionary<string, string> { ["type"] = "Choose Bar, Restaurant or Bar and restaurant." });
        var values = new Dictionary<string, JsonElement>
        {
            [SettingsRegistry.BusinessName] = JsonSerializer.SerializeToElement((r.Name ?? "").Trim()),
            [SettingsRegistry.BusinessAddress] = JsonSerializer.SerializeToElement((r.Address ?? "").Trim()),
            [SettingsRegistry.BusinessPhone] = JsonSerializer.SerializeToElement((r.Phone ?? "").Trim()),
            [SettingsRegistry.BusinessVatRate] = JsonSerializer.SerializeToElement(r.VatRate),
            [SettingsRegistry.BusinessCurrency] = JsonSerializer.SerializeToElement(r.Currency),
            [SettingsRegistry.BusinessTimeZoneKey] = JsonSerializer.SerializeToElement(r.TimeZone),
        };
        if (r.Type != BusinessTypes.Restaurant)
            values[SettingsRegistry.PrintModeKey] = JsonSerializer.SerializeToElement(nameof(PrintMode.OneButton));
        if ((r.Name ?? "").Trim().Length == 0)
            throw new FieldErrorsException(new Dictionary<string, string> { [SettingsRegistry.BusinessName] = "Enter the business name." });
        var clean = SettingsView.Check(values);

        var row = await db.BusinessSettings.OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        if (row is null)
        {
            row = new BusinessSettings();
            db.BusinessSettings.Add(row);
        }
        var before = SettingsRegistry.Read(row);
        var changed = new List<string>();
        foreach (var (key, value) in clean)
        {
            if (SettingValues.Same(before[key], value)) continue;
            SettingsRegistry.Write(row, key, value);
            audit.Write(new AuditRecord(AuditActions.SettingsChanged, AuditEntities.Setting, key, new { value = before[key] }, new { value },
                Actor: SetupFlow.SetupActor));
            changed.Add(key);
        }

        var state = await flow.State(ct);
        state.BusinessType = r.Type;
        if (r.Type != BusinessTypes.Restaurant) await EnsureTemperature(ct);
        await db.SaveChangesAsync(ct);
        await flow.Mark(SetupSteps.Business, SetupStepStatus.Done, $"{(r.Name ?? "").Trim()} · {Label(r.Type)}", ct);
        if (changed.Count > 0) await events.Publish(PosEventNames.SettingsChanged, new { keys = changed }, ct);
    }

    public static string Label(string type) => type switch
    {
        BusinessTypes.BarAndRestaurant => "Bar and restaurant",
        _ => type,
    };

    /// <summary>Beers ask Temperature: Cold (chosen already) or Warm, choose 1.</summary>
    private async Task EnsureTemperature(CancellationToken ct)
    {
        if (await db.ModifierGroups.AnyAsync(g => g.Name == "Temperature", ct)) return;
        var group = new ModifierGroup { Name = "Temperature", MinSelect = 1, MaxSelect = 1, SortOrder = 10 };
        group.Options.Add(new ModifierOption { GroupId = group.Id, Name = "Cold", SortOrder = 10, IsDefault = true });
        group.Options.Add(new ModifierOption { GroupId = group.Id, Name = "Warm", SortOrder = 20 });
        db.ModifierGroups.Add(group);
    }
}

public sealed record SetupAdminRequest(string FullName, string UserName, string Password, string Pin, string PinAgain);

public sealed record StaffToAdd(string Name, string Role);

public sealed record SetupStaffRequest(IReadOnlyList<StaffToAdd> People);

/// <summary>A new person and their first PIN: shown once, then only on the printed sheet.</summary>
public sealed record NewStaffPin(Guid Id, string Name, string Role, string Pin);

public sealed record SetupStaffResult(IReadOnlyList<NewStaffPin> People, string PinSheetPdf);

/// <summary>
/// Step 2, First admin and PIN: the owner (built-in Admin role, admin-site name and password, a
/// 4-digit PIN), then an optional quick staff list whose people get random PINs they must change
/// at their first login. The PINs come back once, with the PIN sheet; they are never stored readable.
/// </summary>
public sealed class SetupPeople(
    ITausiDb db, SetupFlow flow, StaffAccess staff, PinRules pins, IPinHasher hasher, IAuditLog audit)
{
    public async Task<SessionUser> Admin(SetupAdminRequest r, CancellationToken ct)
    {
        var role = await db.Roles.FirstAsync(x => x.SystemKey == RoleKeys.Admin, ct);
        var existing = await FirstAdmin(role.Id, ct);
        var errors = new Dictionary<string, string>();
        string name = "";
        try
        {
            name = await staff.ValidName(r.FullName, existing?.Id, ct);
        }
        catch (DomainException e)
        {
            errors["fullName"] = e.Message;
        }
        var userName = (r.UserName ?? "").Trim().ToLowerInvariant();
        if (userName.Length is < 3 or > 40 || !userName.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'))
            errors["userName"] = "Use 3 to 40 letters, digits, dots, dashes or underscores.";
        else if (await db.Users.AnyAsync(u => u.UserName == userName && u.Id != existing!.Id, ct))
            errors["userName"] = "That name is taken.";
        if ((r.Password ?? "").Length < StaffAccess.MinPasswordLength)
            errors["password"] = $"At least {StaffAccess.MinPasswordLength} characters.";
        if (!System.Text.RegularExpressions.Regex.IsMatch(r.Pin ?? "", "^[0-9]{4}$")) errors["pin"] = "Enter 4 digits.";
        else if (r.Pin != r.PinAgain) errors["pinAgain"] = "The two PINs are different.";
        else
        {
            try
            {
                await pins.Check(r.Pin, existing?.Id, ct);
            }
            catch (DomainException e)
            {
                errors["pin"] = e.Message;
            }
        }
        if (errors.Count > 0) throw new FieldErrorsException(errors);

        var user = existing ?? new User { RoleId = role.Id };
        user.Name = name;
        user.NormalizedName = Domain.People.User.Normalize(name);
        user.UserName = userName;
        user.PasswordHash = hasher.Hash(r.Password!);
        pins.Apply(user, r.Pin!);
        user.MustChangePin = false;
        user.Active = true;
        if (existing is null) db.Users.Add(user);
        audit.Write(new AuditRecord(existing is null ? AuditActions.StaffCreated : AuditActions.StaffUpdated, nameof(User), user.Id.ToString(),
            null, StaffAccess.AuditView(user, role.Name), Actor: SetupFlow.SetupActor));
        await db.SaveChangesAsync(ct);
        await flow.Mark(SetupSteps.Admin, SetupStepStatus.Done, $"Owner: {name} ({userName})", ct);
        return new SessionUser(user.Id, user.Name, role.Id, role.Name, null, null, null);
    }

    /// <summary>The quick staff list: each person gets a random PIN and must change it at first login.</summary>
    public async Task<SetupStaffResult> Staff(SetupStaffRequest r, CancellationToken ct)
    {
        var roles = await db.Roles.Where(x => x.SystemKey != null).ToDictionaryAsync(x => x.SystemKey!, ct);
        var errors = new Dictionary<string, string>();
        var people = r.People ?? [];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < people.Count; i++)
        {
            if (!names.Add((people[i].Name ?? "").Trim())) errors[$"people.{i}.name"] = "This name is in the list twice.";
            try
            {
                await staff.ValidName(people[i].Name, null, ct);
            }
            catch (DomainException e)
            {
                errors[$"people.{i}.name"] = e.Message;
            }
            if (!new[] { RoleKeys.Waiter, RoleKeys.Cashier, RoleKeys.Supervisor }.Contains((people[i].Role ?? "").ToLowerInvariant()))
                errors[$"people.{i}.role"] = "Choose Waiter, Cashier or Supervisor.";
        }
        if (errors.Count > 0) throw new FieldErrorsException(errors);

        var created = await Create(people.Select(p => (p.Name.Trim(), roles[p.Role.ToLowerInvariant()], (string?)null)).ToList(), ct);
        var business = (await SettingsRegistry.Load(db, ct)).String(SettingsRegistry.BusinessName);
        return new SetupStaffResult(created, Convert.ToBase64String(PinSheet.Build(business, created)));
    }

    /// <summary>New people with random PINs (also for the SambaPOS import); saved, audited, and their PINs returned once.</summary>
    public async Task<List<NewStaffPin>> Create(IReadOnlyList<(string Name, Role Role, string? ExternalRef)> people, CancellationToken ct)
    {
        var created = new List<NewStaffPin>();
        foreach (var (name, role, externalRef) in people)
        {
            // Each PIN is checked against the ones already saved, so save as we go.
            var pin = await pins.Generate(null, ct);
            var user = new User
            {
                Name = name, NormalizedName = Domain.People.User.Normalize(name), RoleId = role.Id, MustChangePin = true, ExternalRef = externalRef,
            };
            pins.Apply(user, pin);
            db.Users.Add(user);
            audit.Write(new AuditRecord(AuditActions.StaffCreated, nameof(User), user.Id.ToString(), null, StaffAccess.AuditView(user, role.Name),
                Actor: SetupFlow.SetupActor));
            await db.SaveChangesAsync(ct);
            created.Add(new NewStaffPin(user.Id, name, role.Name, pin));
        }
        return created;
    }

    private Task<User?> FirstAdmin(Guid adminRole, CancellationToken ct) =>
        db.Users.Where(u => u.RoleId == adminRole && u.UserName != null).OrderBy(u => u.Id).FirstOrDefaultAsync(ct);
}

/// <summary>
/// The PIN sheet: an A4 page (or more) with each person's name, role and first PIN, "Change this
/// PIN at first login", and a dashed cut line between people.
/// </summary>
public static class PinSheet
{
    public static byte[] Build(string business, IReadOnlyList<NewStaffPin> people)
    {
        var pdf = new SimplePdf();
        const float left = 50, slip = 120;
        var right = pdf.PaperWidth - 50;
        var y = 50f;
        pdf.Text(left, y, $"{business} · first PINs", 14, bold: true);
        pdf.Text(left, y + 18, "Cut along the lines and give each person their own slip.", 9);
        y += 40;
        foreach (var person in people)
        {
            if (y + slip > pdf.PaperHeight - 40)
            {
                pdf.NewPage();
                y = 40;
            }
            for (var x = left; x < right; x += 10) pdf.Line(x, y, Math.Min(x + 5, right), y, 0.5f);
            pdf.Text(left, y + 28, person.Name, 16, bold: true);
            pdf.Text(left, y + 46, person.Role, 11);
            pdf.Text(left, y + 80, "PIN", 11);
            pdf.Text(left + 40, y + 82, person.Pin, 26, bold: true);
            pdf.Text(left, y + 104, "Change this PIN at first login.", 10);
            y += slip;
        }
        for (var x = left; x < right; x += 10) pdf.Line(x, y, Math.Min(x + 5, right), y, 0.5f);
        return pdf.Build();
    }
}
