using System.Windows.Threading;

namespace FlowStock.App.Services;

public sealed class WpfLiveRefreshCoordinator : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(300);
    private readonly object _sync = new();
    private readonly WpfLiveUpdateClient _client;
    private readonly FileLogger _logger;
    private readonly List<Subscription> _subscriptions = new();
    private CancellationTokenSource? _debounce;
    private Dispatcher? _dispatcher;
    private bool _refreshInProgress;
    private bool _trailingRefreshPending;

    public WpfLiveRefreshCoordinator(WpfLiveUpdateClient client, FileLogger logger)
    {
        _client = client;
        _logger = logger;
        _client.Changed += OnInvalidated;
        _client.ResyncRequired += OnInvalidated;
    }

    public IDisposable Register(Func<bool> canRefresh, Action refresh, Action markPending)
    {
        var subscription = new Subscription(this, canRefresh, refresh, markPending);
        lock (_sync)
        {
            _subscriptions.Add(subscription);
        }
        return subscription;
    }

    public void Start(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _client.Start();
    }

    public void Dispose()
    {
        _client.Changed -= OnInvalidated;
        _client.ResyncRequired -= OnInvalidated;
        lock (_sync)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = null;
            _subscriptions.Clear();
        }
        _client.Dispose();
    }

    private void OnInvalidated(object? sender, EventArgs e)
    {
        CancellationToken token;
        lock (_sync)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = new CancellationTokenSource();
            token = _debounce.Token;
        }
        _ = DebounceAsync(token);
    }

    private async Task DebounceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DebounceDelay, cancellationToken).ConfigureAwait(false);
            var dispatcher = _dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
            {
                return;
            }
            await dispatcher.InvokeAsync(RefreshVisible, DispatcherPriority.Background, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A newer live event replaced this refresh.
        }
    }

    private void RefreshVisible()
    {
        if (_refreshInProgress)
        {
            _trailingRefreshPending = true;
            return;
        }

        _refreshInProgress = true;
        try
        {
            Subscription[] subscriptions;
            lock (_sync)
            {
                subscriptions = _subscriptions.ToArray();
            }

            foreach (var subscription in subscriptions)
            {
                if (subscription.IsDisposed)
                {
                    continue;
                }

                try
                {
                    if (subscription.CanRefresh())
                    {
                        subscription.Refresh();
                    }
                    else
                    {
                        subscription.MarkPending();
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error("WPF live read refresh failed", ex);
                }
            }
        }
        finally
        {
            _refreshInProgress = false;
            if (_trailingRefreshPending)
            {
                _trailingRefreshPending = false;
                RefreshVisible();
            }
        }
    }

    private void Unregister(Subscription subscription)
    {
        lock (_sync)
        {
            _subscriptions.Remove(subscription);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly WpfLiveRefreshCoordinator _owner;
        private readonly Func<bool> _canRefresh;
        private readonly Action _refresh;
        private readonly Action _markPending;

        public Subscription(
            WpfLiveRefreshCoordinator owner,
            Func<bool> canRefresh,
            Action refresh,
            Action markPending)
        {
            _owner = owner;
            _canRefresh = canRefresh;
            _refresh = refresh;
            _markPending = markPending;
        }

        public bool IsDisposed { get; private set; }

        public bool CanRefresh() => _canRefresh();

        public void Refresh() => _refresh();

        public void MarkPending() => _markPending();

        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }
            IsDisposed = true;
            _owner.Unregister(this);
        }
    }
}
