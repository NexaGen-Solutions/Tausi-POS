namespace Tausi.Infrastructure;

/// <summary>Config section "Tausi". Relative paths are under the server's content root.</summary>
public sealed class TausiOptions
{
    public const string Section = "Tausi";
    public const string ConnectionStringName = "Tausi";

    /// <summary>Uploaded files and generated server secrets live here. Back it up with the database.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>Daily log files, kept 30 days. Empty: console only (development).</summary>
    public string? LogDirectory { get; set; }

    /// <summary>The .tausibak backups. Not inside the data directory, which they back up.</summary>
    public string BackupDirectory { get; set; } = "backups";

    /// <summary>The installed config folder (Windows: C:\ProgramData\Tausi POS\config), copied into each backup.</summary>
    public string? ConfigDirectory { get; set; }

    /// <summary>Optional base64 secrets. When empty they are generated once into the data directory.</summary>
    public string? PinPepper { get; set; }
    public string? JwtSigningKey { get; set; }

    public PrintingOptions Printing { get; set; } = new();

    public BackupOptions Backups { get; set; } = new();

    public UpdateOptions Updates { get; set; } = new();
}

public sealed class BackupOptions
{
    /// <summary>Off in tests: no nightly backup.</summary>
    public bool Nightly { get; set; } = true;

    /// <summary>PostgreSQL's tools; give the full path when they aren't on the PATH.</summary>
    public string PgDump { get; set; } = "pg_dump";
    public string PgRestore { get; set; } = "pg_restore";

    /// <summary>
    /// SQL Server writes and reads its .bak files itself, so they go through a folder both can reach:
    /// as SQL Server sees it, and as this server sees it. Both default to &lt;backups&gt;/work.
    /// </summary>
    public string? SqlServerFolder { get; set; }
    public string? SqlServerFolderHere { get; set; }
}

public sealed class UpdateOptions
{
    /// <summary>NexaGen's update feed. Checked only when the owner presses Check for updates.</summary>
    public string FeedUrl { get; set; } = "https://updates.nexagensolutions.com/tausi/updates.json";

    /// <summary>Where the server keeps the terminal update it offers (the installer puts it there).</summary>
    public string TerminalFolder { get; set; } = "updates/terminal";
}

public enum PrintTransportMode
{
    /// <summary>Send to real printers by each printer's connection mode.</summary>
    Real,

    /// <summary>Development: write every job as text into <see cref="PrintingOptions.OutputDirectory"/>.</summary>
    File,
}

public sealed class PrintingOptions
{
    public PrintTransportMode Transport { get; set; } = PrintTransportMode.Real;
    public string OutputDirectory { get; set; } = "print-output";
    public int NetworkTimeoutSeconds { get; set; } = 5;
}
