using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.Edge;

/// <summary>
/// The logger factory Edge's own Kestrel and YARP get: Jellyfin's, with the framework's categories let through at
/// Warning and above only. YARP logs every request at Information, which would flood Jellyfin's log, and the inner
/// host logged a bind failure with a stack trace ahead of Trailhead's own line (spike T-S1, points 2 and 6).
/// </summary>
/// <remarks>Never disposes Jellyfin's factory: Edge's container is disposed on every stop, Jellyfin's isn't.</remarks>
internal sealed class EdgeLoggerFactory : ILoggerFactory
{
    private static readonly string[] Quiet = { "Microsoft.Extensions.Hosting", "Microsoft.AspNetCore", "Yarp" };

    private readonly ILoggerFactory _inner;

    internal EdgeLoggerFactory(ILoggerFactory inner) => _inner = inner;

    internal static bool IsQuiet(string category)
        => Quiet.Any(q => category.Equals(q, StringComparison.Ordinal) || category.StartsWith(q + ".", StringComparison.Ordinal));

    public ILogger CreateLogger(string categoryName)
    {
        var logger = _inner.CreateLogger(categoryName);
        return IsQuiet(categoryName) ? new AtLeast(logger, LogLevel.Warning) : logger;
    }

    public void AddProvider(ILoggerProvider provider)
    {
        // Edge adds no providers; Jellyfin's are the ones that write.
    }

    public void Dispose()
    {
    }

    private sealed class AtLeast : ILogger
    {
        private readonly ILogger _inner;
        private readonly LogLevel _min;

        internal AtLeast(ILogger inner, LogLevel min)
        {
            _inner = inner;
            _min = min;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => _inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _min && _inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= _min)
            {
                _inner.Log(logLevel, eventId, state, exception, formatter);
            }
        }
    }
}
