namespace FlowStock.Core.Models;

public sealed class ProductionPalletLabelContractException : InvalidOperationException
{
    public ProductionPalletLabelContractException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
