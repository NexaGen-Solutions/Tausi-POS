using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Auth;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Domain.Common;
using Tausi.Domain.Setup;

namespace Tausi.Application.Setup;

/// <summary>Where the setup token is kept for the person installing: setup-token.txt in the data folder.</summary>
public interface ISetupTokenFile
{
    string Path { get; }
    string? Read();
    void Write(string token);
    void Delete();
}

/// <summary>
/// Whether the server is in setup mode, remembered between requests (setup is completed once).
/// Unknown until the database has been asked.
/// </summary>
public sealed class SetupGate
{
    private int _state = -1; // -1 unknown, 0 setting up, 1 complete

    public async Task<bool> IsComplete(ITausiDb db, CancellationToken ct)
    {
        var state = Volatile.Read(ref _state);
        if (state >= 0) return state == 1;
        var complete = await db.SetupStates.AsNoTracking().AnyAsync(s => s.CompletedAt != null, ct);
        Set(complete);
        return complete;
    }

    public void Set(bool complete) => Volatile.Write(ref _state, complete ? 1 : 0);

    /// <summary>Forget what is known (after a reset or a restore).</summary>
    public void Forget() => Volatile.Write(ref _state, -1);
}

public static class SetupCodes
{
    public const string Required = "setup_required";
    public const string Complete = "setup_complete";
    public const string WrongToken = "setup_token_wrong";
    public const string StepsMissing = "setup_steps_missing";
}

public sealed record SetupStepDto(string Key, string Status, string? Summary);

public sealed record SetupStatusDto(
    bool Complete, bool Unlocked, DateTimeOffset? LockedUntil, IReadOnlyList<SetupStepDto> Steps, string? Current, string? BusinessType,
    string? BusinessName);

