using System.Windows;
using System.Windows.Controls;
using FlowStock.App.Services;

namespace FlowStock.App;

public partial class OrderControlWindow : Window
{
    private readonly AppServices _services;

    public OrderControlWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
        Loaded += (_, _) => LoadTasks();
    }

    private async void LoadTasks()
    {
        var result = await _services.WpfOrderControl.ListAsync(ActiveOnlyCheckBox.IsChecked == true).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            MessageBox.Show(result.ErrorMessage ?? "Не удалось загрузить задания.", "Контроль заказов", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        TasksGrid.ItemsSource = result.Tasks;
        CancelButton.IsEnabled = TasksGrid.SelectedItem is WpfOrderControlTaskRow;
    }

    private async void LoadDetails(long taskId)
    {
        var result = await _services.WpfOrderControl.GetAsync(taskId).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            MessageBox.Show(result.ErrorMessage ?? "Не удалось загрузить детали.", "Контроль заказов", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        HusGrid.ItemsSource = result.Hus;
        EventsGrid.ItemsSource = result.Events;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => LoadTasks();

    private void ActiveOnlyCheckBox_Changed(object sender, RoutedEventArgs e) => LoadTasks();

    private void TasksGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CancelButton.IsEnabled = TasksGrid.SelectedItem is WpfOrderControlTaskRow;
        if (TasksGrid.SelectedItem is WpfOrderControlTaskRow task)
        {
            LoadDetails(task.Id);
        }
        else
        {
            HusGrid.ItemsSource = null;
            EventsGrid.ItemsSource = null;
        }
    }

    private async void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (TasksGrid.SelectedItem is not WpfOrderControlTaskRow task)
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"Отменить контроль {task.TaskRef}?",
            "Контроль заказов",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var result = await _services.WpfOrderControl.CancelAsync(task.Id).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            MessageBox.Show(result.ErrorMessage ?? "Не удалось отменить задание.", "Контроль заказов", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LoadTasks();
    }
}
