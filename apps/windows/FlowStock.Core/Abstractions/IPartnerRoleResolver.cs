using FlowStock.Core.Models;

namespace FlowStock.Core.Abstractions;

public interface IPartnerRoleResolver
{
    bool IsCustomer(Partner partner);
}
