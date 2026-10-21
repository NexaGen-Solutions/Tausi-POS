using System.Text.Json.Serialization;

namespace Tausi.Application.Menu;

// ---- Admin view ----

public sealed record AdminCategoryDto(Guid Id, string Name, int SortOrder, bool Active, int ActiveItems, long RowVersion);

public sealed record AdminItemDto(
    Guid Id,
    string Name,
    string ShortName,
    Guid CategoryId,
    int SortOrder,
    decimal? VatRate,
    bool Active,
    string? ErpItemCode,
    IReadOnlyDictionary<Guid, decimal> Prices,
    IReadOnlyList<Guid> OwnGroupIds,
    IReadOnlyList<Guid> InheritedGroupIds,
    long RowVersion);

public sealed record AdminPriceListDto(Guid Id, string Name, bool Active, bool IsDefault, long RowVersion);

public sealed record AdminOptionDto(Guid Id, string Name, decimal PriceAddOn, int SortOrder, bool Active);

public sealed record AdminGroupDto(
    Guid Id,
    string Name,
    int MinSelect,
    int MaxSelect,
    int SortOrder,
    bool Active,
    IReadOnlyList<AdminOptionDto> Options,
    IReadOnlyList<Guid> CategoryIds,
    IReadOnlyList<Guid> ItemIds,
    long RowVersion);

public sealed record AdminMenuDto(
    IReadOnlyList<AdminCategoryDto> Categories,
    IReadOnlyList<AdminItemDto> Items,
    IReadOnlyList<AdminPriceListDto> PriceLists,
    IReadOnlyList<AdminGroupDto> Groups,
    bool CanEdit,
    bool CanEditPrices,
    decimal BusinessVatRate,
    string Currency);

public sealed record PriceLogEntryDto(
    DateTimeOffset At,
    string? ItemName,
    string? OptionName,
    string? PriceListName,
    decimal? OldPrice,
    decimal? NewPrice,
    string ChangedBy,
    string? ApprovedBy,
    string Where,
    string Source);

/// <summary>What was saved, with its new version for the next request (e.g. its prices).</summary>
public sealed record SavedRef(Guid Id, long RowVersion);

// ---- Requests ----
// Structure requests carry no price fields at all, and refuse unknown fields: a price sent to
// them is rejected (400) rather than silently ignored. Prices have their own endpoints.

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CategoryRequest(string Name, bool Active = true, long RowVersion = 0);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReorderRequest(IReadOnlyList<Guid> Ids);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeactivateCategoryRequest(bool IncludeItems, long RowVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ItemRequest(
    string Name,
    string? ShortName,
    Guid CategoryId,
    decimal? VatRate,
    bool Active,
    string? ErpItemCode,
    IReadOnlyList<Guid>? GroupIds,
    int? SortOrder = null,
    long RowVersion = 0);

/// <summary>Prices by price list id (including VAT); null removes a price from a non-default list.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ItemPricesRequest(IReadOnlyDictionary<Guid, decimal?> Prices, bool AllowZero, long RowVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OptionRequest(Guid? Id, string Name, int SortOrder, bool Active = true);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GroupRequest(
    string Name,
    int MinSelect,
    int MaxSelect,
    int SortOrder,
    bool Active,
    IReadOnlyList<OptionRequest> Options,
    IReadOnlyList<Guid>? CategoryIds,
    IReadOnlyList<Guid>? ItemIds,
    long RowVersion = 0);

/// <summary>Add-ons by option id (including VAT).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OptionPricesRequest(IReadOnlyDictionary<Guid, decimal> AddOns, long RowVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PriceListRequest(string Name, bool Active = true, long RowVersion = 0);

// ---- Terminal view ----

public sealed record PosOptionDto(Guid Id, string Name, decimal PriceAddOn, bool IsDefault = false);

public sealed record PosGroupDto(Guid Id, string Name, int MinSelect, int MaxSelect, IReadOnlyList<PosOptionDto> Options);

public sealed record PosItemDto(
    Guid Id, string Name, string ShortName, decimal Price, decimal VatRate, IReadOnlyList<PosGroupDto> Groups);

public sealed record PosCategoryDto(Guid Id, string Name, IReadOnlyList<PosItemDto> Items);

/// <summary>The menu as one terminal sells it. <c>MenuVersion</c> changes whenever anything in it does.</summary>
public sealed record PosMenuDto(string MenuVersion, Guid PriceListId, string PriceListName, IReadOnlyList<PosCategoryDto> Categories);

public static class MenuCodes
{
    public const string NameTaken = "name_taken";
    public const string NameInvalid = "name_invalid";
    public const string ShortNameTooLong = "short_name_too_long";
    public const string VatInvalid = "vat_invalid";
    public const string PriceNegative = "price_negative";
    public const string PriceZero = "price_zero";
    public const string DefaultPriceRequired = "default_price_required";
    public const string GroupInvalid = "group_invalid";
    public const string Stale = "stale";
    public const string ImportErrors = "import_errors";

    public const string StaleMessage = "This was changed by someone else. Reload and try again.";
}
