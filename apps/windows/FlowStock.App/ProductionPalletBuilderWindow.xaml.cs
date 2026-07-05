using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace FlowStock.App;

/// <summary>
/// Серверно-авторитетный конструктор паллет. Редактируемая зона — только suggested-дельта
/// на текущую нехватку; существующий план и FILLED-история показываются read-only.
/// Локальная валидация лишь подсвечивает проблемы; окончательное решение — за confirm.
/// </summary>
public partial class ProductionPalletBuilderWindow : Window
{
    private static readonly System.Windows.Media.Brush PrimaryActionBrush =
        new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x15, 0x4A, 0xA3));
    private static readonly System.Windows.Media.Brush PrimaryActionForeground = System.Windows.Media.Brushes.White;

    private readonly AppServices _services;
    private readonly long _orderId;
    private ProductionPalletBuilderViewModel? _viewModel;
    private bool _isBusy;
    private bool _closingConfirmed;

    /// <summary>True после успешного сохранения дельты (вызывающему окну нужно перечитать заказ).</summary>
    public bool PlanChanged { get; private set; }

    public ProductionPalletBuilderWindow(AppServices services, long orderId)
    {
        _services = services;
        _orderId = orderId;
        InitializeComponent();
    }

    private async void ProductionPalletBuilderWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitialLoadAsync();
    }

    /// <summary>First load: builds the long-lived ViewModel from the server preview.</summary>
    private async Task InitialLoadAsync()
    {
        SetBusy(true);
        try
        {
            var result = await _services.WpfProductionPalletApi.TryGetPlanPreviewAsync(_orderId).ConfigureAwait(true);
            if (!result.IsSuccess || result.Preview == null)
            {
                ShowServerError(result.Message);
                return;
            }

            _viewModel = ProductionPalletBuilderViewModel.FromPreview(result.Preview, LoadPreviewAsync, Confirm);
            _viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(ProductionPalletBuilderViewModel.ValidationErrors)
                    or nameof(ProductionPalletBuilderViewModel.CanSave)
                    or nameof(ProductionPalletBuilderViewModel.ServerErrorMessage)
                    or nameof(ProductionPalletBuilderViewModel.IsDirty)
                    or nameof(ProductionPalletBuilderViewModel.RefreshIsPrimaryAction)
                    or nameof(ProductionPalletBuilderViewModel.OpenPlanTabHeader)
                    or nameof(ProductionPalletBuilderViewModel.HistoryTabHeader))
                {
                    RefreshStatusArea();
                }
            };

            SuggestedPalletsList.ItemsSource = _viewModel.SuggestedPallets;
            OpenPlanList.ItemsSource = _viewModel.OpenPlanPallets;
            HistoricalList.ItemsSource = _viewModel.HistoricalPallets;
            ServerErrorText.Visibility = Visibility.Collapsed;
            RefreshStatusArea();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private Task<WpfPalletPlanPreviewApiResult> LoadPreviewAsync(CancellationToken cancellationToken)
    {
        return _services.WpfProductionPalletApi.TryGetPlanPreviewAsync(_orderId, cancellationToken);
    }

    private static bool Confirm(string message)
    {
        return MessageBox.Show(message, "Конструктор паллет", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            == MessageBoxResult.Yes;
    }

    private void RefreshStatusArea()
    {
        if (_viewModel == null)
        {
            return;
        }

        HeaderText.Text = _viewModel.HeaderText;
        ValidationErrorsList.ItemsSource = _viewModel.ValidationErrors;

        DirtyBadgeText.Text = ProductionPalletBuilderViewModel.DirtyStatusText;
        DirtyBadge.Visibility = _viewModel.IsDirty ? Visibility.Visible : Visibility.Collapsed;

        OpenPlanTab.Header = _viewModel.OpenPlanTabHeader;
        HistoryTab.Header = _viewModel.HistoryTabHeader;
        UpdateEmptyState(OpenPlanScroll, OpenPlanEmptyText, _viewModel.HasOpenPlanPallets,
            ProductionPalletBuilderViewModel.OpenPlanEmptyText);
        UpdateEmptyState(HistoryScroll, HistoryEmptyText, _viewModel.HasHistoricalPallets,
            ProductionPalletBuilderViewModel.HistoryEmptyText);

        ApplyRefreshPrimaryStyle(_viewModel.RefreshIsPrimaryAction);
        ApplyBusyState();

        if (!string.IsNullOrWhiteSpace(_viewModel.ServerErrorMessage))
        {
            ShowServerError(_viewModel.ServerErrorMessage!);
        }
        else
        {
            ServerErrorText.Visibility = Visibility.Collapsed;
        }
    }

    private static void UpdateEmptyState(UIElement list, TextBlock emptyText, bool hasItems, string message)
    {
        emptyText.Text = message;
        emptyText.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        list.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyRefreshPrimaryStyle(bool isPrimary)
    {
        if (isPrimary)
        {
            RefreshButton.Background = PrimaryActionBrush;
            RefreshButton.Foreground = PrimaryActionForeground;
            RefreshButton.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            RefreshButton.ClearValue(BackgroundProperty);
            RefreshButton.ClearValue(ForegroundProperty);
            RefreshButton.FontWeight = FontWeights.Normal;
        }
    }

    private void ShowServerError(string message)
    {
        ServerErrorText.Text = message;
        ServerErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Single busy gate: mirrors into the ViewModel (so its mutators refuse) and disables the whole
    /// editable area, the action buttons and the close button while an async operation is in flight.
    /// </summary>
    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        _viewModel?.SetBusy(busy);
        ApplyBusyState();
    }

    private void ApplyBusyState()
    {
        var editable = !_isBusy;
        // Disabling the ItemsControl disables every editor inside it: qty boxes, remove/move buttons,
        // the remainder ComboBox and its add button — the whole suggested layout freezes at once.
        SuggestedPalletsList.IsEnabled = editable;
        AddPalletButton.IsEnabled = editable;
        RefreshButton.IsEnabled = editable;
        CloseButton.IsEnabled = editable;
        ResetButton.IsEnabled = editable && _viewModel?.CanResetToServerSuggestion == true;
        SaveButton.IsEnabled = editable && _viewModel?.CanSave == true;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingComponentQtyEdit();
        _viewModel?.RequestResetToServerSuggestion();
        RefreshStatusArea();
    }

    private void AddPallet_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.AddPallet();
        RefreshStatusArea();
    }

    private void RemovePallet_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null && sender is FrameworkElement { Tag: BuilderPalletViewModel pallet })
        {
            _viewModel.RemovePallet(pallet);
            RefreshStatusArea();
        }
    }

    private void RemoveComponent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: BuilderComponentViewModel component }
            && FindOwnerPallet(component) is { } pallet)
        {
            pallet.RemoveComponent(component.OrderLineId);
            RefreshStatusArea();
        }
    }

    private void ComponentQty_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox { Tag: BuilderComponentViewModel component } box
            && FindOwnerPallet(component) is { } pallet)
        {
            ApplyComponentQtyBox(box, component, pallet);
        }
    }

    /// <summary>
    /// Commits the text currently sitting in the focused ComponentQtyBox (OneWay-bound + applied on
    /// LostFocus) before an action or close, so a value typed but not yet blurred is not lost. Reuses
    /// the same parse/apply path as LostFocus; a following LostFocus with the same text is a no-op.
    /// </summary>
    private void CommitPendingComponentQtyEdit()
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox { Name: "ComponentQtyBox", Tag: BuilderComponentViewModel component } box
            && FindOwnerPallet(component) is { } pallet)
        {
            ApplyComponentQtyBox(box, component, pallet);
        }
    }

    private void ApplyComponentQtyBox(System.Windows.Controls.TextBox box, BuilderComponentViewModel component, BuilderPalletViewModel pallet)
    {
        if (!pallet.TryApplyComponentQtyText(component.OrderLineId, box.Text))
        {
            box.Text = component.Qty.ToString("0.###", CultureInfo.InvariantCulture);
        }

        RefreshStatusArea();
    }

    private void MoveQty_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null
            || sender is not FrameworkElement { Tag: BuilderComponentViewModel component } element
            || FindOwnerPallet(component) is not { } from)
        {
            return;
        }

        var row = (System.Windows.Controls.Panel)element.Parent!;
        var targetBox = row.Children.OfType<System.Windows.Controls.TextBox>().First(box => box.Name == "MoveTargetBox");
        var qtyBox = row.Children.OfType<System.Windows.Controls.TextBox>().First(box => box.Name == "MoveQtyBox");
        if (!int.TryParse(targetBox.Text.Trim(), out var targetNo)
            || _viewModel.SuggestedPallets.FirstOrDefault(pallet => pallet.TempNo == targetNo) is not { } to
            || ReferenceEquals(to, from))
        {
            MessageBox.Show("Укажите номер существующей другой паллеты.", "Конструктор паллет", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!BuilderPalletViewModel.TryParseComponentQty(qtyBox.Text, out var qty))
        {
            MessageBox.Show("Укажите количество к переносу.", "Конструктор паллет", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_viewModel.TryMoveQty(from, to, component.OrderLineId, qty, out var error))
        {
            MessageBox.Show(error, "Конструктор паллет", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        RefreshStatusArea();
    }

    private void AddSelectedRemainder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BuilderPalletViewModel pallet })
        {
            return;
        }

        if (!pallet.AddSelectedRemainder(out var error))
        {
            MessageBox.Show(error, "Конструктор паллет", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        RefreshStatusArea();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null)
        {
            await InitialLoadAsync();
            return;
        }

        CommitPendingComponentQtyEdit();
        SetBusy(true);
        try
        {
            await _viewModel.ReloadFromServerAsync().ConfigureAwait(true);
        }
        finally
        {
            SetBusy(false);
            RefreshStatusArea();
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null)
        {
            return;
        }

        CommitPendingComponentQtyEdit();
        if (!_viewModel.CanSave)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _services.WpfProductionPalletApi.TryConfirmExplicitPlanAsync(
                    _orderId,
                    _viewModel.PreviewFingerprint,
                    _viewModel.BuildConfirmRequestPallets())
                .ConfigureAwait(true);
            if (result.IsSuccess)
            {
                _viewModel.MarkSaved();
                PlanChanged = true;
                _closingConfirmed = true;
                // Opened via ShowDialog: assigning DialogResult closes the window exactly once.
                DialogResult = true;
                return;
            }

            if (result.Error != null)
            {
                // Structured errors: refresh-requiring codes emphasise "Обновить данные с сервера"
                // and keep Save disabled until a successful reload; the operator decides when to reload.
                _viewModel.ApplyServerError(result.Error);
            }
            else
            {
                ShowServerError(result.Message);
            }
        }
        finally
        {
            SetBusy(false);
            RefreshStatusArea();
        }
    }

    private void ProductionPalletBuilderWindow_Closing(object sender, CancelEventArgs e)
    {
        if (_closingConfirmed || _viewModel == null)
        {
            return;
        }

        CommitPendingComponentQtyEdit();
        if (!_viewModel.RequestClose())
        {
            e.Cancel = true;
            return;
        }

        _closingConfirmed = true;
    }

    private BuilderPalletViewModel? FindOwnerPallet(BuilderComponentViewModel component)
    {
        return _viewModel?.SuggestedPallets.FirstOrDefault(pallet => pallet.Components.Contains(component));
    }
}

/// <summary>Maps <c>true</c> to <see cref="Visibility.Collapsed"/> and <c>false</c> to visible.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility.Collapsed;
    }
}
