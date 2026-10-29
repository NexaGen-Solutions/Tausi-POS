using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Domain.Money;
using Tausi.Domain.Settings;

namespace Tausi.Application.Settings;

public enum SettingType
{
    Bool,
    Int,
    Decimal,
    String,
    Enum,
    List,
}

/// <summary>
/// One setting: its key, type, default, where it shows on the settings screen, its label and help,
/// its limits, and whether terminals reload when it changes. <see cref="Rule"/> settings are the
/// switches in the screen's Rules list, in that order. <see cref="Editable"/> is false for the logo,
/// which has its own upload and remove.
/// </summary>
public sealed record SettingDefinition(
    string Key,
    SettingType Type,
    JsonNode? Default,
    string Section,
    string Label,
    string Help,
    decimal? Min = null,
    decimal? Max = null,
    IReadOnlyList<string>? Allowed = null,
    bool ReloadsTerminals = true,
    int MaxLength = 0,
    bool Required = false,
    int? Rule = null,
    bool Editable = true);

/// <summary>
/// Every setting, in one place. Read settings only through <see cref="SettingValues"/> (or the typed
/// records built from it); a value the owner hasn't changed is the default here.
/// Most values live in <c>BusinessSettings.OtherJson</c> under their key ("pos.idleLogoutMinutes" is
/// <c>{"pos":{"idleLogoutMinutes":10}}</c>); the business details, rounding and print mode have columns.
/// </summary>
public static class SettingsRegistry
{
    public static class Sections
    {
        public const string Business = "business";
        public const string Rules = "rules";
        public const string Pos = "pos";
        public const string Printing = "printing";
        public const string Money = "money";
        public const string CashUp = "cashup";
        public const string Stock = "stock";
    }

    public static readonly IReadOnlyList<(string Key, string Title)> SectionTitles =
    [
        (Sections.Business, "Business details"),
        (Sections.Rules, "Rules"),
        (Sections.Pos, "POS"),
        (Sections.Printing, "Printing"),
        (Sections.Money, "Money"),
        (Sections.CashUp, "Cash-up"),
        (Sections.Stock, "Stock"),
    ];

    public const string BusinessName = "business.name";
    public const string BusinessLogo = "business.logo";
    public const string BusinessAddress = "business.address";
    public const string BusinessPhone = "business.phone";
    public const string BusinessVatRate = "business.vatRate";
    public const string BusinessCurrency = "business.currency";
    public const string BusinessTimeZoneKey = "business.timeZone";
    public const string BusinessReceiptFooter = "business.receiptFooter";
    public const string PosIdleLogoutEnabled = "pos.idleLogoutEnabled";
    public const string PosIdleLogoutMinutes = "pos.idleLogoutMinutes";
    public const string PosLogoutAfterSettle = "pos.logoutAfterSettle";
    public const string PosShowOthersTickets = "pos.showOthersTickets";
    public const string PosLogoutAfterPrint = "pos.logoutAfterPrint";
    public const string PosTableIdleMinutes = "pos.tableIdleMinutes";
    public const string PosApprovalExpiryMinutes = "pos.approvalExpiryMinutes";
    public const string PosApprovalsFromOwnCounter = "pos.approvalsFromOwnCounter";
    public const string PosApprovalChime = "pos.approvalChime";
    public const string PrintModeKey = "print.mode";
    public const string PrintLogoOnBill = "print.logoOnBill";
    public const string PrintPoweredByFooter = "print.poweredByFooter";
    public const string PrintShowVoidsOnReceipt = "print.showVoidsOnReceipt";
    public const string PrintVatAmounts = "print.vatAmounts";
    public const string MoneyRounding = "money.rounding";
    public const string BooksBackdateDays = "books.backdateDays";
    public const string PrintKeepSlipTextDays = "print.keepSlipTextDays";
    public const string PaymentsMpesaCodeRequired = "payments.mpesaCodeRequired";
    public const string PaymentsRecordBroughtBy = "payments.recordBroughtBy";
    public const string TicketsCloseVoidedWithoutReceipt = "tickets.closeVoidedWithoutReceipt";
    public const string CashDefaultFloat = "cash.defaultFloat";
    public const string CashDenominations = "cash.denominations";
    public const string CashupBlind = "cashup.blind";
    public const string StockReturnOnVoidIfNotServed = "stock.returnOnVoidIfNotServed";

