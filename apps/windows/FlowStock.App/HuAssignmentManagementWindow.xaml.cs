using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace FlowStock.App;

public partial class HuAssignmentManagementWindow : Window
{
    private const string WindowTitleText = "Управление привязками HU";
    private readonly HuAssignmentManagementController _controller;
    private bool _allowClose;
    private bool _isSelectingItem;

    public HuAssignmentManagementWindow(AppServices services)
        : this(new HuAssignmentManagementController(
            new WpfHuAssignmentManagementApiClient(services.WpfReadApi),
            ex => services.AppLogger.Error("HU assignment management window failed.", ex)))
    {
    }

    internal HuAssignmentManagementWindow(HuAssignmentManagementController controller)
    {
        _controller = controller;
        InitializeComponent();
        DataContext = _controller;
    }

    private void HuAssignmentManagementWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_controller.LoadInitial(out var message) && !string.IsNullOrWhiteSpace(message))
        {
            MessageBox.Show(message, WindowTitleText, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        SyncItemSelection();
        RefreshViews();
    }

    private void ItemsSearch_Click(object sender, RoutedEventArgs e)
    {
        if (!TryConfirmDiscardChanges("Поиск товара сбросит несохранённые изменения. Продолжить?"))
        {
            return;
        }

        if (!_controller.LoadItems(out var message))
        {
            ShowInfo(message);
            return;
        }

        if (_controller.Items.Count > 0)
        {
            _controller.SelectItem(_controller.Items[0], discardStagedChanges: true, out _);
        }

        SyncItemSelection();
        RefreshViews();
    }

    private void ItemsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSelectingItem || ItemsCombo.SelectedItem is not WpfHuBindingManageItemRow item)
        {
            return;
        }

        var discard = !HasUnsavedChanges() || TryConfirmDiscardChanges("Смена товара сбросит несохранённые изменения. Продолжить?");
        if (!_controller.SelectItem(item, discard, out var message))
        {
            ShowInfo(message);
            SyncItemSelection();
            return;
        }

        RefreshViews();
    }

    private void StateFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StateFilterCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _controller.StateFilter = tag;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var discard = !HasUnsavedChanges() || TryConfirmDiscardChanges("Обновление сбросит несохранённые изменения. Продолжить?");
        if (!_controller.RefreshCurrent(discard, out var message))
        {
            ShowInfo(message);
        }

        RefreshViews();
    }

    private void HuSearch_Click(object sender, RoutedEventArgs e)
    {
        if (!_controller.SearchCurrent(out var message))
        {
            ShowInfo(message);
        }

        RefreshViews();
    }

    private void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        if (!_controller.PreviousPage(out var message))
        {
            ShowInfo(message);
        }

        RefreshViews();
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (!_controller.NextPage(out var message))
        {
            ShowInfo(message);
        }

        RefreshViews();
    }

    private void HuGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _controller.SelectHu(HuGrid.SelectedItem as HuAssignmentManagementHuItem);
    }

    private void TargetsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _controller.SelectTargetLine(TargetsGrid.SelectedItem as HuAssignmentManagementTargetLineItem);
    }

    private void HuGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_controller.CanBind)
        {
            Bind_Click(sender, e);
        }
    }

    private void Bind_Click(object sender, RoutedEventArgs e)
    {
        if (!_controller.BindSelected(out var message))
        {
            ShowInfo(message);
        }

        RefreshViews();
    }

    private void Move_Click(object sender, RoutedEventArgs e)
    {
        if (!_controller.MoveSelected(out var message))
        {
            ShowInfo(message);
        }

        RefreshViews();
    }

    private void Detach_Click(object sender, RoutedEventArgs e)
    {
        if (!_controller.DetachSelected(out var message))
        {
            ShowInfo(message);
        }

        RefreshViews();
    }

    private void CancelHuChange_Click(object sender, RoutedEventArgs e)
    {
        if (!_controller.CancelSelectedChange(out var message))
        {
            ShowInfo(message);
        }

        RefreshViews();
    }

    private void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        _controller.ResetAllChanges();
        RefreshViews();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        try
        {
            var result = _controller.Save();
            RefreshViews();
            switch (result.Outcome)
            {
                case HuAssignmentManagementSaveOutcome.Success:
                    MessageBox.Show(result.Message, WindowTitleText, MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                case HuAssignmentManagementSaveOutcome.StaleReloaded:
                    MessageBox.Show(
                        "Данные изменились на сервере. Состояние будет обновлено.",
                        WindowTitleText,
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    break;
                case HuAssignmentManagementSaveOutcome.NetworkFailure:
                    MessageBox.Show(result.Message, WindowTitleText, MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
                case HuAssignmentManagementSaveOutcome.NoChanges:
                    MessageBox.Show(result.Message, WindowTitleText, MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                default:
                    MessageBox.Show(result.Message, WindowTitleText, MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
            }
        }
        finally
        {
            SaveButton.IsEnabled = _controller.CanSave;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!TryConfirmClose())
        {
            return;
        }

        _allowClose = true;
        Close();
    }

    private void HuAssignmentManagementWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !HasUnsavedChanges())
        {
            return;
        }

        if (!TryConfirmClose())
        {
            e.Cancel = true;
            return;
        }

        _allowClose = true;
    }

    private bool TryConfirmClose()
    {
        if (!HasUnsavedChanges())
        {
            return true;
        }

        return MessageBox.Show(
            "Есть несохранённые изменения привязок HU. Закрыть окно и отменить их?",
            WindowTitleText,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private bool TryConfirmDiscardChanges(string message)
    {
        if (!HasUnsavedChanges())
        {
            return true;
        }

        return MessageBox.Show(
            message,
            WindowTitleText,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private bool HasUnsavedChanges() => _controller.HasStagedChanges;

    private void ShowInfo(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        MessageBox.Show(message, WindowTitleText, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SyncItemSelection()
    {
        _isSelectingItem = true;
        try
        {
            ItemsCombo.SelectedItem = _controller.SelectedItem;
        }
        finally
        {
            _isSelectingItem = false;
        }
    }

    private void RefreshViews()
    {
        CollectionViewSource.GetDefaultView(HuGrid.ItemsSource)?.Refresh();
        CollectionViewSource.GetDefaultView(TargetsGrid.ItemsSource)?.Refresh();
        CollectionViewSource.GetDefaultView(ChangesGrid.ItemsSource)?.Refresh();
        SyncItemSelection();
    }
}
