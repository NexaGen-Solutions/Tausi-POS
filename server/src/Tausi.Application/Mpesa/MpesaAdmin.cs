using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Security;
using Tausi.Domain.Mpesa;
using Tausi.Domain.People;

namespace Tausi.Application.Mpesa;

/// <summary>Which secrets are stored (never their values).</summary>
public sealed record MpesaSecretsDto(bool ConsumerKey, bool ConsumerSecret, bool Passkey, bool RelayApiKey, bool Unreadable);

public sealed record RelayStatusDto(string State, string? Reason, DateTimeOffset? LastMessageAt, DateTimeOffset? LastCollectedAt);

public sealed record MpesaAdminDto(MpesaSecretsDto Secrets, RelayStatusDto Relay, DateTimeOffset? C2BRegisteredAt, bool SetUp);

/// <summary>New secrets: null leaves one as it is, "" clears it.</summary>
public sealed record SaveMpesaSecretsRequest(string? ConsumerKey, string? ConsumerSecret, string? Passkey, string? RelayApiKey);

public sealed record MpesaCheck(string Name, bool Ok, string Message);

/// <summary>What the terminal screens need to know.</summary>
public sealed record PosMpesaDto(bool SetUp, bool StkEnabled, bool WaiterPrompts, bool AllowTypedCodes, int Received);

