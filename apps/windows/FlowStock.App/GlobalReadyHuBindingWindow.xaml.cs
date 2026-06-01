using System.ComponentModel;
using System.Windows;

namespace FlowStock.App;

public partial class GlobalReadyHuBindingWindow : Window
{
    private readonly AppServices _services;
    private readonly WpfReadyHuBindingReadModel? _initialReadModel;
    private GlobalReadyHuBindingSession? _session;
    private GlobalReadyHuCandidateItem? _selectedHu;
    private GlobalReadyHuCompatibleLineItem? _selectedLine;
    private bool _allowClose;

    public GlobalReadyHuBindingWindow(AppServices services, WpfReadyHuBindingReadModel? initialReadModel)
    {
        _services = services;
        _initialReadModel = initialReadModel;
        InitializeComponent();
    }

    private void GlobalReadyHuBindingWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialReadModel != null)
        {
            LoadSession(_initialReadModel);
            return;
        }

        RefreshReadModel(showError: true);
    }

    private void LoadSession(WpfReadyHuBindingReadModel readModel)
    {
        _session = new GlobalReadyHuBindingSession(readModel);
        DataContext = _session;
        StatusText.Text = _session.Summary;
    }

    private void CandidatesTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selectedHu = e.NewValue as GlobalReadyHuCandidateItem;
        _selectedLine = null;
        _session?.SelectHu(_selectedHu);
        StatusText.Text = _session?.Summary ?? string.Empty;
    }

    private void CompatibleTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selectedLine = e.NewValue as GlobalReadyHuCompatibleLineItem;
    }

    private void Bind_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null)
        {
            return;
        }

        if (!_session.StageBind(_selectedHu, _selectedLine, out var message))
        {
            MessageBox.Show(message, "Глобальная привязка готовых HU", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _selectedHu = null;
        _selectedLine = null;
        StatusText.Text = "Изменения подготовлены. База будет изменена после сохранения.";
    }

    private void Detach_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null)
        {
            return;
        }

        var staged = StagedBindingsGrid.SelectedItem as GlobalReadyHuStagedBinding;
        if (!_session.StageDetach(staged, out var message))
        {
            MessageBox.Show(message, "Глобальная привязка готовых HU", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StatusText.Text = "Подготовленная привязка отменена.";
    }

    private void Auto_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null)
        {
            return;
        }

        _session.StageAuto();
        StatusText.Text = "Авто-подбор подготовлен локально. База будет изменена после сохранения.";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_session?.HasStagedChanges == true
            && MessageBox.Show(
                "Есть подготовленные привязки. Обновление сбросит их. Продолжить?",
                "Глобальная привязка готовых HU",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        RefreshReadModel(showError: true);
    }

    private void RefreshReadModel(bool showError)
    {
        if (!_services.WpfReadApi.TryGetReadyHuBindingReadModel(out var model))
        {
            if (showError)
            {
                MessageBox.Show(
                    "Не удалось обновить список готовых HU.",
                    "Глобальная привязка готовых HU",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return;
        }

        if (_session == null)
        {
            LoadSession(model);
        }
        else
        {
            _session.RefreshFrom(model);
        }

        _selectedHu = null;
        _selectedLine = null;
        StatusText.Text = _session?.Summary ?? string.Empty;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null)
        {
            return;
        }

        var batches = _session.BuildApplyFinalByOrder();
        if (batches.Count == 0)
        {
            _allowClose = true;
            DialogResult = true;
            Close();
            return;
        }

        SaveButton.IsEnabled = false;
        var successful = new List<GlobalReadyHuBindingApplyOrderBatch>();
        try
        {
            foreach (var batch in batches)
            {
                if (!_services.WpfReadApi.TryApplyFinalHuBindings(batch.OrderId, batch.Lines, out _, out var error))
                {
                    ShowApplyFailure(successful, batch, error, batches.Count - successful.Count - 1);
                    return;
                }

                successful.Add(batch);
                _session.MarkOrderApplySuccess(batch.OrderId);
            }

            _allowClose = true;
            DialogResult = true;
            Close();
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private static void ShowApplyFailure(
        IReadOnlyList<GlobalReadyHuBindingApplyOrderBatch> successful,
        GlobalReadyHuBindingApplyOrderBatch failed,
        WpfHuBindingApplyFinalError? error,
        int notRunCount)
    {
        var title = "Глобальная привязка готовых HU";
        var message = BuildApplyFinalErrorMessage(error);
        if (successful.Count == 0)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var savedOrders = string.Join(", ", successful.Select(batch => batch.OrderRef));
        MessageBox.Show(
            $"Часть привязок сохранена: {successful.Count} заказ(ов): {savedOrders}.{Environment.NewLine}" +
            $"Не сохранен заказ {failed.OrderRef}. Не запущено заказов: {notRunCount}.{Environment.NewLine + Environment.NewLine}" +
            $"{message}{Environment.NewLine + Environment.NewLine}" +
            "Успешные привязки уже сохранены. Обновите список перед продолжением.",
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static string BuildApplyFinalErrorMessage(WpfHuBindingApplyFinalError? error)
    {
        if (error == null)
        {
            return "Сервер отклонил привязку HU. Проверьте выбор HU и повторите действие.";
        }

        if (string.Equals(error.ErrorCode, "HU_BINDING_STALE", StringComparison.OrdinalIgnoreCase))
        {
            return "Список HU изменился. Подготовленные изменения оставлены на экране; обновите список перед продолжением.";
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(error.Message))
        {
            parts.Add(error.Message);
        }
        else if (!string.IsNullOrWhiteSpace(error.ErrorCode))
        {
            parts.Add(error.ErrorCode);
        }

        if (error.Problems.Count > 0)
        {
            parts.Add(string.Join(Environment.NewLine, error.Problems));
        }

        return parts.Count == 0
            ? "Сервер отклонил привязку HU."
            : string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!TryConfirmClose())
        {
            return;
        }

        _allowClose = true;
        DialogResult = false;
        Close();
    }

    private void GlobalReadyHuBindingWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _session?.HasStagedChanges != true)
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
        if (_session?.HasStagedChanges != true)
        {
            return true;
        }

        return MessageBox.Show(
            "Есть подготовленные привязки. Закрыть окно без сохранения?",
            "Глобальная привязка готовых HU",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}
