using System.Windows;

namespace FlowStock.App;

public interface IMixedPalletComponentFillDialogFactory
{
    bool TrySelectComponents(Window owner, WpfProductionPalletDetail pallet, out IReadOnlyList<long> componentLineIds);
}

public sealed class MixedPalletComponentFillDialogFactory : IMixedPalletComponentFillDialogFactory
{
    public bool TrySelectComponents(Window owner, WpfProductionPalletDetail pallet, out IReadOnlyList<long> componentLineIds)
    {
        var window = new MixedPalletComponentFillWindow(pallet)
        {
            Owner = owner
        };

        var confirmed = window.ShowDialog() == true;
        componentLineIds = confirmed
            ? window.SelectedComponentLineIds
            : Array.Empty<long>();
        return confirmed;
    }
}
