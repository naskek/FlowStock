using FlowStock.Core.Models;

namespace FlowStock.App;

public sealed class IncomingRequestOrderApiBridgeService
{
    private readonly WpfIncomingRequestsApiService _incomingRequestsApi;

    public IncomingRequestOrderApiBridgeService(
        SettingsService settings,
        FileLogger logger,
        WpfIncomingRequestsApiService incomingRequestsApi)
    {
        _ = settings;
        _ = logger;
        _incomingRequestsApi = incomingRequestsApi;
    }

    public bool CanHandle(string? requestType) =>
        string.Equals(requestType, OrderRequestType.CreateOrder, StringComparison.OrdinalIgnoreCase)
        || string.Equals(requestType, OrderRequestType.SetOrderStatus, StringComparison.OrdinalIgnoreCase);

    public async Task<IncomingRequestOrderApprovalResult> ApproveAsync(
        OrderRequest request,
        string resolvedBy,
        CancellationToken cancellationToken = default)
    {
        if (!CanHandle(request.RequestType))
        {
            return IncomingRequestOrderApprovalResult.Failure(
                IncomingRequestOrderApprovalResultKind.UnsupportedRequest,
                $"Неподдерживаемый тип заявки: {request.RequestType}");
        }

        var confirmed = await _incomingRequestsApi
            .TryConfirmOrderRequestAsync(request.Id, resolvedBy, cancellationToken)
            .ConfigureAwait(false);
        return confirmed
            ? IncomingRequestOrderApprovalResult.Success(null, "Заявка подтверждена сервером.")
            : IncomingRequestOrderApprovalResult.Failure(
                IncomingRequestOrderApprovalResultKind.ValidationFailed,
                "Сервер отклонил подтверждение заявки.");
    }
}

public sealed class IncomingRequestOrderApprovalResult
{
    public IncomingRequestOrderApprovalResultKind Kind { get; init; }
    public string Message { get; init; } = string.Empty;
    public long? AppliedOrderId { get; init; }
    public Exception? Exception { get; init; }

    public bool IsSuccess => Kind == IncomingRequestOrderApprovalResultKind.Approved;

    public static IncomingRequestOrderApprovalResult Success(long? appliedOrderId, string message) => new()
    {
        Kind = IncomingRequestOrderApprovalResultKind.Approved,
        AppliedOrderId = appliedOrderId,
        Message = message
    };

    public static IncomingRequestOrderApprovalResult Failure(
        IncomingRequestOrderApprovalResultKind kind,
        string message,
        Exception? exception = null) => new()
    {
        Kind = kind,
        Message = message,
        Exception = exception
    };
}

public enum IncomingRequestOrderApprovalResultKind
{
    Approved,
    ValidationFailed,
    NotFound,
    UnsupportedRequest,
    ServerRejected,
    ServerUnavailable,
    Timeout,
    InvalidConfiguration,
    InvalidResponse,
    UnexpectedError
}