    public const int MaxMinutes = 24 * 60;

    public static readonly IReadOnlyList<SettingDefinition> All =
    [
        new(BusinessName, SettingType.String, "", Sections.Business, "Business name",
            "Shown on the login screen, admin site, bills and receipts.", MaxLength: 100, Required: true),
        new(BusinessLogo, SettingType.String, null, Sections.Business, "Logo",
            "PNG or JPG. Printed in black and white on receipts.", Editable: false),
        new(BusinessAddress, SettingType.String, "", Sections.Business, "Address",
            "Printed under the name on bills and receipts.", MaxLength: 200),
        new(BusinessPhone, SettingType.String, "", Sections.Business, "Phone",
            "Printed under the address.", MaxLength: 50),
        new(BusinessVatRate, SettingType.Decimal, 16m, Sections.Business, "VAT rate (prices include VAT)",
            "The standard rate, in percent. Each menu item can have its own.", Min: 0m, Max: 99.99m),
        new(BusinessCurrency, SettingType.String, "KES", Sections.Business, "Currency",
            "A 3-letter code, e.g. KES.", MaxLength: 3, Required: true),
        new(BusinessTimeZoneKey, SettingType.String, "Africa/Nairobi", Sections.Business, "Time zone",
            "Every printed time and date uses it.", MaxLength: BusinessTimeZone.MaxLength, Required: true),
        new(BusinessReceiptFooter, SettingType.String, "", Sections.Business, "Receipt footer",
            "e.g. Thank you for visiting", MaxLength: 300),

        new(PrintModeKey, SettingType.Enum, nameof(Domain.Settings.PrintMode.OneButton), Sections.Rules, "One Print button",
            "Prints the full bill, then only new items to the counter. Off: separate Send and Print bill.",
            Allowed: Enum.GetNames<PrintMode>(), Rule: 1),
        new(PosApprovalsFromOwnCounter, SettingType.Bool, true, Sections.Rules, "Supervisors approve from their own counter",
            "Requests appear on the supervisor's terminal. Who needs approval is set in Permissions. Off: only the supervisor's PIN on the spot.",
            Rule: 2),
        new(PaymentsRecordBroughtBy, SettingType.Bool, true, Sections.Rules, "Record who brought the cash",
            "Cashier picks the waiter when settling cash. Off: the waiter who opened the ticket.", Rule: 3),
        new(PrintShowVoidsOnReceipt, SettingType.Bool, false, Sections.Rules, "Show voided items on the receipt",
            "Off: the receipt lists all ordered items except voids.", Rule: 4),
        new(TicketsCloseVoidedWithoutReceipt, SettingType.Bool, true, Sections.Rules, "Close a fully voided ticket without a receipt",
            "Avoids printing empty receipts.", Rule: 5),
        new(StockReturnOnVoidIfNotServed, SettingType.Bool, true, Sections.Rules, "Return stock on void if not yet served",
            "Supervisor ticks \"not served\" when approving. Off: every void counts as poured.", Rule: 6),
        new(PaymentsMpesaCodeRequired, SettingType.Bool, true, Sections.Rules, "M-Pesa code required",
            "Cashier must type the code from the SMS. Off: the code is optional.", Rule: 7),
        new(PosIdleLogoutEnabled, SettingType.Bool, true, Sections.Rules, "Log out after {minutes} minutes idle",
            "Each terminal returns to the PIN screen.", Rule: 8),

        new(PosIdleLogoutMinutes, SettingType.Int, 10, Sections.Pos, "Idle logout (minutes)",
            "How long a terminal can sit unused before it logs out.", Min: 1, Max: 120),
        new(PosLogoutAfterSettle, SettingType.Bool, false, Sections.Pos, "Log out after settling",
            "The cashier's terminal returns to the PIN screen after each receipt."),
        new(PosLogoutAfterPrint, SettingType.Bool, false, Sections.Pos, "Log out after printing",
            "The waiter's terminal returns to the PIN screen after Print, Send or Print bill."),
        new(PosShowOthersTickets, SettingType.Bool, true, Sections.Pos, "Waiters see other waiters' tables",
            "The amount and waiter's name on their tables, and their tickets read-only. Off: a waiter sees others' tables only as taken."),
        new(PosTableIdleMinutes, SettingType.Int, 30, Sections.Pos, "Table idle warning (minutes)",
            "A table with an open ticket shows as idle after this long without activity.", Min: 1, Max: MaxMinutes),
        new(PosApprovalExpiryMinutes, SettingType.Int, 30, Sections.Pos, "Approval requests expire after (minutes)",
            "A request nobody decides is closed after this long.", Min: 1, Max: MaxMinutes),
        new(PosApprovalChime, SettingType.Bool, true, Sections.Pos, "Approval chime",
            "Supervisors' terminals play a short sound when a request comes in."),

        new(PrintLogoOnBill, SettingType.Bool, true, Sections.Printing, "Logo on bills and receipts",
            "Prints the logo at the top when one is uploaded."),
        new(PrintPoweredByFooter, SettingType.Bool, false, Sections.Printing, "\"Powered by Tausi POS\" footer",
            "Adds the line at the foot of bills and receipts."),
        new(PrintVatAmounts, SettingType.Bool, true, Sections.Printing, "VAT amounts on bills and receipts",
            "Prints \"Total includes VAT 16%\" with the amount. Off: one line, \"Inclusive of VAT\"."),
        new(PrintKeepSlipTextDays, SettingType.Int, 365, Sections.Printing, "Keep printed slip text (days)",
            "The ticket detail shows exactly what each slip printed; older slip text is cleared each night.", Min: 7, Max: 3650,
            ReloadsTerminals: false),

        new(MoneyRounding, SettingType.Enum, nameof(RoundingRule.NearestOne), Sections.Money, "Round totals",
            "Totals due are rounded; the difference is kept as its own line.", Allowed: Enum.GetNames<RoundingRule>()),
        new(BooksBackdateDays, SettingType.Int, 31, Sections.Money, "Back-dating allowed (days)",
            "How far back expenses, payments and banking can be dated on the admin site.", Min: 0, Max: 366, ReloadsTerminals: false),

        new(CashDefaultFloat, SettingType.Decimal, 0m, Sections.CashUp, "Usual opening float",
            "Offered when a cashier starts a shift.", Min: 0m, Max: 10_000_000m),
        new(CashDenominations, SettingType.List, new JsonArray(1000, 500, 200, 100, 50), Sections.CashUp, "Notes to count",
            "One row per note in the cash count; coins are one amount.", Min: 1, Max: 1_000_000),
        new(CashupBlind, SettingType.Bool, true, Sections.CashUp, "Blind count",
            "The cashier sees the expected cash only after submitting the count."),
    ];

