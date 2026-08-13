using System.ComponentModel;
using System.IO;
using System.Windows;
using FlowStock.DesktopUpdate;

namespace FlowStock.Updater;

public partial class UpdaterWindow : Window
{
    private readonly bool _recovery;
    private readonly string _sessionId;
    private readonly UpdaterProcessOutcome _processOutcome;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _terminal;
    private bool _switching;
    private string _currentStage = "подготовка";

    public UpdaterWindow(bool recovery, string sessionId, UpdaterProcessOutcome? processOutcome = null)
    {
        _recovery = recovery;
        _sessionId = sessionId;
        _processOutcome = processOutcome ?? new UpdaterProcessOutcome(recovery);
        InitializeComponent();
        Loaded += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        var paths = new DesktopUpdatePaths();
        paths.EnsureBaseDirectories();
        var logPath = paths.LogFile(_sessionId);
        void Log(string message)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }

        try
        {
            var runner = new ProcessRunner();
            var git = new GitRepositoryClient(runner);
            var engine = new UpdaterEngine(
                paths,
                new RuntimeStateManager(paths),
                runner,
                git,
                DesktopUpdateHttpClientFactory.Create(TimeSpan.FromSeconds(30)),
                Log);
            var progress = new Progress<UpdateProgress>(value =>
            {
                StageText.Text = value.Stage;
                _currentStage = value.Stage;
                MessageText.Text = value.Message;
                Progress.IsIndeterminate = value.IsIndeterminate;
                _switching = value.Stage is "переключение" or "запуск" or "проверка старта";
                CloseButton.IsEnabled = !_switching;
            });

            if (_recovery)
            {
                await engine.RecoverAsync(_sessionId, progress);
            }
            else
            {
                await engine.RunUpdateAsync(_sessionId, progress, _cancellation.Token);
            }

            StageText.Text = "завершено";
            MessageText.Text = _recovery ? "Восстановление завершено." : "Обновление установлено.";
            Progress.IsIndeterminate = false;
            Progress.Value = 100;
            _terminal = true;
            _processOutcome.MarkSuccess();
            CloseButton.IsEnabled = true;
            CloseButton.Content = "Закрыть";
            if (ShouldCloseAutomaticallyAfterSuccess(_recovery))
            {
                Close();
            }
        }
        catch (OperationCanceledException)
        {
            _processOutcome.MarkFailure();
            StageText.Text = "отменено";
            MessageText.Text = $"Обновление отменено. Технический лог: {logPath}";
            _terminal = true;
            CloseButton.Content = "Закрыть";
        }
        catch (Exception exception)
        {
            _processOutcome.MarkFailure();
            Log(exception.ToString());
            StageText.Text = $"ошибка: {_currentStage}";
            MessageText.Text = $"{exception.Message}{Environment.NewLine}Технический лог: {logPath}";
            Progress.IsIndeterminate = false;
            _terminal = true;
            CloseButton.IsEnabled = true;
            CloseButton.Content = "Закрыть";
        }
    }

    public static bool ShouldCloseAutomaticallyAfterSuccess(bool recovery) => recovery;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_terminal)
        {
            Close();
            return;
        }

        if (_switching)
        {
            return;
        }

        if (MessageBox.Show(
                "Отменить обновление и оставить текущую версию FlowStock?",
                "FlowStock Updater",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _cancellation.Cancel();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_terminal)
        {
            e.Cancel = true;
            CloseButton_Click(this, new RoutedEventArgs());
        }
    }
}
