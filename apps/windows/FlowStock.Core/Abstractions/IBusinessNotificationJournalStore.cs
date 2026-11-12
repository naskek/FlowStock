using FlowStock.Core.Models;

namespace FlowStock.Core.Abstractions;

public interface IBusinessNotificationJournalStore
{
    BusinessNotificationPage GetBusinessNotificationPage(BusinessNotificationQuery query, string readerKey);
}
