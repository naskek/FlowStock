using System.Net;
using System.Text;
using FlowStock.App;

namespace FlowStock.Server.Tests.Marking;

[Collection("Marking environment variables")]
public sealed class WpfMarkingApiServiceTests
{
    [Fact]
    public void ServerSettings_MarkingTimeout_HasIndependentDefaultAndClampsToSupportedRange()
    {
        var defaults = new ServerSettings();

        Assert.Equal(15, defaults.CloseTimeoutSeconds);
        Assert.Equal(120, defaults.MarkingTimeoutSeconds);

        var low = new ServerSettings { CloseTimeoutSeconds = 15, MarkingTimeoutSeconds = 0 }.Normalize();
        var high = new ServerSettings { CloseTimeoutSeconds = 15, MarkingTimeoutSeconds = 601 }.Normalize();

        Assert.Equal(1, low.MarkingTimeoutSeconds);
        Assert.Equal(600, high.MarkingTimeoutSeconds);
        Assert.Equal(15, low.CloseTimeoutSeconds);
        Assert.Equal(15, high.CloseTimeoutSeconds);
    }

    [Fact]
    public void SettingsService_RoundTripsMarkingTimeoutAndUsesJsonDefault()
    {
        using var fixture = new SettingsFixture();
        fixture.Settings.Save(new BackupSettings
        {
            Server = new ServerSettings
            {
                CloseTimeoutSeconds = 15,
                MarkingTimeoutSeconds = 237
            }
        });

        var loaded = fixture.Settings.Load();
        Assert.Equal(237, loaded.Server.MarkingTimeoutSeconds);
        Assert.Contains("\"marking_timeout_seconds\": 237", File.ReadAllText(fixture.SettingsPath));

        File.WriteAllText(fixture.SettingsPath, """{"server":{"close_timeout_seconds":15}}""");
        Assert.Equal(120, fixture.Settings.Load().Server.MarkingTimeoutSeconds);
    }

