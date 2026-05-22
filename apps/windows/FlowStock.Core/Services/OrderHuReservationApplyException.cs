namespace FlowStock.Core.Services;

public sealed class OrderHuReservationApplyException : Exception
{
    public OrderHuReservationApplyException(string errorCode, string message, IReadOnlyList<string>? problems = null)
        : base(message)
    {
        ErrorCode = errorCode;
        Problems = problems ?? Array.Empty<string>();
    }

    public string ErrorCode { get; }

    public IReadOnlyList<string> Problems { get; }
}