    private static readonly Dictionary<string, SettingDefinition> ByKey = All.ToDictionary(d => d.Key, StringComparer.Ordinal);

    public static SettingDefinition? Find(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>Settings kept in their own column of BusinessSettings rather than in OtherJson.</summary>
    private static readonly HashSet<string> Columns =
    [
        BusinessName, BusinessLogo, BusinessAddress, BusinessPhone, BusinessVatRate, BusinessCurrency,
        BusinessTimeZoneKey, BusinessReceiptFooter, MoneyRounding, PrintModeKey,
    ];

    /// <summary>The current values: what is stored, else the default (also for a stored value that is no longer valid).</summary>
    public static SettingValues Read(BusinessSettings? row)
    {
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        JsonNode? other = null;
        try
        {
            other = string.IsNullOrWhiteSpace(row?.OtherJson) ? null : JsonNode.Parse(row.OtherJson);
        }
        catch (JsonException)
        {
            // Unreadable: every value falls back to its default.
        }

        foreach (var d in All)
        {
            var stored = row is null ? null : Columns.Contains(d.Key) ? FromColumn(row, d.Key) : Lookup(other, d.Key);
            values[d.Key] = stored is not null && d.Editable && Normalize(d, ToElement(stored), out var clean) is null
                ? clean
                : d.Key == BusinessLogo ? stored?.DeepClone() : d.Default?.DeepClone();
        }
        return new SettingValues(values);
    }

    public static async Task<SettingValues> Load(ITausiDb db, CancellationToken ct) =>
        Read(await db.BusinessSettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct));

