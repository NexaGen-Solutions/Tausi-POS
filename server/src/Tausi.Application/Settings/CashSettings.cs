using Tausi.Application.Abstractions;
using Tausi.Domain.Settings;
using static Tausi.Application.Settings.SettingsRegistry;

namespace Tausi.Application.Settings;

/// <summary>
/// Cash drawer settings: <c>cash.defaultFloat</c>, <c>cash.denominations</c> and <c>cashup.blind</c>.
/// The count has one row per note in <see cref="Denominations"/>, plus coins as one amount. With
/// <see cref="BlindCount"/> on, the cashier sees the expected cash and the difference only after
/// submitting the count.
/// </summary>
public sealed record CashSettings(decimal DefaultFloat, IReadOnlyList<int> Denominations, bool BlindCount)
{
    public static readonly IReadOnlyList<int> KenyanNotes = [1000, 500, 200, 100, 50];
    public static readonly CashSettings Default = From(SettingsRegistry.Read(null));

    public static CashSettings From(SettingValues v) =>
        new(v.Decimal(CashDefaultFloat), v.IntList(CashDenominations), v.Bool(CashupBlind));

    public static CashSettings From(string? otherJson) => From(SettingsRegistry.Read(new BusinessSettings { OtherJson = otherJson ?? "" }));

    public static async Task<CashSettings> Load(ITausiDb db, CancellationToken ct) => From(await SettingsRegistry.Load(db, ct));
}
