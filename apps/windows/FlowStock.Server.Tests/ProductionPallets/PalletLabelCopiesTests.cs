using FlowStock.App;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class PalletLabelCopiesTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(100, 100)]
    [InlineData(150, 100)]
    public void PalletLabelSettings_Normalize_ClampsCopiesToRange(int input, int expected)
    {
        var settings = new PalletLabelSettings { Copies = input };

        settings.Normalize();

        Assert.Equal(expected, settings.Copies);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData(" 2 ", 2)]
    [InlineData("100", 100)]
    public void TryParseCopies_AcceptsValidValues(string text, int expected)
    {
        var parsed = PalletLabelPrintSelectionWindow.TryParseCopies(text, out var copies);

        Assert.True(parsed);
        Assert.Equal(expected, copies);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("2.5")]
    public void TryParseCopies_RejectsInvalidValues(string? text)
    {
        var parsed = PalletLabelPrintSelectionWindow.TryParseCopies(text, out _);

        Assert.False(parsed);
    }
}
