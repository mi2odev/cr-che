using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Creche.Infrastructure.Logging;

/// <summary>Writes one log file per day (creche-yyyyMMdd.log) in the logs folder and prunes old files.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _folder;
    private readonly LogLevel _minimum;
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public FileLoggerProvider(string folder, LogLevel minimum = LogLevel.Information, int keepDays = 30)
    {
        _folder = folder;
        _minimum = minimum;
        Directory.CreateDirectory(folder);
        Prune(keepDays);
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    public void Dispose() => _loggers.Clear();

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var now = DateTime.Now;
        string line = string.Create(CultureInfo.InvariantCulture, $"{now:HH:mm:ss.fff} [{Short(level)}] {category}: {message}");
        if (exception is not null) line += Environment.NewLine + exception;
        string path = Path.Combine(_folder, $"creche-{now:yyyyMMdd}.log");
        try
        {
            lock (_gate) File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (IOException) { /* logging must never crash the app */ }
        catch (UnauthorizedAccessException) { }
    }

    private void Prune(int keepDays)
    {
        try
        {
            var limit = DateTime.Now.AddDays(-keepDays);
            foreach (var file in Directory.EnumerateFiles(_folder, "creche-*.log"))
                if (File.GetLastWriteTime(file) < limit) File.Delete(file);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Short(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "---",
    };

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= provider._minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}
