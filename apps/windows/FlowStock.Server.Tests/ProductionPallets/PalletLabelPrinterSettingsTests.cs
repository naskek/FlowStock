using FlowStock.App;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class PalletLabelPrinterSettingsTests
{
    [Fact]
    public void NormalizePrinterNames_TrimsSortsAndRemovesDuplicatesIgnoringCase()
    {
        string?[] names =
        [
            "  Zebra ZT411  ",
            "Microsoft Print to PDF",
            "zebra zt411",
            null,
            " ",
            @"\\print-server\Pallets"
        ];

        var normalized = WindowsPrinterCatalog.NormalizePrinterNames(names);

        Assert.Equal(
            ["Microsoft Print to PDF", "Zebra ZT411", @"\\print-server\Pallets"],
            normalized);
    }

    [Fact]
    public void SettingsService_SavesWhitespacePrinterNameAsNull()
    {
        using var temp = new TempSettings();
        var service = new SettingsService(temp.SettingsPath);
        var settings = BackupSettings.Default();
        settings.PalletLabels.PrinterName = "   ";

        service.Save(settings);

        Assert.Null(service.Load().PalletLabels.PrinterName);
    }

    [Fact]
    public void SettingsService_SavesAndLoadsSelectedPrinterName()
    {
        using var temp = new TempSettings();
        var service = new SettingsService(temp.SettingsPath);
        var settings = service.Load();
        settings.PalletLabels.PrinterName = @"\\print-server\Pallets";

        service.Save(settings);

        Assert.Equal(@"\\print-server\Pallets", service.Load().PalletLabels.PrinterName);
    }

    [Fact]
    public void ChangingPrinterName_PreservesAllOtherSettingsSections()
    {
        using var temp = new TempSettings();
        var service = new SettingsService(temp.SettingsPath);
        service.Save(new BackupSettings
        {
            BackupsEnabled = false,
            BackupMode = BackupMode.OnEveryStart,
            BackupIfOlderThanHours = 48,
            KeepLastNBackups = 17,
            HuNextSequence = 42,
            DocumentNumbering = new DocumentNumberingSettings
            {
                Template = "{PREFIX}/{SEQ}",
                Year = "2026",
                SequenceStyle = "D4"
            },
            Postgres = new PostgresSettings
            {
                Host = "db.example",
                Port = "5433",
                Database = "flowstock",
                Username = "operator",
                Password = "secret"
            },
            Server = new ServerSettings
            {
                ServerBaseUrl = "https://server.example",
                PcClientUrl = "https://pc.example",
                TsdClientUrl = "https://tsd.example/tsd",
                DeviceId = "WPF-01",
                CloseTimeoutSeconds = 27,
                AllowInvalidTls = true
            },
            PalletLabels = new PalletLabelSettings
            {
                TemplatePath = @"templates\custom.btw",
                PrinterName = "Old printer",
                Copies = 3
            },
            RecentPostgres =
            [
                new PostgresConnectionProfile
                {
                    Host = "recent.example",
                    Port = "5432",
                    Database = "recent",
                    Username = "recent-user",
                    Password = "recent-password"
                }
            ]
        });

        var current = service.Load();
        current.PalletLabels.PrinterName = "New printer";
        service.Save(current);

        var saved = service.Load();
        Assert.False(saved.BackupsEnabled);
        Assert.Equal(BackupMode.OnEveryStart, saved.BackupMode);
        Assert.Equal(48, saved.BackupIfOlderThanHours);
        Assert.Equal(17, saved.KeepLastNBackups);
        Assert.Equal(42, saved.HuNextSequence);
        Assert.Equal("{PREFIX}/{SEQ}", saved.DocumentNumbering.Template);
        Assert.Equal("2026", saved.DocumentNumbering.Year);
        Assert.Equal("D4", saved.DocumentNumbering.SequenceStyle);
        Assert.Equal("db.example", saved.Postgres.Host);
        Assert.Equal("5433", saved.Postgres.Port);
        Assert.Equal("flowstock", saved.Postgres.Database);
        Assert.Equal("operator", saved.Postgres.Username);
        Assert.Equal("secret", saved.Postgres.Password);
        Assert.Equal("https://server.example", saved.Server.ServerBaseUrl);
        Assert.Equal("https://pc.example", saved.Server.PcClientUrl);
        Assert.Equal("https://tsd.example/tsd", saved.Server.TsdClientUrl);
        Assert.Equal("WPF-01", saved.Server.DeviceId);
        Assert.Equal(27, saved.Server.CloseTimeoutSeconds);
        Assert.True(saved.Server.AllowInvalidTls);
        Assert.Equal(@"templates\custom.btw", saved.PalletLabels.TemplatePath);
        Assert.Equal("New printer", saved.PalletLabels.PrinterName);
        Assert.Equal(3, saved.PalletLabels.Copies);
        var recent = Assert.Single(saved.RecentPostgres);
        Assert.Equal("recent.example", recent.Host);
        Assert.Equal("5432", recent.Port);
        Assert.Equal("recent", recent.Database);
        Assert.Equal("recent-user", recent.Username);
        Assert.Equal("recent-password", recent.Password);
    }

    [Fact]
    public void BuildSelectionState_KeepsSavedPrinterWhenItIsNotInstalled()
    {
        var state = PalletLabelPrinterSelectionState.Build(
            ["Installed printer"],
            "Missing printer");

        Assert.Equal("Missing printer", state.PrinterName);
        Assert.True(state.IsPrinterMissing);
        Assert.DoesNotContain("Missing printer", state.InstalledPrinterNames);
    }

    [Theory]
    [InlineData("  Environment printer  ", "Settings printer", "Environment printer")]
    [InlineData("", "Settings printer", "Settings printer")]
    [InlineData("   ", "Settings printer", "Settings printer")]
    [InlineData(null, "  Settings printer  ", "Settings printer")]
    [InlineData(null, "   ", null)]
    public void ResolvePrinterName_EnvironmentOverrideHasPriority(
        string? environmentValue,
        string? settingsValue,
        string? expected)
    {
        Assert.Equal(
            expected,
            PalletLabelPrinterNameResolver.ResolvePrinterName(environmentValue, settingsValue));
    }

    private sealed class TempSettings : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            $"flowstock-printer-settings-tests-{Guid.NewGuid():N}");

        public string SettingsPath => Path.Combine(_directory, "settings.json");

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
