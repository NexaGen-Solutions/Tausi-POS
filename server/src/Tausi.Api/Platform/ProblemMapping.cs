using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tausi.Api.Auth;
using Tausi.Application.Auth;
using Tausi.Application.Common;
using Tausi.Application.Security;
using Tausi.Domain.Common;

namespace Tausi.Api.Platform;

/// <summary>
/// Turns exceptions into one consistent problem-details JSON. Permission errors carry the
/// permission key; "needs approval" adds <c>approvalRequired: true</c> so the UI opens the approval pad.
/// </summary>
public sealed class ProblemMapping(IProblemDetailsService problems, ILogger<ProblemMapping> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        var problem = exception switch
        {
            ApprovalRequiredException e => Problem(403, "Approval required", e.Message, ("approvalRequired", true), ("permission", e.PermissionKey)),
            PermissionDeniedException e => Problem(403, "Not allowed", e.Message, ("approvalRequired", false), ("permission", e.PermissionKey)),
            LoginLockedException e => Locked(http, e),
            LoginRejectedException e => Problem(401, "Login failed", e.Message, ("attemptsLeft", e.AttemptsLeft)),
            UnauthenticatedException when AuthSetup.SessionExpired(http) =>
                Problem(401, "Session expired", "You were logged out. Enter your PIN again.", ("code", TausiClaims.SessionExpiredCode)),
            UnauthenticatedException e => Problem(401, "Not signed in", e.Message),
            NotFoundException e => Problem(404, "Not found", e.Message),
            Tausi.Application.Day.OpenTicketsException e => Problem(409, "Conflict", e.Message, ("code", e.Code), ("openTickets", e.Tickets), ("openShifts", e.Shifts)),
            FieldErrorsException e => Problem(400, "Invalid request", e.Message, ("code", "invalid_fields"), ("fieldErrors", e.Errors)),
            ConflictException { Code: { } code } e => Problem(409, "Conflict", e.Message, ("code", code)),
            ConflictException e => Problem(409, "Conflict", e.Message),
            ForbiddenException e => Problem(403, "Not allowed", e.Message, ("approvalRequired", false), ("code", e.Code)),
            DbUpdateConcurrencyException => Problem(409, "Conflict", "Someone else changed this at the same time. Reload and try again."),
            DomainException { Code: { } code } e => Problem(400, "Invalid request", e.Message, ("code", code)),
            DomainException e => Problem(400, "Invalid request", e.Message),
            BadHttpRequestException e => Problem(400, "Invalid request", e.Message),
            _ when Unavailable.Reason(exception) is { } reason => Busy(http, reason),
            _ => null,
        };
        if (problem is null)
        {
            log.LogError(exception, "Unhandled error on {Method} {Path}", http.Request.Method, http.Request.Path);
            problem = Problem(500, "Server error", "Something went wrong on the server. Try again.");
        }

        http.Response.StatusCode = problem.Status!.Value;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem, Exception = exception });
    }

    /// <summary>503: the database can't be reached, or the disk is full. Nothing was half-saved (each write is one transaction).</summary>
    private ProblemDetails Busy(HttpContext http, string reason)
    {
        http.Response.Headers.RetryAfter = Unavailable.RetryAfter.ToString();
        log.LogWarning("{Method} {Path} answered 503: {Reason}", http.Request.Method, http.Request.Path, reason);
        return reason == Unavailable.DiskFull
            ? Problem(503, "Disk full", "The server's disk is full, so nothing can be saved. Free some space on the server PC, or call NexaGen.", ("code", reason))
            : Problem(503, "Server busy", "The server can't reach its database. Retrying…", ("code", reason));
    }

    /// <summary>A refusal the server gave on purpose (a 4xx), as opposed to a fault while it worked.</summary>
    public static bool IsRefusal(Exception exception) => exception is ApprovalRequiredException or PermissionDeniedException
        or LoginLockedException or LoginRejectedException or UnauthenticatedException or NotFoundException
        or Tausi.Application.Day.OpenTicketsException or FieldErrorsException or ConflictException or ForbiddenException
        or DbUpdateConcurrencyException or DomainException or BadHttpRequestException;

    private static ProblemDetails Locked(HttpContext http, LoginLockedException e)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling((e.Until - DateTimeOffset.UtcNow).TotalSeconds));
        http.Response.Headers.RetryAfter = seconds.ToString();
        return Problem(429, "Locked", e.Message, ("lockedUntil", e.Until), ("retryAfterSeconds", seconds));
    }

    private static ProblemDetails Problem(int status, string title, string detail, params (string Key, object? Value)[] extensions)
    {
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        foreach (var (key, value) in extensions) problem.Extensions[key] = value;
        return problem;
    }
}
