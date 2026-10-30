using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tausi.Api.Auth;
using Tausi.Application.Security;
using Tausi.Domain.Platform;
using Tausi.Infrastructure.Persistence;

namespace Tausi.Api.Platform;

/// <summary>
/// Honours the <c>Idempotency-Key</c> header on API writes (POST, PUT, PATCH, DELETE).
/// The first request runs and a successful (2xx) answer is stored for 24 hours; repeats get that
/// answer back without running again. The same key with a different body, endpoint or caller is a
/// 422. A repeat while the first is still running is a 409. Failed requests are not stored, so a
/// retry after an error (or after getting an approval) runs again. A request whose changes committed
/// keeps its key even if it then fails or its answer can't be stored, so it never runs twice: a repeat
/// is a 409 with the code <c>already_done</c>. An unfinished key that committed nothing can be run again
/// after <see cref="StaleAfter"/> (its first run ended without clearing it, in a stall or a crash).
/// </summary>
public sealed class IdempotencyMiddleware(
    RequestDelegate next, IServiceScopeFactory scopes, TimeProvider clock, ILogger<IdempotencyMiddleware> log)
{
    private const int MaxKeyLength = 100;
    private const int StoreAttempts = 3;
    public const string AlreadyDoneCode = "already_done";
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);
    private static readonly string[] Writes = [HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete];

    public async Task InvokeAsync(HttpContext http, IProblemDetailsService problems)
    {
        string? key = http.Request.Headers[TausiClaims.IdempotencyKeyHeader];
        if (string.IsNullOrEmpty(key) || !Writes.Contains(http.Request.Method) || !http.Request.Path.StartsWithSegments("/api")
            || http.GetEndpoint()?.Metadata.GetMetadata<ReadOnlyPost>() is not null)
        {
            await next(http);
            return;
        }
        if (key.Length > MaxKeyLength)
        {
            await WriteProblem(http, problems, 400, "The Idempotency-Key header is too long.");
            return;
        }

        var caller = http.User.FindFirst(TausiClaims.UserId)?.Value
            ?? http.Features.Get<DeviceTerminal>()?.TerminalId.ToString()
            ?? "anonymous";
        var endpoint = $"{http.Request.Method} {http.Request.Path}";
        var requestHash = await HashBody(http.Request);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TausiDbContext>();

        var existing = await db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Key == key, http.RequestAborted);
        if (existing is null)
        {
            var record = new IdempotencyRecord
            {
                Key = key,
                Caller = caller,
                Endpoint = endpoint,
                RequestHash = requestHash,
                CreatedAt = clock.GetUtcNow(),
            };
            db.IdempotencyRecords.Add(record);
            try
            {
                await db.SaveChangesAsync(http.RequestAborted);
                await RunAndStore(http, db, record);
                return;
            }
            catch (DbUpdateException)
            {
                // Another request with this key got in first.
                db.ChangeTracker.Clear();
                existing = await db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Key == key, http.RequestAborted);
                if (existing is null) throw;
            }
        }

        if (existing.Caller != caller || existing.Endpoint != endpoint || existing.RequestHash != requestHash)
        {
            await WriteProblem(http, problems, 422, "This Idempotency-Key was already used for a different request.");
            return;
        }
        if (!existing.Completed)
        {
            if (existing.Ran)
            {
                await WriteProblem(http, problems, 409, "That already went through. Check the screen before doing it again.", AlreadyDoneCode);
                return;
            }
            var now = clock.GetUtcNow();
            if (now - existing.CreatedAt > StaleAfter && await Claim(db, existing, now, http.RequestAborted))
            {
                db.Attach(existing);
                await RunAndStore(http, db, existing);
                return;
            }
            await WriteProblem(http, problems, 409, "The first request with this Idempotency-Key is still being processed.");
            return;
        }

        http.Response.StatusCode = existing.ResponseStatus;
        http.Response.Headers["Idempotency-Replayed"] = "true";
        if (existing.ResponseContentType is not null) http.Response.ContentType = existing.ResponseContentType;
        if (existing.ResponseBody is not null) await http.Response.WriteAsync(existing.ResponseBody, http.RequestAborted);
    }

    /// <summary>Takes over a stale key; only one repeat can.</summary>
    private static async Task<bool> Claim(TausiDbContext db, IdempotencyRecord record, DateTimeOffset now, CancellationToken ct)
    {
        var was = record.CreatedAt;
        var claimed = await db.IdempotencyRecords.Where(r => r.Id == record.Id && r.CreatedAt == was && !r.Ran && !r.Completed)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.CreatedAt, now), ct);
        record.CreatedAt = now;
        return claimed == 1;
    }

    private async Task RunAndStore(HttpContext http, TausiDbContext db, IdempotencyRecord record)
    {
        http.RequestServices.GetService<IdempotencyMark>()?.RecordId = record.Id;
        var original = http.Response.Body;
        await using var buffer = new MemoryStream();
        http.Response.Body = buffer;
        var keep = false;
        var fault = false;
        try
        {
            await next(http);

            // Only answers the store can replay as text are kept: JSON or nothing (a 204). A file is passed on as it is.
            var type = http.Response.ContentType;
            if (http.Response.StatusCode is >= 200 and < 300 && (type is null || type.Contains("json", StringComparison.OrdinalIgnoreCase)))
            {
                // The action is done: from here the key is never given up, or a retry would run it again.
                keep = true;
                record.Completed = true;
                record.ResponseStatus = http.Response.StatusCode;
                record.ResponseContentType = http.Response.ContentType;
                record.ResponseBody = Redact(http, Encoding.UTF8.GetString(buffer.ToArray()));
                await Store(db, record);
            }
            fault = !keep && http.Response.StatusCode >= 500;
        }
        catch (Exception e) when (!ProblemMapping.IsRefusal(e))
        {
            fault = true;
            throw;
        }
        finally
        {
            http.Response.Body = original;
            if (!keep) await Release(db, record, fault);
            buffer.Position = 0;
            await buffer.CopyToAsync(original, CancellationToken.None);
        }
    }

    /// <summary>
    /// Gives the key up so a retry runs again, unless a fault came after the request's changes committed.
    /// In a stall even this can fail: the key then stays, and <see cref="StaleAfter"/> frees it if nothing ran.
    /// </summary>
    private async Task Release(TausiDbContext db, IdempotencyRecord record, bool fault)
    {
        try
        {
            if (fault && await db.IdempotencyRecords.AsNoTracking().Where(r => r.Id == record.Id).Select(r => r.Ran).FirstOrDefaultAsync(CancellationToken.None))
                return;
            await db.IdempotencyRecords.Where(r => r.Id == record.Id).ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (Exception e) when (fault)
        {
            log.LogWarning(e, "Couldn't check the key of a failed request to {Endpoint}; it stays until it goes stale", record.Endpoint);
        }
    }

    /// <summary>
    /// Keeps the answer of a request that ran. If the database won't take it (a stall), the key stays
    /// unfinished: the caller still gets the answer, and a repeat is a 409 instead of a second run.
    /// </summary>
    private async Task Store(TausiDbContext db, IdempotencyRecord record)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(CancellationToken.None);
                return;
            }
            catch (Exception e) when (attempt < StoreAttempts)
            {
                log.LogWarning(e, "Couldn't store the answer to {Endpoint} (attempt {Attempt}); trying again", record.Endpoint, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt));
            }
            catch (Exception e)
            {
                log.LogError(e, "Couldn't store the answer to {Endpoint}; a repeat of key {Key} will be refused (409)", record.Endpoint, record.Key);
                return;
            }
        }
    }

    private static string Redact(HttpContext http, string body)
    {
        var redact = http.GetEndpoint()?.Metadata.GetMetadata<IdempotencyRedact>();
        if (redact is null || JsonNode.Parse(body) is not JsonObject json) return body;
        foreach (var field in redact.Fields)
            if (json.ContainsKey(field)) json[field] = null;
        return json.ToJsonString();
    }

    private static async Task<string> HashBody(HttpRequest request)
    {
        request.EnableBuffering();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, request.HttpContext.RequestAborted)) > 0)
            sha.AppendData(chunk, 0, read);
        request.Body.Position = 0;
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static async Task WriteProblem(HttpContext http, IProblemDetailsService problems, int status, string detail, string? code = null)
    {
        http.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = "Idempotency-Key problem", Detail = detail };
        if (code is not null) problem.Extensions["code"] = code;
        await problems.WriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem });
    }
}
