using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Npgsql;
using Tausi.Application.Backups;
using Tausi.Application.Common;
using Tausi.Domain.Common;

namespace Tausi.Infrastructure.Backups;

/// <summary>Backs up and restores the database itself, one provider each.</summary>
public interface IDatabaseDump
{
    string Provider { get; }

    /// <summary>The file name inside the .tausibak's database/ folder.</summary>
    string FileName { get; }

    /// <summary>Writes a backup of the database to <paramref name="file"/>, and checks it can be restored.</summary>
    Task Dump(string file, CancellationToken ct);

    /// <summary>Replaces the database with the backup in <paramref name="file"/>.</summary>
    Task Restore(string file, CancellationToken ct);
}

/// <summary>
/// PostgreSQL: <c>pg_dump -Fc</c> to stdout, checked with <c>pg_restore --list</c>. Restoring empties
/// the public schema and runs <c>pg_restore</c> from stdin, so it needs no right to create databases.
/// The tools are found on the PATH (or set Tausi:Backups:PgDump), and on Windows in
/// C:\Program Files\PostgreSQL\&lt;newest&gt;\bin.
/// </summary>
public sealed class PostgresDump(string connectionString, BackupOptions options) : IDatabaseDump
{
    public string Provider => "Postgres";
    public string FileName => "tausi.pgdump";

    public async Task Dump(string file, CancellationToken ct)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        await using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write))
            await Tool.Run(Find(options.PgDump), ["--format=custom", "--no-owner", "--no-privileges", .. ConnectionArgs(b)], Password(b), null, output, ct);
        await using var input = File.OpenRead(file);
        await Tool.Run(Find(options.PgRestore), ["--list"], null, input, Stream.Null, ct);
    }

    public async Task Restore(string file, CancellationToken ct)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        NpgsqlConnection.ClearAllPools();
        await using (var conn = new NpgsqlConnection(connectionString))
        {
            await conn.OpenAsync(ct);
            // Other sessions (the server's own background work) would block dropping the schema.
            await using (var kick = new NpgsqlCommand(
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid()", conn))
                await kick.ExecuteNonQueryAsync(ct);
            await using var reset = new NpgsqlCommand("DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;", conn);
            await reset.ExecuteNonQueryAsync(ct);
        }
        NpgsqlConnection.ClearAllPools();
        await using var input = File.OpenRead(file);
        await Tool.Run(Find(options.PgRestore),
            ["--no-owner", "--no-privileges", "--exit-on-error", "--single-transaction", .. ConnectionArgs(b, "--dbname")], Password(b), input, Stream.Null, ct);
    }

    private static string[] ConnectionArgs(NpgsqlConnectionStringBuilder b, string dbFlag = "--dbname") =>
    [
        "--host", b.Host ?? "localhost", "--port", b.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--username", b.Username ?? "postgres", dbFlag, b.Database ?? "postgres",
    ];

    private static Dictionary<string, string> Password(NpgsqlConnectionStringBuilder b) =>
        string.IsNullOrEmpty(b.Password) ? [] : new() { ["PGPASSWORD"] = b.Password };

    /// <summary>The tool as set, else on Windows the newest PostgreSQL's bin folder when it isn't on the PATH.</summary>
    private static string Find(string tool)
    {
        if (Path.IsPathRooted(tool) || !OperatingSystem.IsWindows()) return tool;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL");
        if (!Directory.Exists(root)) return tool;
        var newest = Directory.GetDirectories(root)
            .Select(d => (Dir: d, Version: int.TryParse(Path.GetFileName(d).Split('.')[0], out var v) ? v : 0))
            .OrderByDescending(x => x.Version).Select(x => Path.Combine(x.Dir, "bin", tool + ".exe")).FirstOrDefault(File.Exists);
        return newest ?? tool;
    }
}

/// <summary>
/// SQL Server: <c>BACKUP DATABASE … WITH CHECKSUM</c> (and COMPRESSION where the edition has it; Express
/// doesn't), checked with <c>RESTORE VERIFYONLY</c>. SQL Server reads and writes the .bak itself, through a
/// folder it can reach (<see cref="BackupOptions.SqlServerFolder"/>). Restoring puts the database in single-user
/// mode and restores over it, moving its files to the server's data folder.
/// </summary>
public sealed class SqlServerDump(string connectionString, BackupOptions options, string defaultFolder) : IDatabaseDump
{
    public string Provider => "SqlServer";
    public string FileName => "tausi.bak";

    private string FolderForSql => options.SqlServerFolder ?? defaultFolder;
    private string FolderHere => options.SqlServerFolderHere ?? options.SqlServerFolder ?? defaultFolder;

    public async Task Dump(string file, CancellationToken ct)
    {
        var name = $"tausi-{Guid.NewGuid():N}.bak";
        Directory.CreateDirectory(FolderHere);
        var sqlPath = SqlPath(name);
        var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        try
        {
            // EngineEdition 4 is Express, which can't compress backups.
            var edition = Convert.ToInt32(await Scalar(conn, "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int)", ct), System.Globalization.CultureInfo.InvariantCulture);
            var compress = edition != 4 ? ", COMPRESSION" : "";
            await Execute(conn, $"BACKUP DATABASE {Quote(database)} TO DISK = @path WITH CHECKSUM, INIT, FORMAT, COPY_ONLY{compress}", sqlPath, ct);
            await Execute(conn, "RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM", sqlPath, ct);
            var here = Path.Combine(FolderHere, name);
            if (Readable(here)) File.Move(here, file);
            else await CopyOut(conn, sqlPath, file, ct);
        }
        finally
        {
            TryDelete(Path.Combine(FolderHere, name));
        }
    }

