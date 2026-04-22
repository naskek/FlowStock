using System.Windows;
using FlowStock.Core.Models;

namespace FlowStock.App;

public partial class LocationEditWindow : Window
{
    private readonly AppServices _services;
    private readonly Location? _location;

    public long? SavedLocationId { get; private set; }

    public LocationEditWindow(AppServices services, Location? location = null)
    {
        _services = services;
        _location = location;

        InitializeComponent();

        if (_location == null)
        {
            Title = "Добавление места хранения";
            IdBox.Text = "(будет присвоен)";
            return;
        }

        Title = $"Редактирование места хранения #{_location.Id}";
        IdBox.Text = _location.Id.ToString();
        CodeBox.Text = _location.Code;
        NameBox.Text = _location.Name;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text?.Trim() ?? string.Empty;
        var name = NameBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Введите код и наименование места хранения.", "Места хранения", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            if (_location == null)
            {
                var result = await _services.WpfCatalogApi.TryCreateLocationAsync(code, name).ConfigureAwait(true);
                if (!result.IsSuccess && !string.IsNullOrWhiteSpace(result.Error))
                {
                    throw new InvalidOperationException(result.Error);
                }

                var locationId = result.IsSuccess
                    ? (result.CreatedId ?? 0)
                    : 0;
                if (locationId <= 0)
                {
                    throw new InvalidOperationException("Сервер не вернул идентификатор нового места хранения.");
                }
                SavedLocationId = locationId;
            }
            else
            {
                var result = await _services.WpfCatalogApi.TryUpdateLocationAsync(_location.Id, code, name).ConfigureAwait(true);
                if (!result.IsSuccess)
                {
                    throw new InvalidOperationException(result.Error ?? "Не удалось обновить место хранения через сервер.");
                }

                SavedLocationId = _location.Id;
            }

            DialogResult = true;
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Места хранения", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Места хранения", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
