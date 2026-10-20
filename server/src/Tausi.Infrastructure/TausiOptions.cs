namespace Tausi.Infrastructure;

/// <summary>Config section "Tausi". Relative paths are under the server's content root.</summary>
public sealed class TausiOptions
{
    public const string Section = "Tausi";

    /// <summary>Uploaded files and generated server secrets live here. Back it up with the database.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>Optional base64 secrets. When empty they are generated once into the data directory.</summary>
    public string? PinPepper { get; set; }
    public string? JwtSigningKey { get; set; }

    public PrintingOptions Printing { get; set; } = new();
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
