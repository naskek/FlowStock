using System.ComponentModel;
using System.Windows;
using System.Windows.Data;

namespace FlowStock.App;

public partial class MixedPalletComponentFillWindow : Window
{
    private readonly MixedPalletComponentFillViewModel _viewModel;

    public MixedPalletComponentFillWindow(WpfProductionPalletDetail pallet)
    {
        InitializeComponent();
        _viewModel = new MixedPalletComponentFillViewModel(pallet);
        DataContext = _viewModel;

        var view = CollectionViewSource.GetDefaultView(_viewModel.Rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MixedPalletComponentFillRowViewModel.GroupHeader)));
    }

    public IReadOnlyList<long> SelectedComponentLineIds => _viewModel.SelectedComponentLineIds;

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanConfirm)
        {
            MessageBox.Show("Выберите хотя бы один компонент.", "Наполнение микс-паллеты", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
