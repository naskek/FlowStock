namespace FlowStock.Core.Services;

public sealed class CommercialTermsException : InvalidOperationException
{
    public CommercialTermsException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
