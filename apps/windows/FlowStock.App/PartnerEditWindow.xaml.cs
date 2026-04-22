using System.Windows;
using System.Windows.Input;
using FlowStock.Core.Models;
using Npgsql;

namespace FlowStock.App;

public partial class PartnerEditWindow : Window
{
    private readonly AppServices _services;
    private readonly Partner? _partner;
    private readonly List<PartnerStatusOption> _statusOptions = new()
    {
        new PartnerStatusOption(PartnerStatus.Supplier, "Поставщик"),
        new PartnerStatusOption(PartnerStatus.Client, "Клиент"),
        new PartnerStatusOption(PartnerStatus.Both, "Клиент и поставщик")
    };

    public long? SavedPartnerId { get; private set; }

    public PartnerEditWindow(AppServices services, Partner? partner = null)
    {
        _services = services;
        _partner = partner;

        InitializeComponent();
        StatusCombo.ItemsSource = _statusOptions;

        if (_partner == null)
        {
            Title = "Добавление контрагента";
            IdBox.Text = "(будет присвоен)";
            StatusCombo.SelectedItem = _statusOptions.FirstOrDefault(option => option.Status == PartnerStatus.Both)
                                       ?? _statusOptions.LastOrDefault();
            return;
        }

        Title = $"Редактирование контрагента #{_partner.Id}";
        IdBox.Text = _partner.Id.ToString();
        NameBox.Text = _partner.Name;
        InnBox.Text = _partner.Code ?? string.Empty;
        var currentStatus = _services.WpfPartnerApi.TryGetPartners(out var apiPartners)
            ? apiPartners.FirstOrDefault(entry => entry.Partner.Id == _partner.Id)?.Status ?? PartnerStatus.Both
            : PartnerStatus.Both;
        StatusCombo.SelectedItem = _statusOptions.FirstOrDefault(option => option.Status == currentStatus)
                                   ?? _statusOptions.LastOrDefault();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;
        var inn = NormalizeInn(InnBox.Text);
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Введите наименование контрагента.", "Контрагенты", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!IsValidInn(inn))
        {
            MessageBox.Show("ИНН должен содержать только цифры.", "Контрагенты", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryValidateInnUnique(inn, _partner?.Id))
        {
            return;
        }

        var status = (StatusCombo.SelectedItem as PartnerStatusOption)?.Status ?? PartnerStatus.Both;

        try
        {
            if (_partner == null)
            {
                var result = await _services.WpfPartnerApi.TryCreatePartnerAsync(name, inn, status).ConfigureAwait(true);
                if (!result.IsSuccess && !string.IsNullOrWhiteSpace(result.Error))
                {
                    throw new InvalidOperationException(result.Error);
                }

                var partnerId = result.IsSuccess
                    ? (result.PartnerId ?? 0)
                    : 0;
                if (partnerId <= 0)
                {
                    throw new InvalidOperationException("Сервер не вернул идентификатор нового контрагента.");
                }

                SavedPartnerId = partnerId;
            }
            else
            {
                var result = await _services.WpfPartnerApi.TryUpdatePartnerAsync(_partner.Id, name, inn, status).ConfigureAwait(true);
                if (!result.IsSuccess)
                {
                    throw new InvalidOperationException(result.Error ?? "Не удалось обновить контрагента через сервер.");
                }

                SavedPartnerId = _partner.Id;
            }

            DialogResult = true;
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Контрагенты", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (PostgresException ex) when (IsPostgresConstraint(ex))
        {
            if (TryValidateInnUnique(inn, _partner?.Id))
            {
                MessageBox.Show("Не удалось сохранить контрагента. Нарушено ограничение базы данных.", "Контрагенты", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Контрагенты", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool TryValidateInnUnique(string? inn, long? currentPartnerId)
    {
        if (string.IsNullOrWhiteSpace(inn))
        {
            return true;
        }

        var duplicate = _services.WpfPartnerApi.TryGetPartners(out var apiPartners)
            ? apiPartners.Select(entry => entry.Partner).FirstOrDefault(partner => string.Equals(partner.Code, inn, StringComparison.OrdinalIgnoreCase))
            : null;
        if (duplicate == null)
        {
            return true;
        }

        if (currentPartnerId.HasValue && duplicate.Id == currentPartnerId.Value)
        {
            return true;
        }

        MessageBox.Show(
            $"Контрагент с таким ИНН уже существует: {duplicate.Name}. Продолжить нельзя.",
            "Контрагенты",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    private static bool IsValidInn(string? inn)
    {
        if (string.IsNullOrWhiteSpace(inn))
        {
            return true;
        }

        return inn.All(char.IsDigit);
    }

    private static string? NormalizeInn(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool IsPostgresConstraint(PostgresException ex)
    {
        return string.Equals(ex.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal);
    }

    private void InnBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private void InnBox_OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(System.Windows.DataFormats.Text))
        {
            e.CancelCommand();
            return;
        }

        var text = e.DataObject.GetData(System.Windows.DataFormats.Text) as string;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (!text.All(char.IsDigit))
        {
            e.CancelCommand();
        }
    }

    private sealed record PartnerStatusOption(PartnerStatus Status, string Name);
}
