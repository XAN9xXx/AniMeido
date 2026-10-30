using Microsoft.Extensions.Logging;

namespace AniMeido.Tests;

internal sealed class ServiceTestLogger<T> : ILogger<T>
{
    private readonly List<LogLevel> _levels = [];
    internal int Count(LogLevel level) { lock (_levels) return _levels.Count(value => value == level); }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_levels) _levels.Add(logLevel);
    }
}
