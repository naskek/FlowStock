using FlowStock.Core.Models;

namespace FlowStock.Core.Abstractions;

public interface IOrderStatusDiagnosticsStore
{
    IReadOnlyList<FullyShippedCustomerOrderStatusCandidate> GetFullyShippedCustomerOrderStatusCandidates();

    IReadOnlyList<CustomerReadinessOrderStatusCandidate> GetCustomerReadinessOrderStatusCandidates();
}
