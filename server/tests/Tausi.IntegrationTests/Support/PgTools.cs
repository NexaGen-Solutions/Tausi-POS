namespace Tausi.IntegrationTests.Support;

/// <summary>
/// pg_dump and pg_restore for the backup tests: the installed ones (CI has PostgreSQL 16's), else
/// small scripts that run them in the postgres:16 image (needs Docker, which the tests use anyway).
/// </summary>
public static class PgTools
{
    public static string Dump { get; } = Find("pg_dump");
    public static string Restore { get; } = Find("pg_restore");

    private static string Find(string tool)
    {
        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, tool)).FirstOrDefault(File.Exists);
        if (onPath is not null || OperatingSystem.IsWindows()) return onPath ?? tool;

        var dir = Path.Combine(Path.GetTempPath(), "tausi-pgtools");
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, tool);
        File.WriteAllText(script, $"#!/bin/sh\nexec docker run --rm -i --network host -e PGPASSWORD postgres:16 {tool} \"$@\"\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }
}
