using System.Collections.Concurrent;
using System.Text;

public static class FileLoggingExtensions
{
    public static ILoggingBuilder AddFile(this ILoggingBuilder builder, string logsDirectory)
    {
        builder.Services.AddSingleton<ILoggerProvider>(_ => new FileLoggerProvider(logsDirectory));
        return builder;
    }
}

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly BlockingCollection<string> _queue = new();
    private readonly Task _writerTask;
    private readonly string _logsDirectory;

    public FileLoggerProvider(string logsDirectory)
    {
        _logsDirectory = logsDirectory;
        Directory.CreateDirectory(_logsDirectory);
        _writerTask = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(categoryName, _queue);
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        _queue.Dispose();
    }

    private async Task WriteLoopAsync()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            var path = Path.Combine(_logsDirectory, $"hello-docker-mcp-{DateTimeOffset.Now:yyyyMMdd}.log");
            await File.AppendAllTextAsync(path, line + Environment.NewLine, new UTF8Encoding(false));
        }
    }
}

internal sealed class FileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly BlockingCollection<string> _queue;

    public FileLogger(string categoryName, BlockingCollection<string> queue)
    {
        _categoryName = categoryName;
        _queue = queue;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel >= LogLevel.Information;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        if (string.IsNullOrWhiteSpace(message) && exception is null)
        {
            return;
        }

        var line = $"{DateTimeOffset.Now:O} [{logLevel}] {_categoryName}: {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        if (!_queue.IsAddingCompleted)
        {
            _queue.Add(line);
        }
    }
}