    public async Task Restore(string file, CancellationToken ct)
    {
        var name = $"tausi-restore-{Guid.NewGuid():N}.bak";
        Directory.CreateDirectory(FolderHere);
        File.Copy(file, Path.Combine(FolderHere, name));
        var sqlPath = SqlPath(name);
        var builder = new SqlConnectionStringBuilder(connectionString);
        var database = builder.InitialCatalog;
        builder.InitialCatalog = "master";
        builder.Pooling = false;
        SqlConnection.ClearAllPools();
        try
        {
            await using var conn = new SqlConnection(builder.ConnectionString);
            await conn.OpenAsync(ct);
            var moves = await Moves(conn, sqlPath, database, ct);
            var exists = await Scalar(conn, $"SELECT DB_ID({Literal(database)})", ct) is not DBNull and not null;
            if (exists) await Execute(conn, $"ALTER DATABASE {Quote(database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE", null, ct);
            try
            {
                await Execute(conn, $"RESTORE DATABASE {Quote(database)} FROM DISK = @path WITH REPLACE, CHECKSUM, RECOVERY{moves}", sqlPath, ct, timeoutSeconds: 3600);
            }
            finally
            {
                if (await Scalar(conn, $"SELECT DB_ID({Literal(database)})", ct) is not DBNull and not null)
                    await Execute(conn, $"ALTER DATABASE {Quote(database)} SET MULTI_USER", null, ct);
            }
        }
        finally
        {
            TryDelete(Path.Combine(FolderHere, name));
            SqlConnection.ClearAllPools();
        }
    }

    private static bool Readable(string path)
    {
        try
        {
            using var _ = File.OpenRead(path);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// When this server can't read the file SQL Server wrote (it isn't on this PC, or SQL Server on Linux
    /// made it readable to itself only), SQL Server reads it back over the connection.
    /// </summary>
    private static async Task CopyOut(SqlConnection conn, string sqlPath, string file, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            $"SELECT BulkColumn FROM OPENROWSET(BULK {Literal(sqlPath)}, SINGLE_BLOB) AS b", conn) { CommandTimeout = 1800 };
        await using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, ct);
        if (!await reader.ReadAsync(ct)) throw new DomainException("SQL Server couldn't read its own backup back.", BackupCodes.Failed);
        await using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write);
        await using var stream = reader.GetStream(0);
        await stream.CopyToAsync(output, ct);
    }

    /// <summary>MOVE clauses that put the backup's files in this server's default folders, named after the database.</summary>
    private static async Task<string> Moves(SqlConnection conn, string sqlPath, string database, CancellationToken ct)
    {
        var dataFolder = await Scalar(conn, "SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(4000))", ct) as string;
        var logFolder = await Scalar(conn, "SELECT CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(4000))", ct) as string;
        if (string.IsNullOrEmpty(dataFolder)) return "";
        var files = new List<(string Logical, string Type)>();
        await using (var list = new SqlCommand("RESTORE FILELISTONLY FROM DISK = @path", conn))
        {
            list.Parameters.AddWithValue("@path", sqlPath);
            await using var reader = await list.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                files.Add((reader.GetString(reader.GetOrdinal("LogicalName")), reader.GetString(reader.GetOrdinal("Type"))));
        }
        var separator = dataFolder.Contains('\\') ? "\\" : "/";
        var parts = files.Select((f, i) =>
        {
            var folder = (f.Type == "L" ? logFolder ?? dataFolder : dataFolder).TrimEnd('\\', '/');
            var suffix = f.Type == "L" ? "_log.ldf" : i == 0 ? ".mdf" : $"_{i}.ndf";
            return $", MOVE {Literal(f.Logical)} TO {Literal(folder + separator + database + suffix)}";
        });
        return string.Concat(parts);
    }

    private string SqlPath(string name)
    {
        var folder = FolderForSql;
        var separator = folder.Contains('\\') ? "\\" : "/";
        return folder.TrimEnd('\\', '/') + separator + name;
    }

    private static async Task<object?> Scalar(SqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync(ct);
    }

    private static async Task Execute(SqlConnection conn, string sql, string? path, CancellationToken ct, int timeoutSeconds = 1800)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = timeoutSeconds };
        if (path is not null) cmd.Parameters.AddWithValue("@path", path);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
    private static string Literal(string value) => "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Runs a command-line tool with stdin and stdout connected to streams; its stderr becomes the error.</summary>
internal static class Tool
{
    public static async Task Run(string file, IReadOnlyList<string> args, IDictionary<string, string>? env, Stream? stdin, Stream stdout, CancellationToken ct)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) start.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (k, v) in env) start.Environment[k] = v;

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException($"{file} didn't start.");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new DomainException($"Couldn't run {Path.GetFileName(file)}: {e.Message}. Install the PostgreSQL client tools or set Tausi:Backups:PgDump.", BackupCodes.Failed);
        }
        using (process)
        {
            var errors = process.StandardError.ReadToEndAsync(ct);
            var output = process.StandardOutput.BaseStream.CopyToAsync(stdout, ct);
            if (stdin is not null)
            {
                try
                {
                    await stdin.CopyToAsync(process.StandardInput.BaseStream, ct);
                }
                catch (IOException)
                {
                    // The tool stopped reading (it failed); its error says why.
                }
                finally
                {
                    process.StandardInput.Close();
                }
            }
            await output;
            await process.WaitForExitAsync(ct);
            var error = (await errors).Trim();
            if (process.ExitCode != 0)
                throw new DomainException($"{Path.GetFileNameWithoutExtension(file)} failed: {(error.Length > 500 ? error[..500] : error)}", BackupCodes.Failed);
        }
    }
}