    /// <summary>Writes a checked value into the row (a column, or its place in OtherJson).</summary>
    public static void Write(BusinessSettings row, string key, JsonNode? value)
    {
        switch (key)
        {
            case BusinessName: row.Name = value!.GetValue<string>(); return;
            case BusinessAddress: row.Address = value!.GetValue<string>(); return;
            case BusinessPhone: row.Phone = value!.GetValue<string>(); return;
            case BusinessVatRate: row.VatRate = value!.GetValue<decimal>(); return;
            case BusinessCurrency: row.Currency = value!.GetValue<string>(); return;
            case BusinessTimeZoneKey: row.TimeZone = value!.GetValue<string>(); return;
            case BusinessReceiptFooter: row.ReceiptFooter = value!.GetValue<string>(); return;
            case MoneyRounding: row.Rounding = Enum.Parse<RoundingRule>(value!.GetValue<string>()); return;
            case PrintModeKey: row.PrintMode = Enum.Parse<PrintMode>(value!.GetValue<string>()); return;
        }

        JsonObject other;
        try
        {
            other = JsonNode.Parse(string.IsNullOrWhiteSpace(row.OtherJson) ? "{}" : row.OtherJson) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            other = [];
        }
        var (section, name) = Split(key);
        if (other[section] is not JsonObject group)
        {
            group = [];
            other[section] = group;
        }
        group[name] = value?.DeepClone();
        row.OtherJson = other.ToJsonString();
    }

    /// <summary>
    /// Checks a value for a setting and returns the error, or null with the value as stored
    /// (trimmed text, an upper-case currency, the notes sorted largest first).
    /// </summary>
    public static string? Normalize(SettingDefinition d, JsonElement value, out JsonNode? clean)
    {
        clean = null;
        switch (d.Type)
        {
            case SettingType.Bool:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return "Choose on or off.";
                clean = JsonValue.Create(value.GetBoolean());
                return null;

            case SettingType.Int:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var whole))
                    return $"Enter a whole number from {d.Min:0} to {d.Max:0}.";
                if (whole < d.Min || whole > d.Max) return $"Enter a whole number from {d.Min:0} to {d.Max:0}.";
                clean = JsonValue.Create(whole);
                return null;

