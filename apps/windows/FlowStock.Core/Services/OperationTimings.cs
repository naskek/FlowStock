using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace FlowStock.Core.Services;

public sealed class OperationTimings : IDisposable
{
    private static readonly AsyncLocal<OperationTimings?> Current = new();
    private readonly OperationTimings? _previous;
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly Dictionary<string, double> _stages = new();
    public double ElapsedMs => _total.Elapsed.TotalMilliseconds;
    public IReadOnlyDictionary<string, double> Stages => _stages;
    public string Summary => string.Join(" ", _stages.Select(pair =>
        $"{pair.Key}_ms={pair.Value.ToString("0.###", CultureInfo.InvariantCulture)}"));

    public OperationTimings()
    {
        _previous = Current.Value;
        Current.Value = this;
    }

    public static IDisposable? Measure(string stage) => Current.Value is { } owner ? new Stage(owner, stage) : null;
    public void Dispose() => Current.Value = _previous;

    private sealed class Stage(OperationTimings owner, string name) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner._stages.TryGetValue(name, out var previous);
            owner._stages[name] = previous + Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        }
    }
}
