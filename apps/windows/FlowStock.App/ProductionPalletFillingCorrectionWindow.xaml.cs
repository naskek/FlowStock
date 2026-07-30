using System.Windows;

namespace FlowStock.App;

public partial class ProductionPalletFillingCorrectionWindow : Window
{
    private readonly AppServices _services;
    private WpfHuCorrectionPreviewResult? _preview;
    private Guid? _requestId;
    private string? _requestPayload;
    private long? _sourcePrdDocId;
    private long? _corDocId;
    private long? _replacementPrdDocId;

    public ProductionPalletFillingCorrectionWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
        HuTextBox.Focus();
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            _preview = await _services.WpfProductionPalletApi.TryGetFillingCorrectionPreviewAsync(HuTextBox.Text);
            _requestId = null;
            _requestPayload = null;
            if (!_preview.IsSuccess)
            {
                MessageBox.Show(_preview.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            HuTextBox.Text = _preview.HuCode;
            ActionText.Text = _preview.Action switch
            {
                "CORRECT_FILLED" => "Действие: CORRECT_FILLED — COR + replacement pallet",
                "RESET_PARTIAL" => "Действие: RESET_PARTIAL — полный сброс mixed HU",
                _ => "Коррекция недоступна"
            };
            SourceText.Text = $"Source PRD: {_preview.SourcePrdRef ?? "—"} (ID {_preview.SourcePrdDocId?.ToString() ?? "—"})";
            MarkingText.Text = $"Кодов маркировки: {_preview.MarkingCodeCount}";
            LedgerText.Text = _preview.LedgerInversion.Count == 0
                ? "Ledger-инверсия: не требуется"
                : "Ledger-инверсия: " + string.Join(
                    "; ",
                    _preview.LedgerInversion.Select(line =>
                        $"item {line.ItemId}, location {line.LocationId}, {line.HuCode}: {line.SourceQty:g} → {line.CorrectionQty:g}"));
            BlockersText.Text = _preview.Blockers.Count == 0
                ? string.Empty
                : string.Join(Environment.NewLine, _preview.Blockers.Select(blocker => $"{blocker.Code}: {blocker.Message}"));
            ComponentsGrid.ItemsSource = _preview.Components;
            _sourcePrdDocId = _preview.SourcePrdDocId;
            OpenSourceButton.IsEnabled = _sourcePrdDocId.HasValue;
            ConfirmButton.IsEnabled = _preview.CanConfirm;
            await LoadHistoryAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_preview?.CanConfirm != true || string.IsNullOrWhiteSpace(_preview.Action))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(ReasonTextBox.Text))
        {
            MessageBox.Show("Укажите причину корректировки.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var normalizedReason = ReasonTextBox.Text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var payload = $"{_preview.HuCode}\n{_preview.Action}\n{normalizedReason}";
        if (!string.Equals(payload, _requestPayload, StringComparison.Ordinal))
        {
            _requestId = Guid.NewGuid();
            _requestPayload = payload;
        }

        SetBusy(true);
        try
        {
            var result = await _services.WpfProductionPalletApi.TryConfirmFillingCorrectionAsync(
                _requestId!.Value,
                _preview.HuCode,
                _preview.Action,
                normalizedReason);
            if (!result.IsSuccess)
            {
                MessageBox.Show(
                    $"{result.Message}\n\nПри сетевом timeout повторите подтверждение: будет использован тот же request_id.",
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _sourcePrdDocId = result.SourcePrdDocId;
            _corDocId = result.CorDocId;
            _replacementPrdDocId = result.ReplacementPrdDocId;
            OpenSourceButton.IsEnabled = _sourcePrdDocId.HasValue;
            OpenCorButton.IsEnabled = _corDocId.HasValue;
            OpenReplacementButton.IsEnabled = _replacementPrdDocId.HasValue;
            ConfirmButton.IsEnabled = false;
            MessageBox.Show(
                result.Replay ? $"{result.Message}\nВозвращён сохранённый результат." : result.Message,
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            await LoadHistoryAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void History_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            await LoadHistoryAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadHistoryAsync()
    {
        var hu = string.IsNullOrWhiteSpace(_preview?.HuCode) ? HuTextBox.Text : _preview.HuCode;
        HistoryGrid.ItemsSource = await _services.WpfProductionPalletApi.TryGetFillingCorrectionHistoryAsync(hu);
    }

    private void HistoryGrid_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not WpfHuCorrectionHistoryEntry entry)
        {
            return;
        }

        _sourcePrdDocId = entry.SourcePrdDocId;
        _corDocId = entry.CorDocId;
        _replacementPrdDocId = entry.ReplacementPrdDocId;
        OpenSourceButton.IsEnabled = _sourcePrdDocId.HasValue;
        OpenCorButton.IsEnabled = _corDocId.HasValue;
        OpenReplacementButton.IsEnabled = _replacementPrdDocId.HasValue;
    }

    private void OpenSource_Click(object sender, RoutedEventArgs e) => OpenDocument(_sourcePrdDocId);
    private void OpenCor_Click(object sender, RoutedEventArgs e) => OpenDocument(_corDocId);
    private void OpenReplacement_Click(object sender, RoutedEventArgs e) => OpenDocument(_replacementPrdDocId);

    private void OpenDocument(long? docId)
    {
        if (!docId.HasValue)
        {
            return;
        }
        new OperationDetailsWindow(_services, docId.Value) { Owner = this }.ShowDialog();
    }

    private void SetBusy(bool busy)
    {
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
        ConfirmButton.IsEnabled = !busy && _preview?.CanConfirm == true && _corDocId == null;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
