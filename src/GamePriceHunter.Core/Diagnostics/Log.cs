using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace GamePriceHunter.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public interface IAppLogger
{
    void Log(LogLevel level, string message, Exception? exception = null);
}

public static class AppLoggerExtensions
{
    public static void Debug(this IAppLogger logger, string message) => logger.Log(LogLevel.Debug, message);

    public static void Info(this IAppLogger logger, string message) => logger.Log(LogLevel.Info, message);

    public static void Warn(this IAppLogger logger, string message, Exception? exception = null) => logger.Log(LogLevel.Warning, message, exception);

    public static void Error(this IAppLogger logger, string message, Exception? exception = null) => logger.Log(LogLevel.Error, message, exception);
}

public sealed class NullLogger : IAppLogger
{
    public static NullLogger Instance { get; } = new();

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
    }
}

/// <summary>
/// Daily rolling file logger. Writes on a background thread so callers never block on disk I/O.
/// </summary>
public sealed class FileLogger : IAppLogger, IDisposable
{
    private readonly string _directory;
    private readonly LogLevel _minLevel;
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 10_000);
    private readonly Thread _writer;

    public FileLogger(string directory, LogLevel minLevel = LogLevel.Info, int keepDays = 7)
    {
        _directory = directory;
        _minLevel = minLevel;
        Directory.CreateDirectory(directory);
        DeleteOldFiles(keepDays);
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "FileLogger" };
        _writer.Start();
    }

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        if (level < _minLevel)
            return;

        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" [").Append(level.ToString().ToUpperInvariant()).Append("] ")
            .Append(message);
        if (exception is not null)
            line.AppendLine().Append(exception);

        _queue.TryAdd(line.ToString());
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
    }

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                var path = Path.Combine(_directory, $"app-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Logging must never take the app down.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void DeleteOldFiles(int keepDays)
    {
        try
        {
            var threshold = DateTime.Now.AddDays(-keepDays);
            foreach (var file in Directory.EnumerateFiles(_directory, "app-*.log"))
            {
                if (File.GetLastWriteTime(file) < threshold)
                    File.Delete(file);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
