using System.Globalization;

namespace FlowStock.App;

internal sealed class CommercialStatisticsViewState
{
    private static readonly CultureInfo RussianCulture = CultureInfo.GetCultureInfo("ru-RU");

    private readonly int _pageSize;
    private long _nextRequestId;
    private long _activeRequestId;
    private int _offset;
    private int _totalCount;
    private int _currentItemCount;
    private string? _gtins;
    private string? _itemNameContains;
    private string? _volumes;
    private DateTime? _detailPeriodFrom;
    private DateTime? _detailPeriodTo;

    public CommercialStatisticsViewState(int pageSize)
    {
        _pageSize = pageSize;
    }

    public bool IsLoading { get; private set; }
    public string? DetailMonth { get; private set; }
    public DateTime? DetailPeriodFrom => _detailPeriodFrom;
    public DateTime? DetailPeriodTo => _detailPeriodTo;
    public string? Gtins => _gtins;
    public string? ItemNameContains => _itemNameContains;
    public string? Volumes => _volumes;
    public bool CanReturnToWholePeriod =>
        !IsLoading && !string.IsNullOrWhiteSpace(DetailMonth);
    public bool CanMovePrevious => !IsLoading && _offset > 0;
    public bool CanMoveNext => !IsLoading && _offset + _currentItemCount < _totalCount;

    public string RangeText =>
        _currentItemCount == 0
            ? $"0 из {_totalCount}"
            : $"{_offset + 1}–{_offset + _currentItemCount} из {_totalCount}";

    public string DetailLabel
    {
        get
        {
            if (!DateTime.TryParseExact(
                    DetailMonth,
                    "yyyy-MM",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var month))
            {
                return "Детализация за весь период";
            }

            return $"Детализация за {month.ToString("MMMM", RussianCulture)} {month.Year}";
        }
    }

    public void SetAdvancedFilters(
        string? gtins,
        string? itemNameContains,
        string? volumes = null)
    {
        _gtins = string.IsNullOrWhiteSpace(gtins) ? null : gtins.Trim();
        _itemNameContains = string.IsNullOrWhiteSpace(itemNameContains)
            ? null
            : itemNameContains.Trim();
        _volumes = string.IsNullOrWhiteSpace(volumes) ? null : volumes.Trim();
    }

    public CommercialStatisticsLoad StartLoad(WpfCommercialStatisticsFilters filters)
    {
        var requestFrom = DetailMonth is not null && _detailPeriodFrom.HasValue
            ? _detailPeriodFrom.Value
            : filters.From;
        var requestTo = DetailMonth is not null && _detailPeriodTo.HasValue
            ? _detailPeriodTo.Value
            : filters.To;

        DropDetailMonthOutsidePeriod(requestFrom, requestTo);
        if (DetailMonth is null)
        {
            requestFrom = filters.From;
            requestTo = filters.To;
        }

        _activeRequestId = ++_nextRequestId;
        IsLoading = true;
        return new CommercialStatisticsLoad(
            _activeRequestId,
            new WpfCommercialStatisticsRequest(
                filters.Mode,
                filters.GroupBy,
                requestFrom,
                requestTo,
                DetailMonth,
                filters.PartnerId,
                filters.ItemId,
                filters.Gtin,
                filters.Brand,
                filters.Volume,
                filters.Statuses,
                _pageSize,
                _offset,
                filters.Sort,
                Gtins: filters.Gtins ?? _gtins,
                ItemNameContains: filters.ItemNameContains ?? _itemNameContains,
                Volumes: _volumes));
    }

    public bool TryComplete(long requestId, WpfCommercialStatisticsResult result)
    {
        if (requestId != _activeRequestId)
        {
            return false;
        }

        IsLoading = false;
        _totalCount = Math.Max(0, result.Groups.TotalCount);
        _offset = Math.Max(0, result.Groups.Offset);
        _currentItemCount = result.Groups.Items.Count;
        return true;
    }

    public bool TryFail(long requestId)
    {
        if (requestId != _activeRequestId)
        {
            return false;
        }

        IsLoading = false;
        _currentItemCount = 0;
        _totalCount = 0;
        return true;
    }

    public bool MovePrevious()
    {
        if (!CanMovePrevious)
        {
            return false;
        }

        _offset = Math.Max(0, _offset - _pageSize);
        return true;
    }

    public bool MoveNext()
    {
        if (!CanMoveNext)
        {
            return false;
        }

        _offset += _pageSize;
        return true;
    }

    public void ResetOffset()
    {
        _offset = 0;
    }

    public void CriteriaChanged(bool periodChanged)
    {
        InvalidateActiveRequest();
        ResetOffset();
        _currentItemCount = 0;
        _totalCount = 0;
        if (periodChanged)
        {
            ClearDetailSelection();
        }
    }

    public void SelectDetailMonth(
        string? month,
        DateTime? periodFrom = null,
        DateTime? periodTo = null)
    {
        var normalized = string.IsNullOrWhiteSpace(month) ? null : month.Trim();
        var hadDetailSelection = !string.IsNullOrWhiteSpace(DetailMonth);

        if (string.Equals(DetailMonth, normalized, StringComparison.Ordinal))
        {
            if (!hadDetailSelection
                && periodFrom.HasValue
                && periodTo.HasValue)
            {
                _detailPeriodFrom = periodFrom.Value.Date;
                _detailPeriodTo = periodTo.Value.Date;
            }
            return;
        }

        InvalidateActiveRequest();
        DetailMonth = normalized;
        ResetOffset();

        if (normalized is null)
        {
            _detailPeriodFrom = null;
            _detailPeriodTo = null;
            return;
        }

        if (!hadDetailSelection
            && periodFrom.HasValue
            && periodTo.HasValue)
        {
            _detailPeriodFrom = periodFrom.Value.Date;
            _detailPeriodTo = periodTo.Value.Date;
        }
    }

    public bool ReturnToWholePeriod()
    {
        if (!CanReturnToWholePeriod)
        {
            return false;
        }

        SelectDetailMonth(null);
        return true;
    }

    private void InvalidateActiveRequest()
    {
        _activeRequestId = ++_nextRequestId;
        IsLoading = false;
    }

    private void DropDetailMonthOutsidePeriod(DateTime from, DateTime to)
    {
        if (string.IsNullOrWhiteSpace(DetailMonth))
        {
            return;
        }

        if (!DateTime.TryParseExact(
                DetailMonth,
                "yyyy-MM",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var detail)
            || detail.Date < new DateTime(from.Year, from.Month, 1)
            || detail.Date > new DateTime(to.Year, to.Month, 1))
        {
            ClearDetailSelection();
            ResetOffset();
        }
    }

    private void ClearDetailSelection()
    {
        DetailMonth = null;
        _detailPeriodFrom = null;
        _detailPeriodTo = null;
    }
}

internal sealed record WpfCommercialStatisticsFilters(
    string Mode,
    string GroupBy,
    DateTime From,
    DateTime To,
    long? PartnerId,
    long? ItemId,
    string? Gtin,
    string? Brand,
    string? Volume,
    string? Statuses,
    string Sort,
    string? Gtins = null,
    string? ItemNameContains = null);

internal sealed record CommercialStatisticsLoad(
    long RequestId,
    WpfCommercialStatisticsRequest Request);
