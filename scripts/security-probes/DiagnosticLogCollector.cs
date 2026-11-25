using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SecurityRegression;

public sealed class DiagnosticLogCollector : ILoggerProvider, ISupportExternalScope
{
    public sealed record Entry(string Code, string Text, string Scope, bool HasException);
    public readonly ConcurrentQueue<Entry> Entries = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public ILogger CreateLogger(string categoryName) => new Capture(this);
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
    public void Dispose() { }

    private sealed class Capture(DiagnosticLogCollector owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);
        public bool IsEnabled(LogLevel level) => level != LogLevel.None;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> values ||
                values.FirstOrDefault(pair => pair.Key == "DiagnosticCode").Value is not string code || !code.StartsWith("AN", StringComparison.Ordinal)) return;
            var scopes = new List<string>();
            owner._scopes.ForEachScope((scope, target) => target.Add(scope?.ToString() ?? ""), scopes);
            owner.Entries.Enqueue(new Entry(code, formatter(state, exception), string.Join(" ", scopes), exception is not null));
        }
    }
}
