using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Domain.Common;
using Tausi.Domain.People;
using Tausi.Domain.Settings;
using Tausi.Licensing;

namespace Tausi.Application.Setup;

/// <summary>Which public keys the server trusts. The test key only in the Development environment.</summary>
public sealed record LicenceOptions(bool AcceptTestKey);

public sealed record LicenceStatusDto(
    string Status, bool IsTrial, string? Edition, string? Business, string? LicenceId, int TerminalLimit, int TerminalsUsed,
    DateTimeOffset? ExpiresAt, int? DaysLeft, DateTimeOffset? SupportUntil, bool OverLimit, string? Banner);

public sealed record EnterLicenceRequest(string Key);

public static class LicenceCodes
{
    public const string Invalid = "licence_invalid";
    public const string WrongBusiness = "licence_wrong_business";
    public const string Expired = "licence_expired";
    public const string TrialUsed = "licence_trial_used";
}

/// <summary>What waits for a key once a licence is past its grace period. Selling, printing, settling and closing the day never do.</summary>
public enum LicensedFeature
{
    Pairing,
    MenuAdmin,
    StockAdmin,
    Reports,
    Settings,
}

/// <summary>
/// Licences: a key signed by NexaGen (checked offline against the public keys built in here) or a
/// 30-day trial with 2 terminals. The rules never stop a bar mid-service (see <see cref="LicencePolicy"/>).
/// </summary>
public sealed class LicenceService(
    ITausiDb db, LicenceOptions options, ICurrentSession session, IPermissionService permissions, IAuditLog audit, IPosEvents events,
    TimeProvider clock)
{
    /// <summary>NexaGen's public key; its private key is kept offline with the licence tool.</summary>
    public const string ProductionPublicKey = "2ew8OJM3O9sadOQm/RzbFzBUMzEX6N8mfAiGE9w0mkY=";

    /// <summary>The tests' key pair (accepted only in Development); its private key lives only in the tests.</summary>
    public const string TestPublicKey = "M1lWbg6p6uU3NqXaD4ZOqjrEJ+51fcbHB8yjnVjaDWI=";

    public IEnumerable<byte[]> PublicKeys =>
        options.AcceptTestKey
            ? [Convert.FromBase64String(ProductionPublicKey), Convert.FromBase64String(TestPublicKey)]
            : [Convert.FromBase64String(ProductionPublicKey)];

    public async Task<Licence?> Current(CancellationToken ct) => await db.Licences.OrderBy(l => l.Id).FirstOrDefaultAsync(ct);

    /// <summary>The time the licence is judged at; also moves the "latest seen" forward, so turning the clock back gains nothing.</summary>
    public async Task<DateTimeOffset> Now(CancellationToken ct)
    {
        var licence = await Current(ct);
        var clockNow = clock.GetUtcNow();
        if (licence is not null && (licence.LastSeenAt is null || clockNow > licence.LastSeenAt))
        {
            licence.LastSeenAt = clockNow;
            await db.SaveChangesAsync(ct);
        }
        return LicencePolicy.Now(licence, clockNow);
    }

    public async Task<LicenceStatusDto> Status(CancellationToken ct)
    {
        var now = await Now(ct);
        var licence = await Current(ct);
        var status = LicencePolicy.Status(licence, now);
        var used = await TerminalsInUse(ct);
        var days = LicencePolicy.DaysLeft(licence, now);
        var banner = status switch
        {
            LicenceStatus.Trial => $"Trial: {days} days left · Enter licence key",
            LicenceStatus.ExpiresSoon => $"Your licence ends in {days} days. Enter a new key to keep everything working.",
            LicenceStatus.Grace => $"Your licence has ended. In {Math.Max(0, (int)Math.Ceiling(((licence!.ExpiresAt!.Value + LicencePolicy.Grace) - now).TotalDays))} days the Menu, Stock, Reports and Settings pages and pairing will wait for a new key. Selling never stops.",
            LicenceStatus.Expired => "Your licence has ended. Enter a new key to use the Menu, Stock, Reports and Settings pages and to pair terminals. Selling, printing and settling still work.",
            _ => null,
        };
        var limit = TerminalLimit(licence);
        return new LicenceStatusDto(status.ToString(), licence?.IsTrial ?? false, licence?.Edition, licence?.BusinessName, licence?.LicenceId,
            limit, used, licence?.ExpiresAt, days, licence?.SupportUntil, limit > 0 && used > limit, banner);
    }

    /// <summary>Enters a key (setup step 6, or Settings → Licence with settings.manage).</summary>
    public async Task<LicenceStatusDto> Enter(EnterLicenceRequest r, bool asSetup, CancellationToken ct)
    {
        if (!asSetup) await permissions.Require(session.RequireUser(), PermissionKeys.SettingsManage, ct: ct);
        var problem = LicenceKeys.TryRead(r.Key, PublicKeys, out var payload);
        if (problem != LicenceKeyProblem.None)
            throw new DomainException("That licence key isn't valid. Check it was copied completely.", LicenceCodes.Invalid);
        var business = (await SettingsRegistry.Load(db, ct)).String(SettingsRegistry.BusinessName);
        if (!LicenceKeys.SameBusiness(payload!.Business, business))
            throw new DomainException($"This key is for '{payload.Business}'.", LicenceCodes.WrongBusiness);

        var licence = await Current(ct);
        var old = licence is null ? null : new { licence.LicenceId, licence.Edition, licence.TerminalLimit, licence.ExpiresAt, licence.IsTrial };
        if (licence is null)
        {
            licence = new Licence();
            db.Licences.Add(licence);
        }
        licence.Key = r.Key.Trim();
        licence.LicenceId = payload.Id;
        licence.BusinessName = payload.Business;
        licence.Edition = payload.Edition;
        licence.TerminalLimit = payload.MaxTerminals;
        licence.IssuedAt = payload.Issued;
        licence.ExpiresAt = payload.Expires;
        licence.SupportUntil = payload.SupportUntil;
        licence.IsTrial = false;
        licence.LastCheckedAt = clock.GetUtcNow();
        audit.Write(new AuditRecord(AuditActions.LicenceEntered, nameof(Licence), payload.Id, old,
            new { payload.Id, payload.Edition, payload.MaxTerminals, payload.Expires, payload.SupportUntil },
            Actor: asSetup ? SetupFlow.SetupActor : null));
        await db.SaveChangesAsync(ct);
        await events.Publish(PosEventNames.LicenceChanged, new { status = "entered" }, ct);
        return await Status(ct);
    }

    /// <summary>The 30-day trial: 2 terminals, every feature. Only when there has never been a licence or trial.</summary>
    public async Task<LicenceStatusDto> StartTrial(bool asSetup, CancellationToken ct)
    {
        if (!asSetup) await permissions.Require(session.RequireUser(), PermissionKeys.SettingsManage, ct: ct);
        if (await Current(ct) is not null)
            throw new DomainException("This install already has a licence or a trial.", LicenceCodes.TrialUsed);
        var now = clock.GetUtcNow();
        var business = (await SettingsRegistry.Load(db, ct)).String(SettingsRegistry.BusinessName);
        db.Licences.Add(new Licence
        {
            IsTrial = true, Edition = "Trial", BusinessName = business, TerminalLimit = Licence.TrialTerminals, IssuedAt = now,
            ExpiresAt = now.AddDays(Licence.TrialDays), LastSeenAt = now, LastCheckedAt = now,
        });
        audit.Write(new AuditRecord(AuditActions.LicenceTrialStarted, nameof(Licence), null, null,
            new { days = Licence.TrialDays, terminals = Licence.TrialTerminals }, Actor: asSetup ? SetupFlow.SetupActor : null));
        await db.SaveChangesAsync(ct);
        await events.Publish(PosEventNames.LicenceChanged, new { status = "trial" }, ct);
        return await Status(ct);
    }

    /// <summary>Refuses what waits for a key after the grace period; everything else always runs.</summary>
    public async Task Require(LicensedFeature feature, CancellationToken ct)
    {
        var status = LicencePolicy.Status(await Current(ct), await Now(ct));
        if (status != LicenceStatus.Expired) return;
        var what = feature switch
        {
            LicensedFeature.Pairing => "Pairing new terminals",
            LicensedFeature.MenuAdmin => "The Menu page",
            LicensedFeature.StockAdmin => "The Stock pages",
            LicensedFeature.Reports => "Reports",
            _ => "Settings",
        };
        throw new ForbiddenException($"{what} waits for a licence key. Enter one in Settings → Licence. Selling, printing and settling still work.",
            LicenceCodes.Expired);
    }

    /// <summary>The terminal limit: the licence's, the trial's, or none (0) before either exists.</summary>
    public static int TerminalLimit(Licence? licence) => licence?.TerminalLimit ?? 0;

    public Task<int> TerminalsInUse(CancellationToken ct) =>
        db.Terminals.CountAsync(t => t.DeviceTokenHash != null && t.RevokedAt == null, ct);
}
