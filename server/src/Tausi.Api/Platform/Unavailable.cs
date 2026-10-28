using System.Net.Sockets;
using Npgsql;

namespace Tausi.Api.Platform;

/// <summary>
/// When a request fails because the server can't do any work right now, not because of the request:
/// the database can't be reached (a restart, a 30-second outage) or the disk is full. Both answer 503
/// with a code and Retry-After, so terminals show "Server busy, retrying" instead of an error, and a
/// half-done write is rolled back by its transaction.
/// </summary>
public static class Unavailable
{
    public const string ServerBusy = "server_busy";
    public const string DiskFull = "disk_full";

    /// <summary>Retry-After, in seconds.</summary>
    public const int RetryAfter = 5;

    /// <summary><see cref="ServerBusy"/>, <see cref="DiskFull"/>, or null when the request itself failed.</summary>
    public static string? Reason(Exception exception)
    {
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                // PostgreSQL: 53100 disk_full; 53xxx out of resources; 57P0x shutting down; 08xxx connection.
                case PostgresException pg when pg.SqlState == "53100":
                    return DiskFull;
                case PostgresException pg when pg.SqlState.StartsWith("53", StringComparison.Ordinal)
                                               || pg.SqlState.StartsWith("57P0", StringComparison.Ordinal)
                                               || pg.SqlState.StartsWith("08", StringComparison.Ordinal):
                    return ServerBusy;
                case PostgresException:
                    return null;
                case NpgsqlException n when n.IsTransient:
                    return ServerBusy;
                // SQL Server: 1105 / 9002 a full data or log file; the rest are lost connections and timeouts.
                case Microsoft.Data.SqlClient.SqlException sql when sql.Number is 1105 or 9002:
                    return DiskFull;
                case Microsoft.Data.SqlClient.SqlException sql when sql.Number is -2 or 2 or 53 or 233 or 4060 or 10053 or 10054 or 10060 or 10061 or 11001:
                    return ServerBusy;
                case IOException io when IsDiskFull(io):
                    return DiskFull;
                case SocketException or TimeoutException:
                    return ServerBusy;
            }
        }
        return null;
    }

    /// <summary>ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL on Windows, ENOSPC on Linux.</summary>
    private static bool IsDiskFull(IOException e) => (e.HResult & 0xFFFF) is 0x70 or 0x27 || e.HResult == 28;
}
