using System.Net.Sockets;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Tausi.Api.Platform;

namespace Tausi.Tests.Platform;

/// <summary>Task 16, Part C: the database down for a while, or a full disk, answers a clear 503 the terminals retry.</summary>
public class UnavailableTests
{
    private static PostgresException Postgres(string sqlState) => new("message", "ERROR", "ERROR", sqlState);

    [Theory]
    [InlineData("08006", Unavailable.ServerBusy)] // connection failure
    [InlineData("57P01", Unavailable.ServerBusy)] // the server is shutting down
    [InlineData("53300", Unavailable.ServerBusy)] // too many connections
    [InlineData("53100", Unavailable.DiskFull)]
    [InlineData("23505", null)] // a unique key: the request's own fault, not the server's
    public void Postgres_errors_say_whether_the_server_is_unavailable(string sqlState, string? reason)
    {
        Unavailable.Reason(new InvalidOperationException("wrapped", Postgres(sqlState))).Should().Be(reason);
    }

    [Fact]
    public void Lost_connections_timeouts_and_full_disks_are_recognised_and_ordinary_errors_are_not()
    {
        Unavailable.Reason(new Exception("x", new SocketException((int)SocketError.ConnectionRefused))).Should().Be(Unavailable.ServerBusy);
        Unavailable.Reason(new TimeoutException()).Should().Be(Unavailable.ServerBusy);
        Unavailable.Reason(new IOException("No space left on device", 28)).Should().Be(Unavailable.DiskFull);
        Unavailable.Reason(new IOException("disk full", unchecked((int)0x80070070))).Should().Be(Unavailable.DiskFull);
        Unavailable.Reason(new InvalidOperationException("a bug")).Should().BeNull();
        Unavailable.Reason(new IOException("file not found", unchecked((int)0x80070002))).Should().BeNull();
    }

    [Theory]
    [InlineData("08006", "server_busy")]
    [InlineData("53100", "disk_full")]
    public async Task The_API_answers_503_with_the_code_and_Retry_After(string sqlState, string code)
    {
        var written = new CapturingProblems();
        var http = new DefaultHttpContext();
        var handled = await new ProblemMapping(written, NullLogger<ProblemMapping>.Instance)
            .TryHandleAsync(http, new Microsoft.EntityFrameworkCore.DbUpdateException("save failed", Postgres(sqlState)), CancellationToken.None);

        handled.Should().BeTrue();
        http.Response.StatusCode.Should().Be(503);
        http.Response.Headers.RetryAfter.ToString().Should().Be("5");
        written.Problem!.Extensions["code"].Should().Be(code);
    }

    private sealed class CapturingProblems : IProblemDetailsService
    {
        public ProblemDetails? Problem { get; private set; }

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            Problem = context.ProblemDetails;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
        {
            Problem = context.ProblemDetails;
            return ValueTask.FromResult(true);
        }
    }
}