            case SettingType.Decimal:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number))
                    return "Enter a number.";
                if (number < d.Min || number > d.Max) return $"Enter a number from {d.Min:0.##} to {d.Max:#,##0.##}.";
                if (decimal.Round(number, 2) != number) return "Use at most 2 decimal places.";
                clean = JsonValue.Create(number);
                return null;

            case SettingType.String:
            {
                if (value.ValueKind != JsonValueKind.String) return "Enter some text.";
                var text = value.GetString()!.Trim();
                if (d.Required && text.Length == 0) return $"{d.Label} is required.";
                if (d.MaxLength > 0 && text.Length > d.MaxLength) return $"Use at most {d.MaxLength} characters.";
                if (d.Key == BusinessCurrency)
                {
                    if (text.Length != 3 || !text.All(char.IsAsciiLetter)) return "Use a 3-letter code, e.g. KES.";
                    text = text.ToUpperInvariant();
                }
                if (d.Key == BusinessTimeZoneKey && !BusinessTimeZone.IsValid(text))
                    return "Choose a time zone from the list, e.g. Africa/Nairobi.";
                clean = JsonValue.Create(text);
                return null;
            }

            case SettingType.Enum:
            {
                var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                var match = d.Allowed!.FirstOrDefault(a => string.Equals(a, text, StringComparison.OrdinalIgnoreCase));
                if (match is null) return $"Choose one of: {string.Join(", ", d.Allowed!)}.";
                clean = JsonValue.Create(match);
                return null;
            }

            case SettingType.List:
            {
                const string error = "Enter note values, e.g. 1000, 500, 200, 100, 50.";
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is 0 or > 20) return error;
                var notes = new List<int>();
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var note) || note < d.Min || note > d.Max)
                        return error;
                    notes.Add(note);
                }
                clean = new JsonArray(notes.Distinct().OrderDescending().Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
                return null;
            }

            default:
                return "Unknown setting.";
        }
    }

    private static JsonNode? FromColumn(BusinessSettings row, string key) => key switch
    {
        BusinessName => row.Name,
        BusinessLogo => row.LogoFileId is { } id ? JsonValue.Create(id.ToString()) : null,
        BusinessAddress => row.Address,
        BusinessPhone => row.Phone,
        BusinessVatRate => row.VatRate,
        BusinessCurrency => row.Currency,
        // Rows from before the setting have no time zone: the server's.
        BusinessTimeZoneKey => string.IsNullOrWhiteSpace(row.TimeZone) ? BusinessTimeZone.ServerZoneId() : row.TimeZone,
        BusinessReceiptFooter => row.ReceiptFooter,
        MoneyRounding => row.Rounding.ToString(),
        PrintModeKey => row.PrintMode.ToString(),
        _ => null,
    };

    private static JsonNode? Lookup(JsonNode? other, string key)
    {
        var (section, name) = Split(key);
        return other is JsonObject root && root[section] is JsonObject group ? group[name] : null;
    }

    private static (string Section, string Name) Split(string key)
    {
        var dot = key.IndexOf('.');
        return (key[..dot], key[(dot + 1)..]);
    }

    private static JsonElement ToElement(JsonNode node) => JsonSerializer.SerializeToElement(node);
}

/// <summary>The value of every setting, read once. Typed getters for the registry's keys.</summary>
public sealed class SettingValues(IReadOnlyDictionary<string, JsonNode?> values)
{
    public IReadOnlyDictionary<string, JsonNode?> All => values;

    public JsonNode? this[string key] => values[key];

    public bool Bool(string key) => values[key]!.GetValue<bool>();

    public int Int(string key) => values[key]!.GetValue<int>();

    public decimal Decimal(string key) => values[key]!.GetValue<decimal>();

    public string String(string key) => values[key]?.GetValue<string>() ?? "";

    public IReadOnlyList<int> IntList(string key) => values[key]!.AsArray().Select(n => n!.GetValue<int>()).ToList();

    public TEnum Enum<TEnum>(string key) where TEnum : struct, System.Enum => System.Enum.Parse<TEnum>(String(key));

    /// <summary>The same values with some replaced (already checked), e.g. for a preview of unsaved changes.</summary>
    public SettingValues With(IReadOnlyDictionary<string, JsonNode?> changes)
    {
        var copy = values.ToDictionary(v => v.Key, v => v.Value?.DeepClone(), StringComparer.Ordinal);
        foreach (var (key, value) in changes) copy[key] = value?.DeepClone();
        return new SettingValues(copy);
    }

    public static bool Same(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);
}
