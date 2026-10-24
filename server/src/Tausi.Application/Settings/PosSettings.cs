using Tausi.Application.Abstractions;
using Tausi.Domain.Settings;
using static Tausi.Application.Settings.SettingsRegistry;

namespace Tausi.Application.Settings;

/// <summary>
/// Terminal behaviour settings (the "pos.*" keys of <see cref="SettingsRegistry"/>).
/// <see cref="TableIdleMinutes"/> is how long a table's open ticket can go without activity before the
/// table map shows it as unpaid and idle. <see cref="ApprovalExpiryMinutes"/> is how long an approval
/// request waits before it expires. With <see cref="IdleLogoutEnabled"/> off, sessions never go idle;
/// the 12-hour limit still applies.
/// </summary>
public sealed record PosSettings(
    int IdleLogoutMinutes, bool LogoutAfterSettle, int TableIdleMinutes = PosSettings.DefaultTableIdleMinutes,
    int ApprovalExpiryMinutes = PosSettings.DefaultApprovalExpiryMinutes, bool IdleLogoutEnabled = true,
    bool ApprovalsFromOwnCounter = true, bool ApprovalChime = true)
{
    public const int DefaultApprovalExpiryMinutes = 30;
    public const int DefaultIdleLogoutMinutes = 10;
    public const int DefaultTableIdleMinutes = 30;

    public static readonly PosSettings Default = From(SettingsRegistry.Read(null));

    /// <summary>How long a session may go unused; never, when idle logout is off.</summary>
    public TimeSpan IdleLimit => IdleLogoutEnabled ? TimeSpan.FromMinutes(IdleLogoutMinutes) : TimeSpan.MaxValue;

    /// <summary>The idle logout the terminals count down, or null when it is off.</summary>
    public int? IdleLogoutMinutesIfOn => IdleLogoutEnabled ? IdleLogoutMinutes : null;

    public TimeSpan TableIdleLimit => TimeSpan.FromMinutes(TableIdleMinutes);

    public TimeSpan ApprovalExpiry => TimeSpan.FromMinutes(ApprovalExpiryMinutes);

    public static PosSettings From(SettingValues v) => new(
        v.Int(PosIdleLogoutMinutes), v.Bool(PosLogoutAfterSettle), v.Int(PosTableIdleMinutes), v.Int(PosApprovalExpiryMinutes),
        v.Bool(PosIdleLogoutEnabled), v.Bool(PosApprovalsFromOwnCounter), v.Bool(PosApprovalChime));

    /// <summary>From an OtherJson document alone (the business details don't matter here).</summary>
    public static PosSettings From(string? otherJson) => From(SettingsRegistry.Read(new BusinessSettings { OtherJson = otherJson ?? "" }));

    public static async Task<PosSettings> Load(ITausiDb db, CancellationToken ct) => From(await SettingsRegistry.Load(db, ct));
}
