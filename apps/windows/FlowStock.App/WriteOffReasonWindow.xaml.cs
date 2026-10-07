using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using FlowStock.Core.Models;

namespace FlowStock.App;

public partial class WriteOffReasonWindow : Window
{
    private readonly AppServices? _productionServices;
    public bool IsUiPreview => _productionServices is null;
    private AppServices _services => _productionServices
        ?? throw new InvalidOperationException(UiPreviewContext.OperationUnavailable);
    private readonly SettingsPageLoading _loading = null!;
    private readonly Action? _onChanged;
    private readonly ObservableCollection<WriteOffReason> _reasons = new();
    private WriteOffReason? _selectedReason;

    public WriteOffReasonWindow(AppServices services, Action? onChanged)
    {
        ArgumentNullException.ThrowIfNull(services);
        _productionServices = services;
        _onChanged = onChanged;
        InitializeComponent();
        _loading = new SettingsPageLoading((FrameworkElement)Content, _services.AppLogger);

        ReasonsGrid.ItemsSource = _reasons;
        _loading.InitializeOnLoaded(LoadReasonsAsync);
        UpdateDeleteButton();
    }

    private Task LoadReasonsAsync() => _loading.RunAsync(async () =>
    {
        var result = await _services.WpfCatalogApi.TryGetWriteOffReasonsAsync();
        var reasons = result.Value;
        _reasons.Clear();
        foreach (var reason in reasons.OrderBy(reason => reason.Name, StringComparer.OrdinalIgnoreCase))
        {
            _reasons.Add(reason);
        }

        UpdateDeleteButton();
    });

    private async void AddReason_Click(object sender, RoutedEventArgs e)
    {
        if (IsUiPreview) return;

        var code = ReasonCodeBox.Text?.Trim() ?? string.Empty;
        var name = ReasonNameBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(code))
        {
            MessageBox.Show("Введите код причины списания.", "Причины списания", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Введите наименование причины списания.", "Причины списания", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var result = await _services.WpfCatalogApi.TryCreateWriteOffReasonAsync(code.ToUpperInvariant(), name).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                if (string.Equals(result.Error, "WRITE_OFF_REASON_ALREADY_EXISTS", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("Такая причина списания уже существует.", "Причины списания", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                throw new InvalidOperationException(result.Error ?? "Не удалось создать причину списания через сервер.");
            }

            ReasonCodeBox.Text = string.Empty;
            ReasonNameBox.Text = string.Empty;
            await LoadReasonsAsync();
            _onChanged?.Invoke();
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Причины списания", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Причины списания", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DeleteReason_Click(object sender, RoutedEventArgs e)
    {
        if (IsUiPreview) return;

        if (_selectedReason == null)
        {
            MessageBox.Show("Выберите причину списания.", "Причины списания", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Удалить причину списания \"{_selectedReason.Name}\"?",
            "Причины списания",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var result = await _services.WpfCatalogApi.TryDeleteWriteOffReasonAsync(_selectedReason.Id).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(result.Error ?? "Не удалось удалить причину списания через сервер.");
            }

            _selectedReason = null;
            ReasonsGrid.SelectedItem = null;
            await LoadReasonsAsync();
            UpdateDeleteButton();
            _onChanged?.Invoke();
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Причины списания", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Причины списания", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Причины списания", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ReasonsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsUiPreview) return;

        _selectedReason = ReasonsGrid.SelectedItem as WriteOffReason;
        UpdateDeleteButton();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsUiPreview) return;

        if (!DeleteKeyGesture.IsDeleteGesture(e)
            || !ReasonsGrid.IsKeyboardFocusWithin
            || _selectedReason == null)
        {
            return;
        }

        e.Handled = true;
        DeleteReason_Click(ReasonsGrid, new RoutedEventArgs());
    }

    private void UpdateDeleteButton()
    {
        if (DeleteReasonButton != null)
        {
            DeleteReasonButton.IsEnabled = _selectedReason != null;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
