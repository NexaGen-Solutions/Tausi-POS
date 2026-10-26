using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Tausi.Infrastructure.Platform;

/// <summary>
/// Daily log files (tausi-20261025.log) in the log folder, kept for <see cref="KeepDays"/> days.
/// Lines are written by one background task, so logging never waits for the disk.
/// </summary>
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const int KeepDays = 30;

    private readonly string _folder;
    private readonly TimeProvider _clock;
    private readonly Channel<string> _lines = Channel.CreateBounded<string>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly Task _writer;

    public FileLoggerProvider(string folder, TimeProvider? clock = null)
    {
        _folder = folder;
        _clock = clock ?? TimeProvider.System;
        Directory.CreateDirectory(folder);
        _writer = Task.Run(Write);
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    internal void Add(string line) => _lines.Writer.TryWrite(line);

    internal DateTimeOffset Now => _clock.GetLocalNow();

    public static string FileName(DateOnly day) => $"tausi-{day:yyyyMMdd}.log";

    private async Task Write()
    {
        DateOnly? day = null;
        StreamWriter? file = null;
        try
        {
            while (await _lines.Reader.WaitToReadAsync())
            {
                var today = DateOnly.FromDateTime(Now.DateTime);
                if (day != today)
                {
                    if (file is not null) await file.DisposeAsync();
                    file = new StreamWriter(new FileStream(Path.Combine(_folder, FileName(today)), FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8);
                    day = today;
                    DeleteOld(_folder, today);
                }
                while (_lines.Reader.TryRead(out var line)) await file!.WriteAsync(line);
                await file!.FlushAsync();
            }
        }
        catch (IOException)
        {
            // A full or missing disk: stop writing files; the console and event log still work.
        }
        finally
        {
            if (file is not null) await file.DisposeAsync();
        }
    }

    /// <summary>Deletes log files older than <see cref="KeepDays"/> days.</summary>
    public static void DeleteOld(string folder, DateOnly today)
    {
        foreach (var path in Directory.EnumerateFiles(folder, "tausi-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(path)["tausi-".Length..];
            if (DateOnly.TryParseExact(name, "yyyyMMdd", out var day) && day < today.AddDays(-KeepDays))
            {
                try { File.Delete(path); }
                catch (IOException) { }
            }
        }
    }

    public void Dispose()
    {
        _lines.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var text = new StringBuilder()
                .Append(provider.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append(" [").Append(Level(logLevel)).Append("] ")
                .Append(category).Append(": ")
                .Append(formatter(state, exception))
                .AppendLine();
            if (exception is not null) text.Append(exception).AppendLine();
            provider.Add(text.ToString());
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            _ => "CRT",
        };
    }
}