/// <summary>
/// Settings → M-Pesa (board 26), with <c>settings.manage</c>: which secrets are stored (shown as ••••),
/// saving new ones (encrypted; the audit says which changed, never the values), the relay's status,
/// and Test connection: a Daraja token, the relay login (which gives this business's callback
/// addresses) and, the first time or when they changed, registering the C2B addresses with Daraja.
/// </summary>
public sealed class MpesaAdmin(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, ISecretProtector secrets, IDaraja daraja, IRelayClient relay,
    RelayStatus status, IAuditLog audit, TimeProvider clock)
{
    public async Task<MpesaAdminDto> Get(CancellationToken ct)
    {
        await permissions.Require(session.RequireUser(), PermissionKeys.SettingsManage, ct: ct);
        var account = await db.MpesaAccounts.AsNoTracking().OrderBy(a => a.Id).FirstOrDefaultAsync(ct);
        var settings = await MpesaSettings.Load(db, secrets, ct);
        return new MpesaAdminDto(
            new MpesaSecretsDto(!string.IsNullOrEmpty(account?.ConsumerKey), !string.IsNullOrEmpty(account?.ConsumerSecret),
                !string.IsNullOrEmpty(account?.Passkey), !string.IsNullOrEmpty(account?.RelayApiKey), settings.SecretsUnreadable),
            Status(), account?.C2BRegisteredAt, settings.SetUp);
    }

    public RelayStatusDto Status() => new(status.State.ToString(), status.Reason, status.LastMessageAt, status.LastCollectedAt);

    public async Task<MpesaAdminDto> SaveSecrets(SaveMpesaSecretsRequest r, CancellationToken ct)
    {
        await permissions.Require(session.RequireUser(), PermissionKeys.SettingsManage, ct: ct);
        var account = await db.MpesaAccounts.OrderBy(a => a.Id).FirstOrDefaultAsync(ct);
        if (account is null)
        {
            account = new MpesaAccount();
            db.MpesaAccounts.Add(account);
        }
        var changed = new List<string>();
        string? Set(string? current, string? value, string name)
        {
            if (value is null) return current;
            var trimmed = value.Trim();
            if (trimmed.Length > 500) throw new Common.FieldErrorsException(new Dictionary<string, string> { [name] = "That is too long." });
            changed.Add(name);
            return trimmed.Length == 0 ? null : secrets.Protect(trimmed);
        }

        account.ConsumerKey = Set(account.ConsumerKey, r.ConsumerKey, "consumerKey");
        account.ConsumerSecret = Set(account.ConsumerSecret, r.ConsumerSecret, "consumerSecret");
        account.Passkey = Set(account.Passkey, r.Passkey, "passkey");
        var relayKey = account.RelayApiKey;
        account.RelayApiKey = Set(account.RelayApiKey, r.RelayApiKey, "relayApiKey");
        if (r.RelayApiKey is not null && account.RelayApiKey != relayKey) account.StkCallbackUrl = null;
        account.UpdatedAt = clock.GetUtcNow();
        if (changed.Count > 0)
            audit.Write(new AuditRecord(AuditActions.MpesaSettingsChanged, "MpesaAccount", account.Id.ToString(), null, new { changed }));
        await db.SaveChangesAsync(ct);
        return await Get(ct);
    }

    public async Task<IReadOnlyList<MpesaCheck>> Test(CancellationToken ct)
    {
        await permissions.Require(session.RequireUser(), PermissionKeys.SettingsManage, ct: ct);
        var settings = await MpesaSettings.Load(db, secrets, ct);
        var checks = new List<MpesaCheck>();
        if (settings.SecretsUnreadable)
            checks.Add(new MpesaCheck("Credentials", false, "The saved credentials can't be read on this server (moved or restored?). Enter them again."));
        if (!settings.SetUp)
        {
            checks.Add(new MpesaCheck("Daraja", false,
                settings.PayTo.Length == 0 || settings.ShortCode.Length == 0
                    ? settings.Paybill ? "Enter the paybill number." : "Enter the till number and the store number."
                    : "Enter the consumer key, consumer secret and passkey."));
            return checks;
        }

        try
        {
            await daraja.CheckCredentials(settings.Credentials!, settings.Live, ct);
            checks.Add(new MpesaCheck("Daraja", true, $"Signed in to Safaricom Daraja ({(settings.Live ? "live" : "sandbox")})."));
        }
        catch (DarajaException e)
        {
            checks.Add(new MpesaCheck("Daraja", false, e.Message));
            return checks;
        }

        if (string.IsNullOrEmpty(settings.RelayApiKey))
        {
            checks.Add(new MpesaCheck("Relay", false, "Enter the relay API key NexaGen gave you."));
            return checks;
        }
        RelayAccount me;
        try
        {
            me = await relay.Me(settings.RelayUrl, settings.RelayApiKey, ct);
            checks.Add(new MpesaCheck("Relay", true, $"Signed in to the relay as {me.Name}."));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            checks.Add(new MpesaCheck("Relay", false, $"Can't reach the relay: {e.Message}"));
            return checks;
        }

        var account = await db.MpesaAccounts.OrderBy(a => a.Id).FirstAsync(ct);
        account.StkCallbackUrl = me.Callbacks.Stk;
        var registration = $"{settings.ShortCode}|{me.Callbacks.Confirmation}|{me.Callbacks.Validation}";
        if (account.C2BRegisteredFor == registration)
            checks.Add(new MpesaCheck("Payments to the till", true, "Safaricom sends payments to the relay (registered before)."));
        else
        {
            try
            {
                await daraja.RegisterC2B(settings.Credentials!, settings.Live, settings.ShortCode, me.Callbacks.Confirmation, me.Callbacks.Validation, ct);
                account.C2BRegisteredFor = registration;
                account.C2BRegisteredAt = clock.GetUtcNow();
                checks.Add(new MpesaCheck("Payments to the till", true, "Registered: Safaricom now sends payments to the relay."));
            }
            catch (DarajaException e)
            {
                checks.Add(new MpesaCheck("Payments to the till", false, $"Couldn't register with Safaricom: {e.Message}"));
            }
        }
        audit.Write(new AuditRecord(AuditActions.MpesaSettingsChanged, "MpesaAccount", account.Id.ToString(), null,
            new { tested = checks.Select(c => new { c.Name, c.Ok }) }));
        await db.SaveChangesAsync(ct);
        return checks;
    }

    /// <summary>For the terminal screens (any logged-in user).</summary>
    public async Task<PosMpesaDto> ForPos(MpesaReceived received, CancellationToken ct)
    {
        session.RequireUser();
        var training = session.Terminal?.Training == true;
        var s = await MpesaSettings.Load(db, secrets, ct);
        var setUp = s.SetUp && !training;
        return new PosMpesaDto(setUp, setUp && s.StkEnabled, setUp && s.StkEnabled && s.WaiterPrompts, !setUp || s.AllowTypedCodes,
            setUp ? await received.Count(ct) : 0);
    }
}
