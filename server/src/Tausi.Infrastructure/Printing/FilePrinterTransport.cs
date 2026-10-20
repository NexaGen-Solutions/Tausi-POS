using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Tausi.Application.Printing;
using Tausi.Domain.Devices;
using Tausi.Domain.Printing;

namespace Tausi.Infrastructure.Printing;

/// <summary>
/// Development only: writes each job as readable text into the print-output folder, so slip
/// layouts can be checked without a printer. Used for printers whose connection is File, and for
/// every printer when Tausi:Printing:Transport = File.
/// </summary>
public sealed class FilePrinterTransport(IOptions<TausiOptions> options, IHostEnvironment env, TimeProvider clock)
    : IPrinterTransport
{
    public PrinterConnection? Mode => PrinterConnection.File;

    public string OutputDirectory { get; } =
        Path.GetFullPath(options.Value.Printing.OutputDirectory, env.ContentRootPath);

    public async Task Send(Printer printer, PrintJob job, CancellationToken ct)
    {
        Directory.CreateDirectory(OutputDirectory);
        var safeName = string.Concat(printer.Name.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        var stamp = clock.GetLocalNow().ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(OutputDirectory, $"{stamp}-{safeName}-{job.Kind}-{job.Id.ToString("N")[..8]}.txt");
        var header = $"# {printer.Name} ({printer.ConnectionMode}, {printer.PaperWidthMm} mm) · {job.Kind} · job {job.Id}{Environment.NewLine}";
        await File.WriteAllTextAsync(path, header + job.TextPreview, ct);
    }
}
