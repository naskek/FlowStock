using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;

namespace FlowStock.App;

public static class WpfLiveRefreshGuard
{
    public static bool IsDataGridEditing(DataGrid grid)
    {
        var view = CollectionViewSource.GetDefaultView(grid.ItemsSource) as IEditableCollectionView;
        return view?.IsEditingItem == true || view?.IsAddingNew == true;
    }
}
