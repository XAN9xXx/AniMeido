using Microsoft.Extensions.Logging;

namespace AniMeido.PluginHost;

/// <summary>插件使用日志抽象；文件策略和进程标识仍由 Host 负责。</summary>
internal sealed class HostLogger<T> : ILogger<T>
{
    public HostLogger()
    {
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    
    public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Warning and < LogLevel.None;

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

        var message = $"{typeof(T).FullName}: {formatter(state, exception)}";
        switch (logLevel)
        {
            case LogLevel.Critical:
                HostLog.Fatal(message, exception);
                break;
            case LogLevel.Error:
                HostLog.Error(message, exception);
                break;
            default:
                HostLog.Warning(message, exception);
                break;
        }
    }
}