/// <summary>
/// The first-run setup: the setup token (printed at start, in setup-token.txt), unlocking the wizard
/// (five wrong tries lock it for five minutes), each step's progress so it resumes after a reload,
/// and Finish. Every setup endpoint needs the browser's setup session from <see cref="Unlock"/>.
/// </summary>
public sealed class SetupFlow(
    ITausiDb db, SetupGate gate, ISetupTokenFile tokenFile, IAuditLog audit, IPosEvents events, TimeProvider clock)
{
    /// <summary>Letters and digits that can't be misread (no 0/O, 1/I/L).</summary>
    private const string TokenAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static string NewToken()
    {
        var chars = Enumerable.Range(0, 8).Select(_ => TokenAlphabet[RandomNumberGenerator.GetInt32(TokenAlphabet.Length)]).ToArray();
        return $"{new string(chars[..4])}-{new string(chars[4..])}";
    }

    /// <summary>"abcd efgh", "ABCD-EFGH" and "abcdefgh" are the same token.</summary>
    public static string Canonical(string? token)
    {
        var plain = new string((token ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        return plain.Length == 8 ? $"{plain[..4]}-{plain[4..]}" : plain;
    }

    /// <summary>
    /// At start-up in setup mode: the token to print. The one in setup-token.txt is kept while it is
    /// still the current one, so a restart doesn't change what the installer wrote down.
    /// </summary>
    public async Task<string?> EnsureToken(CancellationToken ct)
    {
        var state = await State(ct);
        if (state.IsComplete) return null;
        var existing = tokenFile.Read() is { } saved ? Canonical(saved) : null;
        if (existing is not null && state.TokenHash == SecureTokens.Hash(existing)) return existing;
        var token = NewToken();
        state.TokenHash = SecureTokens.Hash(token);
        state.TokenCreatedAt = clock.GetUtcNow();
        state.FailedAttempts = 0;
        await db.SaveChangesAsync(ct);
        tokenFile.Write(token);
        return token;
    }

    /// <summary>Checks the token; on success returns a new setup session for this browser.</summary>
    public async Task<string> Unlock(string? token, CancellationToken ct)
    {
        var state = await State(ct);
        if (state.IsComplete) throw new ConflictException("Setup is already complete.", SetupCodes.Complete);
        var now = clock.GetUtcNow();
        if (state.LockedUntil is { } until && until > now) throw new LoginLockedException(until);
        if (state.TokenHash is null || SecureTokens.Hash(Canonical(token)) != state.TokenHash)
        {
            state.FailedAttempts++;
            var left = SetupState.MaxTries - state.FailedAttempts;
            if (left <= 0)
            {
                state.FailedAttempts = 0;
                state.LockedUntil = now + SetupState.LockTime;
                await db.SaveChangesAsync(ct);
                throw new LoginLockedException(state.LockedUntil.Value);
            }
            await db.SaveChangesAsync(ct);
            throw new DomainException(
                $"That isn't the setup token. It's printed in the server's console and in {tokenFile.Path}. {left} tries left.",
                SetupCodes.WrongToken);
        }
        state.FailedAttempts = 0;
        state.LockedUntil = null;
        var session = SecureTokens.New();
        state.SessionHash = SecureTokens.Hash(session);
        await db.SaveChangesAsync(ct);
        return session;
    }

    /// <summary>The setup session is valid while setup is in progress.</summary>
    public async Task<bool> IsSession(string? session, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(session)) return false;
        var hash = SecureTokens.Hash(session);
        return await db.SetupStates.AsNoTracking().AnyAsync(s => s.CompletedAt == null && s.SessionHash == hash, ct);
    }

    public async Task RequireSession(string? session, CancellationToken ct)
    {
        var state = await State(ct);
        if (state.IsComplete) throw new ConflictException("Setup is already complete.", SetupCodes.Complete);
        if (string.IsNullOrEmpty(session) || state.SessionHash != SecureTokens.Hash(session))
            throw new UnauthenticatedException("Enter the setup token first.");
    }

    public async Task<SetupStatusDto> Status(string? session, CancellationToken ct)
    {
        var state = await State(ct);
        var unlocked = !state.IsComplete && !string.IsNullOrEmpty(session) && state.SessionHash == SecureTokens.Hash(session);
        var progress = unlocked ? await db.SetupProgress.AsNoTracking().ToListAsync(ct) : [];
        var steps = SetupSteps.All.Select(k =>
        {
            var p = progress.FirstOrDefault(x => x.Step == k);
            return new SetupStepDto(k, p is null ? "Todo" : p.Status.ToString(), p?.Summary);
        }).ToList();
        var lockedUntil = state.LockedUntil > clock.GetUtcNow() ? state.LockedUntil : null;
        var business = unlocked ? (await Settings.SettingsRegistry.Load(db, ct)).String(Settings.SettingsRegistry.BusinessName) : null;
        return new SetupStatusDto(state.IsComplete, unlocked, lockedUntil, steps, steps.FirstOrDefault(s => s.Status == "Todo")?.Key,
            unlocked ? state.BusinessType : null, string.IsNullOrEmpty(business) ? null : business);
    }

    /// <summary>Records a step as done (with what it did) or skipped; doing it again replaces the record.</summary>
    public async Task Mark(string step, SetupStepStatus status, string? summary, CancellationToken ct)
    {
        if (!SetupSteps.All.Contains(step)) throw new NotFoundException("No such step.");
        if (status == SetupStepStatus.Skipped && !SetupSteps.Skippable.Contains(step))
            throw new DomainException("This step can't be skipped.", "setup_not_skippable");
        var row = await db.SetupProgress.FirstOrDefaultAsync(p => p.Step == step, ct);
        if (row is null)
        {
            row = new SetupProgress { Step = step };
            db.SetupProgress.Add(row);
        }
        row.Status = status;
        row.Summary = summary is { Length: > 500 } ? summary[..500] : summary;
        row.At = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Finish: steps 1, 2 and 6 must be done. Setup is complete, the token is gone (and its file),
    /// the audit log says so, and terminals reload to the PIN screen.
    /// </summary>
    public async Task Finish(CancellationToken ct)
    {
        var state = await State(ct);
        if (state.IsComplete) throw new ConflictException("Setup is already complete.", SetupCodes.Complete);
        var done = await db.SetupProgress.Where(p => p.Status == SetupStepStatus.Done).Select(p => p.Step).ToListAsync(ct);
        var missing = new[] { SetupSteps.Business, SetupSteps.Admin, SetupSteps.Licence }.Where(s => !done.Contains(s)).ToList();
        if (missing.Count > 0)
            throw new DomainException($"Finish these steps first: {string.Join(", ", missing.Select(Title))}.", SetupCodes.StepsMissing);
        state.CompletedAt = clock.GetUtcNow();
        state.TokenHash = null;
        state.SessionHash = null;
        audit.Write(new AuditRecord(AuditActions.SetupCompleted, NewValue: new { steps = done }, Actor: SetupActor));
        await db.SaveChangesAsync(ct);
        gate.Set(true);
        tokenFile.Delete();
        await events.Publish(PosEventNames.SetupCompleted, new { at = state.CompletedAt }, ct);
    }

    public const string SetupActor = "setup";

    public static string Title(string step) => step switch
    {
        SetupSteps.Business => "Your business",
        SetupSteps.Admin => "First admin and PIN",
        SetupSteps.Devices => "Terminals and printers",
        SetupSteps.Menu => "Import the menu",
        SetupSteps.Stock => "Opening stock count",
        _ => "Licence key",
    };

    /// <summary>The setup row, made on first use (a fresh database has none).</summary>
    public async Task<SetupState> State(CancellationToken ct)
    {
        var state = await db.SetupStates.OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        if (state is not null) return state;
        state = new SetupState();
        db.SetupStates.Add(state);
        await db.SaveChangesAsync(ct);
        return state;
    }

    /// <summary>True while the wizard is running (for the setup-only allowances, such as approving your own opening count).</summary>
    public static async Task<bool> InProgress(ITausiDb db, CancellationToken ct) =>
        !await db.SetupStates.AsNoTracking().AnyAsync(s => s.CompletedAt != null, ct);
}