    [Fact]
    public void EffectiveConfiguration_UsesValidEnvOverrideAndFallsBackForInvalidValue()
    {
        using var fixture = new SettingsFixture(markingTimeoutSeconds: 211);
        var original = Environment.GetEnvironmentVariable("FLOWSTOCK_SERVER_MARKING_TIMEOUT_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("FLOWSTOCK_SERVER_MARKING_TIMEOUT_SECONDS", "777");
            Assert.Equal(600, fixture.CreateService(new StubHandler()).GetEffectiveConfiguration().TimeoutSeconds);

            Environment.SetEnvironmentVariable("FLOWSTOCK_SERVER_MARKING_TIMEOUT_SECONDS", "not-a-number");
            Assert.Equal(211, fixture.CreateService(new StubHandler()).GetEffectiveConfiguration().TimeoutSeconds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FLOWSTOCK_SERVER_MARKING_TIMEOUT_SECONDS", original);
        }
    }

    [Fact]
    public async Task PreviewTimeout_IsReadOnlyRussianFailureAndLogsFullException()
    {
        using var fixture = new SettingsFixture();
        var exception = new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 15 seconds elapsing.");
        var service = fixture.CreateService(new StubHandler(exception));

        var result = await service.TryPreviewOrderAsync(10);

        Assert.False(result.IsSuccess);
        Assert.Contains("Предпросмотр Excel ЧЗ не выполнен", result.Message);
        Assert.Contains("можно безопасно повторить", result.Message);
        Assert.DoesNotContain("HttpClient.Timeout", result.Message);
        Assert.Contains("HttpClient.Timeout", File.ReadAllText(fixture.LogPath));
    }

    [Fact]
    public async Task PreviewExternalCancellation_IsNotReportedAsTimeout()
    {
        using var fixture = new SettingsFixture();
        using var cancellation = new CancellationTokenSource();
        var handler = new StubHandler((_, token) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException("caller cancelled", token);
        });

        var result = await fixture.CreateService(handler).TryPreviewOrderAsync(10, cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Contains("отменён", result.Message);
        Assert.DoesNotContain("времени ожидания", result.Message);
        Assert.DoesNotContain("caller cancelled", result.Message);
    }

    [Fact]
    public async Task ExportTimeout_IsOutcomeUnknownAndPostIsNotRetried()
    {
        using var fixture = new SettingsFixture();
        var handler = new StubHandler(new TaskCanceledException("raw timeout in English"));
        var service = fixture.CreateService(handler);

        var result = await service.TryExportOrderAsync(10);

        Assert.False(result.IsSuccess);
        Assert.Equal(OrderMarkingExportOutcome.OutcomeUnknown, result.Outcome);
        Assert.Contains("Сервер мог уже завершить или всё ещё выполнять", result.Message);
        Assert.Contains("Автоматический повтор не выполнен", result.Message);
        Assert.DoesNotContain("raw timeout in English", result.Message);
        Assert.Equal(1, handler.CallCount);
        Assert.Contains("raw timeout in English", File.ReadAllText(fixture.LogPath));
    }

    [Fact]
    public async Task ExportNetworkFailure_IsOutcomeUnknown()
    {
        using var fixture = new SettingsFixture();
        var handler = new StubHandler(new HttpRequestException("connection reset by peer"));

        var result = await fixture.CreateService(handler).TryExportOrderAsync(10);

        Assert.Equal(OrderMarkingExportOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal(1, handler.CallCount);
        Assert.DoesNotContain("connection reset by peer", result.Message);
    }

    [Fact]
    public async Task ExportTimeoutException_IsOutcomeUnknown()
    {
        using var fixture = new SettingsFixture();
        var handler = new StubHandler(new TimeoutException("transport timeout"));

        var result = await fixture.CreateService(handler).TryExportOrderAsync(10);

        Assert.Equal(OrderMarkingExportOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal(1, handler.CallCount);
        Assert.DoesNotContain("transport timeout", result.Message);
    }

    [Fact]
    public async Task ExportPreCancelledToken_IsCancelledWithoutCallingHandler()
    {
        using var fixture = new SettingsFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var handler = new StubHandler();

        var result = await fixture.CreateService(handler).TryExportOrderAsync(10, cancellation.Token);

        Assert.Equal(OrderMarkingExportOutcome.Cancelled, result.Outcome);
        Assert.Equal(0, handler.CallCount);
        Assert.DoesNotContain("результат неизвестен", result.Message);
    }

    [Fact]
    public async Task ExportExternalCancellationAfterPostStart_IsCancelledOutcomeUnknown()
    {
        using var fixture = new SettingsFixture();
        using var cancellation = new CancellationTokenSource();
        var handler = new StubHandler((_, token) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException("caller cancelled", token);
        });

        var result = await fixture.CreateService(handler).TryExportOrderAsync(10, cancellation.Token);

        Assert.Equal(OrderMarkingExportOutcome.CancelledOutcomeUnknown, result.Outcome);
        Assert.Contains("результат неизвестен", result.Message);
        Assert.DoesNotContain("caller cancelled", result.Message);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ExportHttpError_IsDefiniteFailure()
    {
        using var fixture = new SettingsFixture();
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"Заказ уже выполнен."}""", Encoding.UTF8, "application/json")
        });

        var result = await fixture.CreateService(handler).TryExportOrderAsync(10);

        Assert.Equal(OrderMarkingExportOutcome.Failure, result.Outcome);
        Assert.Equal("Заказ уже выполнен.", result.Message);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public void DbConnectionWindow_ServerSettingsCopiesIncludeMarkingTimeout()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "DbConnectionWindow.xaml.cs");

        Assert.Contains("\"FLOWSTOCK_SERVER_MARKING_TIMEOUT_SECONDS\"", source);
        Assert.True(
            source.Split("MarkingTimeoutSeconds = ", StringSplitOptions.None).Length >= 4,
            "Все реальные копирования ServerSettings должны сохранять marking timeout.");
        Assert.Contains("sameMarkingTimeout", source);
    }

    [Fact]
    public void OrderDetails_SaveDialogCancellationExplainsServerSuccessWithoutSecondPost()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");

        Assert.Contains("Формирование Excel ЧЗ на сервере завершено, но локальный файл не сохранён.", source);
        Assert.Contains("Повторное формирование безопасно и не создаст новые коды.", source);
        Assert.Equal(1, CountOccurrences(source, "TryExportOrderAsync(_orderId.Value)"));
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }

        return count;
    }

    private sealed class SettingsFixture : IDisposable
    {
        private readonly string _directory;

        public SettingsFixture(int markingTimeoutSeconds = 120)
        {
            _directory = Path.Combine(Path.GetTempPath(), $"flowstock-marking-{Guid.NewGuid():N}");
            SettingsPath = Path.Combine(_directory, "settings.json");
            LogPath = Path.Combine(_directory, "flowstock.log");
            Settings = new SettingsService(SettingsPath);
            Settings.Save(new BackupSettings
            {
                Server = new ServerSettings
                {
                    ServerBaseUrl = "http://127.0.0.1:7154",
                    CloseTimeoutSeconds = 15,
                    MarkingTimeoutSeconds = markingTimeoutSeconds
                }
            });
        }

        public string SettingsPath { get; }
        public string LogPath { get; }
        public SettingsService Settings { get; }

        public WpfMarkingApiService CreateService(HttpMessageHandler handler)
        {
            return new WpfMarkingApiService(Settings, new FileLogger(LogPath), _ => handler);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _response;

        public StubHandler()
            : this(new HttpResponseMessage(HttpStatusCode.OK))
        {
        }

        public StubHandler(Exception exception)
            : this((_, _) => throw exception)
        {
        }

        public StubHandler(HttpResponseMessage response)
            : this((_, _) => response)
        {
        }

        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response)
        {
            _response = response;
        }

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_response(request, cancellationToken));
        }
    }
}

[CollectionDefinition("Marking environment variables", DisableParallelization = true)]
public sealed class MarkingEnvironmentVariableCollection;
