namespace FlowStock.App;

internal sealed class CommercialStatisticsAutoRefreshCoordinator
{
    private long _revision;
    private long? _pendingRevision;

    public long Schedule()
    {
        _pendingRevision = ++_revision;
        return _revision;
    }

    public bool TryConsume(out long revision)
    {
        if (!_pendingRevision.HasValue)
        {
            revision = 0;
            return false;
        }

        revision = _pendingRevision.Value;
        _pendingRevision = null;
        return true;
    }

    public void Cancel()
    {
        _pendingRevision = null;
    }
}
