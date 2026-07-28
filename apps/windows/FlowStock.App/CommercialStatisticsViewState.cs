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

    public CommercialStatisticsViewState(int pageSize)
    {
        _pageSize = pageSize;
    }

    public bool IsLoading { get; private set; }
    public string? DetailMonth { get; private set; }
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

    public CommercialStatisticsLoad StartLoad(WpfCommercialStatisticsFilters filters)
    {
        DropDetailMonthOutsidePeriod(filters.From, filters.To);
        _activeRequestId = ++_nextRequestId;
        IsLoading = true;
        return new CommercialStatisticsLoad(
            _activeRequestId,
            new WpfCommercialStatisticsRequest(
                filters.Mode,
                filters.GroupBy,
                filters.From,
                filters.To,
                DetailMonth,
                filters.PartnerId,
                filters.ItemId,
                filters.Gtin,
                filters.Brand,
                filters.Volume,
                filters.Statuses,
                _pageSize,
                _offset,
                filters.Sort));
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
            DetailMonth = null;
        }
    }

    public void SelectDetailMonth(string? month)
    {
        var normalized = string.IsNullOrWhiteSpace(month) ? null : month.Trim();
        if (string.Equals(DetailMonth, normalized, StringComparison.Ordinal))
        {
            return;
        }

        InvalidateActiveRequest();
        DetailMonth = normalized;
        ResetOffset();
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
            DetailMonth = null;
            ResetOffset();
        }
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
    string Sort);

internal sealed record CommercialStatisticsLoad(
    long RequestId,
    WpfCommercialStatisticsRequest Request);
