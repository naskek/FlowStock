using LightWms.Core.Models;

namespace LightWms.Core.Abstractions;

public interface IHuRegistryUpdater
{
    bool TryApplyImportEvent(
        ImportEvent importEvent,
        Doc? doc,
        Item? item,
        Location? fromLocation,
        Location? toLocation,
        bool itemResolved,
        out string? error);
}
