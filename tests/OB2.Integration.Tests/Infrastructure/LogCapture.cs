using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OB2.Integration.Tests.Infrastructure;

/// <summary>Keeps every log line an engine writes, so a test can see what it decided (D-90).</summary>
public sealed class LogCapture : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public static (ILoggerFactory Factory, LogCapture Capture) Create()
    {
        var capture = new LogCapture();
        return (LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(capture)), capture);
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private sealed class Logger(LogCapture owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Lines.Enqueue(formatter(state, exception));
    }
}
