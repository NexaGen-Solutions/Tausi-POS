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
/// retry after an error (or after getting an approval) runs again.
/// </summary>
public sealed class IdempotencyMiddleware(RequestDelegate next, IServiceScopeFactory scopes, TimeProvider clock)
{
    private const int MaxKeyLength = 100;
    private static readonly string[] Writes = [HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete];

    public async Task InvokeAsync(HttpContext http, IProblemDetailsService problems)
    {
        string? key = http.Request.Headers[TausiClaims.IdempotencyKeyHeader];
        if (string.IsNullOrEmpty(key) || !Writes.Contains(http.Request.Method) || !http.Request.Path.StartsWithSegments("/api"))
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
            await WriteProblem(http, problems, 409, "The first request with this Idempotency-Key is still being processed.");
            return;
        }

        http.Response.StatusCode = existing.ResponseStatus;
        http.Response.Headers["Idempotency-Replayed"] = "true";
        if (existing.ResponseContentType is not null) http.Response.ContentType = existing.ResponseContentType;
        if (existing.ResponseBody is not null) await http.Response.WriteAsync(existing.ResponseBody, http.RequestAborted);
    }

    private async Task RunAndStore(HttpContext http, TausiDbContext db, IdempotencyRecord record)
    {
        var original = http.Response.Body;
        await using var buffer = new MemoryStream();
        http.Response.Body = buffer;
        var stored = false;
        try
        {
            await next(http);

            if (http.Response.StatusCode is >= 200 and < 300)
            {
                record.Completed = true;
                record.ResponseStatus = http.Response.StatusCode;
                record.ResponseContentType = http.Response.ContentType;
                record.ResponseBody = Redact(http, Encoding.UTF8.GetString(buffer.ToArray()));
                await db.SaveChangesAsync(CancellationToken.None);
                stored = true;
            }
        }
        finally
        {
            http.Response.Body = original;
            if (!stored)
                await db.IdempotencyRecords.Where(r => r.Id == record.Id).ExecuteDeleteAsync(CancellationToken.None);
            buffer.Position = 0;
            await buffer.CopyToAsync(original, CancellationToken.None);
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

    private static async Task WriteProblem(HttpContext http, IProblemDetailsService problems, int status, string detail)
    {
        http.Response.StatusCode = status;
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = new ProblemDetails { Status = status, Title = "Idempotency-Key problem", Detail = detail },
        });
    }
}
